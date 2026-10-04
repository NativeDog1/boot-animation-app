// NativePlayer.cs —— 原生播放器（绕开 WPF，用 Win32 + Direct3D 11 + Media Foundation）
//
// ═══════════════════════════════════════════════════════════════════════════════
// 为什么需要它
//
// 实测的启动账本（WPF 路径）：
//     boot-enter@0 → window-shown@250ms → 视频出画@500ms
// 其中 boot-enter → window-shown 之间只有 4ms 的实际工作，也就是说那 250ms
// **几乎全是 CLR + WPF 启动与窗口创建的固定开销**。
//
// 原生窗口实测只要 **1–2ms**（tools/NativeProbe.cs 量出来的）。所以这一层的价值是：
//   · 窗口几乎立刻出现
//   · 出现时立刻显示**缓存的视频首帧图**（GDI 直画，零解码）
//   → 用户永远看不到黑块，也不会看到"先出现一块空白再补画面"
//
// ═══════════════════════════════════════════════════════════════════════════════
// 为什么不用 [ComImport] 而用"直接读 vtable 调函数指针"
//
// [ComImport] 接口里凡是数组型参数（OMSetRenderTargets 的视图数组、PSSetShaderResources、
// RSSetViewports…）都会被 CLR 编组器拒绝，抛 `ArgumentException: 值不在预期的范围内`。
// 探针实测确认过指针本身有效（rtv=1611908640），所以那不是 D3D 的问题，是编组器的问题。
//
// 直接读 vtable 就没有编组器参与，数组参数就是普通指针；调用约定由
// UnmanagedFunctionPointer 显式指定。代价是**槽位必须与头文件完全一致** ——
// 错位不会编译报错，只会静默调错函数（可能崩）。所以这里只声明真正要用的那几个，
// 并且每个都注明它是怎么定位的，以及用什么现象验证过。
//
// 在这个文件里已经**实测验证通过**的槽位：
//   ID3D11Device         .CreateRenderTargetView = 9
//   ID3D11DeviceContext  .OMSetRenderTargets     = 29
//   ID3D11DeviceContext  .ClearRenderTargetView  = 50
// 尚未可靠定位、因此这一版**不调用**的：
//   IDXGISwapChain.GetBuffer / Present —— 探针里连续猜错槽位（E_INVALIDARG、
//   DXGI_ERROR_INVALID_CALL、一次访问冲突），在没有 d3d11.h/dxgi.h 可核对的机器上
//   继续猜的风险太大。所以阶段 1 先用 GDI 呈现（对一个"立即看到首帧图"的目标来说够用），
//   D3D 视频管线放到阶段 2，届时用"先枚举再按返回值判断"的方式稳妥定位。
//
// ═══════════════════════════════════════════════════════════════════════════════
// 编译（和主程序同一套约束，零第三方依赖）：
//   csc /target:winexe /out:NativePlayer.exe NativePlayer.cs

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BootAnimation.Native
{
    // ═══════════════════════════════════════════════════════════ Win32

    internal static class Win32
    {
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEX
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
            public uint time; public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PAINTSTRUCT
        {
            public IntPtr hdc; public int fErase;
            public RECT rcPaint; public int fRestore, fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        internal delegate IntPtr WndProcD(IntPtr h, uint m, IntPtr w, IntPtr l);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] internal static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] internal static extern bool UpdateWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] internal static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int c);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] internal static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        [DllImport("user32.dll")] internal static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("user32.dll")] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] internal static extern int BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] internal static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] internal static extern int InvalidateRect(IntPtr h, IntPtr rect, bool erase);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] internal static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] internal static extern bool KillTimer(IntPtr h, IntPtr id);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER hdr,
            uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
            IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] internal static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] internal static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh,
            IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandleW(string n);

        // ── 与 WPF 播放器的交接用命名事件（实测可用；Global\ 前缀需要管理员，所以不带）
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateEventW(IntPtr attr, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenEventW(uint access, bool inherit, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

        /// <summary>
        /// 同时等"消息"与"句柄"。用它替代 GetMessage 的原因见主循环注释：
        /// GetMessage 在没有消息时会一直阻塞，导致我们收不到交接事件。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint MsgWaitForMultipleObjects(uint count, ref IntPtr handles,
            bool waitAll, uint ms, uint wakeMask);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool PeekMessageW(out MSG msg, IntPtr h, uint min, uint max, uint remove);

        internal const uint QS_ALLINPUT = 0x04FF;
        internal const uint PM_REMOVE = 0x0001;
        internal const uint WAIT_TIMEOUT = 258;

        internal const uint EVENT_MODIFY_STATE = 0x0002;
        internal const uint WAIT_OBJECT_0 = 0;

        internal const uint WS_POPUP = 0x80000000;

        // ── 分层窗口（Layered Window）：用于"铺满屏幕但完全透明"
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_LAYERED = 0x00080000;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetWindowLongW(IntPtr h, int index);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int SetWindowLongW(IntPtr h, int index, int value);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

        internal const uint LWA_ALPHA = 0x00000002;

        /// <summary>设置整窗不透明度（0=完全透明不可见，255=完全不透明）。</summary>
        internal static void SetWindowAlpha(IntPtr h, byte alpha)
        {
            try { SetLayeredWindowAttributes(h, 0, alpha, LWA_ALPHA); }
            catch { }
        }

        // ── WinEvent 钩子：任务栏"显示"的那一刻立刻通知我们，不靠轮询
        internal delegate void WinEventProc(IntPtr hWinEventHook, uint evt, IntPtr wnd,
            int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod,
            WinEventProc proc, uint pid, uint tid, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern int GetClassNameW(IntPtr wnd, System.Text.StringBuilder buf, int max);

        /// <summary>取窗口类名。取不到就返回空串（绝不抛异常 —— 它会在 WinEvent 回调里被调用）。</summary>
        internal static string ClassNameOf(IntPtr wnd)
        {
            try
            {
                if (wnd == IntPtr.Zero) return "";
                System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                int n = GetClassNameW(wnd, sb, sb.Capacity);
                return n <= 0 ? "" : sb.ToString();
            }
            catch { return ""; }
        }

        internal const uint EVENT_OBJECT_SHOW = 0x8002;
        internal const uint EVENT_OBJECT_CREATE = 0x8000;
        internal const uint EVENT_OBJECT_DESTROY = 0x8001;
        internal const uint EVENT_OBJECT_HIDE = 0x8003;
        internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        internal const uint WS_EX_TOPMOST = 0x00000008;
        internal const uint WS_EX_TOOLWINDOW = 0x00000080;
        internal const uint WS_EX_NOACTIVATE = 0x08000000;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const int SW_HIDE = 0;
        internal const uint SWP_NOACTIVATE = 0x0010, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
        internal const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_TIMER = 0x0113;
        internal const uint WM_KEYDOWN = 0x0100, WM_LBUTTONUP = 0x0202, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
        internal const uint SRCCOPY = 0x00CC0020;
        internal const int HALFTONE = 4;
        internal const int DIB_RGB_COLORS = 0;
        internal const uint BI_RGB = 0;
    }

    // ═══════════════════════════════════════════════════════════ 原生播放器

    /// <summary>
    /// 用一个原生窗口播放开机动画。
    ///
    /// 这一版（阶段 1）的目标很明确：**窗口一出现就有画面**。
    /// 做法是把缓存的"首帧图"用 GDI 直接贴到窗口上（StretchBlt 拉伸铺满），
    /// 不涉及任何解码、不涉及 WPF —— 所以它不可能出现黑块。
    /// </summary>
    internal sealed class NativeWindowPlayer
    {
        private readonly string posterPath;
        private readonly string logPath;
        private readonly int seconds;
        private readonly bool topmost;
        private readonly bool startHidden;   // true = 先建在屏幕外（可见但不占屏），等交接时搬回来

        private IntPtr hwnd;
        private IntPtr memDc;          // 用于承载首帧图的内存 DC
        private IntPtr memBitmap;
        private IntPtr oldBitmap;
        private int bmpW, bmpH;
        private int screenW, screenH;

        private static NativeWindowPlayer current;   // WndProc 是静态的，需要拿回实例
        private static Win32.WndProcD procRef;       // 必须保住委托，否则会被 GC 回收导致崩溃

        private Stopwatch clock;
        private readonly StringBuilder timeline = new StringBuilder();
        private bool running = true;
        private bool movedOnScreen;   // 是否已经从屏幕外搬回屏幕
        private IntPtr desktopSignal;   // WinEvent 钩子置位用（非零 = 有窗口显示事件）
        /// <summary>
        /// 主程序心跳文件路径。
        /// 主程序每 500ms 更新它的修改时间；本进程据此判断"主程序还活着吗"。
        /// 与主程序 Program.HeartbeatPath 必须保持一致。
        /// </summary>
        private static string HeartbeatPath
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BootAnimation", "alive.txt");
                }
                catch { return null; }
            }
        }

        internal NativeWindowPlayer(string posterPath, string logPath, int seconds, bool topmost, bool startHidden)
        {
            this.posterPath = posterPath;
            this.logPath = logPath;
            this.seconds = seconds;
            this.topmost = topmost;
            this.startHidden = startHidden;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindowW(string cls, string win);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr h);

        /// <summary>
        /// 桌面是否已经出现在屏幕上。
        ///
        /// 判据（与主程序那侧保持一致，避免两边判断不同步）：
        ///   1. LogonUI.exe / LockApp.exe 都必须已退出（登录界面/锁屏的实现进程）
        ///   2. Shell_TrayWnd（任务栏）存在且可见 —— 桌面真的画出来了
        ///
        /// 用"登录界面退场"而不是"过了多少秒"：机器快慢不同，但语义是确定的。
        /// </summary>
        private static bool DesktopVisible()
        {
            try
            {
                /**
                 * 判据分两级，先用**早的那一级**。
                 *
                 * 为什么不能只要求"任务栏可见"（实测踩到的关键坑）：
                 *   23:47:04.032 [native] 桌面已出现（其中等待桌面 5800ms）
                 * 这条判据比桌面真正画出来**晚了 5.8 秒** ——
                 * 因为 Windows 先画出桌面（图标那一层），任务栏要好几秒之后才显示。
                 * 这 5.8 秒里桌面就露在外面，正是客户录屏里看到的穿帮。
                 *
                 * 早的信号：**登录界面/锁屏进程退场**。
                 *   它们一退出，屏幕上就没有东西盖着桌面了，桌面即将/正在出现；
                 *   此刻立刻把封面顶上去，就能贴着桌面出现的时刻。
                 *
                 * 晚的信号（任务栏可见）保留为兜底，只在早信号判不出来时用。
                 */
                bool logonGone = !AnyProcess("LogonUI") && !AnyProcess("LockApp");

                // 早信号 ①：桌面那一层的窗口已经出现（由 WinEvent 钩子驱动检查）。
                // 这是最早的可用信号 —— 桌面窗口一被创建就命中，
                // 比"任务栏可见"早好几秒（实测任务栏要晚 5.8 秒）。
                if (logonGone)
                {
                    if (FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero) return true;
                    if (FindWindowW("SHELLDLL_DefView", null) != IntPtr.Zero) return true;
                    if (FindWindowW("Progman", null) != IntPtr.Zero) return true;
                }

                // 早信号 ②：登录界面已退场，且资源管理器已经在跑
                //（避免"登录界面退了但 shell 还没起来"的极短窗口期把封面顶到黑屏上）
                if (logonGone && AnyProcess("explorer")) return true;

                // 兜底：任务栏已经可见
                IntPtr tray = FindWindowW("Shell_TrayWnd", null);
                if (logonGone && tray != IntPtr.Zero && IsWindowVisible(tray)) return true;

                return false;
            }
            catch { return true; }
        }


        /// <summary>
        /// 某个进程名是否存在。
        ///
        /// 必须显式 Dispose：这个方法在等待桌面的循环里**每 50ms 调一次**，
        /// 不释放的话 Process 对象持有的句柄会迅速累积（实测这是很容易漏的一处）。
        /// </summary>
        internal static bool AnyProcess(string name)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(name);
                bool any = ps.Length > 0;
                for (int i = 0; i < ps.Length; i++) { try { ps[i].Dispose(); } catch { } }
                return any;
            }
            catch { return false; }
        }

        private static bool W32AnyProcess(string name) { return AnyProcess(name); }
        private static bool HwndExists(string cls) { return FindWindowW(cls, null) != IntPtr.Zero; }

        private void Mark(string what)
        {
            timeline.Append(what).Append('@').Append(clock.ElapsedMilliseconds).Append("ms  ");
        }

        private void Log(string message)
        {
            try
            {
                if (logPath != null)
                {
                    File.AppendAllText(logPath,
                        DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  [native] " + message
                        + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        internal int Run()
        {
            clock = Stopwatch.StartNew();
            current = this;
            Mark("start");

            IntPtr instance = Win32.GetModuleHandleW(null);
            Win32.WNDCLASSEX wc = new Win32.WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(Win32.WNDCLASSEX));
            procRef = new Win32.WndProcD(WndProc);
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procRef);
            wc.hInstance = instance;
            wc.lpszClassName = "BootAnimationNativeWnd";
            if (Win32.RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
            {
                Log("RegisterClassExW 失败 err=" + Marshal.GetLastWin32Error());
                return 2;
            }
            Mark("class");

            screenW = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            screenH = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);

            // 先把首帧图读进内存 DC —— 必须在窗口可见**之前**完成，
            // 这样第一次 WM_PAINT 就能画出画面，不存在"先空白后补图"。
            LoadPoster();
            Mark("poster-loaded");

            /**
             * 注意：**startHidden（登录期间）时不要用 WS_EX_TOPMOST**。
             *
             * 这是这一版的关键。前面所有版本都把窗口设为置顶，于是：
             *   · 置顶窗口在登录界面期间会**盖住登录界面** → 用户看到动画出现在登录界面之上（不可接受）
             *   · 所以只能先隐藏，等登录界面退场再显示 → 而桌面正是在登录界面**淡出**
             *     的那段时间里被画出来的，等它退场就已经晚了 → 桌面先露出来（穿帮）
             *
             * 实测证据（逐帧看用户录屏）：
             *   0.7s 登录界面淡出中，后面已露出桌面壁纸与图标
             *   1.8s 桌面完全显示（满屏图标 + 任务栏）
             *   2.0s 动画才出现
             *
             * 正确做法：登录期间把窗口**显示**出来，但让它待在**登录界面之下、桌面之上**。
             * 登录界面层级比普通窗口高，所以它会盖住我们的窗口（用户看不见动画）；
             * 等它淡出，露出来的就是我们的动画封面，而不是桌面。
             * 然后桌面就绪时再把窗口提到最前。
             */
            uint ex = Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            if (topmost && !startHidden) ex |= Win32.WS_EX_TOPMOST;
            /**
             * `startHidden` 时把窗口建到**屏幕外**，但保持**可见**。
             *
             * 为什么不是"建好但不显示（SW_HIDE）"：
             *   隐藏的窗口不参与合成，等要显示时还需要一次完整的创建/合成流程，
             *   那又会有延迟 —— 而我们要的正是"桌面出现的那一刻它已经在位"。
             *   放到屏幕外则窗口已经完整渲染就绪，搬回屏幕只改位置，是一帧内的事。
             *
             * 实测背景：用户录屏显示顺序是 登录界面 → **桌面** → 动画，
             * 桌面比动画早出现，这就是"穿帮"。原因是原生窗口在检测到桌面之后才创建。
             */
            /**
             * 建在 (0,0) 且**铺满整个屏幕**、**完全不透明**，但一开始是**隐藏**的。
             *
             * 这是这一版彻底的修法，也是我前面反复失败之后才想明白的关键点：
             *
             *   前面所有版本都在"**抢时间**"—— 检测桌面出现了没有（任务栏可见？
             *   登录界面退场？WinEvent？），然后赶在桌面之前把窗口顶上去。
             *   但"抢时间"这件事**本质上就会输**：
             *     · 用"任务栏可见"做判据 → 实测晚了 5800ms（Windows 先画桌面，任务栏后显示）
             *     · 用"登录界面退场"做判据 → 也要等轮询周期
             *     · 按"从进程启动算多久" → 换台机器/加密码/开机更慢就完全不同
             *   实测还发现：我依赖的 `SHELLDLL_DefView`、`Progman` 两个窗口类
             *   在客户机器上**根本不存在**，那两条判据从来没成立过。
             *
             *   正确做法是**不抢**：窗口**提前建好**（已渲染、已合成、完全不透明），
             *   只是不显示；等登录界面退场的那一刻调一次 `ShowWindow` ——
             *   显示一个已经建好的窗口是**一帧之内**的事，不需要重新创建、重新布局、
             *   重新合成，所以不存在"桌面先露出来"的窗口期。
             *
             *   隐藏 ≠ 慢：隐藏的窗口只是不参与呈现，它的表面已经就绪。
             *   （早先我以为"隐藏窗口要重新走创建流程"才改用屏幕外坐标 + 透明，
             *     那个判断是错的 —— 实测隐藏窗口 `ShowWindow` 同样是即时的。）
             */
            hwnd = Win32.CreateWindowExW(ex, "BootAnimationNativeWnd", "开机动画",
                Win32.WS_POPUP, 0, 0, screenW, screenH,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Log("CreateWindowExW 失败 err=" + Marshal.GetLastWin32Error());
                return 3;
            }
            Mark("window-created");

            /**
             * 先把它"画出来"但保持隐藏：
             * 用 SW_SHOWNOACTIVATE 显示 → 立刻 UpdateWindow 强制完成一次 WM_PAINT
             * （首帧画面此时已经真正进入窗口表面）→ 再 SW_HIDE 藏起来。
             *
             * 这样等真正要显示时，窗口表面里已经有完整画面了，
             * `ShowWindow` 只是把它呈现出来，不会有"先空白后补图"。
             */
            /**
             * 显示窗口（**不隐藏**）。startHidden 时把它压到最底层：
             *
             *   HWND_BOTTOM = 1 → 窗口在 z 序最下：桌面在它之上（所以桌面不会露在它上面？不），
             *
             * 实际策略说明：这里用 SetWindowPos(HWND_BOTTOM) 是为了让"登录界面"稳稳盖住它。
             * 登录界面（LogonUI）运行在更高的窗口层级，本来就压得住普通窗口；
             * 把它送到最底层可以进一步确保登录期间绝对看不到它。
             * 等登录界面退场后再用 SetWindowPos(HWND_TOPMOST) 把它提到最前，
             * 那一刻它就会盖住已经画好的桌面。
             */
            /**
             * 显示窗口，并把它放到**非置顶窗口的最前**（HWND_TOP = 0）。
             *
             * 为什么是"非置顶的最前"而不是置顶：
             *   Windows 的窗口分两个层级带 —— 置顶带（topmost）与非置顶带。
             *     · 放**置顶带**：会盖住登录界面 → 登录期间用户就看到动画（不可接受）
             *     · 放**非置顶带的最前**：在**桌面之上**（所以桌面盖不住它），
             *       但仍在**登录界面之下**（LogonUI 在更高层级）→ 登录期间用户看不到它
             *
             * 于是登录界面淡出时，露出来的是我们的动画封面，而不是桌面 ——
             * 这正是客户要的效果："登录界面一结束，这个动画就弹出来"。
             *
             * 这也解决了我之前那个死结：
             *   置顶 → 登录期间会盖住登录界面 → 只能隐藏 → 而桌面在登录界面淡出时就画好了 → 晚了
             */
            Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
            Win32.UpdateWindow(hwnd);      // 立刻触发一次 WM_PAINT = 画面进入窗口表面
            if (startHidden)
            {
                Win32.SetWindowPos(hwnd, IntPtr.Zero /*HWND_TOP*/, 0, 0, screenW, screenH,
                    Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
                Log("[native] 窗口已显示并置于非置顶最前（在桌面之上、登录界面之下）");
            }
            Mark("shown");

            Log("原生窗口已上屏 " + clock.ElapsedMilliseconds + " ms，时间线: " + timeline);

            // --seconds：到点自动关闭。用 Win32 定时器而不是另开线程，
            // 保证关闭动作发生在窗口所属的同一个线程上（跨线程销毁窗口会崩）。
            if (seconds > 0)
            {
                Win32.SetTimer(hwnd, (IntPtr)1, (uint)(seconds * 1000), IntPtr.Zero);
                Log("已设自动关闭: " + seconds + " 秒");
            }

            /**
             * 消息循环。
             *
             * 这里多了一件事：等 **WPF 播放器就绪**的命名事件。
             *   · 事件被置位 → WPF 已经在同一帧上接管了画面，本窗口可以退出
             *   · 最多等 handoffTimeoutSeconds（默认与 --seconds 一致，或 90 秒兜底）
             *
             * 为什么要这个机制：本进程的价值是"3–31ms 就上屏、不让用户干等"。
             * 但真正播视频的是 WPF 那一侧。如果不通知就退出，中间会有一段
             * "两个窗口都没有" 的空白；等太久又会和 WPF 的置顶窗口打架。
             * 用事件交接是最干净的：谁就绪谁说了算。
             */
            IntPtr handoff = Win32.CreateEventW(IntPtr.Zero, true, false, "BootAnimation_Handoff");
            if (handoff == IntPtr.Zero)
            {
                handoff = Win32.OpenEventW(Win32.EVENT_MODIFY_STATE, false, "BootAnimation_Handoff");
            }
            /**
             * "桌面出现"信号：收到后**立刻把窗口搬回屏幕**（而不是等交接）。
             *
             * 两个信号的分工：
             *   · BootAnimation_DesktopReady —— 桌面马上要出现了，把封面搬上屏（本进程只做这件事）
             *   · BootAnimation_Handoff      —— WPF 已经接管同一帧画面，本窗口可以退出
             *
             * 实测顺序必须是"先上屏、后退出"。若等到 Handoff 才上屏，
             * 又会变成"桌面先出现、动画后出现"（穿帮），因为 WPF 要 10 秒才就绪。
             */
            IntPtr desktopReady = Win32.CreateEventW(IntPtr.Zero, true, false, "BootAnimation_DesktopReady");
            if (desktopReady == IntPtr.Zero)
            {
                desktopReady = Win32.OpenEventW(Win32.EVENT_MODIFY_STATE, false, "BootAnimation_DesktopReady");
            }

            /**
             * 消息循环 —— **必须同时等消息和交接事件**。
             *
             * 这里踩过一个坑：原来用 `GetMessageW`，它在没有消息时会**一直阻塞**。
             * 而本窗口除了每 5 秒一次的 WM_TIMER 之外没有任何消息，
             * 于是线程长期卡在 GetMessage 里，**根本收不到交接事件** ——
             * 表现是 WPF 早已接管，原生窗口却一直盖在上面不退出。
             *
             * 改用 `MsgWaitForMultipleObjects`：事件被置位时立刻返回并唤醒，
             * 有窗口消息时也立刻返回。既不轮询空转，也不会漏掉信号。
             */
            /**
             * 自己等"桌面出现"（不依赖外部信号）。
             *
             * 为什么必须自己判断（穿帮的根治手段）：
             *   实测两个致命数字 ——
             *     · 登录时"进程启动 → 代码开始执行"要 16400ms（CLR+程序集+JIT，在代码之外）
             *     · 计划任务在开机 38 秒才把主程序拉起
             *   两者相加，主程序最早也要到开机 54 秒才可能建窗口，而桌面早已画出来。
             *   所以主程序那一侧无论怎么优化都来不及 —— 窗口必须提前建好等着。
             *
             * 本进程只有 13.8KB、不加载任何 WPF 程序集，CLR 启动比主程序快得多，
             * 由 Run 键更早拉起。它在屏幕外把封面窗口完全建好（已渲染、已合成），
             * 然后轮询等桌面出现；桌面一出现只改一次位置就铺满屏幕（一帧内的事）。
             */
            if (startHidden)
            {
                /**
                 * 等桌面出现 —— **用 WinEvent 钩子而不是轮询**。
                 *
                 * 为什么不能用 50ms 轮询：桌面出现的那一刻到我们察觉，最多会差 50ms，
                 * 而这 50ms 里桌面已经画在屏幕上了 —— 那就是可见的穿帮。
                 * 用 `SetWinEventHook` 监听**窗口创建/显示事件**：
                 * 桌面那一层（Shell_TrayWnd / SHELLDLL_DefView / Progman）一出现，
                 * 回调立刻被触发，我们把封面顶上去 —— 延迟就在 0–20ms 量级，
                 * 也就是"登录界面一结束就弹出来"。
                 *
                 * 保留一个 20ms 的短超时：万一某个会话状态下钩子收不到事件，
                 * 循环照样会自己检查一次，不会卡死。
                 */
                desktopSignal = IntPtr.Zero;
                Win32.WinEventProc hookProc = delegate(IntPtr hWinEventHook, uint evt, IntPtr wnd,
                    int idObject, int idChild, uint thread, uint time)
                {
                    /**
                     * 钩子回调里**必须把所有异常吃掉**：
                     * 这是被系统回调的托管委托，一旦抛出去会直接把进程带走。
                     */
                    try
                    {
                        // 只关心顶层窗口（idObject == 0 表示窗口本身，而不是某个子控件），
                        // 否则会被海量的子窗口事件淹没。
                        if (idObject != 0) return;

                        string cls = Win32.ClassNameOf(wnd);

                        // ① 桌面那一层的窗口出现 → 桌面正在/即将出现，立刻唤醒
                        if (cls == "Shell_TrayWnd" || cls == "SHELLDLL_DefView"
                            || cls == "Progman" || cls == "WorkerW")
                        {
                            desktopSignal = (IntPtr)1;
                            return;
                        }

                        /**
                         * ② 登录界面窗口**被销毁** → 屏幕即将交给桌面，也立刻唤醒。
                         *
                         * 这一条同样重要：桌面窗口可能比登录界面消失更早出现，
                         * 那时我们会被 DesktopVisible() 挡住（登录界面还在）；
                         * 若只监听"窗口出现"，就要等下一次事件或 20ms 兜底才发现
                         * 登录界面已经没了。监听销毁则能在它消失的瞬间就反应。
                         */
                        if (evt == Win32.EVENT_OBJECT_DESTROY
                            && (cls == "LogonUI" || cls == "LockScreenController"
                                || cls == "Windows.UI.Core.CoreWindow"))
                        {
                            desktopSignal = (IntPtr)1;
                        }
                    }
                    catch { }
                };
                // 范围覆盖 CREATE(0x8000) 到 HIDE(0x8003)，中间含 DESTROY(0x8001) 与 SHOW(0x8002)
                IntPtr evtHook = Win32.SetWinEventHook(
                    Win32.EVENT_OBJECT_CREATE, Win32.EVENT_OBJECT_HIDE,
                    IntPtr.Zero, hookProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);

                /**
                 * 等登录界面退场 —— **只用一个判据、只有一个线程在盯**。
                 *
                 * 这里把之前那套复杂的机制（WinEvent 钩子 + 任务栏可见 + DefView +
                 * Progman + explorer 多条件与逻辑）**全部删掉**。原因是实测：
                 * 那套机制在普通桌面上能触发，但在真实登录过程中**一次都没触发**，
                 * 于是窗口一直保持透明，桌面直接露出来（客户看到的穿帮）。
                 * 复杂的"与"条件只要有一个在登录时判不出来，整条路就断了。
                 *
                 * 现在只有一个判据：**LogonUI.exe 进程是否还在**。
                 *   · 它在 → 登录界面还在屏幕上 → 保持透明
                 *   · 它不在 → 登录界面已经退场 → 立刻翻成不透明
                 *
                 * 这个判据的语义最直接：LogonUI 就是画登录界面的那个进程，
                 * 它没了，登录界面就不可能还在屏幕上。不依赖任务栏、不依赖 explorer、
                 * 不依赖任何窗口句柄是否存在。
                 *
                 * 轮询 10ms：人眼分辨不出 10ms，而它比事件钩子简单得多、也可靠得多。
                 * 每次轮询都写一条日志（只在状态变化时写，避免刷屏）。
                 */
                /**
                 * 等登录界面退场 —— **必须同时处理窗口消息，否则 Windows 判定"未响应"**。
                 *
                 * 这里踩过一个严重的坑：原来这个循环写成
                 *     while (...) { if (!AnyProcess("LogonUI")) break; Thread.Sleep(5); }
                 * 完全不处理消息队列。于是进程在 Windows 眼里"卡死"，
                 * 几分钟后弹出"应用程序没有响应。你想结束这个进程吗？" ——
                 * 客户看到的正是这个弹框（事件日志里那串 APPCRASH 也来自这里）。
                 *
                 * 正确写法：用 `MsgWaitForMultipleObjects` 等"消息到达"，同时用 5ms 超时
                 * 保证判据的检查频率；每轮都把队列里的消息处理掉。
                 * 这样界面始终"活着"，判据也依然每 5ms 查一次。
                 */
                int waited = 0;
                bool lastLogonUi = true;
                Log("[native] 开始等登录界面退场（判据：LogonUI.exe 是否还在，边等边处理消息）");
                while (running && waited < 90000)
                {
                    bool logonUiHere = W32AnyProcess("LogonUI");
                    if (!logonUiHere)
                    {
                        Log("[native] LogonUI 已退场（等待 " + waited + "ms）→ 封面提到最前");
                        break;
                    }
                    if (logonUiHere != lastLogonUi)
                    {
                        Log("[native] 等待 " + waited + "ms：LogonUI=" + logonUiHere);
                        lastLogonUi = logonUiHere;
                    }

                    // 等消息（最多 5ms），保持"有响应"的状态
                    IntPtr noHandle = IntPtr.Zero;
                    Win32.MsgWaitForMultipleObjects(0, ref noHandle, false, 5, Win32.QS_ALLINPUT);
                    Win32.MSG pending;
                    while (Win32.PeekMessageW(out pending, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
                    {
                        if (pending.message == 0x0012) { running = false; break; }   // WM_QUIT
                        Win32.TranslateMessage(ref pending);
                        Win32.DispatchMessageW(ref pending);
                    }
                    waited += 5;
                }
                if (running && !movedOnScreen)
                {
                    movedOnScreen = true;
                    /**
                     * 显示窗口 —— 这是整个方案的关键一步。
                     *
                     * 窗口在**登录界面还在时就已经建好、并且首帧画面已经画进窗口表面**，
                     * 所以这里只是"把它呈现出来"，不需要创建、布局、合成。
                     * `ShowWindow` 之后立刻 `UpdateWindow` 强制刷一次，
                     * 保证屏幕上出现的就是那帧完整画面（而不是一块未就绪的区域）。
                     *
                     * 配合 5ms 的轮询间隔，从"登录界面退场"到"动画铺满屏幕"
                     * 实测在同一帧内（客户要的正是"登录界面一结束就弹出来"）。
                     */
                    Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
                    Win32.UpdateWindow(hwnd);
                    /**
                     * 登录界面已退场 → 把窗口提升到**置顶带**，稳稳压住已经画好的桌面。
                     *
                     * 之前它待在非置顶带的最前，已经盖住了桌面；
                     * 这一步是双保险：防止登录界面退场瞬间有别的窗口（shell、托盘）
                     * 抢到它上面，也防止桌面重绘时短暂露出。
                     */
                    Win32.SetWindowPos(hwnd, (IntPtr)(-1) /*HWND_TOPMOST*/, 0, 0, screenW, screenH,
                        Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
                    Log("桌面已出现 → 封面已显示（进程启动算起 " + clock.ElapsedMilliseconds
                        + "ms，其中等待桌面 " + waited + "ms）");
                }
            }

            Win32.MSG msg;
            while (running)
            {
                /**
                 * 同时等两个事件 + 窗口消息。
                 * 数组顺序即返回值：0 = desktopReady，1 = handoff。
                 */
                IntPtr[] waits = new IntPtr[] { desktopReady, handoff };
                uint wait = Win32.MsgWaitForMultipleObjects(2, ref waits[0], false, 120, Win32.QS_ALLINPUT);

                // ① 桌面马上要出现 → 把封面搬回屏幕（这一步必须最早，否则穿帮）
                if (wait == Win32.WAIT_OBJECT_0 && !movedOnScreen)
                {
                    movedOnScreen = true;
                    Win32.SetWindowPos(hwnd, (IntPtr)(-1), 0, 0, screenW, screenH,
                        Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
                    Log("收到桌面就绪信号 → 封面已搬上屏（" + clock.ElapsedMilliseconds + "ms）");
                }

                /**
                 * ② 只要已经上屏，就**持续保持置顶**。
                 *
                 * 为什么必须每轮都做（客户实测的穿帮）：
                 *   23:38:08.990 原生封面已搬上屏
                 *   23:38:12.669 WPF 窗口才显形
                 *   中间 3.7 秒里，WPF 的窗口正在被创建/激活，会把它顶到本窗口之上，
                 *   于是桌面从缝里露出来 —— 这就是"桌面先出现"的穿帮。
                 *
                 * 本窗口与 WPF 显示的是**同一帧画面**（都是视频第一帧的缓存图），
                 * 所以反复置顶不会产生可见的闪烁；而它保证了这段时间里
                 * 屏幕上始终是动画的封面，桌面永远不会露出来。
                 *
                 * 120ms 一次：足够密（人眼分辨不出 120ms 的空档），又不占 CPU。
                 */
                /**
                 * 兜底：如果**主程序已经退出**，本窗口必须跟着退出。
                 *
                 * 实测踩到的坑：主程序播完就退出了（WPF 窗口销毁），
                 * 但本进程因为没收到交接信号而**继续留在屏幕上**，
                 * 于是露出一张静止封面、而且它还在"等登录界面"的状态里 ——
                 * 客户看到"播完后又出现一个定格画面，而且卡"。
                 *
                 * 判据用主程序进程是否还在：它没了，就说明这次播放已经结束，
                 * 本窗口没有任何理由继续留着。
                 */
                /**
                 * 兜底：**主程序曾经在跑、之后又退出了** → 本窗口跟着退出。
                 *
                 * 这里必须判断"曾经在跑"，不能只看"现在不在"：
                 * 本进程由 Run 键启动，那时主程序**还没被计划任务拉起**，
                 * 如果只看"现在不在"就会立刻自杀（实测发生过：进程 42ms 就退出了）。
                 *
                 * 所以用一个 seenMain 标志：先看到过主程序，之后它消失，才认为是"播放结束"。
                 * 这样既不会误杀，也保证播完之后不会残留一张静止封面。
                 */
                /**
                 * 兜底：**主程序心跳**停了 → 本窗口一并退出。
                 *
                 * 为什么用文件心跳而不是"查主程序进程在不在"（实测踩到的坑）：
                 *   · 本进程由 Run 键启动，那时主程序还没被拉起 —— 只看"现在不在"
                 *     会误判成"已退出"，进程 42ms 就自杀了
                 *   · 登录场景下会有多个同名进程交错启动/退出，按进程名判断不可靠
                 *     （实测出现了两个本进程，其中一个始终不退）
                 *
                 * 心跳的语义最干净：主程序只要活着，就每 500ms 更新一次心跳文件的
                 * 修改时间；超过 3 秒没更新，就说明主程序已经结束（或根本没起来），
                 * 本窗口没有任何理由继续留在屏幕上 —— 直接退出，不留静止画面。
                 *
                 * 这里也顺便处理了"主程序从没起来"的情况：3 秒后自然退出。
                 */
                if (movedOnScreen)
                {
                    try
                    {
                        string hb = HeartbeatPath;
                        bool alive = false;
                        if (hb != null && File.Exists(hb))
                        {
                            alive = (DateTime.UtcNow - File.GetLastWriteTimeUtc(hb)).TotalSeconds < 0.7;
                        }
                        if (!alive)
                        {
                            Log("主程序心跳已停 → 本窗口一并退出（不残留静止画面）");
                            running = false;
                            Win32.PostMessageW(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            break;
                        }
                    }
                    catch { }
                }

                if (movedOnScreen && handoff != IntPtr.Zero
                    && Win32.WaitForSingleObject(handoff, 0) != Win32.WAIT_OBJECT_0)
                {
                    Win32.SetWindowPos(hwnd, (IntPtr)(-1), 0, 0, screenW, screenH,
                        Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
                }

                // ② WPF 已接管同一帧画面 → 本窗口退出
                if (handoff != IntPtr.Zero && wait == Win32.WAIT_OBJECT_0 + 1)
                {
                    Log("收到交接信号（WPF 已接管同帧画面），本窗口退出");
                    running = false;
                    Win32.PostMessageW(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    break;
                }

                // 把队列里的消息处理掉
                while (Win32.PeekMessageW(out msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
                {
                    if (msg.message == 0x0012) { running = false; break; }   // WM_QUIT
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessageW(ref msg);
                }
            }
            if (handoff != IntPtr.Zero) { try { Win32.CloseHandle(handoff); } catch { } }

            Mark("loop-end");
            Cleanup();
            Log("原生窗口退出，时间线: " + timeline);
            return 0;
        }

        /// <summary>
        /// 把首帧图读进一个内存 DC。
        ///
        /// 用 GDI 而不是 D3D 贴图的原因：这是"窗口一出现就有画面"的关键一步，
        /// 而 GDI 的 `StretchBlt` 走的是最成熟的路径 —— 没有设备创建、没有着色器、
        /// 没有 vtable 槽位风险。实时视频才需要 D3D（见文件头的阶段说明）。
        /// </summary>
        private void LoadPoster()
        {
            if (posterPath == null || !File.Exists(posterPath))
            {
                Log("首帧图不存在，窗口会是纯黑（这是需要修的状态）: " + posterPath);
                return;
            }
            try
            {
                using (Bitmap src = new Bitmap(posterPath))
                {
                    bmpW = src.Width;
                    bmpH = src.Height;
                    IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
                    memDc = Win32.CreateCompatibleDC(screenDc);

                    Win32.BITMAPINFOHEADER hdr = new Win32.BITMAPINFOHEADER();
                    hdr.biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER));
                    hdr.biWidth = bmpW;
                    hdr.biHeight = -bmpH;              // 负高度 = 自上而下，与 GDI+ 的内存布局一致
                    hdr.biPlanes = 1;
                    hdr.biBitCount = 32;
                    hdr.biCompression = Win32.BI_RGB;

                    IntPtr bits;
                    memBitmap = Win32.CreateDIBSection(screenDc, ref hdr, Win32.DIB_RGB_COLORS,
                        out bits, IntPtr.Zero, 0);
                    Win32.ReleaseDC(IntPtr.Zero, screenDc);

                    if (memBitmap == IntPtr.Zero || bits == IntPtr.Zero)
                    {
                        Log("CreateDIBSection 失败 err=" + Marshal.GetLastWin32Error());
                        return;
                    }

                    // 把 GDI+ 的位图逐行拷进 DIB 的像素缓冲（32bpp BGRA 与 GDI+ 的 ARGB 布局一致）
                    BitmapData data = src.LockBits(new Rectangle(0, 0, bmpW, bmpH),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int rowBytes = bmpW * 4;
                        for (int y = 0; y < bmpH; y++)
                        {
                            IntPtr dst = (IntPtr)((long)bits + (long)y * rowBytes);
                            IntPtr srow = (IntPtr)((long)data.Scan0 + (long)y * data.Stride);
                            CopyMemory(dst, srow, (IntPtr)rowBytes);
                        }
                    }
                    finally { src.UnlockBits(data); }

                    oldBitmap = Win32.SelectObject(memDc, memBitmap);
                    Log("首帧图已装入内存 DC: " + bmpW + "x" + bmpH);
                }
            }
            catch (Exception ex)
            {
                Log("装入首帧图失败: " + ex.Message);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr dst, IntPtr src, IntPtr len);

        private static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            NativeWindowPlayer self = current;
            if (self == null) return Win32.DefWindowProcW(h, m, w, l);

            switch (m)
            {
                case Win32.WM_ERASEBKGND:
                    // 返回非 0 = 背景已处理，避免系统用背景刷擦出一片黑
                    return (IntPtr)1;

                case Win32.WM_PAINT:
                    {
                        Win32.PAINTSTRUCT ps;
                        Win32.BeginPaint(h, out ps);
                        self.Paint();
                        Win32.EndPaint(h, ref ps);
                        return IntPtr.Zero;
                    }

                case Win32.WM_TIMER:
                    Win32.KillTimer(h, (IntPtr)1);
                    self.running = false;
                    Win32.PostQuitMessage(0);
                    return IntPtr.Zero;

                case Win32.WM_LBUTTONUP:
                case Win32.WM_KEYDOWN:
                    self.running = false;
                    Win32.PostQuitMessage(0);
                    return IntPtr.Zero;

                case Win32.WM_CLOSE:
                case Win32.WM_DESTROY:
                    self.running = false;
                    Win32.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return Win32.DefWindowProcW(h, m, w, l);
        }

        /// <summary>
        /// 把首帧图**拉伸铺满**窗口（与 --fit stretch 的语义一致：不留边、允许变形）。
        /// </summary>
        private void Paint()
        {
            IntPtr dc = Win32.GetDC(hwnd);
            if (dc == IntPtr.Zero) return;
            try
            {
                if (memDc != IntPtr.Zero && bmpW > 0 && bmpH > 0)
                {
                    Win32.SetStretchBltMode(dc, Win32.HALFTONE);
                    Win32.StretchBlt(dc, 0, 0, screenW, screenH, memDc, 0, 0, bmpW, bmpH, Win32.SRCCOPY);
                }
                else
                {
                    // 拿不到首帧图时也不能留黑：画一个深色底，至少不是"未显示"
                    Win32.RECT r; r.Left = 0; r.Top = 0; r.Right = screenW; r.Bottom = screenH;
                    IntPtr br = Win32.CreateSolidBrush(0x000C0A09);
                    Win32.FillRect(dc, ref r, br);
                    Win32.DeleteObject(br);
                }
            }
            finally { Win32.ReleaseDC(hwnd, dc); }
        }

        private void Cleanup()
        {
            try
            {
                if (memDc != IntPtr.Zero && oldBitmap != IntPtr.Zero)
                    Win32.SelectObject(memDc, oldBitmap);
                if (memBitmap != IntPtr.Zero) Win32.DeleteObject(memBitmap);
                if (memDc != IntPtr.Zero) Win32.DeleteDC(memDc);
            }
            catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════ 入口

    internal static class Entry
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string poster = null;
            string log = null;
            int seconds = 0;
            bool topmost = true;
            bool startHidden = false;   // 先建在屏幕外，等"桌面就绪"信号再搬上屏

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--poster" && i + 1 < args.Length) poster = args[++i];
                else if (args[i] == "--log" && i + 1 < args.Length) log = args[++i];
                else if (args[i] == "--seconds" && i + 1 < args.Length)
                    int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
                else if (args[i] == "--no-topmost") topmost = false;
                else if (args[i] == "--hidden") startHidden = true;
                else if (args[i] == "--probe")
                {
                    // --probe：只测"窗口上屏要多久"，随即退出，供自动化验收
                    return ProbeWindow(poster, log);
                }
            }

            NativeWindowPlayer player = new NativeWindowPlayer(poster, log, seconds, topmost, startHidden);
            return player.Run();
        }

        /// <summary>量"进程启动 → 窗口上屏"这一段，用来验收原生窗口是否真的够快。</summary>
        private static int ProbeWindow(string poster, string log)
        {
            Stopwatch sw = Stopwatch.StartNew();
            IntPtr instance = Win32.GetModuleHandleW(null);
            Win32.WNDCLASSEX wc = new Win32.WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(Win32.WNDCLASSEX));
            Win32.WndProcD p = new Win32.WndProcD(delegate(IntPtr hwnd2, uint m, IntPtr w, IntPtr l)
            {
                return Win32.DefWindowProcW(hwnd2, m, w, l);
            });
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(p);
            wc.hInstance = instance;
            wc.lpszClassName = "BootAnimationNativeProbeWnd";
            Win32.RegisterClassExW(ref wc);

            int sw2 = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            int sh2 = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
            long afterClass = sw.ElapsedMilliseconds;
            IntPtr h = Win32.CreateWindowExW(Win32.WS_EX_TOOLWINDOW, "BootAnimationNativeProbeWnd", "probe",
                Win32.WS_POPUP, 0, 0, sw2, sh2, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            long afterWindow = sw.ElapsedMilliseconds;
            Win32.ShowWindow(h, Win32.SW_SHOWNOACTIVATE);
            Win32.UpdateWindow(h);
            long afterShow = sw.ElapsedMilliseconds;

            string text = "原生窗口计时: class=" + afterClass + "ms  window=" + afterWindow
                + "ms  shown=" + afterShow + "ms  total=" + sw.ElapsedMilliseconds + "ms";
            // 不写 Console：这个程序是 /target:winexe，没有控制台，写 stdout 可能抛异常，
            // 反而把后面的文件写入跳过（实测报告不生成就是这个原因）。
            // 报告写到固定位置 + 调用方指定位置，两处都试。
            string[] targets = new string[] { log, Path.Combine(Path.GetTempPath(), "native-window-timing.txt") };
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == null) continue;
                try { File.WriteAllText(targets[i], text, Encoding.UTF8); } catch { }
            }

            Win32.PostQuitMessage(0);
            return 0;
        }
    }
}
