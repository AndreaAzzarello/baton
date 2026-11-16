using System.Text.Json;
using Baton.Core;
using Baton.Core.Grabber;
using Baton.Core.Integration;
using Baton.Core.Portable;
using Baton.Core.Rules;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>Rules in the export and the import of the configuration (PortableData): no secrets, a checked preview, risky ones off.</summary>
public sealed class RulePortabilityTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private DownloadStore _store = null!;
    private DownloadService _service = null!;
    private GrabberService _grabber = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        (_store, _service, _grabber) = Open("a.db");
        return Task.CompletedTask;
    }

    private (DownloadStore, DownloadService, GrabberService) Open(string name)
    {
        var store = new DownloadStore(Path.Combine(_dir, name));
        store.SaveSettings(new AppSettings { DownloadFolder = Path.Combine(_dir, "dl"), ProxyMode = ProxyMode.None });
        var service = new DownloadService(store, autoTick: false);
        service.StopQueueAsync().GetAwaiter().GetResult();
        return (store, service, new GrabberService(service, store, autoTick: false));
    }

    public async Task DisposeAsync()
    {
        await _grabber.DisposeAsync();
        await _service.DisposeAsync();
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Rule R(string name, Action<RuleConditions> conditions, Action<RuleActions>? actions = null)
    {
        var rule = new Rule { Name = name };
        conditions(rule.Conditions);
        actions?.Invoke(rule.Actions);
        return rule;
    }

    private static string ProfileFolder(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", name); // an imported folder must be inside the profile

    private DownloadQueue QueueNamed(string name)
    {
        _service.SaveQueue(new DownloadQueue { Name = name });
        return _service.Queues.Single(q => q.Name == name);
    }

    private string Export(IReadOnlyList<Rule> rules, DownloadService? from = null)
    {
        from ??= _service;
        var file = PortableData.ExportSettings(from.Settings, from.Categories, from.Queues, [], DateTime.UtcNow, rules);
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, PortableData.Serialize(file));
        return path;
    }

    private List<ImportItem> Plan(string path) =>
        PortableImport.Plan(PortableData.Read(path), _service.GetSnapshots(), _service.Categories, _service.Queues, _grabber.Projects, _service.Rules);

    [Fact]
    public void Export_HasRulesWithoutIdsOrSecrets_AndCategoriesAndQueuesByName()
    {
        var night = QueueNamed("Night");
        var video = _service.Categories.Single(c => c.Key == Categories.VideoKey);
        _service.SaveRules([
            R("Films", c => { c.Domains = ["films.test"]; c.CategoryId = video.Id; }, a => { a.QueueId = night.Id; a.CategoryId = video.Id; a.Rename = "{date} {name}{ext}"; }),
            R("Archive", c => c.Extensions = ["zip"], a => a.Folder = Path.Combine(_dir, "zips")),
        ]);

        var json = File.ReadAllText(Export(_service.Rules));
        using var doc = JsonDocument.Parse(json);
        var rules = doc.RootElement.GetProperty("rules");
        Assert.Equal(["Films", "Archive"], rules.EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        Assert.Equal("Night", rules[0].GetProperty("actionQueue").GetString());
        Assert.Equal(video.Name, rules[0].GetProperty("actionCategory").GetString());
        Assert.Equal(video.Name, rules[0].GetProperty("conditionCategory").GetString());
        Assert.DoesNotContain("\"id\"", json.Substring(json.IndexOf("\"rules\"", StringComparison.Ordinal)));      // no database ids
        Assert.DoesNotContain("isEmpty", json);
        Assert.DoesNotContain("isRisky", json);
        Assert.DoesNotContain("imported", json);
        Assert.False(rules[0].GetProperty("actions").TryGetProperty("queueId", out var queueId) && queueId.ValueKind == JsonValueKind.Number);

        // Without rules the file reads as before.
        var plain = PortableData.Read(Export([]));
        Assert.Empty(plain.Rules ?? []);
    }

    [Fact]
    public async Task Import_ShowsAPreview_NothingTickedAndRiskyOnesWarned_AndTheNamesFindThisAppsIds()
    {
        var (store, other, grabber) = Open("b.db");
        try
        {
            other.SaveQueue(new DownloadQueue { Name = "Night" });
            var path = Export([
                R("Quiet", c => c.Domains = ["a.test"], a => a.Folder = ProfileFolder("dm-rule-test")),
                R("Opener", c => c.Domains = ["b.test"], a => a.OpenWhenDone = true),
                R("Skipper", c => c.Domains = ["c.test"], a => a.SkipConfirmation = true),
                R("ToNight", c => c.Domains = ["d.test"], a => a.QueueId = other.Queues.Single(q => q.Name == "Night").Id),
            ], other);

            var queue = QueueNamed("Night"); // the other app has its own id for it
            var items = Plan(path).Where(i => i.Kind == ImportKind.Rule).ToList();
            Assert.Equal(4, items.Count);
            Assert.All(items, i => Assert.False(i.Include));                 // never ticked in advance
            Assert.All(items, i => Assert.Null(i.Problem));
            Assert.NotNull(items.Single(i => i.Name == "Opener").Warning);
            Assert.NotNull(items.Single(i => i.Name == "Skipper").Warning);
            Assert.Null(items.Single(i => i.Name == "Quiet").Warning);

            foreach (var item in items)
                item.Include = true;
            var result = PortableImport.Apply(items, DuplicateMode.Skip, _service, _grabber);
            Assert.Equal(4, result.Added);

            var rules = _service.Rules.ToDictionary(r => r.Name);
            Assert.True(rules["Quiet"].Enabled);
            Assert.True(rules["Quiet"].Imported);
            Assert.False(rules["Opener"].Enabled);                            // opens files: off until the user turns it on
            Assert.False(rules["Skipper"].Enabled);                           // adds without asking: off
            Assert.True(rules["Opener"].Actions.OpenWhenDone);                // but kept as written
            Assert.Equal(queue.Id, rules["ToNight"].Actions.QueueId);
            Assert.All(rules.Values, r => Assert.True(r.Id > 0));
            Assert.Equal(["Quiet", "Opener", "Skipper", "ToNight"], _service.Rules.OrderBy(r => r.SortOrder).Select(r => r.Name));
        }
        finally
        {
            await grabber.DisposeAsync();
            await other.DisposeAsync();
            store.Dispose();
        }
    }

    [Fact]
    public void Import_AMissingCategoryOrQueue_IsDroppedFromActions_AndSwitchesOffTheRuleWhoseConditionNamedIt()
    {
        var path = Path.Combine(_dir, "missing.json");
        File.WriteAllText(path, """
            { "format": "Baton", "version": 1, "kind": "settings",
              "rules": [
                { "name": "NoQueue", "conditions": { "domains": ["a.test"] }, "actions": { "folder": null }, "actionQueue": "Gone", "actionCategory": "AlsoGone" },
                { "name": "NoCategory", "conditions": { "domains": ["b.test"] }, "conditionCategory": "Gone", "actions": {} } ] }
            """);
        var items = Plan(path);
        Assert.All(items.Where(i => i.Kind == ImportKind.Rule), i => Assert.NotNull(i.Warning));
        foreach (var item in items.Where(i => i.Kind == ImportKind.Rule))
            item.Include = true;
        PortableImport.Apply(items, DuplicateMode.Skip, _service, _grabber);

        var rules = _service.Rules.ToDictionary(r => r.Name);
        Assert.True(rules["NoQueue"].Enabled);
        Assert.Null(rules["NoQueue"].Actions.QueueId);
        Assert.Null(rules["NoQueue"].Actions.CategoryId);
        Assert.False(rules["NoCategory"].Enabled);                            // without its condition it would match too much
        Assert.Null(rules["NoCategory"].Conditions.CategoryId);
    }

    [Fact]
    public void Import_Duplicates_AreMarked_AndFollowTheChosenMode()
    {
        _service.SaveRules([R("Same", c => c.Domains = ["old.test"]), R("Other", c => c.Domains = ["x.test"])]);
        var oldId = _service.Rules.Single(r => r.Name == "Same").Id;
        var path = Export([R("same", c => c.Domains = ["new.test"])], _service); // a name matches regardless of case

        List<ImportItem> Chosen() => Plan(path).Where(i => i.Kind == ImportKind.Rule).Select(i => { i.Include = true; return i; }).ToList();
        Assert.True(Chosen().Single().IsDuplicate);

        Assert.Equal(1, PortableImport.Apply(Chosen(), DuplicateMode.Skip, _service, _grabber).Skipped);
        Assert.Equal(["old.test"], _service.Rules.Single(r => r.Name == "Same").Conditions.Domains);

        var replaced = PortableImport.Apply(Chosen(), DuplicateMode.Replace, _service, _grabber);
        Assert.Equal(1, replaced.Replaced);
        var same = _service.Rules.Single(r => r.Id == oldId);                  // same row, same place
        Assert.Equal(["new.test"], same.Conditions.Domains);
        Assert.Equal(0, same.SortOrder);

        PortableImport.Apply(Chosen(), DuplicateMode.KeepBoth, _service, _grabber);
        Assert.Equal(3, _service.Rules.Count);
        Assert.Contains(_service.Rules, r => r.Name == "same (2)");
    }

    [Fact]
    public void HostileRules_AreRefused_AndNeverImported()
    {
        var path = Path.Combine(_dir, "hostile-rules.json");
        var many = string.Join(",", Enumerable.Range(0, 150).Select(i => $"\"d{i}.test\""));
        File.WriteAllText(path, $$"""
            { "format": "Baton", "version": 1, "kind": "settings",
              "rules": [
                null,
                { "name": "", "conditions": { "domains": ["a.test"] }, "actions": {} },
                { "name": "NoCondition", "conditions": {}, "actions": {} },
                { "name": "BadRegex", "conditions": { "urlRegex": "(a)\\1" }, "actions": {} },
                { "name": "Relative", "conditions": { "domains": ["a.test"] }, "actions": { "folder": "..\\..\\Windows" } },
                { "name": "Share", "conditions": { "domains": ["a.test"] }, "actions": { "folder": "\\\\server\\share\\x" } },
                { "name": "Many", "conditions": { "domains": [{{many}}] }, "actions": {} },
                { "name": "Origin", "conditions": { "origins": ["evil"] }, "actions": {} },
                { "name": "Connections", "conditions": { "domains": ["a.test"] }, "actions": { "connections": 9999 } },
                { "name": "Sizes", "conditions": { "minSize": 10, "maxSize": 5 }, "actions": {} },
                { "name": "NullLists", "conditions": { "domains": null, "extensions": [null, "zip"] }, "actions": {} } ] }
            """);
        var items = Plan(path).Where(i => i.Kind == ImportKind.Rule).ToList();
        Assert.Equal(10, items.Count);                                          // the null entry is dropped
        Assert.All(items.Where(i => i.Name != "NullLists"), i => Assert.NotNull(i.Problem));
        Assert.Null(items.Single(i => i.Name == "NullLists").Problem);          // nulls in the lists are cleaned, not fatal

        foreach (var item in items)
            item.Include = true;
        PortableImport.Apply(items, DuplicateMode.KeepBoth, _service, _grabber);
        Assert.Equal(["NullLists"], _service.Rules.Select(r => r.Name));
    }

    [Fact]
    public void ABrowserSize_TravelsInTheProtocol_IsValidated_AndOldMessagesStillRead()
    {
        var json = JsonSerializer.Serialize(new ChannelMessage(Messages.Add, Download: new BrowserDownload("https://x.test/a.zip", Size: 123_456)), Messages.Json);
        Assert.Contains("\"size\":123456", json);
        Assert.Equal(123_456, JsonSerializer.Deserialize<ChannelMessage>(json, Messages.Json)!.Download!.Size);

        // An extension from before 1.4.1 sends no size.
        var old = JsonSerializer.Deserialize<ChannelMessage>("""{"type":"add","download":{"url":"https://x.test/a.zip","fileName":"a.zip"}}""", Messages.Json)!;
        Assert.Null(old.Download!.Size);
        Assert.Null(MessageValidator.Validate(old));

        Assert.Null(MessageValidator.Validate(new ChannelMessage(Messages.Add, Download: new BrowserDownload("https://x.test/a.zip", Size: 5))));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Add, Download: new BrowserDownload("https://x.test/a.zip", Size: -5))));
        Assert.NotNull(MessageValidator.Validate(new ChannelMessage(Messages.Add, Download: new BrowserDownload("https://x.test/a.zip", Size: long.MaxValue))));
    }

    [Fact]
    public void ASizeRule_FiresForABrowserAddWithAKnownSize_AndNotWithoutIt()
    {
        var big = R("Big files", c => { c.Domains = ["files.test"]; c.MinSize = 100 * 1024 * 1024; }, a => a.Folder = Path.Combine(_dir, "big"));
        _service.SaveRules([big]);

        Assert.Equal("Big files", _service.PreviewRules("https://files.test/a.iso", RuleOrigins.Browser, 200L * 1024 * 1024).Rule?.Name);
        Assert.Null(_service.PreviewRules("https://files.test/a.iso", RuleOrigins.Browser, 1024).Rule);
        Assert.Null(_service.PreviewRules("https://files.test/a.iso", RuleOrigins.Browser).Rule);   // size unknown: a size rule does not fire

        var id = _service.Add("https://files.test/a.iso", startNow: false, ruleOrigin: RuleOrigins.Browser, knownSize: 200L * 1024 * 1024);
        Assert.Equal(Path.Combine(_dir, "big"), _service.GetSnapshot(id)!.Directory);
        var small = _service.Add("https://files.test/b.iso", startNow: false, ruleOrigin: RuleOrigins.Browser, knownSize: 1024);
        Assert.NotEqual(Path.Combine(_dir, "big"), _service.GetSnapshot(small)!.Directory);
    }
}
