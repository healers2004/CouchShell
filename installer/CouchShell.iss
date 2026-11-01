; The Windows installer. Build it with tools\build-installer.ps1, which passes in AppVersion and the
; folder holding the published program. It installs for the current user, so it needs no admin rights.

#ifndef AppVersion
  #error Build this with tools\build-installer.ps1
#endif

[Setup]
; Identifies the program to Windows across versions, so never change it.
AppId={{6F3B0E52-8C1D-4B7A-9E64-2D5A71C4F0A8}
AppName=CouchShell
AppVersion={#AppVersion}
AppPublisher=Vaasu Jindal
AppPublisherURL=https://github.com/healers2004/CouchShell
DefaultDirName={localappdata}\Programs\CouchShell
DefaultGroupName=CouchShell
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\LICENSE
OutputDir=..\out
OutputBaseFilename=CouchShellSetup-{#AppVersion}
SetupIconFile=..\ui\CouchShell\Assets\app.ico
UninstallDisplayIcon={app}\CouchShell.exe
UninstallDisplayName=CouchShell
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"
Name: startup; Description: "Start CouchShell when I sign in"; Flags: unchecked

[Files]
Source: "{#PublishFolder}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CouchShell"; Filename: "{app}\CouchShell.exe"
Name: "{userdesktop}\CouchShell"; Filename: "{app}\CouchShell.exe"; Tasks: desktopicon

[Registry]
; A value under Run needs no admin rights, and goes away on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CouchShell"; ValueData: """{app}\CouchShell.exe"" --at-logon"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\CouchShell.exe"; Description: "Open CouchShell now"; Flags: nowait postinstall skipifsilent

[Code]
// Stops this installed copy's programs, found by their full path so that nothing else with the
// same name is touched. They hold their files open, which would block an update or uninstall.
procedure StopRunningCopy(const Folder: String);
var
  Command: String;
  ResultCode: Integer;
begin
  Command := '-NoProfile -NonInteractive -Command "Get-Process CouchShell,couchd,tachyon -ErrorAction SilentlyContinue | '
    + 'Where-Object { $_.Path -like ''' + Folder + '\*'' } | Stop-Process -Force; Start-Sleep -Milliseconds 500"';
  Exec('powershell.exe', Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCopy(ExpandConstant('{app}'));
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningCopy(ExpandConstant('{app}'));
  Result := True;
end;

// Settings and history are the user's, so they're only removed on request. A silent uninstall keeps them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() then
    if MsgBox('Also delete your CouchShell settings, history and logs?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\CouchShell'), True, True, True);
end;
