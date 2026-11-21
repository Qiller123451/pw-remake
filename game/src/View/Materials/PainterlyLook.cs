// The painterly look of the HD heroes (docs/HD_HEROES.md): an Arcane-like, hand-painted shading for the models made
// by tools/hd_heroes/refit.py. Their materials carry the glTF extra pw_style = "painterly"; ModelMaterials.ToLambert
// hands them here instead of making its Lambert material.
//
// What the shader does (all values are uniforms below, tuned together - change them here):
//   * light in three soft bands (shadow / mid tone / light) instead of a smooth Lambert ramp, the border between
//     them broken up by a brush-stroke noise in texture space, so it looks painted rather than computed;
//   * shadows tinted cool (violet-blue) and the lit side warm, as a painter mixes them, instead of plain darker colour;
//   * a soft warm rim light on the lit side and a small, hard highlight - the "painted" highlight of the Arcane look;
//   * the outline of the figure slightly darkened (view-angle contour), which separates the hero from the ground
//     at the game's camera distance without the hard black outlines of a Telltale look;
//   * a little more saturation than the texture has.
// The toolkit's model viewer draws the same look (ParaWorldToolkit pwexport/web/materials.js, PainterlyMaterial): when a
// value here changes, change it there too.
// It is a single pass (opaque, or cut out for the hair; no next pass): UnitsBehind (silhouettes behind buildings) and the shadow pass treat it
// like the other model materials. Only ever on HD models (the "HD heroes" option); everything else keeps the
// original's Lambert look.
using Godot;

namespace PwRemake.View.Materials;

public static class PainterlyLook
{
    static readonly System.Collections.Generic.Dictionary<(bool cut, bool twoSided), Shader> s_shaders = new();

    /// <summary>true for a glTF material that asks for this look (extras.pw_style = "painterly")</summary>
    public static bool Wants(System.Text.Json.Nodes.JsonObject? extras) =>
        extras?["pw_style"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && s == "painterly";

    /// <summary>The painterly material of an imported glTF material (its albedo colour and texture). A cut-out material
    /// (alphaMode MASK: the hair, GSF flag 0x1) keeps its alpha test, a double-sided one both sides.</summary>
    public static Material Make(StandardMaterial3D imported, Texture2D? map, string flags, string? model)
    {
        bool cut = imported.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor;
        bool twoSided = imported.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
        if (!s_shaders.TryGetValue((cut, twoSided), out var sh)) s_shaders[(cut, twoSided)] = sh = new Shader { Code = Code(cut, twoSided) };
        var m = new ShaderMaterial { Shader = sh, ResourceName = imported.ResourceName };
        m.SetShaderParameter("albedo", imported.AlbedoColor);
        m.SetShaderParameter("texture_albedo", map != null ? map : default(Variant));
        if (cut) m.SetShaderParameter("alpha_scissor_threshold", imported.AlphaScissorThreshold);
        ModelMaterials.SetTag(m, new MaterialTag { GsfFlags = flags, Map = map, Model = model });
        return m;
    }

    // (the code must start with "shader_type spatial;": UnitsBehind inserts its stencil line after it; "ALPHA =" only
    // with ALPHA_SCISSOR_THRESHOLD: UnitsBehind reads a lone "ALPHA =" as a blended material)
    static string Code(bool cut, bool twoSided) => @"shader_type spatial;
render_mode blend_mix, " + (twoSided ? "cull_disabled" : "cull_back") + @", depth_draw_opaque;
uniform float alpha_scissor_threshold = 0.5;
varying flat float mirrored;

uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D texture_albedo : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;

// the three light bands: the light level (N.L, -1..1, plus the brush noise) where shadow turns into mid tone and
// mid tone into full light, how soft the borders are, and how bright the mid tone is (0..1 of full light)
uniform float band_shadow = 0.12;
uniform float band_light = 0.50;
uniform float band_soft = 0.06;
uniform float mid_level = 0.58;
// painted shadow and light colours (multiplied with the light's colour)
uniform vec3 shadow_tint : source_color = vec3(0.26, 0.24, 0.44);
uniform vec3 light_tint : source_color = vec3(1.04, 1.00, 0.93);
// brush strokes on the band borders: strength (in N.L units) and size (strokes per texture width)
uniform float brush_strength = 0.20;
uniform float brush_scale = 10.0;
// rim light on the lit side, small painted highlight, darkened contour, saturation
uniform vec3 rim_color : source_color = vec3(1.0, 0.85, 0.65);
uniform float rim_strength = 0.45;
uniform float rim_width = 0.30;
uniform float highlight_strength = 0.12;
uniform float highlight_size = 0.06;
uniform float contour = 0.38;
uniform float saturation = 1.15;

varying float brush;

// (a hash without sin(): the sin() hash loses precision on GPUs and drew moire patterns on the HD heroes)
float pw_hash(vec2 p) { vec3 q = fract(vec3(p.xyx) * 0.1031); q += dot(q, q.yzx + 33.33); return fract((q.x + q.y) * q.z); }
float pw_noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(pw_hash(i), pw_hash(i + vec2(1.0, 0.0)), u.x), mix(pw_hash(i + vec2(0.0, 1.0)), pw_hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

void vertex() {
	mirrored = determinant(mat3(MODEL_MATRIX)) < 0.0 ? 1.0 : 0.0;
}

void fragment() {
	// (two-sided on a mirrored model: Godot turned the normal of the side it took for the back - as ModelMaterials.TwoSided)
	" + (twoSided ? "if (mirrored > 0.5) NORMAL = -NORMAL;" : "") + @"
	vec4 a = albedo * texture(texture_albedo, UV);
	vec3 c = a.rgb;
	float grey = dot(c, vec3(0.299, 0.587, 0.114));
	c = max(mix(vec3(grey), c, saturation), vec3(0.0));
	// contour: the surface turning away from the eye gets darker
	float edge = 1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0);
	c *= 1.0 - contour * smoothstep(0.55, 0.95, edge);
	ALBEDO = c;
	" + (cut ? "ALPHA = a.a;\n\tALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;" : "") + @"
	METALLIC = 0.0;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	// stretched strokes (long in u, short in v), two sizes
	vec2 q = UV * brush_scale;
	// strokes finer than a pixel fade out (else they alias into moire at a distance)
	float fine = 1.0 - smoothstep(0.25, 0.6, max(length(dFdx(q * vec2(2.3, 7.0))), length(dFdy(q * vec2(2.3, 7.0)))));
	float coarse = 1.0 - smoothstep(0.25, 0.6, max(length(dFdx(q * vec2(1.0, 3.0))), length(dFdy(q * vec2(1.0, 3.0)))));
	brush = coarse * (0.65 * (pw_noise(q * vec2(1.0, 3.0)) - 0.5) + 0.35 * fine * (pw_noise(q * vec2(2.3, 7.0) + 17.0) - 0.5));
}

void light() {
	float ndl = dot(NORMAL, LIGHT) + brush * brush_strength;
	// the shadow map darkens like the terminator (a hero in a building's shadow is in the shadow band)
	float shadowed = LIGHT_IS_DIRECTIONAL ? ATTENUATION : 1.0;
	float lit = smoothstep(band_shadow - band_soft, band_shadow + band_soft, ndl) * smoothstep(0.2, 0.8, shadowed);
	float full = smoothstep(band_light - band_soft, band_light + band_soft, ndl) * lit;
	vec3 tone = mix(shadow_tint, mix(vec3(mid_level), light_tint, full), lit);
	// a lamp's falloff with distance (directional lights have none)
	float reach = LIGHT_IS_DIRECTIONAL ? 1.0 : ATTENUATION;
	DIFFUSE_LIGHT += LIGHT_COLOR * tone * reach / PI;
	float rim = smoothstep(1.0 - rim_width, 1.0, 1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0)) * lit;
	vec3 h = normalize(LIGHT + VIEW);
	float hl = smoothstep(1.0 - highlight_size, 1.0 - highlight_size * 0.6, dot(NORMAL, h)) * full;
	SPECULAR_LIGHT += LIGHT_COLOR * reach / PI * (rim_color * rim * rim_strength + vec3(hl * highlight_strength));
}
";
}
