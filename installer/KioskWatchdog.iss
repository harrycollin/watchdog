; Kiosk Watchdog — Inno Setup installer
; Builds a single Setup.exe that installs or upgrades in place (same AppId).
;
; Before compiling, publish the app on Windows:
;   dotnet publish src\KioskWatchdog.UI\KioskWatchdog.UI.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish
;
; Then:
;   ISCC.exe installer\KioskWatchdog.iss

#ifndef MyAppName
  #define MyAppName "Kiosk Watchdog"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyAppPublisher
  #define MyAppPublisher "KioskWatchdog"
#endif
#define MyAppExeName "KioskWatchdog.exe"
#define MyServiceName "KioskWatchdog"

[Setup]
AppId={{8F3C2A1E-9B47-4D6E-A1C0-7E5F2D9B8A10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\KioskWatchdog
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
UsePreviousSetupType=yes
CloseApplications=force
RestartApplications=no
OutputDir=..\artifacts\installer
OutputBaseFilename=KioskWatchdogSetup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
; Same AppId + this flag = upgrade existing install instead of a second copy
AllowNoIcons=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Single publish folder — one application, overwritten on upgrade
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\config\config.example.json"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\KioskWatchdog"; Flags: uninsneveruninstall
Name: "{commonappdata}\KioskWatchdog\logs"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Open Kiosk Watchdog"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove service registration leftovers only; keep ProgramData config/logs
Type: files; Name: "{app}\*.pdb"

[Code]
const
  ServiceName = '{#MyServiceName}';

function ExecOk(const Filename, Params: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(Filename, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function ExecQuiet(const Filename, Params: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(Filename, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure StopWatchdogService;
begin
  ExecQuiet('net.exe', 'stop ' + ServiceName);
  Sleep(1500);
end;

procedure EnsureEventSource;
begin
  { Best-effort; ignore failures on locked systems }
  ExecQuiet('powershell.exe',
    '-NoProfile -Command "if (-not [System.Diagnostics.EventLog]::SourceExists(''KioskWatchdog'')) { [System.Diagnostics.EventLog]::CreateEventSource(''KioskWatchdog'',''Application'') }"');
end;

procedure RegisterOrUpdateService;
var
  AppExe: string;
  BinPath: string;
begin
  AppExe := ExpandConstant('{app}\{#MyAppExeName}');
  { sc.exe wants the exe and its args inside one quoted binPath value }
  BinPath := '"' + AppExe + ' --service"';

  StopWatchdogService;

  if not ExecOk('sc.exe', 'query ' + ServiceName) then
  begin
    ExecOk('sc.exe',
      'create ' + ServiceName +
      ' binPath= ' + BinPath +
      ' start= auto' +
      ' DisplayName= "Kiosk Watchdog"');
  end
  else
  begin
    ExecOk('sc.exe', 'config ' + ServiceName + ' binPath= ' + BinPath + ' start= auto');
  end;

  ExecOk('sc.exe', 'description ' + ServiceName + ' "Monitors the kiosk application and restarts it on failure."');
  ExecOk('sc.exe', 'failure ' + ServiceName + ' reset= 86400 actions= process/60000/process/60000/process/60000');
  ExecOk('sc.exe', 'failureflag ' + ServiceName + ' 1');
  EnsureEventSource;
  ExecQuiet('net.exe', 'start ' + ServiceName);
end;

procedure RemoveService;
begin
  StopWatchdogService;
  ExecQuiet('sc.exe', 'delete ' + ServiceName);
  Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Stop service so files can be replaced on upgrade }
  StopWatchdogService;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigPath: string;
  ExamplePath: string;
begin
  if CurStep = ssPostInstall then
  begin
    ConfigPath := ExpandConstant('{commonappdata}\KioskWatchdog\config.json');
    ExamplePath := ExpandConstant('{app}\config.example.json');
    { Never overwrite existing config on upgrade }
    if (not FileExists(ConfigPath)) and FileExists(ExamplePath) then
      FileCopy(ExamplePath, ConfigPath, False);

    RegisterOrUpdateService;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveService;
end;
