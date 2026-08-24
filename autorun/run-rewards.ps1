# run-rewards.ps1 - Microsoft Rewards Script 自动运行主脚本
# 由计划任务或 run-manual.bat 调用
# 用法: powershell -File run-rewards.ps1 [-Force]
#   -Force  跳过时间检查与"今日已运行"检查（手动运行时使用）

param(
    [switch]$Force
)

# 启动标记：每次被触发都先写一行到 startup.log，排查“触发了但没运行”的黑盒问题
# （之前的故障就是脚本在早期异常退出、却无任何日志，导致无法判断到底有没有启动）
try {
    $startupDir = Join-Path $PSScriptRoot 'logs'
    if (-not (Test-Path $startupDir)) { New-Item -ItemType Directory -Path $startupDir -Force | Out-Null }
    Add-Content -Path (Join-Path $startupDir 'startup.log') `
        -Value "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] 启动 (Force=$Force, PID=$pid)" -Encoding UTF8
} catch {}

# 设置窗口标题（避免在 Windows Terminal 命令行中传中文标题触发 Node.js 断言失败）
try { $Host.UI.RawUI.WindowTitle = 'Microsoft Rewards Script' } catch {}

# 防御性隐藏 Windows Terminal / ConPTY 遗留的 PseudoConsoleWindow（左下角灰色方块）
try {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace Win32 {
    public class PseudoConsoleHider {
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet=CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public const int SW_HIDE = 0;
        public static void HidePseudoConsole() {
            uint pid = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((hWnd, lParam) => {
                uint wpid; GetWindowThreadProcessId(hWnd, out wpid);
                if (wpid == pid) {
                    StringBuilder cn = new StringBuilder(256); GetClassName(hWnd, cn, 256);
                    if (cn.ToString() == "PseudoConsoleWindow") { ShowWindow(hWnd, SW_HIDE); }
                }
                return true;
            }, IntPtr.Zero);
        }
    }
}
'@
    [Win32.PseudoConsoleHider]::HidePseudoConsole()
} catch {}

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

# ---------- Windows 通知（toast，依赖 WinRT，无需第三方模块） ----------
function Send-Toast([string]$title, [string]$message) {
    try {
        # 注册一个 AUMID，确保通知能正常弹出（首次运行时写入注册表）
        $appId = 'RewardsManager.Automation'
        $regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\$appId"
        if (-not (Test-Path $regPath)) { New-Item -Path $regPath -Force | Out-Null }
        New-ItemProperty -Path $regPath -Name 'ShowInActionCenter' -Value 1 -PropertyType DWord -Force | Out-Null

        # 加载 WinRT 通知类型
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null

        $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $texts = $template.GetElementsByTagName('text')
        $texts.Item(0).AppendChild($template.CreateTextNode($title)) | Out-Null
        $texts.Item(1).AppendChild($template.CreateTextNode($message)) | Out-Null
        $toast = [Windows.UI.Notifications.ToastNotification]::new($template)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
    } catch {
        try { Add-Content -Path (Join-Path $LogsDir 'notify-error.log') -Value "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] Toast failed: $_" -Encoding UTF8 } catch {}
    }
}

# ---------- 1. 窗口可见性 ----------
# 手动运行时（-Force）保持窗口在前台，方便查看实时输出；
# 计划任务/静默运行时按 automation-settings.json 的 windowMode 处理 Windows Terminal 窗口：
#   silent    = 完全隐藏
#   minimized = 最小化到任务栏（默认，用户可点开看实时输出）
#   normal    = 正常显示
if (-not $Force) {
    # wt.exe 冷启动比 powershell 慢，多等一会确保窗口句柄可用
    Start-Sleep -Seconds 3
    try {
        $wmSettingsFile = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'automation-settings.json'
        $windowMode = 'minimized'
        if (Test-Path $wmSettingsFile) {
            try {
                $wm = Get-Content $wmSettingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($wm.windowMode) { $windowMode = [string]$wm.windowMode }
            } catch {}
        }
        Add-Type -Namespace Win32 -Name WindowApi -MemberDefinition @'
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool ShowWindowAsync(System.IntPtr hWnd, int nCmdShow);
'@
        $wtProc = Get-Process | Where-Object { $_.ProcessName -match 'WindowsTerminal|wt' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($wtProc) {
            # 0 = SW_HIDE, 6 = SW_MINIMIZE, 9 = SW_RESTORE
            $cmd = switch ($windowMode) { 'silent' { 0 } 'normal' { 9 } default { 6 } }
            [Win32.WindowApi]::ShowWindowAsync($wtProc.MainWindowHandle, $cmd) | Out-Null
        }
    } catch {}
}

# ---------- 2. 文件锁防双实例 ----------
if (Test-Path $LockFile) {
    $lockAge = (Get-Date) - (Get-Item $LockFile).LastWriteTime
    if ($lockAge.TotalHours -lt 3) {
        Write-Skip "另一个实例正在运行（锁文件存在，创建于 $($lockAge.TotalMinutes.ToString('0')) 分钟前），退出"
        exit 0
    }
    Remove-Item $LockFile -Force -ErrorAction SilentlyContinue # 残留锁（>3小时）自动清理
}
# 锁文件同时记录本 ps1 的 PID，供 RewardsManager 的“停止”按钮精确定位并结束整个进程树
New-Item -ItemType File -Path $LockFile -Force | Out-Null
Set-Content -Path $LockFile -Value "$pid" -Encoding UTF8

try {
    # ---------- 3. 刷新 PATH，移除可能干扰的条目，让系统 Node 可被 PATH 找到 ----------
    $machinePath = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath    = [System.Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = ($machinePath + ';' + $userPath -split ';' |
        Where-Object { $_ -and $_ -notmatch 'TRAE|[\\/]binaries$' } |
        Select-Object -Unique) -join ';'

    # ---------- 4. 时间检查 ----------
    if (-not $Force -and (Get-Date).Hour -lt $MinHour) {
        Write-Skip "当前时间早于 ${MinHour}:00，跳过本次运行"
        exit 0
    }

    # ---------- 5. 当天防重复 ----------
    $today = Get-Date -Format 'yyyy-MM-dd'
    if (-not $Force -and (Test-Path $LastRunFile)) {
        $raw = Get-Content $LastRunFile -Raw -ErrorAction SilentlyContinue
        $lastRun = if ($raw) { $raw.Trim() } else { '' }
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

    # 读取自动化设置：通知模式（both=启动+完成 / complete=仅完成 / none=不通知）
    $notifyMode = 'none'
    $settingsFile = Join-Path $AutorunDir 'automation-settings.json'
    if (Test-Path $settingsFile) {
        try {
            $s = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($s.notifyMode) { $notifyMode = [string]$s.notifyMode }
        } catch {}
    }

    # 运行 node：区分手动/自动场景。
    # 手动运行（-Force）时直接在前台运行，输出实时显示在终端，同时写入日志文件；
    # 计划任务运行时用前台管道逐行写入日志文件（实时增长），不弹窗。
    # 脚本路径（可能含空格）。
    $scriptPath = Join-Path $ProjectDir 'dist\index.js'
    # 用数组 splat：`& $nodeExe @nodeArgs` 时 PowerShell 才会把每个元素当成独立参数；
    # 若用拼接字符串，`&` 会把整串当成单个参数，导致 node 报 bad option。
    $nodeArgs = @('--no-warnings', $scriptPath)
    if ($Force) {
        # 手动模式：前台运行 + Tee 到日志。
        Write-Host "正在启动 Microsoft Rewards Script（PID=$pid）..." -ForegroundColor Cyan
        Write-Host "日志同时写入: $RunLog" -ForegroundColor DarkGray
        Write-Host ""
        if ($notifyMode -eq 'both') {
            Send-Toast -Title 'Microsoft Rewards Script 已启动' -Message "手动运行已开始，PID=$pid"
        }
        $writer = [System.IO.StreamWriter]::new($RunLog, $false, [System.Text.Encoding]::UTF8)
        try {
            & $nodeExe @nodeArgs 2>&1 | ForEach-Object {
                $line = "$_"
                $writer.WriteLine($line)
                $writer.Flush()
                Write-Host $line
                # 强制刷出 .NET 控制台缓冲，避免 Windows Terminal 渲染滞后（终端实时）
                try { [Console]::Out.Flush() } catch {}
            }
        } finally {
            $writer.Close()
        }
        $exitCode = $LASTEXITCODE
    } else {
        # 自动模式：前台管道 + 逐行 Tee 到日志（文件实时增长，不再等进程退出才写入）。
        # 注意：不能用 Start-Process -RedirectStandardOutput——实测其重定向文件在进程运行期间
        # 始终为空，退出后才一次性写入，导致日志/状态栏无法实时反映进度。
        # 这里用纯 PowerShell 管道（& $nodeExe 2>&1 | ForEach-Object）+ Add-Content：
        #   - 管道/ForEach-Object/Add-Content 均为语言与核心 cmdlet，不受计划任务
        #     ConstrainedLanguage 模式限制（不像 New-Object Process 的成员访问会抛“找不到属性”）；
        #   - 2>&1 天然合并 stdout/stderr，无需 Start-Process 那样单独 .err 再合并；
        #   - Add-Content 每行立即落盘，日志实时可读。
        # 启动通知在管道开始前发送（前台管道开始后即阻塞，无“启动后间隙”）。
        if ($notifyMode -eq 'both') {
            $mode = if ($Force) { '手动' } else { '自动（计划任务）' }
            $startMsg = "自动运行已启动（模式=$mode）。`n预计运行约 30-40 分钟，完成后将再次通知。"
            Send-Toast -Title 'Microsoft Rewards Script 已启动' -Message $startMsg
        }
        & $nodeExe @nodeArgs 2>&1 | ForEach-Object {
            $line = "$_"
            # 日志文件实时写入（C# 状态栏/通知解析用）
            Add-Content -Path $RunLog -Value $line -Encoding UTF8
            # 同时输出到 wt.exe 终端窗口（用户点开可实时查看；silent 模式窗口隐藏则无可见效果）
            Write-Host $line
            try { [Console]::Out.Flush() } catch {}
        }
        $exitCode = $LASTEXITCODE
    }

    # 完成通知（重新解析日志，确保收尾行拿全）
    if ($notifyMode -eq 'both' -or $notifyMode -eq 'complete') {
        $completeLines = @()
        foreach ($l in (Get-Content -Path $RunLog -Encoding UTF8)) {
            # 新版(v4.3.0+) 按账号归集：ACCOUNT-END 含每账号积分；RUN-END 为本轮汇总
            if ($l -match '\[ACCOUNT-END\]' -or $l -match '\[RUN-END\]') { $completeLines += $l }
        }
        if ($completeLines.Count -gt 0) {
            Send-Toast -Title 'Microsoft Rewards Script 运行完成' -Message ($completeLines -join "`n")
        }
    }

    $outputText = Get-Content -Path $RunLog -Raw -Encoding UTF8

    # ---------- 8. 结果判定：依据日志内容而非进程退出码 ----------
    # 注意：本环境下 Start-Process -PassThru 返回的 Process 对象 .ExitCode 恒为 $null，
    # 无法用于判断成功与否；改为依据 node 自身输出的完成标记与积分来判定。
    # 新版(v4.3.0+) 完成标记为 [RUN-END]，经 i18n 翻译为 [运行结束]；本轮回总 pointsGained/获得积分。
    $completed = $outputText -match '\[RUN-END\]' -or $outputText -match '\[运行结束\]'
    # 无论日志是英文(pointsGained)还是中文(获得积分)，只要有任一非 0 积分即视为成功
    $hasPositive = $outputText -match 'pointsGained=[1-9]' -or $outputText -match '获得积分=[1-9]'
    $zeroPoints = -not $hasPositive
    if ($completed -and -not $zeroPoints) {
        Set-Content -Path $LastRunFile -Value $today -Encoding UTF8
    } else {
        $reason = if (-not $completed) { '未检测到“[运行结束]”标记（可能中途崩溃）' } else { '获得积分为 0' }
        Write-Err "运行失败：$reason（详见 $RunLog）"
        exit 1
    }
}
catch {
    # 主体 try 内任何未捕获异常都会落到这里，写入 error.log 便于排查“黑盒退出”问题
    try { Write-Err ("未捕获异常导致脚本中止: " + $_.Exception.Message + "`n" + $_.ScriptStackTrace) } catch {}
    exit 1
}
finally {
    Remove-Item $LockFile -Force -ErrorAction SilentlyContinue
}
