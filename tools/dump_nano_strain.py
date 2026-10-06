"""Dump the stats dict of chosen nanos from the OMNICELL-CONTENT nanos pack.

Mirrors NanoLibrary.cs's reader grammar exactly (v3) so the walk cannot drift:
gzip -> magic(LEB-string) -> i32 version -> u8 kind -> i32 count -> records.
Record: 5x i32 header, dicts(attack,defend,stats), actions, events, v2 record-extra.
"""
import gzip
import struct
import sys

TARGETS = set(range(121330, 121347)) | {223380, 223372, 223382, 288774}
PACK = r"F:\testcellao\AOBuddy20\AOBuddy20\AOBuddy20\GameData\nanos.ocp"


class R:
    def __init__(self, data):
        self.d = data
        self.p = 0

    def i8(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def b(self):
        v = self.i8()
        return v != 0

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.p)[0]
        self.p += 4
        return v

    def f32(self):
        v = struct.unpack_from("<f", self.d, self.p)[0]
        self.p += 4
        return v

    def s(self):
        n = 0
        sh = 0
        while True:
            byte = self.i8()
            n |= (byte & 0x7F) << sh
            if not (byte & 0x80):
                break
            sh += 7
        v = self.d[self.p:self.p + n].decode("utf-8", "replace")
        self.p += n
        return v

    def dict_(self):
        c = self.i32()
        return {self.i32(): self.i32() for _ in range(c)}

    def intlist(self):
        c = self.i32()
        return [self.i32() for _ in range(c)]

    def bytes_(self):
        n = self.i32()
        v = self.d[self.p:self.p + n]
        self.p += n
        return v

    def reqs(self):
        c = self.i32()
        return [{k: self.i32() for k in ("childOp", "op", "stat", "target", "value")}
                for _ in range(c)]

    def function(self, version):
        ftype = self.i32()
        self.i32()  # target
        self.i32()  # tick count
        self.i32()  # tick interval
        self.b()    # dolocalstats
        reqs = self.reqs()
        args = []
        for _ in range(self.i32()):
            tag = self.i8()
            if tag == 1:
                args.append(self.i32())
            elif tag == 2:
                args.append(self.f32())
            elif tag == 3:
                args.append(self.s())
            else:
                raise ValueError(f"arg tag {tag}")
        if version >= 2 and self.b():
            self.i32(); self.i32(); self.i32(); self.i32()
            self.intlist()
            self.bytes_()
        return ftype, reqs, args

    def actions(self):
        out = []
        for _ in range(self.i32()):
            at = self.i32()
            out.append((at, self.reqs()))
        return out

    def events(self, version):
        out = []
        for _ in range(self.i32()):
            et = self.i32()
            for _ in range(self.i32()):
                ft, reqs, args = self.function(version)
                out.append((et, ft, reqs, args))
        return out

    def record_extra(self, version):
        if not self.b():
            return
        self.i32(); self.i32(); self.i32()
        self.s()
        self.intlist()
        for _ in range(self.i32()):
            self.i32()
            self.intlist()
        self.intlist()
        for _ in range(self.i32()):
            self.i32(); self.i32()
            for _ in range(self.i32()):
                self.i32()
                self.intlist()
        for _ in range(self.i32()):
            self.i32()
            self.intlist()
        for _ in range(self.i32()):
            self.i32()
            for _ in range(self.i32()):
                self.bytes_()
        if version >= 3:
            for _ in range(self.i32()):
                self.function(version)


def main():
    with gzip.open(PACK, "rb") as f:
        data = f.read()
    r = R(data)
    magic = r.s()
    version = r.i32()
    kind = r.i8()
    count = r.i32()
    print(f"magic={magic} version={version} kind={kind} count={count}")
    hits = {}
    for i in range(count):
        nid = r.i32()
        r.i32(); r.i32(); r.i32(); r.i32()
        r.dict_()          # attack
        r.dict_()          # defend
        stats = r.dict_()  # stats
        r.actions()
        r.events(version)
        if version >= 2:
            r.record_extra(version)
        if nid in TARGETS:
            hits[nid] = stats
        if len(hits) == len(TARGETS):
            break
    print(f"consumed {r.p} bytes of {len(data)}")
    for nid in sorted(hits):
        st = hits[nid]
        print(f"\nnano {nid}: {len(st)} stats; 75(=strain?)={st.get(75)}, 551(=stacking?)={st.get(551)}, 8={st.get(8)}")
        for k in sorted(st):
            print(f"    {k}: {st[k]}")


if __name__ == "__main__":
    sys.exit(main())
