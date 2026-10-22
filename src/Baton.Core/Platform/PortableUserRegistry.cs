using Baton.Core.Integration;

namespace Baton.Core.Platform;

/// <summary>
/// The registry of the portable version: it reads, but writes nothing, except the browsers' native messaging host once the
/// user agreed (<see cref="AllowBrowserHost"/>). Every other write (start with Windows, notifications, link scheme, the
/// clean-up of DownloadManager's entries) is dropped and counted, so the portable copy leaves no trace on the PC.
/// </summary>
public sealed class PortableUserRegistry(IUserRegistry inner) : IUserRegistry
{
    private int _refused;

    /// <summary>The user agreed to register this copy as the browsers' native messaging host.</summary>
    public bool AllowBrowserHost { get; set; }

    /// <summary>Writes dropped so far (for tests and the log).</summary>
    public int Refused => _refused;

    public string? GetString(string keyPath, string? valueName) => inner.GetString(keyPath, valueName);

    public IReadOnlyList<string> GetValueNames(string keyPath) => inner.GetValueNames(keyPath);

    public void SetString(string keyPath, string? valueName, string value)
    {
        if (Allowed(keyPath))
            inner.SetString(keyPath, valueName, value);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        if (Allowed(keyPath))
            inner.DeleteValue(keyPath, valueName);
    }

    public void DeleteKey(string keyPath)
    {
        if (Allowed(keyPath))
            inner.DeleteKey(keyPath);
    }

    private bool Allowed(string keyPath)
    {
        var host = AllowBrowserHost && SystemRegistration.BrowserHostKeys.Append(SystemRegistration.FirefoxHostKey)
            .Any(k => keyPath.Equals($@"{k}\{NativeHost.Name}", StringComparison.OrdinalIgnoreCase));
        if (!host)
            Interlocked.Increment(ref _refused);
        return host;
    }
}
