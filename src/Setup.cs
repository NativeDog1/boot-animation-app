// Setup —— 「开机动画」的安装程序
//
// 编译（同样用 Windows 自带的 csc，见 build.ps1）：
//   csc /target:winexe /win32icon:build\app.ico
//       /resource:BootAnimation.exe,BootAnimation.payload.exe
//       /r:<框架>\WPF\... src\Setup.cs src\SetupInfo.cs
//
// 为什么自己写安装程序而不是用 Inno Setup / NSIS：
//   1. 那两个都要先装（Inno 还需要管理员权限，会弹 UAC）
//   2. 客户最需要的「当场选一段片头」在通用向导里做不出来
//   3. 全程零依赖，和播放器用的是同一套编译方式
//
// 客户拿到的是**一个 exe**：双击 → 选片头 → 安装 → 完成。
// 卸载入口会注册到「设置 → 应用 → 已安装的应用」，走的是每用户安装，不需要管理员。
//
// 必须写成 C# 5 语法（自带 csc 只认到 C# 5）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

internal sealed class Choice
{
    public readonly string Id;
    public readonly string Name;
    public readonly string Size;

    public Choice(string id, string name, string size)
    {
        Id = id;
        Name = name;
        Size = size;
    }
}

internal static class Setup
{
    private const string Version = "1.1.0";
    private const string PayloadResource = "BootAnimation.payload.exe";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "BootAnimation";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BootAnimation";

    private static readonly Choice[] Clips = new Choice[]
    {
        new Choice("brand", "DeepSeek 品牌片头", "1.25 MB"),
        new Choice("cyberpunk", "DeepSeek 赛博朋克片头", "1.77 MB"),
        new Choice("awakening", "DeepSeek 数字角色苏醒", "2.48 MB"),
        new Choice("startup", "DeepSeek 启动问题", "3.15 MB"),
    };

    private static string DataDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BootAnimation");
        }
    }

    private static string InstallDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "BootAnimation");
        }
    }

    private static string TargetExe
    {
        get { return Path.Combine(InstallDir, "BootAnimation.exe"); }
    }

    private static string StartMenuLink
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "开机动画.lnk");
        }
    }

    /// 桌面快捷方式。路径在这里重复写一遍是有原因的：Setup.exe 与 BootAnimation.exe 是两次
    /// 独立编译，拿不到对方 internal 的常量。两处必须一起改。
    private static string DesktopLink
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "开机动画.lnk");
        }
    }

    private static string LogPath
    {
        get { return Path.Combine(DataDir, "setup.log"); }
    }

    private static void Log(string message)
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
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        bool silent = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "/SILENT" || args[i] == "/VERYSILENT" || args[i] == "--silent") silent = true;
        }
        Log("setup 启动 args=" + string.Join(" ", args) + " silent=" + silent);

        if (silent)
        {
            string error;
            if (Install("brand", true, false, false, true, out error)) return 0;
            Log("静默安装失败: " + error);
            return 1;
        }

        Application app = new Application();
        Window window = BuildWindow(app);
        app.Run(window);
        return 0;
    }

    private static Window BuildWindow(Application app)
    {
        Window win = new Window();
        win.Title = "安装 开机动画";
        win.Width = 520;
        win.Height = 460;
        win.ResizeMode = ResizeMode.NoResize;
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.Background = Brushes.White;

        StackPanel panel = new StackPanel();
        panel.Margin = new Thickness(22, 18, 22, 18);

        TextBlock title = new TextBlock();
        title.Text = "开机动画";
        title.FontSize = 22;
        title.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(title);

        TextBlock sub = new TextBlock();
        sub.Text = "登录 Windows 后立刻全屏播放一段片头动画，播完自动消失。版本 " + Version;
        sub.Foreground = Brushes.Gray;
        sub.Margin = new Thickness(0, 4, 0, 14);
        panel.Children.Add(sub);

        TextBlock pick = new TextBlock();
        pick.Text = "开机时播放哪一段？（视频已内嵌在程序里，不用另外准备文件）";
        pick.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(pick);

        ListBox list = new ListBox();
        list.Height = 130;
        for (int i = 0; i < Clips.Length; i++)
        {
            ListBoxItem item = new ListBoxItem();
            item.Content = Clips[i].Name + "    （" + Clips[i].Size + "）";
            item.Tag = Clips[i].Id;
            list.Items.Add(item);
        }
        list.SelectedIndex = 0;
        panel.Children.Add(list);

        CheckBox autoStart = new CheckBox();
        autoStart.Content = "开机时自动播放（写入当前用户的自启项，随时可在任务管理器里关掉）";
        autoStart.IsChecked = true;
        autoStart.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(autoStart);

        CheckBox playNow = new CheckBox();
        playNow.Content = "安装完成后立即播放一次看看";
        playNow.IsChecked = true;
        playNow.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(playNow);

        CheckBox muteCheck = new CheckBox();
        muteCheck.Content = "开机时静音（片头自带音效，勾上则只播画面不出声）";
        muteCheck.IsChecked = false;
        muteCheck.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(muteCheck);

        TextBlock where = new TextBlock();
        where.Text = "安装位置：" + InstallDir + "（每用户安装，不需要管理员权限）"
            + Environment.NewLine + "已在桌面和开始菜单创建「开机动画」快捷方式，卸载时会一并删除。";
        where.Foreground = Brushes.Gray;
        where.TextWrapping = TextWrapping.Wrap;
        where.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(where);

        TextBlock status = new TextBlock();
        status.Text = "";
        status.Margin = new Thickness(0, 12, 0, 0);
        status.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(status);

        StackPanel buttons = new StackPanel();
        buttons.Orientation = Orientation.Horizontal;
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 14, 0, 0);

        Button install = new Button();
        install.Content = "安装";
        install.Width = 110;
        install.Height = 32;
        install.IsDefault = true;
        install.Click += delegate
        {
            install.IsEnabled = false;
            ListBoxItem sel = list.SelectedItem as ListBoxItem;
            string clip = sel == null ? "brand" : Convert.ToString(sel.Tag, CultureInfo.InvariantCulture);
            bool auto = autoStart.IsChecked == true;
            bool now = playNow.IsChecked == true;
            bool mute = muteCheck.IsChecked == true;
            status.Foreground = Brushes.Black;
            status.Text = "正在安装…";
            string error;
            bool ok = Install(clip, auto, now, mute, false, out error);
            if (ok)
            {
                status.Foreground = Brushes.Green;
                status.Text = "安装完成。开机就会播放你选的那一段。";
                install.Content = "完成";
                install.IsEnabled = true;
                install.Click += delegate { win.Close(); };
            }
            else
            {
                status.Foreground = Brushes.Firebrick;
                status.Text = "安装失败：" + error;
                install.IsEnabled = true;
            }
        };
        buttons.Children.Add(install);

        Button cancel = new Button();
        cancel.Content = "取消";
        cancel.Width = 110;
        cancel.Height = 32;
        cancel.Margin = new Thickness(10, 0, 0, 0);
        cancel.IsCancel = true;
        cancel.Click += delegate { win.Close(); };
        buttons.Children.Add(cancel);

        panel.Children.Add(buttons);
        win.Content = panel;
        return win;
    }

    /// 真正的安装动作。返回 false 时 error 里是给人看的原因。
    private static bool Install(string clip, bool autoStart, bool playNow, bool mute, bool keepExistingClip, out string error)
    {
        error = null;
        try
        {
            // 正在播的时候 exe 被占用，复制会失败 —— 先把它关掉
            foreach (Process running in Process.GetProcessesByName("BootAnimation"))
            {
                try { running.Kill(); Log("关掉正在运行的实例 pid=" + running.Id); }
                catch { }
            }

            // 1. 释放内嵌的播放器
            Directory.CreateDirectory(InstallDir);
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream stream = asm.GetManifestResourceStream(PayloadResource))
            {
                if (stream == null) { error = "安装包损坏：缺少内嵌程序"; return false; }
                string tmp = TargetExe + ".new";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fs);
                }
                File.Copy(tmp, TargetExe, true);
                File.Delete(tmp);
            }
            Log("已释放播放器到 " + TargetExe);

            // 2. 记住选的片头
            Directory.CreateDirectory(DataDir);
            string settingsFile = Path.Combine(DataDir, "settings.txt");
            if (keepExistingClip && File.Exists(settingsFile))
            {
                // 静默重装/升级时不要动用户已经选好的片头：以前这里无条件写 clip，
                // 而静默路径硬编码 "brand"，结果是"更新完我的开机动画变回去了"。
                Log("已保留原有选片，未覆盖");
            }
            else
            {
                File.WriteAllText(settingsFile, clip);
                Log("片头设为 " + clip);
            }

            // 3. 开机自启
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (autoStart)
                {
                    key.SetValue(RunValueName, "\"" + TargetExe + "\" --play --delay 3" + (mute ? " --mute" : ""));
                    Log("已写入自启键" + (mute ? "（静音）" : "（带声音）"));
                }
                else
                {
                    key.DeleteValue(RunValueName, false);
                    Log("按用户选择未写自启键");
                }
            }

            // 4. 开始菜单快捷方式 → 打开选片窗口
            CreateShortcut(StartMenuLink, TargetExe, "--choose", "选择开机动画的片头");

            // 4b. 桌面快捷方式 —— 同一个目标。用户下载完最直接的入口就是桌面，
            //     卸载时由 BootAnimation.exe 一并删除。
            CreateShortcut(DesktopLink, TargetExe, "--choose", "选择开机动画的片头");

            // 5. 「应用和功能」里的卸载入口
            long sizeKb = new FileInfo(TargetExe).Length / 1024;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
            {
                key.SetValue("DisplayName", "开机动画");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "BootAnimation");
                key.SetValue("DisplayIcon", TargetExe + ",0");
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", "\"" + TargetExe + "\" --uninstall");
                key.SetValue("QuietUninstallString", "\"" + TargetExe + "\" --uninstall --silent");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
            }
            Log("已注册卸载入口");

            // 注册 bootanim:// 协议 —— 网页上点「装到我的开机动画」时靠它唤起本程序。
            // 没有它，社区网站的一键安装按钮就点不动。
            using (RegistryKey proto = Registry.CurrentUser.CreateSubKey(@"Software\Classes\bootanim"))
            {
                proto.SetValue(null, "URL:BootAnimation Protocol");
                proto.SetValue("URL Protocol", "");
                using (RegistryKey cmdKey = proto.CreateSubKey(@"shell\open\command"))
                {
                    cmdKey.SetValue(null, "\"" + TargetExe + "\" --install-url \"%1\"");
                }
                Log("已注册 bootanim:// 协议");
            }

            // 6. 装完立刻试看
            if (playNow)
            {
                ProcessStartInfo psi = new ProcessStartInfo(TargetExe, "--play --seconds 8");
                psi.UseShellExecute = false;
                Process.Start(psi);
                Log("已启动试看");
            }

            Log("安装完成");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log("安装异常: " + ex);
            return false;
        }
    }

    /// 用 WScript.Shell 建快捷方式。晚绑定，省掉对 IWshRuntimeLibrary 的引用。
    private static void CreateShortcut(string linkPath, string target, string arguments, string description)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) { Log("建快捷方式失败：找不到 WScript.Shell"); return; }
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            Type sc = shortcut.GetType();
            sc.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
            sc.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            sc.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { InstallDir });
            sc.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { target + ",0" });
            sc.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
            sc.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            Log("已建快捷方式 " + linkPath);
        }
        catch (Exception ex)
        {
            // 快捷方式失败不该让整个安装失败
            Log("建快捷方式异常: " + ex.Message);
        }
    }
}
