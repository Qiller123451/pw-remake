// The fog of war as the original draws it: drifting cloud wisps over the ground the player does not see now.
//
// The original's default fog of war renderer (Rendering/FOW/Type 0 -> Pest::CFOWRenderer2; PWClient.exe, read for
// the remake 2026-11-21, docs/TERRAIN.md "Fog of war: how the original draws it") draws nothing on the ground itself:
//   * a fog map of 16 m cells (2 bits: 0 never seen, 1 explored, 3 seen now - 0x6fa630 / 0x6fa7a0) and from it a
//     byte per cell "seen now", eased every 0.04 s by +6 while the cell is seen and -2 while not (0x44ec08 ->
//     0x6fa7f0: from nothing to fully seen in 1.7 s, back in 5.1 s);
//   * a corner of a cell clears the clouds by s = max(0, (byte - 128) / 128); a sprite every 4 m (4 x 4 per cell,
//     0x71b9xx) gets the alpha (1 - bilinear(s)) x fade, fade = clamp((0.75 - (d - 10) / R) x 6, 0, 1) with d the
//     distance from the camera and R the terrain renderer's range (0x71bb03); sprites under 0.05 are left out;
//   * the sprite: camera-facing, 6 m to each side along the camera's right and 8 m up along its up from a point on
//     the ground (0x71a850 ff.), a cell of the 4 x 4 atlas (a random byte per sprite: bits 0-1 the column, 2-3 the
//     row, 0-5 one of 64 drift paths), drifting on two sines of +-2 m each plus a common +-1 m (0x71a431 ff.), its
//     colour the sky manager's (0x727e00: row 8 of the setting's sky gradient at the time of day; row 0 with fewer
//     rows; 0x607090 without a table) x (205 + 50 x (0.5 + 0.25 x (two sines of place and time))) / 255, its alpha
//     the colour's alpha x the sprite's x 150 / 255 (0x71a79e);
//   * the texture: the setting's Texture/Scape/<Setting>/fog.tga, else Effects/fogofwar.tga (0x6fc432, 0x71ab49) -
//     tools/prepare/fog_textures.py makes them assets/terrain/<Setting>/fog.png and assets/terrain/fogofwar.png.
// Here: one MultiMesh of sprites on the 4 m lattice round the camera, the alpha and the drift in the vertex shader
// from a texture of the eased bytes; the instances are laid out again only when the camera has moved a cell.
using System;
using Godot;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.Sim.World;

namespace PwRemake.View.Terrain;

/// <summary>The original's fog of war clouds (CFOWRenderer2) over what the local player does not see now.</summary>
public partial class FogClouds : Node3D
{
    static readonly Logger L = Log.For("View.FogClouds");
    /// <summary>the fog map's cell (m) and the sprites' spacing (m) - the original's</summary>
    public const double CELL = 16, STEP = 4;
    /// <summary>the eased byte per 0.04 s tick: up while seen, down while not (0x44ec08)</summary>
    const double TICK = 0.04, UP = 6, DOWN = 2;

    MultiMeshInstance3D _mmi = null!;
    MultiMesh _mm = null!;
    ShaderMaterial _mat = null!;
    HeightField _ground = null!;
    double? _water;
    double _size;
    int _n;                                   // cells per side
    byte[] _bytes = Array.Empty<byte>();
    float[] _eased = Array.Empty<float>();
    byte[] _target = Array.Empty<byte>();
    Image? _img;
    ImageTexture? _tex;
    double _tickLeft;
    bool _all = true;                         // everything seen (no fog of war, or the debug reveal)
    (int X0, int Z0, int X1, int Z1) _laid = (-1, -1, -1, -1);
    float[] _buf = Array.Empty<float>();
    /// <summary>the terrain renderer's range R of the distance fade (the original copies its scape renderer's each frame)</summary>
    public double Range = 800;
    /// <summary>a cutscene plays: no clouds (docs/GAME_SESSION.md "No fog of war in a cutscene")</summary>
    public bool Suspended { get; set; }
    /// <summary>the texture's source (assets/terrain/...), for the log and the docs</summary>
    public string Source { get; private set; } = "";

    const string SHADER = @"shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, skip_vertex_transform;
uniform sampler2D cloud_tex : filter_linear_mipmap, repeat_disable;
uniform sampler2D seen_tex : filter_nearest, repeat_disable;   // the eased byte per 16 m cell
uniform vec3 grid;            // x0, z0 (the map's corner), cells per side
uniform vec4 tint;            // the sky manager's colour (rgb) and its alpha
uniform float atlas = 4.0;
uniform vec2 eye;             // the camera's x, z
uniform float range = 620.0;
varying float v_alpha;
varying float v_bright;
varying vec2 v_uv;

float corner(ivec2 c) {
    int n = int(grid.z);
    if (c.x < 0 || c.y < 0 || c.x >= n || c.y >= n) return 0.0;
    float b = texelFetch(seen_tex, c, 0).r * 255.0;
    return max(0.0, (b - 128.0) / 128.0);
}

void vertex() {
    vec3 base = MODEL_MATRIX[3].xyz;
    float flags = INSTANCE_CUSTOM.x;                    // the random byte
    // what the cell's corners clear (bilinear over the cell: the sprite's place in it)
    vec2 p = (base.xz - grid.xy) / 16.0;
    ivec2 c0 = ivec2(floor(p));
    vec2 f = p - floor(p);
    float s = mix(mix(corner(c0), corner(c0 + ivec2(1, 0)), f.x), mix(corner(c0 + ivec2(0, 1)), corner(c0 + ivec2(1, 1)), f.x), f.y);
    // the distance fade round the camera (the cell's corner, as the original)
    float d = distance(vec2(c0) * 16.0 + grid.xy, eye);
    float fade = clamp((0.75 - (d - 10.0) / range) * 6.0, 0.0, 1.0);
    float a = (1.0 - s) * fade * INSTANCE_CUSTOM.w;
    if (fade < 0.05 || a < 0.05) { VERTEX = vec3(0.0, 0.0, 10.0); v_alpha = 0.0; v_bright = 0.0; v_uv = vec2(0.0); }
    else {
        // the drift (64 paths: 0x71a431 ff., t = time x 0.02)
        float t = TIME * 0.02;
        float i = mod(flags, 64.0);
        float ka = (i * 1.141 + 105.0) * 0.05, kb = (i * 0.131 + (70.0 - i) + 154.0) * 0.05;
        vec2 wob = vec2(2.0 * sin(ka * t) + sin(t * 7.234), 2.0 * sin(kb * t) + sin(t * 7.723));
        vec3 c = base + vec3(wob.x, 0.0, wob.y);
        // camera-facing: 6 m to each side, 8 m up (the camera's right and up)
        vec3 right = normalize(INV_VIEW_MATRIX[0].xyz), up = normalize(INV_VIEW_MATRIX[1].xyz);
        vec3 w = c + right * (VERTEX.x * 12.0) + up * ((VERTEX.y + 0.5) * 8.0);
        VERTEX = (VIEW_MATRIX * vec4(w, 1.0)).xyz;
        // the brightness: 205 + 50 x (0.5 + 0.25 x (two sines of the place and the time)), / 255
        float su = sin((c.x + t * 1.79) * 0.07 + (c.z + t * 2.1) * 0.13 + t * 1.89);
        float sv = sin((c.x + t * 1.7) * 0.11 + (c.z + t * 1.9) * 0.095 + t * 2.1);
        v_bright = (205.0 + 50.0 * (0.5 + 0.25 * (su + sv))) / 255.0;
        v_alpha = a;
        float col = mod(mod(flags, 4.0), atlas), row = mod(mod(floor(flags / 4.0), 4.0), atlas);
        v_uv = (vec2(col, row) + UV) / atlas;
    }
}

void fragment() {
    vec4 tx = texture(cloud_tex, v_uv);
    ALBEDO = tint.rgb * tx.rgb * v_bright;
    ALPHA = tx.a * v_alpha * tint.a * (150.0 / 255.0);
}
";

    /// <summary>the clouds of a map's setting; null without any cloud texture in the data (the game then draws the old
    /// darkening pass: FogOverlay)</summary>
    public static FogClouds? Create(MapSource S, HeightField ground, Func<string, string?> find)
    {
        int atlas = 4;
        string? path = find($"assets/terrain/{S.Setting}/fog.png"), src = $"assets/terrain/{S.Setting}/fog.png";
        if (path == null) { path = find("assets/terrain/fogofwar.png"); src = "assets/terrain/fogofwar.png"; }
        if (path == null) { path = find("assets/tex/particle_06.png"); src = "assets/tex/particle_06.png (not the original's fog texture: run Prepare)"; atlas = 2; }
        if (path == null) { L.Warn("no fog of war cloud texture (assets/terrain/<Setting>/fog.png: Prepare) - the darkening pass instead"); return null; }
        var img = Image.LoadFromFile(path);
        if (img == null || img.IsEmpty()) return null;
        img.GenerateMipmaps();
        var c = new FogClouds { Name = "FogClouds", _ground = ground, _water = S.Water, _size = S.Size, Source = src };
        c._n = (int)Math.Ceiling(S.Size / CELL) + 1;
        int nn = c._n * c._n;
        c._bytes = new byte[nn]; c._eased = new float[nn]; c._target = new byte[nn];
        Array.Fill(c._eased, 255f); Array.Fill(c._bytes, (byte)255); Array.Fill(c._target, (byte)255);
        c._img = Image.CreateFromData(c._n, c._n, false, Image.Format.R8, c._bytes);
        c._tex = ImageTexture.CreateFromImage(c._img);
        // (after the water: a transparent plane drawn later would cover the clouds over the sea)
        c._mat = new ShaderMaterial { Shader = new Shader { Code = SHADER }, RenderPriority = 2 };
        c._mat.SetShaderParameter("cloud_tex", ImageTexture.CreateFromImage(img));
        c._mat.SetShaderParameter("seen_tex", c._tex);
        c._mat.SetShaderParameter("atlas", (float)atlas);
        c._mat.SetShaderParameter("grid", new Vector3((float)(-S.Size / 2), (float)(-S.Size / 2), c._n));
        c.SetTint(new Color(0x60 / 255f, 0x70 / 255f, 0x90 / 255f));
        c._mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = new QuadMesh { Size = Vector2.One } };
        c._mmi = new MultiMeshInstance3D { Multimesh = c._mm, MaterialOverride = c._mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Sprites" };
        c.AddChild(c._mmi);
        L.Info($"fog of war clouds: {src}");
        return c;
    }

    /// <summary>the sprites' colour: the sky manager's (row 8 of the setting's sky gradient at the time of day)</summary>
    public void SetTint(Color c) => _mat.SetShaderParameter("tint", new Vector4(c.R, c.G, c.B, c.A));

    /// <summary>the local player's fog: a 16 m cell is seen when any of the grid's cells in it is seen now; RevealAll
    /// (no fog of war, the debug reveal): no clouds</summary>
    public void SetFog(FogGrid g)
    {
        _all = g.RevealAll;
        int k = (int)Math.Round(CELL / g.Cell);
        Array.Clear(_target);
        for (int z = 0; z < g.N; z++)
            for (int x = 0; x < g.N; x++)
                if (g.Vis[z * g.N + x] == 1)
                {
                    int cx = x / k, cz = z / k;
                    if (cx < _n && cz < _n) _target[cz * _n + cx] = 1;
                }
    }

    /// <summary>a frame: ease the bytes (0.04 s ticks), lay the sprites out round the camera when it moved a cell</summary>
    public void Step(double dt, Camera3D cam)
    {
        bool on = !_all && !Suspended;
        _mmi.Visible = on;
        if (!on) return;
        _tickLeft += dt;
        int ticks = (int)Math.Floor(_tickLeft / TICK);
        if (ticks > 0)
        {
            _tickLeft -= ticks * TICK;
            bool changed = false;
            for (int i = 0; i < _eased.Length; i++)
            {
                float e = _eased[i];
                float to = _target[i] == 1 ? Math.Min(255f, e + (float)(UP * ticks)) : Math.Max(0f, e - (float)(DOWN * ticks));
                if (to == e) continue;
                _eased[i] = to;
                byte b = (byte)to;
                if (b != _bytes[i]) { _bytes[i] = b; changed = true; }
            }
            if (changed) { _img!.SetData(_n, _n, false, Image.Format.R8, _bytes); _tex!.Update(_img); }
        }
        var eye = cam.GlobalPosition;
        _mat.SetShaderParameter("eye", new Vector2(eye.X, eye.Z));
        _mat.SetShaderParameter("range", (float)Range);
        Layout(cam);
    }

    /// <summary>the sprites on the 4 m lattice of the cells the camera sees (the ground under the screen's corners, at
    /// most 0.75 R away: the fade's end - the original takes the terrain patches in view), aligned to the cells</summary>
    void Layout(Camera3D cam)
    {
        double half = _size / 2, reach = 0.75 * Range;
        var eye = cam.GlobalPosition;
        var vp = cam.GetViewport().GetVisibleRect().Size;
        float gy = (float)_ground.At(eye.X, eye.Z) - 40;          // (a plane below: valleys and the sea further out)
        double minX = eye.X, maxX = eye.X, minZ = eye.Z, maxZ = eye.Z;
        foreach (var sp in new[] { Vector2.Zero, new Vector2(vp.X, 0), new Vector2(0, vp.Y), vp, new Vector2(vp.X / 2, 0) })
        {
            var ro = cam.ProjectRayOrigin(sp); var d = cam.ProjectRayNormal(sp);
            Vector3 hit;
            if (d.Y < -1e-3f && (hit = ro + d * ((gy - ro.Y) / d.Y)).DistanceTo(eye) < reach + 50) { }
            else { var hd = new Vector3(d.X, 0, d.Z); hit = eye + (hd.LengthSquared() > 1e-6f ? hd.Normalized() : Vector3.Forward) * (float)reach; }
            minX = Math.Min(minX, hit.X); maxX = Math.Max(maxX, hit.X); minZ = Math.Min(minZ, hit.Z); maxZ = Math.Max(maxZ, hit.Z);
        }
        // (3 cells more each way: the ground below the camera's plane and the sprites' 12 x 8 m reach beyond their cell)
        int x0 = Math.Max(0, (int)Math.Floor((minX + half) / CELL) - 3), x1 = Math.Min(_n - 1, (int)Math.Floor((maxX + half) / CELL) + 3);
        int z0 = Math.Max(0, (int)Math.Floor((minZ + half) / CELL) - 3), z1 = Math.Min(_n - 1, (int)Math.Floor((maxZ + half) / CELL) + 3);
        if (_laid == (x0, z0, x1, z1)) return;
        _laid = (x0, z0, x1, z1);
        int per = (int)(CELL / STEP);
        int count = Math.Max(0, (x1 - x0 + 1) * (z1 - z0 + 1) * per * per);
        if (_buf.Length != count * 16) _buf = new float[count * 16];
        int o = 0;
        for (int gz = z0; gz <= z1; gz++)
            for (int gx = x0; gx <= x1; gx++)
                for (int j = 0; j < per; j++)
                    for (int i = 0; i < per; i++)
                    {
                        double x = gx * CELL + i * STEP - half, z = gz * CELL + j * STEP - half;
                        double h = _ground.At(x, z);
                        if (_water is double wl && wl > h) h = wl;
                        // a random byte per sprite (the original's table 0xa40a68, indexed by a hash of the indices)
                        uint r = (uint)((gx * 4 + i) * 73856093) ^ (uint)((gz * 4 + j) * 19349663);
                        r ^= r >> 13; r *= 0x5bd1e995; r ^= r >> 15;
                        _buf[o++] = 1; _buf[o++] = 0; _buf[o++] = 0; _buf[o++] = (float)x;
                        _buf[o++] = 0; _buf[o++] = 1; _buf[o++] = 0; _buf[o++] = (float)h;
                        _buf[o++] = 0; _buf[o++] = 0; _buf[o++] = 1; _buf[o++] = (float)z;
                        _buf[o++] = r & 0xff; _buf[o++] = 0; _buf[o++] = 0; _buf[o++] = 1;
                    }
        _mm.InstanceCount = count;
        if (count > 0) RenderingServer.MultimeshSetBuffer(_mm.GetRid(), _buf);
        _mm.CustomAabb = new Aabb(new Vector3((float)(x0 * CELL - half - 16), -200, (float)(z0 * CELL - half - 16)),
                                  new Vector3((float)((x1 - x0 + 2) * CELL + 32), 600, (float)((z1 - z0 + 2) * CELL + 32)));
    }
}
