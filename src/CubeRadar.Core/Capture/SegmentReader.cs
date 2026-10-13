// Derived from cyberbadger6969/aion2-dps-meter @ 414de5c (GPL-3.0), src/AionMeter.Core/Capture/LiveCapture.cs
// (class SegmentReader). Changes: none besides namespace.
using PacketDotNet;
using SharpPcap;

namespace CubeRadar.Core.Capture;

/// <summary>Link layer → TCP segment, shared by live capture and capture-file replay.</summary>
public static class SegmentReader
{
    public readonly record struct Segment(long TimeMs, FlowKey Key, uint Seq, bool Syn, bool FinOrRst, byte[] Payload);

    public static bool TryRead(RawCapture raw, out Segment segment)
    {
        segment = default;
        Packet packet;
        try
        {
            packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or NotImplementedException)
        {
            return false;
        }
        var ip = packet.Extract<IPPacket>();
        var tcp = packet.Extract<TcpPacket>();
        if (ip is null || tcp is null) return false;

        var t = raw.Timeval;
        var ms = (long)t.Seconds * 1000 + (long)t.MicroSeconds / 1000;
        var key = new FlowKey(ip.SourceAddress, tcp.SourcePort, ip.DestinationAddress, tcp.DestinationPort);
        segment = new Segment(ms, key, tcp.SequenceNumber, tcp.Synchronize, tcp.Finished || tcp.Reset, tcp.PayloadData ?? []);
        return true;
    }
}
