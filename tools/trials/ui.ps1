param([string]$Window, [string]$Action, [string]$Name, [string]$Value, [int]$ProcessId)
# Minimal UI Automation driver: invoke a button/menu item, set a text box, select an item or dump the tree.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
# Only the trial app's windows: with -ProcessId that process, without it the only Baton process there is. With several Baton processes
# (the real app in the notification area is one) a window of the real app could be driven, so it refuses instead of guessing.
$batonNames = "Baton", "DownloadManager"
$batonProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in $batonNames })
if ($ProcessId) {
  if (-not ($batonProcesses | Where-Object { $_.Id -eq $ProcessId })) { Write-Output "refusing: process $ProcessId is not a Baton process"; exit 2 }
} elseif ($batonProcesses.Count -gt 1) {
  Write-Output "refusing: $($batonProcesses.Count) Baton processes are running (the real app may be one); pass -ProcessId <pid of the trial app>"; exit 2
}
function Mine($id) { if ($ProcessId) { $id -eq $ProcessId } else { (Get-Process -Id $id -ErrorAction SilentlyContinue).ProcessName -in $batonNames } }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Window)
$win = $null
for ($i = 0; $i -lt 20 -and -not $win; $i++) {
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) {
    # Owned dialogs are children of their owner window.
    $wcond = New-Object System.Windows.Automation.AndCondition($cond, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
    foreach ($top in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
      $owner = $top.Current.ProcessId
      if ($owner -and (Mine $owner)) {
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
    if (-not (Mine $top.Current.ProcessId)) { continue }
    foreach ($w in @($top) + @($top.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))) {
      if ($w.Current.Name -like "*$Window*") { $win = $w; break }
    }
    if ($win) { break }
  }
}
if (-not $win) { Write-Output "window '$Window' not found"; exit 1 }

function Find($n, [switch]$Item, [switch]$Button, [switch]$Value, [switch]$Toggle) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $n)
  if ($Button) {
    $c = New-Object System.Windows.Automation.AndCondition($c, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsInvokePatternAvailableProperty, $true)))
  }
  # A label and its text box often share the name: only the element that can take the action.
  if ($Value) {
    $c = New-Object System.Windows.Automation.AndCondition($c, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsValuePatternAvailableProperty, $true)))
  }
  if ($Toggle) {
    $c = New-Object System.Windows.Automation.AndCondition($c, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsTogglePatternAvailableProperty, $true)))
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
  # Win32 buttons without UI Automation's Invoke (Inno Setup's wizard): BM_CLICK to that control, no input on the desktop.
  "click"  {
    Add-Type -Namespace U -Name M -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);'
    [U.M]::PostMessage([IntPtr](Find $Name).Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; "clicked $Name"
  }
  "set"    { (Find $Name -Value).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value); "set $Name" }
  "get"    { (Find $Name -Value).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
  "select" { (Find $Name -Item).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); "selected $Name" }
  "expand" { (Find $Name -Item).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); "expanded $Name" }
  "toggle" { (Find $Name -Toggle).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); "toggled $Name" }
  "focus"  { (Find $Name -Item).SetFocus(); "focused $Name" }
  "dump"   {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) { $c = $e.Current; if ($c.Name) { "{0,-14} {1}" -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name } }
  }
  # As dump, with what a check needs: text box values, check boxes on/off, disabled controls.
  "state"  {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
      $c = $e.Current
      $extra = ''
      $p = $null
      if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { $extra += " = $($p.Current.Value)" }
      if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $extra += " [$($p.Current.ToggleState)]" }
      if (-not $c.IsEnabled) { $extra += ' [disabled]' }
      if (-not $c.Name -and -not $extra) { continue }
      "{0,-14} {1}{2}" -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name, $extra
    }
  }
}

