using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using FluentFTP;
using FluentFTP.Exceptions;
using FluentFTP.Proxy.AsyncProxy;
using Baton.Core.Security;

namespace Baton.Core.Net;

/// <summary>
/// How bytes of one file are fetched. The download engine only asks what the file is (<see cref="ProbeAsync"/>)
/// and for a stream from an offset (<see cref="OpenAsync"/>), so segmentation, resume and fallback work the same
/// for HTTP and FTP.
/// </summary>
public interface ITransport : IDisposable
{
    Task<ProbeResult> ProbeAsync(CancellationToken ct);

    /// <summary>A stream starting at <paramref name="offset"/>; it may run past <paramref name="end"/> (inclusive, -1 = open).</summary>
    Task<Stream> OpenAsync(DownloadRecord record, long offset, long end, CancellationToken ct);
}

/// <summary>
/// HTTP and HTTPS with byte ranges, guarded by If-Range. A download started by a form (POST) is sent once per
/// attempt: the probe's response is the download itself, so the form is not submitted twice, and it is fetched as
/// one stream because servers do not answer ranges of a POST.
/// </summary>
public sealed class HttpTransport(HttpClient http, bool ownsClient, string url, RequestContext? context, TimeSpan timeout) : ITransport
{
    private HttpResponseMessage? _pending; // a POST response kept from the probe for the first OpenAsync

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        if (context is not { IsPost: true })
            return await HttpProbe.ProbeAsync(http, url, ct, context, timeout);

        Interlocked.Exchange(ref _pending, null)?.Dispose();
        var response = await HttpErrors.SendAsync(http, CreatePost(), timeout, ct);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
                throw HttpErrors.FromResponse(response);
        }
        _pending = response;
        var finalUri = response.RequestMessage?.RequestUri ?? new Uri(url);
        return new ProbeResult(response.Content.Headers.ContentLength, false, null, response.Content.Headers.LastModified?.ToString("R"),
            FileNames.FromResponse(response.Content.Headers.ContentDisposition, finalUri), response.Content.Headers.ContentType?.MediaType);
    }

    public async Task<Stream> OpenAsync(DownloadRecord record, long offset, long end, CancellationToken ct)
    {
        if (context is { IsPost: true })
        {
            var response = Interlocked.Exchange(ref _pending, null) ?? await HttpErrors.SendAsync(http, CreatePost(), timeout, ct);
            if (!response.IsSuccessStatusCode)
            {
                using (response)
                    throw HttpErrors.FromResponse(response);
            }
            return new OwnedStream(await response.Content.ReadAsStreamAsync(ct), response);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        context.ApplyTo(request);
        if (record.SupportsRanges)
        {
            request.Headers.Range = new RangeHeaderValue(offset, end >= 0 ? end : null);
            if (record.ETag is not null)
                request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(record.ETag));
            else if (record.LastModified is not null && DateTimeOffset.TryParse(record.LastModified, out var date))
                request.Headers.IfRange = new RangeConditionHeaderValue(date);
        }

        var get = await HttpErrors.SendAsync(http, request, timeout, ct);
        try
        {
            Validate(get, record, offset);
            return new OwnedStream(await get.Content.ReadAsStreamAsync(ct), get);
        }
        catch
        {
            get.Dispose();
            throw;
        }
    }

    private HttpRequestMessage CreatePost()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = FormBody.Create(context!.Body, context.BodyType) };
        context.ApplyTo(request);
        return request;
    }

    private static void Validate(HttpResponseMessage response, DownloadRecord record, long offset)
    {
        if (record.SupportsRanges)
        {
            if (response.StatusCode == HttpStatusCode.OK)
                throw new ResourceChangedException("The server sent the whole file instead of the requested range.");
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                throw new ResourceChangedException("The requested range no longer exists on the server.");
            if (!response.IsSuccessStatusCode)
                throw HttpErrors.FromResponse(response);
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset || (range.Length is { } length && length != record.TotalSize))
                throw new ResourceChangedException("The server returned a different range than requested.");
        }
        else if (!response.IsSuccessStatusCode)
        {
            throw HttpErrors.FromResponse(response);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _pending, null)?.Dispose();
        if (ownsClient)
            http.Dispose();
    }
}

/// <summary>The fields of a submitted form, sent again the way the form encoded them.</summary>
public static class FormBody
{
    public const string UrlEncoded = "application/x-www-form-urlencoded";
    public const string Multipart = "multipart/form-data";

    /// <summary>
    /// <paramref name="fields"/> is always URL-encoded (that is how the extension passes them); a multipart form gets a
    /// multipart body with the same fields, as text parts (forms that upload files are left to the browser).
    /// </summary>
    public static HttpContent Create(string? fields, string? type)
    {
        fields ??= "";
        if (type != Multipart)
            return new StringContent(fields, null, UrlEncoded);
        var content = new MultipartFormDataContent();
        foreach (var (name, value) in Parse(fields))
            content.Add(new StringContent(value), name);
        return content;
    }

    public static List<(string Name, string Value)> Parse(string fields) =>
        fields.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var at = pair.IndexOf('=');
                var name = at < 0 ? pair : pair[..at];
                var value = at < 0 ? "" : pair[(at + 1)..];
                return (Decode(name), Decode(value));
            })
            .ToList();

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}

public enum FtpSecurity
{
    /// <summary>ftp:// — no encryption.</summary>
    None,

    /// <summary>ftpes:// — plain connection upgraded with AUTH TLS (explicit FTPS), port 21.</summary>
    Explicit,

    /// <summary>ftps:// — TLS from the first byte (implicit FTPS), port 990.</summary>
    Implicit,
}

/// <summary>FTP and FTPS: SIZE and MDTM to identify the file, REST to resume and to fetch segments in parallel.</summary>
public sealed class FtpTransport : ITransport
{
    private readonly Uri _url;
    private readonly string _path;
    private readonly NetworkCredential _login;
    private readonly AppSettings _settings;
    private readonly CertificateTrust _trust;
    private readonly ProxyResolver? _proxy;
    private bool _certificateRejected;

    public FtpTransport(Uri url, NetworkCredential login, AppSettings settings, CertificateTrust trust, ProxyResolver? proxy)
    {
        _url = url;
        _path = Uri.UnescapeDataString(url.AbsolutePath);
        _login = login;
        _settings = settings;
        _trust = trust;
        _proxy = proxy;
    }

    public static bool IsFtp(Uri url) => url.Scheme is "ftp" or "ftps" or "ftpes";

    public static FtpSecurity SecurityOf(Uri url) => url.Scheme switch
    {
        "ftps" => FtpSecurity.Implicit,
        "ftpes" => FtpSecurity.Explicit,
        _ => FtpSecurity.None,
    };

    public static int PortOf(Uri url) => url.IsDefaultPort || url.Port <= 0 ? (SecurityOf(url) == FtpSecurity.Implicit ? 990 : 21) : url.Port;

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        using var client = await ConnectAsync(ct);
        try
        {
            var size = await client.GetFileSize(_path, -1, ct);
            if (size < 0 && !await client.FileExists(_path, ct))
                throw new DownloadFailedException("The file does not exist on the FTP server.");
            DateTime? modified = null;
            if (client.HasFeature(FtpCapability.MDTM))
            {
                var time = await client.GetModifiedTime(_path, ct);
                if (time != DateTime.MinValue)
                    modified = DateTime.SpecifyKind(time, DateTimeKind.Utc);
            }
            var resumable = size >= 0 && client.HasFeature(FtpCapability.REST);
            return new ProbeResult(size >= 0 ? size : null, resumable, null, modified?.ToString("R"), FileNames.Sanitize(FileNames.FromUrl(_url)));
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
        finally
        {
            await QuietDisconnectAsync(client);
        }
    }

    public async Task<Stream> OpenAsync(DownloadRecord record, long offset, long end, CancellationToken ct)
    {
        var client = await ConnectAsync(ct);
        try
        {
            var stream = await client.OpenRead(_path, FtpDataType.Binary, offset, false, ct);
            return new OwnedStream(stream, client);
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (Translate(ex) is { } translated)
                throw translated;
            throw;
        }
    }

    private async Task<AsyncFtpClient> ConnectAsync(CancellationToken ct)
    {
        var port = PortOf(_url);
        var timeout = (int)TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 5, 600)).TotalMilliseconds;
        var config = new FtpConfig
        {
            EncryptionMode = SecurityOf(_url) switch
            {
                FtpSecurity.Implicit => FtpEncryptionMode.Implicit,
                FtpSecurity.Explicit => FtpEncryptionMode.Explicit,
                _ => FtpEncryptionMode.None,
            },
            DataConnectionType = _settings.FtpActiveMode ? FtpDataConnectionType.AutoActive : FtpDataConnectionType.AutoPassive,
            ConnectTimeout = timeout,
            ReadTimeout = timeout,
            DataConnectionConnectTimeout = timeout,
            DataConnectionReadTimeout = timeout,
            RetryAttempts = 1,
            ValidateAnyCertificate = false,
            LogPassword = false,
            LogUserName = false,
            LogToConsole = false,
            SocketKeepAlive = true,
        };

        var client = CreateClient(port, config);
        client.ValidateCertificate += (_, e) =>
        {
            var accepted = _trust.Validate(_url.Host, port, e.Certificate, e.PolicyErrors);
            _certificateRejected |= !accepted;
            e.Accept = accepted;
        };
        try
        {
            await client.Connect(ct);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (Translate(ex) is { } translated)
                throw translated;
            throw;
        }
    }

    /// <summary>A SOCKS5 or HTTP (CONNECT) proxy carries FTP; other proxy kinds cannot, and the connection is direct.</summary>
    private AsyncFtpClient CreateClient(int port, FtpConfig config)
    {
        if (_proxy?.Resolve(_url) is { } proxy && proxy.Scheme is "socks5" or "http")
        {
            var profile = new FtpProxyProfile
            {
                FtpHost = _url.Host,
                FtpPort = port,
                FtpCredentials = _login,
                ProxyHost = proxy.Host,
                ProxyPort = proxy.Port,
                ProxyCredentials = _proxy.Credentials?.GetCredential(proxy, "Basic"),
            };
            AsyncFtpClientProxy client = proxy.Scheme == "socks5" ? new AsyncFtpClientSocks5Proxy(profile) : new AsyncFtpClientHttp11Proxy(profile);
            client.Config = config;
            return client;
        }
        return new AsyncFtpClient(_url.Host, _login, port, config);
    }

    /// <summary>Turns FTP replies into what the engine understands: busy (like HTTP 429), login, missing file, certificate.</summary>
    private Exception? Translate(Exception ex)
    {
        if (ex is OperationCanceledException or DownloadFailedException or AuthenticationRequiredException or ServerBusyException)
            return null;
        if (_certificateRejected || ex is AuthenticationException && ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            return new UntrustedCertificateException(_url.Host, ex);
        var code = ex switch
        {
            FtpCommandException command => command.CompletionCode,
            _ when ex.Message.Length >= 3 && char.IsDigit(ex.Message[0]) => ex.Message[..3],
            _ => null,
        };
        return code switch
        {
            "421" => new ServerBusyException($"The FTP server refused the connection: {ex.Message}", ex),
            "530" => new AuthenticationRequiredException(_url, false, "FTP", _url.Host, "530"),
            "550" or "553" => new DownloadFailedException($"The FTP server refused the file: {ex.Message}", ex),
            ['4', _, _] => new IOException($"The FTP server reported a temporary error: {ex.Message}", ex),
            ['5', _, _] => new DownloadFailedException($"The FTP server reported an error: {ex.Message}", ex),
            _ when ex is FtpException => new IOException(ex.Message, ex),
            _ => null,
        };
    }

    private static async Task QuietDisconnectAsync(AsyncFtpClient client)
    {
        try
        {
            if (client.IsConnected)
                await client.Disconnect(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or FtpException or TimeoutException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>A stream that releases what produced it (the HTTP response, the FTP connection) when it is disposed.</summary>
internal sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                // Closing an FTP data stream before its end makes the server answer 426: that is expected here.
                inner.Dispose();
            }
            catch (Exception ex) when (ex is IOException or FtpException or TimeoutException or ObjectDisposedException)
            {
            }
            owner.Dispose();
        }
        base.Dispose(disposing);
    }
}
