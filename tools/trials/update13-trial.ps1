<#
.SYNOPSIS
  Signed in-app updates of 1.3, isolated in %TEMP%\dm-trials\upd13: never touches publish\, the real app or its data.
.DESCRIPTION
  -Build  signs (tools\sign-release.ps1, the release key) the 1.3.0 Baton.exe given with -Exe130 and a 1.3.1 trial build of
          the current code, writes their latest.json and serves them on 127.0.0.1:8766. Nothing of this is published.
  -Run    one scenario, then reports OK/FAIL:
            legacy12   the real 1.2.0 (-Exe120, published from the v1.2.0 tag) updates itself to 1.3.0, as users of 1.2 will;
            installed  1.3.0 installed by the installer (into upd13\inst, with BATON_DATA) updates itself to 1.3.1;
            portable   1.3.0 from the portable zip updates itself to 1.3.1 (portable mode, so without BATON_DATA: the
                       browser-host question was answered "No" in its data beforehand).
          The app checks 30 s after the start; the "Update" window is driven with UI Automation only.
  finish.ps1 stops what is left (apps under the trial folder, the server).
#>
param(
  [switch]$Build, [string]$Exe130, [string]$Exe120,
  [ValidateSet('legacy12', 'installed', 'portable')][string]$Run, [string]$Setup, [string]$Zip, [string]$FromCopyOf
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$work = if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP 'dm-trials' }
$base = Join-Path $work 'upd13'
$srv = Join-Path $base 'srv'
$tools = $PSScriptRoot
if (-not (Test-Path "$work\started.txt")) { Get-Date -Format o | Set-Content "$work\started.txt" }
function Check([bool]$ok, [string]$what) { if ($ok) { "OK   $what" } else { "FAIL $what" } }
function Version([string]$exe) { [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.Split('+')[0] }
function Stop-Trial { Get-CimInstance Win32_Process -Filter "Name='Baton.exe' OR Name='Baton.exe.old'" | Where-Object { "$($_.ExecutablePath)" -like "$base\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
function Settings([string]$db, [string]$url) {
  python -I -c "import sqlite3,sys; d=sqlite3.connect(sys.argv[1]); [d.execute('INSERT INTO settings(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value',(k,v)) for k,v in (('UpdateCheckEnabled','1'),('UpdateUrl',sys.argv[2]),('PortableBrowserHost','0'),('DesktopRelocationAsked','1'))]; d.commit()" $db $url
}

if ($Build) {
  Stop-Trial
  foreach ($v in '130', '131') { New-Item -ItemType Directory -Force "$srv\$v" | Out-Null }
  Copy-Item $Exe130 "$srv\130\Baton.exe" -Force
  & 'C:\Program Files\dotnet\dotnet.exe' publish (Join-Path $root 'src\Baton.App') -c Release -r win-x64 -o "$srv\131" -p:Version=1.3.1 -v quiet -nologo | Out-Null
  Get-ChildItem "$srv\131" -Exclude 'Baton.exe' | Remove-Item -Recurse -Force
  foreach ($v in '130', '131') {
    powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root 'tools\sign-release.ps1') -Exe "$srv\$v\Baton.exe" -Url "http://127.0.0.1:8766/$v/Baton.exe" -Notes "Trial update $v"
    if ($LASTEXITCODE -ne 0) { throw "signing $v failed" }
  }
  Get-CimInstance Win32_Process -Filter "Name='python.exe'" | Where-Object { $_.CommandLine -like '*dm-trials*upd13*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  Start-Process python -ArgumentList '-I', '-m', 'http.server', '8766', '--bind', '127.0.0.1', '--directory', "`"$srv`"" -WindowStyle Hidden
  "built: 1.3.0 and 1.3.1 signed in $srv, served on http://127.0.0.1:8766/"
}

if ($Run) {
  Stop-Trial
  $dir = Join-Path $base $Run
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  New-Item -ItemType Directory -Force $dir | Out-Null
  Remove-Item Env:BATON_DATA, Env:DOWNLOADMANAGER_DATA -ErrorAction SilentlyContinue
  switch ($Run) {
    'legacy12' { $app = "$dir\app"; New-Item -ItemType Directory $app | Out-Null; Copy-Item $Exe120 "$app\Baton.exe"; $env:BATON_DATA = "$dir\data"; $data = "$dir\data"; $target = '1.3.0' }
    'installed' {
      $env:BATON_DATA = "$dir\data"; $data = "$dir\data"; $app = "$dir\inst"; $target = '1.3.1'
      Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$app`"" -Wait
    }
    'portable' { $app = "$dir\app"; Expand-Archive $Zip $app; $data = "$app\Data"; $target = '1.3.1' }
  }
  if ($FromCopyOf) {
    # A consistent read-only copy of a real database, with its download folders moved into the trial folder.
    New-Item -ItemType Directory -Force $data | Out-Null
    python -I -c "import sqlite3,sys; s=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro', uri=True); d=sqlite3.connect(sys.argv[2]); s.backup(d); d.close()" (Join-Path $FromCopyOf 'downloads.db') "$data\downloads.db"
    python -I (Join-Path $tools 'prep_data.py') "$data\downloads.db" (Join-Path $work 'downloads') | Out-Null
  }
  $before = Version "$app\Baton.exe"
  # First start creates the data (the welcome wizard is skipped by the settings written below).
  $p = Start-Process "$app\Baton.exe" -ArgumentList '--tray' -PassThru; Start-Sleep 6; Stop-Process -Id $p.Id -Force; Start-Sleep 2
  $url = if ($target -eq '1.3.0') { 'http://127.0.0.1:8766/130/latest.json' } else { 'http://127.0.0.1:8766/131/latest.json' }
  Settings "$data\downloads.db" $url
  $count = python -I -c "import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute('select count(*) from downloads').fetchone()[0])" "$data\downloads.db"
  Start-Process "$app\Baton.exe" -ArgumentList '--tray' | Out-Null
  $title = $null
  for ($i = 0; $i -lt 60 -and -not $title; $i++) {
    Start-Sleep 1
    $title = Get-Process Baton -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$base\*" -and $_.MainWindowTitle -in 'Update', 'Aggiornamento' } | Select-Object -First 1 -ExpandProperty MainWindowTitle
  }
  Check ([bool]$title) "the update $target is offered ($title)"
  & "$tools\shot.ps1" -Title $title -Out "$work\shots\upd13-$Run.png" -NoFront -ProcessId (Get-Process Baton | Where-Object { $_.Path -like "$base\*" } | Select-Object -First 1).Id | Out-Null
  & "$tools\ui.ps1" -Window $title -Action invoke -Name $(if ($title -eq 'Update') { 'Download and install' } else { 'Scarica e installa' }) | Out-Null
  for ($i = 0; $i -lt 120 -and (Version "$app\Baton.exe") -ne $target; $i++) { Start-Sleep 1 }
  Start-Sleep 15 # the new version reports that it started, the old one (the watcher) closes
  Check ((Version "$app\Baton.exe") -eq $target) "Baton.exe is now $(Version "$app\Baton.exe") (was $before)"
  Check ((Test-Path "$app\Baton.exe.old") -and (Version "$app\Baton.exe.old") -eq $before) "Baton.exe.old is the previous version"
  $running = Get-Process Baton -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$app\Baton.exe" }
  Check ([bool]$running) "the new version is running"
  $after = python -I -c "import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute('select count(*) from downloads').fetchone()[0])" "$data\downloads.db"
  Check ($after -eq $count) "same downloads in the data ($after)"
  if ($Run -eq 'portable') { Check ((Test-Path "$app\portable.flag") -and (Test-Path "$app\Data\downloads.db")) "portable.flag and Data are still next to the program" }
  Stop-Trial
  if ($Run -eq 'installed') {
    Check (Test-Path "$app\unins000.exe") "the uninstaller is still there"
    # The trial installation goes (with BATON_DATA still set, so its clean-up reaches only the trial).
    Start-Process "$app\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES' -Wait
    for ($i = 0; $i -lt 60 -and (Test-Path "$app\Baton.exe"); $i++) { Start-Sleep 1 }
    Check (-not (Test-Path "$app\Baton.exe") -and -not (Test-Path "$app\Baton.exe.old")) "uninstalled, Baton.exe.old included"
  }
}
