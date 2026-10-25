#Requires -Version 7.0
[CmdletBinding()]
param([switch]$SkipChecks)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1') -SkipChecks:$SkipChecks
Push-Location $projectRoot
try {
    $projectFile = 'src/ShippingPrinter.Windows/ShippingPrinter.Windows.csproj'
    $version = ([xml](Get-Content -LiteralPath $projectFile -Raw)).Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a stable semantic version in the project file.' }
    $packageName = "ShippingPrinter-$version-win-x64"
    $distRoot = Join-Path $projectRoot 'dist'
    # Each run publishes to a new directory so old files cannot enter the archive.
    $stagingRoot = Join-Path $distRoot ('.staging-' + [guid]::NewGuid().ToString('N'))
    $publishDirectory = Join-Path $stagingRoot $packageName
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    dotnet publish $projectFile -c Release -r win-x64 --self-contained true --no-restore `
        -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    foreach ($name in @('README.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $publishDirectory
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $publishDirectory -Recurse
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $publishDirectory -Recurse
    $licenseDirectory = Join-Path $publishDirectory 'licenses'
    $assets = Get-Content -LiteralPath 'src/ShippingPrinter.Windows/obj/project.assets.json' -Raw | ConvertFrom-Json -AsHashtable
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'ShippingPrinter.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $prefix = "$($framework.name).Runtime.win-x64"
        $relativePackagePath = "$($prefix.ToLowerInvariant())/$($framework.version)"
        $packagePath = $null
        foreach ($cache in $assets.packageFolders.Keys) {
            $candidate = Join-Path $cache $relativePackagePath
            if (Test-Path -LiteralPath $candidate) { $packagePath = $candidate; break }
        }
        if (-not $packagePath) { throw "Runtime package not found: $relativePackagePath" }
        foreach ($license in Get-ChildItem -LiteralPath $packagePath -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$') {
            Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenseDirectory "$prefix-$($license.Name)")
        }
    }
    foreach ($required in @('ShippingPrinter.exe', 'ShippingPrinter.dll', 'coreclr.dll', 'PresentationFramework.dll', 'licenses/Microsoft.NETCore.App.Runtime.win-x64-LICENSE.TXT', 'licenses/Microsoft.WindowsDesktop.App.Runtime.win-x64-LICENSE')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required))) { throw "Missing release file: $required" }
    }
    $archive = Join-Path $distRoot "$packageName.zip"
    Compress-Archive -LiteralPath $publishDirectory -DestinationPath $archive -Force
    $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$digest  $packageName.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Host "Portable app: $publishDirectory"
    Write-Host "Archive:      $archive"
    Write-Host "SHA-256:      $digest"
} finally {
    Pop-Location
}
