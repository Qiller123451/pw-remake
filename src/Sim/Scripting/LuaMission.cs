// A mission folder as the game loads it (docs/LUA_MAPS.md, docs/MAP_FORMAT.md; the folder is a map of the remake's
// format - its terrain is read by the map source, Maps/MapFiles):
//
//   map.lua           `mission = { ... }`: what the menu shows and the setup needs (the campaign reads it as JSON nodes)
//   world.lua         objects, regions, groups, quests, question marks, variables, dialogues, sequences, and the
//                     parameter defaults of the trigger node types ("trigger_defaults") - Lua data (Maps/LuaData)
//   triggers.lua      the triggers (declarative: `trigger "name" { ... }`)
//   custom.lua        optional: own code
//   texts/<lang>.lua  the texts
//
// Load() runs the Lua files in the sandbox (LuaHost) and turns every `trigger` into a TriggerDef; the C# engine
// (TriggerEngine) is built from them. Bind() then gives the Lua code the Game API (GameApi.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MoonSharp.Interpreter;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Scripting;

public sealed class LuaMission
{
    static readonly Logger L = Log.For("Lua.Mission");
    public readonly string Folder;
    public readonly LuaHost Lua;
    public readonly MissionScript Script = new();
    public JsonNode? Mission, World;
    /// <summary>All files loaded without a Lua error.</summary>
    public bool Ok = true;
    Table? nodeMeta;

    LuaMission(string folder, LuaHost lua) { Folder = folder; Lua = lua; }

    /// <summary>The default standard library folder: game/lua/std next to the running game (or PW_LUA_STD).</summary>
    public static string DefaultStdDir()
    {
        var env = Environment.GetEnvironmentVariable("PW_LUA_STD");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "game", "lua", "std");
            if (Directory.Exists(c)) return c;
            c = Path.Combine(d.FullName, "lua", "std");
            if (Directory.Exists(c)) return c;
        }
        return Path.Combine("game", "lua", "std");
    }

    /// <summary>Loads a mission folder: JSON data, texts of the language (fallback uk, then any), the std library,
    /// triggers.lua and custom.lua.</summary>
    public static LuaMission Load(string folder, string? stdDir = null, string lang = "uk")
    {
        if (Maps.PwMapReader.IsOldMissionFolder(folder))
            throw new InvalidOperationException($"{folder}: a mission folder of the old layout (mission.json, world.json) - run Prepare again to convert it to the map format (docs/MAP_FORMAT.md)");
        var lua = new LuaHost(stdDir ?? DefaultStdDir(), folder);
        var m = new LuaMission(folder, lua);
        m.Mission = Maps.PwMapReader.MissionOf(folder);
        m.World = Maps.PwMapReader.WorldOf(folder);
        if (m.World != null) MissionScript.ReadCommon(m.Script, m.World, m.World["trigger_defaults"]);
        m.Script.Name = m.Mission?["key"].Str() ?? Path.GetFileName(folder.TrimEnd('/', '\\'));
        foreach (var (k, v) in Maps.PwMapReader.TextsOf(folder, lang)) m.Script.Texts[k] = v;
        m.Install();
        if (lua.RunString("require 'triggers'", "std") == null) m.Ok = false;
        foreach (var file in new[] { "triggers.lua", "custom.lua" })
        {
            var p = Path.Combine(folder, file);
            if (File.Exists(p) && !lua.RunFile(p)) m.Ok = false;
        }
        return m;
    }

    /// <summary>Only the Lua part (tests, tools): a host with the std library and the given source as triggers.lua.</summary>
    public static LuaMission FromSource(string source, string? stdDir = null, string chunk = "triggers.lua", MissionScript? data = null)
    {
        var lua = new LuaHost(stdDir ?? DefaultStdDir(), null);
        var m = new LuaMission("", lua);
        if (data != null)
        {
            m.Script.Quests.AddRange(data.Quests); m.Script.Variables.AddRange(data.Variables);
            foreach (var (k, v) in data.Texts) m.Script.Texts[k] = v;
            foreach (var (k, v) in data.ConditionDefaults) m.Script.ConditionDefaults[k] = v;
            foreach (var (k, v) in data.ActionDefaults) m.Script.ActionDefaults[k] = v;
        }
        m.Install();
        if (lua.RunString("require 'triggers'", "std") == null) m.Ok = false;
        if (lua.RunString(source, chunk) == null) m.Ok = false;
        return m;
    }

    /// <summary>Gives the Lua code the Game API of a running engine (Script / Check, the global `api`).</summary>
    public void Bind(TriggerEngine engine)
    {
        engine.Lua = Lua;
        Lua.Random = engine.Host.Random;
        Lua.Api = GameApi.Create(engine, Lua);
        Lua.Lua.Globals["api"] = Lua.Api;
    }

    // ------------------------------------------------------------------------------------------- the __pw table
    void Install()
    {
        var S = Lua.Lua;
        var pw = new Table(S);
        var types = new Table(S);
        int i = 1;
        foreach (var e in TriggerTypes.All)
        {
            var t = new Table(S);
            t["code"] = e.Code; t["name"] = e.Name; t["cond"] = e.IsCondition; t["act"] = !e.IsCondition;
            types[i++] = t;
        }
        pw["types"] = types;
        pw["is_condition"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(TriggerTypes.IsCondition(a[0].CastToString() ?? "")));
        pw["is_action"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(TriggerTypes.IsAction(a[0].CastToString() ?? "")));
        pw["trigger"] = DynValue.NewCallback((ctx, a) =>
        {
            if (a.Count < 1 || a[0].Type != DataType.String) throw new ScriptRuntimeException("trigger: expects a name: trigger \"name\" { ... }");
            var name = a[0].String;
            var where = ctx.CallingLocation?.FormatLocation(S) ?? "";
            return DynValue.NewCallback((c2, b) =>
            {
                if (b.Count < 1 || b[0].Type != DataType.Table) throw new ScriptRuntimeException($"trigger \"{name}\": expects a table {{ ... }}");
                Script.Triggers.Add(Define(name, b[0].Table, where));
                return DynValue.Nil;
            });
        });
        S.Globals["__pw"] = pw;
        nodeMeta = null;
    }

    static readonly HashSet<string> TriggerFields = new(StringComparer.Ordinal)
        { "guid", "folder", "description", "enabled", "once", "random", "by_difficulty", "node_off", "expr", "conditions", "actions" };

    TriggerDef Define(string name, Table def, string where)
    {
        nodeMeta ??= Lua.Lua.Globals.Get("__pw").Table?.Get("node_meta").Table;
        string Err(string m) => $"trigger \"{name}\": {m}";
        foreach (var k in def.Keys)
            if (k.Type != DataType.String || !TriggerFields.Contains(k.String)) throw new ScriptRuntimeException(Err($"unknown field {k.ToPrintString()} (allowed: {string.Join(", ", TriggerFields)})"));
        bool Flag(string k)
        {
            var v = def.Get(k);
            return v.Type switch { DataType.Nil => false, DataType.Boolean => v.Boolean, DataType.Number => v.Number != 0, _ => throw new ScriptRuntimeException(Err($"{k} must be true or false")) };
        }
        string Str(string k, string d)
        {
            var v = def.Get(k);
            if (v.IsNil()) return d;
            return LuaHost.ParamString(v) ?? throw new ScriptRuntimeException(Err($"{k} must be a string"));
        }
        var t = new TriggerDef
        {
            Name = name, Guid = Str("guid", ""), Folder = Str("folder", "Root"), Description = Str("description", ""), Expression = Str("expr", ""),
            Enabled = Flag("enabled"), Once = Flag("once"), Random = Flag("random"), ByDifficulty = Flag("by_difficulty"), NodeOff = Flag("node_off"), Source = where,
        };
        if (t.Folder.Length == 0) t.Folder = "Root";
        Nodes(def.Get("conditions"), true, t.Conditions, Err);
        Nodes(def.Get("actions"), false, t.Actions, Err);
        // the expression must parse (errors at load time, with the line of the trigger)
        try { TriggerExpr.Compile(t.Expression, t.Conditions.Count); }
        catch (FormatException e) { throw new ScriptRuntimeException(Err(e.Message)); }
        return t;
    }

    void Nodes(DynValue list, bool cond, List<NodeDef> into, Func<string, string> err)
    {
        string what = cond ? "conditions" : "actions";
        if (list.IsNil()) return;
        if (list.Type != DataType.Table) throw new ScriptRuntimeException(err($"{what} must be a list {{ ... }}"));
        int n = list.Table.Length;
        foreach (var k in list.Table.Keys)
            if (k.Type != DataType.Number || k.Number < 1 || k.Number > n || k.Number != Math.Floor(k.Number)) throw new ScriptRuntimeException(err($"{what} must be a list (found the key {k.ToPrintString()})"));
        for (int i = 1; i <= n; i++)
        {
            var v = list.Table.Get(i);
            if (v.Type != DataType.Table || (nodeMeta != null && v.Table.MetaTable != nodeMeta))
                throw new ScriptRuntimeException(err($"{what}[{i}] is not a condition / action (write e.g. Time{{ duration = 1 }})"));
            var t = v.Table;
            var code = t.Get("code").CastToString() ?? "";
            var nm = t.Get("name").CastToString() ?? code;
            if (cond ? !t.Get("cond").CastToBool() : !t.Get("act").CastToBool())
                throw new ScriptRuntimeException(err($"{what}[{i}]: {nm} is {(cond ? "an action" : "a condition")}, not {(cond ? "a condition" : "an action")}"));
            var nd = new NodeDef { Code = code };
            if (code == "SCRP" || code == "CHCK")
            {
                nd.Function = t.Get("fn"); nd.Lua = Lua;
                var ev = t.Get("every");
                if (ev.Type == DataType.Number && ev.Number > 0) nd.Every = ev.Number;
                var df = t.Get("difficulty");
                if (!df.IsNil()) nd.Difficulty = Difficulty(df, err, what, i);
            }
            else
            {
                var p = t.Get("p").Table;
                if (p != null)
                    foreach (var kv in p.Pairs)
                    {
                        if (kv.Key.Type != DataType.String) throw new ScriptRuntimeException(err($"{what}[{i}] {nm}: parameter names must be strings"));
                        var key = kv.Key.String;
                        // (the original also stores the action's difficulty among its parameters: the base attribute
                        // `difficulty` of action_attrib_def.txt, always equal to the compiled one)
                        if (!cond && key == "difficulty") { nd.Difficulty = Difficulty(kv.Value, err, what, i); nd.P["difficulty"] = nd.Difficulty.ToString(); continue; }
                        if (key == "note") { nd.Note = kv.Value.CastToString() ?? ""; continue; }
                        nd.P[key] = LuaHost.ParamString(kv.Value) ?? throw new ScriptRuntimeException(err($"{what}[{i}] {nm}: {key} must be a string, number or boolean"));
                    }
            }
            into.Add(nd);
        }
    }

    static int Difficulty(DynValue v, Func<string, string> err, string what, int i)
    {
        if (v.Type == DataType.Number && v.Number is 0 or 1 or 2) return (int)v.Number;
        if (v.Type == DataType.String)
            switch (v.String.ToLowerInvariant()) { case "easy": case "0": return 0; case "medium": case "normal": case "1": return 1; case "hard": case "2": return 2; }
        throw new ScriptRuntimeException(err($"{what}[{i}]: difficulty must be \"easy\", \"medium\" or \"hard\""));
    }
}
