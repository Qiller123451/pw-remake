// The map editor's paint tools (docs/MAP_EDITOR.md "Paint"): ground materials (the setting's eight textures, 4 m cells),
// walkability (2 m walk cells: walkable / blocked / auto), the biome layer (docs/BIOMES.md, 8 m cells) and the water
// level - and their commands for undo. A brush step returns its command (not done yet); the steps of a stroke merge
// into one undo step. Plain maths, no Godot.
//
// Materials are discrete: a soft brush (any falloff but Flat) paints a cell where its weight beats the cell's own hash
// (BrushShape.Hash) - a dithered, irregular edge like the original's painted borders; the Flat curve paints a hard
// edge. The view blends the cells anyway (MapSource's noise-warped splat).
using System;
using System.Collections.Generic;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

/// <summary>a change of cells of one of the document's grids (each cell's old and new value; a stroke's steps merge:
/// the first old, the last new value)</summary>
public abstract class CellEdit : IEditCommand
{
    readonly Dictionary<int, (int Old, int New)> _c = new();
    readonly List<int> _order = new();
    protected int W;
    public GridRect Rect { get; private set; } = GridRect.Empty;
    public abstract string Name { get; }
    public int Count => _c.Count;

    protected CellEdit(int w) { W = w; }

    public void Add(int i, int j, int oldV, int newV)
    {
        int k = j * W + i;
        if (_c.TryGetValue(k, out var v)) _c[k] = (v.Old, newV);
        else { _c[k] = (oldV, newV); _order.Add(k); }
        Rect = Rect.Union(new GridRect(i, j, i, j));
    }

    /// <summary>cell (i, j)'s (old, new) value; null when the edit does not touch it</summary>
    public (int Old, int New)? Of(int i, int j) => _c.TryGetValue(j * W + i, out var v) ? v : null;

    protected abstract void Set(IEditorMapDoc d, int i, int j, int v);
    protected abstract void Done(IEditorMapDoc d, GridRect r);

    public void Do(IEditorMapDoc d) { foreach (var k in _order) Set(d, k % W, k / W, _c[k].New); Done(d, Rect); }
    public void Undo(IEditorMapDoc d) { foreach (var k in _order) Set(d, k % W, k / W, _c[k].Old); Done(d, Rect); }

    public bool TryMerge(IEditCommand next)
    {
        if (next is not CellEdit c || c.GetType() != GetType() || c.W != W) return false;
        foreach (var k in c._order) Add(k % W, k / W, c._c[k].Old, c._c[k].New);
        return true;
    }
}

/// <summary>ground materials painted (4 m cells)</summary>
public sealed class MaterialEdit : CellEdit
{
    public MaterialEdit(int w) : base(w) { }
    public override string Name => "Paint ground";
    protected override void Set(IEditorMapDoc d, int i, int j, int v) => d.SetMaterial(i, j, v);
    protected override void Done(IEditorMapDoc d, GridRect r) => d.MaterialsChanged(r);
}

/// <summary>walk cells painted walkable / blocked / auto (2 m cells; the value is a WalkOverride)</summary>
public sealed class WalkEdit : CellEdit
{
    public WalkEdit(int w) : base(w) { }
    public override string Name => "Paint walkability";
    protected override void Set(IEditorMapDoc d, int i, int j, int v) => d.SetWalkOverride(i, j, (WalkOverride)v);
    protected override void Done(IEditorMapDoc d, GridRect r) => d.WalkChanged(r);
}

/// <summary>the water level changed (null: no water); a slider's drag merges into one step</summary>
public sealed class WaterEdit : IEditCommand
{
    public double? Old, New;
    public WaterEdit(double? oldLevel, double? newLevel) { Old = oldLevel; New = newLevel; }
    public string Name => "Water level";
    public void Do(IEditorMapDoc d) => d.SetWater(New);
    public void Undo(IEditorMapDoc d) => d.SetWater(Old);
    public bool TryMerge(IEditCommand next) { if (next is not WaterEdit w) return false; New = w.New; return true; }
}

/// <summary>the biome layer before and after (copies: a layer is small - 128 x 128 cells x biomes bytes on a 1 km
/// map); the samples the change covers</summary>
public sealed class BiomeEdit : IEditCommand
{
    public BiomeLayer? Before, After;
    public GridRect Rect;
    public BiomeEdit(BiomeLayer? before, BiomeLayer? after, GridRect rect) { Before = before; After = after; Rect = rect; }
    public string Name => "Paint biome";
    public void Do(IEditorMapDoc d) => d.SetBiomes(After, Rect);
    public void Undo(IEditorMapDoc d) => d.SetBiomes(Before, Rect);
    public bool TryMerge(IEditCommand next)
    {
        if (next is not BiomeEdit b) return false;
        After = b.After; Rect = Rect.Union(b.Rect);
        return true;
    }
}

public static class PaintTools
{
    /// <summary>paints ground material m (0..7) with the brush at map (mx, my)</summary>
    public static MaterialEdit? Materials(IEditorMapDoc d, HeightBrush b, double mx, double my, int m)
    {
        if (d.MatsW <= 0 || m < 0 || m > 7) return null;
        var r = BrushShape.Cells(mx, my, b.Radius, d.OriginX, d.OriginY, d.MatCell, d.MatsW, d.MatsH, centred: true);
        var e = new MaterialEdit(d.MatsW);
        for (int j = r.J0; j <= r.J1; j++)
            for (int i = r.I0; i <= r.I1; i++)
            {
                double w = b.Shape.Weight(d.OriginX + (i + 0.5) * d.MatCell - mx, d.OriginY + (j + 0.5) * d.MatCell - my, b.Radius);
                if (w <= 0 || (b.Shape.Curve != BrushCurve.Flat && w < BrushShape.Hash(i, j, 77))) continue;
                int old = d.Material(i, j);
                if (old != m) e.Add(i, j, old, m);
            }
        // (a brush smaller than a cell still paints the cell under it)
        int ci = (int)Math.Floor((mx - d.OriginX) / d.MatCell), cj = (int)Math.Floor((my - d.OriginY) / d.MatCell);
        if (e.Count == 0 && b.Radius < d.MatCell && ci >= 0 && cj >= 0 && ci < d.MatsW && cj < d.MatsH && d.Material(ci, cj) != m) e.Add(ci, cj, d.Material(ci, cj), m);
        return e.Count > 0 ? e : null;
    }

    /// <summary>paints the walk cells under the brush (its outline, a hard edge) with o</summary>
    public static WalkEdit? Walk(IEditorMapDoc d, HeightBrush b, double mx, double my, WalkOverride o)
    {
        if (d.WalkW <= 0) return null;
        var r = BrushShape.Cells(mx, my, b.Radius, d.WalkOriginX, d.WalkOriginY, d.WalkCell, d.WalkW, d.WalkH, centred: true);
        var e = new WalkEdit(d.WalkW);
        for (int j = r.J0; j <= r.J1; j++)
            for (int i = r.I0; i <= r.I1; i++)
            {
                if (b.Shape.Weight(d.WalkOriginX + (i + 0.5) * d.WalkCell - mx, d.WalkOriginY + (j + 0.5) * d.WalkCell - my, b.Radius) <= 0) continue;
                var old = d.GetWalkOverride(i, j);
                if (old != o) e.Add(i, j, (int)old, (int)o);
            }
        return e.Count > 0 ? e : null;
    }

    /// <summary>paints biome `id` with the brush (BiomeAuthoring.Paint: a soft round blob with a noisy edge, strength
    /// 0..1 per step); a map without a layer gets one (its setting everywhere), a biome the layer lacks is added. null:
    /// nothing changed.</summary>
    public static BiomeEdit? Biome(IEditorMapDoc d, string id, double mx, double my, double radius, double strength)
    {
        var before = d.Biomes;
        var l = MapDataDoc.CloneLayer(before) ?? BiomeLayer.Create(new[] { d.Setting }, d.OriginX, d.OriginY, d.HeightsW * d.Cell, d.HeightsH * d.Cell);
        int k = l.AddBiome(id);
        var r = BiomeAuthoring.Paint(l, k, mx, my, radius, Math.Clamp(strength, 0, 1));
        if (r is not { } rect) return null;
        if (before != null && MapDataDoc.SameShape(before, l) && before.Weights.AsSpan().SequenceEqual(l.Weights)) return null;
        var g = new GridRect((int)Math.Floor((rect.X0 - d.OriginX) / d.Cell), (int)Math.Floor((rect.Y0 - d.OriginY) / d.Cell),
                             (int)Math.Ceiling((rect.X1 - d.OriginX) / d.Cell), (int)Math.Ceiling((rect.Y1 - d.OriginY) / d.Cell));
        return new BiomeEdit(MapDataDoc.CloneLayer(before), l, g);
    }

    /// <summary>the water level set (null: no water); null when it is so already</summary>
    public static WaterEdit? Water(IEditorMapDoc d, double? level) => d.Water == level ? null : new WaterEdit(d.Water, level);
}
