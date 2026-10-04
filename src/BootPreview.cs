// BootPreview.cs —— 沉浸式开机预览（Boot Preview / Cinema Mode）
//
// 这是整个软件最该有特色的地方，所以它必须是一个**独立的全屏层**，而不是
// "在主窗口里放一个视频框"。理由有三条，每条都是实测出来的：
//
//   1. **比例必须是真的。** Windows 开机时动画铺满的是**这块屏幕**，不是主窗口里
//      那个被侧栏挤窄的盒子。预览区按用户真实屏幕比例（EnumDisplaySettings 读物理像素）
//      渲染，2560×1600 的机器就按 1.6 显示。比例不对的"开机预览"是在骗人。
//   2. **控制条不该常驻。** 开机屏幕上是没有 UI 的，所以默认满屏播放、鼠标不动时
//      一点控件都不显示。这是"沉浸"与"播放器"的唯一区别。
//   3. **Fit / Fill / Original 必须禁止拉伸。** 三者都只改**显示区域的比例与贴合方式**，
//      永远不动视频的宽高比。
//
// 另外把"显示区域比例"和"贴合方式"拆成两个正交的开关 —— 这是 Display Preview 的实质：
//   Display 决定**盒子**多大、什么比例（Current / 16:9 / 16:10 / 21:9 / 4K / 1440p / 1080p）
//   Fit     决定**画面**怎么放进盒子（Fit 留边 / Fill 裁切 / Original 原始尺寸）
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员、无 XAML。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace BootAnimation
{
    /// <summary>预览层的形态。</summary>
    internal enum PreviewLayer
    {
        /// <summary>媒体预览：铺满窗口，看视频本身。</summary>
        Media = 0,
        /// <summary>开机预览：模拟开机屏幕（黑场 + 按真实比例的显示区域）。</summary>
        BootScreen = 1,
    }

    /// <summary>显示区域比例的预设。原动画永远不被拉伸。</summary>
    internal enum DisplayProfile
    {
        Current = 0,
        Wide169 = 1,
        Wide1610 = 2,
        Ultra219 = 3,
        Uhd4K = 4,
        Qhd1440 = 5,
        Fhd1080 = 6,
    }

    internal sealed class BootPreview : Window
    {
        private readonly AnimationInfo info;
        private readonly PreviewLayer layer;
        private readonly MediaPlayerBox player = new MediaPlayerBox();
        private readonly Border screenBox = new Border();
        private readonly StackPanel topBar = new StackPanel();
        private readonly StackPanel bottomBar = new StackPanel();
        private readonly DispatcherTimer hideChrome = new DispatcherTimer();
        private readonly TextBlock timeText;
        private readonly Border timelineFill;
        private readonly Border timelineTrack;
        private readonly TextBlock displayLabel;
        private readonly TextBlock fitLabel;

        private DisplayProfile profile = DisplayProfile.Current;
        private FitMode fit = FitMode.Ambient;
        private int actualW;
        private int actualH;
        private bool chromeVisible = true;
        private bool dragging;

        internal BootPreview(AnimationInfo info, PreviewLayer layer)
        {
            this.info = info;
            this.layer = layer;

            Ui.ApplyWindow(this);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.Arrow;
            Title = (layer == PreviewLayer.BootScreen ? "开机预览 — " : "预览 — ")
                + AnimationStateStore.DisplayName(info);

            Platform.PrimaryScreenPixels(out actualW, out actualH);
            if (actualW <= 0 || actualH <= 0) { actualW = 1920; actualH = 1080; }

            // ── 背景层：极暗，只为了让小黑场不显得像"没渲染出来"
            Grid root = new Grid();
            root.Background = Brushes.Black;

            // ── 显示区域：按 profile 的比例居中
            screenBox.Background = Brushes.Black;
            screenBox.HorizontalAlignment = HorizontalAlignment.Center;
            screenBox.VerticalAlignment = VerticalAlignment.Center;
            screenBox.ClipToBounds = true;
            screenBox.CornerRadius = new CornerRadius(layer == PreviewLayer.BootScreen ? 2 : 0);
            screenBox.SnapsToDevicePixels = true;

            player.ShowPlaceholder("正在准备预览…");
            screenBox.Child = player;
            root.Children.Add(screenBox);
            // 注意：一个元素只能有一个父级。下面 top 会被放进 topLayer，
            // 所以这里**不能**再 root.Children.Add(top) —— 那样会在
            // "指定的元素已经是另一个元素的逻辑子元素"上直接抛异常，
            // 而 WinExe 的未处理异常是静默的（表现为"点了没反应"）。

            // ── 顶部 chrome：一行，只在鼠标动时出现
            StackPanel top = topBar;
            top.HorizontalAlignment = HorizontalAlignment.Left;
            top.VerticalAlignment = VerticalAlignment.Top;
            top.Margin = new Thickness(Theme.S6, Theme.S5, Theme.S6, 0);

            TextBlock kind = Ds.Label(layer == PreviewLayer.BootScreen ? "Boot Preview" : "Media Preview", Theme.Fg2);
            kind.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(kind);

            TextBlock name = Ds.Text(AnimationStateStore.DisplayName(info), Theme.TMeta, Theme.Fg, FontWeights.SemiBold);
            name.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(name);

            displayLabel = Ds.Label(ProfileLabel(profile), Theme.Fg3);
            displayLabel.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(displayLabel);

            fitLabel = Ds.Label(PreviewEngine.FitLabel(fit), Theme.Fg3);
            fitLabel.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(fitLabel);

            // ── 底部 chrome：细时间轴 + 极简控制
            StackPanel bottom = bottomBar;
            bottom.HorizontalAlignment = HorizontalAlignment.Center;
            bottom.VerticalAlignment = VerticalAlignment.Bottom;
            bottom.Margin = new Thickness(Theme.S7, 0, Theme.S7, Theme.S6);
            bottom.Width = 720;

            // 细时间轴：4px 轨道 + 强调色进度。需求明确"Timeline 应该非常细"。
            timelineTrack = new Border();
            timelineTrack.Height = 4;
            timelineTrack.CornerRadius = new CornerRadius(2);
            timelineTrack.Background = Theme.LineStrong;
            timelineTrack.Cursor = Cursors.Hand;
            Grid trackGrid = new Grid();
            timelineFill = new Border();
            timelineFill.Height = 4;
            timelineFill.CornerRadius = new CornerRadius(2);
            timelineFill.Background = Accent.Brush;
            timelineFill.HorizontalAlignment = HorizontalAlignment.Left;
            timelineFill.Width = 0;
            trackGrid.Children.Add(timelineTrack);
            trackGrid.Children.Add(timelineFill);
            timelineTrack.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                dragging = true;
                SeekFrom(e.GetPosition(trackGrid).X, trackGrid.ActualWidth);
            };
            timelineTrack.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (dragging) SeekFrom(e.GetPosition(trackGrid).X, trackGrid.ActualWidth);
            };
            timelineTrack.MouseLeftButtonUp += delegate { dragging = false; };
            bottom.Children.Add(trackGrid);

            StackPanel controls = Ds.Bar(Theme.S3);
            controls.HorizontalAlignment = HorizontalAlignment.Center;
            controls.Children.Add(Ds.IconAction("▶", "播放", delegate { player.Play(); }));
            controls.Children.Add(Ds.IconAction("⏸", "暂停", delegate { player.Pause(); }));
            controls.Children.Add(Ds.IconAction("↺", "重新播放", delegate { player.Restart(); }));
            controls.Children.Add(Ds.IconAction("🔊", "静音 / 取消静音", delegate
            {
                muted = !muted;
                player.SetMuted(muted);
            }));
            controls.Children.Add(Ds.IconAction("⛶", "全屏（F11）", delegate { ToggleFullscreen(); }));

            timeText = Ds.Text("00:00 / 00:00", Theme.TMicro, Theme.Fg2, FontWeights.Normal);
            timeText.VerticalAlignment = VerticalAlignment.Center;
            timeText.Margin = new Thickness(Theme.S3, 0, Theme.S3, 0);
            controls.Children.Add(timeText);

            // Display 与 Fit 的切换做成小节文字按钮，避免堆一堆大按钮
            controls.Children.Add(Ds.Action("Display", ActionLevel.Tertiary, delegate { CycleProfile(); }));
            controls.Children.Add(Ds.Action("Fit", ActionLevel.Tertiary, delegate { CycleFit(); }));
            controls.Children.Add(Ds.Action("Exit Cinema", ActionLevel.Tertiary, delegate { Close(); }));

            bottom.Children.Add(controls);

            // top 与 bottom 各自单独叠一层（必须分开：它们要在 root 里对齐到上/下两端）。
            //
            // 这里**不能**再有一个统一的 chrome 容器把 top 也装进去 —— 元素只能有一个父级，
            // 装两处就会抛"指定的元素已经是另一个元素的逻辑子元素"。之前正是留了一个
            // 已废弃的 chrome 字段，导致 top 被挂两次、预览直接打不开。
            Grid chromeLayer = new Grid();
            chromeLayer.IsHitTestVisible = false;
            chromeLayer.Children.Add(bottom);
            bottom.IsHitTestVisible = true;
            root.Children.Add(chromeLayer);

            Grid topLayer = new Grid();
            topLayer.IsHitTestVisible = false;
            topLayer.Children.Add(top);
            top.IsHitTestVisible = true;
            root.Children.Add(topLayer);

            Content = root;

            // ── 鼠标静止 2.4 秒后隐藏 chrome（沉浸）
            hideChrome.Interval = TimeSpan.FromSeconds(2.4);
            hideChrome.Tick += delegate { hideChrome.Stop(); SetChrome(false); };
            MouseMove += delegate
            {
                if (!chromeVisible) SetChrome(true);
                hideChrome.Stop();
                hideChrome.Start();
                Cursor = Cursors.Arrow;
            };

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Close();
                else if (e.Key == Key.F11) ToggleFullscreen();
                else if (e.Key == Key.F) CycleFit();
                else if (e.Key == Key.D) CycleProfile();
                else if (e.Key == Key.Space) player.TogglePlay();
            };
            MouseLeftButtonUp += delegate { if (!chromeVisible || true) { } };
            Closed += delegate { player.Release(); hideChrome.Stop(); };

            Loaded += delegate
            {
                ApplyProfile();
                LayoutScreen();
                player.SetPoster(PreviewEngine.PosterBrushFor(info));
                player.Load(info.Path);
                player.SetFit(fit, actualW, actualH);
                player.Play();
                muted = false;
                hideChrome.Start();
                StartClock();
            };
        }

        private bool muted;
        private DispatcherTimer clock;
        private bool isFullscreenBox = true;

        private void StartClock()
        {
            clock = new DispatcherTimer();
            clock.Interval = TimeSpan.FromMilliseconds(120);
            clock.Tick += delegate { UpdateClock(); };
            clock.Start();
            Closed += delegate { if (clock != null) clock.Stop(); };
        }

        private void UpdateClock()
        {
            try
            {
                MediaElement m = player.Element;
                TimeSpan pos = m.Position;
                TimeSpan len = m.NaturalDuration.HasTimeSpan ? m.NaturalDuration.TimeSpan : TimeSpan.Zero;
                timeText.Text = Mmss(pos) + " / " + (len > TimeSpan.Zero ? Mmss(len) : "--:--");
                double frac = len > TimeSpan.Zero ? pos.TotalMilliseconds / len.TotalMilliseconds : 0;
                if (frac < 0) frac = 0;
                if (frac > 1) frac = 1;
                double trackW = timelineTrack.ActualWidth;
                if (trackW > 0) timelineFill.Width = trackW * frac;
            }
            catch { }
        }

        private static string Mmss(TimeSpan t)
        {
            return t.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":"
                + t.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private void SeekFrom(double x, double width)
        {
            if (width <= 0) return;
            try
            {
                MediaElement m = player.Element;
                if (!m.NaturalDuration.HasTimeSpan) return;
                double frac = x / width;
                if (frac < 0) frac = 0;
                if (frac > 1) frac = 1;
                m.Position = TimeSpan.FromMilliseconds(m.NaturalDuration.TimeSpan.TotalMilliseconds * frac);
            }
            catch { }
        }

        private void SetChrome(bool visible)
        {
            chromeVisible = visible;
            // 上下两条要一起淡（它们各自是独立的一层，没有共同父容器可用）
            Ds.Fade(topBar, visible ? 1.0 : 0.0, Theme.AnimSlow);
            Ds.Fade(bottomBar, visible ? 1.0 : 0.0, Theme.AnimSlow);
        }

        private void ToggleFullscreen()
        {
            // "全屏"在这个窗口里指的是显示区域占满窗口（去掉黑场边距）
            isFullscreenBox = !isFullscreenBox;
            LayoutScreen();
        }

        /// <summary>切换显示区域比例。**不改视频，只改盒子。**</summary>
        private void CycleProfile()
        {
            int next = ((int)profile + 1) % 7;
            profile = (DisplayProfile)next;
            displayLabel.Text = ProfileLabel(profile).ToUpperInvariant();
            ApplyProfile();
            LayoutScreen();
            player.SetFit(fit, actualW, actualH);
        }

        internal string ProfileLabel(DisplayProfile p)
        {
            if (p == DisplayProfile.Wide169) return "16:9";
            if (p == DisplayProfile.Wide1610) return "16:10";
            if (p == DisplayProfile.Ultra219) return "21:9";
            if (p == DisplayProfile.Uhd4K) return "4K 3840×2160";
            if (p == DisplayProfile.Qhd1440) return "1440p 2560×1440";
            if (p == DisplayProfile.Fhd1080) return "1080p 1920×1080";
            return "当前显示器 " + actualW.ToString(CultureInfo.InvariantCulture) + "×"
                + actualH.ToString(CultureInfo.InvariantCulture);
        }

        private void ApplyProfile()
        {
            // Display 的比例决定**显示区域**的形状；Original 之外的 Fit 都不拉伸视频。
            // 这里只记录比例，实际尺寸在 LayoutScreen 里按窗口算。
        }

        private double ProfileRatio()
        {
            if (profile == DisplayProfile.Wide169) return 16.0 / 9.0;
            if (profile == DisplayProfile.Wide1610) return 16.0 / 10.0;
            if (profile == DisplayProfile.Ultra219) return 21.0 / 9.0;
            if (profile == DisplayProfile.Uhd4K) return 3840.0 / 2160.0;
            if (profile == DisplayProfile.Qhd1440) return 2560.0 / 1440.0;
            if (profile == DisplayProfile.Fhd1080) return 1920.0 / 1080.0;
            if (actualW > 0 && actualH > 0) return (double)actualW / actualH;
            return 16.0 / 9.0;
        }

        /// <summary>
        /// 布局显示区域。
        ///
        /// 开机预览：区域按 profile 比例居中，四周留一点黑场（像一块屏幕嵌在黑色环境里）
        /// Media 预览：区域直接铺满窗口
        /// 全屏（F11）：两种情况都铺满。
        /// </summary>
        private void LayoutScreen()
        {
            double availW = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
            double availH = ActualHeight > 0 ? ActualHeight : SystemParameters.PrimaryScreenHeight;

            if (layer == PreviewLayer.Media || isFullscreenBox)
            {
                // 媒体预览直接铺满：不引入黑场，让视频自己最大
                screenBox.Width = double.NaN;
                screenBox.Height = double.NaN;
                screenBox.HorizontalAlignment = HorizontalAlignment.Stretch;
                screenBox.VerticalAlignment = VerticalAlignment.Stretch;
                player.SetFit(fit, actualW, actualH);
                return;
            }

            double ratio = ProfileRatio();
            // 留 6% 边距，模拟"屏幕嵌在环境里"，而不是贴边铺满
            double maxH = availH * 0.88;
            double maxW = availW * 0.88;
            double h = maxH;
            double w = h * ratio;
            if (w > maxW) { w = maxW; h = w / ratio; }
            screenBox.HorizontalAlignment = HorizontalAlignment.Center;
            screenBox.VerticalAlignment = VerticalAlignment.Center;
            screenBox.Width = w;
            screenBox.Height = h;
        }

        /// <summary>Fit 循环：Fit（整帧留边）→ Fill（铺满裁切）→ Original（原始尺寸居中）。</summary>
        private void CycleFit()
        {
            if (fit == FitMode.Ambient) fit = FitMode.Cover;
            else if (fit == FitMode.Cover) fit = FitMode.Contain;
            else if (fit == FitMode.Contain) fit = FitMode.Auto;
            else fit = FitMode.Ambient;
            fitLabel.Text = PreviewEngine.FitLabel(fit).ToUpperInvariant();

            // Original：不缩放，按视频原始像素居中（大于窗口就自然裁掉）
            if (fit == FitMode.Auto)
            {
                player.Element.Stretch = Stretch.None;
                player.Element.HorizontalAlignment = HorizontalAlignment.Center;
                player.Element.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                player.Element.HorizontalAlignment = HorizontalAlignment.Stretch;
                player.Element.VerticalAlignment = VerticalAlignment.Stretch;
                player.SetFit(fit, actualW, actualH);
            }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            LayoutScreen();
        }
    }
}
