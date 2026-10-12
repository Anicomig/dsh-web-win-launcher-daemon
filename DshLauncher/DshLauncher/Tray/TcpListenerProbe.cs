// Tray\TcpListenerProbe.cs - which process is listening on a given TCP port.
//
// Why not netstat: its column layout is localized (this machine runs a Chinese
// Windows), so parsing it is locale-dependent. GetExtendedTcpTable is not.
// Why not IPGlobalProperties: GetActiveTcpListeners() has no PID.
//
// Verified against the real system: port 3080 -> the daemon's node, 3300 -> a
// manually started one, and the two are told apart correctly.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DshLauncher.Tray
{
    internal static class TcpListenerProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state, localAddr, localPort, remoteAddr, remotePort, pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPTABLE_OWNER_PID
        {
            public uint rows;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public MIB_TCPROW_OWNER_PID[] table;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
            public uint localPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] remoteAddr;
            public uint remotePort;
            public uint state;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCP6TABLE_OWNER_PID
        {
            public uint rows;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public MIB_TCP6ROW_OWNER_PID[] table;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
            bool sort, int ulIPVersion, int tblClass, int reserved);

        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

        /// <summary>
        /// PIDs listening on the given port (IPv4 + IPv6). On any failure an empty
        /// list is returned: callers must treat "unknown" as "no match", never as
        /// "match everything", or a probe failure would turn into an over-kill.
        /// </summary>
        public static List<int> PidsListeningOn(int port)
        {
            List<int> found = new List<int>();
            if (port <= 0 || port > 65535) return found;
            Collect(AF_INET, port, found);
            Collect(AF_INET6, port, found);
            return found;
        }

        private static void Collect(int af, int port, List<int> found)
        {
            int len = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref len, true, af, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (len <= 0) return;

            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                int rc = GetExtendedTcpTable(buf, ref len, true, af, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (rc != 0) return;

                if (af == AF_INET)
                {
                    MIB_TCPTABLE_OWNER_PID t = (MIB_TCPTABLE_OWNER_PID)Marshal.PtrToStructure(
                        buf, typeof(MIB_TCPTABLE_OWNER_PID));
                    int size = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                    for (int i = 0; i < (int)t.rows; i++)
                    {
                        IntPtr row = (IntPtr)(buf.ToInt64() + 4 + (long)i * size);
                        MIB_TCPROW_OWNER_PID r = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(
                            row, typeof(MIB_TCPROW_OWNER_PID));
                        // the field is host-order-in-low-byte per the docs
                        int lp = (int)(((r.localPort & 0xFF) << 8) | ((r.localPort >> 8) & 0xFF));
                        if (lp == port) found.Add((int)r.pid);
                    }
                }
                else
                {
                    MIB_TCP6TABLE_OWNER_PID t = (MIB_TCP6TABLE_OWNER_PID)Marshal.PtrToStructure(
                        buf, typeof(MIB_TCP6TABLE_OWNER_PID));
                    int size = Marshal.SizeOf(typeof(MIB_TCP6ROW_OWNER_PID));
                    for (int i = 0; i < (int)t.rows; i++)
                    {
                        IntPtr row = (IntPtr)(buf.ToInt64() + 4 + (long)i * size);
                        MIB_TCP6ROW_OWNER_PID r = (MIB_TCP6ROW_OWNER_PID)Marshal.PtrToStructure(
                            row, typeof(MIB_TCP6ROW_OWNER_PID));
                        if ((int)r.localPort == port) found.Add((int)r.pid);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }
}