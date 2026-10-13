"""Own-position track from all server->client sources found so far, with plausibility statistics.

Sources (own entity id = most frequent id matching the 2a 38 rule, or --self):
  2a38  body ends with: caster varint, 01 00, x y z f32 -- the caster's position  (verified 2026-10-13, B7)
  2b38  body ends with: caster varint, u8, u32, 02, x y z f32 -- the caster's position (2026-10-13)
  0238  cast, kind 2: target varint, heading f32, x y z f32 = the TARGET's position (own position when we are the
        target: self-casts and mobs attacking us); some skills carry a small vector instead (rejected)
  0e38  entity varint, 02, own id varint, u32, u8, u32, x y z f32 (own position as the target)
  2136  map load: count u32, map u32, instance u32, u32, x y z f32 (TheLoop encoder; unverified)

usage: python tools/selftrack.py <frames.jsonl> [--self ID] [--csv out.csv] [--svg out.svg]
"""
import argparse
import math
import os
import statistics
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from a2wire import load_jsonl, varint  # noqa: E402
from radius import self_fixes  # noqa: E402

MAP_LIMIT = 420_000.0


def world(x, y, z):
    return (all(math.isfinite(v) for v in (x, y, z)) and abs(x) < MAP_LIMIT and abs(y) < MAP_LIMIT and abs(z) < 120_000
            and math.hypot(x, y) > 2_000)


def caster_position(b, op):
    """(caster id, x y z) from the tail of a 2a 38 / 2b 38 record, or None."""
    gap = 2 if op == "2a38" else 6  # bytes between the caster varint and x y z
    if len(b) < 12 + gap + 2:
        return None
    if op == "2a38" and b[-14:-12] != b"\x01\x00":
        return None
    if op == "2b38" and b[-13] != 0x02:
        return None
    end = len(b) - 12 - gap
    for w in (1, 2, 3):
        start = end - w
        if start < 1 or b[start - 1] & 0x80:  # the byte before would continue into this varint: not its start
            continue
        v = varint(b, start)
        if v and v[1] == end:
            x, y, z = struct.unpack_from("<3f", b, len(b) - 12)
            return (v[0], (x, y, z)) if world(x, y, z) else None
    return None


def position_fix(op, b):
    """(entity id, (x, y, z)) named by one record, or None. Entity None = the record has no entity (map load)."""
    if not b:
        return None
    if op in ("2a38", "2b38"):
        return caster_position(b, op)
    if op == "0238":  # x y z is the TARGET's position (2026-10-13: median 0 units from the target, 160-307 from the caster)
        _, o = varint(b, 0)
        v = varint(b, o)
        if not v or v[1] + 6 > len(b):
            return None
        o = v[1] + 4 + 1  # skill u32, seq u8
        if b[o] != 2:
            return None
        tg = varint(b, o + 1)
        if not tg or tg[1] + 16 > len(b):
            return None
        x, y, z = struct.unpack_from("<3f", b, tg[1] + 4)
        return (tg[0], (x, y, z)) if world(x, y, z) else None
    if op == "0e38":
        _, o = varint(b, 0)
        if o >= len(b) or b[o] != 2:
            return None
        tgt, o2 = varint(b, o + 1)
        if o2 + 21 > len(b):
            return None
        x, y, z = struct.unpack_from("<3f", b, o2 + 9)
        return (tgt, (x, y, z)) if world(x, y, z) else None
    if op == "2136" and len(b) >= 28:
        x, y, z = struct.unpack_from("<3f", b, 16)
        return (None, (x, y, z)) if world(x, y, z) else None
    return None


def extract(rows, sid):
    pts = []  # (t, source, (x, y, z))
    for r in rows:
        op = r["op"]
        if op not in ("2a38", "2b38", "0238", "0e38", "2136"):
            continue
        got = position_fix(op, bytes.fromhex(r["hex"]))
        if got and got[0] in (sid, None):
            pts.append((r["t"], op, got[1]))
    pts.sort(key=lambda p: p[0])
    return pts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jsonl")
    ap.add_argument("--self", type=int, dest="self_id")
    ap.add_argument("--csv")
    ap.add_argument("--svg")
    a = ap.parse_args()
    rows = list(load_jsonl(a.jsonl, ("frame", "marker")))
    frames = [r for r in rows if r["type"] == "frame"]
    _, sid, _ = self_fixes(frames, a.self_id)
    pts = extract(frames, sid)
    t0, t1 = frames[0]["t"], frames[-1]["t"]
    print(f"own id {sid}; {len(pts)} fixes over {(t1 - t0) / 1000:.0f} s")
    by = {}
    for _, s, _ in pts:
        by[s] = by.get(s, 0) + 1
    print("  by source:", by)
    if len(pts) < 2:
        return

    gaps = [(b[0] - a_[0]) / 1000 for a_, b in zip(pts, pts[1:])]
    speeds = []
    jumps = []
    for p, q in zip(pts, pts[1:]):
        dt = (q[0] - p[0]) / 1000
        d = math.dist(p[2][:2], q[2][:2])
        if dt >= 0.3:
            speeds.append(d / dt / 100)  # m/s at 1 unit = 1 cm
            if d / dt / 100 > 40:
                jumps.append((p, q, d, dt))
    print(f"  gaps: median {statistics.median(gaps):.1f} s, 90% {sorted(gaps)[int(len(gaps) * 0.9)]:.1f} s, "
          f"max {max(gaps):.1f} s; time without a fix for > 10 s: {sum(g for g in gaps if g > 10):.0f} s")
    print(f"  implied speed between fixes: median {statistics.median(speeds):.1f} m/s, 90% {sorted(speeds)[int(len(speeds) * 0.9)]:.1f} m/s,"
          f" max {max(speeds):.1f} m/s")
    for p, q, d, dt in jumps[:10]:
        print(f"  JUMP +{(p[0] - t0) / 1000:.1f}s {p[1]} -> +{(q[0] - t0) / 1000:.1f}s {q[1]}: {d / 100:.0f} m in {dt:.1f} s")

    # same-instant agreement between sources (within 1 s)
    pairs = []
    for i, p in enumerate(pts):
        for q in pts[i + 1:]:
            if q[0] - p[0] > 1000:
                break
            if q[1] != p[1]:
                pairs.append((p[1], q[1], math.dist(p[2][:2], q[2][:2])))
    if pairs:
        print("  cross-source agreement within 1 s (planar units):")
        agg = {}
        for s1, s2, d in pairs:
            agg.setdefault(tuple(sorted((s1, s2))), []).append(d)
        for k, v in agg.items():
            print(f"    {k[0]} vs {k[1]}: n={len(v)} median {statistics.median(v):.0f} max {max(v):.0f}")

    if a.csv:
        with open(a.csv, "w", encoding="utf-8") as f:
            f.write("t_ms,source,x,y,z\n")
            for t, s, (x, y, z) in pts:
                f.write(f"{t},{s},{x:.1f},{y:.1f},{z:.1f}\n")
        print(f"  csv: {a.csv}")
    if a.svg:
        write_svg(a.svg, pts, [r for r in rows if r["type"] == "marker"], frames)
        print(f"  svg: {a.svg}")


def write_svg(path, pts, markers, frames):
    """Track (map north = -y up, east = +x right, per Cube Watch) plus cube spawns from 34 36 (template 1200039)."""
    cubes = []
    for r in frames:
        if r["op"] == "3436":
            b = bytes.fromhex(r["hex"])
            _, o = varint(b, 0)
            if struct.unpack_from("<I", b, o + 2)[0] in (1200039, 1301199):
                cubes.append(struct.unpack_from("<3f", b, o + 6))
    xs = [p[2][0] for p in pts] + [c[0] for c in cubes]
    ys = [p[2][1] for p in pts] + [c[1] for c in cubes]
    pad = 2000
    x0, x1, y0, y1 = min(xs) - pad, max(xs) + pad, min(ys) - pad, max(ys) + pad
    w = 800
    h = max(200, int(w * (y1 - y0) / max(1, x1 - x0)))
    sx = lambda x: (x - x0) / (x1 - x0) * w  # noqa: E731
    sy = lambda y: (y - y0) / (y1 - y0) * h  # noqa: E731
    colors = {"2a38": "#2a7", "2b38": "#27a", "0238": "#a72", "0e38": "#a27", "2136": "#000"}
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" style="background:#fff;font:11px sans-serif">']
    out.append('<polyline fill="none" stroke="#999" stroke-width="1" points="'
               + " ".join(f"{sx(p[2][0]):.1f},{sy(p[2][1]):.1f}" for p in pts) + '"/>')
    for t, s, (x, y, _) in pts:
        out.append(f'<circle cx="{sx(x):.1f}" cy="{sy(y):.1f}" r="2.5" fill="{colors.get(s, "#000")}"/>')
    for x, y, _ in cubes:
        out.append(f'<rect x="{sx(x) - 5:.1f}" y="{sy(y) - 5:.1f}" width="10" height="10" fill="none" stroke="#d00" stroke-width="2"/>')
    out.append(f'<text x="5" y="14">{len(pts)} own fixes (green 2a38, blue 2b38, orange 02 38, purple 0e 38); red squares = cubes; '
               f'{(x1 - x0) / 100:.0f} m wide</text>')
    out.append("</svg>")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(out))


if __name__ == "__main__":
    main()
