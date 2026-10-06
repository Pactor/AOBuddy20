// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: ControlPriority.cs
// 
// Last modified: 2026-09-30 00:19
// Created:       2026-09-29 23:09
// 
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

namespace AOBuddy20.Enums;

public enum ControlPriority
{
    None = 0,
    Travel = 100,
    Selling = 200,
    ExternalBuffing = 400, // being buffed by someone else - ABOVE mission: the buff-up runs before a mission goes
    Mission = 300, // the blitz run - waits while a buff dance is in flight
    Resupply = 500, // above mission (a run must not starve mid-mission), below combat
    Selfbuffing = 600, // buffing self - above resupply, below combat
    Pet = 650, // pet summon/maintain/command - above selfbuffing, below combat (the pet follows the fight)
    Combat = 700, // interrupts everything below it
    LowHealthNanoEmergency = 800,
    Emergency = 900, // e.g. player dying, disconnect

}