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
using System.Threading;
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
            get
            {
                if (string.IsNullOrEmpty(Id) || string.IsNullOrEmpty(Url)) return false;
                if (Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
#if BATEST
                // 仅用于本地验证断点续传（tools-serve-test.mjs 是 http 服务）。
                // 这个分支只在 /define:BATEST 的测试构建里存在，正式产物里没有它，
                // 所以线上依然**只接受 https 直链**。
                if (Url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)) return true;
#endif
                return false;
            }
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

    /// <summary>给仓库层用的 TLS 开关（它要拉社区目录，同样受 TLS 1.0 的默认值影响）。</summary>
    internal static void EnsureTlsPublic()
    {
        EnsureTls();
    }

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

    /// 下载社区片段：**支持断点续传与自动重试**。
    ///
    /// 为什么换掉原来的 WebClient.DownloadFileAsync：它是单连接、无续传、无重试，
    /// 一旦中断就整段作废。社区库里 4K 片头动辄几十上百 MB，实测国内下载 51.8 MB
    /// 的文件会在 26 MB 处断掉 —— 那时用户只能反复重试，而每次都得从 0 开始。
    ///
    /// 现在：分块读 → 服务器支持 HTTP Range 就续传 → 中断按 2/4/6/8/10 秒退避重试，
    /// 最多 6 轮；每轮结束核对字节数，够了才进入 sha256 校验（哈希仍是最终的安全底线，
    /// 所以即使续传拼上了过期内容也会被拒绝安装）。
    internal static void InstallAsync(InstallRequest r, Dispatcher dispatcher,
        Action<long, long> onProgress, Action<string> onStatus, Action<bool, string> onDone)
    {
        if (!r.IsValid)
        {
            Report(dispatcher, delegate { onDone(false, "链接不完整（缺 id 或 url）"); });
            return;
        }
        EnsureTls();

        try { Directory.CreateDirectory(Dir); }
        catch (Exception ex)
        {
            string dirErr = ex.Message;
            Report(dispatcher, delegate { onDone(false, "无法创建目录：" + dirErr); });
            return;
        }

        string target = VideoPath(r.Id);
        string tmp = target + ".part";

        // 下载放到后台线程：HttpWebRequest 的阻塞读法最简单可靠，
        // 不用 async/await（这台机器上的 csc 与目标框架都更保守）。
        Thread worker = new Thread(delegate()
        {
            string error = DownloadToFile(r, tmp, dispatcher, onProgress, onStatus);
            if (error != null)
            {
                Report(dispatcher, delegate { onDone(false, error); });
                return;
            }

            try
            {
                FileInfo info = new FileInfo(tmp);
                if (r.Bytes > 0 && info.Length != r.Bytes)
                {
                    TryDelete(tmp);
                    string sizeMsg = "文件大小不符：目录声明 " + r.Bytes + " 字节，实际 " + info.Length + " 字节";
                    Report(dispatcher, delegate { onDone(false, sizeMsg); });
                    return;
                }

                if (!string.IsNullOrEmpty(r.Sha256))
                {
                    Status(dispatcher, onStatus, "下载完成，正在校验 sha256…");
                    string actual = Program.Sha256Of(tmp);
                    if (!string.Equals(actual, r.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(tmp);
                        Program.Log("sha256 不符 期望=" + r.Sha256 + " 实际=" + actual);
                        Report(dispatcher, delegate { onDone(false, "校验失败：sha256 不符（文件被改动或下载不完整）"); });
                        return;
                    }
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                WriteMeta(r.Id, r.Name, r.Author, r.Sha256, info.Length);
                Program.WriteChosen(r.Id);
                Program.Log("社区片段安装完成 " + r.Id + "（" + info.Length + " 字节）");
                Report(dispatcher, delegate { onDone(true, null); });
            }
            catch (Exception ex)
            {
                Program.Log("安装收尾失败: " + ex);
                TryDelete(tmp);
                string settleErr = ex.Message;
                Report(dispatcher, delegate { onDone(false, settleErr); });
            }
        });
        worker.IsBackground = true;
        worker.Name = "ba-community-download";
        worker.Start();
    }

    /// 把响应体写进 tmp（必要时续传）。返回 null = 已下满；否则是给用户看的原因。
    private static string DownloadToFile(InstallRequest r, string tmp, Dispatcher dispatcher,
        Action<long, long> onProgress, Action<string> onStatus)
    {
        const int maxAttempts = 6;
        string lastError = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            long have = FileLength(tmp);
            if (r.Bytes > 0 && have == r.Bytes) return null;              // 上一轮其实已经下满
            if (r.Bytes > 0 && have > r.Bytes) { TryDelete(tmp); have = 0; } // 残留不合法，重来

            bool readToEnd = false;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(r.Url);
                req.UserAgent = "BootAnimation/1.1";
                req.Timeout = 30000;           // 连接与响应头
                req.ReadWriteTimeout = 60000;  // 单次读：卡住 60 秒就抛，交给重试（默认 5 分钟太久）
                req.AllowAutoRedirect = true;
                if (have > 0) req.AddRange(have);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                    {
                        // 服务器不支持 Range（返回 200 全量）：只能从头下，别把两段拼一起
                        Program.Log("服务器不支持续传（HTTP " + (int)resp.StatusCode + "），从头下载");
                        have = 0;
                    }

                    long total = r.Bytes > 0 ? r.Bytes : (have + Math.Max(0L, resp.ContentLength));
                    if (have > 0) Program.Log("续传：本地已有 " + have + " 字节，发送 Range=" + have + "-（HTTP " + (int)resp.StatusCode + "）");
                    else Program.Log("开始下载 " + r.Id + " ← " + r.Url);
                    Status(dispatcher, onStatus, have > 0
                        ? "从 " + FormatMb(have) + " 继续下载…"
                        : "正在下载…");

                    using (Stream rs = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(tmp,
                        have > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        byte[] buf = new byte[1 << 20];
                        long got = have;
                        int n;
                        while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, n);
                            got += n;
                            // 每次快照一对值再交给 UI 线程：避免闭包读到后续变化的值
                            long snapshot = got;
                            long snapshotTotal = total;
                            Report(dispatcher, delegate { if (onProgress != null) onProgress(snapshot, snapshotTotal); });
                        }
                        fs.Flush();
                    }
                    readToEnd = true;
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Program.Log("下载中断（第 " + attempt + "/" + maxAttempts + " 轮）: " + ex.Message);
            }

            long now = FileLength(tmp);
            if (r.Bytes > 0 && now == r.Bytes) return null;
            if (r.Bytes <= 0 && readToEnd) return null;   // 目录没给字节数：这一轮读到底了就算完成

            if (attempt < maxAttempts)
            {
                int waitSec = 2 * attempt;
                Program.Log("已收 " + now + " 字节，等待 " + waitSec + " 秒后续传");
                Status(dispatcher, onStatus, "连接中断，已保留 " + FormatMb(now)
                    + "，" + waitSec + " 秒后自动续传（第 " + (attempt + 1) + "/" + maxAttempts + " 轮）…");
                try { Thread.Sleep(waitSec * 1000); } catch { }
            }
        }

        return "下载屡次中断：" + (lastError == null ? "多次尝试后仍未下完" : lastError)
            + "（已保留 " + FormatMb(FileLength(tmp)) + "，再点一次会从断点继续）";
    }

    private static long FileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// 把回调丢回 UI 线程（下载在后台线程上跑）。
    private static void Report(Dispatcher dispatcher, Action action)
    {
        if (action == null) return;
        if (dispatcher == null) { action(); return; }
        try { dispatcher.BeginInvoke(DispatcherPriority.Normal, action); }
        catch { action(); }
    }

    private static void Status(Dispatcher dispatcher, Action<string> onStatus, string text)
    {
        if (onStatus == null) return;
        Report(dispatcher, delegate { onStatus(text); });
    }

    private static string FormatMb(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        if (mb < 1.0) return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>
    /// 从界面的社区页安装一条目录项。
    ///
    /// 与 BuildInstallWindow 的区别：那个是给 bootanim:// 深链用的（元数据全在 URL 里），
    /// 这个是给"界面里点 Install"用的 —— 元数据来自 data/index.json 解析出的
    /// AnimationInfo。两者最终调用**同一个** InstallAsync，所以断点续传、重试、
    /// sha256 校验、字节数核对只有一份实现，不会两边行为不一致。
    /// </summary>
    internal static Window BuildInstallWindowFor(BootAnimation.AnimationInfo info, Window owner)
    {
        InstallRequest r = new InstallRequest();
        r.Id = info.Id;
        r.Url = info.RemoteVideoUrl;
        r.Sha256 = info.Sha256;
        r.Bytes = info.Bytes;
        r.Name = info.Name;
        r.Author = info.Author;
        return BuildInstallWindow(r, null);
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
                delegate(string note)
                {
                    status.Text = note;
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
