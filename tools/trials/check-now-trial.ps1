<#
.SYNOPSIS
  "Check now" from a published version against the real GitHub release, isolated in %TEMP%\dm-trials\check-now
  (BATON_DATA: registry in memory, own pipe and data). The update window is driven with UI Automation only.
.EXAMPLE
  powershell -File tools\trials\check-now-trial.ps1 -Exe %TEMP%\dm-trials\v130\Baton.exe -Expect 1.3.1
  powershell -File tools\trials\check-now-trial.ps1 -Zip Baton-1.3.2-portable.zip -Expect 1.3.3   # with the extension next to it
  (-Zip: the folder of a portable zip without portable.flag, as the installer lays it out; from 1.3.3 the extension next to
  Baton.exe must follow the update.)
#>
param([string]$Exe, [string]$Zip, [Parameter(Mandatory)][string]$Expect)
$ErrorActionPreference = 'Stop'
$work = if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP 'dm-trials' }
$base = Join-Path $work 'check-now'
function Version([string]$file) { [Diagnostics.FileVersionInfo]::GetVersionInfo($file).ProductVersion.Split('+')[0] }
function Check([bool]$ok, [string]$what) { if ($ok) { "OK   $what" } else { "FAIL $what" } }
if (-not (Test-Path "$work\started.txt")) { Get-Date -Format o | Set-Content "$work\started.txt" }
Get-Process Baton -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$base\*" } | Stop-Process -Force
if (Test-Path $base) { Remove-Item $base -Recurse -Force }
if ($Zip) {
  Expand-Archive $Zip "$base\app"
  Remove-Item "$base\app\portable.flag"
} else {
  New-Item -ItemType Directory -Force "$base\app" | Out-Null
  Copy-Item $Exe "$base\app\Baton.exe"
}
function ExtensionVersion { if (Test-Path "$base\app\extension\manifest.json") { (Get-Content "$base\app\extension\manifest.json" -Raw | ConvertFrom-Json).version } else { '(none)' } }
$env:BATON_DATA = "$base\data"
$from = Version "$base\app\Baton.exe"
$p = Start-Process "$base\app\Baton.exe" -ArgumentList '--tray' -PassThru; Start-Sleep 6; Stop-Process -Id $p.Id -Force; Start-Sleep 2
# Only the automatic check is turned on: the update address stays the version's own default (the project's releases).
python -I -c "import sqlite3,sys; d=sqlite3.connect(sys.argv[1]); [d.execute('INSERT INTO settings(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value',(k,v)) for k,v in (('UpdateCheckEnabled','1'),('DesktopRelocationAsked','1'),('PortableBrowserHost','0'))]; d.commit()" "$base\data\downloads.db"
Start-Process "$base\app\Baton.exe" -ArgumentList '--tray' | Out-Null
$title = $null
for ($i = 0; $i -lt 90 -and -not $title; $i++) {
  Start-Sleep 1
  $title = Get-Process Baton -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$base\*" -and $_.MainWindowTitle -in 'Update', 'Aggiornamento' } |
    Select-Object -First 1 -ExpandProperty MainWindowTitle
}
Check ([bool]$title) "the update is offered to $from"
$names = (& "$PSScriptRoot\ui.ps1" -Window $title -Action dump) -join "`n"
Check ($names -match [regex]::Escape($Expect)) "the offered version is $Expect"
& "$PSScriptRoot\ui.ps1" -Window $title -Action invoke -Name $(if ($title -eq 'Update') { 'Download and install' } else { 'Scarica e installa' }) | Out-Null
for ($i = 0; $i -lt 300 -and (Version "$base\app\Baton.exe") -ne $Expect; $i++) { Start-Sleep 1 }
Start-Sleep 20 # the new version reports that it started; the previous one, waiting as a watcher, closes
Check ((Version "$base\app\Baton.exe") -eq $Expect) "Baton.exe is now $(Version "$base\app\Baton.exe")"
Check ((Version "$base\app\Baton.exe.old") -eq $from) "Baton.exe.old is $from"
Check (@(Get-Process Baton -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$base\app\Baton.exe" }).Count -eq 1) "the new version is running"
if ($Zip) {
  for ($i = 0; $i -lt 120 -and (ExtensionVersion) -ne $Expect; $i++) { Start-Sleep 1 }
  Check ((ExtensionVersion) -eq $Expect) "the extension next to it is $(ExtensionVersion) (extension.old: $(if (Test-Path "$base\app\extension.old") { 'kept' } else { 'none' }))"
  Get-ChildItem "$base\data\logs" -Filter *.log | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { Select-String -Path $_.FullName -Pattern 'extension' | Select-Object -Last 3 | ForEach-Object { "     log: $($_.Line.Trim())" } }
}
"SHA-256 $((Get-FileHash "$base\app\Baton.exe").Hash)"
Get-Process Baton -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$base\*" } | Stop-Process -Force
Remove-Item Env:BATON_DATA
