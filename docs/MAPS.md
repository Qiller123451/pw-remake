# Maps

Everything about map data in the simulation, without the engine: the maps the game reads (the remake's map format:
[MAP_FORMAT.md](MAP_FORMAT.md) - folders of Lua and pictures, converted once from the original game's `.ula` by
Prepare), the reader of the original's maps the converter uses, the remake's random jungle map, forest blocks, the "map
source" the world is built from, and the ground levelling of buildings. Code: `src/Sim/Maps` (namespace
`PwRemake.Sim.Maps`), a port of the JS remake's `game/maps/*.js`, `game/mapgen.js`, `game/ground.js` and the data part of
`engine/terrain.js` (pw-exporter 98c7703). The `.ula` file format itself is described in the toolkit's
`docs/MAP_FORMAT.md` (and `pwexport/data/ksy/paraworld_surf.ksy`).

How a map gets to the world: `MapFiles.Find(cfg, id)` (the converted map of a setup's id) -> `MapFiles.Read(path)`
(`PwMapReader` -> `PwMap.ToMapData()`) -> `MapData` -> `MapSource.OriginalSource` -> the world. The `.ula` reader
(`Ula.cs`) is called only by the converter (`PwMapConvert`; a `.ula` given by its path is converted in memory).

| JS (remake/src) | C# (src/Sim/Maps) | what |
|---|---|---|
| game/maps/ula.js | `Ula.cs` (reader), `MapData.cs` (the result) | `.ula` file -> `MapData` (the converter's: PwMapConvert) |
| (new) | `PwMap.cs`, `PwMapReader.cs`, `PwMapWriter.cs`, `PwMapConvert.cs`, `LuaData.cs`, `Png.cs`, `MapFiles.cs` | the remake's map format: MAP_FORMAT.md |
| game/maps/frame.js | `Frame.cs` | map space vs. world: headings, quaternions |
| game/maps/forest.js | `Forest.cs` (`Forest.ParseForest`, `ForestData`) | forest blocks + forest.json |
| game/maps/source.js | `MapSource.cs` | `MapData` or the generator -> what the world build needs |
| game/mapgen.js | `MapGen.cs` | the random jungle skirmish map |
| game/ground.js | `Ground.cs` (`GroundBrushes`, `Ground.LevelGround`, `Ground.BrushCells`) | buildings level the ground and paint materials |
| engine/terrain.js HeightField, fbm, mulberry32 | `HeightField.cs`, `Noise.cs` | the world's sampled ground; noise |
| (helpers) | `Bytes.cs` | `ByteView` (DataView), `Latin1` (TextDecoder 'latin1' = Windows-1252), `JsNum` (JS `+s`) |

## Coordinates

ParaWorld is a Direct3D game: its map space is **left-handed** - x east, **y south**, z up, metres from the map's
north-west corner. The world (JS three.js, Godot) is right-handed with y up and -z north. A map source centres the map:

```
game x = map x - ox        ox = Hx * 2 / 2  (half the terrain extent: height samples per side * 2 m / 2)
game z = map y - oy        oy = Hy * 2 / 2
game y = height
heading = Frame.MapHeading(rot) = pi - rot       rot = -2 atan2(qz, qw) of the stored quaternion
orientation of tilted pieces = Frame.MapQuat(q)  (plateaus, cliffs: the whole quaternion)
```

`MapSource.ToGame(mx, my)` / `ToMap(x, z)` convert; `MapSource.Origin = [ox, oy]`. (The terrain extent comes from the
block counts of the height grid, which can be a little larger than the header's width / height.)

A map extended for co-op (`MapExtension.Apply`, docs/COOP.md "Maps") keeps the old map's coordinates: its grids start at
`MapData.OffX / OffY` (0 for every original map, negative after an extension to the north / west), `MapSource.Origin`
is the extended map's centre in map coordinates and `MapSource.Half` its half extent (the minimap uses `Half`).

`MapData` and its records (objects, plants, regions, forest blocks, walk grid) are in **map** coordinates;
`MapSource` and everything in it (trees, decor, starts, nests' areas ...) are in **game** coordinates - except
`MapSource.Regions`, which stay map regions (the campaign's `Regions` converts, as campaign/regions.js does).

## Reading an original map (the converter): `Ula.ParseUla(bytes, fileName)` -> `MapData`

The file is a container of zlib blocks (`Ula.Unpack`) holding a `SURF` chunk tree (`Ula.Chunks`). What each chunk gives:

| chunk | `MapData` | notes |
|---|---|---|
| `LInf` | `Info` (key/values: LevelName, MapName, Setting, MaxPlayers ...), `Description` (the description tree flattened: `Root/Base/StartTime` and also `StartTime`), `InfoTree`, `Preview` (200x200 RGBA), `DefaultCamera` (radians, 0.785), `StartTime` (seconds of the day: 12:00 missing, 8:30 malformed), `Name`, `MaxPlayers` | strings Windows-1252 |
| `Terr` | `W`, `H` (metres), `Water` (level, m), `Hx`, `Hy`, `Heights` (float32 m, 2 m grid, `Heights[j * Hx + i]` at map (2i, 2j)), `Mx`, `My`, `Mats` (0..7 per 4 m cell), `TerrainSetting` | `Setting` = the texture set the remake has (`KNOWN_SETTINGS`: TestSet -> Jungle), `SettingName` = the level's own name (forest kinds) |
| `PaFi` | `Walk` (`WalkGrid`: 2 m cells, 1 = land units can walk; null when missing or not fitting) | the editor's land grid: no steep slopes, no water; objects not in it |
| `Objs` | `Objects` (`MapObject`: Type tag, Name, Cls, X, Y, Z, Rot, Q, Owner (null = 0xff), Attr) | found by scanning for the type tag, as the JS (plain CGameObj scenery without a tag is not found) |
| `IOMG` | `Plants` (`MapPlant`: Name, X, Y, Z, Rot, Q) | landscape decoration instances |
| `Frst` | `Forest` (`ForestMap`: W, H, Blocks of 31 item bytes) | trees by `ForestData.ForestItems` |
| `Rgns` | `Regions` (`MapRegion`: Name, Shapes: rect / oval boxes, Enabled) | nests' areas, lakes, campaign triggers |

Queries of `MapData` (map coordinates): `HeightAtMap(mx, my)` (bilinear on the 2 m grid, clamped - the same function
as `MapSource.Height`), `MaterialAtMap`, `WalkableMap` (null without a grid), `WaterDepthMap`, `Region(name)`,
`RegionsAtMap`. `MapRegion.ContainsMap` / `RegionShape.ContainsMap` are campaign/regions.js `containsMap`.

Trigger, quest, dialogue and AI chunks (`Trgr`, `Ques`, `DlgS`, `AI`) are not read here: the campaign export
(pw-campaign/1 JSON, `docs/CAMPAIGN_FORMAT.md` of the toolkit) has them.

## Forest blocks: `ForestData`

`ForestData.LoadFile(".../forest.json")` (pipeline step "forest": the 32 layouts copied from the player's
PWServer.exe and the tree kinds of every setting). `ForestItems(md, heightAt)` gives every standing tree (X, Y, Rot,
Kind 0..4, Amount) and undergrowth plant (X, Y, Kind 0..7) in map coordinates - nothing at or below 16 m (under
water). `ForestKinds(setting)` names the models. The JS keeps the table in a module variable (`setForestData`); here
it is an object passed to `MapSource.OriginalSource`.

A map is read by `MapFiles.Read(path)`: a map folder or `.pwmap` of the remake's format (MAP_FORMAT.md; with its biome
layer, docs/BIOMES.md), or a `.ula` / remake recipe `.pwmap.json` given by its path, converted in memory.

## Bridges (how the original makes them)

Investigated 2026-11-13 (MIRAGE 13 report, docs/SIMULATION.md "Bridges"):

* **No walk surface.** Every bridge class (`*bridge*`: hcl13_bridge_01, hc_bridge_*, jungle_bridge*, *_river_bridge_in /
  _out, sav_bridge_*, pt_footbridge_*, ice_rhinebridge ...) is a `CDecoColObj` (gamedata `script`). Its OnInit only
  hands the object to the pathfinder as a blocker (`CSrvWrap.GetPathfinder().AddPFBlocker(this)`, MiscObj.usl; MIRAGE:
  an object loaded with attributes blocks only when they say `blocker` true - its SDK's switch -, one without attributes
  as before; the toolkit reads no attributes on the maps' bridges), which blocks the model's pathfinder boxes (the GSF
  collision table, manifest `pf`). The only objects with a walkable top are the walls (the wall map's `/Walkable`
  config). Units stand on the terrain.
* **Crossed on the ground.** Where a bridge is a way, the level designers raised a strip of terrain to about the deck's
  height under it and set river pieces in and out on both sides: mission 9's `jungle_river_bridge_in` / `_out` at
  map (589, 386) / (589, 405), z 21, over a flat strip at 23 m the walk grid lets units cross (the river at 17 - 18 m
  north and south of it); BoosterPack 1's `nor_river_bridge_*`, mission 7's `sav_bridge_*`, mission 10's
  `jungle_river_bridge_*` alike. Their pathfinder boxes block the posts and the river beside the strip.
* **Scenery over blocked ground.** Where the ground under a deck is low, the walk grid blocks it and the bridge is not a
  way: mission 13's two `hcl13_bridge_01` (map (604.5, 476.9) and (602.1, 503.1), z 24.0) stand over the ravine's
  river bed (16 - 18 m, the river pieces `sav_river_*` at 19 - 21 m), and the crossing is the strip of ground between
  them (map y 482 - 496, 23 m), which the river runs under (`sav_river_02l` at (603.2, 484.1), z 20.1); the bridges
  have no pathfinder boxes. Mission 5's `hc_bridge_*` (z 37 - 52 over ground 8 - 15 m lower) and the jungle bridge of
  missions 9 and 10 (z 60 over a 50 m gorge, both ends on blocked cells; mission 9's is broken by sc_3025) likewise.
* **Footbridges block.** Mission 6's pirate town `pt_footbridge_*` have one box over their whole length (3.7 m wide,
  5.7 m high): units do not walk under or along them.
* The PaFi grids per start location are **not** limited to the ground reachable from that start (MAP_FORMAT.md of the
  toolkit says so): mission 9's keep the Dragon Clan village's basin in every start's grid, also those of the regions
  it is cut off from; they differ from the first grid by the placed objects only.

The remake: the walk grid as it is, bridges block their boxes, and the ground just under a deck without boxes is
not walked (`World/Bridges.cs`).

## The map source: `MapSource`

`MapSource.OriginalSource(md, rules, cfg, hasModel, forestData)` for an original map and
`MapSource.GeneratedSource(cfg)` for the random jungle map give the same shape (game coordinates):

| field | what | used by |
|---|---|---|
| `Size`, `MeshCell` | the square world (m) and the height sampling step (2 m on every map: the original's grid; the JS took 4 m above 1400 m) | world, nav grid, fog, terrain mesh |
| `Play` | playable rectangle (the map minus a 32 m frame) | nav marks outside cells blocked |
| `Height(x, z)` | ground height (bilinear on the map's 2 m grid / the generator's formula) | sampled into the world's `HeightField` |
| `Water` | water level or null; `WaterDepth(x, z)` | nav (water marks, ships), water drawing |
| `Walkable(x, z)` | the map's PaFi grid, null when unknown; `WalkTest()` = the grid or `SlopeWalkable` (32 degrees) | nav `markTerrain` |
| `WallGrid` | the wall tiles' offset (docs/spec/walls.md §2) | walls |
| `Textures`, `Scales`, `Splat(hf)` | the 8 ground textures `<Setting>/scape_k.jpg` and the per-point weights (4 m material grid, bilinear, with a noise shift for round borders) | terrain renderer |
| `SoundMat(x, z)` | the ambience's ground material of the 32 m square (0..7, 8 = forest) | ambient sound |
| `Trees`, `Decor`, `Stones`, `Bushes`, `Fish` | resources and scenery (models, wood, amounts, blocking) | world setup, props |
| `Animals`, `Nests` | single animals and respawning nests with their areas (from the regions) and start animals | wild animals system |
| `Starts`, `Bases`, `StartSlots` | start locations, the pair farthest apart, each one's slot | skirmish setup |
| `Mp`, `Owned`, `Flags` | multiplayer settings, the players' map objects, domination flags | skirmish |
| `Wells`, `Lakes`, `Items`, `ItemSpawns`, `ItemPool` | healing wells, lakes of healing / damage, artefacts | landmarks, items |
| `Regions` | the map's regions (map coordinates); `Region(name)`, `RegionContains(name, x, z)` | lakes, nests |
| `Setting`, `DefaultCamera`, `StartTime`, `Weather`, `LevelName`, `Atmos`, `AiOptions` | level info | sky / time of day, camera, weather, music, AI |
| `Models` | model names the map needs, in first-use order | asset loading |
| `Md` | the `MapData` behind it (original maps) | |
| `BiomeLayer`, `BiomeAt(x, z)`, `UseBiomes(l)` | the remake's biome layer (null: the setting alone) and the biome weights at a point - drawn and heard only (docs/BIOMES.md) | terrain, grass, light, ambience |

What the JS asked of the rules (`D`) and the asset manifest comes through `IMapRules` (ClassGfx, Script,
ResourceValue, Exists, StatsGfx = `stats(name, 1, null).gfx`, NestSpecies = `extra.nests`) and a `hasModel` test, so
`src/Sim/Maps` does not depend on the Rules port. With the ported `Rules` (src/Sim/Rules) the adapter is one line per
method: `ClassGfx`, `Script`, `Exists` directly, `ResourceValue` = `Rules.ResourceValue(section, name, 0)` when above 0
else null, `StatsGfx` = the gfx of `Rules.Stats(name, 1, null)`, `NestSpecies` from gamedata `nests` (absent in
today's data); `hasModel` = the asset manifest has that model name.

`cfg` (`MapSourceConfig`): `SkipObjects` (campaign missions place their units and addressed scenery themselves; a
non-null set also drops the players' objects), `NoForest`, and for the random map `Players` and `Seed`.

Drawing-only parts of the JS source stayed out (nothing three.js); the source keeps the data the terrain renderer
needs: heights, material weights per point, texture names, water level, setting name.

## How the world uses it (main.js buildWorld)

1. `hf = new HeightField(S.Size, S.MeshCell, S.Height)` - the world's ground (float32 samples, the triangle split of
   the mesh: `hf.At(x, z)`, `hf.NormalY`). World and terrain mesh read the same samples.
2. The nav grid blocks the cells outside `S.Play` (2 m margin), marks the water (`S.Water`), then the terrain:
   `markTerrain(S.WalkTest(hf.At))` - the map's walk grid or the slope rule; then labels regions.
3. A building put down: `Ground.LevelGround(hf, brushes.Get(gfx), brushes.Mpp, x, z, heading)` levels the samples
   (the view refreshes the mesh rows of the returned rectangle) and `Ground.BrushCells(...)` gives the 4 m material
   cells it paints (`GroundBrushes.MaterialCellOf(S.Size)`); the world keeps the list (saved games redo it).
4. Map content: trees, decor, stones, bushes, fish, animals, nests, wells, lakes, items, flags, owned objects as listed.

## How the terrain renderer uses it (game/src/View)

* mesh: `hf.N x hf.N` vertices at `i * Cell - Size / 2`, heights `hf.H`;
* ground: `S.Splat(hf)(x, z)` per vertex = weights of `S.Textures` (8 for original maps, 4 for the random map, plus the
  painted cells of `BrushCells` mapped to texture slots - main.js `GROUND_NEAR`, view code);
* water plane at `S.Water`; sky and light from `S.Setting` and `S.StartTime`; `S.Weather` for rain and snow;
  `S.Md.Preview` for the map list picture.

## Tests

* `tests/PwRemake.Tests/MapsGoldenTests.cs` - the JS against the C# on 28 real maps (17 campaign, 3 BoosterPack,
  3 MIRAGE, 5 multiplayer): heights, materials, walk grid, every object with its attributes, plants, regions, forest
  blocks and trees, the whole map source (lists, rules questions, models), 400 sample points (height, height field,
  walkable, slope rule, sound material, normal) and 60 splat points; the random map (2 and 4 players); 12 ground
  brushes. `dotnet run --project tests/PwRemake.Tests -- Maps`.
* Goldens: `tests/golden/maps/*.json.gz`, written by `tests/golden/maps_dump.mjs` running the JS modules in node
  (`maps_dump.sh` regenerates them; `node_hooks.mjs` stubs three.js). Maps not staged on the machine are skipped.
* `tests/PwRemake.Tests/MapsTests.cs` - numbers, frame, regions, slope rule, queries on a real map.

Port notes: `Math.hypot` is `Math.Sqrt(a*a + b*b)` (may differ in the last bit; the goldens agree exactly);
the JS's module globals (`MAP` of mapgen.js, forest data) are instances (`MapGen`, `ForestData`); the description
tree's recursion stops at depth 4000 (the JS ended in a caught stack overflow on a damaged tree).
