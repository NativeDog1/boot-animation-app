<#
  开机动画（BootAnimation）—— 安装 / 卸载

  这个脚本不修改任何系统级设置，只做四件事：
    1) 把 exe 复制到一个固定位置（%LOCALAPPDATA%\Programs\BootAnimation）
    2) 注册开机自启（默认写 HKCU\...\Run 键，不需要管理员）
    3) 注册 bootanim:// 协议 —— 社区网站上点「用开机动画打开」靠它唤起本程序
    4) 留一份「卸载清单」，卸载时照着原样删掉

  用法：
    .\install.ps1                    # 安装（默认：Run 键，不需要管理员）
    .\install.ps1 -Method task       # 用计划任务（需要管理员，启动更早）
    .\install.ps1 -Uninstall         # 卸载，还原成没装过的样子
    .\install.ps1 -DryRun            # 只打印将要做什么，不改动任何东西
    .\install.ps1 -Clip cyberpunk    # 安装时顺便把片头设成某一段

  为什么默认用 Run 键而不是计划任务：Run 键不需要管理员权限，卸载只需删一个键值，
  而且能在「设置 → 应用 → 启动」和任务管理器里被你自己随时关掉。计划任务能更早启动，
  但要管理员，所以做成可选项。
#>
param(
    [ValidateSet('run', 'task')][string]$Method = 'run',
    [switch]$Uninstall,
    [switch]$DryRun,
    [string]$Clip = '',
    [double]$Delay = 3
)

$ErrorActionPreference = 'Stop'

$RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValueName = 'BootAnimation'
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
            if ($DryRun) { Step ('将删除 Run 键值: ' + $RunValueName) }
            else {
                Remove-ItemProperty -Path $RunKeyPath -Name $RunValueName -ErrorAction SilentlyContinue
                Step ('已删除 Run 键值: ' + $RunValueName)
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
    if ($Method -eq 'run') {
        Step ('将写入 Run 键 ' + $RunValueName + ' = "' + (Join-Path $InstallDir 'BootAnimation.exe') + '" --play --delay ' + $Delay)
    } else {
        Step ('将注册计划任务 ' + $TaskName + '（登录时触发，需要管理员）')
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
if ($Method -eq 'run') {
    $command = '"' + $targetExe + '" --play --delay ' + $Delay
    New-Item -Path $RunKeyPath -Force | Out-Null
    Set-ItemProperty -Path $RunKeyPath -Name $RunValueName -Value $command
    Step ('已注册开机自启（Run 键）: ' + $command)
} else {
    $action = New-ScheduledTaskAction -Execute $targetExe -Argument ('--play --delay ' + $Delay)
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew
    try {
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Description '登录后播放开机动画' -Force | Out-Null
        Step ('已注册计划任务: ' + $TaskName)
    } catch {
        Say ('  注册计划任务失败（多半是因为没有管理员权限）: ' + $_.Exception.Message)
        Say '  改用不需要管理员的 Run 键重试。'
        $command = '"' + $targetExe + '" --play --delay ' + $Delay
        New-Item -Path $RunKeyPath -Force | Out-Null
        Set-ItemProperty -Path $RunKeyPath -Name $RunValueName -Value $command
        Step ('已注册开机自启（Run 键）: ' + $command)
        $Method = 'run'
    }
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
Say ('  立刻试看一次: 运行 "' + $targetExe + '" --play --seconds 5')
Say ('  换个片头:     运行 "' + $targetExe + '" --choose')
Say '  完全卸载:     运行本脚本加 -Uninstall'
