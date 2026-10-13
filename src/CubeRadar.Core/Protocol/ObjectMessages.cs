using System.Buffers.Binary;

namespace CubeRadar.Core.Protocol;

/// <summary>World object (gadget) spawn, e.g. a Hidden Cube. <see cref="CreatedMs"/> is a Unix-ms time that reads as the
/// object's creation (hypothesis, see docs/FINDINGS.md); 0 when absent.</summary>
public readonly record struct ObjectSpawn(uint Id, ushort Kind, uint Template, float X, float Y, float Z, float Heading,
    byte State, long CreatedMs);

public readonly record struct ObjectRemoved(uint Id, byte Reason);

/// <summary>A player starts (<see cref="Done"/> = false) or finishes opening an object. On a finished Hidden Cube the
/// tail reads count 1 + item 0x259AC523 (presumably the consumed key).</summary>
public readonly record struct ObjectInteraction(uint ObjectId, uint ActorId, bool Done, uint Count, uint Item);

public readonly record struct EntityPosition(uint EntityId, float X, float Y, float Z);

/// <summary>
/// Object lifecycle and own-position records, verified on Global build 25800038 (2026-10-13):
/// <code>
/// 34 36  id varint, kind u16, template u32, x y z f32, f32, heading f32, u8 u8 state u8, created u64 ms, …
/// 35 36  id varint, reason u8 (04 = destroyed / opened, 01 = left view), …
/// 38 36  object varint, actor varint, 01
/// 3a 36  object varint, actor varint, count u32, item u32
/// 2a 38  entity varint, …, entity varint (repeated), 01 00, x y z f32   (own position when entity = own id)
/// </code>
/// </summary>
public static class ObjectMessages
{
    public const ushort Spawn = 0x3436, Removed = 0x3536, OpenStart = 0x3836, OpenDone = 0x3A36, PositionRecord = 0x2A38;
    public const byte ReasonDestroyed = 0x04, ReasonLeftView = 0x01;

    public static bool TryParseSpawn(ReadOnlySpan<byte> b, out ObjectSpawn spawn)
    {
        spawn = default;
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var id) || o + 26 > b.Length) return false;
        var kind = Wire.U16(b, o);
        var template = Wire.U32(b, o + 2);
        var x = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 6)..]);
        var y = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 10)..]);
        var z = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 14)..]);
        var heading = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 22)..]);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        byte state = 0;
        long created = 0;
        if (o + 37 <= b.Length)
        {
            state = b[o + 28];
            created = BinaryPrimitives.ReadInt64LittleEndian(b[(o + 29)..]);
            if (created is < 1_600_000_000_000 or > 2_600_000_000_000) created = 0;
        }
        spawn = new ObjectSpawn(id, kind, template, x, y, z, heading, state, created);
        return true;
    }

    public static bool TryParseRemoved(ReadOnlySpan<byte> b, out ObjectRemoved removed)
    {
        removed = default;
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var id) || o >= b.Length) return false;
        removed = new ObjectRemoved(id, b[o]);
        return true;
    }

    public static bool TryParseInteraction(ReadOnlySpan<byte> b, bool done, out ObjectInteraction interaction)
    {
        interaction = default;
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var obj) || !Wire.TryVarint(b, ref o, out var actor)) return false;
        uint count = 0, item = 0;
        if (done && o + 8 <= b.Length)
        {
            count = Wire.U32(b, o);
            item = Wire.U32(b, o + 4);
        }
        interaction = new ObjectInteraction(obj, actor, done, count, item);
        return true;
    }

    /// <summary><c>2a 38</c> whose tail repeats the leading entity id followed by <c>01 00</c> and x y z.</summary>
    public static bool TryParsePositionRecord(ReadOnlySpan<byte> b, out EntityPosition position)
    {
        position = default;
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var id)) return false;
        var tail = b.Length - 14 - o;
        if (tail < o || !b[tail..(tail + o)].SequenceEqual(b[..o]) || b[b.Length - 14] != 0x01 || b[b.Length - 13] != 0x00)
            return false;
        var x = BinaryPrimitives.ReadSingleLittleEndian(b[^12..]);
        var y = BinaryPrimitives.ReadSingleLittleEndian(b[^8..]);
        var z = BinaryPrimitives.ReadSingleLittleEndian(b[^4..]);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        position = new EntityPosition(id, x, y, z);
        return true;
    }
}
