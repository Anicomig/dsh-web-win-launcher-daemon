using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ConfigHost
{
    /// <summary>
    /// 无界面自检：不启动窗口，逐个跑桥方法与纯逻辑，输出 PASS/FAIL，有失败则退出码非 0。
    ///
    /// 为什么需要它：DshConfigHost 是 GUI 程序，端到端验证否则只能靠肉眼或浏览器自动化
    /// （后者在本项目上反复失败，成本极高）。有了 --selftest，桥的每个方法
    /// 都能在进程内直接调用并断言，一次命令给退出码。
    ///
    /// 铁律：自检必须纯只读。结尾会断言配置文件哈希未变。
    /// </summary>
    internal static class SelfTest
    {
        private static int _pass, _fail;
        private static readonly List<string> Lines = new List<string>();

        private static void T(string name, bool ok, string detail)
        {
            if (ok) { _pass++; Lines.Add("PASS  " + name); }
            else { _fail++; Lines.Add("FAIL  " + name + (detail == null ? "" : "   -> " + detail)); }
        }

        private static void T(string name, bool ok) { T(name, ok, null); }

        public static int Run()
        {
            // 重定向下 Console.OutputEncoding 赋值可能抛，换 SetOut
            try { Console.SetOut(new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false))); } catch { }
            byte[] cfgBefore = File.Exists(AppEnv.ConfigFile) ? File.ReadAllBytes(AppEnv.ConfigFile) : null;

            T("AppEnv.installRoot 存在", Directory.Exists(AppEnv.InstallRoot), AppEnv.InstallRoot);
            T("AppEnv.configFile 存在", AppEnv.ConfigExists, AppEnv.ConfigFile);
            T("AppEnv.startupDir 非空", !string.IsNullOrEmpty(AppEnv.StartupDir), AppEnv.StartupDir);
            T("AppEnv.hostVersion 非空", !string.IsNullOrEmpty(AppEnv.HostVersion), AppEnv.HostVersion);

            var b = new HostBridge(null);

            // ---- 读配置：必须与磁盘逐字符一致 ----
            var read = JsonDocument.Parse(b.ReadConfig()).RootElement;
            T("ReadConfig ok", read.GetProperty("ok").GetBoolean());
            var text = read.GetProperty("text").GetString();
            bool bom;
            var disk = ConfigEditor.ReadFile(AppEnv.ConfigFile, out bom);
            T("ReadConfig 文本与磁盘逐字符一致", text == disk,
              "len " + (text == null ? -1 : text.Length) + " vs " + disk.Length);
            T("ReadConfig hasBom 与磁盘一致", read.GetProperty("hasBom").GetBoolean() == bom);
            T("配置文件有 BOM", bom);
            T("配置文件是 LF（无 CR）", disk.IndexOf('\r') < 0,
              "CR 个数=" + disk.Count(c => c == '\r'));
            T("配置文件含 CONFIG_PORT", disk.Contains("$CONFIG_PORT"));

            // ---- 入参安全：全部必须被拒 ----
            T("WriteConfig 拒绝空参数", !Ok(b.WriteConfig("")));
            T("WriteConfig 拒绝坏 JSON", !Ok(b.WriteConfig("{")));
            T("WriteConfig 拒绝非对象", !Ok(b.WriteConfig("[1]")));
            T("WriteConfig 拒绝缺 edits", !Ok(b.WriteConfig("{}")));
            T("WriteConfig 拒绝空 edits", !Ok(b.WriteConfig("{\"edits\":[]}")));
            T("WriteConfig 拒绝非法变量名",
              !Ok(b.WriteConfig("{\"edits\":[{\"name\":\"evil; x\",\"kind\":\"int\",\"value\":1}]}")));
            T("WriteConfig 拒绝缺前缀的变量名",
              !Ok(b.WriteConfig("{\"edits\":[{\"name\":\"PORT\",\"kind\":\"int\",\"value\":1}]}")));
            T("WriteConfig 拒绝非法类型",
              !Ok(b.WriteConfig("{\"edits\":[{\"name\":\"CONFIG_PORT\",\"kind\":\"script\",\"value\":1}]}")));
            T("WriteConfig 拒绝含换行的值",
              !Ok(b.WriteConfig("{\"edits\":[{\"name\":\"CONFIG_HOST\",\"kind\":\"str\",\"value\":\"a\\nb\"}]}")));
            T("WriteConfig 拒绝以 - 开头的值",
              !Ok(b.WriteConfig("{\"edits\":[{\"name\":\"CONFIG_HOST\",\"kind\":\"str\",\"value\":\"-x\"}]}")));
            T("WriteConfig 拒绝超长参数",
              !Ok(b.WriteConfig("{\"edits\":[],\"pad\":\"" + new string('x', 262144) + "\"}")));
            T("VerifyPaths 拒绝空数组", !Ok(b.VerifyPaths("{\"paths\":[]}")));
            T("VerifyPaths 拒绝超 50 条",
              !Ok(b.VerifyPaths("{\"paths\":[" + string.Join(",",
                Enumerable.Repeat("{\"key\":\"k\",\"path\":\"C:\\\\Windows\",\"kind\":\"dir\"}", 51)) + "]}")));

            // ---- VerifyPaths 正例 ----
            var vp = JsonDocument.Parse(b.VerifyPaths(
                "{\"paths\":[{\"key\":\"W\",\"path\":\"C:\\\\Windows\",\"kind\":\"dir\"}," +
                "{\"key\":\"N\",\"path\":\"C:\\\\definitely-not-here\",\"kind\":\"file\"}]}")).RootElement;
            T("VerifyPaths ok", vp.GetProperty("ok").GetBoolean());
            var arr = vp.GetProperty("results");
            T("VerifyPaths 存在的判 true", arr[0].GetProperty("exists").GetBoolean(), arr[0].ToString());
            T("VerifyPaths 不存在的判 false", !arr[1].GetProperty("exists").GetBoolean(), arr[1].ToString());

            // ---- GetDefaults：缺文件时必须友好失败而不是崩 ----
            var def = JsonDocument.Parse(b.GetDefaults()).RootElement;
            if (File.Exists(AppEnv.DefaultsPath))
            {
                T("GetDefaults ok（defaults.json 存在）", def.GetProperty("ok").GetBoolean());
            }
            else
            {
                T("GetDefaults 缺失时友好失败", !def.GetProperty("ok").GetBoolean());
                T("GetDefaults 缺失时给出可操作提示",
                  def.GetProperty("message").GetString().Contains("Install.cmd"),
                  def.GetProperty("message").GetString());
            }

            // ---- GetAutostart 结构 ----
            var st = JsonDocument.Parse(b.GetAutostart()).RootElement;
            T("GetAutostart ok", st.GetProperty("ok").GetBoolean());
            var states = st.GetProperty("states");
            T("GetAutostart 含 daemon 与 tray",
              states.TryGetProperty("daemon", out _) && states.TryGetProperty("tray", out _));
            bool shapeOk = true; string detail = "";
            foreach (var key in new[] { "daemon", "tray" })
            {
                if (!states.TryGetProperty(key, out var s)) continue;
                var sv = s.TryGetProperty("state", out var p) ? p.GetString() : null;
                if (sv != "on" && sv != "off" && sv != "error") { shapeOk = false; detail = key + "=" + sv; }
            }
            T("GetAutostart 状态值合法", shapeOk, detail);

            // ---- DaemonController 只 Probe，绝不 Restart（那会动用户进程）----
            var pr = DaemonController.Probe();
            Lines.Add("INFO  daemon running=" + pr.Running + " pid=" + pr.Pid);
            T("DaemonController.Probe 不抛异常", true);

            // ---- 铁律：自检一个字节都没写 ----
            byte[] cfgAfter = File.Exists(AppEnv.ConfigFile) ? File.ReadAllBytes(AppEnv.ConfigFile) : null;
            bool unchanged = (cfgBefore == null && cfgAfter == null)
                || (cfgBefore != null && cfgAfter != null && cfgBefore.Length == cfgAfter.Length
                    && !cfgBefore.Where((x, i) => x != cfgAfter[i]).Any());
            T("★ 自检全程未改动配置文件", unchanged);
            T("★ 自检未留下临时写入文件",
              !Directory.GetFiles(AppEnv.InstallRoot, "*.tmp-*").Any(),
              string.Join(",", Directory.GetFiles(AppEnv.InstallRoot, "*.tmp-*")));

            foreach (var l in Lines) Console.WriteLine(l);
            Console.WriteLine("===== " + _pass + " 通过 / " + _fail + " 失败 =====");
            try { Console.Out.Flush(); } catch { }
            return _fail == 0 ? 0 : 1;
        }

        private static bool Ok(string json)
        {
            try { return JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean(); }
            catch { return false; }
        }
    }
}