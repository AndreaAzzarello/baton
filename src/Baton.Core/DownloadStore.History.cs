using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Baton.Core.Duplicates;
using Baton.Core.Security;
using Microsoft.Data.Sqlite;

namespace Baton.Core;

/// <summary>
/// The history of removed downloads that lets a later add be recognised as a duplicate. An address is stored only as an
/// HMAC-SHA256 with a random key of this installation, kept encrypted with DPAPI: addresses are easy to guess (a well
/// known site), so a plain hash could be matched against a list of them, a keyed one cannot. No address, cookie or secret
/// is written, and the table is not part of an export.
/// </summary>
public sealed partial class DownloadStore : IDuplicateHistory
{
    private const string HistoryKeySetting = "DuplicateHistoryKey";
    private const int HistoryKeyLength = 32;

    /// <summary>The oldest an entry may be, and the most entries kept.</summary>
    public static readonly TimeSpan HistoryMaxAge = TimeSpan.FromDays(365);

    public const int HistoryMaxEntries = 5000;

    private byte[]? _historyKey;

    private void CreateHistoryTable(SqliteTransaction tx) => Execute("""
        CREATE TABLE IF NOT EXISTS download_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            url_hash TEXT NOT NULL,
            file_name TEXT NOT NULL,
            path TEXT NOT NULL,
            size INTEGER NOT NULL,
            etag TEXT,
            last_modified TEXT,
            added_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_history_url ON download_history(url_hash);
        CREATE INDEX IF NOT EXISTS ix_history_size ON download_history(size);
        """, tx);

    /// <summary>The key of this installation. A missing or unreadable one (another Windows profile) is replaced, which empties the history: its hashes could not match any more.</summary>
    private byte[] HistoryKey()
    {
        if (_historyKey is not null)
            return _historyKey;
        using (var cmd = Command("SELECT value FROM settings WHERE key = $k;"))
        {
            cmd.Parameters.AddWithValue("$k", HistoryKeySetting);
            if (Secrets.Unprotect(cmd.ExecuteScalar() as string) is { } stored)
            {
                try
                {
                    var key = Convert.FromBase64String(stored);
                    if (key.Length == HistoryKeyLength)
                        return _historyKey = key;
                }
                catch (FormatException)
                {
                }
            }
        }
        using var tx = _db.BeginTransaction();
        var created = NewHistoryKey(tx);
        tx.Commit();
        return created;
    }

    /// <summary>A fresh random key; the entries made with the old one are deleted together with it.</summary>
    private byte[] NewHistoryKey(SqliteTransaction tx)
    {
        var key = RandomNumberGenerator.GetBytes(HistoryKeyLength);
        Upsert(HistoryKeySetting, Secrets.Protect(Convert.ToBase64String(key)), tx);
        Execute("DELETE FROM download_history;", tx);
        return _historyKey = key;
    }

    private string HashUrl(string normalizedUrl) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(HistoryKey(), Encoding.UTF8.GetBytes(normalizedUrl)));

    /// <summary>Remembers a removed download (its address given already normalized, see <see cref="DuplicateDetector.NormalizeUrl"/>).</summary>
    public void AddHistory(string normalizedUrl, string fileName, string path, long size, string? etag, string? lastModified, DateTime nowUtc)
    {
        lock (_lock)
        {
            var hash = HashUrl(normalizedUrl);
            using var tx = _db.BeginTransaction();
            using (var delete = Command("DELETE FROM download_history WHERE url_hash = $h AND path = $p;", tx))
            {
                delete.Parameters.AddWithValue("$h", hash);
                delete.Parameters.AddWithValue("$p", path);
                delete.ExecuteNonQuery();
            }
            using var insert = Command("""
                INSERT INTO download_history (url_hash, file_name, path, size, etag, last_modified, added_utc)
                VALUES ($h, $name, $p, $size, $etag, $lm, $added);
                """, tx);
            insert.Parameters.AddWithValue("$h", hash);
            insert.Parameters.AddWithValue("$name", fileName);
            insert.Parameters.AddWithValue("$p", path);
            insert.Parameters.AddWithValue("$size", size);
            insert.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
            insert.Parameters.AddWithValue("$lm", (object?)lastModified ?? DBNull.Value);
            insert.Parameters.AddWithValue("$added", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public IReadOnlyList<HistoryEntry> FindByUrl(string normalizedUrl)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT url_hash, file_name, path, size, etag, last_modified, added_utc FROM download_history WHERE url_hash = $h;");
            cmd.Parameters.AddWithValue("$h", HashUrl(normalizedUrl));
            return ReadHistory(cmd);
        }
    }

    public IReadOnlyList<HistoryEntry> FindBySize(long size)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT url_hash, file_name, path, size, etag, last_modified, added_utc FROM download_history WHERE size = $s;");
            cmd.Parameters.AddWithValue("$s", size);
            return ReadHistory(cmd);
        }
    }

    public int HistoryCount()
    {
        lock (_lock)
            return (int)(long)Scalar("SELECT COUNT(*) FROM download_history;")!;
    }

    private static List<HistoryEntry> ReadHistory(SqliteCommand cmd)
    {
        var entries = new List<HistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new HistoryEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return entries;
    }

    /// <summary>Forgets what is remembered of these addresses (already normalized): "Clear completed" with the box to forget them.</summary>
    public void ForgetHistory(IEnumerable<string> normalizedUrls)
    {
        lock (_lock)
        {
            var hashes = normalizedUrls.Select(HashUrl).ToList(); // the key may need creating: before the transaction
            using var tx = _db.BeginTransaction();
            using var cmd = Command("DELETE FROM download_history WHERE url_hash = $h;", tx);
            var hash = cmd.Parameters.Add("$h", SqliteType.Text);
            foreach (var value in hashes)
            {
                hash.Value = value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Empties the history and replaces the key, so nothing made before can be recognised, even from a copy of the old file.</summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            NewHistoryKey(tx);
            tx.Commit();
        }
    }

    /// <summary>
    /// Drops the entries whose file is gone, those older than <see cref="HistoryMaxAge"/> and whatever passes
    /// <see cref="HistoryMaxEntries"/> (the oldest first). Returns how many went.
    /// </summary>
    public int PruneHistory(DateTime nowUtc, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        List<(long Id, string Path)> rows = [];
        lock (_lock)
        {
            using var cmd = Command("SELECT id, path FROM download_history;");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        // Outside the lock: a slow or disconnected drive must not stop the list.
        var missing = rows.Where(r => !fileExists(r.Path)).Select(r => r.Id).ToList();
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var removed = 0;
            using (var delete = Command("DELETE FROM download_history WHERE id = $id;", tx))
            {
                var id = delete.Parameters.Add("$id", SqliteType.Integer);
                foreach (var value in missing)
                {
                    id.Value = value;
                    removed += delete.ExecuteNonQuery();
                }
            }
            using (var old = Command("DELETE FROM download_history WHERE added_utc < $limit;", tx))
            {
                old.Parameters.AddWithValue("$limit", (nowUtc - HistoryMaxAge).ToString("O", CultureInfo.InvariantCulture));
                removed += old.ExecuteNonQuery();
            }
            using (var excess = Command($"DELETE FROM download_history WHERE id NOT IN (SELECT id FROM download_history ORDER BY added_utc DESC, id DESC LIMIT {HistoryMaxEntries});", tx))
                removed += excess.ExecuteNonQuery();
            tx.Commit();
            return removed;
        }
    }
}
