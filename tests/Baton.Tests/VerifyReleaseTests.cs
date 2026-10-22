using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Baton.Core.Updates;

namespace Baton.Tests;

/// <summary>
/// tools\verify-release.ps1, as a user runs it on downloaded files: SHA-256 and release-key signature as before, and the
/// Authenticode signature when there is one (valid, missing, or broken by a change to the file).
/// </summary>
public sealed class VerifyReleaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", "verify-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public VerifyReleaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Baton.slnx")))
            dir = dir.Parent;
        return dir.FullName;
    }

    /// <summary>The file as a release asset: its .sha256 and its .sig by the test key.</summary>
    private string Release(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        File.WriteAllText(path + ".sha256", $"{sha}  {name}\n");
        var q = _key.ExportParameters(false).Q;
        var key = ReleaseKey.FromBase64(Convert.ToBase64String([.. q.X!, .. q.Y!]))!;
        File.WriteAllText(Path.Combine(_dir, "keys.txt"), Convert.ToBase64String([.. q.X!, .. q.Y!]) + "\n");
        var signature = _key.SignData(Encoding.UTF8.GetBytes(UpdateSignature.Statement(new Version(1, 3, 0), name, sha)), HashAlgorithmName.SHA256);
        File.WriteAllText(path + ".sig", $"{key.Id} {Convert.ToBase64String(signature)}\n");
        return path;
    }

    private (int Code, string Output) Verify(string exe, bool requireAuthenticode = false)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Started from PowerShell 7 (as on CI), Windows PowerShell would inherit its module path and miss Get-FileHash.
            Environment = { ["PSModulePath"] = null },
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Root(), "tools", "verify-release.ps1"),
                     "-Exe", exe, "-Version", "1.3.0", "-KeyList", Path.Combine(_dir, "keys.txt") })
            info.ArgumentList.Add(arg);
        if (requireAuthenticode)
            info.ArgumentList.Add("-RequireAuthenticode");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    // Signed by Microsoft with an embedded Authenticode signature, on every machine that runs these tests.
    private static byte[] SignedProgram() => File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "dotnet.exe") is var p && File.Exists(p)
        ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));

    [Fact]
    public void AuthenticodeSignedRelease_IsAccepted_AndItsSignerShown()
    {
        var (code, output) = Verify(Release("Baton-Setup-1.3.0.exe", SignedProgram()), requireAuthenticode: true);
        Assert.True(code == 0, output);
        Assert.Contains("Authenticode: CN=", output);
        Assert.Contains("O=Microsoft Corporation", output);
    }

    [Fact]
    public void ReleaseWithoutAuthenticode_PassesUnlessItIsRequired()
    {
        var exe = Release("Baton.exe", File.ReadAllBytes(typeof(VerifyReleaseTests).Assembly.Location)); // a program without a signature
        var (code, output) = Verify(exe);
        Assert.True(code == 0, output);
        Assert.Contains("no Authenticode signature", output);
        Assert.NotEqual(0, Verify(exe, requireAuthenticode: true).Code);
    }

    [Fact]
    public void ChangedAfterTheAuthenticodeSignature_IsRefused_EvenWithAValidReleaseKeySignature()
    {
        // What a tampered download looks like if someone also re-signed it with a stolen release key.
        var content = SignedProgram();
        content[0x200] ^= 0xFF;
        var (code, output) = Verify(Release("Baton.exe", content));
        Assert.NotEqual(0, code);
        Assert.Contains("not valid", output);
    }
}
