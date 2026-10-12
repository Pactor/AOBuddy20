# BuffDryRun — OE / Equip Solver (design + status)

Status 2026-10-08. The solver answers "how do I equip/cast X at level Y" as a **path**, never "you can't."
Core pipeline works; candidate *classification* had 3 precise bugs — #1 and #2 now FIXED, #3 open (below).
Nothing in the bot is changed — this is the offline dry-run tool (`tools/BuffDryRun`, http://localhost:9090).

## The answer model (verified with the owner — this is the contract)

Lever order for closing a skill gap to equip/cast something:
1. **Buffs within current NCU** — strain-grouped (one per strain), self-cast (your profession) + cast-on-others
   (bot/wrangle), receivable only. → resulting skill.
2. If short, **the NCU answer first** (before spending money): the buff set that *would* reach the goal, its
   NCU cost, and **how much more NCU is needed**. "Need +X NCU, then hold [these buffs] → goal."
3. **Get that NCU cheaply**: NCU memory chips from a shop terminal, bounded by **belt slots** (starter belt
   holds 2; buy e.g. 2× low-QL +3 NCU chips). Checked BEFORE implants because it is cheap.
4. **Implants / gear — last resort** (money): e.g. at lvl 15 the best 2-hand implant ≈ +7 and usually costs
   MORE than the NCU chips.
5. Show a **rough credit price for both routes** (NCU chips vs implant) so the cheaper one is obvious.
6. "Can't" only when every lever is exhausted.

## Verified data facts (locked — do not re-derive; see [[buff-system-data-contract]], [[ncu-buff-tiers]])

- **Equip requirement** of a weapon/gear = the item's **action 8** criteria in ItemData (NOT UseCriteria=3).
  Includes weapon skill(s) + specials (Fast Attack/Brawl/Sneak Attack…) + profession + level + cash +
  specialization + expansion. (e.g. Ofab Wolf 301715 = 2hEdged 150, Brawl 70, FastAttack 70.)
- **Profession ownership** of a nano = the `Profession ==` requirement in its **Use** requirements (Decision
  Maker 210301 → Profession 14). No Profession req = no hard lock (27076 is school-grouped under Keeper but
  has no cast lock).
- **Self vs others** = the **per-skill ModifyStat target**: User(1)/Wearer(2)=self, Target(3)=others. The
  formula-wide `TargetsOthers` is unreliable (tripped by side-effects) — use the per-skill target. Fixed in
  NanoLibrary: `NanoProfile.ModifyTargets` / `ModifyTargetsOthers(stat)`.
- **Stacking** = **NanoStrain (stat 75)**. Same strain never stacks (higher StackingOrder/value wins);
  different strains stack. All wrangles share strain 220 → one wrangle only.
- **Directly castable** vs landed-effect: a real buff has a **cast gate** (non-empty UseCriteria). The landed
  effect-nanos (a wrangle's result, a Trader transfer's result like Divest 275032 — +350 to all skills, no
  gate) carry the numbers but have NO criteria. Exclude gateless effect-nanos from candidates.
- `>` N means the real threshold is N+1 (auno `>= 61` == data `> 60`).

## Lock / signal reference — everything the nano lib reads (the data contract)

This is the complete set of gates and signals the library (`NanoLibrary` → `NanoProfile`, from the
client-extracted `nanos.ocp` + `ItemData.bin`) exposes, and where each lives. All of it is client truth
(extracted from the 18.8.62 RDB), not OmniCell logic.

**Requirement triples** — every nano's `Actions[].Requirements` (and an item's `Criteria`) is a raw
`{Stat, Operator, Target, Value}`. The **Target** field is what separates a **caster gate** from a
**receiver/target gate** — the same stat (e.g. Level) can gate who may CAST vs who may RECEIVE. Captured:

- **Profession lock** — `Profession (368) == <id>` on the caster. No such crit = no hard class lock.
- **Level lock, caster** — `Level (54) > N` targeting the caster (N+1 real).
- **Level lock, receiver** — `Level > N` with the TARGET-tagged target: the buff's target must be ≥ N
  (Alg's receiver-gate work keys on exactly this; use-reqs gate the caster, target-tagged crits gate the
  receiver).
- **Specialization lock** — `Specialization (182) BitAnd <bits>`; froob/low-spec fails the bit.
- **Expansion / froob lock** — `Expansion (389) BitAnd <bit>` (2 = Shadowlands, also 16, 32). **Lives on
  the nano PROGRAM *or* on its CRYSTAL** (`"Nano Crystal (<name>)"` DummyItem). 148 SL nanos (Umbral type)
  carry it ONLY on the crystal — the program looks froob-castable; the client blocks the upload. The solver
  checks both (`CrystalExpansion`). A froob owns no expansion bits, so ANY Expansion-BitAnd crit blocks them.
- **Skill minimums** — `MC/TS/…` > N: the soft gaps buffs can close.

**Self vs others (per skill)** — `NanoProfile.ModifyTargets[stat]` / `ModifyTargetsOthers(stat)`: the
per-ModifyStat effect target (User(1)/Wearer(2) = self, Target(3) = others). This is the accurate
"can this skill-buff land on another player?" signal. The formula-wide `TargetsOthers` is unreliable (an
unrelated side-effect trips it) — use the per-skill target.

**Delivery / team-required** — `NanoProfile.Casts` function type of a wrapper nano: `CastNano (53051)` =
self only, `TeamCastNano (53066)` = **must be in a team** to receive, `AreaCastNano (53087)` = others in area.

**Stacking** — `NanoStrain (75)`: same strain never stacks (higher `StackingOrder (551)` wins); different
strains stack. All wrangles share strain 220 → one wrangle only.

**Directly castable vs landed-effect** — a real castable buff has a non-empty UseCriteria (a cast gate).
Gateless effect-nanos (a wrangle's landed result, a Trader transfer result) carry the numbers but no gate —
excluded from candidates (you request the gated caster nano, not its landed effect).

`>` N means the real threshold is N+1 (auno `>= 61` == data `> 60`).

## Endpoints

- `POST /solve` — the solver (below).
- `POST /plan` — pet-summon planner (Engineer, uses real BuildControlPlan).
- `GET /modifiers?stat=<Stat>&prof=<id>` — every nano modifying a skill, tagged strain/profession/self-other/NCU.
- `GET /item?ids=…` — an item's criteria by action (8 = equip).
- `GET /nanoinfo?id=…` — a nano's full record from nanos.ocp vs ItemData (incl. modifyTargets).
- `GET /ncu` — NCU buff tiers by level. `GET /allgates` — every nano's cast gates → CSV.

## Solver (/solve) — what it does

Input: character (profession, level, maxNcu, skills{}, spec, expansion) + `itemId` (equip) or `nanoId`.
1. Reads required skills + fixed gates (action 8 for items). Blocks on unmet fixed gates (prof/level/spec/exp).
2. Computes per-skill gaps vs GetStat.
3. Gathers receivable candidate buffs: modifies a gapped skill +, no debuff component, has a cast gate,
   meets the character's spec/expansion, and is deliverable (others-target, OR self with profession+caster
   skills). Best per strain.
4. Knapsack (exact, ≤16 strains): best set within current NCU, and the min-NCU set that closes all gaps →
   `extraNcuNeeded` + the cheap NCU-chip route + implant last-resort note.

## OPEN BUGS (fix next — the verdict NUMBERS are not yet trustworthy)

1. ~~**Self-cast Keeper buffs not entering the pool.**~~ **FIXED 2026-10-08.** Root cause: `ReqChecker`
   evaluates Self/User criteria against `DynelManager.LocalPlayer`, which is null in the offline tool, so
   every self-cast requirement (profession/PM/SI) failed. Fix: `BuildStub` now sets
   `LocalPlayerProxy.LocalPlayer = me`. Verified: froob lvl-15 Keeper → Ofab Wolf best-in-NCU now =
   Impartiality (self +29) + Brawl Expertise → 2hEdged 106 (was 97).
2. ~~**SL buffs not excluded for froob.**~~ **FIXED 2026-10-12.** The SL signal lives on the nano
   **CRYSTAL**, not the program: the uploadable `"Nano Crystal (<name>)"` DummyItem carries
   `Expansion BitAnd <bit>` in its UseCriteria (Param1=389, Op=22 BitAnd, Param2=2 Shadowlands / also 16, 32),
   which a froob (Expansion=0) fails, so the client refuses the upload. The program alone looks
   froob-castable. Verified over the pack: 148 "hidden SL" nanos gated only on the crystal (Umbral
   Wrangler program 235058 / crystal 235280; Intense Nano Net 218814+, Fearbringer 226059+, Shock 226301+);
   zero false positives vs classic froob families. (My earlier guesses - Flags bit 1, requirement
   "target 19" - were both coincidences and are disproven.) Fix: `ItemData.AllDummyItemIds()` + the
   solver's `CrystalExpansion(programName)` map OR'd into the candidate filter AND the target-nano gate -
   a nano is froob-locked if its program OR its crystal has an `Expansion BitAnd` the character lacks.
3. **Goal-set picks implausible mega-buffs.** With #1/#2 unfixed, the min-NCU "goal set" grabs a single huge
   wrangle (Umbral Premium +153 @53 NCU) instead of a realistic small stack (Keeper self + composites + small
   wrangle ≈ 15-18 NCU). Fixing #1/#2 should largely fix this; also consider capping per-buff amount sanity.

## NOT BUILT YET (designed, next phase)

- **NCU chips + belt capacity**: read the character's belt slot count and current NCU chips; compute how many
  low-QL chips (+N NCU each) are needed for `extraNcuNeeded`, bounded by free belt slots; rough shop price.
- **Implant alternative + pricing**: best skill-implant boost available at the character's level and its rough
  cost, shown next to the NCU-chip cost so the cheaper route is obvious.
- Both need the belt / NCU-chip / implant item data wired in (we have it; not yet read by the solver).

## Known-good hand-verified answer (the froob lvl-15 Keeper → Ofab Wolf, the reference case)
Gaps: 2hEdged +73, Brawl +1 (FA met). Best within 10 NCU ≈ 2hEdged 126 (Impartiality +29 + one +20 composite,
different strains) — SHORT 24. The buffs to reach 150 exist but need ~15-18 NCU; he has 10 → needs ~+5-8 NCU
(buy 2 NCU chips into the starter belt's 2 free slots). Implants (+~7 2hand) are the pricier last resort.
The solver must reproduce THIS once bugs #1-#3 are fixed.
