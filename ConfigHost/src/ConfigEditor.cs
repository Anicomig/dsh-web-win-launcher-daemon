using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ConfigHost
{
    /// <summary>
    /// 对 DshLauncherDaemon_Config.ps1 做字节保真的"外科手术式"改写：
    /// 只替换 `$NAME = 值` 里的「值」那一段，其余字节（缩进、= 两侧空格、
    /// 值后的对齐空格、行尾注释、BOM、行尾风格）一律不动。
    ///
    /// 设计依据：docs\config-host.md「改配置」一节（早期内部规格文档已随仓库整理移除）。
    /// 全部语义由 ConfigHost\tests\ConfigEditorTests.cs 逐条锁死。
    /// </summary>
    internal static class ConfigEditor
    {
        private static readonly Regex NameRx =
            new Regex(@"^CONFIG_[A-Z0-9_]{1,40}$", RegexOptions.Compiled);

        private static readonly HashSet<string> AllowedKinds =
            new HashSet<string>(StringComparer.Ordinal) { "int", "str", "bool" };

        public sealed class Edit
        {
            public readonly string Name;    // CONFIG_PORT
            public readonly string Kind;    // int | str | bool
            public readonly object Value;

            public Edit(string name, string kind, object value)
            {
                Name = name; Kind = kind; Value = value;
            }
        }

        public sealed class Outcome
        {
            public readonly string Text;
            public readonly int Changed;
            public readonly List<string> Skipped;

            public Outcome(string text, int changed, List<string> skipped)
            {
                Text = text; Changed = changed; Skipped = skipped;
            }
        }

        // ---------------- BOM ----------------

        public static bool HasBom(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 3
                && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        public static string Decode(byte[] bytes, out bool hasBom)
        {
            hasBom = HasBom(bytes);
            var text = Encoding.UTF8.GetString(bytes ?? new byte[0]);
            // GetString 会把 BOM 解成 U+FEFF 字符，这里剥掉它，
            // 让上层拿到的文本与"有没有 BOM"这件事彻底解耦。
            if (hasBom) text = text.Substring(1);
            return text;
        }

        public static byte[] Encode(string text, bool hasBom)
        {
            var body = Encoding.UTF8.GetBytes(text ?? string.Empty);
            if (!hasBom) return body;
            var all = new byte[body.Length + 3];
            all[0] = 0xEF; all[1] = 0xBB; all[2] = 0xBF;
            Buffer.BlockCopy(body, 0, all, 3, body.Length);
            return all;
        }

        // ---------------- 值渲染 ----------------

        public static string Render(string kind, object value)
        {
            switch (kind)
            {
                case "bool":
                    return Truthy(value) ? "$true" : "$false";
                case "int":
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture)
                                   .ToString(CultureInfo.InvariantCulture);
                case "str":
                    return "\"" + (value ?? string.Empty).ToString() + "\"";
                default:
                    throw new ArgumentException("kind 非法: " + kind);
            }
        }

        private static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            var s = v.ToString();
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        // ---------------- 核心替换 ----------------

        /// <summary>
        /// 值段的三种形态：双引号串、单引号串（内部允许空格），或裸词（不允许空格）。
        ///
        /// 两个曾经踩过的坑，都由测试锁死：
        ///   · 用 \S+? 当值段 —— 匹配不到 "D:\Program Files\nodejs\node.exe" 这种
        ///     带空格的引号路径，那一行会被静默跳过，还照样返回 changed=1。
        ///   · 行尾锚点不允许可选的 \r —— CRLF 文件上「行尾没有注释」的行整体匹配失败，
        ///     同样静默跳过。真实配置文件是纯 LF 所以主路径看不出来，
        ///     等谁用记事本另存一次就炸。
        /// </summary>
        private static Regex BuildRegex(string name)
        {
            var pattern =
                "^(?<pre>[ \t]*\\$" + name + "[ \t]*=[ \t]*)" +
                "(?<val>\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s#][^ \t\\r\\n]*)" +
                "(?<pad>[ \t]*)(?<cmt>#.*)?\r?$";
            return new Regex(pattern, RegexOptions.Multiline);
        }

        public static Outcome Apply(string text, IEnumerable<Edit> edits)
        {
            if (text == null) throw new ArgumentNullException("text");

            var outText = text;
            var changed = 0;
            var skipped = new List<string>();

            foreach (var e in edits ?? new Edit[0])
            {
                if (e == null || string.IsNullOrEmpty(e.Name))
                    throw new ArgumentException("edit 缺少 name");
                if (!NameRx.IsMatch(e.Name))
                    throw new ArgumentException("name 非法: " + e.Name);
                if (!AllowedKinds.Contains(e.Kind))
                    throw new ArgumentException("kind 非法: " + e.Kind);
                if (e.Value == null)
                    throw new ArgumentException("value 不能为 null: " + e.Name);

                if (e.Kind == "str")
                {
                    var s = e.Value.ToString();
                    if (s != null && (s.IndexOf('\r') >= 0 || s.IndexOf('\n') >= 0))
                        throw new ArgumentException("值含换行: " + e.Name);
                }

                var m = BuildRegex(e.Name).Match(outText);
                if (!m.Success)
                {
                    // 文件里没有这一行：跳过，绝不新增行。
                    // 新增会把配置文件写得和安装脚本生成的模板不一致。
                    skipped.Add(e.Name);
                    continue;
                }

                var rendered = Render(e.Kind, e.Value);
                var g = m.Groups["val"];
                // 只替换值段本身，pre/pad/cmt 一个字节都不碰
                outText = outText.Substring(0, g.Index) + rendered
                        + outText.Substring(g.Index + g.Length);
                changed++;
            }

            return new Outcome(outText, changed, skipped);
        }

        // ---------------- 文件读写 ----------------

        public static string ReadFile(string path, out bool hasBom)
        {
            hasBom = false;
            return Decode(File.ReadAllBytes(path), out hasBom);
        }

        /// <summary>
        /// 原子写：先写临时文件再替换，避免断电/被杀时留下半截配置。
        /// 临时文件用唯一后缀，同目录内多次写不会互相撞。
        /// </summary>
        public static void WriteFileAtomically(string path, string text, bool hasBom)
        {
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                File.WriteAllBytes(tmp, Encode(text, hasBom));
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); }
                    catch { }
                }
            }
        }
    }
}
