-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 14 "Prisoners".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  extend = {
    seed = 114,
    east = 96,
    grow = {
      { x = 560, z = 1080, r = 55 },
    },
  },
  seats = {
    [2] = {
      start = { x = 700, z = 1310, dir = 0 },
      army = "default",
      extend = {
        south = 192,
        grow = {
          { x = 700, z = 1010, r = 150 },
          { x = 700, z = 1160, r = 150 },
          { x = 500, z = 1200, r = 110 },
        },
        keep = {
          { x = 330, z = 880, r = 140 },
        },
      },
      resources = {
        { kind = "kit", x = 700, z = 1310 },
      },
    },
    [3] = {
      start = { x = 1075, z = 840, dir = 0, land = 110 },
      army = "default",
      extend = {
        grow = {
          { x = 985, z = 790, r = 55 },
        },
        keep = {
          { x = 910, z = 650, r = 50 },
        },
        bridges = {
          {
            points = {
              { x = 1000, z = 800 },
              { x = 937, z = 785 },
            },
            width = 16,
            cut = 10,
            shape = "path",
          },
        },
      },
      resources = {
        { kind = "kit", x = 1075, z = 840 },
      },
    },
  },
  enemies = {
    {
      name = "SEAS patrol post",
      seats = 2,
      owner = "new",
      tribe = "SEAS",
      like = 1,
      at = { x = 560, z = 1080, r = 36 },
      preset = "outpost",
      brain = false,
      notes = "A new SEAS player (standing as the SEAS base, player 1) on the south coast between seat 1 and seat 2. Leighton's flight (02_ns_6105: no building of player 1 left) and the prison checks (players 2, 3) do not count it.",
    },
  },
  allies = {
    usable = {  },
  },
  scaling = {
    count = 0.6,
    health = 0.25,
    damage = 0.15,
    exclude = { 2, 3 },
  },
  triggers = { give_to = 1 },
  notes = "Base mission. Seat 2's land grows south of seat 1's beach (the place the co-op mission had; the west coast the plan proposed leaves no dry build area between the island prison's keep zone and the old bay; the island prison stays an island: keep), seat 3's from the east coast with a short pass up (the islet east stays). All walk to the SEAS posts about as far as seat 1 (728 / 756 / 602 m). The prisons (players 2, 3) are scripted: not scaled. Revised 2026-11-11 (docs/COOP.md \"Fair bases\": every seat at least as far from an enemy building as seat 1's base is in single player, and near seat 1): the SEAS labour camp south of seat 3 was dropped (its buildings stood 136 m from seat 3; moving it cut it off from the seats' land). Nearest enemy building: 175 / 238 / 304 m.",
}
