// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: GameCommands.cs
//
// Last modified: 2026-10-01
// Created:       2026-10-01 (ported from AOBuddy10 GameCommands.cs, R1.6)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOSharp.Clientless;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Utils;

/// <summary>
///     The wire commands every system sends by hand, in ONE place (AOBuddy10 R1.6) - so a fix to
///     how a command is shaped reaches every sender at once instead of hand-rolled copies.
/// </summary>
public static class GameCommands
{
    /// <summary>
    ///     GenericCmd Use on a WORLD object (lift, terminal, grid, whompa, bank, vending machine):
    ///     Count=1, Temp4=1 - the exact bytes the owner's client sends (capture 20260923-201746).
    ///     Safe from any thread; the send lock (the packet id) serialises it with the other senders.
    /// </summary>
    public static void UseObject(LocalPlayer me, Identity target)
    {
        Client.Send(new GenericCmdMessage
        { Action = GenericCmdAction.Use, User = me.Identity, Target = target, Count = 1, Temp4 = 1 });
    }

    /// <summary>
    ///     GenericCmd Use in its OPEN form (Count=1, Temp4=0): a bag in the inventory, or a container
    ///     holding items - opening it, not activating it (AOBuddy10 GameCommands, the stash's proven open).
    /// </summary>
    public static void OpenContainer(LocalPlayer me, Identity target)
    {
        Client.Send(new GenericCmdMessage
        { Action = GenericCmdAction.Use, User = me.Identity, Target = target, Count = 1, Temp4 = 0 });
    }

    /// <summary>
    ///     Use an INVENTORY ITEM on a target (the lockpick on a shut mission door): the target is
    ///     selected first, then the item's slot is used - the client's click-item, click-target.
    ///     The item form is Item.Use's proven shape (Count=1, no Temp4 - the stims).
    /// </summary>
    public static void UseItemOn(LocalPlayer me, Identity itemSlot, Identity target)
    {
        Targeting.SetTarget(target);
        Client.Send(new GenericCmdMessage
        { Action = GenericCmdAction.Use, User = me.Identity, Target = itemSlot, Count = 1 });
    }
}