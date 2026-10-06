# Blood Plasma Farming — Design (not implemented)

Design-only. Bot farms biological mobs, converts looted parts into Blood Plasma with a
Bio-Comminutor, fills bags, and hands them to a Trader alt for the better sell price.
No selling by the farmer.

## Verified item data (local, OmniCell 18.8.62 + Tyrbot recipes)

| Thing | Item ID | Source |
|---|---|---|
| Basic Bio-Comminutor | **154331** | itemnames.sql |
| Advanced Bio-Comminutor | **154332** | itemnames.sql |
| Blood Plasma (lo/hi) | **154358 / 154359** | itemnames.sql |
| Monster Parts | **42641** | recipe 20 |
| Monster Parts with Ivory | 42644 | recipe 20 alt |
| Pelted Monster Parts | 42642 | recipe 20 alt |
| Pelted Monster Parts with Ivory | 42646 | recipe 20 alt |

**Recipe (Tyrbot recipe #20):** `Bio-Comminutor + Monster Parts → Blood Plasma`,
skill = **PT (Pharma Tech)**. Right-click the comminutor onto the parts.
Recipe note (verbatim intent): comminutor QL should be **≥ the Monster Parts QL** or you
lose QL in the result → always carry a comminutor QL ≥ target part QL.

### Comminutor price (decoded from AOBuddy20/GameData, stat 74 = Value)
154331 and 154332 are the **low/high templates of one comminutor line**, not two separate
items — "Basic" is just the low-QL name, "Advanced" the high-QL name. Value (stat 74) from
`ItemValues.bin` + the square-of-QL curve (`Utils/ItemValues.cs`):
- **QL 1  (154331): Value 420**
- **QL 200 (154332): Value 510,300**
- Between them `Value = round(420 + (510300-420) · t²)`, `t = (QL-1)/199`.

| QL | Value | ~buy @ Basic pharmacy terminal (SellMod 105%) |
|---:|---:|---:|
| 25 | 7,836 | 8,228 |
| 50 | 31,334 | 32,901 |
| 100 | 126,612 | 132,943 |
| 150 | 286,267 | 300,580 |
| 200 | 510,300 | 535,815 |

Purchase cost = `Value × SellModifier/100 × CL discount` (CL knocks 1% off per full 40 Comp
Lit). The owner only buys this once, so cost matters little except at QL 150+.

### Comminutor is OWNER-SUPPLIED — the bot never shops for it
The bot does **not** buy or hunt down the right-QL comminutor; making it find/purchase the
correct QL on its own would be far too fiddly. The owner hands the farmer a comminutor of the
QL they want to run, and the bot just **uses whatever comminutor is in inventory**:
- On start, read the equipped/held comminutor's QL. That QL is the **ceiling on plasma QL**
  (combine at QL ≤ comminutor QL, per the recipe note, or you lose QL).
- Effective plasma QL cap = `min(comminutorQL, floor(PharmaTech / 2.5), partQL)`.
- If no comminutor is present, or its QL is below the parts being farmed, **announce and stop**
  rather than guessing or buying one. The price table above is reference for the owner only.

## Skill gate (owner's stated rule = ground truth)
- Pharma Tech required ≈ **2.5 × plasma QL** (QL 200 → ~500 PT).
- Bot computes **maxQL = floor(PharmaTech / 2.5)** from the farmer's live skill, and only
  attempts combines at/under that. Parts above maxQL are not comminuted (no wasted parts,
  no QL loss). Comminutor QL must also be ≥ part QL (see recipe note).

## Ruined-parts hazard — HARD BLACKLIST
Certain corrupted parts can **destroy the Bio-Comminutor** if combined. Never feed:
- **Withered Flesh**
- **Half-Digested Human Parts** (e.g. Half Digested Human Torso **223567**)

Rules:
- Blacklist by **item ID**, not name match; keep it extensible for new corrupted variants.
- Only comminute IDs on the known-good parts list (42641/42642/42644/42646). **Unknown part
  → skip, never risk the tool** (losing it halts the whole run).
- Tool-health guard: if the comminutor leaves inventory unexpectedly mid-run, **halt and
  announce** — do not keep hunting with no way to convert.

## Core loop (per mob)
1. Engage one biological mob of the level-appropriate type.
2. Loot its body part.
3. If part ID is good AND part QL ≤ maxQL AND comminutor QL ≥ part QL → comminute → Blood Plasma.
4. Drop plasma into the active bag; roll to the next bag when full.
5. All bags full → announce **"ready"**; owner transfers bags to the Trader. Farmer never sells.

## Bag logistics
- Carry **10+ empty bags**, kept open as plasma destinations.
- Done condition = N bags × plasma stack size filled. (Plasma stack size per bag = confirm in-game.)

## Continuous farming — don't stop when bags fill (bank offload + rebuy)
When all bags are full the bot does **not** stop. It cycles through the bank and keeps going:
1. Travel to the nearest **bank** terminal and open it (`Inventory.Bank`, `BankOpen`).
2. **Deposit the full plasma bags into the bank** — move each full bag (with its contents) via
   `Item.MoveToBank()`. Storing the whole backpack is cleaner than emptying item-by-item.
3. **Buy fresh empty bags** — reuse `ResupplyController`'s existing `buybags` path, **limited by
   the farmer's current credits** (buy 10, or as many as credits allow).
4. Resume farming and fill the new bags. Repeat.
5. **Stop only when** it can no longer buy a bag (out of credits), the **bank is full**, or the
   owner says stop — then announce.

Important constraints this depends on:
- **The farmer never sells**, so it earns no credits — bag-buying runs off a **seed credit
  float** the owner gives it. Bags are cheap (a backpack is a few hundred credits), so a small
  float lasts many cycles; still, it is finite and is the real stop condition. (Open: confirm
  the empty-bag item id + cost, and seed amount.)
- **Bank is per-character.** Depositing to the farmer's bank only *parks* plasma to free
  inventory — it does **not** deliver to the Trader. Delivery is still a **face-to-face trade**:
  the farmer (carrying current bags + whatever it withdraws from its bank) meets the Trader and
  trades them over, then the Trader sells. Banking just means the farm run isn't wasted while
  waiting for that handoff.

## Architecture / where it lives
This is a **task**, so it is a new **`BloodPlasmaController`** in `AOBuddy20/Controller/`,
alongside `HuntController` / `MissionController` — **not a new brain**. Brains
(`CombatBrain`, `PetBrain`, `*SelfbuffingBrain`…) are per-tick profession *decision* modules;
controllers are task orchestrators. `BloodPlasmaController` drives a small state machine and
leans on existing pieces:

| Need | Reuse / new |
|---|---|
| Travel to the farm zone | `MovementController` / `NavController` (reuse) |
| Find + kill biological mobs | `HuntController` + `CombatBrain` (reuse; add a biological + good-part-dropper filter) |
| **Loot the corpse** | **NEW `LootController`** — no loot action exists yet (GAP 1, see LOOTCONTROLLER-PLAN.md) |
| **Comminute parts → plasma** | **NEW tradeskill send** — NO item-on-item support exists in the SDK at all (GAP 2) + skill/QL gate + ruined-parts blacklist |
| Bag management | `LootBagStore` exists for a designated destination bag; **NEW**: fill N bags, roll-over, done-condition, never-sell |
| Bank offload + rebuy on full | `Item.MoveToBank()` + `ResupplyController` `buybags` exist; **NEW**: glue the full-bag-bank-and-rebuy loop (GAP 3, smallest) |
| Trader-side selling | extend `SellController` for the Specialist Commerce terminal + buff-CL-before-sell |

State machine (farmer): `Travel → Hunt → Loot → Comminute → Bag → (bags full? → Bank+Rebuy) → (credits out? → Announce)`.
The Trader side (buff best Trading-line nano → sell at OT Specialist Commerce) is a separate, later mode.

## Three build gaps (the real work; none exist today)
1. **`LootController`** — open a corpse, transfer wanted items out. Transfer primitives
   (`Item.MoveToInventory/MoveToContainer`) exist; the corpse-open path must be verified/wired.
   Fully specced in **LOOTCONTROLLER-PLAN.md**. **This blocks everything.**
2. **Comminute = item-on-item tradeskill send** — the biggest unknown. `Item.Use()` only targets
   a character/slot (GenericCmd Use), there is **no "use item A on item B"** send. Right-clicking
   the comminutor onto Monster Parts is a distinct tradeskill action the server resolves. Must
   capture it from a live sniff and wire the send + the result/skill-fail handling. Until this
   exists, the whole mode is impossible — spike it early.
3. **Bank-offload + rebuy glue** — smallest. `MoveToBank()` and `buybags` both exist; just the
   state-machine wiring for the continuous loop.

## Sell-price scaling → the Trader (the payout model)
Plasma isn't a fixed payout; the **buy-back** a vendor pays scales with the seller's **Computer
Literacy**, faction, and the terminal's BuyModifier (stat 426). Note the direction: the
`Utils/ItemValues.cs` formula `Value × SellMod/100 × CL discount` is the **buy side** (what a
terminal *charges* you, CL makes it cheaper). **Selling is the mirror** — higher CL and a higher
BuyModifier *raise* what you receive. The exact sell-back formula is **not yet decoded** (open
item); what is certain is the ranking: more CL + the top BuyModifier terminal = more credits. So
the Trader's job is to stack Comp Lit, then sell at the right terminal.

### Trader self Comp Lit buff line (decoded from GameData/nanos.ocp, client v18.8.62)
All three are gated `VisualProfession == Trader` and have **no Level requirement** — the cast
gate is the two nano skills (buffable/over-equippable), not character level.

| Nano | +Comp Lit | NCU | PsyMod req | SensImp req | Nano-point cost |
|---|---:|---:|---:|---:|---:|
| Frequent Customer | +55 | 7 | 103 | 86 | 236 |
| Bulk Trader | +160 | 30 | 455 | 417 | 378 |
| **Trading Mogul** | **+260** | **51** | **780** | **708** | 540 |

(The pack's skill reqs differ from community web tables — the client data above is authoritative.)

### Casting the best one (Trading Mogul, +260) below its natural level
- **Not level-locked.** Bridge PsyMod to **780** and SensImp to **708** with buffs: +140
  nanoskill buffs on each line plus a self **Supreme Wrangler**.
- **The bridge math is unconfirmed** — a Trader's base PsyMod/SensImp at ~L50 is roughly
  200–300; +140 + a wrangle may still fall short of 780/708. So **don't assume Mogul casts at
  L50.** Needs the real L50 skill numbers (open item).
- **Graceful fallback (design rule):** cast the **highest Trading-line buff the Trader's current
  skills actually allow** — Frequent Customer (+55) → Bulk Trader (+160) → Trading Mogul (+260).
  The bot picks the best it can land, not Mogul unconditionally. Any CL still beats none.
- **NCU floor:** Mogul needs **51 NCU** to sit (+ headroom for the bridge buffs while casting).
  A 51-NCU belt is roughly a **level-50** thing — so L50 is the NCU floor, while the *skill*
  gate may push the full +260 higher.
- Only un-buffable requirement is being a Trader (he is).

### Where to sell — the Trader's Specialist Commerce terminal
The owner **can** sell plasma at any normal shop themselves, but the best buy-back comes from a
**Trader-only Specialist Commerce** terminal — not a generic shop. Locations: **ICC**,
**Borealis**, and one on each **clan** and **omni** side. Confirmed from `ItemValues.bin`
(stat 426 = BuyModifier, what the terminal pays you); these carry the top buy-back in the game:

| Terminal | Template id | BuyMod (pays you) |
|---|---:|---:|
| **OT (Omni) Specialist Commerce** | 99499 | **8** |
| Clan Specialist Commerce | 99538 | 7 |
| Specialist Commerce (ICC/neutral) | 151987 | 7 |
| typical shop terminal | — | 4 (median of 730) |

So the Omni terminal pays ~2× a normal shop before CL. CL raises it further.

## Earnings estimate — per 10 full bags, sold by the Trader
Blood Plasma **Value (stat 74)** decoded from `ItemValues.bin`: QL1 = 1,240 → QL200 = 906,100,
scaling as QL² (plasma lowid 154358 / highid 154359). Plasma QL ≈ the farmed part QL (capped by
comminutor QL and Pharma Tech).

The per-bag credits are **calibrated to the Tyrbot recipe-20 anchor** ("4 full bags of QL70
parts ≈ 1,000,000" → ~250,000 per full bag at QL70, Trader-sold), then scaled by the Value
curve: `perBag(QL) ≈ 250,000 × Value(QL) / Value(70)`.

| Char level | Plasma QL (rep.) | Plasma Value each | ~ per full bag | **~ per 10 bags** |
|---|---:|---:|---:|---:|
| 20–40 | 30 | 20,456 | 46,000 | **~0.46M** |
| 40–70 | 60 | 80,779 | 184,000 | **~1.8M** |
| 70–100 | 100 | 225,187 | 512,000 | **~5.1M** |
| 100–150 | 150 | 508,520 | 1,155,000 | **~11.6M** |
| 150–200 | 200 | 906,100 | 2,059,000 | **~20.6M** |

**Assumptions (alg: these set the real number):**
1. "Full bag" is calibrated to the QL70 anchor, so it already folds in bag capacity × the
   Trader buy-back %. The two still need an in-game confirm: **plasma stack size / bag slot
   count**, and the **exact buy-back formula** (BuyModifier 8 at OT Specialist Commerce, how
   Comp Lit scales the sell side).
2. Figures assume the **best-price setup**: Omni Trader at OT Specialist Commerce with Trading
   Mogul (+260 CL) up. A generic shop (BuyMod 4, no CL) roughly **halves** these.
3. Linear-ish time per bag is not modelled — high-QL bands kill slower, so credits/hour flatten
   well below the raw per-bag jump; the table is **credits per session of 10 bags**, not per hour.

### Faction + the best-price stack
Same-faction shops pay more, so an **Omni-aligned** Trader at an Omni (or ICC) **Specialist
Commerce** terminal is the ceiling. **Best price = Omni Trader with the highest Trading-line CL
buff it can cast (ideally QL 165 Trading Mogul, +260 CL), selling at a Specialist Commerce
terminal.** Farmer level only sets which zone/part-QL it reaches; credits come from CL + faction
+ the right terminal.

### Target setup
Trader **Omni**, casting the **best Trading-line CL buff its skills allow** (Mogul +260 when it
can reach PsyMod 780 / SensImp 708 with buffs; Bulk Trader +160 earlier), selling at a
**Specialist Commerce** terminal (ICC/Omni side). L50 is the **NCU** floor for Mogul (51 NCU);
whether L50 also meets the buffed skill gate is unconfirmed (open item). That setup is the price
tier everything else scales from.

### Config / user-facing copy (must appear in the mode's config or help text)
> You can sell Blood Plasma at any shop yourself, but the Trader's **Specialist Commerce**
> terminal pays the most (ICC, Borealis, and the clan/omni-side terminals). Best prices of all:
> an **Omni-aligned Trader** casting the highest **Trading-line** Comp Lit buff it can (up to
> **Trading Mogul, +260 Comp Lit**), selling at a Specialist Commerce terminal.

## Level → zone → biological target
| Char lvl | Part QL | Where / what |
|---|---|---|
| 20–40 | 20–40 | Greater Omni Forest / Galway (sandworms, medusas); Rhinoman Cockpit; Nascence Spider Marsh / Croakers |
| 40–70 | 40–80 | Lush Fields, Stret West (spiders/mantises); Steps of Madness; Elysium Chill Spiders |
| 70–100 | 70–120 | The Reck wildlife; Elysium Cape Callous mortiigs |
| 100–150 | 120–180 | S. Foul Hills / Smuggler's Den mantises; Deep Reck, E. Belial mutants |
| 150–200 | 180–200+ | Scheol/Adonis wildlife & chimeras; **RK mission terminal, Monster/Biological slider, QL200+** (densest, no spawn wait) |

Mission-terminal mode (150–200) reuses existing mission roll / room-clear with a
biological-slanted slider preset.

## Resolved this pass (from AOBuddy20 GameData — no external sources)
- Comminutor price curve (ItemValues.bin): QL1 = 420 → QL200 = 510,300, square-of-QL.
- Blood Plasma Value (ItemValues.bin): QL1 = 1,240 → QL200 = 906,100, square-of-QL.
- Trader self CL line (nanos.ocp): Frequent Customer +55 / Bulk Trader +160 / Trading Mogul
  +260; NCU 7/30/51; skill reqs (PsyMod/SensImp) decoded; no Level lock.
- Best buy-back terminal (ItemValues.bin stat 426): OT Specialist Commerce BuyMod 8 (vs median 4).
- Comminutor is owner-supplied; bot never shops for it.
- Three build gaps identified (Loot / Comminute-tradeskill-send / Bank-rebuy glue).

## Open items to close before build
1. **GAP 1 — build `LootController`** (open corpse → transfer wanted items out). Blocks all of
   it. Spec in LOOTCONTROLLER-PLAN.md.
2. **GAP 2 — comminute tradeskill send** (use-item-on-item). No SDK support; capture from a live
   sniff and wire it + skill-fail handling. Spike early — highest risk.
3. **GAP 3 — bank-offload + rebuy glue** (`MoveToBank` + `buybags` exist; wire the loop).
4. **Plasma stack size per bag** (sets the "N bags full" done condition).
5. **Empty-bag item id + cost + seed credit float** for the rebuy loop.
6. **Good-part loot-table per zone** (which mobs actually drop 42641/42642/42644/42646).
7. **Comminutor durability / charges** — confirm whether it has limited uses beyond breaking on
   corrupted parts (affects how often the owner must resupply it).
8. **Comminutor QL to stock per level band** (buy QL ≥ farmed part QL to avoid QL loss).
9. **Exact sell-back formula** (how CL + BuyModifier set the payout) and the **L50 skill-bridge
   math** (does +140 buffs + Supreme Wrangler reach PsyMod 780 / SensImp 708 at L50, or higher?).
10. **Pharma Tech check** — how the farmer reads its live PT to set maxQL (which stat / packet).
