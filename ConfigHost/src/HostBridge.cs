using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConfigHost
{
    /// <summary>
    /// 页面能调到的东西的全集 —— 除本类外，页面碰不到任何 DshConfigHost 侧对象。
    ///
    /// 两条铁律：
    ///   1) 任何方法都不得让异常穿到 JS 侧 —— 一律 catch 成 {ok:false,message}。
    ///   2) 每个方法返回 JSON 字符串。跨界返回 string 是实测可靠的（见设计附录 A），
    ///      返回复杂 POCO 要走 COM 编组，是另一类风险源。
    /// </summary>
    [ComVisible(true)]
    public class HostBridge
    {
        private const int MaxPayload = 262144;      // 256 KB
        private const int MaxValueLen = 1024;
        private const int MaxEdits = 40;
        private const int MaxPaths = 50;

        private readonly IRestartHost _owner;

        // HostWindow 是 internal，所以构造函数必须也是 internal，
        // 否则 CS0051「参数的类型不可访问」。类保持 public 是 COM 可见性需要。
        internal HostBridge(IRestartHost owner) { _owner = owner; }

        // ---------------- 工具 ----------------

        private static string Serialize(object o)
        {
            return JsonSerializer.Serialize(o);
        }

        private static string Fail(string message)
        {
            return Serialize(new Dictionary<string, object> { { "ok", false }, { "message", message } });
        }

        private static string Ok(Dictionary<string, object> extra)
        {
            var d = new Dictionary<string, object> { { "ok", true } };
            if (extra != null)
            {
                foreach (var kv in extra) d[kv.Key] = kv.Value;
            }
            return Serialize(d);
        }

        private static bool TryParse(string json, out JsonElement root, out string error)
        {
            root = default(JsonElement);
            error = null;
            if (string.IsNullOrEmpty(json)) { error = "参数为空"; return false; }
            if (json.Length > MaxPayload) { error = "参数过长（上限 " + MaxPayload + " 字符）"; return false; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (Exception ex) { error = "参数不是合法 JSON：" + ex.Message; return false; }
            using (doc)
            {
                root = doc.RootElement.Clone();
            }
            if (root.ValueKind != JsonValueKind.Object) { error = "参数顶层必须是对象"; return false; }
            return true;
        }

        // ---------------- 1. 环境 ----------------

        public string GetEnvironment()
        {
            try
            {
                var d = DaemonController.Probe();
                return Ok(new Dictionary<string, object>
                {
                    { "installRoot",     AppEnv.InstallRoot },
                    { "configFile",      AppEnv.ConfigFile },
                    { "configExists",    AppEnv.ConfigExists },
                    { "startupDir",      AppEnv.StartupDir },
                    { "hostVersion",     AppEnv.HostVersion },
                    { "webview2Version", _owner == null ? "" : (_owner.WebView2Version ?? "") },
                    { "daemonRunning",   d.Running },
                    { "daemonPid",       d.Pid }
                });
            }
            catch (Exception ex) { return Fail("读取环境信息失败：" + ex.Message); }
        }

        // ---------------- 2. 读配置 ----------------

        public string ReadConfig()
        {
            try
            {
                if (!AppEnv.ConfigExists) return Fail("找不到配置文件：" + AppEnv.ConfigFile);
                bool hasBom;
                var text = ConfigEditor.ReadFile(AppEnv.ConfigFile, out hasBom);
                return Ok(new Dictionary<string, object> { { "text", text }, { "hasBom", hasBom } });
            }
            catch (Exception ex) { return Fail("读取配置失败：" + ex.Message); }
        }

        // ---------------- 3. 写配置 ----------------

        public string WriteConfig(string json)
        {
            try
            {
                JsonElement root; string err;
                if (!TryParse(json, out root, out err)) return Fail(err);

                JsonElement editsEl;
                if (!root.TryGetProperty("edits", out editsEl) || editsEl.ValueKind != JsonValueKind.Array)
                    return Fail("缺少 edits 数组");
                int n = 0;
                foreach (var _ in editsEl.EnumerateArray()) n++;
                if (n == 0) return Fail("edits 为空");
                if (n > MaxEdits) return Fail("edits 超过 " + MaxEdits + " 条");

                var edits = new List<ConfigEditor.Edit>();
                foreach (var it in editsEl.EnumerateArray())
                {
                    if (it.ValueKind != JsonValueKind.Object) return Fail("edits 元素必须是对象");
                    var name = it.TryGetProperty("name", out var np) ? np.GetString() : null;
                    var kind = it.TryGetProperty("kind", out var kp) ? kp.GetString() : null;
                    if (string.IsNullOrEmpty(name)) return Fail("edits 元素缺 name");
                    if (string.IsNullOrEmpty(kind)) return Fail("edits 元素缺 kind: " + name);
                    if (!it.TryGetProperty("value", out var v)) return Fail("edits 元素缺 value: " + name);

                    // 名称与类型在这里挡一次，ConfigEditor.Apply 里再挡一次（纵深防御）
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^CONFIG_[A-Z0-9_]{1,40}$"))
                        return Fail("变量名非法：" + name);
                    if (kind != "int" && kind != "str" && kind != "bool")
                        return Fail("类型非法：" + kind);

                    object val;
                    switch (v.ValueKind)
                    {
                        case JsonValueKind.True: val = true; break;
                        case JsonValueKind.False: val = false; break;
                        case JsonValueKind.Number:
                            if (!v.TryGetInt64(out var lng)) return Fail("整数超出范围：" + name);
                            val = lng;
                            break;
                        case JsonValueKind.String:
                            var s = v.GetString();
                            if (s == null) return Fail("值为 null：" + name);
                            if (s.Length > MaxValueLen) return Fail("值过长：" + name);
                            if (s.StartsWith("-")) return Fail("值不能以 - 开头：" + name);
                            val = s;
                            break;
                        default: return Fail("值类型不受支持：" + name);
                    }
                    edits.Add(new ConfigEditor.Edit(name, kind, val));
                }

                if (!AppEnv.ConfigExists) return Fail("找不到配置文件：" + AppEnv.ConfigFile);

                // 以磁盘当前内容为基准，绝不信任页面送来的副本
                bool hasBom;
                var text = ConfigEditor.ReadFile(AppEnv.ConfigFile, out hasBom);
                var r = ConfigEditor.Apply(text, edits);

                var backup = BackupConfigOnce();
                ConfigEditor.WriteFileAtomically(AppEnv.ConfigFile, r.Text, hasBom);

                var msg = r.Changed > 0
                    ? "已保存 " + r.Changed + " 项 —— 改动需重启守护进程才生效"
                    : "没有需要写入的改动";
                var extra = new Dictionary<string, object>
                {
                    { "changed", r.Changed },
                    { "skipped", r.Skipped },
                    { "message", msg }
                };
                if (backup != null) extra["backup"] = backup;
                return Ok(extra);
            }
            catch (ArgumentException ex) { return Fail("参数被拒绝：" + ex.Message); }
            catch (Exception ex) { return Fail("写入失败：" + ex.Message); }
        }

        private static string BackupConfigOnce()
        {
            try
            {
                var dir = Path.Combine(AppEnv.InstallRoot, "_backup",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-config-before-write");
                Directory.CreateDirectory(dir);
                var dst = Path.Combine(dir, AppEnv.ConfigFileName);
                if (!File.Exists(dst)) File.Copy(AppEnv.ConfigFile, dst, true);
                return dir;
            }
            catch { return null; }   // 备份失败不阻断保存：与现有 PowerShell 行为一致
        }

        // ---------------- 4. 默认值 ----------------

        public string GetDefaults()
        {
            try
            {
                if (!File.Exists(AppEnv.DefaultsPath))
                    return Fail("缺少 defaults.json，请在项目根运行「Install.cmd」后重新打开本窗口");
                var raw = File.ReadAllText(AppEnv.DefaultsPath, System.Text.Encoding.UTF8);
                using (var doc = JsonDocument.Parse(raw))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return Fail("defaults.json 顶层必须是对象");
                    var vals = new Dictionary<string, object>();
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        vals[p.Name] = p.Value.Clone();
                    }
                    return Ok(new Dictionary<string, object> { { "values", vals } });
                }
            }
            catch (Exception ex) { return Fail("读取 defaults.json 失败：" + ex.Message); }
        }

        // ---------------- 5. 路径校验 ----------------

        public string VerifyPaths(string json)
        {
            try
            {
                JsonElement root; string err;
                if (!TryParse(json, out root, out err)) return Fail(err);

                JsonElement arr;
                if (!root.TryGetProperty("paths", out arr) || arr.ValueKind != JsonValueKind.Array)
                    return Fail("缺少 paths 数组");
                int n = 0;
                foreach (var _ in arr.EnumerateArray()) n++;
                if (n == 0) return Fail("paths 为空");
                if (n > MaxPaths) return Fail("一次最多校验 " + MaxPaths + " 条");

                var results = new List<Dictionary<string, object>>();
                foreach (var it in arr.EnumerateArray())
                {
                    var key = it.TryGetProperty("key", out var kp) ? kp.GetString() : "";
                    var path = it.TryGetProperty("path", out var pp) ? pp.GetString() : "";
                    var kind = it.TryGetProperty("kind", out var dp) ? dp.GetString() : "any";

                    var row = new Dictionary<string, object>();
                    row["key"] = key;
                    row["path"] = path;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            row["exists"] = false;
                            row["resolved"] = null;
                        }
                        else
                        {
                            // 只做 Exists —— 不读内容、不列目录（设计第十三节）
                            var full = Path.GetFullPath(path);
                            bool isDir = Directory.Exists(full);
                            bool isFile = File.Exists(full);
                            row["resolved"] = full;
                            row["exists"] = kind == "dir" ? isDir
                                          : kind == "file" ? isFile
                                          : (isDir || isFile);
                        }
                    }
                    catch (Exception ex2)
                    {
                        row["exists"] = false;
                        row["resolved"] = null;
                        row["error"] = ex2.Message;
                    }
                    results.Add(row);
                }
                return Ok(new Dictionary<string, object>
                {
                    { "results", results },
                    { "message", "已校验 " + n + " 条路径" }
                });
            }
            catch (Exception ex) { return Fail("路径校验失败：" + ex.Message); }
        }

        // ---------------- 6 / 7. 开机自启 ----------------

        public string GetAutostart()
        {
            try
            {
                return Ok(new Dictionary<string, object> { { "states", StartupManager.GetStates() } });
            }
            catch (Exception ex) { return Fail("读取自启状态失败：" + ex.Message); }
        }

        public string SetAutostart(string json)
        {
            try
            {
                JsonElement root; string err;
                if (!TryParse(json, out root, out err)) return Fail(err);
                var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
                var target = root.TryGetProperty("target", out var t) ? t.GetString() : null;
                if (action != "enable" && action != "disable") return Fail("action 非法");
                if (target != "daemon" && target != "tray") return Fail("target 非法");

                string message;
                bool ok = StartupManager.Apply(target, action, out message);
                return Serialize(new Dictionary<string, object>
                {
                    { "ok", ok },
                    { "message", message },
                    { "states", StartupManager.GetStates() }
                });
            }
            catch (Exception ex) { return Fail("设置自启失败：" + ex.Message); }
        }
        // ---------------- 9. 在资源管理器里打开所在文件夹 ----------------

        /// <summary>
        /// 目录就打开它自己，文件则打开它所在的目录 —— 与用户描述一致。
        /// 只执行 explorer.exe，从不执行传入的路径本身；路径必须已经存在，
        /// 不存在就报错而不是创建。explorer 即使成功也常返回非零退出码，
        /// 所以不等待、不看退出码，只报告"已打开哪个目录"。
        /// </summary>
        public string OpenInFolder(string json)
        {
            try
            {
                JsonElement root; string err;
                if (!TryParse(json, out root, out err)) return Fail(err);

                JsonElement pe;
                if (!root.TryGetProperty("path", out pe) || pe.ValueKind != JsonValueKind.String)
                    return Fail("缺少 path");

                string dir, why;
                if (!FolderTarget.TryResolve(pe.GetString(), out dir, out why)) return Fail(why);

                // 只执行 explorer.exe，从不执行传入的路径本身。
                // explorer 即使成功也常返回非零退出码，所以不等待、不看退出码。
                var si = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + dir + "\"",
                    UseShellExecute = true
                };
                Process.Start(si);
                return Ok(new Dictionary<string, object> { { "opened", dir } });
            }
            catch (Exception ex) { return Fail("打开文件夹失败：" + ex.Message); }
        }
        // ---------------- 12. 打开方式（存 DshLauncher.json） ----------------

        /// <summary>
        /// 「打开方式」不放在 DshLauncherDaemon_Config.ps1 里，而是存
        /// DshLauncher\DshLauncher.json —— 两个原因：
        ///  1. 它只影响托盘怎么打开网页，守护进程根本不关心；
        ///  2. ps1 的写入是"外科手术式替换已存在行"，字段不在文件里就会被静默跳过，
        ///     而 JSON 是托盘的完整配置，写入必然是新增/替换，不存在跳过。
        /// 切换后立即落盘，不需要点保存。
        /// </summary>
        private static string JsonConfigPath()
        {
            return Path.Combine(AppEnv.InstallRoot, "DshLauncher", "DshLauncher.json");
        }

        /// <summary>读当前的打开方式与 app-id（供页面初始化）。</summary>
        public string GetOpenMode(string json)
        {
            try
            {
                string path = JsonConfigPath();
                string mode = "browser", appId = "";
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path, new UTF8Encoding(false));
                    string m = ReadJsonString(text, "openMode");
                    string a = ReadJsonString(text, "edgeAppId");
                    if (m == "browser" || m == "app") mode = m;
                    if (a != null && Regex.IsMatch(a, "^[a-p]{32}$")) appId = a;
                }
                return Ok(new Dictionary<string, object>
                {
                    { "mode", mode },
                    { "appId", appId },
                    { "path", path },
                    { "exists", File.Exists(path) }
                });
            }
            catch (Exception ex) { return Fail("读取打开方式失败：" + ex.Message); }
        }

        /// <summary>写入打开方式（立即生效，不需要保存按钮）。</summary>
        public string SetOpenMode(string json)
        {
            try
            {
                JsonElement root; string err;
                if (!TryParse(json, out root, out err)) return Fail(err);

                string mode = root.TryGetProperty("mode", out var me) ? me.GetString() : null;
                if (mode != "browser" && mode != "app") return Fail("mode 必须是 browser 或 app");

                string appId = root.TryGetProperty("appId", out var ae) ? (ae.GetString() ?? "") : "";
                appId = appId.Trim();
                if (appId.Length > 0 && !Regex.IsMatch(appId, "^[a-p]{32}$"))
                    return Fail("appId 必须是 32 个 a-p 字符");
                if (mode == "app" && appId.Length == 0)
                    return Fail("选择网页应用需要先检测到 app-id");

                string path = JsonConfigPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    return Fail("找不到托盘目录：" + dir);

                // 保留文件里已有的 daemonExe / webPort，只替换这两个键
                string existing = File.Exists(path) ? File.ReadAllText(path, new UTF8Encoding(false)) : "";
                string daemonExe = ReadJsonString(existing, "daemonExe");
                if (string.IsNullOrEmpty(daemonExe))
                    daemonExe = Path.Combine(AppEnv.InstallRoot, "DshLauncherDaemon.exe");
                int port = ReadJsonInt(existing, "webPort");
                if (port <= 0) port = 3080;

                var sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"daemonExe\": \"").Append(daemonExe.Replace("\\", "\\\\")).Append("\",\r\n");
                sb.Append("  \"webPort\": ").Append(port.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
                sb.Append("  \"openMode\": \"").Append(mode).Append("\",\r\n");
                sb.Append("  \"edgeAppId\": \"").Append(appId).Append("\"\r\n");
                sb.Append("}\r\n");

                string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);

                return Ok(new Dictionary<string, object>
                {
                    { "mode", mode },
                    { "appId", appId },
                    { "message", mode == "app" ? "已设为「打开网页应用」" : "已设为「打开网页」" }
                });
            }
            catch (Exception ex) { return Fail("保存打开方式失败：" + ex.Message); }
        }

        /// <summary>从 JSON 文本里取一个字符串键（容忍缺键与转义）。</summary>
        private static string ReadJsonString(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\\", "\\");
        }

        private static int ReadJsonInt(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            if (!m.Success) return 0;
            int v;
            return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // ---------------- 11. 用系统默认浏览器打开网址 ----------------

        /// <summary>
        /// 页面自身的导航会被 NavigationStarting 拦下（设计第十三节），所以
        /// 「用 Edge 打开这个地址」这类链接必须交给宿主用系统默认程序打开。
        /// 只接受 http/https，避免变成任意命令执行入口。
        /// </summary>
        public string OpenUrl(string json)
        {
            try
            {
                JsonElement root; string err;
                if (!TryParse(json, out root, out err)) return Fail(err);

                JsonElement ue;
                if (!root.TryGetProperty("url", out ue) || ue.ValueKind != JsonValueKind.String)
                    return Fail("缺少 url");

                var url = (ue.GetString() ?? string.Empty).Trim();
                if (url.Length == 0) return Fail("网址为空");
                if (url.Length > 2048) return Fail("网址过长");
                if (url.IndexOf('\r') >= 0 || url.IndexOf('\n') >= 0) return Fail("网址含换行");

                Uri parsed;
                if (!Uri.TryCreate(url, UriKind.Absolute, out parsed))
                    return Fail("网址格式不合法");
                if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                    return Fail("只允许 http/https");

                var si = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                Process.Start(si);
                return Ok(new Dictionary<string, object> { { "opened", url } });
            }
            catch (Exception ex) { return Fail("打开网址失败：" + ex.Message); }
        }

        // ---------------- 10. 检测 Edge 网页应用 ----------------

        /// <summary>
        /// 按配置的 host 与 port，去 Edge 的数据里找有没有对应的网页应用。
        ///
        /// 只读 Edge 的文件，绝不写入。命中返回 app-id 与名称；没命中把已装应用
        /// 列出来供兜底选择，并说明「该应用可能还没打开过」。
        /// </summary>
        public string DetectEdgeApp(string json)
        {
            try
            {
                JsonElement root; string err;
                if (!TryParse(json, out root, out err)) return Fail(err);

                string host = root.TryGetProperty("host", out var h) ? h.GetString() : "127.0.0.1";
                if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";

                int port = 0;
                if (root.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number)
                    port = p.GetInt32();
                if (port <= 0)
                {
                    // 没传端口就从当前配置里读，省得页面多传一次
                    port = ReadPortFromConfig();
                }
                if (port <= 0) return Fail("端口未知，无法检测");

                var hit = EdgeAppDetector.FindByHostPort(host, port);
                var all = EdgeAppDetector.Scan();
                string edgeExe = EdgeAppDetector.FindEdgeExe();

                if (hit != null)
                {
                    return Ok(new Dictionary<string, object>
                    {
                        { "found", true },
                        { "appId", hit.AppId },
                        { "name", hit.Name },
                        { "url", hit.Url },
                        { "edgeExe", edgeExe ?? string.Empty },
                        { "scanned", all.Count }
                    });
                }

                var list = new List<object>();
                foreach (var a in all)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        { "appId", a.AppId },
                        { "name", a.Name },
                        { "url", a.Url }
                    });
                }
                return Ok(new Dictionary<string, object>
                {
                    { "found", false },
                    { "apps", list },
                    { "edgeExe", edgeExe ?? string.Empty },
                    { "scanned", all.Count }
                });
            }
            catch (Exception ex) { return Fail("检测网页应用失败：" + ex.Message); }
        }

        private int ReadPortFromConfig()
        {
            try
            {
                // 与 Program.ReadPort 同样的定位方式：向上找 ConfigHost 这一层，
                // 不能用 Directory.GetParent(BaseDirectory) —— Debug 产物下那会指错
                string dir = AppContext.BaseDirectory.TrimEnd('\\');
                string cfg = null;
                for (int up = 0; up < 6 && dir != null; up++)
                {
                    string parent = Path.GetDirectoryName(dir);
                    if (parent == null) break;
                    if (string.Equals(Path.GetFileName(dir), "ConfigHost", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg = Path.Combine(parent, "DshLauncherDaemon_Config.ps1");
                        break;
                    }
                    dir = parent;
                }
                if (string.IsNullOrEmpty(cfg) || !File.Exists(cfg)) return 0;

                string text = File.ReadAllText(cfg, new UTF8Encoding(false));
                var m = Regex.Match(text, @"^\s*\$CONFIG_PORT\s*=\s*(\d+)", RegexOptions.Multiline);
                if (!m.Success) return 0;
                int v;
                return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
            }
            catch { return 0; }
        }

        // ---------------- 8. 重启守护进程（唯一长任务） ----------------

        public string RestartDaemon()
        {
            try
            {
                // 立即返回：等旧进程退出最长 10 秒，同步做会冻结窗口并停摆消息泵
                if (_owner == null) return Fail("DshConfigHost 窗口未就绪");
                _owner.RequestRestart();
                return Ok(new Dictionary<string, object> { { "started", true } });
            }
            catch (Exception ex) { return Fail("无法开始重启：" + ex.Message); }
        }
    }
}
