// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: PerkBonuses.cs
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using System.IO;
using AOBuddy20.Configuration;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Controlling;

/// <summary>
///     PERMANENT STAT BONUSES - the perk/research layer folded into every stat read (ported from
///     AOBuddy10 PerkBonuses). A character's final stats include permanent bonuses the clientless bot
///     cannot read as one number off the wire: trained PERKS grant stacking stat bonuses per level.
///     The login FullCharacter carries the trained perk STEP ids (LocalPlayer.Perks[].SkillId);
///     GameData/perks.sql + Perks.xml map ids -> lines -> bonuses. Auto-detected from the wire at login
///     (a perk-id signature check makes that once-per-change, not per frame), so it works for ANY
///     character with no per-toon config (rules.md). The merged map is pushed onto LocalPlayer's
///     permanent-bonus layer (<see cref="SimpleChar.SetPermanentBonuses" />), which the SDK folds into
///     GetStat - so MC/TS/NCU and every perk-boosted stat read HONEST final values (this is what made
///     an Engineer's raw MC/TS read 297 instead of 326: the perk layer was never applied).
///     SCOPE: perks.sql holds the 74 STANDARD (Shadowlands) perk lines. Alien (AI) and LE-research
///     perks are a DIFFERENT data source and are not applied here yet (see perk-map.md).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class PerkBonuses
{
    private readonly string _baseDir;
    private readonly ILogger<PerkBonuses> _logger;
    private readonly PerkData _perkData = new();

    private Dictionary<Stat, int> _permanentBonuses = new();
    private LocalPlayer? _bonusTarget;
    private string _diag = "perk bonuses not initialised";
    private string? _lastPerkSig;

    public PerkBonuses(ILogger<PerkBonuses> logger)
    {
        _logger = logger;
        _baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Init();
    }

    public string Diagnostic => _diag;

    private void Init()
    {
        try
        {
            var sqlPath = Path.Combine(_baseDir, "GameData", "perks.sql");
            var xmlPath = Path.Combine(_baseDir, "GameData", "Perks.xml");
            var sqlOk = _perkData.Load(sqlPath);
            var xmlOk = _perkData.LoadPerkXml(xmlPath);

            if (!sqlOk)
            {
                _diag = $"perks.sql not loaded ({sqlPath}) - perk bonuses OFF";
                _logger.LogWarning($"PERKS: {_diag}");
                return;
            }

            _diag = $"perks.sql {_perkData.PerkCount} perks, Perks.xml {(xmlOk ? _perkData.PerkXmlCount + " ids" : "FAIL - auto-detect OFF")}. " +
                    "Auto-detecting trained perks from the wire at login.";
            _logger.LogInformation($"PERKS: {_diag}");
        }
        catch (Exception ex)
        {
            _diag = "perk init failed: " + ex.Message;
            _logger.LogError(ex, "PERKS: init failed");
        }
    }

    /// <summary>Per frame: re-detect the trained perk lines when the wire id-signature changes, then keep
    /// the bonus layer applied to the current LocalPlayer instance (the SDK re-creates it on full updates).</summary>
    public void Tick(LocalPlayer me)
    {
        if (me == null)
        {
            return;
        }

        RefreshFromWire(me);
        Apply(me);
    }

    private void RefreshFromWire(LocalPlayer me)
    {
        if (!_perkData.PerkXmlLoaded)
        {
            return;
        }

        var perks = me.Perks;
        if (perks == null || perks.Length == 0)
        {
            return;
        }

        var ids = perks.Select(p => p.SkillId).Where(id => id != 0).OrderBy(id => id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var sig = string.Join(",", ids);
        if (sig == _lastPerkSig)
        {
            return;
        }

        _lastPerkSig = sig;

        var lines = _perkData.DetectPerkLines(ids, out var unresolved);
        var bonuses = _perkData.ComputeBonuses(lines, out var unknown, out var unmapped, out var applied);
        _permanentBonuses = bonuses;
        _bonusTarget = null; // force re-apply

        var sum = bonuses.Count == 0
            ? "(none)"
            : string.Join(", ", bonuses.OrderBy(b => b.Key.ToString()).Select(b => $"{b.Key}+{b.Value}"));
        _diag = $"Perks [wire {ids.Count} ids, {unresolved.Count} unresolved (alien/LE/research)]: " +
                $"[{(applied.Count > 0 ? string.Join(", ", applied) : "none")}]. Bonuses: {sum}." +
                (unknown.Count > 0 ? $" UNKNOWN: [{string.Join(", ", unknown)}]." : "") +
                (unmapped.Count > 0 ? $" UNMAPPED skills: [{string.Join(", ", unmapped)}]." : "");
        _logger.LogInformation($"PERKS: {_diag}");
    }

    private void Apply(LocalPlayer me)
    {
        if (ReferenceEquals(_bonusTarget, me))
        {
            return;
        }

        me.SetPermanentBonuses(new Dictionary<Stat, int>(_permanentBonuses));
        _bonusTarget = me;
        _logger.LogInformation($"PERKS: permanent-bonus layer applied ({_permanentBonuses.Count} stats).");
    }

    /// <summary>The 'perks' command: the diagnostic and the live perk-boosted MC/TS/NCU with the layer on.</summary>
    public void Report(Action<string> reply)
    {
        reply(_diag.Length > 440 ? _diag[..440] : _diag);
        var me = DynelManager.LocalPlayer;
        if (me == null)
        {
            return;
        }

        me.TryGetStat(Stat.MaterialCreation, out var mc);
        me.TryGetStat(Stat.SpaceTime, out var ts);
        me.TryGetStat(Stat.MaxNCU, out var ncu);
        var pMc = me.PermanentBonuses.TryGetValue(Stat.MaterialCreation, out var a) ? a : 0;
        var pTs = me.PermanentBonuses.TryGetValue(Stat.SpaceTime, out var b) ? b : 0;
        reply($"Now: MC={mc} (perk +{pMc}), TS={ts} (perk +{pTs}), MaxNCU={ncu}. Perm layer holds {me.PermanentBonuses.Count} stats.");
    }
}
