using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Baton.Core;
using Microsoft.Win32.SafeHandles;

namespace Baton.App.Services;

/// <summary>Sleep, hibernation and shutdown through Windows.</summary>
public sealed class WindowsPower : IPowerController
{
    public void Sleep() => SetSuspendState(hibernate: false, force: false, wakeupEventsDisabled: false);

    public void Hibernate() => SetSuspendState(hibernate: true, force: false, wakeupEventsDisabled: false);

    /// <summary>The standard shutdown command: other programs are asked to close, nothing is forced.</summary>
    public void Shutdown() =>
        Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
}

/// <summary>
/// Records the action instead of carrying it out. Enabled with BATON_SIMULATE_POWER=1, to try end actions
/// without turning the PC off.
/// </summary>
public sealed class SimulatedPower(string logPath) : IPowerController
{
    public static bool IsRequested => Environment.GetEnvironmentVariable("BATON_SIMULATE_POWER") == "1";

    public void Sleep() => Log("sleep");
    public void Hibernate() => Log("hibernate");
    public void Shutdown() => Log("shutdown");

    private void Log(string action) => File.AppendAllText(logPath, $"{DateTime.Now:O} {action}{Environment.NewLine}");
}

/// <summary>
/// A Windows waitable timer that wakes the PC from sleep or hibernation for the next scheduled start of a queue
/// that asks for it. Windows honours it only when "Allow wake timers" is enabled in the power plan.
/// </summary>
public sealed class WakeTimer : IDisposable
{
    private SafeWaitHandle? _timer;
    private DateTime? _armedFor;

    public DateTime? ArmedFor => _armedFor;

    /// <summary>Arms the timer for <paramref name="utc"/>, or cancels it when null.</summary>
    public void Set(DateTime? utc)
    {
        if (utc == _armedFor)
            return;
        _timer ??= CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
        if (_timer.IsInvalid)
            return;
        if (utc is not { } due)
        {
            CancelWaitableTimer(_timer);
            _armedFor = null;
            return;
        }
        var fileTime = due.ToFileTimeUtc(); // positive: absolute time
        if (SetWaitableTimer(_timer, ref fileTime, 0, IntPtr.Zero, IntPtr.Zero, resume: true))
            _armedFor = due;
        else
            Trace.WriteLine(new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    public void Dispose() => _timer?.Dispose();

    private const uint TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll")]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
