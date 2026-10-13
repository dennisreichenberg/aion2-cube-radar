namespace CubeRadar.Core.Radar;

/// <summary>
/// Map orientation: which world axis points north. Cube Watch reports north = world −Y and east = +X (Unreal's world is
/// left-handed); that is the default, still to be confirmed in game (docs/FINDINGS.md).
/// </summary>
public readonly record struct Orientation(double NorthX, double NorthY, double EastX, double EastY)
{
    public static Orientation Parse(string north) => north.Trim().ToLowerInvariant() switch
    {
        "-y" => new Orientation(0, -1, 1, 0),
        "+y" or "y" => new Orientation(0, 1, -1, 0),
        "+x" or "x" => new Orientation(1, 0, 0, 1),
        "-x" => new Orientation(-1, 0, 0, -1),
        _ => throw new FormatException($"north must be -y, +y, +x or -x, not \"{north}\""),
    };

    /// <summary>Compass bearing 0–360° (0 = north, 90 = east) from (x0, y0) to (x1, y1).</summary>
    public double Bearing(double x0, double y0, double x1, double y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var n = dx * NorthX + dy * NorthY;
        var e = dx * EastX + dy * EastY;
        var deg = Math.Atan2(e, n) * 180 / Math.PI;
        return deg < 0 ? deg + 360 : deg;
    }

    public static string Compass(double bearing)
    {
        string[] names = ["N", "NO", "O", "SO", "S", "SW", "W", "NW"];
        return names[(int)Math.Round(bearing / 45) % 8];
    }
}

public static class Units
{
    /// <summary>Game units are centimetres (Unreal; consistent with ≈ 2 m when opening a cube, FINDINGS B7).</summary>
    public const double PerMetre = 100;

    public static double Planar(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
}
