// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: BuffCatalog.cs
//
// Last modified: 2026-10-05
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using System.IO;
using System.Text.RegularExpressions;
using AOBuddy20.Brains;
using AOBuddy20.Configuration;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serilog.Events;

namespace AOBuddy20.Controlling;

/// <summary>
///     BUFF CATALOG (step 4a.2, pet-focused) - the public buff bot's menu: each buff's tell code,
///     NCU and nano id, from GameData/ChewysBuffs.json (RubiKa). Ported from AOBuddy10. Dimension
///     aware: RubiKa2019 uses CodedocBuffs.json, whose selection model is level-locked and different
///     enough that its proper handling is 4a.3 - here it is loaded best-effort with the same shape.
///     This first pass serves ONLY the pet brain's buff-first summon: produce the tells to lift the
///     caster's Matter Creation / Time and Space enough to summon a better pet, highest NCU buff
///     FIRST (owner, 2026-10-05: it expands Max NCU so the rest fit), then one nano-skill buff.
///     SAFE SET: only buffs that raise BOTH MC and TS (the all-nano-skill composites and the
///     weapon/nano Skill Wrangler ladder) and that are receivable at any level (froob, Level &gt; 0)
///     - the level-gated Umbral wranglers and the multi-hour Mochams are left out until the full
///     optimizer adds proper receiver-requirement gating (TODO 4a.2-full).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class BuffCatalog
{
    public sealed class BuffEntry
    {
        public string Profession = "";
        public string Name = "";
        public string Effect = "";
        public int Ncu;
        public int? NanoId;
        public string Tell = "";

        // The ids the receiver gate / modifiers actually live on. A plain buff lands as its own
        // nano, so this is just { NanoId }; a team-cast buff (NCU line, Umbral) lands via separate
        // "landIds", and the receiver requirement + the stat it adds live on THOSE (owner, 2026-10-05).
        public List<int> LandIds = new();

        // --- Enriched from nanos.ocp at load (NanoLibrary), so the equations read verified pack data,
        // never a parsed effect string. 0 / empty when the nano is not in the pack.
        public int Strain; // NanoStrain (stat 75): the OVERWRITE group - two buffs sharing a strain do
        // not stack (the higher StackingOrder wins), so the planner never requests two of one strain.
        public int ReceiverLevel; // minimum RECEIVER level (target=18 Level> gate, +1); 0 = no level gate.
        public bool NeedsSl; // receiver must own Shadowlands (target=18 Expansion 389 BitAnd 2).
        public IReadOnlyDictionary<int, int> Modifies = new Dictionary<int, int>(); // stat -> flat amount added.

        /// <summary>Castable by the bot itself: the "Generic" buffs anyone who learned them can cast,
        /// or this bot's own profession's buffs. Everything else needs the matching buff bot.</summary>
        public bool SelfCastableBy(string myProfession) =>
            Profession.Equals("Generic", StringComparison.OrdinalIgnoreCase)
            || Profession.Equals(myProfession, StringComparison.OrdinalIgnoreCase);

        /// <summary>How much this buff adds to a stat (MC 130 / TS 131 / Max NCU 181 / ...), from the pack.</summary>
        public int Adds(int statId) => Modifies.TryGetValue(statId, out var v) ? v : 0;
    }

    // Raw requirement encoding in the pack (verified 2026-10-05 from the cast-criteria dump):
    private const int OpEqualTo = 0, OpGreaterThan = 2, OpBitAnd = 22; // requirement operators
    private const int TargetReceiver = 18, TargetSelf = 19; // criterion target: OnTarget vs OnSelf
    private const int StatLevel = 54, StatExpansion = 389; // Level gate; Expansion flag (BitAnd 2 = Shadowlands)
    private const int StatProfession = 60, StatVisualProfession = 368; // caster profession gates
    private const int ActionToUse = 3; // the cast/use action that carries the requirements

    private static readonly Regex Plus = new(@"\+(\d+)", RegexOptions.Compiled);

    private readonly ILogger<BuffCatalog> _logger;
    private readonly List<BuffEntry> _buffs = new();

    public bool Loaded { get; private set; }
    public IReadOnlyList<BuffEntry> Buffs => _buffs;

    public BuffCatalog(AccountInfo config, ILogger<BuffCatalog> logger)
    {
        _logger = logger;
        var is2019 = (config.Dimension ?? "").Replace(" ", "").Equals("RubiKa2019", StringComparison.OrdinalIgnoreCase);
        var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameData",
            is2019 ? "CodedocBuffs.json" : "ChewysBuffs.json");
        Load(file);
    }

    private void Load(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                _logger.LogWarning($"BUFFS: no buff catalog at {file} - computed buff plans are off (config tells still work).");
                return;
            }

            var doc = JObject.Parse(File.ReadAllText(file));

            // Two dimension shapes. RubiKa/Chewy: { professions:[ { name, buffs:[...] } ] } grouped by
            // profession, buff has 'tell' + 'id'/'landIds'. RubiKa2019/Codedoc: a FLAT { buffs:[...] },
            // each buff carrying its own 'section' (profession), 'code' (the tell) and 'ids'. Read either.
            if (doc["professions"] is JArray professions)
            {
                foreach (var p in professions)
                {
                    var prof = (string?)p["name"] ?? "";
                    foreach (var b in p["buffs"] ?? new JArray())
                    {
                        _buffs.Add(ParseBuff(b, prof));
                    }
                }
            }
            else
            {
                foreach (var b in doc["buffs"] ?? new JArray())
                {
                    _buffs.Add(ParseBuff(b, (string?)b["section"] ?? ""));
                }
            }

            Enrich();

            Loaded = _buffs.Count > 0;
            _logger.LogInformation($"BUFFS: {_buffs.Count} catalog entries from {Path.GetFileName(file)} " +
                                   $"({_buffs.Count(x => x.Strain > 0)} enriched from the nano pack).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"BUFFS: failed to read {Path.GetFileName(file)}.");
        }
    }

    // Normalize one buff from either dimension's JSON into a BuffEntry. Tell = Chewy 'tell' or Codedoc
    // 'code'; the candidate nano ids (where the gate/modifiers live) = 'id', else team 'landIds', else
    // Codedoc 'ids' (a shared name may list several; the enricher keeps the one actually carrying the effect).
    private static BuffEntry ParseBuff(JToken b, string profession)
    {
        var entry = new BuffEntry
        {
            Profession = profession,
            Name = (string?)b["name"] ?? "",
            Effect = (string?)b["effect"] ?? "",
            Ncu = (int?)b["ncu"] ?? 0,
            NanoId = (int?)b["id"],
            Tell = (string?)b["tell"] ?? (string?)b["code"] ?? "",
        };

        if (b["landIds"] is JArray land)
        {
            entry.LandIds.AddRange(land.Select(x => (int)x));
        }
        else if (entry.NanoId.HasValue)
        {
            entry.LandIds.Add(entry.NanoId.Value);
        }
        else if (b["ids"] is JArray ids)
        {
            entry.LandIds.AddRange(ids.Select(x => (int)x));
        }

        return entry;
    }

    // Fold the pack-verified facts (strain / receiver gate / flat modifiers) onto each entry, so every
    // equation reads from nanos.ocp rather than a parsed effect string. The receiver gate is read per
    // nano by <see cref="ReceiverGate" /> (it auto-detects whose level a Level> gate belongs to).
    private void Enrich()
    {
        if (!NanoLibrary.Loaded)
        {
            _logger.LogWarning("BUFFS: nano pack not loaded - catalog keeps JSON data only (no strain/gate/modifiers).");
            return;
        }

        foreach (var b in _buffs)
        {
            // The nano that actually carries the effect (several ids may share a name; take the one with
            // the most stat modifiers - the landed buff, not the team-cast wrapper). NCU's many tiers are
            // resolved per-tier in BestNcuBuff, so enriching off the representative one is harmless there.
            var rep = b.LandIds.Select(NanoLibrary.Find).Where(n => n != null).Cast<NanoProfile>()
                .OrderByDescending(n => n.Modifies.Count).FirstOrDefault();
            if (rep == null)
            {
                continue;
            }

            b.Strain = rep.Stat(75);
            var (level, sl) = ReceiverGate(rep);
            b.ReceiverLevel = level;
            b.NeedsSl = sl;
            b.Modifies = rep.Modifies;
        }
    }

    // The receiver's own gate on a nano - the Level minimum and the Shadowlands flag. Which target the
    // Level> gate belongs to is read from the data: a nano with a CASTER profession gate (profession ==
    // on OnSelf) is cast BY a bot, so self=caster and the receiver's reqs are on OnTarget (18); a nano
    // with no caster profession gate is the LANDED buff running on us, so self=receiver (19). SL is
    // always an OnTarget flag. Caster level/skill criteria are the buff bot's problem, never ours.
    private static (int Level, bool NeedsSl) ReceiverGate(NanoProfile nano)
    {
        var castByBot = nano.Actions.Any(a => a.ActionType == ActionToUse && a.Requirements.Any(r =>
            r.Target == TargetSelf && r.Operator == OpEqualTo &&
            (r.Stat == StatProfession || r.Stat == StatVisualProfession)));
        var receiverTarget = castByBot ? TargetReceiver : TargetSelf;

        var level = 0;
        var needsSl = false;
        foreach (var act in nano.Actions)
        {
            if (act.ActionType != ActionToUse)
            {
                continue;
            }

            foreach (var req in act.Requirements)
            {
                if (req.Stat == StatLevel && req.Operator == OpGreaterThan && req.Target == receiverTarget)
                {
                    level = Math.Max(level, req.Value + 1); // GreaterThan N is inclusive of N+1
                }
                else if (req.Stat == StatExpansion && req.Operator == OpBitAnd && (req.Value & 2) != 0 &&
                         req.Target == TargetReceiver)
                {
                    needsSl = true;
                }
            }
        }

        return (level, needsSl);
    }

    /// <summary>Can THIS receiver take this buff: its level clears the gate and it owns SL if required.</summary>
    public static bool Castable(BuffEntry b, int myLevel, bool paid) =>
        b.ReceiverLevel <= myLevel && (!b.NeedsSl || paid);

    /// <summary>
    ///     The cheapest single-skill rung that adds at least <paramref name="need" /> to a nano skill
    ///     (<paramref name="statId" />), or the biggest castable rung if none reaches it. A "rung" is a
    ///     SINGLE-skill buff - it raises this skill but not its partner (<paramref name="otherStatId" />)
    ///     - which is exactly how the Mocham's/Infuse/Mastery/Teachings ladder (one strain per skill)
    ///     separates from the all-skills Composite line (which raises both and STACKS on top). Rungs of
    ///     one skill share a strain, so only one is ever held; we pick the lowest that clears the control
    ///     floor and bank the NCU (owner, 2026-10-05: +90 over +140 when it still holds). Level/SL gated.
    /// </summary>
    public BuffEntry? BestRung(int statId, int otherStatId, int need, int myLevel, bool paid) =>
        BestRungIn(_buffs, statId, otherStatId, need, myLevel, paid);

    // As BestRung, over a given candidate pool (bot catalog plus our learned self-buffs). A rung is a
    // single-skill buff (adds this skill, not its partner); pool spanning both sources lets a bigger
    // self-cast rung win where one exists (owner, 2026-10-05: self-buffs sometimes beat the bot's).
    private static BuffEntry? BestRungIn(IEnumerable<BuffEntry> pool, int statId, int otherStatId, int need,
        int myLevel, bool paid)
    {
        var rungs = pool
            .Where(b => b.Adds(statId) > 0 && b.Adds(otherStatId) == 0 && Castable(b, myLevel, paid))
            .OrderBy(b => b.Adds(statId))
            .ToList();
        if (rungs.Count == 0)
        {
            return null;
        }

        return rungs.FirstOrDefault(b => b.Adds(statId) >= need) ?? rungs[^1];
    }

    /// <summary>
    ///     The buffs the bot can put on ITSELF: its learned nanos (<paramref name="me" />.SpellList) that
    ///     raise a nano skill or an ability and that it can cast right now, turned into catalog candidates
    ///     (Profession = ours so they route self-cast, no bot tell). Merged into the control-plan pool so a
    ///     profession's own self-buff competes with - and where bigger, beats - the bot's, and SELF-ONLY
    ///     buffs (no bot equivalent) are available at all. Empty for a class with no such self-buffs (an
    ///     Engineer has no self MC/TS - this mainly feeds the MP brain and self-cast survival).
    /// </summary>
    private List<BuffEntry> SelfBuffCandidates(LocalPlayer me, string myProfession)
    {
        var list = new List<BuffEntry>();
        var learned = me?.SpellList;
        if (learned == null)
        {
            return list;
        }

        foreach (var id in learned)
        {
            var np = NanoLibrary.Find(id);
            if (np == null)
            {
                continue;
            }

            // Relevant to the MC/TS plan only: it raises a nano skill or a trickle ability.
            var relevant = np.Modify(130) > 0 || np.Modify(131) > 0
                           || np.Modify(StatStrength) > 0 || np.Modify(StatAgility) > 0
                           || np.Modify(StatStamina) > 0 || np.Modify(StatIntelligence) > 0;
            if (!relevant)
            {
                continue;
            }

            // Only if we can actually cast it now (learned AND reqs met) - then its gate is moot.
            if (!ItemData.Find(id, out NanoItem ni) || ni == null || !SafeMeetsUseReqs(ni, me))
            {
                continue;
            }

            var entry = new BuffEntry
            {
                Profession = myProfession, // marks it self-castable and routes to self-cast
                Name = NanoLibrary.NameOf(id),
                NanoId = id,
                Ncu = ni.NCU,
                Strain = np.Stat(75),
                Modifies = np.Modifies,
                Tell = "", // self only - no bot tell
            };
            entry.LandIds.Add(id);
            list.Add(entry);
        }

        return list;
    }

    private static bool SafeMeetsUseReqs(NanoItem ni, LocalPlayer me)
    {
        try
        {
            return ni.MeetsUseReqs(me, false, false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The MC rung (stat 130, partner TS 131) and the TS rung (131, partner 130).</summary>
    public BuffEntry? BestMcRung(int need, int myLevel, bool paid) => BestRung(130, 131, need, myLevel, paid);
    public BuffEntry? BestTsRung(int need, int myLevel, bool paid) => BestRung(131, 130, need, myLevel, paid);

    // Ability stat ids and the VERIFIED trickle factors into MC/TS (GameData/SkillTrickle.json, factor
    // order [Str,Agi,Sta,Int,Sen,Psy]): MC(130) = Stamina 0.2 + Int 0.8; TS(131) = Agility 0.2 + Int 0.8.
    // A skill's trickle = floor( Sum(ability * factor) / 4 ), so +12 to all abilities = floor(12/4) = +3.
    private const int StatStrength = 16, StatAgility = 17, StatStamina = 18, StatIntelligence = 19;

    /// <summary>How much an ability buff trickles into a nano skill (130/131), from the verified factors.</summary>
    private static int AttrTrickle(BuffEntry b, int statId)
    {
        double sum = statId switch
        {
            130 => b.Adds(StatStamina) * 0.2 + b.Adds(StatIntelligence) * 0.8,
            131 => b.Adds(StatAgility) * 0.2 + b.Adds(StatIntelligence) * 0.8,
            _ => 0,
        };
        return (int)Math.Floor(sum / 4.0);
    }

    /// <summary>
    ///     How much a RUNNING nano (by id) contributes to a nano skill (130/131) right now - its flat
    ///     modifier plus any ability trickle - read from the pack. Lets a brain back out the UNBUFFED
    ///     base (current skill minus the running buffs) so the control plan starts from solid ground.
    /// </summary>
    public static int SkillContributionOf(int nanoId, int statId)
    {
        var np = NanoLibrary.Find(nanoId);
        if (np == null)
        {
            return 0;
        }

        double trickle = statId switch
        {
            130 => np.Modify(StatStamina) * 0.2 + np.Modify(StatIntelligence) * 0.8,
            131 => np.Modify(StatAgility) * 0.2 + np.Modify(StatIntelligence) * 0.8,
            _ => 0,
        };
        return np.Modify(statId) + (int)Math.Floor(trickle / 4.0);
    }

    /// <summary>The NanoStrain (stat 75) of a nano by id, 0 if unknown - for the running-buff skip set.</summary>
    public static int StrainOf(int nanoId) => NanoLibrary.Find(nanoId)?.Stat(75) ?? 0;

    /// <summary>The durable (no-wrangle) buff plan to CONTROL a pet, and whether it holds at the floor.</summary>
    public sealed class ControlPlan
    {
        public bool CanControl;
        public int DurableMc, DurableTs; // projected skill with the durable buffs, no wrangle
        public int FloorMc, FloorTs; // control floor = ceil(controlFloor * req), per skill
        public double MarginPct; // min(durable/req) * 100 - how far above the 80% line
        public NcuPick? Ncu; // the NCU buff to request first
        public BuffEntry? McRung, TsRung; // the chosen single-skill rungs (lowest that holds)
        public readonly List<BuffEntry> Stackers = new(); // the both-skill + attribute buffs applied
        public int MaxNcu, NcuUsed, NcuFree; // NCU budget once the durable set is up
    }

    /// <summary>
    ///     Control-first: can we DURABLY hold a pet needing <paramref name="reqMc" /> / <paramref name="reqTs" />
    ///     at the 80% floor WITHOUT the (short) wrangle, and what is the minimal durable buff set? Takes the
    ///     UNBUFFED base skills (<paramref name="baseMc" />/<paramref name="baseTs" />) so the caller controls
    ///     the baseline (the first live test strips buffs, so me's live stats == base). Builds the stack: the
    ///     best castable both-skill buff per strain (Nano Expertise, and the Composite line if paid - they
    ///     stack across strains), the attribute trickle, then the LOWEST single-skill rung that still clears
    ///     the floor (banking NCU per owner's +90-over-+140 rule). CanControl is false when even the biggest
    ///     rung cannot reach the floor - the caller must then summon a SMALLER pet (the sustain-gate).
    /// </summary>
    public ControlPlan BuildControlPlan(LocalPlayer me, int baseMc, int baseTs, int reqMc, int reqTs, bool paid,
        string myProfession = "", double controlFloor = 0.80)
    {
        var plan = new ControlPlan
        {
            FloorMc = (int)Math.Ceiling(controlFloor * reqMc),
            FloorTs = (int)Math.Ceiling(controlFloor * reqTs),
        };
        if (!Loaded || me == null)
        {
            return plan;
        }

        var myLevel = me.TryGetStat(Stat.Level, out var lvl) ? lvl : 0;

        // Candidate pool = the bot catalog PLUS our own learned self-buffs, so a profession's self-buff
        // competes with the bot's per strain (and self-only buffs are available at all).
        var pool = _buffs.Concat(SelfBuffCandidates(me, myProfession)).ToList();

        plan.Ncu = BestNcuBuff(me);

        // Both-skill stackers: the best castable buff of each distinct strain that raises MC AND TS.
        // Different strains stack, so we take one per strain (Composite Nano Expertise strain 91 always;
        // the Composite line strain 165 when paid). TODO: for the multi-tier Composite strain this takes
        // the biggest - NCU-minimal tier selection there is a later refinement.
        var both = pool
            .Where(b => b.Adds(130) > 0 && b.Adds(131) > 0 && Castable(b, myLevel, paid))
            .GroupBy(b => b.Strain)
            .Select(g => g.OrderByDescending(b => Math.Min(b.Adds(130), b.Adds(131))).First())
            .ToList();

        // Attribute buffs (raise abilities, not the skills directly) - best castable per strain, by MC trickle.
        var attrs = pool
            .Where(b => Castable(b, myLevel, paid) && b.Adds(130) == 0 && b.Adds(131) == 0 && AttrTrickle(b, 130) > 0)
            .GroupBy(b => b.Strain)
            .Select(g => g.OrderByDescending(b => AttrTrickle(b, 130)).First())
            .ToList();

        plan.Stackers.AddRange(both);
        plan.Stackers.AddRange(attrs);

        var fixedMc = baseMc + both.Sum(b => b.Adds(130)) + attrs.Sum(b => AttrTrickle(b, 130));
        var fixedTs = baseTs + both.Sum(b => b.Adds(131)) + attrs.Sum(b => AttrTrickle(b, 131));

        var needMc = plan.FloorMc - fixedMc;
        var needTs = plan.FloorTs - fixedTs;
        plan.McRung = needMc > 0 ? BestRungIn(pool, 130, 131, needMc, myLevel, paid) : null;
        plan.TsRung = needTs > 0 ? BestRungIn(pool, 131, 130, needTs, myLevel, paid) : null;

        plan.DurableMc = fixedMc + (plan.McRung?.Adds(130) ?? 0);
        plan.DurableTs = fixedTs + (plan.TsRung?.Adds(131) ?? 0);
        plan.CanControl = plan.DurableMc >= plan.FloorMc && plan.DurableTs >= plan.FloorTs;
        plan.MarginPct = reqMc > 0 && reqTs > 0
            ? Math.Min((double)plan.DurableMc / reqMc, (double)plan.DurableTs / reqTs) * 100.0
            : 0;

        // NCU budget once the durable set is up (the wrangle is separate - summon-moment only).
        var baseMaxNcu = me.TryGetStat(Stat.MaxNCU, out var mncu) ? mncu : 0;
        plan.MaxNcu = baseMaxNcu + (plan.Ncu?.MaxNcuAdded ?? 0);
        plan.NcuUsed = both.Sum(b => b.Ncu) + attrs.Sum(b => b.Ncu)
                       + (plan.McRung?.Ncu ?? 0) + (plan.TsRung?.Ncu ?? 0);
        plan.NcuFree = plan.MaxNcu - plan.NcuUsed;

        return plan;
    }

    /// <summary>How a buff is obtained.</summary>
    public enum BuffSource
    {
        SelfCast, // the bot casts it on itself (a Generic buff it learned, or its own profession's buff)
        BuffBot, // request it from the configured public buff bot via a tell
        Unavailable, // self-castable in principle but not learned, and no bot/tell to ask - out of reach
    }

    /// <summary>One concrete step to obtain a buff: cast it ourselves, or tell a bot.</summary>
    public sealed class BuffAction
    {
        public BuffSource Source;
        public string Name = "";
        public int Ncu; // the buff's NCU footprint (0 for the NCU buff's own tell line)
        public int SelfCastNanoId; // when Source == SelfCast
        public string BotName = ""; // when Source == BuffBot
        public string Tell = ""; // when Source == BuffBot

        public string Describe => Source switch
        {
            BuffSource.SelfCast => $"self-cast {Name} (nano {SelfCastNanoId})",
            BuffSource.BuffBot => $"tell {BotName} \"{Tell}\" for {Name}",
            _ => $"{Name} UNAVAILABLE (not learned, no bot)",
        };
    }

    /// <summary>
    ///     How to obtain one buff: self-cast when it is a Generic we have learned or our own profession's
    ///     buff we have learned; otherwise a tell to the configured bot (one public bot casts many
    ///     professions). Self-castable-in-principle but not learned falls back to the bot if it lists the
    ///     buff, else Unavailable. <paramref name="isLearned" /> tests whether a nano is in our library.
    /// </summary>
    public BuffAction RouteFor(BuffEntry b, string myProfession, string botName, Func<int, bool> isLearned)
    {
        var nanoId = b.NanoId ?? (b.LandIds.Count > 0 ? b.LandIds[0] : 0);
        var action = new BuffAction { Name = b.Name, Ncu = b.Ncu };

        if (b.SelfCastableBy(myProfession) && nanoId != 0 && isLearned(nanoId))
        {
            action.Source = BuffSource.SelfCast;
            action.SelfCastNanoId = nanoId;
            return action;
        }

        if (!string.IsNullOrWhiteSpace(botName) && !string.IsNullOrWhiteSpace(b.Tell))
        {
            action.Source = BuffSource.BuffBot;
            action.BotName = botName;
            action.Tell = b.Tell;
            return action;
        }

        action.Source = BuffSource.Unavailable;
        return action;
    }

    /// <summary>
    ///     The ordered actions to put a <see cref="ControlPlan" /> up: NCU buff FIRST (it expands Max NCU
    ///     so the rest fit), then the both-skill + attribute stackers, then the single-skill rungs. Each is
    ///     routed to self-cast or a bot tell. The (short) wrangle is NOT here - it is a summon-moment extra,
    ///     added by the summon step only when the pet's cast requirement needs it.
    /// </summary>
    private const int NcuStrain = 257; // the Fixer Max-NCU line's NanoStrain

    public List<BuffAction> RoutePlan(ControlPlan plan, string myProfession, string botName, Func<int, bool> isLearned,
        ISet<int>? skipStrains = null)
    {
        var actions = new List<BuffAction>();
        if (plan == null)
        {
            return actions;
        }

        var skip = skipStrains ?? new HashSet<int>();

        // NCU buff first - unless it is already up with time to spare (its strain is in skip).
        if (plan.Ncu != null && !string.IsNullOrWhiteSpace(botName) && !skip.Contains(NcuStrain))
        {
            actions.Add(new BuffAction
            {
                Source = BuffSource.BuffBot, Name = $"+{plan.Ncu.MaxNcuAdded} Max NCU",
                BotName = botName, Tell = plan.Ncu.Tell,
            });
        }

        foreach (var b in plan.Stackers)
        {
            if (!skip.Contains(b.Strain))
            {
                actions.Add(RouteFor(b, myProfession, botName, isLearned));
            }
        }

        if (plan.McRung != null && !skip.Contains(plan.McRung.Strain))
        {
            actions.Add(RouteFor(plan.McRung, myProfession, botName, isLearned));
        }

        if (plan.TsRung != null && plan.TsRung != plan.McRung && !skip.Contains(plan.TsRung.Strain))
        {
            actions.Add(RouteFor(plan.TsRung, myProfession, botName, isLearned));
        }

        return actions;
    }

    private const int StatMaxHealth = 1;
    private static readonly int[] AcStats = { 90, 91, 92, 93, 94, 95, 96, 97 }; // the eight ACs

    // Classify a buff for the pet-tank survival fill: which defensive category it serves and how big.
    // Category is read from the pack modifiers where they are flat (HP = MaxHealth, AC = the AC stats);
    // HoT / shield / evade are tick-or-special effects, so the curated effect text is the classifier
    // (amounts/gates still come from the pack). Returns tier -1 when the buff is not a survival buff.
    private static (int Tier, int Magnitude) SurvivalRank(BuffEntry b)
    {
        // Skip the control-side buffs (skill / NCU) - those are the pet's, not survival.
        if (b.Adds(130) > 0 || b.Adds(131) > 0 || b.Adds(181) > 0)
        {
            return (-1, 0);
        }

        var hp = b.Adds(StatMaxHealth);
        var ac = AcStats.Sum(b.Adds);
        var eff = b.Effect;
        var isHot = eff.Contains("HoT", StringComparison.OrdinalIgnoreCase);
        var isShield = eff.Contains("Shield", StringComparison.OrdinalIgnoreCase)
                       || eff.Contains("Absorb", StringComparison.OrdinalIgnoreCase)
                       || eff.Contains("Layers", StringComparison.OrdinalIgnoreCase)
                       || eff.Contains("Reflect", StringComparison.OrdinalIgnoreCase);
        var isEvade = eff.Contains("Evade", StringComparison.OrdinalIgnoreCase)
                      || eff.Contains(" RS", StringComparison.OrdinalIgnoreCase);

        // Pet-tank priority (higher tier picked first): HP > HoT > AC > shield > evade.
        if (hp > 0) return (5, hp);
        if (isHot) return (4, b.Ncu); // HoT magnitude is a tick effect, not a flat modify - rank by NCU
        if (ac > 0) return (3, ac);
        if (isShield) return (2, b.Ncu);
        if (isEvade) return (1, b.Ncu);
        return (-1, 0);
    }

    /// <summary>
    ///     Spend leftover NCU on durable survivability for hunting: the best castable buff of each
    ///     defensive category (HP / HoT / AC / shield / evade), in that priority, one per NanoStrain so
    ///     nothing overwrites, each added only while it fits the remaining NCU. Level/SL gated, and routed
    ///     to self-cast or a bot tell like the control plan. <paramref name="usedStrains" /> seeds the
    ///     strains the control set already occupies so survival never collides with it.
    /// </summary>
    public List<BuffAction> SurvivalFill(LocalPlayer me, int ncuBudget, bool paid, string myProfession,
        string botName, Func<int, bool> isLearned, IEnumerable<int>? usedStrains = null)
    {
        var actions = new List<BuffAction>();
        if (!Loaded || me == null || ncuBudget <= 0)
        {
            return actions;
        }

        var myLevel = me.TryGetStat(Stat.Level, out var lvl) ? lvl : 0;
        var used = new HashSet<int>(usedStrains ?? Enumerable.Empty<int>());
        var budget = ncuBudget;

        var ranked = _buffs
            .Select(b => (Buff: b, Rank: SurvivalRank(b)))
            .Where(x => x.Rank.Tier >= 0 && x.Buff.Ncu > 0 && Castable(x.Buff, myLevel, paid))
            .ToList();

        // One buff per category tier, highest tier first; within a tier the biggest that fits and whose
        // strain is free. (A buff with no strain in the pack - Strain 0 - is treated as its own group.)
        foreach (var tier in ranked.Select(x => x.Rank.Tier).Distinct().OrderByDescending(t => t))
        {
            var pick = ranked
                .Where(x => x.Rank.Tier == tier && x.Buff.Ncu <= budget && !used.Contains(x.Buff.Strain))
                .OrderByDescending(x => x.Rank.Magnitude)
                .FirstOrDefault();
            if (pick.Buff == null)
            {
                continue;
            }

            actions.Add(RouteFor(pick.Buff, myProfession, botName, isLearned));
            used.Add(pick.Buff.Strain);
            budget -= pick.Buff.Ncu;
        }

        return actions;
    }

    /// <summary>The NCU buff's tell (the Fixer Max-NCU ladder), or null if the catalog has none.</summary>
    public string? NcuTell()
    {
        var ncu = FindNcuEntry();
        return string.IsNullOrEmpty(ncu?.Tell) ? null : ncu.Tell;
    }

    /// <summary>The chosen Max-NCU buff for a receiver: the tell to send, and how much Max NCU it adds.</summary>
    public sealed class NcuPick
    {
        public string Tell = "";
        public int MaxNcuAdded; // +Max NCU the selected tier grants (e.g. +60 at L50)
        public int LandId; // the landed nano actually running on the receiver
        public int ReceiverLevel; // the level that tier needs
    }

    private BuffEntry? FindNcuEntry() =>
        _buffs.FirstOrDefault(b => b.Tell.Equals("ncu", StringComparison.OrdinalIgnoreCase)
                                   || b.Effect.Contains("NCU", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     The best Max-NCU buff the configured bot will cast that THIS receiver's level allows - the
    ///     highest +Max NCU tier whose receiver-level gate is met. The Fixer NCU line is one tell that
    ///     casts a whole ladder; the bot lands the tier matching your level, so we read each tier's
    ///     receiver level (landed nano, target=19) and its +Max NCU (stat 181 modify) from the pack and
    ///     pick the biggest you qualify for. Requested FIRST (owner, 2026-10-05: it expands Max NCU so
    ///     the rest of the stack fits). Null when there is no NCU buff or none fits the level.
    /// </summary>
    public NcuPick? BestNcuBuff(LocalPlayer me)
    {
        var entry = FindNcuEntry();
        if (!Loaded || me == null || entry == null || entry.LandIds.Count == 0)
        {
            return null;
        }

        var myLevel = me.TryGetStat(Stat.Level, out var lvl) ? lvl : 0;

        NcuPick? best = null;
        foreach (var id in entry.LandIds)
        {
            var nano = NanoLibrary.Find(id);
            if (nano == null)
            {
                continue;
            }

            var (needLevel, _) = ReceiverGate(nano); // auto-detects the receiver's own level gate
            var added = nano.Modify(181); // Stat.MaxNCU
            if (added <= 0 || needLevel > myLevel)
            {
                continue;
            }

            if (best == null || added > best.MaxNcuAdded)
            {
                best = new NcuPick { Tell = entry.Tell, MaxNcuAdded = added, LandId = id, ReceiverLevel = needLevel };
            }
        }

        return best;
    }

    /// <summary>
    ///     The safe MC/TS buffs (raise BOTH, receivable at any level), each with its parsed +N gain,
    ///     smallest first. The all-nano-skill composites and the weapon/nano Skill Wrangler ladder;
    ///     the level-gated Umbral and the multi-hour Mochams are excluded (TODO 4a.2-full: proper
    ///     receiver-requirement gating lets those back in).
    /// </summary>
    public List<(BuffEntry Entry, int Gain)> SafeNanoSkillBuffs()
    {
        var result = new List<(BuffEntry, int)>();
        foreach (var b in _buffs)
        {
            var eff = b.Effect;
            var raisesBoth = eff.Contains("Nano skills", StringComparison.OrdinalIgnoreCase)
                             || eff.Contains("Nano Skills", StringComparison.OrdinalIgnoreCase);
            if (!raisesBoth)
            {
                continue; // single-skill (MatCrea-only / SpaceTime-only) needs two tells - optimizer's job
            }

            if (eff.Contains("Umbral", StringComparison.OrdinalIgnoreCase)
                || eff.Contains("Hour", StringComparison.OrdinalIgnoreCase))
            {
                continue; // level-gated - excluded until receiver-req gating (TODO)
            }

            var m = Plus.Match(eff);
            if (!m.Success)
            {
                continue;
            }

            result.Add((b, int.Parse(m.Groups[1].Value)));
        }

        return result.OrderBy(x => x.Item2).ToList();
    }

    /// <summary>
    ///     The ordered tells to lift our nano skills by <paramref name="need" />: the NCU buff
    ///     first, then the SMALLEST safe nano-skill buff closing the gap - or the biggest if none
    ///     closes it (we over-buff for the summon moment, then downshift; see PETBRAIN-DESIGN.md).
    ///     The raw-gap entry point: the caller computes the need from whatever skill pair its
    ///     target gates on (the MP pet lines gap on BioMet (128) / MatMet (127) + TS, not only
    ///     MC/TS). Empty when the catalog is not loaded.
    /// </summary>
    public List<string> PlanForSkillGap(LocalPlayer me, int need)
    {
        var tells = new List<string>();
        if (!Loaded || me == null || need <= 0)
        {
            return tells;
        }

        var ncu = NcuTell();
        if (ncu != null)
        {
            tells.Add(ncu);
        }

        var cands = SafeNanoSkillBuffs();
        if (cands.Count > 0)
        {
            var pick = cands.FirstOrDefault(x => x.Gain >= need);
            if (pick.Entry == null)
            {
                pick = cands[^1]; // none closes the gap: the biggest we safely can
            }

            tells.Add(pick.Entry.Tell);
        }

        return tells;
    }

    /// <summary>
    ///     The ordered tells to summon a pet needing <paramref name="reqMc" /> / <paramref name="reqTs" />
    ///     MC/TS: the gap is read from our current (buffed) MC and TS.
    /// </summary>
    public List<string> PlanForPetSummon(LocalPlayer me, int reqMc, int reqTs)
    {
        if (!Loaded || me == null)
        {
            return new List<string>();
        }

        var curMc = me.TryGetStat(Stat.MaterialCreation, out var mc) ? mc : 0;
        var curTs = me.TryGetStat(Stat.SpaceTime, out var ts) ? ts : 0;
        var need = Math.Max(Math.Max(0, reqMc - curMc), Math.Max(0, reqTs - curTs));
        return PlanForSkillGap(me, need);
    }
}
