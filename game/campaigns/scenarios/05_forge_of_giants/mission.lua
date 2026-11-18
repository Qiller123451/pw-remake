-- Scenario 5: The Forge of Giants (the Frozen Reach). A countdown: the enemy tribe builds a Titan in its fortress, fed
-- by three forges. Each forge brought down puts the Titan back by three minutes and leaves it weaker. Won when the
-- fortress falls (and the Titan, once it walks). Ported from the browser remake's game/scenarios/missions.js scenario 5.
local S = require "sequel"
local C = require "scenarios"
local T = S.T

local function homePos(M, def)
  local h = M.home("me")
  if h then local i = M.info(h) return { i.x, i.z } end
  return def
end

return {
  setup = function(M)
    local st, K = M.state, C.KIT[M.def.foeTribe]
    C.startHero(M)
    st.home = C.settle(M, "foe", M.def.foeTribe, 185, -185, { level = 3, hero = true, workers = 8, warriors = 8, archers = 6, beasts = 2,
      towers = { { 30, 24 }, { -30, 24 }, { 30, -24 }, { -30, -24 }, { 0, 40 }, { 40, 0 } } })
    st.forges, st.forgeOf = {}, {}
    for i, p in ipairs({ { -150, -120 }, { 40, -20 }, { 140, 110 } }) do
      local x, z = p[1], p[2]
      local b = M.building(K.temple, "foe", x, z, 0)
      if b then M.scale_hp(b, 1.5) st.forgeOf[b] = i - 1 end
      M.building(K.tower, "foe", x + 16, z + 14, 0)
      M.spawn(K.warrior, "foe", x, z + 18, C.more(M, 3), { level = 2 }) M.spawn(K.archer, "foe", x + 6, z + 20, C.more(M, 2), { level = 2 })
      M.mark(x, z, "forge:" .. (i - 1), 0xff8030)
      st.forges[i] = { x = x, z = z, b = b }
    end
    st.titanAt = M.def.titanAt
    M.set_clock(st.titanAt, "The Titan walks in")
    M.objective("forges", "Destroy the three forges (each sets the Titan back 3:00 and weakens it)", { of = 3, secondary = true })
    M.objective("fortress", "Destroy the warlord's fortress (his main building, north-east)", {})
    M.mark(185, -185, "fortress", 0xff5030)
    M.say("seer", "The forges glow day and night. When the last bolt is struck, the Titan will open its eyes.")
    M.say("warlord", "Kneel now, and you may serve my giant. Stand, and you will be its first meal.")
    M.say(st.v, "Three forges. We break them, or we break the giant. Either way - we break something.")
    M.after(5 * T, "patrol")
  end,
  patrol = function(M)
    local st, K = M.state, C.KIT[M.def.foeTribe]
    if st.over or not S.alive(M, st.home) then return end
    local us = M.spawn(K.warrior, "foe", 160, -160, C.more(M, 4), { level = 2 })
    S.concat(us, M.spawn(K.archer, "foe", 165, -155, C.more(M, 2), { level = 2 }))
    M.send(us, homePos(M, { -200, 200 }))
    M.say("scout", "A war band left the fortress - coming our way!")
    M.after(S.by(M, { 240, 200, 160 }), "patrol")
  end,
  died = function(M, a)
    local st = M.state
    local fi = st.forgeOf[a.e]
    if fi ~= nil and not st.titan then
      st.forgeOf[a.e] = nil
      local n = #S.filter(st.forges, function(f) return not S.alive(M, f.b) end)
      st.titanAt = st.titanAt + 3 * T
      M.set_clock(st.titanAt, "The Titan walks in")
      M.unmark("forge:" .. fi)
      M.progress("forges", n)
      M.say("seer", ({ "One forge is cold. The giant's heart beats slower.", "Two forges dark. The giant will be lame.", "The last forge is out! If it walks now, it walks half-made." })[n])
    end
    if st.titan and a.e == st.titan then M.complete("titan") M.say(st.v, "The Titan is down!") end
  end,
  titan = function(M)
    local st, K = M.state, C.KIT[M.def.foeTribe]
    if st.titan or st.over then return end
    local lit = #S.filter(st.forges, function(f) return S.alive(M, f.b) end)
    local t = S.first(M.spawn(K.titan, "foe", 165, -160, 1, { level = 4 }))
    if not t then return end
    st.titan = t
    M.set_clock(0)
    M.scale_hp(t, 3 + 2 * lit) M.set(t, "scale", 1.8)
    M.send(S.concat({ t }, M.spawn(K.warrior, "foe", 160, -150, C.more(M, 5), { level = 3 })), homePos(M, { -200, 200 }))
    M.objective("titan", "Bring down the Titan", {})
    M.say("warlord", "Rise, my giant. Walk.")
    M.say("scout", "It is moving - the Titan is coming for our village!")
  end,
  tick = function(M)
    local st = M.state
    if st.over then return end
    if C.homeLost(M) then st.over = true M.lose("Your village has fallen.") return end
    if not st.titan and M.time >= st.titanAt and S.alive(M, st.home) then M.call("titan") end
    -- (the Titan keeps walking on the player)
    if S.alive(M, st.titan) and M.info(st.titan).task == "idle" then
      local h = M.home("me")
      if h then local i = M.info(h) M.send({ st.titan }, { i.x, i.z }) end
    end
    if not S.alive(M, st.home) and not M.is_done("fortress") then
      M.complete("fortress")
      M.say("warlord", "My forges... my giant...")
      if not st.titan then M.say("seer", "The fortress has fallen before the giant woke. It will sleep forever now.") end
    end
    if M.is_done("fortress") and not S.alive(M, st.titan) then
      st.over = true
      M.say(st.v, "The Frozen Reach is free.")
      M.after(8, "end")
    end
  end,
  ["end"] = function(M) M.win(M.state.titan and "The Titan has fallen, and the forges are cold." or "The fortress fell before the Titan could walk.") end,
}
