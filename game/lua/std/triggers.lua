-- The declarative trigger syntax of the maps (docs/LUA_MAPS.md). Loaded before a mission's triggers.lua.
--
--   trigger "Gg01: Spawn Aje Group" {
--     folder = "Root/G: Gameplay", once = true, by_difficulty = true, expr = "1 && (2 || !3)",
--     conditions = { Timer{ timer_id = 0 } },
--     actions = {
--       SpawnGroup{ difficulty = "easy", classes_0 = "aje_warrior 2 3", group = "...", pos = "512 0 300" },
--       SetTrigger{ trigger = "Gg02: Wave dead", state = 1 },
--     },
--   }
--
-- Every condition and action type of the original has a constructor here, under its readable name (Time, Region,
-- SpawnGroup ...) and under its 4-letter code (TIME, REGN, SPGR ...). A constructor only records which type with
-- which parameters; the behaviour is the C# class of that type (src/Sim/Scripting/Conditions.cs, Actions.cs),
-- built when the file has been read. The table of names comes from the engine (TriggerTypes.cs), so the names
-- here, in the converter and in the documentation are always the same.
local pw = __pw

local NodeMT = {}          -- metatable of the condition / action nodes the constructors return
pw.node_meta = NodeMT

local function constructor(code, name, cond, act)
  return function(p)
    if type(p) ~= "table" then
      error(name .. ": expects a table of parameters, e.g. " .. name .. "{ key = value }", 2)
    end
    return setmetatable({ code = code, name = name, cond = cond, act = act, p = p }, NodeMT)
  end
end

-- readable names: each is a condition or an action (Timer and Diplomacy are both: the list decides)
local byName = {}
for _, t in ipairs(pw.types) do
  local e = byName[t.name]
  if e then
    e.cond = e.cond or t.cond
    e.act = e.act or t.act
  else
    byName[t.name] = { code = t.code, cond = t.cond, act = t.act }
  end
end
for name, e in pairs(byName) do
  if name ~= "Check" and name ~= "Script" then _G[name] = constructor(e.code, name, e.cond, e.act) end
end
-- the 4-letter codes: TRIG{...}, REGN{...}; a code that is a condition and an action (TIMR, DIPL, TECH, QUES)
-- takes its meaning from the list it is in
for _, t in ipairs(pw.types) do
  if t.code ~= "CHCK" and t.code ~= "SCRP" then
    _G[t.code] = constructor(t.code, t.code, pw.is_condition(t.code), pw.is_action(t.code))
  end
end

--- Node("XXXX"){ ... }: a node of a type this list does not know (the engine warns once and ignores it).
function Node(code)
  return constructor(code, code, true, true)
end

--- Script(function(api) ... end): an action that runs Lua code (the Game API: docs/LUA_MAPS.md).
function Script(fn, opts)
  if type(fn) ~= "function" then error("Script: expects a function: Script(function(api) ... end)", 2) end
  local n = setmetatable({ code = "SCRP", name = "Script", cond = false, act = true, fn = fn, p = {} }, NodeMT)
  if type(opts) == "table" then n.difficulty = opts.difficulty end
  return n
end

--- Check(function(api) return <bool> end [, seconds]): a condition written in Lua, evaluated when the trigger is
--- enabled and then every `seconds` (default 0.5).
function Check(fn, every)
  if type(fn) ~= "function" then error("Check: expects a function: Check(function(api) return ... end)", 2) end
  return setmetatable({ code = "CHCK", name = "Check", cond = true, act = false, fn = fn, every = every, p = {} }, NodeMT)
end

--- trigger "name" { ... }: defines a trigger (implemented by the engine: it checks the fields and builds it).
trigger = pw.trigger

-- difficulty names for actions (difficulty = "easy" | "medium" | "hard", or 0 | 1 | 2)
EASY, MEDIUM, HARD = 0, 1, 2

-- A capitalised name that is not defined is almost always a misspelt type (Tme{ ... }): say so, with the line.
setmetatable(_G, { __index = function(_, k)
  if type(k) == "string" and k:match("^%u") then
    error("unknown name '" .. k .. "' (no condition / action type of that name: docs/LUA_MAPS.md lists them)", 2)
  end
  return nil
end })
