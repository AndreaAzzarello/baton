using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Core;

/// <summary>SQLite persistence for downloads, their segments and app settings.</summary>
public sealed class DownloadStore : IDisposable
{
    private readonly object _lock = new();
    private readonly SqliteConnection _db;

    public DownloadStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
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
            """);
        AddColumnIfMissing("downloads", "request_context", "TEXT");
    }

    public long Insert(DownloadRecord record)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                INSERT INTO downloads (url, directory, file_name, total_size, etag, last_modified, supports_ranges,
                                       status, error, sort_order, added_utc, completed_utc, request_context)
                VALUES ($url, $dir, $name, $size, $etag, $lm, $ranges, $status, $error, $order, $added, $completed, $context);
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
                    sort_order = $order, added_utc = $added, completed_utc = $completed, request_context = $context
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
                        TotalSize = reader.IsDBNull(reader.GetOrdinal("total_size")) ? null : reader.GetInt64(reader.GetOrdinal("total_size")),
                        ETag = NullableString(reader, "etag"),
                        LastModified = NullableString(reader, "last_modified"),
                        SupportsRanges = reader.GetInt64(reader.GetOrdinal("supports_ranges")) != 0,
                        Status = (DownloadStatus)reader.GetInt32(reader.GetOrdinal("status")),
                        Error = NullableString(reader, "error"),
                        SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order")),
                        AddedUtc = ParseDate(reader.GetString(reader.GetOrdinal("added_utc"))),
                        CompletedUtc = NullableString(reader, "completed_utc") is { } c ? ParseDate(c) : null,
                        Context = RequestContexts.Unprotect(NullableString(reader, "request_context")),
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
        };
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var (key, value) in values)
            {
                using var cmd = Command("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v;", tx);
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
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
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Upgrades a database created by an earlier version.</summary>
    private void AddColumnIfMissing(string table, string column, string type)
    {
        using (var cmd = Command($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;"))
        {
            cmd.Parameters.AddWithValue("$column", column);
            if ((long)cmd.ExecuteScalar()! > 0)
                return;
        }
        Execute($"ALTER TABLE {table} ADD COLUMN {column} {type};");
    }

    private static string? NullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
