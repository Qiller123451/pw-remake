# Saved games

Save and load a running game: the pause menu's **Save game** / **Load game**, **Load game** on the title screen,
**F5** / **Ctrl+S** quick save, **F8** quick load, an **autosave** every N minutes (Options: off, 5 ... 60 minutes;
`settings.json` `autosaveMinutes`). The browser remake's `game/save.js` and `ui/saves.js` (pw-exporter 98c7703) are
the model for what the player gets; the way the game is written down is different - this file says how and why.

```
src/Sim/Game/SaveGame.cs           the file: header, start, flags, commands, answers, hashes, view, thumbnail; read / write / list
src/Sim/Game/GameRecorder.cs       GameRecorder (records a running game, Capture -> SaveGame), SaveReplay (loads one: replays it)
src/Sim/Game/PresenterTap.cs       the interface's answers to the mission, recorded and replayed
src/Sim/Commands/CommandCodec.cs   commands as JSON
src/Sim/Commands/SceneEndCommand.cs  a cutscene / dialogue scene is over (the mission UI) - a command now
src/Sim/Game/DebugToolCommand.cs   the debug tools (moved from the interface: the fog switch, win / lose, quest marks included)
src/Sim/World/FogOfWar.cs          the fog of war, kept and updated by the world (was the view's)
game/src/App/GameSession.Saves.cs  recording from the first step, saving, autosave, loading, the view after loading
game/src/UI/SavesPanel.cs          the list of saves (thumbnail, header), the title screen's Load window (LoadGameScreen)
game/src/UI/TitleScreen.cs         Continue Campaign: SaveGame.NewestCampaign - the newest save that is not a skirmish
game/src/UI/Game/PauseMenu.cs      Save game / Load game windows, the autosave option
tests/PwRemake.Tests/SavesTests.cs play, save, load, play on: the same world (base and MIRAGE)
```

## Where and what

Saves are files in the user folder (`AppPaths.SavesDir`: `saves/` beside `settings.json`; on Windows
`%APPDATA%\Godot\app_userdata\ParaWorld Remake\saves`): `<name>.pwsave`, `quicksave.pwsave`, `autosave.pwsave`.
A `.pwsave` is a zip archive anyone can open:

| entry | |
|---|---|
| `header.json` | readable: `format` ("pwremake-save"), `version`, `build` (the game's version), `name`, `kind` (manual / quick / auto), `date`, `mode`, `configuration` (+ title), `mission`, `missionFolder`, `title`, `tribe`, `difficulty`, `debug`, `gameTime` (s), `steps`, `commands` |
| `game.json` | `start` (the GameStart - with the campaign colour chosen, `color` / `coop.seats[].color`: docs/CAMPAIGN.md "Your colour"), `setup` (a mode's own setup; null for a campaign mission), `flags` (SimFlags), `steps`, `commands`, `answers`, `hashes`, `view` |
| `thumb.jpg` | the game view, 256 px wide (when the pause menu opened, or at the quick save / autosave) |

A save of 3 game minutes is 1-2 KB plus the picture; an hour of play is a few hundred KB at most (only the player's
own orders are in it).

## The design: a replay, verified

The simulation (`src/Sim`) is deterministic: fixed 50 ms steps, the world's own random numbers, every change by a
player a command (`src/Sim/Commands`) applied at a step - built for lockstep co-op and PvP (docs/ARCHITECTURE.md).
There were two ways to save it:

| | (a) the state (the JS `save.js`) | (b) the inputs (chosen) |
|---|---|---|
| what is written | every object of the running game: entities with their tasks, paths, states, animation controllers and attached parts; players and tech trees; the AI brains with their plans; the registry, regions, the trigger engine with every condition's state, timers, queues; the Lua state of `custom.lua` / `Script` closures; delayed actions; the random generator; the walk grids, wall map, fog | the GameStart (mission, tribe, difficulty, debug options, seed), how the simulation runs (SimFlags), and everything that came in from outside since: the players' commands with their steps, the interface's answers to the mission; plus a hash of the world every 30 s and the player's view |
| loading | build the mission again, delete its objects, make the saved ones and fill them, rebuild what is derived (the JS: 430 lines, anchors, `revive` per class) | build the mission again, run its steps with the saved inputs (on the loading screen, while the models load) |
| exactly the same world? | only if every field of every class is saved and revived right - a forgotten field is a silent difference (the JS found several: triggers all off after loading, the regions forgotten, a nest's areas ...) | yes by construction: the same code runs the same steps; the hashes prove it on every load |
| maintenance | every new field of any system must be added to the saver (C# has no generic object graph of plain objects as JS has); MoonSharp's Lua closures and upvalues (a `custom.lua` that keeps state) cannot be serialised at all | none for new rules or systems; a new kind of input (a new command class) is plain data and saved by `CommandCodec` without code |
| robust against | reading old saves after field changes (with migration code) | nothing changes in the format when the rules change; but a save replays the same only with the same simulation code - another version of the game may go another way (the hashes find it, below) |
| file size | the whole world: MBs | KBs |
| load time | ~ the set-up | the set-up + the replay of the game time: the steps run 30-1000x faster than real time on this container (below) |
| co-op / PvP / replays | a separate path | the same path: the inputs are exactly what lockstep sends; a save is a replay file |

(b) is the robust one: it needs no code per system, cannot miss a field, works for every mode that is a
deterministic simulation with commands, and every load verifies itself. Its costs are the load time (proportional to
the game time) and that a save is tied to the simulation code it was made with - both handled below. A state snapshot
can still come later as a cache *on top* (a snapshot of the world every N minutes inside the save, checked against the
replay's hash): the inputs stay the truth.

### Making every change of the world an input

A replay is only the same game if nothing reaches the world except through the recorded inputs. The audit of the
game's code (everything in `game/` that wrote to the world or that the world read from the view) found these, now
fixed:

| what | before | now |
|---|---|---|
| the fog of war | the session's grid, updated from the frame loop (every 0.2 s of steps, and at frames: after a debug tool, when a cutscene began, the oracle's reveals when the frame's events were handled); the human's units do not pick targets in the fog (Combat), effects and the campaign's question marks ask it - so the game depended on the frames | the world's (`World.EnableFog`, `FogOfWar.cs`): updated at the end of every 4th step and at the end of a step that revealed or hid an area or switched it; the oracle's circles from the step's events; the view only draws it |
| the event list | the view drained `World.Events` once per drawn frame; the campaign and the computer players' defence read it by index, so events they had not read yet (those of the AI and campaign phases of the last step) were lost - after one step or after eight, as the frames fell | readers keep absolute positions (`World.EventBase`); the game turns on `ViewEvents` (the view gets its own copy) and `TrimEvents` (the world drops events older than the step before, at the start of a step) - nothing is lost to a reader, the same on every machine. Headless runs without the flags behave as before |
| a cutscene's or dialogue's end | the mission UI called `TriggerEngine.EndSequence` / `EndDialog` at a frame | `SceneEndCommand` (applied at the next step, recorded) through `SessionEngineLink` |
| a cutscene's actors | the cutscene player moved the world's units and props every frame, played clips on their animation controllers (callbacks, `LookChanged`), set `BusyAnim`, and at its end the idle clip | the stage poses them for drawing only (its own AnimCtl per actor: `EntityViews.AnimOf`); the lasting result - the idle clip, the end pose of every actor that the timeline moved, snap back - is the world's, in `SeqActors.Finish`, applied with the end command (`SceneEndCommand.Timeline`: the timeline ran - a film that plays shows none) |
| the debug tools | a command, but with callbacks to the session (win / lose, mark a quest) and the fog switch changing the view's grid | `DebugToolCommand` in `src/Sim/Game`, plain data; "fog" is a tool |
| `--spawn`, `--reveal` (test options) | put objects down / revealed the fog directly | debug tool commands |
| the interface's answers (is the mission UI busy, does it play this scene, where is the camera, is this selected, the profile) | asked by the trigger engine at its steps | asked through `PresenterTap`: recorded, answered from the record when replaying (no call reaches the real UI then) |

Not inputs (made again by the replay): the computer players' commands (decided inside the step), the trigger engine
and Lua (inside the step), the AI's and the world's random numbers. Left as they are (test tools that are not part of
a played game): `--test-placement` puts a wall line down and finishes buildings directly (saving is refused in such a
run); the playthrough bot applies its commands between steps (its tests do not save).

### Loading

`GameSession.LoadGame(file)` reads the save (`SaveGame.Read`; another format version is refused here with a message,
never half loaded), switches to the save's configuration, and shows the mission's loading screen with the save:
`GameSession.Load` creates the mission with the save's GameStart and SimFlags (the tap answers from the record from
the start: the trigger engine reads the player's profile while it is set up - the profile of the saved game's start,
not today's), then replays the steps on a worker thread (`SaveReplay`) while the main thread loads the models;
"Restoring the saved game 12:30 / 45:00" shows the progress. After the replay:

* the recorder of the new session has recorded the replayed inputs again, so a save of the loaded game holds the
  whole history;
* the player's view comes back (below) and the game goes on.

**Verification.** Every 30 s of game time (600 steps) the save holds the state hash of the mission
(`MissionSession.StateHash`: the world's and the campaign's); the replay compares each one, and the tap checks that
every question of the mission comes at the step it came in the saved game. A difference stops the replay there: the
world is a valid game, but not the saved one - a save of another version of the game whose rules changed. The rest
of the saved inputs is dropped (they were for another world), the game goes on from that moment, and the player is
told: "This game was saved by another version of the remake: it could be restored up to 12:30 of 45:00". The log
says which hash or question differed.

**Versions.** `header.json` / `game.json` carry `format` and `version` (`SaveGame.FormatVersion`, now 1). A save of
another format version is refused cleanly (`SaveFormatException`: "saved by a newer version" / "an older version ...
cannot be loaded any more"); the load window lists such files greyed out with the reason. Raise the version when the
file changes in a way an older reader cannot read, or when an input changes its meaning (a command's fields renamed,
a new SimFlags default). Changes of the rules do not need a new version: the hashes catch their effect.

**Load time.** The replay runs the simulation as fast as it can: on this container (2 shared cores, other processes
running) a 3-minute save of mission 1 replays in 2-4.5 s (40-90x real time), the tutorial at 260-1000x, MIRAGE
mission 3 at 30-50x; the step-time measurements of the big missions (docs/SIMULATION.md "Step time": median
0.5-1.2 ms on missions 7, 13, 15) put an hour of mission 7 at one to two minutes here, less on a desktop CPU. That
is the price of the design; the snapshot cache above is the way to cut it if long missions need it.

### What the view gets back (as the JS's restore)

`GameSession.SaveView` / `AfterLoad`: the camera (position, turn, distance), the selection, the control groups and
the camera views under the digits and the group selected (`GameInput.SaveState`), the mission UI's quest log state
(which quests were read - the "new" marks -, the flashing quest button, the quest chosen in the log) and its minimap
markers with their age (`MissionUiState.SaveView`), the player's pause. From the world: the info bar
(`TriggerEngine.InfoBarShown`), the question marks (the presenter adapter keeps them until the UI exists), a MUSI
music track of the mission (the last one the replay asked for). A cutscene or dialogue scene that was on show begins
again (the JS's `resumeShows`) and ends by command as always. Not kept (as in the JS): the news ticker's lines, the
level music's position, the game speed (a setting).

Saving is refused while a cutscene shows, after the mission's end, and while a loaded game is still being restored.
The autosave waits until the game runs (not paused, no menu, no quest log, no scene on show) - IngameScreen.usl's
UpdateAutosaveTimer, as in the JS.

## Other modes (skirmish, networked games)

**Skirmish** (docs/SKIRMISH.md): saved and loaded like a mission. The save's `GameStart.Skirmish` is the setup (also
written as the save's `setup`); `GameSession.LoadGame` rebuilds the skirmish from it through the same path as a new
game (`SkirmishSession.Create` with the save's `SimFlags`, `IGameSim.StateHash` = the world's) and replays it; the
end screen's "Load Game" opens the load window. Test: `SaveLoadContinueSkirmish` (MIRAGE, SEAS against Aje with 1000
points and debug options: play 3 minutes, save, load, play on - the same hashes).

Nothing in the format or the recorder is campaign-specific: a save holds the `GameStart` (with whatever a mode puts
in it), an optional `setup` JSON for a mode's own set-up object, `SimFlags` (applied with `SimFlags.Apply(world,
human)`), the commands (any `Command` class; `CommandCodec` finds classes of every loaded assembly) and the
interface's answers (only missions ask). To save a skirmish: create the session with the flags, attach a
`GameRecorder(world, hash, null)` before the first step, `Capture(start, flags, setup: <the setup as JSON>, view:
...)`; to load it: create the session from the save's start / setup / flags, `new SaveReplay(save, world, hash,
null).Run()` - what the skirmish does.

**Networked games** (docs/MULTIPLAYER.md "Saves and replays"): the host saves; the save's GameStart has the seats,
its answers are LockstepPresenter's - loading it (also a dedicated server's turn record without answers or hashes)
asks that presenter again during the replay (`PresenterTap.DeterministicInner`).

## Tests and test runs

`dotnet run --project tests/PwRemake.Tests -- Saves` (`PW_SAVES_MINUTES`: minutes played before saving, default 3):

* `SaveLoadContinueBaseTutorial`, `...BaseMission1` (hard), `...MirageMission3AsSeas`: a session with a scripted
  mission UI (plays every scene and ends it by command some steps later, busy while a cutscene waits, moves its
  camera and selects now and then, has a profile) and scripted orders (attack-moves, group walks, a debug tool) plays
  N minutes, saves to a file, a fresh session replays the file (with another profile today); the state hashes, the
  pending commands and the trigger firings are equal, the loaded game's UI was never asked; both then play 1.5 more
  minutes with the same inputs - equal hashes every 15 s; a save of the loaded game replays to the loaded game.
* `DivergenceStopsTheReplay`: a tampered hash stops the replay at that step, the rest of the commands is dropped,
  the interface answers again, the game goes on.
* `FileHeaderAndVersion`: header, start, flags, hashes, answers read back; a newer format version and a file that is
  no save are refused.
* `CommandCodecRoundTrip`: every command class (NaN, tuples, nullables, `[JsonIgnore]`), an unknown class refused.
* `TapRecordsAndReplays`.

In the game (docs/DEBUG.md): `--save-at <game s> [--save-to <file>]` saves once the game time is reached (log line
`SAVED <file>: step, game time, state hash`), `--load <file>` loads a save at the start (log line `LOADED <name>:
step, game time, state hash` - the same hash as the SAVED line, or the divergence).
