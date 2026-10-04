; ══════════════════════════════════════════════════════════════════════
;  ClassSoftwareHub · Windows 7 移植版 — Inno Setup 6 安装脚本
;
;  编译（在工程根目录）：
;    & "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\ClassSoftwareHub-Win7.iss
;    （版本号默认取下面的 DesktopVersion；临时覆盖就加 /DDesktopVersion=1.1.0-insider1.2）
;
;  产物：dist\installer\ClassSoftwareHub-Setup-win7-dv<DesktopVersion>.exe
;
;  与 WinUI 版（另一个仓库 ClassSoftwareHub-Desktop 的 installer\ClassSoftwareHub.iss）的关系
;  —— 两版是**同机可共存**的两个产品：
;   · 安装形状不同：本版装的是「启动器 + app 子目录」两层（和便携版同构），
;     所以以后换版本只覆盖 app\ 就行，启动器和快捷方式永远有效。
;   · AppId 不同：Inno 靠 AppId 认亲，两版用不同的 GUID，才不会把对方当成"自己的旧版本"。
;   · 目录不同：默认装到 ...\Programs\ClassSoftwareHub-Win7，避免在同机上跟 WinUI 版抢文件。
;   · 系统底线不同：本版要求 Windows 7 SP1（.NET 6 在 Win7 上就要求 SP1），
;     而 WinUI 版要求 Windows 10 1809。检查逻辑见下面 [Code] 段。
;
;  ⚠️ 更新器按「文件名里含 setup」来认包：文件名必须含 Setup（本脚本的 OutputBaseFilename 已满足）。
;     本仓库是 Win7 版自己的独立仓库，tag 形如 dv1.1.0-insider1.2，不带任何前缀。
; ══════════════════════════════════════════════════════════════════════

; ⚠️ 唯一的版本号来源，必须和 Core/ShellConfig.cs 的 ShellVersion 一字不差（写在这里时**不带** dv 前缀）
#ifndef DesktopVersion
  #define DesktopVersion "1.1.0-insider1.2"
#endif

#define AppName "ClassSoftwareHub"
#define AppExeName "ClassSoftwareHub.exe"
#define AppSite "https://classsoftwarehub.us.ci"

[Setup]
; ⚠️ 这个 GUID 是「同一款软件」的标识，升级/回滚靠它认亲。
;    **本版有自己的 GUID，刻意与 WinUI 版不同** —— 两版是不同的产品线，不能互认。
AppId={{C4E7B2A1-6D38-4F5C-9A17-0E8B3D5C7F42}
AppName={#AppName}
AppVersion={#DesktopVersion}
AppVerName={#AppName} dv{#DesktopVersion} (Windows 7)
AppPublisher={#AppName}
AppPublisherURL={#AppSite}
AppSupportURL={#AppSite}
DefaultDirName={localappdata}\Programs\{#AppName}-Win7
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
PrivilegesRequired=lowest
OutputDir=..\dist\installer
OutputBaseFilename=ClassSoftwareHub-Setup-win7-dv{#DesktopVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} dv{#DesktopVersion} (Windows 7)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; ⚠️ 这里**故意不设 AppMutex**（理由与 WinUI 版一致）：Inno 一启动就查 Mutex，
;    查到应用还在跑就直接拒装，而那个框会被 /SUPPRESSMSGBOXES 自动按「取消」
;    → 静默更新变成"什么都没发生"。改由 [Code] PrepareToInstall 自己 taskkill。
CloseApplications=yes
RestartApplications=no
; ⚠️ MinVersion 故意留 6.1：系统版本由 [Code] 自己判，这样不够格时能弹出「打开网页版」的按钮
MinVersion=6.1sp1

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; ── 两层结构：根目录只有启动器，应用本体在 app\ 下（与便携版一致）──
Source: "..\dist\win7\ClassSoftwareHub.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\win7\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 快捷方式指向**启动器**：路径永远不变，以后更新只换 app\ 子目录
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 装完自动拉起应用（交互安装、应用内更新的静默安装都算）。
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait

; ══════════════════════════════════════════════════════════════════════
;  兜底逻辑（[Code]）
;   · 系统低于 Windows 7 SP1 → 不给装，并给一个「打开网页版」的去处
;     （.NET 6 在 Win7 上要求 SP1，这是硬门槛，没法绕）
;   · 缺 WebView2 运行时 → 只**提示**，不自动下载：
;     Win7 上 WebView2 运行时止于 109.x，官方 bootstrapper 拉的是新版、装了也用不了
;     → 自动装反而制造"装完了还是不行"的困惑。给下载页链接，让用户自己判断。
;   · .NET 6：我们是**自包含**发布（self-contained），目标机不用另装，无需兜底。
; ══════════════════════════════════════════════════════════════════════

[Code]

const
  WebUrl = '{#AppSite}';
  WebView2Page = 'https://developer.microsoft.com/microsoft-edge/webview2/#download-section';
  // WebView2 运行时在注册表里的固定 GUID
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

/// <summary>应用的单实例 Mutex 还在不在（在 = 旧版本还跑着）。</summary>
function AppIsRunning(): Boolean;
begin
  Result := CheckForMutexes('{#AppName}.Desktop.SingleInstance');
end;

/// <summary>
/// 安装前把还在运行的旧版本关掉。
///
/// 为什么非自己动手不可（与 WinUI 版同因，2026-10-01 实测）：
///   ① 客户端的"关闭窗口"默认是收进托盘，进程不退；
///   ② 这段客户端代码早就发到用户机器上了、改不了，能改的只有这份安装脚本；
///   ③ 原先靠 AppMutex 拦（已从 [Setup] 移除）→ 弹框被 /SUPPRESSMSGBOXES 自动取消 → 静默失败；
///   ④ 也不能指望 /CLOSEAPPLICATIONS：它走 Restart Manager 发 WM_CLOSE，
///      而本应用把 WM_CLOSE 吞去收托盘了 → RM 永远关不掉它。
///   所以：先 `taskkill`（不带 /F）礼貌请一次，还不退就强杀。
///   应用无未保存状态、设置即时落盘，被杀不丢数据；[Run] 段随后会把它拉起来。
/// </summary>
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R: Integer;
begin
  Result := '';
  if not AppIsRunning() then
    Exit;

  if not WizardSilent() then
  begin
    if MsgBox('检测到 {#AppName} 正在运行，安装前需要先关闭它。' + #13#10 + #13#10 +
              '是否继续？', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := '安装已取消：请先关闭 {#AppName} 再运行安装程序。';
      Exit;
    end;
  end;

  // ① 先好好请一次
  Exec('cmd.exe', '/c taskkill /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, R);
  Sleep(1500);

  // ② 还不退就强杀
  if AppIsRunning() then
  begin
    Exec('cmd.exe', '/c taskkill /F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(600);
  end;
end;

/// <summary>NT 内核号 → 用户认识的商品名。</summary>
function WindowsName(V: TWindowsVersion): String;
begin
  if (V.Major = 10) and (V.Build >= 22000) then
    Result := 'Windows 11'
  else if V.Major = 10 then
    Result := 'Windows 10'
  else if (V.Major = 6) and (V.Minor = 3) then
    Result := 'Windows 8.1'
  else if (V.Major = 6) and (V.Minor = 2) then
    Result := 'Windows 8'
  else if (V.Major = 6) and (V.Minor = 1) then
    Result := 'Windows 7'
  else if (V.Major = 6) and (V.Minor = 0) then
    Result := 'Windows Vista'
  else
    Result := Format('Windows %d.%d', [V.Major, V.Minor]);

  if V.ServicePackMajor > 0 then
    Result := Result + Format(' SP%d', [V.ServicePackMajor]);
end;

/// <summary>
/// 系统够不够跑 .NET 6 + Avalonia：要求 **Windows 7 SP1** 及以上。
/// （.NET 6 是最后一个官方支持 Win7 的 .NET 版本，且它在 Win7 上要求 SP1 —— 硬门槛。）
/// </summary>
function SystemVersionOk(var Why: String): Boolean;
var
  V: TWindowsVersion;
begin
  GetWindowsVersionEx(V);
  Why := Format('%s（Build %d）', [WindowsName(V), V.Build]);

  // Vista 及以下：没戏
  if V.Major < 6 then
  begin
    Result := False;
    Exit;
  end;

  // Vista（6.0）
  if (V.Major = 6) and (V.Minor = 0) then
  begin
    Result := False;
    Exit;
  end;

  // Windows 7 但没打 SP1
  if (V.Major = 6) and (V.Minor = 1) and (V.ServicePackMajor < 1) then
  begin
    Result := False;
    Exit;
  end;

  Result := True;
end;

/// <summary>系统里有没有 Evergreen WebView2 运行时（按机器装 / 按用户装都认）。</summary>
function HasWebView2(): Boolean;
var
  Ver: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Ver) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Ver) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Ver);
end;

function OpenUrl(const Url: String): Boolean;
var
  Err: Integer;
begin
  Result := ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, Err);
end;

function InitializeSetup(): Boolean;
var
  Why: String;
  Choice: Integer;
begin
  Result := True;

  // ── 1. 系统版本 ──────────────────────────────────────────────────
  if not SystemVersionOk(Why) then
  begin
    Choice := MsgBox(
      '这个应用跑不起来：它需要 Windows 7 SP1 或更高版本。' + #13#10 +
      '你现在的系统是：' + Why + #13#10 + #13#10 +
      '如果是因为没打 SP1 —— 装上 Windows 7 Service Pack 1 之后就能用了。' + #13#10 + #13#10 +
      '另外别着急：网页版在浏览器里（浏览器、手机都行）功能是一样的。' + #13#10 + #13#10 +
      '现在就打开网页版看看？',
      mbCriticalError, MB_YESNO);
    if Choice = IDYES then
      OpenUrl(WebUrl);
    Result := False;
    Exit;
  end;

  // ── 2. WebView2 运行时（可选组件，只提示不自动装）────────────────
  if not HasWebView2() then
  begin
    Choice := MsgBox(
      '没检测到 WebView2 运行时。' + #13#10 +
      '它不是必须的：界面全是原生实现的，只有在应用内显示网页时才会用到' +
      '（例如从软件详情页打开官网）。' + #13#10 + #13#10 +
      '⚠️ 注意：Windows 7 上只能装到 109.x 版的 WebView2 运行时，装不上是正常的。' + #13#10 + #13#10 +
      '要现在打开下载页看看吗？（选「否」会直接继续安装，不影响主要功能）',
      mbConfirmation, MB_YESNO);
    if Choice = IDYES then
      OpenUrl(WebView2Page);
  end;
end;
