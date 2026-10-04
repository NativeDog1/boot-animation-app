// LogonProbe.cs —— 判定"登录后动画延迟"的归属
//
// 问题：客户实测登录后 20–30 秒才出现开机动画，而这台机器（32GB / NVMe / 16 核）
// 硬件上完全不该如此。程序自身从启动到画面出现只要约 570ms（boot.log 稳定可查），
// 所以延迟必然落在"程序被拉起之前"或"登录环境特有的阻塞"上。
//
// 这个探针的作用是把责任**明确切开**：
//
//   它和 BootAnimation.exe 一起由 Run 键在登录时启动。
//   如果**它自己**也是过很久才拿到执行机会，那说明 Windows 登录过程本身慢
//   （延迟不归我）；如果它几秒内就跑了、而 BootAnimation 很久才动，
//   那说明问题在 BootAnimation 这一侧。
//
// 判据用三个时间戳对照：
//   ① 本次登录会话的起始时间（WTSQuerySessionInformation 拿不到就用 explorer 的启动时间近似）
//   ② 系统开机时长（GetTickCount64）—— 排除"机器本来就慢"的可能
//   ③ 它自己被启动的时刻 —— 与 ① 的差就是"Windows 把 Run 项拉起来花了多久"
//
// 它同时监控 BootAnimation.exe 的启动时刻与它的 boot.log 首行时间，
// 于是"Windows 慢"和"程序内部慢"能被分开看。
//
// 编译: csc /target:winexe /out:LogonProbe.exe LogonProbe.cs
// 输出: %LOCALAPPDATA%\BootAnimation\logon-probe.log

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LogonProbe
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId,
            int infoClass, out IntPtr buffer, out int bytesReturned);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr p);

        [DllImport("kernel32.dll")]
        private static extern int WTSGetActiveConsoleSessionId();

        private static string outPath;

        [STAThread]
        private static int Main(string[] args)
        {
            int seconds = 120;
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
                    "BootAnimation", "logon-probe.log");
            }

            DateTime now = DateTime.Now;
            ulong uptimeMs = GetTickCount64();

            W("================================================================");
            W("登录延迟归属探针");
            W("现在时刻              : " + now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            W("系统已开机时长        : " + (uptimeMs / 1000) + " 秒  （排除'机器本来就慢'）");

            // ① 登录会话起始时间
            DateTime? logonTime = QueryLogonTime();
            if (logonTime.HasValue)
            {
                double gap = (now - logonTime.Value).TotalSeconds;
                W("本次登录会话起始      : " + logonTime.Value.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                W("→ 从登录到我被启动    : " + Math.Round(gap, 1) + " 秒   **这就是 Windows 拉起 Run 项花的时间**");
            }
            else
            {
                W("拿不到登录会话起始时间，改用 explorer.exe 启动时间近似");
            }

            // ② explorer 启动时间（桌面就绪的近似基准）
            try
            {
                Process[] ex = Process.GetProcessesByName("explorer");
                if (ex.Length > 0)
                {
                    DateTime est = ex[0].StartTime;
                    W("explorer.exe 启动于     : " + est.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + "  → 桌面就绪后 " + Math.Round((now - est).TotalSeconds, 1) + " 秒我才被拉起");
                    for (int i = 0; i < ex.Length; i++) ex[i].Dispose();
                }
                else W("explorer.exe 尚未启动（说明我在桌面就绪前就跑了）");
            }
            catch (Exception e) { W("读 explorer 启动时间失败: " + e.Message); }

            // ③ 监控 BootAnimation 的出现与它自己的日志
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BootAnimation");
            string bootClock = Path.Combine(dataDir, "boot.log");
            string mainLog = Path.Combine(dataDir, "boot-animation.log");

            W("");
            W("开始监控 BootAnimation.exe（每 200ms 一次，共 " + seconds + " 秒）");
            bool seen = false;
            DateTime firstSeen = DateTime.MinValue;
            long lastClockLen = -1;
            Stopwatch sw = Stopwatch.StartNew();

            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (!seen)
                {
                    Process[] ba = Process.GetProcessesByName("BootAnimation");
                    if (ba.Length > 0)
                    {
                        seen = true;
                        firstSeen = DateTime.Now;
                        string st = "?";
                        try { st = ba[0].StartTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture); }
                        catch { }
                        W("BootAnimation.exe 出现  : " + firstSeen.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                            + "   进程启动于 " + st
                            + "   （我启动后 +" + Math.Round((firstSeen - now).TotalSeconds, 1) + " 秒）");
                        for (int i = 0; i < ba.Length; i++) ba[i].Dispose();
                    }
                }
                // 监控 boot.log 增长（程序自己的分段计时）
                try
                {
                    if (File.Exists(bootClock))
                    {
                        FileInfo fi = new FileInfo(bootClock);
                        if (fi.Length != lastClockLen)
                        {
                            lastClockLen = fi.Length;
                            string[] lines = File.ReadAllLines(bootClock);
                            if (lines.Length > 0)
                            {
                                W("  boot.log 最新一行: " + lines[lines.Length - 1]);
                            }
                        }
                    }
                }
                catch { }

                Thread.Sleep(200);
            }

            W("");
            W("结论判读指引:");
            W("  · 若'从登录到我被启动'很大（>10 秒）→ 延迟归 Windows 登录过程，不是程序的问题");
            W("  · 若它很小、但 BootAnimation 出现得很晚 → 延迟在 Run 项被逐个拉起的过程中");
            W("  · 若两者都小 → 延迟在 BootAnimation 进程内部，需要看 boot.log 的分段计时");
            W("================================================================");
            return 0;
        }

        /// <summary>取本次交互式登录会话的起始时间（用于算"登录→Run 项被拉起"的间隔）。</summary>
        private static DateTime? QueryLogonTime()
        {
            try
            {
                int session = WTSGetActiveConsoleSessionId();
                if (session < 0) return null;
                IntPtr buf; int len;
                // WTSInfoClass 的值：WTSConnectState=8, WTSWinStationName=6 … 没有直接的登录时间。
                // 改用"当前进程的会话中，第一个登录进程(winlogon/userinit)的时间"不可靠，
                // 这里退回用 explorer 的启动时间（见调用方），所以本函数返回 null。
                return null;
            }
            catch { return null; }
        }

        private static void W(string line)
        {
            try
            {
                File.AppendAllText(outPath,
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
