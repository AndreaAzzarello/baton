<#
.SYNOPSIS
  Real trial of the installer, isolated: installs into %TEMP%\dm-trials\inst, starts the installed app only with BATON_DATA
  (registry in memory, own pipe and data), uninstalls with BATON_DATA too (the uninstaller's "--uninstall" and its data
  question then reach only the trial). Inno Setup itself writes its "Installed apps" entry and the Start menu shortcut:
  both go with the uninstall, and finish.ps1 reports them if a trial stops half-way.
.EXAMPLE
  powershell -File tools\trials\install-trial.ps1 -Setup %TEMP%\dm-trials\pkg\Baton-Setup-1.3.0.exe -Lang en -All
  ... -Install / -Run / -Uninstall one step at a time; -Wizard opens the wizard (interactive, driven with UI Automation)
#>
param(
  [Parameter(Mandatory)][string]$Setup,
  [ValidateSet('en', 'it')][string]$Lang = 'en',
  [switch]$Install, [switch]$Run, [switch]$Uninstall, [switch]$All, [switch]$Wizard
)
$ErrorActionPreference = 'Stop'
$work = if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP 'dm-trials' }
$dir = Join-Path $work 'inst\Baton'
$data = Join-Path $work 'inst-data'
$shots = Join-Path $work 'shots'
New-Item -ItemType Directory -Force $shots, $data | Out-Null
if (-not (Test-Path "$work\started.txt")) { Get-Date -Format o | Set-Content "$work\started.txt" }
# Every Baton.exe started from here on (the installer's checks, the app, the uninstaller's clean-up) is sandboxed.
$env:BATON_DATA = $data
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{27469723-AE14-4420-B2B4-189B8BE709AB}_is1'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Baton.lnk'
$desk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Baton.lnk'
$tools = $PSScriptRoot
function Check([bool]$ok, [string]$what) { if ($ok) { "OK   $what" } else { "FAIL $what" } }
function Target([string]$link) { (New-Object -ComObject WScript.Shell).CreateShortcut($link).TargetPath }

# Never next to a real installation: its entry and shortcut would be replaced by the trial's.
if ((Test-Path $key) -and -not ("$((Get-ItemProperty $key).InstallLocation)" -like "$work\*")) { throw "Baton is installed for real ($((Get-ItemProperty $key).InstallLocation)): no trial here" }
if ((Test-Path $lnk) -and -not ((Target $lnk) -like "$work\*")) { throw "a Start menu shortcut 'Baton' exists already: no trial here" }

if ($Install -or $All) {
  $log = Join-Path $work "inst-setup-$Lang.log"
  $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LANG=$Lang", "/DIR=`"$dir`"", "/LOG=`"$log`"" -PassThru -Wait
  Check ($p.ExitCode -eq 0) "setup exit code $($p.ExitCode)"
  foreach ($f in 'Baton.exe', 'LICENSE.txt', 'THIRD-PARTY-LICENSES.txt', 'unins000.exe') { Check (Test-Path (Join-Path $dir $f)) "installed $f" }
  $entry = Get-ItemProperty $key -ErrorAction SilentlyContinue
  Check ($entry.DisplayName -eq 'Baton') "Installed apps: name '$($entry.DisplayName)'"
  Check ($entry.DisplayVersion -eq (Get-Item (Join-Path $dir 'Baton.exe')).VersionInfo.ProductVersion.Split('+')[0]) "Installed apps: version $($entry.DisplayVersion)"
  Check ($entry.Publisher -eq 'Andrea Azzarello') "Installed apps: publisher '$($entry.Publisher)'"
  Check ("$($entry.DisplayIcon)" -like "$dir\Baton.exe*") "Installed apps: icon $($entry.DisplayIcon)"
  Check ("$($entry.InstallLocation)".TrimEnd('\') -eq $dir) "Installed apps: location $($entry.InstallLocation)"
  Check ((Test-Path $lnk) -and (Target $lnk) -eq "$dir\Baton.exe") "Start menu shortcut"
  Check (-not (Test-Path $desk) -or (Target $desk) -ne "$dir\Baton.exe") "no Desktop shortcut (the task is off by default)"
  Check ((Get-Content $log -Raw) -match 'Installation process succeeded') "setup log"
}

if ($Run -or $All) {
  # First start: the welcome wizard, in the language of Windows; then the main window, then the other language.
  $app = Start-Process (Join-Path $dir 'Baton.exe') -PassThru
  Start-Sleep 6
  $title = if ((Get-Culture).TwoLetterISOLanguageName -eq 'it') { 'Benvenuto in Baton' } else { 'Welcome to Baton' }
  & "$tools\shot.ps1" -Title $title -Out "$shots\inst-firstrun.png" -NoFront -ProcessId $app.Id
  & "$tools\ui.ps1" -Window $title -Action invoke -Name $(if ($title -like 'Benvenuto*') { 'Salta' } else { 'Skip' }) | Out-Null
  Start-Sleep 3
  & "$tools\shot.ps1" -Title 'Baton' -Out "$shots\inst-main-windows.png" -NoFront -ProcessId $app.Id
  Stop-Process -Id $app.Id -Force; Start-Sleep 2
  foreach ($language in 'English', 'Italian') {
    python -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute('INSERT OR REPLACE INTO settings(key,value) VALUES(?,?)',('Language',sys.argv[2])); c.commit()" (Join-Path $data 'downloads.db') $language
    $app = Start-Process (Join-Path $dir 'Baton.exe') -PassThru
    Start-Sleep 6
    $names = & "$tools\ui.ps1" -Window 'Baton' -Action dump
    $expected = if ($language -eq 'English') { 'All downloads' } else { 'Tutti i download' }
    Check (($names -join "`n") -match [regex]::Escape($expected)) "$language interface ('$expected' in the window)"
    & "$tools\shot.ps1" -Title 'Baton' -Out "$shots\inst-main-$language.png" -NoFront -ProcessId $app.Id
    Stop-Process -Id $app.Id -Force; Start-Sleep 2
  }
}

if ($Wizard) {
  # Interactive, in the chosen language: every page is captured, the buttons are pressed with UI Automation.
  $p = Start-Process $Setup -ArgumentList "/LANG=$Lang", "/DIR=`"$dir`"" -PassThru
  Start-Sleep 5
  $setupProcess = 'Baton-Setup-*'
  $windowTitle = (Get-Process $setupProcess | Where-Object MainWindowTitle | Select-Object -First 1).MainWindowTitle
  $next = if ($Lang -eq 'it') { 'Avanti' } else { 'Next' }
  for ($page = 1; $page -le 6; $page++) {
    & "$tools\shot.ps1" -Title $windowTitle -Out "$shots\wizard-$Lang-$page.png" -NoFront -Process $setupProcess
    $names = & "$tools\ui.ps1" -Window $windowTitle -Action dump 2>$null
    $names | Set-Content -Encoding UTF8 "$shots\wizard-$Lang-$page.txt"
    $button = @($next, 'Install', 'Installa', 'Finish', 'Fine') | Where-Object { $names -match "\s$([regex]::Escape($_))$" } | Select-Object -First 1
    if (-not $button) { "no button on page $page"; break }
    "page $page -> $button"
    & "$tools\ui.ps1" -Window $windowTitle -Action click -Name $button | Out-Null
    if ($button -in 'Finish', 'Fine') { break }
    Start-Sleep $(if ($button -in "Install", "Installa") { 15 } else { 4 })
  }
}

if ($Uninstall -or $All) {
  Get-Process Baton -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$dir\Baton.exe" } | Stop-Process -Force
  $log = Join-Path $work "inst-uninstall-$Lang.log"
  Start-Process (Join-Path $dir 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', "/LOG=`"$log`"" -Wait
  # The uninstaller runs again from a temporary copy and returns at once: wait for it to finish.
  for ($i = 0; $i -lt 60 -and (Test-Path (Join-Path $dir 'Baton.exe')); $i++) { Start-Sleep 1 }
  Start-Sleep 2
  Check (-not (Test-Path $dir)) "program folder removed"
  Check (-not (Test-Path $key)) "Installed apps entry removed"
  Check (-not (Test-Path $lnk)) "Start menu shortcut removed"
  Check (Test-Path (Join-Path $data 'downloads.db')) "data kept (silent uninstall)"
}
