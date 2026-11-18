-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 4 "The Dustriders".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  extend = {
    seed = 104,
    south = 160,
    grow = {
      { x = 640, z = 1260, r = 50 },
    },
  },
  seats = {
    [2] = {
      start = { x = 420, z = 1295, dir = 0, land = 130 },
      army = { "aje_worker", "aje_worker", "aje_worker", "aje_worker", "aje_spearman", "aje_spearman" },
      extend = {
        south = 224,
        grow = {
          { x = 470, z = 1160, r = 60 },
          { x = 430, z = 1215, r = 60 },
        },
        bridges = {
          {
            points = {
              { x = 425, z = 1235 },
              { x = 432, z = 1190 },
              { x = 440, z = 1120 },
              { x = 419, z = 1073 },
            },
            width = 20,
            cut = 22,
            shape = "path",
          },
        },
      },
      resources = {
        { kind = "kit", x = 420, z = 1295 },
      },
    },
    [3] = {
      start = { x = 549, z = 956, dir = 0 },
      army = { "aje_worker", "aje_worker", "aje_worker", "aje_worker", "aje_spearman", "aje_spearman" },
      resources = {
        { kind = "kit", x = 549, z = 956 },
      },
    },
  },
  enemies = {
    {
      name = "Barbarian lookout",
      seats = 2,
      owner = "new",
      tribe = "Hu",
      like = 5,
      at = { x = 640, z = 1260, r = 36 },
      preset = "outpost",
      brain = false,
      notes = "A new barbarian player standing as the first barbarian outpost (player 5): towers on the bay between seat 2 and the village. The outposts' counts (Gf01/Gf02: players 5, 6 defeated) and the win (player 7 at the goods) ignore it.",
    },
  },
  allies = {
    usable = {  },
  },
  scaling = {
    count = 0.6,
    health = 0.25,
    damage = 0.15,
    exclude = { 2, 4 },
  },
  triggers = { give_to = 1 },
  notes = "Base mission (the Dustriders' village is the player's). Seat 2's land grows from the south-west coast with a graded pass up the cliffs into the valley west of the village, seat 3's south of the village, on land grown from the beach below it (a graded way up to the beach; the north-west coast is cliffs and the east coast by the temple is reached only through the barbarian town's gates). The barbarian town and camp are the nearest enemies for all three. The start army is the heroes only: the seats get workers and spearmen. Neutrals and the trader (players 2, 4) are left as they are. Revised 2026-11-11 (docs/COOP.md \"Fair bases\": every seat at least as far from an enemy building as seat 1's base is in single player, and near seat 1): seat 3's base moved to the old land west of the village (116 m from seat 1); the barbarian war camp south-east of the village (163 m from seat 3's old place) was dropped with the land grown for it. Nearest enemy building: 191 / 199 / 233 m.",
}
