// The Lua side of the maps: the sandbox (no io / os / file loading, the instruction limit, errors with file:line
// that do not stop the game), the declarative trigger syntax (readable names and 4-letter codes, checks with useful
// messages), Script / Check and the Game API inside a running engine, and that the readable-name tables of the
// engine and of the converter agree.
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PwRemake.Sim.Scripting;

namespace PwRemake.Tests;

public static class Repo
{
    /// <summary>The repository root (from this source file's location).</summary>
    public static string Root([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    public static string Std => Path.Combine(Root(), "game", "lua", "std");
}

[TestClass]
public static class ScriptingLuaTests
{
    static LuaHost Host() => new(Repo.Std, null) { InstructionLimit = 200_000 };

    public static void SandboxHasNoDangerousModules(TestContext t)
    {
        var L = Host();
        foreach (var name in new[] { "io", "os", "debug", "load", "loadfile", "dofile", "loadstring", "collectgarbage", "dynamic", "coroutine" })
        {
            var r = L.RunString($"return {name} == nil", "probe.lua");
            t.True(r != null && r.Boolean, $"{name} is not available");
        }
        t.True(L.RunString("return string.dump == nil", "probe.lua")!.Boolean, "string.dump is not available");
        t.True(L.RunString("return math.floor(2.5) == 2 and string.format('%d', 3) == '3' and #table.concat({'a','b'}) == 2", "probe.lua")!.Boolean, "math, string and table work");
        var n = L.Errors.Count;
        t.True(L.RunString("io.open('/etc/passwd')", "evil.lua") == null && L.Errors.Count == n + 1, "using io is a Lua error, logged");
        t.True(L.RunString("require '../../etc/passwd'", "evil.lua") == null, "require cannot leave the mission folder / std");
        t.True(L.RunString("require 'nonexistent_module'", "evil.lua") == null, "require of a missing module is an error");
        t.True(L.RunString("return string.rep('x', 10000000)", "evil.lua") == null, "a giant string.rep is refused");
        var dir = Path.Combine(Path.GetTempPath(), "pw_lua_req_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "helper.lua"), "calls = (calls or 0) + 1 return 42");
        var M = new LuaHost(Repo.Std, dir);
        t.True(M.RunString("return require('helper') + require('helper') == 84 and calls == 1", "probe.lua")!.Boolean, "require loads a module of the mission folder once");
        Directory.Delete(dir, true);
    }

    public static void InstructionLimitStopsLoops(TestContext t)
    {
        var L = Host();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        t.True(L.RunString("while true do end", "loop.lua") == null, "an endless loop ends with an error");
        t.True(L.Errors.Last().Contains("loop.lua:1") && L.Errors.Last().Contains("instructions"), $"the error names file:line and the limit ({L.Errors.Last()})");
        t.True(L.RunString("while true do pcall(function() while true do end end) end", "loop2.lua") == null, "a pcall inside the loop cannot catch the limit");
        t.True(L.RunString("local function f() return f() + 1 end return f()", "rec.lua") == null, "endless recursion is an error");
        t.True(sw.ElapsedMilliseconds < 20000, $"quick ({sw.ElapsedMilliseconds} ms)");
        t.True(L.RunString("local s = 0 for i = 1, 1000 do s = s + i end return s", "ok.lua")!.Number == 500500, "the next call runs normally (the budget is per call)");
    }

    public static void LuaErrorsHaveFileAndLine(TestContext t)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pw_lua_err_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "triggers.lua"), "trigger \"a\" { conditions = { True{} }, actions = {} }\n\ntrigger \"b\" { conditions = { SetVar{ varname = 'x' } } }\n");
        var m = LuaMission.Load(dir, Repo.Std);
        t.True(!m.Ok && m.Lua.Errors.Any(e => e.Contains("triggers.lua:3") && e.Contains("SetVar is an action, not a condition")), $"an action in the condition list: error at triggers.lua:3 ({string.Join(" | ", m.Lua.Errors)})");
        t.Equal(1, m.Script.Triggers.Count, "the triggers before the error exist");
        File.WriteAllText(Path.Combine(dir, "triggers.lua"), "trigger \"a\" { conditons = {} }");
        m = LuaMission.Load(dir, Repo.Std);
        t.True(m.Lua.Errors.Any(e => e.Contains("unknown field") && e.Contains("conditons")), "a misspelt field is an error");
        File.WriteAllText(Path.Combine(dir, "triggers.lua"), "trigger \"a\" { conditions = { True{} }, expr = '1 &&' }");
        m = LuaMission.Load(dir, Repo.Std);
        t.True(m.Lua.Errors.Any(e => e.Contains("bad expression")), "a bad expression is an error at load time");
        File.WriteAllText(Path.Combine(dir, "triggers.lua"), "trigger \"a\" { conditions = { Tme{ duration = 1 } } }");
        m = LuaMission.Load(dir, Repo.Std);
        t.True(m.Lua.Errors.Any(e => e.Contains("triggers.lua:1") && e.Contains("Tme")), "an unknown constructor names itself and the line");
        Directory.Delete(dir, true);
    }

    public static void DeclarativeSyntax(TestContext t)
    {
        var m = LuaMission.FromSource(@"
trigger ""Gg01: Spawn"" {
  guid = ""abc"", folder = ""Root/G: Gameplay"",
  enabled = true, once = true, by_difficulty = true, expr = ""1 && (2 || !3)"",
  conditions = { Timer{ timer_id = 0 }, TIME{ duration = 1.5 }, Var{ varname = ""n"", operation = ""=="", value = 2, ['local'] = 1 } },
  actions = {
    SpawnGroup{ difficulty = ""easy"", classes_0 = ""aje_warrior 2 3"", pos = ""512 0 300"" },
    SPGR{ difficulty = 2, classes_0 = ""aje_warrior 2 5"" },
    Timer{ timer_id = 3, duration = 10 },
    Diplomacy{ changes = ""0|2|1\n1|2|0"" },
    SetTrigger{ trigger = ""Gg02"", state = ""on"" },
    Node(""ZZZZ""){ x = true },
  },
}");
        t.True(m.Ok, "loads: " + string.Join(" | ", m.Lua.Errors));
        var d = m.Script.Triggers.Single();
        t.True(d.Name == "Gg01: Spawn" && d.Guid == "abc" && d.Folder == "Root/G: Gameplay" && d.Enabled && d.Once && d.ByDifficulty && !d.Random && d.Expression == "1 && (2 || !3)", "the trigger fields");
        t.Equal("TIMR TIME CVAR", string.Join(" ", d.Conditions.Select(c => c.Code)), "condition codes (Timer in the condition list = TIMR condition)");
        t.Equal("SPGR SPGR TIMR DIPL TRIG ZZZZ", string.Join(" ", d.Actions.Select(c => c.Code)), "action codes");
        t.True(d.Conditions[1].P["duration"] == "1.5" && d.Conditions[0].P["timer_id"] == "0" && d.Conditions[2].P["local"] == "1" && d.Conditions[2].P["value"] == "2", "numbers become the data's strings");
        t.True(d.Actions[0].Difficulty == 0 && d.Actions[1].Difficulty == 2 && d.Actions[2].Difficulty == 1 && d.Actions[0].P["difficulty"] == "0", "difficulty by name or number, default medium (also the base attribute difficulty)");
        t.Equal("0|2|1\n1|2|0", d.Actions[3].P["changes"], "multi-line strings");
        t.Equal("1", d.Actions[5].P["x"], "booleans become 1 / 0");
    }

    public static void ScriptCheckAndGameApi(TestContext t)
    {
        var m = LuaMission.FromSource(@"
local spawned
trigger ""start"" { enabled = true, once = true,
  conditions = { True{} },
  actions = {
    SetVar{ varname = ""n"", operation = ""set"", value = 1, ['local'] = 1 },
    Script(function(api)
      api.vars.add(""n"", 2)
      spawned = api.units.spawn(""hu_warrior"", 1, 40, 50, 2)
      api.ui.infobar(""hello"")
      api.action(""Quest"", { quest_name = ""Q1"", dest_state = 0 })
    end),
  },
}
trigger ""watch"" { enabled = true, once = true,
  conditions = { Check(function(api) return api.vars.number(""n"") >= 3 and api.objects.info(spawned).level == 2 end) },
  actions = { Script(function(api) api.triggers.enable(""bad"") end), Win{} },
}
trigger ""bad"" { once = true, conditions = { True{} }, actions = { Script(function(api) while true do end end), SetVar{ varname = ""after"", value = ""yes"", ['local'] = 1 } } }
", Repo.Std, data: TB.Mission(Array.Empty<TriggerDef>(), quests: new[] { new QuestState { Guid = "q1", Name = "Q1" } }));
        t.True(m.Ok, "loads: " + string.Join(" | ", m.Lua.Errors));
        var h = new FakeMissionHost();
        var e = new TriggerEngine(h, m.Script);
        h.E = e;
        m.Lua.InstructionLimit = 100_000;
        m.Bind(e);
        h.Run(1.5);
        t.Equal("3", e.GetVarOrNull("n"), "Script runs after the actions before it; api.vars");
        t.True(h.Calls.Contains("Spawn hu_warrior 1 40 50 L2") && h.InfoBar == "hello", "api.units.spawn, api.ui.infobar");
        t.True(e.Quest("Q1")!.Visible, "api.action runs any action type by its C# class");
        t.True(h.Ended is { Won: true }, "Check: the Lua condition fired its trigger");
        t.True(m.Lua.Errors.Any(x => x.Contains("instructions")) && e.GetVarOrNull("after") == "yes", "an endless Script is stopped, logged, and the trigger's other actions run");
    }

    public static void NameTablesAgree(TestContext t)
    {
        var py = File.ReadAllText(Path.Combine(Repo.Root(), "tools", "prepare", "campaign_to_lua.py"));
        (string, string)[] Table(string name)
        {
            var m = Regex.Match(py, name + @" = \{(.*?)\n\}", RegexOptions.Singleline);
            return Regex.Matches(m.Groups[1].Value, @"'([A-Z_]{4})': '(\w+)'").Select(x => (x.Groups[1].Value, x.Groups[2].Value)).ToArray();
        }
        var pc = Table("CONDITION_NAMES"); var pa = Table("ACTION_NAMES");
        var cc = TriggerTypes.All.Where(e => e.IsCondition && e.Origin != "lua").Select(e => (e.Code, e.Name)).ToArray();
        var ca = TriggerTypes.All.Where(e => !e.IsCondition && e.Origin != "lua").Select(e => (e.Code, e.Name)).ToArray();
        t.True(pc.Length > 20 && pc.OrderBy(x => x).SequenceEqual(cc.OrderBy(x => x)), "the converter's condition names = TriggerTypes");
        t.True(pa.Length > 40 && pa.OrderBy(x => x).SequenceEqual(ca.OrderBy(x => x)), "the converter's action names = TriggerTypes");
        t.Equal(23, TriggerTypes.Campaign.Count(e => e.IsCondition), "23 condition types of the campaign");
        t.Equal(44, TriggerTypes.Campaign.Count(e => !e.IsCondition), "44 action types of the campaign");
        foreach (var e in TriggerTypes.All)
            t.True(e.IsCondition ? TriggerTypes.CreateCondition(e.Code) != null : TriggerTypes.CreateAction(e.Code) != null, $"{e.Code} has a class");
        var doc = File.ReadAllText(Path.Combine(Repo.Root(), "docs", "LUA_MAPS.md"));
        foreach (var e in TriggerTypes.All) t.True(doc.Contains("`" + e.Name + "`") && doc.Contains(e.Code), $"docs/LUA_MAPS.md lists {e.Name} / {e.Code}");
    }
}
