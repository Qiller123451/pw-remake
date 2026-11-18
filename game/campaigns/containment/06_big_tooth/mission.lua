-- Containment 6: Big Tooth (Bellingham, Washington, October 18, 1964). Electrified nets, darts and a capture truck
-- for the giant. Ported from game/sequel/missions.js mission 6.
local S = require "sequel"

return {
  setup = function(M)
    M.pen("pen", -160, 186)
    M.mark(-200, 120, "pen", 0x7dff6a)
    S.civilians(M, 10, -10, 10, 120)
    M.state.boss = S.first(M.spawn("sq_big_tooth", "outbreak", 160, -170, 1))
    if M.state.boss then M.set(M.state.boss, "scale", 1.6) end
    M.rift("hill", 190, -190, { every = 70, first = 60, waves = { { { "Velociraptor", 4 } }, { { "Deinonychus", 3 } }, { { "Allosaurus", 1 }, { "Velociraptor", 3 } } } })
    M.objective("nets", "Build Electrified Nets (Taslov can build them)", { of = 2, secondary = true })
    M.objective("boss", "Stun Big Tooth on a net, put it to sleep and haul it to the pen", {})
    M.objective("people", "Bring the townsfolk to the shelter", { of = 6, secondary = true, civ = true })
    M.say("kiko", "I've got eyes on it. It's - Colonel, it's taller than the water tower.")
    M.say("taslov", "My nets will hold it for fifteen seconds. In those fifteen seconds every dart you have goes into it.")
    M.after(60, "roam")
  end,
  roam = function(M)            -- (not while she sleeps)
    local b = M.state.boss and M.info(M.state.boss)
    if b and b.alive and not b.asleep and not b.parked then M.send({ M.state.boss }, { 0, 0 }) end
    M.after(45, "roam")
  end,
  evacuated = function(M, a) M.progress("people", a.n) end,
  tick = function(M)
    M.progress("nets", S.built(M, "sq_electric_net"))
    local b = M.state.boss
    if b and not M.alive(b) and not M.state.dead and not M.state.caught then
      M.state.dead = true
      M.say("booker", "Well. That's one way.")
      M.complete("boss")
      M.after(8, "end")
    end
  end,
  netted = function(M, a)
    if a.u == M.state.boss then M.notify("Big Tooth is caught in the net!", "good") M.say("taslov", "Now! Darts, now!") end
  end,
  asleep = function(M, a) if a.u == M.state.boss then M.say("booker", "She's down! Trucks, go go go!") end end,
  captured = function(M, a)
    if a.u == M.state.boss then
      M.state.caught = true
      M.complete("boss")
      M.say("booker", "Biggest pet in the State of Washington.")
      M.after(8, "end")
    end
  end,
  ["end"] = function(M) M.win("Big Tooth will never bite again.") end,
}
