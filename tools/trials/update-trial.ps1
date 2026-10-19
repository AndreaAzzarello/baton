<#
.SYNOPSIS
  Signed update trial, isolated in %TEMP%\dm-trials\update: never touches publish\, the real app or its data.
.DESCRIPTION
  -Build    publishes the current code as 1.1.0 (app\, the "installed" copy) and 1.1.1 (srv\good\), signs 1.1.1 with the
            release key (tools\sign-release.ps1), and writes two hostile manifests offering 1.1.2:
              srv\replay.json  the genuine 1.1.1 signature replayed under version 1.1.2
              srv\bad.json     a tampered program (one byte changed) with the genuine signature of the original
            then starts a local server on 127.0.0.1:8766 serving srv\.
  -Start X  starts the isolated app (DOWNLOADMANAGER_DATA) with the automatic check on and the update address
            http://127.0.0.1:8766/X (e.g. good/latest.json, replay.json, bad.json). The check runs 30 s after the start;
            drive the "Aggiornamento" window with ui.ps1 only.
  finish.ps1 stops the app (its exe is under the trial folder) and the server (its command line names dm-trials).
#>
param([switch]$Build, [string]$Start)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$work = if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP "dm-trials" }
$base = Join-Path $work "update"
$app = Join-Path $base "app"; $srv = Join-Path $base "srv"; $data = Join-Path $base "data"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$startedFile = Join-Path $work "started.txt"
New-Item -ItemType Directory -Force $work | Out-Null
if (-not (Test-Path $startedFile)) { Set-Content $startedFile (Get-Date -Format o) }

function Stop-TrialApp {
  Get-CimInstance Win32_Process -Filter "Name='DownloadManager.exe'" | Where-Object { "$($_.ExecutablePath)" -like "$base\*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; "stopped trial app $($_.ProcessId)" }
  Get-CimInstance Win32_Process -Filter "Name='DownloadManager.exe.old'" | Where-Object { "$($_.ExecutablePath)" -like "$base\*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}

if ($Build) {
  Stop-TrialApp
  if (Test-Path $base) { Remove-Item $base -Recurse -Force } # inside the trial folder only
  New-Item -ItemType Directory -Force $app, "$srv\good", "$srv\bad", $data | Out-Null
  foreach ($v in @(@{ Version = "1.1.0"; Out = $app }, @{ Version = "1.1.1"; Out = "$srv\good" })) {
    & $dotnet publish (Join-Path $root "src\DownloadManager.App") -c Release -r win-x64 -o $v.Out -p:Version=$($v.Version) -v quiet -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish $($v.Version) failed" }
    Get-ChildItem $v.Out -Exclude "DownloadManager.exe" | Remove-Item -Recurse -Force # only the single file is served
  }
  $url = "http://127.0.0.1:8766/good/DownloadManager.exe"
  powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root "tools\sign-release.ps1") -Exe "$srv\good\DownloadManager.exe" -Url $url -Notes "Prova di aggiornamento firmato"
  if ($LASTEXITCODE -ne 0) { throw "signing failed" }
  $good = Get-Content "$srv\good\latest.json" -Raw | ConvertFrom-Json
  $utf8 = New-Object Text.UTF8Encoding($false)

  # Replay: the genuine signature of 1.1.1, offered as 1.1.2.
  $replay = [ordered]@{ version = "1.1.2"; notes = "replay"; url = $url; sha256 = $good.sha256; signature = $good.signature }
  [IO.File]::WriteAllText("$srv\replay.json", ($replay | ConvertTo-Json), $utf8)
  # Tampered: one byte of the program changed, its new hash declared, the original signature kept.
  $bytes = [IO.File]::ReadAllBytes("$srv\good\DownloadManager.exe")
  $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 0xFF
  [IO.File]::WriteAllBytes("$srv\bad\DownloadManager.exe", $bytes)
  $badSha = (Get-FileHash "$srv\bad\DownloadManager.exe" -Algorithm SHA256).Hash.ToLowerInvariant()
  $bad = [ordered]@{ version = "1.1.2"; notes = "tampered"; url = "http://127.0.0.1:8766/bad/DownloadManager.exe"; sha256 = $badSha; signature = $good.signature }
  [IO.File]::WriteAllText("$srv\bad.json", ($bad | ConvertTo-Json), $utf8)

  Get-CimInstance Win32_Process -Filter "Name='python.exe'" | Where-Object { $_.CommandLine -like "*dm-trials*update*srv*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  Start-Process python -ArgumentList "-I", "-m", "http.server", "8766", "--bind", "127.0.0.1", "--directory", "`"$srv`"" -WindowStyle Hidden
  "built: 1.1.0 in $app, signed 1.1.1 and hostile manifests in $srv (http://127.0.0.1:8766/)"
}

if ($Start) {
  Stop-TrialApp
  $env:DOWNLOADMANAGER_DATA = $data
  try {
    if (-not (Test-Path (Join-Path $data "downloads.db"))) {
      $p = Start-Process "$app\DownloadManager.exe" -ArgumentList "--tray" -PassThru
      Start-Sleep 5
      Stop-Process -Id $p.Id -Force
      Start-Sleep 1
    }
    $py = Join-Path $base "settings.py"
    Set-Content $py -Encoding ASCII @'
import sqlite3, sys
db = sqlite3.connect(sys.argv[1])
for k, v in (("UpdateCheckEnabled", "1"), ("UpdateUrl", sys.argv[2])):
    db.execute("INSERT INTO settings(key, value) VALUES(?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", (k, v))
db.commit()
'@
    python -I $py (Join-Path $data "downloads.db") "http://127.0.0.1:8766/$Start"
    Start-Process "$app\DownloadManager.exe" -ArgumentList "--tray" | Out-Null
  } finally { Remove-Item Env:DOWNLOADMANAGER_DATA }
  "started $([Diagnostics.FileVersionInfo]::GetVersionInfo("$app\DownloadManager.exe").FileVersion) with update address http://127.0.0.1:8766/$Start (check in 30 s)"
}
