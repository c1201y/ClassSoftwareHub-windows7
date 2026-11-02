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
    [switch]$Zip,

    # 官方构建签名（见 tools\make-build-stamp.ps1）。⚠️ 签名是在 publish 阶段编进程序集的，
    # 这里只做**校验**：拿它的 base64 去 ClassSoftwareHub.dll 里找，找不到就报错。
    #
    # -BuildStampB64 直接给 base64（CI 用，不必落盘明文签名）；
    # -BuildStamp    给签名文件路径（本地用）。
    # 两者都没给时依次找 dist\build-stamp.txt、.secrets\build-stamp.txt；都没有则报错
    # —— 免得把「社区版」当成正式包发出去。
    [string]$BuildStamp,
    [string]$BuildStampB64,
    [switch]$SkipLicenseStamp
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

# ── 3. 校验官方构建签名确实编进了 DLL ────────────────────────────────
# 签名在 **publish 阶段**由 csproj 的 AssemblyMetadata 写进程序集，这里只做校验 ——
# 打包脚本跑在 publish 之后，改不了二进制，能做的就是别让没签名的包溜出去。

function Get-StampBase64([string]$path) {
    $line = (Get-Content -LiteralPath $path -Raw).Trim()
    if (-not $line) { throw "签名文件是空的：$path" }
    return [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($line))
}

$stampB64 = $BuildStampB64
$stampSource = '-BuildStampB64'

if (-not $stampB64) {
    $stampPath = $BuildStamp
    if (-not $stampPath) {
        foreach ($candidate in @(
                (Join-Path $outRootFull 'build-stamp.txt'),
                (Join-Path (Split-Path -Parent $PSScriptRoot) '.secrets\build-stamp.txt'))) {
            if (Test-Path -LiteralPath $candidate) { $stampPath = $candidate; break }
        }
    }
    if ($stampPath -and (Test-Path -LiteralPath $stampPath)) {
        $stampB64 = Get-StampBase64 $stampPath
        $stampSource = Split-Path -Leaf $stampPath
    }
}

# AssemblyMetadata 的字符串参数在元数据里是 UTF-8（SerString），字符串常量才走 UTF-16 的 #US 堆。
# 两种都试一遍，免得哪天换了注入方式就静默失效。
function Test-EmbeddedStamp([string]$dllPath, [string]$b64) {
    if (-not (Test-Path -LiteralPath $dllPath)) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($dllPath)
    if ($bytes.Length % 2 -ne 0) {
        $bytes = $bytes[0..($bytes.Length - 2)]
    }
    return ([System.Text.Encoding]::UTF8.GetString($bytes).Contains($b64) -or
            [System.Text.Encoding]::Unicode.GetString($bytes).Contains($b64))
}

if ($SkipLicenseStamp) {
    Say '⚠ 跳过签名校验（产物将被判定为社区版，需要激活）'
}
elseif ($stampB64) {
    $dll = Join-Path $appDirFull 'ClassSoftwareHub.dll'
    if (-not (Test-EmbeddedStamp $dll $stampB64)) {
        throw @"
构建签名没有编进程序集：$(Split-Path -Leaf $dll) 里找不到对应签名（来源：$stampSource）。

   签名是在 publish 阶段注入的，打包脚本改不了二进制。正确顺序：

     pwsh tools\make-build-stamp.ps1 -Version $Version
     # 用它输出的 base64 去 publish：
     dotnet publish src\ClassSoftwareHub.Win7.csproj -c Release -r win-x64 --self-contained true ``
         -p:PublishTrimmed=false -p:PublishReadyToRun=true ``
         -p:CshBuildStampB64=<base64> -o dist\win7\app

   然后再跑本脚本。确实要打不带签名的包（内部自测）时，加 -SkipLicenseStamp。
"@
    }
    Say "✓ 构建签名已编入程序集（来源：$stampSource）"
}
else {
    throw @"
缺少构建签名（-BuildStampB64 / -BuildStamp 都没给，也没找到 build-stamp.txt）。

官方包必须带签名，否则用户装上是「社区版」，在线服务会要求激活。
先生成：
  pwsh tools\make-build-stamp.ps1 -Version $Version

确实要打一个不带签名的包（内部自测）时，加 -SkipLicenseStamp。
"@
}

# ── 4. 使用说明 ───────────────────────────────────────────────────────
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

# ── 5. 打 zip ─────────────────────────────────────────────────────────
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
