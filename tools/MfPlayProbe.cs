// MfPlayProbe.cs —— 逐个确认 IMFPMediaPlayer 的 vtable 槽位
//
// 为什么必须先做这个探针：
//   原生视频播放要用 MFPlay（mfplay.dll!MFPCreateMediaPlayer）。它是 COM 接口，
//   而这台机器上**没有 Windows SDK**（没有 mfplay.h / d3d11.h 可核对），
//   槽位只能靠实测确认。
//
//   上一次在 IDXGISwapChain 上靠猜槽位付出了代价：E_INVALIDARG →
//   DXGI_ERROR_INVALID_CALL → 访问冲突 0xC0000005。教训是：
//   **一个槽位都不能猜，必须逐个用"调用后看结果"验证。**
//
// 本探针的策略：
//   1. 建一个原生窗口（已验证可行，3ms）
//   2. MFStartup → MFPCreateMediaPlayer → IMFPMediaPlayer
//   3. 只调用**一个**槽位，用"返回 HRESULT 是否合理"判断它是不是我以为的那个方法
//   4. 把每个槽位的调用结果打出来，供人工判读
//
// 关键判据（不需要 SDK 也能判断）：
//   · SetVideoWindow 在正确的槽位上会返回 S_OK（0）
//   · SetVolume 传 0.5f 会返回 S_OK
//   · CreateMediaItemFromURL 传一个**存在的文件**会返回 S_OK
//   · 如果某个槽位返回 E_NOINTERFACE / 崩溃 / 荒谬的值，说明槽位不对
//
// 编译: csc /target:winexe /out:MfPlayProbe.exe MfPlayProbe.cs
// 输出: %TEMP%\mfplay-probe.txt

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MfPlayProbe
{
    internal static class Program
    {
        // ═══════════════════════════════════════ Win32

        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int L, T, R, B; }

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

        internal delegate IntPtr WndProcD(IntPtr h, uint m, IntPtr w, IntPtr l);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string n);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr LoadLibraryA(string n);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr m, string n);

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

        // ═══════════════════════════════════════ 探针主体

        private static StringBuilder log = new StringBuilder();
        private static string outPath;
        private static Stopwatch clock = Stopwatch.StartNew();

        private static void L(string s)
        {
            log.Append(clock.ElapsedMilliseconds.ToString().PadLeft(5)).Append("ms  ").Append(s).AppendLine();
        }

        /// <summary>
        /// 读 COM 对象的第 slot 个函数指针并转成委托。
        /// slot 已含 IUnknown 的 3 个（QueryInterface/AddRef/Release）。
        /// </summary>
        private static T Fn<T>(IntPtr comObject, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(comObject);
            IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        [STAThread]
        private static int Main(string[] args)
        {
            outPath = Path.Combine(Path.GetTempPath(), "mfplay-probe.txt");
            string media = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--media" && i + 1 < args.Length) media = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
            }
            if (media == null)
            {
                media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BootAnimation", "fast", "nativedog1__nativedog1-447c7d96.mp4");
            }

            L("=== MFPlay 槽位探针 ===");
            L("目标视频: " + media);
            L("视频存在: " + File.Exists(media));

            // ① MFStartup
            IntPtr mfplat = LoadLibraryA("mfplat.dll");
            if (mfplat == IntPtr.Zero) { L("!! mfplat.dll 加载失败"); return 1; }
            IntPtr pStartup = GetProcAddress(mfplat, "MFStartup");
            if (pStartup == IntPtr.Zero) { L("!! 找不到 MFStartup"); return 2; }
            MfStartupD startup = (MfStartupD)(object)Marshal.GetDelegateForFunctionPointer(pStartup, typeof(MfStartupD));
            // MFSTARTUP_FULL = 0, MFSTARTUP_LITE = 1；版本用 MF_VERSION = 0x00020070
            int hr = startup(0x00020070, 0);
            L("MFStartup hr=0x" + hr.ToString("X8") + (hr == 0 ? "  (S_OK)" : "  ** 失败 **"));
            if (hr != 0) { L("Media Foundation 起不来，后续无法进行"); Dump(); return 3; }

            // ② 原生窗口
            IntPtr inst = GetModuleHandleW(null);
            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
            WndProcD proc = new WndProcD(delegate(IntPtr h, uint m, IntPtr w, IntPtr l)
            {
                return DefWindowProcW(h, m, w, l);
            });
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc);   // 必须保住委托
            wc.hInstance = inst;
            wc.lpszClassName = "MfPlayProbeWnd";
            RegisterClassExW(ref wc);
            int sw = GetSystemMetrics(SM_CXSCREEN);
            int sh = GetSystemMetrics(SM_CYSCREEN);
            IntPtr hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, "MfPlayProbeWnd", "MFPlayProbe",
                WS_POPUP, 0, 0, sw, sh, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { L("!! CreateWindowExW 失败"); Dump(); return 4; }
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            UpdateWindow(hwnd);
            L("原生窗口已上屏 hwnd=" + hwnd);

            // ③ MFPCreateMediaPlayer
            IntPtr mfplay = LoadLibraryA("mfplay.dll");
            if (mfplay == IntPtr.Zero) { L("!! mfplay.dll 加载失败"); Dump(); return 5; }
            IntPtr pCreate = GetProcAddress(mfplay, "MFPCreateMediaPlayer");
            if (pCreate == IntPtr.Zero) { L("!! 找不到 MFPCreateMediaPlayer"); Dump(); return 6; }
            MfCreateMediaPlayerD create =
                (MfCreateMediaPlayerD)(object)Marshal.GetDelegateForFunctionPointer(pCreate, typeof(MfCreateMediaPlayerD));

            IntPtr player;
            // MFPCreateMediaPlayer(url, startPlayback=false, flags=MFP_OPTION_NONE,
            //                      callback=null, hWnd, out player)
            hr = create(media, false, 0, IntPtr.Zero, hwnd, out player);
            L("MFPCreateMediaPlayer hr=0x" + hr.ToString("X8") + " player=0x" + player.ToString("X"));
            if (hr != 0 || player == IntPtr.Zero)
            {
                L("** 带 URL 的创建失败，改试不传 URL 的创建方式（更常见） **");
                hr = create(null, false, 0, IntPtr.Zero, hwnd, out player);
                L("MFPCreateMediaPlayer(url=null) hr=0x" + hr.ToString("X8") + " player=0x" + player.ToString("X"));
            }
            if (hr != 0 || player == IntPtr.Zero) { L("!! 拿不到播放器，探针终止"); Dump(); return 7; }

            // ④ 逐个槽位探测。IMFPMediaPlayer 的 vtable 顺序（IUnknown 占 0..2）：
            //    3 SetFrameStep  4 SetVolume  5 GetVolume  6 SetMute  7 GetMute
            //    8 SetLooping  9 SetPlaybackRate  10 GetPlaybackRate  11 GetSupportedRates
            //    12 SetPlaybackRateThinning  13 SetPlaybackRateThinningMode
            //    14 GetPlaybackRateThinningMode  15 SetMediaItem  16 GetMediaItem
            //    17 CreateMediaItemFromURL  18 CreateMediaItemFromObject  19 GetMediaItemFromURL ...
            //    24 SetVideoWindow
            // 这些序号是**推测**，所以下面逐个试，用返回值判断。
            L("");
            L("=== 逐个槽位实测（判据：返回 0 才可能是对的） ===");

            // 试 SetVideoWindow：猜测在 24，但把 20..28 都试一遍并记录结果
            for (int slot = 20; slot <= 28; slot++)
            {
                try
                {
                    SetVideoWindowD f = Fn<SetVideoWindowD>(player, slot);
                    int r = f(player, hwnd);
                    L("  槽位 " + slot + " (试作 SetVideoWindow) → hr=0x" + r.ToString("X8")
                        + (r == 0 ? "   ← 很可能是 SetVideoWindow" : ""));
                }
                catch (Exception ex)
                {
                    L("  槽位 " + slot + " 调用异常: " + ex.GetType().Name + " " + ex.Message);
                }
            }

            // 试 SetVolume（只有一个 float 参数，传 0.7 应返回 S_OK）
            L("");
            for (int slot = 3; slot <= 8; slot++)
            {
                try
                {
                    SetVolumeD f = Fn<SetVolumeD>(player, slot);
                    int r = f(player, 0.7f);
                    L("  槽位 " + slot + " (试作 SetVolume) → hr=0x" + r.ToString("X8")
                        + (r == 0 ? "   ← 很可能是 SetVolume/SetMute 一族" : ""));
                }
                catch (Exception ex)
                {
                    L("  槽位 " + slot + " 调用异常: " + ex.GetType().Name);
                }
            }

            L("");
            L("=== 探针结束（没有崩溃） ===");
            Dump();
            return 0;
        }

        // 函数签名（只写真正要调的那几个）
        internal delegate int MfStartupD(uint version, uint flags);
        internal delegate int MfCreateMediaPlayerD(string url, bool startPlayback, uint flags,
            IntPtr callback, IntPtr hwnd, out IntPtr player);
        internal delegate int SetVideoWindowD(IntPtr self, IntPtr hwnd);
        internal delegate int SetVolumeD(IntPtr self, float volume);

        private static void Dump()
        {
            try { File.WriteAllText(outPath, log.ToString(), Encoding.UTF8); } catch { }
        }
    }
}
