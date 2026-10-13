"""Fit the agent's bounding-cylinder radius from a calibration capture (circling trees etc.).

The client's position track around a known mesh gives r_agent as the closest approach between
position samples and the blocking surface: while circling in contact, that distance is the
agent radius. Also reads the blocking BAND: which triangle heights actually block (the owner
observed the character clipping through upper branches - collision likely tests only a low
band above the feet, so the fit reports each contact's triangle height range).

Usage: python agentradius.py <capture.csv> [--pf <id>] [--nav <dir>] [--cluster <m>]
"""
import math
import struct
import sys
from collections import defaultdict

import wireheight as wh


def pt_tri_2d(px, pz, tri):
    """2D (xz) distance from point to a triangle's projection; 0 inside."""
    ax, az, bx, bz, cx, cz = tri[0], tri[2], tri[3], tri[5], tri[6], tri[8]
    d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz)
    d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz)
    d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az)
    neg = (d1 < 0) or (d2 < 0) or (d3 < 0)
    pos = (d1 > 0) or (d2 > 0) or (d3 > 0)
    if not (neg and pos):
        return 0.0
    def seg(qx, qz, rx, rz):
        vx, vz = rx - qx, rz - qz
        L = vx * vx + vz * vz
        t = 0 if L == 0 else max(0.0, min(1.0, ((px - qx) * vx + (pz - qz) * vz) / L))
        return math.hypot(px - (qx + t * vx), pz - (qz + t * vz))
    return min(seg(ax, az, bx, bz), seg(bx, bz, cx, cz), seg(cx, cz, ax, az))


def main(argv):
    cap = argv[1]
    nav = wh.NAV_DIR
    cluster_r = 6.0
    i = 2
    while i < len(argv):
        if argv[i] == "--nav":
            nav = argv[i + 1]; i += 2
        elif argv[i] == "--cluster":
            cluster_r = float(argv[i + 1]); i += 2
        else:
            i += 1

    pts = []
    zone_pf = None
    for d, raw in wh.csv_messages(cap):
        if len(raw) < 58:
            continue
        n3 = struct.unpack_from(">i", raw, 16)[0]
        if n3 == wh.N3_PFANARCHYF and zone_pf is None and len(raw) >= 54:
            t, ins = struct.unpack_from(">ii", raw, 46)
            if t in wh.PF_TYPE_IDS and 0 < ins < 30000:
                zone_pf = ins
        if n3 == wh.N3_CHARDCMOVE and d.endswith("client"):
            x, y, z = struct.unpack_from(">fff", raw, 46)
            pts.append((x, y, z))
    pf = zone_pf or 600
    print(f"samples: {len(pts)}  pf: {pf}")

    col = wh.Collision(nav, pf, files=("meshes.bin",))
    print(f"mesh triangles: {len(col.tris):,}")

    # greedy spatial clustering: circling sessions cluster tightly around their tree
    clusters = []
    for p in pts:
        for c in clusters:
            cx = sum(q[0] for q in c) / len(c)
            cz = sum(q[2] for q in c) / len(c)
            if math.hypot(p[0] - cx, p[2] - cz) < cluster_r:
                c.append(p)
                break
        else:
            clusters.append([p])
    # merge cluster pairs closer than cluster_r
    merged = True
    while merged:
        merged = False
        for a in range(len(clusters)):
            for b in range(a + 1, len(clusters)):
                ax = sum(q[0] for q in clusters[a]) / len(clusters[a])
                az = sum(q[2] for q in clusters[a]) / len(clusters[a])
                bx = sum(q[0] for q in clusters[b]) / len(clusters[b])
                bz = sum(q[2] for q in clusters[b]) / len(clusters[b])
                if math.hypot(ax - bx, az - bz) < cluster_r:
                    clusters[a] += clusters[b]
                    del clusters[b]
                    merged = True
                    break
            if merged:
                break
    clusters = [c for c in clusters if len(c) >= 20]
    clusters.sort(key=len, reverse=True)
    print(f"clusters >=20 samples: {len(clusters)}")

    for ci, c in enumerate(clusters):
        cx = sum(q[0] for q in c) / len(c)
        cz = sum(q[2] for q in c) / len(c)
        ymin = min(q[1] for q in c)
        # candidate triangles near the cluster: STEEP ones only (trunks/roots/wall faces) -
        # the horizontal surfaces are what the body stands ON, and including them makes every
        # ground sample a 0.00 m "contact" with the floor it is standing on.
        cand = set()
        cells = set()
        for q in c:
            ccx, ccz = int(q[0] // col.cell), int(q[2] // col.cell)
            for dx in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    cells.add((ccx + dx, ccz + dz))
        for cell in cells:
            for idx in col.grid.get(cell, ()):
                v = col.tris[idx]
                e1x, e1y, e1z = v[3] - v[0], v[4] - v[1], v[5] - v[2]
                e2x, e2y, e2z = v[6] - v[0], v[7] - v[1], v[8] - v[2]
                nx = e1y * e2z - e1z * e2y
                ny = e1z * e2x - e1x * e2z
                nz = e1x * e2y - e1y * e2x
                ln = math.sqrt(nx * nx + ny * ny + nz * nz)
                if ln > 1e-9 and abs(ny) / ln <= 0.5:
                    cand.add(idx)
        if not cand:
            print(f"\ncluster #{ci}: {len(c)} samples at ({cx:.0f},{cz:.0f}) - no steep geometry nearby (open ground)")
            continue
        contacts = []
        for (x, y, z) in c:
            dmin, tri = None, None
            for idx in cand:
                v = col.tris[idx]
                # triangle must reach into the plausible body band around this sample
                if v[1] > y + 2.5 or max(v[1], v[4], v[7]) < y - 0.5:
                    continue
                dd = pt_tri_2d(x, z, v)
                if dmin is None or dd < dmin:
                    dmin, tri = dd, v
            if dmin is not None:
                contacts.append((x, y, z, dmin, tri))
        if not contacts:
            print(f"\ncluster #{ci}: {len(c)} samples at ({cx:.0f},{cz:.0f}) y {ymin:.1f} - no mesh nearby")
            continue
        contacts.sort(key=lambda t: t[3])
        r = contacts[0][3]
        rt = contacts[0][4]
        tri_y = (rt[1], max(rt[1], rt[4], rt[7]))
        # band probe: for close contacts (<1 m), where does the touched triangle sit vertically?
        close = [t for t in contacts if t[3] < 1.0]
        band = defaultdict(int)
        for (_, y, _, _, rt2) in close:
            lo = rt2[1]
            hi = max(rt2[1], rt2[4], rt2[7])
            if hi < y + 0.5:
                band["below waist (hi < y+0.5)"] += 1
            elif lo > y + 1.2:
                band["above head (lo > y+1.2)"] += 1
            else:
                band["body band (y+0.5..y+1.2)"] += 1
        print(f"\ncluster #{ci}: {len(c):3d} samples at ({cx:.0f},{cz:.0f}), y {ymin:.1f}..{max(q[1] for q in c):.1f}")
        print(f"  closest approach to mesh: {r:.2f} m  -> agent radius ~= {r:.2f} (triangle y {tri_y[0]:.2f}..{tri_y[1]:.2f})")
        print(f"  contacts <1 m: {len(close)}; their triangle band: {dict(band) or 'none'}")
        for (x, y, z, dmin, rt2) in contacts[:5]:
            lo = rt2[1]
            hi = max(rt2[1], rt2[4], rt2[7])
            print(f"    ({x:8.1f},{z:8.1f}) y={y:6.2f}  d={dmin:5.2f}  tri y {lo:6.2f}..{hi:6.2f}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
