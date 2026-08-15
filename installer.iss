; dsh-tray Windows installer (per-user, no admin/UAC required)
#define MyAppName "DeepSeek Harness Tray"
#define MyAppVersion "0.1.2"
#define MyAppPublisher "dsh-tray"
#define MyAppExeName "dsh-tray.exe"

[Setup]
AppId={{C2E7A0B4-5D1F-4A6E-9B3C-8F0D2A5E6C7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\DeepSeek Harness Tray
OutputBaseFilename=dsh-tray-setup
OutputDir=.
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
