using System.Diagnostics;
using System.IO.Compression;

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
        var exe = Directory.GetFiles(Path.Combine(root, "src", "Baton.App", "bin"), "Baton.exe", SearchOption.AllDirectories)
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
