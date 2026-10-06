# LootController — Design (not implemented)

A **reusable** capability: walk to a dead mob's corpse, open it, and transfer wanted items
out. Blood Plasma farming is blocked on this, but hunting and missions want it too, so it is
its own controller in `AOBuddy20/Controller/`, not buried inside any one mode.

## What already exists (verified in-tree)
- **Corpse awareness**: `CorpseFullUpdateMessage` (AOSharp.Clientless `Client.cs`) fires on
  death, names the mob, and `MarkDead`s it; `DynelManager` tracks corpse counts and the last
  corpse. So we are told a corpse exists and which mob it was.
- **Container model**: `Inventory.ContainerOpened`, `OnContainerUpdate`, `Container.RegisterItems`,
  and the `ContainerItemAdded/Removed` events — opening a container surfaces its items.
- **Transfer primitives**: `Item.Use()`, `Item.MoveToInventory(slot)`, `Item.MoveToContainer(id)`,
  `Item.MoveToBank()`. These are the send side we need; no new packet work if a corpse opens
  like a normal container.
- **Movement**: `MovementController` / `NavController` to approach the corpse.
- **Loot destination**: `LootBagStore` + the `lootbag` command already pick which bag loot goes to.

## The one thing to verify first (the gap)
There is **no loot action today** — nothing opens a corpse and takes items. Confirm, live, that:
1. A corpse is reachable as a **container** (an `Identity` we can open via `Use`/GenericCmd and
   that comes back through `ContainerOpened`), and
2. its items expose the normal `MoveToInventory` / `MoveToContainer` sends.

If yes, LootController is pure orchestration over existing sends. If the corpse open or the
from-corpse transfer uses a different opcode, that send is the only new wire work. **This
verification is task 1 and gates the estimate.**

## API (what other modes call)
```
LootController.LootNearby(predicate, destination)   // loot all in-range unlooted corpses
LootController.Loot(corpseIdentity, predicate, dest) // loot one
event ItemLooted(Item, corpseIdentity)
event CorpseEmptied / CorpseSkipped(reason)
Enabled, Radius, WantFilter, Destination            // config
```
- **predicate**: which items to take — by id / id-set / name / QL band / "take all". Blood
  Plasma passes the good Monster-Parts id set (42641/42642/42644/42646).
- **destination**: the designated loot bag (`LootBagStore`) if set, else general inventory.

## State machine (per corpse)
```
PickTarget -> Approach -> Open -> Read -> TransferWanted -> Close -> MarkLooted -> next
```
1. **PickTarget** — from the dead/corpse set: in `Radius`, not in the `_looted` set, line-of-sight
   / reachable. Nearest first.
2. **Approach** — `MovementController` to within the game's loot range; give up after a timeout
   or if the corpse despawns.
3. **Open** — `Use` the corpse; wait for `ContainerOpened` for that identity (settle timeout,
   Resupply-style: no open in N s = treat as gone, move on).
4. **Read** — enumerate the container's items.
5. **TransferWanted** — for each item matching `predicate`, `MoveToInventory` (or into the loot
   bag). One at a time; wait for `ItemRemoved`/`ContainerItemRemoved` confirm before the next so
   a dropped packet can't desync. Stop early if inventory / loot bag is **full** (raise a signal,
   see below).
6. **Close / MarkLooted** — add the corpse identity to `_looted` (bounded LRU set so it can't
   grow forever); emit `CorpseEmptied`.

## Robustness (the parts that actually matter)
- **Corpse despawn** mid-approach or mid-loot → abandon this corpse, no stall.
- **Out of range / unreachable** → skip after timeout, blacklist briefly.
- **Inventory or loot bag full** → stop taking, emit `InventoryFull`; the owning mode decides
  (Blood Plasma: trigger the bank-offload/rebuy loop — see BLOODPLASMA-PLAN.md).
- **Ghost / unmatched corpse** (CorpseMatched < CorpseCount) → still lootable by identity; if the
  open fails, skip.
- **Already-looted / empty** → `_looted` set prevents re-opening the same corpse.
- **Combat safety** → loot only when not actively tanking/aggroed, or when the owning mode says
  it is safe; never break off a fight to loot.
- **Kill-credit / locked corpses** → if the server refuses the open (not our kill), skip quietly.

## Integration
- **HuntController** calls `LootNearby(...)` after each kill (or batches corpses after a pull).
- **BloodPlasmaController** sets `WantFilter` = good Monster Parts, `Destination` = its parts bag,
  and consumes `ItemLooted` to feed the comminute step.
- **MissionController** can reuse it for reward/chest loot later.

## Open items
1. **Verify corpse-open + from-corpse transfer** (the gap above). Capture a live loot if unsure.
2. Loot **range** value (game's max loot distance) — confirm from a sniff.
3. Whether a corpse exposes **all** items at once or needs paging.
4. `_looted` set sizing / eviction (corpse identities are unique per kill).
