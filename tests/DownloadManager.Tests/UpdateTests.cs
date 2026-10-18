using System.Security.Cryptography;
using System.Text;
using DownloadManager.Core.Updates;

namespace DownloadManager.Tests;

/// <summary>Updates against a local server: manifests, GitHub releases, hash verification, the swap and the automatic rollback.</summary>
public sealed class UpdateTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private static readonly Version Current = new(1, 0, 0);

    public async Task InitializeAsync()
    {
        _server = await TestFileServer.StartAsync();
        Directory.CreateDirectory(_dir);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] FakeExe(int seed)
    {
        var bytes = TestFileServer.RandomBytes(300_000, seed);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string Serve(string name, string text, string type = "application/json") =>
        _server.Add(name, new ServedFile { Content = Encoding.UTF8.GetBytes(text), ContentType = type });

    [Fact]
    public async Task Manifest_NewerVersionIsOffered_SameOrOlderIsNot()
    {
        var exe = FakeExe(1);
        var exeUrl = _server.Add("DownloadManager.exe", new ServedFile { Content = exe });
        var newer = Serve("newer.json", $$"""{ "version": "1.1.0", "notes": "Novità: grabber più veloce", "url": "{{exeUrl}}", "sha256": "{{Sha(exe)}}" }""");
        var same = Serve("same.json", $$"""{ "version": "1.0", "url": "{{exeUrl}}", "sha256": "{{Sha(exe)}}" }""");
        var noHash = Serve("nohash.json", $$"""{ "version": "2.0.0", "url": "{{exeUrl}}" }""");

        var info = await UpdateChecker.CheckAsync(_http, newer, Current, default);
        Assert.Equal(new Version(1, 1, 0), info!.Version);
        Assert.Contains("grabber", info.Notes);
        Assert.Null(await UpdateChecker.CheckAsync(_http, same, Current, default));
        await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, noHash, Current, default));
        await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, "http://updates.example.com/latest.json", Current, default));
        Assert.Null(UpdateChecker.Allowed("http://user:pw@127.0.0.1/x"));
    }

    [Fact]
    public async Task GitHubRelease_WithAShaAsset_OrAShaInTheNotes()
    {
        var exe = FakeExe(2);
        var exeUrl = _server.Add("DownloadManager.exe", new ServedFile { Content = exe });
        var shaUrl = Serve("DownloadManager.exe.sha256", $"{Sha(exe)}  DownloadManager.exe\n", "text/plain");
        var withAsset = Serve("release.json", $$"""
            { "tag_name": "v1.2.0", "name": "1.2.0", "body": "- novità",
              "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" },
                          { "name": "DownloadManager.exe.sha256", "browser_download_url": "{{shaUrl}}" } ] }
            """);
        var inNotes = Serve("release2.json", $$"""
            { "tag_name": "v1.3.0", "body": "Correzioni.\n\nSHA-256: `{{Sha(exe)}}`",
              "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" } ] }
            """);
        var without = Serve("release3.json", $$"""{ "tag_name": "v1.4.0", "body": "", "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" } ] }""");

        Assert.Equal(Sha(exe), (await UpdateChecker.CheckAsync(_http, withAsset, Current, default))!.Sha256);
        Assert.Equal(new Version(1, 3, 0), (await UpdateChecker.CheckAsync(_http, inNotes, Current, default))!.Version);
        await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, without, Current, default));
    }

    [Fact]
    public async Task Download_IsVerified_BeforeAnythingChanges()
    {
        var exe = FakeExe(3);
        var url = new Uri(_server.Add("DownloadManager.exe", new ServedFile { Content = exe }));
        var folder = Path.Combine(_dir, "update");

        var good = await UpdateInstaller.DownloadAsync(_http, new UpdateInfo(new Version(1, 1, 0), "", url, Sha(exe), "DownloadManager.exe"), folder, null, default);
        Assert.Equal(exe, File.ReadAllBytes(good));

        File.Delete(good);
        await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateInstaller.DownloadAsync(_http, new UpdateInfo(new Version(1, 1, 0), "", url, new string('0', 64), "DownloadManager.exe"), folder, null, default));
        Assert.Empty(Directory.GetFiles(folder)); // a file that does not match is never kept

        var text = new Uri(_server.Add("notexe.exe", new ServedFile { Content = Encoding.UTF8.GetBytes("<html>error</html>") }));
        await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateInstaller.DownloadAsync(_http, new UpdateInfo(new Version(1, 1, 0), "", text, Sha(Encoding.UTF8.GetBytes("<html>error</html>")), "x"), folder, null, default));
    }

    private sealed class FakeProcess(Func<bool> exited) : IProcessHandle
    {
        public bool Killed { get; private set; }
        public bool HasExited => exited();
        public void Kill() => Killed = true;
    }

    private (string Exe, string Marker) Installed(byte[] oldContent, byte[] newContent)
    {
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        var exe = Path.Combine(app, "DownloadManager.exe");
        File.WriteAllBytes(exe, oldContent);
        var verified = Path.Combine(_dir, "verified.exe");
        File.WriteAllBytes(verified, newContent);
        UpdateInstaller.Swap(verified, exe);
        return (exe, Path.Combine(_dir, "update.ok"));
    }

    [Fact]
    public async Task Watcher_KeepsAnUpdateThatStarts()
    {
        var (exe, marker) = Installed(FakeExe(4), FakeExe(5));
        Assert.Equal(FakeExe(5), File.ReadAllBytes(exe));
        Assert.Equal(FakeExe(4), File.ReadAllBytes(UpdateInstaller.BackupOf(exe)));

        var started = new List<string>();
        var ok = await UpdateInstaller.WatchAsync(exe, marker, () => Task.CompletedTask, path =>
        {
            started.Add(path);
            _ = Task.Delay(300).ContinueWith(_ => File.WriteAllText(marker, "ok")); // the new version reports it started
            return new FakeProcess(() => false);
        }, TimeSpan.FromSeconds(10), _ => Assert.Fail("no restart expected"));

        Assert.True(ok);
        Assert.Equal([exe], started);
        Assert.Equal(FakeExe(5), File.ReadAllBytes(exe));
    }

    [Fact]
    public async Task Watcher_RestoresThePreviousVersion_WhenTheUpdateCrashesOrStaysSilent()
    {
        var (exe, marker) = Installed(FakeExe(6), FakeExe(7));
        string? restarted = null;
        var crashed = await UpdateInstaller.WatchAsync(exe, marker, () => Task.CompletedTask, _ => new FakeProcess(() => true),
            TimeSpan.FromSeconds(10), path => restarted = path);
        Assert.False(crashed);
        Assert.Equal(exe, restarted);
        Assert.Equal(FakeExe(6), File.ReadAllBytes(exe));
        Assert.Equal(FakeExe(7), File.ReadAllBytes(exe + ".failed"));

        (exe, marker) = Installed(FakeExe(8), FakeExe(9));
        var silent = new FakeProcess(() => false);
        Assert.False(await UpdateInstaller.WatchAsync(exe, marker, () => Task.CompletedTask, _ => silent, TimeSpan.FromSeconds(1), _ => { }));
        Assert.True(silent.Killed);
        Assert.Equal(FakeExe(8), File.ReadAllBytes(exe));
    }

    [Fact]
    public void ManualRestore_SwapsBack_AndNothingWithoutABackup()
    {
        var (exe, _) = Installed(FakeExe(10), FakeExe(11));
        Assert.True(UpdateInstaller.Restore(exe));
        Assert.Equal(FakeExe(10), File.ReadAllBytes(exe));
        Assert.False(UpdateInstaller.Restore(exe)); // the backup was used
    }
}
