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
  @{ Expect = 0; Command = "Remove-Item $root\dist -Recurse -Force" },
  @{ Expect = 0; Command = "rm -rf '$($root -replace '\\', '/')/dist'" },
  @{ Expect = 0; Command = "rm -rf /$($root.Substring(0, 1).ToLower())$($root.Substring(2) -replace '\\', '/')/dist" },
  @{ Expect = 2; Command = "Remove-Item $root\..\other -Recurse -Force" },
  @{ Expect = 2; Command = "Remove-Item $root\src\..\..\other -Recurse -Force" },
  @{ Expect = 2; Command = "Remove-Item $root.bak -Recurse -Force" },
  @{ Expect = 2; Command = "Remove-Item ${root}2 -Recurse -Force" },
  @{ Expect = 2; Command = 'Remove-Item C:\Users\user\Desktop\DownloadManager\dist -Recurse -Force' },
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
$guardCases += @(
  @{ Expect = 0; Command = "cd '$root'; graphify update ." },
  @{ Expect = 0; Command = "cd $($root -replace '\\', '/') && graphify update ." },
  @{ Expect = 0; Command = 'git commit -m "docs: the Desktop folder moved"; rm -f notes.tmp' },
  @{ Expect = 0; Command = "Remove-Item `$env:TEMP\x -Recurse; Write-Output 'sul Desktop dell''utente'" },
  @{ Expect = 2; Command = 'rm -rf C:/Users/user/Desktop/other' },
  @{ Expect = 2; Command = 'cd Desktop; Remove-Item old.txt' },
  @{ Expect = 2; Command = 'Remove-Item "$env:USERPROFILE\Desktop\x.txt"' },
  @{ Expect = 2; Command = 'rm -rf ~/Desktop/other' },
  @{ Expect = 2; Command = 'del %USERPROFILE%\Desktop\x.txt' }
)
# Spellings relative to the home folder only make sense when the project sits under it.
if ($env:USERPROFILE -and $root.StartsWith($env:USERPROFILE.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
  $rel = $root.Substring($env:USERPROFILE.TrimEnd('\').Length + 1)
  $relSlash = $rel -replace '\\', '/'
  $guardCases += @(
    @{ Expect = 0; Command = "cd ~/$relSlash && graphify update ." },
    @{ Expect = 0; Command = "cd ~/$relSlash && rm -rf graphify-out/cache" },
    @{ Expect = 0; Command = "cd `"`$HOME/$relSlash`" && rm -rf dist" },
    @{ Expect = 0; Command = "Remove-Item `$env:USERPROFILE\$rel\dist -Recurse -Force" },
    @{ Expect = 0; Command = "del %USERPROFILE%\$rel\dist\x.txt" },
    @{ Expect = 2; Command = "rm -rf ~/$relSlash/../other" },
    @{ Expect = 2; Command = "Remove-Item `$env:USERPROFILE\$rel.bak -Recurse" }
  )
}
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

# guard: ADR files go to the vault, not docs/adr/.
foreach ($adr in @(@{ Path = "$root\docs\adr\0001-x.md"; Expect = 2 }, @{ Path = "$root\docs\API.md"; Expect = 0 })) {
  $r = Invoke-Hook "guard.ps1" (@{ tool_name = "Write"; tool_input = @{ file_path = $adr.Path } } | ConvertTo-Json -Compress)
  $ok = $r.Code -eq $adr.Expect
  if (-not $ok) { $failures++ }
  "{0} guard {1} Write {2}" -f ($(if ($ok) { "OK  " } else { "FAIL" })), ($(if ($adr.Expect -eq 2) { "blocca" } else { "passa " })), $adr.Path
}

# lf.ps1: CRLF text goes back to LF (BOM kept), a binary file and a file outside the project stay untouched.
$tmp = Join-Path $root "zz-lf-test"
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
  $text = Join-Path $tmp "a.cs"; $png = Join-Path $tmp "a.png"
  $outside = Join-Path $env:TEMP "zz-lf-outside.txt"
  [IO.File]::WriteAllBytes($text, [byte[]](0xEF, 0xBB, 0xBF, 0x61, 0x0D, 0x0A, 0x62, 0x0D, 0x0A))
  [IO.File]::WriteAllBytes($png, [byte[]](0x89, 0x50, 0x0D, 0x0A, 0x00, 0x0D, 0x0A))
  [IO.File]::WriteAllBytes($outside, [byte[]](0x61, 0x0D, 0x0A))
  foreach ($f in $text, $png, $outside) { Invoke-Hook "lf.ps1" (@{ tool_name = "Write"; tool_input = @{ file_path = $f } } | ConvertTo-Json -Compress) | Out-Null }
  $okText = ([BitConverter]::ToString([IO.File]::ReadAllBytes($text))) -eq "EF-BB-BF-61-0A-62-0A"
  $okPng = ([BitConverter]::ToString([IO.File]::ReadAllBytes($png))) -eq "89-50-0D-0A-00-0D-0A"
  $okOutside = ([BitConverter]::ToString([IO.File]::ReadAllBytes($outside))) -eq "61-0D-0A"
  "{0} lf.ps1 CRLF -> LF con BOM conservato; {1} binario intatto; {2} file fuori dal progetto intatto" -f ($(if ($okText) { "OK  " } else { "FAIL" })), ($(if ($okPng) { "OK" } else { "FAIL" })), ($(if ($okOutside) { "OK" } else { "FAIL" }))
  if (-not ($okText -and $okPng -and $okOutside)) { $failures++ }
} finally { Remove-Item $tmp -Recurse -Force; Remove-Item $outside -Force -ErrorAction SilentlyContinue }   # the test's own temporary files

"`n$(if ($failures) { "$failures caso/i FALLITO/I" } else { 'tutti i casi superati' })"
exit $failures
