-- Containment 14: Power Play (Lake Mead, Nevada, June 24, 1966). Base against base: Crane's three rift engines, fed
-- from a generating station on the shore. A smashed engine is raised again while the station runs - unless the player
-- holds its site; the station's fall silences them all. Ported from game/sequel/missions_1966.js mission 14.
local S = require "sequel"

local function site(st, id) for _, s in ipairs(st.sites) do if s.id == id then return s end end end

return {
  setup = function(M)
    local st, c = M.state, "crane"
    st.hq = M.building("seas_headquarters", c, 165, -160, math.pi)
    M.building("seas_barracks", c, 125, -160, math.pi) M.building("seas_garage", c, 165, -120, math.pi)
    for _, p in ipairs({ { 120, -120 }, { 205, -120 }, { 130, -205 } }) do M.building("seas_turret_tower", c, p[1], p[2], 0) end
    st.station = M.building("sq_power_station", c, 205, -205, math.pi / 4)
    M.spawn("sq_crane", c, 175, -180, 1)
    M.spawn("seas_gunner", c, 140, -140, 6, { level = 2 }) M.spawn("seas_marksman", c, 150, -145, 3, { level = 2 })
    st.engineSite = {}
    st.sites = {
      { id = "west", x = -170, z = -120, waves = { { { "Velociraptor", 4 } }, { { "Deinonychus", 3 }, { "Velociraptor", 2 } }, { { "Allosaurus", 1 }, { "Velociraptor", 3 } }, { { "Carnotaurus", 1 }, { "Deinonychus", 3 } } } },
      { id = "hill", x = 10, z = 20, waves = { { { "Smilodon", 2 } }, { { "Velociraptor", 4 } }, { { "Smilodon", 2 }, { "Deinonychus", 3 } }, { { "Allosaurus", 2 } } } },
      { id = "shore", x = 200, z = 60, waves = { { { "Baryonyx", 2 } }, { { "Velociraptor", 4 } }, { { "Baryonyx", 2 }, { "Deinonychus", 2 } }, { { "Spinosaurus", 1 }, { "Baryonyx", 1 } } } },
    }
    for _, s in ipairs(st.sites) do M.call("raise", { id = s.id, first = s.id == "west" and 260 or (s.id == "hill" and 320 or 380) }) end
    M.objective("engines", "Silence Crane's three rift engines - all at once, or by cutting the power at his station by the lake", { of = 3 })
    M.objective("hq", "Destroy Crane's headquarters (south-east)", {})
    M.mark(205, -205, "station", 0xffe070)
    M.say("taslov", "He has copied my Gate - badly. Each engine tears a little hole and holds it open with brute current. Break the engine and the hole falls shut.")
    M.say("taslov", "But it is his station that feeds them. While it runs, his engineers can raise a broken engine in two minutes.")
    M.say("halloran", "Then we either sit on the wreckage or we pull the plug. Booker, find me a way to the lake.")
  end,
  -- an engine on its site, and its rift (again, after it was smashed: a new one)
  raise = function(M, a)
    local st = M.state
    local s = site(st, a.id)
    if not s or st.dark then return end
    if not S.alive(M, st.station) then return end
    if #M.near(s.x, s.z, 40, "me") > 0 then M.after(30, "raise", { id = a.id }) return end      -- the player holds the site
    s.engine = M.building("sq_rift_engine", "crane", s.x, s.z - 16, 0)
    if s.engine then st.engineSite[s.engine] = s.id end
    M.rift(s.id, s.x, s.z, { every = S.by(M, { 150, 120, 100 }), first = a.first or 40, target = { -190, 190 }, cap = 12, waves = s.waves })
    s.hasRift = true
    if s.raised then M.notify("Crane's engineers have raised a rift engine again", "bad") S.post(M, { { "seas_gunner", 3, 2 } }, s.x, s.z, 30) end
    s.raised = (s.raised or 0) + 1
    if s.raised > 1 and not st.rebuiltSaid then st.rebuiltSaid = true M.say("booker", "They put it back up! Next time we leave somebody sitting on the pieces.") end
    M.call("count")
  end,
  count = function(M)
    local st = M.state
    local dark = #S.filter(st.sites, function(s) return not s.hasRift or not M.rift_open(s.id) end)
    M.progress("engines", dark)
    if dark == 3 and not st.dark then
      st.dark = true
      M.say("taslov", "All three dark! Feel that? The air just got lighter.")
      M.say("crane", "Very well, Margaret. This side of the door is getting crowded anyway.")
    end
  end,
  died = function(M, a)
    local st = M.state
    local sid = st.engineSite[a.e]
    if sid then
      st.engineSite[a.e] = nil
      local s = site(st, sid)
      if s and s.hasRift then M.close_rift(s.id) end
      if not st.firstSmash then st.firstSmash = true M.say("taslov", "One engine down - and the rift with it.") end
      M.call("count")
      if not st.dark and S.alive(M, st.station) then M.after(120, "raise", { id = sid }) end
    end
    if a.e == st.station and not st.dark then
      M.say("taslov", "The station! Every engine just lost its current!")
      for _, s in ipairs(st.sites) do
        if S.alive(M, s.engine) then M.kill(s.engine) end
        if s.hasRift then M.close_rift(s.id) end
      end
      M.call("count")
    end
  end,
  tick = function(M)
    local st = M.state
    if not S.hero_alive(M, "sq_halloran") and M.time > 5 then M.lose("Colonel Halloran has fallen.") return end
    if not S.alive(M, st.hq) and not M.is_done("hq") then
      M.complete("hq")
      M.say("kiko", "Crane's headquarters is burning - and I've lost him. He drove straight into the shore rift before it closed.")
      M.say("taslov", "Into the rift? Then he is on the other side. In Tala's world.")
    end
    if M.is_done("hq") and M.is_done("engines") and not st.won then
      st.won = true
      M.say("halloran", "Lev. How soon can the Door open?")
      M.say("taslov", "A month. Less, if nobody shoots at me.")
      M.say("halloran", "Then we are going after him.")
      M.after(10, "end")
    end
  end,
  ["end"] = function(M) M.win("The engines are silent. Crane has fled - to the other side.") end,
}
