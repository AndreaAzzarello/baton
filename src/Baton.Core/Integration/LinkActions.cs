namespace Baton.Core.Integration;

/// <summary>
/// What the buttons of the action notifications do inside the running app. The link is checked here (signature and expiry), not
/// by the process Windows started for it: whoever forwards a link, a forged or old one does nothing.
/// </summary>
/// <param name="offerClipboard">Opens the add dialog for a copied link.</param>
/// <param name="muteHost">Stops offering links from this site.</param>
public sealed class LinkActions(DownloadService service, NotificationLinks links, ClipboardOffers offers, Action<string> openFile,
    Action<string> offerClipboard, Action<string> muteHost)
{
    public async Task<ChannelReply> HandleAsync(string link)
    {
        if (!links.TryParse(link, service.UtcNow, out var action, out var path, out var downloadId) || !action.NeedsApp())
            return Fail("The notification button is not valid or has expired.");
        switch (action)
        {
            case NotificationAction.ClipboardDownload:
                if (!offers.TryTake(path, service.UtcNow, out var url))
                    return Fail("The offer has expired.");
                offerClipboard(url);
                return new ChannelReply(true);
            case NotificationAction.ClipboardIgnore:
                offers.TryTake(path, service.UtcNow, out _);
                return new ChannelReply(true);
            case NotificationAction.ClipboardMute:
                if (!offers.TryTake(path, service.UtcNow, out var muted))
                    return Fail("The offer has expired.");
                if (Uri.TryCreate(muted, UriKind.Absolute, out var uri))
                    muteHost(uri.Host);
                return new ChannelReply(true);
        }

        // A valid button works while the download is in the list, also after the app was closed and restarted. Only a download
        // still waiting for the answer is acted on: one the user already settled (started, finished) is left as it is.
        if (service.GetSnapshot(downloadId) is not { } snapshot)
            return Fail("The download is no longer in the list.");
        if (snapshot.Status != DownloadStatus.Paused)
        {
            if (action == NotificationAction.DuplicateOpen && File.Exists(path))
                openFile(path);
            return new ChannelReply(true);
        }
        switch (action)
        {
            case NotificationAction.DuplicateDownload:
                service.ContinueDespiteDuplicate(downloadId);
                break;
            case NotificationAction.DuplicateOpen:
                if (File.Exists(path))
                    openFile(path);
                await service.RemoveAsync(downloadId, deleteFile: false);
                break;
            default:
                await service.RemoveAsync(downloadId, deleteFile: false);
                break;
        }
        return new ChannelReply(true);
    }

    private static ChannelReply Fail(string error) => new(false, Error: error);
}
