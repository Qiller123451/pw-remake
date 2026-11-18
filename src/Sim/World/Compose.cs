// Multi-part objects as the original scripts assemble them, driven by the datamined table
// ModelExporter/pwexport/data/composites.json (normalized by pwexport/composites.py into gamedata.json "composites"):
//   seas_wehrspinne:        turret seas_wehrspinne_top at "we" (turns to the target), gunner at the top's "Dri1"
//   hu_triceratops (titan): two ballista tops at "con2"/"con3" with a gunner each, transporter build-up at "con"
//   aje_resource_collector: drawbar at "Db_1", wagon aje_resource_collector_<a..e> (by epoch) trailing at "Db_2"
//   ridden animals:         captain (tech tree captainclass) at "Ride", level flag <tribe>_animal_flag_0<level> at "flag"
//   brachiosaurus etc.:     optional build-ups after a per-unit upgrade, with their own rider / flag links
//   open transporters:      the passengers ride on the build-up at its links Dri0..Dri9 that no part of the table uses
//                           (BuildUpBase.usl CBuildUpTransporterOpen.UpdateLinkedObjs); closed transporters hide them
// Every part: {kind, gfx, variants, by, link, pi (parent part, -1 = the unit), anim, attack_anim, cond, arg, flex}.
// Weapons, worker tools and carried goods are handled by the unit itself (Entities.cs / economy).
//
// Ported from remake/src/game/compose.js (pw-exporter 98c7703): the simulation part - which parts an object shows
// (conditions, variants by level / epoch, riders by level), their animations (riders' idle / attack poses, the log
// cannon's set-up), the turrets' angles, the render mask of animals and vehicles (saddle, party colour, gear,
// wounds). Placing the part models on the links, the trailer's following and the passengers' models are the view's
// (game/src/View) from this data.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Rules;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>A part attached to an entity's model (or to another part) at a link: what the view attaches and poses.</summary>
public sealed class AttachedPart
{
    public CompositeSpec? Spec;
    /// <summary>the part's model</summary>
    public string Gfx = "";
    /// <summary>the link of the parent model it hangs at</summary>
    public string Link = "";
    /// <summary>the part it hangs on (null: the entity's own model)</summary>
    public AttachedPart? Parent;
    public string Fourcc = "";
    public AnimCtl? Anim;
    /// <summary>turrets: the angle turned about the link (the JS rotation.z, negated: + turns like an entity's heading)</summary>
    public double Yaw;
    /// <summary>riders: their idle and attack clips</summary>
    public string? Idle, Attack;
    /// <summary>a turret that shoots by itself (a weapon class: the titan's ballistas; combat.js gunnerUpdate)</summary>
    public string? WClass;
    public double ShotT = -1;
    /// <summary>its gunner's state (Combat.GunnerUpdate: weapon, cooldown, target)</summary>
    public GunnerState? Gun;
    /// <summary>the log cannon's set-up parts: "frame" | "gun"</summary>
    public string? Dep;
    public string? AttackAnim;
    /// <summary>a trailer following the drawbar (FlexLinkAction) instead of hanging on the link</summary>
    public bool Flex;
    /// <summary>building state of static parts (applyState of the JS)</summary>
    public int VisLevel = 4, VisDmg;
    public bool Night;
    /// <summary>a script's decoration (ExtraPart): its building state is its own (VisLevel 4, VisDmg its destruction
    /// level), not the host's, and the view applies it on any host</summary>
    public bool OwnState;
    /// <summary>script visibility flags of the part's model (SetRndInvMaskSingleFlagInv: bit -> shown)</summary>
    public (int Bit, bool On)[]? VisFlags;
    /// <summary>open transporters: the passenger riding here (Composite.SyncPassengers) and its clip</summary>
    public Unit? Passenger;
    public string? Stand, Shoot;
    /// <summary>a ruin part of a destroyed building (Building.RuinParts): the live part's model it stands in for - the
    /// view places it where that part hung (the link of the building's or the parent part's live model)</summary>
    public string? From;
    public override string ToString() => $"{Gfx}@{Link}";
}

public sealed partial class Unit
{
    /// <summary>the render mask of an animal / vehicle (parts.js animalMask: hidden attribute bits), -1 = none (characters, buildings)</summary>
    public int HideMask = -1;
    /// <summary>an owned animal / vehicle tinted with its owner's colour (attr bit 5 parts)</summary>
    public bool PartyTinted;
}

public static class Compose
{
    // the parts.js flags (FightingObj.usl VIS_FLAG_ANML_*)
    public const int PARTYCOL = 5, SADDLE = 6, HELMET = 7, ARMOR = 8, STANDARTE = 9, ARMORSADDLE = 10, MISC = 11;
    const int PICK = 18, SHADOW = 19;
    static readonly (int Bit, double At)[] WOUNDS = { (20, 0.8), (21, 0.7), (22, 0.6), (23, 0.5), (24, 0.4), (25, 0.3), (27, 0.2) };   // Animal.usl UpdateHitpoints
    const int WOUND_ALL = 0x0ff00000;
    const int GEAR_ALL = 0x7e0;          // bits 5..10 (MISC 11 visible by default)

    /// <summary>
    /// the gear an invention shows on a tamed animal: class -> [[invention, render flags, hides the plain saddle]]
    /// (Base Animal.usl UpdateGfxFlags: allosaurus / brachiosaurus / stegosaurus / kentrosaurus armour; the mammoth's and
    /// rhino's armour with the armoured saddle instead of the saddle; animal blades = the helmet of mammoth, boar and
    /// rhino; the megaloceros' drums and the rhino's pennant)
    /// </summary>
    public static readonly Dictionary<string, (string Inv, int[] Bits, bool SaddleOff)[]> GEAR = new()
    {
        ["aje_allosaurus"] = new[] { ("aje_allosaurus_armor", new[] { ARMOR }, false) },
        ["aje_brachiosaurus"] = new[] { ("aje_brachiosaurus_armor", new[] { ARMOR }, false) },
        ["aje_stegosaurus"] = new[] { ("aje_stegosaurus_armor", new[] { ARMOR }, false) },
        ["hu_kentrosaurus"] = new[] { ("hu_kentrosaurus_armor", new[] { ARMOR }, false) },
        ["hu_mammoth"] = new[] { ("animal_blades", new[] { HELMET }, false), ("hu_mammoth_armor", new[] { ARMOR, ARMORSADDLE }, true) },
        ["hu_wild_boar"] = new[] { ("animal_blades", new[] { HELMET }, false) },
        ["hu_scout"] = new[] { ("drums", new[] { HELMET }, false) },
        ["hu_rhino"] = new[] { ("pennant", new[] { STANDARTE }, false), ("animal_blades", new[] { HELMET }, false), ("hu_rhino_armor", new[] { ARMOR, ARMORSADDLE }, true) },
    };

    /// <summary>
    /// units that set up on the spot to shoot (BuildUpBase.usl CBuildUpWeapon SwitchToAttackMode / SwitchToWalkMode,
    /// Animal.usl CMammoth): the log cannon mammoth lies down ("rest", held) while its support frame lowers ("build_down"
    /// of the bottom part, 1.5 s busy), the gun on top turns to the target and plays "attack_front" once a shot; to move
    /// again it gets up (rest backwards) and the frame rises ("build_up", 1 s busy).
    /// Sle: the body's own start / loop / end clip is the whole set-up (Vehicle.usl CWehrspinne, MIRAGE's CSeasMGWalker:
    /// SwitchToAttackMode = AnimAction(clip, 1, intro, no outro) - it curls up -, every shot SetAttackAnim =
    /// SetSLEAnim(clip, 0 / 9999, no intro, no outro) - it stays curled -, SwitchToWalkMode = AnimAction(clip, 1, no intro,
    /// outro) - it uncurls -; Fight.usl switches to attack mode in range and to walk mode only to follow a target out of
    /// range or when the fight ends, Break). Down / Up are then the clip's start and end parts (Rest's loop marks).
    /// Before (2026-11-12, user report "it uncurls after every attack") the Black Widow played its whole attack_front
    /// (curl, shot, uncurl) for every shot.
    /// </summary>
    public sealed record DeployDef(string Rest, string Frame, string Gun, double Down, double Up, bool Sle = false);
    public static readonly Dictionary<string, DeployDef> DEPLOY = new()
    {
        // the mammoth's "rest" is a start / loop / end clip (frames 0-35 it lies down, 35-55 it rests, 55-90 it gets up):
        // CMammoth.SwitchToAttackMode AnimAction("rest", 1, intro, no outro), SwitchToWalkMode AnimAction("rest", 0, no
        // intro, outro) (Animal.usl:2416-2443). It was played whole (lying down, resting and getting up again) to set up
        // and backwards to get up: the cannon mammoth stood while it was set up to shoot
        ["hu_mammoth_log_cannon"] = new DeployDef("rest", "hu_mammoth_log_cannon_buildup_bottom", "hu_mammoth_log_cannon_buildup_top", 1.4, 1.4, Sle: true),
        ["seas_wehrspinne"] = new DeployDef("attack_front", "", "", 1.2, 1.16, Sle: true),     // (frames 0-30 curl, 30-31 loop, 31-60 uncurl)
        ["seas_mgwalker"] = new DeployDef("standup", "", "", 1.0, 1.0, Sle: true),             // MIRAGE (the spider body, standup 0-25 / 25-65 / 65-90)
    };

    // wild animals whose "misc" parts belong to the beast (MIRAGE FightingObj.ShowAnmlMiscFlag)
    static readonly Regex WILD_MISC = new("^(stegosaurus|kentrosaurus|mammoth|macrolemy[sd]_(land|water))$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TamedNoMisc = new("^C(Ankylosaurus|Brachiosaurus|Parasaurolophus)$", RegexOptions.Compiled);
    static readonly Regex Catapult = new("catapult|gatling", RegexOptions.Compiled);

    /// <summary>
    /// attach a part's model at a link of its parent (null: the parent model has no such link or the part no model).
    /// The view puts the model there (link frames are the models' native Z-up frames).
    /// </summary>
    public static AttachedPart? Attach(World W, Entity host, AttachedPart? parent, string? parentGfx, string? link, string? gfx)
    {
        if (string.IsNullOrEmpty(link) || string.IsNullOrEmpty(gfx)) return null;
        var M = W.Models;
        var g = gfx.ToLowerInvariant();
        if (!M.Has(g)) return null;
        var pf = M.File(parentGfx);
        if (pf != null && !pf.Links.ContainsKey(link)) return null;
        var clips = M.Clips(g);
        return new AttachedPart { Gfx = g, Link = link, Parent = parent, Fourcc = M.Fourcc(g), Anim = clips.Count > 0 ? new AnimCtl(clips, host) : null };
    }

    /// <summary>is a filter (upgrade) switched on for this unit (its own upgrades) or its owner?</summary>
    static bool UpgradeOn(Entity e, string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (e.LocalTT != null && e.LocalTT.Has(path)) return true;
        return e.Owner?.Tt != null && e.Owner.Tt.Has(path);
    }

    /// <summary>which model of a part to show: by unit level / epoch, riders by CCaptain.UpdateGfx ("&lt;gfx minus last
    /// char&gt;&lt;level&gt;" if such a model exists), else the part's own model</summary>
    public static string PartGfx(World W, CompositeSpec spec, Entity e)
    {
        var v = spec.Variants;
        if (spec.By == "level" && v.Count > 0) return v[Math.Max(0, Math.Min(v.Count - 1, e.Level - 1))];
        if (spec.By == "epoch" && v.Count > 0) return v[Math.Max(0, Math.Min(v.Count - 1, (e.Owner != null ? e.Owner.Epoch() : 1) - 1))];
        if (spec.Kind == "rider" && spec.Gfx.Length > 0 && char.IsAsciiDigit(spec.Gfx[^1]))
        {
            var lv = spec.Gfx.Substring(0, spec.Gfx.Length - 1) + e.Level;
            if (W.Models.Has(lv)) return lv;
        }
        return spec.Gfx;
    }

    /// <summary>the parts an entity shows right now (conditions of the table), in parent-before-child order</summary>
    public static List<CompositeSpec> ActiveParts(World W, Entity e, List<CompositeSpec> specs, bool construction = false)
        => ActiveParts(specs, s => s.Cond switch
        {
            "always" => true,
            "ready" => e is not Building b || b.Built,
            "construction" => construction,
            "upgrade" => UpgradeOn(e, s.ArgStr),
            "invent" => e.Owner != null && W.Data.Invented(e.RulesOwner(), s.ArgStr ?? "", e.Owner.Tribe, e),
            _ => false,      // activation, special: driven elsewhere or not at all
        });

    /// <summary>the parts shown when Ok says which conditions (always / upgrade / invent ...) hold; build-ups decide the
    /// parts that depend on them (buildup / unless)</summary>
    public static List<CompositeSpec> ActiveParts(List<CompositeSpec> specs, Func<CompositeSpec, bool> Ok)
    {
        var on = new HashSet<int>();
        // build-ups first: other parts depend on which one is mounted
        foreach (var s in specs) if (s.Kind == "buildup" && Ok(s)) on.Add(s.I);
        var res = new List<CompositeSpec>();
        foreach (var s in specs)
        {
            bool yes;
            if (s.Kind == "buildup") yes = on.Contains(s.I);
            else if (s.Cond == "buildup") yes = (s.ArgList ?? new List<int>()).Any(i => on.Contains(i));
            else if (s.Cond == "unless") yes = !(s.ArgList ?? new List<int>()).Any(i => on.Contains(i));
            else yes = Ok(s);
            if (yes && s.Pi >= 0 && !res.Any(p => p.I == s.Pi)) yes = false;    // parent not shown
            if (yes) res.Add(s);
        }
        return res;
    }

    /// <summary>
    /// The add-on that fires the unit's own ranged weapon (the scripts' main CBuildUpWeapon object: the launcher on the
    /// saltasaurus and the seismosaurus, the gatling on the parasaurolophus, the rhino's ballista, the catapults of the
    /// ankylosaurus / brachiosaurus / mosasaurus, the guns of vehicles and ships): the first turret without a weapon class
    /// of its own (Composite.Top - one with a weapon class shoots by itself, Combat.GunnerUpdate), else a build-up with a
    /// clip of its own (the catapults: their "anim" is the clip they play on attack - datamine gen.py "attack_front (on
    /// attack)"), else a build-up (the muraenosaurus' submarine hull). null: none (riders and passengers are no guns).
    /// </summary>
    public static CompositeSpec? GunSpec(IReadOnlyList<CompositeSpec> act)
        => act.FirstOrDefault(s => s.Kind == "turret" && s.WeaponClass == null)
        ?? act.FirstOrDefault(s => s.Kind == "buildup" && s.Pi < 0 && s.Anim != null)
        ?? act.FirstOrDefault(s => s.Kind == "buildup" && s.Pi < 0);

    /// <summary>
    /// Does the carrier itself play the weapon's clip for a blow or shot (Combat.AttackUpdate)? Variants of an animal share
    /// its model and so its clips: the weapon tables of the ranged variants copy the strike of the plain animal
    /// ("attack_front": MIRAGE's saltasaurus launcher, the seismosaurus, the gatling parasaurolophus, the ballista rhino,
    /// the catapult ankylosaurus and mosasaurus, the submarine muraenosaurus, the pteranodon archers) or list none (the
    /// SEAS MG triceratops: the default "attack_front"). FightingObj.AttackEnemy sets the weapon's clip on the unit
    /// whenever the model has it, so those animals stomped, bit or swung their tails while the gun on their back or the
    /// archer on it shot. The rule: an animal (IsAnimal: its class, or its model is an animal's) stands for a shot of a ranged weapon
    /// that a gun add-on fires (the gun plays its clip, the crew its attack pose: DoCaptainAttackAnim), and for a shot
    /// whose clip is a melee strike of its model (Rules.IsStrikeOf) when a rider shoots it. Melee blows (a variant's own
    /// strike, a ranged unit's short weapon) stay the carrier's; a ranged clip of its own stays too (the Dustriders'
    /// dilophosaurus spits with "attack_front_m"). Vehicles and ships keep the weapon's clip: theirs is the hull's recoil.
    /// </summary>
    public static bool CarrierStrikes(bool ranged, bool animal, bool gunFires, bool riderShoots, bool strikeClip)
        => !(ranged && animal && (gunFires || (riderShoots && strikeClip)));

    /// <summary>an animal carrier (CarrierStrikes): a class of type ANML, or one drawn with an animal's model (from the
    /// animals' archives: the catapult mosasaurus, the submarine muraenosaurus, the cannon turtle are ships)</summary>
    public static bool IsAnimal(string clsType, string? arch) => clsType == "ANML" || (arch != null && arch.Contains("animals", StringComparison.OrdinalIgnoreCase));

    /// <summary>the clip the gun add-on plays for a shot: its own "anim" (on attack), else the usual fire clips</summary>
    public static string?[] GunClips(CompositeSpec gun) => new[] { gun.Anim, "attack_front", "gun_shoot" };

    /// <summary>hide mask for animals/vehicles: gear flags as set by the scripts, wounds by the hit point ratio (parts.js animalMask)</summary>
    /// <summary>an animal class that keeps the wild look under an owner: the arena animals (CArenaAnimal and derived) -
    /// mission 10's allied allosaurus wore a saddle and harness</summary>
    public static bool WildLook(Rules.Rules D, string cls) => D.IsA(cls, "CArenaAnimal");

    public static int AnimalMask(bool owned,IEnumerable<int>? gear = null, bool noSaddle = false, double? hp = null, bool? misc = null, bool armor = false)
    {
        int hide = GEAR_ALL | WOUND_ALL | (1 << PICK) | (1 << SHADOW);
        void Show(int b) { hide &= ~(1 << b); }
        if (owned) { Show(SADDLE); Show(PARTYCOL); }
        if (armor) Show(ARMOR);
        if (gear != null) foreach (var b in gear) Show(b);      // helmet, armour, armoured saddle, standard (GEAR)
        if (noSaddle) hide |= 1 << SADDLE;
        if (misc == false) hide |= 1 << MISC;
        double hr = hp ?? 1;
        foreach (var (b, t) in WOUNDS) if (hr <= t) Show(b);
        return hide;
    }

    public static int WoundStage(double hr) { int n = 0; foreach (var (_, t) in WOUNDS) if (hr <= t) n++; return n; }
}

/// <summary>The parts of a unit (riders, turrets, build-ups, flags, wagons) and its render mask.</summary>
public sealed class Composite
{
    readonly Unit _u;
    /// <summary>attached parts (rider, turrets, build-ups, flags ...)</summary>
    public List<AttachedPart> Parts = new();
    /// <summary>first turret: aims at the target, shots leave from its Proj link (combat.muzzle)</summary>
    public AttachedPart? Top;
    /// <summary>the add-on that fires the unit's ranged weapon (Compose.GunSpec): plays its clip at each shot (Fire)</summary>
    public AttachedPart? Gun;
    public List<AttachedPart> Turrets = new();
    /// <summary>turrets that shoot by themselves (a weapon class: the titan's ballistas; combat.gunnerUpdate)</summary>
    public List<AttachedPart> Gunners = new();
    public AttachedPart? Rider;
    /// <summary>trailer following the drawbar's hitch (FlexLinkAction); the view makes it follow</summary>
    public AttachedPart? Wagon;
    public AttachedPart? Bar;
    string _maskKey = "";
    public string Key = "";
    /// <summary>open transporters: the free passenger links of the build-up (in order)</summary>
    public List<string> Seats = new();
    /// <summary>the passengers shown on them (a part per passenger, Passenger set)</summary>
    public List<AttachedPart> Riding = new();
    string _paxKey = "";
    public Compose.DeployDef? Dep;
    double _checkT;

    public Composite(Unit u) { _u = u; }

    public void Dispose()
    {
        ClearRiding();
        Seats = new List<string>();
        foreach (var p in Parts) p.Anim?.Stop();
        Wagon?.Anim?.Stop();
        Parts = new List<AttachedPart>(); Turrets = new List<AttachedPart>(); Gunners = new List<AttachedPart>();
        Top = Gun = Rider = Wagon = Bar = null;
        Key = "";
    }

    List<CompositeSpec> Specs() => _u.Owner != null && !Wild() ? _u.World.Data.Composites(_u.Name) : new List<CompositeSpec>();
    bool Wild() => _u.Cls == "ANML" && _u.World.Data.IsWildClass(_u.Name);

    /// <summary>signature of what should be shown: rebuilt when it changes (level up, upgrade, epoch)</summary>
    (List<CompositeSpec> Act, string Key) Wanted()
    {
        var W = _u.World;
        var act = Compose.ActiveParts(W, _u, Specs());
        return (act, string.Join(",", act.Select(s => s.I + ":" + Compose.PartGfx(W, s, _u))));
    }

    public void Build()
    {
        var u = _u; var W = u.World;
        Dispose();
        var (act, key) = Wanted();
        Key = key;
        var made = new Dictionary<int, AttachedPart>();
        var gun = Compose.GunSpec(act);
        foreach (var s in act)
        {
            var gfx = Compose.PartGfx(W, s, u);
            var parent = s.Pi >= 0 ? made.GetValueOrDefault(s.Pi) : null;
            if (s.Pi >= 0 && parent == null) continue;
            if (s.Flex) { MakeWagon(s, gfx, parent); continue; }
            var p = Compose.Attach(W, u, parent, parent != null ? parent.Gfx : u.Gfx, s.Link, gfx);
            if (p == null) continue;
            p.Spec = s;
            made[s.I] = p;
            Parts.Add(p);
            if (s.Kind == "turret" && s.WeaponClass != null) { p.WClass = s.WeaponClass; Gunners.Add(p); }
            else if (s.Kind == "turret") { Turrets.Add(p); Top ??= p; }
            if (s.Kind == "drawbar" && Bar == null) Bar = p;
            if (s.Kind == "rider")
            {
                Rider ??= p;
                p.Idle = s.Anim ?? "ride_idle_0";
                p.Attack = s.AttackAnim ?? (p.Idle == "ride_idle_0" ? "ride_attack_front" : null);
            }
            Compose.DEPLOY.TryGetValue(u.Name, out var dep);
            if (dep != null && p.Anim != null && (gfx == dep.Frame || gfx == dep.Gun))
            {
                // the frame and the gun hold a pose (first frame: folded up, gun at rest) - their clips are no idle loops
                p.Dep = gfx == dep.Frame ? "frame" : "gun";
                Hold(p, p.Dep == "frame" ? "build_down" : "attack_front", u.Deploy != null && u.Deploy.State != "walk" && p.Dep == "frame");
            }
            else if (s == gun && s.Kind == "buildup" && s.Anim != null && p.Anim != null)
            {
                // a catapult build-up: its clip is the throw (on attack), held at its first frame between shots
                // (it was looped as a standing clip: the catapult kept throwing while the animal stood)
                if (p.Anim.Pick(s.Anim) is string c) Hold(p, c, false);
            }
            else if (p.Anim != null)
            {
                var a = p.Anim.Pick(s.Anim, s.Kind == "rider" ? "ride_idle_0" : null, s.Kind == "rider" ? "balista_stand" : null, "standanim");
                if (a != null && s.Kind != "turret") p.Anim.Play(a);
            }
            if (s == gun) Gun = p;
            // the pirate boss ship's parts show the ship's damage (CPirateShip.UpdateDestructionFlags: PirateShip.cs)
            if (s.Kind == World.PIRATE_PART) { p.OwnState = true; p.VisLevel = 4; p.VisDmg = World.PirateDamage(u.Hp, u.MaxHp); }
        }
        Dep = Compose.DEPLOY.GetValueOrDefault(u.Name);
        if (Dep != null && u.Deploy == null) u.Deploy = new DeployState();
        if (Dep != null && u.Deploy!.State != "walk")
        {          // (a new model while set up: lying / curled up again)
            if (Dep.Sle) u.Anim!.Play(Dep.Rest, new AnimOpts { Loop = true, Restart = true, Fade = 0 });
            else { var a = u.Anim!.Play(Dep.Rest, new AnimOpts { Loop = false, Restart = true, Fade = 0 }); if (a != null) a.Time = a.Duration; }
            u.BusyAnim = true;
        }
        // the passengers' seats: the build-up's Dri links in their order, but those its own parts (the captain) take
        var bu = Parts.FirstOrDefault(p => p.Spec!.Kind == "buildup");
        if (bu != null)
        {
            var used = new HashSet<string>(act.Where(s => s.Pi == bu.Spec!.I).Select(s => s.Link));
            var links = W.Models.File(bu.Gfx)?.Links.Keys ?? Enumerable.Empty<string>();
            Seats = links.Where(k => Regex.IsMatch(k, @"^Dri\d$") && !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
        UpdateMask(true);
        u.LookChanged();
    }

    public void ClearRiding() { foreach (var r in Riding) r.Anim?.Stop(); Riding = new List<AttachedPart>(); _paxKey = ""; }

    /// <summary>the passengers of an open transporter on their seats (playing its stand clip; the shooting clip while
    /// it shoots from up there: transport.js passengerUpdate)</summary>
    public void SyncPassengers(double dt, IReadOnlyList<Unit>? passengers)
    {
        var u = _u; var W = u.World;
        var pax = Seats.Count > 0 && passengers != null ? passengers.Where(p => p.Alive && p.Gfx != null).Take(Seats.Count).ToList() : new List<Unit>();
        var key = string.Join(",", pax.Select(p => p.Id + ":" + p.Gfx));
        if (key != _paxKey)
        {
            ClearRiding();
            _paxKey = key;
            var bu = Parts.FirstOrDefault(p => p.Spec!.Kind == "buildup");
            for (int i = 0; i < pax.Count; i++)
            {
                var p = pax[i];
                var r = Compose.Attach(W, u, bu, bu?.Gfx, Seats[i], p.Gfx);
                if (r == null) continue;
                r.Passenger = p;
                r.Stand = r.Anim?.Pick("standanim", "idle_0", "stand");
                r.Shoot = r.Anim?.Pick("bow_1", "shoot_1", "throw_1", "attack_front", "attack_1");
                if (r.Stand != null) r.Anim!.Play(r.Stand);
                Riding.Add(r);
            }
            u.LookChanged();
        }
        foreach (var r in Riding)
        {
            if (r.Anim == null) continue;
            var want = r.Shoot != null && W.Time - r.Passenger!.LastHitDone < 0.8 ? r.Shoot : r.Stand;
            if (want != null && r.Anim.CurName != want) r.Anim.Play(want, want == r.Shoot ? new AnimOpts { Loop = false, Fade = 0.1 } : null);
            r.Anim.Update(dt);
        }
    }

    /// <summary>trailers (collector wagon, carts, drum wagon) follow the drawbar like a trailer instead of hanging on a link</summary>
    void MakeWagon(CompositeSpec spec, string gfx, AttachedPart? parent)
    {
        var u = _u; var W = u.World;
        if (!W.Models.Has(gfx)) return;
        var clips = W.Models.Clips(gfx);
        Wagon = new AttachedPart { Spec = spec, Gfx = gfx.ToLowerInvariant(), Link = spec.Link, Parent = parent, Flex = true, Fourcc = W.Models.Fourcc(gfx), Anim = clips.Count > 0 ? new AnimCtl(clips, u) : null };
    }

    string? _wagonWalk; bool _wagonWalkSet;
    /// <summary>the trailer's clip: its walk while the unit moves, still otherwise (its position is the view's)</summary>
    public void SyncWagon(double dt)
    {
        var w = Wagon;
        if (w?.Anim == null) return;
        // not every trailer has a "walk_1" clip (some epoch variants of the collector wagon only have others):
        // take the walk clip it has, or leave it still
        if (!_wagonWalkSet) { _wagonWalkSet = true; _wagonWalk = w.Anim.Pick("walk_1", "walk_2", "walk_3", "walk_0") ?? w.Anim.Clips.Names.FirstOrDefault(n => n.Contains("walk") && !n.Contains('#')); }
        double v2 = _u.Vel.X * _u.Vel.X + _u.Vel.Y * _u.Vel.Y + _u.Vel.Z * _u.Vel.Z;
        bool moving = v2 > 0.05;
        if (moving && _wagonWalk != null) w.Anim.Play(_wagonWalk, new AnimOpts { Ts = Math.Min(2, Math.Sqrt(v2) / 2.5) });
        else if (w.Anim.Cur != null) w.Anim.Cur.TimeScale = 0;
        w.Anim.Update(dt);
    }

    /// <summary>render mask of the animal/vehicle itself: saddle and party colour when owned, armour inventions, wounds</summary>
    public void UpdateMask(bool force)
    {
        var u = _u;
        if (u.Fourcc != "Anim" && u.Fourcc != "Vehi") return;
        var p = u.Owner;
        double hr = u.Alive ? u.Hp / u.MaxHp : 1;
        // (an arena animal - CArenaAnimal: mission 10's arena_allosaurus, "Warden's Pet" of player 5 - is a beast that
        // fights for its owner, never tamed: no saddle, harness or party colour, as a wild one)
        bool wild = Wild(), owned = p != null && !wild && !Compose.WildLook(_u.World.Data, _u.Name);
        // the gear inventions show on the animal (Animal.usl UpdateGfxFlags of each class)
        var gear = new List<int>();
        bool noSaddle = false;
        if (owned && p!.Tt != null && Compose.GEAR.TryGetValue(u.Name, out var rule))
            foreach (var (inv, bits, saddleOff) in rule) if (p.Tt.Invented(inv)) { gear.AddRange(bits); if (saddleOff) noSaddle = true; }
        var key = (owned ? 1 : 0) + "|" + string.Join(",", gear) + "|" + Compose.WoundStage(hr);
        if (key == _maskKey && !force) return;
        _maskKey = key;
        u.HideMask = Compose.AnimalMask(owned, gear, noSaddle, hr, MiscShown(wild));
        if (owned && !u.PartyTinted) u.PartyTinted = true;
        foreach (var q in Parts) if (q.Fourcc != "Char" && q.Fourcc != "Anim" && !q.OwnState) { q.VisLevel = 4; q.VisDmg = 0; }
        u.LookChanged();
    }

    /// <summary>
    /// the animal's "misc" parts (VIS_FLAG_ANML_MISC): on a wild animal only what is part of the beast itself - the
    /// plates, shells, the mammoth's coat (MIRAGE FightingObj.ShowAnmlMiscFlag); the platforms and racks of the others
    /// are tack. Tamed ones (Base Animal.usl OnInit): hidden on ankylosaurus, brachiosaurus and parasaurolophus unless
    /// they carry a weapon build-up (catapult, gatling), on the lumber mammoth and on arena animals
    /// </summary>
    bool MiscShown(bool wild)
    {
        var u = _u;
        if (wild) return WILD_MISC_Match(u.Gfx ?? u.Name);
        if (u.Name == "hu_mammoth_lumber_upgrade" || u.Def?.Script == "CArenaAnimal") return false;
        if (TamedNoMiscMatch(u.Def?.Script ?? "")) return Parts.Any(p => CatapultMatch(p.Spec!.Gfx));
        return true;
    }
    static bool WILD_MISC_Match(string s) => Regex.IsMatch(s, "^(stegosaurus|kentrosaurus|mammoth|macrolemy[sd]_(land|water))$", RegexOptions.IgnoreCase);
    static bool TamedNoMiscMatch(string s) => Regex.IsMatch(s, "^C(Ankylosaurus|Brachiosaurus|Parasaurolophus)$");
    static bool CatapultMatch(string s) => Regex.IsMatch(s, "catapult|gatling");

    /// <summary>a part's clip held at its first or last frame</summary>
    static void Hold(AttachedPart p, string clip, bool end)
    {
        var a = p.Anim?.Play(clip, new AnimOpts { Loop = false, Restart = true, Fade = 0 });
        if (a == null) return;
        a.Time = end ? a.Duration : 0;
        a.Paused = !end;
    }

    public AttachedPart? Part(string kind) => Parts.FirstOrDefault(p => p.Dep == kind);

    /// <summary>set up to shoot: false while it is still setting up (the unit waits); the state lives on the unit (saved games)</summary>
    public bool DeployReady(double time)
    {
        var u = _u; var d = u.Deploy;
        if (Dep == null || d == null) return true;
        if (d.State == "attack") return true;
        if (d.State == "down") { if (time < d.Until) return false; d.State = "attack"; return true; }
        d.State = "down"; d.Until = time + SlePart(Dep.Down, true);
        u.BusyAnim = true;
        // (a start / loop / end clip played looping: its start part, then its loop part held while it shoots)
        u.Anim!.Play(Dep.Rest, new AnimOpts { Loop = Dep.Sle, Restart = true, Fade = 0.15 });
        var f = Part("frame");
        if (f?.Anim != null) { var a = f.Anim.Play("build_down", new AnimOpts { Loop = false, Restart = true, Fade = 0 }); if (a != null) a.Paused = false; }
        return false;
    }

    /// <summary>before it moves: get up again; true while it is still getting up</summary>
    public bool HoldMove(double time)
    {
        var u = _u; var d = u.Deploy;
        if (Dep == null || d == null || d.State == "walk") return false;
        if (d.State == "up")
        {
            if (time < d.Until) return true;
            d.State = "walk"; u.BusyAnim = false;
            return false;
        }
        d.State = "up"; d.Until = time + SlePart(Dep.Up, false);
        u.BusyAnim = true;
        if (Dep.Sle)
        {
            // its end part (uncurls, gets up: SwitchToWalkMode AnimAction(clip, 0 / 1, no intro, outro)); the turrets turn
            // back (Break: ResetRotation)
            u.Anim!.Play(Dep.Rest + "#e", new AnimOpts { Loop = false, Restart = true, Fade = 0.08 });
            foreach (var t in Turrets) t.Yaw = 0;
        }
        else
        {
            var a = u.Anim!.Play(Dep.Rest, new AnimOpts { Loop = false, Restart = true, Fade = 0.1 });
            if (a != null) { double len = a.Duration; a.TimeScale = -len / Math.Max(0.5, Dep.Up); a.Time = len; }
        }
        var f = Part("frame");
        if (f?.Anim != null) { var b = f.Anim.Play("build_up", new AnimOpts { Loop = false, Restart = true, Fade = 0 }); if (b != null) b.Paused = false; }
        var g = Part("gun");
        if (g != null) g.Yaw = 0;                              // (ResetRotation)
        return true;
    }

    /// <summary>the length of a Sle clip's start (or end) part from the model's loop marks, else the table's</summary>
    double SlePart(double table, bool start)
    {
        if (Dep is not { Sle: true } || _u.Anim?.LoopOf(Dep.Rest) is not { } lp) return table;
        return start ? lp.T0 : Math.Max(0, _u.Anim.Duration(Dep.Rest) - lp.T1);
    }

    /// <summary>set up: the gun turns to the target (CanRotate; a body that curls up: its turret); returns its error</summary>
    public double AimGun(double x, double z, double dt)
    {
        var g = Part("gun");
        if (g == null) return Turrets.Count > 0 ? Aim(x, z, dt) : 0;
        var u = _u;
        double want = SMath.Atan2(-(x - u.Pos.X), -(z - u.Pos.Z)) - u.Heading;
        double cur = g.Yaw;
        double diff = want - cur; diff = SMath.Atan2(SMath.Sin(diff), SMath.Cos(diff));
        double step = Math.Min(Math.Abs(diff), 1.5 * dt);
        g.Yaw = cur + Math.Sign(diff) * step;
        return Math.Abs(diff) - step;
    }

    /// <summary>a shot of the unit's ranged weapon from its gun add-on (Compose.GunSpec): the add-on's clip once</summary>
    public void Fire()
    {
        var g = Gun;
        if (g?.Anim == null || g.Dep != null) return;
        var c = g.Anim.Pick(Compose.GunClips(g.Spec!));
        var a = c != null ? g.Anim.Play(c, new AnimOpts { Loop = false, Restart = true, Fade = 0 }) : null;
        if (a != null) a.Paused = false;
    }

    /// <summary>a shot: the gun's recoil clip once</summary>
    public void Fired() { var g = Part("gun"); if (g?.Anim != null) { var a = g.Anim.Play("attack_front", new AnimOpts { Loop = false, Restart = true, Fade = 0 }); if (a != null) a.Paused = false; } }

    /// <summary>
    /// turn the turrets towards a world point (relative to the carrier's heading); returns the first turret's error.
    /// (The JS turned each turret in its link's own frame - turnPart; here the turret's angle is relative to the
    /// carrier's heading, the view applies it in the link's frame.)
    /// </summary>
    public double Aim(double x, double z, double dt)
    {
        if (Turrets.Count == 0) return 0;
        double? first = null;
        foreach (var t in Turrets)
        {
            double d = TurnPart(t, x, z, dt, 3);
            first ??= d;
        }
        return first ?? 0;
    }

    double TurnPart(AttachedPart t, double x, double z, double dt, double speed)
    {
        var u = _u;
        double dx = x - u.Pos.X, dz = z - u.Pos.Z;
        if (dx * dx + dz * dz < 1e-6) return 0;
        double want = Geo.HeadingTo(dx, dz) - u.Heading;
        double diff = want - t.Yaw; diff = SMath.Atan2(SMath.Sin(diff), SMath.Cos(diff));
        double step = Math.Min(Math.Abs(diff), speed * dt);
        t.Yaw += Math.Sign(diff) * step;
        return Math.Abs(diff) - step;
    }

    /// <summary>
    /// The carrier died (Combat.UnitDied): its parts stop where they are. A dead unit's parts are not updated any more,
    /// so a running clip of theirs went on in the view - the Dustriders' ankylosaurus catapult (its build-up plays
    /// "attack_front" as its standing clip) kept swinging on the dead body. Riders and gunners take their sitting pose
    /// (no rider model has a clip of dying), every other part (catapults, turrets, howdahs, cranes) its standing clip,
    /// else the first frame of the clip it had - held, not running.
    /// </summary>
    public void Died()
    {
        foreach (var p in Parts)
        {
            if (p.Anim == null) continue;
            var rest = p.Spec!.Kind == "rider" ? p.Anim.Pick(p.Idle, "ride_idle_0", "riding_base", "balista_stand", "standanim")
                : p.Anim.Pick("standanim", "idle_0", "stand");
            var clip = rest ?? p.Anim.CurName?.Split('#')[0];
            var a = clip != null ? p.Anim.Play(clip, new AnimOpts { Loop = false, Restart = true, Fade = 0 }) : null;
            if (a != null) { a.Time = 0; a.Paused = true; }
            else if (p.Anim.Cur != null) p.Anim.Cur.Paused = true;
        }
        foreach (var w in Riding.Append(Wagon)) if (w?.Anim?.Cur != null) w.Anim.Cur.Paused = true;
    }

    public void Update(double dt, bool attacking, IReadOnlyList<Unit>? passengers = null)
    {
        var u = _u;
        // got up again: free to move and idle (compose.js DEPLOY)
        var d = Dep != null ? u.Deploy : null;
        if (d != null && d.State == "up" && u.World.Time >= d.Until) { d.State = "walk"; u.BusyAnim = false; u.Anim!.Play(u.IdleAnim); }
        double now = u.World.Time;
        foreach (var p in Parts)
        {
            p.Anim?.Update(dt);
            if (p.Spec!.Kind == "rider" && p.Anim != null && p.Attack != null)
            {
                // a gunner shows its attack pose while its turret shoots, the main rider while the unit fights
                var host = p.Spec.Pi >= 0 ? Parts.FirstOrDefault(q => q.Spec!.I == p.Spec.Pi) : null;
                var want = (host != null && host.WClass != null ? now < host.ShotT + 1 : attacking) ? p.Attack : p.Idle;
                if (p.Anim.CurName != want && p.Anim.Has(want)) p.Anim.Play(want);
            }
        }
        foreach (var p in Parts) if (p.Spec!.Kind == World.PIRATE_PART && u.Alive) { int pd = World.PirateDamage(u.Hp, u.MaxHp); if (pd != p.VisDmg) { p.VisDmg = pd; u.LookChanged(); } }
        _checkT -= dt;
        if (_checkT <= 0)
        {           // level up / upgrade / new epoch: rebuild when the wanted parts change
            _checkT = 1;
            if (Wanted().Key != Key) Build();
        }
        SyncWagon(dt);
        if (Seats.Count > 0 || Riding.Count > 0) SyncPassengers(dt, passengers);
        UpdateMask(false);
    }
}
