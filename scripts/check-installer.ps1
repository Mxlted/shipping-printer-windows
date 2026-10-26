#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Installer checks require a Windows desktop.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/ShippingPrinter.Windows/ShippingPrinter.Windows.csproj') -Raw)).Project.PropertyGroup.Version
$packageName = "ShippingPrinter-$version-win-x64"
$setup = Join-Path $projectRoot "dist/$packageName-Setup.exe"
$archive = Join-Path $projectRoot "dist/$packageName.zip"
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7CB210E3-9407-465A-B89C-B79617317B45}_is1'
# Never replace or uninstall a real installation during a check.
if (Test-Path -LiteralPath $uninstallKey) { throw 'Shipping Printer is already installed for this user. Run this check in a separate Windows account.' }
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Shipping Printer/Shipping Printer.lnk'
if (Test-Path -LiteralPath $shortcut) { throw 'A Shipping Printer Start menu shortcut already exists. Run this check in a separate Windows account.' }
foreach ($file in @($setup, $archive)) {
    $expected = (Get-Content -LiteralPath "$file.sha256").Split(' ')[0]
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) { throw "Checksum mismatch: $file" }
}

$testRoot = Join-Path $projectRoot ('artifacts/installer-check-' + [guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $testRoot 'installed'
$profileDirectory = Join-Path $testRoot 'profile'
New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
$uninstaller = Join-Path $installDirectory 'unins000.exe'
$oldProfile = $env:SHIPPING_PRINTER_DATA
$app = $null

function Invoke-SetupProcess([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw "Installer timed out. Inspect $testRoot before cleaning up." }
    if ($process.ExitCode -ne 0) { throw "Installer process exited with code $($process.ExitCode). See $testRoot." }
}

try {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/RESTARTEXITCODE=3010', '/TASKS=""', "/DIR=`"$installDirectory`"")
    Invoke-SetupProcess $setup ($arguments + "/LOG=`"$testRoot/install.log`"")
    $registration = Get-ItemProperty -LiteralPath $uninstallKey
    if ($registration.DisplayVersion -ne $version -or $registration.InstallLocation.TrimEnd('\') -ne $installDirectory) {
        throw 'Installed-app registration has the wrong version or location.'
    }
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Start menu shortcut was not created.' }
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcut).TargetPath -ne (Join-Path $installDirectory 'ShippingPrinter.exe')) {
        throw 'Start menu shortcut points to the wrong executable.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $testRoot 'portable')
    $portableRoot = Join-Path $testRoot "portable/$packageName"
    $files = @(Get-ChildItem -LiteralPath $portableRoot -File -Recurse)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($portableRoot, $file.FullName)
        $installed = Join-Path $installDirectory $relative
        if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "Installer and portable contents differ: $relative"
        }
    }
    Write-Host "PASS Install, registration, shortcut, and $($files.Count) matching payload files"

    $env:SHIPPING_PRINTER_DATA = $profileDirectory
    $app = Start-Process -FilePath (Join-Path $installDirectory 'ShippingPrinter.exe') -WindowStyle Hidden -PassThru
    if (-not $app.WaitForInputIdle(15000)) { throw 'Installed app did not become ready.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 100
        $app.Refresh()
    } while (-not $app.HasExited -and $app.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($app.HasExited -or $app.MainWindowHandle -eq 0) { throw 'Installed app did not create its main window.' }
    if (-not $app.CloseMainWindow() -or -not $app.WaitForExit(15000)) { throw 'Installed app did not close cleanly.' }
    if ($app.ExitCode -ne 0) { throw "Installed app exited with code $($app.ExitCode)." }
    Write-Host 'PASS Installed app launch and clean shutdown'

    $settings = Join-Path $profileDirectory 'settings.json'
    if (-not (Test-Path -LiteralPath $settings)) { throw 'App did not save its isolated preferences.' }
    $settingsHash = (Get-FileHash -LiteralPath $settings).Hash
    # Unknown files in the install folder must survive an update and uninstall.
    $sentinel = Join-Path $installDirectory 'user-document.txt'
    Set-Content -LiteralPath $sentinel -Value 'Keep user-created files.'
    Invoke-SetupProcess $setup ($arguments + "/LOG=`"$testRoot/reinstall.log`"")
    if (@(Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*.exe').Count -ne 1) { throw 'Reinstall created duplicate uninstallers.' }
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash) { throw 'Reinstall changed preferences.' }
    Write-Host 'PASS Reinstall with preferences retained'

    Invoke-SetupProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$testRoot/uninstall.log`"")
    if ((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $shortcut)) { throw 'Uninstall left registration or shortcut behind.' }
    foreach ($file in $files) {
        if (Test-Path -LiteralPath (Join-Path $installDirectory ([IO.Path]::GetRelativePath($portableRoot, $file.FullName)))) {
            throw "Uninstall left a payload file behind: $($file.Name)"
        }
    }
    if (-not (Test-Path -LiteralPath $sentinel) -or (Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash) {
        throw 'Uninstall removed user files or changed preferences.'
    }
    Write-Host 'PASS Uninstall removes payload and registration, retaining user files and preferences'
    Write-Host "Installer lifecycle checks passed. Logs: $testRoot"
} finally {
    $env:SHIPPING_PRINTER_DATA = $oldProfile
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    if ((Test-Path -LiteralPath $uninstallKey) -and (Test-Path -LiteralPath $uninstaller)) {
        Invoke-SetupProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
}
