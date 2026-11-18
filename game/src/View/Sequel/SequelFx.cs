// What only the eye sees of the remake's own campaigns (docs/SEQUEL.md "Effects"): the roads on the ground, the
// night (a darker, bluer light or the mission's own dusk), the rifts pulsing (the warp gate's vortex in them, shrinking
// while a stabilizer seals them, lightning from the stabilizer), searchlight beams sweeping at night with their lit
// ground, the power lines between the poles, the windmills' rotors and the water wheels turning, the set pieces (the big
// wheel's rim, boats on the river), the mission 5 rocket and its flight, the electric weapons' arcs, and the reach of
// the open rifts while a stabilizer is being placed. It reads the simulation and changes nothing in it.
// Ported from remake/src/ui/sequel_ui.js (effects, pieces, wires, rift, searchlight with soft / groundLight / drape,
// placeRings, arcs) and
// remake/src/game/sequel/maps.js roadMeshes (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Sequel;
using PwRemake.Sim.World;
using PwRemake.View.Models;

namespace PwRemake.View.Sequel;

public partial class SequelFx : Node3D
{
    static readonly Logger L = Log.For("View.Sequel");
    readonly GameSession G;
    readonly SequelSession S;
    SequelRuntime R => S.Runtime;
    PwRemake.Sim.World.World W => S.World;
    /// <summary>rotors turning on buildings: the rotor model, its hub in the building model's frame, its axis, turns/s</summary>
    static readonly Dictionary<string, (string Model, Vector3 At, Vector3 Axis, float Speed)> ROTORS = new()
    {
        ["sq_windmill"] = ("sq_windmill_rotor", new Vector3(0, 13.05f, -1.45f), Vector3.Back, 3.5f),
        ["sq_watermill"] = ("sq_waterwheel", new Vector3(0, 2.2f, 4.0f), Vector3.Right, -0.8f),
    };
    // the searchlight (art/sequel/works.py searchlight, searchlamp): the lamp's pivot height, how far out the beam meets
    // the ground, the lit spot's radius, the glow round the tower (the 26 m it dazzles in), how fast the beam sweeps (rad/s)
    const float LAMP_Y = 9.2f, LAMP_REACH = 17, LAMP_SPOT = 6, LAMP_AREA = 28, LAMP_SWEEP = 0.5f;
    static readonly Dictionary<string, (float W, float Sh)> ROAD_TEX = new() { ["highway"] = (9.0f, 1.6f), ["street"] = (7.0f, 1.0f), ["dirt"] = (6.0f, 1.4f), ["city"] = (15.0f, 0.3f), ["pave"] = (24.0f, 0.4f) };
    const float ROAD_LENGTH = 48;

    readonly Dictionary<Building, Node3D> _rotors = new();
    readonly Dictionary<Building, (Node3D? Lamp, MeshInstance3D Beam, GroundDisc Spot, GroundDisc Area, MeshInstance3D Glow)> _lights = new();
    readonly Dictionary<Building, Node3D> _vortex = new();
    readonly Dictionary<SqPiece, (Node3D Outer, Node3D Inner)> _pieces = new();
    readonly List<(Vector3 A, Vector3 B, float T)> _arcs = new();
    MeshInstance3D _wires = null!, _arcMesh = null!, _rings = null!;
    string _wireKey = "", _ringKey = "";
    bool? _night;
    Node3D? _rocket;
    float _rocketY, _flight;
    bool _launched, _errShown;
    double _stabArcT;
    readonly RandomNumberGenerator _rng = new();

    public SequelFx(GameSession g, SequelSession s) { G = g; S = s; }

    static StandardMaterial3D Unlit(Color c, bool add = false) => new()
    {
        AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = add ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix, DisableFog = true, NoDepthTest = false,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true,
    };

    public override void _Ready()
    {
        _wires = new MeshInstance3D { Mesh = new ImmediateMesh(), MaterialOverride = Unlit(new Color(0.1f, 0.1f, 0.09f)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _arcMesh = new MeshInstance3D { Mesh = new ImmediateMesh(), MaterialOverride = Unlit(new Color(0.66f, 0.88f, 1f, 0.95f), true), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _rings = new MeshInstance3D { Mesh = new ImmediateMesh(), MaterialOverride = Unlit(Colors.White), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_wires); AddChild(_arcMesh); AddChild(_rings);
        try { BuildRoads(); } catch (Exception e) { L.Error("the roads", e); }
    }

    float Ground(double x, double z) => (float)G.View.Ground.At(x, z);

    // ------------------------------------------------------------------ the roads (maps.js roadMeshes)
    void BuildRoads()
    {
        var mats = new Dictionary<string, StandardMaterial3D>();
        StandardMaterial3D Mat(string kind)
        {
            if (mats.TryGetValue(kind, out var m)) return m;
            Texture2D? tex = null;
            var path = G.Assets.Resolve($"assets/tex/sq_road_{kind}.png");
            if (path != null) { var img = Image.LoadFromFile(path); if (img != null && !img.IsEmpty()) { img.GenerateMipmaps(); tex = ImageTexture.CreateFromImage(img); } }
            m = new StandardMaterial3D
            {
                AlbedoTexture = tex, AlbedoColor = tex == null ? new Color(0.22f, 0.22f, 0.21f, 0.9f) : Colors.White, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                TextureRepeat = true, Roughness = 1, RenderPriority = kind is "dirt" or "pave" ? 1 : kind is "street" or "city" ? 2 : 3, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            if (tex == null) L.WarnOnce("road:" + kind, $"no road surface assets/tex/sq_road_{kind}.png (the sequel's art): plain asphalt");
            mats[kind] = m;
            return m;
        }
        foreach (var r in S.Roads)
        {
            if (r.Path.Count < 2) continue;
            var kind = ROAD_TEX.ContainsKey(r.Kind) ? r.Kind : "highway";
            var (tw, sh) = ROAD_TEX[kind];
            float w = (float)(r.Width * (tw + 2 * sh) / tw);
            var pts = SequelMap.Along(r.Path, 2);
            var last = r.Path[^1]; var prev = r.Path[^2];
            pts.Add(new[] { last[0], last[1], Math.Atan2(last[0] - prev[0], last[1] - prev[1]) });
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            double dist = 0;
            float lift = kind is "dirt" or "pave" ? 0.06f : kind is "street" or "city" ? 0.08f : 0.1f;
            for (int i = 0; i < pts.Count; i++)
            {
                double x = pts[i][0], z = pts[i][1], a = pts[i][2];
                if (i > 0) dist += Math.Sqrt((x - pts[i - 1][0]) * (x - pts[i - 1][0]) + (z - pts[i - 1][1]) * (z - pts[i - 1][1]));
                double nx = Math.Cos(a), nz = -Math.Sin(a);
                if (i == 0) continue;
                double px = pts[i - 1][0], pz = pts[i - 1][1], pa = pts[i - 1][2], pnx = Math.Cos(pa), pnz = -Math.Sin(pa);
                double d0 = dist - Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                Vector3 V(double cx, double cz, double kx, double kz, int side) { double vx = cx + kx * side * w / 2, vz = cz + kz * side * w / 2; return new Vector3((float)vx, Ground(vx, vz) + lift, (float)vz); }
                var a0 = V(px, pz, pnx, pnz, -1); var b0 = V(px, pz, pnx, pnz, 1); var a1 = V(x, z, nx, nz, -1); var b1 = V(x, z, nx, nz, 1);
                float v0 = (float)(d0 / ROAD_LENGTH), v1 = (float)(dist / ROAD_LENGTH);
                void P(Vector3 p, float u, float v) { st.SetNormal(Vector3.Up); st.SetUV(new Vector2(u, v)); st.AddVertex(p); }
                // (wound clockwise seen from above: Godot's front faces - a back face would be lit from below)
                P(a0, 0, v0); P(b0, 1, v0); P(a1, 0, v1);
                P(b0, 1, v0); P(b1, 1, v1); P(a1, 0, v1);
            }
            var mesh = st.Commit();
            AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = Mat(kind), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
    }

    // ------------------------------------------------------------------ every frame
    public override void _Process(double delta)
    {
        float dt = (float)Math.Min(0.1, delta);
        float t = (float)(Time.GetTicksMsec() / 1000.0);
        try
        {
            Night();
            foreach (var b in W.Buildings)
            {
                if (!b.Alive) continue;
                if (b.Name == "sq_rift") Rift(b, t, dt);
                else if (b.Name == "sq_searchlight") Searchlight(b, t);
                if (ROTORS.ContainsKey(b.Name) && b.Built) Rotor(b, dt);
            }
            foreach (var b in _lights.Keys.Where(b => !b.Alive).ToList()) { var l = _lights[b]; l.Beam.QueueFree(); l.Spot.Mesh.QueueFree(); l.Area.Mesh.QueueFree(); l.Glow.QueueFree(); _lights.Remove(b); }
            foreach (var b in _rotors.Keys.Where(b => !b.Alive).ToList()) _rotors.Remove(b);
            foreach (var b in _vortex.Keys.Where(b => !b.Alive).ToList()) _vortex.Remove(b);
            Wires();
            Arcs(dt);
            Pieces(dt);
            Rocket(dt);
            PlaceRings();
        }
        catch (Exception e) { if (!_errShown) { _errShown = true; L.Error("the sequel's effects", e); } }
    }

    /// <summary>the night: the mission's dusk (def.dusk: sky, sun and sky light shares, the sun's colour) or a dark blue one</summary>
    void Night()
    {
        bool on = R.NightOn && !G.Cinematic;
        if (_night == on) return;
        _night = on;
        var d = R.Def["dusk"] as JsonObject;
        Color C(JsonNode? n, int def) { int v = n is JsonValue ? (int)n.Num() : def; return new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f); }
        G.Renderer.SetNight(on ? ((float)(d?["sun"].Num(0.2) ?? 0.2), C(d?["sunColor"], 0x8aa8ff), (float)(d?["hemi"].Num(0.34) ?? 0.34), C(d?["sky"], 0x101828)) : null);
    }

    Node3D? Inst(string model)
    {
        if (!G.Models.Has(model)) return null;
        return G.Models.Instantiate(model);
    }

    // a rift pulses; while a stabilizer seals it, it shrinks and lightning runs from the stabilizer into it
    void Rift(Building b, float t, float dt)
    {
        var m = G.Entities.ModelOf(b);
        if (m == null) return;
        if (!_vortex.ContainsKey(b))
        {
            // the vortex of the warp gate (single_04's aje_warpgate): its swirl and sparks, without the building
            var v = Inst("aje_warpgate");
            if (v != null)
            {
                foreach (var mi in v.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()) mi.Visible = false;
                m.AddChild(v);
            }
            _vortex[b] = v ?? new Node3D();
        }
        var r = R.Rifts.LastOrDefault(x => x.E == b);
        float seal = r == null ? 0 : (float)Math.Min(1, r.Seal / Math.Max(1, r.SealTime));
        float pulse = 1 + 0.04f * Mathf.Sin(t * 3);
        m.Scale = Vector3.One * (1 - 0.45f * seal) * pulse;
        if (r?.Stab is { Alive: true } st)
        {
            _stabArcT -= dt;
            if (_stabArcT <= 0)
            {
                _stabArcT = 0.12;
                float top = (float)(st.Ht > 0 ? st.Ht : 6);
                Arc(new Vector3((float)st.Pos.X, Ground(st.Pos.X, st.Pos.Z) + top * 0.9f, (float)st.Pos.Z), new Vector3((float)r.X, Ground(r.X, r.Z) + 4, (float)r.Z), 0.18f);
            }
        }
    }

    // a searchlight at night: the lamp turns, its beam reaches down to a lit spot sweeping round the tower, the ground it
    // dazzles in glows (the gameplay: SequelRuntime night, Viewers). The lit spot and the glow round the tower are discs
    // of light laid on the ground: their vertices follow the terrain (sequel_ui.js groundLight, drape).
    void Searchlight(Building b, float t)
    {
        if (!_lights.TryGetValue(b, out var L0))
        {
            Node3D? lamp = null;
            var m = G.Entities.ModelOf(b);
            if (m != null && (lamp = Inst("sq_searchlamp")) != null) { lamp.Position = new Vector3(0, LAMP_Y, 0); m.AddChild(lamp); }
            var beam = new MeshInstance3D { Mesh = BeamCone(), MaterialOverride = LightMat(new Color(1f, 0.94f, 0.78f), null, 5), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            var spot = GroundLight(new Color(0x84 / 255f, 0x76 / 255f, 0x5a / 255f), 12);
            var area = GroundLight(new Color(0x3a / 255f, 0x34 / 255f, 0x26 / 255f), 16);
            // (the lamp's glow: a soft sprite 5 m across, always facing the camera)
            var glow = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(5, 5) }, MaterialOverride = LightMat(new Color(1f, 0.957f, 0.816f), SoftTex(), 5, billboard: true), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(beam); AddChild(spot.Mesh); AddChild(area.Mesh); AddChild(glow);
            Drape(area, b.Pos.X, b.Pos.Z, LAMP_AREA);
            _lights[b] = L0 = (lamp, beam, spot, area, glow);
        }
        bool on = R.NightOn && b.Built;
        L0.Beam.Visible = L0.Spot.Mesh.Visible = L0.Area.Mesh.Visible = L0.Glow.Visible = on;
        if (!on) return;
        float yaw = t * LAMP_SWEEP + b.Id;                                 // the beam's heading (the lamp's front is -z)
        float fx = -Mathf.Sin(yaw), fz = -Mathf.Cos(yaw);
        if (L0.Lamp != null) L0.Lamp.Rotation = new Vector3(0, yaw - (float)b.Rot, 0);
        float gy = Ground(b.Pos.X, b.Pos.Z);
        var lampP = new Vector3((float)b.Pos.X + fx * 0.6f, gy + LAMP_Y - 0.3f, (float)b.Pos.Z + fz * 0.6f);
        float sx = (float)b.Pos.X + fx * LAMP_REACH, sz = (float)b.Pos.Z + fz * LAMP_REACH;
        var spotP = new Vector3(sx, Ground(sx, sz), sz);
        // (the cone: apex at the lamp, its open end 1 m along +z - turned so that +z points at the spot, stretched to it)
        var look = Transform3D.Identity.LookingAt(spotP - lampP, Vector3.Up, true);
        L0.Beam.Transform = new Transform3D(look.Basis * Basis.FromScale(new Vector3(LAMP_SPOT * 0.8f, LAMP_SPOT * 0.8f, lampP.DistanceTo(spotP))), lampP);   // (scaled in its own frame)
        Drape(L0.Spot, sx, sz, LAMP_SPOT * (1 + 0.04f * Mathf.Sin(t * 7)));
        L0.Glow.Position = lampP;
    }

    // ---- the searchlight's pieces (sequel_ui.js soft, lightMat, groundLight, drape)
    static GradientTexture2D? _softTex;
    /// <summary>a soft round light (white in the middle, nothing at the rim): lit spots, lamp glows</summary>
    static GradientTexture2D SoftTex()
    {
        if (_softTex != null) return _softTex;
        var g = new Gradient();
        g.Offsets = new[] { 0f, 0.55f, 1f };
        g.Colors = new[] { new Color(1, 1, 1, 1), new Color(1, 1, 1, 0.55f), new Color(1, 1, 1, 0) };
        return _softTex = new GradientTexture2D { Gradient = g, Width = 64, Height = 64, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f) };
    }
    /// <summary>an additive light: the colour times the soft map (none: the vertex colours alone), drawn after the ground</summary>
    static StandardMaterial3D LightMat(Color c, Texture2D? map, int order, bool billboard = false)
    {
        var m = Unlit(c, true);
        m.AlbedoTexture = map;
        m.RenderPriority = order;
        if (billboard) { m.BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled; m.BillboardKeepScale = true; }
        return m;
    }
    /// <summary>a disc of light laid on the ground: a 2 x 2 m grid of n x n cells (the soft map makes it round), its
    /// vertices placed on the terrain by Drape</summary>
    sealed class GroundDisc { public MeshInstance3D Mesh = null!; public Vector2[] Base = null!; public int[] Index = null!; public Vector2[] Uv = null!; }
    static GroundDisc GroundLight(Color c, int n)
    {
        var d = new GroundDisc { Base = new Vector2[(n + 1) * (n + 1)], Uv = new Vector2[(n + 1) * (n + 1)], Index = new int[n * n * 6] };
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++) { d.Base[j * (n + 1) + i] = new Vector2(-1 + 2f * i / n, -1 + 2f * j / n); d.Uv[j * (n + 1) + i] = new Vector2((float)i / n, (float)j / n); }
        int k = 0;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c0 = a + n + 1, c1 = c0 + 1;
                // (wound clockwise seen from above: Godot's front faces)
                d.Index[k++] = a; d.Index[k++] = b; d.Index[k++] = c0;
                d.Index[k++] = b; d.Index[k++] = c1; d.Index[k++] = c0;
            }
        d.Mesh = new MeshInstance3D { Mesh = new ArrayMesh(), MaterialOverride = LightMat(c, SoftTex(), 4), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        return d;
    }
    /// <summary>lays the disc round (x, z) with radius r on the terrain, lift m above it</summary>
    void Drape(GroundDisc d, double x, double z, float r, float lift = 0.25f)
    {
        var pos = new Vector3[d.Base.Length];
        var nrm = new Vector3[d.Base.Length];
        for (int i = 0; i < pos.Length; i++)
        {
            float px = (float)x + d.Base[i].X * r, pz = (float)z + d.Base[i].Y * r;
            pos[i] = new Vector3(px, Ground(px, pz) + lift, pz);
            nrm[i] = Vector3.Up;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = pos;
        arrays[(int)Mesh.ArrayType.Normal] = nrm;
        arrays[(int)Mesh.ArrayType.TexUV] = d.Uv;
        arrays[(int)Mesh.ArrayType.Index] = d.Index;
        var mesh = (ArrayMesh)d.Mesh.Mesh;
        mesh.ClearSurfaces();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }
    /// <summary>the beam: an open cone, its apex at the origin and its 1 m wide end 1 m along +z, brighter at the lamp
    /// (vertex colours 0.35 there, 0.05 at the open end)</summary>
    static ArrayMesh? _cone;
    static ArrayMesh BeamCone()
    {
        if (_cone != null) return _cone;
        const int SEG = 20;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < SEG; i++)
        {
            float a0 = Mathf.Tau * i / SEG, a1 = Mathf.Tau * (i + 1) / SEG;
            void V(Vector3 p) { float k = 0.3f * (1 - p.Z) + 0.05f; st.SetColor(new Color(k, k, k)); st.AddVertex(p); }
            V(Vector3.Zero); V(new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 1)); V(new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 1));
        }
        return _cone = st.Commit();
    }

    void Rotor(Building b, float dt)
    {
        var (model, at, axis, speed) = ROTORS[b.Name];
        if (!_rotors.TryGetValue(b, out var r))
        {
            var m = G.Entities.ModelOf(b);
            if (m == null) return;
            r = Inst(model) ?? new Node3D();
            r.Position = at;
            m.Model.AddChild(r);
            _rotors[b] = r;
        }
        r.Rotate(axis, dt * speed);
    }

    // the power lines: a sagging wire between each pole and the one it hangs on (PowerGrid.Links)
    void Wires()
    {
        var links = R.Grid.Links;
        var key = string.Join(",", links.Select(l => l.A.Id + "-" + l.B.Id));
        if (key == _wireKey) return;
        _wireKey = key;
        var im = (ImmediateMesh)_wires.Mesh;
        im.ClearSurfaces();
        if (links.Count == 0) return;
        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        Vector3 Top(Building e) => new((float)e.Pos.X, Ground(e.Pos.X, e.Pos.Z) + (float)PowerGrid.AttachHeight(e), (float)e.Pos.Z);
        foreach (var (a, b) in links)
        {
            var p = Top(a); var q = Top(b);
            int n = 10; float sag = Math.Min(2.5f, p.DistanceTo(q) * 0.05f);
            float dx = q.Z - p.Z, dz = p.X - q.X, l = Mathf.Sqrt(dx * dx + dz * dz); if (l == 0) l = 1;
            foreach (var side in new[] { -0.6f, 0.6f })
            {
                Vector3? prev = null;
                for (int i = 0; i <= n; i++)
                {
                    float tt = i / (float)n;
                    var v = p.Lerp(q, tt);
                    v.Y -= sag * 4 * tt * (1 - tt);
                    v.X += dx / l * side; v.Z += dz / l * side;
                    if (prev is { } pv) { im.SurfaceSetColor(new Color(0.1f, 0.1f, 0.09f)); im.SurfaceAddVertex(pv); im.SurfaceSetColor(new Color(0.1f, 0.1f, 0.09f)); im.SurfaceAddVertex(v); }
                    prev = v;
                }
            }
        }
        im.SurfaceEnd();
    }

    /// <summary>a lightning arc for a moment (the electric weapons: "sqarc" events; a stabilizer at work)</summary>
    public void Arc(Vector3 a, Vector3 b, float life = 0.3f) => _arcs.Add((a, b, life));

    void Arcs(float dt)
    {
        for (int i = _arcs.Count - 1; i >= 0; i--) { var a = _arcs[i]; a.T -= dt; if (a.T <= 0) _arcs.RemoveAt(i); else _arcs[i] = a; }
        var im = (ImmediateMesh)_arcMesh.Mesh;
        im.ClearSurfaces();
        if (_arcs.Count == 0) return;
        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        var c = new Color(0.66f, 0.88f, 1f, 0.95f);
        foreach (var (p, q, _) in _arcs)
        {
            var prev = p;
            for (int i = 1; i <= 8; i++)
            {
                var v = p.Lerp(q, i / 8f);
                if (i < 8) v += new Vector3(_rng.Randf() - 0.5f, _rng.Randf() - 0.5f, _rng.Randf() - 0.5f) * 1.2f;
                im.SurfaceSetColor(c); im.SurfaceAddVertex(prev); im.SurfaceSetColor(c); im.SurfaceAddVertex(v);
                prev = v;
            }
        }
        im.SurfaceEnd();
    }

    // the set pieces: the big wheel's rim turning, boats on the river - drawn where the script has them
    void Pieces(float dt)
    {
        var live = new HashSet<SqPiece>();
        foreach (var p in R.Pieces)
        {
            live.Add(p);
            if (!_pieces.TryGetValue(p, out var o))
            {
                var inner = Inst(p.Model);
                if (inner == null) continue;
                var outer = new Node3D();
                outer.AddChild(inner);
                AddChild(outer);
                _pieces[p] = o = (outer, inner);
            }
            o.Outer.Position = new Vector3((float)p.X, p.Y is double y ? (float)y : Ground(p.X, p.Z), (float)p.Z);
            o.Outer.Rotation = new Vector3(0, (float)p.Rot, 0);
            if (p.Spin != 0) o.Inner.Rotate(Vector3.Back, dt * (float)p.Spin * Mathf.Tau);
        }
        foreach (var p in _pieces.Keys.Where(p => !live.Contains(p)).ToList()) { _pieces[p].Outer.QueueFree(); _pieces.Remove(p); }
    }

    /// <summary>mission 5: lift-off (the count reached zero)</summary>
    public void Launch() => _launched = true;

    // mission 5: the rocket on its pad, and its flight after the count reaches zero
    void Rocket(float dt)
    {
        var pad = W.Buildings.FirstOrDefault(b => b.Alive && b.Name == "sq_launch_pad");
        if (pad == null) return;
        if (_rocket == null)
        {
            _rocket = Inst("sq_rocket_body");
            if (_rocket == null) return;
            _rocketY = Ground(pad.Origin.X, pad.Origin.Z) + 5.0f;                  // on the launch table
            _rocket.Position = new Vector3((float)pad.Origin.X, _rocketY, (float)pad.Origin.Z);
            AddChild(_rocket);
        }
        if (_launched || R.IsDone("pad"))
        {
            _flight += dt;
            _rocket.Position = new Vector3(_rocket.Position.X, _rocketY + 2 * _flight * _flight, _rocket.Position.Z);
            if (G.Effects != null && _rng.Randf() < 0.6f) G.Effects.Fx.Dust(_rocket.Position, 4);
            if (_flight > 30) { _rocket.Visible = false; }
        }
    }

    // while a Rift Stabilizer is being placed: the reach of every open rift (green where the ghost is within it)
    void PlaceRings()
    {
        var gh = G.Input?.Mode == "place" ? G.Input.GhostInfo : null;
        var want = gh is { } g && R.Classes.Tagged(g.Name, "stabilizer") ? R.Rifts.Where(r => r.Open).ToList() : new List<SqRift>();
        bool Inside(SqRift r) => gh is { } g2 && Math.Sqrt((g2.X - r.X) * (g2.X - r.X) + (g2.Z - r.Z) * (g2.Z - r.Z)) < SequelRuntime.RIFT_RANGE;
        var key = string.Join("|", want.Select(r => r.Id + Inside(r)));
        if (key == _ringKey) return;
        _ringKey = key;
        var im = (ImmediateMesh)_rings.Mesh;
        im.ClearSurfaces();
        if (want.Count == 0) return;
        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var r in want)
        {
            var c = Inside(r) ? new Color(0.38f, 1f, 0.38f) : new Color(0.38f, 0.88f, 1f);
            double rr = SequelRuntime.RIFT_RANGE - 2;
            Vector3? prev = null;
            for (int i = 0; i <= 72; i++)
            {
                double a = i / 72.0 * Math.PI * 2, px = r.X + Math.Cos(a) * rr, pz = r.Z + Math.Sin(a) * rr;
                var v = new Vector3((float)px, Ground(px, pz) + 0.5f, (float)pz);
                if (prev is { } pv) { im.SurfaceSetColor(c); im.SurfaceAddVertex(pv); im.SurfaceSetColor(c); im.SurfaceAddVertex(v); }
                prev = v;
            }
        }
        im.SurfaceEnd();
    }
}
