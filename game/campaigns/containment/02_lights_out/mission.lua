-- Containment 2: Lights Out (Black Canyon Generating Station, June 21, 1964). A night defence of the station until
-- dawn; power poles, searchlights and the first electric weapons. Ported from game/sequel/missions.js mission 2.
local S = require "sequel"
local T = S.T

return {
  setup = function(M)
    M.state.station = M.building("sq_power_station", "me", 20, 40, 0)
    M.objective("hold", "Keep the generating station standing until dawn (12:00)", {})
    M.objective("power", "Power the base: workers in the station, a line of Power Poles (at most 40 m apart) to the headquarters", { secondary = true })
    M.objective("lights", "Build three Searchlight Towers", { of = 3, secondary = true })
    M.mark(20, 40, "station", 0xffe070, 90)
    M.rift("west", -120, -150, { every = 120, first = 240, target = M.state.station, waves = {
      { { "Velociraptor", 4 } }, { { "Deinonychus", 4 } }, { { "Velociraptor", 4 }, { "Allosaurus", 1 } }, { { "Carnotaurus", 1 }, { "Deinonychus", 4 } }, { { "Allosaurus", 2 }, { "Velociraptor", 5 } } } })
    M.rift("east", 130, -160, { every = 130, first = 300, target = M.state.station, waves = {
      { { "Smilodon", 2 } }, { { "Velociraptor", 5 } }, { { "Smilodon", 3 }, { "Deinonychus", 2 } }, { { "Carnotaurus", 2 } } } })
    M.state.dawn = 12 * T
    M.set_clock(M.state.dawn, "Dawn in")
    M.say("taslov", "That station is the only thing in this desert that can feed my stabilizers. If it goes dark, the rifts stay open.")
    M.say("halloran", "Lights on the canyon road. Nothing gets to those turbines.")
    M.say("taslov", "And Colonel - a few engineers in that turbine hall and a pole line to camp, and we have all the current we want. My lightning guns are very thirsty.")
    M.after(6 * T, "midnight")
    M.after(9 * T, "last")
  end,
  midnight = function(M)
    M.say("kiko", "More of them on the ridge. A big one. Your searchlights are the only thing I can see from up here.")
    M.spawn("Allosaurus", "outbreak", -40, -200, 1, { target = M.state.station })
  end,
  last = function(M)
    M.say("radio", "Three hours to sunrise, Colonel. Barometers are going crazy around the rifts.")
    for _, r in ipairs(M.rifts()) do M.rift_set(r.id, "every", 80) end
  end,
  tick = function(M)
    M.progress("lights", S.built(M, "sq_searchlight"))
    local st = M.state.station and M.info(M.state.station)
    if st and st.alive and st.powered and st.workers > 0 and not M.is_done("power") then
      M.complete("power")
      M.say("taslov", "Do you hear it? Half of Nevada's current, and now some of it is ours. Build me a coil and I will show you what it does to a raptor.")
    end
    if not S.alive(M, M.state.station) then M.fail("hold") end
    if M.time >= M.state.dawn and not M.state.day then
      M.state.day = true
      M.set_night(false)
      M.complete("hold")
      M.say("halloran", "Sunrise. Every creature in that canyon just went to ground.")
      M.say("taslov", "And the station is still humming. Good. Now I can build you something bigger.")
      M.after(8, "end")
    end
  end,
  ["end"] = function(M) M.win("The lights stayed on.") end,
}
