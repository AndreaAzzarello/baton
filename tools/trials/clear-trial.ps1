<#
.SYNOPSIS
  "Clear completed" for real but isolated, in %TEMP%\dm-trials\clear: 20 downloads (10 completed with their files,
  10 paused), the toolbar button invoked through UI Automation, the confirmation answered. Two runs:
  without the Recycle Bin (the 10 completed leave the list, every file stays) and with "Also move the files to the
  Recycle Bin" ticked (the 10 files go to the Windows Recycle Bin). At the end the files are restored from the Recycle Bin
  into the trial folder and the folder is removed, so the user's Recycle Bin is left as it was.
  UI Automation only: no mouse or keyboard on the desktop.
.PARAMETER Exe
  Default publish\Baton.exe.
#>
param([string]$Exe)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $Exe) { $Exe = Join-Path $root "publish\Baton.exe" }
$work = Join-Path $env:TEMP "dm-trials\clear"
$files = Join-Path $work "files"
$names = 0..19 | Where-Object { $_ % 2 -eq 0 } | ForEach-Object { "clear-{0:00}.bin" -f $_ }
$results = @()
function Report([string]$step, [bool]$ok, [string]$detail) {
  $script:results += $ok
  "{0} {1} — {2}" -f ($(if ($ok) { "OK  " } else { "FAIL" })), $step, $detail
}

function Run([bool]$recycle) {
  if (Test-Path $work) { Remove-Item $work -Recurse -Force } # the trial's own folder
  $data = Join-Path $work "data"
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
    name = f"clear-{i:02}.bin"
    status = 3 if i % 2 == 0 else 2  # even: completed, with a file; odd: paused
    c.execute("""insert into downloads(url, directory, file_name, total_size, supports_ranges, status, sort_order, added_utc, completed_utc, queue_id)
                 values (?,?,?,?,1,?,?,?,?,?)""", (f"http://127.0.0.1:9/{name}", folder, name, 1000, status, i, now, now if status == 3 else None, queue))
    if status == 3:
        open(os.path.join(folder, name), "wb").write(b"t" * 1000)
c.execute("insert or replace into settings(key, value) values('ToolbarItems', 'add,|,start,pause,remove,clearcompleted,|,queue,~,settings')")
c.execute("insert or replace into settings(key, value) values('DownloadFolder', ?)", (folder,))
c.commit()
'@
  python -I $py (Join-Path $data "downloads.db") $files
  $app = Start-Process $Exe -PassThru
  try {
    $pc = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$app.Id)
    $win = $null; $grid = $null
    for ($i = 0; $i -lt 100 -and -not $grid; $i++) {
      $win = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $pc)
      if ($win) { $grid = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataGrid))) }
      if (-not $grid) { Start-Sleep -Milliseconds 100 }
    }
    Start-Sleep 2
    $button = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, "Rimuovi completati")))
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 1
    if ($recycle) { & "$PSScriptRoot\ui.ps1" -Window "Rimuovi i completati" -Action toggle -Name "Sposta anche i file nel Cestino" | Out-Null; Start-Sleep -Milliseconds 300 }
    $shot = Join-Path $env:TEMP "dm-trials\shots\clear-$(if ($recycle) { 'recycle' } else { 'keep' }).png"
    & "$PSScriptRoot\shot.ps1" -Title "Rimuovi i completati" -Out $shot -NoFront | Out-Null
    $summary = (& "$PSScriptRoot\ui.ps1" -Window "Rimuovi i completati" -Action dump) -match "verranno rimossi" | Select-Object -First 1
    & "$PSScriptRoot\ui.ps1" -Window "Rimuovi i completati" -Action invoke -Name "Rimuovi" | Out-Null
    Start-Sleep 3
    $rows = @($grid.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))))
    $leftNames = @($rows | ForEach-Object { ($_.Current.Name -split ',')[0] })
    $left = @(Get-ChildItem $files -Name)
    $label = if ($recycle) { "with the Recycle Bin" } else { "files kept" }
    Report "$label`: summary" ($summary -match "10 download") "$summary"
    Report "$label`: only the paused stay in the list" ($rows.Count -eq 10 -and -not ($leftNames | Where-Object { $names -contains $_ })) "$($rows.Count) rows: $($leftNames -join ', ')"
    if ($recycle) {
      $bin = (New-Object -ComObject Shell.Application).Namespace(10)
      $inBin = @($bin.Items() | Where-Object { $names -contains $_.Name -and "$($bin.GetDetailsOf($_, 1))" -eq $files })
      Report "$label`: files in the Recycle Bin" ($left.Count -eq 0 -and $inBin.Count -eq 10) "$($left.Count) left in the folder, $($inBin.Count) in the Recycle Bin"
    } else {
      Report "$label`: files still on the disk" ($left.Count -eq 10) "$($left.Count) of 10 in the folder"
    }
    Report "$label`: screenshot" (Test-Path $shot) $shot
  } finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    $app.WaitForExit(10000) | Out-Null # the next run removes its database
    Start-Sleep 1
    Remove-Item Env:BATON_DATA
  }
}

try {
  Run $false
  Run $true
} finally {
  # Leave the user's Recycle Bin as it was: the trial's files go back to the trial folder, which is then removed.
  $bin = (New-Object -ComObject Shell.Application).Namespace(10)
  foreach ($item in @($bin.Items() | Where-Object { $names -contains $_.Name -and "$($bin.GetDetailsOf($_, 1))" -eq $files })) { $item.InvokeVerb("undelete") }
  Start-Sleep 1
  $still = @($bin.Items() | Where-Object { $names -contains $_.Name -and "$($bin.GetDetailsOf($_, 1))" -eq $files }).Count
  Report "Recycle Bin restored" ($still -eq 0) "$still trial files still in the Recycle Bin"
  Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
"{0} of {1} OK" -f @($results | Where-Object { $_ }).Count, $results.Count
