// Building placement (ui/input.js makeGhost / updateGhost / updatePlaceGrid / wallPreview / wallRelease): the ghost of
// the building under the mouse (green where it may stand, red where not), turned by the wheel in 22.5 degree steps;
// the placement grid of round dots every 2 m around it (ObjPlaceChecker.EnableGridRendering, PlaceController.usl) -
// green where the building may be put down with its centre on that dot - to the nearest of which the ghost jumps
// (within 8 m) when the cursor's own spot does not fit; towers, gates and traps snap to their wall tile, coastal
// buildings to the nearest green dot on the shore line, turned to the sea (World.CoastalSnap). Wall lines: press = the line starts, drag = the line (PreviewWall: green / yellow when it
// cannot be paid / red), release = placed (Shift: keep the line as a leg and go on; Ctrl: stay in build mode).
// The placing itself is a command (PlaceBuildingCommand / PlaceWallCommand).
// Ported from remake/src/ui/input.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.View.Entities;
using PwRemake.View.Models;
using PwRemake.View.Parts;

namespace PwRemake.UI.PlayerInput;

public partial class GameInput
{
    const double PLACE_GRID_R = PwRemake.Sim.World.World.PLACE_GRID_R, PLACE_STEP = PwRemake.Sim.World.World.PLACE_STEP, PLACE_SNAP_R = 8;

    /// <summary>what is being placed now (the building's class and where), or null - a mode's own drawing reads it (the
    /// sequel's stabilizer reach: SequelFx)</summary>
    public (string Name, double X, double Z)? GhostInfo => _ghost == null ? null : (_ghost.Name, _ghost.X, _ghost.Z);
    /// <summary>the ghost's turn (radians) and whether it is drawn (on the ground under the mouse; a wall line's tiles are
    /// not this ghost) - the range circle of a shooting building being placed (RangeRings)</summary>
    public double GhostRot => _ghost?.Rot ?? 0;
    public bool GhostShown => _ghost is { Wall: false } g && g.Root.Visible;
    /// <summary>the ghost is green (a click would place it)</summary>
    public bool GhostOk => _ghost?.Ok == true;

    sealed class Ghost
    {
        public Node3D Root = null!;
        public ModelInstance? Obj;
        public ShaderMaterial Mat = null!;
        public ActionDef Action = null!;
        public string Name = "", Gfx = "";
        public double Rot, X, Z;
        public bool Ok, Wall, Active, Pressed;
        public (double X, double Z)? Start;
        public List<WallLineEntry> Held = new();
        public WallLine? Tiles;
        public readonly List<WallGhostItem> Pool = new();
        public MeshInstance3D? Grid;
        public string GridKey = "", AtKey = "";
        public Placement? At;
        public List<(double X, double Z)> OkDots = new();
    }
    Ghost? _ghost;
    // the ghost's material: a see-through shell in one colour. Two passes: the depth of the building's surfaces first
    // (nothing drawn), before every ghost's colour (render priority), then the colour where a surface is the nearest -
    // only the front surfaces are tinted. With one plain alpha pass (input.js: depthWrite false) every inner and back
    // face showed through, and the ghost looked like several buildings layered. The depth pass lies 4 cm behind the
    // surfaces: at the same depth the colour pass failed in spots in Forward+ (holes; the Compatibility renderer drew it
    // whole).
    static readonly Dictionary<string, ShaderMaterial> s_ghostMats = new();
    static Shader? s_ghostDepth;

    static ShaderMaterial GhostMat(int color, float alpha)
    {
        var k = color + ":" + alpha;
        if (!s_ghostMats.TryGetValue(k, out var m))
        {
            s_ghostDepth ??= new Shader
            {
                Code = @"shader_type spatial;
render_mode unshaded, blend_add, depth_draw_always, cull_disabled, shadows_disabled, fog_disabled;
void vertex() {
    vec3 cam = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
    VERTEX += normalize(VERTEX - cam) * 0.04;
}
void fragment() { ALBEDO = vec3(0.0); ALPHA = 0.0; }
",
            };
            s_ghostMats[k] = m = new ShaderMaterial
            {
                Shader = s_ghostDepth, RenderPriority = -2,
                NextPass = new StandardMaterial3D
                {
                    AlbedoColor = new Color(PwRemake.View.Entities.EntityViews.Hex(color), alpha), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha, DisableFog = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    RenderPriority = -1,
                },
            };
        }
        return m;
    }

    // the placement grid's dots: round points in their own colours, over everything
    static StandardMaterial3D PlaceGridMat() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, UsePointSize = true, PointSize = 9,
        NoDepthTest = true, DisableFog = true, RenderPriority = 30,
    };

    // ------------------------------------------------------------------------------------------------ the warm-up
    // The first ghost of a building used to hitch (user report: "a lag when we build a building for the first time, the
    // next time there is none"): its model loaded then (GameSession.Models.cs now loads the buildings the player can
    // build with the mission) and the graphics driver compiled the shaders of the ghost's materials (the two passes, the
    // dots, the hidden sprites) and of the building's own materials at their first drawing. WarmUp draws all of it once
    // when the game starts - out of sight: behind the camera, with a cull margin wide enough that nothing is culled
    // (the draw calls are made, so the shaders and pipelines are compiled; the pixels are clipped) - and removes it
    // after a few frames.
    Node3D? _warm;
    int _warmFrames;

    /// <summary>draws the ghost and the building of every building name once, out of sight (the game's first frames)</summary>
    public void WarmUpPlacement(IEnumerable<string> buildings)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cam = G.Renderer.Camera;
        _warm = new Node3D { Name = "PlacementWarmUp", Position = new Vector3(0, 0, 40) };      // (+z: behind the camera)
        int n = 0;
        var ghosts = new Node3D();
        var real = new Node3D();
        _warm.AddChild(ghosts); _warm.AddChild(real);
        foreach (var name in buildings)
        {
            var gm = GhostModel(name);
            if (gm == null) continue;
            gm.SetBuildingState(4, 0, G.Me.Epoch()); SetGhostLook(gm);
            ghosts.AddChild(gm);
            Paint(gm, GhostMat((n & 1) == 0 ? 0x60ff60 : 0xff4040, 0.45f));
            if (gm.Template is { } t)
            {
                var m = G.Models.Instantiate(t, G.Me.PartyColor is int pc ? EntityViews.Hex(pc) : null);
                m.SetBuildingState(4, 0, G.Me.Epoch());
                WarmLook(m);
                real.AddChild(m);
            }
            n++;
        }
        // the dots (points, vertex colours)
        var im = new ImmediateMesh();
        im.SurfaceBegin(Mesh.PrimitiveType.Points);
        im.SurfaceSetColor(new Color(0.25f, 1, 0.3f)); im.SurfaceAddVertex(Vector3.Zero);
        im.SurfaceSetColor(new Color(1, 0.2f, 0.15f)); im.SurfaceAddVertex(Vector3.Up);
        im.SurfaceEnd();
        _warm.AddChild(new MeshInstance3D { Mesh = im, MaterialOverride = PlaceGridMat(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        NoCull(_warm);
        cam.AddChild(_warm);
        _warmFrames = 0;
        L.Info($"placement warm-up: {n} buildings, ghost and model, drawn once ({sw.Elapsed.TotalMilliseconds:0} ms to set up)");
    }

    // (the real building: its own materials; no lights, emitters, clips' particles, and no shadows - behind the camera
    // they could fall into the view; the shadow pass of the buildings' materials is compiled by the map's buildings)
    void WarmLook(ModelInstance m)
    {
        static void Walk(Node n)
        {
            if (n is GeometryInstance3D gi) gi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            if (n is Light3D l) l.Visible = false;
            if (n is GpuParticles3D gp) gp.Emitting = false;
            if (n is ModelInstance mi) mi.Sprays = false;
            foreach (var c in n.GetChildren()) Walk(c);
        }
        Walk(m);
        G.View?.Particles?.Untrack(m);
    }

    static void NoCull(Node n)
    {
        if (n is GeometryInstance3D gi) gi.ExtraCullMargin = 16384;
        foreach (var c in n.GetChildren()) NoCull(c);
    }

    // the warm-up's objects go after three frames (drawn in the first ones)
    void StepWarmUp()
    {
        if (_warm == null || ++_warmFrames < 3) return;
        _warm.QueueFree();
        _warm = null;
    }

    static void Paint(Node n, Material m)
    {
        // (the model's sprites - lanterns, banners, the grass at its foot - are not drawn: billboards whose shader the
        // ghost material would replace; a material that draws nothing, not Visible, which the part states switch with
        // the building's state - and no depth, which cut holes into the ghost)
        if (n is MultiMeshInstance3D sp) { sp.MaterialOverride = s_noSprite ??= new ShaderMaterial { Shader = new Shader { Code = "shader_type spatial;\nrender_mode unshaded, shadows_disabled;\nvoid fragment() { discard; }\n" } }; sp.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off; }
        else if (n is GeometryInstance3D gi) { gi.MaterialOverride = m; gi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off; }
        foreach (var c in n.GetChildren()) Paint(c, m);
    }
    static ShaderMaterial? s_noSprite;

    // the model the building will have: the tech tree's, the class file's, a mission's stand-in; none: a box
    ModelInstance? GhostModel(string name)
    {
        var st = W.Data.Stats(name, 1, G.Me);
        foreach (var g in new[] { st?.Gfx, W.Data.ClassGfx(name), W.StandIns.GetValueOrDefault(name.ToLowerInvariant()) })
            if (!string.IsNullOrEmpty(g) && EntityViews.HasFile(G.Models, g))
                return G.Models.Template(g.ToLowerInvariant(), G.Entities.OptionsOf(g), true) is { } t ? G.Models.Instantiate(t) : null;
        return null;
    }

    void MakeGhost(ActionDef action)
    {
        ClearGhost();
        var name = action.Results.FirstOrDefault()?.Obj ?? "";
        var def = W.Data.Def(name, G.Me);
        var gh = new Ghost { Action = action, Name = name, Root = new Node3D { Name = "PlacementGhost" } };
        gh.Mat = GhostMat(0x60ff60, 0.45f);
        gh.Obj = GhostModel(name);
        if (gh.Obj != null) { gh.Obj.SetBuildingState(4, 0, G.Me.Epoch()); SetGhostLook(gh.Obj); gh.Root.AddChild(gh.Obj); }
        else gh.Root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(6, 4, 6) }, Position = new Vector3(0, 2, 0) });
        Paint(gh.Root, gh.Mat);
        G.AddChild(gh.Root);
        // wall pieces: one preview per tile of the line; the single ghost is hidden (traps are dragged as lines too)
        gh.Wall = def != null && (def.WallKind == "wall" || def.WallKind == "trap");
        if (gh.Wall) gh.Root.Visible = false;
        // the placement grid (ObjPlaceChecker.EnableGridRendering): not for wall lines (their tiles are the preview);
        // shore buildings too - green where one may stand on the shore with its centre on the dot (World.CoastalAt)
        if (!gh.Wall)
        {
            gh.Grid = new MeshInstance3D
            {
                Name = "PlaceGrid", Mesh = new ImmediateMesh(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = PlaceGridMat(),
            };
            G.AddChild(gh.Grid);
        }
        _ghost = gh;
    }

    void ClearGhost()
    {
        if (_ghost == null) return;
        _ghost.Root.QueueFree();
        _ghost.Grid?.QueueFree();
        foreach (var p in _ghost.Pool) p.Obj.QueueFree();
        _ghost = null;
    }

    void UpdateGhost()
    {
        var gh = _ghost;
        if (gh == null) return;
        if (gh.Wall) { WallPreview(); return; }
        if (GroundPoint(_mouse) is not Vector3 g) { gh.Root.Visible = false; gh.Ok = false; if (gh.Grid != null) gh.Grid.Visible = false; return; }
        gh.Root.Visible = true;
        // a building with a placement grid stands on its dots (every 2 m): the ghost's colour is the colour of the dot
        // it stands on, and both are the simulation's placement rule for that spot
        if (gh.Grid != null) { gh.X = Math.Round(g.X / PLACE_STEP) * PLACE_STEP; gh.Z = Math.Round(g.Z / PLACE_STEP) * PLACE_STEP; }
        else { gh.X = Math.Round(g.X); gh.Z = Math.Round(g.Z); }
        // towers and traps go onto wall tiles: pointing at a wall piece means its tile, not the ground behind it
        var gdef = W.Data.Def(gh.Name, G.Me);
        var kind = gdef?.WallKind; bool ladder = gdef?.Ladder == true;
        (double X, double Z)? cursor = null;          // the ground under the cursor, when the ghost snapped to a wall piece
        if (kind is "tower" or "trap" or "gate" || ladder)
        {
            var e = EntityAt(_mouse);
            Building? w = e is Building eb && eb.Def?.WallKind == "wall" && (eb.Owner == G.Me || ladder) ? eb : null;
            if (w == null && !ladder && W.Walls is { } WM) { var (ti, tj) = WM.TileOf(gh.X, gh.Z); if (WM.At(ti, tj)?.Wall is { Alive: true } tw && tw.Owner == G.Me) w = tw; }
            if (w != null) { cursor = (gh.X, gh.Z); gh.X = w.Pos.X; gh.Z = w.Pos.Z; }
        }
        // coastal buildings snap to the shore: show the ghost where it will really stand
        var ws = MyUnits().Where(u => u.CanBuild).ToList();
        (double, double)? from = ws.Count > 0 ? (ws.Average(u => u.Pos.X), ws.Average(u => u.Pos.Z)) : null;
        if (gh.Grid != null) UpdatePlaceGrid(gh, gh.X, gh.Z);
        Placement at;
        if (gdef?.Coastal == true)
        {
            // (the shore snap looks at up to some hundred dots: once per 2 m cell the cursor is in)
            var ck = $"{gh.X},{gh.Z},{W.Nav.Version},{W.Buildings.Count}";
            if (gh.AtKey != ck || gh.At == null) { gh.AtKey = ck; gh.At = W.PlacementOf(gh.Name, gh.X, gh.Z, gh.Rot, G.Me, from); }
            at = gh.At;
        }
        else at = W.PlacementOf(gh.Name, gh.X, gh.Z, gh.Rot, G.Me, from);
        // a gate pointed at the end piece of a gap (the gap a destroyed gate left): the wall piece's tile takes no gate,
        // the ground under the cursor - the gap - may (the ghost then snaps into the gap)
        if (!at.Ok && kind == "gate" && cursor is var (ux, uz))
        {
            var atc = W.PlacementOf(gh.Name, ux, uz, gh.Rot, G.Me, from);
            if (atc.Ok) at = atc;
        }
        if (!at.Ok && gh.Grid != null && gh.OkDots.Count > 0 && kind == null)
        {
            // the spot under the cursor does not fit: the nearest green dot within PLACE_SNAP_R takes it
            (double X, double Z)? best = null; double bd = PLACE_SNAP_R;
            foreach (var (x, z) in gh.OkDots) { double d = Math.Sqrt((x - gh.X) * (x - gh.X) + (z - gh.Z) * (z - gh.Z)); if (d <= bd) { bd = d; best = (x, z); } }
            if (best is var (bx, bz))
            {
                var at2 = W.PlacementOf(gh.Name, bx, bz, gh.Rot, G.Me, from);
                if (at2.Ok) { at = at2; gh.X = bx; gh.Z = bz; }
            }
        }
        gh.X = at.X; gh.Z = at.Z; gh.Rot = at.Rot;
        double y = Math.Max(W.Height(gh.X, gh.Z), W.WaterLevel is double wl && gdef?.Coastal == true ? wl + 1.5 : -1e9);
        // wall-map objects stand at the tile's quantised height; a gate shows its end pieces only on a diagonal
        if (kind is "gate" or "tower" && W.Walls is { } WM2) { var (ti, tj) = WM2.TileOf(gh.X, gh.Z); y = WM2.ZOf(ti, tj); }
        if (kind == "gate" && at.Fit != null && gh.Obj != null) { var st = new BuildingState(4, 0, G.Me.Epoch()) { GateEnds = at.Fit.D % 2 == 0 }; gh.Obj.SetBuildingState(st); Paint(gh.Root, gh.Mat); }
        gh.Root.Position = new Vector3((float)gh.X, (float)y, (float)gh.Z);
        gh.Root.Rotation = new Vector3(0, (float)gh.Rot, 0);
        gh.Ok = at.Ok && G.Fog.Explored(gh.X, gh.Z);
        var m = GhostMat(gh.Ok ? 0x60ff60 : 0xff4040, 0.45f);
        if (m != gh.Mat) { gh.Mat = m; Paint(gh.Root, m); }
    }

    // the dots of the placement grid around the ghost: a dot every 2 m, green where the building may be put down with
    // its centre on that dot (turned as it is now), red where not; worked out again when the ghost moves to another
    // 2 m cell or turns
    void UpdatePlaceGrid(Ghost gh, double x0, double z0)
    {
        double S = PLACE_STEP;
        double cx = Math.Round(x0 / S) * S, cz = Math.Round(z0 / S) * S;
        var key = $"{cx},{cz},{gh.Rot},{W.Nav.Version},{W.Buildings.Count}";
        gh.Grid!.Visible = gh.Root.Visible;
        if (gh.GridKey == key) return;
        gh.GridKey = key;
        var im = (ImmediateMesh)gh.Grid.Mesh;
        im.ClearSurfaces();
        gh.OkDots = new();
        double R = PLACE_GRID_R;
        bool any = false;
        // the simulation's own placement rule per dot (World.PlaceGridOf -> PlaceSpotOk = PlacementOf(...).Ok, what
        // StartConstruction checks); only the unexplored ground is the input's own rule, for the ghost too
        foreach (var (x, z, sok) in W.PlaceGridOf(gh.Name, cx, cz, gh.Rot, G.Me, R))
        {
            bool ok = sok && G.Fog.Explored(x, z);
            if (ok) gh.OkDots.Add((x, z));
            if (!any) { im.SurfaceBegin(Mesh.PrimitiveType.Points); any = true; }
            double d = Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
            float fade = (float)Math.Min(1, (R - d) / 6 + 0.25);       // fading towards the edge of the grid
            im.SurfaceSetColor(ok ? new Color(0.25f * fade, 1.0f * fade, 0.3f * fade) : new Color(1.0f * fade, 0.2f * fade, 0.15f * fade));
            im.SurfaceAddVertex(new Vector3((float)x, (float)W.Height(x, z) + 0.4f, (float)z));
        }
        if (any) im.SurfaceEnd();
    }

    /// <summary>the building last placed (G: place another of it - the remake's own key)</summary>
    public ActionDef? LastPlaced;

    /// <summary>G: the last placed building's ghost again (needs a builder selected, its requirements and its price)</summary>
    void PlaceLast()
    {
        var a = LastPlaced;
        if (a == null) { G.Message("No building placed yet", "bad"); G.PlayError(); return; }
        if (!MyUnits().Any(u => u.CanBuild)) { G.Message("Select a worker to build", "bad"); G.PlayError(); return; }
        if (W.Data.Check(G.Me!, a) != null) { G.Message(G.Reason("req"), "bad"); G.PlayError(); return; }
        if (W.LimitWhy(G.Me, a) is { } lim) { G.Message(G.Reason(lim), "bad"); G.PlayError(); return; }      // (MIRAGE's building limits)
        if (!G.Me!.CanAfford(a.Cost)) { G.Message(G.Reason("cost"), "bad"); G.PlayError(); return; }
        G.PlayUi("UI_click");
        SetMode("place", a);
    }

    void PlaceClick(bool shift)
    {
        var gh = _ghost;
        if (gh == null || !gh.Ok) { G.Message("Cannot build here", "bad"); G.PlayError(); return; }
        var workers = MyUnits().Where(u => u.CanBuild).ToList();      // (Taslow and the Dustriders' fighters build too)
        var a = gh.Action;
        G.Submit(new PlaceBuildingCommand { Action = a.Path, X = gh.X, Z = gh.Z, Rot = gh.Rot, Workers = Ids(workers) }, why =>
        {
            if (why != null) { G.Message(G.Reason(why), "bad"); G.PlayError(); return; }
            LastPlaced = a;
            if (workers.Count > 0) G.PlayOrdered("build", workers);
            else G.Message("Select workers and right-click the foundation to build it");
        });
        if (!shift) SetMode(null);
    }

    // ---- wall lines (docs/spec/walls.md §3): hover = one tile under the cursor; press = start; drag = the line;
    // release = place (Shift: keep the line as a leg and go on from here; Ctrl: stay in build mode); right click = cancel
    void WallPreview()
    {
        var gh = _ghost!;
        if (GroundPoint(_mouse) is not Vector3 g) return;
        var s = gh.Active && gh.Start is var (sx, sz) ? (sx, sz) : (g.X, g.Z);
        gh.Tiles = W.WallLineOf(G.Me, gh.Action, s.Item1, s.Item2, g.X, g.Z, gh.Held);
        // PreviewWall (0x4f9f70): the new tiles and the own wall pieces next to them, each with the parts it will have
        // (a wall model shows only the parts its tile names: without them a ghost of a piece showed nothing); no line
        // (CalcWall false): nothing is shown
        var list = W.WallPreviewOf(G.Me, gh.Tiles);
        int k = 0;
        foreach (var t in list)
        {
            var name = t.Name ?? gh.Name;
            if (k >= gh.Pool.Count || gh.Pool[k].Name != name)
            {
                var o = GhostModel(name) ?? (name != gh.Name ? GhostModel(gh.Name) : null);
                if (o == null) continue;
                SetGhostLook(o);
                G.AddChild(o);
                var item = new WallGhostItem { Obj = o, Name = name };
                if (k < gh.Pool.Count) { gh.Pool[k].Obj.QueueFree(); gh.Pool[k] = item; } else gh.Pool.Add(item);
            }
            var p = gh.Pool[k++];
            var st = new BuildingState(4, 0, G.Me.Epoch());
            if (t.Parts != null) st.WallParts = GhostPartSet(t.Parts);
            p.Obj.SetBuildingState(st);
            if (t.State != p.State) { Paint(p.Obj, GhostMat(WALL_COL.GetValueOrDefault(t.State, 0x60ff60), 0.5f)); p.State = t.State; }
            p.Obj.Position = new Vector3((float)t.X, (float)t.Y, (float)t.Z);
            p.Obj.Rotation = new Vector3(0, t.Parts != null ? (float)Building.WALL_ROT : 0f, 0);
            p.Obj.Visible = true;
        }
        for (int n = k; n < gh.Pool.Count; n++) gh.Pool[n].Obj.Visible = false;
    }

    static readonly Dictionary<string, int> WALL_COL = new() { ["ok"] = 0x60ff60, ["have"] = 0x60ff60, ["cost"] = 0xffe060, ["bad"] = 0xff4040, ["gate"] = 0x00ffff };

    sealed class WallGhostItem
    {
        public ModelInstance Obj = null!;
        public string Name = "", State = "";
    }

    // the part sets by their list (BuildingState compares them by reference: one set per list, applied only on change)
    readonly Dictionary<string, HashSet<string>> _ghostParts = new();
    HashSet<string> GhostPartSet(List<string> parts)
    {
        var key = string.Join(",", parts);
        if (!_ghostParts.TryGetValue(key, out var s)) _ghostParts[key] = s = new HashSet<string>(parts);
        return s;
    }

    // a ghost is only looked at - the building's shape in the ghost colour: no shadows, no lights, no smoke, fire,
    // steam or fans (the model emitters: the SEAS buildings' fans are emitters too, a turning sprite of
    // particle_09 - it turned dark on the green ghost: user report); no sprites (Paint). input.js makeGhost: untrack.
    void SetGhostLook(ModelInstance m)
    {
        GhostLook(m);
        G.View?.Particles?.Untrack(m);
    }

    static void GhostLook(Node n)
    {
        if (n is GeometryInstance3D gi) gi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        if (n is Light3D l) l.Visible = false;
        if (n is GpuParticles3D gp) gp.Emitting = false;
        if (n is PwRemake.View.Models.ModelInstance mi) mi.Sprays = false;      // (its model emitters: View/Fx/Particles)
        foreach (var c in n.GetChildren()) GhostLook(c);
    }

    /// <summary>the ghost's model (null: none, or a box)</summary>
    public ModelInstance? GhostObj => _ghost?.Obj;

    /// <summary>what of the ghost is more than the building's shape in the ghost colour (tests: an empty list)</summary>
    public List<string> GhostLeaks()
    {
        var r = new List<string>();
        if (_ghost?.Obj is not { } m) return r;
        if (m.Sprays) r.Add("its emitters spray");
        if (G.View?.Particles is { } P && P.Tracks(m)) r.Add("its emitters are tracked");
        void Walk(Node n)
        {
            if (n is Node3D n3 && !n3.Visible) return;
            if (n is MultiMeshInstance3D mm) { if (mm.MaterialOverride != s_noSprite) r.Add("sprites " + n.Name); }
            else if (n is GeometryInstance3D gi && gi.MaterialOverride != _ghost.Mat) r.Add("own material " + n.Name);
            if (n is GeometryInstance3D g2 && g2.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) r.Add("shadow " + n.Name);
            if (n is Light3D) r.Add("light " + n.Name);
            foreach (var c in n.GetChildren()) Walk(c);
        }
        Walk(m);
        if (m.Anim is { } a && a.IsPlaying) r.Add("a clip plays: " + a.CurName);
        return r;
    }

    void WallRelease(bool shift, bool ctrl)
    {
        var gh = _ghost;
        if (gh == null || !gh.Active) return;
        WallPreview();
        var tiles = gh.Tiles;
        var last = tiles != null && tiles.Count > 0 ? tiles[^1] : null;
        // Shift: the line so far is frozen as a leg, the next one starts here (PlaceController: m_iNumTilesHold)
        if (shift && last != null) { gh.Held = tiles!.ToList(); gh.Start = (last.X, last.Z); return; }
        if (tiles == null || tiles.Count == 0) { G.Message("The wall cannot be built here", "bad"); G.PlayError(); return; }
        var builders = MyUnits().Where(u => u.CanBuild).ToList();
        G.Submit(new PlaceWallCommand
        {
            Action = gh.Action.Path, Tiles = tiles.Select(t => (t.I, t.J, t.X, t.Z, t.State == "have")).ToList(), Workers = Ids(builders), Queued = ctrl,
        }, why =>
        {
            if (why != null) { G.Message(G.Reason(why), "bad"); G.PlayError(); return; }
            LastPlaced = gh.Action;
            if (builders.Count > 0) G.PlayOrdered("build", builders);
            else G.Message("Select workers and right-click the foundation to build it");
        });
        gh.Active = false; gh.Held = new(); gh.Start = null;
        if (!ctrl) SetMode(null);
    }
}
