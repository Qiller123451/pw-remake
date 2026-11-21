// The map editor's forest and plant tools (docs/MAP_EDITOR.md "Paint: forest and plants"):
//
//   Forest   paints / erases the map's forest blocks: 32 m squares whose 15 trees and 16 undergrowth plants stand where
//            the engine's layout for the square puts them (Forest.cs, forest.json); the tree models are the setting's
//            five forest kinds (forest.json "settings"). Painting sets a square's items standing (state 2, as the
//            original maps: 0x40 | amount, the amount 9 .. 18 as theirs) - each tree with the chance `density` if its
//            kind is chosen, the undergrowth with `density` if wanted; erasing sets them all to nothing (the block
//            stays, empty: an undo then gives the map's own list back exactly). A square is reached when its centre is
//            inside the brush, or the cursor is in it.
//   Plants   scatters landscape decoration (MapPlant: the setting's bushes, ferns, flowers, reeds, small stones ...)
//            under the brush: each step tries area x density / 100 m2 x dt points (at least one), takes those not
//            closer than `spacing` to another plant, not under water (unless allowed), the soft edge by the brush's
//            weight; each a random kind of the chosen ones, turned at random, standing on the ground. Erasing removes
//            the plants under the brush (of the chosen kinds only, if asked).
//
// Their commands keep the exact order of the map's lists for undo. Plain maths, no Godot.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

/// <summary>forest squares changed: each one's items before and after (null: no block), in the order first touched</summary>
public sealed class ForestEdit : IEditCommand
{
    readonly Dictionary<(int, int), (byte[]? Old, byte[]? New)> _s = new();
    readonly List<(int X, int Y)> _order = new();
    public GridRect Rect { get; private set; } = GridRect.Empty;
    public string Name => "Paint forest";
    public int Count => _s.Count;

    public void Add(int x, int y, byte[]? oldB, byte[]? newB)
    {
        if (_s.TryGetValue((x, y), out var v)) _s[(x, y)] = (v.Old, newB);
        else { _s[(x, y)] = (oldB, newB); _order.Add((x, y)); }
        Rect = Rect.Union(new GridRect(x, y, x, y));
    }

    public (byte[]? Old, byte[]? New)? Of(int x, int y) => _s.TryGetValue((x, y), out var v) ? v : null;

    public void Do(IEditorMapDoc d) { foreach (var k in _order) d.SetForestSquare(k.X, k.Y, _s[k].New); d.ForestChanged(Rect); }
    // (in reverse: a block the edit added is the last of its kind in the list - removing it leaves the rest in order)
    public void Undo(IEditorMapDoc d) { for (int n = _order.Count - 1; n >= 0; n--) { var k = _order[n]; d.SetForestSquare(k.X, k.Y, _s[k].Old); } d.ForestChanged(Rect); }

    public bool TryMerge(IEditCommand next)
    {
        if (next is not ForestEdit f) return false;
        foreach (var k in f._order) Add(k.X, k.Y, f._s[k].Old, f._s[k].New);
        return true;
    }
}

/// <summary>plants added and removed, in order (each: added?, the list index, the plant)</summary>
public sealed class PlantEdit : IEditCommand
{
    readonly List<(bool Add, int Index, MapPlant P)> _ops = new();
    public string Name { get; init; } = "Scatter plants";
    public int Count => _ops.Count;
    public IEnumerable<MapPlant> Added => _ops.Where(o => o.Add).Select(o => o.P);
    public IEnumerable<MapPlant> Removed => _ops.Where(o => !o.Add).Select(o => o.P);
    double _x0 = double.MaxValue, _y0 = double.MaxValue, _x1 = double.MinValue, _y1 = double.MinValue;

    public void Add(bool add, int index, MapPlant p)
    {
        _ops.Add((add, index, p));
        _x0 = Math.Min(_x0, p.X); _y0 = Math.Min(_y0, p.Y); _x1 = Math.Max(_x1, p.X); _y1 = Math.Max(_y1, p.Y);
    }

    public void Do(IEditorMapDoc d)
    {
        foreach (var o in _ops) { if (o.Add) d.InsertPlant(o.Index, o.P); else d.RemovePlantAt(o.Index); }
        d.PlantsChanged(_x0 - 4, _y0 - 4, _x1 + 4, _y1 + 4);
    }

    public void Undo(IEditorMapDoc d)
    {
        for (int n = _ops.Count - 1; n >= 0; n--) { var o = _ops[n]; if (o.Add) d.RemovePlantAt(o.Index); else d.InsertPlant(o.Index, o.P); }
        d.PlantsChanged(_x0 - 4, _y0 - 4, _x1 + 4, _y1 + 4);
    }

    public bool TryMerge(IEditCommand next)
    {
        if (next is not PlantEdit p || p.Name != Name) return false;
        foreach (var o in p._ops) Add(o.Add, o.Index, o.P);
        return true;
    }
}

/// <summary>the forest brush's settings</summary>
public sealed class ForestPaint
{
    /// <summary>the share of a square's items that stand (0..1)</summary>
    public double Density = 1;
    /// <summary>the forest kinds (0..4: forest.json's five trees of the setting) painted; others' places stay empty</summary>
    public bool[] Kinds = { true, true, true, true, true };
    /// <summary>the 16 undergrowth plants too</summary>
    public bool Undergrowth = true;
    public bool Erase;
}

/// <summary>the plant brush's settings</summary>
public sealed class PlantScatter
{
    /// <summary>the plant names (their class / model names, as the map's IOMG) scattered; erase: only these if Only</summary>
    public List<string> Kinds = new();
    /// <summary>plants per 100 m2 tried per second at the centre</summary>
    public double Density = 4;
    /// <summary>no two plants closer than this (m)</summary>
    public double Spacing = 2.5;
    public bool InWater;
    public bool Erase;
    /// <summary>erase only the chosen kinds</summary>
    public bool Only;
}

public static class VegetationTools
{
    /// <summary>the forest kind (0..4) of each of square (x, y)'s 15 trees in the engine's layout (null: no forest table)</summary>
    public static int[]? TreeKinds(ForestData? fd, int x, int y)
    {
        if (fd == null) return null;
        var b = new ForestBlock { X = x, Y = y };
        for (int i = 0; i < Forest.TREES; i++) b.B[i] = 0x41;
        var items = fd.ForestItems(new MapData { Forest = new ForestMap { W = x + 1, H = y + 1, Blocks = { b } } }, null);
        return items.Trees.Count == Forest.TREES ? items.Trees.Select(t => t.Kind).ToArray() : null;
    }

    /// <summary>a standing item's byte (state 2, amount 9..18 by the item's hash)</summary>
    static byte Standing(int x, int y, int i) => (byte)(0x40 | (9 + (int)(BrushShape.Hash(x, y, 1000 + i) * 10)));

    /// <summary>the items a painted square gets</summary>
    public static byte[] SquareItems(ForestData? fd, int x, int y, ForestPaint p)
    {
        var b = new byte[Forest.ITEMS];
        if (p.Erase) return b;
        var kinds = TreeKinds(fd, x, y);
        for (int i = 0; i < Forest.ITEMS; i++)
        {
            bool tree = i < Forest.TREES;
            if (tree && kinds != null && !(kinds[i] < p.Kinds.Length && p.Kinds[kinds[i]])) continue;
            if (!tree && !p.Undergrowth) continue;
            if (BrushShape.Hash(x, y, 2000 + i) >= p.Density) continue;
            b[i] = Standing(x, y, i);
        }
        return b;
    }

    /// <summary>paints (or erases) the forest squares under the brush at map (mx, my)</summary>
    public static ForestEdit? PaintForest(IEditorMapDoc d, ForestData? fd, HeightBrush b, double mx, double my, ForestPaint p)
    {
        var all = d.ForestSquares;
        double C = Forest.CELL;
        int cx = (int)Math.Floor(mx / C), cy = (int)Math.Floor(my / C);
        var e = new ForestEdit();
        for (int y = Math.Max(all.J0, (int)Math.Floor((my - b.Radius) / C)); y <= Math.Min(all.J1, (int)Math.Floor((my + b.Radius) / C)); y++)
            for (int x = Math.Max(all.I0, (int)Math.Floor((mx - b.Radius) / C)); x <= Math.Min(all.I1, (int)Math.Floor((mx + b.Radius) / C)); x++)
            {
                bool hit = (x == cx && y == cy) || b.Shape.Weight((x + 0.5) * C - mx, (y + 0.5) * C - my, b.Radius) > 0;
                if (!hit) continue;
                var old = d.ForestSquare(x, y);
                if (p.Erase && old == null) continue;
                var nb = SquareItems(fd, x, y, p);
                if (old != null && old.AsSpan().SequenceEqual(nb)) continue;
                if (old == null && nb.All(v => v == 0)) continue;
                e.Add(x, y, old, nb);
            }
        return e.Count > 0 ? e : null;
    }

    /// <summary>the plant's quaternion for a turn about the map's up axis (the inverse of Ula's Rot = -2 atan2(qz, qw))</summary>
    public static double[] QuatOf(double rot) => new[] { 0, 0, -Math.Sin(rot / 2), Math.Cos(rot / 2) };

    /// <summary>one step of scattering (or erasing: p.Erase) plants with the brush at map (mx, my) over dt seconds</summary>
    public static PlantEdit? ScatterPlants(IEditorMapDoc d, HeightBrush b, double mx, double my, double dt, PlantScatter p, SimRandom rng)
    {
        double R = b.Radius;
        var plants = d.Plants;
        if (p.Erase)
        {
            var e = new PlantEdit { Name = "Erase plants" };
            // (from the end: the indices of those still to remove stay valid)
            for (int k = plants.Count - 1; k >= 0; k--)
            {
                var q = plants[k];
                if (b.Shape.Weight(q.X - mx, q.Y - my, R) <= 0 && !(R < 1 && Math.Abs(q.X - mx) < 1 && Math.Abs(q.Y - my) < 1)) continue;
                if (p.Only && !p.Kinds.Contains(q.Name, StringComparer.OrdinalIgnoreCase)) continue;
                e.Add(false, k, q);
            }
            return e.Count > 0 ? e : null;
        }
        if (p.Kinds.Count == 0 || dt <= 0) return null;
        double area = b.Shape.Form == BrushForm.Square ? 4 * R * R : Math.PI * R * R;
        int n = Math.Max(1, (int)Math.Round(area * p.Density / 100 * dt));
        // the plants near the brush (spacing)
        var near = new List<(double X, double Y)>();
        double reach = R + p.Spacing;
        foreach (var q in plants) if (Math.Abs(q.X - mx) <= reach && Math.Abs(q.Y - my) <= reach) near.Add((q.X, q.Y));
        double sp2 = p.Spacing * p.Spacing;
        double x0 = d.OriginX, y0 = d.OriginY, x1 = d.OriginX + (d.HeightsW - 1) * d.Cell, y1 = d.OriginY + (d.HeightsH - 1) * d.Cell;
        var add = new PlantEdit { Name = "Scatter plants" };
        int at = plants.Count;
        for (int k = 0; k < n; k++)
        {
            double x = mx + rng.Range(-R, R), y = my + rng.Range(-R, R);
            double w = b.Shape.Weight(x - mx, y - my, R), roll = rng.Next();
            string name = rng.Pick(p.Kinds);
            double rot = rng.Range(-Math.PI, Math.PI);
            if (w <= 0 || roll >= w || x < x0 || y < y0 || x > x1 || y > y1) continue;
            double z = d.HeightAt(x, y);
            if (!p.InWater && d.Water is double wl && z <= wl) continue;
            bool free = true;
            foreach (var q in near) if ((q.X - x) * (q.X - x) + (q.Y - y) * (q.Y - y) < sp2) { free = false; break; }
            if (!free) continue;
            near.Add((x, y));
            add.Add(true, at++, new MapPlant { Name = name, X = x, Y = y, Z = z, Rot = rot, Q = QuatOf(rot) });
        }
        return add.Count > 0 ? add : null;
    }
}
