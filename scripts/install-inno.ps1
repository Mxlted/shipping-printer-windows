#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Inno Setup requires Windows.' }
$toolsDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) '.local/tools'
$compilerDirectory = Join-Path $toolsDirectory 'inno-6.7.3'
$compiler = Join-Path $compilerDirectory 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { Write-Output $compiler; return }

New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
$download = Join-Path $toolsDirectory 'innosetup-6.7.3.exe'
$expectedHash = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
if (-not (Test-Path -LiteralPath $download)) {
    Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download
}
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash) {
    throw "Inno Setup download checksum mismatch: $download"
}
$process = Start-Process -FilePath $download -WindowStyle Hidden -PassThru -Wait -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', '/TASKS=""',
    "/DIR=`"$compilerDirectory`""
)
if ($process.ExitCode -ne 0) { throw "Inno Setup installation exited with code $($process.ExitCode)." }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Inno Setup compiler was not installed.' }
Write-Output $compiler
