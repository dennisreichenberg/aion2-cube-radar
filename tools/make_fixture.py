"""Cuts a slice of a real server->client stream into a test fixture (fixtures/<name>.json): the raw stream bytes,
starting and ending on frame boundaries, plus the frames the independent Python decoder finds in them. The C# tests
decode the same bytes and must get the same frames.

usage: python tools/make_fixture.py <raw.pcapng> <name> --from-ms T0 --to-ms T1 [--flow-port PORT] [--note TEXT]
       (T0/T1 are offsets in ms from the first byte of the stream)
       --strings  only list printable strings in the slice (check for names / chat before committing)
"""
import argparse
import datetime
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from a2wire import Decoder, server_streams  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("capture")
    ap.add_argument("name")
    ap.add_argument("--from-ms", type=int, required=True)
    ap.add_argument("--to-ms", type=int, required=True)
    ap.add_argument("--client", default="")
    ap.add_argument("--note", default="")
    ap.add_argument("--strings", action="store_true")
    a = ap.parse_args()

    (key, st), = server_streams(a.capture).items()
    frames = Decoder().decode_stream(bytes(st.data), aligned=st.syn_seen)
    t_first = st.times[0][1]

    def t_of(offset):
        t = t_first
        for off, tu in st.times:
            if off > offset:
                break
            t = tu
        return (t - t_first) // 1000

    tops = [f for f in frames if f.src == "top"]
    start = next(f for f in tops if t_of(f.offset) >= a.from_ms and f.op == "0036")  # start on a heartbeat
    end_frame = [f for f in tops if t_of(f.offset) <= a.to_ms][-1]
    lo, hi = start.offset, end_frame.offset + end_frame.wire
    chunk = bytes(st.data[lo:hi])

    strings = sorted({m.group().decode() for m in re.finditer(rb"[\x20-\x7e]{5,}", chunk)})
    if a.strings:
        print(f"{len(chunk)} bytes, printable strings: {strings}")
        return

    sub = Decoder().decode_stream(chunk, aligned=True)
    fixture = {
        "description": a.note,
        "capture": os.path.relpath(a.capture).replace("\\", "/"),
        "captured_utc": datetime.datetime.fromtimestamp((t_first // 1000 + a.from_ms) / 1000, datetime.UTC).isoformat(timespec="seconds"),
        "client": a.client,
        "server": f"{key[0]}:{key[1]}",
        "stream_offset": lo,
        "stream_hex": chunk.hex(),
        "frames": [{"op": f.op, "src": f.src, "depth": f.depth, "path": f.path, "flag": f.flag, "wire": f.wire,
                    "hex": f.body.hex()} for f in sub],
    }
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures", a.name + ".json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(fixture, f, indent=1)
        f.write("\n")
    ops = {}
    for f in sub:
        ops[f.op + "/" + f.src] = ops.get(f.op + "/" + f.src, 0) + 1
    print(f"{out}: {len(chunk)} bytes, {len(sub)} frames {ops}")


if __name__ == "__main__":
    main()
