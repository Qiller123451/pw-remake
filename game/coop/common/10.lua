-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 10 "The Prophet".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  extend = {
    seed = 110,
    style = "island",
    west = 256,
    land = {
      { x = -50, z = 555, r = 95 },
    },
    bridges = {
      {
        points = {
          { x = -40, z = 815 },
          { x = -40, z = 760 },
          { x = -45, z = 700 },
          { x = -50, z = 555 },
        },
        width = 18,
        cut = 12,
        shape = "path",
      },
    },
  },
  seats = {
    [2] = {
      start = { x = 690, z = 1285, dir = 0 },
      army = "default",
      extend = {
        south = 288,
        bridges = {
          {
            points = {
              { x = 690, z = 1285 },
              { x = 695, z = 1185 },
              { x = 688, z = 1115 },
            },
            width = 20,
            cut = 26,
            shape = "isthmus",
          },
        },
      },
      resources = {
        { kind = "kit", x = 690, z = 1285 },
      },
    },
    [3] = {
      start = { x = -40, z = 815, dir = 0 },
      army = "default",
      extend = {
        west = 288,
        bridges = {
          {
            points = {
              { x = -40, z = 815 },
              { x = 130, z = 795 },
              { x = 199, z = 779 },
            },
            width = 20,
            cut = 24,
            shape = "isthmus",
          },
        },
      },
      resources = {
        { kind = "kit", x = -40, z = 815 },
      },
    },
  },
  enemies = {
    {
      name = "SEAS east outpost guns",
      seats = 2,
      owner = 2,
      at = { x = 735, z = 905, r = 30 },
      buildings = {
        { class = "seas_hq_defense_turret" },
        { class = "seas_hq_defense_turret" },
        { class = "seas_hq_machinegun_nest" },
        { class = "seas_hq_machinegun_nest" },
      },
      notes = "The SEAS east outpost (player 2) grows on its south side, where seat 2's pass comes up: two turrets and two machine-gun nests. The side quest Danger Limitation (players 1 and 2 defeated) counts them: they stand at the outpost, reachable from the jungle.",
    },
    {
      name = "SEAS west outpost guns",
      seats = 3,
      owner = 1,
      at = { x = 275, z = 735, r = 30 },
      buildings = {
        { class = "seas_hq_defense_turret" },
        { class = "seas_hq_defense_turret" },
        { class = "seas_hq_machinegun_nest" },
      },
      notes = "The SEAS west outpost (player 1) grows on its west side, facing seat 3's pass: two turrets and a machine-gun nest. Counted by Danger Limitation as the east outpost's.",
    },
    {
      name = "SEAS landing camp",
      seats = 3,
      owner = "new",
      tribe = "SEAS",
      like = 1,
      at = { x = -50, z = 555, r = 40 },
      preset = "camp",
      brain = { behaviour = "Dodo", attack = true, first_attack = 660, interval = 330, target = 3 },
      notes = "A new SEAS player on an islet north of seat 3's island (260 m), joined to it by a path: its waves cross seat 3's island into the jungle. It touches neither the cliff ring's beach nor any gate. No objective counts it.",
    },
  },
  allies = {
    usable = {  },
  },
  scaling = {
    count = 0.6,
    health = 0.25,
    damage = 0.15,
    exclude = {  },
  },
  triggers = { give_to = 1 },
  notes = "Base mission. Islands kept (style island): the island's coast is a ring of cliffs with a narrow beach below them that runs round to the SEAS bases behind their walls and gates - land grown against that coast would open a way round the gates. Each seat's base is an island of the map's own land close to the coast, joined to the jungle by a land bridge and a pass cut through the cliffs (seat 2 south, where the cliffs come down to the sea without a beach; seat 3 west; never the beach that runs round to the SEAS); each faces one SEAS outpost as seat 1 faces both (279 / 429 / 333 m). Enemy additions: both outposts grow towards the seats' passes, and a SEAS landing camp sits on an islet behind seat 3. Revised 2026-11-11 (docs/COOP.md \"Fair bases\": every seat at least as far from an enemy building as seat 1's base is in single player, and near seat 1): seat 2's base 15 m nearer seat 1, the landing camp's islet moved north to (-50, 555) (the path now runs from seat 3's island to it); seat 3 stays beyond 0.4 of the map's width (NearExceptions: no nearer sea inside the cliff ring). Nearest enemy building: 215 / 375 / 222 m.",
}
