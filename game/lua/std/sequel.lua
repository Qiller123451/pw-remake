-- Helpers of the remake's own campaigns' missions (docs/SEQUEL.md "Mission API"): mission.lua files
-- `local S = require "sequel"`. The mission API itself is `M` (src/Sim/Sequel/SequelLua.cs).
-- Ported from the browser remake's game/sequel/missions.js and missions_1966.js (their shared helpers).
local S = {}

S.T = 60                                      -- a minute
S.CIVS = { "sq_civilian_1", "sq_civilian_2", "sq_civilian_3", "sq_civilian_4" }

-- distance test on an info table (M.info) or anything with x and z
function S.near(u, x, z, r)
  local dx, dz = u.x - x, u.z - z
  return math.sqrt(dx * dx + dz * dz) <= r
end
function S.dist(ax, az, bx, bz) local dx, dz = ax - bx, az - bz return math.sqrt(dx * dx + dz * dz) end

-- is the object alive (an id or nil)
function S.alive(M, id) return id ~= nil and M.alive(id) end

-- one more for each difficulty step above normal (one less on easy), at least 1
function S.more(M, n, k, easy)
  k = k or 1
  local d = M.difficulty
  return math.max(1, n + (d == 2 and k or (d == 0 and -(easy or k) or 0)))
end

-- a value of a list by difficulty: S.by(M, {8, 12, 14})
function S.by(M, list) return list[M.difficulty + 1] end

-- townsfolk holed up in their houses: safe until a unit of the player finds them, then they run for the evacuation point
function S.civilians(M, x, z, n, spread)
  spread = spread or 20
  local out = {}
  for i = 0, n - 1 do
    local ids = M.spawn(S.CIVS[i % 4 + 1], "civilians", x + (math.random() - 0.5) * spread, z + (math.random() - 0.5) * spread, 1)
    for _, u in ipairs(ids) do M.set(u, "untargetable", true) M.set(u, "hiding", true) out[#out + 1] = u end
  end
  return out
end

-- the number of finished buildings of a class of the player's
function S.built(M, name) return #M.buildings("me", { name = name, built = true }) end

-- the first entry of a list, or nil
function S.first(list) return list and list[1] or nil end

-- appends the elements of b to a (returns a)
function S.concat(a, b) for _, v in ipairs(b or {}) do a[#a + 1] = v end return a end

-- does a list contain v
function S.has(list, v) for _, x in ipairs(list or {}) do if x == v then return true end end return false end

-- the elements of a list for which f is true
function S.filter(list, f) local o = {} for _, v in ipairs(list or {}) do if f(v) then o[#o + 1] = v end end return o end

-- does any element pass f
function S.any(list, f) for _, v in ipairs(list or {}) do if f(v) then return true end end return false end

-- the alive objects of a list of ids
function S.living(M, list) return S.filter(list, function(id) return M.alive(id) end) end

-- minutes:seconds
function S.fmt(s) s = math.max(0, s) return string.format("%d:%02d", math.floor(s / 60), math.floor(s % 60)) end

-- creatures that keep to a place (they take on what comes within r of it): list of {class, count}
function S.guards(M, list, x, z, r, tag, spread)
  spread = spread or 16
  local out = {}
  for _, g in ipairs(list) do
    for _, u in ipairs(M.spawn(g[1], "outbreak", x + (math.random() - 0.5) * spread, z + (math.random() - 0.5) * spread, g[2])) do
      M.set(u, "home", { x, z, r }) if tag then M.set(u, "tag", tag) end out[#out + 1] = u
    end
  end
  return out
end

-- Crane's men holding a place: list of {class, count, level}
function S.post(M, list, x, z, r, extra)
  local out = {}
  for _, g in ipairs(list) do
    local o = { level = g[3] or 2 }
    for k, v in pairs(extra or {}) do o[k] = v end
    for _, u in ipairs(M.spawn(g[1], "crane", x, z, g[2], o)) do if r then M.set(u, "home", { x, z, r }) end out[#out + 1] = u end
  end
  return out
end

-- the field medics' work after a fight: the player's units within r (all with r nil) get their hit points back
function S.heal(M, x, z, r)
  for _, id in ipairs(M.units("me")) do
    local u = M.info(id)
    if r == nil or S.near(u, x, z, r) then M.set_hp(id, u.maxhp) end
  end
end

-- is a hero of the player's alive
function S.hero_alive(M, name) return #M.units("me", name) > 0 end

return S
