#ifndef AppVersion
  #error AppVersion must be supplied by scripts/package.ps1
#endif
#ifndef PublishDirectory
  #error PublishDirectory must be supplied by scripts/package.ps1
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied by scripts/package.ps1
#endif

[Setup]
; Keep this ID stable so future installers update the existing installation.
AppId={{7CB210E3-9407-465A-B89C-B79617317B45}
AppName=Shipping Printer
AppVersion={#AppVersion}
AppPublisher=Mxlted
AppPublisherURL=https://github.com/Mxlted/shipping-printer-windows
AppSupportURL=https://github.com/Mxlted/shipping-printer-windows/issues
AppUpdatesURL=https://github.com/Mxlted/shipping-printer-windows/releases/latest
DefaultDirName={localappdata}\Programs\Shipping Printer
DefaultGroupName=Shipping Printer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir={#OutputDirectory}
OutputBaseFilename=ShippingPrinter-{#AppVersion}-win-x64-Setup
SetupIconFile=..\src\ShippingPrinter.Windows\Assets\app.ico
UninstallDisplayIcon={app}\ShippingPrinter.exe
UninstallDisplayName=Shipping Printer
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Shipping Printer"; Filename: "{app}\ShippingPrinter.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Shipping Printer"; Filename: "{app}\ShippingPrinter.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ShippingPrinter.exe"; Description: "Open Shipping Printer"; Flags: nowait postinstall skipifsilent

; Preferences live outside {app}. Uninstall removes installed files only.
