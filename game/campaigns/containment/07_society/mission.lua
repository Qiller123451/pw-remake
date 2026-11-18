-- Containment 7: The Society (the Victoria Embankment, London, November 5, 1964). Bonfire Night: the townsfolk over
-- the bridge, Taslov to the tear by the great wheel, then the Society's archive - and Crane's men come up the river
-- stairs. Ported from game/sequel/missions.js mission 7.
local S = require "sequel"
local T = S.T

return {
  setup = function(M)
    M.state.taslov = S.first(M.spawn("sq_taslov", "me", 100, 12, 1))
    M.objective("evac", "Get the townsfolk over the bridge (north end)", { of = 8, civ = true })
    M.objective("rift", "Bring Dr. Taslov to the tear by the wheel and seal it", {})
    S.civilians(M, -200, 58, 4, 12) S.civilians(M, 0, 130, 4, 12) S.civilians(M, 150, 95, 4, 12)
    M.rift("wheel", -150, -62, { every = 80, first = 100, cap = 14, sealTime = 70, waves = {
      { { "Velociraptor", 3 } }, { { "Deinonychus", 3 } }, { { "Velociraptor", 3 }, { "Deinonychus", 2 } }, { { "Allosaurus", 1 }, { "Velociraptor", 3 } }, { { "Carnotaurus", 1 }, { "Deinonychus", 3 } } } })
    M.say("kiko", "Bonfire Night. Half of London thinks the dinosaurs are part of the show.")
    M.say("taslov", "Colonel, the tear is beside the great wheel - and it is no accident. Somebody opened it. Bring me there and I will stitch it shut.")
    M.say("halloran", "Booker, take the troopers into the streets. Get those people over the bridge.")
  end,
  evacuated = function(M, a) M.progress("evac", a.n) end,
  riftClosed = function(M)
    M.complete("rift")
    M.say("taslov", "Closed. Now the archive - Babbit kept the first Gate's papers in the cellar. Give me three minutes.")
    M.say("halloran", "Three minutes. Everybody else, around the Society.")
    M.state.toArchive = true
    if S.alive(M, M.state.taslov) then M.move({ M.state.taslov }, 100, 11) end
  end,
  -- Taslov goes in; Crane's people come up the river stairs
  archive = function(M)
    M.objective("society", "Hold the Society while Taslov reads the archive (3:00)", {})
    M.state.archiveEnd = M.time + 3 * T
    M.set_clock(M.state.archiveEnd, "The archive in")
    M.after(40, "landing")
  end,
  landing = function(M)
    M.say("kiko", "Boats on the river, no lights - a tug and a lighter, heading for the Society's stairs!")
    M.piece("tug", "sq_tug", 300, -112, 0, math.pi) M.piece("lighter", "sq_barge", 320, -112, 0, math.pi)
    M.sail("tug", { { 124, -110 } }, 12) M.sail("lighter", { { 104, -112 } }, 12)
    M.piece("steamer", "sq_riverboat", 380, -120, 0, math.pi) M.sail("steamer", { { 70, -124 } }, 11)
  end,
  arrived_lighter = function(M)
    if M.state.landed then return end
    M.state.landed = true
    M.say("crane", "Good evening, Margaret. Do carry on with your fireworks - I have only come for a few old papers.")
    M.say("halloran", "Crane. Hold them at the back of the house!")
    local k = M.difficulty
    local us = M.spawn("seas_gunner", "crane", 100, -82, 6 + 2 * k, { level = 3 })
    S.concat(us, M.spawn("seas_marksman", "crane", 90, -80, 3 + k, { level = 3 }))
    S.concat(us, M.spawn("seas_rocketman", "crane", 110, -80, 2 + k, { level = 3 }))
    M.send(us, { 100, -54 })
    M.after(30, "landing2")
  end,
  landing2 = function(M)
    local us = M.spawn("seas_gunner", "crane", 60, -82, 4 + 2 * M.difficulty, { level = 3 })
    S.concat(us, M.spawn("seas_mobile_suit", "crane", 140, -80, 1 + M.difficulty, { level = 2 }))
    M.send(us, { 100, -54 })
    M.say("kiko", "More of them coming up the stairs!")
  end,
  tick = function(M)
    local t = M.state.taslov
    if not M.state.inside then
      if not S.alive(M, t) and M.time > 3 then M.lose("Dr. Taslov has fallen.") return end
      if M.state.toArchive and S.alive(M, t) and S.near(M.info(t), 100, 11, 7) then
        M.state.inside = true
        M.remove(t)
        M.call("archive")
      end
    end
    if M.state.archiveEnd and not M.state.taken then
      -- Crane's men at the back of the house for long enough: they are in
      local at = #M.near(100, -54, 14, "crane")
      M.state.breach = at >= 3 and (M.state.breach or 0) + 0.5 or math.max(0, (M.state.breach or 0) - 0.5)
      if M.state.breach >= 20 then M.lose("Crane's men broke into the Society.") return end
      if M.time >= M.state.archiveEnd and M.state.landed then M.call("taken") end
    end
  end,
  -- the archive is safe - but Taslov was taken through the cellar to the steamer while the fight was at the back
  taken = function(M)
    if M.state.taken then return end
    M.state.taken = true
    M.complete("society")
    M.say("taslov", "Colonel, I have the papers - wait, who are - (the radio goes dead)")
    M.say("kiko", "The steamer at the Society's stairs is casting off! Downriver, fast, no lights!")
    M.say("crane", "The papers are no use without the man who can read them, Margaret. I shall take good care of him.")
    M.sail("steamer", { { 0, -128 }, { -200, -136 }, { -420, -140 } }, 9, true)
    for _, id in ipairs({ "tug", "lighter" }) do M.sail(id, { { -60, -120 }, { -420, -128 } }, 8, true) end
    M.move(M.units("crane"), 100, -86)
    M.after(24, "end")
  end,
  ["end"] = function(M)
    -- (the townsfolk still out first: the mission ends when they are over the bridge)
    if not M.is_done("evac") then
      if not M.state.toldEvac then M.state.toldEvac = true M.say("halloran", "Crane can wait. Get the last of those people over the bridge.") end
      M.after(5, "end")
      return
    end
    M.say("halloran", "Get me Washington. And find out where that boat is going.")
    M.win("The archive is safe. Dr. Taslov is not.")
  end,
}
