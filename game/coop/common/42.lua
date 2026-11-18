-- Co-op definition (schema v1, docs/COOP_DESIGN.md; the map part: docs/COOP.md "Maps") of mission 4-2 "A Cold Day In Hell".
-- Coordinates: the original map's (x east, z = map y south, metres from its north-west corner).
return {
  version = 1,
  nobuild = true,
  heroes = { "livingstone_s0", "schliemann_s0" },
  seats = {
    [2] = {
      start = { x = 170, z = 891, dir = 0 },
      army = "none",
    },
    [3] = {
      start = { x = 158, z = 891, dir = 0 },
      army = "none",
    },
  },
  allies = { usable = {  } },
  scaling = { count = 0.6, health = 0.25, damage = 0.15, exclude = { 5 } },
  triggers = { give_to = 1 },
  notes = "No-build mission: Leighton's party crosses the ice caves; Kleemann's expedition joins later (both heroes are divided among the humans). The seats start with the party; no map change. The Avatar and its guardians (player 5) are a scripted fight: not scaled.",
  -- not reworked with the main campaign's redesign (2026-11-03): co-op campaign (mode 2) greyed out, Shared mode only
  campaign = false,
  reason = "Co-op campaign: not reworked yet (Shared mode only)",
}
