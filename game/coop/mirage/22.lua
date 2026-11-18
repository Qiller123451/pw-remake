-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission SEAS 4-3 "Operation Neptune".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
return {
  version = 1,
  nobuild = false,
  heroes = { "schliemann_s0" },
  extend = { seed = 143 },
  seats = {
    [2] = {
      start = { x = -170, z = 560, dir = 0 },
      army = { "seas_worker", "seas_worker", "seas_worker", "seas_worker", "seas_warrior", "seas_warrior" },
      extend = {
        west = 320,
        grow = {
          { x = 200, z = 360, r = 140 },
          { x = 40, z = 430, r = 160 },
          { x = 60, z = 690, r = 150 },
          { x = -80, z = 780, r = 110 },
        },
        keep = {
          { x = 155, z = 818, r = 18 },
        },
        bridges = {
          {
            points = {
              { x = -170, z = 560 },
              { x = 60, z = 420 },
              { x = 222, z = 358 },
            },
            width = 16,
            cut = 8,
            shape = "path",
          },
        },
      },
      resources = {
        { kind = "kit", x = -170, z = 560 },
      },
    },
    [3] = {
      start = { x = 330, z = 1140, dir = 0 },
      army = { "seas_worker", "seas_worker", "seas_worker", "seas_worker", "seas_warrior", "seas_warrior" },
      extend = {
        south = 224,
        grow = {
          { x = 290, z = 900, r = 140 },
          { x = 300, z = 1030, r = 130 },
          { x = 560, z = 1100, r = 120 },
        },
        keep = {
          { x = 660, z = 840, r = 110 },
        },
        bridges = {
          {
            points = {
              { x = 560, z = 1110 },
              { x = 440, z = 1130 },
              { x = 330, z = 1140 },
            },
            width = 16,
            cut = 8,
            shape = "path",
          },
        },
      },
      resources = {
        { kind = "kit", x = 330, z = 1140 },
      },
    },
  },
  enemies = {
    {
      name = "Norse lookout",
      seats = 2,
      owner = "new",
      tribe = "Hu",
      like = 5,
      at = { x = -100, z = 780, r = 36 },
      preset = "outpost",
      brain = false,
      notes = "A new Norse player (standing as the Norsemen's main base, player 5) south of seat 2. The Dustriders' camp check (L01b: player 2 in the player base) and the behaviour switches (players 2, 6 defeated) ignore it.",
    },
    {
      name = "Dragon Clan war camp",
      seats = 3,
      owner = "new",
      tribe = "Ninigi",
      like = 3,
      at = { x = 560, z = 1110, r = 44 },
      preset = "camp",
      brain = { behaviour = "Dodo", attack = true, first_attack = 660, interval = 330, target = 3 },
      notes = "A new Dragon Clan player east of seat 3; its waves go for seat 3. No objective counts it.",
    },
  },
  allies = { usable = {  } },
  scaling = { count = 0.6, health = 0.25, damage = 0.15, exclude = {  } },
  triggers = { give_to = 1 },
  notes = "The SEAS version of 4-3: the same map, places and enemies as 4-3 (the land grows west of the landing strip and south behind the bay; the Dustriders' camp check counts player 2 only).",
  -- not reworked with the main campaign's redesign (2026-11-03): co-op campaign (mode 2) greyed out, Shared mode only
  campaign = false,
  reason = "Co-op campaign: not reworked yet (Shared mode only)",
}
