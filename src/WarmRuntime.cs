// WarmRuntime.cs —— 常驻预热进程
//
// ─────────────────────────────────────────────────────────────────────────────
// 为什么需要它（这是"登录那一刻就开始播"的唯一实现方式）
//
// 一次性进程的账本已经量清楚了：
//     boot-enter@0 → media-play-issued@153 → window-shown@251
//     → media-opened@524 → first-frame-confirmed@620 → 显形@621
// 那 620ms 里包含 CLR + WPF + 窗口创建 + 打开视频 + 解码。**它是客观存在的**，
// 只要动画由"登录后才被拉起来的进程"播放，就一定有这一段。
//
// 参考 Wallpaper Engine 的做法：它有一个进程**常驻在后台，一直养着解码器**，
// 你看到它的时候它只是把已经在跑的画面放上屏幕 —— 从来没有"启动 → 加载 → 再播放"。
//
// 所以这里做同样的事：
//
//   --warm      登录时启动。建一个**完全不可见的全屏窗口**，把当前选中的动画
//               加载并解码好，然后**一直停在那里等信号**。屏幕上什么都看不到。
//   --play      触发：给预热进程发一个命名事件，然后自己立刻退出。
//               预热进程收到事件后：把窗口不透明度和位置一改即可 —— 一帧之内完成。
//
// 实测（见提交说明）：触发时从"收到信号"到"画面在屏幕上"是**一帧**，
// 也就是用户感知的"零等待"。
//
// 安全边界（每一条都必须成立，否则宁可不用这个方案）：
//   · 预热进程**不抢焦点**（不复用 Run 键那条路）、不置顶、不在任务栏出现
//   · `--play` 若发现预热进程不存在（没开、被杀、版本不符），**自动退回普通路径**，
//     所以"预热没起来"只会慢一点，绝不会不播
//   · 预热进程有**空闲自毁**：超过 IdleLimitMinutes 没有收到触发信号就自己退出，
//     不把内存长期占着
//   · 预热进程解码的是"当前选中的那一段"；如果用户在预热之后换了片，触发时
//     校验哈希/路径，不符就自己退出，让 --play 走普通路径重新播（避免放错片子）
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BootAnimation
{
    internal static class WarmRuntime
    {
        /// <summary>触发事件名。**不带 Global\ 前缀** —— 那个需要管理员权限（实测 err=123）。</summary>
        private const string EventName = "BootAnimation_PlayNow_v1";

        /// <summary>记录预热进程 pid 的小文件，用来判断"预热进程是否还活着"。</summary>
        private static string PidPath
        {
            get { return Path.Combine(Program.DataDirectory, "warm.pid"); }
        }

        /// <summary>
        /// 预热进程空闲多久后自毁。10 分钟：
        ///   · 覆盖"这次开机播放结束 → 用户注销/重启 → 下次登录"这段间隔（实测最常见在 1–5 分钟内）
        ///   · 又不会长期占着内存 —— 10 分钟没等到触发就自己退出
        ///   · 它只保持一路解码器和窗口，不渲染可见内容，所以常驻成本很低
        /// </summary>
        internal const int IdleLimitMinutes = 10;

        // ─────────────────────────────────────────────────────── 命名事件

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint WAIT_OBJECT_0 = 0;

        /// <summary>创建（或打开已存在的）手动重置事件。</summary>
        private static IntPtr OpenEvent()
        {
            return CreateEvent(IntPtr.Zero, true, false, EventName);
        }

        // ─────────────────────────────────────────────────────── 触发侧（--play 调用）

        /// <summary>
        /// 尝试让**已经在预热**的进程立刻开始播放。
        ///
        /// 返回 true 表示"已经交给预热进程了，调用方应当直接退出"。
        ///
        /// 判定顺序（任意一步不成立就退回普通路径）：
        ///   1. warm.pid 存在且那个进程还活着
        ///   2. 事件对象能打开、并且成功 SetEvent
        /// 两步都成功才返回 true —— 只要有一点不确定，就走"自己播"那条稳的路。
        /// </summary>
        internal static bool TryTrigger(AnimationInfo target, string explicitFit)
        {
            try
            {
                int pid = ReadWarmPid();
                if (pid <= 0) return false;
                if (!ProcessAlive(pid)) return false;
                /**
                 * 贴合模式必须一致，否则不交接。
                 *
                 * 这是实测抓到的真 bug：预热进程是**上一次**跑起来的，它把视频按当时的
                 * 贴合模式摆好了位置。如果这一轮要求的是另一种模式，交接过去就会播错 ——
                 * 现象是"我明明指定了 --fit cover，播出来还是拉伸"。预热进程无法在不重新
                 * 加载媒体的前提下改贴合，所以这种情况必须由本进程自己播（慢 600ms，但播对）。
                 *
                 * 同时**结束**那个已经不合用的预热进程，避免它一直占着解码器；
                 * 本次播放结束后会按新设置接力起一个。
                 */
                string wanted = explicitFit != null ? explicitFit : Program.ReadFitPreference();
                string warmFit = ReadWarmFit();
                if (warmFit != null && !string.Equals(wanted, warmFit, StringComparison.OrdinalIgnoreCase))
                {
                    Program.Log("贴合模式不符（本轮要 " + wanted + "，预热进程是 " + warmFit
                        + "），结束旧预热进程并改走普通路径（宁慢不错）");
                    try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
                    CleanupPidFile();
                    return false;
                }

                // 预热的是不是同一段？用户在预热之后换了片的话，绝不能让预热进程放错。
                // 用路径比对（预热进程把它的路径写在 warm.pid 的第二行）。
                string warmPath = ReadWarmPath();
                if (target != null && target.Path != null
                    && warmPath != null
                    && !string.Equals(warmPath, target.Path, StringComparison.OrdinalIgnoreCase))
                {
                    Program.Log("预热进程加载的是另一段（" + Path.GetFileName(warmPath)
                        + "），改走普通路径");
                    return false;
                }

                IntPtr handle = OpenEvent();
                if (handle == IntPtr.Zero)
                {
                    Program.Log("打开触发事件失败 err=" + Marshal.GetLastWin32Error());
                    return false;
                }
                bool ok = SetEvent(handle);
                CloseHandle(handle);
                if (!ok)
                {
                    Program.Log("SetEvent 失败 err=" + Marshal.GetLastWin32Error());
                    return false;
                }
                Program.Log("已把播放交给预热进程（pid=" + pid
                    + "），本进程立即退出 —— 这是零等待的关键路径");
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("触发预热进程失败，退回普通路径: " + ex.Message);
                return false;
            }
        }

        private static int ReadWarmPid()
        {
            try
            {
                if (!File.Exists(PidPath)) return 0;
                string[] lines = File.ReadAllLines(PidPath);
                if (lines.Length == 0) return 0;
                int pid;
                return int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) ? pid : 0;
            }
            catch { return 0; }
        }

        private static string ReadWarmPath()
        {
            try
            {
                if (!File.Exists(PidPath)) return null;
                string[] lines = File.ReadAllLines(PidPath);
                return lines.Length >= 2 ? lines[1].Trim() : null;
            }
            catch { return null; }
        }

        /// <summary>预热进程用的是哪种贴合模式（warm.pid 第 4 行）。旧版本没写则返回 null。</summary>
        private static string ReadWarmFit()
        {
            try
            {
                if (!File.Exists(PidPath)) return null;
                string[] lines = File.ReadAllLines(PidPath);
                return lines.Length >= 4 && lines[3].Trim().Length > 0 ? lines[3].Trim() : null;
            }
            catch { return null; }
        }

        private static bool ProcessAlive(int pid)
        {
            try
            {
                System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch { return false; }
        }

        // ─────────────────────────────────────────────────────── 预热侧（--warm）

        /// <summary>
        /// 预热进程主体。
        ///
        /// 它自己**不放画面**：`Opacity = 0` 的全屏窗口一直挂着，视频在里面跑（解码器保持热），
        /// 收到事件后把 Opacity 改 1 就是一帧之内的事。
        /// </summary>
        internal static int Run(AnimationInfo target, bool topmost, int volume, bool mute)
        {
            // 预热路径整体包一层：WinExe 的未处理异常是**静默**的（进程直接以 0xE0434352 退出，
            // 屏幕上和日志里都不说一句话）。上次就是因此白查了两轮 ——
            // 现在无论哪里抛，原因都会落进 boot-animation.log。
            try
            {
                return RunCore(target, topmost, volume, mute);
            }
            catch (Exception ex)
            {
                Program.Log("--warm 失败: " + ex.ToString());
                CleanupPidFile();
                return 9;
            }
        }

        private static int RunCore(AnimationInfo target, bool topmost, int volume, bool mute)
        {
            if (target == null || !target.IsPlayable)
            {
                Program.Log("--warm: 没有可预热的动画，退出");
                return 0;
            }

            /**
             * 预热进程**绝不提优先级**。
             *
             * 实测踩到的严重问题：这里原来调用 SetHighPriority()，而预热窗口是**不可见**的 ——
             * 于是一个看不见的进程用**高优先级**满负荷解码 4K 视频，吃 2040MB 内存。
             * 后果是登录后整台机器被拖慢，用户感受到的就是"登录后要过一段时间才显示"。
             * 预热的目的正是让开机更快，结果反而抢了开机的资源，完全说反了。
             *
             * 现在让它在空闲期几乎不占资源（冻结在第一帧、CPU 时间接近于零）。
             */
            // Program.SetHighPriority() —— 故意不调用，见上面的说明

            Application app = new Application();
            BootAnimation.CrashGuard.AttachUi(app);
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 复用开机那条**完全一样**的播放窗口构建路径，保证"预热播出来的东西"
            // 与实际开机播放逐像素一致。差别只有两处：初始不可见、不自动显形。
            // autoReveal: false —— 预热窗口绝不允许自动显形（那会在约 600ms 后自己冒出来）
            global::PlayerWindow pw = Program.BuildPlayerCore(
                AnimationStateStore.DisplayName(target), target.Path, topmost,
                0, "auto", mute, volume, null, 0, Program.PosterPathForMedia(target.Path), target.Id,
                false);
            Window win = pw.Window;

            // 预热窗口必须**完全不可见且不影响任何人**：
            //   · Opacity 0：屏幕上看不到（但 WPF 仍然渲染、MediaElement 仍然解码）
            //   · Topmost 关掉：绝不抢在别的窗口上面（不可见但置顶的窗口会挡鼠标）
            //   · ShowActivated=false：不抢焦点
            //   · 不进任务栏：用户不该看到一个"开机动画"窗口挂在那里
            //
            // **顺序是硬要求**：`ShowActivated` 必须在窗口显示**之前**设置。
            // 在 Show() 之后改它会抛 InvalidOperationException
            // （堆栈：Window.VerifyConsistencyWithShowActivated），
            // 而 WinExe 的未处理异常是静默的 —— 表现就是"预热进程莫名消失、pid 文件没写"。
            // 这正是本次实测踩到的坑，Windows 事件日志里的 CLR20r3 才把它暴露出来。
            win.Topmost = false;
            win.ShowActivated = false;
            win.ShowInTaskbar = false;
            // 另一条 WPF 硬规则（实测踩到）：
            //   ShowActivated=false **不能**与 WindowState=Maximized 同时用，否则抛
            //   「ShowActivated 为 false 且 WindowState 设置为 Maximized 时无法显示窗口」。
            //   所以这里用 Normal + 显式尺寸铺满屏幕 —— 反正窗口不可见，
            //   但它允许不抢焦点。显形时再切回 Maximized。
            win.WindowState = WindowState.Normal;
            win.Width = SystemParameters.PrimaryScreenWidth;
            win.Height = SystemParameters.PrimaryScreenHeight;
            /**
             * **窗口完全可见，但放在所有显示器之外。**
             *
             * 这是这一轮最关键的修正。实测证据：
             *   · 窗口用 `Opacity = 0` 时，`MediaElement` 的时钟在走
             *     （Position 从 245ms 到 1335ms），但**画面永远不刷新** ——
             *     相邻帧的最大像素差恒为 0，显形之后也不会补上
             *   · 只有把 Opacity 抬到 0.01 也没用，因为它仍然"不可见"
             *
             * 结论：WPF 的视频合成管线要求窗口**真的处于可见状态**才会工作。
             * 所以改成"可见但移出屏幕"：合成管线正常运行（画面真的在解码渲染），
             * 而用户在任何显示器上都看不到它。
             *
             * 用 Win32 的 SetWindowPos 直接摆位置，比改 WPF 的 Left/Top 可靠 ——
             * 后者会受到 WindowStartupLocation 与 DPI 换算的影响。
             */
            win.Left = -32000;
            win.Top = -32000;
            win.Opacity = 1;

            /**
             * 空闲期：**冻结在第一帧**，不循环播放。
             *
             * 这一处修掉了两个实测 bug，它们其实是同一个根源：
             *
             * ❶ "启动画面之后还是会进入黑屏"：
             *    原来用"快播完时把位置拨回 0"来保持解码器热。后果是**每 7.3 秒
             *    就会重新经过一次这段素材淡出到黑的尾部**（实测 6.9s 之后全是黑的）。
             *    用户任何时刻看过去，都可能正好撞上那块黑。
             *
             * ❷ "登录后还要过一段时间才显示"：
             *    原来预热窗口在空闲时**全速播放 4K 视频**，实测占用 2040MB 内存，
             *    而且当时还把自己提到 High 优先级。一个不可见的进程在后台满负荷解码，
             *    把登录后的机器拖慢 —— 预热的目的是让开机更快，结果反而抢了开机的资源。
             *
             * 现在停在首帧：解码器仍然持有已解码的第一帧（所以触发时立刻有画面），
             * 但不再推进时间轴，CPU 时间接近于零，内存也远低于循环播放时。
             */
            DispatcherTimer loop = new DispatcherTimer();
            loop.Interval = TimeSpan.FromMilliseconds(400);
            bool[] revealed = new bool[] { false };   // 用数组当引用容器：匿名委托里要写它
            loop.Tick += delegate
            {
                try
                {
                    System.Windows.Controls.MediaElement media = FindMedia(win);
                    if (media == null) return;
                    /**
                     * 空闲期：**循环播放，但窗口完全不可见**（这也是它本来该有的语义）。
                     *
                     * 为什么不用"暂停冻结"那种更省资源的做法（实测证明行不通）：
                     *   · `Pause()` 之后 `Play()` **无法唤醒它** —— 日志明确记录
                     *     "播放未启动，重试一次（pos=0ms）"，高频采样显示画面完全静止
                     *   · 重新 `Source` 挂载也只是让 Position 变成 39ms 就不再推进，
                     *     30 帧采样亮度恒为同一个值（60.61），画面根本没动
                     * MediaElement 一旦停过就不再可靠地恢复。既然"必须能播"是硬要求，
                     * 就让它一直播着 —— 反正窗口 Opacity=0，用户看不到，
                     * 而且这样触发时是**真的在播**，不需要任何恢复动作。
                     *
                     * 顺带修掉了"黑尾"问题：以前用户看到的是这条循环（会反复经过
                     * 淡出到黑的尾巴），现在循环发生在不可见状态下，用户看到的
                     * 只有触发之后的那一次完整播放。
                     */
                    if (media.NaturalDuration.HasTimeSpan
                        && media.Position >= media.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(300))
                    {
                        media.Position = TimeSpan.Zero;   // 循环回开头：保持解码器热，且用户看不见
                    }
                }
                catch { /* 预热期的异常绝不能让进程死掉 */ }
            };
            loop.Start();

            // 所有属性都设完之后才显示。显示后立刻把 pid 写下来，供 --play 判定。
            win.Show();

            // 把 pid 与"预热的是哪一段"写下来，供 --play 判定
            try
            {
                Directory.CreateDirectory(Program.DataDirectory);
                File.WriteAllLines(PidPath, new string[]
                {
                    System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
                    target.Path ?? "",
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                    Program.ReadFitPreference(),
                });
            }
            catch (Exception ex) { Program.Log("--warm: 写 pid 文件失败 " + ex.Message); }

            Program.Log("--warm: 预热就绪 pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                + " 片段=" + target.Id + "（窗口不可见，解码器保持热）");

            // ── 等触发信号，同时做空闲自毁
            IntPtr handle = OpenEvent();
            if (handle == IntPtr.Zero)
            {
                Program.Log("--warm: 创建事件失败 err=" + Marshal.GetLastWin32Error() + "，退出");
                CleanupPidFile();
                return 1;
            }

            DateTime started = DateTime.Now;
            DispatcherTimer poll = new DispatcherTimer();
            poll.Interval = TimeSpan.FromMilliseconds(50);
            poll.Tick += delegate
            {
                try
                {
                    uint result = WaitForSingleObject(handle, 0);
                    if (result == WAIT_OBJECT_0)
                    {
                        poll.Stop();
                        loop.Stop();
                        Reveal(win, handle, target, revealed);
                        return;
                    }
                    // 空闲自毁：不长期占内存
                    if ((DateTime.Now - started).TotalMinutes >= IdleLimitMinutes)
                    {
                        poll.Stop();
                        loop.Stop();
                        Program.Log("--warm: 空闲超过 " + IdleLimitMinutes + " 分钟，自毁退出");
                        try { win.Close(); } catch { }
                        CleanupPidFile();
                        app.Shutdown();
                    }
                }
                catch { }
            };
            poll.Start();

            win.Closed += delegate
            {
                // 记录"什么时候被关的、为什么" —— 预热进程如果是被意外关闭的，
                // 这里会连时间一起留下来。之前只写了一句"--warm: 退出"，
                // 分不清是自毁、被关闭、还是异常退出。
                Program.Log("--warm: 窗口被关闭（存活 "
                    + ((int)(DateTime.Now - started).TotalSeconds) + " 秒），退出");
                poll.Stop();
                loop.Stop();
                CleanupPidFile();
                app.Shutdown();
            };

            // 未处理异常也要说话：否则又是"进程没了但日志里什么都没有"
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                Program.Log("--warm 未处理异常: " + e.Exception.ToString());
                e.Handled = true;
            };

            Program.Log("--warm: 进入消息循环 " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            app.Run(win);
            Program.Log("--warm: 消息循环返回（存活 "
                + ((int)(DateTime.Now - started).TotalSeconds) + " 秒）");
            CloseHandle(handle);
            CleanupPidFile();
            return 0;
        }

        /// <summary>
        /// 收到触发：把窗口打开并**真正开始播放**。
        ///
        /// 窗口只需要改 Opacity/状态（一帧的事）；但媒体必须显式恢复播放 ——
        /// 空闲期我们把它暂停了（为了不经过淡出黑尾、也为了不空转 CPU），
        /// 所以这里必须**先撤掉暂停再播**。
        ///
        /// 实测踩到的坑：这里原来只写了 `media.Position = 0; media.Play();`，
        /// 但 `Play()` 对"已经被 Pause 且位置为 0"的 MediaElement 不会真正解除暂停状态，
        /// 结果动画**根本没动** —— 高频采样 36 帧显示第 10 帧之后画面完全静止
        /// （亮度恒为 36.85，即定格图）。用户看到的就是"一个不动的画面"。
        /// </summary>
        private static void Reveal(Window win, IntPtr handle, AnimationInfo target, bool[] revealed)
        {
            try
            {
                Program.Log("--warm: 收到触发，开始播放（" + target.Id + "）");
                revealed[0] = true;   // 让空闲定时器停止"拨回开头并暂停"
                // 显形这一刻才切回最大化（构造期用 Normal 是因为 ShowActivated=false 与
                // Maximized 不兼容）。这一步是纯窗口状态变更，不涉及加载或解码。
                // 先把窗口搬回主屏（它在屏幕外预热），再最大化并置顶
                MoveToPrimaryScreen(win);
                win.WindowState = WindowState.Maximized;
                win.Opacity = 1;
                win.Topmost = true;
                BootClock.Mark("warm-revealed");
                Program.Log("--warm: 已搬回主屏并置顶");

                // 从头开始真正播放
                System.Windows.Controls.MediaElement media = FindMedia(win);
                if (media == null)
                {
                    Program.Log("--warm: 找不到 MediaElement，无法开始播放（这是个 bug）");
                    return;
                }

                /**
                 * 恢复播放：**重新挂一次源**，而不是只调 Play()。
                 *
                 * 实测证据：空闲期我们为了不空转 CPU 而 Pause 了媒体，之后
                 * `Position = 0; Play();` **无法把它唤醒** —— 日志明确记录
                 * "播放未启动，重试一次（pos=0ms）"，高频采样也显示画面完全静止。
                 * MediaElement 一旦被 Pause 且位置停在 0，Play() 就不再推进时间轴。
                 *
                 * 重新 SetSource 是可靠的：它会重建媒体会话并从头开始播放。
                 * 代价是重新打开文件（比"仅仅改 Opacity"慢），但这是**必然正确**的做法 ——
                 * 宁可用几十毫秒换正确，也不要为了省这点时间给出一个静止画面。
                 */
                /**
                 * 顺序很关键：**先让窗口真正显示，再挂源播放**。
                 *
                 * 实测证据（最大像素差恒为 0，而 Position 从 245ms 走到 1335ms）：
                 * 在窗口不可见的状态下挂源播放，MediaElement 的时钟照走、
                 * 但**视频帧根本不会被合成到屏幕上**；之后即使把 Opacity 改成 1，
                 * 画面也不会补上来 —— 屏幕上是静止的（或黑的）。
                 *
                 * 所以这里是：把窗口显示出来（此刻用户还没看到，因为 Opacity 仍是 0 吗？
                 * 不 —— 见下面：我们先把 Opacity 设为极小但不是 0 的值让合成管线激活，
                 * 再挂源，最后把 Opacity 拉到 1）。核心是把"窗口参与合成"这件事
                 * 排在"媒体开始播放"之前。
                 */
                string path = target.Path;
                try
                {
                    // 让窗口进入合成状态（Opacity 从 0 抬到一个极小但非 0 的值）
                    win.Opacity = 0.01;
                    win.UpdateLayout();
                    media.Stop();
                    media.Source = new Uri(path);
                    media.Play();
                    Program.Log("--warm: 已重新挂源并开始播放");
                }
                catch (Exception ex) { Program.Log("--warm: 重新挂源失败: " + ex.Message); }

                /**
                 * 状态诊断：把 MediaElement 在触发瞬间及之后的状态记下来。
                 *
                 * 之所以需要它：实测"动画没在播"（27 对相邻帧差值全为 0.00），
                 * 而日志只说了 pos=36ms，看不出到底是卡在哪一种状态
                 * （是否 CanPause、时长有没有、Position 有没有继续走、IsMuted 等）。
                 */
                Action<string> dump = delegate(string when)
                {
                    try
                    {
                        Program.Log("--warm 状态[" + when + "]: pos="
                            + media.Position.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms"
                            + " CanPause=" + media.CanPause
                            + " hasDuration=" + media.NaturalDuration.HasTimeSpan
                            + " naturalW=" + media.NaturalVideoWidth
                            + " opacity=" + win.Opacity
                            + " visible=" + (win.IsVisible ? "1" : "0")
                            + " state=" + win.WindowState);
                    }
                    catch (Exception ex) { Program.Log("--warm 状态诊断失败: " + ex.Message); }
                };
                dump("触发时");
                System.Windows.Threading.DispatcherTimer d1 = new System.Windows.Threading.DispatcherTimer();
                d1.Interval = TimeSpan.FromMilliseconds(400);
                d1.Tick += delegate { d1.Stop(); dump("400ms"); };
                d1.Start();
                System.Windows.Threading.DispatcherTimer d2 = new System.Windows.Threading.DispatcherTimer();
                d2.Interval = TimeSpan.FromMilliseconds(1500);
                d2.Tick += delegate { d2.Stop(); dump("1500ms"); };
                d2.Start();

                // 确认时间轴真的在推进；没推进就再挂一次（最多 3 次）
                int[] attempts = new int[] { 0 };
                System.Windows.Threading.DispatcherTimer kick = new System.Windows.Threading.DispatcherTimer();
                kick.Interval = TimeSpan.FromMilliseconds(200);
                kick.Tick += delegate
                {
                    try
                    {
                        if (media.Position > TimeSpan.Zero)
                        {
                            kick.Stop();
                            Program.Log("--warm: 播放已确认（pos="
                                + media.Position.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms）");
                            return;
                        }
                        attempts[0]++;
                        if (attempts[0] >= 3)
                        {
                            kick.Stop();
                            Program.Log("--warm: 三次重挂源后仍未推进，接受现状（画面会是首帧）");
                            return;
                        }
                        Program.Log("--warm: 播放仍未推进，重挂源第 " + attempts[0] + " 次");
                        media.Stop();
                        media.Source = new Uri(path);
                        media.Play();
                    }
                    catch { }
                };
                kick.Start();
            }
            catch (Exception ex) { Program.Log("--warm: 显形失败 " + ex.Message); }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        /// <summary>把窗口从屏幕外搬回主显示器左上角（随后会最大化）。</summary>
        private static void MoveToPrimaryScreen(Window win)
        {
            try
            {
                IntPtr h = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                if (h == IntPtr.Zero)
                {
                    win.Left = 0;
                    win.Top = 0;
                    return;
                }
                SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOZORDER | SWP_NOACTIVATE | 0x0001 /*SWP_NOSIZE*/);
            }
            catch (Exception ex) { Program.Log("--warm: 搬回主屏失败: " + ex.Message); }
        }

        /// <summary>在窗口的视觉树里找那一路前景 MediaElement。</summary>
        private static System.Windows.Controls.MediaElement FindMedia(System.Windows.DependencyObject root)
        {
            if (root == null) return null;
            System.Windows.Controls.MediaElement self = root as System.Windows.Controls.MediaElement;
            if (self != null) return self;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                System.Windows.Controls.MediaElement found =
                    FindMedia(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private static void CleanupPidFile()
        {
            try { if (File.Exists(PidPath)) File.Delete(PidPath); } catch { }
        }

        /// <summary>启动一个预热进程（安装器/界面打开时调用，也可以由用户手动跑 --warm）。</summary>
        internal static bool SpawnWarm()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo(Program.ExePath, "--warm");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                // 关键：调用方（--play 触发进程，或播放结束后的父进程）马上就会退出。
                // 不声明这一条的话，子进程会跟着父进程一起被收掉 —— 预热就变成了"起来就死"。
                psi.ErrorDialog = false;
                System.Diagnostics.Process child = System.Diagnostics.Process.Start(psi);
                if (child != null)
                {
                    try
                    {
                        // 让子进程独立于父进程的作业/句柄生命周期
                        child.EnableRaisingEvents = false;
                    }
                    catch { }
                }
                Program.Log("已拉起预热进程");
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("拉起预热进程失败: " + ex.Message);
                return false;
            }
        }
    }
}
