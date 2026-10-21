using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Baton.Core.Platform;

/// <summary>Where the files of deleted downloads go: never deleted for good, so a mistake can be undone.</summary>
public interface IRecycleBin
{
    /// <summary>True once the file is in the Recycle Bin; false when it stayed where it was.</summary>
    bool Recycle(string path);
}

/// <summary>No Recycle Bin (the default, and tests): nothing is deleted.</summary>
public sealed class NoRecycleBin : IRecycleBin
{
    public bool Recycle(string path) => false;
}

/// <summary>
/// The Windows Recycle Bin, through the shell (the file can be restored from there). Only local fixed disks have one:
/// elsewhere (network folders, many removable drives) the shell would delete the file for good, so it is left alone.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBin : IRecycleBin
{
    public bool Recycle(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
            if (!File.Exists(full) || Path.GetPathRoot(full) is not { Length: > 0 } root || root.StartsWith(@"\\", StringComparison.Ordinal)
                || new DriveInfo(root).DriveType != DriveType.Fixed)
                return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        var operation = new ShFileOperation
        {
            Function = Delete,
            From = full + "\0\0",
            // A file too big for the Bin, or a Bin turned off, would be deleted for good without a word: Windows asks first.
            Flags = AllowUndo | NoConfirmation | WantNukeWarning | Silent | NoErrorUi,
        };
        return SHFileOperation(ref operation) == 0 && !operation.AnyOperationsAborted && !File.Exists(full);
    }

    private const uint Delete = 3;
    private const ushort Silent = 0x4, NoConfirmation = 0x10, AllowUndo = 0x40, NoErrorUi = 0x400, WantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOperation operation);
}
