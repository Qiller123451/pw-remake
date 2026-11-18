-- Scenario 1: The Great Hunt (the Valley of Bones). A race: five legendary beasts keep to their lairs; the first tribe
-- to bring down three wins. The rival tribe sends hunting parties after the beasts nearest to it.
-- Ported from the browser remake's game/scenarios/missions.js scenario 1.
local S = require "sequel"
local C = require "scenarios"
local T = S.T
local function alive(M, b) return b.e ~= nil and M.alive(b.e) end

return {
  setup = function(M)
    local st = M.state
    C.startHero(M)
    st.home = C.settle(M, "foe", M.def.foeTribe, 200, -200, { level = 2, hero = true, warriors = 0, archers = 0 })
    local function B(id, name, cls, x, z, o, g)
      return { id = id, name = name, x = x, z = z, e = C.beast(M, cls, x, z, o), g = g and C.guards(M, g, x, z, 35, id) or {} }
    end
    st.beasts = {
      B("tusk", "Old Tusk", "Mammoth", -170, -170, { hp = 4, scale = 1.4, r = 35 }, { { "Woolly_Rhino", C.more(M, 2) } }),
      B("horn", "Threehorn", "Triceratops", -150, 40, { hp = 4, scale = 1.4 }, { { "Triceratops", 1 } }),
      B("fang", "Whitefang", "Smilodon", 40, -150, { hp = 5, scale = 1.5 }, { { "Smilodon", C.more(M, 3) } }),
      B("neck", "Skyneck", "Brachiosaurus", 0, 0, { hp = 3, scale = 1.3, r = 30 }, { { "Stegosaurus", C.more(M, 2) } }),
      B("king", "the King of Teeth", "Tarbosaurus", 170, 170, { hp = 5, scale = 1.5, r = 40 }, { { "Velociraptor", C.more(M, 4) } }),
    }
    for _, b in ipairs(st.beasts) do M.mark(b.x, b.z, b.name, 0xffb040) end
    st.mine = 0 st.theirs = 0
    M.objective("hunt", "Bring down three of the five legendary beasts before the rivals do", { of = 3 })
    M.say("herald", "Hear me, hunters! Five beasts, two tribes, one valley. The " .. C.TRIBE_NAMES[M.tribe] .. " and the " .. C.TRIBE_NAMES[M.def.foeTribe] .. " - the first to bring down three takes it all!")
    M.say(st.v, "Threehorn grazes near our camp. Old Tusk lives in the north-west. Let them have the far ones - if they can.")
    M.say("scout", "The beasts will not come to us. Each keeps to its lair, and the big ones do not go down easily. Bring the whole tribe.")
    M.after(4 * T, "party")
    M.after(150, "note")
  end,
  note = function(M) M.say("elder", "A beast brought down feeds the whole tribe for a season. The hunters who strike the last blow carry the honour.") end,
  -- the rivals' hunting parties: at the living beast nearest to their camp
  party = function(M)
    local st, K = M.state, C.KIT[M.def.foeTribe]
    if st.over then return end
    local left = S.filter(st.beasts, function(b) return alive(M, b) end)
    if #left == 0 then return end
    table.sort(left, function(a, b) return S.dist(a.x, a.z, 200, -200) < S.dist(b.x, b.z, 200, -200) end)
    local t = left[1]
    st.parties = (st.parties or 0) + 1
    local n = st.parties
    local us = M.spawn(K.warrior, "foe", 190, -180, C.more(M, 3 + n), { level = 2 })
    S.concat(us, M.spawn(K.archer, "foe", 200, -175, C.more(M, 2 + math.floor(n / 2)), { level = 2 }))
    if n >= 3 then S.concat(us, M.spawn(K.beast, "foe", 205, -185, n >= 5 and 2 or 1, { level = 2 })) end
    M.send(us, { t.x, t.z })
    M.say("scout", "A " .. C.TRIBE_NAMES[M.def.foeTribe] .. " hunting party is out - heading for " .. t.name .. "!")
    M.mark(t.x, t.z, "hunted", 0xff5030, 30)
    M.after(S.by(M, { 240, 200, 160 }), "party")
  end,
  died = function(M, a)
    local st = M.state
    if not st.beasts or st.over then return end
    local b
    for _, x in ipairs(st.beasts) do if x.e == a.e then b = x end end
    if not b then return end
    M.unmark(b.name)
    if a.by_me then
      st.mine = st.mine + 1
      M.progress("hunt", st.mine)
      for _, r in ipairs({ "food", "wood", "stone" }) do M.add_res("me", r, 250) end
      M.notify(b.name .. " has fallen to your hunters! (+250 of each resource)", "good")
      M.say(st.v, ({ b.name .. " is ours! One trophy for the fire.", b.name .. " - two! One more and the valley is ours.", b.name .. "! Three! Let the herald sing of us." })[math.min(3, st.mine)])
    elseif a.by == "foe" then
      st.theirs = st.theirs + 1
      M.notify("The rivals brought down " .. b.name .. " (" .. st.theirs .. " of 3)", "bad")
      M.say("herald", b.name .. " has fallen to the " .. C.TRIBE_NAMES[M.def.foeTribe] .. "! That is " .. st.theirs .. ".")
    else M.notify(b.name .. " is dead - nobody claims it", "") end
  end,
  tick = function(M)
    local st = M.state
    local left = #S.filter(st.beasts, function(b) return alive(M, b) end)
    M.set_note("hunt", "You " .. st.mine .. " · " .. C.TRIBE_NAMES[M.def.foeTribe] .. " " .. st.theirs .. " · " .. left .. " beasts left")
    if st.over then return end
    if C.homeLost(M) then st.over = true M.lose("Your camp has fallen.") return end
    if st.theirs >= 3 then st.over = true M.lose("The " .. C.TRIBE_NAMES[M.def.foeTribe] .. " brought down three beasts first.") return end
    -- no beasts left for three: it is lost
    if st.mine < 3 and st.mine + left < 3 then st.over = true M.lose("Too few beasts are left to win the Hunt.") return end
    if st.mine >= 3 then
      st.over = true
      M.say("herald", "The Hunt is over! The Valley of Bones belongs to the " .. C.TRIBE_NAMES[M.tribe] .. "!")
      M.after(8, "end")
    end
  end,
  ["end"] = function(M) M.win("The " .. C.TRIBE_NAMES[M.tribe] .. " won the Great Hunt.") end,
}
