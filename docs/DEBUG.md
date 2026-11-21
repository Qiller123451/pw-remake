# Debugging: options, tools, keys, command line

The same debug features as the browser remake (its docs/DEBUG_OPTIONS.md), plus the log (docs/LOGGING.md).

## Debug options (before a game)

The campaign menu's **Debug options …** opens a window of options that change the rules of the next game. They are
kept in `settings.json` (`debug`) and apply while **Use the debug options** is ticked (any change ticks it); the
line in the campaign menu says what is on. **Defaults** puts every option back. A game with debug options says so
on the HUD.

| Option | What it does |
|---|---|
| Everything free and instant (you) | buildings stand at once, units are trained and upgrades researched at once, nothing costs anything, level-ups need no skulls |
| Whole map visible | no fog of war |
| Ignore the mission's bans | nothing the mission forbids (AntiActions, TECH triggers) is forbidden for you |
| Computer players: do not attack you | the computer players' own attacks leave you alone (they defend; scripted attacks still come) |
| Food, wood, stone, skulls | your start resources (empty: the game's or the mission's) |
| Storage limit per resource | your storage is at least this big |
| Gathering × | each load delivered is worth that much more |
| Construction, training, research speed × | your buildings go up and your units and upgrades are done that much faster |
| Unit limit, No houses needed | your population limit, and housing not needed |
| Places in the army pyramid per level | e.g. six units of level 5 |
| Start in epoch / Highest epoch (you / enemies) | epoch upgrades given at the start / the ones above do not exist |
| Damage dealt × / Defence × (you / enemies) | 0 = no damage; defence 2 = half the damage taken |
| First-shot delay | the original's first-shot delay: a ranged character's first shot after raising its weapon comes 0.4 s later (Béla 0.6 s) - the shot after it too. Ticked by default (the original's rule); unticked: no delay. For every player |
| First-shot delay in seconds | one time for every unit instead of the original's 0.4 / 0.6 s (empty: the original's; 0 - 5 s) |

*You* is the human player; *enemies* are the players hostile to you and everything without an owner.

The first-shot delay is the original's rule (docs/SIMULATION.md "Setting up and fight positions"): without debug
options every game has it as the original (`World.FirstShot`, set by `Cheats.Apply` from `DebugOptions.FirstShot` /
`FirstShotDelay`). The options are part of the `GameStart` (`debug`), so a saved game, a replay and every machine of a
multiplayer game (the host's debug options) play with the same delay. Settings from before it have the original's.

Code: `src/Sim/Game/DebugOptions.cs` (the options, normalisation, the summary line; from the browser remake's
`game/cheats.js`), `game/src/UI/DebugOptionsWindow.cs` (the window). The rules read them where they decide
(damage, costs, build times, pyramid, fog, AI attacks) - see the systems' code.

## Debug tools (in a game)

With debug options on, the pause menu (Esc) has **Debug tools**: +1000 food/wood/stone, +500 skulls, next epoch,
free and instant on/off, heal everything, selected: level up / destroy, put down an object (any class, side and
level), fog of war on/off (a command too: the fog is the world's), win / lose the mission, mark an objective as done, destroy a player's units and
buildings, copy a report of the objects in view.

## Keys

| key | |
|---|---|
| F9 | performance line (remembered: Options > Graphics "Show performance") |
| F10 | log window |
| F11 / Alt+Enter | fullscreen on / off (Options > Graphics "Window": windowed, maximized - the default -, fullscreen, exclusive fullscreen; settings.json `window`) |
| F12 | screenshot (user folder) |
| F5 / Ctrl+S, F8 | quick save, quick load (in a game: docs/SAVES.md) |

## Command line

Godot passes everything after `--` to the game:

```
godot --path game -- --config mirage --mission 11 --tribe SEAS --difficulty 2 --debug --seed 7
godot --path game -- --screen campaign --quit-after 3 --screenshot /tmp/menu.png
```

| option | |
|---|---|
| `--config <id>` | the game configuration |
| `--mission <n>` | start campaign mission n at once (with `--tribe`, `--difficulty 0..3`, `--seed`) |
| `--skirmish [map]` | start a skirmish at once (docs/SKIRMISH.md "Quick start"): a map file name / path below Data / `random`, `--players Hu,Aje,...` (the first is yours), `--teams 1,2`, `--levels 5,8`, `--aib Dodo,Giraffe`, `--hq 0,1`, `--mode deathmatch\|domination\|defender`, `--points -1\|1000\|3000\|10000`, `--seed`, `--no-fog`, `--debug`; without options the stored setup of the skirmish menu |
| `--end-at <game s>[,lost]`, `--end-stats` | the game ends then, won (or lost) - the end screen; with `--end-stats` the statistics screen over it (skirmish and campaign) |
| `--sequel <n>`, `--scenario <n>` | start a mission of Containment / a scenario at once (docs/SEQUEL.md; with `--tribe`, `--difficulty 0..2`, `--seed`, `--debug`; Containment switches to the MIRAGE configuration); `--skip-briefing`: no briefing window |
| `--editor [--map <name>]` | the map editor (docs/MAP_EDITOR.md) with that map (a name, id or path of a converted map - docs/MAP_FORMAT.md; default: a new flat map); `--editor-selftest`: a raise stroke at the camera's target, checked, undone, checked, then a stroke of every terrain and paint tool, each undone exactly (`EDITOR ok / FAIL` lines, `EDITOR selftest ok`); `--editor-tool <name>`: that tool chosen (Smooth, Plateau, Ground, Forest ...); `--editor-demo`: a plateau, painted ground, a forest and plants at the camera's target (screenshots); `--editor-objects-demo`: a few buildings, units and an animal at the camera's target, one selected (screenshots); `--editor-triggers-demo`: two regions, a quest, a variable and two triggers, the trigger editor open (with `--editor-tool Regions`: the Regions group; screenshots); the self-test also draws a region and makes a trigger in the trigger editor (`EDITOR mission selftest done`); the self-test also runs the object tools (`EDITOR objects selftest ok`); `--editor-test-map <game s>`: a stroke and a building of player 1, then Test map, back to the editor at that game time (`EDITOR test map objects ok`), the map checked unchanged (`EDITOR test map ok`); `--editor-walkthrough [--editor-shots <dir>]` (start without `--map`, under xvfb): docs/MAP_EDITOR.md's tutorial played by itself - a new map, a hill, painted ground, a forest, a base of each of two players, a region "Goal" and the trigger "a character of player 0 in Goal: won", saved into My maps, opened again (the same map), Test map, a worker sent into Goal, the mission won (`EDITOR walkthrough ok`; the pictures `map_editor_walkthrough.jpg`, `_won.jpg`; the game quits) |
| `--custom-map <name>` | a map of My maps with triggers played as a mission (Campaign → Custom campaigns: docs/MAP_EDITOR.md "Playing your map"); `<name>` the folder or `.pwmap` name, `User/<name>` or a path; `--screen custom` shows the campaign menu's Custom campaigns tab |
| `--army-mission <n>` | the army screen of campaign mission n (a mission with an army budget) |
| `--debug` | with "free and instant" on (plus the saved debug options) |
| `--screen campaign\|sequel\|scenarios\|skirmish\|army\|debug\|setup\|load\|options\|controls` | open a menu directly (`army`: "My Army" of the stored skirmish setup's first slot; `load`: the title screen's Load game; `options` with `--options-tab 0..4`: the Options window at Graphics / Advanced / Sound / Gameplay / Controls, docs/MENUS.md) |
| `--quit-after <s>` | quit after s seconds (tests) |
| `--screenshot <file>` | save a screenshot before quitting |
| `--save-at <game s>` [`--save-to <file>`] | save the game once its time is reached (default: `saves/Test save.pwsave` in the user folder); the log line `SAVED <file>: step, game time, state hash` (docs/SAVES.md) |
| `--no-intro` / `--intro` | skip the start films once / play them in a test run (`--quit-after`, `--screenshot`, `--shots` skip them otherwise: docs/MENUS.md "The start films") |
| `--continue` | the title screen's Continue Campaign: load the newest save that is not a skirmish |
| `--load <file>` | load a saved game at the start; the log line `LOADED <name>: step, game time, state hash` (the SAVED line's hash, or why the replay stopped) |
| `--spawn <class>[*n][:foe\|wild\|p<id>[:level]][,...]` | put objects down next to your first unit at game time `--spawn-at <s>` (default 3; the debug tools' put-down) and centre the camera on the first - view checks with `--shots <dir> --shot-every <s>`; `class*n`: a crowd of n, 3 m apart (the sound checks: docs/AUDIO.md) |
| `--spawn-together` | with `--spawn`: every object at the first one's place, not in a ring (a crowd around a building: the units behind it, docs/VIEW.md "Units behind buildings") |
| `--spawn-ring` | with `--spawn`: the objects evenly on a 14 m circle 34 m from your unit (with sides `p<id>`: every player's colour in one view, docs/SKIRMISH.md "16 players") |
| `--spawn-select` | with `--spawn`: a second later the objects put down are selected and the camera shows them all (their selection circles in a screenshot) |
| `--mark-check <game s>` | a campaign mission: the first question mark its triggers show with a text is shown, the camera goes there, the mouse moves over it and clicks it - `MARK ok / FAIL` lines (hover tooltip, picked), the mouse stays over it for `--screenshot` |
| `--use-move <id>[:<game s>]` | your first unit that offers the special move uses it then (default 3 s after `--spawn-at`) on the nearest enemy, through the player's command; with `--shots <dir>` screenshots a quarter, half and nine tenths into its act and after it (`move_1..4.png`), MOVE lines in the log. A hero at the level its move needs: `--spawn Cole_s0::3` (class:side:level). docs/SIMULATION.md "Special moves play out" |
| `--outbreak-shots <dir>` [`--outbreak-shot day\|valley\|night\|boss`] | with `--mission 90` (Outbreak, docs/OUTBREAK.md): the mission's screenshots - the plateau's base by day, an infected town in the valley, a wave of the dead put down for the picture coming up ramp A into camp A at night, a plague lord out of its town at night (the light of the middle of a night: `GameSession.CycleViewTime`; the dialogue box hidden) - into `<dir>` (`outbreak_*.png`), then the game quits |

In a game (docs/GAME_SESSION.md "Command line"): `--menu-at <game s> --menu-page options [--options-tab n]` opens the
pause menu's Options for a screenshot; `--cine <sequence> [--cine-at <game s>]` plays a mission's cutscene and logs
`CINE` lines (scene time, day time, hidden objects, lip-sync weights).

The model viewer (`res://scenes/ModelViewer.tscn`) takes `--level=<0..4> --damage=<0..2>` (a building's state) and
`--yaw=<deg> --pitch=<deg> --dist=<m> --look-y=<m>` for a
close look (a face: `--model=cole_s0 --yaw=180 --pitch=-5 --dist=1.6 --look-y=3.7`).

Environment: `PW_CONTENT=<folder>` uses another content folder (tests).

## Performance

The F9 line (Boot.cs): `fps`, `process` = Godot's TIME_PROCESS - the **longest** whole frame of the last second
(every node's `_process`, the HUD and menus included, and the renderer's own work on the main thread: culling, draw
calls, shadows), `draws`, `MB` = Godot's static memory (not the GPU's, not the .NET heap). The second line is
GameSession's own: `frame` = the average of GameSession._Process alone (simulation, events, input, entities, effects,
view, overlay: each an average in ms). So `process` far above `frame` means spikes and/or work outside the session
(rendering, the HUD); 1000 / fps - frame is the rest of an average frame.

A fixed scene measured (GameSession.Perf.cs):

```
godot --headless --path game -- --load <save> --perf 300 [--perf-wait 60] [--perf-steps 2] [--perf-pan 0.5]
```

`--perf <frames>`: after `--perf-wait` frames (default 60), that many frames with exactly `--perf-steps` simulation
steps each (default 2: x1.5 at ~30 fps) and a fixed interpolation - the same steps and the same pictures on every run
of the same save -, then a `PERF` report in the log and the game quits (`--perf-stay`: it goes on): the sections per
frame, the finer parts (`PerfProbe`: entities, view, overlay), the frame's median and maximum, the managed memory
allocated per frame and the collections, the engine's monitors (draws, objects, nodes, static and video memory), the
simulation's phases (StepProfile). `--perf-pan <m>`: the camera moves that far each frame, to and fro over 200 frames
(the work a moving view does: the props' instance buffers). Edge scrolling is off while measuring. `--headless`
measures the main thread's own work without the software renderer of a test machine (no draw counts then).

A place coming into view for the first time: `--perf-to <x,z | object name>` moves the camera from where the game
starts it (the player's home; `--cam` to start elsewhere) to a point or a campaign object (`V1_hu_kennel_4`: the
Outbreak's first village) over `--perf-move <frames>` (default three fifths of `--perf`), revealing the fog round the
camera every 10 m as a scout walking there would (`--perf-reveal <m>`, default 45; 0: none). The report then lists
the **slowest frames** with their sections and the view parts that took the most in each (`ent:infect` - the infected
look switched on, with its parts `inf:materials`, `inf:rest meshes`, `inf:effects`, `inf:effect models`,
`inf:flies emitter`, `inf:blight`, `inf:new shader`; `entities:infected` - the looks prepared ahead out of sight,
docs/INFECTED_LOOK.md "Performance"), and the **slowest whole frames** (one frame's end to the next: the renderer
too - a shader compiled at its first drawing shows there, not in the session's sections; with a renderer, not
`--headless`). The Outbreak's first village (2026-11-20):

```
godot --headless --path game -- --config base --mission 90 --perf 300 --perf-wait 40 --perf-to V1_hu_kennel_4
xvfb-run -a -s "-screen 0 1280x720x24" godot --rendering-driver vulkan --resolution 1280x720 --path game -- <the same>
```

A run with `--perf`, `--outbreak-shots`, `--save-at` or `--end-at` starts the mission without waiting for the loading
screen's ready button (as `--quit-after` and `--screenshot` do): such a run without `--quit-after` sat at the button
for good - the Outbreak "load" that never ended in the container.

`--perf-mem` (with the loading): a `MEM` line per model loaded (static memory it took, clips, keys, loading steps
with the memory after each) and the total after loading.

A late-game save without playing it in the game (tests/PwRemake.Tests, SavesTests): `PW_PERF_SAVE=<file>
[PW_PERF_SECONDS=860] dotnet run PerfSaveSkirmish` (you and five computer players of level 8 in three teams on
multi_6_sav_601; `PW_PERF_PLAYERS=16`: you and 15 computer players, each its own team, on the huge random jungle), `PerfSave` (a campaign mission, PW_PERF_MISSION). `PerfReplaySkirmish` replays such a save and
profiles PW_PERF_STEPS steps of the simulation (the phases, the AI's modules `ai.*` / `eco.*`, the collections) and
prints the state hash after them - the same hash before and after a change that must not change the game.
The replay also checks the save's own state hashes on the way (a change that alters the game fails there), and prints
the managed memory allocated per step, the collections (gen 0/1/2, their pauses, the last one: its pause and what it
promoted), the slowest step with and without a collection in it, the slowest single unit update and the slowest path
search (`NavGrid.Find`: from, to, grid, the unit's need in cells, coarse way or not). Probes deep in the simulation
report through `StepProfile.Current`: `nav:find`, `nav:coarse` (the coarse graph built), `nav:clearance` / `nav:clearance
part` (the clearance grid whole / near the changes), `tt:rebuild` / `tt:rebuild local` (a tech tree rebuilt).
`PW_PERF_ALLOC=1`: what is allocated, by type (tests/PwRemake.Tests/AllocSampler.cs: the runtime's allocation ticks,
about one per 100 KB). In the game's PERF report, `hud:process` (the HUD's own frame: minimap, refreshes),
`hud:minimap update / upload / paint` and `hud:draw <panel>` (each panel's drawing) are listed with the view's parts.

### What the second round found (2026-11, port/perf2)

The spikes of a long game were mostly **garbage collections**: the simulation allocated 400 - 900 KB a step, so a
gen 1 collection came every 10 - 20 s of play and paused for 100 - 300 ms (it moves what survived: tech tree copies,
memoised values). What was done, all without changing the game (the same state hashes on the replays):

* The players' tech trees share the original's nodes (frozen, `TtNode.Freeze`) and copy only the nodes a filter changes
  (`TtNode.Thaw`, copy on write in `TT.ApplyMod`) - a whole copy of the tribe's tree per rebuild was most of the
  garbage. `Rules.Def / Stats / WeaponSetOf / WeaponStatsOf / ActionsAt / ActionsOf` memoise by a struct key
  (`TechTree.MemoKey`) - no string built and no closure per question.
* `FindTarget` without a local function and delegates per search (reused lists); the timers due without two new lists
  a step; a unit fighting in range keeps its empty path; `AnimCtl.Play` builds the `#l` clip name only when it starts.
* Long steps of the path finder: the **coarse graph** of the far searches is kept up to date by blocks (Mark / MarkCircle
  note the changed blocks; a whole rebuild of a 16-player map was 10 - 50 ms after every building placed or destroyed),
  the **clearance** grid of the big units only near the changed cells (capped at `NavGrid.CLEAR_CAP` = 32 cells: only
  ever compared with a need of a few cells), the gate cells marked per search instead of a lookup per neighbour. Both
  give exactly the whole rebuild's result (WorldTests.NavCoarseIncremental). The slowest single path search of the
  replays is now 4 - 12 ms; searches were not time-sliced (that would change the paths).
* The minimap (HudMinimap / MinimapModel, 30 times a second): the fog paint only when the cells' fog states changed (the
  fog's version changes with every fog step), the scaled map copied unless the cells, zoom or focus changed, one image
  reused, no lists per frame - the same pixels.

| measured (this container: 2 shared cores, timings noisy) | before (main eb2fdd7) | after |
|---|---|---|
| 6 players, 860 s (308 units, 245 buildings), 1200 steps: allocated per step | 407 KB | 142 KB |
| collections (gen 0/1/2), pauses | 3 / 3 / 0, 326 ms | 1 / 1 / 0, 106 ms |
| slowest step | 150 ms (a collection) | 124 ms (a collection); 26 ms without |
| 16 players, 1380 s (583 units, 500 buildings): allocated per step | 884 KB | 301 KB |
| collections, pauses | 10 / 10 / 2, 1284 ms | 3 / 3 / 0, 251 - 427 ms |
| ms per step | 7.6 | 4.9 - 6.3 |
| slowest step | 243 ms (a collection) | 101 - 192 ms (a collection); 23 - 46 ms without |
| game frame, 6-player save, `--perf 300 --reveal`: HUD (`hud:process`) | 2.7 ms, 142 KB | 2.0 ms, 3 KB |
| managed memory allocated per frame (2 steps) | 531 KB | 391 KB |

Left as they are: the collections still pause ~100 ms each on this machine (fewer of them; the .NET GC settings -
server GC, smaller gen 0 budgets - were tried on the replays and did not shorten them: not changed); `world:refresh`
(every unit's values read again after its player's tree changed, 15 - 30 ms twice a second while upgrades come in -
sharing the local trees' memoised values changed the game and was dropped); the particles (`view:particles`, ~3 ms
here per frame with a base in view); the draw calls (units and buildings are separate models with their own party
colours, setting textures, damage / epoch states and animated parts - not instanced; draws were counted, not changed).
