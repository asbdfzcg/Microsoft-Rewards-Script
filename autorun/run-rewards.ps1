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
# 计划任务/静默运行时最小化 Windows Terminal，避免占用桌面。
if (-not $Force) {
    Start-Sleep -Seconds 1
    try {
        Add-Type -Namespace Win32 -Name WindowApi -MemberDefinition @'
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool ShowWindowAsync(System.IntPtr hWnd, int nCmdShow);
'@
        $wtProc = Get-Process | Where-Object { $_.ProcessName -match 'WindowsTerminal|wt' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($wtProc) { [Win32.WindowApi]::ShowWindowAsync($wtProc.MainWindowHandle, 6) | Out-Null } # 6 = SW_MINIMIZE
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
New-Item -ItemType File -Path $LockFile -Force | Out-Null

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
    # 计划任务运行时重定向到日志文件，避免桌面弹窗，且不受 ConstrainedLanguage 限制。
    # 脚本路径（可能含空格）。
    $scriptPath = Join-Path $ProjectDir 'dist\index.js'
    # 手动模式用数组 splat：`& $nodeExe @nodeArgs` 时 PowerShell 才会把每个元素当成独立参数；
    # 若用拼接字符串，`&` 会把整串当成单个参数，导致 node 报 bad option。
    $nodeArgs = @('--no-warnings', $scriptPath)
    # 自动模式用 Start-Process -ArgumentList：数组元素含空格时不会被自动加引号，
    # 路径会在空格处被截断（Cannot find module '...Microsoft'）。因此单独构造一个
    # 带引号包裹路径的字符串参数——这是 8/17、8/18 成功运行时的写法。
    $nodeArgString = "--no-warnings `"$scriptPath`""
    $errLog = $RunLog + '.err'
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
            }
        } finally {
            $writer.Close()
        }
        $exitCode = $LASTEXITCODE
    } else {
        # 自动模式：用 Start-Process 把输出重定向到 run 日志文件。
        # 注意：不要直接用 [System.Diagnostics.Process] 的 OutputDataReceived 事件——
        # 计划任务以 Highest 权限运行时，系统可能将脚本置于 ConstrainedLanguage 模式，
        # 此时 New-Object 出来的 Process 对象无法访问 OutputDataReceived 等成员，会抛
        # “找不到属性 OutputDataReceived”导致整脚本中止。Start-Process 由 PowerShell 引擎
        # 内部执行，不受脚本的 ConstrainedLanguage 限制，重定向可靠。
        # 注意：RedirectStandardOutput 与 RedirectStandardError 不能指向同一文件，
        # 否则 PowerShell 会报“RedirectStandardOutput 和 RedirectStandardError 相同”。
        # 因此 stderr 单独写入 .err 文件，进程结束后合并进主日志，便于统一解析。
        # 异步启动（不带 -Wait）：进程在后台运行，父脚本继续往下走，
        # 这样“启动通知”能在运行初期实时弹出；随后用 WaitForExit 保活，
        # 避免父脚本提前退出误杀后台 node 进程。
        $proc = Start-Process -FilePath $nodeExe -ArgumentList $nodeArgString -WorkingDirectory $ProjectDir `
            -NoNewWindow -RedirectStandardOutput $RunLog -RedirectStandardError $errLog `
            -PassThru -ErrorAction Stop

        # 启动通知：node 把 stdout 块缓冲到重定向文件，运行初期日志尚未刷出，
        # 因此【不】依赖读取 run 日志来触发——直接发送“已启动”通知，确保用户必定收到。
        # （完成通知在 WaitForExit 之后读取，此时缓冲已刷出，可正常拿到收尾行。）
        if ($notifyMode -eq 'both') {
            $mode = if ($Force) { '手动' } else { '自动（计划任务）' }
            $startMsg = "已在后台启动（PID=$($proc.Id)，模式=$mode）。`n预计运行约 30-40 分钟，完成后将再次通知。"
            Send-Toast -Title 'Microsoft Rewards Script 已启动' -Message $startMsg
        }

        # 等待 node 进程结束（保活，确保后台进程不被父脚本退出误杀）
        $proc.WaitForExit()
        if (Test-Path $errLog) {
            try { Add-Content -Path $RunLog -Value (Get-Content -Path $errLog -Raw -Encoding UTF8) -Encoding UTF8 } catch {}
            Remove-Item $errLog -Force -ErrorAction SilentlyContinue
        }
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
    # 新版(v4.3.0+) 完成标记为 [RUN-END]，本轮回总 pointsGained；旧版回退 [运行结束]。
    $completed = $outputText -match '\[RUN-END\]' -or $outputText -match '\[运行结束\]'
    $zeroPoints = $outputText -match 'pointsGained=0(\D|$)' -and $outputText -notmatch 'pointsGained=[1-9]' `
        -and $outputText -notmatch '获得积分=[1-9]'
    if ($completed -and -not $zeroPoints) {
        Set-Content -Path $LastRunFile -Value $today -Encoding UTF8
    } else {
        $reason = if (-not $completed) { '未检测到“[RUN-END]”标记（可能中途崩溃）' } else { '获得积分为 0' }
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
