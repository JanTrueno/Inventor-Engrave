# Builds the add-in and installs it for the current user (Inventor 2025).
# Usage:  powershell -ExecutionPolicy Bypass -File install.ps1          (install / update)
#         powershell -ExecutionPolicy Bypass -File install.ps1 -Remove  (uninstall)
# Close Inventor first; the add-in DLL is locked while Inventor has it loaded.
param([switch]$Remove, [switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\Engrave.bundle'
$target = Join-Path $bundle 'Contents\2025'

if ($Remove) {
    if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force; "Removed $bundle" } else { "Nothing to remove." }
    return
}

if (-not $SkipBuild) {
    dotnet build (Join-Path $root 'Engrave.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

$out = Join-Path $root 'bin\Release\net8.0-windows'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $root 'deploy\PackageContents.xml') $bundle -Force
Copy-Item (Join-Path $root 'deploy\Engrave.Inventor.addin') $target -Force
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $bundle -Force
foreach ($f in 'Engrave.dll', 'Engrave.pdb', 'Engrave.deps.json') {
    Copy-Item (Join-Path $out $f) $target -Force
}
"Installed to $bundle"
"Start Inventor 2025; the Engrave button is on the Sheet Metal tab of a part and the Assemble tab of an assembly."
