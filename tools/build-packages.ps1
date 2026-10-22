<#
.SYNOPSIS
Builds the installer (Inno Setup) and the portable zip from a published Baton.exe.

.EXAMPLE
powershell -File tools\build-packages.ps1                       # from publish\Baton.exe into dist\release
powershell -File tools\build-packages.ps1 -Source C:\x -Out C:\y -Version 1.3.0

The version is read from src\Baton.App\Baton.App.csproj unless given. Inno Setup's compiler is looked up in ISCC (an
environment variable with the full path of ISCC.exe), %LOCALAPPDATA%\Programs\InnoSetup6 and Program Files.
Outputs: Baton-Setup-<version>.exe and Baton-<version>-portable.zip (Baton.exe, portable.flag, licenses). Files inside the
zip carry the time of SOURCE_DATE_EPOCH (seconds; the release workflow sets it to the commit's time), so the same
Baton.exe always gives the same zip. Nothing is signed here: see docs\SIGNING.md.
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

# Portable zip
$zipPath = Join-Path $Out "Baton-$Version-portable.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }
$time = if ($env:SOURCE_DATE_EPOCH) { [DateTimeOffset]::FromUnixTimeSeconds([long]$env:SOURCE_DATE_EPOCH) } else { [DateTimeOffset](Get-Item $exe).LastWriteTimeUtc }
$flag = "This file makes Baton portable: it keeps its data in the Data folder next to Baton.exe and writes nothing to the`r`n" +
        "registry (only the browsers' native messaging host, if you allow it). Delete this file to use the data of the installed`r`n" +
        "version in %LOCALAPPDATA%\Baton instead.`r`n"
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($zipPath, 'CreateNew')
$zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $entries = @(
        @{ Name = 'Baton.exe'; Bytes = [IO.File]::ReadAllBytes($exe) },
        @{ Name = 'portable.flag'; Bytes = [Text.Encoding]::UTF8.GetBytes($flag) },
        @{ Name = 'LICENSE.txt'; Bytes = [IO.File]::ReadAllBytes((Join-Path $root 'LICENSE')) },
        @{ Name = 'THIRD-PARTY-LICENSES.txt'; Bytes = [IO.File]::ReadAllBytes((Join-Path $root 'THIRD-PARTY-LICENSES.txt')) }
    )
    foreach ($e in $entries) {
        $entry = $zip.CreateEntry($e.Name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $time
        $s = $entry.Open(); $s.Write($e.Bytes, 0, $e.Bytes.Length); $s.Dispose()
    }
} finally { $zip.Dispose(); $stream.Dispose() }
Write-Output "portable: $zipPath"

if ($SkipInstaller) { return }
$iscc = @($env:ISCC, (Join-Path $env:LOCALAPPDATA 'Programs\InnoSetup6\ISCC.exe'),
          (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found: install it or set ISCC' }
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$Source" "/DOutputDir=$Out" (Join-Path $root 'installer\Baton.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Write-Output "installer: $(Join-Path $Out "Baton-Setup-$Version.exe")"
