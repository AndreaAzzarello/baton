using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DownloadManager.Core.Integration;
using Microsoft.Win32;

namespace DownloadManager.App;

/// <summary>Registers this executable as the browsers' native messaging host and runs it in that role.</summary>
internal static class BrowserIntegration
{
    // Per-user registry locations where Chromium-based browsers look up native messaging hosts.
    private static readonly string[] BrowserKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
    ];

    /// <summary>Points the browsers at the current executable; repeated on every start so a moved exe keeps working.</summary>
    public static void Register(string dataDirectory)
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var manifestPath = Path.Combine(dataDirectory, NativeHost.Name + ".json");
            var manifest = new
            {
                name = NativeHost.Name,
                description = "DownloadManager",
                path = exe,
                type = "stdio",
                allowed_origins = new[] { $"chrome-extension://{NativeHost.ExtensionId}/" },
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            foreach (var key in BrowserKeys)
            {
                using var hosts = Registry.CurrentUser.CreateSubKey($@"{key}\{NativeHost.Name}");
                hosts.SetValue("", manifestPath);
            }
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
            NativeHost.RunAsync(input, output, AppChannel.DefaultPipeName, LaunchApp, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return 1;
        }
    }

    public static void AllowForeground() => AllowSetForegroundWindow(AsfwAny);

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
