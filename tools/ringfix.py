"""Fallback own-position estimate from objects entering / leaving view ("ring fix") and its error against real fixes.

Objects (34 36) are sent when they come within a template-specific planar radius and removed (35 36 reason 01) when
they leave a slightly larger one. So every enter/leave event says: the player stands on a circle of that radius around
the object. Several events within --window ms are combined by least squares (grid search, 250-unit steps). Radii per
(template, enter/leave) are learned as medians from events that have a real fix within 1 s (in-sample: needs real
fixes from the same session or a radii table later).

usage: python tools/ringfix.py <frames.jsonl> [--window 2000] [--min-events 3]
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
from radius import self_fixes  # noqa: E402
from selftrack import extract  # noqa: E402


def events(rows):
    objs, ev = {}, []
    for r in rows:
        if r["op"] not in ("3436", "3536"):
            continue
        b = bytes.fromhex(r["hex"])
        oid, o = varint(b, 0)
        if r["op"] == "3436":
            tmpl = struct.unpack_from("<I", b, o + 2)[0]
            p = struct.unpack_from("<3f", b, o + 6)
            objs[oid] = (tmpl, p)
            ev.append((r["t"], "in", tmpl, p))
        elif b[o] == 1 and oid in objs:
            ev.append((r["t"], "out", *objs[oid]))
    return ev


def solve(usable, step=250, span=16000):
    cx = statistics.mean(p[0] for p, _ in usable)
    cy = statistics.mean(p[1] for p, _ in usable)
    best = None
    for gx in range(-span, span + 1, step):
        for gy in range(-span, span + 1, step):
            x, y = cx + gx, cy + gy
            err = sum((math.hypot(x - p[0], y - p[1]) - r) ** 2 for p, r in usable)
            if best is None or err < best[0]:
                best = (err, x, y)
    return best[1], best[2]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jsonl")
    ap.add_argument("--window", type=int, default=2000)
    ap.add_argument("--min-events", type=int, default=3)
    a = ap.parse_args()
    rows = list(load_jsonl(a.jsonl, ("frame",)))
    _, sid, _ = self_fixes(rows)
    pts = extract(rows, sid)
    pt = [p[0] for p in pts]

    def fix(t, gap):
        k = bisect.bisect_left(pt, t)
        c = [i for i in (k - 1, k) if 0 <= i < len(pts)]
        if not c:
            return None
        j = min(c, key=lambda i: abs(pt[i] - t))
        return pts[j][2] if abs(pt[j] - t) <= gap else None

    ev = events(rows)
    learned = collections.defaultdict(list)
    for t, kind, tmpl, p in ev:
        q = fix(t, 1000)
        if q:
            learned[(tmpl, kind)].append(math.hypot(p[0] - q[0], p[1] - q[1]))
    radii = {k: statistics.median(v) for k, v in learned.items() if len(v) >= 2}
    print(f"own id {sid}, {len(pts)} real fixes, {len(ev)} enter/leave events, radii for {len(radii)} (template, in/out)")

    errs, covered = [], 0
    for t, *_ in ev:
        win = [(p, radii[(tm, k)]) for t2, k, tm, p in ev if abs(t2 - t) <= a.window and (tm, k) in radii]
        if len(win) < a.min_events:
            continue
        covered += 1
        q = fix(t, 500)
        if q:
            x, y = solve(win)
            errs.append(math.hypot(x - q[0], y - q[1]))
    print(f"ring estimates possible at {covered} of {len(ev)} events; checked against a real fix within 0.5 s: n={len(errs)}")
    if errs:
        e = sorted(errs)
        print(f"  error (planar): median {statistics.median(e):.0f} units, 90% {e[int(len(e) * 0.9)]:.0f}, max {e[-1]:.0f}")


if __name__ == "__main__":
    main()
