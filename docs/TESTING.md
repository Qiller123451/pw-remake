# Testing

## Simulation tests (no engine)

```
dotnet run --project tests/PwRemake.Tests              # all
dotnet run --project tests/PwRemake.Tests -- Triggers  # those whose name contains "Triggers"
```

A small runner of our own (`tests/PwRemake.Tests/Program.cs`): every `public static void X(TestContext t)` of a
class marked `[TestClass]` is a test. Tests that need the game's data read it from environment variables and are
skipped without them:

| variable | |
|---|---|
| `PW_DATA` | a converted data folder of the toolkit (gamedata.json, techtree.json, assets/manifest.json ...) |
| `PW_DATA_MIRAGE` | the same for MIRAGE |
| `PW_CAMPAIGN` | a folder of exported missions (pw-campaign/1 JSON) |
| `PW_CONTENT` | a content folder with configs/ - **converted to the map format** (configs/<id>/maps, missions with map.lua: docs/MAP_FORMAT.md). Test content made before the map format is converted with `dotnet run --project tools/mapconvert -c Release -- prepare --config <content>/configs/<id> --missions-from <content>/configs/<id>/missions` (in place: the old JSON files stay beside, so older code still reads it) |
| `PW_CONTENT_OLD` | mission folders of the old layout (mission.json, world.json), the converter's input in `MapFormatTests` (default /home/claude/testcontent) |
| `PW_MODELS_EXTRA` | more folders of converted models (.glb), `;`-separated, for the model checks below |

Golden tests compare the C# rules, maps and trigger engine with the browser remake's JavaScript run in node on the
same data (`tests/golden/`): the port must give the same numbers. (The map goldens keep reading the original `.ula`;
`MapFormatTests` check that the converted maps give exactly the same: docs/MAP_FORMAT.md "Tests".)

Skirmish and army budget (docs/SKIRMISH.md): `PointBuyGolden*` (tests/golden/pointbuy_dump.mjs: pointbuy.js and
skirmish.js in node, 0 differences), `SkirmishArmyBudget`, `SkirmishSetupJson`, `SkirmishMapList`,
`SkirmishModesAndRandomMap`, `CampaignArmyScreen`; the MIRAGE computer players: `MirageAje*` (docs/AI.md). A skirmish in
the game with an end screen and the statistics for screenshots: `--skirmish multi_2_jun_001 --players Hu,Aje --points
1000 --preload start --end-at 6 --end-stats --shots <dir> --shot-every 4` (docs/DEBUG.md).

Models (no engine; docs/VIEW.md): `BuildingParts` draws every building model of both configurations in every state with
the view's part rules and looks for parts drawn twice (`LumberjackCottage` the report's case); `CompositeLinks`
checks every composite (riders, turrets, build-ups, flags, cranes) against its model's links; `BuildingRuin` destroys
every building class of both configurations and checks that it plays its destruction - its ruin and its turrets'
ruins, one of them with a clip whenever one could have it (docs/VIEW.md "Destruction") - and that every shooting
building's range circle is the range it fires at, the ghost's the same (docs/HUD.md "Attack ranges"). The prepare tool's own
tests: `cd tools/prepare && python -m unittest discover tests` (films and start films, sky tables, texts, cutscene-only models, effect
models, the Lua conversion, the cutscene actors' clips and links, the setting textures). The setting textures' choice:
`SettingTexturesTests` (the index, a texture + setting -> its variant and the fallbacks, old content without the index,
the biomes' settings, the converted models' texture keys).

The animation audit (docs/SIMULATION.md "Animation audit (2026-11-13)"): `AnimAuditTests` - AnimCtl.PlayLoops (start part
once, counted loop parts, end part), a Hu worker's chopping / hacking / mining (no restarts, the end part before the pick-up),
the carrying walks, the idle variants with their end parts, the hammer's end part, the death clip by the side of the blow
(the front's hit_fw) and Del's harakiri, a field's grow frame and work chunks, the snare trap's victim (catching, hanging,
snare_trap_dying, all_snar_trap_end when the trap is destroyed), the Hu harbour's crane at Cr_3 looping "work" while the
harbour produces. `AnimHeadHitGaitTests` - the head cut off (a "res" victim after
res_strike_13 with the draw 0: `hu_worker_head` and chop_1; another strike, another draw, a "nat" victim, an animal
killer: none; World.Rng untouched), the hit reactions (the side of a melee blow f / b / l / r; none for a projectile, a
stunned unit, a vehicle), the walkanimconfig.txt tables and the gait transitions (a worker 1 -> 2: accel_1_2 then
walk_2's loop part, 2 -> 1 brake_2_1, 1 -> 3 through gait 2, stopping in a transition: the new gait's end part; the
megaloceros 1 -> 4), the data's heads (a model for every head class) and side hit reaction clips. The clips and heads
are in data prepared by the toolkit from 2026-11-13 on (older data: those tests skip); with the container's data plus a
fresh conversion set `PW_DATA` to it and `PW_MODELS` to its `assets/models`.

Set-up units and fight positions (docs/SIMULATION.md): `FightPositionTests` - the Black Widow, the log cannon and
MIRAGE's MG walker set up once, stay set up through their shots and after the fight, get up to move; an archer and a
SEAS gunner raise the weapon once, shoot from the loop part, lower it when the fight is over. `FirstShotDelayTests` -
the original's first-shot delay: an archer's / gunner's first shot 0.4 s later (Bela 0.6 s; the projectile leaves that
much later), the shots after it none, again after the weapon was lowered, the debug option off / a time of its own,
the option in the GameStart. MIRAGE's game options in every start: `MirageGameOptionsPathsTests` (the multiplayer
skirmish lobby, a campaign mission, the co-op lobby, an ally's tribe swap). MIRAGE's classes (docs/SIMULATION.md "The
MIRAGE classes"): `MirageSeasBuildingsTests` - one per SEAS class (psi disruptor, speaker, Stygimoloch nest, Tesla
machine, mine, pub, warp gate, portal terminus, the towers' turrets); `MirageClassesTests` - one test per Norse /
Dustrider / shared class (stone mine, mud pit, punching bag, big horn and Amazon temple, sensor tower, gallimimus nest,
portal terminus, zeppelin launch pad, special box, goods, Halicarnassus gate, pirate boss ship), the launch pads'
build-ups, the option FlyingEnabled (and Portals), the ruins that stay (also saved and loaded). The container's MIRAGE
data has no model files for most of these: the links (Bl_0 / Bl_1, psh1..3, D_01..14) fall back as the code says.
`CutsceneMission13Tests`:
sc_4070's carrier hull, the speakers' mouths, no attack loops on cutscene parts (docs/GAME_SESSION.md "Cutscenes").

Cutscenes (docs/GAME_SESSION.md "Cutscenes"): `CutsceneGateTests.GatesSteadyEveryMission` plays every mission of both
configurations as the game does - 60 game seconds, each cutscene stopping the world and played at 30 frames per second
through `SceneClips` with the view's rule for following a clip (a new start, or 0.25 s off -> put back), ended by the
`SceneEndCommand` - then every other cutscene with a bound building on the world as it is; no gate may change more than
3 times within 10 s in the world, no bound building may be seen to change more often than its animation items ask
(~6 minutes; `PW_CAMPAIGN_MISSIONS=16,13`: only those). Without the fix mission 16's gates 0 and 2 were put back 286
times in sc_7050. `Mission16Gates` (both configurations: open after sc_7050; sc_7052's gate 0 held open, closing at
37.5 s, closed after; the gates' clips as the user's data has them - the container has no model of them),
`FrameRanges`. `CutsceneActorTests`: mission 2's turtle with its shell and the ankylosaurus' rider,
`SwappedActorsEveryMission` (every mission with an actor of the player that the world does not have, with every
tribe).

Bridges (docs/SIMULATION.md "Bridges"): `dotnet run --project tests/PwRemake.Tests -- BridgeTests` - the ground just
under a deck blocked without cutting a way, units crossing beside a bridge never under it, mission 6 / 7 bridges
blocking their boxes, MIRAGE / base mission 13's allies walking back from the cleared SEAS outpost (about 2 minutes).

Goals out of reach (docs/SIMULATION.md "Goals out of reach"): `UnreachableGoalTests` - mission 9's Dragon Clan village
reachable from the heroes' start for every unit size, its altars' and airplane's islands other regions; a goal more
than 160 m off on another island: every way ends at the region's cell nearest it (brute force), not found; small and big
units sent there (mission 9, and mission 6's army from its island to the pirates' prison) stand at that point (~20 s).

Crossings (docs/AI.md "transports"): `FerryTests` - mission 2's scripted turtles, mission 15's SEAS hovercraft, mission
12's Hu and Aje crossings. `Mission12ShipCrossing` waits (up to 2 more minutes) for each crossing player's first
fighter before it orders the attack: the Aje start with workers only and train their first raptor handler at 30 - 50 s
by the seed, so at 30 s seeds 2, 6 and 7 had none and never crossed ("p5: a crossing" failed - the test's set-up, not
the AI). `PW_FERRY_SEED=n` runs it on another seed (seeds 1 - 8 pass).

## The game, headless and with screenshots

```
godot --headless --path game -- --config base --mission 11 --quit-after 60      # runs a mission a minute, no drawing
godot --path game -- --screen campaign --quit-after 3 --screenshot menu.png      # a picture of a menu
```

On a machine without a GPU, `xvfb-run` with Mesa's llvmpipe Vulkan draws real frames (slow but exact). The log of
the run is in the user folder (`logs/`), errors also on stderr.

## The remake's own campaigns (Containment, the scenarios)

`dotnet run --project tests/PwRemake.Tests -- Sequel` (docs/SEQUEL.md "Tests"): the catalog, the sequel's classes in
the MIRAGE rules, the maps from their recipes (the same every time), mission 1, every mission and scenario set up and
run `PW_SEQUEL_SECONDS` (200) game seconds with no Lua errors, a Containment mission saved and loaded (the same world).
`PW_PLAY=1 ... -- SequelPlaythroughs` plays every mission to its win (the browser remake's sequel_flow.js and
scenario_flow.js). Results of 2026-11-02 (test data: the MIRAGE configuration of /home/claude/testcontent):

| | runs | won |
|---|---|---|
| Containment 1-16 | 16 | 16 |
| Scenarios 1-5 as Hu, Aje, Ninigi, SEAS (4: a side chosen and silence) | 24 | 24 |

In the game: `--config mirage --sequel 7 --skip-briefing --reveal --cam -150,-40,0,140 --shots <dir>`
(docs/images/sequel_mission1.jpg, sequel_mission7.jpg). The development container's MIRAGE data has no models and no
terrain: the screenshots used a scratch configuration with the base game's assets under MIRAGE's rules and the
sequel's art from the toolkit (tools/prepare/sequel_art.py into its overrides).

## Networked games (docs/MULTIPLAYER.md)

```
dotnet run --project tests/PwRemake.Tests -- Multiplayer       # host + 2-3 clients over localhost: PvP, co-op, desync, pause, rejoin, server, saves (PW_MP_MINUTES)
dotnet run --project tests/PwRemake.Tests -- Determinism       # FdLibm vs V8 (tests/golden/fdlibm.json, node tests/golden/fdlibm_dump.mjs) and System.Math; culture
```

### Two instances (Godot, xvfb)

Each instance its own user folder (`HOME`), the host first:

```
HOME=/tmp/pwh PW_CONTENT=/home/claude/testcontent xvfb-run -a -s "-screen 0 1600x900x24" godot --rendering-driver opengl3 --resolution 1600x900 --path game -- \
  --config base --host 19055 --mp-name Kacper --players human,human,Aje --skirmish-random --mp-autostart 2 --preload start --quit-after 200
HOME=/tmp/pwc PW_CONTENT=/home/claude/testcontent xvfb-run -a -s "-screen 0 1600x900x24" godot --rendering-driver opengl3 --resolution 1600x900 --path game -- \
  --config base --join 127.0.0.1:19055 --mp-name Zidell --mp-ready --preload start --quit-after 170
```

Co-op: `--host 19056 --mp-mode coop --mp-mission 11 --mp-autostart 2` (`--skip-cine`: the scenes are voted done at
once). Both logs get a line per hash check (`[Net.Lockstep] step 100 (0:04): hash ...`): they must be equal. Results
(2026-11-02, llvmpipe, two instances on one container): the skirmish (2 humans + Aje) and co-op mission 11 ran with
equal hashes at every check (steps 100-400; the software renderer makes both slow - the host waits for the slower
machine), the co-op's opening cutscene ended by both machines' votes. Two instances take ~1 GB each with
`--preload start`.

Co-op campaign mode (docs/COOP.md): `--host 19077 --mp-mode coop --mp-mission 3 --coop-mode campaign --coop-seats
human,ai,ai --coop-tribes Hu,Aje,Ninigi --mp-autostart 1` (one instance: you and two AI commanders; `human,human,ai` and
`--mp-autostart 2` with a `--join` instance for two humans). 2026-11-03, llvmpipe: the lobby with its seats and hero
split, the game with three seats and bases, the co-op panel.

## Determinism

The determinism test runs the same mission twice with the same seed and commands and compares a hash of the world
after N steps; co-op and multiplayer depend on it (docs/ROADMAP.md).

## Playthroughs (every mission to its end, scripted)

`tests/PwRemake.Tests/Playthrough/`: the browser remake's scripted playthroughs, ported file by file -
`CampaignBot.cs` (remake/tests/campaign_bot.js) and `Play00.cs` ... `Play16.cs` (campaign_play_00.js ... _16.js) - and
the ones written here from the missions' own triggers (the JS had none): `PlayB41.cs`, `PlayB42.cs`, `PlayB43.cs`
(BoosterPack 1's missions 4-1 .. 4-3, ids 41-43, and MIRAGE's SEAS versions of them, ids 20-22) and `PlaySeas02.cs`
(MIRAGE's "Seeking Ada", id 23). `PlaythroughTests.Plays` maps a mission id (index.json "id") to its playthrough. A
playthrough is a list of steps (name, what to do each game second, done?, seconds allowed) that plays the main quests
of a mission the way a player would, with `MissionSession` (triggers, computer players on), difficulty 1, seed 1. The
bot gives its orders as the world's commands (applied at once between steps, as the computer player's
`CommandHands`); its cheats are those of the JS bot and counted in the report: the player's units are healed, enemies
near its fighters lose a fifth of their hit points a second, a unit that makes no progress towards its goal for 60 s
is teleported (progress: 2 m nearer the goal, or 8 m from where it stood; for a unit that has no way there
(`Unit.NoWay`: across water - it walks to the shore nearest the goal and stops) only 8 m nearer: jostling in the crowd
at the shore restarted the count of one or another, and a group was carried across one by one over minutes; and one
of them carried across takes the others of the group with no way there along, as a transport would - one by one,
mission 9's army was spread over three islands and the heroes fell alone at the airplane; a goal that moves, an enemy
being hunted, does not restart the count; the count runs only while `go()` keeps sending the unit: one it left alone for more than 10 s -
there, or fighting / standing guard on the playthrough's other orders - starts anew where it stands; "there" is within
6 m plus sqrt(n) m for a group of n plus a big unit's radius, measured from the ring of free ground round a goal on
blocked ground - a building, rocks: where the units' own walk ends), resources topped up where a step says so (and for an upgrade dearer than the stores hold). A
mission with an army budget starts with the army screen's default army, as the campaign menu starts it (the JS took the
level's start army: the Dustriders' resource collector is a card bought with the budget; only for the Dustriders'
own point-buy missions - mission 11's default army as the Norsemen lacks the heroes). Seen on 2026-11-02 and not
investigated: a run's result can depend on the runs before it in the same process (MIRAGE 15 as the SEAS lost alone and
was won in the series) - the table comes from one series run.

```
PW_PLAY=1 dotnet run --project tests/PwRemake.Tests -- Playthroughs                       # all: 20 missions (base: Hu Aje Ninigi) + 24 (mirage: + SEAS) = 156 runs
PW_PLAY=1 PW_PLAY_CFG=mirage PW_PLAY_MISSIONS=9,13 PW_PLAY_TRIBES=SEAS dotnet run --project tests/PwRemake.Tests -- Playthroughs
PW_PLAY=1 PW_PLAY_MISSIONS=41,42,43,20,21,22,23 PW_PLAY_OUT=/tmp/play dotnet run --project tests/PwRemake.Tests -- Playthroughs   # BoosterPack 1 + SEAS
PW_PLAY=1 dotnet run --project tests/PwRemake.Tests -- PlaythroughTable                   # only rebuild docs/PLAYTHROUGHS.md
PW_PLAY_STUCKLOG=1 PW_PLAY=1 PW_PLAY_CFG=base PW_PLAY_MISSIONS=12 PW_PLAY_TRIBES=Hu dotnet run --project tests/PwRemake.Tests -- Playthroughs   # why units are teleported
```

`PW_PLAY_STUCKLOG=1` writes, before each teleport, the unit's state (where it stands, its task and target - for a
fight the zone, line of sight, last shot -, its way, the nav cell under it, whether a fresh search finds a way, the
units and buildings round it, where and when the count began) and, from 20 s without progress, a line every 5 s.

`PW_PLAY_MISSIONS` takes index.json ids (41-43 BoosterPack 1, 20-23 MIRAGE's SEAS missions; "B41" works too); the
default is every mission in the menu's order, a mission a configuration does not have is skipped (20-23 are MIRAGE's).
Each run prints the step log and the report (steps passed with their game times, cheats, quests, sequences, the
player's units and buildings; for a failed step the trigger log's tail, the live triggers and why the step's trigger
did not fire) and writes `Playthrough/results/<cfg>_<NN>_<tribe>.json` (the result) and `results/logs/<...>.txt` (the
report); `docs/PLAYTHROUGHS.md` is rebuilt from all result files after every run (`PW_PLAY_OUT`: another folder).
The test content (2026-11-02): the BoosterPack 1 and SEAS mission folders of /home/claude/testcontent (base 41-43,
MIRAGE 20-23 and 41-43) and both `missions/index.json` were made again with the current prepare tool (`python3
tools/prepare/prepare.py --paraworld /mnt/user-data/uploads/Paraworld --toolkit /home/claude/pw-exporter --content
<scratch> --no-films`, the folders copied over): the old ones had "!boosterpack1.ltf load failed!" / empty texts (no
titles, quest texts or dialogue lines - the playthroughs time dialogues by their texts), from before pack_texts.py. Only
texts changed (triggers, objects identical); the other missions were byte-identical. The base index no longer lists
20-23 (MIRAGE's maps; the prepare tool exports them only for MIRAGE - their old folders stay unused). No file from the
player's PC was needed: the installation copy under /mnt/user-data/uploads/Paraworld has every map.
The data come as for the campaign tests (`PW_CONTENT`, default /home/claude/testcontent; /opt/data/base and
/opt/data/mirage). A run takes 2 s - 5 min (2 - 60 game minutes; mission 7 is the slowest); all 119 of the 17 missions about an hour and a half on two shared cores, the 37 BoosterPack / SEAS runs about 40 minutes.
`PlaythroughTests.SmokeMission03` (mission 3 as the Norsemen, a few seconds) is part of the normal suite.
The results (2026-11-03, main 4d79b54 after port/chase, port/m16gates, port/aiferry, port/viewfix: all 156 runs won -
the 17 missions 119, BoosterPack 1 and the SEAS missions 37; playthroughs 6, 8 and 15 adapted to the merged rules):
docs/PLAYTHROUGHS.md, the menu's states and the failures: docs/CAMPAIGN.md "Mission status". The BoosterPack runs take 3-57 game minutes (4-1 is
the long one: base, epochs, 20 flamethrowers).

## Soak runs (the whole game, headless)

`tests/PwRemake.Tests/SoakTests.cs`, skipped unless `PW_SOAK` is set (they take long):

```
PW_SOAK=1 PW_SOAK_CFG=base PW_SOAK_MODE=passive,bot PW_SOAK_MINUTES=15 PW_SOAK_OUT=soak.md dotnet run --project tests/PwRemake.Tests -- SoakMissions
PW_SOAK=1 PW_SOAK_CFG=base,mirage PW_SOAK_MINUTES=20 dotnet run --project tests/PwRemake.Tests -- SoakSkirmish     # PW_SOAK_PAIRS=Hu-Aje, PW_SOAK_BEHAVIOURS, PW_SOAK_LEVEL
PW_SOAK=1 PW_SOAK_MINUTES=10 dotnet run --project tests/PwRemake.Tests -- SoakDeterminism
PW_SOAK=1 PW_SOAK_MISSIONS=7,13,15 PW_SOAK_MINUTES=5 dotnet run --project tests/PwRemake.Tests -- SoakPerf          # PW_SOAK_PROFILE=0: without the step profile
```

* `SoakMissions`: every mission of the configurations with `MissionSession` (triggers, computer players), the human
  player passive or the soak bot (its starting army attack-moves to the nearest enemy every 10 s when idle); one table
  row per run: triggers fired, quests shown / done, the end, trigger errors, error log lines, NaN positions, units,
  step time; then the distinct error / warning log lines and the trigger warnings.
* `SoakSkirmish`: AI against AI on multi_2_jun_001 (`SkirmishSession`), every tribe pair; per side the timeline of
  remake/tests/ai_match.js (epochs reached at, workers / army at 10 minutes, peak army, attacks of its plan, kills /
  lost, buildings).
* `SoakDeterminism`: a mission with everything on (triggers, brains, the bot) and an AI-vs-AI skirmish, twice with the
  same seed: the same state hash every minute.
* `SoakPerf`: the step time (median, p95, p99, max) on the big missions, the GC's share, and the step profile
  (`StepProfile`: the phases by time and allocation, the slowest step, the slowest single unit update).
Run them from the repository's root (the missions' Lua standard library is found from the working directory).

**The AI report** (`tests/PwRemake.Tests/AiReportTests.cs`, skipped unless `PW_AIREPORT` is set; docs/AI.md "Co-op
allies and stalls"): a probe on every computer player (it only reads the world) prints a line per minute - epoch,
resources, income, workers by job (idle / food / wood / stone / build / fight), stuck workers, units trained and
buildings finished, army and its levels, attacks, the reserve and the build requests with why each waits or failed -
and a stall report (idle workers, stuck workers, building sites unfinished for 3 minutes, no growth for 3 minutes with
resources banked), then a summary table per side (epochs at, workers / army at 10 and 20 minutes, peak army, attacks,
stalls, places not found, support squads, kills / lost). About 30 s per 20-minute skirmish, 10-40 s per mission.

```
PW_AIREPORT=1 dotnet run --project tests/PwRemake.Tests -- AiReport.Skirmish   # PW_AIREPORT_CFG=base,mirage PW_AIREPORT_PAIRS=Hu-Aje PW_AIREPORT_MINUTES=20 PW_AIREPORT_LEVEL=4 PW_AIREPORT_BEHAVIOUR=Giraffe PW_AIREPORT_SEED=1 PW_AIREPORT_MAP=<ula>
PW_AIREPORT=1 dotnet run --project tests/PwRemake.Tests -- AiReport.Coop       # PW_AIREPORT_MISSIONS=1,4,8,9,13 PW_AIREPORT_TRIBES=Aje,Ninigi PW_AIREPORT_MINUTES=15
# PW_AIREPORT_TIMELINE=0: only the stalls and the tables; PW_AIREPORT_OUT=<file>: also appended there
```

### Results (2026-10-31, the integration)

* `SoakMissions`, 15 game minutes, every mission of base and MIRAGE (24 each), passive and with the bot: no exception,
  no error log line, no trigger error, no unknown condition / action, no NaN position in any of the 96 runs. Without a
  player the missions wait at their first quests (9-50 of 94-597 triggers fired); the computer players wake and attack
  as scripted (Turtle / Dodo brains with 1-26 attacks launched). Missions that end without a player, as their scripts
  say: 7 (the pirates' ambush kills a hero at 20 s - the heroes have to flee), 11 (the arena's waves kill a hero after
  40-90 s), 12 (the telescope tower falls at 70-100 s), 16 (a hero falls in the first minute), 14 (base, 7.5 min), 10
  (MIRAGE, 10.6 min); with the bot (it attacks the nearest enemy, which the story often does not want) also 2, 3, 5,
  8, 9, 10, 23, 42 / B42, 43 / B43. The MIRAGE missions 20-23 and 41-43 have no title in that content
  ("!boosterpack1.ltf load failed!", "!cep.ltf load failed!": the missions' text tables of the export, not the
  simulation; the prepare tool's pack_texts.py reads them since, and the test content was made again 2026-11-02 -
  "Playthroughs").
* `SoakSkirmish`: docs/AI.md "AI against AI".
* `SoakPerf`: docs/SIMULATION.md "Step profile".
