<#
.SYNOPSIS
  Signs the Firefox extension as "unlisted" on addons.mozilla.org (self-distribution): the signed .xpi installs
  permanently in any Firefox, but the add-on is not published in the AMO catalogue.
.DESCRIPTION
  The AMO API credentials are read from environment variables of the current session, never from a file:
    AMO_JWT_ISSUER  (the "JWT issuer", e.g. user:12345:67)
    AMO_JWT_SECRET  (the "JWT secret")
  They are handed to web-ext through its own environment variables (WEB_EXT_API_KEY / WEB_EXT_API_SECRET) of the child
  process only, so they never appear on a command line, in the repository or in the output.
  Each upload needs a new version number (extension\manifest.json): AMO refuses a version it has already signed.
.PARAMETER Check
  Only checks what is needed (Node.js, the credentials, the build) without uploading anything.
.EXAMPLE
  $env:AMO_JWT_ISSUER = "user:12345:67"; $env:AMO_JWT_SECRET = "<secret>"; .\tools\sign-firefox.ps1
#>
param([switch]$Check)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
# web-ext is pinned to an exact version and run through npx: nothing is installed in the project.
$webExt = "web-ext@10.7.0"

if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw "Node.js 20 or later is required (https://nodejs.org)." }
$issuer = $env:AMO_JWT_ISSUER
$secret = $env:AMO_JWT_SECRET
if (-not $issuer -or -not $secret) {
  Write-Host "Missing AMO credentials. In this PowerShell window set:" -ForegroundColor Yellow
  Write-Host '  $env:AMO_JWT_ISSUER = "user:..."   # JWT issuer'
  Write-Host '  $env:AMO_JWT_SECRET = "..."        # JWT secret'
  Write-Host "(they are generated at https://addons.mozilla.org/developers/addon/api/key/ - see README, Firefox)"
  exit 2
}
if ($issuer -notmatch '^user:\d+:\d+$') { throw "AMO_JWT_ISSUER does not look like a JWT issuer (user:<number>:<number>)." }

$manifest = Get-Content (Join-Path $root "extension\manifest.json") -Raw | ConvertFrom-Json
Write-Host "Extension version $($manifest.version): building dist\firefox..."
node (Join-Path $root "tools\build-extension.mjs")
if ($LASTEXITCODE -ne 0) { throw "The extension build failed." }
$source = Join-Path $root "dist\firefox"
$artifacts = Join-Path $root "dist\signed"
if ($Check) { Write-Host "Ready: credentials present, build in $source. Run without -Check to upload and sign."; exit 0 }

New-Item -ItemType Directory -Force $artifacts | Out-Null
$env:WEB_EXT_API_KEY = $issuer
$env:WEB_EXT_API_SECRET = $secret
try {
  # Unlisted: signed for self-distribution; AMO may still review it automatically (usually a few minutes).
  npx --yes $webExt sign --channel unlisted --source-dir $source --artifacts-dir $artifacts --approval-timeout 900000
  if ($LASTEXITCODE -ne 0) { throw "web-ext sign failed (exit code $LASTEXITCODE)." }
} finally {
  Remove-Item Env:WEB_EXT_API_KEY, Env:WEB_EXT_API_SECRET -ErrorAction SilentlyContinue
}
$signed = Get-ChildItem $artifacts -Filter *.xpi | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Signed: $($signed.FullName)" -ForegroundColor Green
Write-Host "Install it in Firefox: about:addons > gear icon > Install Add-on From File..."
