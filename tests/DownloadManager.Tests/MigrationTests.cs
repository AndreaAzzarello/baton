using DownloadManager.Core;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Tests;

/// <summary>Databases written by earlier versions open without losing anything.</summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "downloads.db");

    public MigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Run(string sql)
    {
        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>The schema of version 0.2 (no version number, request context column present).</summary>
    private const string Version02Schema = """
        CREATE TABLE downloads (id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, directory TEXT NOT NULL, file_name TEXT,
            total_size INTEGER, etag TEXT, last_modified TEXT, supports_ranges INTEGER NOT NULL, status INTEGER NOT NULL, error TEXT,
            sort_order INTEGER NOT NULL, added_utc TEXT NOT NULL, completed_utc TEXT, request_context TEXT);
        CREATE TABLE segments (download_id INTEGER NOT NULL REFERENCES downloads(id) ON DELETE CASCADE, idx INTEGER NOT NULL,
            start INTEGER NOT NULL, end_pos INTEGER NOT NULL, written INTEGER NOT NULL, PRIMARY KEY (download_id, idx));
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """;

    [Fact]
    public void Version02_IsUpgraded_WithDownloadsSegmentsAndSettingsIntact()
    {
        Run(Version02Schema + """
            INSERT INTO downloads (url, directory, file_name, total_size, etag, supports_ranges, status, sort_order, added_utc)
                VALUES ('https://example.com/files/tool.zip', 'D:\dl', 'tool.zip', 1000, '"e1"', 1, 2, 0, '2026-10-01T10:00:00.0000000Z');
            INSERT INTO downloads (url, directory, file_name, supports_ranges, status, sort_order, added_utc, completed_utc)
                VALUES ('https://example.com/movie.mkv', 'D:\dl', 'movie.mkv', 1, 3, 1, '2026-10-01T11:00:00.0000000Z', '2026-10-01T11:30:00.0000000Z');
            INSERT INTO downloads (url, directory, supports_ranges, status, sort_order, added_utc)
                VALUES ('https://example.com/get?id=3', 'D:\dl', 0, 4, 2, '2026-10-01T12:00:00.0000000Z');
            INSERT INTO segments VALUES (1, 0, 0, 499, 499), (1, 1, 500, 999, 120);
            INSERT INTO settings VALUES ('MaxConnections', '12'), ('DownloadFolder', 'D:\dl'), ('ConfirmBrowserDownloads', '0');
            """);

        using (var store = new DownloadStore(DbPath))
        {
            Assert.Equal(DownloadStore.SchemaVersion, store.Version);
            Assert.NotNull(store.BackupPath);
            Assert.True(File.Exists(store.BackupPath));

            var categories = store.LoadCategories();
            Assert.Equal(["compressed", "documents", "music", "video", "programs", "general"], categories.Select(c => c.Key));
            long Id(string key) => categories.Single(c => c.Key == key).Id;

            var downloads = store.LoadAll().OrderBy(d => d.Id).ToList();
            Assert.Equal(3, downloads.Count);
            Assert.Equal(Id("compressed"), downloads[0].CategoryId);
            Assert.Equal(Id("video"), downloads[1].CategoryId);
            Assert.Equal(Id("general"), downloads[2].CategoryId);
            Assert.All(downloads, d => Assert.False(d.AutoCategory)); // existing folders are never moved

            Assert.Equal(619, downloads[0].DownloadedBytes);
            Assert.Equal("\"e1\"", downloads[0].ETag);
            Assert.Equal(@"D:\dl", downloads[1].Directory);
            Assert.Equal(DownloadStatus.Completed, downloads[1].Status);
            Assert.Equal(DownloadStatus.Failed, downloads[2].Status);

            var settings = store.LoadSettings();
            Assert.Equal(12, settings.MaxConnections);
            Assert.False(settings.ConfirmBrowserDownloads);
            Assert.Equal(AppTheme.System, settings.Theme); // new setting, default value
            Assert.True(settings.NotifyOnCompletion);
        }

        // The backup still holds the old version untouched.
        var backup = Directory.GetFiles(_dir, "*.v0.bak").Single();
        using (var old = new SqliteConnection($"Data Source={backup};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM downloads; ";
            Assert.Equal(3L, cmd.ExecuteScalar());
        }

        // Opening again is a no-op: no second backup, nothing duplicated.
        File.Delete(backup);
        using var again = new DownloadStore(DbPath);
        Assert.Null(again.BackupPath);
        Assert.Equal(6, again.LoadCategories().Count);
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
    }

    [Fact]
    public void NewDatabase_StartsAtCurrentVersion_WithoutBackup()
    {
        using var store = new DownloadStore(DbPath);
        Assert.Equal(DownloadStore.SchemaVersion, store.Version);
        Assert.Null(store.BackupPath);
        Assert.Equal(6, store.LoadCategories().Count);
    }

    [Fact]
    public void NewerDatabase_IsRefused_InsteadOfBeingDamaged()
    {
        Run(Version02Schema + "PRAGMA user_version = 99;");
        Assert.Throws<InvalidOperationException>(() => new DownloadStore(DbPath));
        Assert.Equal(99L, Scalar("PRAGMA user_version;"));
    }

    [Fact]
    public void Settings_ThemeNotificationsAndPreferences_RoundTrip()
    {
        using (var store = new DownloadStore(DbPath))
        {
            store.SaveSettings(new AppSettings { Theme = AppTheme.Dark, NotifyOnCompletion = false, DownloadFolder = @"C:\x" });
            store.SetPreference("columns", "{\"a\":1}");
            store.SetPreference("columns", "{\"a\":2}");
        }
        using var reopened = new DownloadStore(DbPath);
        var settings = reopened.LoadSettings();
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.False(settings.NotifyOnCompletion);
        Assert.Equal("{\"a\":2}", reopened.GetPreference("columns"));
        Assert.Null(reopened.GetPreference("missing"));
    }
}
