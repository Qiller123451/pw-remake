-- Containment 16: Open Door (Copper Basin, Nevada, August 6, 1966). The finale at the Door: Tala's caravans to the
-- Dustriders' camp, Crane's three relay masts (they open the Door to his creatures and shield his Behemoth), and
-- Crane himself running for the Door. Ported from game/sequel/missions_1966.js mission 16.
local S = require "sequel"
local T = S.T
local function more(M, n, k) return S.more(M, n, k, 1) end

local function mastsUp(M) return S.any(M.state.masts, function(m) return M.alive(m) end) end

return {
  setup = function(M)
    local st = M.state
    st.door = M.building("sq_gate_machine", "me", 0, 0, 0)
    M.spawn("aje_warrior", "aje", -50, 150, 4, { level = 2 })
    st.masts = {}
    for _, p in ipairs({ { 190, -170 }, { -200, -180 }, { 200, 120 } }) do
      local m = M.building("sq_relay_mast", "crane", p[1], p[2], 0)
      if m then
        st.masts[#st.masts + 1] = m
        local i = M.info(m)
        S.post(M, { { "seas_gunner", more(M, 3), 3 }, { "seas_rocketman", 1, 2 } }, i.x - 10, i.z + 10, 30)
        M.building("seas_turret_tower", "crane", i.x + 12, i.z + 12, 0)
        M.mark(i.x, i.z, "mast", 0xff5030)
      end
    end
    st.caravans = 0 st.arrived = 0 st.need = S.by(M, { 6, 8, 10 })
    M.objective("door", "Keep the Door standing", {})
    M.objective("caravans", "Bring Tala's caravans from the Door to the Dustriders' camp (" .. st.need .. " riders)", { of = st.need, secondary = true })
    M.objective("masts", "Bring down Crane's three relay masts on the mesas", { of = 3 })
    M.mark(-60, 160, "camp", 0x7dff6a)
    M.say("radio", "Copper Basin, this is Washington. The President is on the line. The Door may open when ready.")
    M.say("taslov", "Opening... now. Ladies and gentlemen - the other world.")
    M.say("tala", "My people are coming through. Walk them to the camp, Colonel. They have never seen a road.")
    M.after(20, "caravan") M.after(150, "hijack") M.after(4 * T, "column") M.after(8 * T, "behemoth") M.after(11 * T, "column")
  end,
  caravan = function(M)
    local st = M.state
    if st.caravans >= 4 or st.over then return end
    st.caravans = st.caravans + 1
    -- (the civilians' side: they follow the first unit of the player's that reaches them to the camp)
    local us = M.spawn("aje_warrior", "civilians", 0, 22, 3, { level = 2 })
    S.concat(us, M.spawn("aje_archer", "civilians", 4, 24, 1, { level = 2 }))
    M.set(us, "tag", "caravan")
    M.notify("A caravan has come through the Door (" .. st.caravans .. " of 4)", "good")
    M.after(140, "caravan")
  end,
  evacuated = function(M, a)
    M.state.arrived = a.n
    M.progress("caravans", a.n)
    if a.n == 4 then M.say("tala", "They are in the camp - and staring at your trucks, Booker.") end
  end,
  -- Crane's masts take over the Door's circuit: it spits his creatures
  hijack = function(M)
    local st = M.state
    if not mastsUp(M) then return end
    M.rift("door", 0, -26, { every = S.by(M, { 120, 100, 85 }), first = 15, target = { -130, 140 }, cap = 14, waves = {
      { { "Velociraptor", 4 } }, { { "Deinonychus", 3 }, { "Velociraptor", 2 } }, { { "Allosaurus", 1 }, { "Deinonychus", 3 } }, { { "Carnotaurus", 1 }, { "Smilodon", 2 } },
      { { "Atroxosaurus", 1 }, { "Velociraptor", 4 } }, { { "Spinosaurus", 1 }, { "Allosaurus", 1 } } } })
    st.wild = true
    M.remove_rift_building("door")                -- (no second ring: it comes out of the Door itself)
    M.say("taslov", "No - no, no! The Door is drawing current from outside. Something is pushing it wide open!")
    M.say("crane", "Good afternoon, Margaret. Lev's Door is a lovely piece of work. I have simply added a few improvements.")
    M.say("taslov", "The masts on the mesas - he is feeding the Door through them. Bring them down and it is mine again.")
  end,
  column = function(M)
    local t = { 0, 0 }
    M.send(M.spawn("seas_gunner", "crane", 230, -60, more(M, 5), { level = 3 }), t) M.send(M.spawn("seas_mobile_suit", "crane", 235, -40, more(M, 1), { level = 2 }), t)
    M.send(M.spawn("seas_rocketman", "crane", -230, 30, more(M, 4), { level = 3 }), t)
    M.say("kiko", "Columns from east and west, Colonel - Crane's men, going for the Door.")
  end,
  behemoth = function(M)
    local st = M.state
    st.boss = S.first(M.spawn("sq_behemoth", "crane", 10, -245, 1))
    if not st.boss then return end
    M.send({ st.boss }, { 0, 0 })
    M.set(st.boss, "scale", 1.6)
    if mastsUp(M) then M.invulnerable(st.boss, true) end
    M.send(M.spawn("seas_gunner", "crane", 0, -240, more(M, 4), { level = 3 }), { 0, 0 })
    M.objective("behemoth", "Break the Behemoth before it reaches the Door", {})
    M.mark(10, -245, "Behemoth", 0xff5030, 20)
    M.say("kiko", "Colonel... it's a walker. A walker the size of a church, coming up the south road!")
    M.say("crane", "Meet the Behemoth. The Door will be mine, Margaret - and so will everything beyond it.")
    if mastsUp(M) then M.say("taslov", "There is a field round it - the same current from the masts! While one mast stands, nothing will scratch it.") end
  end,
  died = function(M, a)
    local st = M.state
    if S.has(st.masts, a.e) then
      local n = #S.filter(st.masts, function(m) return not M.alive(m) end)
      M.progress("masts", n)
      M.say("taslov", ({ "One mast down! The Door's current is dropping.", "Two! One more, Colonel!", "The last one! The Door is mine again!" })[n])
      if n == 3 then
        if st.wild then M.close_rift("door") end
        if S.alive(M, st.boss) then M.invulnerable(st.boss, false) M.say("booker", "And that big ugly thing just lost its shield. Everybody - shoot the legs!") end
      end
    end
    if st.boss and a.e == st.boss then M.call("eject") end
    if st.crane and a.e == st.crane and not st.over then
      st.over = true
      M.complete("crane")
      M.say("halloran", "Augustus Crane is dead. It didn't have to end like that.")
      M.say("tala", "For him, it did.")
      M.after(10, "end")
    end
  end,
  -- Crane climbs out of the wreck and runs for the Door
  eject = function(M)
    local st = M.state
    M.complete("behemoth")
    local b = st.boss and M.info(st.boss)
    local x, z = b and b.x or 0, b and b.z or -100
    st.crane = S.first(M.spawn("sq_crane", "crane", x + 6, z, 1, { level = 3 }))
    if not st.crane then st.over = true M.after(10, "end") return end
    M.set_hp(st.crane, M.info(st.crane).maxhp * 0.5)
    M.move({ st.crane }, 0, -10)
    M.objective("crane", "Crane is running for the Door: catch him - get a hero to him (Halloran, Booker or Tala)", {})
    M.say("kiko", "Somebody just climbed out of the wreck - it's Crane! He's running for the Door!")
    M.say("halloran", "He wants to disappear on the other side. Not today.")
  end,
  tick = function(M)
    local st = M.state
    if not S.alive(M, st.door) then M.fail("door") return end
    if not S.hero_alive(M, "sq_halloran") and M.time > 5 then M.lose("Colonel Halloran has fallen.") return end
    -- the caravans: lost when too few are left to reach the camp
    if not M.is_done("caravans") and st.caravans >= 4 then
      local out = #M.units("civilians", { tag = "caravan" })
      if st.arrived + out < st.need then M.fail("caravans") end
    end
    -- the Behemoth at the Door
    if S.alive(M, st.boss) and M.info(st.boss).task == "idle" then M.send({ st.boss }, { 0, 0 }) end
    -- Crane on the run
    if S.alive(M, st.crane) and not st.over then
      local c = M.info(st.crane)
      if c.task == "idle" then M.move({ st.crane }, 0, -10) end
      local hero = S.first(M.units("me", function(u)
        return (u.name == "sq_halloran" or u.name == "sq_booker" or u.name == "sq_tala") and S.near(u, c.x, c.z, 8)
      end))
      if hero then
        st.over = true
        M.remove(st.crane)
        M.complete("crane")
        M.say("crane", "Margaret. I suppose you will want me to say something gracious.")
        M.say("halloran", "I want you to say nothing at all, all the way to Washington. Augustus Crane, you are under arrest.")
        M.after(10, "end")
      elseif S.near(c, 0, -10, 8) then
        st.over = true
        M.remove(st.crane)
        M.say("taslov", "He is through! He went through the Door!")
        M.lose("Crane escaped through the Door.")
      end
    end
  end,
  ["end"] = function(M)
    local st = M.state
    M.kill(M.units("outbreak"))
    if st.wild and M.rift_open("door") then M.close_rift("door") end
    M.complete("door")
    M.say("taslov", "The Door is steady. Open - and ours to close.")
    M.say("tala", "Then leave it open a while, Lev. My people have come a long way to see your roads.")
    M.say("halloran", "Washington, Copper Basin. The Door is open. Containment... has a new meaning.")
    M.win("The Door stands open, and Augustus Crane will never close it.")
  end,
}
