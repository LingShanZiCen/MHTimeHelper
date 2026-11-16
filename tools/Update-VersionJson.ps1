# 发版时用：把打包好的 exe 的 sha256 与体积写回仓库根目录 version.json
#
# 用法（在仓库根目录执行）：
#   powershell -ExecutionPolicy Bypass -File tools\Update-VersionJson.ps1 -Exe ..\dist\Nightforge.exe -Version 1.0.4
#
# 作用：
#   1) version 改成指定版本号；
#   2) exeUrl 指向该版本 Release 里的 Nightforge.exe，并默认走加速镜像
#      （部分网络下 github.com 的 releases/download 域名不可用，直链会让自动更新卡住）；
#   3) 同时写入 exeUrls 多地址列表：镜像优先、官方直链兜底，1.0.4 起的客户端会依次尝试；
#   4) sha256 / size 由本地 exe 实测填入——客户端下载后会拿这两个值校验，填错会导致更新被丢弃。

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$jsonPath = Join-Path $root "version.json"
$exePath = (Resolve-Path $Exe).Path

if (-not (Test-Path $jsonPath)) {
    throw "找不到 $jsonPath，请在仓库内执行本脚本"
}

$ver = $Version.Trim().TrimStart('v', 'V')
$direct = "https://github.com/LingShanZiCen/MHTimeHelper/releases/download/v$ver/Nightforge.exe"
$mirrors = @(
    "https://gh-proxy.com/$direct",
    "https://ghfast.top/$direct",
    "https://ghproxy.net/$direct"
)

$hash = (Get-FileHash $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $exePath).Length

$raw = [System.IO.File]::ReadAllText($jsonPath, [System.Text.Encoding]::UTF8)
$raw = [regex]::Replace($raw, '"version"\s*:\s*"[^"]*"', "`"version`": `"$ver`"")
$raw = [regex]::Replace($raw, '"exeUrl"\s*:\s*"[^"]*"', "`"exeUrl`": `"$($mirrors[0])`"")
$raw = [regex]::Replace($raw, '"sha256"\s*:\s*"[^"]*"', "`"sha256`": `"$hash`"")
$raw = [regex]::Replace($raw, '"size"\s*:\s*\d+', "`"size`": $size")

# 旧的 exeUrls（不论单行还是多行）先清掉，再按新版本重建
$raw = [regex]::Replace($raw, '(?s)\s*"exeUrls"\s*:\s*\[[^\]]*\],', '')

$all = @($mirrors + @($direct)) | ForEach-Object { "`"$_`"" }
$arrayText = "`"exeUrls`": [" + ($all -join ", ") + "],"
$raw = [regex]::Replace($raw, '("exeUrl"\s*:\s*"[^"]*",)', "`$1`r`n  $arrayText")

[System.IO.File]::WriteAllText($jsonPath, $raw, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "version.json 已更新："
Write-Host "  版本   : $ver"
Write-Host "  体积   : $size 字节"
Write-Host "  sha256 : $hash"
Write-Host "  镜像   : $($mirrors.Count) 条 + 官方直链 1 条"
Write-Host ""
Write-Host "接下来：把 exe 以 Nightforge.exe 为名上传到 GitHub Release v$ver，再提交 version.json 到 main。"
