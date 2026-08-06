<#
.SYNOPSIS
    生成 Microsoft Rewards Script 便携发布包（极简运行时整包）。

.DESCRIPTION
    只收集"运行时必需"的文件 + 构建所需的最小源码（tsconfig.json + src/），
    并剔除 src/ 中的测试/示例/开发产物，压成一个 zip。用户下载解压后双击
    autorun/RewardsManager.exe 即可，向导会自动 npm install 并从源码构建 dist/。
    发布包不含 dist/，用户修改配置后重新运行向导即可重建 dist/；
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
$RepoRoot = (Resolve-Path "$PSScriptRoot/..").Path
$Staging  = Join-Path $env:TEMP "mrs-release-staging"
$Log      = Join-Path $env:TEMP "mrs-build-summary.txt"
"=== build $(Get-Date) ===" | Out-File $Log -Encoding utf8
function Log($m) { $m | Out-File $Log -Append -Encoding utf8; Write-Host $m }

# 读版本号
$pkg     = Get-Content (Join-Path $RepoRoot "package.json") -Raw | ConvertFrom-Json
$version = $pkg.version
Log "version=$version"

# 白名单：只打进包的文件（相对仓库根）。每行一个元素，避免行内注释吞掉下一行。
# 不含 dist/（由向导在用户机从 tsconfig.json + src/ 构建）；含构建所需最小源码。
$files = @(
    "package.json",
    "package-lock.json",
    "config.example.json",
    ".env.example",
    "env.example",
    "tsconfig.json",
    "src/",
    "scripts/main/copyAssets.js",
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

# 源码头裁剪：剔除测试/示例/开发产物（发布包只留运行+构建所需的最小源码）
$ExcludeRe = '(?:\\|/)(?:__tests__|examples|fixtures|node_modules)(?:\\|/)|(?:test|spec)\.ts$|\.d\.ts$|\.js\.map$'

function Copy-Rel($rel) {
    $src = Join-Path $RepoRoot $rel
    if (-not (Test-Path $src)) { Log "WARN skip missing: $rel"; return }
    $dst = Join-Path $Staging $rel
    New-Item (Split-Path $dst) -ItemType Directory -Force | Out-Null
    if (Test-Path -PathType Container $src) {
        # 目录：递归拷贝，跳过被排除的文件（.d.ts / .js.map）
        Get-ChildItem $src -Recurse | Where-Object {
            -not $_.PSIsContainer -and $_.FullName -notmatch $ExcludeRe
        } | ForEach-Object {
            $rel2 = $_.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
            $target = Join-Path $Staging $rel2
            New-Item (Split-Path $target) -ItemType Directory -Force | Out-Null
            Copy-Item $_.FullName $target -Force
        }
    } else {
        Copy-Item $src $dst -Force
    }
    Log "+ $rel"
}

# 发布包不含 dist/，dist 由向导在用户机从 tsconfig.json + src/ 构建，无需打包前构建。

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
    Log "内容: package.json, config.example.json, .env.example, env.example, tsconfig.json, src/(已裁剪测试/示例), scripts/main/copyAssets.js, autorun/(RewardsManager.exe + 脚本)"
    Log "用户解压后: 双击 autorun/RewardsManager.exe -> 向导自动装 Node/依赖/浏览器 -> 从源码构建 dist/"
} catch {
    Log "ZIP ERROR: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    exit 1
}
