using System.Text.Json;
using DownloadManager.Core.Integration;

namespace DownloadManager.Core.Platform;

/// <summary>
/// Everything the app registers for the current user, written through <see cref="IUserRegistry"/>:
/// the browsers' native messaging host, the notification identity and link scheme, and the start-with-Windows entry.
/// </summary>
public sealed class SystemRegistration(IUserRegistry registry, string executable)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "DownloadManager";
    public const string TrayArgument = "--tray";
    public const string AppUserModelId = "DownloadManager.App";
    public const string LinkScheme = NotificationLinks.Scheme;

    /// <summary>Per-user registry locations where Chromium-based browsers look up native messaging hosts (Opera reads Chrome's).</summary>
    public static readonly string[] BrowserHostKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
    ];

    public string Executable => executable;

    /// <summary>Writes the host manifest next to the data and points every browser at it.</summary>
    public void RegisterBrowserHost(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var manifestPath = Path.Combine(dataDirectory, NativeHost.Name + ".json");
        var manifest = new
        {
            name = NativeHost.Name,
            description = "DownloadManager",
            path = executable,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{NativeHost.ExtensionId}/" },
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var key in BrowserHostKeys)
            registry.SetString($@"{key}\{NativeHost.Name}", null, manifestPath);
    }

    /// <summary>Name and icon shown on notifications, and the "downloadmanager:" scheme behind their buttons.</summary>
    public void RegisterNotifications(string iconPath)
    {
        var identity = $@"Software\Classes\AppUserModelId\{AppUserModelId}";
        registry.SetString(identity, "DisplayName", "DownloadManager");
        registry.SetString(identity, "IconUri", iconPath);

        var scheme = $@"Software\Classes\{LinkScheme}";
        registry.SetString(scheme, null, "URL:DownloadManager");
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
            if (registry.GetString(RunKey, name) is { } other && other.Contains("DownloadManager.exe", StringComparison.OrdinalIgnoreCase))
                registry.DeleteValue(RunKey, name);
        }
        if (registry.GetString(RunKey, RunValue) is { } command && !command.Contains(executable, StringComparison.OrdinalIgnoreCase))
            SetStartWithWindows(true);
    }
}
