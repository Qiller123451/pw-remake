// Camera-facing foliage sprites of the whole map: the leaves of trees and plants (the GSF billboard chunks of
// Vgtn / Ress / Deko models, root extras.foliage), the undergrowth's leaf clusters and flowers. Sprites are grouped by
// texture and 64 m chunk, one MultiMesh per group (instance origin = the sprite's centre; colour = size, rotation in
// the screen plane, tint, aspect; custom data = the atlas cell's uv rectangle). Detail sprites (the original's
// close-range LOD layer) are a second MultiMesh per group, drawn only for chunks within DetailDist of the camera.
//
//   var h = field.Add(texture, sprites);   // a handle to hide them later (a tree felled, a building put down)
//   field.Build();                          // after the map's sprites are added
//   field.UpdateView(cameraPosition);       // every frame (the detail layer)
//   field.Hide(h);
//
// Also treeSprites: the foliage of a model's root extras as world sprites for one placed instance.
// Ported from remake/src/engine/props.js FoliageField, treeSprites, SPRITE_SCALE (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.Sim.Core;

namespace PwRemake.View.World;

/// <summary>one foliage sprite in world space (props.js: {x, y, z, size, uv, detail, rot, tint, aspect})</summary>
public struct FoliageSprite
{
    public float X, Y, Z, Size;
    public Vector4 Uv;
    public bool Detail;
    /// <summary>rotation in the screen plane; null = random</summary>
    public float? Rot;
    /// <summary>0..1 brightness; null = random</summary>
    public float? Tint;
    public float Aspect;
}

public sealed class FoliageHandle
{
    internal FoliageField.Group G = null!;
    internal int Start, Count, DStart, DCount;
}

public partial class FoliageField : Node3D
{
    /// <summary>
    /// The drawn height of a GSF sprite is a quarter of its stored size (calibrated on objects of known size: the Dragon
    /// Clan barrels and paper lanterns, flowers, fruit; at the full size a barrel would stand 6.8 m tall).
    /// </summary>
    public const float SPRITE_SCALE = 0.25f;

    internal sealed class Group
    {
        public Texture2D Tex = null!;
        public int Cx, Cz;
        public readonly List<FoliageSprite> List = new(), DList = new();
        public MultiMeshInstance3D? Mesh, DMesh;
    }

    const string Code = @"shader_type spatial;
render_mode unshaded, cull_disabled, skip_vertex_transform;
uniform sampler2D map : source_color, filter_linear_mipmap_anisotropic;
uniform vec3 light = vec3(1.0);
uniform vec4 u_holes[16];
uniform int u_hole_count = 0;
varying vec2 v_uv;
varying float v_tint;
varying vec3 v_w;
void vertex() {
    // the instance's centre; the quad spread in the view plane (in the shadow pass: facing the light)
    vec3 c = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    vec4 mv = VIEW_MATRIX * vec4(c, 1.0);
    float s = sin(INSTANCE_CUSTOM.x), co = cos(INSTANCE_CUSTOM.x);
    vec2 q = vec2(VERTEX.x * INSTANCE_CUSTOM.w, VERTEX.y);
    vec2 p = vec2(q.x * co - q.y * s, q.x * s + q.y * co) * INSTANCE_CUSTOM.z;
    mv.xy += p;
    v_uv = mix(COLOR.xy, COLOR.zw, vec2(VERTEX.x + 0.5, 0.5 - VERTEX.y));
    v_tint = INSTANCE_CUSTOM.y;
    v_w = c;
    VERTEX = mv.xyz;
    NORMAL = vec3(0.0, 0.0, 1.0);
}
void fragment() {
    vec4 t = texture(map, v_uv);
    if (t.a < 0.5) discard;
    // see-through canopies: fragments between the camera and a unit are dithered away (props.js applyHoles)
    float dith = fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715))));
    vec3 cam = CAMERA_POSITION_WORLD;
    for (int i = 0; i < 16; i++) { if (i >= u_hole_count) break;
        vec3 hc = u_holes[i].xyz; vec3 d = normalize(cam - hc); vec3 p = v_w - hc; float tt = dot(p, d);
        if (tt > 1.5) { float r = u_holes[i].w; float f = clamp((r - length(p - d * tt)) / (r * 0.45), 0.0, 1.0); if (f > dith) discard; } }
    ALBEDO = t.rgb * light * (0.55 + 0.55 * v_tint);
}
";

    readonly int _chunk;
    readonly Dictionary<(ulong, int, int), Group> _groups = new();
    readonly Dictionary<ulong, ShaderMaterial> _mats = new();
    static Shader? s_shader;
    static QuadMesh? s_quad;
    readonly Random _rnd = new(1234);
    public Vector3 Light = new(1, 1, 1);
    public float DetailDist = 150;
    Vector4[] _holes = new Vector4[16];
    public int SpriteCount { get; private set; }

    public FoliageField(int chunk = 80) { _chunk = chunk; Name = "Foliage"; }

    /// <summary>sprites of one texture (one tree, one bush); returns a handle to hide them later</summary>
    public FoliageHandle? Add(Texture2D? tex, IReadOnlyList<FoliageSprite> sprites)
    {
        if (tex == null || sprites.Count == 0) return null;
        int cx = (int)Math.Floor(sprites[0].X / _chunk), cz = (int)Math.Floor(sprites[0].Z / _chunk);
        var key = (tex.GetRid().Id, cx, cz);
        if (!_groups.TryGetValue(key, out var g)) { g = new Group { Tex = tex, Cx = cx, Cz = cz }; _groups[key] = g; }
        var h = new FoliageHandle { G = g, Start = g.List.Count, DStart = g.DList.Count };
        foreach (var s in sprites) { if (s.Detail) { g.DList.Add(s); h.DCount++; } else { g.List.Add(s); h.Count++; } }
        SpriteCount += sprites.Count;
        return h;
    }

    public void Hide(FoliageHandle? h)
    {
        if (h == null) return;
        static void Zero(MultiMeshInstance3D? mesh, int start, int count)
        {
            if (mesh == null || count == 0) return;
            var mm = mesh.Multimesh;
            for (int i = start; i < start + count; i++) { var c = mm.GetInstanceCustomData(i); c.B = 0; mm.SetInstanceCustomData(i, c); }
        }
        Zero(h.G.Mesh, h.Start, h.Count);
        Zero(h.G.DMesh, h.DStart, h.DCount);
    }

    /// <summary>show the detail layer only for chunks near the camera</summary>
    public void UpdateView(Vector3 camPos)
    {
        float c = _chunk, D2 = DetailDist * DetailDist;
        foreach (var g in _groups.Values)
        {
            if (g.DMesh == null) continue;
            float dx = (g.Cx + 0.5f) * c - camPos.X, dz = (g.Cz + 0.5f) * c - camPos.Z;
            g.DMesh.Visible = dx * dx + dz * dz < D2;
        }
    }

    public void Build()
    {
        foreach (var g in _groups.Values)
        {
            if (g.Mesh == null && g.List.Count > 0) { g.Mesh = MakeMesh(g, g.List); AddChild(g.Mesh); }
            if (g.DMesh == null && g.DList.Count > 0) { g.DMesh = MakeMesh(g, g.DList); AddChild(g.DMesh); }
        }
    }

    MultiMeshInstance3D MakeMesh(Group g, List<FoliageSprite> list)
    {
        s_quad ??= new QuadMesh { Size = new Vector2(1, 1) };
        int n = list.Count;
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true,
            Mesh = s_quad, InstanceCount = n,
        };
        var buf = new float[n * 20];
        Aabb box = default;
        float maxS = 0;
        for (int i = 0; i < n; i++)
        {
            var s = list[i];
            int o = i * 20;
            buf[o] = 1; buf[o + 3] = s.X; buf[o + 5] = 1; buf[o + 7] = s.Y; buf[o + 10] = 1; buf[o + 11] = s.Z;
            // colour: the uv rectangle; custom: rotation, tint, size, aspect
            buf[o + 12] = s.Uv.X; buf[o + 13] = s.Uv.Y; buf[o + 14] = s.Uv.Z; buf[o + 15] = s.Uv.W;
            buf[o + 16] = s.Rot ?? (float)(_rnd.NextDouble() * 6.283); buf[o + 17] = s.Tint ?? (float)_rnd.NextDouble();
            buf[o + 18] = s.Size; buf[o + 19] = s.Aspect > 0 ? s.Aspect : 1;
            var p = new Vector3(s.X, s.Y, s.Z);
            box = i == 0 ? new Aabb(p, Vector3.Zero) : box.Expand(p);
            maxS = Math.Max(maxS, s.Size * Math.Max(1, s.Aspect));
        }
        mm.Buffer = buf;
        mm.CustomAabb = box.Grow(maxS);
        return new MultiMeshInstance3D
        {
            Multimesh = mm, MaterialOverride = Mat(g.Tex), Name = "foliage",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };
    }

    ShaderMaterial Mat(Texture2D tex)
    {
        var id = tex.GetRid().Id;
        if (_mats.TryGetValue(id, out var m)) return m;
        s_shader ??= new Shader { Code = Code };
        m = new ShaderMaterial { Shader = s_shader };
        m.SetShaderParameter("map", tex);
        m.SetShaderParameter("light", Light);
        m.SetShaderParameter("u_holes", _holes);
        _mats[id] = m;
        return m;
    }

    /// <summary>the light the sprites are drawn with (main.js: foliage.light 0.86, 0.9, 0.8)</summary>
    public void SetLight(Vector3 light)
    {
        Light = light;
        foreach (var m in _mats.Values) m.SetShaderParameter("light", light);
    }

    /// <summary>see-through canopies: up to 16 points (x, y, z, radius) - units under the trees - whose line to the camera is kept clear</summary>
    public void SetHoles(IReadOnlyList<Vector4> holes)
    {
        int n = Math.Min(16, holes.Count);
        for (int i = 0; i < 16; i++) _holes[i] = i < n ? holes[i] : default;
        foreach (var m in _mats.Values) { m.SetShaderParameter("u_holes", _holes); m.SetShaderParameter("u_hole_count", n); }
    }

    static readonly Regex Lod = new("_lod([0-9a-f]+)$", RegexOptions.IgnoreCase);
    static readonly Regex Particle = new("particle", RegexOptions.IgnoreCase);

    /// <summary>
    /// treeSprites: the root extras.foliage of a tree / plant / decoration template as world sprites for one placed
    /// instance (x, y, z, heading rotY, scale), grouped by texture file. ground: ground cover (flowers, reeds, grass of
    /// the landscape objects) - upright stalks standing on the ground. Left out: glows (GSF material flag 0x4, marked
    /// "glow" by ModelLibrary - additive, WorldView.AddGlowSprites draws them) and the lights shown only at night
    /// (attribute bit 20 of buildings and decorations: a lantern's halo; the map's decorations show their day look).
    /// </summary>
    public static bool FieldSprite(JsonObject f, string fourcc) =>
        !f["glow"].Bool() && PwRemake.View.Parts.PartBits.TodOf((uint)f["attr"].Num(), fourcc) != PwRemake.View.Parts.TimeOfDay.Night;

    public static Dictionary<string, List<FoliageSprite>> TreeSprites(JsonObject? extras, double x, double y, double z, double rotY, double scale, bool ground, Random rnd)
    {
        var outMap = new Dictionary<string, List<FoliageSprite>>();
        if (extras?["foliage"] is not JsonArray foliage) return outMap;
        double s = Math.Sin(rotY), c = Math.Cos(rotY);
        double top = 0.5;
        foreach (var e in foliage) if (e?["pts"] is JsonArray pa) for (int i = 2; i < pa.Count; i += 3) top = Math.Max(top, pa[i].Num());
        foreach (var e in foliage)
        {
            if (e is not JsonObject f || !FieldSprite(f, extras!["fourcc"].Str())) continue;
            string tex = f["tex"].Str();
            if (tex == "" || Particle.IsMatch(tex)) continue;     // no texture / particle emitters (butterflies etc.)
            if (!outMap.TryGetValue(tex, out var list)) outMap[tex] = list = new List<FoliageSprite>();
            if (f["pts"] is not JsonArray p) continue;
            var lm = Lod.Match(f["mesh"].Str());
            bool detail = lm.Success && (Convert.ToInt32(lm.Groups[1].Value, 16) & 4) == 0;   // not drawn at the original's LOD level 2 -> near-only layer
            Vector4 uv = f["uv"] is JsonArray u && u.Count >= 4 ? new Vector4((float)u[0].Num(), (float)u[1].Num(), (float)u[2].Num(), (float)u[3].Num()) : new Vector4(0, 0, 1, 1);
            double aspect = f["uv"] is JsonArray ? (uv.Z - uv.X) / Math.Max(1e-3, uv.W - uv.Y) : 1;
            double fsize = f["size"].Num();
            for (int i = 0; i + 2 < p.Count; i += 3)
            {
                // Z-up model coords -> Y-up (-x, z, -y) (the models' root mirrors x, game/maps/frame.js), then yaw + scale
                double lx = -p[i].Num(), ly = p[i + 2].Num(), lz = -p[i + 1].Num();
                bool gr = ground && ly < 0.5;
                double size = fsize * SPRITE_SCALE * scale;
                list.Add(new FoliageSprite
                {
                    X = (float)(x + (lx * c + lz * s) * scale), Y = (float)(y + ly * scale + (gr ? size * 0.45 : 0)), Z = (float)(z + (-lx * s + lz * c) * scale),
                    Size = (float)size, Uv = uv, Detail = detail, Aspect = (float)aspect,
                    Rot = gr || aspect != 1 ? (float)((rnd.NextDouble() - 0.5) * 0.25) : null,
                    Tint = (float)(Math.Min(1, Math.Max(0, ly / top)) * 0.8 + rnd.NextDouble() * 0.2),
                });
            }
        }
        return outMap;
    }
}
