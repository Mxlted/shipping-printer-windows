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
    dotnet build tests/ShippingPrinter.Checks/ShippingPrinter.Checks.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipChecks) {
        dotnet run --project tests/ShippingPrinter.Checks/ShippingPrinter.Checks.csproj -c Release --no-build -- artifacts/checks
        if ($LASTEXITCODE -ne 0) { throw 'Offline checks failed.' }
    }
} finally {
    Pop-Location
}
