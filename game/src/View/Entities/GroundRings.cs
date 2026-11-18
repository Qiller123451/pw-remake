// The selection circles of the original game, drawn ON the ground and merged where they overlap.
//
// The look comes from Effects/Selection.tga (Texture/Effects/Selection_(0256).dds). Its top strip (rows 8-11) is the
// circle's line: 4 texels across - black, colour, colour, black - in 9 segments of 28 texels: grey (objects without
// an owner), then PlayerColor0..7 (yellow, red, cyan, blue, green, purple, pink, orange). Each segment has a full
// half (alpha 255, selected) and a faint half (alpha 106, used here for the hover ring). The engine lays the line on
// the ground; where the circles of several selected objects overlap, only the outline of their union is drawn (the
// parts of a circle inside another circle are left out), so a selected group reads as one connected shape.
//
// How: every group of circles that should merge (one colour, one kind) is cut into tiles of TILE metres on the
// terrain's own grid (WorldView.Ground, the HeightField the terrain mesh is built from). Each tile is a small mesh on
// exactly the terrain's vertices (same triangle split), so it lies on the drawn ground; its shader takes the circles
// that reach the tile and draws the band just inside the boundary of their union: d = min over circles of (distance
// to centre - radius); the line is where -w < d < 0. The vertices are pulled 8 cm towards the camera so the band is
// never inside the coplanar terrain; depth test stays on, so buildings and units in front hide it as in the original.
//
// Same as remake/src/ui/groundrings.js.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.App;
using PwRemake.Sim.World;

namespace PwRemake.View.Entities;

public partial class GroundRings : Node3D
{
    /// <summary>RGB of the line per segment of the strip: [0] no owner, [1 + slot] PlayerColor&lt;slot&gt;</summary>
    static readonly int[] RING_RGB = { 0xa5aab5, 0xffaa00, 0xbd0000, 0x10cbad, 0x3165ce, 0x00a200, 0x5d009a, 0xde008c, 0xef4500 };
    /// <summary>the faint half of a segment (alpha 106 of 255): hover</summary>
    public const float FAINT = 106f / 255f;

    /// <summary>the line's colour (0xRRGGBB) for an owner: its party colour slot (the original's eight), else its UI colour (the
    /// remake's colours, "No colour"),
    /// else grey (no owner)</summary>
    public static int RingRgb(Player? owner)
    {
        if (owner == null) return RING_RGB[0];
        if (owner.ColorSlot >= 0 && owner.ColorSlot + 1 < RING_RGB.Length) return RING_RGB[owner.ColorSlot + 1];
        // the remake's palette colours (slots 8..15) and those of the whole spectrum: the player's (dark) colour
        return owner.Color;
    }

    /// <summary>wild animals (no owner), Scripts/Game/misc/ACColors.txt "dark": color8 AnimalFriendly (peaceful
    /// animals and those that only fight back: Aggressive -1 / 0), color10 AnimalHostile (hunters: Aggressive 1).
    /// (color9 AnimalNeutral e0e833 is in the table too; when the engine uses it is not known - not used here.)</summary>
    public const int ANIMAL_FRIENDLY = 0x6ddc4d, ANIMAL_NEUTRAL = 0xe0e833, ANIMAL_HOSTILE = 0xe0612b;

    /// <summary>the line's colour (0xRRGGBB) for an object: its owner's (RingRgb); a wild animal by how it behaves;
    /// else grey</summary>
    public static int RingRgbOf(Entity e)
    {
        if (e.Owner == null && e.Cls == "ANML") return e.Def != null && e.Def.Aggressive == 1 ? ANIMAL_HOSTILE : ANIMAL_FRIENDLY;
        return RingRgb(e.Owner);
    }

    /// <summary>width of the line (m) for a circle of radius r: ~11 % of a unit's circle, ~3 % of a big building's
    /// (measured on the original's screenshots)</summary>
    public static double Width(double r) => Math.Max(0.08, Math.Min(0.45, 0.11 * Math.Sqrt(r)));

    const int MAXC = 64;          // circles one tile's shader takes
    const double TILE = 16;       // tile size in metres (rounded to whole terrain cells)

    const string SHADER = @"shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix, fog_disabled, shadows_disabled, world_vertex_coords;
uniform vec4 circ[64];            // x, z, radius, line width
uniform vec2 seg[64];             // a long ring: half its middle line (x, z) - 0, 0: a circle
uniform int n_circ;
uniform vec3 col : source_color;
uniform float alpha_mul;
varying vec2 v_xz;
void vertex() {
    v_xz = VERTEX.xz;
    vec3 cam = INV_VIEW_MATRIX[3].xyz;
    VERTEX += normalize(cam - VERTEX) * 0.08 + vec3(0.0, 0.03, 0.0);
}
void fragment() {
    float d = 1.0e9;
    float w = 0.1;
    for (int i = 0; i < 64; i++) {
        if (i >= n_circ) break;
        vec4 c = circ[i];
        vec2 p = v_xz - c.xy, a = seg[i];
        float aa = dot(a, a);
        float h = aa > 0.0 ? clamp(dot(p, a) / aa, -1.0, 1.0) : 0.0;
        float di = length(p - a * h) - c.z;
        if (di < d) { d = di; w = c.w; }
    }
    w = max(w, fwidth(d) * 3.0);                       // never thinner than ~3 pixels (far camera)
    float u = (1.0 + d / w) * 4.0 - 0.5;               // texel across the strip: 0 black, 1-2 colour, 3 black
    float a = clamp(min(u + 1.0, 4.0 - u), 0.0, 1.0);  // (bilinear fade to the transparent rows around the strip)
    if (a <= 0.0) discard;
    float b = clamp(min(u, 3.0 - u), 0.0, 1.0);
    ALBEDO = col * b;
    ALPHA = a * alpha_mul;
}
";
    static Shader? s_shader;

    sealed class Group { public int Rgb; public float Alpha; public readonly List<Vector4> C = new(); public readonly List<Vector2> A = new(); }
    sealed class Tile { public int I0 = int.MaxValue, J0 = int.MaxValue, I1 = -1, J1 = -1; public readonly List<int> C = new(); }

    readonly GameSession S;
    readonly Dictionary<string, Group> _groups = new();
    readonly List<MeshInstance3D> _pool = new();
    int _used;
    readonly Vector4[] _circ = new Vector4[MAXC];
    readonly Vector2[] _seg = new Vector2[MAXC];

    public GroundRings(GameSession s) { S = s; Name = "GroundRings"; }

    /// <summary>a circle for this frame. Circles of one key merge; key null = by colour and alpha. w: line width (m),
    /// null = Width(r); (ax, az): a long ring (a stadium) - the circle drawn out from -(ax, az) to +(ax, az)
    /// (SelectionRing.Shape)</summary>
    public void Add(double x, double z, double r, int rgb, float alpha = 1, string? key = null, double? w = null, double ax = 0, double az = 0)
    {
        if (!(r > 0)) return;
        key ??= $"{rgb:x6}/{alpha:0.000}";
        if (!_groups.TryGetValue(key, out var g)) _groups[key] = g = new Group { Rgb = rgb, Alpha = alpha };
        g.C.Add(new Vector4((float)x, (float)z, (float)r, (float)(w ?? Width(r))));
        g.A.Add(new Vector2((float)ax, (float)az));
    }

    MeshInstance3D NextMesh()
    {
        if (_used >= _pool.Count)
        {
            s_shader ??= new Shader { Code = SHADER };
            var mat = new ShaderMaterial { Shader = s_shader, RenderPriority = 6 };
            var m = new MeshInstance3D
            {
                Mesh = new ImmediateMesh(), MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // the mesh moves every frame: no culling by a stale box
                ExtraCullMargin = 16384,
            };
            AddChild(m);
            _pool.Add(m);
        }
        var r = _pool[_used++];
        r.Visible = true;
        return r;
    }

    // the mesh on terrain vertices i0..i1, j0..j1 (inclusive); two triangles per cell split along (i, j+1) - (i+1, j)
    // as the terrain (HeightField.At, TerrainMesh)
    void Lay(MeshInstance3D m, int i0, int j0, int i1, int j1)
    {
        var hf = S.View.Ground;
        int n = hf.N;
        double e = hf.Cell, half = hf.Size / 2;
        double? wl = S.World.WaterLevel;
        Vector3 V(int i, int j)
        {
            double h = hf.H[j * n + i];
            if (wl is double w && h < w) h = w + 0.02;              // on the water surface (ships, shores)
            return new Vector3((float)(i * e - half), (float)h, (float)(j * e - half));
        }
        var im = (ImmediateMesh)m.Mesh;
        im.ClearSurfaces();
        im.SurfaceBegin(Godot.Mesh.PrimitiveType.Triangles);
        for (int j = j0; j < j1; j++)
            for (int i = i0; i < i1; i++)
            {
                Vector3 a = V(i, j), b = V(i, j + 1), c = V(i + 1, j + 1), d = V(i + 1, j);
                im.SurfaceAddVertex(a); im.SurfaceAddVertex(d); im.SurfaceAddVertex(b);
                im.SurfaceAddVertex(b); im.SurfaceAddVertex(d); im.SurfaceAddVertex(c);
            }
        im.SurfaceEnd();
    }

    /// <summary>draws this frame's circles and forgets them</summary>
    public void Flush()
    {
        _used = 0;
        var hf = S.View?.Ground;
        if (hf != null)
        {
            int n = hf.N;
            double e = hf.Cell, half = hf.Size / 2;
            int T = Math.Max(1, (int)Math.Round(TILE / e));
            int CellOf(double v) => (int)Math.Floor((v + half) / e);
            foreach (var g in _groups.Values)
            {
                var tiles = new Dictionary<(int, int), Tile>();
                for (int c = 0; c < g.C.Count; c++)
                {
                    var q = g.C[c];
                    double reach = q.Z + q.W * 0.2 + 0.05, rx = reach + Math.Abs(g.A[c].X), rz = reach + Math.Abs(g.A[c].Y);
                    int ci0 = Math.Max(0, CellOf(q.X - rx)), ci1 = Math.Min(n - 2, CellOf(q.X + rx));
                    int cj0 = Math.Max(0, CellOf(q.Y - rz)), cj1 = Math.Min(n - 2, CellOf(q.Y + rz));
                    if (ci1 < ci0 || cj1 < cj0) continue;
                    for (int tj = cj0 / T; tj <= cj1 / T; tj++)
                        for (int ti = ci0 / T; ti <= ci1 / T; ti++)
                        {
                            if (!tiles.TryGetValue((ti, tj), out var t)) tiles[(ti, tj)] = t = new Tile();
                            // the cells of this tile the circle reaches
                            t.I0 = Math.Min(t.I0, Math.Max(ci0, ti * T)); t.I1 = Math.Max(t.I1, Math.Min(ci1, ti * T + T - 1));
                            t.J0 = Math.Min(t.J0, Math.Max(cj0, tj * T)); t.J1 = Math.Max(t.J1, Math.Min(cj1, tj * T + T - 1));
                            t.C.Add(c);
                        }
                }
                var col = PartyColorOf(g.Rgb);
                foreach (var t in tiles.Values)
                {
                    var m = NextMesh();
                    int cnt = Math.Min(MAXC, t.C.Count);
                    for (int k = 0; k < MAXC; k++) { _circ[k] = k < cnt ? g.C[t.C[k]] : default; _seg[k] = k < cnt ? g.A[t.C[k]] : default; }
                    var mat = (ShaderMaterial)m.MaterialOverride;
                    mat.SetShaderParameter("circ", _circ);
                    mat.SetShaderParameter("seg", _seg);
                    mat.SetShaderParameter("n_circ", cnt);
                    mat.SetShaderParameter("col", col);
                    mat.SetShaderParameter("alpha_mul", g.Alpha);
                    Lay(m, t.I0, t.J0, t.I1 + 1, t.J1 + 1);          // cells i0..i1 -> vertices i0..i1+1
                }
            }
        }
        for (int i = _used; i < _pool.Count; i++) _pool[i].Visible = false;
        _groups.Clear();
    }

    static Color PartyColorOf(int rgb) => Color.Color8((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ------------------------------------------------------------ health bars (Selection.tga, rows 32-47)
    /// <summary>the health gradient (rows 32-39, 64 texels: red at 0 % .. green at 100 %), every 8th texel</summary>
    static readonly int[] GRAD = { 0xff1d00, 0xff3800, 0xff6900, 0xffa200, 0xffc900, 0xdecb00, 0x8ccb00, 0x42c700, 0x18c700 };
    public static Color HpGradient(double f)
    {
        double x = Math.Clamp(f, 0, 1) * (GRAD.Length - 1);
        int i = Math.Min(GRAD.Length - 2, (int)Math.Floor(x));
        return PartyColorOf(GRAD[i]).Lerp(PartyColorOf(GRAD[i + 1]), (float)(x - i));
    }
    /// <summary>the swatches under it (rows 40-47, 8 texels each): bar background (black, alpha 191), yellow, light blue</summary>
    public static readonly Color BAR_BACK = new(0, 0, 0, 0.75f), BAR_BUILD = Color.Color8(247, 243, 90), BAR_QUEUE = Color.Color8(139, 192, 238);
    /// <summary>the level emblems: 32 x 32 cells from (74, 32), level 1..5</summary>
    public static Rect2 Emblem(int level) => new(74 + 32 * (Math.Clamp(level, 1, 5) - 1), 32, 32, 32);
    /// <summary>an object's line colour</summary>
    public static Color RingColor(Entity e) => PartyColorOf(RingRgbOf(e));
}
