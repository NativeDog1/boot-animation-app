// AnimationRepository.cs —— 动画从哪来、怎么读、怎么探测
//
// 三层来源，统一成一个列表：
//   1. 内置：内嵌在 exe 里的四段（解到 %LOCALAPPDATA%\BootAnimation\clips）
//   2. 本地：%LOCALAPPDATA%\BootAnimation\community\*.mp4（含 *.meta）
//      以及数据目录根下散放的视频文件
//   3. 远程：社区目录 data/index.json 里的条目（只有海报和元数据，没下载）
//
// 探测分辨率和时长用**自己解析 MP4 的 atom**，不调 ffprobe：
//   · 启动链路里不能起子进程（起进程 + 等它跑完是几百毫秒）
//   · ffprobe 在别人机器上不一定有
//   · mp4 的 tkhd 里有宽高、mvhd 里有 timescale+duration，stts 里有帧数
//   拿不到就留空（null），**绝不编一个数字**。
//
// 编译器约束：csc（C# 5）—— 无字符串插值、无 ?.、无表达式体成员、无 XAML。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace BootAnimation
{
    internal static class AnimationRepository
    {
        /// <summary>社区目录。只读，不修改社区项目本身。</summary>
        internal const string IndexUrl =
            "https://raw.githubusercontent.com/NativeDog1/-Boot-Animation-Community-/main/data/index.json";

        internal static string CommunityDir
        {
            get { return Path.Combine(Program.DataDirPath, "community"); }
        }

        /// <summary>内置四段在磁盘上的落点（启动时由 Program.Extract 解出来）。</summary>
        internal static string[] BuiltInIds()
        {
            string[] ids = new string[Program.ClipIds.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = Program.ClipIds[i];
            return ids;
        }

        // ───────────────────────────────────────────────────────────── 本地扫描

        /// <summary>
        /// 扫描全部**本地**动画（内置 + 已下载）。这是 UI 用的完整列表。
        /// 启动链路**不调用**它 —— 启动只解析当前那一段（见 BootRuntime）。
        /// </summary>
        internal static List<AnimationInfo> ScanLocal()
        {
            List<AnimationInfo> list = new List<AnimationInfo>();

            // 1. 内置
            for (int i = 0; i < Program.ClipIds.Length; i++)
            {
                string id = Program.ClipIds[i];
                AnimationInfo info = new AnimationInfo();
                info.Id = id;
                info.Name = Program.ClipNameOf(id);
                info.Author = "内置";
                info.Source = AnimationSource.BuiltIn;
                info.Path = Program.ClipPathOf(id);
                if (info.Path != null && File.Exists(info.Path))
                {
                    ProbeFile(info);
                    info.Sha256 = Program.ClipShaOf(id);
                }
                info.PosterPath = Program.PosterCachePathFor(id);
                list.Add(info);
            }

            // 2. 社区已下载（靠 .meta 认领，和旧版行为一致，保证兼容）
            try
            {
                if (Directory.Exists(CommunityDir))
                {
                    string[] metas = Directory.GetFiles(CommunityDir, "*.meta");
                    for (int i = 0; i < metas.Length; i++)
                    {
                        Dictionary<string, string> m = ReadKeyValueFile(metas[i]);
                        string id = Get(m, "id");
                        if (string.IsNullOrEmpty(id)) continue;

                        string video = Path.Combine(CommunityDir, id + ".mp4");
                        if (!File.Exists(video)) continue;
                        // 内置那四段可能被社区目录里同名条目重复列出来，跳过
                        if (IndexOfId(list, id) >= 0) continue;

                        AnimationInfo info = new AnimationInfo();
                        info.Id = id;
                        info.Name = Get(m, "name");
                        info.Author = Get(m, "author");
                        info.Source = AnimationSource.Community;
                        info.Path = video;
                        info.Sha256 = Get(m, "sha256");
                        long bytes;
                        if (long.TryParse(Get(m, "bytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes))
                            info.Bytes = bytes;
                        ProbeFile(info);
                        // 海报缓存按**文件名**建（LoadPosterBrush/BuildPoster 用的是
                        // Path.GetFileNameWithoutExtension），不是按 clip id。
                        // 之前这里写的是 PosterCachePathFor(id)，两条路径对不上，
                        // 结果是社区片段的"氛围背景"永远读不到、只能是纯黑边。
                        info.PosterPath = Program.PosterPathForMedia(info.Path);
                        list.Add(info);
                    }
                }
            }
            catch (Exception ex) { Program.Log("扫描社区目录失败: " + ex.Message); }

            // 3. 数据目录根下散放的视频（用户自己丢进来的）。故意只扫一层，
            //    不做递归 —— 递归扫描一个可能包含大量文件的目录会在 UI 线程上卡住。
            try
            {
                string root = Program.DataDirPath;
                if (Directory.Exists(root))
                {
                    string[] exts = new string[] { "*.mp4", "*.m4v", "*.wmv", "*.avi", "*.mov" };
                    for (int e = 0; e < exts.Length; e++)
                    {
                        string[] files = Directory.GetFiles(root, exts[e]);
                        for (int i = 0; i < files.Length; i++)
                        {
                            string fileName = Path.GetFileNameWithoutExtension(files[i]);
                            // clips/ 与 community/ 里的文件已在上面处理过，避免重复列出
                            if (DirectoryOf(files[i]).EndsWith("clips", StringComparison.OrdinalIgnoreCase)) continue;
                            if (DirectoryOf(files[i]).EndsWith("community", StringComparison.OrdinalIgnoreCase)) continue;
                            if (IndexOfId(list, fileName) >= 0) continue;

                            AnimationInfo info = new AnimationInfo();
                            info.Id = fileName;
                            info.Name = fileName;
                            info.Author = "本地文件";
                            info.Source = AnimationSource.Local;
                            info.Path = files[i];
                            ProbeFile(info);
                            info.PosterPath = Program.PosterPathForMedia(files[i]);
                            list.Add(info);
                        }
                    }
                }
            }
            catch (Exception ex) { Program.Log("扫描数据目录失败: " + ex.Message); }

            list.Sort(delegate(AnimationInfo a, AnimationInfo b)
            {
                // 内置在前，然后社区，最后本地散放；同组按名字
                int bySource = ((int)a.Source).CompareTo((int)b.Source);
                if (bySource != 0) return bySource;
                return string.Compare(AnimationStateStore.DisplayName(a), AnimationStateStore.DisplayName(b),
                    StringComparison.CurrentCulture);
            });
            return list;
        }

        private static string DirectoryOf(string file)
        {
            try { return Path.GetDirectoryName(file) ?? ""; }
            catch { return ""; }
        }

        private static int IndexOfId(List<AnimationInfo> list, string id)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        // ───────────────────────────────────────────────────────────── 社区目录

        /// <summary>
        /// 拉取社区目录并转成 Remote 条目。
        ///
        /// **绝不在启动链路上调用**：它要联网。BootRuntime 有硬性约束 —— 启动动画期间
        /// 不访问任何网络。这个只在 UI 的 Community 页被打开时调用。
        /// </summary>
        internal static List<AnimationInfo> FetchCommunityIndex(out string error)
        {
            error = null;
            List<AnimationInfo> list = new List<AnimationInfo>();
            try
            {
                Program.EnsureTls();
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(IndexUrl);
                req.UserAgent = "BootAnimation/1.2";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 20000;
                string body;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                List<Dictionary<string, string>> rows = JsonArrayOfObjects(body);
                for (int i = 0; i < rows.Count; i++)
                {
                    Dictionary<string, string> row = rows[i];
                    string id = Get(row, "id");
                    if (string.IsNullOrEmpty(id)) continue;

                    AnimationInfo info = new AnimationInfo();
                    info.Id = id;
                    info.Name = Get(row, "name");
                    info.Author = Get(row, "author");
                    info.Source = AnimationSource.Remote;
                    info.RemoteVideoUrl = Get(row, "video");
                    info.RemotePosterUrl = Get(row, "preview");
                    info.Sha256 = Get(row, "sha256");
                    info.Bytes = ParseLong(Get(row, "bytes"));
                    info.Width = (int)ParseDouble(Get(row, "width"));
                    info.Height = (int)ParseDouble(Get(row, "height"));
                    info.Fps = ParseDouble(Get(row, "fps"));
                    info.DurationSeconds = ParseDouble(Get(row, "duration"));
                    list.Add(info);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Program.Log("拉取社区目录失败: " + ex.Message);
            }
            return list;
        }

        // ───────────────────────────────────────────── 极简 JSON 读取（无第三方依赖）
        //
        // 为什么手写而不是引 DataContractJsonSerializer：
        //   我们只需要"一个对象数组里的若干字符串/数字字段"，而 DataContractJsonSerializer
        //   要求为每种结构先定义契约类型（C# 5 下还得配 CollectionDataContract），
        //   为一个只读的公开目录拉进一整套序列化契约不划算。
        //
        // 局限（如实说明）：它按 `"键": 值` 的形态做逐字段提取，适用本目录的**扁平对象**；
        // 嵌套结构只取最外层同名字段。目录格式若将来改成深层嵌套，这里要跟着改。
        // 它**不做**完整校验，所以任何解析不出来的值都留空，由调用方显示"未知"。

        private static List<Dictionary<string, string>> JsonArrayOfObjects(string json)
        {
            List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
            if (json == null) return rows;

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            int objStart = -1;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        rows.Add(JsonObject(json.Substring(objStart, i - objStart + 1)));
                        objStart = -1;
                    }
                }
            }
            return rows;
        }

        private static Dictionary<string, string> JsonObject(string obj)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 1;   // 跳过 '{'
            while (i < obj.Length)
            {
                int keyStart = obj.IndexOf('"', i);
                if (keyStart < 0) break;
                int keyEnd = FindStringEnd(obj, keyStart + 1);
                if (keyEnd < 0) break;
                string key = obj.Substring(keyStart + 1, keyEnd - keyStart - 1);

                int colon = obj.IndexOf(':', keyEnd + 1);
                if (colon < 0) break;
                int v = colon + 1;
                while (v < obj.Length && char.IsWhiteSpace(obj[v])) v++;
                if (v >= obj.Length) break;

                string value;
                if (obj[v] == '"')
                {
                    int valEnd = FindStringEnd(obj, v + 1);
                    if (valEnd < 0) break;
                    value = Unescape(obj.Substring(v + 1, valEnd - v - 1));
                    i = valEnd + 1;
                }
                else if (obj[v] == '[' || obj[v] == '{')
                {
                    // 数组/嵌套对象：跳过整段（我们不需要 tags 这类字段）
                    int close = SkipComposite(obj, v);
                    if (close < 0) break;
                    value = obj.Substring(v, close - v + 1);
                    i = close + 1;
                }
                else
                {
                    int valEnd = v;
                    while (valEnd < obj.Length && obj[valEnd] != ',' && obj[valEnd] != '}') valEnd++;
                    value = obj.Substring(v, valEnd - v).Trim();
                    i = valEnd;
                }
                map[key] = value;

                int next = obj.IndexOf(',', i);
                if (next < 0) break;
                i = next + 1;
            }
            return map;
        }

        private static int FindStringEnd(string s, int from)
        {
            bool escaped = false;
            for (int i = from; i < s.Length; i++)
            {
                char c = s[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') return i;
            }
            return -1;
        }

        private static int SkipComposite(string s, int at)
        {
            char open = s[at];
            char close = open == '[' ? ']' : '}';
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = at; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static string Unescape(string raw)
        {
            if (raw.IndexOf('\\') < 0) return raw;
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c != '\\' || i + 1 >= raw.Length) { sb.Append(c); continue; }
                i++;
                char n = raw[i];
                if (n == 'n') sb.Append('\n');
                else if (n == 't') sb.Append('\t');
                else if (n == 'r') sb.Append('\r');
                else if (n == 'u' && i + 4 < raw.Length)
                {
                    int code;
                    if (int.TryParse(raw.Substring(i + 1, 4), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                }
                else sb.Append(n);
            }
            return sb.ToString();
        }

        private static string Get(Dictionary<string, string> map, string key)
        {
            string v;
            return map.TryGetValue(key, out v) ? v : "";
        }

        private static long ParseLong(string raw)
        {
            long v;
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static double ParseDouble(string raw)
        {
            double v;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        private static Dictionary<string, string> ReadKeyValueFile(string path)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0) continue;
                    map[lines[i].Substring(0, eq).Trim()] = lines[i].Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex) { Program.Log("读元数据失败 " + path + ": " + ex.Message); }
            return map;
        }

        // ───────────────────────────────────────────────────── MP4 atom 探测
        //
        // 只读需要的几个盒子，不做完整解析：
        //   moov > mvhd        timescale + duration
        //   moov > trak > tkhd 宽高（16.16 定点的高 16 位）
        //   moov > trak > mdia > minf > stbl > stts  采样数 → 帧率
        // moov 可能在文件末尾（未 faststart），所以两种位置都要找。

        internal static void ProbeFile(AnimationInfo info)
        {
            if (info == null || info.Path == null) return;
            try
            {
                FileInfo fi = new FileInfo(info.Path);
                if (fi.Exists) info.Bytes = fi.Length;
            }
            catch { /* 拿不到大小不影响播放 */ }

            try
            {
                using (FileStream fs = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long moovAt = FindTopLevel(fs, "moov");
                    if (moovAt < 0) return;
                    fs.Position = moovAt;
                    byte[] header = new byte[8];
                    if (fs.Read(header, 0, 8) < 8) return;
                    long moovSize = ReadU32(header, 0);
                    long bodyAt = moovAt + 8;
                    long bodyEnd = moovAt + moovSize;
                    if (moovSize <= 8 || bodyEnd > fs.Length) bodyEnd = fs.Length;

                    ReadMvhd(fs, bodyAt, bodyEnd, info);
                    ReadTrak(fs, bodyAt, bodyEnd, info);
                }
            }
            catch (Exception ex)
            {
                Program.Log("探测媒体信息失败 " + Path.GetFileName(info.Path) + ": " + ex.Message);
            }
        }

        private static long FindTopLevel(FileStream fs, string type)
        {
            fs.Position = 0;
            byte[] header = new byte[8];
            long limit = fs.Length;
            while (fs.Position + 8 <= limit)
            {
                long at = fs.Position;
                if (fs.Read(header, 0, 8) < 8) break;
                long size = ReadU32(header, 0);
                string box = Encoding.ASCII.GetString(header, 4, 4);
                if (box == type) return at;
                if (size == 1)
                {
                    // 64 位长度：紧跟 8 字节 largesize
                    byte[] large = new byte[8];
                    if (fs.Read(large, 0, 8) < 8) break;
                    size = (long)ReadU32(large, 0) << 32 | ReadU32(large, 4);
                }
                if (size < 8) break;           // 坏盒子，停止而不是无限循环
                fs.Position = at + size;
            }
            return -1;
        }

        private static void ReadMvhd(FileStream fs, long from, long to, AnimationInfo info)
        {
            long at = FindBox(fs, from, to, new string[] { "mvhd" });
            if (at < 0) return;
            byte[] box = ReadBox(fs, at, to);
            if (box == null || box.Length < 20) return;
            int version = box[0];
            int p = 4;
            long timescale;
            long duration;
            if (version == 1)
            {
                // creation(8) modification(8) timescale(4) duration(8)
                if (box.Length < p + 8 + 8 + 4 + 8) return;
                p += 16;
                timescale = ReadU32(box, p); p += 4;
                duration = (long)ReadU32(box, p) << 32 | ReadU32(box, p + 4);
            }
            else
            {
                if (box.Length < p + 4 + 4 + 4 + 4) return;
                p += 8;
                timescale = ReadU32(box, p); p += 4;
                duration = ReadU32(box, p);
            }
            if (timescale > 0 && duration > 0) info.DurationSeconds = (double)duration / timescale;
        }

        private static void ReadTrak(FileStream fs, long from, long to, AnimationInfo info)
        {
            // 可能有多个 trak（视频 + 音频）；取第一个带有效宽高的。
            long at = from;
            while (at >= 0 && at < to)
            {
                long trak = FindBox(fs, at, to, new string[] { "trak" });
                if (trak < 0) return;
                long trakEnd = trak + ReadBoxSize(fs, trak, to);

                long tkhd = FindBox(fs, trak + 8, trakEnd, new string[] { "tkhd" });
                if (tkhd >= 0)
                {
                    byte[] box = ReadBox(fs, tkhd, trakEnd);
                    if (box != null && box.Length >= 4)
                    {
                        int version = box[0];
                        int p = 4 + (version == 1 ? 8 + 8 : 4 + 4);
                        p += 4;                       // track id
                        p += 4;                       // reserved
                        p += (version == 1 ? 8 : 4);  // duration
                        p += 8;                       // reserved
                        p += 2 + 2 + 2 + 2;           // layer/altgroup/volume/reserved
                        p += 36;                      // matrix
                        if (box.Length >= p + 8)
                        {
                            long width = ReadU32(box, p);
                            long height = ReadU32(box, p + 4);
                            int w = (int)(width >> 16);
                            int h = (int)(height >> 16);
                            if (w > 0 && h > 0)
                            {
                                info.Width = w;
                                info.Height = h;
                            }
                        }
                    }
                }

                // 帧率：stts 的采样总数 / 时长
                long stts = FindBoxDeep(fs, trak + 8, trakEnd, new string[] { "mdia", "minf", "stbl", "stts" });
                if (stts >= 0 && info.DurationSeconds > 0)
                {
                    byte[] box = ReadBox(fs, stts, trakEnd);
                    if (box != null && box.Length >= 8)
                    {
                        long count = ReadU32(box, 4);          // entry_count
                        long samples = 0;
                        int p = 8;
                        for (long i = 0; i < count && p + 8 <= box.Length; i++)
                        {
                            samples += ReadU32(box, p);        // sample_count
                            p += 8;
                        }
                        if (samples > 0) info.Fps = samples / info.DurationSeconds;
                    }
                }

                if (info.Width > 0 && info.Height > 0) return;
                at = trakEnd;
            }
        }

        /// <summary>按路径逐层找盒子，返回最内层盒子的起始偏移。</summary>
        private static long FindBoxDeep(FileStream fs, long from, long to, string[] path)
        {
            long at = from;
            for (int i = 0; i < path.Length; i++)
            {
                long found = FindBox(fs, at, to, new string[] { path[i] });
                if (found < 0) return -1;
                if (i == path.Length - 1) return found;
                at = found + 8;
                long size = ReadBoxSize(fs, found, to);
                if (size <= 8) return -1;
                to = found + size;
            }
            return -1;
        }

        private static long FindBox(FileStream fs, long from, long to, string[] names)
        {
            fs.Position = from;
            byte[] header = new byte[8];
            while (fs.Position + 8 <= to)
            {
                long at = fs.Position;
                if (fs.Read(header, 0, 8) < 8) break;
                long size = ReadU32(header, 0);
                string box = Encoding.ASCII.GetString(header, 4, 4);
                for (int i = 0; i < names.Length; i++)
                {
                    if (box == names[i]) return at;
                }
                if (size < 8) break;
                fs.Position = at + size;
            }
            return -1;
        }

        private static long ReadBoxSize(FileStream fs, long at, long limit)
        {
            try
            {
                fs.Position = at;
                byte[] header = new byte[8];
                if (fs.Read(header, 0, 8) < 8) return limit - at;
                long size = ReadU32(header, 0);
                if (size < 8) return limit - at;
                return size;
            }
            catch { return limit - at; }
        }

        private static byte[] ReadBox(FileStream fs, long at, long limit)
        {
            long size = ReadBoxSize(fs, at, limit);
            long bodyLen = size - 8;
            if (bodyLen <= 0 || bodyLen > 1 << 20) return null;   // 超过 1MB 的盒子不是我们要的
            fs.Position = at + 8;
            byte[] body = new byte[bodyLen];
            int read = fs.Read(body, 0, (int)bodyLen);
            if (read < bodyLen)
            {
                byte[] shorter = new byte[read];
                Array.Copy(body, shorter, read);
                return shorter;
            }
            return body;
        }

        private static long ReadU32(byte[] b, int at)
        {
            if (at + 4 > b.Length) return 0;
            return ((long)b[at] << 24) | ((long)b[at + 1] << 16) | ((long)b[at + 2] << 8) | b[at + 3];
        }
    }
}
