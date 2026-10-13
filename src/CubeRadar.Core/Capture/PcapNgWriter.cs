using System.Buffers.Binary;
using System.Text;

namespace CubeRadar.Core.Capture;

/// <summary>
/// Minimal pcapng writer (section header, one interface block per capture adapter, enhanced packet blocks with
/// microsecond timestamps). libpcap's own dumper only writes classic pcap. Readable by Wireshark and by
/// <see cref="CaptureFileSource"/>. Not thread-safe: call from one thread or lock around it.
/// </summary>
public sealed class PcapNgWriter : IDisposable
{
    private const uint ShbType = 0x0A0D0D0A, IdbType = 0x00000001, EpbType = 0x00000006;
    private const uint ByteOrderMagic = 0x1A2B3C4D;

    private readonly Stream _out;
    private readonly Dictionary<string, int> _interfaces = new();

    public PcapNgWriter(string path, string application)
    {
        _out = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
        Path = path;
        var options = new List<byte>();
        AddOption(options, 4, Encoding.UTF8.GetBytes(application)); // shb_userappl
        AddEnd(options);
        var body = new byte[16 + options.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(body, ByteOrderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 1); // major
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 0); // minor
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), -1); // section length unknown
        options.CopyTo(body, 16);
        WriteBlock(ShbType, body);
    }

    public string Path { get; }
    public long Packets { get; private set; }

    /// <summary>Interface id for an adapter, writing its description block the first time.</summary>
    public int Interface(string name, int linkType, string? description = null)
    {
        var key = name + "|" + linkType;
        if (_interfaces.TryGetValue(key, out var id)) return id;
        var options = new List<byte>();
        AddOption(options, 2, Encoding.UTF8.GetBytes(name)); // if_name
        if (!string.IsNullOrEmpty(description)) AddOption(options, 3, Encoding.UTF8.GetBytes(description)); // if_description
        AddOption(options, 9, [6]); // if_tsresol: 10^-6 s
        AddEnd(options);
        var body = new byte[8 + options.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)linkType);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 0); // snaplen: unlimited
        options.CopyTo(body, 8);
        WriteBlock(IdbType, body);
        id = _interfaces.Count;
        _interfaces[key] = id;
        return id;
    }

    public void WritePacket(int interfaceId, ulong unixMicroseconds, ReadOnlySpan<byte> data, int originalLength)
    {
        var padded = (data.Length + 3) & ~3;
        var body = new byte[20 + padded];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)interfaceId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)(unixMicroseconds >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)unixMicroseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)Math.Max(originalLength, data.Length));
        data.CopyTo(body.AsSpan(20));
        WriteBlock(EpbType, body);
        Packets++;
    }

    public void Flush() => _out.Flush();

    public void Dispose() => _out.Dispose();

    private void WriteBlock(uint type, byte[] body)
    {
        var total = 12 + body.Length; // type, length, body (already 32-bit aligned), length
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head, type);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)total);
        _out.Write(head);
        _out.Write(body);
        _out.Write(head[4..]);
    }

    private static void AddOption(List<byte> options, ushort code, byte[] value)
    {
        Span<byte> h = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(h, code);
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], (ushort)value.Length);
        options.AddRange(h.ToArray());
        options.AddRange(value);
        for (var i = value.Length; i % 4 != 0; i++) options.Add(0);
    }

    private static void AddEnd(List<byte> options) => options.AddRange(new byte[4]); // opt_endofopt
}
