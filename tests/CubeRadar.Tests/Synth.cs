using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace CubeRadar.Tests;

/// <summary>Builds synthetic wire data following the community framing rule (size on wire = len + varint bytes − 4).</summary>
internal static class Synth
{
    public static byte[] Varint(uint v)
    {
        var o = new List<byte>();
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            o.Add(v != 0 ? (byte)(b | 0x80) : b);
        } while (v != 0);
        return o.ToArray();
    }

    /// <summary>varint len, [flag], opcode (2 bytes as on the wire), body.</summary>
    public static byte[] Frame(ushort opcode, byte[] body, int flag = -1)
    {
        var inner = new List<byte>();
        if (flag >= 0) inner.Add((byte)flag);
        inner.Add((byte)(opcode >> 8));
        inner.Add((byte)opcode);
        inner.AddRange(body);
        return [.. Varint((uint)(inner.Count + 4)), .. inner];
    }

    public static byte[] Heartbeat(long serverMs)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(body, serverMs);
        return Frame(0x0036, body);
    }

    /// <summary>varint len, [flag], FF FF, u32 raw size, LZ4 block of the concatenated inner frames.</summary>
    public static byte[] Bundle(byte[] innerFrames, int flag = -1)
    {
        var lz = new byte[LZ4Codec.MaximumOutputSize(innerFrames.Length)];
        var n = LZ4Codec.Encode(innerFrames, lz);
        var payload = new List<byte>();
        if (flag >= 0) payload.Add((byte)flag);
        payload.AddRange([0xFF, 0xFF]);
        var raw = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, (uint)innerFrames.Length);
        payload.AddRange(raw);
        payload.AddRange(lz.AsSpan(0, n).ToArray());
        return [.. Varint((uint)(payload.Count + 4)), .. payload];
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] Body(int length, byte seed)
    {
        var b = new byte[length];
        for (var i = 0; i < length; i++) b[i] = (byte)(seed + i * 7 + 1 | 1); // never 0x00 / never FF FF pairs by accident
        return b;
    }

    /// <summary>Ethernet + IPv4 + TCP (no options) around a payload; checksums are not checked by the reader.</summary>
    public static byte[] TcpPacket(byte[] srcIp, ushort srcPort, byte[] dstIp, ushort dstPort, uint seq, byte flags, byte[] payload)
    {
        var p = new byte[14 + 20 + 20 + payload.Length];
        p[12] = 0x08; // EtherType IPv4
        var ip = p.AsSpan(14);
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)(20 + 20 + payload.Length));
        ip[8] = 64;
        ip[9] = 6; // TCP
        srcIp.CopyTo(ip[12..]);
        dstIp.CopyTo(ip[16..]);
        var tcp = p.AsSpan(34);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], seq);
        tcp[12] = 0x50; // data offset 5 words
        tcp[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 65535);
        payload.CopyTo(p.AsSpan(54));
        return p;
    }
}
