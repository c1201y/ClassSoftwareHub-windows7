# 自己编译 / 发版

本仓库只有 **Windows 7 移植版**一套工程（`src-win7\`）。

## 编译环境

- .NET SDK **6.0**（`dotnet --version` 应为 6.x）
- Windows x64（目标 `win-x64`）
- 打安装包另需 Inno Setup 6

## 调试构建

```powershell
dotnet build src-win7\ClassSoftwareHub.Win7.csproj -c Release
```

exe 在 `src-win7\bin\Release\net6.0-windows7.0\win-x64\ClassSoftwareHub.exe`。

> 开发机上可加 `--simulate-win7` 参数强制走 Win7 判定分支
> （不用真装一台 Win7 就能复现教室机的代码路径，见 `src-win7\Platform\OsInfo.cs`）。

## 产物形状：启动器 + 应用程序文件夹

两层结构 —— 根目录只有启动器一个 exe，应用本体全在 `app\` 里：

```
ClassSoftwareHub.exe      ← 启动器（src-win7\Launcher，自包含 + 裁剪 + 单文件，约 9 MB）
app\                      ← 应用本体（publish 产物，含 content\ 与 ContentIcons\）
使用说明.txt              ← 便携版才有
```

好处：以后更新只换 `app\` 子目录 —— 启动器不动，桌面上快捷方式指向的路径永远有效。

## 打便携版

```powershell
# 1. 应用本体（WinExe：不带 CshConsoleBuild，双击不出黑窗）
dotnet publish src-win7\ClassSoftwareHub.Win7.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishTrimmed=false -p:PublishReadyToRun=true -o dist\win7\app

# 2. 启动器
dotnet publish src-win7\Launcher\ClassSoftwareHub.Launcher.csproj -c Release -r win-x64 -o dist\win7

# 3. 组装 + 打 zip
.\tools\pack-win7-portable.ps1 -AppDir dist\win7\app -LauncherDir dist\win7 `
  -Version 1.0.0 -OutRoot dist -Zip
```

产物：`dist\ClassSoftwareHub-Win7-Portable\`（目录）与 `dist\ClassSoftwareHub-Portable-win7-dv<版本>.zip`。

> ⚠️ **别加 `-p:CshConsoleBuild=true`** —— 那会把子系统从 Windows 换成控制台，双击多弹一个黑窗。
> 那个开关只用于现场排障（见 `src-win7\Platform\DiagConsole.cs`），产物不进发布包。
>
> ⚠️ 便携包 zip 的名字里**必须含 `portable`** —— 客户端的更新器会跳过含这个词的资产，
> 免得把整个便携包当成安装包去静默安装。

## 打安装版（Inno Setup 6）

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" `
  installer\ClassSoftwareHub-Win7.iss /DDesktopVersion=1.0.0
```

产物：`dist\installer\ClassSoftwareHub-Setup-win7-dv<版本>.exe`。

脚本的 `[Files]` 读 `..\dist\win7\ClassSoftwareHub.exe` 与 `..\dist\win7\app\*`，
所以**先跑上面「打便携版」的第 1、2 步**再编译安装包。

安装版与原版可同机共存：安装目录（`...\Programs\ClassSoftwareHub-Win7`）与 Inno 的 AppId 都不同。

## 发版

**① 打 tag 自动发（推荐）**

```powershell
git tag dv1.0.0
git push origin dv1.0.0
```

`.github/workflows/win7-release.yml` 会：构建应用本体与启动器 → 组装便携版 → 编译安装包 →
上传 artifact 留档 → 创建 Release（tag 里带 `insider` 自动勾 Pre-release）。

也可以到 Actions 页面手动 `Run workflow`（可填版本号、更新说明，勾 `publish` 就一并发 Release）。

**② 本地手动发**

```powershell
$env:GITHUB_TOKEN = "..."     # 需要仓库写权限，别写进任何文件
node tools\publish-release-win7.mjs `
  --version 1.0.0 --channel stable `
  --installer "dist\installer\ClassSoftwareHub-Setup-win7-dv1.0.0.exe" `
  --portable  "dist\ClassSoftwareHub-Portable-win7-dv1.0.0.zip" `
  --notes "dist\release\notes.md"
```

加 `--dry-run` 可以先看摘要（只算哈希、写 `.md5`，不碰 GitHub）。

## 更新通道

本仓库**就是** Win7 版的更新源 —— 与 WinUI 版（`ClassSoftwareHub-Desktop`）的 Release 列表
天然隔离，所以 tag 就是干净的 `dv1.0.0`（正式版）/ `dv1.0.0-insider1.1`（预览版），**不带任何前缀**。

客户端只读本仓库的 Release：正式版取 `Latest`（非预发布），预览版取 Pre-release
（常量在 `src-win7\Core\ShellConfig.cs` 的 `UpdateRepoOwner` / `UpdateRepoName`）。

> 机制上仍保留了「tag 前缀过滤」（`UpdateTagPrefix`，当前为空串）——
> 万一哪天又想把两版合回一个仓库，填上前缀即可；发版脚本里的 `TAG_PREFIX` 要同步改。

## 版本号规则

`dv` + `主.功能.补丁` + `-insider架构.迭代`，唯一来源是 `src-win7\Core\ShellConfig.cs` 的 `ShellVersion`：

- 第一位「架构」—— 只有整个应用的技术路线发生重大变动才动；
- 第二位「功能」—— 每叠加一块新功能涨一次；
- 第三位「补丁」—— 小功能 / 小更新 / 小修复；
- `insider` 两位 —— 预览线自己的基线与迭代次数。

做出一个能用的版本 → 发 `1.0.0-insider1.0`；一直改到没问题 → 删掉 `-insider` 后缀 → 上线正式版 `1.0.0`。
