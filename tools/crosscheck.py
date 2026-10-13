"""Decodes a capture with the independent Python decoder (a2wire) and compares every frame with the frames.jsonl the
C# dumper wrote for the same capture: opcode, origin, path, flag and body bytes must match one to one.

usage: python tools/crosscheck.py <raw.pcapng> <frames.jsonl>
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from a2wire import Decoder, load_jsonl, server_streams  # noqa: E402


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    capture, jsonl = sys.argv[1:]
    flows = {r["flow"]: r for r in load_jsonl(jsonl, ("flow",))}
    cs = list(load_jsonl(jsonl, ("frame",)))
    streams = server_streams(capture)
    ok = True
    for fid, fl in flows.items():
        key = (*fl["server"].rsplit(":", 1), *fl["client"].rsplit(":", 1))
        st = next((s for k, s in streams.items() if (k[0], str(k[1]), k[2], str(k[3])) == key), None)
        if st is None:
            print(f"flow {fid}: not found in capture")
            ok = False
            continue
        dec = Decoder()
        py = dec.decode_stream(bytes(st.data), aligned=st.syn_seen)
        mine = [r for r in cs if r["flow"] == fid]
        a = [(f.op, f.src, f.path, f.flag, f.body.hex()) for f in py]
        b = [(r["op"], r["src"], r.get("path", ""), r.get("flag", -1), r["hex"]) for r in mine]
        same = a == b
        ok &= same
        print(f"flow {fid} {fl['server']} -> {fl['client']}: python {len(a):,} frames (gaps {st.gaps}, resyncs {dec.resyncs}), "
              f"C# {len(b):,} frames -> {'IDENTICAL' if same else 'DIFFERENT'}")
        if not same:
            for i, (x, y) in enumerate(zip(a, b)):
                if x != y:
                    print(f"  first difference at frame {i + 1}:\n    py {x[:4]} {x[4][:80]}\n    c# {y[:4]} {y[4][:80]}")
                    break
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
