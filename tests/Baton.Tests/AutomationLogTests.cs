using Baton.Core;
using Baton.Core.Automation;

namespace Baton.Tests;

public sealed class AutomationLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "baton-log-" + Guid.NewGuid().ToString("N"));
    private readonly DownloadStore _store;
    private DateTime _now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    public AutomationLogTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new DownloadStore(Path.Combine(_dir, "downloads.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Record_ThenRead_NewestFirst_AndFilterByOrigin()
    {
        var log = new AutomationLog(_store, () => _now);
        log.Record(AutomationLog.Api, "GET /downloads", "200");
        log.Record(AutomationLog.Mcp, "add", "403", "level too low");
        log.Record(AutomationLog.Api, "POST /downloads", "429");
        var all = log.Read();
        Assert.Equal(3, all.Count);
        Assert.Equal("POST /downloads", all[0].Action);
        Assert.Equal(2, log.Read(AutomationLog.Api).Count);
        Assert.Single(log.Read(AutomationLog.Mcp));
    }

    [Theory]
    [InlineData("added https://host/file.zip?token=abc&x=1#frag", "added https://host/file.zip")]
    [InlineData("from https://user:pass@host/a?b=c", "from https://host/a")]
    [InlineData("a=1 https://h/p?secret=1 b", "a=1 https://h/p b")]
    [InlineData("ftp://bob:pa@ss@host/f.zip", "ftp://host/f.zip")]       // a password with "@"
    [InlineData("ftp://bob:pa?ss@host/x", "ftp://host/x")]                // ... with "?"
    [InlineData("ftp://bob:pa#ss@host/x", "ftp://host/x")]                // ... with "#"
    [InlineData("https://host/p?mail=a@b.com", "https://host/p")]         // an "@" in the query is not user info
    public void Clean_NeverKeepsQueryFragmentOrUserInfo(string input, string expected) =>
        Assert.Equal(expected, AutomationLog.Clean(input));

    [Fact]
    public void Clean_BoundsLength() => Assert.True(AutomationLog.Clean(new string('x', 5000)).Length <= 301);

    [Fact]
    public void Prune_RemovesOlderThanThirtyDays_AndKeepsAtMostTheLimit()
    {
        var log = new AutomationLog(_store, () => _now);
        log.Record(AutomationLog.Api, "old", "200");
        _now = _now.AddDays(31);
        log.Record(AutomationLog.Api, "new", "200");
        log.Prune();
        Assert.Equal(["new"], log.Read().Select(e => e.Action));

        for (var i = 0; i < 12; i++)
            log.Record(AutomationLog.Api, "n" + i, "200");
        _store.PruneAutomationLog(_now, TimeSpan.FromDays(30), 5);
        Assert.Equal(5, log.Read().Count);
        Assert.Equal("n11", log.Read()[0].Action);
    }

    [Fact]
    public void Clear_EmptiesTheLog_AndRaisesChanged()
    {
        var log = new AutomationLog(_store, () => _now);
        var changed = 0;
        log.Changed += () => changed++;
        log.Record(AutomationLog.RulesOrigin, "rule", "applied");
        log.Clear();
        Assert.Empty(log.Read());
        Assert.Equal(2, changed);
    }

    [Fact]
    public void TwentyThousandUnauthenticatedRefusals_AreFewLines_AndTheCountIsKept_A10()
    {
        var log = new AutomationLog(_store, () => _now);
        var refusals = new Baton.Core.Integration.UnauthenticatedRefusals(log, () => _now);
        for (var i = 0; i < 20_000; i++)
            refusals.Note("wrongToken", "GET /downloads", "401", ActivityText.Reason("wrongToken"));
        // Another reason has its own line; the first refusal of each is written at once.
        refusals.Note("webPage", "GET /downloads", "403", ActivityText.Reason("webPage"));
        Assert.Equal(2, log.Read(AutomationLog.Api, 100).Count);

        // A minute later the next refusal closes the count of the minute before.
        _now += TimeSpan.FromMinutes(1.5);
        refusals.Note("wrongToken", "GET /downloads", "401", ActivityText.Reason("wrongToken"));
        var lines = log.Read(AutomationLog.Api, 100);
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.Detail == "!wrongToken ×19999");

        // Nothing is lost when the API stops with a count still waiting.
        refusals.Note("wrongToken", "GET /downloads", "401", ActivityText.Reason("wrongToken"));
        refusals.Flush();
        Assert.Contains(log.Read(AutomationLog.Api, 100), l => l.Detail == "!wrongToken ×1");
    }
}
