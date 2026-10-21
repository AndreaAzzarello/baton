using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Baton.Core;
using Baton.Core.Resources;

namespace Baton.App.Services;

/// <summary>
/// The icon in the notification area. A click (or Enter/Space when the icon has the keyboard focus) opens a menu
/// (a WPF menu, so it follows the app's theme) with the speed limit, the queues and Exit; a double click shows the
/// window. Built on the Windows Shell_NotifyIcon API directly: no Windows Forms, no extra library.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Speed limits offered in the menu, in KB/s; 0 = no limit.</summary>
    public static readonly long[] PresetLimits = [0, 256, 512, 1024, 2048, 5120, 10240];

    private const int IconId = 1;
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private readonly DownloadService _service;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly Func<long, long?> _askCustomLimit;
    private readonly ContextMenu _menu = new();
    private readonly NativeWindow _window;
    private readonly int _taskbarCreated;
    private IntPtr _iconHandle;
    private string _tip = Strings.AppName;

    public TrayIcon(DownloadService service, Action show, Action exit, Func<long, long?> askCustomLimit)
    {
        _service = service;
        _show = show;
        _exit = exit;
        _askCustomLimit = askCustomLimit;
        // A plain hidden Win32 window receives the icon's notifications. Not a WPF HwndSource: that would start the
        // graphics stack (Direct3D and the GPU driver, tens of MB) for an app that may never show a window.
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window = new NativeWindow(WndProc);
        _iconHandle = LoadAppIcon();
        AddIcon();
    }

    /// <summary>A short balloon, for example the first time the window hides in the notification area.</summary>
    public void ShowHint(string text)
    {
        var data = NewData(NifInfo);
        data.szInfoTitle = Strings.AppName;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NiifInfo;
        Shell_NotifyIcon(NimModify, ref data);
    }

    /// <summary>Tooltip with the current activity; Windows limits it to 127 characters.</summary>
    public void SetStatus(string text)
    {
        _tip = text.Length > 127 ? text[..127] : text;
        var data = NewData(NifTip | NifShowTip);
        Shell_NotifyIcon(NimModify, ref data);
    }

    private void AddIcon()
    {
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        Shell_NotifyIcon(NimAdd, ref data);
        data.uTimeoutOrVersion = NotifyIconVersion4; // richer notifications, including keyboard selection
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private NotifyIconData NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _iconHandle,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    /// <returns>True when the message was handled.</returns>
    private bool WndProc(int msg, IntPtr lParam)
    {
        if (msg == _taskbarCreated)
        {
            AddIcon(); // Explorer restarted: the icon must be added again
            return true;
        }
        if (msg != CallbackMessage)
            return false;
        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case NinSelect:
            case NinKeySelect:
            case WmContextMenu:
                OpenMenu();
                return true;
            case WmLButtonDblClk:
                _menu.IsOpen = false;
                _show();
                return true;
        }
        return false;
    }

    /// <summary>A hidden top-level window (top-level, so it also receives the TaskbarCreated broadcast).</summary>
    private sealed class NativeWindow : IDisposable
    {
        private static readonly string ClassName = "Baton.Tray." + Environment.ProcessId;
        private readonly WndProcDelegate _proc; // kept alive while Windows may call it
        private readonly Func<int, IntPtr, bool> _handler;

        public NativeWindow(Func<int, IntPtr, bool> handler)
        {
            _handler = handler;
            _proc = Proc;
            var windowClass = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref windowClass);
            Handle = CreateWindowEx(0, ClassName, "Baton", WsPopup, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
        }

        public IntPtr Handle { get; }

        private IntPtr Proc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) =>
            _handler(msg, lParam) ? IntPtr.Zero : DefWindowProc(hwnd, msg, wParam, lParam);

        public void Dispose()
        {
            DestroyWindow(Handle);
            UnregisterClass(ClassName, GetModuleHandle(null));
        }

        private const int WsPopup = unchecked((int)0x80000000);

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>The application icon embedded in the executable, at the size the notification area uses on this screen.</summary>
    private static IntPtr LoadAppIcon()
    {
        var size = GetSystemMetrics(SmCxSmIcon);
        try
        {
            if (LoadIconWithScaleDown(GetModuleHandle(null), new IntPtr(32512), size, size, out var icon) == 0 && icon != IntPtr.Zero)
                return icon;
        }
        catch (EntryPointNotFoundException)
        {
            // Only the version 6 common controls have it; the extracted small icon is the fallback.
        }
        return ExtractIconEx(Environment.ProcessPath ?? "", 0, IntPtr.Zero, out var small, 1) > 0 ? small : IntPtr.Zero;
    }

    public void OpenMenu()
    {
        BuildMenu();
        // A menu without an owner window closes on outside clicks only if the app is in the foreground. The tray's own
        // window is used, so the main window is not created just to open the menu.
        SetForegroundWindow(_window.Handle);
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
        var data = NewData(0);
        Shell_NotifyIcon(NimDelete, ref data);
        if (_iconHandle != IntPtr.Zero)
            DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;
        _window.Dispose();
    }

    private const int NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const int NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const int NiifInfo = 0x1, NotifyIconVersion4 = 4;
    private const int NinSelect = 0x400, NinKeySelect = 0x401, WmContextMenu = 0x7B, WmLButtonDblClk = 0x203;
    private const int SmCxSmIcon = 49;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("comctl32.dll")]
    private static extern int LoadIconWithScaleDown(IntPtr instance, IntPtr name, int width, int height, out IntPtr icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? module);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
