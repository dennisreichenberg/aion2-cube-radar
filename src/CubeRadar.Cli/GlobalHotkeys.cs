using System.Runtime.InteropServices;

namespace CubeRadar.Cli;

/// <summary>
/// System-wide hotkeys through <c>RegisterHotKey</c> (no keyboard hook, nothing touches the game). They work while the
/// game has focus. A combination another program already owns cannot be registered; that is reported.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    public sealed record Binding(uint Modifiers, uint VirtualKey, string Label, string Display);

    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private const uint WmHotkey = 0x0312, WmQuit = 0x0012;

    private readonly IReadOnlyList<Binding> _bindings;
    private readonly Action<Binding> _onPressed;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;

    public GlobalHotkeys(IReadOnlyList<Binding> bindings, Action<Binding> onPressed)
    {
        _bindings = bindings;
        _onPressed = onPressed;
        _thread = new Thread(Run) { IsBackground = true, Name = "hotkeys" };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Per binding: registered, or the Win32 error.</summary>
    public List<(Binding Binding, bool Ok, int Error)> Results { get; } = new();

    public static readonly string DefaultSpec =
        "ctrl+shift+F5=cube_visible,ctrl+shift+F6=cube_opened,ctrl+shift+F7=cube_out_of_sight,ctrl+shift+F8=cube_back,ctrl+shift+F9=note";

    /// <summary><c>ctrl+shift+F5=cube_visible,alt+F10=note</c>. Keys: F1–F24, A–Z, 0–9, NUM0–NUM9, PAUSE, SCROLL, INSERT, HOME, END, PGUP, PGDN.</summary>
    public static List<Binding> Parse(string spec)
    {
        var list = new List<Binding>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1) throw new FormatException($"hotkey \"{part}\": expected keys=label");
            var keys = part[..eq].Split('+', StringSplitOptions.TrimEntries);
            uint mods = ModNoRepeat;
            uint vk = 0;
            foreach (var k in keys)
            {
                switch (k.ToUpperInvariant())
                {
                    case "CTRL" or "CONTROL": mods |= ModControl; break;
                    case "SHIFT": mods |= ModShift; break;
                    case "ALT": mods |= ModAlt; break;
                    case "WIN": mods |= ModWin; break;
                    default: vk = KeyCode(k) ?? throw new FormatException($"hotkey \"{part}\": unknown key \"{k}\""); break;
                }
            }
            if (vk == 0) throw new FormatException($"hotkey \"{part}\": no key");
            list.Add(new Binding(mods, vk, part[(eq + 1)..], part[..eq]));
        }
        return list;
    }

    private static uint? KeyCode(string k)
    {
        var u = k.ToUpperInvariant();
        if (u.Length >= 2 && u[0] == 'F' && int.TryParse(u[1..], out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        if (u.StartsWith("NUM") && u.Length == 4 && char.IsDigit(u[3])) return (uint)(0x60 + (u[3] - '0'));
        if (u.Length == 1 && (char.IsAsciiLetterUpper(u[0]) || char.IsAsciiDigit(u[0]))) return u[0];
        return u switch
        {
            "PAUSE" => 0x13,
            "SCROLL" => 0x91,
            "INSERT" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PGUP" => 0x21,
            "PGDN" => 0x22,
            _ => null,
        };
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        for (var i = 0; i < _bindings.Count; i++)
        {
            var b = _bindings[i];
            var ok = RegisterHotKey(IntPtr.Zero, i + 1, b.Modifiers, b.VirtualKey);
            Results.Add((b, ok, ok ? 0 : Marshal.GetLastWin32Error()));
        }
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message != WmHotkey) continue;
            var id = (int)msg.wParam;
            if (id >= 1 && id <= _bindings.Count) _onPressed(_bindings[id - 1]);
        }

        for (var i = 0; i < _bindings.Count; i++) UnregisterHotKey(IntPtr.Zero, i + 1);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2_000);
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
