// Golden tests of the sys2 port against the JS remake (tests/golden/sys2_dump.mjs -> tests/golden/sys2_<data>.json):
// the wall grid (BuildName for every connection set, CalcWall for lines / gates / towers / traps, the pieces' parts,
// pathfinder masks and arm masks as pieces are added, replaced and removed - on a flat and a hilly ground with trees),
// and the formulas: the workers' loads, production / research times of every action (normal, a computer player's
// handicaps, the debug speed), build times, the pyramid's skull costs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys2GoldenTests
{
    static JsonNode Golden(TestContext t, string data)
    {
        string? f = null;
        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        for (var d = new DirectoryInfo(dir); d != null && f == null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "golden", $"sys2_{Path.GetFileName(data.TrimEnd('/'))}.json");
            if (File.Exists(p)) { f = p; break; }
        }
        if (f == null) t.Skip("no golden file for " + data);
        return Json.ParseFile(f!)!;
    }

    public static void BuildNameAllConnectionSets(TestContext t)
    {
        var g = Golden(t, Sys2.Base);
        int[] vals = { -10, -1, 0, 1 };
        var lines = new List<string>();
        for (int k = 0; k < 65536; k++)
        {
            var rel = new int[8]; int x = k;
            for (int d = 0; d < 8; d++) { rel[d] = vals[x & 3]; x >>= 2; }
            lines.Add(string.Join(".", WallMap.BuildName(rel)));
        }
        var sample = new[] { 0, 1, 5, 17, 85, 170, 255, 1000, 4369, 21845, 43690, 65535 };
        var gs = g["buildNameSample"]!.AsArray();
        for (int i = 0; i < sample.Length; i++) t.Equal(gs[i]!.GetValue<string>(), lines[sample[i]], $"BuildName #{sample[i]}");
        t.Equal(g["buildNameHash"]!.GetValue<uint>(), Sys2.Fnv(string.Join("\n", lines)), "BuildName of all 65536 sets");
    }

    public static void WallGridBase(TestContext t) => WallGrid(t, Sys2.Base);
    public static void WallGridMirage(TestContext t) => WallGrid(t, Sys2.Mirage);

    static readonly (string N, double Sx, double Sz, double Ex, double Ez)[] QUERIES =
    {
        ("hu_palisade", -40, -20, 40, -20), ("hu_palisade", -40, -40, 20, 30), ("hu_small_wall", 10, 10, 10, 90),
        ("hu_palisade", 25, 25, 45, 35), ("hu_palisade", -60, 40, -40, 40), ("aje_clay_wall", -80, -80, 80, 70),
        ("hu_small_tower", 0, -20, 0, -20), ("hu_palisade_gate", 4, -20, 4, -20), ("ninigi_pitfall", 50, 50, 50, 50),
        ("seas_fence", -100, 100, -20, 60),
    };
    static readonly double[][] TREES = { new double[] { 30, 30 }, new double[] { 33, 28 }, new double[] { -50, 40 }, new double[] { -52, 44 }, new double[] { -48, 41 }, new double[] { -55, 39 }, new double[] { 60, -60 } };

    static string TilesN(WallCalc? w) => w == null ? "null" : $"{w.Race},{w.Cls},{w.Type},{w.D}:" + string.Join(";", w.Tiles.Select(x => $"{x.I},{x.J},{(x.Existing ? 1 : 0)},{(x.GateOK ? 1 : 0)}"));
    static string TilesJ(JsonNode? n)
    {
        if (n == null) return "null";
        return $"{n["race"]},{n["cls"]},{n["type"]},{n["d"]}:" + string.Join(";", n["tiles"]!.AsArray().Select(x => string.Join(",", x!.AsArray().Select(v => v!.ToString()))));
    }

    static void WallGrid(TestContext t, string data)
    {
        var g = Golden(t, data)["walls"]!;
        var terrains = new Dictionary<string, Func<double, double, double>>
        {
            ["flat"] = (x, z) => 0,
            ["hilly"] = (x, z) => 4 * Math.Sin(x / 23) + 3 * Math.Cos(z / 17) + 0.02 * x,
        };
        foreach (var (terrain, hf) in terrains)
        {
            var G = g[terrain]!;
            var (w, _, _) = Sys2.World(t, data, height: hf);
            var owner = new Player(1, "Hu", new PlayerOpts { Rules = w.Data });
            w.Players.Clear(); w.Players.Add(owner);
            foreach (var tr in TREES) w.AddResource("tree", "wood", tr[0], tr[1], 100, new ResOpts { Radius = 1.5 });
            var WM = w.Walls!;
            WM.Overbuild = false;           // (the original's rule: the remake's overbuilding is not in the browser remake)
            for (int k = 0; k < QUERIES.Length; k++)
            {
                var q = QUERIES[k];
                t.Equal(TilesJ(G["calc"]![k]), TilesN(WM.CalcWall(owner, q.N, q.Sx, q.Sz, q.Ex, q.Ez)), $"{terrain}: CalcWall {q}");
            }
            var blds = new List<Building>();
            Building? Add(string name, int i, int j, double rot = 0)
            {
                var (x, z) = WM.Centre(i, j);
                var b = w.PlaceBuilding(name, owner, x, z, rot, true);
                t.True(b != null, $"{name} placed");
                blds.Add(b!);
                return b;
            }
            int step = 0;
            void Snap(string label)
            {
                var S = G["steps"]![step++]!;
                t.Equal(S["label"]!.GetValue<string>(), label, "step");
                var js = S["pieces"]!.AsArray();
                var mine = blds.Where(b => b.Alive).ToList();
                t.Equal(js.Count, mine.Count, $"{terrain} {label}: pieces");
                for (int k = 0; k < mine.Count; k++)
                {
                    var J = js[k]!.AsArray(); var b = mine[k];
                    string where = $"{terrain} {label}: {b.Name} #{k}";
                    t.Equal(J[0]!.GetValue<string>(), b.Name, where);
                    t.Equal(J[1]?.ToString() ?? "null", b.Tile?.I.ToString() ?? "null", where + " tile i");
                    t.Equal(J[2]?.ToString() ?? "null", b.Tile?.J.ToString() ?? "null", where + " tile j");
                    if (b.Def!.WallKind == "wall")
                    {
                        t.Equal(J[3]!.GetValue<string>(), string.Join(".", b.WallParts ?? new List<string>()), where + " parts");
                        t.Equal(J[4]!.GetValue<int>(), b.PfMask, where + " pf mask");
                        t.Equal(J[5]!.GetValue<int>(), b.ArmMask, where + " arm mask");
                    }
                    if (b.Def.WallKind == "gate")
                    {
                        t.Equal(J[6]!.GetValue<int>(), b.GateEnds ? 1 : 0, where + " gate ends");
                        t.Equal(J[7]!.GetValue<int>(), b.GateD ?? -1, where + " gate d");
                    }
                }
                var o = new List<string>();
                for (int j = -6; j <= 6; j++) for (int i = -8; i <= 8; i++) o.Add(WM.Word(i, j) + ":" + string.Join(".", WM.PartsOf(i, j)) + ":" + WM.PfMask(i, j) + ":" + WM.Mask(i, j));
                t.Equal(S["grid"]!.GetValue<uint>(), Sys2.Fnv(string.Join("|", o)), $"{terrain} {label}: the grid's words, parts and masks");
            }
            foreach (var q in new[] { QUERIES[0], QUERIES[1], QUERIES[2] })
            {
                var wl = WM.CalcWall(owner, q.N, q.Sx, q.Sz, q.Ex, q.Ez);
                if (wl != null) foreach (var x in wl.Tiles) if (!x.Existing) Add(q.N, x.I, x.J);
                Snap($"line {q.N} {q.Sx} {q.Sz} {q.Ex} {q.Ez}");
            }
            {
                var wl = WM.CalcWall(owner, "hu_palisade_gate", 4, -20, 4, -20);
                t.Equal(TilesJ(G["gate"]), TilesN(wl), $"{terrain}: the gate");
                if (wl != null)
                {
                    var (i, j) = WM.TileOf(4, -20); var tt = WM.At(i, j);
                    if (tt?.Wall != null) { tt.Wall.Alive = false; WM.Remove(tt.Wall); }
                    Add("hu_palisade_gate", i, j, WallMap.GateRot(wl.D));
                }
                Snap("gate");
            }
            {
                var wl = WM.CalcWall(owner, "hu_small_tower", -28, -20, -28, -20);
                t.Equal(TilesJ(G["tower"]), TilesN(wl), $"{terrain}: the tower");
                if (wl != null) { var x = wl.Tiles[^1]; Add("hu_small_tower", x.I, x.J); }
                Snap("tower");
                var tr = WM.CalcWall(owner, "ninigi_pitfall", 50, 50, 50, 50);
                t.Equal(TilesJ(G["trap"]), TilesN(tr), $"{terrain}: the trap");
                if (tr != null) Add("ninigi_pitfall", tr.Tiles[0].I, tr.Tiles[0].J);
                Snap("trap");
            }
            {
                var b = blds.FirstOrDefault(x => x.Alive && x.Name == "hu_palisade" && x.Tile == (0, -1)) ?? blds.FirstOrDefault(x => x.Alive && x.Name == "hu_palisade");
                if (b != null) { b.Alive = false; WM.Remove(b); }
                Snap("removed");
            }
            for (int k = 0; k < QUERIES.Length; k++)
            {
                var q = QUERIES[k];
                t.Equal(TilesJ(G["after"]![k]), TilesN(WM.CalcWall(owner, q.N, q.Sx, q.Sz, q.Ex, q.Ez)), $"{terrain}: CalcWall afterwards {q}");
            }
        }
    }

    public static void FormulasBase(TestContext t) => Formulas(t, Sys2.Base);
    public static void FormulasMirage(TestContext t) => Formulas(t, Sys2.Mirage);

    static void Formulas(TestContext t, string data)
    {
        var G = Golden(t, data);
        int n = 0;
        foreach (var (tribe, F) in G["formulas"]!.AsObject())
        {
            var (w, _, _) = Sys2.World(t, data, tribe);
            var p = new Player(3, tribe, new PlayerOpts { Rules = w.Data });
            w.Players.Add(p);
            foreach (var row in F!["workers"]!.AsArray())
            {
                var name = row!["name"]!.GetValue<string>();
                var u = w.SpawnUnit(name, p, 0, 0, row["level"]!.GetValue<int>());
                if (u == null) { t.Log($"{name}: no model, skipped"); continue; }
                foreach (var res in new[] { "wood", "stone", "food" })
                {
                    var cap = w.CarryCap(u, res);
                    t.Equal(row[res]![0]!.GetValue<double>(), cap, $"{tribe} {name}: carry {res}");
                    double tf = (u.Stats!.Tf > 0 ? u.Stats.Tf : 2.0) * (res == "wood" ? 1 : W_.NODE_TF);
                    double k = res == "food" ? 3 : 5;
                    t.Equal(row[res]![1]!.GetValue<int>(), Math.Max(1, (int)Math.Truncate(k * tf * cap / Math.Max(1, cap))), $"{tribe} {name}: loops {res}");
                }
                n++;
            }
            var acts = w.Data.Actions(p).Where(a => (a.Kind == "Build" || a.Kind == "Upgrades") && !a.Remake).ToList();     // (the remake's own actions: not in the JS)
            var jprod = F["prod"]!.AsArray();
            t.Equal(jprod.Count, acts.Count, $"{tribe}: actions");
            for (int i = 0; i < acts.Count; i++)
            {
                var a = acts[i]; var J = jprod[i]!.AsArray();
                t.Equal(J[0]!.GetValue<string>(), a.Path, $"{tribe}: action #{i}");
                p.AiMods = null; p.Cheat = null;
                t.Near(J[1]!.GetValue<double>(), w.ProductionTime(a, p), 1e-9, $"{a.Path}: time");
                p.AiMods = new AiMods { Gather = 1.2, BuildTime = 0.7, ResearchTime = 0.8, WeaponTime = 1 };
                t.Near(J[2]!.GetValue<double>(), w.ProductionTime(a, p), 1e-9, $"{a.Path}: a computer player's time");
                p.AiMods = null; p.Cheat = new PlayerCheat { Speed = 2.5 };
                t.Near(J[3]!.GetValue<double>(), w.ProductionTime(a, p), 1e-9, $"{a.Path}: the debug speed's time");
                n++;
            }
            p.Cheat = null;
            var jb = F["build"]!.AsArray();
            var builds = acts.Where(a => a.Cat == "Build/BLDG").ToList();
            t.Equal(jb.Count, builds.Count, $"{tribe}: buildings");
            double wtf = F["workers"]!.AsArray().Count > 0 ? F["workers"]![0]!["tf"]!.GetValue<double>() : 2;
            for (int i = 0; i < builds.Count; i++)
            {
                var a = builds[i]; double D = a.Time > 0 ? a.Time : 10;
                for (int N = 1; N <= 4; N++) t.Near(jb[i]![1]![N - 1]!.GetValue<double>(), D * wtf * (0.1 + 0.9 * Math.Pow(0.8, N - 1)), 1e-9, $"{a.Path}: build time with {N} workers");
            }
        }
        var (w2, _, _) = Sys2.World(t, data);
        foreach (var s in G["skulls"]!.AsArray()) { var a = s!.AsArray(); t.Equal(a[2]!.GetValue<double>(), w2.SkullCost(a[0]!.GetValue<int>(), a[1]!.GetValue<int>()), $"skull cost {a[0]} -> {a[1]}"); }
        var pyr = G["pyramid"]!.AsArray();
        for (int i = 0; i < 5; i++) t.Equal(pyr[i]!.GetValue<double>(), w2.PyramidFor(null)[i], $"pyramid level {i + 1}");
        t.Log($"{Path.GetFileName(data)}: {n} worker rows and actions compared");
    }
}
