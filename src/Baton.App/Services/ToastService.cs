using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Baton.Core;
using Windows.UI.Notifications;

namespace Baton.App.Services;

/// <summary>
/// Windows notifications for completed downloads. The app is not packaged, so it registers its own identity
/// (name and icon shown on the notification) and the "baton:" link scheme that the buttons use.
/// </summary>
public sealed class ToastService(string dataDirectory)
{
    public const string AppUserModelId = "Baton.App";

    // A notification without a group cannot be taken back one by one outside a packaged app (History.Remove rejects "").
    private const string Group = "downloads";

    // Trial only: BATON_TOAST_TRIAL=<file> shows real notifications from an isolated run under the identity the installed app
    // already registered (nothing is written to the registry) and appends each notification's XML to that file. It does nothing
    // unless BATON_DATA is set too, so it can never change what a normal or portable copy does.
    private static readonly string? TrialLog = TrialLogPath(Environment.GetEnvironmentVariable("BATON_DATA"), Environment.GetEnvironmentVariable("BATON_TOAST_TRIAL"));

    internal static string? TrialLogPath(string? batonData, string? trialLog) => batonData is { Length: > 0 } && trialLog is { Length: > 0 } ? trialLog : null;

    private NotificationLinks? _links;
    private bool _registered;

    public NotificationLinks Links => _links ??= NotificationLinks.FromKeyFile(KeyPath(dataDirectory));

    public static string KeyPath(string dataDirectory) => Path.Combine(dataDirectory, "notification.key");

    /// <summary>Repeated on every start so a moved executable keeps working.</summary>
    public void Register()
    {
        // Windows shows notifications only for an identity registered in the real registry: none in a sandboxed run, nor in
        // the portable copy, which writes nothing there.
        if (AppEnvironment.IsSandboxed || AppEnvironment.IsPortable)
        {
            _registered = TrialLog is not null;
            return;
        }
        try
        {
            var icon = Path.Combine(dataDirectory, "notification.png");
            SaveIcon(icon);

            AppEnvironment.Registration.RegisterNotifications(icon);
            _registered = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Without registration the app simply shows no notifications.
        }
    }

    /// <summary>The copied links waiting for a click on their notification (kept in memory only).</summary>
    public Baton.Core.Integration.ClipboardOffers Offers { get; } = new();

    /// <summary>A duplicate found by the server's answer, as a notification with buttons. False when it could not be shown (the dialog is the fallback).</summary>
    public bool ShowDuplicate(Baton.Core.Duplicates.DuplicateNotice notice) =>
        Show(ActionToasts.BuildDuplicate(notice, Links), "duplicate-" + notice.DownloadId.ToString(System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromDays(1));

    /// <summary>"Download with Baton?" for a copied link. The address stays in <see cref="Offers"/>; the buttons carry a short-lived reference.</summary>
    public bool ShowClipboardOffer(string url, string fileName, string host) =>
        Show(ActionToasts.BuildClipboard(fileName, host, Offers.Add(url, DateTime.UtcNow), Links), "clipboard", ActionToasts.ClipboardLifetime);

    private bool Show(string xmlText, string tag, TimeSpan lifetime)
    {
        if (!_registered)
            return false;
        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            // Notifications turned off in Windows (or by policy): the caller falls back to a dialog or a balloon.
            if (notifier.Setting != NotificationSetting.Enabled)
                return false;
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(xmlText);
            if (TrialLog is not null)
                File.AppendAllText(TrialLog, xmlText + Environment.NewLine);
            notifier.Show(new ToastNotification(xml) { Tag = tag, Group = Group, ExpirationTime = DateTimeOffset.Now + lifetime });
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void ShowCompleted(DownloadSnapshot download)
    {
        if (!_registered)
            return;
        try
        {
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(CompletionToast.Build(download, Format.Bytes(download.TotalSize ?? download.Downloaded), Links));
            ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(new ToastNotification(xml) { Tag = download.Id.ToString(), Group = Group });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            // Notifications turned off in Windows, or Focus Assist: nothing to do.
        }
    }

    /// <summary>Takes the notifications of downloads no longer in the list out of the notification centre.</summary>
    public void Remove(IEnumerable<long> downloadIds)
    {
        if (!_registered)
            return;
        try
        {
            var history = ToastNotificationManager.History;
            foreach (var id in downloadIds)
                history.Remove(id.ToString(), Group, AppUserModelId);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or UnauthorizedAccessException)
        {
            // Notifications turned off in Windows: there is nothing to take back.
        }
    }

    private static void SaveIcon(string path)
    {
        var decoder = BitmapDecoder.Create(Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream,
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var largest = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(largest));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
