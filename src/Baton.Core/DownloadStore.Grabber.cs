using System.Globalization;
using System.Text.Json;
using Baton.Core.Grabber;
using Baton.Core.Security;

namespace Baton.Core;

/// <summary>Grabber projects and the files each one has seen (schema version 6).</summary>
public sealed partial class DownloadStore
{
    private static readonly JsonSerializerOptions CookieJson = new(JsonSerializerDefaults.Web);

    public List<GrabberProject> LoadGrabberProjects()
    {
        lock (_lock)
        {
            var result = new List<GrabberProject>();
            using var cmd = Command("SELECT id, name, start_url, settings, schedule, secrets, added_utc, last_run_utc, last_result FROM grabber_projects ORDER BY id;");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var project = GrabberProject.DeserializeSettings(reader.GetString(3));
                project.Id = reader.GetInt64(0);
                project.Name = reader.GetString(1);
                project.StartUrl = reader.GetString(2);
                project.Schedule = DownloadQueue.DeserializeSchedule(reader.GetString(4));
                project.Cookies = ReadCookies(reader.IsDBNull(5) ? null : reader.GetString(5));
                project.AddedUtc = ParseDate(reader.GetString(6));
                project.LastRunUtc = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7));
                project.LastResult = reader.IsDBNull(8) ? null : reader.GetString(8);
                result.Add(project);
            }
            return result;
        }
    }

    /// <summary>Inserts a new project (Id 0) or updates an existing one. Cookies are encrypted with DPAPI.</summary>
    public void SaveGrabberProject(GrabberProject project)
    {
        lock (_lock)
        {
            using var cmd = Command(project.Id == 0
                ? """
                  INSERT INTO grabber_projects (name, start_url, settings, schedule, secrets, added_utc, last_run_utc, last_result)
                  VALUES ($name, $url, $settings, $schedule, $secrets, $added, $run, $result);
                  SELECT last_insert_rowid();
                  """
                : """
                  UPDATE grabber_projects SET name = $name, start_url = $url, settings = $settings, schedule = $schedule, secrets = $secrets,
                      last_run_utc = $run, last_result = $result
                  WHERE id = $id;
                  SELECT $id;
                  """);
            cmd.Parameters.AddWithValue("$name", project.Name);
            cmd.Parameters.AddWithValue("$url", project.StartUrl);
            cmd.Parameters.AddWithValue("$settings", project.SerializeSettings());
            cmd.Parameters.AddWithValue("$schedule", DownloadQueue.SerializeSchedule(project.Schedule));
            cmd.Parameters.AddWithValue("$secrets", project.Cookies.Count > 0 ? Secrets.Protect(JsonSerializer.Serialize(project.Cookies, CookieJson)) : DBNull.Value);
            cmd.Parameters.AddWithValue("$added", project.AddedUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$run", project.LastRunUtc is { } run ? run.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$result", (object?)project.LastResult ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", project.Id);
            project.Id = (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>Deletes a project and what it remembered of its files (never the files themselves).</summary>
    public void DeleteGrabberProject(long id)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var files = Command("DELETE FROM grabber_files WHERE project_id = $id;", tx))
            {
                files.Parameters.AddWithValue("$id", id);
                files.ExecuteNonQuery();
            }
            using (var project = Command("DELETE FROM grabber_projects WHERE id = $id;", tx))
            {
                project.Parameters.AddWithValue("$id", id);
                project.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public Dictionary<string, GrabberFileState> LoadGrabberFiles(long projectId)
    {
        lock (_lock)
        {
            var result = new Dictionary<string, GrabberFileState>(StringComparer.Ordinal);
            using var cmd = Command("""
                SELECT url, local_path, etag, last_modified, size, content_type, source_page, content_hash, first_seen_utc, last_seen_utc, removed_utc,
                       download_id, obtained
                FROM grabber_files WHERE project_id = $id;
                """);
            cmd.Parameters.AddWithValue("$id", projectId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                var state = new GrabberFileState
                {
                    Url = reader.GetString(0),
                    LocalPath = Text(1),
                    ETag = Text(2),
                    LastModified = Text(3),
                    Size = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    ContentType = Text(5),
                    SourcePage = Text(6),
                    ContentHash = Text(7),
                    FirstSeenUtc = ParseDate(reader.GetString(8)),
                    LastSeenUtc = ParseDate(reader.GetString(9)),
                    RemovedUtc = Text(10) is { } removed ? ParseDate(removed) : null,
                    DownloadId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    Obtained = reader.GetInt64(12) != 0,
                };
                result[state.Url] = state;
            }
            return result;
        }
    }

    /// <summary>Adds or replaces what a project knows about some files, in one transaction.</summary>
    public void SaveGrabberFiles(long projectId, IEnumerable<GrabberFileState> files)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                INSERT INTO grabber_files (project_id, url, local_path, etag, last_modified, size, content_type, source_page, content_hash,
                                           first_seen_utc, last_seen_utc, removed_utc, download_id, obtained)
                VALUES ($p, $url, $local, $etag, $lm, $size, $type, $source, $hash, $first, $last, $removed, $download, $obtained)
                ON CONFLICT(project_id, url) DO UPDATE SET local_path = $local, etag = $etag, last_modified = $lm, size = $size,
                    content_type = $type, source_page = $source, content_hash = $hash, first_seen_utc = $first, last_seen_utc = $last,
                    removed_utc = $removed, download_id = $download, obtained = $obtained;
                """, tx);
            var parameters = new[] { "$p", "$url", "$local", "$etag", "$lm", "$size", "$type", "$source", "$hash", "$first", "$last", "$removed", "$download", "$obtained" }
                .ToDictionary(n => n, n =>
                {
                    var parameter = cmd.CreateParameter(); // the type follows each value
                    parameter.ParameterName = n;
                    cmd.Parameters.Add(parameter);
                    return parameter;
                });
            foreach (var f in files)
            {
                parameters["$p"].Value = projectId;
                parameters["$url"].Value = f.Url;
                parameters["$local"].Value = (object?)f.LocalPath ?? DBNull.Value;
                parameters["$etag"].Value = (object?)f.ETag ?? DBNull.Value;
                parameters["$lm"].Value = (object?)f.LastModified ?? DBNull.Value;
                parameters["$size"].Value = (object?)f.Size ?? DBNull.Value;
                parameters["$type"].Value = (object?)f.ContentType ?? DBNull.Value;
                parameters["$source"].Value = (object?)f.SourcePage ?? DBNull.Value;
                parameters["$hash"].Value = (object?)f.ContentHash ?? DBNull.Value;
                parameters["$first"].Value = f.FirstSeenUtc.ToString("O", CultureInfo.InvariantCulture);
                parameters["$last"].Value = f.LastSeenUtc.ToString("O", CultureInfo.InvariantCulture);
                parameters["$removed"].Value = f.RemovedUtc is { } removed ? removed.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value;
                parameters["$download"].Value = (object?)f.DownloadId ?? DBNull.Value;
                parameters["$obtained"].Value = f.Obtained ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>A download sent by the grabber completed: its file is obtained.</summary>
    public void MarkGrabberDownloadObtained(long downloadId)
    {
        lock (_lock)
        {
            using var cmd = Command("UPDATE grabber_files SET obtained = 1 WHERE download_id = $id;");
            cmd.Parameters.AddWithValue("$id", downloadId);
            cmd.ExecuteNonQuery();
        }
    }

    private static List<BrowserCookie> ReadCookies(string? secret)
    {
        // Cookies that cannot be decrypted (another Windows profile) are dropped; the project still works without them.
        if (Secrets.Unprotect(secret) is not { } json)
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<BrowserCookie>>(json, CookieJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
