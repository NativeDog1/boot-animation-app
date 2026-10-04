// BootTimeline.cs —— 极轻量的启动时间线采样器
//
// 目的：客户实测"登录后 20–30 秒才出现开机动画"，而这台机器（32GB / NVMe / 16 核）
// 硬件上完全不应该。要定位它必须拿到**真实登录环境**的数据 ——
// 在已登录的桌面上测量是测不出来的（我之前的错误就在这里）。
//
// 设计原则：它自己必须**极其轻**，否则它会成为被测对象的一部分而污染数据。
//   · 不引用 WPF、不引用 System.Drawing，只 P/Invoke（编译量极小，CLR 启动最快）
//   · 不用 WMI/CIM（那些查询本身要几百毫秒）
//   · 只做 4 件事：查进程是否存在、读两个文件的长度、查进程 CPU/内存、写一行日志
//   · 每 100ms 一次，持续 90 秒，然后自己退出
//
// 它会记录的关键判据：
//   1. explorer.exe 何时出现（= 桌面就绪，作为时间基准）
//   2. BootAnimation.exe / NativePlayer.exe 何时出现、何时消失
//   3. 这两个进程的 CPU 累计与内存 —— 分辨它是"在忙"还是"在等"
//   4. boot-animation.log 与 boot.log 的增长 —— 程序自己走到哪一步了
//
// 编译: csc /target:winexe /out:BootTimeline.exe BootTimeline.cs
// 用法: BootTimeline.exe [--seconds 90] [--out <路径>]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BootTimelineProbe
{
    internal static class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern void OutputDebugStringW(string s);

        private static string outPath;
        private static StringBuilder buffer = new StringBuilder();
        private static readonly object gate = new object();

        [STAThread]
        private static int Main(string[] args)
        {
            int seconds = 90;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--seconds" && i + 1 < args.Length)
                    int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
                else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
            }
            if (outPath == null)
            {
                outPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BootAnimation", "boot-timeline.log");
            }

            Stopwatch clock = Stopwatch.StartNew();
            Write("================================================================");
            Write("启动时间线采样开始（进程启动为 0 基准）");
            Write("采样间隔 100ms，持续 " + seconds + " 秒");
            Write("自身启动时刻: " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));

            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BootAnimation");
            string mainLog = Path.Combine(dataDir, "boot-animation.log");
            string clockLog = Path.Combine(dataDir, "boot.log");

            /**
             * 记录"同期还有哪些重进程在抢资源"。
             *
             * 为什么需要：这台机器上装着 **Wallpaper Engine**（wallpaper64 常驻），
             * 它和开机动画抢同一套东西 —— D3D 设备、视频硬件解码器、全屏渲染。
             * 如果它在动画启动的那一刻正在初始化动态壁纸，几十秒的等待就有了来源，
             * 而且那与我的程序无关。所以要把它和其它重进程的存在与 CPU 一起记下来。
             *
             * 每 2 秒采一次，避免自己成为负担。
             */
            string[] heavyNames = new string[]
            {
                "wallpaper64", "wallpaperservice64", "webwallpaper32", "ui32",
                "dwm", "explorer", "SearchIndexer", "MsMpEng", "HipsTray", "HipsDaemon",
                "OneDrive", "QQNT", "douyin", "LGHUB", "BaiduNetdisk"
            };

            bool explorerSeen = false;
            long explorerAt = 0;
            Dictionary<string, bool> firstSeen = new Dictionary<string, bool>();
            Dictionary<string, bool> goneSeen = new Dictionary<string, bool>();
            Dictionary<string, long> firstAt = new Dictionary<string, long>();
            Dictionary<string, string> startTime = new Dictionary<string, string>();
            long lastMainLen = -1;
            long lastClockLen = -1;

            while (clock.Elapsed.TotalSeconds < seconds)
            {
                long t = clock.ElapsedMilliseconds;

                // ① 桌面就绪基准
                if (!explorerSeen)
                {
                    Process[] ex = Process.GetProcessesByName("explorer");
                    if (ex.Length > 0)
                    {
                        explorerSeen = true;
                        explorerAt = t;
                        Write("explorer.exe 出现（桌面就绪）  +" + t + "ms");
                        for (int i = 0; i < ex.Length; i++) ex[i].Dispose();
                    }
                }

                // ② 被测的两个进程
                string[] names = new string[] { "BootAnimation", "NativePlayer" };
                for (int n = 0; n < names.Length; n++)
                {
                    string name = names[n];
                    Process[] procs = Process.GetProcessesByName(name);
                    if (procs.Length > 0)
                    {
                        if (!firstSeen.ContainsKey(name))
                        {
                            firstSeen[name] = true;
                            firstAt[name] = t;
                            string st = "?";
                            try { st = procs[0].StartTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture); }
                            catch { }
                            startTime[name] = st;
                            string rel = explorerSeen
                                ? ("（桌面就绪后 +" + (t - explorerAt) + "ms）")
                                : "（桌面尚未就绪）";
                            Write(name + " 首次出现  +" + t + "ms " + rel
                                + "  进程启动于 " + st
                                + "  内存=" + (procs[0].WorkingSet64 / 1048576) + "MB");
                        }
                        // 每秒记一次状态：CPU 累计是否在涨，决定它是"在忙"还是"在等"
                        if (t % 1000 < 100)
                        {
                            string cpu = "?";
                            try { cpu = ((long)procs[0].TotalProcessorTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture); }
                            catch { }
                            bool resp = false;
                            try { resp = procs[0].Responding; } catch { }
                            Write("  [" + name + "] 内存=" + (procs[0].WorkingSet64 / 1048576)
                                + "MB  CPU累计=" + cpu + "ms  响应=" + resp);
                        }
                    }
                    else if (firstSeen.ContainsKey(name) && !goneSeen.ContainsKey(name))
                    {
                        goneSeen[name] = true;
                        long alive = t - firstAt[name];
                        Write(name + " 已退出  +" + t + "ms（存活 " + alive + "ms）");
                    }
                    for (int i = 0; i < procs.Length; i++) procs[i].Dispose();
                }

                // ③ 程序自身的日志增长 —— 最能说明"卡在哪一步"
                lastMainLen = ReportGrowth(mainLog, lastMainLen, "主日志");
                lastClockLen = ReportGrowth(clockLog, lastClockLen, "boot.log");

                // ④ 每 2 秒记一次"同期竞争者"的内存与 CPU，看谁在吃机器
                if (t % 2000 < 100)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int h = 0; h < heavyNames.Length; h++)
                    {
                        Process[] hp = Process.GetProcessesByName(heavyNames[h]);
                        if (hp.Length == 0) continue;
                        long mem = 0; long cpu = 0;
                        try { mem = hp[0].WorkingSet64 / 1048576; } catch { }
                        try { cpu = (long)hp[0].TotalProcessorTime.TotalMilliseconds; } catch { }
                        sb.Append(heavyNames[h]).Append('(').Append(mem).Append("MB/").Append(cpu).Append("ms) ");
                        for (int k = 0; k < hp.Length; k++) hp[k].Dispose();
                    }
                    if (sb.Length > 0) Write("  同期进程: " + sb.ToString());
                }

                Thread.Sleep(100);
            }

            Write("采样结束（" + seconds + " 秒）");
            Write("================================================================");
            Flush();
            return 0;
        }

        private static long ReportGrowth(string path, long lastLen, string label)
        {
            try
            {
                if (!File.Exists(path)) return lastLen;
                FileInfo fi = new FileInfo(path);
                if (fi.Length == lastLen) return lastLen;
                string[] lines = File.ReadAllLines(path);
                int from = lines.Length > 5 ? lines.Length - 5 : 0;
                Write(label + " 增长到 " + fi.Length + " 字节，新增内容:");
                for (int i = from; i < lines.Length; i++) Write("      " + lines[i]);
                return fi.Length;
            }
            catch { return lastLen; }
        }

        /// <summary>
        /// 写一行日志。**每次都立即落盘**（不是攒着最后写）：
        /// 因为要观测的场景正是"进程可能卡住/被杀"，攒着写会丢掉最关键的那一段。
        /// </summary>
        private static void Write(string line)
        {
            string text = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line;
            lock (gate)
            {
                try { File.AppendAllText(outPath, text + Environment.NewLine, Encoding.UTF8); }
                catch { }
            }
            try { OutputDebugStringW(text); } catch { }
        }

        private static void Flush()
        {
            lock (gate) { buffer = new StringBuilder(); }
        }
    }
}
