using Baton.Core;
using Baton.Core.Grabber;
using Baton.Core.Portable;
using Baton.Core.Security;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

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
            Assert.Equal(Core.Resources.Strings.ImportNetworkWarning, config.Single(i => i.Kind == ImportKind.NetworkSettings).Warning);
            config.Single(i => i.Kind == ImportKind.NetworkSettings).Include = true; // the proxy needs its own, warned tick
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
        Assert.NotNull(items.Single(i => i.Kind == ImportKind.Category).Problem);
        Assert.NotNull(items.Single(i => i.Kind == ImportKind.Project).Problem);
        foreach (var item in items)
            item.Include = true;
        PortableImport.Apply(items, DuplicateMode.KeepBoth, _service, _grabber);
        Assert.DoesNotContain(_service.GetSnapshots(), s => s.Directory.Contains("relative")); // a folder from the file is never used

        Assert.Equal(["a.zip", "b.zip"], _service.GetSnapshots().Select(s => s.DisplayName).Order()); // the name is only a name
        Assert.Equal("", _service.Settings.ScannerPath);
        Assert.Equal(AppSettings.DefaultUpdateUrl, _service.Settings.UpdateUrl);
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
              "settings": { "downloadFolder": "PROFILE", "maxConnections": 100000, "timeoutSeconds": 0, "proxyPort": -1 } }
            """.Replace("PROFILE", ProfileFolder("Dl").Replace(@"\", @"\\")));

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
            Projects = [new Core.Grabber.GrabberProject { Name = "Hourly", StartUrl = "https://p.test/", Folder = ProfileFolder("p"),
                Schedule = new QueueSchedule { Enabled = true } }],
        };
        PortableImport.Apply(PortableImport.Plan(file, _service.GetSnapshots(), _service.Categories, _service.Queues, _grabber.Projects),
            DuplicateMode.KeepBoth, _service, _grabber);
        var queue = _service.Queues.Single(q => q.Name == "Night");
        Assert.Equal(EndAction.None, queue.EndAction);
        Assert.False(queue.Schedule.Enabled);
        Assert.False(_grabber.Projects.Single(p => p.Name == "Hourly").Schedule.Enabled);
    }

    private static string ProfileFolder(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", name);

    [Fact]
    public void ImportedFolders_NeverOnTheNetwork_InSystemOrStartupFolders_OrOutsideTheProfile()
    {
        string Escape(string path) => path.Replace(@"\", @"\\");
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var file = Path.Combine(_dir, "folders.json");
        File.WriteAllText(file, $$"""
            { "format": "DownloadManager", "kind": "settings",
              "categories": [
                { "name": "Unc", "extensions": ["exe"], "folder": "\\\\attacker.test\\share" },
                { "name": "LongUnc", "extensions": ["exe"], "folder": "\\\\?\\UNC\\attacker.test\\share" },
                { "name": "Device", "extensions": ["exe"], "folder": "\\\\.\\PhysicalDrive0" },
                { "name": "Startup", "extensions": ["exe"], "folder": "{{Escape(startup)}}" },
                { "name": "Windows", "extensions": ["dll"], "folder": "C:\\Windows\\System32" },
                { "name": "Root", "extensions": ["zip"], "folder": "C:\\Dl" },
                { "name": "Fine", "extensions": ["pdf"], "folder": "{{Escape(ProfileFolder("Pdf"))}}" },
                { "name": "Relative", "extensions": ["txt"], "folder": "Testi" } ],
              "settings": { "downloadFolder": "{{Escape(ProfileFolder("ok"))}}", "proxyMode": "Pac", "pacUrl": "http://wpad.attacker.test/proxy.pac",
                            "confirmBrowserDownloads": false } }
            """);
        var items = PortableImport.Plan(PortableData.Read(file), _service.GetSnapshots(), _service.Categories, _service.Queues, _grabber.Projects);
        string? Problem(string name) => items.Single(i => i.Name == name).Problem;

        Assert.Equal(Core.Resources.Strings.ImportFolderNotLocal, Problem("Unc"));
        Assert.Equal(Core.Resources.Strings.ImportFolderNotLocal, Problem("LongUnc"));
        Assert.Equal(Core.Resources.Strings.ImportFolderNotLocal, Problem("Device"));
        Assert.Equal(Core.Resources.Strings.ImportFolderSensitive, Problem("Startup"));
        Assert.Equal(Core.Resources.Strings.ImportFolderSensitive, Problem("Windows"));
        Assert.Equal(Core.Resources.Strings.ImportFolderOutsideProfile, Problem("Root"));
        Assert.Null(Problem("Fine"));
        Assert.Null(Problem("Relative"));
        Assert.All(items, i => Assert.False(i.Include)); // nothing of a configuration is ticked in advance
        var network = items.Single(i => i.Kind == ImportKind.NetworkSettings);
        Assert.Equal(Core.Resources.Strings.ImportPacNotHttps, network.Problem); // a PAC over plain http could be swapped on the way
        Assert.Contains(Core.Resources.Strings.ImportNetworkNoConfirm, network.Detail);

        // Even ticked, refused items and the refused network part change nothing.
        foreach (var item in items)
            item.Include = true;
        PortableImport.Apply(items, DuplicateMode.KeepBoth, _service, _grabber);
        Assert.DoesNotContain(_service.Categories, c => c.Name is "Unc" or "Startup" or "Windows" or "Root");
        Assert.NotEqual(ProxyMode.Pac, _service.Settings.ProxyMode);
        Assert.True(_service.Settings.ConfirmBrowserDownloads);
        Assert.Equal(ProfileFolder("ok"), _service.Settings.DownloadFolder);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetShortPathName(string longPath, System.Text.StringBuilder shortPath, int length);

    [Fact]
    public void ImportedFolders_AreJudgedByTheirRealPath_NotByShortNamesOrLinks()
    {
        // A junction: what it leads to counts, also for a sub-folder that does not exist yet.
        var target = Path.Combine(_dir, "real target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_dir, "link");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
            mklink.WaitForExit();
        try
        {
            Assert.Equal(Path.Combine(target, "new", "sub"), SafePaths.RealPath(Path.Combine(link, "new", "sub")), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(link); // the junction only, never what it points to
        }

        // "C:\Users\x\APPDAT~1\...\Startup" is the Startup folder, even though it does not read as one.
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var buffer = new System.Text.StringBuilder(1024);
        var shortName = GetShortPathName(startup, buffer, buffer.Capacity) > 0 ? buffer.ToString() : startup;
        Assert.Equal(startup, SafePaths.RealPath(shortName), ignoreCase: true);
        Assert.Equal(Core.Resources.Strings.ImportFolderSensitive, SafePaths.CheckImportedFolder(Path.Combine(shortName, "x")));
        // PowerShell runs a profile.ps1 found there at every start.
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.Equal(Core.Resources.Strings.ImportFolderSensitive, SafePaths.CheckImportedFolder(Path.Combine(documents, "WindowsPowerShell")));
        Assert.Equal(Core.Resources.Strings.ImportFolderSensitive, SafePaths.CheckImportedFolder(Path.Combine(documents, "PowerShell", "Modules")));
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
