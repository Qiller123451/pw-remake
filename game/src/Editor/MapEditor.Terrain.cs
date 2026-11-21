// The map editor screen's part for the tools of phase 2 (docs/MAP_EDITOR.md "Terrain and paint"): the tools' context,
// the view following the document's terrain changes (only the touched area: the ground's materials, the biomes, the
// grass, the trees and decoration, the water), the map's objects standing on edited ground, and the self-test's stroke
// of every terrain and paint tool.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Maps;
using PwRemake.View.Models;

namespace PwRemake.Editor;

public partial class MapEditor
{
    readonly ToolContext _ctx = new();
    GridRect _vegRect = GridRect.Empty;
    double _vegT;
    // the map's objects drawn as models (WorldView.MapModels): their height over the ground when drawn
    readonly Dictionary<ModelInstance, double> _modelOffsets = new(ReferenceEqualityComparer.Instance);
    /// <summary>trees and decoration of an edited area are drawn anew at most this often during a stroke (s)</summary>
    const double VegetationEvery = 0.2;

    /// <summary>the tools' context now (the modifier keys of this moment)</summary>
    ToolContext Ctx()
    {
        _ctx.Doc = _doc!; _ctx.History = EditorSession.History!; _ctx.Source = _src!; _ctx.View = _view!;
        _ctx.Ctrl = Input.IsKeyPressed(Key.Ctrl); _ctx.Shift = Input.IsKeyPressed(Key.Shift);
        _ctx.Note = SetStatusNote; _ctx.Rebuild = BuildProperties;
        return _ctx;
    }

    ToolPoint? PointOf(Vector3? h)
    {
        if (h is not { } p || _src == null) return null;
        var (mx, my) = _src.ToMap(p.X, p.Z);
        return new ToolPoint(p.X, p.Y, p.Z, mx, my);
    }

    /// <summary>the view was drawn (LoadView): the models' heights over the ground, the tool panel for this map</summary>
    void TerrainViewLoaded()
    {
        _modelOffsets.Clear();
        _vegRect = GridRect.Empty;
        foreach (var m in _view!.MapModels) _modelOffsets[m] = m.Position.Y - _view.Ground.At(m.Position.X, m.Position.Z);
        // --editor-tool <label>: that tool chosen (screenshots, tests)
        if (_args.TryGetValue("editor-tool", out var want) && EditorTools.All.FirstOrDefault(t => t.Label.Replace(" ", "").Equals(want.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) is { } wt)
        {
            EditorSession.ToolGroup = wt.Group;
            EditorTools.Choose(wt);
            BuildPalette();
        }
        BuildProperties();
        if (_args.ContainsKey("editor-demo") && !_demoDone) { _demoDone = true; Demo(); }
    }
    static bool _demoDone;

    /// <summary>--editor-demo: a plateau, painted ground, a forest and plants round the camera's target, kept (for a
    /// screenshot of the tools' results)</summary>
    void Demo()
    {
        var c = Ctx();
        var at = _cam!.Target;
        var p0 = PointOf(new Vector3(at.X, (float)_view!.Ground.At(at.X, at.Z), at.Z))!.Value;
        var b = EditorSession.Brush;
        void Stroke(Func<int, IEditCommand?> make, int n) { for (int k = 0; k < n; k++) c.Run(make(k)); History.EndMerge(); }
        b.Radius = 14;
        Stroke(k => HeightTools.Plateau(Doc, b, p0.Mx + 30, p0.My - 10, p0.Y + 7, 35), 1);
        b.Radius = 10;
        Stroke(k => PaintTools.Materials(Doc, b, p0.Mx - 25 + k * 3, p0.My + 15, (Doc.MaterialAt(p0.Mx, p0.My) + 4) % 8), 12);
        b.Radius = 24;
        Stroke(k => VegetationTools.PaintForest(Doc, EditorSession.Forest, b, p0.Mx - 60, p0.My - 40, new ForestPaint()), 1);
        var rng = new Sim.Core.SimRandom(3);
        var ps = new PlantScatter { Kinds = PlantPaintTool.KindsOf(Doc).Where(k => k.Contains("bush") || k.Contains("fern")).Take(4).ToList(), Density = 12, Spacing = 2.5 };
        b.Radius = 12;
        Stroke(k => VegetationTools.ScatterPlants(Doc, b, p0.Mx + 5 + k * 2, p0.My + 35, 0.1, ps, rng), 10);
        RefreshVegetationNow();
        L.Info($"EDITOR demo: {History.UndoCount} edits round ({p0.Mx:0}, {p0.My:0})");
    }

    /// <summary>the game rectangle (x0, z0, x1, z1) of the document's samples r, m metres around</summary>
    (double X0, double Z0, double X1, double Z1) GameRect(GridRect r, double m)
    {
        var d = Doc;
        var (x0, z0) = _src!.ToGame(d.OriginX + r.I0 * d.Cell - m, d.OriginY + r.J0 * d.Cell - m);
        var (x1, z1) = _src.ToGame(d.OriginX + r.I1 * d.Cell + m, d.OriginY + r.J1 * d.Cell + m);
        return (x0, z0, x1, z1);
    }

    /// <summary>the terrain and paint changes of the document (OnDocChanged): the view there follows</summary>
    void OnTerrainChanged(DocChange c)
    {
        var v = _view!;
        switch (c.Kind)
        {
            case DocChangeKind.Materials:
            {
                var (x0, z0, x1, z1) = GameRect(c.Rect, 6);
                v.Terrain.RefreshSplat(x0, z0, x1, z1);
                v.InvalidateGrass((x0 + x1) / 2, (z0 + z1) / 2, Math.Max(x1 - x0, z1 - z0) / 2 + 4);
                break;
            }
            case DocChangeKind.Walk:
            {
                var (x0, z0, x1, z1) = GameRect(c.Rect, 2);
                v.InvalidateGrass((x0 + x1) / 2, (z0 + z1) / 2, Math.Max(x1 - x0, z1 - z0) / 2 + 4);      // (no grass where units cannot walk)
                break;
            }
            case DocChangeKind.Biomes:
            {
                var (x0, z0, x1, z1) = GameRect(c.Rect, 0);
                v.RefreshBiomes(x0, z0, x1, z1);
                break;
            }
            case DocChangeKind.Forest:
            case DocChangeKind.Plants:
                QueueVegetation(c.Rect);
                break;
            case DocChangeKind.Water:
            {
                var w = Doc.Water;
                if ((v.Water == null) != (w == null)) { _loadAt = _frames + 1; break; }      // (water came or went: drawn anew)
                _src!.Water = w;
                if (w is double level)
                {
                    v.Water!.SetLevel(level);
                    foreach (var g in v.GrassLayers) g.WaterLevel = level;
                }
                v.InvalidateGrass();
                break;
            }
        }
    }

    /// <summary>the trees and decoration over samples r are drawn anew soon (now when no stroke is held)</summary>
    void QueueVegetation(GridRect r)
    {
        if (_vegRect.IsEmpty) _vegT = VegetationEvery;
        _vegRect = _vegRect.Union(r);
    }

    /// <summary>every frame: the queued vegetation refresh (during a stroke at most every VegetationEvery s)</summary>
    void TerrainFrame(double dt)
    {
        if (_vegRect.IsEmpty || _view == null) return;
        if ((_vegT -= dt) > 0 && _stroke) return;
        RefreshVegetationNow();
    }

    void RefreshVegetationNow()
    {
        var r = _vegRect;
        _vegRect = GridRect.Empty;
        if (r.IsEmpty || _view == null || _doc == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fresh = EditorSession.SourceOf(Doc);
        var (x0, z0, x1, z1) = GameRect(r, 2);
        _view.RefreshVegetation(fresh, x0, z0, x1, z1);
        // the map's objects (buildings, units, wells ...) on the ground there follow it, their offset kept
        foreach (var m in _view.MapModels)
        {
            var p = m.Position;
            if (p.X < x0 || p.X > x1 || p.Z < z0 || p.Z > z1 || !_modelOffsets.TryGetValue(m, out var off) || off > MapDataDoc.SeatAbove) continue;
            m.Position = new Vector3(p.X, (float)(_view.Ground.At(p.X, p.Z) + off), p.Z);
        }
        L.Debug($"vegetation refreshed in {sw.ElapsedMilliseconds} ms");
    }

    // ================================================================================ the self-test of phase 2

    /// <summary>everything the terrain and paint tools change, as one string (a MapDataDoc; null otherwise)</summary>
    static string? DocState(IEditorMapDoc d)
    {
        if (d is not MapDataDoc m) return null;
        var md = m.Md;
        return string.Join("|", string.Join(",", md.Heights), Convert.ToBase64String(md.Mats), md.Walk == null ? "" : Convert.ToBase64String(md.Walk.Cells), md.Water,
            string.Join(";", md.Forest.Blocks.Select(b => $"{b.X},{b.Y},{Convert.ToBase64String(b.B)}")), string.Join(";", md.Plants.Select(p => $"{p.Name},{p.X},{p.Y},{p.Z}")),
            string.Join(";", md.Objects.Select(o => o.Z)), md.Biomes == null ? "-" : Convert.ToBase64String(md.Biomes.Weights),
            string.Join("", Enumerable.Range(0, m.WalkW * m.WalkH).Select(k => (int)m.GetWalkOverride(k % m.WalkW, k / m.WalkW))));
    }

    /// <summary>--editor-selftest, phase 2: one stroke of every terrain and paint tool at the camera's target through the
    /// tools' own path (Begin / Step / End, the history, the view's refresh), each one undo step that undoes exactly</summary>
    void TerrainSelfTest(Action<bool, string> T)
    {
        var at = _cam!.Target;
        var c = Ctx();
        var p0 = PointOf(new Vector3(at.X, (float)_view!.Ground.At(at.X, at.Z), at.Z))!.Value;
        ToolPoint P(double dx, double dz) => c.At(p0.Mx + dx, p0.My + dz);
        var groupWas = EditorSession.ToolGroup;
        var brush = EditorSession.Brush;
        double radius = brush.Radius;
        brush.Radius = 16;
        PaintSettings.Plants.Kinds.Clear();
        PaintSettings.Plants.Kinds.AddRange(PlantPaintTool.KindsOf(Doc).Take(3));
        PaintSettings.Material = (Doc.MaterialAt(p0.Mx, p0.My) + 3) % 8;
        PaintSettings.Walk = WalkOverride.Blocked;
        PaintSettings.Biome = BiomeSet.Default.All.FirstOrDefault(b => !b.Id.Equals(Doc.Setting, StringComparison.OrdinalIgnoreCase))?.Id ?? "Jungle";
        int trees0 = _view.Trees.Count(t => !t.Gone);
        // smooth, flatten, ramp and level change nothing on flat ground (a new map): a hill under the strokes first, one
        // undo step that is undone at the end
        double hs = 0, hmin = double.MaxValue, hmax = double.MinValue;
        for (int k = -2; k <= 2; k++) for (int l = -2; l <= 2; l++) { hs = Doc.HeightAt(p0.Mx + k * 6, p0.My + l * 6); hmin = Math.Min(hmin, hs); hmax = Math.Max(hmax, hs); }
        bool relief = hmax - hmin < 0.5;
        if (relief)
        {
            bool lower = brush.Lower; brush.Lower = false;
            for (int k = 0; k < 8; k++) History.Execute(brush.Step(Doc, p0.Mx + 4, p0.My + 2, 0.2)!, merge: true);
            History.EndMerge();
            brush.Lower = lower;
            L.Info($"EDITOR      flat ground: a hill first ({Doc.HeightAt(p0.Mx + 4, p0.My + 2) - hmin:0.0} m)");
        }
        foreach (var tool in EditorTools.All)
        {
            if (tool.Group is not ("Terrain" or "Paint")) continue;     // (the object and player tools: their own self-test)
            if (tool is PlantPaintTool && PaintSettings.Plants.Kinds.Count == 0) { L.Info("EDITOR skip plants: no plant models for the setting"); continue; }
            EditorSession.ToolGroup = tool.Group;
            EditorTools.Choose(tool);
            string? before = DocState(Doc);
            int undo0 = History.UndoCount;
            c = Ctx();
            c.Ctrl = c.Shift = false;
            switch (tool)
            {
                case RampTool: tool.Begin(c, P(-20, 0)); tool.Begin(c, P(20, 10)); break;
                case LevelTool: tool.Begin(c, P(-10, -10)); tool.Step(c, P(0, 0), 0.1); tool.End(c, P(12, 8)); break;
                case WaterTool: tool.Begin(c, P(0, 0) with { Y = (Doc.Water ?? 16) + 1.5 }); break;
                case PlateauTool: TerrainSettings.Typed = false; goto default;
                default:
                    _stroke = true;
                    tool.Begin(c, P(0, 0));
                    for (int k = 0; k < 8; k++) tool.Step(c, P(k * 2, k), 0.1);
                    tool.End(c, P(16, 8));
                    _stroke = false;
                    History.EndMerge();
                    break;
            }
            string? after = DocState(Doc);
            T(History.UndoCount == undo0 + 1 && before != after, $"{tool.Group} / {tool.Label}: one undo step that changed the map");
            if (tool is ForestPaintTool or PlantPaintTool)
            {
                RefreshVegetationNow();
                L.Info($"EDITOR      {tool.Label}: {_view.Trees.Count(t => !t.Gone)} trees drawn (were {trees0}), {_view.Decor.Count(e => !e.Gone)} decorations, {Doc.Plants.Count} plants");
            }
            History.Undo();
            RefreshVegetationNow();
            T(DocState(Doc) == before, $"{tool.Group} / {tool.Label}: undo gives the map back exactly");
        }
        if (relief) History.Undo();
        T(_view.Trees.Count(t => !t.Gone) == trees0, "the view's trees as before");
        brush.Radius = radius;
        EditorSession.ToolGroup = groupWas;
        BuildPalette(); BuildProperties();
    }
}
