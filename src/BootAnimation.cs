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

    private static string ReadChosen()
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
        string want = null;      // --clip <id>
        string file = null;      // --file <path>
        double seconds = 0;      // --seconds <n> 到点自动关
        double delay = 0;        // --delay <n> 显示前先等几秒，让桌面先铺好
        string fit = "cover";    // --fit cover|contain
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

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--selftest") return SelfTest();
                else if (a == "--manager") return BootAnimation.Shell.Run();
            else if (a == "--uninstall") uninstall = true;
            else if (a == "--silent") silent = true;
            else if (a == "--list") list = true;
            else if (a == "--choose") choose = true;
            else if (a == "--play") play = true;
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

        Log("启动 args=" + string.Join(" ", args));

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

        // 无参数启动 = 选片窗口：客户第一次用、或想换片时就是这个入口
        if (!choose && !play && want == null && file == null) choose = true;

        if (choose)
        {
            Application picker = new Application();
            Window dialog = BuildPicker(picker);
            picker.Run(dialog);
            // 选完立刻播一次，客户能马上看到效果
            string picked = ReadChosen();
            if (picked == null) return 0;
            want = picked;
        }

        string path = null;
        string label = null;
        if (file != null)
        {
            path = file;
            label = Path.GetFileName(file);
        }
        else
        {
            // 选中的可能是社区片段（id 形如 owner__slug），也可能是内置片段
            string chosen = want == null ? ReadChosen() : want;
            if (Community.LooksLikeCommunityId(chosen))
            {
                CommunityEntry ce = Community.Find(chosen);
                if (ce != null)
                {
                    path = ce.Path;
                    label = ce.Display;
                }
            }

            if (path == null)
            {
                Clip clip = Find(chosen);
                if (clip == null) clip = Clips[0];
                label = clip.Name;
                try
                {
                    path = Extract(clip);
                }
                catch (Exception ex)
                {
                    Log("解包失败: " + ex.Message);
                    return 3;
                }
            }
        }

        if (path == null || !File.Exists(path))
        {
            Log("片段文件不存在: " + path);
            return 4;
        }

        // 登录时桌面还在铺：等一小会儿再抢屏幕，比一开机就盖上去自然
        if (delay > 0)
        {
            Log("延迟 " + delay.ToString(CultureInfo.InvariantCulture) + " 秒后显示");
            System.Threading.Thread.Sleep((int)(delay * 1000));
        }

        Program.started = System.Diagnostics.Stopwatch.StartNew();
        Application app = new Application();
        Window window = BuildPlayer(label, path, topmost, seconds, fit, mute, volume);
        window.Show();
        app.Run();
        Log("退出，进程存活 " + started.ElapsedMilliseconds + " ms");
        return 0;
    }

    /// 进程启动到出画的时间；用来量「开机动画到底卡在哪一段」。
    private static System.Diagnostics.Stopwatch started = System.Diagnostics.Stopwatch.StartNew();

    internal static long StartedMs()
    {
        return started.ElapsedMilliseconds;
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

    /// 播放窗口：无边框、铺满、置顶，画面没出来之前显示状态文字。
    private static Window BuildPlayer(string label, string path, bool topmost, double seconds, string fit, bool mute, int volume)
    {
        Window win = new Window();
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

        MediaElement media = new MediaElement();
        media.Stretch = string.Equals(fit, "contain", StringComparison.OrdinalIgnoreCase)
            ? Stretch.Uniform      // 完整显示，四周留黑边
            : Stretch.UniformToFill; // 铺满，允许裁边（默认，和插件的 cover 一致）
        media.LoadedBehavior = MediaState.Manual;
        media.UnloadedBehavior = MediaState.Manual;
        // 素材自带音轨（即梦AI 生成时就带声音），默认放出来。
        // 不想让开机时出声：加 --mute，或用 --volume 0~100 调音量。
        media.Volume = mute ? 0 : volume / 100.0;
        media.IsMuted = mute;
        root.Children.Add(media);

        TextBlock status = new TextBlock();
        status.Text = "正在加载：" + label;
        status.Foreground = Brushes.White;
        status.FontSize = 14;
        status.HorizontalAlignment = HorizontalAlignment.Center;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextAlignment = TextAlignment.Center;
        root.Children.Add(status);

        win.Content = root;

        media.MediaOpened += delegate
        {
            Log("MediaOpened " + media.NaturalVideoWidth + "x" + media.NaturalVideoHeight
                + " duration=" + media.NaturalDuration
                + " 进程启动到出画 " + StartedMs() + " ms"
                // 声音出不来时，这三个值能直接说清是「没有音轨」「被静音」还是「音量是 0」
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
        media.MediaEnded += delegate
        {
            Log("MediaEnded");
            win.Close();
        };

        win.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) win.Close(); };
        win.MouseLeftButtonUp += delegate { win.Close(); };

        // 打不开也要有交代：超时就说明白，而不是停在黑屏上。
        DispatcherTimer guard = new DispatcherTimer();
        guard.Interval = TimeSpan.FromSeconds(20);
        guard.Tick += delegate
        {
            guard.Stop();
            Log("打不开视频，20 秒超时: " + path);
            status.Visibility = Visibility.Visible;
            status.Text = "视频打不开（20 秒无响应）" + Environment.NewLine + "按 Esc 关闭";
        };
        guard.Start();
        media.MediaOpened += delegate { guard.Stop(); };

        if (seconds > 0)
        {
            DispatcherTimer auto = new DispatcherTimer();
            auto.Interval = TimeSpan.FromSeconds(seconds);
            auto.Tick += delegate { auto.Stop(); Log("到达 --seconds 自动关闭"); win.Close(); };
            auto.Start();
        }

        win.Loaded += delegate
        {
            media.Source = new Uri(path);
            media.Play();
        };
        return win;
    }
}
