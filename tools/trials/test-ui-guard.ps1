<#
.SYNOPSIS
  Test of tools\trials\ui.ps1: with more than one Baton process it must refuse to act unless -ProcessId names one.
  It starts two harmless stand-ins called Baton.exe (copies of ping.exe in %TEMP%), never touches a real Baton, and stops only those two.
#>
$ErrorActionPreference = "Stop"
$ui = Join-Path $PSScriptRoot "ui.ps1"
$dir = Join-Path $env:TEMP ("dm-ui-guard-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $dir | Out-Null
$fakes = @()
$failed = 0
function Check($name, $ok, $detail) { if ($ok) { "OK   $name" } else { "FAIL $name  ($detail)"; $script:failed++ } }
try {
  foreach ($n in 1, 2) {
    $d = Join-Path $dir "$n"; New-Item -ItemType Directory $d | Out-Null
    Copy-Item "$env:SystemRoot\System32\PING.EXE" (Join-Path $d "Baton.exe")
    $fakes += Start-Process (Join-Path $d "Baton.exe") -ArgumentList "-n", "60", "127.0.0.1" -PassThru -WindowStyle Hidden
  }
  Start-Sleep 1
  # 1. several Baton processes and no -ProcessId: refuse, say why, exit 2, touch nothing
  $out = powershell -NoProfile -ExecutionPolicy Bypass -File $ui -Window "No such window" -Action dump 2>&1 | Out-String
  Check "refuses without -ProcessId when several Baton processes run" ($LASTEXITCODE -eq 2 -and $out -match "-ProcessId") "exit $LASTEXITCODE; $out"
  # 2. with -ProcessId it goes on (the window does not exist, so: not found, exit 1, not a refusal)
  $out = powershell -NoProfile -ExecutionPolicy Bypass -File $ui -Window "No such window" -Action dump -ProcessId $fakes[0].Id 2>&1 | Out-String
  Check "acts on the process named by -ProcessId" ($LASTEXITCODE -eq 1 -and $out -match "not found") "exit $LASTEXITCODE; $out"
  # 3. a -ProcessId that is not a Baton process is refused
  $out = powershell -NoProfile -ExecutionPolicy Bypass -File $ui -Window "No such window" -Action dump -ProcessId $PID 2>&1 | Out-String
  Check "refuses a -ProcessId that is not a Baton process" ($LASTEXITCODE -eq 2) "exit $LASTEXITCODE; $out"
}
finally {
  foreach ($f in $fakes) { if ($f -and -not $f.HasExited) { Stop-Process -Id $f.Id -Force } }
  Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { "$failed check(s) failed"; exit 1 } else { "all checks passed" }
