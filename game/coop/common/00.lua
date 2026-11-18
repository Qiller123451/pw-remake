-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 0 "Tutorial".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Stina_s0", "Bela_s0" },
  extend = { seed = 100 },
  seats = {
    [2] = {
      start = { x = 985, z = 180, dir = 0 },
      army = { "hu_worker", "hu_worker", "hu_worker", "hu_worker", "hu_warrior", "hu_warrior", "hu_archer" },
      start_resources = { food = 150, wood = 150, stone = 50 },
      extend = {
        east = 128,
        grow = {
          { x = 850, z = 300, r = 150 },
          { x = 930, z = 220, r = 140 },
          { x = 900, z = 370, r = 140 },
        },
      },
      resources = {
        { kind = "kit", x = 985, z = 180 },
      },
    },
    [3] = {
      start = { x = 1000, z = 620, dir = 0 },
      army = { "hu_worker", "hu_worker", "hu_worker", "hu_worker", "hu_warrior", "hu_warrior", "hu_archer" },
      start_resources = { food = 150, wood = 150, stone = 50 },
      extend = {
        east = 96,
        south = 64,
        grow = {
          { x = 840, z = 510, r = 150 },
          { x = 920, z = 580, r = 130 },
          { x = 900, z = 450, r = 140 },
        },
      },
      resources = {
        { kind = "kit", x = 1000, z = 620 },
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
    exclude = { 3 },
  },
  triggers = { give_to = 1 },
  notes = "Hero phase, then base. Chapter 1 is the heroes' (Cole, Stina, Bela: one each); when Ada hands over the village (Gba02: workers and the town centre) the bases are handed out: seat 2's on land grown from the start headland's north-east coast, seat 3's from its east coast further south (about 500 m apart; the places the co-op tutorial had). Both join the start pen (the ground seat 1 walks before the first gate opens), so the tutorial's gates still open the island step by step; the north lagoon and the village's south-east coast the review proposed lie inside the village's fence, beyond the gates - land there would join the pen to the village round the gates. No enemy additions: the tutorial is scripted step by step. The thief (player 3) is a scripted pickpocket: not scaled.",
}
