using SharpPcap;
using SharpPcap.LibPcap;

namespace CubeRadar.Core.Capture;

/// <summary>Reads a .pcap or .pcapng file (through Npcap's libpcap) and hands out its packets in order.</summary>
public static class CaptureFileSource
{
    public static long ReadAll(string path, Action<RawCapture> onPacket, CancellationToken ct = default)
    {
        long count = 0;
        using var reader = new CaptureFileReaderDevice(path);
        reader.Open();
        while (!ct.IsCancellationRequested && reader.GetNextPacket(out var e) == GetPacketStatus.PacketRead)
        {
            count++;
            onPacket(e.GetPacket());
        }
        return count;
    }
}
