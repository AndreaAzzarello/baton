<#
.SYNOPSIS
  Signs a DownloadManager release so the app (1.1.0 and later) accepts it as an update.
.DESCRIPTION
  -NewKey creates a signing key pair (ECDSA P-256) once. The private key is kept outside the repository, in
  %LOCALAPPDATA%\DownloadManager-signing, encrypted with DPAPI (only this Windows user can use it) or, with -Password,
  with a password (a copy that can be kept offline and used on another computer). The public key is added to
  src\DownloadManager.Core\Updates\release-keys.txt, which is built into the app: commit it and publish a version that
  contains it before signing anything with the new key.

  Without -NewKey the script signs a published program: it writes next to it
    <exe>.sha256  the hash (sha256sum format), read by every version
    <exe>.sig     one signature line per key, required from 1.1.0
  and, with -Url, a JSON manifest (latest.json) for an update address that is not GitHub.
  The signature covers "version + file name + SHA-256", so an old signed program cannot pass for a newer version.
  Rotation: sign with the old and the new key (-Key both) while versions that know only the old one are around; a
  key is retired by removing it from release-keys.txt in a later version.
.PARAMETER Exe
  The program to sign (default publish\DownloadManager.exe).
.PARAMETER Version
  The version it is published as (default: the program's own file version). It must match the release tag.
.PARAMETER Key
  The ids of the keys to sign with (default: every key in the key folder).
.EXAMPLE
  .\tools\sign-release.ps1 -NewKey                      # once: key protected by DPAPI
  .\tools\sign-release.ps1 -NewKey -Password            # a spare key protected by a password, to keep offline
  .\tools\sign-release.ps1                              # signs publish\DownloadManager.exe
  .\tools\sign-release.ps1 -Url https://example.org/DownloadManager.exe   # also writes latest.json
#>
param(
  [switch]$NewKey,
  [switch]$Password,
  [string]$Exe,
  [string]$Version,
  [string[]]$Key,
  [string]$Url,
  [string]$Notes = "",
  [string]$KeyFolder = (Join-Path $env:LOCALAPPDATA "DownloadManager-signing")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security
$root = (Resolve-Path "$PSScriptRoot\..").Path
$keyList = Join-Path $root "src\DownloadManager.Core\Updates\release-keys.txt"
$entropy = [Text.Encoding]::UTF8.GetBytes("DownloadManager release signing key")
$utf8 = New-Object Text.UTF8Encoding($false)

$fullKeyFolder = [IO.Path]::GetFullPath($KeyFolder)
if ($fullKeyFolder.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
  throw "The private keys must stay outside the repository ($root)."
}

function Read-Secret([string]$prompt) {
  $secure = Read-Host $prompt -AsSecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# Password protection: PBKDF2-SHA256 -> AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC).
function Get-PasswordKeys([string]$secret, [byte[]]$salt, [int]$iterations) {
  $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($secret, $salt, $iterations, [Security.Cryptography.HashAlgorithmName]::SHA256)
  try { return , $kdf.GetBytes(64) } finally { $kdf.Dispose() }
}

function Get-Mac([byte[]]$macKey, [byte[]]$iv, [byte[]]$data) {
  $hmac = New-Object Security.Cryptography.HMACSHA256(, $macKey)
  try { return , $hmac.ComputeHash([byte[]]($iv + $data)) } finally { $hmac.Dispose() }
}

function Protect-Key([byte[]]$private) {
  if (-not $Password) {
    return @{ protect = "dpapi"; data = [Convert]::ToBase64String(
      [Security.Cryptography.ProtectedData]::Protect($private, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)) }
  }
  $secret = Read-Secret "Password for the new key (at least 12 characters)"
  if ($secret.Length -lt 12) { throw "The password is too short." }
  if ((Read-Secret "Repeat the password") -ne $secret) { throw "The passwords do not match." }
  $salt = New-Object byte[] 16; $iv = New-Object byte[] 16
  $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($salt); $rng.GetBytes($iv); $rng.Dispose()
  $iterations = 600000
  $keys = Get-PasswordKeys $secret $salt $iterations
  $aes = [Security.Cryptography.Aes]::Create()
  try {
    $aes.Key = [byte[]]$keys[0..31]; $aes.IV = $iv
    $encryptor = $aes.CreateEncryptor()
    $data = $encryptor.TransformFinalBlock($private, 0, $private.Length)
    $mac = Get-Mac ([byte[]]$keys[32..63]) $iv $data
  } finally { $aes.Dispose(); [Array]::Clear($keys, 0, $keys.Length) }
  return @{ protect = "password"; salt = [Convert]::ToBase64String($salt); iterations = $iterations
            iv = [Convert]::ToBase64String($iv); data = [Convert]::ToBase64String($data); mac = [Convert]::ToBase64String($mac) }
}

function Unprotect-Key($file) {
  $data = [Convert]::FromBase64String($file.data)
  if ($file.protect -eq "dpapi") {
    return , [Security.Cryptography.ProtectedData]::Unprotect($data, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
  }
  $secret = Read-Secret "Password of key $($file.id)"
  $salt = [Convert]::FromBase64String($file.salt); $iv = [Convert]::FromBase64String($file.iv)
  $keys = Get-PasswordKeys $secret $salt ([int]$file.iterations)
  try {
    $expected = Get-Mac ([byte[]]$keys[32..63]) $iv $data
    $actual = [Convert]::FromBase64String($file.mac)
    $diff = $expected.Length -bxor $actual.Length
    for ($i = 0; $i -lt [Math]::Min($expected.Length, $actual.Length); $i++) { $diff = $diff -bor ($expected[$i] -bxor $actual[$i]) }
    if ($diff -ne 0) { throw "Wrong password for key $($file.id) (or the key file was altered)." }
    $aes = [Security.Cryptography.Aes]::Create()
    try {
      $aes.Key = [byte[]]$keys[0..31]; $aes.IV = $iv
      $decryptor = $aes.CreateDecryptor()
      return , $decryptor.TransformFinalBlock($data, 0, $data.Length)
    } finally { $aes.Dispose() }
  } finally { [Array]::Clear($keys, 0, $keys.Length) }
}

function Get-KeyId([byte[]]$point) {
  $sha = [Security.Cryptography.SHA256]::Create()
  try { return (($sha.ComputeHash($point)[0..7] | ForEach-Object { $_.ToString("x2") }) -join "") } finally { $sha.Dispose() }
}

if ($NewKey) {
  $creation = New-Object Security.Cryptography.CngKeyCreationParameters
  $creation.ExportPolicy = [Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
  # No name: an ephemeral key, never stored in the Windows key store; only the protected copy below remains.
  # [NullString]: PowerShell would turn $null into "" and persist a key named "" in the store.
  $cng = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $creation)
  try {
    if (-not $cng.IsEphemeral) { $cng.Delete(); throw "The key was not ephemeral: deleted from the key store, nothing saved." }
    $private = $cng.Export([Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
    $public = $cng.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
  } finally { $cng.Dispose() }
  $point = [byte[]]$public[8..71] # BCRYPT_ECCKEY_BLOB: magic, length, then X and Y (32 bytes each)
  $id = Get-KeyId $point
  $file = Protect-Key $private
  [Array]::Clear($private, 0, $private.Length)
  $file.id = $id
  $file.public = [Convert]::ToBase64String($point)
  $file.created = (Get-Date).ToString("yyyy-MM-dd")
  New-Item -ItemType Directory -Force $fullKeyFolder | Out-Null
  $path = Join-Path $fullKeyFolder "$id.key.json"
  [IO.File]::WriteAllText($path, ($file | ConvertTo-Json), $utf8)
  $line = "$($file.public)  # $id, $($file.protect), created $($file.created)`n"
  [IO.File]::AppendAllText($keyList, $line, $utf8)
  Write-Host "New key $id ($($file.protect)) saved in $path" -ForegroundColor Green
  Write-Host "Public key added to src\DownloadManager.Core\Updates\release-keys.txt: rebuild, commit and publish a version that contains it."
  if ($file.protect -eq "dpapi") { Write-Host "DPAPI ties the key to this Windows user: also make a spare key with -NewKey -Password and keep its file offline." -ForegroundColor Yellow }
  exit 0
}

if (-not $Exe) { $Exe = Join-Path $root "publish\DownloadManager.exe" }
$Exe = (Resolve-Path $Exe).Path
$name = [IO.Path]::GetFileName($Exe)
if (-not $Version) {
  $v = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).FileVersion
  $Version = "$($v.Major).$($v.Minor).$([Math]::Max(0, $v.Build))"
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.1.0." }

$keyFiles = @(Get-ChildItem $fullKeyFolder -Filter "*.key.json" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })
if ($Key) { $keyFiles = @($keyFiles | Where-Object { $Key -contains $_.id }) }
if ($keyFiles.Count -eq 0) { throw "No signing key in $fullKeyFolder (create one with -NewKey)." }
$trusted = if (Test-Path $keyList) { Get-Content $keyList -Raw } else { "" }

$sha = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
$statement = $utf8.GetBytes("DownloadManager update 1`nversion=$Version`nfile=$name`nsha256=$sha`n")
$lines = @("# $name $Version")
foreach ($file in $keyFiles) {
  if (-not $trusted.Contains($file.public)) { Write-Warning "Key $($file.id) is not in release-keys.txt: versions built from this repository will not accept its signature." }
  $private = Unprotect-Key $file
  try {
    $cng = [Security.Cryptography.CngKey]::Import($private, [Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
    $ecdsa = New-Object Security.Cryptography.ECDsaCng($cng)
    try { $signature = $ecdsa.SignData($statement, [Security.Cryptography.HashAlgorithmName]::SHA256) } # IEEE P1363 (r||s)
    finally { $ecdsa.Dispose(); $cng.Dispose() }
  } finally { [Array]::Clear($private, 0, $private.Length) }
  $lines += "$($file.id) $([Convert]::ToBase64String($signature))"
}
$sigText = ($lines -join "`n") + "`n"
[IO.File]::WriteAllText("$Exe.sha256", "$sha  $name`n", $utf8)
[IO.File]::WriteAllText("$Exe.sig", $sigText, $utf8)
Write-Host "Signed $name $Version (SHA-256 $sha) with $($keyFiles.Count) key(s):" -ForegroundColor Green
Write-Host "  $Exe.sha256"
Write-Host "  $Exe.sig"
if ($Url) {
  # The same rule as the app: https, or http only to this computer (local trials).
  if ($Url -notmatch '^(https://|http://(127\.0\.0\.1|localhost)[:/])') { throw "The download address must be https." }
  $manifest = [ordered]@{ version = $Version; notes = $Notes; url = $Url; sha256 = $sha; signature = $sigText }
  $manifestPath = Join-Path ([IO.Path]::GetDirectoryName($Exe)) "latest.json"
  [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), $utf8)
  Write-Host "  $manifestPath"
}
