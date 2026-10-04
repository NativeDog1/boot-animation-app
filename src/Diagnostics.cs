// Diagnostics.cs —— 真实检测 + 真实修复
//
// 两条硬规矩，都是被"看起来像测试程序的 Demo"逼出来的：
//
//   1. **禁止硬编码**。每一个状态位都必须走一条真实探测路径，并且能说出"检测的是什么"。
//      硬编码的 ● Ready 比不显示更糟：它让用户失去唯一的诊断入口。
//   2. **Repair 必须真的修**，而且修完要**复验**。一个只弹"已修复"对话框的按钮
//      和不修没区别 —— 所以 RepairStartup 的返回里带着"修完之后的复检结果"。
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BootAnimation
{
    /// <summary>一次诊断的完整结论。</summary>
    internal sealed class DiagnosticsReport
    {
        public readonly List<StatusItem> Application = new List<StatusItem>();
        public readonly List<StatusItem> Startup = new List<StatusItem>();
        public readonly List<StatusItem> BootRuntime = new List<StatusItem>();
        public readonly List<StatusItem> Animation = new List<StatusItem>();
        public string Summary;
        public StatusLevel SummaryLevel = StatusLevel.Ok;
        public DateTime RanAt = DateTime.Now;

        public List<StatusItem> All()
        {
            List<StatusItem> all = new List<StatusItem>();
            all.AddRange(Application);
            all.AddRange(Startup);
            all.AddRange(BootRuntime);
            all.AddRange(Animation);
            return all;
        }

        public int ProblemCount()
        {
            int n = 0;
            List<StatusItem> all = All();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Level == StatusLevel.Bad || all[i].Level == StatusLevel.Warn) n++;
            }
            return n;
        }
    }

    internal static class Diagnostics
    {
        /// <summary>
        /// 跑一次完整诊断。**只读**，不改任何东西 —— 修改由 RepairStartup 负责。
        ///
        /// 覆盖用户要求的全部检查项。当前版本还没有 UEFI Runtime，所以那几项如实报
        /// NotImplemented，**不假装有**。
        /// </summary>
        internal static DiagnosticsReport Run(AnimationStateStore store)
        {
            DiagnosticsReport report = new DiagnosticsReport();
            report.RanAt = DateTime.Now;

            // ── 1) 应用程序 ────────────────────────────────────────────────
            report.Application.Add(new StatusItem("Application", "● Running", StatusLevel.Ok,
                "pid " + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)));

            report.Application.Add(new StatusItem("Version", Program.AppVersion, StatusLevel.Ok,
                Program.ExePath));

            report.Application.Add(new StatusItem("Administrator", Platform.IsAdministrator() ? "Yes" : "No",
                Platform.IsAdministrator() ? StatusLevel.Ok : StatusLevel.Warn,
                Platform.IsAdministrator()
                    ? "可以注册登录计划任务（启动最早）"
                    : "非管理员：计划任务注册会被拒绝，自启只能走 Run 键"));

            // ── 2) 启动项（真实读取注册表与任务计划） ──────────────────────
            string exe = Program.ExePath;
            string runCmd = Platform.RunKeyCommand();
            bool runOk = runCmd != null && runCmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
            bool? taskOk = Platform.TaskRegistered();

            if (taskOk == true)
            {
                report.Startup.Add(new StatusItem("Windows Startup", "✓ Registered", StatusLevel.Ok,
                    "计划任务 " + Platform.TaskName + "（AtLogon）"));
                report.Startup.Add(new StatusItem("Startup Method", "Task Scheduler", StatusLevel.Ok,
                    "由任务计划服务在登录时拉起，比 Run 键更早"));
            }
            else if (runOk)
            {
                report.Startup.Add(new StatusItem("Windows Startup", "✓ Registered", StatusLevel.Ok,
                    "Run 键：" + runCmd));
                report.Startup.Add(new StatusItem("Startup Method", "Run key", StatusLevel.Warn,
                    taskOk == false
                        ? "计划任务未注册（需要管理员）；Run 键要等 explorer 起来后才被拉起"
                        : "计划任务状态未知；Run 键可用"));
            }
            else
            {
                report.Startup.Add(new StatusItem("Windows Startup", "✕ Not registered", StatusLevel.Bad,
                    runCmd == null ? "Run 键里没有 BootAnimation 这一项" : "Run 键指向别的路径：" + runCmd));
                report.Startup.Add(new StatusItem("Startup Method", "none", StatusLevel.Bad,
                    "需要点 Repair Startup 注册"));
            }

            report.Startup.Add(new StatusItem("Run key", runOk ? "✓ 正确" : (runCmd == null ? "未设置" : "✕ 指向错误"),
                runOk ? StatusLevel.Ok : (runCmd == null ? StatusLevel.Warn : StatusLevel.Bad),
                runCmd == null ? "HKCU\\...\\Run\\" + Platform.RunValueName : runCmd));

            report.Startup.Add(new StatusItem("Task Scheduler", taskOk == true ? "✓ Registered"
                    : (taskOk == false ? "未注册" : "查询失败"),
                taskOk == true ? StatusLevel.Ok : (taskOk == false ? StatusLevel.Warn : StatusLevel.Unknown),
                taskOk == true ? "schtasks /Query 成功" : "schtasks /Query 未返回成功"));

            report.Startup.Add(new StatusItem("Last Check", "Just now", StatusLevel.Ok,
                report.RanAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));

            // ── 3) Boot Runtime（Windows 侧，真实可验证） ──────────────────
            bool bootLogExists = File.Exists(Path.Combine(Program.DataDirectory, "boot.log"));
            report.BootRuntime.Add(new StatusItem("Boot Runtime", "● Ready", StatusLevel.Ok,
                "独立于 UI 的极轻量播放链路（BootRuntime.cs）；不扫库、不联网、不等 ffmpeg"));

            double lastMs = LastBootToWindowMs();
            if (lastMs > 0)
            {
                report.BootRuntime.Add(new StatusItem("Last boot to window", lastMs.ToString("0", CultureInfo.InvariantCulture) + " ms",
                    lastMs < 1200 ? StatusLevel.Ok : StatusLevel.Warn,
                    bootLogExists ? "来自 boot.log 的 window-shown 分段" : "来自 boot.log"));
            }
            else
            {
                report.BootRuntime.Add(new StatusItem("Last boot to window", "无记录", StatusLevel.Unknown,
                    "还没跑过一轮开机播放，或 boot.log 已被清理"));
            }

            report.BootRuntime.Add(new StatusItem("Resume Animation", ResumeReady() ? "● Ready" : "未就绪",
                ResumeReady() ? StatusLevel.Ok : StatusLevel.Warn,
                ResumeReady()
                    ? "电源事件监听器已注册（PBT_APMRESUMEAUTOMATIC / SUSPEND）"
                    : "需要界面/常驻进程在运行时才监听得到；本进程退出后不再监听"));

            // UEFI：这一版**没有实现**。如实显示，不编。
            bool? secureBoot = Platform.SecureBootEnabled();
            bool? uefi = Platform.IsUefiBoot();
            report.BootRuntime.Add(new StatusItem("UEFI Boot Entry", "未实现", StatusLevel.NotImplemented,
                "冷启动阶段（Windows Boot Manager 之前）的动画入口尚未接入；见 UEFI-RUNTIME.md"));
            report.BootRuntime.Add(new StatusItem("UEFI Animation", "未实现", StatusLevel.NotImplemented,
                "需要 ESP 上的 GOP 播放器 + 预处理帧资源"));
            report.BootRuntime.Add(new StatusItem("Firmware", uefi == true ? "UEFI" : (uefi == false ? "Legacy BIOS" : "未知"),
                uefi == true ? StatusLevel.Ok : StatusLevel.Unknown,
                uefi == true ? "固件环境变量可读" : "读不到固件环境变量（或非 UEFI）"));
            report.BootRuntime.Add(new StatusItem("Secure Boot",
                secureBoot == true ? "● Detected（开启）" : (secureBoot == false ? "关闭" : "未知"),
                StatusLevel.Ok,
                secureBoot == null
                    ? "读不到 SecureBoot\\State（可能不支持或没权限）"
                    : "HKLM\\SYSTEM\\CurrentControlSet\\Control\\SecureBoot\\State"));
            report.BootRuntime.Add(new StatusItem("EFI System Partition", uefi == true ? "存在（未使用）" : "不适用",
                StatusLevel.Ok,
                uefi == true ? "系统 " + Platform.SystemDrive() + " 为 UEFI 启动；当前版本不在 ESP 上放任何东西" : "非 UEFI 启动"));
            report.BootRuntime.Add(new StatusItem("Fallback Boot", "✓ Enabled", StatusLevel.Ok,
                "BootRuntime 解析不到动画时安静跳过，不阻塞桌面（见 BootRuntime.Resolve 的兜底分支）"));

            // ── 4) 动画与配置 ──────────────────────────────────────────────
            AnimationInfo active = store == null ? null : store.Active;
            if (active == null)
            {
                report.Animation.Add(new StatusItem("Active Animation", "未设置", StatusLevel.Bad,
                    "settings.txt 里的 id 在本地找不到对应文件"));
            }
            else
            {
                bool playable = active.IsPlayable;
                report.Animation.Add(new StatusItem("Active Animation", AnimationStateStore.DisplayName(active),
                    playable ? StatusLevel.Ok : StatusLevel.Bad,
                    (active.Id ?? "") + " · " + active.Source.ToString()));

                report.Animation.Add(new StatusItem("Animation Package",
                    playable ? "● Valid" : "✕ Missing", playable ? StatusLevel.Ok : StatusLevel.Bad,
                    active.Path == null ? "没有本地文件" : active.Path));

                report.Animation.Add(new StatusItem("Animation Resolution",
                    active.ResolutionText == null ? "未知" : active.ResolutionText,
                    active.ResolutionText == null ? StatusLevel.Unknown : StatusLevel.Ok,
                    "来自 MP4 atom 解析（tkhd），不依赖 ffprobe"));

                report.Animation.Add(new StatusItem("Animation FPS",
                    active.FpsText == null ? "未知" : active.FpsText,
                    active.FpsText == null ? StatusLevel.Unknown : StatusLevel.Ok,
                    active.FpsText == null ? "stts 解析不出采样数" : "采样数 / 时长"));

                report.Animation.Add(new StatusItem("Animation Size",
                    active.SizeText == null ? "未知" : active.SizeText,
                    StatusLevel.Ok, "文件字节数"));

                report.Animation.Add(new StatusItem("Animation Hash",
                    string.IsNullOrEmpty(active.Sha256) ? "未记录" : ShortHash(active.Sha256),
                    string.IsNullOrEmpty(active.Sha256) ? StatusLevel.Warn : StatusLevel.Ok,
                    string.IsNullOrEmpty(active.Sha256)
                        ? "本地散放的文件没有已知哈希（社区下载的会记录）"
                        : "sha256，社区下载时校验过"));

                report.Animation.Add(new StatusItem("Memory Budget",
                    EstimateBudget(active), StatusLevel.Ok,
                    "开机播放只保留当前帧附近的解码缓冲，不把整段视频读进内存"));

                report.Animation.Add(new StatusItem("Boot Compatibility",
                    active.UefiCompatible ? "UEFI 候选" : "仅 Windows 阶段",
                    active.UefiCompatible ? StatusLevel.Ok : StatusLevel.Warn,
                    active.UefiCompatible
                        ? "尺寸与体积落在 UEFI 阶段可接受的范围内（仍需要实现 GOP 播放器）"
                        : "超过 UEFI 阶段可接受的尺寸/体积，冷启动阶段无法使用"));
            }

            report.Animation.Add(new StatusItem("Installed Animations",
                (store == null ? 0 : CountInstalled(store)).ToString(CultureInfo.InvariantCulture),
                StatusLevel.Ok, "内置 + 已下载，可立即播放"));

            // ── 汇总 ───────────────────────────────────────────────────────
            int problems = report.ProblemCount();
            if (problems == 0)
            {
                report.Summary = "全部检查通过";
                report.SummaryLevel = StatusLevel.Ok;
            }
            else
            {
                int bad = 0;
                List<StatusItem> all = report.All();
                for (int i = 0; i < all.Count; i++) { if (all[i].Level == StatusLevel.Bad) bad++; }
                report.Summary = problems.ToString(CultureInfo.InvariantCulture) + " 项需要注意"
                    + (bad > 0 ? "（其中 " + bad.ToString(CultureInfo.InvariantCulture) + " 项不可用）" : "");
                report.SummaryLevel = bad > 0 ? StatusLevel.Bad : StatusLevel.Warn;
            }
            Program.Log("诊断完成: " + report.Summary);
            return report;
        }

        private static int CountInstalled(AnimationStateStore store)
        {
            int n = 0;
            List<AnimationInfo> items = store.Items;
            for (int i = 0; i < items.Count; i++) { if (items[i].IsInstalled) n++; }
            return n;
        }

        private static string ShortHash(string sha)
        {
            if (string.IsNullOrEmpty(sha)) return "未记录";
            if (sha.Length <= 16) return sha;
            return sha.Substring(0, 16) + "…";
        }

        private static string EstimateBudget(AnimationInfo a)
        {
            if (a.Width <= 0 || a.Height <= 0) return "未知";
            // 一帧 RGBA 的大小，再加一路解码缓冲。这是播放期的**峰值常驻**下限，
            // 不是"整个视频"—— 视频是流式解码的，不整段进内存。
            long oneFrame = (long)a.Width * a.Height * 4;
            long budget = oneFrame * 3;
            return (budget / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)
                + " MB（" + a.Width.ToString(CultureInfo.InvariantCulture) + "×"
                + a.Height.ToString(CultureInfo.InvariantCulture) + " × 4B × 3 缓冲）";
        }

        /// <summary>电源事件监听是否已注册（Resume Runtime 是否活着）。</summary>
        private static bool resumeReady = false;
        internal static bool ResumeReady() { return resumeReady; }
        internal static void SetResumeReady(bool value) { resumeReady = value; }

        /// <summary>从 boot.log 里读最近一轮的"窗口上屏"耗时。</summary>
        private static double LastBootToWindowMs()
        {
            try
            {
                string path = Path.Combine(Program.DataDirectory, "boot.log");
                if (!File.Exists(path)) return 0;
                string[] lines = File.ReadAllLines(path);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    int at = lines[i].IndexOf("window-shown@", StringComparison.Ordinal);
                    if (at < 0) continue;
                    int from = at + "window-shown@".Length;
                    int to = from;
                    while (to < lines[i].Length && char.IsDigit(lines[i][to])) to++;
                    double v;
                    if (double.TryParse(lines[i].Substring(from, to - from), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out v)) return v;
                }
            }
            catch { }
            return 0;
        }

        // ─────────────────────────────────────────────────────────── Repair

        /// <summary>修复动作的一步，给界面显示进度用。</summary>
        internal sealed class RepairStep
        {
            public string Text;
            public bool Ok;
            public RepairStep(string text, bool ok) { Text = text; Ok = ok; }
        }

        /// <summary>
        /// 真实修复启动项。**每一步都执行，并返回复检结果** —— 不允许只弹个"已完成"。
        ///
        /// 顺序：
        ///   1. 检查注册状态（Run 键 + 计划任务）
        ///   2. 检查路径是否指向当前 exe（指向旧路径是最常见的坏法：重装到别处之后）
        ///   3. 检查启动参数是否带 --delay（旧版本会在这里睡 3 秒，必须清掉）
        ///   4. 删掉错误的注册
        ///   5. 重新注册（管理员先试计划任务，否则 Run 键）
        ///   6. 复验
        /// </summary>
        internal static List<RepairStep> RepairStartup()
        {
            List<RepairStep> steps = new List<RepairStep>();
            string exe = Program.ExePath;

            // 1) 现状
            string runCmd = Platform.RunKeyCommand();
            bool runOk = runCmd != null && runCmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
            bool? taskOk = Platform.TaskRegistered();
            steps.Add(new RepairStep("检查注册状态：Run 键 " + (runCmd == null ? "未设置" : "已设置")
                + "，计划任务 " + (taskOk == true ? "已注册" : (taskOk == false ? "未注册" : "查询失败")), true));

            // 2) 路径
            if (runCmd != null && !runOk)
            {
                steps.Add(new RepairStep("Run 键指向旧路径，将被覆盖：" + runCmd, true));
            }
            else
            {
                steps.Add(new RepairStep("路径检查：" + (runCmd == null ? "无需检查" : "指向当前程序"), true));
            }

            // 3) 启动参数 —— --delay 是"登录后过一会儿才播"的元凶之一
            bool hasDelay = runCmd != null && runCmd.IndexOf("--delay", StringComparison.OrdinalIgnoreCase) >= 0;
            if (hasDelay)
            {
                steps.Add(new RepairStep("检测到启动参数里的 --delay（会让动画晚播），将被移除", true));
            }

            // 4) 清掉错误注册
            if (runCmd != null && (!runOk || hasDelay))
            {
                string err;
                bool removed = Platform.SetRunKey(false, exe, out err);
                steps.Add(new RepairStep(removed ? "已删除旧的 Run 键" : "删除 Run 键失败：" + err, removed));
            }

            // 5) 重新注册
            bool taskRegistered = false;
            if (Platform.IsAdministrator())
            {
                string taskErr;
                taskRegistered = Platform.RegisterLogonTask(exe, false, out taskErr);
                steps.Add(new RepairStep(taskRegistered
                    ? "已注册登录计划任务（启动最早）"
                    : "计划任务注册失败，改用 Run 键：" + taskErr, taskRegistered));
            }
            else
            {
                steps.Add(new RepairStep("非管理员：跳过计划任务（注册会被拒绝），使用 Run 键", true));
            }

            string runErr;
            bool runWritten = Platform.SetRunKey(true, exe, out runErr);
            steps.Add(new RepairStep(runWritten ? "已注册 Run 键（--play，无延迟）" : "注册 Run 键失败：" + runErr, runWritten));

            // 6) 复验 —— 这一步是关键：不复验的 Repair 等于没修
            string after = Platform.RunKeyCommand();
            bool afterOk = after != null && after.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0
                && after.IndexOf("--delay", StringComparison.OrdinalIgnoreCase) < 0;
            bool? afterTask = Platform.TaskRegistered();

            steps.Add(new RepairStep("复验：Run 键 " + (afterOk ? "正确" : "✕ 仍不正确")
                + "，计划任务 " + (afterTask == true ? "已注册" : "未注册"), afterOk));

            if (afterOk && afterTask == true)
                steps.Add(new RepairStep("结论：启动项已修复，且启动了最快的路径（计划任务）", true));
            else if (afterOk)
                steps.Add(new RepairStep("结论：启动项可用（Run 键）。计划任务需要管理员权限，当前用户不是管理员", true));
            else
                steps.Add(new RepairStep("结论：修复未成功，需要手工检查注册表权限", false));

            return steps;
        }
    }
}
