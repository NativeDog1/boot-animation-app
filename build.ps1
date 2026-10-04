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

# ---------------------------------------------------------------- 背景大图
#
# 氛围模式（整帧显示 + 用同一段画面放大模糊填满四周）需要一张"这一段的某个画面"。
# WPF 的 MediaElement 不能把自己当画刷用，所以这张图在**构建期**用 ffmpeg 抽出来，
# 再内嵌进 exe。抽 0.2 秒而不是第 0 帧：不少片头是黑场淡入，第 0 帧会得到纯黑图，
# 那样的"氛围填充"等于什么都没填。
#
# 没有 ffmpeg 时不失败：只是没有背景图，氛围模式会退化成黑边（contain 的效果）。
# 也正因如此，这一步是"尽力而为"，不阻塞构建。
$posterDir = Join-Path $buildDir 'posters'
$posterFiles = @()
$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
if ($ffmpeg) {
    Write-Host '=== 0/2 生成氛围背景大图（ffmpeg） ==='
    New-Item -ItemType Directory -Path $posterDir -Force | Out-Null
    foreach ($clip in $clips) {
        $source = Join-Path $mediaDir $clip.file
        $target = Join-Path $posterDir ($clip.res -replace '\.mp4$', '.jpg')
        # 提亮是在**这里**做，不是在 WPF 里做。
        #
        # 原因：WPF 没有"亮度"效果。用"白底 + 原图当 OpacityMask"合成出来的提亮会把颜色
        # 洗成灰（实测边条 R=G=B=103），而这一段素材的颜色恰恰是青蓝色调的主视觉。
        # ffmpeg 的 eq 是按通道乘系数，颜色能保住。
        #
        # 为什么要提亮：氛围背景的原料往往是很暗的画面，直接模糊+半透明叠上去，
        # 边条量出来是 R=0 G=1 B=0 —— 和纯黑没有肉眼差别，等于没做。
        & $ffmpeg.Source -hide_banner -loglevel error -ss 0.2 -i $source -frames:v 1 `
            -vf 'scale=1280:-2,eq=brightness=0.16:contrast=1.10:saturation=1.30' -q:v 3 -y $target 2>&1 | Out-Null
        if (Test-Path $target) {
            $posterFiles += @{ file = $target; res = ('poster-' + ($clip.res -replace '\.mp4$', '.jpg')) }
            Write-Host ('  ' + $clip.res + ' -> ' + (Split-Path -Leaf $target) + ' (' + [Math]::Round((Get-Item $target).Length / 1KB) + ' KB)')
        } else {
            Write-Host ('  ' + $clip.res + ' 背景图生成失败（该段退化为纯黑边）')
        }
    }
} else {
    Write-Host '=== 0/2 跳过背景大图：找不到 ffmpeg（氛围模式会退化成黑边） ==='
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
foreach ($poster in $posterFiles) {
    $appArgs += ('/resource:' + $poster.file + ',' + $poster.res)
}
$appArgs += (Join-Path $srcDir 'BootAnimation.cs')
$appArgs += (Join-Path $srcDir 'BootRuntime.cs')
$appArgs += (Join-Path $srcDir 'AnimationState.cs')
$appArgs += (Join-Path $srcDir 'AnimationRepository.cs')
$appArgs += (Join-Path $srcDir 'PreviewEngine.cs')
$appArgs += (Join-Path $srcDir 'Platform.cs')
$appArgs += (Join-Path $srcDir 'Diagnostics.cs')
$appArgs += (Join-Path $srcDir 'ResumeRuntime.cs')
$appArgs += (Join-Path $srcDir 'WarmRuntime.cs')
$appArgs += (Join-Path $srcDir 'CrashGuard.cs')
$appArgs += (Join-Path $srcDir 'Theme.cs')
$appArgs += (Join-Path $srcDir 'DesignSystem.cs')
$appArgs += (Join-Path $srcDir 'Notifications.cs')
$appArgs += (Join-Path $srcDir 'Shell.cs')
$appArgs += (Join-Path $srcDir 'BootPreview.cs')
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

# ---------------------------------------------------------------- 原生播放器
# 单独编译 NativePlayer.exe（放在主程序旁边，安装时一起装）。
# 它刻意**不引用任何 WPF 程序集** —— 那正是它能 3–31ms 上屏的原因：
# 跳过 CLR 加载 WPF、Application 初始化、XAML 布局这一整套。
# 编译失败不让整个构建失败：主程序会检测不到它并退回 WPF 路径，功能不受影响。
Write-Host ''
Write-Host '=== 1.5/2 编译原生播放器 NativePlayer.exe ==='
$nativeExe = Join-Path $here 'NativePlayer.exe'
$nativeArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu',
    ('/out:' + $nativeExe),
    '/r:System.Drawing.dll'
)
$nativeArgs += (Join-Path $srcDir 'NativePlayer.cs')
& $csc $nativeArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host '  原生播放器编译失败（主程序会自动退回 WPF 路径，功能不受影响）'
} else {
    Write-Host ('  产物: ' + $nativeExe)
    Write-Host '  说明: 无 WPF 依赖，窗口 3–31ms 上屏且上屏时已有缓存的视频首帧图'
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
Write-Host ('  贴合方式:   --fit auto（默认）| cover | contain | ambient')
Write-Host ('  分发:      把 ' + $setup.FullName + ' 发给客户')
