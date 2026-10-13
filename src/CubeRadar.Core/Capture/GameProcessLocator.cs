// Derived from cyberbadger6969/aion2-dps-meter @ 414de5c (GPL-3.0), src/AionMeter.Core/Capture/GameProcessLocator.cs.
// Changes: namespace only.
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace CubeRadar.Core.Capture;

public readonly record struct GameConnection(IPAddress LocalIp, ushort LocalPort, IPAddress RemoteIp, ushort RemotePort);

/// <summary>Finds the running AION 2 client and the TCP connections it owns (via the Windows TCP table).</summary>
public static class GameProcessLocator
{
    public static readonly string[] ProcessNames = ["AION2", "Aion2", "AION2-Win64-Shipping", "Aion2-Win64-Shipping"];

    public static int[] FindGameProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                ids.Add(p.Id);
                p.Dispose();
            }
        }
        return ids.ToArray();
    }

    /// <summary>Established IPv4 connections of the given processes, excluding plain web traffic (80/443).</summary>
    public static List<GameConnection> FindConnections(IReadOnlyCollection<int> pids)
    {
        var result = new List<GameConnection>();
        if (pids.Count == 0) return result;

        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return result;
            var count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var i = 0; i < count; i++, rowPtr += rowSize)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                if (row.State != MIB_TCP_STATE_ESTAB || !pids.Contains((int)row.OwningPid)) continue;
                var remotePort = Port(row.RemotePort);
                if (remotePort is 80 or 443) continue;
                result.Add(new GameConnection(new IPAddress(row.LocalAddr), Port(row.LocalPort), new IPAddress(row.RemoteAddr), remotePort));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    private static ushort Port(uint networkOrder) => (ushort)((networkOrder & 0xFF) << 8 | (networkOrder >> 8) & 0xFF);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint MIB_TCP_STATE_ESTAB = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}
