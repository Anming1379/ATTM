; ATTM 安装包脚本（Inno Setup 6）
; 由 tools\publish.ps1 调用，也可以手动用 ISCC 编译：
;   ISCC.exe /DAppVersion=0.1.0 /DSourceDir=..\dist\app packaging\ATTM.iss
;
; 安装包发布的是“框架依赖”版本，体积小；
; 如果机器上没有 .NET 10 桌面运行时，会自动调用 install-dotnet.cmd 安装。

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist\installer"
#endif

#define AppName "ATTM"
#define AppPublisher "Anming1379"
#define AppExeName "ATTM.exe"
#define AppUrl "https://github.com/Anming1379/ATTM"

[Setup]
AppId={{7C4E1B62-9D3A-4F7E-8A11-2B5C6D7E8F90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=ATTM-{#AppVersion}-setup
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
ChangesAssociations=yes
AllowNoIcons=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked
Name: "associate"; Description: "关联 .attm 谱面文件"; GroupDescription: "附加任务："

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; 示例谱子与语法教程：装到 音乐\ATTM（已存在就不覆盖，免得冲掉用户的改动）
; 绿色版不做这一步，解压即用
Source: "{#SourceDir}\samples\欢乐颂 - 演示谱.attm"; DestDir: "{code:GetMusicFolder}\ATTM"; Flags: onlyifdoesntexist uninsneveruninstall ignoreversion
Source: "{#SourceDir}\samples\小星星 - 演示谱.attm"; DestDir: "{code:GetMusicFolder}\ATTM"; Flags: onlyifdoesntexist uninsneveruninstall ignoreversion
Source: "{#SourceDir}\samples\Affix Your Name! - Anming_cat.attm"; DestDir: "{code:GetMusicFolder}\ATTM"; Flags: onlyifdoesntexist uninsneveruninstall ignoreversion
Source: "{#SourceDir}\ATTM 语法教程.md"; DestDir: "{code:GetMusicFolder}\ATTM"; Flags: onlyifdoesntexist uninsneveruninstall ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\ATTM 谱面示例"; Filename: "{app}\samples"
Name: "{group}\安装 .NET 10 运行时"; Filename: "{app}\install-dotnet.cmd"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 缺运行时先装（脚本自己会跳过已安装的情况）
; 注意：这里不要加 runhidden —— 下载 60 MB 需要看得见的进度，
; 需要 UAC 时也要能看到"请在弹窗里点 是"的提示，否则会被误认为卡死。
Filename: "{app}\install-dotnet.cmd"; Parameters: "/quiet"; \
  StatusMsg: "正在安装 .NET 10 桌面运行时（会弹出一个窗口显示下载进度）..."; \
  Flags: waituntilterminated; Check: NeedsDotNet

Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; \
  Flags: nowait postinstall skipifsilent; Check: not NeedsDotNet

[Registry]
; .attm 文件关联（HKA 会按权限落到 HKCR 或 HKCU\Software\Classes）
Root: HKA; Subkey: "Software\Classes\.attm"; ValueType: string; ValueName: ""; ValueData: "ATTM.Score"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\ATTM.Score"; ValueType: string; ValueName: ""; ValueData: "ATTM 谱面"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\ATTM.Score\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\ATTM.Score\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: associate

[Code]
// 音乐文件夹：优先用系统登记的路径（用户可能把它重定向到别的盘）
function GetMusicFolder(Param: String): String;
var
  Value: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders', 'My Music', Value) then
    Result := Value
  else if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders', 'My Music', Value) then
    Result := Value
  else
    Result := GetEnv('USERPROFILE') + '\Music';
end;

// 机器上有没有 .NET 10 桌面运行时（10.x 的 Microsoft.WindowsDesktop.App）
function NeedsDotNet(): Boolean;
var
  SharedRoot: String;
  FindRec: TFindRec;
  Found: Boolean;
begin
  Found := False;
  SharedRoot := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');

  if DirExists(SharedRoot) then
  begin
    if FindFirst(SharedRoot + '\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            if Pos('10.', FindRec.Name) = 1 then
              Found := True;
        until Found or (not FindNext(FindRec));
      finally
        FindClose(FindRec);
      end;
    end;
  end;

  Result := not Found;
end;

// 装完了却还是没有运行时：给一句人话提示，别让用户点开程序时看到 .NET 报错
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssDone) and NeedsDotNet() then
    MsgBox('没有检测到 .NET 10 桌面运行时，ATTM 暂时还不能启动。' + #13#10 + #13#10 +
           '可以双击安装目录里的 install-dotnet.cmd 重新安装，' + #13#10 +
           '或者从 https://dotnet.microsoft.com/download/dotnet/10.0 手动下载安装。',
           mbInformation, MB_OK);
end;
