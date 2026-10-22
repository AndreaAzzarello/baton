using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Baton.Core.Resources;
using Baton.Core.Setup;

namespace Baton.Core.Updates;

/// <summary>
/// The browser extension's package in a release (from 1.3.3), as the release lists it: the address of
/// "baton-extension-&lt;version&gt;.zip" with the SHA-256 and the signatures read from its ".sha256" and ".sig" assets
/// (or the manifest's "extension" object). Hash and signatures may be missing: <see cref="ExtensionPackage.DownloadAsync"/> refuses it then.
/// </summary>
public sealed record ExtensionAsset(string Name, Uri? Download, string? Sha256, string? Signatures);

/// <summary>
/// Keeps the extension next to Baton.exe (the "extension" folder for Chromium and baton-firefox.xpi, see
/// <see cref="BundledExtension"/>) at the program's version. The package is signed like the program ("version + file
/// name + SHA-256", <see cref="UpdateSignature"/>), downloaded, verified, extracted next to the program into
/// "extension.new" (same drive, so the swap is a rename), checked to hold the expected version, and then swapped in:
/// the previous folder and package stay as "extension.old" and "baton-firefox.xpi.old" for "restore previous version".
/// </summary>
public static class ExtensionPackage
{
    public const string Prefix = "baton-extension-";
    public const string StagingName = BundledExtension.FolderName + ".new";
    public const string BackupName = BundledExtension.FolderName + ".old";

    public const long MaxPackageBytes = 64L * 1024 * 1024;
    // Limits of what the zip may unpack to, counted while writing (the sizes in its directory can lie): a zip bomb stops here.
    public const long MaxUnpackedBytes = 128L * 1024 * 1024;
    public const int MaxEntries = 2000;

    public static string NameFor(Version version) => $"{Prefix}{version.ToString(3)}.zip";

    private static string XpiBackupName => BundledExtension.FirefoxPackageName + ".old";

    /// <summary>The version in extension\manifest.json under <paramref name="folder"/> (a program folder or an unpacked package), null when unreadable.</summary>
    public static Version? VersionIn(string folder) => ManifestVersion(Path.Combine(folder, BundledExtension.FolderName, "manifest.json"));

    /// <summary>The version of the ".old" extension, null when there is none.</summary>
    public static Version? OldVersion(string programFolder) => ManifestVersion(Path.Combine(programFolder, BackupName, "manifest.json"));

    private static Version? ManifestVersion(string manifest)
    {
        try
        {
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1024 * 1024)
                return null;
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("version", out var v)
                && v.ValueKind == JsonValueKind.String && Version.TryParse(v.GetString(), out var version)
                ? new Version(version.Major, version.Minor, Math.Max(0, version.Build))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads, verifies and stages the package of <paramref name="version"/> next to the program (see <see cref="Stage"/>);
    /// the downloaded file is deleted afterwards. A release without a package is refused like a bad one.
    /// </summary>
    public static async Task PrepareAsync(HttpClient http, ExtensionAsset? asset, Version version, string programFolder, string downloadFolder,
        CancellationToken ct, IReadOnlyList<ReleaseKey>? keys = null)
    {
        if (asset is null)
            throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateNoPackage, NameFor(version)));
        var package = await DownloadAsync(http, asset, version, downloadFolder, ct, keys);
        try
        {
            Stage(package, programFolder, version);
        }
        finally
        {
            File.Delete(package);
        }
    }

    /// <summary>
    /// An update made by 1.3.2 or earlier replaced only Baton.exe: when the extension next to the program is older than
    /// <paramref name="version"/> (the program's), the package of that very release is installed. False when there was
    /// nothing to do (up to date, or no extension folder at all).
    /// </summary>
    public static async Task<bool> RepairAsync(HttpClient http, string address, Version version, string programFolder, string downloadFolder,
        CancellationToken ct, IReadOnlyList<ReleaseKey>? keys = null)
    {
        if (VersionIn(programFolder) is not { } found || found >= version)
            return false;
        var asset = await UpdateChecker.ExtensionOfAsync(http, address, version, ct);
        await PrepareAsync(http, asset, version, programFolder, downloadFolder, ct, keys);
        Swap(programFolder);
        return true;
    }

    /// <summary>Downloads the package to <paramref name="folder"/> and checks its hash and signature; the path of the verified file.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, ExtensionAsset asset, Version version, string folder, CancellationToken ct,
        IReadOnlyList<ReleaseKey>? keys = null)
    {
        keys ??= UpdateSignature.Trusted;
        if (UpdateChecker.Allowed(asset.Download?.AbsoluteUri) is not { } download)
            throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateNoPackage, asset.Name));
        if (!UpdateChecker.IsSha256(asset.Sha256))
            throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateNoHash, asset.Name));
        if (string.IsNullOrWhiteSpace(asset.Signatures))
            throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateNoSignature, asset.Name));
        if (!UpdateSignature.Verify(asset.Signatures, version, asset.Name, asset.Sha256!, keys))
            throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateBadSignature, asset.Name));

        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, asset.Name);
        var temp = target + ".part";
        try
        {
            using (var response = await http.GetAsync(download, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                    throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Strings.UpdateErrorStatus, (int)response.StatusCode));
                if (response.Content.Headers.ContentLength > MaxPackageBytes)
                    throw new UpdateException(Strings.UpdateErrorTooLarge);
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                var buffer = new byte[1 << 16];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (written > MaxPackageBytes)
                        throw new UpdateException(Strings.UpdateErrorTooLarge);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            string actual;
            await using (var stream = File.OpenRead(temp))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateHashMismatch, asset.Name));
            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>
    /// Unpacks a verified package into "extension.new" next to the program and checks that it holds the extension of
    /// <paramref name="version"/>. Only "extension/…" files and baton-firefox.xpi are accepted: no path outside the
    /// folder, no link, no more than the limits above. Anything else leaves nothing behind.
    /// </summary>
    public static void Stage(string package, string programFolder, Version version)
    {
        var staging = Path.Combine(programFolder, StagingName);
        Writable(programFolder, () =>
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
        });
        try
        {
            Writable(programFolder, () => Extract(package, staging));
            var found = VersionIn(staging);
            if (found != version)
                throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateWrongVersion,
                    found?.ToString(3) ?? "?", version.ToString(3)));
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private static void Extract(string package, string staging)
    {
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        long unpacked = 0;
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = OpenZip(package);
        if (zip.Entries.Count > MaxEntries)
            throw Invalid("too many files");
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var isFolder = name.EndsWith('/');
            var parts = name.TrimEnd('/').Split('/');
            if (parts.Any(p => !SafeName(p)))
                throw Invalid(entry.FullName);
            if (!(parts[0] == BundledExtension.FolderName && (parts.Length > 1 || isFolder))
                && !(parts.Length == 1 && parts[0] == BundledExtension.FirefoxPackageName && !isFolder))
                throw Invalid(entry.FullName);
            // Unix mode in the high half of the external attributes: a symbolic link is refused, never followed.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw Invalid(entry.FullName);
            var target = Path.GetFullPath(Path.Combine(staging, Path.Combine(parts)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw Invalid(entry.FullName);
            if (isFolder)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            if (!written.Add(target))
                throw Invalid(entry.FullName + " listed twice");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                var buffer = new byte[1 << 16];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    unpacked += read;
                    if (unpacked > MaxUnpackedBytes)
                        throw Invalid("too large once unpacked");
                    output.Write(buffer, 0, read);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) // a damaged entry, an unknown compression
            {
                throw Invalid($"{entry.FullName}: {ex.Message}");
            }
        }
    }

    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// A plain file or folder name: not empty, "." or "..", no drive or stream (":"), no character Windows refuses, no
    /// device name ("NUL", "COM1.txt"), and no trailing dot or space (Windows drops them, so two names could become one).
    /// </summary>
    private static bool SafeName(string part) =>
        part.Length > 0 && part != "." && part != ".." && !part.Contains(':') && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !part.EndsWith('.') && !part.EndsWith(' ')
        && !DeviceNames.Contains(part.Split('.')[0].TrimEnd(), StringComparer.OrdinalIgnoreCase);

    private static ZipArchive OpenZip(string package)
    {
        try
        {
            return ZipFile.OpenRead(package);
        }
        catch (InvalidDataException ex)
        {
            throw Invalid(ex.Message);
        }
    }

    private static UpdateException Invalid(string detail) =>
        new(string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateInvalid, detail));

    /// <summary>
    /// Puts the staged extension in place: the current folder and Firefox package become ".old" (replacing older ones),
    /// the staged ones take their names. A failure halfway puts the previous ones back.
    /// </summary>
    public static void Swap(string programFolder)
    {
        var staging = Path.Combine(programFolder, StagingName);
        var current = Path.Combine(programFolder, BundledExtension.FolderName);
        var backup = Path.Combine(programFolder, BackupName);
        var aside = Path.Combine(programFolder, AsideName);
        var xpi = Path.Combine(programFolder, BundledExtension.FirefoxPackageName);
        var xpiBackup = Path.Combine(programFolder, XpiBackupName);
        var stagedXpi = Path.Combine(staging, BundledExtension.FirefoxPackageName);
        Writable(programFolder, () =>
        {
            DeleteFolder(backup);
            DeleteFolder(aside);
            if (File.Exists(xpiBackup))
                File.Delete(xpiBackup);
            var hadFolder = Directory.Exists(current);
            var hadXpi = false;
            if (hadFolder)
                Directory.Move(current, backup);
            try
            {
                Directory.Move(Path.Combine(staging, BundledExtension.FolderName), current);
                if (File.Exists(stagedXpi))
                {
                    if (File.Exists(xpi))
                    {
                        File.Move(xpi, xpiBackup);
                        hadXpi = true;
                    }
                    File.Move(stagedXpi, xpi);
                }
            }
            catch
            {
                if (hadXpi && !File.Exists(xpi))
                    File.Move(xpiBackup, xpi);
                if (hadFolder && Directory.Exists(backup))
                {
                    if (Directory.Exists(current))
                        Directory.Move(current, aside);
                    Directory.Move(backup, current);
                }
                throw;
            }
        });
        TryDelete(staging);
        TryDelete(aside);
    }

    /// <summary>
    /// "Restore previous version", and the watcher when a new version does not start: the ".old" extension comes back,
    /// but only when it belongs to <paramref name="restoredVersion"/>, the program being put back (an older ".old" left by
    /// an earlier update stays where it is). The current folder is moved aside first, so a failure puts it back whole.
    /// A Firefox package the update added where there was none stays. Returns true when it was restored.
    /// </summary>
    public static bool Restore(string programFolder, Version restoredVersion)
    {
        if (OldVersion(programFolder) != restoredVersion)
            return false;
        var backup = Path.Combine(programFolder, BackupName);
        var current = Path.Combine(programFolder, BundledExtension.FolderName);
        var aside = Path.Combine(programFolder, AsideName);
        var xpi = Path.Combine(programFolder, BundledExtension.FirefoxPackageName);
        var xpiBackup = Path.Combine(programFolder, XpiBackupName);
        Writable(programFolder, () =>
        {
            DeleteFolder(aside);
            var hadFolder = Directory.Exists(current);
            if (hadFolder)
                Directory.Move(current, aside);
            try
            {
                Directory.Move(backup, current);
            }
            catch
            {
                if (hadFolder && !Directory.Exists(current))
                    Directory.Move(aside, current);
                throw;
            }
            if (File.Exists(xpiBackup))
                File.Move(xpiBackup, xpi, overwrite: true);
        });
        TryDelete(aside);
        return true;
    }

    /// <summary>Where a folder being replaced waits while the swap may still be undone.</summary>
    private const string AsideName = BundledExtension.FolderName + ".failed";

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    // Access denied, write-protected media.
    private static readonly int[] NotWritableErrors = [unchecked((int)0x80070005), unchecked((int)0x80070013)];

    /// <summary>
    /// Runs <paramref name="work"/>; a file system error becomes an <see cref="UpdateException"/> that says what happened:
    /// a folder that cannot be written, or another problem with the files (a full disk, a file in use).
    /// </summary>
    private static void Writable(string programFolder, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException && NotWritableErrors.Contains(ex.HResult))
        {
            throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateNotWritable, programFolder, ex.Message), ex);
        }
        catch (IOException ex)
        {
            throw new UpdateException(string.Format(CultureInfo.CurrentCulture, Strings.ExtensionUpdateFiles, ex.Message), ex);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: the next update clears it before staging.
        }
    }
}
