// Golden tests of the computer player's tables: the JS remake's ai/data.js (run in node by
// tests/golden/ai_data_dump.mjs) against the C# AiData (src/Sim/Ai/Data.cs), for both configurations and for the
// built-in fallbacks (no ai.json): difficulty rows, gifts, workers, squads, behaviours, build lists, counter ships,
// attack plans, armies, unit mixes, housing, outposts, hero requests, spawn levels - every query of the dump.
//
// Needs node and the JS sources (PW_REMAKE_SRC, default /home/claude/pw-exporter/remake/src) and the data folders
// (PW_DATA / PW_DATA_MIRAGE, default /opt/data/base, /opt/data/mirage).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Core;

namespace PwRemake.Tests;

[TestClass]
public static class AiDataGoldenTests
{
    public static void AiDataGoldenBase(TestContext t) => Run(t, RulesTestData.Base(t));
    public static void AiDataGoldenMirage(TestContext t) => Run(t, RulesTestData.Mirage(t));
    public static void AiDataGoldenFallback(TestContext t) => Run(t, "-");

    static void Run(TestContext t, string data)
    {
        var dump = Dump(t, data);
        var A = data == "-" ? AiData.Fallback : AiData.Load(data);
        t.Equal(dump["hasAiData"].Bool(), A.HasAiData, "hasAiData");
        int n = 0, bad = 0;
        var fails = new List<string>();
        foreach (var q in (JsonArray)dump["queries"]!)
        {
            string op = q!["op"].Str(); var args = (JsonArray)q["args"]!;
            JsonNode? mine = Query(A, op, args);
            n++;
            if (!Same(q["r"], mine)) { bad++; if (fails.Count < 8) fails.Add($"{op}({Show(args)}): JS {Show(q["r"])} / C# {Show(mine)}"); }
        }
        foreach (var f in fails) t.Log(f);
        t.Log($"{n} queries, {bad} differences");
        t.Equal(0, bad, "differences to the JS tables");
    }

    static JsonNode? Query(AiData A, string op, JsonArray a)
    {
        switch (op)
        {
            case "level": { var l = A.Level(a[0].Num()); return Obj(("class", l.Class), ("wait", l.Wait), ("controlWait", l.ControlWait), ("gift", Obj(("food", l.GiftFood), ("wood", l.GiftWood), ("stone", l.GiftStone), ("skulls", l.GiftSkulls))),
                ("attackStrength", l.AttackStrength), ("quickAttackStrength", l.QuickAttackStrength), ("pyramidAttackUnits", l.PyramidAttackUnits), ("gather", l.Gather), ("buildTime", l.BuildTime), ("researchTime", l.ResearchTime),
                ("unitLimit", Obj(("levels", new JsonArray(l.UnitLimitLevels.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())), ("total", l.UnitLimitTotal))), ("weaponDuration", l.WeaponDuration)); }
            case "gift": { var g = A.Gift(a[0].Num(), a[1].Bool()); return Obj(("food", g["food"]), ("wood", g["wood"]), ("stone", g["stone"]), ("skulls", g["skulls"])); }
            case "maxWorkers": return A.MaxWorkers(a[0].Str(), a[1].Int());
            case "squadTimeout": return A.SquadTimeout(a[0].Str());
            case "kindergartenWait": return A.KindergartenWait(a[0].Str());
            case "squad": { var s = A.Squad(a[0].Str(), a[1].Str()); return Obj(("min", s.Min), ("max", s.Max), ("bad", s.Bad)); }
            case "behaviour":
            {
                var b = A.Behaviour(a[0].Str());
                if (b == null) return null;
                return Obj(("params", new JsonArray(b.Params.Select(p => (JsonNode?)new JsonArray(JsonValue.Create(p.Key), p.Value?.DeepClone())).ToArray())), ("paused", b.Paused), ("economyOff", b.EconomyOff), ("brainWash", b.BrainWash),
                    ("variants", b.Variants), ("kindergarten", b.Kindergarten), ("personality", b.Personality), ("valueTable", b.ValueTable), ("subStrategy", b.SubStrategy));
            }
            case "behaviourNames": return new JsonArray(A.BehaviourNames.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            case "buildList": return Rows(A.BuildList(Ctx(A, a[0]!)));
            case "counterShips": return Rows(A.CounterShips(Ctx(A, a[0]!)));
            case "attackPlan": { var p = A.AttackPlan(Ctx(A, a[0]!)); return Obj(("attacks", new JsonArray(p.Attacks.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())), ("guerilla", p.Guerilla)); }
            case "hasArmies": return A.HasArmies(a[0].Str());
            case "army": return Army(A.Army(a[0].Str(), a[1].Str(), a[2].Str()));
            case "armyAny": return Army(A.ArmyAny(a[0].Str()));
            case "unitMix":
            {
                var m = A.UnitMix(a[0].Str(), a[1].Int());
                if (m == null) return null;
                var o = new JsonObject();
                foreach (var k in m.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    o[k] = new JsonArray(m[k].Select(g => (JsonNode?)Obj(("weight", g.Weight), ("units", new JsonArray(g.Units.Select(u => (JsonNode?)Obj(("cls", u.Cls), ("npc", u.Npc), ("objFlag", u.ObjFlag))).ToArray())))).ToArray());
                return o;
            }
            case "housing": return Rows(A.Housing(a[0].Str()));
            case "outpost": { var o = A.Outpost(a[0].Str(), a[1].Str()); return o == null ? null : new JsonArray(o.Select(r => (JsonNode?)new JsonArray(JsonValue.Create(r.Request), JsonValue.Create(r.ObjFlag ?? ""), JsonValue.Create(r.Count != 0 ? r.Count : 1))).ToArray()); }
            case "heroRequests": return Rows(A.HeroRequests(a[0].Int()));
            case "spawnLevel": return A.SpawnLevel(a[0].Str());
        }
        throw new Exception("unknown query " + op);
    }

    static AiCtx Ctx(AiData A, JsonNode c)
    {
        var has = ((JsonArray)c["has"]!).Select(x => x.Str()).ToHashSet();
        return new AiCtx
        {
            Tribe = c["tribe"].Str(), Letter = c["letter"].Str(), Age = c["age"].Int(), D = c["d"].Int(), Cls = A.Level(c["d"].Num()).Class, Multimap = c["multimap"].Bool(),
            LevelName = c["levelName"] is JsonValue ln ? ln.Str() : null, Player = c["player"].Int(), Tactic = c["tactic"].Int(), Harbour = c["harbour"].Bool(), Watermap = c["watermap"].Bool(),
            Warpgate = c["warpgate"].Bool(), OwnWarpgate = c["ownWarpgate"].Bool(), Has = has.Contains, DefenderGame = c["defenderGame"].Bool(), Defender = c["defender"].Bool(),
        };
    }
    static JsonNode Rows(List<AiRow> l) => new JsonArray(l.Select(r => (JsonNode?)new JsonArray(JsonValue.Create(r.Request), JsonValue.Create(r.ObjFlag), JsonValue.Create(r.Count), JsonValue.Create(r.Unique))).ToArray());
    static JsonNode? Army(List<ArmyRow>? a) => a == null ? null : new JsonArray(a.Select(e => (JsonNode?)Obj(("min", e.Min), ("max", e.Max),
        ("alternatives", new JsonArray(e.Alternatives.Select(x => (JsonNode?)Obj(("cls", x.Cls), ("level", x.Level), ("objFlag", x.ObjFlag))).ToArray())))).ToArray());
    static JsonObject Obj(params (string K, object? V)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv) o[k] = v switch { null => null, JsonNode n => n, string s => JsonValue.Create(s), bool b => JsonValue.Create(b), int i => JsonValue.Create(i), double d => JsonValue.Create(d), _ => throw new Exception("type " + v.GetType()) };
        return o;
    }

    /// <summary>deep equality; numbers as doubles; object keys in any order</summary>
    static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a is JsonArray x && b is JsonArray y) return x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second));
        if (a is JsonObject o && b is JsonObject p) return o.Count == p.Count && o.All(kv => p.ContainsKey(kv.Key) && Same(kv.Value, p[kv.Key]));
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.TryGetValue<double>(out var da) && vb.TryGetValue<double>(out var db)) return da == db || Math.Abs(da - db) <= 1e-12 * Math.Max(1, Math.Abs(da));
            return a.ToJsonString() == b.ToJsonString();
        }
        return false;
    }
    static string Show(JsonNode? n) { var s = n?.ToJsonString() ?? "null"; return s.Length > 300 ? s.Substring(0, 300) + "…" : s; }

    static readonly Dictionary<string, JsonNode> s_dumps = new();
    static JsonNode Dump(TestContext t, string data)
    {
        if (s_dumps.TryGetValue(data, out var d)) return d;
        string src = RulesTestData.RemakeSrc();
        if (!File.Exists(Path.Combine(src, "game", "ai", "data.js"))) t.Skip($"JS remake sources not found ({src}; PW_REMAKE_SRC)");
        string script = Path.Combine(AiTestData.RepoRoot(), "tests", "golden", "ai_data_dump.mjs");
        string outDir = Path.Combine(Path.GetTempPath(), "pwremake-golden");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, "ai_data_" + Math.Abs(data.GetHashCode()).ToString(CultureInfo.InvariantCulture) + ".json");
        var psi = new ProcessStartInfo("node", $"\"{script}\" \"{src}\" \"{data}\" \"{outFile}\"") { RedirectStandardError = true, RedirectStandardOutput = true };
        Process? p;
        try { p = Process.Start(psi); } catch (Exception e) { t.Skip("node not available: " + e.Message); return null!; }
        p!.WaitForExit(300000);
        string err = p.StandardError.ReadToEnd();
        if (p.ExitCode != 0) throw new Exception($"ai_data_dump.mjs failed ({p.ExitCode}): {err}");
        return s_dumps[data] = Json.ParseFile(outFile)!;
    }
}

/// <summary>shared helpers of the AI tests</summary>
public static class AiTestData
{
    /// <summary>the repository (tests/golden): from the test binary's folder, else the working directory</summary>
    public static string RepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            while (d != null && !Directory.Exists(Path.Combine(d, "tests", "golden"))) d = Path.GetDirectoryName(d);
            if (d != null) return d;
        }
        throw new Exception("repository root not found");
    }
}
