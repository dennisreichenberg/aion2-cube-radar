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

    /// <summary>Named event the live radar waits on; setting it closes the radar (see Program, "--quit").</summary>
    public const string QuitSignalName = @"Local\CubeRadar.Quit";

    /// <summary>Held by the live radar while it runs: one at a time, and the update installer waits for it.</summary>
    public const string InstanceLockName = @"Local\CubeRadar.LiveRadar";

    /// <param name="afterUpdate">Started by the updater: the old radar is still shutting down, wait for it.</param>
    public static int Run(string[] a, OpcodeBook book, bool afterUpdate = false)
    {
        string? Opt(string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        var port = Opt("--port") is { } p ? int.Parse(p, CultureInfo.InvariantCulture) : 5313;
        var replay = Opt("--replay");
        var speed = Opt("--speed") is { } sp ? double.Parse(sp, CultureInfo.InvariantCulture) : 1.0;
        var stopAt = Opt("--stop-at") is { } sa ? double.Parse(sa, CultureInfo.InvariantCulture) : double.MaxValue;
        var beep = !a.Contains("--no-beep");
        var overlayOnly = a.Contains("--overlay-only");
        if (overlayOnly && a.Contains("--no-overlay"))
        {
            Console.Error.WriteLine("--overlay-only and --no-overlay exclude each other.");
            return 2;
        }
        if (replay is not null && !File.Exists(replay))
        {
            Console.Error.WriteLine($"not found: {replay}");
            return 2;
        }
        void Tell(string message)
        {
            Console.Error.WriteLine(message);
            if (overlayOnly || !NativeConsole.HasConsole)
                System.Windows.Forms.MessageBox.Show(message, "Cube Radar", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
        }
        if (replay is null && !LiveSniffer.IsNpcapInstalled())
        {
            Tell("Npcap ist nicht installiert.\n\nBitte von https://npcap.com herunterladen, mit den Standardoptionen installieren " +
                 "und Cube Radar danach neu starten.");
            return 2;
        }

        // One live radar at a time: a second one would fight over hotkeys, the web port and the capture.
        // A radar that is just closing gets a moment (a quick restart); after an update the installer already waited.
        using var instance = new Mutex(initiallyOwned: true, InstanceLockName, out var firstInstance);
        if (replay is null && !firstInstance) firstInstance = WaitFor(instance, TimeSpan.FromSeconds(afterUpdate ? 60 : 3));
        if (replay is null && !firstInstance)
        {
            Tell("Cube Radar läuft bereits.\n\nBeenden: Rechtsklick auf das Radar-Symbol unten rechts in der Taskleiste → „Beenden“ " +
                 "(oder ⚙ im Overlay → „Radar beenden“).");
            return 3;
        }

        var cfg = RadarConfig.Load(AppPaths.Config("radar.json"), AppPaths.Config("cubes.json"), AppPaths.DataRoot);
        var spots = SpotIndex.TryLoad(cfg.SpotsCsv);
        var settings = RadarSettings.Load(Path.Combine(AppPaths.DataDir, "settings.json"));
        var overlayPng = Opt("--overlay-png");
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        using var store = new SqliteRadarStore(
            replay is null ? Path.Combine(AppPaths.DataDir, "radar.db") : Path.Combine(Path.GetTempPath(), $"cuberadar_replay_{stamp}.db"),
            (replay is null ? "live_" : "replay_") + stamp);
        var engine = new RadarEngine(cfg, store, spots);
        if (settings.North is { } savedNorth)
        {
            try { engine.SetNorth(savedNorth); }
            catch (FormatException) { settings.North = null; }
        }

        string? dir = null;
        if (replay is null)
        {
            dir = Path.Combine(AppPaths.Captures, "radar_" + stamp);
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
        // "cuberadar --quit" asks the live radar to close, like "Beenden" in the tray.
        using var quitSignal = replay is null ? new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName) : null;
        if (quitSignal is not null)
            ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => stop.Set(), null, Timeout.Infinite, executeOnlyOnce: true);

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
        // Cancelled when the radar closes: an open web view would otherwise hold the web server for its full 30 s
        // shutdown timeout (the browser never closes its WebSocket by itself).
        using var closing = new CancellationTokenSource();

        void PlayCubeSound()
        {
            if (CubeSound.Play(settings.SoundFile, settings.SoundVolumePct) is { } note) Log(note);
        }
        if (beep) CubeSound.Prepare(); // listed in the Windows volume mixer before the first cube

        void SetNorth(string north)
        {
            engine.SetNorth(north); // throws FormatException for anything but -y, +y, +x, -x
            settings.North = engine.North;
            settings.Save();
            Log($"north = {engine.North}");
            hub.Kick();
        }
        engine.CubeAppeared += c =>
        {
            Log($"CUBE {c.ObjectId} template {c.Template} {c.Zone} at ({c.X:0}, {c.Y:0}, {c.Z:0})" +
                (c.FirstDistance is { } d ? $" · {d / Units.PerMetre:0} m away" : "") +
                (c.CreatedMs > 0 ? $" · exists for {Math.Max(0, (c.FirstSeenMs - c.CreatedMs) / 1000)} s" : ""));
            if (beep && settings.SoundEnabled) PlayCubeSound();
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
        var files = new PhysicalFileProvider(AppPaths.WebRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.MapGet("/api/state", () => Results.Json(State(), Json));
        app.MapGet("/api/spots", (string? zone) =>
            Results.Json(spots is null || zone is null ? [] : spots.InZone(zone).Select(s => new[] { s.X, s.Y, s.Z }).ToList(), Json));
        app.MapGet("/api/history", (string? zone) => Results.Json(store.History(zone), Json));
        app.MapPost("/api/north", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(ctx.Request.Body);
            try
            {
                SetNorth(body?.GetValueOrDefault("north") ?? "");
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            return Results.Ok();
        });
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
            using var closingOrAborted = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, closing.Token);
            var id = hub.Add(ws);
            var buffer = new byte[256];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buffer, closingOrAborted.Token);
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
        if (overlayOnly) HideConsole(dir is null ? Path.Combine(Path.GetTempPath(), $"cuberadar_{stamp}.log") : Path.Combine(dir, "radar.log"));
        Console.WriteLine($"radar: {url}");
        if (dir is not null) Console.WriteLine($"session: {dir}");
        Console.WriteLine($"cube templates: {string.Join(", ", engine.Templates.Select(t => $"{t.Template} {t.Zone} ({t.Status})"))}");
        Console.WriteLine(spots is null ? "spot list: none (optional, see config/radar.json spots_csv)" : $"spot list: {spots.All.Count} spots");
        if (!a.Contains("--no-browser") && !overlayOnly) TryOpen(url);

        // ---------------------------------------------------------------- capture
        var bindings = replay is not null || a.Contains("--no-hotkeys") ? [] : GlobalHotkeys.Parse(Opt("--hotkeys") ?? GlobalHotkeys.DefaultSpec);
        using var markers = new MarkerInput(session, bindings, beep, stop.Set);
        markers.PrintHelp();

        // Only the single "Cube Radar.exe" updates itself; a build in the repository is updated with git.
        using var updater = replay is null && AppPaths.SingleExe && Environment.ProcessPath is { } exe
            ? new Updater(settings, exe, stop.Set)
            : null;

        OverlayWindow? overlay = null;
        GlobalHotkeys? overlayKeys = null;
        if (!a.Contains("--no-overlay"))
        {
            var actions = new OverlayActions(() => TryOpen(url), stop.Set, PlayCubeSound, () => engine.North, SetNorth, () =>
            {
                Directory.CreateDirectory(AppPaths.Captures);
                TryOpen(AppPaths.Captures);
            }, updater);
            overlay = OverlayWindow.Start(engine.Snapshot, settings, actions);
            overlay.ShowTray();
            if (Opt("--settings-png") is { } settingsPng) SettingsForm.SavePng(settingsPng, settings, actions);
            overlayKeys = new GlobalHotkeys(
                GlobalHotkeys.Parse("ctrl+shift+F10=overlay on/off,ctrl+shift+F11=overlay settings,ctrl+shift+F12=overlay range"),
                b =>
                {
                    if (b.Label.EndsWith("on/off")) overlay.ToggleVisible();
                    else if (b.Label.EndsWith("settings")) overlay.ToggleSettings();
                    else overlay.CycleRange();
                });
            foreach (var (b, ok, err) in overlayKeys.Results)
                Console.WriteLine($"  hotkey {b.Display,-16} → {b.Label,-18} {(ok ? "ok" : $"NOT registered (error {err})")}");
        }
        if (updater is not null)
        {
            Console.WriteLine($"version {Updater.Current.ToString(3)} · update check {(settings.UpdateCheckEnabled ? "on" : "off")}" +
                              (afterUpdate ? " · started by the updater" : ""));
            updater.Start();
        }

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
                if (overlayPng is not null)
                {
                    OverlayRenderer.SavePng(overlayPng, engine.Snapshot(), settings.OverlayRangeM);
                    Log($"overlay frame: {overlayPng}");
                }
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

        var shutdown = Stopwatch.StartNew();
        var steps = new List<string>();
        void Step(string name, Action action)
        {
            var t = shutdown.ElapsedMilliseconds;
            action();
            steps.Add($"{name} {shutdown.ElapsedMilliseconds - t} ms");
        }
        Step("overlay", () =>
        {
            overlayKeys?.Dispose();
            overlay?.Shutdown();
            CubeSound.Close();
        });
        Step("capture", () =>
        {
            sniffer?.Stop();
            sniffer?.Dispose();
        });
        Step("web", () =>
        {
            closing.Cancel();
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2)); // never the default 30 s
            try
            {
                app.StopAsync(grace.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // the grace period is over; the process ends anyway
            }
        });
        Step("session", () => session.Finish(dir is null ? null : Path.Combine(dir, "census.json")));
        Log($"shutdown in {shutdown.ElapsedMilliseconds} ms ({string.Join(", ", steps)})");
        if (dir is not null) Console.WriteLine($"session: {dir}");
        return 0;
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>Waits until the other radar releases the single-instance lock (or ends without releasing it).</summary>
    private static bool WaitFor(Mutex instance, TimeSpan timeout)
    {
        try
        {
            return instance.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // the old process ended while holding it; the lock is ours now
        }
    }

    /// <summary>Overlay-only mode: no console window; everything that would be printed goes to a log file.</summary>
    private static void HideConsole(string logPath)
    {
        var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Console.SetOut(log);
        Console.SetError(log);
        var console = GetConsoleWindow();
        if (console != IntPtr.Zero) ShowWindow(console, 0); // SW_HIDE
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

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
