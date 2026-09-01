#preproc ispp

#ifndef MyAppVersion
  #error MyAppVersion must be supplied by eng/build-installer.ps1
#endif

#ifndef MyAppVersionQuad
  #error MyAppVersionQuad must be supplied by eng/build-installer.ps1
#endif

#ifndef PublishDir
  #error PublishDir must be supplied by eng/build-installer.ps1
#endif

#ifndef OutputDir
  #error OutputDir must be supplied by eng/build-installer.ps1
#endif

#define MyAppName "Project File Hub"
#define MyAppPublisher "Anjero"
#define MyAppExeName "ProjectFileHub.exe"
#define MyAppUrl "https://github.com/anjero-sudo/Project-File-Hub"

[Setup]
AppId={{7370CC21-B0E6-48EF-92D4-B25D513BD1CC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
AppContact={#MyAppUrl}/issues
AppCopyright=Copyright (C) 2026 Anjero
DefaultDirName={localappdata}\Programs\ProjectFileHub
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=ProjectFileHub-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\src\ProjectFileHub.App\Assets\ProjectFileHub.ico
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\app\{#MyAppExeName}
UninstallFilesDir={app}\uninstall
VersionInfoVersion={#MyAppVersionQuad}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
WizardStyle=modern
DisableWelcomePage=no
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName},*.dll,*.xbf,*.pri
RestartApplications=no
UsePreviousAppDir=yes
ChangesAssociations=no
ChangesEnvironment=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; The application payload is installer-owned and isolated below {app}\app. Replacing
; this exact subdirectory prevents stale self-contained runtime files on upgrade while
; preserving settings, project registries, indexes, and any legacy version directories.
Type: filesandordirs; Name: "{app}\app"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\app\{#MyAppExeName}"; WorkingDir: "{app}\app"; IconFilename: "{app}\app\{#MyAppExeName}"; Comment: "{#MyAppName}"; Check: not IsSmokeTest
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\app\{#MyAppExeName}"; WorkingDir: "{app}\app"; IconFilename: "{app}\app\{#MyAppExeName}"; Comment: "{#MyAppName}"; Tasks: desktopicon; Check: not IsSmokeTest

[Run]
Filename: "{app}\app\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; WorkingDir: "{app}\app"; Flags: nowait postinstall skipifsilent

[Code]
const
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupValueName = 'ProjectFileHub';

var
  MigrateStartupRegistration: Boolean;

function IsSmokeTest(): Boolean;
begin
  Result := CompareText(ExpandConstant('{param:PFHSMOKETEST|0}'), '1') = 0;
end;

function InstalledExecutablePath: String;
begin
  Result := ExpandConstant('{app}\app\{#MyAppExeName}');
end;

function StartupValueTargetsInstalledApp(const Value: String): Boolean;
var
  InstalledPath: String;
begin
  InstalledPath := InstalledExecutablePath;
  Result := (CompareText(Value, InstalledPath) = 0) or
    (CompareText(Value, '"' + InstalledPath + '"') = 0);
end;

function InitializeSetup(): Boolean;
var
  ExistingValue: String;
begin
  MigrateStartupRegistration :=
    (not IsSmokeTest) and
    RegQueryStringValue(HKCU, StartupKey, StartupValueName, ExistingValue);
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and MigrateStartupRegistration then
    RegWriteStringValue(
      HKCU,
      StartupKey,
      StartupValueName,
      '"' + InstalledExecutablePath + '"');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExistingValue: String;
begin
  if (CurUninstallStep = usUninstall) and
     RegQueryStringValue(HKCU, StartupKey, StartupValueName, ExistingValue) and
     StartupValueTargetsInstalledApp(ExistingValue) then
    RegDeleteValue(HKCU, StartupKey, StartupValueName);
end;
