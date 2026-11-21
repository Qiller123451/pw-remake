# ParaWorld Remake (Godot)

ParaWorld (SEK / Sunflowers, 2006) rebuilt with **Godot 4.7 (.NET)**, **C#** and **Lua**, from the player's own copy of
the game: the rules come from the original tech tree and server scripts, the look and sound from the original's
models, textures, interface art, sounds and music. It continues the browser remake of the
[ParaWorld Toolkit](https://github.com/Qiller123451/pw-exporter) - the same rules, ported to C# - with campaign maps
that are Lua files anyone can read and change.

**No game files are part of this repository.** You need ParaWorld installed (with BoosterPack 1; MIRAGE optional).

## Getting started

1. **Install** [Godot 4.7 .NET](https://godotengine.org/download) (the ".NET" build), the
   [.NET 10 SDK](https://dotnet.microsoft.com/download), [Python 3](https://www.python.org/downloads/) and the
   [ParaWorld Toolkit](https://github.com/Qiller123451/pw-exporter).
2. **Convert the game data once** with the toolkit: start its launcher, point it at your ParaWorld folder and run
   *ParaWorld Remake → Prepare the game data* - once for the official game, and once with MIRAGE if you want to
   play MIRAGE (the toolkit's campaign menu offers it). This converts the models, textures, interface and rules.
3. **Make the game configurations**: double-click `Prepare.bat` (or `python tools/prepare/prepare.py
   --paraworld E:\Paraworld`). It finds the converted data, exports the campaign missions, converts every original map
   and mission to the remake's map format (folders of Lua and pictures under `content/configs/<config>/maps` and
   `missions`: [docs/MAP_FORMAT.md](docs/MAP_FORMAT.md)), and converts what the toolkit leaves out (films, effect models,
   the buildings' setting textures ...). Run it again after an update that changes the formats (the title screen says
   so); it converts only what changed. See [docs/CONTENT.md](docs/CONTENT.md).
4. **Play**: open `game/project.godot` in Godot .NET and press Play (F5). The title screen asks which game to play:
   **ParaWorld** (Norsemen, Dustriders, Dragon Clan) or **MIRAGE** (adds the SEAS).

## What the remake adds to the original

The remake follows the original's rules and look, and goes beyond it where that makes the game better:

- **Up to 16 players** in skirmish and multiplayer (up to 16 teams), a random jungle that grows with the player count,
  and a **colour picker** per player (any RGB colour, or the 16 preset colours) - docs/SKIRMISH.md "16 players".
- **Biomes**: any number of landscapes on one map with soft transitions (ground, grass, light, fog, sky, sounds), a
  biome brush in the map viewer and `tools/biomes` - docs/BIOMES.md; the demo map "Frost and Jungle".
- **Map editor in the game** (main menu → *Map Editor*): make a map or open one (yours, the installation's, a campaign
  mission), shape the terrain (raise / lower, smooth, flatten, plateau, roughen, ramp, level, water), paint ground,
  biomes, walkability, forests and plants, place buildings, units, animals, resources and decoration, set up players and
  map settings, draw regions, write triggers in a trigger editor - all with undo / redo; **Test map** plays it at once
  from memory (a skirmish, or a mission when it has triggers); **Save** puts it into *My maps* (the user folder's
  `maps/`), **Export / Import** share it as one `.pwmap` file. Your maps are listed in the skirmish and multiplayer setup
  (*My maps*; a MIRAGE map only in MIRAGE: map.lua `requires`; in multiplayer a player who lacks the host's map gets it
  in the lobby, Warcraft 3 style), maps with triggers play as missions under Campaign →
  *Custom campaigns* - docs/MAP_EDITOR.md (a user guide with two tutorials).
- **Custom campaigns** (Campaign → second tab): every mission that is not the original campaign, grouped - the
  Booster Pack, MIRAGE's own missions, the remake's campaigns, your maps with triggers and your edited copies of
  campaign missions ("(edited)") - and **campaigns of your own**: pick and order missions and maps, each opens when the
  one before is won, shared as one `.pwcampaign` file - docs/CAMPAIGN.md "Custom campaigns", docs/MENUS.md.
- **Co-op campaign** for 2 - 3 players (people or AI commanders), with the maps grown for the extra bases -
  docs/COOP.md. AI commanders send a squad to an ally whose base is attacked (docs/COOP.md "AI commanders"). The
  enemies are scaled for the extra seats only while the players can build: none in no-build missions and hero phases.
  In a mission with an army budget **every player buys their own seat's army** on their own machine; the host waits
  for everybody's army (at most 2 minutes) - docs/COOP.md "Armies".
- **SEAS in player colours** (white parts and painted markings; tents as outlines) and the option *Original colours*
  - docs/VIEW.md "SEAS paint"; **units behind buildings** as silhouettes in their owner's colour (the original's
  "flatshades", Options → Graphics; dimmed at night) - docs/VIEW.md "Units behind buildings"; selection circles, range
  rings and markers lie on the ground **under the buildings** (a building in front hides that part of a ring, the back
  of a building's own ring too) - docs/VIEW.md "Rings under buildings"; destroyed **vehicles leave their wrecks** (the collapse clips of their models) - docs/VIEW.md.
- **Interface**: the objectives panel (top right, Shift+J folds it), quest marks with their texts, the build /
  produce menu opening on every selection, keys A (attack), F (middle menu row), G (the last building again) -
  docs/HUD.md, docs/GAME_SESSION.md "Keys and mouse"; full-screen films and cutscenes (black bars optional).
- **Attack ranges**: a shooting building shows its range (and minimum range) round its ghost while it is placed, and
  the player's own towers, turrets, cannons and bunkers show theirs on the ground (option *Show attack ranges*,
  Ctrl+Shift+R) - docs/HUD.md "Attack ranges"; under the same option MIRAGE's **range markers** of the selected units
  and buildings (attack, alarm, sight, healing, aura; overlapping rings of a type merged into one outline, animated by
  real time) - docs/HUD.md "Range markers".
- **Sound**: a separate *Unit Sounds* volume, the original's sound limits, its crowd sounds (a fight's din, marching) and the
  animation sounds at their GSF pitch - docs/AUDIO.md.
- **The original's setting textures on buildings, by biome**: buildings, walls and built scenery wear the original's
  repainted atlases of the landscape they stand in (snow on the roofs in the Icewaste and Frost, sand in the Savanna,
  moss in the Jungle, soot in the Ashvalley) - on a mixed map the biome where each stands; buildings without such a
  texture get shader snow in snowy biomes. Option *Setting textures on buildings* - docs/VIEW.md "Setting textures".
- **Ground under every building**: each building put down levels and paints the ground under it in its tribe's look -
  the original had brushes for only some (none for the SEAS' laboratory, tents, cannons, the towers of the Dustriders
  and the Dragon Clan, MIRAGE's buildings) - docs/TERRAIN.md "Ground under buildings".
- **Faction swap**: any campaign mission with another tribe; the objectives count that tribe's buildings and units -
  docs/CAMPAIGN.md.
- **Your colour in the campaigns**: the campaign menu's *Your colour* (the skirmish palette of 16 colours and the RGB
  picker) picks the human player's colour before a mission - the original campaign, the Custom campaigns tab's missions
  (Booster Pack, MIRAGE's, Outbreak, My maps, your campaigns) and each human seat of the co-op lobby; the colours of the
  mission's other players (enemies, allies, neutral and computer players) are greyed out ("used by ..."). Remembered
  per campaign; a remembered colour another player of the next mission has gives that mission's own colour - docs/CAMPAIGN.md "Your colour".
- **MIRAGE's game options everywhere**: *Sixth stage*, *No titans*, *Flying units*, *Portal termini* and *Building limit* in the skirmish setup, the multiplayer lobby, the
  campaign menu and the co-op lobby (MIRAGE only; on / off as MIRAGE's defaults) - docs/CAMPAIGN.md, docs/COOP.md.
- **MIRAGE's building limits** (option *Building limit*): at most the tech tree's number of each building (towers 10,
  temples 5, walls 300 ...); a queued tower upgrade or Aje farm conversion counts from the moment it is queued; the build
  button greys out with "n/max" in its tooltip, and the computer player keeps to them too - docs/SIMULATION.md
  "Building limits".
- **The computer player**: at difficulty 9 on skirmish maps the cheat manager fills an incomplete attack squad for free
  as the original does; MIRAGE's computer players follow the game options (*Sixth stage*: they research it and build
  its buildings; *Flying units*: flyers join their attack squads; no titan / flyer rows when removed / off) - docs/AI.md.
- **MIRAGE's computer player heals at fountains**: its damaged units (below 60 %) walk to the nearest fountain or SEAS
  well within 200 m - fights included - heal and walk back, without being called away on the way - docs/AI.md
  "Healing at fountains".
- **MIRAGE's flying units really fly** (option *Flying units*; the MIRAGE flying units design v4): pteranodons,
  cargolifter, dirigible, zeppelins and the flying helicopter cruise at 15 m in straight lines over everything, keep
  apart, land and take off with the abilities *Land* / *Take off*; a landed pteranodon walks on its ground model; the
  wild Pteranodon and Archaeopteryx fly too - docs/SIMULATION.md "MIRAGE: flying units".
- **MIRAGE's building classes do what they do in MIRAGE**: the psi disruptor slows enemy attacks, the speaker armours own
  units, the Stygimoloch nest breeds stygimolochs, a rally point on a Tesla machine brings new units out there, the
  Norse and SEAS mines are hacked for stone, every tribe's portal terminus sends units to another (option *Portal
  termini*, without the warp gate), the SEAS warp gate counts down, the towers' turrets and the great cannon's barrel;
  the mud pit breaks legs, the sensor tower uncovers camouflage and traps, the gallimimus nest breeds, the zeppelin
  launch pad becomes a zeppelin, the titans' launch pads grow their titan on the site, the Scorpion strikes with its
  claws and burns buildings with its flamethrower, the pirate boss ship turns its
  broadside to fire its volley, pauses 9 s on a level-up and sinks part by part, buildings without construction stages
  rise out of the ground on paving tiles with MIRAGE's cranes, and fake corpses (custom_buildups.txt), the SEAS
  headquarters' ruins stay - docs/SIMULATION.md "The MIRAGE classes".
- **First-shot delay** as the original (a ranged unit's first shot after raising its weapon 0.4 s later, Béla 0.6 s),
  with a debug option to switch it off or set its time - docs/DEBUG.md.
- **Stronger walls over weaker ones**: a wall line of a stronger class builds over the player's own weaker pieces (the
  SEAS wall over the SEAS fence, the Dragon Clan's palisade over the skewers ...; each tile costs as a new one), and
  the **SEAS fence upgrade** (MIRAGE): a fence piece (or gate) turns into a SEAS wall (wall gate) as the Dustriders'
  bone palisade turns into a clay wall - wall upgrades now also give the piece the stronger class's hit points -
  docs/SIMULATION.md "Wall upgrades and overbuilding".
- **Infected look** (for missions, view only): any building, unit or animal can be drawn infected in three stages -
  rotting wood and stone, slime and glowing green veins, green fires, the game's poison clouds and flies, a blight on
  the ground (tuned per Norse building family: walls, towers, the field, the harbour ...); animals pale with torn
  wounds showing ribs and blood, and green eyes (hand-tuned wound tables for the common animals, the others from their
  skins); Norse characters pale with rotting clothes, torn cloth over wounds and green eyes (wounds per skeleton);
  riders follow their mounts, vehicles and ships rot like buildings, the undead only glow; units born of infected
  buildings (the kennel's sabre-tooths, trained units, a nest's young) are infected too; objects out of sight get their
  look ahead, a few per frame, and each look's shaders are compiled once out of sight - no spike when an infected
  village comes into view - docs/INFECTED_LOOK.md "Performance".
- **Outbreak** (Campaign → Custom campaigns → *Remake missions*; solo or co-op for up to 3): StarCraft II's "Outbreak"
  in ParaWorld - hold a 240 m base at the valley's level, enclosed by a ring of low rock ridges (north and east passes
  open, the south-west one behind a rockslide the infected break in night 2), through
  the nights against twelve infected Norse villages, burn them by day - the sun burns the infected caught in the open;
  nastier nights, a counterattack from buildings hit at night, two plague lords that hide by day and stalk their towns
  at night, every seat in epoch 2 with six workers at work and its own field; each camp has a giant fallen tree and a
  quarry that never run out - docs/OUTBREAK.md. With it, for every map:
  a **day-night cycle** (map.lua `day_night`, the map editor's settings, the trigger condition *DayPhase*, Lua
  `api.daynight`), **infinite resources** (map attribute `infinite`), **indestructible objects** (attribute
  `indestructible`) - docs/MAP_FORMAT.md - and Lua `api.units.burn` / `api.units.gather` (docs/LUA_MAPS.md).

## Documentation

| | |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | how the game is built: simulation, view, UI, commands, configurations, Lua maps |
| [docs/PORTING.md](docs/PORTING.md) | the port from the browser remake: conventions and what is done |
| [docs/SIMULATION.md](docs/SIMULATION.md) | the world, entities, the step, events, commands; movement (ways, avoidance, goals out of reach, bridges) |
| [docs/SKIRMISH.md](docs/SKIRMISH.md) | the skirmish: setup (a plain JSON object), the army budget, the end screen and statistics |
| [docs/LUA_MAPS.md](docs/LUA_MAPS.md) | for map makers: the mission folder, triggers in Lua, every condition and action, the Game API |
| [docs/TRIGGERS.md](docs/TRIGGERS.md) | how the trigger engine works (the original's semantics) |
| [docs/MAP_FORMAT.md](docs/MAP_FORMAT.md) | the remake's map format (map.lua, terrain pictures, Lua data; .pwmap): for map makers; converting the original maps |
| [docs/MAPS.md](docs/MAPS.md) | map data in the simulation: the map source, forests, the original .ula reader (the converter's) |
| [docs/MAP_EDITOR.md](docs/MAP_EDITOR.md) | the map editor in the game: every tool, keys, saving and sharing (.pwmap), playing your map, tutorials ([plan](docs/MAP_EDITOR_PLAN.md)) |
| [docs/CAMPAIGN.md](docs/CAMPAIGN.md) | the campaign: missions, the army screen, the faction swap |
| [docs/COOP.md](docs/COOP.md) | the co-op campaign: seats, hero phases, AI commanders, the grown maps |
| [docs/MULTIPLAYER.md](docs/MULTIPLAYER.md) | online games: the lobby, lockstep, up to 16 players |
| [docs/AI.md](docs/AI.md) | the computer players |
| [docs/HUD.md](docs/HUD.md) | the in-game interface: top bar, info window, command bar, pyramid, minimap, fonts |
| [docs/GAME_SESSION.md](docs/GAME_SESSION.md) | a game in Godot: loading, keys and mouse, cutscenes, performance |
| [docs/MISSION_UI.md](docs/MISSION_UI.md) | the mission's interface: quests, objectives panel, dialogues, cutscenes, films |
| [docs/AUDIO.md](docs/AUDIO.md) | sounds, music, ambience, volumes |
| [docs/TERRAIN.md](docs/TERRAIN.md) | the ground, water, grass, forests, sky and weather |
| [docs/BIOMES.md](docs/BIOMES.md) | biomes: any number of landscapes on one map, the remake's own maps, the authoring tools |
| [docs/VIEW.md](docs/VIEW.md) | drawing the original models: glTF at runtime, part flags, animations, attached parts |
| [docs/EFFECTS.md](docs/EFFECTS.md) | effects: the models' particle emitters, the game's effect models, the drawn effects |
| [docs/OUTBREAK.md](docs/OUTBREAK.md) | the remake's mission Outbreak: the map generator, the day-night waves, tuning, co-op |
| [docs/INFECTED_LOOK.md](docs/INFECTED_LOOK.md) | the infected look of buildings, units and animals: stages, looks, layers, building tunes, wound tables |
| [docs/MODDER_NOTES.md](docs/MODDER_NOTES.md) | for modders of the original game and MIRAGE: engine rules read from the exes, formulas, GSF / map format facts, known bugs (stands on its own) |
| [docs/CONTENT.md](docs/CONTENT.md) | game configurations, where assets come from, the prepare tool, own assets later |
| [docs/LOGGING.md](docs/LOGGING.md) | the log: categories, levels, files, the in-game log window, error reports |
| [docs/SAVES.md](docs/SAVES.md) | saved games: a replay of the inputs, verified by hashes; the file; what is restored |
| [docs/MENUS.md](docs/MENUS.md) | the title screen, the Options window (settings.json, applied at once), the original's menu art and texts |
| [docs/DEBUG.md](docs/DEBUG.md) | debug options before a game, debug tools in it, keys, command line |
| [docs/TESTING.md](docs/TESTING.md) | tests without the engine, headless runs, screenshots |
| [docs/ROADMAP.md](docs/ROADMAP.md) | what comes next: the remake's own campaign, co-op, online multiplayer |

## Layout

```
game/        the Godot project (scenes, C# for drawing, sound and interface, the Lua library of maps)
src/Sim/     the game without the engine: rules, world, systems, AI, campaign, Lua scripting (plain .NET)
src/ThirdParty/MoonSharp/   the Lua interpreter (C#, BSD-3)
tests/       tests of the simulation (dotnet run --project tests/PwRemake.Tests)
tools/       the prepare tool (Python), tools/mapconvert (the map converter), tools/biomes (biome maps), tools/coop, tools/sequel
docs/        documentation
content/     made by the prepare tool on your computer (not in the repository)
```

## Licence

The code of this repository: see LICENSE. MoonSharp: BSD-3-Clause (src/ThirdParty/MoonSharp/LICENSE.txt). ParaWorld
and its data belong to their owners; this project ships none of it.
