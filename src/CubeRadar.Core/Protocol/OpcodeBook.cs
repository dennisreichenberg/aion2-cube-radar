using System.Text.Json;

namespace CubeRadar.Core.Protocol;

public sealed record OpcodeInfo(string Name, string Status, string? Source, string? Note);

/// <summary>
/// Opcode names from <c>config/opcodes.json</c> (wire byte order, e.g. "4136"). Patches move opcodes, so names live in
/// the config, not in code. Unknown opcodes are shown as their two bytes.
/// </summary>
public sealed class OpcodeBook
{
    private readonly Dictionary<ushort, OpcodeInfo> _byOpcode;

    private OpcodeBook(Dictionary<ushort, OpcodeInfo> byOpcode) => _byOpcode = byOpcode;

    public static OpcodeBook Empty { get; } = new(new Dictionary<ushort, OpcodeInfo>());

    public IReadOnlyDictionary<ushort, OpcodeInfo> All => _byOpcode;

    public static OpcodeBook Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var map = new Dictionary<ushort, OpcodeInfo>();
        foreach (var p in doc.RootElement.GetProperty("opcodes").EnumerateObject())
        {
            if (!TryParse(p.Name, out var op)) throw new FormatException($"{path}: bad opcode key \"{p.Name}\"");
            var v = p.Value;
            map[op] = new OpcodeInfo(
                v.GetProperty("name").GetString() ?? Hex(op),
                v.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                v.TryGetProperty("source", out var src) ? src.GetString() : null,
                v.TryGetProperty("note", out var n) ? n.GetString() : null);
        }
        return new OpcodeBook(map);
    }

    /// <summary>Looks for <c>config/opcodes.json</c> next to the executable, then upwards from the working directory.</summary>
    public static OpcodeBook LoadDefault()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "config", "opcodes.json");
                if (File.Exists(candidate)) return Load(candidate);
            }
        }
        return Empty;
    }

    public string Name(ushort op) => _byOpcode.TryGetValue(op, out var i) ? i.Name : Hex(op);

    public static string Hex(ushort op) => $"{op >> 8:x2}{op & 0xFF:x2}";

    public static bool TryParse(string text, out ushort op)
    {
        op = 0;
        var t = text.Replace(" ", "");
        if (t.Length != 4) return false;
        return ushort.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out op);
    }
}
