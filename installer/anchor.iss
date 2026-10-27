; Anchor Inno Setup script
; Builds the installer for the Anchor Windows tray application.
; Compile via scripts\build-installer.ps1 (which publishes the app first).

#define MyAppName "Anchor"
#define MyAppVersion "1.1.0"
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
; Self-contained single-file publish still emits a few native .dll neighbors, so package everything.
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

// Remove the toast registration that ToastNotificationManagerCompat
// (Microsoft.Toolkit.Uwp.Notifications 7.1.3) writes on first use: the key
// Software\Classes\AppUserModelId\<AUMID>, whose CustomActivator value names a
// COM class Software\Classes\CLSID\{<guid>} with LocalServer32 = "<exe>" <arg>
// (and the same CLSID plus AppID\{<guid>} under HKLM when Anchor ran elevated),
// and the icon it caches in %LOCALAPPDATA%\ToastNotificationManagerCompat\Apps.
// Windows derives the AUMID from the exe path, so find the registration by the
// activator that launches this install's exe instead of computing the AUMID.
// The 64-bit app writes Classes\CLSID in the 64-bit view, which a 32-bit
// process would otherwise see redirected to Wow6432Node.
procedure RemoveToastRegistration;
var
  Aumids: TArrayOfString;
  I: Integer;
  ExeArg, AumidKey, Activator, Server, IconFolder: String;
begin
  ExeArg := Lowercase('"' + ExpandConstant('{app}\{#MyAppExeName}') + '"');
  if not RegGetSubkeyNames(HKEY_CURRENT_USER_64, 'Software\Classes\AppUserModelId', Aumids) then
    Exit;

  for I := 0 to GetArrayLength(Aumids) - 1 do
  begin
    AumidKey := 'Software\Classes\AppUserModelId\' + Aumids[I];
    if RegQueryStringValue(HKEY_CURRENT_USER_64, AumidKey, 'CustomActivator', Activator) and
       RegQueryStringValue(HKEY_CURRENT_USER_64, 'Software\Classes\CLSID\' + Activator + '\LocalServer32', '', Server) and
       (Pos(ExeArg, Lowercase(Server)) = 1) then
    begin
      RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER_64, AumidKey);
      RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER_64, 'Software\Classes\CLSID\' + Activator);
      // Windows keeps the user's notification preferences for the AUMID here.
      RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER_64,
        'Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\' + Aumids[I]);

      if RegQueryStringValue(HKEY_LOCAL_MACHINE_64, 'Software\Classes\CLSID\' + Activator + '\LocalServer32', '', Server) and
         (Pos(ExeArg, Lowercase(Server)) = 1) then
      begin
        RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE_64, 'Software\Classes\CLSID\' + Activator);
        RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE_64, 'Software\Classes\AppID\' + Activator);
      end;

      // The toolkit names the icon folder after the AUMID, or after the CLSID
      // (without braces) when the AUMID holds a slash.
      if Pos('/', Aumids[I]) > 0 then
        IconFolder := Copy(Activator, 2, Length(Activator) - 2)
      else
        IconFolder := Aumids[I];
      DelTree(ExpandConstant('{localappdata}\ToastNotificationManagerCompat\Apps\') + IconFolder, True, True, True);
    end;
  end;
end;

// Remove the app-owned autostart value and toast registration on uninstall.
// Done at runtime (not in a [Registry] section) so the admin-install compile
// stays free of per-user-area warnings, and so the values are removed from the
// uninstalling user's hive.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'Anchor');
    RemoveToastRegistration;
  end;
end;

// Note: the user's config at %APPDATA%\Anchor is intentionally left in place on uninstall.
