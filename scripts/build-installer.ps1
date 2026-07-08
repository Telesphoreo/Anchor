#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes Anchor and compiles the Windows installer.

.DESCRIPTION
    Run from the repository root. Performs a self-contained single-file publish of the
    tray application, locates the Inno Setup compiler (ISCC.exe), and compiles
    installer\anchor.iss into artifacts\installer. Fails fast on any step.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolve the repository root (this script lives in <root>\scripts).
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "==> Repository root: $RepoRoot"

$PublishDir = Join-Path $RepoRoot 'artifacts\publish'
$InstallerOut = Join-Path $RepoRoot 'artifacts\installer'
$IssFile = Join-Path $RepoRoot 'installer\anchor.iss'

# --- 1. Publish -------------------------------------------------------------
Write-Host "==> Publishing Anchor (self-contained single-file win-x64)..."
dotnet publish src/AnchorTray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

if (-not (Test-Path (Join-Path $PublishDir 'Anchor.exe'))) {
    Write-Error "Publish succeeded but Anchor.exe was not found in $PublishDir."
    exit 1
}

# --- 2. Locate ISCC.exe -----------------------------------------------------
Write-Host "==> Locating Inno Setup compiler (ISCC.exe)..."
$Iscc = $null

$OnPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
if ($OnPath) {
    $Iscc = $OnPath.Source
}

if (-not $Iscc) {
    $Candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $Candidates) {
        if ($c -and (Test-Path $c)) {
            $Iscc = $c
            break
        }
    }
}

if (-not $Iscc) {
    Write-Error @"
Could not find ISCC.exe (the Inno Setup 6 compiler).
Install Inno Setup 6 from https://jrsoftware.org/isdl.php, or add ISCC.exe to PATH.
Searched: PATH, `${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe, `$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe.
"@
    exit 1
}

Write-Host "==> Using ISCC: $Iscc"

# --- 3. Compile the installer ----------------------------------------------
New-Item -ItemType Directory -Force -Path $InstallerOut | Out-Null

Write-Host "==> Compiling installer: $IssFile"
& $Iscc $IssFile
if ($LASTEXITCODE -ne 0) {
    Write-Error "ISCC failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

Write-Host "==> Done. Installer written to: $InstallerOut"
