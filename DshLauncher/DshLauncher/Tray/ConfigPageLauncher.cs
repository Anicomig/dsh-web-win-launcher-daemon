// Tray\ConfigPageLauncher.cs - opens the configuration window.
//
// The page is hosted by ConfigHost\DshConfigHost.exe (WinForms + WebView2).
// The host discovers absolute paths from its own location, so the tray no
// longer has to maintain a paths.js side channel, and no browser or custom
// protocol is involved any more.
//
// Contract unchanged: never throws; every failure comes back as a message
// the tray can show.

using System;
using System.Diagnostics;
using System.IO;

namespace DshLauncher.Tray
{
    internal static class ConfigPageLauncher
    {
        private const string HostDirName = "ConfigHost";
        private const string HostExeName = "DshConfigHost.exe";

        public static string OpenConfigPage(string installRoot, string trayExePath)
        {
            try
            {
                if (string.IsNullOrEmpty(installRoot) || !Directory.Exists(installRoot))
                {
                    return "找不到安装目录: " + installRoot;
                }

                string hostExe = Path.Combine(installRoot, HostDirName, HostExeName);
                if (!File.Exists(hostExe))
                {
                    return "找不到 " + HostDirName + "\\" + HostExeName
                         + "（DshConfigHost 未编译，请先运行 ConfigHost\build.cmd）";
                }

                ProcessStartInfo si = new ProcessStartInfo();
                si.FileName = hostExe;
                si.WorkingDirectory = Path.GetDirectoryName(hostExe);
                si.UseShellExecute = true;
                Process.Start(si);

                return "已打开配置窗口";
            }
            catch (Exception ex)
            {
                return "打开配置窗口失败: " + ex.Message;
            }
        }
    }
}
