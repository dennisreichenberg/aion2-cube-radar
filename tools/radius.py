"""Own position and view (interest) radius from a frames.jsonl.

Own position: `2a 38` records whose tail repeats the leading entity id, followed by `01 00` and x y z f32 (rule from
Cube Watch; checked on 2026-10-13: at both cube openings the fix was 195 / 234 units from the cube). The own id is the
id that matches this rule most often (override with --self).

Radius: for every object spawn (34 36) and every out-of-view removal (35 36 reason 01), the planar distance to the
nearest own-position fix within --max-gap ms. Enter distances bound the send radius from below, leave distances from
above (plus hysteresis); position fixes lag, so expect ± a few thousand units at riding/gliding speed.

usage: python tools/radius.py <frames.jsonl> [--self ID] [--max-gap 1500] [--cubes 1200039,1301199]
"""
import argparse
import bisect
import collections
import math
import os
import statistics
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from a2wire import load_jsonl, varint  # noqa: E402


def self_fixes(rows, self_id=None):
    """[(t, (x, y, z))] for the own entity, and the id used."""
    cand = collections.defaultdict(list)
    for r in rows:
        if r["op"] != "2a38":
            continue
        b = bytes.fromhex(r["hex"])
        if len(b) < 16:
            continue
        eid, w = varint(b, 0)
        if b[len(b) - 14 - w:len(b) - 14] == b[:w] and b[len(b) - 14:len(b) - 12] == b"\x01\x00":
            cand[eid].append((r["t"], struct.unpack_from("<3f", b, len(b) - 12)))
    if not cand:
        return [], None, {}
    sid = self_id if self_id is not None else max(cand, key=lambda k: len(cand[k]))
    return cand.get(sid, []), sid, {k: len(v) for k, v in cand.items()}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jsonl")
    ap.add_argument("--self", type=int, dest="self_id")
    ap.add_argument("--max-gap", type=int, default=1500)
    ap.add_argument("--cubes", default="1200039,1301199")
    a = ap.parse_args()
    cubes = {int(x) for x in a.cubes.split(",") if x}

    rows = list(load_jsonl(a.jsonl, ("frame",)))
    fixes, sid, cand = self_fixes(rows, a.self_id)
    print(f"own id {sid} ({len(fixes)} fixes); ids matching the self-position rule: {cand}")
    if not fixes:
        return
    ft = [f[0] for f in fixes]

    def near(t):
        k = bisect.bisect_left(ft, t)
        j = min((i for i in (k - 1, k) if 0 <= i < len(fixes)), key=lambda i: abs(ft[i] - t))
        return fixes[j] if abs(ft[j] - t) <= a.max_gap else None

    def around(t):
        """fixes just before and after t (for interpolation / bounds)."""
        k = bisect.bisect_left(ft, t)
        return (fixes[k - 1] if k > 0 else None), (fixes[k] if k < len(fixes) else None)

    objs = {}
    per = collections.defaultdict(lambda: {"enter": [], "leave": []})
    cube_lines = []
    for r in rows:
        op = r["op"]
        if op not in ("3436", "3536"):
            continue
        b = bytes.fromhex(r["hex"])
        oid, o = varint(b, 0)
        if op == "3436":
            tmpl = struct.unpack_from("<I", b, o + 2)[0]
            p = struct.unpack_from("<3f", b, o + 6)
            objs[oid] = (tmpl, p)
            f = near(r["t"])
            if f:
                per[tmpl]["enter"].append(math.dist(p[:2], f[1][:2]))
            if tmpl in cubes:
                before, after = around(r["t"])
                parts = []
                for name, fx in (("before", before), ("after", after)):
                    if fx:
                        parts.append(f"{name} {(fx[0] - r['t']) / 1000:+.1f}s d2={math.dist(p[:2], fx[1][:2]):.0f}")
                if before and after and after[0] > before[0]:
                    k = (r["t"] - before[0]) / (after[0] - before[0])
                    me = [before[1][i] + k * (after[1][i] - before[1][i]) for i in range(3)]
                    parts.append(f"interpolated d2={math.dist(p[:2], me[:2]):.0f} d3={math.dist(p, me):.0f}")
                cube_lines.append(f"  cube {oid} tmpl {tmpl} at ({p[0]:.0f}, {p[1]:.0f}, {p[2]:.0f}): " + " | ".join(parts))
        elif b[o] == 1 and oid in objs:
            f = near(r["t"])
            if f:
                per[objs[oid][0]]["leave"].append(math.dist(objs[oid][1][:2], f[1][:2]))

    print("cube sightings (first 34 36):")
    print("\n".join(cube_lines) or "  none")
    print(f"planar distance (units) to own position within {a.max_gap} ms, per template:")
    for tmpl, d in sorted(per.items(), key=lambda kv: -(len(kv[1]["enter"]) + len(kv[1]["leave"]))):
        en, lv = sorted(d["enter"]), sorted(d["leave"])
        med = lambda v: f"{statistics.median(v):6.0f}" if v else "     -"
        mark = "  <- cube" if tmpl in cubes else ""
        print(f"  {tmpl:<8} enter n={len(en):2} median {med(en)} [{', '.join(f'{x:.0f}' for x in en)}]"
              f"   leave n={len(lv):2} median {med(lv)} [{', '.join(f'{x:.0f}' for x in lv)}]{mark}")


if __name__ == "__main__":
    main()
