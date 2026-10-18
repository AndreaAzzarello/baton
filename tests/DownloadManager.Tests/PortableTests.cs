using DownloadManager.Core;
using DownloadManager.Core.Grabber;
using DownloadManager.Core.Portable;
using DownloadManager.Core.Security;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Tests;

/// <summary>Export and import: lists and configuration travel, secrets never do; imported files are checked.</summary>
public sealed class PortableTests : IAsyncLifetime
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
        service.StopQueueAsync().GetAwaiter().GetResult(); // imported downloads must not start by themselves in the test either
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

    private const string Secret = "TOP-SECRET-VALUE";

    private void FillWithSecrets()
    {
        var cookies = new List<BrowserCookie> { new("sid", Secret, "files.test", "/", true, false) };
        _service.Add($"https://user:{Secret}@files.test/a.zip", startNow: false);
        _service.Add("https://files.test/export", startNow: false, context: new RequestContext("https://files.test/form", "UA", cookies)
        {
            Method = "POST", Body = $"token={Secret}", BodyType = "application/x-www-form-urlencoded",
        });
        _service.Add("https://files.test/b.iso", startNow: false, expectedHash: new string('a', 64));
        var settings = _service.Settings;
        settings.ProxyMode = ProxyMode.Manual;
        settings.ProxyHost = "proxy.test";
        settings.ProxyUser = "me";
        settings.ProxyPassword = Secret;
        settings.ScannerPath = @"C:\Tools\scan.exe";
        settings.UpdateUrl = "https://updates.test/latest.json";
        _service.UpdateSettings(settings);
        _service.Credentials.Save(new SiteCredential { Host = "files.test", Username = "me", Password = Secret }, remember: true);
        _grabber.Save(new GrabberProject { Name = "Libri", StartUrl = "https://books.test/", Cookies = [new BrowserCookie("s", Secret, "books.test", "/", false, true)] });
    }

    [Fact]
    public void Exports_NeverContainSecrets()
    {
        FillWithSecrets();
        var list = PortableData.Serialize(PortableData.ExportDownloads(_service.ExportRecords(), _service.Categories, _service.Queues, DateTime.UtcNow));
        var urls = PortableData.ExportUrls(_service.ExportRecords());
        var config = PortableData.Serialize(PortableData.ExportSettings(_service.Settings, _service.Categories, _service.Queues, _grabber.Projects, DateTime.UtcNow));

        foreach (var text in new[] { list, urls, config })
        {
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain("dpapi:", text);
        }
        Assert.Contains("https://files.test/a.zip", urls);
        Assert.Contains("SHA256:" + new string('a', 64), list);
        Assert.Contains("proxy.test", config);
        Assert.DoesNotContain("scan.exe", config);
        Assert.DoesNotContain("updates.test", config);
        Assert.Contains("books.test", config);
    }

    [Fact]
    public async Task RoundTrip_IntoAnotherInstallation_WithDuplicatesHandled()
    {
        FillWithSecrets();
        var listFile = Path.Combine(_dir, "list.json");
        var configFile = Path.Combine(_dir, "config.json");
        File.WriteAllText(listFile, PortableData.Serialize(PortableData.ExportDownloads(_service.ExportRecords(), _service.Categories, _service.Queues, DateTime.UtcNow)));
        _service.SaveQueue(new DownloadQueue { Name = "Notte", MaxConcurrent = 2 });
        File.WriteAllText(configFile, PortableData.Serialize(PortableData.ExportSettings(_service.Settings, _service.Categories, _service.Queues, _grabber.Projects, DateTime.UtcNow)));

        var (store, service, grabber) = Open("b.db");
        try
        {
            service.Add("https://files.test/b.iso", startNow: false); // already there
            var items = PortableImport.Plan(PortableData.Read(listFile), service.GetSnapshots(), service.Categories, service.Queues, grabber.Projects);
            Assert.Equal(3, items.Count);
            Assert.True(items.Single(i => i.Detail.EndsWith("b.iso")).IsDuplicate);
            Assert.False(items.Single(i => i.Detail.EndsWith("b.iso")).Include); // duplicates start unticked
            var result = PortableImport.Apply(items, DuplicateMode.Skip, service, grabber);
            Assert.Equal(2, result.Added);
            Assert.Equal(3, service.GetSnapshots().Count);
            Assert.All(service.GetSnapshots(), s => Assert.NotEqual(DownloadStatus.Downloading, s.Status));
            Assert.Null(store.LoadAll().Single(r => r.Url.EndsWith("export")).Context); // the form's data did not travel

            var config = PortableImport.Plan(PortableData.Read(configFile), service.GetSnapshots(), service.Categories, service.Queues, grabber.Projects);
            config.Single(i => i.Kind == ImportKind.Settings).Include = true;
            PortableImport.Apply(config, DuplicateMode.Replace, service, grabber);
            Assert.Contains(service.Queues, q => q.Name == "Notte" && q.MaxConcurrent == 2);
            Assert.Equal("proxy.test", service.Settings.ProxyHost);
            Assert.Equal("", service.Settings.ProxyPassword);
            Assert.Equal("", service.Settings.ScannerPath);
            Assert.Empty(grabber.Projects.Single(p => p.Name == "Libri").Cookies);

            PortableImport.Apply(PortableImport.Plan(PortableData.Read(configFile), service.GetSnapshots(), service.Categories, service.Queues, grabber.Projects),
                DuplicateMode.KeepBoth, service, grabber);
            Assert.Contains(service.Queues, q => q.Name == "Notte (2)");
        }
        finally
        {
            await grabber.DisposeAsync();
            await service.DisposeAsync();
            store.Dispose();
        }
    }

    [Fact]
    public void HostileImports_AreRefusedOrNeutralized()
    {
        var file = Path.Combine(_dir, "hostile.json");
        File.WriteAllText(file, """
            { "format": "DownloadManager", "version": 1, "kind": "settings",
              "settings": { "downloadFolder": "C:\\Downloads", "scannerPath": "C:\\evil.exe", "updateUrl": "https://evil.test/", "ffmpegPath": "C:\\evil.exe" },
              "categories": [ { "name": "Escape", "extensions": ["zip"], "folder": "..\\..\\Windows" } ],
              "queues": [], "projects": [ { "name": "x", "startUrl": "file:///C:/Windows/" } ],
              "downloads": [ { "url": "file:///C:/Windows/win.ini", "status": "Paused", "addedUtc": "2026-01-01T00:00:00Z" },
                             { "url": "https://ok.test/a.zip", "directory": "relative\\path", "status": "Paused", "addedUtc": "2026-01-01T00:00:00Z" },
                             { "url": "https://ok.test/b.zip", "fileName": "..\\..\\b.zip", "status": "Paused", "addedUtc": "2026-01-01T00:00:00Z" } ] }
            """);
        var items = PortableImport.Plan(PortableData.Read(file), _service.GetSnapshots(), _service.Categories, _service.Queues, _grabber.Projects);
        Assert.NotNull(items.Single(i => i.Kind == ImportKind.Download && i.Detail.StartsWith("file:")).Problem);
        Assert.NotNull(items.Single(i => i.Detail.EndsWith("a.zip")).Problem);
        Assert.NotNull(items.Single(i => i.Kind == ImportKind.Category).Problem);
        Assert.NotNull(items.Single(i => i.Kind == ImportKind.Project).Problem);
        foreach (var item in items)
            item.Include = true;
        PortableImport.Apply(items, DuplicateMode.KeepBoth, _service, _grabber);

        var imported = Assert.Single(_service.GetSnapshots());
        Assert.Equal("b.zip", imported.DisplayName); // the name is only a name
        Assert.Equal("", _service.Settings.ScannerPath);
        Assert.Equal("", _service.Settings.UpdateUrl);
        Assert.Equal("", _service.Settings.FfmpegPath);
        Assert.DoesNotContain(_service.Categories, c => c.Name == "Escape");

        File.WriteAllText(file, "{ \"format\": \"Other\" }");
        Assert.Throws<FormatException>(() => PortableData.Read(file));
    }

    [Fact]
    public void NullsAndOutOfRangeNumbers_InAnImportedFile_DoNotBreakTheImport()
    {
        var file = Path.Combine(_dir, "hostile.json");
        File.WriteAllText(file, """
            { "format": "DownloadManager", "kind": "settings",
              "downloads": [null], "categories": [null, { "name": null }], "queues": [null, { "name": "Q", "maxConcurrent": -4, "schedule": { "days": null } }],
              "projects": [null, { "name": "P", "startUrl": "https://p.test/", "allowedDomains": null, "extensions": null, "schedule": null }],
              "settings": { "downloadFolder": "C:\\\\Dl", "maxConnections": 100000, "timeoutSeconds": 0, "proxyPort": -1 } }
            """);

        var items = PortableImport.Plan(PortableData.Read(file), _service.GetSnapshots(), _service.Categories, _service.Queues, _grabber.Projects);

        Assert.NotNull(items.Single(i => i.Kind == ImportKind.Category).Problem);
        Assert.NotNull(items.Single(i => i.Kind == ImportKind.Queue).Problem);
        Assert.Null(items.Single(i => i.Kind == ImportKind.Project).Problem);
        var settings = items.Single(i => i.Kind == ImportKind.Settings);
        settings.Include = true;
        PortableImport.Apply(items, DuplicateMode.KeepBoth, _service, _grabber);
        Assert.Equal(32, _service.Settings.MaxConnections);
        Assert.Equal(5, _service.Settings.TimeoutSeconds);
        Assert.InRange(_service.Settings.ProxyPort, 1, 65535);
    }

    [Fact]
    public void ImportedEntries_NeverPointAtFilesOfThisPc_NorRunByThemselves()
    {
        var documents = Path.Combine(_dir, "Documents");
        var id = _service.AddImported(new ExportedDownload("https://x.test/thesis.docx", "thesis.docx", documents, null, null, "Completed", 10,
            DateTime.UtcNow, DateTime.UtcNow, null, null));
        var imported = _service.GetSnapshot(id)!;
        Assert.Equal(DownloadStatus.Paused, imported.Status);
        Assert.NotEqual(documents, imported.Directory);

        var file = new PortableFile
        {
            Kind = PortableFile.SettingsKind,
            Queues = [new ExportedQueue("Night", null, 2, new QueueSchedule { Enabled = true }, 0, 0, 0, EndAction.Shutdown)],
            Projects = [new Core.Grabber.GrabberProject { Name = "Hourly", StartUrl = "https://p.test/", Folder = Path.Combine(_dir, "p"),
                Schedule = new QueueSchedule { Enabled = true } }],
        };
        PortableImport.Apply(PortableImport.Plan(file, _service.GetSnapshots(), _service.Categories, _service.Queues, _grabber.Projects),
            DuplicateMode.KeepBoth, _service, _grabber);
        var queue = _service.Queues.Single(q => q.Name == "Night");
        Assert.Equal(EndAction.None, queue.EndAction);
        Assert.False(queue.Schedule.Enabled);
        Assert.False(_grabber.Projects.Single(p => p.Name == "Hourly").Schedule.Enabled);
    }

    [Fact]
    public void PlainTextLists_AreImportedAsAddresses()
    {
        var file = Path.Combine(_dir, "list.txt");
        File.WriteAllText(file, "https://a.test/1.zip\nnot an address\nftp://b.test/2.iso and https://a.test/1.zip again\n");
        var items = PortableImport.Plan(PortableData.Read(file), _service.GetSnapshots(), _service.Categories, _service.Queues, _grabber.Projects);
        Assert.Equal(["https://a.test/1.zip", "ftp://b.test/2.iso"], items.Select(i => i.Detail));
        PortableImport.Apply(items, DuplicateMode.Skip, _service, _grabber);
        Assert.Equal(2, _service.GetSnapshots().Count);
        Assert.All(_service.GetSnapshots(), s => Assert.Equal(DownloadStatus.Paused, s.Status));
    }
}
