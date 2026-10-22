<#
.SYNOPSIS
  The README's screenshots of the main window, in English, light and dark: the isolated app on a copy of the test data
  (-Data, file names given in English), three rows selected through UI Automation, the window captured by process.
.EXAMPLE
  powershell -File tools\trials\readme-shots.ps1 -Exe publish\Baton.exe -Data %TEMP%\dm-trials\readme-data-baton -Out docs\screenshots
#>
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Data, [Parameter(Mandatory)][string]$Out, [string]$Language = 'English')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$work = Join-Path $env:TEMP 'dm-trials\readme-shots'
foreach ($theme in 'Light', 'Dark') {
  if (Test-Path $work) { Remove-Item $work -Recurse -Force }
  New-Item -ItemType Directory $work | Out-Null
  python -I -c "import sqlite3,sys; s=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro', uri=True); d=sqlite3.connect(sys.argv[2]); s.backup(d); d.close()" (Join-Path $Data 'downloads.db') "$work\downloads.db"
  $py = Join-Path $work 'prep.py'
  Set-Content $py -Encoding UTF8 @'
import sqlite3, sys
names = ["setup-editor-3.2-x64.msi", "annual-report-2025.pdf", "open-movie-1080p.mp4", "podcast-episode-42.mp3",
         "weather-dataset-2026.zip", "portable-tools.7z", "user-manual.epub", "trailer-720p.webm", "4k-wallpapers.zip",
         "release-notes.txt"]
db = sqlite3.connect(sys.argv[1])
for (rowid,), name in zip(db.execute("SELECT id FROM downloads ORDER BY sort_order").fetchall(), names):
    db.execute("UPDATE downloads SET file_name = ?, url = ? WHERE id = ?", (name, "https://downloads.example.com/" + name, rowid))
for key, value in (("Language", sys.argv[2]), ("Theme", sys.argv[3]), ("UpdateCheckEnabled", "0"), ("DesktopRelocationAsked", "1")):
    db.execute("INSERT INTO settings(key, value) VALUES(?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", (key, value))
db.commit()
'@
  python -I $py "$work\downloads.db" $Language $theme
  $env:BATON_DATA = $work
  $app = Start-Process $Exe -PassThru
  try {
    $pc = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$app.Id)
    $grid = $null
    for ($i = 0; $i -lt 100 -and -not $grid; $i++) {
      $win = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $pc)
      if ($win) { $grid = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataGrid))) }
      if (-not $grid) { Start-Sleep -Milliseconds 100 }
    }
    Start-Sleep 2
    $rows = @($grid.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))))
    $rows[2].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $rows[4].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
    $rows[8].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
    Start-Sleep 2
    & "$PSScriptRoot\shot.ps1" -Title 'Baton' -Out (Join-Path $Out "main-$($theme.ToLowerInvariant()).png") -NoFront -ProcessId $app.Id
  } finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; Wait-Process -Id $app.Id -ErrorAction SilentlyContinue; Start-Sleep 2
    Remove-Item Env:BATON_DATA
  }
}
