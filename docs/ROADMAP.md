# Roadmap

## Now: the original campaign

The single player campaign of the original game (17 missions + BoosterPack 1's three), playable with any tribe of the
chosen configuration (ParaWorld: Norsemen, Dustriders, Dragon Clan; MIRAGE: also the SEAS), with the browser remake's
features and debug options. Missions become playable one by one; the campaign menu greys out the others
(`game/src/UI/CampaignStatus.cs`). Progress of the port: docs/PORTING.md.

## Next

1. ~~**The remake's own campaigns**~~ done (docs/SEQUEL.md): *Containment (1964)* and its 1966 missions, and the
   standalone scenarios of the browser remake - as Lua mission folders (game/campaigns) with their own runtime and
   mission API (src/Sim/Sequel), menus, interface and effects; the sequel's art from the toolkit (tools/prepare).
2. **Skirmish** against the computer on the original maps and generated ones.
3. ~~Saved games~~ done (docs/SAVES.md: a save is the start and the inputs, replayed and verified on load; a state
   snapshot can come later as a cache of long games).

## Co-op in the main campaign

**Done** (docs/MULTIPLAYER.md, docs/COOP.md): co-op over the network - every scene shown to everybody and ended by the
lobby's scene rule, the host's saves, hash checks - in two modes: **Shared** (everybody on the mission's player) and the
**Co-op campaign** (2-3 seats with their own faction, base and economy, humans or AI commanders with general commands,
the story heroes split among the humans, enemies scaled by seats and co-op difficulty, transfers of units and
resources; online and offline). In work: the map extension and every mission's co-op definition (branch port/coopmaps;
the core's test definitions: missions 2, 3, 11). Planned: continuing a saved networked game over the network.

Two (or more) players play a mission together. Prepared in the design:

* **Commands, not calls.** Everything a player does reaches the world as a command (docs/ARCHITECTURE.md
  "Commands"); the world runs fixed 50 ms steps and is deterministic. Co-op is two command streams on the same
  steps: each machine runs the whole simulation (lockstep) and only commands travel - a few bytes per order however
  many units fight.
* **Who controls what**: the mission's player 0 can be shared by two humans (both order the same army) or split - a
  second human slot of a mission gets its own start (the campaign set-up places a second start army; missions with
  allied computer players can hand one over). `Player.ControlledBy` lists the humans of a player.
* **Triggers** run on every machine in the same order (they are part of the simulation); quests, dialogues and
  cutscenes are shown to everyone. Lua runs on MoonSharp, managed code - the same results everywhere.
* **Checks**: every few seconds the machines compare a hash of the world; a difference is a bug (logged with the step
  and a dump), not a silent drift.

## Online multiplayer (PvP)

**Done** (docs/MULTIPLAYER.md): lockstep over TCP with input delay, a host that orders the commands, a lobby (join by IP
and port, ready, the setup sent to everybody), chat, pause, waiting for and rejoining a lost player, hash checks with
desync dumps, determinism across machines (fdlibm for the elementary functions, the invariant culture, a fog per human),
a dedicated server (src/Server) and its replays. Not yet: custom map download, a server list, a replay viewer.

* **Lockstep over the network** with an input delay (commands are scheduled a few steps ahead), a host that relays
  and orders the commands, join through a lobby; the same deterministic simulation as co-op.
* **Custom maps**: a map is a folder (zip) with its Lua scripts and data (docs/LUA_MAPS.md); it is downloaded with the
  game. Lua runs sandboxed (no files, no network, an instruction limit), so a downloaded map cannot harm the computer.
  Engine-level mods (new C# code) are never downloaded automatically - they are installed on purpose
  (docs/ARCHITECTURE.md, the three tiers of maps / plugins / the game).
* **Replays** are the command streams of games.
* A **dedicated server** is the simulation without the view (src/Sim is a plain .NET library).

Why lockstep and not the snapshot model of the first Godot draft (ParaWorldGodot docs/Multiplayer.txt, written for a
VR first-person game): an RTS has hundreds of units; sending their state is expensive, sending orders is not. The
price is determinism, which the simulation keeps from the start (docs/CODE_STYLE in PORTING.md).

## Later

* A map editor in Godot: terrain, objects, the trigger menu that writes the declarative part of `triggers.lua`
  (hand-written code shows as a "custom script" block).
* Lua scripts for the computer player's strategy (build orders, attack plans, difficulty) on top of the C# AI.
* A C# importer of the original formats inside the game, replacing the Python prepare tool.
* Own assets (docs/CONTENT.md "Own assets later").
