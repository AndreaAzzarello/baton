namespace Baton.Core;

/// <summary>
/// Marks a completed file as coming from the Internet, as browsers do, so Windows (SmartScreen, Office
/// protected view) treats it with the same care when it is opened, including from a notification.
/// </summary>
public static class ZoneIdentifier
{
    private const int InternetZone = 3;

    public static void TryMark(string path, string url, string? referrer)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var lines = new List<string> { "[ZoneTransfer]", $"ZoneId={InternetZone}" };
            if (IsWebUrl(referrer))
                lines.Add($"ReferrerUrl={referrer}");
            if (IsWebUrl(url))
                lines.Add($"HostUrl={url}");
            File.WriteAllLines(path + ":Zone.Identifier", lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Drives without alternate data streams (FAT32, some network shares) cannot carry the mark.
        }
    }

    private static bool IsWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" or "ftps" or "ftpes"
        && uri.UserInfo.Length == 0 && !value!.Contains('\n');
}
