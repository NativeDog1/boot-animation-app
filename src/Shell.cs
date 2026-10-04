// Shell.cs —— 主窗口与全部页面（Media-first 架构）
//
// ─────────────────────────────────────────────────────────────────────────────
// 这一版重做的不是"样式"，是**空间结构与视觉层级**。原来的首页是
// "左导航 + 中视频 + 右按钮 + 下状态卡"——功能齐全，但它是 Dashboard 的骨架，
// 而 Dashboard 骨架天然把技术信息放在与内容同等的地位。
//
// 新的层级（用户视线的落点顺序，也是需求里明确要求的）：
//
//   第 1 眼  当前动画的大型 Hero Media（占首页主要视觉区域，600px 上限）
//   第 2 眼  动画名称 + 作者 + ACTIVE + 元信息（分辨率/FPS/时长/体积）
//   第 3 眼  Preview Boot（唯一 Primary CTA）
//   第 4 眼  System Status / Startup / Boot Runtime（退到下方，去掉大黑卡）
//
// 关键手法（每一条都是"成熟软件 vs 内部工具"的实际差别）：
//   · Hero 不用卡片边框 —— 画面与页面背景融合，靠背景氛围层托住
//   · 播放控件**不常驻** —— 鼠标进入 Hero 才出现，且是小型 icon 按钮 + 极细时间轴
//   · 按钮分三档主次 —— Primary 只有一个（Preview Boot），Apply 是 Secondary 且随状态变字
//   · System Status 去掉外框、缩小字号、靠对齐形成秩序，不再压过动画
//   · 每一页的背景都由当前动画 Artwork 驱动（模糊 + 暗化 + 晕影），Accent 随之变化
//
// 状态同步：所有页面都从 AnimationStateStore 读、订阅它的 Changed 重建自己 ——
// 没有任何页面持有"当前动画"的副本，所以不存在"某一处忘了刷新"。
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员、无 XAML。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    internal enum Page
    {
        Home = 0,
        Library = 1,
        Community = 2,
        System = 3,
        Startup = 4,
        Diagnostics = 5,
        Settings = 6,
        About = 7,
    }

    /// <summary>动画库的筛选页签。</summary>
    internal enum LibraryFilter
    {
        All = 0,
        Installed = 1,
        Favorites = 2,
        Recent = 3,
    }

    internal sealed class Shell : Window
    {
        private readonly AnimationStateStore store = new AnimationStateStore();
        private readonly ContentControl host = new ContentControl();
        private readonly StackPanel navList = new StackPanel();
        private readonly List<NavEntry> navEntries = new List<NavEntry>();
        private readonly DispatcherTimer statusTimer = new DispatcherTimer();

        // 背景三层：Artwork（模糊）→ 暗化 → 晕影
        private readonly Image backdropImage = new Image();
        private readonly Border backdropDim = new Border();
        private readonly Border backdropVignette = new Border();

        private readonly NoticeHost notices = new NoticeHost();
        private readonly TextBlock sidebarStatus = Ds.Text("", Theme.TMicro, Theme.Fg3, FontWeights.Normal);

        /// <summary>
        /// 首次运行引导层。默认 Visibility.Hidden，只有 IsFirstRun() 为真时才显示
        /// （见 Loaded 里那段）。保持字段是为了让「开始使用」按钮能把它收起来。
        /// </summary>
        private Grid welcomeLayer;

        private Page current = Page.Home;
        private LibraryFilter filter = LibraryFilter.All;
        private MediaPlayerBox hero;
        private Border heroControls;
        private FitMode heroFit = FitMode.Ambient;
        private bool heroMuted;
        private DiagnosticsReport lastReport;
        private string resumeNote = "";
        private readonly List<string> recentIds = new List<string>();

        private sealed class NavEntry
        {
            public Page Page;
            public Border Box;
            public TextBlock Cn;
            public TextBlock En;
            public Border Marker;
        }

        internal Shell()
        {
            Ui.ApplyWindow(this);
            Title = "开机动画";
            Width = Theme.WinDefaultW;
            Height = Theme.WinDefaultH;
            MinWidth = Theme.WinMinW;
            MinHeight = Theme.WinMinH;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Grid root = new Grid();
            root.Children.Add(BuildBackdrop());

            Grid body = new Grid();
            ColumnDefinition navCol = new ColumnDefinition();
            navCol.Width = new GridLength(Theme.NavW);
            ColumnDefinition mainCol = new ColumnDefinition();
            mainCol.Width = new GridLength(1, GridUnitType.Star);
            body.ColumnDefinitions.Add(navCol);
            body.ColumnDefinitions.Add(mainCol);
            body.Children.Add(BuildSidebar());
            Grid.SetColumn(host, 1);
            body.Children.Add(host);
            root.Children.Add(body);

            // 通知层盖在最上面（不占布局）
            root.Children.Add(notices);

            /**
             * 首次运行引导（只在第一次自动弹出）。
             *
             * 为什么需要它：这个软件的用法不是"打开就能看懂"——
             * 用户要先明白「选一段动画 → 它会在下次开机登录时播放」，
             * 而"开机时先露出桌面"这类问题的成因（原生封面窗口/缓存/启动项）
             * 对新用户完全不可见。与其让用户去翻文档或来问，
             * 不如第一次打开就把三件最要紧的事说清楚。
             *
             * 放在通知层之后 = 盖在应用之上；点「开始使用」才关闭。
             */
            welcomeLayer = BuildWelcome();
            root.Children.Add(welcomeLayer);

            Content = root;

            store.Changed += delegate { Refresh(); };

            Loaded += delegate
            {
                ResumeRuntime.Start();
                statusTimer.Interval = TimeSpan.FromSeconds(3);
                statusTimer.Tick += delegate { UpdateSidebarStatus(); };
                statusTimer.Start();
                LoadLibraryAsync();

                // 首次运行才显示引导，之后不再打扰
                if (Program.IsFirstRun())
                {
                    if (welcomeLayer != null) welcomeLayer.Visibility = Visibility.Visible;
                }
            };
            Closed += delegate
            {
                ResumeRuntime.Stop();
                if (hero != null) hero.Release();
                statusTimer.Stop();
            };
        }

        internal void NotifyResumed(DateTime at)
        {
            resumeNote = "最近唤醒 " + at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            if (current == Page.System || current == Page.Startup) Refresh();
            UpdateSidebarStatus();
        }

        // ══════════════════════════════════════════════════ 页面背景（Wallpaper Driven）

        /// <summary>
        /// 首次运行引导。
        ///
        /// 只讲四件"新用户不知道就会踩坑"的事，不做功能罗列：
        ///   1. 这个软件做什么（选一段动画，下次开机登录时播放）
        ///   2. 默认已经是真全屏（拉伸填满），不需要调
        ///   3. 开机时它会自己接管（不先露桌面），播完自动进桌面
        ///   4. 同一次开机只播一次
        ///
        /// 另外给两个入口：跑一次环境自检、打开数据目录 —— 这两件事新用户最可能马上需要。
        /// 刻意保持简短：引导越长越像说明书，用户越会直接关掉。
        /// </summary>
        private Grid BuildWelcome()
        {
            Grid layer = new Grid();
            // 用现成的重遮罩画刷（已 Freeze）。引导要完全压住下面的界面，
            // 所以用最重的那一层，而不是自己拼一个半透明色。
            layer.Background = Theme.OverlayHeavy;
            layer.Visibility = Visibility.Hidden;   // 由 Loaded 决定是否显示

            StackPanel col = Ds.Col(Theme.S5);
            col.MaxWidth = 620;
            col.HorizontalAlignment = HorizontalAlignment.Center;
            col.VerticalAlignment = VerticalAlignment.Center;

            col.Children.Add(Ds.HeroTitle("开机动画"));

            TextBlock lede = Ds.Text(
                "选一段动画，它会在你下次开机登录完成的瞬间铺满屏幕播放一遍，播完自动进入桌面。",
                Theme.TBody, Theme.Fg2, FontWeights.Normal);
            lede.TextWrapping = TextWrapping.Wrap;
            lede.Margin = new Thickness(0, Theme.S2, 0, Theme.S4);
            col.Children.Add(lede);

            col.Children.Add(Ds.Divider(0, Theme.S4));

            col.Children.Add(WelcomePoint("1", "在「动画库」选片头",
                "内嵌素材、你自己的视频文件、社区下载的动画都可以。选好后按「应用」。"));
            col.Children.Add(WelcomePoint("2", "画面默认就是真全屏",
                "拟合方式是拉伸填满，不会出现上下空缺。想改成裁切填满或整帧显示，在首页或设置里切换。"));
            col.Children.Add(WelcomePoint("3", "开机时它会自己接管",
                "登录界面一消失就出现，不会先露出桌面；播完直接进桌面，不需要点击。如果没做到，去「诊断」页看环境自检。"));
            col.Children.Add(WelcomePoint("4", "同一次开机只播一次",
                "不会重复播放。想再看可以随时在首页点预览。"));

            StackPanel actions = Ds.Bar(Theme.S3);
            actions.Margin = new Thickness(0, Theme.S5, 0, 0);
            actions.HorizontalAlignment = HorizontalAlignment.Left;

            actions.Children.Add(Ds.Action("开始使用", ActionLevel.Primary, delegate
            {
                Program.MarkWelcomeSeen();
                if (welcomeLayer != null) welcomeLayer.Visibility = Visibility.Hidden;
            }));

            actions.Children.Add(Ds.Action("运行环境自检", ActionLevel.Secondary, delegate
            {
                Program.MarkWelcomeSeen();
                if (welcomeLayer != null) welcomeLayer.Visibility = Visibility.Hidden;
                current = Page.Diagnostics;
                Refresh();
            }));

            actions.Children.Add(Ds.Action("打开数据目录", ActionLevel.Tertiary, delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", Program.DataDirectory); }
                catch (Exception ex) { notices.Show("无法打开", ex.Message, NoticeLevel.Error, null); }
            }));

            col.Children.Add(actions);

            TextBlock hint = Ds.Text(
                "这个引导只出现一次。以后想再看，删掉数据目录里的 welcomed.txt 即可。",
                Theme.TMicro, Theme.Fg3, FontWeights.Normal);
            hint.Margin = new Thickness(0, Theme.S4, 0, 0);
            col.Children.Add(hint);

            Border card = new Border();
            card.Background = Theme.Card;
            card.BorderBrush = Theme.LineStrong;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RDialog);
            card.Padding = new Thickness(Theme.S8);
            card.Child = col;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;

            layer.Children.Add(card);
            return layer;
        }

        /// <summary>引导里的一条：序号 + 标题 + 一句话说明。</summary>
        private UIElement WelcomePoint(string index, string title, string detail)
        {
            Grid row = new Grid();
            ColumnDefinition numCol = new ColumnDefinition();
            numCol.Width = new GridLength(34);
            ColumnDefinition textCol = new ColumnDefinition();
            textCol.Width = new GridLength(1, GridUnitType.Star);
            row.ColumnDefinitions.Add(numCol);
            row.ColumnDefinitions.Add(textCol);
            row.Margin = new Thickness(0, 0, 0, Theme.S4);

            TextBlock num = Ds.Text(index, Theme.TMicro, Theme.Fg3, FontWeights.SemiBold);
            num.VerticalAlignment = VerticalAlignment.Top;
            num.Margin = new Thickness(0, 3, 0, 0);
            Grid.SetColumn(num, 0);
            row.Children.Add(num);

            StackPanel text = Ds.Col(3);
            text.Children.Add(Ds.Text(title, Theme.TBody, Theme.Fg, FontWeights.SemiBold));
            TextBlock d = Ds.Text(detail, Theme.TMeta, Theme.Fg3, FontWeights.Normal);
            d.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(d);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            return row;
        }
        /// <summary>
        /// 三层背景：当前动画 Artwork（放大模糊）→ 暗化 → 晕影。
        ///
        /// 这是"Wallpaper Driven UI"的落地。关键约束是**背景绝不能影响可读性**，
        /// 所以暗化层是不透明的黑色渐变而不是半透明，晕影再压一次四周 ——
        /// 三件一起做，文字对比度才有保证。没有 Artwork 时整层退化为纯色底。
        /// </summary>
        private Grid BuildBackdrop()
        {
            Grid layer = new Grid();
            layer.Background = Theme.Bg;

            backdropImage.Stretch = Stretch.UniformToFill;
            backdropImage.Opacity = 0.28;
            BlurEffect blur = new BlurEffect();
            blur.Radius = 32;
            blur.KernelType = KernelType.Gaussian;
            backdropImage.Effect = blur;
            layer.Children.Add(backdropImage);

            // 暗化：上深下更深的纵向渐变，保证底部状态区也读得清
            LinearGradientBrush dim = new LinearGradientBrush();
            dim.StartPoint = new Point(0, 0);
            dim.EndPoint = new Point(0, 1);
            dim.GradientStops.Add(new GradientStop(Color.FromArgb(0xd8, 0x09, 0x0a, 0x0c), 0.0));
            dim.GradientStops.Add(new GradientStop(Color.FromArgb(0xe8, 0x09, 0x0a, 0x0c), 0.55));
            dim.GradientStops.Add(new GradientStop(Color.FromArgb(0xf5, 0x09, 0x0a, 0x0c), 1.0));
            backdropDim.Background = dim;
            layer.Children.Add(backdropDim);

            // 晕影：四周再压暗一档，把视线收到中间
            RadialGradientBrush vig = new RadialGradientBrush();
            vig.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.45));
            vig.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0, 0, 0), 1.0));
            backdropVignette.Background = vig;
            layer.Children.Add(backdropVignette);

            return layer;
        }

        private void UpdateBackdrop()
        {
            AnimationInfo theme = store.Preview ?? store.Active;
            Accent.Use(theme);
            ImageBrush brush = PreviewEngine.PosterBrushFor(theme);
            backdropImage.Source = brush == null ? null : brush.ImageSource;
        }

        // ══════════════════════════════════════════════════ 侧栏

        private Border BuildSidebar()
        {
            Grid col = new Grid();
            RowDefinition top = new RowDefinition();
            top.Height = GridLength.Auto;
            RowDefinition mid = new RowDefinition();
            mid.Height = new GridLength(1, GridUnitType.Star);
            RowDefinition foot = new RowDefinition();
            foot.Height = GridLength.Auto;
            col.RowDefinitions.Add(top);
            col.RowDefinitions.Add(mid);
            col.RowDefinitions.Add(foot);

            // ── 品牌区：中文主标题 + 英文小标签
            StackPanel brand = Ds.Col(0);
            brand.Margin = new Thickness(Theme.S5, Theme.S6, Theme.S5, Theme.S6);
            brand.Children.Add(Ds.Text("开机动画", 17, Theme.Fg, FontWeights.SemiBold));
            TextBlock en = Ds.Label("Boot Animation Platform", Theme.Fg3);
            en.Margin = new Thickness(0, 5, 0, 0);
            brand.Children.Add(en);
            Grid.SetRow(brand, 0);
            col.Children.Add(brand);

            // ── 导航：中文 + 英文小标签，Active 用极克制的强调色底 + 左侧细线
            StackPanel list = Ds.Col(0);
            list.Margin = new Thickness(Theme.S3, 0, Theme.S3, 0);
            AddNavGroup(list, "HOME", new Page[] { Page.Home, Page.Library, Page.Community });
            AddNavGroup(list, "SYSTEM", new Page[] { Page.Startup, Page.System, Page.Diagnostics });
            AddNavGroup(list, "APP", new Page[] { Page.Settings, Page.About });
            Grid.SetRow(list, 1);
            col.Children.Add(list);

            // ── 侧栏底部：两个真实状态（不堆全部状态，细节在系统状态页）
            StackPanel footPanel = Ds.Col(Theme.S2);
            footPanel.Margin = new Thickness(Theme.S5, Theme.S4, Theme.S5, Theme.S5);

            TextBlock head = Ds.Label("System Status", Theme.Fg3);
            footPanel.Children.Add(head);

            sidebarRuntime = Ds.Text("", Theme.TMicro, Theme.Fg2, FontWeights.Normal);
            sidebarStartup = Ds.Text("", Theme.TMicro, Theme.Fg2, FontWeights.Normal);
            footPanel.Children.Add(sidebarRuntime);
            footPanel.Children.Add(sidebarStartup);

            sidebarStatus.Margin = new Thickness(0, Theme.S2, 0, 0);
            sidebarStatus.TextWrapping = TextWrapping.Wrap;
            footPanel.Children.Add(sidebarStatus);

            Grid.SetRow(footPanel, 2);
            col.Children.Add(footPanel);

            Border side = new Border();
            side.Background = Theme.Bg2;
            side.BorderBrush = Theme.Line;
            side.BorderThickness = new Thickness(0, 0, 1, 0);
            side.Child = col;
            Grid.SetColumn(side, 0);
            return side;
        }

        private TextBlock sidebarRuntime;
        private TextBlock sidebarStartup;

        private void AddNavGroup(StackPanel parent, string label, Page[] pages)
        {
            TextBlock g = Ds.Label(label, Theme.Fg3);
            g.Margin = new Thickness(Theme.S2, Theme.S5, 0, Theme.S2);
            parent.Children.Add(g);
            for (int i = 0; i < pages.Length; i++)
            {
                parent.Children.Add(BuildNavItem(pages[i]));
            }
        }

        /// <summary>
        /// 一个导航项：左侧 2px 细线（Active 时点亮）+ 中文主标 + 英文小标。
        /// **没有边框** —— 逐项描边正是"像后台程序"的典型特征。
        /// </summary>
        private Border BuildNavItem(Page page)
        {
            Grid row = new Grid();
            ColumnDefinition markerCol = new ColumnDefinition();
            markerCol.Width = new GridLength(2);
            ColumnDefinition textCol = new ColumnDefinition();
            textCol.Width = new GridLength(1, GridUnitType.Star);
            row.ColumnDefinitions.Add(markerCol);
            row.ColumnDefinitions.Add(textCol);

            Border marker = new Border();
            marker.Background = Brushes.Transparent;
            marker.CornerRadius = new CornerRadius(1);
            marker.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            StackPanel texts = Ds.Col(0);
            texts.Margin = new Thickness(Theme.S3, Theme.S2 + 1, Theme.S2, Theme.S2 + 1);
            TextBlock cn = Ds.Text(PageTitleCn(page), Theme.TBody, Theme.Fg2, FontWeights.Normal);
            TextBlock en = Ds.Label(PageTitleEn(page), Theme.Fg3);
            en.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(cn);
            texts.Children.Add(en);
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);

            Border box = new Border();
            box.CornerRadius = new CornerRadius(Theme.RButton);
            box.Background = Brushes.Transparent;
            box.Margin = new Thickness(0, 1, 0, 1);
            box.Child = row;
            box.Cursor = Cursors.Hand;

            NavEntry entry = new NavEntry();
            entry.Page = page;
            entry.Box = box;
            entry.Cn = cn;
            entry.En = en;
            entry.Marker = marker;
            navEntries.Add(entry);

            Page captured = page;
            box.MouseLeftButtonUp += delegate { Go(captured); };
            box.MouseEnter += delegate { if (current != captured) box.Background = Theme.Raise; };
            box.MouseLeave += delegate { if (current != captured) box.Background = Brushes.Transparent; };
            return box;
        }

        private static string PageTitleCn(Page p)
        {
            if (p == Page.Home) return "首页";
            if (p == Page.Library) return "动画库";
            if (p == Page.Community) return "社区";
            if (p == Page.Startup) return "启动项";
            if (p == Page.System) return "系统状态";
            if (p == Page.Diagnostics) return "诊断";
            if (p == Page.Settings) return "设置";
            return "关于";
        }

        private static string PageTitleEn(Page p)
        {
            if (p == Page.Home) return "Home";
            if (p == Page.Library) return "Library";
            if (p == Page.Community) return "Community";
            if (p == Page.Startup) return "Startup";
            if (p == Page.System) return "System";
            if (p == Page.Diagnostics) return "Diagnostics";
            if (p == Page.Settings) return "Settings";
            return "About";
        }

        private void Go(Page page)
        {
            if (current == page) return;
            current = page;
            UpdateNavVisual();
            Refresh();
        }

        private void UpdateNavVisual()
        {
            for (int i = 0; i < navEntries.Count; i++)
            {
                NavEntry e = navEntries[i];
                bool on = e.Page == current;
                e.Box.Background = on ? Accent.Dim : Brushes.Transparent;
                e.Marker.Background = on ? Accent.Brush : Brushes.Transparent;
                e.Cn.Foreground = on ? Theme.Fg : Theme.Fg2;
                e.Cn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                e.En.Foreground = on ? Theme.Fg2 : Theme.Fg3;
            }
        }

        private void UpdateSidebarStatus()
        {
            if (sidebarRuntime == null) return;
            bool runtimeOk = store.Active != null && store.Active.IsPlayable;
            sidebarRuntime.Text = (runtimeOk ? "● " : "▲ ") + "Runtime "
                + (runtimeOk ? "Ready" : "Not ready");

            bool startupOk = lastReport != null && !HasLevel(lastReport.Startup, StatusLevel.Bad);
            sidebarStartup.Text = (startupOk ? "● " : "▲ ") + "Startup "
                + (startupOk ? "Enabled" : "Needs repair");

            sidebarRuntime.Foreground = runtimeOk ? Theme.Fg2 : Theme.Warn;
            sidebarStartup.Foreground = startupOk ? Theme.Fg2 : Theme.Warn;

            AnimationInfo active = store.Active;
            sidebarStatus.Text = resumeNote.Length > 0
                ? resumeNote
                : (store.StatusText.Length > 0 ? store.StatusText : "");
            sidebarStatus.Foreground = store.StatusTone == "bad" ? Theme.Danger : Theme.Fg3;
        }

        private static bool HasLevel(List<StatusItem> items, StatusLevel level)
        {
            for (int i = 0; i < items.Count; i++) { if (items[i].Level == level) return true; }
            return false;
        }

        // ══════════════════════════════════════════════════ 数据

        private void LoadLibraryAsync()
        {
            store.SetStatus("正在扫描本地动画…", "");
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                List<AnimationInfo> list = null;
                string error = null;
                try { list = AnimationRepository.ScanLocal(); }
                catch (Exception ex) { error = ex.Message; }

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate()
                {
                    if (list != null)
                    {
                        store.ReplaceAll(list);
                        string chosen = Program.ReadChosen();
                        if (!string.IsNullOrEmpty(chosen)) store.SyncActive(chosen);
                        store.SetStatus("", "");
                        if (lastReport == null) lastReport = Diagnostics.Run(store);
                    }
                    else
                    {
                        store.SetStatus("扫描动画失败：" + error, "bad");
                        notices.Show("扫描失败", "无法读取本地动画：" + error, NoticeLevel.Error,
                            new NoticeAction[] { new NoticeAction("重试", ActionLevel.Secondary,
                                delegate { LoadLibraryAsync(); }) });
                    }
                    Refresh();
                }));
            });
            worker.IsBackground = true;
            worker.Name = "ba-scan";
            worker.Start();
        }

        // ══════════════════════════════════════════════════ 渲染

        private void Refresh()
        {
            UpdateNavVisual();
            UpdateBackdrop();
            UpdateSidebarStatus();

            UIElement page;
            if (current == Page.Home) page = BuildHome();
            else if (current == Page.Library) page = BuildLibrary();
            else if (current == Page.Community) page = BuildCommunity();
            else if (current == Page.System) page = BuildSystemStatus();
            else if (current == Page.Startup) page = BuildStartup();
            else if (current == Page.Diagnostics) page = BuildDiagnostics();
            else if (current == Page.Settings) page = BuildSettings();
            else page = BuildAbout();

            // 页面切换：淡入 + 极轻的向上位移（需求允许"Fade + slight translate"）
            host.Content = page;
            TranslateTransform slide = new TranslateTransform(0, 8);
            host.RenderTransform = slide;
            Ds.Fade(host, 1.0, Theme.AnimSlow);
            Ds.SlideTo(slide, 0, Theme.AnimSlow);
        }

        // ══════════════════════════════════════════════════ 首页

        private UIElement BuildHome()
        {
            AnimationInfo target = store.Preview;
            if (target == null) target = store.Active;
            if (target == null && store.Count > 0) target = store.Items[0];

            StackPanel page = Ds.Col(0);

            // ── 1. Hero Media：页面绝对视觉核心
            page.Children.Add(BuildHero(target));

            // ── 2. 媒体详情区（名称 / 作者 / 状态 / 元信息 + 主操作）
            page.Children.Add(BuildHeroDetails(target));

            // ── 3. 系统状态区（退到下方，去卡片化）
            page.Children.Add(Ds.Divider(Theme.S7, Theme.S7));
            page.Children.Add(BuildSystemSection());

            page.Children.Add(Ds.Divider(Theme.S7, Theme.S7));
            page.Children.Add(BuildStartupSection());

            // ── 4. 已安装动画（横向条）
            page.Children.Add(Ds.Divider(Theme.S7, Theme.S7));
            page.Children.Add(BuildInstalledStrip());

            return Ds.Page(page);
        }

        /// <summary>
        /// Hero：当前动画成为整个页面的视觉主体。
        ///
        /// 三条与"普通 Video 组件"的区别：
        ///   · **没有卡片边框** —— 画面直接坐在背景氛围上，靠一个很淡的渐变遮罩收边
        ///   · **控件不常驻** —— 鼠标进入才淡入；离开即淡出
        ///   · **控件是小型 icon + 极细时间轴**，不是一排等宽大按钮
        /// </summary>
        private Border BuildHero(AnimationInfo target)
        {
            Border frame = new Border();
            frame.Background = Brushes.Black;
            frame.CornerRadius = new CornerRadius(Theme.RCard);
            frame.ClipToBounds = true;

            // 高度随窗口自适应：需求 500–650，小窗口给 420 保底
            double h = ActualHeight * 0.48;
            if (h < 420) h = 420;
            if (h > 650) h = 650;
            frame.Height = h;

            Grid stack = new Grid();

            hero = new MediaPlayerBox();
            hero.SetPoster(PreviewEngine.PosterBrushFor(target));
            if (target != null && target.IsPlayable)
            {
                hero.Load(target.Path);
                hero.SetMuted(heroMuted);
                hero.SetFit(heroFit, ActualWidth, ActualHeight);
            }
            else
            {
                hero.ShowPlaceholder(target == null
                    ? "还没有选中任何动画 —— 去「动画库」挑一段。"
                    : "「" + AnimationStateStore.DisplayName(target) + "」还没有本地文件，去「社区」页下载它。");
            }
            stack.Children.Add(hero);

            // ── 悬停控制层：底部渐变 + 小型控件
            Grid controlsLayer = new Grid();
            controlsLayer.VerticalAlignment = VerticalAlignment.Bottom;

            Border scrim = new Border();
            scrim.Height = 96;
            scrim.VerticalAlignment = VerticalAlignment.Bottom;
            LinearGradientBrush grad = new LinearGradientBrush();
            grad.StartPoint = new Point(0, 0);
            grad.EndPoint = new Point(0, 1);
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0xb8, 0, 0, 0), 1.0));
            scrim.Background = grad;
            controlsLayer.Children.Add(scrim);

            StackPanel bar = Ds.Col(Theme.S2);
            bar.Margin = new Thickness(Theme.S4, 0, Theme.S4, Theme.S3);

            // 极细时间轴
            Border track = new Border();
            track.Height = 3;
            track.CornerRadius = new CornerRadius(2);
            track.Background = Theme.LineStrong;
            timelineFill = new Border();
            timelineFill.Height = 3;
            timelineFill.CornerRadius = new CornerRadius(2);
            timelineFill.Background = Accent.Brush;
            timelineFill.HorizontalAlignment = HorizontalAlignment.Left;
            timelineFill.Width = 0;
            Grid trackHolder = new Grid();
            trackHolder.Children.Add(track);
            trackHolder.Children.Add(timelineFill);
            trackHolder.Cursor = Cursors.Hand;
            trackHolder.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                SeekHero(e.GetPosition(trackHolder).X, trackHolder.ActualWidth);
            };
            bar.Children.Add(trackHolder);

            StackPanel row = Ds.Bar(Theme.S2);
            row.Children.Add(Ds.IconAction("▶", "播放", delegate { hero.Play(); }));
            row.Children.Add(Ds.IconAction("⏸", "暂停", delegate { hero.Pause(); }));
            row.Children.Add(Ds.IconAction("↺", "重新播放", delegate { hero.Restart(); }));
            row.Children.Add(Ds.IconAction(heroMuted ? "🔇" : "🔊", "静音 / 取消静音", delegate
            {
                heroMuted = !heroMuted;
                hero.SetMuted(heroMuted);
                Refresh();
            }));
            row.Children.Add(Ds.IconAction("⛶", "全屏媒体预览（Cinema）", delegate
            {
                OpenPreview(target, PreviewLayer.Media);
            }));

            heroTime = Ds.Text("00:00 / 00:00", Theme.TMicro, Theme.Fg2, FontWeights.Normal);
            heroTime.VerticalAlignment = VerticalAlignment.Center;
            heroTime.Margin = new Thickness(Theme.S2, 0, Theme.S2, 0);
            row.Children.Add(heroTime);

            // Preview Boot 是**独立的主要操作**，不与播放控制混在一起 —— 它单独占右边
            StackPanel right = Ds.Bar(Theme.S2);
            right.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(Ds.Action("Preview Boot", ActionLevel.Primary, delegate
            {
                OpenPreview(target, PreviewLayer.BootScreen);
            }));

            Grid barGrid = new Grid();
            barGrid.Children.Add(row);
            barGrid.Children.Add(right);
            bar.Children.Add(barGrid);
            controlsLayer.Children.Add(bar);

            heroControls = new Border();
            heroControls.Child = controlsLayer;
            // 默认**完全不显示**（包含不占命中测试）：开机屏幕上没有播放器控件，
            // 所以 Hero 在鼠标进入之前应该是干净的。只把 Opacity 设 0 是不够的 ——
            // 透明但仍在的元素会吃掉鼠标事件，也可能被 RenderTargetBitmap 抓到。
            heroControls.Opacity = 0;
            heroControls.Visibility = Visibility.Collapsed;
            heroControls.Background = Brushes.Transparent;
            stack.Children.Add(heroControls);

            frame.Child = stack;

            // 悬停才显示控件
            frame.MouseEnter += delegate { heroControls.Visibility = Visibility.Visible; Ds.Fade(heroControls, 1.0, Theme.AnimNormal); };
            frame.MouseLeave += delegate { Ds.Fade(heroControls, 0.0, Theme.AnimFast); heroControls.Visibility = Visibility.Collapsed; };

            StartHeroClock();
            return frame;
        }

        private Border timelineFill;
        private TextBlock heroTime;
        private DispatcherTimer heroClock;

        private void StartHeroClock()
        {
            if (heroClock != null) { heroClock.Stop(); }
            heroClock = new DispatcherTimer();
            heroClock.Interval = TimeSpan.FromMilliseconds(140);
            heroClock.Tick += delegate { UpdateHeroClock(); };
            heroClock.Start();
        }

        private void UpdateHeroClock()
        {
            try
            {
                if (hero == null || heroTime == null) return;
                MediaElement m = hero.Element;
                if (m.NaturalDuration.HasTimeSpan)
                {
                    TimeSpan len = m.NaturalDuration.TimeSpan;
                    heroTime.Text = Mmss(m.Position) + " / " + Mmss(len);
                    double frac = len.TotalMilliseconds > 0 ? m.Position.TotalMilliseconds / len.TotalMilliseconds : 0;
                    if (frac < 0) frac = 0;
                    if (frac > 1) frac = 1;
                    double w = timelineFill != null ? timelineFill.ActualWidth : 0;
                    if (timelineFill != null && w >= 0) timelineFill.Width = Math.Max(0, TrackWidth() * frac);
                }
                else
                {
                    heroTime.Text = "--:-- / --:--";
                }
            }
            catch { }
        }

        private double TrackWidth()
        {
            // 时间轴宽度 = Hero 宽度 - 左右内边距
            double w = ActualWidth - Theme.NavW - Theme.S7 * 2 - Theme.S4 * 2;
            return w > 0 ? w : 0;
        }

        private static string Mmss(TimeSpan t)
        {
            return t.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":"
                + t.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private void SeekHero(double x, double width)
        {
            if (width <= 0 || hero == null) return;
            try
            {
                MediaElement m = hero.Element;
                if (!m.NaturalDuration.HasTimeSpan) return;
                double frac = x / width;
                if (frac < 0) frac = 0;
                if (frac > 1) frac = 1;
                m.Position = TimeSpan.FromMilliseconds(m.NaturalDuration.TimeSpan.TotalMilliseconds * frac);
            }
            catch { }
        }

        /// <summary>
        /// 媒体详情区：名称（最大字）+ 作者 + ACTIVE + 元信息，右侧是分主次的操作。
        ///
        /// Apply 的文案随真实状态变化（未应用 / 已应用 / 安装中 / 需修复），
        /// 不再是"一个白色大按钮点了不知道会发生什么"。
        /// </summary>
        private UIElement BuildHeroDetails(AnimationInfo target)
        {
            Grid grid = new Grid();
            ColumnDefinition left = new ColumnDefinition();
            left.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition right = new ColumnDefinition();
            right.Width = GridLength.Auto;
            grid.ColumnDefinitions.Add(left);
            grid.ColumnDefinitions.Add(right);
            grid.Margin = new Thickness(0, Theme.S6, 0, 0);

            StackPanel info = Ds.Col(0);

            // 标题行：名称 + 状态徽章（同一行，徽章紧跟名称）
            StackPanel titleRow = Ds.Bar(Theme.S3);
            TextBlock title = Ds.HeroTitle(target == null ? "未选择动画" : AnimationStateStore.DisplayName(target));
            titleRow.Children.Add(title);
            if (target != null)
            {
                if (string.Equals(target.Id, store.ActiveId, StringComparison.OrdinalIgnoreCase))
                {
                    Border badge = Ds.BadgeSolid("Active");
                    badge.VerticalAlignment = VerticalAlignment.Center;
                    titleRow.Children.Add(badge);
                }
                else if (target.IsInstalled)
                {
                    Border badge = Ds.Badge("Installed", Theme.Fg3);
                    badge.VerticalAlignment = VerticalAlignment.Center;
                    titleRow.Children.Add(badge);
                }
                else
                {
                    Border badge = Ds.Badge("Not installed", Theme.Warn);
                    badge.VerticalAlignment = VerticalAlignment.Center;
                    titleRow.Children.Add(badge);
                }
            }
            info.Children.Add(titleRow);

            if (target != null && !string.IsNullOrEmpty(target.Author))
            {
                TextBlock by = Ds.Text(target.Author, Theme.TBody, Theme.Fg2, FontWeights.Normal);
                by.Margin = new Thickness(0, Theme.S2, 0, 0);
                info.Children.Add(by);
            }

            // 元信息：大字值 + 小标签，横向排。拿不到的项**不显示**。
            if (target != null)
            {
                StackPanel meta = Ds.MetaRow(
                    Ds.MetaField("分辨率", target.ResolutionText),
                    Ds.MetaField("FPS", target.FpsText),
                    Ds.MetaField("时长", target.DurationText),
                    Ds.MetaField("体积", target.SizeText),
                    Ds.MetaField("来源", SourceLabel(target.Source)));
                meta.Margin = new Thickness(0, Theme.S5, 0, 0);
                info.Children.Add(meta);
            }

            if (store.StatusText.Length > 0)
            {
                TextBlock st = Ds.Text(store.StatusText, Theme.TMeta,
                    store.StatusTone == "bad" ? Theme.Danger : (store.StatusTone == "ok" ? Theme.Ok : Theme.Fg3),
                    FontWeights.Normal);
                st.Margin = new Thickness(0, Theme.S4, 0, 0);
                info.Children.Add(st);
            }

            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            // ── 操作区：主 / 次 / 三级，大小明显不同
            StackPanel actions = Ds.Col(Theme.S3);
            actions.VerticalAlignment = VerticalAlignment.Top;
            actions.Margin = new Thickness(Theme.S7, 0, 0, 0);

            StackPanel topRow = Ds.Bar(Theme.S3);
            topRow.HorizontalAlignment = HorizontalAlignment.Right;

            // Preview Boot —— 唯一的 Primary
            topRow.Children.Add(Ds.Action("Preview Boot", ActionLevel.Primary, delegate
            {
                OpenPreview(target, PreviewLayer.BootScreen);
            }));

            // Apply —— Secondary，文案随真实状态
            string applyText = ApplyButtonText(target);
            bool applyEnabled = target != null && target.IsInstalled
                && !string.Equals(target.Id, store.ActiveId, StringComparison.OrdinalIgnoreCase);
            Button apply = Ds.Action(applyText, ActionLevel.Secondary, delegate
            {
                if (target == null) return;
                if (string.Equals(applyText, "Repair Animation", StringComparison.Ordinal))
                {
                    RepairAnimation(target);
                    return;
                }
                if (store.Apply(target.Id))
                {
                    PushRecent(target.Id);
                    Refresh();
                }
            });
            apply.IsEnabled = applyEnabled;
            if (applyEnabled)
            {
                ToolTipService.SetToolTip(apply, "把这一段设为下次登录时播放的动画");
            }
            topRow.Children.Add(apply);
            actions.Children.Add(topRow);

            StackPanel bottomRow = Ds.Bar(Theme.S2);
            bottomRow.HorizontalAlignment = HorizontalAlignment.Right;

            // Random —— 三级
            bottomRow.Children.Add(Ds.Action("🎲 Random", ActionLevel.Tertiary, delegate
            {
                Random rng = new Random(unchecked(Environment.TickCount * 31 + Guid.NewGuid().GetHashCode()));
                AnimationInfo pick = store.PickRandom(rng, true);
                if (pick != null)
                {
                    PushRecent(pick.Id);
                    Refresh();
                }
            }));

            // 移除：只在可移除时才出现（内置与远程条目不给这个入口）
            if (target != null && target.Source != AnimationSource.BuiltIn && target.Source != AnimationSource.Remote)
            {
                bottomRow.Children.Add(Ds.Action("Remove", ActionLevel.Danger, delegate
                {
                    RemoveAnimation(target);
                }));
            }
            actions.Children.Add(bottomRow);

            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            return grid;
        }

        private static string SourceLabel(AnimationSource s)
        {
            if (s == AnimationSource.BuiltIn) return "内置";
            if (s == AnimationSource.Community) return "社区下载";
            if (s == AnimationSource.Local) return "本地文件";
            return "社区目录";
        }

        /// <summary>
        /// Apply 按钮的文案由**真实状态**决定，而不是固定一句。
        /// 四种状态：未安装 / 已应用 / 可应用 / 需要修复。
        /// </summary>
        private string ApplyButtonText(AnimationInfo target)
        {
            if (target == null) return "Apply Animation";
            if (!target.IsPlayable) return "Install to use";
            if (target.Package == PackageState.Failed || target.Package == PackageState.Stale)
                return "Repair Animation";
            if (store.Busy) return "Installing…";
            if (string.Equals(target.Id, store.ActiveId, StringComparison.OrdinalIgnoreCase))
                return "Active Animation";
            return "Apply Animation";
        }

        private void RepairAnimation(AnimationInfo info)
        {
            notices.Show("Boot Runtime",
                "「" + AnimationStateStore.DisplayName(info) + "」的运行时资源校验未通过。"
                + "重新生成时会校验文件完整性并重建背景资源。",
                NoticeLevel.Warning,
                new NoticeAction[]
                {
                    new NoticeAction("Repair", ActionLevel.Primary, delegate { RebuildPackage(info); }),
                    new NoticeAction("Diagnostics", ActionLevel.Secondary, delegate
                    {
                        Go(Page.Diagnostics);
                        RunDiagnosticsNow();
                    }),
                });
        }

        private void RebuildPackage(AnimationInfo info)
        {
            store.SetBusy(true, "正在重建 " + AnimationStateStore.DisplayName(info) + " 的运行时资源…");
            Refresh();
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                string error = null;
                try
                {
                    // 资源准备 = 校验文件 + 重建氛围背景图（都在本地，不联网）
                    if (!info.IsPlayable) throw new Exception("本地文件不存在");
                    string sha = Program.Sha256Of(info.Path);
                    if (!string.IsNullOrEmpty(info.Sha256)
                        && !string.Equals(sha, info.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception("sha256 不符：文件已被改动或下载不完整");
                    }
                    string poster = Program.PosterCachePathFor(info.Id);
                    Program.BuildPosterInternal(info.Path, poster);
                }
                catch (Exception ex) { error = ex.Message; }

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate()
                {
                    store.SetBusy(false, null);
                    if (error == null)
                    {
                        store.SetStatus("运行时资源已重建：" + AnimationStateStore.DisplayName(info), "ok");
                        notices.Show("Boot Runtime", "资源已重建并校验通过，下次启动会直接使用它。",
                            NoticeLevel.Success, null);
                    }
                    else
                    {
                        store.SetStatus("重建失败：" + error, "bad");
                        notices.Show("Boot Runtime", "重建失败：" + error, NoticeLevel.Error,
                            new NoticeAction[]
                            {
                                new NoticeAction("Retry", ActionLevel.Secondary, delegate { RebuildPackage(info); }),
                                new NoticeAction("Diagnostics", ActionLevel.Secondary, delegate
                                {
                                    Go(Page.Diagnostics); RunDiagnosticsNow();
                                }),
                            });
                    }
                    LoadLibraryAsync();
                }));
            });
            worker.IsBackground = true;
            worker.Name = "ba-repair";
            worker.Start();
        }

        private void PushRecent(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            recentIds.Remove(id);
            recentIds.Insert(0, id);
            while (recentIds.Count > 12) recentIds.RemoveAt(recentIds.Count - 1);
        }

        // ══════════════════════════════════════════════════ 系统状态区（去卡片化）

        private UIElement BuildSystemSection()
        {
            StackPanel wrap = Ds.Col(0);
            wrap.Children.Add(Ds.Label("System Status", Theme.Fg3));
            wrap.Children.Add(Ds.Block(BuildStatusGroups(), Theme.S4, 0));

            StackPanel actions = Ds.Bar(Theme.S3);
            actions.Margin = new Thickness(0, Theme.S5, 0, 0);
            actions.Children.Add(Ds.Action("Run Diagnostics", ActionLevel.Tertiary, delegate
            {
                Go(Page.Diagnostics);
                RunDiagnosticsNow();
            }));
            if (lastReport != null)
            {
                TextBlock when = Ds.Text("Last check " + lastReport.RanAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    Theme.TMicro, Theme.Fg3, FontWeights.Normal);
                when.VerticalAlignment = VerticalAlignment.Center;
                actions.Children.Add(when);
            }
            wrap.Children.Add(actions);
            return wrap;
        }

        /// <summary>
        /// 状态分组：两列、无外框、靠对齐形成秩序。
        ///
        /// 原来是一整块黑色信息卡 —— 那会让技术信息在视觉上压过动画本身。
        /// 去掉框、缩小字号、用 12px 的稳定行距，它才退到"随时可查"的位置。
        /// </summary>
        private UIElement BuildStatusGroups()
        {
            DiagnosticsReport report = lastReport;
            if (report == null)
            {
                return Ds.State("正在检测…", "System Status 的每一项都来自真实探测。", null);
            }

            Grid grid = new Grid();
            ColumnDefinition a = new ColumnDefinition();
            a.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition b = new ColumnDefinition();
            b.Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions.Add(a);
            grid.ColumnDefinitions.Add(b);

            StackPanel left = Ds.Col(Theme.S5);
            /**
             * **环境自检放在最前面。**
             *
             * 用户的真实关切是"我这份装好没有、开机能不能一次到位"，
             * 而不是"Application / Animation 各自的状态"。
             * 所以把决定"会不会先露出桌面"的那几项（原生封面窗口、首帧图缓存、
             * 快速版、登录触发方式）单独成组、放在第一眼位置，
             * 每一条都直接写"缺什么 + 怎么补"，用户不用自己找。
             */
            left.Children.Add(BuildStatusGroup("环境自检（决定开机能不能一次到位）", report.SelfCheck));
            left.Children.Add(BuildStatusGroup("APPLICATION", report.Application));
            left.Children.Add(BuildStatusGroup("ANIMATION", report.Animation));

            StackPanel right = Ds.Col(Theme.S5);
            right.Children.Add(BuildStatusGroup("BOOT RUNTIME", report.BootRuntime));

            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            right.Margin = new Thickness(Theme.S7, 0, 0, 0);
            grid.Children.Add(left);
            grid.Children.Add(right);
            return grid;
        }

        private UIElement BuildStatusGroup(string heading, List<StatusItem> items)
        {
            StackPanel body = Ds.Col(0);
            for (int i = 0; i < items.Count; i++)
            {
                StatusItem it = items[i];
                body.Children.Add(Ds.StatusRow(it.Glyph(), it.Tone(), it.Label, it.Value, it.Detail));
            }
            return Ds.Group(heading, body);
        }

        private UIElement BuildStartupSection()
        {
            StackPanel wrap = Ds.Col(0);
            wrap.Children.Add(Ds.Label("Startup", Theme.Fg3));

            DiagnosticsReport report = lastReport;
            List<StatusItem> items = report == null ? new List<StatusItem>() : report.Startup;
            wrap.Children.Add(Ds.Block(BuildStatusGroup("WINDOWS STARTUP", items), Theme.S4, 0));

            StackPanel actions = Ds.Bar(Theme.S3);
            actions.Margin = new Thickness(0, Theme.S5, 0, 0);
            actions.Children.Add(Ds.Action("Repair Startup", ActionLevel.Secondary, delegate { RepairStartupNow(); }));
            actions.Children.Add(Ds.Action("Preview Resume Playback", ActionLevel.Tertiary, delegate
            {
                ResumeRuntime.PlayResumeNow();
                store.SetStatus("已启动一次唤醒播放（复用与开机完全相同的播放链路）", "ok");
                Refresh();
            }));
            wrap.Children.Add(actions);

            if (repairLog.Count > 0)
            {
                StackPanel log = Ds.Col(0);
                log.Margin = new Thickness(0, Theme.S4, 0, 0);
                for (int i = 0; i < repairLog.Count; i++)
                {
                    Diagnostics.RepairStep step = repairLog[i];
                    TextBlock line = Ds.Text((step.Ok ? "✓  " : "✕  ") + step.Text, Theme.TMicro,
                        step.Ok ? Theme.Fg2 : Theme.Danger, FontWeights.Normal);
                    line.Margin = new Thickness(0, 0, 0, 3);
                    log.Children.Add(line);
                }
                wrap.Children.Add(log);
            }
            return wrap;
        }

        private readonly List<Diagnostics.RepairStep> repairLog = new List<Diagnostics.RepairStep>();

        private void RepairStartupNow()
        {
            repairLog.Clear();
            try
            {
                List<Diagnostics.RepairStep> steps = Diagnostics.RepairStartup();
                repairLog.AddRange(steps);
                bool allOk = true;
                for (int i = 0; i < steps.Count; i++) { if (!steps[i].Ok) allOk = false; }
                store.SetStatus(allOk ? "启动项已修复并复验通过" : "修复完成，但有步骤失败（见下方清单）",
                    allOk ? "ok" : "warn");
                notices.Show("Startup", allOk
                        ? "启动项已重新注册并复验通过。"
                        : "修复执行完成，但有步骤未通过 —— 展开下方清单可以看每一步的结果。",
                    allOk ? NoticeLevel.Success : NoticeLevel.Warning,
                    new NoticeAction[]
                    {
                        new NoticeAction("Run Diagnostics", ActionLevel.Secondary,
                            delegate { Go(Page.Diagnostics); RunDiagnosticsNow(); }, true),
                    });
            }
            catch (Exception ex)
            {
                repairLog.Add(new Diagnostics.RepairStep("修复过程抛异常：" + ex.Message, false));
                store.SetStatus("修复失败：" + ex.Message, "bad");
                notices.Show("Startup", "修复失败：" + ex.Message, NoticeLevel.Error,
                    new NoticeAction[] { new NoticeAction("重试", ActionLevel.Secondary,
                        delegate { RepairStartupNow(); }) });
            }
            lastReport = Diagnostics.Run(store);
            Refresh();
        }

        private void RunDiagnosticsNow()
        {
            store.SetStatus("正在检测…", "");
            lastReport = Diagnostics.Run(store);
            store.SetStatus(lastReport.Summary,
                lastReport.SummaryLevel == StatusLevel.Ok ? "ok" : "warn");
            Refresh();
        }

        // ══════════════════════════════════════════════════ 已安装横条

        private UIElement BuildInstalledStrip()
        {
            StackPanel wrap = Ds.Col(0);
            wrap.Children.Add(Ds.Label("Installed Animations", Theme.Fg3));

            List<AnimationInfo> installed = new List<AnimationInfo>();
            List<AnimationInfo> all = store.Items;
            for (int i = 0; i < all.Count; i++) { if (all[i].IsInstalled) installed.Add(all[i]); }

            if (installed.Count == 0)
            {
                wrap.Children.Add(Ds.Block(Ds.State("还没有可播放的动画",
                    store.StatusText.Length > 0 ? store.StatusText : "正在扫描本地动画…", null), Theme.S4, 0));
                return wrap;
            }

            ScrollViewer scroller = new ScrollViewer();
            scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroller.Margin = new Thickness(0, Theme.S4, 0, 0);

            StackPanel row = Ds.Bar(Theme.S4);
            for (int i = 0; i < installed.Count; i++)
            {
                row.Children.Add(BuildStripTile(installed[i]));
            }
            scroller.Content = row;
            wrap.Children.Add(scroller);
            return wrap;
        }

        private Border BuildStripTile(AnimationInfo info)
        {
            bool isActive = string.Equals(info.Id, store.ActiveId, StringComparison.OrdinalIgnoreCase);
            bool isPreview = string.Equals(info.Id, store.PreviewId, StringComparison.OrdinalIgnoreCase);

            Border tile = new Border();
            tile.Width = 260;
            tile.Background = Theme.Card;
            tile.BorderBrush = isActive ? Accent.Brush : Theme.Line;
            tile.BorderThickness = new Thickness(isActive ? 2 : 1);
            tile.CornerRadius = new CornerRadius(Theme.RCard);
            tile.ClipToBounds = true;
            tile.Cursor = Cursors.Hand;

            StackPanel stack = Ds.Col(0);
            Border cover = new Border();
            cover.Height = 146;
            cover.Background = Brushes.Black;
            cover.ClipToBounds = true;
            cover.Child = CoverImage(info, Stretch.UniformToFill);
            stack.Children.Add(cover);

            StackPanel body = Ds.Col(0);
            body.Margin = new Thickness(Theme.S3, Theme.S3, Theme.S3, Theme.S3);
            body.Children.Add(Ds.Text(AnimationStateStore.DisplayName(info), Theme.TBody, Theme.Fg,
                isActive ? FontWeights.SemiBold : FontWeights.Normal));

            string metaLine = "";
            if (info.ResolutionText != null) metaLine = info.ResolutionText;
            if (info.DurationText != null) metaLine += (metaLine.Length > 0 ? " · " : "") + info.DurationText;
            if (metaLine.Length > 0)
            {
                TextBlock m = Ds.Text(metaLine, Theme.TMicro, Theme.Fg3, FontWeights.Normal);
                m.Margin = new Thickness(0, 3, 0, 0);
                body.Children.Add(m);
            }

            if (isActive)
            {
                Border b = Ds.BadgeSolid("Active");
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(0, Theme.S2, 0, 0);
                body.Children.Add(b);
            }
            stack.Children.Add(body);
            tile.Child = stack;

            AnimationInfo captured = info;
            tile.MouseLeftButtonUp += delegate { SelectAndPreview(captured); };
            tile.MouseEnter += delegate { tile.BorderBrush = Accent.Brush; };
            tile.MouseLeave += delegate { tile.BorderBrush = isActive ? Accent.Brush : Theme.Line; };
            return tile;
        }


        private static Image CoverImage(AnimationInfo info, Stretch stretch)
        {
            Image img = new Image();
            img.Stretch = stretch;
            ImageBrush brush = PreviewEngine.PosterBrushFor(info);
            if (brush != null) img.Source = brush.ImageSource;
            return img;
        }

        // ══════════════════════════════════════════════════ 动画库

        private UIElement BuildLibrary()
        {
            StackPanel page = Ds.Col(0);

            // 顶部：标题 + 筛选页签（真媒体库的骨架）
            Grid head = new Grid();
            ColumnDefinition hl = new ColumnDefinition();
            hl.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition hr = new ColumnDefinition();
            hr.Width = GridLength.Auto;
            head.ColumnDefinitions.Add(hl);
            head.ColumnDefinitions.Add(hr);

            StackPanel titles = Ds.Col(0);
            titles.Children.Add(Ds.PageTitle("动画库"));
            TextBlock sub = Ds.Text("点封面即可预览。Apply 才真正应用。", Theme.TMeta, Theme.Fg3, FontWeights.Normal);
            sub.Margin = new Thickness(0, Theme.S2, 0, 0);
            titles.Children.Add(sub);
            Grid.SetColumn(titles, 0);
            head.Children.Add(titles);

            StackPanel tabs = Ds.Bar(Theme.S1);
            tabs.VerticalAlignment = VerticalAlignment.Bottom;
            AddFilterTab(tabs, LibraryFilter.All, "全部");
            AddFilterTab(tabs, LibraryFilter.Installed, "已安装");
            AddFilterTab(tabs, LibraryFilter.Favorites, "收藏");
            AddFilterTab(tabs, LibraryFilter.Recent, "最近使用");
            Grid.SetColumn(tabs, 1);
            head.Children.Add(tabs);
            page.Children.Add(head);

            page.Children.Add(new Border { Height = Theme.S6 });

            List<AnimationInfo> items = FilteredItems();
            if (items.Count == 0)
            {
                page.Children.Add(Ds.State(EmptyTitle(),
                    "换个页签看看，或到「社区」页下载新的动画。", null));
                return Ds.Page(page);
            }

            // 大封面网格：按可用宽度决定列数（重排，不是缩放）
            int columns = ActualWidth >= 1700 ? 4 : (ActualWidth >= 1300 ? 3 : 2);
            double usable = ActualWidth - Theme.NavW - Theme.S7 * 2;
            double gap = Theme.S5;
            double cardW = (usable - (columns - 1) * gap) / columns;
            if (cardW < 260) cardW = 260;

            WrapPanel wrap = new WrapPanel();
            for (int i = 0; i < items.Count; i++)
            {
                Border card = BuildMediaCard(items[i], cardW);
                card.Margin = new Thickness(0, 0, gap, gap);
                wrap.Children.Add(card);
            }
            page.Children.Add(wrap);
            return Ds.Page(page);
        }

        private string EmptyTitle()
        {
            if (filter == LibraryFilter.Favorites) return "还没有收藏";
            if (filter == LibraryFilter.Recent) return "还没有最近使用记录";
            if (filter == LibraryFilter.Installed) return "还没有已安装的动画";
            return "还没有动画";
        }

        private void AddFilterTab(StackPanel parent, LibraryFilter which, string text)
        {
            bool on = filter == which;
            Button b = Ds.Action(text, ActionLevel.Tertiary, delegate
            {
                filter = which;
                Refresh();
            });
            b.FontSize = Theme.TMeta;
            if (on)
            {
                b.Foreground = Theme.Fg;
                b.FontWeight = FontWeights.SemiBold;
                b.Background = Theme.Raise;
            }
            parent.Children.Add(b);
        }

        private List<AnimationInfo> FilteredItems()
        {
            List<AnimationInfo> all = store.Items;
            List<AnimationInfo> outList = new List<AnimationInfo>();
            if (filter == LibraryFilter.All)
            {
                outList.AddRange(all);
                return outList;
            }
            if (filter == LibraryFilter.Installed)
            {
                for (int i = 0; i < all.Count; i++) { if (all[i].IsInstalled) outList.Add(all[i]); }
                return outList;
            }
            if (filter == LibraryFilter.Recent)
            {
                for (int r = 0; r < recentIds.Count; r++)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (string.Equals(all[i].Id, recentIds[r], StringComparison.OrdinalIgnoreCase))
                        {
                            outList.Add(all[i]);
                        }
                    }
                }
                return outList;
            }
            // Favorites：当前版本还没有收藏存储，如实返回空而不是伪造一批
            return outList;
        }

        /// <summary>
        /// 媒体卡片：大封面 + 名称 + 作者 + 元信息 + 状态。
        ///
        /// 悬停只做三件事（需求指定）：轻微放大 1.02、封面出现暗色 Overlay、
        /// 中央出现 Play Icon。**不在卡片上堆按钮** —— 那是 Dashboard 的做法，
        /// 媒体库的做法是"点进去看详情"。
        /// </summary>
        private Border BuildMediaCard(AnimationInfo info, double width)
        {
            Border card = new Border();
            card.Width = width;
            card.Background = Theme.Card;
            card.BorderBrush = Theme.Line;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RCard);
            card.ClipToBounds = true;
            card.Cursor = Cursors.Hand;

            StackPanel stack = Ds.Col(0);

            // 封面
            Border cover = new Border();
            cover.Background = Brushes.Black;
            cover.Height = width * 9.0 / 16.0;
            cover.ClipToBounds = true;

            Grid coverGrid = new Grid();
            coverGrid.RenderTransformOrigin = new Point(0.5, 0.5);
            ScaleTransform scale = new ScaleTransform(1.0, 1.0);
            coverGrid.RenderTransform = scale;
            coverGrid.Children.Add(CoverImage(info, Stretch.UniformToFill));

            Border hoverDim = new Border();
            hoverDim.Background = Theme.Overlay;
            hoverDim.Opacity = 0;
            coverGrid.Children.Add(hoverDim);

            Grid playWrap = new Grid();
            playWrap.Opacity = 0;
            Button play = new Button();
            play.Content = "▶";
            play.FontSize = 22;
            play.Width = 52;
            play.Height = 52;
            play.Foreground = Brushes.White;
            play.Background = Theme.OverlayHeavy;
            play.BorderBrush = Theme.LineStrong;
            play.BorderThickness = new Thickness(1);
            play.Cursor = Cursors.Hand;
            play.HorizontalAlignment = HorizontalAlignment.Center;
            play.VerticalAlignment = VerticalAlignment.Center;
            AnimationInfo captured = info;
            play.Click += delegate { SelectAndPreview(captured); };
            playWrap.Children.Add(play);
            coverGrid.Children.Add(playWrap);

            // 右下角 More：进入详情（点整卡同效，这里给一个明确的可点目标）
            Button more = new Button();
            more.Content = "⋯";
            more.FontSize = 16;
            more.Width = 30;
            more.Height = 30;
            more.Foreground = Brushes.White;
            more.Background = Theme.OverlayHeavy;
            more.BorderThickness = new Thickness(0);
            more.Cursor = Cursors.Hand;
            more.HorizontalAlignment = HorizontalAlignment.Right;
            more.VerticalAlignment = VerticalAlignment.Bottom;
            more.Margin = new Thickness(0, 0, Theme.S3, Theme.S3);
            more.Opacity = 0;
            more.Click += delegate { SelectAndPreview(captured); };
            coverGrid.Children.Add(more);

            cover.Child = coverGrid;
            stack.Children.Add(cover);

            card.MouseEnter += delegate
            {
                Ds.ScaleTo(scale, 1.02, Theme.AnimFast);
                Ds.Fade(hoverDim, 1.0, Theme.AnimFast);
                Ds.Fade(playWrap, 1.0, Theme.AnimFast);
                Ds.Fade(more, 1.0, Theme.AnimFast);
                card.BorderBrush = Theme.LineStrong;
            };
            card.MouseLeave += delegate
            {
                Ds.ScaleTo(scale, 1.0, Theme.AnimNormal);
                Ds.Fade(hoverDim, 0.0, Theme.AnimNormal);
                Ds.Fade(playWrap, 0.0, Theme.AnimNormal);
                Ds.Fade(more, 0.0, Theme.AnimNormal);
                card.BorderBrush = Theme.Line;
            };

            // 信息区
            StackPanel body = Ds.Col(0);
            body.Margin = new Thickness(Theme.S4, Theme.S4, Theme.S4, Theme.S4);

            Grid nameRow = new Grid();
            ColumnDefinition nc = new ColumnDefinition();
            nc.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition bc = new ColumnDefinition();
            bc.Width = GridLength.Auto;
            nameRow.ColumnDefinitions.Add(nc);
            nameRow.ColumnDefinitions.Add(bc);
            TextBlock name = Ds.Text(AnimationStateStore.DisplayName(info), Theme.TBody, Theme.Fg,
                FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(name, 0);
            nameRow.Children.Add(name);
            if (string.Equals(info.Id, store.ActiveId, StringComparison.OrdinalIgnoreCase))
            {
                Border activeDot = new Border();
                activeDot.Width = 7;
                activeDot.Height = 7;
                activeDot.CornerRadius = new CornerRadius(4);
                activeDot.Background = Accent.Brush;
                activeDot.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(activeDot, 1);
                nameRow.Children.Add(activeDot);
            }
            body.Children.Add(nameRow);

            if (!string.IsNullOrEmpty(info.Author))
            {
                TextBlock by = Ds.Text(info.Author, Theme.TMicro, Theme.Fg3, FontWeights.Normal);
                by.Margin = new Thickness(0, 3, 0, 0);
                body.Children.Add(by);
            }

            string metaLine = "";
            if (info.ResolutionText != null) metaLine = info.ResolutionText;
            if (info.DurationText != null) metaLine += (metaLine.Length > 0 ? "  ·  " : "") + info.DurationText;
            if (info.SizeText != null) metaLine += (metaLine.Length > 0 ? "  ·  " : "") + info.SizeText;
            if (metaLine.Length > 0)
            {
                TextBlock m = Ds.Text(metaLine, Theme.TMicro, Theme.Fg3, FontWeights.Normal);
                m.Margin = new Thickness(0, Theme.S2, 0, 0);
                body.Children.Add(m);
            }

            if (!info.IsInstalled)
            {
                Border b = Ds.Badge("Not installed", Theme.Warn);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(0, Theme.S3, 0, 0);
                body.Children.Add(b);
            }

            stack.Children.Add(body);
            card.Child = stack;
            return card;
        }

        // ══════════════════════════════════════════════════ 社区

        private List<AnimationInfo> communityCache;
        private TextBlock communityStatus;

        private UIElement BuildCommunity()
        {
            StackPanel page = Ds.Col(0);

            Grid head = new Grid();
            ColumnDefinition hl = new ColumnDefinition();
            hl.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition hr = new ColumnDefinition();
            hr.Width = GridLength.Auto;
            head.ColumnDefinitions.Add(hl);
            head.ColumnDefinitions.Add(hr);

            StackPanel titles = Ds.Col(0);
            titles.Children.Add(Ds.PageTitle("社区动画"));
            TextBlock sub = Ds.Text("来自社区目录。安装前先 Preview —— 避免下载完才发现不是想要的效果。",
                Theme.TMeta, Theme.Fg3, FontWeights.Normal);
            sub.Margin = new Thickness(0, Theme.S2, 0, 0);
            titles.Children.Add(sub);
            Grid.SetColumn(titles, 0);
            head.Children.Add(titles);

            StackPanel actions = Ds.Bar(Theme.S2);
            actions.VerticalAlignment = VerticalAlignment.Bottom;
            actions.Children.Add(Ds.Action("刷新目录", ActionLevel.Secondary, delegate { LoadCommunityAsync(); }));
            actions.Children.Add(Ds.Action("在浏览器打开", ActionLevel.Tertiary, delegate
            {
                try { Process.Start(Community.SiteUrl); } catch (Exception ex) { Program.Log(ex.Message); }
            }));
            Grid.SetColumn(actions, 1);
            head.Children.Add(actions);
            page.Children.Add(head);

            communityStatus = Ds.Text("", Theme.TMeta, Theme.Fg3, FontWeights.Normal);
            communityStatus.Margin = new Thickness(0, Theme.S3, 0, 0);
            page.Children.Add(communityStatus);

            page.Children.Add(new Border { Height = Theme.S6 });

            if (communityCache == null)
            {
                page.Children.Add(Ds.State("还没有拉取社区目录",
                    "点「刷新目录」从社区读取 index.json。这一步要联网，所以不会在开机时进行。", null));
                return Ds.Page(page);
            }
            if (communityCache.Count == 0)
            {
                page.Children.Add(Ds.State("目录为空或读取失败",
                    "社区功能不影响开机动画的播放；检查网络后重试。", Theme.Warn));
                return Ds.Page(page);
            }

            int columns = ActualWidth >= 1700 ? 4 : (ActualWidth >= 1300 ? 3 : 2);
            double usable = ActualWidth - Theme.NavW - Theme.S7 * 2;
            double gap = Theme.S5;
            double cardW = (usable - (columns - 1) * gap) / columns;
            if (cardW < 260) cardW = 260;

            WrapPanel wrap = new WrapPanel();
            for (int i = 0; i < communityCache.Count; i++)
            {
                Border card = BuildCommunityCard(communityCache[i], cardW);
                card.Margin = new Thickness(0, 0, gap, gap);
                wrap.Children.Add(card);
            }
            page.Children.Add(wrap);
            return Ds.Page(page);
        }

        /// <summary>社区卡片与媒体库卡片**同一套视觉语言**（需求明确要求统一）。</summary>
        private Border BuildCommunityCard(AnimationInfo info, double width)
        {
            Border card = new Border();
            card.Width = width;
            card.Background = Theme.Card;
            card.BorderBrush = Theme.Line;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RCard);
            card.ClipToBounds = true;

            StackPanel stack = Ds.Col(0);

            Border cover = new Border();
            cover.Background = Brushes.Black;
            cover.Height = width * 9.0 / 16.0;
            cover.ClipToBounds = true;
            Image img = new Image();
            img.Stretch = Stretch.UniformToFill;
            if (!string.IsNullOrEmpty(info.RemotePosterUrl))
            {
                try { img.Source = new BitmapImage(new Uri(info.RemotePosterUrl, UriKind.Absolute)); }
                catch { /* 海报取不到就留黑底，不影响下面的操作 */ }
            }
            // 已安装的社区条目在封面上直接标出来，避免用户重复下载。
            //
            // 注意：一个 UIElement 只允许挂在**一个**父元素上。之前这里先把 img 赋给
            // cover.Child，之后又往 coverStack 里 add 同一个 img —— WPF 会直接抛
            // "指定的元素已经是另一个元素的逻辑子元素"，而 WinExe 的未处理异常是静默的，
            // 表现就是"点了没反应"。现在 img 只 add 一次。
            bool haveLocal = FindLocal(info.Id) != null;
            Grid coverStack = new Grid();
            coverStack.Children.Add(img);
            if (haveLocal)
            {
                Border tag = Ds.Badge("已安装", Theme.Ok);
                tag.HorizontalAlignment = HorizontalAlignment.Left;
                tag.VerticalAlignment = VerticalAlignment.Top;
                tag.Margin = new Thickness(Theme.S3, Theme.S3, 0, 0);
                tag.Background = Theme.OverlayHeavy;
                coverStack.Children.Add(tag);
            }
            cover.Child = coverStack;
            stack.Children.Add(cover);

            StackPanel body = Ds.Col(0);
            body.Margin = new Thickness(Theme.S4, Theme.S4, Theme.S4, Theme.S4);

            TextBlock name = Ds.Text(AnimationStateStore.DisplayName(info), Theme.TBody, Theme.Fg,
                FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(name);

            if (!string.IsNullOrEmpty(info.Author))
            {
                TextBlock by = Ds.Text(info.Author, Theme.TMicro, Theme.Fg3, FontWeights.Normal);
                by.Margin = new Thickness(0, 3, 0, 0);
                body.Children.Add(by);
            }

            string metaLine = "";
            if (info.ResolutionText != null) metaLine = info.ResolutionText;
            if (info.DurationText != null) metaLine += (metaLine.Length > 0 ? "  ·  " : "") + info.DurationText;
            if (info.SizeText != null) metaLine += (metaLine.Length > 0 ? "  ·  " : "") + info.SizeText;
            if (metaLine.Length > 0)
            {
                TextBlock m = Ds.Text(metaLine, Theme.TMicro, Theme.Fg3, FontWeights.Normal);
                m.Margin = new Thickness(0, Theme.S2, 0, 0);
                body.Children.Add(m);
            }

            // 兼容性：把真实判据的结果说出来，而不是一个没有来源的标签
            TextBlock compat = Ds.Text(info.UefiCompatible
                    ? "兼容性：Windows 阶段可用 · UEFI 候选"
                    : "兼容性：Windows 阶段可用",
                Theme.TMicro, Theme.Fg3, FontWeights.Normal);
            compat.Margin = new Thickness(0, Theme.S2, 0, 0);
            body.Children.Add(compat);

            StackPanel acts = Ds.Bar(Theme.S2);
            acts.Margin = new Thickness(0, Theme.S4, 0, 0);
            AnimationInfo captured = info;
            acts.Children.Add(Ds.Action("Preview", ActionLevel.Tertiary, delegate { PreviewRemote(captured); }));
            if (haveLocal)
            {
                acts.Children.Add(Ds.Action("In Library", ActionLevel.Tertiary, delegate
                {
                    AnimationInfo local = FindLocal(captured.Id);
                    if (local != null) SelectAndPreview(local);
                }));
            }
            else
            {
                acts.Children.Add(Ds.Action("Install", ActionLevel.Secondary, delegate { InstallRemote(captured); }));
            }
            body.Children.Add(acts);

            stack.Children.Add(body);
            card.Child = stack;
            return card;
        }

        private void LoadCommunityAsync()
        {
            if (communityStatus != null) communityStatus.Text = "正在读取社区目录…";
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                string error;
                List<AnimationInfo> list = AnimationRepository.FetchCommunityIndex(out error);
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate()
                {
                    if (error != null)
                    {
                        communityCache = new List<AnimationInfo>();
                        if (communityStatus != null) communityStatus.Text = "读取失败：" + error;
                        notices.Show("Community", "无法读取社区目录：" + error
                            + "。这不影响开机动画的播放。", NoticeLevel.Warning,
                            new NoticeAction[] { new NoticeAction("Retry", ActionLevel.Secondary,
                                delegate { LoadCommunityAsync(); }) });
                    }
                    else
                    {
                        communityCache = list;
                        if (communityStatus != null)
                            communityStatus.Text = "共 " + list.Count.ToString(CultureInfo.InvariantCulture) + " 条";
                    }
                    Refresh();
                }));
            });
            worker.IsBackground = true;
            worker.Name = "ba-community-index";
            worker.Start();
        }

        private void PreviewRemote(AnimationInfo info)
        {
            AnimationInfo local = FindLocal(info.Id);
            if (local != null && local.IsPlayable)
            {
                SelectAndPreview(local);
                return;
            }
            notices.Show("Community",
                "「" + AnimationStateStore.DisplayName(info) + "」还没下载，先在卡片上点 Install；"
                + "装好就能像本地动画一样预览。", NoticeLevel.Info,
                new NoticeAction[] { new NoticeAction("Install now", ActionLevel.Primary,
                    delegate { InstallRemote(info); }) });
            Refresh();
        }

        private AnimationInfo FindLocal(string id)
        {
            List<AnimationInfo> items = store.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Id, id, StringComparison.OrdinalIgnoreCase)) return items[i];
            }
            return null;
        }

        private void InstallRemote(AnimationInfo info)
        {
            if (string.IsNullOrEmpty(info.RemoteVideoUrl))
            {
                notices.Show("Community", "这条目录项没有可下载的直链。", NoticeLevel.Error, null);
                return;
            }
            Window dialog = Community.BuildInstallWindowFor(info, this);
            dialog.Owner = this;
            dialog.ShowDialog();
            // 装完自动进入动画库（需求要求）
            LoadLibraryAsync();
            Go(Page.Library);
        }

        private void RemoveAnimation(AnimationInfo info)
        {
            if (info == null || info.Path == null) return;
            bool ok = ConfirmDialog.Ask(this, "Remove Animation",
                "从库里移除「" + AnimationStateStore.DisplayName(info) + "」？" + Environment.NewLine + Environment.NewLine
                + "会删除文件：" + Environment.NewLine + info.Path + Environment.NewLine + Environment.NewLine
                + "动画资源本身不会被修改，需要时可以重新下载。",
                "Remove", true);
            if (!ok) return;
            try
            {
                ReleaseFile(info);
                store.SetStatus("已移除：" + AnimationStateStore.DisplayName(info), "ok");
            }
            catch (Exception ex)
            {
                notices.Show("Library", "移除失败：" + ex.Message, NoticeLevel.Error, null);
                store.SetStatus("移除失败：" + ex.Message, "bad");
            }
            LoadLibraryAsync();
        }

        private void ReleaseFile(AnimationInfo info)
        {
            if (hero != null && info.Path != null
                && string.Equals(hero.CurrentPath, info.Path, StringComparison.OrdinalIgnoreCase))
            {
                hero.Release();
            }
            try { if (File.Exists(info.Path)) File.Delete(info.Path); }
            catch (Exception ex) { Program.Log("删除文件失败: " + ex.Message); throw; }

            if (info.Source == AnimationSource.Community)
            {
                string meta = Path.Combine(AnimationRepository.CommunityDir, info.Id + ".meta");
                try { if (File.Exists(meta)) File.Delete(meta); } catch { }
            }
            string posterFile = Program.PosterCachePathFor(info.Id);
            try { if (File.Exists(posterFile)) File.Delete(posterFile); } catch { }

            if (store.ActiveId != null && string.Equals(store.ActiveId, info.Id, StringComparison.OrdinalIgnoreCase))
            {
                string fallback = Program.ClipIds[0];
                store.SyncActive(fallback);
                Program.WriteChosen(fallback);
            }
        }

        // ══════════════════════════════════════════════════ 预览

        /// <summary>
        /// 打开沉浸式预览。**每次都新建一个 MediaPlayerBox**，
        /// 所以不存在"多个动画共享错误的播放器实例"。
        /// </summary>
        private void OpenPreview(AnimationInfo info, PreviewLayer layer)
        {
            if (info == null)
            {
                notices.Show("Preview", "还没有选中动画。", NoticeLevel.Info,
                    new NoticeAction[] { new NoticeAction("去动画库", ActionLevel.Secondary,
                        delegate { Go(Page.Library); }) });
                return;
            }
            if (!info.IsPlayable)
            {
                notices.Show("Preview",
                    "「" + AnimationStateStore.DisplayName(info) + "」还没有本地文件，"
                    + "无法在此刻播放。它是社区目录里的条目。", NoticeLevel.Warning,
                    new NoticeAction[]
                    {
                        new NoticeAction("去社区安装", ActionLevel.Primary, delegate { Go(Page.Community); }),
                        new NoticeAction("Run Diagnostics", ActionLevel.Secondary, delegate
                        {
                            Go(Page.Diagnostics); RunDiagnosticsNow();
                        }, true),
                    });
                return;
            }
            // 先把主窗口里那一路放掉，避免两路同时解码同一文件
            if (hero != null) hero.Release();
            BootPreview preview = new BootPreview(info, layer);
            preview.ShowDialog();
            // 回来之后重建首页：播放器已被释放，必须重新绑定
            Refresh();
        }

        private void SelectAndPreview(AnimationInfo info)
        {
            store.SelectPreview(info.Id);
            PushRecent(info.Id);
            if (current != Page.Home) Go(Page.Home);
            else Refresh();
        }

        // ══════════════════════════════════════════════════ 系统 / 启动 / 诊断

        private UIElement BuildSystemStatus()
        {
            StackPanel page = Ds.Col(0);
            page.Children.Add(Ds.PageTitle("系统状态"));
            TextBlock d = Ds.Text("每一项都来自真实探测，没有任何硬编码。鼠标停在值上可以看到检测的是什么。",
                Theme.TMeta, Theme.Fg3, FontWeights.Normal);
            d.Margin = new Thickness(0, Theme.S2, 0, Theme.S7);
            page.Children.Add(d);

            if (lastReport == null)
            {
                page.Children.Add(Ds.State("正在检测…", "首次进入这一页会自动跑一次诊断。", null));
                return Ds.Page(page);
            }

            page.Children.Add(BuildStatusGroups());
            page.Children.Add(Ds.Divider(Theme.S7, Theme.S7));
            page.Children.Add(BuildStartupSection());
            return Ds.Page(page);
        }

        private UIElement BuildStartup()
        {
            StackPanel page = Ds.Col(0);
            page.Children.Add(Ds.PageTitle("启动项"));
            TextBlock d = Ds.Text("三条自启路径是不同的三件事，必须分开看：随 Windows 启动、睡眠唤醒、冷启动之前。",
                Theme.TMeta, Theme.Fg3, FontWeights.Normal);
            d.Margin = new Thickness(0, Theme.S2, 0, Theme.S7);
            page.Children.Add(d);

            page.Children.Add(ExplainBlock("1 · 随 Windows 启动本程序",
                "登录后由 Run 键或计划任务拉起 BootAnimation.exe 播一次动画。",
                lastReport == null ? null : FindItem(lastReport.Startup, "Windows Startup")));
            page.Children.Add(ExplainBlock("2 · 睡眠 / 休眠唤醒时播放",
                "S3/S4 恢复不会重新执行 UEFI，也不会重跑登录流程，所以需要独立的电源事件监听（ResumeRuntime）。",
                lastReport == null ? null : FindItem(lastReport.BootRuntime, "Resume Animation")));
            page.Children.Add(ExplainBlock("3 · 冷启动 / 重启（Windows Boot Manager 之前）",
                "需要在 ESP 上放一个 GOP 播放器并抢先于 bootmgfw.efi 执行。当前版本未实现，如实标注。",
                lastReport == null ? null : FindItem(lastReport.BootRuntime, "UEFI Boot Entry")));
            page.Children.Add(ExplainBlock("Fallback",
                "任何一步失败都必须能正常进 Windows。Windows 侧的兜底是「解析不到动画就安静跳过」，不会卡住桌面。",
                lastReport == null ? null : FindItem(lastReport.BootRuntime, "Fallback Boot")));

            page.Children.Add(Ds.Divider(Theme.S7, Theme.S7));
            page.Children.Add(BuildStartupSection());
            return Ds.Page(page);
        }

        private static StatusItem FindItem(List<StatusItem> items, string label)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Label, label, StringComparison.OrdinalIgnoreCase)) return items[i];
            }
            return null;
        }

        /// <summary>说明块：一个小标题 + 正文 + 真实状态徽章。没有卡片外框。</summary>
        private UIElement ExplainBlock(string title, string body, StatusItem status)
        {
            StackPanel s = Ds.Col(0);
            StackPanel head = Ds.Bar(Theme.S3);
            head.Children.Add(Ds.Text(title, Theme.TBody, Theme.Fg, FontWeights.SemiBold));
            if (status != null) head.Children.Add(Ds.Badge(status.Glyph() + " " + status.Value, status.Tone()));
            s.Children.Add(head);
            TextBlock b = Ds.Text(body, Theme.TMeta, Theme.Fg2, FontWeights.Normal);
            b.Margin = new Thickness(0, Theme.S2, 0, Theme.S5);
            s.Children.Add(b);
            return s;
        }

        private UIElement BuildDiagnostics()
        {
            StackPanel page = Ds.Col(0);
            page.Children.Add(Ds.PageTitle("诊断"));
            TextBlock d = Ds.Text("覆盖启动项、Boot Runtime、动画包、哈希、分辨率、内存预算等全部检查项。",
                Theme.TMeta, Theme.Fg3, FontWeights.Normal);
            d.Margin = new Thickness(0, Theme.S2, 0, Theme.S6);
            page.Children.Add(d);

            StackPanel bar = Ds.Bar(Theme.S3);
            bar.Children.Add(Ds.Action("Run Diagnostics", ActionLevel.Secondary, delegate { RunDiagnosticsNow(); }));
            if (lastReport != null)
            {
                TextBlock sum = Ds.Text(lastReport.Summary, Theme.TBody,
                    lastReport.SummaryLevel == StatusLevel.Ok ? Theme.Ok : Theme.Warn, FontWeights.Normal);
                sum.VerticalAlignment = VerticalAlignment.Center;
                bar.Children.Add(sum);
            }
            page.Children.Add(bar);

            if (lastReport == null)
            {
                page.Children.Add(Ds.Block(Ds.State("还没跑过诊断", "点 Run Diagnostics。", null), Theme.S5, 0));
                return Ds.Page(page);
            }

            page.Children.Add(new Border { Height = Theme.S6 });
            List<StatusItem> all = lastReport.All();
            StackPanel list = Ds.Col(0);
            for (int i = 0; i < all.Count; i++)
            {
                StatusItem it = all[i];
                StackPanel row = Ds.Col(0);
                row.Children.Add(Ds.Text(it.Glyph() + "  " + it.Label + "  —  " + it.Value,
                    Theme.TBody, it.Tone(), FontWeights.Normal));
                if (!string.IsNullOrEmpty(it.Detail))
                {
                    TextBlock det = Ds.Text(it.Detail, Theme.TMicro, Theme.Fg3, FontWeights.Normal);
                    det.Margin = new Thickness(Theme.S4, 3, 0, 0);
                    row.Children.Add(det);
                }
                row.Margin = new Thickness(0, 0, 0, Theme.S4);
                list.Children.Add(row);
            }
            page.Children.Add(list);
            return Ds.Page(page);
        }

        // ══════════════════════════════════════════════════ 设置 / 关于

        private UIElement BuildSettings()
        {
            StackPanel page = Ds.Col(0);
            page.Children.Add(Ds.PageTitle("设置"));
            TextBlock d = Ds.Text("预览相关的开关。改完立刻生效，不需要重启。",
                Theme.TMeta, Theme.Fg3, FontWeights.Normal);
            d.Margin = new Thickness(0, Theme.S2, 0, Theme.S7);
            page.Children.Add(d);

            // 贴合方式
            page.Children.Add(Ds.Label("Fit Mode", Theme.Fg3));
            TextBlock note = Ds.Text("窗口长宽比不会等于视频长宽比。Auto 在裁切小于 1.2% 时铺满，否则整帧 + 氛围填充。"
                + "三种模式都不会拉伸视频。", Theme.TMicro, Theme.Fg3, FontWeights.Normal);
            note.Margin = new Thickness(0, Theme.S2, 0, Theme.S3);
            page.Children.Add(note);

            StackPanel fits = Ds.Bar(Theme.S2);
            FitMode[] modes = new FitMode[] { FitMode.Auto, FitMode.Ambient, FitMode.Cover, FitMode.Contain };
            for (int i = 0; i < modes.Length; i++)
            {
                FitMode m = modes[i];
                Button b = Ds.Action(PreviewEngine.FitLabel(m), ActionLevel.Tertiary, delegate
                {
                    heroFit = m;
                    if (hero != null) hero.SetFit(heroFit, ActualWidth, ActualHeight);
                    store.SetStatus("贴合方式已设为 " + PreviewEngine.FitLabel(m), "ok");
                    Refresh();
                });
                if (m == heroFit)
                {
                    b.Foreground = Theme.Fg;
                    b.Background = Accent.Dim;
                    b.FontWeight = FontWeights.SemiBold;
                }
                fits.Children.Add(b);
            }
            page.Children.Add(fits);

            page.Children.Add(Ds.Divider(Theme.S7, Theme.S7));

            // 默认静音
            page.Children.Add(Ds.Label("Audio", Theme.Fg3));
            TextBlock muteNote = Ds.Text("登录时突然出声对很多人是打扰。这个开关只影响预览；开机播放用 --mute 控制。",
                Theme.TMicro, Theme.Fg3, FontWeights.Normal);
            muteNote.Margin = new Thickness(0, Theme.S2, 0, Theme.S3);
            page.Children.Add(muteNote);
            page.Children.Add(Ds.Action(heroMuted ? "当前：静音" : "当前：有声", ActionLevel.Tertiary, delegate
            {
                heroMuted = !heroMuted;
                if (hero != null) hero.SetMuted(heroMuted);
                Refresh();
            }));

            page.Children.Add(Ds.Divider(Theme.S7, Theme.S7));

            // 数据目录
            page.Children.Add(Ds.Label("Storage", Theme.Fg3));
            StackPanel facts = Ds.Col(Theme.S2);
            facts.Margin = new Thickness(0, Theme.S3, 0, 0);
            facts.Children.Add(Ds.MetaRow(Ds.MetaField("数据目录", Program.DataDirectory)));
            facts.Children.Add(Ds.MetaRow(Ds.MetaField("背景图缓存", Path.Combine(Program.DataDirectory, "posters"))));
            page.Children.Add(facts);

            return Ds.Page(page);
        }

        private UIElement BuildAbout()
        {
            StackPanel page = Ds.Col(0);
            page.Children.Add(Ds.PageTitle("关于"));
            StackPanel facts = Ds.Col(Theme.S4);
            facts.Margin = new Thickness(0, Theme.S6, 0, 0);
            facts.Children.Add(Ds.MetaRow(Ds.MetaField("版本", Program.AppVersion)));
            facts.Children.Add(Ds.MetaRow(Ds.MetaField("程序", Program.ExePath)));
            facts.Children.Add(Ds.MetaRow(Ds.MetaField("社区", Community.SiteUrl)));

            page.Children.Add(facts);
            page.Children.Add(Ds.Divider(Theme.S7, Theme.S7));
            page.Children.Add(Ds.State("关于 UEFI 阶段",
                "冷启动（Windows Boot Manager 之前）播放在当前版本尚未实现，"
                + "设计、接入点与 Fallback 机制见仓库里的 UEFI-RUNTIME.md。", null));
            return Ds.Page(page);
        }

        // ══════════════════════════════════════════════════ 入口

        /// <summary>
        /// 打开管理界面。**只有用户主动打开界面时才创建它** ——
        /// 开机播放走 BootRuntime，完全不经过这个类。
        /// </summary>
        internal static int Run()
        {
            Application app = Application.Current;
            bool owned = false;
            if (app == null)
            {
                app = new Application();
                owned = true;
            }
            BootAnimation.CrashGuard.AttachUi(app);
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            Shell shell = new Shell();
            app.MainWindow = shell;
            if (owned) app.Run(shell);
            else shell.Show();
            return 0;
        }
    }
}
