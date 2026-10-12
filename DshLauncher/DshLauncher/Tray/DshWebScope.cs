// Tray\DshWebScope.cs - decides whether a node process is "the dsh web we own".
//
// The tray used to match any node whose command line looked like dsh web, which
// also caught a second instance the user started by hand on another port: "关闭
// dsh web" configured for 3080 would kill the 3300 one too.
//
// Both the daemon and the tray pass an explicit --port, so the command line is
// the primary source of truth. Listening-socket ownership is the fallback for a
// web started without --port (it then uses the config default).
//
// Kept as pure functions so it can be unit tested without a GUI or a real node.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DshLauncher.Tray
{
    internal static class DshWebScope
    {
        private static readonly Regex PortRx =
            new Regex(@"--port(?:\s+|=)(\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The port named on the command line, or 0 when there is none.</summary>
        public static int PortFromCommandLine(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return 0;
            Match m = PortRx.Match(commandLine);
            if (!m.Success) return 0;
            int port;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out port))
            {
                return 0;
            }
            return port;
        }

        /// <summary>
        /// Does this node belong to the dsh web we manage?
        ///   - an explicit --port that differs from ours  -> never ours (hard rule)
        ///   - an explicit --port equal to ours           -> ours
        ///   - no --port                                  -> ours only if it is
        ///     listening on our port
        /// </summary>
        public static bool IsOurDshWeb(string commandLine, int webPort, bool listeningOnOurPort)
        {
            if (webPort <= 0) return true;   // nothing configured: do not narrow blindly
            int declared = PortFromCommandLine(commandLine);
            if (declared > 0) return declared == webPort;
            return listeningOnOurPort;
        }

        /// <summary>
        /// Describe the rejected command line for the tray's status message, so a
        /// "found nothing" result explains itself instead of looking like a bug.
        /// </summary>
        public static List<int> OtherPortPids(List<string> commandLines, List<int> pids, int webPort)
        {
            List<int> others = new List<int>();
            if (commandLines == null || pids == null) return others;
            for (int i = 0; i < commandLines.Count && i < pids.Count; i++)
            {
                int declared = PortFromCommandLine(commandLines[i]);
                if (declared > 0 && declared != webPort && !others.Contains(pids[i])) others.Add(pids[i]);
            }
            return others;
        }
    }
}