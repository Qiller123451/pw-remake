// Painting a map's biome layer (docs/BIOMES.md "Authoring"): the tools/biomes command line and the map viewer's brush
// use these. Everything in map coordinates (x east, y south, metres from the map's north-west corner - the coordinates
// of the editor, the co-op definitions and the layer file).
using System;

namespace PwRemake.Sim.Maps;

public static class BiomeAuthoring
{
    /// <summary>a noise of about +-1 with large and small wiggles (the borders of painted biomes)</summary>
    public static double Wiggle(double x, double y, double seed, double scale = 1) =>
        ((Noise.Fbm(x * 0.006 / scale, y * 0.006 / scale, seed, 4) - 0.5) * 2 + 0.35 * (Noise.Fbm(x * 0.03 / scale, y * 0.03 / scale, seed + 101, 3) - 0.5) * 2) / 1.35 * 1.6;

    /// <summary>
    /// biome `from` at map point a, biome `to` at b, a soft transition between them (smoothstep along a -> b), the
    /// transition line pushed back and forth by up to `noise` metres. Every cell is set to the two biomes' mix (others
    /// there are replaced).
    /// </summary>
    public static void Gradient(BiomeLayer l, int from, int to, (double X, double Y) a, (double X, double Y) b, double noise = 60, double seed = 7)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy, len = Math.Sqrt(len2);
        if (len2 <= 0) throw new ArgumentException("the gradient's two points are the same");
        var w = new double[l.N];
        for (int j = 0; j < l.H; j++)
            for (int i = 0; i < l.W; i++)
            {
                double px = l.X0 + (i + 0.5) * l.Cell, py = l.Y0 + (j + 0.5) * l.Cell;
                double t = ((px - a.X) * dx + (py - a.Y) * dy) / len2 + noise * Wiggle(px, py, seed) / len;
                double k = Noise.Smoothstep(0, 1, t);
                Array.Clear(w);
                w[from] += 1 - k; w[to] += k;
                l.SetCell(i, j, w);
            }
    }

    /// <summary>the two points of a gradient that splits the map in half across `side` (east: `from` in the west, `to`
    /// in the east ...), the transition `band` metres wide around the middle</summary>
    public static ((double, double) A, (double, double) B) Split(BiomeLayer l, string side, double band)
    {
        double cx = l.X0 + l.W * l.Cell / 2, cy = l.Y0 + l.H * l.Cell / 2, h = band / 2;
        return side.ToLowerInvariant() switch
        {
            "east" => ((cx - h, cy), (cx + h, cy)),
            "west" => ((cx + h, cy), (cx - h, cy)),
            "south" => ((cx, cy - h), (cx, cy + h)),
            "north" => ((cx, cy + h), (cx, cy - h)),
            _ => throw new ArgumentException($"side {side}: east, west, north or south"),
        };
    }

    /// <summary>
    /// a soft round brush: the cells within `radius` (its edge pushed in and out by `noise` x radius) move towards
    /// biome b by `strength` (0..1) x the falloff (1 inside, 0 at the edge over the outer `soft` share of the radius).
    /// Returns the touched map rectangle (x0, y0, x1, y1), null when nothing changed.
    /// </summary>
    public static (double X0, double Y0, double X1, double Y1)? Paint(BiomeLayer l, int b, double cx, double cy, double radius, double strength = 1, double soft = 0.5, double noise = 0.25, double seed = 13)
    {
        if (b < 0 || b >= l.N || radius <= 0) return null;
        double R = radius * (1 + noise);
        int i0 = Math.Max(0, (int)Math.Floor((cx - R - l.X0) / l.Cell)), i1 = Math.Min(l.W - 1, (int)Math.Floor((cx + R - l.X0) / l.Cell));
        int j0 = Math.Max(0, (int)Math.Floor((cy - R - l.Y0) / l.Cell)), j1 = Math.Min(l.H - 1, (int)Math.Floor((cy + R - l.Y0) / l.Cell));
        bool any = false;
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                double px = l.X0 + (i + 0.5) * l.Cell, py = l.Y0 + (j + 0.5) * l.Cell;
                double r = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) / (radius * (1 + noise * Wiggle(px, py, seed, 0.5)));
                double k = strength * (1 - Noise.Smoothstep(1 - soft, 1, r));
                if (k <= 0.002) continue;
                var w = l.CellWeights(i, j);
                for (int q = 0; q < l.N; q++) w[q] = w[q] * (1 - k) + (q == b ? k : 0);
                l.SetCell(i, j, w);
                any = true;
            }
        return any ? (l.X0 + i0 * l.Cell, l.Y0 + j0 * l.Cell, l.X0 + (i1 + 1) * l.Cell, l.Y0 + (j1 + 1) * l.Cell) : null;
    }
}
