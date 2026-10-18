using System.Globalization;
using DownloadManager.Core.Security;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Core;

/// <summary>SQLite persistence for downloads, their segments, categories and app settings.</summary>
public sealed partial class DownloadStore : IDisposable
{
    /// <summary>Version written by this build. Every older version is upgraded by <see cref="Migrate"/>.</summary>
    public const int SchemaVersion = 7;

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

    /// <summary>
    /// For the previous version put back after an update that did not start: if the new one already migrated the database
    /// to a schema this version cannot open, the copy taken before that migration is put back and the migrated database is
    /// kept beside it as ".v&lt;N&gt;.newer". True when the copy was restored.
    /// </summary>
    public static bool RestoreBackupForThisVersion(string databasePath)
    {
        if (!File.Exists(databasePath))
            return false;
        long version;
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            version = (long)command.ExecuteScalar()!;
        }
        var backup = $"{databasePath}.v{SchemaVersion}.bak";
        if (version <= SchemaVersion || !File.Exists(backup))
            return false;
        var newer = $"{databasePath}.v{version}.newer";
        File.Move(databasePath, newer, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(databasePath + suffix))
                File.Move(databasePath + suffix, newer + suffix, overwrite: true);
        }
        File.Copy(backup, databasePath);
        return true;
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
        if (version < 3)
            MigrateTo3(tx);
        if (version < 4)
            MigrateTo4(tx);
        if (version < 5)
            MigrateTo5(tx);
        if (version < 6)
            MigrateTo6(tx);
        if (version < 7)
            MigrateTo7(tx);
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

    /// <summary>
    /// Named queues: the main queue takes over the limit and the started/stopped state of the single queue of
    /// earlier versions, and every existing download joins it.
    /// </summary>
    private void MigrateTo3(SqliteTransaction tx)
    {
        Execute("""
            CREATE TABLE queues (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                key TEXT UNIQUE,
                name TEXT NOT NULL,
                max_concurrent INTEGER NOT NULL,
                running INTEGER NOT NULL,
                schedule TEXT NOT NULL,
                retry_count INTEGER NOT NULL,
                retry_delay_s INTEGER NOT NULL,
                quota_mb INTEGER NOT NULL,
                end_action TEXT NOT NULL,
                sort_order INTEGER NOT NULL);
            """, tx);
        AddColumnIfMissing("downloads", "queue_id", "INTEGER", tx);
        AddColumnIfMissing("downloads", "failures", "INTEGER NOT NULL DEFAULT 0", tx);
        AddColumnIfMissing("downloads", "retry_at", "TEXT", tx);

        string? Setting(string key)
        {
            using var cmd = Command("SELECT value FROM settings WHERE key = $k;", tx);
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
        var main = new DownloadQueue
        {
            Key = DownloadQueue.MainKey,
            Name = Resources.Strings.QueueMainName,
            MaxConcurrent = int.TryParse(Setting(nameof(AppSettings.MaxConcurrentDownloads)), out var limit) ? Math.Clamp(limit, 1, 20) : 3,
            Running = Setting(nameof(AppSettings.QueueRunning)) != "0",
        };
        InsertQueue(main, tx);
        // The old limit now belongs to the main queue; the overall limit across all queues starts higher.
        using (var total = Command("UPDATE settings SET value = '10' WHERE key = $k AND CAST(value AS INTEGER) < 10;", tx))
        {
            total.Parameters.AddWithValue("$k", nameof(AppSettings.MaxConcurrentDownloads));
            total.ExecuteNonQuery();
        }
        using var update = Command("UPDATE downloads SET queue_id = $q;", tx);
        update.Parameters.AddWithValue("$q", main.Id);
        update.ExecuteNonQuery();
    }

    /// <summary>Saved logins (password encrypted with DPAPI) and certificates the user chose to trust.</summary>
    private void MigrateTo4(SqliteTransaction tx)
    {
        Execute("""
            CREATE TABLE credentials (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                host TEXT NOT NULL,
                port INTEGER,
                username TEXT NOT NULL,
                secret TEXT NOT NULL,
                windows_account INTEGER NOT NULL DEFAULT 0,
                added_utc TEXT NOT NULL);
            CREATE TABLE trusted_certificates (
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                fingerprint TEXT NOT NULL,
                subject TEXT NOT NULL,
                added_utc TEXT NOT NULL,
                PRIMARY KEY (host, port, fingerprint));
            """, tx);
    }

    /// <summary>Video streams (HLS, DASH): the chosen quality and what the download is put together from.</summary>
    private void MigrateTo5(SqliteTransaction tx)
    {
        AddColumnIfMissing("downloads", "media", "TEXT", tx);
    }

    /// <summary>
    /// Grabber projects (settings as JSON, browser cookies encrypted with DPAPI, schedule) and what each run saw of
    /// every file, for synchronization.
    /// </summary>
    private void MigrateTo6(SqliteTransaction tx)
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS grabber_projects (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                start_url TEXT NOT NULL,
                settings TEXT NOT NULL,
                schedule TEXT NOT NULL,
                secrets TEXT,
                added_utc TEXT NOT NULL,
                last_run_utc TEXT,
                last_result TEXT);
            CREATE TABLE IF NOT EXISTS grabber_files (
                project_id INTEGER NOT NULL REFERENCES grabber_projects(id) ON DELETE CASCADE,
                url TEXT NOT NULL,
                local_path TEXT,
                etag TEXT,
                last_modified TEXT,
                size INTEGER,
                content_type TEXT,
                source_page TEXT,
                content_hash TEXT,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                removed_utc TEXT,
                PRIMARY KEY (project_id, url));
            """, tx);
    }

    /// <summary>
    /// Checks after the download (hash, antivirus) on downloads; on the grabber's files, the download each one was sent
    /// as and whether it completed, so a file whose download failed or was removed is offered again. Files remembered
    /// by version 6 were sent already and count as obtained.
    /// </summary>
    private void MigrateTo7(SqliteTransaction tx)
    {
        AddColumnIfMissing("downloads", "post_check", "TEXT", tx);
        AddColumnIfMissing("grabber_files", "download_id", "INTEGER", tx);
        AddColumnIfMissing("grabber_files", "obtained", "INTEGER NOT NULL DEFAULT 1", tx);
        // Cookies are now forgotten after each run by default; a scheduled project that already used them would lose its
        // access silently after the upgrade, so it keeps them (the option can be changed in its wizard).
        Execute("""
            UPDATE grabber_projects SET settings = json_set(settings, '$.forgetCookiesAfterRun', json('false'))
            WHERE secrets IS NOT NULL AND json_valid(settings) AND json_valid(schedule) AND json_extract(schedule, '$.enabled') = 1;
            """, tx);
    }

    public List<SiteCredential> LoadCredentials()
    {
        lock (_lock)
        {
            var result = new List<SiteCredential>();
            using var cmd = Command("SELECT id, host, port, username, secret, windows_account, added_utc FROM credentials ORDER BY host, id;");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SiteCredential
                {
                    Id = reader.GetInt64(0),
                    Host = reader.GetString(1),
                    Port = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    Username = reader.GetString(3),
                    // A password that cannot be decrypted (another Windows profile) is treated as missing.
                    Password = Secrets.Unprotect(reader.GetString(4)) ?? "",
                    UseWindowsAccount = reader.GetInt64(5) != 0,
                    AddedUtc = ParseDate(reader.GetString(6)),
                });
            }
            return result;
        }
    }

    public void SaveCredential(SiteCredential credential)
    {
        lock (_lock)
        {
            using var cmd = Command(credential.Id == 0
                ? """
                  INSERT INTO credentials (host, port, username, secret, windows_account, added_utc) VALUES ($host, $port, $user, $secret, $windows, $added);
                  SELECT last_insert_rowid();
                  """
                : "UPDATE credentials SET host = $host, port = $port, username = $user, secret = $secret, windows_account = $windows WHERE id = $id; SELECT $id;");
            cmd.Parameters.AddWithValue("$host", credential.Host);
            cmd.Parameters.AddWithValue("$port", (object?)credential.Port ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$user", credential.Username);
            cmd.Parameters.AddWithValue("$secret", Secrets.Protect(credential.Password));
            cmd.Parameters.AddWithValue("$windows", credential.UseWindowsAccount ? 1 : 0);
            cmd.Parameters.AddWithValue("$added", credential.AddedUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$id", credential.Id);
            credential.Id = (long)cmd.ExecuteScalar()!;
        }
    }

    public void DeleteCredential(long id)
    {
        lock (_lock)
        {
            using var cmd = Command("DELETE FROM credentials WHERE id = $id;");
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public List<TrustedCertificate> LoadTrustedCertificates()
    {
        lock (_lock)
        {
            var result = new List<TrustedCertificate>();
            using var cmd = Command("SELECT host, port, fingerprint, subject, added_utc FROM trusted_certificates ORDER BY host;");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(new TrustedCertificate(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), ParseDate(reader.GetString(4))));
            return result;
        }
    }

    public void SaveTrustedCertificate(TrustedCertificate certificate)
    {
        lock (_lock)
        {
            using var cmd = Command("INSERT OR REPLACE INTO trusted_certificates (host, port, fingerprint, subject, added_utc) VALUES ($h, $p, $f, $s, $a);");
            cmd.Parameters.AddWithValue("$h", certificate.Host);
            cmd.Parameters.AddWithValue("$p", certificate.Port);
            cmd.Parameters.AddWithValue("$f", certificate.Fingerprint);
            cmd.Parameters.AddWithValue("$s", certificate.Subject);
            cmd.Parameters.AddWithValue("$a", certificate.AddedUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteTrustedCertificate(TrustedCertificate certificate)
    {
        lock (_lock)
        {
            using var cmd = Command("DELETE FROM trusted_certificates WHERE host = $h AND port = $p AND fingerprint = $f;");
            cmd.Parameters.AddWithValue("$h", certificate.Host);
            cmd.Parameters.AddWithValue("$p", certificate.Port);
            cmd.Parameters.AddWithValue("$f", certificate.Fingerprint);
            cmd.ExecuteNonQuery();
        }
    }

    public List<DownloadQueue> LoadQueues()
    {
        lock (_lock)
        {
            var result = new List<DownloadQueue>();
            using var cmd = Command("""
                SELECT id, key, name, max_concurrent, running, schedule, retry_count, retry_delay_s, quota_mb, end_action, sort_order
                FROM queues ORDER BY sort_order, id;
                """);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new DownloadQueue
                {
                    Id = reader.GetInt64(0),
                    Key = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Name = reader.GetString(2),
                    MaxConcurrent = reader.GetInt32(3),
                    Running = reader.GetInt64(4) != 0,
                    Schedule = DownloadQueue.DeserializeSchedule(reader.GetString(5)),
                    RetryCount = reader.GetInt32(6),
                    RetryDelaySeconds = reader.GetInt32(7),
                    QuotaMBPerHour = reader.GetInt64(8),
                    EndAction = Enum.TryParse<EndAction>(reader.GetString(9), out var action) ? action : EndAction.None,
                    SortOrder = reader.GetInt32(10),
                });
            }
            return result;
        }
    }

    /// <summary>Inserts a new queue (Id 0) or updates an existing one.</summary>
    public void SaveQueue(DownloadQueue queue)
    {
        lock (_lock)
        {
            if (queue.Id == 0)
            {
                InsertQueue(queue, null);
                return;
            }
            using var cmd = Command("""
                UPDATE queues SET name = $name, max_concurrent = $max, running = $running, schedule = $schedule, retry_count = $retries,
                    retry_delay_s = $delay, quota_mb = $quota, end_action = $action, sort_order = $order
                WHERE id = $id;
                """);
            BindQueue(cmd, queue);
            cmd.Parameters.AddWithValue("$id", queue.Id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes a queue; its downloads move to <paramref name="moveTo"/>.</summary>
    public void DeleteQueue(long id, long moveTo)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var move = Command("UPDATE downloads SET queue_id = $to WHERE queue_id = $id;", tx))
            {
                move.Parameters.AddWithValue("$to", moveTo);
                move.Parameters.AddWithValue("$id", id);
                move.ExecuteNonQuery();
            }
            using (var delete = Command("DELETE FROM queues WHERE id = $id;", tx))
            {
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private void InsertQueue(DownloadQueue queue, SqliteTransaction? tx)
    {
        using var cmd = Command("""
            INSERT INTO queues (key, name, max_concurrent, running, schedule, retry_count, retry_delay_s, quota_mb, end_action, sort_order)
            VALUES ($key, $name, $max, $running, $schedule, $retries, $delay, $quota, $action, $order);
            SELECT last_insert_rowid();
            """, tx);
        BindQueue(cmd, queue);
        cmd.Parameters.AddWithValue("$key", (object?)queue.Key ?? DBNull.Value);
        queue.Id = (long)cmd.ExecuteScalar()!;
    }

    private static void BindQueue(SqliteCommand cmd, DownloadQueue q)
    {
        cmd.Parameters.AddWithValue("$name", q.Name);
        cmd.Parameters.AddWithValue("$max", q.MaxConcurrent);
        cmd.Parameters.AddWithValue("$running", q.Running ? 1 : 0);
        cmd.Parameters.AddWithValue("$schedule", DownloadQueue.SerializeSchedule(q.Schedule));
        cmd.Parameters.AddWithValue("$retries", q.RetryCount);
        cmd.Parameters.AddWithValue("$delay", q.RetryDelaySeconds);
        cmd.Parameters.AddWithValue("$quota", q.QuotaMBPerHour);
        cmd.Parameters.AddWithValue("$action", q.EndAction.ToString());
        cmd.Parameters.AddWithValue("$order", q.SortOrder);
    }

    public long Insert(DownloadRecord record)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                INSERT INTO downloads (url, directory, file_name, total_size, etag, last_modified, supports_ranges, status, error,
                                       sort_order, added_utc, completed_utc, request_context, category_id, auto_category, content_type,
                                       queue_id, failures, retry_at, media, post_check)
                VALUES ($url, $dir, $name, $size, $etag, $lm, $ranges, $status, $error,
                        $order, $added, $completed, $context, $category, $auto, $ctype, $queue, $failures, $retry, $media, $check);
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
                    category_id = $category, auto_category = $auto, content_type = $ctype,
                    queue_id = $queue, failures = $failures, retry_at = $retry, media = $media, post_check = $check
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
                        QueueId = NullableLong(reader, "queue_id"),
                        Failures = reader.GetInt32(reader.GetOrdinal("failures")),
                        RetryAtUtc = NullableString(reader, "retry_at") is { } retry ? ParseDate(retry) : null,
                        Media = ReadMedia(NullableString(reader, "media")),
                        Check = ReadJson<PostDownload.PostCheck>(NullableString(reader, "post_check")),
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
                    case nameof(AppSettings.GlobalQuotaMBPerHour): settings.GlobalQuotaMBPerHour = long.Parse(value, CultureInfo.InvariantCulture); break;
                    case nameof(AppSettings.CloseToTray): settings.CloseToTray = value == "1"; break;
                    case nameof(AppSettings.ProxyMode) when Enum.TryParse<ProxyMode>(value, out var mode): settings.ProxyMode = mode; break;
                    case nameof(AppSettings.ProxyType) when Enum.TryParse<ProxyType>(value, out var type): settings.ProxyType = type; break;
                    case nameof(AppSettings.ProxyHost): settings.ProxyHost = value; break;
                    case nameof(AppSettings.ProxyPort): settings.ProxyPort = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case nameof(AppSettings.ProxyUser): settings.ProxyUser = value; break;
                    case nameof(AppSettings.ProxyPassword): settings.ProxyPassword = Secrets.Unprotect(value) ?? ""; break;
                    case nameof(AppSettings.ProxyBypass): settings.ProxyBypass = value; break;
                    case nameof(AppSettings.PacUrl): settings.PacUrl = value; break;
                    case nameof(AppSettings.FtpActiveMode): settings.FtpActiveMode = value == "1"; break;
                    case nameof(AppSettings.TimeoutSeconds): settings.TimeoutSeconds = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case nameof(AppSettings.MaxConnectionsPerServer): settings.MaxConnectionsPerServer = int.Parse(value, CultureInfo.InvariantCulture); break;
                    // The default of an earlier version follows the app's version instead of staying behind.
                    case nameof(AppSettings.UserAgent): settings.UserAgent = value.StartsWith("DownloadManager/0.", StringComparison.Ordinal) ? AppSettings.DefaultUserAgent : value; break;
                    case nameof(AppSettings.FfmpegPath): settings.FfmpegPath = value; break;
                    case nameof(AppSettings.ComputeHashOnCompletion): settings.ComputeHashOnCompletion = value == "1"; break;
                    case nameof(AppSettings.HashAlgorithm) when PostDownload.FileHash.Algorithms.Contains(value): settings.HashAlgorithm = value; break;
                    case nameof(AppSettings.ScanOnCompletion): settings.ScanOnCompletion = value == "1"; break;
                    case nameof(AppSettings.ScannerPath): settings.ScannerPath = value; break;
                    case nameof(AppSettings.ScannerArguments): settings.ScannerArguments = value; break;
                    case nameof(AppSettings.ScanThreatCodes): settings.ScanThreatCodes = value; break;
                    case nameof(AppSettings.ScanTimeoutSeconds) when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scanTimeout):
                        settings.ScanTimeoutSeconds = scanTimeout; break;
                    case nameof(AppSettings.UpdateCheckEnabled): settings.UpdateCheckEnabled = value == "1"; break;
                    case nameof(AppSettings.UpdateUrl): settings.UpdateUrl = value; break;
                    case nameof(AppSettings.DesktopRelocationAsked): settings.DesktopRelocationAsked = value == "1"; break;
                    case nameof(AppSettings.ToolbarItems): settings.ToolbarItems = value; break;
                    case nameof(AppSettings.ToolbarSmallIcons): settings.ToolbarSmallIcons = value == "1"; break;
                    case nameof(AppSettings.ToolbarShowText): settings.ToolbarShowText = value == "1"; break;
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
            [nameof(AppSettings.GlobalQuotaMBPerHour)] = settings.GlobalQuotaMBPerHour.ToString(CultureInfo.InvariantCulture),
            [nameof(AppSettings.CloseToTray)] = settings.CloseToTray ? "1" : "0",
            [nameof(AppSettings.ProxyMode)] = settings.ProxyMode.ToString(),
            [nameof(AppSettings.ProxyType)] = settings.ProxyType.ToString(),
            [nameof(AppSettings.ProxyHost)] = settings.ProxyHost,
            [nameof(AppSettings.ProxyPort)] = settings.ProxyPort.ToString(CultureInfo.InvariantCulture),
            [nameof(AppSettings.ProxyUser)] = settings.ProxyUser,
            // Never stored in clear: encrypted with the Windows user's key.
            [nameof(AppSettings.ProxyPassword)] = settings.ProxyPassword.Length == 0 ? "" : Secrets.Protect(settings.ProxyPassword),
            [nameof(AppSettings.ProxyBypass)] = settings.ProxyBypass,
            [nameof(AppSettings.PacUrl)] = settings.PacUrl,
            [nameof(AppSettings.FtpActiveMode)] = settings.FtpActiveMode ? "1" : "0",
            [nameof(AppSettings.TimeoutSeconds)] = settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            [nameof(AppSettings.MaxConnectionsPerServer)] = settings.MaxConnectionsPerServer.ToString(CultureInfo.InvariantCulture),
            [nameof(AppSettings.UserAgent)] = settings.UserAgent,
            [nameof(AppSettings.FfmpegPath)] = settings.FfmpegPath,
            [nameof(AppSettings.ComputeHashOnCompletion)] = settings.ComputeHashOnCompletion ? "1" : "0",
            [nameof(AppSettings.HashAlgorithm)] = settings.HashAlgorithm,
            [nameof(AppSettings.ScanOnCompletion)] = settings.ScanOnCompletion ? "1" : "0",
            [nameof(AppSettings.ScannerPath)] = settings.ScannerPath,
            [nameof(AppSettings.ScannerArguments)] = settings.ScannerArguments,
            [nameof(AppSettings.ScanThreatCodes)] = settings.ScanThreatCodes,
            [nameof(AppSettings.ScanTimeoutSeconds)] = settings.ScanTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            [nameof(AppSettings.UpdateCheckEnabled)] = settings.UpdateCheckEnabled ? "1" : "0",
            [nameof(AppSettings.UpdateUrl)] = settings.UpdateUrl,
            [nameof(AppSettings.DesktopRelocationAsked)] = settings.DesktopRelocationAsked ? "1" : "0",
            [nameof(AppSettings.ToolbarItems)] = settings.ToolbarItems,
            [nameof(AppSettings.ToolbarSmallIcons)] = settings.ToolbarSmallIcons ? "1" : "0",
            [nameof(AppSettings.ToolbarShowText)] = settings.ToolbarShowText ? "1" : "0",
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
        cmd.Parameters.AddWithValue("$queue", (object?)r.QueueId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$failures", r.Failures);
        cmd.Parameters.AddWithValue("$retry", r.RetryAtUtc is { } retry ? retry.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$media", r.Media is { } media ? System.Text.Json.JsonSerializer.Serialize(media, MediaJson) : DBNull.Value);
        cmd.Parameters.AddWithValue("$check", r.Check is { } check ? System.Text.Json.JsonSerializer.Serialize(check, MediaJson) : DBNull.Value);
    }

    private static readonly System.Text.Json.JsonSerializerOptions MediaJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static T? ReadJson<T>(string? json) where T : class
    {
        try
        {
            return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<T>(json, MediaJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static MediaSource? ReadMedia(string? json)
    {
        try
        {
            return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<MediaSource>(json, MediaJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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
