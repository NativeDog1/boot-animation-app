<#
  开机动画 —— 构建

  产出两个 exe（都不需要安装任何 SDK，用 Windows 自带的 csc.exe）：
    BootAnimation.exe        播放器本体，四段视频内嵌在里面（约 8.7 MB）
    BootAnimation-Setup.exe  发给客户的安装包，播放器内嵌在里面（约 8.7 MB）

  用法：
    .\build.ps1            # 两个都编，并跑一次自检校验内嵌视频
    .\build.ps1 -NoTest    # 只编，不校验
#>
param([switch]$NoTest)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $here 'src'
$mediaDir = Join-Path $here 'media'
$buildDir = Join-Path $here 'build'
$icon = Join-Path $buildDir 'app.ico'
$appExe = Join-Path $here 'BootAnimation.exe'
$setupExe = Join-Path $here 'BootAnimation-Setup.exe'
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'

if (-not (Test-Path $csc)) { throw ('找不到 Windows 自带的 C# 编译器: ' + $csc) }
if (-not (Test-Path $icon)) { throw ('找不到图标: ' + $icon) }

$refs = @(
    (Join-Path $fw 'WPF\PresentationFramework.dll'),
    (Join-Path $fw 'WPF\PresentationCore.dll'),
    (Join-Path $fw 'WPF\WindowsBase.dll'),
    (Join-Path $fw 'System.Xaml.dll')
)
foreach ($ref in $refs) { if (-not (Test-Path $ref)) { throw ('缺少程序集: ' + $ref) } }

# 资源名必须与源码里 Clip.Res 一致
$clips = @(
    @{ file = 'deepseek-brand-intro.mp4'; res = 'brand.mp4' },
    @{ file = 'deepseek-cyberpunk-intro.mp4'; res = 'cyberpunk.mp4' },
    @{ file = 'deepseek-awakening-intro.mp4'; res = 'awakening.mp4' },
    @{ file = 'deepseek-startup-intro.mp4'; res = 'startup.mp4' }
)
foreach ($clip in $clips) {
    $path = Join-Path $mediaDir $clip.file
    if (-not (Test-Path $path)) { throw ('缺少素材: ' + $path) }
}

# ---------------------------------------------------------------- 播放器
Write-Host '=== 1/2 编译播放器 BootAnimation.exe ==='
$appArgs = @('/nologo', '/target:winexe', '/platform:anycpu', ('/out:' + $appExe), ('/win32icon:' + $icon))
foreach ($ref in $refs) { $appArgs += ('/r:' + $ref) }
$rawTotal = 0
foreach ($clip in $clips) {
    $path = Join-Path $mediaDir $clip.file
    $rawTotal += (Get-Item $path).Length
    $appArgs += ('/resource:' + $path + ',' + $clip.res)
}
$appArgs += (Join-Path $srcDir 'BootAnimation.cs')
$appArgs += (Join-Path $srcDir 'Theme.cs')
$appArgs += (Join-Path $srcDir 'Shell.cs')
$appArgs += (Join-Path $srcDir 'Community.cs')
$appArgs += (Join-Path $srcDir 'AssemblyInfo.cs')
& $csc $appArgs
if ($LASTEXITCODE -ne 0) { throw '播放器编译失败' }
$exe = Get-Item $appExe
Write-Host ('  产物: ' + $exe.FullName)
Write-Host ('  大小: ' + [Math]::Round($exe.Length / 1MB, 2) + ' MB（内嵌视频合计 ' + [Math]::Round($rawTotal / 1MB, 2) + ' MB）')

if (-not $NoTest) {
    Write-Host '  自检中（解包内嵌视频并校验哈希）…'
    $proc = Start-Process -FilePath $appExe -ArgumentList '--selftest' -Wait -PassThru -WindowStyle Hidden
    $report = Join-Path $env:LOCALAPPDATA 'BootAnimation\selftest.txt'
    if (Test-Path $report) {
        # 报告是 C# 按 UTF-8（无 BOM）写的；不指定编码，PS 5.1 会当 ANSI 读成乱码
        Get-Content $report -Encoding UTF8 | ForEach-Object { Write-Host ('    ' + $_) }
    }
    if ($proc.ExitCode -ne 0) { throw ('自检失败，退出码 ' + $proc.ExitCode) }
    Write-Host '  自检通过'
}

# ---------------------------------------------------------------- 安装包
Write-Host ''
Write-Host '=== 2/2 编译安装包 BootAnimation-Setup.exe ==='
$setupArgs = @('/nologo', '/target:winexe', '/platform:anycpu', ('/out:' + $setupExe), ('/win32icon:' + $icon))
foreach ($ref in $refs) { $setupArgs += ('/r:' + $ref) }
$setupArgs += ('/resource:' + $appExe + ',BootAnimation.payload.exe')
$setupArgs += (Join-Path $srcDir 'Setup.cs')
$setupArgs += (Join-Path $srcDir 'SetupInfo.cs')
& $csc $setupArgs
if ($LASTEXITCODE -ne 0) { throw '安装包编译失败' }
$setup = Get-Item $setupExe
Write-Host ('  产物: ' + $setup.FullName)
Write-Host ('  大小: ' + [Math]::Round($setup.Length / 1MB, 2) + ' MB —— 这就是发给客户的唯一文件')

Write-Host ''
Write-Host '构建完成。'
Write-Host ('  自用/试看: ' + $appExe + ' --play --seconds 5')
Write-Host ('  分发:      把 ' + $setup.FullName + ' 发给客户')
