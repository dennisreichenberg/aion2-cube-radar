"""Opcode census and marker list of a frames.jsonl written by `cuberadar dump`.

usage: python tools/census.py <frames.jsonl> [--top 40]
"""
import argparse
import collections
import datetime
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from a2wire import load_jsonl  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jsonl")
    ap.add_argument("--top", type=int, default=40)
    a = ap.parse_args()

    ops = collections.Counter()
    srcs = collections.defaultdict(collections.Counter)
    names = {}
    markers, gaps, bundles = [], 0, collections.Counter()
    first = last = None
    hb = 0
    for r in load_jsonl(a.jsonl):
        t = r.get("type")
        if t == "frame":
            ops[r["op"]] += 1
            srcs[r["op"]][r["src"]] += 1
            names[r["op"]] = r.get("name", "")
            first = r["t"] if first is None else first
            last = r["t"]
            hb += r["op"] == "0036"
        elif t == "marker":
            markers.append(r)
        elif t == "gap":
            gaps += 1
        elif t == "bundle":
            bundles["ok" if r["ok"] else "bad"] += 1
    if first is None:
        print("no frames")
        return
    secs = max(0.001, (last - first) / 1000)
    print(f"{sum(ops.values()):,} frames over {secs:.1f} s, {len(ops)} opcodes, heartbeats {hb / secs:.2f}/s, "
          f"bundles {dict(bundles)}, gaps {gaps}")
    for op, n in ops.most_common(a.top):
        s = srcs[op]
        print(f"  {op}  {names[op]:<16} {n:8,} {n / secs:7.2f}/s   top {s['top']:6} lz4 {s['lz4']:6} emb {s['emb']:6}")
    print(f"markers: {len(markers)}")
    for m in markers:
        when = datetime.datetime.fromtimestamp(m["t"] / 1000).strftime("%H:%M:%S.%f")[:-3]
        print(f"  {when}  +{(m['t'] - first) / 1000:8.1f} s  {m['label']:<18} {m.get('text', '')}")


if __name__ == "__main__":
    main()
