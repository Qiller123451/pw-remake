// One draw call of the models' particles: every sprite of one sheet, atlas grid and blending, as the instances of a
// MultiMesh of one quad. The CPU (Particles) fills the instance buffer each frame; the vertex shader turns each quad
// to the camera (or lays it on the ground plane), sizes it, hangs it at its atlas cell's pivot, and picks its cell.
//
// Instance data (MultiMesh: 3D transform + colour + custom data, 20 floats; colour and custom data are half floats in
// the Compatibility renderer):
//   transform origin = the sprite's world position (the basis is the identity)
//   colour           = rgb (the particle's colour, faded) and alpha
//   custom           = (height in m, rotation, atlas cell + 256 x ground flag + 512 x green, pivot: x + 32 y, 0..31 each)
//                      (green: an infected model's emitter - fire and light turned green, smoke tinted: InfectedLook)
//
// Ported from remake/src/engine/particles.js (Batch, VERT, FRAG) (pw-exporter 98c7703).
using System;
using Godot;

namespace PwRemake.View.Fx;

public sealed partial class ParticleBatch : MultiMeshInstance3D
{
    public const int FLOATS = 20;
    /// <summary>sprites in one batch at most (particles.js MAX_PER_BATCH)</summary>
    public const int MAX = 4000;
    static Shader? s_shaderMix, s_shaderAdd;

    public readonly Texture2D Tex;
    public readonly int GridW, GridH;
    public readonly bool Additive;
    /// <summary>a cell's width to its height (the sprites' width to their height)</summary>
    public readonly float Aspect;
    public int N { get; private set; }
    /// <summary>sprites of the last flush (tests, diagnostics) and their data (FLOATS per sprite)</summary>
    public int LastN { get; private set; }
    public ReadOnlySpan<float> Data => _buf;
    float[] _buf = new float[64 * FLOATS];
    int _cap = 64;
    Vector3 _min, _max;
    readonly MultiMesh _mm;

    public ParticleBatch(Texture2D tex, int gw, int gh, bool additive)
    {
        Tex = tex; GridW = gw; GridH = gh; Additive = additive;
        Name = $"particles_{tex.ResourceName}_{gw}x{gh}{(additive ? "_add" : "")}";
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One }, InstanceCount = _cap, VisibleInstanceCount = 0,
        };
        Multimesh = _mm;
        CastShadow = ShadowCastingSetting.Off;
        // a cell's width to its height (rays.png: 16 x 2 cells of 16 x 128 px)
        float aspect = Aspect = tex.GetHeight() > 0 ? (tex.GetWidth() / (float)gw) / (tex.GetHeight() / (float)gh) : 1;
        var mat = new ShaderMaterial { Shader = additive ? (s_shaderAdd ??= Make(true)) : (s_shaderMix ??= Make(false)) };
        mat.SetShaderParameter("map", tex);
        mat.SetShaderParameter("grid", new Vector2(gw, gh));
        mat.SetShaderParameter("aspect", aspect);
        // (additive sprites after the others: three.js renderOrder 6 / 5)
        mat.RenderPriority = additive ? 2 : 1;
        MaterialOverride = mat;
        Visible = false;
    }

    static Shader Make(bool add) => new()
    {
        Code = @"shader_type spatial;
render_mode unshaded, " + (add ? "blend_add" : "blend_mix") + @", depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
// (no source_color: the sheet's sRGB values, multiplied by the colour as the original's texture stage did; the fog is
// the CPU's fade - 0x705191)
uniform sampler2D map : filter_linear_mipmap, repeat_disable;
uniform vec2 grid = vec2(1.0);
uniform float aspect = 1.0;
varying vec2 v_uv;
varying vec4 v_col;
varying flat float v_green;
void vertex() {
    // (the corner offsets in the world: the instance's basis is the identity, so they are the model's - Godot's
    // Compatibility renderer does not keep a view-space VERTEX of skip_vertex_transform for MultiMesh instances)
    float size = INSTANCE_CUSTOM.x, rot = INSTANCE_CUSTOM.y;
    float green = floor(INSTANCE_CUSTOM.z / 512.0), cz = INSTANCE_CUSTOM.z - green * 512.0;
    float ground = floor(cz / 256.0), tile = cz - ground * 256.0;
    v_green = green;
    float qy = floor(INSTANCE_CUSTOM.w / 32.0), qx = INSTANCE_CUSTOM.w - qy * 32.0;
    vec2 piv = vec2(qx, qy) / 15.5 - 1.0;          // -1 .. 1 from the cell's middle to its edge (y down)
    vec2 corner = vec2(UV.x - 0.5, 0.5 - UV.y);    // -0.5 .. 0.5, y up
    float s = sin(rot), k = cos(rot);
    if (ground > 0.5) {
        // flag 0x40000000: a square lying flat on the world's ground plane at the particle's height, turned by rot
        float side = size * 0.7071;
        vec2 g = vec2((corner.x - piv.x * 0.5) * side, (corner.y + piv.y * 0.5) * side);
        vec3 fwd = vec3(s, 0.0, k), rgt = vec3(k, 0.0, -s);
        VERTEX = rgt * g.x + fwd * g.y;
    } else {
        // (never larger than the largest share of the screen: Particles limits the size - PROJECTION_MATRIX is not
        // usable in this vertex shader in the Compatibility renderer, its [1][1] reads 0)
        float h = size, w = h * aspect;
        vec2 q = vec2((corner.x - piv.x * 0.5) * w, (corner.y + piv.y * 0.5) * h);
        vec2 r = vec2(k * q.x - s * q.y, s * q.x + k * q.y);
        VERTEX = INV_VIEW_MATRIX[0].xyz * r.x + INV_VIEW_MATRIX[1].xyz * r.y;
    }
    float tx = mod(tile, grid.x), ty = floor(tile / grid.x);
    v_uv = vec2((tx + corner.x + 0.5) / grid.x, (ty + 0.5 - corner.y) / grid.y);
    v_col = COLOR;
}
" + GammaBlend.Glsl + Materials.InfectedLook.GreenGlsl + @"
void fragment() {
    vec4 t = texture(map, v_uv);
" + (add
            // additive sprites (fire, sparks) are drawn on black and added by their colour: the texture's alpha is not
            // used (the flames of gore.tga have alpha 0)
            ? @"    if (max(t.r, max(t.g, t.b)) < 0.01) discard;
    float a = v_col.a;
"
            : @"    float a = t.a * v_col.a;
") + @"    if (a < 0.01) discard;
    // the original's sprite: texel x colour in sRGB values (D3D9, MODULATE), added to the screen's sRGB value or mixed
    // with it (GammaBlend). Until 2026-11-18 the port copied the browser remake here: three.js wrote the texture's
    // linear value unencoded, and the port decoded it once more (Forward+ decoding the texture as well): the heal
    // rays' brightest texel 0.6 over ground of 0.3 showed 0.43 in Forward+ (0.62 in Compatibility) where the original
    // shows 0.9 (Kacper 2026-11-18: 'less visible').
    vec3 c = t.rgb * v_col.rgb;
" + (add
            ? "    if (v_green > 0.5) c = pw_green(c);\n"
            // (smoke and dust of an infected model: a sickly green-grey)
            : "    if (v_green > 0.5) c = mix(c, dot(c, vec3(0.3333)) * vec3(0.6, 0.9, 0.4), 0.8);\n") + (add
            // (the alpha scales the light before it is added: blend_add would scale it after the conversion)
            ? @"    ALBEDO = pw_gamma_add(c * a);
    ALPHA = 1.0;
"
            : @"    ALBEDO = pw_mix_out(c);
    ALPHA = a;
") + @"}
",
    };

    /// <summary>a sprite: world position, height (m), alpha, colour, atlas cell, rotation, packed pivot, ground flag,
    /// green (an infected model's: InfectedLook.GreenGlsl)</summary>
    public bool Push(Vector3 w, float size, float a, float r, float g, float b, int tile, float rot, float pivot, bool ground, bool green = false)
    {
        if (N >= MAX) return false;
        if (N >= _cap)
        {
            _cap = Math.Min(MAX, _cap * 2);
            Array.Resize(ref _buf, _cap * FLOATS);
        }
        int o = N * FLOATS;
        var B = _buf;
        B[o] = 1; B[o + 1] = 0; B[o + 2] = 0; B[o + 3] = w.X;
        B[o + 4] = 0; B[o + 5] = 1; B[o + 6] = 0; B[o + 7] = w.Y;
        B[o + 8] = 0; B[o + 9] = 0; B[o + 10] = 1; B[o + 11] = w.Z;
        B[o + 12] = r; B[o + 13] = g; B[o + 14] = b; B[o + 15] = a;
        B[o + 16] = size; B[o + 17] = rot; B[o + 18] = tile + (ground ? 256 : 0) + (green ? 512 : 0); B[o + 19] = pivot;
        if (N == 0) { _min = w; _max = w; }
        else { _min = _min.Min(w); _max = _max.Max(w); }
        N++;
        return true;
    }

    /// <summary>pivot x / y (-1 .. 1 as particles.js: (mid - 127) / 127) packed into one float: 32 steps each (the
    /// Compatibility renderer keeps custom data as half floats - whole numbers below 2048 are exact)</summary>
    public static float PackPivot(float px, float py)
    {
        int ix = Math.Clamp((int)Math.Round((px + 1) * 15.5f), 0, 31), iy = Math.Clamp((int)Math.Round((py + 1) * 15.5f), 0, 31);
        return ix + 32 * iy;
    }

    /// <summary>the frame's sprites to the graphics card (and the batch hidden when it has none)</summary>
    public void Flush(float maxSize)
    {
        LastN = N;
        if (N == 0)
        {
            if (Visible) { Visible = false; _mm.VisibleInstanceCount = 0; }
            return;
        }
        if (_mm.InstanceCount != _cap) _mm.InstanceCount = _cap;
        _mm.Buffer = _buf;
        _mm.VisibleInstanceCount = N;
        var pad = Vector3.One * (maxSize + 1);
        _mm.CustomAabb = new Aabb(_min - pad, _max - _min + pad * 2);
        Visible = true;
        N = 0;
    }

}
