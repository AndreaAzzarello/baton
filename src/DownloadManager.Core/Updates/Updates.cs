using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownloadManager.Core.Updates;

/// <summary>A newer version found at the update address.</summary>
public sealed record UpdateInfo(Version Version, string Notes, Uri Download, string Sha256, string Name);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the update address: a GitHub "latest release" (api.github.com/repos/OWNER/REPO/releases/latest), whose
/// executable asset comes with a ".sha256" asset or a "SHA-256: …" line in the notes, or a small JSON manifest
/// { "version": "1.1.0", "notes": "…", "url": "https://…/DownloadManager.exe", "sha256": "…" }. Without a SHA-256 an
/// update is never offered. Only https addresses are used (http only for this computer, for tests).
/// </summary>
public static partial class UpdateChecker
{
    private const int MaxManifestBytes = 1024 * 1024;

    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, string address, Version current, CancellationToken ct)
    {
        var url = Allowed(address) ?? throw new UpdateException(Resources.Strings.UpdateErrorAddress);
        var text = await GetTextAsync(http, url, MaxManifestBytes, ct);
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException ex)
        {
            throw new UpdateException(Resources.Strings.UpdateErrorNoRelease, ex);
        }
        var info = root.TryGetProperty("tag_name", out _) ? await FromGitHubAsync(http, root, ct) : FromManifest(root, url);
        return info.Version > Normalize(current) ? info : null;
    }

    private static UpdateInfo FromManifest(JsonElement root, Uri baseUrl)
    {
        var version = ParseVersion(Text(root, "version")) ?? throw new UpdateException(Resources.Strings.UpdateErrorNoRelease);
        var download = Text(root, "url") is { } link && Uri.TryCreate(baseUrl, link, out var u) ? Allowed(u.AbsoluteUri) : null;
        var sha = Text(root, "sha256")?.Trim().ToLowerInvariant();
        if (download is null || sha is null || !IsSha256(sha))
            throw new UpdateException(Resources.Strings.UpdateErrorNoHash);
        return new UpdateInfo(version, Text(root, "notes") ?? "", download, sha, Path.GetFileName(download.AbsolutePath));
    }

    private static async Task<UpdateInfo> FromGitHubAsync(HttpClient http, JsonElement root, CancellationToken ct)
    {
        var version = ParseVersion(Text(root, "tag_name")) ?? throw new UpdateException(Resources.Strings.UpdateErrorNoRelease);
        var notes = Text(root, "body") ?? "";
        var assets = root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(a => (Name: Text(a, "name") ?? "", Url: Text(a, "browser_download_url"))).ToList()
            : [];
        var exe = assets.FirstOrDefault(a => a.Name.Equals("DownloadManager.exe", StringComparison.OrdinalIgnoreCase));
        if (exe.Url is null)
            exe = assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (exe.Url is null || Allowed(exe.Url) is not { } download)
            throw new UpdateException(Resources.Strings.UpdateErrorNoExe);

        string? sha = null;
        var shaAsset = assets.FirstOrDefault(a => a.Name.Equals(exe.Name + ".sha256", StringComparison.OrdinalIgnoreCase)
                                                  || a.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (shaAsset.Url is not null && Allowed(shaAsset.Url) is { } shaUrl)
        {
            var sums = await GetTextAsync(http, shaUrl, 64 * 1024, ct);
            // "hash  name" lines, or just the hash.
            sha = sums.Split('\n').Select(l => l.Trim()).Where(l => l.Length >= 64)
                .Where(l => l.Length == 64 || l.EndsWith(exe.Name, StringComparison.OrdinalIgnoreCase))
                .Select(l => l[..64].ToLowerInvariant()).FirstOrDefault(IsSha256);
        }
        sha ??= ShaInNotes().Match(notes) is { Success: true } m ? m.Groups["h"].Value.ToLowerInvariant() : null;
        if (sha is null)
            throw new UpdateException(Resources.Strings.UpdateErrorNoHash);
        return new UpdateInfo(version, notes, download, sha, exe.Name);
    }

    /// <summary>The address if it may be used: https, or http to this computer.</summary>
    public static Uri? Allowed(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
            return null;
        if (uri.Scheme == "https")
            return uri;
        return uri.Scheme == "http" && uri.IsLoopback ? uri : null;
    }

    public static Version? ParseVersion(string? text)
    {
        var match = VersionText().Match(text ?? "");
        return match.Success && Version.TryParse(match.Value, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static bool IsSha256(string? hex) => hex is { Length: 64 } && hex.All(Uri.IsHexDigit);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<string> GetTextAsync(HttpClient http, Uri url, int limit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json, application/json;q=0.9, */*;q=0.5");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Resources.Strings.UpdateErrorStatus, (int)response.StatusCode));
        if (response.Content.Headers.ContentLength > limit)
            throw new UpdateException(Resources.Strings.UpdateErrorTooLarge);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > limit)
            throw new UpdateException(Resources.Strings.UpdateErrorTooLarge);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex VersionText();

    [GeneratedRegex(@"sha-?256\s*[:=]?\s*`?(?<h>[0-9a-fA-F]{64})", RegexOptions.IgnoreCase)]
    private static partial Regex ShaInNotes();
}

/// <summary>
/// Puts an update in place safely. The new executable is downloaded next to the data, verified (SHA-256, and that it
/// is a Windows program), then the running executable is renamed to "DownloadManager.exe.old" (Windows allows renaming
/// a running program) and the new one takes its name. The old version, started as a watcher, then starts the new one
/// and waits for it to report that it started; if it closes or does not report within the time allowed, the old
/// version is put back and started again. "Restore previous version" does the same swap by hand.
/// </summary>
public static class UpdateInstaller
{
    public const long MaxUpdateBytes = 600L * 1024 * 1024;
    public const string WatchArgument = "--update-watch";
    public const string UpdatedArgument = "--updated";

    public static string BackupOf(string exe) => exe + ".old";

    /// <summary>Downloads the update to <paramref name="folder"/> and checks it; the path of the verified file.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo update, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "DownloadManager.new.exe");
        var temp = target + ".part";
        using (var response = await http.GetAsync(update.Download, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode)
                throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Resources.Strings.UpdateErrorStatus, (int)response.StatusCode));
            var total = response.Content.Headers.ContentLength;
            if (total > MaxUpdateBytes)
                throw new UpdateException(Resources.Strings.UpdateErrorTooLarge);
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > MaxUpdateBytes)
                    throw new UpdateException(Resources.Strings.UpdateErrorTooLarge);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                if (total is > 0)
                    progress?.Report((double)written / total.Value);
            }
        }
        try
        {
            await VerifyAsync(temp, update.Sha256, ct);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        File.Move(temp, target, overwrite: true);
        return target;
    }

    public static async Task VerifyAsync(string file, string sha256, CancellationToken ct)
    {
        string actual;
        await using (var stream = File.OpenRead(file))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(Resources.Strings.UpdateErrorHashMismatch);
        await using var check = File.OpenRead(file);
        if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
            throw new UpdateException(Resources.Strings.UpdateErrorNotProgram);
    }

    /// <summary>Renames the running executable to ".old" and puts the verified update in its place.</summary>
    public static void Swap(string verified, string currentExe)
    {
        var backup = BackupOf(currentExe);
        if (File.Exists(backup))
            File.Delete(backup);
        File.Move(currentExe, backup);
        try
        {
            File.Copy(verified, currentExe);
        }
        catch
        {
            File.Move(backup, currentExe); // nothing changed after all
            throw;
        }
        File.Delete(verified);
    }

    /// <summary>The previous version is put back; the one that failed is kept as ".failed" for inspection.</summary>
    public static bool Restore(string currentExe)
    {
        var backup = BackupOf(currentExe);
        if (!File.Exists(backup))
            return false;
        var failed = currentExe + ".failed";
        if (File.Exists(failed))
            File.Delete(failed);
        if (File.Exists(currentExe))
            File.Move(currentExe, failed);
        File.Move(backup, currentExe);
        return true;
    }

    /// <summary>
    /// The watcher's work (run by the previous version): wait for the app that started it to close, start the new
    /// version, and wait for its "started" mark. Restores the previous version when the new one closes or stays silent.
    /// Returns true when the update is in place.
    /// </summary>
    public static async Task<bool> WatchAsync(string currentExe, string marker, Func<Task> waitForOldApp, Func<string, IProcessHandle> start,
        TimeSpan timeout, Action<string> restart, CancellationToken ct = default)
    {
        await waitForOldApp();
        if (File.Exists(marker))
            File.Delete(marker);
        var process = start(currentExe);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(marker))
                return true;
            if (process.HasExited)
                break;
            await Task.Delay(200, ct);
        }
        if (File.Exists(marker))
            return true;
        process.Kill();
        Restore(currentExe);
        restart(currentExe);
        return false;
    }
}

/// <summary>A started process as the watcher sees it (tests use a fake one).</summary>
public interface IProcessHandle
{
    bool HasExited { get; }
    void Kill();
}

public sealed class ProcessHandle(System.Diagnostics.Process process) : IProcessHandle
{
    public bool HasExited => process.HasExited;

    public void Kill()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
