#define MyAppName "EdgeKit"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "EdgeKit"
#define MyAppExeName "EdgeKit.App.exe"
#ifndef MyRuntimeTag
#define MyRuntimeTag "win-x64"
#endif
#ifndef MyArchitecturesAllowed
#define MyArchitecturesAllowed "x64compatible"
#endif
#ifndef MyArchitecturesInstallIn64BitMode
#define MyArchitecturesInstallIn64BitMode "x64compatible"
#endif
#define MySourceDir "..\artifacts\publish\EdgeKit-" + MyRuntimeTag
#define MyOutputDir "..\artifacts\installer"
#define MyInstallSubdir "bin"

[Setup]
AppId={{8A6FE3F1-9E68-4D99-A51E-2F4C928E5B49}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#MyOutputDir}
OutputBaseFilename=EdgeKitSetup-{#MyAppVersion}-{#MyRuntimeTag}
SetupIconFile=..\src\EdgeKit.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyInstallSubdir}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardImageFile=assets\wizard-image.bmp
WizardSmallImageFile=assets\wizard-small-image.bmp
ArchitecturesAllowed={#MyArchitecturesAllowed}
ArchitecturesInstallIn64BitMode={#MyArchitecturesInstallIn64BitMode}
PrivilegesRequired=lowest
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[InstallDelete]
Type: filesandordirs; Name: "{app}\{#MyInstallSubdir}"
Type: files; Name: "{app}\EdgeKit.App.exe"
Type: files; Name: "{app}\createdump.exe"
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\*.pri"
Type: files; Name: "{app}\*.winmd"
Type: filesandordirs; Name: "{app}\Assets"
Type: filesandordirs; Name: "{app}\Microsoft.UI.Xaml"
Type: filesandordirs; Name: "{app}\??-??"
Type: filesandordirs; Name: "{app}\???-??"
Type: filesandordirs; Name: "{app}\??-????-??"
Type: filesandordirs; Name: "{app}\ca-Es-VALENCIA"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\{#MyInstallSubdir}"
Type: filesandordirs; Name: "{app}"
Type: files; Name: "{userappdata}\Microsoft\Windows\Start Menu\Programs\EdgeKit.lnk"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}\{#MyInstallSubdir}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,??-??\*,???-??\*,??-????-??\*,ca-Es-VALENCIA\*"
Source: "{#MySourceDir}\en-us\*"; DestDir: "{app}\{#MyInstallSubdir}\en-us"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#MySourceDir}\zh-CN\*"; DestDir: "{app}\{#MyInstallSubdir}\zh-CN"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#MySourceDir}\zh-TW\*"; DestDir: "{app}\{#MyInstallSubdir}\zh-TW"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyInstallSubdir}\{#MyAppExeName}"; WorkingDir: "{app}\{#MyInstallSubdir}"; AppUserModelID: "EdgeKit"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyInstallSubdir}\{#MyAppExeName}"; WorkingDir: "{app}\{#MyInstallSubdir}"; Tasks: desktopicon; AppUserModelID: "EdgeKit"

[Run]
Filename: "{app}\{#MyInstallSubdir}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
procedure KillEdgeKitProcesses();
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{cmd}'),
    '/c taskkill /IM EdgeKit.App.exe /F /T >nul 2>nul',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  KillEdgeKitProcesses();
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  KillEdgeKitProcesses();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#MyAppName}');

    DataDir := ExpandConstant('{localappdata}\EdgeKit');
    if DirExists(DataDir) and (not UninstallSilent) then
    begin
      if MsgBox(
        '是否同时删除 EdgeKit 用户数据？' + #13#10#13#10 +
        '包含设置、剪贴板数据库、日志、hosts/environment 备份等。' + #13#10 +
        '选择“否”会保留这些数据，便于以后重新安装。',
        mbConfirmation,
        MB_YESNO) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
