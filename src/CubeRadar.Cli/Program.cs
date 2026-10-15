using System.Globalization;
using CubeRadar.Cli;
using CubeRadar.Core.Capture;
using CubeRadar.Core.Dump;
using CubeRadar.Core.Protocol;

// aion2-cube-radar developer tool. Strictly passive: reads packets through Npcap, never sends anything to the game or
// alters it. The only internet access is the optional update check of the single exe (Updater).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

// A downloaded update, started by the old radar to take its place (see Updater).
if (args is [Updater.InstallFlag, .. var install]) return Updater.RunInstaller(install);

AppPaths.Init();
var book = File.Exists(AppPaths.Config("opcodes.json")) ? OpcodeBook.Load(AppPaths.Config("opcodes.json")) : OpcodeBook.Empty;

// The updater restarts the new exe with the previous arguments plus --after-update.
var afterUpdate = args.Contains(Updater.AfterUpdateFlag);
if (afterUpdate) args = [.. args.Where(x => x != Updater.AfterUpdateFlag)];

// Double-clicked "Cube Radar.exe" (no console, no arguments): just the overlay.
if (args.Length == 0 && !NativeConsole.HasConsole) args = ["radar", "--overlay-only"];

try
{
    return args.FirstOrDefault() switch
    {
        "devices" => Devices(),
        "conns" => Connections(),
        "dump" when args.Length > 1 && args[1] == "live" => DumpLive(args[2..]),
        "dump" when args.Length > 1 => DumpFile(args[1], args[2..], writeJsonl: true),
        "census" when args.Length > 1 => DumpFile(args[1], args[2..], writeJsonl: false),
        "radar" => RadarCommand.Run(args[1..], book, afterUpdate),
        "--version" => Print(DumpSession.Version),
        "--quit" => Quit(),
        _ => Usage(),
    };
}
catch (Exception ex) when (!NativeConsole.HasConsole && ex is not FormatException)
{
    NativeConsole.Notify($"Cube Radar wurde wegen eines Fehlers beendet:\n\n{ex.GetType().Name}: {ex.Message}\n\nDaten und Log: {AppPaths.DataRoot}");
    return 1;
}
catch (FormatException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

int Usage()
{
    Console.WriteLine($"""
        cuberadar {DumpSession.Version} - passive AION 2 frame dumper (Npcap)

        cuberadar devices                          list Npcap adapters
        cuberadar conns                            the AION 2 processes and their TCP connections
        cuberadar dump live [options]              capture live into captures/<date_time>/:
                                                   raw.pcapng, frames.jsonl, markers.jsonl, census.json/.txt
            --out DIR          session root (default: <project>/captures)
            --device NAME      force an Npcap adapter (see 'devices'); default: the one carrying the game
            --seconds N        stop after N seconds (default: until 'q' or Ctrl+C)
            --hotkeys SPEC     marker hotkeys, default:
                               {GlobalHotkeys.DefaultSpec}
            --no-hotkeys       console markers only
            --no-beep          no confirmation beep for markers
            --no-heartbeat     leave heartbeat frames out of frames.jsonl (still counted)
            --no-embedded      do not look for LZ4 bundles inside packet bodies
        cuberadar dump <file.pcap|.pcapng> [--out DIR] [--markers FILE] [--no-heartbeat] [--no-embedded]
                                                   decode a recording into <name>.replay.jsonl + census;
                                                   markers.jsonl next to the file is merged automatically
        cuberadar census <file.pcap|.pcapng>       opcode census and stream health only
        cuberadar radar [options]                  live Hidden Cube radar on http://127.0.0.1:5313
            --port N           web port (default 5313)
            --device NAME      force an Npcap adapter
            --replay FILE      feed a recorded .pcap/.pcapng instead of live capture
            --speed X          replay speed factor (default 1; 0 = as fast as possible)
            --stop-at S        stop feeding the replay S seconds into the capture (inspect that moment)
            --no-record        do not record raw.pcapng into captures/radar_<date_time>/
            --no-hotkeys, --hotkeys SPEC, --no-beep   as for 'dump live' (beep also on new cubes)
            --no-browser       do not open the browser
            --no-overlay       no mini radar overlay (Ctrl+Shift+F10 on/off, F11 settings: drag it while they are
                               open, F12 range)
            --overlay-only     only the overlay: no browser, console hidden (log in the session folder);
                               tray icon to open the web view or quit
        cuberadar --quit                           ask the running radar to close (like "Beenden" in the tray)
        cuberadar --version                        print the version

        Console during 'dump live': Enter = marker (text optional), 1-5 = the hotkey markers, q = stop.
        """);
    return 1;
}

int Print(string s)
{
    Console.WriteLine(s);
    return 0;
}

int Quit()
{
    if (!EventWaitHandle.TryOpenExisting(RadarCommand.QuitSignalName, out var signal))
    {
        Console.WriteLine("no live radar is running");
        return 1;
    }
    using (signal) signal.Set();
    Console.WriteLine("asked the radar to quit");
    return 0;
}

int Devices()
{
    foreach (var (name, desc) in LiveSniffer.ListDevices()) Console.WriteLine($"{desc,-60} {name}");
    return 0;
}

int Connections()
{
    var pids = GameProcessLocator.FindGameProcessIds();
    Console.WriteLine($"AION 2 processes: {(pids.Length == 0 ? "none" : string.Join(", ", pids))}");
    foreach (var c in GameProcessLocator.FindConnections(pids))
        Console.WriteLine($"  {c.LocalIp}:{c.LocalPort} -> {c.RemoteIp}:{c.RemotePort}{(c.RemotePort == StreamTracker.GamePort ? "  (game port)" : "")}");
    return 0;
}

int DumpFile(string path, string[] a, bool writeJsonl)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"not found: {path}");
        return 2;
    }
    var options = ParseDumpOptions(a);
    var dir = Opt(a, "--out") ?? Path.GetDirectoryName(Path.GetFullPath(path))!;
    Directory.CreateDirectory(dir);
    var stem = Path.GetFileNameWithoutExtension(path);
    var jsonl = writeJsonl ? Path.Combine(dir, stem + ".replay.jsonl") : null;
    var census = Path.Combine(dir, stem + ".replay.census.json");

    using var session = new DumpSession(book, options, jsonl);
    session.Jsonl?.Session("file", Path.GetFullPath(path), DumpSession.Version);
    var markerFile = Opt(a, "--markers") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "markers.jsonl");
    if (File.Exists(markerFile))
    {
        var markers = DumpSession.ReadMarkerLog(markerFile);
        session.QueueReplayMarkers(markers);
        Console.WriteLine($"markers: {markers.Count} from {markerFile}");
    }
    session.FlowLocked += f => Console.WriteLine($"game stream {f.Id}: {f.Key}{(f.Aligned ? " (from SYN)" : " (joined mid-stream)")}");

    var packets = CaptureFileSource.ReadAll(path, raw => session.OnPacket(raw, "file"));
    var report = session.Finish(census);
    Console.WriteLine($"{packets:N0} packets read");
    Console.WriteLine(report);
    File.WriteAllText(Path.ChangeExtension(census, ".txt"), report);
    if (jsonl is not null) Console.WriteLine($"frames: {jsonl}");
    Console.WriteLine($"census: {census}");
    return 0;
}

int DumpLive(string[] a)
{
    if (!LiveSniffer.IsNpcapInstalled())
    {
        Console.Error.WriteLine("Npcap is not installed (https://npcap.com).");
        return 2;
    }
    var options = ParseDumpOptions(a);
    var root = Opt(a, "--out") ?? AppPaths.Captures;
    var dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
    Directory.CreateDirectory(dir);
    var seconds = Opt(a, "--seconds") is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : 0;
    var beep = !a.Contains("--no-beep");
    var bindings = a.Contains("--no-hotkeys") ? [] : GlobalHotkeys.Parse(Opt(a, "--hotkeys") ?? GlobalHotkeys.DefaultSpec);

    using var session = new DumpSession(book, options,
        Path.Combine(dir, "frames.jsonl"), Path.Combine(dir, "raw.pcapng"), Path.Combine(dir, "markers.jsonl"));
    session.Jsonl!.Session("live", Opt(a, "--device") ?? "auto", DumpSession.Version);
    session.FlowLocked += f => Log($"game stream {f.Id}: {f.Key}{(f.Aligned ? " (from SYN)" : " (joined mid-stream)")}");

    using var sniffer = new LiveSniffer(new LiveSnifferOptions { DeviceName = Opt(a, "--device") });
    sniffer.PacketArrived += (raw, dev) => session.OnPacket(raw, dev.Name, LiveSniffer.Describe(dev));
    sniffer.StatusChanged += st => Log(st + (sniffer.Filter is { } f ? $" · filter: {f}" : ""));

    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Set();
    };

    using var markers = new MarkerInput(session, bindings, beep, stop.Set);
    Console.WriteLine($"session: {dir}");
    markers.PrintHelp();

    sniffer.Start();
    var started = DateTime.UtcNow;
    var lastStats = DateTime.UtcNow;
    while (!stop.Wait(5_000))
    {
        var line = session.Read(x =>
        {
            var c = x.Census;
            long Count(ushort op) => c.Ops.TryGetValue(op, out var st) ? st.Count : 0;
            var flows = x.Tracker.GameFlows;
            return $"packets {x.Packets:N0} · streams {flows.Count} · frames {c.Frames:N0} · hb {c.HeartbeatRate:N1}/s · " +
                   $"spawn 41 36: {Count(0x4136)} · 34 36: {Count(0x3436)} · bundles {c.BundlesOk:N0} (bad {c.BundlesBad}) · " +
                   $"resyncs {flows.Sum(f => f.Decoder.Resyncs)} · gaps {flows.Sum(f => f.Reassembler.Gaps)} · markers {c.Markers}";
        });
        var health = sniffer.Health();
        Log(line + (health is { } h ? $" · npcap drops {h.Dropped}" : ""));
        if (DateTime.UtcNow - lastStats > TimeSpan.FromSeconds(10))
        {
            session.WriteStats(health);
            lastStats = DateTime.UtcNow;
        }
        if (seconds > 0 && DateTime.UtcNow - started > TimeSpan.FromSeconds(seconds)) break;
    }

    var npcap = sniffer.Health();
    sniffer.Stop();
    var report = session.Finish(Path.Combine(dir, "census.json"), npcap);
    File.WriteAllText(Path.Combine(dir, "census.txt"), report);
    Console.WriteLine(report);
    Console.WriteLine($"session: {dir}");
    return 0;
}

DumpOptions ParseDumpOptions(string[] a) => new()
{
    WriteHeartbeats = !a.Contains("--no-heartbeat"),
    ScanEmbedded = !a.Contains("--no-embedded"),
};

static string? Opt(string[] a, string name) =>
    Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

