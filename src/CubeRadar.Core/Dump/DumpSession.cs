using System.Text.Json;
using CubeRadar.Core.Capture;
using CubeRadar.Core.Protocol;
using SharpPcap;

namespace CubeRadar.Core.Dump;

public sealed class DumpOptions
{
    public bool WriteHeartbeats { get; init; } = true;
    public bool ScanEmbedded { get; init; } = true;
}

/// <summary>
/// Raw packets in (live or from a file) → optional pcapng recording, TCP/frame decoding, JSONL frame log, census and
/// markers. All entry points are serialised by one lock, so capture, hotkey and console threads can call in.
/// </summary>
public sealed class DumpSession : IDisposable
{
    private readonly object _gate = new();
    private readonly OpcodeBook _book;
    private readonly Queue<Marker> _replayMarkers = new();
    private readonly StreamWriter? _markerLog;

    public DumpSession(OpcodeBook book, DumpOptions options, string? jsonlPath, string? pcapngPath = null, string? markerLogPath = null)
    {
        _book = book;
        Tracker = new StreamTracker { ScanEmbedded = options.ScanEmbedded };
        if (jsonlPath is not null) Jsonl = new JsonlWriter(jsonlPath, book) { WriteHeartbeats = options.WriteHeartbeats };
        if (pcapngPath is not null) Pcap = new PcapNgWriter(pcapngPath, "aion2-cube-radar " + Version);
        if (markerLogPath is not null) _markerLog = new StreamWriter(markerLogPath, append: true) { AutoFlush = true };

        Tracker.OnFrame = (GameFlow flow, in FrameMeta m, ReadOnlySpan<byte> body) =>
        {
            Census.AddFrame(flow.TimeMs, in m, body);
            Jsonl?.Frame(flow, in m, body);
            Tap?.Invoke(flow, in m, body);
        };
        Tracker.OnBundle = (GameFlow flow, in BundleMeta m) =>
        {
            Census.AddBundle(in m);
            Jsonl?.Bundle(flow, in m);
        };
        Tracker.FlowLocked += f =>
        {
            Jsonl?.Flow(f);
            FlowLocked?.Invoke(f);
        };
        Tracker.GapSkipped += (f, t) => Jsonl?.Gap(f, t);
    }

    public static string Version => typeof(DumpSession).Assembly.GetName().Version?.ToString(3) ?? "0";

    public StreamTracker Tracker { get; }
    public Census Census { get; } = new();
    public JsonlWriter? Jsonl { get; }
    public PcapNgWriter? Pcap { get; }
    public long Packets { get; private set; }
    public long LastPacketMs { get; private set; }

    /// <summary>Every decoded frame, under the session lock (e.g. for the radar engine).</summary>
    public FrameSink? Tap { get; set; }

    /// <summary>Raised under the session lock when a game stream is recognised.</summary>
    public event Action<GameFlow>? FlowLocked;

    public void OnPacket(RawCapture raw, string interfaceName, string? interfaceDescription = null)
    {
        lock (_gate)
        {
            Packets++;
            var t = raw.Timeval;
            if (Pcap is not null)
            {
                var id = Pcap.Interface(interfaceName, (int)raw.LinkLayerType, interfaceDescription);
                Pcap.WritePacket(id, (ulong)t.Seconds * 1_000_000 + (ulong)t.MicroSeconds, raw.Data, raw.PacketLength);
            }
            LastPacketMs = (long)t.Seconds * 1000 + (long)t.MicroSeconds / 1000;
            while (_replayMarkers.Count > 0 && _replayMarkers.Peek().TimeMs <= LastPacketMs) WriteMarker(_replayMarkers.Dequeue());
            if (SegmentReader.TryRead(raw, out var seg))
                Tracker.OnSegment(seg.TimeMs, seg.Key, seg.Seq, seg.Syn, seg.FinOrRst, seg.Payload);
        }
    }

    /// <summary>A marker set now (hotkey / console). Also appended to the marker log so replays can merge it.</summary>
    public void AddMarker(Marker m)
    {
        lock (_gate)
        {
            WriteMarker(m);
            if (_markerLog is not null)
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    w.WriteStartObject();
                    w.WriteString("type", "marker");
                    JsonlWriter.WriteMarkerFields(w, m);
                    w.WriteEndObject();
                }
                _markerLog.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
    }

    /// <summary>Markers recorded with a live session, merged into a replay's frame log by time.</summary>
    public void QueueReplayMarkers(IEnumerable<Marker> markers)
    {
        lock (_gate)
            foreach (var m in markers.OrderBy(m => m.TimeMs)) _replayMarkers.Enqueue(m);
    }

    public static List<Marker> ReadMarkerLog(string path)
    {
        var list = new List<Marker>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!r.TryGetProperty("type", out var type) || type.GetString() != "marker") continue;
            list.Add(new Marker(r.GetProperty("t").GetInt64(), r.GetProperty("label").GetString() ?? "",
                r.TryGetProperty("text", out var text) ? text.GetString() : null,
                r.TryGetProperty("via", out var via) ? via.GetString() ?? "" : ""));
        }
        return list;
    }

    public Dictionary<string, long> Health(CaptureHealth? npcap = null)
    {
        lock (_gate)
        {
            var h = Census.Health(Tracker, npcap);
            h["packets"] = Packets;
            return h;
        }
    }

    /// <summary>Snapshot under the lock, for status lines.</summary>
    public T Read<T>(Func<DumpSession, T> read)
    {
        lock (_gate) return read(this);
    }

    public void WriteStats(CaptureHealth? npcap = null)
    {
        var h = Health(npcap);
        lock (_gate)
        {
            Jsonl?.Stats(LastPacketMs, h);
            Jsonl?.Flush();
            Pcap?.Flush();
        }
    }

    /// <summary>Writes leftover markers and census.json; returns the printable report.</summary>
    public string Finish(string? censusPath, CaptureHealth? npcap = null)
    {
        lock (_gate)
        {
            while (_replayMarkers.Count > 0) WriteMarker(_replayMarkers.Dequeue());
        }
        var health = Health(npcap);
        lock (_gate)
        {
            Jsonl?.Stats(LastPacketMs, health);
            if (censusPath is not null)
            {
                var json = Census.ToJson(_book, health);
                json["flows"] = Tracker.GameFlows.Select(f => $"{f.Id}: {f.Key}").ToList();
                File.WriteAllText(censusPath, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            }
            Jsonl?.Flush();
            Pcap?.Flush();
            return Census.Report(_book, health);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Jsonl?.Dispose();
            Pcap?.Dispose();
            _markerLog?.Dispose();
        }
    }

    private void WriteMarker(Marker m)
    {
        Census.Markers++;
        Jsonl?.Marker(m);
    }
}
