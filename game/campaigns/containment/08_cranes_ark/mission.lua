-- Containment 8: Crane's Ark (Kuskokwim Mountains, Alaska, December 3, 1964). Free Taslov from Crane's prison,
-- destroy his headquarters; the Ark's animals are let loose. Ported from game/sequel/missions.js mission 8.
local S = require "sequel"
local T = S.T

return {
  setup = function(M)
    local c = "crane"
    M.state.hq = M.building("seas_headquarters", c, 150, -150, math.pi)
    M.building("seas_barracks", c, 110, -150, math.pi) M.building("seas_garage", c, 150, -110, math.pi)
    for _, p in ipairs({ { 100, -110 }, { 190, -110 }, { 100, -190 }, { 80, -140 } }) do M.building("seas_turret_tower", c, p[1], p[2], 0) end
    M.state.jail = M.building("seas_jail_part_01", c, 190, -180, 0)
    M.spawn("sq_crane", c, 160, -170, 1)
    M.spawn("seas_gunner", c, 120, -120, 6, { level = 2 })
    M.spawn("seas_marksman", c, 130, -125, 4, { level = 2 })
    -- the Ark's cages: animals Crane lets loose when the alarm goes
    M.state.cages = { { 60, -60 }, { 40, -100 }, { 90, -40 } }
    M.objective("taslov", "Reach Crane's prison and free Dr. Taslov", {})
    M.objective("hq", "Destroy Crane's headquarters", {})
    M.say("booker", "Thermal scope shows a hundred heartbeats in that compound. Most of them are not human.")
    M.say("halloran", "Then we go in fast and we go in loud. Taslov first.")
    M.after(4 * T, "release")
  end,
  release = function(M)
    if M.state.released then return end
    M.state.released = true
    M.say("crane", "Margaret. You were always too sentimental for this job. Meet my menagerie.")
    for _, p in ipairs(M.state.cages) do
      M.spawn("Smilodon", "outbreak", p[1], p[2], 2, { target = { -170, 180 } })
      M.spawn("Woolly_Rhino", "outbreak", p[1] + 8, p[2], 1, { target = { -170, 180 } })
    end
    M.spawn("Mammoth", "outbreak", 60, -60, 2, { target = { -170, 180 } })
    M.after(5 * T, "release2")
  end,
  release2 = function(M)
    M.spawn("Mammoth", "outbreak", 90, -40, 2, { target = { -170, 180 } })
    M.spawn("Smilodon", "outbreak", 40, -100, 4, { target = { -170, 180 } })
  end,
  tick = function(M)
    if not M.state.freed and S.alive(M, M.state.jail) then
      -- (within 18 m of the prison - or of its walls: the browser remake's prison model was smaller than the footprint
      -- a building without model data gets)
      local reach = math.max(18, M.info(M.state.jail).radius + 6)
      if #M.near(190, -180, reach, "me") > 0 then
        M.state.freed = true
        M.spawn("sq_taslov", "me", 180, -170, 1)
        M.complete("taslov")
        M.say("taslov", "Colonel! He wants the Gate machine. He wants to open them all, permanently. He thinks he can sell dinosaurs to the Army!")
        M.say("crane", "Not sell, Lev. Lease.")
      end
    end
    if not M.state.freed and M.state.jail and not M.alive(M.state.jail) then M.fail("taslov") end
    if M.state.freed and not S.alive(M, M.state.hq) and not M.state.won then
      M.state.won = true
      M.complete("hq")
      M.say("kiko", "A plane just took off from behind the compound - heading south. Crane is getting away!")
      M.say("halloran", "Let him run. We have Taslov.")
      M.after(10, "end")
    end
    if not M.state.released and #M.near(120, -130, 80, "me") > 0 then M.call("release") end
  end,
  ["end"] = function(M) M.win("The Ark is broken. Crane is on the run.") end,
}
