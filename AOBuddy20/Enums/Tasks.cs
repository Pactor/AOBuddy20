// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: Tasks.cs
// 
// Last modified: 2026-09-30 00:19
// Created:       2026-09-29 23:09
// 
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

namespace AOBuddy20.Enums;

public enum Tasks
{
    Nothing,
    Assist,
    Mission,
    LootBody,
    Resupply,
    SellGoods,
    Buff,
    Heal,
    Combat, // brains: a CombatBrain engagement is open
    Selfbuff, // brains: a SelfbuffingBrain episode is open
    ExternalBuff, // brains: an ExternalBuffingBrain episode is open
    Pet, // brains: a PetBrain episode is open (summon/maintain/command)
}