using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ConfigHost
{
    /// <summary>
    /// 从 Microsoft Edge 的数据里找出「网页应用（PWA）」的 app-id。
    ///
    /// 为什么只能读、不能算：app-id 是 Edge 内部为应用分配的 128 位值，
    /// 展示时把每位十六进制 0-f 映射成 a-p，因此形如 32 个 a-p 字符。
    /// 实测用 URL 做 MD5/SHA1/SHA256/SHA512 都推算不出来，只能从 Edge 自己的
    /// 数据里读。
    ///
    /// 数据来源与取舍
    /// --------------
    ///   Preferences  -> web_app_install_metrics：只有 app-id，没有 URL
    ///   Preferences  -> web_apps.daily_metrics ：只有 URL，没有 app-id
    ///   Icon 目录     ：只有 app-id，没有 URL
    ///   **Sessions   ：app-id 与 URL 同时出现**  ← 唯一能把两者对上的来源
    ///
    /// Sessions 是 Edge 的会话恢复数据，只有「实际开过窗口」的应用才会留下记录。
    /// 这正好符合产品语义：没被打开过的应用，用户也不会想从托盘启动它。
    ///
    /// 只读，绝不写 Edge 的任何文件；读的时候必须声明 FileShare.ReadWrite，
    /// 否则 Edge 正在运行时会抛「文件被另一个进程占用」。
    /// </summary>
    internal static class EdgeAppDetector
    {
        /// <summary>app-id：32 个字符，字母只可能是 a-p。</summary>
        private static readonly Regex AppIdRx = new Regex("[a-p]{32}", RegexOptions.Compiled);

        /// <summary>在 app-id 之后就近找起始网址。</summary>
        private static readonly Regex UrlRx =
            new Regex("https?://[^\x00-\x1f\"]{4,120}", RegexOptions.Compiled);

        private const int NearbyBytes = 300;   // app-id 往后多远内找 URL
        private const long MaxScanBytes = 6L * 1024 * 1024;

        public sealed class EdgeApp
        {
            public string AppId = string.Empty;
            public string Url = string.Empty;
            public string Name = string.Empty;
        }

        /// <summary>
        /// 扫描 Sessions，得到 app-id -> URL 的映射。
        /// 失败（目录不存在 / 文件被锁）都静默跳过 —— 检测不到就用别的方式兜底，
        /// 不能因为读不了文件就报错。
        /// </summary>
        public static List<EdgeApp> Scan()
        {
            var result = new List<EdgeApp>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            string localAppData;
            try
            {
                localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            catch
            {
                return result;
            }
            if (string.IsNullOrEmpty(localAppData)) return result;

            string sessDir = Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Sessions");
            if (!Directory.Exists(sessDir)) return result;

            FileInfo[] files;
            try { files = new DirectoryInfo(sessDir).GetFiles(); }
            catch { return result; }

            foreach (var f in files)
            {
                ScanFile(f.FullName, result, seen);
            }
            return result;
        }

        private static void ScanFile(string path, List<EdgeApp> result, HashSet<string> seen)
        {
            FileStream fs = null;
            try
            {
                // 共享读取是必须的：Edge 正在运行时这些文件被独占占用
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long len = fs.Length;
                if (len <= 0) return;
                int toRead = (int)Math.Min(MaxScanBytes, len);

                var buf = new byte[toRead];
                int got = fs.Read(buf, 0, toRead);
                if (got <= 0) return;

                // 按 latin1 解码：保证「一个字节 = 一个字符」，正则里的 \x00-\x1f
                // 才能正确切出可打印串；中文虽显示成乱码但不影响 URL 与 app-id 提取
                string s = Encoding.Latin1.GetString(buf, 0, got);

                foreach (Match m in AppIdRx.Matches(s))
                {
                    string id = m.Value;

                    int start = m.Index;
                    int take = Math.Min(NearbyBytes, s.Length - start);
                    string seg = s.Substring(start, take);

                    var u = UrlRx.Match(seg);
                    string url = u.Success ? u.Value : string.Empty;
                    string name = string.Empty;
                    TryReadName(seg, ref name);

                    // 同一个 app-id 可能在多个 Session 文件里出现，且每次记录的
                    // 完整度不同（实测出现过 URL 被截断成 http://127.0.0.1:3 的
                    // 残缺值）。所以不按「先到先得」，而是保留信息最全的那条。
                    if (seen.Contains(id))
                    {
                        var prev = result.Find(x => x.AppId == id);
                        if (prev != null)
                        {
                            if (url.Length > prev.Url.Length) prev.Url = url;
                            if (string.IsNullOrEmpty(prev.Name) && !string.IsNullOrEmpty(name)) prev.Name = name;
                        }
                        continue;
                    }
                    seen.Add(id);
                    result.Add(new EdgeApp { AppId = id, Url = url, Name = name });
                }
            }
            catch
            {
                // 读不了就跳过：Sessions 里通常有多个文件，能读的那几个往往已经够用
            }
            finally
            {
                if (fs != null)
                {
                    try { fs.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// 名称紧跟在 start_url 之后，格式 0x12 &lt;长度&gt; &lt;UTF-8 名称&gt;。
        /// 尽力而为：拿不到就留空，不影响启动。
        /// </summary>
        private static void TryReadName(string seg, ref string name)
        {
            try
            {
                var u = UrlRx.Match(seg);
                if (!u.Success) return;
                int i = u.Index + u.Length;
                if (i + 2 > seg.Length) return;

                // 0x12 是 protobuf 里「字段 2、长度分隔」的标识
                if ((int)seg[i] != 0x12) return;
                int n = (int)seg[i + 1];
                if (n <= 0 || i + 2 + n > seg.Length) return;

                // seg 是 latin1 解码出来的，一个字符对应一个字节，
                // 先按 latin1 取回原始字节，再按 UTF-8 解出中文名
                byte[] bytes = Encoding.Latin1.GetBytes(seg.Substring(i + 2, n));
                name = Encoding.UTF8.GetString(bytes).Trim();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 按主机与端口找出对应的网页应用。
        /// 命中要求：URL 里包含 "host:port"。host 允许 127.0.0.1 / localhost 等写法。
        /// </summary>
        public static EdgeApp FindByHostPort(string host, int port)
        {
            if (port <= 0) return null;

            string want = host + ":" + port.ToString(CultureInfo.InvariantCulture);
            var apps = Scan();

            // 先找完整匹配（URL 形如 http://127.0.0.1:3080/）
            foreach (var a in apps)
            {
                if (string.IsNullOrEmpty(a.Url)) continue;
                if (a.Url.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    a.Url.IndexOf("/", StringComparison.Ordinal) > -1)
                {
                    return a;
                }
            }
            // 再放宽：只要包含 host:port 即可（有些记录里 URL 会被截断）
            foreach (var a in apps)
            {
                if (string.IsNullOrEmpty(a.Url)) continue;
                if (a.Url.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return a;
                }
            }
            return null;
        }

        /// <summary>定位 msedge.exe，找不到返回 null。</summary>
        public static string FindEdgeExe()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             "Microsoft", "Edge", "Application", "msedge.exe")
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }
    }
}
