// The map editor's height tools besides raise / lower (HeightBrush.cs) - docs/MAP_EDITOR.md "Terrain tools". Each
// returns the HeightEdit one step (one frame of a held stroke, or one click) makes, not done yet; the steps of a stroke
// merge into one undo step (EditHistory, HeightEdit.TryMerge: the same Name). The brush (HeightBrush) gives the radius,
// the strength and the shape (BrushShape: circle / square, falloff curve).
//
//   Smooth    the samples move towards their neighbours' mean (a 3 x 3 kernel 1-2-1)
//   Flatten   towards a height (the one under the cursor where the stroke began, or a typed one)
//   Plateau   a flat top at a height inside the radius, its sides falling at a slope (degrees) until they meet the
//             ground (and cut down into higher ground the same way) - at most PlateauReach metres beyond the radius
//   Roughen   value noise added (Noise.Fbm; its size in metres): the same place always gets the same pattern
//   Ramp      a straight ramp between two points (their heights), its width, a soft border
//   Level     a rectangle set to one height
//
// The blend tools (smooth, flatten, roughen) take the strength as a rate: per second at the centre a share
// 1 - exp(-strength / 4 x dt x weight) of the way (strength 6: 78 % in a second). Raise / lower keeps metres per
// second. Heights stay within HeightBrush.MinHeight .. MaxHeight. Plain maths, no Godot.
using System;
using System.Collections.Generic;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

public static class HeightTools
{
    public const string SmoothName = "Smooth terrain", FlattenName = "Flatten terrain", PlateauName = "Plateau",
        RoughenName = "Roughen terrain", RampName = "Ramp", LevelName = "Level area";
    /// <summary>how far beyond its radius a plateau's sides reach at most (m)</summary>
    public const double PlateauReach = 80;

    static float Clamp(double h) => (float)Math.Clamp(h, HeightBrush.MinHeight, HeightBrush.MaxHeight);

    /// <summary>the share of the way a blend tool goes in dt at weight w</summary>
    public static double Rate(double strength, double dt, double w) => 1 - Math.Exp(-strength / 4 * dt * w);

    /// <summary>every sample in the brush's footprint with weight &gt; 0: newHeight(i, j, weight, old) (null: unchanged)</summary>
    static HeightEdit? Each(IEditorMapDoc d, double mx, double my, double radius, BrushShape shape, string name, Func<int, int, double, float, double?> f)
    {
        var r = HeightBrush.Footprint(d, mx, my, radius);
        if (r.IsEmpty) return null;
        var e = new HeightEdit { Name = name };
        for (int j = r.J0; j <= r.J1; j++)
            for (int i = r.I0; i <= r.I1; i++)
            {
                double w = shape.Weight(d.OriginX + i * d.Cell - mx, d.OriginY + j * d.Cell - my, radius);
                if (w <= 0) continue;
                float old = d.Height(i, j);
                if (f(i, j, w, old) is not double nh) continue;
                float h = Clamp(nh);
                if (h != old) e.Add(i, j, d.HeightsW, old, h);
            }
        return e.Count > 0 ? e : null;
    }

    /// <summary>smooth: each sample a share of the way to the 1-2-1 weighted mean of its 3 x 3 neighbourhood (read
    /// before the step: the step's own changes do not feed back)</summary>
    public static HeightEdit? Smooth(IEditorMapDoc d, HeightBrush b, double mx, double my, double dt)
    {
        if (dt <= 0) return null;
        int W = d.HeightsW, H = d.HeightsH;
        return Each(d, mx, my, b.Radius, b.Shape, SmoothName, (i, j, w, old) =>
        {
            double s = 0, n = 0;
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int a = i + di, c = j + dj;
                    if (a < 0 || c < 0 || a >= W || c >= H) continue;
                    double k = (di == 0 ? 2 : 1) * (dj == 0 ? 2 : 1);
                    s += d.Height(a, c) * k; n += k;
                }
            return old + (s / n - old) * Rate(b.Strength, dt, w);
        });
    }

    /// <summary>flatten: a share of the way to `target` (m)</summary>
    public static HeightEdit? Flatten(IEditorMapDoc d, HeightBrush b, double mx, double my, double dt, double target)
    {
        if (dt <= 0) return null;
        return Each(d, mx, my, b.Radius, b.Shape, FlattenName, (i, j, w, old) => old + (target - old) * Rate(b.Strength, dt, w));
    }

    /// <summary>a plateau at map (mx, my): `top` (m) within the radius (the brush's outline: circle or square), outside
    /// it the ground is kept between top -+ (distance beyond the radius) x tan(slope) - a slope up from lower ground and
    /// down from higher - up to PlateauReach beyond the radius. Done again at the same place it changes nothing.</summary>
    public static HeightEdit? Plateau(IEditorMapDoc d, HeightBrush b, double mx, double my, double top, double slopeDeg)
    {
        double tan = Math.Tan(Math.Clamp(slopeDeg, 5, 85) * Math.PI / 180);
        double reach = Math.Min(PlateauReach, (HeightBrush.MaxHeight - HeightBrush.MinHeight) / tan);
        var shape = new BrushShape(b.Shape.Form, BrushCurve.Flat);
        double R = b.Radius;
        return Each(d, mx, my, R + reach, shape, PlateauName, (i, j, w, old) =>
        {
            double dist = shape.Distance(d.OriginX + i * d.Cell - mx, d.OriginY + j * d.Cell - my);
            double s = Math.Max(0, dist - R) * tan;
            return Math.Clamp(old, top - s, top + s);
        });
    }

    /// <summary>roughen: value noise of features about `size` metres (two octaves) added, strength x dt metres at most
    /// at the centre</summary>
    public static HeightEdit? Roughen(IEditorMapDoc d, HeightBrush b, double mx, double my, double dt, double size = 12, double seed = 5)
    {
        if (dt <= 0) return null;
        double f = 1 / Math.Max(1, size);
        return Each(d, mx, my, b.Radius, b.Shape, RoughenName, (i, j, w, old) =>
        {
            double x = d.OriginX + i * d.Cell, y = d.OriginY + j * d.Cell;
            double n = (Noise.Fbm(x * f, y * f, seed, 2) - 0.5) * 2;
            return old + n * b.Strength * dt * w;
        });
    }

    /// <summary>a ramp from map (ax, ay) at height ha to (bx, by) at hb: the samples within width / 2 of the line between
    /// them take its height (linear along it), a border of `border` metres beyond blends to the ground (smoothstep);
    /// nothing beyond the two ends</summary>
    public static HeightEdit? Ramp(IEditorMapDoc d, double ax, double ay, double ha, double bx, double by, double hb, double width, double border)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        if (len2 < 1e-6 || width <= 0) return null;
        double half = width / 2, outer = half + Math.Max(0, border);
        int i0 = (int)Math.Floor((Math.Min(ax, bx) - outer - d.OriginX) / d.Cell), i1 = (int)Math.Ceiling((Math.Max(ax, bx) + outer - d.OriginX) / d.Cell);
        int j0 = (int)Math.Floor((Math.Min(ay, by) - outer - d.OriginY) / d.Cell), j1 = (int)Math.Ceiling((Math.Max(ay, by) + outer - d.OriginY) / d.Cell);
        var e = new HeightEdit { Name = RampName };
        for (int j = Math.Max(0, j0); j <= Math.Min(d.HeightsH - 1, j1); j++)
            for (int i = Math.Max(0, i0); i <= Math.Min(d.HeightsW - 1, i1); i++)
            {
                double px = d.OriginX + i * d.Cell - ax, py = d.OriginY + j * d.Cell - ay;
                double t = (px * dx + py * dy) / len2;
                if (t < 0 || t > 1) continue;
                double qx = px - t * dx, qy = py - t * dy, dist = Math.Sqrt(qx * qx + qy * qy);
                if (dist >= outer) continue;
                double w = dist <= half ? 1 : 1 - Noise.Smoothstep(half, outer, dist);
                float old = d.Height(i, j);
                float h = Clamp(old + (ha + (hb - ha) * t - old) * w);
                if (h != old) e.Add(i, j, d.HeightsW, old, h);
            }
        return e.Count > 0 ? e : null;
    }

    /// <summary>every sample in the map rectangle (x0, y0) .. (x1, y1) (either corner first) set to h</summary>
    public static HeightEdit? Level(IEditorMapDoc d, double x0, double y0, double x1, double y1, double h)
    {
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (y1 < y0) (y0, y1) = (y1, y0);
        int i0 = Math.Max(0, (int)Math.Ceiling((x0 - d.OriginX) / d.Cell)), i1 = Math.Min(d.HeightsW - 1, (int)Math.Floor((x1 - d.OriginX) / d.Cell));
        int j0 = Math.Max(0, (int)Math.Ceiling((y0 - d.OriginY) / d.Cell)), j1 = Math.Min(d.HeightsH - 1, (int)Math.Floor((y1 - d.OriginY) / d.Cell));
        var e = new HeightEdit { Name = LevelName };
        float v = Clamp(h);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                float old = d.Height(i, j);
                if (old != v) e.Add(i, j, d.HeightsW, old, v);
            }
        return e.Count > 0 ? e : null;
    }
}
