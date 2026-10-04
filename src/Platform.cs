// Platform.cs —— Windows 平台适配与真实状态探测
//
// 这个文件只做一件事：**如实回答"现在到底是什么状态"**。
//
// 为什么单独一层：界面上那些 "● Ready" / "✓ Registered" 一旦是硬编码的，整个
// System Status 面板就变成了装饰品 —— 用户看到"Startup ✓ Registered"却开机没播，
// 除了重装没有别的办法。所以每一个状态位都必须有一条真实的检测路径，并且**能说出
// 检测的是什么**（detection detail），失败时能说出失败在哪一步。
//
// 三条自启路径必须严格区分（混在一起会让用户以为"开了"其实没开）：
//   1. Windows 启动时拉起本程序（HKCU\...\Run 或计划任务）
//   2. 睡眠/休眠恢复时播（本进程常驻 + 电源事件监听）
//   3. 冷启动/重启时在 Windows Boot Manager 之前播（UEFI，**当前未实现**）
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace BootAnimation
{
    /// <summary>一个状态位的取值。</summary>
    internal enum StatusLevel
    {
        /// <summary>好。</summary>
        Ok = 0,
        /// <summary>需要注意，但功能还能用。</summary>
        Warn = 1,
        /// <summary>不可用 / 未注册 / 失败。</summary>
        Bad = 2,
        /// <summary>这一版还没有实现，如实说"未实现"而不是假装有。</summary>
        NotImplemented = 3,
        /// <summary>不知道（探测本身失败了）。</summary>
        Unknown = 4,
    }

    /// <summary>一个状态位：给人看的标签 + 真实取值 + 探测细节。</summary>
    internal sealed class StatusItem
    {
        public string Label;
        public string Value;
        public StatusLevel Level;
        /// <summary>这个值是怎么来的。Diagnostics 页会显示它，便于追责。</summary>
        public string Detail;

        public StatusItem(string label, string value, StatusLevel level, string detail)
        {
            Label = label;
            Value = value;
            Level = level;
            Detail = detail;
        }

        /// <summary>状态点该用的颜色。</summary>
        public System.Windows.Media.Brush Tone()
        {
            if (Level == StatusLevel.Ok) return Theme.Ok;
            if (Level == StatusLevel.Warn) return Theme.Warn;
            if (Level == StatusLevel.Bad) return Theme.Danger;
            if (Level == StatusLevel.NotImplemented) return Theme.Fg3;
            return Theme.Info;
        }

        /// <summary>给点的前缀符号。用符号而不是只有颜色，色弱用户也能分辨。</summary>
        public string Glyph()
        {
            if (Level == StatusLevel.Ok) return "●";
            if (Level == StatusLevel.Warn) return "▲";
            if (Level == StatusLevel.Bad) return "✕";
            if (Level == StatusLevel.NotImplemented) return "○";
            return "?";
        }
    }

    internal static class Platform
    {
        internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string RunValueName = "BootAnimation";
        internal const string TaskName = "BootAnimation";

        // ───────────────────────────────────────────────────────── 自启状态

        /// <summary>Run 键里现在写的是什么命令行；没有则 null。</summary>
        internal static string RunKeyCommand()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return null;
                    object v = key.GetValue(RunValueName);
                    return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) { Program.Log("读 Run 键失败: " + ex.Message); return null; }
        }

        /// <summary>
        /// 计划任务是否已注册。
        ///
        /// 用 schtasks.exe 查询而不是 TaskScheduler COM：这个程序刻意保持零依赖，
        /// 而 schtasks 是 Windows 自带的。查询失败（比如被策略禁用）返回 Unknown，
        /// 不能当成"没注册"—— 那会让 Repair 去重复注册。
        /// </summary>
        internal static bool? TaskRegistered()
        {
            string stdout;
            int code = RunTool("schtasks.exe", "/Query /TN \"" + TaskName + "\"", 15000, out stdout);
            if (code == 0) return true;
            // 找不到任务时 schtasks 返回 1 且输出含 "cannot find" / "找不到"
            if (stdout != null && (stdout.IndexOf("cannot find", StringComparison.OrdinalIgnoreCase) >= 0
                || stdout.IndexOf("找不到", StringComparison.Ordinal) >= 0
                || stdout.IndexOf("ERROR: The system cannot find", StringComparison.OrdinalIgnoreCase) >= 0))
                return false;
            return null;
        }

        /// <summary>当前进程是不是管理员。计划任务注册能力取决于它。</summary>
        internal static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// 注册登录计划任务（启动最早的那条路）。
        ///
        /// 参数刻意与 install.ps1 / Setup.cs 保持一致：`--play`，不带 --delay。
        /// `/RL LIMITED` + `/IT` 表示"当前用户、受限权限、交互式会话"，不需要提权。
        /// </summary>
        internal static bool RegisterLogonTask(string exePath, bool mute, out string error)
        {
            error = null;
            string args = "--play" + (mute ? " --mute" : "");
            string line = "/Create /TN \"" + TaskName + "\" /TR \"" + exePath + " " + args + "\""
                + " /SC ONLOGON /RL LIMITED /F /IT";
            string stdout;
            int code = RunTool("schtasks.exe", line, 30000, out stdout);
            if (code == 0) return true;
            error = SummarizeTool(stdout, code);
            Program.Log("注册计划任务失败: " + error);
            return false;
        }

        internal static bool DeleteLogonTask(out string error)
        {
            error = null;
            string stdout;
            int code = RunTool("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", 20000, out stdout);
            if (code == 0) return true;
            // 本来就没有 → 视为成功（幂等），否则 Repair 会在"没任务"的机器上报错
            if (stdout != null && (stdout.IndexOf("cannot find", StringComparison.OrdinalIgnoreCase) >= 0
                || stdout.IndexOf("找不到", StringComparison.Ordinal) >= 0)) return true;
            error = SummarizeTool(stdout, code);
            return false;
        }

        /// <summary>写/删 Run 键。返回 false 时 error 给出原因。</summary>
        internal static bool SetRunKey(bool enable, string exePath, out string error)
        {
            error = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) { error = "打不开 Run 键"; return false; }
                    if (enable)
                    {
                        key.SetValue(RunValueName, "\"" + exePath + "\" --play");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                    return true;
                }
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        internal static int RunTool(string file, string arguments, int timeoutMs, out string stdout)
        {
            stdout = null;
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo(file, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                string err = p.StandardError.ReadToEnd();
                string outText = p.StandardOutput.ReadToEnd();
                p.WaitForExit(timeoutMs);
                stdout = outText + (err.Length > 0 ? Environment.NewLine + err : "");
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                stdout = ex.Message;
                return -1;
            }
        }

        private static string SummarizeTool(string text, int code)
        {
            if (string.IsNullOrEmpty(text)) return "退出码 " + code.ToString(CultureInfo.InvariantCulture);
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) continue;
                // schtasks 的 "ERROR: ..." / 中文 "错误: ..." 行才是原因
                if (t.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || t.StartsWith("错误"))
                    return t;
            }
            return lines[lines.Length - 1].Trim();
        }

        // ───────────────────────────────────────────────────────── 系统信息

        /// <summary>主屏物理像素尺寸（不是 DIP）。模拟开机屏幕时要按它给比例。</summary>
        internal static void PrimaryScreenPixels(out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                DEVMODE mode = new DEVMODE();
                mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(null, -1, ref mode))
                {
                    width = mode.dmPelsWidth;
                    height = mode.dmPelsHeight;
                }
            }
            catch { }
            if (width <= 0 || height <= 0)
            {
                // 退路：WPF 的 DIP 尺寸 × 系统 DPI。不如 API 准，但比没有好。
                try
                {
                    width = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                    height = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
                }
                catch { }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        /// <summary>
        /// Secure Boot 是否开启。
        ///
        /// 读注册表 `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State`，值 1 = 开启。
        /// 键在"不支持 Secure Boot 的机器"和"没有权限读"两种情况下都可能不存在，
        /// 所以三态：true / false / null（不知道）。
        /// </summary>
        internal static bool? SecureBootEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", false))
                {
                    if (key == null) return null;
                    object v = key.GetValue("UEFISecureBootEnabled");
                    if (v == null) return null;
                    return Convert.ToInt32(v, CultureInfo.InvariantCulture) == 1;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 系统是不是 UEFI 启动的（而不是 Legacy BIOS）。
        ///
        /// 判据：存在 `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State`，
        /// 或者存在 `\\.\MountPointManager` 之外更稳的一条 —— PEFirmwareType。
        /// 这里用最朴素的一条：`GetFirmwareEnvironmentVariable` 能否读到东西。
        /// 读不到就是 BIOS 或者没有权限，返回 null（不知道）。
        /// </summary>
        internal static bool? IsUefiBoot()
        {
            try
            {
                // PEFirmwareType: 1 = BIOS, 2 = UEFI。NtQuerySystemInformation 不方便，
                // 改用一个更直观的判据：固件环境变量能不能读。
                System.Text.StringBuilder buffer = new System.Text.StringBuilder(8);
                uint got = GetFirmwareEnvironmentVariable("BootOrder", "{8be4df61-93ca-11d2-aa0d-00e098032b8c}",
                    buffer, (uint)buffer.Capacity);
                if (got > 0) return true;
                int err = Marshal.GetLastWin32Error();
                // 1314 = 权限不足（说明是 UEFI，只是没权限读）；其余按"不是 UEFI"处理
                if (err == 1314) return true;
                return false;
            }
            catch { return null; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFirmwareEnvironmentVariable(string name, string guid,
            System.Text.StringBuilder buffer, uint size);

        /// <summary>系统盘盘符（通常是 C:），用于报告安装位置。</summary>
        internal static string SystemDrive()
        {
            try
            {
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string root = Path.GetPathRoot(windir);
                return root == null ? "C:\\" : root;
            }
            catch { return "C:\\"; }
        }

        /// <summary>系统启动时刻。</summary>
        internal static DateTime BootTime()
        {
            try { return DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// 当前用户会话是不是"刚登录"（用来判断这轮启动是不是开机）。
        /// 判据：系统已运行时间小于 10 分钟。
        /// </summary>
        internal static bool LooksLikeFreshBoot()
        {
            try
            {
                return (DateTime.Now - BootTime()).TotalMinutes < 10.0;
            }
            catch { return false; }
        }
    }
}
