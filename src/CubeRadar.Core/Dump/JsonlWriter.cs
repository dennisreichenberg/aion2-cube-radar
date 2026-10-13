using System.Text.Json;
using CubeRadar.Core.Capture;
using CubeRadar.Core.Protocol;

namespace CubeRadar.Core.Dump;

public sealed record Marker(long TimeMs, string Label, string? Text, string Via);

/// <summary>
/// One JSON object per line. Record types: <c>session</c>, <c>flow</c>, <c>frame</c>, <c>bundle</c>, <c>gap</c>,
/// <c>marker</c>, <c>stats</c>. Times are Unix milliseconds (capture clock for packets, wall clock for markers).
/// Thread-safe.
/// </summary>
public sealed class JsonlWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly Stream _out;
    private readonly Utf8JsonWriter _w;
    private readonly OpcodeBook _book;

    public JsonlWriter(string path, OpcodeBook book)
    {
        Path = path;
        _book = book;
        _out = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _w = new Utf8JsonWriter(_out, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public string Path { get; }
    public bool WriteHeartbeats { get; init; } = true;
    public long Lines { get; private set; }

    public void Session(string mode, string source, string version)
    {
        lock (_gate)
        {
            Begin("session");
            _w.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _w.WriteString("mode", mode);
            _w.WriteString("source", source);
            _w.WriteString("tool", version);
            End();
        }
    }

    public void Flow(GameFlow f)
    {
        lock (_gate)
        {
            Begin("flow");
            _w.WriteNumber("t", f.LockedAtMs);
            _w.WriteNumber("flow", f.Id);
            _w.WriteString("server", $"{f.Key.Src}:{f.Key.SrcPort}");
            _w.WriteString("client", $"{f.Key.Dst}:{f.Key.DstPort}");
            _w.WriteBoolean("aligned", f.Aligned);
            End();
        }
    }

    public void Frame(GameFlow flow, in FrameMeta m, ReadOnlySpan<byte> body)
    {
        if (!WriteHeartbeats && m.Opcode == FrameDecoder.HeartbeatOpcode) return;
        lock (_gate)
        {
            Begin("frame");
            _w.WriteNumber("n", m.Seq);
            _w.WriteNumber("t", flow.TimeMs);
            _w.WriteNumber("flow", flow.Id);
            _w.WriteString("src", Source(m.Source));
            _w.WriteNumber("depth", m.Depth);
            if (m.Path.Length > 0) _w.WriteString("path", m.Path);
            _w.WriteString("op", OpcodeBook.Hex(m.Opcode));
            if (_book.All.TryGetValue(m.Opcode, out var info)) _w.WriteString("name", info.Name);
            _w.WriteNumber("len", body.Length);
            _w.WriteNumber("wire", m.WireSize);
            if (m.Flag >= 0) _w.WriteNumber("flag", m.Flag);
            _w.WriteString("hex", Convert.ToHexStringLower(body));
            End();
        }
    }

    public void Bundle(GameFlow flow, in BundleMeta m)
    {
        lock (_gate)
        {
            Begin("bundle");
            _w.WriteNumber("id", m.Id);
            _w.WriteNumber("t", flow.TimeMs);
            _w.WriteNumber("flow", flow.Id);
            _w.WriteString("src", Source(m.Source));
            _w.WriteNumber("depth", m.Depth);
            if (m.Path.Length > 0) _w.WriteString("path", m.Path);
            if (m.HostSeq != 0) _w.WriteNumber("host", m.HostSeq);
            _w.WriteNumber("raw", m.RawSize);
            _w.WriteNumber("zlen", m.CompressedSize);
            _w.WriteNumber("out", m.DecodedSize);
            _w.WriteBoolean("ok", m.Ok);
            End();
        }
    }

    public void Gap(GameFlow flow, long timeMs)
    {
        lock (_gate)
        {
            Begin("gap");
            _w.WriteNumber("t", timeMs);
            _w.WriteNumber("flow", flow.Id);
            End();
        }
    }

    public void Marker(Marker m)
    {
        lock (_gate)
        {
            Begin("marker");
            WriteMarkerFields(_w, m);
            End();
        }
    }

    public static void WriteMarkerFields(Utf8JsonWriter w, Marker m)
    {
        w.WriteNumber("t", m.TimeMs);
        w.WriteString("local", DateTimeOffset.FromUnixTimeMilliseconds(m.TimeMs).ToLocalTime().ToString("HH:mm:ss.fff"));
        w.WriteString("label", m.Label);
        if (!string.IsNullOrEmpty(m.Text)) w.WriteString("text", m.Text);
        w.WriteString("via", m.Via);
    }

    public void Stats(long timeMs, IReadOnlyDictionary<string, long> values)
    {
        lock (_gate)
        {
            Begin("stats");
            _w.WriteNumber("t", timeMs);
            foreach (var (k, v) in values) _w.WriteNumber(k, v);
            End();
        }
    }

    public void Flush()
    {
        lock (_gate) _out.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _w.Dispose();
            _out.Dispose();
        }
    }

    public static string Source(FrameSource s) => s switch
    {
        FrameSource.Top => "top",
        FrameSource.Bundle => "lz4",
        _ => "emb",
    };

    private void Begin(string type)
    {
        _w.WriteStartObject();
        _w.WriteString("type", type);
    }

    private void End()
    {
        _w.WriteEndObject();
        _w.Flush();
        _out.WriteByte((byte)'\n');
        _w.Reset();
        Lines++;
    }
}
