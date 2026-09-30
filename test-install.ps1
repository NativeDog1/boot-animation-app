<#
  开机动画 —— 安装包往返自测

  把「客户会经历的完整流程」跑一遍，并把每一处实际状态打出来：
    静默安装 → 逐项核对 → 静默卸载 → 逐项核对 → 再装回去

  只有全部 ✓ 才说明这个安装包可以发给客户。

  用法：
    .\test-install.ps1
#>
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$setup = Join-Path $here 'BootAnimation-Setup.exe'
$instDir = Join-Path $env:LOCALAPPDATA 'Programs\BootAnimation'
$installed = Join-Path $instDir 'BootAnimation.exe'
$dataDir = Join-Path $env:LOCALAPPDATA 'BootAnimation'
$uninsKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BootAnimation'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$link = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\开机动画.lnk'

if (-not (Test-Path $setup)) { throw ('找不到安装包: ' + $setup + '，先跑 .\build.ps1') }

$fail = 0
function Check($label, $ok) {
    if ($ok) { Write-Host ('  [OK]   ' + $label) }
    else { Write-Host ('  [FAIL] ' + $label); $script:fail++ }
}

Write-Host '1) 静默安装'
Get-Process BootAnimation -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $setup -ArgumentList '/SILENT' -Wait -PassThru
Check ('安装退出码为 0（实际 ' + $p.ExitCode + '）') ($p.ExitCode -eq 0)
Check '程序 exe 已就位' (Test-Path $installed)
Check '开机自启已注册' ([bool]((Get-ItemProperty $runKey -Name BootAnimation -ErrorAction SilentlyContinue).BootAnimation))
Check '卸载入口已注册' (Test-Path $uninsKey)
Check '开始菜单快捷方式已创建' (Test-Path $link)

Write-Host '   客户在「应用和功能」里会看到：'
if (Test-Path $uninsKey) {
    $k = Get-ItemProperty $uninsKey
    Write-Host ('     ' + $k.DisplayName + '  ' + $k.DisplayVersion + '  ' + $k.Publisher)
    Write-Host ('     卸载命令: ' + $k.UninstallString)
}

Write-Host '2) 静默卸载（走「应用和功能」那条路）'
$u = Start-Process -FilePath $installed -ArgumentList '--uninstall', '--silent' -Wait -PassThru
Check ('卸载退出码为 0（实际 ' + $u.ExitCode + '）') ($u.ExitCode -eq 0)

# 目录删除是交给一个分离的 cmd 做的，给它几秒钟
$gone = $false
for ($i = 1; $i -le 15; $i++) {
    Start-Sleep -Seconds 1
    if (-not (Test-Path $instDir)) { $gone = $true; break }
}
Check ('程序目录已被删除（等了 ' + $i + ' 秒）') $gone
Check '自启键已删除' (-not [bool]((Get-ItemProperty $runKey -Name BootAnimation -ErrorAction SilentlyContinue).BootAnimation))
Check '卸载入口已删除' (-not (Test-Path $uninsKey))
Check '开始菜单快捷方式已删除' (-not (Test-Path $link))
Check '临时脚本已自删' (-not (Test-Path (Join-Path $env:TEMP 'ba-uninstall.cmd')))
Check '选片与日志被保留（设计如此）' (Test-Path (Join-Path $dataDir 'settings.txt'))

Write-Host '3) 再装回去（恢复正常使用状态）'
$p2 = Start-Process -FilePath $setup -ArgumentList '/SILENT' -Wait -PassThru
Check ('重新安装退出码为 0（实际 ' + $p2.ExitCode + '）') ($p2.ExitCode -eq 0)
Check '程序 exe 已回来' (Test-Path $installed)

Write-Host ''
if ($fail -eq 0) {
    Write-Host '全部通过 —— 这个安装包可以发给客户。'
} else {
    Write-Host ('有 ' + $fail + ' 项没通过，先别发。日志: ' + (Join-Path $dataDir 'setup.log'))
    exit 1
}
