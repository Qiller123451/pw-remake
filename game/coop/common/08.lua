-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 8 "Valley of the Gods".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  extend = {
    seed = 108,
    west = 256,
    grow = {
      { x = -40, z = 1060, r = 75 },
      { x = 5, z = 975, r = 60 },
    },
  },
  seats = {
    [2] = {
      start = { x = 480, z = 1180, dir = 0 },
      army = "default",
      resources = {
        { kind = "kit", x = 480, z = 1180 },
      },
    },
    [3] = {
      start = { x = 45, z = 880, dir = 0, land = 125 },
      army = "default",
      extend = {
        west = 224,
        bridges = {
          {
            points = {
              { x = 80, z = 822 },
              { x = 110, z = 817 },
              { x = 140, z = 812 },
            },
            width = 18,
            cut = 6,
            shape = "isthmus",
          },
        },
      },
      resources = {
        { kind = "kit", x = 45, z = 880 },
      },
    },
  },
  enemies = {
    {
      name = "Barbarian outriders",
      seats = 2,
      owner = "new",
      tribe = "Hu",
      like = 1,
      at = { x = 815, z = 1080, r = 34 },
      preset = "outpost",
      brain = false,
      notes = "A new barbarian player (standing as the outpost, player 1) with towers east of seat 2. The outpost's count (G01, rescamp) ignores it.",
    },
    {
      name = "Barbarian war camp",
      seats = 3,
      owner = "new",
      tribe = "Hu",
      like = 2,
      at = { x = -40, z = 1060, r = 44 },
      preset = "camp",
      brain = { behaviour = "Dodo", attack = true, first_attack = 720, interval = 330, target = 3 },
      notes = "A new barbarian player south of seat 3; its waves go for seat 3. No objective counts it.",
    },
  },
  allies = {
    usable = {  },
  },
  scaling = {
    count = 0.6,
    health = 0.25,
    damage = 0.15,
    exclude = { 3, 5 },
  },
  triggers = { give_to = 1 },
  -- the hero phase (user, 2026-11-11): seats 2 / 3 get their bases when the barbarian camp on the plateau north of the
  -- start (region "rescamp", player 1's) is cleared - D02#ds_2592 "Excellent, the plateau is ours", as in single player;
  -- the AI commanders' fighters attack the camp until then
  phase = { hero = true, base_when = "trigger:D02#ds_2592", attack = "rescamp" },
  -- seat 1's four start spearmen dealt round the seats (Kleemann, the heroes and the workers stay seat 1's)
  deal_start = true,
  notes = "Hero phase, then base (user, 2026-11-11). Seat 1 starts with its base as in single player; the four start spearmen are dealt round the seats (3 seats: 2 / 1 / 1 to seats 2 / 3 / 1 - the fewest units first; 2 seats: 2 / 2), the heroes go to the humans, Kleemann and the workers stay seat 1's. Until the barbarian camp on the plateau north of the start (player 1's, region rescamp) is cleared - D02#ds_2592 'the plateau is ours' - seats 2 and 3 have no base and the enemies get no more units; AI commanders' fighters attack the camp. Then the bases, the enemy additions and the count scaling. Base mission (Kleemann is escorted to the keystones). Seat 2 builds on the bay's south shore east of the start (the old map's land: the bay south of it is full of ice floes the growth keeps clear of; the bay's north shore behind the fortress' gates is not touched; the islets stay islets); the outriders stand on the land east of the bay, seat 3's from the west coast with a short way up into the valley. Seats 1 and 2 face the barbarian fortress from the south, seat 3 the barbarian town from the west (470 / 671 / 456 m). Valhalla's guards and the keystone guards (players 3, 5) are scripted: not scaled.",
}
