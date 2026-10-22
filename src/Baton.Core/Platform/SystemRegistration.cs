using System.Text.Json;
using Baton.Core.Integration;

namespace Baton.Core.Platform;

/// <summary>
/// Everything the app registers for the current user, written through <see cref="IUserRegistry"/>:
/// the browsers' native messaging host, the notification identity and link scheme, and the start-with-Windows entry.
/// </summary>
public sealed class SystemRegistration(IUserRegistry registry, string executable)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "Baton";
    public const string TrayArgument = "--tray";
    public const string AppUserModelId = "Baton.App";
    public const string LinkScheme = NotificationLinks.Scheme;

    // Up to 1.1 the app was called DownloadManager: what it registered under that name (see RemoveLegacy).
    public const string LegacyRunValue = "DownloadManager";
    public const string LegacyAppUserModelId = "DownloadManager.App";
    public const string LegacyLinkScheme = "downloadmanager";
    public const string LegacyHostName = "com.downloadmanager.host";
    public const string LegacyExeName = "DownloadManager.exe";

    /// <summary>Per-user registry locations where Chromium-based browsers look up native messaging hosts (Opera reads Chrome's).</summary>
    public static readonly string[] BrowserHostKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
    ];

    /// <summary>Where Firefox looks up native messaging hosts for the current user.</summary>
    public const string FirefoxHostKey = @"Software\Mozilla\NativeMessagingHosts";

    /// <summary>The installer's entry in "Installed apps" (Inno Setup's AppId followed by "_is1"; see installer\Baton.iss).</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{27469723-AE14-4420-B2B4-189B8BE709AB}_is1";

    public string Executable => executable;

    /// <summary>
    /// Writes the host manifests next to the data and points every browser at them. Firefox needs a manifest of
    /// its own: it lists extension IDs (allowed_extensions) instead of Chromium's origins.
    /// </summary>
    public void RegisterBrowserHost(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var manifestPath = Path.Combine(dataDirectory, NativeHost.Name + ".json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            name = NativeHost.Name,
            description = "Baton",
            path = executable,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{NativeHost.ExtensionId}/" },
        }, options));
        foreach (var key in BrowserHostKeys)
            registry.SetString($@"{key}\{NativeHost.Name}", null, manifestPath);

        var firefoxPath = Path.Combine(dataDirectory, NativeHost.Name + ".firefox.json");
        File.WriteAllText(firefoxPath, JsonSerializer.Serialize(new
        {
            name = NativeHost.Name,
            description = "Baton",
            path = executable,
            type = "stdio",
            allowed_extensions = new[] { NativeHost.FirefoxExtensionId },
        }, options));
        registry.SetString($@"{FirefoxHostKey}\{NativeHost.Name}", null, firefoxPath);
    }

    /// <summary>Name and icon shown on notifications, and the "baton:" scheme behind their buttons.</summary>
    public void RegisterNotifications(string iconPath)
    {
        var identity = $@"Software\Classes\AppUserModelId\{AppUserModelId}";
        registry.SetString(identity, "DisplayName", "Baton");
        registry.SetString(identity, "IconUri", iconPath);

        var scheme = $@"Software\Classes\{LinkScheme}";
        registry.SetString(scheme, null, "URL:Baton");
        registry.SetString(scheme, "URL Protocol", "");
        registry.SetString($@"{scheme}\shell\open\command", null, $"\"{executable}\" \"%1\"");
    }

    public bool StartsWithWindows =>
        registry.GetString(RunKey, RunValue) is { } command && command.Contains(executable, StringComparison.OrdinalIgnoreCase);

    public void SetStartWithWindows(bool enabled)
    {
        if (enabled)
            registry.SetString(RunKey, RunValue, $"\"{executable}\" {TrayArgument}");
        else
            registry.DeleteValue(RunKey, RunValue);
    }

    /// <summary>
    /// Keeps the "Run" key tidy: one entry at most, and if it points to another copy of the app (for example a
    /// build that was moved or deleted) it is moved to this executable. Never creates an entry the user did not ask for.
    /// </summary>
    public void RepairStartEntry()
    {
        foreach (var name in registry.GetValueNames(RunKey).Where(n => n != RunValue && n.StartsWith(RunValue, StringComparison.OrdinalIgnoreCase)))
        {
            if (registry.GetString(RunKey, name) is { } other && other.Contains("Baton.exe", StringComparison.OrdinalIgnoreCase))
                registry.DeleteValue(RunKey, name);
        }
        if (registry.GetString(RunKey, RunValue) is { } command && !command.Contains(executable, StringComparison.OrdinalIgnoreCase))
            SetStartWithWindows(true);
    }

    /// <summary>
    /// Removes what DownloadManager (up to 1.1) registered: its browser host, link scheme and notification identity. Its
    /// start-with-Windows entries are removed too, and if there was one, Baton's takes its place: never created otherwise.
    /// Only values that really point to DownloadManager.exe are touched.
    /// </summary>
    public void RemoveLegacy()
    {
        foreach (var key in BrowserHostKeys.Append(FirefoxHostKey))
            registry.DeleteKey($@"{key}\{LegacyHostName}");
        registry.DeleteKey($@"Software\Classes\{LegacyLinkScheme}");
        registry.DeleteKey($@"Software\Classes\AppUserModelId\{LegacyAppUserModelId}");

        var startedWithWindows = false;
        foreach (var name in registry.GetValueNames(RunKey).Where(n => n.StartsWith(LegacyRunValue, StringComparison.OrdinalIgnoreCase)))
        {
            if (registry.GetString(RunKey, name) is { } command && command.Contains(LegacyExeName, StringComparison.OrdinalIgnoreCase))
            {
                registry.DeleteValue(RunKey, name);
                startedWithWindows = true;
            }
        }
        if (startedWithWindows)
            SetStartWithWindows(true);
    }

    /// <summary>This executable is the one the installer put in place (and "Installed apps" lists).</summary>
    public bool IsInstalledCopy =>
        registry.GetString(UninstallKey, "InstallLocation") is { Length: > 0 } location
        && SamePath(location, Path.GetDirectoryName(executable) ?? "");

    /// <summary>After an update the program replaced itself: "Installed apps" shows the new version.</summary>
    public void UpdateInstalledVersion(string version)
    {
        if (IsInstalledCopy && registry.GetString(UninstallKey, "DisplayVersion") != version)
            registry.SetString(UninstallKey, "DisplayVersion", version);
    }

    /// <summary>The programs this user's registrations point to: link scheme, start with Windows and browser hosts.</summary>
    public IReadOnlyList<string> RegisteredPrograms()
    {
        var programs = new List<string?>
        {
            ProgramOf(registry.GetString($@"Software\Classes\{LinkScheme}\shell\open\command", null)),
            ProgramOf(registry.GetString(RunKey, RunValue)),
        };
        foreach (var key in BrowserHostKeys.Append(FirefoxHostKey))
            programs.Add(HostProgram($@"{key}\{NativeHost.Name}"));
        return programs.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Removes what points to this executable: the browsers' host (and its manifest files), and unless only the host is
    /// asked for, the link scheme with the notification identity and the start-with-Windows entry. Whatever points to
    /// another copy of the app stays: uninstalling one copy never breaks another.
    /// </summary>
    public void Unregister(bool browserHostOnly = false)
    {
        // All read first: the Chromium browsers share one manifest file, deleted only at the end.
        var ours = BrowserHostKeys.Append(FirefoxHostKey).Select(k => $@"{k}\{NativeHost.Name}")
            .Where(key => HostProgram(key) is { } program && SamePath(program, executable))
            .Select(key => (Key: key, Manifest: registry.GetString(key, null)!))
            .ToList();
        foreach (var (key, _) in ours)
            registry.DeleteKey(key);
        foreach (var manifest in ours.Select(o => o.Manifest).Distinct(StringComparer.OrdinalIgnoreCase))
            TryDelete(manifest);
        if (browserHostOnly)
            return;
        var scheme = $@"Software\Classes\{LinkScheme}";
        if (ProgramOf(registry.GetString($@"{scheme}\shell\open\command", null)) is { } linked && SamePath(linked, executable))
        {
            registry.DeleteKey(scheme);
            registry.DeleteKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
        }
        if (ProgramOf(registry.GetString(RunKey, RunValue)) is { } started && SamePath(started, executable))
            registry.DeleteValue(RunKey, RunValue);
    }

    /// <summary>The browsers' host points to another copy of the app that is still on the disk.</summary>
    public bool BrowserHostHeldByAnother =>
        BrowserHostKeys.Append(FirefoxHostKey).Select(k => HostProgram($@"{k}\{NativeHost.Name}"))
            .Any(program => program is not null && !SamePath(program, executable) && File.Exists(program));

    /// <summary>The program named by a host manifest ("path"), when the manifest can be read.</summary>
    private string? HostProgram(string key)
    {
        if (registry.GetString(key, null) is not { Length: > 0 } manifest)
            return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            return json.RootElement.TryGetProperty("path", out var path) ? path.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The program of a command line: "C:\x\Baton.exe" --tray gives C:\x\Baton.exe.</summary>
    public static string? ProgramOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Only a leftover file in the data folder.
        }
    }

    /// <summary>Back to DownloadManager (1.1): if Baton started with Windows, the old program does again, never otherwise.</summary>
    public void RestoreLegacyStartEntry(string legacyExe)
    {
        if (registry.GetString(RunKey, RunValue) is null)
            return;
        registry.DeleteValue(RunKey, RunValue);
        registry.SetString(RunKey, LegacyRunValue, $"\"{legacyExe}\" {TrayArgument}");
    }
}
