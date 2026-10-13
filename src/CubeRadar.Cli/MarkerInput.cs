using CubeRadar.Core.Dump;

namespace CubeRadar.Cli;

/// <summary>Markers from global hotkeys and from the console (Enter = note, 1–9 = hotkey markers, q = stop).</summary>
internal sealed class MarkerInput : IDisposable
{
    private static readonly int[] Tones = [880, 660, 440, 990, 550];

    private readonly DumpSession _session;
    private readonly IReadOnlyList<GlobalHotkeys.Binding> _bindings;
    private readonly bool _beep;
    private readonly GlobalHotkeys? _hotkeys;

    public MarkerInput(DumpSession session, IReadOnlyList<GlobalHotkeys.Binding> bindings, bool beep, Action stop)
    {
        _session = session;
        _bindings = bindings;
        _beep = beep;
        if (bindings.Count > 0) _hotkeys = new GlobalHotkeys(bindings, b => Mark(b.Label, null, "hotkey", Tones[IndexOf(b) % Tones.Length]));

        var console = new Thread(() =>
        {
            while (true)
            {
                var line = Console.ReadLine();
                if (line is null) return; // stdin closed
                line = line.Trim();
                if (line is "q" or "quit" or "/q")
                {
                    stop();
                    return;
                }
                if (line.Length == 1 && line[0] is >= '1' and <= '9' && line[0] - '1' < _bindings.Count)
                {
                    var i = line[0] - '1';
                    Mark(_bindings[i].Label, null, "console", Tones[i % Tones.Length]);
                }
                else Mark("note", line.Length == 0 ? null : line, "console", 770);
            }
        }) { IsBackground = true, Name = "console" };
        console.Start();
    }

    public void PrintHelp()
    {
        if (_hotkeys is not null)
            foreach (var (b, ok, err) in _hotkeys.Results)
                Console.WriteLine($"  hotkey {b.Display,-16} → {b.Label,-18} {(ok ? "ok" : $"NOT registered (error {err}, taken by another program?)")}");
        Console.WriteLine("  console: Enter = marker (text optional), 1-5 = hotkey markers, q = stop");
    }

    public void Dispose() => _hotkeys?.Dispose();

    private void Mark(string label, string? text, string via, int tone)
    {
        var m = new Marker(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), label, text, via);
        _session.AddMarker(m);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MARKER {label}{(string.IsNullOrEmpty(text) ? "" : $" \"{text}\"")} ({via})");
        if (_beep) Task.Run(() => Console.Beep(tone, 150));
    }

    private int IndexOf(GlobalHotkeys.Binding b)
    {
        for (var i = 0; i < _bindings.Count; i++)
            if (ReferenceEquals(_bindings[i], b)) return i;
        return 0;
    }
}
