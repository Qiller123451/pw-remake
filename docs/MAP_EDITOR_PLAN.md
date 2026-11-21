# Map editor - plan (2026-11-18)

Goal: make and edit maps for the remake inside the game (Warcraft 3 style): terrain, objects, players, regions,
triggers in Lua; share them as one file; play them in skirmish, multiplayer and as missions. The remake does not need
to stay compatible with the original's files: maps are written only in the remake's own format; the original maps are
converted to it once (Prepare).

## The map format (phase 0)

`.ula` is not kept as the remake's map format: it is SEK's binary chunk format (unknown fields, Windows-1252 strings,
forest layouts that live in the game's exe), nothing outside our code can edit it, and writing it means recreating every
chunk exactly. The new format is a folder of Lua and images - readable, editable with ordinary tools (a text editor, an
image editor), diff-friendly in git; zipped as `<name>.pwmap` for sharing.

```
<map>/
├─ map.lua            return { format = "pw-remake-map", version = 1, name, author, description, size, setting,
│                              water, time, weather, camera, players (slots, tribes, teams, starts), options,
│                              terrain = { height_range, material names, ... } }
├─ terrain/
│  ├─ height.png      16-bit greyscale: the heights on the 2 m grid (map.lua height_range: min .. max)
│  ├─ materials.png   8-bit: the ground material per cell (index into map.lua's material list)
│  ├─ biomes.png      8-bit: the biome layer (optional)
│  ├─ walk.png        8-bit: the walk grid (1 = land units can walk; from the original's PaFi, else computed)
│  └─ scenery.*       grass / plants / converted scenery (IdiObjs) and forest blocks - Lua if small enough, else a
│                     documented little-endian binary
├─ world.lua          objects (class, position, rotation, owner, level, name, attributes), regions, groups, quests,
│                     question marks, variables, dialogues, sequences (today's world.json, as Lua)
├─ triggers.lua       as today (declarative triggers; the editor's trigger menu writes them)
├─ custom.lua         as today (hand-written code; the editor never touches it)
├─ texts/<lang>.lua   the map's texts
└─ minimap.png        the preview
```

The game reads only this format: skirmish, multiplayer, co-op (its map extension works on the loaded map), campaign
missions. The `.ula` reader stays only in the converter. Prepare converts every original map (base, booster pack,
MIRAGE; the campaign missions with their world and triggers) - re-run Prepare once after the update.

## Phases

| # | Phase | What it gives | Size | State |
|---|---|---|---|---|
| 0 | Map format | the format above; reader/writer (C#, deterministic); converter `.ula` -> new format; every original map converted and checked against the `.ula` reading; all game modes switched to it | M-L | **done** (2026-11-18): docs/MAP_FORMAT.md |
| 1 | Editor shell | "Map Editor" in the main menu (inside the game): editor camera, tool panels, undo/redo, new/open/save, **Test map** (starts a skirmish on it at once) | M | **done** (2026-11-18) |
| 2 | Terrain | brushes: raise/lower, smooth, flatten, plateau, noise; paint ground materials (the setting's textures); biome layer; water level; walkability auto + paint override; forest blocks; grass/decoration scatter; live re-meshing in chunks | L | **done** (2026-11-18) |
| 3 | Objects | a palette from the tech tree and class lists (buildings, units, animals, resources, nests, fountains, decoration, scenery; Workshop units); place, move, rotate, owner, level; copy/paste, multi-select, snapping; start locations | M | **done** (2026-11-19) |
| 4 | Players and map settings | slots, tribes, teams, diplomacy, start resources and army, tech filters, MIRAGE options, time/weather, name/description/author, texts per language, minimap made automatically | S-M | **done** (2026-11-19) |
| 5 | Regions and mission data | regions (circles, rectangles), groups, variables, quests, question marks, camera points | M | **done** (2026-11-19) |
| 6 | Trigger editor | a tree of triggers; conditions/actions from the catalogue (docs/LUA_MAPS.md) with parameter forms that pick regions/objects from the map; writes triggers.lua; "custom script" blocks; validation | L | **done** (2026-11-19) |
| 7 | Packaging and sharing | `.pwmap` zip, listed in skirmish/multiplayer, a content hash so multiplayer clients have the same map, Workshop units bundled; docs/MAP_EDITOR.md with screenshots and a tutorial | S-M | **done** (2026-11-19): Save / Save as into My maps, Open, Export / Import .pwmap, My maps in skirmish / multiplayer (content hash, protocol 5), Custom maps as missions; Workshop units not bundled (docs/MAP_EDITOR.md "Workshop and mod units"); polish (2026-11-19): maps sent to players who lack them in the lobby (protocol 6), map.lua `requires` and the lists filtered by it, placed objects alive in missions, one trigger check that loads the file, missions saved byte for byte |
| later | | cutscene/sequence editor, the AI maps (AIRG regions) | | open |

Reference for the original editor's tools and windows: the LevelEd add-on in the installation
(`Data/LevelEd`: `data/leveled/scripts`, `data/leveled/ui`).

Every phase ends with tests, docs and delivery of the changed files.

**State (2026-11-19): phases 0 - 7 are done** - the user guide is docs/MAP_EDITOR.md (every tool, saving and sharing,
playing your map, two tutorials); the whole way from an empty map to a won mission is played by the game itself with
`--editor-walkthrough`. Open: the cutscene / sequence editor, the AI maps (AIRG regions), sending maps over the network
in the lobby, bundling Workshop units into a `.pwmap`.
