using Microsoft.Data.Sqlite;

namespace Baton.Core.Setup;

public enum MigrationOutcome
{
    /// <summary>Nothing to move: Baton already has its data, or there is no DownloadManager data.</summary>
    NothingToDo,
    Migrated,
    /// <summary>The copy failed: the old folder is untouched and stays in use until a later start succeeds.</summary>
    Failed,
}

/// <summary>
/// Up to 1.1 the app was called DownloadManager and kept its data in %LOCALAPPDATA%\DownloadManager. The first start of
/// Baton copies that folder into its own: the database through SQLite's backup (a consistent copy, checked before use),
/// everything else (logs, notification secret, update state, database backups) as files. The copy is prepared in a
/// staging folder and the database is put in place last, so an interrupted copy is simply done again at the next
/// start. The old folder is never changed (apart from a note saying where the data went): nothing can be lost, and a
/// return to the previous version still finds its data.
/// </summary>
public static class LegacyData
{
    public const string DatabaseName = "downloads.db";
    public const string MarkerName = "migrated-to-baton.txt";

    public static (MigrationOutcome Outcome, string? Error) Migrate(string? legacy, string target)
    {
        var targetDb = Path.Combine(target, DatabaseName);
        if (File.Exists(targetDb) || legacy is null || !File.Exists(Path.Combine(legacy, DatabaseName)))
            return (MigrationOutcome.NothingToDo, null);
        var staging = target + ".migrating";
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true); // left by an interrupted attempt
            CopyFiles(legacy, staging);
            var stagedDb = Path.Combine(staging, DatabaseName);
            CopyDatabase(Path.Combine(legacy, DatabaseName), stagedDb);

            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Where(f => f != stagedDb))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(staging, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(file, destination, overwrite: true);
            }
            File.Move(stagedDb, targetDb); // last: from here on Baton has its data
            Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Removed at the next attempt.
            }
            return (MigrationOutcome.Failed, ex.Message);
        }
        try
        {
            File.WriteAllText(Path.Combine(legacy, MarkerName),
                $"Copied to {target} on {DateTime.Now:yyyy-MM-dd HH:mm} by Baton. This folder is no longer used: delete it once Baton works.\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a note for the user.
        }
        return (MigrationOutcome.Migrated, null);
    }

    /// <summary>Everything but the database (copied apart), the old browser host files and leftovers of an update.</summary>
    private static void CopyFiles(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(DatabaseName, StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(Platform.SystemRegistration.LegacyHostName, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || name.Equals(MarkerName, StringComparison.OrdinalIgnoreCase))
                continue;
            var destination = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    /// <summary>A consistent copy through SQLite's backup, then checked: same schema version, same downloads, no damage.</summary>
    private static void CopyDatabase(string source, string destination)
    {
        int version, downloads;
        using (var from = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False"))
        using (var to = new SqliteConnection($"Data Source={destination};Pooling=False"))
        {
            from.Open();
            to.Open();
            version = Scalar(from, "PRAGMA user_version;");
            downloads = Scalar(from, "SELECT count(*) FROM downloads;");
            from.BackupDatabase(to);
        }
        using var copy = new SqliteConnection($"Data Source={destination};Mode=ReadOnly;Pooling=False");
        copy.Open();
        using var check = copy.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        if (check.ExecuteScalar() as string != "ok" || Scalar(copy, "PRAGMA user_version;") != version
            || Scalar(copy, "SELECT count(*) FROM downloads;") != downloads)
            throw new InvalidDataException("The copy of the database does not match the original.");
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
