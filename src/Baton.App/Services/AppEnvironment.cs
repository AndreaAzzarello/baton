using System.IO;
using System.Security.Cryptography;
using System.Text;
using Baton.Core.Integration;
using Baton.Core.Platform;
using Baton.Core.Setup;

namespace Baton.App.Services;

/// <summary>
/// Where the app keeps its data and what it may touch on the system. With BATON_DATA (used for test runs) the app is
/// sandboxed: the registry is kept in memory and the single-instance pipe is separate, so a test run never changes the
/// start-with-Windows entry, the browser registration or the notifications of the real app.
/// DOWNLOADMANAGER_DATA, the variable of the versions called DownloadManager, only serves test runs: alone it sandboxes
/// the app like BATON_DATA (a script written for the old name never reaches the real data); next to BATON_DATA it names
/// the old data folder that the migration copies.
/// </summary>
public static class AppEnvironment
{
    // Normalized, so "C:/x/data" and "C:\x\data\" name the same sandbox (same pipe, same single instance).
    private static string? Folder(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(value))
        : null;

    private static readonly string? CustomData = Folder("BATON_DATA") ?? Folder("DOWNLOADMANAGER_DATA");

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static bool IsSandboxed => CustomData is not null;

    /// <summary>The data folder of DownloadManager (up to 1.1), copied at the first start.</summary>
    public static string? LegacyDataDirectory { get; } = IsSandboxed
        ? Folder("BATON_DATA") is null ? null : Folder("DOWNLOADMANAGER_DATA")
        : Path.Combine(LocalAppData, "DownloadManager");

    private static readonly string BatonDirectory = CustomData ?? Path.Combine(LocalAppData, "Baton");

    /// <summary>DownloadManager's data is there and has not been copied yet.</summary>
    private static bool NeedsMigration =>
        !File.Exists(Path.Combine(BatonDirectory, LegacyData.DatabaseName))
        && LegacyDataDirectory is { } legacy && File.Exists(Path.Combine(legacy, LegacyData.DatabaseName));

    /// <summary>
    /// Baton's folder once it holds the data; until the copy has succeeded, DownloadManager's. Every process of the app
    /// (the app, the browser host, a notification button) decides the same way.
    /// </summary>
    public static string DataDirectory { get; private set; } = NeedsMigration ? LegacyDataDirectory! : BatonDirectory;

    public static IUserRegistry Registry { get; } = IsSandboxed ? new MemoryUserRegistry() : new WindowsUserRegistry();

    public static SystemRegistration Registration { get; } = new(Registry, Environment.ProcessPath ?? "Baton.exe");

    /// <summary>The browser host always talks to the real app; a sandboxed run listens on a pipe of its own.</summary>
    public static string PipeName { get; } = IsSandboxed
        ? AppChannel.DefaultPipeName + "." + Hash(BatonDirectory)
        : AppChannel.DefaultPipeName;

    private static string Hash(string folder) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant())))[..8];

    /// <summary>
    /// DownloadManager (1.1) is running on the data still to be copied: copying now would leave both apps working on the
    /// same downloads. Its single instance is named like Baton's, with its old name (and its data folder in a sandbox).
    /// </summary>
    public static bool LegacyAppRunning
    {
        get
        {
            if (!NeedsMigration)
                return false;
            var name = @"Local\DownloadManager." + AppChannel.DefaultPipeName["Baton.".Length..]
                + (IsSandboxed ? "." + Hash(LegacyDataDirectory!) : "");
            if (!Mutex.TryOpenExisting(name, out var running))
                return false;
            running.Dispose();
            return true;
        }
    }

    /// <summary>
    /// Copies DownloadManager's data the first time (see <see cref="LegacyData"/>). When that fails the old folder stays in
    /// use and the error is returned, to be shown; the next start tries again.
    /// </summary>
    public static string? MigrateLegacyData()
    {
        var (outcome, error) = LegacyData.Migrate(LegacyDataDirectory, BatonDirectory);
        if (outcome == MigrationOutcome.Migrated)
            DataDirectory = BatonDirectory;
        return outcome == MigrationOutcome.Failed ? error : null;
    }
}
