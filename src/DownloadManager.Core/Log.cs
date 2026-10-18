using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DownloadManager.Core;

/// <summary>
/// A small rotating log in the app's data folder (logs\app.log, then app.1.log … app.N.log): when the file passes its
/// size limit it is renamed and the oldest one dropped. Nothing sensitive is written: addresses lose their login,
/// query and fragment, and values that look like passwords, tokens or cookies are masked whatever the caller passes.
/// Without <see cref="Configure"/> (tests, the native host) nothing is written.
/// </summary>
public static partial class Log
{
    private static readonly object Gate = new();
    private static string? _directory;
    private static long _maxBytes = 1024 * 1024;
    private static int _files = 5;

    public static string? Directory => _directory;

    public static string? CurrentFile => _directory is null ? null : Path.Combine(_directory, "app.log");

    public static void Configure(string directory, long maxBytes = 1024 * 1024, int files = 5)
    {
        lock (Gate)
        {
            _directory = directory;
            _maxBytes = Math.Max(4096, maxBytes);
            _files = Math.Clamp(files, 1, 50);
        }
    }

    /// <summary>Stops writing (tests).</summary>
    public static void Disable()
    {
        lock (Gate)
            _directory = null;
    }

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Warning(string area, string message) => Write("WARN", area, message);

    public static void Error(string area, string message, Exception? exception = null) =>
        Write("ERROR", area, exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    /// <summary>An address as it may be logged: scheme, host, port and path only.</summary>
    public static string Url(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "?";
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture))}{uri.AbsolutePath}";
    }

    /// <summary>Masks what must never reach a log, wherever it appears in a message.</summary>
    public static string Redact(string message)
    {
        var text = UrlPattern().Replace(message, m => Url(m.Value));
        text = SecretPattern().Replace(text, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}***");
        return text.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static void Write(string level, string area, string message)
    {
        lock (Gate)
        {
            if (_directory is null)
                return;
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                var file = Path.Combine(_directory, "app.log");
                if (File.Exists(file) && new FileInfo(file).Length >= _maxBytes)
                    Rotate(file);
                var line = $"{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)} {level,-5} {area}: {Redact(message)}{Environment.NewLine}";
                // Shared: during an update the previous version (the watcher) and the new one write at the same time.
                var bytes = Encoding.UTF8.GetBytes(line);
                using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written never stops the app.
            }
        }
    }

    private static void Rotate(string file)
    {
        var oldest = Numbered(_files - 1);
        if (File.Exists(oldest))
            File.Delete(oldest);
        for (var i = _files - 2; i >= 1; i--)
        {
            if (File.Exists(Numbered(i)))
                File.Move(Numbered(i), Numbered(i + 1));
        }
        if (_files > 1)
            File.Move(file, Numbered(1));
        else
            File.Delete(file);
    }

    private static string Numbered(int index) => Path.Combine(_directory!, $"app.{index.ToString(CultureInfo.InvariantCulture)}.log");

    [GeneratedRegex(@"(?:https?|ftps?|ftpes)://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?<key>password|passwd|pwd|secret|token|cookie|authorization|api[_-]?key|session[_-]?id|jwt)(?<sep>\s*[=:]\s*)[^\s&;,]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}

/// <summary>Disk errors in words: a full disk, a folder that cannot be written, a path that is too long.</summary>
public static class StorageErrors
{
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);
    private const int FilenameTooLong = unchecked((int)0x800700CE);

    public static bool IsDiskFull(Exception ex) => ex is IOException { HResult: DiskFull or HandleDiskFull };

    /// <summary>Errors that waiting and retrying will not fix: a full disk, no permission, a path too long.</summary>
    public static bool IsPermanent(Exception ex) =>
        IsDiskFull(ex) || ex is UnauthorizedAccessException or PathTooLongException || ex is IOException { HResult: FilenameTooLong };

    /// <summary>A message for the user, or the exception's own when it is not a storage problem.</summary>
    public static string Describe(Exception ex, string? path = null)
    {
        var where = string.IsNullOrEmpty(path) ? "" : " " + path;
        if (IsDiskFull(ex))
            return string.Format(CultureInfo.CurrentCulture, Resources.Strings.ErrorDiskFull, where.Trim());
        if (ex is UnauthorizedAccessException)
            return string.Format(CultureInfo.CurrentCulture, Resources.Strings.ErrorFolderNotWritable, where.Trim());
        if (ex is PathTooLongException || ex is IOException { HResult: FilenameTooLong })
            return string.Format(CultureInfo.CurrentCulture, Resources.Strings.ErrorPathTooLong, where.Trim());
        return ex.Message;
    }
}
