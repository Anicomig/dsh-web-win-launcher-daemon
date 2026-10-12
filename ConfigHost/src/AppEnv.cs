using System;
using System.IO;
using System.Reflection;

namespace ConfigHost
{
    /// <summary>
    /// DshConfigHost 对磁盘布局的唯一认知来源。exe 位于 &lt;安装根&gt;\ConfigHost\，
    /// 所以安装根 = exe 所在目录的上一级 —— 不需要托盘传参，双击即可用。
    ///
    /// 注意：这个推断只在产物被放到 ConfigHost\ 时成立。bin\Debug\net8.0-windows\
    /// 里的开发产物推出来的安装根是错的，所以所有运行验证都必须先把 exe
    /// 复制到 &lt;安装根&gt;\ConfigHost\ 再跑（ConfigHost\build.cmd 负责这件事）。
    /// </summary>
    internal static class AppEnv
    {
        public const string ConfigFileName  = "DshLauncherDaemon_Config.ps1";
        public const string DaemonExeName   = "DshLauncherDaemon.exe";
        public const string DaemonProcName  = "DshLauncherDaemon";
        public const string DefaultsFile    = "defaults.json";

        public static string HostDir { get; } = AppContext.BaseDirectory.TrimEnd('\\');

        public static string InstallRoot { get; } =
            Path.GetDirectoryName(HostDir.TrimEnd('\\')) ?? HostDir;

        public static string ConfigFile      { get { return Path.Combine(InstallRoot, ConfigFileName); } }
        public static string DaemonExe       { get { return Path.Combine(InstallRoot, DaemonExeName); } }
        public static string AutostartSrcDir { get { return Path.Combine(InstallRoot, "autostart"); } }
        public static string StartupDir      { get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); } }
        public static string DefaultsPath    { get { return Path.Combine(HostDir, DefaultsFile); } }

        public static string HostVersion
        {
            get
            {
                var a = Assembly.GetExecutingAssembly();
                var v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (v == null || string.IsNullOrEmpty(v.InformationalVersion)) return "0.0.0";
                return v.InformationalVersion.Split('+')[0];
            }
        }

        public static bool ConfigExists { get { return File.Exists(ConfigFile); } }
    }
}
