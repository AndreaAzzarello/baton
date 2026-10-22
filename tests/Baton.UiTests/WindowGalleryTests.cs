using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Baton.App;
using Baton.App.Services;
using Baton.App.ViewModels;
using Baton.App.Views;
using Baton.Core;
using Baton.Core.Grabber;
using Baton.Core.Integration;
using Baton.Core.Net;
using Baton.Core.Portable;
using Baton.Core.Security;
using Baton.Core.Setup;
using Baton.Core.Updates;
using Microsoft.Data.Sqlite;

namespace Baton.UiTests;

/// <summary>
/// Every window of the app, in English and Italian (and in the pseudo-language when Strings.qps-ploc.resx was generated):
/// no label, button or title may be cut. Each window's content is moved into a host window off screen that never takes
/// the focus (several windows activate themselves when loaded). With BATON_SHOTS=folder, every window is also saved as a
/// PNG in both themes, for a look by eye.
/// </summary>
public sealed class WindowGalleryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-uitests", "gallery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly bool PseudoAvailable =
        Baton.Core.Resources.Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("qps-ploc"), true, false) is not null;

    [Fact]
    public void EveryWindow_ShowsItsTextsWhole_InEveryLanguage()
    {
        var shots = Environment.GetEnvironmentVariable("BATON_SHOTS");
        var problems = new List<string>();
        var pseudoProblems = new List<string>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                // The app's resources as App.xaml merges them. Relative addresses (the windows' "app.ico") belong to the app's
                // assembly, not to the test host: the resource assembly can be set only once, so it is set directly.
                typeof(Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .SetValue(null, typeof(Baton.App.App).Assembly);
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Clear();
                foreach (var name in new[] { "Light", "Controls" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Baton;component/Themes/{name}.xaml") });
                var themes = new ThemeService(app);
                var languages = new List<(string Name, AppLanguage Language, bool Pseudo)> { ("en", AppLanguage.English, false), ("it", AppLanguage.Italian, false) };
                if (PseudoAvailable)
                    languages.Add(("pseudo", AppLanguage.English, true));
                foreach (var (name, language, pseudo) in languages)
                {
                    SetLanguage(language, pseudo);
                    foreach (var theme in shots is null ? [AppTheme.Light] : new[] { AppTheme.Light, AppTheme.Dark })
                    {
                        themes.Apply(theme);
                        using var demo = new Demo(Path.Combine(_dir, $"{name}-{theme}"));
                        foreach (var (window, title) in demo.Windows())
                        {
                            var found = Inspect(window, shots is null ? null : Path.Combine(shots, $"{name}-{theme.ToString().ToLowerInvariant()}-{title}.png"));
                            // Only the real languages must fit; the pseudo-language is listed with the shots.
                            if (!pseudo)
                                problems.AddRange(found.Select(p => $"{name} {title}: {p}"));
                            else if (theme == AppTheme.Light)
                                pseudoProblems.AddRange(found.Select(p => $"{title}: {p}"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Localizer.Apply(AppLanguage.Italian);
                // The application outlives this thread: its brushes, styles and Fluent theme belong to it and would break
                // the windows of the other tests, made on other threads. They go back to WPF's defaults.
                if (Application.Current is { } app)
                {
                    app.ThemeMode = ThemeMode.None;
                    app.Resources = new ResourceDictionary();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
        if (shots is not null)
            File.WriteAllLines(Path.Combine(shots, "cut-texts.txt"), problems.Concat(pseudoProblems.Select(p => "pseudo " + p)));
        Assert.True(problems.Count == 0, "cut texts:\n" + string.Join("\n", problems));
    }

    private static void SetLanguage(AppLanguage language, bool pseudo)
    {
        Localizer.Apply(language);
        if (pseudo)
        {
            // The texts of the pseudo-language, the formats of English (what BATON_PSEUDO=1 does in the app).
            CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("qps-ploc");
            Baton.Core.Resources.Strings.Culture = CultureInfo.CurrentUICulture;
            LocalizedStrings.Instance.Refresh();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Shows the window's content off screen, lists the texts that do not fit and saves a picture when asked.</summary>
    private static List<string> Inspect(Window window, string? png)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Window
        {
            Title = window.Title,
            Content = content,
            DataContext = window.DataContext,
            Style = window.Style,
            Width = window.Width,
            Height = window.Height,
            MinWidth = window.MinWidth,
            MinHeight = window.MinHeight,
            SizeToContent = window.SizeToContent,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            ShowInTaskbar = false,
            Language = window.Language,
        };
        foreach (var key in window.Resources.Keys)
            host.Resources[key] = window.Resources[key];
        host.Show();
        Pump();
        host.UpdateLayout();
        Pump();

        var problems = new List<string>();
        Check(content, content, problems);
        if (png is not null)
            Save(host, content, png);
        host.Close();
        window.Close();
        return problems;
    }

    private static void Check(FrameworkElement root, DependencyObject node, List<string> problems)
    {
        if (node is UIElement { IsVisible: false })
            return;
        // Lists show data (file names, addresses): trimmed on purpose.
        if (node is DataGrid or ListBox or TreeView or ComboBox or ToolTip or TextBox)
            return;
        if (node is TextBlock { Text.Length: > 0 } text && text.TextWrapping == TextWrapping.NoWrap && text.ActualWidth > 0)
        {
            var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            var needed = formatted.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;
            if (needed > text.ActualWidth + 1)
                problems.Add($"\"{text.Text}\" needs {needed:0} px, has {text.ActualWidth:0}");
            else if (Outside(root, text))
                problems.Add($"\"{text.Text}\" is outside the window");
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Check(root, VisualTreeHelper.GetChild(node, i), problems);
    }

    /// <summary>Beyond the right or bottom edge of the window (and not in a part that scrolls).</summary>
    private static bool Outside(FrameworkElement root, FrameworkElement element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null && parent != root; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer or ScrollContentPresenter or Popup)
                return false;
        }
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        return bounds.Right > root.ActualWidth + 1 || bounds.Bottom > root.ActualHeight + 1;
    }

    private static void Save(Window host, FrameworkElement content, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const double scale = 1.5;
        var width = content.ActualWidth;
        var height = content.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(host.Background ?? (Brush)Application.Current.FindResource("SurfaceBrush"), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>A database with a few downloads in every state, and every window of the app on it.</summary>
    private sealed class Demo : IDisposable
    {
        private readonly string _dir;
        private readonly DownloadStore _store;
        private readonly DownloadService _service;
        private readonly MainViewModel _main;
        private readonly IDialogService _dialogs;
        private readonly IShellService _shell;

        public Demo(string dir)
        {
            _dir = dir;
            Directory.CreateDirectory(dir);
            var db = Path.Combine(dir, "downloads.db");
            using (new DownloadStore(db)) { }
            using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
            {
                connection.Open();
                var rows = new (string Name, int Status, long? Size, long Written)[]
                {
                    ("ubuntu-24.04.1-desktop-amd64.iso", 3, 6_114_656_256, 0),
                    ("Annual report 2025.pdf", 3, 2_480_000, 0),
                    ("vacation-video.mp4", 2, 1_288_490_188, 412_000_000),
                    ("dataset.zip", 2, null, 52_000_000),
                    ("setup-tool.exe", 4, 84_000_000, 0),
                    ("podcast-episode-12.mp3", 0, 64_000_000, 0),
                };
                foreach (var (name, status, size, written) in rows)
                {
                    using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO downloads(url, directory, file_name, total_size, supports_ranges, status, error, sort_order, added_utc, completed_utc)
                        VALUES ($url, $dir, $name, $size, 1, $status, $error, (SELECT count(*) FROM downloads), '2026-10-21T10:00:00Z', $done);
                        INSERT INTO segments (download_id, idx, start, end_pos, written) SELECT last_insert_rowid(), 0, 0, coalesce($size, 0) - 1, $written WHERE $written > 0;
                        """;
                    insert.Parameters.AddWithValue("$url", "https://downloads.example.com/files/" + Uri.EscapeDataString(name));
                    insert.Parameters.AddWithValue("$dir", dir);
                    insert.Parameters.AddWithValue("$name", name);
                    insert.Parameters.AddWithValue("$size", (object?)size ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$status", status);
                    insert.Parameters.AddWithValue("$error", status == 4 ? Baton.Core.Resources.Strings.ErrorAllConnectionsRefused : DBNull.Value);
                    insert.Parameters.AddWithValue("$done", status == 3 ? "2026-10-21T10:05:00Z" : DBNull.Value);
                    insert.Parameters.AddWithValue("$written", written);
                    insert.ExecuteNonQuery();
                }
            }
            _store = new DownloadStore(db);
            _service = new DownloadService(_store, autoTick: false);
            _dialogs = Stub<IDialogService>();
            _shell = Stub<IShellService>();
            _main = new MainViewModel(_service, _store, _dialogs, _shell, Stub<IStartupService>(),
                new GrabberUi(new GrabberService(_service, _store), _service, _dialogs, _shell));
            _main.Refresh();
        }

        private static T Stub<T>() where T : class => System.Reflection.DispatchProxy.Create<T, MainListCommandsTests.Fake<T>>();

        public IEnumerable<(Window Window, string Title)> Windows()
        {
            var categories = _service.Categories;
            var queues = _service.Queues;
            var folder = _service.Settings.DownloadFolder;
            var main = new MainWindow(_main, () => Task.CompletedTask, () => false);
            _main.UpdateSelection(_main.Items.Take(3));
            yield return (main, "main");
            yield return (new SettingsWindow(new SettingsViewModel(_dialogs, _service.Settings, runAtStartup: false, _service.Credentials, _service.Certificates,
                _ => Task.CompletedTask, () => { })), "settings");
            yield return (new AddDownloadWindow(new AddDownloadViewModel(_dialogs, categories, folder, "https://downloads.example.com/files/archive.zip",
                note: Baton.Core.Resources.Strings.FromBrowserWithCookies, queues: queues)), "add");
            yield return (new CategoriesWindow(new CategoriesViewModel(_dialogs, categories, folder)), "categories");
            yield return (new QueuesWindow(new QueuesViewModel(_dialogs, queues, () => DateTime.UtcNow, startupEnabled: false, () => { })), "queues");
            yield return (new CountdownWindow(EndAction.Shutdown, queues[0].DisplayName, TimeSpan.FromSeconds(60)), "countdown");
            yield return (new SpeedLimitWindow(512), "speedlimit");
            yield return (new LoginWindow(new LoginRequest(1, new Uri("http://intranet.example.com/"), false, "Basic", "Company files", "report.pdf")), "login");
            yield return (new CertificateWindow(new CertificateQuestion("ftp.example.com", 990, "CN=ftp.example.com", "CN=Example Self-Signed",
                new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89",
                SslPolicyErrors.RemoteCertificateChainErrors)), "certificate");
            var snapshot = _service.GetSnapshots().First(s => s.Status == DownloadStatus.Paused);
            yield return (new PropertiesWindow(new PropertiesViewModel(snapshot, _service, _dialogs, _shell)), "properties");
            yield return (new ProgressWindow(new ProgressViewModel(_main.Items.First(i => i.Status == DownloadStatus.Paused), _service, _shell)), "progress");
            yield return (new BatchWindow(new BatchViewModel(new BatchRequest("https://www.example.com/gallery", "Gallery",
                [new BatchItem("https://www.example.com/a.zip", "Archive", "link"), new BatchItem("https://www.example.com/photo.jpg", "Photo", "image"),
                 new BatchItem("https://www.example.com/manual.pdf", "Manual", "link")]), _dialogs, categories, folder, queues, queues[0].Id)), "batch");
            yield return (new GrabberProjectsWindow(new GrabberProjectsViewModel(new GrabberUi(new GrabberService(_service, _store), _service, _dialogs, _shell), _dialogs, _shell)), "grabber-projects");
            var wizard = new GrabberWizardViewModel(_dialogs, new GrabberProject { Name = "Example", StartUrl = "https://www.example.com/" }, categories, queues, folder, () => DateTime.UtcNow);
            for (var step = 0; step < 5; step++)
            {
                if (step > 0)
                    wizard.NextCommand.Execute(null);
                yield return (new GrabberWizardWindow(wizard), $"grabber-wizard-{step + 1}");
            }
            yield return (new ImportWindow(new ImportViewModel("baton-export.json",
            [
                new ImportItem(ImportKind.Download, "report.pdf", "https://www.example.com/report.pdf", new object()),
                new ImportItem(ImportKind.Category, "Books", "epub mobi", new object()) { IsDuplicate = true },
                new ImportItem(ImportKind.Queue, "Night", "2", new object()),
            ])), "import");
            yield return (new ToolbarWindow(new ToolbarViewModel("", smallIcons: false, showText: true)), "toolbar");
            yield return (new FirstRunWindow(new FirstRunViewModel(_dialogs, _shell, categories,
                [new DetectedBrowser("Microsoft Edge", false, "msedge.exe", "edge://extensions", "Microsoft\\Edge"),
                 new DetectedBrowser("Firefox", true, "firefox.exe", "about:debugging", "Mozilla")], _dir, () => null)), "firstrun");
            yield return (new UpdateWindow(new UpdateViewModel(new UpdateInfo(new Version(1, 3, 0), "- English interface\n- Installer", new Uri("https://example.com/Baton.exe"), new string('0', 64), "Baton.exe"),
                "1.2.0", (_, _) => Task.CompletedTask)), "update");
            yield return (new MessageWindow(Baton.Core.Resources.Strings.ConfirmRemove.Replace("{0}", "3"), Baton.Core.Resources.Strings.RemoveTitle, MessageKind.Question,
                [(Baton.Core.Resources.Strings.Ok, Answer.Yes), (Baton.Core.Resources.Strings.Cancel, Answer.Cancel)], Answer.Yes), "message");
        }

        public void Dispose()
        {
            _main.Stop();
            Task.Run(() => _service.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            _store.Dispose();
        }
    }
}
