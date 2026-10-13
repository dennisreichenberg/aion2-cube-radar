"""Object lifecycle per template from a frames.jsonl: spawn (34 36), open start/done (38 36 / 3a 36), removal (35 36).
Layouts are the Cube Watch hypotheses (see docs/FINDINGS.md); this tool is how we check them on our own captures.

usage: python tools/objects.py <frames.jsonl> [--template N] [--events]
"""
import argparse
import collections
import datetime
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from a2wire import load_jsonl, varint  # noqa: E402

CUBE_TEMPLATE = 0x0013DACF  # Cube Watch hypothesis


def plausible_ms(v, ref):
    return abs(v - ref) < 400 * 24 * 3600 * 1000


def parse_spawn(b, t):
    obj, o = varint(b, 0)
    kind = b[o:o + 2].hex()
    template = struct.unpack_from("<I", b, o + 2)[0]
    x, y, z = struct.unpack_from("<3f", b, o + 6)
    tail = b[o + 18:]
    ts = None
    for k in range(0, len(tail) - 7):  # first u64 after the position that reads as a Unix-ms time near the capture
        v = struct.unpack_from("<Q", tail, k)[0]
        if v and plausible_ms(v, t):
            ts = v
            break
    return obj, kind, template, (x, y, z), ts


def local(ms):
    return datetime.datetime.fromtimestamp(ms / 1000).strftime("%H:%M:%S.%f")[:-3]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jsonl")
    ap.add_argument("--template", type=int, help="list every event of this template")
    ap.add_argument("--events", action="store_true", help="list all open/remove events in time order")
    a = ap.parse_args()

    objs = {}       # id -> dict
    events = []     # (t, kind, obj, info)
    markers = []
    t0 = None
    for r in load_jsonl(a.jsonl, ("frame", "marker")):
        if r["type"] == "marker":
            markers.append(r)
            continue
        t0 = t0 or r["t"]
        op = r["op"]
        if op not in ("3436", "3536", "3836", "3a36"):
            continue
        b = bytes.fromhex(r["hex"])
        if op == "3436":
            obj, kind, tmpl, pos, ts = parse_spawn(b, r["t"])
            o = objs.setdefault(obj, {"template": tmpl, "kind": kind, "pos": pos, "ts": ts, "spawns": [], "opens": [],
                                      "dones": [], "removes": []})
            o["spawns"].append(r["t"])
            o["template"], o["pos"], o["ts"] = tmpl, pos, ts
            events.append((r["t"], "spawn", obj, f"tmpl={tmpl} kind={kind} xyz=({pos[0]:.0f}, {pos[1]:.0f}, {pos[2]:.0f})"
                                                 + (f" created {local(ts)} ({(r['t'] - ts) / 1000:.0f} s ago)" if ts else "")))
        else:
            obj, o2 = varint(b, 0)
            rest = b[o2:]
            if op == "3536":
                info = f"reason={rest[:1].hex()} rest={rest[1:].hex()}"
                key = "removes"
            else:
                actor, o3 = varint(b, o2)
                info = f"actor={actor} rest={b[o3:].hex()}"
                key = "opens" if op == "3836" else "dones"
            o = objs.setdefault(obj, {"template": None, "kind": None, "pos": None, "ts": None, "spawns": [], "opens": [],
                                      "dones": [], "removes": []})
            o[key].append((r["t"], info))
            events.append((r["t"], {"3536": "remove", "3836": "open", "3a36": "done"}[op], obj, info))

    by_t = collections.defaultdict(list)
    for oid, o in objs.items():
        by_t[o["template"]].append(oid)
    print(f"{len(objs)} objects, {len(markers)} markers")
    print("template      hex       objs  spawns opened done  rm:04 rm:01 rm:other  (unknown = spawned before capture)")
    for tmpl, ids in sorted(by_t.items(), key=lambda kv: (-sum(bool(objs[i]["dones"]) for i in kv[1]), -len(kv[1]))):
        os_ = [objs[i] for i in ids]
        rm = collections.Counter(info.split()[0] for o in os_ for _, info in o["removes"])
        name = "unknown" if tmpl is None else f"{tmpl:<9} 0x{tmpl:06x}"
        flag = "  <- Cube Watch cube template" if tmpl == CUBE_TEMPLATE else ""
        print(f"{name:<23} {len(ids):5} {sum(len(o['spawns']) for o in os_):6} {sum(bool(o['opens']) for o in os_):6} "
              f"{sum(bool(o['dones']) for o in os_):4} {rm['reason=04']:6} {rm['reason=01']:5} "
              f"{sum(rm.values()) - rm['reason=04'] - rm['reason=01']:8}{flag}")

    for m in markers:
        print(f"MARKER {local(m['t'])} +{(m['t'] - t0) / 1000:7.1f} s  {m['label']} {m.get('text', '')}")

    if a.template is not None or a.events:
        print()
        for t, kind, obj, info in sorted(events):
            tmpl = objs[obj]["template"]
            if a.template is not None and tmpl != a.template:
                continue
            print(f"{local(t)} +{(t - t0) / 1000:7.1f} s  {kind:<6} obj={obj:<7} tmpl={tmpl}  {info}")
        for m in markers:
            print(f"{local(m['t'])} +{(m['t'] - t0) / 1000:7.1f} s  MARKER {m['label']}")


if __name__ == "__main__":
    main()
