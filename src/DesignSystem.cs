// DesignSystem.cs —— 统一视觉语言与控件库
//
// 为什么要有这个文件（而不是继续在 Shell.cs 里拼控件）：
//
//   改造前的界面问题不是"某个按钮太丑"，而是**每个页面各自拼控件**：
//   首页有 PrimaryButton/SecondaryButton/GhostButton/PlayerButton，
//   动画库用 Ui.Button，社区页又手搓一遍，System Status 是一整块黑卡。
//   结果是同类操作在不同页面长得不一样 —— 这正是"像内部工具"的核心成因。
//   成熟软件的共同点不是某个特效，而是**同类东西永远长一样**。
//
// 所以这里把"按钮 / 徽章 / 元信息 / 小节 / 状态行 / 卡片 / 对话框 / 通知"
// 全部收敛成唯一实现，页面只负责组合。
//
// 关于"Fluent"的诚实说明：目标是 .NET Framework 4.x + WPF（csc 编译，无第三方依赖），
// 所以**没有** Composition API / Acrylic / Reveal / Mica。这些是 WinUI 3 才有的。
// 这里能做到的是 Fluent 的**排版、层级、间距、克制动效**，再加上自己的暗色表面系统。
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员、无 XAML。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace BootAnimation
{
    /// <summary>按钮的主次级别。三档必须能一眼分辨，不允许长得一样。</summary>
    internal enum ActionLevel
    {
        /// <summary>页面上唯一最重的动作（如 Preview Boot）。</summary>
        Primary = 0,
        /// <summary>次要动作（如 Apply）。</summary>
        Secondary = 1,
        /// <summary>三级动作 / 图标按钮（如 Random、More）。</summary>
        Tertiary = 2,
        /// <summary>危险动作（如移除、卸载）。</summary>
        Danger = 3,
    }

    internal static class Ds
    {
        // ═══════════════════════════════════════════════════════════ 文本

        /// <summary>正文。</summary>
        internal static TextBlock Body(string text)
        {
            return Text(text, Theme.TBody, Theme.Fg, FontWeights.Normal);
        }

        /// <summary>次要正文。</summary>
        internal static TextBlock BodyMuted(string text)
        {
            return Text(text, Theme.TBody, Theme.Fg2, FontWeights.Normal);
        }

        /// <summary>
        /// 全大写小标签。**不做假字间距** ——
        /// WPF 4 的 `TextBlock` 没有 `CharacterSpacing`（那是 UWP / WinUI 的属性）。
        /// 手工往 `Inlines` 里逐字插空格是能凑出效果，但复制出来的文本会带空格，
        /// 而且中文标签会变成难看的"开 机 动 画"。所以这里只靠字号 / 字重 / 颜色
        /// 建立层级 —— 这是 WPF 下唯一诚实的做法。
        /// </summary>
        internal static TextBlock Label(string text, Brush color)
        {
            return Text(text.ToUpperInvariant(), Theme.TMicro, color == null ? Theme.Fg3 : color,
                FontWeights.SemiBold);
        }

        internal static TextBlock Section(string text)
        {
            return Text(text, Theme.TSection, Theme.Fg, FontWeights.SemiBold);
        }

        /// <summary>Hero 标题：当前动画的名字，页面上最大的字。</summary>
        internal static TextBlock HeroTitle(string text)
        {
            TextBlock t = Text(text, Theme.THero, Theme.Fg, FontWeights.SemiBold);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            return t;
        }

        internal static TextBlock PageTitle(string text)
        {
            TextBlock t = Text(text, Theme.TTitle, Theme.Fg, FontWeights.SemiBold);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            return t;
        }

        internal static TextBlock Text(string text, double size, Brush color, FontWeight weight)
        {
            TextBlock block = new TextBlock();
            block.Text = text == null ? "" : text;
            block.FontSize = size;
            block.Foreground = color;
            block.FontFamily = Theme.Font;
            block.FontWeight = weight;
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }

        // ═══════════════════════════════════════════════════════════ 布局

        /// <summary>竖排容器。</summary>
        internal static StackPanel Col(double gap)
        {
            StackPanel p = new StackPanel();
            p.Orientation = Orientation.Vertical;
            p.Tag = gap;
            return p;
        }

        /// <summary>横排容器。</summary>
        internal static StackPanel Bar(double gap)
        {
            StackPanel p = new StackPanel();
            p.Orientation = Orientation.Horizontal;
            p.Tag = gap;
            return p;
        }

        /// <summary>按固定间距追加子项（自动插入间隔，不用每个调用点自己算）。</summary>
        internal static void Add(StackPanel panel, UIElement child)
        {
            double gap = panel.Tag is double ? (double)panel.Tag : Theme.S3;
            if (panel.Children.Count > 0)
            {
                child.SetValue(FrameworkElement.MarginProperty, panel.Orientation == Orientation.Horizontal
                    ? new Thickness(gap, 0, 0, 0)
                    : new Thickness(0, gap, 0, 0));
            }
            panel.Children.Add(child);
        }

        /// <summary>可伸缩的空白（把后面的内容推到右边）。</summary>
        internal static Border Spacer()
        {
            Border b = new Border();
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.VerticalAlignment = VerticalAlignment.Stretch;
            return b;
        }

        /// <summary>一个像素的分隔线，上下留白。</summary>
        internal static Border Divider(double topGap, double bottomGap)
        {
            Border line = new Border();
            line.Height = 1;
            line.Background = Theme.Line;
            line.Margin = new Thickness(0, topGap, 0, bottomGap);
            return line;
        }

        /// <summary>带上下留白的块（页面里代替"到处写 Margin"）。</summary>
        internal static Border Block(UIElement content, double top, double bottom)
        {
            Border b = new Border();
            b.Margin = new Thickness(0, top, 0, bottom);
            b.Child = content;
            return b;
        }

        /// <summary>
        /// 页面内容的标准容器：左右留白 48、上下 32。
        ///
        /// 留白是"看起来像专业软件"最直接的一条：内部工具往往把控件铺满整屏，
        /// 而成熟软件舍得给边距。
        /// </summary>
        internal static ScrollViewer Page(UIElement content)
        {
            StackPanel pad = new StackPanel();
            pad.Margin = new Thickness(Theme.S7, Theme.S6, Theme.S7, Theme.S8);
            pad.Children.Add(content);
            ScrollViewer scroller = new ScrollViewer();
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroller.Content = pad;
            return scroller;
        }

        // ═══════════════════════════════════════════════════════════ 按钮

        /// <summary>
        /// 统一按钮。**三档主次必须一眼可辨**：
        ///   Primary   —— 实心强调色，页面上只应出现一个
        ///   Secondary —— 实色表面 + 描边
        ///   Tertiary  —— 透明，只有悬停时才出现底色
        /// </summary>
        internal static Button Action(string text, ActionLevel level, Action onClick)
        {
            Button b = new Button();
            b.Content = text;
            b.FontFamily = Theme.Font;
            b.FontSize = level == ActionLevel.Primary ? Theme.TBody : Theme.TMeta;
            b.FontWeight = level == ActionLevel.Primary ? FontWeights.SemiBold : FontWeights.Normal;
            b.Cursor = Cursors.Hand;
            b.Padding = level == ActionLevel.Primary
                ? new Thickness(Theme.S5, Theme.S3, Theme.S5, Theme.S3)
                : new Thickness(Theme.S4, Theme.S2 + 2, Theme.S4, Theme.S2 + 2);
            b.MinHeight = level == ActionLevel.Primary ? 46 : 38;
            b.BorderThickness = new Thickness(1);

            if (level == ActionLevel.Primary)
            {
                b.Foreground = Brushes.White;
                b.Background = Accent.Brush;
                b.BorderBrush = Accent.Brush;
            }
            else if (level == ActionLevel.Secondary)
            {
                b.Foreground = Theme.Fg;
                b.Background = Theme.Raise;
                b.BorderBrush = Theme.LineStrong;
            }
            else if (level == ActionLevel.Danger)
            {
                b.Foreground = Theme.Danger;
                b.Background = Brushes.Transparent;
                b.BorderBrush = Theme.Line;
            }
            else
            {
                b.Foreground = Theme.Fg2;
                b.Background = Brushes.Transparent;
                b.BorderBrush = Brushes.Transparent;
            }

            // **必须换掉默认模板**。
            //
            // WPF 默认的 Button 模板在 IsEnabled=false 时会去画 Aero 的系统浅灰底 ——
            // 在深色界面上就是一块刺眼的白方块（实测截图里 Apply 按钮就是那样）。
            // 设置 Background 是**压不住**它的，因为那是模板自己画的。
            // 所以这里给一个最小模板：只画我们自己的底 + 内容，禁用只降不透明度。
            b.Template = ButtonTemplate();

            if (onClick != null) b.Click += delegate { onClick(); };
            HoverFill(b, level);
            return b;
        }

        /// <summary>最小按钮模板：Border 画底，ContentPresenter 放内容。</summary>
        private static ControlTemplate ButtonTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Root";
            border.SetValue(Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty,
                new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Theme.RButton));

            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.MarginProperty,
                new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(content);

            template.VisualTree = border;

            // 禁用：只降不透明度，绝不换成系统配色
            Trigger off = new Trigger();
            off.Property = UIElement.IsEnabledProperty;
            off.Value = false;
            off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "Root"));
            template.Triggers.Add(off);

            return template;
        }

        /// <summary>小型图标按钮（播放控制条用）。方形、无文字、悬停才显形。</summary>
        internal static Button IconAction(string glyph, string tooltip, Action onClick)
        {
            Button b = new Button();
            b.Content = glyph;
            b.FontFamily = Theme.Font;
            b.FontSize = 14;
            b.Foreground = Theme.Fg;
            b.Background = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Width = 32;
            b.Height = 32;
            b.Padding = new Thickness(0);
            b.Cursor = Cursors.Hand;
            if (!string.IsNullOrEmpty(tooltip)) ToolTipService.SetToolTip(b, tooltip);
            if (onClick != null) b.Click += delegate { onClick(); };
            b.MouseEnter += delegate { b.Background = Theme.Raise; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            // 属性都设完之后再挂模板，避免"模板先绑、属性后改"的阅读顺序陷阱
            b.Template = ButtonTemplate();
            return b;
        }

        private static void HoverFill(Button b, ActionLevel level)
        {
            Brush baseBg = b.Background;
            b.MouseEnter += delegate
            {
                if (!b.IsEnabled) return;
                if (level == ActionLevel.Primary) b.Background = Accent.Hover;
                else if (level == ActionLevel.Secondary) b.Background = Theme.Card;
                else b.Background = Theme.Raise;
            };
            b.MouseLeave += delegate { b.Background = baseBg; };
        }

        // ═══════════════════════════════════════════════════════════ 徽章 / 标签

        /// <summary>状态徽章（ACTIVE / 已安装 / 未安装）。描边 + 同色文字，不用实心块。</summary>
        internal static Border Badge(string text, Brush tone)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(Theme.RButton);
            b.BorderBrush = tone;
            b.BorderThickness = new Thickness(1);
            b.Background = Brushes.Transparent;
            b.Padding = new Thickness(Theme.S2 + 2, 3, Theme.S2 + 2, 3);
            TextBlock t = Label(text, tone);
            b.Child = t;
            return b;
        }

        /// <summary>实心强调徽章（只用于"当前生效"这一处，页面上最多一个）。</summary>
        internal static Border BadgeSolid(string text)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(Theme.RButton);
            b.Background = Accent.Brush;
            b.Padding = new Thickness(Theme.S2 + 2, 3, Theme.S2 + 2, 3);
            b.Child = Label(text, Brushes.White);
            return b;
        }

        // ═══════════════════════════════════════════════════════════ 元信息

        /// <summary>
        /// 一个元信息字段：小标签在上、值在下。
        ///
        /// 值拿不到时**整格不显示**，而不是写"未知"占位 —— 一排"未知"比少一项更像工具。
        /// </summary>
        internal static StackPanel MetaField(string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            StackPanel cell = Col(0);
            cell.Children.Add(Label(label, Theme.Fg3));
            TextBlock v = Text(value, Theme.TBody, Theme.Fg, FontWeights.Normal);
            v.Margin = new Thickness(0, 3, 0, 0);
            cell.Children.Add(v);
            return cell;
        }

        /// <summary>一排元信息。用固定右间距，避免水平排布时 Margin 语义错位。</summary>
        internal static StackPanel MetaRow(params StackPanel[] fields)
        {
            StackPanel row = Bar(0);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i] == null) continue;
                fields[i].Margin = new Thickness(0, 0, Theme.S7, 0);
                row.Children.Add(fields[i]);
            }
            return row;
        }

        // ═══════════════════════════════════════════════════════════ 状态行

        /// <summary>
        /// 一行系统状态：● 标签 值。
        ///
        /// **不用大黑卡包起来** —— 这是"System Status 压过主视觉"的成因。
        /// 去掉外框、缩小字号、靠对齐形成秩序，它才会退到该有的位置。
        /// </summary>
        internal static Grid StatusRow(string glyph, Brush tone, string label, string value, string detail)
        {
            Grid row = new Grid();
            ColumnDefinition c0 = new ColumnDefinition();
            c0.Width = new GridLength(18);
            ColumnDefinition c1 = new ColumnDefinition();
            c1.Width = new GridLength(210);
            ColumnDefinition c2 = new ColumnDefinition();
            c2.Width = new GridLength(1, GridUnitType.Star);
            row.ColumnDefinitions.Add(c0);
            row.ColumnDefinitions.Add(c1);
            row.ColumnDefinitions.Add(c2);
            row.Margin = new Thickness(0, 0, 0, Theme.S2 + 2);

            TextBlock g = Text(glyph, Theme.TMicro, tone, FontWeights.Normal);
            g.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(g, 0);
            row.Children.Add(g);

            TextBlock l = Text(label, Theme.TMeta, Theme.Fg3, FontWeights.Normal);
            l.VerticalAlignment = VerticalAlignment.Center;
            l.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(l, 1);
            row.Children.Add(l);

            TextBlock v = Text(value, Theme.TMeta, tone, FontWeights.Normal);
            v.VerticalAlignment = VerticalAlignment.Center;
            v.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(v, 2);
            if (!string.IsNullOrEmpty(detail)) ToolTipService.SetToolTip(v, detail);
            row.Children.Add(v);
            return row;
        }

        // ═══════════════════════════════════════════════════════════ 状态块

        /// <summary>
        /// 空 / 加载 / 错误 / 成功四态块。
        /// 所有页面都必须给出四态之一，不允许留空白 —— 这是硬要求。
        /// </summary>
        internal static Border State(string title, string detail, Brush tone)
        {
            StackPanel panel = Col(Theme.S2);
            panel.Children.Add(Text(title, Theme.TBody, Theme.Fg, FontWeights.SemiBold));
            if (!string.IsNullOrEmpty(detail))
            {
                TextBlock d = Text(detail, Theme.TMeta, Theme.Fg2, FontWeights.Normal);
                d.Margin = new Thickness(0, Theme.S1, 0, 0);
                panel.Children.Add(d);
            }
            Border b = new Border();
            b.Background = Theme.Bg2;
            b.BorderBrush = tone == null ? Theme.Line : tone;
            b.BorderThickness = new Thickness(1);
            b.CornerRadius = new CornerRadius(Theme.RCard);
            b.Padding = new Thickness(Theme.S5);
            b.Child = panel;
            return b;
        }

        /// <summary>一段带标题的分组（System Status 那种），去掉重外框，只有一条细分隔线。</summary>
        internal static StackPanel Group(string heading, UIElement body)
        {
            StackPanel g = Col(Theme.S3);
            TextBlock h = Label(heading, Theme.Fg3);
            h.Margin = new Thickness(0, 0, 0, Theme.S3);
            g.Children.Add(h);
            g.Children.Add(body);
            return g;
        }

        // ═══════════════════════════════════════════════════════════ 动效

        /// <summary>淡入到目标透明度（120 / 200 / 320 三档，不做弹跳）。</summary>
        internal static void Fade(UIElement target, double to, int ms)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            QuadraticEase ease = new QuadraticEase();
            ease.EasingMode = EasingMode.EaseOut;
            a.EasingFunction = ease;
            target.BeginAnimation(UIElement.OpacityProperty, a);
        }

        /// <summary>缩放到指定倍数（卡片悬停最多 1.02）。</summary>
        internal static void ScaleTo(ScaleTransform target, double to, int ms)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            QuadraticEase ease = new QuadraticEase();
            ease.EasingMode = EasingMode.EaseOut;
            a.EasingFunction = ease;
            target.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            target.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        /// <summary>轻微位移（页面切换用，位移量很小，不做滑入）。</summary>
        internal static void SlideTo(TranslateTransform target, double to, int ms)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            QuadraticEase ease = new QuadraticEase();
            ease.EasingMode = EasingMode.EaseOut;
            a.EasingFunction = ease;
            target.BeginAnimation(TranslateTransform.YProperty, a);
        }
    }

    /// <summary>
    /// 强调色（Accent）。
    ///
    /// 需求：Accent 由**当前动画**决定，且只允许用在 Active 状态 / Primary 按钮 /
    /// Progress / Focus / 小 Indicator 上 —— 不允许整屏变红。
    ///
    /// 这里不做真实的取色（WPF 4 没有可靠的取色路径，硬做只能靠采样一帧再算主色，
    /// 那会带来一次额外解码）。当前实现用一组**克制的**候选色，由动画来源/id 稳定映射 ——
    /// 稳定比"智能"重要：同一段动画每次打开必须是同一个颜色，否则界面会显得飘忽。
    /// 默认是很克制的深红（品牌色）与冷白，不出现霓虹、紫蓝渐变、RGB。
    /// </summary>
    internal static class Accent
    {
        private static readonly SolidColorBrush Default = Frozen(0xd1, 0x1d, 0x2e);
        private static SolidColorBrush current;

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            SolidColorBrush b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
            b2.Freeze();
            return b2;
        }

        /// <summary>当前强调色。</summary>
        internal static SolidColorBrush Brush
        {
            get { return current ?? Default; }
        }

        /// <summary>悬停态（比强调色亮一档）。</summary>
        internal static SolidColorBrush Hover
        {
            get
            {
                SolidColorBrush b = Brush;
                Color c = b.Color;
                return new SolidColorBrush(Color.FromRgb(
                    (byte)Math.Min(255, c.R + 18),
                    (byte)Math.Min(255, c.G + 14),
                    (byte)Math.Min(255, c.B + 14)));
            }
        }

        /// <summary>低透明度版本（Active 背景、Focus 光晕）。</summary>
        internal static SolidColorBrush Dim
        {
            get
            {
                Color c = Brush.Color;
                return new SolidColorBrush(Color.FromArgb(28, c.R, c.G, c.B));
            }
        }

        /// <summary>当前动画的 id 变了就重算一次强调色。</summary>
        internal static void Use(AnimationInfo info)
        {
            current = Choose(info);
        }

        private static SolidColorBrush Choose(AnimationInfo info)
        {
            if (info == null) return Default;
            // 用 id 的稳定散列在候选色里挑一个。候选色全部是低饱和深色系：
            // 深红 / 琥珀 / 苔绿 / 石板蓝 / 灰紫。没有霓虹、没有高饱和青。
            int h = 0;
            string key = info.Id == null ? "" : info.Id;
            for (int i = 0; i < key.Length; i++) h = unchecked(h * 31 + key[i]);
            int pick = Math.Abs(h) % 5;
            if (pick == 0) return Frozen(0xd1, 0x1d, 0x2e);   // 深红（默认/品牌）
            if (pick == 1) return Frozen(0xb0, 0x6a, 0x1f);   // 琥珀
            if (pick == 2) return Frozen(0x3f, 0x7a, 0x52);   // 苔绿
            if (pick == 3) return Frozen(0x3d, 0x5a, 0x8c);   // 石板蓝
            return Frozen(0x6b, 0x4f, 0x7a);                  // 灰紫
        }
    }
}
