// Properties\Resources.Designer.cs - hand-written equivalent of the VS-generated
// designer.
//
// build.cmd embeds TWO top-level manifest resources:
//   /resource:DshLauncher.ico,DshLauncher.Properties.Resources.dshLauncherIcon
//   /resource:DshLauncher_light.ico,DshLauncher.Properties.Resources.dshLauncherIconLight
//
// The first is the dark-taskbar variant (white glyph), the second the
// light-taskbar variant (black glyph). A black glyph on a dark taskbar is
// invisible, which is exactly what happened during acceptance testing.
//
// Do NOT use ComponentResourceManager here: it looks for a
// "DshLauncher.Properties.Resources.resources" container, which does not exist for
// a top-level resource, and throws MissingManifestResourceException
// (observed as process exit code 0xE0434352). Read the manifest stream directly.

using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace DshLauncher.Properties
{
    internal static class Resources
    {
        /// <summary>White glyph, for a dark taskbar.</summary>
        private const string IconDarkResourceName =
            "DshLauncher.Properties.Resources.dshLauncherIcon";

        /// <summary>Black glyph, for a light taskbar.</summary>
        private const string IconLightResourceName =
            "DshLauncher.Properties.Resources.dshLauncherIconLight";

        /// <summary>Dark-taskbar variant (white glyph).</summary>
        public static Icon DshLauncherIcon
        {
            get { return LoadIcon(IconDarkResourceName, "DshLauncher.ico"); }
        }

        /// <summary>Light-taskbar variant (black glyph).</summary>
        public static Icon DshLauncherIconLight
        {
            get { return LoadIcon(IconLightResourceName, "DshLauncher_light.ico"); }
        }

        private static Icon LoadIcon(string resourceName, string fallbackFileName)
        {
            Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resourceName);

            if (stream != null)
            {
                // Copy into a MemoryStream: Icon may keep reading from the source
                // stream, and the manifest stream must not be disposed underneath it.
                byte[] bytes;
                using (stream)
                {
                    bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                return new Icon(new MemoryStream(bytes));
            }

            // Fallback: a copy of the .ico sitting next to the executable.
            string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string icoPath = Path.Combine(asmDir ?? ".", fallbackFileName);
            if (File.Exists(icoPath))
            {
                return new Icon(icoPath);
            }

            // Last resort: the system application icon. Never returns null.
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
