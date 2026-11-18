// Ground grass, like the original: clumps from the setting's (the biome's) grass atlas (Texture/Scape/<Setting>/grassblades_(0512).dds,
// 4 x 4 sprites -> assets/terrain/<Setting>/grass.png) scattered over the grassy ground materials around the camera.
//
// The ground is divided into tiles of TILE metres. Every tile gets the same clumps every time it is built (positions
// come from a hash of the tile coordinates), so nothing shimmers when the camera moves. Only the tiles within
// `radius` of the camera target are drawn; clumps shrink to nothing towards that edge, so they fade in smoothly.
//
//   var grass = GrassLayer.Create(texture, height: (x, z) => y, density: (x, z) => 0..1, water, light);
//   grass.UpdateView(cameraTarget)   // every frame (rebuilds only when the camera moved half a tile)
//
// Godot: one MultiMesh of quads (instance origin = the clump's foot, custom data = height, atlas cell, tint), built
// into camera-facing sprites standing on their foot by the shader (the JS's InstancedBufferGeometry).
// Ported from remake/src/engine/grass.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using Godot;

namespace PwRemake.View.Terrain;

public partial class GrassLayer : MultiMeshInstance3D
{
    const int TILE = 16;            // m
    const int PER_TILE = 150;       // candidate clumps per tile (kept with probability = density)

    const string Code = @"shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, skip_vertex_transform;
uniform sampler2D map : source_color, filter_linear_mipmap_anisotropic;
uniform vec3 light;
uniform vec3 u_cam;          // camera target (for the fade at the edge)
uniform float u_radius;
varying vec2 v_uv;
varying float v_tint;
varying float v_shade;
void vertex() {
    vec3 foot_w = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    float hgt = INSTANCE_CUSTOM.x;
    float d = length(foot_w.xz - u_cam.xz);
    float h = hgt * (1.0 - smoothstep(u_radius * 0.7, u_radius, d));
    // a camera-facing sprite standing on its foot (like the original's ground sprites): built in view space
    vec4 foot = VIEW_MATRIX * vec4(foot_w, 1.0);
    float sway = sin(TIME * 1.7 + foot_w.x * 0.35 + foot_w.z * 0.21) * 0.10 * VERTEX.y * h;
    vec4 mv = foot + vec4(VERTEX.x * h * 1.7 + sway, VERTEX.y * h, 0.0, 0.0);
    float cell = INSTANCE_CUSTOM.y;
    vec2 c = vec2(mod(cell, 4.0), floor(cell / 4.0));
    v_uv = (c + vec2(VERTEX.x + 0.5, 1.0 - VERTEX.y)) / 4.0;
    v_uv.y = 1.0 - v_uv.y;
    v_tint = INSTANCE_CUSTOM.z;
    v_shade = 0.55 + 0.45 * VERTEX.y;          // darker at the roots
    VERTEX = mv.xyz;
    NORMAL = vec3(0.0, 0.0, 1.0);
}
void fragment() {
    vec4 t = texture(map, v_uv);
    if (t.a < 0.45) discard;
    ALBEDO = t.rgb * light * v_shade * (0.85 + 0.3 * v_tint);
}
";

    /// <summary>the light the blades are drawn with (unshaded sprites: the foliage's light)</summary>
    public void SetLight(Vector3 light) => _mat.SetShaderParameter("light", light);

    Func<double, double, double> _height = null!, _density = null!;
    double? _water;
    double _radius, _size;
    int _seed;
    readonly Dictionary<(int, int), float[]> _tiles = new();
    readonly LinkedList<(int, int)> _order = new();
    (double x, double z)? _center;
    int _max;
    float[] _buf = Array.Empty<float>();
    ShaderMaterial _mat = null!;
    public int Count { get; private set; }

    /// <summary>
    /// texture: the setting's grass atlas; height(x, z): the ground; density(x, z): 0..1 grass at a point; water: the
    /// water level (no grass below it + 0.3 m); light: the foliage light; radius: drawn around the camera target (m);
    /// size: a clump's height (m); seed: mixed into the tiles' random numbers (0: the JS's; a map's other biomes' grass
    /// layers have their own, so their clumps do not stand on the same spots).
    /// </summary>
    public static GrassLayer Create(Texture2D texture, Func<double, double, double> height, Func<double, double, double> density,
        double? water, Vector3 light, double radius = 100, double size = 1.5, int seed = 0)
    {
        var g = new GrassLayer { Name = "Grass", _height = height, _density = density, _water = water, _radius = radius, _size = size, _seed = seed };
        var quad = new QuadMesh { Size = new Vector2(1, 1), CenterOffset = new Vector3(0, 0.5f, 0) };   // y 0..1: foot at the bottom
        g._max = (int)Math.Ceiling(Math.Pow(2 * radius / TILE + 2, 2)) * PER_TILE;
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = quad,
            InstanceCount = g._max, VisibleInstanceCount = 0,
        };
        g._mat = new ShaderMaterial { Shader = new Shader { Code = Code }, RenderPriority = 1 };
        g._mat.SetShaderParameter("map", texture);
        g._mat.SetShaderParameter("light", light);
        g._mat.SetShaderParameter("u_radius", (float)radius);
        g.Multimesh = mm;
        g.MaterialOverride = g._mat;
        g.CastShadow = ShadowCastingSetting.Off;
        g._buf = new float[g._max * 16];
        return g;
    }

    // deterministic random numbers for a tile (mulberry32-like, as grass.js rng)
    static Func<double> Rng(int seed)
    {
        uint s = unchecked((uint)seed);
        return () =>
        {
            s = unchecked(s + 0x6D2B79F5);
            uint t = s;
            t = unchecked((t ^ (t >> 15)) * (t | 1));
            t ^= unchecked(t + (t ^ (t >> 7)) * (t | 61));
            return ((t ^ (t >> 14)) & 0xFFFFFFFFu) / 4294967296.0;
        };
    }

    float[] Tile(int i, int j)
    {
        if (_tiles.TryGetValue((i, j), out var t)) return t;
        var r = Rng(unchecked((i * 73856093) ^ (j * 19349663) ^ _seed));
        var outList = new List<float>();
        for (int k = 0; k < PER_TILE; k++)
        {
            double x = (i + r()) * TILE, z = (j + r()) * TILE, keep = r(), cell = Math.Floor(r() * 16), tint = r(), hs = r();
            double d = _density(x, z);
            if (keep >= d) continue;
            double y = _height(x, z);
            if (_water is double w && y < w + 0.3) continue;
            outList.Add((float)x); outList.Add((float)(y - 0.05)); outList.Add((float)z);
            outList.Add((float)(_size * (0.75 + 0.5 * hs))); outList.Add((float)cell); outList.Add((float)tint);
        }
        t = outList.ToArray();
        _tiles[(i, j)] = t;
        _order.AddLast((i, j));
        if (_tiles.Count > 4000) { _tiles.Remove(_order.First!.Value); _order.RemoveFirst(); }   // keep the cache bounded
        return t;
    }

    void Rebuild(double cx, double cz)
    {
        double R = _radius;
        int i0 = (int)Math.Floor((cx - R) / TILE), i1 = (int)Math.Floor((cx + R) / TILE);
        int j0 = (int)Math.Floor((cz - R) / TILE), j1 = (int)Math.Floor((cz + R) / TILE);
        int n = 0;
        var B = _buf;
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                double dx = Math.Max(0, Math.Abs((i + 0.5) * TILE - cx) - TILE / 2.0), dz = Math.Max(0, Math.Abs((j + 0.5) * TILE - cz) - TILE / 2.0);
                if (dx * dx + dz * dz > R * R) continue;
                var t = Tile(i, j);
                for (int k = 0; k < t.Length && n < _max; k += 6, n++)
                {
                    int o = n * 16;
                    // Transform3D rows (basis x, origin x; basis y, origin y; basis z, origin z), then the custom data
                    B[o] = 1; B[o + 1] = 0; B[o + 2] = 0; B[o + 3] = t[k];
                    B[o + 4] = 0; B[o + 5] = 1; B[o + 6] = 0; B[o + 7] = t[k + 1];
                    B[o + 8] = 0; B[o + 9] = 0; B[o + 10] = 1; B[o + 11] = t[k + 2];
                    B[o + 12] = t[k + 3]; B[o + 13] = t[k + 4]; B[o + 14] = t[k + 5]; B[o + 15] = 0;
                }
            }
        Multimesh.Buffer = B;
        Multimesh.VisibleInstanceCount = n;
        // the instances follow the camera: bounds = around the camera target
        Multimesh.CustomAabb = new Aabb(new Vector3((float)(cx - R - 4), -1000, (float)(cz - R - 4)), new Vector3((float)(2 * R + 8), 4000, (float)(2 * R + 8)));
        Count = n;
    }

    /// <summary>every frame: the camera target (rebuilds only when it moved half a tile)</summary>
    public void UpdateView(Vector3 target)
    {
        _mat.SetShaderParameter("u_cam", target);
        if (_center == null || Math.Sqrt(Math.Pow(target.X - _center.Value.x, 2) + Math.Pow(target.Z - _center.Value.z, 2)) > TILE / 2.0)
        {
            _center = (target.X, target.Z);
            Rebuild(target.X, target.Z);
        }
    }

    /// <summary>forget the cached tiles around (x, z) - or all of them (null) - and rebuild (buildings placed or removed, settings)</summary>
    public void Invalidate(double? x = null, double? z = null, double r = 0)
    {
        if (x == null || z == null) { _tiles.Clear(); _order.Clear(); }
        else
        {
            for (int j = (int)Math.Floor((z.Value - r) / TILE); j <= (int)Math.Floor((z.Value + r) / TILE); j++)
                for (int i = (int)Math.Floor((x.Value - r) / TILE); i <= (int)Math.Floor((x.Value + r) / TILE); i++)
                    if (_tiles.Remove((i, j))) _order.Remove((i, j));
        }
        _center = null;
    }

    // (how much grass grows on each ground material of a setting - the JS's GRASS_DENSITY - is the biomes' grassDensity
    // now: Sim/Maps/Biomes.cs BiomeSet.Original, game/maps/biomes.json)
}
