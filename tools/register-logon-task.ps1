# 用管理员权限注册"开机动画"的登录触发计划任务
#
# ─────────────────────────────────────────────────────────────────────────────
# 为什么需要这一步（有实测数据支撑，不是猜测）
#
# 2026-10-03 那次登录的三个时刻对齐后是这样：
#
#     21:44:53.763   Windows 事件日志: "已收到 Shell 启动通知"   ← 桌面开始加载
#            ↓  整整 27.7 秒，explorer 没有执行 Run 键
#     21:45:21.443   BootAnimation.exe 进程被创建（它自己记的"系统已开机 49.4 秒"）
#     21:45:22.035   画面出现（进程启动算起 463ms）
#
# 也就是说：**延迟 100% 在 explorer 处理 Run 键之前，程序本身只用了 463ms。**
# 而且那 27.7 秒里 BootAnimation 进程**还不存在** —— 不是它慢，是没人启动它。
#
# 计划任务由**任务计划服务**在登录时触发，不走 explorer 的启动项队列，
# 所以能跳过那 27.7 秒。这是不需要改程序就能拿到的最早触发点。
#
# ─────────────────────────────────────────────────────────────────────────────
# 怎么用
#
#   1. 右键本文件 → "使用 PowerShell 运行"
#      （如果没这个菜单：开始菜单搜 PowerShell → 右键"以管理员身份运行" →
#        把下面这一行贴进去回车：）
#        & ".\tools\register-logon-task.ps1"
#
#   2. 看到 "已注册" 就成功了。之后**注销再登录**即可验证效果。
#
#   3. 想撤销：加 -Remove 参数运行。
#        & "...\register-logon-task.ps1" -Remove
#
# ─────────────────────────────────────────────────────────────────────────────

param(
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$TaskName = 'BootAnimationLogon'
$Exe = Join-Path $env:LOCALAPPDATA 'Programs\BootAnimation\BootAnimation.exe'

# ── 权限检查
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host ""
    Write-Host "  ✗ 当前不是管理员权限。" -ForegroundColor Red
    Write-Host ""
    Write-Host "  请这样重新运行：" -ForegroundColor Yellow
    Write-Host "    开始菜单搜 'PowerShell' → 右键 → '以管理员身份运行'"
    Write-Host "    然后把下面这行贴进去回车："
    Write-Host ""
    Write-Host ("    & `"" + $PSCommandPath + "`"") -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  已复制到剪贴板，可以直接 Ctrl+V 粘贴。"
    try { Set-Clipboard -Value ('& "' + $PSCommandPath + '"') } catch { }
    exit 1
}

if ($Remove) {
    Write-Host "=== 移除计划任务 $TaskName ==="
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "  已移除" -ForegroundColor Green
    } else {
        Write-Host "  不存在，无需移除"
    }
    exit 0
}

if (-not (Test-Path $Exe)) {
    Write-Host "  ✗ 找不到程序: $Exe" -ForegroundColor Red
    Write-Host "    请先运行 install.ps1 安装。" -ForegroundColor Yellow
    exit 1
}

Write-Host "=== 注册登录触发计划任务 ==="
Write-Host "  程序: $Exe"
Write-Host "  任务: $TaskName"
Write-Host ""

# ── 动作：--play（和 Run 键同一条参数）
$action = New-ScheduledTaskAction -Execute $Exe -Argument '--play'

# ── 触发器：登录时立即触发（不加 Delay —— Delay 正是我们要绕开的东西）
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$trigger.Delay = 'PT0S'

# ── 设置：
#   · 允许按需启动、不在电池模式跳过（开机动画不该因为"省电"就不放）
#   · 执行时间限制 0 = 不限制（默认 3 天足够，但 0 更明确）
#   · 不在任务失败时重启（失败就安静跳过，符合"拿不到就不打扰"的原则）
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -RestartCount 0

# ── 主体：当前用户，交互式（必须能显示窗口），最高权限不需要
$principal = New-ScheduledTaskPrincipal -UserId ($env:USERDOMAIN + '\' + $env:USERNAME) `
    -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal `
    -Description '登录时立即播放开机动画。绕过 explorer 的 Run 键延迟（实测 27.7 秒）。' `
    -Force | Out-Null

Write-Host "  已注册 ✓" -ForegroundColor Green
Write-Host ""
Write-Host "=== 当前任务状态 ==="
$t = Get-ScheduledTask -TaskName $TaskName
Write-Host ("  任务名: " + $t.TaskName)
Write-Host ("  状态  : " + $t.State)
$t.Triggers | ForEach-Object { Write-Host ("  触发器: " + $_.CimClass.CimClassName) }
Write-Host ""
Write-Host "=== 下一步 ===" -ForegroundColor Yellow
Write-Host "  1. 注销再登录，观察动画是否变快"
Write-Host "  2. 如果计划任务生效，可以把 Run 键那条删掉（避免两条都跑）："
Write-Host '     Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name BootAnimation'
Write-Host "     （程序有单实例守卫，两条都留着也不会叠画面，只是稍浪费一次进程创建）"
Write-Host ""
Write-Host "  撤销：再次以管理员运行本文件并加 -Remove" -ForegroundColor DarkGray
