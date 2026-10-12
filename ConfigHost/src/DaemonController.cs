using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ConfigHost
{
    /// <summary>
    /// 守护进程的停止/启动。语义移植自旧版 ConfigTools\Protocol.ps1 的 Invoke-RestartDaemon
    /// （那个脚本已随配置窗口改造移除；它的语义在真机上验证过，尤其是"必须等旧进程
    /// 真正退出"这一条 —— 不等会导致新 daemon 看到端口仍被占用却不拉起 dsh web）。
    /// </summary>
    internal static class DaemonController
    {
        public const string ProcName = AppEnv.DaemonProcName;

        public sealed class ProbeResult { public bool Running; public string Pid; }
        public sealed class RestartResult { public bool Ok; public string Message; }

        public static ProbeResult Probe()
        {
            try
            {
                var ps = Process.GetProcessesByName(ProcName);
                bool running = ps.Length > 0;
                string pid = "";
                if (running)
                {
                    var parts = new string[ps.Length];
                    for (int i = 0; i < ps.Length; i++) parts[i] = ps[i].Id.ToString();
                    pid = string.Join(",", parts);
                }
                foreach (var p in ps) { try { p.Dispose(); } catch { } }
                return new ProbeResult { Running = running, Pid = pid };
            }
            catch { return new ProbeResult { Running = false, Pid = "" }; }
        }

        public static RestartResult Restart(string exePath, string workingDir)
        {
            if (!File.Exists(exePath))
                return new RestartResult { Ok = false, Message = "找不到守护进程：" + exePath };

            int oldCount = 0;
            Process[] old = new Process[0];
            try
            {
                old = Process.GetProcessesByName(ProcName);
                oldCount = old.Length;
                if (oldCount > 0)
                {
                    foreach (var p in old)
                    {
                        // 非递归：与已验证的 Stop-Process -Force 语义一致。
                        // Kill(true) 会连 node（dsh web）一起杀掉，而重启守护进程
                        // 不应该影响已经在跑的 dsh web。
                        try { p.Kill(); }
                        catch { }
                    }

                    // 必须等进程真正退出，否则新 daemon 会看到端口仍被占用，
                    // 直接进入 ACTIVE 却不拉起 dsh web —— 表现为“重启后网页打不开”。
                    var deadline = DateTime.Now.AddSeconds(10);
                    bool still = true;
                    while (DateTime.Now < deadline)
                    {
                        var check = Process.GetProcessesByName(ProcName);
                        int alive = check.Length;
                        foreach (var c in check) { try { c.Dispose(); } catch { } }
                        if (alive == 0) { still = false; break; }
                        Thread.Sleep(200);
                    }
                    if (still)
                        return new RestartResult { Ok = false,
                            Message = "旧守护进程 10 秒内未退出，已放弃启动新的（避免两个 daemon 并存）" };
                }
            }
            finally
            {
                foreach (var p in old) { try { p.Dispose(); } catch { } }
            }

            try
            {
                var si = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = workingDir,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var child = Process.Start(si);
                string pid = child == null ? "?" : child.Id.ToString();
                if (child != null) { try { child.Dispose(); } catch { } }
                string verb = oldCount > 0 ? "已重启后台守护进程（PID " : "后台守护进程未在运行，已启动（PID ";
                return new RestartResult { Ok = true, Message = verb + pid + "）" };
            }
            catch (Exception ex)
            {
                return new RestartResult { Ok = false, Message = "启动失败：" + ex.Message };
            }
        }
    }
}