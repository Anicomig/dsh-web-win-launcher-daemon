// Program.cs - DshLauncher entry point.
// Hosts a NotifyIcon in an ApplicationContext so the process has no visible window.
// The tray icon stays alive until "仅退出 Launcher" is selected from the menu.
//
// Startup diagnostics are written to tray_startup.log next to the executable.
// A windowless app that fails to show its icon has no other way to report why:
// Shell_NotifyIcon fails silently when the process is not on the interactive
// window station (WinSta0), which is the first thing worth ruling out.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DshLauncher.Tray;

namespace DshLauncher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Single instance: without this guard each launch adds another tray icon.
            // (Observed during acceptance testing: four icons from four launches.)
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(
                       true, @"Local\DshLauncher.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    Log("another instance is already running; exiting");
                    return 0;
                }

                return RunTray();
            }
        }

        private static int RunTray()
        {
            Log("=== DshLauncher start ===");
            Log("assembly    = " + Assembly.GetExecutingAssembly().Location);
            Log("session     = " + Process.GetCurrentProcess().SessionId);
            Log("winSta      = " + GetWindowStationName());
            Log("desktop     = " + GetDesktopName());
            Log("interactive = " + Environment.UserInteractive);

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var host = new NotifyIconHost())
                {
                    host.Initialize();
                    Log("NotifyIcon: " + DescribeNotifyIcon(host.NotifyIconInternal));
                    Log("Visible    = " + (host.NotifyIconInternal != null
                        ? host.NotifyIconInternal.Visible.ToString() : "(null)"));

                    var ctx = new TrayApplicationContext(host);
                    Log("entering Application.Run (message loop)");
                    Application.Run(ctx);
                    Log("Application.Run returned");
                }
                Log("=== clean exit ===");
                return 0;
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex.GetType().FullName + ": " + ex.Message);
                Log(ex.StackTrace);
                return 1;
            }
        }

        // ---- diagnostics -------------------------------------------------

        private static string _logPath;

        private static void Log(string message)
        {
            try
            {
                if (_logPath == null)
                {
                    string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    _logPath = Path.Combine(dir ?? ".", "tray_startup.log");
                }
                File.AppendAllText(
                    _logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never break the app.
            }
        }

        private const int UOI_NAME = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetProcessWindowStation();

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetUserObjectInformation(
            IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

        private static string GetWindowStationName()
        {
            return QueryObjectName(GetProcessWindowStation());
        }

        private static string GetDesktopName()
        {
            return QueryObjectName(GetThreadDesktop(GetCurrentThreadId()));
        }

        private static string QueryObjectName(IntPtr handle)
        {
            try
            {
                var sb = new StringBuilder(256);
                int needed;
                if (GetUserObjectInformation(handle, UOI_NAME, sb, sb.Capacity * 2, out needed))
                {
                    return sb.ToString();
                }
                return "(query failed, err=" + Marshal.GetLastWin32Error() + ")";
            }
            catch (Exception ex)
            {
                return "(error: " + ex.Message + ")";
            }
        }

        // Report the NotifyIcon's internal window handle: a non-zero handle proves
        // the hidden message window exists, which is what Shell_NotifyIcon needs.
        private static string DescribeNotifyIcon(NotifyIcon ni)
        {
            if (ni == null) return "(notify icon is null)";
            try
            {
                FieldInfo field = typeof(NotifyIcon).GetField("_window",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null)
                {
                    field = typeof(NotifyIcon).GetField("window",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                if (field == null) return "(internal window field not found)";

                var window = field.GetValue(ni) as NativeWindow;
                if (window == null) return "(internal window is null)";
                return "handle=0x" + window.Handle.ToInt64().ToString("X");
            }
            catch (Exception ex)
            {
                return "(reflect error: " + ex.Message + ")";
            }
        }

        // ---- application context ----------------------------------------

        // ApplicationContext subclass that holds the NotifyIcon host so
        // Application.ExitThread() / Application.Exit() can find it on dispose.
        // No OnMainFormClosed override: there is no main form; the tray icon
        // IS the form for lifecycle purposes.
        private sealed class TrayApplicationContext : ApplicationContext
        {
            private readonly NotifyIconHost _host;

            public TrayApplicationContext(NotifyIconHost host)
            {
                _host = host;
                _host.RequestExit += new EventHandler(OnRequestExit);
            }

            private void OnRequestExit(object sender, EventArgs e)
            {
                Log("exit requested from tray menu");
                ExitThread();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _host.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
