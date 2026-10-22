using Baton.Core;
using Baton.Core.Integration;
using Baton.Core.Platform;
using Baton.Core.Setup;
using Baton.Core.Updates;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>
/// The installed and the portable copy: what each may write to the registry (in memory here), what the uninstaller
/// removes, what the installer finds and imports, and the updater in both folders.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class InstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", "installer-" + Guid.NewGuid().ToString("N"));

    public InstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Exe(string folder)
    {
        var exe = Path.Combine(folder, "Baton.exe");
        File.WriteAllText(exe, "MZ");
        return exe;
    }

    private static IEnumerable<string> HostKeys => SystemRegistration.BrowserHostKeys.Append(SystemRegistration.FirefoxHostKey).Select(k => $@"{k}\{NativeHost.Name}");

    [Fact]
    public void Portable_WritesNothingToTheRegistry_ButTheBrowserHostOnceAllowed()
    {
        var memory = new MemoryUserRegistry();
        memory.SetString(SystemRegistration.RunKey, "OtherApp", "other.exe");
        memory.SetString(@"Software\Classes\downloadmanager", null, "URL:DownloadManager");
        var portable = new PortableUserRegistry(memory);
        var exe = Exe(Folder("usb", "Baton"));
        var registration = new SystemRegistration(portable, exe);

        registration.RegisterBrowserHost(Folder("usb", "Baton", "Data"));
        registration.RegisterNotifications(@"X:\Baton\Data\notification.png");
        registration.SetStartWithWindows(true);
        registration.RepairStartEntry();
        registration.RemoveLegacy();
        registration.UpdateInstalledVersion("1.3.0");
        Assert.Equal([SystemRegistration.RunKey, @"Software\Classes\downloadmanager"], memory.Keys.Order().ToArray().Reverse());
        Assert.Equal("other.exe", memory.GetString(SystemRegistration.RunKey, "OtherApp")); // reading still works
        Assert.True(portable.Refused > 0);

        portable.AllowBrowserHost = true;
        registration.RegisterBrowserHost(Folder("usb", "Baton", "Data"));
        registration.SetStartWithWindows(true);
        Assert.All(HostKeys, key => Assert.EndsWith(".json", memory.GetString(key, null)));
        Assert.Null(memory.GetString(SystemRegistration.RunKey, SystemRegistration.RunValue));
        Assert.Equal(5 + 2, memory.Keys.Count);

        // Turned off in the settings: the registration of this copy goes, and nothing else.
        registration.Unregister(browserHostOnly: true);
        Assert.All(HostKeys, key => Assert.Null(memory.GetString(key, null)));
        Assert.Equal(2, memory.Keys.Count);

        // Start with Windows, only while the user turns it on or off: its own entry, pointing to this folder, beside the
        // installed copy's, which it never touches.
        memory.SetString(SystemRegistration.RunKey, SystemRegistration.RunValue, @"""C:\Programs\Baton\Baton.exe"" --tray");
        portable.AllowBrowserHost = false;
        portable.AllowStartEntry = true;
        registration.SetStartWithWindows(true);
        registration.RegisterBrowserHost(Folder("usb", "Baton", "Data"));
        registration.RepairStartEntry();
        portable.DeleteKey(SystemRegistration.RunKey); // never the whole key
        portable.SetString(SystemRegistration.RunKey, "OtherApp", "x"); // nor another program's value
        Assert.Contains(exe, memory.GetString(SystemRegistration.RunKey, SystemRegistration.PortableRunValue));
        Assert.Equal(@"""C:\Programs\Baton\Baton.exe"" --tray", memory.GetString(SystemRegistration.RunKey, SystemRegistration.RunValue));
        Assert.True(registration.StartsWithWindows);
        Assert.True(new SystemRegistration(memory, @"C:\Programs\Baton\Baton.exe").StartsWithWindows);
        new SystemRegistration(memory, @"C:\Programs\Baton\Baton.exe").RepairStartEntry(); // the installed copy leaves it alone
        Assert.NotNull(memory.GetString(SystemRegistration.RunKey, SystemRegistration.PortableRunValue));
        Assert.All(HostKeys, key => Assert.Null(memory.GetString(key, null)));
        portable.AllowStartEntry = false;
        registration.SetStartWithWindows(false);
        Assert.True(registration.StartsWithWindows);
        portable.AllowStartEntry = true;
        registration.SetStartWithWindows(false);
        Assert.False(registration.StartsWithWindows);
        Assert.Equal("other.exe", memory.GetString(SystemRegistration.RunKey, "OtherApp"));
    }

    [Fact]
    public void PortablePermission_LastsForItsScope_AndAnInnerScopeDoesNotEndTheOuterOne()
    {
        var portable = new PortableUserRegistry(new MemoryUserRegistry());
        using (PortableUserRegistry.Allow(portable, browserHost: true))
        {
            using (PortableUserRegistry.Allow(portable, browserHost: true, startEntry: true))
                Assert.True(portable.AllowBrowserHost && portable.AllowStartEntry);
            Assert.True(portable.AllowBrowserHost); // still within the outer change
            Assert.False(portable.AllowStartEntry);
        }
        Assert.False(portable.AllowBrowserHost || portable.AllowStartEntry);
        PortableUserRegistry.Allow(new MemoryUserRegistry(), startEntry: true).Dispose(); // the installed copy needs no permission
    }

    [Fact]
    public void BundledExtension_IsFoundNextToTheProgram_OrNotAtAll()
    {
        var program = Folder("app");
        Assert.Null(BundledExtension.Folder(program));
        Assert.Null(BundledExtension.FirefoxPackage(program));
        Directory.CreateDirectory(Path.Combine(program, "extension"));
        Assert.Null(BundledExtension.Folder(program)); // an empty folder is not the extension
        File.WriteAllText(Path.Combine(program, "extension", "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(program, "baton-firefox.xpi"), "PK");
        Assert.Equal(Path.Combine(program, "extension"), BundledExtension.Folder(program));
        Assert.Equal(Path.Combine(program, "baton-firefox.xpi"), BundledExtension.FirefoxPackage(program));
    }

    [Fact]
    public void Uninstall_RemovesWhatPointsToThisCopy_AndLeavesAnotherCopyAlone()
    {
        var memory = new MemoryUserRegistry();
        var installed = new SystemRegistration(memory, Exe(Folder("Programs", "Baton")));
        installed.RegisterBrowserHost(Folder("LocalAppData", "Baton"));
        installed.RegisterNotifications(@"C:\data\notification.png");
        installed.SetStartWithWindows(true);
        memory.SetString(SystemRegistration.RunKey, "OtherApp", "other.exe");
        var manifest = memory.GetString(HostKeys.First(), null)!;
        Assert.True(File.Exists(manifest));

        // Another copy took over the browsers meanwhile: uninstalling the installed one must not break it.
        var other = new SystemRegistration(memory, Exe(Folder("Portable")));
        other.RegisterBrowserHost(Folder("Portable", "Data"));
        installed.Unregister();
        Assert.All(HostKeys, key => Assert.NotNull(memory.GetString(key, null)));
        Assert.Null(memory.GetString(@"Software\Classes\baton\shell\open\command", null));
        Assert.Null(memory.GetString(@"Software\Classes\AppUserModelId\Baton.App", "DisplayName"));
        Assert.Null(memory.GetString(SystemRegistration.RunKey, SystemRegistration.RunValue));
        Assert.Equal("other.exe", memory.GetString(SystemRegistration.RunKey, "OtherApp"));

        // The other copy uninstalled too: its host goes with its manifest files.
        var otherManifest = memory.GetString(HostKeys.First(), null)!;
        other.Unregister();
        Assert.All(HostKeys, key => Assert.Null(memory.GetString(key, null)));
        Assert.False(File.Exists(otherManifest));
        Assert.Equal("other.exe", memory.GetString(SystemRegistration.RunKey, "OtherApp"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Baton\\Baton.exe\" --tray", @"C:\Program Files\Baton\Baton.exe")]
    [InlineData("\"C:\\x\\Baton.exe\" \"%1\"", @"C:\x\Baton.exe")]
    [InlineData(@"C:\x\Baton.exe --tray", @"C:\x\Baton.exe")]
    [InlineData("", null)]
    [InlineData("\"", null)]
    public void ProgramOfACommand(string command, string? program) => Assert.Equal(program, SystemRegistration.ProgramOf(command));

    [Fact]
    public void InstalledCopy_ShowsTheUpdatedVersionInInstalledApps()
    {
        var memory = new MemoryUserRegistry();
        var folder = Folder("Programs", "Baton");
        memory.SetString(SystemRegistration.UninstallKey, "InstallLocation", folder + @"\"); // as Inno Setup writes it
        memory.SetString(SystemRegistration.UninstallKey, "DisplayVersion", "1.3.0");
        new SystemRegistration(memory, Exe(folder)).UpdateInstalledVersion("1.3.1");
        Assert.Equal("1.3.1", memory.GetString(SystemRegistration.UninstallKey, "DisplayVersion"));

        // A copy elsewhere (portable, or a build) never touches the installed one's entry.
        Assert.False(new SystemRegistration(memory, Exe(Folder("elsewhere"))).IsInstalledCopy);
        new SystemRegistration(memory, Exe(Folder("elsewhere"))).UpdateInstalledVersion("9.9.9");
        Assert.Equal("1.3.1", memory.GetString(SystemRegistration.UninstallKey, "DisplayVersion"));
    }

    private static void Database(string folder, int downloads)
    {
        using (new DownloadStore(Path.Combine(folder, "downloads.db"))) { }
        using var connection = new SqliteConnection($"Data Source={Path.Combine(folder, "downloads.db")};Pooling=False");
        connection.Open();
        for (var i = 0; i < downloads; i++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO downloads(url, directory, file_name, supports_ranges, status, sort_order, added_utc) VALUES ('https://example.test/f', 'C:\\d', $n, 1, 3, $n, '2026-10-21T10:00:00Z');";
            insert.Parameters.AddWithValue("$n", i);
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void SetupCheck_FindsThePortableCopy_ItsData_AndDownloadManager()
    {
        var local = Folder("LocalAppData");
        var memory = new MemoryUserRegistry();
        var installedExe = Path.Combine(_dir, "Programs", "Baton", "Baton.exe");
        var portableFolder = Folder("usb", "Baton");
        var portableExe = Exe(portableFolder);
        File.WriteAllText(Path.Combine(portableFolder, SetupCheck.PortableFlag), "");
        Database(Folder("usb", "Baton", SetupCheck.PortableDataFolder), 3);
        Database(Folder("LocalAppData", "DownloadManager"), 2);
        var portable = new SystemRegistration(memory, portableExe);
        portable.RegisterBrowserHost(Path.Combine(portableFolder, SetupCheck.PortableDataFolder));

        var report = SetupCheck.Find(memory, Path.Combine(local, "Baton"), Path.Combine(local, "DownloadManager"), installedExe, running: false);
        Assert.False(report.HasData);
        // The portable import fills the installed data: DownloadManager's copy at the first start is not promised.
        Assert.Null(report.LegacyData);
        Assert.Equal([portableExe], report.Copies);
        Assert.Equal(Path.Combine(portableFolder, SetupCheck.PortableDataFolder), report.PortableData);
        Assert.Equal($"running=0\r\ndata=0\r\ncopy={portableExe}\r\nportable={report.PortableData}\r\n", report.Format());

        // Imported with the copy that moved DownloadManager's data: all downloads, the portable folder untouched (no
        // note telling to delete it: the portable copy keeps using it).
        var target = Path.Combine(local, "Baton");
        Assert.Equal(MigrationOutcome.Migrated, LegacyData.Migrate(report.PortableData, target, leaveNote: false).Outcome);
        using (var store = new DownloadStore(Path.Combine(target, "downloads.db")))
            Assert.Equal(3, store.LoadAll().Count);
        Assert.True(File.Exists(Path.Combine(report.PortableData!, "downloads.db")));
        Assert.False(File.Exists(Path.Combine(report.PortableData!, LegacyData.MarkerName)));

        // Now the installed app has data: nothing more is offered, the copy is still named.
        var again = SetupCheck.Find(memory, Path.Combine(local, "Baton"), Path.Combine(local, "DownloadManager"), installedExe, running: true);
        Assert.True(again.HasData && again.Running);
        Assert.Null(again.PortableData);
        Assert.Null(again.LegacyData);
        Assert.Equal([portableExe], again.Copies);
        Assert.Equal(MigrationOutcome.NothingToDo, LegacyData.Migrate(report.PortableData, target).Outcome);
    }

    [Fact]
    public void SetupCheck_WithoutAPortableCopy_PromisesDownloadManagersData_AndSurvivesMalformedPaths()
    {
        var local = Folder("LocalAppData");
        Database(Folder("LocalAppData", "DownloadManager"), 2);
        var memory = new MemoryUserRegistry();
        memory.SetString(SystemRegistration.RunKey, SystemRegistration.RunValue, @"""C:\bad<|>\Baton.exe"" --tray");
        var report = SetupCheck.Find(memory, Path.Combine(local, "Baton"), Path.Combine(local, "DownloadManager"),
            Path.Combine(_dir, "Programs", "Baton", "Baton.exe"), running: false);
        Assert.Equal(Path.Combine(local, "DownloadManager"), report.LegacyData);
        Assert.Empty(report.Copies);
    }

    [Fact]
    public void SetupCheck_IgnoresTheInstalledCopyItself_AndCopiesThatNoLongerExist()
    {
        var memory = new MemoryUserRegistry();
        var installedExe = Exe(Folder("Programs", "Baton"));
        new SystemRegistration(memory, installedExe).SetStartWithWindows(true);
        new SystemRegistration(memory, Path.Combine(_dir, "deleted", "Baton.exe")).RegisterNotifications("icon.png");
        var report = SetupCheck.Find(memory, Folder("LocalAppData", "Baton"), null, installedExe, running: false);
        Assert.Empty(report.Copies);
        Assert.Equal("running=0\r\ndata=0\r\n", report.Format());
    }

    [Fact]
    public void PortableBrowserHostChoice_IsStoredAsAskedOrNot()
    {
        var path = Path.Combine(_dir, "downloads.db");
        using (var store = new DownloadStore(path))
        {
            Assert.Null(store.LoadSettings().PortableBrowserHost);
            var settings = store.LoadSettings();
            store.SaveSettings(settings);
            Assert.Null(store.LoadSettings().PortableBrowserHost); // saving other settings does not answer for the user
            settings.PortableBrowserHost = false;
            store.SaveSettings(settings);
            Assert.False(store.LoadSettings().PortableBrowserHost);
            settings.PortableBrowserHost = true;
            store.SaveSettings(settings);
            Assert.True(store.LoadSettings().PortableBrowserHost);
        }
        Assert.Equal(AppLanguage.Automatic, DownloadStore.LanguageOf(path));
        Assert.Equal(AppLanguage.Automatic, DownloadStore.LanguageOf(Path.Combine(_dir, "missing.db")));
    }

    [Theory]
    [InlineData("portable")]
    [InlineData("installed")]
    public void Update_ReplacesTheProgram_AndKeepsWhatIsAroundIt(string mode)
    {
        var folder = Folder(mode);
        var exe = Exe(folder);
        var around = mode == "portable"
            ? new[] { SetupCheck.PortableFlag, Path.Combine(SetupCheck.PortableDataFolder, "downloads.db") }
            : new[] { "unins000.exe", "unins000.dat", "LICENSE.txt" };
        foreach (var file in around)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, file))!);
            File.WriteAllText(Path.Combine(folder, file), file);
        }
        var verified = Path.Combine(Folder("updates"), "Baton.new.exe");
        File.WriteAllText(verified, "MZ new");

        UpdateInstaller.Swap(verified, exe);
        Assert.Equal("MZ new", File.ReadAllText(exe));
        Assert.Equal("MZ", File.ReadAllText(UpdateInstaller.BackupOf(exe))); // Baton.exe.old, for "restore previous version"
        Assert.All(around, file => Assert.Equal(file, File.ReadAllText(Path.Combine(folder, file))));

        Assert.True(UpdateInstaller.Restore(exe));
        Assert.Equal("MZ", File.ReadAllText(exe));
        Assert.All(around, file => Assert.True(File.Exists(Path.Combine(folder, file))));
    }
}
