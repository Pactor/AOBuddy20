# AOBuddy20

A clientless Anarchy Online bot. It runs a character headlessly (no game client) on the
AOSharp.Clientless SDK, plays it with a stack of profession-aware brains and controllers, and is
driven by its owner through in-game tells — or a private channel, or a local HTTP API.

One process runs one character, defined by one self-contained config file. Everything the bot
knows about its body, its packs and the ground it walked lives in per-character JSON under
`Build\`.

## What it does

- **Movement & travel** — follow-the-owner (stacks on him and mirrors his movement), walk to
  coordinates, and cross-playfield travel planned over a zoning graph (zone lines walked across,
  whompas used, pads walked onto), then a plain walk to the target.
- **Combat** — a data-driven combat brain; for pet professions (Engineer, Metaphysicist) a pet
  brain summons, maintains and commands the pet roster. A hunt mode makes the pets attack
  hostiles in a radius while the bot stays put.
- **Buffs** — keeps its own learned nanos up (selfbuffing), and asks public buff bots (Chewy's /
  Codedoc style) for external buffs. Pet professions run a full *pet-first buff cycle*: strip,
  NCU buff, per-line skill stacks → summon each pet at the peak → pet buffs → obedience floor →
  comfort fill.
- **Mission blitz loop** — roll at a mission terminal (sliders configured), take find-item /
  find-person missions, walk in, select the target, bag the reward, walk out, repeat. A *want
  list* restricts rolls to wanted rewards (exact item or kind/profession/QL queries). *Clear
  mode* (`mission clear on`) kills every mob in the building before the objective — the XP, and
  for Omni and Clan a side token with the reward; the objective waits until the server's clear
  share passes 90%.
- **Economy** — resupply stims/rechargers/backpacks from shop terminals (it remembers which
  terminal sells what), and sell bag contents to a vendor (NODROP and main inventory untouched).
- **Survival** — stims in combat, rechargers with a sit/rest cycle out of combat, healing itself
  and the owner.
- **Local API** — `GET /status`, `/nav`, `/inventory`, `/log` and `POST /command` on 127.0.0.1,
  so external tooling (see `tools/AOBuddyMonitor`) can watch and drive the same command table
  the owner's tells use.

## Running it

```
dotnet build                # the whole solution builds into Build\
Build\AOBuddy20.exe --config <name>.json
```

`--config` (alias `--conf`) names a JSON file **relative to `Build\`** — one per character
(`--config dadbod.json`). Start from `Build\config.example.json`. Unknown keys are ignored, a
missing key keeps its code default, values are read at startup (edit and restart). Commands that
change runtime settings (`hunt radius …`, `hunt blacklist …`, …) save the change back into the
file. `Owner` is optional — leave it empty and the bot runs solo: nobody commands it and
owner-assist is off.

Key config sections (see `config.example.json` for the full annotated list):

| Group | Notable keys |
| --- | --- |
| Account | `Username`, `Password`, `Character`, `Dimension` (`RubiKa` / `RubiKa2019`), `Owner`, `AutoAcceptOwnerTeamInvite`, `Follow`, `UsePrivateChannel` |
| Mission | `MissionDifficulty`, the six `MissionSlider*` values (wire range −100..+100, 0 = terminal default), `MissionTerminalRadius`, `MissionZones`, `WantUnseenRolls` |
| Resupply | `LowStimCount`/`LowRechargerCount`, `ResupplyStimName`, `ResupplyLockpickName`, `Resupply*Target`, `ResupplySearchRadius`, `ResupplyCashReserve`, `ResupplyContainerName`, `ResupplyShopPf` |
| Heal | `HealNanoCombatPct`, `HealNanoOutOfCombatPct`, `HealRestMaxSeconds` |
| Hunt | `HuntRadius`, `HuntMaxLevelMargin`, `HuntFactionMode`, `HuntBlacklist` |
| Buff bots | `BuffBotName`, `BuffRequestTells` (highest-NCU tell first), `BuffHandshakeSeconds`, `BuffTravelToSpot`, `PetAutoBuff`, `PetBuffWaitSeconds`, `BuffIncludeWrangle` |
| API | `BotApiPort` (0 = off) |

## How it is controlled

The bot obeys exactly one person: the character named in `Owner`. Everything else is logged but
never obeyed.

- **Tells** (default): `/tell <bot> <command>`. Replies come back by tell. The bot greets the
  owner with `AOBuddy20 online - send 'help' for commands.` the first time he comes into view.
- **Private channel** (opt-in, `UsePrivateChannel: true`): the Tyrbot pattern — the bot invites
  the owner into its own private channel at login, accepts an invite from the owner, and obeys
  commands spoken there (answers go back to the channel). Tells stay enabled as a fallback.
- **Local HTTP API** (`BotApiPort`): `POST /command` with a command as the body runs it exactly
  as an owner tell would; replies are collected for ~2.5 s and returned. `GET /status`,
  `GET /nav`, `GET /inventory`, `GET /log` for watching. Localhost only.

A leading `!` is accepted and stripped (`!follow` = `follow`); the command word is
case-insensitive. Async reports (shopping results, selling reports, mission-run progress) answer
the id the last command came from.

## Commands

### Movement & body

| Command | What it does |
| --- | --- |
| `follow` | Follow the owner: run to him, stack on his spot, mirror his movement. This is the idle default after login (`Follow: true`). Any goal preempts it; when the goal clears, follow resumes on its own. |
| `stay` | Stop following; stand put. |
| `come` | Walk to the owner's current position (must be in view: `Can't see you (out of range?)` otherwise). |
| `goto x [y] z` | Walk to coordinates on the current playfield at Travel priority. Y is optional — without it the walk keeps the bot's current height (a bare `goto x z` never aims underground). |
| `goto x z <playfield>` | Travel shape: the last word names the destination playfield (id or unambiguous name part); the bot crosses there first, then walks to x z. |
| `travel <playfield>` | Cross-playfield travel: the zoning graph plans the cheapest route (zone lines, whompas, pads). Playfield by id or any unambiguous part of its name. |
| `travel x z <playfield>` | Same, then walk to the coordinates. |
| `pos` / `status` | One-liner of where the bot is and what it is doing. |
| `navdata` | The nav data's own verdict about where the bot stands — check the data against the live character before relying on it. |
| `stop` | Stop means stop: ends resupply and mission runs, clears follow **and** all goals. Standing down. |
| `sit` | Sit down (ends follow, clears goals — standing later must not resume it). |
| `stand` | Stand up. |

Manual orders (`goto`, `come`, `sit`) take the body away from any running travel plan.

### Pets & hunting

| Command | What it does |
| --- | --- |
| `pet attack` / `pet kill` | Point the pets at the **owner's current target** and hold them on it (overrides hunt/owner-assist until it dies or `pet follow`). The owner must have a target — select or hit a mob first. |
| `pet follow` / `pet stop` | Stand the pets down; manual target cleared, back to auto (hunt / owner-assist). |
| `pet` | Status: manual target held, or pets on auto. |
| `hunt on` / `hunt off` | Pets attack hostiles within the radius while the bot stays put. Off until `hunt on`. Needs an attack pet up. Owner-assist (helping in the owner's own fight) works regardless. |
| `hunt radius <5-100>` | Engagement radius in meters (clamped, saved to the config). |
| `hunt maxlevel <0-500>` | Level ceiling for a target: best attack pet's level + this margin (scales off the pet, not the player). |
| `hunt faction auto\|on\|off` | Shadowlands faction-safe hunting. `Auto`: on once a Redeemed/Unredeemed standing is set. |
| `hunt blacklist add <mob name>` | Never hunt that mob (names may contain spaces; owner-assist still can). |
| `hunt blacklist remove <mob name>` | Lift the blacklist entry. |
| `hunt blacklist list` | Show the blacklist. |
| `hunt` | Status: on/off, radius, faction mode, ceiling, blacklist size, kills. |

### Buffs

| Command | What it does |
| --- | --- |
| `buffs` / `buffs start` | Run the configured tell list (`BuffRequestTells`, in order — highest NCU buff first). Walks to the buff bot's spot first when `BuffTravelToSpot` is on. Handshake: invite → team → tells → buffs land → auto-kick. Be un-teamed and near the bot. |
| `buffs pet` | Convenience trigger: the simple pet-summon tells (NCU + the biggest safe nano-skill buff). The *accurate*, land-gated, self-cast-aware path is the automatic one (`PetAutoBuff: true`) — this is the by-hand shortcut. |
| `buffs stop` | End the running buff session. |
| `buffs status` | Session stage, landed/already-up/failed counts, bot name and catalog size. |
| `nanoreset` | Strip every running buff off the bot (the cycle's own clean-slate wire). The external buffing re-fills by itself afterwards: with the buffs gone the outcome ledger reads worn and the floor + comfort fill re-enters on its decide ticks — no `buffs pet` needed. Tier wants stay gated while the pets are out. |

For Metaphysicists with `PetAutoBuff` on, the whole pet-first cycle (strip → NCU → per-line
buff+summon → pet buffs → obedience floor → comfort) runs on its own whenever a better pet is one
skill-buff away — no command needed. An owner-started `buffs`/`buffs pet` session gets the same
pet-hold coordination around it.

### Bags & loot

| Command | What it does |
| --- | --- |
| `lootbag` / `lootbag list` | The packs' bags, **numbered** (up to 10 told; the rest counted): contents, free slots, and which are designated as loot bags. |
| `lootbag add <n>` | Designate bag #n (the number `lootbag list` shows) as *the* loot bag — mission rewards and loot go there. The designation follows the bag's identity, never its (client-side, invisible-to-bots) name. |
| `lootbag remove <n>` | Undesignate bag #n. |

### Shopping & selling

| Command | What it does |
| --- | --- |
| `resupply` | Shop for stims and rechargers, chosen by the bot's own skills, topped back up to the configured targets. Uses terminal memory; travels to `ResupplyShopPf` when nothing is in reach. Never fills the last `ResupplyKeepFreeSlots` slots; respects `ResupplyCashReserve`. |
| `resupply status` | What a running (or last) resupply is doing. |
| `resupply stop` | Abort the run. |
| `resupply forget` | Forget which terminals sell what — the next run checks them all again. |
| `resupply machines` | List the shop terminals within the search radius and what they stock (first 15 told). |
| `resupply bags <1-20>` | Buy that many bags (`ResupplyContainerName`), traveling to the shop playfield if needed. |
| `sell` | Sell the **bag contents** to a shop terminal. Opens every bag first to check what is sellable, then travels only when something is. NODROP items, the bags themselves and the main inventory are never sold — items are staged through the main inventory one batch at a time. |
| `sell status` | Progress of the sell run. |
| `sell stop` | Abort. |

### Missions

| Command | What it does |
| --- | --- |
| `mission run` | Start the blitz loop: roll at a (saved or nearby) terminal, take a find-item/find-person mission, travel to the building, walk in, select the target, bag the reward, walk out, repeat. A mission still in the quest log from before a restart is finished first. Switches a want run off; keeps clear mode as it is. |
| `mission stop` | End the run. |
| `mission clear on` / `mission clear off` | **Clear mode**: kill every mob in the building before the objective — the XP, and for Omni and Clan a side token with the reward. The bot fights what attacks it, closes on the nearest pull one at a time, walks every room (the find-person target's room last — he is never fought, even when he swings at the bot), rides buttons to floors with rooms left, and keeps walking until the server's "% of the building's mobs dead" share passes **90%** — only then the objective. Rooms count as walked on arrival, never on setting out. Toggling works mid-run: `off` hands the body back to the objective. `mission run clear on\|off` is the same toggle. Session-only (like follow/stay). |
| `mission clear` | **Abandon**: stop any run AND delete the held mission — no re-roll (that is `skip`), no resume (that is `stop`). Drops the mission key from the packs with it. |
| `mission skip` | Delete the held mission and roll a fresh one (drops an in-progress mission; deletes a held one when idle). |
| `mission status` | Phase, held mission, saved terminal — and while clearing, the clear share (`x% cleared`, or `n of m mobs seen dead` when the server sends no share). |
| `mission roll` | One by-hand roll at the terminal the bot stands at — the offer list lands with `mission list`. No run is started. |
| `mission list` | The missions offered by the last roll, numbered, with the reason a mission would be skipped. |
| `mission accept <n>` | Accept the nth offer from the list by hand. |
| `mission buybags <1-20>` | Stop any run and go buy bags (same shopping trip as `resupply bags`). |
| `mission probe [x z]` | Debugging aid: what walls the composed building data says stand around a point (default: the bot's position). |

**The want list** — restrict rolls to wanted rewards. Wanted items are never sold.

| Command | What it does |
| --- | --- |
| `mission want` | Start rolling for the list (from the next roll when a run is on; starts the run when idle). |
| `mission want add <exact item name>` | Want an exact item by name. |
| `mission want add <kind/profession/QL query>` | Want by query: `nano engineer ql 20-30`, `implant ql 200+`, `ncu ql 30-45`, `weapon ql 100` … |
| `mission want remove <n>` | Remove entry n (numbered by `want list`). |
| `mission want list` | The entries and the current mode. |
| `mission want mode always\|list` | `always`: keep rolling for the whole list. `list`: one pass, then stop. |
| `mission want status` | Rolls so far, what was collected, what is still wanted. |
| `mission want lines [part]` | Look up nano line names for queries (e.g. `want lines pet`). |
| `mission want drop <nano name>` | Mark a nano (all crystals named like it) as not-offered — never roll for it again. |
| `mission want undrop <nano name>` | Reverse a `drop`. |
| `mission want clear got` | Forget what earlier want runs already collected (they become wanted again). |

### Housekeeping

| Command | What it does |
| --- | --- |
| `brain` | Which combat / pet / selfbuffing / externalbuffing brains this character loaded (per profession), and whether each is dormant. The verification command for brain selection at logon. |
| `help` | The one-tell command overview. |

## Layout

```
AOBuddy20/            the bot: BotLoop, Brains (per profession), Controllers (mission, movement,
                      heal, hunt, resupply, sell, buffbot, buff catalog), Nav, Chat, Network API
AOSharp.Clientless/  the clientless SDK (login, packets, dynels, chat)
AOSharp.Common/      shared game data (stats, nanos, items, messages)
tools/               offline utilities (nav extractors, the buff dry-run, the monitor, …)
Build/               everything compiles here; configs and per-character state live beside the exe
```

Per-character state in `Build\`: the config JSON, `lootbags-<char>.json`, `resupply.json`
(terminal memory), `missionterminal.json`, `walked.json` (learned ground), `wants-<char>.json`.
