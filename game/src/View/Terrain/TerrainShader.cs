// The ground's splat shader: up to 8 texture layers blended per vertex weight.
//   * the layers are one Texture2DArray: the map's 8 ground materials (scape_k) - with biomes (docs/BIOMES.md) every
//     biome's 8, any number of them. A vertex refers to 8 of them (4 for the random map): its tile's palette, a vertex
//     attribute (CUSTOM2: two layer indices per component, a + 256 b), weights in CUSTOM0 / CUSTOM1. The vertex shader
//     decodes the palette and looks up the layers' scales (flat varyings), so the pixels cost what 8 separate textures
//     cost, however many biomes the map has; an original map's palette is layers 0..7 - the materials in their own
//     order, as the JS drew them (LayerPick, TerrainMesh).
//   * every material is sampled at two scales, the second turned (mat2(0.8, -0.6, 0.6, 0.8)) and mixed in by a value
//     noise -> no visible tile grid
//   * height blending: a material's weight is multiplied by its own detail (brightness relative to the texture's
//     average, a very blurred mip) and squared, so transitions follow the textures' structure (stones, tufts) instead
//     of a soft blur, and a bright material does not win over a dark one just for being bright
//   * steep slopes (normal y below 0.62 .. 0.38) are projected from the side (x / z facing) instead of from above, so
//     cliffs do not smear
//   * a large-scale brightness variation (0.9 .. 1.1)
// The borders between materials are warped already in the weights (MapSource.Splat: a noise shift of the lookup and
// four taps). Lit like three.js' MeshLambertMaterial (diffuse only, no specular).
//
// Ported from remake/src/engine/terrain.js buildTerrain (onBeforeCompile) (pw-exporter 98c7703): GLSL -> Godot shading
// language (the JS: one sampler per material; here one texture array and the palette).
using System.Linq;
using System.Text;

namespace PwRemake.View.Terrain;

public static class TerrainShader
{
    /// <summary>the most layers a map can have (16 biomes of 8 materials): the scales' uniform array (two per vec4)</summary>
    public const int MAX_LAYERS = 128;

    /// <summary>the shader code for n = 4 or 8 slots per vertex (the JS made one program per count: 'pw-terrain' + N)</summary>
    public static string Code(int n)
    {
        var sb = new StringBuilder();
        sb.Append(@"shader_type spatial;
render_mode diffuse_lambert, specular_disabled;
uniform sampler2DArray ta : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
");
        sb.Append($"uniform vec4 sc[{MAX_LAYERS / 2}];          // the layers' scales: layer 2i in .xy, 2i + 1 in .zw\n");
        sb.Append(@"varying vec4 v_splat;
" + (n > 4 ? "varying vec4 v_splat2;\n" : "") + @"// the palette's layers (slots 0-3, 4-7) and their scales (two slots per vec4), decoded per vertex: flat, the
// same at every vertex of a tile
varying flat vec4 v_la;
varying flat vec4 v_lb;
varying flat vec4 v_s01;
varying flat vec4 v_s23;
" + (n > 4 ? "varying flat vec4 v_s45;\nvarying flat vec4 v_s67;\n" : "") + @"varying vec3 v_wpos;
varying vec3 v_wn;

// value noise for breaking up the texture repetition
float pw_hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float pw_noise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(pw_hash(i), pw_hash(i + vec2(1.0, 0.0)), f.x), mix(pw_hash(i + vec2(0.0, 1.0)), pw_hash(i + vec2(1.0, 1.0)), f.x), f.y);
}
// one ground material: two scales (the second rotated) mixed by noise -> no visible tile grid
vec3 pw_sample(float l, vec2 uv, float n) {
    vec3 a = texture(ta, vec3(uv, l)).rgb;
    vec2 uv2 = mat2(vec2(0.8, -0.6), vec2(0.6, 0.8)) * uv * 0.43 + 0.37;
    return mix(a, texture(ta, vec3(uv2, l)).rgb, n * 0.55);
}
// the texture's average brightness (a very blurred mip) - detail = brightness above/below it
float pw_mean(float l, vec2 uv) { return dot(texture(ta, vec3(uv, l), 9.0).rgb, vec3(0.333)); }
// steep slopes: project from the side (x/z facing) instead of from above, so cliffs don't smear
vec3 pw_tex(float l, vec2 s, float n, vec3 wp, vec3 nw, float steep) {
    vec3 top = pw_sample(l, wp.xz * s, n);
    if (steep < 0.02) return top;
    vec3 side = abs(nw.x) > abs(nw.z) ? pw_sample(l, wp.zy * s, n) : pw_sample(l, wp.xy * s, n);
    return mix(top, side, steep);
}
// a layer's scale
vec2 pw_sc(float l) { int i = int(l + 0.5); vec4 v = sc[i / 2]; return (i % 2) == 0 ? v.xy : v.zw; }

void vertex() {
    v_splat = CUSTOM0;
" + (n > 4 ? "    v_splat2 = CUSTOM1;\n" : "") + @"    vec4 p = CUSTOM2;
    vec4 la = vec4(mod(p.x, 256.0), floor(p.x / 256.0), mod(p.y, 256.0), floor(p.y / 256.0));
    vec4 lb = vec4(mod(p.z, 256.0), floor(p.z / 256.0), mod(p.w, 256.0), floor(p.w / 256.0));
    v_la = la; v_lb = lb;
    v_s01 = vec4(pw_sc(la.x), pw_sc(la.y));
    v_s23 = vec4(pw_sc(la.z), pw_sc(la.w));
" + (n > 4 ? "    v_s45 = vec4(pw_sc(lb.x), pw_sc(lb.y));\n    v_s67 = vec4(pw_sc(lb.z), pw_sc(lb.w));\n" : "") + @"    v_wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_wn = normalize(mat3(MODEL_MATRIX) * NORMAL);
}

void fragment() {
    vec3 nw = normalize(v_wn);
    float steep = smoothstep(0.62, 0.38, nw.y);
    float nz = pw_noise(v_wpos.xz * 0.045) * 0.7 + pw_noise(v_wpos.xz * 0.011) * 0.3;
");
        if (n > 4) sb.Append("    float tot = max(0.001, dot(v_splat, vec4(1.0)) + dot(v_splat2, vec4(1.0)));\n    vec4 s = v_splat / tot, s2 = v_splat2 / tot;\n");
        else sb.Append("    vec4 s = v_splat / max(0.001, dot(v_splat, vec4(1.0)));\n");
        sb.Append("    vec3 c = vec3(0.0); float ht = 0.0;\n");
        // every slot: its layer (the palette), colour, weighted by splat weight x its own detail (brightness relative
        // to the texture's average), squared
        for (int k = 0; k < n; k++)
        {
            string w = k < 4 ? "s." + "xyzw"[k] : "s2." + "xyzw"[k - 4];
            string layer = (k < 4 ? "v_la." : "v_lb.") + "xyzw"[k % 4];
            string scale = $"v_s{k / 2 * 2}{k / 2 * 2 + 1}." + (k % 2 == 0 ? "xy" : "zw");
            sb.Append($"    if ({w} > 0.004) {{ float l{k} = {layer}; vec2 q{k} = {scale}; vec3 c{k} = pw_tex(l{k}, q{k}, nz, v_wpos, nw, steep); float h{k} = {w} * clamp(0.55 + 1.6 * (dot(c{k}, vec3(0.333)) - pw_mean(l{k}, v_wpos.xz * q{k})), 0.08, 1.0); h{k} *= h{k}; c += c{k} * h{k}; ht += h{k}; }}\n");
        }
        sb.Append(@"    c /= max(ht, 1e-4);
    c *= 0.9 + 0.2 * pw_noise(v_wpos.xz * 0.02);          // large-scale brightness variation
    ALBEDO = c;
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;
}
");
        return sb.ToString();
    }

    /// <summary>the JS default scales when a source gives none (buildTerrain opts.scales)</summary>
    public static readonly double[][] DefaultScales = { new[] { 1 / 14.0, 1 / 3.8 }, new[] { 1 / 14.0, 1 / 3.8 }, new[] { 1 / 12.0, 1 / 4.3 }, new[] { 1 / 12.0, 1 / 2.0 } };

    public static string Dump(int n) => string.Join("\n", Code(n).Split('\n').Select((l, i) => $"{i + 1,4} {l}"));
}
