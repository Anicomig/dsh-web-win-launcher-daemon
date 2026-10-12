// Tray\DarkMenuRenderer.cs - dark theme for the tray ContextMenuStrip.
//
// The default WinForms professional renderer paints a light grey menu, which
// looks out of place next to the dark tray icon and the dark web UI. This file
// holds a dark colour table plus one static helper (Apply) that installs the
// renderer, the colours and the border in a single call.
//
// Target: .NET Framework 4.0 / C# 5. No null-conditional operator, no string
// interpolation, no expression-bodied members, no nameof.

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshLauncher.Tray
{
    // A dark colour table plus a helper that applies the whole dark theme to a
    // ContextMenuStrip: renderer, colours, border, and Windows 11 rounded corners.
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        // Dark palette. Values are fixed by the design table at the top of the
        // file header comment.
        private static readonly Color MenuBackground  = Color.FromArgb(31, 31, 31);   // #1F1F1F
        private static readonly Color BorderColor     = Color.FromArgb(58, 58, 58);   // #3A3A3A
        private static readonly Color NormalText      = Color.FromArgb(232, 232, 232); // #E8E8E8
        private static readonly Color HoverBackground = Color.FromArgb(45, 90, 135);  // #2D5A87
        private static readonly Color HoverText       = Color.FromArgb(255, 255, 255); // #FFFFFF
        private static readonly Color PressedBackground = Color.FromArgb(30, 78, 121); // #1E4E79
        private static readonly Color DisabledText    = Color.FromArgb(120, 120, 120);

        public DarkMenuRenderer()
            : base(new DarkColorTable())
        {
            // Plain dark rectangle: no rounded top corners.
            RoundedEdges = false;
        }

        // Applies renderer + colours to the supplied menu so the caller only needs
        // one call. Idempotent and safe to call on every menu open.
        public static void Apply(ContextMenuStrip menu)
        {
            if (menu == null)
            {
                return;
            }

            // A fresh renderer every time is cheap and keeps this method free of
            // any state that could accumulate across menu opens.
            menu.Renderer = new DarkMenuRenderer();

            menu.BackColor = MenuBackground;
            menu.ForeColor = NormalText;
            menu.ShowImageMargin = false;
            menu.DropShadowEnabled = true;
            menu.Padding = new Padding(2);

            ApplyToItems(menu.Items);
            ApplyRoundedCorners(menu);
        }

        // ---- Windows 11 rounded corners ----------------------------------

        // DWMWA_WINDOW_CORNER_PREFERENCE, Windows 11 (build 22000) and later.
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Asks the Desktop Window Manager to round the menu's corners. This is how
        /// Windows 11 draws its own menus, so the result is properly anti-aliased -
        /// unlike a GDI+ Region clip, which is visibly jagged.
        ///
        /// Touching Handle forces the popup window into existence, which must happen
        /// before the attribute can be set. On Windows 10 and earlier the call simply
        /// fails and the menu keeps square corners; that is not an error.
        /// </summary>
        private static void ApplyRoundedCorners(ContextMenuStrip menu)
        {
            try
            {
                IntPtr hwnd = menu.Handle;
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                int preference = DwmwcpRound;
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, 4);
            }
            catch
            {
                // No DWM, or an OS that predates the attribute: keep square corners.
            }
        }

        // Walks the item tree and pushes the dark colours down to every item.
        // No event handlers are attached here, so calling Apply repeatedly cannot
        // accumulate subscriptions.
        private static void ApplyToItems(ToolStripItemCollection items)
        {
            if (items == null)
            {
                return;
            }

            for (int i = 0; i < items.Count; i++)
            {
                ToolStripItem item = items[i];
                if (item == null)
                {
                    continue;
                }

                // Separators have no visible text, but their colours must not stay
                // at the light defaults inherited from the tool strip.
                item.ForeColor = NormalText;

                // Transparent lets the renderer paint the hover / pressed state.
                item.BackColor = Color.Transparent;

                ToolStripDropDownItem dropDownItem = item as ToolStripDropDownItem;
                if (dropDownItem != null)
                {
                    ApplyToItems(dropDownItem.DropDownItems);
                }
            }
        }

        // Text colour is decided here instead of relying on the item ForeColor,
        // because the base renderer switches to a system highlight colour for the
        // selected item.
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item != null)
            {
                if (!e.Item.Enabled)
                {
                    e.TextColor = DisabledText;
                }
                else if (e.Item.Selected || e.Item.Pressed)
                {
                    e.TextColor = HoverText;
                }
                else
                {
                    e.TextColor = NormalText;
                }
            }

            base.OnRenderItemText(e);
        }

        // Dark colour table for the professional renderer.
        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected
            {
                get { return HoverBackground; }
            }

            public override Color MenuItemSelectedGradientBegin
            {
                get { return HoverBackground; }
            }

            public override Color MenuItemSelectedGradientEnd
            {
                get { return HoverBackground; }
            }

            public override Color MenuItemPressedGradientBegin
            {
                get { return PressedBackground; }
            }

            public override Color MenuItemPressedGradientMiddle
            {
                get { return PressedBackground; }
            }

            public override Color MenuItemPressedGradientEnd
            {
                get { return PressedBackground; }
            }

            public override Color MenuItemBorder
            {
                get { return BorderColor; }
            }

            public override Color MenuBorder
            {
                get { return BorderColor; }
            }

            public override Color ToolStripDropDownBackground
            {
                get { return MenuBackground; }
            }

            public override Color ImageMarginGradientBegin
            {
                get { return MenuBackground; }
            }

            public override Color ImageMarginGradientMiddle
            {
                get { return MenuBackground; }
            }

            public override Color ImageMarginGradientEnd
            {
                get { return MenuBackground; }
            }

            public override Color SeparatorDark
            {
                get { return BorderColor; }
            }

            public override Color SeparatorLight
            {
                get { return BorderColor; }
            }
        }
    }
}
