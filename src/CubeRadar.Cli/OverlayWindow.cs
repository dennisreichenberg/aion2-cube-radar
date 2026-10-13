using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CubeRadar.Core.Radar;

namespace CubeRadar.Cli;

/// <summary>
/// Small always-on-top, click-through window over the game: the nearest live cubes with direction, distance and
/// height. It never takes focus. Ctrl+Shift+F10 shows/hides it, Ctrl+Shift+F11 unlocks it for dragging (position is
/// saved when locking again). Works over windowed / borderless games; nothing can draw over exclusive fullscreen.
/// </summary>
internal sealed class OverlayWindow : Form
{
    private const int WsExLayered = 0x80000, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExTopmost = 0x8;
    private const int GwlExStyle = -20;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Func<RadarSnapshot> _snapshot;
    private readonly RadarSettings _settings;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private RadarSnapshot? _last;
    private bool _unlocked;
    private int _ticks;

    public OverlayWindow(Func<RadarSnapshot> snapshot, RadarSettings settings)
    {
        _snapshot = snapshot;
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Background;
        Opacity = 0.9;
        DoubleBuffered = true;
        Text = "Cube Radar Overlay";
        var scale = DeviceDpi / 96f;
        Size = OverlayRenderer.Measure(null, scale);
        var screen = Screen.PrimaryScreen!.WorkingArea;
        var x = settings.OverlayX ?? screen.Right - Width - (int)(24 * scale);
        var y = settings.OverlayY ?? screen.Top + (int)(300 * scale);
        Location = OnSomeScreen(new Point(x, y)) ? new Point(x, y) : new Point(screen.Right - Width - 24, screen.Top + 300);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        MouseDown += (_, e) =>
        {
            if (!_unlocked || e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0); // WM_NCLBUTTONDOWN on the caption: Windows does the dragging
        };
    }

    /// <summary>Runs the overlay on its own UI thread; returns once the window exists.</summary>
    public static OverlayWindow Start(Func<RadarSnapshot> snapshot, RadarSettings settings)
    {
        OverlayWindow? window = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            window = new OverlayWindow(snapshot, settings);
            _ = window.Handle; // the handle must exist for calls from other threads, also while hidden
            if (settings.OverlayVisible) window.Show();
            ready.Set();
            Application.Run(new ApplicationContext());
        }) { IsBackground = true, Name = "overlay" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return window!;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopmost;
            return cp;
        }
    }

    public void ToggleVisible() => Ui(() =>
    {
        Visible = !Visible;
        _settings.OverlayVisible = Visible;
        _settings.Save();
    });

    public void ToggleLock() => Ui(() =>
    {
        _unlocked = !_unlocked;
        var style = GetWindowLong(Handle, GwlExStyle);
        style = _unlocked ? style & ~WsExTransparent : style | WsExTransparent;
        SetWindowLong(Handle, GwlExStyle, style);
        if (!_unlocked)
        {
            _settings.OverlayX = Left;
            _settings.OverlayY = Top;
            _settings.Save();
        }
        if (_unlocked && !Visible) Visible = true;
        Invalidate();
    });

    public void Shutdown() => Ui(() =>
    {
        Close();
        Application.ExitThread();
    });

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private void Tick()
    {
        _last = _snapshot();
        var size = OverlayRenderer.Measure(_last, DeviceDpi / 96f);
        if (size != Size) Size = size;
        // Some games push themselves to the top when they regain focus: re-assert every 2 s.
        if (++_ticks % 8 == 0 && Visible) SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) =>
        OverlayRenderer.Render(e.Graphics, ClientSize, _last, DeviceDpi / 96f, _unlocked);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private static bool OnSomeScreen(Point p) => Screen.AllScreens.Any(s => s.WorkingArea.Contains(p));

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}

internal static class Palette
{
    public static readonly Color Background = Color.FromArgb(16, 20, 26);
    public static readonly Color Text = Color.FromArgb(230, 235, 240);
    public static readonly Color Muted = Color.FromArgb(139, 150, 163);
    public static readonly Color Line = Color.FromArgb(52, 62, 74);
    public static readonly Color Visible = Color.FromArgb(61, 220, 132);
    public static readonly Color OutOfView = Color.FromArgb(242, 179, 61);
    public static readonly Color Opening = Color.FromArgb(255, 225, 77);
    public static readonly Color Me = Color.FromArgb(77, 163, 255);
    public static readonly Color Up = Color.FromArgb(124, 196, 255);
    public static readonly Color Down = Color.FromArgb(255, 159, 107);
    public static readonly Color Warn = Color.FromArgb(255, 107, 91);
}

/// <summary>Draws the overlay content; also used to render a PNG for checking the layout.</summary>
internal static class OverlayRenderer
{
    private const int MaxRows = 3, Width = 300, Header = 26, Row = 34, Pad = 8;
    private const double LevelMetres = 3;

    public static List<CubeView> Rows(RadarSnapshot? s) =>
        s is null
            ? []
            : s.Cubes.Where(c => c.Status is nameof(CubeStatus.Visible) or nameof(CubeStatus.Opening) or nameof(CubeStatus.OutOfView))
                .OrderBy(c => c.Status == nameof(CubeStatus.OutOfView) ? 1 : 0)
                .ThenBy(c => c.DistanceM ?? double.MaxValue)
                .Take(MaxRows)
                .ToList();

    public static Size Measure(RadarSnapshot? s, float scale)
    {
        var rows = Math.Max(1, Rows(s).Count);
        return new Size((int)(Width * scale), (int)((Header + rows * Row + Pad) * scale));
    }

    public static void Render(Graphics g, Size size, RadarSnapshot? s, float scale, bool unlocked)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Palette.Background);
        g.ScaleTransform(scale, scale);
        var w = size.Width / scale;
        var h = size.Height / scale;

        using var small = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var label = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var big = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Point);

        using (var border = new Pen(unlocked ? Palette.Warn : Palette.Line, unlocked ? 2 : 1) { DashStyle = unlocked ? DashStyle.Dash : DashStyle.Solid })
            g.DrawRectangle(border, 0.5f, 0.5f, w - 1, h - 1);

        Text(g, "CUBE RADAR", small, Palette.Muted, Pad, 5);
        if (unlocked)
        {
            Text(g, "ziehen · Strg+Umschalt+F11 = fest", small, Palette.Warn, w - Pad, 5, right: true);
        }
        else if (s is not null)
        {
            var (posText, posColor) = s switch
            {
                { Ring: { } r } when s.Self is null || r.TimeMs > s.Self.TimeMs => ($"Pos. ± {r.Uncertainty / 100:0} m", Palette.OutOfView),
                { Self: { } f } => ($"Pos. vor {f.AgeMs / 1000} s", f.AgeMs <= s.FixFreshSeconds * 1000 ? Palette.Visible : f.AgeMs <= 30_000 ? Palette.OutOfView : Palette.Warn),
                _ => ("Pos. unbekannt", Palette.Warn),
            };
            var tw = g.MeasureString(posText, small).Width;
            Text(g, posText, small, Palette.Muted, w - Pad, 5, right: true);
            using var dot = new SolidBrush(posColor);
            g.FillEllipse(dot, w - Pad - tw - 8, 10, 7, 7);
        }

        var rows = Rows(s);
        if (rows.Count == 0)
        {
            Text(g, s is null ? "wartet auf Daten …" : "kein Cube in Sichtweite", label, Palette.Muted, Pad + 2, Header + 7);
            return;
        }

        var y = (float)Header;
        foreach (var c in rows)
        {
            var color = c.Status switch
            {
                nameof(CubeStatus.Visible) => Palette.Visible,
                nameof(CubeStatus.Opening) => Palette.Opening,
                _ => Palette.OutOfView,
            };
            var cx = Pad + 13f;
            var cy = y + Row / 2f;
            using (var ring = new Pen(Palette.Line, 1.2f)) g.DrawEllipse(ring, cx - 13, cy - 13, 26, 26);
            if (c.Bearing is { } b) Arrow(g, cx, cy, (float)b, color);
            else Text(g, "?", label, Palette.Muted, cx - 4, cy - 9);

            var x = Pad + 34f;
            var dist = c.DistanceM is { } d ? $"{d:0} m" : "? m";
            Text(g, dist, big, color, x, cy - 13);
            x += g.MeasureString(dist, big).Width + 2;
            if (c.Compass is { } compass && c.Bearing is { } bearing)
            {
                var dir = $"{compass} {bearing:0}°";
                Text(g, dir, label, Palette.Text, x, cy - 9);
                x += g.MeasureString(dir, label).Width + 6;
            }

            var (heightText, heightColor) = c.DzUnits is { } dz
                ? Math.Abs(dz / 100) <= LevelMetres ? ("≈ gleich", Palette.Muted)
                : dz > 0 ? ($"▲ {dz / 100:0} m", Palette.Up) : ($"▼ {-dz / 100:0} m", Palette.Down)
                : ("Höhe ?", Palette.Muted);
            Text(g, heightText, label, heightColor, w - Pad, cy - 9, right: true);
            if (c.Status == nameof(CubeStatus.OutOfView)) Text(g, "außer Sicht", small, Palette.OutOfView, x, cy + 5);
            else if (c.Status == nameof(CubeStatus.Opening)) Text(g, "wird geöffnet!", small, Palette.Opening, x, cy + 5);
            y += Row;
        }
    }

    private static void Arrow(Graphics g, float cx, float cy, float bearing, Color color)
    {
        var state = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(bearing); // 0° = up = north
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, [new PointF(0, -10), new PointF(6, 6), new PointF(0, 2.5f), new PointF(-6, 6)]);
        g.Restore(state);
        using var tick = new SolidBrush(Palette.Muted);
        g.FillRectangle(tick, cx - 0.75f, cy - 13, 1.5f, 3); // north tick
    }

    private static void Text(Graphics g, string text, Font font, Color color, float x, float y, bool right = false)
    {
        using var brush = new SolidBrush(color);
        if (right) x -= g.MeasureString(text, font).Width;
        g.DrawString(text, font, brush, x, y);
    }

    /// <summary>One frame as PNG (for checking the layout without a game).</summary>
    public static void SavePng(string path, RadarSnapshot? s, float scale = 1.5f)
    {
        var size = Measure(s, scale);
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp)) Render(g, size, s, scale, unlocked: false);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
