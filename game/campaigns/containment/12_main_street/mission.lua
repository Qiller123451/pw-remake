-- Containment 12: Main Street (Pueblo, Colorado, April 9, 1966). A heroes' mission: no base, nothing to build. The
-- three heroes clear the town district by district; who holds out in each district joins them once it is clear.
-- Old Scar waits in the stockyards. Ported from game/sequel/missions_1966.js mission 12.
local S = require "sequel"
local function more(M, n, k) return S.more(M, n, k, 1) end

return {
  setup = function(M)
    local st = M.state
    st.districts = {
      { id = "depot", name = "the freight depot", x = -140, z = 110, r = 50, foes = { { "Velociraptor", more(M, 6) }, { "Deinonychus", more(M, 2) } },
        join = { { "sq_trooper", 4, 2 }, { "seas_medic", 1, 2 } }, jx = -150, jz = 92, who = "guard",
        lines = { "Colonel! Captain Ruiz, Colorado Guard. We've been behind these sandbags since six. Four rifles and a medic - they're yours.", "Doc - see to the Colonel's people first." } },
      { id = "old", name = "Old Town", x = -60, z = -60, r = 60, foes = { { "Deinonychus", more(M, 4) }, { "Velociraptor", more(M, 3) }, { "Allosaurus", 1 } },
        join = { { "seas_marksman", 3, 3 } }, jx = -60, jz = -60, who = "deputy",
        lines = { "Deputy Alvarez. Three of us up in the bank tower, and we can still shoot straight. Point us at something." } },
      { id = "main", name = "Main Street", x = 120, z = 50, r = 70, foes = { { "Allosaurus", 1 }, { "Velociraptor", more(M, 5) } },
        join = { { "seas_mobile_suit", 1, 2 }, { "sq_jeep", 2, 1 } }, jx = 120, jz = 50, who = "driver",
        lines = { "Kowalski, motor pool. My walker threw a track at the crossing and we've been sitting in it all morning. Track's fixed. Let's go hunting." } },
      { id = "grain", name = "the grain elevator", x = 150, z = -135, r = 50, foes = { { "Smilodon", more(M, 3) }, { "Velociraptor", more(M, 4) }, { "Allosaurus", 1 } }, tame = true },
    }
    if M.difficulty == 2 then table.insert(st.districts[3].foes, { "Carnotaurus", 1 }) end
    for _, d in ipairs(st.districts) do d.us = S.guards(M, d.foes, d.x, d.z, math.min(45, d.r), d.id) M.mark(d.x, d.z, d.id, 0xffb040) end
    -- two of the menagerie's triceratops still penned in the stock car: Booker can talk them round
    st.tame = M.spawn("Triceratops", "outbreak", 162, -120, 2)
    M.set(st.tame, "untargetable", true) M.set(st.tame, "disarmed", true) M.trap(st.tame, 1e6, "penned")
    -- Old Scar and her pack in the stockyards
    st.boss = S.first(M.spawn("sq_old_scar", "outbreak", 200, 200, 1))
    if st.boss then M.set(st.boss, "scale", 1.4) M.set(st.boss, "home", { 200, 200, 40 }) end
    S.guards(M, { { "Velociraptor", more(M, 5) }, { "Deinonychus", more(M, 3) } }, 195, 190, 40, "yard")
    S.civilians(M, -60, -60, 5, 70) S.civilians(M, 120, 50, 5, 90)
    M.objective("districts", "Clear the four districts of the town (marked on the map)", { of = 4 })
    M.objective("people", "Bring the townsfolk to the buses on the highway", { of = S.by(M, { 5, 7, 8 }), secondary = true, civ = true })
    M.mark(-215, 158, "buses", 0x7dff6a)
    M.grant(4)
    M.say("radio", "Colonel, the Guard is pinned down at the freight depot and the sheriff's office has gone quiet. Everything on that train is loose.")
    M.say("halloran", "No time for a base. We walk in, we clear it a street at a time, and we pick up everybody who can still hold a rifle.")
    M.say("tala", "I know these animals. I hunted their cousins before your world had a name for them. Stay behind me, Booker.")
    M.say("booker", "Ma'am, I've fed half of these by hand. Some of 'em even like me.")
    M.after(150, "patrol")
  end,
  -- packs out of the stockyards hunting the heroes, until Old Scar comes out herself
  patrol = function(M)
    if M.state.bossOut or not S.alive(M, M.state.boss) then return end
    local h = S.first(M.units("me", "sq_halloran"))
    local t = { -200, 200 }
    if h then local i = M.info(h) t = { i.x, i.z } end
    M.spawn("Velociraptor", "outbreak", 190, 180, more(M, 3), { target = t })
    M.state.patrols = (M.state.patrols or 0) + 1
    if M.state.patrols % 2 == 0 then M.spawn("Deinonychus", "outbreak", 195, 185, more(M, 2), { target = t }) end
    if not M.state.patrolSaid then M.state.patrolSaid = true M.say("kiko", "Kiko here, flying top cover. A pack just broke out of the stockyards and they're coming your way.") end
    M.after(S.by(M, { 180, 150, 120 }), "patrol")
  end,
  evacuated = function(M, a)
    M.progress("people", a.n)
    if a.n == 3 then M.say("guard", "Buses are filling up. Keep 'em coming, Colonel.") end
  end,
  tick = function(M)
    local st = M.state
    if not S.hero_alive(M, "sq_halloran") and M.time > 5 then M.lose("Colonel Halloran has fallen.") return end
    if #M.units("me") == 0 and M.time > 5 then M.lose("Nobody is left standing in Pueblo.") return end
    -- a district is clear when its creatures are dead and the player stands in it: its holdouts come out, the medics
    -- patch everybody up
    for _, d in ipairs(st.districts) do
      if not d.done and not S.any(d.us, function(id) return M.alive(id) end) and #M.near(d.x, d.z, d.r, "me") > 0 then
        local wait = false
        if d.tame then
          local bk = S.first(M.units("me", "sq_booker"))
          local pen = S.living(M, st.tame)
          if #pen > 0 and not (bk and S.near(M.info(bk), 162, -120, 22)) then
            if not st.tameSaid then
              st.tameSaid = true
              M.say("booker", "Hold your fire on the stock car! Those two are tame - or they were. Let me walk up to them. Just me.")
              M.mark(162, -120, "stock car", 0x7dff6a, 120)
            end
            wait = true
          else
            M.set(pen, "untargetable", false) M.set(pen, "disarmed", false) M.trap(pen, 0) M.set_owner(pen, "me") M.set(pen, "home", nil)
            if #pen > 0 then M.say("booker", "Easy, girls. Easy. Remember me? There we go.") M.say("booker", "Colonel - meet the cavalry.") end
          end
        else
          for _, j in ipairs(d.join) do M.spawn(j[1], "me", d.jx, d.jz, j[2], { level = j[3] }) end
          for i, l in ipairs(d.lines) do M.say(i == 1 and d.who or "guard", l) end
        end
        if not wait then
          d.done = true
          M.unmark(d.id)
          S.heal(M)
          M.grant(2)                    -- (command points for the artillery)
          if d.id == "depot" then M.say("halloran", "Good. Every district we clear, the medics get a breather to patch everybody up. Between districts, we are on our own.") end
          M.notify(d.name:sub(1, 1):upper() .. d.name:sub(2) .. " is clear", "good")
          M.progress("districts", #S.filter(st.districts, function(x) return x.done end))
        end
      end
    end
    if M.is_done("districts") and not st.bossOut then
      st.bossOut = true
      M.objective("scar", "Kill Old Scar in the stockyards (south-east)", {})
      M.mark(200, 200, "Old Scar", 0xff5030)
      M.say("booker", "That leaves the stockyards. Colonel, the big one in there - Crane called her Old Scar. One eye, and she remembers every man who ever put a cage on her.")
      M.say("tala", "Then she will remember me too. Together, all of us. Not one at a time.")
      if S.alive(M, st.boss) then M.set(st.boss, "home", { 200, 200, 70 }) end
    end
    if st.boss and not M.alive(st.boss) and not st.won then
      st.won = true
      M.complete("scar")
      if not M.is_done("districts") then M.progress("districts", 4) end
      M.say("halloran", "She's down. Booker - how did that train come off the rails?")
      M.say("booker", "Didn't come off, Colonel. Somebody cut the couplings with a torch. And they left this on the last car - a little brass crane.")
      M.say("halloran", "...He wants us to know.")
      M.after(10, "end")
    end
  end,
  ["end"] = function(M) M.win("Pueblo is safe - and Augustus Crane is back.") end,
}
