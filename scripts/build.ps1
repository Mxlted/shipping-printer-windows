#Requires -Version 7.0
[CmdletBinding()]
param([switch]$SkipChecks)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Shipping Printer builds and runs on Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    dotnet restore tests/ShippingPrinter.Checks/ShippingPrinter.Checks.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    # Exercise the same self-contained runtime used by the portable release.
    # Framework-dependent checks can silently select a different installed patch.
    dotnet build tests/ShippingPrinter.Checks/ShippingPrinter.Checks.csproj -c Release -r win-x64 --self-contained true --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipChecks) {
        dotnet run --project tests/ShippingPrinter.Checks/ShippingPrinter.Checks.csproj -c Release -r win-x64 --self-contained true --no-build -- artifacts/checks
        if ($LASTEXITCODE -ne 0) { throw "Offline checks process exited with code $LASTEXITCODE (0x$('{0:X8}' -f $LASTEXITCODE)). See artifacts/checks/checks.json." }
    }
} finally {
    Pop-Location
}
