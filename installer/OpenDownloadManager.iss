; Inno Setup script for the Windows installer. scripts\package.ps1 builds it:
;   iscc /DAppVersion=0.1.0 /DSourceDir=<published app> /DOutputDir=<folder> installer\OpenDownloadManager.iss
;
; It installs for the current user by default, into %LOCALAPPDATA%\Programs, with no admin rights:
; the app registers its browser connection under HKCU, so a per-user install is all it needs.
;
; The wizard is kept short: install mode, one welcome page that holds the few choices (folder,
; desktop shortcut, licence) and the Install button, then progress and Finish. The pictures in
; installer\art are made from the app icon by installer\art\render.ps1. Needs Inno Setup 6.6 or later.

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
; The folder is shown, and can be changed, on the welcome page.
DisableDirPage=yes
DisableWelcomePage=no
DisableReadyPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile=..\windows\OpenDownloadManager\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename=OpenDownloadManager-{#AppVersion}-setup-x64
Compression=lzma2/max
SolidCompression=yes
; Light or dark, following Windows.
WizardStyle=modern dynamic
WizardImageFile=art\wizard-*.png
WizardImageFileDynamicDark=art\wizard-dark-*.png
WizardSmallImageFile=art\small-*.png
WizardSmallImageFileDynamicDark=art\small-*.png
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Install [name]
WelcomeLabel2=Fast downloads that pause and resume any time, and videos from the sites you use.
FinishedHeadingLabel=[name] is installed
FinishedLabelNoIcons=It's in the Start menu.
FinishedLabel=It's in the Start menu.
ClickFinish=
ClickNext=

[CustomMessages]
InstallTo=Install to
UpdateIn=Updates the copy in
UpdateTitle=Update %1
UpdateButton=&Update
Change=&Change...
DesktopShortcut=Create a &desktop shortcut
LicenceNote=Free software under the GNU General Public License v3.
LicenceLink=Read the licence
LicenceTitle=Licence
SetUpExtension=Set up the &browser extension
SetUpExtensionNote=Adds it to Edge, Chrome or another browser, so their downloads come here.

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Shown from the welcome page's licence link.
Source: "..\LICENSE"; Flags: dontcopy

[InstallDelete]
; Files from an older version that this one no longer ships.
Type: filesandordirs; Name: "{app}\tools"

[Icons]
; Straight in the Start menu's app list, not in a folder of its own.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Check: WantsDesktopIcon

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "{code:LaunchParameters}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitApp"

[UninstallDelete]
; The browser connection's manifest. Downloads, the download list and settings stay.
Type: files; Name: "{localappdata}\OpenDownloadManager\{#HostName}.json"

[Code]
var
  FolderLabel, FolderPath, LicenceNote, LicenceLink, ExtensionNote: TNewStaticText;
  ChangeButton: TNewButton;
  DesktopIcon, Extension: TNewCheckBox;
  BarTrack, BarFill: TBitmapImage;

// Windows' accent colour, as the app uses it: a shade darker on light pages, lighter on dark ones.
function AccentColor: TColor;
var
  Abgr: Cardinal;
  R, G, B: Integer;
begin
  if not RegQueryDWordValue(HKCU, 'Software\Microsoft\Windows\DWM', 'AccentColor', Abgr) then Abgr := $D47800;
  R := Abgr and $FF; G := (Abgr shr 8) and $FF; B := (Abgr shr 16) and $FF;
  if IsDarkInstallMode then
  begin
    R := R + (255 - R) * 45 div 100; G := G + (255 - G) * 45 div 100; B := B + (255 - B) * 45 div 100;
  end else
  begin
    R := R * 82 div 100; G := G * 82 div 100; B := B * 82 div 100;
  end;
  Result := R or (G shl 8) or (B shl 16);
end;

// One left edge for the header and the page under it, the header's text centred in its band,
// and the small picture fully inside it with the same margin on the right as the page.
procedure LayoutHeader;
var
  Panel: TPanel;
  Edge, Margin, Size, TextTop, TextWidth: Integer;
begin
  Panel := WizardForm.MainPanel;
  Edge := WizardForm.InnerNotebook.Left;
  Margin := Panel.ClientWidth - (WizardForm.InnerNotebook.Left + WizardForm.InnerNotebook.Width);
  Size := ScaleY(40);
  WizardForm.WizardSmallBitmapImage.Stretch := True;
  WizardForm.WizardSmallBitmapImage.SetBounds(Panel.ClientWidth - Margin - Size, (Panel.ClientHeight - Size) div 2, Size, Size);
  TextWidth := WizardForm.WizardSmallBitmapImage.Left - Edge - ScaleX(16);
  WizardForm.PageNameLabel.SetBounds(Edge, 0, TextWidth, WizardForm.PageNameLabel.Height);
  WizardForm.PageDescriptionLabel.AutoSize := False;
  WizardForm.PageDescriptionLabel.SetBounds(Edge, 0, TextWidth, ScaleY(16));
  TextTop := (Panel.ClientHeight - (WizardForm.PageNameLabel.Height + ScaleY(4) + WizardForm.PageDescriptionLabel.Height)) div 2;
  WizardForm.PageNameLabel.Top := TextTop;
  WizardForm.PageDescriptionLabel.Top := TextTop + WizardForm.PageNameLabel.Height + ScaleY(4);
end;

// A solid block of colour that every wizard style paints as it is.
procedure Fill(Image: TBitmapImage; Color: TColor);
begin
  Image.BackColor := Color;
  Image.Stretch := True;
  Image.Bitmap.Width := 1;
  Image.Bitmap.Height := 1;
  Image.Bitmap.Canvas.Pixels[0, 0] := Color;
end;

// The Installing page: what is happening, a thin bar in the accent colour, then the file.
procedure LayoutInstalling;
var
  Page: TNewNotebookPage;
  Top: Integer;
begin
  Page := WizardForm.InstallingPage;
  WizardForm.StatusLabel.Top := ScaleY(8);
  Top := WizardForm.StatusLabel.Top + WizardForm.StatusLabel.Height + ScaleY(12);
  WizardForm.ProgressGauge.Visible := False;
  // Bitmap images, not panels: the dark style repaints panels in its own colour.
  BarTrack := TBitmapImage.Create(WizardForm);
  BarTrack.Parent := Page;
  if IsDarkInstallMode then Fill(BarTrack, $3A3A3A) else Fill(BarTrack, $E0E0E0);
  BarTrack.SetBounds(WizardForm.ProgressGauge.Left, Top, WizardForm.ProgressGauge.Width, ScaleY(6));
  BarFill := TBitmapImage.Create(WizardForm);
  BarFill.Parent := Page;
  Fill(BarFill, AccentColor);
  BarFill.SetBounds(BarTrack.Left, BarTrack.Top, 0, BarTrack.Height);
  WizardForm.FilenameLabel.Top := BarTrack.Top + BarTrack.Height + ScaleY(10);
  WizardForm.FilenameLabel.Font.Color := clGrayText;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if MaxProgress > 0 then BarFill.Width := MulDiv(BarTrack.Width, CurProgress, MaxProgress);
end;

function IsUpgrade: Boolean;
begin
  Result := WizardForm.PrevAppDir <> '';
end;

function DesktopShortcut: String;
begin
  Result := ExpandConstant('{autodesktop}\{#AppName}.lnk');
end;

function WantsDesktopIcon: Boolean;
begin
  Result := DesktopIcon.Checked;
end;

procedure ShowFolder;
begin
  FolderPath.Caption := MinimizePathName(WizardForm.DirEdit.Text, FolderPath.Font, FolderPath.Width);
  FolderPath.Hint := WizardForm.DirEdit.Text;
end;

procedure ChangeClick(Sender: TObject);
var
  Dir: String;
begin
  Dir := WizardForm.DirEdit.Text;
  if BrowseForFolder(SetupMessage(msgBrowseDialogLabel), Dir, True) then
  begin
    // Like the folder page: a chosen parent folder gets the app's own folder inside it.
    if CompareText(ExtractFileName(RemoveBackslashUnlessRoot(Dir)), '{#AppName}') <> 0 then
      Dir := AddBackslash(Dir) + '{#AppName}';
    WizardForm.DirEdit.Text := Dir;
    ShowFolder;
  end;
end;

procedure LicenceClick(Sender: TObject);
var
  Form: TSetupForm;
  Memo: TNewMemo;
  Close: TNewButton;
  Text: AnsiString;
  Body: String;
begin
  ExtractTemporaryFile('LICENSE');
  LoadStringFromFile(ExpandConstant('{tmp}\LICENSE'), Text);
  Body := String(Text);
  StringChangeEx(Body, #13#10, #10, True);
  StringChangeEx(Body, #10, #13#10, True);
  // The text is laid out for a fixed-width font, 80 columns.
  Form := CreateCustomForm(ScaleX(560), ScaleY(480), False, True);
  try
    Form.Caption := CustomMessage('LicenceTitle');
    Memo := TNewMemo.Create(Form);
    Memo.Parent := Form;
    Memo.SetBounds(ScaleX(12), ScaleY(12), Form.ClientWidth - ScaleX(24), Form.ClientHeight - ScaleY(60));
    Memo.Anchors := [akLeft, akTop, akRight, akBottom];
    Memo.ReadOnly := True;
    Memo.ScrollBars := ssVertical;
    Memo.Font.Name := 'Consolas';
    Memo.WordWrap := False;
    Memo.Text := Body;
    Close := TNewButton.Create(Form);
    Close.Parent := Form;
    Close.Caption := SetupMessage(msgButtonOK);
    Close.SetBounds(Form.ClientWidth - ScaleX(87), Form.ClientHeight - ScaleY(37), ScaleX(75), ScaleY(23));
    Close.Anchors := [akRight, akBottom];
    Close.ModalResult := mrOk;
    Close.Default := True;
    Close.Cancel := True;
    Form.ActiveControl := Close;
    Form.ShowModal;
  finally
    Form.Free;
  end;
end;

function NewText(Parent: TWinControl; Caption: String; Top: Integer): TNewStaticText;
begin
  Result := TNewStaticText.Create(WizardForm);
  Result.Parent := Parent;
  Result.Caption := Caption;
  Result.Left := WizardForm.WelcomeLabel2.Left;
  Result.Top := Top;
end;

procedure InitializeWizard;
var
  Page: TWinControl;
  Left, Width, Top: Integer;
begin
  Page := WizardForm.WelcomePage;
  Left := WizardForm.WelcomeLabel2.Left;
  Width := WizardForm.WelcomeLabel2.Width;
  // The description is one sentence: let the choices sit close under it.
  WizardForm.WelcomeLabel2.AutoSize := True;
  Top := WizardForm.WelcomeLabel2.Top + WizardForm.WelcomeLabel2.Height + ScaleY(28);

  WizardForm.Caption := FmtMessage(SetupMessage(msgSetupWindowTitle), ['{#AppName} {#AppVersion}']);
  if IsUpgrade then WizardForm.WelcomeLabel1.Caption := FmtMessage(CustomMessage('UpdateTitle'), ['{#AppName}']);

  if IsUpgrade then FolderLabel := NewText(Page, CustomMessage('UpdateIn'), Top)
  else FolderLabel := NewText(Page, CustomMessage('InstallTo'), Top);
  FolderLabel.Font.Style := [fsBold];

  FolderPath := NewText(Page, '', FolderLabel.Top + FolderLabel.Height + ScaleY(4));
  FolderPath.AutoSize := False;
  FolderPath.ShowHint := True;
  FolderPath.Width := Width - ScaleX(92);
  FolderPath.Height := ScaleY(16);

  ChangeButton := TNewButton.Create(WizardForm);
  ChangeButton.Parent := Page;
  ChangeButton.Caption := CustomMessage('Change');
  ChangeButton.SetBounds(Left + Width - ScaleX(80), FolderPath.Top - ScaleY(5), ScaleX(80), ScaleY(23));
  ChangeButton.OnClick := @ChangeClick;
  // An update goes where the app already is.
  ChangeButton.Visible := not IsUpgrade;

  DesktopIcon := TNewCheckBox.Create(WizardForm);
  DesktopIcon.Parent := Page;
  DesktopIcon.Caption := CustomMessage('DesktopShortcut');
  DesktopIcon.SetBounds(Left, FolderPath.Top + ScaleY(36), Width, ScaleY(17));
  // Keep what the last install chose.
  DesktopIcon.Checked := FileExists(DesktopShortcut);

  LicenceNote := NewText(Page, CustomMessage('LicenceNote'), 0);
  LicenceNote.Top := WizardForm.WelcomePage.ClientHeight - ScaleY(40);
  LicenceNote.Font.Color := clGrayText;
  LicenceLink := NewText(Page, CustomMessage('LicenceLink'), LicenceNote.Top + LicenceNote.Height + ScaleY(2));
  LicenceLink.Font.Color := clHotLight;
  LicenceLink.Font.Style := [fsUnderline];
  LicenceLink.Cursor := crHand;
  LicenceLink.OnClick := @LicenceClick;

  // Finish: next to "Launch", the browser extension's guide in the app.
  Extension := TNewCheckBox.Create(WizardForm);
  Extension.Parent := WizardForm.FinishedPage;
  Extension.Caption := CustomMessage('SetUpExtension');
  Extension.Checked := True;
  ExtensionNote := TNewStaticText.Create(WizardForm);
  ExtensionNote.Parent := WizardForm.FinishedPage;
  ExtensionNote.Caption := CustomMessage('SetUpExtensionNote');
  ExtensionNote.Font.Color := clGrayText;
  ExtensionNote.WordWrap := True;

  LayoutHeader;
  LayoutInstalling;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpWelcome then
  begin
    ShowFolder;
    // Every page between here and installing is skipped, so this button starts it.
    if IsUpgrade then WizardForm.NextButton.Caption := CustomMessage('UpdateButton')
    else WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
  end;
  if CurPageID = wpFinished then
  begin
    // Under the "Launch" box. Inno stretches its list to the bottom of the page: one line is enough.
    WizardForm.RunList.Height := ScaleY(22);
    Extension.Visible := WizardForm.RunList.Visible;
    ExtensionNote.Visible := Extension.Visible;
    Extension.SetBounds(WizardForm.RunList.Left + ScaleX(4), WizardForm.RunList.Top + WizardForm.RunList.Height + ScaleY(8),
      WizardForm.RunList.Width - ScaleX(4), ScaleY(17));
    ExtensionNote.SetBounds(Extension.Left + ScaleX(21), Extension.Top + Extension.Height + ScaleY(3),
      Extension.Width - ScaleX(21), ScaleY(30));
  end;
end;

function LaunchParameters(Param: String): String;
begin
  Result := '';
  if Extension.Checked then Result := '--setup-extension';
end;

// "Set up the browser extension" without "Launch" still starts the app, to show the guide.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Code: Integer;
begin
  Result := True;
  if (CurPageID = wpFinished) and Extension.Visible and Extension.Checked and not WizardForm.RunList.Checked[0] then
    ExecAsOriginalUser(ExpandConstant('{app}\{#AppExe}'), '--setup-extension', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

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
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\{#HostName}');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Vivaldi\NativeMessagingHosts\{#HostName}');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Chromium\NativeMessagingHosts\{#HostName}');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'OpenDownloadManager');
  end;
end;
