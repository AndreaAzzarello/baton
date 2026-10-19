using System.Xml.Linq;
using DownloadManager.Core;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Tests;

/// <summary>Logic behind the UI: links from the clipboard or a drop, column layout, notifications and live progress.</summary>
public sealed class UiSupportTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync() => _server = await TestFileServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("https://example.com/a.zip", true)]
    [InlineData("  http://example.com/file?x=1  ", true)]
    [InlineData("ftp://example.com/a.zip", true)]
    [InlineData("ftps://example.com/a.zip", true)]
    [InlineData("gopher://example.com/a.zip", false)]
    [InlineData("file:///C:/a.zip", false)]
    [InlineData("https://example.com/a b.zip", false)]
    [InlineData("example.com/a.zip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ClipboardText_IsUsedOnlyWhenItIsOneWebAddress(string? text, bool accepted)
    {
        Assert.Equal(accepted, Urls.SingleHttpUrl(text) is not null);
    }

    [Fact]
    public void Toolbar_layout_drops_unknown_and_repeated_items_and_keeps_one_spacer()
    {
        Assert.Equal(ToolbarLayout.Default, ToolbarLayout.Format(ToolbarLayout.Parse("")));
        Assert.Equal("settings,|,add,~", ToolbarLayout.Format(ToolbarLayout.Parse("|,settings,|,|,ghost,add,add,|")));
        Assert.Equal("add,~,queue", ToolbarLayout.Format(ToolbarLayout.Parse("add,|,~,~,queue")));
    }

    [Fact]
    public void DroppedText_YieldsEveryDistinctWebAddress()
    {
        var text = "Scarica (https://example.com/one.zip), poi http://example.com/two.iso.\r\n" +
                   "https://example.com/one.zip\nmailto:x@example.com ftp://files.example.com/f gopher://no.example.com/g https://example.com/three?x=1&y=2\0";
        Assert.Equal(
            ["https://example.com/one.zip", "http://example.com/two.iso", "ftp://files.example.com/f", "https://example.com/three?x=1&y=2"],
            Urls.ExtractHttpUrls(text));
        Assert.Empty(Urls.ExtractHttpUrls("nessun link qui"));
    }

    private static readonly IReadOnlyList<ColumnState> Defaults =
    [
        new() { Key = "Name", DisplayIndex = 0, Width = 0 },
        new() { Key = "SizeSort", DisplayIndex = 1, Width = 100 },
        new() { Key = "Speed", DisplayIndex = 2, Width = 90 },
        new() { Key = "Directory", DisplayIndex = 3, Width = 200, Visible = false },
    ];

    [Fact]
    public void ColumnLayout_RoundTrips_OrderWidthVisibilityAndSort()
    {
        var layout = ColumnLayout.Restore(null, Defaults);
        layout.Columns.Single(c => c.Key == "Speed").DisplayIndex = 0;
        layout.Columns.Single(c => c.Key == "Name").DisplayIndex = 2;
        layout.Columns.Single(c => c.Key == "SizeSort").Width = 140;
        layout.Columns.Single(c => c.Key == "Directory").Visible = true;
        layout.SortKey = "SizeSort";
        layout.SortDescending = true;

        var restored = ColumnLayout.Restore(layout.Serialize(), Defaults);
        Assert.Equal(["Speed", "SizeSort", "Name", "Directory"], restored.Columns.OrderBy(c => c.DisplayIndex).Select(c => c.Key));
        Assert.Equal(140, restored.Columns.Single(c => c.Key == "SizeSort").Width);
        Assert.True(restored.Columns.Single(c => c.Key == "Directory").Visible);
        Assert.Equal("SizeSort", restored.SortKey);
        Assert.True(restored.SortDescending);
    }

    [Fact]
    public void ColumnLayout_ToleratesOldUnknownAndDamagedData()
    {
        // Saved by a version that had a "Rating" column and no "Directory" column.
        const string saved = """
            {"columns":[{"key":"Rating","displayIndex":0,"width":50,"visible":true},
                        {"key":"Speed","displayIndex":1,"width":-5,"visible":false}],"sortKey":"Rating","sortDescending":true}
            """;
        var restored = ColumnLayout.Restore(saved, Defaults);
        Assert.Equal(["Name", "SizeSort", "Speed", "Directory"], restored.Columns.Select(c => c.Key));
        Assert.Equal(90, restored.Columns.Single(c => c.Key == "Speed").Width); // invalid width: default
        Assert.False(restored.Columns.Single(c => c.Key == "Speed").Visible);
        Assert.Equal(Enumerable.Range(0, 4), restored.Columns.Select(c => c.DisplayIndex).Order());
        Assert.Null(restored.SortKey); // unknown column: queue order

        var broken = ColumnLayout.Restore("{not json", Defaults);
        Assert.Equal(Defaults.Select(d => d.Visible), broken.Columns.Select(c => c.Visible));
    }

    [Fact]
    public void NotificationLinks_RoundTrip_AndRejectForgedOrTamperedLinks()
    {
        var keyPath = Path.Combine(_dir, "notification.key");
        var links = NotificationLinks.FromKeyFile(keyPath);
        var path = @"C:\Users\Test\Downloads\Compressi\file con spazi & simboli.zip";

        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var open = links.Create(NotificationAction.Open, path, 42, now + NotificationLinks.Lifetime);
        Assert.StartsWith("downloadmanager:open?", open);
        Assert.True(NotificationLinks.FromKeyFile(keyPath).TryParse(open, now, out var action, out var parsed, out var id)); // same key from disk
        Assert.Equal(NotificationAction.Open, action);
        Assert.Equal(path, parsed);
        Assert.Equal(42, id);

        // Another user's (or a web page's) key, a different verb, or an edited path are all refused.
        Assert.False(new NotificationLinks(new byte[32]).TryParse(open, now, out _, out _, out _));
        Assert.False(links.TryParse(open.Replace("downloadmanager:open", "downloadmanager:reveal"), now, out _, out _, out _));
        var forgedPath = links.Create(NotificationAction.Open, @"C:\Windows\System32\calc.exe", 42, now + NotificationLinks.Lifetime).Split("&s=")[0];
        Assert.False(links.TryParse(forgedPath + "&s=" + open.Split("&s=")[1], now, out _, out _, out _));
        Assert.False(links.TryParse("downloadmanager:open?p=QUJD", now, out _, out _, out _));
        Assert.False(links.TryParse("https://example.com", now, out _, out _, out _));
        // Another download, or a later expiry, cannot be swapped in; an old link has expired.
        Assert.False(links.TryParse(open.Replace("&d=42&", "&d=43&"), now, out _, out _, out _));
        Assert.False(links.TryParse(open.Replace("&e=", "&e=9"), now, out _, out _, out _));
        Assert.False(links.TryParse(open, now + NotificationLinks.Lifetime + TimeSpan.FromSeconds(1), out _, out _, out _));
    }

    [Fact]
    public void CompletionToast_IsValidXml_WithSignedButtons()
    {
        var links = new NotificationLinks(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var snapshot = new DownloadSnapshot(7, "https://example.com/a.zip", "a <b> & \"c\".zip", @"C:\dl\Compressi", @"C:\dl\Compressi\a <b> & ""c"".zip",
            1024, 1024, DownloadStatus.Completed, null, 0, 0, true, DateTime.UtcNow);

        var toast = XElement.Parse(CompletionToast.Build(snapshot, "1 KB", links));
        Assert.Equal("protocol", toast.Attribute("activationType")!.Value);
        Assert.Contains(toast.Descendants("text"), t => t.Value == "a <b> & \"c\".zip");
        Assert.Contains(toast.Descendants("text"), t => t.Value.Contains("Compressi") && !t.Value.Contains(@"C:\dl"));
        var actions = toast.Descendants("action").ToList();
        Assert.Equal(2, actions.Count);
        Assert.True(links.TryParse(actions[0].Attribute("arguments")!.Value, DateTime.UtcNow, out var a0, out var p0, out var id0));
        Assert.Equal((NotificationAction.Open, snapshot.FinalPath, 7L), (a0, p0, id0));
        Assert.True(links.TryParse(actions[1].Attribute("arguments")!.Value, DateTime.UtcNow, out var a1, out _, out _));
        Assert.Equal(NotificationAction.Reveal, a1);
        // The buttons stop working after a week.
        Assert.False(links.TryParse(actions[0].Attribute("arguments")!.Value, DateTime.UtcNow.AddDays(8), out _, out _, out _));
    }

    [Fact]
    public async Task Service_ReportsLiveSegments_AndRaisesCompletedOnce()
    {
        var file = new ServedFile { Content = TestFileServer.RandomBytes(1024 * 1024, 95), Throttle = _ => 256 * 1024 };
        var url = _server.Add("live.bin", file);
        using var store = new DownloadStore(Path.Combine(_dir, "state.db"));
        store.SaveSettings(new AppSettings { DownloadFolder = _dir, MaxConnections = 4 });
        await using var service = new DownloadService(store, engineTemplate: new EngineOptions
        {
            MinSegmentSize = 64 * 1024,
            RetryBaseDelay = TimeSpan.FromMilliseconds(50),
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
        });
        var completed = new List<DownloadSnapshot>();
        service.DownloadCompleted += s => { lock (completed) completed.Add(s); };

        var id = service.Add(url);
        await WaitUntil(() => service.GetSegments(id).Count(s => s.Active) >= 2);
        var live = service.GetSegments(id);
        Assert.True(live.Count >= 2);
        Assert.Equal(file.Content.Length, live.Sum(s => s.End - s.Start + 1)); // the ranges cover the file exactly once

        file.Throttle = _ => 0;
        await WaitUntil(() => service.GetSnapshot(id)!.Status == DownloadStatus.Completed);
        await WaitUntil(() => { lock (completed) return completed.Count > 0; });
        Assert.Equal(id, Assert.Single(completed).Id);
        Assert.All(service.GetSegments(id), s => Assert.False(s.Active));

        // Downloaded files carry the Internet zone mark, like a browser download.
        var mark = await File.ReadAllTextAsync(completed[0].FinalPath + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", mark);
        Assert.Contains("HostUrl=" + url, mark);
    }

    [Fact]
    public async Task ServerThatNeverAnswers_TimesOut_InsteadOfHanging()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); // accepts connections but never sends a response
        var url = $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/silent.bin";
        using var http = DownloadService.CreateHttpClient();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            HttpProbe.ProbeAsync(http, url, CancellationToken.None, timeout: TimeSpan.FromMilliseconds(500)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached in time.");
            await Task.Delay(25);
        }
    }
}
