<#
.SYNOPSIS
  Selection in the main list of the isolated app, through UI Automation only (no mouse or keyboard on the desktop).
.DESCRIPTION
  Starts the app on %TEMP%\dm-trials\selection (20 downloads: paused, completed, failed, never started by themselves),
  selects rows 2, 4 and 6 and reads the selection back right away and after a few list updates (every 500 ms), together
  with the rows' automation ids: a selection that empties, or rows that are recreated, mean the list is rebuilt under
  the user's hands. Stops the app it started.
#>
param([string]$Exe, [int]$Seconds = 3, [string]$Shot)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $Exe) { $Exe = Join-Path $root "src\Baton.App\bin\Debug\net10.0-windows10.0.19041.0\Baton.exe" }
$data = Join-Path $env:TEMP "dm-trials\selection"
New-Item -ItemType Directory -Force $data | Out-Null
$env:BATON_DATA = $data
if (-not (Test-Path "$data\downloads.db")) {
  $p = Start-Process $Exe -ArgumentList "--tray" -PassThru; Start-Sleep 5; Stop-Process -Id $p.Id -Force; Start-Sleep 1
  python -I "$PSScriptRoot\prep_data.py" "$data\downloads.db" (Join-Path $env:TEMP "dm-trials\downloads") | Out-Null
  python -I "$PSScriptRoot\fill_downloads.py" "$data\downloads.db" (Join-Path $env:TEMP "dm-trials\downloads") 20 | Out-Null
}
$app = Start-Process $Exe -PassThru
try {
  $pc = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$app.Id)
  $grid = $null
  for ($i = 0; $i -lt 100 -and -not $grid; $i++) {
    $win = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $pc)
    if ($win) { $grid = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataGrid))) }
    if (-not $grid) { Start-Sleep -Milliseconds 100 }
  }
  if (-not $grid) { throw "list not found" }
  Start-Sleep 2
  $itemCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
  function Rows { @($grid.FindAll([System.Windows.Automation.TreeScope]::Children, $itemCond)) }
  function Selected { $sel = $grid.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection(); @($sel | ForEach-Object { $_.Current.Name }) }
  $rows = Rows
  "rows: $($rows.Count)"
  $rows[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $rows[3].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
  $rows[5].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
  $ids = ($rows | Select-Object -First 6 | ForEach-Object { ($_.GetRuntimeId() -join ".") }) -join " "
  "selected now: $((Selected) -join ', ')"
  Start-Sleep -Seconds $Seconds
  "selected after $Seconds s: $((Selected) -join ', ')"
  if ($Shot) { & "$PSScriptRoot\shot.ps1" -Title "Baton" -Out $Shot -NoFront }
  $ids2 = ((Rows) | Select-Object -First 6 | ForEach-Object { ($_.GetRuntimeId() -join ".") }) -join " "
  "rows recreated: $($ids -ne $ids2)"
} finally {
  Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
  Remove-Item Env:BATON_DATA
}
