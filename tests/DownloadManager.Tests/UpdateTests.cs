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
    public async Task GitHubRelease_IsOfferedOnlyWithItsShaAsset()
    {
        var exe = FakeExe(2);
        var exeUrl = _server.Add("DownloadManager.exe", new ServedFile { Content = exe });
        var shaUrl = Serve("DownloadManager.exe.sha256", $"{Sha(exe)}  DownloadManager.exe\n", "text/plain");
        var withAsset = Serve("release.json", $$"""
            { "tag_name": "v1.2.0", "name": "1.2.0", "body": "- novità",
              "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" },
                          { "name": "DownloadManager.exe.sha256", "browser_download_url": "{{shaUrl}}" } ] }
            """);
        // A hash in the notes or in a SHA256SUMS file is not enough any more, nor an asset that holds no hash.
        var inNotes = Serve("release2.json", $$"""
            { "tag_name": "v1.3.0", "body": "Correzioni.\n\nSHA-256: `{{Sha(exe)}}`",
              "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" } ] }
            """);
        var sumsUrl = Serve("SHA256SUMS", $"{Sha(exe)}  DownloadManager.exe\n", "text/plain");
        var sums = Serve("release4.json", $$"""
            { "tag_name": "v1.5.0", "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" },
                                               { "name": "SHA256SUMS", "browser_download_url": "{{sumsUrl}}" } ] }
            """);
        var badUrl = Serve("bad.sha256", "not a hash\n", "text/plain");
        var bad = Serve("release5.json", $$"""
            { "tag_name": "v1.6.0", "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" },
                                               { "name": "DownloadManager.exe.sha256", "browser_download_url": "{{badUrl}}" } ] }
            """);
        var without = Serve("release3.json", $$"""{ "tag_name": "v1.4.0", "body": "", "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" } ] }""");

        Assert.Equal(Sha(exe), (await UpdateChecker.CheckAsync(_http, withAsset, Current, default))!.Sha256);
        foreach (var refused in new[] { inNotes, without, sums, bad })
        {
            var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, refused, Current, default));
            Assert.True(ex.Unverifiable);
            Assert.Contains("DownloadManager.exe.sha256", ex.Message); // the message names what the release lacks
        }

        // A release that is not newer is never an error, even without its hash (no dialog at every start).
        var same = Serve("release6.json", $$"""{ "tag_name": "v1.0.0", "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" } ] }""");
        var older = Serve("old.json", """{ "version": "0.9.0", "url": "http://elsewhere.example/x.exe" }""");
        Assert.Null(await UpdateChecker.CheckAsync(_http, same, Current, default));
        Assert.Null(await UpdateChecker.CheckAsync(_http, older, Current, default));
    }

    [Theory]
    [InlineData("{0}  DownloadManager.exe\n", true)]                                     // sha256sum
    [InlineData("SHA256 (DownloadManager.exe) = {0}\n", true)]                           // BSD
    [InlineData("SHA256 hash of DownloadManager.exe:\r\n{0}\r\nCertUtil: done.\r\n", true)] // certutil
    [InlineData("﻿{0}\r\n", true)]                                                  // PowerShell, UTF-8 with BOM
    [InlineData("{0}  DownloadManager-arm64.exe\n", false)]                              // another program's hash
    [InlineData("no hash here", false)]
    public void ShaAsset_IsReadInTheUsualFormats(string template, bool accepted)
    {
        var sha = new string('a', 64);
        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, sha).TrimStart('﻿');
        Assert.Equal(accepted ? sha : null, UpdateChecker.ShaFromAsset(text, "DownloadManager.exe"));
    }

    [Fact]
    public async Task ShaAsset_WrittenAsUtf16ByWindowsPowerShell_IsRead()
    {
        var exe = FakeExe(5);
        var exeUrl = _server.Add("DownloadManager.exe", new ServedFile { Content = exe });
        var shaUrl = _server.Add("DownloadManager.exe.sha256",
            new ServedFile { Content = [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes(Sha(exe).ToUpperInvariant() + "\r\n")] });
        var release = Serve("utf16.json", $$"""
            { "tag_name": "v1.7.0", "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" },
                                               { "name": "DownloadManager.exe.sha256", "browser_download_url": "{{shaUrl}}" } ] }
            """);
        Assert.Equal(Sha(exe), (await UpdateChecker.CheckAsync(_http, release, Current, default))!.Sha256);
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
