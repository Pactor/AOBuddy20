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

## VERIFIED buff tiers + receiver level locks (2026-10-05, from nanos.ocp cast criteria)

The all-6-nano-skill **Composite** buffs are RECEIVER level-locked AND require Shadowlands
(the lower `Level >` of the two in each nano's ToUse is the receiver gate; the higher is the
casting MP's):

| Buff | tell | +skill (all 6) | Receiver needs | NCU |
|---|---|---|---|---|
| Composite Teachings | compt | +25 | L15 + SL | 6 |
| Composite Mastery | compmast | +50 | L40 + SL | 13 |
| Composite Infuse With Knowledge | cominf | +90 | L90 + SL | 25 |
| Composite Mochams (1h) | compmoch1 | +140 | L175 + SL | 48 |
| Composite Mochams (2h/4h/8h) | compmoch2/4/8 | +140 | L201 / L205 / L209 + SL | 51/54/55 |

The **SINGLE-SKILL Mocham's Gift** buffs are the +140 you get at low level - **NO level req,
NO SL** (only PM/SI ~744 + VisualProfession 12 on the casting MP):

| Buff | tell | +skill | NCU |
|---|---|---|---|
| Mocham's Gift: MatCrea | mcmo | +140 MC | 51 |
| Mocham's Gift: SpaceTime | stmo | +140 TS | 50 |
| (BioMet/MatMet/PsyMod/SenseImp each +140) | bmmo/mmmo/pmmo/simo | +140 one skill | 50-52 |

So to lift **MC and TS** for pet summoning at ANY level: `mcmo` (+140 MC) + `stmo` (+140 TS),
each ~50 NCU, plus a wrangle. The all-skills composites add ON TOP (different nano strains:
Mocham's Gift = strain 159/161, Composite = strain 165, wrangle = strain 220 - all stack) once
you meet their level. **ALWAYS strive for the +140** (the player max), single-skill if the
all-skills tier is out of level (owner, 2026-10-05).

Wrangle: **Skill Wrangler (Premium) = +131** (tell `131`, 58 NCU, raises MC & TS); the *Team*
variant is +132 (tell `132`) - use +131.

**Worked example (L50 Engineer, raw MC/TS 326, 148 NCU):** NCU buff first (the stack needs it:
mcmo 51 + stmo 50 + wrangle 58 = 159 > 148) -> mcmo +140 -> stmo +140 (MC/TS 466) -> wrangle
+131 (597 PEAK). Durable (no wrangle) = 466 -> control max 466/0.80 = 582 -> **summon the best
req <= 582 = Semi-Sentient Guardbot (569, held at 82%)**, NOT Patchwork Warbot (596, would OE at
78%). Wrangle off -> 466, pet holds, free 58 NCU for survivability. The current code does NOT do
this yet: it excludes single-skill buffs, has no NCU math / sustain-gate / downshift (4a.2-full
+ 4b TODOs).

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
