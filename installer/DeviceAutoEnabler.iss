; Inno Setup script for Device Auto Enabler.
; Builds a double-click installer that:
;   - installs the self-contained exe to Program Files,
;   - drops a default config into %ProgramData%\DeviceAutoEnabler\ (only if absent),
;   - locks down that folder's ACLs (Administrators + SYSTEM full, Users read-only),
;   - registers + starts the LocalSystem service,
;   - stops + removes the service on uninstall (user config is preserved).
;
; Build:  iscc /DSourceDir=<publish folder> /DMyAppVersion=1.2.3 installer\DeviceAutoEnabler.iss

#define MyAppName "Device Auto Enabler"
#define MyAppFolder "DeviceAutoEnabler"
#define MyAppPublisher "Device Auto Enabler"
#define MyServiceName "DeviceAutoEnabler"
#define MyExeName "DeviceAutoEnabler.exe"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

; Folder containing the published DeviceAutoEnabler.exe. Overridable from the command line.
#ifndef SourceDir
  #define SourceDir "..\src\DeviceAutoEnabler\bin\Release\net8.0-windows\win-x64\publish"
#endif

[Setup]
AppId={{7C4A1B2E-9F3D-4E8A-8B21-DEV1CEAUT0EN}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppFolder}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyExeName}
OutputDir=..\dist
OutputBaseFilename=DeviceAutoEnabler-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Device management requires elevation; the service itself runs as LocalSystem.
PrivilegesRequired=admin
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; The self-contained single-file service executable.
Source: "{#SourceDir}\{#MyExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Ship the example config alongside the app as a reference copy.
Source: "..\config\config.example.json"; DestDir: "{app}"; Flags: ignoreversion
; Place the active config into ProgramData only if it does not already exist,
; and never remove it on uninstall (preserves the user's device list).
Source: "..\config\config.example.json"; DestDir: "{commonappdata}\{#MyServiceName}"; DestName: "config.json"; Flags: onlyifdoesntexist uninsneveruninstall

[Dirs]
Name: "{commonappdata}\{#MyServiceName}"
Name: "{commonappdata}\{#MyServiceName}\logs"

[Run]
; 1) Lock down ProgramData folder ACLs using well-known SIDs (language-independent):
;    Administrators (S-1-5-32-544) + SYSTEM (S-1-5-18) = full control,
;    Users (S-1-5-32-545) = read & execute only. Inheritance is removed first.
Filename: "{sys}\icacls.exe"; \
  Parameters: """{commonappdata}\{#MyServiceName}"" /inheritance:r /grant:r ""*S-1-5-32-544:(OI)(CI)F"" /grant:r ""*S-1-5-18:(OI)(CI)F"" /grant:r ""*S-1-5-32-545:(OI)(CI)RX"" /T /C"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Securing configuration folder..."

; 1b) Let interactive users edit ONLY config.json directly. SYSTEM (S-1-5-18) and Administrators (S-1-5-32-544) keep full control so the
;     service can always read it; the logs folder stays read-only for Users. This deliberately
;     lets a standard user change which devices the service enables (accepted tradeoff).
Filename: "{sys}\icacls.exe"; \
  Parameters: """{commonappdata}\{#MyServiceName}\config.json"" /grant ""*S-1-5-32-545:M"" /C"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Granting configuration edit permission..."

; 2) Register and start the service (LocalSystem, auto-start) via the app's own install verb.
Filename: "{app}\{#MyExeName}"; Parameters: "install"; Flags: runhidden waituntilterminated; StatusMsg: "Registering and starting the service..."

[UninstallRun]
; Stop and remove the service before the files are deleted.
Filename: "{app}\{#MyExeName}"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[UninstallDelete]
; Remove rolling logs on uninstall; the user config is intentionally kept.
Type: filesandordirs; Name: "{commonappdata}\{#MyServiceName}\logs"
