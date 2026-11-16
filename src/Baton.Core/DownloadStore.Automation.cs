using System.Globalization;
using Baton.Core.Automation;
using Microsoft.Data.Sqlite;

namespace Baton.Core;

public sealed partial class DownloadStore
{
    /// <summary>
    /// Schema 10: the automatic rules and the log of what the local API, MCP and the rules did. The rule's conditions and actions are
    /// JSON documents (they grow with the versions without a new column each time); the log never holds an address with its query,
    /// cookies or any secret, only what happened.
    /// </summary>
    private void MigrateTo10(SqliteTransaction tx)
    {
        AddColumnIfMissing("downloads", "rule_applied", "TEXT", tx);
        CreateAutomationTables(tx);
    }

    private void CreateAutomationTables(SqliteTransaction tx) => Execute("""
        CREATE TABLE IF NOT EXISTS rules (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            sort_order INTEGER NOT NULL,
            name TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            imported INTEGER NOT NULL DEFAULT 0,
            conditions TEXT NOT NULL DEFAULT '{}',
            actions TEXT NOT NULL DEFAULT '{}'
        );
        CREATE TABLE IF NOT EXISTS automation_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            at_utc TEXT NOT NULL,
            origin TEXT NOT NULL,
            action TEXT NOT NULL,
            outcome TEXT NOT NULL,
            download_id INTEGER,
            detail TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS ix_automation_log_time ON automation_log(at_utc);
        """, tx);

    public void AddAutomationLog(AutomationLogEntry entry)
    {
        lock (_lock)
        {
            using var cmd = Command("""
                INSERT INTO automation_log (at_utc, origin, action, outcome, download_id, detail)
                VALUES ($at, $origin, $action, $outcome, $id, $detail);
                """);
            cmd.Parameters.AddWithValue("$at", entry.AtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$origin", entry.Origin);
            cmd.Parameters.AddWithValue("$action", entry.Action);
            cmd.Parameters.AddWithValue("$outcome", entry.Outcome);
            cmd.Parameters.AddWithValue("$id", (object?)entry.DownloadId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detail", entry.Detail);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The newest entries first, optionally of one origin.</summary>
    public IReadOnlyList<AutomationLogEntry> ReadAutomationLog(string? origin, int limit)
    {
        lock (_lock)
        {
            using var cmd = Command($"""
                SELECT id, at_utc, origin, action, outcome, download_id, detail FROM automation_log
                {(origin is null ? "" : "WHERE origin = $origin")} ORDER BY id DESC LIMIT $limit;
                """);
            if (origin is not null)
                cmd.Parameters.AddWithValue("$origin", origin);
            cmd.Parameters.AddWithValue("$limit", limit);
            var entries = new List<AutomationLogEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                entries.Add(new AutomationLogEntry(reader.GetInt64(0),
                    DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetString(6)));
            return entries;
        }
    }

    public void ClearAutomationLog()
    {
        lock (_lock)
            Execute("DELETE FROM automation_log;");
    }

    /// <summary>Removes what is older than <paramref name="maxAge"/>, then the oldest beyond <paramref name="maxEntries"/>.</summary>
    public void PruneAutomationLog(DateTime nowUtc, TimeSpan maxAge, int maxEntries)
    {
        lock (_lock)
        {
            using (var old = Command("DELETE FROM automation_log WHERE at_utc < $cutoff;"))
            {
                old.Parameters.AddWithValue("$cutoff", (nowUtc - maxAge).ToString("O", CultureInfo.InvariantCulture));
                old.ExecuteNonQuery();
            }
            using var extra = Command("DELETE FROM automation_log WHERE id NOT IN (SELECT id FROM automation_log ORDER BY id DESC LIMIT $max);");
            extra.Parameters.AddWithValue("$max", maxEntries);
            extra.ExecuteNonQuery();
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions RuleJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>The rules in their order (a rule whose stored text cannot be read comes back marked <c>Unreadable</c>, never dropped).</summary>
    public List<Rules.Rule> LoadRules()
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT id, sort_order, name, enabled, imported, conditions, actions FROM rules ORDER BY sort_order, id;");
            var rules = new List<Rules.Rule>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rule = new Rules.Rule
                {
                    Id = reader.GetInt64(0), SortOrder = reader.GetInt32(1), Name = reader.GetString(2),
                    Enabled = reader.GetInt64(3) != 0, Imported = reader.GetInt64(4) != 0,
                };
                try
                {
                    rule.Conditions = System.Text.Json.JsonSerializer.Deserialize<Rules.RuleConditions>(reader.GetString(5), RuleJson) ?? new();
                    rule.Actions = System.Text.Json.JsonSerializer.Deserialize<Rules.RuleActions>(reader.GetString(6), RuleJson) ?? new();
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
                {
                    // Kept as it is in the database (SaveRules does not touch its text) and shown as unreadable, never dropped.
                    rule = new Rules.Rule { Id = rule.Id, SortOrder = rule.SortOrder, Name = rule.Name, Enabled = false, Imported = rule.Imported, Unreadable = true };
                }
                rules.Add(rule);
            }
            return rules;
        }
    }

    /// <summary>Replaces all the rules with <paramref name="rules"/>, in this order; new ones (id 0) get their id.</summary>
    public void SaveRules(IReadOnlyList<Rules.Rule> rules)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var keep = rules.Where(r => r.Id > 0).Select(r => r.Id).ToHashSet();
            foreach (var existing in LoadRulesIds(tx).Where(id => !keep.Contains(id)))
            {
                using var delete = Command("DELETE FROM rules WHERE id = $id;", tx);
                delete.Parameters.AddWithValue("$id", existing);
                delete.ExecuteNonQuery();
            }
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                rule.SortOrder = i;
                if (rule.Unreadable && rule.Id > 0)
                {
                    // Only its place changes: the stored text stays exactly as it is.
                    using var move = Command("UPDATE rules SET sort_order = $order WHERE id = $id;", tx);
                    move.Parameters.AddWithValue("$order", i);
                    move.Parameters.AddWithValue("$id", rule.Id);
                    move.ExecuteNonQuery();
                    continue;
                }
                using var cmd = Command(rule.Id > 0
                    ? "UPDATE rules SET sort_order = $order, name = $name, enabled = $enabled, imported = $imported, conditions = $conditions, actions = $actions WHERE id = $id;"
                    : "INSERT INTO rules (sort_order, name, enabled, imported, conditions, actions) VALUES ($order, $name, $enabled, $imported, $conditions, $actions); SELECT last_insert_rowid();", tx);
                cmd.Parameters.AddWithValue("$order", i);
                cmd.Parameters.AddWithValue("$name", rule.Name);
                cmd.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$imported", rule.Imported ? 1 : 0);
                cmd.Parameters.AddWithValue("$conditions", System.Text.Json.JsonSerializer.Serialize(rule.Conditions, RuleJson));
                cmd.Parameters.AddWithValue("$actions", System.Text.Json.JsonSerializer.Serialize(rule.Actions, RuleJson));
                if (rule.Id > 0)
                {
                    cmd.Parameters.AddWithValue("$id", rule.Id);
                    cmd.ExecuteNonQuery();
                }
                else
                    rule.Id = (long)cmd.ExecuteScalar()!;
            }
            tx.Commit();
        }
    }

    private List<long> LoadRulesIds(SqliteTransaction tx)
    {
        using var cmd = Command("SELECT id FROM rules;", tx);
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }
}
