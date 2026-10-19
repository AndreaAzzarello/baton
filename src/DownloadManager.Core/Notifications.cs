using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DownloadManager.Core.Resources;

namespace DownloadManager.Core;

public enum NotificationAction
{
    /// <summary>Opens the downloaded file with its default program.</summary>
    Open,

    /// <summary>Shows the file selected in Explorer.</summary>
    Reveal,

    /// <summary>Brings the app's window forward.</summary>
    Show,
}

/// <summary>
/// Links behind the buttons of a completion notification, such as "downloadmanager:open?p=...&amp;s=...".
/// Windows starts the app with the link when a button is clicked, even after the app has exited.
/// A web page can invoke the same scheme, so every link is signed with a per-user secret: a forged link,
/// for example one that would open an arbitrary program, is rejected. The signature also covers the download the link
/// belongs to and an expiry, so an old notification cannot open whatever file later takes that path.
/// </summary>
public sealed class NotificationLinks(byte[] key)
{
    public const string Scheme = "downloadmanager";

    /// <summary>How long a notification's buttons work.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Loads the secret from <paramref name="path"/>, creating it the first time.</summary>
    public static NotificationLinks FromKeyFile(string path)
    {
        try
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32)
                return new NotificationLinks(existing);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, key);
        return new NotificationLinks(key);
    }

    public static bool IsLink(string? argument) =>
        argument is not null && argument.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public string Create(NotificationAction action, string path, long downloadId, DateTime expiresUtc)
    {
        var verb = action.ToString().ToLowerInvariant();
        var encoded = Base64Url(Encoding.UTF8.GetBytes(path));
        var id = downloadId.ToString(CultureInfo.InvariantCulture);
        var expires = new DateTimeOffset(expiresUtc, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{Scheme}:{verb}?p={encoded}&d={id}&e={expires}&s={Sign(verb, encoded, id, expires)}";
    }

    /// <summary>
    /// Reads a link created by <see cref="Create"/>; false for anything else, a wrong signature or an expired link.
    /// The caller still checks that <paramref name="downloadId"/> is the finished download at <paramref name="path"/>.
    /// </summary>
    public bool TryParse(string link, DateTime nowUtc, out NotificationAction action, out string path, out long downloadId)
    {
        action = default;
        path = "";
        downloadId = 0;
        if (!IsLink(link))
            return false;
        var rest = link[(Scheme.Length + 1)..];
        var question = rest.IndexOf('?');
        if (question <= 0)
            return false;
        var verb = rest[..question].ToLowerInvariant();
        if (!Enum.TryParse(verb, ignoreCase: true, out action) || !Enum.IsDefined(action) || verb != action.ToString().ToLowerInvariant())
            return false;

        string? encoded = null, signature = null, id = null, expires = null;
        foreach (var pair in rest[(question + 1)..].Split('&'))
        {
            if (pair.StartsWith("p=", StringComparison.Ordinal))
                encoded = pair[2..];
            else if (pair.StartsWith("d=", StringComparison.Ordinal))
                id = pair[2..];
            else if (pair.StartsWith("e=", StringComparison.Ordinal))
                expires = pair[2..];
            else if (pair.StartsWith("s=", StringComparison.Ordinal))
                signature = pair[2..];
        }
        if (encoded is null || signature is null || id is null || expires is null)
            return false;
        var expected = Encoding.ASCII.GetBytes(Sign(verb, encoded, id, expires));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature)))
            return false;
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out downloadId)
            || !long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, 253402300799)).UtcDateTime <= nowUtc)
            return false;
        try
        {
            path = Encoding.UTF8.GetString(FromBase64Url(encoded));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string Sign(string verb, string encodedPath, string downloadId, string expires) =>
        Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(verb + "\n" + encodedPath + "\n" + downloadId + "\n" + expires)));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>The XML of a Windows toast notification for a completed download.</summary>
public static class CompletionToast
{
    public static string Build(DownloadSnapshot download, string sizeText, NotificationLinks links, DateTime? utcNow = null)
    {
        var path = download.FinalPath ?? Path.Combine(download.Directory, download.DisplayName);
        var expires = (utcNow ?? DateTime.UtcNow) + NotificationLinks.Lifetime;
        var problem = download.Check?.HasProblem == true;
        var checks = PostDownload.PostCheck.Describe(download.Check);
        var actions = new XElement("actions");
        // A file the antivirus or the hash flagged is not offered to be opened from the notification.
        if (!problem)
        {
            actions.Add(new XElement("action",
                new XAttribute("content", Strings.ToastOpen),
                new XAttribute("activationType", "protocol"),
                new XAttribute("arguments", links.Create(NotificationAction.Open, path, download.Id, expires))));
        }
        actions.Add(new XElement("action",
            new XAttribute("content", Strings.ToastOpenFolder),
            new XAttribute("activationType", "protocol"),
            new XAttribute("arguments", links.Create(NotificationAction.Reveal, path, download.Id, expires))));
        var toast = new XElement("toast",
            new XAttribute("activationType", "protocol"),
            new XAttribute("launch", links.Create(NotificationAction.Show, path, download.Id, expires)),
            new XElement("visual",
                new XElement("binding", new XAttribute("template", "ToastGeneric"),
                    new XElement("text", problem ? Strings.ToastCompletedProblemTitle : Strings.ToastCompletedTitle),
                    new XElement("text", download.DisplayName),
                    // Only the folder's name: the full path wraps badly, and "Open folder" shows it anyway.
                    new XElement("text", string.Format(CultureInfo.CurrentCulture, Strings.ToastCompletedDetails, sizeText,
                        Path.GetFileName(Path.TrimEndingDirectorySeparator(download.Directory)) is { Length: > 0 } folder ? folder : download.Directory)
                        + (checks is null ? "" : "\n" + checks)))),
            actions);
        return toast.ToString(SaveOptions.DisableFormatting);
    }
}
