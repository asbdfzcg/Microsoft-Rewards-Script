# run-rewards.ps1 - Microsoft Rewards Script 自动运行主脚本
# 由计划任务或 run-manual.bat 调用
# 用法: powershell -File run-rewards.ps1 [-Force]
#   -Force  跳过时间检查与"今日已运行"检查（手动运行时使用）

param(
    [switch]$Force
)

# 设置窗口标题（避免在 Windows Terminal 命令行中传中文标题触发 Node.js 断言失败）
try { $Host.UI.RawUI.WindowTitle = 'Microsoft Rewards Script' } catch {}

# 统一使用 UTF-8 编码输出日志，避免 WinForms 读取时中文乱码
$OutputEncoding = [System.Text.Encoding]::UTF8
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ===================== 可配置参数 =====================
$MinHour            = 7     # 最早执行时间（24小时制）
$NetworkMaxRetries  = 20    # 网络检测最大重试次数
$NetworkRetryDelay  = 90    # 每次重试间隔（秒），20 x 90s ≈ 30 分钟
# ======================================================

$ErrorActionPreference = 'Continue'
$AutorunDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir  = Split-Path -Parent $AutorunDir
$LogsDir     = Join-Path $AutorunDir 'logs'
$LockFile    = Join-Path $AutorunDir '.run-lock'
$LastRunFile = Join-Path $AutorunDir '.last-run-date'
$SkipLog     = Join-Path $LogsDir 'skip.log'
$ErrorLog    = Join-Path $LogsDir 'error.log'

if (-not (Test-Path $LogsDir)) { New-Item -ItemType Directory -Path $LogsDir -Force | Out-Null }

$timestamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
$RunLog = Join-Path $LogsDir "run_$timestamp.log"

function Write-Skip([string]$msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"
    Add-Content -Path $SkipLog -Value $line -Encoding UTF8
}
function Write-Err([string]$msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"
    Add-Content -Path $ErrorLog -Value $line -Encoding UTF8
}

# 自动探测 Node.js 可执行文件路径（优先 PATH，其次常见安装目录与 tools/node）
function Find-Node {
    $p = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($p) { return $p.Source }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'nodejs\node.exe'),
        'C:\Program Files\nodejs\node.exe',
        'D:\Program Files\nodejs\node.exe',
        (Join-Path $ProjectDir 'tools\node\node.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

# ---------- 1. 等待 1 秒后最小化 Windows Terminal 窗口 ----------
Start-Sleep -Seconds 1
try {
    Add-Type -Namespace Win32 -Name WindowApi -MemberDefinition @'
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(System.IntPtr hWnd, int nCmdShow);
'@
    $wtProc = Get-Process | Where-Object { $_.ProcessName -match 'WindowsTerminal|wt' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($wtProc) { [Win32.WindowApi]::ShowWindowAsync($wtProc.MainWindowHandle, 6) | Out-Null } # 6 = SW_MINIMIZE
} catch {}

# ---------- 2. 文件锁防双实例 ----------
if (Test-Path $LockFile) {
    $lockAge = (Get-Date) - (Get-Item $LockFile).LastWriteTime
    if ($lockAge.TotalHours -lt 3) {
        Write-Skip "另一个实例正在运行（锁文件存在，创建于 $($lockAge.TotalMinutes.ToString('0')) 分钟前），退出"
        exit 0
    }
    Remove-Item $LockFile -Force -ErrorAction SilentlyContinue # 残留锁（>3小时）自动清理
}
New-Item -ItemType File -Path $LockFile -Force | Out-Null

try {
    # ---------- 3. 刷新 PATH，移除可能干扰的条目，让系统 Node 可被 PATH 找到 ----------
    $machinePath = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath    = [System.Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = ($machinePath + ';' + $userPath -split ';' |
        Where-Object { $_ -and $_ -notmatch 'TRAE|workbuddy[\\/]binaries' } |
        Select-Object -Unique) -join ';'

    # ---------- 4. 时间检查 ----------
    if (-not $Force -and (Get-Date).Hour -lt $MinHour) {
        Write-Skip "当前时间早于 ${MinHour}:00，跳过本次运行"
        exit 0
    }

    # ---------- 5. 当天防重复 ----------
    $today = Get-Date -Format 'yyyy-MM-dd'
    if (-not $Force -and (Test-Path $LastRunFile)) {
        $lastRun = (Get-Content $LastRunFile -Raw -ErrorAction SilentlyContinue).Trim()
        if ($lastRun -eq $today) {
            Write-Skip "今天（$today）已成功运行过，跳过"
            exit 0
        }
    }

    # ---------- 6. 等待网络就绪（DNS + HTTP 双重检测） ----------
    $networkReady = $false
    for ($i = 1; $i -le $NetworkMaxRetries; $i++) {
        $dnsOk = $false
        $httpOk = $false
        try {
            Resolve-DnsName -Name 'www.bing.com' -ErrorAction Stop | Out-Null
            $dnsOk = $true
        } catch {}
        try {
            $resp = Invoke-WebRequest -Uri 'https://www.bing.com' -Method Head -TimeoutSec 15 -UseBasicParsing -ErrorAction Stop
            if ($resp.StatusCode -lt 500) { $httpOk = $true }
        } catch {}
        if ($dnsOk -and $httpOk) { $networkReady = $true; break }
        Start-Sleep -Seconds $NetworkRetryDelay
    }
    if (-not $networkReady) {
        Write-Err "等待网络超过 $($NetworkMaxRetries * $NetworkRetryDelay / 60) 分钟仍未就绪，本次运行失败"
        exit 1
    }

    # ---------- 7. 执行主脚本 ----------
    Set-Location $ProjectDir
    $nodeExe = Find-Node
    if (-not $nodeExe) {
        Write-Err "未找到 Node.js（需 ≥24）。请先安装 Node，或从 RewardsManager 完成环境初始化。"
        exit 1
    }

    # 让 patchright/playwright 使用项目内浏览器（与 RewardsManager 环境初始化保持一致）
    $env:PLAYWRIGHT_BROWSERS_PATH = '0'

    # 同时输出到终端与日志文件（不能赋值给变量，否则终端看不到输出）
    # --no-warnings 屏蔽 Node.js v24 的 SQLite 实验性警告等杂讯
    & $nodeExe --no-warnings (Join-Path $ProjectDir 'dist\index.js') 2>&1 | Tee-Object -FilePath $RunLog
    $exitCode = $LASTEXITCODE
    $outputText = Get-Content -Path $RunLog -Raw -Encoding UTF8

    # ---------- 8. 结果判定：退出码 + 获得积分检查 ----------
    $zeroPoints = $outputText -match '获得积分=0\D' -and $outputText -notmatch '获得积分=[1-9]'
    if ($exitCode -eq 0 -and -not $zeroPoints) {
        Set-Content -Path $LastRunFile -Value $today -Encoding UTF8
    } else {
        Write-Err "运行失败：退出码=$exitCode，获得积分为0=$zeroPoints（详见 $RunLog）"
        exit 1
    }
}
finally {
    Remove-Item $LockFile -Force -ErrorAction SilentlyContinue
}
