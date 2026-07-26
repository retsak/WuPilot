#ifndef AppVersion
  #define AppVersion "0.3.1"
#endif

#ifndef AppArch
  #define AppArch "x64"
#endif

#if AppArch == "x64"
  #ifndef AppSourceDir
    #define AppSourceDir "..\artifacts\WuPilot-win-x64"
  #endif
  #define ArchitectureDisplayName "x64"
#elif AppArch == "arm64"
  #ifndef AppSourceDir
    #define AppSourceDir "..\artifacts\WuPilot-win-arm64"
  #endif
  #define ArchitectureDisplayName "ARM64"
#else
  #error Unsupported AppArch. Use x64 or arm64.
#endif

[Setup]
AppId={{D24C3645-F453-41A0-81C9-EE121034BBE4}
AppName=WuPilot
AppVersion={#AppVersion}
AppVerName=WuPilot {#AppVersion} ({#ArchitectureDisplayName})
AppPublisher=Joseph Kaster
AppPublisherURL=https://github.com/retsak/WuPilot
AppSupportURL=https://github.com/retsak/WuPilot/issues
AppUpdatesURL=https://github.com/retsak/WuPilot/releases
AppCopyright=Copyright (c) 2026 Joseph Kaster
VersionInfoCompany=Joseph Kaster
VersionInfoDescription=WuPilot Windows Update Workbench Installer
VersionInfoProductName=WuPilot
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\WuPilot
DefaultGroupName=WuPilot
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=admin
MinVersion=10.0.17763
WizardStyle=modern dynamic windows11 includetitlebar
WizardSizePercent=110
SetupIconFile=..\src\WuPilot.App\Assets\WuPilot.ico
WizardSmallImageFile=..\src\WuPilot.App\Assets\WuPilot-256.png
WizardSmallImageFileDynamicDark=..\src\WuPilot.App\Assets\WuPilot-256.png
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=WuPilot-{#AppVersion}-win-{#AppArch}-setup
UninstallDisplayIcon={app}\WuPilot.exe
UninstallDisplayName=WuPilot
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes

#if AppArch == "x64"
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible and not arm64
#else
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autoupdates"; Description: "Check for stable WuPilot updates when the application launches"; GroupDescription: "Update preferences:"; Flags: checkedonce

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WuPilot"; Filename: "{app}\WuPilot.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\WuPilot"; Filename: "{app}\WuPilot.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\WuPilot"; ValueType: dword; ValueName: "AutomaticUpdateChecks"; ValueData: "1"; Tasks: autoupdates; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\WuPilot"; ValueType: dword; ValueName: "AutomaticUpdateChecks"; ValueData: "0"; Tasks: not autoupdates; Flags: uninsdeletekey

[Run]
Filename: "{app}\WuPilot.exe"; Description: "{cm:LaunchProgram,WuPilot}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent shellexec
