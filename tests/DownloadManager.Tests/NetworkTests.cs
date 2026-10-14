using System.Security.Cryptography;
using System.Text.Json;
using DownloadManager.Core;
using DownloadManager.Core.Integration;
using DownloadManager.Core.Net;
using DownloadManager.Core.Security;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Tests;

/// <summary>HTTP authentication, proxies (HTTP, SOCKS5, PAC, exceptions), cookies and stored secrets.</summary>
public sealed class NetworkTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly List<IAsyncDisposable> _others = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "state.db");

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
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        foreach (var other in _others)
            await other.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private T Track<T>(T disposable) where T : IAsyncDisposable
    {
        _others.Add(disposable);
        return disposable;
    }

    private DownloadStore Store(Action<AppSettings>? configure = null)
    {
        var store = new DownloadStore(DbPath);
        var settings = new AppSettings { DownloadFolder = _dir, MaxConnections = 2, ProxyMode = ProxyMode.None };
        configure?.Invoke(settings);
        store.SaveSettings(settings);
        return store;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task WaitUntil(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached in time.");
            await Task.Delay(25);
        }
    }

    private static async Task<DownloadStatus> Finished(DownloadService service, long id)
    {
        await WaitUntil(() => service.GetSnapshot(id)!.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        return service.GetSnapshot(id)!.Status;
    }

    // Authentication

    [Fact]
    public async Task Basic401_AsksForALogin_ThenResumes_AndTheSavedLoginIsReused()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(300_000, 20), BasicLogin = ("lucia", "pw-basic-1") };
        var url = _server.Add("basic.bin", file);
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        LoginRequest? request = null;
        service.LoginRequired += r => request = r;

        var id = service.Add(url);
        await WaitUntil(() => request is not null);
        Assert.Equal(DownloadStatus.Paused, service.GetSnapshot(id)!.Status);
        Assert.Equal(("Basic", "Test files", false), (request!.Scheme, request.Realm, request.IsProxy));

        service.ProvideLogin(request, "lucia", "pw-basic-1", remember: true);
        Assert.Equal(DownloadStatus.Completed, await Finished(service, id));
        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "basic.bin")));

        request = null;
        var second = service.Add(url, fileName: "basic-2.bin");
        Assert.Equal(DownloadStatus.Completed, await Finished(service, second));
        Assert.Null(request);
        AssertNowhereInDatabase("pw-basic-1");
    }

    [Fact]
    public async Task Digest_WorksWithASavedLogin_AndATemporaryLoginIsNotSaved()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(200_000, 21), DigestLogin = ("dario", "pw-digest-2") };
        var url = _server.Add("digest.bin", file);
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        LoginRequest? request = null;
        service.LoginRequired += r => request = r;

        var id = service.Add(url);
        await WaitUntil(() => request is not null);
        Assert.Equal("Digest", request!.Scheme);
        service.ProvideLogin(request, "dario", "pw-digest-2", remember: false);
        Assert.Equal(DownloadStatus.Completed, await Finished(service, id));
        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "digest.bin")));
        Assert.Contains(_server.Requests, r => r.Authorization.StartsWith("Digest ", StringComparison.Ordinal));
        Assert.Empty(service.Credentials.Saved); // "Remember" unchecked: only for this session
        Assert.NotNull(service.Credentials.Find("127.0.0.1", new Uri(url).Port));
    }

    [Fact]
    public async Task SavedLogin_IsNeverSentToAnotherHost()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(1000, 22) };
        var url = _server.Add("open.bin", file);
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        service.Credentials.Save(new SiteCredential { Host = "other.example", Username = "u", Password = "never-sent" }, remember: true);

        var id = service.Add(url);
        Assert.Equal(DownloadStatus.Completed, await Finished(service, id));
        Assert.All(_server.Requests, r => Assert.Equal("", r.Authorization));
    }

    // Proxies

    [Fact]
    public async Task HttpProxy407_AsksForTheProxyLogin_ThenDownloadsThroughIt()
    {
        var proxy = Track(new HttpTestProxy { Login = ("proxy-user", "pw-proxy-3") });
        var file = new ServedFile { Content = TestFileServer.RandomBytes(400_000, 23) };
        _server.Add("proxied.bin", file);
        using var store = Store(s =>
        {
            s.ProxyMode = ProxyMode.Manual;
            s.ProxyType = ProxyType.Http;
            s.ProxyHost = "127.0.0.1";
            s.ProxyPort = proxy.Port;
        });
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        LoginRequest? request = null;
        service.LoginRequired += r => request = r;

        var id = service.Add(_server.UrlVia("proxied.bin", "files.test"));
        await WaitUntil(() => request is not null);
        Assert.True(request!.IsProxy);
        Assert.Equal(proxy.Port, request.Target.Port);

        service.ProvideLogin(request, "proxy-user", "pw-proxy-3", remember: true);
        Assert.Equal(DownloadStatus.Completed, await Finished(service, id));
        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "proxied.bin")));
        Assert.Contains(proxy.Destinations, d => d.StartsWith("files.test:", StringComparison.Ordinal));
        Assert.Equal("proxy-user", store.LoadSettings().ProxyUser);
        Assert.Equal("pw-proxy-3", store.LoadSettings().ProxyPassword); // decrypted when read back
        AssertNowhereInDatabase("pw-proxy-3"); // but encrypted at rest
    }

    [Fact]
    public async Task Socks5Proxy_WithLogin_CarriesHttpDownloads()
    {
        var proxy = Track(new Socks5TestProxy { Login = ("s5", "pw-socks-4") });
        var file = new ServedFile { Content = TestFileServer.RandomBytes(500_000, 24) };
        _server.Add("socks.bin", file);
        using var store = Store(s =>
        {
            s.ProxyMode = ProxyMode.Manual;
            s.ProxyType = ProxyType.Socks5;
            s.ProxyHost = "127.0.0.1";
            s.ProxyPort = proxy.Port;
            s.ProxyUser = "s5";
            s.ProxyPassword = "pw-socks-4";
        });
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var id = service.Add(_server.UrlVia("socks.bin", "files.test"));
        Assert.Equal(DownloadStatus.Completed, await Finished(service, id));
        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "socks.bin")));
        Assert.Contains(proxy.Destinations, d => d.StartsWith("files.test:", StringComparison.Ordinal));
        Assert.Equal(0, proxy.RefusedLogins);
    }

    [Fact]
    public async Task Localhost_AndExceptions_NeverUseTheProxy()
    {
        var proxy = Track(new HttpTestProxy());
        var file = new ServedFile { Content = TestFileServer.RandomBytes(100_000, 25) };
        var url = _server.Add("local.bin", file);
        using var store = Store(s =>
        {
            s.ProxyMode = ProxyMode.Manual;
            s.ProxyHost = "127.0.0.1";
            s.ProxyPort = proxy.Port;
            s.ProxyBypass = "*.intranet.example; 10.*; <local>";
        });
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var id = service.Add(url); // 127.0.0.1
        Assert.Equal(DownloadStatus.Completed, await Finished(service, id));
        Assert.Empty(proxy.Destinations);

        var resolver = service.Network.Proxy;
        Assert.Null(resolver.Resolve(new Uri("http://localhost:8080/x")));
        Assert.Null(resolver.Resolve(new Uri("http://[::1]/x")));
        Assert.Null(resolver.Resolve(new Uri("http://files.intranet.example/x")));
        Assert.Null(resolver.Resolve(new Uri("http://10.1.2.3/x")));
        Assert.Null(resolver.Resolve(new Uri("http://nas/x"))); // <local>: names without a dot
        Assert.Equal(new Uri($"http://127.0.0.1:{proxy.Port}"), resolver.Resolve(new Uri("https://example.com/x")));
    }

    [Fact]
    public void ProxyTypes_MapToTheirSchemes()
    {
        foreach (var (type, scheme) in new[] { (ProxyType.Http, "http"), (ProxyType.Https, "https"), (ProxyType.Socks5, "socks5") })
        {
            var resolver = new ProxyResolver(new AppSettings { ProxyMode = ProxyMode.Manual, ProxyType = type, ProxyHost = "proxy.lan", ProxyPort = 3128 });
            Assert.Equal(new Uri($"{scheme}://proxy.lan:3128"), resolver.Resolve(new Uri("https://example.com/")));
        }
        Assert.Null(new ProxyResolver(new AppSettings { ProxyMode = ProxyMode.None }).Resolve(new Uri("https://example.com/")));
    }

    private const string Pac = """
        function FindProxyForURL(url, host) {
            if (isPlainHostName(host) || shExpMatch(host, "*.direct.test")) return "DIRECT";
            if (dnsDomainIs(host, ".socks.test")) return "SOCKS5 127.0.0.1:1080";
            if (isInNet(host, "192.168.0.0", "255.255.0.0")) return "DIRECT";
            if (url.substring(0, 4) == "ftp:") return "PROXY 127.0.0.1:2121";
            return "PROXY 127.0.0.1:3128; DIRECT";
        }
        """;

    [Fact]
    public void Pac_ChoosesPerHost_WithTheStandardHelpers()
    {
        var resolver = new ProxyResolver(new AppSettings { ProxyMode = ProxyMode.Pac }, new PacScript(Pac));
        Assert.Null(resolver.Resolve(new Uri("http://intranet/x")));
        Assert.Null(resolver.Resolve(new Uri("http://files.direct.test/x")));
        Assert.Null(resolver.Resolve(new Uri("http://192.168.1.20/x")));
        Assert.Equal(new Uri("socks5://127.0.0.1:1080"), resolver.Resolve(new Uri("https://a.socks.test/x")));
        Assert.Equal(new Uri("http://127.0.0.1:2121"), resolver.Resolve(new Uri("ftp://ftp.example.org/f")));
        Assert.Equal(new Uri("http://127.0.0.1:3128"), resolver.Resolve(new Uri("https://example.org/")));
        Assert.Null(resolver.Resolve(new Uri("http://localhost/x"))); // never, whatever the PAC says
    }

    [Fact]
    public async Task Pac_IsLoadedFromHttpOrFile_AndABrokenOneMeansDirect()
    {
        _server.Add("proxy.pac", new ServedFile { Content = System.Text.Encoding.UTF8.GetBytes(Pac) });
        var fromHttp = new ProxyResolver(new AppSettings { ProxyMode = ProxyMode.Pac, PacUrl = $"{_server.BaseUrl}/files/proxy.pac" });
        await fromHttp.LoadPacAsync();
        Assert.Null(fromHttp.PacError);
        Assert.Equal(new Uri("http://127.0.0.1:3128"), fromHttp.Resolve(new Uri("https://example.org/")));

        var path = Path.Combine(_dir, "local.pac");
        await File.WriteAllTextAsync(path, Pac);
        var fromFile = new ProxyResolver(new AppSettings { ProxyMode = ProxyMode.Pac, PacUrl = new Uri(path).AbsoluteUri });
        await fromFile.LoadPacAsync();
        Assert.Equal(new Uri("socks5://127.0.0.1:1080"), fromFile.Resolve(new Uri("https://b.socks.test/")));

        await File.WriteAllTextAsync(path, "function nope( {");
        var broken = new ProxyResolver(new AppSettings { ProxyMode = ProxyMode.Pac, PacUrl = path });
        await broken.LoadPacAsync();
        Assert.NotNull(broken.PacError);
        Assert.Null(broken.Resolve(new Uri("https://example.org/")));

        var endless = new PacScript("function FindProxyForURL(u, h) { while (true) {} }");
        var guarded = new ProxyResolver(new AppSettings { ProxyMode = ProxyMode.Pac }, endless);
        Assert.Null(guarded.Resolve(new Uri("https://example.org/"))); // stopped by the time limit, then direct
    }

    [Fact]
    public async Task Pac_EndToEnd_SendsTheDownloadToTheChosenProxy()
    {
        var proxy = Track(new HttpTestProxy());
        var file = new ServedFile { Content = TestFileServer.RandomBytes(150_000, 26) };
        _server.Add("pac.bin", file);
        var path = Path.Combine(_dir, "e2e.pac");
        await File.WriteAllTextAsync(path, $$"""function FindProxyForURL(url, host) { return "PROXY 127.0.0.1:{{proxy.Port}}"; }""");
        using var store = Store(s =>
        {
            s.ProxyMode = ProxyMode.Pac;
            s.PacUrl = path;
        });
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        await service.Network.Proxy.LoadPacAsync();

        var id = service.Add(_server.UrlVia("pac.bin", "files.test"));
        Assert.Equal(DownloadStatus.Completed, await Finished(service, id));
        Assert.Contains(proxy.Destinations, d => d.StartsWith("files.test:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cookies_StayWithTheirSite_ThroughAProxyAndARedirect()
    {
        var proxy = Track(new HttpTestProxy());
        var file = new ServedFile { Content = TestFileServer.RandomBytes(50_000, 27) };
        _server.Add("target.bin", file);
        _server.Add("start.bin", new ServedFile { Content = [], RedirectTo = _server.UrlVia("target.bin", "cdn.other.test") });
        using var store = Store(s =>
        {
            s.ProxyMode = ProxyMode.Manual;
            s.ProxyHost = "127.0.0.1";
            s.ProxyPort = proxy.Port;
        });
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var context = new RequestContext("https://site.test/page", null, [new BrowserCookie("session", "only-for-site", "site.test", "/", false, true)]);

        var id = service.Add(_server.UrlVia("start.bin", "site.test"), fileName: "redirected.bin", context: context);
        Assert.Equal(DownloadStatus.Completed, await Finished(service, id));

        Assert.Contains(_server.Requests, r => r.Path == "start.bin" && r.Cookie.Contains("only-for-site"));
        Assert.All(_server.Requests.Where(r => r.Path == "target.bin"), r => Assert.DoesNotContain("only-for-site", r.Cookie));
    }

    // Stored settings and the extension protocol

    [Fact]
    public void Migration_FromVersion3_AddsLoginAndCertificateTables_WithoutLosingDownloads()
    {
        // A version 3 database (0.4) with one download, made the way 0.4 made it.
        using (var store = new DownloadStore(DbPath))
        {
            store.Insert(new DownloadRecord { Url = "https://example.com/a.zip", Directory = _dir, AddedUtc = DateTime.UtcNow, QueueId = 1 });
        }
        using (var db = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE credentials; DROP TABLE trusted_certificates; PRAGMA user_version = 3;";
            cmd.ExecuteNonQuery();
        }

        using var upgraded = new DownloadStore(DbPath);
        Assert.Equal(DownloadStore.SchemaVersion, upgraded.Version);
        Assert.EndsWith(".v3.bak", upgraded.BackupPath);
        Assert.Single(upgraded.LoadAll());
        Assert.Empty(upgraded.LoadCredentials());
        upgraded.SaveCredential(new SiteCredential { Host = "h", Username = "u", Password = "pw-x", AddedUtc = DateTime.UtcNow });
        Assert.Equal("pw-x", upgraded.LoadCredentials().Single().Password);
    }

    [Fact]
    public void NetworkSettings_RoundTrip_WithThePasswordEncrypted()
    {
        using (var store = new DownloadStore(DbPath))
        {
            store.SaveSettings(new AppSettings
            {
                ProxyMode = ProxyMode.Pac, ProxyType = ProxyType.Socks5, ProxyHost = "p", ProxyPort = 1080, ProxyUser = "me",
                ProxyPassword = "pw-settings-5", ProxyBypass = "*.lan", PacUrl = "http://wpad/wpad.dat", FtpActiveMode = true,
                TimeoutSeconds = 45, MaxConnectionsPerServer = 6, UserAgent = "Test/1.0",
            });
        }
        AssertNowhereInDatabase("pw-settings-5");
        using var reopened = new DownloadStore(DbPath);
        var s = reopened.LoadSettings();
        Assert.Equal((ProxyMode.Pac, ProxyType.Socks5, "p", 1080, "me", "pw-settings-5", "*.lan", "http://wpad/wpad.dat", true, 45, 6, "Test/1.0"),
            (s.ProxyMode, s.ProxyType, s.ProxyHost, s.ProxyPort, s.ProxyUser, s.ProxyPassword, s.ProxyBypass, s.PacUrl, s.FtpActiveMode,
                s.TimeoutSeconds, s.MaxConnectionsPerServer, s.UserAgent));
    }

    [Fact]
    public async Task QueueList_ForThePopup_NeverStartsTheApp()
    {
        var pipe = "dm-test-" + Guid.NewGuid().ToString("N");
        var reply = await NativeHost.ForwardAsync(new ChannelMessage(Messages.Queues), pipe, () => throw new InvalidOperationException("must not launch"),
            TimeSpan.FromSeconds(1));
        Assert.True(reply.Ok);
        Assert.False(reply.Running);
        Assert.Empty(reply.Queues!);

        using var cts = new CancellationTokenSource();
        var server = AppChannel.ServeAsync(pipe, m => Task.FromResult(new ChannelReply(true, Queues: m.Type == Messages.Queues ? ["Coda principale", "Notte"] : null)), cts.Token);
        reply = await NativeHost.ForwardAsync(new ChannelMessage(Messages.Queues), pipe, () => false, TimeSpan.FromSeconds(1));
        Assert.Equal(["Coda principale", "Notte"], reply.Queues);
        Assert.Contains("\"queues\":[", JsonSerializer.Serialize(reply, Messages.Json));
        cts.Cancel();
        await server;
    }

    private void AssertNowhereInDatabase(string secret)
    {
        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        foreach (var table in new[] { "downloads", "settings", "credentials" })
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table};";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    Assert.DoesNotContain(secret, reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString());
            }
        }
    }
}
