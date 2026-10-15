<#
.SYNOPSIS
  Ends a real-browser trial. Without -Apply it only REPORTS what is left; with -Apply it stops the trial processes
  and removes the traces the portable Firefox leaves in the user's profile.
.NOTES
  Trial app instances are the publish\DownloadManager.exe processes started with --background after the trial began
  (by the native host of a trial browser). The real app in the tray (--tray or no arguments) is never touched.
#>
param([switch]$Apply)
$work = if ($env:DM_TRIALS) { $env:DM_TRIALS } else { Join-Path $env:TEMP "dm-trials" }
$since = if (Test-Path "$work\started.txt") { [datetime](Get-Content "$work\started.txt") } else { (Get-Date).AddHours(-12) }
# Without the drive letter: some entries spell it \Device\HarddiskVolumeN\... instead of C:\...
$needle = [regex]::Escape((Split-Path $work -NoQualifier))

$apps = Get-CimInstance Win32_Process -Filter "Name='DownloadManager.exe'" |
  Where-Object { ($_.CommandLine -like "*--background*" -and $_.CreationDate -gt $since) -or $_.CommandLine -like "*\bin\Debug\*" }
$site = Get-CimInstance Win32_Process -Filter "Name='python.exe'" | Where-Object { $_.CommandLine -like "*trials*site*server.py*" }
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$autostart = (Get-ItemProperty $run).PSObject.Properties | Where-Object { $_.Name -like "Mozilla-Firefox-*" -and "$($_.Value)" -match $needle }
$mozilla = "HKCU:\Software\Mozilla\Firefox"
$mozValues = @()
if (Test-Path $mozilla) {
  foreach ($key in @(Get-Item $mozilla) + @(Get-ChildItem $mozilla -Recurse)) {
    foreach ($name in $key.GetValueNames()) { if ($name -match $needle -or "$($key.GetValue($name))" -match $needle) { $mozValues += [pscustomobject]@{ Key = $key.PSPath; Name = $name } } }
  }
}
$mui = "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"
$muiValues = (Get-Item $mui).GetValueNames() | Where-Object { $_ -match $needle }
$audio = "HKCU:\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore"
$audioKeys = @(Get-ChildItem $audio -ErrorAction SilentlyContinue | Where-Object { "$($_.GetValue(''))" -match $needle })
# Explorer remembers every program that showed a tray icon: entries of debug builds or trial copies are trial traces.
$trayIcons = "HKCU:\Control Panel\NotifyIconSettings"
$trayKeys = @(Get-ChildItem $trayIcons -ErrorAction SilentlyContinue | Where-Object {
  $path = "$($_.GetValue('ExecutablePath'))"; $path -like "*\bin\Debug\*DownloadManager.exe" -or $path -match $needle })

"trial apps: $(@($apps).Count) | test site: $(@($site).Count) | Firefox autostart: $(@($autostart).Count) | Firefox registry values: $($mozValues.Count) | shell cache: $(@($muiValues).Count) | audio: $($audioKeys.Count) | tray icon cache: $($trayKeys.Count)"
if (-not $Apply) { "(report only; run with -Apply to clean up)"; return }

$apps | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; "stopped app $($_.ProcessId)" }
$site | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; "stopped site $($_.ProcessId)" }
$autostart | ForEach-Object { Remove-ItemProperty $run -Name $_.Name; "removed autostart $($_.Name)" }
foreach ($v in $mozValues) { Remove-ItemProperty -LiteralPath $v.Key -Name $v.Name }
foreach ($n in $muiValues) { Remove-ItemProperty -LiteralPath $mui -Name $n }
foreach ($k in $audioKeys) { Remove-Item -LiteralPath $k.PSPath -Recurse }
foreach ($k in $trayKeys) { Remove-Item -LiteralPath $k.PSPath -Recurse }
# Keys the portable Firefox created and that are now empty (the user has no installed Firefox of their own).
if (Test-Path $mozilla) {
  Get-ChildItem $mozilla -Recurse | Sort-Object { $_.Name.Length } -Descending |
    Where-Object { $_.ValueCount -eq 0 -and $_.SubKeyCount -eq 0 } | ForEach-Object { Remove-Item -LiteralPath $_.PSPath }
  $top = Get-Item $mozilla
  if ($top.ValueCount -eq 0 -and $top.SubKeyCount -eq 0) { Remove-Item $mozilla }
}
Remove-Item "$work\started.txt" -ErrorAction SilentlyContinue   # the trial is over
"cleaned"
