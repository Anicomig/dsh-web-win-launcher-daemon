using System;
using System.IO;

namespace ConfigHost
{
    /// <summary>
    /// 把「用户给的一个路径」解析成「该在资源管理器里打开哪个目录」：
    /// 目录就开它自己，文件就开它的上级目录。
    ///
    /// 单独成文件、纯逻辑、不碰进程 —— 这样它能被 xUnit 直接测。
    /// 真正启动 explorer.exe 的那一行留给人工点一次确认，
    /// 因为自动化去开 Explorer 窗口是收不干净的（窗口由共享的 explorer
    /// 进程承载，按标题匹配关不准，删掉目标目录还会留下错误对话框）。
    /// </summary>
    internal static class FolderTarget
    {
        public static bool TryResolve(string path, out string dir, out string error)
        {
            dir = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path)) { error = "路径为空"; return false; }
            if (path.Length > 1024) { error = "路径过长"; return false; }
            if (path.IndexOf('\r') >= 0 || path.IndexOf('\n') >= 0) { error = "路径含换行"; return false; }

            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) { error = "路径不合法：" + ex.Message; return false; }

            try
            {
                if (Directory.Exists(full)) dir = full;
                else if (File.Exists(full)) dir = Path.GetDirectoryName(full);
                else { error = "路径不存在：" + full; return false; }
            }
            catch (Exception ex2) { error = "解析失败：" + ex2.Message; return false; }

            if (string.IsNullOrEmpty(dir)) { error = "没有可打开的上级目录"; return false; }
            if (!Directory.Exists(dir)) { error = "所在文件夹不存在：" + dir; return false; }
            return true;
        }
    }
}