-- Containment 5: Countdown at the Cape (Cape Canaveral, Florida, September 30, 1964). Protect Launch Complex 34 for
-- fifteen minutes until lift-off. Ported from game/sequel/missions.js mission 5.
local S = require "sequel"
local T = S.T

return {
  setup = function(M)
    M.state.pad = M.building("sq_launch_pad", "me", 10, 0, 0)
    M.state.launch = 15 * T
    M.set_clock(M.state.launch, "Lift-off in")
    M.objective("pad", "Protect Launch Complex 34 until lift-off", {})
    M.rift("beachN", 120, -130, { every = 120, first = 240, target = M.state.pad, waves = {
      { { "Velociraptor", 4 } }, { { "Baryonyx", 2 } }, { { "Carnotaurus", 1 }, { "Velociraptor", 3 } }, { { "Spinosaurus", 1 }, { "Baryonyx", 2 } } } })
    M.rift("beachS", 120, 140, { every = 130, first = 300, target = M.state.pad, waves = {
      { { "Baryonyx", 2 } }, { { "Velociraptor", 5 } }, { { "Spinosaurus", 1 }, { "Carnotaurus", 1 } } } })
    M.spawn("Kronosaurus", "outbreak", 190, 0, 1, { swim = true })
    M.spawn("Dunkleosteus", "outbreak", 200, 60, 1, { swim = true })
    M.say("scientist", "Imai, flight control. Fifteen minutes, Colonel. We cannot hold the count - the window closes with the sun.")
    M.say("halloran", "You fly the rocket, Doctor. We will keep the beach.")
    M.after(5 * T, "ten") M.after(10 * T, "five") M.after(14 * T, "one")
  end,
  ten = function(M) M.say("scientist", "T minus ten. Fuelling complete.") end,
  five = function(M)
    M.say("scientist", "T minus five. Gantry retracting. Please keep them off my rocket!")
    for _, r in ipairs(M.rifts()) do M.rift_set(r.id, "every", 90) end
  end,
  one = function(M) M.say("scientist", "T minus sixty seconds...") end,
  tick = function(M)
    if not S.alive(M, M.state.pad) then M.fail("pad") end
    if M.time >= M.state.launch and not M.state.won then
      M.state.won = true
      M.complete("pad")
      M.say("scientist", "Lift-off! We have lift-off!")
      M.say("taslov", "Now she sees all of them. And so do we.")
      M.state.flight = 0
      M.fx("launch", 10, 0, 1)            -- (the rocket goes up: the view's SequelFx)
      M.after(12, "end")
    end
  end,
  ["end"] = function(M) M.win("The stabilizer satellite is in orbit.") end,
}
