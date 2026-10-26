#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipChecks,
    [string]$InnoSetupCompiler
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $InnoSetupCompiler) {
    $InnoSetupCompiler = Join-Path $projectRoot '.local/tools/inno-6.7.3/ISCC.exe'
}
if (-not (Test-Path -LiteralPath $InnoSetupCompiler -PathType Leaf)) {
    throw 'Inno Setup compiler not found. Run scripts/install-inno.ps1 or pass -InnoSetupCompiler with the path to ISCC.exe.'
}
$InnoSetupCompiler = (Resolve-Path -LiteralPath $InnoSetupCompiler).Path
& (Join-Path $PSScriptRoot 'build.ps1') -SkipChecks:$SkipChecks
$stagingRoot = $null
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
    Compress-Archive -LiteralPath $publishDirectory -DestinationPath (Join-Path $stagingRoot "$packageName.zip")
    & $InnoSetupCompiler /Qp "/DAppVersion=$version" "/DPublishDirectory=$publishDirectory" `
        "/DOutputDirectory=$stagingRoot" (Join-Path $projectRoot 'installer/ShippingPrinter.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    # Publish outputs only after both formats have built successfully.
    foreach ($fileName in @("$packageName.zip", "$packageName-Setup.exe")) {
        $source = Join-Path $stagingRoot $fileName
        $destination = Join-Path $distRoot $fileName
        $digest = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        Move-Item -LiteralPath $source -Destination $destination -Force
        "$digest  $fileName" | Set-Content -LiteralPath "$destination.sha256" -Encoding ascii
        Write-Host "Package: $destination"
        Write-Host "SHA-256: $digest"
    }
} finally {
    if ($stagingRoot -and (Test-Path -LiteralPath $stagingRoot)) {
        $resolvedStaging = (Resolve-Path -LiteralPath $stagingRoot).Path
        $expectedParent = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
        if ((Split-Path $resolvedStaging -Parent) -ne $expectedParent -or
            (Split-Path $resolvedStaging -Leaf) -notlike '.staging-*') {
            throw "Refusing to remove unexpected staging path: $resolvedStaging"
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
    Pop-Location
}
