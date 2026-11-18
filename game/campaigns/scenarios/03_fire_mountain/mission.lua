-- Scenario 3: Fire Mountain (the Ash Valley). King of the hill with three tribes: whoever stands alone on the
-- volcano's summit gains time; the first to five minutes wins. The mountain erupts now and then, marked a few seconds
-- before the rocks come down. Ported from the browser remake's game/scenarios/missions.js scenario 3.
local S = require "sequel"
local C = require "scenarios"
local T = S.T

local function tribeOf(M, r) return C.TRIBE_NAMES[r == "foe" and M.def.foeTribe or M.def.foe2Tribe] end

return {
  setup = function(M)
    local st = M.state
    C.startHero(M)
    st.homes = { foe = C.settle(M, "foe", M.def.foeTribe, -185, -110, { level = 2, warriors = 0, archers = 0 }), foe2 = C.settle(M, "foe2", M.def.foe2Tribe, 185, -110, { level = 2, warriors = 0, archers = 0 }) }
    st.held = { me = 0, foe = 0, foe2 = 0 }
    st.need = 5 * T
    M.add_zone({ name = "summit", x = 0, z = 0, r = 24 })
    M.mark(0, 0, "summit", 0xffd040)
    M.objective("summit", "Hold the summit alone for 5:00 in all - first to it wins", {})
    M.say("seer", "The mountain speaks in five turns of the sand. Whoever stands on its crown alone, when the sand runs out, is chosen.")
    M.say(st.v, "The " .. C.TRIBE_NAMES[M.def.foeTribe] .. " from the west, the " .. C.TRIBE_NAMES[M.def.foe2Tribe] .. " from the east. They will fight each other as gladly as us.")
    M.after(50, "rumble")
    M.after(150, "push")
  end,
  -- the rivals send their fighters up the mountain
  push = function(M)
    local st = M.state
    if st.over then return end
    for _, r in ipairs({ "foe", "foe2" }) do
      local a = C.army(M, r)
      local part = {}
      for i = 1, math.ceil(#a * 0.7) do part[i] = a[i] end
      M.send(part, { (math.random() - 0.5) * 16, (math.random() - 0.5) * 16 })
    end
    if not st.pushSaid then st.pushSaid = true M.say("scout", "Both of them are marching on the summit!") end
    M.after(S.by(M, { 150, 120, 95 }), "push")
  end,
  -- the mountain: a rumble, the marked ground, the rocks
  rumble = function(M)
    local st = M.state
    if st.over then return end
    st.rumbles = (st.rumbles or 0) + 1
    local big = st.rumbles % 4 == 0
    local n = big and 9 or 5
    local spots = {}
    for i = 1, n do
      local a = math.random() * math.pi * 2
      local r = (big and 0 or 8) + math.random() * (big and 30 or 70)
      spots[i] = { math.cos(a) * r, math.sin(a) * r }
    end
    for i, p in ipairs(spots) do M.mark(p[1], p[2], "fire:" .. (i - 1), 0xff3010, 5) end
    M.notify(big and "The summit itself is erupting - get off the crown!" or "The mountain rumbles - fire is coming down!", "bad")
    if not st.fireSaid then st.fireSaid = true M.say("seer", "The mountain shows where it will strike. Watch the red marks on the map, and step aside.") end
    M.after(4, "fall", { spots = spots })
    M.after(big and 60 or 40 + math.random() * 25, "rumble")
  end,
  fall = function(M, a) for _, p in ipairs(a.spots) do C.strike(M, p[1], p[2]) end end,
  tick = function(M)
    local st = M.state
    if st.over then return end
    if C.homeLost(M) then st.over = true M.lose("Your camp has fallen.") return end
    -- who stands on the crown: the sides with units there (not the dead and the wild)
    local on = {}
    for _, r in ipairs({ "me", "foe", "foe2" }) do if #M.in_zone("summit", r, { worker = false }) > 0 then on[#on + 1] = r end end
    if #on == 1 then
      local r = on[1]
      st.held[r] = st.held[r] + 0.5
      if r ~= st.holder then
        st.holder = r
        M.notify(r == "me" and "You hold the summit!" or ("The " .. tribeOf(M, r) .. " hold the summit!"), r == "me" and "good" or "bad")
      end
    else st.holder = #on > 0 and "contested" or nil end
    M.set_note("summit", "You " .. S.fmt(st.held.me) .. " · " .. C.TRIBE_NAMES[M.def.foeTribe] .. " " .. S.fmt(st.held.foe) .. " · " .. C.TRIBE_NAMES[M.def.foe2Tribe] .. " " .. S.fmt(st.held.foe2) .. (st.holder == "contested" and " · contested" or ""))
    M.set_clock(st.holder == "me" and M.time + (st.need - st.held.me) or 0, "Chosen in")
    for _, r in ipairs({ "foe", "foe2" }) do
      if st.held[r] >= st.need then st.over = true M.lose("The mountain chose the " .. tribeOf(M, r) .. ".") return end
    end
    if st.held.me >= st.need then
      st.over = true
      M.complete("summit")
      M.say("seer", "The sand has run out. The mountain has chosen the " .. C.TRIBE_NAMES[M.tribe] .. "!")
      M.after(8, "end")
    end
  end,
  ["end"] = function(M) M.win("The fire mountain has chosen your tribe.") end,
}
