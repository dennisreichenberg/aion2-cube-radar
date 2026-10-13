using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CubeRadar.Core.Radar;

namespace CubeRadar.Cli;

/// <summary>
/// Small always-on-top, click-through mini radar over the game: north up, we in the middle, live cubes with distance
/// and height, our recent trail. It never takes focus. Ctrl+Shift+F10 shows/hides it, Ctrl+Shift+F11 unlocks it for
/// dragging (position saved when locking again), Ctrl+Shift+F12 cycles the range. Works over windowed / borderless
/// games; nothing can draw over exclusive fullscreen.
/// </summary>
internal sealed class OverlayWindow : Form
{
    private const int WsExLayered = 0x80000, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExTopmost = 0x8;
    private const int GwlExStyle = -20;
    private static readonly IntPtr HwndTopmost = new(-1);
    public static readonly int[] Ranges = [60, 120, 250];

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
        Size = OverlayRenderer.Measure(scale);
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

    public void CycleRange() => Ui(() =>
    {
        var i = Array.IndexOf(Ranges, _settings.OverlayRangeM);
        _settings.OverlayRangeM = Ranges[(i + 1) % Ranges.Length];
        _settings.Save();
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
        // Some games push themselves to the top when they regain focus: re-assert every 2 s.
        if (++_ticks % 8 == 0 && Visible) SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) =>
        OverlayRenderer.Render(e.Graphics, ClientSize, _last, DeviceDpi / 96f, _settings.OverlayRangeM, _unlocked);

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
    public static readonly Color Background = Color.FromArgb(14, 18, 23);
    public static readonly Color Text = Color.FromArgb(230, 235, 240);
    public static readonly Color Muted = Color.FromArgb(139, 150, 163);
    public static readonly Color Line = Color.FromArgb(48, 58, 70);
    public static readonly Color Visible = Color.FromArgb(61, 220, 132);
    public static readonly Color OutOfView = Color.FromArgb(242, 179, 61);
    public static readonly Color Opening = Color.FromArgb(255, 225, 77);
    public static readonly Color Me = Color.FromArgb(77, 163, 255);
    public static readonly Color Warn = Color.FromArgb(255, 107, 91);
}

/// <summary>Draws the mini radar; also used to render a PNG for checking the layout.</summary>
internal static class OverlayRenderer
{
    private const float Side = 230, Header = 22, Margin = 14;
    private const double LevelMetres = 3, SendRadiusM = 100;

    public static Size Measure(float scale) => new((int)(Side * scale), (int)((Side + Header) * scale));

    public static void Render(Graphics g, Size size, RadarSnapshot? s, float scale, int rangeM, bool unlocked)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Palette.Background);
        g.ScaleTransform(scale, scale);

        using var small = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
        using var label = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var tiny = new Font("Segoe UI", 7f, FontStyle.Regular, GraphicsUnit.Point);

        using (var border = new Pen(unlocked ? Palette.Warn : Palette.Line, unlocked ? 2 : 1) { DashStyle = unlocked ? DashStyle.Dash : DashStyle.Solid })
            g.DrawRectangle(border, 0.5f, 0.5f, Side - 1, Side + Header - 1);

        // header: name, own position age, range
        Text(g, "CUBE RADAR", small, Palette.Muted, 7, 4);
        Text(g, $"{rangeM} m", small, Palette.Muted, Side - 7, 4, right: true);
        var me = Me(s);
        if (s is not null)
        {
            var (posText, posColor) = me switch
            {
                { Ring: > 0 } m => ($"± {m.Ring / 100:0} m", Palette.OutOfView),
                { } m => ($"Pos. {m.AgeMs / 1000} s", m.AgeMs <= s.FixFreshSeconds * 1000 ? Palette.Visible : m.AgeMs <= 30_000 ? Palette.OutOfView : Palette.Warn),
                null => ("Pos. ?", Palette.Warn),
            };
            var tw = g.MeasureString(posText, small).Width;
            var px = Side / 2 - tw / 2 + 5;
            using var dot = new SolidBrush(posColor);
            g.FillEllipse(dot, px - 10, 9, 6, 6);
            Text(g, posText, small, Palette.Muted, px, 4);
        }

        var cx = Side / 2;
        var cy = Header + Side / 2;
        var r = Side / 2 - Margin;
        var px2world = r / (rangeM * 100f); // pixels per game unit

        // rings, cross, north
        using (var ring = new Pen(Palette.Line, 1))
        {
            g.DrawEllipse(ring, cx - r, cy - r, 2 * r, 2 * r);
            g.DrawEllipse(ring, cx - r / 2, cy - r / 2, r, r);
            g.DrawLine(ring, cx - r, cy, cx + r, cy);
            g.DrawLine(ring, cx, cy - r, cx, cy + r);
        }
        Text(g, $"{rangeM / 2} m", tiny, Palette.Muted, cx + 3, cy - r / 2 - 12);
        Text(g, "N", small, Palette.Text, cx - 4, cy - r - 13);
        if (SendRadiusM < rangeM)
        {
            using var send = new Pen(Color.FromArgb(110, Palette.Me), 1) { DashStyle = DashStyle.Dash };
            var sr = (float)(SendRadiusM * 100 * px2world);
            g.DrawEllipse(send, cx - sr, cy - sr, 2 * sr, 2 * sr);
        }

        if (s is null || me is null)
        {
            Text(g, s is null ? "wartet auf Daten …" : "eigene Position unbekannt", label, Palette.Muted, cx, cy + r / 2, center: true);
            if (s is not null) Text(g, "(Buff wirken hilft)", tiny, Palette.Muted, cx, cy + r / 2 + 16, center: true);
            return;
        }

        var o = s.Orientation;
        PointF ToScreen(double x, double y)
        {
            double dx = x - me.X, dy = y - me.Y;
            var n = dx * o.NorthX + dy * o.NorthY;
            var e = dx * o.EastX + dy * o.EastY;
            return new PointF(cx + (float)(e * px2world), cy - (float)(n * px2world));
        }

        // own trail (last ~2 minutes): shows where we came from, i.e. roughly where we are heading
        using (var trail = new Pen(Color.FromArgb(90, Palette.Me), 1.5f))
        {
            PointF? prev = null;
            foreach (var p in s.Trail.TakeLast(40))
            {
                if (p.Length < 2) { prev = null; continue; }
                var q = ToScreen(p[0], p[1]);
                if (prev is { } a && Inside(a, cx, cy, r) && Inside(q, cx, cy, r)) g.DrawLine(trail, a, q);
                prev = q;
            }
        }

        // us
        if (me.Ring > 0)
        {
            using var unc = new Pen(Color.FromArgb(140, Palette.Me), 1) { DashStyle = DashStyle.Dot };
            var ur = Math.Max(4f, (float)(me.Ring * px2world));
            g.DrawEllipse(unc, cx - ur, cy - ur, 2 * ur, 2 * ur);
        }
        using (var meBrush = new SolidBrush(me.AgeMs <= s.FixFreshSeconds * 1000 ? Palette.Me : Color.FromArgb(130, Palette.Me)))
            g.FillEllipse(meBrush, cx - 4.5f, cy - 4.5f, 9, 9);

        // cubes, nearest drawn last (on top)
        var live = s.Cubes.Where(c => c.Status is nameof(CubeStatus.Visible) or nameof(CubeStatus.Opening) or nameof(CubeStatus.OutOfView))
            .OrderByDescending(c => c.DistanceM ?? double.MaxValue).ToList();
        foreach (var c in live)
        {
            var color = c.Status switch
            {
                nameof(CubeStatus.Visible) => Palette.Visible,
                nameof(CubeStatus.Opening) => Palette.Opening,
                _ => Palette.OutOfView,
            };
            var dist = Math.Sqrt(Math.Pow(c.X - me.X, 2) + Math.Pow(c.Y - me.Y, 2)) / 100;
            (string Kind, string Text) height = c.DzUnits is { } dz
                ? Math.Abs(dz / 100) <= LevelMetres ? ("level", "≈") : dz > 0 ? ("up", $"▲{dz / 100:0}") : ("down", $"▼{-dz / 100:0}")
                : ("level", "");
            var text = $"{dist:0} m {height.Text}".Trim();
            var p = ToScreen(c.X, c.Y);
            if (Inside(p, cx, cy, r - 4))
            {
                Marker(g, p.X, p.Y, 6.5f, height.Kind, color, filled: c.Status != nameof(CubeStatus.OutOfView));
                var lx = p.X + 9;
                if (lx + g.MeasureString(text, label).Width > Side - 3) lx = p.X - 9 - g.MeasureString(text, label).Width;
                Text(g, text, label, color, lx, p.Y - 8);
            }
            else
            {
                // beyond the range: an open chevron on the rim (not a filled triangle, which would read as ▲/▼ height)
                var a = Math.Atan2(p.Y - cy, p.X - cx);
                var ax = cx + (float)Math.Cos(a) * (r - 7);
                var ay = cy + (float)Math.Sin(a) * (r - 7);
                var state = g.Save();
                g.TranslateTransform(ax, ay);
                g.RotateTransform((float)(a * 180 / Math.PI));
                using (var chevron = new Pen(color, 2.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLines(chevron, [new PointF(-4, -7), new PointF(5, 0), new PointF(-4, 7)]);
                    g.DrawLines(chevron, [new PointF(-10, -7), new PointF(-1, 0), new PointF(-10, 7)]);
                }
                g.Restore(state);
                var tx = cx + (float)Math.Cos(a) * (r - 30);
                var ty = cy + (float)Math.Sin(a) * (r - 22);
                Text(g, text, label, color, tx, ty - 7, center: true);
            }
        }
    }

    private sealed record MeView(double X, double Y, long AgeMs, double Ring);

    /// <summary>Our position: the later of real fix and ring estimate (as in the web UI).</summary>
    private static MeView? Me(RadarSnapshot? s)
    {
        if (s is null) return null;
        if (s.Ring is { } r && (s.Self is null || r.TimeMs > s.Self.TimeMs)) return new MeView(r.X, r.Y, r.AgeMs, r.Uncertainty);
        return s.Self is { } f ? new MeView(f.X, f.Y, f.AgeMs, 0) : null;
    }

    private static bool Inside(PointF p, float cx, float cy, float r) => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) <= r * r;

    /// <summary>Square for "same height", triangle up / down for above / below us.</summary>
    private static void Marker(Graphics g, float x, float y, float s, string kind, Color color, bool filled)
    {
        PointF[] pts = kind switch
        {
            "up" => [new(x, y - s), new(x + s, y + s * 0.8f), new(x - s, y + s * 0.8f)],
            "down" => [new(x, y + s), new(x + s, y - s * 0.8f), new(x - s, y - s * 0.8f)],
            _ => [new(x - s * 0.8f, y - s * 0.8f), new(x + s * 0.8f, y - s * 0.8f), new(x + s * 0.8f, y + s * 0.8f), new(x - s * 0.8f, y + s * 0.8f)],
        };
        if (filled)
        {
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, pts);
        }
        else
        {
            using var pen = new Pen(color, 2);
            g.DrawPolygon(pen, pts);
        }
    }

    private static void Text(Graphics g, string text, Font font, Color color, float x, float y, bool right = false, bool center = false)
    {
        using var brush = new SolidBrush(color);
        var w = g.MeasureString(text, font).Width;
        if (right) x -= w;
        else if (center) x -= w / 2;
        g.DrawString(text, font, brush, x, y);
    }

    /// <summary>One frame as PNG (for checking the layout without a game).</summary>
    public static void SavePng(string path, RadarSnapshot? s, int rangeM = 120, float scale = 1.5f)
    {
        var size = Measure(scale);
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp)) Render(g, size, s, scale, rangeM, unlocked: false);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
