// Theme.cs —— 主题令牌与组件基座（S1）
//
// 为什么要有这个文件：在此之前，界面是 4～5 个窗口各自 `new Window()` + 逐个 Add 控件拼出来的，
// 颜色、字号、间距全是各处硬写。于是"把界面做完整"这件事变成了改五个地方，也没法整体调空间
// 比例 —— 那正是它看起来像小型工具窗口的原因之一。
//
// 这里只放两样东西：
//   1. Theme —— 颜色 / 间距 / 圆角 / 字级（对应网站的 tokens.css），改一处全站生效；
//   2. Ui    —— 控件工厂：按钮、卡片、分组标题、事实行、状态点、四态块、侧栏项。
//
// 编译器约束：产物用 Windows 自带的 csc（C# 4/5），所以
//   ✗ 字符串插值 $"..."   ✗ 空条件运算符 ?.   ✗ 表达式体成员 =>   ✗ nameof/元组/out var
// 一律用经典写法。不引任何第三方依赖，全部是 WPF 自带类型。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BootAnimation
{
    /// <summary>颜色 / 间距 / 字级。改这里就够了，不要在窗口里硬写色值。</summary>
    internal static class Theme
    {
        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // —— 表面：三级深色（需求给定）。比之前更中性的冷灰，不是纯黑也不是蓝黑 ——
        public static readonly SolidColorBrush Bg = Frozen(0x09, 0x0a, 0x0c);
        public static readonly SolidColorBrush Bg2 = Frozen(0x0d, 0x0f, 0x12);
        public static readonly SolidColorBrush Card = Frozen(0x11, 0x13, 0x18);
        public static readonly SolidColorBrush Raise = Frozen(0x17, 0x1a, 0x20);

        // —— 描边：靠 alpha 白，在任何面上都对。侧栏不再逐项描边，只用这一档做分隔 ——
        public static readonly SolidColorBrush Line = Alpha(255, 255, 255, 0.07);
        public static readonly SolidColorBrush LineStrong = Alpha(255, 255, 255, 0.13);

        /// <summary>覆盖在视频上的半透明底（播放控制条、卡片播放按钮）。</summary>
        public static readonly SolidColorBrush Overlay = Alpha(9, 10, 12, 0.72);
        /// <summary>Studio 模式下更重的遮罩（控制条浮在画面上时必须能压住亮部）。</summary>
        public static readonly SolidColorBrush OverlayHeavy = Alpha(9, 10, 12, 0.88);

        // —— 文字：三级冷白（需求给定） ——
        public static readonly SolidColorBrush Fg = Frozen(0xf4, 0xf5, 0xf7);
        public static readonly SolidColorBrush Fg2 = Frozen(0xa7, 0xad, 0xb7);
        public static readonly SolidColorBrush Fg3 = Frozen(0x6f, 0x76, 0x82);

        // —— 品牌红：只用于主操作与当前状态，绝不整屏铺 ——
        public static readonly SolidColorBrush Brand = Frozen(0xe1, 0x1d, 0x2e);
        public static readonly SolidColorBrush BrandHi = Frozen(0xf2, 0x33, 0x46);
        public static readonly SolidColorBrush BrandPress = Frozen(0xc2, 0x14, 0x1f);
        public static readonly SolidColorBrush BrandDim = Alpha(0xe1, 0x1d, 0x2e, 0.12);

        // —— 语义色 ——
        public static readonly SolidColorBrush Ok = Frozen(0x3f, 0xb9, 0x50);
        public static readonly SolidColorBrush Warn = Frozen(0xd2, 0x99, 0x22);
        public static readonly SolidColorBrush Danger = Frozen(0xf8, 0x51, 0x49);
        public static readonly SolidColorBrush Info = Frozen(0x7e, 0xa0, 0xff);

        private static SolidColorBrush Alpha(byte r, byte g, byte b, double a)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb((byte)(a * 255), r, g, b));
            brush.Freeze();
            return brush;
        }

        // —— 间距刻度：4 / 8 / 12 / 16 / 24 / 32 / 48 / 64（需求给定的空间系统） ——
        public const double S1 = 4;
        public const double S2 = 8;
        public const double S3 = 12;
        public const double S4 = 16;
        public const double S5 = 24;
        public const double S6 = 32;
        public const double S7 = 48;
        public const double S8 = 64;

        // —— 圆角（需求给定）：按钮 8 / 输入 8 / 卡片 10–12 / 对话框 12–16 ——
        public const double RButton = 8;
        public const double R1 = 6;
        public const double R2 = 10;
        public const double RCard = 12;
        public const double RDialog = 14;
        public const double R3 = 14;

        // —— 字级（需求给定）：Hero 40–56 / 标题 36–48 / 小节 20–24 / 正文 14–16 / 元信息 12–14 ——
        /// <summary>Hero 标题：当前动画的名字。</summary>
        public const double THero = 44;
        /// <summary>页面大标题。</summary>
        public const double TTitle = 34;
        public const double TH1 = 28;
        public const double TH2 = 21;
        /// <summary>小节标题 20–24 的下沿。</summary>
        public const double TSection = 20;
        public const double TBody = 15;
        public const double TMeta = 13;
        public const double TMicro = 12;

        /// <summary>全大写小标签的字符间距。英文小标签要"散"一点才像成熟软件，不是挤在一起。</summary>
        public const double TrackLabel = 1.2;

        /// <summary>动效时长（需求给定）：快 120 / 普通 200 / 慢 320。</summary>
        public const int AnimFast = 120;
        public const int AnimNormal = 200;
        public const int AnimSlow = 320;

        /// <summary>系统字体栈：Windows 用 Segoe UI Variable / Segoe UI，中文回落雅黑。</summary>
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei, sans-serif");

        /// <summary>等宽：id / sha256 / 命令行。</summary>
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace");

        // —— 外壳尺寸（需求 S2）：默认 1600×1000、最小 1100×700、侧栏 240–260 ——
        public const double WinDefaultW = 1600;
        public const double WinDefaultH = 1000;
        public const double WinMinW = 1100;
        public const double WinMinH = 700;
        public const double NavW = 248;

        /// <summary>
        /// 四档断点（需求：1100–1300 Compact / 1300–1600 Standard / 1600–2200 Wide / 2200+ UltraWide）。
        /// 返回 0..3；调用方按档位**重排**内容，而不是缩放 UI。
        /// </summary>
        public static int TierFor(double width)
        {
            if (width < 1300) return 0;
            if (width < 1600) return 1;
            if (width < 2200) return 2;
            return 3;
        }

        /// <summary>该档位下 Home 预览的最小高度（需求：≥420 / Standard 480–560 / Wide 500–650）。</summary>
        public static double PreviewHeightFor(int tier)
        {
            if (tier <= 0) return 420;
            if (tier == 1) return 500;
            return 560;
        }

        /// <summary>该档位下内容区最大宽度；宽屏要真的放开，不锁在 1000 左右。</summary>
        public static double ContentMaxFor(int tier)
        {
            if (tier <= 0) return 1100;
            if (tier == 1) return 1400;
            return 1600;
        }
    }

    /// <summary>控件工厂。窗口里只用这些，不自己 Set 一堆属性。</summary>
    internal static class Ui
    {
        public static TextBlock Text(string text, double size, Brush color)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = size;
            block.Foreground = color;
            block.FontFamily = Theme.Font;
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }

        public static TextBlock Strong(string text, double size, Brush color)
        {
            TextBlock block = Text(text, size, color);
            block.FontWeight = FontWeights.SemiBold;
            return block;
        }

        public static TextBlock Mono(string text, double size, Brush color)
        {
            TextBlock block = Text(text, size, color);
            block.FontFamily = Theme.Mono;
            return block;
        }

        /// <summary>小节标题（需求：18–22px）。</summary>
        public static TextBlock Section(string text)
        {
            TextBlock block = Strong(text, Theme.TH2, Theme.Fg);
            block.Margin = new Thickness(0, 0, 0, Theme.S3);
            return block;
        }

        /// <summary>分组小标签（侧栏分组名那种）。故意不用标题元素，避免打乱标题层级。</summary>
        public static TextBlock GroupLabel(string text)
        {
            TextBlock block = Text(text, Theme.TMicro, Theme.Fg3);
            block.FontWeight = FontWeights.SemiBold;
            block.Margin = new Thickness(0, 0, 0, Theme.S2);
            return block;
        }

        public static Border Card(UIElement content)
        {
            Border border = new Border();
            border.Background = Theme.Card;
            border.BorderBrush = Theme.Line;
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(Theme.R3);
            border.Padding = new Thickness(Theme.S5);
            border.Child = content;
            return border;
        }

        public static Border Panel(UIElement content, Brush background)
        {
            Border border = new Border();
            border.Background = background;
            border.BorderBrush = Theme.Line;
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(Theme.R3);
            border.Padding = new Thickness(Theme.S5);
            border.Child = content;
            return border;
        }

        /// <summary>一像素分隔线。</summary>
        public static Border Divider()
        {
            Border line = new Border();
            line.Height = 1;
            line.Background = Theme.Line;
            line.Margin = new Thickness(0, Theme.S4, 0, Theme.S4);
            return line;
        }

        public static StackPanel Stack(bool horizontal, double gap)
        {
            StackPanel panel = new StackPanel();
            panel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
            if (horizontal) panel.Margin = new Thickness(0);
            panel.Tag = gap;   // 由 Gap() 使用，避免每个调用点自己算间距
            return panel;
        }

        /// <summary>按固定间距往 StackPanel 里加子项（自动插入间隔）。</summary>
        public static void Add(StackPanel panel, UIElement child, bool first)
        {
            double gap = panel.Tag is double ? (double)panel.Tag : Theme.S3;
            if (!first && panel.Orientation == Orientation.Vertical)
                child.SetValue(FrameworkElement.MarginProperty, new Thickness(0, gap, 0, 0));
            else if (!first)
                child.SetValue(FrameworkElement.MarginProperty, new Thickness(gap, 0, 0, 0));
            panel.Children.Add(child);
        }

        /// <summary>状态点 + 文字（System Status 里那一列）。</summary>
        public static StackPanel StatusDot(Brush tone, string label)
        {
            StackPanel row = Stack(horizontal: true, gap: Theme.S2);
            Ellipse dot = new Ellipse();
            dot.Width = 7;
            dot.Height = 7;
            dot.Fill = tone;
            dot.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(dot);
            TextBlock text = Text(label, Theme.TBody, Theme.Fg2);
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            return row;
        }

        /// <summary>事实行：左标签（灰）+ 右值。用于分辨率/时长/路径这类。</summary>
        public static Grid Fact(string label, UIElement value)
        {
            Grid grid = new Grid();
            ColumnDefinition left = new ColumnDefinition();
            left.Width = new GridLength(150);
            ColumnDefinition right = new ColumnDefinition();
            right.Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions.Add(left);
            grid.ColumnDefinitions.Add(right);
            grid.ColumnDefinitions.Add(new ColumnDefinition());   // 占位，保证右侧不被压缩

            TextBlock key = Text(label, Theme.TMeta, Theme.Fg3);
            Grid.SetColumn(key, 0);
            grid.Children.Add(key);

            value.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            return grid;
        }

        public static Grid Fact(string label, string value)
        {
            return Fact(label, Text(value, Theme.TBody, Theme.Fg));
        }

        public static Button Button(string text, bool primary, RoutedEventHandler onClick)
        {
            Button button = new Button();
            button.Content = text;
            button.FontFamily = Theme.Font;
            button.FontSize = Theme.TBody;
            button.Padding = new Thickness(Theme.S4, Theme.S2, Theme.S4, Theme.S2);
            button.MinWidth = 92;
            button.Cursor = System.Windows.Input.Cursors.Hand;
            button.BorderThickness = new Thickness(1);
            button.Foreground = primary ? Brushes.White : Theme.Fg;
            button.Background = primary ? Theme.Brand : Theme.Card;
            button.BorderBrush = primary ? Theme.Brand : Theme.LineStrong;
            if (onClick != null) button.Click += onClick;
            return button;
        }

        public static Button GhostButton(string text, RoutedEventHandler onClick)
        {
            Button button = Button(text, false, onClick);
            button.Background = Brushes.Transparent;
            button.BorderBrush = Brushes.Transparent;
            button.Foreground = Theme.Fg2;
            return button;
        }

        /// <summary>
        /// 四态块（loading / empty / error / success）。所有页面都必须给出这四种里的对应一种，
        /// 而不是留一块空白 —— 这是需求 §31 的硬要求。
        /// </summary>
        public static Border State(string title, string detail, Brush tone)
        {
            StackPanel panel = Stack(false, Theme.S2);
            TextBlock head = Strong(title, Theme.TBody, Theme.Fg);
            panel.Children.Add(head);
            if (detail != null && detail.Length > 0)
            {
                TextBlock body = Text(detail, Theme.TMeta, Theme.Fg2);
                body.Margin = new Thickness(0, Theme.S1, 0, 0);
                panel.Children.Add(body);
            }
            Border border = new Border();
            border.Background = Theme.Bg2;
            border.BorderBrush = tone == null ? Theme.LineStrong : tone;
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(Theme.R3);
            border.Padding = new Thickness(Theme.S5);
            border.Child = panel;
            return border;
        }

        /// <summary>侧栏项。current 为真时用品牌色竖条标出当前页。</summary>
        public static Border NavItem(string text, bool current, RoutedEventHandler onClick)
        {
            TextBlock label = Text(text, Theme.TBody, current ? Theme.Fg : Theme.Fg2);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(Theme.S3, 0, Theme.S3, 0);

            Border item = new Border();
            item.CornerRadius = new CornerRadius(Theme.R1);
            item.Padding = new Thickness(0, Theme.S2, 0, Theme.S2);
            item.Background = current ? Theme.BrandDim : Brushes.Transparent;
            item.BorderThickness = new Thickness(current ? 2 : 0, 0, 0, 0);
            item.BorderBrush = Theme.Brand;
            item.Child = label;
            item.Cursor = System.Windows.Input.Cursors.Hand;
            item.Tag = onClick;
            item.MouseLeftButtonUp += NavItemClick;
            item.MouseEnter += NavItemEnter;
            item.MouseLeave += NavItemLeave;
            return item;
        }

        private static void NavItemClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            Border item = sender as Border;
            if (item == null) return;
            RoutedEventHandler handler = item.Tag as RoutedEventHandler;
            if (handler != null) handler(item, new RoutedEventArgs());
        }

        private static void NavItemEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            Border item = sender as Border;
            if (item != null && item.BorderThickness.Left == 0) item.Background = Theme.Raise;
        }

        private static void NavItemLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            Border item = sender as Border;
            if (item != null && item.BorderThickness.Left == 0) item.Background = Brushes.Transparent;
        }

        /// <summary>窗口统一底：深色背景 + 系统字体。所有新窗口都从这里开始。</summary>
        public static void ApplyWindow(Window window)
        {
            window.Background = Theme.Bg;
            window.Foreground = Theme.Fg;
            window.FontFamily = Theme.Font;
            window.FontSize = Theme.TBody;
            window.UseLayoutRounding = true;
            window.SnapsToDevicePixels = true;
        }
    }
}
