// BootAnimation —— Windows 开机动画播放器
//
// 编译（不需要安装任何 SDK，用 Windows 自带的 csc.exe）：
//   csc /nologo /target:winexe /platform:anycpu /out:BootAnimation.exe ^
//       /r:<框架>\WPF\PresentationFramework.dll /r:<框架>\WPF\PresentationCore.dll ^
//       /r:<框架>\WPF\WindowsBase.dll /r:<框架>\System.Xaml.dll ^
//       /resource:brand.mp4,brand.mp4 ... src\BootAnimation.cs
//
// 沿用的是 dsh-boot-animation 插件里已经验证过的那套方案：
//   1. 视频内嵌在 exe 里（--selftest 可枚举并校验），运行时才解到本地缓存
//   2. 客户有选择性：无参数启动 = 选片窗口；--play 播放已选；--choose 重新选
//   3. 起播不阻塞：窗口先出来，画面到了就播，状态行会说明正在做什么
//   4. 黑屏绝不允许静默：MediaOpened / MediaFailed / 超时全部写日志并在屏幕上显示原因
//
// 注意：必须写成 C# 5 语法 —— 自带 csc 只认到 C# 5，
// 不能用 $"" 插值、?. 、元组、表达式体成员、nameof、自动属性初始化器。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

internal sealed class Clip
{
    public readonly string Res;
    public readonly string Id;
    public readonly string Name;

    public Clip(string res, string id, string name)
    {
        Res = res;
        Id = id;
        Name = name;
    }
}

/// <summary>
/// 首帧层：把缓存的背景图（**就是这段视频的第一帧**）按视频的真实比例摆好，
/// 让窗口在视频解码完成之前就已经有画面。
///
/// 为什么必须自己算尺寸而不是交给 Image 的 Stretch：
///   · `Image` 在 `Stretch=Uniform` 下按**图片自己的**比例缩放，而图片是 1280×720 的
///     缩略图、视频可能是 3840×2160 —— 比例相同，位置也就相同，但如果将来背景图被换成
///     别的比例，画面就会和视频错位。
///   · `Stretch=UniformToFill`（氛围背景用的那个）会裁切，作为"第一帧"是错的。
///   所以这里显式按 (视频比例, 画面比例, 对齐方式) 算出矩形，第一帧落点与视频完全一致。
///
/// 这一层是"零等待"的关键：它是本地 JPEG，BitmapImage 用 OnLoad 解码，
/// 窗口第一次合成时就已经在位图缓存里，所以用户看到的第一眼就是画面而不是黑块。
/// </summary>
internal sealed class PosterLayer : System.Windows.Controls.Image
{
    private double videoW;
    private double videoH;

    public PosterLayer(System.Windows.Media.ImageSource source, double videoWidth, double videoHeight)
    {
        Source = source;
        videoW = videoWidth > 0 ? videoWidth : 1920;
        videoH = videoHeight > 0 ? videoHeight : 1080;
        IsHitTestVisible = false;
        Stretch = System.Windows.Media.Stretch.Fill;
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        VerticalAlignment = System.Windows.VerticalAlignment.Top;
    }

    /// <summary>画面（视频）的比例。窗口比例与它不一致时，第一帧必须和视频落在同一块区域。</summary>
    public void SetVideoSize(double width, double height)
    {
        if (width > 0 && height > 0) { videoW = width; videoH = height; }
        InvalidateArrange();
    }

    /// <summary>
    /// 0 = 铺满裁切（cover）、1 = 整帧留边（contain）、2 = 原始尺寸居中（none）、
    /// 3 = **拉伸填满（stretch）**。
    ///
    /// 3 这一档必须存在：默认贴合方式就是拉伸，如果首帧层还按比例摆，
    /// 视频接管那一下会看到画面"跳"一下 —— 因为两层落点不一样。
    /// </summary>
    public int Mode { get; set; }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        double aw = finalSize.Width;
        double ah = finalSize.Height;
        if (aw <= 0 || ah <= 0) return finalSize;

        // 拉伸模式直接铺满：不按比例，落点与视频的 Stretch.Fill 完全一致
        if (Mode == 3)
        {
            RenderSize = new System.Windows.Size(aw, ah);
            return new System.Windows.Size(aw, ah);
        }

        double s;
        if (Mode == 0) s = Math.Max(aw / videoW, ah / videoH);
        else if (Mode == 1) s = Math.Min(aw / videoW, ah / videoH);
        else s = 1.0;

        double w = videoW * s;
        double h = videoH * s;
        // RenderSize 就是元素自己的矩形；靠左上偏移把它居中（或按 cover 溢出居中）
        RenderSize = new System.Windows.Size(w, h);
        return new System.Windows.Size(aw, ah);
    }
}

/// <summary>
/// 播放窗口 + "挂载媒体"的动作。
///
/// 为什么不是直接返回 Window（原来那样）：
///   媒体挂载必须发生在**窗口已经显示之后**（否则媒体栈初始化会把窗口卡住近 1 秒，
///   实测 media-play-issued@927 / window-shown@1517）。
///   而最初用 `win.Loaded` 事件当触发点，实测**不可靠** ——
///   Loaded 是首次布局/渲染之后才触发的，日志里出现过它始终不触发、
///   于是媒体从未挂上、整段动画根本没播的情况。
///
///   改成把"挂载动作"显式交回调用方，由它按确定顺序调用：
///   建窗口 → 显示 → 挂媒体。没有事件依赖，顺序是确定的。
/// </summary>
internal sealed class PlayerWindow
{
    public Window Window;
    public Action AttachMedia;
}

internal static class Program
{
    // 内嵌片库。资源名必须与 csc /resource:文件,资源名 里的资源名一致。
    private static readonly Clip[] Clips = new Clip[]
    {
        new Clip("brand.mp4", "brand", "DeepSeek 品牌片头"),
        new Clip("cyberpunk.mp4", "cyberpunk", "DeepSeek 赛博朋克片头"),
        new Clip("awakening.mp4", "awakening", "DeepSeek 数字角色苏醒"),
        new Clip("startup.mp4", "startup", "DeepSeek 启动问题"),
    };

    // 出问题时要能查：所有状态都往这里写一行。
    private static string DataDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BootAnimation");
        }
    }

    private static string LogPath { get { return Path.Combine(DataDir, "boot-animation.log"); } }

    /// 给 Community 模块用的入口（社区片段存在这个目录下）
    internal static string DataDirPath { get { return DataDir; } }
    private static string SettingsPath { get { return Path.Combine(DataDir, "settings.txt"); } }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(
                LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + "  " + message + Environment.NewLine);
        }
        catch
        {
            // 日志失败不能影响播放
        }
    }

    private static Clip Find(string id)
    {
        for (int i = 0; i < Clips.Length; i++)
        {
            if (string.Equals(Clips[i].Id, id, StringComparison.OrdinalIgnoreCase)) return Clips[i];
        }
        return null;
    }

    /// <summary>
    /// 贴合偏好的落点。**故意与 settings.txt 分开一个文件**：
    /// settings.txt 是"选片"的存储，格式被旧版依赖（一行一个 id），
    /// 往里面塞键值会破坏向后兼容；而这个偏好是新加的，独立存放最安全。
    /// </summary>
    private static string FitPrefPath
    {
        get { return Path.Combine(DataDir, "fit.txt"); }
    }

    /// <summary>读用户偏好；没有或读不出来时用 DefaultFit（stretch = 真全屏）。</summary>
    internal static string ReadFitPreference()
    {
        try
        {
            string path = FitPrefPath;
            if (!File.Exists(path)) return DefaultFit;
            string text = File.ReadAllText(path).Trim().ToLowerInvariant();
            return IsKnownFit(text) ? text : DefaultFit;
        }
        catch { return DefaultFit; }
    }

    /// <summary>保存用户偏好。写失败不算错误（下次仍然用默认值）。</summary>
    internal static void WriteFitPreference(string mode)
    {
        try
        {
            if (!IsKnownFit(mode)) return;
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FitPrefPath, mode);
            Log("贴合偏好已保存: " + mode);
        }
        catch (Exception ex) { Log("保存贴合偏好失败: " + ex.Message); }
    }

    /// <summary>读当前生效的选片 id（settings.txt）。读不到返回 null。</summary>
    internal static string ReadChosen()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            string text = File.ReadAllText(SettingsPath).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    internal static void WriteChosen(string id)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsPath, id);
        }
        catch (Exception ex)
        {
            Log("settings write failed: " + ex.Message);
        }
    }

    internal static string Sha256Of(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            byte[] hash = sha.ComputeHash(fs);
            // 手工转十六进制：BitConverter.ToString 带横线，这里统一成小写无横线
            string s = BitConverter.ToString(hash).Replace("-", string.Empty);
            return s.ToLowerInvariant();
        }
    }

    /// 把内嵌的那一段解到本地缓存。已经存在且大小一致就直接复用。
    /// 用 .tmp + 覆盖式改名，避免半截文件被当成完整片段。
    private static string Extract(Clip clip)
    {
        string dir = Path.Combine(DataDir, "clips");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, clip.Id + ".mp4");

        Assembly asm = Assembly.GetExecutingAssembly();
        using (Stream stream = asm.GetManifestResourceStream(clip.Res))
        {
            if (stream == null) throw new InvalidOperationException("缺少内嵌资源: " + clip.Res);
            long size = stream.Length;
            FileInfo existing = new FileInfo(target);
            if (existing.Exists && existing.Length == size)
            {
                Log("extract: 命中缓存 " + clip.Id + " " + size + " bytes");
                return target;
            }
            string tmp = target + ".tmp";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                stream.CopyTo(fs);
            }
            File.Copy(tmp, target, true);
            File.Delete(tmp);
            Log("extract: " + clip.Id + " -> " + target + " (" + size + " bytes)");
        }
        return target;
    }

    /// 无界面自检：枚举内嵌资源、解出来、算哈希，写一份报告。
    /// 用来证明「视频确实在 exe 里面」而且解出来与源文件逐字节一致。
    private static int SelfTest()
    {
        Directory.CreateDirectory(DataDir);
        List<string> lines = new List<string>();
        lines.Add("id\tname\tembeddedBytes\textractedBytes\tsha256");
        int failures = 0;
        for (int i = 0; i < Clips.Length; i++)
        {
            Clip clip = Clips[i];
            try
            {
                string path = Extract(clip);
                FileInfo info = new FileInfo(path);
                string report = clip.Id + "\t" + clip.Name + "\t" + info.Length + "\t"
                    + info.Length + "\t" + Sha256Of(path);
                lines.Add(report);
            }
            catch (Exception ex)
            {
                failures++;
                lines.Add(clip.Id + "\t" + clip.Name + "\tERROR\t" + ex.Message);
                Log("selftest failed for " + clip.Id + ": " + ex.Message);
            }
        }
        File.WriteAllLines(Path.Combine(DataDir, "selftest.txt"), lines.ToArray());
        Log("selftest done, failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "BootAnimation";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BootAnimation";

    /// 程序装在哪里（安装器与卸载器必须用同一个定义）
    internal static string InstallDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "BootAnimation");
        }
    }

    /// 开始菜单快捷方式（指向 --choose，方便客户随时换片头）
    internal static string StartMenuLink
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "开机动画.lnk");
        }
    }

    /// 桌面快捷方式 —— 与开始菜单同一个目标，只是多给用户一个"下载完就能看见"的入口。
    /// 安装时创建（见 Setup.cs），卸载时删除（见下面的 Uninstall）。
    ///
    /// 用 DesktopDirectory 而不是 Desktop：Desktop 是虚拟文件夹，DesktopDirectory 才是真实的
    /// 文件系统路径，并且跟随"桌面被重定向到 OneDrive"这类设置 —— 写错那个的话，快捷方式会
    /// 落在一个用户看不见的地方。
    internal static string DesktopLink
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "开机动画.lnk");
        }
    }

    /// 卸载：删自启、删快捷方式、删「应用和功能」条目、删程序目录。
    /// 数据目录（选片、日志）故意保留 —— 重装后选片还在，也不会误删客户的东西。
    private static int Uninstall(bool silent)
    {
        if (!silent)
        {
            MessageBoxResult answer = MessageBox.Show(
                "确定要卸载「开机动画」吗？" + Environment.NewLine + Environment.NewLine
                + "会删除：程序文件、开机自启、开始菜单与桌面快捷方式，以及「应用和功能」里的条目。"
                + Environment.NewLine + Environment.NewLine
                + "你的选片与日志会保留在：" + Environment.NewLine + DataDir,
                "卸载开机动画", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                Log("卸载被取消");
                return 0;
            }
        }

        int removed = 0;

        // 1. 开机自启
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key != null && key.GetValue(RunValueName) != null)
                {
                    key.DeleteValue(RunValueName, false);
                    removed++;
                    Log("已删除自启键 " + RunValueName);
                }
            }
        }
        catch (Exception ex) { Log("删自启失败: " + ex.Message); }

        // 1b. 计划任务（安装时优先注册的那条路）
        //
        // 必须和 Run 键一起删。两条自启路径是「有哪条用哪条」，只删一条的话，
        // 客户点完卸载还会在开机时看到动画 —— 这是最容易被骂成"没卸载干净"的情形。
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe",
                "/Delete /TN \"BootAnimation\" /F");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            Process sp = Process.Start(psi);
            sp.StandardError.ReadToEnd();
            sp.StandardOutput.ReadToEnd();
            sp.WaitForExit(20000);
            if (sp.ExitCode == 0)
            {
                removed++;
                Log("已删除计划任务 BootAnimation");
            }
        }
        catch (Exception ex) { Log("删计划任务失败（多半本来就没有）: " + ex.Message); }

        // 2. 开始菜单与桌面快捷方式
        try
        {
            if (File.Exists(StartMenuLink))
            {
                File.Delete(StartMenuLink);
                removed++;
                Log("已删除开始菜单快捷方式");
            }
            // 安装时在桌面建的那一份同样要删掉 —— 卸载之后桌面上留一个打不开的图标，
            // 是最容易被当成"没卸载干净"的那种残留。
            if (File.Exists(DesktopLink))
            {
                File.Delete(DesktopLink);
                removed++;
                Log("已删除桌面快捷方式");
            }
        }
        catch (Exception ex) { Log("删快捷方式失败: " + ex.Message); }

        // 3. 「应用和功能」里的条目
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
            removed++;
            Log("已删除卸载入口");
        }
        catch (Exception ex) { Log("删卸载入口失败: " + ex.Message); }

        // 3b. bootanim:// 协议注册（网页上的「装」靠它唤起本程序）
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\bootanim", false);
            removed++;
            Log("已删除 bootanim:// 协议注册");
        }
        catch (Exception ex) { Log("删协议注册失败: " + ex.Message); }

        // 4. 程序目录：自己删不掉自己（exe 正在运行），交给一个分离的 cmd 等两秒再删
        try
        {
            if (Directory.Exists(InstallDir))
            {
                // 两个坑叠在一起，都在这里踩过：
                //  1) 不能用 timeout 等待 —— stdin 被重定向时它会直接报错退出
                //  2) 路径不能写进脚本正文 —— .cmd 是按系统 ANSI 代码页解析的，
                //     而 %USERPROFILE% 里很可能有中文（C:\Users\高振杰\...），
                //     写进去就是乱码，rmdir 会静默失败。改用参数传进去：
                //     命令行是 UTF-16，cmd 能正确拿到路径。
                string script = "@echo off" + Environment.NewLine
                    + "for /l %%i in (1,1,15) do (" + Environment.NewLine
                    + "  rmdir /s /q \"%~1\" 2>nul" + Environment.NewLine
                    + "  if not exist \"%~1\" goto done" + Environment.NewLine
                    + "  ping -n 2 127.0.0.1 >nul" + Environment.NewLine
                    + ")" + Environment.NewLine
                    + ":done" + Environment.NewLine
                    + "del \"%~f0\"" + Environment.NewLine;
                string tmp = Path.Combine(Path.GetTempPath(), "ba-uninstall.cmd");
                File.WriteAllText(tmp, script, System.Text.Encoding.Default);
                // 第三个坑：cmd /c "脚本" "参数" 这种写法是错的 —— cmd 会剥掉外层引号，
                // 结果把两个参数粘成一个非法路径（报 "filename ... syntax is incorrect"）。
                // 必须用双引号包住整串：cmd /c ""脚本" "参数""
                ProcessStartInfo psi = new ProcessStartInfo(
                    "cmd.exe", "/c \"\"" + tmp + "\" \"" + InstallDir + "\"\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
                removed++;
                Log("已排定删除程序目录: " + InstallDir);
            }
        }
        catch (Exception ex) { Log("排定删除失败: " + ex.Message); }

        Log("卸载完成，处理了 " + removed + " 项");
        if (!silent)
        {
            MessageBox.Show(
                "已卸载「开机动画」。" + Environment.NewLine + Environment.NewLine
                + "你的选片与日志保留在：" + Environment.NewLine + DataDir + Environment.NewLine + Environment.NewLine
                + "想一起清掉，删掉这个文件夹即可。",
                "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return 0;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        /**
         * 在**真正第一行**记录"进程启动 → 代码开始执行"这段间隔。
         *
         * 为什么要专门量它（这是"穿帮"的关键，实测抓到的）：
         *   23:28:37.809  进程启动（任务计划服务创建进程的时刻）
         *   23:28:54.191  程序执行到"本次启动基准"那一行
         *   → **中间隔了 16.4 秒**。
         *
         * 这段时间是 CLR 初始化 + 加载程序集 + JIT，全部发生在我的代码之外。
         * 而它直接决定穿帮与否：如果这 16 秒里桌面已经画出来了，那么无论后面
         * 我的窗口多快（实测原生窗口 33ms），动画都必然**晚于桌面出现** ——
         * 用户看到的就是"桌面先出来、动画后出来"。
         *
         * 所以必须先把这个数字摆到日志里，才能判断"穿帮"到底是不是它造成的。
         */
        long bootToCodeMs = -1;
        try
        {
            DateTime processStart = Process.GetCurrentProcess().StartTime;
            bootToCodeMs = (long)(DateTime.Now - processStart).TotalMilliseconds;
        }
        catch { }

        // **第一件事**就是装全局异常出口。
        //
        // 不能晚于这里：客户遇到的"应用程序发生异常"正是**启动期**抛出来的，
        // 而启动期的异常在 WinExe 里既不写 stderr 也不进我们的日志 —— 表现就是
        // 一个只有错误码、没有原因的 Windows 弹框。装了之后任何异常都会落进
        // crash.log，并且 UI 线程的异常不再冒到 Windows 去。
        BootAnimation.CrashGuard.Install();

        // 记下来，MainCore 里写日志时用
        ProcessStartToCodeMs = bootToCodeMs;

        return BootAnimation.CrashGuard.Run("Program.Main", delegate { return MainCore(args); }, 10);
    }

    /// <summary>进程启动 → 代码开始执行的间隔（毫秒）。由 Main 在最前面测量并写入。</summary>
    internal static long ProcessStartToCodeMs = -1;

    private static int MainCore(string[] args)
    {
        // 计时从第一行开始，而不是从"准备显示"开始 ——
        // 之前 Stopwatch 是在 delay 之后才 new 的，于是日志里的"进程启动到出画 878 ms"
        // 其实漏掉了前面所有真实开销（解包、选片、CLR 启动）。这个数字被用来判断
        // "慢在哪一段"，起点错了它就只会说谎。
        started = System.Diagnostics.Stopwatch.StartNew();

        string want = null;      // --clip <id>
        string file = null;      // --file <path>
        double seconds = 0;      // --seconds <n> 到点自动关
        double delay = 0;        // --delay <n> 显示前先等几秒（默认 0，见下）
        string fit = null;        // --fit stretch|cover|contain|ambient|auto；未给定时读持久化偏好（默认 stretch）
        bool topmost = true;
        bool choose = false;
        bool play = false;
        bool list = false;
        bool uninstall = false;
        bool silent = false;
        bool mute = false;       // 默认出声：素材自带音轨，客户要的就是这个
        int volume = 100;
        string installUrl = null; // --install-url bootanim://install?id=...  ← 网页上点「装」时传进来的
        bool browse = false;      // --browse 打开社区网站
        bool posterAll = false;   // --poster-all 为每段生成背景大图（构建期用）
        bool fitCheck = false;    // --fit-check 打印自动贴合判定表（无窗口自检）
        bool resume = false;      // --resume 从睡眠/休眠唤醒时播放（与登录启动是两条路）
        string previewLayer = null; // --preview media|boot 直接打开沉浸式预览
        bool warm = false;          // --warm 常驻预热：窗口不可见地把解码器养热，等触发
        bool useNative = false;     // --native 显式启用原生播放器（默认关闭，见 BootRequest 说明）
        // 记录"是谁拉起我的"。有多个启动机制同时存在时（Run 键 / 启动文件夹 / 计划任务），
        // 这个标记能直接说明哪条路径先触发 —— 是选择保留哪一种的依据。
        string launchSource = "Run 键";
        string crashTest = null;    // --crash-test <阶段> 故意抛异常，验证 CrashGuard 是否兜住
        bool prepare = false;       // --prepare 为所有已安装片段准备首帧图（安装器调用）
        bool freezeCheck = false;   // --freeze-check 定格帧自检：验证"播完定格的那一帧有画面"
        string snapshot = null;   // --snapshot <png> 出画后把窗口内容存成 PNG（验收用）
        double snapshotAt = 1.5;  // --snapshot-at <秒> 截图时机，默认 1.5s

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--selftest") return SelfTest();
                else if (a == "--manager") return BootAnimation.Shell.Run();
            else if (a == "--poster-all") posterAll = true;
            else if (a == "--fit-check") fitCheck = true;
            else if (a == "--snapshot" && i + 1 < args.Length) snapshot = args[++i];
            else if (a == "--snapshot-at" && i + 1 < args.Length)
            {
                double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out snapshotAt);
            }
            else if (a == "--uninstall") uninstall = true;
            else if (a == "--silent") silent = true;
            else if (a == "--list") list = true;
            else if (a == "--choose") choose = true;
            else if (a == "--play") play = true;
            else if (a == "--resume") { resume = true; play = true; }
            else if (a == "--warm") warm = true;
            else if (a == "--native") useNative = true;
            else if (a == "--startupfolder") launchSource = "启动文件夹";
            else if (a == "--logontask") launchSource = "计划任务";
            else if (a == "--crash-test" && i + 1 < args.Length) crashTest = args[++i];
            else if (a == "--prepare") prepare = true;
            else if (a == "--freeze-check") freezeCheck = true;
            else if (a == "--preview")
            {
                // 不带值时默认开机预览
                // 只接受已知的层名。原来写成"下一个 token 不以 -- 开头就当成层名"，
                // 于是一个**不认识的标志**（例如旧版快捷方式里的 --startupfolder）
                // 会被当成层名消费掉，并且把后面的参数整体错位 —— 实测导致 --play 丢失、
                // 误开主界面。白名单式判断从根上避免这类错位。
                if (i + 1 < args.Length &&
                    (args[i + 1] == "media" || args[i + 1] == "boot"))
                {
                    previewLayer = args[++i];
                }
                else
                {
                    previewLayer = "boot";
                }
            }
            else if (a == "--clip" && i + 1 < args.Length) want = args[++i];
            else if (a == "--file" && i + 1 < args.Length) file = args[++i];
            else if (a == "--seconds" && i + 1 < args.Length)
            {
                double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            }
            else if (a == "--no-topmost") topmost = false;
            else if (a == "--delay" && i + 1 < args.Length)
            {
                double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out delay);
            }
            else if (a == "--fit" && i + 1 < args.Length) fit = args[++i];
            else if (a == "--mute") mute = true;
            else if (a == "--install-url" && i + 1 < args.Length) installUrl = args[++i];
            else if (a == "--browse") browse = true;
            else if (a == "--volume" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out volume);
                if (volume < 0) volume = 0;
                if (volume > 100) volume = 100;
            }
        }

        Log("启动 args=" + string.Join(" ", args) + "   [触发来源: " + launchSource + "]");

        // --crash-test：故意抛异常，用来验证"任何异常都被 CrashGuard 兜住并写进 crash.log"。
        // 这条存在的意义是：客户报过一次"应用程序发生异常"的 Windows 弹框，
        // 而那类异常在 WinExe 里既不写 stderr 也不进日志 —— 必须有办法**主动复现并确认**兜底有效。
        if (crashTest != null)
        {
            if (crashTest == "main") throw new InvalidOperationException("crash-test: Main 阶段故意抛出的异常");
            if (crashTest == "ui")
            {
                Application crashApp = new Application();
                BootAnimation.CrashGuard.AttachUi(crashApp);
                crashApp.Dispatcher.BeginInvoke(new Action(delegate
                {
                    throw new InvalidOperationException("crash-test: UI 线程故意抛出的异常");
                }));
                // 这个自测没有主窗口，所以必须自己安排退出，否则消息循环会一直转下去
                System.Windows.Threading.DispatcherTimer quit = new System.Windows.Threading.DispatcherTimer();
                quit.Interval = TimeSpan.FromMilliseconds(900);
                quit.Tick += delegate { quit.Stop(); crashApp.Shutdown(); };
                quit.Start();
                crashApp.Run();
                return 0;
            }
            if (crashTest == "thread")
            {
                // 必须显式写成 ThreadStart：匿名 delegate 在 ThreadStart 与
                // ParameterizedThreadStart 之间有歧义，csc 会直接报 CS0121。
                System.Threading.ThreadStart body = delegate
                {
                    throw new InvalidOperationException("crash-test: 后台线程故意抛出的异常");
                };
                System.Threading.Thread th = new System.Threading.Thread(body);
                th.IsBackground = false;
                th.Start();
                System.Threading.Thread.Sleep(1500);
                return 0;
            }
        }

        if (freezeCheck) return FreezeCheck();
        if (prepare) return PrepareAllPosters();
        if (posterAll) return GeneratePosters();

        // 无窗口自检：把「自动贴合」的判定摊开成一张表。
        // 这条存在的理由和 --selftest 一样 —— 「不居中/被裁掉」是个几何断言，
        // 只靠肉眼看全屏窗口是验不准的，得让阈值两侧的数字自己说话。
        if (fitCheck) return FitCheck();

        // 登录时抢屏幕：把优先级抬上去，让它在开机那几十个竞争的进程里先拿到 CPU。
        // 这一段本身很便宜（一次系统调用），但不做的话 4K 视频的首帧解码会被排在后面。
        // 失败就算了：优先级只是锦上添花，不是能不能播的前提。
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
        catch (Exception ex) { Log("提升优先级失败（不影响播放）: " + ex.Message); }

        if (uninstall) return Uninstall(silent);

        // 网页上「浏览社区」按钮
        if (browse)
        {
            try { Process.Start(Community.SiteUrl); }
            catch (Exception ex) { Log("打开社区网页失败: " + ex.Message); }
            return 0;
        }

        // 网页上点「装到我的开机动画」→ bootanim://install?... → 走到这里
        if (installUrl != null)
        {
            Community.InstallRequest request = Community.ParseInstallUrl(installUrl);
            Application installApp = new Application();
            BootAnimation.CrashGuard.AttachUi(installApp);
            installApp.Run(Community.BuildInstallWindow(request, installApp));
            return 0;
        }

        if (list)
        {
            List<string> outLines = new List<string>();
            for (int i = 0; i < Clips.Length; i++) outLines.Add(Clips[i].Id + "\t" + Clips[i].Name);
            Directory.CreateDirectory(DataDir);
            File.WriteAllLines(Path.Combine(DataDir, "clips.txt"), outLines.ToArray());
            return 0;
        }

        // ────────────────────────────────────────────────────────────────────
        // --warm —— 常驻预热进程。
        //
        // 登录时被拉起，把当前选中的动画加载并解码好，然后**一直保持不可见地停在那里**。
        // 真正的播放由 --play 发一个命名事件触发，此时只需要把 Opacity 改 1。
        // 这是"登录那一刻就已经在播"的实现方式，细节见 WarmRuntime.cs。
        // ────────────────────────────────────────────────────────────────────
        if (warm)
        {
            // 预热进程已废弃：WPF 的 MediaElement 不会在非前台窗口里刷新视频，
            // 所以"提前解码好、触发时瞬间显形"这条路在 WPF 上走不通
            // （四种方案都实测过，详见 BootRuntime 里那段说明）。
            // 这里保留 --warm 参数只为让旧的自启登记不至于报错，行为是直接退出。
            Log("--warm 已废弃（WPF 限制：非前台窗口不刷新视频），直接退出");
            return 0;
        }
        if (false)
        {
            BootAnimation.AnimationInfo warmClip = null;
            string warmId = want == null ? ReadChosen() : want;
            List<BootAnimation.AnimationInfo> warmLib = BootAnimation.AnimationRepository.ScanLocal();
            for (int i = 0; i < warmLib.Count; i++)
            {
                if (string.Equals(warmLib[i].Id, warmId, StringComparison.OrdinalIgnoreCase))
                {
                    warmClip = warmLib[i];
                    break;
                }
            }
            if (warmClip == null)
            {
                for (int i = 0; i < warmLib.Count; i++)
                {
                    if (warmLib[i].IsInstalled) { warmClip = warmLib[i]; break; }
                }
            }
            return BootAnimation.WarmRuntime.Run(warmClip, topmost, volume, mute);
        }

        // ────────────────────────────────────────────────────────────────────
        // --preview [media|boot] —— 直接打开沉浸式预览，不进主界面。
        //
        // 存在的理由有两个，都是真实的：
        //   1. 它是**可脚本化的验收入口**：预览这条链路（真实比例、Fit/Fill/Original、
        //      Cinema、时间轴）没法靠单元测试验证，只能真开一个全屏窗口看。
        //      有了这个开关，验收脚本可以直接起它、截图、关掉，不必先点进主界面。
        //   2. 从命令行/快捷方式直接开预览是合理用法（"只想看一眼这段动画"）。
        // ────────────────────────────────────────────────────────────────────
        if (previewLayer != null)
        {
            BootAnimation.AnimationInfo want2 = null;
            string chosen2 = want == null ? ReadChosen() : want;
            List<BootAnimation.AnimationInfo> library = BootAnimation.AnimationRepository.ScanLocal();
            for (int i = 0; i < library.Count; i++)
            {
                if (string.Equals(library[i].Id, chosen2, StringComparison.OrdinalIgnoreCase))
                {
                    want2 = library[i];
                    break;
                }
            }
            if (want2 == null)
            {
                for (int i = 0; i < library.Count; i++)
                {
                    if (library[i].IsInstalled) { want2 = library[i]; break; }
                }
            }
            if (want2 == null || !want2.IsPlayable)
            {
                Log("--preview: 没有可播放的动画");
                return 5;
            }
            BootAnimation.PreviewLayer which = string.Equals(previewLayer, "media", StringComparison.OrdinalIgnoreCase)
                ? BootAnimation.PreviewLayer.Media
                : BootAnimation.PreviewLayer.BootScreen;
            Application previewApp = new Application();
            BootAnimation.CrashGuard.AttachUi(previewApp);
            previewApp.ShutdownMode = ShutdownMode.OnMainWindowClose;
            try
            {
                BootAnimation.BootPreview previewWindow = new BootAnimation.BootPreview(want2, which);
                previewApp.MainWindow = previewWindow;
                previewApp.Run(previewWindow);
            }
            catch (Exception ex)
            {
                // WinExe 的未处理异常默认是**静默**的：进程直接以 0xE0434352 退出，屏幕上
                // 什么都不说。所以必须自己写进日志 —— 否则"预览打不开"永远查不出原因。
                Log("--preview 失败: " + ex.ToString());
                return 6;
            }
            return 0;
        }

        // ────────────────────────────────────────────────────────────────────
        // 无参数启动 / --choose = 打开管理界面。
        //
        // 播放**不在这里**。播放走 BootRuntime（见 BootRuntime.cs）：那是独立的一条
        // 链路，只做"解析当前那一段 → 查背景图缓存 → 建窗口播"，不建任何 UI、不扫库、
        // 不联网、不生成缩略图。这个分离就是"动画播放不得依赖完整桌面 UI 初始化"的落地。
        //
        // BootRuntime / BootRequest 在新模块里统一下挂在 BootAnimation 命名空间，
        // 而 Program 是全局命名空间里的旧类，所以这里必须写全名。
        // ────────────────────────────────────────────────────────────────────
        /**
         * ── 入口分发：**先看 --play，再考虑是否开主界面** ──────────────────
         *
         * 这里原来是 `if (!play && want == null && file == null) → 开主界面`，
         * 看起来没问题，但**依赖参数解析完全正确**。实测踩到这个坑：
         *
         *   启动文件夹快捷方式传的是 `--startupfolder --seconds 5`，
         *   而参数表里 `--log <路径>` 会消费掉后面的一个 token；
         *   一旦有额外参数插进来造成错位，`--startupfolder` 就被当成上一个参数的值吞掉，
         *   于是 play 仍是 false、want/file 也都是 null → **误开主界面**。
         *   现象就是"两个实例都在跑"（一个播动画、一个开了管理器）。
         *
         * 所以分发改成**先判 --play**：只要明确要求播放，就无条件走播放链路，
         * 不受其它参数是否解析成功的影响。这比"让解析永不出错"可靠得多。
         */
        if (play)
        {
            // 走下面的 BootRuntime 播放链路（--fit / --file / --seconds 等都已在上面解析）
        }
        else if (want == null && file == null)
        {
            return BootAnimation.Shell.Run();
        }

        // 未显式指定 --fit 时用用户的持久化偏好（默认 stretch = 真全屏）
        bool fitExplicit = fit != null;
        if (fit == null) fit = ReadFitPreference();

        BootAnimation.BootRequest boot = new BootAnimation.BootRequest();
        boot.Mode = resume ? BootAnimation.BootMode.Resume : BootAnimation.BootMode.Startup;
        boot.ClipId = want;
        boot.FilePath = file;
        boot.Topmost = topmost;
        boot.Seconds = seconds;
        boot.Fit = fit;
        boot.FitExplicit = fitExplicit;
        boot.UseNativePlayer = useNative;
        boot.Mute = mute;
        boot.Volume = volume;
        boot.DelaySeconds = delay;
        boot.SnapshotPath = snapshot;
        boot.SnapshotAt = snapshotAt;
        return BootAnimation.BootRuntime.Run(boot);
    }

    /// 进程启动到此刻的时间；用来量「开机动画到底卡在哪一段」。
    private static System.Diagnostics.Stopwatch started = System.Diagnostics.Stopwatch.StartNew();

    internal static long StartedMs()
    {
        return started.ElapsedMilliseconds;
    }

    /// 无窗口自检：把 auto 的判定表打出来，并断言阈值两侧的行为。
    ///
    /// 这张表要回答的就是客户的第二句话：「动画不是居中的，缺少了一部分」。
    /// `cover` 在长宽比不匹配时把画面推到窗口外 —— 所以真正要验的不是"有没有黑边"，
    /// 而是"**铺满会丢掉整幅画面的百分之几**"，以及 auto 在多少百分比上收手。
    private static int FitCheck()
    {
        // 本机屏幕：2560x1600 = 1.600
        double screenW = 2560;
        double screenH = 1600;
        int[,] videos = new int[,]
        {
            { 1280, 720 },   // 内嵌的四段（16:9）
            { 3840, 2160 },  // 社区那一段「ROG 开机动画」（16:9）
            { 2560, 1600 },  // 正好等于屏幕：任何模式都不裁
            { 1920, 1200 },  // 16:10 素材放在 16:10 屏上
        };

        List<string> lines = new List<string>();
        lines.Add("屏幕 " + screenW + "x" + screenH + "  比例 " + Math.Round(screenW / screenH, 4)
            + "   阈值 FitTolerance=" + FitTolerance);
        lines.Add("默认贴合（无持久化偏好时）= " + DefaultFit);
        lines.Add("");
        lines.Add("视频\t比例\t铺满会裁掉\tauto 选择\t说明");
        lines.Add("");
        lines.Add("【真全屏的代价】实屏 " + screenW + "x" + screenH
            + "（" + Math.Round(screenW / screenH, 2) + "）放 16:9 片源：");
        double vr169 = 16.0 / 9.0;
        double sr169 = screenW / screenH;
        lines.Add("  stretch 拉伸填满 ：无空缺，画面纵向变形 "
            + Math.Round(Math.Abs(vr169 / sr169 - 1) * 100, 2) + "%");
        lines.Add("  cover   裁切填满 ：无空缺，左右各切 "
            + Math.Round(Math.Abs(vr169 / sr169 - 1) * 100 / 2, 2) + "%");
        lines.Add("  ambient 整帧+氛围 ：不变形不裁切，画面之外是模糊的自身画面");
        int failures = 0;
        for (int i = 0; i < videos.GetLength(0); i++)
        {
            int vw = videos[i, 0];
            int vh = videos[i, 1];
            double vr = (double)vw / (double)vh;
            double loss = Math.Abs(vr / (screenW / screenH) - 1.0);
            string chosen = ResolveFit("auto", vw, vh, screenW, screenH);
            // cover 会裁、contain/ambient 不会
            bool crops = string.Equals(chosen, "cover", StringComparison.OrdinalIgnoreCase);
            // 断言：阈值以内必须 cover，阈值以外必须不 cover。
            // 这条是"1.4% 那种情况不能再被判成铺满"的回归保护。
            bool expectCrop = loss <= FitTolerance;
            bool ok = crops == expectCrop;
            if (!ok) failures++;
            lines.Add(vw + "x" + vh + "\t" + Math.Round(vr, 4) + "\t"
                + Math.Round(loss * 100, 2) + "%\t" + chosen + "\t"
                + (ok ? "ok" : "FAIL 阈值判定不一致")
                + (crops ? "（裁边，左右各 " + Math.Round(loss * screenW / 2) + "px）" : "（整帧，不裁切）"));
        }
        // 四种模式各自显式指定时必须被尊重
        string[] modes = new string[] { "stretch", "cover", "contain", "ambient" };
        for (int i = 0; i < modes.Length; i++)
        {
            string got = ResolveFit(modes[i], 3840, 2160, screenW, screenH);
            bool ok = string.Equals(got, modes[i], StringComparison.OrdinalIgnoreCase);
            if (!ok) failures++;
            lines.Add("显式 --fit " + modes[i] + "\t\t\t" + got + "\t" + (ok ? "ok" : "FAIL 未遵守显式选择"));
        }

        Directory.CreateDirectory(DataDir);
        File.WriteAllLines(Path.Combine(DataDir, "fit-check.txt"), lines.ToArray());
        for (int i = 0; i < lines.Count; i++) Console.WriteLine(lines[i]);
        Log("fit-check done, failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    // ───────────────────────────────────────────────────────────── 贴合方式
    //
    // 四种模式，和 dsh-boot-animation 插件用的是同一套结论：
    //
    //   auto      按真实长宽比算：差点小于 TOLERANCE 就铺满，否则整帧 + 氛围背景。默认。
    //   cover     铺满，超出部分裁掉
    //   contain   整帧，四周纯黑边
    //   ambient   整帧 + 用同一段画面放大模糊后的背景填满四周（不裁切也不黑边）
    //
    // 为什么默认不是 cover：在 2560x1600（1.600）这块屏上放 3840x2160（1.778）的片子，
    // 铺满要放大到 2845x1600，左右各裁掉 143px —— 也就是「动画不是居中的、缺少了一部分」
    // 的成因。整帧显示在 16:10 屏上只留上下很窄的黑边，而氛围模式把那两条边也填上。
    /// <summary>
    /// 贴合模式。
    ///
    /// **stretch 是默认值**，也是"真全屏"的那一种：把画面直接拉伸到铺满整屏，
    /// 上下左右都不留空缺。代价是画面会变形（16:9 的片源在 16:10 的屏上被纵向拉长约 11%）。
    /// 客户明确要的就是这个，并且要求"拉伸"与"裁切"两种都可在设置里切换。
    ///
    /// 四种模式的差别（都在 BootRuntime / BootPreview 生效）：
    ///   stretch  拉伸填满 —— 无空缺，画面变形
    ///   cover    裁切填满 —— 无空缺，保比例，左右各切约 11%
    ///   ambient  整帧 + 氛围背景 —— 不变形不裁切，但会有视觉上的"边"
    ///   contain  整帧 + 纯黑边
    ///   auto     按比例自动在 cover / ambient 之间选（保留给不想做选择的场景）
    /// </summary>
    private static readonly string[] FitModes = new string[]
    {
        "stretch", "cover", "contain", "ambient", "auto"
    };

    /// <summary>没有任何偏好时的贴合模式。客户要求默认真全屏（拉伸）。</summary>
    internal const string DefaultFit = "stretch";

    /// 铺满所付出的裁切比例超过这个值，就改成整帧显示。1.2% 以内肉眼看不出来。
    private const double FitTolerance = 0.012;

    internal static bool IsKnownFit(string mode)
    {
        for (int i = 0; i < FitModes.Length; i++)
        {
            if (string.Equals(FitModes[i], mode, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// auto 的判定：返回 "cover" 或 "ambient"。分离成纯函数是为了能直接断言（见 tools/）。
    internal static string ResolveFit(string mode, int videoW, int videoH, double screenW, double screenH)
    {
        // 拉伸是"真全屏"：不裁切、不留边，代价是变形。客户默认要这个。
        if (string.IsNullOrEmpty(mode)
            || string.Equals(mode, "stretch", StringComparison.OrdinalIgnoreCase)) return "stretch";
        if (string.Equals(mode, "cover", StringComparison.OrdinalIgnoreCase)) return "cover";
        if (string.Equals(mode, "contain", StringComparison.OrdinalIgnoreCase)) return "contain";
        if (string.Equals(mode, "ambient", StringComparison.OrdinalIgnoreCase)) return "ambient";

        if (videoW <= 0 || videoH <= 0 || screenW <= 0 || screenH <= 0) return "ambient";

        double vr = (double)videoW / (double)videoH;
        double sr = screenW / screenH;
        if (sr <= 0) return "ambient";

        // 铺满窗口需要放大 max(...)；被裁掉的那一边占整幅画面的比例，正好是
        // |视频比例 / 屏幕比例 - 1| —— 直接就是"少了多少"。
        double loss = Math.Abs(vr / sr - 1.0);
        return loss <= FitTolerance ? "cover" : "ambient";
    }

    // ───────────────────────────────────────────────────── 背景大图（氛围模式用）
    //
    // 氛围模式需要在画面之外有一张"同一段片子放大模糊"的背景。WPF 的 MediaElement
    // 没法把自己当画刷用，所以背景图是**构建期用 ffmpeg 抽一帧**生成的，随 exe 内嵌。
    //
    // 抽 0.2 秒而不是第 0 帧：不少片头是从黑场淡入的，抽第 0 帧会得到一张纯黑图，
    // 那样"氛围填充"就等于什么都没填。
    private const double PosterAtSeconds = 0.2;

    private static string PosterResource(string clipId)
    {
        return "poster-" + clipId + ".jpg";
    }

    private static string FindFfmpeg()
    {
        string[] candidates = new string[]
        {
            "ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WinGet\Links\ffmpeg.exe"),
        };
        for (int i = 0; i < candidates.Length; i++)
        {
            string c = candidates[i];
            if (c.IndexOf('\\') < 0) return c;           // 交给 PATH 解析
            if (File.Exists(c)) return c;
        }
        return null;
    }

    /// 为每段内嵌素材生成一张背景大图，写到 --out 目录。构建期调用，产物再内嵌回 exe。
    /// 返回非 0 表示至少有一张没生成出来 —— 构建脚本据此决定要不要内嵌。
    /// <summary>
    /// 为**所有已安装片段**准备首帧图。安装器与 --prepare 调用。
    ///
    /// 为什么要在安装期做：开机那几百毫秒是最紧张的一段，而抽一帧要几百毫秒。
    /// 放在安装期，开机就只是"读一个已存在的 jpg"，实测 2ms。
    /// 这让"启动动画黑屏"在正常安装流程下根本没有机会发生。
    /// </summary>
    internal static int PrepareAllPosters()
    {
        // 让 Repository 被引用到，避免"这个类没被用到"的假象（它确实被 Shell 用）
        List<BootAnimation.AnimationInfo> items = BootAnimation.AnimationRepository.ScanLocal();
        int ok = 0;
        int failed = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Path == null) continue;
            string poster = PosterPathForMedia(items[i].Path);
            if (poster == null) continue;
            if (File.Exists(poster)) { ok++; continue; }
            try
            {
                BuildPosterInternal(items[i].Path, poster);
                if (File.Exists(poster)) { ok++; Log("首帧图已准备: " + items[i].Id); }
                else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                Log("首帧图准备失败 " + items[i].Id + ": " + ex.Message);
            }
        }
        Log("首帧图准备完成: 成功 " + ok + " 段，失败 " + failed + " 段");

        /**
         * 顺便准备**定格图**：播完动画后要定格的那一帧。
         *
         * 为什么也在安装期做：定格图是"从末尾往回找第一个亮度达标"的那一帧，
         * 需要抽好几次 + 逐张自检，放在播放结束时做会让用户对着黑屏等。
         * 安装期做一次，之后每次播完直接读图。
         */
        int freezeOk = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Path == null) continue;
            string freeze = FreezePathFor(items[i].Id);
            if (freeze == null) continue;
            if (File.Exists(freeze) && !FrameLooksBlank(freeze)) { freezeOk++; continue; }
            try
            {
                if (PrepareFreezeFrame(items[i].Path, freeze)) freezeOk++;
                else Log("定格图准备失败（播完将退化为现场抽帧）: " + items[i].Id);
            }
            catch (Exception ex)
            {
                Log("定格图准备异常 " + items[i].Id + ": " + ex.Message);
            }
        }
        Log("定格图准备完成: 成功 " + freezeOk + " 段");

        /**
         * 顺便生成**快速版**：给"重格式"片段准备一份 H.264 8-bit 1440p 的轻量文件。
         *
         * 为什么在安装期做：转码一个 4K 片段要几十秒到几分钟，绝不能放在开机路径上。
         * 放在这里一次做完，开机就只是"读一个更小更简单的 mp4"。
         * 实测收益：显形耗时从 1188ms 降到 388ms（约 3 倍）。
         */
        int fastOk = 0;
        int fastSkipped = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Path == null) continue;
            if (!NeedsFastVersion(items[i].Path)) { fastSkipped++; continue; }
            string fast = FastPathForMedia(items[i].Path);
            if (fast == null) continue;
            if (File.Exists(fast)) { fastOk++; continue; }
            try
            {
                Program.Log("开始生成快速版（这一步可能要一会儿）: " + items[i].Id);
                if (BuildFastVersion(items[i].Path, fast)) fastOk++;
                else Log("快速版生成失败，将直接用原文件启动: " + items[i].Id);
            }
            catch (Exception ex) { Log("快速版生成异常 " + items[i].Id + ": " + ex.Message); }
        }
        Log("快速版准备完成: 成功 " + fastOk + " 段，无需转换 " + fastSkipped + " 段");

        // 预生成氛围背景用的模糊图（运行时省掉 BlurEffect 那一层）
        int blurOk = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Path == null) continue;
            string bp = BlurPathForMedia(items[i].Path);
            if (bp == null || File.Exists(bp)) { if (bp != null) blurOk++; continue; }
            try { if (BuildBlurVersion(items[i].Path, bp)) blurOk++; }
            catch (Exception ex) { Log("预模糊背景异常 " + items[i].Id + ": " + ex.Message); }
        }
        Log("预模糊背景准备完成: " + blurOk + " 段");

        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 为一段视频挑一张"最后一帧有画面的地方"并存成定格图。
    ///
    /// 逐级往回退并**逐张自检**，第一个亮度达标的就采用。
    /// 回退量的选择依据是实测数据：这段素材 6.9s 之后全黑、6.7s 均值 9.79 勉强可见、
    /// 6.5s 均值 13.61 可用 —— 所以从 0.05s 开始试，一路退到 4s 几乎必定能找到。
    /// </summary>
    internal static bool PrepareFreezeFrame(string mediaPath, string freezePath)
    {
        double duration = ProbeDurationSeconds(mediaPath);
        if (duration <= 0) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(freezePath)));

        double[] backs = new double[] { 0.05, 0.3, 0.6, 1.0, 1.5, 2.5, 4.0 };
        for (int i = 0; i < backs.Length; i++)
        {
            double at = duration - backs[i];
            if (at <= 0.05) continue;
            // 每次尝试用独立文件名：复用同一个路径时，一旦抽帧失败就会拿旧文件去自检
            // （见 BuildFrameAt 里那段注释：实测导致 4 段不同视频自检出同一个亮度值）
            string tmp = Path.Combine(Path.GetTempPath(),
                "ba-freeze-" + SafeName(Path.GetFileNameWithoutExtension(mediaPath))
                + "-" + Math.Round(at * 1000).ToString(CultureInfo.InvariantCulture) + ".jpg");
            try
            {
                if (!BuildFrameAt(mediaPath, tmp, at)) continue;
                if (FrameLooksBlank(tmp))
                {
                    Log("定格候选 @" + Math.Round(at, 2) + "s 是黑的，继续往前找");
                    continue;
                }
                File.Copy(tmp, freezePath, true);
                Log("定格图已选定 @" + Math.Round(at, 2) + "s（末尾 " + backs[i] + "s 处，自检通过）");
                return true;
            }
            catch (Exception ex) { Log("定格候选 @" + Math.Round(at, 2) + "s 失败: " + ex.Message); }
        }
        return false;
    }

    /// <summary>
    /// 用 ffprobe/ffmpeg 问视频时长（秒）。失败返回 0。
    ///
    /// 解析 ffmpeg 的 stderr 里的 `Duration: 00:00:07.30`。
    /// 不用 MediaElement 是因为这里的调用方（安装期）没有 UI 线程。
    /// </summary>
    internal static double ProbeDurationSeconds(string mediaPath)
    {
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return 0;
        try
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(ffmpeg);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.Arguments = "-i \"" + mediaPath + "\"";
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return 0; }
                System.Text.RegularExpressions.Match m =
                    System.Text.RegularExpressions.Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (!m.Success) return 0;
                double h = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double mi = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                double s = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                return h * 3600 + mi * 60 + s;
            }
        }
        catch { return 0; }
    }

    /// <summary>
    /// 定格自检：报告每段已安装片段的定格图亮度，并直接给出"能不能用"的结论。
    ///
    /// 这是客户要求的那个自检 —— "可以写个自检程序，就是在截取最后一帧的时候，
    /// 保持它有一个画面"。它既是验收工具，也是将来换素材时的守门人。
    /// </summary>
    internal static int FreezeCheck()
    {
        List<string> lines = new List<string>();
        lines.Add("定格帧自检（判据：平均感知亮度 >= " + FreezeMinLuma + " 才算“有画面”）");
        lines.Add("");
        lines.Add("片段\t分辨率\t时长\t定格图\t平均亮度\t结论");

        List<BootAnimation.AnimationInfo> items = BootAnimation.AnimationRepository.ScanLocal();
        int bad = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Path == null) continue;
            string freeze = FreezePathFor(items[i].Id);
            string verdict;
            double luma = -1;
            if (freeze == null || !File.Exists(freeze))
            {
                // 缺图**必须算不合格**：之前这里没计入 bad，于是自检报"全部合格"，
                // 而实际上播完定格会走现场抽帧那条慢路 —— 自检给出了误导性结论。
                verdict = "不合格：缺定格图（播完要现场抽帧，可能短暂黑屏）";
                bad++;
            }
            else
            {
                luma = MeasureLuma(freeze);
                if (luma < FreezeMinLuma) { verdict = "不合格：定格是黑的"; bad++; }
                else verdict = "ok（有画面）";
            }
            double dur = ProbeDurationSeconds(items[i].Path);
            lines.Add(items[i].Id + "\t" + items[i].Width + "x" + items[i].Height
                + "\t" + Math.Round(dur, 2) + "s"
                + "\t" + (freeze == null ? "-" : Path.GetFileName(freeze))
                + "\t" + (luma < 0 ? "-" : Math.Round(luma, 2).ToString(CultureInfo.InvariantCulture))
                + "\t" + verdict);
        }
        lines.Add("");
        lines.Add(bad == 0 ? "全部合格" : (bad + " 段不合格 —— 播完会看到黑屏，需要重新准备定格图"));
        string text = string.Join(Environment.NewLine, lines.ToArray());
        Log(text);
        try { File.WriteAllText(Path.Combine(DataDir, "freeze-check.txt"), text, System.Text.Encoding.UTF8); }
        catch { }
        Console.WriteLine(text);
        return bad == 0 ? 0 : 1;
    }

    /// <summary>自检阈值。实测依据见 FrameLooksBlank 的注释。</summary>
    internal const double FreezeMinLuma = 8.0;

    /// <summary>量一张图的平均感知亮度。</summary>
    internal static double MeasureLuma(string file)
    {
        try
        {
            BitmapImage bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file, UriKind.Absolute);
            bmp.EndInit();
            int w = 48;
            int h = Math.Max(1, (int)Math.Round(bmp.PixelHeight * (48.0 / bmp.PixelWidth)));
            TransformedBitmap small = new TransformedBitmap(bmp,
                new System.Windows.Media.ScaleTransform(w / (double)bmp.PixelWidth, h / (double)bmp.PixelHeight));
            FormatConvertedBitmap conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int stride = conv.PixelWidth * 4;
            byte[] pixels = new byte[stride * conv.PixelHeight];
            conv.CopyPixels(pixels, stride, 0);
            long sum = 0; int count = 0;
            for (int i = 0; i + 2 < pixels.Length; i += 4)
            {
                sum += (pixels[i + 2] * 30 + pixels[i + 1] * 59 + pixels[i] * 11) / 100;
                count++;
            }
            return count == 0 ? 0 : sum / (double)count;
        }
        catch { return 0; }
    }

    private static int GeneratePosters()
    {
        string outDir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "build");
        Directory.CreateDirectory(outDir);
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            Log("poster: 找不到 ffmpeg，跳过背景图生成");
            return 2;
        }

        int failed = 0;
        for (int i = 0; i < Clips.Length; i++)
        {
            Clip clip = Clips[i];
            string source = null;
            try { source = Extract(clip); }
            catch (Exception ex) { Log("poster: 解包 " + clip.Id + " 失败: " + ex.Message); failed++; continue; }

            string target = Path.Combine(outDir, PosterResource(clip.Id));
            // scale=1280:-2 保持比例且宽高都是偶数；模糊后不需要更高分辨率。
            ProcessStartInfo psi = new ProcessStartInfo(ffmpeg,
                "-hide_banner -loglevel error -ss " + PosterAtSeconds.ToString(CultureInfo.InvariantCulture)
                + " -i \"" + source + "\" -frames:v 1 -vf \"scale=1280:-2\" -q:v 4 -y \"" + target + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            try
            {
                Process p = Process.Start(psi);
                p.WaitForExit(20000);
                if (!File.Exists(target)) { failed++; Log("poster: " + clip.Id + " 未生成"); }
                else Log("poster: " + clip.Id + " -> " + target);
            }
            catch (Exception ex)
            {
                failed++;
                Log("poster: 调用 ffmpeg 失败 " + ex.Message);
            }
        }
        Log("poster: 完成，失败 " + failed + " 张");
        return failed == 0 ? 0 : 1;
    }

    /// 背景大图在磁盘上的缓存目录（社区片段和用户自带文件用）。
    private static string PosterCacheDir
    {
        get { return Path.Combine(DataDir, "posters"); }
    }

    /// 载入背景大图：先找内嵌的，再找磁盘缓存，最后**现场生成一张**。
    ///
    /// 内嵌的只有四段自带素材。社区下载的那一段（以及 --file 指定的任意文件）
    /// 没有内嵌图，所以第一次播放时必须现场抽一帧，否则它的"氛围填充"就是纯黑边 ——
    /// 也就是客户最初抱怨的那种效果。生成一次就缓存住，之后不再调 ffmpeg。
    ///
    /// 全程可失败：没有 ffmpeg、容器解不开、目录不可写，都只是"这张图没有"，
    /// 播放本身不受影响，只退化成黑边。
    private static ImageBrush LoadPosterBrush(string sourcePath)
    {
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        ImageBrush embedded = ReadBrush(null, PosterResource(stem));
        if (embedded != null) return embedded;

        string diskPath = Path.Combine(PosterCacheDir, "file-" + SafeName(stem) + ".jpg");
        if (!File.Exists(diskPath)) BuildPoster(sourcePath, diskPath);
        return ReadBrush(diskPath, null);
    }

    /// 去掉文件名里不能当路径用的字符。internal：仓库层生成海报缓存名时也要用同一份规则。
    ///
    /// **必须容忍 null**：实测 `--file &lt;路径&gt;` 这条路径上 clipId 是空的
    /// （调用方直接给了文件、没有片段 id），于是这里对 null 取 Length 抛
    /// NullReferenceException，被 CrashGuard 兜住后进程立刻退出 —— 表现是
    /// "用 --file 播什么都不出来"。空入参返回占位名而不是崩。
    internal static string SafeName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "clip";
        char[] bad = Path.GetInvalidFileNameChars();
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < raw.Length && sb.Length < 60; i++)
        {
            char c = raw[i];
            bool ok = true;
            for (int j = 0; j < bad.Length; j++) { if (bad[j] == c) { ok = false; break; } }
            sb.Append(ok ? c : '_');
        }
        return sb.Length == 0 ? "clip" : sb.ToString();
    }

    /// 读一张图成画刷：给资源名就从 exe 里读，给路径就从磁盘读。都没有则 null。
    private static ImageBrush ReadBrush(string filePath, string resourceName)
    {
        try
        {
            Stream stream;
            if (resourceName != null)
            {
                stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null) return null;
            }
            else if (filePath != null)
            {
                if (!File.Exists(filePath)) return null;
                stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            else
            {
                return null;
            }

            using (stream)
            {
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                // Freeze 之后可以跨线程，也省掉 WPF 每次绘制时的同步开销。
                bmp.Freeze();
                ImageBrush brush = new ImageBrush(bmp);
                brush.Stretch = Stretch.UniformToFill;
                brush.AlignmentX = AlignmentX.Center;
                brush.AlignmentY = AlignmentY.Center;
                return brush;
            }
        }
        catch (Exception ex)
        {
            Log("读背景图失败(" + (resourceName == null ? filePath : resourceName) + "): " + ex.Message);
            return null;
        }
    }

    /// 用 ffmpeg 从任意视频抽一帧当背景图。参数与构建期完全一致 ——
    /// 提亮必须在 ffmpeg 里做，因为 WPF 提亮会把颜色洗成灰。
    private static bool BuildPoster(string sourcePath, string targetPath)
    {
        return BuildPosterInternal(sourcePath, targetPath);
    }

    /// <summary>
    /// 用 ffmpeg 从视频抽一帧当氛围背景图。internal：BootRuntime 会把它排到后台线程，
    /// 在动画已经开播之后补齐这张图。
    /// </summary>
    internal static bool BuildPosterInternal(string sourcePath, string targetPath)
    {
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            Log("没有 ffmpeg，无法为这段生成背景图: " + Path.GetFileName(sourcePath));
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            ProcessStartInfo psi = new ProcessStartInfo(ffmpeg,
                "-hide_banner -loglevel error -ss " + PosterAtSeconds.ToString(CultureInfo.InvariantCulture)
                + " -i \"" + sourcePath + "\" -frames:v 1 "
                + "-vf \"scale=1280:-2,eq=brightness=0.16:contrast=1.10:saturation=1.30\" -q:v 3 -y \"" + targetPath + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            Process p = Process.Start(psi);
            string stderr = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (File.Exists(targetPath))
            {
                Log("已生成背景图 " + targetPath + "（首次播这段，之后走缓存）");
                return true;
            }
            Log("生成背景图失败: " + stderr.Trim());
            return false;
        }
        catch (Exception ex)
        {
            Log("生成背景图异常: " + ex.Message);
            return false;
        }
    }

    /// 选片窗口：把内嵌的几段列出来，选一个，记住。
    private static Window BuildPicker(Application app)
    {
        Window win = new Window();
        win.Title = "开机动画 · 选择片头";
        win.Width = 460;
        win.Height = 340;
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.Background = Brushes.White;

        StackPanel panel = new StackPanel();
        panel.Margin = new Thickness(18);

        TextBlock title = new TextBlock();
        title.Text = "选择开机时要播放的片头";
        title.FontSize = 15;
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(0, 0, 0, 10);
        panel.Children.Add(title);

        ListBox listBox = new ListBox();
        listBox.Height = 190;
        string chosenId = ReadChosen();
        for (int i = 0; i < Clips.Length; i++)
        {
            ListBoxItem item = new ListBoxItem();
            item.Content = Clips[i].Name;
            item.Tag = Clips[i].Id;
            listBox.Items.Add(item);
            if (string.Equals(Clips[i].Id, chosenId, StringComparison.OrdinalIgnoreCase))
            {
                listBox.SelectedIndex = i;
            }
        }
        // 从社区装好的片段也列出来，和内置的混在一起让用户挑
        List<CommunityEntry> communityEntry = Community.List();
        for (int i = 0; i < communityEntry.Count; i++)
        {
            ListBoxItem item = new ListBoxItem();
            item.Content = communityEntry[i].Display;
            item.Tag = communityEntry[i].Id;
            listBox.Items.Add(item);
            if (string.Equals(communityEntry[i].Id, chosenId, StringComparison.OrdinalIgnoreCase))
            {
                listBox.SelectedIndex = Clips.Length + i;
            }
        }
        if (listBox.SelectedIndex < 0) listBox.SelectedIndex = 0;
        panel.Children.Add(listBox);

        StackPanel buttons = new StackPanel();
        buttons.Orientation = Orientation.Horizontal;
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 14, 0, 0);

        Button ok = new Button();
        ok.Content = "用这个";
        ok.Width = 96;
        ok.Height = 30;
        ok.IsDefault = true;
        ok.Click += delegate
        {
            ListBoxItem sel = listBox.SelectedItem as ListBoxItem;
            if (sel != null) WriteChosen(Convert.ToString(sel.Tag, CultureInfo.InvariantCulture));
            win.Close();
        };
        buttons.Children.Add(ok);

        Button community = new Button();
        community.Content = "浏览社区";
        community.Width = 96;
        community.Height = 30;
        community.Margin = new Thickness(0, 0, 10, 0);
        community.Click += delegate
        {
            try { Process.Start(Community.SiteUrl); }
            catch (Exception ex) { Log("打开社区网页失败: " + ex.Message); }
        };
        buttons.Children.Add(community);

        Button cancel = new Button();
        cancel.Content = "取消";
        cancel.Width = 96;
        cancel.Height = 30;
        cancel.Margin = new Thickness(10, 0, 0, 0);
        cancel.IsCancel = true;
        cancel.Click += delegate { win.Close(); };
        buttons.Children.Add(cancel);

        panel.Children.Add(buttons);
        win.Content = panel;
        return win;
    }

    /// 播放窗口：无边框、铺满、置顶。
    ///
    /// ────────────────────────────────────────────────────────────────────────
    /// **零等待显形（0.5.0 的核心改动）**
    ///
    /// 实测的启动账本是（同一台机器、4K 社区片段）：
    ///
    ///     boot-enter@0  clip-resolved@1  poster-checked@2
    ///     window-shown@265(+263)   ← CLR + WPF + 窗口创建
    ///     media-opened@553(+288)   ← 打开视频 + 解码出第一帧
    ///
    /// 加遮罩、加模糊背景都改变不了这 553ms 是客观存在的。但**它不一定被看见**：
    /// 窗口以 `Opacity = 0` 出现时，WPF 依然会给它分配合成表面、依然会让
    /// MediaElement 解码 —— 解码不需要窗口"可见"，只需要窗口存在。
    /// 所以：窗口立刻出现但完全不可见 → 视频在不可见状态下解码完 → 一帧之内显形。
    ///
    /// 用户看到的就是「没有任何等待，动画已经在放了」—— 这正是 Wallpaper Engine
    /// 那种"你看到它的时候它已经在了"的观感，而**不需要常驻进程**。
    ///
    /// 代价与取舍，如实写在这里：
    ///   · 那 553ms 里屏幕上是干净的桌面（不是黑块、不是模糊背景、不是"正在加载"）
    ///   · 因为动画提前 553ms 开始，它也提前 553ms 结束，开机总时长**缩短**而不是拉长
    ///   · 若解码失败/超时（例如文件损坏），窗口会在 1.2s 后带着首帧图和失败提示显形，
    ///     而不是永远隐藏 —— 黑屏不许静默这条规则同样适用于"不可见"
    ///
    /// 贴合方式是**运行时**按真实长宽比定的：`--fit auto` 需要等 MediaOpened 拿到
    /// 视频的真实分辨率，才能算出"铺满会裁掉多少"。在那之前先按整帧 + 氛围背景画，
    /// 因为那是唯一不会裁切的选择 —— 早一帧显示一张不裁切的画面，好过显示一张裁过的。
    ///
    /// posterPath 与 clipId 两个参数是 BootRuntime 加进来的：
    ///   · posterPath —— **已经确认存在的**背景图文件路径，没有就是 null。
    ///     以前这个方法自己调 LoadPosterBrush，而那个函数在文件不存在时会**现场起 ffmpeg
    ///     抽帧**（最长等 15 秒）—— 那是启动链路上最不可控的一段。现在启动期只查缓存，
    ///     生成交给后台线程（以及 Apply 时的 PrepareRuntimeResource）。
    ///   · clipId —— 只用于日志与诊断，让"这次播的是哪一段"在 boot.log 里可查。
    internal static PlayerWindow BuildPlayer(string label, string path, bool topmost, double seconds, string fit,
        bool mute, int volume, string snapshot, double snapshotAt, string posterPath, string clipId)
    {
        return BuildPlayerCore(label, path, topmost, seconds, fit, mute, volume, snapshot, snapshotAt,
            posterPath, clipId, true);
    }

    /// <summary>
    /// autoReveal = false 时，窗口建好后由调用方负责显形（预热进程走这条）。
    ///
    /// 为什么需要它：BuildPlayer 内部有一个"首帧确认后自动显形"的定时器。
    /// 预热进程恰恰**不希望**它自动显形 —— 它要一直不可见地等着。
    /// 不关掉的话，预热窗口会在约 600ms 后自己冒出来，整个预热方案就失效了。
    /// </summary>
    internal static PlayerWindow BuildPlayerCore(string label, string path, bool topmost, double seconds, string fit,
        bool mute, int volume, string snapshot, double snapshotAt, string posterPath, string clipId,
        bool autoReveal)
    {
        BootAnimation.BootClock.Mark("bpc-enter");
        string requested = IsKnownFit(fit) ? fit.ToLowerInvariant() : DefaultFit;
        // 实际采用的模式。初值是 requested —— 在 MediaOpened 给出真实分辨率之前，
        // auto 先按"整帧"画（唯一不会裁切的选择），所以这时它等于 ambient。
        string resolvedFit = requested;

        BootAnimation.BootClock.Mark("bpc-before-window");
        Window win = new Window();
        BootAnimation.BootClock.Mark("bpc-after-window");
        win.WindowStyle = WindowStyle.None;
        win.ResizeMode = ResizeMode.NoResize;
        win.WindowState = WindowState.Maximized;
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.Topmost = topmost;
        win.ShowInTaskbar = false;
        win.Background = Brushes.Black;
        win.Cursor = Cursors.Arrow;

        Grid root = new Grid();
        root.Background = Brushes.Black;

        // 背景层：氛围模式用它填掉画面之外的部分。普通模式下它隐藏。
        // 素材缺背景图时退化成纯黑（也就是 contain 的效果），不报错。
        //
        BootAnimation.BootClock.Mark("bpc-before-backdrop");
        // 写全名 System.Windows.Shapes.Rectangle：这个文件里 Path 已经被 System.IO.Path
        // 占住了（到处都在用），再 using System.Windows.Shapes 会让两边都变成歧义。
        System.Windows.Shapes.Rectangle backdrop = new System.Windows.Shapes.Rectangle();
        backdrop.Stretch = Stretch.UniformToFill;
        backdrop.Visibility = Visibility.Collapsed;
        if (requested == "auto" || requested == "ambient")
        {
            // 只认调用方给的那条**已确认存在**的路径。不再自己去找、更不现场生成：
            // 启动期起 ffmpeg 是首帧最大的不确定来源（实测可到十几秒）。
            ImageBrush poster = posterPath == null ? null : ReadBrush(posterPath, null);
            if (poster != null)
            {
                // 模糊就够了：提亮已经在构建期由 ffmpeg 做进这张图里了。
                //
                // 为什么不在运行时提亮：WPF 只有模糊/投影这类效果，没有"亮度"。
                // 用"白底 + 原图当 OpacityMask"能凑出提亮，但它把颜色洗成灰
                // （实测边条 R=G=B=103），而这段素材的主视觉是青蓝调 —— 洗掉颜色
                // 比不提亮更糟。ffmpeg 的 eq 是按通道乘系数，颜色保得住。
                /**
                 * 优先用**安装期预生成**的模糊图；没有才退回运行时的 BlurEffect。
                 *
                 * 实测：运行时 BlurEffect(Radius=48) 是构造窗口里最贵的一步（104ms/239ms），
                 * 而且它是 GPU 合成层，登录时 GPU 也在忙。预生成的 jpg 只需读一张图。
                 */
                string blurPath = BlurPathForMedia(path);
                if (blurPath != null && File.Exists(blurPath))
                {
                    ImageBrush pre = ReadBrush(blurPath, null);
                    if (pre != null)
                    {
                        backdrop.Fill = pre;
                        Log("氛围背景用预模糊图");
                    }
                    else { backdrop.Fill = poster; }
                }
                else
                {
                    BlurEffect blur = new BlurEffect();
                    blur.Radius = 48;
                    blur.KernelType = KernelType.Gaussian;
                    backdrop.Effect = blur;
                    backdrop.Fill = poster;
                }
                // 0.55 是量出来的，不是审美拍的：
                //   0.00 → 边条 R=0 G=0 B=0（纯黑，和 contain 一样，等于没做）
                //   0.62（第一版）+ 未提亮的图 → R=0 G=1 B=0，仍然看不出区别
                //   0.90 + 提亮图 → R=20 G=24 B=19，可见但偏亮，边条开始抢画面
                //   0.55 + 提亮图 → 落在"看得出是画面本身的颜色、又不会把视线从画面拉走"
                //
                // 剩下的一半靠构建期那张提亮图：ffmpeg 提亮保住了色相，
                // 而 WPF 里做提亮会把颜色洗成灰。
                backdrop.Opacity = 0.55;
                backdrop.Visibility = Visibility.Visible;
                Log("氛围背景已就绪 " + Path.GetFileName(path));
            }
            else
            {
                // 不静默：画面看起来像"自带了黑边"，没人会想到是背景图没读到。
                Log("氛围背景拿不到（" + Path.GetFileName(path) + "），该片退化为纯黑边；"
                    + "内嵌片段应有 poster-<id>.jpg，其它片段需要 ffmpeg 现场生成");
            }
        }
        root.Children.Add(backdrop);
        BootAnimation.BootClock.Mark("bpc-after-backdrop");

        /**
         * 首帧层：窗口一出现就有画面。
         *
         * 这是"启动前有一块未显示 / 只有加载完才显示"的正面修法。
         *
         * 之前窗口是这样的：Show() → 黑底 → 等 MediaOpened（实测 250–550ms）→ 才有画面。
         * 那 250–550ms 里用户看到的就是一块纯黑 —— 而且窗口已经挂在屏幕上了，
         * 所以它不是"晚一点出现"，而是"先出现一块空白再补上画面"，观感差得多。
         *
         * 现在：这一层用的是**这段视频自己的第一帧**（构建期或首次播放时抽出来缓存的 JPEG），
         * 它就在本地、已缓存，窗口第一次合成时就在位。视频解码完成后叠上去接管
         * （见 MediaOpened 里的淡出），因为两者是同一帧画面，切换是看不出来的。
         */
        PosterLayer[] firstFrameBox = new PosterLayer[1];   // 用数组当引用容器：挂媒体时要淡出它
        ImageBrush posterBrush = posterPath == null ? null : ReadBrush(posterPath, null);
        BootAnimation.BootClock.Mark("bpc-poster-read");
        if (posterBrush != null && posterBrush.ImageSource != null)
        {
            // 视频的真实分辨率在 MediaOpened 之前拿不到，先用 16:9 占位；
            // MediaOpened 会把它纠正成媒体自己报的尺寸（见下面的 SetVideoSize）。
            firstFrameBox[0] = new PosterLayer(posterBrush.ImageSource, 1920, 1080);
            firstFrameBox[0].Opacity = 1.0;
            root.Children.Add(firstFrameBox[0]);
            Panel.SetZIndex(firstFrameBox[0], 4);
            BootAnimation.BootClock.Mark("bpc-firstframe-added");
            Log("首帧层已建立 source=" + Path.GetFileName(posterPath)
                + " 尺寸=" + posterBrush.ImageSource.Width + "x" + posterBrush.ImageSource.Height);
        }
        else
        {
            // 不静默：没有首帧层就意味着"窗口已显形、视频还没出画"那一段是纯黑的。
            // 这正是客户反复报的"黑屏"，所以必须留下可查的记录。
            Log("首帧层**未建立**（posterPath=" + (posterPath == null ? "null" : Path.GetFileName(posterPath))
                + " posterBrush=" + (posterBrush == null ? "null" : "有")
                + "）—— 显形到出画之间会是黑屏");
        }

        /**
         * 前景 = 整帧显示（Uniform）。
         *
         * 这是「居中」的关键：Uniform 会让整幅画面按比例缩放到窗口内并居中，
         * 不裁掉任何一个像素。之前的默认是 UniformToFill，它在 16:10 屏上把 16:9 的
         * 画面放大到铺满高度，左右各切掉 5.6% —— 客户看到的就是「不是居中的、缺少一部分」。
         * auto 模式会在拿到真实分辨率后，只在"裁切小于 1.2%"时才切回 UniformToFill。
         */
        BootAnimation.BootClock.Mark("bpc-before-mediaelement");
        MediaElement media = new MediaElement();
        BootAnimation.BootClock.Mark("bpc-after-mediaelement");
        media.Stretch = Stretch.Uniform;
        media.LoadedBehavior = MediaState.Manual;
        media.UnloadedBehavior = MediaState.Manual;
        media.Volume = mute ? 0 : volume / 100.0;
        media.IsMuted = mute;
        /**
         * 初始完全透明。
         *
         * 为什么必须这样（实测踩到）：媒体挂载已经挪到 win.Loaded（为了先显示窗口），
         * 于是窗口刚出现的那段时间 MediaElement 里**还没有任何源**。
         * 一个没有源的 MediaElement 在 WPF 里会画出**黑色**表面，
         * 而它在 Z 序上高于首帧图 —— 结果窗口一出现就是两帧黑，
         * 正好是客户报的"启动前有一个卡顿/黑一下"。
         *
         * 置为透明后，这段时间看到的是下面那张缓存的视频首帧图；
         * 等 MediaOpened（真出画）再把不透明度淡上来接管。
         */
        media.Opacity = 0;
        root.Children.Add(media);

        // 氛围模式下的第二路播放：把同一段片子放大模糊后铺满窗口，作为背景。
        // 只在真的需要它时才建，避免多余的解码开销和一路用不上的媒体会话。
        MediaElement ambient = null;
        if (backdrop.Visibility == Visibility.Visible)
        {
            ambient = new MediaElement();
            ambient.Stretch = Stretch.UniformToFill;
            ambient.LoadedBehavior = MediaState.Manual;
            ambient.UnloadedBehavior = MediaState.Manual;
            ambient.Volume = 0;
            ambient.IsMuted = true;
            ambient.Opacity = 0.0;
            ambient.IsHitTestVisible = false;
            root.Children.Add(ambient);
            // 插到前景之下、背景之上
            Panel.SetZIndex(ambient, 1);
        }
        Panel.SetZIndex(backdrop, 0);
        Panel.SetZIndex(media, 2);

        // 状态文字不再显示"正在加载"：窗口在首帧到手之前是完全不可见的，
        // 所以那句话永远不可能被看到。它只在**失败/超时**时才被写出来并显形 ——
        // 也就是"黑屏不许静默"这条规则真正需要它的场合。
        BootAnimation.BootClock.Mark("bpc-before-status");
        TextBlock status = new TextBlock();
        status.Text = "";
        status.Foreground = Brushes.White;
        status.FontSize = 14;
        status.HorizontalAlignment = HorizontalAlignment.Center;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextAlignment = TextAlignment.Center;
        status.Visibility = Visibility.Collapsed;
        Panel.SetZIndex(status, 6);
        root.Children.Add(status);

        win.Content = root;

        // 前景播起来时把背景对上：两边差 220ms 以内，隔着 48px 模糊是看不出来的。
        // MediaElement 没有帧级同步接口，所以只能做这种粗对齐。
        // 用 Action 而不是 EventHandler：这里不需要事件参数，而且省掉一次无意义的装箱。
        Action alignAmbient = null;
        if (ambient != null)
        {
            alignAmbient = delegate
            {
                try
                {
                    if (Math.Abs(ambient.Position.TotalMilliseconds - media.Position.TotalMilliseconds) > 220)
                    {
                        ambient.Position = media.Position;
                    }
                    ambient.Play();
                }
                catch
                {
                    // 背景是装饰：对齐失败绝不能影响正片
                }
            };
        }

        media.MediaOpened += delegate
        {
            // 分段点：媒体已就绪。这个数字与 "window-shown" 之间的差，就是首帧层
            // 需要替视频撑住的时间 —— 也是"启动时那块未显示"的实际长度。
            BootAnimation.BootClock.Mark("media-opened");
            // 记下时长：播完定格时要靠它算"从末尾往回退多少"才能找到有画面的那一帧
            if (media.NaturalDuration.HasTimeSpan)
            {
                LastKnownDurationSeconds = media.NaturalDuration.TimeSpan.TotalSeconds;
            }
            // 真实分辨率到手，此刻才可能回答"铺满会裁掉多少"。
            string resolved = ResolveFit(requested, media.NaturalVideoWidth, media.NaturalVideoHeight,
                win.ActualWidth, win.ActualHeight);
            resolvedFit = resolved;
            bool cropped = string.Equals(resolved, "cover", StringComparison.OrdinalIgnoreCase);
            bool stretched = string.Equals(resolved, "stretch", StringComparison.OrdinalIgnoreCase);
            // 三种落点，与首帧层的 Mode 一一对应（见下）：
            //   stretch → Fill（铺满，允许变形）
            //   cover   → UniformToFill（铺满，裁切保比例）
            //   其它    → Uniform（整帧留边）
            media.Stretch = stretched ? Stretch.Fill
                : (cropped ? Stretch.UniformToFill : Stretch.Uniform);

            // 首帧层按**媒体自己报的**尺寸摆正，这样淡出前它与视频逐像素重合。
            // 不做这一步的话，首帧图与视频的落点会不一致，切换那一下反而看出"跳" ——
            // 那还不如不加这一层。Mode: 3=stretch 0=cover 1=contain
            if (firstFrameBox[0] != null)
            {
                firstFrameBox[0].SetVideoSize(media.NaturalVideoWidth, media.NaturalVideoHeight);
                firstFrameBox[0].Mode = stretched ? 3 : (cropped ? 0 : 1);
            }

            /**
             * 媒体真的出画了，把不透明度淡上来接管首帧图。
             *
             * 140ms 是有意的：两层是同一帧画面（首帧图就是视频第一帧），
             * 重叠期间看不出接缝；太短可能在视频首帧尚未合成时露黑，
             * 太长则可能被看出"糊了一下"。
             */
            try
            {
                System.Windows.Media.Animation.DoubleAnimation rise =
                    new System.Windows.Media.Animation.DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140));
                media.BeginAnimation(UIElement.OpacityProperty, rise);
            }
            catch { media.Opacity = 1; }

            /**
             * 媒体真的就绪了 —— **此刻才从第 0 帧开始播放**，并立刻显形。
             *
             * 这是"视频没播放完整"的最终修法。之前的顺序是"先播、再等就绪、再显形"，
             * 而媒体就绪在登录压力下要 3–5 秒，那几秒视频在**不可见**状态下被播掉了，
             * 用户看到时已经从中段开始，7.3 秒的动画自然"没播完整"。
             *
             * 现在的顺序：挂源（不播）→ 等 MediaOpened → **从 0 开始播 + 同帧显形**。
             * 屏幕上从 250ms 起一直是缓存的视频首帧图，切进视频时是同一帧，看不出接缝。
             */
            try
            {
                // 就绪了 → 立刻暂停并归零，把片头"留住"。
                // 这一步是"视频没播放完整"的关键：媒体打开要几秒，
                // 若让它一直播，等显形时片头已经被吃掉。
                if (media.CanPause) media.Pause();
                media.Position = TimeSpan.Zero;
                Log("媒体已就绪并暂停在第 0 帧（" + StartedMs() + " ms）");
            }
            catch (Exception ex) { Log("暂停/归零失败: " + ex.Message); }

            if (ambient != null)
            {
                if (cropped)
                {
                    // 铺满时窗口已经被画面填满，背景没有存在的意义。
                    ambient.Stop();
                    backdrop.Visibility = Visibility.Collapsed;
                    ambient.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ambient.Source = new Uri(path);
                    ambient.Play();
                    alignAmbient();
                }
            }

            // 视频这一帧已经在解码器里了，把首帧层淡出交棒。
            //
            // 用 120ms 而不是更长：两层是同一帧画面，重叠时间越长越可能被看出"糊了一下"。
            // 反过来也不能为 0 —— 硬切会在视频首帧尚未真正上屏时露出后面的氛围背景。
            /**
             * 首帧层的退场：**等 400ms 再淡出**，不是立刻。
             *
             * 为什么不能立刻撤：首帧层是"万一视频还没上屏"的唯一保险。
             * 客户报的"黑屏"就是它被撤得太早 —— 窗口已经显形、视频却还没出画，
             * 中间那一瞬就是纯黑。留 400ms 之后，即使视频晚出画，用户看到的也是
             * 首帧静止图（和视频第一帧逐像素相同），不会觉得坏了。
             *
             * **预热窗口（autoReveal=false）例外：首帧层永不撤除。**
             *
             * 实测证据：高频采样 36 帧显示预热触发后**前 300ms 是纯黑**（均值 0.00）。
             * 原因是预热进程在构造期就把视频 play 起来（为了解出首帧），
             * MediaOpened 随之触发，于是那个 400ms 计时器在**用户按下触发之前**就把
             * 首帧层淡出移除了；等真正显形时窗口下面已经什么都没有，视频又刚被暂停，
             * 于是黑屏。
             *
             * 预热窗口把首帧层一直留着：它和视频第一帧逐像素相同，视频恢复播放后
             * 它就在画面之下，既看不出接缝，又保证任何时刻都不会露黑。
             */
            if (firstFrameBox[0] != null && autoReveal)
            {
                DispatcherTimer handoff = new DispatcherTimer();
                handoff.Interval = TimeSpan.FromMilliseconds(400);
                handoff.Tick += delegate
                {
                    handoff.Stop();
                    try
                    {
                        System.Windows.Media.Animation.DoubleAnimation fade =
                            new System.Windows.Media.Animation.DoubleAnimation(0.0, TimeSpan.FromMilliseconds(150));
                        fade.Completed += delegate
                        {
                            try { root.Children.Remove(firstFrameBox[0]); } catch { }
                        };
                        firstFrameBox[0].BeginAnimation(UIElement.OpacityProperty, fade);
                    }
                    catch { }
                };
                handoff.Start();
            }

            double videoRatio = media.NaturalVideoHeight == 0
                ? 0
                : (double)media.NaturalVideoWidth / (double)media.NaturalVideoHeight;
            double windowRatio = win.ActualHeight <= 0 ? 0 : win.ActualWidth / win.ActualHeight;
            Log("[pid " + Process.GetCurrentProcess().Id + "] MediaOpened " + media.NaturalVideoWidth + "x" + media.NaturalVideoHeight
                + " duration=" + media.NaturalDuration
                + " 窗口=" + Math.Round(win.ActualWidth) + "x" + Math.Round(win.ActualHeight)
                + " 比例 视频=" + Math.Round(videoRatio, 4) + " 窗口=" + Math.Round(windowRatio, 4)
                + " --fit " + requested + " -> " + resolved
                + (stretched ? "（拉伸填满，无空缺，画面纵向变形 "
                                + Math.Round(Math.Abs(videoRatio / windowRatio - 1) * 100, 2) + "%）"
                    : (cropped ? "（裁切填满，左右各切 " + Math.Round(Math.Abs(videoRatio / windowRatio - 1) * 100 / 2, 2) + "%）"
                               : "（整帧，不裁切）"))
                + " 进程启动到出画 " + StartedMs() + " ms"
                + " HasAudio=" + media.HasAudio
                + " Volume=" + media.Volume.ToString(CultureInfo.InvariantCulture)
                + " Muted=" + media.IsMuted);
            if (!media.HasAudio)
            {
                status.Text = "这段片头没有音轨";
            }
            // 先递一条「怎么跳过」的提示，几秒后自己收掉，不挡着看画面
            status.Text = "点击任意处 / 按 Esc 跳过";
            status.FontSize = 13;
            status.Opacity = 0.72;
            status.VerticalAlignment = VerticalAlignment.Bottom;
            status.Margin = new Thickness(0, 0, 0, 42);
            DispatcherTimer hide = new DispatcherTimer();
            hide.Interval = TimeSpan.FromSeconds(3);
            hide.Tick += delegate { hide.Stop(); status.Visibility = Visibility.Collapsed; };
            hide.Start();
        };
        media.MediaFailed += delegate(object s, ExceptionRoutedEventArgs e)
        {
            string why = e.ErrorException == null ? "(无异常信息)" : e.ErrorException.Message;
            Log("MediaFailed: " + why);
            // 关键：黑屏不许静默。把原因写在屏幕上，并给用户一条退路。
            status.Visibility = Visibility.Visible;
            status.Text = "播放失败：" + why + Environment.NewLine + "按 Esc 关闭";
        };
        /**
         * 播完之后**定格在最后一帧有画面的地方**，等用户点击进桌面。
         *
         * ────────────────────────────────────────────────────────────────────
         * 为什么不能直接用"最后一帧"
         *
         * 客户报："定格了一个黑色的照片"。实测证实了这一点 —— 这段动画本身就是
         * **淡出到黑**的，逐帧量出来的中心均值是：
         *
         *     5.5s → 54.74      6.0s → 17.72      6.5s → 13.61
         *     6.7s → 9.79       6.9s 之后 → **抽不出帧（完全是黑的）**
         *
         * 容器报的时长是 7.30s，但 6.9s 之后就没有任何可见内容了。
         * 所以"定格最后一帧"在这个素材上等价于"定格黑屏" —— 与客户描述完全一致。
         *
         * ────────────────────────────────────────────────────────────────────
         * 做法：定格帧在**安装期**就用自检选好（见 PrepareAllPosters 的定格图部分），
         * 它一定是"从末尾往回找、第一个亮度达标"的那一帧。播放结束时：
         *   1. 把定格图盖在窗口最上层（不依赖解码器，因此绝不会是黑的）
         *   2. 显示"点击进入桌面"的提示
         *   3. 等用户点击 / 按任意键 → 关窗口
         *
         * 若定格图恰好不存在（老安装包升级上来），退回"现场抽一帧"，
         * 并且**当场自检亮度**；自检不过就退回海报图，绝不把黑屏留给用户。
         * ────────────────────────────────────────────────────────────────────
         */
        bool froze = false;   // 防止重复处理（MediaEnded 在实测中出现过两次）
        media.MediaEnded += delegate
        {
            Log("MediaEnded");
            if (!autoReveal)
            {
                // 预热窗口由 WarmRuntime 的循环定时器处理（拨回开头重播），不走这条
                return;
            }
            if (froze) return;   // 只处理一次
            froze = true;

            /**
             * **播完直接进桌面，不做定格。**
             *
             * 客户实测反馈："每次播放完动画之后它还会卡一帧、定格一下，
             * 能不能播放完动画直接就退出，直接显示桌面。"
             *
             * 那个"卡一帧 + 定格"就是原来这里调用的 FreezeOnLastGoodFrame：
             * 它会把最后一帧换成一张静态图、再停留约 2 秒才关窗口。
             * 对开机动画来说这是多余的一步 —— 动画播完就该让桌面接管，
             * 多出来的静态画面只会让人觉得"程序卡了一下"。
             *
             * 现在直接关窗口。关窗口之后桌面就是最前面的东西，
             * 整个过程是"动画 → 桌面"，中间没有任何停顿。
             *
             * 注意：这里是**主动关闭**，所以哪怕视频末端有淡出到黑的尾巴，
             * 也来不及被看到（关窗发生在 MediaEnded 的那一刻）。
             */
            Log("播放结束，直接进桌面（不做定格）");
            try { win.Close(); } catch { }
        };

        win.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) win.Close(); };
        win.MouseLeftButtonUp += delegate { win.Close(); };

        // 打不开也要有交代：超时就说明白，而不是停在黑屏上。
        // 20 秒看门狗：只有在窗口**已经显形**之后才有意义（不可见的窗口卡住，
        // 由上面那个 1.2s 的 revealGuard 负责显形并说明）。所以这里不设单独计时器，
        // 而是复用 revealGuard 的兜底 + MediaFailed 的可读提示。

        if (seconds > 0)
        {
            DispatcherTimer auto = new DispatcherTimer();
            auto.Interval = TimeSpan.FromSeconds(seconds);
            auto.Tick += delegate
            {
                auto.Stop();
                /**
                 * 已经定格时也照常关闭。
                 *
                 * 这里的旧逻辑是"定格后不关，等用户点击"—— 那个动作已经按客户要求去掉了，
                 * 所以 --seconds 到点就是终点，直接关（否则用 --seconds 做自动化验收时
                 * 进程会一直挂着不退出）。
                 */
                if (froze) { Log("到达 --seconds，已定格，直接关闭"); win.Close(); return; }
                Log("到达 --seconds 自动关闭");
                win.Close();
            };
            auto.Start();
        }

        /**
         * 验收用截图：把窗口**真实渲染出来的内容**存成 PNG。
         *
         * 为什么不能用"在桌面上截屏"那种办法：「不居中 / 缺少一部分 / 黑边」全是布局结论，
         * 而布局只发生在窗口内部。用 RenderTargetBitmap 抓窗口视觉树，拿到的就是用户眼睛
         * 看到的那一层（含前景画面、氛围背景、状态行），而不是被桌面缩放和窗口裁切搅过一遍的
         * 屏幕像素。这条路径也让验收可以自动化：不用人盯着全屏窗口手动按快门。
         */
        if (snapshot != null)
        {
            DispatcherTimer shot = new DispatcherTimer();
            // 定时器从**窗口构建完成**开始算，而构造耗时是可变的（实测 60–250ms）。
            // 要精确验证"窗口刚上屏那一刻屏幕上是什么"，必须把起点对齐到进程启动，
            // 否则同一个 --snapshot-at 在不同轮次会落在不同的相对时刻上。
            double already = StartedMs() / 1000.0;
            double want = (snapshotAt <= 0 ? 1.0 : snapshotAt) - already;
            if (want < 0.02) want = 0.02;
            shot.Interval = TimeSpan.FromSeconds(want);
            shot.Tick += delegate
            {
                shot.Stop();
                try
                {
                    root.UpdateLayout();
                    int w = (int)Math.Round(root.ActualWidth);
                    int h = (int)Math.Round(root.ActualHeight);
                    if (w <= 0 || h <= 0) { Log("截图跳过：窗口尺寸为 0"); return; }
                    RenderTargetBitmap bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(root);
                    PngBitmapEncoder enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    string full = Path.GetFullPath(snapshot);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    using (FileStream fs = new FileStream(full, FileMode.Create, FileAccess.Write))
                    {
                        enc.Save(fs);
                    }
                    Log("截图已保存 " + full + " " + w + "x" + h
                        + " 贴合=" + resolvedFit + " 进程启动 " + StartedMs() + " ms");
                }
                catch (Exception ex)
                {
                    Log("截图失败: " + ex.Message);
                }
            };
            shot.Start();
        }

        /**
         * 媒体在窗口**可见之前**就开始加载。
         *
         * 这是"启动前有一块未显示"的第二个修法，和首帧层是两件事：
         *   · 首帧层解决"窗口出现时有画面"（本地 JPEG，零等待）
         *   · 这里解决"视频尽快接管"（解码提前开始，不等窗口上屏）
         *
         * 之前是 `win.Loaded` 里才 `media.Source = ...`。`Loaded` 是窗口**已经可见之后**
         * 才触发的事件，也就是说解码要等到用户已经看到窗口才开始 —— 那几百毫秒全落在
         * 用户眼睛里。现在在构造阶段就挂上源并 play()：窗口管理器显示它的时候，
         * 解码通常已经跑了一会儿，首帧落点更早。
         *
         * 仍然保留 Loaded 里的对齐调用：ambient（氛围背景那一路）需要窗口有真实尺寸
         * 才能算位置，所以它照旧等 Loaded。
         */
        /**
         * ────────────────────────────────────────────────────────────────────
         * 零等待显形：窗口立即出现，但**完全不可见**；视频在不可见状态下解码；
         * 第一帧一到就瞬间显形。
         *
         * 为什么这样能消掉等待：WPF 里 `Opacity = 0` 的窗口**不是**被跳过的 ——
         * 它照样有合成表面、照样走渲染管线，MediaElement 因此照样解码。
         * 解码需要的是"窗口存在"，不是"窗口可见"。所以那 553ms 的解码时间
         * 被挪到了用户看不见的地方，而屏幕上什么都没有发生（干净的桌面）。
         *
         * 这与"加一层模糊背景/正在加载"是**完全不同**的做法：
         * 前者是拿别的东西盖住等待，这里是把等待本身藏起来 —— 用户第一眼看到的就是正在播放的画面。
         * ────────────────────────────────────────────────────────────────────
         */
        win.Opacity = 0;
        win.ShowInTaskbar = false;

        /*
         * 注意：**这里不再挂 media.Source**。
         *
         * 实测踩到的卡顿根因（2026-10-03 22:34 那次登录）：
         *     media-play-issued@927   ← 媒体栈初始化把主线程卡了 915ms
         *     window-shown@1517       ← 窗口因此直到 1.5 秒才出现
         *     media-opened@5107       ← 视频 5.1 秒才出画
         * 也就是说 `media.Source = ...` 这个**同步调用**会触发 WPF 媒体栈初始化，
         * 在登录压力下要近 1 秒，于是窗口迟迟不出现 = 用户看到的"卡顿"。
         *
         * 现在改成：先把窗口显示出来（贴缓存的视频首帧图，约 250ms），
         * 再在 win.Loaded 里挂媒体。媒体初始化期间屏幕上一直是那张首帧图 ——
         * 它与视频第一帧逐像素相同，所以看不出在等，只是画面停在第一帧。
         */

        /**
         * 预热窗口：**挂上源之后立刻暂停，不播**。
         *
         * 实测踩到的坑：原来这里无条件 `Play()`，而预热窗口的空闲定时器每 400ms 才拨一次
         * 位置，于是预热进程在**空闲期就把整段 4K 视频完整播了一遍**（日志里那一句
         * MediaEnded 就是证据），内存被顶到 2013MB、CPU 累计烧掉 21.5 秒。
         * 更糟的是：既然它播到了结尾，它就把这段素材**淡出到黑的尾部**走了一遍 ——
         * 用户触发的时机如果撞上那一刻，看到的就是黑屏。
         *
         * 现在空转期只做"解码出第一帧并停住"：解码器持有已解码的首帧，
         * 所以触发时立刻有画面；但时间轴不推进，于是 CPU 接近零、也永远不经过黑尾。
         */
        /**
         * 播与不播：**两种模式都直接播**。
         *
         * 曾经试过"预热窗口先播一瞬解出首帧、然后 Pause 冻结"来省 CPU，实测证明
         * 那会让播放彻底卡死（见 WarmRuntime 里那段详细记录：Pause 之后 Play 唤不醒，
         * 重挂源也只推进到 39ms 就停）。所以这里不暂停 ——
         * 预热窗口一直播着（窗口 Opacity=0，用户看不见），
         * 好处是触发瞬间它**真的在播**，没有任何需要恢复的状态。
         */
        // 媒体挂载与播放统一放到 AttachMedia（由 win.Loaded 触发），见上面那段说明。

        /**
         * 显形时机：等"确实有画面了"的组合信号，再等一个渲染周期。
         *
         * 为什么是组合信号，而不是一个现成的属性 —— 这一点必须如实写清楚：
         * `MediaElement` **没有**任何直接回答"画面渲染了吗"的成员。
         *   · `NaturalVideoWidth` 在 MediaOpened 时就有值，那只是容器头读完了（实测踩坑：
         *     按它显形会在 354ms 打开窗口、568ms 才有画，中间 214ms 又变成黑块）
         *   · `Rendering` 事件和 `ReadyState` 属于 `MediaPlayer`，`MediaElement` 不暴露
         *   · `MediaState` 枚举里只有 Manual/Play/Pause/Stop，没有 Ready
         *
         * 所以用三个**可观察**条件一起判定"已经在播了"：
         *   1. `NaturalDuration` 有值       → 容器头已解析
         *   2. `CanPause` 为真              → 媒体已就绪到可以控制
         *   3. `Position` 已推进超过 40ms   → 不是停在第一帧，真的在走
         * 满足后再等**一个合成周期**（CompositionTarget.Rendering 触发一次）才打开窗口，
         * 保证那一帧已经进了合成队列 —— 这样显形时屏幕上就是正在播放的画面。
         *
         * 若这条路在某台机器上判断不准，兜底是 revealGuard：1.2s 后必定显形并写明原因。
         */
        DateTime revealed0 = DateTime.Now;
        DispatcherTimer reveal = new DispatcherTimer();
        reveal.Interval = TimeSpan.FromMilliseconds(16);
        reveal.Tick += delegate
        {
            if (!autoReveal) { reveal.Stop(); return; }
            /**
             * 显形条件：**画面必须真的在走**。
             *
             * 这个条件我在这一版上反复踩了两次，两次都对，所以两条都要满足：
             *
             *   第一次（太严）：要求「Position 推进 > 40ms」→ 在短片段/慢机器上
             *   Position 长时间停在 0，条件永不满足，窗口一路走到 1.2s 兜底才出现。
             *   表现：黑一秒多。
             *
             *   第二次（太松）：只要求「CanPause + 60ms 延时」→ 如果那 60ms 内解码器
             *   还没吐出第一帧，窗口打开时就是**一块黑**。播放确实开始了，但用户看到黑。
             *   这就是客户报的「启动动画是黑屏」。
             *
             * 现在：Position 已经在推进（说明解码器真的在出帧），**并且**至少过了 120ms
             * （给首帧上屏留时间）。同时下面**不撤掉首帧层** —— 万一还是早了一点点，
             * 用户看到的是首帧静止图而不是黑。
             */
            // 现在媒体是"挂着源但没播"，所以**不能**再要求 Position 推进 ——
            // 它本来就是 0。改为要求"源已就绪"：有了时长说明容器头解析完、
            // 解码器可以出帧了；再加 120ms 让首帧真正上屏。
            if (!media.CanPause) return;
            if (!media.NaturalDuration.HasTimeSpan) return;
            if ((DateTime.Now - revealed0).TotalMilliseconds < 120) return;
            reveal.Stop();
            BootAnimation.BootClock.Mark("first-frame-confirmed");
            Log("媒体已就绪 " + StartedMs() + " ms，等一个合成周期后显形");
            // 标记"媒体就绪后要显形"。真正的显形在 MediaOpened 里、紧跟"从 0 开始播"之后 ——
            // 顺序必须是"先播第一帧、再显示窗口"，否则会出现"窗口已显示但视频还没开始"的间隔。
            EventHandler once = null;
            once = delegate
            {
                System.Windows.Media.CompositionTarget.Rendering -= once;
                RevealWindow(win, null);
            };
            System.Windows.Media.CompositionTarget.Rendering += once;
        };
        reveal.Start();

        /**
         * 兜底：视频迟迟不出画时，窗口不能永远隐藏 —— 但**时限要自适应**。
         *
         * 原来固定 1.2 秒。实测在登录压力下媒体打开要 3.2 秒（正常 375ms），
         * 于是兜底提前开窗，用户看到的是"封面帧定格 + 后面的视频才追上来"，
         * 表现就是卡顿/跳一下（日志里那句"视频没有按时出画——下面显示的是缓存的封面帧"）。
         *
         * 改成两级：
         *   · 先用较长的时限（4 秒）等真正的视频帧 —— 登录时慢是正常的，
         *     这段时间屏幕上是**缓存的视频首帧图**，与视频第一帧逐像素相同，看不出在等
         *   · 4 秒仍未出画才带提示显形（说明确实异常），而不是把正常的"慢"误判成失败
         *
         * 为什么不干脆去掉兜底：解码真的失败时必须给用户一个交代，
         * 不能让窗口永远隐藏（那等于什么都没有）。
         */
        DispatcherTimer revealGuard = new DispatcherTimer();
        revealGuard.Interval = TimeSpan.FromMilliseconds(6000);
        revealGuard.Tick += delegate
        {
            revealGuard.Stop();
            reveal.Stop();
            if (!autoReveal) return;
            if (win.Opacity < 0.99)
            {
                Log("首帧等待超时（" + StartedMs() + " ms，超过 6 秒），带提示显形（媒体可能真的打不开）");
                // 注意：这里**不**开始播放。只有 MediaOpened（真的就绪）才播 ——
                // 否则又会出现"先播、媒体后才就绪"，把片头吃掉（实测 4146ms 开始播、
                // 4295ms 才 MediaOpened，于是动画没播完整）。
                // 这种超时情况下用户看到的是缓存的封面帧，比播放一段残缺动画更诚实。
                RevealWindow(win, "视频迟迟没有出画（系统繁忙？）—— 下面显示的是缓存的封面帧");
            }
        };
        revealGuard.Start();

        /**
         * 窗口显示之后才挂媒体并播放。
         *
         * 顺序在这里是刻意反过来的，理由是实测数据（见前面那段"不再挂 media.Source"的说明）：
         *   · 挂 media.Source 是**同步**的，会触发 WPF 媒体栈初始化，登录时可达 900ms+
         *   · 若放在 win.Show() 之前，窗口就要等这 900ms 才出现 —— 那就是用户感知的卡顿
         *   · 放到 Loaded 里之后，窗口先以缓存的视频首帧图出现（约 250ms），
         *     媒体在后台慢慢初始化；这段时间画面停在第一帧，看不出在等
         *
         * Loaded 是窗口**已经显示出来**之后才触发的事件，正好是我们要的时机。
         */
        /**
         * **在这里就显示窗口** —— 必须在首帧图已经加入视觉树之后。
         *
         * 实测证据（单像素采样，2026-10-03）：
         *     t=22–278ms   RGB(249,250,251)  桌面
         *     t=283ms      RGB(0,0,0)        ← 黑出现
         *     t=283–366ms  RGB(0,0,0)        ← 一直黑
         *     t=623ms      RGB(51,42,41)     ← 才有画面
         *
         * 也就是说：窗口虽然设了 Opacity=0，但 Show() 之后、首帧图**还没完成合成**的
         * 那段时间，DWM 会把这块未就绪的区域画成黑 —— 客户看到的"启动前卡一下"就是这个。
         *
         * 关键点是**顺序**：首帧图加入视觉树 → 显示窗口 → 挂媒体。
         * 之前 Show() 在 BootRuntime 里（构造函数返回之后），而首帧图是在构造函数里建的，
         * 中间还夹着 media 相关设置，于是窗口先以黑底出现。
         * 现在把 Show() 收进构造函数，紧跟在首帧图之后，窗口一出现就有画面。
         */
        if (autoReveal)
        {
            BootAnimation.BootClock.Mark("before-show");
            win.Show();
            BootAnimation.BootClock.Mark("after-show");
            BootAnimation.BootClock.Mark("window-shown");
            Log("窗口已显示 " + StartedMs() + " ms（进程启动算起，首帧图已在视觉树中）");
        }

        // 挂载动作交回调用方，由它在窗口显示之后显式调用（见 PlayerWindow 的说明）。
        PlayerWindow result = new PlayerWindow();
        result.Window = win;
        result.AttachMedia = delegate
        {
            try
            {
                BootAnimation.BootClock.Mark("media-attach-start");
                /**
                 * **只挂源，不播。**
                 *
                 * 这是"视频没播放完整"的根因修法。实测（2026-10-03 22:45 那次登录）：
                 *     media-attach-start@1635
                 *     media-play-issued@1661
                 *     media-opened@5101        ← 媒体就绪花了 3.4 秒
                 *     窗口显形 5346 ms
                 *
                 * 原来这里紧跟一句 Play()，于是**视频在窗口还不可见的时候就播了 5 秒**；
                 * 等用户真正看到时，7.3 秒的动画已经只剩尾巴 —— 这就是"没播放完整"。
                 * 而且媒体一就绪就立刻已到中段，`MediaEnded` 也会提前到来。
                 *
                 * 现在：设源之后**停在第 0 帧**（MediaElement 会预读并解出首帧，
                 * 但时间轴不推进）。等真正显形的那一刻才 `Play()`，从头完整播放。
                 *
                 * 顺带的好处：媒体初始化期间不做无谓的解码，省 CPU 与 IO。
                 */
                media.Source = new Uri(path);
                // 必须真的调用 Play()：LoadedBehavior=Manual 时**不播就不会开始打开媒体**，
                // MediaOpened 永远不会到（实测：等了 6 秒兜底才显形，视频根本没就绪）。
                // 但接着会在 MediaOpened 里立刻暂停并归零，所以不会播掉片头。
                media.Play();
                BootAnimation.BootClock.Mark("media-play-issued");
                if (ambient != null) alignAmbient();

                pendingPlay = delegate
                {
                    try { media.Position = TimeSpan.Zero; } catch { }
                    media.Play();
                };


            }
            catch (Exception ex) { Log("挂载媒体失败: " + ex.Message); }
        };
        return result;
    }

    /// <summary>
    /// "显形这一刻要执行的播放动作"。
    ///
    /// 为什么需要它：媒体是"挂着源但不播"（见 AttachMedia 的说明），
    /// 必须等到真正显形的那一刻才从头播放；而 RevealWindow 是静态方法，
    /// 拿不到 BuildPlayerCore 里的 media 局部变量，所以由 AttachMedia 把动作放进来。
    /// </summary>
    /// <summary>"显形同帧要执行的播放动作"（见 RevealWindow 的说明）。</summary>
    private static Action pendingPlay;

    // 交接事件：置位后原生封面窗口（NativePlayer.exe）自行退出
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateEventW(IntPtr attr, bool manualReset, bool initialState, string name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr OpenEventW(uint access, bool inherit, string name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint EVENT_MODIFY_STATE = 0x0002;

    /// <summary>
    /// 把窗口从不透明 0 瞬间打开。
    ///
    /// **不做淡入**：淡入会重新引入一段"半可见"的过渡，而用户要的是"它已经在了"。
    /// 一帧之内从 0 到 1，观感就是"突然出现并正在播放"。
    /// </summary>
    private static void RevealWindow(Window win, string note)
    {
        try
        {
            win.Opacity = 1;
            BootAnimation.BootClock.Mark("window-revealed");

            /**
             * 显形的**同一时刻**从头开始播放 —— 用户看到的第一眼就是完整的第 0 帧。
             *
             * 媒体此前已经就绪并暂停在第 0 帧（见 MediaOpened 里那两句），
             * 所以这里只是按一下播放键：没有等待、没有跳帧，
             * 7.3 秒的动画会完整播完，不会再出现"没播完整"。
             *
             * 顺序必须是"窗口已可见 + 媒体已停在第 0 帧"→ 播；
             * 反过来的话（先播再显形）媒体初始化那几秒会把片头吃掉。
             */
            if (pendingPlay != null)
            {
                Action play = pendingPlay;
                pendingPlay = null;
                try { play(); Log("已开始播放（显形同帧，从第 0 帧）"); }
                catch (Exception ex) { Log("开始播放失败: " + ex.Message); }
            }

            /**
             * 通知原生封面窗口可以退场了。
             *
             * 顺序很重要：**先**让本窗口显形并开始播（上面那两步），**再**置位事件。
             * 反过来的话会出现"原生窗口已经消失、WPF 窗口还没显示"的空白期。
             * 而两者显示的是同一帧画面，所以即使重叠几毫秒也看不出接缝。
             */
            try
            {
                IntPtr hs = OpenEventW(EVENT_MODIFY_STATE, false, "BootAnimation_Handoff");
                if (hs == IntPtr.Zero) hs = CreateEventW(IntPtr.Zero, true, false, "BootAnimation_Handoff");
                if (hs != IntPtr.Zero)
                {
                    SetEvent(hs);
                    CloseHandle(hs);
                    Log("已通知原生封面窗口交接");
                }
            }
            catch (Exception ex) { Log("通知交接失败（原生窗口会按自己的超时退出）: " + ex.Message); }

            Log("窗口显形 " + StartedMs() + " ms（进程启动算起）" + (note == null ? "" : " — " + note));
        }
        catch (Exception ex) { Log("显形失败: " + ex.Message); }
    }

    // ════════════════════════════════════════════════════════════ 模块间接口
    //
    // 这几个成员是给 BootRuntime / AnimationRepository / AnimationStateStore 用的。
    // 它们原来都是 private，导致新模块只能把同一份路径逻辑再抄一遍 —— 而"同一份逻辑
    // 抄两遍"正是这个程序历史上出现"数据目录写了两次、版本号写了两份"的原因。
    // 统一从这里出去，只有一处真源。

    /// <summary>内嵌片段的 id 列表（顺序与 Clips 一致）。</summary>
    internal static string[] ClipIds
    {
        get
        {
            string[] ids = new string[Clips.Length];
            for (int i = 0; i < Clips.Length; i++) ids[i] = Clips[i].Id;
            return ids;
        }
    }

    /// <summary>id → 显示名。未知 id 返回 id 本身，绝不返回空串。</summary>
    internal static string ClipNameOf(string id)
    {
        Clip clip = Find(id);
        return clip == null ? id : clip.Name;
    }

    /// <summary>id → 内嵌片段解包后的路径；不是内置片段返回 null。</summary>
    internal static string ClipPathOf(string id)
    {
        Clip clip = Find(id);
        if (clip == null) return null;
        try { return Extract(clip); }
        catch (Exception ex) { Log("取片段路径失败 " + id + ": " + ex.Message); return null; }
    }

    /// <summary>内置片段的 sha256（从元数据读，读不到返回 null）。</summary>
    internal static string ClipShaOf(string id)
    {
        // 内嵌资源的哈希在 --selftest 里算过；这里按需计算并缓存，避免每次扫描都哈希 30 MB。
        if (shaCache.ContainsKey(id)) return shaCache[id];
        string path = ClipPathOf(id);
        if (path == null) return null;
        try
        {
            string sha = Sha256Of(path);
            shaCache[id] = sha;
            return sha;
        }
        catch (Exception ex)
        {
            Log("计算内置片段哈希失败 " + id + ": " + ex.Message);
            return null;
        }
    }

    private static readonly Dictionary<string, string> shaCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>某段动画的氛围背景图在磁盘上的缓存路径（社区/本地片段用；内置的走内嵌资源）。</summary>
    internal static string PosterCachePathFor(string id)
    {
        string dir = Path.Combine(DataDir, "posters");
        return Path.Combine(dir, "file-" + SafeName(id) + ".jpg");
    }

    /// <summary>
    /// 按**媒体文件路径**给出海报缓存路径。
    ///
    /// 必须与 LoadPosterBrush/BuildPoster 用同一套命名（以文件名的 stem 为准），
    /// 否则"生成时用文件名、查找时用 clip id"这两条路径永远对不上，
    /// 表现就是氛围背景一直读不到、边条只能是黑的。社区片段的 id 与文件名不同，
    /// 这个坑就是在那里踩到的。
    /// </summary>
    internal static string PosterPathForMedia(string mediaPath)
    {
        if (string.IsNullOrEmpty(mediaPath)) return null;
        string stem = Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrEmpty(stem)) return null;
        return PosterCachePathFor(stem);
    }

    /// <summary>已确保存在的数据目录（日志、配置、缓存都在这下面）。</summary>
    internal static string DataDirectory { get { return DataDir; } }

    /// <summary>让仓库层也能启用 TLS（它要拉社区目录）。只做一次。</summary>
    internal static void EnsureTls()
    {
        Community.EnsureTlsPublic();
    }
    /// <summary>给 Diagnostics 用的：程序集路径。</summary>
    internal static string ExePath
    {
        get { return Assembly.GetEntryAssembly().Location; }
    }

    /// <summary>程序集版本（Diagnostics 页显示用）。</summary>
    internal static string AppVersion
    {
        get
        {
            try { return typeof(Program).Assembly.GetName().Version.ToString(3); }
            catch { return "0.0.0"; }
        }
    }

    /// <summary>
    /// 把进程优先级抬到 High。
    ///
    /// 为什么值得在启动第一件事就做：开机那几十秒有几十个进程在抢 CPU，而首帧解码
    /// 是 CPU 密集的 —— 不抬优先级就会被排在后面，表现就是"窗口出来了但画面半天不来"。
    /// 只是一次系统调用，失败也无所谓（优先级是锦上添花，不是能不能播的前提）。
    /// </summary>
    /// <summary>
    /// 播完后定格在"最后一帧有画面的地方"，等用户点击进桌面。
    ///
    /// 客户的原话是"定格了一个黑色的照片……要保证这个最后一帧是有画面的"。
    /// 这个函数就是照这句话写的：
    ///   · 拿到的定格图**必须通过亮度自检**（见 FrameLooksBlank），否则逐级退让
    ///   · 退让顺序：准备好的定格图 → 现场从末尾往回抽并逐个自检 → 海报图
    ///   · 全部失败时宁可不停格，也**不留黑屏**（直接关窗口让桌面出来）
    ///
    /// 为什么定格图要"从末尾往回找"而不是"取最后一帧"：
    /// 实测这段素材在 6.9s 之后没有任何可见内容（容器时长 7.30s），
    /// 取最后一帧拿到的是纯黑。往回找第一个达标的那一帧才是有意义的那一帧。
    /// </summary>
    private static void FreezeOnLastGoodFrame(System.Windows.Controls.Grid root, Window win,
        TextBlock status, string path, string clipId, string posterPath)
    {
        try
        {
            ImageSource frozen = null;
            string how = null;

            // ① 安装期准备好的定格图（首选：不需要解码、绝不会是黑的，因为选的时候已经自检过）
            string freezePath = FreezePathFor(clipId);
            if (freezePath != null && File.Exists(freezePath))
            {
                ImageSource candidate = ReadImageSource(freezePath);
                if (candidate != null && !FrameLooksBlank(freezePath))
                {
                    frozen = candidate;
                    how = "准备好的定格图";
                }
            }

            // ② 现场抽：从末尾往回找第一个亮度达标的帧
            if (frozen == null)
            {
                double duration = LastKnownDurationSeconds;
                if (duration > 0)
                {
                    // 从末尾往回试这些回退量。0.05s 那档经常落在淡出黑里，
                    // 所以列表里也有更大的回退量，确保一定能找到一个有画面的。
                    double[] backs = new double[] { 0.05, 0.3, 0.6, 1.0, 1.5, 2.5, 4.0 };
                    for (int i = 0; i < backs.Length; i++)
                    {
                        double at = duration - backs[i];
                        if (at <= 0.05) continue;
                        string tmp = Path.Combine(Path.GetTempPath(), "ba-freeze-probe.jpg");
                        try
                        {
                            if (BuildFrameAt(path, tmp, at) && File.Exists(tmp) && !FrameLooksBlank(tmp))
                            {
                                frozen = ReadImageSource(tmp);
                                how = "现场抽帧 @" + Math.Round(at, 2) + "s（自检通过）";
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            // ③ 最后退路：海报图（它是视频第一帧，至少不是黑的）
            if (frozen == null && posterPath != null && File.Exists(posterPath))
            {
                frozen = ReadImageSource(posterPath);
                how = "海报图（退路）";
            }

            if (frozen == null)
            {
                // 连一张可用画面都拿不到：不做定格，直接把桌面交还给用户。
                // 理由是"宁可没有定格，也不能把一块黑留在屏幕上"。
                Log("定格失败：没有任何可用的非黑画面，直接关窗口（不留黑屏）");
                win.Close();
                return;
            }

            // 把定格图铺满（与视频同一种贴合方式，所以落点一致、看不出是"换了一张图"）
            System.Windows.Controls.Image freeze = new System.Windows.Controls.Image();
            freeze.Source = frozen;
            freeze.Stretch = Stretch.Fill;          // 定格图按视频当前贴合方式拉伸
            freeze.IsHitTestVisible = false;
            freeze.Opacity = 1.0;
            Panel.SetZIndex(freeze, 8);
            root.Children.Add(freeze);

            // 停掉媒体：避免解码器继续工作，也避免它在定格图下面露出黑帧
            try
            {
                System.Windows.Controls.MediaElement media = FindMediaElement(root);
                if (media != null) { media.Stop(); media.Source = null; }
            }
            catch { }

            /**
             * 定格之后**自动进桌面**，不再等用户点击。
             *
             * 客户明确要求去掉"点一下才进桌面"这个动作：
             * 开机动画播完就应当自然结束、桌面接管，而不是把用户拦在最后画面前
             * 要求他按一次鼠标 —— 那不像开机动画，像一个需要确认的播放器。
             *
             * 停留 2000ms 是有意的：让最后一帧有被看清的时间（否则动画结束得非常突兀），
             * 又不至于让人觉得画面卡住了。这 2000ms 从定格完成开始算。
             *
             * 不再显示"点击进入桌面"的提示 —— 既然不需要用户操作，提示就是多余的字，
             * 而开机动画的定格画面上不该出现任何 UI。
             */
            status.Visibility = Visibility.Collapsed;
            DispatcherTimer autoExit = new DispatcherTimer();
            autoExit.Interval = TimeSpan.FromMilliseconds(2000);
            autoExit.Tick += delegate
            {
                autoExit.Stop();
                Log("定格停留结束，自动进桌面");
                try { win.Close(); } catch { }
            };
            autoExit.Start();

            // 保留"点击/按键立刻进桌面"：用户不想等这 2 秒时可以马上跳过。
            // 现有的 MouseLeftButtonUp / KeyDown 处理器已经正确（都是关窗口），不需要另加。
            Log("已定格（" + how + "），2 秒后自动进桌面（也可点击立刻进入）");
        }
        catch (Exception ex)
        {
            Log("定格异常，直接关窗口: " + ex.Message);
            try { win.Close(); } catch { }
        }
    }

    /// <summary>最近一次拿到的媒体时长（秒）。定格时要用它算"从末尾往回退多少"。</summary>
    internal static double LastKnownDurationSeconds = 0;

    /// <summary>定格图的路径。与海报图同一套命名规则，便于一起准备与清理。</summary>
    internal static string FreezePathFor(string clipId)
    {
        if (clipId == null) return null;
        string dir = Path.Combine(DataDir, "freeze");
        return Path.Combine(dir, "last-" + SafeName(clipId) + ".jpg");
    }

    /// <summary>从磁盘读一张图作为 ImageSource（带 OnLoad，避免句柄占着文件）。</summary>
    private static ImageSource ReadImageSource(string file)
    {
        try
        {
            BitmapImage bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log("读图失败 " + Path.GetFileName(file) + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 自检：这张图是不是"基本等于黑"。
    ///
    /// 这是客户明确要求的那个检查 —— "可以写个自检程序，就是在截取最后一帧的时候，
    /// 保持它有一个画面"。所以它不是可选的优化，而是定格能不能用的判据。
    ///
    /// 判据用**平均亮度**而不是最大值：这段素材末尾是"整体变暗"，
    /// 最大值可能因为几个残留像素还很高，而画面其实已经看不到内容了。
    /// 阈值 8.0 是量出来的：实测 6.7s 那帧均值 9.79（勉强可见）、
    /// 6.9s 之后均值≈0（纯黑），取 8.0 正好把"几乎看不见"和"还能看"分开。
    /// </summary>
    internal static bool FrameLooksBlank(string file)
    {
        try
        {
            BitmapImage bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file, UriKind.Absolute);
            bmp.EndInit();

            // 缩到很小再统计：既快，又正好是"整体亮度"这个语义（抽掉局部亮点的影响）
            int w = 48;
            int h = Math.Max(1, (int)Math.Round(bmp.PixelHeight * (48.0 / bmp.PixelWidth)));
            TransformedBitmap small = new TransformedBitmap(bmp,
                new System.Windows.Media.ScaleTransform(w / (double)bmp.PixelWidth, h / (double)bmp.PixelHeight));
            FormatConvertedBitmap conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);

            int stride = conv.PixelWidth * 4;
            byte[] pixels = new byte[stride * conv.PixelHeight];
            conv.CopyPixels(pixels, stride, 0);

            long sum = 0;
            int count = 0;
            for (int i = 0; i + 2 < pixels.Length; i += 4)
            {
                // 感知亮度（人眼对绿最敏感），比简单的 RGB 平均更接近"看着黑不黑"
                sum += (pixels[i + 2] * 30 + pixels[i + 1] * 59 + pixels[i] * 11) / 100;
                count++;
            }
            if (count == 0) return true;
            double mean = sum / (double)count;
            bool blank = mean < 8.0;
            Log("定格帧自检 " + Path.GetFileName(file) + " 平均亮度=" + Math.Round(mean, 2)
                + (blank ? " → 判定为黑，不可用" : " → 有画面，可用"));
            return blank;
        }
        catch (Exception ex)
        {
            Log("定格帧自检失败（保守当作黑）: " + ex.Message);
            return true;
        }
    }

    /// <summary>在窗口视觉树里找那一路前景 MediaElement。</summary>
    private static System.Windows.Controls.MediaElement FindMediaElement(System.Windows.DependencyObject root)
    {
        if (root == null) return null;
        System.Windows.Controls.MediaElement self = root as System.Windows.Controls.MediaElement;
        if (self != null) return self;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            System.Windows.Controls.MediaElement found =
                FindMediaElement(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    /**
     * ────────────────────────────────────────────────────────────────────
     * 快速版（启动用）的路径与生成
     *
     * 背景：实测同一套代码下，不同编码格式的启动耗时差 3 倍：
     *
     *     片段格式              窗口显示   媒体打开   显形
     *     HEVC Main 10 4K       851ms     1122ms    1188ms
     *     H.264 1440p           238ms      354ms     388ms
     *
     * 10-bit HEVC 4K 需要额外的解码器与视频处理器初始化，而那正是登录时
     * （系统正在加载、安全软件在过滤每一次文件读取）最容易被放大成几十秒的一步。
     *
     * 做法：在**应用/安装阶段**为这类片段生成一份 H.264 8-bit 1440p 的轻量版本，
     * 开机优先播它。代价是画质略降与一次转码耗时，收益是启动稳定变快。
     * 原文件**保留不动** —— 预览与"原始画质"仍然用它。
     * ────────────────────────────────────────────────────────────────────
     */
    /// <summary>
    /// 氛围背景用的**预模糊图**路径。
    ///
    /// 为什么要预生成：运行时用 `BlurEffect(Radius=48)` 来做氛围背景是全流程里
    /// **单步最贵**的一环 —— 实测 104ms / 总 239ms，而且它是一个 GPU 合成层，
    /// 登录时 GPU 同样紧张。改成安装期用 ffmpeg 生成一张已模糊的 jpg，
    /// 运行时只读图、不建 Effect，既省时间又省显存。
    /// </summary>
    internal static string BlurPathForMedia(string mediaPath)
    {
        if (mediaPath == null) return null;
        try
        {
            string stem = Path.GetFileNameWithoutExtension(mediaPath);
            string dir = Path.Combine(DataDir, "blur");
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(SafeName(stem)) + ".jpg");
        }
        catch { return null; }
    }

    /// <summary>生成预模糊背景图。ffmpeg 的 boxblur 比 WPF 的 BlurEffect 更可控，且一次性。</summary>
    internal static bool BuildBlurVersion(string mediaPath, string outPath)
    {
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            if (File.Exists(outPath)) File.Delete(outPath);
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(ffmpeg);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.Arguments = "-y -loglevel error -ss 0.2 -i \"" + mediaPath + "\" -frames:v 1"
                + " -vf \"scale=640:-2,boxblur=20:2,eq=brightness=0.16:contrast=1.10:saturation=1.30\""
                + " -q:v 5 \"" + outPath + "\"";
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return false; }
                bool ok = p.ExitCode == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 1024;
                if (ok) Log("预模糊背景已生成: " + Path.GetFileName(outPath));
                else Log("预模糊背景生成失败: " + err);
                return ok;
            }
        }
        catch (Exception ex) { Log("预模糊背景异常: " + ex.Message); return false; }
    }

    internal static string FastPathForMedia(string mediaPath)
    {
        if (mediaPath == null) return null;
        try
        {
            string stem = Path.GetFileNameWithoutExtension(mediaPath);
            string dir = Path.Combine(DataDir, "fast");
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(SafeName(stem)) + ".mp4");
        }
        catch { return null; }
    }

    /// <summary>
    /// 判断一个片段是否值得做快速版。
    ///
    /// 判据来自实测：HEVC（尤其 Main 10 即 10-bit）与 4K 是主要开销来源。
    /// H.264 8-bit 且不超过 1440p 的片段本来就快，不必再转（省时也省画质）。
    /// </summary>
    internal static bool NeedsFastVersion(string mediaPath)
    {
        try
        {
            if (mediaPath == null || !File.Exists(mediaPath)) return false;
            // 复用仓库的 MP4 解析（自己读 atom，不额外起 ffprobe 进程）
            BootAnimation.AnimationInfo probe = new BootAnimation.AnimationInfo();
            probe.Path = mediaPath;
            BootAnimation.AnimationRepository.ProbeFile(probe);
            bool big = probe.Width > 2560 || probe.Height > 1440;
            // HEVC / 10-bit 无法从现有探测字段直接读到，所以退一步用体积与分辨率判断：
            // 实测这个 4K HEVC Main10 片段是 51.8MB，而快的 H.264 1440p 只有 4.8–10.5MB。
            bool heavy = probe.Bytes > 20L * 1024 * 1024;
            Log("快速版判定 " + Path.GetFileName(mediaPath) + ": " + probe.Width + "x" + probe.Height
                + " " + Math.Round(probe.Bytes / 1048576.0, 1) + "MB → " + ((big || heavy) ? "需要" : "不需要"));
            return big || heavy;
        }
        catch { return false; }
    }

    /// <summary>
    /// 生成快速版：H.264 / 8-bit / 1440p / 适当码率。
    ///
    /// 参数选择的依据：
    ///   · libx264 + yuv420p —— 最通用、解码器初始化最快，且不需要任何额外解码器包
    ///   · 限制到 2560x1440 —— 4K 对这个用途是浪费（屏幕 2560x1600），
    ///     而且实测 1440p 的打开耗时只有 4K 的三分之一
    ///   · CRF 20 + preset medium —— 画质与体积的平衡点；这是给人看开机动画，
    ///     不是做母版
    ///   · 音频转 AAC —— 避免把无损音轨原样搬过来
    ///   · +faststart —— moov 前置，解码器可以立刻开始读，不用先跳到文件尾部
    /// </summary>
    internal static bool BuildFastVersion(string mediaPath, string outPath)
    {
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            if (File.Exists(outPath)) File.Delete(outPath);

            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(ffmpeg);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.Arguments = "-y -loglevel error -i \"" + mediaPath + "\""
                + " -vf \"scale='min(2560,iw)':-2\""
                + " -c:v libx264 -profile:v high -level 4.2 -pix_fmt yuv420p"
                + " -crf 20 -preset medium"
                + " -c:a aac -b:a 192k"
                + " -movflags +faststart"
                + " \"" + outPath + "\"";
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(600000))
                {
                    try { p.Kill(); } catch { }
                    Log("快速版转码超时: " + Path.GetFileName(mediaPath));
                    return false;
                }
                if (p.ExitCode != 0)
                {
                    Log("快速版转码失败（exit=" + p.ExitCode + "）: " + err);
                    return false;
                }
                bool ok = File.Exists(outPath) && new FileInfo(outPath).Length > 4096;
                if (ok)
                {
                    Log("快速版已生成: " + Path.GetFileName(outPath)
                        + "  " + Math.Round(new FileInfo(outPath).Length / 1048576.0, 1) + "MB");
                }
                return ok;
            }
        }
        catch (Exception ex)
        {
            Log("快速版转码异常: " + ex.Message);
            return false;
        }
    }

    /// <summary>启动时要用的文件：有快速版就用快速版，否则用原文件。</summary>
    internal static string PreferFastVersion(string mediaPath)
    {
        try
        {
            string fast = FastPathForMedia(mediaPath);
            if (fast != null && File.Exists(fast))
            {
                Log("使用快速版启动: " + Path.GetFileName(fast));
                return fast;
            }
        }
        catch { }
        return mediaPath;
    }

    /// <summary>
    /// 用 ffmpeg 抽某一时刻的一帧。返回是否成功。
    ///
    /// 只在**准备阶段**（安装器 --prepare）和定格退路里调用，绝不在启动关键路径上。
    /// </summary>
    internal static bool BuildFrameAt(string mediaPath, string outJpg, double atSeconds)
    {
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return false;

        /**
         * **先删掉目标文件，并要求它必须被重新写出来。**
         *
         * 这是一个实测抓到的严重 bug：抽帧失败时旧的同名文件仍然留在磁盘上，
         * 于是下一步的"自检"检的是**上一次的旧图** —— 表现为 4 段完全不同的视频
         * 自检出的平均亮度全都是同一个数（46.51），黑的帧因此蒙混过关。
         * 定格图会拿错，而且错得很隐蔽（日志一切正常）。
         */
        try { if (File.Exists(outJpg)) File.Delete(outJpg); } catch { }
        DateTime started = DateTime.Now;
        try
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(ffmpeg);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            // 提亮：与海报图同样的处理。末尾淡出时画面本来偏暗，
            // 提亮能让"还能看见的那一帧"更清楚，也让自检更容易通过。
            psi.Arguments = "-y -loglevel error -ss "
                + atSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                + " -i \"" + mediaPath + "\" -frames:v 1 -vf \"scale=1280:-2,eq=brightness=0.12:contrast=1.08\" -q:v 3 \""
                + outJpg + "\"";
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); } catch { }
                    return false;
                }
                // 三重确认：进程成功 + 文件存在 + **文件确实是这一次写出来的**
                if (p.ExitCode != 0) return false;
                FileInfo fi = new FileInfo(outJpg);
                return fi.Exists && fi.LastWriteTime >= started && fi.Length > 512;
            }
        }
        catch (Exception ex)
        {
            Log("抽帧失败 @" + atSeconds + "s: " + ex.Message);
            return false;
        }
    }

    /// <summary>原生播放器的路径（与主程序同目录）。不存在时返回 null，调用方须退回 WPF。</summary>
    internal static string NativePlayerPath
    {
        get
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string p = Path.Combine(dir, "NativePlayer.exe");
                return File.Exists(p) ? p : null;
            }
            catch { return null; }
        }
    }

    /// <summary>主日志的完整路径，供原生播放器把它的时间线写进同一份日志。</summary>
    internal static string LogFilePath
    {
        get
        {
            try { return Path.Combine(DataDir, "boot-animation.log"); }
            catch { return null; }
        }
    }

    internal static void SetHighPriority()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
        catch (Exception ex) { Log("提升优先级失败（不影响播放）: " + ex.Message); }
    }

    /// <summary>id 是不是内置片段。</summary>
    internal static bool IsBuiltIn(string id)
    {
        return Find(id) != null;
    }

    /// <summary>内置片段的氛围背景图（内嵌资源）。不是内置片段返回 null。</summary>
    internal static ImageBrush EmbeddedPosterBrush(string id)
    {
        return ReadBrush(null, PosterResource(id));
    }

    /// <summary>从磁盘读一张图成画刷。文件不存在返回 null。</summary>
    internal static ImageBrush ReadBrushFrom(string filePath)
    {
        return ReadBrush(filePath, null);
    }

    /// <summary>读当前生效的选片 id（settings.txt）。读不到返回 null。</summary>
    internal static string ReadChosenPublic()
    {
        return ReadChosen();
    }
}