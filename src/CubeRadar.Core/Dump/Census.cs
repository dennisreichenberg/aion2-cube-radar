using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using CubeRadar.Core.Capture;
using CubeRadar.Core.Protocol;

namespace CubeRadar.Core.Dump;

/// <summary>Per-opcode counts and stream health of one capture. Not thread-safe.</summary>
public sealed class Census
{
    public sealed class OpStat
    {
        public long Count, Top, Bundle, Embedded, Bytes;
        public int MinLen = int.MaxValue, MaxLen;
        public long FirstMs, LastMs;
    }

    public Dictionary<ushort, OpStat> Ops { get; } = new();
    public long Frames { get; private set; }
    public long FirstMs { get; private set; }
    public long LastMs { get; private set; }
    public long Heartbeats { get; private set; }
    public long FirstHeartbeatMs { get; private set; }
    public long LastHeartbeatMs { get; private set; }
    public long FirstServerMs { get; private set; }
    public long LastServerMs { get; private set; }
    public long BundlesOk { get; private set; }
    public long BundlesBad { get; private set; }
    public long EmbeddedBundles { get; private set; }
    public int MaxDepth { get; private set; }
    public long Markers { get; set; }

    public void AddFrame(long timeMs, in FrameMeta m, ReadOnlySpan<byte> body)
    {
        Frames++;
        if (FirstMs == 0) FirstMs = timeMs;
        LastMs = Math.Max(LastMs, timeMs);
        MaxDepth = Math.Max(MaxDepth, m.Depth);
        if (!Ops.TryGetValue(m.Opcode, out var s)) Ops[m.Opcode] = s = new OpStat { FirstMs = timeMs };
        s.Count++;
        s.LastMs = timeMs;
        s.Bytes += body.Length;
        s.MinLen = Math.Min(s.MinLen, body.Length);
        s.MaxLen = Math.Max(s.MaxLen, body.Length);
        switch (m.Source)
        {
            case FrameSource.Top: s.Top++; break;
            case FrameSource.Bundle: s.Bundle++; break;
            default: s.Embedded++; break;
        }

        if (m.Opcode == FrameDecoder.HeartbeatOpcode)
        {
            Heartbeats++;
            if (FirstHeartbeatMs == 0) FirstHeartbeatMs = timeMs;
            LastHeartbeatMs = timeMs;
            if (body.Length >= 8)
            {
                var server = BinaryPrimitives.ReadInt64LittleEndian(body);
                if (FirstServerMs == 0) FirstServerMs = server;
                LastServerMs = server;
            }
        }
    }

    public void AddBundle(in BundleMeta m)
    {
        if (m.Ok) BundlesOk++;
        else BundlesBad++;
        if (m.Source == FrameSource.Embedded && m.HostSeq != 0) EmbeddedBundles++;
    }

    /// <summary>Heartbeats per second of capture time while heartbeats were seen.</summary>
    public double HeartbeatRate =>
        Heartbeats < 2 || LastHeartbeatMs <= FirstHeartbeatMs ? 0 : (Heartbeats - 1) * 1000.0 / (LastHeartbeatMs - FirstHeartbeatMs);

    /// <summary>Stream health counters summed over all game flows.</summary>
    public static Dictionary<string, long> Health(StreamTracker tracker, CaptureHealth? npcap = null)
    {
        var d = new Dictionary<string, long>
        {
            ["segments"] = tracker.Segments,
            ["game_bytes"] = tracker.GameBytes,
            ["flows"] = tracker.GameFlows.Count,
            ["frames"] = tracker.GameFlows.Sum(f => f.Decoder.Frames),
            ["frames_top"] = tracker.GameFlows.Sum(f => f.Decoder.TopFrames),
            ["frames_lz4"] = tracker.GameFlows.Sum(f => f.Decoder.BundleFrames),
            ["frames_emb"] = tracker.GameFlows.Sum(f => f.Decoder.EmbeddedFrames),
            ["bundles"] = tracker.GameFlows.Sum(f => f.Decoder.Bundles),
            ["bundles_emb"] = tracker.GameFlows.Sum(f => f.Decoder.EmbeddedBundles),
            ["bad_bundles"] = tracker.GameFlows.Sum(f => f.Decoder.BadBundles),
            ["truncated_bundles"] = tracker.GameFlows.Sum(f => f.Decoder.TruncatedBundles),
            ["bundle_size_mismatch"] = tracker.GameFlows.Sum(f => f.Decoder.BundleSizeMismatches),
            ["short_frames"] = tracker.GameFlows.Sum(f => f.Decoder.ShortFrames),
            ["resyncs"] = tracker.GameFlows.Sum(f => f.Decoder.Resyncs),
            ["dropped_bytes"] = tracker.GameFlows.Sum(f => f.Decoder.DroppedBytes),
            ["tcp_gaps"] = tracker.GameFlows.Sum(f => f.Reassembler.Gaps),
            ["tcp_duplicates"] = tracker.GameFlows.Sum(f => f.Reassembler.Duplicates),
            ["tcp_out_of_order"] = tracker.GameFlows.Sum(f => f.Reassembler.OutOfOrder),
        };
        if (npcap is { } n)
        {
            d["npcap_received"] = n.Received;
            d["npcap_dropped"] = n.Dropped;
        }
        return d;
    }

    public string Report(OpcodeBook book, IReadOnlyDictionary<string, long> health, int top = 40)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var seconds = Math.Max(0.001, (LastMs - FirstMs) / 1000.0);
        sb.AppendLine(inv, $"-- census: {Frames:N0} frames over {seconds:N1} s, {Ops.Count} opcodes, max bundle depth {MaxDepth}, markers {Markers}");
        sb.AppendLine("   opcode name              count      /s    top    lz4    emb   min..max len  first..last s");
        foreach (var (op, s) in Ops.OrderByDescending(x => x.Value.Count).Take(top))
        {
            sb.AppendLine(inv,
                $"   {OpcodeBook.Hex(op)}   {Trim(book.Name(op), 16),-16} {s.Count,8:N0} {s.Count / seconds,7:N2} {s.Top,6} {s.Bundle,6} {s.Embedded,6}   {s.MinLen,5}..{s.MaxLen,-6} {(s.FirstMs - FirstMs) / 1000.0,6:N1}..{(s.LastMs - FirstMs) / 1000.0,-6:N1}");
        }
        if (Ops.Count > top) sb.AppendLine(inv, $"   … {Ops.Count - top} more opcodes (see census.json)");

        sb.AppendLine("-- health");
        foreach (var (k, v) in health) sb.AppendLine(inv, $"   {k,-22} {v,12:N0}");
        sb.AppendLine("-- checks");
        foreach (var (ok, text) in Checks(health)) sb.AppendLine($"   [{(ok ? " OK " : "WARN")}] {text}");
        return sb.ToString();
    }

    /// <summary>Phase-0 acceptance: heartbeats ~20/s, spawns present, resyncs / decode errors near zero.</summary>
    public IEnumerable<(bool Ok, string Text)> Checks(IReadOnlyDictionary<string, long> h)
    {
        var inv = CultureInfo.InvariantCulture;
        var rate = HeartbeatRate;
        yield return (rate is >= 15 and <= 25, string.Create(inv, $"heartbeat rate {rate:N2}/s (expected ~20/s)"));
        var serverSpan = (LastServerMs - FirstServerMs) / 1000.0;
        var captureSpan = (LastHeartbeatMs - FirstHeartbeatMs) / 1000.0;
        yield return (captureSpan <= 0 || Math.Abs(serverSpan - captureSpan) <= Math.Max(2, captureSpan * 0.01),
            string.Create(inv, $"server clock span {serverSpan:N1} s vs capture span {captureSpan:N1} s"));
        var spawns = Ops.TryGetValue(0x4136, out var sp) ? sp.Count : 0;
        yield return (spawns > 0, $"spawn frames (41 36): {spawns:N0}");
        var frames = Math.Max(1, h.GetValueOrDefault("frames"));
        var resyncs = h.GetValueOrDefault("resyncs");
        yield return (resyncs <= Math.Max(2, frames / 10_000), $"resyncs {resyncs:N0} for {frames:N0} frames");
        var bad = h.GetValueOrDefault("bad_bundles") + h.GetValueOrDefault("truncated_bundles");
        yield return (bad == 0, $"bad / truncated bundles {bad:N0}");
        var gaps = h.GetValueOrDefault("tcp_gaps");
        yield return (gaps == 0, $"TCP gaps (lost packets) {gaps:N0}");
        if (h.TryGetValue("npcap_dropped", out var dropped)) yield return (dropped == 0, $"Npcap drops {dropped:N0}");
    }

    public Dictionary<string, object> ToJson(OpcodeBook book, IReadOnlyDictionary<string, long> health) => new()
    {
        ["first_ms"] = FirstMs,
        ["last_ms"] = LastMs,
        ["frames"] = Frames,
        ["heartbeats"] = Heartbeats,
        ["heartbeat_rate"] = Math.Round(HeartbeatRate, 3),
        ["server_first_ms"] = FirstServerMs,
        ["server_last_ms"] = LastServerMs,
        ["bundles_ok"] = BundlesOk,
        ["bundles_bad"] = BundlesBad,
        ["bundles_embedded"] = EmbeddedBundles,
        ["max_depth"] = MaxDepth,
        ["markers"] = Markers,
        ["health"] = health,
        ["checks"] = Checks(health).Select(c => new Dictionary<string, object> { ["ok"] = c.Ok, ["text"] = c.Text }).ToList(),
        ["opcodes"] = Ops.OrderByDescending(x => x.Value.Count).ToDictionary(
            x => OpcodeBook.Hex(x.Key),
            x => (object)new Dictionary<string, object>
            {
                ["name"] = book.Name(x.Key),
                ["count"] = x.Value.Count,
                ["top"] = x.Value.Top,
                ["lz4"] = x.Value.Bundle,
                ["emb"] = x.Value.Embedded,
                ["bytes"] = x.Value.Bytes,
                ["min_len"] = x.Value.MinLen,
                ["max_len"] = x.Value.MaxLen,
                ["first_ms"] = x.Value.FirstMs,
                ["last_ms"] = x.Value.LastMs,
            }),
    };

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
