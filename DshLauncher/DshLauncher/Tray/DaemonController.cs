// Tray\DaemonController.cs - daemon lifecycle actions used by the tray menu.
//
// Every member only returns a human-readable result string and never lets an
// exception escape: the caller decides how (or whether) to display it. There is
// deliberately no MessageBox and no UI dependency here beyond Process.Start.
//
// The configuration provider is invoked on EVERY operation, so the daemon path,
// port and log directory are always re-read from the JSON config in real time.
// It is never cached.

namespace DshLauncher.Tray
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Management;
    using System.Text.RegularExpressions;

    internal sealed class DaemonController
    {
        /// <summary>
        /// Re-read on every operation; must not be cached.
        /// </summary>
        private readonly Func<TrayConfig> _configProvider;

        /// <summary>
        /// Name of the daemon process image, without extension.
        /// </summary>
        public const string DaemonProcessName = "DshLauncherDaemon";

        public DaemonController(Func<TrayConfig> configProvider)
        {
            if (configProvider == null)
            {
                throw new ArgumentNullException("configProvider");
            }

            _configProvider = configProvider;
        }

        /// <summary>
        /// True when at least one process named DaemonProcessName is running.
        /// Any failure to enumerate processes is reported as "not running".
        /// </summary>
        public bool IsDaemonRunning()
        {
            Process[] procs = null;

            try
            {
                procs = Process.GetProcessesByName(DaemonProcessName);
                if (procs == null)
                {
                    return false;
                }

                return procs.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                DisposeAll(procs);
            }
        }

        /// <summary>
        /// Opens the daemon web UI:
        ///   - default browser when OPEN_MODE = browser
        ///   - Edge PWA app window when OPEN_MODE = app and an app-id is configured
        /// </summary>
        public string OpenWeb()
        {
            try
            {
                TrayConfig cfg = _configProvider();

                if (cfg.OpenMode == "app" && !string.IsNullOrEmpty(cfg.EdgeAppId))
                {
                    return OpenWebAsEdgeApp(cfg);
                }
                Process.Start(cfg.WebUrl);
                return "已打开 " + cfg.WebUrl;
            }
            catch (Exception ex)
            {
                return "打开网页失败: " + ex.Message;
            }
        }

        private string OpenWebAsEdgeApp(TrayConfig cfg)
        {
            // 沿用 README 推荐的 msedge.exe 候选路径；找不到回退到默认浏览器 + 提示
            string edge = LocateEdgeExe();
            if (edge == null)
            {
                Process.Start(cfg.WebUrl);
                return "未找到 msedge.exe，已用默认浏览器打开 " + cfg.WebUrl;
            }
            var psi = new ProcessStartInfo
            {
                FileName = edge,
                // --profile-directory=Default 与 daemon 一致；--app-id 走"已注册应用"路径
                // 会复用已开过的窗口（这是 PWA 的正常行为，文档坑 2 验证过）
                Arguments = "--profile-directory=Default --app-id=" + cfg.EdgeAppId,
                UseShellExecute = true
            };
            try
            {
                Process.Start(psi);
                return "已打开网页应用 " + (cfg.EdgeAppId.Length > 8 ? cfg.EdgeAppId.Substring(0, 8) + "..." : cfg.EdgeAppId);
            }
            catch (Exception ex)
            {
                // 启动失败回退到默认浏览器，不让用户被卡住
                Process.Start(cfg.WebUrl);
                return "用 Edge PWA 启动失败（" + ex.Message + "），已回退到默认浏览器";
            }
        }

        private static string LocateEdgeExe()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             "Microsoft", "Edge", "Application", "msedge.exe")
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>
        /// Starts the daemon executable without elevation and without waiting.
        /// UseShellExecute is used so the shell can resolve the image, but no
        /// elevation verb is requested: the daemon must start with no UAC prompt.
        /// </summary>
        public string StartDaemon()
        {
            try
            {
                TrayConfig cfg = _configProvider();

                if (!File.Exists(cfg.DaemonExePath))
                {
                    return "找不到守护进程: " + cfg.DaemonExePath;
                }

                if (IsDaemonRunning())
                {
                    return "守护进程已在运行";
                }

                ProcessStartInfo si = new ProcessStartInfo();
                si.FileName = cfg.DaemonExePath;
                si.WorkingDirectory = cfg.DaemonDirectory;
                si.UseShellExecute = true;

                Process.Start(si);
                return "已启动守护进程";
            }
            catch (Exception ex)
            {
                return "启动守护进程失败: " + ex.Message;
            }
        }

        /// <summary>
        /// Kills every process named DaemonProcessName and reports the outcome.
        /// Processes that cannot be killed (typically because the daemon runs at
        /// a higher integrity level) are counted and reported, not thrown.
        /// </summary>
        public string StopDaemon()
        {
            Process[] procs = null;

            try
            {
                procs = Process.GetProcessesByName(DaemonProcessName);
                if (procs == null || procs.Length == 0)
                {
                    return "守护进程未在运行";
                }

                int killed = 0;
                int failed = 0;

                for (int i = 0; i < procs.Length; i++)
                {
                    Process p = procs[i];

                    try
                    {
                        p.Kill();
                        killed++;
                    }
                    catch
                    {
                        failed++;
                    }

                    // Give the OS a moment to reap the process. This must never
                    // propagate: the kill result is already recorded above.
                    try
                    {
                        p.WaitForExit(3000);
                    }
                    catch
                    {
                    }
                }

                string message = "已结束守护进程 (" + killed.ToString() + " 个)";
                if (failed > 0)
                {
                    message += "，" + failed.ToString() + " 个失败（可能需要管理员权限）";
                }

                return message;
            }
            catch (Exception ex)
            {
                return "结束守护进程失败: " + ex.Message;
            }
            finally
            {
                DisposeAll(procs);
            }
        }

        /// <summary>
        /// Opens the configured log directory in Explorer, creating it first when
        /// it does not exist yet.
        /// </summary>
        public string OpenLogFolder()
        {
            try
            {
                TrayConfig cfg = _configProvider();

                if (!Directory.Exists(cfg.LogDirectory))
                {
                    try
                    {
                        Directory.CreateDirectory(cfg.LogDirectory);
                    }
                    catch
                    {
                        return "日志目录不存在: " + cfg.LogDirectory;
                    }
                }

                Process.Start("explorer.exe", "\"" + cfg.LogDirectory + "\"");
                return "已打开 " + cfg.LogDirectory;
            }
            catch (Exception ex)
            {
                return "打开日志目录失败: " + ex.Message;
            }
        }

        /// <summary>
        /// True when a dsh web node process is running. Used only for menu labelling.
        /// </summary>
        public bool IsDshWebRunning()
        {
            try
            {
                return FindDshWebProcessIds().Count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Kills the dsh web node process once.
        ///
        /// Note this is intentionally a ONE-SHOT kill: while the daemon is alive it
        /// polls port 3080 and will start a fresh node within its next poll, which is
        /// exactly what makes this action useful as a "restart dsh web". When the
        /// daemon is not running nothing brings node back, so the menu labels the same
        /// action "close dsh web" instead.
        /// </summary>
        public string StopDshWeb()
        {
            try
            {
                List<int> pids = FindDshWebProcessIds();
                if (pids.Count == 0)
                {
                    int wp = ConfiguredWebPort();
                        return wp > 0
                            ? ("端口 " + wp + " 上没有发现 dsh web 进程（其它端口上的实例不会被触碰）")
                            : "未发现运行中的 dsh web 进程";
                }

                int killed = 0;
                int failed = 0;

                for (int i = 0; i < pids.Count; i++)
                {
                    Process p = null;
                    try
                    {
                        p = Process.GetProcessById(pids[i]);
                        p.Kill();
                        try { p.WaitForExit(3000); }
                        catch { }
                        killed++;
                    }
                    catch
                    {
                        failed++;
                    }
                    finally
                    {
                        if (p != null)
                        {
                            try { p.Dispose(); }
                            catch { }
                        }
                    }
                }

                string message = "已结束 dsh web 进程 (" + killed + " 个)";
                if (failed > 0)
                {
                    message += "，" + failed + " 个失败（可能需要管理员权限）";
                }
                return message;
            }
            catch (Exception ex)
            {
                return "结束 dsh web 失败: " + ex.Message;
            }
        }

        /// <summary>
        /// Starts dsh web when neither the daemon nor dsh web is running.
        ///
        /// This is the third state of the tray's dsh web entry; the other two states
        /// both reuse StopDshWeb. It launches node directly rather than starting the
        /// daemon, which keeps the two menu entries independent: "启动 dsh web" leaves
        /// the daemon stopped, so the entry then reads "关闭 dsh web" until the user
        /// starts the daemon from its own entry.
        /// </summary>
        public string StartDshWeb()
        {
            try
            {
                TrayConfig cfg = _configProvider();
                return DshWebLauncher.StartDshWeb(cfg.DaemonDirectory);
            }
            catch (Exception ex)
            {
                return "启动 dsh web 失败: " + ex.Message;
            }
        }

        /// <summary>
        /// Locates dsh web node processes by two independent signals:
        ///
        ///   1. the process is a child of a live DshLauncherDaemon process - precise,
        ///      and the normal case;
        ///   2. the command line looks like the dsh CLI running the web profile -
        ///      the fallback for a node process orphaned when the daemon exited, since
        ///      its recorded parent PID then points at a dead process.
        ///
        /// The desktop profile is explicitly excluded so the Electron application is
        /// never touched. Returns an empty list if process enumeration is unavailable.
        /// </summary>
        /// <summary>
        /// The web port we are supposed to manage. 0 when it cannot be determined,
        /// in which case the narrowing step is skipped rather than guessed.
        /// </summary>
        private int ConfiguredWebPort()
        {
            try
            {
                TrayConfig cfg = _configProvider();
                if (cfg == null) return 0;
                return cfg.WebPort;
            }
            catch
            {
                return 0;
            }
        }

        private List<int> FindDshWebProcessIds()
        {
            List<int> found = new List<int>();

            int webPort = ConfiguredWebPort();
            List<int> listeningOnOurPort = TcpListenerProbe.PidsListeningOn(webPort);

            List<int> daemonPids = new List<int>();
            try
            {
                Process[] daemons = Process.GetProcessesByName(DaemonProcessName);
                for (int i = 0; i < daemons.Length; i++)
                {
                    try { daemonPids.Add(daemons[i].Id); }
                    catch { }
                }
                DisposeAll(daemons);
            }
            catch
            {
            }

            try
            {
                ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name='node.exe'");

                ManagementObjectCollection rows = searcher.Get();
                try
                {
                    foreach (ManagementBaseObject row in rows)
                    {
                        int pid = Convert.ToInt32(row["ProcessId"]);
                        int parentPid = Convert.ToInt32(row["ParentProcessId"]);
                        object rawCmd = row["CommandLine"];
                        string cmd = rawCmd == null ? string.Empty : rawCmd.ToString();

                        bool isDaemonChild = daemonPids.Contains(parentPid);

                        bool looksLikeDshWeb =
                            cmd.IndexOf("deepseek-ai", StringComparison.OrdinalIgnoreCase) >= 0
                            && cmd.IndexOf("bin.js", StringComparison.OrdinalIgnoreCase) >= 0
                            && Regex.IsMatch(cmd, @"(^|\s)web(\s|$)", RegexOptions.IgnoreCase)
                            && cmd.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) < 0;

                        if (!isDaemonChild && !looksLikeDshWeb) continue;

                        // Narrowing: a dsh web the user started on another port is
                        // none of ours, even though it looks exactly like ours.
                        if (!DshWebScope.IsOurDshWeb(cmd, webPort, listeningOnOurPort.Contains(pid)))
                        {
                            continue;
                        }

                        if (!found.Contains(pid)) found.Add(pid);
                    }
                }
                finally
                {
                    try { rows.Dispose(); } catch { }
                    try { searcher.Dispose(); } catch { }
                }
            }
            catch
            {
                // WMI unavailable: fall back to whatever the daemon-child pass found.
            }

            return found;
        }

        /// <summary>
        /// Disposes a process array without ever throwing; null entries and null
        /// arrays are tolerated.
        /// </summary>
        private static void DisposeAll(Process[] procs)
        {
            if (procs == null)
            {
                return;
            }

            for (int i = 0; i < procs.Length; i++)
            {
                if (procs[i] == null)
                {
                    continue;
                }

                try
                {
                    procs[i].Dispose();
                }
                catch
                {
                }
            }
        }
    }
}