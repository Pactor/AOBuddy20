# Fixer (profession 4) — Castable Nano Reference

Generated 2026-10-04T17:23:16Z for the AOBuddy10 bot. **226 nanos.**

## Method / provenance

- **Nano data (ground truth):** `E:\Funcom\OmniCell\OmniCell\Datafiles\nanos.ocp` — OmniCell OMNICELL-CONTENT v3 pack (client 18.8.50_EP1 extraction), loaded through `OmniCell.Core` `NanoLoader.CacheAllNanos` (net10 DLL). 10965 nano formulas in the pack.
- **Names:** joined by nano id against `itemnames.sql` (`itemnames` table).
- **Enums:** stat ids and nano-line names from `AOSharp.Common/GameData/Stat.cs` and `NanoEnums.cs`.
- **Extractor source:** `E:\Funcom\AOBuddy10\tools\mp-nano-extractor` (re-runnable).
- **No web data was used for any id, name or level.**

## FIX-castability criterion

A nano is included when one of its cast `Actions` (`ActionType.ToUse` = 3) has an `EqualTo` requirement on **`Profession`(stat 60) == 4** or **`VisualProfession`(stat 368) == 4**. VisualProfession is how most older profession nanos are locked. Split: 108 via Profession(60), 118 via VisualProfession(368). Every match uses the EqualTo operator.

## Category counts

| Category | Count |
| --- | ---: |
| Summon Weapon / Shield (Creation) | 55 |
| Self / Team Buffs | 26 |
| Team Casts (friendly) | 19 |
| Debuffs | 7 |
| Area Casts (AoE) | 9 |
| Root | 8 |
| Snare | 14 |
| Root / Snare Breakers | 15 |
| Heals / HoT | 28 |
| Travel / Summon Utility | 7 |
| Misc / Utility | 7 |
| Damage Buffs | 17 |
| Evade Buffs | 13 |
| Weapon & Special Attack Buffs | 1 |
| **Total** | **226** |

## Summon Weapon / Shield (Creation) (55)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 224403 | Summon Shadowweb Spinner MK I | NOSTACKING (0) | 25 | Profession == 4; SpaceTime > 137; BiologicalMetamorphosis > 137; MaterialMetamorphosis > 151; Level > 24; Expansion op22 2 | SpawnItem[SASN,1,0] |
| 224405 | Summon Shadowweb Spinner MK II | NOSTACKING (0) | 75 | Profession == 4; SpaceTime > 399; BiologicalMetamorphosis > 399; MaterialMetamorphosis > 462; Level > 74; Expansion op22 2; Specialization op22 1 | SpawnItem[SHSN,1,0] |
| 224408 | Summon Shadowweb Spinner MK III | NOSTACKING (0) | 100 | Profession == 4; SpaceTime > 550; BiologicalMetamorphosis > 550; MaterialMetamorphosis > 652; Level > 99; Expansion op22 2; Specialization op22 2 | SpawnItem[SWSE,1,0] |
| 224410 | Summon Shadowweb Spinner MK IV | NOSTACKING (0) | 145 | Profession == 4; SpaceTime > 782; BiologicalMetamorphosis > 782; MaterialMetamorphosis > 896; Level > 144; Expansion op22 2; Specialization op22 4 | SpawnItem[SDSN,1,0] |
| 224412 | Summon Shadowweb Spinner MK V | NOSTACKING (0) | 165 | Profession == 4; SpaceTime > 847; BiologicalMetamorphosis > 847; MaterialMetamorphosis > 961; Level > 164; Expansion op22 2; Specialization op22 4 | SpawnItem[SBSM,1,0] |
| 224414 | Summon Shadowweb Spinner MK VI | NOSTACKING (0) | 201 | Profession == 4; SpaceTime > 953; BiologicalMetamorphosis > 953; MaterialMetamorphosis > 1089; Level > 200; Expansion op22 2; Specialization op22 8 | SpawnItem[SASM,1,0] |
| 224416 | Summon Shadowweb Spinner MK VII | NOSTACKING (0) | 205 | Profession == 4; SpaceTime > 1051; BiologicalMetamorphosis > 1054; MaterialMetamorphosis > 1229; Level > 204; Expansion op22 2; Specialization op22 8 | SpawnItem[SESM,1,0] |
| 224418 | Summon Shadowweb Spinner MK VIII | NOSTACKING (0) | 209 | Profession == 4; SpaceTime > 1149; BiologicalMetamorphosis > 1154; MaterialMetamorphosis > 1369; Level > 208; Expansion op22 2; Specialization op22 8 | SpawnItem[SWSN,1,0] |
| 224420 | Summon Shadowweb Spinner MK IX | NOSTACKING (0) | 215 | Profession == 4; SpaceTime > 1297; BiologicalMetamorphosis > 1304; MaterialMetamorphosis > 1579; Level > 214; Expansion op22 2; Specialization op22 8 | SpawnItem[SWSP,1,0] |
| 242954 | Energize Rubi-Ka Grid Tunnel | NOSTACKING (0) | 220 | Profession == 4; MaterialMetamorphosis > 1754; SpaceTime > 1419; Level > 219; Flags op35 160982; ExpansionPlayfield == 1; Expansion op22 2; Specialization op22 8 | SpawnItem[QUGD,1,0] |
| 224422 | Summon Shadowweb Spinner MK X | NOSTACKING (0) | 220 | Profession == 4; SpaceTime > 1419; BiologicalMetamorphosis > 1429; MaterialMetamorphosis > 1754; Level > 219; Expansion op22 2; Specialization op22 8 | SpawnItem[SBSR,1,0] |
| 155345 | Bootleg Beamers 'n Bolters (OP-C) | NOSTACKING (0) | - | SpaceTime > 314; MaterialCreation > 400; VisualProfession == 4; Cash > 155 | Hit[61,-155,-155,0]; SpawnItem[FESM,100,0] |
| 155340 | Bootleg Beamers 'n Bolters (OP-CC) | NOSTACKING (0) | - | SpaceTime > 582; MaterialCreation > 686; VisualProfession == 4; Cash > 670 | Hit[61,-670,-670,0]; SpawnItem[FESO,200,0] |
| 155342 | Bootleg Beamers 'n Bolters (OP-CLX) | NOSTACKING (0) | - | SpaceTime > 499; MaterialCreation > 624; VisualProfession == 4; Cash > 421 | Hit[61,-421,-421,0]; SpawnItem[FESO,160,0] |
| 155341 | Bootleg Beamers 'n Bolters (OP-CLXXX) | NOSTACKING (0) | - | SpaceTime > 548; MaterialCreation > 643; VisualProfession == 4; Cash > 538 | Hit[61,-538,-538,0]; SpawnItem[FESO,180,0] |
| 155343 | Bootleg Beamers 'n Bolters (OP-CXL) | NOSTACKING (0) | - | SpaceTime > 449; MaterialCreation > 610; VisualProfession == 4; Cash > 318 | Hit[61,-318,-318,0]; SpawnItem[FESO,140,0] |
| 155344 | Bootleg Beamers 'n Bolters (OP-CXX) | NOSTACKING (0) | - | SpaceTime > 387; MaterialCreation > 527; VisualProfession == 4; Cash > 229 | Hit[61,-229,-229,0]; SpawnItem[FESO,120,0] |
| 155347 | Bootleg Beamers 'n Bolters (OP-LX) | NOSTACKING (0) | - | SpaceTime > 170; MaterialCreation > 210; VisualProfession == 4; Cash > 50 | Hit[61,-50,-50,0]; SpawnItem[FESM,60,0] |
| 155346 | Bootleg Beamers 'n Bolters (OP-LXXX) | NOSTACKING (0) | - | SpaceTime > 243; MaterialCreation > 312; VisualProfession == 4; Cash > 95 | Hit[61,-95,-95,0]; SpawnItem[FESM,80,0] |
| 155348 | Bootleg Beamers 'n Bolters (OP-XL) | NOSTACKING (0) | - | SpaceTime > 111; MaterialCreation > 139; VisualProfession == 4; Cash > 19 | Hit[61,-19,-19,0]; SpawnItem[FESM,40,0] |
| 155349 | Bootleg Beamers 'n Bolters (OP-XX) | NOSTACKING (0) | - | SpaceTime > 48; MaterialCreation > 61; VisualProfession == 4; Cash > 3 | Hit[61,-3,-3,0]; SpawnItem[FESM,20,0] |
| 155355 | Bootleg Blades 'n Blunts (OP-C) | NOSTACKING (0) | - | SpaceTime > 314; MaterialCreation > 400; VisualProfession == 4; Cash > 138 | Hit[61,-138,-138,0]; SpawnItem[FXSM,100,0] |
| 155350 | Bootleg Blades 'n Blunts (OP-CC) | NOSTACKING (0) | - | SpaceTime > 582; MaterialCreation > 686; VisualProfession == 4; Cash > 596 | Hit[61,-596,-596,0]; SpawnItem[FXSO,200,0] |
| 155352 | Bootleg Blades 'n Blunts (OP-CLX) | NOSTACKING (0) | - | SpaceTime > 499; MaterialCreation > 624; VisualProfession == 4; Cash > 374 | Hit[61,-374,-374,0]; SpawnItem[FXSO,160,0] |
| 155351 | Bootleg Blades 'n Blunts (OP-CLXXX) | NOSTACKING (0) | - | SpaceTime > 548; MaterialCreation > 643; VisualProfession == 4; Cash > 478 | Hit[61,-478,-478,0]; SpawnItem[FXSO,180,0] |
| 155353 | Bootleg Blades 'n Blunts (OP-CXL) | NOSTACKING (0) | - | SpaceTime > 449; MaterialCreation > 610; VisualProfession == 4; Cash > 283 | Hit[61,-283,-283,0]; SpawnItem[FXSO,140,0] |
| 155354 | Bootleg Blades 'n Blunts (OP-CXX) | NOSTACKING (0) | - | SpaceTime > 387; MaterialCreation > 527; VisualProfession == 4; Cash > 204 | Hit[61,-204,-204,0]; SpawnItem[FXSO,120,0] |
| 155357 | Bootleg Blades 'n Blunts (OP-LX) | NOSTACKING (0) | - | SpaceTime > 170; MaterialCreation > 210; VisualProfession == 4; Cash > 44 | Hit[61,-44,-44,0]; SpawnItem[FXSM,60,0] |
| 155356 | Bootleg Blades 'n Blunts (OP-LXXX) | NOSTACKING (0) | - | SpaceTime > 243; MaterialCreation > 312; VisualProfession == 4; Cash > 85 | Hit[61,-85,-85,0]; SpawnItem[FXSM,80,0] |
| 155358 | Bootleg Blades 'n Blunts (OP-XL) | NOSTACKING (0) | - | SpaceTime > 111; MaterialCreation > 139; VisualProfession == 4; Cash > 17 | Hit[61,-17,-17,0]; SpawnItem[FXSM,40,0] |
| 155359 | Bootleg Blades 'n Blunts (OP-XX) | NOSTACKING (0) | - | SpaceTime > 48; MaterialCreation > 61; VisualProfession == 4; Cash > 2 | Hit[61,-2,-2,0]; SpawnItem[FXSM,20,0] |
| 160979 | Hack Grid Data Stream | FixerGrid (1039) | - | Profession == 4; SensoryImprovement > 179; SpaceTime > 131; BreakingEntry > 202; Flags op106 1 | SpawnItem[FIGI,1,0]; SpawnItem[QIGR,1,0] |
| 152181 | Restock Ammo (Level OP-C) | NOSTACKING (0) | - | SpaceTime > 475; MaterialCreation > 618; VisualProfession == 4; Cash > 147 | Hit[61,-147,-147,0]; SpawnItem[FXSU,1,0]; SpawnItem[FXSU,1,0]; SpawnItem[FXSU,1,0] |
| 152183 | Restock Ammo (Level OP-CC) | NOSTACKING (0) | - | SpaceTime > 493; MaterialCreation > 622; VisualProfession == 4; Cash > 315 | Hit[61,-315,-315,0]; SpawnItem[FRSG,1,0]; SpawnItem[FRSG,1,0]; SpawnItem[FRSG,1,0] |
| 301867 | Restock Ammo (Level OP-CCXL) | NOSTACKING (0) | - | SpaceTime > 662; MaterialCreation > 792; VisualProfession == 4; Cash > 9999; Flags op106 1 | Hit[61,-10000,-10000,0]; SpawnItem[OZ3J,1,0] |
| 129714 | Restock Ammo (Level OP-I) | NOSTACKING (0) | - | SpaceTime > 64; MaterialCreation > 81; VisualProfession == 4; Cash > 57 | Hit[61,-57,-57,0]; SpawnItem[FXSU,1,0] |
| 152180 | Restock Ammo (Level OP-II) | NOSTACKING (0) | - | SpaceTime > 85; MaterialCreation > 110; VisualProfession == 4; Cash > 115 | Hit[61,-115,-115,0]; SpawnItem[FRSG,1,0] |
| 152182 | Restock Ammo (Level OP-X) | NOSTACKING (0) | - | SpaceTime > 290; MaterialCreation > 365; VisualProfession == 4; Cash > 103 | Hit[61,-103,-103,0]; SpawnItem[FXSU,1,0]; SpawnItem[FXSU,1,0] |
| 152179 | Restock Ammo (Level OP-XX) | NOSTACKING (0) | - | SpaceTime > 328; MaterialCreation > 427; VisualProfession == 4; Cash > 208 | Hit[61,-208,-208,0]; SpawnItem[FRSG,1,0]; SpawnItem[FRSG,1,0] |
| 297632 | Restock Special Ammo | SummonItem (1036) | - | Burst > 4; Profession == 4; Cash > 500; Flags op106 1 | Hit[61,-500,-500,0]; SpawnItem[NHVX,10,0]; SpawnItem[NHVX,20,0]; SpawnItem[NHVX,30,0]; SpawnItem[NHVX,40,0]; SpawnItem[NHVX,50,0]; SpawnItem[NHVX,60,0]; SpawnItem[NHVX,70,0]; SpawnItem[NHVX,80,0]; SpawnItem[NHVX,90,0]; SpawnItem[NHVX,100,0]; SpawnItem[NHVX,110,0]; SpawnItem[NHVX,120,0]; SpawnItem[NHVX,130,0]; SpawnItem[NHVX,140,0]; SpawnItem[NHVX,150,0]; SpawnItem[NHVX,160,0]; SpawnItem[NHVX,170,0]; SpawnItem[NHVX,180,0]; SpawnItem[NHVX,190,0]; SpawnItem[NHVX,200,0]; SpawnItem[NHVX,225,0]; SpawnItem[NHVX,250,0]; SpawnItem[NHVX,275,0]; SpawnItem[NHVX,300,0] |
| 155335 | Smuggler Shipment (OP-C) | NOSTACKING (0) | - | SpaceTime > 318; MaterialCreation > 407; VisualProfession == 4; Cash > 159 | Hit[61,-159,-159,0]; SpawnItem[FRSI,100,0] |
| 155330 | Smuggler Shipment (OP-CC) | NOSTACKING (0) | - | SpaceTime > 584; MaterialCreation > 688; VisualProfession == 4; Cash > 677 | Hit[61,-677,-677,0]; SpawnItem[FRSO,200,0] |
| 155332 | Smuggler Shipment (OP-CLX) | NOSTACKING (0) | - | SpaceTime > 502; MaterialCreation > 625; VisualProfession == 4; Cash > 426 | Hit[61,-426,-426,0]; SpawnItem[FRSO,160,0] |
| 155331 | Smuggler Shipment (OP-CLXXX) | NOSTACKING (0) | - | SpaceTime > 550; MaterialCreation > 644; VisualProfession == 4; Cash > 545 | Hit[61,-545,-545,0]; SpawnItem[FRSO,180,0] |
| 155333 | Smuggler Shipment (OP-CXL) | NOSTACKING (0) | - | SpaceTime > 451; MaterialCreation > 611; VisualProfession == 4; Cash > 323 | Hit[61,-323,-323,0]; SpawnItem[FRSO,140,0] |
| 155334 | Smuggler Shipment (OP-CXX) | NOSTACKING (0) | - | SpaceTime > 391; MaterialCreation > 533; VisualProfession == 4; Cash > 233 | Hit[61,-233,-233,0]; SpawnItem[FRSO,120,0] |
| 155337 | Smuggler Shipment (OP-LX) | NOSTACKING (0) | - | SpaceTime > 174; MaterialCreation > 215; VisualProfession == 4; Cash > 52 | Hit[61,-52,-52,0]; SpawnItem[FRSI,60,0] |
| 155336 | Smuggler Shipment (OP-LXXX) | NOSTACKING (0) | - | SpaceTime > 246; MaterialCreation > 316; VisualProfession == 4; Cash > 98 | Hit[61,-98,-98,0]; SpawnItem[FRSI,80,0] |
| 155338 | Smuggler Shipment (OP-XL) | NOSTACKING (0) | - | SpaceTime > 114; MaterialCreation > 142; VisualProfession == 4; Cash > 20 | Hit[61,-20,-20,0]; SpawnItem[FRSI,40,0] |
| 155339 | Smuggler Shipment (OP-XX) | NOSTACKING (0) | - | SpaceTime > 51; MaterialCreation > 65; VisualProfession == 4; Cash > 3 | Hit[61,-3,-3,0]; SpawnItem[FRSI,20,0] |
| 155186 | Summon Grid Armor Mk I | SummonItem (1036) | - | Profession == 4; BreakingEntry > 173; SpaceTime > 260; MaterialMetamorphosis > 364 | SpawnItem[GRAO,78,0] |
| 155188 | Summon Grid Armor Mk II | SummonItem (1036) | - | Profession == 4; BreakingEntry > 253; SpaceTime > 379; MaterialMetamorphosis > 532 | SpawnItem[GMAK,111,0] |
| 155187 | Summon Grid Armor Mk III | SummonItem (1036) | - | Profession == 4; BreakingEntry > 314; SpaceTime > 471; MaterialMetamorphosis > 643 | SpawnItem[GIAO,111,0] |
| 155189 | Summon Grid Armor Mk IV | SummonItem (1036) | - | Profession == 4; BreakingEntry > 399; SpaceTime > 590; MaterialMetamorphosis > 781 | SpawnItem[GIAM,198,0] |
| 273349 | Summon Shadowweb Spinner MK XI | NOSTACKING (0) | - | Profession == 4; SpaceTime > 1501; BiologicalMetamorphosis > 1501; MaterialMetamorphosis > 1894; NanoFocusLevel op22 64 | SpawnItem[SESX,1,0] |

## Self / Team Buffs (26)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 162484 | Suppressor | FixerSuppressorBuff (253) | 25 | Profession == 4; PsychologicalModification > 166; SensoryImprovement > 166; Level > 24 | Modify MGSMG +46; Modify Burst +30 |
| 162486 | Major Suppressor | FixerSuppressorBuff (253) | 75 | Profession == 4; PsychologicalModification > 349; SensoryImprovement > 349; Level > 74 | Modify MGSMG +79; Modify Burst +50 |
| 162488 | Supreme Suppressor | FixerSuppressorBuff (253) | 100 | Profession == 4; PsychologicalModification > 502; SensoryImprovement > 502; Level > 99 | Modify MGSMG +100; Modify Burst +64; Modify MultiRanged +10 |
| 227390 | Slip of Accident | FixerDodgeBuffLine (673) | 125 | PsychologicalModification > 728; SensoryImprovement > 728; Profession == 4; Level > 124; Expansion op22 2; Specialization op22 2 | ModifyPercentage DodgeRanged +5 |
| 162490 | Spray With Lead | FixerSuppressorBuff (253) | 135 | Profession == 4; PsychologicalModification > 636; SensoryImprovement > 636; Level > 134 | Modify MGSMG +121; Modify Burst +78; Modify MultiRanged +15; Modify ProjectileDamageModifier +3; Modify MeleeDamageModifier +3; Modify EnergyDamageModifier +3; Modify ChemicalDamageModifier +3; Modify RadiationDamageModifier +3; Modify ColdDamageModifier +3; Modify FireDamageModifier +3; Modify PoisonDamageModifier +3 |
| 162492 | Shower With Lead | FixerSuppressorBuff (253) | 175 | Profession == 4; PsychologicalModification > 725; SensoryImprovement > 725; Level > 174 | Modify MGSMG +134; Modify Burst +85; Modify FullAuto +85; Modify MultiRanged +22; Modify ProjectileDamageModifier +5; Modify MeleeDamageModifier +5; Modify EnergyDamageModifier +5; Modify ChemicalDamageModifier +5; Modify RadiationDamageModifier +5; Modify ColdDamageModifier +5; Modify FireDamageModifier +5; Modify PoisonDamageModifier +5; Modify RangedInit +25; Modify DuckExp +5; Modify DodgeRanged +5; Modify EvadeClsC +5 |
| 227392 | Slip of Idea | FixerDodgeBuffLine (673) | 175 | PsychologicalModification > 983; SensoryImprovement > 983; Profession == 4; Level > 174; Expansion op22 2; Specialization op22 4 | ModifyPercentage DodgeRanged +10 |
| 162494 | Frenzy of Shells | FixerSuppressorBuff (253) | 195 | Profession == 4; PsychologicalModification > 786; SensoryImprovement > 786; Level > 194 | Modify MGSMG +141; Modify Burst +90; Modify FullAuto +90; Modify MultiRanged +38; Modify ProjectileDamageModifier +8; Modify MeleeDamageModifier +8; Modify EnergyDamageModifier +8; Modify ChemicalDamageModifier +8; Modify RadiationDamageModifier +8; Modify ColdDamageModifier +8; Modify FireDamageModifier +8; Modify PoisonDamageModifier +8; Modify RangedInit +40; Modify DuckExp +10; Modify DodgeRanged +10; Modify EvadeClsC +10 |
| 227394 | Slip of Intent | FixerDodgeBuffLine (673) | 201 | PsychologicalModification > 1089; SensoryImprovement > 1088; Profession == 4; Level > 200; Expansion op22 2; Specialization op22 8 | ModifyPercentage DodgeRanged +15 |
| 227396 | Slip of Will | FixerDodgeBuffLine (673) | 209 | PsychologicalModification > 1367; SensoryImprovement > 1364; Profession == 4; Level > 208; Expansion op22 2; Specialization op22 8 | ModifyPercentage DodgeRanged +20 |
| 227398 | Slip of Mind | FixerDodgeBuffLine (673) | 216 | PsychologicalModification > 1714; SensoryImprovement > 1710; Profession == 4; Level > 215; Expansion op22 2; Specialization op22 8 | ModifyPercentage DodgeRanged +25 |
| 117223 | Assume Profession: Fixer | FalseProfession (218) | - | VisualProfession == 5; VisualProfession == 4; Profession == 5; PsychologicalModification > 443; SensoryImprovement > 443; BiologicalMetamorphosis > 443 | RemoveNanoStrain[680]; Skill[318,70]; ModifyPercentage PsychologicalModification -15; ModifyPercentage SensoryImprovement -15; ModifyPercentage MaterialMetamorphosis -15; ModifyPercentage MaterialCreation -15; ModifyPercentage SpaceTime -15; ModifyPercentage BiologicalMetamorphosis -15; ChangeVariable[368,4] |
| 266308 | Blindside | NOSTACKING (0) | - | Expansion op22 32; SpaceTime > 799; Profession == 4; SensoryImprovement > 799; Profession == 7 | Modify BiologicalMetamorphosis -750; Modify MaterialMetamorphosis -750; Modify MaterialCreation -750; Modify PsychologicalModification -750; Modify SpaceTime -750; Modify SensoryImprovement -750 |
| 31380 | Blood Makes Noise | PerceptionBuffs (191) | - | PsychologicalModification > 542; SensoryImprovement > 542; VisualProfession == 4 | Modify Perception +240 |
| 274373 | Communication Disruption | NOSTACKING (0) | - | SpaceTime > 19; SensoryImprovement > 19; Profession == 4; Flags op119 0; Flags op4 0; Flags op4 0 | Modify Intelligence -1 |
| 31385 | Cracker's Luck | Break_EntryBuffs (171) | - | PsychologicalModification > 74; SensoryImprovement > 74; VisualProfession == 4 | Modify BreakingEntry +23; Modify TrapDisarm +23 |
| 31387 | Embrace of Shadows | ConcealmentBuff (193) | - | PsychologicalModification > 133; SensoryImprovement > 133; VisualProfession == 4 | Modify Concealment +40 |
| 32039 | False Profession: Fixer | FalseProfession (218) | - | VisualProfession == 5; VisualProfession == 4; Profession == 5; PsychologicalModification > 120; SensoryImprovement > 120; BiologicalMetamorphosis > 120 | RemoveNanoStrain[680]; Skill[318,150]; ModifyPercentage PsychologicalModification -25; ModifyPercentage SensoryImprovement -25; ModifyPercentage MaterialMetamorphosis -25; ModifyPercentage MaterialCreation -25; ModifyPercentage SpaceTime -25; ModifyPercentage BiologicalMetamorphosis -25; ChangeVariable[368,4] |
| 273355 | Improved Frenzy of Shells | FixerSuppressorBuff (253) | - | Profession == 4; PsychologicalModification > 1614; SensoryImprovement > 1614; NanoFocusLevel op22 64 | Modify MGSMG +201; Modify Burst +130; Modify FullAuto +130; Modify MultiRanged +58; Modify ProjectileDamageModifier +18; Modify MeleeDamageModifier +18; Modify EnergyDamageModifier +18; Modify ChemicalDamageModifier +18; Modify RadiationDamageModifier +18; Modify ColdDamageModifier +18; Modify FireDamageModifier +18; Modify PoisonDamageModifier +18; Modify RangedInit +60; Modify DuckExp +20; Modify DodgeRanged +20; Modify EvadeClsC +20 |
| 31390 | Karma Harvest | Break_EntryBuffs (171) | - | PsychologicalModification > 699; SensoryImprovement > 699; VisualProfession == 4 | Modify BreakingEntry +130; Modify TrapDisarm +130 |
| 162496 | Lesser Suppressor | FixerSuppressorBuff (253) | - | Profession == 4; PsychologicalModification > 78; SensoryImprovement > 78 | Modify MGSMG +24; Modify Burst +15 |
| 117212 | Mimic Profession: Fixer | FalseProfession (218) | - | VisualProfession == 5; VisualProfession == 4; Profession == 5; PsychologicalModification > 732; SensoryImprovement > 732; BiologicalMetamorphosis > 732 | RemoveNanoStrain[680]; Skill[318,15]; ModifyPercentage PsychologicalModification -5; ModifyPercentage SensoryImprovement -5; ModifyPercentage MaterialMetamorphosis -5; ModifyPercentage MaterialCreation -5; ModifyPercentage SpaceTime -5; ModifyPercentage BiologicalMetamorphosis -5; ChangeVariable[368,4] |
| 43370 | Minor Suppressor | FixerSuppressorBuff (253) | - | PsychologicalModification > 4; SensoryImprovement > 4; VisualProfession == 4 | Modify MGSMG +8; Modify Burst +7 |
| 31405 | Shijima's Cloak | NOSTACKING (0) | - | PsychologicalModification > 408; SensoryImprovement > 396; Profession == 4 | Modify Concealment +70 |
| 31406 | Stack the Odds | Break_EntryBuffs (171) | - | PsychologicalModification > 335; SensoryImprovement > 335; VisualProfession == 4 | Modify BreakingEntry +79; Modify TrapDisarm +79 |
| 266309 | Unsteady Hands | NOSTACKING (0) | - | Expansion op22 32; MaterialCreation > 799; Profession == 3; SpaceTime > 799; Profession == 4 | Modify Burst -600; Modify MGSMG -600; Modify RangedInit -600 |

## Team Casts (friendly) (19)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 162591 | Grid Runner (Team) | NOSTACKING (0) | 25 | SensoryImprovement > 211; SpaceTime > 154; Profession == 4; Level > 24; ExpansionPlayfield == 0 | TeamCastNano[162584] => 'Grid Runner (Team)' [strain 150 RunspeedBuffs] {Modify RunSpeed +230} |
| 162597 | Hack Grid Vector (Team) | NOSTACKING (0) | 25 | SensoryImprovement > 150; SpaceTime > 111; Profession == 4; Level > 24; ExpansionPlayfield == 0 | TeamCastNano[162583] => 'Hack Grid Vector (Team)' [strain 150 RunspeedBuffs] {Modify RunSpeed +160} |
| 162995 | NCU Compressor | NOSTACKING (0) | 25 | VisualProfession == 4; SpaceTime > 91; SensoryImprovement > 124; MaterialMetamorphosis > 124; Level > 24 | TeamCastNano[162994] => 'NCU Compressor' [strain 257 FixerNCUBuff] {Modify MaxNCU +20} |
| 163079 | Retool NCU | NOSTACKING (0) | 25 | VisualProfession == 4; SpaceTime > 157; SensoryImprovement > 215; MaterialMetamorphosis > 215; Level > 24 | TeamCastNano[163005] => 'Retool NCU' [strain 257 FixerNCUBuff] {Modify MaxNCU +40} |
| 163081 | Jury-rigged NCU Analyzer | NOSTACKING (0) | 50 | VisualProfession == 4; SpaceTime > 236; SensoryImprovement > 330; MaterialMetamorphosis > 330; Level > 49 | TeamCastNano[163006] => 'Jury-rigged NCU Analyzer' [strain 257 FixerNCUBuff] {Modify MaxNCU +60} |
| 162599 | Leech Grid Vector (Team) | NOSTACKING (0) | 50 | SensoryImprovement > 304; SpaceTime > 218; Profession == 4; Level > 49; ExpansionPlayfield == 0 | TeamCastNano[162585] => 'Leech Grid Vector (Team)' [strain 150 RunspeedBuffs] {Modify RunSpeed +280} |
| 163083 | Deck Recoder | NOSTACKING (0) | 75 | VisualProfession == 4; SpaceTime > 335; SensoryImprovement > 472; MaterialMetamorphosis > 472; Level > 74 | TeamCastNano[163008] => 'Deck Recoder' [strain 257 FixerNCUBuff] {Modify MaxNCU +85} |
| 162593 | Grid Surfer (Team) | NOSTACKING (0) | 75 | SensoryImprovement > 402; SpaceTime > 290; Profession == 4; Level > 74; ExpansionPlayfield == 0 | TeamCastNano[162586] => 'Grid Surfer (Team)' [strain 150 RunspeedBuffs] {Modify RunSpeed +360} |
| 162603 | Partial Grid Jump (Team) | NOSTACKING (0) | 100 | SensoryImprovement > 547; SpaceTime > 391; Profession == 4; Level > 99; ExpansionPlayfield == 0 | TeamCastNano[162587] => 'Partial Grid Jump (Team)' [strain 150 RunspeedBuffs] {Modify RunSpeed +420} |
| 163085 | Recompiling Memory Analyzer | NOSTACKING (0) | 125 | Profession == 4; SpaceTime > 434; SensoryImprovement > 604; MaterialMetamorphosis > 604; Level > 124 | TeamCastNano[163010] => 'Recompiling Memory Analyzer' [strain 257 FixerNCUBuff] {Modify MaxNCU +110} |
| 162589 | Grid Phase Accelerator (Team) | NOSTACKING (0) | 145 | SensoryImprovement > 661; SpaceTime > 493; Profession == 4; Level > 144; ExpansionPlayfield == 0 | TeamCastNano[162581] => 'Grid Phase Accelerator (Team)' [strain 150 RunspeedBuffs] {Modify RunSpeed +590} |
| 163087 | QuarkStor NCU Core | NOSTACKING (0) | 145 | Profession == 4; SpaceTime > 488; SensoryImprovement > 657; MaterialMetamorphosis > 657; Level > 144 | TeamCastNano[163012] => 'QuarkStor NCU Core' [strain 257 FixerNCUBuff] {Modify MaxNCU +150} |
| 163094 | Active Viral Compressor | NOSTACKING (0) | 175 | Profession == 4; SpaceTime > 556; SensoryImprovement > 729; MaterialMetamorphosis > 729; Level > 174 | TeamCastNano[163015] => 'Active Viral Compressor' [strain 257 FixerNCUBuff] {Modify MaxNCU +195} |
| 162595 | Gridspace Freedom (Team) | NOSTACKING (0) | 185 | SensoryImprovement > 769; SpaceTime > 582; Profession == 4; Level > 184; ExpansionPlayfield == 0 | TeamCastNano[162588] => 'Gridspace Freedom (Team)' [strain 150 RunspeedBuffs] {Modify RunSpeed +680} |
| 163095 | Sentient Viral Recoder | NOSTACKING (0) | 195 | Profession == 4; SpaceTime > 591; SensoryImprovement > 783; MaterialMetamorphosis > 783; Level > 194 | TeamCastNano[163057] => 'Sentient Viral Recoder' [strain 257 FixerNCUBuff] {Modify MaxNCU +250} |
| 275043 | Firewalled Sync Compressor | NOSTACKING (0) | - | Profession == 4; SpaceTime > 1501; SensoryImprovement > 1614; MaterialMetamorphosis > 1614; NanoFocusLevel op22 64 | CastNano[275135] => 'Firewalled Sync Compressor' [strain 257 FixerNCUBuff] {Modify NanoResist +50; Modify MaxNCU +500; ResistNanoStrain[145,25]; ResistNanoStrain[146,25]; ReduceNanoStrainDuration[145,1000000]; ReduceNanoStrainDuration[146,1000000]}; TeamCastNano[275044] => 'Sync Compressor' [strain 257 FixerNCUBuff] {Modify MaxNCU +500} |
| 142710 | Grid Excursion | NOSTACKING (0) | - | PsychologicalModification > 563; SensoryImprovement > 563; MaterialMetamorphosis > 563; VisualProfession == 4; ExpansionPlayfield == 0 | TeamCastNano[142702] => 'Grid Excursion' [strain 0 NOSTACKING] {} |
| 162601 | Limited Grid Jump (Team) | NOSTACKING (0) | - | SensoryImprovement > 90; SpaceTime > 67; Profession == 4; ExpansionPlayfield == 0 | TeamCastNano[162582] => 'Limited Grid Jump (Team)' [strain 150 RunspeedBuffs] {Modify RunSpeed +110} |
| 142714 | Team Grid Phreak | NOSTACKING (0) | - | PsychologicalModification > 277; SensoryImprovement > 277; MaterialMetamorphosis > 277; VisualProfession == 4; ExpansionPlayfield == 0 | TeamCastNano[142705] => 'Team Grid Phreak' [strain 0 NOSTACKING] {} |

## Debuffs (7)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 260765 | Luck's Capricious Consequence | EvasionDebuffs (197) | 190 | Expansion op22 2; PsychologicalModification > 843; SensoryImprovement > 843; Profession == 4; Level > 189 | Modify DuckExp -170; Modify DodgeRanged -170; Modify EvadeClsC -170; Modify CurrentNCU +25 |
| 31392 | Luck's Fickle Fate | EvasionDebuffs (197) | - | PsychologicalModification > 393; SensoryImprovement > 393; VisualProfession == 4 | Modify DuckExp -85; Modify DodgeRanged -85; Modify EvadeClsC -85; Modify CurrentNCU +15 |
| 273357 | Luck's Improved Capricious Consequence | EvasionDebuffs (197) | - | PsychologicalModification > 1614; SensoryImprovement > 1614; Profession == 4; NanoFocusLevel op22 64 | Modify DuckExp -250; Modify DodgeRanged -250; Modify EvadeClsC -250; Modify CurrentNCU +35 |
| 263259 | Minor NCU Crash | PsychicDebuff (169) | - | Expansion op22 2; SpaceTime > 541; SensoryImprovement > 541; Profession == 4; SelectedTargetType op22 16; Flags op4 0; Flags op101 302405 | RemoveNanoEffects[15,4,3]; CastNano[302405] => 'Recovering NCU' [strain 0 NOSTACKING] {} |
| 263261 | NCU Crash | PsychicDebuff (169) | - | Expansion op22 2; SpaceTime > 541; SensoryImprovement > 541; Profession == 4; SelectedTargetType op22 16; Flags op4 0; Flags op101 302405 | RemoveNanoEffects[15,4,3]; RemoveNanoEffects[15,5,3]; CastNano[302405] => 'Recovering NCU' [strain 0 NOSTACKING] {} |
| 302412 | NCU Vulnerability Exploitation | PsychicDebuff (169) | - | SpaceTime > 1199; SensoryImprovement > 1199; Profession == 4; NanoFocusLevel op22 64; SelectedTargetType op22 16; Flags op4 0; Flags op101 302405 | CastNano[269467] => 'c0mP0s1t3 477r1bu73z b00s7' [strain 0 NOSTACKING] {Modify Strength -20; Modify Agility -20; Modify Stamina -20; Modify Intelligence -20; Modify Sense -20; Modify Psychic -20}; CastNano[269468] => 'c0mP0s1t3 n4n0 3xp3r71z3' [strain 0 NOSTACKING] {Modify SpaceTime -20; Modify MaterialMetamorphosis -20; Modify BiologicalMetamorphosis -20; Modify MaterialCreation -20; Modify SensoryImprovement -20; Modify PsychologicalModification -20}; RemoveNanoEffects[40,2,3]; RemoveNanoEffects[40,3,3]; RemoveNanoEffects[40,4,3]; RemoveNanoEffects[40,5,3]; RemoveNanoEffects[40,2,3]; RemoveNanoEffects[40,4,3]; RemoveNanoEffects[40,3,3]; RemoveNanoEffects[40,5,3]; RemoveNanoEffects[40,2,3]; RemoveNanoEffects[40,4,3]; CastNano[302405] => 'Recovering NCU' [strain 0 NOSTACKING] {} |
| 269465 | Program Override | PsychicDebuff (169) | - | SpaceTime > 869; SensoryImprovement > 869; Profession == 4; Expansion op22 2; SelectedTargetType op22 16; Flags op4 0; Flags op101 302405 | RemoveNanoEffects[35,5,3]; RemoveNanoEffects[35,4,3]; CastNano[269467] => 'c0mP0s1t3 477r1bu73z b00s7' [strain 0 NOSTACKING] {Modify Strength -20; Modify Agility -20; Modify Stamina -20; Modify Intelligence -20; Modify Sense -20; Modify Psychic -20}; CastNano[269468] => 'c0mP0s1t3 n4n0 3xp3r71z3' [strain 0 NOSTACKING] {Modify SpaceTime -20; Modify MaterialMetamorphosis -20; Modify BiologicalMetamorphosis -20; Modify MaterialCreation -20; Modify SensoryImprovement -20; Modify PsychologicalModification -20}; CastNano[302405] => 'Recovering NCU' [strain 0 NOSTACKING] {} |

## Area Casts (AoE) (9)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 85222 | Active Distributed Entanglement | NOSTACKING (0) | - | SensoryImprovement > 183; SpaceTime > 133; VisualProfession == 4 | AreaCastNano[82905,20] => 'Active Distributed Entanglement' [strain 145 Snare] {Modify RunSpeed -430} |
| 85224 | Greater Net Cast Wide | NOSTACKING (0) | - | SensoryImprovement > 598; SpaceTime > 429; VisualProfession == 4 | AreaCastNano[82904,20] => 'Greater Net Cast Wide' [strain 145 Snare] {Modify RunSpeed -1030} |
| 85217 | Invasive Distributed Entanglement | NOSTACKING (0) | - | SensoryImprovement > 326; SpaceTime > 233; VisualProfession == 4 | AreaCastNano[82903,20] => 'Invasive Distributed Entanglement' [strain 145 Snare] {Modify RunSpeed -690} |
| 85218 | Lesser Net Cast Wide | NOSTACKING (0) | - | SensoryImprovement > 120; SpaceTime > 88; VisualProfession == 4 | AreaCastNano[82902,20] => 'Lesser Net Cast Wide' [strain 145 Snare] {Modify RunSpeed -340} |
| 85221 | Mass Gravity Bindings | NOSTACKING (0) | - | SensoryImprovement > 669; SpaceTime > 505; VisualProfession == 4 | AreaCastNano[82901,20] => 'Mass Gravity Bindings' [strain 145 Snare] {Modify RunSpeed -1280} |
| 85220 | Net Cast Wide | NOSTACKING (0) | - | SensoryImprovement > 230; SpaceTime > 167; VisualProfession == 4 | AreaCastNano[82900,20] => 'Net Cast Wide' [strain 145 Snare] {Modify RunSpeed -615} |
| 85219 | Passive Distributed Entanglement | NOSTACKING (0) | - | SensoryImprovement > 47; SpaceTime > 35; VisualProfession == 4 | AreaCastNano[82899,20] => 'Passive Distributed Entanglement' [strain 145 Snare] {Modify RunSpeed -160} |
| 85216 | Spin Nanoweb | NOSTACKING (0) | - | SensoryImprovement > 767; SpaceTime > 580; VisualProfession == 4 | AreaCastNano[82898,20] => 'Spin Nanoweb' [strain 145 Snare] {Modify RunSpeed -1550} |
| 85223 | Spin Weak Nanoweb | NOSTACKING (0) | - | SensoryImprovement > 478; SpaceTime > 339; VisualProfession == 4 | AreaCastNano[82897,20] => 'Spin Weak Nanoweb' [strain 145 Snare] {Modify RunSpeed -900} |

## Root (8)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 56222 | Delay Retreat | Root (146) | - | PsychologicalModification > 299; SpaceTime > 215; VisualProfession == 4 | RestrictAction[4] |
| 56220 | Greater Delay Retreat | Root (146) | - | PsychologicalModification > 679; SpaceTime > 517; VisualProfession == 4 | RestrictAction[4] |
| 56223 | Greater Halt Flight | Root (146) | - | PsychologicalModification > 454; SpaceTime > 325; VisualProfession == 4 | RestrictAction[4] |
| 56219 | Greater Prolong Encounter | Root (146) | - | PsychologicalModification > 753; SpaceTime > 570; VisualProfession == 4 | RestrictAction[4] |
| 56226 | Halt Flight | Root (146) | - | PsychologicalModification > 38; SpaceTime > 29; VisualProfession == 4 | RestrictAction[4] |
| 56224 | Lesser Prolong Encounter | Root (146) | - | PsychologicalModification > 138; SpaceTime > 102; VisualProfession == 4 | RestrictAction[4] |
| 56225 | No Escape Possible | Root (146) | - | PsychologicalModification > 82; SpaceTime > 61; VisualProfession == 4 | RestrictAction[4] |
| 56221 | Prolong Encounter | Root (146) | - | PsychologicalModification > 611; SpaceTime > 439; VisualProfession == 4 | RestrictAction[4] |

## Snare (14)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 217039 | Intense Micro Entanglement | Snare (145) | 25 | SensoryImprovement > 157; SpaceTime > 141; Profession == 4; Expansion op22 2; Level > 24; Specialization op22 1 | CastNano[218742] => 'Intense Micro Entanglement (High)' [strain 145 Snare] {Modify RunSpeed -350}; CastNano[218742] => 'Intense Micro Entanglement (High)' [strain 145 Snare] {Modify RunSpeed -350}; CastNano[218743] => 'Intense Micro Entanglement (Low)' [strain 145 Snare] {Modify RunSpeed -120}; CastNano[218743] => 'Intense Micro Entanglement (Low)' [strain 145 Snare] {Modify RunSpeed -120} |
| 223133 | Intense Nano Net | Snare (145) | 75 | SensoryImprovement > 475; SpaceTime > 409; Profession == 4; Expansion op22 2; Level > 74; Specialization op22 1 | CastNano[218815] => 'Intense Nano Net' [strain 145 Snare] {Modify RunSpeed -770}; CastNano[218815] => 'Intense Nano Net' [strain 145 Snare] {Modify RunSpeed -770}; CastNano[218814] => 'Intense Nano Net' [strain 145 Snare] {Modify RunSpeed -270}; CastNano[218814] => 'Intense Nano Net' [strain 145 Snare] {Modify RunSpeed -270} |
| 223135 | Intense Personal Entanglement | Snare (145) | 125 | SensoryImprovement > 808; SpaceTime > 688; Profession == 4; Expansion op22 2; Level > 124; Specialization op22 2 | CastNano[218816] => 'Intense Personal Entanglement' [strain 145 Snare] {Modify RunSpeed -900}; CastNano[218816] => 'Intense Personal Entanglement' [strain 145 Snare] {Modify RunSpeed -900}; CastNano[218817] => 'Intense Personal Entanglement' [strain 145 Snare] {Modify RunSpeed -280}; CastNano[218817] => 'Intense Personal Entanglement' [strain 145 Snare] {Modify RunSpeed -280} |
| 223137 | Intense Gravity Bindings | Snare (145) | 145 | SensoryImprovement > 896; SpaceTime > 782; Profession == 4; Expansion op22 2; Level > 144; Specialization op22 2 | CastNano[218819] => 'Intense Gravity Bindings' [strain 145 Snare] {Modify RunSpeed -1200}; CastNano[218819] => 'Intense Gravity Bindings' [strain 145 Snare] {Modify RunSpeed -1200}; CastNano[218818] => 'Intense Gravity Bindings' [strain 145 Snare] {Modify RunSpeed -280}; CastNano[218818] => 'Intense Gravity Bindings' [strain 145 Snare] {Modify RunSpeed -280} |
| 223139 | Intense Targetted Nanoweb | Snare (145) | 195 | SensoryImprovement > 1051; SpaceTime > 926; Profession == 4; Expansion op22 2; Level > 194; Specialization op22 4 | CastNano[218876] => 'Intense Targetted Nanoweb' [strain 145 Snare] {Modify RunSpeed -1400}; CastNano[218876] => 'Intense Targetted Nanoweb' [strain 145 Snare] {Modify RunSpeed -1400}; CastNano[218875] => 'Intense Targetted Nanoweb' [strain 145 Snare] {Modify RunSpeed -620}; CastNano[218875] => 'Intense Targetted Nanoweb' [strain 145 Snare] {Modify RunSpeed -620} |
| 223141 | Intense Nano Bindings | Snare (145) | 207 | SensoryImprovement > 1296; SpaceTime > 1101; Profession == 4; Expansion op22 2; Level > 206; Specialization op22 8 | CastNano[218878] => 'Intense Nano Bindings' [strain 145 Snare] {Modify RunSpeed -1700}; CastNano[218878] => 'Intense Nano Bindings' [strain 145 Snare] {Modify RunSpeed -1700}; CastNano[218877] => 'Intense Nano Bindings' [strain 145 Snare] {Modify RunSpeed -620}; CastNano[218877] => 'Intense Nano Bindings' [strain 145 Snare] {Modify RunSpeed -620} |
| 223143 | Intense Agglutinative Nanoweb | Snare (145) | 214 | SensoryImprovement > 1537; SpaceTime > 1272; Profession == 4; Expansion op22 2; Level > 213; Specialization op22 8 | CastNano[218880] => 'Intense Agglutinative Nanoweb' [strain 145 Snare] {Modify RunSpeed -2000}; CastNano[218880] => 'Intense Agglutinative Nanoweb' [strain 145 Snare] {Modify RunSpeed -2000}; CastNano[218879] => 'Intense Agglutinative Nanoweb' [strain 145 Snare] {Modify RunSpeed -800}; CastNano[218879] => 'Intense Agglutinative Nanoweb' [strain 145 Snare] {Modify RunSpeed -800} |
| 82513 | Active Micro Entanglement | Snare (145) | - | SensoryImprovement > 111; SpaceTime > 82; VisualProfession == 4 | Modify RunSpeed -320 |
| 82502 | Gravity Bindings | Snare (145) | - | SensoryImprovement > 634; SpaceTime > 461; VisualProfession == 4 | Modify RunSpeed -1080 |
| 82504 | Greater Nano Net | Snare (145) | - | SensoryImprovement > 512; SpaceTime > 365; VisualProfession == 4 | Modify RunSpeed -920 |
| 82507 | Invasive Micro Entanglement | Snare (145) | - | SensoryImprovement > 364; SpaceTime > 260; VisualProfession == 4 | Modify RunSpeed -750 |
| 82515 | Lesser Nano Net | Snare (145) | - | SensoryImprovement > 60; SpaceTime > 45; VisualProfession == 4 | Modify RunSpeed -180 |
| 82510 | Nano Net | Snare (145) | - | SensoryImprovement > 199; SpaceTime > 145; VisualProfession == 4 | Modify RunSpeed -480 |
| 82518 | Passive Micro Entanglement | Snare (145) | - | SensoryImprovement > 30; SpaceTime > 23; VisualProfession == 4 | Modify RunSpeed -100 |

## Root / Snare Breakers (15)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 203597 | Burst Bonds | NOSTACKING (0) | 50 | Profession == 4; SensoryImprovement > 283; SpaceTime > 241; Level > 49 | ReduceNanoStrainDuration[145,596]; SystemText[Your movement is less restricted.] |
| 203978 | Burst Bonds (Other) | NOSTACKING (0) | 50 | Profession == 4; SensoryImprovement > 346; SpaceTime > 298; Level > 49 | ReduceNanoStrainDuration[145,515]; SystemText[Your movement is less restricted.] |
| 203811 | Escape Captivation | NOSTACKING (0) | 50 | Profession == 4; PsychologicalModification > 408; SpaceTime > 353; Level > 49 | ReduceNanoStrainDuration[146,45]; SystemText[Your movement is less restricted.] |
| 203595 | Shatter Bonds | NOSTACKING (0) | 75 | Profession == 4; SensoryImprovement > 508; SpaceTime > 434; Level > 74 | ReduceNanoStrainDuration[145,814]; SystemText[Your movement is less restricted.] |
| 203980 | Shatter Bonds (Other) | NOSTACKING (0) | 100 | Profession == 4; SensoryImprovement > 617; SpaceTime > 521; Level > 99 | ReduceNanoStrainDuration[145,736]; SystemText[Your movement is less restricted.] |
| 203599 | Rend Bonds | NOSTACKING (0) | 125 | Profession == 4; SensoryImprovement > 756; SpaceTime > 643; Level > 124 | ReduceNanoStrainDuration[145,1161]; SystemText[Your movement is less restricted.] |
| 203982 | Rend Bonds (Other) | NOSTACKING (0) | 145 | Profession == 4; SensoryImprovement > 812; SpaceTime > 707; Level > 144 | ReduceNanoStrainDuration[145,1052]; SystemText[Your movement is less restricted.] |
| 203601 | Bargain with Fate | NOSTACKING (0) | 165 | Profession == 4; SensoryImprovement > 866; SpaceTime > 763; Level > 164 | ReduceNanoStrainDuration[145,1466]; SystemText[Your movement is less restricted.] |
| 203813 | Master Escapologist | NOSTACKING (0) | 165 | Profession == 4; PsychologicalModification > 870; SpaceTime > 766; Level > 164 | ReduceNanoStrainDuration[146,122]; SystemText[Your movement is less restricted.] |
| 203984 | Fortune's Smile | NOSTACKING (0) | 195 | Profession == 4; SensoryImprovement > 954; SpaceTime > 840; Level > 194 | ReduceNanoStrainDuration[145,1353]; SystemText[Your movement is less restricted.] |
| 203603 | Luck's Lost Twin | NOSTACKING (0) | 195 | Profession == 4; SensoryImprovement > 959; SpaceTime > 845; Level > 194 | ReduceNanoStrainDuration[145,1702]; SystemText[Your movement is less restricted.] |
| 275680 | Refactor NCU Matrix | DOTRemoval (825) | - | MaterialMetamorphosis > 1754; BiologicalMetamorphosis > 1429; SpaceTime > 1419; Profession == 4; NanoFocusLevel op22 64 | ReduceNanoStrainDuration[6,1000000]; ReduceNanoStrainDuration[7,1000000]; ReduceNanoStrainDuration[8,1000000]; ReduceNanoStrainDuration[9,1000000]; ReduceNanoStrainDuration[10,1000000]; ReduceNanoStrainDuration[145,1000000]; ReduceNanoStrainDuration[146,1000000]; ReduceNanoStrainDuration[186,1000000]; ReduceNanoStrainDuration[887,1000000]; ReduceNanoStrainDuration[856,1000000]; ReduceNanoStrainDuration[883,1000000]; ReduceNanoStrainDuration[147,1000000]; ReduceNanoStrainDuration[582,1000000] |
| 203593 | Scatter Bonds | NOSTACKING (0) | - | Profession == 4; SensoryImprovement > 61; SpaceTime > 57 | ReduceNanoStrainDuration[145,337]; SystemText[Your movement is less restricted.] |
| 203976 | Scatter Bonds (Other) | NOSTACKING (0) | - | Profession == 4; SensoryImprovement > 118; SpaceTime > 106 | ReduceNanoStrainDuration[145,318]; SystemText[Your movement is less restricted.] |
| 279374 | Wake Up Call | DOTRemoval (825) | - | VisualProfession == 4; SensoryImprovement > 119; SpaceTime > 119 | ReduceNanoStrainDuration[6,1000000]; ReduceNanoStrainDuration[7,1000000]; ReduceNanoStrainDuration[9,1000000]; ReduceNanoStrainDuration[8,1000000]; ReduceNanoStrainDuration[10,1000000]; ReduceNanoStrainDuration[186,1000000]; ReduceNanoStrainDuration[883,1000000]; ReduceNanoStrainDuration[147,1000000]; ReduceNanoStrainDuration[887,1000000]; ReduceNanoStrainDuration[856,1000000]; ReduceNanoStrainDuration[960,1000000] |

## Heals / HoT (28)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 162718 | Hacked Diagnosis | FixerLongHoT (255) | 25 | MaterialMetamorphosis > 203; BiologicalMetamorphosis > 148; SpaceTime > 148; Profession == 4; Level > 24 | Hit[27,58,64,0] |
| 162716 | Rebinding Sutures | FixerLongHoT (255) | 25 | MaterialMetamorphosis > 133; BiologicalMetamorphosis > 98; SpaceTime > 98; Profession == 4; Level > 24 | Hit[27,35,41,0] |
| 162720 | Kitchen-sink Surgery | FixerLongHoT (255) | 50 | MaterialMetamorphosis > 293; BiologicalMetamorphosis > 211; SpaceTime > 211; Profession == 4; Level > 49 | Hit[27,87,92,0] |
| 162722 | Blackmarket Prescription | FixerLongHoT (255) | 75 | MaterialMetamorphosis > 378; BiologicalMetamorphosis > 271; SpaceTime > 271; Profession == 4; Level > 74 | Hit[27,111,119,0] |
| 162724 | Biosign Rejuvenator | FixerLongHoT (255) | 100 | MaterialMetamorphosis > 487; BiologicalMetamorphosis > 346; SpaceTime > 346; Profession == 4; Level > 99 | Hit[27,154,162,0] |
| 162726 | Nano Cauterization | FixerLongHoT (255) | 100 | MaterialMetamorphosis > 573; BiologicalMetamorphosis > 409; SpaceTime > 409; Profession == 4; Level > 99 | Hit[27,194,201,0] |
| 162728 | Systolic Equalizer | FixerLongHoT (255) | 135 | MaterialMetamorphosis > 641; BiologicalMetamorphosis > 469; SpaceTime > 469; Profession == 4; Level > 134 | Hit[27,235,243,0] |
| 162730 | Backyard Revitalization | FixerLongHoT (255) | 155 | MaterialMetamorphosis > 682; BiologicalMetamorphosis > 519; SpaceTime > 519; Profession == 4; Level > 154 | Hit[27,276,283,0] |
| 162732 | Cellular Crashcart | FixerLongHoT (255) | 175 | MaterialMetamorphosis > 742; BiologicalMetamorphosis > 563; SpaceTime > 563; Profession == 4; Level > 174 | Hit[27,326,335,0] |
| 162734 | Dr Hack 'n Quack | FixerLongHoT (255) | 195 | MaterialMetamorphosis > 778; BiologicalMetamorphosis > 588; SpaceTime > 588; Profession == 4; Level > 194 | Hit[27,362,370,0] |
| 252046 | Lasting Life | FixerLongHoT (255) | 205 | MaterialMetamorphosis > 1229; BiologicalMetamorphosis > 1054; SpaceTime > 1051; Profession == 4; Level > 204; Expansion op22 2; Specialization op22 8; Level > 194 | Hit[27,406,439,0] |
| 252048 | Life Bond | FixerLongHoT (255) | 208 | MaterialMetamorphosis > 1334; BiologicalMetamorphosis > 1129; SpaceTime > 1125; Profession == 4; Level > 207; Expansion op22 2; Specialization op22 8; Level > 201 | Hit[27,434,471,0] |
| 252050 | Lasting Ultimatum | FixerLongHoT (255) | 213 | MaterialMetamorphosis > 1509; BiologicalMetamorphosis > 1254; SpaceTime > 1247; Profession == 4; Level > 212; Expansion op22 2; Specialization op22 8; Level > 204 | Hit[27,466,502,0] |
| 85228 | Advanced Insurance Hack | HealOverTime (12) | - | BiologicalMetamorphosis > 376; SpaceTime > 376; MaterialMetamorphosis > 527; VisualProfession == 4 | Hit[27,64,139,0] |
| 270352 | Advanced Medical Claim | HealOverTime (12) | - | BiologicalMetamorphosis > 604; MaterialMetamorphosis > 808; SpaceTime > 604; Profession == 4 | Hit[27,153,345,0] |
| 85225 | Advanced Policy Skim | HealOverTime (12) | - | BiologicalMetamorphosis > 542; SpaceTime > 542; MaterialMetamorphosis > 708; VisualProfession == 4 | Hit[27,104,244,0] |
| 85215 | Basic Insurance Hack | HealOverTime (12) | - | BiologicalMetamorphosis > 20; SpaceTime > 20; MaterialMetamorphosis > 25; VisualProfession == 4 | Hit[27,2,6,0] |
| 85231 | Basic Policy Skim | HealOverTime (12) | - | BiologicalMetamorphosis > 246; SpaceTime > 246; MaterialMetamorphosis > 344; VisualProfession == 4 | Hit[27,38,82,0] |
| 85230 | Detailed Medical Claim | HealOverTime (12) | - | BiologicalMetamorphosis > 275; SpaceTime > 275; MaterialMetamorphosis > 383; VisualProfession == 4 | Hit[27,43,92,0] |
| 85233 | Falsify Medical Records | HealOverTime (12) | - | BiologicalMetamorphosis > 125; SpaceTime > 125; MaterialMetamorphosis > 170; VisualProfession == 4 | Hit[27,17,39,0] |
| 85227 | Flawless Medical Claim | HealOverTime (12) | - | BiologicalMetamorphosis > 491; SpaceTime > 491; MaterialMetamorphosis > 660; VisualProfession == 4 | Hit[27,95,200,0] |
| 275679 | Greater Preservation Matrix | FixerLongHoT (255) | - | MaterialMetamorphosis > 1754; BiologicalMetamorphosis > 1429; SpaceTime > 1419; Profession == 4; NanoFocusLevel op22 64 | Hit[27,6,0,0] |
| 85232 | Insurance Hack | HealOverTime (12) | - | BiologicalMetamorphosis > 181; SpaceTime > 181; MaterialMetamorphosis > 251; VisualProfession == 4 | Hit[27,26,63,0] |
| 85234 | Medical Claim | HealOverTime (12) | - | BiologicalMetamorphosis > 70; SpaceTime > 70; MaterialMetamorphosis > 95; VisualProfession == 4 | Hit[27,10,21,0] |
| 85226 | Omni-Med Incursion | HealOverTime (12) | - | BiologicalMetamorphosis > 573; MaterialMetamorphosis > 758; SpaceTime > 573; VisualProfession == 4 | Hit[27,122,276,0] |
| 85229 | Policy Skim | HealOverTime (12) | - | BiologicalMetamorphosis > 443; SpaceTime > 443; MaterialMetamorphosis > 617; VisualProfession == 4 | Hit[27,83,168,0] |
| 162714 | Relieving Salve | FixerLongHoT (255) | - | MaterialMetamorphosis > 56; BiologicalMetamorphosis > 42; SpaceTime > 42; Profession == 4 | Hit[27,15,18,0] |
| 273352 | Superior Insurance Hack | HealOverTime (12) | - | MaterialMetamorphosis > 1754; BiologicalMetamorphosis > 1429; SpaceTime > 1419; Profession == 4; NanoFocusLevel op22 64 | TeamCastNano[273353] => 'Superior Insurance Hack' [strain 12 HealOverTime] {Hit[27,355,545,0]} |

## Travel / Summon Utility (7)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 301861 | Expeditious Evacuation | EmergencyGrid (1032) | 205 | PsychologicalModification > 939; SensoryImprovement > 939; MaterialMetamorphosis > 939; Profession == 4; ExpansionPlayfield == 0; Flags op124 2; PlayfieldType op107 2; Level > 204 | Set[27,1]; Set[214,1]; TeamCastNano[301862] => 'Expeditious Evacuation' [strain 1032 EmergencyGrid] {} |
| 301859 | Vanish into the Digital Void | EmergencyGrid (1032) | 205 | PsychologicalModification > 824; SensoryImprovement > 824; MaterialMetamorphosis > 824; Profession == 4; ExpansionPlayfield == 0; Flags op124 2; PlayfieldType op107 2; Level > 204 | Set[27,1]; Set[214,1] |
| 31388 | Grid Phreak | SelfGrid (1030) | - | PsychologicalModification > 179; SensoryImprovement > 179; VisualProfession == 4; MaterialMetamorphosis > 179; ExpansionPlayfield == 0; PlayfieldType op107 2 | (1 non-effect functions only) |
| 160982 | Hack Grid Data Stream (Team) | FixerGrid (1039) | - | Profession == 4; SensoryImprovement > 478; SpaceTime > 339; BreakingEntry > 400 | TeamCastNano[160981] => 'Hack Grid Data Stream' [strain 0 NOSTACKING] {SystemText[You were unable to receive a Data Receptacle because your inventory is full.]; SpawnItem[FIGI,1,0]} |
| 142712 | Instantaneous Encoding | EmergencyGrid (1032) | - | PsychologicalModification > 339; SensoryImprovement > 339; MaterialMetamorphosis > 339; VisualProfession == 4; ExpansionPlayfield == 0; Flags op124 2; PlayfieldType op107 2 | Set[27,1]; Set[214,1]; TeamCastNano[142703] => 'Instantaneous Encoding' [strain 1032 EmergencyGrid] {} |
| 142707 | Re-Matrix Grid Vector | TeamGrid (1031) | - | PsychologicalModification > 486; SensoryImprovement > 486; MaterialMetamorphosis > 486; VisualProfession == 4; ExpansionPlayfield == 0; PlayfieldType op107 2 | TeamCastNano[142704] => 'Re-Matrix Grid Vector' [strain 1031 TeamGrid] {} |
| 142713 | Reckless Digitization | EmergencyGrid (1032) | - | PsychologicalModification > 225; SensoryImprovement > 225; MaterialMetamorphosis > 225; VisualProfession == 4; ExpansionPlayfield == 0; Flags op124 2; PlayfieldType op107 2 | Set[27,1]; Set[214,1] |

## Misc / Utility (7)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 234073 | Tap Notum Vein: Nascence | NOSTACKING (0) | 25 | Flags op33 226824; Flags op3 0; GOS > -50001; Profession == 4; PsychologicalModification > 151; SensoryImprovement > 151; MaterialMetamorphosis > 151; ExpansionPlayfield == 1; Level > 24; Expansion op22 2 | Teleport[297,79,208,4676]; Teleport[625,18,308,4677] |
| 234082 | Tap Notum Vein: Elysium | NOSTACKING (0) | 75 | Flags op33 226987; Flags op3 0; GOS > -1; Profession == 4; PsychologicalModification > 495; SensoryImprovement > 495; MaterialMetamorphosis > 495; ExpansionPlayfield == 1; Level > 74; Expansion op22 2 | Teleport[297,79,208,4678]; Teleport[625,18,308,4680] |
| 279376 | Experienced Survivor | FixerFearImmunity (901) | 100 | VisualProfession == 4; Level > 99; SensoryImprovement > 119; SpaceTime > 119 | ResistNanoStrain[883,1000] |
| 234084 | Tap Notum Vein: Scheol | NOSTACKING (0) | 100 | Flags op33 226991; Flags op3 0; GOS > 999; Profession == 4; PsychologicalModification > 766; SensoryImprovement > 766; MaterialMetamorphosis > 766; ExpansionPlayfield == 1; Level > 99; Expansion op22 2 | Teleport[297,79,208,4682]; Teleport[625,18,308,4683] |
| 234075 | Tap Notum Vein: Adonis | NOSTACKING (0) | 145 | Flags op33 226989; Flags op3 0; GOS > 4999; Profession == 4; PsychologicalModification > 871; SensoryImprovement > 871; MaterialMetamorphosis > 871; ExpansionPlayfield == 1; Level > 144; Expansion op22 2 | Teleport[297,79,208,4684]; Teleport[625,18,308,4686] |
| 234077 | Tap Notum Vein: Penumbra | NOSTACKING (0) | 165 | Flags op33 226985; Flags op3 0; GOS > 9999; Profession == 4; PsychologicalModification > 939; SensoryImprovement > 939; MaterialMetamorphosis > 939; ExpansionPlayfield == 1; Level > 164; Expansion op22 2 | Teleport[297,79,208,4688]; Teleport[625,18,308,4690] |
| 234079 | Tap Notum Vein: Inferno | NOSTACKING (0) | 195 | Flags op33 226988; Flags op3 0; GOS > 17999; Profession == 4; PsychologicalModification > 1054; SensoryImprovement > 1054; MaterialMetamorphosis > 1054; ExpansionPlayfield == 1; Level > 194; Expansion op22 2 | Teleport[297,79,208,4692]; Teleport[625,18,308,4694] |

## Damage Buffs (17)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 81882 | Advanced Augmentation Cloud | DamageBuffs_LineA (4) | - | SensoryImprovement > 653; MaterialMetamorphosis > 653; VisualProfession == 4 | Skill[278,21]; Skill[279,21]; Skill[280,21]; Skill[281,21]; Skill[282,21]; Skill[311,21]; Skill[316,21]; Skill[317,21] |
| 81884 | Augmentation Cloud | DamageBuffs_LineA (4) | - | SensoryImprovement > 492; MaterialMetamorphosis > 492; VisualProfession == 4 | Skill[278,17]; Skill[279,17]; Skill[280,17]; Skill[281,17]; Skill[282,17]; Skill[311,17]; Skill[316,17]; Skill[317,17] |
| 31384 | Contact Poison | DamageBuffs_LineA (4) | - | MaterialMetamorphosis > 129; SensoryImprovement > 129; VisualProfession == 4 | Skill[278,7]; Skill[279,7]; Skill[317,7] |
| 81883 | Greater Nano Boost | DamageBuffs_LineA (4) | - | SensoryImprovement > 623; MaterialMetamorphosis > 623; VisualProfession == 4 | Skill[278,20]; Skill[279,20]; Skill[280,20]; Skill[281,20]; Skill[282,20]; Skill[311,20]; Skill[316,20]; Skill[317,20] |
| 81886 | Hasty Augmentation Cloud | DamageBuffs_LineA (4) | - | SensoryImprovement > 207; MaterialMetamorphosis > 207; VisualProfession == 4 | Skill[278,10]; Skill[279,10]; Skill[280,10]; Skill[281,10]; Skill[282,10]; Skill[311,10]; Skill[316,10]; Skill[317,10] |
| 222835 | Improved Augmentation Cloud | DamageBuffs_LineA (4) | - | Profession == 4; Profession == 1; Profession == 5; SensoryImprovement > 483; MaterialMetamorphosis > 483 | Modify ProjectileDamageModifier +20; Modify MeleeDamageModifier +20; Modify EnergyDamageModifier +20; Modify ChemicalDamageModifier +20; Modify RadiationDamageModifier +20; Modify ColdDamageModifier +20; Modify FireDamageModifier +20; Modify PoisonDamageModifier +20 |
| 222833 | Improved Hasty Augmentation Cloud | DamageBuffs_LineA (4) | - | Profession == 4; Profession == 1; Profession == 5; SensoryImprovement > 207; MaterialMetamorphosis > 207 | Modify ProjectileDamageModifier +15; Modify MeleeDamageModifier +15; Modify EnergyDamageModifier +15; Modify ChemicalDamageModifier +15; Modify RadiationDamageModifier +15; Modify ColdDamageModifier +15; Modify FireDamageModifier +15; Modify PoisonDamageModifier +15 |
| 222837 | Improved Neural Interfaced Augmentation Cloud | DamageBuffs_LineA (4) | - | Profession == 4; Profession == 1; Profession == 5; SensoryImprovement > 683; MaterialMetamorphosis > 683 | Modify ProjectileDamageModifier +30; Modify MeleeDamageModifier +30; Modify EnergyDamageModifier +30; Modify ChemicalDamageModifier +30; Modify RadiationDamageModifier +30; Modify ColdDamageModifier +30; Modify FireDamageModifier +30; Modify PoisonDamageModifier +30 |
| 222838 | Improved Semi-Sentient Augmentation Cloud | DamageBuffs_LineA (4) | - | Profession == 4; Profession == 1; Profession == 5; SensoryImprovement > 783; MaterialMetamorphosis > 783 | Modify ProjectileDamageModifier +40; Modify MeleeDamageModifier +40; Modify EnergyDamageModifier +40; Modify ChemicalDamageModifier +40; Modify RadiationDamageModifier +40; Modify ColdDamageModifier +40; Modify FireDamageModifier +40; Modify PoisonDamageModifier +40 |
| 81887 | Lesser Nano Boost | DamageBuffs_LineA (4) | - | SensoryImprovement > 56; MaterialMetamorphosis > 56; VisualProfession == 4 | Skill[278,5]; Skill[279,5]; Skill[280,5]; Skill[281,5]; Skill[282,5]; Skill[311,5]; Skill[316,5]; Skill[317,5] |
| 31448 | Lifebane Modification | DamageBuffs_LineA (4) | - | MaterialMetamorphosis > 422; SensoryImprovement > 422; VisualProfession == 4 | Skill[278,16]; Skill[279,16]; Skill[317,16] |
| 81885 | Nano Boost | DamageBuffs_LineA (4) | - | SensoryImprovement > 374; MaterialMetamorphosis > 374; VisualProfession == 4 | Skill[278,15]; Skill[279,15]; Skill[280,15]; Skill[281,15]; Skill[282,15]; Skill[311,15]; Skill[316,15]; Skill[317,15] |
| 81880 | Neural Interfaced Augmentation Cloud | DamageBuffs_LineA (4) | - | SensoryImprovement > 732; MaterialMetamorphosis > 732; VisualProfession == 4 | Skill[278,25]; Skill[279,25]; Skill[280,25]; Skill[281,25]; Skill[282,25]; Skill[311,25]; Skill[316,25]; Skill[317,25] |
| 31401 | Poison Modification | DamageBuffs_LineA (4) | - | MaterialMetamorphosis > 21; SensoryImprovement > 21; VisualProfession == 4 | Skill[278,2]; Skill[279,2]; Skill[317,2] |
| 81879 | Semi-Sentient Augmentation Cloud | DamageBuffs_LineA (4) | - | SensoryImprovement > 783; MaterialMetamorphosis > 783; VisualProfession == 4 | Skill[278,27]; Skill[279,27]; Skill[280,27]; Skill[281,27]; Skill[282,27]; Skill[311,27]; Skill[316,27]; Skill[317,27] |
| 81881 | Targeted Augmentation Cloud | DamageBuffs_LineA (4) | - | SensoryImprovement > 688; MaterialMetamorphosis > 688; VisualProfession == 4 | Skill[278,23]; Skill[279,23]; Skill[280,23]; Skill[281,23]; Skill[282,23]; Skill[311,23]; Skill[316,23]; Skill[317,23] |
| 31411 | Venom Modification | DamageBuffs_LineA (4) | - | MaterialMetamorphosis > 288; SensoryImprovement > 288; VisualProfession == 4 | Skill[278,13]; Skill[279,13]; Skill[317,13] |

## Evade Buffs (13)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 223127 | Shadow Step | MajorEvasionBuffs (144) | 50 | SensoryImprovement > 291; SpaceTime > 247; VisualProfession == 4; Level > 49; Specialization op22 1; Expansion op22 2 | CastNano[272412] => 'Shadow Step' [strain 150 RunspeedBuffs] {TeamCastNano[215717]; TeamCastNano[215717]; Modify RunSpeed +250}; Modify DuckExp +36; Modify DodgeRanged +36; Modify EvadeClsC +36 |
| 215718 | Shadow Trail | MajorEvasionBuffs (144) | 100 | SensoryImprovement > 766; SpaceTime > 650; VisualProfession == 4; Level > 99; Specialization op22 1; Expansion op22 2 | CastNano[272413] => 'Shadow Trail' [strain 150 RunspeedBuffs] {TeamCastNano[215719]; TeamCastNano[215719]; Modify RunSpeed +400}; Modify DuckExp +78; Modify DodgeRanged +78; Modify EvadeClsC +78 |
| 223129 | Path of Shadow | MajorEvasionBuffs (144) | 165 | SensoryImprovement > 979; SpaceTime > 860; Profession == 4; Level > 164; Specialization op22 4; Expansion op22 2 | CastNano[272414] => 'Path of Shadow' [strain 150 RunspeedBuffs] {TeamCastNano[215720]; TeamCastNano[215720]; Modify RunSpeed +640}; Modify DuckExp +119; Modify DodgeRanged +119; Modify EvadeClsC +119 |
| 223125 | Blessed By Shadow | MajorEvasionBuffs (144) | 205 | SensoryImprovement > 1502; SpaceTime > 1247; Profession == 4; Level > 204; Specialization op22 8; Expansion op22 2 | CastNano[272416] => 'Blessed By Shadow' [strain 150 RunspeedBuffs] {TeamCastNano[215722]; TeamCastNano[215722]; Modify RunSpeed +820}; Modify DuckExp +150; Modify DodgeRanged +150; Modify EvadeClsC +150 |
| 223131 | Touched With Shadow | MajorEvasionBuffs (144) | 205 | SensoryImprovement > 1226; SpaceTime > 1051; Profession == 4; Level > 204; Specialization op22 8; Expansion op22 2 | CastNano[272415] => 'Touched With Shadow' [strain 150 RunspeedBuffs] {TeamCastNano[215721]; TeamCastNano[215721]; Modify RunSpeed +725}; Modify EvadeClsC +128; Modify DuckExp +128; Modify DodgeRanged +128 |
| 93126 | Grid Phase Accelerator | MajorEvasionBuffs (144) | - | SensoryImprovement > 665; SpaceTime > 499; VisualProfession == 4; ExpansionPlayfield == 0 | Modify DuckExp +70; Modify DodgeRanged +70; Modify EvadeClsC +70; Modify RunSpeed +620 |
| 93130 | Grid Runner | MajorEvasionBuffs (144) | - | SensoryImprovement > 220; SpaceTime > 160; VisualProfession == 4; ExpansionPlayfield == 0 | Modify DuckExp +16; Modify DodgeRanged +16; Modify EvadeClsC +16; Modify RunSpeed +260 |
| 93128 | Grid Surfer | MajorEvasionBuffs (144) | - | SensoryImprovement > 412; SpaceTime > 297; VisualProfession == 4; ExpansionPlayfield == 0 | Modify DuckExp +37; Modify DodgeRanged +37; Modify EvadeClsC +37; Modify RunSpeed +390 |
| 93132 | Gridspace Freedom | MajorEvasionBuffs (144) | - | SensoryImprovement > 774; SpaceTime > 586; VisualProfession == 4; ExpansionPlayfield == 0 | Modify DuckExp +79; Modify DodgeRanged +79; Modify EvadeClsC +79; Modify RunSpeed +720 |
| 93131 | Hack Grid Vector | MajorEvasionBuffs (144) | - | SensoryImprovement > 158; SpaceTime > 117; VisualProfession == 4; ExpansionPlayfield == 0 | Modify DuckExp +7; Modify DodgeRanged +7; Modify EvadeClsC +7; Modify RunSpeed +190 |
| 93129 | Leech Grid Vector | MajorEvasionBuffs (144) | - | SensoryImprovement > 315; SpaceTime > 225; VisualProfession == 4; ExpansionPlayfield == 0 | Modify DuckExp +25; Modify DodgeRanged +25; Modify EvadeClsC +25; Modify RunSpeed +310 |
| 93125 | Limited Grid Jump | MajorEvasionBuffs (144) | - | SensoryImprovement > 99; SpaceTime > 73; VisualProfession == 4; ExpansionPlayfield == 0 | Modify DuckExp +2; Modify DodgeRanged +2; Modify EvadeClsC +2; Modify RunSpeed +140 |
| 93127 | Partial Grid Jump | MajorEvasionBuffs (144) | - | SensoryImprovement > 558; SpaceTime > 398; VisualProfession == 4; ExpansionPlayfield == 0 | Modify DuckExp +61; Modify DodgeRanged +61; Modify EvadeClsC +61; Modify RunSpeed +450 |

## Weapon & Special Attack Buffs (1)

| id | name | nano line (strain) | minLvl | cast reqs | effect |
| ---: | --- | --- | ---: | --- | --- |
| 31409 | Back Pain | SneakAttackBuffs (208) | - | PsychologicalModification > 308; SensoryImprovement > 308; VisualProfession == 4 | Modify SneakAttack +70 |

