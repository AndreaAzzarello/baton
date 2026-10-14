using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using DownloadManager.Core;
using DownloadManager.Core.Resources;
using Forms = System.Windows.Forms;

namespace DownloadManager.App.Services;

/// <summary>
/// The icon in the notification area. A click opens a menu (a WPF menu, so it follows the app's theme) with the
/// speed limit, the queues and Exit; a double click shows the window.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Speed limits offered in the menu, in KB/s; 0 = no limit.</summary>
    public static readonly long[] PresetLimits = [0, 256, 512, 1024, 2048, 5120, 10240];

    private readonly Forms.NotifyIcon _icon;
    private readonly DownloadService _service;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly Func<long, long?> _askCustomLimit;
    private readonly ContextMenu _menu = new();

    public TrayIcon(DownloadService service, Action show, Action exit, Func<long, long?> askCustomLimit)
    {
        _service = service;
        _show = show;
        _exit = exit;
        _askCustomLimit = askCustomLimit;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        _icon = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(stream), Text = Strings.AppName, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right)
                OpenMenu();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _show();
        };
    }

    /// <summary>A short balloon, for example the first time the window hides in the notification area.</summary>
    public void ShowHint(string text) => _icon.ShowBalloonTip(5000, Strings.AppName, text, Forms.ToolTipIcon.Info);

        /// <summary>Tooltip with the current activity; Windows limits it to 127 characters.</summary>
    public void SetStatus(string text) => _icon.Text = text.Length > 127 ? text[..127] : text;

    public void OpenMenu()
    {
        BuildMenu();
        // A menu without an owner window closes on outside clicks only if the app is in the foreground.
        if (Application.Current.MainWindow is { } main)
            SetForegroundWindow(new WindowInteropHelper(main).EnsureHandle());
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
        _menu.Focus();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.SetValue(AutomationPropertiesName, Strings.TrayMenuName);

        var open = new MenuItem { Header = Strings.TrayOpen, FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => _show();
        _menu.Items.Add(open);
        _menu.Items.Add(new Separator());

        var current = _service.Settings.SpeedLimitKBps;
        var limit = new MenuItem { Header = string.Format(CultureInfo.CurrentCulture, Strings.TraySpeedLimit, LimitText(current)) };
        foreach (var value in PresetLimits)
        {
            var item = new MenuItem { Header = LimitText(value), IsCheckable = true, IsChecked = value == current };
            item.Click += (_, _) => _service.SetSpeedLimit(value);
            limit.Items.Add(item);
        }
        limit.Items.Add(new Separator());
        var custom = new MenuItem
        {
            Header = Strings.TraySpeedCustom,
            IsCheckable = true,
            IsChecked = !PresetLimits.Contains(current),
        };
        custom.Click += (_, _) =>
        {
            if (_askCustomLimit(current) is { } chosen)
                _service.SetSpeedLimit(chosen);
        };
        limit.Items.Add(custom);
        _menu.Items.Add(limit);

        var startAll = new MenuItem { Header = Strings.TrayStartQueues };
        startAll.Click += (_, _) =>
        {
            foreach (var queue in _service.Queues)
                _service.StartQueue(queue.Id);
        };
        var stopAll = new MenuItem { Header = Strings.TrayStopQueues };
        stopAll.Click += async (_, _) =>
        {
            foreach (var queue in _service.Queues)
                await _service.StopQueueAsync(queue.Id);
        };
        _menu.Items.Add(startAll);
        _menu.Items.Add(stopAll);
        _menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = Strings.TrayExit };
        exit.Click += (_, _) => _exit();
        _menu.Items.Add(exit);
    }

    public static string LimitText(long kbps) => kbps switch
    {
        <= 0 => Strings.SpeedNoLimit,
        >= 1024 when kbps % 1024 == 0 => string.Format(CultureInfo.CurrentCulture, "{0} MB/s", kbps / 1024),
        _ => string.Format(CultureInfo.CurrentCulture, "{0} KB/s", kbps),
    };

    private static readonly DependencyProperty AutomationPropertiesName = System.Windows.Automation.AutomationProperties.NameProperty;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
