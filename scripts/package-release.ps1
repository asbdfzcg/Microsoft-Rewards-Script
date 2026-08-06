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

# 运行时不需要的文件类型（TypeScript 声明与 source map），从发布包剔除
$ExcludeRe = '\.(d\.ts|js\.map)$'

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

# ---- 确保 dist/ 编译产物存在（缺失则自动构建，杜绝产出无 dist 的坏包）----
$distEntry = Join-Path $RepoRoot "dist/index.js"
if (-not (Test-Path $distEntry)) {
    Log "dist/index.js 缺失，尝试自动构建 (npm run build) ..."
    $npm = Get-Command npm -ErrorAction SilentlyContinue
    if (-not $npm) { throw "未找到 npm，无法自动构建。请在开发机先执行 npm run build 再打包。" }
    Push-Location $RepoRoot
    try {
        $buildOut = & npm run build 2>&1
        if ($LASTEXITCODE -ne 0) {
            $buildOut | ForEach-Object { Log ("  build> " + $_) }
            throw "npm run build 失败，退出码 $LASTEXITCODE"
        }
    } catch {
        throw "自动构建失败: $_"
    } finally { Pop-Location }
    if (-not (Test-Path $distEntry)) { throw "自动构建后仍缺少 dist/index.js，请检查构建配置。" }
    Log "自动构建完成，dist/ 已就绪"
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
    Log "内容: package.json, config.example.json, env.example, dist/(编译产物, 已剔除 .d.ts/.js.map), autorun/(RewardsManager.exe + 脚本)"
    Log "用户解压后: 双击 autorun/RewardsManager.exe -> 向导自动装 Node/依赖/浏览器"
} catch {
    Log "ZIP ERROR: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    exit 1
}
