<#
.SYNOPSIS
Builds the installer (Inno Setup) and the portable zip from a published Baton.exe.

.EXAMPLE
powershell -File tools\build-packages.ps1                       # from publish\Baton.exe into dist\release
powershell -File tools\build-packages.ps1 -Source C:\x -Out C:\y -Version 1.3.1

The version is read from src\Baton.App\Baton.App.csproj unless given. Inno Setup's compiler is looked up in ISCC (an
environment variable with the full path of ISCC.exe), %LOCALAPPDATA%\Programs\InnoSetup6 and Program Files.
Outputs: Baton-Setup-<version>.exe and Baton-<version>-portable.zip (Baton.exe, portable.flag, licenses), both with the
browser extension of the same version next to Baton.exe: the "extension" folder (Chromium, Load unpacked) and
baton-firefox.xpi; node builds it first. Also baton-extension-<version>.zip, the same "extension" folder and xpi for the
app's updater (sign it like Baton.exe). Files inside the zips carry the time of SOURCE_DATE_EPOCH (seconds; the release
workflow sets it to the commit's time). Nothing is signed here: see docs\SIGNING.md.
#>
param(
    [string]$Source,
    [string]$Out,
    [string]$Version,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $root 'publish' }
if (-not $Out) { $Out = Join-Path $root 'dist\release' }
if (-not $Version) {
    $Version = ([xml](Get-Content -Raw (Join-Path $root 'src\Baton.App\Baton.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
# Full paths: Inno Setup reads relative ones from the folder of the script (installer\), not from here.
$Source = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Source)
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
$exe = Join-Path $Source 'Baton.exe'
if (-not (Test-Path $exe)) { throw "Baton.exe not found in $Source (dotnet publish src\Baton.App -c Release -r win-x64 -o publish)" }
$fileVersion = (Get-Item $exe).VersionInfo.ProductVersion
if ($fileVersion -notlike "$Version*") { throw "Baton.exe is version $fileVersion, not $Version" }
New-Item -ItemType Directory -Force $Out | Out-Null

# The browser extension of the same version, shipped next to Baton.exe: the unpacked Chromium build as "extension"
# (Load unpacked) and the Firefox package, so nobody needs a separate download.
node (Join-Path $root 'tools\build-extension.mjs') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'node tools\build-extension.mjs failed' }
$extension = Join-Path $root 'dist\chromium'
$firefox = Join-Path $root "dist\baton-firefox-$Version.xpi"
$extensionVersion = (Get-Content -Raw (Join-Path $extension 'manifest.json') | ConvertFrom-Json).version
if ($extensionVersion -ne $Version) { throw "the extension is version $extensionVersion, not $Version" }
if (-not (Test-Path $firefox)) { throw "the Firefox package $firefox is missing" }

# Portable zip
$zipPath = Join-Path $Out "Baton-$Version-portable.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }
$time = if ($env:SOURCE_DATE_EPOCH) { [DateTimeOffset]::FromUnixTimeSeconds([long]$env:SOURCE_DATE_EPOCH) } else { [DateTimeOffset](Get-Item $exe).LastWriteTimeUtc }
$flag = "This file makes Baton portable: it keeps its data in the Data folder next to Baton.exe and writes to the registry`r`n" +
        "only what you allow (the browsers' native messaging host, start with Windows). Delete this file to use the data of the`r`n" +
        "installed version in %LOCALAPPDATA%\Baton instead.`r`n"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# Writes a zip with the entries in the given order and fixed times, then checks that it holds the files it needs.
function Write-Zip([string]$path, $entries, [string[]]$needed) {
    if (Test-Path $path) { Remove-Item $path }
    $stream = [IO.File]::Open($path, 'CreateNew')
    $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($e in $entries) {
            $entry = $zip.CreateEntry($e.Name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $time
            $s = $entry.Open(); $s.Write($e.Bytes, 0, $e.Bytes.Length); $s.Dispose()
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    $check = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        foreach ($n in $needed) {
            if (-not ($check.Entries | Where-Object FullName -eq $n)) { throw "$([IO.Path]::GetFileName($path)) lacks $n" }
        }
    } finally { $check.Dispose() }
}

# The extension as it sits next to Baton.exe: the Firefox package, then the folder in a fixed order (by path), with
# forward slashes as zip entries use.
$extensionEntries = @(@{ Name = 'baton-firefox.xpi'; Bytes = [IO.File]::ReadAllBytes($firefox) })
foreach ($file in Get-ChildItem $extension -Recurse -File | Sort-Object FullName) {
    $extensionEntries += @{ Name = 'extension/' + $file.FullName.Substring($extension.Length + 1).Replace('\', '/'); Bytes = [IO.File]::ReadAllBytes($file.FullName) }
}
$extensionNeeded = 'baton-firefox.xpi', 'extension/manifest.json', 'extension/background.js'

$entries = @(
    @{ Name = 'Baton.exe'; Bytes = [IO.File]::ReadAllBytes($exe) },
    @{ Name = 'portable.flag'; Bytes = [Text.Encoding]::UTF8.GetBytes($flag) },
    @{ Name = 'LICENSE.txt'; Bytes = [IO.File]::ReadAllBytes((Join-Path $root 'LICENSE')) },
    @{ Name = 'THIRD-PARTY-LICENSES.txt'; Bytes = [IO.File]::ReadAllBytes((Join-Path $root 'THIRD-PARTY-LICENSES.txt')) }
) + $extensionEntries
# The package works on its own only with the extension inside.
Write-Zip $zipPath $entries (@('Baton.exe', 'portable.flag') + $extensionNeeded)
Write-Output "portable: $zipPath"

# The extension's package for the app's updater (from 1.3.3): the same files, signed like Baton.exe (sign-release.ps1).
$packagePath = Join-Path $Out "baton-extension-$Version.zip"
Write-Zip $packagePath $extensionEntries $extensionNeeded
Write-Output "extension: $packagePath"

if ($SkipInstaller) { return }
$iscc = @($env:ISCC, (Join-Path $env:LOCALAPPDATA 'Programs\InnoSetup6\ISCC.exe'),
          (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found: install it or set ISCC' }
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$Source" "/DOutputDir=$Out" "/DExtensionDir=$extension" "/DFirefoxPackage=$firefox" (Join-Path $root 'installer\Baton.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Write-Output "installer: $(Join-Path $Out "Baton-Setup-$Version.exe")"
