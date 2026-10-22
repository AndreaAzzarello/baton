using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Baton.Core.Setup;
using Baton.Core.Updates;

namespace Baton.Tests;

/// <summary>
/// The extension next to Baton.exe follows the program (1.3.3): its signed package in the release, the checks before
/// anything changes (hash, signature, version, the zip's contents), the swap with ".old", the rollback, and the repair
/// after an update made by 1.3.2, which replaced only Baton.exe.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ExtensionUpdateTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private static readonly Version Old = new(1, 3, 2);
    private static readonly Version New = new(1, 3, 3);

    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly ECDsa Stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly ReleaseKey[] Keys = [KeyOf(Signer)];

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

    private static ReleaseKey KeyOf(ECDsa key)
    {
        var q = key.ExportParameters(false).Q;
        return ReleaseKey.FromBase64(Convert.ToBase64String([.. q.X!, .. q.Y!]))!;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Sign(Version version, string file, byte[] content, ECDsa signer) =>
        $"# {file} {version.ToString(3)}\n{KeyOf(signer).Id} {Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(UpdateSignature.Statement(version, file, Sha(content))), HashAlgorithmName.SHA256))}\n";

    /// <summary>A package as build-packages.ps1 makes it: the Chromium folder and the Firefox package.</summary>
    private static byte[] Package(string version, Action<ZipArchive>? extra = null, bool firefox = true)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(zip, "extension/manifest.json", $$"""{ "manifest_version": 3, "version": "{{version}}" }""");
            Entry(zip, "extension/background.js", "// " + version);
            if (firefox)
                Entry(zip, "baton-firefox.xpi", "xpi " + version);
            extra?.Invoke(zip);
        }
        return memory.ToArray();
    }

    private static void Entry(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>A program folder as the installer (or the portable zip) leaves it, with the extension of <paramref name="version"/>.</summary>
    private string ProgramFolder(string version, bool portable = false)
    {
        var folder = Path.Combine(_dir, "app" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(folder, "extension"));
        File.WriteAllText(Path.Combine(folder, "Baton.exe"), "program");
        File.WriteAllText(Path.Combine(folder, "extension", "manifest.json"), $$"""{ "version": "{{version}}" }""");
        File.WriteAllText(Path.Combine(folder, "extension", "old-only.js"), "dropped in the new version");
        File.WriteAllText(Path.Combine(folder, "baton-firefox.xpi"), "xpi " + version);
        if (portable)
        {
            File.WriteAllText(Path.Combine(folder, "portable.flag"), "");
            Directory.CreateDirectory(Path.Combine(folder, "Data"));
            File.WriteAllText(Path.Combine(folder, "Data", "downloads.db"), "the user's list");
        }
        return folder;
    }

    private string Downloads => Path.Combine(_dir, "updates");

    private int _releases;

    /// <summary>A GitHub release of <paramref name="version"/> with Baton.exe and, unless left out, the package and its assets.</summary>
    private string Release(Version version, byte[]? package, string? signature = null, string? sha = null, bool withSig = true)
    {
        var name = ExtensionPackage.NameFor(version);
        var tag = $"r{++_releases}-v{version.ToString(3)}"; // each release with its own files on the server
        var assets = new List<string>();
        if (package is not null)
        {
            assets.Add(Asset(name, _server.Add(tag + "-" + name, new ServedFile { Content = package })));
            assets.Add(Asset(name + ".sha256", Serve(tag + "-" + name + ".sha256", $"{sha ?? Sha(package)}  {name}\n")));
            if (withSig)
                assets.Add(Asset(name + ".sig", Serve(tag + "-" + name + ".sig", signature ?? Sign(version, name, package, Signer))));
        }
        return Serve(tag + "-release.json", $$"""{ "tag_name": "v{{version.ToString(3)}}", "assets": [ {{string.Join(", ", assets)}} ] }""");
    }

    private static string Asset(string name, string url) => $$"""{ "name": "{{name}}", "browser_download_url": "{{url}}" }""";

    private string Serve(string name, string text) => _server.Add(name, new ServedFile { Content = Encoding.UTF8.GetBytes(text), ContentType = "text/plain" });

    private async Task<ExtensionAsset?> AssetOf(string release) => await UpdateChecker.ExtensionOfAsync(_http, release, New, default);

    [Fact]
    public async Task SignedPackage_ReplacesFolderAndFirefoxPackage_KeepingTheOldOnes()
    {
        var folder = ProgramFolder("1.3.2");
        var asset = await AssetOf(Release(New, Package("1.3.3")));
        Assert.NotNull(asset);
        Assert.Equal("baton-extension-1.3.3.zip", asset!.Name);

        await ExtensionPackage.PrepareAsync(_http, asset, New, folder, Downloads, default, Keys);
        Assert.Equal(Old, ExtensionPackage.VersionIn(folder)); // nothing changes before the swap
        ExtensionPackage.Swap(folder);

        Assert.Equal(New, ExtensionPackage.VersionIn(folder));
        Assert.False(File.Exists(Path.Combine(folder, "extension", "old-only.js"))); // the folder is replaced, not merged
        Assert.Equal("xpi 1.3.3", File.ReadAllText(Path.Combine(folder, "baton-firefox.xpi")));
        Assert.Equal(Old, ExtensionPackage.OldVersion(folder));
        Assert.Equal("xpi 1.3.2", File.ReadAllText(Path.Combine(folder, "baton-firefox.xpi.old")));
        Assert.False(Directory.Exists(Path.Combine(folder, ExtensionPackage.StagingName)));
        Assert.Empty(Directory.GetFiles(Downloads)); // the downloaded package is not kept

        // "Restore previous version" back to 1.3.2: the extension of 1.3.2 comes back with it.
        Assert.False(ExtensionPackage.Restore(folder, new Version(1, 3, 1))); // an ".old" of another version stays
        Assert.True(ExtensionPackage.Restore(folder, Old));
        Assert.Equal(Old, ExtensionPackage.VersionIn(folder));
        Assert.True(File.Exists(Path.Combine(folder, "extension", "old-only.js")));
        Assert.Equal("xpi 1.3.2", File.ReadAllText(Path.Combine(folder, "baton-firefox.xpi")));
        Assert.False(Directory.Exists(Path.Combine(folder, ExtensionPackage.BackupName)));
    }

    [Fact]
    public async Task UpdateCheck_ListsThePackage_AndAReleaseWithoutOneStillUpdatesTheProgram()
    {
        var exe = TestFileServer.RandomBytes(4096, 3);
        exe[0] = (byte)'M';
        exe[1] = (byte)'Z';
        var package = Package("1.3.3");
        string Release(bool withPackage)
        {
            var assets = new List<string>
            {
                Asset("Baton.exe", _server.Add($"p{withPackage}-Baton.exe", new ServedFile { Content = exe })),
                Asset("Baton.exe.sha256", Serve($"p{withPackage}-Baton.exe.sha256", $"{Sha(exe)}  Baton.exe\n")),
                Asset("Baton.exe.sig", Serve($"p{withPackage}-Baton.exe.sig", Sign(New, "Baton.exe", exe, Signer))),
            };
            if (withPackage)
            {
                assets.Add(Asset("baton-extension-1.3.3.zip", _server.Add("baton-extension-1.3.3.zip", new ServedFile { Content = package })));
                assets.Add(Asset("baton-extension-1.3.3.zip.sha256", Serve("baton-extension-1.3.3.zip.sha256", $"{Sha(package)}  baton-extension-1.3.3.zip\n")));
                assets.Add(Asset("baton-extension-1.3.3.zip.sig", Serve("baton-extension-1.3.3.zip.sig", Sign(New, "baton-extension-1.3.3.zip", package, Signer))));
            }
            return Serve($"p{withPackage}-release.json", $$"""{ "tag_name": "v1.3.3", "assets": [ {{string.Join(", ", assets)}} ] }""");
        }

        var update = await UpdateChecker.CheckAsync(_http, Release(true), Old, default, Keys);
        Assert.Equal(Sha(package), update!.Extension!.Sha256);
        Assert.Contains(KeyOf(Signer).Id, update.Extension.Signatures);

        var without = await UpdateChecker.CheckAsync(_http, Release(false), Old, default, Keys);
        Assert.NotNull(without); // the program is still offered
        Assert.Null(without!.Extension);
        var folder = ProgramFolder("1.3.2");
        var ex = await Assert.ThrowsAsync<UpdateException>(() => ExtensionPackage.PrepareAsync(_http, without.Extension, New, folder, Downloads, default, Keys));
        Assert.Contains("baton-extension-1.3.3.zip", ex.Message);
        Assert.Equal(Old, ExtensionPackage.VersionIn(folder));
    }

    [Fact]
    public async Task MissingOrWrongSignatureOrHash_LeavesTheExtensionAsItIs()
    {
        var package = Package("1.3.3");
        var name = ExtensionPackage.NameFor(New);
        string[] refused =
        [
            Release(New, package, withSig: false),
            Release(New, package, signature: Sign(New, name, package, Stranger)),
            Release(New, package, signature: Sign(new Version(1, 3, 4), name, package, Signer)), // signed for another version
            Release(New, package, sha: new string('0', 64)), // a hash the signature does not cover
        ];
        foreach (var release in refused)
        {
            var folder = ProgramFolder("1.3.2");
            await Assert.ThrowsAsync<UpdateException>(() => ExtensionPackage.RepairAsync(_http, release, New, folder, Downloads, default, Keys));
            Assert.Equal(Old, ExtensionPackage.VersionIn(folder));
            Assert.False(Directory.Exists(Path.Combine(folder, ExtensionPackage.StagingName)));
            Assert.False(Directory.Exists(Path.Combine(folder, ExtensionPackage.BackupName)));
        }

        // A signed hash, but the server sends other bytes: refused after the download, and the file is deleted.
        var other = Package("1.3.3", zip => Entry(zip, "extension/extra.js", "tampered"));
        var tampered = new ExtensionAsset(name, new Uri(_server.Add("tampered-" + name, new ServedFile { Content = other })), Sha(package),
            Sign(New, name, package, Signer));
        var target = ProgramFolder("1.3.2");
        var mismatch = await Assert.ThrowsAsync<UpdateException>(() => ExtensionPackage.PrepareAsync(_http, tampered, New, target, Downloads, default, Keys));
        Assert.Contains("SHA-256", mismatch.Message);
        Assert.Empty(Directory.GetFiles(Downloads));
        Assert.Equal(Old, ExtensionPackage.VersionIn(target));
    }

    [Fact]
    public async Task PackageOfAnotherVersion_IsRefused()
    {
        var folder = ProgramFolder("1.3.2");
        var ex = await Assert.ThrowsAsync<UpdateException>(() => ExtensionPackage.RepairAsync(_http, Release(New, Package("1.3.2")), New, folder, Downloads, default, Keys));
        Assert.Contains("1.3.2", ex.Message);
        Assert.Equal(Old, ExtensionPackage.VersionIn(folder));
        Assert.False(Directory.Exists(Path.Combine(folder, ExtensionPackage.StagingName)));
    }

    public static TheoryData<string> HostileEntries =>
    [
        "../outside.txt",
        "extension/../../outside.txt",
        "extension\\..\\..\\outside.txt",
        "C:/Windows/outside.txt",
        "/etc/outside",
        "extension/C:outside",
        "Baton.exe", // only the extension may come from the package
        "evil.js",
        "extension/../Baton.exe",
        "extension/NUL",
        "extension/com1.js",
        "extension/background.js.", // Windows would drop the dot: the same file as background.js
        "extension/BACKGROUND.JS", // listed twice once case is ignored, as Windows does
        "extension/file.js:stream",
    ];

    [Theory]
    [MemberData(nameof(HostileEntries))]
    public void HostileZip_IsRefused_AndWritesNothingOutside(string entry)
    {
        var folder = ProgramFolder("1.3.2");
        var package = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(package, Package("1.3.3", zip => Entry(zip, entry, "x")));
        Assert.Throws<UpdateException>(() => ExtensionPackage.Stage(package, folder, New));
        Assert.False(File.Exists(Path.Combine(_dir, "outside.txt")));
        Assert.Equal("program", File.ReadAllText(Path.Combine(folder, "Baton.exe")));
        Assert.False(Directory.Exists(Path.Combine(folder, ExtensionPackage.StagingName)));
    }

    [Fact]
    public void SymbolicLinkInTheZip_IsRefused()
    {
        var folder = ProgramFolder("1.3.2");
        var package = Path.Combine(_dir, "link.zip");
        File.WriteAllBytes(package, Package("1.3.3", zip =>
        {
            var link = zip.CreateEntry("extension/link.js");
            link.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); // Unix mode lrwxrwxrwx
            using var s = link.Open();
            s.Write(Encoding.UTF8.GetBytes("../../Baton.exe"));
        }));
        var ex = Assert.Throws<UpdateException>(() => ExtensionPackage.Stage(package, folder, New));
        Assert.Contains("link.js", ex.Message);
    }

    [Fact]
    public void ZipBomb_StopsAtTheLimit_WhateverItsDirectorySays()
    {
        var folder = ProgramFolder("1.3.2");
        var package = Path.Combine(_dir, "bomb.zip");
        File.WriteAllBytes(package, Package("1.3.3", zip =>
        {
            using var s = zip.CreateEntry("extension/zeros.bin", CompressionLevel.SmallestSize).Open();
            var chunk = new byte[1 << 20];
            for (var i = 0; i <= ExtensionPackage.MaxUnpackedBytes / chunk.Length; i++)
                s.Write(chunk);
        }));
        Assert.True(new FileInfo(package).Length < 2 * 1024 * 1024); // small to download, huge once unpacked
        var ex = Assert.Throws<UpdateException>(() => ExtensionPackage.Stage(package, folder, New));
        Assert.Contains("large", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(folder, ExtensionPackage.StagingName)));

        File.WriteAllBytes(package, Package("1.3.3", zip =>
        {
            for (var i = 0; i < ExtensionPackage.MaxEntries; i++)
                zip.CreateEntry($"extension/f{i}.js");
        }));
        Assert.Throws<UpdateException>(() => ExtensionPackage.Stage(package, folder, New));
        File.WriteAllText(package, "not a zip");
        Assert.Throws<UpdateException>(() => ExtensionPackage.Stage(package, folder, New));
    }

    [Fact]
    public async Task FolderThatCannotBeWritten_IsReported_NotThrownAsAnythingElse()
    {
        var folder = ProgramFolder("1.3.2");
        var release = Release(New, Package("1.3.3"));
        var me = WindowsIdentity.GetCurrent().User!;
        var info = new DirectoryInfo(folder);
        var deny = new FileSystemAccessRule(me, FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var ex = await Assert.ThrowsAsync<UpdateException>(() => ExtensionPackage.RepairAsync(_http, release, New, folder, Downloads, default, Keys));
            Assert.Contains(folder, ex.Message);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
        Assert.Equal(Old, ExtensionPackage.VersionIn(folder));
    }

    [Fact]
    public async Task RepairAfterAnUpdateBy132_InstallsThePackageOfItsOwnVersion_InstalledAndPortable()
    {
        var release = Release(New, Package("1.3.3"));
        foreach (var portable in new[] { false, true })
        {
            var folder = ProgramFolder("1.3.2", portable);
            Assert.True(await ExtensionPackage.RepairAsync(_http, release, New, folder, Downloads, default, Keys));
            Assert.Equal(New, ExtensionPackage.VersionIn(folder));
            Assert.Equal(Old, ExtensionPackage.OldVersion(folder));
            Assert.Equal("program", File.ReadAllText(Path.Combine(folder, "Baton.exe")));
            if (portable)
                Assert.Equal("the user's list", File.ReadAllText(Path.Combine(folder, "Data", "downloads.db")));
            // Up to date: nothing more is downloaded.
            Assert.False(await ExtensionPackage.RepairAsync(_http, "http://127.0.0.1:1/unreachable.json", New, folder, Downloads, default, Keys));
        }

        // No extension folder (a build without it), or one newer than the program: never touched, nothing contacted.
        var bare = Path.Combine(_dir, "bare");
        Directory.CreateDirectory(bare);
        Assert.False(await ExtensionPackage.RepairAsync(_http, "http://127.0.0.1:1/unreachable.json", New, bare, Downloads, default, Keys));
        Assert.False(Directory.Exists(Path.Combine(bare, BundledExtension.FolderName)));
        Assert.False(await ExtensionPackage.RepairAsync(_http, "http://127.0.0.1:1/unreachable.json", Old, ProgramFolder("1.3.3"), Downloads, default, Keys));
    }

    [Fact]
    public async Task ReleaseOfItsOwnVersion_FromGitHubLatestOrAManifest()
    {
        Assert.Equal("https://api.github.com/repos/AndreaAzzarello/baton/releases/tags/v1.3.3",
            UpdateChecker.ReleaseAddressFor(new Uri("https://api.github.com/repos/AndreaAzzarello/baton/releases/latest"), New).AbsoluteUri);
        Assert.Equal("https://example.org/latest.json", UpdateChecker.ReleaseAddressFor(new Uri("https://example.org/latest.json"), New).AbsoluteUri);

        // Another version at the address (a manifest always describes the latest): no package of this version there.
        Assert.Null(await UpdateChecker.ExtensionOfAsync(_http, Release(new Version(1, 3, 4), Package("1.3.4")), New, default));

        var package = Package("1.3.3");
        var zipUrl = _server.Add("m-baton-extension-1.3.3.zip", new ServedFile { Content = package });
        var manifest = Serve("m-latest.json", $$"""
            { "version": "1.3.3", "url": "Baton.exe", "sha256": "{{new string('a', 64)}}",
              "extension": { "url": "{{zipUrl}}", "sha256": "{{Sha(package)}}", "signature": {{System.Text.Json.JsonSerializer.Serialize(Sign(New, "baton-extension-1.3.3.zip", package, Signer))}} } }
            """);
        var folder = ProgramFolder("1.3.2");
        Assert.True(await ExtensionPackage.RepairAsync(_http, manifest, New, folder, Downloads, default, Keys));
        Assert.Equal(New, ExtensionPackage.VersionIn(folder));
    }

    [Fact]
    public void RestoreWithAFileInUse_LeavesTheCurrentExtensionWhole()
    {
        var folder = ProgramFolder("1.3.2");
        var package = Path.Combine(_dir, "restore.zip");
        File.WriteAllBytes(package, Package("1.3.3"));
        ExtensionPackage.Stage(package, folder, New);
        ExtensionPackage.Swap(folder);
        // The ".old" folder is held by another program (an indexer, an antivirus): it cannot come back now.
        using (File.Open(Path.Combine(folder, ExtensionPackage.BackupName, "old-only.js"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<UpdateException>(() => ExtensionPackage.Restore(folder, Old));
        }
        Assert.Equal(New, ExtensionPackage.VersionIn(folder)); // the current one is still complete
        Assert.True(File.Exists(Path.Combine(folder, "extension", "background.js")));
        Assert.True(ExtensionPackage.Restore(folder, Old)); // and it works once the file is free
        Assert.Equal(Old, ExtensionPackage.VersionIn(folder));
        Assert.False(Directory.Exists(Path.Combine(folder, "extension.failed")));
    }

    [Fact]
    public async Task ReleaseThatIsNotAnObject_IsNoRelease_NotACrash()
    {
        foreach (var text in new[] { "[1, 2]", "42", "\"text\"", "{ bad" })
        {
            var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.ExtensionOfAsync(_http, Serve($"bad{text.Length}.json", text), New, default));
            Assert.Equal(Baton.Core.Resources.Strings.UpdateErrorNoRelease, ex.Message);
            await Assert.ThrowsAsync<UpdateException>(() => UpdateChecker.CheckAsync(_http, Serve($"bad{text.Length}b.json", text), Old, default, Keys));
        }
    }

    [Fact]
    public void SwapFailingHalfway_PutsThePreviousExtensionBack()
    {
        var folder = ProgramFolder("1.3.2");
        var package = Path.Combine(_dir, "ok.zip");
        File.WriteAllBytes(package, Package("1.3.3"));
        ExtensionPackage.Stage(package, folder, New);
        // The Firefox package is held open by another program: it cannot be renamed.
        using (File.Open(Path.Combine(folder, "baton-firefox.xpi"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<UpdateException>(() => ExtensionPackage.Swap(folder));
        Assert.Equal(Old, ExtensionPackage.VersionIn(folder));
        Assert.Equal("xpi 1.3.2", File.ReadAllText(Path.Combine(folder, "baton-firefox.xpi")));
        Assert.True(File.Exists(Path.Combine(folder, "extension", "old-only.js")));
    }
}
