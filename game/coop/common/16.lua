-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 16 "Showdown!".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
-- Redesigned 2026-11-03 with the user (fair base places, hero phase, gifts dealt over the seats): docs/COOP.md.
return {
  version = 1,
  nobuild = true,
  heroes = { "Cole_s0", "Bela_s0", "special_eusmilus", "lovelace_s0" },
  seats = {
    [2] = {
      start = { x = 510, z = 509, dir = 0 },
      army = "none",
    },
    [3] = {
      start = { x = 498, z = 509, dir = 0 },
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
    exclude = { 3 },
  },
  triggers = { give_to = 1 },
  notes = "No-build mission: the heroes break the SEAS bases in the volcano and fight the Scorpio and Babbit. Heroes one each, the host gives Lovelace to one human (2 humans: two each); the turret towers the story hands over are dealt round the seats; no extra units and no extra enemies (numbers not scaled; health and damage are); Babbit (player 3) is the end boss: not scaled at all; no map change (a cave).",
}
