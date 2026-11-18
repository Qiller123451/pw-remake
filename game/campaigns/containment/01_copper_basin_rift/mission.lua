-- Containment 1: The Copper Basin Rift (Copper Basin, Nevada, June 14, 1964). The townsfolk to the buses, the first
-- rift by the old copper mine, Dr. Taslov and his stabilizer. Data: mission.json; the API: docs/SEQUEL.md.
-- Ported from the browser remake's game/sequel/missions.js mission 1.
local S = require "sequel"

return {
  setup = function(M)
    M.state.need = S.by(M, { 8, 12, 14 })
    S.civilians(M, -40, -60, 7, 60) S.civilians(M, 10, -20, 5, 40) S.civilians(M, -170, 60, 3, 20) S.civilians(M, -40, 70, 3, 20)
    M.spawn("Velociraptor", "outbreak", 60, -90, 3) M.spawn("Velociraptor", "outbreak", -100, -20, 2)
    M.building("sq_field_hq", "me", -110, 200, 0.4)
    M.mark(-90, 200, "buses", 0x7dff6a)
    M.objective("evac", "Lead the townsfolk to the buses", { of = M.state.need, civ = true })
    M.say("radio", "Colonel, the town is dark and the sheriff stopped answering twenty minutes ago.")
    M.say("halloran", "Then we go in. Troopers, find the townsfolk - they will follow a uniform. Buses are waiting by the field post.")
    M.say("sheriff", "...anybody hear me? We are holed up all over Main Street. Those things are fast!")
    M.after(70, "rift")
    M.after(150, "taslov")
  end,
  rift = function(M)
    M.rift("mine", 170, -60, { every = 50, first = 5, waves = {
      { { "Velociraptor", 3 } }, { { "Velociraptor", 4 } }, { { "Deinonychus", 3 }, { "Velociraptor", 2 } }, { { "Allosaurus", 1 }, { "Velociraptor", 4 } } } })
    M.say("kiko", "Command, Kiko. I see it from up here - a ring of light by the old copper mine. Things are coming out of it.")
    M.say("halloran", "Then that is where they come from. Hold the town.")
  end,
  taslov = function(M)
    M.spawn("sq_taslov", "me", -40, 230, 1)
    M.say("taslov", "Taslov, Lev. My grandfather built the machine that tore the first hole. I built the thing that can stitch it.")
    M.say("taslov", "Have your engineers raise a Rift Stabilizer close to it - thirty metres - and keep the beasts off it for a minute.")
    M.introduce("sq_stabilizer", true)
    M.objective("seal", "Build a Rift Stabilizer at the rift by the mine and keep it standing until the rift closes", {})
    local r = M.rifts()
    if #r > 0 and not r[1].open then M.complete("seal") end
  end,
  riftClosed = function(M) M.complete("seal") end,
  evacuated = function(M, a)
    M.progress("evac", a.n)
    if a.n == 4 then M.say("sheriff", "Bless you, soldier. The Hendersons are still in the gas station!") end
  end,
  tick = function(M)
    local lost = M.counter("civLost")
    if lost > S.by(M, { 8, 6, 4 }) then M.lose("Too many townsfolk were lost.") end
    if #M.units("me", "sq_halloran") == 0 and M.time > 5 then M.lose("Colonel Halloran has fallen.") end
    if M.is_done("evac") and M.is_done("seal") and not M.state.won then
      M.state.won = true
      M.say("taslov", "There. Closed - for now. But listen to the ground, Colonel. It is still humming.")
      M.say("halloran", "Then we will be back. Pack up.")
      M.after(10, "end")
    end
  end,
  ["end"] = function(M) M.win("Copper Basin is safe. For now.") end,
}
