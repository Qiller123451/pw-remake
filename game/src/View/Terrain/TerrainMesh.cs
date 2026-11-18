// The terrain mesh: the world's height field (Sim/Maps HeightField: hf.N x hf.N samples every hf.Cell metres, centred
// on (0, 0)) as triangles, with the ground's texture layer weights of every vertex for the splat shader (TerrainShader):
// an original map's 8 material weights (MapSource.Splat), the random map's 4, a map with biomes every biome's 8
// (GroundLayers, docs/BIOMES.md). A vertex keeps its 8 strongest layers; each tile of TILE x TILE cells has one palette
// of 8 layers its vertices' weights refer to (LayerPick). With 8 layers or fewer the palette is the layers in order
// everywhere (an original map: exactly the JS's 8 weights per vertex). A chunk whose tiles have different palettes
// gets its vertices per tile (the samples on a tile border twice, one per palette). The triangles are split as three.js' PlaneGeometry splits them (the diagonal from (i, j+1)
// to (i+1, j)) - HeightField.At interpolates on the same triangles, so units stand on the drawn ground.
//
// Godot side: the grid is cut into chunks of CHUNK x CHUNK cells (one ArrayMesh each, culled per chunk); a change of
// the heights or materials (a building levels the ground: Ground.LevelGround / BrushCells) rebuilds the chunks it
// touches (the JS uploaded a band of rows of its one mesh). Normals are central differences of the samples
// everywhere (the JS: computeVertexNormals when built, central differences in refreshTerrain) - the same on both sides
// of a chunk border, so no seams.
//
// Ported from remake/src/engine/terrain.js buildTerrain, refreshTerrain, refreshSplat (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.View.Terrain;

public partial class TerrainMesh : Node3D
{
    static readonly PwRemake.Sim.Core.Logger L = Log.For("View.Terrain");
    /// <summary>cells per chunk side (64 cells = 128 m on the 2 m grid)</summary>
    public const int CHUNK = 64;
    /// <summary>cells per palette tile side (16 m on the 2 m grid); CHUNK is a multiple</summary>
    public const int TILE = 8;
    const int SLOTS = LayerPick.SLOTS;

    public HeightField Hf { get; private set; } = null!;
    /// <summary>the texture layer weights at world (x, z), one per layer</summary>
    public Func<double, double, double[]> Weights { get; private set; } = null!;
    public ShaderMaterial Material { get; private set; } = null!;
    /// <summary>the shader's slots per vertex: 4 (the random map) or 8</summary>
    public int N { get; private set; }
    /// <summary>texture layers (the Texture2DArray's)</summary>
    public int Layers { get; private set; }
    /// <summary>layers of one biome (the palettes' padding: 8; the random map's 4)</summary>
    public int PerBiome { get; private set; } = 8;
    int _cn, _tn;                              // chunks, tiles per side
    MeshInstance3D[] _chunks = Array.Empty<MeshInstance3D>();
    float[] _w = Array.Empty<float>();         // per vertex SLOTS weights: by layer (Layers <= SLOTS), else of _l's layers
    short[] _l = Array.Empty<short>();         // per vertex SLOTS layers, strongest first (-1: none); only when Layers > SLOTS
    int[][] _pal = Array.Empty<int[]>();       // per tile its palette (Layers > SLOTS)
    static readonly int[] Identity = Enumerable.Range(0, SLOTS).ToArray();
    public double BuildMs { get; private set; }

    /// <summary>
    /// Builds the mesh (buildTerrain). weights(x, z) returns one weight per layer; layers: the layers' images (equal
    /// size; made into one Texture2DArray); scales: the layers' [sx, sz] (null = the JS defaults); perBiome: the layers of
    /// one biome (8; the random map's set: its count).
    /// </summary>
    public static TerrainMesh Build(HeightField hf, Func<double, double, double[]> weights, IReadOnlyList<Image> layers, IReadOnlyList<double[]>? scales = null, int perBiome = 8)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (layers.Count > TerrainShader.MAX_LAYERS) throw new ArgumentException($"{layers.Count} ground layers: at most {TerrainShader.MAX_LAYERS}");
        var t = new TerrainMesh { Name = "Terrain", Hf = hf, Weights = weights, Layers = layers.Count, PerBiome = Math.Max(1, perBiome) };
        t.N = layers.Count > 4 ? 8 : 4;
        int n = hf.N;
        t._w = new float[n * n * SLOTS];
        if (t.Layers > SLOTS) { t._l = new short[n * n * SLOTS]; Array.Fill(t._l, (short)-1); }
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                t.SetWeights(i, j);
        t._tn = (n - 1 + TILE - 1) / TILE;
        if (t.Layers > SLOTS)
        {
            t._pal = new int[t._tn * t._tn][];
            for (int tj = 0; tj < t._tn; tj++) for (int ti = 0; ti < t._tn; ti++) t.SetPalette(ti, tj);
        }
        var sc = new List<double[]>(scales ?? TerrainShader.DefaultScales);
        while (sc.Count < t.Layers) sc.Add(sc[^1]);
        var mat = new ShaderMaterial { Shader = new Shader { Code = TerrainShader.Code(t.N) } };
        mat.SetShaderParameter("ta", MakeArray(layers));
        var arr = new Vector4[TerrainShader.MAX_LAYERS / 2];
        for (int k = 0; k < t.Layers; k++)
        {
            ref var v = ref arr[k / 2];
            if (k % 2 == 0) { v.X = (float)sc[k][0]; v.Y = (float)sc[k][1]; } else { v.Z = (float)sc[k][0]; v.W = (float)sc[k][1]; }
        }
        mat.SetShaderParameter("sc", arr);
        t.Material = mat;
        t._cn = (n - 1 + CHUNK - 1) / CHUNK;
        t._chunks = new MeshInstance3D[t._cn * t._cn];
        for (int cj = 0; cj < t._cn; cj++)
            for (int ci = 0; ci < t._cn; ci++)
            {
                var mi = new MeshInstance3D { Name = $"chunk_{ci}_{cj}", MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                t._chunks[cj * t._cn + ci] = mi;
                t.AddChild(mi);
                t.BuildChunk(ci, cj);
            }
        t.BuildMs = sw.Elapsed.TotalMilliseconds;
        L.Debug($"terrain {n}x{n} samples, {t._cn * t._cn} chunks, {t.Layers} layers: {t.BuildMs:0} ms");
        return t;
    }

    /// <summary>the layers' images as one texture array (all made the size and format of the first, with mipmaps)</summary>
    static Texture2DArray MakeArray(IReadOnlyList<Image> layers)
    {
        var first = layers[0];
        var imgs = new Godot.Collections.Array<Image>();
        foreach (var src in layers)
        {
            var img = src;
            if (img.GetWidth() != first.GetWidth() || img.GetHeight() != first.GetHeight() || img.GetFormat() != first.GetFormat() || !img.HasMipmaps())
            {
                img = (Image)src.Duplicate();
                if (img.HasMipmaps()) img.ClearMipmaps();
                if (img.GetFormat() != first.GetFormat()) img.Convert(first.GetFormat());
                if (img.GetWidth() != first.GetWidth() || img.GetHeight() != first.GetHeight()) img.Resize(first.GetWidth(), first.GetHeight(), Image.Interpolation.Bilinear);
                img.GenerateMipmaps();
            }
            imgs.Add(img);
        }
        var ta = new Texture2DArray();
        var err = ta.CreateFromImages(imgs);
        if (err != Error.Ok) L.Warn($"ground texture array: {err}");
        return ta;
    }

    void SetWeights(int i, int j)
    {
        double half = Hf.Size / 2;
        var ws = Weights(i * Hf.Cell - half, j * Hf.Cell - half);
        int o = (j * Hf.N + i) * SLOTS;
        if (Layers <= SLOTS)
        {
            for (int k = 0; k < SLOTS; k++) _w[o + k] = k < ws.Length ? (float)ws[k] : 0;
            return;
        }
        Span<int> idx = stackalloc int[SLOTS]; Span<float> wv = stackalloc float[SLOTS];
        int c = LayerPick.Top(ws, SLOTS, idx, wv);
        for (int k = 0; k < SLOTS; k++) { _l[o + k] = (short)(k < c ? idx[k] : -1); _w[o + k] = k < c ? wv[k] : 0; }
    }

    /// <summary>a vertex's weight of layer l (0 when it does not have it)</summary>
    float WeightOf(int s, int l)
    {
        int o = s * SLOTS;
        if (Layers <= SLOTS) return l < SLOTS ? _w[o + l] : 0;
        for (int k = 0; k < SLOTS; k++) if (_l[o + k] == l) return _w[o + k];
        return 0;
    }

    void SetPalette(int ti, int tj)
    {
        int n = Hf.N;
        var sums = new double[Layers];
        for (int j = tj * TILE; j <= Math.Min(n - 1, tj * TILE + TILE); j++)
            for (int i = ti * TILE; i <= Math.Min(n - 1, ti * TILE + TILE); i++)
            {
                int o = (j * n + i) * SLOTS;
                for (int k = 0; k < SLOTS; k++) if (_l[o + k] >= 0) sums[_l[o + k]] += _w[o + k];
            }
        _pal[tj * _tn + ti] = LayerPick.Palette(sums, PerBiome);
    }

    int[] PaletteOf(int ti, int tj) => Layers <= SLOTS ? Identity : _pal[Math.Min(_tn - 1, tj) * _tn + Math.Min(_tn - 1, ti)];

    sealed class Arrays
    {
        public readonly List<Vector3> Pos = new(), Nrm = new();
        public readonly List<float> C0 = new(), C1 = new(), C2 = new();
        public readonly List<int> Idx = new();
    }

    /// <summary>the vertices i0..i1 x j0..j1 with their weights for palette pal, and their triangles</summary>
    void Emit(Arrays a, int i0, int j0, int i1, int j1, int[] pal)
    {
        int n = Hf.N;
        int w = i1 - i0 + 1, h = j1 - j0 + 1, b = a.Pos.Count;
        var H = Hf.H;
        double e = Hf.Cell, half = Hf.Size / 2;
        var packed = LayerPick.Pack(pal);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                int s = j * n + i;
                a.Pos.Add(new Vector3((float)(i * e - half), H[s], (float)(j * e - half)));
                double dx = (H[j * n + Math.Min(n - 1, i + 1)] - H[j * n + Math.Max(0, i - 1)]) / (2 * e);
                double dz = (H[Math.Min(n - 1, j + 1) * n + i] - H[Math.Max(0, j - 1) * n + i]) / (2 * e);
                double l = Math.Sqrt(dx * dx + 1 + dz * dz);
                a.Nrm.Add(new Vector3((float)(-dx / l), (float)(1 / l), (float)(-dz / l)));
                for (int q = 0; q < 4; q++) a.C0.Add(WeightOf(s, pal[q]));
                for (int q = 4; q < 8; q++) a.C1.Add(WeightOf(s, pal[q]));
                for (int q = 0; q < 4; q++) a.C2.Add(packed[q]);
            }
        // two triangles per cell, split along (i, j+1) - (i+1, j) as PlaneGeometry; Godot's front faces are clockwise
        for (int j = 0; j < h - 1; j++)
            for (int i = 0; i < w - 1; i++)
            {
                int p = b + j * w + i, q = b + (j + 1) * w + i, c = b + (j + 1) * w + i + 1, d = b + j * w + i + 1;
                a.Idx.Add(p); a.Idx.Add(d); a.Idx.Add(q);
                a.Idx.Add(q); a.Idx.Add(d); a.Idx.Add(c);
            }
    }

    void BuildChunk(int ci, int cj)
    {
        int n = Hf.N;
        int i0 = ci * CHUNK, j0 = cj * CHUNK, i1 = Math.Min(n - 1, i0 + CHUNK), j1 = Math.Min(n - 1, j0 + CHUNK);
        var a = new Arrays();
        // one palette for the whole chunk (always with 8 layers or fewer): its vertices shared, as the JS's mesh
        int t0 = i0 / TILE, t1 = (i1 - 1) / TILE, r0 = j0 / TILE, r1 = (j1 - 1) / TILE;
        bool same = true;
        var p0 = PaletteOf(t0, r0);
        for (int tj = r0; tj <= r1 && same; tj++) for (int ti = t0; ti <= t1 && same; ti++) same = PaletteOf(ti, tj).AsSpan().SequenceEqual(p0);
        if (same) Emit(a, i0, j0, i1, j1, p0);
        else
            for (int tj = r0; tj <= r1; tj++)
                for (int ti = t0; ti <= t1; ti++)
                    Emit(a, ti * TILE, tj * TILE, Math.Min(i1, ti * TILE + TILE), Math.Min(j1, tj * TILE + TILE), PaletteOf(ti, tj));
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = a.Pos.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = a.Nrm.ToArray();
        arrays[(int)Mesh.ArrayType.Custom0] = a.C0.ToArray();
        arrays[(int)Mesh.ArrayType.Custom1] = a.C1.ToArray();
        arrays[(int)Mesh.ArrayType.Custom2] = a.C2.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = a.Idx.ToArray();
        var fmt = (Mesh.ArrayFormat)(((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
            | ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift)
            | ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom2Shift));
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, fmt);
        _chunks[cj * _cn + ci].Mesh = mesh;
    }

    void RebuildChunks(int i0, int j0, int i1, int j1)
    {
        int c0 = Math.Max(0, (i0 - 1) / CHUNK), c1 = Math.Min(_cn - 1, (i1 + 1) / CHUNK);
        int r0 = Math.Max(0, (j0 - 1) / CHUNK), r1 = Math.Min(_cn - 1, (j1 + 1) / CHUNK);
        // (a sample on a chunk border belongs to both chunks)
        for (int cj = r0; cj <= r1; cj++)
            for (int ci = c0; ci <= c1; ci++)
                BuildChunk(ci, cj);
    }

    /// <summary>
    /// refreshTerrain: the height samples i0..i1, j0..j1 of the height field changed (Ground.LevelGround): heights and
    /// normals of those vertices and a margin of one.
    /// </summary>
    public void RefreshHeights(SampleRect r)
    {
        int n = Hf.N;
        RebuildChunks(Math.Max(0, r.I0 - 1), Math.Max(0, r.J0 - 1), Math.Min(n - 1, r.I1 + 1), Math.Min(n - 1, r.J1 + 1));
    }

    /// <summary>
    /// refreshSplat: the ground's layers again (weights(x, z)) for the vertices around world x0..x1, z0..z1 (a
    /// building's ground brush painted the material grid: Ground.BrushCells; a biome brush painted the biome layer).
    /// </summary>
    public void RefreshSplat(double x0, double z0, double x1, double z1, Func<double, double, double[]>? weights = null)
    {
        if (weights != null) Weights = weights;
        int n = Hf.N;
        double e = Hf.Cell, half = Hf.Size / 2;
        int i0 = Math.Max(0, (int)Math.Floor((x0 + half) / e)), i1 = Math.Min(n - 1, (int)Math.Ceiling((x1 + half) / e));
        int j0 = Math.Max(0, (int)Math.Floor((z0 + half) / e)), j1 = Math.Min(n - 1, (int)Math.Ceiling((z1 + half) / e));
        if (i1 < i0 || j1 < j0) return;
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) SetWeights(i, j);
        if (Layers > SLOTS)
            for (int tj = Math.Max(0, (j0 - 1) / TILE); tj <= Math.Min(_tn - 1, j1 / TILE); tj++)
                for (int ti = Math.Max(0, (i0 - 1) / TILE); ti <= Math.Min(_tn - 1, i1 / TILE); ti++) SetPalette(ti, tj);
        RebuildChunks(i0, j0, i1, j1);
    }

    /// <summary>the weights a vertex has now, one per layer (tests; an original map: its 8 material weights)</summary>
    public float[] VertexWeights(int i, int j)
    {
        var r = new float[Math.Max(SLOTS, Layers)];
        int s = j * Hf.N + i;
        for (int l = 0; l < Layers; l++) r[l] = WeightOf(s, l);
        return r;
    }
    /// <summary>the palette of the tile with vertex (i, j) (tests)</summary>
    public int[] PaletteAt(int i, int j) => PaletteOf(Math.Min(i / TILE, _tn - 1), Math.Min(j / TILE, _tn - 1));
    public int ChunkCount => _chunks.Length;
}
