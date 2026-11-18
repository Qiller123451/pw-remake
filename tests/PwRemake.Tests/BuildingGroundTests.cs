// The ground under every building put down (src/Sim/World/BuildingGround.cs, docs/TERRAIN.md "Ground under buildings"):
//
//   BuildingGroundTests.EveryBuildingChangesTheGround   every building class each tribe can build (ParaWorld: Norsemen,
//        Dustriders, Dragon Clan; MIRAGE: the same and the SEAS, with MIRAGE's own buildings), put down on a slope as a
//        building put down in the game is: it has a ground brush (the original's, else one made of its footprint and its
//        tribe's brushes), levels the ground under it and paints its materials - except walls, gates, traps and coastal
//        buildings (BuildingGround.Changes); the list is printed (class, model, brush, what it did)
//   BuildingGroundTests.MadeBrushes                      a made brush covers the footprint with the template's core,
//        keeps the original's rim and materials, is deterministic; the original's brushes stay as they are
//
//   dotnet run --project tests/PwRemake.Tests -- BuildingGround
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Maps;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class BuildingGroundTests
{
    /// <summary>the configuration's ground brushes (MIRAGE's data without its own: the base game's - the mod adds none)</summary>
    static GroundBrushes Brushes(TestContext t, string data)
    {
        foreach (var d in new[] { data, Sys2.Base })
        {
            var f = Path.Combine(d, "assets", "terrain", "objbrush.json");
            if (File.Exists(f)) return GroundBrushes.LoadFile(f);
        }
        t.Skip("no objbrush.json");
        return null!;
    }

    public static void EveryBuildingChangesTheGround(TestContext t)
    {
        var problems = new List<string>();
        foreach (var (data, tribes) in new[] { (Sys2.Base, new[] { "Hu", "Aje", "Ninigi" }), (Sys2.Mirage, new[] { "Hu", "Aje", "Ninigi", "SEAS" }) })
        {
            if (!File.Exists(Path.Combine(data, "techtree.json"))) { t.Log($"{data}: no data, skipped"); continue; }
            var cfg = data == Sys2.Base ? "ParaWorld" : "MIRAGE";
            foreach (var tribe in tribes)
            {
                var (w0, p0, _) = Sys2.World(t, data, tribe);
                var classes = w0.Data.Actions(p0)
                    .Where(a => a.Kind == "Build" && a.Results.Count > 0 && a.Cat.Contains("BLDG") && w0.Data.Info(a.Results[0].Obj) is { Type: "BLDG" } ix && ix.Tribe == tribe)
                    .Select(a => a.Results[0].Obj).Distinct().ToList();
                int changes = 0, made = 0;
                foreach (var cls in classes)
                {
                    // a fresh slope for each: 0.12 east, 0.08 south
                    var hf = new HeightField(256, 2, (x, z) => 20 + 0.12 * x + 0.08 * z);
                    var (w, p, _) = Sys2.World(t, data, tribe, height: hf.At);
                    var brushes = Brushes(t, data);
                    w.UseGround(hf, brushes);
                    var def = w.Data.Def(cls, p);
                    var gfx = w.Data.Stats(cls, 1, p)?.Gfx;
                    bool should = BuildingGround.Changes(def);
                    var before = (float[])hf.H.Clone();
                    var b = w.PlaceAdapted(cls, p, 0, 0, 0.4);
                    var ev = w.Events.FirstOrDefault(e => e.Type == "terrain");
                    int changed = 0; for (int k = 0; k < hf.H.Length; k++) if (Math.Abs(hf.H[k] - before[k]) > 0.05) changed++;
                    int cells = ev?["cells"] is List<BrushCell> lc ? lc.Count : 0;
                    string kind = brushes.Original(gfx) != null ? "original" : brushes.Get(gfx) != null ? "made" : "none";
                    string why = def == null ? "no class" : def.Coastal ? "coastal" : def.Ladder ? "ladder" : def.WallKind is "wall" or "gate" or "trap" ? def.WallKind : "";
                    t.Log($"{cfg,-9} {tribe,-6} {cls,-34} {gfx,-34} brush {kind,-8} " + (should ? $"levels {changed} samples, paints {cells} cells" : $"no ground change ({why})"));
                    if (!should) continue;
                    changes++; if (kind == "made") made++;
                    if (b == null) { problems.Add($"{cfg} {tribe} {cls}: not placed"); continue; }
                    if (kind == "none" && w.Models.Box(gfx) == null && w.Models.Get(gfx)?.Pf == null) { t.Log($"    ({gfx}: no model data in the test data - its size is unknown)"); continue; }
                    if (kind == "none") problems.Add($"{cfg} {tribe} {cls} ({gfx}): no ground brush");
                    else if (changed < 4 || cells < 1) problems.Add($"{cfg} {tribe} {cls} ({gfx}): the ground did not change ({changed} samples, {cells} cells)");
                }
                t.Log($"{cfg} {tribe}: {classes.Count} building classes, {changes} change the ground ({made} with a made brush)");
                t.True(classes.Count >= 15, $"{cfg} {tribe}: the tribe's buildings listed ({classes.Count})");
            }
        }
        foreach (var p in problems) t.Log("PROBLEM " + p);
        t.Equal(0, problems.Count, "every building but walls, gates, traps and coastal ones changes the ground (listed above)");
    }

    public static void MadeBrushes(TestContext t)
    {
        var (R, M) = Sys2.Load(t, Sys2.Mirage);
        var brushes = Brushes(t, Sys2.Mirage);
        BuildingGround.Use(brushes, R, M);
        // the SEAS' laboratory (the hospital) has none in the original
        var gfx = R.Stats("seas_laboratory", 1, null)?.Gfx;
        t.True(gfx != null && brushes.Original(gfx) == null, $"seas_laboratory ({gfx}): no original brush");
        var b = brushes.Get(gfx);
        t.True(b != null && b.M != null, "a made brush with materials");
        var foot = Footprint.FootBoxes(M, gfx);
        double fx0 = foot.Min(q => -q[0] - q[2]), fx1 = foot.Max(q => -q[0] + q[2]), fz0 = foot.Min(q => -q[1] - q[3]), fz1 = foot.Max(q => -q[1] + q[3]);
        double mpp = brushes.Mpp;
        double A(double bx, double bz) { int u = (int)Math.Floor(bx / mpp + b!.W / 2.0), v = (int)Math.Floor(bz / mpp + b.H / 2.0); return u < 0 || v < 0 || u >= b.W || v >= b.H ? 0 : b.A[v * b.W + u] / 255.0; }
        // the footprint levelled fully, nothing beyond the brush
        int full = 0, n = 0;
        for (double bz = fz0 + 0.3; bz < fz1; bz += 0.5) for (double bx = fx0 + 0.3; bx < fx1; bx += 0.5) { n++; if (A(bx, bz) > 0.9) full++; }
        t.True(full >= n * 0.95, $"the footprint {fx1 - fx0:0.0} x {fz1 - fz0:0.0} m levelled ({full} of {n} points)");
        t.True(A(fx1 + b!.W * mpp, 0) == 0, "nothing beyond the brush");
        // a SEAS template's materials: every material of the made brush is one of the SEAS' brushes'
        var seasMats = new HashSet<byte>(brushes.OriginalNames().Where(k => k.StartsWith("seas_")).SelectMany(k => brushes.Original(k)!.M!));
        t.True(b.M!.All(m => seasMats.Contains(m)), "materials of the SEAS' own brushes");
        // a rim like the original's: alpha falls off outside the footprint
        t.True(A(fx1 + 1.5, (fz0 + fz1) / 2) < A(fx1 - 0.5, (fz0 + fz1) / 2), "the rim falls off");
        // deterministic, and the original's brushes unchanged
        var b2 = BuildingGround.Make(brushes, gfx!, "SEAS", foot)!;
        t.True(b2.W == b.W && b2.H == b.H && b2.A.SequenceEqual(b.A) && b2.M!.SequenceEqual(b.M), "the same brush again");
        var h = brushes.Get("hu_fireplace")!;
        t.True(ReferenceEquals(h, brushes.Original("hu_fireplace")), "an original brush is the original's");
        t.True(brushes.Get("no_such_model") == null, "no model, no brush");
    }
}
