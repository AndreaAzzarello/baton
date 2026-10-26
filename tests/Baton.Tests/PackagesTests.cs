using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Baton.Tests;

/// <summary>
/// The packages carry the browser extension next to Baton.exe: tools\build-packages.ps1 really builds the portable zip
/// from the compiled Baton.exe, and the installer script installs the same files in {app}.
/// </summary>
public sealed class PackagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", "packages-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Baton.slnx")))
            dir = dir.Parent;
        return dir.FullName;
    }

    [Fact]
    public async Task ThePortableZip_HasTheExtensionFolder_AndTheFirefoxPackage()
    {
        var root = Root();
        // The newest build of the project's version: a Release build left by an earlier version must not be picked.
        var projectVersion = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "Baton.App", "Baton.App.csproj")).Descendants("Version").First().Value;
        var exe = Directory.GetFiles(Path.Combine(root, "src", "Baton.App", "bin"), "Baton.exe", SearchOption.AllDirectories)
            .Where(f => FileVersionInfo.GetVersionInfo(f).ProductVersion?.StartsWith(projectVersion + "+", StringComparison.Ordinal) == true)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { ["PSModulePath"] = null, ["SOURCE_DATE_EPOCH"] = "1767225600" },
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "tools", "build-packages.ps1"),
                     "-Source", Path.GetDirectoryName(exe)!, "-Out", _dir, "-SkipInstaller" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = await process.StandardError.ReadToEndAsync();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await output + errors);

        using var zip = ZipFile.OpenRead(Directory.GetFiles(_dir, "Baton-*-portable.zip").Single());
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("Baton.exe", names);
        Assert.Contains("portable.flag", names);
        Assert.Contains("baton-firefox.xpi", names);
        // Every file of the Chromium build, under "extension/" next to Baton.exe.
        var chromium = Path.Combine(root, "dist", "chromium");
        foreach (var file in Directory.GetFiles(chromium, "*", SearchOption.AllDirectories))
            Assert.Contains("extension/" + Path.GetRelativePath(chromium, file).Replace('\\', '/'), names);

        // The updater's package: the same extension files, and accepted as they are by the app (from 1.3.3).
        var package = Directory.GetFiles(_dir, "baton-extension-*.zip").Single();
        using (var extension = ZipFile.OpenRead(package))
            Assert.Equal(names.Where(n => n.StartsWith("extension/") || n == "baton-firefox.xpi").Order(), extension.Entries.Select(e => e.FullName).Order());
        var version = Core.Updates.UpdateChecker.ParseVersion(Path.GetFileNameWithoutExtension(package))!;
        var program = Path.Combine(_dir, "program");
        Directory.CreateDirectory(program);
        Core.Updates.ExtensionPackage.Stage(package, program, version);
        Core.Updates.ExtensionPackage.Swap(program);
        Assert.Equal(version, Core.Updates.ExtensionPackage.VersionIn(program));
        Assert.True(File.Exists(Path.Combine(program, "baton-firefox.xpi")));
    }

    [Fact]
    public async Task WingetManifests_UseSchema112_AndKeepTheItalianText_ScoopTheZipHash()
    {
        Directory.CreateDirectory(_dir);
        var setup = Path.Combine(_dir, "Baton-Setup-9.8.7.exe");
        var zip = Path.Combine(_dir, "Baton-9.8.7-portable.zip");
        File.WriteAllText(setup, "setup");
        File.WriteAllText(zip, "zip");
        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { ["PSModulePath"] = null },
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Root(), "tools", "make-manifests.ps1"),
                     "-Setup", setup, "-Zip", zip, "-Out", Path.Combine(_dir, "out"), "-ReleaseDate", "2026-10-22" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + errors);

        var folder = Path.Combine(_dir, "out", "winget", "manifests", "a", "AndreaAzzarello", "Baton", "9.8.7");
        var files = new Dictionary<string, string>
        {
            ["AndreaAzzarello.Baton.yaml"] = "version",
            ["AndreaAzzarello.Baton.installer.yaml"] = "installer",
            ["AndreaAzzarello.Baton.locale.en-US.yaml"] = "defaultLocale",
            ["AndreaAzzarello.Baton.locale.it-IT.yaml"] = "locale",
        };
        Assert.Equal(files.Keys.Order(), Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        foreach (var (name, type) in files)
        {
            var text = File.ReadAllText(Path.Combine(folder, name));
            Assert.Contains($"# yaml-language-server: $schema=https://aka.ms/winget-manifest.{type}.1.12.0.schema.json", text);
            Assert.Contains("ManifestVersion: 1.12.0", text);
            Assert.DoesNotContain("\r", text);
        }
        var installer = File.ReadAllText(Path.Combine(folder, "AndreaAzzarello.Baton.installer.yaml"));
        Assert.Contains("InstallerUrl: https://github.com/AndreaAzzarello/baton/releases/download/v9.8.7/Baton-Setup-9.8.7.exe", installer);
        Assert.Contains("InstallerSha256: " + Convert.ToHexString(SHA256.HashData("setup"u8)), installer);
        Assert.Contains("più segmenti", File.ReadAllText(Path.Combine(folder, "AndreaAzzarello.Baton.locale.it-IT.yaml")));
        var scoop = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "out", "scoop", "baton.json"))).RootElement;
        Assert.Equal("9.8.7", scoop.GetProperty("version").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("zip"u8)), scoop.GetProperty("architecture").GetProperty("64bit").GetProperty("hash").GetString());
    }

    [Fact]
    public void ScriptsWithAccentedText_HaveABom_SoWindowsPowerShellReadsThemAsUtf8()
    {
        // Without it PowerShell 5.1 reads them in the ANSI code page: "più" became "piÃ¹" in the winget manifests of 1.3.1.
        var root = Root();
        var broken = Directory.GetFiles(Path.Combine(root, "tools"), "*.ps1", SearchOption.AllDirectories)
            .Where(f => File.ReadAllBytes(f) is var b && b.Any(c => c > 127) && !b.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]))
            .Select(f => Path.GetRelativePath(root, f));
        Assert.Empty(broken);
    }

    [Fact]
    public void TheInstaller_PutsTheExtensionInTheProgramFolder_AndClearsTheOldOne()
    {
        var script = File.ReadAllLines(Path.Combine(Root(), "installer", "Baton.iss")).Select(l => l.Trim()).ToList();
        Assert.Contains("""Source: "{#ExtensionDir}\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs""", script);
        Assert.Contains("""Source: "{#FirefoxPackage}"; DestDir: "{app}"; DestName: "baton-firefox.xpi"; Flags: ignoreversion""", script);
        Assert.Contains(@"Type: filesandordirs; Name: ""{app}\extension""", script);
        // The names the app looks for (BundledExtension) are those the packages use.
        Assert.Equal("extension", Core.Setup.BundledExtension.FolderName);
        Assert.Equal("baton-firefox.xpi", Core.Setup.BundledExtension.FirefoxPackageName);
    }
}
