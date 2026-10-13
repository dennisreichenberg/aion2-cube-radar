using System.Text.Json;
using CubeRadar.Core.Protocol;

namespace CubeRadar.Tests;

/// <summary>Real Hidden Cube lifecycles (fixtures/cubes/*.json): spawn → open start → open done → loot → removal.</summary>
public class CubeFixtureTests
{
    private const uint KeyItem = 0x259AC523;

    private static (JsonElement Root, List<(long T, ushort Op, byte[] Body)> Frames) Load(string name)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "cubes", name)));
        var frames = doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => (
            f.GetProperty("t").GetInt64(),
            Convert.ToUInt16(f.GetProperty("op").GetString(), 16),
            Convert.FromHexString(f.GetProperty("hex").GetString()!))).ToList();
        return (doc.RootElement, frames);
    }

    [Fact]
    public void Two_verteron_cubes_are_spawned_opened_and_removed()
    {
        var (root, frames) = Load("2026-10-13_verteron_two_cubes.json");
        var self = root.GetProperty("self_id").GetUInt32();

        foreach (var cube in root.GetProperty("cubes").EnumerateArray())
        {
            var id = cube.GetProperty("id").GetUInt32();

            var spawns = frames.Where(f => f.Op == ObjectMessages.Spawn)
                .Select(f => ObjectMessages.TryParseSpawn(f.Body, out var s) ? s : default).Where(s => s.Id == id).ToList();
            var spawn = Assert.Single(spawns);
            Assert.Equal(cube.GetProperty("template").GetUInt32(), spawn.Template);
            Assert.Equal(1200039u, spawn.Template);
            Assert.Equal(cube.GetProperty("x").GetSingle(), spawn.X, 0.01f);
            Assert.Equal(cube.GetProperty("y").GetSingle(), spawn.Y, 0.01f);
            Assert.Equal(cube.GetProperty("z").GetSingle(), spawn.Z, 0.01f);
            var spawnTime = frames.First(f => f.Op == ObjectMessages.Spawn && ObjectMessages.TryParseSpawn(f.Body, out var s) && s.Id == id).T;
            Assert.InRange(spawnTime - spawn.CreatedMs, 0, 10 * 60_000); // created before it was seen

            var interactions = frames.Where(f => f.Op is ObjectMessages.OpenStart or ObjectMessages.OpenDone)
                .Select(f => ObjectMessages.TryParseInteraction(f.Body, f.Op == ObjectMessages.OpenDone, out var i) ? i : default)
                .Where(i => i.ObjectId == id).ToList();
            Assert.Equal(2, interactions.Count);
            Assert.False(interactions[0].Done);
            Assert.True(interactions[1].Done);
            Assert.All(interactions, i => Assert.Equal(self, i.ActorId));
            Assert.Equal((1u, KeyItem), (interactions[1].Count, interactions[1].Item));

            var removed = frames.Where(f => f.Op == ObjectMessages.Removed)
                .Select(f => ObjectMessages.TryParseRemoved(f.Body, out var r) ? r : default).Single(r => r.Id == id);
            Assert.Equal(ObjectMessages.ReasonDestroyed, removed.Reason);

            // Own position at the moment of opening: within a few metres of the cube.
            var openT = frames.First(f => f.Op == ObjectMessages.OpenStart && ObjectMessages.TryParseInteraction(f.Body, false, out var i) && i.ObjectId == id).T;
            var fix = frames.Where(f => f.T <= openT)
                .Select(f => PositionMessages.TryParse(f.Op, f.Body, out var p) ? p : default).Last(p => p.EntityId == self);
            var d = MathF.Sqrt(MathF.Pow(fix.X - spawn.X, 2) + MathF.Pow(fix.Y - spawn.Y, 2) + MathF.Pow(fix.Z - spawn.Z, 2));
            Assert.InRange(d, 0, 500);
        }
    }
}
