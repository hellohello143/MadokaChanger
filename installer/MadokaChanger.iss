#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

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
CloseApplications=no
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
Filename: "{app}\MadokaChanger.exe"; Description: "Launch MadokaChanger"; Flags: nowait postinstall skipifsilent; Check: not IsSmokeTest

[Code]
// The verification script installs into a workspace folder without changing
// shortcuts or registering an application. It still exercises the real payload
// and generated uninstaller; the app is never launched by a silent install.
function IsSmokeTest: Boolean;
begin
  Result := ExpandConstant('{param:SMOKETEST|0}') = '1';
end;
