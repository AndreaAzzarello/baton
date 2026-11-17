using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Baton.Core;
using Baton.Core.Automation;
using Baton.Core.Integration;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>The local REST API: who may call it, what each level allows, the private-network guard and the limits.</summary>
public sealed class LocalApiTests : IAsyncLifetime
{
    private const string Token = "test-token-0123456789abcdef0123456789abcdef";

    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private DownloadService _service = null!;
    private DownloadStore _store = null!;
    private LocalApiServer _api = null!;
    private HttpClient _http = null!;
    private int _port;
    private DateTime _now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static readonly EngineOptions Fast = new()
    {
        MinSegmentSize = 64 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        ReadTimeout = TimeSpan.FromSeconds(10),
        CheckpointInterval = TimeSpan.FromMilliseconds(100),
    };

    public async Task InitializeAsync()
    {
        _server = await TestFileServer.StartAsync();
        Directory.CreateDirectory(_dir);
        _store = new DownloadStore(Path.Combine(_dir, "state.db"));
        _service = new DownloadService(_store, engineTemplate: Fast, autoTick: false);
        Configure(AutomationLevel.Extended, allowLocal: true);
        var control = new ControlHandler(_service, () => AutomationLevel.Off, () => _service.Settings.ApiLevel, _ => { }, "1.4.1");
        _api = new LocalApiServer(_service, control, () => _now);
        // A port that was free a moment ago can be taken by another test class running in parallel: pick another one.
        for (var attempt = 1; ; attempt++)
        {
            _port = FreePort();
            if (_api.Start(_port))
                break;
            Assert.True(attempt < 20, $"no free port for the API after {attempt} tries ({_api.StartError})");
        }
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/api/v1/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        _api.Dispose();
        await _service.DisposeAsync();
        await _server.DisposeAsync();
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Configure(AutomationLevel level, bool allowLocal = false) =>
        _service.UpdateSettings(new AppSettings
        {
            DownloadFolder = _dir, MaxConnections = 4, ProxyMode = ProxyMode.None, ApiLevel = level, ApiToken = Token, AllowLocalNetworkForAgents = allowLocal,
        });

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private HttpRequestMessage Req(HttpMethod method, string path, object? body = null, string? token = Token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> Call(HttpMethod method, string path, object? body = null, string? token = Token)
    {
        using var response = await _http.SendAsync(Req(method, path, body, token));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(30);
        }
    }

    [Theory]
    [InlineData("downloads")]
    [InlineData("openapi.json")]
    [InlineData("version")]
    public async Task NoToken_Or_WrongToken_Is401_OnEveryEndpoint(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, path, token: null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, path, token: Token + "x")).Status);
    }

    [Fact]
    public async Task EmptyConfiguredToken_NeverMatches()
    {
        _service.UpdateSettings(new AppSettings { DownloadFolder = _dir, ProxyMode = ProxyMode.None, ApiLevel = AutomationLevel.Extended, ApiToken = "" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "version", token: "")).Status);
    }

    [Fact]
    public async Task WrongHost_Is403()
    {
        var request = Req(HttpMethod.Get, "version");
        request.Headers.Host = "evil.example";
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LocalhostHostName_IsAccepted()
    {
        var request = Req(HttpMethod.Get, "version");
        request.Headers.Host = $"localhost:{_port}";
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OriginHeader_Is403_EvenWithTheRightToken()
    {
        var request = Req(HttpMethod.Get, "version");
        request.Headers.Add("Origin", "https://evil.example");
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task LevelOff_Is403()
    {
        Configure(AutomationLevel.Off);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "downloads")).Status);
    }

    [Fact]
    public async Task ReadOnly_ReadsButCannotAddOrControl()
    {
        Configure(AutomationLevel.ReadOnly, allowLocal: true);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "downloads")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "queues")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "openapi.json")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "downloads", new { url = _server.Add("r.bin", new ServedFile { Content = new byte[10] }) })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "downloads/1/pause")).Status);
    }

    [Fact]
    public async Task ReadAdd_CannotPause()
    {
        Configure(AutomationLevel.ReadAdd, allowLocal: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "downloads/1/pause")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "queues/Main/start")).Status);
    }

    [Fact]
    public async Task OpenpenapiAndVersion_Work_WithTheToken()
    {
        var (status, json) = await Call(HttpMethod.Get, "openapi.json");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("3.0.3", json.GetProperty("openapi").GetString());
        var (_, version) = await Call(HttpMethod.Get, "version");
        Assert.Equal("1.4.1", version.GetProperty("version").GetString());
        Assert.Equal(Messages.ProtocolVersion, version.GetProperty("protocol").GetInt32());
    }

    [Fact]
    public async Task Add_Download_Then_List_Status_And_Duplicate()
    {
        var url = _server.Add("api.bin", new ServedFile { Content = new byte[200_000] });
        var (status, json) = await Call(HttpMethod.Post, "downloads", new { url });
        Assert.Equal(HttpStatusCode.Created, status);
        var id = json.GetProperty("id").GetInt64();
        await WaitFor(() => _service.GetSnapshot(id)!.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        var (_, one) = await Call(HttpMethod.Get, $"downloads/{id}");
        Assert.Equal("completed", one.GetProperty("status").GetString());
        Assert.False(one.TryGetProperty("cookies", out _));
        var (_, list) = await Call(HttpMethod.Get, "downloads");
        Assert.Single(list.GetProperty("downloads").EnumerateArray());

        var (dupStatus, dup) = await Call(HttpMethod.Post, "downloads", new { url });
        Assert.Equal(HttpStatusCode.Conflict, dupStatus);
        Assert.Equal("duplicate", dup.GetProperty("result").GetString());
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "downloads", new { url, allowDuplicate = true })).Status);
    }

    [Fact]
    public async Task Add_Refuses_Folder_UnknownFields_And_UnknownCategory()
    {
        var url = _server.Add("f.bin", new ServedFile { Content = new byte[10] });
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "downloads", new { url, folder = @"C:\Windows" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "downloads", "{\"url\":\"" + url + "\",\"cookies\":[]}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "downloads", "{\"url\":\"" + url + "\",\"method\":\"POST\"}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "downloads", new { url, category = "nope" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "downloads", "not json")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "downloads", new { })).Status);
        Assert.Empty(_service.GetSnapshots());
    }

    /// <summary>The API published in 1.4.1: same status per cause, same fields. A3 added a code to the pipe reply only; this must not change.</summary>
    [Fact]
    public async Task Compatibility_ErrorsAndFields_AreThoseOf141()
    {
        var url = _server.Add("c.bin", new ServedFile { Content = new byte[1000] });
        var (created, added) = await Call(HttpMethod.Post, "downloads", new { url });
        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal(["id", "result"], Names(added));
        var id = added.GetProperty("id").GetInt64();
        await WaitFor(() => _service.GetSnapshot(id)!.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        var (_, one) = await Call(HttpMethod.Get, $"downloads/{id}");
        // A null field may be left out; nothing outside this set may appear and the core ones must be there.
        string[] known = ["addedUtc", "category", "completedUtc", "downloaded", "error", "folder", "id", "name", "path", "queue", "status", "totalSize", "url"];
        Assert.Empty(Names(one).Except(known));
        Assert.Empty(new[] { "addedUtc", "downloaded", "folder", "id", "name", "status", "url" }.Except(Names(one)));

        var (dupStatus, dup) = await Call(HttpMethod.Post, "downloads", new { url });
        Assert.Equal(HttpStatusCode.Conflict, dupStatus);
        Assert.Equal(["duplicate", "result"], Names(dup));
        Assert.Equal(["existingId", "existingPath", "reason"], Names(dup.GetProperty("duplicate")));

        foreach (var (method, path, body, expected) in new (HttpMethod, string, object?, HttpStatusCode)[]
                 {
                     (HttpMethod.Get, "downloads/9999", null, HttpStatusCode.NotFound),
                     (HttpMethod.Post, "downloads/9999/pause", null, HttpStatusCode.NotFound),
                     (HttpMethod.Post, "queues/nope/start", null, HttpStatusCode.NotFound),
                     (HttpMethod.Post, "downloads", new { url = "http://127.0.0.1:1/x.bin", category = "nope" }, HttpStatusCode.BadRequest),
                     (HttpMethod.Post, "downloads", new { url = "gopher://example.com/x" }, HttpStatusCode.BadRequest),
                     (HttpMethod.Post, $"downloads/{id}/retry", null, HttpStatusCode.BadRequest),
                 })
        {
            var (status, json) = await Call(method, path, body);
            Assert.Equal(expected, status);
            Assert.Equal(["error"], Names(json));
        }

        Configure(AutomationLevel.Extended, allowLocal: false);
        var (privateStatus, privateJson) = await Call(HttpMethod.Post, "downloads", new { url = "http://192.168.1.1/x.bin" });
        Assert.Equal(HttpStatusCode.Forbidden, privateStatus);
        Assert.Equal(["error"], Names(privateJson));
        Configure(AutomationLevel.ReadOnly, allowLocal: true);
        var (levelStatus, levelJson) = await Call(HttpMethod.Post, "downloads", new { url });
        Assert.Equal(HttpStatusCode.Forbidden, levelStatus);
        Assert.Equal(["error"], Names(levelJson));
    }

    private static string[] Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("http://127.0.0.1:1/x.bin")]
    [InlineData("http://localhost/x.bin")]
    [InlineData("http://10.0.0.5/x.bin")]
    [InlineData("http://192.168.1.1/x.bin")]
    [InlineData("http://172.16.0.9/x.bin")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://100.64.0.1/x.bin")]
    [InlineData("http://[::1]/x.bin")]
    [InlineData("http://[fe80::1]/x.bin")]
    [InlineData("http://[::ffff:10.0.0.1]/x.bin")]
    [InlineData("ftp://192.168.1.1/x.bin")]
    public async Task PrivateNetworkAddresses_AreRefused_UnlessAllowed(string url)
    {
        Configure(AutomationLevel.Extended, allowLocal: false);
        var (status, _) = await Call(HttpMethod.Post, "downloads", new { url });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(_service.GetSnapshots());
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("gopher://example.com/x")]
    public async Task OtherSchemes_AreRefused(string url)
    {
        Configure(AutomationLevel.Extended, allowLocal: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "downloads", new { url })).Status);
    }

    [Fact]
    public async Task LocalNetworkSetting_Allows_LocalAddresses()
    {
        Configure(AutomationLevel.Extended, allowLocal: true);
        var url = _server.Add("ok.bin", new ServedFile { Content = new byte[1000] });
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "downloads", new { url })).Status);
    }

    [Fact]
    public async Task ADownloadAskedForByAnAgent_NeverConnectsToThisComputer_EvenAfterTheCheck()
    {
        // The connection itself is checked (a redirect or a changed DNS answer could lead to a private address): a request marked
        // PublicOnly fails against the local test server, which is what the API sets on every download it adds.
        var url = _server.Add("blocked.bin", new ServedFile { Content = new byte[1000] });
        var id = _service.Add(url, _dir, context: new RequestContext(null, null, []) { PublicOnly = true });
        await WaitFor(() => _service.GetSnapshot(id)!.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Failed, _service.GetSnapshot(id)!.Status);
    }

    [Fact]
    public async Task Control_Pause_Resume_Remove_And_Unknown_Id()
    {
        var url = _server.Add("slow.bin", new ServedFile { Content = new byte[300_000] });
        var (_, added) = await Call(HttpMethod.Post, "downloads", new { url, start = false });
        var id = added.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"downloads/{id}/pause")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Delete, $"downloads/{id}")).Status);
        Assert.Null(_service.GetSnapshot(id));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"downloads/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, "downloads/9999")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "queues/Nope/start")).Status);
    }

    [Fact]
    public async Task Open_IsNotARoute()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "downloads/1/open")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, "nothing")).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Call(HttpMethod.Put, "downloads")).Status);
    }

    [Fact]
    public async Task MoreThan120RequestsAMinute_Is429_ThenRecovers()
    {
        for (var i = 0; i < LocalApiServer.RequestsPerMinute; i++)
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "version")).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Call(HttpMethod.Get, "version")).Status);
        _now = _now.AddMinutes(2);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "version")).Status);
    }

    [Fact]
    public async Task ABrowsersNoCorsRequest_IsRefused_AndRequestsWithoutTheTokenDoNotUseTheBudget()
    {
        var request = Req(HttpMethod.Get, "version");
        request.Headers.Add("Sec-Fetch-Site", "cross-site"); // sent by browsers also for "no-cors" GETs, which carry no Origin
        using (var response = await _http.SendAsync(request))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        for (var i = 0; i < LocalApiServer.RequestsPerMinute + 20; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "version", token: "wrong")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "version")).Status); // the real client is not locked out
    }

    [Fact]
    public async Task BodyOverOneMegabyte_Is413()
    {
        var big = "{\"url\":\"http://example.com/" + new string('a', 1_100_000) + "\"}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Call(HttpMethod.Post, "downloads", big)).Status);
    }

    [Fact]
    public void PortInUse_FailsWithAClearError_WithoutThrowing()
    {
        var control = new ControlHandler(_service, () => AutomationLevel.Off, () => AutomationLevel.Off, _ => { }, "1");
        using var second = new LocalApiServer(_service, control);
        Assert.False(second.Start(_port));
        Assert.Equal("port-in-use", second.StartError);
        Assert.False(second.IsRunning);
    }

    [Fact]
    public async Task Log_HasRefusalsAndAdds_WithoutQueryStringOrToken()
    {
        await Call(HttpMethod.Get, "downloads", token: "wrong-secret-token");
        var url = _server.Add("log.bin", new ServedFile { Content = new byte[1000] }) + "?sig=SECRETVALUE";
        await Call(HttpMethod.Post, "downloads", new { url });
        var lines = _service.AutomationLog.Read(AutomationLog.Api);
        Assert.Contains(lines, l => l.Outcome == "401");
        Assert.Contains(lines, l => l.Outcome == "201");
        Assert.DoesNotContain(lines, l => (l.Detail + l.Action).Contains("SECRETVALUE") || (l.Detail + l.Action).Contains("wrong-secret-token"));
    }
}
