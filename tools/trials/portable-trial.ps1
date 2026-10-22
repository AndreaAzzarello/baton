<#
.SYNOPSIS
  Real trial of the portable zip: extracted into %TEMP%\dm-trials\portable, started WITHOUT BATON_DATA (the real portable
  mode: data in Data next to Baton.exe, its own pipe, the real registry wrapped so that it only reads). The question about
  the browsers' host is answered "No": "Yes" would point the user's real browsers at the trial copy.
  Every key the app could write is compared before and after, and the whole HKCU is searched for the trial's path
  (Windows' own caches of programs that ran or showed a tray icon are reported by finish.ps1 and cleaned with -Apply).
.EXAMPLE
  powershell -File tools\trials\portable-trial.ps1 -Zip %TEMP%\dm-trials\pkg\Baton-1.3.0-portable.zip
#>
param([Parameter(Mandatory)][string]$Zip)
$ErrorActionPreference = 'Stop'
$work = if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP 'dm-trials' }
$dir = Join-Path $work 'portable\Baton'
$shots = Join-Path $work 'shots'
$tools = $PSScriptRoot
Remove-Item Env:BATON_DATA, Env:DOWNLOADMANAGER_DATA -ErrorAction SilentlyContinue
if (-not (Test-Path "$work\started.txt")) { Get-Date -Format o | Set-Content "$work\started.txt" }
function Check([bool]$ok, [string]$what) { if ($ok) { "OK   $what" } else { "FAIL $what" } }

# What the installed app writes: start with Windows, links, notifications, browser hosts, Installed apps.
$keys = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Run', 'HKCU\Software\Classes\baton', 'HKCU\Software\Classes\AppUserModelId\Baton.App',
  'HKCU\Software\Google\Chrome\NativeMessagingHosts', 'HKCU\Software\Microsoft\Edge\NativeMessagingHosts', 'HKCU\Software\Chromium\NativeMessagingHosts',
  'HKCU\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts', 'HKCU\Software\Mozilla\NativeMessagingHosts',
  'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{27469723-AE14-4420-B2B4-189B8BE709AB}_is1'
function Snapshot { foreach ($k in $keys) { "[$k]"; cmd /c "reg query `"$k`" /s 2>nul" } }

if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
Expand-Archive $Zip $dir
Check ((Test-Path "$dir\Baton.exe") -and (Test-Path "$dir\portable.flag")) "zip: Baton.exe and portable.flag"
$before = Snapshot

$app = Start-Process "$dir\Baton.exe" -PassThru
Start-Sleep 7
$welcome = if ((Get-Culture).TwoLetterISOLanguageName -eq 'it') { 'Benvenuto in Baton' } else { 'Welcome to Baton' }
& "$tools\shot.ps1" -Title $welcome -Out "$shots\portable-firstrun.png" -NoFront -ProcessId $app.Id
# The extension shipped in the zip, as the wizard offers it; start with Windows not preselected.
Check ((Test-Path "$dir\extension\manifest.json") -and (Test-Path "$dir\baton-firefox.xpi")) "zip: extension folder and baton-firefox.xpi"
$state = & "$tools\ui.ps1" -Window $welcome -Action state
Check ([bool]($state | Where-Object { $_ -match '^Edit\s+\S' -and $_ -like "*= $dir\extension" })) "wizard: full path $dir\extension, with an accessible name"
Check ([bool]($state | Where-Object { $_ -match '^Button\s+(Apri la cartella dell''estensione|Open the extension folder)$' })) "wizard: open the extension folder is on"
Check ([bool]($state | Where-Object { $_ -match '^Button\s+(Copia il percorso|Copy the path)' -and $_ -notmatch 'disabled' })) "wizard: copy the path is on"
Check ([bool]($state | Where-Object { $_ -match '^CheckBox\s+.*Windows.*\[Off\]$' })) "wizard: start with Windows not selected"
& "$tools\ui.ps1" -Window $welcome -Action invoke -Name $(if ($welcome -like 'Benvenuto*') { 'Salta' } else { 'Skip' }) | Out-Null
Start-Sleep 4
# The one question of the portable copy (owned by the main window).
& "$tools\shot.ps1" -Title 'Baton' -Out "$shots\portable-question.png" -NoFront -ProcessId $app.Id
$dump = & "$tools\ui.ps1" -Window 'Baton' -Action dump
Check (($dump -join "`n") -match 'portable') "the browser-host question is asked"
& "$tools\ui.ps1" -Window 'Baton' -Action invoke -Name 'No' | Out-Null
Start-Sleep 3
Check (Test-Path "$dir\Data\downloads.db") "data next to the program ($dir\Data)"
Check (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'Baton\portable.flag'))) "nothing in %LOCALAPPDATA%\Baton"
& "$tools\shot.ps1" -Title 'Baton' -Out "$shots\portable-main.png" -NoFront -ProcessId $app.Id
Stop-Process -Id $app.Id -Force
Start-Sleep 2
$answer = python -c "import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute('SELECT value FROM settings WHERE key=?',(sys.argv[2],)).fetchone())" "$dir\Data\downloads.db" PortableBrowserHost
Check ($answer -match "'0'") "the answer 'No' is remembered ($answer)"

$after = Snapshot
$diff = Compare-Object $before $after
Check (-not $diff) "registry keys the app could write: unchanged"
if ($diff) { $diff | Format-Table -AutoSize | Out-String -Width 250 }
$traces = cmd /c "reg query HKCU /s /f `"dm-trials\portable`" /d 2>nul" | Where-Object { $_ -like 'HKEY_*' -and $_ -notmatch 'MuiCache|NotifyIconSettings|Compatibility Assistant|BAM|Explorer\\FeatureUsage' }
Check (-not $traces) "no value of the app pointing to the portable copy"
$traces
