# diagnose.ps1 - Microsoft Rewards Script 全方位诊断脚本
$ErrorActionPreference = 'Continue'
$AutorunDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Split-Path -Parent $AutorunDir
$TaskName   = 'MicrosoftRewardsScript'

function Section($title) { Write-Host ""; Write-Host "===== $title =====" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "  [正常] $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "  [警告] $msg" -ForegroundColor Yellow }
function Bad($msg)  { Write-Host "  [异常] $msg" -ForegroundColor Red }

Section "1. 计划任务状态"
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) {
    Ok "任务存在，状态: $($task.State)"
    $info = $task | Get-ScheduledTaskInfo
    $exitMsg = switch ($info.LastTaskResult) {
        0       { '0（成功）' }
        267011  { '267011 = 0x41303（任务尚未运行，正常）' }
        267012  { '267012 = 0x41304（任务未调度）' }
        267010  { '267010 = 0x41302（任务已禁用）' }
        267009  { '267009 = 0x41301（任务正在运行）' }
        default { "$($info.LastTaskResult)" }
    }
    Write-Host "  上次运行: $($info.LastRunTime)  退出码: $exitMsg"
    Write-Host "  下次运行: $($info.NextRunTime)"
    $action = $task.Actions[0]
    Write-Host "  执行程序: $($action.Execute)"
    if ($action.Arguments -notlike "*$AutorunDir*") {
        Warn "任务参数可能指向旧路径: $($action.Arguments)"
        Write-Host "         建议重新运行 setup-task.bat 重建任务"
    }
    if ($task.State -eq 'Disabled') { Bad "任务已被禁用！" }
} else {
    Bad "计划任务不存在，请运行 setup-task.bat 创建"
}

Section "2. 今日运行状态"
$lastRunFile = Join-Path $AutorunDir '.last-run-date'
$lockFile    = Join-Path $AutorunDir '.run-lock'
if (Test-Path $lastRunFile) { Ok "今天已运行标记: $(Get-Content $lastRunFile -Raw)" } else { Write-Host "  今天尚未成功运行" }
if (Test-Path $lockFile) { Warn "锁文件残留: $lockFile（如确认无实例运行可删除）" }

Section "3. 日志文件"
$logsDir = Join-Path $AutorunDir 'logs'
if (Test-Path $logsDir) {
    $logs = Get-ChildItem $logsDir -Filter '*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 5
    foreach ($l in $logs) { Write-Host "  $($l.Name)  ($([math]::Round($l.Length/1KB,1)) KB, $($l.LastWriteTime))" }
    $errLog = Join-Path $logsDir 'error.log'
    if ((Test-Path $errLog) -and (Get-Item $errLog).Length -gt 0) {
        Warn "error.log 最近 3 条:"
        Get-Content $errLog -Tail 3 | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    }
} else { Warn "日志目录不存在（尚未运行过）" }

Section "4. 网络连通性"
try { Resolve-DnsName -Name 'www.bing.com' -ErrorAction Stop | Out-Null; Ok "DNS 解析正常" } catch { Bad "DNS 解析失败" }
try {
    $r = Invoke-WebRequest -Uri 'https://www.bing.com' -Method Head -TimeoutSec 10 -UseBasicParsing
    Ok "HTTP 访问正常 (状态码 $($r.StatusCode))"
} catch { Bad "HTTP 访问失败: $($_.Exception.Message)" }

Section "5. Node.js 环境"
$nodeFound = $false

# 优先检查项目内便携 Node（沙盒/便携环境使用）
$localNode = Join-Path $ProjectDir 'tools\node\node.exe'
if (Test-Path $localNode) {
    try {
        $v = & $localNode --version
        if ($v -match 'v(\d+)' -and [int]$Matches[1] -ge 24) {
            Ok "项目内 Node.js: $v (>= 24 满足要求) 路径: $localNode"
            $nodeFound = $true
        } else {
            Bad "项目内 Node.js 版本过低: $v（需要 >= 24）路径: $localNode"
        }
    } catch {
        Bad "项目内 Node.js 无法执行: $localNode"
    }
}

# 再检查系统 Node
$sysNode = 'D:\Program Files\nodejs\node.exe'
if (Test-Path $sysNode) {
    try {
        $v = & $sysNode --version
        if ($v -match 'v(\d+)' -and [int]$Matches[1] -ge 24) { Ok "系统 Node.js: $v (>= 24 满足要求)" }
        else { Bad "系统 Node.js 版本过低: $v（需要 >= 24）" }
    } catch {
        Bad "系统 Node.js 无法执行: $sysNode"
    }
} elseif (-not $nodeFound) {
    Bad "未找到 Node.js（项目内或系统均未发现）"
}

Section "6. 项目文件"
foreach ($f in @('config.json', '.env', 'dist\index.js', 'package.json')) {
    $p = Join-Path $ProjectDir $f
    if (Test-Path $p) { Ok "$f 存在" } else { Bad "$f 缺失: $p" }
}

Section "7. Windows Terminal"
if (Get-Command wt.exe -ErrorAction SilentlyContinue) { Ok "wt.exe 可用" } else { Warn "未找到 wt.exe，将回退到普通 PowerShell 窗口" }

Section "8. 更新状态"
$updateFile = Join-Path $AutorunDir 'update-status.json'
if (Test-Path $updateFile) {
    $u = Get-Content $updateFile -Raw | ConvertFrom-Json
    Write-Host "  当前版本: $($u.currentVersion)  最新版本: $($u.latestVersion)  检查时间: $($u.checkedAt)"
    if ($u.updateAvailable) { Warn "有新版本可用，可打开 RewardsManager.exe 查看更新日志并更新" }
} else { Write-Host "  尚无更新检查记录（脚本运行后自动生成）" }

Write-Host ""
Write-Host "===== 诊断完成 =====" -ForegroundColor Cyan
