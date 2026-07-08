; Anchor Inno Setup script
; Builds the installer for the Anchor Windows tray application.
; Compile via scripts\build-installer.ps1 (which publishes the app first).

#define MyAppName "Anchor"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Telesphoreo"
#define MyAppURL "https://github.com/Telesphoreo/Anchor"
#define MyAppExeName "Anchor.exe"

[Setup]
AppId={{6F3A4C2E-1B7D-4E9A-9C31-8A5D2F0B7E44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\artifacts\installer
OutputBaseFilename=Anchor-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Requires admin to install to Program Files.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Close the running instance (matches the app's single-instance mutex) before install/uninstall.
CloseApplications=yes
RestartApplications=no
AppMutex=Anchor.SingleInstance
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "launchafterinstall"; Description: "Launch {#MyAppName} when setup finishes"; GroupDescription: "Options:"

[Files]
; Self-contained single-file publish still emits a few native .dll neighbors — package everything.
Source: "..\artifacts\publish\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

; Autostart is owned entirely by the app (it registers the per-user HKCU Run value
; on first launch and via Settings). The installer deliberately writes no per-user
; registry area, so an admin install stays machine-scoped. Uninstall cleanup of that
; value is done at runtime in [Code] (see CurUninstallStepChanged).

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent; Tasks: launchafterinstall

[UninstallRun]
; taskkill fallback in case the AppMutex close-down did not stop the process.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName} /T"; Flags: runhidden; RunOnceId: "KillAnchor"

[Code]
// Belt-and-suspenders: force-close the running app before files are copied,
// in addition to the AppMutex handling above.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName} /T', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Remove the app-owned autostart value on uninstall. Done at runtime (not in a
// [Registry] section) so the admin-install compile stays free of per-user-area
// warnings, and so the value is removed from the uninstalling user's hive.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'Anchor');
end;

// Note: the user's config at %APPDATA%\Anchor is intentionally left in place on uninstall.
