// The selection circle's size (src/Sim/World/SelectionRing.cs): round the model's drawn extent on the ground,
// never smaller than the old body / footprint ring. Ring prints old vs new radius for a range of classes (models from
// PW_MODELS or the data's assets/models, + PW_MODELS_EXTRA=<folder>[;<folder>]; a class without its .glb keeps the old ring).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class SelectionRingTests
{
    static readonly string[] CLASSES =
    {
        "hu_worker", "hu_warrior", "aje_spearman", "ninigi_ninja", "seas_mechanical_walker", "hu_mammoth", "hu_triceratops",
        "aje_brachiosaurus", "aje_brachiosaurus_catapult", "ninigi_seismosaurus", "aje_catapult_rex", "hu_titan_ballista", "hu_titan_logcannon",
        "hu_steam_tank", "hu_dragon_boat", "hu_transport_ship", "ninigi_transport_boat", "seas_carrier", "hu_mammoth_log_cannon", "aje_allosaurus", "aje_triceratops_archer",
        "hu_headquarters", "aje_headquarters", "seas_headquarters", "hu_fortress", "aje_fortress", "ninigi_fortress", "hu_harbour", "hu_corn_field",
    };

    /// <summary>the rule itself: the model's reach decides when it is larger than the body ring; capped; never smaller</summary>
    public static void Formula(TestContext t)
    {
        // a small figure: the body ring stays (0.7 * 1.25 + 0.2)
        t.Near(1.075, SelectionRing.Radius(false, 0.7, 0.6), 1e-9, "infantry keeps its body ring");
        // a walker whose legs reach 5 m out: round the legs
        t.Near(5 * 1.05 + 0.15, SelectionRing.Radius(false, 1.2, 5), 1e-9, "widow-like legs");
        // no model data: the old ring
        t.Near(1.2 * 1.25 + 0.2, SelectionRing.Radius(false, 1.2, 0), 1e-9, "no data");
        // a building: footprint, a flat plot's pick radius, the model's reach - the largest
        t.Near(6.3, SelectionRing.Radius(true, 6, 3), 1e-9, "building footprint");
        t.Near(9, SelectionRing.Radius(true, 6, 3, 9), 1e-9, "field plot");
        t.Near(8 * 1.05 + 0.15, SelectionRing.Radius(true, 6, 8), 1e-9, "wide building");
        t.Near(6 * SelectionRing.BLDG_MAX, SelectionRing.Radius(true, 6, 20), 1e-9, "building capped by its footprint");
        t.Near(SelectionRing.MAX, SelectionRing.Radius(false, 2, 40), 1e-9, "capped");
    }

    /// <summary>the reach from the model: its drawn box (a skinned model's in the GSF frame, z down from the ground: x / y on
    /// the ground), else its pick volume; measured from the model's origin</summary>
    public static void ReachFromModel(TestContext t)
    {
        var man = new JsonObject { ["models"] = new JsonObject { ["walker"] = new JsonObject(), ["house"] = new JsonObject(), ["pickonly"] = new JsonObject() } };
        var mi = new JsonObject
        {
            ["models"] = new JsonObject
            {
                // a spider walker: legs 5.5 m out along y, 4 m along x, 3 m tall (z down)
                ["walker"] = new JsonObject { ["fourcc"] = "Vehi", ["box"] = new JsonArray(-4.0, -5.5, -3.0, 4.0, 5.0, 0.02), ["pick"] = new JsonArray(-1.0, 0.0, -1.0, 1.0, 2.0, 1.0) },
                // a building, y up: 6 m tall, 7 m to one side
                ["house"] = new JsonObject { ["fourcc"] = "Bldg", ["box"] = new JsonArray(-5.0, 0.0, -7.0, 5.0, 6.0, 4.0) },
                ["pickonly"] = new JsonObject { ["fourcc"] = "Anim", ["pick"] = new JsonArray(-1.0, 0.0, -3.0, 1.0, 2.0, 2.5) },
            }
        };
        var M = new ModelManifest(man, mi, _ => null);
        t.Near(5.5, M.SelReach("walker") ?? -1, 1e-9, "skinned box: x / y on the ground");
        t.Near(7, M.SelReach("house") ?? -1, 1e-9, "y-up box: x / z on the ground");
        t.Near(3, M.SelReach("pickonly") ?? -1, 1e-9, "pick volume without a drawn box");
        t.True(M.SelReach("missing") == null, "no data");
    }

    /// <summary>old vs new radius of real classes (prints a table)</summary>
    public static void Classes(TestContext t)
    {
        var data = t.Data("PW_DATA_MIRAGE");
        var R = Rules.Load(data);
        var dirs = new List<string> { Path.Combine(data, "assets", "models") };
        if (Environment.GetEnvironmentVariable("PW_DATA") is { Length: > 0 } b) dirs.Add(Path.Combine(b, "assets", "models"));
        dirs.AddRange((Environment.GetEnvironmentVariable("PW_MODELS_EXTRA") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        var man = Json.ParseFile(Path.Combine(data, "assets", "manifest.json"))!;
        var M = new ModelManifest(man, null, n => dirs.Select(d => Path.Combine(d, n + ".glb")).FirstOrDefault(File.Exists));
        var W = Sys1.NewWorld(R, M, 1024);
        var p = Sys1.AddPlayer(W, R, 1, "SEAS");
        int bigger = 0, n = 0;
        t.Log($"{"class",-28} {"gfx",-32} {"body r",6} {"reach",6} {"old",6} {"new",6}");
        foreach (var c in CLASSES)
        {
            var d = R.Def(c, null);
            if (d == null) { t.Log($"{c,-28} (no class)"); continue; }
            Entity? e = d.Type == "BLDG" ? W.PlaceBuilding(c, p, 60 + 36 * n, 512) : W.SpawnUnit(c, p, 60 + 36 * n, 900);
            if (e == null) { t.Log($"{c,-28} (not placed)"); continue; }
            double old = e is Building bb ? Math.Max(bb.Radius * 1.05, bb.PickR) : e.Radius * 1.25 + 0.2;
            double nw = SelectionRing.Of(e);
            t.Log($"{c,-28} {e.Gfx,-32} {e.Radius,6:0.00} {e.SelReach,6:0.00} {old,6:0.00} {nw,6:0.00}{(e.SelReach == 0 ? "  (no model data here)" : "")}");
            t.True(nw >= old - 1e-9 && nw <= Math.Max(old, e is Building ? e.Radius * SelectionRing.BLDG_MAX : SelectionRing.MAX), c + ": never smaller, capped");
            if (Environment.GetEnvironmentVariable("PW_RING_BOXES") != null && M.File(e.Gfx) is { } fi) t.Log($"    box {fi.BoxMin} {fi.BoxMax} pick {fi.PickMin} {fi.PickMax}");
            n++; if (nw > old + 0.05) bigger++;
        }
        t.True(n > 0, "classes checked");
    }
    /// <summary>every ring a circle (Kacper: "make everything a circle"); long buildings (walls, gates, ships ...) a circle
    /// round their footprint's middle - every footprint corner inside, no bigger than the circle round their place
    /// (SelectionRing.Shape); prints the long ones</summary>
    public static void LongBuildings(TestContext t)
    {
        foreach (var v in new[] { "PW_DATA", "PW_DATA_MIRAGE" })
        {
            var data = Environment.GetEnvironmentVariable(v);
            if (string.IsNullOrEmpty(data) || !Directory.Exists(data)) continue;
            var R = Rules.Load(data);
            var man = Json.ParseFile(Path.Combine(data, "assets", "manifest.json"))!;
            var M = new ModelManifest(man, null, n => Path.Combine(data, "assets", "models", n + ".glb") is var f && File.Exists(f) ? f : null);
            var W = Sys1.NewWorld(R, M, 4096);
            var p = Sys1.AddPlayer(W, R, 1, "SEAS");
            int n = 0, longs = 0;
            foreach (var (c, ix) in R.Index.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (ix.Type != "BLDG") continue;
                Building? b;
                try { b = W.PlaceBuilding(c, p, -1800 + 90 * (n % 40), -1800 + 90 * (n / 40)); } catch { b = null; }
                if (b == null) continue;
                n++;
                var (r, ax, az, scx, scz) = SelectionRing.Shape(b);
                t.True(ax == 0 && az == 0, c + ": a circle");
                if (Math.Abs(SelectionRing.Of(b) - r) < 1e-9 && scx == 0 && scz == 0) continue;           // the circle round its place
                longs++;
                double c0 = SMath.Cos(b.Rot), s0 = SMath.Sin(b.Rot);
                foreach (var q in b.Boxes)
                {
                    double cx = q[0] - b.Pos.X, cz = q[1] - b.Pos.Z, lx = cx * c0 - cz * s0, lz = cx * s0 + cz * c0;
                    foreach (var (ex, ez) in new[] { (lx - q[2], lz - q[3]), (lx + q[2], lz - q[3]), (lx - q[2], lz + q[3]), (lx + q[2], lz + q[3]) })
                    {
                        double wx = ex * c0 + ez * s0 - scx, wz = -ex * s0 + ez * c0 - scz;     // from the ring's middle
                        t.True(Math.Sqrt(wx * wx + wz * wz) <= r + 1e-6, $"{c}: footprint corner within its circle");
                    }
                }
                double circle = SelectionRing.Of(b);
                t.Log($"{v,-14} {c,-36} footprint {2 * b.Hx,5:0.0} x {2 * b.Hz,5:0.0}  circle r {circle,5:0.0} -> round the middle r {r,5:0.0}");
            }
            t.True(n > 50 && longs > 0, $"{v}: buildings placed ({n}), long ones ({longs})");
        }
    }
}
