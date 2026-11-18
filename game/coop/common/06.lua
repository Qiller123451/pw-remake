-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 6 "Pirates and Hostages".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  extend = {
    seed = 106,
    east = 352,
  },
  seats = {
    [2] = {
      start = { x = 1460, z = 375, dir = 0, land = 115 },
      army = "default",
      extend = {
        east = 160,
        grow = {
          { x = 1280, z = 340, r = 140 },
          { x = 1380, z = 320, r = 120 },
        },
        keep = {
          { x = 1330, z = 710, r = 110 },
          { x = 1360, z = 200, r = 50 },
          { x = 1290, z = 610, r = 40 },
        },
      },
      resources = {
        { kind = "kit", x = 1460, z = 375 },
      },
    },
    [3] = {
      start = { x = 1610, z = 545, dir = 0, land = 120 },
      army = "default",
      extend = {
        east = 288,
        grow = {
          { x = 1700, z = 720, r = 70 },
          { x = 1745, z = 790, r = 60 },
          { x = 1785, z = 855, r = 75 },
          { x = 1300, z = 440, r = 130 },
          { x = 1450, z = 520, r = 130 },
        },
        keep = {
          { x = 1330, z = 710, r = 110 },
          { x = 1290, z = 610, r = 40 },
        },
        bridges = {
          {
            points = {
              { x = 1590, z = 540 },
              { x = 1545, z = 535 },
              { x = 1495, z = 530 },
            },
            width = 20,
            cut = 6,
            shape = "isthmus",
          },
        },
      },
      resources = {
        { kind = "kit", x = 1610, z = 545 },
      },
    },
  },
  naval = true,
  enemies = {
    {
      name = "Pirate raiders' cove",
      seats = 3,
      owner = "new",
      tribe = "Ninigi",
      like = 5,
      at = { x = 1785, z = 855, r = 40 },
      preset = "camp",
      brain = { behaviour = "Dodo", attack = true, first_attack = 720, interval = 360, target = 3 },
      notes = "A new pirate player south-east of seat 3 (316 m), on grown land joined to seat 3's (the lobes touch only at their beaches and meet in ridges: the extension leads graded paths across - docs/COOP.md \"Joins\"); its waves go for seat 3 on foot. No objective counts it.",
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
  notes = "Base and naval mission (the big pirate ship is sunk from the sea). The Dustriders' island grows east into the open sea, where no sea lane runs: seat 2's land north-east, seat 3's east; by land and sea both are about as far from the pirates as seat 1 (299 / 348 / 271 m). The pirate island in the south-east, the channel to it and the north strip stay as they are (keep): the ships' ways west and south are open as before. Revised 2026-11-11 (docs/COOP.md \"Fair bases\": every seat at least as far from an enemy building as seat 1's base is in single player, and near seat 1): the pirate lookout between the seats (84 m from seat 1) was dropped, the raiders' cove moved south-east to (1785, 855) on its own grown land. Nearest enemy building: 288 / 364 / 307 m.",
}
