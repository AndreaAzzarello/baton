<#
.SYNOPSIS
  Verifies a downloaded Baton release the way the app checks an update: SHA-256 and signature, and the Authenticode
  signature when the file carries one.
.DESCRIPTION
  Needs Baton.exe with Baton.exe.sha256 and Baton.exe.sig next to it (the three release
  assets). The signature must be valid for "version + file name + SHA-256" with one of the public keys in
  src\Baton.Core\Updates\release-keys.txt (or the list given with -KeyList). Nothing is installed or run.
  Works the same for the installer (Baton-Setup-<version>.exe with its .sha256 and .sig).
  Authenticode (from 1.3, once SignPath Foundation signs the releases): a file that carries a signature must have a valid
  one, which Windows checks up to a trusted root; the signer is shown. A file without one passes (releases signed only
  with the release key), unless -RequireAuthenticode is given.
.PARAMETER Version
  The version the release claims (its tag without "v"); default: the program's own file version.
.PARAMETER Name
  The program's name in the release (Baton.exe), which the signature covers; default: the file's own name. Needed when
  the browser saved it under another name, e.g. "Baton (1).exe". Its .sha256 and .sig are looked for under that name too.
.EXAMPLE
  .\tools\verify-release.ps1 -Exe "$HOME\Downloads\Baton.exe" -Version 1.2.0
  .\tools\verify-release.ps1 -Exe "$HOME\Downloads\Baton-Setup-1.3.0.exe" -RequireAuthenticode
#>
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [string]$Version,
  [string]$Name,
  [string]$KeyList,
  [switch]$RequireAuthenticode
)
$ErrorActionPreference = "Stop"
if (-not $KeyList) { $KeyList = Join-Path $PSScriptRoot "..\src\Baton.Core\Updates\release-keys.txt" }
$Exe = (Resolve-Path $Exe).Path
$name = if ($Name) { $Name } else { [IO.Path]::GetFileName($Exe) }
$folder = [IO.Path]::GetDirectoryName($Exe)
$shaFile = if (Test-Path "$Exe.sha256") { "$Exe.sha256" } else { Join-Path $folder "$name.sha256" }
$sigFile = if (Test-Path "$Exe.sig") { "$Exe.sig" } else { Join-Path $folder "$name.sig" }
if (-not $Version) {
  $v = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).FileVersion
  $Version = "$($v.Major).$($v.Minor).$([Math]::Max(0, $v.Build))"
}
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.1.1 (the release tag without 'v')." }

$sha = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
# The first 64-digit hex value, as the app reads it: sha256sum, BSD ("SHA256 (name) = hash"), certutil or the bare hash.
$declared = if ((Get-Content $shaFile -Raw) -match '\b[0-9a-fA-F]{64}\b') { $Matches[0].ToLowerInvariant() } else { "(none)" }
if ($declared -ne $sha) { throw "SHA-256 mismatch: the file is $sha, $([IO.Path]::GetFileName($shaFile)) says $declared." }

$keys = @{}
foreach ($line in Get-Content $KeyList) {
  $text = ($line -split '#')[0].Trim()
  if (-not $text) { continue }
  $point = [Convert]::FromBase64String($text)
  $hash = [Security.Cryptography.SHA256]::Create().ComputeHash($point)
  $keys[(($hash[0..7] | ForEach-Object { $_.ToString("x2") }) -join "")] = $point
}

# "DownloadManager update 1": the statement's format name from before the rename, unchanged (see UpdateSignature).
$statement = (New-Object Text.UTF8Encoding($false)).GetBytes("DownloadManager update 1`nversion=$Version`nfile=$name`nsha256=$sha`n")
$valid = @()
foreach ($line in Get-Content $sigFile) {
  $parts = @($line.Trim() -split '\s+')
  if ($parts.Count -ne 2 -or -not $keys.ContainsKey($parts[0])) { continue } # comments and unknown keys prove nothing
  # BCRYPT_ECDSA_PUBLIC_P256_MAGIC ("ECS1"), key length 32, then X||Y.
  $blob = [byte[]](0x45, 0x43, 0x53, 0x31, 0x20, 0, 0, 0) + $keys[$parts[0]]
  $cng = [Security.Cryptography.CngKey]::Import($blob, [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
  $ecdsa = New-Object Security.Cryptography.ECDsaCng($cng)
  try {
    if ($ecdsa.VerifyData($statement, [Convert]::FromBase64String($parts[1]), [Security.Cryptography.HashAlgorithmName]::SHA256)) { $valid += $parts[0] }
  } catch [FormatException] { } finally { $ecdsa.Dispose(); $cng.Dispose() }
}
if ($valid.Count -eq 0) { throw "No valid signature of $name $Version by a known key: do not install it." }

# Authenticode: added by SignPath before the release key signs, so the SHA-256 above already covers it.
$authenticode = Get-AuthenticodeSignature -LiteralPath $Exe
$signer = "no Authenticode signature"
switch ($authenticode.Status) {
  'Valid' { $signer = "Authenticode: $($authenticode.SignerCertificate.Subject)" }
  'NotSigned' { if ($RequireAuthenticode) { throw "$name has no Authenticode signature." } }
  default { throw "$name has an Authenticode signature that is not valid ($($authenticode.Status)): $($authenticode.StatusMessage) Do not install it." }
}
Write-Host "OK: $name $Version, SHA-256 $sha, signed by $($valid -join ', '); $signer." -ForegroundColor Green
