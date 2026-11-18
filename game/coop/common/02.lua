-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 2 "Druid Island".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  extend = {
    seed = 102,
    east = 160,
    grow = {
      { x = 1000, z = 1060, r = 75 },
      { x = 905, z = 1080, r = 65 },
      { x = 850, z = 1115, r = 60 },
    },
    keep = {
      { x = 745, z = 725, r = 60 },
    },
  },
  seats = {
    [2] = {
      start = { x = 775, z = 1160, dir = 0, land = 110 },
      army = "default",
      extend = {
        south = 96,
        grow = {
          { x = 705, z = 1120, r = 50 },
        },
        bridges = {
          {
            points = {
              { x = 735, z = 1130 },
              { x = 681, z = 1097 },
            },
            width = 16,
            cut = 10,
            shape = "path",
          },
        },
      },
      resources = {
        { kind = "kit", x = 775, z = 1160 },
      },
    },
    [3] = {
      start = { x = 297, z = 968, dir = 0, land = 130 },
      army = "default",
      extend = {
        bridges = {
          {
            points = {
              { x = 297, z = 968 },
              { x = 365, z = 991 },
              { x = 395, z = 996 },
            },
            width = 16,
            cut = 10,
            shape = "path",
          },
        },
      },
      resources = {
        { kind = "kit", x = 297, z = 968 },
      },
    },
  },
  enemies = {
    {
      name = "Poachers' towers",
      seats = 2,
      owner = 1,
      at = { x = 700, z = 330, r = 40 },
      buildings = {
        { class = "aje_small_tower" },
        { class = "aje_medium_tower", x = 690, z = 310, rot = 0 },
      },
      army = {
        { class = "aje_warrior", count = 3 },
      },
      notes = "The poachers' base (player 1) grows: L02MQ03 (player 1 defeated) counts them - they stand in the base the quest sends the player to destroy.",
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
  notes = "Base mission. Seats 2 and 3 share the south-east with seat 1 (the user's choice): seat 2's land grows from the rocky south-east point next to seat 1 (a short graded pass), seat 3's from the east coast south of the Dustriders' islet (it stays an islet: keep); 310 m apart, each its own build space. The poachers' hunting camp sits behind both on grown land at the far south-east. All three walk north to the poachers (958 / 1279 / 1095 m). The holy mammoths and the arch druid (players 3, 4) are not tribes: no seat. Revised 2026-11-11 (docs/COOP.md \"Fair bases\": every seat at least as far from an enemy building as seat 1's base is in single player, and near seat 1): seat 3's base moved to new land grown from the coast west of seat 1 (a graded path up to the old land), 161 m from seat 1; the poachers' hunting camp behind the south-east seats was dropped (it stood 245 m from both). Nearest enemy building: seat 1 690 m, seat 2 788, seat 3 723.",
}
