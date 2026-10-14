using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Core;

/// <summary>
/// Marking the part file sparse lets segments write far into a preallocated file
/// without NTFS first zero-filling everything before the write offset.
/// </summary>
internal static class SparseFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    public static void TryMarkSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return;
        DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);
}
