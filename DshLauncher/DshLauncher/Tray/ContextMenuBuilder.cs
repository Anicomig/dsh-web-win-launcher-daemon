// Tray\ContextMenuBuilder.cs - builds the right-click menu, applies the dark
// theme, and wires the actions to DaemonController.
//
// The menu is state-driven: labels and enabled state are recomputed every time it
// opens, from the JSON config plus the live process state.
//
//   Daemon item  - one entry that toggles. Label is the ACTION to perform:
//                  "启动后台 Daemon" when stopped, "关闭后台 Daemon" when running.
//   config item  - opens the configuration window (ConfigHost\DshConfigHost.exe).
//   dsh web item - ONE entry with three labels, each naming the action:
//                    daemon running    -> "重启 dsh web"  (kill node once; the daemon
//                                          then starts a fresh one)
//                    web up, no daemon -> "关闭 dsh web"  (kill node once; with no
//                                          daemon nothing starts it again)
//                    neither running   -> "启动 dsh web"  (the tray launches node
//                                          itself, see DshWebLauncher.cs)
//
// Result reporting: successes go to a balloon tip (unobtrusive), failures go to a
// MessageBox (they need acknowledging).

using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshLauncher.Tray
{
    internal sealed class ContextMenuBuilder : IDisposable
    {
        private NotifyIcon _icon;
        private ContextMenuStrip _menu;
        private DaemonController _daemon;

        private ToolStripMenuItem _configWarning;
        private ToolStripSeparator _configSeparator;
        private ToolStripMenuItem _openWeb;
        private ToolStripMenuItem _daemonToggle;
        private ToolStripMenuItem _dshWebAction;
        private ToolStripMenuItem _openLog;
        private ToolStripMenuItem _configItem;
        private ToolStripMenuItem _exitBoth;
        private ToolStripMenuItem _exitAll;
        private ToolStripMenuItem _exitLauncher;

        public event EventHandler RequestExit;

        public void WireTo(NotifyIcon icon)
        {
            _icon = icon;

            // Load() never throws. Refresh the port from the daemon's Config.ps1 once
            // at startup so the JSON always carries the port the daemon will use.
            TrayConfig startupConfig = TrayConfig.Load();
            startupConfig.RefreshPortFromDaemonConfig();

            // Passing the static loader as a delegate makes DaemonController re-read
            // the JSON file for every single operation.
            _daemon = new DaemonController(TrayConfig.Load);

            _menu = BuildMenu();

            // Deliberately NOT subscribing Opening. ShowAtCursor already refreshes the
            // menu state before measuring; refreshing again while the drop-down is
            // being shown would change properties (Padding, item visibility) mid-show,
            // which can make ToolStripDropDown recompute its position and undo the
            // anchoring. Keeping the refresh strictly before Show avoids that.
            //
            // Deliberately NOT assigning icon.ContextMenuStrip either: that path goes
            // through TrackPopupMenu, which always anchors the menu's TOP-LEFT corner
            // at the cursor and offers no way to change it. The tray calls
            // ShowAtCursor() from its MouseUp handler instead.
        }

        /// <summary>
        /// Shows the menu anchored by its TOP-RIGHT corner at the cursor, so it opens
        /// up and to the left of the pointer.
        ///
        /// The menu size is measured after refreshing the item labels and visibility,
        /// because those change the width. The position is then clamped to the working
        /// area of the screen under the cursor: if the menu would leave the screen on
        /// the left it flips to the cursor's right, and it is nudged up when it would
        /// overflow the bottom.
        /// </summary>
        public void ShowAtCursor()
        {
            if (_menu == null)
            {
                return;
            }

            // Refresh first: item text and visibility change the measured size.
            RefreshMenuState();

            Point cursor = Cursor.Position;
            Size size = _menu.GetPreferredSize(Size.Empty);
            if (size.Width <= 0 || size.Height <= 0)
            {
                size = _menu.Size;
            }

            Rectangle work = Screen.FromPoint(cursor).WorkingArea;

            // Place the menu UP AND TO THE RIGHT of the pointer: the menu's
            // BOTTOM-LEFT corner sits at the cursor.
            //
            // The tray icon lives in the bottom-right corner of the screen, so the
            // vertical part is effectively "open upwards" (there is never room below)
            // and the horizontal part is "open to the right". The flips below only
            // kick in near the opposite edges.
            int x = cursor.X;                // menu's left edge at the cursor
            int y = cursor.Y - size.Height;  // menu's bottom edge at the cursor

            if (x + size.Width > work.Right) // no room on the right: flip to the left
            {
                x = cursor.X - size.Width;
            }
            if (x < work.Left)               // still overflowing: clamp to the edge
            {
                x = work.Left;
            }
            if (x + size.Width > work.Right)
            {
                x = work.Right - size.Width;
            }

            if (y < work.Top)                // no room above: open downwards
            {
                y = cursor.Y;
            }
            if (y + size.Height > work.Bottom)
            {
                y = work.Bottom - size.Height;
            }
            if (y < work.Top)
            {
                y = work.Top;
            }

            EnsureAnchorWindow();

            // Give the menu the anchor as its Win32 owner BEFORE it is ever shown, and
            // then show it with ABSOLUTE coordinates.
            //
            // Both halves were established by measurement:
            //
            //   * ToolStripDropDown.Show(Control, Point) does NOT reliably apply an
            //     owner - in the real tray process the menu window came back with
            //     "owner = NONE", and an unowned, visible, top-level window without
            //     WS_EX_TOOLWINDOW is exactly what the shell awards a taskbar button
            //     to. Setting GWLP_HWNDPARENT explicitly (touching Handle creates the
            //     window while it is still hidden) stops the button being created, and
            //     the owner survives the subsequent Show.
            //
            //   * Positioning relative to the anchor was racy: setting
            //     anchor.Location and immediately calling Show(anchor, offset) could
            //     use the anchor's previous position, which measured as a 48x36 px
            //     drift. Show(x, y) takes absolute screen coordinates and lands
            //     exactly, so the anchor never has to move at all.
            IntPtr menuHandle = _menu.Handle;
            IntPtr anchorHandle = _anchorWindow.Handle;
            IntPtr ownerBefore = GetWindow(menuHandle, GwOwner);

            // WS_EX_TOOLWINDOW is what actually keeps the taskbar button away.
            //
            // ToolStripDropDown.Show() RESETS the Win32 owner to NULL, measured
            // directly: ownerAfterSet=0x9B0EB4 then ownerAfterShow=NONE. An unowned,
            // visible, top-level window without WS_EX_TOOLWINDOW is precisely what the
            // shell awards a taskbar button to, so setting the owner alone cannot work.
            // The extended style survives Show, so setting it first means the button is
            // never created at all.
            SetToolWindow(menuHandle, true);

            _menu.Show(x, y);

            // Show() cleared the owner: put it back so the menu still behaves as a
            // popup of the anchor (correct z-order, dismissed along with its owner).
            try
            {
                SetWindowOwner(menuHandle, anchorHandle);
            }
            catch
            {
            }

            IntPtr ownerAfterShow = GetWindow(menuHandle, GwOwner);
            _lastDiag = "anchor=0x" + anchorHandle.ToInt64().ToString("X")
                + " ownerBefore=" + Fmt(ownerBefore)
                + " ownerAfterShow=" + Fmt(ownerAfterShow)
                + " toolWindow=" + IsToolWindow(menuHandle);

            // Must come AFTER Show: this is what gives the menu foreground activation,
            // without which clicking elsewhere does not dismiss it.
            try
            {
                SetForegroundWindow(_menu.Handle);
            }
            catch
            {
            }

            LogPlacement(cursor, size, x, y);
        }

        // ---- hidden owner window -----------------------------------------

        private Form _anchorWindow;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // GWLP_HWNDPARENT = -8. For a top-level window this sets the OWNER.
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr value);

        private static void SetWindowOwner(IntPtr child, IntPtr owner)
        {
            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(child, -8, owner);
            }
            else
            {
                SetWindowLong32(child, -8, owner);
            }
        }

        private const uint GwOwner = 4;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x00000080;

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

        private static int ExStyle(IntPtr hWnd)
        {
            return (int)(IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, GwlExStyle)
                : GetWindowLong32(hWnd, GwlExStyle));
        }

        private static void SetToolWindow(IntPtr hWnd, bool on)
        {
            int current = ExStyle(hWnd);
            int wanted = on ? (current | WsExToolWindow) : (current & ~WsExToolWindow);
            if (wanted == current)
            {
                return;
            }

            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(hWnd, GwlExStyle, new IntPtr(wanted));
            }
            else
            {
                SetWindowLong32(hWnd, GwlExStyle, new IntPtr(wanted));
            }
        }

        private static bool IsToolWindow(IntPtr hWnd)
        {
            return (ExStyle(hWnd) & WsExToolWindow) != 0;
        }

        private string _lastDiag = string.Empty;

        private static string Fmt(IntPtr h)
        {
            return h == IntPtr.Zero ? "NONE" : "0x" + h.ToInt64().ToString("X");
        }

        /// <summary>
        /// A 1x1 borderless window parked off-screen. It exists only so the menu has a
        /// real owner: an owned popup gets no taskbar button and behaves like a menu,
        /// whereas an unowned drop-down is a plain top-level window.
        /// </summary>
        private void EnsureAnchorWindow()
        {
            if (_anchorWindow != null)
            {
                return;
            }

            _anchorWindow = new Form();
            _anchorWindow.FormBorderStyle = FormBorderStyle.None;
            _anchorWindow.ShowInTaskbar = false;
            _anchorWindow.StartPosition = FormStartPosition.Manual;
            _anchorWindow.MinimizeBox = false;
            _anchorWindow.MaximizeBox = false;
            _anchorWindow.ShowIcon = false;
            _anchorWindow.Size = new Size(1, 1);
            _anchorWindow.Location = new Point(-32000, -32000);
            _anchorWindow.Show();
        }

        // ---- placement diagnostics ---------------------------------------

        private const string PlacementLogName = "tray_menu.log";
        private const long PlacementLogMaxBytes = 128 * 1024;

        /// <summary>
        /// Records where the menu was asked to appear and where it actually appeared.
        /// Written next to the executable; truncated once it passes 128 KB.
        /// </summary>
        private void LogPlacement(Point cursor, Size size, int wantX, int wantY)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = System.IO.Path.Combine(dir ?? ".", PlacementLogName);

                System.IO.FileInfo info = new System.IO.FileInfo(path);
                if (info.Exists && info.Length > PlacementLogMaxBytes)
                {
                    System.IO.File.Delete(path);
                }

                int actualX = -1;
                int actualY = -1;
                try
                {
                    Rectangle bounds = _menu.Bounds;
                    actualX = bounds.X;
                    actualY = bounds.Y;
                }
                catch
                {
                }

                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + "  cursor=" + cursor.X + "," + cursor.Y
                    + "  measured=" + size.Width + "x" + size.Height
                    + "  requested=" + wantX + "," + wantY
                    + "  actual=" + actualX + "," + actualY
                    + "  right=" + (actualX + size.Width)
                    + "  bottom=" + (actualY + size.Height)
                    + "  " + _lastDiag
                    + Environment.NewLine;

                System.IO.File.AppendAllText(path, line);
            }
            catch
            {
                // Diagnostics must never break the menu.
            }
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            _configWarning = new ToolStripMenuItem("配置读取异常");
            _configWarning.Enabled = false;
            _configWarning.Visible = false;

            _configSeparator = new ToolStripSeparator();
            _configSeparator.Visible = false;

            _openWeb = new ToolStripMenuItem("打开网页 (3080)");
            _daemonToggle = new ToolStripMenuItem("启动后台 Daemon");
            _dshWebAction = new ToolStripMenuItem("关闭 dsh web");
            _openLog = new ToolStripMenuItem("打开 log");
            _configItem = new ToolStripMenuItem("配置");

            ToolStripSeparator sep = new ToolStripSeparator();
            _exitBoth = new ToolStripMenuItem("退出 Daemon 及 Launcher");
            _exitAll = new ToolStripMenuItem("关闭 dsh web，Daemon 及 Launcher");
            _exitLauncher = new ToolStripMenuItem("仅退出 Launcher");

            _openWeb.Click += new EventHandler(OnOpenWeb);
            _daemonToggle.Click += new EventHandler(OnDaemonToggle);
            _dshWebAction.Click += new EventHandler(OnDshWebAction);
            _openLog.Click += new EventHandler(OnOpenLog);
            _configItem.Click += new EventHandler(OnConfig);
            _exitBoth.Click += new EventHandler(OnExitBoth);
            _exitAll.Click += new EventHandler(OnExitAll);
            _exitLauncher.Click += new EventHandler(OnExitLauncher);

            menu.Items.AddRange(new ToolStripItem[] {
                _configWarning, _configSeparator,
                _openWeb, _daemonToggle, _dshWebAction, _openLog, _configItem,
                sep, _exitBoth, _exitAll, _exitLauncher
            });
            return menu;
        }


        /// <summary>
        /// Recomputes every dynamic part of the menu from the current state: the port
        /// label, the two state-dependent item labels, and the config warning row.
        /// Idempotent, so it is safe to call both before measuring (ShowAtCursor) and
        /// again from Opening.
        /// </summary>
        private void RefreshMenuState()
        {
            TrayConfig cfg = TrayConfig.Load();

            _openWeb.Text = (cfg.OpenMode == "app" && !string.IsNullOrEmpty(cfg.EdgeAppId))
                ? "打开网页应用 (" + cfg.WebPort + ")"
                : "打开网页 (" + cfg.WebPort + ")";

            bool daemonRunning = _daemon.IsDaemonRunning();

            // One entry, two labels. The label names the action, not the state.
            _daemonToggle.Text = daemonRunning ? "关闭后台 Daemon" : "启动后台 Daemon";

            // One entry, three labels; the label always names the action. The branches
            // are resolved in the same order the click handler dispatches, so the label
            // can never disagree with what the click will do.
            if (daemonRunning)
            {
                // The daemon keeps node alive, so killing it amounts to a restart.
                _dshWebAction.Text = "重启 dsh web";
            }
            else if (_daemon.IsDshWebRunning())
            {
                // Nothing would start node again, so killing it really closes it.
                _dshWebAction.Text = "关闭 dsh web";
            }
            else
            {
                // Nothing is running: this click starts dsh web.
                _dshWebAction.Text = "启动 dsh web";
            }

            bool hasWarning = !string.IsNullOrEmpty(cfg.LoadError);
            _configWarning.Visible = hasWarning;
            _configSeparator.Visible = hasWarning;
            if (hasWarning)
            {
                _configWarning.Text = "配置异常: " + Truncate(cfg.LoadError, 64);
            }

            // Idempotent: safe to reapply on every open.
            DarkMenuRenderer.Apply(_menu);
        }

        private void OnOpenWeb(object sender, EventArgs e)
        {
            Report(_daemon.OpenWeb());
        }

        private void OnDaemonToggle(object sender, EventArgs e)
        {
            if (_daemon.IsDaemonRunning())
            {
                Report(_daemon.StopDaemon());
                return;
            }

            // Starting: take the port from the daemon's own config first.
            TrayConfig cfg = TrayConfig.Load();
            cfg.RefreshPortFromDaemonConfig();
            Report(_daemon.StartDaemon());
        }

        // Three states share this single entry. The branches mirror RefreshMenuState
        // exactly, so the label the user just read always describes this click.
        private void OnDshWebAction(object sender, EventArgs e)
        {
            if (_daemon.IsDaemonRunning())
            {
                // Restart: kill node once, then the daemon starts a fresh one.
                Report(_daemon.StopDshWeb());
                return;
            }

            if (_daemon.IsDshWebRunning())
            {
                // Close: kill node once; with no daemon nothing brings it back.
                Report(_daemon.StopDshWeb());
                return;
            }

            // Start: nothing was running, so launch dsh web directly.
            Report(_daemon.StartDshWeb());
        }

        // 配置窗口需要安装根目录（用来定位 Config.ps1 与 autostart\ 里的快捷方式）。
        // 它自己会按所在位置反推，托盘只需把根目录传过去即可。
        private void OnConfig(object sender, EventArgs e)
        {
            TrayConfig cfg = TrayConfig.Load();

            string trayExe = string.Empty;
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetEntryAssembly();
                if (asm != null && asm.Location != null)
                {
                    trayExe = asm.Location;
                }
            }
            catch (Exception)
            {
                // 拿不到自身路径不影响打开页面，paths.js 里该项留空即可
            }

            Report(ConfigPageLauncher.OpenConfigPage(cfg.DaemonDirectory, trayExe));
        }
        private void OnOpenLog(object sender, EventArgs e)
        {
            Report(_daemon.OpenLogFolder());
        }

        // Leaves dsh web running: only the daemon and this tray exit.
        private void OnExitBoth(object sender, EventArgs e)
        {
            _daemon.StopDaemon();
            RaiseExit();
        }

        // Full stop: dsh web, then the daemon, then this tray.
        private void OnExitAll(object sender, EventArgs e)
        {
            _daemon.StopDshWeb();
            _daemon.StopDaemon();
            RaiseExit();
        }

        private void OnExitLauncher(object sender, EventArgs e)
        {
            RaiseExit();
        }

        private void RaiseExit()
        {
            EventHandler handler = RequestExit;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void Report(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            if (IsErrorResult(message))
            {
                MessageBox.Show(message, "DSH Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_icon != null)
            {
                _icon.BalloonTipTitle = "DSH Launcher";
                _icon.BalloonTipText = message;
                _icon.ShowBalloonTip(2500);
            }
        }

        // DaemonController reports failures with one of these markers; everything
        // else is a success or an informational message.
        private static bool IsErrorResult(string message)
        {
            return message.IndexOf("失败", StringComparison.Ordinal) >= 0
                || message.IndexOf("找不到", StringComparison.Ordinal) >= 0
                || message.IndexOf("不存在", StringComparison.Ordinal) >= 0;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.Length <= max) return text;
            return text.Substring(0, max) + "...";
        }

        public void Dispose()
        {
            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }

            if (_anchorWindow != null)
            {
                _anchorWindow.Dispose();
                _anchorWindow = null;
            }
        }
    }
}
