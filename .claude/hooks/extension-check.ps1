# PostToolUse (Edit, Write): after a change in extension/ or tests/extension/, checks the syntax of the file and runs
# the extension's Node tests (about a second). On failure the error goes back to Claude (exit 2).
[Console]::InputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8
try { $path = ([Console]::In.ReadToEnd() | ConvertFrom-Json).tool_input.file_path } catch { exit 0 }
$root = Split-Path (Split-Path $PSScriptRoot)
if (-not $path) { exit 0 }
$relative = [IO.Path]::GetFullPath($path)
if (-not $relative.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { exit 0 }   # another project
$relative = $relative.Substring($root.Length + 1)
if ($relative -notmatch '(?i)^(extension|tests\\extension)\\') { exit 0 }
$problems = @()

if ($path -match '(?i)\.m?js$') {
  $source = Get-Content $path -Raw -Encoding UTF8
  if ($source -match '(?m)^\s*(import|export)\b') {
    # ES modules go through stdin: Node 24's "node --check file.js" reports no error at all for a .js file that uses
    # module syntax, even when it is broken.
    $OutputEncoding = [Text.Encoding]::UTF8
    $out = $source | & node --input-type=module --check 2>&1
  } else {
    $out = & node --check $path 2>&1
  }
  if ($LASTEXITCODE -ne 0) { $problems += "node --check $([IO.Path]::GetFileName($path)):`n$(($out | Select-Object -First 6) -join "`n")" }
}
elseif ($path -match '(?i)\.json$') {
  try { Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null } catch { $problems += "JSON non valido in $([IO.Path]::GetFileName($path)): $($_.Exception.Message)" }
}

if ($problems.Count -eq 0 -and $path -match '(?i)\.(m?js|json)$') {
  Push-Location $root
  $out = & node --test --test-reporter=dot "tests/extension/*.test.mjs" 2>&1
  $failed = $LASTEXITCODE -ne 0
  Pop-Location
  if ($failed) { $problems += "test dell'estensione falliti (node --test tests/extension):`n$(($out | Select-Object -Last 40) -join "`n")" }
}

if ($problems.Count -gt 0) {
  [Console]::Error.WriteLine($problems -join "`n`n")
  exit 2
}
exit 0
