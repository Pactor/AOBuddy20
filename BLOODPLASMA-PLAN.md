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

## Sell-price scaling → the Trader (the payout model)
Plasma isn't a fixed payout; shop price scales with the seller's **Computer Literacy** and
faction. From `Utils/ItemValues.cs` (verified live at ICC Fair Trade): price = `Value ×
SellMod/100 × CL discount`, and **CL knocks 1% off per full 40 Comp Lit**. Higher CL = better
prices. So the Trader's job is to stack as much Comp Lit as possible, then sell.

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
- **Not level-locked.** Bridge PsyMod to **780** and SensImp to **708** with buffs: a **+140
  nanoskill buff** on each line plus a self **Supreme Wrangler** covers most of the gap.
- **NCU is the real floor:** Mogul costs **51 NCU** to sit, plus headroom for the bridging
  buffs while casting. A Trader can field a 51-NCU belt at roughly **level 50** — so **~L50 is
  the practical target** to self-cast Trading Mogul and sell at full +260 CL.
- Only un-buffable requirement is being a Trader (he is). Nothing here is level-gated.

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
Same-faction shops pay more, so an **Omni-aligned** Trader at an Omni (or ICC) Specialized
Commerce terminal is the ceiling. **Best price = Omni Trader, high enough level to self-buff
QL 165 Trading Mogul (+260 CL), selling at a Specialized Commerce terminal.** Farmer level only
sets which zone/part-QL it reaches; credits come from CL + faction + the right terminal.

### Target setup (locked)
Trader **Omni**, level high enough to self-cast **QL 165 Trading Mogul (+260 CL)** on a 51-NCU
belt (~L50 practical), selling at a **Specialized Commerce** terminal (ICC/Omni side). That is
the price tier everything else scales from.

### Config / user-facing copy (must appear in the mode's config or help text)
> You can sell Blood Plasma at any shop yourself, but the Trader's **Specialized Commerce**
> terminal pays the most (ICC, Borealis, and the clan/omni-side terminals). Best prices of all:
> an **Omni-aligned Trader**, high enough level to self-buff **QL 165 Trading Mogul (+260 Comp
> Lit)**, selling at a Specialized Commerce terminal.

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
- Trader self CL line (nanos.ocp): Frequent Customer +55 / Bulk Trader +160 / Trading Mogul
  +260; NCU 7/30/51; skill reqs decoded; no Level lock.
- Target setup: Trader ~L50 Omni, self-casts Trading Mogul (+260 CL) on a 51-NCU belt.

## Open items to close before build
1. **Plasma stack size per bag** (sets the "N bags full" done condition).
2. **Confirm the good-part loot-table** per zone (which mobs actually drop 42641/variants).
3. Which comminutor QL to stock per level band (buy QL ≥ farmed part QL to avoid QL loss).
4. Confirm the **Supreme Wrangler + +140 nanoskill buff** values actually bridge L50 PsyMod/
   SensImp to 780/708 (compute once the Trader's base skills at L50 are known).
