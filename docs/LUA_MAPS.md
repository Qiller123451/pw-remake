# Maps in Lua

A map of the remake - today the 17 missions of the original campaign (and the booster pack and MIRAGE missions),
later custom maps - is a folder. Its triggers are Lua: readable, editable with any text editor, and still run exactly
as the original's trigger engine ran them. This file is for map makers: the folder, the trigger syntax, every
condition and action type with its parameters, Lua code of your own, the Game API and the sandbox. How the engine
works inside: [TRIGGERS.md](TRIGGERS.md).

## 1. The mission folder

```
content/configs/<config>/missions/
├─ index.json                the campaign menu: the missions in playing order
└─ 11_single_11/
   ├─ mission.json           what the menu shows and the setup needs
   ├─ world.json             objects, regions, groups, quests, question marks, variables, dialogues, sequences
   ├─ triggers.lua           the triggers (converted 1:1 from the original map)
   ├─ custom.lua             optional: your own code (loaded after triggers.lua)
   └─ texts/uk.json          the mission's texts (key -> text); other languages beside it (de.json ...)
```

The folders of the original missions are made by `tools/prepare/campaign_to_lua.py` from the toolkit's mission
export (`python3 campaign_to_lua.py <json_dir> <missions_dir> [--force]`). Re-running it rewrites `triggers.lua`,
`mission.json`, `world.json` and `texts/`; it never touches `custom.lua` - put your changes there, or copy the folder
under a new name.

`mission.json`: `id` (0 = tutorial), `key`, `folder`, `title`, `description`, `tribe` (the player's: Hu, Aje, Ninigi,
SEAS), `point_buy` (the army-building screen), `min_credits`, `map` (the original `.ula` below the installation's
`Data` folder: the terrain), `lang`, `map_info` (start time, camera, weather, ...), `players` (slots 0-7 with tribe,
team, diplomacy, resources, tech filters, start army, start location). `index.json` holds the menu fields of every
mission.

`world.json`: `objects`, `groups`, `question_marks`, `regions` (+ `region_tree`), `quests`, `variables`,
`dialogs`, `sequences`, `refs` (GUID -> what it names), `trigger_tree`, `trigger_defaults` (the parameter defaults
of every node type - the engine fills in what a node leaves out) and the export's `warnings`. The format of the
entries is the toolkit's (`docs/CAMPAIGN_FORMAT.md` §10 of the ParaWorld Toolkit). Positions are map metres: x east,
y south, z up, from the map's north-west corner.

## 2. Triggers

```lua
trigger "Gg01: Spawn Aje Group" {
  guid = "kbplfliilmiaknbbgjelaamaghejdoap",   -- only needed if other triggers address it by GUID
  folder = "Root/G: Gameplay",                -- folder path (SetFolder switches folders on / off)
  enabled = true,                             -- on at the start of the mission
  once = true,                                -- switched off when it fires (a SetTrigger can switch it on again)
  by_difficulty = true,                       -- each action runs only on its own difficulty
  expr = "1 && (2 || !3)",                    -- how the conditions combine (1-based numbers); empty = all of them
  conditions = {
    Timer{ timer_id = 0 },
    Region{ rgn_guid = "…", obj_owner = 0, obj_type = "CHTR|", obj_count = ">0" },
    Var{ varname = "wave", operation = "==", value = 3, ["local"] = 1 },
  },
  actions = {
    SpawnGroup{ difficulty = "easy", classes_0 = "aje_warrior 2 3", group = "…", pos = "512 300 0" },
    SpawnGroup{ difficulty = "hard", classes_0 = "aje_warrior 2 5", group = "…", pos = "512 300 0" },
    SetTrigger{ trigger = "Gg02: Wave dead", state = "on" },
  },
}
```

Fields of a trigger (all optional except the name): `guid`, `folder` (default `"Root"`), `description`, `enabled`,
`once`, `random` (one randomly chosen action runs per firing), `by_difficulty`, `node_off` (its folder starts
switched off), `expr`, `conditions`, `actions`. A misspelt field is an error that names the file and line.

How a trigger runs (the original's rules, [TRIGGERS.md](TRIGGERS.md)):

* only an **enabled** trigger whose folders are all on listens; its conditions start when it is enabled (a `Time`
  counts from that moment, a `True` fires at once);
* when a condition changes, the expression is evaluated at once; if it is true, the trigger **fires**: its actions
  run at the end of the current game step, in their order, after the actions of triggers that fired before it;
* a `once` trigger switches itself off when it fires; enabling it again (`SetTrigger`) starts it again from the
  beginning. A trigger without `once` fires again whenever a condition changes while the expression is true (a
  `Time` without `once` fires every `duration` seconds);
* an action that finds nothing to work on does nothing; an action or condition that fails is logged and the mission
  goes on.

**Parameters** are the original's: strings, numbers, `true` / `false` (= 1 / 0). Numbers and strings are the same
thing to the engine (`obj_owner = 1` and `obj_owner = "1"` are equal). A parameter left out has its default
(`trigger_defaults` in world.json, which are the original editor's: e.g. `obj_owner = -2` "any owner",
`rgn_guid = "UniqueWorldRegion"` "the whole map"). Lists inside one parameter keep the original's separators (`|`,
new lines `"\n"`, tabs `"\t"`). A name that is not a Lua identifier is written `["local"] = 1`.

Every action can have `difficulty = "easy" | "medium" | "hard"` (or 0 / 1 / 2; default medium): with
`by_difficulty` it runs only on that difficulty. Every node can have a `note = "..."` (kept, not used).

Conditions and actions are written with their **readable name** (`Region{...}`) or the original's **4-letter code**
(`REGN{...}`). `Timer` and `Diplomacy` are both a condition and an action; the list they stand in decides. A type the
engine does not know is written `Node("XXXX"){ ... }` (it is ignored with a warning).

### 2.1 Object queries

Most types select objects with the same parameters (the original's `CObjFinder`). Several queries in one node are
told apart by a prefix: none (the subject), `dst_` (the targets of `ObjDo`), `B_` (the second set of `Sight` and
`IsAttacking`), `trgt_` (`CreateObj` position), `sub_` (the transporters of `Transport`).

| parameter | default | meaning |
|---|---|---|
| `rgn_guid` | `UniqueWorldRegion` | the region the objects must be in (the whole map by default) |
| `obj_name`, `obj_guid` | `NA` | one named object or group (a group = its members). Then the filters below are not used |
| `obj_type` | `All` | `\|`-separated object types: `CHTR` characters, `ANML` animals, `VHCL` vehicles, `SHIP`, `BLDG`, `DCCO` scenery ... `All` = all real things |
| `obj_owner` | `-2` | player 0-7; `-2` any, `-1` without owner |
| `obj_class` | `NA` | exact class (`hu_worker`) |
| `exclude_class` | – | `\|`-separated classes to leave out |
| `from_condition` | `-1` | actions: take the objects condition number i (0-based) of the same trigger found (`Region`) |

**Count expressions** (`Region.obj_count`, `BuildingCount.value`, `GroupCount.check_val`, `PlayerAttrib.attrib_value`,
`Var`): `>=n`, `>n`, `<=n`, `<n`, `==n` or `=n`, `!=n`; a bare number `n` means `>=n`; `n` may be `$(variable)`.

### 2.2 Conditions

| name | code | parameters | true when |
|---|---|---|---|
| `Time` | TIME | `duration` (s), `reset` (1), `show` (0: 1 = countdown on the HUD), `countup` | `duration` seconds after the trigger was enabled; without `once` again every `duration` s; `reset = 0`: a switched-off timer keeps the time it had left |
| `RandomTime` | RTME | `Min`, `Max` (s) | after a random time `max(Min, random(0 .. Max-1))`; restarts without `once` |
| `Timer` | TIMR | `timer_id` (0) | the level timer with that id ran out (`Timer` action); also heard while the trigger is off |
| `True` | TRUE | – | at once when the trigger is enabled ("a sub-routine": other triggers call it with `SetTrigger`) |
| `Region` | REGN | query, `obj_count` | the number of objects the query finds passes `obj_count`. `obj_type = "GROU"` + a group: the whole group is in the region |
| `Dead` | DEAD | query | one of the objects died / was deleted, or the query finds nothing at all ("already dead or never existed") |
| `Dying` | DYIN | query | one of the objects died (stays true); never for an empty query |
| `ObjAttrib` | OBJP | query, `attrib_name`, `attrib_value` (`<35`, `>1`, `=Open` ...), `attrib_max` | the attribute of the object that changed last passes the test: `hitpoints` (with `attrib_max = "maxhitpoints"`: percent), `level` (0-based), `GateState`, `CurTask`, any data attribute |
| `Item` | ITEM | query, `item_class` (`item_whatever` = any) | an object of the query takes such an item (or carries one when the trigger is enabled) |
| `BuildingCount` | BLDG | `rgn_guid`, `class`, `owner` (0), `value`, `variable` | finished objects of the class and owner in the region pass `value`; the count is also written to `variable` |
| `Sight` | SGHT | query, query `B_` | an object of the first query sees one of the second (checked every 2 s) |
| `IsAttacking` | ISFG | query, query `B_` | at the moment an object of the first query attacks one of the second |
| `WaypointReached` | WAYR | query | an object of the query reached the end of its `Waypoints` path (mode 0) |
| `GroupCount` | CKGR | `group_guid`, `check_val` | the member count of the group passes `check_val` (`<1` = "the spawned wave is dead") |
| `Tutorial` | UNTT | `condition_type` (`CAME` camera, `USEL` selection, `TRMO` boarding), `camera_event`, query (`B_`) | the player moved the camera / selected a unit of the query / boarded |
| `Var` | CVAR | `varname`, `operation` (`==` compares text, `>` `<` `=` numbers; none = `>=`), `value`, `local` (1 = level variable, else profile) | the variable passes the test |
| `QuestState` | QUES | `quest_guid` / `quest_name`, `dest_state` (0 visible, 1 accomplished, 2 failed, 3 none of them) | the quest is in that state |
| `PlayerAttrib` | PLYR | `player_id` (0), `attrib_name` (`food` `wood` `stone` `iron` `units`), `attrib_value` | the attribute passes the test |
| `Tech` | TECH | `filter`, `player` (0) | the player's tech tree has the filter (an epoch reached: `Filters/Hu/Upgrades/hu_fireplace/age_2`; an item used) |
| `PlayerDefeated` | PLDE | `player_id` (0), `check_pyramid` (1), `check_producer` (1), `check_bldgs` (0), `check_none` (0) | the player lost an object and has nothing left (no unit in the limit, no producer / no building ...) |
| `Diplomacy` | DIPL | `plyr1`, `plyr2`, `relation` (0 hostile, 1 neutral, 2 friendly) | `plyr1`'s relation towards `plyr2` |
| `SequenceEnded` | SQEN | `sequence_name` | that sequence ended (until another one ends) |
| `DialogEnded` | DSEN | `dlgscene_name` | that dialogue scene ended (for the rest of the mission) |
| `GroupInRegion` | GREG | `group`, query, `obj_count` | MIRAGE: the members of the group the query finds pass `obj_count` |
| `Check` | CHCK | a Lua function | your function returns true (§3) |

### 2.3 Actions

| name | code | parameters | does |
|---|---|---|---|
| `SetTrigger` | TRIG | `guid` (or `trigger` = name), `state` (1 / `"on"` enable, 0 / missing / `"off"` disable, 2 / `"toggle"`) | switches a trigger on / off; switching on an enabled trigger changes nothing |
| `SetFolder` | ACND | `nodename` (folder path + `/`), `deststate` (1 on, 0 off) | switches a folder: off = all triggers below stop (they keep their `enabled`) |
| `Win` | QUIT | – | the mission is won |
| `GameOver` | GAOV | `reason` (text key) | the mission is lost; the defeat screen comes 4 s later with the reason |
| `Bonus` | BONI | `points` | bonus points (army budget of later missions) |
| `Feedback` | FDBK | `msg_text` | a designer's debug note: written to the log only |
| `Sequence` | SQNZ | `sequence`, `quit` (1 = won at its end), `snap_cam_back`, `snap_actors_back`, `camera_data`, `disable_fow`, `fow_pos`, `fow_radius` | plays a cutscene (queued); `SequenceEnded` at its end |
| `Dialog` | DGSC | `scene` | plays a dialogue scene (queued; the game goes on); `DialogEnded` at its end |
| `Quest` | QUES | `quest_guid` / `quest_name`, `dest_state` (0 show, 1 accomplished + bonus, 2 failed, 3 hide) | changes a quest |
| `QuestionMark` | QMRK | `questionmark`, `questionstate` (`STATE_INVISIBLE`, `QM_STATE_YELLOW`, `QM_STATE_GREEN`, `QM_STATE_RED`, `EC_STATE_YELLOW`), `questiontooltip` | a question mark's look and tooltip |
| `MapPing` | MPNG | `id`, `add_remove` (1 removes), `pos` + `pos_objquery_flag`, or query; `owner` (`All`, a player, `trigger obj`), `colortype` (`SPMainQuest`, `SPOptQuest`, `SPHint`, `Attack`, `FixedColor` + `fixedcolor` `r:g:b`), `extended`, `time_to_life` (ms, 0 = permanent), `num_repeats`, `ms_between` | a minimap marker |
| `Timer` | TIMR | `timer_id` (1), `event` (`create` (re)starts, `pause`, `unpause`, `kill`), `duration`, `show` (1), `tooltip` | a level timer; `Timer` conditions hear it run out |
| `Sound` | PSND | `soundname`, `player` (0; -1 everybody), `position` | plays a sound event |
| `InfoBar` | INBA | `text` = `key<TAB>arg1<TAB>arg2` (args may be `$(variable)`); empty removes it | the line at the top of the screen (`%1`, `%2` of the text are the args; refreshed when a variable changes) |
| `ObjDo` | ACDO | query, `action`, `additional_params`, query `dst_` | every object does `action`: `WalkAction` (`"[x y z] \| speed"`: the gait 1 slowest .. 4 run, at most the unit's maxspeed - the original's `/Speed=n`; gait 1 is about 1.5 m/s, 2 about 5, 3 about 7), `Aggressive Walk`, `Attack` (the nearest `dst_` object), `SetPos`, `RotateTo`, `SetAnim` (`"name \| loops"`), `Invulnerability` (`Enable`, `Disable`, `Timer: s`), `WeaponRemove`, `AbortTask`, `Stop`, `Kill`, `FullHeal`, `Open Gate`, `Close Gate`, `Auto Gate`, `SetRallyPoint`, `GiveItem`, `BuildUp`, `JumpOffWall`, `LevelUp`, `BuildDown`, `entrench`, `PrepareUnboard` |
| `SpawnGroup` | SPGR | `classes_0` … `classes_4` (`"class[§buildup] level count\|…"`, level 1-based), `owner` (0), `pos`, `group`, `checkpyramid`, `use_spawn_obj` + query (the building they come out of), `spawn_delay` | creates units into a group, at `pos` or one by one out of a building |
| `ObjAppear` | OBAP | query, `flags` (1 visible, 2 hitable, 4 selectable, 8 / 16 / 32 construct / destruct / deconstruct; 0 = gone) | sets all appearance flags (`7` = a normal unit appears) |
| `Waypoints` | WYPT | query, `waypoints` (`"x y z\|x y z\|…"`), `patrolmode` (0 once, 1 circle, 2 back and forth), `walkspeed` (the gait, as `WalkAction`), `straightwalk` | the objects walk / patrol the points |
| `CreateObj` | COBJ | `obj_name` (the class), `obj_owner` (0), `obj_pos`, `obj_rot`, `obj_level` (0-based), `ignore_pyramid` (1), `pos_from_obj` + query `trgt_`, `buildup` | creates one object |
| `DeleteObj` | DELO | query, `maxobjs` (-1 all) | deletes the objects (no death animation; `Dead` fires) |
| `Occupy` | OCPY | query, `new_owner` (0), `chk_highlight`, `highlight_time` | gives the objects to a player |
| `ComplexCreate` | CPLX | `obj_name`, `obj_owner`, `obj_pos`, `cptlvl` (1-based), `passengers` (`"class/level\|…"`), `buildup` | a transporter with passengers |
| `AddToGroup` | ADGR | query, `group`, `add_to_group_units` (GUIDs), `selector_enabled` (1) | adds objects to a group |
| `RemoveFromGroup` | RMGR | query, `group`, `remove_from_group_units`, `selector_enabled` | MIRAGE: removes objects from a group |
| `Replace` | REPL | query, `new_obj`, `obj_level` | the first object becomes one of another class (same place, owner, group) |
| `Transport` | TRSP | query (passengers), query `sub_` (transporters), `mount` (1 board, 0 unload), `objects_units`, `subjects_units` | boards / unloads |
| `Unit` | UNIT | query, `attrib_name` (`hitpoints`), `attrib_mod` (`+n`, `-n`, `=n`) | changes hit points |
| `Effect` | EFCT | `effect_path`, `loop_value`, `pos`, `use_objquery` + query | plays a particle effect |
| `SetVar` | VARS | `varname`, `operation` (`set`, `+`, `-`, `*`, `/`), `value`, `local` (1 = level variable, else profile) | changes a variable (`set` keeps the text as it is) |
| `Resources` | RSRC | `player_id` (0), `res_rsrclist` (`"food\|+100\|0:wood\|=50\|1"` = name, change, cap) or `res_name`, `res_mod`, `res_cap` | changes resources |
| `Diplomacy` | DIPL | `changes` (lines `"p1\|relation\|p2"`, one direction each) | changes relations |
| `NeutralToAnimals` | SNFA | `player` (0), `neutral` (1 nest animals ignore the player, 2 its units ignore wild animals, 3 both) | animals and a player leave each other alone |
| `TechFilters` | TECH | `filters` (lines `"player\|1 on / 0 off\|/Filters/..."`) | switches tech tree filters (an `AntiActions` filter forbids a build / upgrade) |
| `PlayerCaps` | PLCP | `player`, `food`, `wood`, `stone` | storage limits |
| `BlockSlots` | BLSL | `player_id`, `open_1` … `open_5` (missing = 0) | unit slots per level of the army pyramid |
| `Population` | POPL | `player_id`, `limit` | population limit |
| `ActivateRegion` | ARGN | `rgn_guid`, `sub_idx` (-1 all shapes), `dest_state` | a region or one of its shapes on / off |
| `MoveRegion` | MRGN | `rgn_guid`, `pos` | moves a region |
| `FogReveal` | SFOW | `pos` (or `[0 0 0]` + query: follows each object), `radius`, `duration` (s; -1 / 0 = for good), `owner` (0) | reveals the fog of war |
| `AiBehavior` | AIBV | `player_id`, `behavior` (`Mikrobe` asleep, `Turtle`, `Dodo`, `Giraffe`, `FightOnly`, `Schnecke`), `module` (`CTRL`) | a computer player's personality |
| `AiFight` | AIFT | `player_id`, query (targets), `attack_type`, `position_edit`, `spawn_position`, `attack_with_all`, `ship_land`, `custom_attack` ... | a computer player attacks |
| `AiDefendArea` | AIDA | `player_id`, `id`, `position`, `radius`, `max_units` (0 removes) | a defence area |
| `AiLockUnit` | AILU | `player_id`, `lock` (1), `units` (GUIDs), `enable_objsel` + query | units the AI must not use (or gets back) |
| `AiAggression` | AIAM | `aggro_state` (0 stand, 1 defensive, 2 aggressive), `aggro_state_units`, `selector_enabled` + query | aggression mode of units |
| `AiRegion` | AIRG | `player_id`, `all_players`, `map_name`, `region_name`, `value`, `add_edit` | a weight in an AI influence map |
| `AiCommand` | AICM | `player_id`, `module`, `command` | a command to an AI module (unused by the campaign) |
| `AiBehaviorOld` | AIST | as `AiBehavior` | the old name of AIBV |
| `Music` | MUSI | `musicfile`, `loop` | plays a music track (unused by the campaign) |
| `Weather` | WTHR | `chgtype` | the next track of the weather program (unused by the campaign) |
| `Script` | SCRP | a Lua function | runs your function (§3) |

## 3. Your own code: `Script`, `Check`, `custom.lua`

```lua
trigger "Bonus gold for the arena" {
  enabled = true, once = true,
  conditions = {
    SequenceEnded{ sequence_name = "Cpn_single_001/sc_4009.seq" },
    Check(function(api) return api.vars.number("waves_won") >= 3 end),       -- polled every 0.5 s
  },
  actions = {
    Script(function(api)
      for _, id in ipairs(api.objects.find{ obj_owner = 0, obj_type = "CHTR|" }) do
        api.units.hp(id, api.objects.info(id).maxhp)
      end
      api.players.resource(0, "food", "+500")
      api.action("MapPing", { pos = "[256 240 0]", pos_objquery_flag = 1, id = "gift", colortype = "SPHint" })
    end),
    Win{},
  },
}
```

* `Script(function(api) ... end)` is an action; `Check(function(api) return <bool> end [, seconds])` a condition
  (checked when the trigger is enabled and then every `seconds`, default 0.5).
* `custom.lua` runs after `triggers.lua`: define more triggers, shared functions, constants. `require "name"` loads
  `name.lua` from the standard library (`game/lua/std`) or the mission folder.
* An error in your code is written to the log with file and line (`custom.lua:12: attempt to index a nil value`);
  the trigger's other actions still run and the game goes on.

## 4. The Game API

Your functions get `api` (it is also the global `api` for functions of `custom.lua`; it exists once the mission
runs, not while the files load). Objects are numbers (ids), positions map metres, players
0-7.

| call | does |
|---|---|
| `api.time()`, `api.difficulty()` (0 easy, 1 medium, 2 hard), `api.human()` | mission time in seconds, difficulty, the human player |
| `api.random()` | a random number 0 ≤ r < 1 (the game's; `math.random` is the same generator) |
| `api.log(...)`, `api.text(key)` | write to the log; a text of the mission |
| `api.win([text])`, `api.lose([reason key])` | end the mission |
| `api.action(type, params)` | run any action type now, e.g. `api.action("SpawnGroup", { classes_0 = "hu_warrior 2 3", pos = "100 100 0", owner = 1 })` |
| `api.vars.get(name)`, `api.vars.number(name)`, `api.vars.set(name, value)`, `api.vars.add(name [, n])` | level variables (`Var` conditions hear the change) |
| `api.triggers.enable(name)`, `.disable(name)`, `.enabled(name)`, `.fired(name)`, `.folder(path, on)` | triggers by name or GUID, folders |
| `api.quests.set(name, state)` (`0`/`"show"`, `1`/`"done"`, `2`/`"failed"`, `3`/`"hide"`), `api.quests.get(name)` | quests (`{name, visible, done, failed, main}`) |
| `api.timers.start(id, seconds [, show, tooltip key])`, `.pause(id)`, `.resume(id)`, `.kill(id)`, `.left(id)` | level timers |
| `api.objects.find{ query }` | ids of the objects of a query (§2.1), e.g. `{ obj_class = "hu_worker", obj_owner = 0, rgn_guid = "…" }` |
| `api.objects.info(id)` | `{ id, guid, name, class, type, owner, x, y, z, hp, maxhp, level, alive, hidden }` or nil |
| `api.objects.find_one(guid or name)`, `api.objects.group(group)` | one object; the members of a group |
| `api.objects.add_to_group(group, id)`, `api.objects.remove(id)`, `api.objects.appear(id, flags)` | groups, delete, appearance flags |
| `api.units.spawn(class, owner, x, y [, level [, group]])` | creates a unit / building, returns its id |
| `api.units.move(id, x, y [, attack])`, `.attack(id, target)`, `.stop(id)`, `.teleport(id, x, y)` | orders (`move`: at the unit's own pace) |
| `api.units.kill(id)`, `.hp(id, hp)`, `.owner(id, player)`, `.invulnerable(id, on [, seconds])`, `.anim(id, name [, loops])` | change a unit |
| `api.players.resource(p, name [, "+100"])`, `.attribute(p, name)`, `.relation(a, b [, rel])`, `.tech(p, filter [, on])` | players |
| `api.ui.infobar(text)`, `.ping(x, y [, id])`, `.unping(id)`, `.dialog(path)`, `.sequence(path [, quit])`, `.sound(name)`, `.reveal(x, y [, radius, seconds])` | presentation |
| `api.ai.behavior(p, behavior [, module])` | a computer player's behaviour |

## 5. The sandbox

A map can come from anywhere (a download, a multiplayer game), so its Lua runs in a sandbox:

* available: the basic functions (`pairs`, `ipairs`, `type`, `tostring`, `tonumber`, `select`, `error`, `assert`,
  `pcall`, `setmetatable` ...), `string`, `table`, `math`, `bit32`, `print` (to the log);
* not available: `io`, `os`, `debug`, `coroutine`, `load` / `loadstring` / `loadfile` / `dofile` (no other files, no
  compiled chunks), `string.dump`, `collectgarbage`; `require` only reads `game/lua/std` and the mission folder;
* every call from the game into Lua (loading a file, a `Script`, a `Check`) may run a limited number of instructions
  (2 million; loading 20 million): an endless loop ends with an error in the log and the game goes on;
* `math.random` uses the game's random numbers, so a mission plays the same on every machine (multiplayer, replays);
* a capitalised name that does not exist (`Tme{ ... }`) is an error naming the line.

## 6. Examples

A wave that comes every 90 seconds until a region is reached:

```lua
trigger "wave" {
  enabled = true,                        -- not once: fires every 90 s
  conditions = { Time{ duration = 90 } },
  actions = {
    SpawnGroup{ classes_0 = "aje_warrior 2 4|aje_archer 1 2", owner = 1, pos = "800 200 0", group = "WaveGroup" },
    ObjDo{ obj_name = "WaveGroup", action = "Aggressive Walk", additional_params = "[300 400 0] | 2" },
  },
}
trigger "stop the waves" {
  enabled = true, once = true,
  conditions = { Region{ rgn_guid = "…", obj_owner = 0, obj_type = "CHTR|", obj_count = ">0" } },
  actions = { SetTrigger{ trigger = "wave", state = "off" }, Quest{ quest_name = "L99MQ01", dest_state = 1 } },
}
```

A sub-routine called from two places (the original's pattern with `True`):

```lua
trigger "reward" { once = true, conditions = { True{} }, actions = { Resources{ res_rsrclist = "food|+200|0" }, Sound{ soundname = "ui_quest_accomplished" } } }
-- anywhere: SetTrigger{ trigger = "reward", state = "on" }
-- (once: it switches itself off when it fires, so every enabling runs it again; a trigger that is still on is not
-- restarted by enabling it)
```
