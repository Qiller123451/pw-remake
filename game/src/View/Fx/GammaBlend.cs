// The original's additive light in the remake's framebuffer: shader code shared by the particles (ParticleBatch), the
// additive meshes (ModelMaterials GlowShader: GSF material flag 0x4) and the additive sprites of the models
// (ModelSprites glow).
//
// ParaWorld draws with Direct3D 9 into an 8-bit screen that holds sRGB values, with no sRGB conversion anywhere: a
// sprite is texture x vertex colour (texture stage MODULATE) and an additive one is added to the screen's sRGB value
// (PWClient.exe renderer 0x704d40: additive particles get alpha 0 and their colour, the rest colour x alpha - one blend
// for both, ONE / INVSRCALPHA, so alpha 0 adds). Godot's Forward+ and Mobile renderers blend in linear light (an HDR
// buffer encoded to sRGB at the end); the Compatibility renderer blends the sRGB values as the original does (measured
// 2026-11-18 on llvmpipe: an additive 0.4 over a 0.3 background gave 0.49 in Forward+ - linear - and the shader's
// output added raw in Compatibility). Light added in linear is much fainter on the screen than the same value added in
// sRGB: a heal ray's texel 0.6 (sRGB) over ground of 0.3 gives 0.9 in the original, 0.66 added in linear (and 0.43
// with the old particle shader, which also decoded the texture twice - the browser remake's look). Over darker ground
// this match is too bright (0.1 + 0.2: 0.43 for the original's 0.3), over brighter ground too faint (0.4 + 0.6: 0.93
// for 1.0) - the error of matching on one background.
//
// pw_gamma_add(s) returns what a blend_add shader writes so that the screen shows background + s: exactly in
// Compatibility, and in the linear renderers exactly over a background of GAMMA_ADD_BASE (the original's ground under
// the effects: the median of Kacper's reference shot of a Norse temple's healing area is 71 / 82 / 48 of 255, grey 67).
// pw_mix_out(s) is what a blend_mix / opaque shader writes for the sRGB colour s. The callers sample their textures
// without the source_color hint, so texture() gives the stored sRGB values in every renderer.
namespace PwRemake.View.Fx;

public static class GammaBlend
{
    /// <summary>the background (sRGB value) the linear renderers' additive light is matched on</summary>
    public const float GAMMA_ADD_BASE = 0.3f;

    /// <summary>GLSL: pw_srgb_lin (sRGB value to linear), pw_gamma_add (an sRGB colour to add -> what blend_add
    /// writes), pw_mix_out (an sRGB colour for blend_mix / opaque -> ALBEDO)</summary>
    public static readonly string Glsl = @"
vec3 pw_srgb_lin(vec3 c) {
    c = max(c, vec3(0.0));
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}
vec3 pw_gamma_add(vec3 s) {
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
    return s;
#else
    const vec3 B = vec3(" + GAMMA_ADD_BASE.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture) + @");
    return pw_srgb_lin(B + s) - pw_srgb_lin(B);
#endif
}
vec3 pw_mix_out(vec3 s) {
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
    return s;
#else
    return pw_srgb_lin(s);
#endif
}
";
}
