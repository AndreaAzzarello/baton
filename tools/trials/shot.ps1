param([string]$Title, [string]$Out, [switch]$NoFront, [string[]]$Process = @('Baton', 'DownloadManager'), [int]$ProcessId)
# Captures the top-level window whose title contains $Title (PrintWindow, works even when partly covered).
# Only windows of the named processes (-Process, e.g. an installer) or of one process (-ProcessId: never the real app).
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public struct RECT { public int L, T, R, B; }
  // Only windows of the app's own processes: exact title first, then a title containing the text.
  public static IntPtr Find(string title, uint[] pids) {
    IntPtr exact = IntPtr.Zero, partial = IntPtr.Zero;
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (Array.IndexOf(pids, pid) < 0) return true;
      var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
      var t = sb.ToString();
      if (t == title && exact == IntPtr.Zero) exact = h;
      else if (t.Contains(title) && partial == IntPtr.Zero) partial = h;
      return true; }, IntPtr.Zero);
    return exact != IntPtr.Zero ? exact : partial;
  }
}
"@
[W]::SetProcessDPIAware() | Out-Null
$pids = if ($ProcessId) { [uint32[]]@($ProcessId) } else { [uint32[]]@(Get-Process $Process -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id }) }
if (-not $pids) { Write-Output "no process: $($Process -join ",")$ProcessId"; exit 1 }
$h = [W]::Find($Title, $pids)
if ($h -eq [IntPtr]::Zero) { Write-Output "window not found: $Title"; exit 1 }
if (-not $NoFront) { [W]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 400 }
$r = New-Object W+RECT
[W]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $hgt = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
[W]::PrintWindow($h, $dc, 2) | Out-Null
$g.ReleaseHdc($dc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "saved $Out ($w x $hgt)"
