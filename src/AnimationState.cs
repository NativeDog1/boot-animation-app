// AnimationState.cs —— 动画模型与唯一状态源
//
// 为什么要有这个文件（真实 bug 的根因）：
//
//   之前"换了一段动画，Preview 还是旧的，必须按 F5"不是渲染问题，是**状态问题**。
//   HomeView 在构造时就把一个 MediaElement 的 Source 钉死成传入的 mediaPath，
//   之后再没有任何代码路径能改它 —— 于是"当前动画"这个事实同时存在于四个互不相干的地方：
//     · settings.txt 里的 clipId
//     · Shell.Run() 读出来传进构造函数的字符串
//     · HomeView.mediaPath 这个只读字段
//     · MediaElement.Source（构造那一刻定下，永不变）
//   换动画只改了第一处，其余三处都不知道，所以必须重建整个窗口（=F5）才看得到。
//
//   这里把"当前动画"收敛成**一个**对象上的一份状态。UI 只订阅，不自己存。
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员、无 XAML、无 LINQ 必需。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BootAnimation
{
    /// <summary>一段动画从哪来。决定它能不能被删、能不能被"应用"。</summary>
    internal enum AnimationSource
    {
        /// <summary>内嵌在 exe 里，删不掉，永远可用。</summary>
        BuiltIn = 0,
        /// <summary>从社区下载的，存在 %LOCALAPPDATA%\BootAnimation\community。</summary>
        Community = 1,
        /// <summary>用户自己放进来的（--file 或手工拷贝）。</summary>
        Local = 2,
        /// <summary>只在社区目录里、还没下载 —— 只能预览远程海报，不能播放。</summary>
        Remote = 3,
    }

    /// <summary>运行时就绪度。启动链路只看这一个字段决定走快路还是慢路。</summary>
    internal enum PackageState
    {
        /// <summary>没有 prepared 包，启动时必须现场准备（慢）。</summary>
        NotPrepared = 0,
        /// <summary>prepared 包可用，启动可以直接读（快）。</summary>
        Ready = 1,
        /// <summary>源文件已不在或校验不过，需要重新准备。</summary>
        Stale = 2,
        /// <summary>准备失败并且记录了原因。</summary>
        Failed = 3,
    }

    /// <summary>
    /// 一段动画的全部已知事实。字段全部来自真实探测，宁可留空（null）也不编一个数字。
    /// </summary>
    internal sealed class AnimationInfo
    {
        public string Id;
        public string Name;
        public string Author;
        public AnimationSource Source;
        /// <summary>可播放的本地文件路径；Remote 时为 null。</summary>
        public string Path;
        public string PosterPath;
        public string RemotePosterUrl;
        public string RemoteVideoUrl;
        public string Sha256;
        public long Bytes;
        public int Width;
        public int Height;
        /// <summary>帧率。WPF 不暴露，只有 ffprobe 在时才填 —— 拿不到就是 0，不猜。</summary>
        public double Fps;
        public double DurationSeconds;
        public PackageState Package = PackageState.NotPrepared;
        /// <summary>prepared 包目录；Ready 时非空。</summary>
        public string PackageDir;
        /// <summary>prepared 包占用字节；0 表示未知。</summary>
        public long PackageBytes;
        /// <summary>准备/校验过程中最后一次失败原因，给 Diagnostics 页用。</summary>
        public string PackageNote;

        public AnimationInfo()
        {
            Source = AnimationSource.BuiltIn;
            Package = PackageState.NotPrepared;
        }

        public bool IsPlayable
        {
            get { return Path != null && Path.Length > 0 && File.Exists(Path); }
        }

        public bool IsInstalled
        {
            get { return Source != AnimationSource.Remote && IsPlayable; }
        }

        /// <summary>分辨率文本。未知时返回 null，让调用方决定怎么显示"未知"，而不是写 "0×0"。</summary>
        public string ResolutionText
        {
            get
            {
                if (Width <= 0 || Height <= 0) return null;
                return Width.ToString(CultureInfo.InvariantCulture) + "×" + Height.ToString(CultureInfo.InvariantCulture);
            }
        }

        public string FpsText
        {
            get
            {
                if (Fps <= 0) return null;
                return Fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps";
            }
        }

        public string DurationText
        {
            get
            {
                if (DurationSeconds <= 0) return null;
                return DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            }
        }

        public string SizeText
        {
            get
            {
                if (Bytes <= 0) return null;
                double mb = Bytes / 1024.0 / 1024.0;
                if (mb >= 1.0) return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
                return (Bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            }
        }

        /// <summary>
        /// 这段动画能不能安全地放进 UEFI 阶段跑。UEFI 只有 ESP 上的 FAT 分区、内存受限、
        /// 没有解码器 —— 所以判断标准很硬：必须在本地、整段能塞进内存预算、分辨率不过分。
        /// 现在的实现里没有任何 UEFI Runtime，所以这个值只用于**如实显示**，不驱动任何行为。
        /// </summary>
        public bool UefiCompatible
        {
            get
            {
                if (!IsPlayable) return false;
                if (Width <= 0 || Height <= 0) return false;
                if (Width > 3840 || Height > 2160) return false;
                if (Bytes > 64L * 1024 * 1024) return false;
                return true;
            }
        }
    }

    /// <summary>
    /// 唯一状态源。
    ///
    /// 所有 UI 都从这里读"当前是什么"，也都通过这里改。改完立刻触发一次变更事件，
    /// 每个页面据此**重建自己的内容**（而不是去 cache 里找旧对象）。
    /// 这是"不需要 F5"的机制保证：没有任何一个页面自己持有"当前动画"的副本。
    /// </summary>
    internal sealed class AnimationStateStore
    {
        private readonly List<AnimationInfo> items = new List<AnimationInfo>();
        private readonly object gate = new object();

        private string activeId;
        private string previewId;
        private bool busy;
        private string statusText = "";
        private string statusTone = "";

        /// <summary>状态变了。订阅者必须**重新读**快照，不许缓存旧对象。</summary>
        public event EventHandler Changed;

        public object Gate { get { return gate; } }

        /// <summary>库里的全部动画（只读快照）。</summary>
        public List<AnimationInfo> Items
        {
            get { lock (gate) { return new List<AnimationInfo>(items); } }
        }

        public int Count { get { lock (gate) { return items.Count; } } }

        /// <summary>正在生效的动画 id（下次登录会播的那一段）。</summary>
        public string ActiveId
        {
            get { lock (gate) { return activeId; } }
        }

        /// <summary>当前在 Preview 里选中的动画 id。**可以和 ActiveId 不同** —— 预览不等于应用。</summary>
        public string PreviewId
        {
            get { lock (gate) { return previewId; } }
        }

        public bool Busy
        {
            get { lock (gate) { return busy; } }
        }

        public string StatusText
        {
            get { lock (gate) { return statusText; } }
        }

        public string StatusTone
        {
            get { lock (gate) { return statusTone; } }
        }

        public AnimationInfo Active { get { return ById(activeId); } }
        public AnimationInfo Preview { get { return ById(previewId); } }

        public AnimationInfo ById(string id)
        {
            if (id == null) return null;
            lock (gate)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (string.Equals(items[i].Id, id, StringComparison.OrdinalIgnoreCase)) return items[i];
                }
            }
            return null;
        }

        /// <summary>整体替换目录。用于扫描完成、社区索引刷新。</summary>
        public void ReplaceAll(List<AnimationInfo> next)
        {
            lock (gate)
            {
                items.Clear();
                if (next != null) items.AddRange(next);
            }
            Raise();
        }

        /// <summary>
        /// 应用一段动画 = 把它记为"下次登录要播的那段"。
        ///
        /// 这一步**必须**同时做三件事，否则就会出现"Apply 了但状态没变"：
        ///   1. 落盘（settings.txt）—— 启动链路读的是它
        ///   2. 更新内存里的 activeId
        ///   3. 广播变更，让所有页面重建
        ///   4. **准备运行时资源**（校验 + 缓存"第一帧"）—— 见 PrepareRuntimeResource
        /// 之前只做了第 1 步。
        /// </summary>
        public bool Apply(string id)
        {
            AnimationInfo target = ById(id);
            if (target == null) { SetStatus("找不到这段动画", "bad"); return false; }
            if (!target.IsInstalled)
            {
                SetStatus("这段还没安装，安装后才能应用", "warn");
                return false;
            }
            try
            {
                Program.WriteChosen(target.Id);
            }
            catch (Exception ex)
            {
                Program.Log("写入选片失败: " + ex.Message);
                SetStatus("保存失败：" + ex.Message, "bad");
                return false;
            }
            lock (gate) { activeId = target.Id; }
            SetStatus("已应用：" + DisplayName(target), "ok");
            PrepareRuntimeResource(target);
            return true;
        }

        /// <summary>
        /// 在后台准备一段动画的**运行时资源**：校验哈希 + 确保"第一帧"缓存图存在。
        ///
        /// 为什么由 Apply 触发、而不是开机时做：
        ///   首帧层用的就是这张缓存图。它不存在时，启动那几百毫秒里只能显示模糊氛围层 ——
        ///   客户看到的就是"一块未显示的东西"。与其在开机最紧张的几百毫秒里起 ffmpeg，
        ///   不如在用户点 Apply 的这一刻做完，开机只负责**读**。
        ///   这就是"安装/应用时准备资源，启动时只读"的分工。
        ///
        /// 只做本地、有限、可失败的事，绝不联网。失败不是错误：这一版仍然能播，
        /// 只是首帧层缺席（退化为氛围背景），下一次 Apply 会再试。
        /// </summary>
        public void PrepareRuntimeResource(AnimationInfo info)
        {
            if (info == null || info.Path == null) return;
            AnimationInfo captured = info;
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                try
                {
                    // 已安装的社区片段带 sha256，校验一次是有价值的（文件可能被改动）。
                    // 本地散放的文件没有已知哈希，跳过校验而不是编一个出来。
                    if (!string.IsNullOrEmpty(captured.Sha256))
                    {
                        string actual = Program.Sha256Of(captured.Path);
                        if (!string.Equals(actual, captured.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            captured.Package = PackageState.Failed;
                            captured.PackageNote = "sha256 不符：文件已被改动";
                            Program.Log("准备运行时资源失败（哈希不符）: " + captured.Id);
                            return;
                        }
                    }
                    string poster = Program.PosterPathForMedia(captured.Path);
                    if (poster != null && !System.IO.File.Exists(poster))
                    {
                        Program.BuildPosterInternal(captured.Path, poster);
                    }
                    captured.Package = PackageState.Ready;
                    captured.PackageNote = "第一帧已缓存，启动时直接读";
                    Program.Log("运行时资源就绪: " + captured.Id);
                }
                catch (Exception ex)
                {
                    captured.Package = PackageState.Stale;
                    captured.PackageNote = ex.Message;
                    Program.Log("准备运行时资源失败 " + captured.Id + ": " + ex.Message);
                }
            });
            worker.IsBackground = true;
            worker.Priority = System.Threading.ThreadPriority.BelowNormal;
            worker.Name = "ba-prepare";
            worker.Start();
        }

        /// <summary>把 Preview 指向一段动画。**不改** activeId —— 预览和应用是两件事。</summary>
        public void SelectPreview(string id)
        {
            lock (gate) { previewId = id; }
            Raise();
        }

        public void SetBusy(bool value, string text)
        {
            lock (gate)
            {
                busy = value;
                if (text != null) statusText = text;
            }
            Raise();
        }

        public void SetStatus(string text, string tone)
        {
            lock (gate)
            {
                statusText = text == null ? "" : text;
                statusTone = tone == null ? "" : tone;
            }
            Raise();
        }

        /// <summary>同步一次 activeId（启动时从磁盘读，或外部改了配置）。</summary>
        public void SyncActive(string id)
        {
            lock (gate) { activeId = id; }
            Raise();
        }

        /// <summary>
        /// 随机挑一段：只从**已经可以播**的里面挑（内置 + 已安装），并当场把 Preview 和
        /// Active 一起切过去。要求"随机后必须立即显示结果"就是靠这里一次性广播完成的 ——
        /// 分两次改状态会给 UI 留出显示中间态的机会。
        /// </summary>
        public AnimationInfo PickRandom(Random rng, bool alsoApply)
        {
            List<AnimationInfo> pool = new List<AnimationInfo>();
            lock (gate)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].IsInstalled) pool.Add(items[i]);
                }
            }
            if (pool.Count == 0) { SetStatus("没有可播放的动画", "bad"); return null; }

            // 尽量避免连续两次抽到同一段（池子 > 1 时）。
            AnimationInfo pick = pool[rng.Next(pool.Count)];
            if (pool.Count > 1 && string.Equals(pick.Id, previewId, StringComparison.OrdinalIgnoreCase))
            {
                pick = pool[(pool.IndexOf(pick) + 1) % pool.Count];
            }

            lock (gate) { previewId = pick.Id; }
            if (alsoApply)
            {
                try { Program.WriteChosen(pick.Id); }
                catch (Exception ex) { Program.Log("随机后写入选片失败: " + ex.Message); }
                lock (gate) { activeId = pick.Id; }
            }
            SetStatus("随机选中：" + DisplayName(pick), "ok");
            return pick;
        }

        /// <summary>显示名：没有名字就退回 id，绝不显示空白。</summary>
        public static string DisplayName(AnimationInfo a)
        {
            if (a == null) return "（无）";
            if (!string.IsNullOrEmpty(a.Name)) return a.Name;
            if (!string.IsNullOrEmpty(a.Id)) return a.Id;
            return "（未命名）";
        }

        private void Raise()
        {
            EventHandler handler = Changed;
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { Program.Log("状态广播失败: " + ex.Message); }
        }
    }
}
