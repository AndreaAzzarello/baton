using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Core.Automation;

namespace Baton.Core.Integration;

/// <summary>The body of "POST /downloads". Only these fields: anything else (cookies, a form body, a method) is refused.</summary>
public sealed record ApiAddRequest(string? Url, string? FileName = null, string? Referrer = null, string? UserAgent = null, string? Queue = null,
    string? Category = null, bool? Start = null, bool? AllowDuplicate = null, string? Folder = null);

/// <summary>
/// The local REST API (<c>/api/v1/...</c>), served by <see cref="HttpListener"/> on 127.0.0.1 with no administrator rights. Every request
/// is checked in this order: it comes from this computer, its Host is exactly 127.0.0.1:port or localhost:port (http.sys does not check
/// it, and a web page reaching the port by DNS rebinding would carry another one), it has no Origin header (a web page's request does,
/// and there is no CORS: no web page may use the API), the rate limit, the Bearer token (constant time) and finally what the chosen
/// level allows. The routes become the messages of protocol 4 with origin "api" and go through <see cref="MessageValidator"/>,
/// <see cref="OriginPolicy"/> and <see cref="ControlHandler"/> like everything else.
/// </summary>
public sealed class LocalApiServer : IDisposable
{
    public const int MaxBodyBytes = 1024 * 1024;
    public const int RequestsPerMinute = 120;
    private const string Prefix = "/api/v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly DownloadService _service;
    private readonly ControlHandler _control;
    private readonly Func<DateTime> _clock;
    private readonly Queue<DateTime> _recent = new();
    private readonly object _rateLock = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public LocalApiServer(DownloadService service, ControlHandler control, Func<DateTime>? clock = null)
    {
        _service = service;
        _control = control;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>The port being served, or null when stopped.</summary>
    public int? Port { get; private set; }

    /// <summary>Why the last <see cref="Start"/> failed (port in use), shown in the settings; null when it works or was never tried.</summary>
    public string? StartError { get; private set; }

    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>Starts serving on <paramref name="port"/>. False when it cannot (the app goes on without the API).</summary>
    public bool Start(int port)
    {
        Stop();
        var listener = new HttpListener();
        // Without administrator rights only these exact prefixes are accepted ("+" and "*" are not).
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            listener.Close();
            StartError = ex.ErrorCode == 183 || ex.ErrorCode == 32 ? "port-in-use" : "failed";
            Log.Warning("api", $"could not listen on port {port}: error {ex.ErrorCode}");
            return false;
        }
        StartError = null;
        Port = port;
        _listener = listener;
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(listener, port, _stop.Token));
        Log.Info("api", $"listening on 127.0.0.1:{port}");
        return true;
    }

    public void Stop()
    {
        _stop?.Cancel();
        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        _listener = null;
        Port = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(HttpListener listener, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Stopped (or being stopped): end. Anything else (a client that reset its connection) must not end the API.
                if (ct.IsCancellationRequested || !listener.IsListening || ex is ObjectDisposedException)
                    return;
                continue;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(context, port);
                }
                catch (Exception ex)
                {
                    Log.Warning("api", "request failed: " + ex.GetType().Name);
                    TryRespond(context, 500, new { error = "Internal error." });
                }
            }, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext http, int port)
    {
        var request = http.Request;
        var path = request.Url?.AbsolutePath ?? "";
        var (status, body, detail) = await HandleCoreAsync(http, port, path);
        var routeName = ActivityText.ApiAction(request.HttpMethod, path);
        // Refusals and everything that changes something are logged; reading is not (it would drown the rest).
        if (status is 401 or 403 or 413 or 429 || (request.HttpMethod is "POST" or "DELETE" && status != 404))
            _service.AutomationLog.Record(AutomationLog.Api, routeName, status.ToString(System.Globalization.CultureInfo.InvariantCulture), detail ?? "");
        Respond(http, status, body);
    }

    private async Task<(int Status, object Body, string? Detail)> HandleCoreAsync(HttpListenerContext http, int port, string path)
    {
        var request = http.Request;
        if (request.RemoteEndPoint is not { } remote || !IPAddress.IsLoopback(remote.Address))
            return (403, new { error = "Only this computer may use the API." }, ActivityText.Reason("notLoopback"));
        var host = request.Headers["Host"];
        if (host is null || !(host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) || host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)))
            return (403, new { error = "Unexpected Host header." }, ActivityText.Reason("badHost"));
        // A browser tells a cross-site request by Origin (CORS requests) or by Sec-Fetch-Site (also "no-cors" GETs, which carry no Origin);
        // a script or program sends neither.
        if (request.Headers["Origin"] is not null || request.Headers["Sec-Fetch-Site"] is not null)
            return (403, new { error = "Requests from web pages are not accepted." }, ActivityText.Reason("webPage"));
        var authorization = request.Headers["Authorization"];
        var presented = authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
        if (!AutomationToken.Matches(_service.Settings.ApiToken, presented))
        {
            http.Response.AddHeader("WWW-Authenticate", "Bearer");
            return (401, new { error = "Missing or wrong token." }, ActivityText.Reason(presented is null ? "noToken" : "wrongToken"));
        }
        // Only calls with the right token use the budget: nothing else can lock the real client out.
        if (!AllowedByRate())
            return (429, new { error = "Too many requests; at most 120 per minute." }, ActivityText.Reason("rateLimit"));
        if (!path.StartsWith(Prefix + "/", StringComparison.Ordinal))
            return (404, new { error = "Not found." }, null);
        var route = path[Prefix.Length..].Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        var method = request.HttpMethod.ToUpperInvariant();

        byte[] body = [];
        if (method is "POST" or "PUT" or "PATCH")
        {
            if (request.ContentLength64 > MaxBodyBytes)
                return (413, new { error = "The request body is larger than 1 MB." }, ActivityText.Reason("bodyTooLarge"));
            var read = await ReadBodyAsync(request.InputStream);
            if (read is null)
                return (413, new { error = "The request body is larger than 1 MB." }, ActivityText.Reason("bodyTooLarge"));
            body = read;
        }

        var (message, error) = Translate(method, route, body, http.Request.QueryString);
        if (error is not null)
            return error.Value;
        if (message is null)
            return await Answer(method, route);
        return await Run(message, route);
    }

    /// <summary>GET /openapi.json and GET /queues, which need no message of their own beyond the level check.</summary>
    private async Task<(int Status, object Body, string? Detail)> Answer(string method, string[] route)
    {
        // Both need at least read access.
        var check = new ChannelMessage(route[0] == "queues" ? Messages.Queues : Messages.Version, Origin: Origins.Api);
        if (_control.Authorize(check) is { } refused)
            return (403, new { error = refused.Error }, ActivityText.Reason("level"));
        await Task.CompletedTask;
        if (route[0] == "openapi.json")
            return (200, new RawJson(OpenApiJson()), null);
        var queues = _service.Queues.OrderByDescending(q => q.IsMain).Select(q => new { name = q.DisplayName, main = q.IsMain }).ToList();
        return (200, new { queues }, null);
    }

    /// <summary>The HTTP status of a failure, chosen from its code (published in 1.4.1: 404, 403 or 400).</summary>
    public static int StatusFor(ReplyCode? code) => code switch
    {
        ReplyCode.NoDownload or ReplyCode.NoQueue => 404,
        ReplyCode.PrivateNetwork or ReplyCode.AutomationOff or ReplyCode.LevelTooLow => 403,
        ReplyCode.TooManyPending => 400,
        _ => 400,
    };

    private async Task<(int Status, object Body, string? Detail)> Run(ChannelMessage message, string[] route)
    {
        if (MessageValidator.Validate(message) is { } invalid)
            return (400, new { error = invalid }, ActivityText.Reason("invalid"));
        if (_control.Authorize(message) is { } refused)
            return (403, new { error = refused.Error }, ActivityText.Reason("level"));
        var reply = await _control.HandleAsync(message);
        var detail = message.Download is { } d ? Automation.AutomationLog.Clean(d.Url) : message.Id is { } id ? "#" + id : message.Queue;
        if (!reply.Ok)
        {
            var status = StatusFor(reply.Code);
            return (status, new { error = reply.Error }, ActivityText.Reason(ActivityText.ReasonOf(reply.Code)));
        }
        if (message.Type == Messages.Add)
        {
            if (reply.Result == "duplicate")
                return (409, new { result = "duplicate", duplicate = reply.Duplicate }, ActivityText.Reason("duplicate", Automation.AutomationLog.Clean(message.Download!.Url)));
            return (201, new { id = reply.Id, result = "added" }, detail);
        }
        return message.Type switch
        {
            Messages.Version => (200, new { version = reply.AppVersion, protocol = Messages.ProtocolVersion }, null),
            Messages.List => (200, message.Limit is null && message.After is null ? new { downloads = reply.Downloads } : new { downloads = reply.Downloads, nextCursor = reply.NextCursor }, null),
            Messages.Status or Messages.Pause or Messages.Resume or Messages.Retry => (200, (object?)reply.Download ?? new { }, detail),
            Messages.Remove => (200, new { removed = reply.Id }, detail),
            _ => (200, new { ok = true }, detail),
        };
    }

    /// <summary>The route as a message (null message and no error: answered here), or the error response.</summary>
    private static (ChannelMessage? Message, (int, object, string?)? Error) Translate(string method, string[] route, byte[] body, System.Collections.Specialized.NameValueCollection query)
    {
        (ChannelMessage?, (int, object, string?)?) NotFound() => (null, (404, new { error = "Not found." }, null));
        (ChannelMessage?, (int, object, string?)?) Wrong() => (null, (405, new { error = "Method not allowed." }, null));
        (ChannelMessage?, (int, object, string?)?) Bad(string why, string code) => (null, (400, new { error = why }, ActivityText.Reason(code)));
        ChannelMessage Msg(string type, long? id = null, string? queue = null, ChannelMessage? basis = null) => new(type, Version: Messages.ProtocolVersion, Origin: Origins.Api, Id: id, Queue: queue);

        bool Id(string text, out long id) => long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0;

        switch (route)
        {
            case ["version"]:
                return method == "GET" ? (Msg(Messages.Version), null) : Wrong();
            case ["openapi.json"]:
                return method == "GET" ? (null, null) : Wrong();
            case ["queues"]:
                return method == "GET" ? (null, null) : Wrong();
            case ["queues", var name, var action] when action is "start" or "stop":
                return method == "POST" ? (Msg(action == "start" ? Messages.QueueStart : Messages.QueueStop, queue: name), null) : Wrong();
            case ["downloads"]:
                if (method == "GET")
                {
                    // Without limit and after: the whole list, as since 1.4.1. With them: a page, and nextCursor when there is more.
                    int? limit = null;
                    long? after = null;
                    if (query["limit"] is { } limitText)
                    {
                        if (!int.TryParse(limitText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var l) || l is < 1 or > MessageValidator.MaxPageSize)
                            return Bad($"limit must be a number from 1 to {MessageValidator.MaxPageSize}.", "invalid");
                        limit = l;
                    }
                    if (query["after"] is { } afterText)
                    {
                        if (!long.TryParse(afterText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var a) || a < 1)
                            return Bad("after must be the nextCursor of a previous page.", "invalid");
                        after = a;
                    }
                    return (new ChannelMessage(Messages.List, Version: Messages.ProtocolVersion, Origin: Origins.Api, Limit: limit, After: after), null);
                }
                if (method != "POST")
                    return Wrong();
                ApiAddRequest? add;
                try
                {
                    add = JsonSerializer.Deserialize<ApiAddRequest>(body, StrictJson);
                }
                catch (JsonException)
                {
                    return Bad("The body must be a JSON object with only: url, fileName, referrer, userAgent, queue, category, start, allowDuplicate.", "badBody");
                }
                if (add is null || string.IsNullOrWhiteSpace(add.Url))
                    return Bad("Missing url.", "missingUrl");
                if (add.Folder is not null)
                    return Bad("The API cannot choose a folder; use a category.", "folderRefused");
                var download = new BrowserDownload(add.Url.Trim(), add.FileName, add.Referrer, add.UserAgent, Queue: add.Queue, Category: add.Category,
                    Start: add.Start, AllowDuplicate: add.AllowDuplicate);
                return (new ChannelMessage(Messages.Add, download, Messages.ProtocolVersion, Origin: Origins.Api), null);
            case ["downloads", var idText] when Id(idText, out var id):
                return method switch
                {
                    "GET" => (Msg(Messages.Status, id), null),
                    "DELETE" => (Msg(Messages.Remove, id), null),
                    _ => Wrong(),
                };
            case ["downloads", var idText, var action] when Id(idText, out var id) && action is "pause" or "resume" or "retry":
                return method == "POST" ? (Msg(action, id), null) : Wrong();
            default:
                return NotFound();
        }
    }

    private bool AllowedByRate()
    {
        var now = _clock();
        lock (_rateLock)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(1))
                _recent.Dequeue();
            if (_recent.Count >= RequestsPerMinute)
                return false;
            _recent.Enqueue(now);
            return true;
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream input)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await input.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxBodyBytes)
                return null;
        }
        return buffer.ToArray();
    }

    private sealed record RawJson(string Text);

    private static string OpenApiJson()
    {
        using var stream = typeof(LocalApiServer).Assembly.GetManifestResourceStream("Baton.Core.Integration.openapi.json");
        if (stream is null)
            return "{}";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Respond(HttpListenerContext http, int status, object body)
    {
        var text = body is RawJson raw ? raw.Text : JsonSerializer.Serialize(body, Json);
        var bytes = Encoding.UTF8.GetBytes(text);
        var response = http.Response;
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.AddHeader("Cache-Control", "no-store");
        response.AddHeader("X-Content-Type-Options", "nosniff");
        response.ContentLength64 = bytes.Length;
        try
        {
            response.OutputStream.Write(bytes);
        }
        finally
        {
            response.Close();
        }
    }

    private static void TryRespond(HttpListenerContext http, int status, object body)
    {
        try
        {
            Respond(http, status, body);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
