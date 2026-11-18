// Sprites that belong to a model: 2D parts of buildings, ships and machines (Dragon Clan paper lanterns, banners,
// torch flames, the grass and bushes around buildings): billboard chunks of the GSF (root extras.foliage:
// {mesh, tex, uv, size, attr, pts, bones}). One MultiMesh per billboard node, centres in the node's local space,
// facing the camera; it carries the node's attribute bits so the part states show it in the same states as the meshes
// around it (construction stages, epochs, damage).
//
// Trees and plants (the camera-facing foliage of Vgtn models): the game draws them in a world-wide field (props.js
// FoliageField / treeSprites, not ported yet); TreeSprites makes them per model instead (model viewer, few trees).
//
// Ported from remake/src/engine/props.js (modelSprites, treeSprites, SPRITE_SCALE) (pw-exporter 98c7703).
using System;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.Sim.Core;

namespace PwRemake.View.Models;

public static class ModelSprites
{
    /// <summary>
    /// The drawn height of a GSF sprite is a quarter of its stored size (calibrated on objects of known size: the
    /// Dragon Clan barrels and paper lanterns, flowers, fruit; at the full size a barrel would stand 6.8 m tall).
    /// </summary>
    public const float SPRITE_SCALE = 0.25f;

    /// <summary>The light the sprites are drawn with (main.js: spriteLight = the foliage light).</summary>
    public static Color Light = new(1, 1, 1);

    static Shader? s_shader, s_glow, s_tree;
    static QuadMesh? s_quad;

    static float[] Pts(JsonObject f)
    {
        if (f["pts"] is not JsonArray a) return Array.Empty<float>();
        var r = new float[a.Count];
        for (int i = 0; i < r.Length; i++) r[i] = (float)a[i].Num();
        return r;
    }
    static float[] Uv(JsonObject f) => f["uv"] is JsonArray a && a.Count >= 4 ? new[] { (float)a[0].Num(), (float)a[1].Num(), (float)a[2].Num(), (float)a[3].Num() } : new[] { 0f, 0f, 1f, 1f };

    /// <summary>A building's billboard node as a sprite set. f: a root extras.foliage entry; map: the texture of the
    /// billboard's material; glow: the material adds its picture (GSF material flag 0x4 - the halos of lamps at night).</summary>
    public static MultiMeshInstance3D Create(JsonObject f, Texture2D map, bool glow)
    {
        s_quad ??= new QuadMesh { Size = new Vector2(1, 1) };
        var pts = Pts(f);
        int n = pts.Length / 3;
        float h = (float)f["size"].Num() * SPRITE_SCALE;
        var uv = Uv(f);
        float aspect = (uv[2] - uv[0]) / Mathf.Max(1e-3f, uv[3] - uv[1]);
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = s_quad, InstanceCount = n };
        var box = new Aabb();
        for (int i = 0; i < n; i++)
        {
            var c = new Vector3(pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]);
            // sprites on the ground (grass, bushes around a building) stand on it instead of sinking in half
            if (c.Z < 0.3f) c.Z += h * 0.45f;
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, c));
            box = i == 0 ? new Aabb(c, Vector3.Zero) : box.Expand(c);
        }
        mm.CustomAabb = box.Grow(h * Mathf.Max(1, aspect));
        Shader sh;
        if (glow) sh = s_glow ??= new Shader { Code = Code(true, false) };
        else sh = s_shader ??= new Shader { Code = Code(false, false) };
        var mat = new ShaderMaterial { Shader = sh, RenderPriority = glow ? 3 : 0 };
        mat.SetShaderParameter("map", map);
        mat.SetShaderParameter("uv_rect", new Vector4(uv[0], uv[1], uv[2], uv[3]));
        mat.SetShaderParameter("size", new Vector2(h * aspect, h));
        mat.SetShaderParameter("light", new Vector3(Light.R, Light.G, Light.B));
        return new MultiMeshInstance3D
        {
            Multimesh = mm, MaterialOverride = mat, Name = f["mesh"].Str() + "_sprites",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    static readonly Regex Lod = new("_lod([0-9a-f]+)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// The foliage of a tree or plant (Vgtn / Ress / Deko) as sprites in the model's own frame (to be added below the
    /// Model node: the GSF frame, so the stored points are used as they are). props.js treeSprites, per model.
    /// Sprites of the original's close-range LOD layer (not drawn at its LOD level 2) are marked as detail.
    /// </summary>
    public static MultiMeshInstance3D? CreateTree(JsonObject f, Texture2D map, Random rnd, float top)
    {
        var tex = f["tex"].Str();
        if (tex == "" || Regex.IsMatch(tex, "particle", RegexOptions.IgnoreCase)) return null;   // no texture / particle emitters (butterflies etc.)
        s_quad ??= new QuadMesh { Size = new Vector2(1, 1) };
        var pts = Pts(f);
        int n = pts.Length / 3;
        if (n == 0) return null;
        float size = (float)f["size"].Num() * SPRITE_SCALE;
        var uv = Uv(f);
        float aspect = (uv[2] - uv[0]) / Mathf.Max(1e-3f, uv[3] - uv[1]);
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = s_quad, InstanceCount = n };
        var box = new Aabb();
        for (int i = 0; i < n; i++)
        {
            var c = new Vector3(pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]);
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, c));
            // rotation in the screen plane, tint by height (top of the crown lighter)
            float rot = aspect != 1 ? (float)(rnd.NextDouble() - 0.5) * 0.25f : (float)rnd.NextDouble() * 6.283f;
            float tint = Mathf.Clamp(c.Z / top, 0, 1) * 0.8f + (float)rnd.NextDouble() * 0.2f;
            mm.SetInstanceCustomData(i, new Color(rot, tint, aspect, 0));
            box = i == 0 ? new Aabb(c, Vector3.Zero) : box.Expand(c);
        }
        mm.CustomAabb = box.Grow(size * Mathf.Max(1, aspect));
        s_tree ??= new Shader { Code = Code(false, true) };
        var mat = new ShaderMaterial { Shader = s_tree };
        mat.SetShaderParameter("map", map);
        mat.SetShaderParameter("uv_rect", new Vector4(uv[0], uv[1], uv[2], uv[3]));
        mat.SetShaderParameter("size", new Vector2(size, size));
        mat.SetShaderParameter("light", new Vector3(Light.R, Light.G, Light.B));
        var lm = Lod.Match(f["mesh"].Str());
        bool detail = lm.Success && (Convert.ToInt32(lm.Groups[1].Value, 16) & 4) == 0;
        return new MultiMeshInstance3D
        {
            Multimesh = mm, MaterialOverride = mat, Name = f["mesh"].Str() + "_foliage",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            // the detail layer only near the camera (FoliageField.detailDist 150)
            VisibilityRangeEnd = detail ? 150 : 0,
        };
    }

    /// <summary>The highest foliage point of a model (treeSprites: the tint runs from the ground to it).</summary>
    public static float FoliageTop(JsonArray foliage)
    {
        float top = 0.5f;
        foreach (var e in foliage)
            if (e is JsonObject f) { var p = Pts(f); for (int i = 2; i < p.Length; i += 3) top = Mathf.Max(top, p[i]); }
        return top;
    }

    static string Code(bool glow, bool tree) => @"
shader_type spatial;
render_mode " + (glow ? "blend_add, depth_draw_never, fog_disabled, " : "") + @"unshaded, cull_disabled, shadows_disabled;
uniform sampler2D map : " + (glow ? "" : "source_color, ") + @"filter_linear_mipmap_anisotropic;
uniform vec4 uv_rect;
uniform vec2 size;
uniform vec3 light = vec3(1.0);
varying float v_tint;
void vertex() {
    // the instance's centre in view space, the quad spread in the screen plane (scaled with the model)
    vec4 mv = MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0);
    float sc = length(MODEL_MATRIX[0].xyz);
    vec2 q = VERTEX.xy;
" + (tree ? @"    float s = sin(INSTANCE_CUSTOM.x), c = cos(INSTANCE_CUSTOM.x);
    q.x *= INSTANCE_CUSTOM.z;
    q = vec2(q.x * c - q.y * s, q.x * s + q.y * c);
    v_tint = INSTANCE_CUSTOM.y;
" : "    v_tint = 1.0;\n") + @"    mv.xy += q * size * sc;
    UV = mix(uv_rect.xy, uv_rect.zw, vec2(VERTEX.x + 0.5, 0.5 - VERTEX.y));
    POSITION = PROJECTION_MATRIX * mv;
}
" + (glow ? PwRemake.View.Fx.GammaBlend.Glsl : "") + @"
void fragment() {
    vec4 t = texture(map, UV);
" + (glow
        // (a glow sprite - a lantern's halo - is added as the original adds: to the screen's sRGB value, its texture
        // sampled as sRGB values; Fx/GammaBlend)
        ? "    ALBEDO = pw_gamma_add(t.rgb);\n" : "    if (t.a < 0.5) discard;\n    ALBEDO = t.rgb * light" + (tree ? " * (0.55 + 0.55 * v_tint)" : "") + ";\n") + @"}
";
}
