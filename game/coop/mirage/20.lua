-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission SEAS 4-1 "The Devil's Advocate".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
return {
  version = 1,
  nobuild = false,
  heroes = { "livingstone_s0" },
  extend = { seed = 141 },
  seats = {
    [2] = {
      start = { x = 180, z = 1200, dir = 0 },
      army = "default",
      extend = {
        south = 256,
        grow = {
          { x = 190, z = 1000, r = 150 },
          { x = 330, z = 1060, r = 150 },
          { x = 410, z = 1170, r = 110 },
        },
        bridges = {
          {
            points = {
              { x = 180, z = 1200 },
              { x = 190, z = 1050 },
              { x = 192, z = 926 },
            },
            width = 16,
            cut = 10,
            shape = "path",
          },
        },
      },
      resources = {
        { kind = "kit", x = 180, z = 1200 },
      },
    },
    [3] = {
      start = { x = 1500, z = 300, dir = 0 },
      army = "default",
      extend = {
        east = 256,
        grow = {
          { x = 1340, z = 230, r = 160 },
          { x = 1360, z = 380, r = 140 },
          { x = 1480, z = 520, r = 120 },
        },
        bridges = {
          {
            points = {
              { x = 1500, z = 300 },
              { x = 1320, z = 270 },
              { x = 1234, z = 280 },
            },
            width = 16,
            cut = 10,
            shape = "path",
          },
        },
      },
      resources = {
        { kind = "kit", x = 1500, z = 300 },
      },
    },
  },
  enemies = {
    {
      name = "Norse lookout",
      seats = 2,
      owner = "new",
      tribe = "Hu",
      like = 2,
      at = { x = 420, z = 1180, r = 36 },
      preset = "outpost",
      brain = false,
      notes = "A new Norse player standing as the Norsemen's main base (player 2): neutral while it is, hostile when it turns. The defender objects' checks (Gd15, Gd55) and the AI deaths (Aa99-Ad99) ignore it.",
    },
    {
      name = "Dustrider raiders",
      seats = 3,
      owner = "new",
      tribe = "Aje",
      like = 3,
      at = { x = 1520, z = 520, r = 44 },
      preset = "camp",
      brain = { behaviour = "Dodo", attack = true, first_attack = 720, interval = 360, target = 3 },
      notes = "A new Dustrider player standing as the Dustriders' main base (player 3): its waves go for seat 3 once that base is hostile. No objective counts it.",
    },
  },
  allies = { usable = {  } },
  scaling = { count = 0.6, health = 0.25, damage = 0.15, exclude = {  } },
  triggers = { give_to = 1 },
  notes = "The SEAS version of 4-1: the same map, places and enemies as 4-1 (the land grows south of the landing and east at the rendezvous point; the new Norse and Dustrider players follow those main bases' diplomacy).",
  -- not reworked with the main campaign's redesign (2026-11-03): co-op campaign (mode 2) greyed out, Shared mode only
  campaign = false,
  reason = "Co-op campaign: not reworked yet (Shared mode only)",
}
