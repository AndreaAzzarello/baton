namespace Baton.Core.Integration;

public enum LinkOutcome
{
    /// <summary>The app did what the button asked (or there was nothing left to do for an offer that is gone).</summary>
    Done,

    /// <summary>The app checked the link and refused it: bad signature, expired, or the download is gone.</summary>
    Expired,

    /// <summary>The app was needed but did not start or answer in time.</summary>
    AppNotReachable,
}

/// <summary>
/// Hands a notification button ("baton:...") to the running app, starting it when it is closed. A button of a duplicate stays
/// valid while the app is closed (the download waits in the list): Baton starts and then acts on it. The offers for copied links
/// live in the app's memory only, so with the app closed they are simply gone.
/// </summary>
public static class LinkLauncher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <param name="send">Sends one message to the app's pipe; null when no app listens.</param>
    /// <param name="startApp">Starts Baton in the notification area; false when it could not be started.</param>
    public static async Task<LinkOutcome> ForwardAsync(NotificationAction action, string link, Func<ChannelMessage, Task<ChannelReply?>> send,
        Func<bool> startApp, TimeSpan startTimeout, TimeSpan? pollInterval = null)
    {
        var message = new ChannelMessage(Messages.Link, Link: link);
        var reply = await send(message);
        if (reply is null)
        {
            // A copied link offered by an app that has since closed is simply gone: nothing to say.
            if (action is NotificationAction.ClipboardDownload or NotificationAction.ClipboardIgnore or NotificationAction.ClipboardMute)
                return LinkOutcome.Done;
            if (!startApp())
                return LinkOutcome.AppNotReachable;
            // The app is starting: it answers on the pipe once it is up and has opened the list.
            var deadline = DateTime.UtcNow + startTimeout;
            while (reply is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(pollInterval ?? PollInterval);
                reply = await send(message);
            }
            if (reply is null)
                return LinkOutcome.AppNotReachable;
        }
        return reply.Ok || action == NotificationAction.ClipboardIgnore ? LinkOutcome.Done : LinkOutcome.Expired;
    }
}
