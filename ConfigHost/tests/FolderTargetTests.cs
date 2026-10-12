using System;
using System.IO;
using Xunit;

namespace ConfigHost.Tests
{
    /// <summary>
    /// 「打开所在文件夹」的路径解析。纯逻辑，不启动 explorer，
    /// 所以可以放心测 —— 启动那行由人工点一次确认。
    /// </summary>
    public class FolderTargetTests
    {
        private static readonly string Tmp = Path.Combine(
            Path.GetTempPath(), "dsh-foldertarget-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        private string MakeDir(string name)
        {
            Directory.CreateDirectory(Tmp);
            var p = Path.Combine(Tmp, name);
            Directory.CreateDirectory(p);
            return p;
        }

        [Fact]
        public void Directory_OpensItself()
        {
            var d = MakeDir("aDir");
            string dir, err;
            Assert.True(FolderTarget.TryResolve(d, out dir, out err));
            Assert.Equal(d, dir);
            Assert.Null(err);
        }

        [Fact]
        public void File_OpensItsParent()
        {
            var d = MakeDir("bDir");
            var f = Path.Combine(d, "Config.ps1");
            File.WriteAllText(f, "x");
            string dir, err;
            Assert.True(FolderTarget.TryResolve(f, out dir, out err));
            Assert.Equal(d, dir);   // 用户要的就是这个语义
        }

        [Fact]
        public void FileWithSpacesInPath_StillResolves()
        {
            var d = Path.Combine(MakeDir("cDir"), "Program Files");
            Directory.CreateDirectory(d);
            var f = Path.Combine(d, "node.exe");
            File.WriteAllText(f, "x");
            string dir, err;
            Assert.True(FolderTarget.TryResolve(f, out dir, out err));
            Assert.Equal(d, dir);
        }

        [Fact]
        public void Nonexistent_IsRejected()
        {
            string dir, err;
            Assert.False(FolderTarget.TryResolve(
                Path.Combine(Path.GetTempPath(), "definitely-not-here-12345"), out dir, out err));
            Assert.Null(dir);
            Assert.Contains("不存在", err);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("C:\\a\r\nb")]
        public void BadInput_IsRejected(string p)
        {
            string dir, err;
            Assert.False(FolderTarget.TryResolve(p, out dir, out err));
            Assert.Null(dir);
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void Overlong_IsRejected()
        {
            string dir, err;
            Assert.False(FolderTarget.TryResolve(new string('a', 2000), out dir, out err));
            Assert.Contains("过长", err);
        }
    }
}