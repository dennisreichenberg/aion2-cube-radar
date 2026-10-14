using System.Text.Json;
using CubeRadar.Core.Capture;
using CubeRadar.Core.Dump;
using CubeRadar.Core.Protocol;
using static CubeRadar.Tests.Synth;

namespace CubeRadar.Tests;

/// <summary>pcapng writer → libpcap reader → flow lock → framing → JSONL, with a marker merged in by time.</summary>
public class PipelineTests
{
    private static readonly byte[] Server = [104, 18, 0, 1], Client = [192, 168, 1, 20];

    [Fact]
    [Trait("Needs", "Npcap")] // libpcap reads the file back; the GitHub runner has no Npcap
    public void Pcapng_roundtrip_through_the_dump_pipeline()
    {
        var dir = Directory.CreateTempSubdirectory("cuberadar-test").FullName;
        try
        {
            var pcap = Path.Combine(dir, "raw.pcapng");
            const long t0 = 1_760_000_000_000;
            var stream = Concat(
                Heartbeat(t0), Heartbeat(t0 + 50), Heartbeat(t0 + 100),
                Bundle(Concat(Frame(0x4136, Body(40, 1)), Frame(0x3436, Body(30, 2)))),
                Heartbeat(t0 + 150), Frame(0x0438, Body(200, 3)), Heartbeat(t0 + 200));

            using (var w = new PcapNgWriter(pcap, "test"))
            {
                var iface = w.Interface("test0", 1);
                uint seq = 5000;
                var us = (ulong)t0 * 1000;
                // SYN, then the stream in 3 segments, the 2nd and 3rd swapped on the wire.
                w.WritePacket(iface, us, TcpPacket(Server, 13328, Client, 50000, seq - 1, 0x12, []), 54);
                var parts = stream.Chunk(40).Select(c => c.ToArray()).ToList();
                var order = Enumerable.Range(0, parts.Count).ToList();
                (order[1], order[2]) = (order[2], order[1]);
                foreach (var i in order)
                {
                    var off = (uint)parts.Take(i).Sum(p => p.Length);
                    var pkt = TcpPacket(Server, 13328, Client, 50000, seq + off, 0x18, parts[i]);
                    w.WritePacket(iface, us + (ulong)(i + 1) * 10_000, pkt, pkt.Length);
                }
            }

            var jsonl = Path.Combine(dir, "frames.jsonl");
            string report;
            using (var session = new DumpSession(OpcodeBook.Empty, new DumpOptions(), jsonl))
            {
                session.QueueReplayMarkers([new Marker(t0 + 25, "cube_visible", "test", "hotkey")]);
                var n = CaptureFileSource.ReadAll(pcap, raw => session.OnPacket(raw, "file"));
                Assert.True(n > 3);
                report = session.Finish(null);
                Assert.True(session.Tracker.GameFlows.Single().Aligned);
            }

            var lines = File.ReadAllLines(jsonl).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            var frames = lines.Where(l => l.GetProperty("type").GetString() == "frame").ToList();
            Assert.Equal(["0036", "0036", "0036", "4136", "3436", "0036", "0438", "0036"],
                frames.Select(f => f.GetProperty("op").GetString()));
            Assert.Equal("lz4", frames[3].GetProperty("src").GetString());
            Assert.Equal("B1.1", frames[4].GetProperty("path").GetString());
            Assert.Equal(200, frames[6].GetProperty("len").GetInt32());
            Assert.Equal(Convert.ToHexStringLower(Body(200, 3)), frames[6].GetProperty("hex").GetString());

            var types = lines.Select(l => l.GetProperty("type").GetString()).ToList();
            Assert.Contains("flow", types);
            Assert.Contains("bundle", types);
            var marker = lines.Single(l => l.GetProperty("type").GetString() == "marker");
            Assert.Equal("cube_visible", marker.GetProperty("label").GetString());
            Assert.Contains("resyncs", report);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
