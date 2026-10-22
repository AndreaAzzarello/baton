<#
.SYNOPSIS
  The extension next to Baton.exe follows a signed in-app update (1.3.3), isolated in %TEMP%\dm-trials\upd133: never
  touches publish\, the real app or its data.
.DESCRIPTION
  -Build  serves a release like GitHub's (release.json with tag v<version>) on 127.0.0.1:8767 from -Release, the folder
          of the signed files: Baton.exe and baton-extension-<version>.zip, each with .sha256 and .sig.
  -Run    installed | portable: extracts -From (the real 1.3.2 portable zip; for "installed" without portable.flag, as the
          installer lays the folder out, with BATON_DATA), then
            1. puts in its "extension" folder the extension of this code labelled 1.3.2 (-Edge loads it): a real 1.3.2
               extension cannot reload itself, so this is the next update as it will happen for 1.3.3 users;
            2. 1.3.2 finds the update ("Update" window, UI Automation only) and replaces Baton.exe; 1.3.3 starts and
               installs its own signed extension package next to it;
            3. Edge (headless, ext-update-browser.mjs) reloads the extension by itself and Check connection is OK;
            4. Firefox (-Firefox) with an older extension shows the notice in its popup;
            5. "Restore the previous version" brings back Baton.exe 1.3.2 and its extension.
  finish.ps1 stops what is left (apps under the trial folder, the server).
#>
param(
  [switch]$Build, [string]$Release, [string]$Version = '1.3.3',
  [ValidateSet('installed', 'portable')][string]$Run, [string]$From,
  [string]$Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe', [switch]$Firefox
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$work = if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP 'dm-trials' }
$base = Join-Path $work 'upd133'
$srv = Join-Path $base 'srv'
$tools = $PSScriptRoot
$port = 8767
if (-not (Test-Path "$work\started.txt")) { Get-Date -Format o | Set-Content "$work\started.txt" }
function Check([bool]$ok, [string]$what) { if ($ok) { "OK   $what" } else { "FAIL $what" } }
function Version([string]$exe) { [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.Split('+')[0] }
function ExtensionVersion([string]$folder) {
  $m = Join-Path $folder 'manifest.json'
  if (Test-Path $m) { (Get-Content $m -Raw | ConvertFrom-Json).version } else { '(none)' }
}
function Stop-Trial { Get-CimInstance Win32_Process -Filter "Name='Baton.exe' OR Name='Baton.exe.old'" | Where-Object { "$($_.ExecutablePath)" -like "$base\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
function Wait-Window([string[]]$titles, [int]$seconds) {
  for ($i = 0; $i -lt $seconds; $i++) {
    $t = Get-Process Baton -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$base\*" -and $_.MainWindowTitle -in $titles } | Select-Object -First 1 -ExpandProperty MainWindowTitle
    if ($t) { return $t }
    Start-Sleep 1
  }
  return $null
}

if ($Build) {
  Stop-Trial
  Get-CimInstance Win32_Process -Filter "Name='python.exe'" | Where-Object { $_.CommandLine -like '*dm-trials*upd133*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  New-Item -ItemType Directory -Force $srv | Out-Null
  $package = "baton-extension-$Version.zip"
  $assets = foreach ($name in 'Baton.exe', 'Baton.exe.sha256', 'Baton.exe.sig', $package, "$package.sha256", "$package.sig") {
    Copy-Item (Join-Path $Release $name) $srv -Force
    [ordered]@{ name = $name; browser_download_url = "http://127.0.0.1:$port/$name" }
  }
  $json = [ordered]@{ tag_name = "v$Version"; name = $Version; body = "Trial release $Version (local)"; assets = @($assets) } | ConvertTo-Json -Depth 4
  [IO.File]::WriteAllText("$srv\release.json", $json, (New-Object Text.UTF8Encoding $false))
  Start-Process python -ArgumentList '-I', '-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', "`"$srv`"" -WindowStyle Hidden
  "served: $srv on http://127.0.0.1:$port/release.json"
}

if ($Run) {
  Stop-Trial
  $dir = Join-Path $base $Run
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  $app = Join-Path $dir 'app'
  Expand-Archive $From $app
  Remove-Item Env:BATON_DATA, Env:DOWNLOADMANAGER_DATA -ErrorAction SilentlyContinue
  if ($Run -eq 'installed') {
    Remove-Item "$app\portable.flag"
    $data = Join-Path $dir 'data'
    $env:BATON_DATA = $data
  } else {
    $data = Join-Path $app 'Data'
  }
  $from = Version "$app\Baton.exe"
  # 1. This code's extension, labelled with the version it replaces.
  node (Join-Path $root 'tools\build-extension.mjs') | Out-Null
  Remove-Item "$app\extension" -Recurse -Force
  Copy-Item (Join-Path $root 'dist\chromium') "$app\extension" -Recurse
  $manifest = Get-Content "$app\extension\manifest.json" -Raw | ConvertFrom-Json
  $manifest.version = $from
  [IO.File]::WriteAllText("$app\extension\manifest.json", ($manifest | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding $false))

  $p = Start-Process "$app\Baton.exe" -ArgumentList '--tray' -PassThru; Start-Sleep 6; Stop-Process -Id $p.Id -Force; Start-Sleep 2
  python -I -c "import sqlite3,sys; d=sqlite3.connect(sys.argv[1]); [d.execute('INSERT INTO settings(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value',(k,v)) for k,v in (('UpdateCheckEnabled','1'),('UpdateUrl',sys.argv[2]),('PortableBrowserHost','0'),('DesktopRelocationAsked','1'),('Language','English'))]; d.commit()" "$data\downloads.db" "http://127.0.0.1:$port/release.json"
  Start-Process "$app\Baton.exe" -ArgumentList '--tray' | Out-Null

  # The browsers' host is the installed Baton.exe: with BATON_DATA it forwards to this trial's own pipe.
  $results = Join-Path $dir 'edge.txt'
  $env:DM_CHROMIUM_EXT = "$app\extension"
  $env:DM_TRIAL_DATA = $data
  $env:DM_TRIAL_VERSION = $Version
  $browser = Start-Process node -ArgumentList (Join-Path $tools 'ext-update-browser.mjs'), "`"$Edge`"" -WorkingDirectory $tools -RedirectStandardOutput $results -RedirectStandardError "$results.err" -PassThru -WindowStyle Hidden

  # 2. The update, as the user would install it.
  $title = Wait-Window 'Update', 'Aggiornamento' 60
  Check ([bool]$title) "$from offers the update $Version ($title)"
  & "$tools\ui.ps1" -Window $title -Action invoke -Name 'Download and install' | Out-Null
  for ($i = 0; $i -lt 120 -and (Version "$app\Baton.exe") -ne $Version; $i++) { Start-Sleep 1 }
  for ($i = 0; $i -lt 120 -and (ExtensionVersion "$app\extension") -ne $Version; $i++) { Start-Sleep 1 }
  Check ((Version "$app\Baton.exe") -eq $Version) "Baton.exe is $(Version "$app\Baton.exe"), Baton.exe.old $(Version "$app\Baton.exe.old")"
  Check ((ExtensionVersion "$app\extension") -eq $Version) "extension next to it: $(ExtensionVersion "$app\extension") (extension.old $(ExtensionVersion "$app\extension.old"))"
  Check ((Test-Path "$app\baton-firefox.xpi") -and (Test-Path "$app\baton-firefox.xpi.old")) "baton-firefox.xpi replaced, the previous one kept"
  Check (-not (Test-Path "$app\extension.new")) "nothing left half unpacked"
  $log = Get-ChildItem "$data\logs" -Filter *.log | Sort-Object LastWriteTime | Select-Object -Last 1
  Select-String -Path $log.FullName -Pattern 'extension' | Select-Object -Last 4 | ForEach-Object { "     log: $($_.Line.Trim())" }

  # 3. Edge reloads the extension by itself.
  $browser.WaitForExit(240000) | Out-Null
  Get-Content $results; Get-Content "$results.err" -ErrorAction SilentlyContinue | Select-Object -First 5

  # 4. Firefox with an older extension: the notice in its popup.
  if ($Firefox) {
    $ff = Join-Path $dir 'firefox.txt'
    node (Join-Path $tools 'ext-update-firefox.mjs') > $ff 2> "$ff.err"
    Get-Content $ff; Get-Content "$ff.err" -ErrorAction SilentlyContinue | Select-Object -First 5
  }

  # 5. Restore the previous version: Baton.exe 1.3.2 and its extension come back.
  & "$tools\ui.ps1" -Window 'Baton' -Action invoke -Name 'Settings' | Out-Null
  Start-Sleep 2
  & "$tools\ui.ps1" -Window 'Settings' -Action select -Name 'Updates' | Out-Null
  & "$tools\ui.ps1" -Window 'Settings' -Action invoke -Name 'Restore the previous version' | Out-Null
  Start-Sleep 2
  & "$tools\ui.ps1" -Window 'Update' -Action invoke -Name 'OK' | Out-Null
  for ($i = 0; $i -lt 60 -and (Version "$app\Baton.exe") -ne $from; $i++) { Start-Sleep 1 }
  Start-Sleep 5
  Check ((Version "$app\Baton.exe") -eq $from) "restored: Baton.exe is $(Version "$app\Baton.exe")"
  Check ((ExtensionVersion "$app\extension") -eq $from -and -not (Test-Path "$app\extension.old")) "restored: the extension is $(ExtensionVersion "$app\extension")"
  Check ([bool](Get-Process Baton -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$app\Baton.exe" })) "the restored version is running"
  if ($Run -eq 'portable') { Check ((Test-Path "$app\portable.flag") -and (Test-Path "$app\Data\downloads.db")) "portable.flag and Data are still next to the program" }
  Stop-Trial
  Remove-Item Env:BATON_DATA, Env:DM_CHROMIUM_EXT, Env:DM_TRIAL_DATA, Env:DM_TRIAL_VERSION -ErrorAction SilentlyContinue
}
