// Shell.cs —— 主窗口外壳（S2）
//
// 这是需求的"应用外壳"：左侧栏 + 顶部栏 + 主内容区，取代此前 460×340 的选片小窗。
//   · 默认 1440×900、最小 1100×700（Theme 里的常量，一处定义）
//   · 侧栏 248px 常驻：品牌 / 导航 / 系统 / 设置 / GitHub
//   · 按窗口宽度**重排**（Theme.TierFor），不是缩放：≥1500px 时 Home 变成"预览在左、
//     信息在右"，窄了自动堆叠
//
// 编译器约束与 Theme.cs 相同：csc 4/5，无字符串插值、无 ?.、无表达式体成员、无 XAML。
// 视频只用 WPF 自带的 MediaElement；**不自动播放**（需求 §27），点 Preview 才播。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BootAnimation
{
    internal static class Shell
    {
        // 导航分组的**诚实**映射：只列这个程序真的有的页面。没有的页面宁可先不放，
        // 也不做点了没反应的入口。
        private static readonly string[][] Groups = new string[][]
        {
            new string[] { "PRODUCT", "首页", "动画库", "随机播放", "我的库" },
            new string[] { "SYSTEM", "系统状态", "启动项" },
            new string[] { "COMMUNITY", "社区片段" },
            new string[] { "APP", "设置", "关于" }
        };

        /// <summary>
        /// 建主窗口。
        /// clipId / clipName：当前选中的片段（由调用方从 settings 读出后传进来）。
        /// mediaPath：可播放的文件路径；为 null 表示这个片段现在取不到媒体。
        /// </summary>
        public static Window Build(string clipId, string clipName, string mediaPath, string dataDir)
        {
            Window win = new Window();
            Ui.ApplyWindow(win);
            win.Title = "DSH Boot Animation";
            win.Width = Theme.WinDefaultW;
            win.Height = Theme.WinDefaultH;
            win.MinWidth = Theme.WinMinW;
            win.MinHeight = Theme.WinMinH;
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Grid root = new Grid();
            ColumnDefinition nav = new ColumnDefinition();
            nav.Width = new GridLength(Theme.NavW);
            ColumnDefinition body = new ColumnDefinition();
            body.Width = new GridLength(1, GridUnitType.Star);
            root.ColumnDefinitions.Add(nav);
            root.ColumnDefinitions.Add(body);

            Border sidebar = BuildSidebar();
            Grid.SetColumn(sidebar, 0);
            root.Children.Add(sidebar);

            Grid right = new Grid();
            RowDefinition top = new RowDefinition();
            top.Height = new GridLength(56);
            RowDefinition content = new RowDefinition();
            content.Height = new GridLength(1, GridUnitType.Star);
            right.RowDefinitions.Add(top);
            right.RowDefinitions.Add(content);

            Border topBar = BuildTopBar(clipId, mediaPath, win);
            Grid.SetRow(topBar, 0);
            right.Children.Add(topBar);

            // 主内容区：水平/垂直内边距给足，宽屏才有呼吸感（需求：不要让内容挤在一起）
            Border host = new Border();
            host.Padding = new Thickness(Theme.S6, Theme.S6, Theme.S6, Theme.S6);
            ScrollViewer scroller = new ScrollViewer();
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            host.Child = scroller;
            Grid.SetRow(host, 1);
            right.Children.Add(host);

            Grid.SetColumn(right, 1);
            root.Children.Add(right);
            win.Content = root;

            ContentControl slot = new ContentControl();
            scroller.Content = slot;
            slot.Content = BuildHome(clipId, clipName, mediaPath, dataDir, win);

            // 按宽度重排：需求 §"响应式窗口" —— 重排内容，不是缩放 UI。
            win.SizeChanged += delegate(object sender, SizeChangedEventArgs e)
            {
                int tier = Theme.TierFor(e.NewSize.Width);
                slot.Tag = tier;
                object current = slot.Content;
                HomeView home = current as HomeView;
                if (home != null) home.ApplyTier(tier);
            };

            return win;
        }

        private static Border BuildSidebar()
        {
            StackPanel column = Ui.Stack(false, Theme.S3);

            StackPanel brand = Ui.Stack(false, 0);
            TextBlock name = Ui.Strong("DSH", 17, Theme.Fg);
            TextBlock sub = Ui.Text("Boot Animation", Theme.TMicro, Theme.Fg3);
            sub.Margin = new Thickness(0, 2, 0, 0);
            brand.Children.Add(name);
            brand.Children.Add(sub);
            brand.Margin = new Thickness(Theme.S3, Theme.S5, Theme.S3, Theme.S5);
            column.Children.Add(brand);

            for (int g = 0; g < Groups.Length; g++)
            {
                string[] group = Groups[g];
                column.Children.Add(Ui.GroupLabel(group[0]));
                for (int i = 1; i < group.Length; i++)
                {
                    // 当前页只有"首页"是真的可切换视图；其余在后续切片里落地。
                    bool current = group[1] == "首页" && i == 1;
                    Border item = Ui.NavItem(group[i], current, null);
                    item.Margin = new Thickness(Theme.S2, 0, Theme.S2, 2);
                    column.Children.Add(item);
                }
            }

            TextBlock github = Ui.Text("GitHub", Theme.TMeta, Theme.Fg2);
            github.Margin = new Thickness(Theme.S3, Theme.S5, Theme.S3, 0);
            // 版本号只有一处真源：AssemblyInfo.cs 的 AssemblyVersion。这里读程序集，
            // 不再手写一份（此前 Setup.cs 与 SetupInfo.cs 各写了一份，已经不一致了）。
            Version assemblyVersion = typeof(Shell).Assembly.GetName().Version;
            TextBlock version = Ui.Text("Version " + assemblyVersion.ToString(3), Theme.TMicro, Theme.Fg3);
            version.Margin = new Thickness(Theme.S3, 2, Theme.S3, Theme.S4);
            column.Children.Add(github);
            column.Children.Add(version);

            Border side = new Border();
            side.Background = Theme.Bg2;
            side.BorderBrush = Theme.Line;
            side.BorderThickness = new Thickness(0, 0, 1, 0);
            side.Child = column;
            return side;
        }

        private static Border BuildTopBar(string clipId, string mediaPath, Window win)
        {
            Grid bar = new Grid();
            ColumnDefinition left = new ColumnDefinition();
            left.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition right = new ColumnDefinition();
            bar.ColumnDefinitions.Add(left);
            bar.ColumnDefinitions.Add(right);

            StackPanel actions = Ui.Stack(true, Theme.S2);
            actions.HorizontalAlignment = HorizontalAlignment.Right;
            actions.VerticalAlignment = VerticalAlignment.Center;
            actions.Margin = new Thickness(0, 0, Theme.S5, 0);

            Button search = new Button();
            search.Content = "搜索  (Ctrl+K)";
            search.FontFamily = Theme.Font;
            search.FontSize = Theme.TMeta;
            search.Foreground = Theme.Fg3;
            search.Background = Theme.Card;
            search.BorderBrush = Theme.Line;
            search.Padding = new Thickness(Theme.S4, Theme.S1, Theme.S4, Theme.S1);
            search.MinWidth = 160;
            search.IsEnabled = false;                 // 命令面板在 S5 落地，先不放一个假按钮
            ToolTipService.SetToolTip(search, "命令面板将在后续切片接入");

            Button settings = Ui.GhostButton("⚙", null);
            settings.IsEnabled = false;
            ToolTipService.SetToolTip(settings, "设置页将在后续切片接入");

            actions.Children.Add(search);
            actions.Children.Add(settings);
            Grid.SetColumn(actions, 1);
            bar.Children.Add(actions);

            Border top = new Border();
            top.Background = Theme.Bg;
            top.BorderBrush = Theme.Line;
            top.BorderThickness = new Thickness(0, 0, 0, 1);
            top.Child = bar;
            return top;
        }

        /// <summary>Home：大预览优先（需求 §Home）—— 预览是整个页面最大的视觉主体。</summary>
        private static HomeView BuildHome(string clipId, string clipName, string mediaPath, string dataDir, Window win)
        {
            return new HomeView(clipId, clipName, mediaPath, dataDir);
        }

    /// <summary>
    /// 建主窗口并进入消息循环（由 BootAnimation.cs 的 --manager 分支调用）。
    /// 已知临时重复：数据目录这里又写了一遍，S6 收敛配置时一并去掉。
    /// </summary>
    public static int Run()
    {
        string dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BootAnimation");
        string settingsPath = System.IO.Path.Combine(dataDir, "settings.txt");
        string clipId = System.IO.File.Exists(settingsPath) ? System.IO.File.ReadAllText(settingsPath).Trim() : "";
        if (clipId.Length == 0) clipId = "brand";
        string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
        string mediaDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), "clips");
        string candidate = System.IO.Path.Combine(mediaDir, clipId + ".mp4");
        string mediaPath = System.IO.File.Exists(candidate) ? candidate : null;
        Window win = Build(clipId, clipId, mediaPath, dataDir);
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        app.Run(win);
        return 0;
    }

    }

    /// <summary>
    /// Home 视图。宽屏（≥1500px）预览与信息左右并排，窄屏上下堆叠 —— 由 ApplyTier 重排。
    /// </summary>
    internal sealed class HomeView : Grid
    {
        private readonly Border previewBox;
        private readonly MediaElement media;
        private readonly StackPanel info;
        private readonly TextBlock metaLine;
        private readonly StackPanel previewColumn;
        private readonly Grid split;
        private readonly string mediaPath;

        public HomeView(string clipId, string clipName, string mediaPath, string dataDir)
        {
            this.mediaPath = mediaPath;

            // —— 左：大预览。需求 §Home：最小 420px，Standard 480–560，Wide 500–650 ——
            media = new MediaElement();
            media.LoadedBehavior = MediaState.Manual;
            media.UnloadedBehavior = MediaState.Manual;
            media.Stretch = Stretch.Uniform;
            media.Volume = 0;

            previewBox = new Border();
            previewBox.Background = Brushes.Black;
            previewBox.CornerRadius = new CornerRadius(Theme.R3);
            previewBox.BorderBrush = Theme.Line;
            previewBox.BorderThickness = new Thickness(1);
            previewBox.Height = Theme.PreviewHeightFor(1);
            previewBox.Child = media;

            previewColumn = Ui.Stack(false, Theme.S4);
            previewColumn.Children.Add(previewBox);

            StackPanel title = Ui.Stack(false, Theme.S1);
            title.Children.Add(Ui.Strong(clipName, Theme.TTitle, Theme.Fg));
            metaLine = Ui.Text("读取媒体信息…", Theme.TMeta, Theme.Fg3);
            title.Children.Add(metaLine);
            previewColumn.Children.Add(title);

            if (mediaPath != null && mediaPath.Length > 0)
            {
                media.Source = new Uri(mediaPath, UriKind.Absolute);
                media.MediaOpened += delegate(object s, RoutedEventArgs e)
                {
                    string res = media.NaturalVideoWidth + "×" + media.NaturalVideoHeight;
                    string dur = media.NaturalDuration.HasTimeSpan
                        ? media.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s"
                        : "时长不可得";
                    // 帧率 WPF 不暴露，所以这一行**没有** FPS —— 不编一个数字出来。
                    metaLine.Text = res + " · " + dur + " · 帧率不可得";
                };
                media.MediaFailed += delegate(object s, ExceptionRoutedEventArgs e)
                {
                    metaLine.Text = "媒体打不开：" + e.ErrorException.Message;
                };
            }
            else
            {
                metaLine.Text = "这个片段现在取不到媒体文件";
            }

            // —— 右：信息与操作 ——
            info = Ui.Stack(false, Theme.S5);
            info.Children.Add(Ui.Section("CURRENT ANIMATION"));
            info.Children.Add(Ui.Fact("ClipId", clipId));
            info.Children.Add(Ui.Fact("片名", clipName));
            info.Children.Add(Ui.Fact("数据目录", dataDir));

            StackPanel buttons = Ui.Stack(true, Theme.S3);
            Button preview = Ui.Button("Preview", false, delegate
            {
                if (media.Source != null) { media.Position = TimeSpan.Zero; media.Play(); }
            });
            Button apply = Ui.Button("Apply", true, null);
            Button random = Ui.Button("Random", false, null);
            apply.IsEnabled = false;
            random.IsEnabled = false;
            ToolTipService.SetToolTip(apply, "应用与随机在后续切片接入（先不做点了没反应的按钮）");
            ToolTipService.SetToolTip(random, "应用与随机在后续切片接入");
            buttons.Children.Add(preview);
            buttons.Children.Add(apply);
            buttons.Children.Add(random);
            info.Children.Add(buttons);

            split = new Grid();
            ColumnDefinition a = new ColumnDefinition();
            ColumnDefinition b = new ColumnDefinition();
            split.ColumnDefinitions.Add(a);
            split.ColumnDefinitions.Add(b);
            // 两行也要先建好：窄屏堆叠时信息放在第 1 行，没有 RowDefinition 会直接抛。
            RowDefinition r0 = new RowDefinition();
            RowDefinition r1 = new RowDefinition();
            r0.Height = GridLength.Auto;
            r1.Height = GridLength.Auto;
            split.RowDefinitions.Add(r0);
            split.RowDefinitions.Add(r1);
            split.Children.Add(previewColumn);
            split.Children.Add(info);
            Grid.SetColumn(previewColumn, 0);
            Grid.SetRow(previewColumn, 0);
            Grid.SetColumn(info, 1);

            Children.Add(split);
            ApplyTier(1);
        }

        /// <summary>
        /// 按档位重排：≥1500px（tier ≥ 2）预览在左、信息在右；窄了改成上下堆叠。
        /// 同时调整预览高度与两栏留白 —— 变的是空间分配，不是缩放。
        /// </summary>
        public void ApplyTier(int tier)
        {
            bool sideBySide = tier >= 2;
            previewBox.Height = Theme.PreviewHeightFor(tier);

            if (sideBySide)
            {
                split.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                split.ColumnDefinitions[1].Width = new GridLength(360);
                Grid.SetRow(info, 0);
                Grid.SetColumn(info, 1);
                info.Margin = new Thickness(Theme.S6, 0, 0, 0);
            }
            else
            {
                split.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                split.ColumnDefinitions[1].Width = new GridLength(0);
                Grid.SetRow(info, 1);
                Grid.SetColumn(info, 0);
                info.Margin = new Thickness(0, Theme.S6, 0, 0);
            }
        }
    }
}
