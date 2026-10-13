# OVERLAND-TODO



Overland/travel problems left after the Recast lattice fleet build (2026-10-12).

Each item: symptom â†’ likely cause â†’ fix â†’ acceptance test. Suggested order: 4 â†’ 3 â†’ 1 â†’ 2

(4 is the most frequent, 2 self-heals today).



---



## 1. Wompahs: walks through them / too near their entrance when they are not the goal



**Symptom.** The route crosses wompah pads (server teleports the body â€” an unplanned zone hop)

or squeezes past a wompah entrance within trigger range on trips where the wompah is not the goal.



**Likely cause.** Wompah pads and ramps are flat walkable surfaces â€” the lattice rightly has them

as levels, and nothing tells the planner a pad is a *trigger*. The teleport knowledge exists in

two places, unused by the lattice: Zoning.json's statels with teleport functions, and the

1000013 records' own `TeleportDestPf`/`LocalizerType`/`LocalizerInstance` (already carried per

AOCL chunk in meshes.bin, and per record in the extractor).



**Fix.**

1. At lattice build time (or grid load): stamp wompah/teleport cells into a **teleport set**

   (pad cells + a trigger-radius disc around each entrance).

2. Price, don't block: `RefusedCellWeight`-style extra cost per teleport cell â€” routed through

   only when the goal is the teleport itself (goal in the pad disc â‡’ no cost).

3. Long-term (Option B territory): model teleports as off-mesh connections â€” crossing costs the

   weight and carries the destination.



**Test.** Plan routes that pass near a known wompah (ICC/Andromeda towers, Avalon pads): the

chosen path keeps â‰¥ trigger-radius from the entrance unless the goal is the wompah; a route

*to* the wompah still lands on the pad.



---



## 2. Zone-in puts the bot on the wrong y-lattice level?



**Symptom.** Andromeda (655) 2026-10-12 23:07: proxy crossing into the ten-deck tower area

(levels 36.1 â€¦ 152.6 m in one cell); the bot's first walk diverged 16 m from the server's

ground-level corridor â€” 3 SetPos yanks over 15 s, then self-healed (re-plans converged,

zero yanks afterward). Data verified right: `floor +0,0 m vs data` on every SetPos.



**Likely cause.** At the zone-in cell the route's start **level choice**: the bot's believed Y

after the proxy crossing may not be the level the server placed it on, so the first segment

walks a different level's geometry (deck approach vs ground corridor).



**Fix.**

1. The server's zone-in SetPos IS the truth: after a zone-in/proxy crossing, pin the walk's

   start floor to the lattice level nearest the SetPos Y (already measurable â€” the yank log

   now prints walk-Y vs server-Y, added 2026-10-12).

2. NearestFloor at zone-in cells: prefer the LOWEST matching level when several are within

   tolerance (ground before decks).

3. If divergence persists: hold movement until the first server position after the zone-in

   arrives, and seed the route from *that* point (the server places the body before we plan).



**Test.** Repeat the Newland City â†’ Andromeda crossing and the ICC tower entries: yank count

at zone-in = 0, and the yank log (when forced) shows walk-Y â‰ˆ server-Y on the same level.



---



## 3. Walking on TOP of mission entrances — ROOT CAUSE FIXED 2026-10-12 (verify live)



**Root cause (capture pf 551 23:23, entrance C0360227).** NOT the route, NOT the lattice: the

bot's broadcast y was frozen at 39.81 for the ENTIRE capture (all 719 moves) while walking 32 m

toward a 24.8 m entrance. The lock: FloorY's guard `|h - y| > 4 -> return y` was symmetric —

zoned in 15 m above the entrance floor, every step's floor was "too far away" and replaced by

the body's own height. Self-locking: once >4 m above the floor, the floor is unreachable.



**Fixed**: FloorY is asymmetric now — climb >4 m still refused (roof/cave), descent follows the

data floor capped at 15 m/step (deep-artifact guard; outdoors no fall damage). Build green.



**Still open (verify on the live run).**

1. The live entrance walk at a sloped-side house: bot descends and enters at floor level.

2. The APPROACH-DIRECTION problem remains as designed below (front axis + via waypoint + roof

   pricing) — the FloorY fix removes the altitude lock, not the "circle to the front" preference:

   1. Per entrance anchor (Zoning.json missionEntrances has positions, not facing): derive the

      **front axis** — the doorway is a hole in a wall face; the wall's steep triangles around

      the door (meshes.bin, band ±6 m, height door-floor .. door-floor+4) give the wall plane,

      its normal = approach axis. Precompute per entrance at lattice build / grid load.

   2. Route constraint when the goal is the entrance: the path must pass through

      **front-waypoint = door + front·(4-6 m) at the door's floor Y** before the door cell —

      a mandatory via, like FloorGrid's doorway standoff-door-exit waypoints.

   3. Price the roof: footprint cells whose level is > ~2 m above the door's floor Y get

      climb-cost (not blocked — the client CAN climb them; the bot just shouldn't).



**Test.** Live: the entrance walk at a sloped-side house descends to floor level and enters.

Then the front-axis work above (unchanged). Regression: the y=67.25 walk stays clean.



---



## 4. Missions: bot hugs the walls and sometimes gets pulled back



**Symptom.** Inside missions the route runs flush along walls; the server occasionally

SetPos-corrects (the character model clips the wall the pivot test doesn't see).



**Likely cause.** The calibrated collision is a pivot POINT at band [0.2, 0.6] â€” no radius â€”

so the lattice treats wall-adjacent cells as fully open and the string-pull hugs surfaces.

The pull-backs prove a *practical* model radius (~0.1-0.3 m) plus visual clipping: the server

corrects when the model intersects.



**Fix.**

1. **Erode, don't price**: in the mission lattice build, apply Recast `agentRadius` 0.3 m

   (or erode walkable cells within 0.3 m of a NULL-area column post-build) â€” doorways 1.6 m

   wide stay passable (1.6 âˆ’ 2Ã—0.3 = 1.0 m corridor), walls get a hard keep-out the pivot

   cannot cross. Client-faithful caveat: this is stricter than the client's own point test â€”

   deliberate, it kills the pull-backs.

2. Verify the mission-grid side too: NorthboundGrid/FloorGrid rooms need the same erosion or

   clearance pricing (OverlandGrid has StampClearance + wall-hug cost; the mission grids may

   not price it â€” check `FloorGrid`'s search costs).

3. Keep `ClearWeight`-style hug cost on top: erosion = can't touch, cost = prefer the middle.



**Test.** ICC/Andromeda tower missions (the wall-heaviest routes): SetPos count inside

missions â‰ˆ 0 over a full run; route length increase vs today â‰¤ ~10%; doorway transit still

works (the 1.6 m subway door is the regression gate).



---



## Done today (context for the above)



- Lattice fleet: 336 outdoor pfs, 98.3% walkable, wire-validated (0.23-0.24 m median).

- Calibrated: AO collision = pivot point, band [0.2, 0.6]; walkableHeight 0.6; no radius.

- Yank log now records walk position+Y vs server SetPos (feeds item 2's diagnosis).

- **FALLS IMPLEMENTED in WalkStep** (Scotty-warp case: warped onto a tree canopy or a column): the step's floor >4 m below the body (and not in water) = a fall — x/z freeze at takeoff, movement SILENCE for √(2h/25) (g=25, measured), then the landing announcement at the data floor. A server SetPos mid-fall RE-ANCHORS: its y is the truth — keep falling from it (recomputed drop, fresh countdown) or land if it sits on ground.
- **ICC "swimming on the ground" root-caused + fixed (2026-10-13)**: the pin escalation (4 chained SetPos pins -> enter the swim "to cross water the data calls dry") fired on the proxy crossing's placement burst — dry ground, inside the crossing structure's mesh. SwimY grid at the crossing: ALL DRY (the liquid polygons are not the trigger). Fixed: the escalation can't fire inside the proxy settle window (20 s after zone-in), and the pin chain resets at zone-in. The post-teleport "no movement" was the drift-guard deadlock — now bounded (15 s hold, stand-up probe, then resume on the data floor; a real rejection still surfaces as a SetPos rubberband).

