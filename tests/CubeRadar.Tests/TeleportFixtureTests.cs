using System.Text.Json;
using CubeRadar.Core.Protocol;
using CubeRadar.Core.Radar;

namespace CubeRadar.Tests;

/// <summary>fixtures/cubes/2026-10-13_verteron_1200041_teleport.json: 21 36 / 23 36 own positions and cube template 1200041.</summary>
public class TeleportFixtureTests
{
    private static (JsonElement Root, List<(long T, ushort Op, byte[] Body)> Frames) Load()
    {
        var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "cubes", "2026-10-13_verteron_1200041_teleport.json")));
        var frames = doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => (
            f.GetProperty("t").GetInt64(),
            Convert.ToUInt16(f.GetProperty("op").GetString(), 16),
            Convert.FromHexString(f.GetProperty("hex").GetString()!))).ToList();
        return (doc.RootElement, frames);
    }

    /// <summary>A record whose first varint is the own id (4a 36 is one of the self-only opcodes).</summary>
    private static readonly byte[] SelfVote = [0xDC, 0x47, 0x00, 0x00];

    [Fact]
    public void Map_load_and_teleport_give_the_own_position_and_the_zone()
    {
        var (_, frames) = Load();
        var engine = new RadarEngine(new RadarConfig());
        var t0 = frames[0].T;
        engine.OnFrame(t0 - 2_000, 0x4A36, SelfVote);
        engine.OnFrame(t0 - 1_000, 0x4A36, SelfVote);
        Assert.Equal(9180u, engine.SelfId);
        engine.OnFrame(frames[0].T, frames[0].Op, frames[0].Body); // where we stood before the teleport (598 m away)

        var load = frames.First(f => f.Op == 0x2136);
        engine.OnFrame(load.T, load.Op, load.Body);
        Assert.Equal(1010, engine.MapId);
        Assert.Equal("Verteron", engine.Snapshot().Zone);
        Assert.Equal(PositionKind.MapLoad, engine.SelfFix!.Value.Kind);

        var teleport = frames.First(f => f.Op == 0x2336);
        engine.OnFrame(teleport.T, teleport.Op, teleport.Body);
        var tp = engine.SelfFix!.Value;
        Assert.Equal(PositionKind.Teleport, tp.Kind);

        // The next buff record puts us exactly where the teleport said.
        var next = frames.First(f => f.Op == 0x2A38 && f.T > teleport.T);
        engine.OnFrame(next.T, next.Op, next.Body);
        var after = engine.SelfFix!.Value;
        Assert.Equal(PositionKind.BuffA, after.Kind);
        Assert.InRange(Units.Planar(tp.X, tp.Y, after.X, after.Y), 0, 1);
        Assert.Contains(engine.Snapshot().Trail, p => p.Length == 0); // teleport breaks the trail
    }

    [Fact]
    public void Teleport_before_the_own_id_is_known_is_adopted_later()
    {
        var (_, frames) = Load();
        var engine = new RadarEngine(new RadarConfig());
        var load = frames.First(f => f.Op == 0x2136);
        engine.OnFrame(load.T, load.Op, load.Body);
        Assert.Null(engine.SelfId);
        Assert.Null(engine.SelfFix);

        engine.OnFrame(load.T + 100, 0x4A36, SelfVote);
        engine.OnFrame(load.T + 200, 0x4A36, SelfVote);
        Assert.Equal(9180u, engine.SelfId);
        Assert.Equal(PositionKind.MapLoad, engine.SelfFix!.Value.Kind);
        Assert.Equal(9180u, engine.SelfFix.Value.EntityId);
    }

    [Fact]
    public void Template_1200041_cube_is_recognised_and_was_spawned_just_before_we_saw_it()
    {
        var (root, frames) = Load();
        var cube = root.GetProperty("cube");
        var engine = new RadarEngine(new RadarConfig { KeepGoneSeconds = 3600 });
        engine.OnFrame(frames[0].T - 2_000, 0x4A36, SelfVote);
        engine.OnFrame(frames[0].T - 1_000, 0x4A36, SelfVote);
        var appeared = new List<CubeState>();
        engine.CubeAppeared += c => appeared.Add(c);
        foreach (var (t, op, body) in frames) engine.OnFrame(t, op, body);

        var c = Assert.Single(appeared);
        Assert.Equal(cube.GetProperty("id").GetUInt32(), c.ObjectId);
        Assert.Equal(1200041u, c.Template);
        Assert.Equal(CubeStatus.Opened, c.Status);
        Assert.True(c.OpenedBySelf);
        Assert.InRange(c.FirstSeenMs - c.CreatedMs, 0, 2_000); // spawned right in front of us
        Assert.InRange(c.FirstDistance!.Value, 0, 6_000);       // 46 m away when it appeared
    }
}
