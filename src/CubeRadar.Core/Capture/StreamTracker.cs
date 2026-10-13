// Derived from cyberbadger6969/aion2-dps-meter @ 414de5c (GPL-3.0), src/AionMeter.Core/Capture/PacketPipeline.cs.
// Changes: no event parser; frames and bundles go to sinks together with their flow and capture time; flows get a
// short numeric id; reassembly counters are exposed per flow.
using System.Net;
using CubeRadar.Core.Protocol;

namespace CubeRadar.Core.Capture;

public readonly record struct FlowKey(IPAddress Src, ushort SrcPort, IPAddress Dst, ushort DstPort)
{
    public override string ToString() => $"{Src}:{SrcPort} → {Dst}:{DstPort}";
}

/// <summary>A server→client stream that carries game heartbeats.</summary>
public sealed class GameFlow
{
    internal GameFlow(int id, FlowKey key, long lockedAtMs, bool aligned)
    {
        Id = id;
        Key = key;
        LockedAtMs = lockedAtMs;
        Aligned = aligned;
    }

    public int Id { get; }
    public FlowKey Key { get; }
    public long LockedAtMs { get; }
    /// <summary>The stream was seen from its SYN, so decoding starts at a frame boundary.</summary>
    public bool Aligned { get; }
    /// <summary>Capture time of the segment being decoded.</summary>
    public long TimeMs { get; internal set; }
    public FrameDecoder Decoder { get; internal set; } = null!;
    public TcpReassembler Reassembler { get; internal set; } = null!;
}

public delegate void FrameSink(GameFlow flow, in FrameMeta meta, ReadOnlySpan<byte> body);

public delegate void BundleSink(GameFlow flow, in BundleMeta meta);

/// <summary>
/// TCP segments in, game frames out. Every server→client flow is watched for the heartbeat frame (<c>0E 00 36</c>).
/// Once a flow shows enough of them it is locked as a game stream and everything buffered so far is replayed through
/// reassembly → framing, so records sent right at connect (our own character) are not lost. TLS flows are ignored.
/// </summary>
public sealed class StreamTracker
{
    public const int GamePort = 13328;
    private const int PreLockBufferBytes = 512 * 1024;
    private const int FlowIdleMs = 120_000;

    private static readonly byte[][] HeartbeatSignatures = [[0x0E, 0x00, 0x36], [0x06, 0x00, 0x36]];

    private readonly Dictionary<FlowKey, Flow> _flows = new();
    private readonly List<GameFlow> _gameFlows = new();
    private long _lastPruneMs;

    public FrameSink? OnFrame { get; set; }
    public BundleSink? OnBundle { get; set; }
    public event Action<GameFlow>? FlowLocked;
    /// <summary>A hole in a game stream was skipped (packets lost before the capture point); args: flow, time ms.</summary>
    public event Action<GameFlow, long>? GapSkipped;

    /// <summary>Heartbeats needed before a flow is trusted (3 on the game port).</summary>
    public int LockThreshold { get; set; } = 6;

    /// <summary>Scan packet bodies for embedded LZ4 bundles.</summary>
    public bool ScanEmbedded { get; set; } = true;

    public long Segments { get; private set; }
    public long GameBytes { get; private set; }
    public long LastGameDataMs { get; private set; }
    public IReadOnlyList<GameFlow> GameFlows => _gameFlows;
    public long Gaps => _gameFlows.Sum(f => f.Reassembler.Gaps);

    public void OnSegment(long timeMs, FlowKey key, uint seq, bool syn, bool finOrRst, ReadOnlySpan<byte> payload)
    {
        Segments++;
        if (timeMs - _lastPruneMs > 10_000) Prune(timeMs);

        if (!_flows.TryGetValue(key, out var flow))
        {
            if (payload.IsEmpty && !syn) return;
            flow = new Flow(key);
            _flows[key] = flow;
        }
        flow.LastSeenMs = timeMs;

        if (syn)
        {
            flow.SynNext = seq + 1;
            return;
        }
        if (finOrRst && payload.IsEmpty)
        {
            if (flow.Game is null) _flows.Remove(key);
            return;
        }
        if (payload.IsEmpty || flow.IsTls) return;

        if (flow.Game is null)
        {
            if (flow.PreLock.Count == 0 && LooksLikeTls(payload))
            {
                flow.IsTls = true;
                return;
            }
            flow.Heartbeats += CountHeartbeats(payload);
            flow.PreLock.Add((seq, payload.ToArray(), timeMs));
            flow.PreLockBytes += payload.Length;
            while (flow.PreLockBytes > PreLockBufferBytes && flow.PreLock.Count > 1)
            {
                flow.PreLockBytes -= flow.PreLock[0].Data.Length;
                flow.PreLock.RemoveAt(0);
                flow.SynNext = null; // the start of the stream is gone
            }

            var threshold = key.SrcPort == GamePort ? Math.Min(3, LockThreshold) : LockThreshold;
            if (flow.Heartbeats < threshold) return;
            Lock(flow, timeMs);
            return;
        }

        Feed(flow.Game, seq, payload, timeMs);
    }

    private void Lock(Flow flow, long timeMs)
    {
        var first = flow.PreLock[0];
        var aligned = flow.SynNext is { } isn && isn == first.Seq;
        var game = new GameFlow(_gameFlows.Count + 1, flow.Key, timeMs, aligned);
        game.Decoder = new FrameDecoder(
            (in FrameMeta m, ReadOnlySpan<byte> body) => OnFrame?.Invoke(game, in m, body),
            (in BundleMeta m) => OnBundle?.Invoke(game, in m),
            aligned) { ScanEmbedded = ScanEmbedded };
        game.Reassembler = new TcpReassembler(d => game.Decoder.Feed(d.Span), () =>
        {
            game.Decoder.Reset();
            GapSkipped?.Invoke(game, game.TimeMs);
        });
        game.Reassembler.Start(first.Seq);
        flow.Game = game;
        _gameFlows.Add(game);
        FlowLocked?.Invoke(game);

        var buffered = flow.PreLock;
        flow.PreLock = new();
        flow.PreLockBytes = 0;
        foreach (var (seq, data, time) in buffered) Feed(game, seq, data, time);
    }

    private void Feed(GameFlow game, uint seq, ReadOnlySpan<byte> payload, long timeMs)
    {
        game.TimeMs = timeMs;
        GameBytes += payload.Length;
        LastGameDataMs = timeMs;
        game.Reassembler.Push(seq, payload, timeMs);
    }

    private static int CountHeartbeats(ReadOnlySpan<byte> payload)
    {
        var count = 0;
        foreach (var sig in HeartbeatSignatures)
        {
            var span = payload;
            int i;
            while ((i = span.IndexOf(sig)) >= 0)
            {
                // A heartbeat frame is exactly 11 bytes; the next byte must start another frame (or end the segment).
                var end = i + 11;
                if (end == span.Length || (end < span.Length && span[end] != 0xFF)) count++;
                span = span[(i + 3)..];
            }
        }
        return count;
    }

    private static bool LooksLikeTls(ReadOnlySpan<byte> p) =>
        p.Length >= 5 && p[0] is >= 0x14 and <= 0x17 && p[1] == 0x03 && p[2] <= 0x04;

    private void Prune(long nowMs)
    {
        _lastPruneMs = nowMs;
        List<FlowKey>? dead = null;
        foreach (var (k, f) in _flows)
            if (nowMs - f.LastSeenMs > FlowIdleMs) (dead ??= new()).Add(k);
        if (dead is not null)
            foreach (var k in dead) _flows.Remove(k);
    }

    private sealed class Flow(FlowKey key)
    {
        public FlowKey Key { get; } = key;
        public GameFlow? Game;
        public bool IsTls;
        public int Heartbeats;
        public uint? SynNext;
        public long LastSeenMs;
        public List<(uint Seq, byte[] Data, long TimeMs)> PreLock = new();
        public int PreLockBytes;
    }
}
