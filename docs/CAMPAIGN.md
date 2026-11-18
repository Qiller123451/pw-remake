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
| `MissionData.cs` | `MissionData` (mission.json + world.json + the TriggerDefs of LuaMission = the JS's one pw-campaign/1 document), `MissionEntry` (index.json) | setup.js loadCampaign |
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
models the view needs: `Need`, `FullTribes`; the tribe swap) -> `MapSourceOf(.ula, skip)` -> `WorldBuilder.Build` (world
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
`SequencePlay` carries the request, world.json's sequence data and the bound `SeqActors` (`PoseAt(actor, t)`,
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
(Open: MIRAGE's master has a table of fixed options per level, Game/misc/CustomMapMirageSettings.txt - e.g. Single_01
FlyingEnabled 1, MaxEpoch 4 - that its lobby's adjust windows set and lock for those maps; the remake's campaign menu does
not apply it: the menu's choice holds for every mission.)

### The army budget (`GameStart.Army`)

Missions with `point_buy` (mission.json) open the army screen before they load (`CampaignMenu.StartWithArmy`,
`CampaignArmy.Of`: the level's preset fixed, the budget max(bonus points of the mission before, the least credits), the
allowed classes, the level's row limits; docs/SKIRMISH.md "Campaign missions with an army budget"). `CampaignConfig.Army`
is placed at the human's start location instead of the level's start army (campaign/setup.js startLocation, SL:482-623);
skulls stay the level's. A won mission's bonus points (`Campaign.Boni`) are the next mission's budget (settings.json
`campaignCredits`).

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
else **NotYet** (greyed out). BoosterPack 1's three missions (ids 41-43; the menu shows them after mission 4, as
Campaigns.txt orders them) and MIRAGE's SEAS missions (ids 20-22: the SEAS versions of 41-43, single_b4N_seas; 23:
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
  The telescope tower ruin is invulnerable for 45 s when the player's units reach it: the mission's own Ga07 (Occupy,
  then Invulnerability "Timer: 45", once), as in the original - nothing keeps it longer (MissionHost.SetInvulnerable
  clears the map's flag, the timer ends it).
* **9 Perilous Path** (bot, 2026-11-12): the same count (the go() above) teleported the army guarding the launch ramp
  45 times a run: `go()` sent it to the ramp's centre, 20 m inside the rocks round it, and it stood at their edge.
  Teleports 55 / 79 / 83 -> 23 (base, the three tribes), MIRAGE Norsemen 54 -> 11, SEAS 52 -> 14.
  The rest are units that cannot get there on the map: the Dragon Clan village (where the army gathers) lies in a
  basin at the water's level cut off from the plateaus of the altars (west, 40 m) and of the airplane (north) by cliffs
  and an 8 m deep channel on the map's own walk grid (terrain alone: no object closes it; `Nav.LabelRegions` gives
  three regions) - the original's route out of the village is not known (a ship? not a bridge: no bridge of the map
  joins those regions - the jungle bridge, map (761 .. 873, 847) at 60 m, spans a gorge east of the plateau with both
  ends on blocked cells and is broken by sc_3025; the river bridges at (589, 386 .. 405) and (401 .. 420, 452) stand
  over walkable strips in the north; the original's bridges have no walk surface, docs/MAPS.md "Bridges"; the
  Norsemen's raids there come by transport ship, Adb01a). Open question, not changed. Units sent from there to a goal more
  than 160 m off wander about the basin: `Nav.Find` replaces a goal in another ground region by the start region's
  nearest cell only within 80 cells, beyond that the search fails and ends at the cell it reached nearest the goal -
  another one from each place. Not changed in the engine (searching the whole map for that cell, Found kept false, lost
  mission 6's playthrough with every tribe tried - the pirates' landings broke the base; not looked into further). The
  bot: moving 8 m is no progress for a unit with no way (`Unit.NoWay`) - MIRAGE's workers wandered there, their count
  never ran out and the ramp was not built (MIRAGE as the Norsemen and the SEAS lost at step 9); `Play09` also sends
  workers more than 60 m from the ramp with `go()`.
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
