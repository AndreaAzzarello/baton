# Stop: at the end of a reply, one incremental .NET build if sources changed since the last good build (never two at
# once: a lock file), and a report of trial processes still running. It only reports; it never stops a process.
[Console]::InputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8
try { $hook = [Console]::In.ReadToEnd() | ConvertFrom-Json } catch { $hook = $null }
$root = Split-Path (Split-Path $PSScriptRoot)
$state = Join-Path $root ".claude\state"
New-Item -ItemType Directory -Force $state | Out-Null
$messages = @()
$blockReason = $null

# --- leftovers from trials (report only)
$started = Join-Path $(if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP "dm-trials" }) "started.txt"
$since = if (Test-Path $started) { [datetime](Get-Content $started) } else { [datetime]::MinValue }
$apps = @(Get-CimInstance Win32_Process -Filter "Name='Baton.exe' OR Name='DownloadManager.exe'" -ErrorAction SilentlyContinue |
  Where-Object { $_.CommandLine -like "*\bin\Debug\*" -or ($_.CommandLine -like "*--background*" -and $_.CreationDate -gt $since) })
$site = @(Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*trials*site*server.py*" })
if ($apps.Count -or $site.Count) {
  $list = ($apps | ForEach-Object { "$($_.ProcessId) $(($_.CommandLine -split '\\')[-1])" }) + ($site | ForEach-Object { "$($_.ProcessId) sito di prova" })
  $messages += "Processi di prova ancora aperti: $($list -join '; '). Chiudili con tools\trials\finish.ps1 -Apply (prima senza -Apply per vedere cosa farebbe)."
}

# --- incremental build, only when C#/XAML/project/resource files changed since the last good build
$stamp = Join-Path $state "build.stamp"
$last = if (Test-Path $stamp) { (Get-Item $stamp).LastWriteTimeUtc } else { [datetime]::MinValue }
$changed = Get-ChildItem (Join-Path $root "src"), (Join-Path $root "tests") -Recurse -File -Include *.cs, *.xaml, *.csproj, *.resx, *.props -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $last } | Select-Object -First 1
if ($changed) {
  $lock = Join-Path $state "build.lock"
  $handle = $null
  try {
    if ((Test-Path $lock) -and ((Get-Date) - (Get-Item $lock).LastWriteTime).TotalMinutes -gt 10) { Remove-Item $lock -Force }  # left by a crashed build
    $handle = [IO.File]::Open($lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
  } catch { $messages += "Build saltata: un'altra build è già in corso." }
  if ($handle) {
    try {
      $begin = Get-Date
      $out = & "C:\Program Files\dotnet\dotnet.exe" build (Join-Path $root "Baton.slnx") -nologo -v q 2>&1
      $seconds = [int]((Get-Date) - $begin).TotalSeconds
      if ($LASTEXITCODE -eq 0) {
        Set-Content $stamp (Get-Date -Format o)
        $warnings = @($out | Select-String ': warning ' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
        $messages += "Build .NET riuscita ($seconds s)$(if ($warnings.Count) { ", $($warnings.Count) avvisi" })."
        if ($warnings.Count) { $blockReason = "La build ha avvisi (il progetto li tiene a zero):`n$(($warnings | Select-Object -First 15) -join "`n")" }
      } else {
        $errors = @($out | Select-String ': error ' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
        if (-not $errors.Count) { $errors = @($out | Select-Object -Last 15) }
        $messages += "Build .NET FALLITA ($seconds s)."
        $blockReason = "La build incrementale è fallita:`n$(($errors | Select-Object -First 20) -join "`n")"
      }
    } finally {
      $handle.Dispose()
      Remove-Item $lock -Force -ErrorAction SilentlyContinue
    }
  }
}

$result = @{}
if ($messages.Count) { $result.systemMessage = $messages -join " " }
# Build problems go back to Claude once; a second stop in a row is not blocked again (no loops).
if ($blockReason -and -not $hook.stop_hook_active) { $result.decision = "block"; $result.reason = $blockReason }
if ($result.Count) { $result | ConvertTo-Json -Compress }
exit 0
