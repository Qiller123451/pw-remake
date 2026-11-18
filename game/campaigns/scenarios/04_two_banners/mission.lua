-- Scenario 4: Two Banners (the Riverlands). A choice: two tribes at war send envoys; the hero walks up to the one to
-- side with. That tribe becomes the ally, the other the enemy - whose main building has to fall. Ignore both and both
-- turn on you. Ported from the browser remake's game/scenarios/missions.js scenario 4.
local S = require "sequel"
local C = require "scenarios"

local function tribeOf(M, r) return r == "foe" and M.def.foeTribe or M.def.foe2Tribe end
local function pos(M, id) local i = M.info(id) return { i.x, i.z } end

return {
  setup = function(M)
    local st = M.state
    C.startHero(M)
    st.homes = { foe = C.settle(M, "foe", M.def.foeTribe, -190, -170, { level = 2, warriors = 0, archers = 0 }), foe2 = C.settle(M, "foe2", M.def.foe2Tribe, 190, -170, { level = 2, warriors = 0, archers = 0 }) }
    st.envoys = {}
    -- the two tribes at war with each other
    M.relation("foe", "foe2", 0)
    M.objective("hear", "Meet the envoys of both tribes at your village", {})
    M.say("elder", "The " .. C.TRIBE_NAMES[M.def.foeTribe] .. " in the west, the " .. C.TRIBE_NAMES[M.def.foe2Tribe] .. " in the east. They have burned each other's fields all summer. Now they both remember we exist.")
    M.say(st.v, "Then we listen. And we choose.")
    M.after(90, "envoy", { r = "foe", x = -60, z = 140, fx = -150, fz = -120 })
    M.after(120, "envoy", { r = "foe2", x = 60, z = 140, fx = 150, fz = -120 })
  end,
  envoy = function(M, a)
    local st, r = M.state, a.r
    local tribe = tribeOf(M, r)
    local K = C.KIT[tribe]
    local e = S.first(M.spawn(K.hero, r, a.fx, a.fz, 1, { level = 3 }))
    local esc = M.spawn(K.warrior, r, a.fx + 6, a.fz - 6, 3, { level = 2 })
    if not e then return end
    M.set(e, "envoy", true)
    M.move({ e }, a.x, a.z)
    for _, u in ipairs(esc) do M.set(u, "envoy", true) M.move({ u }, a.x + (math.random() - 0.5) * 10, a.z - 8) end
    st.envoys[r] = e
    -- (their tribe's computer player leaves them alone: the campaigns' AILU)
    M.ai_lock(r, S.concat({ e }, esc), true)
    M.mark(a.x, a.z, "envoy:" .. r, r == "foe" and 0xff6060 or 0xb080ff)
    M.say("scout", "An envoy of the " .. C.TRIBE_NAMES[tribe] .. " is on the road to the village.")
  end,
  -- what each envoy says when it arrives
  arrived = function(M, a)
    local st, r = M.state, a.r
    local tribe, enemy = tribeOf(M, r), tribeOf(M, r == "foe" and "foe2" or "foe")
    local who = r == "foe" and "envoyA" or "envoyB"
    M.say(who, r == "foe"
      and ("Greetings from the " .. C.TRIBE_NAMES[tribe] .. ". The " .. C.TRIBE_NAMES[enemy] .. " are thieves and liars. Stand with us, and the river is yours when they are gone.")
      or ("The " .. C.TRIBE_NAMES[tribe] .. " send their respect. The " .. C.TRIBE_NAMES[enemy] .. " want your land next - they always do. Join us before they come for it."))
    st.met = (st.met or 0) + 1
    if st.met == 2 then
      M.complete("hear")
      st.deadline = M.time + S.by(M, { 240, 180, 120 })
      M.set_clock(st.deadline, "Choose within")
      M.objective("choose", "Choose a side: walk your hero up to the envoy you will stand with", { secondary = true })
      M.say(st.v, "Both of them want our spears. Whoever we choose, the other becomes our enemy.")
    end
  end,
  choose = function(M, a)
    local st, r = M.state, a.r
    if st.chosen then return end
    st.chosen = r
    M.set_clock(0)
    local other = r == "foe" and "foe2" or "foe"
    M.relation(r, "me", 2) M.relation(other, "me", 0)
    st.allyHome = st.homes[r] st.enemyHome = st.homes[other]
    local at, et = tribeOf(M, r), tribeOf(M, other)
    M.complete("choose")
    M.unmark("envoy:foe") M.unmark("envoy:foe2")
    M.say(r == "foe" and "envoyA" or "envoyB", "The " .. C.TRIBE_NAMES[at] .. " will not forget this. Our warriors march with yours!")
    M.say(other == "foe" and "envoyA" or "envoyB", "So be it. You will burn with them.")
    -- the spurned envoy and his escort turn on the village
    local spurned = M.units(other, { envoy = true })
    M.set(spurned, "envoy", false) M.send(spurned, { 0, 200 })
    M.set(M.units(r, { envoy = true }), "envoy", false)
    for _, k in ipairs({ "foe", "foe2" }) do M.ai_lock(k, M.units(k), false) end
    M.objective("war", "Destroy the " .. C.TRIBE_NAMES[et] .. "' main building", {})
    M.objective("ally", "Keep your allies, the " .. C.TRIBE_NAMES[at] .. ", in the war (their main building standing)", { secondary = true })
    if S.alive(M, st.enemyHome) then local p = pos(M, st.enemyHome) M.mark(p[1], p[2], "enemy", 0xff5030) end
    M.after(60, "raid")
  end,
  -- nobody chosen in time: both tribes take it as an insult
  silence = function(M)
    local st = M.state
    st.chosen = "none"
    M.set_clock(0)
    for _, r in ipairs({ "foe", "foe2" }) do M.relation(r, "me", 0) end
    M.fail("choose")
    M.say("envoyA", "Your silence is an answer.") M.say("envoyB", "Then you are nobody's friend.")
    M.say(st.v, "Both of them. Well - we did not want their land anyway. Now we will take it.")
    M.unmark("envoy:foe") M.unmark("envoy:foe2")
    for _, r in ipairs({ "foe", "foe2" }) do
      local us = M.units(r, { envoy = true })
      M.set(us, "envoy", false) M.ai_lock(r, us, false) M.send(us, { 0, 200 })
    end
    M.objective("war", "Destroy the main buildings of both tribes", { of = 2 })
    for _, r in ipairs({ "foe", "foe2" }) do if S.alive(M, st.homes[r]) then local p = pos(M, st.homes[r]) M.mark(p[1], p[2], "enemy:" .. r, 0xff5030) end end
    M.after(60, "raid")
  end,
  -- the enemy's war parties at the village
  raid = function(M)
    local st = M.state
    if st.over then return end
    local foes = st.chosen == "none" and { "foe", "foe2" } or { st.chosen == "foe" and "foe2" or "foe" }
    local function part(a, k) local o = {} for i = 1, math.ceil(#a * k) do o[i] = a[i] end return o end
    for _, r in ipairs(foes) do M.send(part(C.army(M, r), 0.6), { 0, 200 }) end
    if st.chosen and st.chosen ~= "none" then
      M.send(part(C.army(M, st.chosen), 0.5), S.alive(M, st.enemyHome) and pos(M, st.enemyHome) or { 0, 0 })
    end
    M.say("scout", "War parties on the move!")
    M.after(S.by(M, { 200, 170, 140 }), "raid")
  end,
  tick = function(M)
    local st = M.state
    if st.over then return end
    if C.homeLost(M) then st.over = true M.lose("Your village has fallen.") return end
    -- the envoys at the village; the hero walking up to one
    for _, r in ipairs({ "foe", "foe2" }) do
      local e = st.envoys[r]
      if e then
        if not M.alive(e) and not st.chosen then st.envoys[r] = nil M.notify("An envoy was killed!", "bad") M.call("silence") return end
        local ex = r == "foe" and -60 or 60
        local ei = M.alive(e) and M.info(e)
        if not st["at_" .. r] and ei and S.near(ei, ex, 140, 12) then st["at_" .. r] = true M.call("arrived", { r = r }) end
        if not st.chosen and ei and ((ei.task == "idle" and not S.near(ei, ex, 140, 6)) or (ei.task ~= "idle" and not ei.task:find("move"))) then M.move({ e }, ex, 140) end
        local h = C.hero(M)
        if st.met == 2 and not st.chosen and h and ei and S.near(M.info(h), ei.x, ei.z, 9) then M.call("choose", { r = r }) end
      end
    end
    if st.deadline and not st.chosen and M.time >= st.deadline then M.call("silence") end
    if not st.chosen then return end
    -- the war
    if st.chosen == "none" then
      local down = #S.filter({ "foe", "foe2" }, function(r) return not S.alive(M, st.homes[r]) end)
      M.progress("war", down)
      if down == 2 then st.over = true M.after(8, "end") end
      return
    end
    if st.allyHome and not M.alive(st.allyHome) then M.fail("ally") end
    if not S.alive(M, st.enemyHome) then
      st.over = true
      M.complete("war")
      local ally = M.get_objective("ally")
      if ally and not ally.failed then M.complete("ally") end
      M.say(st.chosen == "foe" and "envoyA" or "envoyB", "The war is over. The river is yours, as we promised.")
      M.after(8, "end")
    end
  end,
  ["end"] = function(M) M.win(M.state.chosen == "none" and "You stood alone - and beat them both." or "The Riverlands are at peace, and your banner flies over them.") end,
}
