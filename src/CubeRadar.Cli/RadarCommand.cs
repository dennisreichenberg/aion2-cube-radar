using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using CubeRadar.Core.Capture;
using CubeRadar.Core.Dump;
using CubeRadar.Core.Protocol;
using CubeRadar.Core.Radar;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace CubeRadar.Cli;

/// <summary>
/// <c>cuberadar radar</c>: live capture (or a replay) → <see cref="RadarEngine"/> → local web UI on 127.0.0.1 with a
/// WebSocket push. Strictly passive like everything else here.
/// </summary>
internal static class RadarCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int Run(string[] a, OpcodeBook book, string root)
    {
        string? Opt(string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        var port = Opt("--port") is { } p ? int.Parse(p, CultureInfo.InvariantCulture) : 5313;
        var replay = Opt("--replay");
        var speed = Opt("--speed") is { } sp ? double.Parse(sp, CultureInfo.InvariantCulture) : 1.0;
        var stopAt = Opt("--stop-at") is { } sa ? double.Parse(sa, CultureInfo.InvariantCulture) : double.MaxValue;
        var beep = !a.Contains("--no-beep");
        if (replay is not null && !File.Exists(replay))
        {
            Console.Error.WriteLine($"not found: {replay}");
            return 2;
        }
        if (replay is null && !LiveSniffer.IsNpcapInstalled())
        {
            Console.Error.WriteLine("Npcap is not installed (https://npcap.com).");
            return 2;
        }

        var cfg = RadarConfig.Load(Path.Combine(root, "config"), root);
        var spots = SpotIndex.TryLoad(cfg.SpotsCsv);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        using var store = new SqliteRadarStore(
            replay is null ? Path.Combine(root, "data", "radar.db") : Path.Combine(Path.GetTempPath(), $"cuberadar_replay_{stamp}.db"),
            (replay is null ? "live_" : "replay_") + stamp);
        var engine = new RadarEngine(cfg, store, spots);

        string? dir = null;
        if (replay is null)
        {
            dir = Path.Combine(root, "captures", "radar_" + stamp);
            Directory.CreateDirectory(dir);
        }
        var record = dir is not null && !a.Contains("--no-record");
        using var session = new DumpSession(book, new DumpOptions(), jsonlPath: null,
            pcapngPath: record ? Path.Combine(dir!, "raw.pcapng") : null,
            markerLogPath: dir is null ? null : Path.Combine(dir, "markers.jsonl"));
        session.Tap = (GameFlow f, in FrameMeta m, ReadOnlySpan<byte> body) => engine.OnFrame(f.TimeMs, m.Opcode, body);
        session.FlowLocked += f => Log($"game stream {f.Id}: {f.Key}");

        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };

        LiveSniffer? sniffer = null;
        var replayState = "";
        object CaptureView() => new
        {
            mode = replay is null ? "live" : "replay",
            status = sniffer?.Status ?? replayState,
            device = sniffer?.DeviceDescription,
            packets = session.Read(s => s.Packets),
            streams = session.Read(s => s.Tracker.GameFlows.Count),
            npcapDropped = sniffer?.Health()?.Dropped,
            recording = record ? Path.Combine(dir!, "raw.pcapng") : null,
        };
        object State() => new { radar = engine.Snapshot(), capture = CaptureView(), wallMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

        using var hub = new SnapshotHub(() => JsonSerializer.SerializeToUtf8Bytes(State(), Json));
        engine.CubeAppeared += c =>
        {
            Log($"CUBE {c.ObjectId} template {c.Template} {c.Zone} at ({c.X:0}, {c.Y:0}, {c.Z:0})" +
                (c.FirstDistance is { } d ? $" · {d / Units.PerMetre:0} m away" : "") +
                (c.CreatedMs > 0 ? $" · exists for {Math.Max(0, (c.FirstSeenMs - c.CreatedMs) / 1000)} s" : ""));
            if (beep) Task.Run(() =>
            {
                Console.Beep(1200, 120);
                Console.Beep(1600, 160);
            });
            hub.Kick();
        };
        engine.CubeChanged += c =>
        {
            Log($"cube {c.ObjectId}: {c.Status}{(c.OpenedBy is { } by ? $" by {by}{(c.OpenedBySelf ? " (you)" : "")}" : "")}{(c.Note is null ? "" : $" · {c.Note}")}");
            hub.Kick();
        };
        engine.TemplateLearned += t => Log($"learned cube template {t.Template} ({t.Zone}, via {t.Status})");

        // ---------------------------------------------------------------- web
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.UseWebSockets();
        var files = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.MapGet("/api/state", () => Results.Json(State(), Json));
        app.MapGet("/api/spots", (string? zone) =>
            Results.Json(spots is null || zone is null ? [] : spots.InZone(zone).Select(s => new[] { s.X, s.Y, s.Z }).ToList(), Json));
        app.MapGet("/api/history", (string? zone) => Results.Json(store.History(zone), Json));
        app.MapGet("/api/export/sightings.csv", () => Results.Text(store.Export(json: false), "text/csv"));
        app.MapGet("/api/export/sightings.json", () => Results.Text(store.Export(json: true), "application/json"));
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var id = hub.Add(ws);
            var buffer = new byte[256];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
            finally
            {
                hub.Remove(id);
            }
        });
        app.StartAsync().GetAwaiter().GetResult();
        var url = $"http://127.0.0.1:{port}/";
        Console.WriteLine($"radar: {url}");
        if (dir is not null) Console.WriteLine($"session: {dir}");
        Console.WriteLine($"cube templates: {string.Join(", ", engine.Templates.Select(t => $"{t.Template} {t.Zone} ({t.Status})"))}");
        Console.WriteLine(spots is null ? "spot list: none (optional, see config/radar.json spots_csv)" : $"spot list: {spots.All.Count} spots");
        if (!a.Contains("--no-browser")) TryOpen(url);

        // ---------------------------------------------------------------- capture
        var bindings = replay is not null || a.Contains("--no-hotkeys") ? [] : GlobalHotkeys.Parse(Opt("--hotkeys") ?? GlobalHotkeys.DefaultSpec);
        using var markers = new MarkerInput(session, bindings, beep, stop.Set);
        markers.PrintHelp();

        if (replay is null)
        {
            sniffer = new LiveSniffer(new LiveSnifferOptions { DeviceName = Opt("--device") });
            sniffer.PacketArrived += (raw, dev) => session.OnPacket(raw, dev.Name, LiveSniffer.Describe(dev));
            sniffer.StatusChanged += st => Log(st);
            sniffer.Start();
        }
        else
        {
            Task.Run(() =>
            {
                replayState = "replaying";
                var started = Stopwatch.StartNew();
                long? first = null;
                var n = CaptureFileSource.ReadAll(replay, raw =>
                {
                    var t = (long)raw.Timeval.Seconds * 1000 + (long)raw.Timeval.MicroSeconds / 1000;
                    first ??= t;
                    if (t - first.Value > stopAt * 1000) return; // hold the state at this point of the capture
                    if (speed > 0)
                    {
                        var wait = (t - first.Value) / speed - started.ElapsedMilliseconds;
                        if (wait > 0) Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(wait, 60_000)));
                    }
                    session.OnPacket(raw, "replay");
                });
                replayState = $"replay finished ({n:N0} packets)";
                Log(replayState + " · press q + Enter to quit");
                hub.Kick();
            });
        }

        while (!stop.Wait(15_000))
        {
            var s = engine.Snapshot();
            var visible = s.Cubes.Count(c => c.Status == nameof(CubeStatus.Visible));
            Log($"{(sniffer?.Status ?? replayState)} · frames {s.Frames:N0} · self {s.SelfId?.ToString() ?? "?"}" +
                $"{(s.Self is { } f ? $" (fix {f.AgeMs / 1000} s old)" : "")} · cubes {visible} visible / {s.Cubes.Count} listed" +
                (s.Warnings.Count > 0 ? $" · WARN {string.Join(" | ", s.Warnings)}" : ""));
        }

        sniffer?.Stop();
        sniffer?.Dispose();
        app.StopAsync().GetAwaiter().GetResult();
        session.Finish(dir is null ? null : Path.Combine(dir, "census.json"));
        if (dir is not null) Console.WriteLine($"session: {dir}");
        return 0;
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    private static void TryOpen(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"open {url} in a browser");
        }
    }

    /// <summary>Pushes the radar state to every connected browser: on change, and at least twice a second.</summary>
    private sealed class SnapshotHub : IDisposable
    {
        private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
        private readonly SemaphoreSlim _kick = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public SnapshotHub(Func<byte[]> snapshot) => _loop = Task.Run(() => Loop(snapshot));

        public Guid Add(WebSocket ws)
        {
            var id = Guid.NewGuid();
            _clients[id] = ws;
            Kick();
            return id;
        }

        public void Remove(Guid id) => _clients.TryRemove(id, out _);

        public void Kick()
        {
            if (_kick.CurrentCount == 0) _kick.Release();
        }

        private async Task Loop(Func<byte[]> snapshot)
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.WhenAny(_kick.WaitAsync(_cts.Token), Task.Delay(500, _cts.Token));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (_clients.IsEmpty) continue;
                var bytes = snapshot();
                foreach (var (id, ws) in _clients)
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
                    }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                    {
                        _clients.TryRemove(id, out _);
                    }
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop.Wait(1_000); } catch (AggregateException) { }
            _cts.Dispose();
        }
    }
}
