// A sampled height grid (fast lookups) built from an analytic function once: the world's ground. The map source's
// height function (maps/source.js: bilinear on the original map's 2 m grid, or the random map's formula) is sampled
// at every meshCell metres over the square world of `size` metres centred on (0, 0); the world asks this grid, so
// units stand on the same triangles the terrain mesh draws. A building's ground brush changes the samples (ground.js
// LevelGround). Samples are float32, as the JS Float32Array.
// Ported from remake/src/engine/terrain.js HeightField (pw-exporter 98c7703); the mesh building stays in the view.
using System;

namespace PwRemake.Sim.Maps;

public sealed class HeightField
{
    public readonly double Size, Cell;
    /// <summary>samples per side (Size / Cell + 1)</summary>
    public readonly int N;
    /// <summary>heights, row-major: H[j * N + i] at world x = i * Cell - Size / 2, z = j * Cell - Size / 2</summary>
    public readonly float[] H;

    public HeightField(double size, double cell, Func<double, double, double> fn)
    {
        Size = size; Cell = cell;
        N = (int)Math.Floor(size / cell + 0.5) + 1;                  // Math.round
        H = new float[N * N];
        double half = size / 2;
        for (int j = 0; j < N; j++) for (int i = 0; i < N; i++) H[j * N + i] = (float)fn(i * cell - half, j * cell - half);
    }

    /// <summary>height at world (x, z)</summary>
    public double At(double x, double z)
    {
        double half = Size / 2;
        int n = N;
        double fx = (x + half) / Cell, fz = (z + half) / Cell;
        fx = Math.Max(0, Math.Min(n - 1.001, fx)); fz = Math.Max(0, Math.Min(n - 1.001, fz));
        int i = (int)Math.Floor(fx), j = (int)Math.Floor(fz);
        double u = fx - i, v = fz - j;
        double a = H[j * n + i], b = H[j * n + i + 1], c = H[(j + 1) * n + i], d = H[(j + 1) * n + i + 1];
        // match the triangle split of PlaneGeometry (diagonal from (i,j+1) to (i+1,j))
        if (u + v <= 1) return a + (b - a) * u + (c - a) * v;
        return d + (c - d) * (1 - u) + (b - d) * (1 - v);
    }

    /// <summary>y component of the ground normal (1 = flat, 0 = vertical), from the slope over one cell</summary>
    public double NormalY(double x, double z)
    {
        double e = Cell;
        double dx = (At(x + e, z) - At(x - e, z)) / (2 * e), dz = (At(x, z + e) - At(x, z - e)) / (2 * e);
        return 1 / Math.Sqrt(1 + dx * dx + dz * dz);
    }
}
