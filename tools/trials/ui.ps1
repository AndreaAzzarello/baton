param([string]$Window, [string]$Action, [string]$Name, [string]$Value)
# Minimal UI Automation driver: invoke a button/menu item, set a text box, select an item or dump the tree.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Window)
$win = $null
for ($i = 0; $i -lt 20 -and -not $win; $i++) {
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) {
    # Owned dialogs are children of their owner window.
    $wcond = New-Object System.Windows.Automation.AndCondition($cond, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
    foreach ($top in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
      if ($top.Current.ProcessId -ne 0 -and (Get-Process -Id $top.Current.ProcessId -ErrorAction SilentlyContinue).ProcessName -eq "DownloadManager") {
        $win = $top.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $wcond)
        if ($win) { break }
      }
    }
  }
  if (-not $win) { Start-Sleep -Milliseconds 250 }
}
if (-not $win) {
  # Titles that change (such as "25% Â· file") are matched by substring.
  foreach ($top in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ((Get-Process -Id $top.Current.ProcessId -ErrorAction SilentlyContinue).ProcessName -ne "DownloadManager") { continue }
    foreach ($w in @($top) + @($top.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))) {
      if ($w.Current.Name -like "*$Window*") { $win = $w; break }
    }
    if ($win) { break }
  }
}
if (-not $win) { Write-Output "window '$Window' not found"; exit 1 }

function Find($n, [switch]$Item, [switch]$Button) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $n)
  if ($Button) {
    $c = New-Object System.Windows.Automation.AndCondition($c, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsInvokePatternAvailableProperty, $true)))
  }
  if ($Item) {
    $types = [System.Windows.Automation.ControlType]::DataItem, [System.Windows.Automation.ControlType]::TreeItem, [System.Windows.Automation.ControlType]::ListItem, [System.Windows.Automation.ControlType]::RadioButton, [System.Windows.Automation.ControlType]::ComboBox, [System.Windows.Automation.ControlType]::TabItem
    $or = New-Object System.Windows.Automation.OrCondition(($types | ForEach-Object { New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $_) }))
    $c = New-Object System.Windows.Automation.AndCondition($c, $or)
  }
  $e = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
  if (-not $e) { throw "element '$n' not found in '$Window'" }
  return $e
}

switch ($Action) {
  "invoke" { (Find $Name -Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); "invoked $Name" }
  "set"    { (Find $Name).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value); "set $Name" }
  "get"    { (Find $Name).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
  "select" { (Find $Name -Item).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); "selected $Name" }
  "expand" { (Find $Name -Item).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); "expanded $Name" }
  "toggle" { (Find $Name).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); "toggled $Name" }
  "focus"  { (Find $Name -Item).SetFocus(); "focused $Name" }
  "dump"   {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) { $c = $e.Current; if ($c.Name) { "{0,-14} {1}" -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name } }
  }
}

