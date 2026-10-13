using System.Text.Json;
using CubeRadar.Core.Protocol;

namespace CubeRadar.Tests;

/// <summary>Real records carrying positions (fixtures/selfpos/*.json), including negatives that must not yield one.</summary>
public class PositionFixtureTests
{
    [Fact]
    public void Position_sources_match_the_fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "selfpos", "2026-10-13_position_sources.json")));
        var self = doc.RootElement.GetProperty("self_id").GetUInt32();
        var checkedOwn = 0;
        foreach (var r in doc.RootElement.GetProperty("records").EnumerateArray())
        {
            var op = Convert.ToUInt16(r.GetProperty("op").GetString(), 16);
            var body = Convert.FromHexString(r.GetProperty("hex").GetString()!);
            var ok = PositionMessages.TryParse(op, body, out var fix);
            var expect = r.GetProperty("expect");
            if (expect.ValueKind == JsonValueKind.Null)
            {
                Assert.False(ok && fix.EntityId == self, $"{r.GetProperty("op")} must not give an own position");
                continue;
            }
            Assert.True(ok, $"{r.GetProperty("op")} {r.GetProperty("note")}");
            Assert.Equal(expect.GetProperty("entity").GetUInt32(), fix.EntityId);
            Assert.Equal(expect.GetProperty("x").GetSingle(), fix.X, 0.05f);
            Assert.Equal(expect.GetProperty("y").GetSingle(), fix.Y, 0.05f);
            Assert.Equal(expect.GetProperty("z").GetSingle(), fix.Z, 0.05f);
            if (fix.EntityId == self) checkedOwn++;
        }
        Assert.True(checkedOwn >= 8);
    }
}
