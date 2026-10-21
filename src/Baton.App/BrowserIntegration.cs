using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Baton.App.Services;
using Baton.Core.Integration;

namespace Baton.App;

/// <summary>Registers this executable as the browsers' native messaging host (through <see cref="AppEnvironment"/>) and runs it in that role.</summary>
internal static class BrowserIntegration
{
    /// <summary>Points the browsers at the current executable; repeated on every start so a moved exe keeps working.</summary>
    public static void Register(string dataDirectory)
    {
        try
        {
            AppEnvironment.Registration.RegisterBrowserHost(dataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Browser integration is optional; the app works without it.
        }
    }

    /// <summary>Runs as the native messaging host for the lifetime of the browser's request.</summary>
    public static int RunHost()
    {
        // The browser started this process from the foreground, so the app may take the focus for its dialog.
        AllowSetForegroundWindow(AsfwAny);
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        try
        {
            // A cold start gets 12 s: the extension gives up after 15 s and lets the browser keep the download, so the host
            // must have given up first, or the app could still receive a download the browser is also fetching.
            // The pipe of the real app; a sandboxed test run (DOWNLOADMANAGER_DATA inherited from the browser) uses its own.
            NativeHost.RunAsync(input, output, AppEnvironment.PipeName, LaunchApp, TimeSpan.FromSeconds(12), App.AppVersion).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return 1;
        }
    }

    public static void AllowForeground() => AllowSetForegroundWindow(AsfwAny);

    /// <summary>Opened by the "Register" button of the extension's connection page (a link, so it works without the host).</summary>
    public const string RegisterLink = Core.NotificationLinks.Scheme + ":register";

    public static bool IsRegisterLink(string argument) =>
        string.Equals(argument.TrimEnd('/'), RegisterLink, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Registers the host for every browser and says so. Harmless if a web page opens the link: it only points the
    /// browsers at this same executable, and the browser asks before opening it.
    /// </summary>
    public static int RegisterFromBrowser()
    {
        Register(AppEnvironment.DataDirectory);
        System.Windows.MessageBox.Show(Core.Resources.Strings.BrowserRegistered, Core.Resources.Strings.AppName,
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return 0;
    }

    /// <summary>
    /// Starts the app outside the browser's job object: otherwise closing the browser could also close
    /// the app and the downloads it is running.
    /// </summary>
    private static bool LaunchApp()
    {
        if (Environment.ProcessPath is not { } exe)
            return false;

        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new StringBuilder($"\"{exe}\" {BackgroundArgument}"); // CreateProcessW may write to this buffer
        if (CreateProcess(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateBreakawayFromJob, IntPtr.Zero,
                Path.GetDirectoryName(exe), ref startup, out var info))
        {
            CloseHandle(info.hProcess);
            CloseHandle(info.hThread);
            return true;
        }

        // The job does not allow breakaway: start the app normally. Shell execute does not inherit the browser's pipes.
        try
        {
            Process.Start(new ProcessStartInfo(exe, BackgroundArgument) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Started by the browser: the main window opens minimized and only the confirmation dialog shows.</summary>
    public const string BackgroundArgument = "--background";

    private const int AsfwAny = -1;
    private const uint CreateBreakawayFromJob = 0x01000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
