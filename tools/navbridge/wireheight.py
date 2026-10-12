"""Compare wire locations (server/client) against terrain heights reconstructed from the RDB.

Ground truth, two sides:
  wire    - a capture in the capture-folder shape: the *.csv that FollowToCsv/decode.sh write
            ('<stream>,<direction>,<hex>' per TCP segment). The tool frames and inflates the
            streams itself (server side zlib from offset 16, client side plaintext padded to 4)
            and reads the position-bearing messages by type id. The bot's diag-packets.log
            ('#' raw lines, ';ZONE'/';DYNEL' decoded lines) is accepted too.
  rdb     - the playfield's AnarchyGroundDataDB_t (RDB type 1000009) read straight from
            ResourceDatabase: the per-patch delta stream (P+1)^2 bytes, reconstructed by the
            client's cumulative-sum-mod-along-rows-and-columns, sampled bilinear at the record's
            own cell size. No extracted ground.bin involved.

Positions pulled from the wire (offsets verified against captured hex):
  CharDCMove (0x54111123)         heading@30(16) position@46(12)   - the mover's own broadcast
  SimpleCharFullUpdate (0x271B3A6B)               position behind the flags prefix
  PlayfieldAnarchyF (0x5F4B1A39)  CharacterCoordinates@33, pf identity@46 - the server's zone-in spot
  FollowTarget (0x260F3671)       X/Y/Z in the FollowTargetInfo    - server-side mobs/NPCs standing
                                                    on the ground: the cleanest terrain truth
  N3Teleport (0x43197D22)         destination coordinates          - server-moved spots
Usage:
  python wireheight.py <capture.csv|capture.txt|botlog.log ...> [--db <rdb dir>] [--pf <id>]
        [--cells 4] [--source server|client|all] [--verbose]

  --cells  comma list of candidate cell sizes to compare; defaults to the record's own value
"""
import glob
import re
import struct
import sys
import zlib
from collections import defaultdict

import rdb

DEFAULT_DB = r"F:\Testcellao\Aobetaclient\cd_image\data\db"

N3_CHARDCMOVE = 0x54111123
N3_SCFU = 0x271B3A6B
N3_PFANARCHYF = 0x5F4B1A39
N3_FOLLOWTARGET = 0x260F3671
N3_TELEPORT = 0x43197D22
PF_TYPE_IDS = {0x00009C50: "Playfield2", 0x0000C79D: "Playfield", 0x0000C79C: "PlayfieldProxy"}


# ── the capture side: FollowToCsv csv -> framed, decompressed messages ────────────────────

def csv_messages(path):
    """Yield (direction, bytes) per message. Input rows: <stream>,<direction>,<hex payload>."""
    streams = defaultdict(list)  # (stream, dir) -> bytes
    with open(path, "r", encoding="latin1") as fh:
        for line in fh:
            parts = line.strip().split(",")
            if len(parts) != 3 or not all(c in "0123456789abcdefABCDEF" for c in parts[2]):
                continue
            try:
                streams[(parts[0], parts[1])].append(bytes.fromhex(parts[2]))
            except ValueError:
                continue
    for (stream, direction), chunks in sorted(streams.items()):
        raw = b"".join(chunks)
        client = direction.endswith("client")
        plain = bytearray()
        pos = 0
        while pos + 2 <= len(raw):
            # a zlib run (the server switches to flush-framed deflate mid-stream): inflate it
            if (raw[pos] & 0x0F) == 8 and ((raw[pos] << 8) | raw[pos + 1]) % 31 == 0:
                d = zlib.decompressobj()
                try:
                    plain += d.decompress(raw[pos:])
                    pos += len(raw) - pos - len(d.unused_data)
                    continue
                except zlib.error:
                    pass
            # otherwise frame one plaintext message: 16-byte header, size big-endian at 6;
            # the client pads its packets to a 4-byte boundary, the server does not
            if pos + 16 > len(raw):
                break
            size = (raw[pos + 6] << 8) | raw[pos + 7]
            if size < 16 or pos + size > len(raw):
                break
            plain += raw[pos:pos + size]
            pos += size if not client else size + (-size % 4)
        p = 0
        while p + 16 <= len(plain):
            size = (plain[p + 6] << 8) | plain[p + 7]
            if size < 16 or p + size > len(plain):
                break
            yield direction, bytes(plain[p:p + size])
            p += size


# ── the RDB side: type 1000009 -> terrain sampler ─────────────────────────────────────────

def parse_reflective(b, start):
    """The client's reflective container (see AONavExtractor/Reflective.cs for the spec)."""
    namecount = struct.unpack_from("<i", b, start + 16)[0]
    p = start + 21
    names = []
    inclass = False
    pending = None
    while len(names) < namecount:
        e = b.index(b"\0", p)
        s = b[p:e].decode("latin1")
        p = e + 1
        if not inclass:
            inclass = True
            names.append(s)
        elif s == "":
            inclass = False
        elif pending is None:
            pending = s
        else:
            names.append(s)
            pending = None
    if p < len(b) and b[p] == 0:
        p += 1
    objcount = struct.unpack_from("<i", b, p)[0]
    p += 4
    objs = []
    for _ in range(objcount + 1):
        _size, one, cnt = struct.unpack_from("<iii", b, p)
        p += 12
        entries = []
        for _ in range(cnt):
            tag = b[p]
            _vt, _es, total = struct.unpack_from("<iii", b, p + 1)
            p += 13
            entries.append((names[tag] if tag < len(names) else "?%d" % tag, b[p:p + total]))
            p += total
        objs.append(entries)
    return names, objs


class Terrain:
    """One playfield's heightfield, straight from the record's delta stream."""

    def __init__(self, rdb_dir, pf, modulo=None):
        r = rdb.Rdb(rdb_dir)
        _h, b = r.read(1000009, pf)
        if b[:4] != b"CHGA":
            raise ValueError("pf %s: not a CHGA ground record" % pf)
        i = b.find(b"AnarchyGroundDataDB_t")
        if i < 21:
            raise ValueError("pf %s: no AnarchyGroundDataDB_t" % pf)
        _names, objs = parse_reflective(b, i - 21)
        root = objs[1]

        def get(name):
            for n, raw in root:
                if n == name:
                    return raw
            return None

        self.pf = pf
        nm = get("name")
        self.name = (nm[4:4 + struct.unpack_from("<i", nm, 0)[0]].decode("latin1").strip()
                     if nm and len(nm) > 4 else str(pf))
        self.w = struct.unpack("<i", get("map_width"))[0]
        self.h = struct.unpack("<i", get("map_height"))[0]
        self.cell = struct.unpack_from("<f", b, 16)[0]
        self.hscale = struct.unpack_from("<f", b, 20)[0]
        # map_modulo: the record's own value for the cumulative-sum wrap; the extractor
        # hardcodes 256 and never reads this field. Modulo overridable to A/B them on the wire.
        self.record_modulo = struct.unpack("<i", get("map_modulo"))[0] if get("map_modulo") else 256
        self.modulo = modulo if modulo else self.record_modulo
        blobs = lambda raw: [zlib.decompress(z) for z in _blobs(raw)]  # noqa: E731
        hm = blobs(get("heightmap_compressed_data"))
        self.P = self.bits = 0
        for p_ in (8, 16, 32, 64, 128, 256):
            for bt in (8, 16):
                if self.P == 0 and (p_ + 1) * (p_ + 1) * (bt // 8) == len(hm[0]):
                    self.P, self.bits = p_, bt
        if self.P == 0:
            raise ValueError("pf %s: patch of %d bytes" % (pf, len(hm[0])))
        S = self.P + 1
        nx, nz = (self.w - 1) // self.P, (self.h - 1) // self.P
        if len(hm) != nx * nz:
            raise ValueError("pf %s: %d patches for %dx%d grid (P=%d)" % (pf, len(hm), nx, nz, self.P))
        mod = self.modulo if self.bits == 8 else 65536
        self.heights = [0] * (self.w * self.h)
        acc = [0] * (S * S)
        for bz in range(nz):
            for bx in range(nx):
                blk = hm[bz * nx + bx]
                for r_ in range(S):
                    for c in range(S):
                        v = blk[r_ * S + c] if self.bits == 8 else struct.unpack_from("<H", blk, (r_ * S + c) * 2)[0]
                        if r_ > 0:
                            v += acc[(r_ - 1) * S + c]
                        acc[r_ * S + c] = v
                for r_ in range(S):
                    for c in range(1, S):
                        acc[r_ * S + c] += acc[r_ * S + c - 1]
                for r_ in range(S):
                    for c in range(S):
                        v = acc[r_ * S + c] % mod
                        self.heights[(bz * self.P + r_) * self.w + bx * self.P + c] = v * 256 if self.bits == 8 else v

    def height(self, x, z, cell=None):
        cell = cell or self.cell
        fx, fz = x / cell, z / cell
        ix, iz = int(fx), int(fz)
        if ix < 0 or iz < 0 or ix + 1 >= self.w or iz + 1 >= self.h:
            return float("nan")
        tx, tz = fx - ix, fz - iz
        H = self.heights
        w = self.w
        v = (H[iz * w + ix] * (1 - tx) * (1 - tz) + H[iz * w + ix + 1] * tx * (1 - tz)
             + H[(iz + 1) * w + ix] * (1 - tx) * tz + H[(iz + 1) * w + ix + 1] * tx * tz)
        return v * self.hscale / 256.0


def _blobs(raw):
    out = []
    p = 0
    while p + 4 <= len(raw):
        n = struct.unpack_from("<i", raw, p)[0]
        p += 4
        out.append(raw[p:p + n])
        p += n
    return out


# ── the wire side: capture -> (source, kind, x, y, z) ─────────────────────────────────────

READABLE = re.compile(r"^\s*(\d+)/(server|client)\s+(\w+Message)\b(.*)$")
ORDERED = re.compile(r"^([0-9.]+)\t(\w+)\t(\d+)\t(\w+Message)\t([0-9a-fA-F]+)$")
BOTRAW = re.compile(r"^#\d+ \S+ \S+ \S+ (S|C) N3/(\w+) len=\d+ hex=([0-9a-fA-F]+)")
BOTLINE = re.compile(r"^;(ZONE|DYNEL|DOOR) ")
XYZ = re.compile(r"\bX=([-0-9.,]+) Y=([-0-9.,]+) Z=([-0-9.,]+)")


def _f(s):  # decoded text uses the host's comma decimals
    return float(s.replace(",", "."))


def scfu_position(raw):
    """SimpleCharFullUpdate: pos sits behind the flags-conditional prefix (verified vs hex)."""
    o = 16 + 4 + 8 + 1 + 1
    flags = struct.unpack_from(">i", raw, o)[0]
    o += 4
    if flags & 0x40:
        o += 4
    if flags & 0x20:
        o += 8
    return struct.unpack_from(">fff", raw, o)


def extract(lines):
    """Yield (source, kind, x, y, z) - source is 'server'/'client' where the capture says."""
    for line in lines:
        m = READABLE.match(line)
        if m:
            stream, src, name, rest = m.groups()
            if name == "FollowTargetMessage":
                xyz = XYZ.search(rest)
                if xyz:
                    yield src, "mob", _f(xyz.group(1)), _f(xyz.group(2)), _f(xyz.group(3))
                continue
            hx = re.search(r"raw=([0-9a-fA-F-]+)", rest)
            if not hx:
                continue
            raw = bytes.fromhex(hx.group(1).replace("-", ""))
            yield from _from_raw(src, name, raw)
            continue
        m = ORDERED.match(line)
        if m:
            ts, direction, _seq, name, hexs = m.groups()
            yield from _from_raw(direction, name, bytes.fromhex(hexs))
            continue
        m = BOTRAW.match(line)
        if m:
            src = "server" if m.group(1) == "S" else "client"
            yield from _from_raw(src, m.group(2) + "Message", bytes.fromhex(m.group(3)))
            continue
        m = BOTLINE.match(line)
        if m:
            kind, rest = m.group(1), m.group(2)
            pm = re.search(r"pos=\((-0-9.,]+),([-0-9.,]+),([-0-9.,]+)\)", rest)
            if not pm:
                continue
            yield "server", kind.lower(), _f(pm.group(1)), _f(pm.group(2)), _f(pm.group(3))


def _from_raw(src, name, raw):
    # offsets verified against captured hex (see module doc); FollowTarget located empirically:
    # [typeid4][identity8][type1][inftype1][movemode1][target8][unk4] then X@43 Y@47 Z@51
    if name == "CharDCMoveMessage" and len(raw) >= 58:
        x, y, z = struct.unpack_from(">fff", raw, 46)
        yield src, "move", x, y, z
    elif name == "SimpleCharFullUpdateMessage" and len(raw) > 60:
        try:
            x, y, z = scfu_position(raw)
            yield src, "dynel", x, y, z
        except struct.error:
            pass
    elif name == "PlayfieldAnarchyFMessage" and len(raw) >= 58:
        x, y, z = struct.unpack_from(">fff", raw, 33)
        yield src, "zonein", x, y, z
    elif name == "FollowTargetMessage" and len(raw) >= 44:
        # two info variants behind [type@28][inftype@29][movemode@30]:
        #   2 FollowTargetInfo    [target8][unk4]           X@43 Y@47 Z@51
        #   1 FollowCoordinateInfo [count@31]               CurrentCoordinates X@32 Y@36 Z@40
        inf = raw[29]
        if inf == 2 and len(raw) >= 55:
            x, y, z = struct.unpack_from(">fff", raw, 43)
            yield src, "mob", x, y, z
        elif inf == 1:
            x, y, z = struct.unpack_from(">fff", raw, 32)
            yield src, "mob", x, y, z


def _from_id(src, n3id, raw):
    names = {N3_CHARDCMOVE: "CharDCMoveMessage", N3_SCFU: "SimpleCharFullUpdateMessage",
             N3_PFANARCHYF: "PlayfieldAnarchyFMessage", N3_FOLLOWTARGET: "FollowTargetMessage"}
    name = names.get(n3id)
    if name:
        yield from _from_raw(src, name, raw)


# ── the comparison ────────────────────────────────────────────────────────────────────────

def fit(terr, pts, cell):
    """wireY = a*terrain+b, least squares over finite pairs."""
    pairs = [(y, terr.height(x, z, cell)) for src, kind, x, y, z in pts]
    pairs = [(y, t) for y, t in pairs if t == t]
    n = len(pairs)
    if n < 2:
        return None
    sx = sum(t for y, t in pairs)
    sy = sum(y for y, t in pairs)
    sxx = sum(t * t for y, t in pairs)
    sxy = sum(y * t for y, t in pairs)
    d = n * sxx - sx * sx
    a = (n * sxy - sx * sy) / d if d else 0.0
    b = (sy - a * sx) / n
    return a, b, n


def main(argv):
    cap_files = []
    db = DEFAULT_DB
    pf = None
    cells = None
    modulo = None
    source_filter = "server"   # server-reported locations are the ask; 'all' adds the client's own
    csv_out = None
    verbose = False
    it = iter(argv[1:])
    for a in it:
        if a == "--db":
            db = next(it)
        elif a == "--pf":
            pf = int(next(it))
        elif a == "--cells":
            cells = [float(c) for c in next(it).split(",")]
        elif a == "--modulo":
            modulo = int(next(it))
        elif a == "--source":
            source_filter = next(it)
        elif a == "--csv":
            csv_out = next(it)
        elif a == "--verbose":
            verbose = True
        else:
            cap_files.extend(glob.glob(a))
    if not cap_files:
        print(__doc__)
        return 2

    lines = []
    points = []
    zone_pf = None
    for f in cap_files:
        if f.lower().endswith(".csv"):
            for direction, raw in csv_messages(f):
                if len(raw) < 20:
                    continue
                n3id = struct.unpack_from(">i", raw, 16)[0]
                if n3id == N3_PFANARCHYF and len(raw) >= 54 and zone_pf is None:
                    t, ins = struct.unpack_from(">ii", raw, 46)
                    if t in PF_TYPE_IDS and 0 < ins < 30000:
                        zone_pf = ins
                points.extend(_from_id("server" if direction.endswith("server") else "client", n3id, raw))
        else:
            with open(f, "r", encoding="latin1") as fh:
                lines.extend(fh)
    points.extend(extract(lines))
    if pf is None:
        pf = zone_pf
        if pf:
            print("playfield from zone-in: %s" % pf)
    if source_filter != "all":
        points = [p for p in points if p[0] == source_filter]
    print("wire points: %d (server %d, client %d)" % (
        len(points), sum(1 for p in points if p[0] == "server"), sum(1 for p in points if p[0] == "client")))
    if pf is None:
        print("no playfield found on the wire: pass --pf")
        return 2

    # A/B the cumulative-sum wrap when the record disagrees with the extractor's hardcode:
    # the wire decides which reconstruction is the terrain the server walks us on.
    terr0 = Terrain(db, pf)
    rec = terr0.record_modulo
    modulos = [modulo] if modulo else ([rec, 256] if rec not in (0, 256) else [256])
    for mod in modulos:
        terr = terr0 if mod == rec else Terrain(db, pf, mod)
        if csv_out and mod == modulos[0]:
            run = cap_files[0].replace("\\", "/").rsplit("/", 1)[-1].split(".")[0]
            n = 0
            with open(csv_out, "w", encoding="utf8") as fh:
                fh.write("run,source,kind,x,z,y_server,y_rdb,delta\n")
                for p in points:
                    t = terr.height(p[2], p[4])
                    if t != t:
                        continue
                    n += 1
                    fh.write("%s,%s,%s,%.2f,%.2f,%.2f,%.2f,%.2f\n" % (
                        run, p[0], p[1], p[2], p[4], p[3], t, p[3] - t))
            print("csv: %d rows -> %s" % (n, csv_out))
        print("rdb pf %s '%s': %dx%d samples, cell=%g hscale=%g P=%d bits=%d modulo=%d -> world %gx%g m" % (
            pf, terr.name, terr.w, terr.h, terr.cell, terr.hscale, terr.P, terr.bits, terr.modulo,
            (terr.w - 1) * terr.cell, (terr.h - 1) * terr.cell))
        if len(modulos) > 1:
            print("  A/B: record map_modulo=%d vs the extractor's hardcode 256" % rec)
        for cell in cells or [terr.cell]:
            label = "cell=%g%s" % (cell, " (record)" if cell == terr.cell else " (override)")
            bykind = defaultdict(list)
            for p in points:
                t = terr.height(p[2], p[4], cell)
                if t == t:
                    bykind[p[1]].append((p, t))
            print("\n-- %s" % label)
            allerr = []
            for kind in sorted(bykind):
                errs = sorted(p[3] - t for p, t in bykind[kind])
                med = errs[len(errs) // 2]
                near = 100 * sum(1 for e in errs if abs(e) < 3) // len(errs)
                allerr += errs
                f = fit(terr, [p for p, _ in bykind[kind]], cell)
                fitx = "fit wireY=%+.3f*terrain%+.2f" % (f[0], f[1]) if f else ""
                print("   %-7s n=%-5d median err=%+7.2f m   |err|<3m: %3d%%   %s" % (kind, len(errs), med, near, fitx))
                if verbose:
                    for p, t in bykind[kind][:12]:
                        print("        %-7s (%9.1f,%9.1f) wireY=%7.2f terrain=%7.2f err=%+7.2f" % (p[0], p[2], p[4], p[3], t, p[3] - t))
            if allerr:
                allerr.sort()
                print("   ALL     n=%-5d median err=%+7.2f m   |err|<3m: %3d%%" % (
                    len(allerr), allerr[len(allerr) // 2], 100 * sum(1 for e in allerr if abs(e) < 3) // len(allerr)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
