# MC / TS skill buffs — the pet-summon enablers (written in stone)

Matter Creation (stat 130) and Time & Space (stat 131) gate every attack-pet summon. This is
the complete landscape of what raises them, extracted from `nanos.ocp` (client 18.8.50_EP1)
by scanning all 10,965 nano formulas for a positive `Modify` (FunctionType 53045) on stat
130 and/or 131, then reading each one's `ToUse` requirements. **160 nanos** raise MC/TS;
**145** raise both. Every id below is from the data — the bot computes the reachable ceiling
from `NanoLibrary` at runtime, it does not hardcode these (rules.md #4). This file is the
reference so we know what the runtime is choosing among.

Verified 2026-10-05. Level shown as the raw `Level > N` from the cast action. Expansion gate
`[SL]` = Shadowlands (`Expansion op22 2`) = **requires a paid account**.

## VERIFIED receiver gates (2026-10-05, dumped from nanos.ocp cast criteria — CORRECTED)

Each cast action's requirement quintuples carry a **target** field that says whose stat is
tested: **`target=19` = OnSelf = the CASTER**, **`target=18` = OnTarget = the RECEIVER (us)**.
`stat 389 BitAnd 2` = Shadowlands (paid) flag; `stat 368/60 == N` = caster profession gate
(12 MP, 9 Enforcer, 7 Trader). So a buff's **receiver** gate = ONLY its `target=18` criteria.
Everything on `target=19` is the buff bot's problem, not ours.

**CORRECTION of the earlier version of this doc:** the Composite line is NOT receiver
level-locked. The L15/L40/L90/L175/L201 numbers are the **casting MP's** level (`target=19`),
not the receiver's. The ONLY receiver gate on the Composite line is `target=18` **Expansion
(SL)** — i.e. **the receiver just needs a paid (Shadowlands) account, at ANY level.** A maxed
Chewy therefore lands Composite Mochams +140-to-all-six on an L51 paid toon.

| Buff | tell | +skill | strain | **receiver gate (target=18)** | NCU |
|---|---|---|---|---|---|
| Mocham's Gift: MatCrea | mcmo | +140 MC | 159 | **none** (froob-ok, any level) | 51 |
| Mocham's Gift: SpaceTime | stmo | +140 TS | 161 | **none** | 50 |
| Infuse: MatCrea / SpaceTime | mci/tsi | +90 | 159/161 | none | 40/40 |
| Mastery: MatCrea / SpaceTime | mcma/stma | +50 | 159/161 | none | 14/13 |
| Teachings: MatCrea / SpaceTime | mcte/stte | +25 | 159/161 | none | 7/7 |
| Composite Teachings/Mastery/Infuse/Mochams | compt/compmast/cominf/compmoch* | +25/+50/+90/+140 all 6 | 165 | **SL only (NO level)** | 6/13/25/48-55 |
| Composite Nano Expertise | c2 | +20 all nano | 91 | none | 4 |
| Composite Attribute Boost | c1 | +12 all attributes | 34 | none | 4 |
| Skill Wrangler (Premium) | 131 | +131 (short) | 220 | none | 58 |
| Essence of Behemoth | es2 | +996 HP/+27 str/sta | 151 | **none** (any level) | 47 |
| Improved Essence of Behemoth | es1 | +1998 HP/+54 | 151 | **Level >= 215** | 56 |
| NCU line (LANDED nano, e.g. 162994) | ncu | +Max NCU | 257 | **receiver Level tiers** (L25->+40 ...) | ~1 |

**Single-skill line = one strain per skill** (MatCrea all 159, SpaceTime all 161), so Mocham's/
Infuse/Mastery/Teachings for a given skill are **SWAPS, not stacks** — you hold exactly one rung.
The Composite line (strain 165) is a DIFFERENT strain, so it stacks on top of a single-skill
rung; `c2` (91), `c1` (34) and the wrangle (220) each stack too.

Wrangle: **Skill Wrangler (Premium) = +131** (tell `131`, 58 NCU); Team variant +132 (`132`).
Use +131. It is **short-duration** — never counted toward durable control.

## Ability trickle into MC/TS (VERIFIED from GameData/SkillTrickle.json, factor order [Str,Agi,Sta,Int,Sen,Psy])

- **MC (130):** Stamina **0.2**, Intelligence **0.8** (rest 0)
- **TS (131):** Agility **0.2**, Intelligence **0.8** (rest 0)

`trickle = floor( Σ(ability × factor) / 4 )`. Both skills' factors sum to 1.0 and are Int-driven.
A uniform **+X to all attributes adds `floor(X/4)`** to MC and TS, so **Composite Attribute Boost
(+12 all) = +3 MC / +3 TS** (clean, no rounding slop). A buff on one ability only is less (e.g.
+12 Int -> +2 MC; +12 Sta -> +0 MC). This is why "the trickle is sometimes 1 point" (owner). The
live `me.GetStat(MC/TS)` ALREADY includes running trickle; the planner only needs this to PROJECT
an attribute buff it is about to request.

## Worked example (L51 Engineer, raw MC/TS 326, Max NCU 148, FROOB path)

Durable rung ladder (durable = raw 326 + rung + Nano Exp 20 + attr-boost trickle +3), and the
biggest pet each rung CONTROLS at the 0.80 floor (durable / 0.80):

| rung | durable MC/TS | controls pet req <= | rung NCU (MC+TS) |
|---|---|---|---|
| +140 Mocham's (mcmo+stmo) | **489** | **611** | 101 |
| +90 Infuse (mci+tsi) | 439 | 548 | 80 |
| +50 Mastery (mcma+stma) | 399 | 498 | 27 |
| +25 Teachings (mcte+stte) | 374 | 467 | 14 |

Rule: **pick the lowest rung whose durable total clears the floor; step up only when it falls
short.** NCU buff FIRST (+60 at L50 -> Max NCU 208) so the stack fits. A QL119 pet (req 554)
needs +140 (548 < 554 <= 611); a pet req <= 548 holds on the cheaper +90, banking ~21 NCU for
survival. **Control is checked on DURABLE buffs only (no wrangle)**; the wrangle only lifts the
summon moment to the 100% cast req, then is dropped. If the durable ceiling can't hold the pet at
80%, the pet is too big — summon a smaller one (sustain-gate picks the pet by control, not peak).

## NCU buffs (Fixer NCU line, strain 257) - receiver LEVEL-LOCKED, each tier

The `ncu` tell casts the Fixer NCU ladder; you keep the best your level allows. Each version is
level-locked to the RECEIVER (owner, 2026-10-05; the nano gates only on `Level`, no prof/skill):

The ladder is one per level bracket (owner's memory, ground truth; +40/+60 and the higher
tiers confirmed from nanos.ocp):

| Receiver level | Buff | +Max NCU |
|---|---|---|
| L10 | NCU Compressor | +20 |
| L25 | Retool NCU | +40 |
| L50 | Jury-rigged NCU Analyzer | **+60** |
| L75 | Deck Recoder | +85 |
| L125 | Recompiling Memory Analyzer | +110 |
| L135 | QuarkStor NCU Core | +150 |
| L165 | Active Viral Compressor | +195 |
| L185 | Sentient Viral Recoder | +250 |
| L215 | Sync Compressor | +500 |

(Separate perk/Grid "NCU Booster" line, strain 558: +10/+23/+40/+70, stacks on top.)

**This is the binding resource for the pet-buff stack.** L50 example: NCU buff +60 -> Max NCU
148 -> 208; the stack mcmo 51 + stmo 50 + wrangle 58 = 159 then FITS (159 < 208, ~49 spare for
survivability). Without the NCU buff (148) the 159-NCU stack does not hold - that is why NCU is
requested FIRST.

## The headline facts (owner's questions, answered against the data)

1. **The Engineer cannot self-buff MC/TS.** Zero Engineer-gated (prof 3) nanos modify 130/131
   on self. The ONLY professions with a self-cast MC/TS buff are **NanoTechnician** and
   **Meta-Physicist** (below). **Consequence for the pet brain: an Engineer's MC/TS lift is
   entirely EXTERNAL** — composite nanos/cans he applies to himself, and Trader/buff-bot
   wrangles cast on him. With no buffs available he is capped at his raw-skill pet.
2. **The tiered +25 / +50 / +90 buffs you remembered are real** — the **Nano Can: Composite**
   line (and +140 at the top):
   | id | name | MC | TS |
   |---|---|---|---|
   | 292302 | Nano Can: Composite Teachings | +25 | +25 |
   | 292301 | Nano Can: Composite Mastery | +50 | +50 |
   | 292300 | Nano Can: Composite Infuse With Knowledge | +90 | +90 |
   | 292299 | Nano Can: Composite Mochams (2 hours) | +140 | +140 |
   The **amounts match your memory exactly**. **EXCLUDED from the bot's buff plan (owner,
   2026-10-05): Nano Cans cost real-world currency — overkill, do not use them.** Kept here
   only as the identification of the +25/+50/+90/+140 line you remembered; the bot's actual
   sources are the in-game `Composite Nano Expertise` line (below) and the bot/Trader buffs.
   (The per-tier item level-lock question is therefore moot — we won't cast these.)
3. **Composite buffs of 1 hour+ require paid + a level — CONFIRMED precisely** (the
   `GeneralMatMetBuff` "Composite Nano Expertise" line, +20 MC/TS, a true composite across the
   nano skills):
   | id | name | gate |
   |---|---|---|
   | 223380 | Composite Nano Expertise | `Level > 0`, no expansion (froob, short) |
   | 223382 | Composite Nano Expertise (2 hours) | **`Level > 124` + [SL]** |
   | 223384 | Composite Nano Expertise (4 hours) | **`Level > 200` + [SL]** |
   | 223386 | Composite Nano Expertise (8 hours) | **`Level > 210` + [SL]** |
   The long-duration composites are Shadowlands (paid) and level-gated, exactly as you said.
   The base one is froob and short.

## Self-cast buffs (what the bot can put on ITSELF)

Profession-gated, target self/wearer:

**NanoTechnician (prof 11):**
- 220347 Enfraam's Cortex Accelerator — MC +4 — `Level > 144` [SL]
- 220349 Izgimmer's Hippocampal Augmentor — MC +15 — `Level > 194` [SL]
- 302274 Izgimmer's Blessing — MC +180 — `Level > 200`

**Meta-Physicist (prof 12):**
- 29309 Odin's Missing Eye — MC +40 / TS +40 — `Level > 0`
- 273379 Odin's Other Eye — MC +100 / TS +100 — `Level > 0`

**Engineer (prof 3): none.** (Bureaucrat, Trader, Adventurer self-cast: none for MC/TS.)

General, self-usable (no profession gate, applies to self):
- 223402 Gift of Beta — MC +150 / TS +150 (BioMetBuff)
- 267026 / 267027 / 267031 Payment Plan / Unforgiven Debts / Accumulated Interest — +9 / +136 / +204 (NOSTACKING line)
- The **Composite Nano Expertise** line (in-game, general, self-applicable) is the Engineer's
  real self-side MC/TS source. (Nano Can: Composite is excluded — real-money, see above.)

## External buffs (cast on the bot by a Trader or a buff bot — Chewy/Codedoc)

These are `tgt=Target`; a Trader or buff bot lands them on us. The big enablers:

- **Team Skill Wrangler** (121215–121228) — froob, `Level > 0`, **+10 up to +132**
  (Weak → Premium). The bread-and-butter wrangle; no level lock on the nano.
- **Umbral Wrangler** (235054–235265, SL) — **level-locked tiers**: +40 at `>24`, +62 at
  `>49`, +82 at `>74`, +94 at `>99`, +106 at `>124`, +117 at `>144`, +125 at `>164`, +132 at
  `>184`, +140 at `>194`, +147–153 at `>204`. (This level-tiered progression may be what your
  "+25/+50/+90 by level" memory is really tracking — it is the SL wrangle, cast by a Trader.)
- **Skill Wrangler - Nano Can** (288960–288978) — the can/bot versions, +3 up to +131, `Level > 0`.
- **Deprive/Divest/Ransack/Plunder Skills Transfer** (85817–85844, 267109/267113, 275031/275032) —
  Trader skill-drain-to-self transfers, +5 up to +350. These land on the Trader, not us —
  listed for completeness.

## What this means for the buff-first summon (Engineer)

1. Reachable ceiling = raw MC/TS **+** whatever of the above is available this session
   **and fits free NCU** (next point).
2. **NCU is a hard filter (owner, 2026-10-05).** Every buff occupies NCU; a buff bigger than
   our free NCU cannot be held, so it must never be requested — asking for a +131 wrangle we
   have no room for just wastes the ask (and can bounce a buff we already hold). Compute free
   NCU = MaxNCU − running (the `SelfbuffingBrain.FreeNcu` rule already in the engine), and
   only count a buff toward the ceiling if its NCU ≤ free. Each buff's NCU is on its
   `NanoItem` (ItemData.bin) — not in this MC/TS scan; the runtime reads it per candidate.
4. Preferred lift: a buff bot (Chewy on RK / Codedoc on RK2019) delivering a wrangle / composite,
   or a Trader wrangle if teamed.
5. **No public buff available → the Engineer applies composites to himself** (Composite Nano
   Expertise / Nano Can: Composite) — there is no profession self-buff to fall back on.
6. Pick the best pet whose MC/TS requirement ≤ that ceiling, then summon.
7. The props are temporary — re-apply before they lapse or the pet goes over-equipped.

## The wrangle trap — DURABLE vs SHORT buffs (owner, 2026-10-05)

A Trader wrangle reaches **+131/+132** (Team Skill Wrangler Premium 121215, Nano Can
variants 288978) — enough to summon the best pet possible. **But the wrangle is
short-duration.** The moment it drops, the pet's summon requirement is measured against your
*unbuffed* skill, and if it now exceeds the over-equip threshold the pet goes OE: it stops
obeying and turns unresponsive, **fast**.

So the ceiling in step 1 must be split by durability:
- **Sustainable ceiling** = raw skill + buffs we can keep up indefinitely (long composites,
  self-buffs, a bot we can re-visit). The pet we *keep* must fit under this.
- **Peak ceiling** = sustainable + a short wrangle. A pet summoned at the peak is only safe if
  we can re-apply an equivalent buff before OE bites — otherwise it is a trap.

**Rule for the brain:** summon the best pet that fits the *sustainable* ceiling's over-equip
margin. Only reach for a wrangle-peak pet when a durable buff of similar size is in hand or a
re-wrangle cadence is guaranteed. A wrangle-summoned pet with no plan to refresh = a
disobedient pet minutes later.

## OE / "stops obeying" threshold — THE FORMULA (owner, 2026-10-05)

The over-equip percentage is:

```
OE% = 100 − ( BuffedSkill / PetRequiredSkill × 100 )
```

- `BuffedSkill` = our current MC (130) / TS (131) WITH all buffs on, read live from stats.
- `PetRequiredSkill` = the summon nano's MC/TS cast requirement (we already read it from the
  nano's `ToUse` action).

Behaviour:
- Skill ≥ requirement → `OE% ≤ 0` → full effectiveness, fully obedient.
- Skill between 80% and 100% of requirement → `OE% 0–20` → works, with an over-equip penalty.
- Skill < 80% of requirement → `OE% > 20` → **the pet stops obeying / goes unresponsive.**

So the control cutoff is **OE% ≤ 20, i.e. buffed skill ≥ 80% of the pet's required skill**,
and it applies **per skill — MC and TS each.** The brain can compute this live every tick from
(buffed MC/TS stats) and (the chosen summon nano's MC/TS reqs), and act before OE% climbs
toward 20 as a buff decays.

**This makes the "maintain buffs" step measurable, not a guess:** for the pet currently up,
`margin = min( buffedMC / reqMC , buffedTS / reqTS )`; re-buff before that margin falls to
0.80. And it makes the wrangle trap exact: a pet summoned at peak (with a short wrangle) whose
`reqSkill` is more than `unbuffedSkill / 0.80` will cross OE the instant the wrangle drops.

Still worth a sniff pass (`sniffs/`, `captures/`) to confirm the obey transition fires exactly
at 80% on the wire and whether the penalty band is linear, but the formula above is the working
model and matches `engineer-pets.json` and the owner's account.

## Still TODO to fully close this out

- ~~Nano Can item level locks~~ — DROPPED: Nano Cans cost real money and are excluded.
- **Durations** for the froob composites and wrangles (names give the long ones: 2h/4h/8h).
- Whether the composites raise the full six nano skills (MM/BM/PM/MC/TS/SI) — only MC/TS were
  scanned here; composites by nature cover more, but the other four are unverified in this pass.
