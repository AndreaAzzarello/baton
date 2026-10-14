using System.Net.Security;
using System.Security.Cryptography;
using DownloadManager.Core;
using DownloadManager.Core.Net;
using DownloadManager.Core.Security;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Tests;

/// <summary>FTP and FTPS against a local server: segments, resume, connection limits, logins, certificates, proxy.</summary>
public sealed class FtpTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _servers = [];
    private string DbPath => Path.Combine(_dir, "state.db");

    private static readonly EngineOptions Fast = new()
    {
        MinSegmentSize = 64 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        ReadTimeout = TimeSpan.FromSeconds(10),
        CheckpointInterval = TimeSpan.FromMilliseconds(100),
    };

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var server in _servers)
            await server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private FtpTestServer Server(FtpTestTls tls = FtpTestTls.None)
    {
        var server = new FtpTestServer(tls);
        _servers.Add(server);
        return server;
    }

    private DownloadStore Store(int connections = 4, Action<AppSettings>? configure = null)
    {
        var store = new DownloadStore(DbPath);
        var settings = new AppSettings { DownloadFolder = _dir, MaxConnections = connections, ProxyMode = ProxyMode.None, TimeoutSeconds = 10 };
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

    [Fact]
    public async Task Probe_UsesSizeAndMdtm()
    {
        var server = Server();
        server.Files["pub/data.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(300_000, 1) };
        var record = new DownloadRecord { Url = server.Url("pub/data.bin"), Directory = _dir };

        var probe = await NetworkContext.Default.ProbeAsync(record, CancellationToken.None);

        Assert.Equal(300_000, probe.TotalSize);
        Assert.True(probe.SupportsRanges);
        Assert.Equal("Fri, 01 May 2026 12:00:00 GMT", probe.LastModified);
        Assert.Equal("data.bin", probe.FileName);
    }

    [Fact]
    public async Task Download_IsSegmented_WithRestOffsets()
    {
        var server = Server();
        var file = server.Files["big.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(2 * 1024 * 1024, 2), Throttle = 1024 * 1024 };
        using var store = Store(connections: 4);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var id = service.Add(server.Url("big.bin"));
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "big.bin")));
        Assert.True(server.Retrs.Count(r => r.Offset > 0) >= 2, "expected parallel segments resumed with REST");
        Assert.True(server.MaxSeenConnections >= 2);
    }

    [Fact]
    public async Task PauseAndResume_ContinuesWithRest()
    {
        var server = Server();
        var file = server.Files["resume.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(1024 * 1024, 3), Throttle = 256 * 1024 };
        using var store = Store(connections: 1);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var id = service.Add(server.Url("resume.bin"));
        await WaitUntil(() => service.GetSnapshot(id)!.Downloaded > 200 * 1024);
        await service.PauseAsync(id);
        var kept = service.GetSnapshot(id)!.Downloaded;
        file.Throttle = 0;
        service.Start(id);
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "resume.bin")));
        Assert.Contains(server.Retrs, r => r.Offset >= kept - 64 * 1024 && r.Offset > 0);
    }

    [Theory]
    [InlineData("421")]
    [InlineData("530")]
    public async Task ServerAllowingOneConnection_FallsBackToOne(string refusal)
    {
        var server = Server();
        server.MaxConnections = 1;
        server.RefuseWith = refusal;
        var file = server.Files["one.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(1024 * 1024, 4), Throttle = 2 * 1024 * 1024 };
        using var store = Store(connections: 4);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        var asked = 0;
        service.LoginRequired += _ => Interlocked.Increment(ref asked);

        var id = service.Add(server.Url("one.bin"));
        await WaitUntil(() => service.GetSnapshot(id)!.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        Assert.Equal(DownloadStatus.Completed, service.GetSnapshot(id)!.Status);
        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "one.bin")));
        Assert.Equal(0, asked); // "530 too many users" on extra connections is not a login problem
    }

    [Fact]
    public async Task ServerWithoutRest_UsesOneStream()
    {
        var server = Server();
        server.RestSupported = false;
        var file = server.Files["norest.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(500_000, 5) };
        using var store = Store(connections: 4);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var id = service.Add(server.Url("norest.bin"));
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "norest.bin")));
        Assert.All(server.Retrs, r => Assert.Equal(0, r.Offset));
    }

    [Fact]
    public async Task LoginInTheAddress_IsUsed_ButNeverStoredOrShown()
    {
        var server = Server();
        server.Login = ("mario", "s3gr3t@pass");
        var file = server.Files["private.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(200_000, 6) };
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var id = service.Add(server.Url("private.bin", userInfo: "mario:s3gr3t%40pass"));
        Assert.DoesNotContain("s3gr3t", service.GetSnapshot(id)!.Url);
        Assert.DoesNotContain("mario", service.GetSnapshot(id)!.Url);
        AssertDatabaseHasNo("s3gr3t");
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "private.bin")));
        AssertDatabaseHasNo("s3gr3t");
    }

    [Fact]
    public async Task RefusedLogin_AsksTheUser_ThenResumes_AndRemembersEncrypted()
    {
        var server = Server();
        server.Login = ("anna", "pw-FTP-123");
        var file = server.Files["ask.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(150_000, 7) };
        using var store = Store();
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);
        LoginRequest? request = null;
        service.LoginRequired += r => request = r;

        var id = service.Add(server.Url("ask.bin"));
        await WaitUntil(() => request is not null);
        Assert.Equal(DownloadStatus.Paused, service.GetSnapshot(id)!.Status);
        Assert.Equal("FTP", request!.Scheme);
        Assert.False(request.IsProxy);

        service.ProvideLogin(request, "anna", "pw-FTP-123", remember: true);
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);
        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "ask.bin")));

        var saved = Assert.Single(service.Credentials.Saved);
        Assert.Equal(("127.0.0.1", "anna"), (saved.Host, saved.Username));
        AssertDatabaseHasNo("pw-FTP-123");

        // A second download from the same server needs no question.
        request = null;
        server.Files["again.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(1000, 8) };
        var second = service.Add(server.Url("again.bin"));
        await WaitUntil(() => service.GetSnapshot(second)!.Status == DownloadStatus.Completed);
        Assert.Null(request);
    }

    [Fact]
    public async Task ActiveMode_IsUsedWhenChosen()
    {
        var server = Server();
        var file = server.Files["active.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(300_000, 9) };
        using var store = Store(connections: 1, s => s.FtpActiveMode = true);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var id = service.Add(server.Url("active.bin"));
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);

        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "active.bin")));
        Assert.All(server.Retrs, r => Assert.True(r.Active));
    }

    [Theory]
    [InlineData(FtpTestTls.Explicit, "ftpes")]
    [InlineData(FtpTestTls.Implicit, "ftps")]
    public async Task Ftps_UntrustedCertificate_IsNeverAcceptedSilently(FtpTestTls tls, string scheme)
    {
        var server = Server(tls);
        var file = server.Files["secure.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(400_000, 10) };
        using var store = Store(connections: 2);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        // Nobody to ask (as in an unattended run): refused.
        var refused = service.Add(server.Url("secure.bin", scheme));
        await WaitUntil(() => service.GetSnapshot(refused)!.Status is DownloadStatus.Failed or DownloadStatus.Completed);
        Assert.Equal(DownloadStatus.Failed, service.GetSnapshot(refused)!.Status);
        Assert.Contains("certificato", service.GetSnapshot(refused)!.Error, StringComparison.OrdinalIgnoreCase);

        // The user is asked once (even with several connections) and chooses "always".
        var questions = new List<CertificateQuestion>();
        service.Certificates.Ask = q =>
        {
            lock (questions) questions.Add(q);
            return Task.FromResult(CertificateDecision.AcceptAlways);
        };
        var accepted = service.Add(server.Url("secure.bin", scheme), fileName: "secure-ok.bin");
        await WaitUntil(() => service.GetSnapshot(accepted)!.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, service.GetSnapshot(accepted)!.Status);
        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "secure-ok.bin")));
        var question = Assert.Single(questions.DistinctBy(q => q.Fingerprint));
        Assert.Equal(CertificateTrust.FingerprintOf(FtpTestServer.Certificate), question.Fingerprint);
        Assert.NotEqual(SslPolicyErrors.None, question.Errors);

        // Remembered: a new run of the app does not ask again.
        await service.DisposeAsync();
        using var reopened = new DownloadStore(DbPath);
        await using var later = new DownloadService(reopened, engineTemplate: Fast, autoTick: false);
        later.Certificates.Ask = _ => throw new InvalidOperationException("must not ask again");
        var third = later.Add(server.Url("secure.bin", scheme), fileName: "secure-3.bin");
        await WaitUntil(() => later.GetSnapshot(third)!.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, later.GetSnapshot(third)!.Status);
    }

    [Fact]
    public async Task UpdateAddress_ComparesSizeAndMdtm()
    {
        var server = Server();
        var content = TestFileServer.RandomBytes(800_000, 11);
        var file = server.Files["v1/file.iso"] = new FtpTestFile { Content = content, Throttle = 200 * 1024 };
        server.Files["mirror/file.iso"] = new FtpTestFile { Content = content, Modified = file.Modified };
        server.Files["other/file.iso"] = new FtpTestFile { Content = TestFileServer.RandomBytes(800_000, 12), Modified = file.Modified.AddDays(1) };
        using var store = Store(connections: 1);
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        var id = service.Add(server.Url("v1/file.iso"));
        await WaitUntil(() => service.GetSnapshot(id)!.Downloaded > 100 * 1024);
        await service.PauseAsync(id);

        Assert.Equal(UrlMatch.Different, (await service.CheckNewUrlAsync(id, server.Url("other/file.iso"))).Match);
        var same = await service.CheckNewUrlAsync(id, server.Url("mirror/file.iso"));
        Assert.Equal(UrlMatch.Same, same.Match);

        await service.ChangeUrlAsync(id, same, keepData: true);
        service.Start(id);
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), Hash(service.GetSnapshot(id)!.FinalPath!)); // .iso: in the "Compressi" folder
        Assert.Contains(server.Retrs, r => r.Path == "mirror/file.iso" && r.Offset > 0);
    }

    [Fact]
    public async Task Ftp_GoesThroughASocks5Proxy()
    {
        var server = Server();
        var file = server.Files["via-proxy.bin"] = new FtpTestFile { Content = TestFileServer.RandomBytes(300_000, 13) };
        var proxy = new Socks5TestProxy { Login = ("proxyuser", "proxypw") };
        _servers.Add(proxy);
        using var store = Store(connections: 1, s =>
        {
            s.ProxyMode = ProxyMode.Manual;
            s.ProxyType = ProxyType.Socks5;
            s.ProxyHost = "127.0.0.1";
            s.ProxyPort = proxy.Port;
            s.ProxyUser = "proxyuser";
            s.ProxyPassword = "proxypw";
        });
        await using var service = new DownloadService(store, engineTemplate: Fast, autoTick: false);

        // "ftp.test" exists only for the proxy, so success proves the proxy carried the connection.
        var id = service.Add(server.Url("via-proxy.bin", host: "ftp.test"));
        await WaitUntil(() => service.GetSnapshot(id)!.Status is DownloadStatus.Completed or DownloadStatus.Failed);

        Assert.Equal(DownloadStatus.Completed, service.GetSnapshot(id)!.Status);
        Assert.Equal(file.Sha256, Hash(Path.Combine(_dir, "via-proxy.bin")));
        Assert.Contains(proxy.Destinations, d => d.StartsWith("ftp.test:", StringComparison.Ordinal));
    }

    private void AssertDatabaseHasNo(string secret)
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
