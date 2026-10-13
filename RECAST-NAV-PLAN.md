# RECAST-NAV-PLAN

The overland nav rebuild: wire-verified data in, Recast-derived walkable model out, the existing
A* behind `IWalkGrid` untouched. Written 2026-10-12, after the terrain+collision layers were
validated against real-client captures (see tools/navbridge/wireheight.py and the numbers below).

## Ground truth (done, wire-proven)

- RDB 1000009 terrain: 4x4 m tiles, per-patch (P+1)^2 delta stream, cumulative sum mod 256.
  Validated: pf 600, 440 wire locations, median |dy| 0.02 m, 100% < 3 m; mob fit slope 1.001.
- RDB 1000013 collision: decoded by the client's own reader (x86 P/Invoke). The current
  collision.bin/walls.bin SPLIT drops steep triangles < 1 m tall - trunks and thin branches are
  missing (verified: the 4x4 m probe at the owner's tree finds canopies only). Fix = step 1.
- Gravity 25 m/s^2; falls broadcast takeoff + landing only; server does no clientless gravity.

## Step 1 - unfiltered extraction (this step)

AONavExtractor writes a third AOCL file `meshes.bin`: ALL non-degenerate 1000013 triangles,
no slope/height filter (the split into collision.bin/walls.bin stays for OverlandGrid
compatibility). Trunk/branch/railing triangles re-enter the dataset. Verify with the tree:
dense probe at pf 600 (3513.1, 1034.2) must show steep geometry near the canopy if trunks
live in 1000013 (else the bump capture in step 2 decides where trunk blocking comes from).

## Step 2 - calibration capture

Owner circles trees/posts/walls with capture on. Determines: agent radius (min distance from
position track to mesh surface - post = sharp, wall = averaged), trunk blocking + location,
gap passability (squeeze between close trunks). One scalar calibrated, two open questions closed.

## Step 3 - coarse lattice for pf 600

Recast spans (outdoor cs 1.0 / ch 0.25; filterLedgeSpans OFF - the under-arch corridor lesson;
**agentHeight 0.6** - the calibrated collision band, see below; agentRadius 0 - AO collides the
pivot POINT, no radius) -> per-cell Y-interval lattice (Northbound v10 interval model).
Validated by wireheight.py against ALL captures before anything routes on it.

## The calibrated collision model (owner captures, 2026-10-12 evening)

- AO collides the PIVOT POINT against geometry in a low band above the feet.
- Owner decision: **client-faithful, band = [foot + 0.2, foot + 0.6]** (check height in there;
  wire-proof: pivot reaches 0.05 m from thin trunks; a slab with bottom +0.50 above the feet
  did NOT block; +1.04 also not). Agent nanos (Ruse of Taren scale -10..-60) intentionally not
  covered: a smaller character goes nowhere the big one can't.
- Recast mapping: agentHeight 0.6 (clearance filter = band top), agentMaxClimb 0.8, slope 60,
  radius 0, filterLedgeSpans OFF. CAUTION: NorthboundGrid mission rooms use agentHeight 1.9 -
  low-ceiling passages are unreachable there today; revisit after step 4.
- The owner's circling capture was made with the live client moving the body - the blocking is
  client-enforced and smooth (slide), and the wire saw it all.

## Step 4 - the walk that started it

Re-run the y=67.25 mission-entrance approach on the new lattice. Success = the body walks the
ramp, through the entrance, at ground level, first try.

## The architecture (Option A)

- Coarse tier: 1.0 m over the whole pf (~57 MB: dense u16 walkable-Y + bitflags + sparse
  multi-level extras; connectivity computed from Y on the fly). Forests live here: canopies =
  extra levels, trunks = indexed geometry. ~75-80 MB/pf runtime, ~10-20 MB packed, one pf
  loaded per zone-in. Tiled offline build (64 m tiles), merged per-pf packs, versioned per
  playfieldVersion.
- Fine tier: 0.25 m only where conservative blocking could sever the only route (doorways,
  arches). The existing NorthboundGrid per-house lattices serve as-is; the coarse->fine
  handoff is already in production code.
- Passability: cells index their collision triangles; an A* edge is a CYLINDER SWEEP - agent
  (r, 1.9 m) swept along the segment, blocked iff a triangle comes within r of the segment AND
  its height range overlaps the body span. No cell blocking for sub-cell geometry, no
  clearance erosion, no sealed groves. Open ground stays heightfield-cheap.
- Falls: freeze x/z, silent for sqrt(2h/25), announce the landing (see memory ao-fall-physics).
- Detour (Option B) later: same pipeline, adds string-pull smoothing + off-mesh teleports
  (1000013 carries TeleportDestPf per record - zone lines, wompahs, lifts).

## Non-goals

- Runtime tile streaming (tile-window U-shape failure modes; merged packs fit in RAM).
- Dynamic obstacles (mobs do not block pathing).
- Mission interiors (NorthboundGrid already owns them).
