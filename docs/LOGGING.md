# Logging

Everything the game does that helps to find a bug is logged: start-up (versions, paths, configuration), loading,
every mission's set-up, trigger activity, errors with their stack traces. One logger for the simulation and the
Godot side (`src/Sim/Core/Log.cs`).

## In the code

```csharp
static readonly Logger L = Log.For("Campaign.Triggers");     // one per class, a category per topic

L.Info("mission 11: 73 triggers loaded");
L.Debug(() => $"condition {c} -> {state}");                 // the lambda runs only when Debug is on for this category
L.Warn("object 'Cole_s0_0' not on the map");
L.WarnOnce(modelName, $"missing model {modelName}");       // once per session per key
L.Error("action SpawnGroup failed", ex);                    // with the exception
```

Levels: `Trace` < `Debug` < `Info` < `Warn` < `Error` < `Fatal`. Categories are dotted: a level set for
`Campaign` applies to `Campaign.Triggers` unless that has its own. Every line has the wall clock, the game time
(seconds since the game started; blank in menus) and the category:

```
17:10:01.919  1234.55 INF [Campaign.Triggers] Gg01: Spawn Aje Group fired
```

### Categories in use

| category | what |
|---|---|
| `App` | start-up, screens, settings, exit |
| `Content`, `Content.Assets` | configurations, missing files |
| `UI.*` | menus, loading screen, theme |
| `Game` | the running game: load steps, pause, speed, end |
| `Rules`, `World`, `Systems.*` | the simulation |
| `Ai.*` | the computer players |
| `Campaign`, `Campaign.Triggers`, `Scripting.Lua` | missions, the trigger engine, Lua scripts |
| `View.*` | models, terrain, effects, sound |
| `Editor` | the map editor: documents opened and made, Test map, strokes (Debug), its test runs (`EDITOR` lines) |
| `Crash` | unhandled exceptions |

## Where it goes (`logging.json`)

The user folder holds `logging.json` (made with the defaults on first start):

```json
{
  "level": "Info",
  "categories": { "Campaign.Triggers": "Debug", "View.Models": "Warn" },
  "sinks": ["file", "memory", "godot"],
  "keepFiles": 10,
  "memoryLines": 4000
}
```

| sink | |
|---|---|
| `file` | `logs/game_<date>_<time>.log` in the user folder, one per run, the newest `keepFiles` are kept; warnings and errors are flushed at once |
| `memory` | the newest lines in memory: the in-game log window and the error report |
| `godot` | Godot's output panel (editor) / the console; errors also in the editor's Debugger → Errors |
| `console` | stdout / stderr - headless runs and tests |

The user folder: Windows `%APPDATA%\Godot\app_userdata\ParaWorld Remake\` (Godot's `user://`).

## In the game

* **F10** - the log window: the newest lines, filtered by level and category; *Copy*; *Log folder*; a command line:
  `log Campaign.Triggers debug` changes a category's level, `log debug` the default level, while the game runs.
* **The error banner** - when an error is logged a red line appears at the top with the message; a click copies an
  error report (versions, configuration, log file, the last 300 lines) to the clipboard, ready to paste into a bug
  report.
* **F9** - the performance line (fps, frame time, draw calls, memory).
* **F12** - a screenshot into the user folder.
