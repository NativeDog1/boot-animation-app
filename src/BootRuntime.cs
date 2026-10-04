// BootRuntime.cs —— 开机/唤醒动画的运行链路（与 UI 完全解耦）
//
// ─────────────────────────────────────────────────────────────────────────────
// 这个文件存在的唯一理由：
//
//   之前"登录之后要过一会儿才播"的根本原因不是加载慢，而是**启动链路上挂了一堆
//   与播放无关的工作**。旧路径是：
//
//     进程启动 → 解析参数 → 提升优先级 → 读 settings
//              → 扫描整个动画库（内置 + community/*.meta + 数据目录全部视频）
//              → 对每一段做 MP4 atom 探测（等于把库里每个文件都打开读一遍）
//              → 为**每一段**内置片段解包整段视频到磁盘
//              → 为当前片段生成氛围背景图（同步等 ffmpeg，最长 15 秒）
//              → 建 WPF Application → 建窗口 → 挂媒体 → 解码 → 出画
//
//   BootRuntime 把这条链路切成两段：
//
//     【启动期 · 必须等】只做三件事：读到"要播哪一段" → 确认那个文件在 → 建窗口播。
//                          不扫库、不探测别的片段、不联网、不建缩略图、不等 ffmpeg。
//     【播出后 · 后台】    背景图生成、库扫描、社区目录，全部排在动画已经在播之后。
//
//   UI 那条链路（Shell.Run / --manager）完全不受影响：它要的是完整信息，慢一点没关系。
//   两条链路**互不调用**，这正是"动画播放不得依赖完整桌面 UI 初始化"的落地方式。
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员、无 XAML。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace BootAnimation
{
    /// <summary>这一轮为什么播。决定超时、是否置顶、以及诊断上怎么记录。</summary>
    internal enum BootMode
    {
        /// <summary>Windows 登录后启动。</summary>
        Startup = 0,
        /// <summary>从睡眠/休眠恢复。</summary>
        Resume = 1,
        /// <summary>界面上手动试播（不写配置、不影响 active）。</summary>
        Manual = 2,
    }

    internal sealed class BootRequest
    {
        public BootMode Mode = BootMode.Startup;
        /// <summary>指定播哪一段；null = 播当前生效的那段。</summary>
        public string ClipId;
        /// <summary>直接播一个文件（跳过解析）。</summary>
        public string FilePath;
        public bool Topmost = true;
        public double Seconds;
        /// <summary>贴合方式。默认 stretch —— 客户要的"真全屏"：拉伸填满、无空缺。</summary>
        public string Fit = Program.DefaultFit;
        /// <summary>
        /// 调用方是否**显式**指定了 --fit。
        ///
        /// 这个标志存在的原因是一个实测抓到的真 bug：预热进程是**上一次**跑起来的，
        /// 它用的是当时的贴合设置。如果这轮用 `--fit cover` 显式要求了别的模式，
        /// 而交接仍然发生，用户看到的就是"我指定了 cover，播出来还是拉伸"。
        /// 所以显式指定时**不允许交接**，宁可慢 600ms 也要播对。
        /// </summary>
        public bool FitExplicit;

        /// <summary>
        /// 是否使用原生播放器（--native）。默认 **false**。
        ///
        /// 之所以默认关闭：原生播放器目前只能"32ms 上屏并显示缓存首帧图"，
        /// 还没有视频解码与呈现能力（实测帧间差恒为 0 = 画面不动）。
        /// 等到原生的 Media Foundation 管线做完、并通过"帧间差不为 0"的验收，
        /// 再把默认值改成 true。在那之前它只是可选路径。
        /// </summary>
        public bool UseNativePlayer;
        public bool Mute;
        public int Volume = 100;
        public double DelaySeconds;
        public string SnapshotPath;
        public double SnapshotAt = 1.5;
    }

    internal static class BootRuntime
    {
        /// <summary>
        /// 启动期允许做的最重的一件事就是"解包当前这一段"。
        /// 其余一切（扫库、探测、背景图、社区）都在动画开播之后。
        /// </summary>
        internal static int Run(BootRequest request)
        {
            /**
             * 在**第一行**就记录"系统开机时长"与"进程启动时刻"。
             *
             * 为什么必须放在最前面：客户实测登录后 20–30 秒才出现动画，而程序自身
             * 从启动到出画只要约 570ms（boot.log 稳定可查）。也就是说那 20–30 秒
             * 落在"程序被拉起之前"。要在下一次真实登录时判定责任归属，就必须有
             * 一个不受程序内部逻辑影响的时间基准 —— 系统开机时长正是这个基准：
             *
             *   · 若"开机时长"与"boot-enter"的差很大（比如 40 秒），
             *     说明进程很晚才被 Windows 拉起 → 延迟归登录过程，不是本程序
             *   · 若进程很早就起来、但 boot-enter 之后某一步卡住 → 延迟在本程序内部
             *
             * 用 GetTickCount64（内核维护的开机毫秒数）而不是 Environment.TickCount，
             * 因为后者是 32 位、会在 24.9 天溢出。
             */
            try
            {
                ulong uptimeMs = GetTickCount64();
                Program.Log("──────── 本次启动基准 ────────");
                Program.Log("进程启动 → 代码开始执行: " + Program.ProcessStartToCodeMs
                    + " ms（这段是 CLR 初始化 + 加载程序集 + JIT，发生在我的代码之外；"
                    + "它若很大，动画就必然晚于桌面出现）");
                Program.Log("系统已开机 " + (uptimeMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                    + " 秒；进程启动于 " + System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + "；现在 " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { Program.Log("写启动基准失败: " + ex.Message); }

            BootClock.Mark("boot-enter");

            /**
             * ── 单实例守卫 ──────────────────────────────────────────────────
             *
             * 为什么要它：登录时 explorer 会按顺序拉起一批 Run 项（这台机器上有 8 个以上），
             * 期间完全可能出现"同一条启动项被处理两次"，或者用户在动画播放期间手动点了图标。
             * 两个实例会各自建一个全屏置顶窗口、各起一路视频解码 —— 互相叠加、
             * 白费 CPU，用户看到的是"两层画面在打架"。
             *
             * 用一个**会话内具名互斥体**做判定：
             *   · 抢到 → 正常播放，退出时释放
             *   · 抢不到 → 说明已经有一个在播，本实例**立刻安静退出**，
             *     不做任何界面动作（这是"拿不到就不打扰"的同一原则）
             *
             * 注意名字不带 Global\ 前缀：那个需要管理员权限（实测 err=123）。
             */
            bool ownsMutex = false;
            // 先清掉上一次调用遗留的错误码，否则 GetLastWin32Error 可能读到无关的旧值 ——
            // 这是这个 API 的经典坑：CreateMutexW 返回非空时错误码可能是 183（已存在）
            // 也可能是上一次调用留下的 0，必须用**紧随其后**的一次读取判断。
            System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            IntPtr mutex = CreateMutexW(IntPtr.Zero, true, "BootAnimation_Playback_SingleInstance");
            int mutexErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Program.Log("单实例守卫: mutex=0x" + mutex.ToString("X") + " err=" + mutexErr
                + (mutexErr == ERROR_ALREADY_EXISTS ? "  → 已有实例" : "  → 我是第一个"));
            if (mutex == IntPtr.Zero)
            {
                // 建不出互斥体不该阻止播放（宁可重复也不要不出画面）
                Program.Log("单实例互斥体创建失败，继续播放");
                ownsMutex = true;
            }
            else if (mutexErr == ERROR_ALREADY_EXISTS)
            {
                Program.Log("已有一个实例在播放，本实例安静退出（避免两层画面叠加）");
                return 0;
            }
            else
            {
                ownsMutex = true;
            }

            if (ownsMutex && mutex != IntPtr.Zero)
            {
                // 进程退出（正常关闭或异常终止）时释放并关闭句柄。
                // 不依赖某一条返回路径 —— 这个函数有多个"安静退出"的分支。
                IntPtr captured = mutex;
                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    try { ReleaseMutex(captured); } catch { }
                    try { CloseHandle(captured); } catch { }
                };
            }

            /**
             * ── "本次开机只播一次"保险 ─────────────────────────────────────────
             *
             * 为什么光有互斥体不够（实测踩到）：
             *   登录时三条启动机制（计划任务 / Run 键 / 启动文件夹）**不是同时**触发的，
             *   而是相隔几十秒**先后**触发。互斥体只能挡住"同一瞬间"的重复，
             *   挡不住"前一个播完、你点掉、后一个才轮到"的情况 ——
             *   现象就是客户报的"播放完一次之后又播放了两次"。
             *
             * 做法：把"最近一次播放的时刻"落盘，开机时长在一段窗口内的重复触发直接跳过。
             *   · 窗口取 10 分钟：覆盖整个登录阶段，又不会影响"手动再播一次"
             *   · 判定依据用**开机时长**（GetTickCount64）而不是墙钟时间 ——
             *     墙钟可能在开机后被同步、夏令时切换等改变，而开机时长是单调的
             *   · 与用户无关：只看"这台机器这次开机有没有播过"
             *
             * 注意必须放在互斥体之后、建窗口之前：越早退出越省资源。
             */
            /**
             * ── 心跳：必须在这里就启动，不能等到窗口显示之后 ──────────────────
             *
             * 这一点踩过坑：原来心跳放在 window.Show() 之后（约 7 秒处），
             * 而本进程会在很早就拉起原生封面窗口，那个窗口 40ms 就开始检查心跳 ——
             * 那时心跳文件还是上一次运行留下的旧文件，于是它判定"主程序已停"
             * 并立刻自杀（实测 43ms 退出，封面完全没派上用场）。
             *
             * 心跳的用途：让原生封面窗口知道"主程序还活着"。主程序退出后心跳停止，
             * 原生窗口在 3 秒内自己退出 —— 避免它留在屏幕上变成一张静止的定格画面。
             *
             * 所以它必须在任何窗口创建之前就写好，越早越好。
             */
            StartHeartbeat();
            if (WasPlayedSinceBoot())
            {
                Program.Log("本次开机已经播放过开机动画，本实例安静退出（避免重复播放）");
                return 0;
            }

            Program.SetHighPriority();

            // ── 1) 解析要播哪一段。只读一个文件，不扫目录 ──────────────────
            string path = request.FilePath;
            string label = path == null ? null : Path.GetFileName(path);
            string clipId = request.ClipId;

            if (path == null)
            {
                if (string.IsNullOrEmpty(clipId)) clipId = Program.ReadChosen();
                if (string.IsNullOrEmpty(clipId)) clipId = Program.ClipIds[0];

                Resolved resolved = Resolve(clipId);
                if (resolved != null)
                {
                    path = resolved.Path;
                    label = resolved.Label;
                    clipId = resolved.Id;
                }
            }
            BootClock.Mark("clip-resolved");

            if (path == null || !File.Exists(path))
            {
                // 关键可靠性要求：拿不到动画不是"卡住"，而是**安静地跳过**。
                // 桌面已经在等着了，开机动画没播不该留下任何东西（何况进程本身还占着屏幕）。
                Program.Log("BootRuntime: 找不到可播放的动画（id=" + (clipId == null ? "(空)" : clipId)
                    + "），跳过动画，直接放行");
                return 0;
            }

            // ── 2) 可选的等待。默认 0；保留是因为手动跑 --delay 仍然有用 ──────
            if (request.DelaySeconds > 0)
            {
                Program.Log("按参数要求先等 " + request.DelaySeconds.ToString(CultureInfo.InvariantCulture) + " 秒");
                Thread.Sleep((int)(request.DelaySeconds * 1000));
            }

            /**
             * ── 2b) 优先用"快速版"文件启动
             *
             * 实测依据（同一套代码，只换文件）：
             *
             *     片段格式              窗口显示   媒体打开   显形
             *     HEVC Main 10 4K       851ms     1122ms    1188ms
             *     H.264 1440p           238ms      354ms     388ms
             *
             * 10-bit HEVC 4K 要多做解码器与视频处理器的初始化，而那正是登录时
             * （系统还在加载、安全驱动在过滤每一次文件读取）最容易被放大的一步。
             * 快速版由应用/安装阶段生成（H.264 8-bit 1440p），原文件保留不动。
             *
             * 注意：海报与定格图仍然按**原文件**的路径查找，所以在切换 path 之前
             * 先把原始路径记下来，避免海报缓存对不上。
             */
            string originalPath = path;
            string fastVersion = Program.PreferFastVersion(path);
            if (!string.Equals(fastVersion, path, StringComparison.OrdinalIgnoreCase))
            {
                path = fastVersion;
                label = Path.GetFileName(path);
                BootClock.Mark("fast-version");
            }

            /**
             * 这里原来有一段"把播放交接给预热进程"的代码，已移除。
             *
             * 移除原因（实测，不是推测）：预热进程需要一个不可见或屏幕外的窗口提前把
             * 视频解码好，但 **WPF 的 MediaElement 不会在非前台窗口里刷新视频**：
             *
             *   方案                        实测结果
             *   Pause 冻结省资源            Play() 唤不醒，高频采样画面全静止
             *   重挂 Source 恢复            Position 走到 39ms 就不再推进
             *   Opacity=0 不可见            时钟在走（0→245→1335ms），帧差恒为 0
             *   移到屏幕外但完全可见         帧差恒为 0
             *   普通启动路径（对照）         帧差 13 处在变 / 6 静止 → 正常播放
             *
             * 也就是说，预热窗口要么给出一个**不动的画面**，要么给出**黑屏**，
             * 两者都比"晚几百毫秒但正确播放"糟得多。所以这里改回单一路径：
             * 由本进程自己建窗口、自己播 —— 实测这条路 230ms 出首帧图、约 500ms 出画，
             * 全程没有黑屏，播完定格在最后一帧有画面的地方等用户点击。
             */
            // ── 3) 背景图：**只读缓存，绝不在启动期生成** ────────────────────
            //
            // 这是启动链路上最容易失控的一环：现场生成要起 ffmpeg 子进程并解码一帧，
            // 实测能让首帧晚到十几秒。所以启动期只查缓存文件在不在；不在就用纯黑边先播，
            // 生成任务丢到后台（见第 5 步），下一次开机就有背景图了。
            bool ambientEnabled = AmbienceWanted(request.Fit);

            /**
             * 首帧图的路径**与氛围背景无关** —— 这一点是我把默认贴合改成 stretch 时
             * 踩出来的大坑，也是客户报的"启动动画黑屏"的真因：
             *
             *   原来写成 `posterPath = ambientEnabled ? ... : null`，
             *   而 `AmbienceWanted("stretch")` 是 **false**（拉伸铺满当然不需要氛围背景）。
             *   于是默认模式（stretch）下 posterPath 恒为 null：
             *     → 首帧层根本没被建（BuildPlayer 拿到 null）
             *     → 窗口从 ~230ms 显形到 ~500ms 出画之间是一块纯黑
             *   实测证据：0.35s 时刻抓帧，中心均值 **0.00**（纯黑）。
             *
             * 正确的划分：**首帧层是"每个模式都要有"的东西**（它是防黑屏的保险），
             * 而氛围背景只是 ambient/auto 才需要的**装饰**。两者共用了同一张缓存图，
             * 但"要不要这张图"的判据完全不同，所以必须分开计算。
             */
            string posterPath = Program.PosterCachePathFor(clipId);
            bool posterReady = posterPath != null && File.Exists(posterPath);

            if (request.Mode == BootMode.Startup || request.Mode == BootMode.Resume)
            {
    /**
                 * ── 先把原生封面窗口**建好**（屏幕外），再去等桌面 ──────────────
                 *
                 * 顺序为什么必须这样（用户录屏给出的实测证据）：
                 *   录屏时间线：0–8.7s 登录界面 → **8.7s 桌面完全显示** → 8.9–10s 黑 → 11.0s 动画
                 *   也就是**桌面比动画早出现**，这就是"穿帮"。
                 *   原因是窗口在"检测到桌面已就绪"之后才创建，天然就落在桌面后面。
                 *
                 * 现在改成：登录一被拉起就立刻建好窗口（放在屏幕外，可见但不占屏、
                 * 已经完成渲染与合成），然后才去等桌面。桌面就绪的瞬间发一个信号，
                 * 原生窗口只需**改一次位置**就铺满屏幕 —— 那是一帧之内的事，
                 * 用户看到的第一眼就是动画，而不是桌面。
                 *
                 * 失败不影响播放：找不到/启动失败，只是少这一层"提前量"。
                 */
                string nativePoster = Program.NativePlayerPath;
                if (nativePoster != null && posterReady && path != null)
                {
                    try
                    {
                        // 清掉上一次可能残留的信号，否则原生窗口会立刻上屏或立刻退出
                        IntPtr s1 = CreateEventW(IntPtr.Zero, true, false, "BootAnimation_Handoff");
                        if (s1 != IntPtr.Zero) CloseHandle(s1);
                        IntPtr s2 = CreateEventW(IntPtr.Zero, true, false, "BootAnimation_DesktopReady");
                        if (s2 != IntPtr.Zero) CloseHandle(s2);

                        System.Diagnostics.ProcessStartInfo npsi =
                            new System.Diagnostics.ProcessStartInfo(nativePoster);
                        npsi.UseShellExecute = false;
                        npsi.CreateNoWindow = true;
                        npsi.Arguments = "--poster \"" + posterPath + "\" --hidden --log \""
                            + Program.LogFilePath + "\"";
                        System.Diagnostics.Process.Start(npsi);
                        BootAnimation.BootClock.Mark("native-poster-created");
                        Program.Log("已在屏幕外建好原生封面窗口（等桌面就绪信号再上屏）");
                    }
                    catch (Exception ex)
                    {
                        Program.Log("建原生封面窗口失败（不影响播放）: " + ex.Message);
                    }
                }

                int waitedMs = 0;
                while (waitedMs < 60000 && !DesktopReady())
                {
                    Thread.Sleep(100);
                    waitedMs += 100;
                }
                // 桌面就绪的**同一时刻**让封面窗口上屏 —— 这一步是消除"穿帮"的关键
                SignalEvent("BootAnimation_DesktopReady");
                Program.Log("桌面已就绪（等待 " + waitedMs + " ms）才开始播放"
                    + (waitedMs >= 60000 ? "，注意：等满 60 秒仍未确认，按超时放行" : ""));
                BootClock.Mark("desktop-ready");
            }

            /**
             * 首帧图缺失时**当场补一张**，而不是拖到后台。
             *
             * 完整因果链：
             *   1. 首帧图（视频第一帧的缓存图）是本程序用来填住"窗口已显形、
             *      但视频还没解码出画"那几百毫秒的唯一手段
             *   2. 它原来是**播放开始之后**由 BelowNormal 后台线程生成的
             *   3. 于是每一段片段**第一次**播放时，poster-checked 必然是 [absent]
             *      → 没有首帧层 → 窗口从 ~230ms 显形到 ~500ms 出画之间就是一块纯黑
             *
             * 这段代价（抽一帧，实测几百毫秒）只在该片段第一次播放时付一次，
             * 之后永远命中缓存。用它换掉"每次首播都黑一下"是明显划算的。
             * 另外安装器的 `--prepare` 会预先备好全部片段的首帧图，
             * 正常情况下这一步根本不会执行（开机只读缓存，实测 2ms）。
             *
             * 注意顺序：必须在**建窗口之前**补，否则这一轮还是黑的。
             */
            if (posterPath != null && !posterReady)
            {
                try
                {
                    // 用 originalPath（原文件）而不是 path（可能是快速版）：
                    // 海报缓存名按"文件名的 stem"规则生成，用快速版会得到另一个名字，
                    // 于是每次启动都要重新抽帧 —— 那正是我们要避免的启动期开销。
                    Program.BuildPosterInternal(originalPath, posterPath);
                    posterReady = File.Exists(posterPath);
                    BootClock.Mark("poster-built", posterReady ? "ok" : "failed");
                    Program.Log("首帧图缺失，已当场补上（这段第一次播放，之后会一直命中缓存）");
                }
                catch (Exception ex)
                {
                    BootClock.Mark("poster-built", "failed");
                    Program.Log("首帧图补不上（不影响播放，只是首帧会短暂黑）: " + ex.Message);
                }
            }
            BootClock.Mark("poster-checked", posterReady ? "cached" : "absent");

            /**
             * ── 3b) 原生渲染路径（优先）
             *
             * 走原生播放器的理由（实测数据）：
             *   WPF 路径：窗口 250ms 才上屏，而且上屏后还要等视频解码（约 500ms 才有画面）
             *   原生路径：窗口 **3–31ms** 上屏，且上屏那一刻**已经有缓存的视频首帧图**
             *
             * 那 3ms 与 250ms 的差距不是优化，是量级差异 —— 因为原生窗口跳过了
             * CLR 加载 WPF 程序集、Application 初始化、XAML 布局这一整套。
             *
             * 失败时**自动退回 WPF 路径**：找不到 NativePlayer.exe、它启动失败、
             * 或返回非 0，都继续往下走原来的逻辑。所以"原生播放器没装好"只会慢一点，
             * 绝不会不播 —— 这条保证比性能更重要。
             */
            /**
             * **默认关闭原生路径**，直到它真的能播视频为止。
             *
             * 现状如实记录（实测）：原生播放器已经做到"窗口 32ms 上屏 + 上屏即有缓存的
             * 首帧图"，但它目前**只画这一张静止的图** —— 帧间最大差恒为 0，
             * 也就是说动画不会播。一个不动的开机动画比"晚 200ms 但会播"糟得多，
             * 所以在原生的视频管线（Media Foundation 解码 + D3D 呈现）完成之前，
             * 这条路必须默认关闭。
             *
             * 用 --native 可以显式启用（开发与逐阶段验收用）。
             */
            string nativeExe = request.UseNativePlayer ? Program.NativePlayerPath : null;
            if (nativeExe != null && File.Exists(nativeExe))
            {
                try
                {
                    System.Diagnostics.ProcessStartInfo npsi =
                        new System.Diagnostics.ProcessStartInfo(nativeExe);
                    npsi.UseShellExecute = false;
                    npsi.CreateNoWindow = true;
                    string nativeArgs = "--poster \"" + (posterReady ? posterPath : "") + "\""
                        + " --log \"" + Program.LogFilePath + "\"";
                    if (request.Seconds > 0)
                    {
                        nativeArgs += " --seconds "
                            + request.Seconds.ToString(CultureInfo.InvariantCulture);
                    }
                    if (!request.Topmost) nativeArgs += " --no-topmost";
                    npsi.Arguments = nativeArgs;

                    System.Diagnostics.Process nativeProc = System.Diagnostics.Process.Start(npsi);
                    BootClock.Mark("native-launched");
                    Program.Log("已交给原生播放器（pid=" + (nativeProc == null ? 0 : nativeProc.Id)
                        + "）—— 窗口 3–31ms 上屏且立即有画面");
                    // 本进程使命结束：动画由原生播放器负责，不需要 WPF
                    return 0;
                }
                catch (Exception ex)
                {
                    Program.Log("启动原生播放器失败，退回 WPF 路径: " + ex.Message);
                }
            }
            else
            {
                Program.Log("未找到原生播放器，使用 WPF 路径: " + (nativeExe == null ? "(null)" : nativeExe));
            }

            // ── 4) 建窗口并播 ───────────────────────────────────────────────
            //
            // 注意 posterPath 传的是**只在缓存命中时才非空**的路径：播放窗口据此决定用
            // 氛围背景还是纯黑边，而它自己**不会**去现场生成——生成在后台（第 5 步）。
            /**
             * ── 中间方案：先用**原生窗口**把封面帧顶上去 ──────────────────────
             *
             * 为什么需要这一步（实测数据）：
             *   同一份代码创建窗口的耗时 ——
             *     已登录桌面：`win.Show()` 94ms
             *     登录时：    `win.Show()` **10400ms**
             *   登录时 WPF 要加载程序集、枚举字体、建 GPU 合成与 DWM 通道，
             *   再叠加安全软件的文件过滤驱动，慢了 110 倍。改代码逻辑碰不到这一点。
             *
             * 原生播放器（`NativePlayer.exe`，无任何 WPF 依赖）实测 **3–31ms** 就能把
             * 缓存的视频首帧图铺满屏幕。所以先让它上屏，用户立刻看到画面；
             * WPF 在后台慢慢准备，就绪后通过命名事件交接，原生窗口自行退出。
             *
             * 因为两者显示的是**同一帧画面**（都是视频第一帧的缓存图），
             * 交接时看不出接缝。
             *
             * 失败不影响播放：找不到原生播放器、启动失败，都只是少了这段"提前显示"，
             * 后面的 WPF 路径照常工作。
             */

            BootAnimation.BootClock.Mark("before-app");
            Application app = new Application();
            BootAnimation.BootClock.Mark("after-app");
            BootAnimation.CrashGuard.AttachUi(app);
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            global::PlayerWindow player = Program.BuildPlayer(
                label, path, request.Topmost, request.Seconds, request.Fit,
                request.Mute, request.Volume, request.SnapshotPath, request.SnapshotAt,
                posterReady ? posterPath : null, clipId);
            Window window = player.Window;
            // 窗口已在 BuildPlayerCore 里显示（首帧图已在视觉树中）；现在显式挂媒体。
            if (player.AttachMedia != null) player.AttachMedia();
            // 注意：窗口已经在 Program.BuildPlayerCore 内部显示过了（在那里显示才能保证
            // "首帧图已加入视觉树"这个顺序，否则会出现约 300ms 的黑窗口期，见那里的说明）。
            // 这里只负责落"本次开机已播放"的标记。
            MarkPlayedSinceBoot();   // 立刻落盘：后续被拉起的实例据此安静退出


            // ── 5) 动画开播之后才做非关键工作 ────────────────────────────────
            //
            // 这里刻意用"窗口已经上屏"作为分界线，而不是"媒体已出画"：
            // 用户此刻看到的是黑底 + 加载提示（最多几十毫秒），后台任务不会跟解码抢 IO。
            ScheduleBackgroundWork(clipId, path, ambientEnabled, posterReady, posterPath);

            // ── 6) 顺手起一个预热进程，供**下一次**登录走零等待路径 ────────────
            //
            // 为什么要在这里做：这次开机播放是"普通路径"（预热进程还没起来），
            // 播完之后用户通常会退出登录或重启 —— 那时如果已经有一个预热好的进程在等，
            // 下一次登录的交接就是 1ms（实测）。不这么做的话，用户要等到下下次登录才享受到。
            //
            // 放在这一步的理由与后台补图完全一样：动画已经在播了，起进程这几毫秒
            // 既不抢解码也不影响画面；而且它是 detached 的，本进程退出后它继续活着。
            /**
             * 这里原来会调 ScheduleWarmChain() 拉起一个 `--warm` 进程"供下次登录预热"。
             * 该方案已废弃（WPF 的 MediaElement 不会在非前台窗口里刷新视频，实测四种
             * 方案全部得到静止画面），所以这段接力也一并去掉 —— 否则每次播放都会
             * 白起一个进程，而它只会立刻打印"已废弃"然后退出。
             */
            app.Run();

            /**
             * 退出前**显式通知原生封面窗口一起退出**。
             *
             * 为什么不能只靠心跳超时（实测踩到的坑）：
             *   动画播完、本进程退出后，原生封面窗口还在屏幕上留了 **2.8 秒**
             *   （日志：主程序 21:11:27.758 退出，原生 21:11:30.608 才退出）——
             *   那 2.8 秒里用户看到的就是一张静止的定格画面，
             *   正是客户报的"播放完整个动画之后又出现了一个定格的画面"。
             *
             * 心跳只能作为**兜底**（防止本进程异常崩溃时原生窗口赖着不走），
             * 正常路径必须由这里显式通知 —— 通知是即时的（命名事件），
             * 原生窗口在 120ms 内就会退出，用户完全看不到多出来的画面。
             */
            SignalEvent("BootAnimation_Handoff");
            Program.Log("已通知原生封面窗口退出");

            Program.Log("退出，进程存活 " + Program.StartedMs() + " ms");
            BootClock.Dump();
            return 0;
        }

        private sealed class Resolved
        {
            public string Id;
            public string Path;
            public string Label;
        }

        /// <summary>
        /// 把一个 clip id 解析成可播放的文件路径。
        ///
        /// **不调用 AnimationRepository.ScanLocal** —— 那是给 UI 用的整体扫描。
        /// 这里只按 id 精确地找那一个文件，路径顺序与旧行为完全一致，保证兼容：
        ///   1. 数据目录根下 <id>.mp4（用户手工放的）
        ///   2. community/<id>.mp4 + <id>.meta（社区下载的）
        ///   3. 内置片段（解包）
        /// 全部失败返回 null，由调用方决定"跳过动画"。
        /// </summary>
        private static Resolved Resolve(string clipId)
        {
            if (string.IsNullOrEmpty(clipId)) return null;

            // 1. 数据目录根下
            string root = Program.DataDirectory;
            string[] exts = new string[] { ".mp4", ".m4v", ".wmv", ".avi", ".mov" };
            for (int i = 0; i < exts.Length; i++)
            {
                string candidate = Path.Combine(root, clipId + exts[i]);
                if (File.Exists(candidate))
                {
                    Resolved r = new Resolved();
                    r.Id = clipId;
                    r.Path = candidate;
                    r.Label = clipId;
                    return r;
                }
            }

            // 2. 社区目录（用 .meta 认领，缺失时只看文件本身）
            string community = AnimationRepository.CommunityDir;
            string communityVideo = Path.Combine(community, clipId + ".mp4");
            if (File.Exists(communityVideo))
            {
                Resolved r = new Resolved();
                r.Id = clipId;
                r.Path = communityVideo;
                r.Label = ReadMetaName(Path.Combine(community, clipId + ".meta"), clipId);
                return r;
            }

            // 3. 内置：只解包**这一段**。旧代码会在这条路上把四段全解一遍（约 34 MB IO）。
            if (Program.IsBuiltIn(clipId))
            {
                string extracted = Program.ClipPathOf(clipId);
                if (extracted != null && File.Exists(extracted))
                {
                    Resolved r = new Resolved();
                    r.Id = clipId;
                    r.Path = extracted;
                    r.Label = Program.ClipNameOf(clipId);
                    return r;
                }
            }

            // 4. 配置里指着一个已经删掉的社区片段：退回内置第一段，而不是黑屏
            Program.Log("BootRuntime: id=" + clipId + " 解析不到文件，回退到内置片段");
            string fallbackId = Program.ClipIds[0];
            string fallback = Program.ClipPathOf(fallbackId);
            if (fallback != null && File.Exists(fallback))
            {
                Resolved r = new Resolved();
                r.Id = fallbackId;
                r.Path = fallback;
                r.Label = Program.ClipNameOf(fallbackId);
                return r;
            }
            return null;
        }

        private static string ReadMetaName(string metaPath, string fallback)
        {
            try
            {
                if (!File.Exists(metaPath)) return fallback;
                string[] lines = File.ReadAllLines(metaPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = lines[i].Substring(5).Trim();
                        if (v.Length > 0) return v;
                    }
                }
            }
            catch { /* 元数据读不到就用 id 当名字 */ }
            return fallback;
        }

        /// <summary>
        /// 在本进程退出前，让一个预热进程接力 —— 供下一次登录使用。
        ///
        /// 只在**普通路径**（没有预热进程可用）时调用：如果这次已经是预热进程在播，
        /// 它自己会处理（见 WarmRuntime 的自毁与接力），不需要再起一个。
        /// </summary>
        /// <summary>
        /// 已废弃：原来用来在播放结束后接力拉起一个 `--warm` 预热进程。
        ///
        /// 废弃原因见 WarmRuntime 的说明（WPF 的 MediaElement 不会在非前台窗口里刷新视频）。
        /// 方法本身保留为空实现，避免误用；不再有任何调用点。
        /// </summary>
        private static void ScheduleWarmChain()
        {
            // 故意留空：调用它会白起一个进程，而那个进程只会立刻退出。
        }

        /// <summary>这个贴合模式需不需要氛围背景。auto 保守地当作需要（整帧是更安全的默认）。</summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr CreateEventW(IntPtr attr, bool manualReset, bool initialState, string name);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr OpenEventW(uint access, bool inherit, string name);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr handle);

        private const uint EVENT_MODIFY_STATE = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string className, string windowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        /// <summary>
        /// 桌面是否**真的**已经可用（可以往上放全屏窗口了）。
        ///
        /// 为什么不能只看 explorer.exe 存在（实测踩到的坑）：
        ///   登录阶段 explorer 可能已经启动，但登录界面还在屏幕上、DWM 还没把桌面画出来。
        ///   这时全屏窗口会被登录界面盖住 —— 用户**只听到声音、看不到画面**，
        ///   等桌面真的出现时 7.3 秒的动画已经放完，于是"一登录就看到定格帧"。
        ///   客户 2026-10-03 22:53 那次实测正是这个现象：
        ///     22:53:47.715 程序被拉起 → 22:53:56.237 开始播放（登录界面可能仍在）
        ///
        /// 所以判据用**登录界面是否退场**，而不是"过了多久"：
        ///   1. `LogonUI.exe` 与 `LockApp.exe` 都必须**不在运行** ——
        ///      它们是登录界面/锁屏的实现进程，运行中就说明用户还没看到桌面
        ///   2. `Shell_TrayWnd`（任务栏）必须**存在且可见** ——
        ///      这是"桌面已经画出来"的直接证据，比"explorer 进程存在"强得多
        ///
        /// 两个条件都满足才认为可以播。这样在快机器上不会白等、
        /// 在慢机器上也不会早播 —— 不需要任何固定缓冲秒数。
        /// </summary>
        private static bool DesktopReady()
        {
            try
            {
                /**
                 * 判据与 NativePlayer 那侧保持一致（用早的那一级）。
                 *
                 * 原来只要求"任务栏可见"，实测它比桌面真正画出来**晚 5.8 秒**
                 * （Windows 先画桌面图标层，任务栏要好几秒后才显示），
                 * 于是 WPF 侧准备得太晚，接管时刻被推后。
                 *
                 * 现在：登录界面退场 + explorer 在跑，就认为桌面已经可用；
                 * 任务栏可见作为兜底。原生封面窗口此时已经盖在屏幕上，
                 * 所以 WPF 早点准备好就早点接管，中间不会有桌面露出来。
                 */
                bool logonGone = !ProcessExists("LogonUI") && !ProcessExists("LockApp");
                if (logonGone && ProcessExists("explorer")) return true;

                IntPtr tray = FindWindowW("Shell_TrayWnd", null);
                if (logonGone && tray != IntPtr.Zero && IsWindowVisible(tray)) return true;

                return false;
            }
            catch { return true; }   // 任何异常都当已就绪：宁可早也不要不出画面
        }

        /// <summary>置位一个命名事件（用它通知原生封面窗口"桌面就绪，可以上屏了"）。</summary>
        private static void SignalEvent(string name)
        {
            try
            {
                IntPtr h = OpenEventW(EVENT_MODIFY_STATE, false, name);
                if (h == IntPtr.Zero) h = CreateEventW(IntPtr.Zero, true, false, name);
                if (h != IntPtr.Zero)
                {
                    SetEvent(h);
                    CloseHandle(h);
                }
            }
            catch { }
        }

        /// <summary>心跳文件路径（与 NativePlayer.HeartbeatPath 保持一致）。</summary>
        internal static string HeartbeatPath
        {
            get { return Path.Combine(Program.DataDirectory, "alive.txt"); }
        }

        private static System.Threading.Timer heartbeatTimer;

        /// <summary>启动心跳：每 500ms 把 alive.txt 的修改时间刷新一次。</summary>
        private static void StartHeartbeat()
        {
            try
            {
                if (heartbeatTimer != null) return;
                string path = HeartbeatPath;
                Touch(path);
                heartbeatTimer = new System.Threading.Timer(delegate { Touch(path); },
                    null, 500, 500);
            }
            catch (Exception ex) { Program.Log("心跳启动失败: " + ex.Message); }
        }

        private static void Touch(string path)
        {
            try
            {
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static bool ProcessExists(string name)
        {
            try
            {
                System.Diagnostics.Process[] ps = System.Diagnostics.Process.GetProcessesByName(name);
                bool any = ps.Length > 0;
                for (int i = 0; i < ps.Length; i++) ps[i].Dispose();
                return any;
            }
            catch { return false; }
        }

        /// <summary>记录"最近一次播放发生在开机后的第几秒"的小文件。</summary>
        private static string PlayedMarkerPath
        {
            get { return Path.Combine(Program.DataDirectory, "played-at.txt"); }
        }

        /// <summary>
        /// 本次开机是否已经播放过。
        ///
        /// 判据：marker 文件里存的"开机时长"与当前开机时长相差小于窗口值。
        /// 用开机时长而不是墙钟：墙钟会被时间同步/夏令时改，开机时长单调递增。
        /// 读不出/写不进的任何异常都当作"没播过" —— 宁可重复一次，也不能不播。
        /// </summary>
        private static bool WasPlayedSinceBoot()
        {
            try
            {
                string path = PlayedMarkerPath;
                if (!File.Exists(path)) return false;
                string text = File.ReadAllText(path).Trim();
                double recorded;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out recorded))
                    return false;
                double now = GetTickCount64() / 1000.0;
                double diff = now - recorded;
                // 窗口 10 分钟。diff 为负说明 marker 是"上一次开机"留下的（开机时长归零重算），
                // 那种情况应当允许播放，所以只拦 0 <= diff < 窗口。
                bool played = diff >= 0 && diff < 600.0;
                if (played)
                {
                    Program.Log("检测到本次开机已播放过（" + Math.Round(recorded, 1)
                        + " 秒前播过，现在 " + Math.Round(now, 1) + " 秒）");
                }
                return played;
            }
            catch { return false; }
        }

        /// <summary>记录"本次开机已播放"，供后续实例判断。</summary>
        private static void MarkPlayedSinceBoot()
        {
            try
            {
                Directory.CreateDirectory(Program.DataDirectory);
                File.WriteAllText(PlayedMarkerPath,
                    (GetTickCount64() / 1000.0).ToString("0.0", CultureInfo.InvariantCulture),
                    System.Text.Encoding.ASCII);
            }
            catch (Exception ex) { Program.Log("写播放标记失败（下次可能重复播放）: " + ex.Message); }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutexW(IntPtr attributes, bool initialOwner, string name);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReleaseMutex(IntPtr mutex);



        private const int ERROR_ALREADY_EXISTS = 183;

        private static bool AmbienceWanted(string fit)
        {
            if (string.IsNullOrEmpty(fit)) return true;
            string f = fit.ToLowerInvariant();
            return f == "auto" || f == "ambient";
        }

        /// <summary>
        /// 非关键工作，全部排在动画已经在播之后。
        ///
        /// 这里只做两件**本地、有限、可失败**的事，而且都在后台线程：
        ///   · 补齐氛围背景图（起 ffmpeg 抽一帧），下次开机就能直接读到
        ///   · 把这次启动的分段耗时写进 boot.log，供 Diagnostics 页读
        ///
        /// 刻意**不做**的：不拉社区目录、不联网、不扫整个动画库。
        /// 社区是 UI 的事；启动动画期间访问网络会在断网的机器上变成启动阻塞。
        /// </summary>
        private static void ScheduleBackgroundWork(string clipId, string mediaPath, bool ambientWanted,
            bool posterReady, string posterPath)
        {
            if (!ambientWanted || posterReady) return;

            Thread worker = new Thread(delegate()
            {
                try
                {
                    // 让出最开始那几十毫秒：此刻解码正在抢 CPU，抽帧不该跟它争。
                    Thread.Sleep(600);
                    Program.Log("BootRuntime: 后台补齐氛围背景图 " + clipId);
                    Program.BuildPosterInternal(mediaPath, posterPath);
                }
                catch (Exception ex)
                {
                    // 后台失败只记录：这一次无非是继续用黑边，下次开机再试。
                    Program.Log("BootRuntime: 后台生成背景图失败 " + ex.Message);
                }
            });
            worker.IsBackground = true;
            worker.Name = "ba-boot-poster";
            worker.Priority = ThreadPriority.BelowNormal;
            worker.Start();
        }
    }

    /// <summary>
    /// 启动期分段计时。
    ///
    /// 为什么值得单独一个类：这个程序历史上"快不快"全靠感觉，而 `--delay 3` 那种
    /// 整段延迟就是在这种模糊里活下来的。把每一步的时间戳写进 boot.log，
    /// "首帧慢在哪里"才是可查的事实而不是猜测。
    /// </summary>
    internal static class BootClock
    {
        private static readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        private static readonly System.Text.StringBuilder line = new System.Text.StringBuilder();
        private static long last;

        internal static void Mark(string step)
        {
            Mark(step, null);
        }

        internal static void Mark(string step, string detail)
        {
            long now = watch.ElapsedMilliseconds;
            long delta = now - last;
            last = now;
            if (line.Length > 0) line.Append("  ");
            line.Append(step).Append("@").Append(now).Append("(+").Append(delta).Append(")");
            if (detail != null) line.Append("[").Append(detail).Append("]");
        }

        internal static long ElapsedMs { get { return watch.ElapsedMilliseconds; } }

        /// <summary>把这一轮的完整时间线写进 boot.log（一行一条，便于事后比对）。</summary>
        internal static void Dump()
        {
            if (line.Length == 0) return;
            try
            {
                string dir = Program.DataDirectory;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "boot.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + "  " + line.ToString() + Environment.NewLine);
            }
            catch { /* 诊断写不进去绝不能影响播放 */ }
        }
    }
}
