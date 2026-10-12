// Tray\NotifyIconHost.cs - owns the NotifyIcon instance and exposes events
// the context menu wires to. Holds the menu builder and the embedded icon.

using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshLauncher.Tray
{
    internal sealed class NotifyIconHost : IDisposable
    {
        private NotifyIcon _notifyIcon;
        private ContextMenuBuilder _menu;
        private bool _initialized;

        public event EventHandler RequestExit;

        /// <summary>
        /// The underlying NotifyIcon, exposed only so startup diagnostics in
        /// Program.cs can report its internal window handle and Visible state.
        /// </summary>
        internal NotifyIcon NotifyIconInternal
        {
            get { return _notifyIcon; }
        }

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _notifyIcon = new NotifyIcon
            {
                Icon       = LoadIcon(),
                Text       = "DSH Launcher Daemon",
                Visible    = true,
            };

            _menu = new ContextMenuBuilder();
            _menu.WireTo(_notifyIcon);
            _menu.RequestExit += new EventHandler(OnMenuRequestExit);

            // The menu is shown by hand rather than through NotifyIcon.ContextMenuStrip,
            // so it can be anchored by its top-right corner. See ShowAtCursor.
            _notifyIcon.MouseUp += new MouseEventHandler(OnTrayMouseUp);
            _notifyIcon.DoubleClick += new EventHandler(OnTrayDoubleClick);
        }

        private void OnMenuRequestExit(object sender, EventArgs e)
        {
            var handler = RequestExit;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void OnTrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            if (_menu != null)
            {
                _menu.ShowAtCursor();
            }
        }

        private void OnTrayDoubleClick(object sender, EventArgs e)
        {
            // Double-click is reserved for a future "open GUI" action; for now it
            // opens the same menu.
            if (_menu != null)
            {
                _menu.ShowAtCursor();
            }
        }

        private static Icon LoadIcon()
        {
            // A black glyph is invisible on a dark taskbar and a white glyph is
            // invisible on a light one. Pick the variant matching the system theme.
            // (Acceptance testing initially shipped only the black glyph and the
            // icon was effectively invisible on this machine's dark taskbar.)
            if (IsLightTaskbar())
            {
                return Properties.Resources.DshLauncherIconLight;
            }
            return Properties.Resources.DshLauncherIcon;
        }

        /// <summary>
        /// Reads the taskbar theme from the per-user personalization settings.
        /// SystemUsesLightTheme = 1 means a light taskbar (needs the black glyph).
        /// </summary>
        private static bool IsLightTaskbar()
        {
            const string personalizeKey =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(personalizeKey))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("SystemUsesLightTheme");
                        if (value != null)
                        {
                            return Convert.ToInt32(value) == 1;
                        }
                    }
                }
            }
            catch
            {
                // Unreadable or malformed value: fall through to the default.
            }

            // Windows 11 ships with a dark taskbar; assume dark when unknown.
            return false;
        }

        public void Dispose()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }
        }
    }
}
