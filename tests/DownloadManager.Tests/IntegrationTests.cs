using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Core;
using DownloadManager.Core.Integration;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Tests;

/// <summary>Browser integration: request context, native messaging framing and the app channel.</summary>
public sealed class IntegrationTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "dm-test-" + Guid.NewGuid().ToString("N");
    private string DbPath => Path.Combine(_dir, "state.db");

    private static readonly EngineOptions Fast = new()
    {
        MinSegmentSize = 64 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        CheckpointInterval = TimeSpan.FromMilliseconds(200),
    };

    private static readonly RequestContext SessionContext = new(
        "https://example.test/page",
        "Mozilla/5.0 TestBrowser",
        [new BrowserCookie("session", "abc123", "127.0.0.1", "/", false, true)]);

    public async Task InitializeAsync() => _server = await TestFileServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static bool HasSession(Microsoft.AspNetCore.Http.HttpRequest r) =>
        r.Cookies["session"] == "abc123" && r.Headers.Referer == "https://example.test/page" && r.Headers.UserAgent == "Mozilla/5.0 TestBrowser";

    [Fact]
    public async Task BrowserContext_IsSentOnEveryConnection_AndClearedAfterCompletion()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(2 * 1024 * 1024, 70), Authorize = HasSession };
        var url = _server.Add("private.bin", file);

        using var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { MaxConnections = 4, DownloadFolder = _dir });
        await using var service = new DownloadService(store, engineTemplate: Fast);

        var refused = service.Add(url, fileName: "refused.bin");
        var allowed = service.Add(url, fileName: "allowed.bin", context: SessionContext);
        await WaitUntil(() => service.GetSnapshots().All(s => s.Status is DownloadStatus.Completed or DownloadStatus.Failed));

        var snaps = service.GetSnapshots().ToDictionary(s => s.Id);
        Assert.Equal(DownloadStatus.Failed, snaps[refused].Status);
        Assert.Equal(DownloadStatus.Completed, snaps[allowed].Status);
        Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_dir, "allowed.bin")))));
        Assert.True(_server.Requests.Count(r => r.Path == "private.bin" && r.From is > 0) >= 2, "expected several range connections");
        Assert.Null(store.LoadAll().Single(r => r.Id == allowed).Context);
    }

    [Fact]
    public void Store_KeepsContextEncrypted_AndUpgradesOldDatabases()
    {
        Directory.CreateDirectory(_dir);
        // A database created by version 0.1, before the request_context column existed.
        using (var old = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE downloads (id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, directory TEXT NOT NULL, file_name TEXT,
                    total_size INTEGER, etag TEXT, last_modified TEXT, supports_ranges INTEGER NOT NULL, status INTEGER NOT NULL, error TEXT,
                    sort_order INTEGER NOT NULL, added_utc TEXT NOT NULL, completed_utc TEXT);
                INSERT INTO downloads (url, directory, supports_ranges, status, sort_order, added_utc)
                VALUES ('http://old.test/a.zip', 'C:\\', 0, 2, 0, '2026-01-01T00:00:00.0000000Z');
                """;
            cmd.ExecuteNonQuery();
        }

        long id;
        using (var store = new DownloadStore(DbPath))
        {
            Assert.Null(Assert.Single(store.LoadAll()).Context);
            id = store.Insert(new DownloadRecord { Url = "http://new.test/b.zip", Directory = _dir, AddedUtc = DateTime.UtcNow, Context = SessionContext });
        }

        using (var raw = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = $"SELECT request_context FROM downloads WHERE id = {id};";
            var stored = (string)cmd.ExecuteScalar()!;
            Assert.StartsWith("dpapi:", stored);
            Assert.DoesNotContain("abc123", stored);
        }

        using var reopened = new DownloadStore(DbPath);
        var context = reopened.LoadAll().Single(r => r.Id == id).Context!;
        Assert.Equal(SessionContext.Referrer, context.Referrer);
        Assert.Equal(SessionContext.Cookies, context.Cookies);
    }

    [Fact]
    public void Cookies_AreOnlySentToTheirOwnHost()
    {
        var container = RequestContexts.ToCookieContainer(
        [
            new BrowserCookie("host", "1", "files.example.com", "/", false, true),
            new BrowserCookie("domain", "2", ".example.com", "/", false, false),
            new BrowserCookie("secure", "3", "files.example.com", "/", true, true),
            new BrowserCookie("bad", "a,b", "files.example.com", "/", false, true),
        ]);

        Assert.Equal("host=1; domain=2; secure=3", SortedHeader(container, "https://files.example.com/x.zip"));
        Assert.Equal("host=1; domain=2", SortedHeader(container, "http://files.example.com/x.zip"));
        Assert.Equal("domain=2", SortedHeader(container, "https://cdn.example.com/x.zip"));
        Assert.Equal("", SortedHeader(container, "https://other.test/x.zip"));

        static string SortedHeader(System.Net.CookieContainer c, string url) =>
            string.Join("; ", c.GetCookieHeader(new Uri(url)).Split("; ", StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(p => p == "host=1" ? 0 : p == "domain=2" ? 1 : 2));
    }

    [Fact]
    public async Task NativeHost_ForwardsMessagesToTheRunningApp()
    {
        var received = new List<ChannelMessage>();
        using var cts = new CancellationTokenSource();
        var server = AppChannel.ServeAsync(_pipe, m =>
        {
            lock (received) received.Add(m);
            return Task.FromResult(new ChannelReply(true));
        }, cts.Token);

        var add = new ChannelMessage(Messages.Add, new BrowserDownload("https://example.test/f.zip", "f.zip", Cookies: [.. SessionContext.Cookies]));
        var replies = await RunHostAsync([new ChannelMessage(Messages.Ping), add], launchApp: () => throw new InvalidOperationException("must not launch"));

        Assert.All(replies, r => Assert.True(r.Ok && r.Running));
        Assert.Equal(2, received.Count);
        Assert.Equal("https://example.test/f.zip", received[1].Download!.Url);
        Assert.Equal("abc123", received[1].Download!.Cookies![0].Value);

        cts.Cancel();
        await server;
    }

    [Fact]
    public async Task NativeHost_StartsTheAppWhenNeeded_ButNotForAPing()
    {
        using var cts = new CancellationTokenSource();
        Task? server = null;
        var launches = 0;
        bool Launch()
        {
            launches++;
            // The app needs a moment to start listening.
            server = Task.Delay(700).ContinueWith(_ => AppChannel.ServeAsync(_pipe, _ => Task.FromResult(new ChannelReply(true)), cts.Token)).Unwrap();
            return true;
        }

        var replies = await RunHostAsync([new ChannelMessage(Messages.Ping), new ChannelMessage(Messages.Add, new BrowserDownload("https://example.test/a"))], Launch);

        Assert.True(replies[0].Ok);
        Assert.False(replies[0].Running);
        Assert.True(replies[1].Ok);
        Assert.Equal(1, launches);
        cts.Cancel();
        await server!;
    }

    [Fact]
    public async Task NativeHost_RejectsMalformedInputWithoutCrashing()
    {
        var input = new MemoryStream();
        await NativeHost.WriteMessageAsync(input, "{not json"u8.ToArray());
        input.Position = 0;
        var output = new MemoryStream();

        await NativeHost.RunAsync(input, output, _pipe, () => false, TimeSpan.FromSeconds(1));

        output.Position = 0;
        var reply = JsonSerializer.Deserialize<ChannelReply>((await NativeHost.ReadMessageAsync(output))!, Messages.Json)!;
        Assert.False(reply.Ok);
        Assert.Null(await NativeHost.ReadMessageAsync(output));
    }

    [Fact]
    public void ExtensionManifestKey_MatchesTheIdAllowedByTheHost()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "DownloadManager.slnx")))
            dir = Path.GetDirectoryName(dir)!;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "extension", "manifest.json")));
        var key = Convert.FromBase64String(manifest.RootElement.GetProperty("key").GetString()!);

        // Chromium derives an extension ID from the first 16 bytes of SHA-256(public key), written with letters a-p.
        var id = new StringBuilder();
        foreach (var b in SHA256.HashData(key).AsSpan(0, 16))
            id.Append((char)('a' + (b >> 4))).Append((char)('a' + (b & 15)));
        Assert.Equal(NativeHost.ExtensionId, id.ToString());
    }

    private async Task<List<ChannelReply>> RunHostAsync(IEnumerable<ChannelMessage> messages, Func<bool> launchApp)
    {
        var input = new MemoryStream();
        foreach (var m in messages)
            await NativeHost.WriteMessageAsync(input, JsonSerializer.SerializeToUtf8Bytes(m, Messages.Json));
        input.Position = 0;
        var output = new MemoryStream();

        await NativeHost.RunAsync(input, output, _pipe, launchApp, TimeSpan.FromSeconds(10));

        output.Position = 0;
        var replies = new List<ChannelReply>();
        while (await NativeHost.ReadMessageAsync(output) is { } json)
            replies.Add(JsonSerializer.Deserialize<ChannelReply>(json, Messages.Json)!);
        return replies;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached in time.");
            await Task.Delay(50);
        }
    }
}
