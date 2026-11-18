// Fog of war: what the local player sees (bright), has seen (dimmed to 55 %) and has never seen (black).
//
// The grid (FogGrid: seen now, seen once) is the simulation's - src/Sim/World/FogOfWar.cs: the world keeps the human
// player's fog and updates it in its steps, the systems ask it (docs/SAVES.md); the view draws it (FogOverlay.SetFog /
// SetFogValues, WorldView.SetFog).
//
// FogOverlay (Godot) draws it. The JS patched every material (applyFow: terrain, props, units, water, grass, sprites
// multiplied their colour by f(fog value) before the scene fog). Here one full-screen pass does it for everything
// drawn: it reads the depth buffer, rebuilds each pixel's world position, samples the fog texture there and
// multiplies the pixel by the same factor
//     f = smoothstep(0, 0.45, v) * mix(0.55, 1, smoothstep(0.45, 0.9, v))      v: 255 seen, 115 explored, 0 never
// and adds back the share of the scene fog that the JS applied after darkening (fog colour * fog amount * (1 - f)),
// so the result equals the JS's darken-then-fog. The sky (no depth) is left alone, as in the JS.
//
// Ported from remake/src/engine/terrain.js (FogOfWar, fowUniforms, applyFow) (pw-exporter 98c7703).
using System;
using Godot;
using PwRemake.Sim.World;

namespace PwRemake.View.Terrain;

/// <summary>Draws the fog of war over everything (a full-screen pass in the transparent queue, last).</summary>
public partial class FogOverlay : Node3D
{
    MeshInstance3D _mul = null!, _add = null!;
    ShaderMaterial _mulMat = null!, _addMat = null!;
    ImageTexture? _tex;
    Image? _img;
    byte[] _bytes = Array.Empty<byte>();
    int _w, _h;
    double _size;
    bool _on = true, _reveal;

    const string Common = @"
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform sampler2D fow_tex : filter_linear, repeat_disable;
uniform vec4 fow_scale;            // x0, z0, 1/size, 1/size
uniform vec3 fog_color : source_color;
uniform vec2 fog_range;            // the scene fog's start and end (m of view depth)
void vertex() { POSITION = vec4(VERTEX.xy * 2.0, 0.5, 1.0); }
// the fog value's factor (terrain.js applyFow) and the scene fog's amount at the pixel; false for the sky
bool fow_at(vec2 suv, mat4 inv_proj, mat4 inv_view, out float f, out float a) {
    float d = texture(depth_tex, suv).r;
    f = 1.0; a = 0.0;
    if (d <= 0.0) return false;                       // (reverse z: nothing drawn there)
    vec4 v = inv_proj * vec4(suv * 2.0 - 1.0, d, 1.0);
    v.xyz /= v.w;
    vec3 w = (inv_view * vec4(v.xyz, 1.0)).xyz;
    vec2 uv = (w.xz - fow_scale.xy) * fow_scale.zw;
    float fv = texture(fow_tex, clamp(uv, 0.0, 1.0)).r;
    if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) fv = 0.0;
    f = smoothstep(0.0, 0.45, fv) * mix(0.55, 1.0, smoothstep(0.45, 0.9, fv));
    a = clamp((-v.z - fog_range.x) / (fog_range.y - fog_range.x), 0.0, 1.0);
    return true;
}
";

    public static FogOverlay Create(double size)
    {
        var o = new FogOverlay { Name = "FogOfWar", _size = size };
        var quad = new QuadMesh { Size = new Vector2(1, 1) };
        o._mulMat = new ShaderMaterial { Shader = new Shader { Code = @"shader_type spatial;
render_mode unshaded, blend_mul, depth_test_disabled, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;
" + Common + @"
void fragment() {
    float f, a;
    fow_at(SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX, f, a);
    ALBEDO = vec3(f);
}" }, RenderPriority = 126 };
        o._addMat = new ShaderMaterial { Shader = new Shader { Code = @"shader_type spatial;
render_mode unshaded, blend_add, depth_test_disabled, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;
" + Common + @"
void fragment() {
    float f, a;
    fow_at(SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX, f, a);
    ALBEDO = fog_color * a * (1.0 - f);
}" }, RenderPriority = 127 };
        double half = size / 2;
        foreach (var m in new[] { o._mulMat, o._addMat }) m.SetShaderParameter("fow_scale", new Vector4((float)-half, (float)-half, (float)(1 / size), (float)(1 / size)));
        o._mul = new MeshInstance3D { Mesh = quad, MaterialOverride = o._mulMat, Name = "Darken", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16384 };
        o._add = new MeshInstance3D { Mesh = quad, MaterialOverride = o._addMat, Name = "FogBack", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16384 };
        o.AddChild(o._mul);
        o.AddChild(o._add);
        o.SetSceneFog(new Color(0x9f / 255f, 0xb8 / 255f, 0xc8 / 255f), 260, 620);
        o.SetFog(null, null, 1, 1);
        return o;
    }

    /// <summary>the scene fog (WorldRenderer) the darkening has to undo its share of</summary>
    public void SetSceneFog(Color color, double start, double end)
    {
        _addMat.SetShaderParameter("fog_color", color);
        _addMat.SetShaderParameter("fog_range", new Vector2((float)start, (float)end));
        _mulMat.SetShaderParameter("fog_range", new Vector2((float)start, (float)end));
    }

    /// <summary>
    /// The fog of war of the local player: per cell (row-major, w x h cells covering the world's square, row 0 at the
    /// north edge = -z) 1 if visible now, 1 if explored. Null arrays: everything visible.
    /// </summary>
    public void SetFog(byte[]? visible, byte[]? explored, int w, int h)
    {
        if (_bytes.Length != w * h) _bytes = new byte[w * h];
        if (visible == null) Array.Fill(_bytes, (byte)255);
        else for (int i = 0; i < _bytes.Length; i++) _bytes[i] = visible[i] != 0 ? (byte)255 : explored != null && explored[i] != 0 ? (byte)115 : (byte)0;
        SetFogValues(_bytes, w, h);
    }

    /// <summary>the fog texture's values directly (255 seen, 115 explored, 0 never: FogGrid.TextureBytes)</summary>
    public void SetFogValues(byte[] values, int w, int h)
    {
        if (_img == null || w != _w || h != _h)
        {
            _w = w; _h = h;
            _img = Image.CreateFromData(w, h, false, Image.Format.R8, values);
            _tex = ImageTexture.CreateFromImage(_img);
            _mulMat.SetShaderParameter("fow_tex", _tex);
            _addMat.SetShaderParameter("fow_tex", _tex);
        }
        else
        {
            _img.SetData(w, h, false, Image.Format.R8, values);
            _tex!.Update(_img);
        }
    }

    /// <summary>the setup's "Disable fog of war" / the debug reveal: nothing is darkened</summary>
    public bool RevealAll { get => _reveal; set { _reveal = value; Apply(); } }
    /// <summary>fowOn: the overlay drawn at all</summary>
    public bool On { get => _on; set { _on = value; Apply(); } }
    void Apply() { _mul.Visible = _add.Visible = _on && !_reveal; }
}
