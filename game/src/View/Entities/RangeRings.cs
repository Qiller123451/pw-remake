// The attack range circles of the shooting buildings (the remake's own; Kacper 2026-11-13: "when I place a building
// that can attack, show its radius while it is being placed; a built one shows its radius unless the setting is off").
//
// What shows:
//  * the building being placed (GameInput's ghost, wall towers on their tile too): its range and, where the weapon
//    has one, its minimum range (dashed) - always, in the player's colour, strong;
//  * the player's own finished shooting buildings (towers, turrets, cannons, bunkers, the SEAS nests ...): a faint
//    circle each while the option "Show attack ranges" is on (settings.json "showRanges", default on; Ctrl+Shift+R
//    toggles it in the game - the original's own key for its range markers, Keys.srf ToggleRangeMarkersAttack);
//  * a selected or hovered shooting building (anyone's): strong, also with the option off (a selected one with the
//    option on: its range markers instead - RangeMarkers, MIRAGE's markers of the selected objects -, only its dashed
//    minimum range here).
// The range is the simulation's own (World.TowerRange / TowerRangeOf): the weapon's range with the tribe's upgrades +
// half the building's radius, from its centre to the target's surface - what Combat.TowerUpdate fires at; the
// minimum range is the weapon's (TowerUpdate: a target nearer than it is not shot at).
//
// How: one thin band per circle on the ground - a triangle strip of 1.5 m segments, each vertex at the terrain's
// height (on the water surface over water), drawn like the selection rings (GroundRings: unshaded, the depth test on,
// pulled a little towards the camera; a dark edge either side of the colour). The band widens with the camera's
// distance in the vertex shader (never thinner than about 2 pixels). A built building's circle is made once and kept
// (rebuilt only when its range changes: an upgrade); the ghost's when it moves. Godot culls the circles off screen by
// their boxes. No allocation per frame: the circles are pooled, the materials kept per colour.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.App;
using PwRemake.Sim.World;

namespace PwRemake.View.Entities;

public partial class RangeRings : Node3D
{
    /// <summary>"Show attack ranges" (settings.json showRanges): every own shooting building's circle</summary>
    public bool ShowAll = true;

    const float FAINT = 0.42f, STRONG = 0.9f;
    const double SEG = 1.5;                 // metres per segment

    const string SHADER = @"shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix, fog_disabled, shadows_disabled, world_vertex_coords;
uniform vec3 col : source_color;
uniform float alpha_mul = 1.0;
uniform float dash = 0.0;            // > 0: dashes of this length (m) - the minimum range
varying float v_side;
varying float v_arc;
void vertex() {
    vec3 cam = INV_VIEW_MATRIX[3].xyz;
    float d = length(cam - VERTEX);
    VERTEX.xz += NORMAL.xz * UV.x * clamp(d * 0.0024, 0.14, 2.5);   // half the band's width
    VERTEX += normalize(cam - VERTEX) * 0.1 + vec3(0.0, 0.05, 0.0);
    v_side = UV.x; v_arc = UV.y;
}
void fragment() {
    if (dash > 0.0 && fract(v_arc / (2.0 * dash)) > 0.5) discard;
    float s = abs(v_side);
    ALBEDO = mix(col * 0.25, col, 1.0 - smoothstep(0.35, 0.6, s));
    ALPHA = alpha_mul * (1.0 - smoothstep(0.7, 1.0, s));
}
";
    static Shader? s_shader;

    sealed class Circle
    {
        public MeshInstance3D M = null!;
        public double X = double.NaN, Z, R;
        public int Seen;
        public ShaderMaterial? Mat;
    }
    sealed class Ring { public readonly Circle Outer = new(), Inner = new(); public int Seen; }

    readonly GameSession S;
    readonly Dictionary<Building, Ring> _rings = new();
    readonly List<Ring> _free = new();
    readonly List<Building> _gone = new();
    readonly Dictionary<(int Rgb, int A, bool Dash), ShaderMaterial> _mats = new();
    readonly Ring _ghost = new();
    int _frame;
    // the ghost's range: worked out when the class, the turn or the player's rules change
    (string Name, double Rot, int Tt) _ghostKey;
    (double R, double Min)? _ghostRange;

    /// <summary>the range markers of the selected objects (MIRAGE's CRangeMarkerMgr), under the same option</summary>
    public RangeMarkers Markers { get; private set; } = null!;

    public RangeRings(GameSession s) { S = s; Name = "RangeRings"; }

    public override void _Ready()
    {
        Init(_ghost.Outer); Init(_ghost.Inner);
        Markers = new RangeMarkers(S);
        AddChild(Markers);
    }

    void Init(Circle c)
    {
        // (the shader widens the band by up to 2.5 m: a margin round its box)
        c.M = new MeshInstance3D { Mesh = new ImmediateMesh(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false, ExtraCullMargin = 4 };
        AddChild(c.M);
    }

    ShaderMaterial Mat(int rgb, float alpha, bool dash)
    {
        var k = (rgb, (int)(alpha * 100), dash);
        if (_mats.TryGetValue(k, out var m)) return m;
        s_shader ??= new Shader { Code = SHADER };
        m = new ShaderMaterial { Shader = s_shader, RenderPriority = 5 };
        m.SetShaderParameter("col", Color.Color8((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        m.SetShaderParameter("alpha_mul", alpha);
        m.SetShaderParameter("dash", dash ? 1.2f : 0f);
        return _mats[k] = m;
    }

    /// <summary>this frame's circles (after the simulation's steps)</summary>
    public void Step()
    {
        _frame++;
        var W = S.World;
        if (W == null || S.View?.Ground == null) return;
        bool cine = S.Cinematic;
        var me = S.Me;
        var hover = S.Input?.Hover;
        // ---- the building being placed
        var gh = !cine && S.Input?.Mode == "place" ? S.Input.GhostInfo : null;
        if (gh is { } g)
        {
            int tt = me?.Tt?.Version ?? 0;
            double rot = S.Input!.GhostRot;
            if (_ghostKey != (g.Name, rot, tt)) { _ghostKey = (g.Name, rot, tt); _ghostRange = W.TowerRangeOf(g.Name, me, rot); }
        }
        if (gh is { } g2 && _ghostRange is var (gr, gmin) && S.Input!.GhostShown)
        {
            int rgb = GroundRings.RingRgb(me);
            Put(_ghost.Outer, g2.X, g2.Z, gr, Mat(rgb, STRONG, false));
            Put(_ghost.Inner, g2.X, g2.Z, gmin, Mat(rgb, STRONG * 0.8f, true));
        }
        else { _ghost.Outer.M.Visible = false; _ghost.Inner.M.Visible = false; }
        // ---- built buildings
        if (!cine)
        {
            var list = W.Buildings;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                if (!b.Alive || !b.Built || b.Weapons.Long?.Projectile == null) continue;
                bool own = b.Owner == me && me != null;
                bool selected = S.Selection.Contains(b);
                bool strong = b == hover || selected;
                if (!strong && !(own && ShowAll)) continue;
                if (!own && !S.VisibleToMe(b) && !S.Fog.Explored(b.Pos.X, b.Pos.Z)) continue;
                if (W.TowerRange(b) is not var (R, min)) continue;
                if (!_rings.TryGetValue(b, out var ring))
                {
                    if (_free.Count > 0) { ring = _free[^1]; _free.RemoveAt(_free.Count - 1); }
                    else { ring = new Ring(); Init(ring.Outer); Init(ring.Inner); }
                    _rings[b] = ring;
                }
                ring.Seen = _frame;
                int rgb = GroundRings.RingRgbOf(b);
                float a = strong ? STRONG : FAINT;
                // (selected with the option on: its attack marker is the circle - RangeMarkers)
                if (selected && ShowAll) ring.Outer.M.Visible = false;
                else Put(ring.Outer, b.Pos.X, b.Pos.Z, R, Mat(rgb, a, false));
                Put(ring.Inner, b.Pos.X, b.Pos.Z, min, Mat(rgb, a * 0.8f, true));
            }
        }
        // circles of buildings no longer shown go back to the pool
        foreach (var (b, ring) in _rings) if (ring.Seen != _frame) _gone.Add(b);
        if (_gone.Count > 0)
        {
            foreach (var b in _gone)
            {
                var ring = _rings[b];
                ring.Outer.M.Visible = false; ring.Inner.M.Visible = false;
                ring.Outer.X = ring.Inner.X = double.NaN;      // (rebuilt when used again)
                _rings.Remove(b);
                _free.Add(ring);
            }
            _gone.Clear();
        }
        if (Markers != null) { Markers.Show = ShowAll; Markers.Step(); }
    }

    /// <summary>a circle shown this frame: the mesh rebuilt only when its centre or radius changed</summary>
    void Put(Circle c, double x, double z, double r, ShaderMaterial mat)
    {
        if (!(r > 0.5)) { c.M.Visible = false; return; }
        if (c.X != x || c.Z != z || c.R != r) { c.X = x; c.Z = z; c.R = r; Build(c); }
        if (c.Mat != mat) { c.Mat = mat; c.M.MaterialOverride = mat; }
        c.M.Visible = true;
    }

    // the band: a triangle strip round the circle, both edges at the circle (the shader moves them apart), the ground's
    // height under each vertex (the water's surface over water)
    void Build(Circle c)
    {
        var hf = S.View.Ground;
        double? wl = S.World.WaterLevel;
        int n = Math.Clamp((int)Math.Ceiling(Math.Tau * c.R / SEG), 32, 512);
        var im = (ImmediateMesh)c.M.Mesh;
        im.ClearSurfaces();
        im.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
        double step = Math.Tau / n;
        for (int i = 0; i <= n; i++)
        {
            double a = i * step, ca = Math.Cos(a), sa = Math.Sin(a);
            double x = c.X + ca * c.R, z = c.Z + sa * c.R, h = hf.At(x, z);
            if (wl is double w && h < w) h = w + 0.02;
            var p = new Vector3((float)x, (float)h, (float)z);
            var nrm = new Vector3((float)ca, 0, (float)sa);
            float arc = (float)(a * c.R);
            im.SurfaceSetNormal(nrm); im.SurfaceSetUV(new Vector2(-1, arc)); im.SurfaceAddVertex(p);
            im.SurfaceSetNormal(nrm); im.SurfaceSetUV(new Vector2(1, arc)); im.SurfaceAddVertex(p);
        }
        im.SurfaceEnd();
    }
}
