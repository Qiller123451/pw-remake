# The trigger engine (for developers)

The campaign runs on the original's trigger system: conditions, a boolean expression, actions, enabled / once flags,
folders, the end-of-step queue. The authoritative description with the original script line references is the
toolkit's `remake/docs/spec/triggers.md` ("the spec" below); the browser remake implemented it in
`remake/src/game/campaign/engine.js`, `conditions.js`, `actions.js`, `trigutil.js` (`docs/CAMPAIGN_RUNTIME.md` §11).
`src/Sim/Scripting` is the port of those files. Map makers read [LUA_MAPS.md](LUA_MAPS.md).

## 1. Files

| file | content | ported from |
|---|---|---|
| `TriggerEngine.cs` | `TriggerEngine` (queue, enable / disable, folders, variables, profile variables, level timers, quests, info bar, sequence and dialogue queues, end of the mission, delayed jobs, debug helpers) and `Trigger` | engine.js |
| `Conditions.cs` | `Condition` (base) and one class per type: `Conditions.TIME`, `REGN`, ... (24 + `CHCK`) | conditions.js |
| `Actions.cs` | `TriggerAction` (base) and one class per type: `Actions.TRIG`, `SPGR`, ... (49 + `SCRP`) | actions.js |
| `TrigUtil.cs` | count expressions (`Compare`), `$(var)` (`ValueString`), `ToInt` (parseInt), `Vec` (parameter vectors), the expression compiler (`TriggerExpr`), `OrderedSet` | trigutil.js, setup.js vec() |
| `TriggerData.cs` | `TriggerDef`, `NodeDef`, `TriggerParams` (the string parameters with the defaults applied), `QuestState`, `MissionScript` (what the engine is built from; also read straight from pw-campaign/1 JSON) | - |
| `TriggerTypes.cs` | the one table code ↔ readable Lua name ↔ class | - |
| `IMissionHost.cs` | the interface to the game (§4) and its small data types (`ObjectInfo`, `SpawnOptions`, `MapMarker`, `SequenceRequest`, `AiAttackOrder`, `MapPos`) | the calls the JS made on G.campaign, the registry, the world |
| `LuaHost.cs` | the sandboxed MoonSharp interpreter (modules, `require`, instruction budget, error logging) | - |
| `LuaMission.cs` | loads a mission folder: JSON data, texts, `game/lua/std`, `triggers.lua`, `custom.lua`; turns `trigger "..." {}` into `TriggerDef`s; `Bind(engine)` | - |
| `GameApi.cs` | the `api` table of Lua code | - |
| `game/lua/std/triggers.lua` | the declarative syntax: a constructor per type (from `TriggerTypes`), `Script`, `Check`, `Node` | - |
| `tools/prepare/campaign_to_lua.py` | pw-campaign/1 JSON → mission folder | - |

## 2. Loading a mission

```csharp
var lm = LuaMission.Load(folder);                 // sandbox, std library, triggers.lua, custom.lua -> lm.Script
var engine = new TriggerEngine(host, lm.Script);  // C# trigger objects: one Condition / TriggerAction object per node
lm.Bind(engine);                                  // Script / Check / custom.lua get `api`; math.random = host.Random
// every simulation step (World.Step phase 6), after the world has reported the step's events:
engine.Update(0.05);
```

Lua is only the file format: `trigger "name" { ... }` calls back into C# (`LuaMission.Define`), which checks the
fields and records a `TriggerDef` (name, guid, folder, flags, expression, nodes with their string parameters). The
engine then creates the objects exactly as from the JSON (`MissionScript.FromCampaignJson`): the node type's defaults
(`world.lua trigger_defaults` = the JSON's `defaults`) under the node's own parameters, one object per node. Only
`Script` / `Check` nodes keep a Lua function; they run through `LuaHost.Call` with the instruction budget.

The round trip is tested on every mission of both configurations (`TriggersRoundTripTests`): convert, load, and
every trigger, flag, expression, node type, difficulty and parameter equals the JSON (base: 4072 triggers, 5259
conditions, 10242 actions; MIRAGE: 4249 / 5468 / 11217).

## 3. How the engine runs (spec §1)

| rule (spec) | where |
|---|---|
| §1.1 no. 1 enabled + folder active = listening; `OnEnabled` / `OnDisabled` | `Listen`, `Enable`, `Disable` |
| §1.1 no. 3 `Invalidate()` evaluates at once | `Invalidate` |
| §1.1 no. 4 level firing (every invalidation while true) | `Invalidate` (no edge detection); polled level conditions invalidate only when their result changed |
| §1.1 no. 5 `once` = disabled when it fires, re-enabled by `TRIG` | `Invalidate` → `Disable` before queueing |
| §1.1 no. 6 actions after the condition's handler: the FIFO queue at the end of the step | `queue`, `Flush` (5000 firings per step = a loop: dropped and logged) |
| §1.2 not compiled = does not exist; empty expression = AND of all; no condition = never | loaders skip `compiled: false`; `TriggerExpr.Compile` |
| §1.3 `TRIG` 1 / 0 / 2, unknown GUID = nothing; enabling an enabled trigger changes nothing | `Actions.TRIG`, `Enable` |
| §1.4 `random` (one action, `host.Random()`), `by_difficulty` (`host.Difficulty`) | `Pick` |
| §1.5 folders: a folder holding a `node_off` trigger starts off; `ACND` off covers sub-folders, on only the folder | constructor, `SetNode`, `FolderActive` |
| §8 "evaluation while a trigger is being enabled": once, after all conditions woke (mission 15 `M03a` / `M03b`) | `Listen` (`Enabling`, `Dirty`) |
| §8 no. 20 disabled in the step it was queued: still fires | the queue holds it |
| §3 variables: `set` keeps the string, `+ - * /` integers, `/ 0` changes nothing; every change notifies | `SetVar`, `VarsChanged` |
| §3 / §8 profile variables (`local != 1`): only the names of ProfileVariables.txt | `Profile`, `SetProfileVar`, `host.LoadProfile/SaveProfile` |
| §6.3 level timers: create restarts, pause / unpause, kill; expiry pushes every `TIMR` condition (also of disabled triggers) | `Timer`, `Update`, `Conditions.TIMR` (`Forever`) |
| §6.1 quests and bonus by difficulty | `SetQuest`, `Boni` |
| §6.4 info bar with `%n` and `$(var)`, refreshed on variable changes | `SetInfoBar`, `InfoBarText` |
| §6.5 / §6.8 dialogues and sequences queue; their end is mandatory (no UI = ends at once) | `PlayDialog` / `EndDialog`, `PlaySequence` / `SequenceTick` / `EndSequence` |
| §5.1 `GAOV`: the AI is told at once, the defeat 4 s later, a second one ignored; `QUIT` / `SQNZ quit` win | `Lose`, `Win` |
| mission 16: an `SQEN` enabled by a trigger that the same sequence end fired still hears it | `SeqEnd`, `Conditions.SQEN.OnEnabled` |
| mission 8: a region a live trigger names whose shapes are marked "not enabled" and that no ARGN touches is switched on | `FixRegions` → `host.EnableDisabledShapes` |

### How conditions hear the world

| channel | pushed by | conditions |
|---|---|---|
| tick list | `Update` (game time) | `TIME`, `RTME` |
| `Timer` (whole level) | a level timer ran out | `TIMR` |
| `Sequence`, `Dialog` (whole level) | `EndSequence`, `EndDialog` | `SQEN`, `DSEN` |
| `Vars` | `VarsChanged` | `CVAR`; `REGN` / `BLDG` / `CKGR` / `PLYR` with `$(` |
| `Quest`, `Diplomacy`, `Group` | `SetQuest`, `OnDiplomacyChanged`, `OnGroupChanged` | `QUES`, `DIPL`, `CKGR`, `GREG` |
| `Removed`, `Lost` | `OnObjectRemoved`, `OnObjectLost` | `DEAD`, `DYIN`; `PLDE` |
| `Attacked`, `Boarded`, `Wayr`, `Item` | `OnAttacked`, `OnBoarded`, `OnWaypointReached`, `OnItemTaken` | `ISFG`, `UNTT TRMO`, `WAYR`, `ITEM` |
| `Objects` | once per step in which `Touch()` was called (spawn, removal, owner, group, appearance, teleport, region) | `REGN`, `DEAD`, `DYIN`, `BLDG`, `GREG` |
| poll list | own interval: `REGN` / `DEAD` on a named region 0.5 / 1 s, `OBJP` 0.25 s, `PLYR` / `TECH` 0.5 s, `BLDG` 1 s, `SGHT` 2 s, `UNTT` 0.3 s, `CHCK` 0.5 s (or its own) | |

Subscriber sets are insertion-ordered (`OrderedSet`): which condition hears an event first, and so the order of the
queue, is the same on every run.

## 4. The host interface (`IMissionHost`)

The campaign port implements it on the World (objects are its registry ids, positions map coordinates, players slots).
The tests implement it as `tests/PwRemake.Tests/TriggersFakeHost.cs`. The methods, by group:

* **mission**: `Difficulty`, `HumanSlot`, `Random()`.
* **queries** (spec §2): `Select(params, prefix)`, `Matches(obj, params, prefix)`, `Info(obj)` → `ObjectInfo`,
  `DataAttribute(obj, name)`, `FindObject(guidOrName)`, `IsWorldRegion(rgn)`, `IsMissingClass(cls, owner)` (tribe
  swap), `CountFinished(cls, owner, rgn)`, `PlayerObjects(slot)`, `CountCharacters(slot, level)`,
  `UnitSlotFree(slot, level)`.
* **groups**: `FindGroup`, `GroupMembers`, `GroupOf`, `AddToGroup`, `RemoveFromGroup`.
* **objects**: `Spawn(cls, owner, x, y, SpawnOptions)`, `SpawnOutOf(source, cls, level, owner, group, target)`,
  `ApplyBuildup`, `Remove`, `Kill`, `SetOwner(obj, slot, highlight)`, `SetAppearance(obj, flags)`, `SetHitpoints`,
  `SetLevel`.
* **orders**: `OrderMove(obj, to, speed, attackMove)`, `OrderAttack`, `OrderStop`, `OrderBuild`, `OrderBoard`,
  `CanBoard`, `UnloadAll`, `EnterTransport`, `Teleport`, `FaceTo`, `PlayAnim`, `AnimLength`, `SetInvulnerable`,
  `SetDisarmed`, `SetGate`, `SetRallyPoint`, `GiveItem`, `Command(obj, "BuildDown" | "entrench" | "JumpOffWall")`,
  `PrepareUnboard`, `StartPatrol(obj, points, mode, speed, straight)` (the patrol driver of engine.js lives in the
  world: it needs the unit's task).
* **players / world**: `PlayerExists`, `Relation`, `SetRelation`, `PlayerAttribute`, `ChangeResource`,
  `HasTechFilter`, `SetTechFilter`, `SetPopulationLimit`, `SetUnitLimits`, `SetResourceCaps`,
  `SetNeutralToAnimals`, `SetRegionEnabled`, `MoveRegion`, `EnableDisabledShapes`, `RevealArea`.
* **computer players**: `AiBehavior`, `AiAttack(slot, AiAttackOrder)`, `AiAutoAttack`, `AiDefendArea`,
  `AiLockUnits`, `SetAggression`, `AiRegionMap`, `AiModuleCommand`, `AiGameOver`.
* **presentation**: `QuestChanged(quest, "shown" | "done" | "failed" | "hidden")`, `SetQuestionMark`, `ShowMarker`,
  `RemoveMarker`, `SetInfoBar`, `PlaySound`, `PlayMusic`, `NextWeather`, `PlayEffect`, `IsBusy`,
  `PlaySequence(SequenceRequest)` → bool (true = the host calls `engine.EndSequence()` later),
  `SequenceFinished(r)` (reveal, actors, camera), `PlayDialog(path, token)` → bool (true = the host calls
  `engine.EndDialog(token)`), `EndMission(won, text)`, `Camera`, `IsSelected` (tutorial).
* **profile**: `LoadProfile()`, `SaveProfile(values)`.

What the host reports to the engine (the world's events, setup.js `attach()`): `OnObjectSpawned`, `OnOwnerChanged`,
`OnBuilt`, `OnObjectRemoved` (an object is gone: died, destroyed, deleted), `OnObjectLost(obj, owner)` (a unit died /
building destroyed), `OnGroupChanged`, `OnDiplomacyChanged`, `OnAttacked(victim, attacker)`, `OnBoarded`,
`OnItemTaken(itemClass, taker)`, `OnWaypointReached`, plus `EndSequence()` / `EndDialog(token)` from the mission UI.
`Touch()` for anything else that changes which objects a query finds.

## 5. Status of the types

All 23 condition and 44 action types of the campaign are implemented, plus the MIRAGE ones (`GREG`, `RMGR`, the
MIRAGE `ACDO` commands), the unused `MUSI`, `WTHR`, `AICM`, `AIST`, the Lua `CHCK` / `SCRP`, and the remake's own
`DAYN` (DayPhase: the map's day-night cycle, polled every 0.25 s through `IMissionHost.DayNight`; docs/OUTBREAK.md). Approximations are
the JS remake's: `SGHT` (sight range of the A objects, no fog-of-war test), `UNTT` (camera polled, selection polled,
boarding event), `PLDE` ("producer" = what the host marks `Produces`), `WYPT` / `TRSP` (the world's patrol and boarding
orders), `DEAD` / `DYIN` (one moment: the death). `FDBK` writes to the log category `Campaign.Feedback` (debug).

Saved games do not write the engine's state: they replay the mission's inputs (docs/SAVES.md), so the engine (and
its Lua) is the same after loading by construction. The UI's ends of scenes come as `SceneEndCommand`s; what the engine
asks of the UI goes through the presenter (recorded). Before (no longer relevant): saving a running engine (the JS `save.js` part; Lua closures of `Script` / `Check` would be
re-created by loading the mission folder again), the debug-tools' `skipQuest`.

## 6. Debugging

* log category `Campaign.Triggers` at Debug: every firing (`[12.3s] Gg01: Spawn: SPGR SPGR TRIG`); `Lua`: errors of
  Lua code with file:line, `print()`; `Campaign.Feedback`: the designers' `FDBK` texts.
* `engine.FiringLog`, `engine.DebugPrint(n)`, `engine.DebugRow(trigger)`, `engine.DebugFire(name)`,
  `engine.DebugEnable(name, on)`, `engine.Errors`, `engine.Warnings` (unknown types, objects that could not be created),
  `engine.Timers`, `engine.Variables`, `engine.Quests`.
* tests: `dotnet run --project tests/PwRemake.Tests -- Triggers` (engine semantics, round trip, every mission two
  minutes on the fake world), `-- ScriptingLua` (sandbox, syntax, API); `python3 -m unittest discover -s
  tools/prepare/tests` (the converter). The real-data tests read `PW_CAMPAIGN_JSON` (default
  `/home/claude/testdata/campaign`, folders `base` and `mirage`).
