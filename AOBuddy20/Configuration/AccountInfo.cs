// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: AccountInfo.cs
// 
// Last modified: 2026-09-30 00:19
// Created:       2026-09-29 23:09
// 
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AOBuddy20.Configuration;

public class AccountInfo
{
    public string Character = "";
    public string Dimension = ""; // "RubiKa" (default) or "RubiKa2019"
    public string Owner = ""; // OPTIONAL - the character whose /tells are obeyed. Empty: the bot runs
    // solo (no one commands it, owner-assist is off). The bot does not need an owner to run.
    public bool AutoAcceptOwnerTeamInvite = true; // group up when the owner invites (AOBuddy10 passive teaming)
    public string Password = "";
    public string Username = "";

    // --- Resupply (AOBuddy10 Config.cs:120-138, 'resupply' command): buying stims/rechargers in a shop.
    // A field missing from the JSON keeps its default.
    public int LowStimCount = 10; // warn when usable stims fall to this many (solo mode's shopping check)
    public int LowRechargerCount = 10; // warn when usable rechargers fall to this many
    public string ResupplyStimName = "Health and Nano Stim"; // exact name: "Stim" also matches Boosted/Burst/Swim
    public string ResupplyRechargerName = "Health and Nano Recharger";
    public int ResupplyStimTarget = 2; // top usable stims back up to this many (times 25)
    public int ResupplyRechargerTarget = 3; // top usable rechargers back up to this many (times 20)
    public float ResupplySearchRadius = 40f; // terminals within this of the bot are considered
    public float ResupplyUseRange = 3f; // walk this close to a terminal before using it
    public int ResupplyKeepFreeSlots = 2; // never fill the last inventory slots with supplies
    public int ResupplyCashReserve = 0; // credits never spent on supplies
    public float ResupplyNagSeconds = 120f; // short of credits: remind the owner this often
    public List<string> ResupplyMachineKeywords = new()
    {
        "Medic", "Health", "Stim", "Recharg", "First Aid", "Treatment", "Pharma",
    }; // a terminal name with one of these ranks first when nothing is remembered about it
    public string ResupplyContainerName = "Large Backpack"; // what 'resupply bags n' / 'mission buybags n'
    // buys (exact name; AOBuddy10's owner buy, template 143832)
    public int ResupplyShopPf = 1187; // nothing in reach: travel here to shop (Neutral Supermarket
    // Advanced, the Fair Trade instance - entered over proxy terminals; in-game an instance whose
    // playfield MODEL is 1187). The way back is not resupply's: follow/mission own the body next.
    // 0 = shop locally only.

    // --- Heal (ControlPriority.LowHealthNanoEmergency): the in-combat heal decision and the
    // out-of-combat recharger rest. In combat the higher heal wins - the stim (locked 40 s per
    // use, its own LockSkill(123,40)) or the best learned one-shot heal nano (cast held until
    // it lands, then the nano-cast recharge lockout). Out of combat the recharger rest cycle.
    public int HealNanoCombatPct = 50; // in combat: heal when nano falls under this % of max
    public int HealNanoOutOfCombatPct = 70; // out of combat: recharger when nano is under this % (more than 30% missing)
    public float HealRestMaxSeconds = 60f; // the recharger rest never sits longer than this, healed or not
    public float StimLockSeconds = 40f; // the stims' own record says LockSkill(FirstAid 123, 40) - the
    // First-Aid lock one stim use costs. Applied client-side in retail and nowhere in omnicell, so
    // this clock is ours alone.
    public float SkillLockFactor = 0f; // how much stat 382 (SkillLockModifier) shortens the stim lock:
    // lock = StimLockSeconds - SkillLockModifier * SkillLockFactor. The retail factor is unverified
    // (client-side, no capture) - 0 runs the flat lock, which never fires a stim into a real lock.

    // --- Mission run (blitz mode, no combat): roll at a mission terminal, take a find-item /
    // find-person mission, walk in, select the target, pocket the reward in a loot bag, walk out.
    public int MissionDifficulty = 6; // the terminal's difficulty slider (captures show 1/6/11)
    public int MissionSliderGoodBad = 0; // the six sliders as WIRE values -100..+100 (0 = middle =
    public int MissionSliderOrderChaos = 0; //   the terminal default; -100 = left end, e.g. all credits)
    public int MissionSliderOpenHidden = 0;
    public int MissionSliderPhysicalMystical = 0;
    public int MissionSliderHeadonStealth = 0;
    public int MissionSliderCreditsXp = 0;
    public float MissionTerminalRadius = 5f; // a terminal within this of the bot is used without the saved one
    public List<string> MissionZones = new(); // zone names or ids a mission may sit in (empty = any)
    public int WantUnseenRolls = 500; // want run: rolls near a nano's QL before it is judged no mission reward

    // Local control API (BotApi): /status /nav /inventory /log and POST /command on 127.0.0.1 only -
    // the monitor (tools/AOBuddyMonitor), the MCP (tools/aobuddy-mcp) and run-bot.ps1 talk to it. 0 = off.
    public int BotApiPort = 5592;

    // --- Hunt (HuntController, the 'hunt' command): the pets fight hostiles in a radius while the bot
    // stays put. Off until 'hunt on'. These are the per-bot defaults; a runtime 'hunt' command updates
    // them and saves this file, so everything stays in the one conf.
    public float HuntRadius = 40f; // mobs within this of the bot are hunted
    public int HuntMaxLevelMargin = 10; // a mob may be at most this far above the best attack pet's level
    public string HuntFactionMode = "Auto"; // Auto | On | Off - Shadowlands faction-safe hunting
    public List<string> HuntBlacklist = new(); // mob NAMES never hunted (mini-bosses you'd only die to)

    // --- Buff bots (BuffBotController, the 'buffs' command): getting buffs from a public buff bot
    // (Chewy on RubiKa, Codedoc on RubiKa2019). The handshake: un-teamed -> the bot invites -> we
    // accept -> it buffs -> it auto-kicks us. 4a.1 is the plumbing; the buff SELECTION comes later,
    // so for now the request tells are listed here verbatim.
    public string BuffBotName = ""; // the buff-bot toon's name; empty = no buff bot configured
    public List<string> BuffRequestTells = new(); // tells sent IN ORDER once teamed. ALWAYS list the
    // highest NCU buff first (owner, 2026-10-05: it expands Max NCU so the rest fit). 4a.2 computes
    // and orders these; until then they are listed here verbatim, NCU first.
    public float BuffHandshakeSeconds = 45f; // the invite/buff window (AOBuddy10 Scotty uses 45)

    // The bot WALKS to the buff spot before asking (4a.1b). WHICH spot (and which bot) is owned by the one
    // Buffs system per server (BuffCatalog.ServerProfile) - not here. BuffTravelToSpot off = ask from
    // wherever the bot already stands (no walk).
    public bool BuffTravelToSpot = true;
    public float BuffSpotArriveMeters = 6f; // "standing at the bot" radius (close enough for its toons to cast on us)

    // Pet buff-first (4b): when ON, a pet brain that wants a better pet it cannot yet summon for lack
    // of Matter Creation / Time and Space will ask the buff bot FIRST (near it and un-teamed), then
    // summon the better pet once the skills are up - rather than summoning a weaker one now. OFF by
    // default (the owner positions the bot and controls buffing); off, the brain summons the best it
    // can now and logs the buff opportunity.
    public bool PetAutoBuff = false;
    public float PetBuffWaitSeconds = 60f; // after asking, wait this long for the buffs before summoning anyway

    // Include the short summon-moment Skill Wrangler in a buff acquisition. OFF (default) gets only the
    // DURABLE stack (NCU + Mocham's + Composites) so the run is repeatable and the resulting skills are the
    // durable hold, not a wrangle peak; flip ON once the durable flow is proven and we want to actually summon.
    public bool BuffIncludeWrangle = false;

    // DRY RUN (owner's first-run safety): when ON, the pet brain performs NO actions - no summon, no
    // buff request, no learning a crystal, no casting anything. It only LOOKS: scans the bags for a
    // pet-summon nano crystal and narrates, step by step, what it found and what it WOULD do (the pet it
    // would go for, whether it can control it, the buffs it would ask for). Read it in the log, then turn
    // this off to let it act. Nothing here spends credits or uploads a nano. OFF by default.
    public bool PetDryRun = false;

    // Where this config was loaded from (set at load; never serialized). Save() writes the whole
    // config back here so a runtime setting change persists in the one self-contained conf file.
    [JsonIgnore] public string ConfigPath = "";

    public void Save()
    {
        if (string.IsNullOrEmpty(ConfigPath))
        {
            return;
        }

        try
        {
            // Merge our fields INTO the existing file so unknown keys the user keeps there (the
            // "_comment_*" docs, or anything else) survive a runtime save - don't rewrite from
            // scratch. Arrays are replaced wholesale (e.g. HuntBlacklist), not concatenated.
            JObject root;
            try
            {
                root = File.Exists(ConfigPath) ? JObject.Parse(File.ReadAllText(ConfigPath)) : new JObject();
            }
            catch
            {
                root = new JObject();
            }

            root.Merge(JObject.FromObject(this), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            File.WriteAllText(ConfigPath, root.ToString(Formatting.Indented));
        }
        catch
        {
            // Best-effort: a config save must never crash the bot.
        }
    }
}