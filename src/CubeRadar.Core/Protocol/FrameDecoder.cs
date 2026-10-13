// Derived from cyberbadger6969/aion2-dps-meter @ 414de5c (GPL-3.0), src/AionMeter.Core/Protocol/FrameDecoder.cs
// and the embedded-bundle scan of src/AionMeter.Core/Protocol/PacketParser.cs.
// Changes: every frame is reported with its origin (top level / LZ4 bundle / bundle embedded in a packet body), its
// nesting path, flag byte and wire size; bundles are reported as records of their own; counters are split by origin.
// Framing, resync and bundle rules are unchanged.
using System.Buffers;
using K4os.Compression.LZ4;

namespace CubeRadar.Core.Protocol;

public enum FrameSource : byte
{
    /// <summary>A frame of the TCP stream itself.</summary>
    Top,
    /// <summary>A frame inside an LZ4 bundle (<c>FF FF</c> payload), at any nesting depth.</summary>
    Bundle,
    /// <summary>A frame inside an LZ4 bundle found in the middle of another packet's body.</summary>
    Embedded,
}

/// <summary>
/// One decoded game packet. <see cref="Seq"/> counts packets of this decoder from 1. <see cref="Path"/> is empty for a
/// top-level frame, otherwise the chain of bundles that carried it: <c>B3.0/B4.2</c> = third frame of bundle 4, which
/// was the first frame of bundle 3. Embedded bundles are written <c>E7.0</c>. <see cref="Flag"/> is the optional
/// 0xF0..0xFE byte after the length varint, or -1. <see cref="WireSize"/> includes the varint.
/// </summary>
public readonly record struct FrameMeta(long Seq, ushort Opcode, FrameSource Source, int Depth, string Path, int Flag, int WireSize);

/// <summary>
/// One LZ4 bundle. <see cref="Path"/> is the path of the frame that carried it ("" = top level); for an embedded bundle
/// <see cref="HostSeq"/> is the packet whose body held it. <see cref="DecodedSize"/> is -1 when decoding failed.
/// </summary>
public readonly record struct BundleMeta(long Id, FrameSource Source, int Depth, string Path, long HostSeq, uint RawSize,
    int CompressedSize, int DecodedSize, bool Ok);

public delegate void FrameHandler(in FrameMeta meta, ReadOnlySpan<byte> body);

public delegate void BundleHandler(in BundleMeta meta);

/// <summary>
/// Splits one server→client byte stream into game packets.
/// <code>
/// frame  = varint len, [0xF0..0xFE], payload        (frame size = len + varintBytes − 4)
/// payload = opcode(2) body | FF FF u32 rawSize lz4-block   (the block holds more frames, possibly nested)
/// </code>
/// 0x00 bytes between frames are padding. After lost data the decoder hunts for the next heartbeat frame
/// (<c>0E 00 36</c>, ~19 per second) instead of guessing at varints.
/// </summary>
public sealed class FrameDecoder
{
    public const int MaxFrame = 65_535;
    public const int MaxUnbundledWait = 16_384;
    public const int MaxBundle = 8 * 1024 * 1024;
    public const int MaxDepth = 4;
    public const ushort HeartbeatOpcode = 0x0036;
    private const int MaxHuntBytes = 256 * 1024;

    private static readonly byte[][] HeartbeatSignatures = [[0x0E, 0x00, 0x36], [0x06, 0x00, 0x36]];

    private readonly FrameHandler _onFrame;
    private readonly BundleHandler? _onBundle;
    private byte[] _buf = new byte[128 * 1024];
    private int _len;
    private bool _hunting;
    private int _huntedBytes;
    private long _seq;
    private long _bundleSeq;

    /// <param name="startsAligned">True when the stream was captured from its first byte (SYN seen). Otherwise the
    /// first segment may start mid-frame, so the decoder begins by hunting for a heartbeat.</param>
    public FrameDecoder(FrameHandler onFrame, BundleHandler? onBundle = null, bool startsAligned = true)
    {
        _onFrame = onFrame;
        _onBundle = onBundle;
        _hunting = !startsAligned;
    }

    /// <summary>Also look for LZ4 bundles inside packet bodies (the own-character record rides that way).</summary>
    public bool ScanEmbedded { get; init; } = true;

    public long Frames => TopFrames + BundleFrames + EmbeddedFrames;
    public long TopFrames { get; private set; }
    public long BundleFrames { get; private set; }
    public long EmbeddedFrames { get; private set; }
    public long Bundles { get; private set; }
    public long EmbeddedBundles { get; private set; }
    public long BadBundles { get; private set; }
    /// <summary>Bundles whose decoded size differs from the announced raw size (still walked).</summary>
    public long BundleSizeMismatches { get; private set; }
    /// <summary>Inner frame lists that ended on an invalid length before the end of the bundle.</summary>
    public long TruncatedBundles { get; private set; }
    public long Resyncs { get; private set; }
    public long DroppedBytes { get; private set; }
    /// <summary>Frames too short to hold an opcode.</summary>
    public long ShortFrames { get; private set; }

    /// <summary>Call when the TCP layer skipped missing bytes: the next byte is not a frame boundary.</summary>
    public void Reset()
    {
        DroppedBytes += _len;
        _len = 0;
        _hunting = true;
        _huntedBytes = 0;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (_len + data.Length > _buf.Length)
        {
            var bigger = new byte[Math.Max(_buf.Length * 2, _len + data.Length)];
            Buffer.BlockCopy(_buf, 0, bigger, 0, _len);
            _buf = bigger;
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
        Drain();
    }

    private void Drain()
    {
        var pos = 0;
        while (pos < _len)
        {
            var span = _buf.AsSpan(pos, _len - pos);

            if (_hunting)
            {
                var hb = FindHeartbeat(span);
                if (hb < 0)
                {
                    var keep = Math.Min(span.Length, 2); // a signature may straddle two segments
                    var skip = span.Length - keep;
                    pos += skip;
                    DroppedBytes += skip;
                    _huntedBytes += skip;
                    if (_huntedBytes > MaxHuntBytes) _hunting = false; // signature changed? fall back to byte resync
                    break;
                }
                pos += hb;
                DroppedBytes += hb;
                _hunting = false;
                continue;
            }

            if (span[0] == 0x00)
            {
                pos++;
                continue;
            }

            if (!Wire.TryVarint(span, 0, out var len, out var vlen))
            {
                if (span.Length < 5) break; // need more bytes
                pos += Resync();
                continue;
            }

            var size = (long)len + vlen - 4;
            if (len < 6 || size > MaxFrame)
            {
                pos += Resync();
                continue;
            }

            if (size > span.Length)
            {
                if (size > MaxUnbundledWait && !IsBundle(span, vlen))
                {
                    pos += Resync();
                    continue;
                }
                break; // wait for the rest of the frame
            }

            HandleFrame(span[..(int)size], vlen, depth: 0, path: "", FrameSource.Top);
            pos += (int)size;
        }

        if (pos > 0)
        {
            _len -= pos;
            if (_len > 0) Buffer.BlockCopy(_buf, pos, _buf, 0, _len);
        }
    }

    /// <summary>Returns how many bytes to skip. Normally switches into heartbeat hunting.</summary>
    private int Resync()
    {
        Resyncs++;
        DroppedBytes++;
        if (_huntedBytes <= MaxHuntBytes)
        {
            _hunting = true;
            _huntedBytes = 0;
        }
        return 1;
    }

    private static int FindHeartbeat(ReadOnlySpan<byte> span)
    {
        var best = -1;
        foreach (var sig in HeartbeatSignatures)
        {
            var i = span.IndexOf(sig);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }

    private static bool IsBundle(ReadOnlySpan<byte> frame, int vlen)
    {
        var o = vlen;
        if (o < frame.Length && frame[o] is >= 0xF0 and <= 0xFE) o++;
        return o + 1 < frame.Length && frame[o] == 0xFF && frame[o + 1] == 0xFF;
    }

    private void HandleFrame(ReadOnlySpan<byte> frame, int vlen, int depth, string path, FrameSource source)
    {
        var o = vlen;
        var flag = -1;
        if (o < frame.Length && frame[o] is >= 0xF0 and <= 0xFE) flag = frame[o++];
        if (o + 2 > frame.Length)
        {
            ShortFrames++;
            return;
        }

        if (frame[o] == 0xFF && frame[o + 1] == 0xFF)
        {
            var id = ++_bundleSeq;
            Bundles++;
            o += 2;
            if (!Wire.TryU32(frame, ref o, out var rawSize) || rawSize == 0 || rawSize > MaxBundle || depth > MaxDepth)
            {
                BadBundles++;
                Report(new BundleMeta(id, source, depth, path, 0, rawSize, frame.Length - o, -1, false));
                return;
            }
            DecodeBundle(frame[o..], rawSize, id, depth, path, hostSeq: 0, source, source == FrameSource.Top ? FrameSource.Bundle : source);
            return;
        }

        var opcode = (ushort)(frame[o] << 8 | frame[o + 1]);
        var body = frame[(o + 2)..];
        var meta = new FrameMeta(++_seq, opcode, source, depth, path, flag, frame.Length);
        switch (source)
        {
            case FrameSource.Top: TopFrames++; break;
            case FrameSource.Bundle: BundleFrames++; break;
            default: EmbeddedFrames++; break;
        }
        _onFrame(in meta, body);

        if (ScanEmbedded && opcode != HeartbeatOpcode && body.Length >= 16 && depth < MaxDepth)
            ScanEmbeddedBundles(body, meta);
    }

    /// <param name="ownSource">Where the bundle itself was found (reported in its <see cref="BundleMeta"/>).</param>
    /// <param name="innerSource">Origin given to the frames inside: Bundle, or Embedded once inside an embedded bundle.</param>
    private bool DecodeBundle(ReadOnlySpan<byte> block, uint rawSize, long id, int depth, string path, long hostSeq,
        FrameSource ownSource, FrameSource innerSource, bool exact = false)
    {
        var target = ArrayPool<byte>.Shared.Rent((int)rawSize);
        try
        {
            int n;
            try
            {
                n = LZ4Codec.Decode(block, target.AsSpan(0, (int)rawSize));
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                n = -1;
            }
            var ok = n > 0 && (!exact || n == rawSize);
            if (!ok && exact) return false; // embedded probe: not a bundle after all, report nothing
            if (exact) _bundleSeq = id;     // the probe's id is taken only now (before nested bundles take theirs)
            if (innerSource == FrameSource.Embedded && hostSeq != 0) EmbeddedBundles++;
            if (!ok) BadBundles++;
            else if (n != rawSize) BundleSizeMismatches++;
            Report(new BundleMeta(id, ownSource, depth, path, hostSeq, rawSize,
                block.Length, ok ? n : -1, ok));
            if (!ok) return false;

            var prefix = (path.Length == 0 ? "" : path + "/") + (hostSeq != 0 ? "E" : "B") + id + ".";
            WalkInner(target.AsSpan(0, n), depth + 1, prefix, innerSource);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(target);
        }
    }

    /// <summary>Frames inside a decompressed bundle. No resync here: an invalid length ends the bundle.</summary>
    private void WalkInner(ReadOnlySpan<byte> data, int depth, string pathPrefix, FrameSource source)
    {
        var pos = 0;
        var index = 0;
        while (pos < data.Length)
        {
            if (data[pos] == 0x00)
            {
                pos++;
                continue;
            }
            var span = data[pos..];
            var size = 0L;
            if (!Wire.TryVarint(span, 0, out var len, out var vlen) || len < 6 || (size = (long)len + vlen - 4) > span.Length)
            {
                TruncatedBundles++;
                return;
            }
            HandleFrame(span[..(int)size], vlen, depth, pathPrefix + index++, source);
            pos += (int)size;
        }
    }

    /// <summary>
    /// Bundles embedded in a packet body: <c>varint len (1–3 bytes), FF FF, u32 rawSize, lz4-block</c>. A candidate
    /// counts only when it decodes to exactly the announced size, so random <c>FF FF</c> pairs are not mistaken for one.
    /// </summary>
    private void ScanEmbeddedBundles(ReadOnlySpan<byte> b, in FrameMeta host)
    {
        for (var i = 1; i + 8 < b.Length; i++)
        {
            if (b[i] != 0xFF || b[i + 1] != 0xFF) continue;
            for (var n = 3; n >= 1; n--)
            {
                var at = i - n;
                if (at < 0 || !Wire.TryVarint(b, at, out var len, out var vlen) || vlen != n) continue;
                var end = at + (long)len + vlen - 4;
                if (len < 12 || end > b.Length) continue;
                var raw = Wire.U32(b, i + 2);
                if (raw is 0 or > MaxBundle) continue;
                if (!DecodeBundle(b[(i + 6)..(int)end], raw, _bundleSeq + 1, host.Depth + 1, host.Path, host.Seq,
                        FrameSource.Embedded, FrameSource.Embedded, exact: true))
                    continue;
                i = (int)end - 1;
                break;
            }
        }
    }

    private void Report(in BundleMeta meta) => _onBundle?.Invoke(in meta);
}
