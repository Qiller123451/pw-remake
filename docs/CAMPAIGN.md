# The campaign layer (for developers)

How a mission folder becomes a running world with its trigger engine: `src/Sim/Campaign` (namespace
`PwRemake.Sim.Campaign`), `src/Sim/Systems/Scripting*.cs` (the World's scripting methods) and `src/Sim/Game/Cheats.cs`.
It is the port of the browser remake's `game/campaign/setup.js`, `registry.js`, `regions.js`, `tribes.js`,
`sequence.js`, `game/scripting.js`, `game/cheats.js` (apply) and the simulation part of `main.js` (`buildWorld`,
`mapSource`, `mapRegions`, `createBrains`) - pw-exporter 98c7703. The spec of this layer is the toolkit's
`remake/docs/CAMPAIGN_RUNTIME.md`; the trigger engine it feeds is docs/TRIGGERS.md, the mission folder docs/LUA_MAPS.md.
(`game/campaign/props.js` is an old copy of `game/prop.js`, which the core already has: `World/Prop.cs`.)

## 1. Files

| file | content | ported from |
|---|---|---|
| `MissionData.cs` | `MissionData` (map.lua's mission + world.lua + the TriggerDefs of LuaMission = the JS's one pw-campaign/1 document), `MissionEntry` (index.json) | setup.js loadCampaign |
| `Campaign.cs` | `Campaign`: plan, build (players, filters, records, buildings, units, props, groups, question marks, start locations, free mains), per-step update (world events -> registry and engine), the mission-level calls (`Spawn`, `Remove`, `SetOwner`/`Convert`, `Teleport`, `SetAppearance`, `PlayAnim`, `SetQuestionMark`, `PlaceFreeMain`, `Next`) | setup.js |
| `Registry.cs` | `Rec`, `Group`, `Registry` (lookups, groups, `Query`, `Select` = the query block of triggers.md §2.1, `Matches` §2.2) | registry.js |
| `Regions.cs` | `Region`, `WorldRegion`, `Regions` | regions.js |
| `Tribes.cs` | `TribeSwap` (role table, closeness profiles, `Cls`, `StandsFor`, `Missing`, `Filter`, `Filters`) | tribes.js |
| `SeqActors.cs` | the actors of a cutscene: binding, pose at time t, the end | sequence.js |
| `MissionHost.cs` | `MissionHost : IMissionHost` - every method; WYPT patrols, EFCT effects, SFOW reveals | the world side of actions.js / conditions.js / engine.js |
| `IMissionPresenter.cs` | the mission UI's interface, `NullPresenter`, `SequencePlay`, `DialogPlay` | G.mission, G.audio, G.rtscam, G.sel |
| `ICampaignBrain.cs` | `ICampaignBrain`, `IBrainFactory`, `BrainOptions`, `BrainAttack` (for src/Sim/Ai) | TribeAI's public methods, createBrains |
| `WorldBuilder.cs` | `RulesMapAdapter : IMapRules`, `SourceMap : IWorldMap` (MapSource + HeightField), `Build` (landscape, players, animals, nests), `CreateBrains`, `FindMap`, `MapSourceOf` | main.js buildWorld, mapSource, mapRegions, createBrains |
| `MissionSession.cs` | `MissionSession`: GameStart + configuration -> everything above, `Step()` | main.js startGame (campaign) |
| `CustomCampaigns.cs` | the campaign menu's data: `MissionGroups` (original / Booster Pack / mod / other, `NextInGroup`), `MyMapInfo` (a map of My maps, edited copies), `UserCampaign` (campaign.lua, progress, .pwcampaign) - section 10 | the remake's own |
| `Systems/Scripting.cs` | World: `SetDiplomacy`, `ChangeOpinion`, `Tribute`, `DeclareWar`, `Spawn`, `FreeSpot`, `RemoveEntity`, `SetOwner`, `SetHp`, `SetInvulnerable`, `SetDisarmed`, `SetLevel`, `Teleport`, `SetParked`, `SetResource`, `SetCaps`, `SetFilter`, `HasFilter`, `RevealArea`/`HideArea`/`RevealsFor`; DELAYED `invulnEnd`, `disarmEnd`; the `CampaignUpdate` hook; public wrappers of hooks (`RecomputeCapsOf`, `DepleteNodeOf`, `LeaveTransportOf`, `UnloadAllOf`) | game/scripting.js |
| `Systems/ScriptingEntities.cs` | fields: `Entity.Rec`, `InvulnUntil`, `Disarmed`, `IsNest`, `StartMain`; `Building.InvulnT`; `Player.Data`, `UnlockAll`, `ScriptFilters`, `FreeMains`, `Cheat`; `UnitTask.Patrol`; `Reveal`; `World.Cheats`, `Highlights`, `Brains`, `Campaign` | loose JS properties |
| `Game/Cheats.cs` | `Cheats.Apply`, `RaiseEpoch`, `SideOf`, `CheatDamage` | cheats.js |

## 2. How a mission is loaded and run

```csharp
var s = MissionSession.Create(start, config, new SessionOptions { Presenter = missionUi });   // Brains: the computer player (default)
// every 50 ms of game time (the app accumulates real time × speed):
s.Step();                       // World.Step: commands, world, AiUpdate (brains), CampaignUpdate (campaign + triggers)
// from the mission UI:
s.EndSequence(); s.EndDialog(token);
// state: s.World, s.Campaign, s.Engine, s.Host, s.Map (MapSource), s.Built.Map (SourceMap: the HeightField the view
// draws), s.StartCamera, s.Over / s.Won, s.Next, s.StateHash()
```

`Create` follows main.js startGame: `LuaMission.Load(folder)` -> `MissionData` -> `new Campaign(data, cfg).Plan(rules,
models)` (classify every object: unit / building / prop / resource / nest / landscape / helper / part; the skip list; the
models the view needs: `Need`, `FullTribes`; the tribe swap) -> `MapSourceOf(the mission folder, skip)` (the folder is the mission's map: docs/MAP_FORMAT.md) -> `WorldBuilder.Build` (world
on a `SourceMap`, trees / blocking scenery / stones / bushes / fish, `Campaign.Build`, wild animals, nests,
`WildlifeReady`, weather) -> `Cheats.Apply` -> `CreateBrains` -> `new TriggerEngine(host, lua.Script, lua.Lua)` +
`lua.Bind(engine)`. The mission folder is `GameStart.MissionFolder` below the configuration's `missions/`, else the
folder of `GameStart.Mission` in `index.json`; the map is `entry.map` below the installation's `Data` (else below each of
the configuration's folders). Difficulty 0..3: the triggers see 0..2 (3 = hard), `Campaign.Nightmare` gives the brains
`BrainOptions.ExtraSquads`.

The step: the world's events of the step reach the registry and the engine in `Campaign.Update` (World's
`CampaignUpdate` hook, after `AiUpdate`): `attacked` -> `OnAttacked`, `built` -> `OnBuilt`, `boarded` -> `OnBoarded`,
`died` / `destroyed` -> `OnObjectLost` then the record is dropped (`OnObjectRemoved`, out of its group:
`OnGroupChanged`), `removed` (RemoveEntity) -> dropped, `owner` -> `OnOwnerChanged`, `diplomacy` -> `OnDiplomacyChanged`,
`itemTaken` -> `OnItemTaken`; spawns report `OnObjectSpawned` at once (`World.OnEntity`). Then `MissionHost.Tick`
(patrols every 0.25 s, effects, highlights), then `TriggerEngine.Update`.

Objects of the engine are registry record ids (`Rec.Id`, given when a record joins the registry, never reused).
Coordinates: the host takes and gives MAP coordinates (x east, y south from the north-west corner); the world runs in
game coordinates (`Campaign.ToGame` / `ToMap`, `Campaign.Heading` = `Frame.MapHeading`, its own inverse).

## 3. The interfaces the game implements

**`IMissionPresenter`** (the mission UI; `NullPresenter` headless): `QuestChanged(quest, "shown"|"done"|"failed"|
"hidden")`, `QuestionMarkChanged(mark)`, `ShowMarker(MapMarker)`, `RemoveMarker(id)`, `SetInfoBar(text)`,
`PlaySound(name, V3? gamePos)`, `PlayMusic(file, loop)`, `IsBusy`, `PlaySequence(SequencePlay)` -> bool (true: call
`MissionSession.EndSequence()` at its end), `SequenceEnded(SequencePlay)` (camera to `play.Camera` unless `SnapBack`),
`PlayDialog(DialogPlay)` -> bool (true: call `EndDialog(token)`), `EndMission(won, text, delay)`, `Camera` (map x, y,
distance, yaw), `IsSelected(entity)`, `Deselect(entity)`, `LoadProfile()` / `SaveProfile(values)`.
`SequencePlay` carries the request, world.lua's sequence data and the bound `SeqActors` (`PoseAt(actor, t)`,
`Hidden`); the host finishes the actors and removes the `disable_fow` reveal when it ends.

**`ICampaignBrain` / `IBrainFactory`**: `Update(dt)`, `SetBehaviour`, `StartAttack(BrainAttack)`,
`StartAutoAttack`, `SetDefenceArea`, `LockUnits`, `SetRegionMap`, `CallModule`, `GameOver`; the factory gets the map's
AI difficulty for the campaign difficulty, the behaviour of `control` (`ai_Mikrobe` -> Mikrobe), `ai_options`, the level
name and `ExtraSquads` (Nightmare). The computer player implements both (src/Sim/Ai/CampaignBrains.cs): `TribeAI` is an
`ICampaignBrain`, `Ai.CampaignBrains(aiData)` the factory `MissionSession.Create` uses unless `SessionOptions.Brains`
names another (`SessionOptions.NoBrains`: none). Its brains join the world's `AiPlayers` (`World.Ai`, made on the first
brain: host ids by `World.ById`, map positions by `Campaign.ToGame`, AIRG regions by `Campaign.Regions`), whose `Update`
is the AI phase of the step; brains of another factory in `World.Brains` are updated there too. The debug option
"passive computer" (`DebugOptions.Ai = "passive"` -> `World.Cheats.Ai`) keeps the computer players' own attacks off the
human player (cheats.js; the mission's scripted attacks still come).

## 4. Tribe swap (`GameStart.Tribe`)

As remake/docs/CAMPAIGN_RUNTIME.md §1.1: `Plan` makes a `TribeSwap(rules, missionTribe, chosenTribe, given, native,
manifest)`; the human player is the chosen tribe (`ForeignActions` on); the player's placed objects, start army, spawns
and objects given later (`SetOwner` -> `Convert`: same record, new object of the equivalent, same hit point share, level,
gate state; buildings re-fitted with `Placement`) become the chosen tribe's; `Registry.ClassIs` lets class queries of the
mission's tribe find the equivalents (also after the mission gave such an object away - `Rec.FromHuman`, set by
`SetOwner`: BoosterPack 1 mission 4-1 hands the player's flamethrowers to the fugitives and counts "hu_flamethrower of
player 5"); level and TECH filters are translated (`FilterFor`, `Filters` drops shared bans);
`IsMissingClass` makes conditions about a class the chosen tribe lacks (or gets later) true - a BuildingCount met so
also writes its counter as reached (`">7"` -> 8), since quests may wait on the variable (mission 13's nests) - except
for units the player starts with (`StartUnitClasses`: mission 20's flamethrowers); the Dustriders' mobile base
(and the main building standing in for it) waits off the map (`FreeMains`) until `PlaceFreeMain`. Tests: mission 11 as
SEAS (MIRAGE), mission 1 as Dustriders, `ObjectiveSwapTests` (mission 13's nests as the SEAS; the audit
`TribeSpecificConditions` lists every condition of both configurations naming a class of the mission's tribe with
each playable tribe's equivalent, and which are met at once).

One mechanism covers every objective: every class query of the trigger engine (the object query's `obj_class` /
`exclude_class` of REGN, OBJP, UNTT, TRMO, SGHT ..., BuildingCount's `class`) goes through `Registry.ClassIs` ->
`TribeSwap.CountsAs`: the equivalent, plus `TribeSwap.ALSO` (a role a tribe fills twice: MIRAGE's SEAS stygimoloch and
machine-gun nests). Roles outside the base game's table that closeness cannot find by name are rows of `ROLES`
("breeding defence": dilophosaurus nest, kennel, skull protector, stygimoloch nest) or `ALIASES` (a mod's class for
a role whose table class the mod lacks: MIRAGE's seas_trade_dino is the cart).

## 5. Debug options (`GameStart.Debug`)

`Cheats.Apply` after the build: free (`Player.Debug`), epochs (`MaxEpoch`, `RaiseEpoch` through `CompleteUpgrade`),
resources, storage / gather / speed / noHousing (`Player.Cheat`, read by economy and production), unit limit and
pyramid (with the pyramid's sum as the floor), unlock (`Player.UnlockAll`: the level's AntiActions do not reach the
player), reveal (`World.Cheats.Reveal` for the view's fog), `Cheats.CheatDamage` for combat.

### MIRAGE's game options (`GameStart.Gameplay`)

The campaign menu has, with MIRAGE only, **Game options**: **Sixth stage (final stage: titans, great cannon ...)**
(MIRAGE's "MaxEpoch 6", ticked by default as MIRAGE's own default), **No titans** (MIRAGE's "RemoveTitans", off by
default), **Flying units** (MIRAGE's "FlyingEnabled", off by default as MIRAGE's own: without it the flyers' build actions
are hidden - xenage/flying_disabled), **Portal termini** (MIRAGE's "Portals", off by default) and **Building limit**
(MIRAGE's "BldgMaxCapacity", off by default: docs/SIMULATION.md "Building limits") - the same options as
the skirmish setup (docs/SKIRMISH.md). The choice is kept in settings.json
(`campaignGameplay`: `epochSix`, `removeTitans`, `flyingEnabled`, `portals`, `buildingLimit`, shared with the co-op lobby) and goes into the mission's `GameStart`
(`gameplay`) - from the menu's Start, through the army screen of a mission with an army budget, `--mission` and
`--army-mission` -, so a saved game and a replay keep it. `MissionSession.Create` puts it in `CampaignConfig.Gameplay`;
`Campaign.Build` gives it to every player of the mission (`PlayerOpts.Gameplay` -> `Rules.PlayerTree`: the xenage
filters epoch_six_enabled / titans_disabled / flying_disabled and portal_terminus_visible for all four tribes, as
StartLocation.usl MirageInitialized), the co-op
seats and enemies too (docs/COOP.md). Null (older saves, the base game): MIRAGE's defaults. Unticked "Sixth stage": the
headquarters' / fireplaces' final_stage is never offered at epoch 5 (docs/SIMULATION.md "MIRAGE: the sixth stage").
Test `MirageGameOptionsPathsTests.CampaignMission`, `MirageClassesTests.FlyingDisabledByDefault`. MIRAGE's per-mission option table (`CustomMapMirageSettings.txt`, e.g. Single_01 with flying on) is deliberately not applied: flying stays off on every mission unless the menu turns it on (Kacper, 2026-11-13).
The computer players follow the chosen options as MIRAGE's do (the sixth stage requested with *Sixth stage*, 2 flyers in the village's waves from epoch 3 at Medium / Hard with *Flying units*, no titan / flyer rows of the unit mix when removed / off): docs/AI.md "MIRAGE: the AI follows the game options".
#### MIRAGE's fixed options per level (`CustomMapMirageSettings.txt`; checked 2026-11-20, master b320f9bb)

**Where MIRAGE applies them.** `Scripts/Game/misc/CustomMapMirageSettings.txt` lists per level (level name, spaces as
`_`) values of the MIRAGE server settings. They are applied **only in the multiplayer lobby**: choosing such a level
(`LevelSettingsPage.UpdateCustomMapMirageSettings` -> `CMirageClnMgr.UpdateCustomMapAttribs`: writes
`Server/GameplayOptions/<name>` - `Server/AllowAlienCommands`, a path fixed in the exe - and `SetAttrib`, after
backing up the user's values; restored when another level is chosen) and the lobby's adjust windows
(`Mirage_UI_scripts.usl CAdjustMirage.SetAndLockMirageSetting`: value set, control locked). The single-player campaign
never reads the file: there the server takes the Options menu's values (`MirageSrvMgr.usl`: `!IsMultiplayer()` ->
`xConf.GetSet...`). The Single_* entries are for MIRAGE's **multiplayer campaign** (CustomMapSettings.txt
`MultiplayerCampaign = 1`), the remake's co-op counterpart. The remake applies none of them (Kacper, 2026-11-13: flying
stays off unless the menu turns it on); the table says what applying them would change.

**MIRAGE's defaults** (`SetDefaultStuff` / the adjust windows, the same in OptionsMenu.usl): every checkbox off except
AllowThrow, OldDisembark, AuraSharing (on); TransportHealing 60, DwnLvlSwitch 0 (no downleveling), MinEpoch 1,
MaxEpoch 6, HQTimer 5 min. Only values different from these are listed. Installed MIRAGE (Dec 2024) vs master:
master sets AllowAlienCommands 1 for Single_05 / 11 / 16 / B42 (installed: 0) and names `Prison_Island` /
`Living_ruins` without the `_[CUSTOM]` suffix; nothing else differs.

**Every level listed** sets UseFM 1 (finishing moves; MIRAGE's default off - the remake always plays them, no change)
and HQTimer -1 (no head-quarter timer - the remake has none in a campaign, no change); all but 05 / 11 / 16 / B42 set
TransportHealing 40 (healers on an open transporter heal at 40 % instead of 60 % - **not in the remake**).

| level (remake mission) | differs from the defaults | applied in the remake |
|---|---|---|
| Single_01 (1 Stranded) | FlyingEnabled 1, DivideSkulls 1, DwnLvlSwitch 1, MaxEpoch 4 | flyers buildable; no sixth stage (the level already stops the Norsemen at epoch 3: no other change); **not implemented**: skull sharing, heroes may be levelled down |
| Single_02 (2 Druid Island) | DwnLvlSwitch 2, MaxEpoch 4 | no sixth stage (level: epoch 3, 4 after the mammoth quest - same); **n.i.**: downleveling |
| Single_03 (3 Amazon Island) | BldgMaxCapacity 1, FlyingEnabled 1, DwnLvlSwitch 2 | building limits on (menu option "Building limit"); flyers; **n.i.**: downleveling |
| Single_04 (4 The Dustriders) | BldgMaxCapacity 1, FlyingEnabled 1, DwnLvlSwitch 1, MaxEpoch 3 | building limits; flyers; no sixth stage; Dustriders capped at epoch 3 - but the level's trigger `Ix04` (25 workers) switches the same age_4 / age_5 AntiActions off, which lifts MIRAGE's cap too (inferred); **n.i.**: downleveling |
| Single_05 (5 The Holy City) | CorpseDamage 1, BonusSkulls 1, DivideSkulls 1, AllowAlienCommands 1, DwnLvlSwitch 2, MaxEpoch 1 | no sixth stage (MaxEpoch 1 adds no AntiAction: StartLocation.usl caps only 2-5); **n.i.**: corpse damage, bonus skulls, skull sharing, allied control, downleveling |
| Single_06 (6 Pirates and Hostages) | FlyingEnabled 1, HeroPool 1, DwnLvlSwitch 2 | flyers; **n.i.**: hero pool, downleveling |
| Single_07 (7 The Water Temple) | BldgMaxCapacity 1, FlyingEnabled 1, MaxEpoch 5 | building limits; flyers; no sixth stage (the level already stops at 5) |
| Single_08 (8 Valley of the Gods) | BldgMaxCapacity 1 | building limits |
| Single_09 (9 Perilous Path) | FlyingEnabled 1, DwnLvlSwitch 1, MaxEpoch 4 | flyers; no sixth stage (level: epoch 3); **n.i.**: downleveling |
| Single_10 (10 The Prophet) | FlyingEnabled 1, MaxEpoch 4 | flyers; no sixth stage (level: epoch 4) |
| Single_11 (11 Arena) | CorpseDamage 1, BonusSkulls 1, TitanSlots 1, UseSupply 1, DivideSkulls 1, AllowAlienCommands 1, DwnLvlSwitch 1, MaxEpoch 1 | no sixth stage; **n.i.**: supply system, titan slots (+2), corpse damage, bonus skulls, skull sharing, allied control, downleveling |
| Single_12 (12 Sea Battles) | BldgMaxCapacity 1 | building limits |
| Single_13 (13 The Rush) | FlyingEnabled 1, DwnLvlSwitch 2 | flyers; **n.i.**: downleveling |
| Single_14 (14 Prisoners) | - | nothing beyond the common rows |
| Single_15 (15 New World Order) | BldgMaxCapacity 1, FlyingEnabled 1, HeroPool 1 | building limits; flyers; **n.i.**: hero pool |
| Single_16 (16 Showdown!) | CorpseDamage 1, BonusSkulls 1, DivideSkulls 1, AllowAlienCommands 1, MaxEpoch 1 | no sixth stage; **n.i.**: as Single_05 without downleveling |
| Single_B41 (41 and its SEAS copy 20: the level name is "Single B41" in both) | FlyingEnabled 1 | flyers |
| Single_B42 (42 / 21) | CorpseDamage 1, TechtreeSteal 1, BonusSkulls 1, DivideSkulls 1, AllowAlienCommands 1, DwnLvlSwitch 2, MaxEpoch 1 | no sixth stage; **n.i.**: tech tree stealing and the rest as Single_05 |
| Single_B43 (43 / 22) | TechtreeSteal 1, MultiTribe 1 | **n.i.**: tech tree stealing, other tribes' workers from the start |

Not listed (MIRAGE's lobby values): the tutorial and "Seeking Ada" (single_seas_02). "No sixth stage" = the menu's
**Sixth stage** unticked (MIRAGE: EpochSix() is MaxEpoch >= 6). MaxEpoch 2-5 also adds AntiActions for
`age_<N+1>` .. `final_stage` of all four tribes to every player with a start location (StartLocation.usl:811-825),
enemies included. What the options do (MIRAGE's tooltips, `Locale/*/Texts/mirage.ltf`): DwnLvlSwitch 0 none, 1 heroes
only, 2 all units (not below captainlevel - 1), 3 no limit (FightingObj.usl `IsLevelAcceptable`, pyramid
`NewPyramid.usl`; the remake refuses every level down: `World.ChangeLevel` "down"); DivideSkulls: a kill's skulls go to
every player who damaged the unit (the remake: the last one); BonusSkulls: a unit's kills raise the skulls it is worth;
CorpseDamage: big corpses hurt enemies they fall on; HeroPool: one player per hero, random hireable heroes;
TechtreeSteal / MultiTribe: other tribes' tech trees / workers; TitanSlots: titans + 2 transport slots; UseSupply:
the supply system instead of the 52-unit limit; AllowAlienCommands: control of allied units.

### The army budget (`GameStart.Army`)

Missions with `point_buy` (map.lua's mission) open the army screen before they load (`CampaignMenu.StartWithArmy`,
`CampaignArmy.Of`: the level's preset fixed, the budget max(bonus points of the mission before, the least credits), the
allowed classes, the level's row limits; docs/SKIRMISH.md "Campaign missions with an army budget"). `CampaignConfig.Army`
is placed at the human's start location instead of the level's start army (campaign/setup.js startLocation, SL:482-623);
skulls stay the level's. Online co-op: every human seat buys its own (docs/COOP.md "Armies").

The level's start army (and a co-op seat's `"default"` army) spawns the classes it names, each at least at its lowest
real level (`Rules.MinLevel`). Only a pure rider - a class whose tech tree object has a 1-HP placeholder at every level
(MIRAGE 7's `aje_velociraptor_handler_char`) - comes as the mount named after it (`Campaign.MountOf`). Fixed
2026-11-20: `hu_archer` (the chariot's captain, a placeholder only at level 1, below its lowest level 2) came as the
chariot - missions 14 and 15 and co-op seat armies (tests `StartArmyTests`).

A won mission's bonus points (`Campaign.Boni`) are the next mission's budget (settings.json
`campaignCredits`).

### Your colour (`GameStart.Color`, `CoopSeat.Color`; 2026-11-21)

Kacper: "Add an option to pick your starting colour in the campaigns, but block the colours used by the enemy." The
campaign menu's **Your colour** (below the tabs, for both: `CampaignMenu.ShowColor`, `game/src/UI/CampaignColor.cs`)
offers the skirmish's choice (`PlayerColorCell`: the original's eight party colours, the remake's eight, any RGB colour
`#rrggbb`) and a first entry **As in the mission** (the mission's own colour, its swatch the human player's colour of
the map). Every colour another player of the shown mission has - an enemy, an ally, a neutral or computer player of
map.lua's `mission.players[]` with a palette `color` - is greyed out in the list and in the picker's palette
("used by <name>"); a colour picked in the square closer than `Colors.NEAR_DISTANCE` to one of them is not taken (the
choice before stays). Players without a palette colour (drawn grey) block nothing.

* **Where**: the original campaign, the Custom campaigns tab's missions (Booster Pack, the mod's, other folders', the
  remake's missions like Outbreak), My maps (the map prepared as a mission to read its players), the user campaigns'
  missions; the co-op lobby (docs/COOP.md "Colours"). Containment and the Scenarios keep their story roles' colours
  (their players come from the sequel scripts; the box is greyed there).
* **Remembered per campaign** (settings.json `campaignColors`, `CampaignColor.Key`): `campaign` (the original),
  `booster`, `mod`, `other:<folder>`, `remake`, `maps`, `user:<campaign>`, `coop` (the lobby's seat). A mission
  started gets the remembered colour when it is free there (`CampaignColor.For`), else none - the mission's own; the
  remembered choice stays for the missions where it is free. The end screen's Retry keeps the start's colour, Continue
  resolves the remembered one for the next mission.
* **The world** (`Campaign.Build`, `MissionColors`): the human player gets the colour (`MissionColors.Apply`: the dark
  colour for the minimap, rings, health bars and chat names, the light one for the models' party colour, the SEAS paint
  and the flags, the palette slot for the minimap's and rings' original colours) only when no other player of the world
  has it (the same check as the menu's: the same palette slot or a light colour closer than `NEAR_DISTANCE`); otherwise
  the mission's own (logged as a setup warning). The check is the world's, so a remembered choice, a hand-edited start
  or another mission version can't give two players one colour, and every machine decides the same.
* **Saves, replays, lockstep**: the colour is part of the GameStart (`color`; co-op seats `coop.seats[].color`), so a
  save (a replay of its inputs: docs/SAVES.md), a replay and the other machines of a network game build the same
  players. A player with a chosen colour (`Player.ColorPicked`) mixes its colour into the state hash (`World.StateHash`,
  the `players` part, the state dump's `colour`); games without a chosen colour hash as before, so older saves still
  load and verify.
* `--mission n --color <id>` starts a mission with a colour (the world refuses a taken one).

Tests: `CampaignColorTests` - the rules (`MissionColors.Used / Blocker / Resolve / ForSeat`, custom colours near a
taken one, the start's JSON round trip), a mission start with a free colour (the human has it: slot, party colour,
minimap colour; the hash changes only with it) and with an enemy's colour (refused: the mission's own, the same hash
as without one), co-op seats (each its own colour; seat 2 can't take seat 1's).

## 6. Tests (`tests/PwRemake.Tests/CampaignTests.cs`)

`dotnet run --project tests/PwRemake.Tests -- CampaignTests` (data: `PW_CONTENT` = /home/claude/testcontent, the
configuration's data or `/opt/data/{base,mirage}`, the staged maps; `PW_CAMPAIGN_MINUTES` shortens the runs):
`Mission11Setup` (players, diplomacy, start army at the start location, filters, groups, regions, props where the data
put them, the query block, 5 minutes with the heroes kept alive by the debug option "your defence" ×100 - left alone
they fall to the waves within two minutes, as in the JS, whose playthrough heals them: the sequence, map pings, the
first quest, six animal waves out of the gates (alive + killed), no errors), `Mission01/02/08`, `AllBase` / `AllMirage` (all 24 missions of each configuration, 5 game minutes each: no
trigger errors, triggers fired), `SwapArenaAsSeas`, `SwapMission01AsDustriders`, `Determinism` (two runs, same hash
every 30 s), `DebugOptionsApplied`, `RegistryAndRegions`, `RuneGatesClosePassages` (BoosterPack 1 mission 4-2: the closed rune gates
close their side chambers). `PW_CAMPAIGN_MISSIONS=41,42,43 ... -- CampaignTests.All` runs only those missions. All pass
(2026-10-31; the gate test 2026-11-02).

## 7. What is missing / waits on other ports

* The world's systems and the computer players are all in (the integration, 2026-10-31): the stubs this layer called
  are gone (`PlacementOf`, `AddProgress`, `ScorpionActivate`, `CompleteUpgrade(at, a, p)`, `AddWell`, `AddLake`,
  `StartWeather`, `Capacity`, nests through `AddWildNest` / `JoinNest` / `FlockAdd`, items through `PlaceItem` /
  `GenerateItems` / `ItemOfProp` / `TakeItem` / `DropItem`, `SetAggro`); the items an item spawn makes get no record (as
  setup.js). Whole-game soak runs of every mission: docs/TESTING.md "Soak".
* **Items the triggers make** (`CreateObj` of an item class - CItem, the tech tree's Items/Objects): an item of the
  world lying on the ground (`Campaign.SpawnItem` -> `World.PlaceItem`, a record of type ITEM like the map's items), which
  a unit picks up (a right click with the unit: the "trade" cursor, task "pickup") as the original's CItem. Until 2026-11-12 such
  a class had no rule class and became a scenery prop: the chest showed but nothing could click or take it - Kacper's
  report from MIRAGE mission 13 (the zeppelin trader's reward `item_pannier` next to the trader's pile of goods; the
  pile itself, `flying_trader_pile_of_goods` / CTraderGoods, is scenery, not selectable in the original either, and the
  reward's food, stone and wood are given at once). The same for every reward chest of missions 2, 4, 7 - 10, 13, 15,
  22 and 43 (base and MIRAGE: amulet, ankh, bridle, compass, heal-all, incubator, pannier, ring, talisman, toolkit,
  totem of war). A map's hidden item (missions 6 and 12: the admiral's hat, the ankh before `ObjAppear` shows them) is
  not there to be taken - no unit (and no computer player) walks off for it (`World.ItemLying`). Tests:
  `TreasureItemTests`.
* **Triggers that wait** (the conditions of the triggers still listening after 5 minutes without a player, all base
  missions - written before the systems were merged; most now wait on the player's progress): CVAR 617 and SQEN / QUES / DSEN (chains that follow the player's progress), REGN 463 (units walking
  somewhere: the player's or the computer's), DEAD 85, DYIN 49, ISFG 56, PLDE 56, OBJP 29 (combat), BLDG 75, TECH 45,
  PLYR 3 (construction, production, economy), SGHT 65, ITEM 12 (items), WAYR 2. The per-mission lists are printed by
  `CampaignTests.AllBase` ("waiting on:").
* Scenery buildings have no hit points;
  `gfx_prefix` ignored; SGHT sees 25 m (the JS's rules had no `stats.sight`). Saving a running mission: docs/SAVES.md.
* Combat should set `UnitTask.Back` on the walk back to a post (the JS's `task.back`): the patrol driver lets such a walk
  be; combat reads `Building.InvulnT`, `Entity.Disarmed`, `Entity.IsNest` and should drop a tower's targets on `owner`.

## 8. Mission status (what the campaign menu offers)

The scripted playthroughs (`tests/PwRemake.Tests/Playthrough`, ports of the browser remake's `campaign_bot.js` and
`campaign_play_00.js` ... `_16.js`; docs/TESTING.md "Playthroughs") play each mission's main quests to the end with
each tribe of both configurations, difficulty 1. `game/src/UI/CampaignStatus.cs` follows them: a mission won with
every tribe of a configuration is **Playable** there, won with some tribes **Partial** (the note names the others),
else **NotYet** (greyed out). BoosterPack 1's three missions (ids 41-43; Campaigns.txt orders them after mission 4 - the menu shows them under
Custom campaigns, section 10) and MIRAGE's SEAS missions (ids 20-22: the SEAS versions of 41-43, single_b4N_seas; 23:
"Seeking Ada", single_seas_02) have playthroughs written here from their triggers (`PlayB41.cs`, `PlayB42.cs`,
`PlayB43.cs`, `PlaySeas02.cs`; the JS had none). The SEAS missions exist only in MIRAGE: the prepare tool exports a
configuration's own folders only (base: Base + BoosterPack1). Every run's step log:
`tests/PwRemake.Tests/Playthrough/results/logs/`, the table of all runs: docs/PLAYTHROUGHS.md.

| mission | ParaWorld (Norsemen, Dustriders, Dragon Clan) | MIRAGE (the three + SEAS) |
|---|---|---|
| 0 Tutorial | won with all | won with all |
| 1 Stranded | won with all | won with all |
| 2 Druid Island | won with all | won with all |
| 3 Amazon Island | won with all | won with all |
| 4 The Dustriders | won with all | won with all |
| 4-1 The Devil's Advocate (41) | won with all | won with all |
| 4-2 A Cold Day In Hell (42) | won with all | won with all |
| 4-3 Operation Neptune (43) | won with all | won with all |
| 5 The Holy City | won with all | won with all |
| 6 Pirates and Hostages | won with all | won with all |
| 7 The Water Temple | won with all | won with all |
| 8 Valley of the Gods | won with all | won with all |
| 9 Perilous Path | won with all | won with all |
| 10 The Prophet | won with all | won with all |
| 11 Arena | won with all | won with all |
| 12 Sea Battles | won with all | won with all |
| 13 The Rush | won with all | won with all |
| 14 Prisoners | won with all | won with all |
| 15 New World Order | won with all | won with all |
| 16 Showdown! | won with all | won with all |
| SEAS 4-1 The Devil's Advocate (20) | - (MIRAGE only) | won with all |
| SEAS 4-2 A Cold Day In Hell (21) | - | won with all |
| SEAS 4-3 Operation Neptune (22) | - | won with all |
| SEAS 1 Seeking Ada (23) | - | won with all |

The BoosterPack 1 and SEAS missions (2026-11-02, again 2026-11-03 on main 4d79b54; all 37 runs won, docs/PLAYTHROUGHS.md):

* **4-1 The Devil's Advocate** (41, SEAS version 20): the party watches the fight at the first contact, builds a base
  (as the Dustriders: a tent, a small farm in small-farm mode and its resource collector - another tribe's mission
  without their mobile base), researches epoch 4 and promotes Leighton to level 4 (the flamethrowers need
  `livingstone_s4`; skulls topped up), trains flamethrowers and marches them in squads to the rendezvous point, where
  they gather in Flamer_Exit before one steps into Activate_Flamer_occupy (Gz01 hands over only those in the region a
  second later); then Leighton boards the carrier and the film sb4108 wins. 19-57 game minutes, 25-40 top-ups.
* **4-2 A Cold Day In Hell** (42 / 21): survivors, Kleemann's expedition, the ice caves to the riddle room, the pentacle
  (Kleemann on the five panels in order), the Avatar. 7-9 minutes. The rune gates' side chambers are not needed: the
  main way leads to the riddle room. There is no object "finalgate" on the map (Gi07's Open Gate finds nothing; the film
  sb4225 moves everyone to the Avatar anyway).
* **4-3 Operation Neptune** (43 / 22): the Dustriders' camp in the bay (MIRAGE: five named buildings, L01b; the others
  turn invulnerable and untargetable when nearly down, Gx01 .. Gx05), then a unit at the Norsemen's horn (the rocket
  panel is the other way). 3-4 minutes.
* **Seeking Ada** (23): the outpost, the infiltration team (Leighton at the meeting point), the main base, four units at
  the sacrificial pit. 7-9 minutes; the base and the docks are left alone.

Fixed on the way (2026-11-02):

* **Gates close their passages** (`Nav.RectCells(..., cover)`, `Building.CellsOf`): a gate's nav cells were the cells
  whose centre lay in its pathfinder boxes; a thin box turned 45 degrees (the rune gates' 1.4 m door, many palisade
  gates' doors) left gaps and units walked through closed gates. Gates now take every cell their boxes touch. Sweep
  (every mission of both configurations, the ground reachable from the player's start with every gate closed, old vs
  new cells): big leaks closed in missions 1 (the barbarian village), 4, 16 (the end boss gates), 41 and 42 (and 20,
  21), 10-512 m² in 2, 3, 5, 7, 8, 10, 13, 14, 22, 23, 43, none in 6, 12, 15. Test `CampaignTests.RuneGatesClosePassages`.
* **An automatic gate of nobody lets everybody pass** (`NavGrid.GateBlocks`: AUTO lets the owner and its friends
  through and blocks enemies - nobody is an enemy of no owner): mission 8's `-01_gate_01` into Valhalla (AUTO, no owner)
  had been walked through by its gaps only; with the gaps closed the heroes could not reach the guardians as the
  Dustriders (the only ownerless AUTO gate of all missions).
* **Only a gate's door opens** (`Building.SolidCells`): the model's pathfinder table marks the door box (attr bits
  0x18000 without 0x4000); the other boxes - towers, wall joints, the 28 m wall arms and pillars of the holy city's
  `hcl13_gate` - block like any building's, open or shut. Before, the whole footprint was gate cells: an open (or, for
  friends, automatic) 67 m city gate was a 67 m hole in the wall. A model without a marked door opens as a whole.
* **Cutscenes: gates and other bound buildings no longer flicker** (player report, MIRAGE 16 "Showdown": "gates keep
  opening and closing rapidly"): the bound buildings of a scene run on a clip of the scene's own (`SceneClips`), the
  ItemAnims play their frame ranges, the world's clip goes on from the scene's at its end (docs/GAME_SESSION.md
  "Cutscenes"). Sweep (both configurations, every mission's opening and every cutscene with a bound building): mission
  16 (sc_7050: end boss gates 0 and 2 put back 286 times; sc_7052, sc_7054: the gates, the Scorpion), 13 (the carriers in
  sc_4070 / sc_4081), and every scene whose bound building had a running clip; in the world no gate flickers (no
  trigger sets a gate every step, no automatic gate toggles faster than its 8 s). Test `CutsceneGateTests`.
* **Cutscenes: actors drawn for the scene only** (player report, mission 2 as the Dustriders: "wrong model for the
  turtle, the ankylosaur is missing its catapult"): the scene's start links (the turtle's shell at "con") and the
  attachments' clips (the rider's ride_idle_0) - both exported by the prepare tool now (`tools/prepare/seq_actors.py`;
  the missions are exported again once) -, and the player's actors as the player's tribe in a swapped mission
  (docs/GAME_SESSION.md "Cutscenes"). Test `CutsceneActorTests`.
* **Cutscenes: swapped actors and parts never loop an attack** (player report, mission 13 as the SEAS: "the SEAS Black
  Widow in the cutscene is constantly playing its attack animation"): sc_4070's aje_allosaurus_1 (owner 0) is drawn as
  the SEAS' equivalent seas_wehrspinne (`Campaign.OwnClass`, "big hunter"), whose turret `seas_wehrspinne_top` took the
  composite table's clip - `attack_front`, the clip it shoots with and the model's only one - as its standing loop (the
  base scenes' Black Widows did the same: sc_4070's three, sc_4080's). A part drawn for a scene now rests (no turret's
  clip, no attack clip: `SeqActors.RestClip`; the world's view never played a turret's clip at rest either), and a clip
  of the scene that the swapped model lacks becomes its own of the same kind - a walk / run / swim of that speed, a fall,
  a hit, an attack only where the scene attacks -, else its idle (`SceneClips.SceneClip`; Dustrider warriors' res_idle_*
  on SEAS soldiers, the allosaurus' walk_2 on the spider). Audit: every scene, every tribe of both configurations (3659
  parts, 408 swapped actors): no part loops an attack the scene's own data does not give it. Test
  `CutsceneMission13Tests.NoAttackLoops`.
* **Tribe swap: objects given away keep their mission class** (`Rec.FromHuman`, `Registry.ClassIs`): 4-1 hands the
  player's flamethrowers to the fugitives and counts "hu_flamethrower of player 5" (SEAS version: seas_flamethrower);
  played with another tribe they were never counted. Sweep: only missions 41 and 20 give the player's objects away by
  class (OCPY with obj_owner 0 and another new owner); mission 8's has no class.
* **Test content**: the BoosterPack / SEAS mission folders of the test content had no texts (made before pack_texts.py;
  docs/TESTING.md "Playthroughs").
* The playthrough bot (`CampaignBot.ActionsOf`) asks an object's own tree for its actions (a building's local upgrades:
  the Dustriders' farm modes show the resource collector, the dilophosaurus), as the HUD and the computer player do.

All 156 runs again on 2026-11-03 on main 4d79b54 (after port/chase, port/m16gates, port/aiferry, port/viewfix): all won
(119 of the 17 missions, 37 BoosterPack / SEAS; docs/PLAYTHROUGHS.md). Before the bot was adapted 151 of 156: missions 6
(base, Norsemen), 8 (base, Dragon Clan) and 15 (base as the Dragon Clan, MIRAGE as the Norsemen and the Dragon Clan)
failed. Bisected over main's merges (each run deterministic, also alone): the three base runs pass on 2731f60
(port/m16gates) and fail from fbd651b (port/aiferry) on; the two MIRAGE runs of 15 fail through the same change (the
SEAS crossings, below). The game behaves as the original there in each case and the bot leaned on the old behaviour (no
game change):

* **8 Valley of the Gods** (base, Dragon Clan): the way to Valhalla's gate leads through the valley of the killer
  statues (`03_killerstatue_01..12`, `hu_undead_killer`, hitable false on the map, 500 a blow: an ambush to run
  through). With the scripted walks at their gait (port/aiferry) the undead groups came a little later, the run took
  fewer cheat kills and so fewer skulls, and `promote()` spent 175 of the 230 skulls on Kleemann - who stays back - before
  Béla: at level 2 she fell to two killers' blows within one second (the chase merge's blows decided at the swing).
  `Play08` promotes the fighters only.
* **6 Pirates and Hostages** (base, Norsemen): the pirates' fire boats now come for the fleet at once (their crossings
  no longer stand two minutes); the fleet, busy with them by the water gate, stopped 15-19 m from the last water turret
  and a fire boat - inside the steam boats' minimum range (about 20 m). The fight ends there (Fight.usl, port/chase) and
  the order given again every second ended again: neither side's guns fired for 40 minutes. `Play06`'s warships back
  off from what they fight when it is inside their minimum range and beyond the cheat's 12 m, as a player would move
  them.
* **15 New World Order** (three runs): the SEAS (players 3-4) now cross by hovercraft and land on the base island
  (docs/AI.md "Transports that stood in place" 2; before, every SEAS crossing ended "no transports"). MIRAGE as the
  Norsemen: the landings broke the fireplace at 14 minutes (the population limit fell to 8, nothing more was trained)
  and the new soldiers stood at the shore - the bot ordered them onto a helicopter near them every second, an attack
  that ends at once for units on foot. MIRAGE as the Dragon Clan: the raids killed the workers. Base as the Dragon Clan:
  station 3's guards fell again and again and the heroes waited there for new ones the full population (20) could not
  give. `Play15`: soldiers still on the base island fight the landings first (within 100 m of the main building), the
  hunts leave flyers alone (the archers shoot them, the cheat wears them down), the skulls buy levels (`promote()`; the
  heroes stood at level 2 with 10 000 skulls), and a held station waits for new guards only while there can be any.

All 156 runs again on 2026-11-03 after port/minrange (the automatic target choice by the minimum range, as the
original's `SortEnemyList` FightingObj.usl:4890-4924: a unit whose primary weapon has a minimum range and that has no
secondary short or medium weapon leaves out the enemies inside its minimum range and those out of its reach;
docs/SIMULATION.md "Target choice by the minimum range"): all won (docs/PLAYTHROUGHS.md). Before the bot was adapted 153
of 156: three MIRAGE runs failed. No game bug in any of them: the rule is the original's, and the units that follow it
(the steam boats, the catamarans, the rocket boats, the log cannons) now fight what they can shoot instead of locking
onto an enemy at their feet, so the fights went otherwise and the bot's plans had been leaning on the old course:

* **6 Pirates and Hostages** (MIRAGE, Norsemen): the Ninigi pirates' landings at the harbour (archers, spearmen, trikes,
  a baryonyx) broke the harbour, the workers and the fireplace while the army stood idle at the freed prison; no warship
  was made after the first dragon boats, which the mines and the fire boats sank. (It was close before too: the old
  run's harbour fell once and was rebuilt in time.) `Play06`: after the prison the army goes home and fights the
  landings within 100 m of the harbour.
* **4-1 The Devil's Advocate** (MIRAGE, Dustriders): the big squad brought 15 flamethrowers through, then every last
  squad of three fell to the Norsemen's army (warriors, archers, steam tanks, log cannons) on the way. `PlayB41`: a
  small last squad (fewer than 8 still missing) takes three more along.
* **12 Sea Battles** (MIRAGE, Norsemen): four carriers broke through while the dragon boats chased the carrier nearest
  them across the map. `Play12`: a carrier within 350 m of its goal (the region "Zielregion der Carrier") first, else
  the nearest (always the one nearest the goal sent the SEAS submarines round the whole map and they were lost); the
  land army hunts only what stands on ground it can walk on (base as the Norsemen: 192 teleports towards a SEAS
  helicopter hovering over the water off the shore).

Missions 6, 12, 41 and 20 were run again with the changed bots in every configuration (25 runs, all won). The cheats
over all 156 runs: 2017 teleports, 17 242 cheat kills, 625 top-ups (before: 1882, 16 675, 657).

The 17 missions again on 2026-11-02 after port/missionfix (merged with main ccd9953, fdlibm maths): all 119 runs won (docs/PLAYTHROUGHS.md; mission 15 again after the last change of `Play15`; the BoosterPack and SEAS rows are
the earlier runs). Before
it 100 of 119 (missions 6 in both configurations, 7, 12, 14, 15 and 2 in MIRAGE, 15 as the Dragon Clan failed). What the
failures were and what changed (port/missionfix, 2026-11-02):

* **12 Sea Battles** (MIRAGE, game bug): MIRAGE moved `seas_carrier_fake` from the buildings to the ships
  (Objects/SEAS/SHIP, the map's own ObjPath attribute says so) and its maps kept the stored type BLDG; the remake placed
  such an object by the stored type, and a BLDG whose class is a SHIP fell through to scenery. Mission 12's fleet
  carrier `seas_carrier_fake_0` (the "Sequence squad": hidden until Gc00 shows it after sc_4030, then on a waypoint
  route) was a prop nobody could sink, and Gcb04 ("no seas_carrier_fake on the map") never came true - it was not a
  placeholder, it is one of the carriers. **Fixed** (`Campaign.Plan`): a map object of a unit or building type is made by
  its class's type (`ObjectPlan.Retyped`, the record takes the class's type). Sweep (`CampaignTests.MapTypeFollowsClass`,
  every map of both configurations): the only such objects are this carrier and mission 13's four SEAS carriers
  (`seas_carrier_fake_0..3`, owner 7, visible: scenery before, now ships that fight); mission 13 is won with all four
  tribes as before. The Dragon Clan's fleet then still lost (its population: below).
* **6 Pirates and Hostages** (game bug + bot): the flagship `pirate_boss` (CPirateBoss, 100 hit points) carries three
  buildings on deck - `pirate_boss_sail` / `_tail` / `_row`, 5000 each - and Gb01 waits for "no pirate_boss in check_boss
  or no building of the pirates there". The remake made the hull an ordinary ship: the pirates' computer player sent it
  after the player's new harbour, it sailed off its parts and out of the oval check_boss, and the mission was won with no
  ship of the player near. **Fixed**: the hull is anchored (`Unit.Stationary`, as mines and water turrets) and an
  automatic target search takes its parts, not the hull (FightingObj.usl:4821-4830 puts the sail, tail and bow in the
  target list instead). The bot did not play the mission as it is meant ("Build a strong fleet to attack it from the
  sea"; the walkthroughs: floating harbour, siege ships, the water gate): the JS sent the heroes to the shore, 60 m from
  the ship, where the cheat's damage reached the parts through their radius. `Play06` now builds a harbour and the
  strongest warships the tribe can make (catamarans at epoch 4 for the Dustriders, steam boats / dragon boats, rocket /
  fire boats, cannon turtles / submarines), sails north of the two mine belts to the water gate, breaks its turrets and
  the gate and sinks the ship; amphibious units (MIRAGE's sequence units after sc_8080 give the Dragon Clan a baryonyx)
  go for it themselves. The playthroughs of a point-buy mission played as the Dustriders now start with the army screen's
  default army as the menu does (the level's start army has no resource collector: it is a card bought with the budget).
* **7 The Water Temple** (bot + game bug): three waves of Black Widows walk to Taslow's place (Ahc02-04) and one inside
  "GameOver - Wehrspinnen at Teslaplace" loses the mission (X03); the JS bot chased the nearest Widow or hovercraft - a
  hovercraft at sea first - and MIRAGE's SEAS (their medium-difficulty infantry upgrades, Ia08) got through meanwhile.
  `Play07` guards the place. Base game as the Dragon Clan then waited 40 minutes for one Widow standing at 543,436
  "walking": **movement bug** - a big unit counted a corner of its way as reached from 1.5 x its radius, turned early into
  the obstacle the corner goes round, slid, was "stuck", got the same way again (`Unit.Steer`): now the early arrival
  needs a clear line to the next point.
* **14 Prisoners** (MIRAGE, bot): MIRAGE's map makes the main jail invulnerable at sc_7042 until both side parts are
  below 1200 hit points (05a-05c); the bot hit the main part only (the other tribes won by the cheat's damage reaching
  the side parts). `Play14` breaks the side parts first. Sweep of MIRAGE's maps for buildings made invulnerable until
  others are damaged: only this one.
* **2 Druid Island** (MIRAGE, Dragon Clan, bot): MIRAGE's Dustriders (Giraffe from the first saved cave) attack the base
  while the heroes are away; the Dragon Clan's fireplace (4000 hit points, the Norsemen's 5000) fell and 02_game_over
  (no producer) lost the mission. `Play02`'s workers repair the base.
  Player report "buildings stayed at low hp and became untargetable" (MIRAGE): the mission's design, not a bug -
  `l_sequence_buildings_inv` makes the Dustriders' temple and rodeo, two farms (below 500 hit points) and two tents
  (below 150) invulnerable, visible but neither hitable nor selectable (OBAP flags=1) and gives them to player 2: the
  camp stays standing, burning, for the ending sequence sc_1089, and 02_L02MQ03_acc counts player 1 beaten without them.
  The same "below N hit points: invulnerable" pattern is in most missions of both configurations (heroes, mission
  buildings). What the remake did wrong there: the player's units ordered onto such a building kept swinging at it
  (combat ValidTarget: an unhittable target ended only automatic fights) - now an unhittable target that cannot be hurt
  either ends a player's fight too (BuildingsLowHpTests).
* **3 Amazon Island** (MIRAGE): the temple guards' three tents by the temple (aje_tent_44..46 of the player "Temple
  guards", `_PN_SP03_GUARDS`; base mission 3's guards have none) are neither hitable nor selectable on MIRAGE's map
  (flags 278528: the selectable and hitable bits clear - decoded by correlation only, CAMPAIGN_FORMAT.md §6). They can
  be clicked and hovered as in the original (Kacper, 2026-11-11): `Campaign.SELECTABLE_ANYWAY` sets their selectable
  bit at the start (info window, tooltip); they stay unhittable and the guards' (test `TempleGuardTentsTests`).
* **15 New World Order** (bot): the base lies on an island cut off from the crater by a channel 8 m deep (both
  configurations): units trained there reach the stations only by ship - the bot's teleport (60 s without progress) did
  it, but a hunted, moving goal restarted the count and an attack order across the water left them standing on the
  shore; now progress is "nearer the goal or 8 m moved", and in this playthrough units more than 60 m from a hunted
  target walk there with the teleporting `go()` (`CampaignBot.HuntFarGo`). MIRAGE's Dustriders
  trained nobody (their warriors and archers are hidden until epoch 2 shows them: the bot's `produce` now takes the next
  epoch for an action hidden without a requirement of its own, and also trains spearmen, the rodeo's first unit); Taslow
  stays on the base island until the end (at a held station he fell, and the bot's buying him back at the tavern did
  not come through - not looked into); fewer workers under a small population limit.
* **12 Sea Battles** (MIRAGE, Dragon Clan, bot): the Dragon Clan's population stays at 20 in MIRAGE without its optional
  supply system (hammock 2 has visibility -2: hammock 1, epoch 3 and `xenage/supply_enabled` each add 1 - data, not a
  bug), and ten workers left room for two fire boats. Fewer workers under a small limit.
* **12 Sea Battles** (base, Norsemen, bot, 2026-11-12): 27 teleports at main eb2fdd7 (23-25 at b29c17a; the 48 of one
  series run of Nov 11 did not come again - neither alone nor after missions 0-11 in one process, at b29c17a nor at
  port/movestuck 6f4196c), none of a unit that could not get on (`PW_PLAY_STUCKLOG=1` logs each one with its state:
  docs/TESTING.md). No unit was stuck: they were the bot's own count. The fleet stood shooting a carrier for a minute
  on `hunt()`'s attack orders, and when `go()` sent it after the next carrier the count that had begun at the last
  `go()` teleported all eight dragon boats at once (moving at 7 m/s, with a way, 2-6 times a run); the army that had
  stood guard at the tower ruin was teleported when step 4 sent it home (its count had begun 1000 s before); a hero
  jostled in the crowd of 20 at the ruin 6-7 m from its point, and a chariot stood 7 m from it behind the workers and
  soldiers nearer it (Unit.QueuedBehind). `CampaignBot.go()` now: the count runs only while `go()` keeps sending the
  unit (one left alone for more than 10 s starts anew where it stands); "there" is within 6 m plus the room the group
  takes (sqrt(n) m) plus a big unit's radius, measured from the ring of free ground round a goal on blocked ground (a
  building, rocks). Teleports 27 -> 0 (base Norsemen; Dustriders 5 -> 0, Dragon Clan 7 -> 0, MIRAGE Norsemen 4 -> 0,
  SEAS 11 -> 0). No change to the movement for this: the boats' way, the avoidance and the separation were not
  involved (every teleported boat had a way, a fresh search found one, those fighting were in range and shooting).
  Re-measured on 2026-11-20 (main 964f237, `PW_PLAY_STUCKLOG=1`): 0 teleports in all seven runs (base Norsemen,
  Dustriders, Dragon Clan; MIRAGE Norsemen, Dustriders, Dragon Clan, SEAS), and no unit without progress for 20 s but
  a MIRAGE marksman shooting a moving carrier. The "doubling" is history: the base Norsemen's count by the recorded
  results was 35 (Nov 1), 9, 24, 29, 27, 27, 23 (Nov 5) - the bot's count, none a stuck unit - and the one series run
  of Nov 11 with 48 never came again; 0 since port/m12 (212a391, merged in 51aec84). Nothing left to bisect. The same
  check elsewhere: MIRAGE 13 as the Dustriders / Dragon Clan, 84 / 114 in the table of Nov 12, are 7 / 8 on main.
  The telescope tower ruin is invulnerable for 45 s when the player's units reach it: the mission's own Ga07 (Occupy,
  then Invulnerability "Timer: 45", once), as in the original - nothing keeps it longer (MissionHost.SetInvulnerable
  clears the map's flag, the timer ends it).
* **9 Perilous Path** (bot, 2026-11-12): the same count (the go() above) teleported the army guarding the launch ramp
  45 times a run: `go()` sent it to the ramp's centre, 20 m inside the rocks round it, and it stood at their edge.
  Teleports 55 / 79 / 83 -> 23 (base, the three tribes), MIRAGE Norsemen 54 -> 11, SEAS 52 -> 14.
  The rest are the bot's crossings of the sea: the map is an archipelago. **The Dragon Clan village is reachable**
  (2026-11-20, checked against the original's own data): it lies on the island of the heroes' start, and a way leads
  from the village to the start and back for every unit size (`UnreachableGoalTests.Mission9VillageReachesTheStart`).
  The altars and the airplane lie on two other islands - the west one (player 1's settlement; its bunkers
  `01_bunker01..04` face the channel to the village) and the north one (players 2 and 3, the airplane, the launch
  ramp) - parted from the village's by an 8 m deep channel and, to the north, a ridge at 50 - 61 m whose every crossing
  is 38 degrees steep or more. That is the map's own walk grid (the original editor's PaFi, converted exactly, the same
  in base and MIRAGE; the slope rule gives the same: no land link under 38.5 degrees). No bridge is the link: none of
  the map's bridges joins two of these regions (the jungle bridge, map (761 .. 873, 847) at 60 m, leads east to the
  temple island - "the temple island cannot be reached onboard a ship as it is surrounded by dangerous cliffs" - and
  is broken by sc_3025; the river bridges at (589, 386 .. 405) and (401 .. 420, 452) stand over walkable strips on the
  north island; the original's bridges have no walk surface, docs/MAPS.md "Bridges"). In the original the player
  crosses by transport boat (the Dragon Clan's `ninigi_transport_boat`, harbour and boat not filtered in this
  mission), as the Barbarians' raids come by transport ship (Adb01a, `04_transport_01..04`). Nothing changed in the
  map. The playthrough does not build boats: its `go()` teleport carries the army and the workers across (most of the
  teleports of a run). It had also built the tower at the airplane and the launch ramp with workers standing at the
  village's shore, 465 m off across the water - a builder whose way ended short set to work where it stood; it no
  longer does (docs/SIMULATION.md "Goals out of reach"), and `Play09` carries the tower's workers across too.
  Units sent from the village to a goal on another island more than 160 m off wandered about it: `Nav.Find` replaced
  such a goal by the nearest reachable cell within 160 m only (docs/SIMULATION.md "Goals out of reach"). Fixed on
  2026-11-20 (the whole map, the straight-line nearest cell; an earlier try on 2026-11-12 lost mission 6's playthrough
  and was reverted): they walk to the shore nearest the goal and stop. Playthroughs 2, 6, 9, 12, 13 and 15 re-run
  after it and the builders' rule, both configurations, every tribe: 38 of 38 won (docs/PLAYTHROUGHS.md); mission 9
  base 39 - 45 teleports a run (the bot now carries a group with no way across together and counts each unit), MIRAGE
  13 - 20; the ramp can take longer (runs of 26 - 75 game minutes) - only the workers at the ramp build it now. The bot: moving 8 m is no progress for a unit with no way (`Unit.NoWay`) - MIRAGE's workers wandered
  there, their count never ran out and the ramp was not built (MIRAGE as the Norsemen and the SEAS lost at step 9);
  `Play09` also sends workers more than 60 m from the ramp with `go()`.
* **13 The Rush** (base, Norsemen, bot, 2026-11-12): "Hold the Fort" was never accomplished at main eb2fdd7 (70
  minutes, also with the old teleport count): `CBL_Ge02#defeated` waits for the third outpost's team to fall below 3,
  and its eight SEAS units (two suits, a spider, two triceratops, a warrior, two flamethrowers) circled their
  attack-move waypoint 1121,785 at 5 m/s for 45 minutes - the head-on avoidance (port/movestuck 6f4196c) turned each
  off from those coming from across the point. Units going to the same point no longer turn off from each other
  (`Unit.Avoid`, docs/SIMULATION.md): won, and the bot's teleports 52 / 69 / 74 -> 11 / 10 / 16 (base), MIRAGE
  Norsemen 118 -> 6, SEAS 121 -> 7.
* **13 The Rush** (MIRAGE, player report 2026-11-13: "ally units ended under the bridge after the SEAS camp was cleared
  and they were coming back"). The allied Dustriders' `team_citydefender` walks by the mission's own triggers:
  CBL_Ga03 sends it (Aggressive Walk) to the SEAS outpost 1 at map (548.5, 474.7), CBL_Ga05 back to (820.2, 350.1).
  The way back crosses the ravine east of the outpost over the strip of ground between the two city bridges
  (`hcl13_bridge_01`, map (604.5, 476.9) and (602.1, 503.1), deck 24 m; the strip 23 m, map y 482 - 496) - the only
  walkable link there on the map's walk grid, as in the original (docs/MAPS.md "Bridges": the bridges are scenery over
  the river bed, which the walk grid blocks, and have no pathfinder boxes). Headless (`BridgeTests.Mission13AlliesComeBack`,
  the team sent back from the cleared outpost; also 45 minutes of the mission with the computer players, the ally's own
  attack on the outpost and its return): no unit ever stood on a blocked cell (ravine, rocks, water) - the way followed
  the strip's edge and cut its north-east corner at map (619 - 621, 481 - 483), on the bank (22 m) 4 - 6 m from the first
  bridge's axis, 2 m below its deck: under its east end, beside the ravine's terrace stones (`sav_rock_terrace_stone_*`,
  CDecoObj: no collision in the original either). Fixed generally (docs/SIMULATION.md "Bridges"): the ground
  just under a deck without pathfinder boxes is blocked unless that cuts a way. The test data has no drawn box of the
  bridge (no model file): the test gives it one (36 x 10 m, east - west); the real box comes from the player's converted
  model.
  Bridges a mission places itself now block their pathfinder boxes (mission 6's footbridges, mission 7's river
  bridges; `Campaign.NO_BLOCK` named them): playthroughs base 6 and 7 as the Norsemen won (21 / 8.9 min, 5 / 4
  teleports). Missions 9 and 13 play as before with the test data (their bridges have no box there).
* Bot helpers (`CampaignBot`): `own()` maps a third tribe's class too (the helpers name Norse classes - a mission played
  with its own tribe got `hu_stone_cottage` back: the Dustriders of mission 6 never built a tent); an upgrade that costs
  more than the stores hold gets its stock (a counted top-up; the Dustriders' epoch 3 costs 700, their stores hold 500).
* Not changed, checked: MIRAGE's computer players' gifts follow its AiModuleControlDefault.usl Think (amounts, epoch
  factors, not player 0, from difficulty 2) and AiImprovementMgr's SpawnResources; the medium-difficulty TechFilters of
  MIRAGE mission 7 (Ia08: the SEAS infantry upgrades) are the map's.
* Fixed (seen on the way): the army screen's default army of mission 11 played as the Norsemen (the level's Hu preset,
  10800 points) could not be paid from the mission's 3000 least credits, so Cole, Béla, Stina and Tesla were dropped and
  the mission was lost at once (Trigger_GameOver: a hero dead); mission 5 as the Norsemen lost Béla (2400 of 2200),
  MIRAGE's 4-3 as the Dustriders / SEAS a card. The budget is now never less than the fixed preset's price
  (`CampaignArmy.Of`; test `PointBuyTests.CampaignArmyKeepsThePreset`: every mission, tribe and configuration).
* Won with a step check failing on the way (counted as played): mission 11 in MIRAGE (the Atroxosaurus is level 5 there,
  the JS check wants 4).

Fixed on the way (2026-11-01): a query's `obj_class` may be a `|`-separated list - the maps MIRAGE edited write
classes like `obj_type` ("hu_stone_cottage|", "Bela_s0|", "ninigi_corsair|ninigi_transport_boat|", 140 queries in 17 of
MIRAGE's missions); they matched nothing, so MIRAGE's tutorial never gave the player the cottage to repair and
mission 8's undead never became a group (`Registry.ClassList`; not in the JS, whose campaign data were the base
game's maps). The playthrough bot itself: dialogue scenes now last as in the game (the mission UI's logic in test mode,
as the JS ran its real UI with `?manual`: with scenes ending at once the tutorial's Gaa39_0 was enabled after its
dialogue had ended and never fired); MIRAGE's tutorial asks for four cottages (the bot reads Gca11's count) and its
skulls fall short of the first promotion (topped up, counted); `actionFor` takes the action of the class's id first
(MIRAGE's aje_huge_farm names aje_small_farm among its results); the teleport cheat no longer puts a land unit into
the open sea (no free ground within 80 m of the goal: it stays - that sank mission 7's SEAS convoy before it set out
and the quest could not be finished; the JS bot had the same hole).

## 9. Scripted waves and the classes they ask for

A wave of the triggers (AIFT with `custom_attack`, docs/AI.md) names an army of the slot's `Singleplayer` table
(ai.json `armies[tribe].Singleplayer[attack_type]`); without `attack_with_all` the computer player trains it (the units
of those classes its pool has are taken first), with it the units appear at their producing building (or anywhere with
`ignore_locations`).

**MIRAGE mission 3 "Amazon Island", medium** (player report: the wave asks the Amazons for 3-4 dilophosaurs, gives up
after 80 s and never attacks). Trigger `06m_small_attacks_at_turtle_behavior` (8-13 min, one of its two actions at
random) orders `SP2Attack_1` = 3-4 `aje_dilophosaurus` (easy: `06e`, the same army; hard: `06h`, PureViolence 2 and
Suicide 2), the same table in both configurations. The data: the dilophosaurus is made at `aje_small_farm` (100 food,
level 1) and only by a farm in small-farm mode - its build action is hidden (`visibility 0`) until the farm's local
upgrade `aje_small_farm` (the mode switch) shows it, and `localflags aje_small_farm`; the medium and huge modes
(`aje_medium_farm` / `aje_huge_farm`: ankylosaurus, stegosaurus ... / brachiosaurus ...) hide it again. MIRAGE's tree
differs from the base game's only in that the modes need no epoch (base: medium farm age 2, huge farm age 3) and in
its `personal` / `supply` flags (the optional supply system), so MIRAGE's Amazons switch farms earlier. In the test run
(seed 2) player 1 has 4 small farms and 2 medium farms when the wave comes - the dilophosaurus *can* be made, but the
army pyramid has no free place in the first row (29 pool units): no farm can queue it ("level"), and nothing would
change that within the squad timeout.

What the original does then (Ai/tasks/AiTaskBuildSquad.usl, the same in Base and MIRAGE; goals/AiGoalSingleplayerAttack
.usl): the squad task requests the missing units and starts a timer (500 / 400 / 300 ticks = 100 / 80 / 60 s at Easy /
Medium / Hard); at the timeout it calls `SpawnUnits` - the cheat manager makes units only on multiplayer maps that are
not custom maps (AiImprovementMgr.usl:139), so in the campaign nothing appears -, cancels its requests and reports itself
**running with the units it has**; the Singleplayer goal then adds 0-4 / 0-6 / 0-8 pool units (`QueryUnits`, fBad 1 /
0.75 / 0.75) and sets out. A request the economy rejects outright, or a solution it cannot make, makes `GetUnits` fail,
and the goal attacks with 1-4 / 1-6 / 2-8 pool units at once. The original never gives up because a class cannot be
trained. The port (from the JS) ended such a squad as "squad failed" unless 60 % of it was there - not in the scripts.
**Fixed** (src/Sim/Ai/Attack.cs `AttackBuild`): after the timeout the wave goes with what it has plus its pool units;
classes the tribe has no build action for are an empty need (nothing is ordered for them, `AttackNew` / `StartAttack`).
Mission 3 now: SP2Attack_1 ordered at 8:01, sets out at 9:22 with 3 pool units (`WaveTests.Mission03Sp2Attack`). Other
goals (Blitz, Suicide, PureViolence ...) keep their minimum of pool units (difficulty.squad).

**Sweep** (`WaveTests.WaveClassesSweep`: every mission of both configurations, every scripted wave, each class of its
army judged for the slot's player at the start of the mission; 365 wave classes, 170 base, 195 MIRAGE): **no wave asks
for a class its player can never produce** (every class has a build action, a producing building the player has or can
build, and an upgrade that shows it). What they need: an epoch (age_2 53, age_3 69, age_4 15, age_5 2), an invention
(`infantry_war` 16, `hu_big_animal_farm` 2), or a farm mode (26 rows, all the Dustriders': dilophosaurus and velociraptor
handler in small-farm mode - missions 3, 7, 20, 41; ankylosaurus and stegosaurus in medium-farm mode - missions 2, 3,
7), the rest are there from the start. Whether the player has that epoch, mode or a free place in the pyramid when the
wave comes is the game's matter: the wave goes with pool units after the timeout, as above. (Spawned waves skip a class
whose producing building is missing, as MakeUnit does.)

Playthroughs of the missions with Dustrider waves after the change (2, 3, 7; every tribe of both configurations, 2026-11-01;
the result files were not rewritten): all won except MIRAGE 2 as the Dragon Clan (as before) and MIRAGE 7 as the
Norsemen (lost at 9:11, "Mission has failed" - the same with the old squad rule switched back on, so not this change;
the table above still shows the earlier win). MIRAGE 2 is now won as the Norsemen and the SEAS.


**Mortal objects** (remake, 2026-11-12): objects a mission keeps unclickable and invulnerable that can be clicked,
attacked and destroyed from the start, in single player, co-op and with any tribe - `Campaign.MORTAL` (MIRAGE 13: the
tower before the gate, ninigi_small_tower_121, which the original frees only after dialogue 4071) plus a co-op
definition's `mortal` names (docs/COOP.md). `Campaign.IsMortal`: the map's flags are overridden at placement, and a
trigger's ObjAppear / Invulnerability cannot lock them again (MissionHost). Test: CoopPhaseTests.MortalTower13.

## 10. Custom campaigns (the campaign menu's second tab)

Kacper's wish (2026-11-19): the campaign menu has two tabs (`game/src/UI/CampaignMenu.cs`, PwTabs as the Options
window; docs/MENUS.md "Campaign menu"). **Campaign** lists the original campaign only - the tutorial and missions 1 - 16
of the configuration (`MissionGroups.Original`: keys `tutorial`, `single_NN`). **Custom campaigns**
(`game/src/UI/CustomCampaignsTab.cs`) lists every other mission, grouped, a header per group:

| group | what | from |
|---|---|---|
| **Booster Pack** | 4-1 The Devil's Advocate, 4-2 A Cold Day In Hell, 4-3 Operation Neptune (ids 41-43, keys `single_bNN`) | index.json; base: `BoosterPack1/Maps/...`. In MIRAGE these ids are MIRAGE's own copies, which stay hidden (`CampaignStatus` Hidden, Kacper's wish of 2026-11-11), so the group is not shown there |
| **MIRAGE** (MIRAGE only) | the SEAS versions of 4-1 .. 4-3 (20-22, `single_bNN_seas`) and "Seeking Ada" (23, `single_seas_02`) - the label adds the tribe where a title is the Booster Pack's | index.json, maps of the mod's own folder (`MIRAGE/Maps/...`) |
| any other data folder | a mission of another folder of the configuration | index.json, grouped by the folder |
| **Remake missions** | the remake's own missions in the map format, shipped in `game/missions/<name>/` (Outbreak - StarCraft II's, every seat in epoch 2: docs/OUTBREAK.md) - the same for every configuration, played as campaign missions (the token `remake:<name>` in GameStart.MissionFolder: `RemakeMissions`; co-op through their `coop.lua`) | `src/Sim/Campaign/RemakeMissions.cs`; the UI's `MissionEntry.Load` appends them |
| **The remake's campaigns** | Containment (1964 - 1966), Scenarios: Start opens their own menus (SequelMenu / ScenarioMenu, Back returns to this tab; Containment is only here - the title screen keeps the Scenarios button) | docs/SEQUEL.md |
| **My maps** | the user's maps with triggers (the former "Custom maps …" menu; map.lua `requires` respected), an edited copy of a campaign mission shown as "<the original's title> (edited)" | docs/MAP_EDITOR.md "Edited missions and custom campaigns" |
| **Campaign: <name>** | the user's campaigns and their missions in order | `campaigns/<name>/campaign.lua` in the user folder |

**Which missions belong to a configuration** (`MissionGroups.Belongs`): the toolkit's campaign export fills gaps of the
mission list from every data folder of the installation (pwexport/campaign.py `campaign_maps`: "folders outside the
configuration only fill gaps"), so on an installation with MIRAGE the official game's index.json can list the SEAS
missions 20-23 with their maps in `MIRAGE/Maps/...`. They need MIRAGE's SEAS and are not the official game's: a mission
whose map's data folder (index.json `map`'s first part, else `map_source`) is not one of the configuration's `folders`
is left out of both tabs. (The test content's base index does not list them; the test adds them as the toolkit would.)

**Continue** (the end screen) plays the next mission **of the same group** (`MissionGroups.NextInGroup`): mission 4 ->
5 (the original's own rule, CampaignMgr.usl), 16 -> the end (before: MIRAGE's index went on to 20), 4-1 -> 4-2 -> 4-3 ->
the end (before: 4-3 -> 5), 20 -> ... -> 23 -> the end; at a group's end the campaign menu opens on the group's tab. The
army budget of the next mission (`CampaignCredits`, `Campaign.Next`) is unchanged: 4-3's bonus still goes to mission 5.
Retry and the next mission keep MIRAGE's game options (`GameStart.Gameplay`; they were dropped before).

**User campaigns** (`src/Sim/Campaign/CustomCampaigns.cs` `UserCampaign`): a folder `campaigns/<name>/` in the user
folder (`UserCampaign.Dir`; `PW_USER_CAMPAIGNS` overrides it) with `campaign.lua` - data only, read with LuaData as
map.lua (no function, no call):

```lua
return {
  format = "pw-campaign", version = 1,
  name = "Rift Patrol", description = "...", author = "Kacper",
  missions = { "My Holy City", "single_seas_02", "single_b41_seas" },
}
```

A mission name is found (`Resolve`) in the campaign's own `maps/` (an imported campaign's), then in My maps (a folder
or `<name>.pwmap`), then among the configuration's missions by key. `Problems` names what is wrong (no name, no
missions, a mission not found, a map that needs another configuration); the menu shows them and does not start a
missing mission. Missions are played in order: mission *i* is open when *i* missions were won (`IsOpen`, `AfterWin`;
settings.json `customCampaignWon`: campaign folder -> missions won; the debug options open all). A mission of the
configuration plays as a campaign mission (its army screen, `CampaignMenu.PlayMission`), a map as a custom map
(`CustomPlay.Play`: CustomMission.Prepare); `GameStart.CustomCampaign` (campaign, index) marks the game - saved with
it - so a win opens the next mission and the end screen's Continue plays it (`CustomPlay.Won`, `CustomPlay.Continue`).

**Sharing**: Export writes one `.pwcampaign` (a zip): `campaign.lua` and `maps/<name>.pwmap` of every map it plays (a
map folder zipped); the configuration's missions are not packed (every installation has them). Import unpacks it into
a new folder (a free name: "Name (2)" ...): campaign.lua checked (data only), only `maps/*.pwmap` entries taken, a path
outside the campaign or a damaged map refuses the file and leaves nothing behind.

Tests: `tests/PwRemake.Tests/CustomCampaignTests.cs` (`dotnet run CustomCampaign`): the grouping of both
configurations' index.json (and the SEAS missions left out of the official game), the next mission in a group,
campaign.lua read / write / checks (data only), the unlock order, the .pwcampaign round trip (the same map content,
"(2)", a file outside the campaign and a damaged map refused), an edited copy recognised (folder and .pwmap; a map
without the original's .ula is not).
