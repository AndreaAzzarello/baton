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

    /// <summary>Back to DownloadManager (1.1): if Baton started with Windows, the old program does again, never otherwise.</summary>
    public void RestoreLegacyStartEntry(string legacyExe)
    {
        if (registry.GetString(RunKey, RunValue) is null)
            return;
        registry.DeleteValue(RunKey, RunValue);
        registry.SetString(RunKey, LegacyRunValue, $"\"{legacyExe}\" {TrayArgument}");
    }
}
