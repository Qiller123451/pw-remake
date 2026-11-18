-- Scenario 2: The Last Pass (the Barrow Pass, Northland). Ten waves of the dead out of the barrows, a countdown to
-- each, night before the eighth, the Barrow King leads the tenth. Ported from game/scenarios/missions.js scenario 2.
local S = require "sequel"
local C = require "scenarios"
local T = S.T

return {
  setup = function(M)
    local st = M.state
    C.startHero(M)
    st.wave = 0 st.last = 10
    st.clock = 3 * T
    st.clockLabel = "Wave 1 in"
    M.objective("waves", "Hold the pass: survive ten waves of the dead", { of = 10 })
    M.mark(0, 30, "the pass", 0x7dff6a)
    M.mark(-60, -95, "barrow", 0x9a7aff) M.mark(60, -95, "barrow", 0x9a7aff)
    M.say("elder", "They come at the turn of every hour, and every time there are more of them. The barrows are in the north - the pass is the only way south.")
    M.say(st.v, "Then the pass is where we stand. Walls across it, towers behind the walls, and every spear we have.")
    M.set_clock(st.clock, st.clockLabel)
    M.after(st.clock, "wave")
  end,
  wave = function(M)
    local st = M.state
    if st.over then return end
    st.wave = st.wave + 1
    local n = st.wave
    local home = M.home("me")
    local t = { 0, 190 }
    if home then local h = M.info(home) t = { h.x, h.z } end
    local B = { { -60, -85 }, { 60, -85 } }
    -- (the base game has no common dead: its undead warrior is a 3000-hit-point champion - held to the cap)
    local function send(cls, k, b, cap)
      local us = M.spawn(cls, "outbreak", b[1], b[2], k, { level = math.min(4, 1 + math.floor(n / 3)) })
      if cap then for _, u in ipairs(us) do if M.info(u).maxhp > cap * M.diff then M.set_hp(u, cap * M.diff, cap * M.diff) end end end
      M.send(us, t)
    end
    local function both(cls, k, cap) send(cls, math.ceil(k / 2), B[1], cap) send(cls, math.floor(k / 2), B[2], cap) end
    both(C.UNDEAD.warrior, C.more(M, 4 + n, 1 + math.floor(n / 4)), 500 + 40 * n)
    if n >= 2 then both(C.UNDEAD.archer, C.more(M, 1 + math.floor(n * 0.8)), 380 + 30 * n) end
    if n >= 4 then both(C.UNDEAD.brute, C.more(M, math.floor(n / 2)), 1100 + 60 * n) end
    if n == 3 or n == 7 then send("Smilodon", C.more(M, 4), B[n == 3 and 1 or 2]) M.say("scout", "Grave-cats with them this time - fast, and hungry!") end
    if n == 5 then
      local m = S.first(M.spawn("Mammoth", "outbreak", 0, -110, 1))
      if m then M.scale_hp(m, 5) M.set(m, "scale", 1.5) M.send({ m }, t) end
      M.say("scout", "Something huge is walking with them - a dead mammoth, bones and all!")
    end
    if n == 8 then M.set_night(true) M.say("elder", "Night falls. The dead see in the dark. We do not.") end
    if n == 10 then
      local k = S.first(M.spawn(C.UNDEAD.king, "outbreak", 0, -100, 1, { level = 3 }))
      if k then M.scale_hp(k, 8) M.set(k, "scale", 1.6) st.king = k M.send({ k }, t) end
      M.objective("king", "Destroy the Barrow King", {})
      M.say("warlord", "Living things. You have held my door for nine hours. Now I hold it.")
      M.say(st.v, "The Barrow King! Everything we have - now!")
    else
      M.say("scout", ({ "Here they come! Wave " .. n .. "!", "Wave " .. n .. " - from both barrows!", "The dead are moving - wave " .. n .. "!" })[n % 3 + 1])
    end
    st.inWave = true st.waveT = M.time
    st.clock = n < 10 and M.time + S.by(M, { 150, 130, 110 }) or 0
    st.clockLabel = "Wave " .. (n + 1) .. " in"
    M.set_clock(st.clock, st.clockLabel)
    if n < 10 then M.after(st.clock - M.time, "wave") end
  end,
  tick = function(M)
    local st = M.state
    if st.over then return end
    if C.homeLost(M) then st.over = true M.lose("The dead broke through the pass.") return end
    -- a wave is beaten when none of the dead are left; the next comes when its countdown is up either way
    local deadL = M.units("outbreak")
    local dead = #deadL
    if st.inWave and dead == 0 then
      st.inWave = false
      M.progress("waves", st.wave)
      if st.wave < 10 then M.notify("Wave " .. st.wave .. " beaten - the next comes in " .. S.fmt(st.clock - M.time), "good") end
      if st.wave == 4 then M.say("elder", "Four. The barrows are still breathing. Use the time - mend the walls.") end
    end
    local note
    if st.wave < 10 then note = st.clockLabel .. " " .. S.fmt(st.clock - M.time) .. (dead > 0 and (" · " .. dead .. " of the dead still standing") or "")
    else note = dead > 0 and (dead .. " of the dead still standing") or "" end
    M.set_note("waves", note)
    if st.king and not M.alive(st.king) then M.complete("king") end
    -- the last few of the dead (stuck somewhere): shown on the map, and after the King they crumble
    if st.inWave and dead > 0 and dead <= 4 and M.time - (st.waveT or 0) > 90 and M.time - (st.markT or 0) > 30 then
      st.markT = M.time
      for i, u in ipairs(deadL) do local q = M.info(u) M.mark(q.x, q.z, "left:" .. (i - 1), 0x9a7aff, 20) end
    end
    if st.wave >= 10 and st.king and not M.alive(st.king) and dead > 0 and dead <= 4 and M.time - st.waveT > 120 then M.kill(deadL) end
    if st.wave >= 10 and dead == 0 and not st.won then
      st.won = true st.over = true
      M.complete("waves")
      M.say("elder", "The barrows are closing. Listen - nothing. The pass held.")
      M.after(8, "end")
    end
  end,
  ["end"] = function(M) M.win("The pass held. The dead sleep again.") end,
}
