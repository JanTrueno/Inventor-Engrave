# Builds the add-in and packs it into dist\Engrave-Setup-<version>.exe with Inno Setup 6.
# Usage:  powershell -ExecutionPolicy Bypass -File build-installer.ps1 [-Version 1.0.0]
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'deploy\PackageContents.xml'))).ApplicationPackage.AppVersion
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

dotnet build (Join-Path $root 'Engrave.csproj') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

& $iscc /Q "/DAppVersion=$Version" (Join-Path $root 'installer\Engrave.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
"Built " + (Join-Path $root "dist\Engrave-Setup-$Version.exe")
