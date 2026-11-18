-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 9 "Perilous Path".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  extend = { seed = 109 },
  seats = {
    [2] = {
      start = { x = 640, z = 1005, dir = 0 },
      army = { "ninigi_worker", "ninigi_worker", "ninigi_worker", "ninigi_worker", "ninigi_warrior", "ninigi_warrior", "ninigi_archer" },
      start_resources = { food = 200, wood = 300, stone = 200 },
      resources = {
        { kind = "kit", x = 640, z = 1005 },
      },
    },
    [3] = {
      start = { x = 520, z = 1085, dir = 0 },
      army = { "ninigi_worker", "ninigi_worker", "ninigi_worker", "ninigi_worker", "ninigi_warrior", "ninigi_warrior", "ninigi_archer" },
      start_resources = { food = 200, wood = 300, stone = 200 },
      resources = {
        { kind = "kit", x = 520, z = 1085 },
      },
    },
  },
  naval = true,
  enemies = {
    {
      name = "Barbarian base 3 reinforced",
      seats = 2,
      owner = 3,
      at = { x = 660, z = 560, r = 45 },
      buildings = {
        { class = "hu_small_tower" },
        { class = "hu_small_tower" },
      },
      army = {
        { class = "hu_warrior", count = 2 },
        { class = "hu_archer", count = 2 },
      },
      notes = "Barbarian base 3 (the north island's south-east, the nearest to the village) grows: towers and a guard. The base's objective (player 3 defeated) counts them - they stand in the base the mission sends the player to destroy. Placed when the bases are handed out.",
    },
    {
      name = "Barbarian base 1 reinforced",
      seats = 3,
      owner = 1,
      at = { x = 300, z = 905, r = 45 },
      buildings = {
        { class = "hu_small_tower" },
        { class = "hu_small_tower" },
        { class = "hu_large_tower" },
      },
      army = {
        { class = "hu_warrior", count = 3 },
        { class = "hu_archer", count = 3 },
      },
      notes = "Barbarian base 1 (the west island, across the inner sea from seat 3) grows: towers and a guard. The base's objective (player 1 defeated) counts them - they stand in the base the mission sends the player to destroy. Placed when the bases are handed out. (A new camp the waves could walk from would need land across the inner sea's mouth, the ships' way.)",
    },
  },
  allies = {
    usable = {  },
  },
  scaling = {
    count = 0.6,
    health = 0.25,
    damage = 0.15,
    exclude = { 4 },
  },
  triggers = { give_to = 1 },
  notes = "Hero phase, then base. The perilous path is the heroes' (one each); when the Dragon Clan village joins (Gd04 after sc_3030) its buildings and workers become seat 1's and its fighters are dealt round the seats (5 each with three seats); seats 2 and 3 get their bases on the village's island south of the village (the old map's land: its only free places; land grown into the inner sea would be too narrow and close to barbarian base 1, land at its mouth would close the ships' way). No map change. Enemy additions: barbarian base 3 grows (2+ seats), barbarian base 1 grows (3 seats) - the barbarians' islands are reached by ship.",
}
