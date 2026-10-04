// PreviewEngine.cs —— 预览引擎（媒体预览 / 开机屏幕预览 / Cinema Mode）
//
// 为什么单独一层：预览和"开机播放"是**两种不同的需求**。
//   · BootRuntime 要的是"最快出画，播完就走"—— 没有 UI、没有控制条、不响应鼠标。
//   · Preview 要的是"可控制、可反复看、可切换目标"—— 有播放/暂停/时间轴/音量/全屏。
//   把两者塞进同一个窗口，就会出现"为了预览的便利牺牲启动速度"或反过来。
//   所以窗口也分开：BootRuntime → Program.BuildPlayer（极简），预览 → 这里。
//
// 状态同步是这个文件的核心职责。之前的 bug 是：HomeView 在构造时就把
// MediaElement.Source 钉死，切换动画没有任何路径能改它 —— 于是必须重建窗口（F5）
// 才能换片。这里所有预览都从 AnimationStateStore 读"当前要预览谁"，
// 并且**每次换源都显式释放旧媒体**。
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员、无 XAML。

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BootAnimation
{
    /// <summary>预览的两种形态。</summary>
    internal enum PreviewKind
    {
        /// <summary>媒体预览：直接看这段视频本身。</summary>
        Media = 0,
        /// <summary>开机屏幕预览：模拟真实开机时这块屏幕上会看到什么。</summary>
        BootScreen = 1,
    }

    /// <summary>贴合方式（与 BootRuntime 的 --fit 同一套语义）。</summary>
    internal enum FitMode
    {
        /// <summary>拉伸填满 —— 真全屏，无空缺，画面变形。默认。</summary>
        Stretch = 0,
        Cover = 1,
        Contain = 2,
        Ambient = 3,
        /// <summary>按比例自动在 cover / ambient 之间选。</summary>
        Auto = 4,
    }

    internal static class PreviewEngine
    {
        internal static FitMode ParseFit(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return FitMode.Stretch;
            string f = raw.ToLowerInvariant();
            if (f == "cover") return FitMode.Cover;
            if (f == "contain") return FitMode.Contain;
            if (f == "ambient") return FitMode.Ambient;
            if (f == "auto") return FitMode.Auto;
            return FitMode.Stretch;
        }

        internal static string FitLabel(FitMode mode)
        {
            if (mode == FitMode.Stretch) return "拉伸填满";
            if (mode == FitMode.Cover) return "裁切填满";
            if (mode == FitMode.Contain) return "整帧（黑边）";
            if (mode == FitMode.Ambient) return "整帧 + 氛围";
            return "自动";
        }

        /// <summary>
        /// auto 的判定，与 Program.ResolveFit 同一套结论（1.2% 阈值）。
        /// 这里重写一遍是因为签名不同（枚举 vs 字符串），但阈值只允许有一个真源 ——
        /// 所以直接问 Program，避免两处漂移。
        /// </summary>
        /// <summary>这个模式是不是"铺满裁切"（用于决定要不要氛围背景）。</summary>
        internal static bool ResolveCover(FitMode mode, int videoW, int videoH, double screenW, double screenH)
        {
            if (mode == FitMode.Cover) return true;
            if (mode == FitMode.Contain || mode == FitMode.Ambient || mode == FitMode.Stretch) return false;
            string resolved = Program.ResolveFit("auto", videoW, videoH, screenW, screenH);
            return string.Equals(resolved, "cover", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把一段动画的封面读成画刷（内置走内嵌资源，其余走磁盘缓存）。都没有则 null。</summary>
        internal static ImageBrush PosterBrushFor(AnimationInfo info)
        {
            if (info == null) return null;
            ImageBrush embedded = Program.EmbeddedPosterBrush(info.Id);
            if (embedded != null) return embedded;
            if (info.PosterPath != null && System.IO.File.Exists(info.PosterPath))
            {
                return Program.ReadBrushFrom(info.PosterPath);
            }
            return null;
        }
    }

    /// <summary>
    /// 媒体播放器控件：把"绑定一段媒体 + 播放控制"收敛成一个可复用的对象。
    ///
    /// 关键点（对应"多个卡片共享错误视频实例"和"旧视频不刷新"）：
    ///   · 每个实例有**自己的** MediaElement，没有全局静态源
    ///   · Load(path) 总是先 Release() 再换源 —— 释放旧解码器、旧文件句柄
    ///   · 换源时立刻把上一帧清掉（否则会短暂显示旧视频的最后一帧）
    /// </summary>
    internal sealed class MediaPlayerBox : Grid
    {
        private readonly MediaElement media;
        private readonly ImageBrush posterBrush;
        private readonly System.Windows.Shapes.Rectangle posterLayer;
        private readonly TextBlock placeholder;
        private string currentPath;

        internal MediaElement Element { get { return media; } }

        internal MediaPlayerBox()
        {
            Background = Brushes.Black;
            ClipToBounds = true;

            // 封面层：媒体第一帧出来之前显示它，避免"点 Preview 之后长时间黑屏"。
            posterLayer = new System.Windows.Shapes.Rectangle();
            posterLayer.Stretch = Stretch.UniformToFill;
            posterLayer.Opacity = 0;
            Children.Add(posterLayer);
            posterBrush = null;

            media = new MediaElement();
            media.LoadedBehavior = MediaState.Manual;
            media.UnloadedBehavior = MediaState.Manual;
            media.Stretch = Stretch.Uniform;
            media.Volume = 0.8;
            media.MediaOpened += delegate { FadePosterOut(); };
            media.MediaFailed += delegate(object s, ExceptionRoutedEventArgs e)
            {
                ShowPlaceholder("打不开这段媒体：" + (e.ErrorException == null ? "未知原因" : e.ErrorException.Message));
            };
            Children.Add(media);

            placeholder = new TextBlock();
            placeholder.Foreground = Brushes.White;
            placeholder.Opacity = 0.75;
            placeholder.FontSize = 13;
            placeholder.TextAlignment = TextAlignment.Center;
            placeholder.TextWrapping = TextWrapping.Wrap;
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            placeholder.Margin = new Thickness(24);
            placeholder.Visibility = Visibility.Collapsed;
            Children.Add(placeholder);
        }

        internal string CurrentPath { get { return currentPath; } }

        /// <summary>显示封面（可选）。换源时先铺它，第一帧到了再淡出。</summary>
        internal void SetPoster(ImageBrush brush)
        {
            if (brush == null)
            {
                posterLayer.Fill = null;
                posterLayer.Opacity = 0;
                return;
            }
            posterLayer.Fill = brush;
            posterLayer.Opacity = 1;
        }

        private void FadePosterOut()
        {
            if (posterLayer.Opacity <= 0) return;
            DoubleAnimation fade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(200));
            posterLayer.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>
        /// 换源。**同一个路径重复调用是 no-op** —— 这很重要：页面上有多个操作会触发重绘，
        /// 每次重绘都重新 load() 会让播放被反复打断（这正是"看起来没切换成功"的另一种成因）。
        /// </summary>
        internal bool Load(string path)
        {
            if (path == null || path.Length == 0)
            {
                Release();
                ShowPlaceholder("这段动画还没有本地文件，先安装再预览。");
                return false;
            }
            if (string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase)) return false;

            Release();
            placeholder.Visibility = Visibility.Collapsed;
            if (posterLayer.Fill != null) posterLayer.Opacity = 1;
            currentPath = path;
            media.Source = new Uri(path, UriKind.Absolute);
            return true;
        }

        /// <summary>
        /// 彻底放掉当前媒体。
        ///
        /// 为什么必须显式做：WPF 的 MediaElement 在 Source 被替换后并不保证立刻释放
        /// 解码器和文件句柄，连续切换多段动画时会出现"切到第三段就开始黑屏/卡顿"。
        /// 置空 Source + Close() 是官方建议的释放路径。
        /// </summary>
        internal void Release()
        {
            try
            {
                media.Stop();
                media.Close();
                media.Source = null;
            }
            catch { /* 已经释放过 */ }
            currentPath = null;
        }

        internal void Play()
        {
            if (currentPath == null) return;
            try { media.Play(); } catch { /* 播放失败由 MediaFailed 报告 */ }
        }

        internal void Pause()
        {
            try { media.Pause(); } catch { }
        }

        internal void Restart()
        {
            if (currentPath == null) return;
            try { media.Position = TimeSpan.Zero; media.Play(); } catch { }
        }

        internal void TogglePlay()
        {
            try
            {
                if (media.CanPause) media.Pause();
                else media.Play();
            }
            catch { }
        }

        internal void ShowPlaceholder(string text)
        {
            placeholder.Text = text;
            placeholder.Visibility = Visibility.Visible;
        }

        internal void HidePlaceholder()
        {
            placeholder.Visibility = Visibility.Collapsed;
        }

        internal void SetMuted(bool muted)
        {
            try { media.IsMuted = muted; } catch { }
        }

        internal void SetVolume(double value)
        {
            try { media.Volume = Math.Max(0.0, Math.Min(1.0, value)); } catch { }
        }

        internal void SetFit(FitMode mode, double screenW, double screenH)
        {
            try
            {
                int vw = media.NaturalVideoWidth;
                int vh = media.NaturalVideoHeight;
                // 拉伸：直接 Fill（铺满，允许变形）—— 这是"真全屏"那一档
                if (mode == FitMode.Stretch) { media.Stretch = Stretch.Fill; return; }
                bool cover = PreviewEngine.ResolveCover(mode, vw, vh, screenW, screenH);
                media.Stretch = cover ? Stretch.UniformToFill : Stretch.Uniform;
            }
            catch { }
        }

        /// <summary>
        /// 把媒体当前帧画成一张位图。
        ///
        /// 这里**故意返回 null**，而不是硬凑一个实现：`DrawingContext.DrawVideo` 要的是
        /// `MediaPlayer`，而 `MediaElement` 是它的控件包装，不能直接传进去。要真拿到帧就得
        /// 另建一路 `MediaPlayer` 跟播放同步 —— 那等于让"开机屏幕预览"多解码一遍，
        /// 而它需要的只是一张**模糊背景**：动画自带的海报图（构建期/首次播放时抽的那张）
        /// 已经完全够用，而且零成本。
        ///
        /// 保留这个方法是把"为什么不做"写进代码，免得下次有人再试图硬凑一遍。
        /// </summary>
        internal ImageBrush SnapshotBrush()
        {
            return null;
        }
    }
}
