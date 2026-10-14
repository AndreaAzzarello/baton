using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DownloadManager.Core;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace DownloadManager.App.Services;

/// <summary>
/// Windows notifications for completed downloads. The app is not packaged, so it registers its own identity
/// (name and icon shown on the notification) and the "downloadmanager:" link scheme that the buttons use.
/// </summary>
public sealed class ToastService(string dataDirectory)
{
    public const string AppUserModelId = "DownloadManager.App";

    private NotificationLinks? _links;
    private bool _registered;

    public NotificationLinks Links => _links ??= NotificationLinks.FromKeyFile(KeyPath(dataDirectory));

    public static string KeyPath(string dataDirectory) => Path.Combine(dataDirectory, "notification.key");

    /// <summary>Repeated on every start so a moved executable keeps working.</summary>
    public void Register()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        try
        {
            var icon = Path.Combine(dataDirectory, "notification.png");
            SaveIcon(icon);

            using (var identity = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}"))
            {
                identity.SetValue("DisplayName", "DownloadManager");
                identity.SetValue("IconUri", icon);
            }

            using (var scheme = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{NotificationLinks.Scheme}"))
            {
                scheme.SetValue("", "URL:DownloadManager");
                scheme.SetValue("URL Protocol", "");
                using var command = scheme.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{exe}\" \"%1\"");
            }
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
