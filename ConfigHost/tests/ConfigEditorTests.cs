using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ConfigHost.Tests
{
    /// <summary>
    /// 配置写入保真的全部断言。夹具用内联文本，绝不用真实配置文件 ——
    /// 那个文件是用户数据，测试不许碰。
    ///
    /// 两条从实测里学到的教训，都体现在下面的用例里：
    ///   1) 值段不能用 \S+? 匹配 —— 它匹配不到带空格的引号路径，
    ///      会让 NODE_EXE / DSH_CLI / WORKSPACE_DIR 被静默跳过（见 T9 用例）。
    ///   2) 行尾锚点要允许可选的 \r —— 否则在 CRLF 文件上，
    ///      「行尾没有注释」的那些行整体匹配失败，同样静默跳过（见 CRLF 用例）。
    /// 真实配置文件是纯 LF，所以第 2 条在主路径上不会暴露，只在别人改用
    /// CRLF 编辑器保存过配置之后才炸 —— 正是最难查的那种。
    /// </summary>
    public class ConfigEditorTests
    {
        private static string Lf(params string[] lines) => string.Join("\n", lines);

        // 与真实文件同形态：LF、= 两侧对齐空格、行尾注释
        private static readonly string Fixture = Lf(
            "# ========== DSH Launcher Daemon 配置 ==========",
            "$CONFIG_PORT                       = 3080              # dsh web 默认端口",
            "$CONFIG_HOST                       = \"127.0.0.1\"",
            "$CONFIG_NO_OPEN                    = $true",
            "$CONFIG_NODE_EXE                   = \"D:\\Program Files\\nodejs\\node.exe\"   # 含空格",
            "$CONFIG_STARTUP_TIMEOUT_SEC        = 180",
            "");

        private static List<ConfigEditor.Edit> Edits(params ConfigEditor.Edit[] e) { return e.ToList(); }

        private static ConfigEditor.Edit E(string name, string kind, object v)
        {
            return new ConfigEditor.Edit(name, kind, v);
        }

        [Fact]
        public void T3_Apply_ChangesOnlyTheTargetLine()
        {
            var r = ConfigEditor.Apply(Fixture, Edits(E("CONFIG_PORT", "int", 3399)));

            Assert.Equal(1, r.Changed);
            var before = Fixture.Split('\n');
            var after = r.Text.Split('\n');
            Assert.Equal(before.Length, after.Length);

            var diffs = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
            Assert.Single(diffs);
            // 缩进、= 两侧空格、值后对齐空格、行尾注释，全部原样
            Assert.Equal("$CONFIG_PORT                       = 3399              # dsh web 默认端口",
                         after[diffs[0]]);
        }

        [Fact]
        public void T2_Apply_IsIdempotent()
        {
            var r = ConfigEditor.Apply(Fixture, Edits(
                E("CONFIG_PORT", "int", 3080),
                E("CONFIG_HOST", "str", "127.0.0.1"),
                E("CONFIG_NO_OPEN", "bool", true),
                E("CONFIG_NODE_EXE", "str", @"D:\Program Files\nodejs\node.exe")));

            Assert.Equal(4, r.Changed);
            Assert.Equal(Fixture, r.Text);
        }

        [Fact]
        public void T9_Apply_HandlesQuotedValueWithSpaces()
        {
            var r = ConfigEditor.Apply(Fixture,
                Edits(E("CONFIG_NODE_EXE", "str", @"D:\Program Files\nodejs 2\node.exe")));

            Assert.Equal(1, r.Changed);
            Assert.Contains(
                "$CONFIG_NODE_EXE                   = \"D:\\Program Files\\nodejs 2\\node.exe\"   # 含空格",
                r.Text.Split('\n'));
        }

        [Fact]
        public void CrlfFile_WithNoTrailingComment_StillMatches()
        {
            var crlf = string.Join("\r\n", new[]
            {
                "$CONFIG_PORT                       = 3080              # 有注释",
                "$CONFIG_HOST                       = \"127.0.0.1\"",          // 无注释：这条曾经整行匹配失败
                "$CONFIG_STARTUP_TIMEOUT_SEC        = 180"
            });

            var r = ConfigEditor.Apply(crlf, Edits(
                E("CONFIG_HOST", "str", "0.0.0.0"),
                E("CONFIG_STARTUP_TIMEOUT_SEC", "int", 300),
                E("CONFIG_PORT", "int", 3081)));

            Assert.Equal(3, r.Changed);
            Assert.Empty(r.Skipped);
            Assert.Contains("$CONFIG_HOST                       = \"0.0.0.0\"", r.Text);
            Assert.Contains("$CONFIG_STARTUP_TIMEOUT_SEC        = 300", r.Text);
            // 行尾风格不许被动过
            Assert.Equal(2, r.Text.Count(c => c == '\r'));
        }

        [Fact]
        public void T5_Apply_RendersBoolAsPowerShellLiterals()
        {
            var on = ConfigEditor.Apply(Fixture, Edits(E("CONFIG_NO_OPEN", "bool", false))).Text;
            Assert.Contains("$CONFIG_NO_OPEN                    = $false", on);

            var off = ConfigEditor.Apply(Fixture, Edits(E("CONFIG_NO_OPEN", "bool", true))).Text;
            Assert.Contains("$CONFIG_NO_OPEN                    = $true", off);

            // 字符串 "true"/"1" 也要当真（桥那边 JSON 解析出来可能是字符串）
            Assert.Contains("$CONFIG_NO_OPEN                    = $true",
                ConfigEditor.Apply(Fixture, Edits(E("CONFIG_NO_OPEN", "bool", "true"))).Text);
        }

        [Fact]
        public void T6_Apply_SkipsMissingVariable_WithoutAppending()
        {
            var r = ConfigEditor.Apply(Fixture, Edits(E("CONFIG_NOT_THERE", "int", 1)));
            Assert.Equal(0, r.Changed);
            Assert.Equal(Fixture, r.Text);                 // 一个字节都不许多
            Assert.Contains("CONFIG_NOT_THERE", string.Join(",", r.Skipped));
        }

        [Fact]
        public void T8_Apply_RejectsIllegalNameAndKind()
        {
            Assert.Throws<ArgumentException>(() =>
                ConfigEditor.Apply(Fixture, Edits(E("evil; calc", "int", 1))));
            Assert.Throws<ArgumentException>(() =>
                ConfigEditor.Apply(Fixture, Edits(E("CONFIG_PORT", "script", 1))));
            Assert.Throws<ArgumentException>(() =>
                ConfigEditor.Apply(Fixture, Edits(E("PORT", "int", 1))));        // 少了 CONFIG_ 前缀
            Assert.Throws<ArgumentException>(() =>
                ConfigEditor.Apply(Fixture, Edits(E("CONFIG_HOST", "str", "a\nb"))));  // 值含换行
        }

        [Fact]
        public void T4_EncodeDecode_PreservesBomAndLineEndings()
        {
            byte[] withBom = ConfigEditor.Encode(Fixture, true);
            Assert.Equal(0xEF, withBom[0]);
            Assert.Equal(0xBB, withBom[1]);
            Assert.Equal(0xBF, withBom[2]);

            bool hasBom;
            var text = ConfigEditor.Decode(withBom, out hasBom);
            Assert.True(hasBom);
            Assert.Equal(Fixture, text);

            byte[] plain = ConfigEditor.Encode(Fixture, false);
            Assert.NotEqual(0xEF, plain[0]);
            Assert.Equal(Fixture, ConfigEditor.Decode(plain, out hasBom));
            Assert.False(hasBom);

            // 行尾绝不转换：LF 文件写完仍是 LF
            Assert.Equal(0, ConfigEditor.Encode(Fixture, false).Count(b => b == (byte)'\r'));
            var crlf = Fixture.Replace("\n", "\r\n");
            Assert.Equal(crlf.Count(c => c == '\r'),
                         ConfigEditor.Encode(crlf, false).Count(b => b == (byte)'\r'));
        }

        [Fact]
        public void Apply_ReplacesFirstOccurrenceOnly()
        {
            // 语义声明：每个字段只替换第一处匹配（设计第七节第 3 条）。
            // 真实文件里每个变量只出现一次，两种语义等价；这条锁住的是畸形文件上的行为。
            var two = Fixture + "$CONFIG_PORT                       = 7777\n";
            var r = ConfigEditor.Apply(two, Edits(E("CONFIG_PORT", "int", 3080)));
            Assert.Equal(1, r.Changed);
            Assert.Contains("7777", r.Text);        // 第二处保持不动
            Assert.Contains("$CONFIG_PORT                       = 3080              # dsh web 默认端口",
                            r.Text.Split('\n'));    // 第一处已改
        }

        [Fact]
        public void Apply_KeepsOtherLinesByteIdentical_WhenMultipleEdits()
        {
            var r = ConfigEditor.Apply(Fixture, Edits(
                E("CONFIG_PORT", "int", 3081),
                E("CONFIG_STARTUP_TIMEOUT_SEC", "int", 240)));

            var b = Fixture.Split('\n');
            var a = r.Text.Split('\n');
            Assert.Equal(2, r.Changed);
            Assert.Equal(b.Length, a.Length);
            // 只有两行变了，注释行与空行都还在原位
            var diffs = Enumerable.Range(0, b.Length).Where(i => b[i] != a[i]).ToList();
            Assert.Equal(2, diffs.Count);
            Assert.Equal(1, diffs[0]);   // PORT 在第 2 行（索引 1）
            Assert.Equal(5, diffs[1]);   // STARTUP_TIMEOUT 在第 6 行（索引 5）
        }
    }
}