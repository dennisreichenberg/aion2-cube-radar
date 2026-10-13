using CubeRadar.Core.Capture;
using CubeRadar.Core.Protocol;
using static CubeRadar.Tests.Synth;

namespace CubeRadar.Tests;

public class FrameDecoderTests
{
    private sealed class Collector
    {
        public readonly List<(FrameMeta Meta, byte[] Body)> Frames = new();
        public readonly List<BundleMeta> Bundles = new();

        public FrameDecoder Decoder(bool aligned = true, bool embedded = true) =>
            new((in FrameMeta m, ReadOnlySpan<byte> b) => Frames.Add((m, b.ToArray())), (in BundleMeta m) => Bundles.Add(m), aligned)
            {
                ScanEmbedded = embedded,
            };
    }

    [Fact]
    public void Heartbeat_matches_the_community_signature()
    {
        var hb = Heartbeat(1_760_000_000_000);
        Assert.Equal(11, hb.Length);
        Assert.Equal(new byte[] { 0x0E, 0x00, 0x36 }, hb[..3]);
    }

    [Fact]
    public void Size_rule_holds_for_one_and_two_byte_varints()
    {
        var c = new Collector();
        var d = c.Decoder();
        var small = Frame(0x4136, Body(20, 1));
        var large = Frame(0x0438, Body(300, 2)); // len > 127 → 2-byte varint
        Assert.True(large[0] >= 0x80);
        d.Feed(Concat(small, large, Heartbeat(5)));

        Assert.Equal(3, c.Frames.Count);
        Assert.Equal((ushort)0x4136, c.Frames[0].Meta.Opcode);
        Assert.Equal(20, c.Frames[0].Body.Length);
        Assert.Equal(small.Length, c.Frames[0].Meta.WireSize);
        Assert.Equal((ushort)0x0438, c.Frames[1].Meta.Opcode);
        Assert.Equal(300, c.Frames[1].Body.Length);
        Assert.Equal(large.Length, c.Frames[1].Meta.WireSize);
        Assert.Equal(0, d.Resyncs);
    }

    [Fact]
    public void Flag_byte_is_reported_and_skipped()
    {
        var c = new Collector();
        c.Decoder().Feed(Frame(0x3336, Body(10, 3), flag: 0xF4));
        var (meta, body) = Assert.Single(c.Frames);
        Assert.Equal(0xF4, meta.Flag);
        Assert.Equal((ushort)0x3336, meta.Opcode);
        Assert.Equal(Body(10, 3), body);
    }

    [Fact]
    public void Padding_and_split_segments()
    {
        var c = new Collector();
        var d = c.Decoder();
        var data = Concat([0, 0], Frame(0x4136, Body(40, 4)), [0], Heartbeat(7));
        foreach (var chunk in data.Chunk(5)) d.Feed(chunk);
        Assert.Equal(new ushort[] { 0x4136, 0x0036 }, c.Frames.Select(f => f.Meta.Opcode));
        Assert.Equal(0, d.Resyncs);
    }

    [Fact]
    public void Nested_bundles_report_origin_and_path()
    {
        var c = new Collector();
        var d = c.Decoder();
        var innermost = Concat(Frame(0x1A37, Body(14, 5)), Frame(0x4236, Body(6, 6)));
        var inner = Concat(Frame(0x4136, Body(30, 7)), Bundle(innermost), Frame(0x0238, Body(12, 8)));
        d.Feed(Concat(Heartbeat(1), Bundle(inner, flag: 0xF1), Heartbeat(2)));

        var got = c.Frames.Select(f => (OpcodeBook.Hex(f.Meta.Opcode), f.Meta.Source, f.Meta.Depth, f.Meta.Path)).ToList();
        Assert.Equal(
        [
            ("0036", FrameSource.Top, 0, ""),
            ("4136", FrameSource.Bundle, 1, "B1.0"),
            ("1a37", FrameSource.Bundle, 2, "B1.1/B2.0"),
            ("4236", FrameSource.Bundle, 2, "B1.1/B2.1"),
            ("0238", FrameSource.Bundle, 1, "B1.2"),
            ("0036", FrameSource.Top, 0, ""),
        ], got);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, c.Frames.Select(f => f.Meta.Seq));
        Assert.Equal(2, c.Bundles.Count);
        Assert.Equal((1L, "", true), (c.Bundles[0].Id, c.Bundles[0].Path, c.Bundles[0].Ok));
        Assert.Equal((2L, "B1.1", true), (c.Bundles[1].Id, c.Bundles[1].Path, c.Bundles[1].Ok));
        Assert.Equal(2, d.Bundles);
        Assert.Equal(0, d.BadBundles);
        Assert.Equal(4, d.BundleFrames);
        Assert.Equal(2, d.TopFrames);
    }

    [Fact]
    public void Corrupt_bundle_is_counted_not_walked()
    {
        var c = new Collector();
        var d = c.Decoder();
        var bundle = Bundle(Frame(0x4136, Body(30, 9)));
        bundle[^3] ^= 0xFF; // damage the compressed block
        bundle[3] = 0xFF;   // and announce a raw size the block cannot produce
        d.Feed(Concat(bundle, Heartbeat(3)));
        Assert.True(d.BadBundles + d.BundleSizeMismatches + d.TruncatedBundles >= 1);
        Assert.Equal(0, d.Resyncs); // the top-level stream is unaffected
        Assert.Equal((ushort)0x0036, c.Frames[^1].Meta.Opcode);
    }

    [Fact]
    public void Garbage_triggers_resync_on_the_next_heartbeat()
    {
        var c = new Collector();
        var d = c.Decoder();
        // 0x03 = len 3 < 6 → invalid; the decoder hunts for 0E 00 36 and resumes there.
        d.Feed(Concat(Heartbeat(1), [0x03, 0x99, 0x98, 0x97], Frame(0x4136, Body(9, 10)), Heartbeat(2), Frame(0x4236, Body(5, 11))));
        Assert.Equal(1, d.Resyncs);
        Assert.Equal(new ushort[] { 0x0036, 0x0036, 0x4236 }, c.Frames.Select(f => f.Meta.Opcode));
    }

    [Fact]
    public void Unaligned_start_hunts_for_a_heartbeat_first()
    {
        var c = new Collector();
        var d = c.Decoder(aligned: false);
        var tail = Frame(0x4136, Body(25, 12))[7..]; // the end of a frame we did not see
        d.Feed(Concat(tail, Heartbeat(9), Frame(0x4136, Body(25, 13))));
        Assert.Equal(new ushort[] { 0x0036, 0x4136 }, c.Frames.Select(f => f.Meta.Opcode));
        Assert.Equal(tail.Length, d.DroppedBytes);
        Assert.Equal(0, d.Resyncs);
    }

    [Fact]
    public void Reset_after_gap_drops_partial_frame_and_hunts()
    {
        var c = new Collector();
        var d = c.Decoder();
        var big = Frame(0x4536, Body(60, 14));
        d.Feed(big[..30]);
        d.Reset();
        d.Feed(Concat(big[45..], Heartbeat(4), Frame(0x4236, Body(5, 15))));
        Assert.Equal(new ushort[] { 0x0036, 0x4236 }, c.Frames.Select(f => f.Meta.Opcode));
    }

    [Fact]
    public void Bundle_embedded_in_a_body_is_found()
    {
        var c = new Collector();
        var d = c.Decoder();
        var hidden = Bundle(Concat(Frame(0x3336, Body(20, 16)), Frame(0x1A37, Body(14, 17))));
        var host = Frame(0x9999, Concat(Body(10, 18), hidden, Body(6, 19)));
        d.Feed(host);

        Assert.Equal(new ushort[] { 0x9999, 0x3336, 0x1A37 }, c.Frames.Select(f => f.Meta.Opcode));
        Assert.Equal(FrameSource.Embedded, c.Frames[1].Meta.Source);
        Assert.Equal("E1.0", c.Frames[1].Meta.Path);
        Assert.Equal("E1.1", c.Frames[2].Meta.Path);
        var b = Assert.Single(c.Bundles);
        Assert.Equal(1L, b.HostSeq);
        Assert.Equal(FrameSource.Embedded, b.Source);
        Assert.Equal(1, d.EmbeddedBundles);

        var off = new Collector();
        off.Decoder(embedded: false).Feed(host);
        Assert.Single(off.Frames);
    }
}

public class TcpReassemblerTests
{
    [Fact]
    public void Out_of_order_and_duplicates_are_delivered_once_in_order()
    {
        var got = new List<byte>();
        var gaps = 0;
        var r = new TcpReassembler(d => got.AddRange(d.ToArray()), () => gaps++);
        r.Start(1000);
        r.Push(1004, [5, 6, 7, 8], 0);
        r.Push(1000, [1, 2, 3, 4], 1);
        r.Push(1002, [3, 4, 5], 2); // overlap / retransmit
        r.Push(1008, [9], 3);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, got);
        Assert.Equal(0, gaps);
    }

    [Fact]
    public void A_hole_is_skipped_after_the_timeout()
    {
        var got = new List<byte>();
        var gaps = 0;
        var r = new TcpReassembler(d => got.AddRange(d.ToArray()), () => gaps++);
        r.Start(0);
        r.Push(0, [1], 0);
        r.Push(10, [2], 100);
        Assert.Equal(new byte[] { 1 }, got);
        r.Push(11, [3], 100 + TcpReassembler.GapTimeoutMs + 1);
        Assert.Equal(new byte[] { 1, 2, 3 }, got);
        Assert.Equal(1, gaps);
    }
}
