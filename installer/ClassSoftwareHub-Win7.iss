; ══════════════════════════════════════════════════════════════════════
;  ClassSoftwareHub · Windows 7 移植版 — Inno Setup 6 安装脚本
;
;  编译（在工程根目录）：
;    & "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\ClassSoftwareHub-Win7.iss
;    （版本号默认取下面的 DesktopVersion；临时覆盖就加 /DDesktopVersion=1.0.0-insider1.1）
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
;     本仓库是 Win7 版自己的独立仓库，tag 形如 dv1.0.0，不带任何前缀。
; ══════════════════════════════════════════════════════════════════════

; ⚠️ 唯一的版本号来源，必须和 Core/ShellConfig.cs 的 ShellVersion 一字不差（写在这里时**不带** dv 前缀）
#ifndef DesktopVersion
  #define DesktopVersion "1.2.0"
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

; ── 安装时展示三份条款（用户协议 / 隐私政策 / 免责声明）────────────────────
; 内容与站点同源：站点仓库根目录 `法律文本.ts` ⇒ 桌面端设置页「条款与声明」用的
; Core/LegalText.cs、以及**这一页**用的 installer/Legal.rtf，都由本仓库
; tools/sync-legal.mjs 从同一份源生成。
;   ⇒ 要改条款：改站点那份 TS，再跑 `node tools/sync-legal.mjs`。⛔ 别手改 Legal.rtf。
;
; ⚠️ 用 RTF 而不是 txt：Inno 的**纯文本**许可文件「不会自动换行」，得自己按列硬换行，
;    中文按字符算宽度更是一团糟；RTF 交给 RichEdit 排版，自带换行与字体。
;    （生成物里的中文全走 \uNNNN? 转义 ⇒ 文件本身是纯 ASCII，没有编码问题。）
; ⚠️ 静默安装（更新器用的 /SP- /SILENT …）会**跳过许可页并视为已接受**，
;    所以升级流程不会被这一页打断。
LicenseFile=Legal.rtf
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
  // .NET 6 在 Windows 7 上的硬前提补丁。
  //   KB3063858 是微软官方文档列给 .NET 6 / Win7 的那一项；它提供 kernel32 的
  //   SetDefaultDllDirectories / AddDllDirectory 等 API，早期版本号是 KB2533623
  //   （该补丁已并入后续累积更新，单独下载已下架）。
  //   两个都指向微软更新目录，用搜索页而不是直链 —— 直链随月度汇总包失效。
  KbUpdatePage = 'https://www.catalog.update.microsoft.com/Search.aspx?q=KB3063858';
  // WebView2 运行时在注册表里的固定 GUID
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  // 本应用 AppId（与 [Setup] 里的 AppId 一字不差；用于判断是不是升级、找卸载键）
  AppIdGuid = '{C4E7B2A1-6D38-4F5C-9A17-0E8B3D5C7F42}';

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

// ── 检查系统有没有 .NET 6 依赖的那组 DLL 目录 API ─────────────────────
// ⚠️ 为什么必须查、而且**只能**查 API：
//   .NET 6 的宿主（apphost / hostfxr）用 LoadLibraryEx 的 LOAD_LIBRARY_SEARCH_* 标志
//   加载 hostfxr.dll / coreclr.dll。这几个标志是 Windows 8 才有的；Windows 7 要靠
//   KB2533623（已并入 KB3063858 及后续累积更新）给 kernel32 补上
//   SetDefaultDllDirectories / AddDllDirectory，标志才会被识别。
//   缺这个补丁时 LoadLibraryEx 返回 ERROR_INVALID_PARAMETER(87)，宿主就抛
//   「Failed to load the dll from [...hostfxr.dll], HRESULT: 0x80070057」并退出 ——
//   用户在界面上看到的就是「双击，闪一下，什么都没发生」。
//   ⇒ 这不是安装包的问题，应用本体（含便携版的启动器）全都跑在这个前提上，
//     换打包方式绕不过去；能做的只有在装之前把它讲清楚。
//
// 为什么查 SetDefaultDllDirectories 而不是 GetProcAddress 探导出表：
//   Inno 的 Pascal Script 对“函数指针”支持很弱，GetProcAddress 的声明容易编译不过；
//   而 delayload 是 Inno 文档明确给出的「函数可能不存在」的检测姿势 ——
//   加载库里没有这个导出时，调用会抛异常，用 try/except 接住即可。
//   这个 API 一旦调用会改掉**本安装进程**的 DLL 搜索目录（限定为
//   System32 + 应用目录 + 用户目录），所以放在 InitializeSetup 的**最后**再调，
//   此时 Setup 自己要用的库都已加载完，不会影响后续解压与建快捷方式。
function SetDefaultDllDirectories(dwFlags: DWORD): BOOL;
  external 'SetDefaultDllDirectories@kernel32.dll stdcall delayload';

/// <summary>
/// kernel32 有没有导出 <c>SetDefaultDllDirectories</c>（= KB2533623 / KB3063858 装没装），
/// 也就是 .NET 6 在 Windows 7 上能不能启动。
/// 检测本身出异常时**按「有」处理**：宁可放过，也不要因为检查器自己出错把安装挡下来。
/// </summary>
function HasDllDirectoryApi(): Boolean;
begin
  Result := True;
  try
    // 参数取 LOAD_LIBRARY_SEARCH_DEFAULT_DIRS($1000)：调用能成立就说明补丁在。
    SetDefaultDllDirectories($00001000);
  except
    Result := False;
  end;
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

  // ── 3. .NET 6 在 Windows 7 上的前提补丁（KB2533623 / KB3063858）──────
  //    ⚠️ 静默安装直接跳过：更新器走的静默流程，弹框会被 /SUPPRESSMSGBOXES 吞掉；
  //       而且更新场景下应用本来就已经在运行（能跑 = 补丁早就装了），没必要再拦。
  //    ⚠️ 这里**只提示、不中止**：补丁要管理员权限装，先让文件到位，
  //       用户（或管理员）打完补丁开机就能用，不用再装一遍。
  //    ⚠️ 故意排在最后：HasDllDirectoryApi 会调用 SetDefaultDllDirectories，
  //       那会改掉本进程的 DLL 搜索目录。放到这个位置，Setup 自己需要的库都已就位。
  if (not WizardSilent) and (not HasDllDirectoryApi()) then
  begin
    Choice := MsgBox(
      '这台电脑缺少一个 Windows 更新，装上之前应用打不开。' + #13#10 + #13#10 +
      'ClassSoftwareHub 用的是 .NET 6 运行时，它在 Windows 7 上依赖 KB3063858' +
      '（早期版本号是 KB2533623，现已并入后续累积更新）。' + #13#10 +
      '没装这个更新时，双击程序会「闪一下就退出」，事件查看器里能看到' +
      '「Failed to load the dll from [...hostfxr.dll]」。' + #13#10 + #13#10 +
      '⚠️ 这不是安装包的问题：重装、换版本、改用便携版都绕不过去，必须先打这个补丁。' + #13#10 +
      '补丁装完如果提示重启，重启之后再打开本应用。' + #13#10 +
      '（教室里的机器一般由管理员统一打补丁，可以把这个编号告诉老师。）' + #13#10 + #13#10 +
      '要现在打开微软更新目录去找这个补丁吗？' + #13#10 +
      '选「否」仍会继续安装，但装完也打不开。',
      mbConfirmation, MB_YESNO);
    if Choice = IDYES then
      OpenUrl(KbUpdatePage);
  end;
end;

// 「附加任务」页里「创建桌面快捷方式」的默认勾选状态：
//   · 首次安装 → 默认勾上（用户可自行取消）；
//   · 升级 → 尊重用户当前选择：桌面上还留着旧图标就勾上保留它，
//            用户之前删了就别再自动建回来（Nick 2026-10-17 反馈的 bug）。
// ⚠️ 静默升级（更新器）的 /TASKS 由 UpdateService 按同一逻辑给出，这里主要管交互式重跑时的预填；
//    WizardSelectTasks 在向导起始阶段调用，对静默安装同样生效（静默不显示该页，以程序设定为准）。
procedure InitializeWizard();
var
  IsUpdate, WantIcon: Boolean;
  UninstKey: String;
begin
  UninstKey := AppIdGuid + '_is1';
  IsUpdate :=
    RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + UninstKey) or
    RegKeyExists(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + UninstKey);

  if not IsUpdate then
    WantIcon := True   // 首次安装：默认创建桌面图标（用户可取消）
  else
    WantIcon := FileExists(ExpandConstant('{autodesktop}\{#AppName}.lnk')); // 升级：按现状保留

  if WantIcon then
    WizardSelectTasks('desktopicon');
end;