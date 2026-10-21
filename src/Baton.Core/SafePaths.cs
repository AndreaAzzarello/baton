namespace Baton.Core;

/// <summary>
/// Which local paths a value from outside (an imported file, a PAC location) may point to. Network paths can leak the
/// user's Windows credentials (NTLM) to the server and serve or receive files elsewhere; system and start-up folders
/// would let a download replace or start programs.
/// </summary>
public static class SafePaths
{
    /// <summary>"\\server\share", "\\?\UNC\…", "\\.\device", "//server": anything that is not a plain drive path.</summary>
    public static bool IsNetworkOrDevice(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
        || path.StartsWith(@"\/", StringComparison.Ordinal) || path.StartsWith(@"/\", StringComparison.Ordinal);

    /// <summary>A drive letter mapped to a network share.</summary>
    public static bool IsOnNetworkDrive(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return true; // not a drive Windows knows: not trusted
        }
    }

    /// <summary>A file or folder on this computer's own drives: fully qualified, not a network or device path.</summary>
    public static bool IsLocal(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(path)
        && !IsNetworkOrDevice(path) && !IsOnNetworkDrive(path);

    /// <summary>
    /// Windows, the programs, ProgramData, the Start menu and Startup folders (of the user and of all users), AppData, and
    /// the PowerShell profile folders.
    /// </summary>
    public static IEnumerable<string> SensitiveFolders()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.Startup,
            Environment.SpecialFolder.CommonStartup, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
        ];
        foreach (var folder in folders)
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0)
                yield return path;
        }
        // PowerShell runs the profile scripts it finds here every time it starts, like a Startup folder.
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length > 0)
        {
            yield return Path.Combine(documents, "WindowsPowerShell");
            yield return Path.Combine(documents, "PowerShell");
        }
    }

    /// <summary>
    /// A folder downloads from an untrusted source (an imported file) may use: on this computer, not a system, program,
    /// start-up or application-data folder, and inside the user's profile or on a data drive (any local drive other than
    /// the one Windows is on). Null when acceptable, otherwise why not.
    /// </summary>
    public static string? CheckImportedFolder(string folder, string? profile = null, string? systemDrive = null)
    {
        if (!IsLocal(folder))
            return Resources.Strings.ImportFolderNotLocal;
        // Compared as the real paths: "APPDAT~1" short names and junctions or symbolic links must not hide where it leads.
        var full = RealPath(Path.GetFullPath(folder));
        if (!IsLocal(full))
            return Resources.Strings.ImportFolderNotLocal; // a link to a network share
        if (SensitiveFolders().Any(s => IsUnder(full, s) || IsUnder(full, RealPath(s))))
            return Resources.Strings.ImportFolderSensitive;
        profile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        systemDrive ??= Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var onSystemDrive = string.Equals(Path.GetPathRoot(full), systemDrive, StringComparison.OrdinalIgnoreCase);
        return !onSystemDrive || IsUnder(full, profile) && !string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(profile),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : Resources.Strings.ImportFolderOutsideProfile;
    }

    /// <summary>
    /// The path as Windows resolves it: the deepest part that exists is opened and asked for its final name (long names,
    /// links followed), and the rest, which does not exist yet, is kept as written.
    /// </summary>
    public static string RealPath(string full)
    {
        var existing = full;
        var rest = "";
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
                return full;
            rest = Path.Combine(Path.GetFileName(existing), rest);
            existing = parent;
        }
        using var handle = CreateFile(existing, 0, 7 /* share read, write, delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS: also folders */, IntPtr.Zero);
        if (handle.IsInvalid)
            return full;
        var buffer = new System.Text.StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
        if (length <= 0 || length >= buffer.Capacity)
            return full;
        var final = buffer.ToString();
        final = final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..]
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        return Path.TrimEndingDirectorySeparator(Path.Combine(final, rest));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path, int length, uint flags);

    public static bool IsUnder(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
