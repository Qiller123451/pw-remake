-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission SEAS 1 "Seeking Ada".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
return {
  version = 1,
  nobuild = false,
  heroes = { "livingstone_s0" },
  extend = { seed = 123 },
  seats = {
    [2] = {
      start = { x = 640, z = 1090, dir = 0 },
      army = { "seas_worker", "seas_worker", "seas_worker", "seas_worker", "seas_warrior", "seas_warrior" },
      extend = {
        south = 224,
        grow = {
          { x = 480, z = 860, r = 130 },
          { x = 560, z = 980, r = 140 },
          { x = 720, z = 980, r = 130 },
          { x = 860, z = 1110, r = 110 },
        },
        keep = {
          { x = 795, z = 825, r = 40 },
          { x = 630, z = 870, r = 30 },
        },
      },
      resources = {
        { kind = "kit", x = 640, z = 1090 },
      },
    },
    [3] = {
      start = { x = -90, z = 720, dir = 0 },
      army = { "seas_worker", "seas_worker", "seas_worker", "seas_worker", "seas_warrior", "seas_warrior" },
      extend = {
        west = 256,
        grow = {
          { x = 210, z = 660, r = 140 },
          { x = 60, z = 700, r = 150 },
          { x = 40, z = 860, r = 120 },
        },
        keep = {
          { x = 180, z = 590, r = 25 },
        },
      },
      resources = {
        { kind = "kit", x = -90, z = 720 },
      },
    },
  },
  enemies = {
    {
      name = "Barbarian lookout",
      seats = 2,
      owner = "new",
      tribe = "Hu",
      like = 4,
      at = { x = 870, z = 1120, r = 36 },
      preset = "outpost",
      brain = false,
      notes = "A new barbarian player (standing as the barbarian support, player 4) east of seat 2. The outpost's quest (player 1 defeated) and the harbours' (player 4's harbours) ignore it.",
    },
    {
      name = "Barbarian raiders",
      seats = 3,
      owner = "new",
      tribe = "Hu",
      like = 2,
      at = { x = 40, z = 900, r = 44 },
      preset = "camp",
      brain = { behaviour = "Dodo", attack = true, first_attack = 720, interval = 330, target = 3 },
      notes = "A new barbarian player south of seat 3; its waves go for seat 3. No objective counts it.",
    },
  },
  allies = { usable = {  } },
  scaling = { count = 0.6, health = 0.25, damage = 0.15, exclude = {  } },
  triggers = { give_to = 1 },
  notes = "Base mission (the base and the docks are optional for Leighton's squad). The start's island chain grows south and west: seat 2's land continues the south coast of the start island, seat 3's the west island of the chain (both on the start's walkable ground); the landing places of the SEAS support (SA1, SA2, the hovercraft's) stay as they are (keep), the barbarians' islands in the north are not touched. The infiltration team (player 3) is steered by the triggers: no seat. The seats get workers and warriors. Enemies: new barbarian players (a lookout always, raiders with three seats) - the outpost's and the harbours' quests count players 1 and 4 only.",
  -- not reworked with the main campaign's redesign (2026-11-03): co-op campaign (mode 2) greyed out, Shared mode only
  campaign = false,
  reason = "Co-op campaign: not reworked yet (Shared mode only)",
}
