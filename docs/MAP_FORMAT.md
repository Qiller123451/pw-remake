# The map format

The remake's own map format, for map makers and for the map editor (docs/MAP_EDITOR_PLAN.md, phase 0). A map is a
**folder** of Lua data files and PNG pictures - readable, editable with a text editor and an image editor, diff-friendly
in git - or the same files in one zip, **`<name>.pwmap`**, for sharing. The game reads only this format: skirmish,
multiplayer, co-op and the campaign's missions. The original game's maps (`.ula`) are converted once by Prepare
(below, "Converting"); the remake does not write `.ula`.

```
<map>/
├─ map.lua              the map: name, size, setting, water, time of day, camera, players, the original's level info
│                       and settings tree, the terrain's numbers; a mission's data (`mission = { ... }`)
├─ terrain/
│  ├─ height.png        16-bit greyscale: the heights on the 2 m grid (map.lua terrain.height_min / height_step)
│  ├─ materials.png     8-bit greyscale: the ground material of each 4 m cell (an index into terrain.materials)
│  ├─ walk.png          8-bit greyscale, optional: the walk grid of 2 m cells (255 = land units can walk)
│  ├─ biomes/<id>.png   8-bit greyscale, optional: the weight of each biome (docs/BIOMES.md)
│  ├─ forest.lua        optional: the forest blocks (32 m squares of trees)
│  └─ plants.lua        the landscape decoration (plants, rocks, plateaus)
├─ objects.lua          the placed objects (buildings, units, animals, trees, stones, nests, start locations ...)
├─ regions.lua          the named regions (rectangles and ovals)
├─ minimap.png          optional: the picture of the map list (RGBA)
├─ world.lua            a mission: objects with their GUIDs, groups, regions, quests, question marks, variables,
│                       dialogues, sequences (the campaign's data - docs/LUA_MAPS.md)
├─ triggers.lua         a mission: the triggers (docs/LUA_MAPS.md)
├─ custom.lua           a mission: your own Lua code (never written by the converter or the editor)
└─ texts/<lang>.lua     a mission: its texts per language (uk = English, de, fr ...)
```

Any other file in the folder (a Lua module `custom.lua` requires, notes) is kept as it is when the map is read and
written again.

## Coordinates

The original's **map coordinates**: metres from the map's north-west corner, **x east, y south, z up** (left-handed,
docs/MAPS.md "Coordinates"). Pictures have their first row at the north (y = 0) and their first column at the west.
Rotations are quaternions `{ x, y, z, w }` as the original stores them; the heading of an upright object is
`-2 atan2(qz, qw)`, counter-clockwise in map space.

## Lua data files

`map.lua`, `world.lua`, `objects.lua`, `regions.lua`, `terrain/forest.lua`, `terrain/plants.lua` and `texts/*.lua` are
**data, never code**: one `return { ... }` of tables, strings, numbers and booleans.

```lua
-- comments are fine (--, --[[ ]])
return {
  name = "My map",                    -- a field: a Lua name ...
  ["Root/Base"] = "x",                -- ... or any string in brackets
  list = { 1, 2.5, -3, "four", true },
  nested = { { 1, 2 }, {} },
}
```

How it is enforced (src/Sim/Maps/LuaData.cs): before anything runs, a token check reads the file and allows only
`return`, table constructors (`{ }`, `name =`, `[key] =`, `,`, `;`), string literals (`"..."`, `'...'`, `[[...]]`),
numbers with an optional minus, `true` / `false` / `nil` and comments. A `function`, a call, a variable, an operator
(`+`, `..`, `#` ...), a loop or any other statement is an error naming the file and line - a data file cannot compute
anything, so it cannot run forever or reach anything. Then MoonSharp (the game's Lua) evaluates it in an **empty
environment** (no libraries, no globals), and the value is checked again: only tables, strings, numbers and booleans; a
table is either a list (keys 1 .. n without gaps) or a record (string keys) - not both; no number that is not finite.
The code a mission runs belongs in `triggers.lua` / `custom.lua`, which run in the mission's sandbox (docs/LUA_MAPS.md
"The sandbox").

Rules of the data:

* `nil` (or leaving a field out) means "none": a field the game finds missing is the same as an empty one.
* An empty table `{}` is a list, except where the format says the field is a record (a mission's `players[*].heroes`,
  `point_buy.preset`, `allowed_units.*`, the world's `dialogs[*].actors`, `sequences[*].play.cams`,
  `trigger_defaults.*` ...: `PwMapReader.MissionObjectPaths` / `WorldObjectPaths`).
* Positions, rotations and region boxes are single precision (32-bit floats, as the original's): written with the
  shortest text that reads back to the same float, read as a float. Other numbers are doubles, written exactly - in
  one spelling, whether they came from JSON (the converter: `16.0` is written `16`) or from a Lua file read before
  (`LuaData.NumText`): data read and written again is the same bytes.
* Strings are UTF-8; the writer escapes `\` `"` and control characters.
* The writer is deterministic: the same map gives the same bytes (fields in a fixed order, short tables on one line).

## map.lua

```lua
return {
  format = "pw-remake-map",          -- required
  version = 1,                       -- the format's version (a newer map is refused with a message)
  name = "Malice",                   -- the map's name (the map list, saved games)
  title = "Frost and Jungle",        -- optional: the shown name instead of the settings' Root/Base/LevelName
  description = "...",               -- optional: instead of Root/Base/Description
  author = "...",                    -- optional: instead of Root/Base/Author
  source = "Base/Maps/Multiplayer/multi_4_ice_601.ula",   -- the map's id: what it was converted from
  requires = "MIRAGE",               -- optional: the configuration the map needs (below); left out: any
  file = "multi_4_ice_601.ula",      -- the original file's name (loading screens, journal entries find it)
  size = { 1024, 1024 },             -- the header's width and height (m); the terrain's extent comes from height.png
  setting = "Icewaste",              -- the ground texture set: Northland Savanna Jungle Icewaste Ashvalley Cave1..3
  setting_name = "TestSet",          -- optional: the level's own setting name, when it differs (forest kinds)
  terrain_setting = "Northland",     -- optional: the original terrain header's setting, when it differs
  water = 16,                        -- the water level (m)
  start_time = "12:00",              -- the time of day the level starts at
  day_night = { day = 300, dusk = 30, night = 180, dawn = 30, start = 0, night_sight = 0.65, first_day = 420 },  -- optional (below)
  camera = 0.785,                    -- the camera's angle at the start (radians)
  max_players = 4,
  terrain = {
    height_min = 0, height_step = 0.0078125,       -- height = height_min + value * height_step (height.png)
    materials = { "Icewaste/scape_0.jpg", ... },   -- the ground texture of material 0, 1 ... (below assets/terrain/)
    biomes = { names = { "Frost", "Rainforest" }, cell = 8, x0 = 0, y0 = 0,      -- optional (docs/BIOMES.md)
               remap = { Rainforest = { 0, 1, 7, 3, 4, 5, 2, 4 } } },
  },
  info = { Version = "3", MapName = "...", Setting = "Icewaste", ... },          -- the original's level info
  settings = { "Root", "", { { "Base", "", { { "LevelName", "_MN_MP_4_MALICE" }, ... } }, ... } },
  mission = { ... },                 -- a campaign mission's data (below); absent on a plain map
}
```

**`day_night`** (optional; the remake's own, `World/DayNight.cs`): a real day-night cycle instead of the original's
slow virtual clock (which only switches the lights). `day`, `dusk`, `night`, `dawn`: each phase's length in game seconds
(defaults 300, 30, 180, 30; dusk and dawn may be 0), `start`: seconds into the cycle at the start (0: the day begins),
`first_day`: the first day's length (0 / left out: as every day), `night_sight`: the units' and buildings' sight at night
as a share of their range (0.7; dusk and dawn go smoothly between), `enabled = false` switches it off. The clock is a
function of the game time - nothing to save, the same on every machine. The world's time of day follows it (day 7:00 -
18:00, dusk to 20:00, night to 5:00, dawn to 7:00: the nests' timetables, the HUD), `World.IsNight()` (the buildings'
lamps and torches, the animals' sleep) is the dark half; the world emits `daynight` at each phase (`phase`, `night` =
how many dusks have begun). The game lights it smoothly (the sun sinks and turns orange at dusk, the night is dark
blue, `GameSession.DayNight.cs`) and shows a small indicator under the top bar (sun / moon, the time to the next phase).
Triggers: the condition `DayPhase`, Lua `api.daynight` (docs/LUA_MAPS.md). The map editor's map settings set it
(docs/MAP_EDITOR.md).

**`requires`**: the configuration a map needs - the mod's name (or a configuration id) - when it uses what only that
configuration has: classes the official game lacks (placed objects, a mission's world objects, classes its triggers
name), MIRAGE's game options (no titans, flying units, portals, building limits), a player slot that plays only the
mod's tribes. The map editor decides it on every save and export (`MapRequirement`, docs/MAP_EDITOR.md "Saving and
sharing"); a map converted by Prepare lives in its configuration's own folder and has none. The game's lists of another
configuration leave such a map out: the skirmish and multiplayer lists, the campaign menu's My maps; the editor's Open shows it greyed
(`MapFiles.RequirementProblem`, `SkirmishMap.Unfit`).

Fields left out are taken from `info` and `settings` as the original game did (the name from LevelName / MapName /
the file name, the setting from `info.Setting` or the terrain header, the time from `Root/Base/StartTime` - 12:00 when
missing, 8:30 when malformed -, the camera from `Root/Base/DefaultCamera`, the players from `MaxPlayers` /
`StartLocations`). The converter writes them all.

**`settings`** is the original's level settings tree, each node `{ name, value [, { children }] }`. What the game reads
from it:

| path | what |
|---|---|
| `Root/Base/LevelName`, `Description`, `Author` | the map list (text keys of the game's map names are translated) |
| `Root/Base/MaxPlayers`, `GameType` (`Deathmatch\|Domination\|`), `MapType` (`multiplayer` / `singleplayer`), `LastManStanding`, `DefenderTimer` | the skirmish setup (`MapSource.MpSettingsOf`) |
| `Root/Base/StartTime`, `DefaultCamera`, `material_atmos` | time of day, camera, ambience (the converter lifts the first two into `start_time` / `camera`) |
| `Root/PlayerSettings/Player_<n>/Restrictions/Base/Tribes` (`Hu:Aje:`), `DefTeam`, `Resources/<food\|wood\|stone\|iron>`, `Chars/Population/Max` | each start slot's tribes, team, resources, population |
| `Root/AIOptions/<option>` | what the map tells the computer players (walls, harbour, watermap, hunt_animals ...) |
| `Root/WeatherData` (`Name`, `Loop`, `Tracks/*`: `Duration`, `Type`, `Intensity`, `WindDir`, `WindStrength`) | the weather program |
| `Root/Items` (`Pool/*`, `MaxItems`) | the artefacts a map's item spawns hand out |

**`mission`** (campaign missions): the menu's and the setup's fields - `id`, `key`, `folder`, `title`, `description`,
`tribe`, `point_buy`, `min_credits`, `map` (the original map's id), `lang`, `map_info` (start time, camera, weather,
black start, AI options ...), `players` (slots 0-7: tribe, team, diplomacy, resources, tech filters, start army, start
location, point-buy presets, heroes). The toolkit's mission export defines them (docs/LUA_MAPS.md §1).

## terrain/height.png

A 16-bit greyscale PNG, one pixel per height sample of the **2 m grid**: pixel (i, j) is the ground at map (2i, 2j).
Its size gives the terrain's extent (`width x 2` by `height x 2` metres; the original's grids are a multiple of 16
samples). The height in metres is `height_min + value * height_step`.

**Quantisation.** The original stores its heights as 16-bit numbers in steps of 1/128 m (`Terr`, docs/MAPS.md), so a
converted map has `height_min = 0, height_step = 0.0078125` and its heights are **exactly** the original's - bit for
bit the same floats the `.ula` reader makes (the range 0 .. 512 m). Nothing changes: the walk grid, the buildings'
levelling, the golden tests against the JS remake (which keep reading the `.ula`) and the converted-vs-`.ula` tests
(`MapFormatTests.ConvertedMapsSameAsUla`) compare exactly, without a tolerance. Heights the editor makes that are not on
that step (or outside 0 .. 512 m) get a range of their own when the map is written: `height_min` = the lowest height
(on the 1/128 m step), `height_step` = 1/128 m, or larger when the range needs it (`(max - min) / 65535`: 2.4 mm for
a 154 m range) - every height within half a step.

## terrain/materials.png

An 8-bit greyscale PNG, one pixel per **4 m cell** (pixel (i, j) covers map 4i .. 4i + 4, 4j .. 4j + 4; half the
height grid's size): the index of the cell's ground material, 0 .. 7, into `terrain.materials` (the setting's 8
textures `assets/terrain/<Setting>/scape_<k>.jpg`). The renderer blends the cells softly (docs/TERRAIN.md); buildings
paint their own cells in the game (docs/MAPS.md "Ground").

## terrain/walk.png

Optional. An 8-bit greyscale PNG of **2 m cells** (the header's size / 2): 255 = land units can walk on the terrain
there, 0 = not (steep slopes, water). The original's map editor made it (chunk `PaFi`); the converter keeps it exactly.
Placed objects are not in it (the game blocks those itself). Without it the game uses the slope rule (32 degrees).
Values from 128 up count as walkable. The remake's map editor writes the cells painted with its walkability brush as
**192** (painted walkable) and **64** (painted blocked) and reads them back as painted (`WalkGrid.Paint`); the game
needs nothing else.

## terrain/biomes/&lt;id&gt;.png

Optional: the remake's biome layer (docs/BIOMES.md). One 8-bit greyscale PNG per biome named in `terrain.biomes.names`
(`terrain/biomes/Frost.png` ...), all the same size: the biome's weight (0 .. 255) in each cell of `cell` metres from
map (`x0`, `y0`), read bilinearly between the cell centres and normalised. `remap` (optional): per biome, which of its
materials each of the map's 8 materials becomes.

## terrain/forest.lua

Optional: the original's **forest blocks**, squares of 32 m with up to 15 trees and 16 undergrowth plants each.

```lua
return {
  w = 32, h = 32,                     -- squares per side
  blocks = {
    { 12, 7, { 75, 79, 64, ..., 0 } }, -- square (12, 7): map 384 .. 416, 224 .. 256; 31 item bytes
  },
}
```

Each item byte: bits 5-6 the state (2 = standing), bits 0-4 the amount (a tree's wood; 0 = none). **Where** the items of
a square stand is not in the map: the engine picks one of its 32 built-in layouts per square (by the square's
position) - the table `forest.json` of the game's converted data (copied from the player's PWServer.exe), with the tree
and undergrowth models of each setting. Nothing stands at or below 16 m.

Why blocks and not the trees themselves (measured on the 53 staged originals): the trees would make the map
self-contained at a small cost (the largest map, the tutorial with 124 blocks, has 1826 trees and 1945 undergrowth
plants: about 160 KB of Lua), but

* the blocks are what the game works with: the co-op extension grows new land with new forest blocks
  (`MapExtension`), the ambience hears a square as "forest" (`MapSource.SoundMaterial`), the map editor's forest brush
  paints squares (plan, phase 2);
* the layouts are a constant of the game, like its models and textures - every configuration's data has `forest.json`
  (the same table for the official game and MIRAGE), so a map is no less portable for needing it;
* the trees' kinds are per setting (`forest.json` `settings`): a block map changes with the setting, a tree list would not.

## terrain/plants.lua

The landscape decoration instances of the original (chunk `IOMG`): plants, bushes, rocks, plateaus, cliffs.

```lua
return {
  names = { "Jungle_Fern_01", "Jungle_Smallrock_07", ... },
  plants = {
    { 1, 342.5, 387.9, 42.8, -0.3, 0.95 },               -- name number, x, y, z, qz, qw (an upright plant)
    { 2, 120.2, 400.1, 31.0, -0.3, 0.95, 0.02, -0.01 },  -- tilted: + qx, qy
  },
}
```

## objects.lua

```lua
return {
  { type = "SLOC", name = "StartLocation_5", x = 244.066, y = 92.24, z = 43.589, q = { 0, 0, -0.3665, 0.93042 }, owner = 2,
    attr = { include_building = "0" } },
  { type = "TREE", name = "Savanna_Cocos_Palm_06_5", x = 342.381, y = 387.946, z = 42.796, q = { 0, 0, 0, 1 },
    attr = { hitpoints = "30" } },
}
```

| field | |
|---|---|
| `type` | `BLDG` building, `CHTR` character, `ANML` animal, `VHCL` vehicle, `SHIP`, `TREE`, `STON` stone, `FRUI` fruit bush, `NEST` animal nest, `SLOC` start location, `DCCO` / `DECO` / `VGTN` / `WOOD` scenery, `FNTN` healing well, `DMGL` lake, `ITEM` artefact, `ITSP` item spawn, `CFXE` effect, `DOFL` domination flag ... |
| `name` | the class with a number (`Hu_Warrior_12`); the class is the name without `_<number>` (or `class = "..."`) |
| `x`, `y`, `z`, `q` | position (map metres) and rotation |
| `owner` | the start slot / player 0 .. 7; left out: nobody's |
| `level` | optional, 0-based |
| `attr` | the object's attributes as strings (`hitpoints`, `spawn_type`, `spawn_max`, `region_name` ...) |

The remake's own attributes of resources (`TREE`, `STON`; the Outbreak's camps): `infinite = "1"` - harvesting never
uses it up (workers take loads as from any tree or stone; `ResNode.Infinite`, the HUD shows ∞; a building put down
next to it does not clear it); `fallen = "1"` (a tree) - it lies felled from the start: a log at once, no chopping
(drawn as its `_timber` model at rest, the tree laid down when the data lacks it); `scale = "2.6"` - drawn, blocking
and worked by more workers as much bigger (0.1 .. 10).

### Indestructible objects

The remake's object attribute `indestructible = "1"` (world.lua objects; made for the Outbreak's old walls of round 4,
which round 5 turned into rock - no map of the repository uses it now): nothing damages,
targets, sells or blows it up - hits do nothing, no unit attacks it, its owner's *Delete* / the AI's demolition is
refused (`Entity.Indestructible`, src/Sim/World/Indestructible.cs; `World.SetIndestructible`). A building of nobody
(no `owner`) with it stands as a building (otherwise only gates of nobody do): walls, gates and towers on the wall
grid (`hu_re_enforced_wall` at a tile centre, rotation 0) join their neighbours as any wall line and block with their
pieces' pathfinder boxes. A mission's own script can still remove or kill it (`api.objects.remove`, `api.units.kill`).
Part of the world's state (the state hash mixes it in).

What the game makes of each type: docs/MAPS.md "The map source". A campaign mission places its units, buildings and
addressed scenery from `world.lua` (with GUIDs for the triggers) and leaves those out of `objects.lua`'s
(`MapSourceConfig.SkipObjects`); both lists come from the same original map.

## regions.lua

```lua
return {
  { name = "Nest_Saurpods_0_ActionArea", shapes = { { "rect", 418.2, 296.4, 48.3, 79.3 }, { "oval", 422.5, 328.2, 26.5, 44.7, false } } },
}
```

A shape is the box x .. x + w, y .. y + h (map metres) or the ellipse inside it; a sixth value `false` = switched off at
the start. A campaign mission's world.lua has the same regions (with GUIDs, rounded to millimetres); regions.lua keeps
the original's floats, and the map editor writes an unchanged shape with them (an unchanged mission saves the same
regions.lua). The nests' areas are regions `<nest>_ActionArea`, `_SafeArea`, `_HotspotArea`; lakes and campaign triggers
name theirs.

## minimap.png

Optional: the map list's picture (any size, RGBA; the original's 200 x 200 or larger, drawn turned by 45 degrees).
Without it the map list draws one from the heights.

## world.lua, triggers.lua, custom.lua, texts/&lt;lang&gt;.lua

A campaign mission (or later any map with triggers): `world.lua` is the mission's world - `objects`, `groups`,
`question_marks`, `regions` (+ `region_tree`), `quests`, `variables`, `dialogs`, `sequences`, `refs`, `trigger_tree`,
`trigger_defaults`, `warnings` (the toolkit's export, docs/LUA_MAPS.md §1); `triggers.lua` and `custom.lua` as
docs/LUA_MAPS.md describes them; `texts/<lang>.lua` is `return { ["key"] = "text", ... }` (the game takes its language,
else `uk`, else the first there is).

## .pwmap

A zip of the folder's files at the same paths (`map.lua` at the top; a zip of the folder itself, with one top folder,
reads as well). The writer stores the PNGs as they are and deflates the rest, entries sorted, with a fixed time stamp:
the same map gives the same file. Read with `PwMapReader.Read(path)` like a folder.

## Where maps are

* **The user's own maps** ("My maps": the map editor's saves, imported `.pwmap` files): the game's user folder's
  `maps/` (`%APPDATA%\Godot\app_userdata\ParaWorld Remake\maps` on Windows; `MapFiles.UserDir`, `PW_USER_MAPS`
  overrides it) - map folders and `.pwmap` files, ids `User/<folder or file name>` (`MapFiles.Find`, `IdOf`,
  `UserMaps`). A map saved there has no `source` (its id is where it is). Maps received from a multiplayer host:
  `maps/downloaded/<content hash>.pwmap` (`MapFiles.Downloaded`); a setup with a content hash finds the copy of that
  content - its own map of the id, else the received one (`MapFiles.FindFor`, `SkirmishMaps.Resolve(cfg, id, hash)`).
* **The content hash** of a map (`MapFiles.ContentHash`): FNV-1a 64 over every file's path and bytes in path order (a
  folder and its `.pwmap` have the same hash) - multiplayer compares it (docs/MULTIPLAYER.md "Maps").

* **A configuration's maps**: `content/configs/<config>/maps/` - the installation's maps of the configuration's Data
  folders, converted by Prepare at the original's path without `.ula`:
  `Data/Base/Maps/Multiplayer/multi_4_ice_601.ula` -> `maps/Base/Maps/Multiplayer/multi_4_ice_601/` (the campaign's maps
  too: the map viewer opens them); the remake's own maps (`game/maps/<name>.pwmap.json` recipes: an original map +
  name, description, author, biomes; docs/BIOMES.md) -> `maps/Remake/<name>/`. Maps from the editor or downloaded:
  any folder or `.pwmap` below `maps/`.
* **A campaign mission** is a map: `content/configs/<config>/missions/<NN>_<key>/` holds its terrain and objects
  (converted from its original map) beside its mission files.
* **Ids.** Setups, saved games, replays and multiplayer lobbies keep naming a map by its old id
  (`Base/Maps/Multiplayer/multi_2_jun_001.ula`, `Remake/frost_to_jungle.pwmap.json`); `MapFiles.Find(cfg, id)` finds the
  converted map (the id's folder below `maps/`, else the same path below each of the configuration's folders, the last
  one first; case does not matter). A map's id is its `map.lua` `source`, else its path below `maps/`.
* **A `.ula` given by its path** (a test, `--map file.ula`, a community map not converted yet) is converted in memory
  (`MapFiles.Read` -> `PwMapConvert.FromUla`): the one place that reads `.ula` is the converter.

## Converting

**Prepare converts the maps** (tools/prepare/prepare.py, docs/CONTENT.md): after the campaign export and its Lua
conversion (`campaign_to_lua.py`, now into `content/cache/missions/<config>/<lang>/`), it runs the map converter:

```
dotnet run --project tools/mapconvert -c Release -- prepare --out content/configs/<config> --install <ParaWorld>
       --folders Base,BoosterPack1 --missions-from content/cache/missions/<config>/<lang> [--force]
```

* every `.ula` below `<ParaWorld>/Data/<folder>/Maps/` of the configuration's folders -> `maps/...` (with its
  `<name>.biomes.json` beside it, if there is one);
* every recipe `game/maps/*.pwmap.json` whose base map the installation has -> `maps/Remake/<name>/`;
* every mission folder of the cache (mission.json, world.json, triggers.lua, texts/*.json) + its original map ->
  `missions/<NN>_<key>/` in the map format, and `missions/index.json`. A `custom.lua` already in the target is kept;
  the old layout's `mission.json`, `world.json`, `texts/*.json` there are removed. The converter checks that each
  mission's JSON comes back the same from its Lua and stops that mission with the path where it does not (a new
  field of the export that is a record needs its path in `PwMapReader`'s lists);
* **incremental**: a map is converted again only when one of its source files (by content) or the converter's version
  (`PwMapConvert.Version`; 2 since 2026-11-19: JSON numbers in one spelling) changed - stamps in `content/cache/maps/<config>.json`; `--force` converts all. A second
  Prepare with nothing changed takes a fraction of a second.

**Re-run Prepare once after this update**: content prepared before it has missions of the old layout and no maps; the
title screen's Game data lists that as a problem ("the missions are in the old layout: run the prepare tool again").

Other commands of `tools/mapconvert`: `ula <map.ula> <out folder | out.pwmap> [--id <id>]` (one map), `mission <old
mission folder> <map.ula> <out>`, `zip <folder> <out.pwmap>`, `unzip <map.pwmap> <out folder>`, `check <map> ...` (reads
each map and prints what it holds), `prepare --config <content/configs/<id>> --missions-from <dir>` (the configuration's
own config.json; `--missions-from` its own missions folder converts in place, the JSON files kept beside - `--clean`
removes them).

## Sizes and load times (measured 2026-11-18, the 53 staged originals)

| | largest | |
|---|---|---|
| a converted map, all files | 2.3 MB (MIRAGE single_b41_seas: 1.0 MB of it its 692 x 692 minimap) | the .ula: 2.4 MB |
| terrain/height.png | 646 KB (single_12, 784 x 688 samples) | |
| objects.lua | 1017 KB (MIRAGE single_15: 2700 objects, 31545 attributes) | |
| terrain/plants.lua | 375 KB (MIRAGE single_13: 5594 plants, 302 models) | |
| terrain/forest.lua | 17 KB (the tutorial: 124 blocks) | |
| world.lua | 2.9 MB (MIRAGE single_15; its world.json 2.8 MB) | |
| all maps of both configurations | 50 + 21 MB maps, 61 + 53 MB missions (each mission holds its terrain) | |

Every Lua file stays well under 2 MB except a mission's `world.lua`, which is the campaign export's data as it was in
JSON - so plants and objects are Lua, not a binary format. Reading (MoonSharp; first read in a process / warm):
MIRAGE mission 15's world.lua 0.9 s / 0.3 s (its world.json: 0.06 s), its map (terrain, objects, plants) 0.7 s /
0.35 s (the .ula: 0.15 s); a mission loads about one second slower than from JSON at the loading screen. A map's header
(the map list) reads `map.lua` only.

## Code

| file | what |
|---|---|
| `src/Sim/Maps/PwMap.cs` | the map document: everything a map holds (and the editor edits); `ToMapData()` - the simulation's shape |
| `src/Sim/Maps/PwMapReader.cs` | folder / `.pwmap` -> `PwMap`; `ReadHeader`; a mission's `MissionOf`, `WorldOf`, `TextsOf` |
| `src/Sim/Maps/PwMapWriter.cs` | `PwMap` -> files, folder (`WriteFolder`), zip (`WriteZip`); `PwMapPaths` |
| `src/Sim/Maps/PwMapConvert.cs` | `.ula` / recipe / old mission folder -> `PwMap` (the only caller of the `.ula` reader) |
| `src/Sim/Maps/LuaData.cs` | the Lua data files: the token check, MoonSharp in an empty environment, JSON nodes, the writer |
| `src/Sim/Maps/Png.cs` | the PNG codec (no packages: `ZLibStream`, our CRC-32) |
| `src/Sim/Maps/MapFiles.cs` | where the game finds maps (`Find`, `All`, the user's `UserMaps`) and reads them (`Read`, `ReadHeader`); `ContentHash` |
| `tools/mapconvert` | the converter's command line (Prepare runs it) |

## Tests

`dotnet run --project tests/PwRemake.Tests -- MapFormat` (PW_CONTENT: converted content; PW_CONTENT_OLD: mission folders
of the old layout, the converter's input):

* `PngRoundTrips`, `PngReadsEveryFilterAndColourType` - the codec (grey 8 / 16, RGBA, every filter type, palettes,
  RGB, deterministic bytes, damaged files refused);
* `LuaDataIsOnlyData` - functions, calls, variables, operators, loops, mixed tables refused with file and line; exact
  numbers (-0, float32 text), string escapes;
* `ConvertedMapsSameAsUla` - every staged original converted, written, read back: the same `MapData` (every field;
  heights bit for bit) and the same `MapSource` with the real rules (every list, the models, 961 sample points of
  height, walk grid, sound material, splat weights), with and without a campaign's objects;
* `RemakeRecipeConverted` - the demo map with its biome layer through the biome pictures;
* `ZipDeterminismAndHeights` - `.pwmap`, the same bytes twice, a zipped folder, edited heights fitted to a range;
* `MissionsConvertedSameData` - every mission of both configurations: the converted mission's data, world, texts and
  triggers as the game reads them are the old JSON and Lua; an old folder is refused naming Prepare;
* `ConvertedMissionPlays` - mission 11 converted: the arena's triggers fire without errors, the same state twice;
* `SkirmishSameOnConvertedMap` - a skirmish on the converted folder, on the `.pwmap` and on the `.ula`: the same state
  hash after two minutes;
* `GameModesStart` - the skirmish list (old ids, the remake's map), a skirmish, a campaign mission, a co-op mission on
  converted content;
* `MapIds` - ids to folders (case, the configuration's folders, `.pwmap` files).

`dotnet run --project tests/PwRemake.Tests -- EditorSave`: the map editor's saves of every kind of map read back as
the same document, written again as the same bytes; walk.png's painted cells; user map ids; an unchanged campaign
mission saved as the converter wrote it, byte for byte (`EditorSaveMissionSameBytes`); `EditorPolishRequires`: map.lua's
`requires` and the lists (docs/MAP_EDITOR.md "Tests").
