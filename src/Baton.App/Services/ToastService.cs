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
            return;
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

    public void ShowCompleted(DownloadSnapshot download)
    {
        if (!_registered)
            return;
        try
        {
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(CompletionToast.Build(download, Format.Bytes(download.TotalSize ?? download.Downloaded), Links));
            ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(new ToastNotification(xml) { Tag = download.Id.ToString() });
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
                history.Remove(id.ToString(), "", AppUserModelId);
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
