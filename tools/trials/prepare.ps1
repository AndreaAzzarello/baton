<#
.SYNOPSIS
  Prepares a real-browser trial: an isolated app data folder whose downloads go to the work folder (never the Desktop),
  the extension builds, the Firefox trial copy, a portable Firefox, the trial dependencies and the local test site.
.PARAMETER Ffmpeg
  Optional ffmpeg.exe for the sandboxed app; by default a path that does not exist (no ffmpeg).
.NOTES
  The sandboxed app runs with BATON_DATA: in-memory registry, own pipe. The real app is not touched.
  The work folder is %TEMP%\dm-trials unless DM_TRIALS is set.
#>
param([string]$Ffmpeg = "", [switch]$SkipFirefox)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$work = if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP "dm-trials" }
$exe = Join-Path $root "publish\Baton.exe"
foreach ($d in "data", "downloads", "shots", "browser-downloads") { New-Item -ItemType Directory -Force (Join-Path $work $d) | Out-Null }
# The trial starts at the first preparation; finish.ps1 -Apply ends it. Running prepare again keeps the date, so
# finish.ps1 still recognizes the app instances started before.
$startedFile = Join-Path $work "started.txt"
if (-not (Test-Path $startedFile)) { Set-Content $startedFile (Get-Date -Format o) }

# 1. Isolated data: the app creates its database once, then its folders are pointed at the work folder.
$data = Join-Path $work "data"
if (-not (Test-Path (Join-Path $data "downloads.db"))) {
  $env:BATON_DATA = $data
  $p = Start-Process $exe -ArgumentList "--tray" -PassThru
  Start-Sleep 4
  Stop-Process -Id $p.Id -Force   # only the instance started here
  Remove-Item Env:BATON_DATA
  Start-Sleep 1
}
if (-not $Ffmpeg) { $Ffmpeg = Join-Path $work "no-ffmpeg\ffmpeg.exe" }
python "$PSScriptRoot\prep_data.py" (Join-Path $data "downloads.db") (Join-Path $work "downloads") $Ffmpeg

# 2. Extension builds, and the Firefox trial copy (BiDi cannot open moz-extension:// pages, so it opens check.html itself).
node (Join-Path $root "tools\build-extension.mjs")
$ffExt = Join-Path $work "ext-firefox-trial"
if (Test-Path $ffExt) { Remove-Item $ffExt -Recurse -Force }   # inside the work folder only
Copy-Item (Join-Path $root "dist\firefox") $ffExt -Recurse
Add-Content (Join-Path $ffExt "background.js") "`n// TRIAL ONLY: open the check page so automation can reach an extension page.`nsetTimeout(() => chrome.tabs.create({ url: `"check.html`" }), 1500);"

# 3. Trial dependencies and portable Firefox (in the work folder, no installation).
Push-Location $PSScriptRoot
if (-not (Test-Path "node_modules\puppeteer-core")) { npm install --no-audit --no-fund | Out-Null }
if (-not $SkipFirefox -and -not (Get-ChildItem (Join-Path $work "firefox-cache") -Recurse -Filter firefox.exe -ErrorAction SilentlyContinue)) {
  npx @puppeteer/browsers install firefox@stable --path (Join-Path $work "firefox-cache") | Select-Object -Last 1
}
Pop-Location

# 4. Local test site (links, form, HLS page, Clear Key page).
$running = Get-CimInstance Win32_Process -Filter "Name='python.exe'" | Where-Object { $_.CommandLine -like "*trials*site*server.py*" }
if (-not $running) {
  Start-Process python -ArgumentList "-I", "`"$PSScriptRoot\site\server.py`"", "8765", "`"$(Join-Path $work 'site-log.jsonl')`"" -WindowStyle Hidden
  Start-Sleep 1
}
"ready: work folder $work"
