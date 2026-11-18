# Co-op campaign: design (agreed with the user, 2026-11-03)

Two co-op modes for the campaign missions (main campaign 0-16, BoosterPack 1 41-43, MIRAGE's SEAS missions 20-23; both
configurations), in the spirit of StarCraft II's co-op missions. Both run over the lockstep network layer
(docs/MULTIPLAYER.md) and are deterministic; saves and replays work as for any game.

## Mode 1: Shared

Every human commands the same units: the mission's human player, shared. Nobody takes over a computer ally. The map,
the enemies and the rules are the single player mission's. (This replaces the "split" choice of the first co-op
version: taking over an ally belongs to mode 2 only.)

## Mode 2: Co-op campaign (2 or 3 players)

### Seats
* 2 or 3 **seats**. Seat 1 is the mission's own human player (its start, its army); seats 2 and 3 are co-op players.
* Each seat is a **human** or an **AI commander** (an AI ally in place of a human). At least one human. Works offline too
  (one human + 1-2 AI commanders) and online (2-3 humans, or 2 humans + 1 AI commander).
* Each seat picks its **faction** freely (Norsemen, Dustriders, Dragon Clan; MIRAGE also SEAS), as in single player's
  tribe choice.
* **Own base and economy** per seat: own buildings, resources, population limit, tech, epochs.

### Map space
* Where an existing computer ally fits the role (an allied player with a base or army in a sensible place), its slot
  can become a co-op seat (it gets the seat's faction, it is no longer driven by its brain; triggers that move it must
  still work - the mission's definition says whether an ally may be used).
* Where the map has a fitting free spot, a new seat starts there.
* Otherwise - and that is most missions - the **map is extended**: new terrain at one or more edges, matching the
  map's setting (heights blended to the existing edge, ground textures, plants, water where it continues), with
  **resources adjusted** (wood, stone, food, iron/ore as the setting has them) so each seat can build a base.
* The extension is **seamless** (agreed 2026-11-03, after the first version's islands on land bridges): the
  mission's landmass itself grows - the island continues outward along a broad front, made of the map's own land
  (patches of its heights, ground, plants, rocks and forests) - so a co-op seat's base is new territory of the same
  land, not a lone island on a strip. The old map's gameplay stays: nothing changes near its buildings, units,
  mission objects, gates and walls, and the new land opens no way round a gate, a wall or a scripted choke. Islands
  remain a per-mission choice where they make sense (an archipelago, sea lanes that must stay open).
* **Starting bases apart**: seats do not all start next to each other; a co-op player may start on the far side of
  the action and fight towards the others ("dramatic").
* Everything a co-op definition adds lives in the repository (`game/coop/...`), applied on top of the original mission
  at load. Single player stays exactly the original.

### Enemies
* Enemies get stronger with the player count, **in numbers and in strength**: more units in waves and garrisons, and
  more health / damage, per extra seat (AI commanders count like humans).
* A definition may also **grow existing enemy bases** (more towers, production, the tribe's own buildings) and **add
  new enemy bases** - of an enemy player of the mission or of a new computer player with the remake's AI, which builds
  up and sends waves at the co-op seats - each from a number of seats on (a 2-seat game gets less). The objectives
  stay winnable: what an objective counts either can be reached and destroyed, or belongs to a new player no
  objective counts.
* On top of the mission difficulty (Easy - Hard / Nightmare) the host picks a **co-op difficulty** (e.g. Normal /
  Hard / Brutal) that sets the scaling factors.
* The scaling is per mission tunable in its co-op definition (some missions need less, e.g. scripted fights).

### Heroes
* **Normal missions**: each human controls one of the story heroes (Stina, Cole, Béla - or the mission's own set).
  With 2 humans, the host assigns the third (one player gets two); with 3 humans, one each. The lobby shows the
  assignment and the host can change it.
* **No-build missions** (a hero party, no base): all heroes are divided evenly among the humans (host can adjust).
* Other heroes that are not part of the story set (tavern / buildable ones) are up for grabs: whoever builds them
  first has them (one per team where the original allows one).
* **Heroes are controlled only by humans**: an AI commander never gets a hero. A hero's death loses the mission as in
  the original (death rules unchanged).
* Heroes can be handed between humans (transfer, below).

### AI commanders
* An AI commander builds its own base and army (the remake's AI, set for a co-op ally) and takes **general commands**
  from the humans, SC2-style: attack a point / a target, defend a point, follow / escort a hero or unit group,
  gather and expand (economy focus), retreat to base; plus an aggression setting (defensive / normal / aggressive).
  Commands are given through a co-op command panel and minimap / world clicks, and are lockstep commands.
* Without commands it defends its base, expands, and joins the humans' attacks.

### Transfers
* Units can be given to another co-op seat (a human or an AI commander): select, then "give to" a seat. Heroes only
  between humans. Buildings stay with their owner.
* Resources can be sent between seats (the diplomacy window's tribute, for co-op seats).

### Mission flow
* Objectives, triggers, quests, dialogues and cutscenes are shared; the mission is won or lost for everybody.
* Triggers that refer to "the player" (player 0 / the human) keep working: the co-op layer maps conditions like "the
  player has N of X" / "a unit of the player is in region R" to all seats, actions that give units or resources to the
  player to seat 1 (or the seat the definition names).

### Agreed 2026-11-03 (second round, after mockups of every mission)
* **Fair bases**: from each seat's point of view its base is about as close to the action as seat 1's - a similar walk to
  the nearest enemy base / objective, not at the opposite end of the map, not right next to an enemy; each seat its own
  build space and resources. Measured by a test (docs/COOP.md "Fair bases"); exceptions named there.
* **Hero phase**: in a mission that starts with heroes only and gives the player a base later (main campaign: 0, 1, 7, 9)
  seats 2 and 3 get their bases at the moment seat 1 gets its base; before that they have no base, no workers, no army.
* **Every step, every player has a usable unit**: what the story hands out in a hero phase or a no-build mission (rescued,
  freed, joining units) is dealt round the seats - one unit to each seat in turn, the seat with the fewest units first -
  never copied. **No extra units and no extra enemies** in those segments (the enemies' numbers are not scaled; health
  and damage are). Mission 1: seats 2 and 3 each start with a Norse warrior beside Cole, the beach nest's two
  Dilophosaurs come together.
* Mission 1: both co-op bases in the north (seat 2 north-east bay, seat 3 the north lagoon above the prison camp), a
  raiders' camp behind seat 3. Mission 2: seats 2 and 3 share the south-east with seat 1, the hunting camp behind them.
  Mission 7: a hero phase, then all three build at Taslow's home. Mission 8 (2026-11-11): seats 2 and 3 get their
  bases only when the barbarian camp north of the start is cleared; until then they have a share of seat 1's start
  spearmen, and AI commanders attack the camp. Mission 10: enemy additions as in the other base
  missions. Mission 13: all bases at the city wall; with three seats a second gate - either gate falling loses the
  mission - and seat 1's placed defence split between the seats.
* Scope: the main campaign (0-16) in both configurations. BoosterPack 1 (41-43) and MIRAGE's SEAS missions (20-23) are
  not reworked yet: co-op campaign mode is greyed out for them (Shared mode stays).

## Files (planned)
* `src/Sim/Coop/`: the co-op definition (schema, loader), seats, hero assignment, scaling, transfers, the AI
  commander's command layer.
* `src/Sim/Maps/MapExtension.cs` (+ view support): extending a map's terrain at its edges with generated terrain.
* `game/coop/<config>/<mission id>.lua` (or a shared one per mission with per-configuration parts): each mission's
  co-op definition - extension, seats' starts and armies, usable allies, new allies, resources, hero split, scaling.
* `game/src/UI/CoopLobby.cs` (extended), a co-op command panel in the HUD, the transfer buttons.
* Docs: docs/COOP.md (how it works, how to write a mission's co-op definition).

## The co-op definition (schema v1, shared by the core and the map work)

One Lua file per mission and configuration: `game/coop/<config>/<NN>.lua` (NN = the mission id of index.json:
`00`..`16`, `20`..`23`, `41`..`43`; a `game/coop/common/<NN>.lua` may hold what both configurations share, the
configuration's file overrides it). It returns a table; coordinates are the original map's map coordinates
(metres, x east, z = the map's y south, from its north-west corner: the mission's own world.json / trigger
coordinates; after an extension the old map keeps its coordinates and the new land lies at negative or
beyond-the-old-size coordinates; the map hook turns the seats' starts into game coordinates - docs/COOP.md "Maps").
What exists only for one seat (its land, its base site, its resources) goes under that seat (`seats[n].extend`,
`seats[n].resources`) and is left out when the seat is empty (a 2-seat game).

```lua
return {
  version = 1,
  nobuild = false,                       -- a hero-party mission without bases: heroes divided evenly
  heroes = { "Cole_s0", "Bela_s0", "Stina_s0" },   -- the story heroes split among the humans (classes of the mission)
  -- terrain added at the edges (metres, multiples of the map's grid); nil = none
  extend = {
    north = 0, south = 192, east = 0, west = 0,
    seed = 7,                            -- for the generated terrain / plants
    style = "grow",                      -- "grow" (default): the landmass grows - the base sites and `grow` lobes join
                                         -- the old land; "island": water between (land bridges); "continue": the edge's
                                         -- land goes on, sites are islands
    water = nil,                         -- optional water level override for the new land
    grow = { { x = 450, z = 1220, r = 170 } },   -- lobes of new land (each a patch of the map), joined to the old coast
    keep = { { x = 745, z = 725, r = 60 } },     -- the old map stays as it is there, and no new land comes in
  },
  -- extra resources (in the old map or the new land): kind = class of the resource object or "wood"|"stone"|"food"
  resources = {
    { class = "stone_big", x = 100, z = -150 },
    { kind = "wood", area = { x = 80, z = -180, r = 40 }, count = 30 },   -- a generated forest patch
  },
  -- the co-op seats beyond seat 1 (the mission's human player): where they start and with what
  seats = {
    [2] = { start = { x = 120, z = -160, dir = 0 }, army = "default",     -- "default": the tribe's start army of the
            -- mission (scaled like seat 1's), or a list: { "worker", "worker", "<class>" ... } in the seat's tribe
            ally = nil },                -- or the id of an existing allied player of the map whose slot it takes
    [3] = { start = { x = 900, z = 40, dir = 3.14 }, army = "default", ally = 3,
            -- only when seat 3 is used: its own land and resources (merged with the top-level ones)
            -- (a seat's base site is made of a patch of the map itself: extend.stamp, chosen automatically if not given;
            -- style "grow": the site is a lobe of start.land m (190) joined to the old land with its `grow` lobes;
            -- bridges are isthmuses, shape = "ford", or "path": a graded way through grown land - docs/COOP.md "Maps")
            extend = { east = 256, bridges = { { points = { { x = 1150, z = 40 }, { x = 900, z = 40 } }, width = 20 } } },
            resources = { { kind = "kit", x = 1150, z = 40 } } },
  },
  -- existing allies of the map: may they become seats (ally = id above)?; new allies are seats too
  allies = { usable = { 3 } },
  -- enemy scaling per extra seat (multiplied by the co-op difficulty's factor); nil = the defaults
  scaling = { count = 0.6, health = 0.25, damage = 0.15, exclude = { 7 } },   -- exclude: player ids left as they are
  -- triggers: which seat receives what the original gives "the player" (default 1), regions that count all seats
  triggers = { give_to = 1 },
  -- the hero phase (automatic when seat 1 starts with neither worker nor building; docs/COOP.md "Hero phase and gifts")
  phase = { hero = true, base_when = "trigger:bela_worker_ready", gifts = "deal" },
  -- (phase.attack = "rescamp": the region AI commanders attack in the hero phase - mission 8's camp; none: they escort
  --  the humans' heroes)
  -- (seats[n].hero_army = { "hu_warrior" }: what a seat gets beside seat 1's start in a hero phase)
  -- the mission's own objects in the co-op game: map attributes, a second gate, wave paths, seat 1's defence split
  map_objects = { { name = "Nest_Small_Carnivores_14", attr = { spawn_max = 2 } } },
  gates = { { seats = 3, class = "hcl13_gate", replaces = { "hcl13_city_wall_79", "hcl13_city_wall_74", "hcl13_city_wall_85" } } },
  reroute = { { seats = 3, near = { x = 821, z = 335, r = 45 }, to = { { x = 729, z = 322 } }, every = 2 } },
  start_split = { { ["for"] = 3, at = { x = 952, z = 430, r = 45 }, to = 2 } },  deal_start = true,
  campaign = true,                       -- false: mode 2 greyed out (`reason`)
  -- enemy bases grown or added (docs/COOP.md "Enemy bases"); coordinates as everywhere: the original map's
  enemies = {
    { name = "Poachers' towers", seats = 2, owner = 1, at = { x = 700, z = 330, r = 40 },     -- a mission's player
      buildings = { { class = "aje_small_tower" }, { class = "aje_medium_tower", x = 690, z = 310 } },
      notes = "player 1's objective counts them: they stand in its base" },
    { name = "Hunting camp", seats = 3, owner = "new", tribe = "Aje", like = 2,              -- a new computer player
      at = { x = 940, z = 750, r = 44 }, preset = "camp",                -- "outpost" | "camp" | "town", or buildings
      army = { "aje_warrior", { class = "aje_archer", count = 2 } },
      brain = { behaviour = "Dodo", attack = true, first_attack = 660, interval = 330, wave = "SuicideAttack", target = 3 },
      notes = "no objective counts a new player" },
  },
  notes = "why the bases are where they are",
}
```

The core (src/Sim/Coop) loads it (`CoopDefinition.Load`), applies seats, armies, heroes, scaling and transfers; the
map work (src/Sim/Maps/MapExtension.cs) applies `extend` and `resources` to the map before the world is built. A
mission without a file can be played in Shared mode only (the co-op lobby greys it out for mode 2).
