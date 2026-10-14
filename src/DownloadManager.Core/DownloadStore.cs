using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Core;

/// <summary>SQLite persistence for downloads, their segments, categories and app settings.</summary>
public sealed class DownloadStore : IDisposable
{
    /// <summary>Version written by this build. Every older version is upgraded by <see cref="Migrate"/>.</summary>
    public const int SchemaVersion = 2;

    private const string PreferencePrefix = "ui.";

    private readonly object _lock = new();
    private readonly SqliteConnection _db;

    public DownloadStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");
        Migrate(databasePath);
    }

    /// <summary>Copy of the database taken before an upgrade in this session; null when none was needed.</summary>
    public string? BackupPath { get; private set; }

    public int Version
    {
        get { lock (_lock) return (int)(long)Scalar("PRAGMA user_version;")!; }
    }

    /// <summary>
    /// Brings a database from any earlier version to <see cref="SchemaVersion"/>, one step at a time, in one
    /// transaction. A database with data is copied first, so a failed upgrade never costs the user their list.
    /// </summary>
    private void Migrate(string databasePath)
    {
        var version = Version;
        if (version > SchemaVersion)
            throw new InvalidOperationException($"The database was created by a newer version of the app (schema {version}).");
        if (version == SchemaVersion)
            return;

        if (TableExists("downloads"))
        {
            BackupPath = $"{databasePath}.v{version}.bak";
            if (File.Exists(BackupPath))
                File.Delete(BackupPath);
            using var backup = Command("VACUUM INTO $path;");
            backup.Parameters.AddWithValue("$path", BackupPath);
            backup.ExecuteNonQuery();
        }

        using var tx = _db.BeginTransaction();
        if (version < 1)
            MigrateTo1(tx);
        if (version < 2)
            MigrateTo2(tx);
        Execute($"PRAGMA user_version = {SchemaVersion};", tx);
        tx.Commit();
    }

    /// <summary>Versions 0.1 and 0.2 had no schema number; 0.2 added the browser request context.</summary>
    private void MigrateTo1(SqliteTransaction tx)
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS downloads (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL,
                directory TEXT NOT NULL,
                file_name TEXT,
                total_size INTEGER,
                etag TEXT,
                last_modified TEXT,
                supports_ranges INTEGER NOT NULL,
                status INTEGER NOT NULL,
                error TEXT,
                sort_order INTEGER NOT NULL,
                added_utc TEXT NOT NULL,
                completed_utc TEXT);
            CREATE TABLE IF NOT EXISTS segments (
                download_id INTEGER NOT NULL REFERENCES downloads(id) ON DELETE CASCADE,
                idx INTEGER NOT NULL,
                start INTEGER NOT NULL,
                end_pos INTEGER NOT NULL,
                written INTEGER NOT NULL,
                PRIMARY KEY (download_id, idx));
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """, tx);
        AddColumnIfMissing("downloads", "request_context", "TEXT", tx);
    }

    /// <summary>Categories: the built-in ones are created and every existing download is assigned by its file name.</summary>
    private void MigrateTo2(SqliteTransaction tx)
    {
        Execute("""
            CREATE TABLE categories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                key TEXT UNIQUE,
                name TEXT NOT NULL,
                extensions TEXT NOT NULL,
                folder TEXT NOT NULL,
                sort_order INTEGER NOT NULL);
            """, tx);
        AddColumnIfMissing("downloads", "category_id", "INTEGER", tx);
        AddColumnIfMissing("downloads", "auto_category", "INTEGER NOT NULL DEFAULT 0", tx);
        AddColumnIfMissing("downloads", "content_type", "TEXT", tx);

        var categories = Categories.Defaults();
        foreach (var category in categories)
            InsertCategory(category, tx);

        var assignments = new List<(long Id, long Category)>();
        using (var select = Command("SELECT id, url, file_name FROM downloads;", tx))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.IsDBNull(2) ? null : reader.GetString(2);
                assignments.Add((reader.GetInt64(0), Categories.MatchUrl(categories, reader.GetString(1), name).Id));
            }
        }
        using var update = Command("UPDATE downloads SET category_id = $c WHERE id = $id;", tx);
        var pCategory = update.Parameters.Add("$c", SqliteType.Integer);
        var pId = update.Parameters.Add("$id", SqliteType.Integer);
        foreach (var (id, category) in assignments)
        {
            pCategory.Value = category;
            pId.Value = id;
            update.ExecuteNonQuery();
        }
    }

    public long Insert(DownloadRecord record)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                INSERT INTO downloads (url, directory, file_name, total_size, etag, last_modified, supports_ranges, status, error,
                                       sort_order, added_utc, completed_utc, request_context, category_id, auto_category, content_type)
                VALUES ($url, $dir, $name, $size, $etag, $lm, $ranges, $status, $error,
                        $order, $added, $completed, $context, $category, $auto, $ctype);
                SELECT last_insert_rowid();
                """, tx);
            Bind(cmd, record);
            record.Id = (long)cmd.ExecuteScalar()!;
            WriteSegments(record, tx);
            tx.Commit();
            return record.Id;
        }
    }

    public void Update(DownloadRecord record)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                UPDATE downloads SET url = $url, directory = $dir, file_name = $name, total_size = $size, etag = $etag,
                    last_modified = $lm, supports_ranges = $ranges, status = $status, error = $error,
                    sort_order = $order, added_utc = $added, completed_utc = $completed, request_context = $context,
                    category_id = $category, auto_category = $auto, content_type = $ctype
                WHERE id = $id;
                """, tx);
            Bind(cmd, record);
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.ExecuteNonQuery();
            WriteSegments(record, tx);
            tx.Commit();
        }
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            using var cmd = Command("DELETE FROM downloads WHERE id = $id;");
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public List<DownloadRecord> LoadAll()
    {
        lock (_lock)
        {
            var records = new Dictionary<long, DownloadRecord>();
            using (var cmd = Command("SELECT * FROM downloads ORDER BY sort_order, id;"))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var r = new DownloadRecord
                    {
                        Id = reader.GetInt64(reader.GetOrdinal("id")),
                        Url = reader.GetString(reader.GetOrdinal("url")),
                        Directory = reader.GetString(reader.GetOrdinal("directory")),
                        FileName = NullableString(reader, "file_name"),
                        TotalSize = NullableLong(reader, "total_size"),
                        ETag = NullableString(reader, "etag"),
                        LastModified = NullableString(reader, "last_modified"),
                        SupportsRanges = reader.GetInt64(reader.GetOrdinal("supports_ranges")) != 0,
                        Status = (DownloadStatus)reader.GetInt32(reader.GetOrdinal("status")),
                        Error = NullableString(reader, "error"),
                        SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order")),
                        AddedUtc = ParseDate(reader.GetString(reader.GetOrdinal("added_utc"))),
                        CompletedUtc = NullableString(reader, "completed_utc") is { } c ? ParseDate(c) : null,
                        Context = RequestContexts.Unprotect(NullableString(reader, "request_context")),
                        CategoryId = NullableLong(reader, "category_id"),
                        AutoCategory = reader.GetInt64(reader.GetOrdinal("auto_category")) != 0,
                        ContentType = NullableString(reader, "content_type"),
                    };
                    records[r.Id] = r;
                }
            }

            using (var cmd = Command("SELECT download_id, start, end_pos, written FROM segments ORDER BY download_id, idx;"))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (records.TryGetValue(reader.GetInt64(0), out var r))
                        r.Segments.Add(new SegmentState { Start = reader.GetInt64(1), End = reader.GetInt64(2), Written = reader.GetInt64(3) });
                }
            }
            return records.Values.ToList();
        }
    }

    public List<Category> LoadCategories()
    {
        lock (_lock)
        {
            var result = new List<Category>();
            using var cmd = Command("SELECT id, key, name, extensions, folder, sort_order FROM categories ORDER BY sort_order, id;");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new Category
                {
                    Id = reader.GetInt64(0),
                    Key = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Name = reader.GetString(2),
                    Extensions = Categories.ParseExtensions(reader.GetString(3)),
                    Folder = reader.GetString(4),
                    SortOrder = reader.GetInt32(5),
                });
            }
            return result;
        }
    }

    /// <summary>Inserts a new category (Id 0) or updates an existing one.</summary>
    public void SaveCategory(Category category)
    {
        lock (_lock)
        {
            if (category.Id == 0)
            {
                InsertCategory(category, null);
                return;
            }
            using var cmd = Command("UPDATE categories SET name = $name, extensions = $ext, folder = $folder, sort_order = $order WHERE id = $id;");
            BindCategory(cmd, category);
            cmd.Parameters.AddWithValue("$id", category.Id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes a category; its downloads move to <paramref name="moveTo"/>.</summary>
    public void DeleteCategory(long id, long moveTo)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var move = Command("UPDATE downloads SET category_id = $to WHERE category_id = $id;", tx))
            {
                move.Parameters.AddWithValue("$to", moveTo);
                move.Parameters.AddWithValue("$id", id);
                move.ExecuteNonQuery();
            }
            using (var delete = Command("DELETE FROM categories WHERE id = $id;", tx))
            {
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public AppSettings LoadSettings()
    {
        var settings = new AppSettings();
        lock (_lock)
        {
            using var cmd = Command("SELECT key, value FROM settings;");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var value = reader.GetString(1);
                switch (reader.GetString(0))
                {
                    case nameof(AppSettings.MaxConnections): settings.MaxConnections = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case nameof(AppSettings.MaxConcurrentDownloads): settings.MaxConcurrentDownloads = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case nameof(AppSettings.SpeedLimitKBps): settings.SpeedLimitKBps = long.Parse(value, CultureInfo.InvariantCulture); break;
                    case nameof(AppSettings.DownloadFolder): settings.DownloadFolder = value; break;
                    case nameof(AppSettings.QueueRunning): settings.QueueRunning = value == "1"; break;
                    case nameof(AppSettings.ConfirmBrowserDownloads): settings.ConfirmBrowserDownloads = value == "1"; break;
                    case nameof(AppSettings.NotifyOnCompletion): settings.NotifyOnCompletion = value == "1"; break;
                    case nameof(AppSettings.Theme) when Enum.TryParse<AppTheme>(value, out var theme): settings.Theme = theme; break;
                }
            }
        }
        return settings;
    }

    public void SaveSettings(AppSettings settings)
    {
        var values = new Dictionary<string, string>
        {
            [nameof(AppSettings.MaxConnections)] = settings.MaxConnections.ToString(CultureInfo.InvariantCulture),
            [nameof(AppSettings.MaxConcurrentDownloads)] = settings.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture),
            [nameof(AppSettings.SpeedLimitKBps)] = settings.SpeedLimitKBps.ToString(CultureInfo.InvariantCulture),
            [nameof(AppSettings.DownloadFolder)] = settings.DownloadFolder,
            [nameof(AppSettings.QueueRunning)] = settings.QueueRunning ? "1" : "0",
            [nameof(AppSettings.ConfirmBrowserDownloads)] = settings.ConfirmBrowserDownloads ? "1" : "0",
            [nameof(AppSettings.NotifyOnCompletion)] = settings.NotifyOnCompletion ? "1" : "0",
            [nameof(AppSettings.Theme)] = settings.Theme.ToString(),
        };
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var (key, value) in values)
                Upsert(key, value, tx);
            tx.Commit();
        }
    }

    /// <summary>A free-form value kept for the UI, such as the column layout; null when never saved.</summary>
    public string? GetPreference(string key)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT value FROM settings WHERE key = $k;");
            cmd.Parameters.AddWithValue("$k", PreferencePrefix + key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetPreference(string key, string value)
    {
        lock (_lock)
            Upsert(PreferencePrefix + key, value, null);
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
    }

    private void Upsert(string key, string value, SqliteTransaction? tx)
    {
        using var cmd = Command("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v;", tx);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private void InsertCategory(Category category, SqliteTransaction? tx)
    {
        using var cmd = Command("""
            INSERT INTO categories (key, name, extensions, folder, sort_order) VALUES ($key, $name, $ext, $folder, $order);
            SELECT last_insert_rowid();
            """, tx);
        BindCategory(cmd, category);
        cmd.Parameters.AddWithValue("$key", (object?)category.Key ?? DBNull.Value);
        category.Id = (long)cmd.ExecuteScalar()!;
    }

    private static void BindCategory(SqliteCommand cmd, Category c)
    {
        cmd.Parameters.AddWithValue("$name", c.Name);
        cmd.Parameters.AddWithValue("$ext", Categories.FormatExtensions(c.Extensions));
        cmd.Parameters.AddWithValue("$folder", c.Folder);
        cmd.Parameters.AddWithValue("$order", c.SortOrder);
    }

    private void WriteSegments(DownloadRecord record, SqliteTransaction tx)
    {
        using (var delete = Command("DELETE FROM segments WHERE download_id = $id;", tx))
        {
            delete.Parameters.AddWithValue("$id", record.Id);
            delete.ExecuteNonQuery();
        }
        using var insert = Command("INSERT INTO segments (download_id, idx, start, end_pos, written) VALUES ($id, $idx, $s, $e, $w);", tx);
        var pId = insert.Parameters.Add("$id", SqliteType.Integer);
        var pIdx = insert.Parameters.Add("$idx", SqliteType.Integer);
        var pStart = insert.Parameters.Add("$s", SqliteType.Integer);
        var pEnd = insert.Parameters.Add("$e", SqliteType.Integer);
        var pWritten = insert.Parameters.Add("$w", SqliteType.Integer);
        for (var i = 0; i < record.Segments.Count; i++)
        {
            var s = record.Segments[i];
            pId.Value = record.Id;
            pIdx.Value = i;
            pStart.Value = s.Start;
            pEnd.Value = s.End;
            pWritten.Value = s.Written;
            insert.ExecuteNonQuery();
        }
    }

    private static void Bind(SqliteCommand cmd, DownloadRecord r)
    {
        cmd.Parameters.AddWithValue("$url", r.Url);
        cmd.Parameters.AddWithValue("$dir", r.Directory);
        cmd.Parameters.AddWithValue("$name", (object?)r.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$size", (object?)r.TotalSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$etag", (object?)r.ETag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lm", (object?)r.LastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ranges", r.SupportsRanges ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", (int)r.Status);
        cmd.Parameters.AddWithValue("$error", (object?)r.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$order", r.SortOrder);
        cmd.Parameters.AddWithValue("$added", r.AddedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$completed", r.CompletedUtc is { } c ? c.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$context", r.Context is { IsEmpty: false } context ? RequestContexts.Protect(context) : DBNull.Value);
        cmd.Parameters.AddWithValue("$category", (object?)r.CategoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$auto", r.AutoCategory ? 1 : 0);
        cmd.Parameters.AddWithValue("$ctype", (object?)r.ContentType ?? DBNull.Value);
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private void Execute(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = Command(sql, tx);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = Command(sql);
        return cmd.ExecuteScalar();
    }

    private bool TableExists(string table)
    {
        using var cmd = Command("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $t;");
        cmd.Parameters.AddWithValue("$t", table);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private void AddColumnIfMissing(string table, string column, string type, SqliteTransaction tx)
    {
        using (var cmd = Command($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;", tx))
        {
            cmd.Parameters.AddWithValue("$column", column);
            if ((long)cmd.ExecuteScalar()! > 0)
                return;
        }
        Execute($"ALTER TABLE {table} ADD COLUMN {column} {type};", tx);
    }

    private static string? NullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long? NullableLong(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
