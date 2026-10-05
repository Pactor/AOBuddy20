# MartialArtist (profession 2) — Castable Nano Reference

Generated 2026-10-04T17:23:20Z for the AOBuddy10 bot. **145 nanos.**

## Method / provenance

- **Nano data (ground truth):** `E:\Funcom\OmniCell\OmniCell\Datafiles\nanos.ocp` — OmniCell OMNICELL-CONTENT v3 pack (client 18.8.50_EP1 extraction), loaded through `OmniCell.Core` `NanoLoader.CacheAllNanos` (net10 DLL). 10965 nano formulas in the pack.
- **Names:** joined by nano id against `itemnames.sql` (`itemnames` table).
- **Enums:** stat ids and nano-line names from `AOSharp.Common/GameData/Stat.cs` and `NanoEnums.cs`.
- **Extractor source:** `E:\Funcom\AOBuddy10\tools\mp-nano-extractor` (re-runnable).
- **No web data was used for any id, name or level.**

## MA-castability criterion

A nano is included when one of its cast `Actions` (`ActionType.ToUse` = 3) has an `EqualTo` requirement on **`Profession`(stat 60) == 2** or **`VisualProfession`(stat 368) == 2**. VisualProfession is how most older profession nanos are locked. Split: 43 via Profession(60), 102 via VisualProfession(368). Every match uses the EqualTo operator.

## Category counts

| Category | Count |
| --- | ---: |
| Self / Team Buffs | 45 |
| Team Casts (friendly) | 2 |
| Fear | 4 |
| Heals / HoT | 30 |
| Misc / Utility | 4 |
| Armor Buffs | 21 |
| Damage Buffs | 12 |
| Evade Buffs | 9 |
| Initiative Buffs | 2 |
| Taunt / Aggro | 6 |
| Weapon & Special Attack Buffs | 10 |
| **Total** | **145** |

## Self / Team Buffs (45)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 216698 | Rumble of Distant Thunder | ControlledDestructionBuff (222) | 75 | SensoryImprovement > 475; BiologicalMetamorphosis > 475; Profession == 2; Level > 74; Expansion op22 2; Specialization op22 1 | Skill[317,74]; Skill[316,74]; Skill[311,74]; Skill[282,74]; Skill[281,74]; Skill[280,74]; Skill[279,74]; Skill[278,74] |
| 218072 | Aegis of Stone | NanoResistBuff (835) | 100 | BiologicalMetamorphosis > 652; SensoryImprovement > 652; VisualProfession == 2; Level > 99; Expansion op22 2; Specialization op22 1 | ModifyPercentage NanoResist +10 |
| 218078 | Petals on Water | ControlledDestructionBuff (222) | 145 | SensoryImprovement > 893; BiologicalMetamorphosis > 893; Profession == 2; Level > 144; Expansion op22 2; Specialization op22 2 | Skill[317,107]; Skill[316,107]; Skill[311,107]; Skill[282,107]; Skill[281,107]; Skill[280,107]; Skill[279,107]; Skill[278,107] |
| 218080 | Dragon Stance | ControlledDestructionBuff (222) | 185 | SensoryImprovement > 1030; BiologicalMetamorphosis > 1030; Profession == 2; Level > 184; Expansion op22 2; Specialization op22 4 | Skill[317,140]; Skill[316,140]; Skill[311,140]; Skill[282,140]; Skill[281,140]; Skill[280,140]; Skill[279,140]; Skill[278,140] |
| 218074 | Aegis of Metal | NanoResistBuff (835) | 195 | BiologicalMetamorphosis > 1049; SensoryImprovement > 1049; VisualProfession == 2; Level > 194; Expansion op22 2; Specialization op22 4 | ModifyPercentage NanoResist +20 |
| 218082 | Smell of Approaching Rain | ControlledDestructionBuff (222) | 203 | SensoryImprovement > 1158; BiologicalMetamorphosis > 1159; Profession == 2; Level > 202; Expansion op22 2; Specialization op22 8 | Skill[317,173]; Skill[316,173]; Skill[311,173]; Skill[282,173]; Skill[281,173]; Skill[280,173]; Skill[279,173]; Skill[278,173] |
| 218076 | Aegis of Notum | NanoResistBuff (835) | 207 | BiologicalMetamorphosis > 1649; SensoryImprovement > 1640; Profession == 2; Level > 206; Expansion op22 2; Specialization op22 8 | ModifyPercentage NanoResist +30 |
| 218084 | Grace of the Emperor Crane | ControlledDestructionBuff (222) | 209 | SensoryImprovement > 1364; BiologicalMetamorphosis > 1369; Profession == 2; Level > 208; Expansion op22 2; Specialization op22 8 | Skill[317,206]; Skill[316,206]; Skill[311,206]; Skill[282,206]; Skill[281,206]; Skill[280,206]; Skill[279,206]; Skill[278,206] |
| 218086 | Peaceful Midnight Sky | ControlledDestructionBuff (222) | 212 | SensoryImprovement > 1502; BiologicalMetamorphosis > 1509; Profession == 2; Level > 211; Expansion op22 2; Specialization op22 8 | Skill[317,239]; Skill[316,239]; Skill[311,239]; Skill[282,239]; Skill[281,239]; Skill[280,239]; Skill[279,239]; Skill[278,239] |
| 218088 | Ripples on the Calm Pond | ControlledDestructionBuff (222) | 216 | SensoryImprovement > 1606; BiologicalMetamorphosis > 1614; Profession == 2; Level > 215; Expansion op22 2; Specialization op22 8 | Skill[317,272]; Skill[316,272]; Skill[311,272]; Skill[282,272]; Skill[281,272]; Skill[280,272]; Skill[279,272]; Skill[278,272] |
| 218090 | Autumn Leaves | ControlledDestructionBuff (222) | 219 | SensoryImprovement > 1710; BiologicalMetamorphosis > 1719; Profession == 2; Level > 218; Expansion op22 2; Specialization op22 8 | Skill[317,305]; Skill[316,305]; Skill[311,305]; Skill[282,305]; Skill[281,305]; Skill[280,305]; Skill[279,305]; Skill[278,305] |
| 117226 | Assume Profession: Martial Artist | FalseProfession (218) | - | VisualProfession == 5; VisualProfession == 2; Profession == 5; PsychologicalModification > 393; SensoryImprovement > 393; BiologicalMetamorphosis > 393 | RemoveNanoStrain[680]; Skill[318,70]; ModifyPercentage PsychologicalModification -15; ModifyPercentage SensoryImprovement -15; ModifyPercentage MaterialMetamorphosis -15; ModifyPercentage MaterialCreation -15; ModifyPercentage SpaceTime -15; ModifyPercentage BiologicalMetamorphosis -15; ChangeVariable[368,2] |
| 81831 | Chirp of the Mournful Cricket | ControlledDestructionBuff (222) | - | SensoryImprovement > 663; BiologicalMetamorphosis > 663; VisualProfession == 2 | Skill[278,77]; Skill[279,77]; Skill[280,77]; Skill[281,77]; Skill[282,77]; Skill[311,77]; Skill[316,77]; Skill[317,77]; CastNano[157742] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 81828 | Crash of Thunder | ControlledDestructionBuff (222) | - | SensoryImprovement > 65; BiologicalMetamorphosis > 65; VisualProfession == 2 | Skill[278,19]; Skill[279,19]; Skill[280,19]; Skill[281,19]; Skill[282,19]; Skill[311,19]; Skill[316,19]; Skill[317,19]; CastNano[157743] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 32035 | False Profession: Martial Artist | FalseProfession (218) | - | VisualProfession == 5; VisualProfession == 2; Profession == 5; PsychologicalModification > 82; SensoryImprovement > 82; BiologicalMetamorphosis > 82 | RemoveNanoStrain[680]; Skill[318,150]; ModifyPercentage PsychologicalModification -25; ModifyPercentage SensoryImprovement -25; ModifyPercentage MaterialMetamorphosis -25; ModifyPercentage MaterialCreation -25; ModifyPercentage SpaceTime -25; ModifyPercentage BiologicalMetamorphosis -25; ChangeVariable[368,2] |
| 28880 | Four Fists of Kali | MartialArtsBuff (209) | - | PsychologicalModification > 781; SensoryImprovement > 781; VisualProfession == 2 | Modify MartialArts +160; Modify SharpObject +60; Modify Dimach +150; Modify FastAttack +30 |
| 266311 | Induce Stupor | NOSTACKING (0) | - | Expansion op22 32; BiologicalMetamorphosis > 799; Profession == 2; SensoryImprovement > 799; Profession == 12 | Modify BiologicalMetamorphosis -750; Modify MaterialMetamorphosis -750; Modify MaterialCreation -750; Modify PsychologicalModification -750; Modify SpaceTime -750; Modify SensoryImprovement -750 |
| 81821 | Inner Peace, Outward Rage | ControlledDestructionBuff (222) | - | SensoryImprovement > 786; BiologicalMetamorphosis > 786; VisualProfession == 2 | Skill[278,94]; Skill[279,94]; Skill[280,94]; Skill[281,94]; Skill[282,94]; Skill[311,94]; Skill[316,94]; Skill[317,94]; CastNano[157742] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 95528 | Last Minute Adjustments | CriticalIncreaseBuff (182) | - | PsychologicalModification > 647; SensoryImprovement > 647; SpaceTime > 647; Profession == 2 | Modify CriticalIncrease +23 |
| 43369 | Lesser Controlled Rage | ControlledRageBuff (246) | - | PsychologicalModification > 4; SensoryImprovement > 4; VisualProfession == 2 | Modify MartialArts +12; Modify PhysicalInit +8 |
| 28894 | Limbo Mastery | NOSTACKING (0) | - | SensoryImprovement > 86; MaterialMetamorphosis > 86; VisualProfession == 2 | Modify DuckExp +30 |
| 81820 | Lotus on Water | ControlledDestructionBuff (222) | - | SensoryImprovement > 142; BiologicalMetamorphosis > 142; VisualProfession == 2 | Skill[278,28]; Skill[279,28]; Skill[280,28]; Skill[281,28]; Skill[282,28]; Skill[311,28]; Skill[316,28]; Skill[317,28]; CastNano[157743] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 160575 | Mark of Danger | CriticalIncreaseBuff (182) | - | PsychologicalModification > 583; SensoryImprovement > 583; SpaceTime > 583; VisualProfession == 2 | Modify CriticalIncrease +8 |
| 160574 | Mark of Peril | CriticalIncreaseBuff (182) | - | PsychologicalModification > 736; SensoryImprovement > 736; SpaceTime > 736; VisualProfession == 2 | Modify CriticalIncrease +11 |
| 160576 | Mark of Risk | CriticalIncreaseBuff (182) | - | PsychologicalModification > 277; SensoryImprovement > 277; SpaceTime > 277; VisualProfession == 2 | Modify CriticalIncrease +6 |
| 28895 | Martial Arts Mastery | MartialArtsBuff (209) | - | PsychologicalModification > 240; SensoryImprovement > 240; VisualProfession == 2 | Modify MartialArts +60; Modify SharpObject +30 |
| 81819 | Meditate on an Autumn Leaf | ControlledDestructionBuff (222) | - | SensoryImprovement > 199; BiologicalMetamorphosis > 199; VisualProfession == 2 | Skill[278,35]; Skill[279,35]; Skill[280,35]; Skill[281,35]; Skill[282,35]; Skill[311,35]; Skill[316,35]; Skill[317,35]; CastNano[157743] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 117215 | Mimic Profession: Martial Artist | FalseProfession (218) | - | VisualProfession == 5; VisualProfession == 2; Profession == 5; PsychologicalModification > 691; SensoryImprovement > 691; BiologicalMetamorphosis > 691 | RemoveNanoStrain[680]; Skill[318,15]; ModifyPercentage PsychologicalModification -5; ModifyPercentage SensoryImprovement -5; ModifyPercentage MaterialMetamorphosis -5; ModifyPercentage MaterialCreation -5; ModifyPercentage SpaceTime -5; ModifyPercentage BiologicalMetamorphosis -5; ChangeVariable[368,2] |
| 28898 | Muscle Booster | StrengthBuff (156) | - | BiologicalMetamorphosis > 335; MaterialMetamorphosis > 335; VisualProfession == 2 | Modify Strength +25; Skill[279,2] |
| 28899 | Muscle Stim | StrengthBuff (156) | - | BiologicalMetamorphosis > 107; MaterialMetamorphosis > 107; VisualProfession == 2 | Modify Strength +12; Skill[279,1] |
| 28904 | Return Attack | RiposteBuff (155) | - | PsychologicalModification > 669; SensoryImprovement > 669; VisualProfession == 2 | Modify Riposte +80; Modify Parry +80 |
| 81818 | Sadness of the Willow | ControlledDestructionBuff (222) | - | SensoryImprovement > 354; BiologicalMetamorphosis > 354; VisualProfession == 2 | Skill[278,51]; Skill[279,51]; Skill[280,51]; Skill[281,51]; Skill[282,51]; Skill[311,51]; Skill[316,51]; Skill[317,51]; CastNano[157743] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 266304 | Slowdown | NOSTACKING (0) | - | Expansion op22 32; BiologicalMetamorphosis > 799; Profession == 9; MaterialCreation > 799; Profession == 2 | Modify EvadeClsC -400; Modify DodgeRanged -400; Modify DuckExp -400; Modify RunSpeed -400 |
| 266971 | Strengthen Ki | NOSTACKING (0) | - | MaterialCreation > 769; BiologicalMetamorphosis > 769; VisualProfession == 2 | Modify Strength +40; Modify ProjectileAC +574; Modify MeleeAC +676; Modify EnergyAC +574; Modify ChemicalAC +574; Modify RadiationAC +574; Modify ColdAC +574; Modify PoisonAC +574; Modify FireAC +574 |
| 266961 | Strengthen Spirit | NOSTACKING (0) | - | BiologicalMetamorphosis > 359; MaterialCreation > 359; VisualProfession == 2 | Modify ProjectileAC +229; Modify MeleeAC +269; Modify EnergyAC +229; Modify ChemicalAC +229; Modify RadiationAC +229; Modify ColdAC +229; Modify PoisonAC +229; Modify FireAC +229 |
| 95526 | Subconscious Guidance | CriticalIncreaseBuff (182) | - | PsychologicalModification > 154; SensoryImprovement > 154; SpaceTime > 154; Profession == 2 | Modify CriticalIncrease +12 |
| 81817 | Summer Rain | ControlledDestructionBuff (222) | - | SensoryImprovement > 708; BiologicalMetamorphosis > 708; VisualProfession == 2 | Skill[278,84]; Skill[279,84]; Skill[280,84]; Skill[281,84]; Skill[282,84]; Skill[311,84]; Skill[316,84]; Skill[317,84]; CastNano[157742] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 81816 | Sunrise over Pond | ControlledDestructionBuff (222) | - | SensoryImprovement > 432; BiologicalMetamorphosis > 432; VisualProfession == 2 | Skill[278,56]; Skill[279,56]; Skill[280,56]; Skill[281,56]; Skill[282,56]; Skill[311,56]; Skill[316,56]; Skill[317,56]; CastNano[157742] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 81815 | Sway of Bamboo | ControlledDestructionBuff (222) | - | SensoryImprovement > 527; BiologicalMetamorphosis > 527; VisualProfession == 2 | Skill[278,63]; Skill[279,63]; Skill[280,63]; Skill[281,63]; Skill[282,63]; Skill[311,63]; Skill[316,63]; Skill[317,63]; CastNano[157742] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 95527 | Universal Vulnerability Compendium | CriticalIncreaseBuff (182) | - | PsychologicalModification > 776; SensoryImprovement > 776; SpaceTime > 776; Profession == 2 | Modify CriticalIncrease +28 |
| 28862 | Velocity | RunspeedBuffs (150) | - | BiologicalMetamorphosis > 288; SensoryImprovement > 288; VisualProfession == 2 | Modify RunSpeed +70 |
| 95529 | Vulnerability Seeker | CriticalIncreaseBuff (182) | - | PsychologicalModification > 417; SensoryImprovement > 417; SpaceTime > 417; Profession == 2 | Modify CriticalIncrease +17 |
| 81814 | Waiting Panda | ControlledDestructionBuff (222) | - | SensoryImprovement > 614; BiologicalMetamorphosis > 614; VisualProfession == 2 | Skill[278,69]; Skill[279,69]; Skill[280,69]; Skill[281,69]; Skill[282,69]; Skill[311,69]; Skill[316,69]; Skill[317,69]; CastNano[157742] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 81813 | Wind-Blown Blossom | ControlledDestructionBuff (222) | - | SensoryImprovement > 272; BiologicalMetamorphosis > 272; VisualProfession == 2 | Skill[278,43]; Skill[279,43]; Skill[280,43]; Skill[281,43]; Skill[282,43]; Skill[311,43]; Skill[316,43]; Skill[317,43]; CastNano[157743] => 'Nano skills inoperative' [strain 0 NOSTACKING] {Modify SensoryImprovement -4000; Modify MaterialMetamorphosis -4000; Modify BiologicalMetamorphosis -4000; Modify PsychologicalModification -4000; Modify MaterialCreation -4000; Modify SpaceTime -4000} |
| 287559 | Zazen Stance | MartialArtistZazenStance (1058) | - | Profession == 2; BiologicalMetamorphosis > 100; SensoryImprovement > 100; Expansion op22 32; NumFightingOpponents == 0 | Modify NPCostModifier +30; Modify HealMultiplier +100; Modify CriticalIncrease -50; ScalingModify ProjectileDamageModifier -400; ScalingModify EnergyDamageModifier -400; ScalingModify MeleeDamageModifier -400; ScalingModify ColdDamageModifier -400; ScalingModify FireDamageModifier -400; ScalingModify PoisonDamageModifier -400; ScalingModify ChemicalDamageModifier -400; ScalingModify RadiationDamageModifier -400; ScalingModify EvadeClsC -400; ScalingModify DodgeRanged -400; ScalingModify DuckExp -400; Set[214,1]; SystemText[Cancelling the Zazen Stance exhausts your nanobots.] |

## Team Casts (friendly) (2)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 28868 | Cohort | NOSTACKING (0) | - | MaterialMetamorphosis > 619; MaterialCreation > 619; VisualProfession == 2 | TeamCastNano[162358] => 'Cohort' [strain 0 NOSTACKING] {Modify ProjectileAC +400; Modify MeleeAC +400; Modify EnergyAC +400; Modify ChemicalAC +400; Modify RadiationAC +400; Modify ColdAC +400; Modify PoisonAC +400; Modify FireAC +400} |
| 28890 | Horde | NOSTACKING (0) | - | MaterialMetamorphosis > 691; MaterialCreation > 691; VisualProfession == 2 | TeamCastNano[162359] => 'Horde' [strain 0 NOSTACKING] {Modify ProjectileDamageModifier +12; Modify MeleeDamageModifier +12; Modify EnergyDamageModifier +12; Modify ChemicalDamageModifier +12; Modify RadiationDamageModifier +12; Modify ColdDamageModifier +12; Modify FireDamageModifier +12; Modify PoisonDamageModifier +12; Modify MartialArts +6} |

## Fear (4)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 162837 | Presence of the Master | Fear (256) | 50 | Profession == 2; PsychologicalModification > 220; BiologicalMetamorphosis > 220; Level > 49 | AreaCastNano[162834,10] => 'Presence of the Master' [strain 256 Fear] {Fear[]} |
| 162839 | Presence of the Overlord | Fear (256) | 100 | Profession == 2; PsychologicalModification > 537; BiologicalMetamorphosis > 537; Level > 99 | AreaCastNano[162835,11] => 'Presence of the Overlord' [strain 256 Fear] {Fear[]} |
| 162841 | Presence of the Dominator | Fear (256) | 165 | Profession == 2; PsychologicalModification > 705; BiologicalMetamorphosis > 705; Level > 164 | AreaCastNano[162836,13] => 'Presence of the Overlord' [strain 256 Fear] {Fear[]} |
| 279345 | Footsteps of The Master | Fear_PVP (883) | - | VisualProfession == 2; PsychologicalModification > 119; BiologicalMetamorphosis > 119; Flags op44 3; Flags op4 0; Flags op42 0 | Fear[]; CastNano[279344] => 'Snared' [strain 887 UnremovableSnare] {ScalingModify RunSpeed -2000} |

## Heals / HoT (30)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 252052 | Flourishing Heal | SingleTargetHealing (951) | 201 | BiologicalMetamorphosis > 1194; SensoryImprovement > 1194; VisualProfession == 2; Level > 200; Expansion op22 2; Specialization op22 8 | Hit[27,980,1554,0]; CastNano[301473] => 'Flourishing Heal' [strain 859 MartialArtistHOTLineA] {Hit[27,1494,1942,0]} |
| 252054 | Soul of Rubi | SingleTargetHealing (951) | 201 | BiologicalMetamorphosis > 1369; SensoryImprovement > 1369; VisualProfession == 2; Level > 200; Expansion op22 2; Specialization op22 8 | Hit[27,1302,1808,0]; CastNano[301474] => 'Soul of Rubi' [strain 859 MartialArtistHOTLineA] {Hit[27,1884,2261,0]} |
| 266974 | Self Reconstruction | NOSTACKING (0) | 204 | BiologicalMetamorphosis > 1194; SensoryImprovement > 1194; Profession == 2; Level > 203 | Hit[27,980,1803,0] |
| 82030 | Enlightened Aura of Healing | SingleTargetHealing (951) | - | BiologicalMetamorphosis > 657; SensoryImprovement > 657; VisualProfession == 2 | Hit[27,566,1026,0]; CastNano[301471] => 'Enlightened Aura of Healing' [strain 859 MartialArtistHOTLineA] {Hit[27,823,987,0]} |
| 270353 | Enlightened Healing Touch | SingleTargetHealing (951) | - | BiologicalMetamorphosis > 774; SensoryImprovement > 774; Profession == 2 | Hit[27,667,930,0]; CastNano[301472] => 'Enlightened Healing Touch' [strain 859 MartialArtistHOTLineA] {Hit[27,969,1163,0]} |
| 28881 | Give Energy: 10 | NOSTACKING (0) | - | BiologicalMetamorphosis > 43; MaterialMetamorphosis > 43; VisualProfession == 2 | Hit[214,10,10,0] |
| 28882 | Give Energy: 100 | NOSTACKING (0) | - | BiologicalMetamorphosis > 251; MaterialMetamorphosis > 251; VisualProfession == 2 | Hit[214,100,100,0] |
| 28883 | Give Energy: 25 | NOSTACKING (0) | - | BiologicalMetamorphosis > 74; MaterialMetamorphosis > 74; VisualProfession == 2 | Hit[214,25,25,0] |
| 28884 | Give Energy: 50 | NOSTACKING (0) | - | BiologicalMetamorphosis > 133; MaterialMetamorphosis > 133; VisualProfession == 2 | Hit[214,50,50,0] |
| 28885 | Give Life: 10 | NOSTACKING (0) | - | BiologicalMetamorphosis > 38; MaterialMetamorphosis > 38; VisualProfession == 2; Health > 20 | Hit[27,-10,-10,92]; Hit[27,10,10,0] |
| 28886 | Give Life: 100 | NOSTACKING (0) | - | BiologicalMetamorphosis > 246; MaterialMetamorphosis > 246; VisualProfession == 2; Health > 200 | Hit[27,-100,-100,92]; Hit[27,100,100,0] |
| 28887 | Give Life: 25 | NOSTACKING (0) | - | BiologicalMetamorphosis > 69; MaterialMetamorphosis > 69; VisualProfession == 2; Health > 50 | Hit[27,-25,-25,92]; Hit[27,25,25,0] |
| 28888 | Give Life: 50 | NOSTACKING (0) | - | BiologicalMetamorphosis > 129; MaterialMetamorphosis > 129; VisualProfession == 2; Health > 100 | Hit[27,-50,-50,92]; Hit[27,50,50,0] |
| 82032 | Greater Healing Touch | SingleTargetHealing (951) | - | BiologicalMetamorphosis > 472; SensoryImprovement > 472; VisualProfession == 2 | Hit[27,350,568,0]; CastNano[301460] => 'Greater Healing Touch' [strain 859 MartialArtistHOTLineA] {Hit[27,591,710,0]} |
| 82031 | Greater Restore Essence | SingleTargetHealing (951) | - | BiologicalMetamorphosis > 598; SensoryImprovement > 598; VisualProfession == 2 | Hit[27,443,719,0]; CastNano[301470] => 'Greater Restore Essence' [strain 859 MartialArtistHOTLineA] {Hit[27,749,899,0]} |
| 82047 | Greater Team Healing Touch | TeamHealing (952) | - | BiologicalMetamorphosis > 502; SensoryImprovement > 502; VisualProfession == 2 | TeamCastNano[82027] => 'Greater Team Healing Touch' [strain 952 TeamHealing] {Hit[27,277,604,0]}; TeamCastNano[301490] => 'Greater Team Healing Touch' [strain 1059 MartialArtistHOT_LineB] {Hit[27,252,302,0]} |
| 82046 | Greater Team Restore Essence | TeamHealing (952) | - | BiologicalMetamorphosis > 621; SensoryImprovement > 621; VisualProfession == 2 | TeamCastNano[82026] => 'Greater Team Restore Essence' [strain 952 TeamHealing] {Hit[27,346,866,0]}; TeamCastNano[301491] => 'Greater Team Restore Essence' [strain 1059 MartialArtistHOT_LineB] {Hit[27,361,433,0]} |
| 82036 | Healing Aura | SingleTargetHealing (951) | - | BiologicalMetamorphosis > 344; SensoryImprovement > 344; VisualProfession == 2 | Hit[27,291,414,0]; CastNano[301459] => 'Healing Aura' [strain 859 MartialArtistHOTLineA] {Hit[27,431,518,0]} |
| 266965 | Healing Meditation | NOSTACKING (0) | - | BiologicalMetamorphosis > 598; SensoryImprovement > 598; VisualProfession == 2 | Hit[27,443,981,0] |
| 82035 | Healing Touch | SingleTargetHealing (951) | - | BiologicalMetamorphosis > 215; SensoryImprovement > 215; VisualProfession == 2 | Hit[27,181,259,0]; CastNano[301458] => 'Healing Touch' [strain 859 MartialArtistHOTLineA] {Hit[27,270,324,0]} |
| 82034 | Lesser Healing Touch | SingleTargetHealing (951) | - | BiologicalMetamorphosis > 60; SensoryImprovement > 60; VisualProfession == 2 | Hit[27,55,73,0]; CastNano[301452] => 'Lesser Healing Touch' [strain 859 MartialArtistHOTLineA] {Hit[27,76,92,0]} |
| 275698 | Matrix of Ka | SingleTargetHealing (951) | - | BiologicalMetamorphosis > 1689; SensoryImprovement > 1689; Profession == 2; NanoFocusLevel op22 64 | Hit[27,1512,2244,0]; CastNano[275699] => 'Matrix of Ka' [strain 859 MartialArtistHOTLineA] {Hit[27,2338,2805,0]} |
| 266955 | Personal Healing | NOSTACKING (0) | - | BiologicalMetamorphosis > 34; SensoryImprovement > 34; VisualProfession == 2 | Hit[27,34,59,0] |
| 82033 | Restore Essence | SingleTargetHealing (951) | - | BiologicalMetamorphosis > 111; SensoryImprovement > 111; VisualProfession == 2 | Hit[27,92,134,0]; CastNano[301457] => 'Restore Essence' [strain 859 MartialArtistHOTLineA] {Hit[27,140,168,0]} |
| 28901 | Spiritual Harmony | SingleTargetHealing (951) | - | BiologicalMetamorphosis > 34; SensoryImprovement > 34; VisualProfession == 2 | Hit[27,35,42,0]; CastNano[301451] => 'Spiritual Harmony' [strain 859 MartialArtistHOTLineA] {Hit[27,44,53,0]} |
| 301493 | Team Flourishing Heal | TeamHealing (952) | - | BiologicalMetamorphosis > 1122; SensoryImprovement > 1122; VisualProfession == 2; Expansion op22 32 | TeamCastNano[301494] => 'Team Flourishing Heal' [strain 952 TeamHealing] {Hit[27,1122,1346,0]}; TeamCastNano[301495] => 'Team Flourishing Heal' [strain 1059 MartialArtistHOT_LineB] {Hit[27,561,673,0]} |
| 82048 | Team Healing Aura | TeamHealing (952) | - | BiologicalMetamorphosis > 326; SensoryImprovement > 326; VisualProfession == 2 | TeamCastNano[82025] => 'Team Healing Aura' [strain 952 TeamHealing] {Hit[27,166,392,0]}; TeamCastNano[301489] => 'Team Healing Aura' [strain 1059 MartialArtistHOT_LineB] {Hit[27,164,196,0]} |
| 82049 | Team Healing Touch | TeamHealing (952) | - | BiologicalMetamorphosis > 191; SensoryImprovement > 191; VisualProfession == 2 | TeamCastNano[82028] => 'Team Healing Touch' [strain 952 TeamHealing] {Hit[27,99,230,0]}; TeamCastNano[301488] => 'Team Healing Touch' [strain 1059 MartialArtistHOT_LineB] {Hit[27,96,115,0]} |
| 273367 | Team Matrix of Ka | TeamHealing (952) | - | BiologicalMetamorphosis > 1719; SensoryImprovement > 1719; Profession == 2; NanoFocusLevel op22 64 | TeamCastNano[273368] => 'Team Matrix of Ka' [strain 952 TeamHealing] {Hit[27,1844,2213,0]}; TeamCastNano[301496] => 'Team Matrix of Ka' [strain 1059 MartialArtistHOT_LineB] {Hit[27,922,1106,0]} |
| 82050 | Team Restore Essence | TeamHealing (952) | - | BiologicalMetamorphosis > 95; SensoryImprovement > 95; VisualProfession == 2 | TeamCastNano[82029] => 'Team Restore Essence' [strain 952 TeamHealing] {Hit[27,54,115,0]}; TeamCastNano[301475] => 'Team Restore Essence' [strain 1059 MartialArtistHOT_LineB] {Hit[27,48,58,0]} |

## Misc / Utility (4)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 266967 | Debilitating Strike | NOSTACKING (0) | - | PsychologicalModification > 647; SensoryImprovement > 647; SpaceTime > 647; Profession == 2 | Skill[379,19] |
| 267526 | Sappo | NOSTACKING (0) | - | SensoryImprovement > 760; BiologicalMetamorphosis > 760; VisualProfession == 2; VisualProfession == 2 | Skill[278,50]; Skill[279,50]; Skill[280,50]; Skill[281,50]; Skill[282,50]; Skill[311,50]; Skill[316,50]; Skill[317,50] |
| 266963 | Smashing Fist | NOSTACKING (0) | - | SensoryImprovement > 527; BiologicalMetamorphosis > 527; VisualProfession == 2 | Skill[278,63]; Skill[279,63]; Skill[280,63]; Skill[281,63]; Skill[282,63]; Skill[311,63]; Skill[316,63]; Skill[317,63] |
| 266957 | Stinging Fist | NOSTACKING (0) | - | SensoryImprovement > 65; BiologicalMetamorphosis > 65; VisualProfession == 2 | Skill[278,19]; Skill[279,19]; Skill[280,19]; Skill[281,19]; Skill[282,19]; Skill[311,19]; Skill[316,19]; Skill[317,19] |

## Armor Buffs (21)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 28869 | Diamond Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 653; SensoryImprovement > 653; VisualProfession == 2 | Modify ProjectileAC +439; Modify MeleeAC +517; Modify EnergyAC +439; Modify ChemicalAC +439; Modify RadiationAC +439; Modify ColdAC +439; Modify PoisonAC +439; Modify FireAC +439 |
| 275700 | Form of Risan | ArmorBuff (3) | - | PsychologicalModification > 1243; SensoryImprovement > 1243; Profession == 2; NanoFocusLevel op22 64 | Modify Agility +30; Modify Strength -70; Modify DuckExp +60; Modify DodgeRanged +60; Modify EvadeClsC +60; Modify ProjectileAC -900; Modify MeleeAC -900; Modify EnergyAC -900; Modify ChemicalAC -900; Modify RadiationAC -900; Modify ColdAC -900; Modify PoisonAC -900; Modify FireAC -900 |
| 28879 | Form of Tessai | ArmorBuff (3) | - | SensoryImprovement > 769; BiologicalMetamorphosis > 769; VisualProfession == 2 | Modify Strength +40; Modify Agility -70; Modify ProjectileAC +574; Modify MeleeAC +676; Modify EnergyAC +574; Modify ChemicalAC +574; Modify RadiationAC +574; Modify ColdAC +574; Modify PoisonAC +574; Modify FireAC +574; Modify RunSpeed -250 |
| 75339 | Greater Shen Protection | ArmorBuff (3) | - | BiologicalMetamorphosis > 609; SensoryImprovement > 609; VisualProfession == 2 | Modify ProjectileAC +380; Modify MeleeAC +447; Modify EnergyAC +380; Modify ChemicalAC +380; Modify RadiationAC +380; Modify ColdAC +380; Modify PoisonAC +380; Modify FireAC +380 |
| 75343 | Greater Steel Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 407; SensoryImprovement > 407; VisualProfession == 2 | Modify ProjectileAC +258; Modify MeleeAC +304; Modify EnergyAC +258; Modify ChemicalAC +258; Modify RadiationAC +258; Modify ColdAC +258; Modify PoisonAC +258; Modify FireAC +258 |
| 75340 | Greater Titanium Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 563; SensoryImprovement > 563; VisualProfession == 2 | Modify ProjectileAC +349; Modify MeleeAC +411; Modify EnergyAC +349; Modify ChemicalAC +349; Modify RadiationAC +349; Modify ColdAC +349; Modify PoisonAC +349; Modify FireAC +349 |
| 75345 | Greater Wooden Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 256; SensoryImprovement > 256; VisualProfession == 2 | Modify ProjectileAC +169; Modify MeleeAC +199; Modify EnergyAC +169; Modify ChemicalAC +169; Modify RadiationAC +169; Modify ColdAC +169; Modify PoisonAC +169; Modify FireAC +169 |
| 75348 | Harden Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 82; SensoryImprovement > 82; VisualProfession == 2 | Modify ProjectileAC +53; Modify MeleeAC +62; Modify EnergyAC +53; Modify ChemicalAC +53; Modify RadiationAC +53; Modify ColdAC +53; Modify PoisonAC +53; Modify FireAC +53 |
| 75346 | Lesser Shen Protection | ArmorBuff (3) | - | BiologicalMetamorphosis > 166; SensoryImprovement > 166; VisualProfession == 2 | Modify ProjectileAC +110; Modify MeleeAC +129; Modify EnergyAC +110; Modify ChemicalAC +110; Modify RadiationAC +110; Modify ColdAC +110; Modify PoisonAC +110; Modify FireAC +110 |
| 75341 | Major Shen Protection | ArmorBuff (3) | - | BiologicalMetamorphosis > 460; SensoryImprovement > 460; VisualProfession == 2 | Modify ProjectileAC +288; Modify MeleeAC +339; Modify EnergyAC +288; Modify ChemicalAC +288; Modify RadiationAC +288; Modify ColdAC +288; Modify PoisonAC +288; Modify FireAC +288 |
| 75349 | Minor Shen Protection | ArmorBuff (3) | - | BiologicalMetamorphosis > 47; SensoryImprovement > 47; VisualProfession == 2 | Modify ProjectileAC +31; Modify MeleeAC +36; Modify EnergyAC +31; Modify ChemicalAC +31; Modify RadiationAC +31; Modify ColdAC +31; Modify PoisonAC +31; Modify FireAC +31 |
| 75336 | Monomolecular Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 699; SensoryImprovement > 699; VisualProfession == 2 | Modify ProjectileAC +499; Modify MeleeAC +587; Modify EnergyAC +499; Modify ChemicalAC +499; Modify RadiationAC +499; Modify ColdAC +499; Modify PoisonAC +499; Modify FireAC +499 |
| 75337 | Partial Diamond Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 631; SensoryImprovement > 631; VisualProfession == 2 | Modify ProjectileAC +410; Modify MeleeAC +482; Modify EnergyAC +410; Modify ChemicalAC +410; Modify RadiationAC +410; Modify ColdAC +410; Modify PoisonAC +410; Modify FireAC +410 |
| 28905 | Rubber Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 124; SensoryImprovement > 124; VisualProfession == 2 | Modify ProjectileAC +80; Modify MeleeAC +95; Modify EnergyAC +80; Modify ChemicalAC +80; Modify RadiationAC +80; Modify ColdAC +80; Modify PoisonAC +80; Modify FireAC +80 |
| 75344 | Shen Protection | ArmorBuff (3) | - | BiologicalMetamorphosis > 309; SensoryImprovement > 309; VisualProfession == 2 | Modify ProjectileAC +199; Modify MeleeAC +234; Modify EnergyAC +199; Modify ChemicalAC +199; Modify RadiationAC +199; Modify ColdAC +199; Modify PoisonAC +199; Modify FireAC +199 |
| 28907 | Steel Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 359; SensoryImprovement > 359; VisualProfession == 2 | Modify ProjectileAC +229; Modify MeleeAC +269; Modify EnergyAC +229; Modify ChemicalAC +229; Modify RadiationAC +229; Modify ColdAC +229; Modify PoisonAC +229; Modify FireAC +229 |
| 75338 | Supreme Shen Protection | ArmorBuff (3) | - | BiologicalMetamorphosis > 673; SensoryImprovement > 673; VisualProfession == 2 | Modify ProjectileAC +469; Modify MeleeAC +552; Modify EnergyAC +469; Modify ChemicalAC +469; Modify RadiationAC +469; Modify ColdAC +469; Modify PoisonAC +469; Modify FireAC +469 |
| 75342 | Titanium Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 512; SensoryImprovement > 512; VisualProfession == 2 | Modify ProjectileAC +318; Modify MeleeAC +375; Modify EnergyAC +318; Modify ChemicalAC +318; Modify RadiationAC +318; Modify ColdAC +318; Modify PoisonAC +318; Modify FireAC +318 |
| 75350 | Toughen Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 21; SensoryImprovement > 21; VisualProfession == 2 | Modify ProjectileAC +14; Modify MeleeAC +16; Modify EnergyAC +14; Modify ChemicalAC +14; Modify RadiationAC +14; Modify ColdAC +14; Modify PoisonAC +14; Modify FireAC +14 |
| 75351 | Transcendent Shen Protection | ArmorBuff (3) | - | BiologicalMetamorphosis > 729; SensoryImprovement > 729; VisualProfession == 2 | Modify ProjectileAC +538; Modify MeleeAC +621; Modify EnergyAC +538; Modify ChemicalAC +538; Modify RadiationAC +538; Modify ColdAC +538; Modify PoisonAC +538; Modify FireAC +538 |
| 75347 | Wooden Skin | ArmorBuff (3) | - | BiologicalMetamorphosis > 207; SensoryImprovement > 207; VisualProfession == 2 | Modify ProjectileAC +139; Modify MeleeAC +164; Modify EnergyAC +139; Modify ChemicalAC +139; Modify RadiationAC +139; Modify ColdAC +139; Modify PoisonAC +139; Modify FireAC +139 |

## Damage Buffs (12)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 270798 | Anvil Fists | DamageBuffs_LineA (4) | - | SensoryImprovement > 1742; BiologicalMetamorphosis > 1742; Profession == 2; NanoFocusLevel op22 64 | Modify MeleeDamageModifier +65; ChangeVariable[339,91] |
| 81830 | Corrosive Fists | DamageBuffs_LineA (4) | - | SensoryImprovement > 174; BiologicalMetamorphosis > 174; VisualProfession == 2; VisualProfession == 2 | Skill[279,9]; Skill[281,9]; ChangeVariable[339,93] |
| 81829 | Energized Fists | DamageBuffs_LineA (4) | - | SensoryImprovement > 103; BiologicalMetamorphosis > 103; VisualProfession == 2; VisualProfession == 2 | Skill[279,6]; Skill[280,6]; ChangeVariable[339,92] |
| 28876 | Fists of Fire | DamageBuffs_LineA (4) | - | SensoryImprovement > 320; BiologicalMetamorphosis > 320; VisualProfession == 2; VisualProfession == 2 | Skill[279,13]; Skill[316,13]; ChangeVariable[339,97] |
| 81826 | Fists of Shocking Touch | DamageBuffs_LineA (4) | - | SensoryImprovement > 230; BiologicalMetamorphosis > 230; VisualProfession == 2; VisualProfession == 2 | Skill[279,11]; Skill[280,11]; ChangeVariable[339,92] |
| 81827 | Fists of Stellar Harmony | DamageBuffs_LineA (4) | - | SensoryImprovement > 760; BiologicalMetamorphosis > 760; VisualProfession == 2; VisualProfession == 2 | Skill[279,26]; Skill[316,26]; ChangeVariable[339,97] |
| 81825 | Fists of the Lightning Crane | DamageBuffs_LineA (4) | - | SensoryImprovement > 679; BiologicalMetamorphosis > 679; VisualProfession == 2; VisualProfession == 2 | Skill[279,23]; Skill[280,23]; ChangeVariable[339,92] |
| 81823 | Fists of the Maelstrom | DamageBuffs_LineA (4) | - | SensoryImprovement > 398; BiologicalMetamorphosis > 398; VisualProfession == 2; VisualProfession == 2 | Skill[279,15]; Skill[280,15]; ChangeVariable[339,92] |
| 81824 | Fists of the Polar Star | DamageBuffs_LineA (4) | - | SensoryImprovement > 492; BiologicalMetamorphosis > 492; VisualProfession == 2; VisualProfession == 2 | Skill[279,17]; Skill[316,17]; ChangeVariable[339,97] |
| 81822 | Fists of the Sorrowful Toad | DamageBuffs_LineA (4) | - | SensoryImprovement > 578; BiologicalMetamorphosis > 578; VisualProfession == 2; VisualProfession == 2 | Skill[279,19]; Skill[281,19]; ChangeVariable[339,93] |
| 269470 | Fists of the Winter Flame | DamageBuff_LineC (252) | - | SensoryImprovement > 760; BiologicalMetamorphosis > 760; Profession == 2; Expansion op22 2 | Modify ProjectileDamageModifier +220; Modify MeleeDamageModifier +220; Modify EnergyDamageModifier +220; Modify ChemicalDamageModifier +220; Modify RadiationDamageModifier +220; Modify ColdDamageModifier +220; Modify FireDamageModifier +220; Modify PoisonDamageModifier +220; Modify PhysicalInit +400 |
| 28892 | Iron Fist | DamageBuffs_LineA (4) | - | SensoryImprovement > 4; BiologicalMetamorphosis > 4; VisualProfession == 2; VisualProfession == 2 | Skill[279,2] |

## Evade Buffs (9)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 218060 | Fake Out | MajorEvasionBuffs (144) | 25 | BiologicalMetamorphosis > 273; SensoryImprovement > 273; Profession == 2; Level > 24; Expansion op22 2; Specialization op22 1 | ModifyPercentage EvadeClsC +6 |
| 218062 | Misleading Conduct | MajorEvasionBuffs (144) | 125 | BiologicalMetamorphosis > 812; SensoryImprovement > 812; Profession == 2; Level > 124; Expansion op22 2; Specialization op22 2 | ModifyPercentage EvadeClsC +10 |
| 218064 | Ward Blow | MajorEvasionBuffs (144) | 175 | BiologicalMetamorphosis > 1005; SensoryImprovement > 1005; Profession == 2; Level > 174; Expansion op22 2; Specialization op22 4 | ModifyPercentage EvadeClsC +12 |
| 218066 | Shuffle Step | MajorEvasionBuffs (144) | 205 | BiologicalMetamorphosis > 1229; SensoryImprovement > 1226; Profession == 2; Level > 204; Expansion op22 2; Specialization op22 8 | ModifyPercentage EvadeClsC +15 |
| 218068 | Elude Step | MajorEvasionBuffs (144) | 214 | BiologicalMetamorphosis > 1544; SensoryImprovement > 1537; Profession == 2; Level > 213; Expansion op22 2; Specialization op22 8 | ModifyPercentage EvadeClsC +19 |
| 218070 | Stutter Step | MajorEvasionBuffs (144) | 220 | BiologicalMetamorphosis > 1754; SensoryImprovement > 1744; Profession == 2; Level > 219; Expansion op22 2; Specialization op22 8 | ModifyPercentage EvadeClsC +24 |
| 28872 | Elusive Target | MajorEvasionBuffs (144) | - | PsychologicalModification > 183; SensoryImprovement > 183; VisualProfession == 2 | Modify DuckExp +50; Modify DodgeRanged +50; Modify EvadeClsC +50 |
| 28878 | Fleet Foot | MajorEvasionBuffs (144) | - | SensoryImprovement > 383; PsychologicalModification > 383; VisualProfession == 2 | Modify DuckExp +85; Modify DodgeRanged +85; Modify EvadeClsC +85 |
| 28903 | Reduce Inertia | MajorEvasionBuffs (144) | - | PsychologicalModification > 627; SensoryImprovement > 627; VisualProfession == 2 | Modify DuckExp +120; Modify DodgeRanged +120; Modify EvadeClsC +120 |

## Initiative Buffs (2)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 28875 | First Strike | InitiativeBuffs (152) | - | PsychologicalModification > 443; SensoryImprovement > 443; VisualProfession == 2 | Modify MeleeInit +93; Modify PhysicalInit +93 |
| 273372 | Unnoticed Strike | InitiativeBuffs (152) | - | PsychologicalModification > 1243; SensoryImprovement > 1243; Profession == 2; NanoFocusLevel op22 64 | Modify MeleeInit +315; Modify PhysicalInit +315; Modify NanoCInit +200 |

## Taunt / Aggro (6)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 28866 | Bad Blood | NOSTACKING (0) | - | PsychologicalModification > 116; VisualProfession == 2 | TauntNpc[274] |
| 100217 | Distracting Nuisance | NOSTACKING (0) | - | PsychologicalModification > 261; VisualProfession == 2 | TauntNpc[1000] |
| 100215 | Encourage Hatred | NOSTACKING (0) | - | PsychologicalModification > 402; VisualProfession == 2 | TauntNpc[4200] |
| 100214 | Eternal Enmity | NOSTACKING (0) | - | PsychologicalModification > 751; VisualProfession == 2 | TauntNpc[8500] |
| 301936 | Irrational Grudge | NOSTACKING (0) | - | PsychologicalModification > 751; VisualProfession == 2 | TauntNpc[10000] |
| 100216 | Seething Resentment | NOSTACKING (0) | - | PsychologicalModification > 625; VisualProfession == 2 | TauntNpc[5600] |

## Weapon & Special Attack Buffs (10)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 28870 | Dirty Fighter | BrawlBuff (154) | - | PsychologicalModification > 158; SensoryImprovement > 158; VisualProfession == 2 | Modify Brawl +45 |
| 210323 | Fervor of the Devotee | FastAttackBuffs (519) | - | Profession == 15; Profession == 14; Profession == 2; Profession == 9; PsychologicalModification > 308; SensoryImprovement > 308 | Modify FastAttack +70 |
| 210327 | Fervor of the Disciple | FastAttackBuffs (519) | - | Profession == 15; Profession == 14; Profession == 2; Profession == 9; PsychologicalModification > 789; SensoryImprovement > 789 | Modify FastAttack +110 |
| 210329 | Fervor of the Fanatic | FastAttackBuffs (519) | - | Profession == 15; Profession == 14; Profession == 2; Profession == 9; PsychologicalModification > 878; SensoryImprovement > 878 | Modify FastAttack +135 |
| 210321 | Fervor of the Henchman | FastAttackBuffs (519) | - | Profession == 15; Profession == 14; Profession == 2; Profession == 9; PsychologicalModification > 81; SensoryImprovement > 81 | Modify FastAttack +30 |
| 210325 | Fervor of the Minion | FastAttackBuffs (519) | - | Profession == 15; Profession == 14; Profession == 2; Profession == 9; PsychologicalModification > 555; SensoryImprovement > 555 | Modify FastAttack +90 |
| 210331 | Fervor of the Zealot | FastAttackBuffs (519) | - | Profession == 15; Profession == 14; Profession == 2; Profession == 9; PsychologicalModification > 949; SensoryImprovement > 949 | Modify FastAttack +150 |
| 263249 | Greater Kyudo | MartialArtistBowBuffs (815) | - | Expansion op22 2; PsychologicalModification > 513; SensoryImprovement > 513; Profession == 2 | Modify Bow +55; Modify AimedShot +55 |
| 263246 | Kyudo | MartialArtistBowBuffs (815) | - | Expansion op22 2; PsychologicalModification > 513; SensoryImprovement > 513; Profession == 2 | Modify Bow +40; Modify AimedShot +40 |
| 273370 | Supreme Kyudo | MartialArtistBowBuffs (815) | - | PsychologicalModification > 1243; SensoryImprovement > 1243; Profession == 2; NanoFocusLevel op22 64 | Modify Bow +120; Modify AimedShot +120 |

