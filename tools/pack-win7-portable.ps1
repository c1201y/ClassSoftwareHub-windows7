# ══════════════════════════════════════════════════════════════════════
#  pack-win7-portable.ps1 — 组装 Windows 7 便携版（启动器 + app 文件夹）
#
#  用法（在工程根目录）：
#    pwsh tools\pack-win7-portable.ps1 `
#         -AppDir      dist\win7\app `
#         -LauncherDir dist\win7 `
#         -Version     1.0.0 `
#         -OutRoot     dist `
#         -Zip
#
#  产物：
#    <OutRoot>\ClassSoftwareHub-Win7-Portable\
#        ClassSoftwareHub.exe     ← 启动器（用户双击这个）
#        app\                     ← 应用本体（publish 产物，含 content\ / ContentIcons\）
#        使用说明.txt
#    <OutRoot>\ClassSoftwareHub-Portable-win7-dv<Version>.zip   （带 -Zip 时）
#
#  ⚠️ zip 名字里必须有 "portable" —— 客户端的更新器会**跳过**名字含 portable 的资产，
#     免得把便携包误当成安装包去静默安装（见 GitHubReleaseSource.PickInstallers）。
# ══════════════════════════════════════════════════════════════════════

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [Parameter(Mandatory = $true)][string]$LauncherDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutRoot = 'dist',
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'

function Say([string]$text) { Write-Host "  $text" }

# ── 路径准备 ──────────────────────────────────────────────────────────
$appDirFull = (Resolve-Path -LiteralPath $AppDir).ProviderPath
$launcherDirFull = (Resolve-Path -LiteralPath $LauncherDir).ProviderPath

if (-not (Test-Path -LiteralPath $OutRoot)) {
    New-Item -ItemType Directory -Path $OutRoot -Force | Out-Null
}
$outRootFull = (Resolve-Path -LiteralPath $OutRoot).ProviderPath

$portableDir = Join-Path $outRootFull 'ClassSoftwareHub-Win7-Portable'
$launcherExe = Join-Path $launcherDirFull 'ClassSoftwareHub.exe'
$appExe = Join-Path $appDirFull 'ClassSoftwareHub.exe'

if (-not (Test-Path -LiteralPath $launcherExe)) {
    throw "找不到启动器：$launcherExe （先发布 src\Launcher\ClassSoftwareHub.Launcher.csproj）"
}
if (-not (Test-Path -LiteralPath $appExe)) {
    throw "找不到应用本体：$appExe （先发布 src\ClassSoftwareHub.Win7.csproj）"
}

Write-Host '── 组装便携版 ──────────────────────────────'
Say "应用本体   : $appDirFull"
Say "启动器     : $launcherExe"
Say "输出版本号 : $Version"
Say "目标目录   : $portableDir"

# ── 清空重建 ──────────────────────────────────────────────────────────
if (Test-Path -LiteralPath $portableDir) {
    Remove-Item -LiteralPath $portableDir -Recurse -Force
}
$appDest = Join-Path $portableDir 'app'
New-Item -ItemType Directory -Path $appDest -Force | Out-Null

# ── 1. 启动器 → 根目录 ────────────────────────────────────────────────
Copy-Item -LiteralPath $launcherExe -Destination (Join-Path $portableDir 'ClassSoftwareHub.exe') -Force
Say '✓ 启动器 → 根目录'

# ── 2. 应用本体 → app\ ────────────────────────────────────────────────
Copy-Item -Path (Join-Path $appDirFull '*') -Destination $appDest -Recurse -Force
$appFiles = Get-ChildItem -LiteralPath $appDest -Recurse -File
$appMb = [math]::Round((($appFiles | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Say "✓ 应用本体 → app\ （$($appFiles.Count) 个文件，$appMb MB）"

# ── 3. 使用说明 ───────────────────────────────────────────────────────
$readme = @"
ClassSoftwareHub · Windows 7 便携版  dv$Version
────────────────────────────────────────────────

这是什么
  解压就能用，不用安装（解压到哪都行，U 盘也行）。
  需要 Windows 7 SP1（32/64 位皆可，本包是 64 位程序）及以上。

怎么用
  双击「ClassSoftwareHub.exe」即可启动。
  ⚠️ 「app」文件夹必须和它放在一起 —— 那是程序本体，单独拿走 exe 是打不开的。

关于更新
  便携版不会自动替换自己（正在运行的程序没法覆盖自己）。
  有新版本时，请到发布页下载新的便携包，解压后**替换掉整个 app 文件夹**即可，
  你自己放在别处的设置（在 %LOCALAPPDATA%\ClassSoftwareHub）不会丢。

想装成正式版？
  到发布页下载「ClassSoftwareHub-Setup-win7-dv$Version.exe」，双击安装，
  会装到 开始菜单 / 桌面，之后的更新可以在应用里一键完成。

出问题了
  程序目录旁边会生成 port.log（排障日志），反馈时把它一起发过来即可。
"@
$readmePath = Join-Path $portableDir '使用说明.txt'
# ⚠️ 说明文件写成 **带 BOM 的 UTF-8**：Win7 记事本对无 BOM 的 UTF-8 会按 ANSI 解，
#    中文会变成一片乱码 —— 用户第一眼看到的就是这个文件，不能砸。
[System.IO.File]::WriteAllText($readmePath, $readme, (New-Object System.Text.UTF8Encoding($true)))
Say '✓ 使用说明.txt'

# ── 4. 打 zip ─────────────────────────────────────────────────────────
if ($Zip) {
    $zipName = "ClassSoftwareHub-Portable-win7-dv$Version.zip"
    $zipPath = Join-Path $outRootFull $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

    # 把**目录本身**压进去：用户解压出来是一个完整的文件夹，而不是散落一地的文件
    Compress-Archive -Path $portableDir -DestinationPath $zipPath -CompressionLevel Optimal -Force

    $zipMb = [math]::Round(((Get-Item -LiteralPath $zipPath).Length / 1MB), 1)
    Say "✓ $zipName （$zipMb MB）"
}

Write-Host ''
Write-Host "完成：$portableDir"
