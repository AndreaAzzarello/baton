using System.IO;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Core.Integration;
using DownloadManager.Core.Platform;

namespace DownloadManager.App.Services;

/// <summary>
/// Where the app keeps its data and what it may touch on the system. With DOWNLOADMANAGER_DATA (used for test runs)
/// the app is sandboxed: the registry is kept in memory and the single-instance pipe is separate, so a test run
/// never changes the start-with-Windows entry, the browser registration or the notifications of the real app.
/// </summary>
public static class AppEnvironment
{
    private static readonly string? CustomData = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA") is { Length: > 0 } custom ? custom : null;

    public static bool IsSandboxed => CustomData is not null;

    public static string DataDirectory { get; } =
        CustomData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager");

    public static IUserRegistry Registry { get; } = IsSandboxed ? new MemoryUserRegistry() : new WindowsUserRegistry();

    public static SystemRegistration Registration { get; } = new(Registry, Environment.ProcessPath ?? "DownloadManager.exe");

    /// <summary>The browser host always talks to the real app; a sandboxed run listens on a pipe of its own.</summary>
    public static string PipeName { get; } = IsSandboxed
        ? AppChannel.DefaultPipeName + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToLowerInvariant())))[..8]
        : AppChannel.DefaultPipeName;
}
