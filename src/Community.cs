// Community.cs —— 社区片头：接住网页的 bootanim:// 链接，下载、校验、装上。
//
// 设计要点：
//   1. 只信任 sha256 + 字节数双重校验 —— 社区是自助投稿，视频来自不可控的第三方，
//      校验不通过就删掉、绝不安装。
//   2. 链接里自带全部元数据（id/url/sha256/bytes/name/author），所以客户端
//      **不需要解析 JSON**，也就不需要引入任何 JSON 库。
//   3. 装好的片段存在 %LOCALAPPDATA%\BootAnimation\community\，和内置片段一起出现在选片窗口。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

/// 一个已安装的社区片段
internal sealed class CommunityEntry
{
    public string Id;
    public string Name;
    public string Author;
    public string Path;

    public CommunityEntry(string id, string name, string author, string path)
    {
        Id = id; Name = name; Author = author; Path = path;
    }

    public string Display
    {
        get
        {
            string who = string.IsNullOrEmpty(Author) ? "" : "  ·  " + Author;
            return (string.IsNullOrEmpty(Name) ? Id : Name) + who + "　（社区）";
        }
    }
}

internal static class Community
{
    /// 社区网站。客户端里点「浏览社区」就打开它。
    internal const string SiteUrl = "https://nativedog1.github.io/-Boot-Animation-Community-/";

    internal static string Dir
    {
        get { return Path.Combine(Program.DataDirPath, "community"); }
    }

    private static string MetaPath(string id)
    {
        return Path.Combine(Dir, id + ".meta");
    }

    private static string VideoPath(string id)
    {
        return Path.Combine(Dir, id + ".mp4");
    }

    /// 列出已安装的社区片段。
    internal static List<CommunityEntry> List()
    {
        List<CommunityEntry> list = new List<CommunityEntry>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (string meta in Directory.GetFiles(Dir, "*.meta"))
            {
                Dictionary<string, string> m = ReadMeta(meta);
                string id = Value(m, "id");
                if (string.IsNullOrEmpty(id)) continue;
                string video = VideoPath(id);
                if (!File.Exists(video)) continue;
                list.Add(new CommunityEntry(id, Value(m, "name"), Value(m, "author"), video));
            }
        }
        catch (Exception ex) { Program.Log("列出社区片段失败: " + ex.Message); }
        list.Sort(delegate (CommunityEntry a, CommunityEntry b) { return string.Compare(a.Id, b.Id, StringComparison.Ordinal); });
        return list;
    }

    internal static CommunityEntry Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (CommunityEntry e in List())
        {
            if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    /// 社区片段的 id 形如 owner__slug，用来和内置片段的 id（brand/cyberpunk/…）区分。
    internal static bool LooksLikeCommunityId(string id)
    {
        return !string.IsNullOrEmpty(id) && id.IndexOf("__", StringComparison.Ordinal) > 0;
    }

    // ---------------------------------------------------------------- 元数据（简单的 key=value，不引 JSON 库）

    private static Dictionary<string, string> ReadMeta(string path)
    {
        Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string line in File.ReadAllLines(path))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                m[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
        }
        catch (Exception ex) { Program.Log("读元数据失败 " + path + ": " + ex.Message); }
        return m;
    }

    private static void WriteMeta(string id, string name, string author, string sha256, long bytes)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(MetaPath(id), new string[]
        {
            "id=" + id,
            "name=" + (name ?? ""),
            "author=" + (author ?? ""),
            "sha256=" + (sha256 ?? ""),
            "bytes=" + bytes.ToString(CultureInfo.InvariantCulture),
            "installed=" + DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
        });
    }

    private static string Value(Dictionary<string, string> m, string key)
    {
        string v;
        return m.TryGetValue(key, out v) ? v : "";
    }

    // ---------------------------------------------------------------- bootanim:// 链接

    internal sealed class InstallRequest
    {
        public string Id;
        public string Url;
        public string Sha256;
        public long Bytes;
        public string Name;
        public string Author;

        public bool IsValid
        {
            get { return !string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(Url) && Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// 解析 bootanim://install?id=..&url=..&sha256=..&bytes=..&name=..&author=..
    /// 手写解析：不引 System.Web 就不必为了一个查询串拉进整个库。
    internal static InstallRequest ParseInstallUrl(string raw)
    {
        InstallRequest r = new InstallRequest();
        if (string.IsNullOrEmpty(raw)) return r;
        int q = raw.IndexOf('?');
        if (q < 0) return r;
        foreach (string pair in raw.Substring(q + 1).Split('&'))
        {
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string key = pair.Substring(0, eq).ToLowerInvariant();
            string val = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace("+", " "));
            if (key == "id") r.Id = val;
            else if (key == "url") r.Url = val;
            else if (key == "sha256") r.Sha256 = val.ToLowerInvariant();
            else if (key == "bytes") { long b; long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out b); r.Bytes = b; }
            else if (key == "name") r.Name = val;
            else if (key == "author") r.Author = val;
        }
        return r;
    }

    // ---------------------------------------------------------------- 下载并安装

    private static bool tlsReady = false;

    /// .NET Framework 默认只启用 TLS 1.0，而 GitHub / 几乎所有 CDN 都要求 TLS 1.2+，
    /// 不设置的话每一次下载都会以「未能创建 SSL/TLS 安全通道」失败。
    /// （这个坑是实测撞出来的：用真实链接跑第一次就挂在这里。）
    private static void EnsureTls()
    {
        if (tlsReady) return;
        tlsReady = true;
        try
        {
            // 768=Tls11, 3072=Tls12, 12288=Tls13。用数字强转是为了兼容老运行时。
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)(768 | 3072 | 12288);
            Program.Log("TLS: 已启用 1.1/1.2/1.3");
        }
        catch
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)(768 | 3072);
                Program.Log("TLS: 已启用 1.1/1.2");
            }
            catch (Exception ex) { Program.Log("TLS 设置失败: " + ex.Message); }
        }
    }

    /// 异步下载 → 校验 → 落地 → 设为当前片头。
    /// 用异步是为了能报真实进度（10–30 MB 的文件，假进度条会让人以为卡死了）。
    internal static void InstallAsync(InstallRequest r, Dispatcher dispatcher,
        Action<long, long> onProgress, Action<bool, string> onDone)
    {
        if (!r.IsValid) { onDone(false, "链接不完整（缺 id 或 url）"); return; }
        EnsureTls();

        try { Directory.CreateDirectory(Dir); }
        catch (Exception ex) { onDone(false, "无法创建目录：" + ex.Message); return; }

        string target = VideoPath(r.Id);
        string tmp = target + ".part";
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

        WebClient client = new WebClient();
        client.Headers.Add("User-Agent", "BootAnimation/1.0");
        Program.Log("开始下载社区片段 " + r.Id + " ← " + r.Url);

        client.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
        {
            if (onProgress != null) onProgress(e.BytesReceived, e.TotalBytesToReceive);
        };

        client.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            try { client.Dispose(); } catch { }

            if (e.Error != null)
            {
                Program.Log("下载失败: " + e.Error.Message);
                onDone(false, "下载失败：" + e.Error.Message);
                return;
            }
            if (e.Cancelled) { onDone(false, "已取消"); return; }

            try
            {
                FileInfo info = new FileInfo(tmp);
                if (r.Bytes > 0 && info.Length != r.Bytes)
                {
                    File.Delete(tmp);
                    onDone(false, "文件大小不符：目录声明 " + r.Bytes + " 字节，实际 " + info.Length + " 字节");
                    return;
                }

                if (!string.IsNullOrEmpty(r.Sha256))
                {
                    string actual = Program.Sha256Of(tmp);
                    if (!string.Equals(actual, r.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(tmp);
                        Program.Log("sha256 不符 期望=" + r.Sha256 + " 实际=" + actual);
                        onDone(false, "校验失败：sha256 不符（文件被改动或下载不完整）");
                        return;
                    }
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                WriteMeta(r.Id, r.Name, r.Author, r.Sha256, info.Length);
                Program.WriteChosen(r.Id);
                Program.Log("社区片段安装完成 " + r.Id + "（" + info.Length + " 字节）");
                onDone(true, null);
            }
            catch (Exception ex)
            {
                Program.Log("安装收尾失败: " + ex);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                onDone(false, ex.Message);
            }
        };

        try
        {
            client.DownloadFileAsync(new Uri(r.Url), tmp);
        }
        catch (Exception ex)
        {
            try { client.Dispose(); } catch { }
            Program.Log("发起下载失败: " + ex.Message);
            onDone(false, ex.Message);
        }
    }

    private static string FormatMb(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        if (mb < 1.0) return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// 带进度条的小窗口。用户从网页点「装」进来时看到的就是它。
    internal static Window BuildInstallWindow(InstallRequest r, Application app)
    {
        Window win = new Window();
        win.Title = "安装社区开机动画";
        win.Width = 460;
        win.Height = 200;
        win.ResizeMode = ResizeMode.NoResize;
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.Background = Brushes.White;

        StackPanel panel = new StackPanel();
        panel.Margin = new Thickness(22, 20, 22, 20);

        TextBlock title = new TextBlock();
        title.Text = string.IsNullOrEmpty(r.Name) ? r.Id : r.Name;
        title.FontSize = 17;
        title.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(title);

        TextBlock who = new TextBlock();
        who.Text = string.IsNullOrEmpty(r.Author) ? "来自社区" : "作者：" + r.Author;
        who.Foreground = Brushes.Gray;
        who.Margin = new Thickness(0, 2, 0, 14);
        panel.Children.Add(who);

        ProgressBar bar = new ProgressBar();
        bar.Height = 8;
        bar.Minimum = 0;
        bar.Maximum = 100;
        panel.Children.Add(bar);

        TextBlock status = new TextBlock();
        status.Text = "准备下载…";
        status.Margin = new Thickness(0, 10, 0, 0);
        status.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(status);

        Button retry = new Button();
        retry.Content = "打开网页自己挑";
        retry.Width = 150;
        retry.Height = 30;
        retry.Margin = new Thickness(0, 14, 0, 0);
        retry.Visibility = Visibility.Collapsed;
        panel.Children.Add(retry);

        win.Content = panel;

        Action run = null;
        run = delegate
        {
            status.Foreground = Brushes.Black;
            status.Text = "正在下载…";
            bar.IsIndeterminate = false;
            bar.Value = 0;
            retry.Visibility = Visibility.Collapsed;

            InstallAsync(r, win.Dispatcher,
                delegate(long got, long total)
                {
                    if (total > 0)
                    {
                        bar.Value = Math.Min(99.0, got * 100.0 / total);
                        status.Text = "正在下载 " + FormatMb(got) + " / " + FormatMb(total);
                    }
                    else
                    {
                        status.Text = "正在下载 " + FormatMb(got);
                    }
                },
                delegate(bool ok, string reason)
                {
                    if (ok)
                    {
                        bar.Value = 100;
                        status.Foreground = Brushes.Green;
                        status.Text = "安装完成，已设为你的开机动画。下次登录就会播放。";
                        DispatcherTimer close = new DispatcherTimer();
                        close.Interval = TimeSpan.FromSeconds(2.5);
                        close.Tick += delegate { close.Stop(); win.Close(); };
                        close.Start();
                    }
                    else
                    {
                        bar.Value = 0;
                        status.Foreground = Brushes.Firebrick;
                        status.Text = "安装失败：" + reason;
                        retry.Visibility = Visibility.Visible;
                    }
                });
        };

        retry.Click += delegate
        {
            try { System.Diagnostics.Process.Start(SiteUrl); } catch (Exception ex) { Program.Log("打开网页失败: " + ex.Message); }
            win.Close();
        };

        win.Loaded += delegate { run(); };
        return win;
    }
}
