// Golden tests of the moves and the nests' areas: tests/golden/sys3_dump.mjs runs the JS remake's moves.js,
// moves_mirage.js and animals.js (Area) in node and the C# port (src/Sim/Systems) must answer the same:
//   the MOVES table (every entry, its kind, range, cooldown flags; AUTO_ORDER, AUTO_INVENT),
//   the moves every unit class of every tribe offers at every level (a fresh tree and one with all of the tribe's
//   upgrades): ids, cooldowns, the server's check, the automatic moves in their order - through World.MovesOf,
//   World.MoveCheck and World.AutoMove,
//   Area.Contains / Radius / RandomPoint with the same random numbers.
// Needs node, the JS sources (PW_REMAKE_SRC) and the data folders (PW_DATA / PW_DATA_MIRAGE); skipped without them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys3GoldenTests
{
    public static void MovesGoldenBase(TestContext t) => RunMoves(t, RulesTestData.Base(t));
    public static void MovesGoldenMirage(TestContext t) => RunMoves(t, RulesTestData.Mirage(t));

    static readonly Dictionary<string, JsonNode> s_dumps = new();

    /// <summary>runs tests/golden/sys3_dump.mjs in node (once per data folder and test run)</summary>
    public static JsonNode Dump(TestContext t, string data)
    {
        if (s_dumps.TryGetValue(data, out var d)) return d;
        string src = RulesTestData.RemakeSrc();
        if (!File.Exists(Path.Combine(src, "game", "systems", "moves.js"))) t.Skip($"JS remake sources not found ({src}; PW_REMAKE_SRC)");
        string golden = Path.Combine(RulesTestData.RepoRoot(), "tests", "golden");
        string outFile = Path.Combine(Path.GetTempPath(), "pwremake-golden", "sys3_" + Math.Abs(data.GetHashCode()) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        var psi = new ProcessStartInfo("node", $"--import \"{Path.Combine(golden, "node_hooks.mjs")}\" \"{Path.Combine(golden, "sys3_dump.mjs")}\" \"{src}\" \"{data}\" \"{outFile}\"") { RedirectStandardError = true, RedirectStandardOutput = true };
        Process? p;
        try { p = Process.Start(psi); } catch (Exception e) { t.Skip("node not available: " + e.Message); return null!; }
        p!.WaitForExit(300000);
        string err = p.StandardError.ReadToEnd();
        if (p.ExitCode != 0) throw new Exception($"sys3_dump.mjs failed ({p.ExitCode}): {err}");
        return s_dumps[data] = Json.ParseFile(outFile)!;
    }

    static void RunMoves(TestContext t, string data)
    {
        var dump = Dump(t, data);
        var R = Rules.Load(data);
        var diffs = new List<string>();
        void Diff(string s) { if (diffs.Count < 500) diffs.Add(s); }

        // ------------------------------------------------------------ the table
        Moves.Init();
        var js = dump["moves"]!.AsObject();
        foreach (var (id, m) in js)
        {
            if (!Moves.MOVES.TryGetValue(id, out var M)) { Diff($"moves.{id}: missing in C#"); continue; }
            void Eq<T>(string k, T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) Diff($"moves.{id}.{k}: JS {a} / C# {b}"); }
            Eq("self", m!["self"]!.GetValue<bool>(), M.Self);
            Eq("target", m["target"] is JsonValue ? m["target"].Str() : null, M.Target);
            Eq("range", m["range"]!.GetValue<double>(), M.Range);
            Eq("rangeOf", m["rangeOf"]!.GetValue<bool>(), M.RangeOf != null);
            Eq("noCooldown", m["noCooldown"]!.GetValue<bool>(), M.NoCooldown);
            Eq("chase", m["chase"]!.GetValue<bool>(), M.Chase);
            Eq("anySpirit", m["anySpirit"]!.GetValue<bool>(), M.AnySpirit);
            Eq("auto", m["auto"]!.GetValue<bool>(), M.Auto != null);
        }
        // (the remake's own: the flyers' Land / Take off of MIRAGE's flying units design - Systems/Flyers.cs - not in the JS)
        foreach (var id in Moves.MOVES.Keys) if (!js.ContainsKey(id) && !Flyers.IsAbility(id)) Diff($"moves.{id}: only in C#");
        var order = dump["autoOrder"]!.AsArray().Select(x => x.Str()).ToList();
        if (!order.SequenceEqual(Moves.AUTO_ORDER)) Diff($"AUTO_ORDER: JS {string.Join(",", order)} / C# {string.Join(",", Moves.AUTO_ORDER)}");
        foreach (var (k, v) in dump["autoInvent"]!.AsObject()) if (!Moves.AUTO_INVENT.TryGetValue(k, out var inv) || inv != v.Str()) Diff($"AUTO_INVENT.{k}");
        if (dump["autoInvent"]!.AsObject().Count != Moves.AUTO_INVENT.Count) Diff("AUTO_INVENT: size");

        // ------------------------------------------------------------ what every unit offers
        var M0 = new ModelManifest(new JsonObject(), null, null);
        var W = new W_(R, M0, new FlatMap(128));
        int compared = 0, usable = 0;
        foreach (var sc in dump["scenarios"]!.AsArray())
        {
            string label = sc!["label"].Str(), tribe = sc["tribe"].Str();
            // (the JS has no game options: no MIRAGE option filters - GameplayOptions - so that the trees are the same)
            var p = new Player(0, tribe, new PlayerOpts { Rules = R, Gameplay = GameplayOptions.NoFilters });
            p.Tt!.Suspend(); foreach (var f in sc["filters"]!.AsArray()) p.Tt.Enable(f.Str()); p.Tt.Resume();
            if (sc["version"]!.GetValue<int>() != p.Tt!.Version) Diff($"{label}: tree version");
            foreach (var (name, levels) in sc["classes"]!.AsObject())
            {
                foreach (var (lv, jl) in levels!.AsObject())
                {
                    var u = new Unit(W, name, p, 0, 0, int.Parse(lv));
                    var mine = W.MovesOf(u).Where(a => !a.Remake).ToList();     // (the remake's own actions: not in the JS)
                    var jm = jl!["moves"]!.AsArray();
                    string where = $"{label}/{name}/{lv}";
                    if (mine.Count != jm.Count) { Diff($"{where}: {jm.Count} moves in JS, {mine.Count} in C#"); continue; }
                    for (int i = 0; i < jm.Count; i++)
                    {
                        var a = mine[i]; var j = jm[i]!;
                        compared++;
                        if (a.Id != j["id"].Str()) { Diff($"{where}[{i}]: JS {j["id"].Str()} / C# {a.Id}"); continue; }
                        if (a.Time != j["time"]!.GetValue<double>()) Diff($"{where}.{a.Id}: cooldown JS {j["time"]} / C# {a.Time}");
                        string? jc = j["check"] is JsonValue ? j["check"].Str() : null, cc = W.MoveCheck(u, a);
                        if (jc != cc) Diff($"{where}.{a.Id}: check JS {jc ?? "ok"} / C# {cc ?? "ok"}");
                        if (j["handled"]!.GetValue<bool>() != Moves.MOVES.ContainsKey(a.Id)) Diff($"{where}.{a.Id}: handled");
                        if (cc == null && Moves.MOVES.ContainsKey(a.Id)) usable++;
                    }
                    W.AutoMove(u, null);
                    var ja = jl["auto"]!.AsArray().Select(x => x.Str()).ToList();
                    var ca = u.AutoMoves?.List.Select(a => a.Id).ToList() ?? new List<string>();
                    if (!ja.SequenceEqual(ca)) Diff($"{where}: automatic moves JS {string.Join(",", ja)} / C# {string.Join(",", ca)}");
                }
            }
        }
        foreach (var d in diffs.Take(40)) t.Log(d);
        t.Log($"{data}: {Moves.MOVES.Count} moves in the table, {compared} offered moves compared ({usable} usable now), {diffs.Count} differences");
        t.True(diffs.Count == 0, $"the C# moves answer as the JS ({diffs.Count} differences, first: {diffs.FirstOrDefault()})");
        t.True(compared > 1000, "a broad sample");
    }

    // ------------------------------------------------------------------ areas
    /// <summary>mulberry32 (the JS dump's random numbers)</summary>
    static Func<double> Mulberry32(int seed)
    {
        int a = seed;
        return () =>
        {
            unchecked
            {
                a = a + 0x6D2B79F5;
                int tt = (int)((uint)(a ^ (int)((uint)a >> 15)) * (uint)(1 | a));
                tt = (tt + (int)((uint)(tt ^ (int)((uint)tt >> 7)) * (uint)(61 | tt))) ^ tt;
                return (uint)(tt ^ (int)((uint)tt >> 14)) / 4294967296.0;
            }
        };
    }

    public static void AreasGolden(TestContext t)
    {
        var dump = Dump(t, RulesTestData.Base(t));
        int n = 0;
        foreach (var ja in dump["areas"]!.AsArray())
        {
            var shapes = ja!["shapes"]!.AsArray().Select(s => new AreaBox { Oval = s!["oval"]!.GetValue<bool>(), X0 = s["x0"]!.GetValue<double>(), Z0 = s["z0"]!.GetValue<double>(), X1 = s["x1"]!.GetValue<double>(), Z1 = s["z1"]!.GetValue<double>() }).ToList();
            var A = new Area(shapes);
            t.Equal(ja["empty"]!.GetValue<bool>(), A.Empty, "empty");
            t.Near(ja["radius"]!.GetValue<double>(), A.Radius(), 1e-12, "radius");
            var rand = Mulberry32(ja["seed"]!.GetValue<int>());
            foreach (var jp in ja["points"]!.AsArray())
            {
                var p = A.RandomPoint(rand);
                if (jp == null) { t.True(p == null, "no point in an empty area"); continue; }
                t.True(p != null, "a point");
                t.Near(jp[0]!.GetValue<double>(), p!.Value.X, 1e-9, "random point x");
                t.Near(jp[1]!.GetValue<double>(), p.Value.Z, 1e-9, "random point z");
                n++;
            }
            var grid = ja["grid"]!.AsArray(); int k = 0;
            for (double x = -60; x <= 170; x += 7.5) for (double z = -40; z <= 150; z += 6.25) t.Equal(grid[k++]!.GetValue<int>() == 1, A.Contains(x, z), $"contains ({x}, {z})");
        }
        var C = Area.Circle(3, -4, 12);
        t.Near(dump["circle"]!["radius"]!.GetValue<double>(), C.Radius(), 1e-12, "circle radius");
        t.Log($"{n} random points and the grids agree");
    }
}
