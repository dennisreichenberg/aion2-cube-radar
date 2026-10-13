using System.Text.Json;
using CubeRadar.Core.Radar;

namespace CubeRadar.Tests;

public class RadarEngineTests
{
    private static List<(long T, ushort Op, byte[] Body)> CubeFrames()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "cubes", "2026-10-13_verteron_two_cubes.json")));
        return doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => (
            f.GetProperty("t").GetInt64(),
            Convert.ToUInt16(f.GetProperty("op").GetString(), 16),
            Convert.FromHexString(f.GetProperty("hex").GetString()!))).ToList();
    }

    [Fact]
    public void Real_cube_lifecycle_ends_opened_by_us_and_is_stored()
    {
        using var store = new SqliteRadarStore(":memory:", "test");
        var engine = new RadarEngine(new RadarConfig { KeepGoneSeconds = 3600 }, store);
        var appeared = new List<uint>();
        engine.CubeAppeared += c => appeared.Add(c.ObjectId);
        foreach (var (t, op, body) in CubeFrames()) engine.OnFrame(t, op, body);

        Assert.Equal(new uint[] { 65509, 34438 }, appeared);
        Assert.Equal(9180u, engine.SelfId);
        var s = engine.Snapshot();
        Assert.Equal(2, s.Cubes.Count);
        Assert.All(s.Cubes, c =>
        {
            Assert.Equal(nameof(CubeStatus.Opened), c.Status);
            Assert.True(c.OpenedBySelf);
            Assert.Equal("Verteron", c.Zone);
        });
        Assert.NotNull(s.Self);

        var csv = store.Export(json: false).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, csv.Length); // header + 2 sightings
        Assert.Contains(csv, l => l.Contains(",65509,1200039,Verteron,") && l.Contains(",Opened,"));
    }

    [Fact]
    public void Out_of_view_cube_that_is_not_sent_again_when_we_come_close_is_gone()
    {
        var frames = CubeFrames();
        var engine = new RadarEngine(new RadarConfig());
        var spawn = frames.First(f => f.Op == 0x3436);                                // cube 65509
        var fixes = frames.Where(f => f.Op == 0x2A38).ToList();
        var near = fixes.Single(f => f.T == 1791884830559);                           // 195 units from cube 65509

        foreach (var f in fixes.Take(4)) engine.OnFrame(f.T - 100_000, f.Op, f.Body); // votes: we are 9180
        engine.OnFrame(spawn.T, spawn.Op, spawn.Body);
        engine.OnFrame(spawn.T + 1_000, 0x3536, [0xE5, 0xFF, 0x03, 0x01, 0x01, 0x00]); // left view (reason 01)
        Assert.Equal(nameof(CubeStatus.OutOfView), engine.Snapshot().Cubes.Single().Status);

        engine.OnFrame(spawn.T + 60_000, near.Op, near.Body);                         // back next to it, no 34 36
        engine.OnFrame(spawn.T + 62_000, 0x0036, new byte[8]);
        Assert.Equal(nameof(CubeStatus.OutOfView), engine.Snapshot().Cubes.Single().Status);
        engine.OnFrame(spawn.T + 63_100, 0x0036, new byte[8]);
        Assert.Equal(nameof(CubeStatus.Gone), engine.Snapshot().Cubes.Single().Status);
    }

    [Fact]
    public void Cube_sent_again_is_visible_again()
    {
        var frames = CubeFrames();
        var engine = new RadarEngine(new RadarConfig());
        var spawn = frames.First(f => f.Op == 0x3436);
        engine.OnFrame(spawn.T, spawn.Op, spawn.Body);
        engine.OnFrame(spawn.T + 1_000, 0x3536, [0xE5, 0xFF, 0x03, 0x01, 0x01, 0x00]);
        engine.OnFrame(spawn.T + 9_000, spawn.Op, spawn.Body);
        var c = engine.Snapshot().Cubes.Single();
        Assert.Equal(nameof(CubeStatus.Visible), c.Status);
        Assert.Equal(spawn.T + 9_000, c.LastSeenMs);
    }

    [Fact]
    public void Unknown_template_opened_with_the_key_item_is_learned_as_a_cube()
    {
        var frames = CubeFrames();
        var engine = new RadarEngine(new RadarConfig { CubeTemplates = new(), KeepGoneSeconds = 3600 }); // knows no cube template
        var learned = new List<uint>();
        engine.TemplateLearned += t => learned.Add(t.Template);
        foreach (var (t, op, body) in frames) engine.OnFrame(t, op, body);
        Assert.Equal(new uint[] { 1200039 }, learned);
        Assert.Equal(2, engine.Snapshot().Cubes.Count);
    }

    [Fact]
    public void Bearing_follows_the_configured_north()
    {
        var north = Orientation.Parse("-y");
        Assert.Equal(0, north.Bearing(0, 0, 0, -100), 3);   // −Y = north
        Assert.Equal(90, north.Bearing(0, 0, 100, 0), 3);   // +X = east
        Assert.Equal(180, north.Bearing(0, 0, 0, 100), 3);
        Assert.Equal("SW", Orientation.Compass(225));
    }
}
