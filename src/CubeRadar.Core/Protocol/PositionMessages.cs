using System.Buffers.Binary;

namespace CubeRadar.Core.Protocol;

/// <summary>A world position of one entity taken from a record that names it.</summary>
public readonly record struct PositionFix(uint EntityId, float X, float Y, float Z, ushort Opcode);

/// <summary>
/// Server→client records that carry an entity's world position. The server never echoes the own movement; the own
/// position only arrives with these events (verified on Global build 25800038, 2026-10-13, docs/FINDINGS.md P1–P3):
/// <code>
/// 2a 38  …, caster varint, 01 00, x y z f32                      (buff record, position of the caster)
/// 2b 38  …, caster varint, u8, u32, 02, x y z f32                (buff record, position of the caster)
/// 02 38  actor varint, flag varint, skill u32, seq u8, kind u8 = 2, target varint, heading f32, x y z f32
///        (position of the TARGET; some skills carry a small vector instead, rejected by the plausibility check)
/// 0e 38  entity varint, 02, target varint, u32, u8, u32, x y z f32, … (position of the target)
/// </code>
/// </summary>
public static class PositionMessages
{
    public const ushort RecordA = 0x2A38, RecordB = 0x2B38, Cast = 0x0238, Targeted = 0x0E38;

    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> b, out PositionFix fix)
    {
        fix = default;
        switch (opcode)
        {
            case RecordA:
                return b.Length >= 16 && b[^14] == 0x01 && b[^13] == 0x00 && TryCasterTail(b, opcode, gap: 2, out fix);
            case RecordB:
                return b.Length >= 20 && b[^13] == 0x02 && TryCasterTail(b, opcode, gap: 6, out fix);
            case Cast:
            {
                var o = 0;
                if (!Wire.TryVarint(b, ref o, out _) || !Wire.TryVarint(b, ref o, out _)) return false;
                o += 5; // skill u32, seq u8
                if (o >= b.Length || b[o++] != 2 || !Wire.TryVarint(b, ref o, out var target)) return false;
                return TryWorld(b, o + 4, target, opcode, out fix); // after the heading
            }
            case Targeted:
            {
                var o = 0;
                if (!Wire.TryVarint(b, ref o, out _) || o >= b.Length || b[o++] != 2) return false;
                if (!Wire.TryVarint(b, ref o, out var target)) return false;
                return TryWorld(b, o + 9, target, opcode, out fix);
            }
            default:
                return false;
        }
    }

    /// <summary>The caster varint ends <paramref name="gap"/> bytes before the trailing x y z. A varint start is only
    /// accepted where the byte before it has no continuation bit (else it would belong to the varint).</summary>
    private static bool TryCasterTail(ReadOnlySpan<byte> b, ushort opcode, int gap, out PositionFix fix)
    {
        fix = default;
        var end = b.Length - 12 - gap;
        for (var w = 1; w <= 3; w++)
        {
            var start = end - w;
            if (start < 1 || (b[start - 1] & 0x80) != 0) continue;
            var o = start;
            if (Wire.TryVarint(b, ref o, out var caster) && o == end)
                return TryWorld(b, b.Length - 12, caster, opcode, out fix);
        }
        return false;
    }

    private static bool TryWorld(ReadOnlySpan<byte> b, int at, uint entity, ushort opcode, out PositionFix fix)
    {
        fix = default;
        if (at < 0 || at + 12 > b.Length) return false;
        var x = BinaryPrimitives.ReadSingleLittleEndian(b[at..]);
        var y = BinaryPrimitives.ReadSingleLittleEndian(b[(at + 4)..]);
        var z = BinaryPrimitives.ReadSingleLittleEndian(b[(at + 8)..]);
        if (!IsWorldPosition(x, y, z)) return false;
        fix = new PositionFix(entity, x, y, z, opcode);
        return true;
    }

    /// <summary>Finite, inside the map bounds (±420,000), and not a small vector near the origin.</summary>
    public static bool IsWorldPosition(float x, float y, float z) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) &&
        MathF.Abs(x) < 420_000 && MathF.Abs(y) < 420_000 && MathF.Abs(z) < 120_000 &&
        MathF.Sqrt(x * x + y * y) > 2_000;
}
