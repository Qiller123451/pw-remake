-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 12 "Sea Battles".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = false,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  extend = { seed = 112, style = "island" },
  seats = {
    [2] = {
      start = { x = 505, z = 365, dir = 0 },
      army = "default",
      extend = {
        bridges = {
          {
            points = {
              { x = 540, z = 330 },
              { x = 620, z = 330 },
            },
            width = 14,
            cut = 2,
            shape = "ford",
          },
        },
      },
      resources = {
        { kind = "kit", x = 505, z = 365 },
      },
    },
    [3] = {
      start = { x = 755, z = 500, dir = 0 },
      army = "default",
      resources = {
        { kind = "kit", x = 755, z = 500 },
      },
    },
  },
  naval = true,
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
  notes = "Base and naval mission (the SEAS carriers come by sea). Islands kept (style island): the scripted landings (Anlanderegion, Aje_Sneak_Unload) and the carriers' lanes must not move. Seat 2 builds on the sand island west of the telescope island (the old map's own land, a ford to the telescope island), facing the carriers' target zone; seat 3 on the telescope island's south side above the SEAS landing coast (the north-east islet the plan had is bare rock without trees). By land and sea 308 / 418 / 228 m. No enemy additions: the SEAS come in scripted waves by sea, which the co-op scaling makes bigger. Revised 2026-11-11 (docs/COOP.md \"Fair bases\": every seat at least as far from an enemy building as seat 1's base is in single player, and near seat 1): seat 3's base 25 m further north on the telescope island (755, 500). Nearest enemy building: 344 / 437 / 350 m.",
}
