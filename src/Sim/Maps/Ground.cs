// The ground levelling under a building a player puts down (the original's ScapeMgr.AdaptTerrain(gfx, pos, rot,
// coastal), ServerApp.usl: every building but walls and gates). Each building model has a ground brush,
// Texture/Scape/ObjBrush/<gfx>.tga (pipeline/build_terrain.py -> assets/terrain/objbrush.json): its alpha says how
// strongly the ground under every half metre is pulled to the building's floor height - fully under the house, a
// soft falloff around it - so it stands level instead of sinking into a slope or hovering over a dip. The building
// then stands at the new height (TP410 in ServerApp.usl). The brush's colour gives the ground material of every 4 m
// cell under it (BrushCells): gravel under the building, earth around it, grass at the corners.
// docs/TERRAIN_NOTES.md "Object brushes".
// Ported from remake/src/game/ground.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

/// <summary>a ground brush: W x H pixels, alpha 0 .. 255, ground material 0 .. 7 per pixel or null (row 0 = the
/// model's -y side)</summary>
public sealed class GroundBrush
{
    public int W, H;
    public byte[] A = Array.Empty<byte>();
    public byte[]? M;
}

/// <summary>the rectangle of height samples LevelGround changed (inclusive)</summary>
public readonly record struct SampleRect(int I0, int J0, int I1, int J1);

/// <summary>a 4 m ground material cell and the material the brush gives it</summary>
public readonly record struct BrushCell(int I, int J, int Material);

public sealed class GroundBrushes
{
    public readonly double Mpp;
    readonly JsonObject _raw;
    readonly Dictionary<string, GroundBrush?> _cache = new();

    /// <summary>json = objbrush.json {metres_per_pixel, brushes: {gfx: {w, h, a: base64 alpha, m: base64 ground material}}}</summary>
    public GroundBrushes(JsonNode? json)
    {
        double mpp = json?["metres_per_pixel"].Num(0) ?? 0;
        Mpp = JsNum.Truthy(mpp) ? mpp : 0.5;
        _raw = json?["brushes"] as JsonObject ?? new JsonObject();
    }

    public static GroundBrushes LoadFile(string path) => new(Json.TryParseFile(path));

    /// <summary>the brush of a model the original has none for (World/BuildingGround: one made from the building's
    /// footprint and a brush of its tribe); null = such a model changes no ground (the original's rule)</summary>
    public Func<string, GroundBrush?>? Fallback;
    /// <summary>what Fallback was made for (BuildingGround.Use sets it once per rules)</summary>
    public object? FallbackOwner;

    /// <summary>the brush of a model (lower-cased): the original's, else the fallback's, or null</summary>
    public GroundBrush? Get(string? gfx)
    {
        string k = (gfx ?? "").ToLowerInvariant();
        if (_cache.TryGetValue(k, out var c)) return c;
        var b = Original(k);
        if (b == null && Fallback != null && k.Length > 0) b = Fallback(k);
        _cache[k] = b;
        return b;
    }

    readonly Dictionary<string, GroundBrush?> _orig = new();
    /// <summary>the original's own brush of a model (Texture/Scape/ObjBrush/&lt;gfx&gt;.tga), or null</summary>
    public GroundBrush? Original(string? gfx)
    {
        string k = (gfx ?? "").ToLowerInvariant();
        if (_orig.TryGetValue(k, out var c)) return c;
        GroundBrush? b = null;
        if (_raw[k] is JsonObject r)
            b = new GroundBrush
            {
                W = r["w"].Int(), H = r["h"].Int(), A = Convert.FromBase64String(r["a"].Str()),
                M = r["m"] is JsonNode m && m.Str() is { Length: > 0 } ms ? Convert.FromBase64String(ms) : null,
            };
        _orig[k] = b;
        return b;
    }

    /// <summary>the models the original has a brush for (ordinal order)</summary>
    public List<string> OriginalNames() { var l = new List<string>(); foreach (var (k, _) in _raw) l.Add(k); l.Sort(StringComparer.Ordinal); return l; }

    /// <summary>forget the brushes made so far (a new Fallback)</summary>
    public void ClearMade() => _cache.Clear();

    /// <summary>the world's 4 m material cell of a point (main.js cellOf): [i, j, centre x, centre z] for a world of
    /// `size` metres centred on (0, 0)</summary>
    public static Func<double, double, (int i, int j, double cx, double cz)> MaterialCellOf(double size)
    {
        double half = size / 2;
        return (x, z) =>
        {
            int i = (int)Math.Floor((x + half) / 4), j = (int)Math.Floor((z + half) / 4);
            return (i, j, i * 4 + 2 - half, j * 4 + 2 - half);
        };
    }
}

public static class Ground
{
    // brush alpha (0..1) at a point of the brush in metres from its centre (bilinear)
    static double Sample(GroundBrush b, double mpp, double bx, double bz)
    {
        double fx = bx / mpp + b.W / 2.0 - 0.5, fz = bz / mpp + b.H / 2.0 - 0.5;
        if (fx < -0.5 || fz < -0.5 || fx > b.W - 0.5 || fz > b.H - 0.5) return 0;
        int i = (int)Math.Max(0, Math.Min(b.W - 2, Math.Floor(fx))), j = (int)Math.Max(0, Math.Min(b.H - 2, Math.Floor(fz)));
        double u = Math.Max(0, Math.Min(1, fx - i)), v = Math.Max(0, Math.Min(1, fz - j));
        var A = b.A;
        int w = b.W;
        return ((A[j * w + i] * (1 - u) + A[j * w + i + 1] * u) * (1 - v) + (A[(j + 1) * w + i] * (1 - u) + A[(j + 1) * w + i + 1] * u) * v) / 255;
    }

    /// <summary>level the height field hf under brush b at world (x, z), building heading `heading`. The model's axes
    /// (and the brush's: x, y with y = rows) lie along the world's x and z at heading pi (Frame.cs). Returns the changed
    /// rectangle of height samples or null.</summary>
    public static SampleRect? LevelGround(HeightField hf, GroundBrush? b, double mpp, double x, double z, double heading)
    {
        if (b == null) return null;
        double t = heading - Math.PI, c = SMath.Cos(t), s = SMath.Sin(t);
        double R = Math.Sqrt((double)b.W * b.W + (double)b.H * b.H) * mpp / 2;
        double half = hf.Size / 2, cell = hf.Cell;
        int n = hf.N;
        int i0 = (int)Math.Max(0, Math.Floor((x - R + half) / cell)), i1 = (int)Math.Min(n - 1, Math.Ceiling((x + R + half) / cell));
        int j0 = (int)Math.Max(0, Math.Floor((z - R + half) / cell)), j1 = (int)Math.Min(n - 1, Math.Ceiling((z + R + half) / cell));
        if (i1 < i0 || j1 < j0) return null;
        // the floor height: the ground under the fully levelled part of the brush (its middle), on average
        var W = new List<double>();
        double sum = 0, wsum = 0;
        bool any = false;
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++)
        {
            double dx = i * cell - half - x, dz = j * cell - half - z;
            double a = Sample(b, mpp, dx * c - dz * s, dx * s + dz * c);
            W.Add(a);
            if (a > 0) any = true;
            if (a > 0.9) { sum += hf.H[j * n + i]; wsum++; }
        }
        if (!any) return null;
        double h0 = wsum != 0 ? sum / wsum : hf.At(x, z);
        int k = 0;
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++, k++)
        {
            double a = W[k];
            if (a > 0) hf.H[j * n + i] = (float)(hf.H[j * n + i] + (h0 - hf.H[j * n + i]) * a);
        }
        return new SampleRect(i0, j0, i1, j1);
    }

    /// <summary>the rectangle of height samples LevelGround touches at (x, z) - without changing them (the view, when the
    /// simulation's height field is its own and the simulation has levelled it already)</summary>
    public static SampleRect? BrushRect(HeightField hf, GroundBrush? b, double mpp, double x, double z)
    {
        if (b == null) return null;
        double R = Math.Sqrt((double)b.W * b.W + (double)b.H * b.H) * mpp / 2;
        double half = hf.Size / 2, cell = hf.Cell;
        int n = hf.N;
        int i0 = (int)Math.Max(0, Math.Floor((x - R + half) / cell)), i1 = (int)Math.Min(n - 1, Math.Ceiling((x + R + half) / cell));
        int j0 = (int)Math.Max(0, Math.Floor((z - R + half) / cell)), j1 = (int)Math.Min(n - 1, Math.Ceiling((z + R + half) / cell));
        return i1 < i0 || j1 < j0 ? null : new SampleRect(i0, j0, i1, j1);
    }

    /// <summary>The ground materials the brush gives the 4 m material cells under it (PWServer.exe AdaptTerrain: every
    /// cell whose centre lies on the brush takes the material of the pixel there - R, G, B above 127 = bits 2, 1, 0 of
    /// the material index, pwexport/scape.py brush_materials). cell(x, z) -> (i, j, cx, cz): the cell of a point and its
    /// centre (GroundBrushes.MaterialCellOf).</summary>
    public static List<BrushCell> BrushCells(GroundBrush? b, double mpp, double x, double z, double heading, Func<double, double, (int i, int j, double cx, double cz)> cell)
    {
        var output = new List<BrushCell>();
        if (b == null || b.M == null) return output;
        double t = heading - Math.PI, c = SMath.Cos(t), s = SMath.Sin(t);
        double R = Math.Sqrt((double)b.W * b.W + (double)b.H * b.H) * mpp / 2 + 4;
        var seen = new HashSet<(int, int)>();
        for (double pz = z - R; pz <= z + R; pz += 2) for (double px = x - R; px <= x + R; px += 2)
        {
            var (i, j, cx, cz) = cell(px, pz);
            if (!seen.Add((i, j))) continue;
            double dx = cx - x, dz = cz - z, bx = dx * c - dz * s, bz = dx * s + dz * c;
            double u = Math.Floor(bx / mpp + b.W / 2.0), v = Math.Floor(bz / mpp + b.H / 2.0);
            if (u < 0 || v < 0 || u >= b.W || v >= b.H) continue;
            output.Add(new BrushCell(i, j, b.M[(int)v * b.W + (int)u]));
        }
        return output;
    }
}
