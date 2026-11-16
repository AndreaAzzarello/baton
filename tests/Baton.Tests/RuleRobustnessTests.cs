using Baton.Core;
using Baton.Core.Rules;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>Rules whose stored text this version cannot read are kept, never dropped; expressions are built once.</summary>
public sealed class RuleRobustnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "baton-rulerob-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "downloads.db");

    public RuleRobustnessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static Rule Make(string name, string domain) => new() { Name = name, Conditions = { Domains = [domain] } };

    private (string Conditions, string Actions, long Enabled, long Order) Raw(string name)
    {
        using var connection = new SqliteConnection($"Data Source={Db};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT conditions, actions, enabled, sort_order FROM rules WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    [Fact]
    public void UnreadableRule_SurvivesLoadAndSave_ExactlyAsStored()
    {
        using (var store = new DownloadStore(Db))
            store.SaveRules([Make("old", "a.test"), Make("future", "b.test")]);
        using (var connection = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE rules SET conditions = '{\"domains\": 5, \"newThing\": [', actions = '<<future>>' WHERE name = 'future';";
            cmd.ExecuteNonQuery();
        }
        var before = Raw("future");

        using (var store = new DownloadStore(Db))
        {
            var rules = store.LoadRules();
            Assert.Equal(2, rules.Count);
            var future = rules.Single(r => r.Name == "future");
            Assert.True(future.Unreadable);
            Assert.False(future.Enabled);
            Assert.False(rules.Single(r => r.Name == "old").Unreadable);

            // The user edits the other rule and moves the unreadable one first, then saves.
            rules.Single(r => r.Name == "old").Name = "renamed";
            rules.Reverse();
            store.SaveRules(rules);
        }

        var after = Raw("future");
        Assert.Equal(before.Conditions, after.Conditions);
        Assert.Equal(before.Actions, after.Actions);
        Assert.Equal(before.Enabled, after.Enabled);
        Assert.Equal(0, after.Order); // only its place changed
        Assert.Equal(1, Raw("renamed").Order);

        using var again = new DownloadStore(Db);
        Assert.Equal(["future", "renamed"], again.LoadRules().Select(r => r.Name));
    }

    [Fact]
    public void UnreadableRule_NeverApplies_AndIsNotExported()
    {
        var unreadable = new Rule { Id = 1, Name = "future", Unreadable = true, Enabled = true, Conditions = { Domains = ["x.test"] } };
        var input = new RuleInput(new Uri("https://x.test/f.zip"), RuleOrigins.Browser, "f.zip", null, null);
        Assert.Null(RuleEngine.Match([unreadable], input));
        var file = Baton.Core.Portable.PortableData.ExportSettings(new AppSettings(), [], [], [], DateTime.UtcNow, [unreadable, Make("ok", "y.test")]);
        Assert.Equal(["ok"], file.Rules!.Select(r => r.Name));
    }

    [Fact]
    public void Regex_IsBuiltOnce_ForManyMatches_AndANewTextBuildsANewOne()
    {
        var pattern = @"\.unique" + Guid.NewGuid().ToString("N")[..8] + "$";
        var before = RuleEngine.CompileCount;
        for (var i = 0; i < 50; i++)
            Assert.False(RuleEngine.RegexMatches(pattern, "https://a.test/f.zip"));
        Assert.Equal(before + 1, RuleEngine.CompileCount);

        Assert.True(RuleEngine.RegexMatches(pattern.Replace(@"\.unique", @"\.unique", StringComparison.Ordinal) + "|f\\.zip$", "https://a.test/f.zip"));
        Assert.Equal(before + 2, RuleEngine.CompileCount); // the rule changed: a new expression
    }

    [Fact]
    public void Regex_IsNonBacktracking_AndABadOneIsNotRetried()
    {
        var before = RuleEngine.CompileCount;
        for (var i = 0; i < 5; i++)
            Assert.False(RuleEngine.RegexMatches("([unclosed" + before, "x"));
        Assert.Equal(before + 1, RuleEngine.CompileCount);
        // A pattern that explodes with backtracking engines answers at once.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(RuleEngine.RegexMatches("^(a+)+$", new string('a', 40) + "!"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }
}
