; Inno Setup script for the Windows installer. scripts\package.ps1 builds it:
;   iscc /DAppVersion=0.1.0 /DSourceDir=<published app> /DOutputDir=<folder> installer\OpenDownloadManager.iss
;
; It installs for the current user only, into %LOCALAPPDATA%\Programs, with no admin rights: the
; app registers its browser connection under HKCU, so a per-user install is all it needs.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist\release"
#endif

#define AppName "Open Download Manager"
#define AppExe "OpenDownloadManager.exe"
#define HostName "org.opendownloadmanager.host"

[Setup]
AppId={{BB9178D5-CD06-464A-B895-CF31D0AEA953}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Open Download Manager contributors
AppPublisherURL=https://github.com/Shadiul/open-download-manager
AppSupportURL=https://github.com/Shadiul/open-download-manager/issues
AppUpdatesURL=https://github.com/Shadiul/open-download-manager/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
LicenseFile=..\LICENSE
SetupIconFile=..\windows\OpenDownloadManager\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename=OpenDownloadManager-{#AppVersion}-setup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files from an older version that this one no longer ships.
Type: filesandordirs; Name: "{app}\tools"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitApp"

[UninstallDelete]
; The browser connection's manifest. Downloads, the download list and settings stay.
Type: files; Name: "{localappdata}\OpenDownloadManager\{#HostName}.json"

[Code]
// Close a running copy before files are replaced. "--quit" asks the app to save and exit;
// Restart Manager (CloseApplications) handles anything still holding a file after that.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
  begin
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1500);
  end;
end;

// The app writes its browser connection and "start with Windows" entry itself on every start;
// the uninstaller takes them away again.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Google\Chrome\NativeMessagingHosts\{#HostName}');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Edge\NativeMessagingHosts\{#HostName}');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'OpenDownloadManager');
  end;
end;
