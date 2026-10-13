"""Stdlib-only helpers for AION 2 captures: pcap/pcapng reading, server->client TCP reassembly, an independent frame
decoder (varint framing, flag byte, LZ4 bundles, embedded bundles) and frames.jsonl loading.

The frame decoder deliberately re-implements the rules of src/CubeRadar.Core/Protocol/FrameDecoder.cs in a few lines,
so the two implementations can be checked against each other on real captures (see make_fixture.py).
"""
from __future__ import annotations

import json
import struct
from dataclasses import dataclass, field
from typing import Iterator

GAME_PORT = 13328
HEARTBEAT_SIGS = (b"\x0e\x00\x36", b"\x06\x00\x36")
MAX_FRAME = 65_535
MAX_BUNDLE = 8 * 1024 * 1024
MAX_DEPTH = 4


# ---------------------------------------------------------------- capture files

def read_packets(path: str) -> Iterator[tuple[int, int, bytes]]:
    """Yields (unix_microseconds, linktype, packet bytes) from a .pcap or .pcapng file."""
    with open(path, "rb") as f:
        data = f.read()
    magic = data[:4]
    if magic == b"\x0a\x0d\x0d\x0a":
        yield from _read_pcapng(data)
    elif magic in (b"\xd4\xc3\xb2\xa1", b"\x4d\x3c\xb2\xa1"):
        nano = magic == b"\x4d\x3c\xb2\xa1"
        linktype = struct.unpack_from("<I", data, 20)[0]
        o = 24
        while o + 16 <= len(data):
            sec, frac, incl, _orig = struct.unpack_from("<IIII", data, o)
            o += 16
            yield sec * 1_000_000 + (frac // 1000 if nano else frac), linktype, data[o:o + incl]
            o += incl
    else:
        raise ValueError(f"{path}: not a little-endian pcap/pcapng file")


def _read_pcapng(data: bytes) -> Iterator[tuple[int, int, bytes]]:
    interfaces: list[tuple[int, int]] = []  # (linktype, ticks per second)
    o = 0
    while o + 12 <= len(data):
        btype, blen = struct.unpack_from("<II", data, o)
        body = data[o + 8:o + blen - 4]
        if btype == 0x0A0D0D0A:
            interfaces = []
        elif btype == 1:
            linktype = struct.unpack_from("<H", body, 0)[0]
            resol = 1_000_000
            p = 8
            while p + 4 <= len(body):
                code, length = struct.unpack_from("<HH", body, p)
                if code == 0:
                    break
                if code == 9 and length >= 1:
                    v = body[p + 4]
                    resol = 2 ** (v & 0x7F) if v & 0x80 else 10 ** v
                p += 4 + ((length + 3) & ~3)
            interfaces.append((linktype, resol))
        elif btype == 6:
            iface, hi, lo, incl, _orig = struct.unpack_from("<IIIII", body, 0)
            linktype, resol = interfaces[iface]
            ticks = hi << 32 | lo
            yield ticks * 1_000_000 // resol, linktype, body[20:20 + incl]
        elif btype == 3:  # simple packet block (no timestamp)
            linktype, _ = interfaces[0]
            yield 0, linktype, body[4:]
        o += blen


@dataclass
class Segment:
    t_us: int
    src: str
    sport: int
    dst: str
    dport: int
    seq: int
    flags: int
    payload: bytes


def parse_tcp(linktype: int, pkt: bytes, t_us: int = 0) -> Segment | None:
    if linktype == 1:  # Ethernet
        if len(pkt) < 14:
            return None
        ethertype = struct.unpack_from(">H", pkt, 12)[0]
        off = 14
        if ethertype == 0x8100 and len(pkt) >= 18:
            ethertype = struct.unpack_from(">H", pkt, 16)[0]
            off = 18
        if ethertype != 0x0800:
            return None
    elif linktype in (0, 108):  # BSD loopback / Npcap loopback: 4-byte address family
        off = 4
    elif linktype in (12, 101, 228):  # raw IPv4
        off = 0
    else:
        return None
    ip = pkt[off:]
    if len(ip) < 20 or ip[0] >> 4 != 4 or ip[9] != 6:
        return None
    ihl = (ip[0] & 0x0F) * 4
    total = struct.unpack_from(">H", ip, 2)[0] or len(ip)
    tcp = ip[ihl:total]
    if len(tcp) < 20:
        return None
    sport, dport, seq = struct.unpack_from(">HHI", tcp, 0)
    doff = (tcp[12] >> 4) * 4
    src = ".".join(map(str, ip[12:16]))
    dst = ".".join(map(str, ip[16:20]))
    return Segment(t_us, src, sport, dst, dport, seq, tcp[13], bytes(tcp[doff:]))


@dataclass
class Stream:
    key: tuple
    data: bytearray = field(default_factory=bytearray)
    times: list = field(default_factory=list)  # (stream offset, t_us) per delivered segment
    gaps: int = 0
    syn_seen: bool = False


def server_streams(path: str, port: int = GAME_PORT) -> dict[tuple, Stream]:
    """Reassembles every server->client stream from `port` (in-order delivery, duplicates trimmed, gaps counted)."""
    segs: dict[tuple, list[Segment]] = {}
    for t_us, lt, pkt in read_packets(path):
        s = parse_tcp(lt, pkt, t_us)
        if s is None or s.sport != port:
            continue
        segs.setdefault((s.src, s.sport, s.dst, s.dport), []).append(s)
    out = {}
    for key, lst in segs.items():
        st = Stream(key)
        nxt = None
        for s in sorted(lst, key=lambda s: (s.seq - lst[0].seq) % 2**32):
            if s.flags & 0x02:  # SYN
                st.syn_seen = True
                nxt = (s.seq + 1) % 2**32
                continue
            if not s.payload:
                continue
            if nxt is None:
                nxt = s.seq
            diff = (s.seq - nxt + 2**31) % 2**32 - 2**31
            p = s.payload
            if diff < 0:
                if -diff >= len(p):
                    continue
                p = p[-diff:]
            elif diff > 0:
                st.gaps += 1
            st.times.append((len(st.data), s.t_us))
            st.data += p
            nxt = (s.seq + len(s.payload)) % 2**32
        out[key] = st
    return out


# ---------------------------------------------------------------- framing

def varint(b: bytes, o: int) -> tuple[int, int] | None:
    """LEB128, at most 5 bytes. Returns (value, new offset) or None."""
    v = shift = 0
    for i in range(o, min(len(b), o + 5)):
        v |= (b[i] & 0x7F) << shift
        if b[i] < 0x80:
            return v, i + 1
        shift += 7
    return None


def lz4_block(src: bytes, size: int) -> bytes:
    """LZ4 block format decompressor (no frame header)."""
    dst = bytearray()
    i, n = 0, len(src)
    while i < n:
        token = src[i]
        i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]
                i += 1
                lit += b
                if b != 255:
                    break
        dst += src[i:i + lit]
        i += lit
        if i >= n:
            break
        off = src[i] | src[i + 1] << 8
        i += 2
        if off == 0 or off > len(dst):
            raise ValueError("lz4: bad offset")
        ml = token & 15
        if ml == 15:
            while True:
                b = src[i]
                i += 1
                ml += b
                if b != 255:
                    break
        ml += 4
        start = len(dst) - off
        for k in range(ml):
            dst.append(dst[start + k])
        if len(dst) > size:
            raise ValueError("lz4: output larger than announced")
    return bytes(dst)


@dataclass
class Frame:
    seq: int
    op: str          # two wire bytes as hex, e.g. "4136"
    src: str         # top | lz4 | emb
    depth: int
    path: str
    flag: int        # -1 = none
    wire: int        # frame size incl. varint
    body: bytes
    offset: int = -1  # stream offset (top-level frames only)


class Decoder:
    """Same rules as the C# FrameDecoder, written independently."""

    def __init__(self, scan_embedded: bool = True):
        self.scan_embedded = scan_embedded
        self.seq = 0
        self.bundle_seq = 0
        self.frames: list[Frame] = []
        self.resyncs = 0
        self.bad_bundles = 0

    def decode_stream(self, data: bytes, aligned: bool) -> list[Frame]:
        pos = 0
        hunting = not aligned
        while pos < len(data):
            if hunting:
                hits = [i for i in (data.find(s, pos) for s in HEARTBEAT_SIGS) if i >= 0]
                if not hits:
                    break
                pos = min(hits)
                hunting = False
                continue
            if data[pos] == 0:
                pos += 1
                continue
            r = varint(data, pos)
            if r is None:
                if len(data) - pos < 5:
                    break
                self.resyncs += 1
                pos += 1
                hunting = True
                continue
            length, after = r
            vlen = after - pos
            size = length + vlen - 4
            if length < 6 or size > MAX_FRAME:
                self.resyncs += 1
                pos += 1
                hunting = True
                continue
            if size > len(data) - pos:
                break  # incomplete at the end of the capture
            self._frame(data[pos:pos + size], vlen, 0, "", "top", offset=pos)
            pos += size
        return self.frames

    def _frame(self, frame: bytes, vlen: int, depth: int, path: str, src: str, offset: int = -1):
        o = vlen
        flag = -1
        if o < len(frame) and 0xF0 <= frame[o] <= 0xFE:
            flag = frame[o]
            o += 1
        if o + 2 > len(frame):
            return
        if frame[o] == 0xFF and frame[o + 1] == 0xFF:
            self.bundle_seq += 1
            bid = self.bundle_seq
            if o + 6 > len(frame) or depth > MAX_DEPTH:
                self.bad_bundles += 1
                return
            raw = struct.unpack_from("<I", frame, o + 2)[0]
            if raw == 0 or raw > MAX_BUNDLE:
                self.bad_bundles += 1
                return
            try:
                inner = lz4_block(frame[o + 6:], raw)
            except (ValueError, IndexError):
                self.bad_bundles += 1
                return
            prefix = (path + "/" if path else "") + f"B{bid}."
            self._walk(inner, depth + 1, prefix, "lz4" if src == "top" else src)
            return
        self.seq += 1
        f = Frame(self.seq, frame[o:o + 2].hex(), src, depth, path, flag, len(frame), bytes(frame[o + 2:]), offset)
        self.frames.append(f)
        if self.scan_embedded and f.op != "0036" and len(f.body) >= 16 and depth < MAX_DEPTH:
            self._embedded(f)

    def _walk(self, data: bytes, depth: int, prefix: str, src: str):
        pos = index = 0
        while pos < len(data):
            if data[pos] == 0:
                pos += 1
                continue
            r = varint(data, pos)
            if r is None:
                return
            length, after = r
            size = length + (after - pos) - 4
            if length < 6 or size > len(data) - pos:
                return
            self._frame(data[pos:pos + size], after - pos, depth, prefix + str(index), src)
            index += 1
            pos += size

    def _embedded(self, host: Frame):
        b = host.body
        i = 1
        while i + 8 < len(b):
            if b[i] == 0xFF and b[i + 1] == 0xFF:
                for n in (3, 2, 1):
                    at = i - n
                    r = varint(b, at) if at >= 0 else None
                    if r is None or r[1] - at != n:
                        continue
                    end = at + r[0] + n - 4
                    if r[0] < 12 or end > len(b):
                        continue
                    raw = struct.unpack_from("<I", b, i + 2)[0]
                    if raw == 0 or raw > MAX_BUNDLE:
                        continue
                    try:
                        inner = lz4_block(b[i + 6:end], raw)
                    except (ValueError, IndexError):
                        continue
                    if len(inner) != raw:
                        continue
                    self.bundle_seq += 1
                    prefix = (host.path + "/" if host.path else "") + f"E{self.bundle_seq}."
                    self._walk(inner, host.depth + 1, prefix, "emb")
                    i = end - 1
                    break
            i += 1


# ---------------------------------------------------------------- frames.jsonl

def load_jsonl(path: str, types: tuple[str, ...] | None = None) -> Iterator[dict]:
    with open(path, encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            r = json.loads(line)
            if types is None or r.get("type") in types:
                yield r


def f32x3(b: bytes, o: int) -> tuple[float, float, float]:
    return struct.unpack_from("<3f", b, o)
