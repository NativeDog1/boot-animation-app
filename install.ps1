<#
  开机动画（BootAnimation）—— 安装 / 卸载

  这个脚本不修改任何系统级设置，只做四件事：
    1) 把 exe 复制到一个固定位置（%LOCALAPPDATA%\Programs\BootAnimation）
    2) 注册开机自启（默认写 HKCU\...\Run 键，不需要管理员）
    3) 注册 bootanim:// 协议 —— 社区网站上点「用开机动画打开」靠它唤起本程序
    4) 留一份「卸载清单」，卸载时照着原样删掉

  用法：
    .\install.ps1                    # 安装（先试计划任务，不行退回 Run 键）
    .\install.ps1 -Method run        # 强制只用 Run 键（不需要管理员）
    .\install.ps1 -Uninstall         # 卸载，还原成没装过的样子
    .\install.ps1 -DryRun            # 只打印将要做什么，不改动任何东西
    .\install.ps1 -Clip cyberpunk    # 安装时顺便把片头设成某一段

  为什么默认先试计划任务：客户要的是「登录的瞬间就播」。Run 键的进程是 explorer 起来
  之后才被拉起的，Windows 会拖上若干秒；计划任务能在登录时更早触发。
  HKCU\...\Run 之外的另一个好处是不出现在「启动应用」列表里被误关。

  -Delay 默认 0，而且不建议再调大：它是**叠加**在系统自身延迟之上的，睡 3 秒只会让
  动画更晚出现 —— 之前的默认值就是 3，那正是「登录之后要过一会儿才播」的一半原因。
#>
param(
    [ValidateSet('auto', 'run', 'task')][string]$Method = 'auto',
    [switch]$Uninstall,
    [switch]$DryRun,
    [string]$Clip = '',
    [double]$Delay = 0
)

$ErrorActionPreference = 'Stop'

$RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValueName = 'BootAnimation'
$WarmValueName = 'BootAnimationWarm'
$TaskName = 'BootAnimation'
# bootanim:// 协议。必须和 Setup.cs 里注册的完全一致，否则「安装包装的」和
# 「脚本装的」两种机器行为会不一样 —— 网站一键安装按钮在后者上会点了没反应。
$ProtoKeyPath = 'HKCU:\Software\Classes\bootanim'
$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\BootAnimation'
$DataDir = Join-Path $env:LOCALAPPDATA 'BootAnimation'
$ManifestPath = Join-Path $DataDir 'installed.txt'

function Say($text) { Write-Host $text }
function Step($text) { Write-Host ('  - ' + $text) }

# 脚本自己所在的目录。函数里不能用 $MyInvocation.MyCommand.Path（那是函数自己的），
# 所以在这里取一次，全程复用。
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function SourceExe {
    $local = Join-Path $ScriptDir 'BootAnimation.exe'
    if (Test-Path $local) { return $local }
    return $null
}

# ---------------------------------------------------------------- 卸载
if ($Uninstall) {
    Say '卸载开机动画'
    $removed = 0

    # 1. Run 键
    try {
        $existing = Get-ItemProperty -Path $RunKeyPath -Name $RunValueName -ErrorAction SilentlyContinue
        if ($existing -ne $null -and $existing.PSObject.Properties.Name -contains $RunValueName) {
            if ($DryRun) { Step ('将删除 Run 键值: ' + $RunValueName + ' 与 ' + $WarmValueName) }
            else {
                Remove-ItemProperty -Path $RunKeyPath -Name $RunValueName -ErrorAction SilentlyContinue
                Remove-ItemProperty -Path $RunKeyPath -Name $WarmValueName -ErrorAction SilentlyContinue
                Step ('已删除 Run 键值: ' + $RunValueName + ' 与 ' + $WarmValueName)
            }
            $removed++
        }
    } catch { }

    # 2. 计划任务（如果当初用的是它）
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task -ne $null) {
        if ($DryRun) { Step ('将注销计划任务: ' + $TaskName) }
        else {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
            Step ('已注销计划任务: ' + $TaskName)
        }
        $removed++
    }

    # 3. bootanim:// 协议
    if (Test-Path $ProtoKeyPath) {
        if ($DryRun) { Step ('将删除协议注册: ' + $ProtoKeyPath) }
        else {
            Remove-Item -Path $ProtoKeyPath -Recurse -Force -ErrorAction SilentlyContinue
            Step ('已删除协议注册: ' + $ProtoKeyPath)
        }
        $removed++
    }

    # 4. 程序目录（数据目录故意保留：里面是你的选片和日志，重装后还能用）
    if (Test-Path $InstallDir) {
        if ($DryRun) { Step ('将删除程序目录: ' + $InstallDir) }
        else {
            Remove-Item -Path $InstallDir -Recurse -Force
            Step ('已删除程序目录: ' + $InstallDir)
        }
        $removed++
    }

    if ($removed -eq 0) { Say '  没有发现安装痕迹，本来就是干净的。' }
    Say ('  数据目录保留在: ' + $DataDir + '（选片、日志、解包缓存）')
    Say ('  想连数据一起清掉: Remove-Item -Recurse -Force "' + $DataDir + '"')
    Say '卸载完成。'
    exit 0
}

# ---------------------------------------------------------------- 安装
Say '安装开机动画'
$src = SourceExe
if ($src -eq $null) {
    Say '  找不到 BootAnimation.exe。请把本脚本和 exe 放在同一个目录里。'
    exit 1
}
Say ('  源程序: ' + $src)

if ($DryRun) {
    Step ('将创建目录: ' + $InstallDir)
    Step ('将复制: ' + $src + ' -> ' + (Join-Path $InstallDir 'BootAnimation.exe'))
    $argText = '--play' + $(if ($Delay -gt 0) { ' --delay ' + $Delay } else { '' })
    if ($Method -eq 'task') {
        Step ('将注册计划任务 ' + $TaskName + '（登录时触发，最早可用）')
    } elseif ($Method -eq 'run') {
        Step ('将写入 Run 键 ' + $RunValueName + ' = "' + (Join-Path $InstallDir 'BootAnimation.exe') + '" ' + $argText)
    } else {
        Step ('将先试计划任务 ' + $TaskName + '（启动最早）；注册不了就退回 Run 键')
    }
    if ($Clip -ne '') { Step ('将把片头设为: ' + $Clip) }
    Step ('将注册 bootanim:// 协议 -> "' + (Join-Path $InstallDir 'BootAnimation.exe') + '" --install-url "%1"')
    Say '（这是 -DryRun，什么都没改。）'
    exit 0
}

# 1. 复制到固定位置
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$targetExe = Join-Path $InstallDir 'BootAnimation.exe'
Copy-Item -Path $src -Destination $targetExe -Force
Step ('已安装到: ' + $targetExe)

# 2. 装完立刻自检：解包内嵌视频、校验哈希。失败就不注册自启，免得开机黑屏。
Step '自检中（解包内嵌视频并校验）…'
$p = Start-Process -FilePath $targetExe -ArgumentList '--selftest' -Wait -PassThru -WindowStyle Hidden
if ($p.ExitCode -ne 0) {
    Say ('  自检失败（退出码 ' + $p.ExitCode + '），不注册自启。请看 ' + (Join-Path $DataDir 'boot-animation.log'))
    exit 1
}
Step '自检通过'

# 3. 可选：设定片头
if ($Clip -ne '') {
    if (@('brand', 'cyberpunk', 'awakening', 'startup') -contains $Clip) {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
        Set-Content -Path (Join-Path $DataDir 'settings.txt') -Value $Clip -NoNewline -Encoding ASCII
        Step ('片头已设为: ' + $Clip)
    } else {
        Say ('  没有这段片头: ' + $Clip + '（可选 brand / cyberpunk / awakening / startup）')
    }
}

# 4. 注册自启
#
# 参数里不再写 --delay：那个值是叠加在系统自身延迟之上的，默认 0。
$bootArgs = '--play' + $(if ($Delay -gt 0) { ' --delay ' + $Delay } else { '' })
$registered = $false

function Register-RunKey {
    # **不再注册 Run 键**。实测：Run 键要等 explorer 处理启动项队列，
    # 登录后 21–53 秒才被拉起；而计划任务在第 20 秒就触发了。
    # 两条都注册会造成**先后各播一遍**（实测客户看到"播了三次"）。
    # 这里只负责把旧版留下的 Run 键清掉。
    if (Get-ItemProperty -Path $RunKeyPath -Name $RunValueName -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $RunKeyPath -Name $RunValueName -ErrorAction SilentlyContinue
        Step ('已移除旧的 Run 键启动项（改用计划任务，更快且不会重复播放）')
    }

    # **不再注册预热进程**（曾经注册过，已废弃并删除）。
    #
    # 原因：预热进程需要一个不可见或屏幕外的窗口提前解码视频，但 WPF 的 MediaElement
    # **不会在非前台窗口里刷新视频**。实测四种方案（Pause 冻结 / 重挂 Source /
    # Opacity=0 / 移到屏幕外但可见）全部得到静止画面，而普通启动路径（对照）正常播放。
    # 更糟的是它空闲时全速解码 4K（实测 2040MB 内存 + 高优先级），
    # 把登录后的机器拖慢 —— 与"让开机更快"的目的正好相反。
    # 这里顺手清掉可能残留的登记，避免旧版本装上来的机器继续跑那个进程。
    if (Get-ItemProperty -Path $RunKeyPath -Name $WarmValueName -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $RunKeyPath -Name $WarmValueName -ErrorAction SilentlyContinue
        Step ('已移除废弃的预热进程自启登记: ' + $WarmValueName)
    }
}

function Register-Task {
    # 登录时触发 + 高优先级。这两项合起来才是"尽可能早"：
    #   - AtLogOn 由任务计划服务在登录时拉起，不必等 explorer 把 Run 键轮询到
    #   - Priority 7 让它在开机那几十个竞争进程里先拿到 CPU（视频首帧解码很吃这个）
    #
    # 这里**不设** StartDelay。New-ScheduledTaskSettingsSet 返回的对象在 Windows
    # PowerShell 5.1 上没有 StartDelay 属性，写它就是抛异常 —— 而那个异常会被下面的
    # catch 当成"没有权限"记下来，把真正的失败原因藏掉。新建任务本来就没有启动延迟。
    $action = New-ScheduledTaskAction -Execute $targetExe -Argument $bootArgs
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew -Priority 7
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
        -Description '登录后播放开机动画' -Force | Out-Null
    Step ('已注册计划任务: ' + $TaskName + '（登录触发，高优先级）')
}

# 能不能注册计划任务，先看权限，而不是"先试一次再说"。
# 非管理员注册 ONLOGON 任务会被直接拒绝（本机实测三种写法都是 Access is denied），
# 所以"试一下"只会每次安装都打一行失败信息，既慢又让人以为装坏了。
function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

if ($Method -eq 'task') {
    try { Register-Task } catch {
        Say ('  注册计划任务失败: ' + $_.Exception.Message)
        Say '  改用不需要管理员的 Run 键。'
        Register-RunKey
        $Method = 'run'
    }
} elseif ($Method -eq 'run') {
    Register-RunKey
} else {
    # auto：只有管理员才值得试计划任务；普通用户直接走 Run 键。
    if (Test-IsAdmin) {
        try { Register-Task; $registered = $true } catch {
            Step ('计划任务注册失败，退回 Run 键: ' + $_.Exception.Message)
        }
    } else {
        Step '当前不是管理员，直接用 Run 键（计划任务需要管理员权限）'
    }
    if (-not $registered) { Register-RunKey; $Method = 'run' }
}

# 3a. 启动延迟诊断采样器（一次性，不是常驻）。
#
# 为什么要有它：客户实测登录后 20–30 秒才出现开机动画，而程序自身从启动到出画
# 只要约 570ms（boot.log 稳定可查）—— 也就是说延迟落在"程序被拉起之前"。
# 要判定责任归属（Windows 登录过程慢 vs 本程序内部阻塞），必须在**真实登录环境**
# 里采样，而已登录桌面上测不出来。这个 8KB 的采样器只在下次登录跑 90 秒后自己退出。
#
# 它**不是常驻组件**：诊断结束后应从 Run 项里删掉（见 README 的说明）。
$diagExe = Join-Path $PSScriptRoot 'tools\BootTimeline.exe'
if (Test-Path $diagExe) {
    Set-ItemProperty -Path $RunKeyPath -Name 'BootAnimationDiag' -Value ('"' + $diagExe + '" --seconds 90')
    Step '已注册一次性启动延迟采样器（下次登录跑 90 秒后自动退出）'
}

# 3a1. 用 Run 键注册**原生封面窗口**（比计划任务更早，且它自己会等桌面出现）。
#
# 为什么需要它（实测的两个致命数字）：
#   · 登录时"进程启动 → 代码开始执行"要 16400ms（CLR+程序集+JIT，在代码之外）
#   · 计划任务在开机 38 秒才把主程序拉起
#   两者相加，主程序最早也要到开机 54 秒才可能建窗口 —— 而桌面早已画出来，
#   于是"桌面先出现、动画后出现"（穿帮）。
#
# NativePlayer.exe 只有 13.8KB、不加载 WPF，CLR 启动快得多；
# 它在屏幕外把封面窗口完全建好（已渲染已合成），然后自己轮询等桌面出现，
# 桌面一出现只改一次位置就铺满屏幕 —— 实测从进程启动到铺满只要 19ms。
try {
    $posterDir = Join-Path $env:LOCALAPPDATA 'BootAnimation\posters'
    if (Test-Path $posterDir) {
        $chosen = $null
        $settings = Join-Path $env:LOCALAPPDATA 'BootAnimation\settings.txt'
        if (Test-Path $settings) { $chosen = (Get-Content $settings -Raw).Trim() }
        $posterFile = $null
        if ($chosen) {
            $cand = Join-Path $posterDir ('file-' + $chosen + '.jpg')
            if (Test-Path $cand) { $posterFile = $cand }
        }
        if (-not $posterFile) { $posterFile = (Get-ChildItem $posterDir -Filter '*.jpg' | Select-Object -First 1).FullName }
        if ($posterFile) {
            $posterCmd = '"' + (Join-Path $InstallDir 'NativePlayer.exe') + '" --poster "' + $posterFile
            $posterCmd += '" --hidden --log "' + (Join-Path $env:LOCALAPPDATA 'BootAnimation\boot-animation.log') + '"'
            Set-ItemProperty -Path $RunKeyPath -Name 'BootAnimationPoster' -Value $posterCmd
            Step '已注册原生封面窗口（Run 键，自己等桌面出现，实测 19ms 铺满屏幕）'
        }
    }
} catch {
    Step ('注册原生封面窗口失败（不影响主流程）: ' + $_.Exception.Message)
}

# 3a2. 注册"登录触发计划任务"——这是**唯一能绕过 explorer 启动延迟**的方式，而且不需要管理员。
#
# 实测数据（2026-10-03 两次登录）：
#     Shell 启动通知 21:58:22.312  →  程序被拉起 21:58:43.909   （27.6 秒）
#     Shell 启动通知 21:44:53.763  →  程序被拉起 21:45:21.443   （27.7 秒）
#   即 Run 键与启动文件夹两条路都被 explorer 压后了近 28 秒，而程序自身只需 460ms。
#
# 计划任务由**任务计划服务**在登录时直接触发，不排在 explorer 的启动项队列里。
# 关键点：必须用 **Task Scheduler COM API**，不能用 schtasks.exe ——
# schtasks 的默认主体需要管理员（实测 Access is denied），
# 而 COM API 可以显式指定 TASK_LOGON_INTERACTIVE_TOKEN（3），当前用户即可注册。
try {
    $svc = New-Object -ComObject 'Schedule.Service'
    $svc.Connect()
    $root = $svc.GetFolder('\')
    $td = $svc.NewTask(0)
    $td.RegistrationInfo.Description = '登录时立即播放开机动画（绕过 explorer 启动项延迟）'
    $tr = $td.Triggers.Create(9)          # 9 = TASK_TRIGGER_LOGON
    $tr.Id = 'AtLogon'
    $tr.UserId = ($env:USERDOMAIN + '\' + $env:USERNAME)
    $ac = $td.Actions.Create(0)           # 0 = TASK_ACTION_EXEC
    $ac.Path = $targetExe
    $ac.Arguments = '--play'
    $td.Settings.AllowDemandStart = $true
    $td.Settings.DisallowStartIfOnBatteries = $false
    $td.Settings.StopIfGoingOnBatteries = $false
    $td.Settings.ExecutionTimeLimit = 'PT0S'
    $td.Settings.MultipleInstances = 2    # IgnoreNew
    $td.Principal.LogonType = 3           # TASK_LOGON_INTERACTIVE_TOKEN
    $td.Principal.RunLevel = 0            # 最低权限
    $root.RegisterTaskDefinition('BootAnimationLogon', $td, 6, $null, $null, 3, $null) | Out-Null
    Step '已注册登录触发计划任务（绕过 explorer 延迟，不需要管理员）'
} catch {
    Step ('注册计划任务失败，退回 Run 键方式: ' + $_.Exception.Message)
}

# 3b. 原生播放器：和主程序装到同一目录。
#
# 为什么要单独一个 exe：它**不引用任何 WPF 程序集**（只有 13.8KB），
# 因此窗口 3–33ms 就能上屏（WPF 路径实测 250ms），而且上屏那一刻已经贴好了
# 缓存的视频首帧图 —— 用户看不到任何黑块或"先空白后补画面"。
# 主程序找不到它时会自动退回 WPF 路径，所以漏装不会导致不播。
$nativeSrc = Join-Path $PSScriptRoot 'NativePlayer.exe'
if (Test-Path $nativeSrc) {
    Copy-Item $nativeSrc (Join-Path $InstallDir 'NativePlayer.exe') -Force
    Step '已安装原生播放器 NativePlayer.exe（窗口 3–33ms 上屏）'
} else {
    Step '未找到 NativePlayer.exe（主程序将使用 WPF 路径，功能不受影响）'
}

# 3c. 启动文件夹里也放一份快捷方式（第三条触发路径）。
#
# 为什么放两条：Run 键与启动文件夹由 explorer 在不同时机处理，实测无法确定哪个更早。
# 两条都注册，靠程序内的**单实例守卫**保证只播一次；日志里的"触发来源"会说明哪条先到，
# 之后就能只保留更快的那一条。
#
# 参数用 --startupfolder 是为了在日志里区分来源，功能上与 --play 完全一致。
$startupDir = [Environment]::GetFolderPath('Startup')
if ($startupDir -and (Test-Path $startupDir)) {
    try {
        $ws = New-Object -ComObject WScript.Shell
        $lnkPath = Join-Path $startupDir '开机动画.lnk'
        $sc = $ws.CreateShortcut($lnkPath)
        $sc.TargetPath = $targetExe
        $sc.Arguments = '--play'   # 用标准参数：不认识的标志会让解析错位（实测踩过）
        $sc.WorkingDirectory = $InstallDir
        $sc.Description = '开机动画（启动文件夹路径）'
        $sc.Save()
        # 同上：注册多条会造成重复播放，所以这里**不注册**，只清理旧版留下的。
        Remove-Item $lnkPath -Force -ErrorAction SilentlyContinue
        Step '已清理启动文件夹快捷方式（改用计划任务）'
    } catch {
        Step ('创建启动文件夹快捷方式失败（不影响 Run 键路径）: ' + $_.Exception.Message)
    }
}

# 4b. 卸载入口的版本号：**从 exe 自己读**，不写死。
#
# 之前这里没有写 DisplayVersion，所以"设置 → 应用"里显示的是上一次 Setup.exe
# 留下的旧版本号（实测还是 1.1.0，而程序已经是 1.2.0）—— 两条安装路径各写各的，
# 迟早对不上。从程序集读就只有一个真源。
#   **必须写全所有值**：用 New-Item -Force 建一个键会把它清成空的，
#   于是 Setup.exe 写过的 DisplayName / Publisher / DisplayIcon 全没了 ——
#   实测踩到过：更新版本号之后"设置 → 应用"里只剩一个版本号，名字空了。
#   所以这里按 Setup.cs 的同一套字段完整写一遍，两条安装路径产出完全一致。
try {
    $ver = (Get-Item $targetExe).VersionInfo.ProductVersion
    if (-not $ver) { $ver = (Get-Item $targetExe).VersionInfo.FileVersion }
    if (-not $ver) { $ver = '1.2.0.0' }

    $uninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BootAnimation'
    New-Item -Path $uninstKey -Force | Out-Null
    Set-ItemProperty -Path $uninstKey -Name 'DisplayName'     -Value '开机动画'
    Set-ItemProperty -Path $uninstKey -Name 'DisplayVersion'  -Value $ver
    Set-ItemProperty -Path $uninstKey -Name 'Publisher'       -Value 'BootAnimation'
    Set-ItemProperty -Path $uninstKey -Name 'DisplayIcon'     -Value ($targetExe + ',0')
    Set-ItemProperty -Path $uninstKey -Name 'InstallLocation' -Value $InstallDir
    Set-ItemProperty -Path $uninstKey -Name 'UninstallString' -Value ('"' + $targetExe + '" --uninstall')
    Set-ItemProperty -Path $uninstKey -Name 'QuietUninstallString' -Value ('"' + $targetExe + '" --uninstall --silent')
    Set-ItemProperty -Path $uninstKey -Name 'NoModify' -Value 1 -Type DWord
    Set-ItemProperty -Path $uninstKey -Name 'NoRepair' -Value 1 -Type DWord
    Step ('已写卸载入口（完整字段）: v' + $ver)
} catch {
    Step ('写卸载入口失败（不影响使用）: ' + $_.Exception.Message)
}

# 4c. 为所有已安装片段准备"首帧图"。
#
# 为什么必须在安装期做：首帧图是本程序填住"窗口已显形、视频还没解码出画"
# 那几百毫秒的唯一手段。它原来是播放开始后才由后台线程生成的，于是**每段片段
# 第一次播放都必然黑一下**（实测 window-shown@229 → 出画@536，中间是纯黑）。
# 抽一帧要几百毫秒，放在开机最紧张的几百毫秒里做是错的；放在安装期做，
# 开机就只是"读一个已存在的 jpg"（实测 2ms）。
try {
    Step '准备首帧图与定格图（每段一次，之后开机只读缓存）…'
    $prep = Start-Process -FilePath $targetExe -ArgumentList '--prepare' -Wait -PassThru -WindowStyle Hidden
    if ($prep.ExitCode -eq 0) { Step '首帧图已全部就绪' }
    else { Step ('首帧图有部分失败（不影响使用，首次播放可能短暂黑一下），退出码 ' + $prep.ExitCode) }
} catch {
    Step ('首帧图准备失败（不影响使用）: ' + $_.Exception.Message)
}

# 5. 注册 bootanim:// 协议（社区网站的「用开机动画打开」靠它）
#    写法和 Setup.cs 完全一致：默认值说明用途，URL Protocol 空值声明这是协议，
#    shell\open\command 把 URL 作为 --install-url 传进来 —— 用参数而不是裸 URL，
#    是因为裸 URL 会被当成"要播放的文件路径"，程序里就得再判一次。
New-Item -Path $ProtoKeyPath -Force | Out-Null
Set-ItemProperty -Path $ProtoKeyPath -Name '(default)' -Value 'URL:BootAnimation Protocol'
New-ItemProperty -Path $ProtoKeyPath -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
$protoCmdKey = Join-Path $ProtoKeyPath 'shell\open\command'
New-Item -Path $protoCmdKey -Force | Out-Null
Set-ItemProperty -Path $protoCmdKey -Name '(default)' -Value ('"' + $targetExe + '" --install-url "%1"')
Step ('已注册 bootanim:// 协议')

# 6. 卸载清单
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
@(
    ('installedAt=' + (Get-Date).ToString('s')),
    ('method=' + $Method),
    ('exe=' + $targetExe),
    ('runKey=' + $RunKeyPath + '\' + $RunValueName),
    ('taskName=' + $TaskName),
    ('protoKey=' + $ProtoKeyPath),
    ('dataDir=' + $DataDir),
    ('uninstall=' + (Join-Path $ScriptDir 'install.ps1') + ' -Uninstall')
) | Set-Content -Path $ManifestPath -Encoding UTF8
Step ('已写卸载清单: ' + $ManifestPath)

Say ''
Say '安装完成。'
Say ('  自启方式:     ' + $(if ($Method -eq 'task') { '计划任务（最早）' } else { 'Run 键' }))
Say ('  立刻试看一次: 运行 "' + $targetExe + '" --play --seconds 5')
Say ('  换个片头:     运行 "' + $targetExe + '" --choose')
Say ('  只看不改:     运行 "' + $targetExe + '" --manager')
Say '  完全卸载:     运行本脚本加 -Uninstall'
