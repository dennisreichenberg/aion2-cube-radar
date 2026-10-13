"""Which server->client records carry the OWN position? A generic f32-triplet scan over every frame (any opcode, any
offset, top level and bundles), compared with the reference track from the verified `2a 38` position records.

For each (opcode, offset) the scan counts frames where x y z f32 at that offset lie within --tol units (planar) of the
reference position at that time (nearest reference fix within --max-gap ms). Offsets are reported from the start
and from the end of the body, since variable-length fields shift one or the other. It also reports, per opcode, how
often the first varint is the own entity id.

usage: python tools/selfscan.py <frames.jsonl> [more.jsonl ...] [--tol 1500] [--max-gap 2000] [--self ID]
"""
import argparse
import bisect
import collections
import math
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from a2wire import load_jsonl, varint  # noqa: E402
from radius import self_fixes  # noqa: E402

MAP_LIMIT = 420_000.0


def plausible(x, y, z):
    return (all(math.isfinite(v) for v in (x, y, z)) and abs(x) < MAP_LIMIT and abs(y) < MAP_LIMIT and abs(z) < 120_000
            and (abs(x) > 50 or abs(y) > 50))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jsonl", nargs="+")
    ap.add_argument("--tol", type=float, default=1500)
    ap.add_argument("--max-gap", type=int, default=2000)
    ap.add_argument("--self", type=int, dest="self_id")
    ap.add_argument("--top", type=int, default=25)
    a = ap.parse_args()

    hits = collections.Counter()        # (op, "s+k" | "e-k") -> frames with own position there
    near_other = collections.Counter()  # (op, offset) -> plausible triplets far from us (other entities)
    frames_per_op = collections.Counter()
    first_is_self = collections.Counter()
    tested = collections.Counter()      # frames per op that had a reference fix in reach
    for path in a.jsonl:
        rows = list(load_jsonl(path, ("frame",)))
        fixes, sid, _ = self_fixes(rows, a.self_id)
        if not fixes:
            print(f"{path}: no reference fixes, skipped")
            continue
        ft = [f[0] for f in fixes]
        sid_bytes = None
        print(f"{path}: own id {sid}, {len(fixes)} reference fixes")
        for r in rows:
            op = r["op"]
            if op == "0036":
                continue
            b = bytes.fromhex(r["hex"])
            frames_per_op[op] += 1
            v = varint(b, 0) if b else None
            if v and v[0] == sid:
                first_is_self[op] += 1
            k = bisect.bisect_left(ft, r["t"])
            cands = [i for i in (k - 1, k) if 0 <= i < len(fixes)]
            if not cands:
                continue
            j = min(cands, key=lambda i: abs(ft[i] - r["t"]))
            if abs(ft[j] - r["t"]) > a.max_gap:
                continue
            ref = fixes[j][1]
            tested[op] += 1
            seen = set()
            for o in range(0, len(b) - 11):
                x, y, z = struct.unpack_from("<3f", b, o)
                if not plausible(x, y, z):
                    continue
                key_s, key_e = (op, f"s+{o}"), (op, f"e-{len(b) - o}")
                if math.dist((x, y), ref[:2]) <= a.tol:
                    if key_s not in seen:
                        hits[key_s] += 1
                        hits[key_e] += 1
                        seen.add(key_s)
                else:
                    near_other[key_s] += 1

    print(f"\nown-position triplets (planar distance <= {a.tol:.0f} to the reference), best (opcode, offset):")
    print("  op    offset   hits / frames with reference   first varint = own id")
    shown = set()
    for (op, off), n in hits.most_common():
        if n < 3:
            break
        if (op, off[0]) in shown:
            continue  # one line per opcode and anchor (start / end)
        shown.add((op, off[0]))
        print(f"  {op}  {off:>7}   {n:5} / {tested[op]:<5} ({n / max(1, tested[op]):5.1%})        "
              f"{first_is_self[op]:5} / {frames_per_op[op]}")
        if len(shown) >= a.top:
            break
    print("\nopcodes whose first varint is the own id (any content):")
    for op, n in first_is_self.most_common(a.top):
        print(f"  {op}  {n:5} / {frames_per_op[op]:<6}")


if __name__ == "__main__":
    main()
