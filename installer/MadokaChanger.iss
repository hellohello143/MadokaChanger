#ifndef AppVersion
  #define AppVersion "1.2.1"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

; LTK Manager is never bundled here: its license forbids redistribution. After the
; files are installed, MadokaChanger.exe --install-ltk downloads the official
; release from GitHub, verifies it and installs it, and only when it is missing.

[Setup]
AppId={{D81CF70D-5CB2-4BD8-902E-7BC8533F5728}
AppName=MadokaChanger
AppVersion={#AppVersion}
AppPublisher=MadokaChanger
AppPublisherURL=https://github.com/hellohello143/MadokaChanger
AppSupportURL=https://github.com/hellohello143/MadokaChanger/issues
AppUpdatesURL=https://github.com/hellohello143/MadokaChanger/releases
DefaultDirName={localappdata}\Programs\MadokaChanger
DefaultGroupName=MadokaChanger
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
DisableProgramGroupPage=yes
DisableWelcomePage=no
AllowNoIcons=yes
UsePreviousAppDir=not IsSmokeTest
CreateUninstallRegKey=not IsSmokeTest
; The in-app updater starts this setup and exits; anything still running from
; {app} is closed by Restart Manager, then terminated as a fallback (see Code).
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
SetupLogging=yes
UninstallDisplayIcon={app}\MadokaChanger.exe
UninstallDisplayName=MadokaChanger
LicenseFile=..\LICENSE
SetupIconFile=..\src\MadokaChanger.App\Assets\MadokaChanger.ico
WizardSmallImageFile=..\src\MadokaChanger.App\Assets\MadokaChibi.png
WizardStyle=modern dark includetitlebar
WizardResizable=yes
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=MadokaChanger-Setup-{#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoDescription=MadokaChanger Setup

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Check: not IsSmokeTest

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MadokaChanger"; Filename: "{app}\MadokaChanger.exe"; WorkingDir: "{app}"; IconFilename: "{app}\MadokaChanger.exe"; Check: not IsSmokeTest
Name: "{autodesktop}\MadokaChanger"; Filename: "{app}\MadokaChanger.exe"; WorkingDir: "{app}"; IconFilename: "{app}\MadokaChanger.exe"; Tasks: desktopicon; Check: not IsSmokeTest

[Run]
Filename: "{app}\MadokaChanger.exe"; WorkingDir: "{app}"; Description: "Launch MadokaChanger"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: not IsSmokeTest

[Code]
const
  LtkReleaseUrl = 'https://github.com/LeagueToolkit/ltk-manager/releases/latest';

var
  LtkNeedsAttention: Boolean;
  LtkNote: TNewStaticText;
  LtkLink: TNewStaticText;

// The verification script installs into a workspace folder without changing
// shortcuts or registering an application. It still exercises the real payload
// and generated uninstaller; the app is never launched by a silent install.
function IsSmokeTest: Boolean;
begin
  Result := ExpandConstant('{param:SMOKETEST|0}') = '1';
end;

// The in-app updater passes /SKIPLTK=1 (the app checks LTK itself) and /RELAUNCH=1.
function SkipLtk: Boolean;
begin
  Result := ExpandConstant('{param:SKIPLTK|0}') = '1';
end;

function ShouldRelaunch: Boolean;
begin
  Result := (ExpandConstant('{param:RELAUNCH|0}') = '1') and WizardSilent and not IsSmokeTest;
end;

function StartsWithText(const Value, Prefix: String): Boolean;
begin
  Result := (Length(Value) >= Length(Prefix)) and (CompareText(Copy(Value, 1, Length(Prefix)), Prefix) = 0);
end;

// Fallback for copies Restart Manager could not close: terminate only
// MadokaChanger.exe processes running from the folder being upgraded.
procedure TerminateRunningCopies;
var
  Locator, Service, Processes, Process: Variant;
  AppDir, ExePath: String;
  I: Integer;
begin
  AppDir := AddBackslash(ExpandConstant('{app}'));
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = ''MadokaChanger.exe''');
    for I := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(I);
      ExePath := '';
      if not VarIsNull(Process.ExecutablePath) then
        ExePath := Process.ExecutablePath;
      if (ExePath <> '') and StartsWithText(ExePath, AppDir) then
      begin
        Log('Terminating running MadokaChanger (pid ' + IntToStr(Process.ProcessId) + '): ' + ExePath);
        Process.Terminate();
      end;
    end;
  except
    Log('Could not enumerate running MadokaChanger processes: ' + GetExceptionMessage);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  TerminateRunningCopies;
  Sleep(300);
  Result := '';
end;

procedure OpenLtkRelease(Sender: TObject);
var
  Code: Integer;
begin
  ShellExecAsOriginalUser('open', LtkReleaseUrl, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

// Runs MadokaChanger.exe --install-ltk. The exe returns instantly when LTK
// Manager's signed patcher is already present. 0 = ready, 2 = admin prompt
// declined, 1 = failed. Never fails the MadokaChanger install.
procedure EnsureLtkManager;
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Checking LTK Manager...';
  WizardForm.FilenameLabel.Caption := 'Installing it from GitHub if it is missing. This can take a minute.';
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    if not ExecAsOriginalUser(ExpandConstant('{app}\MadokaChanger.exe'), '--install-ltk', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Log('LTK Manager step could not start: ' + SysErrorMessage(ResultCode));
      LtkNeedsAttention := True;
    end
    else
    begin
      Log('LTK Manager step exited with ' + IntToStr(ResultCode));
      LtkNeedsAttention := ResultCode <> 0;
    end;
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
    WizardForm.FilenameLabel.Caption := '';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and not IsSmokeTest and not SkipLtk then
    EnsureLtkManager;
  if (CurStep = ssDone) and ShouldRelaunch then
  begin
    Log('Relaunching MadokaChanger after silent update');
    ExecAsOriginalUser(ExpandConstant('{app}\MadokaChanger.exe'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, Code);
  end;
end;

procedure InitializeWizard;
begin
  LtkNote := TNewStaticText.Create(WizardForm);
  LtkNote.Parent := WizardForm.FinishedPage;
  LtkNote.AutoSize := False;
  LtkNote.WordWrap := True;
  LtkNote.Left := WizardForm.FinishedLabel.Left;
  LtkNote.Width := WizardForm.FinishedLabel.Width;
  LtkNote.Height := ScaleY(48);
  LtkNote.Anchors := [akLeft, akTop, akRight];
  LtkNote.Caption := 'LTK Manager is needed to apply skins and was not installed. Open MadokaChanger and use Update to install it, or get it from:';
  LtkNote.Visible := False;

  LtkLink := TNewStaticText.Create(WizardForm);
  LtkLink.Parent := WizardForm.FinishedPage;
  LtkLink.Left := WizardForm.FinishedLabel.Left;
  LtkLink.Caption := LtkReleaseUrl;
  LtkLink.Cursor := crHand;
  LtkLink.Font.Style := [fsUnderline];
  LtkLink.Font.Color := $A66AE8;
  LtkLink.OnClick := @OpenLtkRelease;
  LtkLink.Visible := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and LtkNeedsAttention then
  begin
    LtkNote.Top := WizardForm.RunList.Top + WizardForm.RunList.Height + ScaleY(12);
    LtkLink.Top := LtkNote.Top + LtkNote.Height + ScaleY(2);
    LtkNote.Visible := True;
    LtkLink.Visible := True;
  end;
end;
