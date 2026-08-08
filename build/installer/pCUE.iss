; Inno Setup script for pCUE (unsigned by default).
; Wraps the staged Release build into a single setup .exe with a Program Files install,
; Start-Menu (+ optional desktop) shortcut, and an uninstall entry.
;
; Not meant to be run by hand - build\pack-release.ps1 invokes it as:
;   ISCC /DMyAppVersion=<ver> /DPublishDir=<abs path to staged build> pCUE.iss
; Both defines fall back to sensible values so it still compiles standalone.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0.0"
#endif
#define RepoRoot SourcePath + "..\.."
#ifndef PublishDir
  #define PublishDir RepoRoot + "\artifacts\stage\pCUE"
#endif

#define MyAppName "pCUE"
#define MyAppPublisher "Cybenetics LTD"
#define MyAppExeName "pCUE.exe"
#define MyAppUrl "https://github.com/crmaris/pCUE"
#define MySrcDir PublishDir

[Setup]
; Stable identity - NEVER change this GUID, it is how Windows recognises an in-place upgrade.
AppId={{FFD36531-9723-41F5-B7F1-EF00D5E83765}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
OutputDir={#RepoRoot}\artifacts
OutputBaseFilename=pCUE_{#MyAppVersion}_setup
SetupIconFile={#RepoRoot}\pCUE\small.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; pCUE is AnyCPU but requires admin at runtime (app.manifest requireAdministrator), so the
; installer elevates too and lands in the native Program Files on 64-bit Windows.
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; The app runs elevated and holds a HID handle; an in-place update must be able to replace
; pCUE.exe, so let Restart Manager close a running instance first.
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MySrcDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Registry]
; pCUE writes its own Run-key entry when "Auto Start" is ticked. Clean it up on UNINSTALL only
; (uninsdeletevalue), so an uninstalled app cannot keep trying to launch. Nothing is touched at
; install time - an in-place update must preserve the user's Auto Start choice.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "pCUE"; \
    ValueType: none; Flags: uninsdeletevalue
