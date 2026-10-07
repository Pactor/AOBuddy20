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

using System.Diagnostics;
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

        // Self-vs-others DELIVERY (AOBuddy10's method, from the pack's cast-function type / effect target,
        // NOT the landed nano's own effect targets): a buff bot can only give us a buff that reaches OTHERS.
        public bool CanCastOnOthers = true; // false = self-only (CastNano / only User+Wearer targets) - a bot cannot put it on us.
        public bool NeedsTeam; // true = delivered by TeamCastNano - we must be teamed with the caster; false = single-target, just targeted.
        public int RepNanoId; // the landed nano the gate/modifiers were read from (what actually runs on us).

        /// <summary>Castable by the bot itself: the "Generic" buffs anyone who learned them can cast,
        /// or this bot's own profession's buffs. Everything else needs the matching buff bot.</summary>
        public bool SelfCastableBy(string myProfession) =>
            Profession.Equals("Generic", StringComparison.OrdinalIgnoreCase)
            || Profession.Equals(myProfession, StringComparison.OrdinalIgnoreCase);

        /// <summary>How much this buff adds to a stat (MC 130 / TS 131 / Max NCU 181 / ...), from the pack.</summary>
        public int Adds(int statId) => Modifies.TryGetValue(statId, out var v) ? v : 0;
    }

    /// <summary>
    ///     How much a buff adds to one NANO skill for the pet-line planner: the pack's flat Modify
    ///     where it names the stat directly, otherwise the all-nano-skills effect text ("+20 Nano
    ///     Skills") parsed for its +N - the fallback catches a catalog entry whose rep nano came
    ///     back without the six per-skill modifies.
    /// </summary>
    public static int GainFor(BuffEntry b, int statId)
    {
        var adds = b.Adds(statId);
        if (adds != 0)
        {
            return adds;
        }

        if (b.Effect.Contains("nano skill", StringComparison.OrdinalIgnoreCase))
        {
            var m = Plus.Match(b.Effect);
            if (m.Success)
            {
                return int.Parse(m.Groups[1].Value);
            }
        }

        return 0;
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
    private readonly ServerProfile _server; // THE one server->bot mapping (resolved once, below)
    private readonly string _botOverride;    // optional config.BuffBotName; empty = use the server's default

    public bool Loaded { get; private set; }
    public IReadOnlyList<BuffEntry> Buffs => _buffs;

    // ---- The one Buffs system: everything server-specific lives here ------------------------
    // Nothing outside this class names RubiKa/RubiKa2019/Chewy/Codedoc. The json is request DATA only
    // (which code, is-it-team, nano ids) - it NEVER drives behaviour; all actions are server-agnostic code.

    /// <summary>Who to /tell "cast &lt;code&gt;" on this server (config.BuffBotName overrides the default).</summary>
    public string BotName => string.IsNullOrWhiteSpace(_botOverride) ? _server.DefaultBot : _botOverride;

    /// <summary>The playfield to stand in to be buffed on this server.</summary>
    public int SpotPf => _server.SpotPf;

    /// <summary>Where in that playfield to stand (close enough for the bot's toons to cast on us).</summary>
    public Vector3 SpotPos => _server.SpotPos;

    /// <summary>The server we resolved (true = RubiKa2019/Codedoc, false = RubiKa/Chewy).</summary>
    public bool Is2019 => _server.Is2019;

    /// <summary>The wire tell for a buff: "cast &lt;code&gt;" (both bots; guarded against a double "cast").
    /// This is the "pass the buff, get the tell to use" the caller asks for - the server/bot is resolved here.</summary>
    public string TellFor(BuffEntry buff) => WireTell(buff?.Tell);

    public static string WireTell(string code) =>
        string.IsNullOrWhiteSpace(code) || code.StartsWith("cast ", StringComparison.OrdinalIgnoreCase)
            ? code
            : "cast " + code;

    public BuffCatalog(AccountInfo config, ILogger<BuffCatalog> logger)
    {
        _logger = logger;
        _server = ServerProfile.Resolve(config.Dimension); // the ONE dimension read
        _botOverride = config.BuffBotName ?? "";
        Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameData", _server.JsonFile));
    }

    /// <summary>
    ///     The per-server facts the buff system owns: who the bot is (the /tell target's default name),
    ///     which catalog json, and where to stand. The INVITING toon is NOT here - it varies (Chewysfix,
    ///     Chewystrader, Codetrader, Enfocode...), so teaming accepts whatever non-owner invite arrives
    ///     while a team buff is outstanding rather than matching a name. AOBuddy10's verified values.
    /// </summary>
    public sealed class ServerProfile
    {
        public bool Is2019;
        public string DefaultBot = "";
        public string JsonFile = "";
        public int SpotPf;
        public Vector3 SpotPos;

        public static ServerProfile Resolve(string dimension)
        {
            var is2019 = (dimension ?? "").Replace(" ", "").Equals("RubiKa2019", StringComparison.OrdinalIgnoreCase);
            return is2019
                ? new ServerProfile
                {
                    Is2019 = true, DefaultBot = "Codedoc", JsonFile = "CodedocBuffs.json",
                    SpotPf = 800, SpotPos = new Vector3(632.6f, 66.81f, 723.9f), // Borealis
                }
                : new ServerProfile
                {
                    Is2019 = false, DefaultBot = "Chewysfix", JsonFile = "ChewysBuffs.json",
                    SpotPf = 655, SpotPos = new Vector3(3260f, 0f, 865f), // ICC
                };
        }
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
        if (entry.NanoId.HasValue)
        {
            entry.LandIds.Add(entry.NanoId.Value);
        }
        if (b["ids"] is JArray ids)
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
            // The rep is the nano that LANDS ON THE RECEIVER - the BUFF, not a self-debuff the same spell
            // casts on the caster (a single-target wrangle's main nano modifies the target +131 AND casts a
            // -143 debuff onto the Trader; both have the same modifier count, so count alone picked the
            // debuff). Rank by the net modifier total (positive buff beats negative debuff), then count.
            var rep = b.LandIds.Select(NanoLibrary.Find).Where(n => n != null).Cast<NanoProfile>()
                .OrderByDescending(n => n.Modifies.Values.Sum())
                .ThenByDescending(n => n.Modifies.Count).FirstOrDefault();
            if (rep == null)
            {
                continue;
            }

            b.Strain = rep.Stat(75);
            b.RepNanoId = rep.NanoId;
            var (level, sl) = ReceiverGate(rep);
            b.ReceiverLevel = level;
            b.NeedsSl = sl;
            b.Modifies = rep.Modifies;

            // Self-vs-others delivery: find how the REP (landed) nano is delivered. A wrapper among the
            // entry's own ids that casts the rep decides it - TeamCastNano => team (needs team), AreaCastNano
            // => others (no team), CastNano => SELF ONLY. With no wrapper the rep is cast directly: it can go
            // on another character only if it has a Target(3) landing effect. This is why the +500 Firewalled
            // (rep self-cast via CastNano) is self-only even though its landed nano has a Target(3) effect.
            var wrappers = b.LandIds.Select(NanoLibrary.Find).Where(n => n != null).Cast<NanoProfile>().ToList();
            var casts = wrappers.SelectMany(n => n.Casts).Where(c => c.NanoId == rep.NanoId).ToList();
            if (casts.Count > 0)
            {
                b.NeedsTeam = casts.Any(c => c.Func == FuncTeamCastNano);
                b.CanCastOnOthers = casts.Any(c => c.Func is FuncTeamCastNano or FuncAreaCastNano);
                b.ReceiverLevel = casts.Max(x =>
                {
                    var nn = NanoLibrary.Find(x.NanoId);
                    if (nn != null)
                    {
                        return ReceiverGate(nn).Level;
                    }

                    throw new KeyNotFoundException($"Nano {x.NanoId} not found???");
                });
            }
            else
            {
                b.NeedsTeam = false;
                b.CanCastOnOthers = rep.TargetsOthers;
                b.ReceiverLevel = 1;
            }

            
        }
    }

    // Cast-function types (nanos.ocp effect functions) - the self-vs-others delivery signal.
    private const int FuncCastNano = 53051, FuncTeamCastNano = 53066, FuncAreaCastNano = 53087;

    // The six nano skills (Matter Creation, Time&Space, Bio/Material/Psycho Met, Sensory Imp). A buff is a
    // NANO-SKILL buff if it directly raises one - those do not stack within a profession (per skill, highest
    // only). Attribute buffs raise abilities (16..21) and only TRICKLE to skill - a different line that stacks.
    private static readonly int[] NanoSkillStats = { 122, 127, 128, 129, 130, 131 };
    private static bool IsNanoSkillBuff(BuffEntry b) => NanoSkillStats.Any(ns => b.Adds(ns) > 0);

    /// <summary>
    ///     Is a RUNNING nano a cancel candidate for a pet line (the pet-first cycle's inter-line
    ///     fix, owner 2026-10-07): it raises nano skills, but NONE of this line's pair - the attack
    ///     line's MatCrea singles once the heal line (BioMet+TS) is planned. Composites, wrangles
    ///     and attribute buffs raise the line's skills (directly or by trickle) and stay; the NCU
    ///     buff and survival buffs raise no nano skill at all and stay.
    /// </summary>
    public static bool IsLineForeignNanoSkill(int nanoId, int statA, int statB)
    {
        return IsLineForeignNanoSkill(nanoId, new[] { statA, statB });
    }

    /// <summary>The same test against an arbitrary set of needed stats (the pet-buff lift's).</summary>
    public static bool IsLineForeignNanoSkill(int nanoId, IReadOnlyCollection<int> neededStats)
    {
        foreach (var s in neededStats)
        {
            if (SkillContributionOf(nanoId, s) > 0)
            {
                return false;
            }
        }

        return NanoSkillStats.Any(ns => SkillContributionOf(nanoId, ns) > 0);
    }

    /// <summary>The NCU footprint of a running nano, priced from the catalog (0 when unknown).</summary>
    public int NcuOfNano(int nanoId)
    {
        var e = _buffs.FirstOrDefault(b => b.NanoId == nanoId || b.RepNanoId == nanoId || b.LandIds.Contains(nanoId));
        return e?.Ncu ?? 0;
    }

    // The RECEIVER's own gate on a nano - the Level we must meet and the Shadowlands flag. A buff cast on
    // others carries BOTH gates on the landed nano's criteria: the CASTER's level (the higher one - the bot
    // must be that high to cast it) and the RECEIVER's "to use/affect" level (the lower one - what WE must
    // be). The pack puts both on the Self slot with identical child operators, so they cannot be told apart
    // structurally; the receiver requirement is the LOWEST Level> gate (owner, 2026-10-05: Composite Mochams
    // 8h reads caster 219 / receiver 209 this way). A single-gate nano (every NCU tier, most buffs) is
    // unchanged. SL is read from any Expansion BitAnd-2 gate (caster or target).
    private static (int Level, bool NeedsSl) ReceiverGate(NanoProfile nano)
    {
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
                if (req.Stat == StatLevel && req.Operator == OpGreaterThan)
                {
                    var lv = req.Value + 1; // GreaterThan N is inclusive of N+1
                    level = level == 0 ? lv : Math.Min(level, lv); // the receiver gate is the LOWER of caster/receiver
                }
                else if (req.Stat == StatExpansion && req.Operator == OpBitAnd && (req.Value & 2) != 0)
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

            // Relevant to a pet control plan: it raises a gating nano skill (MatMet/BioMet/MC/TS) or an
            // ability that trickles into one.
            var relevant = TrickleFactors.Keys.Any(s => np.Modify(s) > 0)
                           || AbilityStats.Any(a => np.Modify(a) > 0);
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

    // VERIFIED ability-trickle factors into the pet-gating nano skills (GameData/SkillTrickle.json,
    // ability order [Str,Agi,Sta,Int,Sen,Psy]). A skill's trickle = floor( Sum(ability*factor) / 4 ),
    // so +12 to all abilities = floor(12/4) = +3 for each of these (all Int-dominated). Not MC/TS-only:
    // this is what lets the SAME control plan gate an MP mezz pet on MatMet or a heal pet on BioMet.
    private static readonly int[] AbilityStats = { 16, 17, 18, 19, 20, 21 }; // Str,Agi,Sta,Int,Sen,Psy

    // Verified from OmniCell/CellAO SkillTrickleTable (columns: Str, Agi, Sta, Int, Sen, Psy). All pet-gating
    // nano skills so the SAME control plan gates any pet class: MP mezz (MatMet 127), heal (BioMet 128),
    // Engineer/MP-attack (MC 130 / TS 131), and Crat/others on PsyMod 129 or SI 122.
    private static readonly Dictionary<int, double[]> TrickleFactors = new()
    {
        [122] = new[] { 0.2, 0, 0, 0.8, 0, 0.0 }, // Sensory Improvement
        [127] = new[] { 0, 0.8, 0, 0, 0, 0.2 }, // Material Metamorphosis
        [128] = new[] { 0, 0, 0, 0.8, 0, 0.2 }, // Biological Metamorphosis
        [129] = new[] { 0, 0.8, 0, 0, 0.2, 0.0 }, // Psychological Modification
        [130] = new[] { 0, 0.8, 0.2, 0, 0, 0.0 }, // Matter Creation
        [131] = new[] { 0, 0.2, 0, 0.8, 0, 0.0 }, // Time & Space
    };

    /// <summary>How much an ability buff trickles into a nano skill, from the verified factors (0 if the
    /// skill has no trickle table entry - only the pet-gating nano skills are tracked).</summary>
    private static int AttrTrickle(BuffEntry b, int statId)
    {
        if (!TrickleFactors.TryGetValue(statId, out var f))
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < AbilityStats.Length; i++)
        {
            sum += b.Adds(AbilityStats[i]) * f[i];
        }

        return (int)Math.Floor(sum / 4.0);
    }

    /// <summary>
    ///     How much a RUNNING nano (by id) contributes to a nano skill right now - its flat modifier plus
    ///     any ability trickle - read from the pack. Lets a brain back out the UNBUFFED base (current
    ///     skill minus the running buffs) so the control plan starts from solid ground, for any skill.
    /// </summary>
    public static int SkillContributionOf(int nanoId, int statId)
    {
        var np = NanoLibrary.Find(nanoId);
        if (np == null)
        {
            return 0;
        }

        var trickle = 0;
        if (TrickleFactors.TryGetValue(statId, out var f))
        {
            double sum = 0;
            for (var i = 0; i < AbilityStats.Length; i++)
            {
                sum += np.Modify(AbilityStats[i]) * f[i];
            }

            trickle = (int)Math.Floor(sum / 4.0);
        }

        return np.Modify(statId) + trickle;
    }

    /// <summary>The NanoStrain (stat 75) of a nano by id, 0 if unknown - for the running-buff skip set.</summary>
    public static int StrainOf(int nanoId) => NanoLibrary.Find(nanoId)?.Stat(75) ?? 0;

    /// <summary>
    ///     The durable (no-wrangle) buff plan to CONTROL a pet, over AN ARBITRARY SET of required skills -
    ///     MC+TS for an Engineer robot or MP attack pet, MatMet+TS for an MP mezz pet, BioMet+TS for a heal
    ///     pet. Per skill it holds Durable (projected value) and Floor (0.80*req); CanControl is true only
    ///     when every required skill clears its floor.
    /// </summary>
    public sealed class ControlPlan
    {
        public bool CanControl; // durable (no-wrangle) clears the 80% floor on every required skill -> holdable
        public bool CanSummon;  // peak (durable + wrangle) clears the full requirement -> castable at the summon moment
        public readonly Dictionary<int, int> Durable = new(); // stat -> projected durable value (NO wrangle: what we HOLD at)
        public readonly Dictionary<int, int> Peak = new();    // stat -> durable + the short wrangle (the summon-moment value)
        public readonly Dictionary<int, int> Floor = new(); // stat -> ceil(controlFloor * req)
        public double MarginPct; // min over required skills of durable/req * 100 (the HOLD margin, wrangle dropped)
        public NcuPick? Ncu; // the NCU buff to request first
        public bool NcuAlreadyUp; // the chosen NCU tier is ALREADY running (login with the 4h buff) - its +Max NCU is in the live base, not added again
        public double NcuRemainingSec; // when NcuAlreadyUp: seconds left on it (for the "refresh at T-15m" narration)
        public readonly List<BuffEntry> Buffs = new(); // every buff the plan requests (incl. the wrangle, for the summon peak)
        public readonly List<PlanStep> Steps = new(); // ordered, for a step-by-step "base -> +buff -> running total" trace
        public int MaxNcu, NcuUsed, NcuFree; // NCU budget once the durable set is up

        /// <summary>One buff in the plan, in apply order, with the +skill it contributes - for the dry-run trace.</summary>
        public sealed class PlanStep
        {
            public string Name = "";
            public readonly Dictionary<int, int> Add = new(); // required-stat -> +skill this buff gives (trickle included)
            public bool Wrangle; // the short summon-moment peak - not part of the durable hold
            public int Ncu;      // the buff's NCU footprint
            public int MaxNcuAdded; // for the NCU buff itself: +Max NCU (it adds no skill)
            public int AddFor(int stat) => Add.TryGetValue(stat, out var v) ? v : 0;
        }

        // Convenience for the common MC/TS pet (keeps callers/logs simple).
        public int DurableMc => Durable.TryGetValue(130, out var v) ? v : 0;
        public int DurableTs => Durable.TryGetValue(131, out var v) ? v : 0;
        public int PeakMc => Peak.TryGetValue(130, out var v) ? v : 0;
        public int PeakTs => Peak.TryGetValue(131, out var v) ? v : 0;
        public int FloorMc => Floor.TryGetValue(130, out var v) ? v : 0;
        public int FloorTs => Floor.TryGetValue(131, out var v) ? v : 0;
    }

    /// <summary>
    ///     Control-first, profession-agnostic: can we DURABLY hold a pet whose summon gates on
    ///     <paramref name="reqByStat" /> (stat -> required skill) at the 80% floor WITHOUT the short wrangle,
    ///     and what is the durable buff set? <paramref name="baseByStat" /> is the UNBUFFED base per required
    ///     skill (the caller backs running buffs out). Pool = bot catalog + our learned self-buffs. Phase 1
    ///     takes the best per strain of the buffs that raise MORE THAN ONE required skill or trickle from
    ///     abilities (composites, Nano Expertise, Attribute Boost - they stack across strains); phase 2 adds,
    ///     per still-short skill, the LOWEST single-skill rung closing its gap (banking NCU). CanControl is
    ///     false when a skill cannot reach its floor - the caller then steps down the pet (the sustain-gate).
    /// </summary>
    public ControlPlan BuildControlPlan(LocalPlayer me, IReadOnlyDictionary<int, int> baseByStat,
        IReadOnlyDictionary<int, int> reqByStat, bool paid, string myProfession = "", double controlFloor = 0.80)
    {
        var plan = new ControlPlan();
        foreach (var kv in reqByStat)
        {
            plan.Floor[kv.Key] = (int)Math.Ceiling(controlFloor * kv.Value);
        }

        if (!Loaded || me == null || reqByStat.Count == 0)
        {
            return plan;
        }

        var myLevel = me.TryGetStat(Stat.Level, out var lvl) ? lvl : 0;
        var reqStats = reqByStat.Keys.ToList();
        // Dedupe by the landed nano: a buff we LEARNED (SelfBuffCandidates) is also in the catalog, and the
        // two entries carry different professions - counted twice they would double the skill and stack a
        // buff against itself. Keep one per nano (the catalog entry wins, _buffs first; a Generic catalog
        // buff still self-casts). Only THEN the skill-buff / castable filters.
        int NanoKey(BuffEntry b) => b.RepNanoId > 0 ? b.RepNanoId : b.NanoId ?? (b.LandIds.Count > 0 ? b.LandIds[0] : 0);
        var pool = _buffs.Concat(SelfBuffCandidates(me, myProfession))
            .GroupBy(NanoKey)
            .Select(g => g.First())
            .Where(b => Castable(b, myLevel, paid) && (b.CanCastOnOthers || b.SelfCastableBy(myProfession))
                        && SurvivalRank(b).Tier < 0) // CONTROL = skill buffs only; HP/HoT/AC/shield/evade are
            .ToList();                                // post-summon survival (owner: pet first, survival after)

        plan.Ncu = BestNcuBuff(me, paid);
        if (plan.Ncu != null && me.Buffs != null)
        {
            var upNcu = me.Buffs.FirstOrDefault(b => b.Id == plan.Ncu.LandId);
            if (upNcu != null)
            {
                plan.NcuAlreadyUp = true;
                plan.NcuRemainingSec = upNcu.Cooldown?.RemainingTime ?? 0;
            }
        }

        int Help(BuffEntry e, int stat) => e.Adds(stat) + AttrTrickle(e, stat);

        // Record a plan buff as an ordered step (for the dry-run trace). Wrangle steps are summon-moment
        // peak, not durable - tracked but not folded into the durable total.
        void AddStep(BuffEntry e, bool wrangle)
        {
            var step = new ControlPlan.PlanStep { Name = e.Name, Wrangle = wrangle, Ncu = e.Ncu };
            foreach (var s in reqStats)
            {
                step.Add[s] = Help(e, s);
            }

            plan.Steps.Add(step);
        }

        // The NCU buff is requested first (it expands Max NCU so the rest fit); it adds no skill. Show it.
        // Already running (login with the 4h buff) -> show it as held with its remaining time, not re-added.
        if (plan.Ncu != null)
        {
            var how = plan.Ncu.NeedsTeam ? "team-cast, needs team" : "single-target";
            var name = plan.NcuAlreadyUp
                ? $"+{plan.Ncu.MaxNcuAdded} Max NCU '{plan.Ncu.Name}' [UP, {plan.NcuRemainingSec / 60:F0}m left - refresh at T-15m]"
                : $"+{plan.Ncu.MaxNcuAdded} Max NCU '{plan.Ncu.Name}' ({how})";
            plan.Steps.Add(new ControlPlan.PlanStep { Name = name, MaxNcuAdded = plan.NcuAlreadyUp ? 0 : plan.Ncu.MaxNcuAdded });
        }

        var cur = new Dictionary<int, int>();      // durable running total (NO wrangle)
        var wrangleAdd = new Dictionary<int, int>(); // the wrangle's contribution, held separately for the peak
        foreach (var s in reqStats)
        {
            cur[s] = baseByStat.TryGetValue(s, out var b) ? b : 0;
            wrangleAdd[s] = 0;
        }

        // SKILL SELECTION (owner's stacking rule, 2026-10-05): a profession's NANO-SKILL buffs do NOT stack
        // with each other - per skill only the HIGHEST counts, so a single +140 Mocham's Gift overwrites that
        // profession's +50 Composite (and a composite +140 would overwrite the single). DIFFERENT professions
        // DO stack, and ATTRIBUTE buffs (a different line, trickling to skill) stack on top. So: pick the best
        // nano-skill buff per (profession, skill), then the best-value attribute buffs, then the wrangle - all
        // NCU-bounded to the SUMMON budget (pet first; survival spends the leftover after the wrangle drops).
        var ncuBudget = (me.TryGetStat(Stat.MaxNCU, out var budgetMax) ? budgetMax : 0)
                        + (plan.NcuAlreadyUp ? 0 : plan.Ncu?.MaxNcuAdded ?? 0);
        var usedNcu = 0;

        // 1) Nano-skill buffs, grouped by profession (same profession = one line, no stacking). For each
        // profession take the biggest buff per still-uncovered required skill that fits NCU; a multi-skill
        // buff covers several at once. This is what drops MP's Composite Mastery in favour of Mocham's Gift.
        foreach (var profGroup in pool.Where(b => IsNanoSkillBuff(b) && !IsWrangle(b)).GroupBy(b => b.Profession))
        {
            var covered = new HashSet<int>();
            foreach (var s in reqStats.OrderByDescending(s => profGroup.Max(b => b.Adds(s))))
            {
                if (covered.Contains(s))
                {
                    continue;
                }

                var pick = profGroup
                    .Where(b => b.Adds(s) > 0 && !plan.Buffs.Contains(b) && b.Ncu <= ncuBudget - usedNcu)
                    .OrderByDescending(b => b.Adds(s)).FirstOrDefault();
                if (pick == null)
                {
                    continue;
                }

                plan.Buffs.Add(pick);
                AddStep(pick, false);
                usedNcu += pick.Ncu;
                foreach (var s2 in reqStats)
                {
                    if (pick.Adds(s2) > 0)
                    {
                        covered.Add(s2);
                    }
                }
            }
        }

        // 2) The wrangle FIRST (before the small attribute buffs) - the ~1-minute SUMMON peak (Trader line,
        // stacks), biggest that fits. It is worth far more (+131) than any trickle buff, so it is reserved
        // before leftover NCU goes to attributes. Held aside from the durable total: dropped right after the
        // pet lands (that lapse IS the downshift).
        // The wrangle: the TEAM wrangle (its landed nano reads +132) is locked to us (owner), so we take the
        // one that does NOT need a team - the single-target Skill Wrangler at its real +131 - biggest that
        // fits NCU. NeedsTeam buffs are excluded here; a non-team wrangle is cast straight on us.
        var wrangle = pool.Where(b => IsWrangle(b) && !b.NeedsTeam && b.Ncu <= ncuBudget - usedNcu)
            .OrderByDescending(b => reqStats.Sum(s => Help(b, s))).FirstOrDefault();
        if (wrangle != null)
        {
            plan.Buffs.Add(wrangle);
            AddStep(wrangle, true);
            usedNcu += wrangle.Ncu;
        }

        // 3) The BROAD attribute composite (raises >=3 abilities, so it trickles across the nano skills) -
        // e.g. Composite Attribute Boost. NARROW single/dual-attribute buffs (Feline Grace +Agi, Iron Circle
        // +Str/Stam, Neuronal +Int/Psy) are NOT pet-control buffs - they are survival/utility and belong to
        // the post-summon survival pass (owner: pet first, survival after). A different line, so it stacks.
        foreach (var b in pool
                     .Where(b => !IsNanoSkillBuff(b) && !IsWrangle(b) && AbilityStats.Count(a => b.Adds(a) > 0) >= 3
                                 && reqStats.Any(s => Help(b, s) > 0))
                     .OrderByDescending(b => (double)reqStats.Sum(s => Help(b, s)) / Math.Max(1, b.Ncu)))
        {
            if (plan.Buffs.Contains(b) || b.Ncu > ncuBudget - usedNcu)
            {
                continue;
            }

            plan.Buffs.Add(b);
            AddStep(b, false);
            usedNcu += b.Ncu;
        }

        // Durable totals under the stacking rule: nano-skill buffs contribute their profession's MAX per skill
        // (no same-profession stacking); attribute buffs add their trickle (they stack); the wrangle is the
        // peak only (wrangleAdd), never durable.
        foreach (var s in reqStats)
        {
            var v = cur[s]; // the unbuffed base
            foreach (var pg in plan.Buffs.Where(b => IsNanoSkillBuff(b) && !IsWrangle(b)).GroupBy(b => b.Profession))
            {
                v += pg.Max(b => b.Adds(s));
            }

            foreach (var b in plan.Buffs.Where(b => !IsNanoSkillBuff(b) && !IsWrangle(b)))
            {
                v += Help(b, s);
            }

            cur[s] = v;
            wrangleAdd[s] = wrangle != null ? Help(wrangle, s) : 0;
        }

        plan.CanControl = true;
        plan.CanSummon = true;
        var margin = 1.0;
        foreach (var s in reqStats)
        {
            plan.Durable[s] = cur[s];
            plan.Peak[s] = cur[s] + wrangleAdd[s];
            if (cur[s] < plan.Floor[s])
            {
                plan.CanControl = false; // can't HOLD it durably at the 80% floor
            }

            if (plan.Peak[s] < reqByStat[s])
            {
                plan.CanSummon = false; // can't reach the full req even WITH the wrangle
            }

            if (reqByStat[s] > 0)
            {
                margin = Math.Min(margin, (double)cur[s] / reqByStat[s]); // the HOLD margin (durable, wrangle dropped)
            }
        }

        plan.MarginPct = margin * 100.0;

        var baseMaxNcu = me.TryGetStat(Stat.MaxNCU, out var mncu) ? mncu : 0;
        // When the NCU buff is already running, live Max NCU ALREADY includes its +Max NCU - adding it again
        // would inflate the budget (owner logs in with the 4h buff up). Only project it when it is NOT up yet.
        plan.MaxNcu = baseMaxNcu + (plan.NcuAlreadyUp ? 0 : plan.Ncu?.MaxNcuAdded ?? 0);
        plan.NcuUsed = plan.Buffs.Sum(b => b.Ncu);
        plan.NcuFree = plan.MaxNcu - plan.NcuUsed;
        return plan;
    }

    /// <summary>MC/TS convenience overload (Engineer robot / MP attack pet).</summary>
    public ControlPlan BuildControlPlan(LocalPlayer me, int baseMc, int baseTs, int reqMc, int reqTs, bool paid,
        string myProfession = "", double controlFloor = 0.80) =>
        BuildControlPlan(me, new Dictionary<int, int> { [130] = baseMc, [131] = baseTs },
            new Dictionary<int, int> { [130] = reqMc, [131] = reqTs }, paid, myProfession, controlFloor);

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
        public bool NeedsTeam; // when Source == BuffBot: team-cast (accept the invite, they auto-disband) vs single-target (direct cast, no team)
        public int[] LandIds = Array.Empty<int>(); // the landed nano id(s): "we have it" = any of these is running on us
        public bool RequireAll; // false (default): ANY LandId up = landed. true: EVERY LandId must be up -
                                // a multi-code tell ("cast tsmo mcmo 131 c2") waits for the whole stack.
        public bool IsWrangleStep; // the short summon-moment wrangle - excluded from a durable acquisition run

        public string Describe => Source switch
        {
            BuffSource.SelfCast => $"self-cast {Name} (nano {SelfCastNanoId})",
            BuffSource.BuffBot => $"tell {BotName} \"{Tell}\" for {Name}" + (NeedsTeam ? " [team]" : " [self]"),
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

        action.LandIds = b.LandIds.Count > 0 ? b.LandIds.ToArray() : (nanoId != 0 ? new[] { nanoId } : Array.Empty<int>());
        action.IsWrangleStep = IsWrangle(b);

        if (b.SelfCastableBy(myProfession) && nanoId != 0 && isLearned(nanoId))
        {
            action.Source = BuffSource.SelfCast;
            action.SelfCastNanoId = nanoId;
            return action;
        }

        // A buff bot can only put a buff on us if it CASTS ON OTHERS (team or single-target). A self-only
        // buff (CastNano) we cannot self-cast is out of reach - never ask a bot for it (the +500 NCU trap).
        if (!string.IsNullOrWhiteSpace(botName) && !string.IsNullOrWhiteSpace(b.Tell) && b.CanCastOnOthers)
        {
            action.Source = BuffSource.BuffBot;
            action.BotName = botName;
            action.Tell = b.Tell;
            action.NeedsTeam = b.NeedsTeam;
            return action;
        }

        action.Source = BuffSource.Unavailable;
        return action;
    }

    /// <summary>
    ///     The ordered actions to put a <see cref="ControlPlan" /> up: NCU buff FIRST (it expands Max NCU
    ///     so the rest fit), then the both-skill + attribute stackers, then the single-skill rungs, and the
    ///     short wrangle (it is part of the buff set so the stack PEAKS over the pet's full cast requirement
    ///     at the summon moment). The wrangle is simply never refreshed afterward - letting it lapse is the
    ///     downshift to the durable hold. Each buff is routed to self-cast or a bot tell.
    /// </summary>
    private const int NcuStrain = 257; // the Fixer Max-NCU line's NanoStrain
    private const int WrangleStrain = 220; // the Skill Wrangler line's NanoStrain (verified, skill-buffs-mc-ts.md)

    // The (team) Skill Wrangler is a ~1-minute summon-moment peak (+131 max for the team version), NOT a
    // durable buff - it is dropped right after the pet lands. Identified by its strain, with a name
    // fallback in case a dimension's catalog carries it on strain 0.
    private static bool IsWrangle(BuffEntry e) =>
        e.Strain == WrangleStrain
        || (e.Name?.IndexOf("Wrangl", StringComparison.OrdinalIgnoreCase) >= 0);

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
                BotName = botName, Tell = plan.Ncu.Tell, NeedsTeam = plan.Ncu.NeedsTeam,
                LandIds = plan.Ncu.LandId != 0 ? new[] { plan.Ncu.LandId } : Array.Empty<int>(),
            });
        }

        foreach (var b in plan.Buffs)
        {
            if (!skip.Contains(b.Strain))
            {
                actions.Add(RouteFor(b, myProfession, botName, isLearned));
            }
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
        // Max-NCU buffs are the pet's NCU pillar, never survival.
        if (b.Adds(181) > 0)
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
            .Where(x => x.Rank.Tier >= 0 && x.Buff.Ncu > 0 && Castable(x.Buff, myLevel, paid)
                        && (x.Buff.CanCastOnOthers || x.Buff.SelfCastableBy(myProfession))) // never a self-only buff a bot can't put on us
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

    /// <summary>
    ///     The caster-side SKILL shortfall of one nano (the pet-buff step's lift calculation,
    ///     owner 2026-10-07): from the pack's cast criteria, every GreaterThan gate on one of the
    ///     six nano skills that the CASTER must meet (receiver-tagged leaves are the target's
    ///     business - the family gates - and are not stat lifts), as stat -> how far we fall
    ///     short. AO's comparisons are inclusive (ReqChecker), so the requirement is the gate
    ///     value itself. Empty = castable as-is.
    /// </summary>
    public static Dictionary<int, int> CasterSkillGaps(int nanoId, LocalPlayer me)
    {
        var gaps = new Dictionary<int, int>();
        var nano = NanoLibrary.Find(nanoId);
        if (nano == null || me == null)
        {
            return gaps;
        }

        foreach (var action in nano.Actions ?? Array.Empty<NanoAction>())
        {
            if (action.ActionType != 3)
            {
                continue; // the use/cast action (ActionToUse)
            }

            foreach (var req in action.Requirements ?? Array.Empty<NanoRequirement>())
            {
                if (req.Operator != OpGreaterThan || req.Target == TargetReceiver
                    || !NanoSkillStats.Contains(req.Stat))
                {
                    continue;
                }

                me.TryGetStat((Stat)req.Stat, out var cur);
                var need = req.Value - cur;
                if (need > 0)
                {
                    gaps[req.Stat] = Math.Max(gaps.TryGetValue(req.Stat, out var had) ? had : 0, need);
                }
            }
        }

        return gaps;
    }

    /// <summary>
    ///     The LIFT plan for caster-side shortfalls (the pet-buff step): per NanoStrain the
    ///     strongest candidate raising any NEEDED stat, greedily taken by still-needed
    ///     coverage per NCU until every shortfall is covered or nothing affordable remains.
    ///     Receiver-gated (level + expansion) like every plan; the wrangle goes last.
    /// </summary>
    public List<BuffEntry> PlanCasterLift(LocalPlayer me, IReadOnlyDictionary<int, int> needByStat,
        int ncuBudget, bool paid)
    {
        var plan = new List<BuffEntry>();
        if (!Loaded || me == null || needByStat.Count == 0 || ncuBudget <= 0)
        {
            return plan;
        }

        var myLevel = me.TryGetStat(Stat.Level, out var lvl) ? lvl : 0;
        var needStats = needByStat.Keys.ToList();

        var pool = _buffs
            .Where(b => b.CanCastOnOthers && !string.IsNullOrWhiteSpace(b.Tell) && Castable(b, myLevel, paid))
            .Where(b => needStats.Any(s => GainFor(b, s) > 0))
            .GroupBy(b => b.Strain)
            .Select(g => g.OrderByDescending(b => needStats.Sum(s => GainFor(b, s))).ThenBy(b => b.Ncu).First())
            .ToList();

        var remaining = new Dictionary<int, int>(needByStat);
        var budget = ncuBudget;

        while (true)
        {
            BuffEntry? best = null;
            var bestScore = 0.0;
            foreach (var c in pool)
            {
                if (plan.Contains(c) || c.Ncu > budget)
                {
                    continue;
                }

                var covers = needStats.Sum(s => Math.Min(GainFor(c, s), Math.Max(0, remaining.GetValueOrDefault(s))));
                if (covers <= 0)
                {
                    continue; // nothing this candidate still covers
                }

                var perNcu = covers / Math.Max(1, c.Ncu);
                if (best == null || perNcu > bestScore)
                {
                    best = c;
                    bestScore = perNcu;
                }
            }

            if (best == null)
            {
                break;
            }

            plan.Add(best);
            foreach (var s in needStats)
            {
                remaining[s] = Math.Max(0, remaining.GetValueOrDefault(s) - GainFor(best, s));
            }

            budget -= best.Ncu;
        }

        // Tell order: the durable stack first, the short wrangle last (freshest at the casts).
        plan.Sort((x, y) => (IsWrangle(x) ? 1 : 0).CompareTo(IsWrangle(y) ? 1 : 0));
        return plan;
    }

    /// <summary>The chosen Max-NCU buff for a receiver: the tell to send, and how much Max NCU it adds.</summary>
    public sealed class NcuPick
    {
        public string Tell = "";
        public int MaxNcuAdded; // +Max NCU the selected tier grants (e.g. +60 at L50)
        public int LandId; // the landed nano actually running on the receiver
        public int ReceiverLevel; // the level that tier needs
        public bool NeedsTeam; // the tier is team-cast - we must be teamed with the caster to receive it
        public string Name = ""; // the tier's catalog name (for the dry-run trace)
    }

    private BuffEntry? FindNcuEntry() =>
        _buffs.FirstOrDefault(b => b.Tell.Equals("ncu", StringComparison.OrdinalIgnoreCase)
                                   || b.Effect.Contains("NCU", StringComparison.OrdinalIgnoreCase));

    private const int StatMaxNcu = 181;

    /// <summary>
    ///     The biggest Max-NCU buff a bot can put ON US that our level allows. Scans EVERY NCU tier in the
    ///     catalog (Codedoc lists each tier as its own entry, Chewy as one laddered entry - either way every
    ///     +Max NCU buff is considered, not just the first), keeps only tiers that CAST ON OTHERS (a bot
    ///     cannot give us a self-only buff - this is what kept the +500 Firewalled Sync Compressor, a Fixer
    ///     SELF buff, out), whose receiver level/SL gate we meet, and picks the largest +Max NCU. Requested
    ///     FIRST (it expands Max NCU so the rest of the stack fits). Null when none qualifies.
    /// </summary>
    public NcuPick? BestNcuBuff(LocalPlayer me, bool paid)
    {
        if (!Loaded || me == null)
        {
            return null;
        }

        var myLevel = me.TryGetStat(Stat.Level, out var lvl) ? lvl : 0;

        NcuPick? best = null;
        foreach (var b in _buffs)
        {
            var added = b.Adds(StatMaxNcu);
            if (added <= 0 || !b.CanCastOnOthers || b.ReceiverLevel > myLevel || (b.NeedsSl && !paid))
            {
                continue; // not an NCU buff / self-only / above our level / SL we don't own
            }

            if (best == null || added > best.MaxNcuAdded)
            {
                best = new NcuPick
                {
                    Tell = b.Tell, MaxNcuAdded = added, LandId = b.RepNanoId,
                    ReceiverLevel = b.ReceiverLevel, NeedsTeam = b.NeedsTeam, Name = b.Name,
                };
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

    /// <summary>
    ///     THE PET-LINE PLAN (owner, 2026-10-07): the buff combination that lifts a pet line's skill
    ///     pair (attack MatCrea+TS, heal BioMet+TS, mezz MatMeta+TS) the most OVERALL, kept BALANCED -
    ///     a pet needs BOTH skills at its requirement, so stacking one side high while the other lags
    ///     buys nothing ("not helpful to have 1000 TS and only 300 MC"). Candidates are the wrangle,
    ///     the mocham/infuse/mastery/teaching families and the composites - per NanoStrain only the
    ///     strongest variant is considered (same-strain buffs overwrite, never stack), everything is
    ///     receiver-gated (level + expansion via <see cref="Castable" />) and NCU-budgeted.
    ///     Selection is a greedy over value-per-NCU where the skill currently BEHIND scores double:
    ///     that maximizes the pair's sum AND rebalances every round. The caller sends the returned
    ///     entries' codes in ONE tell ("cast tsmo mcmo 131 c2").
    /// </summary>
    public List<BuffEntry> PlanPetLine(LocalPlayer me, int statA, int statB, int ncuBudget, bool paid)
    {
        var plan = new List<BuffEntry>();
        if (!Loaded || me == null || ncuBudget <= 0)
        {
            return plan;
        }

        var myLevel = me.TryGetStat(Stat.Level, out var lvl) ? lvl : 0;

        // Candidates: anything raising either skill, deliverable by a bot (tell + casts on others),
        // castable by us as the receiver - collapsed to the strongest variant per strain.
        var pool = _buffs
            .Where(b => b.CanCastOnOthers && !string.IsNullOrWhiteSpace(b.Tell) && Castable(b, myLevel, paid))
            .Where(b => GainFor(b, statA) > 0 || GainFor(b, statB) > 0)
            .GroupBy(b => b.Strain)
            .Select(g => g.OrderByDescending(b => GainFor(b, statA) + GainFor(b, statB)).ThenBy(b => b.Ncu).First())
            .ToList();

        var projA = me.TryGetStat((Stat)statA, out var a) ? a : 0;
        var projB = me.TryGetStat((Stat)statB, out var v) ? v : 0;
        var budget = ncuBudget;

        while (true)
        {
            BuffEntry? best = null;
            var bestScore = 0.0;
            foreach (var c in pool)
            {
                if (plan.Contains(c) || c.Ncu > budget)
                {
                    continue;
                }

                var gA = GainFor(c, statA);
                var gB = GainFor(c, statB);
                var score = projA <= projB ? gA * 2.0 + gB : gB * 2.0 + gA;
                var perNcu = score / Math.Max(1, c.Ncu);
                if (best == null || perNcu > bestScore)
                {
                    best = c;
                    bestScore = perNcu;
                }
            }

            if (best == null)
            {
                break; // nothing affordable left - the stack is what the NCU budget allows
            }

            plan.Add(best);
            projA += GainFor(best, statA);
            projB += GainFor(best, statB);
            budget -= best.Ncu;
        }

        // Tell order: the durable stack first, the short wrangle LAST - it must be freshest at the
        // summon moment (it lapses right after the pet lands and is never refreshed).
        plan.Sort((x, y) => (IsWrangle(x) ? 1 : 0).CompareTo(IsWrangle(y) ? 1 : 0));
        return plan;
    }
}
