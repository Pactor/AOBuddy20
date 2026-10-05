# PetBrain work — handoff (2026-10-05)

Where we are after building the Engineer pet brain end to end. Resume from here.
Design: [PETBRAIN-DESIGN.md](PETBRAIN-DESIGN.md). Buff math (verified):
[reference/skill-buffs-mc-ts.md](AOBuddy20/GameData/profiles/reference/skill-buffs-mc-ts.md).

## Status: DONE and pushed (develop)

- **Step 1** — `BrainKind.Pet` + `PetBrain` base (roster, `DriveAttack`, OE math `OeMargin`/
  `CanControl`@0.80/`CanSummon`@1.0, arbiter at `ControlPriority.Pet`=650) + dormant
  `GeneralPetBrain`; wired into `BrainBank`/`BrainRegistry`/`BotLoop`.
- **Step 2** — `EngineerPetBrain`: two-step SHELL summon (use a shell in bags, identified
  data-driven by UseCriteria Profession+TestNumPets; else cast the best learned robot by Ql
  from `engineer-pets.json`), maintain, Follow.
- **Step 3** — `HuntController` (pets fight, bot passive): owner-assist > defend (team+pets
  guard) > proactive hunt; perma-blacklist (persisted), walled/leash set-aside (aggro clears
  temp), faction-safe (SL), pet-scaled level ceiling. `pet attack`/`pet follow` (sticky manual
  target). `hunt on|off|radius|maxlevel|faction|blacklist`.
- **Step 4a.1** — `BuffBotController`: public buff-bot handshake (invite accept from the
  configured bot, send tells, auto-kick), `buffs start|stop|status`.
- **Step 4a.2-pet** — `BuffCatalog` (ChewysBuffs.json) + `PlanForPetSummon` (NCU first, one
  safe MC/TS buff); `buffs pet`.
- **Step 4b (summon side)** — buff-first: if a better robot is gated only by MC/TS, ask the
  buff bot first (opt-in `PetAutoBuff`) and wait, else log + summon best now.
- **Fixes** — don't summon during a heal recharger rest (arbiter guard); `AccountInfo.Save()`
  merges (preserves conf comments). Config is one self-contained `--conf`/`--config` file,
  owner optional (solo runs).

Alg is building the MP brain in parallel on the same base (`MetaphysicistBrain`,
`MetaphysicistExternalBuffingBrain`, a pet cross-brain handshake + `/pet terminate`).

## Verified buff math (the key numbers, in the stone doc)

L50 Engineer, raw MC/TS **326**, Max NCU **148**:
- **NCU buff FIRST** (Fixer line, per-level: +20 L10 / +40 L25 / **+60 L50** / +85 L75 / …) →
  148 → **208**. Binding resource: the stack below (159 NCU) only fits with it.
- **+140 MC/TS = single-skill Mocham's Gift** `mcmo`/`stmo` (NO level/SL lock; 51/50 NCU) →
  MC/TS 466. (All-skills composites ARE level-locked: +25 L15, +50 L40, +90 L90, +140 L175.)
- **wrangle +131** (`131`, 58 NCU) → MC/TS **597 peak**.
- **Summon the best SUSTAINABLE pet**: durable (no wrangle) 466 → control max 466/0.80 = 582 →
  **Semi-Sentient Guardbot (req 569) @ 82%**, NOT Patchwork Warbot (596 = 78% → OE).
- **Wrangle off** → 466, pet holds, reclaim 58 NCU for survivability (downshift).

## TODO — resume here (the real next work)

1. **4a.2-full** — the plan currently EXCLUDES single-skill buffs (so it never picks
   `mcmo`/`stmo` — the ones we actually want) and has no NCU math or receiver level-gating.
   Add: single-skill +140 (MC+TS), real receiver level/SL gating, NCU fit (NCU buff expands
   then the skill buffs fit), composite+wrangle stacking.
2. **4b sustain-gate + downshift** — summon the best pet we can SUSTAIN at 80% (569 not 596),
   then drop the wrangle and reclaim NCU. Needs buff-timer tracking (buff.Cooldown.RemainingTime).
3. **/assist (owner target)** — sniff decoded: /assist has NO wire message; it's the client
   LookAt-ing the assistee's target. Bot already replicates via owner.FightingIdentity
   (`pet attack`). OPEN: is FightingIdentity selection or combat-only? LIVE TEST:
   `pet attack` on a selected vs attacked mob. See [[owner-target-assist]].
4. **CombatBrain toggle** — fight-alongside vs pet-tank is the player's choice (template-
   informed). CombatBrain is dormant; lighting it up with the toggle is the bot-melee half.
5. Smaller: 4a.3 Codedoc (RK2019), 4a.1b travel to buff spot, hunt.Tick throttle (~4/s not 64),
   buff-wait safety (don't wait petless under attack), reconcile `pet terminate` with Alg.

## First live test (owner's plan)

Strip all buffs, drop the pet, verify the Engineer OE's right and summons the best pet its RAW
skills allow. Note: the bot summons best-castable once the pet is GONE (no auto-OE-dismiss yet),
so manual drop is correct. `PetAutoBuff` off, hunt off = conservative first run.

## Architecture: shared base vs profession subclass (for the MP brain)

Over-equipping/buff-first is NOT the Engineer's - every pet class does it, over different skills. The
split:

- **Shared (base `PetBrain` + `BuffCatalog`)** - all profession-agnostic:
  - `BuffCatalog.BuildControlPlan(me, baseByStat, reqByStat, paid, prof)` is **skill-set generalized**:
    MC+TS (Engineer/MP attack), MatMet+TS (MP mezz), BioMet+TS (MP heal) all flow through the SAME
    phases (NCU first, multi-skill stackers per strain, lowest single-skill rung per skill). Trickle
    factors for 127/128/130/131 are verified in the catalog. An MC/TS convenience overload remains.
  - `RoutePlan` (self-cast vs bot tell, skip stable strains), `SurvivalFill`, `SelfBuffCandidates`
    (our learned self-buffs compete per strain), `BestNcuBuff`.
  - base `PetBrain`: `IsPaid`, `BuffUp`, `UnbuffedBase(stat)`, `StableStrains`, `RefreshSoonSec`,
    `OeMargin`/`CanControl`/`DriveAttack`, the crash-guarded `Tick`.
- **Subclass supplies only**: which pets + each pet's **required-skill set** and summon mechanic, the
  per-role drive, and the pet-only buff ids. `EngineerPetBrain` is the worked example (one attack
  robot, `{MC,TS}` from `PetReq`, shell/cast summon, `engineer-pets.json` pet buffs).

**MP brain:** three slots (attack `{MC,TS}`, mezz `{MatMet,TS}`, heal `{BioMet,TS}`), each runs the
same `BuildControlPlan`/sustain-gate/route/maintain pattern the Engineer uses - mirror `BuffFirst` /
`BestControllableRobot` / `MaintainAndSurvive` per slot. Fully unifying that orchestration skeleton
into a base loop (one `PetChoice` abstraction, 3 slots) is the natural next step - it changes the
shared base ctor, so coordinate.

## How to drive/observe live (MCP)

Bot running with `BotApiPort` set (5591) → `bot_command "brain"` (confirm brain loaded),
`bot_log grep="PET|HUNT|BUFFS"`, `bot_status`. Logs are Information-level per action; add
targeted LogDebug + a `DebugPets` flag only if a specific decision stays opaque.
