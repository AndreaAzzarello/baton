# Checks the project hooks with synthetic inputs: every guard case must pass or be blocked as expected, and each
# hook must answer quickly. Run: powershell -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\test-hooks.ps1
$root = Split-Path (Split-Path $PSScriptRoot)
$failures = 0

function Invoke-Hook([string]$script, [string]$json) {
  $psi = New-Object Diagnostics.ProcessStartInfo "powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\$script`""
  $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
  $watch = [Diagnostics.Stopwatch]::StartNew()
  $p = [Diagnostics.Process]::Start($psi)
  $writer = New-Object IO.StreamWriter($p.StandardInput.BaseStream, (New-Object Text.UTF8Encoding $false))
  $writer.Write($json); $writer.Close()
  $out = $p.StandardOutput.ReadToEnd(); $err = $p.StandardError.ReadToEnd()
  $p.WaitForExit()
  [pscustomobject]@{ Code = $p.ExitCode; Out = $out.Trim(); Err = $err.Trim(); Ms = $watch.ElapsedMilliseconds }
}

function Bash([string]$command) { @{ tool_name = "Bash"; tool_input = @{ command = $command } } | ConvertTo-Json -Compress }

$guardCases = @(
  @{ Expect = 0; Command = 'reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run"' },
  @{ Expect = 0; Command = 'Get-ItemProperty "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.andreaazzarello.baton"' },
  @{ Expect = 0; Command = '$env:BATON_DATA = "$env:TEMP\dm-trials\data"; Start-Process publish\Baton.exe --tray' },
  @{ Expect = 0; Command = '"C:\Program Files\dotnet\dotnet.exe" test Baton.slnx' },
  @{ Expect = 0; Command = 'Remove-Item C:\Users\user\Desktop\DownloadManager\dist -Recurse -Force' },
  @{ Expect = 0; Command = 'rm -rf dist' },
  @{ Expect = 2; Command = 'reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Baton /d x' },
  @{ Expect = 2; Command = 'reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v DownloadManager /d x' },
  @{ Expect = 2; Command = 'Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name DownloadManager' },
  @{ Expect = 2; Command = 'reg delete HKCU\Software\Google\Chrome\NativeMessagingHosts\com.downloadmanager.host /f' },
  @{ Expect = 2; Command = 'reg add HKCU\Software\Google\Chrome\NativeMessagingHosts\com.andreaazzarello.baton /ve /d x /f' },
  @{ Expect = 2; Command = 'Remove-Item C:\Users\user\Desktop\hello-2.12.tar.gz' },
  @{ Expect = 2; Command = 'rm "$HOME/Desktop/explicit.txt"' },
  @{ Expect = 2; Command = '[IO.File]::Delete([Environment]::GetFolderPath("Desktop") + "\implicit.txt")' }
)
foreach ($case in $guardCases) {
  $r = Invoke-Hook "guard.ps1" (Bash $case.Command)
  $ok = $r.Code -eq $case.Expect -and $r.Ms -lt 2000
  if (-not $ok) { $failures++ }
  "{0} guard {1} ({2} ms) {3}{4}" -f ($(if ($ok) { "OK  " } else { "FAIL" })), ($(if ($case.Expect -eq 2) { "blocca" } else { "passa " })), $r.Ms, $case.Command,
    $(if ($r.Code -eq 2) { "`n       -> " + $r.Err.Substring(0, [Math]::Min(140, $r.Err.Length)) } else { "" })
}

# Fail-closed: a request the guard cannot read is blocked, not let through.
$garbled = Invoke-Hook "guard.ps1" "{ not json"
$okGarbled = $garbled.Code -eq 2
"{0} guard blocca richiesta illeggibile ({1} ms)" -f ($(if ($okGarbled) { "OK  " } else { "FAIL" })), $garbled.Ms
if (-not $okGarbled) { $failures++ }

# extension-check: a valid change passes; a syntax error in a copy inside extension/ is reported (then removed).
$valid = Invoke-Hook "extension-check.ps1" (@{ tool_name = "Edit"; tool_input = @{ file_path = "$root\extension\rules.js" } } | ConvertTo-Json -Compress)
$okValid = $valid.Code -eq 0 -and $valid.Ms -lt 2000
"{0} extension-check passa  ({1} ms) extension\rules.js" -f ($(if ($okValid) { "OK  " } else { "FAIL" })), $valid.Ms
$broken = "$root\extension\zz-hook-test.js"
Set-Content $broken "export const x = ;" -Encoding UTF8
try {
  $bad = Invoke-Hook "extension-check.ps1" (@{ tool_name = "Write"; tool_input = @{ file_path = $broken } } | ConvertTo-Json -Compress)
} finally { Remove-Item $broken -Force }   # the test's own temporary file, inside the project
$okBad = $bad.Code -eq 2 -and $bad.Err -match 'node --check' -and $bad.Ms -lt 2000
"{0} extension-check blocca ({1} ms) errore di sintassi -> {2}" -f ($(if ($okBad) { "OK  " } else { "FAIL" })), $bad.Ms, ($bad.Err -split "`n")[0]
$other = Invoke-Hook "extension-check.ps1" (@{ tool_name = "Edit"; tool_input = @{ file_path = "$root\src\Baton.Core\Models.cs" } } | ConvertTo-Json -Compress)
$okOther = $other.Code -eq 0 -and $other.Ms -lt 1500
"{0} extension-check ignora ({1} ms) file C#" -f ($(if ($okOther) { "OK  " } else { "FAIL" })), $other.Ms
if (-not ($okValid -and $okBad -and $okOther)) { $failures++ }

"`n$(if ($failures) { "$failures caso/i FALLITO/I" } else { 'tutti i casi superati' })"
exit $failures
