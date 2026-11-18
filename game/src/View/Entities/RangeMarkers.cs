// The range markers of the selected objects (MIRAGE Scripts/Game/mgr/RangeMarkerMgr.usl CRangeMarkerMgr, drawn by
// the engine's CObjMgr range markers), with the two fixes made for MIRAGE's PWClient.exe (/home/claude/mirage_ref/
// rangemarkers_README.md): the animation runs by real time (the same speed for 1 or 100 markers, at any frame rate;
// pause doesn't stop it) and overlapping markers of the same type are merged into one outline.
//
// What shows: for every selected unit and building (types CHTR, ANML, VHCL, SHIP, BLDG; anyone's, as the original)
// up to five rings - the ranges are the simulation's (World.MarkerRangesOf, src/Sim/Systems/RangeMarkerValues.cs):
//   type     colour 1 -> colour 2 (RGB)        width   MIRAGE's attribute
//   attack   255,0,0 -> 255,150,100 (red)      0.3 m   range_marker
//   alarm    140,0,255 (violet)                0.3 m   alarm_range_marker
//   fog      0,255,255 (cyan)                  0.3 m   FowRange
//   healing  255,216,0 (yellow)                0.3 m   HealingMarker
//   aura     0,255,0 -> 0,255,100 (green)      0.3 m   RangeMarker (Personal/<radius>)
// under the remake's one option for ranges: "Show attack ranges" (settings.json showRanges, default on; Ctrl+Shift+R -
// the original's key ToggleRangeMarkersAttack). Off: no markers (MIRAGE's ClearAll); the RangeRings of the buildings
// stay as they are.
//
// How: one mesh per marker type (five draw calls for any number of selected objects). The rings of a type are merged
// (RangeOutline: segments inside another ring of the type are left out) and laid on the ground as a band of quads,
// each vertex at the terrain's height (on the water over water). The work is done only when a ring of the type moved
// (more than 0.2 m), changed or came / went, and then at most 10 times a second while the selection moves; nothing is
// allocated per frame. Up to 12 rings of a type (a few selected objects) follow their objects every frame. The
// shader moves the dashes along the rings (0.03 rad per second: the README's speed, by Godot's TIME = real time) and
// lets them pulse, adding them to the ground as light as the original's ground rings do; it widens the band with the camera's distance (never thinner than about 2 pixels), like the
// selection and range rings.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.App;
using PwRemake.Sim.World;

namespace PwRemake.View.Entities;

public partial class RangeMarkers : Node3D
{
    /// <summary>colour 1 / colour 2 (0xRRGGBB) of each type (RangeMarkerType order) - RangeMarkerMgr.usl m_xColor1/2</summary>
    public static readonly (int C1, int C2)[] COLORS =
    {
        (0xff0000, 0xff9664),   // attack
        (0x8c00ff, 0x8c00ff),   // alarm
        (0x00ffff, 0x00ffff),   // fog of war
        (0xffd800, 0xffd800),   // healing
        (0x00ff00, 0x00ff64),   // aura
    };
    /// <summary>m_fWidth1 / m_fWidth2 of every type</summary>
    public const double WIDTH = 0.3;
    /// <summary>the animation speed of the README's fix: 0.03 per second (rad of the rings' turn)</summary>
    public const float SPEED = 0.03f;
    const int TYPES = 5;
    const double MOVE = 0.2, REBUILD = 0.1;
    /// <summary>up to this many rings of a type follow their objects every frame</summary>
    const int FEW = 12;

    // Added to the screen as light (blend_add, Fx/GammaBlend): the markers' colours have alpha 0 (RangeMarkerMgr.usl
    // {{255,216,0,0}} ...) and the engine's ground rings add them (debris renderer 0x6be370 with the additive copy of
    // Effects/Selection.tga, material 0x704 - 0x6bb110; one blend ONE / INVSRCALPHA, so alpha 0 adds). In the reference
    // shot of a Norse temple's healing marker the band adds about 150 - 175 / 120 - 140 to red / green and keeps the
    // ground's blue (the healing yellow 255,216,0 over it); until 2026-11-18 the port mixed the colour over the
    // ground at 0.9 - an opaque yellow line, not the original's glowing band.
    static readonly string SHADER = @"shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_add, fog_disabled, shadows_disabled, world_vertex_coords;
uniform vec3 col1;
uniform vec3 col2;
uniform float half_w = 0.15;
uniform float speed = 0.03;
varying float v_side;
varying float v_arc;
void vertex() {
    vec3 cam = INV_VIEW_MATRIX[3].xyz;
    float d = length(cam - VERTEX);
    VERTEX.xz += NORMAL.xz * UV.x * clamp(d * 0.0016, half_w, 2.0);
    VERTEX += normalize(cam - VERTEX) * 0.1 + vec3(0.0, 0.05, 0.0);
    v_side = UV.x;
    v_arc = (UV.y + TIME * speed) * UV2.x;          // the dashes run round the ring (radius in UV2.x)
}
" + PwRemake.View.Fx.GammaBlend.Glsl + @"
void fragment() {
    float k = fract(v_arc / 3.0);                   // 1.5 m of colour 1, 1.5 m of colour 2
    vec3 c = k < 0.5 ? col1 : col2;
    float pulse = 0.8 + 0.2 * sin(TIME * speed * 10.0 * 6.2831853);
    float s = abs(v_side);
    ALBEDO = pw_gamma_add(c * pulse * (1.0 - smoothstep(0.75, 1.0, s)));
    ALPHA = 1.0;
}
";
    static Shader? s_shader;

    sealed class Layer
    {
        public MeshInstance3D M = null!;
        public readonly List<MarkerCircle> Rings = new(), Drawn = new();
        public readonly List<MarkerArc> Arcs = new();
        public bool Dirty;
        public double LastBuild = -1e9;
    }

    readonly GameSession S;
    readonly Layer[] _layers = new Layer[TYPES];
    readonly List<(double A0, double A1)> _scratch = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>markers drawn (the option "Show attack ranges")</summary>
    public bool Show = true;

    public RangeMarkers(GameSession s) { S = s; Name = "RangeMarkers"; }

    public override void _Ready()
    {
        s_shader ??= new Shader { Code = SHADER };
        for (int t = 0; t < TYPES; t++)
        {
            var mat = new ShaderMaterial { Shader = s_shader, RenderPriority = 5 };
            mat.SetShaderParameter("col1", Rgb(COLORS[t].C1));
            mat.SetShaderParameter("col2", Rgb(COLORS[t].C2));
            mat.SetShaderParameter("half_w", (float)(WIDTH / 2));
            mat.SetShaderParameter("speed", SPEED);
            var m = new MeshInstance3D
            {
                Mesh = new ImmediateMesh(), MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false, ExtraCullMargin = 16384,      // (one mesh over the whole selection: no culling by its box)
            };
            AddChild(m);
            _layers[t] = new Layer { M = m };
        }
    }

    static Color Rgb(int c) => Color.Color8((byte)(c >> 16), (byte)(c >> 8), (byte)c);

    /// <summary>this frame's markers (RangeRings.Step calls it after the simulation's steps)</summary>
    public void Step()
    {
        var W = S.World;
        if (_layers[0] == null) return;
        foreach (var l in _layers) l.Rings.Clear();
        if (Show && W != null && S.View?.Ground != null && !S.Cinematic)
        {
            var sel = S.Selection.Items;
            for (int i = 0; i < sel.Count; i++)
            {
                var e = sel[i];
                if (!e.Alive || e.Inside || !PwRemake.Sim.World.World.HasRangeMarkers(e)) continue;
                if (e.Owner != S.Me && !S.VisibleToMe(e)) continue;
                var r = W.MarkerRangesOf(e);
                for (int t = 0; t < TYPES; t++)
                {
                    double R = r[(RangeMarkerType)t];
                    if (R > 0.5) _layers[t].Rings.Add(new MarkerCircle(e.Pos.X, e.Pos.Z, R));
                }
            }
        }
        double now = _clock.Elapsed.TotalSeconds;
        foreach (var l in _layers)
        {
            // (a few rings follow their objects every frame; many are worked out again after 0.2 m, 10 times a second)
            bool few = l.Rings.Count <= FEW;
            if (Changed(l, few ? 0.02 : MOVE)) l.Dirty = true;
            if (l.Rings.Count == 0) { l.M.Visible = false; l.Drawn.Clear(); l.Dirty = false; continue; }
            if (l.Dirty && (few || now - l.LastBuild >= REBUILD))
            {
                l.Drawn.Clear(); l.Drawn.AddRange(l.Rings);
                RangeOutline.Build(l.Drawn, l.Arcs, _scratch);
                Lay(l);
                l.Dirty = false; l.LastBuild = now;
            }
            l.M.Visible = true;
        }
    }

    /// <summary>did the rings of a layer change against those drawn (count, a radius, a centre by more than MOVE)?</summary>
    static bool Changed(Layer l, double move)
    {
        if (l.Rings.Count != l.Drawn.Count) return true;
        for (int i = 0; i < l.Rings.Count; i++)
        {
            var a = l.Rings[i]; var b = l.Drawn[i];
            if (Math.Abs(a.R - b.R) > 0.05 || Math.Abs(a.X - b.X) > move || Math.Abs(a.Z - b.Z) > move) return true;
        }
        return false;
    }

    // the arcs as quads on the ground: each vertex twice (the shader moves the two apart along the normal)
    void Lay(Layer l)
    {
        var hf = S.View.Ground;
        double? wl = S.World.WaterLevel;
        var im = (ImmediateMesh)l.M.Mesh;
        im.ClearSurfaces();
        if (l.Arcs.Count == 0) return;
        im.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var arc in l.Arcs)
        {
            var c = l.Drawn[arc.Circle];
            double step = Math.Tau / arc.N;
            Vector3 p0 = default, n0 = default; float a0 = 0;
            for (int k = 0; k <= arc.Count; k++)
            {
                double a = (arc.First + k) * step, ca = Math.Cos(a), sa = Math.Sin(a);
                double x = c.X + ca * c.R, z = c.Z + sa * c.R, h = hf.At(x, z);
                if (wl is double w && h < w) h = w + 0.02;
                var p1 = new Vector3((float)x, (float)h, (float)z);
                var n1 = new Vector3((float)ca, 0, (float)sa);
                float a1 = (float)a;
                if (k > 0)
                {
                    float r = (float)c.R;
                    V(im, p0, n0, -1, a0, r); V(im, p0, n0, 1, a0, r); V(im, p1, n1, 1, a1, r);
                    V(im, p0, n0, -1, a0, r); V(im, p1, n1, 1, a1, r); V(im, p1, n1, -1, a1, r);
                }
                p0 = p1; n0 = n1; a0 = a1;
            }
        }
        im.SurfaceEnd();
    }

    static void V(ImmediateMesh im, Vector3 p, Vector3 n, float side, float angle, float r)
    {
        im.SurfaceSetNormal(n);
        im.SurfaceSetUV(new Vector2(side, angle));
        im.SurfaceSetUV2(new Vector2(r, 0));
        im.SurfaceAddVertex(p);
    }
}
