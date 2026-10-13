using System.Globalization;

namespace CubeRadar.Core.Radar;

public sealed record Spot(string Zone, double X, double Y, double Z);

/// <summary>
/// Known possible cube spots (optional, from a local CSV <c>map,map_code,thgl_id,packet_x,packet_y,z</c>, see
/// docs/FINDINGS.md). Used to recognise cube templates of other zones and to name the zone from a position.
/// </summary>
public sealed class SpotIndex
{
    private const double Cell = 5_000;
    private readonly Dictionary<(int, int), List<Spot>> _grid = new();

    public SpotIndex(IEnumerable<Spot> spots)
    {
        All = spots.ToList();
        foreach (var s in All) Bucket(s.X, s.Y).Add(s);
    }

    public IReadOnlyList<Spot> All { get; }

    public static SpotIndex? TryLoad(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        var spots = new List<Spot>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 6) continue;
            if (double.TryParse(f[3], CultureInfo.InvariantCulture, out var x) && double.TryParse(f[4], CultureInfo.InvariantCulture, out var y) &&
                double.TryParse(f[5], CultureInfo.InvariantCulture, out var z))
                spots.Add(new Spot(f[0], x, y, z));
        }
        return new SpotIndex(spots);
    }

    /// <summary>Nearest spot within <paramref name="maxDistance"/> (planar), or null.</summary>
    public Spot? Nearest(double x, double y, double maxDistance)
    {
        Spot? best = null;
        var bestD = maxDistance;
        var r = (int)Math.Ceiling(maxDistance / Cell);
        var (cx, cy) = Key(x, y);
        for (var i = cx - r; i <= cx + r; i++)
        for (var j = cy - r; j <= cy + r; j++)
        {
            if (!_grid.TryGetValue((i, j), out var list)) continue;
            foreach (var s in list)
            {
                var d = Units.Planar(x, y, s.X, s.Y);
                if (d <= bestD)
                {
                    bestD = d;
                    best = s;
                }
            }
        }
        return best;
    }

    public IEnumerable<Spot> InZone(string zone) => All.Where(s => s.Zone == zone);

    private List<Spot> Bucket(double x, double y)
    {
        var k = Key(x, y);
        if (!_grid.TryGetValue(k, out var list)) _grid[k] = list = new List<Spot>();
        return list;
    }

    private static (int, int) Key(double x, double y) => ((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell));
}
