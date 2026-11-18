# Multiplayer: PvP skirmish and co-op campaign over the network

Two to sixteen players (the remake's: docs/SKIRMISH.md "16 players") on their own machines play one game: a **skirmish** against each other (and computer players), or
a **campaign mission** together (co-op). The model is **deterministic lockstep** (docs/ROADMAP.md "Co-op in the main
campaign", "Online multiplayer (PvP)"): every machine runs the whole simulation; only the players' commands travel,
ordered by a host into **turns**; the machines compare hashes of their worlds and report any difference.

There is no reference netcode: the browser remake has none, the original's GameSpy / LAN servers are gone. The menus
use the original's captions (MPlSelSc, MPHIPScr "Host Direct IP", MPJIPScr "Join Direct IP", PlayerListWindow: GO,
Start Game, Leave Game, Cancel Game, Chat; the default port 1905 of `_UI_GamLWind_Port_TT`) through `GameTexts`.

```
src/Net/                         the network layer - no Godot types (a dedicated server, the tests and the game use it)
  NetConnection.cs               one TCP connection: length-prefixed JSON frames, a reader thread, a thread-safe send
  Lobby.cs                       NetProtocol (version, port, delays), LobbyState / LobbyPeer / CoopChoice, LobbyStart (lobby -> GameStart with seats)
  NetHost.cs                     the host: lobby, start, turns, input delay, pause, lag, scenes, hashes, desync dumps, drop / rejoin, its replay
  NetClient.cs                   a player's side: hello, lobby, chat, turns, votes, hashes, dumps
  LockstepGame.cs                a machine's running game on the turns: pacing, catching up, hash reports, dumps; ContentFingerprint
src/Server/                      the dedicated server (console): a setup file, a lobby, the turns, the replay at the end
src/Sim/Game/LockstepPresenter.cs the mission's interface answers, the same on every machine
src/Sim/Game/GameStart.cs        Seats (who commands which player), SceneRule, Profile; Seats.Apply
src/Sim/Commands/UiStateCommand.cs  the host seat's camera / selection for tutorial conditions
src/Sim/Coop/                    the co-op campaign mode: seats, trigger mapping, scaling, AI commanders, transfers (docs/COOP.md)
src/Sim/Core/FdLibm.cs           deterministic sin / cos / tan / atan / atan2 / asin / acos / exp / log / pow (fdlibm, as V8)
game/src/App/NetSession.cs       this machine's networked game across the screens (host + client), --host / --join
game/src/App/GameSession.Net.cs  the session on the turns: votes, pause, chat, status line, the host seat's view
game/src/UI/MultiplayerMenu.cs   Multiplayer: name, Host (port, game type), Join Direct IP; LobbyChat
game/src/UI/SkirmishMenu.cs      network mode: the PvP lobby
game/src/UI/CoopLobby.cs         the co-op lobby
tests/PwRemake.Tests/MultiplayerTests.cs, DeterminismTests.cs
```

## How to play

**Two PCs.** On the host: title screen → **Multiplayer** → your name, game type *Skirmish (PvP)* or *Campaign
(co-op)*, port (1905) → **Host**. The menu shows the host's IP addresses. On the other PC: Multiplayer → name, the
host's IP and port → **Join**. Over the internet the host forwards the TCP port in its router (a LAN or a VPN such as
ZeroTier / Tailscale needs nothing). Both PCs need the same game version, the same configuration (ParaWorld / MIRAGE)
and the same prepared game data - the lobby shows a problem otherwise and the host cannot start.

**Two instances on one PC** (each needs its own user folder, else they share settings.json):

```
godot --path game -- --config base --host 1905 --mp-name Kacper --players human,human,Aje --skirmish-random --mp-autostart 2
godot --path game -- --config base --join 127.0.0.1:1905 --mp-name Zidell --mp-ready
# co-op: --host 1905 --mp-mode coop --mp-mission 11 --mp-autostart 2   (the second the same --join)
# co-op campaign (mode 2, docs/COOP.md): ... --mp-mode coop --mp-mission 3 --coop-mode campaign --coop-seats human,human,ai --coop-tribes Hu,Aje,Ninigi
```

(On Windows the second instance gets its own user folder with `set APPDATA=%TEMP%\pw2` before starting it; on Linux
`HOME=/tmp/pw2`.)
`--players` lists the skirmish slots (`human` = open for a human, a tribe = a computer player), `--mp-autostart n` starts
once n players are in and ready, `--mp-ready` says ready at once; without them the lobby is used by hand.

**The lobby.** Skirmish: the skirmish menu, the host's: map, game type, points, options (with MIRAGE also **Sixth
stage**, **No titans**, **Flying units** and **Portal termini**, MIRAGE's game options: the host's, the players see
them greyed; `SkirmishSetup.EpochSix` / `RemoveTitans` / `FlyingEnabled` / `Portals` in `GameStart.Skirmish`); a slot is *Open* (for a
human), *Computer* or *Closed*; joining players take the open slots; each player changes its own tribe, team, colour,
army preset and start position; ready = **GO**. Co-op: the host picks the mission (missions not adapted yet are
greyed), the mode, the difficulty, the scene rule and with MIRAGE the game options **Sixth stage** / **No titans** /
**Flying units** / **Portal termini**
(`LobbyState.Campaign.Gameplay` -> `GameStart.Gameplay`: every player of the mission, docs/CAMPAIGN.md). **Shared**: everybody commands the mission's player (the host picks
its tribe). **Co-op campaign** (docs/COOP.md): every player takes a seat and picks its faction; the host makes seats AI
commanders or seat 3 empty, sets the hero split and the co-op difficulty; missions without a co-op definition are
greyed. The chat below. The
host's **Start Game** when everybody is ready.

**In the game.** Everything as single player, plus: Pause pauses everybody ("Paused by Kacper"); the pause menu, the
quest log and the end screen do not stop the game; Enter opens the chat (to all / allies / enemies); a status line
shows "Waiting for …" (a player lagging or reconnecting) and a desync. Quitting leaves the game; the host's leaving
ends it.

## Transport: TCP

`System.Net.Sockets` TCP, Nagle off, frames of 4 bytes length + UTF-8 JSON (commands are already JSON: CommandCodec).
Lockstep needs every command, in order, on every machine - which TCP guarantees; a lost packet costs a
retransmission that the input delay hides mostly (a lockstep game has to wait for a missing turn anyway, with UDP it
would build the same reliability itself). TCP needs no native library, behaves the same on Windows and Linux and runs
in a dedicated server without Godot - ENet would be Godot's (engine-bound) or a native dependency, for no gain here.
Bandwidth: one turn every 50 ms (~40 bytes empty), the commands (~150 bytes each), a hash every 5 s: a few hundred
bytes per second per player, however many units fight.

## Lockstep

* The host (NetHost) has no world. Each player's machine (NetClient + LockstepGame) runs the same world from the same
  `GameStart` - the lobby made into one: the setup or mission, the seed, the debug options and the **seats**
  (`GameStart.Seats`: name and player id per human, seat 0 the host's).
* Players send their commands to the host (`cmd`). Every 50 ms the host issues the next **turn**: its step number and
  the commands that arrived since the last one, in arrival order. The first `InputDelay` turns (4 = 200 ms) are issued
  at once, empty, when everybody has loaded: a command is applied InputDelay steps + the way to the host after it was
  given, at the same step on every machine (`World.Enqueue` before the step: the world applies its commands at the
  start of a step in the queue's order).
* A machine runs a step only when its turn is there; one step per 50 ms of real time, faster when turns pile up
  (catching up), and it waits when they run out. The host stops issuing while a player paused the game, while a
  cutscene is on show (below), while a player is waited for, or while a machine has fallen more than 40 steps (2 s)
  behind - so every machine stands still at the same moment. The game speed is 1 in a networked game.
* The host checks authority: a command must be for the seat's player (a co-op campaign seat's slot; the commands to an AI
  commander and the transfers are the giving human's own), the debug tools need the game's debug options,
  the scenes' ends and the remake campaigns' debug commands are the host's / debug only.
* A command's result (the HUD's "not enough resources" …) reaches the interface through the applied copy
  (`LockstepGame.Delivered`).

### Hash checks and desyncs

Every 100 steps (5 s) each machine sends its hash (`IGameSim.StateHash`: the world's and the mission's) and the hashes
of the world's parts (`World.StateHashParts`: time + random, players, units, unit positions, unit tasks, buildings,
resources, projectiles, timers + effects). A difference is a **desync**: the host tells everybody the step and which
parts differ, names a later step (the next turn) at which every machine writes a **dump** of its world
(`World.StateDump`, `MissionSession.StateDump`: a line per player, entity, record, variable, trigger firing, numbers
round-trippable) into its log folder and sends it to the host; the host writes all dumps to
`logs/desync_<step>/` with `report.txt` - the first lines that differ per machine - and sends the report. The game goes
on (the players see "Desync at 12:30" on the status line and the message); it is a bug to fix, not something to repair
at run time. Test `DesyncIsDetected` shows the report of a deliberately changed resource:
`Host: player 0 Hu ... food=200 ...` / `Player2: player 0 Hu ... food=201 ...`.

### Losing a player

A connection that breaks pauses the game ("Waiting for Zidell (connection lost, 45 s)"). The player can **rejoin**:
Join again with the same name - the host sends the start and every turn so far, the machine builds the world, replays
the turns at full speed (the others wait while it is behind) and plays on. After 60 s, or when the host drops it, the
game goes on without that player (its units stay and do nothing; scene votes no longer wait for it). A player who
quits is dropped at once. The host leaving ends the game (in a dedicated server's game the host is not a player).

## Co-op (campaign missions)

* **Two modes** (`LobbyState.CoopMode`; docs/COOP.md): **Shared** - every human on the mission's human player, the
  same army (`Player.Seats` lists the humans); **Co-op campaign** - 2-3 seats of their own (humans or AI commanders, a
  faction, a base and economy each; `GameStart.Coop`, src/Sim/Coop). The first version's "split" (a human taking an
  allied computer player over, `CampaignConfig.Humans`) is no longer offered in the lobby: an ally's slot is a co-op
  campaign seat now (the definition's `seats[n].ally`), with its faction and no mission brain. The simulation still
  understands a start with a human on an ally (older saves).
* **The triggers, quests, dialogues and cutscenes** run in every machine's world in the same order and are shown to
  everybody (each machine's mission UI). The mission's end is everybody's.
* **Every human has a fog** of its own (each machine keeps all of them: the simulation asks a human's fog for that
  human's units). A shared player has one fog.
* A mission with an army budget starts with the level's own army (no army screen in co-op).

### Interface answers (LockstepPresenter)

Single player, the mission asks the interface a few things (PresenterTap, docs/SAVES.md). In a lockstep game the answers
must be the same on every machine at the same step, so `LockstepPresenter` answers them from shared state:

| question | single player | lockstep |
|---|---|---|
| busy (a cutscene waits) | the mission UI | no (the engine runs one cutscene at a time anyway) |
| plays this cutscene / dialogue | the mission UI | yes - every machine shows it; it ends by votes (below) |
| camera, selected (tutorial UNTT) | the player's | the host seat's, sent as `UiStateCommand` when they change (at most 1/s, only in missions with UNTT conditions) |
| the profile (Tutorial_Started …) | the user's profile.json | the host's, sent with the start (`GameStart.Profile`) |

### Scenes: who ends a cutscene

A cutscene holds a single player game (the mission UI pauses it). In a lockstep game the host holds the turns from the
moment a machine reports the cutscene (`scene-start`) until it ends for everybody; each machine's UI ends it as before
(time up, skipped) - which is now a **vote** (`scene`), not a command. The lobby's **scene rule**:

* **all** (the default): the cutscene ends when every connected player has seen or skipped it - nobody misses the
  story; a player who skipped waits for the others (the game stands still meanwhile).
* **host**: the host's end ends it for everybody (the others' scene closes).

The host then puts one `SceneEndCommand` into the next turn: every machine ends the scene at the same step. Dialogues
(they do not pause a single player game) end the same way, without holding the turns. A machine whose UI cannot show a
scene votes at once. There are no dialogue choices in ParaWorld's missions; if a mission asks a question it is the
mission's own trigger logic on commands.

## Saves and replays

* The **host saves** a networked game (pause menu, F5, autosave); the other players cannot. The save is an ordinary
  `.pwsave` (docs/SAVES.md) whose `GameStart` carries the seats: its commands are everybody's, its interface answers
  the LockstepPresenter's.
* **Loading** such a save (single player, Load Game) replays it exactly - the session uses LockstepPresenter for a start
  with seats, the tap asks it during the replay (`PresenterTap.DeterministicInner`) - and you play on as the host's
  player. A loaded game cannot be continued over the network yet (planned: the host sends the save, every machine
  replays it, the turns go on from its step).
* **The command stream is the replay**: `NetHost.Replay()` is the start plus every turn's commands at their steps (no
  hashes, no answers - the dedicated server's file); loading it replays the whole game (test
  `SaveAndReplayNetworkedGame`). A replay viewer (watching a replay at normal speed) is not there yet: loading a save is
  the fast-forwarded replay.

## Determinism across machines

The simulation was deterministic on one machine (docs/ARCHITECTURE.md "Determinism"). Across machines more is needed;
found and fixed:

* **Elementary functions.** `Math.Sin`, `Cos`, `Tan`, `Atan2`, `Log`, `Pow` … call the C runtime: glibc on Linux, the
  UCRT on Windows (with FMA code paths chosen by CPU) - their last bits differ, and one bit is enough for a lockstep
  game to run apart. `src/Sim/Core/FdLibm.cs` is a port of fdlibm 5.3 (only +, -, *, /, sqrt and bit operations:
  exactly rounded everywhere), and `SMath.Sin` … wrap it; every call in src/Sim uses them. The same library is
  V8's, so the numbers are the browser remake's: bit-equal to node's Math on 3,700 golden arguments
  (`tests/golden/fdlibm.json`), pow within one ulp (V8's pow differs from fdlibm 5.3's in detail), every function
  within one ulp of System.Math over a million arguments. MoonSharp's `math.*` and Lua's `^` operator use it too
  (`MoonSharp.csproj` links the file).
* **Culture.** Numbers as text depend on the machine's culture (a Polish Windows writes `1,5`): Lua's `..` of a number
  (`DynValue.CastToString`), `double.Parse` of the AI's and the campaign's data. Fixed at the places and globally:
  `SimCulture.Use()` (invariant culture as every thread's default) in World's static constructor, Boot and the tests.
  Test: a skirmish played under pl-PL is the same game.
* **The fog of war** was the one human's (`World.Fog`); the simulation asks it (the human's units do not pick targets
  they do not see) - with a hard-coded `Owner.Id == 0`. Now the world keeps a fog per human player
  (`EnableFog` per seat player, `FowOf(player)`), the same on every machine; each machine's view shows its own.
* **Who is "the human"** never depends on the local machine: sessions use the first human (deterministic); the local
  seat is only the view's (`GameSession.Me`).
* Checked and fine: `Dictionary` / `HashSet` enumeration (insertion order, independent of the randomised string hashes);
  no `System.Random`, wall clock or threads in a step (the stopwatches are the step profile's); unstable `List.Sort`
  is deterministic for the same input; static caches are pure functions of their keys (several worlds in one process,
  as in the tests, do not disturb each other).
* Not covered: two machines with different game data (the lobby compares a fingerprint of the data files and the
  mission index and refuses the start), or different builds (the version is compared).

## Dedicated server

`dotnet run --project src/Server -- --setup lobby.json [--port 1905] [--players 2] [--replay out.pwsave]`: a NetHost
without a player of its own, the lobby from a JSON file (a `LobbyState`: mode, skirmish setup or campaign start,
co-op players, scene rule), it starts when `--players` humans are in and ready, relays the turns, compares the hashes
and writes the game's replay when the game ends (Ctrl+C ends it too). It needs no game data (it runs no world).

## Tests

`dotnet run --project tests/PwRemake.Tests -- Multiplayer` (`PW_MP_MINUTES`, default 2): a NetHost and two or three
clients in one process over localhost TCP, each client with its own world; bots order through the network.

* `SkirmishThreeHumans`: three humans and a computer player, two minutes, the same hash on every machine at every check.
* `CoopSharedMission`: mission 1 with the player shared, a slower watcher holding the cutscenes ("all").
* `CoopSharedHostRule`: mission 2 shared with the "host" rule; taking the allied player 3 over is refused in Shared mode.
* `CoopCampaignTwoHumansAndAi`, `CoopCampaignTwoSeats`: the co-op campaign mode over the network (two clients and an AI
  commander; two seats with seat 3 empty) - docs/COOP.md "Tests".
* `DesyncIsDetected`: a changed world found at the next check, the part named, the dumps compared.
* `PauseHoldsEverybody`, `DropRejoinAndLeave` (lost, waited for, rejoined and caught up, another one dropped),
  `LobbyRules` (chat, other data / version as a problem, slots freed, a late joiner refused), `DedicatedServer` (it
  starts by itself, its replay is the game).
* `SaveAndReplayNetworkedGame`: the host's save and the host's turn record replay to the same world.
* `MirageGameOptionsPathsTests`: MIRAGE's sixth stage / no titans through the skirmish lobby and the co-op lobby (as the
  players receive them) to the GameStart and every player's tech tree. Network protocol 4 since (a GameStart's
  `gameplay`); the debug option "First-shot delay" travels in the GameStart's `debug` as every debug option.
* `DeterminismTests`: FdLibm against V8 and System.Math, Lua's math and number strings under pl-PL, a skirmish under
  pl-PL.

In Godot: two instances under xvfb (docs/TESTING.md "Two instances"): a skirmish (2 humans + Aje on a random jungle)
and a co-op mission 11 ran with equal hashes at every check on both, a cutscene ended by both machines' votes.
