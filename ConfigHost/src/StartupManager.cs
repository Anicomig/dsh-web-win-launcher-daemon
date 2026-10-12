using System;
using System.Collections.Generic;
using System.IO;

namespace ConfigHost
{
    /// <summary>
    /// %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup 的 .lnk 管理。
    /// 状态判定沿用现有语义：与源逐字节一致 = on；不存在 = off；
    /// 存在但不一致 = error（不猜测，交给用户判断）。
    /// 这个目录浏览器是硬性禁访的，原生进程没这限制 —— 正是换成 DshConfigHost 的直接收益。
    /// </summary>
    internal static class StartupManager
    {
        private static readonly Dictionary<string, string> SourceNames =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "daemon", "LaunchDshLauncherDaemon.lnk" },
                { "tray",   "LaunchDshLauncher.lnk" }
            };

        private static string SourcePath(string target)
        {
            return Path.Combine(AppEnv.AutostartSrcDir, SourceNames[target]);
        }

        private static string TargetPath(string target)
        {
            return Path.Combine(AppEnv.StartupDir, SourceNames[target]);
        }

        public static Dictionary<string, object> GetStates()
        {
            var all = new Dictionary<string, object>();
            foreach (var target in SourceNames.Keys)
            {
                string state = "off";
                string note = null;
                var name = SourceNames[target];
                try
                {
                    var src = SourcePath(target);
                    var dst = TargetPath(target);
                    if (!File.Exists(dst)) state = "off";
                    else if (!File.Exists(src)) { state = "error"; note = "源快捷方式缺失：" + src; }
                    else if (BytesEqual(src, dst)) state = "on";
                    else { state = "error"; note = "启动项已存在但与源不一致，请检查"; }
                }
                catch (Exception ex) { state = "error"; note = ex.Message; }

                var d = new Dictionary<string, object>();
                d["state"] = state;
                d["name"] = name;
                if (note != null) d["note"] = note;
                all[target] = d;
            }
            return all;
        }

        public static bool Apply(string target, string action, out string message)
        {
            if (!SourceNames.ContainsKey(target))
            {
                message = "未知目标：" + target;
                return false;
            }
            try
            {
                var src = SourcePath(target);
                var dst = TargetPath(target);
                if (action == "enable")
                {
                    if (!File.Exists(src)) { message = "找不到源快捷方式：" + src; return false; }
                    var dir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.Copy(src, dst, true);
                    message = "已加入开机启动：" + SourceNames[target];
                    return true;
                }
                if (action == "disable")
                {
                    if (File.Exists(dst))
                    {
                        File.Delete(dst);
                        message = "已从开机启动移除：" + SourceNames[target];
                    }
                    else
                    {
                        message = "本来就不在开机启动里：" + SourceNames[target];
                    }
                    return true;
                }
                message = "action 非法：" + action;
                return false;
            }
            catch (Exception ex)
            {
                message = "操作失败：" + ex.Message;
                return false;
            }
        }

        private static bool BytesEqual(string a, string b)
        {
            var ba = File.ReadAllBytes(a);
            var bb = File.ReadAllBytes(b);
            if (ba.Length != bb.Length) return false;
            for (int i = 0; i < ba.Length; i++) if (ba[i] != bb[i]) return false;
            return true;
        }
    }
}