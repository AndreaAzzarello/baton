using System.Globalization;
using System.Xml.Linq;
using Baton.Core.Duplicates;
using Baton.Core.Resources;

namespace Baton.Core;

/// <summary>
/// Notifications with buttons that act on the running app: a duplicate found by the server's answer, and a link copied to the
/// clipboard. Every button is a signed "baton:" link (<see cref="NotificationLinks"/>); there is never an "Overwrite" button.
/// </summary>
public static class ActionToasts
{
    /// <summary>How long the buttons of a clipboard offer work (the reference lives in memory only that long).</summary>
    public static readonly TimeSpan ClipboardLifetime = TimeSpan.FromMinutes(10);

    /// <summary>"Download anyway", "Open existing file" (only if it is still there) and "Remove" for a download stopped on a duplicate.</summary>
    public static string BuildDuplicate(DuplicateNotice notice, NotificationLinks links, DateTime? utcNow = null)
    {
        var expires = (utcNow ?? DateTime.UtcNow) + NotificationLinks.Lifetime;
        var existing = notice.Match.ExistingPath;
        string Link(NotificationAction action) => links.Create(action, existing, notice.DownloadId, expires);
        var actions = new XElement("actions", Button(Strings.ToastDownloadAnyway, Link(NotificationAction.DuplicateDownload)));
        if (File.Exists(existing))
            actions.Add(Button(Strings.ToastOpenExisting, Link(NotificationAction.DuplicateOpen)));
        actions.Add(Button(Strings.ToastRemove, Link(NotificationAction.DuplicateRemove)));
        return Toast(Link(NotificationAction.Show), actions, Strings.ToastDuplicateTitle, notice.FileName,
            string.Format(CultureInfo.CurrentCulture, Strings.ToastDuplicateFound, Path.GetFileName(existing)));
    }

    /// <summary>
    /// "Download", "Ignore" and "Not for this site" for a copied link. The links carry only <paramref name="reference"/> (a random
    /// id of <see cref="ClipboardOffers"/>), never the address: it must not end up in the registry or the notification history.
    /// </summary>
    public static string BuildClipboard(string fileName, string host, string reference, NotificationLinks links, DateTime? utcNow = null)
    {
        var expires = (utcNow ?? DateTime.UtcNow) + ClipboardLifetime;
        string Link(NotificationAction action) => links.Create(action, reference, 0, expires);
        var actions = new XElement("actions",
            Button(Strings.ToastDownload, Link(NotificationAction.ClipboardDownload)),
            Button(Strings.ToastIgnore, Link(NotificationAction.ClipboardIgnore)),
            Button(Strings.ToastMuteSite, Link(NotificationAction.ClipboardMute)));
        // A click on the notification itself is the same as "Ignore": it must not open anything by surprise.
        return Toast(Link(NotificationAction.ClipboardIgnore), actions, Strings.ToastClipboardTitle, fileName,
            string.Format(CultureInfo.CurrentCulture, Strings.ToastClipboardFrom, host));
    }

    private static XElement Button(string content, string link) => new("action",
        new XAttribute("content", content), new XAttribute("activationType", "protocol"), new XAttribute("arguments", link));

    private static string Toast(string launch, XElement actions, params string[] lines) =>
        new XElement("toast",
            new XAttribute("activationType", "protocol"),
            new XAttribute("launch", launch),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"), lines.Select(l => new XElement("text", l)))),
            actions).ToString(SaveOptions.DisableFormatting);
}
