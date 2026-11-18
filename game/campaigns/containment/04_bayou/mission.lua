-- Containment 4: The Bayou (Atchafalaya Basin, Louisiana, August 9, 1964). Three rifts in the swamp, hovercraft,
-- swimmers in the channels. Ported from game/sequel/missions.js mission 4.
local S = require "sequel"

return {
  setup = function(M)
    M.objective("rifts", "Seal the three rifts of the swamp", { of = 3 })
    M.objective("village", "Get the people of the farm to safety (to the camp)", { of = 4, secondary = true, civ = true })
    M.add_zone({ name = "camp", x = -200, z = 210, r = 18, kind = "evac" })
    S.civilians(M, -150, 80, 4, 14)
    -- (the user: "aggression on mission 4 is too big" - the rifts' creatures guard them, fewer and slower)
    local function G4(o) o.sealTime = 70 o.cap = 8 o.guard = 75 return o end
    M.rift("north", 160, -150, G4({ every = 100, first = 120, waves = { { { "Deinonychus", 3 } }, { { "Baryonyx", 2 } }, { { "Velociraptor", 3 }, { "Baryonyx", 1 } } } }))
    M.rift("south", 170, 180, G4({ every = 110, first = 180, waves = { { { "Baryonyx", 2 } }, { { "Velociraptor", 4 } }, { { "Spinosaurus", 1 }, { "Deinonychus", 2 } } } }))
    M.rift("west", -170, -160, G4({ every = 120, first = 240, waves = { { { "Velociraptor", 3 } }, { { "Baryonyx", 2 } }, { { "Spinosaurus", 1 }, { "Velociraptor", 2 } } } }))
    -- the swimmers keep to their stretch of the channels (90 m)
    for _, s in ipairs({ { "Mosasaurus", 0, -10 }, { "Liopleurodon", 120, 90 } }) do
      for _, u in ipairs(M.spawn(s[1], "outbreak", s[2], s[3], 1, { swim = true })) do M.set(u, "home", { s[2], s[3], 90 }) end
    end
    M.say("booker", "Gators I can handle. Whatever made that wake is not a gator.")
    M.say("taslov", "Three tears. Each needs its own stabilizer. Put your engineers on the hovercraft - they float over anything.")
  end,
  evacuated = function(M, a) M.progress("village", a.n) end,
  riftClosed = function(M)
    local n = #S.filter(M.rifts(), function(r) return not r.open end)
    M.progress("rifts", n)
    if n == 1 then M.say("kiko", "One down! The water around the north island just went flat calm.") end
    if n == 3 and not M.state.won then M.state.won = true M.say("taslov", "Three for three. The swamp can go back to being merely unpleasant.") M.after(8, "end") end
  end,
  ["end"] = function(M) M.win("The bayou is quiet again.") end,
}
