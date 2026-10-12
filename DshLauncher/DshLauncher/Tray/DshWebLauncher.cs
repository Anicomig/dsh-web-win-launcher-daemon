// Tray\DshWebLauncher.cs - starts dsh web directly from the tray.
//
// The tray's "dsh web" menu item has three states; this file implements the third
// one. When neither the daemon nor dsh web is running, the item starts dsh web on
// its own, so the user does not have to start the daemon just to get the web UI.
//
// The launch deliberately mirrors Start-DshWeb in DshLauncherDaemon.ps1, so a
// tray-started instance is indistinguishable from a daemon-started one:
//
//   <node> "<dsh bin.js>" web --host <host> --port <port> [--no-open] [extra...]
//   cwd  = $CONFIG_WORKSPACE_DIR, or the daemon directory when that is missing
//          (the same fallback the daemon applies)
//   env  = DSH_HOME and DSH_PROFILE_DIR pinned in the child environment
//
// Every parameter is re-read from DshLauncherDaemon_Config.ps1 on each call, so
// editing that file takes effect without restarting the tray.
//
// Why stdout is deliberately NOT redirected: the daemon redirects into
// log\web_<ts>.log because it owns the child for the child's whole lifetime. A
// tray that redirected would hand the child a pipe held by the tray process, and
// exiting the tray ("仅退出 Launcher" must leave dsh web running) would break that
// pipe under a still-running node process. dsh keeps its own structured log under
// %DSH_HOME%\logs\, so no diagnostics are lost.
//
// Language level: C# 5 / .NET Framework 4.0. No null-conditional operators, no
// string interpolation, no expression bodied members, no nameof.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DshLauncher.Tray
{
    internal static class DshWebLauncher
    {
        private const string DaemonConfigFileName = "DshLauncherDaemon_Config.ps1";

        private const int FallbackPort = 3080;
        private const string FallbackHost = "127.0.0.1";
        private const string FallbackProfile = "web";

        /// <summary>
        /// Starts dsh web and returns a human readable result. Never throws: every
        /// failure comes back as a message the caller can display.
        /// </summary>
        /// <param name="daemonDirectory">
        /// Directory holding DshLauncherDaemon_Config.ps1. Located from the JSON
        /// config, so it follows the daemon wherever the folder is moved.
        /// </param>
        public static string StartDshWeb(string daemonDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(daemonDirectory))
                {
                    return "启动 dsh web 失败: 尚未确定守护进程目录";
                }

                string configPs1 = Path.Combine(daemonDirectory, DaemonConfigFileName);
                if (!File.Exists(configPs1))
                {
                    return "找不到 " + DaemonConfigFileName + "，无法启动 dsh web";
                }

                string text = ReadAllText(configPs1);

                string nodeExe = ReadStringValue(text, "CONFIG_NODE_EXE");
                string dshCli = ReadStringValue(text, "CONFIG_DSH_CLI");
                string dshHome = ReadStringValue(text, "CONFIG_DSH_HOME");
                string profile = ReadStringValue(text, "CONFIG_DSH_PROFILE");
                string workspace = ReadStringValue(text, "CONFIG_WORKSPACE_DIR");
                string host = ReadStringValue(text, "CONFIG_HOST");
                int port = ReadIntValue(text, "CONFIG_PORT");
                bool noOpen = ReadBoolValue(text, "CONFIG_NO_OPEN", true);

                if (string.IsNullOrEmpty(nodeExe))
                {
                    return "找不到 node.exe: 配置里没有 $CONFIG_NODE_EXE";
                }
                if (!File.Exists(nodeExe))
                {
                    return "找不到 node.exe: " + nodeExe;
                }

                if (string.IsNullOrEmpty(dshCli))
                {
                    return "找不到 dsh 的 bin.js: 配置里没有 $CONFIG_DSH_CLI";
                }
                if (!File.Exists(dshCli))
                {
                    return "找不到 dsh 的 bin.js: " + dshCli;
                }

                if (string.IsNullOrEmpty(host)) host = FallbackHost;
                if (port <= 0) port = FallbackPort;
                if (string.IsNullOrEmpty(profile)) profile = FallbackProfile;

                // The daemon pins DSH_PROFILE_DIR explicitly; a wrong DSH_HOME is the
                // classic way to end up in a different harness home, so validate the
                // profile directory before launching rather than after.
                string profileDir = null;
                if (!string.IsNullOrEmpty(dshHome))
                {
                    profileDir = Path.Combine(Path.Combine(dshHome, "profiles"), profile);
                    if (!Directory.Exists(profileDir))
                    {
                        return "profile 目录不存在: " + profileDir;
                    }
                }

                // Same fallback the daemon uses: an unusable workspace must not stop
                // dsh web from starting.
                string workDir;
                if (!string.IsNullOrEmpty(workspace) && Directory.Exists(workspace))
                {
                    workDir = workspace;
                }
                else
                {
                    workDir = daemonDirectory;
                }

                List<string> args = new List<string>();
                args.Add(Quote(dshCli));
                args.Add("web");
                args.Add("--host");
                args.Add(host);
                args.Add("--port");
                args.Add(port.ToString(CultureInfo.InvariantCulture));
                if (noOpen)
                {
                    args.Add("--no-open");
                }

                List<string> extra = ReadStringList(text, "CONFIG_EXTRA_ARGS");
                for (int i = 0; i < extra.Count; i++)
                {
                    args.Add(Quote(extra[i]));
                }

                ProcessStartInfo si = new ProcessStartInfo();
                si.FileName = nodeExe;
                si.Arguments = string.Join(" ", args.ToArray());
                si.WorkingDirectory = workDir;
                si.WindowStyle = ProcessWindowStyle.Hidden;

                // UseShellExecute must be false, otherwise EnvironmentVariables is
                // ignored. The daemon achieves the same thing by setting $env: in its
                // own process and letting the child inherit it.
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                if (!string.IsNullOrEmpty(dshHome))
                {
                    si.EnvironmentVariables["DSH_HOME"] = dshHome;
                }
                if (!string.IsNullOrEmpty(profileDir))
                {
                    si.EnvironmentVariables["DSH_PROFILE_DIR"] = profileDir;
                }

                Process child = Process.Start(si);
                if (child == null)
                {
                    return "启动 dsh web 失败: 进程未能创建";
                }

                int pid = 0;
                try { pid = child.Id; }
                catch { }
                try { child.Dispose(); }
                catch { }

                return "已启动 dsh web (端口 "
                    + port.ToString(CultureInfo.InvariantCulture)
                    + "，PID " + pid.ToString(CultureInfo.InvariantCulture) + ")";
            }
            catch (Exception ex)
            {
                return "启动 dsh web 失败: " + ex.Message;
            }
        }

        // ---- config reading ------------------------------------------------

        private static string ReadAllText(string path)
        {
            try
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Fall back to the ambient encoding when the strict read fails.
                return File.ReadAllText(path);
            }
        }

        // $CONFIG_NAME = "value"
        private static string ReadStringValue(string text, string name)
        {
            Match m = Regex.Match(text,
                "\\$\\s*" + name + "\\s*=\\s*\"([^\"]*)\"",
                RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }

        // $CONFIG_NAME = 3080
        private static int ReadIntValue(string text, string name)
        {
            Match m = Regex.Match(text,
                "\\$\\s*" + name + "\\s*=\\s*(\\d+)",
                RegexOptions.Multiline);
            if (!m.Success)
            {
                return 0;
            }

            int value;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value))
            {
                return 0;
            }
            return value;
        }

        // $CONFIG_NAME = $true / $false
        //
        // defaultWhenMissing exists because a missing $CONFIG_NO_OPEN must not be
        // read as "false": that would pop a browser window open on a tray-initiated
        // start, and the menu already has a dedicated "打开网页" entry for that.
        private static bool ReadBoolValue(string text, string name, bool defaultWhenMissing)
        {
            Match m = Regex.Match(text,
                "\\$\\s*" + name + "\\s*=\\s*\\$(true|false)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!m.Success)
            {
                return defaultWhenMissing;
            }
            return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        // $CONFIG_NAME = @("a", "b")  -> ["a", "b"]
        private static List<string> ReadStringList(string text, string name)
        {
            List<string> result = new List<string>();

            Match m = Regex.Match(text,
                "\\$\\s*" + name + "\\s*=\\s*@\\s*\\(([^)]*)\\)",
                RegexOptions.Multiline);
            if (!m.Success)
            {
                return result;
            }

            MatchCollection quoted = Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\"");
            for (int i = 0; i < quoted.Count; i++)
            {
                string value = quoted[i].Groups[1].Value;
                if (value.Length > 0)
                {
                    result.Add(value);
                }
            }
            return result;
        }

        // Config values are filesystem paths and switch names; they never contain a
        // double quote, so plain wrapping is sufficient for the Win32 command line.
        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }
    }
}