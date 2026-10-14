using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DownloadManager.Tests;

/// <summary>
/// Base for the test proxies: names ending in ".test" are made-up hosts that only the proxy can reach (it sends
/// them to 127.0.0.1), so a download that succeeds proves it went through the proxy.
/// </summary>
public abstract class TestProxyBase : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    protected readonly CancellationTokenSource Cts = new();

    protected TestProxyBase()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Required login; null = none.</summary>
    public (string User, string Password)? Login { get; set; }

    /// <summary>Every destination the proxy connected to, as "host:port".</summary>
    public ConcurrentQueue<string> Destinations { get; } = new();

    public int RefusedLogins;

    protected static string MapHost(string host) => host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : host;

    private async Task AcceptLoopAsync()
    {
        while (!Cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(Cts.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await HandleAsync(client.GetStream());
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                    {
                    }
                }
            });
        }
    }

    protected abstract Task HandleAsync(NetworkStream client);

    protected async Task<TcpClient> ConnectAsync(string host, int port)
    {
        Destinations.Enqueue($"{host}:{port}");
        var upstream = new TcpClient();
        await upstream.ConnectAsync(MapHost(host), port, Cts.Token);
        return upstream;
    }

    protected async Task RelayAsync(Stream a, Stream b)
    {
        var one = a.CopyToAsync(b, Cts.Token);
        var two = b.CopyToAsync(a, Cts.Token);
        await Task.WhenAny(one, two);
    }

    public async ValueTask DisposeAsync()
    {
        await Cts.CancelAsync();
        _listener.Stop();
    }
}

/// <summary>HTTP proxy: CONNECT tunnels and absolute-URI requests, with optional Basic login (407).</summary>
public sealed class HttpTestProxy : TestProxyBase
{
    protected override async Task HandleAsync(NetworkStream client)
    {
        var head = await ReadHeadAsync(client);
        if (head is null)
            return;
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        var headers = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')].Trim(), l => l[(l.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);

        if (Login is { } login)
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login.User}:{login.Password}"));
            if (!headers.TryGetValue("Proxy-Authorization", out var given) || given != expected)
            {
                Interlocked.Increment(ref RefusedLogins);
                await WriteAsync(client, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test proxy\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
        }

        if (parts[0] == "CONNECT")
        {
            var target = parts[1].Split(':');
            using var upstream = await ConnectAsync(target[0], int.Parse(target[1]));
            await WriteAsync(client, "HTTP/1.1 200 Connection established\r\n\r\n");
            await RelayAsync(client, upstream.GetStream());
            return;
        }

        // GET http://host:port/path HTTP/1.1 → origin form, one request per connection.
        var uri = new Uri(parts[1]);
        using var origin = await ConnectAsync(uri.Host, uri.Port);
        var forwarded = new StringBuilder($"{parts[0]} {uri.PathAndQuery} {parts[2]}\r\n");
        foreach (var (name, value) in headers)
        {
            if (!name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) && !name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                forwarded.Append($"{name}: {value}\r\n");
        }
        forwarded.Append("Connection: close\r\n\r\n");
        var upstreamStream = origin.GetStream();
        await WriteAsync(upstreamStream, forwarded.ToString());
        await upstreamStream.CopyToAsync(client, Cts.Token);
    }

    private static async Task<string?> ReadHeadAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 4);
        }
        return null;
    }

    private static async Task WriteAsync(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
        await stream.FlushAsync();
    }
}

/// <summary>SOCKS5 proxy (RFC 1928) with optional username/password login (RFC 1929); CONNECT only.</summary>
public sealed class Socks5TestProxy : TestProxyBase
{
    protected override async Task HandleAsync(NetworkStream client)
    {
        var greeting = await ReadAsync(client, 2);
        var methods = await ReadAsync(client, greeting[1]);
        var wanted = Login is null ? (byte)0x00 : (byte)0x02;
        if (!methods.Contains(wanted))
        {
            await client.WriteAsync(new byte[] { 5, 0xFF });
            return;
        }
        await client.WriteAsync(new byte[] { 5, wanted });

        if (Login is { } login)
        {
            var version = await ReadAsync(client, 2);
            var user = Encoding.UTF8.GetString(await ReadAsync(client, version[1]));
            var passwordLength = await ReadAsync(client, 1);
            var password = Encoding.UTF8.GetString(await ReadAsync(client, passwordLength[0]));
            var ok = user == login.User && password == login.Password;
            await client.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) });
            if (!ok)
            {
                Interlocked.Increment(ref RefusedLogins);
                return;
            }
        }

        var request = await ReadAsync(client, 4);
        string host;
        switch (request[3])
        {
            case 1:
                host = new IPAddress(await ReadAsync(client, 4)).ToString();
                break;
            case 3:
                var length = await ReadAsync(client, 1);
                host = Encoding.ASCII.GetString(await ReadAsync(client, length[0]));
                break;
            default:
                host = new IPAddress(await ReadAsync(client, 16)).ToString();
                break;
        }
        var portBytes = await ReadAsync(client, 2);
        var port = portBytes[0] << 8 | portBytes[1];
        using var upstream = await ConnectAsync(host, port);
        await client.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
        await RelayAsync(client, upstream.GetStream());
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }
}
