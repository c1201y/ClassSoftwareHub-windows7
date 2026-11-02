<#
.SYNOPSIS
    生成官方构建签名（build-stamp.txt）—— 发布打包前跑一次。

.DESCRIPTION
    仓库是公开的，所以「哪些二进制算官方版」不能靠源码里的常量，只能靠一把**不在仓库里**的
    密钥签出来。本脚本用发布密钥对构建号做 HMAC-SHA256，产出三行/一行的签名文件，
    打包时塞进 app 目录。客户端会把构建号与签名随请求发给服务端，服务端用同一把密钥验签。

    密钥来源（按优先级）：
      1. 环境变量 $env:CSH_BUILD_SECRET
      2. -Secret 参数
      3. .secrets\build-secret.txt（已在 .gitignore 里挡住）

    ⚠️ 密钥一旦泄露，等于任何人都能伪造官方构建 —— 换密钥、重新发版即可止血
       （服务端同时更新密钥后，旧包的签名立刻失效）。

.EXAMPLE
    pwsh tools\make-build-stamp.ps1 -Version 1.2.0-insider1.5

.EXAMPLE
    # 构建号固定，便于复现某次发布
    pwsh tools\make-build-stamp.ps1 -Version 1.2.0 -BuildId "dv1.2.0+20261102T0730Z" -OutFile dist\build-stamp.txt
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,

    # 构建号。留空 = dv<版本号>+UTC时间戳
    [string]$BuildId,

    [string]$Secret,

    [string]$SecretFile = (Join-Path (Split-Path -Parent $PSScriptRoot) '.secrets\build-secret.txt'),

    [string]$OutFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\build-stamp.txt')
)

$ErrorActionPreference = 'Stop'

function Resolve-Secret {
    if ($env:CSH_BUILD_SECRET) { return $env:CSH_BUILD_SECRET.Trim() }
    if ($Secret) { return $Secret.Trim() }
    if (Test-Path -LiteralPath $SecretFile) {
        $text = (Get-Content -LiteralPath $SecretFile -Raw).Trim()
        if ($text) { return $text }
    }
    throw @"
找不到构建密钥。请任选一种方式提供：
  1. 设置环境变量  $env:CSH_BUILD_SECRET = '<密钥>'
  2. 传参          -Secret '<密钥>'
  3. 写入文件      $SecretFile
（服务端的 CSH_BUILD_SECRET 必须是同一个值，否则验签不通过。）
"@
}

if (-not $BuildId) {
    $BuildId = "dv$Version+" + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmZ')
}

if ($BuildId.Contains('|')) { throw '构建号里不能含 "|"（它是签名文件的分隔符）' }
if ($BuildId.Contains(' ')) { throw '构建号里不能含空格' }

$key = Resolve-Secret
$payload = "v1|$BuildId"

$hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($key))
try {
    $raw = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($payload))
} finally {
    $hmac.Dispose()
}

$signature = -join ($raw | ForEach-Object { $_.ToString('x2') })
$issued = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$line = "v1|$BuildId|$signature|$issued"

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path -LiteralPath $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

# 无 BOM：客户端按行读，BOM 会混进版本字段
[System.IO.File]::WriteAllText($OutFile, $line + "`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host '签名已生成'
Write-Host "  构建号 : $BuildId"
Write-Host "  签发   : $issued"
Write-Host "  输出   : $OutFile"
Write-Host ''
$b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($line))
Write-Host '签名要编进程序集，所以先算 base64、再 publish、最后打包：'
Write-Host "  `$b64 = '$b64'"
Write-Host '  dotnet publish src\ClassSoftwareHub.Win7.csproj -c Release -r win-x64 --self-contained true `'
Write-Host '      -p:PublishTrimmed=false -p:PublishReadyToRun=true `'
Write-Host "      -p:CshBuildStampB64=`$b64 -o dist\win7\app"
Write-Host "  pwsh tools\pack-win7-portable.ps1 -AppDir dist\win7\app -LauncherDir dist\win7-launcher -Version $Version -OutRoot dist -Zip -BuildStamp `"$OutFile`""
Write-Host ''
Write-Host '不想让明文签名留在磁盘上，可以只传 base64：'
Write-Host "  pwsh tools\pack-win7-portable.ps1 ... -BuildStampB64 `"`$b64`""
