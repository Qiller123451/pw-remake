-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 13 "The Rush".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus", "darwin_s0" },
  extend = { seed = 113 },
  seats = {
    [2] = {
      start = { x = 930, z = 370, dir = 0 },
      army = { "ninigi_worker", "ninigi_worker", "ninigi_worker", "ninigi_worker" },
      resources = {
        { kind = "kit", x = 930, z = 370 },
      },
    },
    [3] = {
      start = { x = 850, z = 390, dir = 0 },
      army = { "ninigi_worker", "ninigi_worker", "ninigi_worker", "ninigi_worker" },
      resources = {
        { kind = "kit", x = 850, z = 390 },
      },
    },
  },
  gates = {
    {
      seats = 3,
      name = "the second city gate",
      class = "hcl13_gate",
      replaces = { "hcl13_city_wall_79", "hcl13_city_wall_74", "hcl13_city_wall_85" },
    },
  },
  reroute = {
    {
      seats = 3,
      near = { x = 821, z = 335, r = 45 },
      to = {
        { x = 775, z = 420 },
        { x = 729, z = 322 },
      },
      every = 2,
      from = { x = 538, z = 465, r = 70 },
    },
    {
      seats = 3,
      near = { x = 763, z = 324, r = 30 },
      to = {
        { x = 729, z = 322 },
      },
      every = 1,
    },
  },
  start_split = {
    {
      ["for"] = 3,
      at = { x = 900, z = 555, r = 70 },
      to = 3,
      classes = { "ninigi_small_tower" },
      move = { x = 698, z = 333 },
    },
    {
      ["for"] = 3,
      at = { x = 816, z = 408, r = 12 },
      to = 3,
      classes = { "ninigi_snare_trap" },
      max = 1,
      move = { x = 722, z = 323 },
    },
    {
      ["for"] = 3,
      at = { x = 952, z = 416, r = 12 },
      to = 3,
      classes = { "ninigi_snare_trap" },
      max = 1,
      move = { x = 742, z = 327 },
    },
    {
      ["for"] = 3,
      at = { x = 952, z = 430, r = 45 },
      to = 2,
    },
    {
      ["for"] = 3,
      at = { x = 900, z = 555, r = 70 },
      to = 2,
    },
    {
      ["for"] = 2,
      at = { x = 952, z = 430, r = 45 },
      to = 2,
    },
    {
      ["for"] = 2,
      at = { x = 900, z = 555, r = 70 },
      to = 2,
    },
  },
  deal_start = true,
  enemies = {
    {
      name = "SEAS forward post",
      seats = 2,
      owner = "new",
      tribe = "SEAS",
      like = 2,
      at = { x = 430, z = 560, r = 40 },
      preset = "outpost",
      brain = false,
      notes = "A new SEAS player (standing as the camp in the west, player 2) by the SEAS camp W. The outposts' counts (players 3, 4 defeated) and the traders' check ignore it.",
    },
    {
      name = "SEAS assault camp",
      seats = 3,
      owner = "new",
      tribe = "SEAS",
      like = 1,
      at = { x = 880, z = 655, r = 50 },
      preset = "camp",
      brain = { behaviour = "Dodo", attack = true, first_attack = 660, interval = 300 },
      notes = "A new SEAS player on the waves' way south of the compound (the old SEAS route from the south); its waves go for the nearest seat. No objective counts it.",
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
  notes = "Base mission (hold the city wall: X01 - the city gate or any of its wall pieces destroyed loses the mission). All bases at the wall: seat 1 in front of the gate as in single player, seat 2 in the compound's east part (inside the skewer palisades), seat 3 south-west of seat 1's base on the west waves' way, defending a second gate 90 m west of the first (three seats: a gate of the city's own class in place of wall pieces 79, 74 and 85 - X01's conditions on those pieces watch it, so either gate falling loses the mission with the game's own text; the wall further west stands over an enclosed pocket no attacker reaches). With three seats the west waves and every second east / south wave go for the second gate. Seat 1's placed defence is split: the east palisade, towers and traps seat 2's, two towers and two traps moved to the second gate as seat 3's, the start fighters dealt round the seats; seats 2 and 3 get a main building and 4 workers of their own. Revised 2026-11-11 (docs/COOP.md \"Fair bases\": every seat at least as far from an enemy building as seat 1's base is in single player, and near seat 1): both seats' bases moved inside the compound behind the wall, east of seat 1: seat 2 (930, 370), seat 3 (850, 390) - the compound's east edge lost seat 2's base to the first waves (\"a single tent\"); the split of seat 1's defence is unchanged (seat 3's towers still at the second gate). Nearest enemy building: 191 / 233 / 216 m.",
}
