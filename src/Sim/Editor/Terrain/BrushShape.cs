// The map editor's brush shape (docs/MAP_EDITOR.md "Terrain tools"): every brush tool - the height tools, painting the
// ground materials, biomes, walkability, forests and plants - weighs the points it touches by the same shape: a circle
// or a square of the brush's radius (the square: the radius is half its side), and a falloff curve from the centre
// (1) to the edge (0). Plain maths, no Godot (tests/PwRemake.Tests/EditorTerrainTests.cs).
using System;

namespace PwRemake.Sim.Editor;

/// <summary>the outline of a brush</summary>
public enum BrushForm { Circle, Square }

/// <summary>how a brush's weight falls from its centre to its edge</summary>
public enum BrushCurve
{
    /// <summary>(1 - t^2)^2: soft, the phase 1 raise / lower brush's</summary>
    Smooth,
    /// <summary>1 - t</summary>
    Linear,
    /// <summary>sqrt(1 - t^2): a dome, full most of the way</summary>
    Sphere,
    /// <summary>1 everywhere inside: a hard edge</summary>
    Flat,
}

public readonly record struct BrushShape(BrushForm Form, BrushCurve Curve)
{
    /// <summary>a soft circle (the phase 1 brush)</summary>
    public static readonly BrushShape Default = new(BrushForm.Circle, BrushCurve.Smooth);

    /// <summary>the curve's weight at t = distance / radius (1 at 0, 0 at and beyond 1)</summary>
    public static double CurveAt(BrushCurve c, double t)
    {
        if (t >= 1 || double.IsNaN(t)) return 0;
        if (t < 0) t = 0;
        switch (c)
        {
            case BrushCurve.Linear: return 1 - t;
            case BrushCurve.Sphere: return Math.Sqrt(1 - t * t);
            case BrushCurve.Flat: return 1;
            default: { double k = 1 - t * t; return k * k; }
        }
    }

    /// <summary>the brush's "distance" of an offset (dx, dy) from its centre: Euclidean for the circle, the larger axis
    /// for the square</summary>
    public double Distance(double dx, double dy) => Form == BrushForm.Square ? Math.Max(Math.Abs(dx), Math.Abs(dy)) : Math.Sqrt(dx * dx + dy * dy);

    /// <summary>the weight of the point at offset (dx, dy) from the centre of a brush of this radius (0 outside)</summary>
    public double Weight(double dx, double dy, double radius) => radius <= 0 ? 0 : CurveAt(Curve, Distance(dx, dy) / radius);

    /// <summary>the cells of a grid (cell size `cell`, cell (0, 0)'s point at (ox, oy), w x h of them) a brush at
    /// (mx, my) with this radius can touch, clamped to the grid. centred: the cells' points are their centres
    /// (ox + (i + 0.5) cell); else their corners (ox + i cell: the height samples).</summary>
    public static GridRect Cells(double mx, double my, double radius, double ox, double oy, double cell, int w, int h, bool centred)
    {
        double c = centred ? 0.5 : 0;
        int i0 = (int)Math.Ceiling((mx - radius - ox) / cell - c), i1 = (int)Math.Floor((mx + radius - ox) / cell - c);
        int j0 = (int)Math.Ceiling((my - radius - oy) / cell - c), j1 = (int)Math.Floor((my + radius - oy) / cell - c);
        return new GridRect(Math.Max(0, i0), Math.Max(0, j0), Math.Min(w - 1, i1), Math.Min(h - 1, j1));
    }

    /// <summary>a hash of a cell in 0 .. 1 (deterministic: the dithered edge of a discrete paint brush, the forest's
    /// item amounts) - the same cell always gives the same number</summary>
    public static double Hash(int i, int j, int salt = 0)
    {
        unchecked
        {
            uint h = (uint)i * 0x8da6b343u ^ (uint)j * 0xd8163841u ^ (uint)salt * 0xcb1ab31fu;
            h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
            return (h & 0xffffff) / 16777216.0;
        }
    }
}
