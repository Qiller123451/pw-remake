# Architecture

ParaWorld (SEK / Sunflowers, 2006) rebuilt with **Godot 4.7 (.NET)**, **C#** and **Lua**. The rules come from the
original game's tech tree and server scripts, the look from the original's models, textures, interface art, sounds
and music, converted from the player's own installation. It continues the browser remake of the ParaWorld Toolkit
(`remake/` in <https://github.com/Qiller123451/pw-exporter>): the same rules, ported to C#, with campaign maps that
are Lua.

```
ParaWorldRemakeGodot/
├─ game/                       the Godot project (open game/project.godot in the Godot .NET editor)
│  ├─ project.godot, ParaWorldRemake.csproj, ParaWorldRemake.sln
│  ├─ scenes/                  Boot.tscn (the start), few others - most UI is built in code
│  ├─ src/App/                 start-up, logging into Godot, settings, the game session (sim + view + UI)
│  ├─ src/View/                drawing and sound: terrain, water, models, parts, animations, effects, audio
│  ├─ src/UI/                  menus, HUD, mission UI (quests, dialogues, cutscenes), debug options and tools
│  ├─ src/Editor/              the map editor in the game (docs/MAP_EDITOR.md)
│  └─ lua/                     the Lua library every map gets (std/*.lua: the trigger syntax, helpers)
├─ src/Sim/                    the game without the engine (a plain .NET library, no Godot types)
│  ├─ Core/                    logging, JSON, deterministic math and random numbers
│  ├─ Content/                 game configurations (official / MIRAGE), where assets come from
│  ├─ Rules/                   the original tech tree evaluated at runtime, typed rule queries
│  ├─ World/                   World, entities, players, navigation, the simulation step
│  ├─ Systems/                 the rules by topic (combat, economy, construction, production, moves ...)
│  ├─ Ai/                      the computer player (port of the original's script AI)
│  ├─ Maps/                    the map format (PwMap, reader, writer, converter of the .ula), terrain data, forests
│  ├─ Campaign/                a mission's world: setup, players, objects, regions, tribe swap
│  ├─ Scripting/               Lua host (sandboxed), Game API for scripts, the trigger engine
│  ├─ Editor/                  the map editor's document, commands, undo / redo, brushes, terrain, objects, players (no Godot)
│  └─ Commands/                everything a player does, as data (the seam for co-op and multiplayer)
├─ src/Net/                    networked games: TCP, lobby, host ordering turns, lockstep client (docs/MULTIPLAYER.md)
├─ src/Server/                 the dedicated server (console)
├─ src/ThirdParty/MoonSharp/   the Lua interpreter (C#, BSD-3), vendored as source
├─ tests/PwRemake.Tests/       tests of the simulation without the engine (dotnet run)
├─ tools/prepare/              makes a configuration from an installation: missions to Lua, config.json
└─ docs/                       this documentation
```

## The three layers

| layer | where | knows | does not know |
|---|---|---|---|
| **Simulation** | `src/Sim` | rules, world state, the trigger engine, Lua | Godot, the screen, the mouse |
| **View** | `game/src/View` | the simulation (reads it), Godot | input, menus |
| **UI / App** | `game/src/UI`, `game/src/App` | both, Godot | - |

The simulation is a plain .NET library. The view reads its state every frame (positions, animations, part states,
events) and never changes it; the interface changes it only by **commands** (below). That split is what makes the
later goals possible: a dedicated server is the simulation without the view, a replay is a list of commands, co-op
is two players' commands on one simulation.

## The simulation step

The world advances in **fixed steps of 50 ms** (20 per second, as the JS remake and close to the original server).
The app accumulates real time × game speed and runs as many steps as are due; the view interpolates between the
last two steps.

One step (`World.Step()`):

1. the commands due at this step (from the local player, the computer players, later the network)
2. delayed actions and periodic effects (`World.Later`, data-driven, saved with the game)
3. auras and timed states, tech tree refreshes
4. every unit's task, every building, projectiles, separation, removal of the dead
5. computer players (their own schedule inside the step)
6. the campaign: world events are forwarded to the trigger conditions, then the trigger engine runs its queue (the
   order of the original: conditions invalidate, actions run at the end of the step - docs/TRIGGERS.md)
7. events for the view and the interface (`World.Events`: built, died, hit, quest changed, dialogue started ...)

### Determinism

Everything inside a step is deterministic: the same game data + the same commands = the same game on any machine.
Rules for code in `src/Sim` (docs/CODE_STYLE.md has the list): doubles, the world's `SimRandom` only, no iteration
over hash containers where the order matters, no wall clock, no threads, no engine calls. Lua runs inside the step
on MoonSharp (managed code: the same on every machine). Across machines also: the elementary functions are
`SMath.Sin` ... (FdLibm.cs, never System.Math's), numbers as text use the invariant culture (`SimCulture`) -
docs/MULTIPLAYER.md "Determinism". Tests run the same mission twice and compare a hash of the world (`tests:
determinism`); networked games compare their machines' hashes every 5 s.

### Commands (the seam for co-op and multiplayer)

Every player action is a command object (`src/Sim/Commands`): move, attack, build, produce, use a move, set a rally
point, change diplomacy, use a debug tool ... The interface never calls the world's methods for an order directly; it
submits a command to the session's `ICommandSink`:

* **single player** (now): the local sink queues the command for the next step;
* **co-op / multiplayer** (docs/MULTIPLAYER.md): the session's sink sends it to the host, which orders everybody's
  commands into turns a few steps ahead; every machine runs the same steps with the same commands (the classic RTS
  model - a hundred units cost no bandwidth);
* **replays** (later): the commands of a game written to a file; playing them back is the same game.

What the original player and a co-op partner control is a per-player question in the world (`Player.ControlledBy`);
the campaign's "player 0" can be shared by two humans or split (two human slots of the mission) - see
docs/ROADMAP.md.

## Game configurations and content

The title screen asks for the **game configuration**: the official game (Norsemen, Dustriders, Dragon Clan) or
MIRAGE (adds the SEAS as a playable tribe and the mod's rules). A configuration is a folder `content/configs/<id>/`
(docs/CONTENT.md): `config.json` (which Data folders of the installation, which tribes are playable, where the
converted data is), `missions/` (the campaign as Lua mission folders) and `overrides/` (own assets).

All files are looked up through `AssetSource` by a relative path: the overrides first, then the converted data
(made today by the toolkit's pipeline from the installation - models as glTF, textures, interface art, rules as
JSON), then the installation itself for sounds and music. The game never refers to a game file by an absolute path,
so the original's assets can be replaced piece by piece later (docs/CONTENT.md "Own assets").

## Maps are Lua

A map (campaign mission today, custom maps later) is a folder of Lua and pictures in the remake's map format
(docs/MAP_FORMAT.md; the original maps are converted once by Prepare), its triggers Lua (docs/LUA_MAPS.md):

```
missions/11_single_11/
├─ map.lua             the map and the mission: titles, players, start, budget (`mission = { ... }`), level settings
├─ terrain/            height.png (16-bit), materials.png, walk.png, plants.lua, forest.lua
├─ objects.lua, regions.lua   the map's placed objects and regions
├─ world.lua           objects, regions, groups, quests, question marks, variables, dialogues, sequences (data)
├─ triggers.lua        the triggers, converted 1:1 from the original map - readable and editable
├─ custom.lua          (optional) own code: anything the triggers cannot express
└─ texts/uk.lua        the mission's texts
```

`triggers.lua` is declarative:

```lua
trigger "Gg01: Spawn Aje Group" {
  folder = "Root/G: Gameplay",
  once = true, by_difficulty = true,
  conditions = { Timer{ timer_id = 0 } },
  actions = {
    SpawnGroup{ difficulty = "easy",   classes_0 = "aje_warrior 2 3", group = "aje_wave_1", pos = "512 0 300" },
    SpawnGroup{ difficulty = "medium", classes_0 = "aje_warrior 2 5", group = "aje_wave_1", pos = "512 0 300" },
    SetTrigger{ trigger = "Gg02: Wave dead", state = "on" },
  },
}
```

Each condition and action type (`SpawnGroup`, `Region`, `Dead` ... the original's 23 + 44 types, docs/TRIGGERS.md) is
a **C# class**, written once. Lua only says which ones a trigger combines - when the map loads, the file becomes C#
trigger objects and the trigger engine runs them exactly as the original's engine did. Nothing is written twice.

For what the types cannot express there is code, still in Lua: an action `Script(function(api) ... end)` and a
condition `Check(function(api) return ... end)`, and `custom.lua` for whole mechanics. Scripts see only the **Game
API** (`api.units.spawn`, `api.vars.get`, `api.quests.set` ...): simple flat calls, no engine objects. Lua runs in a
sandbox (no files, no network, no `os`, an instruction limit), so a map downloaded with a multiplayer game cannot
harm the computer it runs on (docs/LUA_MAPS.md "Sandbox").

The trigger menu of a map editor (later) reads and writes the declarative part; hand-written code shows there as a
"custom script" block.

## Logging and debugging

docs/LOGGING.md: one logger with categories (`Log.For("Campaign.Triggers")`), levels per category in `logging.json`
(changeable in the running game), a log file per run in the user folder (`logs/`), the in-game log window (F10), an
error banner (click: copy the error report with the last lines of the log), and trace switches for the trigger
engine (which trigger fired, which condition changed). docs/DEBUG.md: the debug options before a game and the debug
tools in it - the same set as the browser remake.

## Porting status

The C# code is a port of the browser remake's JavaScript, file by file. docs/PORTING.md lists every JS file, where
its C# counterpart is, and what is done.
