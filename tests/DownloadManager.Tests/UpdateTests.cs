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

    // The test's own signing keys: the real one stays outside the repository.
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly ECDsa NextSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly ECDsa Stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly ReleaseKey[] Keys = [KeyOf(Signer)];

    private static ReleaseKey KeyOf(ECDsa key)
    {
        var q = key.ExportParameters(false).Q;
        return ReleaseKey.FromBase64(Convert.ToBase64String([.. q.X!, .. q.Y!]))!;
    }

    /// <summary>A ".sig" text as tools\sign-release.ps1 writes it.</summary>
    private static string Sign(string version, string file, byte[] exe, params ECDsa[] signers) =>
        "# " + file + " " + version + "\n" + string.Concat(signers.Select(s =>
            $"{KeyOf(s).Id} {Convert.ToBase64String(s.SignData(Encoding.UTF8.GetBytes(UpdateSignature.Statement(Version.Parse(version), file, Sha(exe))), HashAlgorithmName.SHA256))}\n"));

    private static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    private string Serve(string name, string text, string type = "application/json") =>
        _server.Add(name, new ServedFile { Content = Encoding.UTF8.GetBytes(text), ContentType = type });

    [Fact]
    public async Task Manifest_NewerVersionIsOffered_SameOrOlderIsNot()
    {
        var exe = FakeExe(1);
        var exeUrl = _server.Add("DownloadManager.exe", new ServedFile { Content = exe });
        var newer = Serve("newer.json", $$"""{ "version": "1.1.0", "notes": "Novità: grabber più veloce", "url": "{{exeUrl}}", "sha256": "{{Sha(exe)}}", "signature": {{Json(Sign("1.1.0", "DownloadManager.exe", exe, Signer))}} }""");
        var same = Serve("same.json", $$"""{ "version": "1.0", "url": "{{exeUrl}}", "sha256": "{{Sha(exe)}}" }""");
        var noHash = Serve("nohash.json", $$"""{ "version": "2.0.0", "url": "{{exeUrl}}" }""");

        var info = await UpdateChecker.CheckAsync(_http, newer, Current, default, Keys);
        Assert.Equal(new Version(1, 1, 0), info!.Version);
        Assert.Contains("grabber", info.Notes);
        Assert.Null(await UpdateChecker.CheckAsync(_http, same, Current, default, Keys));
        await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, noHash, Current, default, Keys));
        await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, "http://updates.example.com/latest.json", Current, default, Keys));
        Assert.Null(UpdateChecker.Allowed("http://user:pw@127.0.0.1/x"));
    }

    [Fact]
    public async Task GitHubRelease_IsOfferedOnlyWithItsShaAsset()
    {
        var exe = FakeExe(2);
        var exeUrl = _server.Add("DownloadManager.exe", new ServedFile { Content = exe });
        var shaUrl = Serve("DownloadManager.exe.sha256", $"{Sha(exe)}  DownloadManager.exe\n", "text/plain");
        var sigUrl = Serve("DownloadManager.exe.sig", Sign("1.2.0", "DownloadManager.exe", exe, Signer), "text/plain");
        var withAsset = Serve("release.json", $$"""
            { "tag_name": "v1.2.0", "name": "1.2.0", "body": "- novità",
              "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" },
                          { "name": "DownloadManager.exe.sha256", "browser_download_url": "{{shaUrl}}" },
                          { "name": "DownloadManager.exe.sig", "browser_download_url": "{{sigUrl}}" } ] }
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

        Assert.Equal(Sha(exe), (await UpdateChecker.CheckAsync(_http, withAsset, Current, default, Keys))!.Sha256);
        foreach (var refused in new[] { inNotes, without, sums, bad })
        {
            var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, refused, Current, default, Keys));
            Assert.True(ex.Unverifiable);
            Assert.Contains("DownloadManager.exe.sha256", ex.Message); // the message names what the release lacks
        }

        // A release that is not newer is never an error, even without its hash (no dialog at every start).
        var same = Serve("release6.json", $$"""{ "tag_name": "v1.0.0", "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" } ] }""");
        var older = Serve("old.json", """{ "version": "0.9.0", "url": "http://elsewhere.example/x.exe" }""");
        Assert.Null(await UpdateChecker.CheckAsync(_http, same, Current, default, Keys));
        Assert.Null(await UpdateChecker.CheckAsync(_http, older, Current, default, Keys));
    }

    [Fact]
    public async Task GitHubRelease_WithoutItsSignature_OrWithAWrongOne_IsRefused()
    {
        var exe = FakeExe(12);
        var exeUrl = _server.Add("DownloadManager.exe", new ServedFile { Content = exe });
        var shaUrl = Serve("DownloadManager.exe.sha256", $"{Sha(exe)}  DownloadManager.exe\n", "text/plain");
        string Release(string name, string? sig) => Serve(name, $$"""
            { "tag_name": "v1.2.0", "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" },
                                               { "name": "DownloadManager.exe.sha256", "browser_download_url": "{{shaUrl}}" }
                                               {{(sig is null ? "" : $$""", { "name": "DownloadManager.exe.sig", "browser_download_url": "{{Serve(name + ".sig", sig, "text/plain")}}" }""")}} ] }
            """);

        var missing = await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, Release("nosig.json", null), Current, default, Keys));
        Assert.True(missing.Unverifiable); // told even after an automatic check
        Assert.Contains("DownloadManager.exe.sig", missing.Message);
        foreach (var (name, sig) in new[]
                 {
                     ("stranger.json", Sign("1.2.0", "DownloadManager.exe", exe, Stranger)), // a key the app does not know
                     ("garbage.json", $"{Keys[0].Id} not-base64!\n{Keys[0].Id} {Convert.ToBase64String(new byte[64])}\n"),
                     ("other-exe.json", Sign("1.2.0", "DownloadManager-arm64.exe", exe, Signer)),
                 })
        {
            var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, Release(name, sig), Current, default, Keys));
            Assert.True(ex.Unverifiable);
            Assert.DoesNotContain(".sig", ex.Message); // present, but not valid: a different message
        }
    }

    [Fact]
    public async Task Signature_BindsVersionAndHash_SoAnOldSignedReleaseCannotPassForANewOne()
    {
        var exe = FakeExe(13);
        var exeUrl = _server.Add("DownloadManager.exe", new ServedFile { Content = exe });
        string Manifest(string name, string version, string sha, string sig) =>
            Serve(name, $$"""{ "version": "{{version}}", "url": "{{exeUrl}}", "sha256": "{{sha}}", "signature": {{Json(sig)}} }""");

        // A genuine 1.1.0 signature replayed under 9.9.9, or with another program's hash, is not valid.
        var signed110 = Sign("1.1.0", "DownloadManager.exe", exe, Signer);
        Assert.NotNull(await UpdateChecker.CheckAsync(_http, Manifest("ok.json", "1.1.0", Sha(exe), signed110), Current, default, Keys));
        foreach (var (name, version, sha) in new[] { ("replay.json", "9.9.9", Sha(exe)), ("swap.json", "1.1.0", Sha(FakeExe(14))) })
            await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, Manifest(name, version, sha, signed110), Current, default, Keys));
        var unsigned = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateChecker.CheckAsync(_http, Serve("none.json", $$"""{ "version": "1.1.0", "url": "{{exeUrl}}", "sha256": "{{Sha(exe)}}" }"""), Current, default, Keys));
        Assert.Contains("\"signature\"", unsigned.Message);
    }

    [Fact]
    public async Task KeyRotation_BothKeysDuringTheChange_TheRetiredOneNoLonger()
    {
        var exe = FakeExe(15);
        var exeUrl = _server.Add("DownloadManager.exe", new ServedFile { Content = exe });
        var n = 0;
        async Task<bool> Accepted(ReleaseKey[] appKeys, params ECDsa[] signers)
        {
            var url = Serve($"rot{n++}.json", $$"""{ "version": "1.1.0", "url": "{{exeUrl}}", "sha256": "{{Sha(exe)}}", "signature": {{Json(Sign("1.1.0", "DownloadManager.exe", exe, signers))}} }""");
            try
            {
                return await UpdateChecker.CheckAsync(_http, url, Current, default, appKeys) is not null;
            }
            catch (UpdateException)
            {
                return false;
            }
        }
        ReleaseKey[] old = [KeyOf(Signer)], both = [KeyOf(Signer), KeyOf(NextSigner)], next = [KeyOf(NextSigner)];

        Assert.True(await Accepted(old, Signer, NextSigner));   // apps that know only the old key, during the change
        Assert.True(await Accepted(next, Signer, NextSigner));  // apps that already have the new one
        Assert.True(await Accepted(both, NextSigner));          // after the change, the new key alone
        Assert.False(await Accepted(next, Signer));             // the retired key is no longer enough
        Assert.False(await Accepted(old, NextSigner));          // nor a key the app has never been given
    }

    [Fact]
    public void SignatureWrittenBySignReleaseScript_IsAccepted()
    {
        // Produced by tools\sign-release.ps1 (Windows PowerShell 5.1, CNG) with a throwaway key: the formats must agree.
        var key = ReleaseKey.FromBase64("4+0Edkc4kh3QyFAjOjKatAnaFZW33qHAeJ7ScOw2yIhoZ8NX1WAVw6MNAC7UvJumXiYN38+BNcz9rmZWJB/nRQ==")!;
        const string sig = "# DownloadManager.exe 1.1.1\n394e91e956cea8bd PF31u54EZkBX1kMPIHkYshMQB50DAshd8faubXU2Pc9dErvLYAVmBLPwyfj0H/ZinEM97edNanzPQPM5I1v+nw==\n";
        const string sha = "b18a3c38ff0476b14379de9a0880bf6c04478d659ed5c2e3bd8c8585fb11269b";
        Assert.Equal("394e91e956cea8bd", key.Id);
        Assert.True(UpdateSignature.Verify(sig, new Version(1, 1, 1), "DownloadManager.exe", sha, [key]));
        Assert.False(UpdateSignature.Verify(sig, new Version(1, 1, 2), "DownloadManager.exe", sha, [key]));
    }

    [Fact]
    public void BuiltInKeys_AreValidPoints_AndThereIsAtLeastOne()
    {
        Assert.NotEmpty(UpdateSignature.Trusted); // otherwise this version could never update again
        Assert.All(UpdateSignature.Trusted, k => Assert.Equal(ReleaseKey.IdOf(k.Point), k.Id));
        Assert.Throws<InvalidDataException>(() => UpdateSignature.Parse(Convert.ToBase64String(new byte[64]))); // not on the curve
        Assert.Empty(UpdateSignature.Parse("# only a comment\n\n"));
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
        var sigUrl = Serve("utf16.sig", Sign("1.7.0", "DownloadManager.exe", exe, Signer).Replace("\n", "\r\n"), "text/plain");
        var release = Serve("utf16.json", $$"""
            { "tag_name": "v1.7.0", "assets": [ { "name": "DownloadManager.exe", "browser_download_url": "{{exeUrl}}" },
                                               { "name": "DownloadManager.exe.sha256", "browser_download_url": "{{shaUrl}}" },
                                               { "name": "DownloadManager.exe.sig", "browser_download_url": "{{sigUrl}}" } ] }
            """);
        Assert.Equal(Sha(exe), (await UpdateChecker.CheckAsync(_http, release, Current, default, Keys))!.Sha256);
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
