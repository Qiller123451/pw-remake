-- "Outbreak": the mission's logic (docs/OUTBREAK.md). Written by hand - the generator (tools/outbreak) never touches
-- this file; it writes layout.lua (where the camps, passes, old walls, villages, the rockslide and the plague lords'
-- lairs are), triggers.lua and the map.
--
-- As StarCraft II's "Outbreak":
-- * every seat starts in epoch 2 (a house of its tribe where the start army needs one) with six workers already at
--   work - two at the seat's own field (its tribe's food building, put down beside the headquarters), two at wood,
--   two at stone;
-- * by day the infected stay in and near their villages (passive unless attacked; a village under attack sends a few
--   defenders out of its buildings); the days are shorter on Hard;
-- * the sunlight burns them: by day an infected unit away from the infected buildings catches fire and burns to death
--   in 5 - 15 s (staggered); a building's daytime defenders burn when their fight is done (50 s); at dawn the night's
--   attackers out in the open burn, those by an infected building go back inside;
-- * at night every surviving infected building sends its dead at the base in a steady stream (a pulse every 20 s;
--   the far towns only while their dead can still arrive before dawn), through the pass on its side - bigger and
--   nastier each night (infected villagers first, then soldiers, from night 3 the aberrations: killers, berserkers,
--   rhinos, mammoths); a building attacked at night spawns its dead fast (the counterattack);
-- * the south-west pass is blocked by a rockslide until the infected break it: during night 2, or at the first night
--   after 40 % of the infected buildings are gone (at once if that happens at night);
-- * two plague lords (the side quest) hide in their towns by day - out of the world: not seen, not hit - and stalk
--   their own towns at night (they never join the waves: the players go to them); both dead: every seat gets 500
--   food, 500 wood and 400 stone.
-- Won: every infected building destroyed. Lost: every headquarters of the players destroyed.
--
-- Everything here runs inside the simulation's step (the Game API, the world's random numbers): the same on every
-- machine of a co-op game and in a saved game's replay. The state is the locals below.

local L = require "layout"
local INF = L.infected

-- ---------------------------------------------------------------- tuning (docs/OUTBREAK.md "Tuning")
local T = {
  per_building = 0.12,     -- attackers per infected building and night ...
  per_night    = 0.16,     -- ... plus this per night number (night 1: 0.28, night 3: 0.60, night 6: 1.08)
  stage3       = 1.4,      -- a fully overrun building (stage 3) sends this many times as many
  difficulty   = { [0] = 0.7, [1] = 1.0, [2] = 1.35 },
  day          = { [0] = 420, [1] = 360, [2] = 180 },   -- the day's length by difficulty (StarCraft II: 6 min Normal, 3 Brutal)
  first_day    = { [0] = 600, [1] = 480, [2] = 300 },   -- the first day
  stream_every = 20,       -- a pulse every so many seconds of the night ...
  walk_speed   = 4,        -- ... from a village only while its dead (about this fast, m/s) arrive before dawn
  guards       = 0.6,      -- guards per building at the start (at least 2 per village)
  defenders    = 2,        -- by day: units a building under attack sends out (+1 every second night) ...
  defend_every = 25,       -- ... at most every so many seconds ...
  defend_max   = 3,        -- ... and so many times
  counter      = 4,        -- at night: units a building under attack spawns (+1 per night) ...
  counter_every = 5,       -- ... every so many seconds ...
  counter_max  = 6,        -- ... so many times a night
  leash        = 22,       -- a guard further than this from its building by day walks back (inside the shelter)
  wave_cap     = 170,      -- the waves stop at so many infected units ...
  cap          = 200,      -- ... the counterattacks at so many
  shelter      = 35,       -- by day an infected unit further than this (m) from every infected building ...
  sun_delay    = 6,        -- ... catches fire within so many seconds (staggered) ...
  burn_min     = 5,        -- ... and burns to death in 5 ...
  burn_max     = 15,       -- ... to 15 s
  defender_life = 50,      -- a building's daytime defenders burn so many seconds after they came out
  breach_night = 2,        -- the rockfall breaks in this night ...
  breach_at    = 45,       -- ... so many seconds into it ...
  breach_share = 0.4,      -- ... or at the first night after this share of the infected buildings is gone
  epoch        = 2,        -- every seat starts in this epoch
  house_below  = 12,       -- a seat whose unit limit is below this gets a house of its tribe (at most two)
  field        = "hu_corn_field",   -- every seat's food building at the start (in its tribe: spawn_own) ...
  field_at     = 26,       -- ... so many metres from its headquarters towards the base's middle
  lord_level   = 4,
  lord_roam    = 24,       -- at night a lord walks (attack-move) to a spot this far round its lair ...
  lord_patrol  = 15,       -- ... every so many seconds ...
  lord_leash   = 50,       -- ... and walks back (not fighting) when it is further than this from its lair
  lord_hp      = { lord = 6000, beast = 8000 },   -- (times the difficulty's factor)
  lord_scale   = { lord = 1.6, beast = 1.15 },    -- drawn this many times bigger (the beast: the Norse titan triceratops)
  reward       = { food = 500, wood = 500, stone = 400 },   -- every seat, both lords dead
}

-- ---------------------------------------------------------------- who they are (the class first found in the rules)
-- The Norsemen's undead and zombies, the Norsemen themselves infected, the wild animals (the rules' spelling). MIRAGE has
-- more zombies (hu_zombie_*): a class the configuration lacks is left out. from / to: the nights they come in.
local WAVES = {
  { from = 1, to = 4, w = 4, cls = { "hu_zombie_warrior", "hu_undead_warrior" } },
  { from = 1, to = 3, w = 2, cls = { "hu_undead_warrior" } },
  { from = 1, to = 3, w = 1, cls = { "Wild_Boar" } },
  { from = 2, w = 3, cls = { "hu_warrior" } },
  { from = 2, w = 2, cls = { "hu_zombie_archer", "hu_archer" } },
  { from = 2, w = 2, cls = { "hu_spearman" } },
  { from = 2, w = 1, cls = { "Smilodon" } },
  { from = 3, w = 2, cls = { "hu_undead_killer" } },
  { from = 3, w = 2, cls = { "hu_zombie_killer", "hu_berserker" } },
  { from = 3, w = 1, cls = { "Woolly_Rhino" } },
  { from = 4, w = 1, cls = { "hu_marksman", "hu_archer" } },
  { from = 4, w = 1, cls = { "Mammoth" } },
  { from = 5, w = 1, cls = { "Eusmilus", "Smilodon" } },
  { from = 6, w = 1, cls = { "hu_mammoth" } },
}
local GUARDS = {
  { w = 3, cls = { "hu_undead_warrior" } },
  { w = 2, cls = { "hu_zombie_warrior", "hu_warrior" } },
  { w = 1, cls = { "hu_zombie_archer", "hu_archer" } },
  { w = 1, cls = { "Smilodon" } },
  { w = 1, cls = { "Wild_Boar" } },
}
local LORDS = {
  lord  = { "schliemann_zombie", "hu_undead_killer" },
  beast = { "hu_triceratops", "Triceratops", "Mammoth" },
}
local HQ = { "hu_fireplace", "aje_resource_collector", "ninigi_fireplace", "seas_headquarters" }

-- ---------------------------------------------------------------- state
local S = {
  ready = false,
  buildings = {},      -- id -> { v, last_hp, next_defend, defended, next_counter, countered }
  villages = {},       -- the layout's villages + { ids = { ... }, quota, sent, p, allowed: the night's share }
  units = {},          -- id -> { kind = "guard" | "wave" | "defender" | "counter", home = {x, y}, v, leg = 1 | 2, last = {x, y}, t, born, fire_at, burning }
  phase = nil, night = 0, night_t0 = nil, pulse = 0,
  left = 0, total = 0, half = false, won = false, lost = false,
  barrier = { ids = {}, open = false, due = false },
  lords = {},          -- { name, kind, v, id, dead, out, home, next }
  lords_dead = 0, lords_seen = false, lords_done = false,
  sent = 0,            -- the night waves' units so far
}

local function pick(list)
  for _, c in ipairs(list) do if api.exists(c) then return c end end
  return nil
end

local function dist(ax, ay, bx, by) local dx, dy = ax - bx, ay - by; return math.sqrt(dx * dx + dy * dy) end

local function diffk() return T.difficulty[api.difficulty()] or 1 end

-- a weighted choice of a table's classes for the night (the world's random numbers)
local function choose(tab, night)
  local sum = 0
  local function ok(e) return (e.from or 0) <= night and (e.to == nil or night <= e.to) end
  for _, e in ipairs(tab) do if ok(e) and pick(e.cls) then sum = sum + e.w end end
  if sum <= 0 then return pick(tab[1].cls) end
  local r = api.random() * sum
  for _, e in ipairs(tab) do
    if ok(e) then
      local c = pick(e.cls)
      if c then r = r - e.w; if r <= 0 then return c end end
    end
  end
  return pick(tab[1].cls)
end

local function infected_units()
  return api.objects.find{ obj_owner = INF, obj_type = "CHTR|ANML|VHCL|" }
end

local function count_units()
  local n = 0
  for _, _ in pairs(S.units) do n = n + 1 end
  return n
end

-- spawns one unit of the class at (x, y) and returns every new unit (the co-op scaling may make more than one)
local function spawn(cls, x, y, level, kind, v, home)
  local before = {}
  for _, id in ipairs(infected_units()) do before[id] = true end
  local first = api.units.spawn(cls, INF, x, y, level)
  local new = {}
  if not first then return new end
  for _, id in ipairs(infected_units()) do
    if not before[id] and not S.units[id] and not S.lord_ids[id] then
      S.units[id] = { kind = kind, v = v, home = home, leg = 1, t = api.time(), last = { x, y }, born = api.time() }
      api.objects.infect(id, 3)
      new[#new + 1] = id
    end
  end
  return new
end
S.lord_ids = {}

local function alive_buildings(v)
  local res = {}
  for _, id in ipairs(v.ids) do
    local i = api.objects.info(id)
    if i and i.alive then res[#res + 1] = i end
  end
  return res
end

-- the entrance a village's dead take: its own, or while the rockslide stands the nearest open one
local function entrance_of(v)
  if v.entrance == L.barrier.entrance and not S.barrier.open then return v.alt end
  return v.entrance
end

-- ---------------------------------------------------------------- start
local function hq_of(slot)
  for _, cls in ipairs(HQ) do
    if api.exists(cls) then
      local l = api.objects.find{ obj_owner = slot, obj_class = cls }
      if #l > 0 then return api.objects.info(l[1]) end
    end
  end
  return nil
end

-- epoch 2 for every seat, a house of the seat's tribe where the start army needs room, the seat's own field (its
-- tribe's food building) and its six workers at work: two at the field, two at wood, two at stone (round 4, Kacper:
-- "start with 6 workers and 2 of each assigned to every resource; put 1 pre-built food extractor")
local WORK = { "food", "food", "wood", "wood", "stone", "stone" }
local function seats_start()
  for _, slot in ipairs(api.players.seats()) do
    api.players.epoch(slot, T.epoch)
    local hq = hq_of(slot)
    if hq then
      -- (beside the headquarters, across the camp's line to the base's middle)
      local cx, cy = L.centre[1], L.centre[2]
      local dx, dy = cx - hq.x, cy - hq.y
      local l = math.max(1, math.sqrt(dx * dx + dy * dy)); dx, dy = dx / l, dy / l
      for k = 1, 2 do
        if (api.players.attribute(slot, "max_units") or 0) >= T.house_below then break end
        local side = (k == 1) and 1 or -1
        api.units.spawn_own("hu_stone_cottage", slot, hq.x - dy * 20 * side + dx * 4, hq.y + dx * 20 * side + dy * 4)
      end
      -- the field: towards the base's middle (the Norsemen's corn field; the tribe swap gives the others' equivalent:
      -- the Dustriders' slaughterhouse, the Dragon Clan's paddy, the SEAS greenhouse - Tribes.cs "field")
      api.units.spawn_own(T.field, slot, hq.x + dx * T.field_at, hq.y + dy * T.field_at)
    end
    -- the workers: the seat's own characters in id order, each to the next resource of WORK that it can gather
    local mine = api.objects.find{ obj_owner = slot, obj_type = "CHTR|" }
    table.sort(mine)
    local k = 1
    for _, id in ipairs(mine) do
      if k > #WORK then break end
      if api.units.gather(id, WORK[k]) then k = k + 1 end
    end
  end
end

local function lords_start()
  for _, ll in ipairs(L.lords) do
    local v = S.villages[ll.village]
    local cls = pick(LORDS[ll.kind])
    if v and cls then
      local x, y = ll.x or v.x, ll.y or v.y        -- (its lair: the town's open spot furthest from the buildings)
      local id = api.units.spawn(cls, INF, x, y, T.lord_level, nil, true)
      if id then
        local lo = { name = ll.name, kind = ll.kind, v = v, id = id, dead = false, out = false, next = 0, home = { x, y } }
        S.lords[#S.lords + 1] = lo
        S.lord_ids[id] = true
        api.units.maxhp(id, T.lord_hp[ll.kind] * diffk())
        api.units.scale(id, T.lord_scale[ll.kind])
        api.objects.infect(id, 3)
        api.objects.appear(id, 0)              -- by day: hidden in its town (not drawn, not found, not hit)
      end
    end
  end
end

local function setup()
  if S.ready then return end
  S.ready = true
  local d = api.difficulty()
  api.daynight.set{ day = T.day[d] or T.day[1], first_day = T.first_day[d] or T.first_day[1] }
  for _, lv in ipairs(L.villages) do
    local v = { name = lv.name, size = lv.size, x = lv.x, y = lv.y, r = lv.r, entrance = lv.entrance, alt = lv.alt, stage = lv.stage, ids = {} }
    for _, name in ipairs(lv.buildings) do
      local id = api.objects.find_one(name)
      local i = id and api.objects.info(id)
      if i and i.alive and (i.maxhp or 0) > 0 then
        v.ids[#v.ids + 1] = id
        S.buildings[id] = { v = v, last_hp = i.hp or 0, next_defend = 0, defended = 0, next_counter = 0, countered = 0 }
        api.objects.infect(id, v.stage)
        S.total = S.total + 1
      else
        api.log("Outbreak: building " .. name .. " is missing (not placed)")
      end
    end
    S.villages[#S.villages + 1] = v
  end
  S.left = S.total
  for _, name in ipairs(L.barrier.rocks) do
    local id = api.objects.find_one(name)
    if id then S.barrier.ids[#S.barrier.ids + 1] = id end
  end
  seats_start()
  lords_start()
  -- the guards: by day they stand by their village's buildings (in their shade: the sun burns the infected out in the
  -- open)
  for _, v in ipairs(S.villages) do
    local n = math.max(2, math.floor(#v.ids * T.guards * diffk() + 0.5))
    local bs = alive_buildings(v)
    for k = 1, n do
      local cls = choose(GUARDS, 1)
      local a = api.random() * 6.283
      local b = bs[1 + (k - 1) % math.max(1, #bs)]
      local hx, hy = b and b.x or v.x, b and b.y or v.y
      local x, y = hx + math.cos(a) * 9, hy + math.sin(a) * 9
      if cls then spawn(cls, x, y, 1, "guard", v, { hx, hy }) end
    end
  end
  api.vars.set("ob_total", S.total)
  api.vars.set("ob_left", S.left)
  api.vars.set("ob_night", 0)
  api.vars.set("ob_lords", 0)
  api.action("InfoBar", { text = "_OB_INFO\t$(ob_left)\t$(ob_total)\t$(ob_lords)" })
  api.quests.set("OB_MQ01", "show")
  api.quests.set("OB_MQ02", "show")
  api.quests.set("OB_SQ01", "show")
  api.quests.set("OB_SQ02", "show")
  api.ui.dialog("outbreak/ob_start.dlg")
  api.log(string.format("Outbreak: %d infected buildings in %d villages, %d plague lords, difficulty %d", S.total, #S.villages, #S.lords, d))
end

-- ---------------------------------------------------------------- night: the waves
local function send(id, u)
  local c = L.camps[entrance_of(u.v)]
  if u.leg == 1 then api.units.move(id, c.entrance_out[1], c.entrance_out[2], true)
  else api.units.move(id, c.x, c.y, true) end
end

-- how long a village's dead walk to the base
local function travel(v)
  local c = L.camps[entrance_of(v)]
  return (dist(v.x, v.y, c.entrance_out[1], c.entrance_out[2]) + dist(c.entrance_out[1], c.entrance_out[2], c.x, c.y)) / T.walk_speed
end

local function wave_from(v, want, level)
  local bs = alive_buildings(v)
  if #bs == 0 then return end
  for i = 1, want do
    if count_units() >= T.wave_cap then break end
    local b = bs[1 + math.floor(api.random() * #bs)]
    local cls = choose(WAVES, S.night)
    if cls then
      for _, id in ipairs(spawn(cls, b.x, b.y, level, "wave", v)) do send(id, S.units[id]); S.sent = S.sent + 1 end
    end
  end
end

-- (the night's pulses: at 0, 20, ... 160 s of a 180 s night)
local function pulses() return math.max(1, math.floor((S.night_len or 180) / T.stream_every)) end

-- the night's share of each village: its quota (by its buildings left at nightfall), spread over the pulses whose dead
-- still arrive before dawn (the far towns send theirs in the night's first part)
local function night_plan()
  local n, np = S.night, pulses()
  for _, v in ipairs(S.villages) do
    local k = (v.stage >= 3) and T.stage3 or 1
    v.quota = #alive_buildings(v) * (T.per_building + T.per_night * n) * k * diffk()
    v.sent, v.p, v.allowed = 0, 0, 1
    for p = 2, np do if (p - 1) * T.stream_every + travel(v) * 0.8 < (S.night_len or 180) then v.allowed = p end end
  end
end

local function wave_pulse(pulse, t)
  local n = S.night
  local level = math.min(4, 1 + math.floor((n - 1) / 2))
  for _, v in ipairs(S.villages) do
    if pulse <= (v.allowed or 1) then
      v.p = v.p + 1
      local want = math.floor((v.quota or 0) * v.p / v.allowed + 0.5) - v.sent
      if want > 0 then v.sent = v.sent + want; wave_from(v, want, level) end
    end
  end
  -- the villages that are left show on the minimap where the waves come from (the first pulse)
  if pulse == 1 then
    for _, v in ipairs(S.villages) do
      if #alive_buildings(v) > 0 then
        api.action("MapPing", { id = "ob_" .. v.name, pos = string.format("[%g %g 0]", v.x, v.y), pos_objquery_flag = 1, colortype = "Attack", time_to_life = 8000 })
      end
    end
  end
end

-- the waves keep walking: leg 1 to the outer end of their pass, leg 2 through it to the camp; a unit that stood still (a fight is
-- over, it lost its way) is sent on again
local function steer_one(id, u, i, now)
  local c = L.camps[entrance_of(u.v)]
  if u.leg == 1 and dist(i.x, i.y, c.entrance_out[1], c.entrance_out[2]) < 14 then u.leg = 2; send(id, u) end
  if now - u.t >= 8 then
    if dist(i.x, i.y, u.last[1], u.last[2]) < 2 then send(id, u) end
    u.last = { i.x, i.y }; u.t = now
  end
end

local function steer()
  local now = api.time()
  for id, u in pairs(S.units) do
    if u.kind == "wave" then
      local i = api.objects.info(id)
      if i and i.alive then steer_one(id, u, i, now) end
    end
  end
end

-- ---------------------------------------------------------------- the sunlight
-- Round 4 (Kacper: "infected should burn and die during the day", as StarCraft II's): by day (and at dawn) an infected
-- unit further than T.shelter from every infected building catches fire within T.sun_delay s (staggered: each its own
-- moment) and burns to death in 5 - 15 s (api.units.burn: the original's fire on it); one back in the shade by then is
-- spared. A building's daytime defenders burn T.defender_life s after they came out (their fight is done). The plague
-- lords never burn (they hide by day).
local function sheltered(x, y, bs)
  for _, b in ipairs(bs) do if dist(x, y, b.x, b.y) <= T.shelter then return true end end
  return false
end

local function sunlight()
  local now = api.time()
  local bs = {}
  for id, _ in pairs(S.buildings) do
    local i = api.objects.info(id)
    if i and i.alive then bs[#bs + 1] = i end
  end
  local ids = {}
  for id, u in pairs(S.units) do if not u.burning then ids[#ids + 1] = id end end
  table.sort(ids)                       -- (the random numbers in the same order on every machine)
  for _, id in ipairs(ids) do
    local u = S.units[id]
    local i = api.objects.info(id)
    if i and i.alive then
      local out = not sheltered(i.x, i.y, bs)
      local spent = u.kind == "defender" and now - (u.born or now) >= T.defender_life
      if not u.fire_at then
        if out or spent then u.fire_at = now + api.random() * T.sun_delay end
      elseif now >= u.fire_at then
        if out or spent then
          u.burning = true
          api.units.burn(id, T.burn_min + api.random() * (T.burn_max - T.burn_min))
        else
          u.fire_at = nil               -- back in the shade in time
        end
      end
    end
  end
end

-- dawn: the night's attackers by an infected building go back inside (they come out again at night: the waves are
-- new units); those in the open are left to the sun
local function go_inside()
  local bs = {}
  for id, _ in pairs(S.buildings) do
    local i = api.objects.info(id)
    if i and i.alive then bs[#bs + 1] = i end
  end
  local ids = {}
  for id, u in pairs(S.units) do if u.kind == "wave" or u.kind == "counter" then ids[#ids + 1] = id end end
  table.sort(ids)
  for _, id in ipairs(ids) do
    local i = api.objects.info(id)
    if i and i.alive and sheltered(i.x, i.y, bs) then S.units[id] = nil; api.objects.remove(id) end
  end
end

-- ---------------------------------------------------------------- the rockfall
local function breach()
  if S.barrier.open then return end
  S.barrier.open = true
  for _, id in ipairs(S.barrier.ids) do api.objects.remove(id) end
  local b = L.barrier
  api.vars.set("ob_breach", 1)
  api.ui.dialog("outbreak/ob_breach.dlg")
  api.ui.ping(b.x, b.y, "ob_breach")
  api.action("MapPing", { id = "ob_breach", pos = string.format("[%g %g 0]", b.x, b.y), pos_objquery_flag = 1, colortype = "Attack", time_to_life = 15000 })
  api.ui.sound("ui_quest_new")
  -- the villages behind it come through at once
  local level = math.min(4, 1 + math.floor((math.max(1, S.night) - 1) / 2))
  for _, v in ipairs(S.villages) do
    if v.entrance == b.entrance then wave_from(v, math.ceil(#alive_buildings(v) * 0.5), level) end
  end
  -- (the dead already on their way round turn to it)
  for id, u in pairs(S.units) do if u.kind == "wave" and u.leg == 1 and u.v.entrance == b.entrance then send(id, u) end end
  api.log("Outbreak: the rockfall is broken (night " .. S.night .. ")")
end

-- ---------------------------------------------------------------- the plague lords
-- At night the lords come out and stalk their own towns (round 4, Kacper: a 6000 HP lord at the base in night 1 -
-- "was he supposed to be sent?"; StarCraft II's lords never join the waves): every 15 s an attack-move to a spot
-- round the lair, fighting what comes near; further than 50 m from it a lord walks back without fighting. At dawn they
-- walk home, by day they are out of the world.
local function lord_roam(lo)
  local a, r = api.random() * 6.283, T.lord_roam * (0.3 + 0.7 * api.random())
  api.units.move(lo.id, lo.home[1] + math.cos(a) * r, lo.home[2] + math.sin(a) * r, true)
end

local function lords_night()
  for _, lo in ipairs(S.lords) do
    if not lo.dead then
      api.objects.appear(lo.id, 7)
      lo.out = true; lo.next = api.time() + T.lord_patrol
      lord_roam(lo)
      api.action("MapPing", { id = "ob_" .. lo.name, pos = string.format("[%g %g 0]", lo.home[1], lo.home[2]), pos_objquery_flag = 1, colortype = "Attack", time_to_life = 10000 })
    end
  end
  if not S.lords_seen and #S.lords > 0 then
    S.lords_seen = true
    api.ui.dialog("outbreak/ob_lords.dlg")
  end
end

local function lords_patrol()
  local now = api.time()
  for _, lo in ipairs(S.lords) do
    if lo.out and not lo.dead then
      local i = api.objects.info(lo.id)
      if i and i.alive then
        if dist(i.x, i.y, lo.home[1], lo.home[2]) > T.lord_leash then
          api.units.move(lo.id, lo.home[1], lo.home[2], false)
          lo.next = now + T.lord_patrol
        elseif now >= lo.next then
          lo.next = now + T.lord_patrol
          lord_roam(lo)
        end
      end
    end
  end
end

local function lords_dawn()
  for _, lo in ipairs(S.lords) do
    if not lo.dead and lo.out then api.units.move(lo.id, lo.home[1], lo.home[2], false) end
  end
end

local function lords_day()
  for _, lo in ipairs(S.lords) do
    if not lo.dead and lo.out then
      lo.out = false
      api.units.teleport(lo.id, lo.home[1], lo.home[2])
      api.objects.appear(lo.id, 0)
    end
  end
end

local function lords_check()
  for _, lo in ipairs(S.lords) do
    if not lo.dead then
      local i = api.objects.info(lo.id)
      if not i or not i.alive then
        lo.dead = true
        S.lords_dead = S.lords_dead + 1
        api.vars.set("ob_lords", S.lords_dead)
        if S.lords_dead < #S.lords then api.ui.dialog("outbreak/ob_lord_dead.dlg") end
      end
    end
  end
  if not S.lords_done and #S.lords > 0 and S.lords_dead >= #S.lords then
    S.lords_done = true
    api.quests.set("OB_SQ02", "done")
    for _, slot in ipairs(api.players.seats()) do
      for res, n in pairs(T.reward) do api.players.resource(slot, res, "+" .. n) end
    end
    api.ui.dialog("outbreak/ob_lords_done.dlg")
  end
end

-- ---------------------------------------------------------------- guards, defenders, the night's counterattack
local function watch(night)
  local now = api.time()
  -- a building that lost hit points sends defenders out: by day a few, slowly; at night many, fast (the counterattack)
  for id, b in pairs(S.buildings) do
    local i = api.objects.info(id)
    if i and i.alive then
      if i.hp < b.last_hp - 0.5 then
        if night then
          if now >= b.next_counter and b.countered < T.counter_max and count_units() < T.cap then
            b.countered = b.countered + 1
            b.next_counter = now + T.counter_every
            local level = math.min(4, 1 + math.floor((math.max(1, S.night) - 1) / 2))
            for k = 1, T.counter + S.night do
              if count_units() >= T.cap then break end
              local cls = choose(WAVES, math.max(1, S.night))
              if cls then
                for _, uid in ipairs(spawn(cls, i.x, i.y, level, "counter", b.v, { i.x, i.y })) do api.units.move(uid, i.x, i.y, true) end
              end
            end
          end
        elseif now >= b.next_defend and b.defended < T.defend_max and count_units() < T.cap then
          b.defended = b.defended + 1
          b.next_defend = now + T.defend_every
          for k = 1, T.defenders + math.floor(S.night / 2) do
            local cls = choose(GUARDS, 1)
            if cls then
              for _, uid in ipairs(spawn(cls, i.x, i.y, math.min(4, 1 + math.floor(S.night / 2)), "defender", b.v, { i.x, i.y })) do
                api.units.move(uid, i.x, i.y, true)
              end
            end
          end
        end
      end
      b.last_hp = i.hp
    end
  end
  -- by day guards and defenders that strayed walk back to their village
  if not night and math.floor(now) % 5 == 0 then
    for id, u in pairs(S.units) do
      if (u.kind == "guard" or u.kind == "defender") and u.home then
        local i = api.objects.info(id)
        if i and i.alive and dist(i.x, i.y, u.home[1], u.home[2]) > T.leash then api.units.move(id, u.home[1], u.home[2], false) end
      end
    end
  end
end

-- ---------------------------------------------------------------- the end
local function count_left()
  local n = 0
  for id, b in pairs(S.buildings) do
    local i = api.objects.info(id)
    if i and i.alive then n = n + 1 else S.buildings[id] = nil end
  end
  return n
end

local function hq_alive()
  for _, slot in ipairs(api.players.seats()) do
    if hq_of(slot) then return true end
  end
  return false
end

-- the kennels' own animals (the Norse kennel keeps two eusmilus round it: Buildings.cs NestB) and anything else the
-- infected's buildings bring out are drawn infected too (every 5 s)
local function infect_strays()
  if math.floor(api.time()) % 5 ~= 0 then return end
  for _, id in ipairs(infected_units()) do
    if not S.units[id] and not S.lord_ids[id] and api.objects.infection(id) ~= 3 then api.objects.infect(id, 3) end
  end
end

local function tick()
  if not S.ready then setup() end
  if S.won or S.lost then return end
  infect_strays()
  -- forget the dead
  for id, _ in pairs(S.units) do
    local i = api.objects.info(id)
    if not i or not i.alive then S.units[id] = nil end
  end
  lords_check()
  -- the buildings left; half of them gone; all gone: won
  local left = count_left()
  if left ~= S.left then
    S.left = left
    api.vars.set("ob_left", left)
    if not S.half and left <= S.total / 2 and left > 0 then S.half = true; api.ui.dialog("outbreak/ob_half.dlg") end
  end
  if left == 0 then
    S.won = true
    api.quests.set("OB_MQ01", "done")
    api.quests.set("OB_MQ02", "done")
    api.ui.dialog("outbreak/ob_win.dlg")
    api.timers.start("ob_win", 6)
    return
  end
  if not hq_alive() then
    S.lost = true
    api.quests.set("OB_MQ02", "failed")
    api.lose("_OB_LOST")
    return
  end
  if not S.barrier.due and S.total - left >= T.breach_share * S.total then S.barrier.due = true end
  -- the day and the night
  local ph, n = api.daynight.phase(), api.daynight.night()
  if ph ~= S.phase then
    local was = S.phase
    S.phase = ph
    if ph == "dusk" then
      S.night = n
      api.vars.set("ob_night", n)
    elseif ph == "night" then
      S.night = n
      S.night_t0 = api.time()
      S.night_len = math.floor(api.daynight.left() + 0.5)
      S.pulse = 0
      for _, b in pairs(S.buildings) do b.countered = 0; b.next_counter = 0 end
      night_plan()
      lords_night()
    elseif ph == "dawn" and was ~= nil then
      -- the sun comes up: the attackers by the buildings go inside, those in the open burn (sunlight); the lords walk
      -- home; a spreading village (stage 2) is overrun after the second night (all of them)
      go_inside()
      for _, u in pairs(S.units) do u.fire_at = nil end
      lords_dawn()
      if n == 2 then
        for _, v in ipairs(S.villages) do
          if v.stage == 2 then
            v.stage = 3
            for _, id in ipairs(v.ids) do api.objects.infect(id, 3) end
          end
        end
      end
    elseif ph == "day" and was ~= nil then
      lords_day()
    end
  end
  if ph == "night" and S.night_t0 then
    local t = api.time() - S.night_t0
    -- the rockslide: during night 2, or the first night after enough buildings are gone
    if not S.barrier.open and (S.barrier.due or (S.night >= T.breach_night and t >= T.breach_at)) then breach() end
    while S.pulse < pulses() and t >= S.pulse * T.stream_every do
      S.pulse = S.pulse + 1
      wave_pulse(S.pulse, t)
    end
  end
  if ph == "night" or ph == "dusk" then steer() end
  if ph == "night" then lords_patrol() end               -- (at dawn they walk home: lords_dawn)
  if ph == "day" or ph == "dawn" then sunlight() end
  watch(ph == "night")
end

-- ---------------------------------------------------------------- triggers
trigger "Outbreak: start" {
  enabled = true, once = true,
  conditions = { True{} },
  actions = { Script(function(api) setup() end) },
}

trigger "Outbreak: every second" {
  enabled = true,                         -- not once: every second
  conditions = { Time{ duration = 1 } },
  actions = { Script(function(api) tick() end) },
}

trigger "Outbreak: won" {
  enabled = true, once = true,
  conditions = { Timer{ timer_id = "ob_win" } },
  actions = { Win{} },
}

-- for the tests and the debug console (docs/OUTBREAK.md): the mission's state
outbreak = {
  state = S, tuning = T, layout = L,
  units = function() return count_units() end,
  left = function() return S.left end,
  breached = function() return S.barrier.open end,
  -- the classes a night's waves may bring (comma-separated), the units of a kind ("wave", "counter", ...)
  wave_classes = function(night)
    local t = {}
    for _, e in ipairs(WAVES) do
      if (e.from or 0) <= night and (e.to == nil or night <= e.to) then local c = pick(e.cls); if c then t[#t + 1] = c end end
    end
    return table.concat(t, ",")
  end,
  count = function(kind)
    local n = 0
    for _, u in pairs(S.units) do if u.kind == kind then n = n + 1 end end
    return n
  end,
  -- the units burning in the sun now
  burning = function()
    local n = 0
    for _, u in pairs(S.units) do if u.burning then n = n + 1 end end
    return n
  end,
  lords = function()
    local t = {}
    for _, lo in ipairs(S.lords) do t[#t + 1] = lo.id end
    return t
  end,
}
