using System.Text.Json;
using CubeRadar.Core.Protocol;

namespace CubeRadar.Core.Radar;

public enum RecordKind
{
    Heartbeat,
    SelfInfo,
    MapLoad,
    Teleport,
    ObjectSpawn,
    ObjectRemoved,
    OpenStart,
    OpenDone,
    PosBuffA,
    PosBuffB,
    PosCast,
    PosTarget,
}

public sealed record SelfCheckRule(RecordKind Kind, int WithinSeconds, string Hint);

public sealed record CubeTemplate(uint Template, string Zone, string Status);

/// <summary><c>config/radar.json</c> plus the cube signature from <c>config/cubes.json</c>.</summary>
public sealed class RadarConfig
{
    public Dictionary<ushort, RecordKind> Opcodes { get; init; } = DefaultOpcodes();
    public HashSet<ushort> SelfIdOpcodes { get; init; } = [0x4A36, 0x4437, 0x4637, 0x038D, 0x028D, 0x338A];
    public List<SelfCheckRule> SelfCheck { get; init; } = [];
    public Dictionary<uint, CubeTemplate> CubeTemplates { get; init; } = new()
    {
        [1200039] = new CubeTemplate(1200039, "Verteron", "verified"),
        [1200041] = new CubeTemplate(1200041, "Verteron", "verified"),
    };
    public Dictionary<int, string> MapZones { get; init; } = new() { [1010] = "Verteron" };
    public uint OpenDoneItem { get; init; } = 0x259AC523;
    public string North { get; init; } = "-y";
    public double FixFreshSeconds { get; init; } = 5;
    public double KeepOutOfViewMinutes { get; init; } = 30;
    public double KeepGoneSeconds { get; init; } = 60;
    public string? SpotsCsv { get; init; }
    public double SpotMatchUnits { get; init; } = 10;
    public double GoneCheckUnits { get; init; } = 8000;
    public int RingWindowMs { get; init; } = 2000;
    public int RingMinEvents { get; init; } = 3;

    public ushort OpcodeOf(RecordKind kind) => Opcodes.First(kv => kv.Value == kind).Key;

    public static Dictionary<ushort, RecordKind> DefaultOpcodes() => new()
    {
        [0x0036] = RecordKind.Heartbeat,
        [0x3336] = RecordKind.SelfInfo,
        [0x2136] = RecordKind.MapLoad,
        [0x2336] = RecordKind.Teleport,
        [0x3436] = RecordKind.ObjectSpawn,
        [0x3536] = RecordKind.ObjectRemoved,
        [0x3836] = RecordKind.OpenStart,
        [0x3A36] = RecordKind.OpenDone,
        [0x2A38] = RecordKind.PosBuffA,
        [0x2B38] = RecordKind.PosBuffB,
        [0x0238] = RecordKind.PosCast,
        [0x0E38] = RecordKind.PosTarget,
    };

    /// <summary>Loads radar.json and cubes.json; relative paths in them (spots_csv) resolve against
    /// <paramref name="projectRoot"/>.</summary>
    public static RadarConfig Load(string radarJson, string cubesJson, string projectRoot)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var radar = JsonDocument.Parse(File.ReadAllText(radarJson), options);
        var r = radar.RootElement;

        var opcodes = new Dictionary<ushort, RecordKind>();
        foreach (var p in r.GetProperty("opcodes").EnumerateObject())
        {
            if (!OpcodeBook.TryParse(p.Value.GetString() ?? "", out var op)) throw new FormatException($"radar.json: bad opcode for {p.Name}");
            opcodes[op] = ParseKind(p.Name);
        }

        var selfIds = new HashSet<ushort>();
        foreach (var e in r.GetProperty("self_id_opcodes").EnumerateArray())
            if (OpcodeBook.TryParse(e.GetString() ?? "", out var op)) selfIds.Add(op);

        var checks = r.TryGetProperty("self_check", out var sc)
            ? sc.EnumerateArray().Select(e => new SelfCheckRule(ParseKind(e.GetProperty("kind").GetString()!),
                e.GetProperty("within_s").GetInt32(), e.TryGetProperty("hint", out var h) ? h.GetString() ?? "" : "")).ToList()
            : [];

        var cubes = new Dictionary<uint, CubeTemplate>();
        uint openItem = 0x259AC523;
        var cubesPath = cubesJson;
        if (File.Exists(cubesPath))
        {
            using var c = JsonDocument.Parse(File.ReadAllText(cubesPath), options);
            foreach (var t in c.RootElement.GetProperty("templates").EnumerateObject())
            {
                var id = uint.Parse(t.Name);
                cubes[id] = new CubeTemplate(id, t.Value.GetProperty("zone").GetString() ?? "?", t.Value.GetProperty("status").GetString() ?? "");
            }
            if (c.RootElement.TryGetProperty("open_done_item", out var item)) openItem = item.GetProperty("value").GetUInt32();
        }

        var mapZones = new Dictionary<int, string>();
        if (r.TryGetProperty("map_zones", out var mz))
            foreach (var p in mz.EnumerateObject()) mapZones[int.Parse(p.Name)] = p.Value.GetString() ?? "?";

        string? Str(string name) => r.TryGetProperty(name, out var v) ? v.GetString() : null;
        double Num(string name, double fallback) => r.TryGetProperty(name, out var v) ? v.GetDouble() : fallback;
        var spots = Str("spots_csv");
        return new RadarConfig
        {
            Opcodes = opcodes,
            SelfIdOpcodes = selfIds,
            SelfCheck = checks,
            CubeTemplates = cubes,
            MapZones = mapZones,
            OpenDoneItem = openItem,
            North = Str("north") ?? "-y",
            FixFreshSeconds = Num("fix_fresh_s", 5),
            KeepOutOfViewMinutes = Num("keep_out_of_view_min", 30),
            KeepGoneSeconds = Num("keep_gone_s", 60),
            SpotsCsv = spots is null ? null : Path.GetFullPath(Path.Combine(projectRoot, spots)),
            SpotMatchUnits = Num("spot_match_units", 10),
            GoneCheckUnits = Num("gone_check_units", 8000),
            RingWindowMs = (int)Num("ring_window_ms", 2000),
            RingMinEvents = (int)Num("ring_min_events", 3),
        };
    }

    private static RecordKind ParseKind(string name) => name switch
    {
        "heartbeat" => RecordKind.Heartbeat,
        "self_info" => RecordKind.SelfInfo,
        "map_load" => RecordKind.MapLoad,
        "teleport" => RecordKind.Teleport,
        "object_spawn" => RecordKind.ObjectSpawn,
        "object_removed" => RecordKind.ObjectRemoved,
        "open_start" => RecordKind.OpenStart,
        "open_done" => RecordKind.OpenDone,
        "pos_buff_a" => RecordKind.PosBuffA,
        "pos_buff_b" => RecordKind.PosBuffB,
        "pos_cast" => RecordKind.PosCast,
        "pos_target" => RecordKind.PosTarget,
        _ => throw new FormatException($"radar.json: unknown record kind \"{name}\""),
    };
}
