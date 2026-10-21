using System.Runtime.InteropServices;

namespace Baton.Core;

public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's real Downloads folder, honoring shell redirection (e.g. OneDrive).</summary>
    public static string Downloads
    {
        get
        {
            if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try
                {
                    return Marshal.PtrToStringUni(ptr)!;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
