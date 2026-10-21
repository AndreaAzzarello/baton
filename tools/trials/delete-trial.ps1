<#
.SYNOPSIS
  Deleting from the disk, for real but isolated: 20 downloads in %TEMP%\dm-trials\delete (the finished ones with their
  files), 5 finished ones selected through UI Automation, "Elimina dal disco" from the toolbar, the confirmation answered,
  then the 5 files must be gone from the folder and be in the Windows Recycle Bin, the others untouched.
  At the end the 5 files are restored from the Recycle Bin into the trial folder and the folder is removed, so the user's
  Recycle Bin is left as it was. UI Automation only: no mouse or keyboard on the desktop.
#>
param([string]$Exe)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $Exe) { $Exe = Join-Path $root "src\Baton.App\bin\Debug\net10.0-windows10.0.19041.0\Baton.exe" }
$work = Join-Path $env:TEMP "dm-trials\delete"
if (Test-Path $work) { Remove-Item $work -Recurse -Force } # the trial's own folder
$data = Join-Path $work "data"; $files = Join-Path $work "files"
New-Item -ItemType Directory -Force $data, $files | Out-Null
$env:BATON_DATA = $data
$p = Start-Process $Exe -ArgumentList "--tray" -PassThru; Start-Sleep 5; Stop-Process -Id $p.Id -Force; Start-Sleep 1
$py = Join-Path $work "setup.py"
Set-Content $py -Encoding ASCII @'
import sqlite3, sys, os, datetime
db, folder = sys.argv[1], sys.argv[2]
c = sqlite3.connect(db)
queue = c.execute("select id from queues order by id limit 1").fetchone()[0]
now = datetime.datetime.now(datetime.timezone.utc).isoformat()
for i in range(20):
    name = f"trial-{i:02}.bin"
    status = 3 if i % 2 == 0 else 2  # even: completed, with a file; odd: paused
    size = 1000 * (i + 1)
    c.execute("""insert into downloads(url, directory, file_name, total_size, supports_ranges, status, sort_order, added_utc, completed_utc, queue_id)
                 values (?,?,?,?,1,?,?,?,?,?)""", (f"http://127.0.0.1:9/{name}", folder, name, size, status, i, now, now if status == 3 else None, queue))
    if status == 3:
        open(os.path.join(folder, name), "wb").write(b"t" * size)
c.execute("insert or replace into settings(key, value) values('ToolbarItems', 'add,|,start,pause,remove,deletefiles,|,queue,~,settings')")
c.execute("insert or replace into settings(key, value) values('DownloadFolder', ?)", (folder,))
c.commit()
'@
python -I $py (Join-Path $data "downloads.db") $files
$app = Start-Process $Exe -PassThru
try {
  $pc = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$app.Id)
  $grid = $null; $win = $null
  for ($i = 0; $i -lt 100 -and -not $grid; $i++) {
    $win = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $pc)
    if ($win) { $grid = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataGrid))) }
    if (-not $grid) { Start-Sleep -Milliseconds 100 }
  }
  Start-Sleep 2
  $items = @($grid.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))))
  "rows realized: $($items.Count) of 20"
  # Five finished downloads (even numbers) among the rows the list has realized (it virtualizes the rest).
  $chosen = @($items | ForEach-Object { ($_.Current.Name -split ',')[0] } | Where-Object { $_ -match '^trial-\d\d\.bin$' -and [int]$_.Substring(6, 2) % 2 -eq 0 } | Select-Object -First 5)
  "chosen: $($chosen -join ', ')"
  $first = $true
  foreach ($name in $chosen) {
    $row = $items | Where-Object { $_.Current.Name -like "$name*" } | Select-Object -First 1
    $pattern = $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    if ($first) { $pattern.Select(); $first = $false } else { $pattern.AddToSelection() }
  }
  $selection = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like "5 selezionati*" } | ForEach-Object { $_.Current.Name }
  "status bar: $selection"
  $button = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, "Elimina dal disco")))
  $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep 1
  & "$PSScriptRoot\ui.ps1" -Window "Elimina download" -Action dump
  & "$PSScriptRoot\ui.ps1" -Window "Elimina download" -Action invoke -Name "OK" | Out-Null
  Start-Sleep 3
  $left = @(Get-ChildItem $files -Name)
  "files left in the folder: $($left.Count) (expected 5 of the 10 finished)"
  $gone = $chosen | Where-Object { $left -contains $_ }
  "chosen still in the folder: $(@($gone).Count)"
  $shell = New-Object -ComObject Shell.Application
  $bin = $shell.Namespace(10)
  $inBin = @($bin.Items() | Where-Object { $chosen -contains $_.Name -and "$($bin.GetDetailsOf($_, 1))" -eq $files })
  "in the Recycle Bin, from the trial folder: $($inBin.Count) -> $(($inBin | ForEach-Object Name) -join ', ')"
  $rows = @($grid.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))))
  "rows in the list now: $($rows.Count) realized (15 expected in all)"
} finally {
  Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
  Remove-Item Env:BATON_DATA
  # Leave the user's Recycle Bin as it was: the trial's files go back to the trial folder, which is then removed.
  $shell = New-Object -ComObject Shell.Application
  $bin = $shell.Namespace(10)
  foreach ($item in @($bin.Items() | Where-Object { $chosen -contains $_.Name -and "$($bin.GetDetailsOf($_, 1))" -eq $files })) { $item.InvokeVerb("undelete") }
  Start-Sleep 1
  $still = @($bin.Items() | Where-Object { $chosen -contains $_.Name -and "$($bin.GetDetailsOf($_, 1))" -eq $files }).Count
  "trial files still in the Recycle Bin after restoring: $still"
  Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
