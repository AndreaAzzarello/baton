using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace Baton.Core.Platform;

/// <summary>
/// Shortcuts (desktop, Start menu, taskbar) that started DownloadManager.exe, the app's name up to 1.1, are pointed at
/// Baton.exe; one still named "DownloadManager" is renamed "Baton". Shortcuts to anything else are left alone.
/// Through the shell's own IShellLink: no scripting host, and nothing that needs the dynamic binder of a published exe.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LegacyShortcuts
{
    /// <summary>Desktop and taskbar as they are; the Start menu with its subfolders, where programs keep their shortcuts.</summary>
    public static IEnumerable<(string Folder, bool Subfolders)> UserFolders =>
    [
        (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
        (Environment.GetFolderPath(Environment.SpecialFolder.Programs), true),
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), false),
    ];

    /// <summary>The number of shortcuts changed.</summary>
    public static int Retarget(IEnumerable<(string Folder, bool Subfolders)> folders, string legacyExe, string exe)
    {
        var changed = 0;
        foreach (var (root, subfolders) in folders.Where(f => Directory.Exists(f.Folder)))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.lnk", subfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (!Retarget(path, legacyExe, exe))
                        continue;
                    changed++;
                    var renamed = Path.Combine(Path.GetDirectoryName(path)!, "Baton.lnk");
                    if (Path.GetFileNameWithoutExtension(path).Equals("DownloadManager", StringComparison.OrdinalIgnoreCase) && !File.Exists(renamed))
                        File.Move(path, renamed);
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A shortcut that cannot be read or written stays as it is.
                }
            }
        }
        return changed;
    }

    /// <summary>The target of a shortcut as stored (also when that file no longer exists).</summary>
    public static string TargetOf(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, RawPath);
            return target.ToString();
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static bool Retarget(string path, string legacyExe, string exe)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 2); // STGM_READWRITE
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, RawPath);
            if (target.Length == 0 || !string.Equals(Path.GetFullPath(target.ToString()), Path.GetFullPath(legacyExe), StringComparison.OrdinalIgnoreCase))
                return false;
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetIconLocation(exe, 0);
            ((IPersistFile)link).Save(path, true);
            return true;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private const int RawPath = 0x4; // SLGP_RAWPATH: the stored path, without resolving it

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
