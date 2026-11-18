-- Helpers of the remake's scenarios (docs/SEQUEL.md "Scenarios"): five standalone missions in ParaWorld's own world,
-- each played as any of the four tribes, on the sequel's mission runtime (M) with the game's normal rules and data.
-- mission.lua files `local C = require "scenarios"`. Class names may be lists: the first the data has is taken (M.cls)
-- - the base game lacks a few of MIRAGE's. Ported from the browser remake's game/scenarios/missions.js (its helpers).
local S = require "sequel"
local C = {}

C.TRIBES = { "Hu", "Aje", "Ninigi", "SEAS" }
C.TRIBE_NAMES = { Hu = "Norsemen", Aje = "Dustriders", Ninigi = "Dragon Clan", SEAS = "SEAS" }
-- what each tribe fields: its main building (a unit for the Dustriders), worker, heroes and units by role
C.KIT = {
  Hu = { base = "hu_fireplace", worker = "hu_worker", hero = { "Bela_s0" }, warrior = { "hu_warrior" }, archer = { "hu_archer" }, spear = { "hu_spearman" }, beast = { "hu_rhino", "hu_mammoth" },
    tower = { "hu_small_tower" }, temple = { "hu_temple" }, titan = { "hu_colossi", "hu_mammoth" } },
  Aje = { base = "aje_resource_collector", worker = "aje_worker", hero = { "Tarna_s0", "Cole_s0" }, warrior = { "aje_warrior" }, archer = { "aje_archer" }, spear = { "aje_spearman" }, beast = { "aje_stegosaurus", "aje_triceratops_archer" },
    tower = { "aje_small_tower" }, temple = { "aje_temple" }, titan = { "aje_tyrannosaurus", "aje_brachiosaurus" } },
  Ninigi = { base = "ninigi_fireplace", worker = "ninigi_worker", hero = { "Miyagi_s0", "hermit_s0" }, warrior = { "ninigi_warrior" }, archer = { "ninigi_archer" }, spear = { "ninigi_spearman" }, beast = { "ninigi_triceratops", "ninigi_sumo" },
    tower = { "ninigi_small_tower" }, temple = { "ninigi_temple" }, titan = { "ninigi_kong", "ninigi_seismosaurus" } },
  SEAS = { base = "seas_headquarters", worker = "seas_worker", hero = { "lovelace_s0" }, warrior = { "seas_warrior" }, archer = { "seas_gunner" }, spear = { "seas_marksman" }, beast = { "seas_mobile_suit", "seas_triceratops" },
    tower = { "seas_turret_tower" }, temple = { "seas_laboratory", "seas_barracks" }, titan = { "seas_rex", "seas_mobile_suit" } },
}
-- the hero's voice on the radio (cast.json) by the class the data gave
C.VOICE = { Bela_s0 = "bela", Tarna_s0 = "tarna", Cole_s0 = "cole", Miyagi_s0 = "miyagi", hermit_s0 = "hermit", lovelace_s0 = "ada" }
C.UNDEAD = { warrior = { "hu_zombie_warrior", "hu_undead_warrior" }, archer = { "hu_zombie_archer", "hu_undead_warrior" }, brute = { "hu_zombie_killer", "hu_undead_warrior" }, king = { "schliemann_zombie", "hu_undead_warrior" } }

-- the enemy tribe of a scenario: the first of its order that is not the player's
function C.other(tribe, order) for _, t in ipairs(order) do if t ~= tribe then return t end end end

-- one more for each difficulty step above normal (one less on easy), at least 1
function C.more(M, n, k) return S.more(M, n, k) end

-- a computer player's settlement put down by the script: its main building, towers, workers, an army
function C.settle(M, role, tribe, x, z, o)
  o = o or {}
  local K = C.KIT[tribe]
  local home
  if tribe == "Aje" then home = S.first(M.spawn(K.base, role, x, z, 1)) else home = M.building(K.base, role, x, z, o.rot or 0) end
  for _, d in ipairs(o.towers or { { 24, 18 }, { -24, 18 }, { 24, -18 }, { -24, -18 } }) do M.building(K.tower, role, x + d[1], z + d[2], 0) end
  M.spawn(K.worker, role, x + 6, z + 14, o.workers or 6)
  M.spawn(K.warrior, role, x - 10, z + 22, o.warriors or 5, { level = o.level or 1 })
  M.spawn(K.archer, role, x + 10, z + 22, o.archers or 4, { level = o.level or 1 })
  if o.beasts then M.spawn(K.beast, role, x, z + 30, o.beasts, { level = o.level or 1 }) end
  if o.hero then M.spawn(K.hero, role, x, z + 10, 1, { level = o.level or 2 }) end
  return home
end

-- the fighters of a role, without workers, the Dustriders' resource collector and envoys
function C.army(M, role)
  return M.units(role, function(u) return not u.worker and not u.name:find("resource_collector") and not u.envoy end)
end

-- a creature that keeps to a place (it takes on what comes within r of it); o.hp: hit points factor, o.scale: drawn bigger
function C.beast(M, cls, x, z, o)
  o = o or {}
  local u = S.first(M.spawn(cls, "outbreak", x, z, 1))
  if not u then return nil end
  if o.hp then M.scale_hp(u, o.hp) end
  if o.scale then M.set(u, "scale", o.scale) end
  M.set(u, "home", { x, z, o.r or 35 })
  return u
end

function C.guards(M, list, x, z, r, tag) return S.guards(M, list, x, z, r, tag, 14) end

-- lost when the player's main building (the Dustriders' collector) is gone and no worker is left to start again
function C.homeLost(M)
  if M.time < 5 then return false end
  return M.home("me") == nil and #M.units("me", { worker = true }) == 0
end

function C.hero(M) return S.first(M.units("me", M.state.heroCls)) end
function C.startHero(M)
  M.state.heroCls = M.cls(C.KIT[M.tribe].hero)
  M.state.v = C.VOICE[M.state.heroCls] or "bela"
end

-- a strike out of the sky (the volcano's): it hits everybody within 9 m
function C.strike(M, x, z, dmg)
  dmg = dmg or 220
  M.fx("explosion", x, z, 1.6) M.fx("dust", x, z, 3)
  M.area_damage(x, z, 9, dmg, dmg * 0.25, "outbreak", 8)
end

return C
