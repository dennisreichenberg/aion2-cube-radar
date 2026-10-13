using System.Drawing;
using System.Windows.Forms;

namespace CubeRadar.Cli;

/// <summary>What the overlay and its settings window can ask the radar to do.</summary>
internal sealed record OverlayActions(
    Action OpenWeb,
    Action Quit,
    Action TestSound,
    Func<string> GetNorth,
    Action<string> SetNorth);

/// <summary>
/// Settings opened from the overlay's gear button (or the tray). Every change applies at once and is saved; there is
/// no OK / Cancel. Runs on the overlay's UI thread.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly Color Bg = Color.FromArgb(22, 27, 33), Field = Color.FromArgb(32, 39, 47), Fg = Color.FromArgb(230, 235, 240),
        Muted = Color.FromArgb(139, 150, 163), Accent = Color.FromArgb(77, 163, 255), Danger = Color.FromArgb(255, 107, 91);

    private static readonly (string Value, string Text)[] Norths =
        [("-y", "−Y (Standard)"), ("+y", "+Y"), ("+x", "+X"), ("-x", "−X")];

    private bool _preview;

    /// <summary>The PNG preview must not steal the focus from the game.</summary>
    protected override bool ShowWithoutActivation => _preview;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE: dark title bar
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public SettingsForm(RadarSettings settings, OverlayActions actions, Action applied, Action resetPosition)
    {
        Text = "Cube Radar – Einstellungen";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 12, 16, 12);

        void Save()
        {
            settings.Save();
            applied();
        }

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, BackColor = Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Section(string title)
        {
            var l = new Label { Text = title, AutoSize = true, ForeColor = Accent, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) };
            grid.Controls.Add(l);
            grid.SetColumnSpan(l, 2);
        }

        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Fg, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
            control.Margin = new Padding(0, 3, 0, 3);
            grid.Controls.Add(control);
        }

        void Full(Control control)
        {
            grid.Controls.Add(control);
            grid.SetColumnSpan(control, 2);
        }

        // ---- sound
        Section("Ton bei neuem Cube");
        var sound = Check("Ton abspielen", settings.SoundEnabled);
        sound.CheckedChanged += (_, _) => { settings.SoundEnabled = sound.Checked; Save(); };
        var test = Btn("Testen");
        test.Click += (_, _) => actions.TestSound();
        Full(Flow(sound, test));

        var file = new TextBox { Width = 230, ReadOnly = true, BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Text = settings.SoundFile ?? "" };
        var browse = Btn("Durchsuchen …");
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "WAV-Dateien (*.wav)|*.wav", Title = "Sound für neue Cubes" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            settings.SoundFile = file.Text = dlg.FileName;
            Save();
        };
        var clear = Btn("Standard-Piepton");
        clear.Click += (_, _) => { settings.SoundFile = null; file.Text = ""; Save(); };
        Row("Eigene .wav (optional)", Flow(file, browse, clear));

        // ---- overlay look
        Section("Overlay");
        var range = Combo(OverlayWindow.Ranges.Select(r => $"{r} m"), Array.IndexOf(OverlayWindow.Ranges, settings.OverlayRangeM));
        range.SelectedIndexChanged += (_, _) => { settings.OverlayRangeM = OverlayWindow.Ranges[range.SelectedIndex]; Save(); };
        Row("Zoom (auch Mausrad)", range);

        int[] scales = [75, 100, 125, 150];
        var scale = Combo(scales.Select(s => $"{s} %"), Array.IndexOf(scales, settings.OverlayScalePct));
        scale.SelectedIndexChanged += (_, _) => { settings.OverlayScalePct = scales[scale.SelectedIndex]; Save(); };
        Row("Größe", scale);

        var opacity = new TrackBar { Minimum = 40, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Width = 200,
            Value = Math.Clamp(settings.OverlayOpacityPct, 40, 100), BackColor = Bg };
        var opacityText = new Label { AutoSize = true, ForeColor = Muted, Text = $"{opacity.Value} %", Margin = new Padding(6, 8, 0, 0) };
        opacity.ValueChanged += (_, _) => { settings.OverlayOpacityPct = opacity.Value; opacityText.Text = $"{opacity.Value} %"; Save(); };
        Row("Deckkraft", Flow(opacity, opacityText));

        var north = Combo(Norths.Select(n => n.Text), Math.Max(0, Array.FindIndex(Norths, n => n.Value == actions.GetNorth())));
        north.SelectedIndexChanged += (_, _) => actions.SetNorth(Norths[north.SelectedIndex].Value);
        Row("Norden", north);
        Full(new Label { Text = "Passt die Richtung nicht zur Minimap, hier umstellen (gilt auch für die Web-Ansicht).",
            AutoSize = true, ForeColor = Muted, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 4) });

        // ---- visibility
        Section("Wann ist das Overlay sichtbar?");
        var focus = Check("Nur wenn Aion 2 im Vordergrund ist", settings.OverlayOnlyWhenGameFocused);
        focus.CheckedChanged += (_, _) => { settings.OverlayOnlyWhenGameFocused = focus.Checked; Save(); };
        Full(focus);
        var menus = Check("In Menüs ausblenden (solange der Mauszeiger sichtbar ist)", settings.OverlayHideInMenus);
        menus.CheckedChanged += (_, _) => { settings.OverlayHideInMenus = menus.Checked; Save(); };
        Full(menus);

        // ---- actions
        Section("Sonstiges");
        var reset = Btn("Position zurücksetzen");
        reset.Click += (_, _) => resetPosition();
        var web = Btn("Web-Ansicht öffnen");
        web.Click += (_, _) => actions.OpenWeb();
        Full(Flow(reset, web));
        Full(new Label { Text = "Hotkeys: Strg+Umschalt+F10 = Overlay an/aus · Strg+Umschalt+F12 = Zoom · Verschieben: Overlay mit der Maus ziehen",
            AutoSize = true, ForeColor = Muted, MaximumSize = new Size(420, 0), Margin = new Padding(0, 8, 0, 0) });

        var quit = Btn("Radar beenden");
        quit.ForeColor = Danger;
        quit.FlatAppearance.BorderColor = Danger;
        quit.Click += (_, _) => { Close(); actions.Quit(); };
        var close = Btn("Schließen");
        close.Click += (_, _) => Close();
        var bottom = Flow(quit, close);
        bottom.Margin = new Padding(0, 14, 0, 0);
        Full(bottom);

        Controls.Add(grid);
    }

    /// <summary>Renders the window to a PNG without showing it (for checking the layout).</summary>
    public static void SavePng(string path, RadarSettings settings, OverlayActions actions)
    {
        var t = new Thread(() =>
        {
            // Controls are only created when the window is shown: show it far off-screen, without taking the focus.
            using var form = new SettingsForm(settings, actions, () => { }, () => { })
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-30_000, -30_000),
                ShowInTaskbar = false,
                _preview = true,
            };
            form.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    private static CheckBox Check(string text, bool value) =>
        new() { Text = text, Checked = value, AutoSize = true, ForeColor = Fg, Margin = new Padding(0, 3, 0, 3) };

    private static Button Btn(string text)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Fg, Padding = new Padding(6, 1, 6, 1) };
        b.FlatAppearance.BorderColor = Color.FromArgb(60, 70, 82);
        return b;
    }

    private static ComboBox Combo(IEnumerable<string> items, int selected)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat };
        c.Items.AddRange(items.Cast<object>().ToArray());
        c.SelectedIndex = Math.Max(0, selected);
        return c;
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Bg, Margin = new Padding(0) };
        foreach (var c in controls)
        {
            c.Margin = new Padding(0, 3, 8, 3);
            f.Controls.Add(c);
        }
        return f;
    }
}
