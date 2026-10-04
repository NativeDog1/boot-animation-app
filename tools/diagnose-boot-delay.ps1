# 诊断开机延迟：记录"登录完成 → 动画出现"之间到底发生了什么
#
# 为什么需要它：客户实测到 20–30 秒延迟，而这台机器（32GB 内存 / NVMe SSD / 16 核）
# 完全不应该出现这种数字。我之前所有测量都在**已登录的桌面**上做，
# 那个环境与"刚登录、系统还在加载"完全不同 —— 所以必须在这里量真实数据。
#
# 它做的事（每 100ms 采一次，持续 60 秒）：
#   1. 记录 explorer.exe 出现的时间（登录桌面就绪的标志）
#   2. 记录 BootAnimation.exe / NativePlayer.exe 出现与消失的时间
#   3. 记录这两个进程的 CPU 累计时间与内存（看它是"在忙"还是"在等"）
#   4. 记录 boot-animation.log 与 boot.log 的增长（程序自己打到哪一步了）
#
# 输出: %LOCALAPPDATA%\BootAnimation\boot-timeline.log
#
# 用法（登录后手动跑，或放在启动项里）:
#   powershell -ExecutionPolicy Bypass -File diagnose-boot-delay.ps1

param(
    [int]$Seconds = 60,
    [int]$IntervalMs = 100
)

$ErrorActionPreference = 'SilentlyContinue'
$dataDir = Join-Path $env:LOCALAPPDATA 'BootAnimation'
$out = Join-Path $dataDir 'boot-timeline.log'
$mainLog = Join-Path $dataDir 'boot-animation.log'
$clockLog = Join-Path $dataDir 'boot.log'

function W($line) {
    $stamp = (Get-Date).ToString('HH:mm:ss.fff')
    "$stamp  $line" | Add-Content -Path $out -Encoding UTF8
}

"================================================================" | Set-Content -Path $out -Encoding UTF8
W "诊断开始（每 $IntervalMs ms 采一次，共 $Seconds 秒）"
W ("进程启动时刻(脚本自身): " + (Get-Process -Id $PID).StartTime.ToString('HH:mm:ss.fff'))

$sw = [Diagnostics.Stopwatch]::StartNew()
$seen = @{}
$lastLogLen = 0
$lastClockLen = 0
$explorerSeen = $false

while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $t = [int]$sw.Elapsed.TotalMilliseconds

    # explorer 出现 = 桌面开始就绪
    if (-not $explorerSeen) {
        $ex = Get-Process explorer -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($ex) {
            $explorerSeen = $true
            W "explorer.exe 出现（桌面就绪）  脚本起算 +${t}ms"
        }
    }

    # 我们的两个进程
    foreach ($name in @('BootAnimation', 'NativePlayer')) {
        $procs = @(Get-Process $name -ErrorAction SilentlyContinue)
        $key = "$name:$($procs.Count)"
        if ($procs.Count -gt 0) {
            if (-not $seen.ContainsKey("first-$name")) {
                $seen["first-$name"] = $true
                $p0 = $procs[0]
                try { $p0.Refresh() } catch { }
                $started = try { $p0.StartTime.ToString('HH:mm:ss.fff') } catch { '?' }
                W "$name 首次出现  脚本起算 +${t}ms  进程启动于 $started  内存=$([math]::Round($p0.WorkingSet64/1MB))MB"
            }
            # 每 1 秒记一次 CPU/内存，看它在忙还是在等
            if ($t % 1000 -lt $IntervalMs) {
                $p0 = $procs[0]
                try { $p0.Refresh() } catch { }
                $cpu = try { [math]::Round($p0.TotalProcessorTime.TotalMilliseconds) } catch { -1 }
                W "  $name 状态: 内存=$([math]::Round($p0.WorkingSet64/1MB))MB  CPU累计=${cpu}ms  响应=$($p0.Responding)"
            }
        }
        elseif ($seen.ContainsKey("first-$name") -and -not $seen.ContainsKey("gone-$name")) {
            $seen["gone-$name"] = $true
            W "$name 已退出  脚本起算 +${t}ms"
        }
    }

    # 程序自己的日志增长 —— 这是最有价值的一条：它说明程序走到哪一步了
    if (Test-Path $mainLog) {
        $len = (Get-Item $mainLog).Length
        if ($len -gt $lastLogLen) {
            $all = Get-Content $mainLog -Encoding UTF8
            $newLines = $all | Select-Object -Last 4
            W "  主日志增长到 $len 字节，最后几行:"
            foreach ($l in $newLines) { W "      $l" }
            $lastLogLen = $len
        }
    }
    if (Test-Path $clockLog) {
        $len = (Get-Item $clockLog).Length
        if ($len -gt $lastClockLen) {
            $l = Get-Content $clockLog -Encoding UTF8 | Select-Object -Last 1
            W "  boot.log 增长: $l"
            $lastClockLen = $len
        }
    }

    Start-Sleep -Milliseconds $IntervalMs
}

W "诊断结束（共 $Seconds 秒）"
W "提示: 把 $out 的内容发给我。重点看两点 ——"
W "  1. BootAnimation 首次出现的时间（相对 explorer = 桌面就绪）"
W "  2. 从首次出现到主日志里出现 '窗口显形' 之间隔了多久，以及那段时间 CPU 累计有没有增长"

Write-Host "诊断完成，报告: $out"
Get-Content $out -Encoding UTF8
