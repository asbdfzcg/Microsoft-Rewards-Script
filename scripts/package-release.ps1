<#
.SYNOPSIS
    生成 Microsoft Rewards Script 便携发布包（极简运行时整包）。

.DESCRIPTION
    只收集"运行时必需"的文件，剔除源码/开发配置，
    压成一个 zip，用户下载解压后双击 autorun/RewardsManager.exe 即可。
    包内已带编译产物 dist/，故用户端无需 TypeScript 源码与 tsc 构建；
    缺失的运行依赖（Node / node_modules / 浏览器）由向导自动下载安装。

    用法：
        pwsh scripts/package-release.ps1
        pwsh scripts/package-release.ps1 -IncludeDocs
#>
param(
    [string]$OutputDir = "$PSScriptRoot/../release",
    [switch]$IncludeDocs
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path "$PSScriptRoot/.."
$Staging  = Join-Path $env:TEMP "mrs-release-staging"
$Log      = Join-Path $env:TEMP "mrs-build-summary.txt"
"=== build $(Get-Date) ===" | Out-File $Log -Encoding utf8
function Log($m) { $m | Out-File $Log -Append -Encoding utf8; Write-Host $m }

# 读版本号
$pkg     = Get-Content (Join-Path $RepoRoot "package.json") -Raw | ConvertFrom-Json
$version = $pkg.version
Log "version=$version"

# 白名单：只打进包的文件（相对仓库根）。每行一个元素，避免行内注释吞掉下一行。
$files = @(
    "package.json",
    "package-lock.json",
    "config.example.json",
    "env.example",
    "dist/",
    "autorun/RewardsManager.exe",
    "autorun/run-rewards.ps1",
    "autorun/setup-task.ps1",
    "autorun/diagnose.ps1",
    "autorun/run-manual.bat",
    "autorun/setup-task.bat"
)
if ($IncludeDocs) { $files += @("README.md", "LICENSE") }

# ---- 暂存 ----
if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
New-Item $Staging -ItemType Directory | Out-Null

function Copy-Rel($rel) {
    $src = Join-Path $RepoRoot $rel
    if (-not (Test-Path $src)) { Log "WARN skip missing: $rel"; return }
    $dst = Join-Path $Staging $rel
    New-Item (Split-Path $dst) -ItemType Directory -Force | Out-Null
    Copy-Item $src $dst -Recurse -Force
    Log "+ $rel"
}

Log "暂存文件 -> $Staging"
foreach ($f in $files) { Copy-Rel $f }

# ---- 压缩（用 python 更可靠，规避 PowerShell 文件锁/缓冲问题）----
try {
    New-Item $OutputDir -ItemType Directory -Force | Out-Null
    $zip = Join-Path $OutputDir "Microsoft-Rewards-Script-portable-v$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    $py = @(Get-Command python, py -ErrorAction SilentlyContinue)[0]
    if (-not $py) { $py = "C:/Users/asbdf/.workbuddy/binaries/python/versions/3.13.12/python.exe" }
    Log "using python: $py"

    $code = "import zipfile,os;src=r'$Staging';dst=r'$zip';z=zipfile.ZipFile(dst,'w',zipfile.ZIP_DEFLATED);[z.write(os.path.join(r,f),os.path.relpath(os.path.join(r,f),src)) for r,_,fs in os.walk(src) for f in fs];z.close();print('OK',os.path.getsize(dst))"
    $pyOut = & $py -c $code 2>&1
    Log "python zip result: $pyOut"
    Log "已生成发布包: $zip"
    Log "版本: v$version   大小: $([math]::Round((Get-Item $zip).Length / 1MB, 2)) MB"
    Log "内容: package.json, config.example.json, env.example, dist/(编译产物), autorun/(RewardsManager.exe + 脚本)"
    Log "用户解压后: 双击 autorun/RewardsManager.exe -> 向导自动装 Node/依赖/浏览器"
} catch {
    Log "ZIP ERROR: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    exit 1
}
