using Baton.Core.Integration;

namespace Baton.Core.Platform;

/// <summary>
/// The registry of the portable version: it reads, but writes nothing, except what the user asks for: the browsers' native
/// messaging host (<see cref="AllowBrowserHost"/>) and its own start-with-Windows entry (<see cref="AllowStartEntry"/>).
/// Every other write (notifications, link scheme, the clean-up of DownloadManager's entries) is dropped and counted.
/// </summary>
public sealed class PortableUserRegistry(IUserRegistry inner) : IUserRegistry
{
    private int _refused;

    /// <summary>The user agreed to register this copy as the browsers' native messaging host.</summary>
    public bool AllowBrowserHost { get; set; }

    /// <summary>The user is turning start with Windows on or off: the portable copy's own "Run" value may be written meanwhile.</summary>
    public bool AllowStartEntry { get; set; }

    /// <summary>Writes dropped so far (for tests and the log).</summary>
    public int Refused => _refused;

    public string? GetString(string keyPath, string? valueName) => inner.GetString(keyPath, valueName);

    public IReadOnlyList<string> GetValueNames(string keyPath) => inner.GetValueNames(keyPath);

    public void SetString(string keyPath, string? valueName, string value)
    {
        if (Allowed(keyPath, valueName))
            inner.SetString(keyPath, valueName, value);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        if (Allowed(keyPath, valueName))
            inner.DeleteValue(keyPath, valueName);
    }

    public void DeleteKey(string keyPath)
    {
        if (Allowed(keyPath, valueName: null))
            inner.DeleteKey(keyPath);
    }

    private bool Allowed(string keyPath, string? valueName)
    {
        var allowed = AllowBrowserHost && SystemRegistration.BrowserHostKeys.Append(SystemRegistration.FirefoxHostKey)
                          .Any(k => keyPath.Equals($@"{k}\{NativeHost.Name}", StringComparison.OrdinalIgnoreCase))
                      || AllowStartEntry && keyPath.Equals(SystemRegistration.RunKey, StringComparison.OrdinalIgnoreCase)
                                         && valueName == SystemRegistration.PortableRunValue;
        if (!allowed)
            Interlocked.Increment(ref _refused);
        return allowed;
    }
}
