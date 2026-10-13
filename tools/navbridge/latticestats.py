"""Per-playfield lattice coverage: walkable %, multi-level %, file size.

Usage: python latticestats.py <navdir> [<pf> ...]   (no pfs = every lattice.bin found)
"""
import glob
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def main():
    nav = sys.argv[1] if len(sys.argv) > 1 else "."
    files = [os.path.join(nav, pf, "lattice.bin") for pf in sys.argv[2:]]
    if len(sys.argv) == 2:
        files = sorted(glob.glob(os.path.join(nav, "*", "lattice.bin")))
    rows = []
    for f in files:
        pf = os.path.basename(os.path.dirname(f))
        size = os.path.getsize(f)
        b = open(f, "rb").read()
        w, h = struct.unpack_from("<ii", b, 19)
        cells = w * h
        walk = 0
        multi = 0
        levels = 0
        p = 27
        for _ in range(cells):
            n = b[p]
            p += 1
            if n:
                walk += 1
                levels += n
                if n > 1:
                    multi += 1
            p += 2 * n
        rows.append((100.0 * walk / cells, 100.0 * multi / cells, pf, cells, walk, size))
    rows.sort()
    print(f"{'pf':>6} {'cells':>10} {'walkable':>9} {'multi%':>6} {'MB':>6}")
    for cov, mp, pf, cells, walk, size in rows:
        flag = "  <<< CHECK" if cov < 50 or cov > 99.95 else ""
        print(f"{pf:>6} {cells:10,} {cov:8.1f}% {mp:5.1f}% {size / 1048576:6.1f}{flag}")
    total_cells = sum(r[3] for r in rows)
    total_walk = sum(r[4] for r in rows)
    total_mb = sum(r[5] for r in rows) / 1048576
    print(f"{len(rows)} playfields, {total_cells:,} cells, {total_walk:,} walkable, {total_mb:.0f} MB total")


if __name__ == "__main__":
    main()
