using System.Text.Json;

namespace CubeRadar.Cli;

/// <summary>Per-user radar settings in <c>data/settings.json</c>: north axis, sound and overlay.</summary>
internal sealed class RadarSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "+y" readable
    };
    private readonly object _gate = new();
    private string _path = "";

    public string? North { get; set; }
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    public bool OverlayVisible { get; set; } = true;
    public int OverlayRangeM { get; set; } = 120;
    /// <summary>Hide the overlay while the mouse cursor is visible (in-game menus / UI mode).</summary>
    public bool OverlayHideInMenus { get; set; } = true;
    /// <summary>Show the overlay only while AION 2 is the foreground window.</summary>
    public bool OverlayOnlyWhenGameFocused { get; set; } = true;
    public int OverlayScalePct { get; set; } = 100;
    public int OverlayOpacityPct { get; set; } = 90;
    /// <summary>Sound when a new cube appears.</summary>
    public bool SoundEnabled { get; set; } = true;
    /// <summary>Optional .wav played instead of the beep.</summary>
    public string? SoundFile { get; set; }
    /// <summary>Volume of the sound, 5–100 %.</summary>
    public int SoundVolumePct { get; set; } = 80;

    public static RadarSettings Load(string path)
    {
        RadarSettings s;
        try
        {
            s = File.Exists(path) ? JsonSerializer.Deserialize<RadarSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            s = new();
        }
        s._path = path;
        return s;
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(this, Json));
            }
            catch (IOException)
            {
                // settings are a convenience; never let them stop the radar
            }
        }
    }
}
