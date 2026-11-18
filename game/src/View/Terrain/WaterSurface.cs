// Sea / lake surface of the original maps: one large plane at the map's water level.
//   * colour from the water depth (terrain height baked into a small texture): clear and light at the shore,
//     dark further out, foam where the ground meets the surface
//   * moving ripples from the original WaterNormalMap (Texture/Scape/Settings/<setting>/WaterNormalMap.tga ->
//     assets/terrain/water_normal.png)
//   * sun glints (Blinn-Phong on the rippled normal) and a fresnel sky reflection
//   * fog of war like everything else (FogOverlay); not touched by the scene fog (the JS ShaderMaterial had fog off)
//
// Ported from remake/src/engine/water.js (pw-exporter 98c7703): GLSL -> Godot shading language.
using System;
using Godot;
using PwRemake.Sim.Maps;

namespace PwRemake.View.Terrain;

public partial class WaterSurface : MeshInstance3D
{
    const string Code = @"shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;
uniform float u_size;
uniform float u_has_normal;
uniform sampler2D u_depth : filter_linear, repeat_disable;
uniform sampler2D u_normal : filter_linear_mipmap, repeat_enable;
uniform vec3 u_sun;
uniform vec3 u_shallow : source_color;
uniform vec3 u_deep : source_color;
uniform vec3 u_sky : source_color;
varying vec3 v_w;
void vertex() {
    v_w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
void fragment() {
    float u_time = TIME;
    vec2 uv = v_w.xz / u_size + 0.5;
    float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
    float d = mix(1.0, texture(u_depth, clamp(uv, 0.0, 1.0)).r, inside);
    if (d < 0.003) discard;                                          // dry land
    vec3 nrm = vec3(0.0, 1.0, 0.0);
    if (u_has_normal > 0.5) {
        vec3 a = texture(u_normal, v_w.xz * 0.018 + vec2(u_time * 0.011, u_time * 0.007)).rgb * 2.0 - 1.0;
        vec3 b = texture(u_normal, v_w.xz * 0.031 - vec2(u_time * 0.009, -u_time * 0.012)).rgb * 2.0 - 1.0;
        vec3 t = normalize(a + b);
        nrm = normalize(vec3(t.x, t.z * 2.2, t.y));
    }
    vec3 V = normalize(CAMERA_POSITION_WORLD - v_w);
    float fres = pow(1.0 - max(dot(nrm, V), 0.0), 3.0);
    vec3 H = normalize(u_sun + V);
    float spec = pow(max(dot(nrm, H), 0.0), 140.0) * 1.4;
    float deep = smoothstep(0.0, 0.85, d);
    vec3 col = mix(u_shallow, u_deep, deep);
    col = mix(col, u_sky, fres * 0.55) + vec3(spec);
    float foam = (1.0 - smoothstep(0.004, 0.035, d)) * (0.55 + 0.45 * sin(u_time * 1.3 + v_w.x * 0.7 + v_w.z * 0.5));
    col = mix(col, vec3(0.92, 0.96, 0.95), foam * 0.6);
    float alpha = mix(0.35, 0.92, smoothstep(0.0, 0.35, d)) + fres * 0.08;
    ALBEDO = col;
    ALPHA = clamp(alpha, 0.0, 1.0);
}
";

    public ShaderMaterial Mat { get; private set; } = null!;
    ImageTexture _depthTex = null!;
    Image _depthImg = null!;
    HeightField _hf = null!;
    double _level;
    int _n;

    /// <summary>
    /// hf: the terrain's height field; level: the water height (m); normalMap: the setting's water normal map (null =
    /// flat); extent: the plane's side (the JS: 4x the map).
    /// </summary>
    public static WaterSurface Build(HeightField hf, double level, Texture2D? normalMap, double? extent = null,
        Color? shallow = null, Color? deep = null, Color? sky = null)
    {
        var w = new WaterSurface { Name = "Water", _hf = hf, _level = level };
        double size = hf.Size;
        // depth texture: 0 = dry land, 1 = 10 m deep or more
        w._n = Math.Min(1024, hf.N);
        w._depthImg = Image.CreateEmpty(w._n, w._n, false, Image.Format.R8);
        w._depthImg.SetData(w._n, w._n, false, Image.Format.R8, w.DepthBytes());
        w._depthTex = ImageTexture.CreateFromImage(w._depthImg);
        double ext = extent ?? size * 4;
        w.Mesh = new PlaneMesh { Size = new Vector2((float)ext, (float)ext) };
        var mat = new ShaderMaterial { Shader = new Shader { Code = Code }, RenderPriority = 2 };
        mat.SetShaderParameter("u_size", (float)size);
        mat.SetShaderParameter("u_depth", w._depthTex);
        if (normalMap != null) mat.SetShaderParameter("u_normal", normalMap);
        mat.SetShaderParameter("u_has_normal", normalMap != null ? 1f : 0f);
        mat.SetShaderParameter("u_sun", new Vector3(0.4f, 0.8f, 0.3f).Normalized());
        mat.SetShaderParameter("u_shallow", shallow ?? Hex(0x3f9a8c));
        mat.SetShaderParameter("u_deep", deep ?? Hex(0x0d3550));
        mat.SetShaderParameter("u_sky", sky ?? Hex(0x9cc4dc));
        w.Mat = mat;
        w.MaterialOverride = mat;
        w.CastShadow = ShadowCastingSetting.Off;
        w.Position = new Vector3(0, (float)level, 0);
        return w;
    }

    static Color Hex(int rgb) => Color.Color8((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    byte[] DepthBytes()
    {
        int n = _n;
        double size = _hf.Size;
        var data = new byte[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double x = ((double)i / (n - 1) - 0.5) * size, z = ((double)j / (n - 1) - 0.5) * size;
                data[j * n + i] = (byte)Math.Max(0, Math.Min(255, Math.Round((_level - _hf.At(x, z)) / 10 * 255 + 1, MidpointRounding.AwayFromZero)));
            }
        return data;
    }

    /// <summary>the ground under the water changed (a harbour levelled the shore): the depth texture again</summary>
    public void RefreshDepth()
    {
        _depthImg.SetData(_n, _n, false, Image.Format.R8, DepthBytes());
        _depthTex.Update(_depthImg);
    }
}
