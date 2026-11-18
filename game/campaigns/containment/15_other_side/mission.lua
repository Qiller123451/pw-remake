-- Containment 15: The Other Side (the Valley of Smoke, ParaWorld, July 30, 1966). Through the Door into Tala's valley:
-- keep the Dustriders' village standing, break Crane's engine, open his cages (the riders join the player), burn the
-- camp. Ported from game/sequel/missions_1966.js mission 15.
local S = require "sequel"
local T = S.T
local function more(M, n, k) return S.more(M, n, k, 1) end

return {
  setup = function(M)
    local st, a, c = M.state, "aje", "crane"
    M.rift("door", -215, 215, { every = 1e9, first = 1e9 })          -- (Taslov's Door: nothing comes out of it)
    st.heart = S.first(M.spawn("aje_resource_collector", a, -60, 60, 1))
    M.building("aje_rodeo", a, -95, 50, 0) M.building("aje_tent", a, -30, 80, 0) M.building("aje_tent", a, -70, 95, 0) M.building("aje_tent", a, -30, 35, 0)
    M.spawn("aje_warrior", a, -50, 40, 6, { level = 3 }) M.spawn("aje_archer", a, -70, 70, 4, { level = 2 })
    st.hq = M.building("seas_headquarters", c, 175, -180, math.pi)
    M.building("seas_barracks", c, 140, -185, math.pi) M.building("seas_turret_tower", c, 140, -150, 0) M.building("seas_turret_tower", c, 200, -140, 0)
    st.engine = M.building("sq_rift_engine", c, 125, -125, 0)
    M.spawn("seas_gunner", c, 150, -160, 6, { level = 3 }) M.spawn("seas_wehrspinne", c, 170, -150, 1)
    -- the cages of Tala's riders, each watched
    st.cages = {}
    for _, p in ipairs({ { -40, -150 }, { 60, 110 }, { 190, 40 } }) do
      local k = { x = p[1], z = p[2], b = M.building("seas_jail_part_01", c, p[1], p[2], 0) }
      st.cages[#st.cages + 1] = k
      if k.b then M.invulnerable(k.b, true) M.set(k.b, "untargetable", true) end
      S.post(M, { { "seas_gunner", more(M, 3), 2 }, { "seas_marksman", 1, 2 } }, k.x + 10, k.z + 6, 28)
      M.building("seas_turret_tower", c, k.x - 12, k.z - 10, 0)
      M.mark(k.x, k.z, "cage", 0xffd040)
    end
    M.objective("village", "Keep the Dustriders' village standing (their great beast lives)", {})
    M.objective("engine", "Break Crane's engine - it brings his men across", {})
    M.objective("cages", "Open Crane's cages: Tala's riders will fight for you", { of = 3, secondary = true })
    M.objective("camp", "Burn Crane's camp (his headquarters, south-east)", {})
    M.say("tala", "Home. It smells of smoke - that is not our smoke.")
    M.say("taslov", "There, past the hill: the hum of an engine. Crane brought his own door. Every few minutes it will bring more of his men.")
    M.say("tala", "And my riders are in his cages. Colonel, I will not ask twice.")
    M.say("halloran", "You don't have to ask once.")
    M.after(4 * T, "wave")
    M.after(10 * T, "king")
  end,
  -- Crane's men through his engine, at the village
  wave = function(M)
    local st = M.state
    if not S.alive(M, st.engine) then return end
    local t = { -60, 60 }
    if S.alive(M, st.heart) then local h = M.info(st.heart) t = { h.x, h.z } end
    M.send(M.spawn("seas_gunner", "crane", 125, -105, more(M, 4), { level = 3 }), t)
    M.send(M.spawn("seas_rocketman", "crane", 130, -100, more(M, 1), { level = 2 }), t)
    st.waves = (st.waves or 0) + 1
    if st.waves % 3 == 0 then M.send(M.spawn("seas_mobile_suit", "crane", 120, -100, 1, { level = 2 }), t) end
    M.mark(125, -125, "wave", 0xff5a3a, 8)
    if st.waves == 1 then M.say("kiko", "Kiko - radio relay's up on our side. A column just walked out of Crane's engine, heading for the village!") end
    M.after(S.by(M, { 200, 170, 140 }), "wave")
  end,
  king = function(M)
    M.say("tala", "Listen. The ground. The King of Teeth smells the fighting - he will come for whatever is weakest.")
    local h = S.first(M.units("me", "sq_halloran"))
    local t = { -60, 60 }
    if h then local i = M.info(h) t = { i.x, i.z } end
    local k = S.first(M.spawn("Tarbosaurus", "outbreak", 0, 240, 1, { target = t }))
    if k then M.set(k, "scale", 1.4) end
  end,
  died = function(M, a)
    local st = M.state
    if a.e == st.engine then
      M.complete("engine")
      M.say("taslov", "The engine is down! No more of them can come across.")
      M.say("crane", "A pity. It was rather expensive.")
    end
    for _, k in ipairs(st.cages) do
      if k.b == a.e and not k.open then k.lost = true M.notify("A cage was destroyed - the riders in it are lost", "bad") end
    end
  end,
  tick = function(M)
    local st = M.state
    if not S.hero_alive(M, "sq_halloran") and M.time > 5 then M.lose("Colonel Halloran has fallen.") return end
    if not S.alive(M, st.heart) then M.fail("village") return end
    -- a cage opens when the player stands at it and none of Crane's men are left by it
    local lines = { "My riders! Take up your spears - you fight with the sky-people now.", "More of them! Crane will pay for every chain.", "All of my people are free. Now we finish this." }
    for _, k in ipairs(st.cages) do
      -- (16 m from the cage - or from its walls, as in mission 8)
      local reach = S.alive(M, k.b) and math.max(16, M.info(k.b).radius + 4) or 16
      if not k.open and not k.lost and S.alive(M, k.b) and #M.near(k.x, k.z, reach, "me") > 0 and #M.near(k.x, k.z, reach + 8, "crane") == 0 then
        k.open = true
        M.remove(k.b)
        M.unmark("cage")
        for _, q in ipairs(st.cages) do if not q.open and not q.lost then M.mark(q.x, q.z, "cage", 0xffd040) end end
        M.spawn("aje_warrior", "me", k.x, k.z + 6, 3, { level = 3 }) M.spawn("aje_archer", "me", k.x + 6, k.z + 6, 2, { level = 3 })
        local n = #S.filter(st.cages, function(q) return q.open end)
        M.progress("cages", n)
        M.say("tala", lines[n])
      end
    end
    if not S.any(st.cages, function(q) return not (q.open or q.lost) end) and not M.is_done("cages") then M.fail("cages") end
    if not S.alive(M, st.hq) and not M.is_done("camp") then
      M.complete("camp")
      M.say("kiko", "His camp is burning. No sign of Crane - his walker went into the jungle, east, before the roof came down.")
    end
    if M.is_done("camp") and M.is_done("engine") and not st.won then
      st.won = true
      M.complete("village")
      M.say("tala", "My valley is ours again. But a man like that does not run without a reason.")
      M.say("taslov", "Colonel - the Door's instruments. Somebody on our side is drawing current from it. From Copper Basin.")
      M.say("halloran", "He doubled back. Everybody home. Now.")
      M.after(10, "end")
    end
  end,
  ["end"] = function(M) M.win("The Valley of Smoke is free. Crane has gone back through - to the Door.") end,
}
