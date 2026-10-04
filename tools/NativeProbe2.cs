// NativeProbe2.cs —— 用"直接读 vtable 调函数指针"验证原生窗口 + D3D11 的启动耗时
//
// 为什么要换掉 [ComImport] 那版：
//   `[ComImport]` 接口里凡是"数组型"参数（OMSetRenderTargets 的视图数组、
//   PSSetShaderResources、RSSetViewports…）都会被 CLR 编组器拒绝，抛
//   `ArgumentException: 值不在预期的范围内`。实测确认指针本身是有效的
//   （rtv=1611908640），所以不是 D3D 的问题，是编组器的问题。
//
//   绕开办法：把 COM 接口指针当成"指向 vtable 的指针"，读第 N 个函数指针后
//   用 Marshal.GetDelegateForFunctionPointer 直接调用。这样：
//     · 没有编组器参与，数组参数就是普通指针
//     · 调用约定由 UnmanagedFunctionPointer 显式指定，不靠猜
//     · 探针已经核对过 vtable 顺序（tools 里那个脚本），可以放心按下标取
//
// 它测的是"原生渲染能不能把 WPF 那 250ms 拿回来"，以及各阶段到底花多少。

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NativeProbe2
{
    // ═══════════════════════════════════════════════════ Win32

    internal static class W
    {
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

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt;
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandleW(string n);

        internal const uint WS_POPUP = 0x80000000;
        internal const uint WS_EX_TOPMOST = 0x00000008;
        internal const uint WS_EX_TOOLWINDOW = 0x00000080;
        internal const uint WS_EX_NOACTIVATE = 0x08000000;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const uint SWP_NOACTIVATE = 0x0010, SWP_NOSIZE = 0x0001;
        internal const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    }

    // ═══════════════════════════════════════════════════ 结构体 / 枚举

    internal enum DXGI_FORMAT : uint { UNKNOWN = 0, R8G8B8A8_UNORM = 28, B8G8R8A8_UNORM = 87 }
    internal enum SWAP_EFFECT : uint { DISCARD = 0, SEQUENTIAL = 1, FLIP_DISCARD = 4 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_MODE_DESC
    {
        public uint Width, Height, Num, Den;
        public DXGI_FORMAT Format;
        public uint ScanlineOrdering, Scaling;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct DXGI_SAMPLE_DESC { public uint Count, Quality; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_SWAP_CHAIN_DESC
    {
        public DXGI_MODE_DESC BufferDesc;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint BufferUsage, BufferCount;
        public IntPtr OutputWindow;
        public int Windowed;
        public SWAP_EFFECT SwapEffect;
        public uint Flags;
    }

    // ═══════════════════════════════════════════════════ vtable 调用

    /// <summary>
    /// 从 COM 接口指针读第 slot 个函数（slot 已含 IUnknown 的 3 个）。
    /// 用泛型 + Marshal.GetDelegateForFunctionPointer 拿到可直接调用的委托。
    /// </summary>
    internal static class Vt
    {
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr dst, IntPtr src, IntPtr len);

        internal static T Fn<T>(IntPtr comObject, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(comObject);
            IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }
    }

    // D3D11CreateDevice
    internal delegate int D3D11CreateDeviceD(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out uint featureLevel, out IntPtr context);

    // IDXGIFactory 的方法在 IDXGIObject 之后：EnumAdapters(3) MakeWindowAssociation(4)
    // GetWindowAssociation(5) CreateSwapChain(6) CreateSoftwareAdapter(7) —— 含 IUnknown 共 3，slot 从 3 起
    internal delegate int CreateSwapChainD(IntPtr self, IntPtr device, ref DXGI_SWAP_CHAIN_DESC desc, out IntPtr swapChain);

    // IDXGISwapChain: GetBuffer 在 IDXGIObject 4 个方法之后 → slot 3+4=7? 
    // IDXGIObject: SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent
    // IDXGISwapChain: Present, GetBuffer, SetFullscreenState, GetFullscreenState, GetDesc,
    //                 ResizeBuffers, ResizeTarget, GetContainingOutput, GetFrameStatistics, GetLastPresentCount
    internal delegate int PresentD(IntPtr self, uint syncInterval, uint flags);
    internal delegate int GetBufferD(IntPtr self, uint index, ref Guid riid, out IntPtr surface);
    internal delegate int SwapChainDescD(IntPtr self, out DXGI_SWAP_CHAIN_DESC desc);

    // ID3D11Device: CreateBuffer(3) CreateTexture1D(4) CreateTexture2D(5) CreateTexture3D(6)
    //               CreateShaderResourceView(7) CreateUnorderedAccessView(8) CreateRenderTargetView(9)
    internal delegate int CreateRenderTargetViewD(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RTV_DESC
    {
        public DXGI_FORMAT Format;
        public uint ViewDimension;
        public uint Unused0;
        public uint MipSlice;
        public uint Unused1, Unused2, Unused3, Unused4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public DXGI_FORMAT Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }
    internal delegate int CreateTexture2DD(IntPtr self, ref TEXTURE2D_DESC desc, IntPtr initial, out IntPtr texture);

    // ID3D11DeviceContext: 见 tools 的 vtable 核对脚本。
    // OMSetRenderTargets 是第 29 个（slot 索引 3..29 → 下标 3+26=29）
    internal delegate void OMSetRenderTargetsD(IntPtr self, uint count, IntPtr views, IntPtr depth);
    internal delegate void ClearRenderTargetViewD(IntPtr self, IntPtr view, ref float color);
    internal delegate void CopyResourceD(IntPtr self, IntPtr dst, IntPtr src);
    internal delegate void UpdateSubresourceD(IntPtr self, IntPtr dst, uint sub, IntPtr box,
        IntPtr srcData, uint rowPitch, uint depthPitch);

    internal static class P
    {
        private static IntPtr hwnd;
        private static IntPtr swapChain;
        private static IntPtr context;
        private static IntPtr device;
        private static IntPtr rtv;
        private static Stopwatch clock = Stopwatch.StartNew();
        private static StringBuilder tl = new StringBuilder();
        private static string reportPath;

        private static void Mark(string s) { tl.Append(s).Append('@').Append(clock.ElapsedMilliseconds).Append("ms  "); }

        private static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            if (m == 0x0002 || m == 0x0010) { W.PostQuitMessage(0); return IntPtr.Zero; }   // DESTROY / CLOSE
            if (m == 0x0100 || m == 0x0202) { W.PostQuitMessage(0); return IntPtr.Zero; }   // KEYDOWN / LBUTTONUP
            return W.DefWindowProcW(h, m, w, l);
        }

        private static int Run(string[] args)
        {
            int seconds = 3;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--seconds" && i + 1 < args.Length) int.TryParse(args[i + 1], out seconds);
                if (args[i] == "--report" && i + 1 < args.Length) reportPath = args[i + 1];
            }
            clock = Stopwatch.StartNew();
            Mark("start");

            // ── 原生窗口
            IntPtr inst = W.GetModuleHandleW(null);
            W.WNDCLASSEX wc = new W.WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(W.WNDCLASSEX));
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(new W.WndProcD(WndProc));
            wc.hInstance = inst;
            wc.lpszClassName = "NativeProbe2Wnd";
            if (W.RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
            { Report("RegisterClassExW 失败 err=" + Marshal.GetLastWin32Error()); return 2; }
            Mark("class");

            int sw = W.GetSystemMetrics(W.SM_CXSCREEN);
            int sh = W.GetSystemMetrics(W.SM_CYSCREEN);
            hwnd = W.CreateWindowExW(W.WS_EX_TOPMOST | W.WS_EX_TOOLWINDOW | W.WS_EX_NOACTIVATE,
                "NativeProbe2Wnd", "NativeProbe2", W.WS_POPUP, 0, 0, sw, sh,
                IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Report("CreateWindowExW 失败 err=" + Marshal.GetLastWin32Error()); return 3; }
            Mark("window-created");

            // ── D3D11 设备（走 d3d11.dll 导出函数，不需要 vtable）
            IntPtr d3d11 = LoadLibraryW("d3d11.dll");
            if (d3d11 == IntPtr.Zero) { Report("加载 d3d11.dll 失败"); return 4; }
            IntPtr fnPtr = GetProcAddress(d3d11, "D3D11CreateDevice");
            if (fnPtr == IntPtr.Zero) { Report("找不到 D3D11CreateDevice"); return 5; }
            D3D11CreateDeviceD create = (D3D11CreateDeviceD)(object)Marshal.GetDelegateForFunctionPointer(
                fnPtr, typeof(D3D11CreateDeviceD));

            uint fl;
            int hr = create(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, 0x20 /*BGRA_SUPPORT*/, IntPtr.Zero, 0, 7,
                out device, out fl, out context);
            if (hr < 0) { Report("D3D11CreateDevice hr=0x" + hr.ToString("X8")); return 6; }
            Mark("d3d-device");

            // ── DXGI 工厂 + 交换链
            IntPtr dxgi = LoadLibraryW("dxgi.dll");
            IntPtr fnFactory = GetProcAddress(dxgi, "CreateDXGIFactory1");
            CreateFactoryD cf = (CreateFactoryD)(object)Marshal.GetDelegateForFunctionPointer(
                fnFactory, typeof(CreateFactoryD));
            Guid iidFactory = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            IntPtr factory;
            hr = cf(ref iidFactory, out factory);
            if (hr < 0) { Report("CreateDXGIFactory1 hr=0x" + hr.ToString("X8")); return 7; }
            Mark("dxgi-factory");

            DXGI_SWAP_CHAIN_DESC scd = new DXGI_SWAP_CHAIN_DESC();
            scd.BufferDesc.Width = (uint)sw;
            scd.BufferDesc.Height = (uint)sh;
            scd.BufferDesc.Format = DXGI_FORMAT.B8G8R8A8_UNORM;
            scd.SampleDesc.Count = 1;
            scd.BufferUsage = 0x20;
            scd.BufferCount = 2;
            scd.OutputWindow = hwnd;
            scd.Windowed = 1;
            scd.SwapEffect = SWAP_EFFECT.DISCARD;
            // IDXGIFactory 的 CreateSwapChain 是第 6 个方法（含 IUnknown 3 个 → slot 索引 3+3=6? ）
            // IDXGIObject: 3,4,5,6 (SetPrivateData/SetPrivateDataInterface/GetPrivateData/GetParent)
            // 所以 EnumAdapters=7? 不对 —— 下标从 0 计：IUnknown 占 0,1,2；
            // IDXGIObject 占 3,4,5,6；IDXGIFactory 的 EnumAdapters=7, MakeWindowAssociation=8,
            // GetWindowAssociation=9, CreateSwapChain=10
            CreateSwapChainD csc = Vt.Fn<CreateSwapChainD>(factory, 10);
            IntPtr sc;
            hr = csc(factory, device, ref scd, out sc);
            if (hr < 0) { Report("CreateSwapChain hr=0x" + hr.ToString("X8")); return 8; }
            swapChain = sc;
            Mark("swap-chain");

            // ── 显示窗口（这是"用户看到的第一眼"）
            W.ShowWindow(hwnd, W.SW_SHOWNOACTIVATE);
            W.SetWindowPos(hwnd, (IntPtr)(-1), 0, 0, sw, sh, W.SWP_NOACTIVATE);
            W.UpdateWindow(hwnd);
            Mark("shown");

            // ── 后台缓冲 + RTV
            // IDXGISwapChain: Present=3, GetBuffer=4? 下标：IUnknown 0,1,2；
            // IDXGIObject 3,4,5,6；IDXGISwapChain 的 Present=7, GetBuffer=8
            Guid iidTex = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            // 先把 swap chain 的 vtable 全部槽位地址打出来，再用"能返回合理数据的那个槽"定位。
            // 之前靠猜槽位已经错过两次（一次调错槽返回 E_INVALIDARG、一次 DXGI_ERROR_INVALID_CALL），
            // 所以改成先枚举、再按返回值判断哪一个才是真正的 GetBuffer。
            IntPtr vt = Marshal.ReadIntPtr(swapChain);
            StringBuilder slots = new StringBuilder("swapchain-vtable: ");
            for (int s = 0; s < 14; s++)
            {
                slots.Append(s).Append('=').Append(Marshal.ReadIntPtr(vt, s * IntPtr.Size).ToString("X")).Append(' ');
            }
            tl.Append(slots.ToString()).Append("  ");

            IntPtr back = IntPtr.Zero;
            int usedSlot = -1;
            for (int s = 3; s < 14 && back == IntPtr.Zero; s++)
            {
                try
                {
                    GetBufferD tryGb = Vt.Fn<GetBufferD>(swapChain, s);
                    IntPtr candidate;
                    Guid g2 = iidTex;
                    int thr = tryGb(swapChain, 0, ref g2, out candidate);
                    if (thr >= 0 && candidate != IntPtr.Zero && s != 7 /*Present*/)
                    {
                        back = candidate; usedSlot = s;
                    }
                }
                catch { }
            }
            if (back == IntPtr.Zero) { Report("所有槽位都没能拿到后台缓冲"); return 9; }
            tl.Append("GetBuffer槽=").Append(usedSlot).Append("  ");
            tl.Append("back=").Append(back).Append("  GetBufferHR=0x").Append(hr.ToString("X8")).Append("  ");
            Mark("backbuffer");
            // 用 GetDesc 反查交换链真实格式，确认与 RTV 描述一致
            SwapChainDescD descFn = Vt.Fn<SwapChainDescD>(swapChain, 11);
            DXGI_SWAP_CHAIN_DESC realDesc;
            int dhr = descFn(swapChain, out realDesc);
            tl.Append("descHR=0x").Append(dhr.ToString("X8"))
              .Append(" fmt=").Append(realDesc.BufferDesc.Format)
              .Append(" ").Append(realDesc.BufferDesc.Width).Append("x").Append(realDesc.BufferDesc.Height)
              .Append(" count=").Append(realDesc.BufferCount).Append("  ");

            // ID3D11Device 的 CreateRenderTargetView 下标：IUnknown 0,1,2；
            // CreateBuffer=3 … CreateRenderTargetView=9
            CreateRenderTargetViewD crtv = Vt.Fn<CreateRenderTargetViewD>(device, 9);
            // 显式给出 RTV 描述：desc=null 依赖"用资源本身的格式"这一默认行为，
            // 在部分驱动上会返回 E_INVALIDARG。写明格式与维度最稳。
            RTV_DESC rtvDesc = new RTV_DESC();
            rtvDesc.Format = DXGI_FORMAT.B8G8R8A8_UNORM;
            rtvDesc.ViewDimension = 1;   // D3D11_RTV_DIMENSION_TEXTURE2D
            IntPtr rtvDescPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RTV_DESC)));
            Marshal.StructureToPtr(rtvDesc, rtvDescPtr, false);
            hr = crtv(device, back, rtvDescPtr, out rtv);
            if (hr < 0) { Report("CreateRenderTargetView hr=0x" + hr.ToString("X8")); return 10; }
            if (rtv == IntPtr.Zero) { Report("RTV 为空"); return 11; }
            Mark("rtv");

            // ── 第一帧
            OMSetRenderTargetsD om = Vt.Fn<OMSetRenderTargetsD>(context, 29);
            ClearRenderTargetViewD clear = Vt.Fn<ClearRenderTargetViewD>(context, 50);
            PresentD present = Vt.Fn<PresentD>(swapChain, 7);

            // OMSetRenderTargets 的 views 参数是"指向数组的指针"
            IntPtr viewsArr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(viewsArr, rtv);

            float[] col = new float[] { 0.06f, 0.06f, 0.09f, 1.0f };
            om(context, 1, viewsArr, IntPtr.Zero);
            clear(context, rtv, ref col[0]);
            hr = present(swapChain, 1, 0);
            Mark("first-present");
            if (hr < 0) { Report("Present hr=0x" + hr.ToString("X8")); return 12; }

            // ── 循环（换色便于外部判断"画在刷"）
            Stopwatch run = Stopwatch.StartNew();
            W.MSG msg;
            int frames = 0;
            while (run.Elapsed.TotalSeconds < seconds)
            {
                while (W.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    W.TranslateMessage(ref msg);
                    W.DispatchMessageW(ref msg);
                }
                col[0] = 0.06f + (float)(0.45 * Math.Abs(Math.Sin(run.Elapsed.TotalSeconds * 2.5)));
                col[2] = 0.09f + (float)(0.30 * Math.Abs(Math.Cos(run.Elapsed.TotalSeconds * 2.5)));
                clear(context, rtv, ref col[0]);
                present(swapChain, 1, 0);
                frames++;
            }
            Mark("loop-end");
            tl.Append("frames=").Append(frames).Append("  ");
            Report(null);
            return 0;
        }

        internal delegate int CreateFactoryD(ref Guid riid, out IntPtr factory);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private static void Report(string failure)
        {
            tl.Append("total=").Append(clock.ElapsedMilliseconds).Append("ms");
            string text = (failure == null ? "OK" : "FAIL") + "  " + (failure ?? "") + Environment.NewLine + tl.ToString();
            if (reportPath != null) { try { File.WriteAllText(reportPath, text, Encoding.UTF8); } catch { } }
            Console.WriteLine(text);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception ex) { Report("异常: " + ex.GetType().Name + ": " + ex.Message); return 99; }
        }
    }
}
