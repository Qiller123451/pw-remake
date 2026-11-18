-- Containment 13: The Long Haul (Raton Pass, New Mexico, May 16, 1966). A convoy at night: five trucks with the parts
-- of Taslov's Door drive the pass by themselves and stop whenever something is near them (or no escort is). Ambushes
-- along the road. Ported from game/sequel/missions_1966.js mission 13.
local S = require "sequel"
local function more(M, n, k) return S.more(M, n, k, 1) end

return {
  setup = function(M)
    local st = M.state
    st.route = { { -250, 200 }, { -150, 205 }, { -40, 195 }, { 80, 205 }, { 200, 195 }, { 235, 140 }, { 200, 70 }, { 80, 65 }, { -40, 75 }, { -160, 65 }, { -235, 10 },
      { -200, -60 }, { -80, -55 }, { 40, -65 }, { 160, -55 }, { 230, -110 }, { 240, -205 } }
    st.need = S.by(M, { 3, 3, 4 })
    -- the trucks in a column on the first stretch (the front one first); per truck: the next waypoint (1-based index
    -- into route, 2 = the route's second point), the waypoint it was ordered to, where it stood when
    local ax, az, bx, bz = st.route[1][1], st.route[1][2], st.route[2][1], st.route[2][2]
    local L = S.dist(ax, az, bx, bz)
    local dx, dz = (bx - ax) / L, (bz - az) / L
    st.trucks, st.wp, st.ord, st.at = {}, {}, {}, {}
    for i = 0, 4 do
      local d = 60 - i * 13
      local t = S.first(M.spawn("sq_cargo_truck", "me", ax + dx * d, az + dz * d, 1, { heading = math.atan2(-dz, dx) }))
      if t then st.wp[t] = 2 st.ord[t] = -1 st.trucks[#st.trucks + 1] = t end
    end
    st.delivered = 0 st.lead = 2
    -- Crane's roadblock at the cut: two towers, men behind sandbags
    st.block = {}
    for _, p in ipairs({ { 0, 52 }, { 10, 90 } }) do local b = M.building("seas_turret_tower", "crane", p[1], p[2], 0) if b then st.block[#st.block + 1] = b end end
    st.blockMen = S.post(M, { { "seas_gunner", more(M, 4), 3 }, { "seas_rocketman", more(M, 2), 2 } }, 6, 70, 30)
    M.objective("convoy", "Get the trucks to the depot over the pass (at least " .. st.need .. " of 5)", { of = st.need })
    M.mark(240, -205, "depot", 0x7dff6a)
    M.grant(6)
    M.say("radio", "Convoy, you are cleared to roll. Sixty miles of mountain and no lights after the first bend.")
    M.say("halloran", "Trucks keep to the road and stop if anything gets close. So nothing gets close. Jeeps up front, flak in the middle, everybody else on the flanks.")
    M.say("booker", "Funny thing about mountains at night, Colonel. Sound carries. Whatever's out there already knows we're coming.")
  end,
  -- the ambushes, by how far the front truck has come (wp: the browser remake's 0-based waypoint)
  leg = function(M, a)
    local st, wp = M.state, a.wp
    local alive = S.living(M, st.trucks)
    local at = st.route[wp + 1]
    if alive[1] then local i = M.info(alive[1]) at = { i.x, i.z } end
    local tail = at
    if #alive > 0 then local i = M.info(alive[#alive]) tail = { i.x, i.z } end
    if wp == 3 then
      M.spawn("Velociraptor", "outbreak", -20, 140, more(M, 4), { target = at })
      M.say("kiko", "Movement on the ridge above you! They're coming down for the trucks!")
    elseif wp == 5 then
      M.spawn("Deinonychus", "outbreak", 270, 250, more(M, 3), { target = at })
      M.spawn("Velociraptor", "outbreak", 280, 120, more(M, 2), { target = at })
      M.say("booker", "The bend's a perfect ambush spot. Which is why there's an ambush in it.")
    elseif wp == 7 then
      M.say("kiko", "Lights on the second leg - sandbags and two towers across the road. That's no Army roadblock.")
      M.say("crane", "Margaret. Leave the trucks and walk home. I would so hate to see your people hurt.")
      M.say("halloran", "Clear the road. The trucks wait until those towers are down.")
    elseif wp == 10 then
      M.spawn("Smilodon", "outbreak", 60, 110, more(M, 3), { target = tail })
      M.say("booker", "Cats in the trees behind us - they're after the tail of the column!")
    elseif wp == 11 then
      M.say("kiko", "Walkers behind you, coming down the road fast - they want the last truck! And something big ahead.")
      M.send(M.spawn("seas_mobile_suit", "crane", -40, 75, more(M, 2), { level = 2 }), st.route[11])
      M.send(M.spawn("seas_gunner", "crane", -30, 80, more(M, 3), { level = 3 }), st.route[11])
      M.spawn("Carnotaurus", "outbreak", -150, -170, 1, { target = { -200, -60 } })
      if M.difficulty > 0 then M.spawn("Allosaurus", "outbreak", -60, -170, 1, { target = { -80, -55 } }) end
    elseif wp == 13 then
      M.set_night(false)
      M.say("halloran", "Sun's coming up. Last leg - and then downhill.")
      M.send(M.spawn("seas_rocketman", "crane", 40, -10, more(M, 3), { level = 3 }), { 40, -65 })
      M.say("kiko", "Rocket teams up on the ridge! They'll have a clear shot at the road!")
    elseif wp == 15 then
      M.spawn("Velociraptor", "outbreak", 150, -265, more(M, 4), { target = { 230, -110 } })
      M.say("booker", "And one last welcome committee by the depot, by the look of it.")
    end
  end,
  died = function(M, a)
    if a.name == "sq_cargo_truck" and a.role == "me" then
      local left = #S.living(M, M.state.trucks)
      M.notify("A convoy truck was destroyed (" .. left .. " left)", "bad")
      if not M.state.lostSaid then M.state.lostSaid = true M.say("radio", "We lost a truck! Taslov says he can build around one missing part. Not two.") end
    end
  end,
  tick = function(M)
    local st, R = M.state, M.state.route
    if not S.hero_alive(M, "sq_halloran") and M.time > 5 then M.lose("Colonel Halloran has fallen.") return end
    -- into the depot
    for _, t in ipairs(S.living(M, st.trucks)) do
      if S.near(M.info(t), 240, -205, 20) then
        M.remove(t)
        st.delivered = st.delivered + 1
        M.notify("Truck in the depot (" .. st.delivered .. ")", "good")
        M.progress("convoy", st.delivered)
      end
    end
    local moving = S.living(M, st.trucks)
    if st.delivered + #moving < st.need and not st.won then M.lose("Too many trucks were lost.") return end
    -- what stops the convoy: creatures or Crane's men within 30 m of a truck, a tower of his within 50 m
    local foes = {}
    for _, role in ipairs({ "outbreak", "crane" }) do
      for _, id in ipairs(M.units(role)) do local u = M.info(id) if not u.untargetable and not u.parked then foes[#foes + 1] = u end end
    end
    local towers = {}
    for _, id in ipairs(M.buildings("crane")) do towers[#towers + 1] = M.info(id) end
    local tinfo = {}
    for _, t in ipairs(moving) do tinfo[t] = M.info(t) end
    local danger = S.any(moving, function(t)
      local ti = tinfo[t]
      return S.any(foes, function(f) return S.near(f, ti.x, ti.z, 30) end) or S.any(towers, function(b) return S.near(b, ti.x, ti.z, 50) end)
    end)
    -- ... and no escort: the trucks do not drive on without fighting units within 45 m of one of them
    local escort = #moving > 0 and #M.units("me", function(u)
      return u.name ~= "sq_cargo_truck" and S.any(moving, function(t) return S.near(u, tinfo[t].x, tinfo[t].z, 45) end)
    end) > 0
    local why = danger and "danger" or (not escort and "escort" or "")
    if why ~= (st.halted or "") then
      st.halted = why
      M.notify(why == "danger" and "The convoy has stopped - enemies near the trucks" or (why == "escort" and "The convoy waits for its escort" or "The convoy is moving again"), why ~= "" and "bad" or "good")
    end
    local halted = st.halted ~= nil and st.halted ~= ""
    for i, t in ipairs(moving) do
      local ti = tinfo[t]
      while st.wp[t] < #R and S.near(ti, R[st.wp[t]][1], R[st.wp[t]][2], 10) do st.wp[t] = st.wp[t] + 1 end
      -- a truck that should drive but has not moved for 15 s (pushed off the road, wedged): back onto the road
      local sa = st.at[t]
      if not sa or S.dist(ti.x, ti.z, sa[1], sa[2]) > 2 or halted then st.at[t] = { ti.x, ti.z, M.time }
      elseif M.time - sa[3] > 15 then
        local p, q = R[st.wp[t] - 1], R[st.wp[t]]
        local L = S.dist(p[1], p[2], q[1], q[2]) if L == 0 then L = 1 end
        local k = math.max(0, math.min(1, ((ti.x - p[1]) * (q[1] - p[1]) + (ti.z - p[2]) * (q[2] - p[2])) / (L * L)))
        M.teleport(t, p[1] + (q[1] - p[1]) * k, p[2] + (q[2] - p[2]) * k)
        st.at[t] = nil st.ord[t] = -1
      end
      local ahead = moving[i - 1]
      local close = ahead ~= nil and S.dist(tinfo[ahead].x, tinfo[ahead].z, ti.x, ti.z) < 15
      if halted or close then
        if ti.task ~= "idle" then M.stop({ t }) end
        st.ord[t] = -1
      elseif st.ord[t] ~= st.wp[t] or ti.task == "idle" then
        st.ord[t] = st.wp[t]
        M.move({ t }, R[st.wp[t]][1], R[st.wp[t]][2])
      end
    end
    local lead = st.lead
    for _, t in ipairs(moving) do if st.wp[t] > lead then lead = st.wp[t] end end
    for w = st.lead + 1, lead do M.call("leg", { wp = w - 1 }) end
    st.lead = lead
    if st.delivered >= st.need and st.needT == nil then st.needT = M.time end
    -- the last trucks in (or 90 s after enough of them are: the stragglers do not hold up the end)
    if st.delivered >= st.need and (#moving == 0 or M.time - st.needT > 90) and not st.won then
      st.won = true
      M.say("radio", "Depot reports the convoy in. Dr. Taslov is - well, he is hugging a crate, Colonel.")
      M.say("halloran", "Crane didn't come himself. He sent men to die for some crates and stayed home. Where is home, Kiko?")
      M.say("kiko", "His walkers came from the south-west. I followed the tracks as far as the Nevada line. Toward the lake.")
      M.after(10, "end")
    end
  end,
  ["end"] = function(M) M.win(M.state.delivered .. " of 5 trucks made it over the pass. The Door can be finished.") end,
}
