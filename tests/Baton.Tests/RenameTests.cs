using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Baton.Core;
using Baton.Core.Platform;
using Baton.Core.Setup;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>From DownloadManager (up to 1.1) to Baton: data copied safely, old registrations removed, nothing visible keeps the old name.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RenameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_dir, "DownloadManager");
    private string Target => Path.Combine(_dir, "Baton");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A DownloadManager data folder as 1.1 leaves it: database with downloads, logs, secret, backups, host files.</summary>
    private void CreateLegacyData(int downloads)
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "logs"));
        using (new DownloadStore(Path.Combine(Legacy, "downloads.db"))) { }
        using (var db = new SqliteConnection($"Data Source={Path.Combine(Legacy, "downloads.db")};Pooling=False"))
        {
            db.Open();
            for (var i = 0; i < downloads; i++)
            {
                using var insert = db.CreateCommand();
                insert.CommandText = """
                    INSERT INTO downloads(url, directory, file_name, total_size, supports_ranges, status, sort_order, added_utc)
                    VALUES ($url, 'C:\Download', $name, 1000, 1, 3, $i, '2026-10-20T10:00:00Z');
                    """;
                insert.Parameters.AddWithValue("$url", $"https://example.test/{i}.zip");
                insert.Parameters.AddWithValue("$name", $"{i}.zip");
                insert.Parameters.AddWithValue("$i", i);
                insert.ExecuteNonQuery();
            }
        }
        File.WriteAllText(Path.Combine(Legacy, "logs", "app.log"), "start 1.1.0\n");
        File.WriteAllBytes(Path.Combine(Legacy, "notification.key"), RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(Path.Combine(Legacy, "downloads.db.v7.bak"), "backup");
        File.WriteAllText(Path.Combine(Legacy, "com.downloadmanager.host.json"), "{}");
    }

    // SQLite's -wal/-shm companions appear when the database is opened (even read-only): they hold no data change.
    private Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(folder, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private static long Count(string db)
    {
        using var connection = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM downloads;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Migration_CopiesEverything_AndLeavesTheOldFolderAsItWas()
    {
        CreateLegacyData(5);
        var before = Snapshot(Legacy);
        Directory.CreateDirectory(Path.Combine(Target, "updates")); // a start mark written before the copy

        Assert.Equal((MigrationOutcome.Migrated, (string?)null), LegacyData.Migrate(Legacy, Target));
        Assert.Equal(5, Count(Path.Combine(Target, "downloads.db")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Legacy, "notification.key")), File.ReadAllBytes(Path.Combine(Target, "notification.key")));
        Assert.True(File.Exists(Path.Combine(Target, "logs", "app.log")));
        Assert.True(File.Exists(Path.Combine(Target, "downloads.db.v7.bak")));
        Assert.False(File.Exists(Path.Combine(Target, "com.downloadmanager.host.json"))); // Baton writes its own host files
        Assert.False(Directory.Exists(Target + ".migrating"));

        var after = Snapshot(Legacy);
        Assert.True(after.Remove(LegacyData.MarkerName)); // only a note is added to the old folder
        Assert.Equal(before, after);

        Assert.Equal(MigrationOutcome.NothingToDo, LegacyData.Migrate(Legacy, Target).Outcome); // once only
        Assert.Equal(MigrationOutcome.NothingToDo, LegacyData.Migrate(Path.Combine(_dir, "none"), Path.Combine(_dir, "new")).Outcome);
        Assert.Equal(MigrationOutcome.NothingToDo, LegacyData.Migrate(null, Path.Combine(_dir, "new")).Outcome);
    }

    [Fact]
    public void Migration_FailingHalfway_LeavesNoHalfCopy_AndSucceedsLater()
    {
        CreateLegacyData(3);
        var before = Snapshot(Legacy);
        using (new FileStream(Path.Combine(Legacy, "logs", "app.log"), FileMode.Open, FileAccess.Read, FileShare.None)) // copy fails here
        {
            var (outcome, error) = LegacyData.Migrate(Legacy, Target);
            Assert.Equal(MigrationOutcome.Failed, outcome);
            Assert.False(string.IsNullOrEmpty(error));
        }
        Assert.False(File.Exists(Path.Combine(Target, "downloads.db"))); // the app keeps using the old folder
        Assert.False(Directory.Exists(Target + ".migrating"));
        Assert.Equal(before, Snapshot(Legacy));

        Assert.Equal(MigrationOutcome.Migrated, LegacyData.Migrate(Legacy, Target).Outcome); // the next start
        Assert.Equal(3, Count(Path.Combine(Target, "downloads.db")));
    }

    [Fact]
    public void Migration_OfADamagedDatabase_Fails_WithoutTouchingIt()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "downloads.db"), "not a database");
        Assert.Equal(MigrationOutcome.Failed, LegacyData.Migrate(Legacy, Target).Outcome);
        Assert.Equal("not a database", File.ReadAllText(Path.Combine(Legacy, "downloads.db")));
        Assert.False(File.Exists(Path.Combine(Target, "downloads.db")));
    }

    [Fact]
    public void OldRegistrations_AreRemoved_AndTheNewOnesWritten_StartEntryOnlyIfThereWasOne()
    {
        const string exe = @"C:\Apps\Baton\Baton.exe";
        var memory = new MemoryUserRegistry();
        foreach (var key in SystemRegistration.BrowserHostKeys.Append(SystemRegistration.FirefoxHostKey))
            memory.SetString($@"{key}\com.downloadmanager.host", null, @"C:\Users\x\AppData\Local\DownloadManager\com.downloadmanager.host.json");
        memory.SetString(@"Software\Classes\downloadmanager", null, "URL:DownloadManager");
        memory.SetString(@"Software\Classes\downloadmanager\shell\open\command", null, @"""C:\Apps\Baton\DownloadManager.exe"" ""%1""");
        memory.SetString(@"Software\Classes\AppUserModelId\DownloadManager.App", "DisplayName", "DownloadManager");
        memory.SetString(SystemRegistration.RunKey, "DownloadManager", @"""C:\Apps\Baton\DownloadManager.exe"" --tray");
        memory.SetString(SystemRegistration.RunKey, "DownloadManagerHelper", @"C:\Other\helper.exe"); // not ours: stays
        memory.SetString(SystemRegistration.RunKey, "Spotify", "spotify.exe");
        memory.SetString(@"Software\Classes\downloadmanagerx", null, "not ours"); // a key that only starts the same way

        var registration = new SystemRegistration(memory, exe);
        registration.RegisterBrowserHost(Path.Combine(_dir, "data"));
        registration.RegisterNotifications(@"C:\data\notification.png");
        registration.RemoveLegacy();

        Assert.Equal("not ours", memory.GetString(@"Software\Classes\downloadmanagerx", null));
        Assert.DoesNotContain(memory.Keys, k => k.Contains("downloadmanager", StringComparison.OrdinalIgnoreCase) && !k.EndsWith("downloadmanagerx", StringComparison.Ordinal));
        foreach (var key in SystemRegistration.BrowserHostKeys.Append(SystemRegistration.FirefoxHostKey))
            Assert.NotNull(memory.GetString($@"{key}\com.andreaazzarello.baton", null));
        Assert.Equal($"\"{exe}\" \"%1\"", memory.GetString(@"Software\Classes\baton\shell\open\command", null));
        Assert.Equal("Baton", memory.GetString(@"Software\Classes\AppUserModelId\Baton.App", "DisplayName"));
        Assert.Null(memory.GetString(SystemRegistration.RunKey, "DownloadManager"));
        Assert.Equal($"\"{exe}\" --tray", memory.GetString(SystemRegistration.RunKey, "Baton")); // it replaced the old one
        Assert.Equal(@"C:\Other\helper.exe", memory.GetString(SystemRegistration.RunKey, "DownloadManagerHelper"));
        Assert.Equal("spotify.exe", memory.GetString(SystemRegistration.RunKey, "Spotify"));

        // "Restore previous version" back to 1.1: its start entry comes back, Baton's goes.
        registration.RestoreLegacyStartEntry(@"C:\Apps\Baton\DownloadManager.exe");
        Assert.Null(memory.GetString(SystemRegistration.RunKey, "Baton"));
        Assert.Equal(@"""C:\Apps\Baton\DownloadManager.exe"" --tray", memory.GetString(SystemRegistration.RunKey, "DownloadManager"));

        var fresh = new MemoryUserRegistry();
        new SystemRegistration(fresh, exe).RemoveLegacy();
        Assert.Null(fresh.GetString(SystemRegistration.RunKey, "Baton")); // never created when there was none
    }

    [Fact]
    public void NotificationLinks_UseTheBatonScheme_AndTheOldOneIsNotAccepted()
    {
        var links = new NotificationLinks(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var link = links.Create(NotificationAction.Open, @"C:\Download\a.zip", 7, now.AddDays(1));
        Assert.StartsWith("baton:open?", link);
        Assert.True(links.TryParse(link, now, out _, out _, out _));
        Assert.False(NotificationLinks.IsLink("downloadmanager:" + link["baton:".Length..]));
        Assert.False(links.TryParse("downloadmanager:" + link["baton:".Length..], now, out _, out _, out _));
    }

    [Fact]
    public void Shortcuts_ToDownloadManagerExe_NowStartBaton_OthersAreLeftAlone()
    {
        var folder = Path.Combine(_dir, "Desktop");
        Directory.CreateDirectory(folder);
        var legacy = Path.Combine(_dir, "app", "DownloadManager.exe");
        var baton = Path.Combine(_dir, "app", "Baton.exe");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        void Create(string name, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, name))!);
            dynamic link = shell.CreateShortcut(Path.Combine(folder, name));
            link.TargetPath = target;
            link.Save();
        }
        Create("DownloadManager.lnk", legacy);
        Create("Downloads (mine).lnk", legacy);
        Create("Other.lnk", @"C:\Windows\notepad.exe");
        Create(@"DownloadManager\DownloadManager.lnk", legacy); // a Start menu folder

        Assert.Equal(2, LegacyShortcuts.Retarget([(folder, false)], legacy, baton)); // the desktop: not its subfolders
        Assert.Equal(1, LegacyShortcuts.Retarget([(folder, true)], legacy, baton)); // the Start menu: subfolders too
        Assert.Equal(baton, (string)shell.CreateShortcut(Path.Combine(folder, "DownloadManager", "Baton.lnk")).TargetPath);
        Assert.Equal(baton, LegacyShortcuts.TargetOf(Path.Combine(folder, "Baton.lnk")));
        Assert.False(File.Exists(Path.Combine(folder, "DownloadManager.lnk")));
        Assert.Equal(baton, (string)shell.CreateShortcut(Path.Combine(folder, "Baton.lnk")).TargetPath);
        Assert.Equal(baton, (string)shell.CreateShortcut(Path.Combine(folder, "Downloads (mine).lnk")).TargetPath);
        Assert.Equal(@"C:\Windows\notepad.exe", (string)shell.CreateShortcut(Path.Combine(folder, "Other.lnk")).TargetPath);
    }

    [Fact]
    public void TrayIcons_HaveTheirPixelGridSizes_AndTheRightFrameIsPicked()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Baton.slnx")))
            root = Path.GetDirectoryName(root)!;
        foreach (var name in new[] { "tray-light.ico", "tray-dark.ico", "app.ico" })
        {
            var ico = File.ReadAllBytes(Path.Combine(root, "src", "Baton.App", name));
            Assert.Equal(16, IconFile.Frame(ico, 16)!.Value.Size);
            Assert.Equal(20, IconFile.Frame(ico, 18)!.Value.Size); // 112 % scaling: the next larger frame, never a blurred smaller one
            Assert.Equal(32, IconFile.Frame(ico, 32)!.Value.Size);
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], IconFile.Frame(ico, 16)!.Value.Data[..4]);
        }
        Assert.Equal(32, IconFile.Frame(File.ReadAllBytes(Path.Combine(root, "src", "Baton.App", "tray-dark.ico")), 48)!.Value.Size); // the largest
        Assert.Equal(256, IconFile.Frame(File.ReadAllBytes(Path.Combine(root, "src", "Baton.App", "app.ico")), 100)!.Value.Size);
        Assert.Null(IconFile.Frame([1, 2, 3], 16));
        Assert.Null(IconFile.Frame([0, 0, 1, 0, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0, 255, 255, 0, 0, 0, 0, 0, 0], 16)); // frame outside the file
    }

    /// <summary>Texts that explain the rename name the old app on purpose; nothing else visible says "DownloadManager".</summary>
    private static readonly string[] ExplainTheRename = ["MigrationFailed", "UpdateConfirmRestoreLegacy", "ImportErrorFormat", "LegacyAppRunning"];

    [Fact]
    public void NoVisibleText_StillSaysDownloadManager()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Baton.slnx")))
            root = Path.GetDirectoryName(root)!;

        var resx = XDocument.Load(Path.Combine(root, "src", "Baton.Core", "Resources", "Strings.resx"));
        var appTexts = resx.Root!.Elements("data").Select(d => (Key: (string)d.Attribute("name")!, Text: (string)d.Element("value")!)).ToList();
        Assert.Contains(appTexts, t => t.Key == "AppName" && t.Text == "Baton");
        Assert.DoesNotContain(appTexts, t => t.Text.Contains("DownloadManager", StringComparison.OrdinalIgnoreCase) && !ExplainTheRename.Contains(t.Key));
        Assert.All(appTexts.Where(t => ExplainTheRename.Contains(t.Key)), t => Assert.Contains("Baton", t.Text));

        foreach (var language in new[] { "en", "it" })
        {
            using var messages = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "extension", "_locales", language, "messages.json")));
            Assert.DoesNotContain(messages.RootElement.EnumerateObject(),
                m => m.Value.GetProperty("message").GetString()!.Contains("DownloadManager", StringComparison.OrdinalIgnoreCase));
        }
        foreach (var file in new[] { "manifest.json", "popup.html", "check.html" })
            Assert.DoesNotContain("DownloadManager", File.ReadAllText(Path.Combine(root, "extension", file)), StringComparison.OrdinalIgnoreCase);
        foreach (var xaml in Directory.EnumerateFiles(Path.Combine(root, "src", "Baton.App"), "*.xaml", SearchOption.AllDirectories))
            Assert.DoesNotContain("DownloadManager", File.ReadAllText(xaml), StringComparison.OrdinalIgnoreCase);
    }
}
