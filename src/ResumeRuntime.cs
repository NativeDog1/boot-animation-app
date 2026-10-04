// ResumeRuntime.cs —— 睡眠/休眠唤醒时的播放链路
//
// 为什么这是一条**独立**的链路，而不是"开机动画顺便管一下"：
//
//   冷启动和睡眠恢复在 Windows 里是两件完全不同的事。冷启动会重新跑固件与
//   Windows Boot Manager；而 S3/S4 恢复是**内存镜像直接还原**，不会重新执行 UEFI，
//   也不会重新跑登录流程。所以"开机动画"那一套在唤醒时根本不会被触发 ——
//   想让唤醒也播，就必须有一个进程在睡眠期间还活着，并监听电源事件。
//
//   这正是为什么 Startup 面板里必须把三件事分开显示：
//     1. Launch with Windows（登录时拉起）
//     2. Resume Animation（唤醒时播放）—— 本文件
//     3. UEFI Boot Animation（冷启动）—— 尚未实现
//   把三者合成一个开关，用户就无法知道"我开了到底哪一条"。
//
// 监听的事件（Windows 官方定义的 WM_POWERBROADCAST 子类型）：
//   PBT_APMRESUMEAUTOMATIC (0x0012) —— 系统自动恢复（最常见，含现代待机 S0）
//   PBT_APMRESUMESUSPEND    (0x0007) —— 因用户操作恢复
//   PBT_APMSUSPEND          (0x0004) —— 即将睡眠（用于记录状态）
//   三者都要处理：只监听 SUSPEND 会漏掉自动恢复，只监听 RESUME 会在某些机型上不触发。
//
// 本进程退出后就不再监听 —— 这是如实的限制，界面上"Resume Animation"一项会说明它。
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员。

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;

namespace BootAnimation
{
    internal static class ResumeRuntime
    {
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_APMSUSPEND = 0x0004;
        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

        private static HwndSource source;
        private static DateTime lastResume = DateTime.MinValue;

        /// <summary>最近一次唤醒的时刻；没发生过返回 MinValue。</summary>
        internal static DateTime LastResume { get { return lastResume; } }

        internal static bool IsListening { get { return source != null; } }

        /// <summary>
        /// 开始监听电源事件。
        ///
        /// 用一个**不可见的消息窗口**接 WM_POWERBROADCAST：WPF 的 Window 不适合做这个
        /// （它会被用户看见、会进任务栏、关闭时机不受控），而 HwndSource 只建一个纯消息
        /// 窗口，生命周期完全由我们控制。
        ///
        /// 必须在 UI 线程上调用（要建窗口句柄）。
        /// </summary>
        internal static bool Start()
        {
            if (source != null) return true;
            try
            {
                HwndSourceParameters p = new HwndSourceParameters("dsb-resume-listener");
                p.Width = 0;
                p.Height = 0;
                p.PositionX = 0;
                p.PositionY = 0;
                p.WindowStyle = 0;
                p.ParentWindow = IntPtr.Zero;
                source = new HwndSource(p);
                source.AddHook(Hook);
                Diagnostics.SetResumeReady(true);
                Program.Log("ResumeRuntime: 已开始监听电源事件（自动恢复 / 用户恢复 / 即将睡眠）");
                return true;
            }
            catch (Exception ex)
            {
                // 监听不上不是致命问题：只是唤醒时不播而已。如实记录并让界面显示未就绪。
                Program.Log("ResumeRuntime: 监听电源事件失败: " + ex.Message);
                Diagnostics.SetResumeReady(false);
                return false;
            }
        }

        internal static void Stop()
        {
            try
            {
                if (source != null)
                {
                    source.RemoveHook(Hook);
                    source.Dispose();
                    source = null;
                }
            }
            catch { }
            Diagnostics.SetResumeReady(false);
        }

        private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_POWERBROADCAST) return IntPtr.Zero;
            int kind = wParam.ToInt32();

            if (kind == PBT_APMSUSPEND)
            {
                Program.Log("ResumeRuntime: 系统即将进入睡眠");
                return IntPtr.Zero;
            }

            if (kind == PBT_APMRESUMEAUTOMATIC || kind == PBT_APMRESUMESUSPEND)
            {
                lastResume = DateTime.Now;
                Program.Log("ResumeRuntime: 检测到唤醒（"
                    + (kind == PBT_APMRESUMEAUTOMATIC ? "自动恢复" : "用户恢复") + "）");
                // 唤醒处理**必须**丢到下一个消息循环里做：这个钩子是在电源广播的
                // 同步路径上被调用的，在这里建窗口/开媒体会拖住整个电源转换。
                try
                {
                    if (Application.Current != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Background,
                            new Action(OnResume));
                    }
                }
                catch (Exception ex) { Program.Log("ResumeRuntime: 唤醒处理派发失败: " + ex.Message); }
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 唤醒后要做什么。
        ///
        /// 刻意**不**在这里直接开一个全屏播放窗口：
        ///   · 用户刚从睡眠回来，最想要的是看到桌面；
        ///   · 强制弹全屏会让"唤醒"变成"被打断"。
        /// 所以这里只把"刚发生过唤醒"这件事记下来并广播给界面 —— 界面据此把
        /// Resume 状态点亮。真正"唤醒也自动全屏播一段"是一个产品开关，需要用户明确打开，
        /// 而当前版本还没有这个开关，所以不做。
        ///
        /// 这一段的取舍写在这里，是因为"唤醒了但没播"看起来像 bug，其实是设计：
        /// 有开关之后才是功能。
        /// </summary>
        private static void OnResume()
        {
            try
            {
                if (Application.Current != null)
                {
                    Window main = Application.Current.MainWindow;
                    Shell shell = main as Shell;
                    if (shell != null) shell.NotifyResumed(lastResume);
                }
            }
            catch (Exception ex) { Program.Log("ResumeRuntime: 通知界面失败: " + ex.Message); }
        }

        /// <summary>把唤醒立刻播一次（供界面的 "Preview Resume" 按钮用）。</summary>
        internal static void PlayResumeNow()
        {
            BootRequest request = new BootRequest();
            request.Mode = BootMode.Resume;
            request.Seconds = 0;
            request.Mute = false;
            // 直接在后台线程起一个新进程来播：复用"播放"那条唯一链路（BootRuntime），
            // 而不是在 UI 进程里再建一套播放窗口。这样"开机怎么播，唤醒就怎么播"，
            // 不会出现两条播放代码各自演化。
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    Program.ExePath, "--resume");
                psi.UseShellExecute = false;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { Program.Log("ResumeRuntime: 启动唤醒预览失败: " + ex.Message); }
        }
    }
}
