using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Baton.Core;
using Baton.Core.Duplicates;
using Baton.Core.Integration;
using Baton.Core.Resources;

namespace Baton.App.Services;

/// <summary>
/// Offers to download a link the user copies (Settings, off by default): a notification that opens the add dialog already
/// filled, never a dialog by itself. What is copied is read only to be judged by <see cref="ClipboardLinks"/> and then
/// forgotten: it is not stored, not logged and not kept in a field. Skipped: copies made by Baton itself and content that
/// the source marked as private (the Windows formats that password managers set, which also keep it out of the clipboard history).
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly DownloadService _service;
    private readonly ToastService _toasts;
    private readonly Action<string> _offer;
    private readonly ClipboardLinks _links = new();
    // WM_CLIPBOARDUPDATE comes several times for one copy: judged once, after a quiet moment.
    private readonly DispatcherTimer _quiet = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public ClipboardWatcher(TrayIcon tray, DownloadService service, ToastService toasts, Action<string> offer)
    {
        _tray = tray;
        _service = service;
        _toasts = toasts;
        _offer = offer;
        _quiet.Tick += (_, _) =>
        {
            _quiet.Stop();
            Judge();
        };
        _tray.ClipboardChanged += OnChanged;
    }

    /// <summary>Turns the listening on or off with the setting.</summary>
    public void Apply(bool enabled)
    {
        _tray.ListenToClipboard(enabled);
        if (!enabled)
            _quiet.Stop();
    }

    private void OnChanged()
    {
        _quiet.Stop();
        _quiet.Start();
    }

    private void Judge()
    {
        if (!_service.Settings.MonitorClipboard)
            return;
        var link = _links.Accept(ReadText(), _service.Categories, DateTime.UtcNow);
        // Already in the list: nothing to offer.
        if (link is null || _service.FindDuplicate(link.AbsoluteUri) is { Reason: DuplicateReason.SameUrl })
            return;
        if (ClipboardLinks.IsIgnored(link.Host, _service.Settings.ClipboardIgnoredHosts))
            return;
        var url = link.AbsoluteUri;
        // A notification with buttons; the tray balloon (click to add) when notifications are not available.
        if (_toasts.ShowClipboardOffer(url, FileNames.FromUrl(link), link.Host))
            return;
        _tray.ShowHint(string.Format(CultureInfo.CurrentCulture, Strings.ClipboardOffer, FileNames.FromUrl(link), link.Host), () => _offer(url));
    }

    public void Dispose()
    {
        _quiet.Stop();
        _tray.ClipboardChanged -= OnChanged;
    }

    // ------------------------------------------------------------------ the clipboard, read once and closed

    private const uint CfUnicodeText = 13;

    /// <summary>The copied text; null when it is not text, is too long, is private, comes from Baton or the clipboard is busy.</summary>
    private static string? ReadText()
    {
        var opened = false;
        for (var attempt = 0; attempt < 5 && !(opened = OpenClipboard(IntPtr.Zero)); attempt++)
            Thread.Sleep(20);
        if (!opened)
            return null;
        try
        {
            if (OwnedByThisProcess() || IsMarkedPrivate() || !IsClipboardFormatAvailable(CfUnicodeText))
                return null;
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero || GlobalSize(handle) > (nuint)((ClipboardLinks.MaxLength + 1) * 2))
                return null;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool OwnedByThisProcess()
    {
        var owner = GetClipboardOwner();
        return owner != IntPtr.Zero && GetWindowThreadProcessId(owner, out var pid) != 0 && pid == Environment.ProcessId;
    }

    /// <summary>
    /// The formats that Windows and password managers use: "exclude from monitoring", and 0 for "can include in the history" /
    /// "can upload to the cloud clipboard". A missing format means nothing was said.
    /// </summary>
    private static bool IsMarkedPrivate()
    {
        if (IsClipboardFormatAvailable(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing")))
            return true;
        return ReadDword("CanIncludeInClipboardHistory") == 0 || ReadDword("CanUploadToCloudClipboard") == 0;
    }

    private static int? ReadDword(string format)
    {
        var id = RegisterClipboardFormat(format);
        if (!IsClipboardFormatAvailable(id))
            return null;
        var handle = GetClipboardData(id);
        if (handle == IntPtr.Zero || GlobalSize(handle) < sizeof(int))
            return null;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.ReadInt32(pointer);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr handle);
}
