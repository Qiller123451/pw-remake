-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 7 "The Water Temple".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  extend = { seed = 107 },
  seats = {
    [2] = {
      start = { x = 714, z = 1110, dir = 0 },
      army = { "aje_worker", "aje_worker", "aje_worker", "aje_worker" },
      start_resources = { food = 300, wood = 300, stone = 300 },
      resources = {
        { kind = "kit", x = 714, z = 1110 },
      },
    },
    [3] = {
      start = { x = 425, z = 1005, dir = 0 },
      army = { "aje_worker", "aje_worker", "aje_worker", "aje_worker" },
      start_resources = { food = 300, wood = 300, stone = 300 },
      resources = {
        { kind = "kit", x = 425, z = 1005 },
      },
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
  notes = "Hero phase, then base (the user's choice: all three build at Taslow's home). The chase is the heroes' (one each); Taslow joins the human with the fewest units. When the heroes reach the farm Taslow's workers, the resource collector, his tents and Tesla towers become seat 1's (Gd03, as in single player) and seats 2 and 3 get their bases on the old land beside it: seat 2 south-east above the harbour where the SEAS spider convoys land, seat 3 south-west on Taslow's land. No map change (the dunes are open); the water city's and the camp's gates are not touched. Distances to the nearest enemy base: 454 / 474 / 615 m - the spiders come to Taslow's place. Revised 2026-11-11 (docs/COOP.md \"Fair bases\": every seat at least as far from an enemy building as seat 1's base is in single player, and near seat 1): seat 2's base moved to the open ground east of Taslow's home (714, 1110). Nearest enemy building: 205 / 268 / 364 m.",
}
