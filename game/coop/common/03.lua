-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 3 "Amazon Island".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  extend = {
    seed = 103,
    grow = {
      { x = 1080, z = 250, r = 75 },
      { x = 1075, z = 340, r = 65 },
    },
  },
  seats = {
    [2] = {
      start = { x = 472, z = 133, dir = 0, land = 100 },
      army = "default",
      extend = {
        bridges = {
          {
            points = {
              { x = 472, z = 133 },
              { x = 427, z = 177 },
              { x = 442, z = 203 },
            },
            width = 16,
            cut = 10,
            shape = "path",
          },
        },
        keep = {
          { x = 230, z = 215, r = 70 },
          { x = 230, z = 760, r = 190 },
        },
      },
      resources = {
        { kind = "kit", x = 472, z = 133 },
      },
    },
    [3] = {
      start = { x = 592, z = 113, dir = 0, land = 95 },
      army = "default",
      extend = {
        east = 96,
        grow = {
          { x = 960, z = 470, r = 80 },
          { x = 1085, z = 440, r = 120 },
        },
        keep = {
          { x = 920, z = 940, r = 170 },
        },
        bridges = {
          {
            points = {
              { x = 592, z = 113 },
              { x = 559, z = 183 },
              { x = 534, z = 200 },
            },
            width = 16,
            cut = 10,
            shape = "path",
          },
          {
            points = {
              { x = 880, z = 486 },
              { x = 825, z = 477 },
            },
            width = 14,
            cut = 4,
            shape = "ford",
          },
          {
            points = {
              { x = 1050, z = 448 },
              { x = 995, z = 452 },
              { x = 942, z = 452 },
            },
            width = 18,
            cut = 20,
            shape = "path",
          },
        },
      },
      resources = {
        { kind = "kit", x = 592, z = 113 },
      },
    },
  },
  enemies = {
    {
      name = "Amazon war camp",
      seats = 3,
      owner = "new",
      tribe = "Aje",
      like = 1,
      at = { x = 1080, z = 250, r = 44 },
      preset = "camp",
      brain = { behaviour = "Dodo", attack = true, first_attack = 720, interval = 330, target = 3 },
      notes = "A new Amazon player on island 2's east side (the land grown there); its waves go for seat 3. No objective counts it.",
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
  notes = "Base mission. Seat 2's land grows from the main island's west coast (a beach), seat 3's from island 2's east side with a ford over the narrow channel to the main island's east beach - both about as far from the Amazons' fortress as seat 1 (540 / 668 / 643 m). The west island (player 4), the north-west islet and island 3 (the marketplace's) stay islands (keep); the fortress in the south is not touched. The flying trader (player 3) is a scripted ally: no seat. Revised 2026-11-11 (docs/COOP.md \"Fair bases\": every seat at least as far from an enemy building as seat 1's base is in single player, and near seat 1): both seats' bases moved to new land north of seat 1 (graded paths up to the old land; 114 / 184 m from seat 1); the scouts' outpost was dropped; the war camp keeps its land on island 2's east side (the old seat 3 land, now a lobe) and sends its waves at seat 3. Nearest enemy building: 456 / 549 / 476 m.",
}
