# PetBrain design (AOBuddy20)

Written 2026-10-04. The plan for a 4th brain family — pets — and how the three pet
professions fit one abstraction. Rules: [rules.md](rules.md). Every pet fact below is
backed by the client data in `GameData/profiles/*-pets.json` and `*-nanos.json` (OmniCell
`nanos.ocp`, client 18.8.50_EP1), or by `GameData/profiles/reference/professions.json`.

## Why a 4th BrainKind

The brain framework (Algorithman, 2026-10-02) has three families: `Combat`, `Selfbuffing`,
`ExternalBuffing`. Pets are none of these. A pet is not a self-buff (it is a persistent
dynel we own and command), and pet-combat is not our weapon combat (the server runs the
pet's swings; we only assign its target). It needs its own family: **`BrainKind.Pet`**.

Decision (owner, 2026-10-04): **pets are a 4th `BrainKind`.** First profession: **Engineer**.

## The pet professions — the data says FIVE, not three

Determined by running the nano extractor (`tools/mp-nano-extractor`, `--prof`) over ALL 14
playable professions and reading the hard `Profession`/`VisualProfession` cast gates — NOT
from `professions.json`'s prose blurbs or from memory, both of which proved incomplete
(owner directive, 2026-10-04: "extract ALL data"). The verification that forced this: the
Trader and Adventurer charm/summon nanos carry hard `Profession == 7` / `== 6` gates and do
NOT appear in any other class's file, so they are real, not over-tagging.

**Commandable / charmable pet professions (summon or charm a controllable pet):**

| Profession | id | Pet evidence (hard-gated) | Shape |
|---|---|---|---|
| **Engineer** | 3 | Robot 90, Tower 1, Pet-Heal 10, Pet-Buff 36 | 1 attack robot |
| **Meta-Physicist** | 12 | Attack 51, Heal 10, Mezz 17, Pet-Buff 40 | up to 3, role-typed |
| **Bureaucrat** | 8 | Bots 65, Charm 37, Pet-Heal 3, Pet-Buff 37 | summon bot **+ charm NPC** |
| **Trader** | 7 | Charm 11 (`CharmOther` "Brain" line) + 1 endgame summon (`Decision by Committee`, bot 220, Prof==7, SL, L220) | charm + 1 late summon |
| **Adventurer** | 6 | Charm 4 (`Leet Friend`→`Soleet Friend`, Prof==6, `MonsterData==17655`) | **charms leets**, tiered by target level |

**Deployables (profession-gated, autonomous — NOT a commandable combat pet):**

| Profession | What | Count |
|---|---|---|
| Engineer | Jamming Tower | 1 |
| Fixer | Summon Shadowweb Spinner MK I–V+ | 55 |
| Adventurer | Spirit Tech Source Projector / Fountain | 10 |

**No pets (verified, 0 hard-gated pet/charm nanos):** Soldier, MartialArtist, Enforcer,
Doctor, NanoTechnician, Keeper, Shade.

- **Adventurer DOES charm** — the owner was right. The `Leet Friend` line (ids 161674,
  161676, 161678, 161680, …) is `Profession == 6` and gates the target on `MonsterData ==
  17655` (the leet) at tiered level bands: you charm a leet. `professions.json`'s "morphs,
  self-heals" blurb simply omitted it.
- **Agent** has no native pet. Its only path is *False Profession* (VisualProfession 368)
  temporarily casting another class's pet nanos — transient mimic. Brain selection keys on
  real `Stat.Profession` (60), so an FP'd Agent would not select a pet brain. Out of scope.
- **Charm ≠ a monolith.** Three unrelated charm systems: Bureaucrat (broad NPC charm, long/
  short), Trader (`CharmOther` humanoid "Brain" swap), Adventurer (`Leet Friend`, leets
  only). Same `Source = Charmed`, different policy and target rules per class.

## The distinctions that shape the abstraction

Five axes, all data-backed:

1. **Creation.** Summon via `SpawnItem` (FunctionType 53064 — Engineer robots, Crat bots)
   or `SummonPet` (53167 — MP). OmniCell's server source documents the two as identical in
   call shape. **Charm** via `CharmNpc` (53127 — Bureaucrat only): takes an *existing* NPC
   and converts it into our pet. This is the one creation path that is not a summon.
2. **Role.** attack / heal / mezz(stun) / deploy. Only the MP fields role-specialised pets
   simultaneously. Engineer = attack only. Crat bot = support/attack; charmed mob = attack.
3. **Cardinality.** Engineer 1; MP up to 3 (one per role); Crat 1 summoned bot + a charm
   ceiling (AO-U: 1 long + 1 short, or 2 short — long unusable while a short charm is held).
4. **Control.** Commandable pets expose the full command vocabulary (below). Contrast the
   cross-cutting category of **simple/vanity followers** (item/perk summons that follow and
   may fight autonomously but expose **no** command channel). These are **explicitly out of
   scope** — there is nothing to command.
5. **Constraint.** Engineer over-equip (OE): an over-equipped bot **stops obeying** and
   drops behind — MC/TS must stay ≥ 80% of the summon requirement. MP: wrangle/over-cast to
   run higher pets. Crat: charm resist / level gate; short charm is timed.

**Mezz nano ≠ mezz pet.** Crat (25) and NanoTech (16) have crowd-control *casts*
(`Mezz`/`Daze`/`RestrictAction`); only the MP has a mezz *pet*. Mezz-as-a-nano lives in the
combat/selfbuff families, mezz-as-a-pet lives here. Keep them apart.

## The model

A pet brain manages a **roster of slots**. One slot:

```
PetSlot {
    Role        : Attack | Heal | Mezz | Deploy
    Source      : Summoned | Charmed
    Identity    : the pet dynel (null while the slot is empty / pending)
    Commandable : bool        // false for a vanity follower that wandered into range
    Obeying     : bool        // Engineer OE: a disobeying bot is effectively lost
}
```

- **Engineer** = one `{Attack, Summoned}` slot. Simplest case → right first target.
- **MP** = three slots, one per role, all `Summoned`.
- **Bureaucrat** = one+ `{_, Summoned}` bot slot plus one+ `{Attack, Charmed}` charm slots
  with the long/short ceiling.
- **Trader** = one `{Attack, Charmed}` slot (the Brain line) and, at endgame, one
  `{_, Summoned}` slot (`Decision by Committee`).
- **Adventurer** = one `{Attack, Charmed}` slot (a leet).

`Source = Charmed` is used by Crat, Trader and Adventurer — three different charm systems,
one slot source. **Deployables** (Engineer tower, Fixer Shadowweb Spinner, Adventurer
source projectors) are autonomous and expose no command channel; model them as `Deploy`
role with no command engine, or keep them out of `PetBrain` entirely — decide per class, do
not force them through the commandable-pet path. Fixer and Engineer-tower-only deployment
is a separate question from the five pet professions above.

## ENGINE (shared base `PetBrain`) vs POLICY (per-profession subclass)

Follows the exact `CombatBrain`/`SelfbuffingBrain` house pattern: an abstract base carrying
the wire-proven engine, with `virtual` policy seams that default to doing nothing (so a
`GeneralPetBrain` fallback is dormant, like the other generals).

**ENGINE (base, invariant):**
- The roster and slot lifecycle (empty → pending-summon → alive → lost on death/zone). Each
  slot records its `Source` (`Summoned` | `Charmed`) — **the pet knows how it was made**
  (owner, 2026-10-05): re-acquire re-summons the nano for a `Summoned` slot, re-charms a
  fresh target for a `Charmed` one, and the two gate on different skills.
- The **PetCommand vocabulary** — **already in the AOBuddy20 SDK**, nothing to port:
  `PetCommand` (Follow/Behind/Wait/Guard/Attack/Social/Terminate/Release/Heal/Report/Chat),
  sent via `LocalPlayer.CommandPets(cmd)` / `CommandPets(cmd, ids)` →
  `PetCommandMessage`. All pets take the same commands; the MP heal pet simply ignores
  attack (it just heals) — and the server already labels it, see below.
- **Pet roles come from the wire**, not from guesswork: `NpcChar.Role` (`PetType`
  Attack/Heal/Support/Social) is read live from each pet's `SimpleCharFullUpdate`
  (verified vs ~100 pets in sniffs). `LocalPlayer.Pets` = the NPCs the server says we own.
  A post-summon `ExpectingPetUntilMs` window (12s) already claims a fresh summon.
- **Summon** through `NanoLibrary` (data-driven cast of a chosen nano id; same cost/NCU
  gating the `SelfbuffingBrain` engine already encodes).
- **Obey/OE tracking** (Engineer): watch the summon requirement vs live MC/TS.
- **Re-acquire** on pet death and on zone (pets drop on zone — re-summon / re-charm).
- Arbiter hold at `ControlPriority.Pet` while an episode is open (take on open, release on
  close — the `HealController` pattern).

**POLICY (subclass, overrides):**
- *What* to summon and *when* (the ladder: best affordable pet ≤ my skills/level).
- Role assignment (MP: which pet does what; heal pet targets owner/team/pets).
- The **charm flow** (Crat only): pick an NPC target, cast `CharmNpc`, promote the result
  into a `Charmed` attack slot, respect the charm ceiling.

`EngineerPetBrain` (first): keep one attack robot up — summon the best bot my MC/TS/level
and credits allow, re-summon on death/zone, hold MC/TS ≥ 80% of the summon req (OE guard),
and run the seven-step pet-heal line on the bot (`Breed == 7` targets the pet). All ids come
from `engineer-pets.json` (91/91 summons, 10 pet-heals, verified vs `itemnames.sql`).

## Buff-first summon — the Engineer core (owner, 2026-10-05)

A better pet needs more Matter Creation (130) + Time & Space (131) than the character has
raw, and those skills are propped up by buffs. If a buff drops, the pet goes over-equipped,
**stops obeying and is effectively lost**. So summoning is not "cast the best nano I can
right now" — it is a sequence, and we **stay petless until it resolves** ("not before").

The full MC/TS buff landscape — every buff that raises them, with amounts, level and paid
(Shadowlands) gates, the +131/+132 Trader wrangle, the wrangle "trap," the NCU filter and the
OE-threshold research — is written in stone in
[reference/skill-buffs-mc-ts.md](AOBuddy20/GameData/profiles/reference/skill-buffs-mc-ts.md).
Key design facts from it: the **Engineer has no self-cast MC/TS buff** (lift is external only);
buffs must **fit free NCU** or they cannot be held (never request what won't fit); a short
**wrangle-summoned pet is a trap** unless an equivalent durable buff is kept up. The sequence:

The governing fact (owner, 2026-10-05): **summoning needs 100% of the pet's skill req;
keeping control needs only 80%** (OE% ≤ 20). So the peak buff stack is needed only for the
instant of the summon — after that we downshift to cheaper buffs that still hold 80%, and the
**NCU we free then buys survivability.** NCU is the real currency.

0. **GO/NO-GO GATE (owner, 2026-10-05): can we SUSTAIN 80%, not just hit 100%?** A pet is a
   valid target only if, after summon, the buffs we can **hold durably within NCU** keep
   `margin = min(buffedMC/reqMC, buffedTS/reqTS) ≥ 0.80`. Reaching 100% at peak to summon is
   not enough — if we cannot sustain 80%, the pet goes OE and is lost, so **do not summon it;
   drop to the next pet down whose 80% we CAN sustain.** Peak-reachable gates the summon;
   sustainable-reachable gates the choice.
1. **Determine the reachable ceiling — and whether we even need the dance.** Best pet whose
   MC/TS req ≤ (raw + sustainable buffs we can hold in NCU)? If that already covers the pet we
   want (no overbuffing needed), skip to step 5 and just summon it.
2. **Only if the pet we want requires overbuffing — SUMMON PHASE (peak):**
   - **Leave team** (the buff-bot handshake requires not being teamed).
   - **NCU buffs FIRST** — Codedoc/Chewy +NCU (≈ +40/+60/+90, level-scaled, level-locked;
     request the highest our level allows). This raises MaxNCU so the skill buffs fit.
   - **Then stack the highest skill buffs we can** (composites) **+ a Trader wrangle if still
     short**, up to **≥ 100%** of the pet's MC/TS req — each buff fitting free NCU.
3. **Summon** the chosen pet. Not before.
4. **DOWNSHIFT / MAINTAIN PHASE:** the pet now needs only **80%** (OE% ≤ 20), not 100%.
   Drop the expensive high-NCU skill buffs and swap in the **cheapest buffs that still hold
   both MC and TS ≥ 80%** of req. **Sequence the swap so skill never dips below 80% mid-swap**
   — land and confirm the cheaper buff, *then* drop the big one (overlap, don't gap). Spend the
   freed NCU on **survivability** (Behemoth HP, the heal-DoT "kitchen sink" lines, etc.).
5. **Just-summon path / steady state.** When no overbuffing is needed, summon directly. Either
   way, continuously watch `margin = min(buffedMC/reqMC, buffedTS/reqTS)` and re-buff before it
   nears **0.80** — a dropped prop = OE = disobedient pet. Continuous obligation, not one-shot.

The buff-bot handshake (ExternalBuffingBrain, the Scotty/invite pattern): not in team → accept
the buffer's team invite → he tells "preparing to buff you" → request the level-locked buff we
qualify for → **the bot auto-kicks us from the team once buffed** (no manual leave — teardown
is automatic; just be un-teamed to accept the invite in the first place). The owner's own team
invites stay manual. Buff-bot offerings (Codedoc NCU/skill lines) are per-server and come from
a sniff — see [[rk2019-codedoc-buffs]].

The buff step depends on the **`ExternalBuffingBrain`** family (Chewy/Codedoc routing), and
on self-buff through the `SelfbuffingBrain`. PetBrain *waits on* whichever applies. This is
the one hard prerequisite: today ExternalBuffingBrain is dormant and its team/invite packets
are not wired, so full buff-first summon cannot function until that lands — which sets the
port order below.

## What already exists in AOBuddy10 (this is a PORT, not new work)

The behaviour is built and battle-tested; the AOBuddy20 task is to re-home it into the brain
framework cleanly and slowly. Do NOT edit these in place (rules.md #9, [[algorithman-collaboration]]) —
port the logic.

| AOBuddy10 file | Lines | Carries |
|---|---|---|
| `PetController.cs` | 1114 | summon best-castable per line (`MeetsUseReqs`, StackingOrder=best), re-summon on death, role-based commands, pet buffs, "summon better once buffs lift nano skills" (line 437 = step 2 above) |
| `HuntController.cs` | 384 | pets hunt mobs in a leash radius around the bot; owner's fight wins; set-aside unreachable; respawn |
| `ChewyBuffs.cs` / `CodedocBuffs.cs` | 1166 / 928 | the buff-bot routing that raises nano skills (step 1) |
| `SupportController.cs` | 1900 | the cast queue + keep-buffs engine the self-buff fallback needs |

**Hunt** becomes a `HuntController` alongside the other controllers (a mode that feeds the
attack-target), not a brain. **Team mirroring** (owner commands their pets → the bot issues
the same command to its pets, when teamed) is a `PetCommandMessage` observer, the same
mirroring pattern as follow. **Cosmetic pets get no brain** — `PetType.Social`, we neither
own nor command them.

## Where it ticks

`BotLoop`'s chain is heal(800) → combat(700) → selfbuff(600) → resupply(500) → mission(400)
→ extbuff(300) → sell(200). Pet needs summon/maintain/command continuously, and its
attack-target should track our combat. **Decision: `ControlPriority.Pet` just below combat**
(the pet follows the fight) — ours to set now that Brains is ours (below).

## Port order (slow and clean, one verified step at a time — rules.md #7)

1. **Framework only:** `BrainKind.Pet` + `PetBrain` base + dormant `GeneralPetBrain`. No
   behaviour. *(Brains is ours now — the framework edits below are ours to make.)*
2. **`EngineerPetBrain` summon + maintain + command**, assuming skills are already high
   enough (no buff routing yet). Gets a working, commanded robot on the simplest path.
3. **Port `HuntController`** → pets fight in a radius.
4. **Buff machinery** (split):
   - **4a.1 DONE** — `BuffBotController`: the public-buff-bot handshake plumbing (accept the
     bot's invite, send request tells, auto-kick), conf-driven. `buffs start|stop|status`.
   - **4a.2-pet DONE** — `BuffCatalog` (ChewysBuffs.json) + `PlanForPetSummon`: NCU buff first,
     then one safe MC/TS buff (all-nano composites + the Skill Wrangler ladder, froob-receivable).
     `buffs pet`.
   - **4b DONE (summon side)** — the EngineerPetBrain is buff-first: when a better robot is
     learned but gated only by MC/TS, it asks the buff bot (`PlanForPetSummon` for that pet's
     req) and waits, rather than summoning a weaker one — opt-in via `PetAutoBuff` (off: it logs
     the opportunity and summons the best it can now). Once the buffs land, skills rise and the
     normal summon picks the better pet. The **downshift** half (reclaim NCU after summon) and a
     true buff-durability **sustain-gate** are still TODO (they need buff-timer tracking).
5. **Team pet-command mirroring.**
6. **MP** (three wire-labelled role slots) and the **charm** classes (Crat / Trader / Adv)
   as further subclasses on the same base.

## Combat mode — the player's choice (owner, 2026-10-05)

Fighting alongside the pets is NOT a fixed rule; it is the player's choice, informed by his
profession template. Two modes:

- **Pet-tank / passive** (what the hunt work built): the bot stays put and does NOT attack;
  the pets fight, and the bot swings back only when something aggros it. For max-level or
  dangerous mobs the player could not survive in melee - "that is why we have the pets."
- **Fight-alongside**: the bot attacks WITH the pets (e.g. an Atrox MP on 2h blunt, the heal
  pet keeping him up), for content he can handle himself.

It is a TOGGLE, defaulting sensibly off the template (does the character have a weapon /
melee skills) but ultimately the owner's call - never auto-forced. Architecturally the bot's
own weapon is the `CombatBrain` (still dormant) and the pets are the `PetBrain`; they COMPOSE
(both priority-gated ticks). The toggle decides whether `CombatBrain` is **active** (the bot
swings) or **defensive-only** (the bot swings back only when a mob is on it - the current
pet-tank behaviour). The hunt/pet work is the pet half; lighting up `CombatBrain` with this
toggle is the bot-melee half (a later step).

## TODO — come back to these

- **4a.2-full (the full buff optimizer):** proper RECEIVER-requirement gating (level/expansion
  read against our own stats, so level-gated buffs — Umbral wranglers, multi-hour Mochams — come
  back in safely), the real NCU math (fit each buff to free NCU after the NCU buff expands Max
  NCU), single-skill two-tell plans (MatCrea + SpaceTime separately), and offense/defense/sustain
  scoring (the general buff optimizer, not just pets). AOBuddy10 `ChewyBuffs` (1166 lines) is the
  reference.
- **4a.3 — Codedoc (RubiKa2019):** its level-locked model differs from Chewy; `CodedocBuffs.json`
  + `codedoc-given.json` / `codedoc-level.json`. `BuffCatalog` loads it best-effort only for now.
- **/assist — the owner's target:** the owner's bare SELECTION is not on the wire (LookAt is
  client→server, not broadcast); only `FightingTarget` (on engagement) is observable. /assist is
  the server-resolved mechanism but is NOT in the SDK — needs a sniff of a /assist to decode the
  message, then wire it into `HuntController.OwnerFightTarget`. See [[owner-target-assist]].
- **4a.1b — travel to the buff spot** before the handshake (the bot casts at range).
- **Buff-first downshift** (reclaim NCU for survivability after the summon) — part of 4b.

## Files

New (ours):
- `Brains/PetBrain.cs` — the base engine + policy seams.
- `Brains/GeneralPetBrain.cs` — dormant fallback (`[Brain(BrainKind.Pet)]`).
- `Brains/EngineerPetBrain.cs` — `[Brain(BrainKind.Pet, Profession.Engineer)]`.
- `Controller/HuntController.cs` — ported from AOBuddy10 (step 3).

Framework edits (Brains was passed to us, 2026-10-05 — these are ours to make directly):
- `Enums/BrainKind.cs` — add `Pet`.
- `Brains/BrainBank.cs` — `Pet` property, `TickPet`, selection in `EnsureSelected`.
- `Brains/BrainRegistry.cs` — `BaseFor(BrainKind.Pet) => typeof(PetBrain)`.
- `BotLoop.cs` — the pet tick in the priority chain (`ControlPriority.Pet`, just below combat).

Note: the AOBuddy10 `PetController`/`HuntController`/buff files are still reference to PORT from,
not edit — but the AOBuddy20 Brains subsystem itself is now ours to change.

## Decisions (Brains is ours — settled here)

1. **Framework edits are ours** to make directly (no coordination gate). ✔
2. **Pet ticks just below combat** (`ControlPriority.Pet`), so the pet follows the fight. ✔
3. **Pet-combat lives wholly in `PetBrain`**; the owner's own weapon, if any, is a separate
   `CombatBrain` — they compose, since both are just priority-gated ticks. ✔
4. **Pet command channel** — already in the SDK (`PetCommand`, `CommandPets`, `PetType` from
   the wire). ✔
