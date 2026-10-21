using Baton.Core;
using Baton.Core.Setup;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>First-run wizard and the one-time move of folders away from the Desktop (on a fake Desktop, never the real one).</summary>
public sealed class SetupTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private DownloadStore _store = null!;
    private DownloadService _service = null!;
    private string Desktop => Path.Combine(_dir, "Desktop");
    private string Target => Path.Combine(_dir, "Downloads", "DownloadManager");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Desktop);
        _store = new DownloadStore(Path.Combine(_dir, "state.db"));
        _store.SaveSettings(new AppSettings { DownloadFolder = Path.Combine(Desktop, "Scaricati"), ProxyMode = ProxyMode.None });
        _service = new DownloadService(_store, autoTick: false);
        await _service.StopQueueAsync();
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Browsers_AreFoundWhereTheyAreInstalled()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"X:\PF86\Microsoft\Edge\Application\msedge.exe",
            @"X:\Local\Programs\Opera\opera.exe",
            @"X:\PF\Mozilla Firefox\firefox.exe",
        };
        string Folder(Environment.SpecialFolder f) => f switch
        {
            Environment.SpecialFolder.ProgramFiles => @"X:\PF",
            Environment.SpecialFolder.ProgramFilesX86 => @"X:\PF86",
            _ => @"X:\Local",
        };
        var browsers = BrowserDetector.Detect(installed.Contains, Folder);
        Assert.Equal(["Microsoft Edge", "Opera", "Mozilla Firefox"], browsers.Select(b => b.Name));
        Assert.True(browsers[2].IsFirefox);
        Assert.Equal("edge://extensions", browsers[0].ExtensionsPage);
        Assert.Equal(@"Software\Google\Chrome", browsers[1].HostKey); // Opera reads Chrome's registration
    }

    [Fact]
    public void FirstRun_OnlyForANewInstallation_AndItsChoicesAreApplied()
    {
        Assert.False(FirstRun.IsNeeded(Path.Combine(_dir, "state.db")));
        Assert.True(FirstRun.IsNeeded(Path.Combine(_dir, "missing.db")));
        Assert.DoesNotContain("Desktop", new FirstRunChoices().DownloadFolder, StringComparison.OrdinalIgnoreCase);

        var video = _service.Categories.Single(c => c.Key == Categories.VideoKey);
        FirstRun.Apply(_service, new FirstRunChoices
        {
            DownloadFolder = Path.Combine(_dir, "Downloads"),
            Theme = AppTheme.Dark,
            CategoryFolders = { [video.Id] = "Film" },
        });
        Assert.Equal(Path.Combine(_dir, "Downloads"), _store.LoadSettings().DownloadFolder);
        Assert.Equal(AppTheme.Dark, _service.Settings.Theme);
        Assert.Equal("Film", _service.Categories.Single(c => c.Id == video.Id).Folder);
    }

    /// <summary>A finished download of an earlier session: written to the database, then the service is opened again.</summary>
    private async Task<long> CompletedAsync(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), name);
        var queue = _service.MainQueueId;
        await _service.DisposeAsync();
        var id = _store.Insert(new DownloadRecord
        {
            Url = "https://files.test/" + name, Directory = folder, FileName = name, Status = DownloadStatus.Completed, TotalSize = name.Length,
            AddedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow, QueueId = queue,
        });
        _service = new DownloadService(_store, autoTick: false);
        return id;
    }

    [Fact]
    public async Task DesktopFolders_ArePlannedForDownloads_AndFilesMoveOnlyWhenAsked()
    {
        var music = _service.Categories.Single(c => c.Key == Categories.MusicKey).Clone();
        music.Folder = Path.Combine(Desktop, "Musica");
        _service.SaveCategory(music);
        var docs = _service.Categories.Single(c => c.Key == Categories.DocumentsKey).Clone();
        docs.Folder = Path.Combine(_dir, "Altrove");
        _service.SaveCategory(docs);
        var onDesktop = await CompletedAsync(Path.Combine(Desktop, "Musica"), "song.mp3");
        var elsewhere = await CompletedAsync(Path.Combine(_dir, "Altrove"), "doc.pdf");
        File.WriteAllText(Path.Combine(Desktop, "unrelated.txt"), "the user's own file");

        var plan = DesktopRelocation.Plan(_service.Settings, _service.Categories, _service.ExportRecords(), [Desktop], Target);
        Assert.Equal(Path.Combine(Target, "Scaricati"), plan.DownloadFolder);
        Assert.Contains(plan.Folders, f => f.CategoryId == music.Id && f.To == Path.Combine(Target, "Musica"));
        Assert.DoesNotContain(plan.Folders, f => f.CategoryId == docs.Id);
        Assert.Equal([onDesktop], plan.Files.Select(f => f.DownloadId));

        // Folders only: the files stay where they are.
        var result = _service.ApplyRelocation(plan, moveFiles: false);
        Assert.Equal(0, result.FilesMoved);
        Assert.True(File.Exists(Path.Combine(Desktop, "Musica", "song.mp3")));
        Assert.Equal(Path.Combine(Target, "Scaricati"), _service.Settings.DownloadFolder);
        Assert.Equal(Path.Combine(Target, "Musica"), _service.Categories.Single(c => c.Id == music.Id).Folder);

        // Asked to move the files too.
        var again = DesktopRelocation.Plan(_service.Settings, _service.Categories, _service.ExportRecords(), [Desktop], Target);
        Assert.True(again.IsEmpty);
        Assert.Single(again.Files);
        Assert.Equal(1, _service.ApplyRelocation(again, moveFiles: true).FilesMoved);
        Assert.True(File.Exists(Path.Combine(Target, "Musica", "song.mp3")));
        Assert.Equal(Path.Combine(Target, "Musica", "song.mp3"), _service.GetSnapshot(onDesktop)!.FinalPath);
        Assert.True(File.Exists(Path.Combine(Desktop, "unrelated.txt"))); // never touched
        Assert.True(File.Exists(Path.Combine(_dir, "Altrove", "doc.pdf")));
        Assert.NotNull(_service.GetSnapshot(elsewhere));
    }
}
