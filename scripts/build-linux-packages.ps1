#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes Anchor for Linux and builds the deb, rpm, and Arch packages.

.DESCRIPTION
    Run from any folder on Linux or Windows with nfpm on PATH. Performs a self-contained
    single-file linux-x64 publish of the tray application into artifacts/publish-linux,
    then builds each package from packaging/nfpm.yaml into artifacts/packages. The package
    version is MyAppVersion from installer/anchor.iss, the same version as the Windows
    installer. Fails fast on any step.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolve the repository root (this script lives in <root>/scripts).
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "==> Repository root: $RepoRoot"

$PublishDir = Join-Path $RepoRoot 'artifacts/publish-linux'
$PackagesOut = Join-Path $RepoRoot 'artifacts/packages'
$IssFile = Join-Path $RepoRoot 'installer/anchor.iss'
$NfpmConfig = Join-Path $RepoRoot 'packaging/nfpm.yaml'

# --- 1. Version -------------------------------------------------------------
$Iss = Get-Content $IssFile -Raw
if ($Iss -notmatch '#define\s+MyAppVersion\s+"([^"]+)"') {
    Write-Error "Could not find MyAppVersion in $IssFile."
    exit 1
}
$Version = $Matches[1]
Write-Host "==> Version: $Version"

# --- 2. Locate nfpm ---------------------------------------------------------
$Nfpm = Get-Command 'nfpm' -ErrorAction SilentlyContinue
if (-not $Nfpm) {
    Write-Error @"
Could not find nfpm on PATH.
Install it from https://nfpm.goreleaser.com/install/ (the release workflow uses v2.48.0).
"@
    exit 1
}
Write-Host "==> Using nfpm: $($Nfpm.Source)"

# --- 3. Publish -------------------------------------------------------------
# A clean folder, so no file from an older publish lands in a package.
if (Test-Path $PublishDir) {
    Remove-Item -Recurse -Force $PublishDir
}

Write-Host "==> Publishing Anchor (self-contained single-file linux-x64)..."
dotnet publish src/AnchorTray -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

foreach ($File in 'Anchor', 'libSkiaSharp.so', 'libHarfBuzzSharp.so') {
    if (-not (Test-Path (Join-Path $PublishDir $File))) {
        Write-Error "Publish succeeded but $File was not found in $PublishDir."
        exit 1
    }
}

# --- 4. Build the packages --------------------------------------------------
New-Item -ItemType Directory -Force -Path $PackagesOut | Out-Null
Get-ChildItem $PackagesOut -File | Remove-Item -Force

$env:VERSION = $Version
foreach ($Packager in 'deb', 'rpm', 'archlinux') {
    Write-Host "==> Building $Packager package..."
    & $Nfpm.Source package --config $NfpmConfig --packager $Packager --target $PackagesOut
    if ($LASTEXITCODE -ne 0) {
        Write-Error "nfpm ($Packager) failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}

Write-Host "==> Done. Packages written to: $PackagesOut"
Get-ChildItem $PackagesOut -File | ForEach-Object { Write-Host "    $($_.Name)" }
