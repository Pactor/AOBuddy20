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

namespace AOBuddy20.Configuration;

public class AccountInfo
{
    public string Character = "";
    public string Dimension = ""; // "RubiKa" (default) or "RubiKa2019"
    public string Owner = ""; // the character whose /tells are obeyed (empty: no one can command the bot)
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

    // Local control API (BotApi): /status /nav /inventory /log and POST /command on 127.0.0.1 only -
    // the monitor (tools/AOBuddyMonitor), the MCP (tools/aobuddy-mcp) and run-bot.ps1 talk to it. 0 = off.
    public int BotApiPort = 5591;
}