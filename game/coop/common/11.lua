-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 11 "Arena".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = true,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus" },
  seats = {
    [2] = {
      start = { x = 279, z = 248, dir = 0 },
      army = "none",
    },
    [3] = {
      start = { x = 267, z = 248, dir = 0 },
      army = "none",
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
  notes = "No-build mission: the heroes fight in the arena. Heroes one each (2 humans: the host's split); nothing is handed out; the arena's opponents are not added to (their health and damage are scaled); no map change (a cave).",
}
