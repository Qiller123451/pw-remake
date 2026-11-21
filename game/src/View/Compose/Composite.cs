// Multi-part objects as the original scripts assemble them - the drawing side. The simulation decides which parts an
// entity shows (compose.js activeParts / partGfx: conditions, upgrades, inventions, unit level, epoch) and gives the
// list as plain data (PartSpec, parent-before-child order, the model already chosen); this builds and runs them:
//   seas_wehrspinne:        turret seas_wehrspinne_top at "we" (turns to the target), gunner at the top's "Dri1"
//   hu_triceratops (titan): two ballista tops at "con2"/"con3" with a gunner each, transporter build-up at "con"
//   aje_resource_collector: drawbar at "Db_1", wagon aje_resource_collector_<a..e> (by epoch) trailing at "Db_2"
//   ridden animals:         captain (tech tree captainclass) at "Ride", level flag <tribe>_animal_flag_0<level> at "flag"
//   brachiosaurus etc.:     optional build-ups after a per-unit upgrade, with their own rider / flag links
//   open transporters:      the passengers ride on the build-up at its links Dri0..Dri9 that no part of the table uses
//                           (BuildUpBase.usl CBuildUpTransporterOpen.UpdateLinkedObjs: LinkAction to "Dri<i>" in
//                           link order, the captain's link ignored, playing TransportObj.GetPassengerLoopAnim
//                           "standanim"); closed transporters (TYPE_TRANSPORTER: ships, vehicles) hide them
// Buildings use the same parts (tower turrets, harbour cranes: entities.js updateTurret) and the construction cranes
// (addCranes) - Attach on the building's model.
//
// Ported from remake/src/game/compose.js (pw-exporter 98c7703): GEAR, DEPLOY, turnPart, attach, the Composite class
// without the simulation's parts (activeParts, partGfx, the deploy state machine of deployReady / holdMove, which live
// in the simulation and reach the view as the parts list and clip calls).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.View.Anim;
using PwRemake.View.Models;
using PwRemake.View.Parts;

namespace PwRemake.View.Compose;

/// <summary>One attached part, as the simulation resolved it (gamedata.json composites entry + the chosen model).</summary>
public sealed class PartSpec
{
    public int I;
    /// <summary>rider, turret, buildup, drawbar, other</summary>
    public string Kind = "other";
    /// <summary>the model to show (partGfx: by level / epoch / rider level already applied)</summary>
    public string Gfx = "";
    public string Link = "";
    /// <summary>parent part (-1 = the unit itself)</summary>
    public int Pi = -1;
    public string? Anim, AttackAnim;
    /// <summary>a trailer following the drawbar (FlexLinkAction) instead of hanging on a link</summary>
    public bool Flex;
    /// <summary>a turret that shoots by itself (the titan's ballistas)</summary>
    public string? WeaponClass;
    /// <summary>MIRAGE's offset of the part in its link's frame (AddObjCustomized, metres, GSF axes); zero = none</summary>
    public Vector3 Offset;
}

public sealed class AttachedPart
{
    public PartSpec Spec = null!;
    public ModelInstance Obj = null!;
    public AnimController? Anim => Obj.Anim;
    public string? Idle, Attack;
    /// <summary>deploying units: "frame" or "gun"</summary>
    public string? Dep;
    /// <summary>game time of this gunner turret's last shot (riders show the attack pose for 1 s)</summary>
    public double ShotT = double.NegativeInfinity;
}

public sealed class Composite
{
    // the gear an invention shows on a tamed animal: class -> [[invention, render flags, hides the plain saddle]]
    // (Base Animal.usl UpdateGfxFlags: allosaurus / brachiosaurus / stegosaurus / kentrosaurus armour; the mammoth's and
    // rhino's armour with the armoured saddle instead of the saddle; animal blades = the helmet of mammoth, boar and
    // rhino; the megaloceros' drums and the rhino's pennant)
    static readonly Dictionary<string, (string inv, int[] bits, bool saddleOff)[]> GEAR = new()
    {
        ["aje_allosaurus"] = new[] { ("aje_allosaurus_armor", new[] { PartBits.ARMOR }, false) },
        ["aje_brachiosaurus"] = new[] { ("aje_brachiosaurus_armor", new[] { PartBits.ARMOR }, false) },
        ["aje_stegosaurus"] = new[] { ("aje_stegosaurus_armor", new[] { PartBits.ARMOR }, false) },
        ["hu_kentrosaurus"] = new[] { ("hu_kentrosaurus_armor", new[] { PartBits.ARMOR }, false) },
        ["hu_mammoth"] = new[] { ("animal_blades", new[] { PartBits.HELMET }, false), ("hu_mammoth_armor", new[] { PartBits.ARMOR, PartBits.ARMORSADDLE }, true) },
        ["hu_wild_boar"] = new[] { ("animal_blades", new[] { PartBits.HELMET }, false) },
        ["hu_scout"] = new[] { ("drums", new[] { PartBits.HELMET }, false) },
        ["hu_rhino"] = new[] { ("pennant", new[] { PartBits.STANDARTE }, false), ("animal_blades", new[] { PartBits.HELMET }, false), ("hu_rhino_armor", new[] { PartBits.ARMOR, PartBits.ARMORSADDLE }, true) },
    };

    /// <summary>The gear flags an owned animal of a class shows, by its owner's inventions (compose.js updateMask).</summary>
    public static (int[] gear, bool noSaddle) GearFor(string className, Func<string, bool> invented)
    {
        var gear = new List<int>();
        bool noSaddle = false;
        if (GEAR.TryGetValue(className, out var rule))
            foreach (var (inv, bits, off) in rule) if (invented(inv)) { gear.AddRange(bits); if (off) noSaddle = true; }
        return (gear.ToArray(), noSaddle);
    }

    // wild animals whose "misc" parts belong to the beast (MIRAGE FightingObj.ShowAnmlMiscFlag)
    static readonly Regex WILD_MISC = new("^(stegosaurus|kentrosaurus|mammoth|macrolemy[sd]_(land|water))$", RegexOptions.IgnoreCase);
    static readonly Regex MISC_HIDDEN = new("^C(Ankylosaurus|Brachiosaurus|Parasaurolophus)$");

    /// <summary>
    /// The animal's "misc" parts (VIS_FLAG_ANML_MISC): on a wild animal only what is part of the beast itself - the
    /// plates, shells, the mammoth's coat (MIRAGE FightingObj.ShowAnmlMiscFlag); the platforms and racks of the others
    /// are tack. Tamed ones (Base Animal.usl OnInit): hidden on ankylosaurus, brachiosaurus and parasaurolophus unless
    /// they carry a weapon build-up (catapult, gatling), on the lumber mammoth and on arena animals.
    /// </summary>
    public static bool MiscShown(bool wild, string gfx, string className, string? script, IEnumerable<string> partGfx)
    {
        if (wild) return WILD_MISC.IsMatch(gfx);
        if (className == "hu_mammoth_lumber_upgrade" || Array.IndexOf(PwRemake.Sim.World.Compose.WILD_LOOK_CLASSES, script) >= 0) return false;
        if (MISC_HIDDEN.IsMatch(script ?? "")) return partGfx.Any(g => g.Contains("catapult") || g.Contains("gatling"));
        return true;
    }

    // units that set up on the spot to shoot (BuildUpBase.usl CBuildUpWeapon SwitchToAttackMode / SwitchToWalkMode,
    // Animal.usl CMammoth): the log cannon mammoth lies down ("rest", held) while its support frame lowers
    // ("build_down" of the bottom part, 1.5 s busy), the gun on top turns to the target and plays "attack_front" once a
    // shot; to move again it gets up (rest backwards) and the frame rises ("build_up", 1 s busy)
    public sealed record Deploy(string Rest, string Frame, string Gun, double Down, double Up);
    public static readonly Dictionary<string, Deploy> DEPLOY = new()
    {
        ["hu_mammoth_log_cannon"] = new("rest", "hu_mammoth_log_cannon_buildup_bottom", "hu_mammoth_log_cannon_buildup_top", 1.5, 1.0),
    };

    readonly ModelInstance _host;
    public readonly List<AttachedPart> Parts = new();      // attached parts (rider, turrets, build-ups, flags ...)
    public readonly List<AttachedPart> Turrets = new();
    public readonly List<AttachedPart> Gunners = new();    // turrets that shoot by themselves (a weapon class: the titan's ballistas)
    /// <summary>first turret: aims at the target, shots leave from its Proj link</summary>
    public AttachedPart? Top, Rider, Bar;
    public readonly List<Node3D> Seats = new();            // open transporters: the free passenger links of the build-up
    readonly List<(ModelInstance obj, string key, string? stand, string? shoot)> _riding = new();
    string _paxKey = "";
    Wagon? _wagon;
    public Deploy? Dep { get; private set; }

    public Composite(ModelInstance host) { _host = host; }

    /// <summary>a composites table offset ([x, y, z], metres in the link's frame) as the attached part's position</summary>
    public static Vector3 OffsetOf(System.Text.Json.Nodes.JsonArray o) => new((float)(o[0]?.GetValue<double>() ?? 0), (float)(o[1]?.GetValue<double>() ?? 0), (float)(o[2]?.GetValue<double>() ?? 0));

    public void Dispose()
    {
        ClearRiding();
        Seats.Clear();
        foreach (var p in Parts) _host.Detach(p.Obj);
        if (_wagon != null) { _wagon.Obj.GetParent()?.RemoveChild(_wagon.Obj); _wagon.Obj.QueueFree(); _wagon = null; }
        Parts.Clear(); Turrets.Clear(); Gunners.Clear(); Top = Rider = Bar = null;
    }

    /// <summary>
    /// Show the parts (parents before children). unitName: the unit's class (deploying units, DEPLOY); deployed: it is
    /// set up (the frame lowered). wagonParent: where a trailer goes (the world node the unit is in).
    /// </summary>
    public void Build(IReadOnlyList<PartSpec> act, string? unitName = null, bool deployed = false, Node3D? wagonParent = null)
    {
        Dispose();
        var made = new Dictionary<int, AttachedPart>();
        Dep = unitName != null && DEPLOY.TryGetValue(unitName, out var dp) ? dp : null;
        foreach (var s in act)
        {
            AttachedPart? parent = s.Pi >= 0 ? made.GetValueOrDefault(s.Pi) : null;
            if (s.Pi >= 0 && parent == null) continue;
            if (s.Flex)
            {
                var hitch = (parent != null ? parent.Obj.FindLink(s.Link) : _host.FindLink(s.Link)) ?? (Node3D)(parent?.Obj ?? _host);
                if (wagonParent != null) MakeWagon(s.Gfx, hitch, wagonParent);
                continue;
            }
            // (a link the model lacks: the part hangs at the model's origin - ModelInstance.LinkOrOrigin)
            var obj = parent != null ? (parent.Obj.LinkOrOrigin(s.Link, s.Gfx) is Node3D l ? _host.AttachTo(l, s.Gfx) : null) : _host.Attach(s.Gfx, s.Link, originIfMissing: true);
            if (obj == null) continue;
            // (the link frame is the parent's GSF frame: the offset goes in as it is, the part turns about its own origin)
            if (s.Offset != Vector3.Zero) obj.Position = s.Offset;
            var p = new AttachedPart { Spec = s, Obj = obj };
            made[s.I] = p;
            Parts.Add(p);
            if (s.Kind == "turret" && s.WeaponClass != null) Gunners.Add(p);
            else if (s.Kind == "turret") { Turrets.Add(p); Top ??= p; }
            if (s.Kind == "drawbar" && Bar == null) Bar = p;
            if (s.Kind == "rider")
            {
                Rider ??= p;
                p.Idle = s.Anim ?? "ride_idle_0";
                p.Attack = s.AttackAnim ?? (p.Idle == "ride_idle_0" ? "ride_attack_front" : null);
            }
            if (Dep != null && p.Anim != null && (s.Gfx == Dep.Frame || s.Gfx == Dep.Gun))
            {
                // the frame and the gun hold a pose (first frame: folded up, gun at rest) - their clips are no idle loops
                p.Dep = s.Gfx == Dep.Frame ? "frame" : "gun";
                p.Anim.Hold(p.Dep == "frame" ? "build_down" : "attack_front", deployed && p.Dep == "frame");
            }
            else if (p.Anim != null)
            {
                var a = p.Anim.Pick(s.Anim, s.Kind == "rider" ? "ride_idle_0" : null, s.Kind == "rider" ? "balista_stand" : null, "standanim");
                if (a != null && s.Kind != "turret") p.Anim.Play(a);
            }
        }
        // the passengers' seats: the build-up's Dri links in their order, but those its own parts (the captain) take
        var bu = Parts.FirstOrDefault(p => p.Spec.Kind == "buildup");
        if (bu != null)
        {
            var used = new HashSet<string>(act.Where(s => s.Pi == bu.Spec.I).Select(s => s.Link));
            foreach (var k in bu.Obj.LinkNames.Where(k => Regex.IsMatch(k, @"^Dri\d$") && !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
                if (bu.Obj.FindLink(k) is Node3D n) Seats.Add(n);
        }
    }

    public AttachedPart? Part(string dep) => Parts.FirstOrDefault(p => p.Dep == dep);

    // ---- turrets

    /// <summary>
    /// a turret faces the +y axis of its link at rest (the game: BuildUpBase.usl RotateToEnemy, vSelfDirection {0, 1, 0})
    /// and turns about the link's z axis; returns the angle still to go
    /// </summary>
    public static float TurnPart(ModelInstance obj, Vector3 target, double dt, double speed)
    {
        if (obj.GetParent() is not Node3D par || !obj.IsInsideTree()) return 0;
        var inv = par.GlobalTransform.AffineInverse();
        var va = inv * obj.GlobalPosition;
        var vb = inv * new Vector3(target.X, obj.GlobalPosition.Y, target.Z);
        float dx = vb.X - va.X, dy = vb.Y - va.Y;
        if (dx * dx + dy * dy < 1e-6f) return 0;
        float want = Mathf.Atan2(-dx, dy);          // the angle about +z that brings +y onto (dx, dy)
        float diff = want - obj.Rotation.Z; diff = Mathf.Atan2(Mathf.Sin(diff), Mathf.Cos(diff));
        float step = Mathf.Min(Mathf.Abs(diff), (float)(speed * dt));
        var r = obj.Rotation; r.Z += Mathf.Sign(diff) * step; obj.Rotation = r;
        return Mathf.Abs(diff) - step;
    }

    /// <summary>turn the turrets towards a world point; returns the first turret's error (in each link's own frame: a
    /// link the Workshop turned turns the turret's rest direction with it)</summary>
    public float Aim(Vector3 target, double dt)
    {
        float? first = null;
        foreach (var t in Turrets) { float d = TurnPart(t.Obj, target, dt, 3); first ??= d; }
        return first ?? 0;
    }

    /// <summary>set up: the gun turns to the target (CanRotate); returns its error. heading: the unit's heading.</summary>
    public float AimGun(Vector3 target, float heading, double dt)
    {
        var g = Part("gun");
        if (g == null || !g.Obj.IsInsideTree()) return 0;
        var p = g.Obj.GlobalPosition;
        float want = Mathf.Atan2(-(target.X - p.X), -(target.Z - p.Z)) - heading;
        float cur = -g.Obj.Rotation.Z;
        float diff = want - cur; diff = Mathf.Atan2(Mathf.Sin(diff), Mathf.Cos(diff));
        float step = Mathf.Min(Mathf.Abs(diff), (float)(1.5 * dt));
        var r = g.Obj.Rotation; r.Z = -(cur + Mathf.Sign(diff) * step); g.Obj.Rotation = r;
        return Mathf.Abs(diff) - step;
    }

    /// <summary>a shot: the gun's recoil clip once</summary>
    public void Fired() { var g = Part("gun"); if (g?.Anim != null && g.Anim.Play("attack_front", new PlayOptions { Loop = false, Restart = true, Fade = 0 })) { } }

    /// <summary>the deploying unit sets up (down = true: the frame lowers) or gets up again (the frame rises, the gun turns back)</summary>
    public void DeployFrame(bool down)
    {
        var f = Part("frame");
        f?.Anim?.Play(down ? "build_down" : "build_up", new PlayOptions { Loop = false, Restart = true, Fade = 0 });
        if (!down) { var g = Part("gun"); if (g != null) { var r = g.Obj.Rotation; r.Z = 0; g.Obj.Rotation = r; } }   // (ResetRotation)
    }

    // ---- per frame

    /// <summary>attacking: the unit fights (riders show their attack pose); now: game time (gunners' shots).</summary>
    public void Update(double dt, bool attacking, double now = 0)
    {
        foreach (var p in Parts)
        {
            if (p.Spec.Kind == "rider" && p.Anim != null && p.Attack != null)
            {
                // a gunner shows its attack pose while its turret shoots, the main rider while the unit fights
                var host = p.Spec.Pi >= 0 ? Parts.FirstOrDefault(q => q.Spec.I == p.Spec.Pi) : null;
                var want = (host != null && host.Spec.WeaponClass != null ? now < host.ShotT + 1 : attacking) ? p.Attack : p.Idle;
                if (p.Anim.CurName != want?.ToLowerInvariant() && p.Anim.Has(want)) p.Anim.Play(want);
            }
        }
        // (the parts' clips run with the host: ModelInstance.Advance)
    }

    // ---- open transporters: the passengers on their seats (a model of each, playing its stand clip; the shooting
    // clip while it shoots from up there: transport.js passengerUpdate)

    public readonly record struct Passenger(string Id, string Gfx, Color? Party, bool Shooting);

    public void SyncPassengers(IReadOnlyList<Passenger> passengers, double dt)
    {
        var pax = Seats.Count > 0 ? passengers.Where(p => !string.IsNullOrEmpty(p.Gfx)).Take(Seats.Count).ToList() : new List<Passenger>();
        var key = string.Join(",", pax.Select(p => p.Id + ":" + p.Gfx));
        if (key != _paxKey)
        {
            ClearRiding();
            _paxKey = key;
            for (int i = 0; i < pax.Count; i++)
            {
                var r = _host.AttachTo(Seats[i], pax[i].Gfx, pax[i].Party);
                if (r == null) continue;
                // (a passenger is its own entity: not infected with the transporter - ModelInstance.SetInfected)
                r.MarkPassenger();
                var stand = r.Anim?.Pick("standanim", "idle_0", "stand");
                var shoot = r.Anim?.Pick("bow_1", "shoot_1", "throw_1", "attack_front", "attack_1");
                if (stand != null) r.Anim!.Play(stand);
                _riding.Add((r, pax[i].Id, stand, shoot));
            }
        }
        for (int i = 0; i < _riding.Count; i++)
        {
            var (obj, id, stand, shoot) = _riding[i];
            if (obj.Anim == null) continue;
            bool shooting = pax.FirstOrDefault(p => p.Id == id).Shooting;
            var want = shoot != null && shooting ? shoot : stand;
            if (want != null && obj.Anim.CurName != want) obj.Anim.Play(want, want == shoot ? new PlayOptions { Loop = false, Fade = 0.1 } : null);
        }
    }

    void ClearRiding()
    {
        foreach (var r in _riding) _host.Detach(r.obj);
        _riding.Clear(); _paxKey = "";
    }

    // ---- trailers (collector wagon, carts, drum wagon) follow the drawbar like a trailer instead of hanging on a link

    sealed class Wagon { public ModelInstance Obj = null!; public Node3D Hitch = null!; public Vector3? Rear; public float Len = 5.5f; public string? Walk; public bool WalkPicked; }

    void MakeWagon(string gfx, Node3D hitch, Node3D parent)
    {
        var obj = _host.Library.Instantiate(gfx, _host.Party);
        if (obj == null) return;
        parent.AddChild(obj);
        _wagon = new Wagon { Obj = obj, Hitch = hitch };
    }

    /// <summary>
    /// Move the trailer behind the hitch. heading: the unit's heading (0 = facing -z); speed: the unit's ground speed
    /// (m/s); height: the ground height at (x, z).
    /// </summary>
    public void SyncWagon(double dt, float heading, float speed, Func<float, float, float> height)
    {
        var w = _wagon;
        if (w == null || !w.Hitch.IsInsideTree()) return;
        var hitch = w.Hitch.GlobalPosition;
        w.Rear ??= new Vector3(hitch.X + Mathf.Sin(heading) * w.Len, 0, hitch.Z + Mathf.Cos(heading) * w.Len);
        float dx = hitch.X - w.Rear.Value.X, dz = hitch.Z - w.Rear.Value.Z, d = Mathf.Sqrt(dx * dx + dz * dz);
        if (d == 0) d = 1;
        w.Rear = new Vector3(hitch.X - dx / d * w.Len, 0, hitch.Z - dz / d * w.Len);
        float h = Mathf.Atan2(-dx, -dz);
        w.Obj.GlobalPosition = new Vector3(hitch.X, height(hitch.X, hitch.Z), hitch.Z);
        w.Obj.Rotation = new Vector3(0, h, 0);
        w.Obj.Visible = _host.IsVisibleInTree();
        if (w.Obj.Anim != null)
        {
            // not every trailer has a "walk_1" clip (some epoch variants of the collector wagon only have others):
            // take the walk clip it has, or leave it still
            if (!w.WalkPicked) { w.WalkPicked = true; w.Walk = w.Obj.Anim.Pick("walk_1", "walk_2", "walk_3", "walk_0") ?? w.Obj.Anim.Names.FirstOrDefault(n => n.Contains("walk") && !n.Contains('#')); }
            bool moving = speed * speed > 0.05f;
            if (moving && w.Walk != null) w.Obj.Anim.Play(w.Walk, new PlayOptions { Ts = Math.Min(2, speed / 2.5) });
            else if (w.Obj.Anim.CurName != null) w.Obj.Anim.SetTimeScale(0);
            w.Obj.Advance(dt);
        }
    }
}
