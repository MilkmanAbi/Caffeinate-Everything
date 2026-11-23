; Inno Setup script for caffeinate (Windows). Built by build.ps1 -Installer, once per architecture
; (the version comes from caffeinate.csproj):
;   ISCC.exe /DArch=x64   /DAppVersion=1.2.0 caffeinate.iss
;   ISCC.exe /DArch=arm64 /DAppVersion=1.2.0 caffeinate.iss
;
; Installs per user by default (no admin), like caffeinate itself; "all users" is offered on the first page.
; The uninstaller removes schedules, restores any settings caffeinate changed, and deletes its data.

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

[Setup]
; One AppId for both architectures: installing one over the other upgrades instead of duplicating.
AppId={{716F32B2-0987-4131-857A-A69160FE1973}
AppName=caffeinate
AppVersion={#AppVersion}
AppVerName=caffeinate {#AppVersion}
AppPublisher=MilkmanAbi
AppPublisherURL=https://github.com/MilkmanAbi/Caffeinate-Everything
AppSupportURL=https://github.com/MilkmanAbi/Caffeinate-Everything/issues
AppUpdatesURL=https://github.com/MilkmanAbi/Caffeinate-Everything/releases
DefaultDirName={autopf}\caffeinate
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
ChangesEnvironment=yes
CloseApplications=yes
OutputDir=..\publish\installers
OutputBaseFilename=caffeinate-{#AppVersion}-windows-{#Arch}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayName=caffeinate {#AppVersion} ({#Arch})
UninstallDisplayIcon={app}\caffeinate.exe
VersionInfoVersion={#AppVersion}
VersionInfoProductName=caffeinate
VersionInfoDescription=caffeinate for Windows setup ({#Arch})

[Tasks]
Name: "addtopath"; Description: "Add caffeinate to &PATH (so it runs from any terminal)"
Name: "updatecheck"; Description: "Tell me when a new &version is out (checks GitHub once a day)"

[Files]
Source: "..\publish\win-{#Arch}\caffeinate.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; Refresh the windowless agent used by saved schedules, and set the update-check preference.
Filename: "{app}\caffeinate.exe"; Parameters: "--post-install"; Flags: runhidden waituntilterminated runasoriginaluser
Filename: "{app}\caffeinate.exe"; Parameters: "--update-check on"; Tasks: updatecheck; Flags: runhidden waituntilterminated runasoriginaluser
Filename: "{app}\caffeinate.exe"; Parameters: "--update-check off"; Tasks: not updatecheck; Flags: runhidden waituntilterminated runasoriginaluser

[UninstallRun]
; Remove saved schedules (and their scheduled tasks), put back any setting a killed caffeinate left
; changed, then stop the background watcher, before the files go away.
Filename: "{app}\caffeinate.exe"; Parameters: "--remove all"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveSchedules"
Filename: "{app}\caffeinate.exe"; Parameters: "--restore"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreSettings"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im caffeinate-agent.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Caffeinate"

[Messages]
FinishedLabel=caffeinate is installed.%n%nOpen a NEW terminal window and run:%n%n    caffeinate -dismu%n    caffeinate --help%n%nTo update later: caffeinate --update

[Code]
const
  UserEnvKey = 'Environment';
  SystemEnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function EnvRoot: Integer;
begin
  if IsAdminInstallMode then Result := HKEY_LOCAL_MACHINE else Result := HKEY_CURRENT_USER;
end;

function EnvKey: String;
begin
  if IsAdminInstallMode then Result := SystemEnvKey else Result := UserEnvKey;
end;

{ ;-delimited, case-insensitive, ignoring a trailing backslash }
function PathHas(Paths, Dir: String): Boolean;
begin
  Result := Pos(';' + Uppercase(RemoveBackslashUnlessRoot(Dir)) + ';',
                ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToPath(Dir: String);
var
  Paths: String;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then Paths := '';
  if PathHas(Paths, Dir) then Exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then Paths := Paths + ';';
  Paths := Paths + RemoveBackslashUnlessRoot(Dir);
  if not RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Paths) then
    Log('Could not add to PATH: ' + Dir);
end;

procedure RemoveFromPath(Dir: String);
var
  Paths, Wrapped, Needle: String;
  P: Integer;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then Exit;
  Wrapped := ';' + Paths + ';';
  Needle := ';' + RemoveBackslashUnlessRoot(Dir) + ';';
  P := Pos(Uppercase(Needle), Uppercase(Wrapped));
  if P = 0 then Exit;
  { drop ";Dir" and keep the following ';' }
  Delete(Wrapped, P, Length(Needle) - 1);
  Paths := Copy(Wrapped, 2, Length(Wrapped) - 2);
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
