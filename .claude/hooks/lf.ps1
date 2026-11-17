# PostToolUse (Edit, Write): the repository is LF only (.gitattributes). If the file just written is a text file inside the
# project and holds CRLF, turn it back to LF (keeping a BOM if there is one). Never touches binary files or other projects.
[Console]::InputEncoding = [Text.Encoding]::UTF8
try { $path = ([Console]::In.ReadToEnd() | ConvertFrom-Json).tool_input.file_path } catch { exit 0 }
if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { exit 0 }
$root = Split-Path (Split-Path $PSScriptRoot)
$full = [IO.Path]::GetFullPath($path)
if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { exit 0 }   # another project
if ($full -match '(?i)\\(\.git|bin|obj|node_modules|graphify-out|dist|publish)\\') { exit 0 }
$relative = $full.Substring($root.Length + 1).Replace('\', '/')
Push-Location $root
try {
  $attr = (& git check-attr text -- $relative 2>$null) -join ''
  if ($attr -match ': text: (unset|binary)') { exit 0 }   # *.png etc. are "binary" in .gitattributes
} finally { Pop-Location }
$bytes = [IO.File]::ReadAllBytes($full)
if ($bytes.Length -gt 5MB -or [Array]::IndexOf($bytes, [byte]0) -ge 0) { exit 0 }   # big or binary
if ([Array]::IndexOf($bytes, [byte]13) -lt 0) { exit 0 }                              # no CR at all: nothing to do
$out = New-Object 'System.Collections.Generic.List[byte]' $bytes.Length
for ($i = 0; $i -lt $bytes.Length; $i++) {
  if ($bytes[$i] -eq 13 -and $i + 1 -lt $bytes.Length -and $bytes[$i + 1] -eq 10) { continue }   # drop the CR of CRLF
  $out.Add($bytes[$i])
}
[IO.File]::WriteAllBytes($full, $out.ToArray())
exit 0
