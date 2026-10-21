using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Baton.Tests;

public enum FtpTestTls
{
    None,
    Explicit,
    Implicit,
}

/// <summary>A file served by <see cref="FtpTestServer"/>.</summary>
public sealed class FtpTestFile
{
    public required byte[] Content { get; set; }
    public DateTime Modified { get; set; } = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Bytes per second on each data connection; 0 = unthrottled.</summary>
    public int Throttle { get; set; }

    public string Sha256 => Convert.ToHexString(SHA256.HashData(Content));
}

public sealed record FtpRetr(string Path, long Offset, bool Active);

/// <summary>
/// A small FTP/FTPS server for tests: login, FEAT, SIZE, MDTM, REST, PASV/EPSV and PORT/EPRT, RETR, explicit
/// (AUTH TLS) and implicit TLS with a self-signed certificate, and fault injection (connection limits with 421
/// or 530, no REST support).
/// </summary>
public sealed class FtpTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private int _connected;

    public FtpTestServer(FtpTestTls tls = FtpTestTls.None)
    {
        Tls = tls;
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public FtpTestTls Tls { get; }
    public ConcurrentDictionary<string, FtpTestFile> Files { get; } = new();

    /// <summary>Null: anonymous logins are accepted.</summary>
    public (string User, string Password)? Login { get; set; }

    /// <summary>More simultaneous control connections than this are refused; 0 = no limit.</summary>
    public int MaxConnections { get; set; }

    /// <summary>How an extra connection is refused: "421" at the greeting, or "530" after the password.</summary>
    public string RefuseWith { get; set; } = "421";

    public bool RestSupported { get; set; } = true;

    public ConcurrentQueue<FtpRetr> Retrs { get; } = new();
    public int LoginCount;
    public int MaxSeenConnections;

    /// <summary>Self-signed certificate for "localhost": FTPS clients must not trust it unless told to.</summary>
    public static X509Certificate2 Certificate { get; } = CreateCertificate();

    public string Url(string path, string scheme = "ftp", string host = "127.0.0.1", string? userInfo = null) =>
        $"{scheme}://{(userInfo is null ? "" : userInfo + "@")}{host}:{Port}/{path.TrimStart('/')}";

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = HandleAsync(client);
        }
    }

    private sealed class Session
    {
        public required Stream Control { get; set; }
        public string? User;
        public bool LoggedIn;
        public long Rest;
        public bool ProtectData;
        public TcpListener? Passive;
        public IPEndPoint? Active;
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        var count = Interlocked.Increment(ref _connected);
        InterlockedMax(ref MaxSeenConnections, count);
        try
        {
            using (tcp)
            {
                Stream stream = tcp.GetStream();
                if (Tls == FtpTestTls.Implicit)
                    stream = await ServerTlsAsync(stream);
                var session = new Session { Control = stream };
                var overLimit = MaxConnections > 0 && count > MaxConnections;
                if (overLimit && RefuseWith == "421")
                {
                    await ReplyAsync(session, "421 Too many connections, try again later.");
                    return;
                }
                await ReplyAsync(session, "220 Test FTP server ready.");
                var reader = new LineReader();
                while (await reader.ReadLineAsync(session.Control, _cts.Token) is { } line)
                {
                    var space = line.IndexOf(' ');
                    var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
                    var arg = space < 0 ? "" : line[(space + 1)..];
                    if (!await CommandAsync(session, verb, arg, overLimit, reader))
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or AuthenticationException2)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _connected);
        }
    }

    // SslStream reports a failed handshake (client refused our certificate) as AuthenticationException.
    private sealed class AuthenticationException2 : Exception;

    private async Task<bool> CommandAsync(Session s, string verb, string arg, bool overLimit, LineReader reader)
    {
        switch (verb)
        {
            case "AUTH" when Tls == FtpTestTls.Explicit && arg.Equals("TLS", StringComparison.OrdinalIgnoreCase):
                await ReplyAsync(s, "234 Proceed with negotiation.");
                s.Control = await ServerTlsAsync(s.Control);
                reader.Reset();
                return true;
            case "USER":
                s.User = arg;
                await ReplyAsync(s, "331 Password required.");
                return true;
            case "PASS":
                if (overLimit && RefuseWith == "530")
                {
                    await ReplyAsync(s, "530 Too many users, try later.");
                    return true;
                }
                var ok = Login is not { } login
                    ? s.User is "anonymous" or "ftp"
                    : s.User == login.User && arg == login.Password;
                if (!ok)
                {
                    await ReplyAsync(s, "530 Login incorrect.");
                    return true;
                }
                s.LoggedIn = true;
                Interlocked.Increment(ref LoginCount);
                await ReplyAsync(s, "230 Logged in.");
                return true;
            case "QUIT":
                await ReplyAsync(s, "221 Bye.");
                return false;
            case "NOOP":
                await ReplyAsync(s, "200 OK.");
                return true;
            case "FEAT":
                var features = new List<string> { "SIZE", "MDTM", "EPSV", "PASV", "UTF8" };
                if (RestSupported)
                    features.Add("REST STREAM");
                if (Tls != FtpTestTls.None)
                    features.AddRange(["AUTH TLS", "PBSZ", "PROT"]);
                await ReplyAsync(s, "211-Features:\r\n" + string.Join("\r\n", features.Select(f => " " + f)) + "\r\n211 End");
                return true;
        }

        if (!s.LoggedIn)
        {
            await ReplyAsync(s, "530 Please login with USER and PASS.");
            return true;
        }

        switch (verb)
        {
            case "SYST":
                await ReplyAsync(s, "215 UNIX Type: L8");
                break;
            case "PBSZ":
                await ReplyAsync(s, "200 PBSZ=0");
                break;
            case "PROT":
                s.ProtectData = arg.Equals("P", StringComparison.OrdinalIgnoreCase);
                await ReplyAsync(s, "200 Protection set.");
                break;
            case "OPTS" or "TYPE" or "MODE" or "STRU" or "CWD":
                await ReplyAsync(s, "200 OK.");
                break;
            case "PWD":
                await ReplyAsync(s, "257 \"/\" is the current directory.");
                break;
            case "SIZE":
                if (Files.TryGetValue(arg.TrimStart('/'), out var sized))
                    await ReplyAsync(s, $"213 {sized.Content.Length}");
                else
                    await ReplyAsync(s, "550 File not found.");
                break;
            case "MDTM":
                if (Files.TryGetValue(arg.TrimStart('/'), out var dated))
                    await ReplyAsync(s, "213 " + dated.Modified.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                else
                    await ReplyAsync(s, "550 File not found.");
                break;
            case "REST":
                if (!RestSupported)
                {
                    await ReplyAsync(s, "502 REST not implemented.");
                    break;
                }
                s.Rest = long.Parse(arg, CultureInfo.InvariantCulture);
                await ReplyAsync(s, $"350 Restarting at {s.Rest}.");
                break;
            case "PASV":
            case "EPSV":
                s.Passive?.Stop();
                s.Passive = new TcpListener(IPAddress.Loopback, 0);
                s.Passive.Start();
                var port = ((IPEndPoint)s.Passive.LocalEndpoint).Port;
                s.Active = null;
                await ReplyAsync(s, verb == "EPSV"
                    ? $"229 Entering Extended Passive Mode (|||{port}|)"
                    : $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                break;
            case "PORT":
                var n = arg.Split(',').Select(int.Parse).ToArray();
                s.Active = new IPEndPoint(IPAddress.Parse($"{n[0]}.{n[1]}.{n[2]}.{n[3]}"), n[4] * 256 + n[5]);
                await ReplyAsync(s, "200 PORT command successful.");
                break;
            case "EPRT":
                var e = arg.Split('|');
                s.Active = new IPEndPoint(IPAddress.Parse(e[2]), int.Parse(e[3], CultureInfo.InvariantCulture));
                await ReplyAsync(s, "200 EPRT command successful.");
                break;
            case "RETR":
                await RetrAsync(s, arg.TrimStart('/'));
                break;
            case "ABOR":
                await ReplyAsync(s, "226 Abort successful.");
                break;
            default:
                await ReplyAsync(s, "502 Command not implemented.");
                break;
        }
        return true;
    }

    private async Task RetrAsync(Session s, string path)
    {
        if (!Files.TryGetValue(path, out var file))
        {
            await ReplyAsync(s, "550 File not found.");
            return;
        }
        var offset = s.Rest;
        s.Rest = 0;
        Retrs.Enqueue(new FtpRetr(path, offset, s.Active is not null));
        await ReplyAsync(s, "150 Opening BINARY mode data connection.");

        TcpClient data;
        if (s.Active is { } active)
        {
            data = new TcpClient();
            await data.ConnectAsync(active);
        }
        else
        {
            data = await s.Passive!.AcceptTcpClientAsync(_cts.Token);
            s.Passive.Stop();
            s.Passive = null;
        }

        var completed = false;
        using (data)
        {
            try
            {
                Stream stream = data.GetStream();
                if (s.ProtectData)
                    stream = await ServerTlsAsync(stream);
                const int chunk = 16 * 1024;
                for (var pos = offset; pos < file.Content.Length; pos += chunk)
                {
                    var count = (int)Math.Min(chunk, file.Content.Length - pos);
                    await stream.WriteAsync(file.Content.AsMemory((int)pos, count), _cts.Token);
                    if (file.Throttle > 0)
                        await Task.Delay(TimeSpan.FromSeconds((double)count / file.Throttle), _cts.Token);
                }
                await stream.FlushAsync(_cts.Token);
                if (stream is SslStream ssl)
                    await ssl.ShutdownAsync();
                completed = true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The client closed the data connection early: a segment was complete or the download paused.
            }
        }
        await ReplyAsync(s, completed ? "226 Transfer complete." : "426 Connection closed; transfer aborted.");
    }

    private static async Task<Stream> ServerTlsAsync(Stream stream)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(Certificate, clientCertificateRequired: false, checkCertificateRevocation: false);
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
            throw new AuthenticationException2();
        }
        return ssl;
    }

    private static async Task ReplyAsync(Session s, string text)
    {
        await s.Control.WriteAsync(Encoding.UTF8.GetBytes(text + "\r\n"));
        await s.Control.FlushAsync();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>Reads CRLF-terminated command lines without consuming bytes after them (AUTH TLS switches streams).</summary>
    private sealed class LineReader
    {
        public void Reset()
        {
        }

        public async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(one, ct);
                if (read == 0)
                    return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null;
                if (one[0] == (byte)'\n')
                    return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add(one[0]);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
    }
}
