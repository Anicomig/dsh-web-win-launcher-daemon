// Tray\TrayConfig.cs
// Frozen configuration surface for the DshLauncher tray application.
//
// Language level: C# 5 / .NET Framework 4.0.
// No null-conditional operators, no string interpolation, no expression bodied
// members, no nameof, no tuples. Get-only properties are backed by private
// fields so the port can be refreshed in place.
//
// The JSON file is parsed with a tiny tolerant flat reader on purpose: we do not
// want a reference to System.Web.Extensions (JavaScriptSerializer) just for two
// fields, and we must tolerate a hand-edited file whose path uses single
// backslashes instead of the JSON required double backslashes.
//
// This file is deliberately ASCII-only so that encoding issues cannot corrupt it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DshLauncher.Tray
{
    internal sealed class TrayConfig
    {
        private const int DefaultWebPort = 3080;
        private const string DefaultWebHost = "http://127.0.0.1:";
        private const string JsonFileName = "DshLauncher.json";
        private const string DaemonExeName = "DshLauncherDaemon.exe";
        private const string DaemonConfigFileName = "DshLauncherDaemon_Config.ps1";
        private const string DaemonExeKey = "daemonExe";
        private const string WebPortKey = "webPort";
        // 「打开方式」与 app-id：与 daemonExe/webPort 同住 DshLauncher.json，
        // 由 DshConfigHost 的「打开方式」切换时立即写入。
        private const string OpenModeKey = "openMode";
        private const string EdgeAppIdKey = "edgeAppId";

        private string _daemonExePath;
        private int _webPort;
        private string _daemonDirectory;
        private string _logDirectory;
        private string _webUrl;
        private string _configFilePath;
        private string _loadError;
        // 打开方式：来自 DshLauncherDaemon_Config.ps1（不是 JSON）
        private string _openMode = "browser";
        private string _edgeAppId = "";

        // Absolute path to DshLauncherDaemon.exe, read from the JSON file.
        public string DaemonExePath { get { return _daemonExePath; } }

        // Web port for the daemon UI, read from the JSON file.
        public int WebPort { get { return _webPort; } }

        // Directory containing DaemonExePath. Derived, never read from JSON.
        public string DaemonDirectory { get { return _daemonDirectory; } }

        // DaemonDirectory + "\log". Derived, never read from JSON.
        public string LogDirectory { get { return _logDirectory; } }

        // "http://127.0.0.1:" + WebPort + "/". Derived.
        public string WebUrl { get { return _webUrl; } }

        // Absolute path of the JSON file this instance was loaded from.
        public string ConfigFilePath { get { return _configFilePath; } }

        // "browser" = 默认浏览器；"app" = Edge 网页应用（PWA）
        public string OpenMode { get { return _openMode; } }
        // 32 个 a-p 字符的 app-id；OpenMode="app" 时才有意义
        public string EdgeAppId { get { return _edgeAppId; } }

        // Human readable reason the last Load() fell back to defaults; null if fine.
        public string LoadError { get { return _loadError; } }

        private TrayConfig()
        {
            _daemonExePath = string.Empty;
            _daemonDirectory = string.Empty;
            _logDirectory = string.Empty;
            _webUrl = DefaultWebHost + DefaultWebPort.ToString(CultureInfo.InvariantCulture) + "/";
            _configFilePath = string.Empty;
            _webPort = DefaultWebPort;
            _loadError = null;
        }

        // Load from disk. NEVER throws. On any failure returns a config with
        // sensible defaults and sets LoadError to a message.
        public static TrayConfig Load()
        {
            TrayConfig config = new TrayConfig();
            try
            {
                string exeDirectory = GetExeDirectory();
                List<string> candidates = BuildCandidatePaths(exeDirectory);
                string found = FindFirstExisting(candidates);

                if (found == null)
                {
                    // No config file anywhere: use defaults, but remember where it
                    // would have lived (candidate #2) so a later refresh can create it.
                    config._configFilePath = PreferredConfigPath(exeDirectory);
                    config.SetDaemonExePath(GetDefaultDaemonExePath(exeDirectory), exeDirectory);
                    config._loadError = "Configuration file not found. Searched: "
                        + JoinPaths(candidates)
                        + ". Falling back to defaults.";
                    config.RecomputeDerived(exeDirectory);
                    return config;
                }

                config._configFilePath = found;

                string json = ReadAllTextUtf8(found);
                string daemonExe = ReadStringValue(json, DaemonExeKey);
                int port = ReadIntValue(json, WebPortKey);
                string problem = null;

                if (daemonExe == null || daemonExe.Trim().Length == 0)
                {
                    daemonExe = GetDefaultDaemonExePath(exeDirectory);
                    problem = "Missing or empty \"" + DaemonExeKey + "\" in " + found
                        + "; using " + daemonExe + ".";
                }

                if (port <= 0)
                {
                    port = DefaultWebPort;
                    if (problem == null)
                    {
                        problem = "Missing or invalid \"" + WebPortKey + "\" in " + found
                            + "; using " + DefaultWebPort.ToString(CultureInfo.InvariantCulture) + ".";
                    }
                    else
                    {
                        problem = problem + " Also missing or invalid \"" + WebPortKey
                            + "\"; using " + DefaultWebPort.ToString(CultureInfo.InvariantCulture) + ".";
                    }
                }

                config._webPort = port;
                config.SetDaemonExePath(daemonExe, exeDirectory);
                config.RecomputeDerived(exeDirectory);
                config._loadError = problem;

                // openMode / edgeAppId 就在这份 JSON 里（由 DshConfigHost 的
                // 「打开方式」立即写入）。托盘每次右键都会重新 Load，所以切换后
                // 下一次右键就能看到菜单项变化，不需要重启托盘。
                try
                {
                    string om = ReadStringValue(json, OpenModeKey);
                    if (om == "browser" || om == "app") config._openMode = om;
                    string ai = ReadStringValue(json, EdgeAppIdKey);
                    if (ai != null && System.Text.RegularExpressions.Regex.IsMatch(ai, "^[a-p]{32}$"))
                        config._edgeAppId = ai;
                }
                catch { /* 缺键或格式不对就保持默认 browser */ }

                return config;
            }
            catch (Exception ex)
            {
                // Last resort: a usable object, never an exception.
                try
                {
                    string exeDirectory = GetExeDirectory();
                    config._configFilePath = PreferredConfigPath(exeDirectory);
                    config._webPort = DefaultWebPort;
                    config.SetDaemonExePath(GetDefaultDaemonExePath(exeDirectory), exeDirectory);
                    config.RecomputeDerived(exeDirectory);
                    config._loadError = "Failed to load configuration: " + ex.Message;
                }
                catch (Exception inner)
                {
                    config._daemonExePath = DaemonExeName;
                    config._webPort = DefaultWebPort;
                    config._daemonDirectory = string.Empty;
                    config._logDirectory = "log";
                    config._webUrl = DefaultWebHost
                        + DefaultWebPort.ToString(CultureInfo.InvariantCulture) + "/";
                    config._configFilePath = JsonFileName;
                    config._loadError = "Failed to load configuration: " + inner.Message;
                }
                return config;
            }
        }

        // Re-read $CONFIG_PORT from DshLauncherDaemon_Config.ps1 in DaemonDirectory,
        // then write the JSON back to disk with the new port. Returns true if the
        // port changed. NEVER throws.
        public bool RefreshPortFromDaemonConfig()
        {
            try
            {
                string daemonDirectory = _daemonDirectory;
                if (daemonDirectory == null || daemonDirectory.Length == 0)
                {
                    return false;
                }

                string configPs1 = Path.Combine(daemonDirectory, DaemonConfigFileName);
                if (!File.Exists(configPs1))
                {
                    return false;
                }

                string text;
                try
                {
                    text = File.ReadAllText(configPs1, Encoding.UTF8);
                }
                catch (Exception)
                {
                    text = File.ReadAllText(configPs1);
                }

                Match match = Regex.Match(text, "\\$\\s*CONFIG_PORT\\s*=\\s*(\\d+)",
                    RegexOptions.Multiline);
                if (!match.Success)
                {
                    return false;
                }

                int newPort;
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out newPort))
                {
                    return false;
                }

                if (newPort <= 0 || newPort == _webPort)
                {
                    return false;
                }

                string target = _configFilePath;
                if (target == null || target.Length == 0)
                {
                    // The file was never found: create it at the preferred location.
                    target = PreferredConfigPath(GetExeDirectory());
                }

                string json = BuildJson(_daemonExePath, newPort, _openMode, _edgeAppId);
                File.WriteAllText(target, json, new UTF8Encoding(false));

                _configFilePath = target;
                _webPort = newPort;
                RecomputeDerived(GetExeDirectory());
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------

        private void SetDaemonExePath(string daemonExe, string exeDirectory)
        {
            _daemonExePath = daemonExe == null ? string.Empty : daemonExe.Trim();
            if (_daemonExePath.Length == 0)
            {
                _daemonExePath = Path.Combine(GetDefaultDaemonDirectory(exeDirectory), DaemonExeName);
            }
        }

        private void RecomputeDerived(string exeDirectory)
        {
            string directory = null;
            if (_daemonExePath != null && _daemonExePath.Length > 0)
            {
                try
                {
                    directory = Path.GetDirectoryName(_daemonExePath);
                }
                catch (Exception)
                {
                    directory = null;
                }
            }

            if (directory == null || directory.Length == 0)
            {
                directory = GetDefaultDaemonDirectory(exeDirectory);
            }

            _daemonDirectory = directory;
            _logDirectory = Path.Combine(directory, "log");
            _webUrl = DefaultWebHost
                + _webPort.ToString(CultureInfo.InvariantCulture) + "/";

            if (_configFilePath == null || _configFilePath.Length == 0)
            {
                _configFilePath = PreferredConfigPath(exeDirectory);
            }
        }

        // Directory of the running executable. Falls back to the application base
        // directory when the assembly has no location (shadow copy, some hosts).
        private static string GetExeDirectory()
        {
            string location = null;

            try
            {
                location = Assembly.GetExecutingAssembly().Location;
            }
            catch (Exception)
            {
                location = null;
            }

            if (location == null || location.Length == 0)
            {
                try
                {
                    Assembly entry = Assembly.GetEntryAssembly();
                    if (entry != null)
                    {
                        location = entry.Location;
                    }
                }
                catch (Exception)
                {
                    location = null;
                }
            }

            string directory = null;
            if (location != null && location.Length > 0)
            {
                try
                {
                    directory = Path.GetDirectoryName(location);
                }
                catch (Exception)
                {
                    directory = null;
                }
            }

            if (directory == null || directory.Length == 0)
            {
                directory = AppDomain.CurrentDomain.BaseDirectory;
            }

            if (directory == null)
            {
                directory = string.Empty;
            }

            return directory;
        }

        // Search order:
        //   1. <exe dir>\DshLauncher.json           (i.e. DshLauncher\dist\DshLauncher.json)
        //   2. <parent of exe dir>\DshLauncher.json  (i.e. DshLauncher\DshLauncher.json)
        //   3. <parent of that>\DshLauncher.json
        private static List<string> BuildCandidatePaths(string exeDirectory)
        {
            List<string> paths = new List<string>();

            // Candidate #1 is directly in the executing assembly's own directory.
            string level1 = exeDirectory;
            AddUnique(paths, Combine(level1, JsonFileName));

            string level2 = SafeDirectoryName(level1);
            AddUnique(paths, Combine(level2, JsonFileName));

            string level3 = SafeDirectoryName(level2);
            AddUnique(paths, Combine(level3, JsonFileName));

            return paths;
        }

        // Preferred location for a JSON file that has to be created: candidate #2,
        // the "<parent of exe dir>\DshLauncher.json" path.
        private static string PreferredConfigPath(string exeDirectory)
        {
            List<string> candidates = BuildCandidatePaths(exeDirectory);
            if (candidates.Count > 1)
            {
                return candidates[1];
            }
            if (candidates.Count > 0)
            {
                return candidates[0];
            }
            return JsonFileName;
        }

        // "<parent of the DshLauncher folder>\DshLauncherDaemon.exe".
        private static string GetDefaultDaemonExePath(string exeDirectory)
        {
            return Path.Combine(GetDefaultDaemonDirectory(exeDirectory), DaemonExeName);
        }

        private static string GetDefaultDaemonDirectory(string exeDirectory)
        {
            // Candidate #2 lives in the "DshLauncher" folder; the daemon sits one
            // level above it.
            string launcherFolder = SafeDirectoryName(exeDirectory);
            string daemonFolder = SafeDirectoryName(launcherFolder);

            if (daemonFolder == null || daemonFolder.Length == 0)
            {
                daemonFolder = launcherFolder;
            }

            if (daemonFolder == null || daemonFolder.Length == 0)
            {
                daemonFolder = exeDirectory;
            }

            if (daemonFolder == null)
            {
                daemonFolder = string.Empty;
            }

            return daemonFolder;
        }

        private static string SafeDirectoryName(string path)
        {
            if (path == null || path.Length == 0)
            {
                return null;
            }

            try
            {
                return Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Combine(string directory, string fileName)
        {
            if (directory == null || directory.Length == 0)
            {
                return fileName;
            }

            try
            {
                return Path.Combine(directory, fileName);
            }
            catch (Exception)
            {
                return fileName;
            }
        }

        private static void AddUnique(List<string> paths, string path)
        {
            if (path == null || path.Length == 0)
            {
                return;
            }

            for (int i = 0; i < paths.Count; i++)
            {
                if (string.Equals(paths[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            paths.Add(path);
        }

        private static string FindFirstExisting(List<string> paths)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                try
                {
                    if (File.Exists(paths[i]))
                    {
                        return paths[i];
                    }
                }
                catch (Exception)
                {
                    // Ignore invalid candidate paths and keep searching.
                }
            }

            return null;
        }

        private static string JoinPaths(List<string> paths)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < paths.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("; ");
                }
                builder.Append(paths[i]);
            }
            return builder.ToString();
        }

        /// <summary>
        /// 从 JSON 文本里取一个字符串键（容错：缺键返回 null）。
        /// openMode / edgeAppId 走这里，不再去解析 ps1 —— 那一版把两个字段
        /// 放进 DshLauncherDaemon_Config.ps1，而 ps1 的写入是"只替换已存在的行"，
        /// 字段不存在时会被静默跳过，于是保存后重开又变回默认。
        /// </summary>
        private static string ReadAllTextUtf8(string path)
        {
            try
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception)
            {
                return File.ReadAllText(path);
            }
        }

        // ---------------------------------------------------------------
        // Tiny tolerant flat-JSON readers (no System.Web.Extensions needed)
        // ---------------------------------------------------------------

        // Find "key", then the next ':', then the next quoted string.
        private static string ReadStringValue(string json, string key)
        {
            if (json == null)
            {
                return null;
            }

            int keyIndex = FindKey(json, key);
            if (keyIndex < 0)
            {
                return null;
            }

            int colon = json.IndexOf(':', keyIndex);
            if (colon < 0)
            {
                return null;
            }

            // The value must be a quoted string starting right after the colon,
            // possibly separated by whitespace. If it is not (for example the file
            // says "daemonExe": , or the value is a number) the key counts as
            // absent instead of accidentally reading the next key.
            int openQuote = colon + 1;
            while (openQuote < json.Length && IsWhiteSpace(json[openQuote]))
            {
                openQuote++;
            }

            if (openQuote >= json.Length || json[openQuote] != '"')
            {
                return null;
            }

            int start = openQuote + 1;
            int end = -1;
            int i = start;

            while (i < json.Length)
            {
                char c = json[i];

                if (c == '\\')
                {
                    // Skip the escaped character.
                    i = i + 2;
                    continue;
                }

                if (c == '"')
                {
                    end = i;
                    break;
                }

                if (c == '\r' || c == '\n')
                {
                    // Unterminated on this line: stop at the line end. This keeps a
                    // hand-edited value that ends with a backslash from swallowing
                    // the rest of the file.
                    break;
                }

                i++;
            }

            if (end < 0)
            {
                end = i;
            }

            if (end <= start)
            {
                return string.Empty;
            }

            string raw = json.Substring(start, end - start);
            return Unescape(raw);
        }

        // Escaped double backslashes and escaped quotes are decoded. A value that
        // contains no "\\" at all is a hand-edited single-backslash path and is
        // returned unchanged.
        private static string Unescape(string raw)
        {
            if (raw == null || raw.Length == 0)
            {
                return raw;
            }

            if (raw.IndexOf("\\\\", StringComparison.Ordinal) < 0)
            {
                return raw;
            }

            StringBuilder builder = new StringBuilder(raw.Length);
            int i = 0;
            while (i < raw.Length)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char next = raw[i + 1];
                    if (next == '\\')
                    {
                        builder.Append('\\');
                        i = i + 2;
                        continue;
                    }
                    if (next == '"')
                    {
                        builder.Append('"');
                        i = i + 2;
                        continue;
                    }
                }

                builder.Append(c);
                i++;
            }

            return builder.ToString();
        }

        // Find "key", then the next ':', then the next run of digits.
        private static int ReadIntValue(string json, string key)
        {
            if (json == null)
            {
                return -1;
            }

            int keyIndex = FindKey(json, key);
            if (keyIndex < 0)
            {
                return -1;
            }

            int colon = json.IndexOf(':', keyIndex);
            if (colon < 0)
            {
                return -1;
            }

            int i = colon + 1;
            while (i < json.Length && !IsDigit(json[i]))
            {
                // Stop at the end of this field so digits belonging to a later
                // value can never be picked up.
                char c = json[i];
                if (c == ',' || c == '}' || c == ']' || c == '\r' || c == '\n')
                {
                    return -1;
                }
                i++;
            }

            if (i >= json.Length)
            {
                return -1;
            }

            int start = i;
            while (i < json.Length && IsDigit(json[i]))
            {
                i++;
            }

            string digits = json.Substring(start, i - start);
            int value;
            if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out value))
            {
                return -1;
            }

            return value;
        }

        // Index just past the quoted key name, or -1 when absent.
        private static int FindKey(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int index = json.IndexOf(needle, StringComparison.Ordinal);
            if (index < 0)
            {
                return -1;
            }

            return index + needle.Length;
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static bool IsWhiteSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n';
        }

        // ---------------------------------------------------------------
        // Serialization
        // ---------------------------------------------------------------

        // Writes the shape the loader expects, escaping backslashes in daemonExe as "\\".
        //
        // openMode / edgeAppId 必须一起写回：这份 JSON 里存着「打开方式」，
        // 而本函数只负责刷新端口 —— 早先只写 daemonExe/webPort，会把用户的
        // 打开方式抹成默认值。
        private static string BuildJson(string daemonExe, int webPort, string openMode, string edgeAppId)
        {
            string path = daemonExe == null ? string.Empty : daemonExe;
            path = path.Replace("\\", "\\\\").Replace("\"", "\\\"");

            string mode = (openMode == "app") ? "app" : "browser";
            string appId = edgeAppId == null ? string.Empty : edgeAppId;

            StringBuilder builder = new StringBuilder();
            builder.Append("{\r\n");
            builder.Append("  \"daemonExe\": \"").Append(path).Append("\",\r\n");
            builder.Append("  \"webPort\": ");
            builder.Append(webPort.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\r\n");
            builder.Append("  \"openMode\": \"").Append(mode).Append("\",\r\n");
            builder.Append("  \"edgeAppId\": \"").Append(appId).Append("\"\r\n");
            builder.Append("}\r\n");
            return builder.ToString();
        }
    }
}
