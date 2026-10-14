using Microsoft.Win32;

namespace DownloadManager.Core.Platform;

/// <summary>
/// The parts of HKEY_CURRENT_USER the app writes to (start with Windows, browser hosts, notifications).
/// Injected everywhere, so tests and test runs of the app use an in-memory copy and never touch the real registry.
/// </summary>
public interface IUserRegistry
{
    string? GetString(string keyPath, string? valueName);

    void SetString(string keyPath, string? valueName, string value);

    void DeleteValue(string keyPath, string valueName);

    IReadOnlyList<string> GetValueNames(string keyPath);
}

/// <summary>The real HKEY_CURRENT_USER.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsUserRegistry : IUserRegistry
{
    /// <summary>
    /// Set by the test assembly: any access to the real registry then throws, so a test that would touch a real
    /// key fails instead of changing the user's system.
    /// </summary>
    public static bool Forbidden { get; set; }

    public string? GetString(string keyPath, string? valueName)
    {
        Guard();
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName ?? "") as string;
    }

    public void SetString(string keyPath, string? valueName, string value)
    {
        Guard();
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName ?? "", value);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        Guard();
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public IReadOnlyList<string> GetValueNames(string keyPath)
    {
        Guard();
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValueNames() ?? [];
    }

    private static void Guard()
    {
        if (Forbidden)
            throw new InvalidOperationException("The real registry must not be used here (tests or a sandboxed test run).");
    }
}

/// <summary>A registry kept in memory: for tests, and for the app started with DOWNLOADMANAGER_DATA.</summary>
public sealed class MemoryUserRegistry : IUserRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public string? GetString(string keyPath, string? valueName)
    {
        lock (_lock)
            return _keys.TryGetValue(keyPath, out var values) && values.TryGetValue(valueName ?? "", out var value) ? value : null;
    }

    public void SetString(string keyPath, string? valueName, string value)
    {
        lock (_lock)
        {
            if (!_keys.TryGetValue(keyPath, out var values))
                _keys[keyPath] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            values[valueName ?? ""] = value;
        }
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        lock (_lock)
        {
            if (_keys.TryGetValue(keyPath, out var values))
                values.Remove(valueName);
        }
    }

    public IReadOnlyList<string> GetValueNames(string keyPath)
    {
        lock (_lock)
            return _keys.TryGetValue(keyPath, out var values) ? values.Keys.ToList() : [];
    }

    public IReadOnlyList<string> Keys
    {
        get { lock (_lock) return _keys.Keys.ToList(); }
    }
}
