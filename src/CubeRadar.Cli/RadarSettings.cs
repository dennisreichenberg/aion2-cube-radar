using System.Text.Json;

namespace CubeRadar.Cli;

/// <summary>Per-user radar settings in <c>data/settings.json</c>: north axis and overlay placement.</summary>
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
