// The particle emitters of the models (GSF chunk type 2: fires, smoke, sparks, the spray of waterfalls, hits ...).
// The converter writes one node per emitter with its data in the extras (EmitterDef; ModelLibrary reads it into the
// template's Emitters).
//
// Particle source 3, ported from PWClient.exe (the browser remake's docs/spec/particles.md; InitPart 0x709740,
// ProcPart 0x707e30, Process 0x704a10). The emitter's vector ("scale" in the file) is its velocity: particles start on
// a disk of radius x in the emitter's plane (sphere, hemisphere, ring by flag) and fly off with 0.7 z along its axis,
// tilted outwards by "spread"; drag, then gravitation (world units, positive = down), centre suction; size grows by
// size_increase per second; fade_in / fade_out are shares of the life; flag 0x80 starts all particles together (a
// burst). The particles live in the emitter's frame (they move with their model) unless flag 0x80000 puts them in the
// world. The game draws a sprite a quarter of "size" high.
//
// Emitters are tracked when a model is instantiated (ModelLibrary.OnInstance -> Track) or placed as a landscape
// object (PropField -> AddStatic). Only emitters near the camera spray; a hidden node (building state, epoch, damage
// stage: PartStates) or a detached model sprays nothing. A weapon's flash sprays only while its wielder attacks.
// The simulation runs on the CPU as the original's does (every slot of a source each frame); the sprites are drawn as
// one MultiMesh per sheet, grid and blending (ParticleBatch), capped by the "Particles" setting (off / low / on /
// high: a number of sprites and a share of the screen to fill).
//
// Ported from remake/src/engine/particles.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.Sim.Core;
using PwRemake.View.Anim;
using PwRemake.View.Materials;
using PwRemake.View.Models;

namespace PwRemake.View.Fx;

public partial class Particles : Node3D
{
    static readonly PwRemake.Sim.Core.Logger L = Log.For("View.Particles");
    const float RANGE = 160;                 // m from the camera target
    const float SRC_OFFSET = 120, SRC_DIST = 100;     // config ParticleSrcOffset / ParticleSrcDist (Rendering, 0x7096ba)
    // what the graphics card is asked to fill per frame, in screen areas (sprites close to the camera cover it all;
    // thousands of them made a graphics card reset in the browser), and the sprites at most
    static readonly Dictionary<string, (float Fill, int Sprites)> LEVELS = new()
    {
        ["low"] = (3, 800), ["on"] = (8, 3000), ["high"] = (16, 6000),
    };
    const float MAX_SHARE = 0.5f;
    // attribute bit 16 on the emitters of weapons (the turrets' and guns' muzzle flashes and shells, the gatlings', the
    // flamers', the staffs' rays): they spray while their wielder attacks (an attack or shoot clip plays). On models that
    // do not attack (ruins, gates) the bit means something else and is ignored.
    const uint FIRING = 1u << 16;
    static readonly Regex ATTACK = new("attack|shoot", RegexOptions.Compiled);
    /// <summary>the weapons' attack clips of the rules (Rules.WeaponClips: "seas_flamethrower_0", "seas_gunner_0",
    /// "marksman_gatling" ... - not named "attack"); with ATTACK they tell a wielder that fires</summary>
    public static HashSet<string> WeaponClips = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>a clip of an attack: "attack..." / "shoot..." or a weapon's clip of the rules</summary>
    public static bool IsAttackClip(string? clip) => clip != null && (ATTACK.IsMatch(clip) || WeaponClips.Contains(clip));

    // flags of particle source 3 (parameter "flags"; docs/spec/particles.md)
    public const uint SPHERE = 0x1, SPHERE_VEL = 0x2, KEEP_POS = 0x4, KEEP_ROT = 0x8, KEEP_SIZE = 0x10, KEEP_TILE = 0x20,
        RADIAL_VEL = 0x40, BURST = 0x80, TILE_FWD = 0x100, RING = 0x200, TILE_BACK = 0x400, TILE_PING = 0x800,
        TILE_PONG = 0x1000, KEEP_COLOR = 0x2000, FADE_ALPHA = 0x4000, FADE_BOTH = 0x8000, RADIAL_H = 0x10000,
        UP_ONLY = 0x20000, BELOW = 0x40000, WORLD = 0x80000, SUCK_ORIGIN = 0x200000, ROT_ORIGIN = 0x400000,
        HEMI = 0x800000, NO_SPREAD = 0x1000000, STILL = 0x2000000, LOCAL_GRAV = 0x4000000, EDGE_SPEED = 0x8000000,
        NO_Y = 0x10000000, ALONG_VEL = 0x20000000, GROUND = 0x40000000;

    /// <summary>one particle slot of a source</summary>
    public struct Part
    {
        public Vector3 P, V, Dv, Inh;
        public float Age, Life, Size, Rot, Rv, A;
        public float Br, Bg, Bb, Cr, Cg, Cb;
        public int Tile0, Tile;
        public bool Vis, Emitted, World;
    }

    /// <summary>an emitter of a model instance (Node) or of a landscape object (Matrix)</summary>
    public sealed class Emitter
    {
        public Node3D? Node;
        public ModelInstance? Root;
        public Transform3D Matrix;
        public EmitterDef E = null!;
        public uint F;
        public string Name = "";
        public Part[] Parts = Array.Empty<Part>();
        public int Live;
        public bool Started, Dead, Fire, BoneFire;
        public float Idle, ChkT, Timer, Carry, RotAcc;
        public float? K;
        public float Pivot;
        public int Lod;
        public Vector3? LastW;
        public Vector3 Md, Sv, Gv, Gvw;
        public bool HasG;
        // asked once: the model's animation (anims), the wielder (weapon bit), the attack bone (no weapon bit)
        public bool CtlAsked; public AnimController? Ctl;
        public bool WielderAsked; public AnimController? Wielder;
        public int AtkTries; public bool? AtkBone;
        public bool InModelFrame;
        /// <summary>the node's own turn (the "unturned" velocities), kept for when its model is gone</summary>
        public Quaternion Q = Quaternion.Identity;
    }

    readonly List<Emitter> _emitters = new();
    readonly List<Emitter> _dormant = new();
    int _wakeAt;
    bool _failed;
    readonly Dictionary<string, ParticleBatch?> _batches = new();
    readonly Random _rnd = new();
    TextureCache? _tex;
    /// <summary>off | low | on | high (the Options' "Particle effects")</summary>
    public string Level = "on";
    public bool Enabled = true;
    /// <summary>(x, z) => false: a place the player has not explored (fog of war) sprays nothing</summary>
    public Func<double, double, bool>? Seen;
    /// <summary>sprites drawn in the last frame, the load factor (0x706b9f), emitters active / dormant</summary>
    public int Drawn { get; private set; }
    public float Load { get; private set; } = 1;
    public int Active => _emitters.Count;
    /// <summary>the live particles of a model's emitters whose node is named so (tests)</summary>
    public int LiveOf(ModelInstance m, string node) => _emitters.Where(x => x.Root == m && x.Node?.Name == node).Sum(x => x.Live);
    public int DormantCount => _dormant.Count;
    public IEnumerable<ParticleBatch> Batches { get { foreach (var b in _batches.Values) if (b != null) yield return b; } }
    /// <summary>the scene's linear fog (start, end): particles fade into it and are not drawn beyond (0x705191)</summary>
    public float FogStart = 260, FogEnd = 620;

    public Particles(TextureCache? textures = null) { Name = "Particles"; _tex = textures; }
    public void SetTextures(TextureCache t) => _tex = t;

    // ------------------------------------------------------------------------------------------------ tracking

    /// <summary>a model instance: its emitters follow their nodes (assets.js cloneModel -> track)</summary>
    public void Track(ModelInstance m)
    {
        _tex ??= m.Library.Textures;
        foreach (var p in m.Template.Emitters)
        {
            var node = m.Root.GetNodeOrNull<Node3D>(p.Path);
            if (node == null) continue;
            Add(new Emitter { Node = node, Root = m, Name = m.Template.Name + "/" + node.Name, Q = node.Quaternion }, p);
        }
    }

    /// <summary>a model whose emitters spray nothing (a placement preview): they are dropped at once</summary>
    public void Untrack(ModelInstance m)
    {
        foreach (var x in _emitters) if (x.Root == m) { x.Dead = true; x.Parts = Array.Empty<Part>(); }
        foreach (var x in _dormant) if (x.Root == m) x.Dead = true;
    }

    /// <summary>emitters of this model are tracked and not dropped (tests: a placement ghost's are not)</summary>
    public bool Tracks(ModelInstance m)
    {
        foreach (var x in _emitters) if (x.Root == m && !x.Dead) return true;
        foreach (var x in _dormant) if (x.Root == m && !x.Dead) return true;
        return false;
    }

    /// <summary>a landscape object (PropField): the template's emitters shown in its default look, placed with the
    /// instance's transform; returns the handle for RemoveStatic</summary>
    public List<Emitter>? AddStatic(ModelTemplate tpl, Transform3D xf)
    {
        if (tpl.Emitters.Count == 0) return null;
        var list = new List<Emitter>();
        foreach (var p in tpl.Emitters)
        {
            var node = tpl.Scene.GetNodeOrNull<Node3D>(p.Path);
            if (node == null) continue;
            bool shown = true;
            for (Node? o = node; o != null; o = o.GetParent()) if (o is Node3D n3 && !n3.Visible) shown = false;
            if (!shown) continue;                                    // another state or epoch of the object
            var x = Add(new Emitter { Matrix = xf * ModelLibrary.ToRoot(node, null), Name = tpl.Name + "/" + node.Name }, p);
            if (x.E != null && x.E.Anims != null) continue;        // a landscape object plays no animation
            list.Add(x);
        }
        return list;
    }

    public void RemoveStatic(List<Emitter>? list) { if (list != null) foreach (var x in list) x.Dead = true; }

    Emitter Add(Emitter x, PartRef p)
    {
        uint attr = p.HasAttr ? p.Attr : 0;
        if ((attr & 0x1f) != 0 && (attr & 1) == 0) return x;          // a lower level of detail
        var e = p.Emitter!;
        x.E = e;
        x.F = e.Flags;
        x.Fire = (attr & FIRING) != 0 && x.Node != null;
        x.BoneFire = (attr & FIRING) == 0 && x.Node != null && e.Anims == null;
        x.Pivot = ParticleBatch.PackPivot((e.MidX - 127) / 127f, (e.MidY - 127) / 127f);
        // the outward tilt of the default velocity (constructor 0x707d06); a spread of -pi/2 or less: none
        float X = Math.Max(e.ScaleX, 0.01f);
        x.K = e.Spread > -Math.PI / 2 ? (float)(Math.Sqrt(0.49 * e.ScaleZ * e.ScaleZ + X * X) * Math.Sin(e.Spread) / X) : null;
        // (the node hangs from the glTF root: its translation is in the model's own z-up frame)
        x.InModelFrame = x.Node != null && x.Node.GetParent() is Node3D par && par.Name == "Model";
        if (e.Anims != null && x.Node == null) return x;              // a landscape object plays no animation
        _emitters.Add(x);
        return x;
    }

    // ------------------------------------------------------------------------------------------------ batches

    ParticleBatch? Batch(EmitterDef e)
    {
        var key = e.Tex + "|" + e.GridW + "x" + e.GridH + "|" + e.Additive;
        if (_batches.TryGetValue(key, out var b)) return b;
        var tex = _tex?.Get(e.Tex);
        if (tex == null) { L.WarnOnce("tex:" + e.Tex, $"particle texture {e.Tex} is missing"); _batches[key] = null; return null; }
        b = new ParticleBatch(tex, e.GridW, e.GridH, e.Additive) { TopLevel = true };
        AddChild(b);
        _batches[key] = b;
        return b;
    }

    // ------------------------------------------------------------------------------------------------ the frame

    static bool Attached(Node3D? n) => n != null && GodotObject.IsInstanceValid(n) && n.IsInsideTree();
    static bool Shown(Node3D n) => n.IsVisibleInTree();
    bool Sprays(Emitter x) => x.Root == null || (GodotObject.IsInstanceValid(x.Root) && x.Root.Sprays);

    // the dormant emitters (their node hidden by its model's state: the fires of the other damage stages and epochs,
    // the parts of wall pieces they don't show - thousands on a map with walls): looked at in slices, a fifteenth of
    // them per frame - back to the active list when the node shows again, gone when its model left the scene
    void Wake()
    {
        var D = _dormant;
        int n = D.Count;
        if (n == 0) return;
        int k = Math.Min(n, Math.Max(400, (n + 14) / 15));
        for (int c = 0; c < k && D.Count > 0; c++)
        {
            if (_wakeAt >= D.Count) _wakeAt = 0;
            int i = _wakeAt;
            var x = D[i];
            void Out() { D[i] = D[^1]; D.RemoveAt(D.Count - 1); }
            if (x.Dead || !Attached(x.Node)) { Out(); continue; }
            if (x.Node!.Visible && Shown(x.Node)) { Out(); _emitters.Add(x); continue; }
            _wakeAt++;
        }
    }

    /// <summary>
    /// one frame (after the entities moved): dt the frame's time; camera; target: the point the camera looks at.
    /// </summary>
    public void Update(double dtD, Camera3D camera, Vector3 target)
    {
        float dt = (float)dtD;
        if (!Enabled || !LEVELS.TryGetValue(Level, out var lv))
        {
            foreach (var b in _batches.Values) b?.Flush(0);
            Drawn = 0;
            Wake();
            return;
        }
        var vp = camera.GetViewport().GetVisibleRect().Size;
        float H = Math.Max(1, vp.Y), W = Math.Max(1, vp.X);
        float scale = camera.Projection == Camera3D.ProjectionType.Perspective ? H / (2 * Mathf.Tan(Mathf.DegToRad(camera.Fov) / 2)) : 500;
        float maxPx = H * MAX_SHARE;
        float budgetArea = lv.Fill * H * W;
        int budgetN = lv.Sprites;
        var eye = camera.GlobalPosition;
        var gp = camera.GetFrustum();
        if (_planes.Length != gp.Count) _planes = new Plane[gp.Count];
        for (int k = 0; k < gp.Count; k++) _planes[k] = gp[k];
        var planes = _planes;
        var view = camera.GlobalTransform.AffineInverse().Basis;
        // the load of the last frame (0x706b9f): above half the limit the sources hold back
        int last = Drawn, lim = lv.Sprites;
        Load = last >= lim / 2 ? 1 - Math.Clamp((last / (float)lim - 0.75f) * 4, 0, 1) : 1;
        Wake();
        // ---- every emitter near the camera target steps (also those out of the view: their smoke is there when the
        // camera turns to them); the ones with sprites are drawn below
        var list = _emitters;
        var draw = _draw;
        draw.Clear();
        int kept = 0, i = 0;
        try
        {
            for (; i < list.Count; i++)
            {
                var x = list[i];
                if (x.Dead) continue;
                bool att = x.Node != null && Attached(x.Node);
                if (x.Node != null && !att && x.Parts.Length == 0) continue;     // its model is gone
                // a node its model's state hides (the fires of the other damage stages, the other epochs, the parts of
                // a wall piece it does not show - most of the thousands): nothing to do but wait (Wake)
                if (att && x.Parts.Length == 0 && !x.Node!.Visible) { _dormant.Add(x); continue; }
                var M = att ? x.Node!.GlobalTransform : x.Matrix;
                if (att) { x.Matrix = M; x.Q = x.Node!.Quaternion; }     // (its last place and turn, should the model go)
                var v = M.Origin;
                float dTarget = v.DistanceTo(target);
                // far from the camera with nothing in flight: only a look every 2 s whether its model is still there
                if (x.Parts.Length == 0 && dTarget > RANGE)
                {
                    if ((x.ChkT -= dt) <= 0) { x.ChkT = 2; if (x.Node != null && !att) continue; }
                    list[kept++] = x;
                    continue;
                }
                bool on = Sprays(x);
                if (!on && x.Parts.Length == 0) continue;               // (dropped: a preview)
                if (x.Node != null)
                {
                    bool shown = att && Shown(x.Node);
                    if (!shown) { x.Idle += dt; if (x.Idle > 10 && x.Parts.Length == 0 && !att) continue; on = false; }
                    else x.Idle = 0;
                }
                list[kept++] = x;
                if (on && Seen != null && !Seen(v.X, v.Z)) on = false;
                if (on && x.E.Anims != null && !Playing(x)) on = false;
                if (on && x.Fire && !Firing(x)) on = false;
                if (on && x.BoneFire && AttackBone(x) && !Firing(x)) on = false;
                if (x.Parts.Length == 0 && (!on || dTarget > RANGE || !InFrustum(planes, v, 15))) continue;
                bool near = on && dTarget <= RANGE;
                // the source's level of detail (0x703e40): every slot within ParticleSrcOffset (120 m) of the camera,
                // then fewer over ParticleSrcDist (100 m), fewer still when the frame is full (load)
                int max = Math.Max(1, x.E.Max);
                var g = x.Root != null && GodotObject.IsInstanceValid(x.Root) && x.Root.IsInsideTree() ? x.Root.GlobalPosition : v;
                float de0 = g.DistanceTo(eye), dl = de0 - SRC_OFFSET;
                x.Lod = dl >= SRC_DIST ? 0 : dl <= 0 ? max : Math.Min(max, (int)Math.Floor(max * (1 - dl / SRC_DIST) * Load) + 2);
                Step(x, dt, M, near);
                if (x.Live > 0) draw.Add((x, M, de0));
            }
        }
        catch (Exception ex) { if (!_failed) { _failed = true; L.Error("particle update failed", ex); } }
        finally
        {
            if (i < list.Count) for (int j = i; j < list.Count; j++) list[kept++] = list[j];      // (an error: the rest kept as is)
            if (kept < list.Count) list.RemoveRange(kept, list.Count - kept);
        }
        // ---- the sprites in the view, the emitters nearest the camera first. Only what is on the screen counts against
        // the limits (sprites and fill) and the load: the emitters out of the view - behind the camera, beside it, all
        // around the 160 m about the target - took most of the budget, so that emitters in the view were cut and came
        // back from frame to frame as the totals changed (the fires and smoke of a base "blinking"), and the load fell
        // to nothing (the far sources down to 2 slots, then up again).
        draw.Sort(static (a, b) => a.D.CompareTo(b.D));
        try
        {
            foreach (var (x, M, _) in draw)
            {
                if (budgetN <= 0 || budgetArea <= 0) break;
                var b = Batch(x.E);
                if (b == null) continue;
                float ws = Mathf.Pow(Math.Abs(M.Basis.Determinant()), 1f / 3);
                if (ws == 0 || float.IsNaN(ws)) ws = 1;
                // (a sprite's reach about its point: its height, the cell's width, the pivot's shift)
                float reach = 1.5f * Math.Max(1, b.Aspect);
                var P = x.E;
                for (int j = 0; j < x.Parts.Length; j++)
                {
                    ref var p = ref x.Parts[j];
                    if (!p.Vis || p.Size < 0.0001f) continue;
                    if (budgetN <= 0 || budgetArea <= 0) break;
                    var w = p.World ? p.P : M * p.P;
                    // fading into the scene's fog (0x705191: the renderer's linear fog start / end), gone beyond its end
                    float de = w.DistanceTo(eye);
                    float fade = 1;
                    if (de > FogStart) { if (de >= FogEnd) continue; fade = (FogEnd - de) / (FogEnd - FogStart); }
                    float size = p.Size / 4 * ws;                                    // (a quarter of "size" high)
                    // never larger than the largest share of the screen (the JS's vertex shader: min(aSize, uMaxPx * d / uScale))
                    size = Math.Min(size, maxPx * Math.Max(0.5f, de) / scale);
                    if (!InFrustum(planes, w, size * reach)) continue;              // (not on the screen)
                    float px = size * scale / Math.Max(0.5f, de);
                    budgetN--; budgetArea -= px * px;
                    float rot = p.Rot;
                    if ((x.F & ALONG_VEL) != 0)
                    {
                        // pointing the way it flies on the screen (0x707e30: the screen angle of pos -> pos + v, plus
                        // "rotate"; SpriteAngle: the client measures it with the screen's y down - with "rotate" pi the
                        // cell's top points along the flight, the healing area's rays stand on their foot); a ground
                        // quad: the heading on the ground. A source that turns its particles (src_rot_speed): that
                        // motion. No motion on the screen: the particle's own turn.
                        var d = P.SrcRotSpeed != 0 ? p.Dv : p.V;
                        if (!p.World) d = M.Basis * d;
                        if ((x.F & GROUND) != 0) rot = (d.X != 0 || d.Z != 0 ? MathF.Atan2(d.X, d.Z) : 0) + P.Rotate;
                        else { d = view * d; rot = SpriteAngle.Screen(d.X, d.Y, P.Rotate) ?? p.Rot; }
                    }
                    // (the fog's fade: an additive particle's colour, the others' alpha - 0x705191 / 0x705329)
                    if (b.Additive) b.Push(w, size, p.A, p.Cr * fade, p.Cg * fade, p.Cb * fade, p.Tile, rot, x.Pivot, (x.F & GROUND) != 0);
                    else b.Push(w, size, p.A * fade, p.Cr, p.Cg, p.Cb, p.Tile, rot, x.Pivot, (x.F & GROUND) != 0);
                }
            }
        }
        catch (Exception ex) { if (!_failed) { _failed = true; L.Error("particle drawing failed", ex); } }
        draw.Clear();
        Drawn = lv.Sprites - budgetN;
        foreach (var b in _batches.Values) b?.Flush(Math.Max(1, maxPx));
    }

    Plane[] _planes = Array.Empty<Plane>();
    readonly List<(Emitter X, Transform3D M, float D)> _draw = new();

    static bool InFrustum(Plane[] f, Vector3 c, float r)
    {
        foreach (var p in f) if (p.DistanceTo(c) > r) return false;
        return true;
    }

    static AnimController? ModelAnim(Node? n)
    {
        for (var o = n; o != null; o = o.GetParent()) if (o is ModelInstance mi && mi.Anim != null) return mi.Anim;
        return null;
    }

    // an emitter that animations drive (extras anims: a finishing move's blood, a sinking ship's smoke, an effect's
    // "anim"): is one of them playing on its model?
    static bool Playing(Emitter x)
    {
        if (!x.CtlAsked) { x.CtlAsked = true; x.Ctl = ModelAnim(x.Node); }
        var n = x.Ctl?.Running;
        // (an emitter of attack clips - the trike's flame - burns on between them while its unit sprays: HoldFire)
        return n != null && x.E.Anims!.Contains(n) || x.Ctl?.HoldFire == true && x.E.Anims!.Any(IsAttackClip);
    }

    // a weapon emitter: is the nearest model above it that can attack (the turret, the gunner) attacking?
    static bool Firing(Emitter x)
    {
        if (!x.WielderAsked)
        {
            x.WielderAsked = true;
            for (Node? o = x.Node; o != null && x.Wielder == null; o = o.GetParent())
                if (o is ModelInstance mi && mi.Anim != null)
                    foreach (var k in mi.Anim.Names) if (ATTACK.IsMatch(k)) { x.Wielder = mi.Anim; break; }
        }
        var c = x.Wielder;
        // (Running, not CurName: in a game the units' clips come from the simulation through AnimController.Start, which
        // left CurName empty - the flamethrowers' fire never showed in a fight, only in a test that played the clip)
        // (HoldFire: a flamethrower between its strikes - the flame burns until the attack ends)
        return c == null || c.HoldFire || (c.IsPlaying && IsAttackClip(c.Running));
    }

    /// <summary>
    /// A weapon's spray hung on the root of a character or of the item in its hand (no weapon bit, no clip, no bone:
    /// the SEAS flamethrower's flame stream and black smoke "particles_feuer" / "particles_rauch" of
    /// seas_flamethrower_s2..s5, the flamer's dripping fire "particles_feuer_tropfen"): the original sprays it while the
    /// soldier fires (its attack clip "seas_flamethrower_0"); always on, the flamethrowers stood flaming at rest.
    /// owner: the model the emitter belongs to; returns the character's animation, or null.
    /// </summary>
    public static AnimController? CharacterWeapon(ModelInstance owner)
    {
        for (Node? o = owner; o != null; o = o.GetParent())
            if (o is ModelInstance mi && mi.Template.Fourcc == "Char")
                return mi.Anim != null && mi.Anim.Names.Any(IsAttackClip) ? mi.Anim : null;
        return null;
    }

    // an emitter without the weapon bit on a bone that only attack clips move (MIRAGE's exo flamethrower: the flame on
    // its gun's helper bone, which only attack_front animates) is a weapon's too: it sprays while the wielder attacks
    static bool AttackBone(Emitter x)
    {
        if (x.AtkBone is bool known) return known;
        ModelInstance? owner = null;
        for (Node? o = x.Node; o != null && owner == null; o = o.GetParent()) if (o is ModelInstance mi && mi.Anim != null) owner = mi;
        // (the model's animations may come after its emitters: asked again for a few frames)
        if (owner == null && ++x.AtkTries < 30) return false;
        x.AtkBone = false;
        var parent = x.Node?.GetParent();
        if (owner == null || parent == null) return false;
        // on the root of a character, or of a fire weapon in its hand (an item with a weapon-bit emitter: the flamer)
        // (the model the emitter is part of - an item without clips is not the character holding it)
        ModelInstance? own = null;
        for (Node? o = parent; o != null && own == null; o = o.GetParent()) if (o is ModelInstance mi) own = mi;
        if (parent is Node3D pn && pn.Name == "Model" && own != null && (own.Template.Fourcc == "Char"
            || own.Template.Parts.Any(p => p.Kind == PartKind.Particles && p.HasAttr && (p.Attr & FIRING) != 0)))
        {
            if (CharacterWeapon(own) is not AnimController ch) return false;
            x.Wielder = ch; x.WielderAsked = true; x.AtkBone = true;
            return true;
        }
        if (owner.Template.Clips == null) return false;
        string? target = parent switch
        {
            BoneAttachment3D ba => ":" + ba.BoneName,
            Node3D n when n.Name != "Model" => owner.Root.GetPathTo(n).ToString(),
            _ => null,
        };
        if (target == null) return false;
        var lib = owner.Template.Clips;
        var moving = new List<string>();
        foreach (var (lower, clip) in owner.Template.ClipNames)
        {
            var a = lib.GetAnimation(clip);
            if (a == null) continue;
            for (int t = 0; t < a.GetTrackCount(); t++)
            {
                var path = a.TrackGetPath(t).ToString();
                if (target.StartsWith(":") ? path.EndsWith(target) : path == target || path.StartsWith(target + ":")) { moving.Add(lower); break; }
            }
        }
        bool atk = moving.Count > 0 && moving.TrueForAll(n => ATTACK.IsMatch(n));
        x.AtkBone = atk;
        if (atk) { x.Wielder = owner.Anim; x.WielderAsked = true; }
        return atk;
    }

    // one frame of a source (Process 0x704a10): every particle slot moves (ProcPart); a dead one starts again (InitPart)
    // when the source may emit, else it stays hidden. emit: the source is shown and near the camera.
    void Step(Emitter x, float dt, Transform3D M, bool emit)
    {
        dt = Math.Min(1, dt);
        if (dt < 0.001f) return;
        var P = x.E;
        int max = Math.Max(1, P.Max);
        uint fl = x.F;
        // the emitter's movement since the last frame, in its own frame (SetMatrix 0x703f70: moveDelta), and the
        // model's speed in the world (srcVel, for particles that live in the world)
        var wpos = M.Origin;
        x.LastW ??= wpos;
        var inv = M.Basis.Inverse();
        x.Md = inv * (wpos - x.LastW.Value);
        if ((fl & WORLD) != 0)
        {
            var vel = (wpos - x.LastW.Value) / dt;
            float f = 12.5f * dt;
            if (dt >= 0.5f) x.Sv = Vector3.Zero; else if (f < 1) x.Sv = x.Sv * (1 - f) + vel * f; else x.Sv = vel;
        }
        x.LastW = wpos;
        bool can = emit;
        // no spray while the emitter is below its model's origin unless flag 0x40000 (Process 0x704a91), for emitters
        // an animation moves: the SEAS turret's muzzle flashes rest inside the tower and flicker at the barrel
        if (can && P.Anims != null && (fl & BELOW) == 0 && x.InModelFrame && x.Node!.Position.Z < 0) can = false;
        if (P.EmitTimer > 0)
        {
            can = false;
            x.Timer += dt;
            if (x.Timer >= P.EmitTimer) { can = emit; x.Timer = P.EmitTimerVar != 0 ? (float)_rnd.NextDouble() * P.EmitTimerVar * P.EmitTimer : 0; }
        }
        // the first look: every slot starts (a continuous source at random ages, a burst all at once)
        if (!x.Started)
        {
            if (!emit) return;
            x.Started = true;
            x.Parts = new Part[max];
            int live = 0;
            for (int i = 0; i < max; i++)
            {
                ref var p = ref x.Parts[i];
                Init(x, ref p, false, M);
                if (P.EmitPerMeter > 0) { p.Vis = false; p.Emitted = false; }    // (constructor 0x707dbe)
                if (p.Vis) live++;
            }
            x.Carry = 0;
            x.Live = live;
            return;
        }
        Gravity(x, M, dt);
        // emit_per_meter: one particle per so many metres the emitter moved (ship wakes, arrow trails)
        float mpp = P.EmitPerMeter > 0 ? 1 / P.EmitPerMeter : 0;
        float dist = mpp > 0 ? x.Md.Length() + x.Carry : 0;
        int lod = x.Lod;
        for (int i = 0; i < x.Parts.Length; i++)
        {
            ref var p = ref x.Parts[i];
            bool ok = can && i < lod && (mpp == 0 || dist >= mpp);
            bool re = false;
            if (!Proc(x, ref p, dt)) { Init(x, ref p, true, M); re = true; if (!ok) { p.Vis = false; p.Emitted = false; } }
            if ((fl & BURST) != 0 && ok && !p.Emitted) { Init(x, ref p, false, M); re = true; }
            if (ok && re && mpp > 0) dist -= mpp;
            if (!re) Look(x, ref p);
        }
        if (mpp > 0) x.Carry = dist % mpp;
        int n = 0;
        foreach (ref readonly var p in x.Parts.AsSpan()) if (p.Vis) n++;
        x.Live = n;
        // nothing left in flight and nothing to emit: it starts afresh when it may emit again
        if (n == 0 && !emit) { x.Started = false; x.Parts = Array.Empty<Part>(); }
    }

    // gravitation for this frame in the particles' frame: world down, or the emitter's own -z
    static void Gravity(Emitter x, Transform3D M, float dt)
    {
        float g = x.E.Gravitation;
        x.HasG = g != 0;
        if (!x.HasG) return;
        if ((x.F & LOCAL_GRAV) != 0) { x.Gv = new Vector3(0, 0, -g * dt); return; }
        x.Gvw = new Vector3(0, -g * dt, 0);                       // (world-space particles)
        x.Gv = M.Basis.Inverse() * x.Gvw;
    }

    // ProcPart: age, motion, then the look; false = its life is over
    bool Proc(Emitter x, ref Part p, float dt)
    {
        var P = x.E;
        uint fl = x.F;
        if (p.Life > 0)
        {
            p.Age += dt;
            if (p.Age >= p.Life) { p.Age %= p.Life; p.Vis = false; return false; }
        }
        if ((fl & STILL) == 0)
        {
            if (P.Drag != 0) p.V *= 1 - Math.Min(1, P.Drag * dt);
            if (x.HasG) p.V += p.World && (fl & LOCAL_GRAV) == 0 ? x.Gvw : x.Gv;
            if (P.CenterSuck != 0)
            {
                var d = -p.P;                                         // (towards the emitter: its origin in its own frame)
                if (P.SuckVel > 0) d -= p.V * P.SuckVel;
                p.V += d * (P.CenterSuck * dt);
            }
            if (P.SrcRotSpeed != 0)
            {
                // turning about the emitter's axis (0x7080cb): R = -theta about z; about the emitter E + 2e - R e (to
                // first order +theta, slowly spiralling out), about the model's origin R pos
                float th = -P.SrcRotSpeed * dt, c = MathF.Cos(th), sn = MathF.Sin(th), px = p.P.X, py = p.P.Y;
                float rx = c * px - sn * py, ry = sn * px + c * py;
                float nx = (fl & ROT_ORIGIN) != 0 ? rx : 2 * px - rx, ny = (fl & ROT_ORIGIN) != 0 ? ry : 2 * py - ry;
                p.Dv = new Vector3(nx - px, ny - py, 0);
                p.P.X = nx; p.P.Y = ny;
            }
            p.P += p.V * dt;
            if (p.World) { p.Inh *= 1 - Math.Min(1, 0.5f * dt); p.P += p.Inh * dt; }
        }
        if (P.Rotate > 0)
        {
            float s = (P.Rotate + p.Rv * P.RotationVar) * dt;
            p.Rot += p.Rot > 0 ? s : -s;
            if (p.Rot > 6.2832f) p.Rot -= 6.2832f; else if (p.Rot < -6.2832f) p.Rot += 6.2832f;
        }
        if (P.SizeIncrease != 0) p.Size += dt * P.SizeIncrease * (P.SizeIncExp != 0 ? MathF.Pow(p.Age, P.SizeIncExp) : 1);
        return true;
    }

    // fade and tile of a particle (the end of ProcPart / InitPart)
    static void Look(Emitter x, ref Part p)
    {
        var P = x.E;
        uint fl = x.F;
        float life = p.Life, a = 1;
        if (P.FadeIn > 0 && p.Age < P.FadeIn * life) a = p.Age / (P.FadeIn * life);
        if (P.FadeOut > 0 && p.Age > life - P.FadeOut * life) a = (life - p.Age) / (P.FadeOut * life);
        a = Math.Clamp(a, 0, 1);
        if (float.IsNaN(a)) a = 0;
        float al = P.Alpha / 255f;
        bool fa = (fl & (FADE_BOTH | FADE_ALPHA)) != 0, fc = (fl & FADE_ALPHA) == 0 || (fl & FADE_BOTH) != 0;
        p.A = fa ? al * a : al;
        p.Cr = fc ? p.Br * a : p.Br; p.Cg = fc ? p.Bg * a : p.Bg; p.Cb = fc ? p.Bb * a : p.Bb;
        if (P.Additive)
        {
            // the renderer's additive path (0x705329): the vertex colour is the particle's colour squared (c * c * the
            // party colour >> 16, white here) - a colour fade goes with the square of the fade (the heal rays are
            // darker while they fade in and out, 0.4 of their life each), a colour of the emitter darkens and deepens
            // (fx_heal_area's dots 200 / 150 / 0 -> 157 / 88 / 0). Its alpha is not used there (alpha 0: the one blend
            // ONE / INVSRCALPHA adds); the port keeps the emitter's alpha and an alpha-only fade (0x4000) - 173 of the
            // 1164 additive emitters of the export have an alpha below 255 (some 0: aje_temple, ninigi_teahouse,
            // domination_flag) and 16 fade only their alpha, which the original would show at full light (not
            // checked against the game).
            p.Cr *= p.Cr; p.Cg *= p.Cg; p.Cb *= p.Cb;
            p.A = (fl & FADE_ALPHA) != 0 && (fl & FADE_BOTH) == 0 ? al * a : al;
        }
        int n = P.Count, f = p.Tile0;
        if (n > 1 && life > 0)
        {
            int t = (int)Math.Floor(P.TileAnimSpeed * p.Age * n / life);
            if ((fl & TILE_FWD) != 0) f = Mod(t, n);
            else if ((fl & TILE_BACK) != 0) f = n - 1 - Mod(t, n);
            else if ((fl & (TILE_PING | TILE_PONG)) != 0) { int m = Mod(t, 2 * n), q = m < n ? m : 2 * n - 1 - m; f = (fl & TILE_PING) != 0 ? q : n - 1 - q; }
        }
        p.Tile = f + P.TileStart;
    }

    static int Mod(int a, int n) => ((a % n) + n) % n;

    // InitPart: where it starts and how it flies (in the emitter's frame: x, y its plane, z its axis)
    void Init(Emitter x, ref Part p, bool re, Transform3D M)
    {
        var P = x.E;
        uint fl = x.F;
        p.Vis = true; p.Emitted = true;
        float w = (float)(_rnd.NextDouble() * Math.PI * 2), u = (float)_rnd.NextDouble(), r = MathF.Sqrt(u) * P.ScaleX;
        float sx = MathF.Sin(w) * r, cy = MathF.Cos(w) * r;
        float A = 0, B = 0, C = 0;
        if ((fl & SPHERE_VEL) != 0 || ((fl & 0x810041) != 0 && (fl & KEEP_POS) == 0))
        {
            A = sx; B = cy; C = (_rnd.NextDouble() < 0.5 ? -1 : 1) * (float)_rnd.NextDouble() * MathF.Sqrt(1 - u) * P.ScaleX;
        }
        if (!(re && (fl & KEEP_POS) != 0))
        {
            if ((fl & SPHERE) != 0) p.P = new Vector3(A, B, C);
            else if ((fl & HEMI) != 0) p.P = new Vector3(A, B, Math.Abs(C));
            else if ((fl & RING) != 0) { float l = MathF.Sqrt(sx * sx + cy * cy); if (l == 0) l = 1; p.P = new Vector3(sx / l * P.ScaleX, cy / l * P.ScaleX, 0); }
            else p.P = new Vector3(sx, cy, 0);
            // EmitPos 0x703ac0: somewhere along the way the emitter moved this frame
            if ((fl & NO_SPREAD) == 0) p.P -= x.Md * (float)_rnd.NextDouble();
            p.World = false;
        }
        // the "unturned" velocities are along the model's axes: into the emitter's frame with its inverse turn
        Vector3 Unturned(Vector3 v) => x.Node != null ? x.Q.Inverse() * v : v;
        if ((fl & RADIAL_H) != 0) { var v = new Vector3(A, B, 0); if (v.LengthSquared() > 0) v = v.Normalized(); p.V = Unturned(v * P.ScaleZ); }
        else if ((fl & RADIAL_VEL) != 0)
        {
            var v = new Vector3(A, B, C);
            if ((fl & UP_ONLY) != 0 && v.Z < 0) v.Z = -v.Z;
            if (v.LengthSquared() > 0) v = v.Normalized();
            p.V = Unturned(v * P.ScaleZ);
        }
        else if ((fl & SPHERE_VEL) != 0)
        {
            var v = new Vector3(A, B, C) * (0.7f * P.ScaleZ / Math.Max(P.ScaleX, 1e-4f));
            if ((fl & UP_ONLY) != 0 && v.Z < 0) v.Z = -v.Z;
            p.V = Unturned(v);
        }
        else
        {
            float hx = sx, hy = cy;
            if ((fl & NO_Y) != 0) hy = 0;
            if ((fl & EDGE_SPEED) != 0 && r > 0) { hx *= P.ScaleX / r; hy *= P.ScaleX / r; }
            if (x.K is float k) { hx *= k; hy *= k; }
            p.V = new Vector3(hx, hy, 0.7f * P.ScaleZ);
        }
        float life = Math.Max(0, P.LifeTime);
        p.Life = life * (P.LifeTimeVar > 0 ? 1 - (float)_rnd.NextDouble() * P.LifeTimeVar : 1);
        if ((fl & BURST) != 0) p.Age = 0; else if (!re) p.Age = (float)_rnd.NextDouble() * p.Life;
        if (!(re && (fl & KEEP_TILE) != 0))
            p.Tile0 = (fl & (TILE_FWD | TILE_PING)) != 0 ? 0 : (fl & (TILE_BACK | TILE_PONG)) != 0 ? P.Count - 1 : (int)Math.Floor(_rnd.NextDouble() * P.Count);
        if (!(re && (fl & KEEP_SIZE) != 0)) p.Size = P.Size * (1 + (P.SizeVar != 0 ? (float)_rnd.NextDouble() * P.SizeVar : 0));
        if (!(re && (fl & KEEP_COLOR) != 0))
        {
            float Ch(float c, int vr) => Math.Max(0, c - (vr > 0 ? _rnd.Next(32768) % vr : 0)) / 255f;
            p.Br = Ch(P.ColorR, P.ColorVarR); p.Bg = Ch(P.ColorG, P.ColorVarG); p.Bb = Ch(P.ColorB, P.ColorVarB);
        }
        if (P.SpeedVar > 0) p.V *= 1 + (float)_rnd.NextDouble() * P.SpeedVar;
        if ((fl & WORLD) != 0) { p.P = M * p.P; p.V = M.Basis * p.V; p.World = true; p.Inh = x.Sv * 0.5f; }
        p.Rv = (float)_rnd.NextDouble();
        if (P.Rotate == 0) p.Rot = 0;
        else if (!(re && (fl & KEEP_ROT) != 0))
            p.Rot = P.EmitRotInc != 0 ? (x.RotAcc += P.EmitRotInc * MathF.PI / 180) : (float)((_rnd.NextDouble() - 0.5) * 2 * Math.PI);
        Look(x, ref p);
    }

    /// <summary>everything dropped (a new map)</summary>
    public void Clear()
    {
        _emitters.Clear(); _dormant.Clear();
        foreach (var b in _batches.Values) b?.Flush(0);
    }
}
