using System.Runtime.CompilerServices;
using Baton.Core.Integration;
using Baton.Core.Platform;
using Microsoft.Win32;

namespace Baton.Tests;

/// <summary>Runs before any test: from here on, touching the real registry throws and fails the test.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class TestIsolation
{
    [ModuleInitializer]
    internal static void ForbidRealRegistry() => WindowsUserRegistry.Forbidden = true;

    /// <summary>
    /// The engine's texts and formats in Italian, whatever the language of the machine (CI runs in English): many tests
    /// check messages as the Italian user reads them. Tests about languages pick their culture explicitly.
    /// </summary>
    [ModuleInitializer]
    internal static void ItalianTexts()
    {
        var italian = System.Globalization.CultureInfo.GetCultureInfo("it-IT");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = italian;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = italian;
        Baton.Core.Resources.Strings.Culture = italian;
    }
}

/// <summary>Tests never write to the real registry, and the app's registrations behave on a registry kept in memory.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class IsolationTests
{
    private const string Exe = @"C:\Apps\Baton\Baton.exe";

    [Fact]
    public void RealRegistry_IsForbiddenDuringTests()
    {
        var real = new WindowsUserRegistry();
        Assert.Throws<InvalidOperationException>(() => real.SetString(SystemRegistration.RunKey, "Baton-test", "x"));
        Assert.Throws<InvalidOperationException>(() => real.GetString(SystemRegistration.RunKey, SystemRegistration.RunValue));
        Assert.Throws<InvalidOperationException>(() => new SystemRegistration(real, Exe).SetStartWithWindows(true));
    }

    [Fact]
    public void Registrations_WriteOnlyToTheInjectedRegistry_AndLeaveTheRealOneAlone()
    {
        var before = SnapshotRealKeys();
        var memory = new MemoryUserRegistry();
        var data = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
        var registration = new SystemRegistration(memory, Exe);

        registration.RegisterBrowserHost(data);
        registration.RegisterNotifications(@"C:\data\notification.png");
        registration.SetStartWithWindows(true);

        Assert.Equal(Path.Combine(data, NativeHost.Name + ".json"),
            memory.GetString($@"{SystemRegistration.BrowserHostKeys[0]}\{NativeHost.Name}", null));
        Assert.Equal($"\"{Exe}\" \"%1\"", memory.GetString(@"Software\Classes\baton\shell\open\command", null));
        Assert.Equal($"\"{Exe}\" --tray", memory.GetString(SystemRegistration.RunKey, SystemRegistration.RunValue));
        Assert.True(registration.StartsWithWindows);
        Assert.Equal(before, SnapshotRealKeys());
        Directory.Delete(data, recursive: true);
    }

    [Fact]
    public void StartEntry_IsRepaired_NeverCreated_AndDuplicatesAreRemoved()
    {
        var memory = new MemoryUserRegistry();
        var registration = new SystemRegistration(memory, Exe);

        registration.RepairStartEntry();
        Assert.Null(memory.GetString(SystemRegistration.RunKey, SystemRegistration.RunValue)); // never added on its own

        // A stale entry for a test build, plus a duplicate left by an older version.
        memory.SetString(SystemRegistration.RunKey, SystemRegistration.RunValue, @"""C:\src\bin\Debug\Baton.exe"" --tray");
        memory.SetString(SystemRegistration.RunKey, "Baton (2)", @"""C:\old\Baton.exe"" --tray");
        memory.SetString(SystemRegistration.RunKey, "Spotify", "spotify.exe");
        registration.RepairStartEntry();

        Assert.Equal($"\"{Exe}\" --tray", memory.GetString(SystemRegistration.RunKey, SystemRegistration.RunValue));
        Assert.Null(memory.GetString(SystemRegistration.RunKey, "Baton (2)"));
        Assert.Equal("spotify.exe", memory.GetString(SystemRegistration.RunKey, "Spotify")); // other programs untouched

        registration.SetStartWithWindows(false);
        Assert.Null(memory.GetString(SystemRegistration.RunKey, SystemRegistration.RunValue));
        Assert.False(registration.StartsWithWindows);
    }

    /// <summary>Read-only look at the real keys the app could write (reading through Microsoft.Win32 directly is allowed).</summary>
    private static string SnapshotRealKeys()
    {
        static string Dump(string path)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key is null ? $"{path}: -" : $"{path}: " + string.Join(";", key.GetValueNames().Order().Select(n => $"{n}={key.GetValue(n)}"));
        }
        return string.Join("\n", new[] { SystemRegistration.RunKey, @"Software\Classes\baton\shell\open\command", @"Software\Classes\AppUserModelId\Baton.App" }
            .Concat(SystemRegistration.BrowserHostKeys.Select(k => $@"{k}\{NativeHost.Name}"))
            .Select(Dump));
    }
}
