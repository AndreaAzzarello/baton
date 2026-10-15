<#
.SYNOPSIS
  Real trial of the grabber in the isolated app: a "grab" message (as the extension's "Capture this site" sends it) opens
  the wizard, which is filled through UI Automation; the run is followed until it ends, then screenshots and checks.
.PARAMETER Url
  Start page. Default: the local trial site (http://127.0.0.1:8765/grab/). For a public site use one made for crawling,
  e.g. https://books.toscrape.com/, with a low -MaxPages.
.PARAMETER Mode
  Files (only the filtered files, then sent to a queue with -Send) or Mirror (offline copy in the work folder).
.NOTES
  Needs tools\trials\prepare.ps1 first (isolated data, site on port 8765). Never input on the desktop: UI Automation only,
  screenshots with -NoFront. Everything is written in the work folder (%TEMP%\dm-trials or DM_TRIALS).
#>
param(
  [string]$Url = "http://127.0.0.1:8765/grab/",
  [ValidateSet("Files", "Mirror")][string]$Mode = "Files",
  [string]$Name = "",
  [string]$Extensions = "pdf",
  [int]$Depth = 2,
  [int]$MaxPages = 50,
  [int]$MaxFiles = 1000,
  [double]$Rate = 10,
  [switch]$Send,
  [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$work = if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP "dm-trials" }
$data = Join-Path $work "data"
$exe = Join-Path $root "publish\DownloadManager.exe"
$ui = Join-Path $PSScriptRoot "ui.ps1"
$shot = Join-Path $PSScriptRoot "shot.ps1"
$shots = Join-Path $work "shots"
if (-not $Name) { $Name = "Prova " + ([Uri]$Url).Host + " " + $Mode }
$stamp = Get-Date -Format "HHmmss"

function Ui([string]$Window, [string]$Action, [string]$Element, [string]$Value) {
  $result = & $ui -Window $Window -Action $Action -Name $Element -Value $Value
  if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "UI: $result" }
  $result
}

# 1. The isolated app (its own pipe and in-memory registry), started minimized if it is not running.
$running = Get-CimInstance Win32_Process -Filter "Name='DownloadManager.exe'" | Where-Object { $_.CommandLine -like "*--background*" }
if (-not $running) {
  $env:DOWNLOADMANAGER_DATA = $data
  Start-Process $exe -ArgumentList "--background" | Out-Null
  Remove-Item Env:DOWNLOADMANAGER_DATA
  Start-Sleep 4
}

# 2. "Capture this site" through the native host, exactly as the extension sends it (with a cookie of the site).
$message = Join-Path $work "grab-message.json"
@{ type = "grab"; version = 3; grab = @{ url = $Url; title = $Name; userAgent = "DM trial"; cookies = @(@{ name = "prova"; value = "grab"; domain = ([Uri]$Url).Host; path = "/"; secure = $false; hostOnly = $true }) } } |
  ConvertTo-Json -Depth 5 | Set-Content $message -Encoding utf8
python (Join-Path $PSScriptRoot "host.py") $exe $data "@$message"
Start-Sleep 2

# 3. The wizard, step by step.
$w = "Nuovo progetto grabber"
if ($Mode -eq "Mirror") { Ui $w select "Copia del sito da consultare offline" | Out-Null }
& $shot -Title $w -Out "$shots\grab-$stamp-1.png" -NoFront | Out-Null
Ui $w invoke "Avanti" | Out-Null
Ui $w set "Profondità" ([string]$Depth) | Out-Null
Ui $w set "Richieste al secondo per sito" ($Rate.ToString([Globalization.CultureInfo]::GetCultureInfo("it-IT"))) | Out-Null
Ui $w invoke "Avanti" | Out-Null
Ui $w set "Altre estensioni" $Extensions | Out-Null
& $shot -Title $w -Out "$shots\grab-$stamp-3.png" -NoFront | Out-Null
Ui $w invoke "Avanti" | Out-Null
Ui $w set "Numero massimo di pagine" ([string]$MaxPages) | Out-Null
Ui $w set "Numero massimo di file" ([string]$MaxFiles) | Out-Null
Ui $w invoke "Avanti" | Out-Null
$folder = if ($Mode -eq "Mirror") { Join-Path $work "mirror" } else { Join-Path $work "downloads\grab" }
Ui $w set "Cartella di destinazione" $folder | Out-Null
& $shot -Title $w -Out "$shots\grab-$stamp-5.png" -NoFront | Out-Null
Ui $w invoke "Salva e avvia" | Out-Null

# 4. The run, until it ends (the result line appears in the window).
$run = "Grabber — $Name"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$done = $null
while (-not $done -and (Get-Date) -lt $deadline) {
  Start-Sleep 2
  $done = & $ui -Window $run -Action dump | Where-Object { $_ -match "terminata:" } | Select-Object -First 1
}
& $shot -Title $run -Out "$shots\grab-$stamp-run.png" -NoFront | Out-Null
if (-not $done) { "FAIL run did not end within $TimeoutSeconds s"; exit 1 }
"OK $($done.Trim())"
& $ui -Window $run -Action dump | Where-Object { $_ -match "pagine|selezionati|file ·" } | Select-Object -First 3

# 5. Files: send the ticked files to the queue (added, not started); Mirror: the copy's start page exists.
if ($Mode -eq "Files" -and $Send) {
  Ui $run invoke "Aggiungi alla coda" | Out-Null
  Start-Sleep 1
  Ui "Grabber — $Name" invoke "OK" | Out-Null
  python -I -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); print('downloads in the list:', c.execute('select count(*) from downloads').fetchone()[0])" (Join-Path $data "downloads.db")
} elseif ($Mode -eq "Mirror") {
  $index = Get-ChildItem $folder -Recurse -Filter index.html | Select-Object -First 1
  if ($index) { "OK offline copy: $($index.FullName) ($((Get-ChildItem $folder -Recurse -File).Count) files)" } else { "FAIL no index.html in $folder" }
  Ui $run invoke "Chiudi" | Out-Null
}
"screenshots: $shots\grab-$stamp-*.png"
