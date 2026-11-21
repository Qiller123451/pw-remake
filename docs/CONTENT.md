# Content: game configurations and where assets come from

## Game configurations

A **game configuration** is one way to play: the official game or a mod. The title screen offers the ones that
exist; the choice is remembered (`settings.json` → `configuration`).

| id | title | Data folders (later wins) | playable tribes |
|---|---|---|---|
| `base` | ParaWorld | Base, BoosterPack1 | Norsemen (Hu), Dustriders (Aje), Dragon Clan (Ninigi) |
| `mirage` | MIRAGE | Base, BoosterPack3, MIRAGE | Hu, Aje, Ninigi, **SEAS** |

A configuration lives in `content/configs/<id>/`:

```
content/configs/mirage/
├─ config.json        what the configuration is (below)
├─ maps/              the installation's maps (and the remake's own) in the remake's map format, converted by Prepare
│                     (docs/MAP_FORMAT.md): maps/Base/Maps/Multiplayer/multi_2_jun_001/, maps/Remake/frost_to_jungle/ ...
├─ missions/          the campaign: index.json + one mission folder per mission - a map of the map format with the
│                     mission's Lua (docs/LUA_MAPS.md)
└─ overrides/         files that replace or add to the converted ones - own assets, and what the prepare tool
                      converts beyond the toolkit (films, effect models, sky tables, assets/tex/settings/ ...)
```

`config.json`:

```json
{
  "id": "mirage",
  "title": "MIRAGE",
  "description": "The MIRAGE mod: its rules, units and heroes, and the SEAS as a playable tribe.",
  "mod": "MIRAGE",
  "folders": ["Base", "BoosterPack3", "MIRAGE"],
  "playableTribes": ["Hu", "Aje", "Ninigi", "SEAS"],
  "data": "E:/Paraworld/export/remake/375f3d3a@MIRAGE",
  "install": "E:/Paraworld",
  "language": "uk",
  "prepared": "2026-10-31 18:00 with converter 1"
}
```

* `data` - the converted game data of this configuration: `gamedata.json`, `techtree.json` (rules), `ai.json`,
  `forest.json`, `help.json`, and `assets/` (`manifest.json`, `models/*.glb`, `tex/`, `ui/`, `terrain/`,
  `sounds.json`, `video/`). Today the ParaWorld Toolkit's pipeline makes this folder from the installation
  (`remake/pipeline`, its docs/DATA_PIPELINE.md); the remake reads it as it is, so both remakes share one
  conversion (2 GB per configuration, made once).
* `install` - the ParaWorld folder (sounds `Data/<folder>/Audio/Sound`, music `Audio/Music`; the original maps are
  converted by Prepare into `maps/` and `missions/` and not read at run time; read at run time, not prepared: the string tables `Data/locale/<lang>/Texts` and the add-ons' `Locale/<lang>/Texts`
  (GameTexts), the mission speech and the journal entries' voices `Audio/SeqSounds` (and `Data/locale/<lang>/Audio`),
  the loading screens' table `Scripts/Game/misc/LoadingScreens.txt` and the speech's sound events
  `Scripts/Server/init/dialogsounds.txt` - docs/GAME_SESSION.md "The journal entry").
* `folders` - which Data folders of the installation belong to the configuration, in load order.

## Where a file comes from (`AssetSource`)

The game never opens a game file by an absolute path. Everything is asked for by a relative path and looked up in
this order:

1. `content/configs/<id>/overrides/<path>` - own or improved files,
2. `<data>/<path>` - the converted original,
3. for `assets/snd/<p>` and `assets/music/<f>`: the installation's `Audio/Sound/<p>` / `Audio/Music/<f>`, the last
   folder of the configuration that has it.

### Own assets later

The project uses ParaWorld's own assets today, exactly like the browser remake. To replace them - a re-made model,
HD textures, new interface art, or one day a complete set of own assets - put files with the same relative path and
format into `overrides/` (a model: `assets/models/<name>.glb` with the same node names, links and animation names
as the original; the manifest entry can be overridden the same way), or make a configuration whose `data` points to
a folder with the same layout. No code changes: the formats are the contract (manifest.json: per model its file,
animations, walk speeds, footprint, links; docs/VIEW.md).

## The prepare tool

`Prepare.bat` / `python tools/prepare/prepare.py [--paraworld <folder>] [--toolkit <folder>] [--data <folder>]
[--lang uk] [--force]`:

1. finds the toolkit's converted data: `<ParaWorld>/export/remake/<installation id>[@<mod>]` or
   `%APPDATA%/ParaWorldToolkit/remake/...` (the toolkit's **Toolkit data → Change** moves it; `--data` points at it);
2. exports every campaign mission of each configuration with the toolkit's exporter (`pwexport.campaign`) into
   `content/cache/campaign/<config>/<lang>/` - **incrementally**: a mission is exported again only when its map or
   the exporter changed (`stamps.json`), `--force` exports all. The texts come from the language folders of the
   base game **and the add-ons** (`tools/prepare/pack_texts.py`): the toolkit's text table reads only the base one,
   so the BoosterPack 1 missions (4-1 … 4-3, the SEAS missions) and MIRAGE's *Seeking Ada* got the master files'
   placeholder "!boosterpack1.ltf load failed!" as title and quest texts;
3. converts them to Lua mission folders of an intermediate layout (`tools/prepare/campaign_to_lua.py`: mission.json,
   world.json, triggers.lua, texts) in `content/cache/missions/<config>/<lang>/`;
3a. **converts the maps** to the remake's map format (`tools/mapconvert`, C#; needs the .NET SDK, as the game):
   every original map of the configuration's Data folders (`Data/<folder>/Maps/**/*.ula`, campaign maps too) into
   `content/configs/<config>/maps/`, the remake's own maps (`game/maps/*.pwmap.json`) into `maps/Remake/`, and every
   mission (its original map + its folder of step 3) into `content/configs/<config>/missions/<NN>_<key>/` (map.lua,
   terrain pictures, world.lua, triggers.lua, texts/*.lua; a `custom.lua` there is kept). **Incremental**: a map is
   converted again only when its source files or the converter changed (`content/cache/maps/<config>.json`),
   `--force` converts all. The game reads only these converted maps: **run Prepare again after the update that brought
   the map format** (the title screen's Game data says so until then). Details: docs/MAP_FORMAT.md "Converting";
4. converts the story films and the start films (the logos and the intro the client plays at start-up: docs/MENUS.md
   "The start films") to Ogg Theora, the format Godot plays (`tools/prepare/films.py`, needs ffmpeg; once, into
   `content/cache/films/` and the configuration's `overrides/assets/video/`; `--no-films` skips it);
5. converts the original effect models the toolkit's export leaves out (the ruins' dust, the rocket smoke, the
   spirits, fireworks, tornado, quicksand: `tools/prepare/effect_models.py`, docs/EFFECTS.md) into the
   configuration's `overrides/assets/` (models, textures, `manifest_fx.json`); then the models only the cutscenes'
   own actors use that the export leaves out (`tools/prepare/cutscene_models.py`: every actor of every mission's
   sequences whose model - the class file's gfx, else the class name - the data lacks, found in the installation's
   archives; mission 1's Stina with her sabre-tooth in sc_1050, `ns1050_stina_eusmilus` of `all_characters.gsf`, a
   class the official game's class files do not even name) into the same overrides;
6. converts the landscape settings' day-time light tables (`Texture/Scape/<Setting>/skygradient_day.tga`, else
   `skygradient.tga`, the last Data folder of the configuration that has one: `tools/prepare/sky.py`) into the
   configuration's `overrides/assets/terrain/<Setting>/sky.json` - a cutscene's time of day lights the scene by them
   (docs/GAME_SESSION.md "Cutscenes"), and their row 8 colours the fog of war clouds (docs/TERRAIN.md "Fog of war");
   and the fog of war's cloud atlases (`Texture/Scape/<Setting>/fog_(0256).dds`, the fallback
   `Texture/Effects/fogofwar.tga`: `tools/prepare/fog_textures.py`) into `overrides/assets/terrain/<Setting>/fog.png`
   and `fogofwar.png` (without them the game uses the original's `particle_06` clouds and says so in the log);
7. converts the buildings' **setting textures** (`tools/prepare/setting_textures.py`): the original's texture atlases
   repainted per landscape setting - `Texture/Buildings/settings/<ashvalley|icewaste|jungle|northland|savanna>/`
   (`<Aje|Hu|Ninigi>_Lvl_<n>_a`, `Hu_Misc_a`; MIRAGE adds `cave3`, `Seas_Building_a`, `Seas_Tent`) and MIRAGE's
   `Texture/settings/icewaste/boosterpack_01_a` - of the configuration's Data folders (a later folder's replacing an
   earlier's: MIRAGE over Base; of a folder's sizes the largest) to PNG in the configuration's
   `overrides/assets/tex/settings/<setting>/<name>.png`, with `overrides/assets/tex/settings/index.json` (key: the
   texture's path below `Texture/` as the models' materials name it, `buildings/hu_lvl_1_a` -> setting -> file).
   Incremental (a variant is converted again when its source changed; `--force` all). The toolkit's conversion has
   only the plain textures; the game draws a building in the variant of the setting (biome) it stands in
   (docs/VIEW.md "Setting textures"). Content prepared before this step has no index: plain textures, no error;
8. writes `config.json`.

The game's **Game data** screen (title screen) lists the configurations and their problems, and can run the tool.

### Without Python later

The conversion steps are Python because the toolkit's readers of the game's formats are Python. The plan
(docs/ROADMAP.md) is a C# importer in the game itself; the content layout above stays the same, so nothing else
changes when it comes.

## Running an exported executable

Godot's Project > Export (Windows Desktop) makes `ParaWorldRemake.exe` and its `data_*` folder. The game reads its
content, Lua library and campaigns from disk, not from the .pck, so the executable is best exported **inside the
repository** - into `game\` (next to `project.godot`), the root, or a build folder below either. It looks for the
repository root from its own folder upwards (the folder with `tools\prepare\prepare.py`, `game\project.godot` or
`content\configs`: `AppPaths.RootDir`) and uses its `content\`, `game\lua` and `game\campaigns` - the same as the
editor, so `Prepare.bat` runs once for both. An executable copied elsewhere uses its own folder (`content\`, `lua\`,
`campaigns\` beside it), or the content folder of settings.json `contentRoot` / `PW_CONTENT`. The biomes and the
remake's own maps are `game\maps` (`maps\` beside such an executable; docs/BIOMES.md).

## Sending the game to a tester (`Package.bat`)

`Package.bat` / `python tools/package/package.py` builds a package that plays without Python, the toolkit or
Prepare, and zips it into `dist/ParaWorldRemake-<date>.zip`:

```
ParaWorldRemake/
├─ game/        the exported executable, its .pck and data_ParaWorldRemake_windows_x86_64/, project.godot (the marker
│               AppPaths.RootDir finds the root by), lua/, maps/, campaigns/, coop/, missions/
├─ content/     configs/<id>/ (as Prepare made them; config.json rewritten, below), hd/
├─ data/<id>/   the converted game data of each configuration (~2 GB each)
├─ Set ParaWorld folder.bat   (+ set_paraworld_folder.ps1) writes the tester's ParaWorld folder into the configurations
└─ README.txt
```

- **Export first.** Export the game from Godot (Project > Export > Windows Desktop, into `game\`). Alternatively,
  `--godot <Godot exe>` exports headless with that preset. The script warns when the sources are newer than the export.
- **The package's `config.json`.** `data` is `../../../data/<id>`: `data` and `install` may be relative to the
  configuration's folder (`GameConfiguration.Resolve`). `install` is empty until the tester runs `Set ParaWorld
  folder.bat`, which checks that the chosen folder contains `Data\Base`.
- **The tester's own installation.** The game reads sounds, music, texts and voices from the tester's ParaWorld at run
  time. For MIRAGE it also needs the MIRAGE and BoosterPack3 folders. The converted data comes from the game itself:
  send it only to people who own ParaWorld.
- **Options.**
  - `--configs base,mirage` picks the configurations (default: all).
  - `--no-data` leaves the converted data out, for a tester who already has it.
  - `--folder` also writes the package as a folder.
  - `--out <folder>` puts the zip somewhere else.
- **Left out.** Sources, `.godot`, `content/cache` (Prepare's working files), tools, tests and docs.
- **The zip.** It is written straight from the files, with no copy of the data first. Pictures, films, sounds and
  models are stored, the rest deflated. It is a ZIP64 archive past 4 GB; Windows 10 and later unpack it.
