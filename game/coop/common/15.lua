-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 15 "New World Order".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus", "tesla_s0", "lovelace_s0" },
  extend = {
    seed = 115,
    east = 96,
    grow = {
      { x = 1150, z = 1150, r = 60 },
    },
  },
  seats = {
    [2] = {
      start = { x = 790, z = 1015, dir = 0 },
      army = "default",
      resources = {
        { kind = "kit", x = 790, z = 1015 },
      },
    },
    [3] = {
      start = { x = 390, z = 1045, dir = 0 },
      army = "default",
      resources = {
        { kind = "kit", x = 390, z = 1045 },
      },
    },
  },
  naval = true,
  enemies = {
    {
      name = "SEAS gun battery",
      seats = 2,
      owner = "new",
      tribe = "SEAS",
      like = 5,
      at = { x = 1150, z = 1150, r = 36 },
      preset = "outpost",
      brain = false,
      notes = "A new SEAS player (standing as the SEAS support, player 5) on the south-east island, off seat 2's coast. The bases' counts (Gi01-04: players 1-4 defeated) and the control points (players 1-4) ignore it.",
    },
    {
      name = "SEAS west base reinforced",
      seats = 3,
      owner = 3,
      at = { x = 195, z = 740, r = 45 },
      buildings = {
        { class = "seas_hq_defense_turret" },
        { class = "seas_hq_defense_turret" },
        { class = "seas_hq_machinegun_nest" },
      },
      army = {
        { class = "seas_gunner", count = 3 },
        { class = "seas_flamethrower", count = 1 },
      },
      notes = "The SEAS west base (player 3, across the strait from seat 3) grows: two turrets, a machine-gun nest and a guard. Its objective (player 3 defeated) counts them - they stand in the base the mission sends the player to destroy.",
    },
  },
  allies = {
    usable = {  },
  },
  scaling = {
    count = 0.6,
    health = 0.25,
    damage = 0.15,
    exclude = { 5 },
  },
  triggers = { give_to = 1 },
  notes = "Base mission. All three build on the base island (the old map's land, no map change but the battery's islet): seat 2 on its east part, seat 3 on its west part, seat 1 between them as in single player - the bridges north and the sea are everybody's way to the SEAS (237 / 303 / 270 m by land and sea, about as far as seat 1). The south-east island the plan gave seat 2 is 1.8x seat 1's way, the land south-west of the base island 2.2x: both too far. The strait north and the islets in the south-west and west stay as they are. The SEAS support's helicopters (player 5) stand over the base island's east and west parts: not scaled (copies would hover over seats 2 and 3's bases from the start). Enemy additions: a SEAS gun battery on the south-east island (2+ seats), the SEAS west base grows (3 seats).",
}
