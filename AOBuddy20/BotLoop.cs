// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: BotLoop.cs
//
// Last modified: 2026-10-04
// Created:       2026-09-29 23:09
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Brains;
using AOBuddy20.Configuration;
using AOBuddy20.Controlling;
using AOBuddy20.Enums;
using AOBuddy20.Nav;
using AOSharp.Common.GameData;
using AOBuddy20.PacketConsumers;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20;

/// <summary>
///     The bot's decision loop (Readme points 1 and 4): ticks on the SDK's update thread - the same
///     thread that processes the packets, and the only one allowed to touch SDK state (review.md #9) -
///     while the movement cycle runs on its own thread in the MovementController. Attack/Use and the
///     task switch belong here; nothing here tells the body HOW to move, only WHERE
///     (MovementController.SetDesiredGoal).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class BotLoop
{
    private readonly AccountInfo _config;
    private readonly ControlArbiter _controlArbiter;
    private readonly BrainBank _brains;
    private readonly HealController _heal;
    private readonly ILogger<BotLoop> _logger;
    private readonly MissionController _missionController;
    private readonly Awareness _awareness;
    private readonly ResupplyController _resupply;
    private readonly SellController _sell;
    private readonly NavController _navMemory;
    private readonly BuffBotController _buffBot;
    private readonly PerkBonuses _perkBonuses;
    private bool _running;

    public BotLoop(ControlArbiter controlArbiter, MissionController missionController, ResupplyController resupply, SellController sell, HealController heal, BrainBank brains, NavController navMemory, Awareness awareness, BuffBotController buffBot, PerkBonuses perkBonuses, AccountInfo config, ILogger<BotLoop> logger)
    {
        _controlArbiter = controlArbiter;
        _missionController = missionController;
        _resupply = resupply;
        _sell = sell;
        _heal = heal;
        _brains = brains;
        _navMemory = navMemory;
        _perkBonuses = perkBonuses;
        _awareness = awareness;
        _buffBot = buffBot;
        _config = config;
        _logger = logger;
    }

    /// <summary>What the bot is working on. Nothing idles; movement keeps running either way.</summary>
    public Tasks CurrentTask { get; set; } = Tasks.Nothing;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        Client.OnUpdate += OnUpdate;
        _logger.LogInformation("Bot loop started.");
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        Client.OnUpdate -= OnUpdate;
        _logger.LogInformation("Bot loop stopped.");
    }

    // Client.Update invokes OnUpdate only while in play, ~64 times a second (UpdateLoop). The
    // UpdateLoop carries no exception guard: one throw in here would kill the SDK's update thread
    // and with it all packet processing, so the tick is guarded end to end.
    private void OnUpdate(object? sender, double deltaTime)
    {
        try
        {
            // NAV memory (AOBuddy10 NavController): record the owner's footsteps and catalogue the
            // playfield's objects and mob spawns, so ground already walked is never guessed again.
            // Zone transitions are not recorded yet - they need the POSITION-JUMP (teleport)
            // detection, which is not ported (realCrossing: false).
            var me = DynelManager.LocalPlayer;
            if (me != null)
            {
                _navMemory.SetPlayfield((int)Playfield.ModelId, Playfield.Name, null, realCrossing: false);
                var owner = DynelManager.Players.FirstOrDefault(pl =>
                    string.Equals(pl.Name, _config.Owner, StringComparison.OrdinalIgnoreCase));
                _navMemory.RecordOwner(owner?.Transform.Position ?? default,
                    owner != null); // not visible: close the run, a break never becomes a segment
                _navMemory.Tick(deltaTime);

                // AWARENESS: the monster picture (near / on bot / on pets) is sensory input - it
                // refreshes before anything decides on it.
                _awareness.Tick(me, deltaTime);

                // PERKS: fold the trained-perk permanent stat bonuses onto the player BEFORE anything
                // reads a stat, so MC/TS/NCU and every perk-boosted value are the honest final numbers
                // (without this the perk layer is empty and e.g. an Engineer's MC/TS read 297 not 326).
                _perkBonuses.Tick(me);

                // BRAINS: pick this character's combat/selfbuffing/externalbuffing brains once the
                // profession is on the wire. The DI container was built before login, so the bank
                // resolves them here, post-login, exactly once per process (review.md #12).
                _brains.EnsureSelected(me);

                // PET is an OVERLAY, not a chain step: it summons/maintains/commands pets but never
                // moves the body, and it MUST keep running during combat and missions (the robot
                // fights while the bot does other things). So it ticks unconditionally here, like
                // Awareness - never gated by the exclusive decision chain below. (ControlPriority.Pet
                // 650 is reserved for when a pet action needs the body; step 2 never takes it.)
                _brains.TickPet(me, deltaTime);

                // BUFF BOTS (4a.1): the public-buff-bot handshake, when a session is open. An overlay
                // like the pet tick - it only sends tells and accepts the bot's invite, moves nothing.
                _buffBot.Tick(me, deltaTime);

                // The decision chain, in descending ControlPriority: heal (800) - the stims and
                // rechargers go in before anything else looks at its state; combat (700) and
                // selfbuffing (600) - the brains, log-only until their families are implemented;
                // resupply (500); MISSION (400) - the blitz run, whose Tick answers false while it
                // waits on the sell step so the chain falls through; external buffing (300) - also
                // a brain; selling (200). A brain family that is unselected or disabled answers
                // false and the chain moves on. Combat is skipped while the mission run is inside a
                // building - blitz means fight nothing there (the heal keeps working).
                // RESUPPLY (AOBuddy10 ResupplyController): the decision tick runs here on the update
                // thread, the same one its packet handlers fire on. While a run is active it owns
                // the body through a MovementController goal at ControlPriority.Resupply and holds
                // the arbiter at that priority; idle, it only answers the owner's trade. SELLING
                // (SellController) runs the same way one priority down, and MISSION the same way
                // at its own.
                if (_heal.Tick(me, deltaTime))
                {
                    CurrentTask = Tasks.Heal;
                }
                else if (!_missionController.SuppressCombat && _brains.TickCombat(me, deltaTime))
                {
                    CurrentTask = Tasks.Combat;
                }
                else if (_brains.TickSelfbuff(me, deltaTime))
                {
                    CurrentTask = Tasks.Selfbuff;
                }
                else if (_resupply.Tick(me, deltaTime))
                {
                    CurrentTask = Tasks.Resupply;
                }
                else if (_missionController.Tick(me, deltaTime))
                {
                    CurrentTask = Tasks.Mission;
                }
                else if (_brains.TickExternalBuff(me, deltaTime))
                {
                    CurrentTask = Tasks.ExternalBuff;
                }
                else if (_sell.Tick(me, deltaTime))
                {
                    CurrentTask = Tasks.SellGoods;
                }
                else if (CurrentTask is Tasks.Resupply or Tasks.SellGoods or Tasks.Heal
                         or Tasks.Combat or Tasks.Selfbuff or Tasks.ExternalBuff or Tasks.Pet or Tasks.Mission)
                {
                    CurrentTask = Tasks.Nothing;
                }
            }

            switch (CurrentTask)
            {
                /*
                case Tasks.Mission: await _missionController.RunAsync(ct); break;
                case Tasks.Resupply: await _resupplier.RunAsync(ct); break;
                case Tasks.Buff: await _buffing.RunAsync(ct); break;
                */
                case Tasks.Nothing:
                default:
                    break; // idle: nothing to decide this tick, movement is on its own thread
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BotLoop tick failed.");
        }
    }
}