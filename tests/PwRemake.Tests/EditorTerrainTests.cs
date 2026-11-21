// Tests of the map editor's terrain and paint tools (phase 2: src/Sim/Editor/Terrain, docs/MAP_EDITOR.md "Terrain and
// paint"): the brush shapes, smoothing, flattening, the plateau, roughening, the ramp, levelling a rectangle; painting
// ground materials, walkability (overrides over the auto grid), biomes, forest squares and plants; the water level;
// objects and plants following a height edit; and every edit undone exactly (one undo step per stroke).
using System;
using System.IO;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Maps;

namespace PwRemake.Tests;

[TestClass]
public static class EditorTerrainTests
{
    const string Jungle2 = "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer/multi_2_jun_001.ula";

    static MapDataDoc Flat(int size = 256, double h = 20, double? water = 16, string setting = "Savanna") =>
        MapDataDoc.CreateFlat(new NewMapSpec { Size = size, BaseHeight = h, Water = water, Setting = setting });

    static ForestData? ForestTable()
    {
        foreach (var p in new[] { Environment.GetEnvironmentVariable("PW_DATA") ?? "", "/opt/data/base" })
            if (p.Length > 0 && File.Exists(Path.Combine(p, "forest.json"))) return ForestData.LoadFile(Path.Combine(p, "forest.json"));
        return null;
    }

    /// <summary>everything a terrain tool can change, as one comparable string of numbers</summary>
    static string State(MapDataDoc d) => string.Join("|",
        string.Join(",", d.Md.Heights), Convert.ToBase64String(d.Md.Mats), Convert.ToBase64String(d.Md.Walk!.Cells), d.Md.Water,
        string.Join(";", d.Md.Forest.Blocks.Select(b => $"{b.X},{b.Y},{Convert.ToBase64String(b.B)}")),
        string.Join(";", d.Md.Plants.Select(p => $"{p.Name},{p.X},{p.Y},{p.Z}")), string.Join(";", d.Md.Objects.Select(o => $"{o.Name},{o.Z}")),
        d.Md.Biomes == null ? "-" : string.Join(",", d.Md.Biomes.Biomes) + Convert.ToBase64String(d.Md.Biomes.Weights),
        string.Join("", Enumerable.Range(0, d.WalkW * d.WalkH).Select(k => (int)d.GetWalkOverride(k % d.WalkW, k / d.WalkW))));

    public static void EditorTerrainBrushShape(TestContext t)
    {
        t.Equal(1.0, BrushShape.CurveAt(BrushCurve.Smooth, 0), "smooth: 1 at the centre");
        t.Near(0.5625, BrushShape.CurveAt(BrushCurve.Smooth, 0.5), 1e-12, "smooth: the phase 1 falloff");
        t.Near(0.5, BrushShape.CurveAt(BrushCurve.Linear, 0.5), 1e-12, "linear");
        t.Near(Math.Sqrt(0.75), BrushShape.CurveAt(BrushCurve.Sphere, 0.5), 1e-12, "sphere");
        t.Equal(1.0, BrushShape.CurveAt(BrushCurve.Flat, 0.99), "flat: 1 to the edge");
        t.Equal(0.0, BrushShape.CurveAt(BrushCurve.Flat, 1), "0 at the edge");
        var sq = new BrushShape(BrushForm.Square, BrushCurve.Flat);
        t.True(sq.Weight(9, 9, 10) == 1 && BrushShape.Default.Weight(9, 9, 10) == 0, "the square's corner is in it, the circle's not");
        t.Near(HeightBrush.Falloff(5, 10), BrushShape.Default.Weight(3, 4, 10), 1e-12, "the default = HeightBrush.Falloff");
        var hs = Enumerable.Range(0, 1000).Select(k => BrushShape.Hash(k, k * 7, 3)).ToList();
        t.True(hs.All(h => h >= 0 && h < 1) && hs.Average() > 0.45 && hs.Average() < 0.55, "the hash: 0..1, even");
        t.Equal(BrushShape.Hash(4, 5, 6), BrushShape.Hash(4, 5, 6), "the hash: the same cell, the same number");
        t.Equal(new GridRect(23, 23, 26, 26), BrushShape.Cells(100, 100, 6, 0, 0, 4, 64, 64, centred: true), "cells round (100, 100), 6 m, 4 m centred");
        // a square brush through the raise tool
        var d = Flat();
        var e = new HeightBrush { Radius = 8, Strength = 10, Shape = sq }.Step(d, 100, 100, 0.5)!;
        t.True(e.Of(53, 53) is { New: 25f } && e.Of(54, 50) == null, "a flat square: its corners raised fully, nothing at the edge");
    }

    public static void EditorTerrainSmooth(TestContext t)
    {
        var d = Flat();
        var h = new EditHistory(d);
        h.Execute(HeightTools.Level(d, 100, 100, 100, 100, 40)!);          // a 20 m spike at sample (50, 50)
        var b = new HeightBrush { Radius = 10, Strength = 8 };
        for (int k = 0; k < 5; k++) h.Execute(HeightTools.Smooth(d, b, 100, 100, 0.1)!, merge: true);
        t.Equal(2, h.UndoCount, "one step for the stroke");
        t.True(d.Height(50, 50) < 35 && d.Height(51, 50) > 20.5f, $"the spike lower ({d.Height(50, 50):0.0}), its neighbours higher ({d.Height(51, 50):0.0})");
        t.Equal(20f, d.Height(60, 50), "nothing beyond the radius");
        t.Near(d.Height(49, 50), d.Height(51, 50), 1e-4, "symmetric");
        t.True(HeightTools.Smooth(Flat(), b, 100, 100, 0.1) == null, "flat ground: nothing to smooth");
        t.Near(1 - Math.Exp(-2), HeightTools.Rate(8, 1, 1), 1e-12, "the rate: strength / 4 per second");
        h.Undo();
        t.Equal(40f, d.Height(50, 50), "undo: the spike back");
    }

    public static void EditorTerrainFlatten(TestContext t)
    {
        var d = Flat(256, 20);
        var b = new HeightBrush { Radius = 12, Strength = 40, Shape = new BrushShape(BrushForm.Circle, BrushCurve.Flat) };
        var h = new EditHistory(d);
        for (int k = 0; k < 20; k++) if (HeightTools.Flatten(d, b, 128, 128, 0.1, 31) is { } fe) h.Execute(fe, merge: true);
        t.Near(31, d.Height(64, 64), 1e-3, "the centre at the target");
        t.Near(31, d.Height(69, 64), 1e-3, "a flat brush: the whole circle");
        t.Equal(20f, d.Height(71, 64), "beyond: untouched");
        t.True(HeightTools.Flatten(d, b, 128, 128, 0.1, 31) is null or { Count: < 5 }, "at the target already: (almost) nothing");
        var soft = new HeightBrush { Radius = 12, Strength = 4 };
        var e = HeightTools.Flatten(Flat(256, 20), soft, 128, 128, 0.25, 10)!;
        t.Near(20 + (10 - 20) * HeightTools.Rate(4, 0.25, 1), e.Of(64, 64)!.Value.New, 1e-4, "a share of the way per step");
        t.True(e.Of(68, 64)!.Value.New > e.Of(64, 64)!.Value.New, "less at the edge (the falloff)");
    }

    public static void EditorTerrainPlateau(TestContext t)
    {
        var d = Flat(256, 20);
        var b = new HeightBrush { Radius = 10 };
        var e = HeightTools.Plateau(d, b, 128, 128, 30, 45)!;
        new EditHistory(d).Execute(e);
        t.Equal(30f, d.Height(64, 64), "the top");
        t.Equal(30f, d.Height(69, 64), "10 m out: still the top");
        t.Near(26, d.Height(71, 64), 1e-4, "14 m out at 45 degrees: 4 m below the top");
        t.Equal(20f, d.Height(74, 64), "20 m out: down on the ground");
        t.True(HeightTools.Plateau(d, b, 128, 128, 30, 45) == null, "the same plateau again: nothing");
        // cutting into higher ground
        var hi = Flat(256, 40);
        new EditHistory(hi).Execute(HeightTools.Plateau(hi, new HeightBrush { Radius = 8 }, 128, 128, 30, 30)!);
        t.Equal(30f, hi.Height(64, 64), "cut down to the top");
        t.Near(30 + 4 * Math.Tan(Math.PI / 6), hi.Height(70, 64), 1e-4, "its side rising at 30 degrees");
        t.Equal(40f, hi.Height(64 + 30, 64), "far out: the ground");
    }

    public static void EditorTerrainRoughenRampLevel(TestContext t)
    {
        var b = new HeightBrush { Radius = 20, Strength = 4 };
        var d1 = Flat(); var d2 = Flat();
        var e1 = HeightTools.Roughen(d1, b, 128, 128, 0.5)!; var e2 = HeightTools.Roughen(d2, b, 128, 128, 0.5)!;
        t.True(e1.Count > 50, $"roughened ({e1.Count} samples)");
        t.True(Enumerable.Range(54, 20).All(i => e1.Of(i, 64)?.New == e2.Of(i, 64)?.New), "the same pattern each time");
        t.True(Enumerable.Range(54, 20).All(i => e1.Of(i, 64) is not { } v || Math.Abs(v.New - 20) <= 2.0001), "at most strength x dt");
        // a ramp from (100, 100) at 20 m to (160, 100) at 40 m, 8 m wide, 4 m border
        var d = Flat(256, 20);
        new EditHistory(d).Execute(HeightTools.Ramp(d, 100, 100, 20, 160, 100, 40, 8, 4)!);
        t.Near(30, d.Height(65, 50), 1e-4, "half way: half the rise");
        t.Near(30, d.Height(65, 52), 1e-4, "4 m to the side: on the ramp still");
        t.Near(20, d.Height(65, 55), 1e-4, "10 m to the side: the ground");
        t.True(d.Height(65, 53) > 20 && d.Height(65, 53) < 30, "the border blends");
        t.Equal(20f, d.Height(82, 50), "beyond the end: untouched");
        // level a rectangle (corners either way round)
        var lv = HeightTools.Level(d, 30, 40, 20, 20, 12)!;
        t.Equal(new GridRect(10, 10, 15, 20), lv.Rect, "the samples inside");
        t.True(lv.Of(12, 15) == (20f, 12f), "set to the height");
    }

    public static void EditorTerrainPaintMaterials(TestContext t)
    {
        var d = Flat();
        var h = new EditHistory(d);
        var before = State(d);
        int changes = 0; d.Changed += c => { if (c.Kind == DocChangeKind.Materials) changes++; };
        var hard = new HeightBrush { Radius = 10, Shape = new BrushShape(BrushForm.Circle, BrushCurve.Flat) };
        for (int k = 0; k < 4; k++) h.Execute(PaintTools.Materials(d, hard, 100 + k * 3, 100, 5)!, merge: true);
        t.Equal(1, h.UndoCount, "one stroke");
        t.Equal(5, d.Material(25, 25), "the cell under the brush");
        t.Equal(5, d.MaterialAt(100, 100), "MaterialAt agrees");
        t.Equal(0, d.Material(25, 30), "20 m away: not painted");
        t.True(changes == 4, "a Materials change per step");
        t.True(PaintTools.Materials(d, hard, 100, 100, 5) == null, "painted already: nothing");
        h.Undo();
        t.Equal(before, State(d), "undo: exactly as before");
        // a soft brush: the middle painted, a dithered edge
        var e = PaintTools.Materials(d, new HeightBrush { Radius = 24 }, 128, 128, 3)!;
        t.True(e.Of(31, 31) != null && e.Of(32, 32) != null, "the middle painted");
        int ring = Enumerable.Range(0, 64).Count(a => e.Of(32 + (int)Math.Round(4.5 * Math.Cos(a)), 32 + (int)Math.Round(4.5 * Math.Sin(a))) != null);
        t.True(ring > 0 && ring < 64, $"the edge dithered ({ring} of 64)");
        t.True(PaintTools.Materials(d, new HeightBrush { Radius = 1 }, 101, 101, 2) != null, "a brush smaller than a cell paints the cell under it");
    }

    public static void EditorTerrainWalkOverrides(TestContext t)
    {
        var d = Flat();
        t.True(d.Md.Walk != null && d.WalkW == d.HeightsW, "a new map gets a walk grid (the slope rule and the water)");
        t.True(d.WalkableAt(100, 100), "flat land walkable");
        var h = new EditHistory(d);
        var before = State(d);
        var b = new HeightBrush { Radius = 6 };
        h.Execute(PaintTools.Walk(d, b, 100, 100, WalkOverride.Blocked)!);
        t.True(!d.WalkableAt(100, 100) && d.WalkableAt(120, 100), "blocked under the brush only");
        t.Equal(WalkOverride.Blocked, d.GetWalkOverride(50, 50), "the override");
        // a hill raised over it: a blocked cell stays blocked; the steep sides elsewhere follow the slope rule
        h.Execute(HeightTools.Plateau(d, new HeightBrush { Radius = 4 }, 140, 100, 40, 70)!);
        t.True(!d.WalkableAt(146, 100), "the plateau's steep side: not walkable (auto)");
        t.True(!d.WalkableAt(100, 100), "the painted cell stays blocked");
        h.Execute(PaintTools.Walk(d, new HeightBrush { Radius = 2 }, 146, 100, WalkOverride.Walkable)!);
        t.True(d.WalkableAt(146, 100), "painted walkable on the steep side");
        h.Execute(PaintTools.Walk(d, new HeightBrush { Radius = 2 }, 146, 100, WalkOverride.Auto)!);
        t.True(!d.WalkableAt(146, 100), "auto again: the slope rule");
        while (h.Undo()) { }
        t.Equal(before, State(d), "everything undone: exactly as before");
        var c = (MapDataDoc)d.Clone();
        d.SetWalkOverride(3, 3, WalkOverride.Blocked);
        t.Equal(WalkOverride.Auto, c.GetWalkOverride(3, 3), "Clone: its own overrides");
    }

    public static void EditorTerrainWater(TestContext t)
    {
        var d = Flat(256, 20, 16);
        var h = new EditHistory(d);
        var before = State(d);
        int water = 0; d.Changed += c => { if (c.Kind == DocChangeKind.Water) water++; };
        h.Execute(PaintTools.Water(d, 18)!, merge: true);
        h.Execute(PaintTools.Water(d, 22)!, merge: true);
        t.Equal(1, h.UndoCount, "a slider's drag: one step");
        t.Equal(22.0, d.Water, "the level");
        t.True(!d.WalkableAt(100, 100), "flooded: not walkable");
        t.True(PaintTools.Water(d, 22) == null, "the same level: nothing");
        h.Execute(PaintTools.Water(d, null)!);
        t.True(d.Water == null && d.WalkableAt(100, 100), "no water");
        t.True(water == 3, "Water changes");
        h.Undo(); h.Undo();
        t.Equal(before, State(d), "undone exactly");
        if (!File.Exists(Jungle2)) return;
        // an original map: its own grid back exactly after the water went up and down again
        var o = MapDataDoc.Open(Jungle2);
        var ob = State(o);
        var oh = new EditHistory(o);
        oh.Execute(PaintTools.Water(o, 19)!);
        int lost = Enumerable.Range(0, o.WalkW * o.WalkH).Count(k => o.Md.Walk!.Cells[k] == 0);
        oh.Undo();
        int had = Enumerable.Range(0, o.WalkW * o.WalkH).Count(k => o.Md.Walk!.Cells[k] == 0);
        t.True(lost > had, $"more blocked under the higher water ({had} -> {lost})");
        t.Equal(ob, State(o), "undo: the map's own grid");
    }

    public static void EditorTerrainBiomes(TestContext t)
    {
        var d = Flat(256, 20, 16, "Savanna");
        var h = new EditHistory(d);
        var kinds = new System.Collections.Generic.List<DocChangeKind>();
        d.Changed += c => kinds.Add(c.Kind);
        for (int k = 0; k < 3; k++) h.Execute(PaintTools.Biome(d, "Jungle", 128, 128, 60, 0.4)!, merge: true);
        t.True(d.Biomes != null && d.Biomes.Biomes.SequenceEqual(new[] { "Savanna", "Jungle" }), "a layer: the setting and the painted biome");
        t.True(d.Biomes!.At(128, 128)[1] > 0.6, $"jungle in the middle ({d.Biomes.At(128, 128)[1]:0.00})");
        t.True(d.Biomes.At(10, 10)[0] > 0.99, "savanna far away");
        t.True(kinds[0] == DocChangeKind.All && kinds.Skip(1).All(k => k == DocChangeKind.Biomes), "the first step draws anew (a biome added), the rest only the area");
        var layer = d.Biomes;
        h.Execute(PaintTools.Biome(d, "Jungle", 60, 60, 30, 1)!);
        t.True(ReferenceEquals(layer, d.Biomes), "the same biomes: painted in place (the view's lookup holds the layer)");
        t.Equal(2, h.UndoCount, "two strokes");
        var mid = Convert.ToBase64String(d.Biomes!.Weights);
        h.Undo();
        t.True(d.Biomes!.At(60, 60)[1] < 0.5, "the second stroke undone");
        h.Undo();
        t.True(d.Biomes == null, "the first undone: no layer");
        h.Redo(); h.Redo();
        t.Equal(mid, Convert.ToBase64String(d.Biomes!.Weights), "redone: the same weights");
        var c = (MapDataDoc)d.Clone();
        t.True(!ReferenceEquals(c.Biomes, d.Biomes) && c.Biomes!.Weights.SequenceEqual(d.Biomes.Weights), "Clone: a copy of the layer");
    }

    public static void EditorTerrainForest(TestContext t)
    {
        var fd = ForestTable();
        if (fd == null) t.Skip("no forest.json");
        var d = Flat(512, 20, 16, "Jungle");
        var h = new EditHistory(d);
        var before = State(d);
        var kinds = VegetationTools.TreeKinds(fd, 3, 4)!;
        t.True(kinds.Length == 15 && kinds.All(k => k >= 0 && k < 5), "the layout's tree kinds of a square");
        var p = new ForestPaint();
        var b = new HeightBrush { Radius = 40 };
        h.Execute(VegetationTools.PaintForest(d, fd, b, 200, 200, p)!, merge: true);
        h.Execute(VegetationTools.PaintForest(d, fd, b, 230, 200, p)!, merge: true);
        t.Equal(1, h.UndoCount, "one stroke");
        int n = d.Md.Forest.Blocks.Count;
        t.True(n >= 4, $"forest squares ({n})");
        var sq = d.ForestSquare(6, 6)!;
        t.True(sq.All(v => (v >> 5) == 2 && (v & 31) >= 9 && (v & 31) <= 18), "every item standing, amounts 9..18 as the original maps");
        var items = fd!.ForestItems(d.Md, d.HeightAt);
        t.True(items.Trees.Count >= 15 * n * 0.9 && items.Deco.Count > 0, $"trees ({items.Trees.Count}) and undergrowth ({items.Deco.Count}) stand");
        // only one kind, half the density
        var one = new ForestPaint { Density = 0.5, Kinds = new[] { false, false, true, false, false }, Undergrowth = false };
        h.Execute(VegetationTools.PaintForest(d, fd, new HeightBrush { Radius = 1 }, 400, 400, one)!);
        var sq2 = d.ForestSquare(12, 12)!;
        var k2 = VegetationTools.TreeKinds(fd, 12, 12)!;
        t.True(Enumerable.Range(0, 15).All(i => sq2[i] == 0 || k2[i] == 2) && sq2.Skip(15).All(v => v == 0), "only kind 2, no undergrowth");
        // erase: emptied, not removed
        h.Execute(VegetationTools.PaintForest(d, fd, new HeightBrush { Radius = 1 }, 200, 200, new ForestPaint { Erase = true })!);
        t.True(d.ForestSquare(6, 6)!.All(v => v == 0), "erased");
        t.True(VegetationTools.PaintForest(d, fd, new HeightBrush { Radius = 1 }, 300, 50, new ForestPaint { Erase = true }) == null, "erasing where there is none: nothing");
        while (h.Undo()) { }
        t.Equal(before, State(d), "undone exactly");
        // an original map: erase and undo keeps its list in order
        if (!File.Exists(Jungle2)) return;
        var o = MapDataDoc.Open(Jungle2);
        var ob = State(o);
        var blk = o.Md.Forest.Blocks[0];
        var oh = new EditHistory(o);
        oh.Execute(VegetationTools.PaintForest(o, fd, new HeightBrush { Radius = 60 }, blk.X * 32 + 16, blk.Y * 32 + 16, new ForestPaint { Erase = true })!);
        oh.Execute(VegetationTools.PaintForest(o, fd, new HeightBrush { Radius = 60 }, blk.X * 32 + 16, blk.Y * 32 + 16, new ForestPaint())!);
        oh.Undo(); oh.Undo();
        t.Equal(ob, State(o), "an original map's forest back exactly");
    }

    public static void EditorTerrainPlants(TestContext t)
    {
        var d = Flat(256, 20, 16, "Jungle");
        var h = new EditHistory(d);
        var before = State(d);
        var p = new PlantScatter { Kinds = { "Jungle_Bush_02", "Jungle_Fern_01" }, Density = 20, Spacing = 3 };
        var rng = new SimRandom(7);
        var b = new HeightBrush { Radius = 15 };
        for (int k = 0; k < 10; k++) { var e = VegetationTools.ScatterPlants(d, b, 128, 128, 0.1, p, rng); if (e != null) h.Execute(e, merge: true); }
        int n = d.Plants.Count;
        t.True(n > 10, $"plants scattered ({n})");
        t.Equal(1, h.UndoCount, "one stroke");
        t.True(d.Plants.All(q => Math.Sqrt((q.X - 128) * (q.X - 128) + (q.Y - 128) * (q.Y - 128)) < 15), "within the brush");
        bool spaced = true;
        for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) { var a = d.Plants[i]; var c = d.Plants[j]; if ((a.X - c.X) * (a.X - c.X) + (a.Y - c.Y) * (a.Y - c.Y) < 9) spaced = false; }
        t.True(spaced, "3 m apart");
        t.True(d.Plants.All(q => q.Z == 20 && p.Kinds.Contains(q.Name)), "on the ground, of the chosen kinds");
        t.Near(d.Plants[0].Rot, -2 * Math.Atan2(d.Plants[0].Q[2], d.Plants[0].Q[3]), 1e-9, "the quaternion gives the turn (as Ula reads it)");
        // under water: none
        var wet = Flat(256, 10, 16, "Jungle");
        t.True(VegetationTools.ScatterPlants(wet, b, 128, 128, 1, p, rng) == null, "nothing under the water");
        p.InWater = true;
        t.True(VegetationTools.ScatterPlants(wet, b, 128, 128, 1, p, rng) != null, "unless allowed");
        // erase half of them (only the bushes), then undo
        var mid = State(d);
        var er = new PlantScatter { Kinds = { "Jungle_Bush_02" }, Erase = true, Only = true };
        h.Execute(VegetationTools.ScatterPlants(d, new HeightBrush { Radius = 30 }, 128, 128, 0.1, er, rng)!);
        t.True(d.Plants.All(q => q.Name == "Jungle_Fern_01"), "only the bushes erased");
        h.Undo();
        t.Equal(mid, State(d), "erase undone: the same list in the same order");
        h.Undo();
        t.Equal(before, State(d), "scatter undone");
    }

    public static void EditorTerrainObjectsFollowGround(TestContext t)
    {
        var d = Flat(256, 20);
        d.Md.Objects.RemoveAll(o => o.Type == "SLOC");
        d.Md.Objects.Add(new MapObject { Type = "TREE", Name = "tree", Cls = "Jungle_Tree_01", X = 128, Y = 128, Z = 20.2 });
        d.Md.Objects.Add(new MapObject { Type = "SHIP", Name = "ship", Cls = "Hu_Boat", X = 130, Y = 128, Z = 25 });
        d.Md.Objects.Add(new MapObject { Type = "DCCO", Name = "rock", Cls = "Rock", X = 126, Y = 128, Z = 17 });
        d.Md.Objects.Add(new MapObject { Type = "TREE", Name = "far", Cls = "Jungle_Tree_01", X = 20, Y = 20, Z = 20 });
        d.Md.Plants.Add(new MapPlant { Name = "Jungle_Bush_02", X = 128, Y = 126, Z = 20 });
        var before = State(d);
        var h = new EditHistory(d);
        var b = new HeightBrush { Radius = 20, Strength = 10 };
        for (int k = 0; k < 10; k++) h.Execute(b.Step(d, 128, 128, 0.1)!, merge: true);
        double g = d.HeightAt(128, 128);
        t.True(g > 29, "a hill");
        t.Near(g + 0.2, d.Md.Objects[0].Z, 1e-4, "the tree on it (its 0.2 m kept)");
        t.Equal(25.0, d.Md.Objects[1].Z, "the ship (5 m above the ground) stays");
        t.Near(d.HeightAt(126, 128) - 3, d.Md.Objects[2].Z, 1e-4, "the sunk rock follows, 3 m deep");
        t.Equal(20.0, d.Md.Objects[3].Z, "far away: untouched");
        t.Near(d.HeightAt(128, 126), d.Md.Plants[0].Z, 1e-4, "the plant too");
        h.Undo();
        t.Equal(before, State(d), "undo: every Z exactly back");
        h.Redo();
        t.Near(d.HeightAt(128, 128) + 0.2, d.Md.Objects[0].Z, 1e-4, "redo: on the hill again");
        // moved by hand (the objects tool): seated anew from where it is
        d.Md.Objects[0].X = 140; d.Md.Objects[0].Z = d.HeightAt(140, 128);
        h.Execute(new HeightBrush { Radius = 6, Strength = 10, Lower = true }.Step(d, 140, 128, 0.5)!);
        t.Near(d.HeightAt(140, 128), d.Md.Objects[0].Z, 1e-4, "a moved object follows from its new place");
    }
}
