using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Baton.App;
using Baton.App.Services;
using Baton.App.ViewModels;
using Baton.Core;
using Baton.Core.Grabber;
using Baton.Core.Platform;
using Microsoft.Data.Sqlite;

namespace Baton.UiTests;

/// <summary>
/// The main list's commands on a selection of several downloads, on the real view model and download service over a
/// temporary database. Dialogs and the shell are recorded stand-ins; files go to a recording Recycle Bin.
/// </summary>
public sealed class MainListCommandsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-uitests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Any interface: calls are recorded; Confirm answers as told, everything else returns its default.</summary>
    public class Fake<T> : DispatchProxy
    {
        public List<(string Method, object?[] Args)> Calls { get; } = [];
        public bool ConfirmAnswer { get; set; } = true;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add((method!.Name, args ?? []));
            if (method.Name == "Confirm")
                return ConfirmAnswer;
            var type = method.ReturnType;
            return type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
        }
    }

    private static (T Proxy, Fake<T> Fake) Make<T>() where T : class
    {
        var proxy = DispatchProxy.Create<T, Fake<T>>();
        return (proxy, (Fake<T>)(object)proxy);
    }

    private sealed class RecordingBin : IRecycleBin
    {
        public List<string> Received { get; } = [];

        public bool Recycle(string path)
        {
            Received.Add(path);
            File.Delete(path); // what the real Recycle Bin does to the folder (the file then lives in the bin)
            return true;
        }
    }

    /// <summary>Statuses 2 paused, 3 completed (with its file on the disk), 4 failed; a negative size is unknown.</summary>
    private string Database(params (string Name, int Status, long Size)[] rows)
    {
        Directory.CreateDirectory(_dir);
        var db = Path.Combine(_dir, "downloads.db");
        using (new DownloadStore(db)) { }
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        foreach (var (name, status, size) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO downloads(url, directory, file_name, total_size, supports_ranges, status, sort_order, added_utc, completed_utc)
                VALUES ($url, $dir, $name, $size, 1, $status, (SELECT count(*) FROM downloads), '2026-10-21T10:00:00Z', $done);
                """;
            insert.Parameters.AddWithValue("$url", "https://example.test/" + name);
            insert.Parameters.AddWithValue("$dir", _dir);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$size", size < 0 ? DBNull.Value : size);
            insert.Parameters.AddWithValue("$status", status);
            insert.Parameters.AddWithValue("$done", status == 3 ? "2026-10-21T10:05:00Z" : DBNull.Value);
            insert.ExecuteNonQuery();
            if (status == 3)
                File.WriteAllBytes(Path.Combine(_dir, name), new byte[size]);
        }
        return db;
    }

    private static void Sta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // As on the app's UI thread: what follows an await comes back here.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Localizer.Apply(AppLanguage.Italian); // the texts checked below
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>Runs the dispatcher (async commands continue on it) until the condition holds.</summary>
    private static void Pump(Func<bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!until() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        Assert.True(until(), "timed out");
    }

    private sealed record Setup(MainViewModel Vm, DownloadService Service, Fake<IDialogService> Dialogs, RecordingBin Bin, DownloadStore Store) : IDisposable
    {
        public DownloadItemViewModel[] Items(params string[] names) => names.Select(n => Vm.Items.Single(i => i.Name == n)).ToArray();

        public void Dispose()
        {
            Vm.Stop();
            Task.Run(() => Service.DisposeAsync().AsTask()).GetAwaiter().GetResult(); // not on the dispatcher: it would wait for itself
            Store.Dispose();
        }
    }

    private Setup Open(string db)
    {
        var store = new DownloadStore(db);
        var service = new DownloadService(store, autoTick: false);
        var bin = new RecordingBin();
        service.RecycleBin = bin;
        var (dialogs, fakeDialogs) = Make<IDialogService>();
        var (shell, _) = Make<IShellService>();
        var (startup, _) = Make<IStartupService>();
        var vm = new MainViewModel(service, store, dialogs, shell, startup, new GrabberUi(new GrabberService(service, store), service, dialogs, shell));
        vm.Refresh();
        return new Setup(vm, service, fakeDialogs, bin, store);
    }

    [Fact]
    public void MixedSelection_EnablesWhatAppliesToSomeOfIt_AndNamesTheCount() => Sta(() =>
    {
        using var s = Open(Database(("a.zip", 3, 1000), ("b.zip", 2, 0), ("c.zip", 4, 0), ("d.zip", 3, 3000)));
        s.Vm.UpdateSelection(s.Items("a.zip", "b.zip", "c.zip", "d.zip"));
        Assert.True(s.Vm.OpenFileCommand.CanExecute(null)); // the finished ones
        Assert.True(s.Vm.StartCommand.CanExecute(null)); // the paused and the failed one
        Assert.True(s.Vm.RetryCommand.CanExecute(null)); // the failed one
        Assert.False(s.Vm.PauseCommand.CanExecute(null)); // nothing runs
        Assert.False(s.Vm.PropertiesCommand.CanExecute(null)); // one download only
        Assert.True(s.Vm.RemoveCommand.CanExecute(null) && s.Vm.DeleteFilesCommand.CanExecute(null));
        Assert.Equal("Rimuovi 4 download dalla lista", s.Vm.RemoveText);
        Assert.Equal("Elimina 4 download dalla lista e dal disco", s.Vm.DeleteFilesText);
        Assert.StartsWith("4 selezionati", s.Vm.SelectionText);
        Assert.Contains(Format.Bytes(4000), s.Vm.SelectionText);

        s.Vm.UpdateSelection(s.Items("a.zip"));
        Assert.True(s.Vm.PropertiesCommand.CanExecute(null));
        Assert.False(s.Vm.RetryCommand.CanExecute(null));
        Assert.False(s.Vm.StartCommand.CanExecute(null));
        Assert.Equal("Rimuovi dalla lista", s.Vm.RemoveText);
        Assert.StartsWith("1 selezionato", s.Vm.SelectionText);

        s.Vm.UpdateSelection([]);
        Assert.Equal("", s.Vm.SelectionText);
        Assert.False(s.Vm.RemoveCommand.CanExecute(null));
    });

    [Fact]
    public void StatusBar_ShowsDownloadedOfTotal_AndAQuestionMarkWhenATotalIsUnknown() => Sta(() =>
    {
        var path = Database(("done.zip", 3, 3000), ("half.zip", 2, 2048), ("nosize.bin", 2, -1));
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var progress = db.CreateCommand(); // 1 KB written of each paused one
            progress.CommandText = """
                INSERT INTO segments (download_id, idx, start, end_pos, written)
                SELECT id, 0, 0, coalesce(total_size, 0) - 1, 1024 FROM downloads WHERE status = 2;
                """;
            progress.ExecuteNonQuery();
        }
        using var t = Open(path);

        t.Vm.UpdateSelection(t.Items("done.zip", "half.zip"));
        Assert.Equal($"2 selezionati  ·  {Format.Bytes(4024)} di {Format.Bytes(5048)}", t.Vm.SelectionText);
        Assert.Null(t.Vm.SelectionToolTip);

        t.Vm.UpdateSelection(t.Items("half.zip"));
        Assert.Equal($"1 selezionato  ·  {Format.Bytes(1024)} di {Format.Bytes(2048)}", t.Vm.SelectionText);

        t.Vm.UpdateSelection(t.Items("done.zip", "nosize.bin"));
        Assert.Equal($"2 selezionati  ·  {Format.Bytes(4024)} di ?", t.Vm.SelectionText);
        Assert.Equal("Dimensione totale sconosciuta", t.Vm.SelectionToolTip);

        Localizer.Apply(AppLanguage.English);
        Assert.Equal($"2 selected  ·  {Format.Bytes(4024)} of ?", t.Vm.SelectionText);
        Assert.Equal("Total size unknown", t.Vm.SelectionToolTip);
        Localizer.Apply(AppLanguage.Italian);
    });

    [Fact]
    public void ShiftDelete_AsksWithCountAndSize_ThenSendsTheFilesToTheRecycleBin() => Sta(() =>
    {
        using var s = Open(Database(("a.zip", 3, 1000), ("b.zip", 2, 0), ("c.zip", 3, 3000), ("keep.zip", 3, 500)));
        s.Vm.UpdateSelection(s.Items("a.zip", "b.zip", "c.zip"));

        s.Dialogs.ConfirmAnswer = false;
        s.Vm.DeleteFilesCommand.Execute(null);
        Assert.Equal(4, s.Service.GetSnapshots().Count); // answered no: nothing happens
        var question = (string)s.Dialogs.Calls.Last(c => c.Method == "Confirm").Args[0]!;
        Assert.Contains("3 download", question);
        Assert.Contains(Format.Bytes(4000), question);
        Assert.Contains("Cestino", question);

        s.Dialogs.ConfirmAnswer = true;
        s.Vm.DeleteFilesCommand.Execute(null);
        Pump(() => s.Service.GetSnapshots().Count == 1);
        Assert.Equal([Path.Combine(_dir, "a.zip"), Path.Combine(_dir, "c.zip")], s.Bin.Received.Order());
        Assert.True(File.Exists(Path.Combine(_dir, "keep.zip")));
    });

    [Fact]
    public void Delete_RemovesFromTheListOnly_AskingWhenThereIsMoreThanOne() => Sta(() =>
    {
        using var s = Open(Database(("a.zip", 3, 100), ("b.zip", 3, 100), ("c.zip", 2, 0)));
        s.Vm.UpdateSelection(s.Items("a.zip"));
        s.Vm.RemoveCommand.Execute(null);
        Pump(() => s.Service.GetSnapshots().Count == 2);
        Assert.DoesNotContain(s.Dialogs.Calls, c => c.Method == "Confirm"); // one download: no question

        s.Vm.Refresh();
        s.Vm.UpdateSelection(s.Items("b.zip", "c.zip"));
        s.Vm.RemoveCommand.Execute(null);
        Pump(() => s.Service.GetSnapshots().Count == 0);
        Assert.Contains(s.Dialogs.Calls, c => c.Method == "Confirm" && ((string)c.Args[0]!).Contains("2 download"));
        Assert.Empty(s.Bin.Received); // files stay on the disk
        Assert.True(File.Exists(Path.Combine(_dir, "a.zip")) && File.Exists(Path.Combine(_dir, "b.zip")));
    });

    [Fact]
    public void Delete_OfOneUnfinishedDownload_AsksFirst_ItsPartialDataWouldGo() => Sta(() =>
    {
        using var s = Open(Database(("a.zip", 2, 0)));
        s.Vm.UpdateSelection(s.Items("a.zip"));
        s.Vm.RemoveCommand.Execute(null);
        Assert.Contains(s.Dialogs.Calls, c => c.Method == "Confirm" && ((string)c.Args[0]!).Contains("1 download"));
    });

    [Fact]
    public void ChangeCategory_AppliesToTheWholeSelection() => Sta(() =>
    {
        using var s = Open(Database(("a.zip", 3, 10), ("b.pdf", 3, 10), ("c.mp3", 2, 0)));
        var target = s.Vm.Categories.First(c => c.Name == "Video").Id;
        s.Vm.ChangeCategory(s.Items("a.zip", "b.pdf", "c.mp3").Select(i => i.Id).ToList(), target);
        Assert.All(s.Service.GetSnapshots(), snap => Assert.Equal(target, snap.CategoryId));
    });
}
