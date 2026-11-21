// The map editor's first terrain tool: the raise / lower brush. Held down, it lifts (or lowers) the height samples
// within its radius a little every frame: by strength (m per second at the centre) x the frame's time x the falloff
// (1 at the centre, 0 at the radius, smooth: (1 - t^2)^2 with t = distance / radius). Each frame's change is a
// HeightEdit command; the steps of one stroke merge into one undo step (EditHistory). Plain maths, no Godot - the
// tests check it (tests/PwRemake.Tests/EditorTests.cs).
using System;
using System.Collections.Generic;

namespace PwRemake.Sim.Editor;

public sealed class HeightBrush
{
    /// <summary>the lowest and highest ground the brush makes (m)</summary>
    public const float MinHeight = 0, MaxHeight = 500;
    /// <summary>radius (m)</summary>
    public double Radius = 16;
    /// <summary>metres per second at the centre</summary>
    public double Strength = 6;
    /// <summary>lower instead of raise</summary>
    public bool Lower;
    /// <summary>the brush's outline and falloff (phase 2: Terrain/BrushShape.cs; the default is the soft circle above)</summary>
    public BrushShape Shape = BrushShape.Default;

    /// <summary>the brush's weight at distance d from its centre: 1 at the centre, 0 at and beyond the radius</summary>
    public static double Falloff(double d, double radius)
    {
        if (radius <= 0 || d >= radius) return 0;
        double t = d / radius, k = 1 - t * t;
        return k * k;
    }

    /// <summary>the samples a brush at map (mx, my) with this radius can touch (clamped to the grid)</summary>
    public static GridRect Footprint(IEditorMapDoc doc, double mx, double my, double radius)
    {
        int i0 = (int)Math.Ceiling((mx - radius - doc.OriginX) / doc.Cell), i1 = (int)Math.Floor((mx + radius - doc.OriginX) / doc.Cell);
        int j0 = (int)Math.Ceiling((my - radius - doc.OriginY) / doc.Cell), j1 = (int)Math.Floor((my + radius - doc.OriginY) / doc.Cell);
        return new GridRect(Math.Max(0, i0), Math.Max(0, j0), Math.Min(doc.HeightsW - 1, i1), Math.Min(doc.HeightsH - 1, j1));
    }

    /// <summary>one step of the brush at map (mx, my) over dt seconds: the edit it makes (not done yet), or null when it
    /// changes nothing (outside the map, at the height limit)</summary>
    public HeightEdit? Step(IEditorMapDoc doc, double mx, double my, double dt)
    {
        var r = Footprint(doc, mx, my, Radius);
        if (r.IsEmpty || dt <= 0) return null;
        double amount = Strength * dt * (Lower ? -1 : 1);
        var e = new HeightEdit();
        for (int j = r.J0; j <= r.J1; j++)
            for (int i = r.I0; i <= r.I1; i++)
            {
                double x = doc.OriginX + i * doc.Cell - mx, y = doc.OriginY + j * doc.Cell - my;
                double w = Shape.Weight(x, y, Radius);
                if (w <= 0) continue;
                float old = doc.Height(i, j);
                float nh = (float)Math.Clamp(old + amount * w, MinHeight, MaxHeight);
                if (nh != old) e.Add(i, j, doc.HeightsW, old, nh);
            }
        return e.Count > 0 ? e : null;
    }
}

/// <summary>a change of height samples: each one's old and new height; one stroke's steps merge (the first old, the
/// last new height of every sample)</summary>
public sealed class HeightEdit : IEditCommand
{
    // sample index (j * W + i) -> (old, new); the order of first touch kept for Do / Undo
    readonly Dictionary<int, (float Old, float New)> _s = new();
    readonly List<int> _order = new();
    int _w;
    public GridRect Rect { get; private set; } = GridRect.Empty;
    public string Name { get; init; } = "Raise / lower terrain";
    public int Count => _s.Count;

    public void Add(int i, int j, int w, float oldH, float newH)
    {
        _w = w;
        int k = j * w + i;
        if (_s.TryGetValue(k, out var v)) _s[k] = (v.Old, newH);
        else { _s[k] = (oldH, newH); _order.Add(k); }
        Rect = Rect.Union(new GridRect(i, j, i, j));
    }

    /// <summary>the heights this edit gives (old, new) of sample (i, j); null when it does not touch it</summary>
    public (float Old, float New)? Of(int i, int j) => _s.TryGetValue(j * _w + i, out var v) ? v : null;

    public void Do(IEditorMapDoc doc)
    {
        foreach (var k in _order) doc.SetHeight(k % _w, k / _w, _s[k].New);
        doc.HeightsChanged(Rect);
    }

    public void Undo(IEditorMapDoc doc)
    {
        foreach (var k in _order) doc.SetHeight(k % _w, k / _w, _s[k].Old);
        doc.HeightsChanged(Rect);
    }

    public bool TryMerge(IEditCommand next)
    {
        if (next is not HeightEdit h || h.Name != Name || (h._w != _w && _s.Count > 0)) return false;
        foreach (var k in h._order) Add(k % h._w, k / h._w, h._w, h._s[k].Old, h._s[k].New);
        return true;
    }
}
