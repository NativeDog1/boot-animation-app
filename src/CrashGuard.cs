// CrashGuard.cs —— 统一异常出口
//
// 为什么必须有这个文件（这是实测踩出来的）：
//
//   客户报"Windows 弹了个应用程序发生异常的框"。而我的每一处错误处理都写在**局部**：
//   `--preview` 有一层 try、`--warm` 有一层 try……但 `Program.Main` 本身、WPF 的
//   消息循环、以及后台线程全都没有兜底。任何在那些地方冒出来的异常都会：
//     1. 直接逃到 Windows，弹出那个只说"应用程序发生异常"、不给原因的框
//     2. 因为 WinExe 的未处理异常**不写 stderr**，日志里也什么都没有
//   → 结果就是"程序没办法运行"，而我和客户都拿不到任何可查的线索。
//
//   这个类把三处出口全部接管：
//     · AppDomain.UnhandledException —— 任意线程（含后台线程）的未捕获异常
//     · DispatcherUnhandledException —— UI 线程的未捕获异常（可标记 Handled，让程序继续活着）
//     · Main 整体包一层 —— 启动期（连 Application 都还没建起来时）的异常
//
//   原则：**宁可留下一个能查的日志和一个能用的窗口，也不要弹一个只有错误码的框。**
//   所以异常被记录之后尽量让进程继续活着（UI 线程的异常标记 Handled），
//   实在活不下去才退出，但退出码与日志都留清楚。
//
// 编译约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员。

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace BootAnimation
{
    internal static class CrashGuard
    {
        private static bool installed;
        private static string crashLogPath;

        /// <summary>崩溃日志。放在数据目录旁边**另起一个文件**：主日志可能因为路径问题写不进去。</summary>
        private static string CrashLog
        {
            get
            {
                if (crashLogPath != null) return crashLogPath;
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BootAnimation");
                    Directory.CreateDirectory(dir);
                    crashLogPath = Path.Combine(dir, "crash.log");
                }
                catch
                {
                    // 连数据目录都建不出来时的最后退路：临时目录
                    crashLogPath = Path.Combine(Path.GetTempPath(), "BootAnimation-crash.log");
                }
                return crashLogPath;
            }
        }

        /// <summary>
        /// 装好全局异常出口。必须在 `Main` 的**最前面**调用 ——
        /// 晚于第一行就可能漏掉启动期的异常，那正是客户遇到的那一种。
        /// </summary>
        internal static void Install()
        {
            if (installed) return;
            installed = true;

            try
            {
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Exception ex = e.ExceptionObject as Exception;
                    Write("AppDomain.UnhandledException（任意线程）",
                        ex == null ? "(非 Exception 对象)" : ex.ToString(),
                        e.IsTerminating ? "进程即将终止" : "进程继续");
                    // 这一处无法阻止终止（.NET 的设计），但至少留下可查的记录
                };
            }
            catch { }

            try
            {
                // 后台线程里抛出的异常默认会直接带走进程。这个订阅把它们记下来。
                TaskScheduler_UnobservedHook();
            }
            catch { }
        }

        /// <summary>
        /// 给 UI 线程装兜底。必须在 Application 建好之后调用（Dispatcher 这时才存在）。
        /// 标记 `Handled = true` 让程序继续活着 —— 一个报错不该让用户丢掉整个程序。
        /// </summary>
        internal static void AttachUi(Application app)
        {
            if (app == null) return;
            try
            {
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    Write("DispatcherUnhandledException（UI 线程）", e.Exception == null ? "(null)" : e.Exception.ToString(),
                        "已标记 Handled，程序继续运行");
                    // 关键：不让它冒到 Windows 去弹框。异常详情已经进日志了。
                    e.Handled = true;
                };
            }
            catch { }
        }

        /// <summary>包一层 Main 般的执行体：抛异常时记日志并返回给定退出码。</summary>
        internal static int Run(string phase, Func<int> body, int failCode)
        {
            try
            {
                return body();
            }
            catch (Exception ex)
            {
                Write(phase, ex.ToString(), "已由 CrashGuard 捕获，返回退出码 " + failCode.ToString(CultureInfo.InvariantCulture));
                return failCode;
            }
        }

        /// <summary>
        /// 把异常写进 crash.log。
        ///
        /// 刻意**同时**尝试主日志：有些情况下（例如数据目录权限异常）crash.log 写不进去，
        /// 而主日志的写入路径已经被验证过是安全的。
        /// </summary>
        internal static void Write(string where, string detail, string note)
        {
            string block = "================================================================" + Environment.NewLine
                + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "  [" + where + "]" + Environment.NewLine
                + "进程: pid=" + SafePid() + "  版本=" + SafeVersion() + Environment.NewLine
                + "说明: " + note + Environment.NewLine
                + detail + Environment.NewLine;

            // 1) 专用崩溃日志
            AppendCrash(block);

            // 2) 主日志（如果它可用）
            try { Program.Log("崩溃: [" + where + "] " + FirstLines(detail, 3)); } catch { }

            // 3) 最后一道：如果连日志都写不出去，尝试桌面上的一个文件
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop))
                {
                    string fallback = Path.Combine(desktop, "BootAnimation-错误报告.txt");
                    if (!File.Exists(fallback)) File.WriteAllText(fallback, block, Encoding.UTF8);
                }
            }
            catch { }
        }

        private static void AppendCrash(string block)
        {
            try
            {
                // 顺手限制大小：崩溃日志不该无限增长
                FileInfo fi = new FileInfo(CrashLog);
                if (fi.Exists && fi.Length > 512 * 1024) File.Delete(CrashLog);
            }
            catch { }
            try { File.AppendAllText(CrashLog, block, Encoding.UTF8); }
            catch { }
        }

        private static string FirstLines(string text, int count)
        {
            if (text == null) return "";
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Length && i < count; i++)
            {
                if (sb.Length > 0) sb.Append(" / ");
                sb.Append(lines[i].Trim());
            }
            return sb.ToString();
        }

        private static string SafePid()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture); }
            catch { return "?"; }
        }

        private static string SafeVersion()
        {
            try { return Program.AppVersion; } catch { return "?"; }
        }

        /// <summary>崩溃日志路径，给诊断页显示用。</summary>
        internal static string CrashLogPath { get { return CrashLog; } }

        /// <summary>
        /// 未观察的 Task 异常钩子。
        ///
        /// `TaskScheduler.UnobservedTaskException` 在 .NET 4.0 里是"让进程崩溃"的路径，
        /// 4.5 之后改成可观察。这个程序全部用 Thread 而不是 Task，所以这里只是
        /// **顺手兜一层**，避免将来有人引入 Task 之后又出现"静默崩溃"。
        /// </summary>
        private static void TaskScheduler_UnobservedHook()
        {
            try
            {
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException +=
                    delegate(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
                    {
                        Write("UnobservedTaskException", e.Exception == null ? "(null)" : e.Exception.ToString(),
                            "已标记观察，不让它带走进程");
                        e.SetObserved();
                    };
            }
            catch { }
        }
    }
}
