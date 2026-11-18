// The browser remake's own drawn effects (engine/fx.js FX): soft round sprites made in code - glow, fire, smoke,
// dust, blood, sparks - and scorch marks on the ground. Here only a fallback for data without the game's effect
// models (WorldFx / GameFx: a hit, death, level-up, blast, quicksand / tornado or fireworks model missing from the
// converted data); everything the original game shows with a model of its own - the destruction (the ruins' clip
// "destroy"), damaged buildings (their damage-stage emitters), the burning resin field, the rocket smoke, the
// fireworks, the tornado and quicksand, the spirits - comes from the game's models (docs/EFFECTS.md).
//
// Godot: the sprites live in a fixed pool (MAX, the oldest skipped when full) and are drawn as one MultiMesh per
// texture (camera-facing quads; glow, fire and sparks added); the scorch marks are a MultiMesh of ground quads (40 at
// most, the oldest replaced; they fade after 25 s).
//
// Ported from remake/src/engine/fx.js (FX: radialTexture, spawn, explosion, muzzle, trail, blood, dust, decal, update)
// (pw-exporter 98c7703). The synthesized sounds of fx.js (Sound) are not ported: the game's own sounds play
// (View/Audio).
using System;
using System.Collections.Generic;
using Godot;

namespace PwRemake.View.Fx;

public partial class DrawnFx : Node3D
{
    public const int MAX = 1500, DECALS = 40;
    public enum Kind { Glow, Fire, Smoke, Dust, Blood, Spark }
    static readonly int KINDS = Enum.GetValues<Kind>().Length;

    struct P
    {
        public Kind K;
        public Vector3 Pos, Vel;
        public float Age, Life, Size0, Size1, Grav, Drag, Alpha, Spin, Rot;
        public Color Color;
    }

    readonly P[] _p = new P[MAX];
    int _n;
    readonly MultiMeshInstance3D[] _mm = new MultiMeshInstance3D[KINDS];
    readonly float[][] _buf = new float[KINDS][];
    readonly int[] _cnt = new int[KINDS];
    readonly Random _rnd = new();
    readonly Func<double, double, double> _height;
    // scorch marks
    readonly (Vector3 Pos, float Size, float Rot, double Age)[] _dec = new (Vector3, float, float, double)[DECALS];
    int _decN, _decNext;
    MultiMeshInstance3D _decMesh = null!;
    float[] _decBuf = new float[DECALS * 16];
    /// <summary>off: nothing new is spawned (the "Particles" setting)</summary>
    public bool Enabled = true;
    public int Live => _n;
    public int Decals => _decN;
    /// <summary>sprites spawned so far (the test scene's checks)</summary>
    public int Spawned { get; private set; }

    static readonly (float, Color)[][] STOPS =
    {
        new[] { (0f, C(255, 255, 230, 1)), (0.25f, C(255, 210, 120, 0.9f)), (0.6f, C(255, 120, 30, 0.35f)), (1f, C(255, 80, 0, 0)) },     // glow
        new[] { (0f, C(255, 230, 160, 1)), (0.4f, C(255, 140, 40, 0.8f)), (1f, C(160, 40, 0, 0)) },                                      // fire
        new[] { (0f, C(90, 85, 78, 0.85f)), (0.5f, C(110, 104, 96, 0.5f)), (1f, C(120, 115, 105, 0)) },                                  // smoke
        new[] { (0f, C(150, 130, 95, 0.7f)), (0.6f, C(150, 130, 95, 0.3f)), (1f, C(150, 130, 95, 0)) },                                  // dust
        new[] { (0f, C(150, 10, 10, 1)), (0.6f, C(110, 0, 0, 0.8f)), (1f, C(80, 0, 0, 0)) },                                              // blood
        new[] { (0f, C(255, 255, 255, 1)), (0.3f, C(255, 220, 120, 1)), (1f, C(255, 150, 0, 0)) },                                        // spark
    };
    static readonly (int Size, float Noise)[] TEX = { (64, 0), (64, 0.35f), (64, 0.5f), (64, 0.5f), (64, 0), (32, 0) };
    static Color C(int r, int g, int b, float a) => new(r / 255f, g / 255f, b / 255f, a);

    public DrawnFx(Func<double, double, double> height)
    {
        _height = height;
        Name = "DrawnFx";
        var rnd = new Random(11);
        for (int k = 0; k < KINDS; k++)
        {
            bool add = k == (int)Kind.Glow || k == (int)Kind.Fire || k == (int)Kind.Spark;
            var tex = Radial(STOPS[k], TEX[k].Size, TEX[k].Noise, rnd);
            var mat = new ShaderMaterial { Shader = add ? (s_add ??= SpriteShader(true)) : (s_mix ??= SpriteShader(false)), RenderPriority = add ? 5 : 4 };
            mat.SetShaderParameter("map", tex);
            _buf[k] = new float[MAX * 20];
            _mm[k] = new MultiMeshInstance3D
            {
                Name = ((Kind)k).ToString(), TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = mat, Visible = false,
                Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = new QuadMesh { Size = Vector2.One }, InstanceCount = MAX, VisibleInstanceCount = 0 },
            };
            AddChild(_mm[k]);
        }
        var scorch = Radial(new[] { (0f, C(20, 14, 8, 0.85f)), (0.5f, C(30, 22, 12, 0.55f)), (1f, C(30, 22, 12, 0)) }, 128, 0.3f, rnd);
        var dm = new ShaderMaterial { Shader = s_decal ??= DecalShader(), RenderPriority = 3 };
        dm.SetShaderParameter("map", scorch);
        _decMesh = new MultiMeshInstance3D
        {
            Name = "Scorch", TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = dm, Visible = false,
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = new PlaneMesh { Size = Vector2.One }, InstanceCount = DECALS, VisibleInstanceCount = 0 },
        };
        AddChild(_decMesh);
    }

    static Shader? s_add, s_mix, s_decal;

    static Shader SpriteShader(bool add) => new()
    {
        Code = @"shader_type spatial;
render_mode unshaded, " + (add ? "blend_add" : "blend_mix") + @", depth_draw_never, cull_disabled, shadows_disabled;
uniform sampler2D map : source_color, filter_linear, repeat_disable;
varying vec4 v_col;
void vertex() {
    // (camera-facing: the corner offsets along the view's axes in the world - the instance's basis is the identity)
    float size = INSTANCE_CUSTOM.x, rot = INSTANCE_CUSTOM.y;
    vec2 q = vec2(UV.x - 0.5, 0.5 - UV.y) * size;
    float s = sin(rot), k = cos(rot);
    vec2 r = vec2(k * q.x - s * q.y, s * q.x + k * q.y);
    VERTEX = INV_VIEW_MATRIX[0].xyz * r.x + INV_VIEW_MATRIX[1].xyz * r.y;
    v_col = COLOR;
}
void fragment() { vec4 t = texture(map, UV); ALBEDO = t.rgb * v_col.rgb; ALPHA = t.a * v_col.a; }
",
    };

    static Shader DecalShader() => new()
    {
        Code = @"shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
uniform sampler2D map : source_color, filter_linear, repeat_disable;
varying float v_a;
void vertex() { v_a = COLOR.a; }
void fragment() { vec4 t = texture(map, UV); ALBEDO = t.rgb; ALPHA = t.a * v_a; }
",
    };

    // fx.js radialTexture: a radial gradient of colour stops, the alpha roughened by noise
    static ImageTexture Radial((float o, Color c)[] stops, int size, float noise, Random rnd)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float h = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - h) * (x + 0.5f - h) + (y + 0.5f - h) * (y + 0.5f - h)) / h;
                Color c = stops[^1].c;
                if (d <= stops[0].o) c = stops[0].c;
                else
                    for (int i = 1; i < stops.Length; i++)
                        if (d <= stops[i].o) { var (o0, c0) = stops[i - 1]; var (o1, c1) = stops[i]; c = c0.Lerp(c1, (d - o0) / Math.Max(1e-5f, o1 - o0)); break; }
                if (noise > 0) c.A *= 1 - noise + noise * (float)rnd.NextDouble();
                img.SetPixel(x, y, c);
            }
        return ImageTexture.CreateFromImage(img);
    }

    float R() => (float)_rnd.NextDouble();
    /// <summary>fx.js rv: a random vector in a cube of side `scale`</summary>
    public Vector3 Rv(float scale) => new((R() - 0.5f) * scale, (R() - 0.5f) * scale, (R() - 0.5f) * scale);

    /// <summary>one sprite (fx.js spawn): size grows from `size` to `size1` over its life, fading out</summary>
    public void Spawn(Kind k, Vector3 pos, float size = 1, float? size1 = null, float life = 1, Vector3? vel = null, float grav = 0,
        float drag = 1.5f, float alpha = 1, float spin = 0, Color? color = null)
    {
        if (!Enabled) return;
        if (_n >= MAX) return;
        _p[_n++] = new P
        {
            K = k, Pos = pos, Vel = vel ?? Vector3.Zero, Age = 0, Life = life, Size0 = size, Size1 = size1 ?? size * 2,
            Grav = grav, Drag = drag, Alpha = alpha, Spin = (R() - 0.5f) * spin, Rot = R() * Mathf.Tau, Color = color ?? Colors.White,
        };
        Spawned++;
    }

    public static Color Hex(int c) => new(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);

    public void Explosion(Vector3 pos, float big = 1)
    {
        var up = new Vector3(0, 1, 0);
        Spawn(Kind.Glow, pos + up, 3 * big, 9 * big, 0.28f);
        for (int i = 0; i < 9; i++)
            Spawn(Kind.Fire, pos + Rv(2 * big) + up, 2 * big, 4.5f * big, 0.45f + R() * 0.35f, Rv(8 * big) + new Vector3(0, 5 * big, 0), drag: 3, spin: 3);
        for (int i = 0; i < 10; i++)
            Spawn(Kind.Smoke, pos + Rv(2.5f * big) + new Vector3(0, 1.5f, 0), 2.5f * big, 8 * big, 2 + R() * 1.5f, Rv(5 * big) + new Vector3(0, 3 + R() * 3, 0), drag: 1.2f, alpha: 0.8f, spin: 1);
        for (int i = 0; i < 14; i++)
            Spawn(Kind.Spark, pos + new Vector3(0, 0.5f, 0), 0.35f, 0.1f, 0.6f + R() * 0.5f, Rv(22) + new Vector3(0, 12, 0), grav: 30, drag: 0.5f);
        for (int i = 0; i < 8; i++)
        {
            var p = pos + Rv(3); p.Y = pos.Y + 0.3f;
            var v = Rv(10); v.Y = 1;
            Spawn(Kind.Dust, p, 2, 7, 1.6f, v, drag: 2, alpha: 0.7f);
        }
        Decal(pos, 5 * big);
    }

    public void Muzzle(Vector3 pos, Vector3? dir = null)
    {
        var d = dir ?? new Vector3(0, 0.3f, 0);
        Spawn(Kind.Glow, pos, 1.2f, 2.4f, 0.1f);
        for (int i = 0; i < 2; i++) Spawn(Kind.Smoke, pos, 0.8f, 3.5f, 1.2f, d * (6 + R() * 4) + Rv(2), drag: 2.5f, alpha: 0.6f);
    }

    public void Trail(Vector3 pos) => Spawn(Kind.Smoke, pos, 0.35f, 1.4f, 0.7f, Rv(0.6f), drag: 1, alpha: 0.45f);

    public void Blood(Vector3 pos, int n = 10)
    {
        for (int i = 0; i < n; i++)
            Spawn(Kind.Blood, pos + Rv(1), 0.5f, 0.9f, 0.7f + R() * 0.4f, Rv(8) + new Vector3(0, 4, 0), grav: 18, drag: 0.8f);
    }

    public void Dust(Vector3 pos, float size = 2)
    {
        for (int i = 0; i < 5; i++)
        {
            var p = pos + Rv(size); p.Y = pos.Y + 0.3f;
            var v = Rv(3); v.Y = 0.8f;
            Spawn(Kind.Dust, p, size, size * 3, 1.8f, v, drag: 2, alpha: 0.6f);
        }
    }

    public void Decal(Vector3 pos, float size)
    {
        if (!Enabled) return;
        int i;
        if (_decN < DECALS) i = _decN++;
        else { i = _decNext; _decNext = (_decNext + 1) % DECALS; }
        _dec[i] = (new Vector3(pos.X, (float)_height(pos.X, pos.Z) + 0.08f, pos.Z), size, R() * 6.28f, 0);
    }

    /// <summary>one frame: the sprites move, grow and fade; the scorch marks fade after 25 s</summary>
    public void Update(double dtD)
    {
        float dt = (float)dtD;
        Array.Clear(_cnt);
        for (int i = _n - 1; i >= 0; i--)
        {
            ref var p = ref _p[i];
            p.Age += dt;
            float t = p.Age / p.Life;
            if (t >= 1) { _p[i] = _p[--_n]; continue; }
            p.Vel.Y -= p.Grav * dt;
            p.Vel *= Math.Max(0, 1 - p.Drag * dt);
            p.Pos += p.Vel * dt;
            p.Rot += p.Spin * dt;
        }
        for (int i = 0; i < _n; i++)
        {
            ref var p = ref _p[i];
            float t = p.Age / p.Life;
            float size = p.Size0 + (p.Size1 - p.Size0) * Mathf.Sqrt(t);
            float op = p.Alpha * (1 - t) * Math.Min(1, t * 12 + 0.2f);
            int k = (int)p.K, o = _cnt[k]++ * 20;
            var B = _buf[k];
            B[o] = 1; B[o + 1] = 0; B[o + 2] = 0; B[o + 3] = p.Pos.X;
            B[o + 4] = 0; B[o + 5] = 1; B[o + 6] = 0; B[o + 7] = p.Pos.Y;
            B[o + 8] = 0; B[o + 9] = 0; B[o + 10] = 1; B[o + 11] = p.Pos.Z;
            B[o + 12] = p.Color.R; B[o + 13] = p.Color.G; B[o + 14] = p.Color.B; B[o + 15] = op;
            B[o + 16] = size; B[o + 17] = p.Rot; B[o + 18] = 0; B[o + 19] = 0;
        }
        for (int k = 0; k < KINDS; k++)
        {
            var mmi = _mm[k];
            int n = _cnt[k];
            if (n == 0) { if (mmi.Visible) { mmi.Visible = false; mmi.Multimesh.VisibleInstanceCount = 0; } continue; }
            mmi.Multimesh.Buffer = _buf[k];
            mmi.Multimesh.VisibleInstanceCount = n;
            mmi.Multimesh.CustomAabb = new Aabb(new Vector3(-5000, -500, -5000), new Vector3(10000, 2000, 10000));
            mmi.Visible = true;
        }
        // scorch marks
        if (_decN == 0) return;
        var D = _decBuf;
        for (int i = 0; i < _decN; i++)
        {
            ref var d = ref _dec[i];
            d.Age += dt;
            float op = d.Age > 25 ? Math.Max(0, 1 - (float)(d.Age - 25) / 10) : 1;
            var b = new Basis(Vector3.Up, d.Rot).Scaled(new Vector3(d.Size, 1, d.Size));
            int o = i * 16;
            D[o] = b.X.X; D[o + 1] = b.Y.X; D[o + 2] = b.Z.X; D[o + 3] = d.Pos.X;
            D[o + 4] = b.X.Y; D[o + 5] = b.Y.Y; D[o + 6] = b.Z.Y; D[o + 7] = d.Pos.Y;
            D[o + 8] = b.X.Z; D[o + 9] = b.Y.Z; D[o + 10] = b.Z.Z; D[o + 11] = d.Pos.Z;
            D[o + 12] = 1; D[o + 13] = 1; D[o + 14] = 1; D[o + 15] = op;
        }
        _decMesh.Multimesh.Buffer = _decBuf;
        _decMesh.Multimesh.VisibleInstanceCount = _decN;
        _decMesh.Multimesh.CustomAabb = new Aabb(new Vector3(-5000, -500, -5000), new Vector3(10000, 2000, 10000));
        _decMesh.Visible = true;
    }

    public void Clear() { _n = 0; _decN = 0; _decNext = 0; Update(0); _decMesh.Visible = false; }
}
