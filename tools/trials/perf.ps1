param([string]$Exe, [string]$Data, [switch]$Tray, [int]$IdleSeconds = 20)
# Measures the isolated app with a large list (see fill_downloads.py): start time, memory, and through UI Automation
# scrolling, sorting and searching the list. With -Tray it only measures the memory of the app idle in the tray.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$env:DOWNLOADMANAGER_DATA = $Data
$watch = [Diagnostics.Stopwatch]::StartNew()
$start = @{ FilePath = $Exe; PassThru = $true }
if ($Tray) { $start.ArgumentList = '--tray' }
$p = Start-Process @start
if (-not $p) { throw "the app did not start" }
function Mem($label) {
  $p.Refresh()
  "{0,-28} working set {1,6:N1} MB | private {2,6:N1} MB" -f $label, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB)
}
if ($Tray) {
  Start-Sleep -Seconds $IdleSeconds
  Mem "tray, idle $IdleSeconds s"
  return
}
$pc = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$p.Id)
$win = $null
while (-not $win -and $watch.Elapsed.TotalSeconds -lt 60) {
  $win = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $pc)
  if (-not $win) { Start-Sleep -Milliseconds 50 }
}
$grid = $null
while (-not $grid -and $watch.Elapsed.TotalSeconds -lt 60) {
  if ($win) { $grid = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataGrid))) }
  if (-not $grid) { Start-Sleep -Milliseconds 50 }
}
if (-not $grid) { throw "list not found" }
"window and list ready         {0,6:N0} ms" -f $watch.ElapsedMilliseconds
Start-Sleep -Seconds 3
Mem "after start"
$rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))).Count
"rows realized (of the list)   $rows"
$scroll = $grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
$t = [Diagnostics.Stopwatch]::StartNew()
foreach ($pos in 10, 25, 50, 75, 100, 60, 30, 0) { $scroll.SetScrollPercent(-1, $pos) }
"8 jumps across the list       {0,6:N0} ms" -f $t.ElapsedMilliseconds
$t.Restart()
for ($i = 0; $i -lt 40; $i++) { $scroll.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeIncrement) }
"40 pages down                 {0,6:N0} ms" -f $t.ElapsedMilliseconds
$header = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition(
  (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::HeaderItem)),
  (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, "Dimensione")))))
$t.Restart()
$header.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$header.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
"sort twice by size            {0,6:N0} ms" -f $t.ElapsedMilliseconds
$search = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition(
  (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)),
  (New-Object System.Windows.Automation.PropertyCondition($A::IsValuePatternAvailableProperty, $true)))))
$t.Restart()
$search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("perf-019")
Start-Sleep -Milliseconds 600
$found = $grid.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))).Count
"search 'perf-019' (incl. 600 ms debounce) {0,6:N0} ms, rows shown $found" -f $t.ElapsedMilliseconds
$search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("")
Start-Sleep -Seconds 2
Mem "after scroll, sort, search"
"pid $($p.Id)"
