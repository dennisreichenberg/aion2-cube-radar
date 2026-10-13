using System.Text.Json;
using CubeRadar.Core.Dump;
using CubeRadar.Core.Protocol;

namespace CubeRadar.Tests;

/// <summary>
/// Real server→client stream slices (fixtures/*.json, cut by tools/make_fixture.py). The expected frames there come
/// from the independent Python decoder in tools/a2wire.py; the C# decoder must find exactly the same frames.
/// </summary>
public class RealFixtureTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        foreach (var f in Directory.GetFiles(dir, "*.json").Order()) data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decoder_matches_the_fixture(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));
        var root = doc.RootElement;
        var stream = Convert.FromHexString(root.GetProperty("stream_hex").GetString()!);
        var expected = root.GetProperty("frames").EnumerateArray().Select(f => (
            f.GetProperty("op").GetString()!,
            f.GetProperty("src").GetString()!,
            f.GetProperty("depth").GetInt32(),
            f.GetProperty("path").GetString()!,
            f.GetProperty("flag").GetInt32(),
            f.GetProperty("wire").GetInt32(),
            f.GetProperty("hex").GetString()!)).ToList();

        var got = new List<(string, string, int, string, int, int, string)>();
        var d = new FrameDecoder((in FrameMeta m, ReadOnlySpan<byte> body) =>
            got.Add((OpcodeBook.Hex(m.Opcode), JsonlWriter.Source(m.Source), m.Depth, m.Path, m.Flag, m.WireSize, Convert.ToHexStringLower(body))));
        foreach (var chunk in stream.Chunk(1460)) d.Feed(chunk); // MSS-sized pieces, frames straddle them

        Assert.Equal(expected.Count, got.Count);
        for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], got[i]);
        Assert.Equal(0, d.Resyncs);
        Assert.Equal(0, d.BadBundles);
        Assert.Equal(0, d.TruncatedBundles);
        Assert.Equal(0, d.DroppedBytes);
    }
}
