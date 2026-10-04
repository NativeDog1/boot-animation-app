// Notifications.cs —— 现代应用内通知与确认对话框
//
// 为什么必须换掉原来的错误窗口：
//
//   原来播放失败时屏幕上出现的是一个**纯文字窗口**："System failed to start.
//   Please check your hardware."。这句话有两个问题：
//     1. 它是**猜的** —— 播放失败可能是编解码器不支持、文件损坏、路径失效、
//        显存不足，而"check your hardware"把用户指向了几乎肯定无关的方向；
//     2. 它**没有任何可执行的动作** —— 用户看完只能关掉，然后呢？
//
// 所以这里的规矩是：
//     · 文案由**真实检测结果**生成（调用方传进来的是具体原因，不是套话）
//     · 每个通知都必须带**下一步动作**（Retry / Repair / Diagnostics / 打开日志）
//     · 出现在应用内，不是系统级 MessageBox
//
// 编译约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员、无 XAML。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BootAnimation
{
    /// <summary>通知的严重级别。只影响左侧那条色带与图标，不影响布局。</summary>
    internal enum NoticeLevel
    {
        Info = 0,
        Success = 1,
        Warning = 2,
        Error = 3,
    }

    /// <summary>一个可执行动作。</summary>
    internal sealed class NoticeAction
    {
        public string Text;
        public ActionLevel Level;
        public Action Run;
        /// <summary>点完之后要不要把通知关掉。默认关（动作通常一次性）。</summary>
        public bool KeepsOpen;

        public NoticeAction(string text, ActionLevel level, Action run)
        {
            Text = text;
            Level = level;
            Run = run;
        }

        /// <summary>第四个参数是"点完不关闭"，用于"跳到另一个页面继续看"这类动作。</summary>
        public NoticeAction(string text, ActionLevel level, Action run, bool keepsOpen)
        {
            Text = text;
            Level = level;
            Run = run;
            KeepsOpen = keepsOpen;
        }
    }

    /// <summary>
    /// 应用内通知层。挂在主窗口上，从右上角滑入。
    ///
    /// 刻意**不做**自动消失（除非调用方指定）：错误信息自己跑掉是用户最讨厌的一类交互，
    /// 尤其是它带着"还有一个按钮能修"的时候。
    /// </summary>
    internal sealed class NoticeHost : Grid
    {
        private readonly StackPanel stack = new StackPanel();
        private readonly List<Border> live = new List<Border>();

        internal NoticeHost()
        {
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(0, Theme.S6, Theme.S6, 0);
            IsHitTestVisible = true;
            Panel.SetZIndex(this, 100);
            stack.Orientation = Orientation.Vertical;
            stack.HorizontalAlignment = HorizontalAlignment.Right;
            Children.Add(stack);
        }

        internal Border Show(string title, string message, NoticeLevel level, NoticeAction[] actions)
        {
            Border card = new Border();
            card.Width = 420;
            card.Background = Theme.Card;
            card.BorderBrush = Theme.LineStrong;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RDialog);
            card.Margin = new Thickness(0, 0, 0, Theme.S3);
            card.Effect = DropShadow();

            Grid inner = new Grid();
            ColumnDefinition accentCol = new ColumnDefinition();
            accentCol.Width = new GridLength(3);
            ColumnDefinition bodyCol = new ColumnDefinition();
            bodyCol.Width = new GridLength(1, GridUnitType.Star);
            inner.ColumnDefinitions.Add(accentCol);
            inner.ColumnDefinitions.Add(bodyCol);

            Border accent = new Border();
            accent.Background = ToneOf(level);
            accent.CornerRadius = new CornerRadius(Theme.RDialog, 0, 0, Theme.RDialog);
            Grid.SetColumn(accent, 0);
            inner.Children.Add(accent);

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(Theme.S4, Theme.S4, Theme.S4, Theme.S4);

            TextBlock head = Ds.Label(title, ToneOf(level));
            body.Children.Add(head);

            TextBlock msg = Ds.Text(message, Theme.TMeta, Theme.Fg, FontWeights.Normal);
            msg.Margin = new Thickness(0, Theme.S2, 0, 0);
            body.Children.Add(msg);

            if (actions != null && actions.Length > 0)
            {
                StackPanel row = Ds.Bar(Theme.S2);
                row.Margin = new Thickness(0, Theme.S4, 0, 0);
                for (int i = 0; i < actions.Length; i++)
                {
                    NoticeAction a = actions[i];
                    Border captured = card;
                    Button btn = Ds.Action(a.Text, a.Level, delegate
                    {
                        try { if (a.Run != null) a.Run(); }
                        catch (Exception ex) { Program.Log("通知动作失败: " + ex.Message); }
                        if (!a.KeepsOpen) Dismiss(captured);
                    });
                    row.Children.Add(btn);
                }
                body.Children.Add(row);
            }

            Button close = Ds.IconAction("✕", "关闭", delegate { Dismiss(card); });
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Top;
            close.Margin = new Thickness(0, Theme.S2, Theme.S2, 0);
            close.Opacity = 0.6;
            Grid.SetColumn(close, 1);
            inner.Children.Add(close);

            Grid.SetColumn(body, 1);
            inner.Children.Add(body);
            card.Child = inner;

            // 从右侧滑入 + 淡入（200ms，不做弹跳）
            TranslateTransform slide = new TranslateTransform(24, 0);
            card.RenderTransform = slide;
            card.Opacity = 0;
            stack.Children.Add(card);
            live.Add(card);
            Ds.Fade(card, 1.0, Theme.AnimNormal);
            DoubleAnimation move = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Theme.AnimNormal));
            QuadraticEase ease = new QuadraticEase();
            ease.EasingMode = EasingMode.EaseOut;
            move.EasingFunction = ease;
            slide.BeginAnimation(TranslateTransform.XProperty, move);
            return card;
        }

        internal void Dismiss(Border card)
        {
            if (card == null) return;
            if (!live.Contains(card)) return;
            live.Remove(card);
            Ds.Fade(card, 0.0, Theme.AnimNormal);
            // 等淡出走完再移出视觉树，否则会突然消失
            System.Windows.Threading.DispatcherTimer t = new System.Windows.Threading.DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(Theme.AnimNormal + 30);
            t.Tick += delegate
            {
                t.Stop();
                try { stack.Children.Remove(card); } catch { }
            };
            t.Start();
        }

        internal void Clear()
        {
            stack.Children.Clear();
            live.Clear();
        }

        internal static Brush ToneOf(NoticeLevel level)
        {
            if (level == NoticeLevel.Success) return Theme.Ok;
            if (level == NoticeLevel.Warning) return Theme.Warn;
            if (level == NoticeLevel.Error) return Theme.Danger;
            return Theme.Info;
        }

        private static System.Windows.Media.Effects.DropShadowEffect DropShadow()
        {
            System.Windows.Media.Effects.DropShadowEffect e =
                new System.Windows.Media.Effects.DropShadowEffect();
            e.BlurRadius = 24;
            e.ShadowDepth = 8;
            e.Opacity = 0.45;
            e.Color = Colors.Black;
            return e;
        }
    }

    /// <summary>
    /// 模态确认对话框（比系统 MessageBox 更克制、可带多个动作）。
    /// 只用于需要"确认"的破坏性操作；错误提示一律走 NoticeHost（不打断操作）。
    /// </summary>
    internal static class ConfirmDialog
    {
        internal static bool Ask(Window owner, string title, string message,
            string confirmText, bool dangerous)
        {
            bool result = false;
            Window w = new Window();
            Ui.ApplyWindow(w);
            w.WindowStyle = WindowStyle.None;
            w.ResizeMode = ResizeMode.NoResize;
            w.Width = 480;
            w.SizeToContent = SizeToContent.Height;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.Owner = owner;
            w.Background = Theme.Card;
            w.AllowsTransparency = false;

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(Theme.S5, Theme.S5, Theme.S5, Theme.S5);
            body.Children.Add(Ds.Label(title, Theme.Fg3));

            TextBlock msg = Ds.Text(message, Theme.TBody, Theme.Fg, FontWeights.Normal);
            msg.Margin = new Thickness(0, Theme.S3, 0, 0);
            body.Children.Add(msg);

            StackPanel row = Ds.Bar(Theme.S2);
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(0, Theme.S5, 0, 0);

            Button cancel = Ds.Action("取消", ActionLevel.Tertiary, delegate { w.Close(); });
            Button ok = Ds.Action(confirmText, dangerous ? ActionLevel.Danger : ActionLevel.Primary,
                delegate { result = true; w.Close(); });
            row.Children.Add(cancel);
            row.Children.Add(ok);
            body.Children.Add(row);

            w.Content = body;
            w.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) w.Close();
                else if (e.Key == Key.Enter) { result = true; w.Close(); }
            };
            w.ShowDialog();
            return result;
        }
    }
}
