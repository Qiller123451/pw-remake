-- Co-op definition (schema v1, docs/COOP.md "The definition") of the remake's mission "Outbreak" (docs/OUTBREAK.md).
-- Each seat's camp sits by one of the plateau's three ramps (layout.lua: camps A north, B east, C south-west - behind
-- the rockfall until the infected break it); seat 1 is the mission's own player at camp A. Every seat starts in epoch 2
-- (custom.lua), its army the mission's start army in its tribe. Every camp has its giant fallen tree and its quarry
-- whether a seat is there or not (in single player they are all yours). The map is not extended: the camps are on it.
-- Coordinates: map metres (x east, z = map y south, from the north-west corner).
return {
  version = 1,
  nobuild = false,
  heroes = {},
  seats = {
    [2] = { start = { x = 444, z = 384, dir = 4.7124 }, army = "default" },        -- camp B, by the east ramp
    [3] = { start = { x = 341.57, z = 426.43, dir = 0.7854 }, army = "default" },  -- camp C, by the south-west ramp (the rockfall)
  },
  -- the infected (player 1): more and tougher with every seat (CoopScaling's defaults: +50 % units, +20 % health,
  -- +10 % damage per extra seat, times the co-op difficulty) - the night waves are spawns, so they grow with it
  scaling = { count = 0.5, health = 0.2, damage = 0.1 },
  reach = { x = 384, z = 384 },
  notes = "Outbreak: one camp per ramp. Hold your ramp at night; burn the infected villages by day together.",
}
