using System;
using System.Threading;
using System.Windows.Forms;

namespace ConfigHost
{
    internal static class Program
    {
        // 单实例（设计第十三节）：两个实例同时写同一个 Config.ps1 会互相覆盖。
        private const string MutexName = @"Local\DshConfigHost.SingleInstance";

        // 第二个实例通过这个命名事件通知第一个"把自己带到前台"。
        // 不能由第二个进程去 SetForegroundWindow 第一个的窗口 —— 前台锁会挡掉；
        // 必须由窗口自己在自己的 UI 线程上激活。
        internal const string ActivateEventName = @"Local\DshConfigHost.Activate";

        [STAThread]
        private static void Main()
        {
            // 诊断开关放在单实例检查之前：它们既不该被锁拦住，
            // 也不该在窗口已开着时先弹一个模态框把自检挡掉。
            if (RunDiagnostics()) return;

            bool createdNew;
            var mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // 已有实例：叫醒它，本进程静默退出。不弹任何框。
                try
                {
                    using (var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName))
                    {
                        ev.Set();
                    }
                }
                catch { }
                return;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    Application.Run(new HostWindow());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("启动失败：" + ex.Message, "DSH 配置",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose();
            }
        }

        /// <summary>
        /// 无界面诊断入口。返回 true 表示本次进程只做诊断、不开窗口。
        /// 保留在产品里是有意的：将来排查「装错目录」只需一个命令行参数，
        /// 比读日志便宜，且完全不改变正常使用行为。
        /// </summary>
        private static bool RunDiagnostics()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length < 2) return false;

            switch (args[1])
            {
                case "--page-test":
                    Environment.Exit(PageTest.Run(false));
                    return true;

                case "--page-test-write":
                    Environment.Exit(PageTest.Run(true));
                    return true;

                case "--selftest":
                    Environment.Exit(SelfTest.Run());
                    return true;

                case "--detect-app":
                    RunDetectApp(args);
                    Environment.Exit(0);          // 不要继续往下开窗
                    return true;

                case "--detect-app-bridge":
                    // 完全复现页面的调用方式：HostBridge.DetectEdgeApp("{}")
                    // 用来对比「命令行直调」与「页面走桥」两条路径的差异
                    Console.WriteLine(new HostBridge(null).DetectEdgeApp("{}"));
                    Environment.Exit(0);
                    return true;

                case "--openurl-test":
                    // 验证 OpenUrl 的接受/拒绝边界，不真的打开浏览器
                    // （合法 http 用例用 -dry 跳过实际启动）
                    RunOpenUrlTest(args);
                    Environment.Exit(0);
                    return true;

                case "--openmode-test":
                    RunOpenModeTest(args);
                    Environment.Exit(0);
                    return true;

                case "--print-env":
                    Console.WriteLine("installRoot=" + AppEnv.InstallRoot);
                    Console.WriteLine("hostDir=" + AppEnv.HostDir);
                    Console.WriteLine("configFile=" + AppEnv.ConfigFile);
                    Console.WriteLine("configExists=" + AppEnv.ConfigExists);
                    Console.WriteLine("startupDir=" + AppEnv.StartupDir);
                    Console.WriteLine("defaultsPath=" + AppEnv.DefaultsPath);
                    Console.WriteLine("hostVersion=" + AppEnv.HostVersion);
                    return true;
            }
            return false;
        }

        /// <summary>
        /// --detect-app [host] [port]
        /// 独立验证 Edge 网页应用检测，不牵涉界面。
        /// 端口省略时从 DshLauncherDaemon_Config.ps1 读 CONFIG_PORT。
        /// </summary>
        private static void RunDetectApp(string[] args)
        {
            const string NL = "\n";
            string host = args.Length >= 3 ? args[2] : "127.0.0.1";
            int port = 0;
            if (args.Length >= 4) int.TryParse(args[3], out port);

            var all = EdgeAppDetector.Scan();
            Console.WriteLine("Sessions 里解析出的应用：" + all.Count + " 个");
            foreach (var a in all)
            {
                Console.WriteLine("   " + a.AppId + "  url=" + a.Url + "  name=" + a.Name);
            }

            int usePort = port > 0 ? port : ReadPort();
            Console.WriteLine("usePort=" + usePort);

            var hit = EdgeAppDetector.FindByHostPort(host, usePort);
            Console.WriteLine("");
            if (hit != null)
            {
                Console.WriteLine("HIT  host=" + host + " port=" + usePort
                    + NL + "  appId=" + hit.AppId + NL + "  name=" + hit.Name + NL + "  url=" + hit.Url);
            }
            else
            {
                Console.WriteLine("MISS host=" + host + " port=" + usePort);
            }
            string exe = EdgeAppDetector.FindEdgeExe();
            Console.WriteLine("edgeExe=" + (exe ?? "(未找到)"));
        }

        /// <summary>
        /// --openurl-test
        /// 只验证 OpenUrl 的接受/拒绝边界：拒绝类用例不会启动任何程序，
        /// 接受类用例只打印「会打开」而不真的打开，避免干扰使用中的电脑。
        /// </summary>
        private static void RunOpenUrlTest(string[] args)
        {
            var bridge = new HostBridge(null);
            string[] cases =
            {
                "{\"url\":\"javascript:alert(1)\"}",
                "{\"url\":\"file:///C:/Windows\"}",
                "{\"url\":\"\"}",
                "{\"nourl\":1}",
                "{\"url\":\"http://127.0.0.1:3080/\\r\\nx\"}",
                "{\"url\":\"not a url\"}",
                "{\"url\":\"http://127.0.0.1:3080/\"}"
            };
            foreach (var c in cases)
            {
                string r;
                bool onlyLast = c.IndexOf("127.0.0.1:3080") >= 0 && c.IndexOf("\\r\\n") < 0;
                if (onlyLast && args.Length >= 3 && args[2] == "real")
                {
                    r = bridge.OpenUrl(c);
                }
                else if (onlyLast)
                {
                    r = "(跳过实际打开；加 real 参数才真开) " + c;
                }
                else
                {
                    r = bridge.OpenUrl(c);
                }
                Console.WriteLine("  " + c.PadRight(52) + " -> " + r);
            }
        }

        /// <summary>
        /// --openmode-test [restore]
        /// 直接测 GetOpenMode / SetOpenMode 两个桥方法，不经页面。
        /// 默认测试完把原值写回去。
        /// </summary>
        private static void RunOpenModeTest(string[] args)
        {
            var bridge = new HostBridge(null);
            string before = bridge.GetOpenMode("{}");
            Console.WriteLine("before: " + before);

            // 32-char synthetic app-id in [a-p] range (the format Edge actually uses).
            // Not a real installed app on any machine; this only exercises the validation
            // path in SetOpenMode.
            string r1 = bridge.SetOpenMode("{\"mode\":\"app\",\"appId\":\"abcdefghijklmnopabcdefghijklmnop\"}");
            Console.WriteLine("set app   : " + r1);
            Console.WriteLine("after app : " + bridge.GetOpenMode("{}"));

            string r2 = bridge.SetOpenMode("{\"mode\":\"browser\",\"appId\":\"\"}");
            Console.WriteLine("set browser: " + r2);
            Console.WriteLine("after brws: " + bridge.GetOpenMode("{}"));

            // 还原
            if (args.Length < 3 || args[2] != "keep")
            {
                try
                {
                    var node = System.Text.Json.JsonDocument.Parse(before);
                    string m = node.RootElement.GetProperty("mode").GetString();
                    string a = node.RootElement.GetProperty("appId").GetString();
                    string rr = bridge.SetOpenMode("{\"mode\":\"" + m + "\",\"appId\":\"" + a + "\"}");
                    Console.WriteLine("restore   : " + rr);
                    Console.WriteLine("final     : " + bridge.GetOpenMode("{}"));
                }
                catch (Exception ex) { Console.WriteLine("restore failed: " + ex.Message); }
            }
        }

        private static int ReadPort()
        {
            try
            {
                // 不要依赖 AppEnv.InstallRoot：Debug 产物在 src\bin\Debug\... 下，
                // 反推出来的安装根是错的。这里按「向上找 ConfigHost 这一层」来定位。
                string dir = AppContext.BaseDirectory.TrimEnd('\\');
                string cfg = null;
                for (int up = 0; up < 6 && dir != null; up++)
                {
                    string parent = System.IO.Path.GetDirectoryName(dir);
                    if (parent == null) break;
                    if (string.Equals(System.IO.Path.GetFileName(dir), "ConfigHost", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg = System.IO.Path.Combine(parent, "DshLauncherDaemon_Config.ps1");
                        break;
                    }
                    dir = parent;
                }
                if (cfg == null || !System.IO.File.Exists(cfg)) return 0;

                string text = System.IO.File.ReadAllText(cfg, new System.Text.UTF8Encoding(false));
                var m = System.Text.RegularExpressions.Regex.Match(
                    text, @"^\s*\$CONFIG_PORT\s*=\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline);
                if (!m.Success) return 0;
                int v;
                return int.TryParse(m.Groups[1].Value, out v) ? v : 0;
            }
            catch { return 0; }
        }
    }
}