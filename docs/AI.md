# The computer player (for developers)

`src/Sim/Ai` is the port of the browser remake's computer player (`remake/src/game/ai.js`, `ai/*.js`, pw-exporter
98c7703), itself a port of ParaWorld's script AI (`Data/Base/Scripts/Ai`). The toolkit's `remake/docs/COMPUTER_PLAYER.md`
says how it plays and which numbers it uses; `remake/docs/spec/ai.md`, `ai_group6.md`, `ai_gametypes.md` are the study
of the original with line references. This file says where things are in C# and how the rest of the game talks to it.

## Files

| file | JS | does |
|---|---|---|
| `Data.cs` (`AiData`) | ai/data.js | the tables of `ai.json` (both configurations) with the built-in fallbacks: difficulty rows, gifts, workers, squads, behaviours, build lists, attack plans, armies, unit mixes, level caps, spawn levels, outposts |
| `Brain.cs` (`TribeAI`) | ai/brain.js | one brain per computer player: think timers, difficulty 0-9 and its handicaps (`Player.AiMods`), gifts, fight factors, the free epoch, behaviours, module commands, unit locks, region maps, the census |
| `Economy.cs` | ai/economy.js | build list and its realisation (one step of the planner, the resource reserve), workers, gathering, housing, storage, repair, hunting, fishing, placement |
| `Army.cs` | ai/army.js | the standing army by unit mix (or the mix derived from the tech tree), extra units, level-ups, Kindergarten, special moves |
| `Attack.cs` (`Attack`) | ai/attack.js | the strategist, the attack goals (squad → gather → walk → fight), scripted waves (AIFT), spawned armies, landings, pest patrol, scout |
| `Defense.cs` (`Pool`) | ai/defense.js | alarm, defenders, militia, workers taking cover, guard point, defence areas (AIDA), towers |
| `Walls.cs`, `Outposts.cs`, `Trade.cs`, `Naval.cs`, `Mines.cs`, `Items.cs`, `Chat.cs`, `GameTypes.cs` | the files of the same name | village wall, outposts, trade route, ship attacks and crossings, Ninigi mine belts (`MineShores`), items, chat requests, Defender / Domination |
| `AiPlayers.cs` | ai.js, main.js createBrains | the brains of a game: created for a campaign map or a skirmish, updated in the step, the trigger actions' entry points |
| `Fountains.cs` | - (MIRAGE's AiGoalUseFountains) | MIRAGE only: damaged units heal at fountains / SEAS wells and walk back (the heal lock) |
| `Hands.cs` (`IAiHands`, `CommandHands`) | - | what the brain does as a player: the world's commands |
| `World.Ai.cs` | - | `World.Ai`, the `AiUpdate` hook, AI fields on entities (`AiFlag`, `AiStuckT` ...) |

Every module is a partial class of `TribeAI`, as the JS mixed its modules into `TribeAI.prototype`; the names are the
JS names in PascalCase (`strategyThink` → `StrategyThink`). `Math.random` is the world's `Rng`, so a game is
deterministic with its computer players (test `AiDeterministic`).

## How the brain acts

* **As a player**: orders, production, building sites, level-ups, moves, cancelling, laying mines and pulling down a
  wall piece go through `IAiHands`. The default `CommandHands` makes the world's commands (`AiOrderCommand`,
  `QueueActionCommand`, `PlaceBuildingCommand`, `ChangeLevelCommand`, `LevelUpCommand`, `UseMoveCommand`,
  `LayWaterThingCommand`, `KillCommand`, `AiCancelQueueCommand`) and applies them at once - the brain needs the answer
  in the same think, as the JS did - and passes each to `CommandHands.Issued` (a host that runs the computer players
  for others sends them on). Tests use their own hands (`tests/PwRemake.Tests/AiTests.cs FakeHands`).
* **On the world directly**, as the JS did, for what only the original's engine did for the computer player: resource
  gifts, the cheat manager's food, a free epoch (`World.QueueFree`), spawned waves (`SpawnUnit`, `CompleteUpgrade`,
  `EnterTransport`), removing a wave's ships, stances, aggression states of archers on the wall.
* It reads the world's state, the rules and the world's events (`attacked {entity, by}`, `died {entity, killer}`).

## Interface

```csharp
var ai = AiData.Load(dataDir);                                       // ai.json of the configuration (or AiData.Fallback)
// campaign (main.js createBrains): every ai_ slot asleep (Mikrobe) until AIBV; difficulty 0..2, 3 = Nightmare
var A = AiPlayers.ForCampaign(world, ai, missionJson, difficulty, missionId, humanSlot).Attach();
//      AiPlayers.ForCampaign(world, ai, IEnumerable<CampaignAiSlot>, difficulty, missionId, mapOptions)
// skirmish: one brain per computer slot (level 0-9, personality or random Dodo / Giraffe / Schnecke)
var A = AiPlayers.ForSkirmish(world, ai, new[] { new SkirmishAiSlot(player, 4, "Dodo") }, mapAiOptions, human).Attach();
A.Passive = true; A.Human = 0;                                         // debug option "passive computer"
A.OnAiAttack = (brain, attack) => ...;                                 // the interface's warning
// World.Step's AI phase calls A.Update(dt) (World.Ai.cs)
```

The campaign's `IMissionHost` implementation forwards the AI trigger actions one to one (host object ids are turned
into entities by `A.Resolve`, default `World.ById`; map positions by `A.MapToGame`; AIRG regions by `A.ResolveRegion`):

| action | IMissionHost | AiPlayers → TribeAI |
|---|---|---|
| AIBV / AIST | `AiBehavior(slot, behavior, module)` | `SetBehaviour(name, module)` (module ≠ CTRL: a module command) |
| AIFT custom | `AiAttack(slot, AiAttackOrder)` | `StartAttack(AttackRequest)` (army tables, spawn, ignore_locations, ship_land, outposts) |
| AIFT auto | `AiAutoAttack(slot, targets)` | `StartAutoAttack(targets)` |
| AIDA | `AiDefendArea(slot, id, pos, radius, max)` | `SetDefenceArea` (max ≤ 0 removes) |
| AILU | `AiLockUnits(slot, units, lock)` | `LockUnits` |
| AIAM | `SetAggression(units, state)` | `World.SetAggro` |
| AIRG | `AiRegionMap(slot / -1, map, region, value, add)` | `SetRegionMap`, read by `RegionValue` (BuildModifier, FOOD, WOOD, STON, Enemy, DefensiveCoverage) |
| AICM | `AiModuleCommand(slot, module, command)` | `CallModule` |
| GAOV | `AiGameOver()` | `OnGameOver` (a flag: the JS brains had no reaction) |

Also `A.PlayerRequest(slot, from, "help" | "food" | "wood" | "stone" | "gift")` (chat / diplomacy window),
`brain.ThinkNow(module)` (one think now: debug tools, tests), `brain.Launched` / `brain.Attacks` (statistics).

## What the brain needs from the systems

Nothing is a stub any more: the calls that were stand-ins while the systems were ported in parallel call the systems
(docs/SIMULATION.md "Stubs" lists where each one went) - `PlacementOf`, `UnitTask.FullT`, `Unit.TradeHome`,
`Walls.Centre` / `WallMap.Line` / `Walls.At`, `LastHitOf`, `World.Items` / `Item.Carrier` / `Unit.Item`, `Go.Flags` /
`Go.Dom` / `Go.Def`, `PassengersOf`, `Moves.MOVES`, and the free epoch's queue item (`ProductionTime`).

## The guard point on open ground

The idle army (the pool: the village guard) stands at `GuardPoint`: a campaign's `defend_place`, else 30 m from the
main building towards the enemy (`GuardPointRaw`, as the JS). That point can lie in a cliff, a building or the sea;
the guards then walked to the nearest reachable corner and stood there packed (player report, MIRAGE mission 3: 20-30
Amazons of player 1 in a knot between the cliff and their slaughterhouse - a building units walk through, so some
stood inside it - "they just clump"). A sweep of every mission at the start (base and MIRAGE) found about 110 computer
players with the point in a cliff, a building, the water or at an edge (clearance under 4 m).

* `OpenGround(p, home)` moves the point to the nearest place with 8 m of open ground around it (less where there is
  none within 50 m), in the ground region of home (that of the nearest labelled free cell when home stands on a
  building or in the water) and at least that far from any building, also those walked over (Aje slaughterhouse,
  fields, traps). Where home has no ground region within 60 m the point stays as it was.
* The producers' rally point is the guard point, so every trained unit stopped on that one spot: `GuardThink` gives
  each idle guard its own random spot around the point once (again when the point moves); a spot in a cliff or a
  building is drawn again (at last the point itself).
* The squads that gather there (`AttackGather`, `SendHome`) use the same point; the spots inside a defence area
  (AIDA, missions 7 and 15: all their centres are open ground) use `OpenGround` of the area's own ground.

The JS has the same flaw. Test `CampaignTests.Mission03GuardOnOpenGround`.

## The village defended while its fighters hunt (fixed 2026-11-11)

`DefenseThink` raises the alarm for an enemy within 28 m of one of the village's buildings, next to a worker, or that
hit a building or a worker; the defenders come from the free pool and from squads still being put together. Two gaps
let four Barbarian warriors beat a co-op AI seat's main building down for 75 s while its fighters stood by (MIRAGE
mission 9, the Aje seat, `CoopPhaseTests.CoopHandOverAiBases`):

* the Aje's main building is a unit, the resource collector - enemies at it or hitting it were no alarm unless a
  building or worker stood near; now they are (`COLLECTOR`, also in `ReadEvents`' hitters);
* a hunt (food, skulls: `Hunt`) kept its fighters chasing a Corythosaurus 50 m away; now a hunt's units are defenders
  too when enemies (not only animals) are in the village.

(It showed when `Unit.Avoid` (docs/SIMULATION.md) changed the warriors' first steps and they went for the collector
instead of the Aje fighters.)

## A squad that cannot be trained

`AttackBuild` follows the original's BuildSquad task (both configurations): the squad's missing units are ordered and
a timer runs (Data.SquadTimeout: 100 / 80 / 60 s at Easy / Medium / Hard); at its end the wave sets out with the
units it has plus its pool units (Singleplayer: 0 to the difficulty's maximum; the other goals their minimum) - the
cheat manager's spawn of the missing units is for skirmish maps only. The JS ended such a squad ("squad failed") unless
60 % of it stood; that made MIRAGE mission 3's dilophosaurus wave never come (docs/CAMPAIGN.md §9). A class the tribe
has no build action for is not ordered (an empty need). `TrainableOf` reports the reason of the producer closest to
making the unit ("level", "cost" before "req" of a farm in another mode). Tests: `WaveTests`.

## Co-op allies and stalls (2026-11-12)

Player reports: "AI allies don't seem that smart and this one got stuck without progressing further" (a Dustrider AI
commander: workers bunched at its collector, the base stopped growing), "look into AI logic for enemies too". Measured
with the AI report (`tests/PwRemake.Tests/AiReportTests.cs`, docs/TESTING.md "The AI report": a probe per computer
player, a line a minute and a stall report): AI against AI on multi_2_jun_001 (Giraffe, level 4, 20 minutes, every
tribe against the next, both configurations) and co-op missions 1, 4, 8, 9, 13 (seat 1 passive, seats 2-3 AI
commanders Aje and Ninigi, 15 minutes).

What was wrong, and the fixes (all in `src/Sim/Ai` and `CoopCommander`):

* **The Dustrider ally never reached its second epoch** (base and MIRAGE missions 1 and 4: 8 of 10 skulls, all 13
  workers idle at full stores from minute 5 to the end). Its skull hunts (`SkullThink`) went after Megaloceros that
  ran faster than the hunters, or that the hunters could not walk to (MIRAGE 1: the mission's walls stand between the
  village and every herd but one - each attack order ended "no way", `Combat.Unreachable`, and was given again by the
  next think); a hunt then held its squad for the whole 20 minutes of an attack. Now: a hunt or support squad ends 2
  minutes after it reached its prey (4 in all, `HUNT_LIMIT`); prey is searched farther out (220 / 450 / 1,000 m - beyond
  220 m only with three fighters left at home -, then bigger animals when the pool has 8); after a hunt that brought nothing, animals that fight back (no fleeing herd)
  as big as the pool can take on; only prey the village can walk to (`Walkable`: a way from the guard point, `Nav.Find`,
  cached - the ground regions are labelled before anything is built, so they miss walls and buildings; and
  `GroundRegion` for the cheap case). A co-op AI commander with no prey it can walk to, or whose hunts failed twice,
  gets a skull a think (12 s) up to what it needs - the remake's own, like the cheat manager's food (the original has
  no Dustrider ally in these missions).
* **Requests that could never be done held their slot**: a building the mission hides (the Aje temple of mission 1) or
  one with no place: a request on top (`Extra`) leaves after three failures. Each request now says why it waits or
  failed (`BuildEntry.Why` / `Last`: place, req, site, sites, workers, producer, cost - the AI report prints it).
* **No place for a building** (a base grown full or in a forest: the Aje on multi_2_jun_001 found no place for a rodeo,
  slaughterhouse or cook house within 80 m from minute 5 on and starved for food; Hu warehouses, the SEAS tents of
  mission 13; the Ninigi harbour of mission 8): after the random tries (as before, the same random numbers) every place
  on rings out to 80 m (as far as the random tries; only a village with a main building), nearest first, from the side
  away from the enemy, on the village's ground region; a class not
  placed is not looked for again for 30 s (`PlaceFails` counts them). Harbours: the nearest shore around the base out
  to 220 m when there is none on the way to the fish.
* **Stores full, nothing to spend on**: a resource at its storage cap asks for more storage (`StorageFor`, as
  RequestMoreResourceBuilding BV:1455 on the build list's update - the JS asked only when one request cost more than
  the cap: base mission 9's Aje ally waited for its third epoch at 700 / 700 reserved with a cap of 500).
* **A small standing army in the late epochs** (skirmish and co-op allies): the army may spend what the build list
  saves up while it has fewer than 3 + 3 × epoch fighters from the third epoch on (`ArmyFloor`; the campaign's enemies
  keep the original's 3 + epoch - their waves are the scripts'). The requests of epochs 4-5 reserve 1,500-2,500 of each
  resource for minutes; the Hu stood at 10-12 fighters through its fourth epoch with 1,900 food in store.
* **Co-op allies hunt for food** (`FoodThink`, as on skirmish maps): the campaign's computer players live off the cheat
  manager's 20 food per 12 s, which left an Aje ally whose bushes were gone at 100-200 food a minute.
* **An AI commander stood by while an ally's village was raided**: it now sends a support squad (`SupportAlly`, the
  original's answer to a chat "help" request: up to 4 / 6 / 8 pool units) when 2 or more enemy fighters are within
  45 m of another seat's main building (docs/COOP.md "AI commanders"; the remake's own - the original's computer
  allies helped only when asked).

"Two mechs kept going straight at each other forever" (MIRAGE 13 co-op) was fixed on main before (`Unit.Avoid`,
docs/SIMULATION.md; `MoveDeadlockTests.HeadOnSwapGetsThrough` covers SEAS mechs).

Before (main eb2fdd7) / after (E2-E4: epoch reached at; army: fighters at 10 / 20 minutes; minutes with 2 or more idle
workers, with 3 or more stuck workers, and without a new unit, building or epoch while 300 or more resources were
banked). The co-op games are 15 minutes (army at 10):

| side | before: E2 / E3 / E4 | army 10 / 20 min | idle / stuck / no-growth minutes | after: E2 / E3 / E4 | army 10 / 20 min | idle / stuck / no-growth minutes |
|---|---|---|---|---|---|---|
| skirmish base Hu (vs Aje) | 3:15 / 9:00 / 13:19 | 21 / 11 | 0/0/1 | 3:15 / 9:15 / 17:39 | 19 / 15 | 0/0/1 |
| skirmish base Aje (vs Hu) | 3:25 / 10:24 / - | 7 / 7 | 5/0/1 | 3:25 / 8:30 / 16:49 | 13 / 17 | 0/0/0 |
| skirmish base Aje (vs Ninigi) | 3:20 / 8:30 / 16:39 | 9 / 13 | 0/0/0 | 3:20 / 8:35 / 16:39 | 18 / 14 | 0/0/0 |
| skirmish base Ninigi (vs Aje) | 4:05 / 12:44 / 16:59 | 12 / 10 | 0/0/0 | 4:05 / 12:09 / - | 13 / 14 | 0/3/0 |
| skirmish base Ninigi (vs Hu) | 3:45 / 13:14 / - | 10 / 11 | 0/0/0 | 3:45 / 11:14 / 17:04 | 17 / 17 | 0/0/0 |
| skirmish base Hu (vs Ninigi) | 3:25 / 11:19 / - | 18 / 18 | 0/0/0 | 3:25 / 9:05 / 18:29 | 12 / 20 | 0/0/1 |
| skirmish MIRAGE Hu (vs Aje) | 4:00 / 12:24 / - | 15 / 19 | 0/0/1 | 4:00 / 12:19 / 19:29 | 17 / 18 | 0/0/1 |
| skirmish MIRAGE Aje (vs Hu) | 3:35 / 12:14 / 17:14 | 15 / 7 | 2/0/1 | 3:35 / 9:40 / 19:24 | 4 / 18 | 1/0/0 |
| skirmish MIRAGE Aje (vs Ninigi) | 4:40 / 12:44 / - | 17 / 2 | 0/0/2 | 4:50 / 13:14 / - | 18 / 3 | 0/0/3 |
| skirmish MIRAGE Ninigi (vs Aje) | 3:55 / 10:59 / 15:04 | 15 / 15 | 0/0/0 | 3:55 / 9:45 / 15:59 | 15 / 18 | 0/0/0 |
| skirmish MIRAGE Ninigi (vs SEAS) | 5:10 / 13:34 / - | 8 / 11 | 0/0/0 | 5:10 / 14:04 / - | 11 / 11 | 0/0/0 |
| skirmish MIRAGE SEAS (vs Ninigi) | 4:35 / 9:55 / 13:04 | 22 / 10 | 0/0/2 | 4:35 / 8:20 / 12:19 | 19 / 18 | 2/0/0 |
| skirmish MIRAGE SEAS (vs Hu) | 6:00 / 13:54 / - | 10 / 11 | 0/0/0 | 6:00 / 12:44 / - | 10 / 4 | 0/0/0 |
| skirmish MIRAGE Hu (vs SEAS) | 3:55 / 9:20 / 13:44 | 19 / 19 | 0/0/0 | 3:55 / 9:35 / 15:14 | 19 / 16 | 0/0/0 |
| co-op base 1 Aje ally | - / - / - | 14 | 0/0/10 | 6:10 / - / - | 13 | 0/0/2 |
| co-op base 1 Ninigi ally | 3:20 / 8:50 / - | 21 | 0/0/4 | 3:20 / 8:15 / - | 24 | 0/0/2 |
| co-op base 4 Aje ally | - / - / - | 11 | 0/0/11 | 3:05 / 11:29 / - | 13 | 0/0/1 |
| co-op base 4 Ninigi ally | 3:30 / 11:39 / - | 12 | 0/1/0 | 3:30 / 11:44 / - | 8 | 0/1/0 |
| co-op base 8 Aje ally | 3:30 / 9:40 / 14:44 | 20 | 0/0/0 | 3:55 / 8:45 / - | 5 | 0/0/0 |
| co-op base 8 Ninigi ally | 5:05 / 11:39 / - | 6 | 0/2/0 | 4:25 / - / - | 0 | 0/1/2 |
| co-op base 9 Aje ally | 3:50 / - / - | 12 | 0/0/7 | 5:15 / 8:40 / - | 19 | 0/0/2 |
| co-op base 9 Ninigi ally | 6:35 / - / - | 10 | 0/0/0 | 7:20 / 13:59 / - | 10 | 0/0/1 |
| co-op base 13 Aje ally | 0:04 / - / - | 2 | 1/0/0 | 0:04 / - / - | 1 | 0/0/0 |
| co-op base 13 Ninigi ally | 0:04 / 8:50 / - | 11 | 0/0/0 | 0:04 / - / - | 1 | 0/0/0 |
| co-op MIRAGE 1 Aje ally | - / - / - | 11 | 0/0/11 | 3:40 / - / - | 18 | 5/0/6 |
| co-op MIRAGE 1 Ninigi ally | 3:15 / 8:30 / - | 23 | 0/0/0 | 3:15 / 8:25 / - | 22 | 0/0/0 |
| co-op MIRAGE 4 Aje ally | - / - / - | 11 | 0/0/11 | 3:15 / 9:00 / - | 23 | 0/0/1 |
| co-op MIRAGE 4 Ninigi ally | 3:20 / 10:04 / - | 18 | 0/0/0 | 3:00 / 13:14 / - | 12 | 0/0/1 |
| co-op MIRAGE 8 Aje ally | 3:55 / 9:35 / 14:14 | 16 | 0/0/0 | 4:55 / 10:29 / - | 9 | 0/0/0 |
| co-op MIRAGE 8 Ninigi ally | 4:20 / - / - | 0 | 0/3/5 | 4:35 / - / - | 14 | 0/2/0 |
| co-op MIRAGE 9 Aje ally | 6:40 / 10:19 / - | 15 | 0/0/0 | 4:50 / 10:54 / - | 9 | 0/0/0 |
| co-op MIRAGE 9 Ninigi ally | 4:40 / 12:39 / - | 14 | 0/0/1 | 5:10 / 12:29 / - | 10 | 0/0/0 |
| co-op MIRAGE 13 Aje ally | 0:04 / 8:20 / - | 15 | 0/0/0 | 0:04 / 13:29 / - | 9 | 0/0/0 |
| co-op MIRAGE 13 Ninigi ally | 0:04 / 6:45 / - | 4 | 0/0/0 | 0:04 / 8:50 / - | 4 | 0/0/0 |

Over all sides: the epoch 4 in 20 minutes reached by 10 skirmish sides of 14 (7 before); idle-worker minutes 7 → 3 in
the skirmishes (no-growth minutes 8 → 6; the 3 stuck minutes are Ninigi workers dug in during a raid); the Dustrider ally reaches its second epoch in every co-op mission (in missions 1 and 4 of both
configurations it never did; 10-11 minutes without growth each). Armies in missions 8 and 13 vary with the mission's
fights (the enemy waves hit the allies there; a different first minute changes who survives them).

Left as they are, with the cause:

* **Mission 8's Ninigi ally is overrun** (base and MIRAGE): the mission's Turtle player sends 35-48 fighters at it from
  minute 6; its workers dig in (`ProtectWorkers`: the Ninigi entrench while an enemy fighter is within 36 m, as the
  original DMo:118-291) and stay in the ground while the enemy razes the village around them - the "stuck workers" of
  the report. The Aje ally's support squads (3-4) are not enough; that is the mission's balance, not a stall.
* **MIRAGE mission 1's Aje ally starves in its second epoch**: the mission hides its temple, bazaar and weapons
  builder and it has no farm; it lives off the cheat manager's food and stands with 12 idle workers at full wood and
  stone until food reaches its third epoch's 1,000.
* **MIRAGE mission 13's SEAS players 3 and 4** have every building hidden by the mission and 10,000 of each resource:
  their workers stand idle by design (they send the scripted waves).
* A hunter that followed a path ending in a dead end stood there: units of a hunt north of the Aje village in MIRAGE
  mission 1 found no way back either (`Nav.Find` from (-121, 168) neither to the prey 37 m away nor home) - noted for
  the pathing work; the AI now no longer sends hunters there.

## The campaign and the skirmish

* Campaign: `CampaignBrains : IBrainFactory` (CampaignBrains.cs) is what `MissionSession` makes its brains with
  (WorldBuilder.CreateBrains picks the slots, difficulty, personality, `ai_options`, level name and Nightmare);
  `TribeAI : ICampaignBrain` takes the trigger actions from `MissionHost` (AIBV, AIFT custom / auto, AIDA, AILU, AIRG,
  AICM, GAOV; AIAM is `World.SetAggro`). The brains join the world's `AiPlayers` (`World.Ai`), whose `Update` is their
  step.
* Skirmish: `SkirmishSession.Create(setup, ula, rules, models, forest, brushes, aiJson, progress, aiControlScript)`
  (src/Sim/Game, docs/SKIRMISH.md) puts the slots' players on the map's start positions (main.js skirmishPlayers,
  skirmish.js resolveSetup; the army budget: PointBuy), the map's own objects, wells, items, the end of the game, and
  `AiPlayers.ForSkirmish` for every AI slot with its AiPBPreset army.
* "Passive computer": `World.Cheats.Ai == "passive"` (as cheats.js; `AiPlayers.Passive` still works for a game without
  debug options).

## AI against AI (SoakSkirmish, multi_2_jun_001, Giraffe against Giraffe, difficulty 4, 20 minutes; 2026-10-31)

| configuration | side | epochs | workers / army at 10 min | peak army | attacks of its plan (first) | kills / lost |
|---|---|---|---|---|---|---|
| base | Hu (vs Aje) | 2@3:05 3@8:40 4@15:19 | 15 / 21 | 22 | 9 (3:24 pyramid × 6) | 65 / 44 |
| base | Aje (vs Hu) | 2@3:10 3@9:00 4@17:49 | 15 / 10 | 17 | 3 (6:10 suicide × 8) | 68 / 49 |
| base | Hu (vs Ninigi) | 2@3:15 3@8:15 4@15:24 | 15 / 11 | 20 | 5 (4:06 pyramid × 7) | 63 / 39 |
| base | Ninigi (vs Hu) | 2@4:00 3@11:14 | 14 / 9 | 22 | 8 (4:31 pyramid × 7) | 53 / 43 |
| base | Aje (vs Ninigi) | 2@3:15 3@8:40 4@16:54 | 14 / 15 | 17 | 3 (7:40 suicide × 8) | 56 / 48 |
| base | Ninigi (vs Aje) | 2@4:10 3@10:44 4@14:24 | 14 / 11 | 26 | 9 (5:46 suicide × 6) | 57 / 29 |
| MIRAGE | Hu (vs Aje / Ninigi / SEAS) | 2@4:00-4:15 3@10:49-11:44 | 14 / 7-10 | 13-21 | 5-9 | |
| MIRAGE | Ninigi (vs Hu / Aje / SEAS) | 2@5:10-5:20 3@13:44-16:44 | 14 / 6 | 15-21 | 4-6 | |
| MIRAGE | SEAS (vs Hu / Aje / Ninigi) | 2@7:05-7:20 3@13:04-16:24 | 14 / 6-8 | 11-15 | 2-4 | |
| MIRAGE | Aje (vs Hu / Ninigi / SEAS) | 2@16:49 / 2@14:29 / none | 13 / 0 | 0-11 | 0 | |

The base timelines are those of the JS measurements above (Hu Giraffe 2@3:20 3@8:55, Aje 2@3:20 3@10:04, Ninigi
2@4:25 3@12:54) - every base side reaches epoch 2 within 15 minutes, attacks within 20 and keeps its workers busy
(remake/tests/ai_match.js's checks). The MIRAGE row of the Dustriders above is from before the fixes below.

### MIRAGE: the Dustriders' computer player never attacked (fixed 2026-11-01)

The browser remake did the same. Three causes, none of them in MIRAGE itself - two in reading MIRAGE's data, one in the
planner; all three affected every MIRAGE computer player, the Dustriders' showed it:

1. **`m_bCustommap`.** MIRAGE conditions the skirmish rows of the attack plans on `m_bMultimap&&!m_bCustommap`
   (AiGoalGeneralAttack.usl:87, 266; m_bCustommap = `CMirageAIMgr.CheckCustomAI(level, player, "CampaignAI")`: the
   level is listed in Scripts/Game/misc/CustomMapSettings.txt with a campaign AI for the player - only the campaign's
   levels are). data.js did not know the atom (unknown atoms do not hold), so every MIRAGE player fell to the campaign
   rows: the Dustriders' Giraffe row is `appended` (= no attacks), the other tribes played their campaign rows. Now
   `AiCtx.Custommap` (Data.cs atoms `m_bCustommap` / `!m_bCustommap`): false for a skirmish's brains
   (AiPlayers.ForSkirmish), true for a campaign's (TribeAIOptions.Custommap; default: not a multiplayer map); null =
   unknown keeps data.js's answer (the golden dump).
2. **The farm.** MIRAGE's tech tree has hidden Build actions `aje_medium_farm` / `aje_huge_farm` that put up an
   `aje_small_farm` in another mode, and its keys are sorted: the planner's "the action that makes BLDG/aje_small_farm"
   (economy.js resolve: the first action whose object it is) was the hidden huge farm's - refused every time, so the
   farm the Dustriders' epoch-1 army comes from (the velociraptor handler: MIRAGE's unit mix) was never built, no
   fighters, no skulls for their second epoch (10 skulls in MIRAGE). Now an object's own action comes first
   (`Rules.OwnActionOf`: the action named like the object, else a visible one, else the first) - in the planner, a
   building's build action (Construction.BuildActionOf) and the HUD's mobile-base card.
3. **MIRAGE's gifts.** MIRAGE's control think gives resources per difficulty in its own form (iFood = n ... then
   `SpawnResources("food", iFood * fFactor)`, a factor per epoch, not for player 0, from difficulty 2;
   AiModuleControlDefault.usl Think:519-587); the toolkit's ai.json reads the base game's `SpawnResources("food", 50)`
   only, so MIRAGE's gifts were all 0. `AiData.ReadControlScript` reads them from the installation's script when
   ai.json has none (skirmish and campaign; base: ai.json's own), `Gift(d, multimap, age, player)` applies them with
   CAiCheatMgr.SpawnResources (AiImprovementMgr.usl:320-365: Easy 5, Medium 20, Hard the listed amount on skirmish
   maps; + 10 on the campaign's).

After the fixes (MIRAGE, SoakSkirmish, Giraffe against Giraffe, difficulty 4, 20 minutes, causes 1 and 2):

| side | epochs | workers / army at 10 min | peak army | attacks (first) |
|---|---|---|---|---|
| Aje (vs Hu) | 2@4:40 3@14:14 | 14 / 7 | 18 | 2 (12:00 pyramid × 10) |
| Aje (vs Ninigi) | 2@4:35 3@13:04 | 14 / 11 | 16 | 5 (5:56 pyramid × 6) |
| Aje (vs SEAS) | 2@4:30 3@10:59 | 14 / 8 | 15 | 5 (10:07 violence × 5) |
| Hu (vs Aje / Ninigi / SEAS) | 2@4:05-4:40 3@11:19-12:34 | 14 / 8-12 | 17-24 | 5-6 |
| Ninigi (vs Hu / Aje / SEAS) | 2@5:10-5:25 3@14:39-16:09 | 14 / 6-11 | 15-17 | 4-6 |
| SEAS (vs Hu / Aje / Ninigi) | 2@6:55-7:10 3@15:24-16:44 | 14 / 6-10 | 12-15 | 2-3 |

Tests: `MirageAjeTests` (the atom and the farm's action; MIRAGE's gifts; 15 minutes of Hu against Aje: the farm, an
army, attacks - pyramid at 6:00, suicide at 8:10 ...).

In the campaign (MIRAGE playthroughs, 2026-11-01, with the friends' cheat; docs/PLAYTHROUGHS.md), the gifts make the
enemies stronger: missions 2 (Hu, Aje, SEAS) and 9 (all four tribes) are now won, missions 7 (Hu and Ninigi: a
Wehrspinne reaches the Tesla place, trigger X03), 14 (Aje: the main prison) and 15 (SEAS: the pumping stations) are
lost by the bot - its limits, not the AI's (the original gives the same amounts).

## Transports that stood in place (fixed 2026-11-02)

Player report (MIRAGE mission 2 "Druid Island"): "the transport turtles ... sometimes stand in place for a long time only
to start moving later". Measured headless per transport (state, passengers, how long it stands) in the island missions
of both configurations (2, 6, 12, 13, 15, BoosterPack) with crossings forced from every computer player whose home lies
on another island than the player's (tests `FerryTests`):

1. **Mission 2's turtles are not the AI but the mission script** (Root/A_AI/c_spawn_ship_attacks): turtles with troops
   aboard go from waypoint to waypoint with `ObjDo WalkAction "[x y z] | 1"`, the next one after a fixed `Time` (45 /
   107 s, 48 / 123 s), the unloading after another (30 / 157 s). `| 1` is the gait (`Action /Walk /Speed=1`, FightingObj
   HandleWalkCommand: GoTo at that gait); the times fit gait 1 exactly (every leg needs 1.4-1.65 m/s, the ships' walk_1
   is 1.53 m/s). The remake ignored the speed and sailed at 7 m/s: the turtles reached each waypoint early and stood
   35-120 s. **Fix**: the scripted walk's gait (`Order.Gait`, `Unit.ScriptSpeed`: the model's walk_n speed, at most the
   unit's maxspeed gait) for `WalkAction` and the waypoints' `walkspeed`, in every mission (scripted walks at speed 2
   now walk at 5 m/s instead of 7, as in the original; the Lua API's `move` keeps the unit's own pace). Now a laden turtle
   never stands longer than 7 s and they land at 3:06 and 5:32 after the trigger, as the script means.
2. **The SEAS never crossed.** The crossing looked for its transports in the fleet; the hovercraft is a vehicle, never
   there: every SEAS crossing (mission 15's scripted `L15_quick_attack` too) stood two minutes and ended "no transports".
   Now any own transport of the class, idle and nearest first.
3. **Boarding waited for the attack goal's think.** The crossing ran with the attack goals (every 5 s from Medium down)
   and boarded on every 7th think only (`N % 7 == 1`): the squad stood up to 30 s before the first order, then 35 s
   between the rounds. The water crossing goal thinks every `10 + r` ticks (WC:80: 2.2-3.6 s at every difficulty) and
   boards at once, again every 7th think (WC:437-469): `FerryThinks` on its own timer (`FerryWait`), the first round
   at the start of the boarding.
4. **Amphibious transports (hovercraft, Aje turtle) waiting in the water or on another island**: nobody on foot reaches
   them; the squad stood through all seven rounds (194 s in mission 12) and gave up. An idle amphibious transport now
   comes to the squad (the crossing) and to a passenger boarding it (`BoardUpdate`, also the player's), as a ship comes
   to the waterline. A lost amphibious transport ends the boarding as a ship does (`FerryLost`).
5. **A transport nobody can have**: the crossing waited two minutes (a transport "req" / no harbour: the Hu and the Aje
   in mission 12); one that could not be paid the first time was never ordered again. The original's allocation fails at
   once (WC:714-751): now at once when there is none and none can be trained (the SEAS get a free hovercraft then for
   their next crossing, WC:737-751 `/AI_Help`), the order is repeated until it is queued, and the two minutes remain for
   a transport being trained or waiting for resources (a reason).
6. **The transport class**: MIRAGE's Aje took the first transport of its tech tree, Miyagi's liopleurodon (4 places):
   of ten, six stood at the shore through the rounds. The water crossing goal and the scripted landings use the tribe's
   transport (WC:646-655, SP:476-487: hu_transport_ship, aje_transport_turtle, ninigi_transport_boat, seas_hovercraft);
   groups per transport are its places (at most ten). Whoever is still out after the seventh round goes home instead of
   standing at the shore.
7. **Hidden units in the pool**: units a mission hides (OBAP 0 = `Parked`, out of the world, they take no orders) were
   workers, guards and squad members - mission 12's SEAS crossing consisted of two hidden marksmen. The census leaves
   them out.
8. **The levels' rules** (GA:236-290, both configurations; MIRAGE adds Single B43): Single 01 and Single 06 players 1-2
   and Single 08 player 2 never cross, Single 08 player 1 one attack in two; Single 09 player 3 by ship, Single 15
   players 1-2 by ship and players 3-4 by land fail at once ("transfer") instead of standing two minutes for a
   transport; the 11 % flank by sea not in King of the Hill / Defender.
9. **A ship stopped short of the landing** (sail): it waited out the five minutes; the original takes a ship that has
   not moved as arrived (SP "swim"): with the target's land at hand it unloads, else it sails on.

10. **The aim's land** (2026-11-12, `AimIsland`): which land an attack goes to was `World.LandIsland` of the aim's own
   cell, and -1 (a bad cell: in the water - a harbour -, on a cliff, a start location on ground a building levelled)
   meant "no crossing": the squad walked at an island it could not reach (base 06's co-op raiders' cove, while seat 3's
   start cell was bad), then took a ferry once the cell was good. Now every step of a crossing (`NeedsCrossing`,
   `StartCrossing`, `LandingFor`, the ferry's and the scripted landing's arrival) uses the same land: the aim's cell,
   else the land nearest to it within 24 m (`World.LandIslandNear`), else the land of the attack's first living
   target, else the land within 60 m. (A co-op wave that cannot walk to its seat at all goes by boat or comes ashore:
   docs/COOP.md "Waves that cannot walk".)

Kept as the original's rules (intended delays): a crossing waits up to 2 minutes for a transport being trained, seven
boarding rounds of 7 crossing thinks (15-25 s each), the squad timeout before a wave sets out (100 / 80 / 60 s), the
gathering at the guard point (40 s at most), the attack limit (20 minutes; a scripted wave that cannot reach its
target by land waits for it as in the original).

## Healing at fountains (MIRAGE; `Fountains.cs`, 2026-11-18)

MIRAGE's goal AiGoalUseFountains (the design delivered to the modder, mirage_ai_fountains_v2 - not in master yet; the
base game's AI has no such goal, so it runs **only with MIRAGE's data**, `TribeAI.UsesFountains`). Every computer
player that is awake (not Mikrobe; the remake has no AI assist for humans), every game mode and difficulty, thinks every
40 ticks (8 s, on its own clock: no random number, the other timers stay as they were):

* **Who**: its characters, animals and vehicles below 60 % of their hit points - fighting or not (v2) - except those
  already on their way (task "fountain"), trapped / asleep / frozen (the "trapped" state), in a transport, flying, locked
  for scripts (AILU), and those sent within the last ten thinks. At most six per think.
* **Where**: the nearest place within 200 m on the unit's land that it may use: the map's fountains (`World.Wells`, at
  least 30 left) and the finished SEAS wells (`Building.SeasWell`, at least 30 energy) of the player and its allies.
* **The order**: "fountain" with the walk back to where the unit stood queued behind it (`UnitTask.Then`: "Q_Action
  /Walk"), through `IAiHands.HealOrder` - `AiOrderCommand.HealLock` sets `Unit.AiHealLock`.
* **The lock** (FightingObj.usl HandleGamePlayCommand): while the heal walk is on (`World.AiHealLocked`), the brain's
  other orders (`AiOrderCommand`, except kill / unload) and special moves (`CommandHands.UseMove`, except Kill, LevelUp,
  Dismount, LeaveTransport) do not reach the unit; it ends when the walk ends (healed, the place gone or empty), with
  death, and for good when the unit is trapped, asleep or frozen. The human player's orders are never held, and his units
  never get the lock.
* Tests: `LimitsFountainsTests.Fountains*`.

## MIRAGE master 2.6.8 (a349cb41; 2026-11-18)

What the ReadMe's AI section and the AI commits since the data (090b65c0 "AI_fixes", c397d064) changed, against the
remake:

| MIRAGE | the remake |
|---|---|
| "Fixed a bug that prevented Dragon Clan AI from lock picking gates" (AiTaskAttackObject / AiGoalGuardVillage CheckSpecialAttack "lockpicking": the gate found was never returned) | **ported**: `Army.MovesThink` - a fighting ninja with the move (researched from ai.json's request "lockpicking": Hard, with ninjas) picks the lock of the nearest enemy gate that is not open within sqrt(50) m (`LockpickGate`). The remake had left the move out (NoCooldown moves were skipped). |
| "You can ask AI player for help by writing "help" in the dm or allies chat"; "AI players can read group chats" (ChatWindow.usl DirectMsg / GroupMsg) | **ported**: the chat bar lists the computer players by name; lines to one of them, to the allies or to the enemies reach the computer players of that side as requests (`game/src/UI/Hud/Logic/ChatRouting.cs`, docs/HUD.md "Chat"). The answers were there (Chat.cs). |
| AiGoalAttackShips OnCalculateValue (a building that is no harbour / carrier: -999999 for short-range squads, else -2) | nothing to do: the remake's ship attack targets only ships and harbours / carriers (Naval.cs HARBOUR) |
| AttackShips / Watercrossing: FindHarbourPos (0,0,0) is not used as the rally point | nothing to do: the remake's squads gather at the harbour it has, else at home |
| BuildWorkers (a null construction info), GeneralAttack (the enemy found, not entry i; an inverted null test in GetSafeSpot), GuardVillage (`==` for `=`), IdleWorkerWhipper (`continue` for `return`), BuildVillageWalls ExistsGateInfo (false without a wall def), SingleplayerAttack (empty ifs) | script errors of the original's code paths; the remake's ports do not have them (they were written from the intent) |

## Tests

`dotnet run --project tests/PwRemake.Tests -- Ai`:
* `AiDataGolden*`: `tests/golden/ai_data_dump.mjs` runs the JS data.js in node on both configurations and without
  ai.json; ~2,700 queries each (levels, gifts, squads, behaviours, build lists, attack plans, armies, unit mixes ...):
  0 differences.
* `AiReport.Skirmish` / `AiReport.Coop` (PW_AIREPORT=1): the AI report - timelines, stalls and the before / after
  table of "Co-op allies and stalls" (docs/TESTING.md "The AI report").
* `AiTests`: decisions on a world built from the real data with recording hands; `AiDeterministic`;
  `AiAgainstAiSmoke` (two brains for three, then fifteen minutes: they train, build and attack);
  `SoakTests.SoakSkirmish` (PW_SOAK=1): AI against AI on multi_2_jun_001 for every tribe pair, a timeline per side as
  remake/tests/ai_match.js prints it (docs/TESTING.md "Soak").
* `FerryTests` (`-- Ferry`): mission 2's scripted turtle landings (both configurations: no laden turtle stands more than
  15 s, both land within 6 minutes); mission 15's SEAS hovercraft crossings (players 3-4 sail within a minute and land
  within 3, players 1-2 never cross); mission 12's Hu ship and Aje turtle crossings (sail within 90 s, land within 4
  minutes); a squad and its transports never stand still together more than 15 s while boarding or sailing. A laden
  transport shot down by an enemy within the limit after half of its way counts as a crossing done (at least one
  crossing of the mission must land): mission 15's p3 hovercraft sails its last ~80 m along the player's shore under the
  fire of the player's heroes, and whether it lands before it sinks depends on the fights of the first minute (seeds 1-8:
  sunk short of the landing 2-3 times out of 8, before and after the animation audit of 2026-11-13, which changed the
  order of the world's random numbers and so failed seed 1 - not a fault of the crossing).
