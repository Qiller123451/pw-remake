-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 1 "Stranded".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Stina_s0", "Bela_s0" },
  extend = { seed = 101 },
  seats = {
    [2] = {
      start = { x = 790, z = 185, dir = 0, land = 150 },
      army = { "hu_worker", "hu_worker", "hu_worker", "hu_worker", "hu_warrior", "hu_warrior", "hu_archer" },
      start_resources = { food = 100, wood = 100, stone = 100 },
      hero_army = { "hu_warrior" },
      extend = {
        north = 96,
        grow = {
          { x = 745, z = 285, r = 100 },
        },
        bridges = {
          {
            points = {
              { x = 735, z = 300 },
              { x = 700, z = 314 },
            },
            width = 16,
            cut = 10,
            shape = "path",
          },
        },
      },
      resources = {
        { kind = "kit", x = 790, z = 185 },
      },
    },
    [3] = {
      start = { x = 530, z = 180, dir = 0, land = 110 },
      army = { "hu_worker", "hu_worker", "hu_worker", "hu_worker", "hu_warrior", "hu_warrior", "hu_archer" },
      reach = { x = 540, z = 330 },
      start_resources = { food = 100, wood = 100, stone = 100 },
      hero_army = { "hu_warrior" },
      extend = {
        north = 256,
        grow = {
          { x = 545, z = 245, r = 65 },
          { x = 480, z = 45, r = 70 },
          { x = 465, z = 0, r = 65 },
          { x = 445, z = -40, r = 85 },
        },
        keep = {
          { x = 645, z = 312, r = 32 },
        },
        bridges = {
          {
            points = {
              { x = 445, z = -40 },
              { x = 462, z = 5 },
              { x = 490, z = 70 },
            },
            width = 18,
            cut = 12,
            shape = "path",
          },
          {
            points = {
              { x = 585, z = 240 },
              { x = 588, z = 275 },
              { x = 586, z = 314 },
            },
            width = 18,
            cut = 6,
            shape = "isthmus",
          },
        },
      },
      resources = {
        { kind = "kit", x = 530, z = 180 },
      },
    },
  },
  map_objects = {
    {
      name = "Nest_Small_Carnivores_14",
      attr = { spawn_max = 2 },
    },
  },
  enemies = {
    {
      name = "Barbarian watchtowers",
      seats = 2,
      owner = 2,
      at = { x = 850, z = 470, r = 40 },
      buildings = {
        { class = "hu_small_tower" },
        { class = "hu_small_tower" },
        { class = "hu_large_tower" },
      },
      army = {
        { class = "hu_warrior", count = 3 },
        { class = "hu_archer", count = 2 },
      },
      notes = "The barbarians' main base (player 2) grows: towers and a guard south of it. Its objective (Gi01: player 2 defeated and no fireplace of player 2) counts them - they stand at the base the mission sends the player to destroy. Placed when the bases are handed out.",
    },
    {
      name = "Barbarian raiders",
      seats = 3,
      owner = "new",
      tribe = "Hu",
      like = 2,
      at = { x = 445, z = -40, r = 44 },
      preset = "camp",
      brain = { behaviour = "Dodo", attack = true, first_attack = 480, interval = 360, target = 3 },
      notes = "A new barbarian player at the foot of the north-west headland (the headland itself is unwalkable rock in the original), behind seat 3: its only way out is through seat 3's lagoon land. It stands to everybody as the main base (player 2) does - neutral until the story turns the barbarians hostile. Placed when the bases are handed out; its first wave 8 minutes later. No objective counts it.",
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
  notes = "Hero phase, then base. Cole lands alone; seats 2 and 3 each start with one Norse warrior beside him and the beach nest sends its two Dilophosaurs together (the original: two in all, one after the other). What the story hands out is dealt round the seats: the village's 4 warriors (1 each, the 4th to the seat with the fewest units), the 4 freed spearmen (the same), Stina and Béla to the humans without a hero, Béla's 3 freed archers (1 each). When Béla frees the workers (the prison camp becomes seat 1's) the bases are handed out, both in the north: seat 2's on land grown into the north-east bay (a graded pass down to the outpost's valley), seat 3's in the north lagoon above the prison camp - a short isthmus over the bay's corner leads into the camp's inside, the rest touches only the empty north-west headland, so seat 3 goes out through the prison gates (seat 1's by then); the lagoon's south-east corner is kept. Enemies (from the hand-over): the barbarians' main base grows (its objective counts the towers); with three seats raiders at the foot of the north-west headland, behind seat 3.",
}
