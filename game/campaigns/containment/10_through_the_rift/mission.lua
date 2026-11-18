-- Containment 10: Through the Rift (ParaWorld, March 20, 1965 by our calendar). Taslov to the temple of the Gate,
-- hold it while he takes the core, then home through the rift. Ported from game/sequel/missions.js mission 10.
local S = require "sequel"
local T = S.T

local function taslov(M) return S.first(M.units("me", "sq_taslov")) end

return {
  setup = function(M)
    M.rift("home", -210, 210, { every = 1e9, first = 1e9 })
    M.spawn("sq_tala", "aje", -150, 170, 1) M.spawn("aje_warrior", "aje", -140, 175, 5, { level = 2 })
    local c = "crane"
    M.building("seas_headquarters", c, 120, -120, 0) M.building("seas_turret_tower", c, 140, -100, 0) M.building("seas_turret_tower", c, 100, -140, 0)
    M.spawn("seas_gunner", c, 130, -130, 6, { level = 3 }) M.spawn("seas_wehrspinne", c, 150, -130, 1)
    M.spawn("Atroxosaurus", "outbreak", 30, -60, 1) M.spawn("Allosaurus", "outbreak", -40, -150, 2) M.spawn("Velociraptor", "outbreak", 60, 60, 4)
    M.objective("temple", "Bring Dr. Taslov to the temple of the Gate (north-east)", {})
    M.say("taslov", "Remarkable. The air is thicker. Everything is greener. Colonel - everything is hungrier.")
    M.say("tala", "The temple is past the hill. The men in grey are already there. And the King of Teeth walks the valley.")
  end,
  tick = function(M)
    local id = taslov(M)
    local tas = id and M.info(id)
    if not tas and M.time > 5 then M.lose("Dr. Taslov has fallen.") end
    if not M.state.at and tas and S.near(tas, 160, -160, 18) then
      M.state.at = true M.complete("temple")
      M.state.extract = M.time + 3 * T
      M.set_clock(M.state.extract, "The core in")
      M.objective("core", "Hold the temple while Taslov extracts the core (3:00)", {})
      M.say("taslov", "It is here. Babbit's old machine - still turning. Three minutes. Do not let anything near me.")
      M.spawn("Atroxosaurus", "outbreak", 60, -40, 1, { target = { 160, -160 } }) M.spawn("Velociraptor", "outbreak", 80, -200, 6, { target = { 160, -160 } })
      M.after(90, "surge")
    end
    if M.state.at and not M.is_done("core") and tas and not S.near(tas, 160, -160, 30) then M.notify("Taslov must stay at the temple!", "bad") end
    if M.state.at and not M.is_done("core") and M.time >= M.state.extract then
      M.complete("core")
      M.say("taslov", "I have it. Now - home, before the door slams.")
      M.objective("home", "Return Taslov to the rift you came through (south-west)", {})
    end
    if M.is_done("core") and not M.state.won and tas and S.near(tas, -210, 210, 25) then
      M.state.won = true M.complete("home")
      M.say("halloran", "Everyone through. Tala - you too. You have a place with us.")
      M.after(8, "end")
    end
  end,
  surge = function(M)
    M.spawn("Allosaurus", "outbreak", 220, -60, 3, { target = { 160, -160 } })
    M.spawn("Triceratops", "outbreak", 220, -220, 2, { target = { 160, -160 } })
  end,
  ["end"] = function(M) M.win("The core of the Gate is in SEAS hands.") end,
}
