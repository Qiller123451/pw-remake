// Doubled parts of buildings: every building model (GSF kind "Bldg") of both configurations is drawn in every state the
// game gives it - construction levels 0-3 and finished, intact and damage 1 / 2, epochs I-V - with the view's part rules
// (game/src/View/Parts/Parts.cs, linked into this project: StaticSig / SigVisible / HelperPart / Unstaged), and the
// chunks shown together are compared: two with the same geometry (vertex count, box and material of every surface)
// and turn, standing within 2.5 m of each other with different state bits, are a part drawn twice (the same bits: the
// model's own repeats, two lamps side by side). Before PartBits.Unstaged the Norse lumberjack
// cottage showed nine such parts (its "_f0" chunks: no state bits), the weapon smith, large tower, temple, harbour
// and tavern moved copies of theirs (docs/VIEW.md "Leftover chunks").
//
// Copies on the very same spot are counted but allowed: they draw the same pixels (the original's data has a few, e.g.
// hu_warehouse's chunks for LOD 0 and LOD 0-4 of one beam). The models come from PW_DATA / PW_DATA_MIRAGE (+ the base
// data for MIRAGE) and PW_MODELS_EXTRA=<folder>[;<folder>].
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.View.Parts;

namespace PwRemake.Tests;

[TestClass]
public static class BuildingPartsTests
{
    public static void BaseBuildingsNoDoubledParts(TestContext t) => Check(t, new[] { t.Data("PW_DATA") });
    public static void MirageBuildingsNoDoubledParts(TestContext t) => Check(t, new[] { t.Data("PW_DATA_MIRAGE"), Environment.GetEnvironmentVariable("PW_DATA") ?? "" });

    /// <summary>the lumberjack cottage's leftover chunks are not drawn; its parts are not doubled in any state</summary>
    public static void LumberjackCottage(TestContext t)
    {
        var f = Path.Combine(t.Data("PW_DATA"), "assets", "models", "hu_lumberjack_cottage.glb");
        if (!File.Exists(f)) t.Skip("no hu_lumberjack_cottage.glb");
        var m = Read(f)!;
        var hidden = m.Chunks.Where(c => PartBits.Unstaged(c.Attr, m.Fourcc)).Select(c => c.Name).ToList();
        t.Log("not drawn: " + string.Join(", ", hidden));
        t.Equal(10, hidden.Count, "the cottage's chunks without a construction state (nine _f0 meshes and mesh 70)");
        var (moved, same) = Doubled(m);
        t.True(moved.Count == 0, "no part drawn twice: " + string.Join("; ", moved));
        // (before: the same check with every chunk drawn finds the doubled parts)
        var (movedOld, sameOld) = Doubled(m, oldRule: true);
        t.True(movedOld.Count + sameOld.Count > same.Count, $"the old rule doubled parts ({movedOld.Count} moved, {sameOld.Count} in place)");
    }

    sealed class Chunk { public string Name = ""; public uint Attr; public string Geo = ""; public double[] T = new double[3]; public string R = ""; }
    sealed class Model { public string Name = "", Fourcc = ""; public List<Chunk> Chunks = new(); }

    static void Check(TestContext t, string[] datas)
    {
        var dirs = datas.Where(d => d.Length > 0).Select(d => Path.Combine(d, "assets", "models"))
            .Concat((Environment.GetEnvironmentVariable("PW_MODELS_EXTRA") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Where(Directory.Exists).ToList();
        var files = new Dictionary<string, string>();
        foreach (var d in dirs) foreach (var f in Directory.GetFiles(d, "*.glb")) files.TryAdd(Path.GetFileName(f), f);
        int buildings = 0, inPlace = 0;
        var bad = new List<string>();
        foreach (var f in files.Values.OrderBy(x => x))
        {
            var m = Read(f);
            if (m == null || m.Fourcc != "Bldg") continue;
            buildings++;
            var (moved, same) = Doubled(m);
            inPlace += same.Count;
            if (moved.Count > 0) bad.Add($"{m.Name}: {string.Join("; ", moved.Take(4))}");
        }
        t.Log($"{buildings} buildings drawn in every state; copies on the same spot: {inPlace}; doubled parts: {(bad.Count == 0 ? "none" : "\n      " + string.Join("\n      ", bad))}");
        t.True(buildings > 0, "buildings checked");
        t.True(bad.Count == 0, $"{bad.Count} buildings draw a part twice");
    }

    /// <summary>the pairs of chunks drawn together in some state: moved copies (a doubled part) and copies on the spot</summary>
    static (List<string> Moved, List<string> Same) Doubled(Model m, bool oldRule = false)
    {
        var drawn = m.Chunks.Where(c => PartBits.HelperPart(c.Attr, m.Fourcc) == null && (oldRule || !PartBits.Unstaged(c.Attr, m.Fourcc))
                                        && !((c.Attr >> 5) == 0 && c.Geo.StartsWith("hull"))).ToList();
        var moved = new HashSet<string>();
        var same = new HashSet<string>();
        var sigs = drawn.Select(c => PartBits.StaticSig(c.Attr)).ToArray();
        for (int level = 0; level <= 4; level++)
            for (int dmg = 0; dmg <= (level == 4 ? 2 : 0); dmg++)
                for (int age = 1; age <= 5; age++)
                {
                    var vis = Enumerable.Range(0, drawn.Count).Where(i => PartBits.SigVisible(sigs[i], level, dmg, age)).Select(i => drawn[i]).ToList();
                    foreach (var g in vis.GroupBy(c => c.Geo + "|" + c.R))
                    {
                        var l = g.ToList();
                        for (int i = 0; i < l.Count; i++)
                            for (int j = i + 1; j < l.Count; j++)
                            {
                                double d = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => (l[i].T[k] - l[j].T[k]) * (l[i].T[k] - l[j].T[k])));
                                var key = $"{Short(l[i].Name)} / {Short(l[j].Name)}";
                                if (d < 0.01) same.Add(key);
                                // (copies with the same bits are the model's own repeats - two lamps, two flags side by side)
                                else if (d < 2.5 && l[i].Attr != l[j].Attr) moved.Add($"{key} ({d:0.00} m apart, level {level} damage {dmg})");
                            }
                    }
                }
        return (moved.ToList(), same.ToList());
        string Short(string n) => n.StartsWith(m.Name + "_") ? n[(m.Name.Length + 1)..] : n;
    }

    static Model? Read(string path)
    {
        JsonNode? j;
        try
        {
            var b = File.ReadAllBytes(path);
            if (BitConverter.ToUInt32(b, 0) != 0x46546C67) return null;
            int len = BitConverter.ToInt32(b, 12);
            j = JsonNode.Parse(Encoding.UTF8.GetString(b, 20, len));
        }
        catch (Exception) { return null; }
        var nodes = j!["nodes"] as JsonArray ?? new JsonArray();
        var root = nodes[j["scenes"]?[0]?["nodes"]?[0]?.Int() ?? 0];
        var m = new Model { Name = Path.GetFileNameWithoutExtension(path), Fourcc = root?["extras"]?["fourcc"].Str() ?? "" };
        if (m.Fourcc != "Bldg") return m;
        foreach (var n in nodes)
        {
            if (n?["mesh"] == null || n["extras"]?["attr"] == null) continue;
            var kind = n["extras"]!["kind"].Str();
            if (kind == "foliage") continue;
            var mesh = j["meshes"]![n["mesh"]!.Int()]!;
            var sb = new StringBuilder();
            int verts = 0;
            foreach (var p in mesh["primitives"] as JsonArray ?? new JsonArray())
            {
                var acc = j["accessors"]![p!["attributes"]!["POSITION"]!.Int()]!;
                verts += acc["count"].Int();
                string V(JsonNode? a) => string.Join(",", (a as JsonArray ?? new JsonArray()).Select(x => Math.Round(x.Num() * 20).ToString()));
                sb.Append($"{acc["count"].Int()}:{V(acc["min"])}:{V(acc["max"])}:{p["material"].Int(-1)};");
            }
            var t = n["translation"] as JsonArray;
            var r = n["rotation"] as JsonArray;
            m.Chunks.Add(new Chunk
            {
                Name = n["name"].Str(), Attr = (uint)n["extras"]!["attr"]!.Num(), Geo = (verts <= 24 ? "hull" : "") + sb,
                T = t == null ? new double[3] : new[] { t[0].Num(), t[1].Num(), t[2].Num() },
                R = r == null ? "" : string.Join(",", r.Select(x => Math.Round(x.Num() * 50))),
            });
        }
        return m;
    }
}
