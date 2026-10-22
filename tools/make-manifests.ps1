<#
.SYNOPSIS
Writes the winget manifests (AndreaAzzarello.Baton) and the Scoop manifest (baton.json) for a release, from the files
that will be published: version and SHA-256 come from the files themselves.

.EXAMPLE
powershell -File tools\make-manifests.ps1 -Setup dist\release\Baton-Setup-1.3.1.exe -Zip dist\release\Baton-1.3.1-portable.zip

Run it on the files that are attached to the GitHub release (after the Authenticode signature, which changes the hash).
Outputs packaging\winget\manifests\a\AndreaAzzarello\Baton\<version>\ (to copy into a fork of microsoft/winget-pkgs)
and packaging\scoop\baton.json (for the bucket AndreaAzzarello/scoop-baton). -BaseUrl serves local trials.
#>
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Zip,
    [string]$BaseUrl,
    [string]$Out,
    [string]$ReleaseDate = (Get-Date -Format 'yyyy-MM-dd')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'packaging' }
if ((Split-Path -Leaf $Setup) -notmatch '^Baton-Setup-(\d+\.\d+\.\d+)\.exe$') { throw "unexpected installer name: $Setup" }
$version = $Matches[1]
if ((Split-Path -Leaf $Zip) -ne "Baton-$version-portable.zip") { throw "the zip is not version $version" }
if (-not $BaseUrl) { $BaseUrl = "https://github.com/AndreaAzzarello/baton/releases/download/v$version" }
$setupSha = (Get-FileHash $Setup -Algorithm SHA256).Hash
$zipSha = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
$id = 'AndreaAzzarello.Baton'
$productCode = '{27469723-AE14-4420-B2B4-189B8BE709AB}_is1'
$schema = '1.12.0'
$header = "# Created with tools\make-manifests.ps1`n"

$dir = Join-Path $Out "winget\manifests\a\AndreaAzzarello\Baton\$version"
New-Item -ItemType Directory -Force $dir | Out-Null
function Write-Yaml([string]$name, [string]$type, [string]$body) {
    $text = $header + "# yaml-language-server: `$schema=https://aka.ms/winget-manifest.$type.$schema.schema.json`n`n" + $body.Trim() + "`n"
    [IO.File]::WriteAllText((Join-Path $dir $name), $text.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
}

Write-Yaml "$id.yaml" 'version' @"
PackageIdentifier: $id
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@

Write-Yaml "$id.installer.yaml" 'installer' @"
PackageIdentifier: $id
PackageVersion: $version
MinimumOSVersion: 10.0.17763.0
InstallerType: inno
Scope: user
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
ReleaseDate: $ReleaseDate
AppsAndFeaturesEntries:
- DisplayName: Baton
  Publisher: Andrea Azzarello
  ProductCode: '$productCode'
Installers:
- Architecture: x64
  InstallerUrl: $BaseUrl/Baton-Setup-$version.exe
  InstallerSha256: $setupSha
  ProductCode: '$productCode'
ManifestType: installer
ManifestVersion: $schema
"@

$tags = "Tags:`n- download-manager`n- downloader`n- segmented-download`n- hls`n- dash`n- browser-extension`n- grabber`n- wpf"
Write-Yaml "$id.locale.en-US.yaml" 'defaultLocale' @"
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: en-US
Publisher: Andrea Azzarello
PublisherUrl: https://github.com/AndreaAzzarello
PublisherSupportUrl: https://github.com/AndreaAzzarello/baton/issues
Author: Andrea Azzarello
PackageName: Baton
PackageUrl: https://github.com/AndreaAzzarello/baton
License: MIT
LicenseUrl: https://github.com/AndreaAzzarello/baton/blob/main/LICENSE
Copyright: Copyright (c) 2026 Andrea Azzarello
ShortDescription: A fast segmented download manager for Windows, with browser extensions for Chromium and Firefox.
Description: |-
  Baton downloads files in several parallel segments, resumes interrupted downloads, organizes them in queues and
  categories with schedules and limits, and saves videos (HLS/DASH) with their languages and subtitles. A grabber finds
  the files of a site or saves an offline copy. The browser extension sends downloads, links and videos to the app.
  English and Italian interface.
Moniker: baton
$tags
ReleaseNotesUrl: https://github.com/AndreaAzzarello/baton/releases/tag/v$version
ManifestType: defaultLocale
ManifestVersion: $schema
"@

Write-Yaml "$id.locale.it-IT.yaml" 'locale' @"
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: it-IT
Publisher: Andrea Azzarello
PackageName: Baton
License: MIT
ShortDescription: Download manager veloce a segmenti per Windows, con estensioni per i browser Chromium e Firefox.
Description: |-
  Baton scarica i file in più segmenti paralleli, riprende i download interrotti, li organizza in code e categorie con
  pianificazioni e limiti, e salva i video (HLS/DASH) con lingue e sottotitoli. Il grabber trova i file di un sito o ne
  salva una copia offline. L'estensione del browser invia all'app download, link e video. Interfaccia in italiano e inglese.
ManifestType: locale
ManifestVersion: $schema
"@

# Scoop: the portable zip; its Data folder persists across versions (scoop keeps it in persist\baton).
$scoop = [ordered]@{
    version = $version
    description = 'A fast segmented download manager for Windows, with browser extensions for Chromium and Firefox (portable version).'
    homepage = 'https://github.com/AndreaAzzarello/baton'
    license = 'MIT'
    notes = @('Data (downloads list, settings) is kept in $persist_dir\Data.',
              'The browser extension is included: allow the browser registration when Baton asks, then load the "extension" subfolder of "scoop prefix baton" (Load unpacked; that path follows updates) or baton-firefox.xpi next to it; see the README.')
    architecture = [ordered]@{ '64bit' = [ordered]@{ url = "$BaseUrl/Baton-$version-portable.zip"; hash = $zipSha } }
    shortcuts = @(, @('Baton.exe', 'Baton'))
    persist = 'Data'
    checkver = [ordered]@{ github = 'https://github.com/AndreaAzzarello/baton' }
    autoupdate = [ordered]@{ architecture = [ordered]@{ '64bit' = [ordered]@{ url = 'https://github.com/AndreaAzzarello/baton/releases/download/v$version/Baton-$version-portable.zip' } } }
}
$scoopDir = Join-Path $Out 'scoop'
New-Item -ItemType Directory -Force $scoopDir | Out-Null
$scoopFile = Join-Path $scoopDir 'baton.json'
[IO.File]::WriteAllText($scoopFile, ($scoop | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
# Windows PowerShell's JSON layout, rewritten in the usual one of Scoop buckets (4 spaces, keys in this order).
python -c "import json,sys; p=sys.argv[1]; d=json.load(open(p,encoding='utf-8')); open(p,'w',encoding='utf-8',newline='\n').write(json.dumps(d,indent=4,ensure_ascii=False)+'\n')" $scoopFile
if ($LASTEXITCODE -ne 0) { throw 'python is needed to format baton.json' }
"winget: $dir"
"scoop:  $(Join-Path $scoopDir 'baton.json')"
