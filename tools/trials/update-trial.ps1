<#
.SYNOPSIS
  Signed update trial from DownloadManager 1.1 to Baton, isolated in %TEMP%\dm-trials\update: never touches publish\,
  the real app or its data.
.DESCRIPTION
  -Build    copies a DownloadManager 1.1 executable (-Installed, by default %TEMP%\dm-trials\v110\DownloadManager.exe,
            published from the v1.1.0 tag) as app\DownloadManager.exe, the "installed" copy; publishes the current code as
            1.2.0 (srv\good\Baton.exe), signs it with the release key (tools\sign-release.ps1) and writes two hostile
            manifests offering 1.2.1:
              srv\replay.json  the genuine 1.2.0 signature replayed under version 1.2.1
              srv\bad.json     a tampered program (one byte changed) with the genuine signature of the original
            then starts a local server on 127.0.0.1:8766 serving srv\.
  -Start X  starts the installed copy with the automatic check on and the update address http://127.0.0.1:8766/X
            (good/latest.json, replay.json, bad.json). 1.1 keeps its data in data-legacy (DOWNLOADMANAGER_DATA); Baton,
            once installed, in data (BATON_DATA) and copies data-legacy there. With -FromCopyOf <folder> the 1.1 data
            starts as a copy of that folder (for example the real %LOCALAPPDATA%\DownloadManager), with the download
            folders moved into the trial folder. The check runs 30 s after the start; drive the windows with ui.ps1 only.
  finish.ps1 stops the apps (their exes are under the trial folder) and the server (its command line names dm-trials).
#>
param([switch]$Build, [string]$Start, [string]$Installed, [string]$FromCopyOf)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$work = if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP "dm-trials" }
$base = Join-Path $work "update"
$app = Join-Path $base "app"; $srv = Join-Path $base "srv"; $data = Join-Path $base "data"; $legacyData = Join-Path $base "data-legacy"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$startedFile = Join-Path $work "started.txt"
New-Item -ItemType Directory -Force $work | Out-Null
if (-not (Test-Path $startedFile)) { Set-Content $startedFile (Get-Date -Format o) }
if (-not $Installed) { $Installed = Join-Path $work "v110\DownloadManager.exe" }

function Stop-TrialApp {
  Get-CimInstance Win32_Process -Filter "Name='DownloadManager.exe' OR Name='DownloadManager.exe.old' OR Name='Baton.exe' OR Name='Baton.exe.old'" |
    Where-Object { "$($_.ExecutablePath)" -like "$base\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; "stopped trial app $($_.ProcessId)" }
}

if ($Build) {
  if (-not (Test-Path $Installed)) { throw "No DownloadManager 1.1 at $Installed (publish the v1.1.0 tag there, or pass -Installed)." }
  Stop-TrialApp
  if (Test-Path $base) { Remove-Item $base -Recurse -Force } # inside the trial folder only
  New-Item -ItemType Directory -Force $app, "$srv\good", "$srv\bad", $data, $legacyData | Out-Null
  Copy-Item $Installed (Join-Path $app "DownloadManager.exe")
  & $dotnet publish (Join-Path $root "src\Baton.App") -c Release -r win-x64 -o "$srv\good" -p:Version=1.2.0 -v quiet -nologo | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "publish 1.2.0 failed" }
  Get-ChildItem "$srv\good" -Exclude "Baton.exe" | Remove-Item -Recurse -Force # only the single file is served
  $url = "http://127.0.0.1:8766/good/Baton.exe"
  powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root "tools\sign-release.ps1") -Exe "$srv\good\Baton.exe" -Url $url -Notes "Prova di aggiornamento firmato a Baton"
  if ($LASTEXITCODE -ne 0) { throw "signing failed" }
  $good = Get-Content "$srv\good\latest.json" -Raw | ConvertFrom-Json
  $utf8 = New-Object Text.UTF8Encoding($false)

  # Replay: the genuine signature of 1.2.0, offered as 1.2.1.
  $replay = [ordered]@{ version = "1.2.1"; notes = "replay"; url = $url; sha256 = $good.sha256; signature = $good.signature }
  [IO.File]::WriteAllText("$srv\replay.json", ($replay | ConvertTo-Json), $utf8)
  # Tampered: one byte of the program changed, its new hash declared, the original signature kept.
  $bytes = [IO.File]::ReadAllBytes("$srv\good\Baton.exe")
  $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 0xFF
  [IO.File]::WriteAllBytes("$srv\bad\Baton.exe", $bytes)
  $badSha = (Get-FileHash "$srv\bad\Baton.exe" -Algorithm SHA256).Hash.ToLowerInvariant()
  $bad = [ordered]@{ version = "1.2.1"; notes = "tampered"; url = "http://127.0.0.1:8766/bad/Baton.exe"; sha256 = $badSha; signature = $good.signature }
  [IO.File]::WriteAllText("$srv\bad.json", ($bad | ConvertTo-Json), $utf8)

  Get-CimInstance Win32_Process -Filter "Name='python.exe'" | Where-Object { $_.CommandLine -like "*dm-trials*update*srv*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  Start-Process python -ArgumentList "-I", "-m", "http.server", "8766", "--bind", "127.0.0.1", "--directory", "`"$srv`"" -WindowStyle Hidden
  "built: DownloadManager $([Diagnostics.FileVersionInfo]::GetVersionInfo("$app\DownloadManager.exe").FileVersion) in $app, signed Baton 1.2.0 and hostile manifests in $srv (http://127.0.0.1:8766/)"
}

if ($Start) {
  Stop-TrialApp
  # Both names: 1.1 reads only DOWNLOADMANAGER_DATA; Baton, started by it after the update, inherits both.
  $env:DOWNLOADMANAGER_DATA = $legacyData
  $env:BATON_DATA = $data
  try {
    $db = Join-Path $legacyData "downloads.db"
    if (-not (Test-Path $db)) {
      if ($FromCopyOf) {
        # A consistent copy of the database (SQLite backup), the other files as they are; downloads go to the trial folder.
        Get-ChildItem $FromCopyOf -File -Recurse | Where-Object { $_.Name -notlike "downloads.db*" -or $_.Name -like "*.bak" } | ForEach-Object {
          $to = Join-Path $legacyData $_.FullName.Substring($FromCopyOf.TrimEnd('\').Length + 1)
          New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
          Copy-Item $_.FullName $to
        }
        python -I -c "import sqlite3,sys; s=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro', uri=True); d=sqlite3.connect(sys.argv[2]); s.backup(d); d.close()" (Join-Path $FromCopyOf "downloads.db") $db
        python -I (Join-Path $PSScriptRoot "prep_data.py") $db (Join-Path $work "downloads") | Out-Null
      } else {
        $p = Start-Process "$app\DownloadManager.exe" -ArgumentList "--tray" -PassThru
        Start-Sleep 5
        Stop-Process -Id $p.Id -Force
        Start-Sleep 1
      }
    }
    $py = Join-Path $base "settings.py"
    Set-Content $py -Encoding ASCII @'
import sqlite3, sys
db = sqlite3.connect(sys.argv[1])
for k, v in (("UpdateCheckEnabled", "1"), ("UpdateUrl", sys.argv[2])):
    db.execute("INSERT INTO settings(key, value) VALUES(?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", (k, v))
db.commit()
'@
    python -I $py $db "http://127.0.0.1:8766/$Start"
    $exe = if (Test-Path "$app\Baton.exe") { "$app\Baton.exe" } else { "$app\DownloadManager.exe" }
    Start-Process $exe -ArgumentList "--tray" | Out-Null
  } finally { Remove-Item Env:DOWNLOADMANAGER_DATA, Env:BATON_DATA }
  "started $exe $([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion) with update address http://127.0.0.1:8766/$Start (check in 30 s)"
}
