# mf-decode-probe.ps1 —— 测 WinRT MediaPlayer 打开各类视频的真实耗时
#
# 目的：把"解码器初始化开销"从混在一起的总耗时里单独量出来。
#
# 为什么这很关键：
#   之前测出"第一次运行 HEVC Main 10 4K 要多花约 785ms，之后就不花了"，
#   但那 785ms 混在窗口创建里分不清是解码器、视频处理器还是别的。
#   登录时一切皆冷，这笔开销要重新付 —— 如果它本身就有几秒，
#   那就是"登录后等很久"的直接原因。
#
# 做法：用 WinRT 的 MediaPlayer（PowerShell 可直接实例化），对**同一个进程**依次
# 打开多个文件，记录每个文件从"设置 Source"到"MediaOpened"的耗时。
#   · 第一个文件包含全部一次性初始化
#   · 后面的文件只反映各自的解码器开销
#   · 同一文件重复打开可看缓存效果
#
# 用法: powershell -File mf-decode-probe.ps1

$ErrorActionPreference = 'Stop'
$d = Join-Path $env:LOCALAPPDATA 'BootAnimation'
$candidates = @(
    @{ n = '原文件 HEVC Main10 4K'; p = (Join-Path $d 'community\nativedog1__nativedog1-447c7d96.mp4') },
    @{ n = '快速版 H.264 8bit 1440p'; p = (Join-Path $d 'fast\nativedog1__nativedog1-447c7d96.mp4') },
    @{ n = '内置 H.264 1440p'; p = (Join-Path $d 'clips\startup.mp4') },
    @{ n = '原文件 再来一次(看缓存)'; p = (Join-Path $d 'community\nativedog1__nativedog1-447c7d96.mp4') }
)

Add-Type -AssemblyName System.Runtime.WindowsRuntime -ErrorAction SilentlyContinue

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                   $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]

function Await($winRtTask, $resultType) {
    $asTask = $asTaskGeneric.MakeGenericMethod($resultType)
    $netTask = $asTask.Invoke($null, @($winRtTask))
    $netTask.Wait(20000) | Out-Null
    if (-not $netTask.IsCompleted) { return $null }
    return $netTask.Result
}

Write-Host "=== 媒体解码开销实测（WinRT MediaPlayer） ==="
Write-Host ""

$mediaPlayerType = [Windows.Media.Playback.MediaPlayer, Windows.Media, ContentType = WindowsRuntime]
$mediaSourceType = [Windows.Media.Core.MediaSource, Windows.Media, ContentType = WindowsRuntime]

$player = [Activator]::CreateInstance($mediaPlayerType)
$player.AutoPlay = $false
$player.IsVideoFrameServerEnabled = $true     # 不需要播放器自己的窗口
$player.Volume = 0

foreach ($c in $candidates) {
    if (-not (Test-Path $c.p)) { Write-Host ("  {0}: 文件不存在" -f $c.n); continue }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $uri = [Uri]("file:///" + ($c.p -replace '\\', '/'))
    $src = $mediaSourceType::CreateFromUri($uri)
    $tCreate = $sw.ElapsedMilliseconds

    $player.Source = $src
    $tSet = $sw.ElapsedMilliseconds

    # 等 MediaOpened（或超时）
    $opened = $false
    for ($i = 0; $i -lt 200; $i++) {
        Start-Sleep -Milliseconds 25
        try {
            if ($player.PlaybackSession.NaturalVideoWidth -gt 0) { $opened = $true; break }
        } catch { }
        if ($sw.ElapsedMilliseconds -gt 20000) { break }
    }
    $tOpened = $sw.ElapsedMilliseconds

    $w = 0; $h = 0
    try { $w = $player.PlaybackSession.NaturalVideoWidth; $h = $player.PlaybackSession.NaturalVideoHeight } catch { }

    Write-Host ("  {0}" -f $c.n)
    Write-Host ("      建 MediaSource = {0}ms   设 Source = {1}ms   **MediaOpened = {2}ms**   尺寸={3}x{4}" -f `
        $tCreate, ($tSet - $tCreate), $tOpened, $w, $h)
}

$player.Dispose()
Write-Host ""
Write-Host "判读: MediaOpened 那一列如果是数百毫秒以上，说明解码器初始化本身很重；"
Write-Host "      登录时一切皆冷，这笔开销会重新付一遍。"
