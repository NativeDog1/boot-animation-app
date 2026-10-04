// NativeProbe.cs —— 验证"绕开 WPF 用原生窗口 + Direct3D 11"的可行性
//
// 为什么先写这个探针，而不是直接改主程序：
//   要判断"原生渲染能不能解决那 250ms 的启动空窗"，必须先回答两个问题：
//     1. 用 csc（C# 5）+ 手写 COM 接口，能不能真的创建 D3D11 设备与交换链？
//     2. **原生窗口从 CreateWindowEx 到第一次 Present 要多久**？
//   这两个问题不确认就动手改主程序，风险太大（几千行 interop 写完才发现方向错）。
//
// 它做的事：建一个原生窗口 → D3D11 设备 + 交换链 → 循环 Present 一个画面。
// 用 --measure 时把各阶段耗时写到指定文件，供外部读取。
//
// 编译（和主程序同一套约束，不引入任何第三方依赖）：
//   csc /target:winexe /out:NativeProbe.exe NativeProbe.cs

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NativeProbe
{
    // ═══════════════════════════════════════════════════════ Win32

    internal static class Win32
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] internal static extern bool UpdateWindow(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] internal static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr GetModuleHandleW(string name);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] internal static extern IntPtr SetCapture(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();

        internal const uint WS_POPUP = 0x80000000;
        internal const uint WS_VISIBLE = 0x10000000;
        internal const uint WS_EX_TOPMOST = 0x00000008;
        internal const uint WS_EX_TOOLWINDOW = 0x00000080;
        internal const uint WS_EX_NOACTIVATE = 0x08000000;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const int SM_CXSCREEN = 0;
        internal const int SM_CYSCREEN = 1;
    }

    // ═══════════════════════════════════════════════════════ Direct3D 11

    internal enum D3D_DRIVER_TYPE { Unknown = 0, Hardware = 1, Reference = 2, Null = 3, Software = 4, WARP = 5 }

    [Flags]
    internal enum D3D11_CREATE_DEVICE_FLAG : uint
    {
        SingleThreaded = 0x1,
        Debug = 0x2,
        BgraSupport = 0x20,
        BGRA_SUPPORT = 0x20,
    }

    internal enum DXGI_FORMAT : uint
    {
        UNKNOWN = 0,
        R8G8B8A8_UNORM = 28,
        B8G8R8A8_UNORM = 87,
        B8G8R8X8_UNORM = 88,
    }

    internal enum DXGI_SWAP_EFFECT : uint { DISCARD = 0, SEQUENTIAL = 1, FLIP_SEQUENTIAL = 3, FLIP_DISCARD = 4 }

    [Flags]
    internal enum DXGI_SWAP_CHAIN_FLAG : uint { NONPREROTATED = 1, ALLOW_MODE_SWITCH = 2, NONE = 0 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_MODE_DESC
    {
        public uint Width, Height;
        public uint RefreshRateNumerator, RefreshRateDenominator;
        public DXGI_FORMAT Format;
        public uint ScanlineOrdering;
        public uint Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_SAMPLE_DESC { public uint Count, Quality; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_SWAP_CHAIN_DESC
    {
        public DXGI_MODE_DESC BufferDesc;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint BufferUsage;
        public uint BufferCount;
        public IntPtr OutputWindow;
        public int Windowed;
        public DXGI_SWAP_EFFECT SwapEffect;
        public DXGI_SWAP_CHAIN_FLAG Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public DXGI_FORMAT Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_SUBRESOURCE_DATA
    {
        public IntPtr pSysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_RENDER_TARGET_VIEW_DESC
    {
        public DXGI_FORMAT Format;
        public uint ViewDimension;
        public uint Unused1, Unused2, Unused3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIEWPORT
    {
        public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    internal static class D3D11
    {
        [DllImport("d3d11.dll")]
        internal static extern int D3D11CreateDevice(
            IntPtr adapter, D3D_DRIVER_TYPE driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
            out IntPtr device, out uint featureLevel, out IntPtr context);
    }

    internal static class DXGI
    {
        [DllImport("dxgi.dll")]
        internal static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
    }

    /// <summary>
    /// D3D11 设备。用 [ComImport] 声明接口 —— csc 原生支持，不需要任何 SDK。
    ///
    /// 方法顺序**必须**与 d3d11.h 里的 vtable 顺序完全一致，一个都不能错位：
    /// 错位不会编译报错，只会在运行时调用到别的函数、表现为访问冲突或莫名失败。
    /// 所以这里只声明真正要用的那几个，并且按头文件顺序排列。
    /// </summary>
    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ID3D11Device
    {
        // IUnknown 的三个方法由 CLR 处理，不写在这里
        int CreateBuffer(IntPtr desc, IntPtr initialData, out IntPtr buffer);
        int CreateTexture1D(IntPtr desc, IntPtr initialData, out IntPtr texture);
        int CreateTexture2D(ref D3D11_TEXTURE2D_DESC desc, ref D3D11_SUBRESOURCE_DATA initialData, out IntPtr texture);
        int CreateTexture3D(IntPtr desc, IntPtr initialData, out IntPtr texture);
        int CreateShaderResourceView(IntPtr resource, IntPtr desc, out IntPtr view);
        int CreateUnorderedAccessView(IntPtr resource, IntPtr desc, out IntPtr view);
        int CreateRenderTargetView(IntPtr resource, IntPtr desc, out IntPtr view);
        int CreateDepthStencilView(IntPtr resource, IntPtr desc, out IntPtr view);
        int CreateInputLayout(IntPtr desc, uint count, IntPtr shaderBytecode, IntPtr size, out IntPtr layout);
        int CreateVertexShader(IntPtr bytecode, IntPtr size, IntPtr link, out IntPtr shader);
        int CreateGeometryShader(IntPtr bytecode, IntPtr size, IntPtr link, out IntPtr shader);
        int CreatePixelShader(IntPtr bytecode, IntPtr size, IntPtr link, out IntPtr shader);
    }

    /// <summary>D3D11 设备上下文。同样只声明要用的方法，顺序按头文件。</summary>
    [ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ID3D11DeviceContext
    {
        void VSSetConstantBuffers(uint start, uint count, IntPtr buffers);
        void PSSetShaderResources(uint start, uint count, IntPtr views);
        void PSSetShader(IntPtr shader, IntPtr classInstances, uint count);
        void PSSetSamplers(uint start, uint count, IntPtr samplers);
        void VSSetShader(IntPtr shader, IntPtr classInstances, uint count);
        int DrawIndexed(uint indexCount, uint startIndex, int baseVertex);
        void Draw(uint vertexCount, uint startVertex);
        int Map(IntPtr resource, uint subresource, uint mapType, uint mapFlags, out D3D11_MAPPED_SUBRESOURCE mapped);
        void Unmap(IntPtr resource, uint subresource);
        void PSSetConstantBuffers(uint start, uint count, IntPtr buffers);
        void IASetInputLayout(IntPtr layout);
        void IASetVertexBuffers(uint start, uint count, IntPtr buffers, IntPtr strides, IntPtr offsets);
        void IASetIndexBuffer(IntPtr buffer, DXGI_FORMAT format, uint offset);
        void DrawIndexedInstanced(uint indexCountPerInstance, uint instanceCount, uint startIndex, int baseVertex, uint startInstance);
        void DrawInstanced(uint vertexCountPerInstance, uint instanceCount, uint startVertex, uint startInstance);
        void GSSetConstantBuffers(uint start, uint count, IntPtr buffers);
        void GSSetShader(IntPtr shader, IntPtr classInstances, uint count);
        void IASetPrimitiveTopology(uint topology);
        void VSSetShaderResources(uint start, uint count, IntPtr views);
        void VSSetSamplers(uint start, uint count, IntPtr samplers);
        void Begin(IntPtr async);
        void End(IntPtr async);
        int GetData(IntPtr async, IntPtr data, uint dataSize, uint flags);
        void SetPredication(IntPtr predicate, bool value);
        void GSSetShaderResources(uint start, uint count, IntPtr views);
        void GSSetSamplers(uint start, uint count, IntPtr samplers);
        void OMSetRenderTargets(uint count, ref IntPtr renderTargetViews, IntPtr depthStencil);
        void OMSetRenderTargetsAndUnorderedAccessViews(uint rtCount, IntPtr rtViews, IntPtr dsView,
            uint uavStart, uint uavCount, IntPtr uavs, IntPtr uavCounts);
        void OMSetBlendState(IntPtr blend, IntPtr blendFactor, uint sampleMask);
        void OMSetDepthStencilState(IntPtr state, uint stencilRef);
        void SOSetTargets(uint count, IntPtr targets, IntPtr offsets);
        void DrawAuto();
        void DrawIndexedInstancedIndirect(IntPtr buffer, uint offset);
        void DrawInstancedIndirect(IntPtr buffer, uint offset);
        void Dispatch(uint x, uint y, uint z);
        void DispatchIndirect(IntPtr buffer, uint offset);
        void RSSetState(IntPtr state);
        void RSSetViewports(uint count, ref D3D11_VIEWPORT viewports);
        void RSSetScissorRects(uint count, IntPtr rects);
        void CopySubresourceRegion(IntPtr dst, uint dstSub, uint dstX, uint dstY, uint dstZ,
            IntPtr src, uint srcSub, IntPtr srcBox);
        void CopyResource(IntPtr dst, IntPtr src);
        void UpdateSubresource(IntPtr dst, uint dstSub, IntPtr box, IntPtr srcData, uint srcRowPitch, uint srcDepthPitch);
        void CopyStructureCount(IntPtr dst, uint dstSub, IntPtr src);
        void ClearRenderTargetView(IntPtr view, ref float color);
        void ClearUnorderedAccessViewUint(IntPtr view, IntPtr values);
        void ClearUnorderedAccessViewFloat(IntPtr view, IntPtr values);
        void ClearDepthStencilView(IntPtr view, uint flags, float depth, byte stencil);
        void GenerateMips(IntPtr view);
        void SetResourceMinLOD(IntPtr resource, float minLod);
        float GetResourceMinLOD(IntPtr resource);
        void ResolveSubresource(IntPtr dst, uint dstSub, IntPtr src, uint srcSub, DXGI_FORMAT format);
        void ExecuteCommandList(IntPtr list, bool restoreState);
        void HSSetShaderResources(uint start, uint count, IntPtr views);
        void HSSetShader(IntPtr shader, IntPtr classInstances, uint count);
        void HSSetSamplers(uint start, uint count, IntPtr samplers);
        void HSSetConstantBuffers(uint start, uint count, IntPtr buffers);
        void DSSetShaderResources(uint start, uint count, IntPtr views);
        void DSSetShader(IntPtr shader, IntPtr classInstances, uint count);
        void DSSetSamplers(uint start, uint count, IntPtr samplers);
        void DSSetConstantBuffers(uint start, uint count, IntPtr buffers);
        void CSSetShaderResources(uint start, uint count, IntPtr views);
        void CSSetUnorderedAccessViews(uint start, uint count, IntPtr uavs, IntPtr uavCounts);
        void CSSetShader(IntPtr shader, IntPtr classInstances, uint count);
        void CSSetSamplers(uint start, uint count, IntPtr samplers);
        void CSSetConstantBuffers(uint start, uint count, IntPtr buffers);
        void VSGetConstantBuffers(uint start, uint count, IntPtr buffers);
        void PSGetShaderResources(uint start, uint count, IntPtr views);
        void PSGetShader(IntPtr shader, IntPtr classInstances, IntPtr count);
        void PSGetSamplers(uint start, uint count, IntPtr samplers);
        void VSGetShader(IntPtr shader, IntPtr classInstances, IntPtr count);
        void PSGetConstantBuffers(uint start, uint count, IntPtr buffers);
        void IAGetInputLayout(IntPtr layout);
        void IAGetVertexBuffers(uint start, uint count, IntPtr buffers, IntPtr strides, IntPtr offsets);
        void IAGetIndexBuffer(IntPtr buffer, IntPtr format, IntPtr offset);
        void GSGetConstantBuffers(uint start, uint count, IntPtr buffers);
        void GSGetShader(IntPtr shader, IntPtr classInstances, IntPtr count);
        void IAGetPrimitiveTopology(IntPtr topology);
        void VSGetShaderResources(uint start, uint count, IntPtr views);
        void VSGetSamplers(uint start, uint count, IntPtr samplers);
        void GetPredication(IntPtr predicate, IntPtr value);
        void GSGetShaderResources(uint start, uint count, IntPtr views);
        void GSGetSamplers(uint start, uint count, IntPtr samplers);
        void OMGetRenderTargets(uint count, IntPtr views, IntPtr depthStencil);
        void OMGetRenderTargetsAndUnorderedAccessViews(uint rtCount, IntPtr rtViews, IntPtr dsView, uint uavStart, uint uavCount, IntPtr uavs);
        void OMGetBlendState(IntPtr blend, IntPtr blendFactor, IntPtr sampleMask);
        void OMGetDepthStencilState(IntPtr state, IntPtr stencilRef);
        void SOGetTargets(uint count, IntPtr targets);
        void RSGetState(IntPtr state);
        void RSGetViewports(IntPtr count, IntPtr viewports);
        void RSGetScissorRects(IntPtr count, IntPtr rects);
        void HSGetShaderResources(uint start, uint count, IntPtr views);
        void HSGetShader(IntPtr shader, IntPtr classInstances, IntPtr count);
        void HSGetSamplers(uint start, uint count, IntPtr samplers);
        void HSGetConstantBuffers(uint start, uint count, IntPtr buffers);
        void DSGetShaderResources(uint start, uint count, IntPtr views);
        void DSGetShader(IntPtr shader, IntPtr classInstances, IntPtr count);
        void DSGetSamplers(uint start, uint count, IntPtr samplers);
        void DSGetConstantBuffers(uint start, uint count, IntPtr buffers);
        void CSGetShaderResources(uint start, uint count, IntPtr views);
        void CSGetUnorderedAccessViews(uint start, uint count, IntPtr uavs);
        void CSGetShader(IntPtr shader, IntPtr classInstances, IntPtr count);
        void CSGetSamplers(uint start, uint count, IntPtr samplers);
        void CSGetConstantBuffers(uint start, uint count, IntPtr buffers);
        void ClearState();
        void Flush();
        uint GetType();
        uint GetContextFlags();
        int FinishCommandList(bool restore, out IntPtr commandList);
    }

    [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIDevice
    {
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        int GetParent(ref Guid riid, out IntPtr parent);
        int GetAdapter(out IntPtr adapter);
        int CreateSurface(IntPtr desc, uint numSurfaces, DXGI_FORMAT usage, IntPtr sharedResource, out IntPtr surface);
        int QueryResourceResidency(IntPtr resources, IntPtr residency, uint numResources);
        int SetGPUThreadPriority(int priority);
        int GetGPUThreadPriority(out int priority);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter
    {
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        int GetParent(ref Guid riid, out IntPtr parent);
        int EnumOutputs(uint output, out IntPtr o);
        int GetDesc(IntPtr desc);
        int CheckInterfaceSupport(ref Guid name, out long umdVersion);
    }

    [ComImport, Guid("310d36a0-d2e7-4c0a-aa04-6a9d23b8886a"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGISwapChain
    {
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        int GetParent(ref Guid riid, out IntPtr parent);
        int GetDevice(ref Guid riid, out IntPtr device);
        int Present(uint syncInterval, uint flags);
        int GetBuffer(uint buffer, ref Guid riid, out IntPtr surface);
        int SetFullscreenState(bool fullscreen, IntPtr target);
        int GetFullscreenState(out bool fullscreen, out IntPtr target);
        int GetDesc(out DXGI_SWAP_CHAIN_DESC desc);
        int ResizeBuffers(uint bufferCount, uint width, uint height, DXGI_FORMAT newFormat, DXGI_SWAP_CHAIN_FLAG flags);
        int ResizeTarget(IntPtr newTargetParameters);
        int GetContainingOutput(out IntPtr output);
        int GetFrameStatistics(IntPtr stats);
        int GetLastPresentCount(out uint lastPresentCount);
    }

    [ComImport, Guid("aec22fb8-76f3-4639-9be0-28eb43a67a2e"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory
    {
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        int GetParent(ref Guid riid, out IntPtr parent);
        int EnumAdapters(uint adapter, out IntPtr a);
        int MakeWindowAssociation(IntPtr window, uint flags);
        int GetWindowAssociation(out IntPtr window);
        int CreateSwapChain(IntPtr device, ref DXGI_SWAP_CHAIN_DESC desc, out IntPtr swapChain);
        int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
    }

    // ═══════════════════════════════════════════════════════ 探针主体

    internal static class Probe
    {

        private static readonly Guid IID_IDXGISwapChain = new Guid("310d36a0-d2e7-4c0a-aa04-6a9d23b8886a");
        private static readonly Guid IID_IDXGIFactory = new Guid("aec22fb8-76f3-4639-9be0-28eb43a67a2e");

        private static IntPtr hwnd;
        private static IDXGISwapChain swapChain;
        private static ID3D11DeviceContext context;
        private static Stopwatch clock = Stopwatch.StartNew();
        private static StringBuilder timeline = new StringBuilder();
        private static string reportPath;

        private static void Mark(string what)
        {
            timeline.Append(what).Append('@').Append(clock.ElapsedMilliseconds).Append("ms  ");
        }

        private static IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            const uint WM_DESTROY = 0x0002;
            const uint WM_CLOSE = 0x0010;
            const uint WM_KEYDOWN = 0x0100;
            const uint WM_LBUTTONUP = 0x0202;
            if (msg == WM_CLOSE || msg == WM_DESTROY) { Win32.PostQuitMessage(0); return IntPtr.Zero; }
            if (msg == WM_KEYDOWN || msg == WM_LBUTTONUP) { Win32.PostQuitMessage(0); return IntPtr.Zero; }
            return Win32.DefWindowProc(h, msg, w, l);
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

            // ── 原生窗口。先建窗口（这是"用户看到的第一眼"，必须最早）
            IntPtr instance = Win32.GetModuleHandleW(null);
            Win32.WNDCLASSEX wc = new Win32.WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(Win32.WNDCLASSEX));
            wc.style = 0;
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(new Win32.WndProc(WndProc));
            wc.hInstance = instance;
            wc.hbrBackground = IntPtr.Zero;
            wc.lpszClassName = "NativeProbeWnd";
            if (Win32.RegisterClassEx(ref wc) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                // 已注册过不算失败
                if (err != 1410) { Report("RegisterClassEx 失败 err=" + err); return 2; }
            }
            Mark("class-registered");

            int sw = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            int sh = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
            hwnd = Win32.CreateWindowEx(Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
                "NativeProbeWnd", "NativeProbe", Win32.WS_POPUP, 0, 0, sw, sh,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Report("CreateWindowEx 失败 err=" + Marshal.GetLastWin32Error()); return 3; }
            Mark("window-created");

            // ── D3D11 设备
            IntPtr devicePtr, contextPtr;
            uint featureLevel;
            int hr = D3D11.D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE.Hardware, IntPtr.Zero,
                (uint)(D3D11_CREATE_DEVICE_FLAG.BgraSupport | D3D11_CREATE_DEVICE_FLAG.SingleThreaded),
                IntPtr.Zero, 0, 7, out devicePtr, out featureLevel, out contextPtr);
            if (hr < 0) { Report("D3D11CreateDevice 失败 hr=0x" + hr.ToString("X8")); return 4; }
            Mark("d3d-device");

            object devObj = Marshal.GetObjectForIUnknown(devicePtr);
            ID3D11Device device = devObj as ID3D11Device;
            if (device == null) { Report("拿不到 ID3D11Device"); return 5; }
            context = Marshal.GetObjectForIUnknown(contextPtr) as ID3D11DeviceContext;
            if (context == null) { Report("拿不到 ID3D11DeviceContext"); return 6; }
            // CreateSwapChain 还要用设备指针：显式加一次引用，避免包装对象的行为差异
            Marshal.AddRef(devicePtr);

            // ── 交换链
            // 拿 IDXGIDevice 要用 QueryInterface，不能靠 ID3D11Device 的 vtable
            // （GetParent 不在 ID3D11Device 上，它是 IDXGIObject 的方法）。
            IntPtr dxgiDevicePtr;
            IntPtr unknown = Marshal.GetIUnknownForObject(device);
            Guid iidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            hr = Marshal.QueryInterface(unknown, ref iidDxgiDevice, out dxgiDevicePtr);
            if (hr < 0) { Report("QueryInterface(IDXGIDevice) 失败 hr=0x" + hr.ToString("X8")); return 7; }
            IDXGIDevice dxgiDevice = Marshal.GetObjectForIUnknown(dxgiDevicePtr) as IDXGIDevice;
            if (dxgiDevice == null) { Report("拿不到 IDXGIDevice"); return 8; }
            Mark("dxgi-device");

            IntPtr adapterPtr;
            hr = dxgiDevice.GetAdapter(out adapterPtr);
            if (hr < 0) { Report("GetAdapter 失败 hr=0x" + hr.ToString("X8")); return 9; }
            IDXGIAdapter adapter = Marshal.GetObjectForIUnknown(adapterPtr) as IDXGIAdapter;

            IntPtr factoryPtr;
            Guid iidFactory = new Guid("aec22fb8-76f3-4639-9be0-28eb43a67a2e");
            hr = DXGI.CreateDXGIFactory1(ref iidFactory, out factoryPtr);
            if (hr < 0) { Report("CreateDXGIFactory1 失败 hr=0x" + hr.ToString("X8")); return 10; }
            IDXGIFactory factory = Marshal.GetObjectForIUnknown(factoryPtr) as IDXGIFactory;
            if (factory == null) { Report("拿不到 IDXGIFactory"); return 11; }
            Mark("dxgi-factory");

            DXGI_SWAP_CHAIN_DESC scd = new DXGI_SWAP_CHAIN_DESC();
            scd.BufferDesc.Width = (uint)sw;
            scd.BufferDesc.Height = (uint)sh;
            scd.BufferDesc.Format = DXGI_FORMAT.B8G8R8A8_UNORM;
            scd.SampleDesc.Count = 1;
            scd.BufferUsage = 0x20;   // DXGI_USAGE_RENDER_TARGET_OUTPUT
            scd.BufferCount = 2;
            scd.OutputWindow = hwnd;
            scd.Windowed = 1;
            scd.SwapEffect = DXGI_SWAP_EFFECT.DISCARD;
            scd.Flags = DXGI_SWAP_CHAIN_FLAG.NONE;

            IntPtr scPtr;
            hr = factory.CreateSwapChain(devicePtr, ref scd, out scPtr);
            if (hr < 0) { Report("CreateSwapChain 失败 hr=0x" + hr.ToString("X8")); return 12; }
            swapChain = Marshal.GetObjectForIUnknown(scPtr) as IDXGISwapChain;
            if (swapChain == null) { Report("拿不到 IDXGISwapChain"); return 13; }
            Mark("swap-chain");

            // ── 第一次上屏：显示窗口 + 立即 Present 一帧纯色
            Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
            Win32.SetWindowPos(hwnd, (IntPtr)(-1), 0, 0, sw, sh,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
            Mark("window-shown");

            IntPtr backBufferPtr;
            Guid iidTex = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // ID3D11Texture2D
            hr = swapChain.GetBuffer(0, ref iidTex, out backBufferPtr);
            if (hr < 0) { Report("GetBuffer 失败 hr=0x" + hr.ToString("X8")); return 14; }

            IntPtr rtvPtr;
            hr = device.CreateRenderTargetView(backBufferPtr, IntPtr.Zero, out rtvPtr);
            if (hr < 0) { Report("CreateRenderTargetView 失败 hr=0x" + hr.ToString("X8")); return 15; }

            // 先确认前面两步真的拿到了对象，而不是静默返回了 0
            if (backBufferPtr == IntPtr.Zero) { Report("GetBuffer 得到空指针"); return 17; }
            if (rtvPtr == IntPtr.Zero) { Report("CreateRenderTargetView 得到空指针"); return 18; }
            timeline.Append("rtv=").Append(rtvPtr).Append("  ");

            float[] color = new float[] { 0.05f, 0.05f, 0.08f, 1.0f };
            try
            {
                context.OMSetRenderTargets(1, ref rtvPtr, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Report("OMSetRenderTargets 失败: " + ex.GetType().Name + ": " + ex.Message);
                return 19;
            }
            context.ClearRenderTargetView(rtvPtr, ref color[0]);
            hr = swapChain.Present(1, 0);
            Mark("first-present");
            if (hr < 0) { Report("Present 失败 hr=0x" + hr.ToString("X8")); return 16; }

            // ── 循环 Present，直到用户点击/按键或超时
            Stopwatch run = Stopwatch.StartNew();
            Win32.MSG msg;
            while (run.Elapsed.TotalSeconds < seconds)
            {
                while (Win32.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessage(ref msg);
                }
                // 每帧换一点颜色，便于外部用截图判断"画面在刷"
                color[0] = 0.05f + (float)(0.4 * Math.Abs(Math.Sin(run.Elapsed.TotalSeconds * 3.0)));
                context.ClearRenderTargetView(rtvPtr, ref color[0]);
                swapChain.Present(1, 0);
            }
            Mark("loop-end");

            Report(null);
            return 0;
        }

        private static void Report(string failure)
        {
            timeline.Append("total=").Append(clock.ElapsedMilliseconds).Append("ms");
            string text = (failure == null ? "OK" : "FAIL") + "  " + (failure ?? "") + Environment.NewLine + timeline.ToString();
            if (reportPath != null)
            {
                try { File.WriteAllText(reportPath, text, Encoding.UTF8); } catch { }
            }
            Console.WriteLine(text);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception ex)
            {
                Report("异常: " + ex.ToString());
                return 99;
            }
        }
    }
}
