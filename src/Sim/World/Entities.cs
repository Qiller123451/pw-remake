// Units (characters, animals, vehicles, ships), buildings, resource nodes and projectiles of the world.
//
// Ported from remake/src/game/entities.js (pw-exporter 98c7703).
//
// The JS entities held their three.js models; these hold only the simulation state. What the view needs to draw
// an entity is plain data on it (the "look"): its model (Gfx), its animation (Anim: clip, time, speed, loop - Anim.cs),
// the weapon in hand, the tool, the carried goods, the attached parts (Comp / Parts: riders, turrets, build-ups,
// cranes - with their own animations and turret angles), the building state the JS applied to the model
// (ConstructLevel, DamageStage, Age, ArmMask, WallParts, GateEnds, Night, ReadyFx), the render mask of animals and
// vehicles (HideMask: saddle, party colour, armour, wounds) and the script visibility flags (VisFlags). LookVersion
// counts every change of these and LookChangedEvent tells about it (AnimCtl.Serial counts clip starts).
// What the JS read from the template (links, footprints, walk speeds, clip lengths, sounds) comes from the
// ModelManifest (src/Sim/Content).
//
// The classes are partial: a system port adds the fields only it uses in its own file under src/Sim/Systems.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>The rules owner of an object with its own local tree (Entity.RulesOwner).</summary>
public sealed class LocalOwner : IRulesOwner
{
    readonly Player _o;
    public LocalOwner(Player o, TechTree tt) { _o = o; Tt = tt; }
    public TechTree? Tt { get; }
    public string Tribe => _o.Tribe;
    public bool Debug => _o.Debug;
    public int MaxEpoch => _o.MaxEpoch;
    public bool ForeignActions => _o.ForeignActions;
}

/// <summary>A production / upgrade queue entry (systems/production.js); the production port adds its fields.</summary>
public sealed partial class QueueItem
{
    public ActionDef Action = null!;
    /// <summary>the Aje BuildDown item: the building is being taken down</summary>
    public bool BuildDown;
}

public abstract partial class Entity : IHashed, IActionUser
{
    public readonly int Id;
    public readonly World World;
    /// <summary>"unit" | "building" | "res"</summary>
    public readonly string Kind;
    /// <summary>Player or null (wild / neutral)</summary>
    public Player? Owner;
    public V3 Pos;
    public double Heading;
    public double Radius = 1;
    public bool Alive = true;
    public double Hp = 1, MaxHp = 1;
    /// <summary>status effects, auras, camouflage, poison (systems/effects.js)</summary>
    public readonly Status St = new();
    /// <summary>special move cooldowns: action id -> game time when ready again</summary>
    public readonly OrderedMap<string, double> Cd = new();
    /// <summary>time of the last attack (the next is allowed after the weapon's duration)</summary>
    public double LastHitDone = -99;
    /// <summary>who hit us (targets we may fight back against)</summary>
    public readonly OrderedSet<Entity> Attackers = new();
    /// <summary>own tech tree for "local" upgrades (farm modes, big tent, defensive mode ...)</summary>
    public LocalTree? LocalTT;
    /// <summary>post the unit returns to after automatic fights (m_vAggressionPos): (x, z)</summary>
    public double AnchorX, AnchorZ;
    /// <summary>"local" inventions of this object (Rules.Invented)</summary>
    public OrderedSet<string>? LocalInventsSet;

    // class data (units and buildings)
    public string Name = "";
    public ObjectDef? Def;
    /// <summary>CHTR | ANML | VHCL | SHIP | BLDG (res: the node type)</summary>
    public string Cls = "";
    public int Level { get; set; } = 1;
    public LevelStats? Stats;
    public WeaponSet Weapons = new();
    public Weapon? Weapon;
    public double Fow;
    public int TtVersion;
    public double DeadT;
    public bool Removed, Parked, Inside, SilentDeath, Untargetable, Unselectable;
    /// <summary>drawn this many times its size (a mission's giant beasts: the sequel's sqScale); 1 = as the model</summary>
    public double DrawScale = 1;
    public List<QueueItem> Queue = new();
    /// <summary>rally point of a producer (building, Aje collector) and the resource it points at</summary>
    public (double X, double Z)? Rally;
    public Entity? RallyTarget;

    // ---- the look (view-facing state)
    /// <summary>the model drawn (lower case or as the tech tree has it)</summary>
    public string? Gfx;
    /// <summary>how far the model reaches out from the object's place (ModelManifest.SelReach; 0: unknown) - the
    /// selection circle's size (SelectionRing)</summary>
    public double SelReach;
    /// <summary>its animation (null: the model has no clips)</summary>
    public AnimCtl? Anim;
    /// <summary>counts changes of the look (model, parts, flags, clip starts)</summary>
    public int LookVersion;
    /// <summary>raised when the look changed (the view may also compare LookVersion once per frame)</summary>
    public event Action<Entity>? LookChangedEvent;
    public void LookChanged() { LookVersion++; LookChangedEvent?.Invoke(this); }

    int _hashKey = SpatialHash<Unit>.NoKey;
    public double HX => Pos.X;
    public double HZ => Pos.Z;
    public int HashKey { get => _hashKey; set => _hashKey = value; }

    protected Entity(World world, string kind, Player? owner, double x, double z)
    {
        Id = world.NextId++;
        World = world;
        Kind = kind;
        Owner = owner;
        Pos = new V3(x, world.Height(x, z), z);
        AnchorX = x; AnchorZ = z;
        world.Register(this);
    }

    ISet<string>? IActionUser.LocalInvents => LocalInventsSet == null ? null : new HashSet<string>(LocalInventsSet);

    LocalOwner? _ro;
    /// <summary>the tree rules are read from: the owner's, or this object's own local tree</summary>
    public IRulesOwner? RulesOwner()
    {
        if (LocalTT == null || Owner == null) return Owner;
        return _ro ??= new LocalOwner(Owner, LocalTT);
    }
    protected void ResetRulesOwner() => _ro = null;

    /// <summary>the owner's party colour (0xRRGGBB) for tinting models, null = untinted</summary>
    public int? Party => Owner?.PartyColor;
    /// <summary>derived combat values of a weapon (default: the weapon in hand), after the tribe's modifiers</summary>
    public WeaponStats? Cs(Weapon? w = null) { w ??= Weapon ?? Weapons.Long; return w != null ? World.Data.WeaponStatsOf(w, Name, RulesOwner()) : null; }
    public double DistTo(Entity e) => Math.Sqrt((e.Pos.X - Pos.X) * (e.Pos.X - Pos.X) + (e.Pos.Z - Pos.Z) * (e.Pos.Z - Pos.Z));
    public virtual double EdgeDist(Entity e)
    {
        if (e is Building b) return Math.Max(0, b.SurfDist(Pos.X, Pos.Z) - (this is Building ? 0 : Radius));
        return Math.Max(0, DistTo(e) - Radius - e.Radius);
    }
    public bool IsEnemy(Entity? e)
    {
        if (e == null || !e.Alive || e == this) return false;
        if (Owner == null || e.Owner == null) return (Owner != null || e.Owner != null) && (e.Kind == "unit" || e.Kind == "building");  // wild vs player
        return Owner.IsEnemy(e.Owner);
    }
    /// <summary>is a timed state active? (effects.js active)</summary>
    public bool Active(string key) => St.Active(key, World.Time);

    public override string ToString() => $"{Kind} {Id} {Name}";
}

// ======================================================================= units (characters, animals, vehicles)
public sealed partial class Unit : Entity
{
    // units that move on land and water (naval.md §3)
    public static readonly Regex AMPHIBIOUS = new("^(aje_transport_turtle|seas_hovercraft|seas_helicopter|ninigi_baryonyx|baryonyx|macrolemys)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CannotFightRe = new("resource_collector|_cart$|trade_dino|smokebomb|drums$|siegetower|hovercraft|transport_ship|transport_boat|fishing_boat|minelayer|corsair|mineship_mine|torpedo_turtle", RegexOptions.Compiled);
    // (and the pirates' flagship of mission 6, CPirateBoss: its sail, tail and bow are buildings standing on its deck -
    // pirate_boss_sail / _tail / _row, which the mission's end counts - that cannot move with it; anchored in its bay. The
    // computer player had sent it after the player's harbour: it left its parts and the oval check_boss, and Gb01
    // ("no pirate_boss in check_boss") won the mission before a ship of the player had come near)
    static readonly Regex StationaryRe = new("mineship_mine|water_turret|^pirate_boss$", RegexOptions.Compiled);
    static readonly Regex NotInPopRe = new("mineship_mine|water_turret|torpedo_turtle", RegexOptions.Compiled);

    public UnitTask Task = UnitTask.Idle();
    public NavPath Path = new();
    /// <summary>no way (a fresh empty path - only when it had one: every step of every unit fighting in range made a
    /// new one)</summary>
    public void ClearPath() { if (Path.Count > 0 || Path.Gate != null) Path = new NavPath(); }
    public V3 Vel;
    public Carry? Carry;
    /// <summary>2 aggressive (default), 1 defensive (workers), 0 hold ground, 3 never fights on its own (poisoner), -1 none</summary>
    public int Stance;
    public bool Wild;
    public double LastHit = -99;
    public double HomeX, HomeZ;
    /// <summary>druid illusions: no moves, no levels, vanish after 60 s</summary>
    public bool Illusion;
    /// <summary>kennel / nest animals: not controlled by the player</summary>
    public bool Autonomous;
    /// <summary>moves on the water grid (World.NavFor)</summary>
    public bool Naval;
    /// <summary>land and water (transport turtle, hovercraft, baryonyx, macrolemys)</summary>
    public bool Amphib;
    /// <summary>mines, water turrets</summary>
    public bool Stationary;
    public bool CountsInPop;
    public bool Tracker, Zeppelin, Flying, Swimming;
    // movement
    public double Speed, RunSpeed, AnimSpeed, RunAnimSpeed, Height = 1.5;
    public Dictionary<string, double> Gaits = new(StringComparer.Ordinal);
    public (double X, double Z)? Goal;
    public Building? Breach;
    public (double X, double Z, double T)? NoWay;
    public double StuckT;
    public int StuckN;
    public V3 StuckFrom;
    public double RepathT, ScanT;
    // animation
    public string? WalkAnim, RunAnim, IdleAnim;
    public bool BusyAnim;
    /// <summary>in its fight position (Combat.FightPosition: the loop part of its shooting clip held between shots;
    /// CCharacter m_bTecInFightPos)</summary>
    public bool FightPos;
    /// <summary>the extra time of its last primary shot (World.FirstShot: the first shot after the weapon came up waits
    /// 0.4 s longer, Bela 0.6; CCharacter m_fWeaponDurationOvertime) - added to the time to its next shot</summary>
    public double ShotOvertime;
    public string? IdleCur;
    public double IdleLeft, FightStandT;
    // states other systems set (world.js reads them)
    public Knock? Knock;
    public JumpFx? JumpFx;
    public double JumpH, LiftH;
    public Lift? Lift;
    public double InvulnT, Fatigue;
    public PendingShot? Pending;
    public double? WallH;
    public Entity? OnWall;
    public WallJump? WJump;
    public object? Docked, DockTile;
    /// <summary>a transport's passengers (systems/transport.js)</summary>
    public List<Unit>? Passengers;
    public List<Waypoint> WPath = new();
    public bool Climbing, Finishing, Guard20;
    public double FlyChange, DisgT, AlarmT, HealedT = double.NaN;
    /// <summary>a worker building near Tesla (construction.js); the sequel's tranquilizer dart until (sequel)</summary>
    public bool FasterBuild;
    public double SqTranq;
    public List<Building>? BuildQueue;
    public double? Grow;
    public bool RideCup;
    public DeployState? Deploy;
    public Wreck? Wreck;
    public ResNode? CorpseNode;
    public bool CorpseGone;
    public object? Finish;
    public bool Night;
    public double NightT;
    // the look
    public ModelInfo? Tpl;
    public string Fourcc = "";
    public string? CamoGfx;
    /// <summary>the weapon whose parts are in the hands (attachWeapon)</summary>
    public Weapon? HeldWeapon;
    /// <summary>
    /// a stream weapon (the flamethrowers' and fire boats' flamers: a line-of-fire weapon without a projectile, 10 m or
    /// more) attacking now - a living target, its last strike within one attack's time: the view keeps the flame on
    /// between the strikes (Kacper: the flame went on and off with each attack clip; it now burns until the attack ends).
    /// For the view only, not part of the game's state.
    /// </summary>
    public bool Spraying
    {
        get
        {
            if (!Alive || Task.Type != "attack" || Task.Target is not { Alive: true }) return false;
            var w = Weapon ?? Weapons.Long;
            if (w == null || !w.Penetration || w.Projectile != null || w.Range < 10) return false;
            return World.Time - LastHitDone < World.AttackDuration(this, Cs(w)) + ShotOvertime + 0.25;
        }
    }
    /// <summary>the weapon parts are shown (hidden while a tool or a load is carried)</summary>
    public bool WeaponsVisible = true;
    /// <summary>a worker's tool at a link (attachTool): model and link; ToolShown: both exist</summary>
    public string? ToolGfx, ToolLink = "HndR";
    public bool ToolShown;
    /// <summary>the carried load's model is shown at its link (setCarry)</summary>
    public bool CarryShown;
    /// <summary>script visibility flags (SetRndInvMaskSingleFlagInv): attribute bit -> shown</summary>
    public readonly Dictionary<int, bool> VisFlags = new();
    public Composite? Comp;
    /// <summary>a dead unit's sinking into the ground (m, the view lowers the model by it)</summary>
    public double SinkY;

    public Unit(World world, string name, Player? owner, double x, double z, int level = 1, double heading = 0, UnitOpts? opts = null)
        : base(world, "unit", owner, x, z)
    {
        opts ??= UnitOpts.None;
        var D = world.Data;
        Name = name;
        Def = D.Def(name, owner)!;
        Cls = Def.Type;
        Level = level;
        Heading = heading;
        // stance: 2 aggressive (default), 1 defensive (workers), 0 hold ground, 3 never fights on its own (poisoner)
        Stance = name.Contains("worker") ? 1 : name == "aje_poisoner" ? 3 : 2;
        Wild = owner == null;
        HomeX = x; HomeZ = z;
        Illusion = opts.Illusion;
        Autonomous = opts.Autonomous;
        Naval = opts.Swim || Cls == "SHIP";
        Amphib = AMPHIBIOUS.IsMatch(name);
        if (Amphib) Naval = false;
        Stationary = opts.Stationary || StationaryRe.IsMatch(name);
        CountsInPop = !(opts.Illusion || opts.Autonomous || opts.Temp) && !NotInPopRe.IsMatch(name);
        StuckFrom = Pos;
        ApplyLevel(level, true);
    }

    public bool IsWorker => Def!.CanHarvest != 0;
    /// <summary>FightingObj.CanBuild (heroes like Tesla too)</summary>
    public bool CanBuild => Def!.CanBuild != 0 && Cls == "CHTR";
    public bool IsDropoff => Def!.Delivery.Count > 0;
    public bool CanWalk => (Stats!.MaxSpeed != 0 ? Stats.MaxSpeed : Stats.Speed != 0 ? Stats.Speed : 1) > 0 && !St.Camo.Has("entr");
    public bool CannotFight => Weapons.Long == null || CannotFightRe.IsMatch(Name);

    public void ApplyLevel(int level, bool initial = false)
    {
        bool up = !initial && level > Level;
        double ratio = initial ? 1 : Hp / MaxHp;
        Level = level;
        Weapon = null;                            // (the new level's weapon in hand: hu_axe_a -> hu_axe_b)
        ReadRules();
        Hp = initial || up ? MaxHp : Math.Max(1, ratio * MaxHp);   // a level up heals fully
        BuildModel();
    }

    /// <summary>(re)read class data, stats and weapons from the tech tree; called after level changes and tree changes</summary>
    public void ReadRules()
    {
        var D = World.Data; var O = RulesOwner();
        Def = D.Def(Name, O)!;
        Stats = D.StatsAt(Name, Level, O)!;
        MaxHp = Stats.Hp * (Grow ?? 1) * (RideCup ? 1.5 : 1);   // (a young wild animal: animals.js growthUpdate; the ride cup: items.js)
        Weapons = D.WeaponSetOf(Name, Level, O);
        if (Weapon == null || !Weapons.All.Contains(Weapon)) Weapon = Weapons.Long;
        Fow = Stats.Fow != 0 ? Stats.Fow : 30;
        TtVersion = O?.Tt?.Version ?? 0;
        World.FlyerRules(this);                   // (MIRAGE's flying units: World.Flyers)
    }

    /// <summary>the tech tree changed (upgrade, building, level filter): new values; hit point ratio kept, model swapped if needed</summary>
    public void RefreshRules()
    {
        double ratio = Hp / MaxHp;
        var gfx = Gfx;
        var w0 = Weapons.Long;
        ReadRules();
        Hp = Math.Max(1, Math.Min(MaxHp, ratio * MaxHp));
        ResetRulesOwner();
        var g = CamoGfx ?? LookGfx(Stats!.Gfx);
        if (g != gfx) BuildModel();
        else if (w0 != Weapons.Long) AttachWeapon(Weapons.Long);
        AutoMovesCache = null;
    }
    /// <summary>(moves.js: the automatic moves of the unit, recomputed after a tree change)</summary>
    public object? AutoMovesCache;

    /// <summary>switch the weapon in hand (short / medium / long)</summary>
    public void UseWeapon(Weapon? w)
    {
        if (w == null || Weapon == w) return;
        Weapon = w;
        var parts = w.Parts.Count > 0 && w.Parts[0].Gfx.Count > 0 ? w.Parts[0].Gfx[0] : null;
        var held = HeldWeapon != null && HeldWeapon.Parts.Count > 0 && HeldWeapon.Parts[0].Gfx.Count > 0 ? HeldWeapon.Parts[0].Gfx[0] : null;
        if (parts != null && parts != held) AttachWeapon(w); else HeldWeapon = w;
    }

    /// <summary>Aje shaman camouflage: looks like a harmless jungle animal</summary>
    public void SetCamoLook(bool on) { CamoGfx = on ? "parasaurolophus" : null; BuildModel(); }

    public void BuildModel()
    {
        var W = World; var M = W.Models;
        Gfx = (CamoGfx != null && M.Has(CamoGfx) ? CamoGfx : null) ?? LookGfx(Stats!.Gfx);    // (a landed pteranodon: its ground model)
        if (!M.Has(Gfx) && W.Data.ClassGfx(Name) is string cg) Gfx = cg;                 // tech tree gfx missing: class file gfx
        if (!M.Has(Gfx) && W.StandIns.TryGetValue(Name.ToLowerInvariant(), out var si)) Gfx = si;   // no model in the game data: the mission's stand-in
        Tpl = M.Get(Gfx);
        Fourcc = M.Fourcc(Gfx);
        Anim?.Stop();
        Comp?.Dispose();
        Anim = new AnimCtl(M.Clips(Gfx), this);
        // animation sound events (footsteps, weapon swings, roars) from the GSF sound table
        var snd = Tpl != null && Tpl.HasSounds ? Tpl.Sounds : Tpl?.Anims != null ? M.Get(Tpl.Anims)?.Sounds : null;
        // and the effect events of its clips (camera shakes: AnimFx), on the same timeline
        Anim.SetEvents(AnimFx.With(snd, Tpl?.Anims ?? Gfx));
        // collision radius from the template size
        var (r, h) = M.UnitSize(Gfx);
        Radius = Cls == "CHTR" ? Math.Min(r, 0.7) : r;
        Height = h;
        SelReach = M.SelReach(Gfx) ?? 0;
        // speeds from the model's walk animations
        var sp = Tpl?.Speeds ?? new Dictionary<string, double>();
        var speedsTpl = sp.ContainsKey("walk_1") ? sp : (Tpl?.Anims != null ? M.Get(Tpl.Anims)?.Speeds : null) ?? new Dictionary<string, double>();
        double idx = Def!.Type == "ANML" && Owner != null ? Or(Stats!.MaxSpeed, Or(Stats.Speed, 1)) : Or(Stats!.Speed, 1);   // owned animals always use maxspeed
        double idxMax = Math.Max(idx, Or(Stats.MaxSpeed, idx));
        // the model's walk set (GSF): "defn" if it has one (the newer SEAS / hero walks walk_<n>_new), else "def"
        // (FightingObj.usl: m_xWalkSet = "defn" if HasWalkSet). Slots 1..3 = walk speeds.
        var ws = Tpl?.Walk ?? new Dictionary<string, string>();
        string Wn(double i) { var k = TT.JsString(i); return ws.TryGetValue(k, out var v) ? v : "walk_" + k; }
        string WalkN(double i) => "walk_" + TT.JsString(i);
        WalkAnim = Anim.Pick(Wn(idx), WalkN(idx), Wn(2), "walk_2", "walk_1");
        RunAnim = Anim.Pick(Wn(idxMax), WalkN(idxMax), Wn(idx), "walk_2") ?? WalkAnim;
        Gaits = speedsTpl;
        Speed = Or(Sp(speedsTpl, WalkN(idx)), Or(Fallback(idx), 3));
        RunSpeed = Or(Sp(speedsTpl, WalkN(idxMax)), Or(Fallback(idxMax), Speed));
        AnimSpeed = Or(Sp(speedsTpl, Wn(idx)), Speed); RunAnimSpeed = Or(Sp(speedsTpl, Wn(idxMax)), RunSpeed);
        if (Wild) { WalkAnim = Anim.Pick(Wn(1), "walk_1", WalkAnim); Speed = Or(Sp(speedsTpl, "walk_1"), 1.5); AnimSpeed = Or(Sp(speedsTpl, Wn(1)), Speed); }
        IdleAnim = Anim.Pick(Def.StandAnim, "standanim", "idle_0", "stand");
        W.FlyerLook(this);                        // (a flyer's stand and walk clips: World.Flyers)
        Anim.Play(IdleAnim);
        // weapon parts on hand links - on the new model: the old one's weapon, tool and load went with it
        HeldWeapon = null; ToolShown = false; CarryShown = false; WeaponsVisible = true;
        AttachWeapon(Weapon != null && Weapons.All.Contains(Weapon) ? Weapon : Weapons.Long);
        if (ToolGfx != null) { var t = ToolGfx; ToolGfx = null; AttachTool(t, ToolLink ?? "HndR"); }
        if (Carry != null) SetCarry(Carry.Res, Carry.Amount, Carry.Look);
        // build-ups, captain, collector wagon, render mask (saddle / party colour / armour / wounds)
        Comp = new Composite(this);
        Comp.Build();
        LookChanged();
    }
    static double Or(double v, double d) => v != 0 && !double.IsNaN(v) ? v : d;
    static double Sp(Dictionary<string, double> s, string k) => s.TryGetValue(k, out var v) ? v : 0;
    static double Fallback(double i) => i >= 0 && i < Rules.Rules.SPEED_FALLBACK.Length && i == Math.Floor(i) ? Rules.Rules.SPEED_FALLBACK[(int)i] : 0;

    /// <summary>does the unit's model have this link</summary>
    public bool FindLink(string? name) => name != null && World.Models.File(Gfx) is { } f ? f.Links.ContainsKey(name) : name != null && World.Models.Has(Gfx);

    public void AttachWeapon(Weapon? w)
    {
        if (HeldWeapon == w) return;
        HeldWeapon = w;
        WeaponsVisible = true;
        LookChanged();
    }

    public void AttachTool(string? gfx, string linkName = "HndR")
    {
        if (ToolShown && ToolGfx == gfx) return;
        if (ToolShown) ToolShown = false;
        ToolGfx = gfx; ToolLink = linkName;
        if (gfx == null) { if (!CarryShown) WeaponsVisible = true; LookChanged(); return; }
        if (!FindLink(linkName) || !World.Models.Has(gfx)) { LookChanged(); return; }
        WeaponsVisible = false;
        ToolShown = true;
        LookChanged();
    }

    /// <summary>
    /// carried resources (SetThing): wood / field harvest on the shoulder (walk set "sldr"), stone and food in the
    /// tribe's container (walk set "cary": Hu pannier, Aje clay jug, Ninigi basket, SEAS backpack).
    /// look = [link, gfx, walkset] from economy.carryLook()
    /// </summary>
    public void SetCarry(string? res, double amount, string[]? look)
    {
        var prevLook = Carry?.Look;
        Carry = res != null && amount > 0 ? new Carry { Res = res, Amount = amount, Look = look ?? prevLook } : null;
        CarryShown = false;
        if (Carry == null || Carry.Look == null) { if (!ToolShown) WeaponsVisible = true; LookChanged(); return; }
        var l = Carry.Look;
        if (!FindLink(l[0]) || !World.Models.Has(l[1])) { LookChanged(); return; }
        CarryShown = true;
        WeaponsVisible = false;
        LookChanged();
    }

    /// <summary>script visibility flag (SetRndInvMaskSingleFlagInv) on the unit's own parts</summary>
    public void SetVisFlag(int bit, bool on) { VisFlags[bit] = on; LookChanged(); }

    public string WalkSet() => Carry == null ? "" : Carry.Look != null ? Carry.Look[2] : Carry.Res == "wood" ? "shoulder" : "carry";

    public string? StandAnim()
    {
        var ws = WalkSet();
        if (ws == "shoulder") return Anim!.Pick("shoulder_standanim") ?? IdleAnim;
        if (ws == "carry") return Anim!.Pick("standing") ?? IdleAnim;
        if (FightStandT > 0) return Anim!.Pick(FightStand()) ?? IdleAnim;
        return IdleAnim;
    }

    /// <summary>
    /// idle behaviour from IdleAnims.txt: weighted random choice of stand / idle animations, each played as the engine's
    /// start / loop / end clip - its start part, min_loops .. max_loops of its loop part, its end part (AnimCtl.PlayLoops;
    /// a clip without loop marks: that many times whole) - then the next. The end part of what the unit did before (a
    /// worker's hammering or chopping, a healer's heal_0, a shot's raised bow) plays out first: the original ends those
    /// actions with it (AnimActionStopLoop) before the idle animation is set.
    /// </summary>
    public void IdleTick(double dt)
    {
        if (BusyAnim) return;
        var A = Anim!;
        // out of its fight position: its standing clip, after the end part of the shooting clip (the bow goes down)
        if (FightPos) { FightPos = false; if (A.Phase == "l" && IdleAnim != null) { IdleCur = null; A.Play(IdleAnim); return; } }
        // an end part running: let it finish
        if (A.Phase == "e" && !A.Done) return;
        // a task's looping start / loop / end clip still running (the task ended): its end part first
        if ((A.Phase == "s" || A.Phase == "l" || A.Phase == "t") && A.CurName != null && A.CurName != IdleCur && A.CurName != IdleAnim && A.Has(A.CurName + "#e")) { IdleCur = null; A.StopLoop(); return; }
        if (Carry != null || FightStandT > 0) { IdleCur = null; A.Play(StandAnim()); return; }
        // a flyer: "idle" in the air, "standanim" on the ground (MIRAGE CAjePteranodon.SetIdleAnim - not IdleAnims.txt's "fly")
        if (Flyer != null && World.FlyerIdleClip(this) is { } fc) { IdleCur = null; A.Play(fc); return; }
        // the T-rex (CTrex.SetIdleAnim, Animal.usl:4181): idle_0 .. idle_4, no standing clip (WildIdle.cs)
        if (World.IsTrex(this) && World.TrexIdle(this)) return;
        var set = World.Data.IdleSet(Name);
        if (set == null) { A.Play(IdleAnim); return; }
        // the idle animation chosen last runs until its loops are done
        if (IdleCur != null && A.CurName == IdleCur && !A.Done) return;
        var opts = set.Where(e => A.Has(e.Anim)).ToList();
        if (opts.Count == 0) { A.Play(IdleAnim); return; }
        double r = World.Rng.Next() * opts.Sum(e => e.Weight);
        var pick = opts[0];
        foreach (var e in opts) { r -= e.Weight; if (r <= 0) { pick = e; break; } }
        int loops = pick.MinLoops + (int)Math.Floor(World.Rng.Next() * (pick.MaxLoops - pick.MinLoops + 1));
        IdleCur = pick.Anim.ToLowerInvariant();
        IdleLeft = A.PlayLoops(IdleCur, loops, true, true, 1, null, 0.25);
    }

    /// <summary>caste specific combat stance (res/nat/tec)</summary>
    public string? FightStand()
    {
        var c = Def!.Caste ?? (Owner != null && Owner.Tribe == "SEAS" ? "tec" : "nat");
        return c == "tec" ? Anim!.Pick("tec_fightpos_standanim", "res_fight_standanim") : c == "res" ? Anim!.Pick("res_fight_standanim", "nat_fight_standanim") : Anim!.Pick("nat_fight_standanim", "res_fight_standanim");
    }

    // -------------------------------------------------------- movement
    public bool SetPath(double x, double z)
    {
        var W = World; var nav = W.NavFor(this);
        // MIRAGE's flying units: the engine's flight at 15 m, own target apart from the other flyers (World.FlyTo;
        // a pteranodon on the ground walks)
        if (Flyer is { Ground: false }) return W.FlyTo(this, x, z);
        // in the air (MIRAGE jetpack flight): straight there, over walls and water (Hu.usl:277)
        if (Flying) { Path = new NavPath { new Waypoint(x, z) }; Goal = (x, z); Breach = null; NoWay = null; StuckT = 0; StuckFrom = Pos; return true; }
        // a goal it found no way to a moment ago: keep going the way it has (the searches that fail are the costly ones)
        if (NoWay is var (nx, nz, nt) && W.Time - nt < 3 && Math.Abs(nx - x) < 1 && Math.Abs(nz - z) < 1) { Goal = (x, z); StuckT = 0; StuckFrom = Pos; return Path.Count > 0; }
        // a group given the same order in the same moment (a player's army, a computer player's attack wave): a unit
        // standing near the one that searched first, with a clear line to the start of its way, takes that way and only
        // its own end. Each searching alone cost 20-40 ms per unit across a campaign map.
        int need = nav.Need(Radius); var S = W.PathShare;
        if (Radius <= 1 && S != null && S.T == W.Time && S.Nav == nav && S.Ver == nav.Version && S.Need == need && S.Owner == Owner &&
            Hypot(S.Tx - x, S.Tz - z) < 40 && Hypot(S.Sx - Pos.X, S.Sz - Pos.Z) < 16)
        {
            var P = S.Path; Waypoint? prev = P.Count > 1 ? P[^2] : null;
            // (a way that does not reach the goal - across water, behind a wall - ends where the first one's ends, and the
            // failed search is not repeated: the costly ones)
            if (prev is Waypoint pv && nav.Los(Pos.X, Pos.Z, P[0].X, P[0].Z, need) && (!S.Found || (nav.IsFree(x, z) && nav.Los(pv.X, pv.Z, x, z, need))))
            {
                Path = new NavPath(P);
                if (S.Found) Path[^1] = new Waypoint(x, z);
                Goal = (x, z);
                Breach = null; NoWay = S.Found ? null : (x, z, W.Time);
                StuckT = 0; StuckFrom = Pos;
                return true;
            }
        }
        var p = nav.Find(Pos.X, Pos.Z, x, z, Radius, 30000, Owner);
        // big units (spider walkers, dinosaurs) squeeze through the narrow passes of the original maps' walk grid
        // rather than not going at all
        if (!nav.Found && Radius > 1) { var q = nav.Find(Pos.X, Pos.Z, x, z, 0.7, 30000, Owner); if (nav.Found) p = q; }
        // the way is shut by an enemy gate: attackers (attack-move, any move of a computer player) break through it
        Breach = null;
        var t = Task;
        if (!nav.Found && nav.GateAt.Count > 0 && (t.Type == "attackmove" || (t.Type == "move" && Owner != null && !Owner.Human)))
        {
            var q = nav.Find(Pos.X, Pos.Z, x, z, Math.Min(Radius, 0.7), 30000, Owner, true);
            if (q != null && q.Gate != null) { p = q; Breach = q.Gate; }
        }
        NoWay = nav.Found || Breach != null ? null : (x, z, W.Time);
        // a big unit standing where its clearance doesn't fit (a black widow at the foot of a dune) gets a way that starts
        // at the nearest cell that does - maybe behind a blocked one: the narrow way there first, or it stood forever
        if (p != null && p.Count > 0 && Radius > 1 && !nav.Los(Pos.X, Pos.Z, p[0].X, p[0].Z, 1))
        {
            bool was = nav.Found;
            var lead = nav.Find(Pos.X, Pos.Z, p[0].X, p[0].Z, 0.7, 4000, Owner);
            if (nav.Found && lead != null && lead.Count > 0)
            {
                var np = new NavPath(lead.Select(w => new Waypoint(w.X, w.Z, true))); np.AddRange(p.Skip(1)); np.Gate = p.Gate; p = np;   // (exact: steer)
            }
            nav.Found = was;
        }
        Path = p ?? new NavPath();
        Goal = (x, z);
        StuckT = 0; StuckFrom = Pos;
        // (offered to the others of the group: a way of more than one leg, no gate to break)
        if (Breach == null && Radius <= 1 && p != null && p.Count > 1) W.PathShare = new PathShare { T = W.Time, Nav = nav, Ver = nav.Version, Need = need, Owner = Owner, Found = nav.Found, Sx = Pos.X, Sz = Pos.Z, Tx = x, Tz = z, Path = p };
        return Path.Count > 0;
    }
    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    /// <summary>is a unit next to this one, nearer the point (tx, tz) than this one's distance d, standing or on its way
    /// to the same point?</summary>
    public bool QueuedBehind(double tx, double tz, double d)
    {
        bool found = false;
        var g = Goal;
        World.UHash.Query(Pos.X, Pos.Z, Radius + 4, o =>
        {
            if (found || o == this || !o.Alive || o.Inside || o.Parked) return;
            if (o.Path.Count > 0 && !(g != null && o.Goal != null && Math.Abs(o.Goal.Value.X - g.Value.X) < 0.5 && Math.Abs(o.Goal.Value.Z - g.Value.Z) < 0.5)) return;
            // one standing at its own place of a formation, not at this one's: walk round it
            if (o.Path.Count == 0 && o.Goal != null && g != null && Hypot(o.Goal.Value.X - g.Value.X, o.Goal.Value.Z - g.Value.Z) > 0.5 && Hypot(o.Pos.X - tx, o.Pos.Z - tz) > Radius + o.Radius + 1) return;
            if (Hypot(o.Pos.X - Pos.X, o.Pos.Z - Pos.Z) < Radius + o.Radius + 1 && Hypot(o.Pos.X - tx, o.Pos.Z - tz) < d) found = true;
        });
        return found;
    }

    /// <summary>a unit coming the other way in this one's way (ux, uz: the way's direction): the direction to walk to pass
    /// it, or null. The way finder knows no units and the separation pushes only along the line between two: two units
    /// whose ways ran head-on through each other walked into each other for good (player report, MIRAGE 13: two SEAS
    /// mechs of a computer player in its base "kept going straight at each other forever"). The original's engine steers
    /// round (the scripts only step a fighter aside at random when it stands in another - FightingObj CheckUnitCollision).
    /// Both turn off to opposite sides, decided alike by both from the pair: the one with the lower id goes away from the
    /// other across its own way (exactly head-on: to its left), the other the opposite way - so they part even when one
    /// sees the other a hair to the left and the other a hair to the right. Not from the unit it is going to (its task's
    /// target: a transport it boards, a foe it charges) nor from one coming to it.</summary>
    (double X, double Z)? Avoid(double ux, double uz, double speed)
    {
        var W = World; var near = W.SteerNear; near.Clear();
        double ahead = Radius + 1 + Math.Min(4, speed * 0.7);
        W.UHash.Collect(Pos.X, Pos.Z, Radius + 5 + ahead, near);
        Unit? best = null; double bestF = double.MaxValue, bestL = 0, bestSum = 0;
        foreach (var o in near)
        {
            if (o == this || !o.Alive || o.Inside || o.Parked || o.Flying || o.Naval != Naval || o.OnWall != null || o.WJump != null || o.Climbing) continue;
            // (not the one it goes to or that comes to it: a squad boarding a hovercraft or turtle that drives to it, a
            // fighter charging one coming at it - stepping aside put the boarding off and the crossing came later)
            if (Task.Target == o || o.Task.Target == this) continue;
            // (not one going to the same point: a group sent to one point - an attack wave's waypoint - comes together
            // there from all sides, each one "head-on" to those across the point; turning off they circled it for good
            // and never arrived, the waypoint chain stalled - base mission 13's third outpost team, eight SEAS units
            // round 1121,785 for 45 minutes, "Hold the Fort" never accomplished. They stop behind the ones nearer it:
            // QueuedBehind)
            if (Goal is var (gx, gz) && o.Goal is var (ogx, ogz) && Math.Abs(gx - ogx) < 1 && Math.Abs(gz - ogz) < 1) continue;
            double vx = o.Vel.X, vz = o.Vel.Z, v2 = vx * vx + vz * vz;
            if (v2 < 0.01 || vx * ux + vz * uz > -0.3 * Math.Sqrt(v2)) continue;         // (only one coming this way)
            double rx = o.Pos.X - Pos.X, rz = o.Pos.Z - Pos.Z;
            double f = rx * ux + rz * uz, l = rx * -uz + rz * ux, sum = Radius + o.Radius;
            if (f <= 0 || f > sum + ahead || Math.Abs(l) >= sum) continue;
            if (f < bestF || (f == bestF && o.Id < best!.Id)) { best = o; bestF = f; bestL = l; bestSum = sum; }
        }
        if (best == null) return null;
        // the pair's side: from the lower id's way and the other's place across it
        bool lo = Id < best.Id;
        double lx, lz;
        if (lo) { lx = ux; lz = uz; }
        else { double vl = Math.Sqrt(best.Vel.X * best.Vel.X + best.Vel.Z * best.Vel.Z); lx = best.Vel.X / vl; lz = best.Vel.Z / vl; }
        var (lp, hp) = lo ? (Pos, best.Pos) : (best.Pos, Pos);
        double L = (hp.X - lp.X) * -lz + (hp.Z - lp.Z) * lx;
        double s = L > 0 ? -1 : 1;                              // (the lower id's side, across its own way)
        if (!lo) s = -s;
        double sx = s * -lz, sz = s * lx;                         // (world: the lower one's side vector, turned for the other)
        // the stronger the nearer it stands across the way
        double need = Math.Clamp(1 - Math.Abs(bestL) / bestSum, 0, 1);
        double w = 0.5 + 1.0 * need;
        double ax = ux + sx * w, az = uz + sz * w;
        return (ax, az);
    }

    public double Steer(double dt, double speed, double arrive = 0.8)
    {
        if (Path.Count == 0) { Vel = V3.Zero; return 0; }
        // broken legs (MIRAGE's mud pit: FightingObj.SetBrokenLegs -> UpdateSpeed: maxspeed and defaultspeed 1)
        if (St.T.Count > 0 && St.Active("brokenLegs", World.Time)) speed = Math.Min(speed, GaitSpeed(1));
        // set up to shoot (the log cannon): it gets up before it moves (compose.js holdMove)
        if (Deploy != null && Deploy.State != "walk" && Comp != null && Comp.HoldMove(World.Time)) { Vel = V3.Zero; return 0; }
        double tx = Path[0].X, tz = Path[0].Z;
        double dx = tx - Pos.X, dz = tz - Pos.Z, d = Hypot(dx, dz);
        bool last = Path.Count == 1;
        // (a big unit counts a corner of its way as reached from its radius and a half - but only where it sees the next
        // leg's point from where it stands: else it turned early into the obstacle the corner goes round, slid along it,
        // was "stuck", got the same way again and turned early again - base mission 7's Black Widow at 543,436 stood
        // forty minutes "walking" to Taslow's place, and the mission waited for it)
        if (d < (last ? arrive : Path[0].Exact ? 1.2 : Math.Max(1.5, Radius * 1.5))
            && (last || Path[0].Exact || d < 1.5 || World.NavFor(this).Los(Pos.X, Pos.Z, Path[1].X, Path[1].Z, 1)))
        {
            Path.RemoveAt(0);
            if (Path.Count == 0) { Vel = V3.Zero; return 0; }
            tx = Path[0].X; tz = Path[0].Z; dx = tx - Pos.X; dz = tz - Pos.Z; d = Hypot(dx, dz);
        }
        double want = Geo.HeadingTo(dx, dz);
        if (!Flying && d > 0.01 && OnWall == null && WJump == null && !Climbing && Avoid(dx / d, dz / d, speed) is var (ax0, az0)) want = Geo.HeadingTo(ax0, az0);
        double diff = Geo.Wrap(want - Heading);
        double turn = (Cls == "CHTR" ? 9 : Radius > 3 ? 1.6 : 3.2) * dt;
        Heading = Geo.Wrap(Heading + Math.Sign(diff) * Math.Min(Math.Abs(diff), turn));
        double sp = speed * Math.Max(0.2, SMath.Cos(Math.Min(Math.Abs(diff), Math.PI / 2)));
        if (last) sp = Math.Min(sp, d * 2 + 0.6);
        // local avoidance: if the step would enter a blocked cell, slide along the obstacle
        var nav = World.NavFor(this);
        double hx = -SMath.Sin(Heading), hz = -SMath.Cos(Heading);
        if (Flying) { Vel = new V3(hx * sp, 0, hz * sp); Pos.X += Vel.X * dt; Pos.Z += Vel.Z * dt; return sp; }
        double look = Math.Max(0.8, Math.Min(2.5, Radius));     // big units too: a look as far as their radius found no way through the narrow passes
        if (!nav.IsFree(Pos.X + hx * look, Pos.Z + hz * look))
        {
            foreach (var da in new[] { 0.5, -0.5, 1.0, -1.0, 1.5, -1.5 })
            {
                double a = Heading + da, ax = -SMath.Sin(a), az = -SMath.Cos(a);
                if (nav.IsFree(Pos.X + ax * look, Pos.Z + az * look)) { hx = ax; hz = az; break; }
            }
        }
        Vel = new V3(hx * sp, 0, hz * sp);
        // hard collision with buildings / walls / blocked terrain: never step from a free cell into a blocked one;
        // slide along the obstacle on one axis instead (units already inside a blocked cell may walk out)
        double nx = Pos.X + Vel.X * dt, nz = Pos.Z + Vel.Z * dt;
        if (CanWalkInto(nav, nx, nz)) { Pos.X = nx; Pos.Z = nz; }
        else if (CanWalkInto(nav, nx, Pos.Z)) { Pos.X = nx; Vel.Z = 0; }
        else if (CanWalkInto(nav, Pos.X, nz)) { Pos.Z = nz; Vel.X = 0; }
        else Vel = V3.Zero;
        StuckT += dt;
        if (StuckT > 1.5)
        {
            // the last stretch is blocked by units standing nearer the goal (a group sent to one point): this one is there
            if (last && V3.Dist(Pos, StuckFrom) < 0.6 && QueuedBehind(tx, tz, d)) { Path.Clear(); Vel = V3.Zero; StuckT = 0; return 0; }
            if (V3.Dist(Pos, StuckFrom) < 0.6 && Goal != null)
            {
                StuckN++;
                // repeatedly stuck: step to the nearest free cell first, then path again
                if (StuckN >= 2)
                {
                    int c = nav.NearestFree(nav.Idx(Pos.X, Pos.Z), 6); var (fx, fz) = nav.Center(c);
                    if (nav.Block[nav.Idx(Pos.X, Pos.Z)] != 0) { Pos.X = fx; Pos.Z = fz; }
                    StuckN = 0;
                }
                var g = Goal.Value; SetPath(g.X, g.Z);
            }
            else StuckN = 0;
            StuckT = 0; StuckFrom = Pos;
        }
        return sp;
    }

    /// <summary>may this unit move to (x, z)? Blocked cells are off limits unless the unit already stands in one (then any
    /// move is allowed so it can get out). Gates are checked per owner (nav.GateBlocks); an enemy standing in a gate's
    /// cell when it was placed or shut may walk out as well.</summary>
    public bool CanWalkInto(NavGrid nav, double x, double z)
    {
        int k = nav.Idx(x, z), cur = nav.Idx(Pos.X, Pos.Z);
        if (k == cur || nav.Block[cur] != 0) return true;
        if (nav.GateAt.Count > 0 && nav.GateBlocks(cur, Owner)) return true;
        if (nav.Block[k] != 0) return false;
        return !(nav.GateAt.Count > 0 && nav.GateBlocks(k, Owner));
    }

    /// <summary>the speed of a gait (1 .. 4: the model's walk_&lt;n&gt;)</summary>
    public double GaitSpeed(int i) => Or(Sp(Gaits, "walk_" + i), Or(i < Rules.Rules.SPEED_FALLBACK.Length ? Rules.Rules.SPEED_FALLBACK[i] : 0, Speed));
    /// <summary>the speed of a scripted walk at gait n (the triggers' WalkAction / waypoints "| n": the original's
    /// Action /Walk /Speed=n, GoTo at that gait - FightingObj HandleWalkCommand), at most the unit's maxspeed gait. The
    /// mission scripts are timed for it: MIRAGE / base mission 2's transport turtles sail their legs at gait 1 (about
    /// 1.5 m/s), and every next waypoint comes when the last one is reached at that pace.</summary>
    public double ScriptSpeed(int gait)
    {
        int max = (int)Math.Max(1, Math.Round(Or(Stats?.MaxSpeed ?? 0, Or(Stats?.Speed ?? 0, 3))));
        return GaitSpeed(Math.Clamp(gait, 1, max));
    }

    public double Face(double x, double z, double dt, double rate = 6)
    {
        double want = Geo.HeadingTo(x - Pos.X, z - Pos.Z);
        double diff = Geo.Wrap(want - Heading);
        Heading = Geo.Wrap(Heading + Math.Sign(diff) * Math.Min(Math.Abs(diff), rate * dt));
        return Math.Abs(diff);
    }

    /// <summary>the walk animation at speed sp (run: the maxspeed gait's; gait &gt; 0: that gait's, a scripted walk)</summary>
    public void MoveAnim(double sp, bool run, int gait = 0)
    {
        if (sp > 0.15)
        {
            BusyAnim = false;            // (walking cuts an action short - its onDone, which ended busyAnim, is gone)
            var a = run ? RunAnim : WalkAnim;
            double asp = run ? RunAnimSpeed : AnimSpeed;
            if (gait > 0 && Anim!.Pick("walk_" + gait) is { } ga && Sp(Gaits, "walk_" + gait) is > 0 and var gs) { a = ga; asp = gs; }
            var ws = WalkSet();
            if (ws != "") { int i = (int)Math.Min(3, Math.Max(1, Stats!.Speed != 0 ? Stats.Speed : 2)); a = Anim!.Pick(ws + "_walk_" + i, ws + "_walk_2", ws + "_walk_1") ?? a; }
            // another gait of the walk set it is walking in: the engine's transition clips first (walkanimconfig.txt:
            // accel_1_2 ... then the new gait's loop part - WalkGaits); only the clip changes, not the speed
            var A = Anim!;
            string[]? via = a != null && A.CurName != null && A.CurName != a && (A.Phase == "l" || A.Phase == "s" || A.Phase == "t") ? WalkGaits.Transitions(A, A.CurName, a) : null;
            A.Play(a, new AnimOpts { Ts = Math.Max(0.4, Math.Min(1.8, sp / asp)), Cut = true, Via = via });   // moving off: no end part of the last action first
        }
        else if (!BusyAnim) Anim!.Play(StandAnim());
    }
}

/// <summary>options of World.SpawnUnit / new Unit: swim, illusion, autonomous, stationary, temp, alt</summary>
public sealed class UnitOpts
{
    public static readonly UnitOpts None = new();
    public bool Swim, Illusion, Autonomous, Stationary, Temp;
    public double? Alt;
    public UnitOpts With(bool? temp = null) { var o = (UnitOpts)MemberwiseClone(); if (temp != null) o.Temp = temp.Value; return o; }
}

/// <summary>a way shared by a group searching in the same moment (Unit.SetPath)</summary>
public sealed class PathShare
{
    public double T, Sx, Sz, Tx, Tz;
    public NavGrid Nav = null!;
    public int Ver, Need;
    public Player? Owner;
    public bool Found;
    public NavPath Path = null!;
}

public static class Geo
{
    const double TAU = Math.PI * 2;
    public static double Wrap(double a) { a = (a + Math.PI) % TAU; if (a < 0) a += TAU; return a - Math.PI; }
    /// <summary>the heading towards (dx, dz): atan2(-dx, -dz) (0 = facing -z)</summary>
    public static double HeadingTo(double dx, double dz) => SMath.Atan2(-dx, -dz);
    /// <summary>maps/frame.js mapHeading: the heading of an object the map turns by rot</summary>
    public static double MapHeading(double rot) => Math.PI - rot;
}

// ======================================================================= buildings
public sealed partial class Building : Entity
{
    static readonly string[] CRANE_LINKS = { "Cr_1", "Cr_2", "Cr_3", "Cr_4" };
    /// <summary>a flat plot's selection circle (the view's ring and bar; 0: the footprint's, Radius)</summary>
    public double PickR;
    /// <summary>the effect a building of a script class shows once finished (Building.usl SetReady -> AddFX)
    /// (by the class or a class it inherits from: MIRAGE's Amazon temple is a CTemple; MIRAGE's big horn: its will-o'-the-wisp,
    /// CHuBigHorn.SetReady AddFX("fx_will_o_wisp_sml", -1))</summary>
    public static readonly Dictionary<string, string> READY_FX = new() { ["CTemple"] = "fx_heal_area", ["CSeasFortress"] = "fx_heal_area", ["CHuBigHorn"] = "fx_will_o_wisp_sml" };
    /// <summary>the ready effect of a class: its own script's, else an ancestor's (READY_FX)</summary>
    public static string? ReadyFxOf(Rules.Rules D, string name)
    {
        var sc = D.Script(name);
        if (sc != null && READY_FX.TryGetValue(sc, out var fx)) return fx;
        foreach (var (k, v) in READY_FX) if (D.IsA(name, k)) return v;
        return null;
    }
    /// <summary>a lone wall post: the hub box (the arms come from the WallMap)</summary>
    public static readonly double[][] WALL_BOXES = { new[] { 0.0, 0, 1.9, 1.9 } };
    /// <summary>Wall pieces never turn: they stand as the map's rotation 0 (maps/frame.js mapHeading(0))</summary>
    public static readonly double WALL_ROT = Geo.MapHeading(0);

    public double Rot;
    public double Progress;
    public bool Built;
    public readonly OrderedSet<Unit> Builders = new();
    /// <summary>farm workers</summary>
    public readonly OrderedSet<Unit> Workers = new();
    public double Cool;
    public bool CountsInPop;
    public IBuildingBehaviour? Behaviour;
    public ModelInfo? Tpl;
    public string Fourcc = "";
    public int ArmMask, PfMask;
    /// <summary>wall pieces: the model parts the WallMap named (hub and one piece per direction)</summary>
    public List<string>? WallParts;
    /// <summary>footprint boxes [cx, cz, hx, hz] in world space</summary>
    public double[][] Boxes = Array.Empty<double[]>();
    public double Hx, Hz, Ht, Fx, Fz;
    /// <summary>the model's origin (the footprint centre is Pos)</summary>
    public V3 Origin;
    /// <summary>footprint centre relative to the model origin, in the model's own frame</summary>
    public double[] FLocal = { 0, 0 };
    public List<int> Cells = new();
    /// <summary>a gate whose model marks its door (GatePf): the cells of the rest (arms, towers, pillars) - they block
    /// like any building's, open or shut; only the door's cells are the pathfinder's gate cells. null: the whole gate is
    /// the door (no door in the model's table)</summary>
    public List<int>? SolidCells;
    public bool Blocking;
    bool? _walkOver;
    public int Age;
    public int DmgStage;
    public List<(string Name, double X, double Z)> Slots = new();
    public readonly OrderedMap<string, Unit> SlotUse = new();
    public double WorkT = -99, DeliverT;
    /// <summary>the last product of the queue is done: the work loop ends now (Production.WorkDone)</summary>
    public bool WorkEnd;
    public int DeliverN;
    public (int I, int J)? Tile;
    /// <summary>gates: 0 open, 1 closed, 2 auto (systems/buildings.js)</summary>
    public int GateState = 2;
    public double BrokenT;
    public double NightT;
    // the look
    /// <summary>construction level 0..3 (4 = finished), damage stage 0..2, epoch (applyState)</summary>
    public int VisLevel = 4, VisDmg, VisAge = 1;
    public bool Night;
    public bool GateEnds;
    bool _gateEndsSet;
    /// <summary>the effect model shown once finished (READY_FX)</summary>
    public string? ReadyFx;
    public bool FxShown;
    /// <summary>attached parts: turrets, the harbour crane, the turtles under the Aje floating harbour (compose.js table)</summary>
    public List<AttachedPart> Parts = new();
    public AttachedPart? Turret;
    string? _partsKey;
    /// <summary>construction cranes at Cr_1..Cr_4: model and animation</summary>
    public List<AttachedPart> Cranes = new();
    /// <summary>the ruin left after destruction (its model; construction.js), sinking later</summary>
    public string? Ruin;
    /// <summary>the ruins of its attached parts (a tower's turret, the rocket ramp's top and bird, the big cannon's
    /// rotator: CTower.CreateBuildingCorpse - "&lt;part&gt;_dest" playing "destroy"), drawn instead of the live parts once
    /// it is destroyed; null: none (World.RuinPartsOf)</summary>
    public List<AttachedPart>? RuinParts;
    /// <summary>the ruin stays for good (Building.usl CBuildingCorpse.ShouldStay: the SEAS headquarters' bunker, big cannon,
    /// defense turret and machine-gun nest, the city gates' ruins); World.PermanentRuin</summary>
    public bool RuinStays;
    /// <summary>a permanent ruin's part ruins were settled (World.SettleRuinParts)</summary>
    public bool RuinPartsSettled;
    public double SinkY;
    string _visKey = "";
    (int L, int Dmg, int Age, int Arm, bool Gate, bool Night, List<string>? Wp, string WpKey)? _vs;

    Func<double, double, double> _baseY;

    public Building(World world, string name, Player? owner, double x, double z, double rot = 0, bool built = true)
        : base(world, "building", owner, x, z)
    {
        var D = world.Data;
        Name = name;
        Def = D.Def(name, owner)!;
        Cls = "BLDG";
        Level = 1;
        // (MIRAGE's launch pads count from their site on: DoesCountInUnitLimit - MirageBuildings.cs)
        CountsInPop = D.IsA(name, "CSwimmingHarbour") || (D.Script(name) is string scr && MiragePads.COUNT_AS_UNIT.Contains(scr));
        Stats = D.Stats(name, 1, owner)!;
        MaxHp = Stats.Hp;
        Behaviour = world.BehaviourOf(this);
        if (Def.WallKind == "wall") rot = WALL_ROT;        // wall pieces never turn (their arms follow the wall grid)
        Rot = rot;
        Progress = built ? 1 : 0;
        Built = built;
        Hp = built ? MaxHp : 1;
        Weapons = D.WeaponSetOf(name, 1, owner);
        Weapon = Weapons.Long;
        Fow = Stats.Fow != 0 ? Stats.Fow : 40;
        Gfx = Stats.Gfx;
        TtVersion = owner?.Tt?.Version ?? 0;
        var M = world.Models;
        Tpl = M.Get(Gfx);
        Fourcc = M.Fourcc(Gfx);
        // wall pieces: hub + the arms towards connected neighbours (wallmap.js); they start as a lone post
        var F = Footprint.Place(M, Gfx, x, z, rot, 0, Def.WallKind == "wall" ? WALL_BOXES : null);
        Boxes = F.Boxes;
        Hx = Math.Max(2, F.Hx); Hz = Math.Max(2, F.Hz);
        var bb = M.Box(Gfx);
        Ht = bb is var (mn, mx) ? Math.Max(3, Math.Min(mx.Y - mn.Y, 30)) : 3;
        Fx = F.Fx; Fz = F.Fz;
        Radius = Math.Max(Hx, Hz) * 0.85;
        SelReach = M.SelReach(Gfx) ?? 0;
        // a flat plot (a field: hu_corn_field, ninigi_paddy - its pick volume at most 6 m high, wider than its pathfinder box):
        // its selection circle goes round the whole plot (the farthest corner of the pick volume), not the footprint
        if (M.File(Gfx) is { PickMin: V3 pn, PickMax: V3 px } && px.Y - pn.Y <= 6)
            PickR = Math.Max(Math.Max(Math.Sqrt(pn.X * pn.X + pn.Z * pn.Z), Math.Sqrt(px.X * px.X + px.Z * px.Z)), Math.Max(Math.Sqrt(pn.X * pn.X + px.Z * px.Z), Math.Sqrt(px.X * px.X + pn.Z * pn.Z)));
        // harbours: the land part stands at sea level + 1.5 (ServerApp.usl:1158)
        _baseY = (gx, gz) => Def!.Coastal && world.WaterLevel != null ? Math.Max(world.Height(gx, gz), world.WaterLevel.Value + 1.5) : world.Height(gx, gz);
        // the wall-map objects (wall pieces, gates, towers) stand at the tile's height quantised to 2 m (wallmap.js zOf);
        // traps on the ground
        var wk = Def.WallKind;
        if (world.WallMap != null && (wk == "wall" || wk == "gate" || wk == "tower")) { var WM = world.WallMap; _baseY = (gx, gz) => { var (i, j) = WM.TileOf(gx, gz); return WM.ZOf(i, j); }; }
        Pos.Y = _baseY(x, z);
        var clips = M.Clips(Gfx);
        Anim = clips.Count > 0 ? new AnimCtl(clips, this) : null;
        if (Anim != null && Tpl != null && Tpl.HasSounds) Anim.SetEvents(Tpl.Sounds);
        // nav cells of the footprint boxes; the pathfinder blocks them once construction level 1 is reached (BuildUpBuilding.usl)
        Cells = CellsOf(Boxes, rot, Def.Gate);
        SolidCells = Def.Gate ? GateSolidCells(Boxes, rot) : null;
        Origin = Pos;
        // footprint centre relative to the model origin, in the model's own frame (moving harbours keep it while sailing)
        { double c = SMath.Cos(rot), s = SMath.Sin(rot), ox = Fx - x, oz = Fz - z; FLocal = new[] { ox * c - oz * s, ox * s + oz * c }; }
        Pos = new V3(Fx, _baseY(Fx, Fz), Fz);
        world.Reserve(Cells, 1);
        Blocking = false;
        if (built) SetBlocking(true);
        Age = owner != null ? owner.Epoch() : 1;
        DmgStage = 0;
        if (!built) AddCranes();
        Slots = LinkSlots("Bl_");
        UpdateTurret();
        WorkT = -99;
        UpdateVisual();
        IdleAnim();
        Behaviour?.Init(this, world);
    }

    public double BaseY(double gx, double gz) => _baseY(gx, gz);

    /// <summary>cover: every cell the boxes touch (gates: a closed gate must close its passage, Nav.RectCells)</summary>
    List<int> CellsOf(double[][] boxes, double rot, bool cover = false)
    {
        var set = new OrderedSet<int>();
        foreach (var b in boxes) foreach (var k in World.Nav.RectCells(b[0], b[1], b[2], b[3], rot, cover)) set.Add(k);
        return set.ToList();
    }

    /// <summary>
    /// a gate's cells outside its door (SolidCells): the model's pathfinder table (GSF pf, the boxes FootBoxes keeps, in
    /// its order) marks the door box with attr bits 0x18000 without 0x4000 (hcl13_gate: the door 0x1801f between its
    /// wall arms 0x1f, posts 0x401f; seas_gate / hu_small_wall_gate: the door 0x...1801f, the towers 0x...1c01f, the wall
    /// joints 0x...9f / 0x...81f). The arms of a 67 m city gate are walls: open or shut, nobody walks through them.
    /// null: no door marked (the whole gate opens, as before).
    /// </summary>
    List<int>? GateSolidCells(double[][] boxes, double rot)
    {
        var pf = World.Models.Get(Gfx)?.Pf;
        if (pf == null) return null;
        var rows = pf.Where(r => r.Length >= 6 && r[0] == 1 && r[4] > 0.5 && r[5] > 0.5).ToList();
        if (rows.Count != boxes.Length) return null;
        static bool Door(double[] r) => r.Length >= 8 && ((long)r[7] & 0x1C000) == 0x18000;
        if (!rows.Any(Door) || rows.All(Door)) return null;
        var door = new HashSet<int>(CellsOf(boxes.Where((b, i) => Door(rows[i])).ToArray(), rot, true));
        return CellsOf(boxes.Where((b, i) => !Door(rows[i])).ToArray(), rot).Where(k => !door.Contains(k)).ToList();
    }

    /// <summary>
    /// attached parts (composites table, Compose.cs): tower turrets (CTower.SetTurret: seas_turret at "we", the rocket
    /// ramp's top + bird, the Hu ballista after hu_ballista_upgrade ...), the Hu harbour crane, the turtles under the
    /// Aje floating harbour. Construction cranes are AddCranes(). Rebuilt when the wanted set changes.
    /// </summary>
    public void UpdateTurret()
    {
        var W = World;
        // (MIRAGE's SEAS towers' turrets once finished, its decorations: World.PartSpecs, MirageSeasBuildings.cs)
        var specs = W.PartSpecs(this, W.Data.Composites(Name).Where(s => s.Cond != "construction").ToList());
        var act = specs.Count > 0 && Owner != null ? Compose.ActiveParts(W, this, specs) : new List<CompositeSpec>();
        var extra = (Behaviour as BuildingBehaviour)?.ExtraParts(this, W);
        var key = string.Join(",", act.Select(s => s.I + ":" + Compose.PartGfx(W, s, this))) + "@" + (Gfx ?? "")
            + (extra != null && extra.Count > 0 ? "+" + string.Join(",", extra.Select(x => x.Key)) : "");
        if (key == _partsKey) return;
        _partsKey = key;
        foreach (var p in Parts) p.Anim?.Stop();
        Parts = new List<AttachedPart>(); Turret = null;
        var made = new Dictionary<int, AttachedPart>();
        foreach (var s in act)
        {
            var parent = s.Pi >= 0 ? made.GetValueOrDefault(s.Pi) : null;
            if (s.Pi >= 0 && parent == null) continue;
            var p = s.Kind == "deco" ? World.AttachDeco(W, this, s) : Compose.Attach(W, this, parent, parent != null ? parent.Gfx : Gfx, s.Link, Compose.PartGfx(W, s, this));
            if (p == null) continue;
            p.Spec = s;
            made[s.I] = p;
            Parts.Add(p);
            if (s.Kind == "turret" && s.Pi < 0 && Turret == null) { Turret = p; p.AttackAnim = s.Anim; }
            else if (p.Anim != null && s.Kind != "turret") { var a = p.Anim.Pick(s.Anim, "swim_1", "standanim", "idle"); if (a != null) p.Anim.Play(a); }
        }
        // the script's own decorations (CMirageDeco: BuildingBehaviour.ExtraParts - the sensor tower's guard, the special
        // box's animals, the launch pads' build-ups): "deco" parts as the composites' MIRAGE decorations (World.PartSpecs,
        // World.AttachDeco), whole (construction level 4) and with a state of their own (destruction level, visibility
        // flags: AttachedPart.OwnState)
        if (extra != null)
            foreach (var x in extra)
            {
                var spec = new CompositeSpec { I = -1, Kind = "deco", Gfx = x.Gfx.ToLowerInvariant(), Link = x.Link, Cond = "always", Whole = true };
                if (x.Offset is { Length: 3 } o)
                {
                    spec.Offset = new System.Text.Json.Nodes.JsonArray(o[0], o[1], o[2]);
                    spec.OffsetModel = new V3(-o[0], o[2], -o[1]);
                }
                var p = World.AttachDeco(W, this, spec);
                if (p == null) continue;
                p.Spec = spec; p.OwnState = true; p.VisLevel = 4; p.VisDmg = x.Dmg; p.VisFlags = x.Flags;
                if (p.Anim != null) { var a = p.Anim.Pick(x.Clip, "standanim", "idle"); if (a != null) p.Anim.Play(a); }
                Parts.Add(p);
            }
        LookChanged();
    }

    /// <summary>tech tree changed: new stats (hit points keep their ratio), possibly a new model (big tent, farm modes, walls)</summary>
    public void RefreshRules()
    {
        UpdateTurret();
        var D = World.Data; var O = RulesOwner();
        ResetRulesOwner();
        double ratio = Hp / MaxHp;
        Def = D.Def(Name, O)!;
        Stats = D.Stats(Name, 1, O)!;
        MaxHp = Stats.Hp;
        Hp = Math.Max(1, Math.Min(MaxHp, ratio * MaxHp));
        Weapons = D.WeaponSetOf(Name, 1, O);
        Weapon = Weapons.Long;
        Fow = Stats.Fow != 0 ? Stats.Fow : 40;
        TtVersion = O?.Tt?.Version ?? 0;
        // a wall piece or gate whose model is now another wall class of its race and type becomes that class
        // (CWall / CGate.UpdateGfx -> WallClassChanged: the filters name the class as the gfx)
        if ((Def.WallKind == "wall" || Def.WallKind == "gate") && Stats.Gfx != null && Stats.Gfx != Name && World.Walls is { } WM
            && WM.ClassOf(Stats.Gfx) is { } nc && WM.ClassOf(Name) is { } oc && nc[0] == oc[0] && nc[2] == oc[2])
        {
            World.WallClassChanged(this, Stats.Gfx.ToLowerInvariant());
            return;
        }
        if (Stats.Gfx != null && Stats.Gfx != Gfx) SwapModel(Stats.Gfx);
    }

    public void SwapModel(string gfx)
    {
        var M = World.Models;
        if (!M.Has(gfx)) return;
        Gfx = gfx;
        Tpl = M.Get(gfx);
        Fourcc = M.Fourcc(gfx);
        SelReach = M.SelReach(gfx) ?? SelReach;
        RemoveCranes();
        Anim?.Stop();
        var clips = M.Clips(gfx);
        Anim = clips.Count > 0 ? new AnimCtl(clips, this) : null;
        _visKey = "";
        UpdateVisual();
        IdleAnim();
        Slots = LinkSlots("Bl_");
        _partsKey = null;
        // a wall piece of a new class (WallClassChanged: the tile's parts and pathfinder boxes again, from the new model)
        if (Def!.WallKind == "wall" && Tile is var (ti, tj) && World.WallMap != null) { Relocate(); World.WallMap.Update(ti, tj); }
        UpdateTurret();
        LookChanged();
    }

    public bool IsDropoff => Built && Def!.Delivery.Count > 0;

    public void SetBlocking(bool on)
    {
        // gates never block; the pathfinder asks them (nav.GateBlocks) - but the parts beside a marked door do (SolidCells)
        if (Def!.Gate)
        {
            if (SolidCells == null || on == Blocking) return;
            Blocking = on;
            World.Nav.Mark(SolidCells, on ? 1 : -1);
            return;
        }
        // traps, fields, the Aje slaughterhouses and MIRAGE's punching bag are walked over (their SetPFBlocking is empty:
        // Building.usl CTrap, CGrowingField, CAjeUnlimitedBuilding, CHuPunchingBag)
        if (on && _walkOver == null) { var D = World.Data; _walkOver = new[] { "CTrap", "CGrowingField", "CAjeUnlimitedBuilding", "CHuPunchingBag" }.Any(c => D.IsA(Name, c)); }
        if (on && _walkOver == true) return;
        if (on == Blocking) return;
        Blocking = on;
        World.Nav.Mark(Cells, on ? 1 : -1);
        World.WaterNav?.Mark(Cells, on ? 1 : -1);   // harbours: ships sail around them
    }

    public bool FindLink(string name) => World.Models.File(Gfx) is { } f ? f.Links.ContainsKey(name) : World.Models.Has(Gfx);

    /// <summary>a link's world position (null: the model has no such link)</summary>
    public V3? LinkWorld(string name)
    {
        var p = World.Models.Link(Gfx, name);
        if (p is not V3 l) return null;
        double c = SMath.Cos(Rot), s = SMath.Sin(Rot);
        return new V3(Origin.X + l.X * c + l.Z * s, Origin.Y + l.Y, Origin.Z - l.X * s + l.Z * c);
    }

    /// <summary>builder positions (links Bl_0..Bl_9)</summary>
    public List<(string Name, double X, double Z)> LinkSlots(string prefix)
    {
        var res = new List<(string, double, double)>();
        for (int i = 0; i < 10; i++) if (LinkWorld(prefix + i) is V3 p) res.Add((prefix + i, p.X, p.Z));
        return res;
    }

    /// <summary>nearest free builder link (CBuilding.OccupyLink)</summary>
    public (string Name, double X, double Z)? OccupySlot(Unit u)
    {
        (string Name, double X, double Z)? best = null; double bd = 1e9;
        var nav = World.Nav;
        foreach (var s in Slots)
        {
            var o = SlotUse.Get(s.Name);
            if (o != null && o != u && o.Alive && o.Task.Building == this) continue;
            if (!nav.IsFree(s.X, s.Z) && Blocking && nav.Block[nav.Idx(s.X, s.Z)] > 1) continue;
            double d = Math.Sqrt((s.X - u.Pos.X) * (s.X - u.Pos.X) + (s.Z - u.Pos.Z) * (s.Z - u.Pos.Z));
            if (d < bd) { bd = d; best = s; }
        }
        if (best != null) SlotUse.Set(best.Value.Name, u);
        return best;
    }

    public void FreeSlot(Unit u) { foreach (var (k, v) in SlotUse) if (v == u) SlotUse.Delete(k); }

    /// <summary>distance from a point to the footprint surface</summary>
    public double SurfDist(double x, double z)
    {
        double best = 1e9;
        double c = SMath.Cos(Rot), s = SMath.Sin(Rot);
        foreach (var b in Boxes)
        {
            double dx = x - b[0], dz = z - b[1];
            double lx = Math.Abs(dx * c - dz * s) - b[2], lz = Math.Abs(dx * s + dz * c) - b[3];
            double d = Math.Sqrt(Math.Max(0, lx) * Math.Max(0, lx) + Math.Max(0, lz) * Math.Max(0, lz)) + Math.Min(0, Math.Max(lx, lz));
            if (d < best) best = d;
        }
        return best;
    }

    public bool Contains(double x, double z, double m = 0) => SurfDist(x, z) < m;

    public void AddCranes()
    {
        var tribe = Owner != null ? Owner.Tribe.ToLowerInvariant() : "seas";
        for (int i = 0; i < CRANE_LINKS.Length; i++)
        {
            var ln = CRANE_LINKS[i];
            if (!FindLink(ln)) continue;
            var gfx = $"{tribe}_crane_0{(i < 2 ? 1 : 2)}";
            if (!World.Models.Has(gfx)) continue;
            var clips = World.Models.Clips(gfx);
            Cranes.Add(new AttachedPart { Gfx = gfx, Link = ln, Anim = clips.Count > 0 ? new AnimCtl(clips, this) : null, Fourcc = World.Models.Fourcc(gfx) });
        }
        AddCustomCranes();                                 // (MIRAGE custom_buildups.txt: CustomBuildUps.cs)
        LookChanged();
    }

    public void RemoveCranes() { foreach (var c in Cranes) c.Anim?.Stop(); Cranes = new List<AttachedPart>(); LookChanged(); }

    public int ConstructLevel() => Built ? 4 : Math.Min(3, (int)Math.Floor(Progress * 4 + 1e-6));
    /// <summary>FightingObj.UpdateDestructionFlags: &lt;=50% hit points stage 1, &lt;=25% stage 2 (finished buildings only)</summary>
    public int DestructLevel() { if (!Built) return 0; double r = Hp / MaxHp; return r <= 0.25 ? 2 : r <= 0.5 ? 1 : 0; }

    /// <summary>the look of the model: construction level, damage stage, epoch, wall arms / parts, gate ends, night</summary>
    public void UpdateVisual(bool force = false)
    {
        int L = ConstructLevel(), dmg = DestructLevel();
        if (Owner != null) Age = Owner.Epoch();
        bool night = Night;          // its lights (World.NightCheck)
        // (called every frame: the state is compared field by field, the key string only made when it changed)
        // (WallParts is only ever replaced - SetParts - never changed in place: the reference says whether it changed)
        var wp = WallParts;
        if (_visKey != "" && _vs is var (sL, sD, sA, sArm, sG, sN, sWp, _) && sL == L && sD == dmg && sA == Age && sArm == ArmMask && sG == GateEnds && sN == night && sWp == wp) return;
        string wpKey = wp != null ? string.Join(".", wp) : "";
        _vs = (L, dmg, Age, ArmMask, GateEnds, night, wp, wpKey);
        var key = $"{L}|{dmg}|{Age}|{ArmMask}|{wpKey}|{(GateEnds ? 1 : 0)}|{night}";
        if (key == _visKey) return;
        _visKey = key;
        DmgStage = dmg;
        // (the JS applyState on the model and the parts that are no characters / animals)
        VisLevel = L; VisDmg = dmg; VisAge = Age;
        // (a whole part - MIRAGE's SEAS turrets and decorations: construction level 4 - follows the building's damage; a
        // part with its own state - a script's decoration, ExtraParts - keeps it)
        foreach (var p in Parts) if (p.Fourcc != "Char" && p.Fourcc != "Anim" && !p.OwnState) { p.Night = night; p.VisLevel = p.Spec?.Whole == true ? 4 : L; p.VisDmg = dmg; }
        if (Built && Cranes.Count > 0) RemoveCranes();
        if (Built) { UpdateTurret(); ReadyFxShow(); }          // "when ready" parts and effects
        LookChanged();
    }

    /// <summary>the wall pieces' default parts when the WallMap named none (hub and the 8 arms)</summary>
    public static readonly string[] DefaultWallParts = { "x2", "0f", "1f", "2f", "3f", "4f", "5f", "6f", "7f" };

    /// <summary>the effect a finished building shows for good (Building.usl SetReady): the temples' healing glow</summary>
    void ReadyFxShow()
    {
        if (FxShown) return;
        FxShown = true;
        if (ReadyFxOf(World.Data, Name) is string name && World.Models.Has(name)) ReadyFx = name;
    }

    /// <summary>
    /// The building's idle clip (CBuilding.SetIdleAnim, Building.usl:751 / MIRAGE 1065): "standanim" looping, else (MIRAGE)
    /// "idle" looping, else "work" and else "deliver": the clip running already ends its loop (EndCurrentLoopAnim: its end
    /// part, or to its end) and stays on its last frame, another one plays it once (SetAnim(.., 1)). Without any of them
    /// the clip stops where it is. (The JS paused a work loop on its frame; a building with "work" but no stand clip now
    /// plays it once when it is idle, as the original does.)
    /// </summary>
    public void IdleAnim()
    {
        var a = Anim;
        if (a == null) return;
        if (a.Has("standanim")) { a.Play("standanim"); return; }
        if (a.Has("idle")) { a.Play("idle"); return; }
        var w = a.Pick("work", "work_1") ?? (a.Has("deliver") ? "deliver" : null);
        if (w != null)
        {
            if (a.CurName == w) a.StopLoop();
            else a.Play(w, new AnimOpts { Loop = false, Restart = true });
            return;
        }
        if (a.Cur != null) { a.Cur.Paused = true; LookChanged(); }
    }

    public void OnWork(double t)
    {
        var a = Anim;
        WorkT = t; WorkEnd = false;
        CraneWork(true);
        if (a == null) return;
        var w = a.Pick("work", "work_1");
        if (w == null) return;
        // (StartWorkAnim / OnWork: SetAnim(work, 3) unless it loops already - also after it was played once or ended)
        bool looping = a.CurName == w && a.Cur != null && (a.Phase == "s" || (a.Cur.Loop && a.Phase != "e"));
        if (!looping) a.Play(w, new AnimOpts { Fade = 0.3, Restart = a.CurName == w });
        else if (a.Cur!.Paused) { a.Cur.Paused = false; LookChanged(); }
    }

    /// <summary>the Hu harbour's crane (CHarbour.SetReady: hu_harbour_crane linked at Cr_3, B:5180-5190; a part of the
    /// composites table), null for other buildings</summary>
    AttachedPart? HarbourCrane => Parts.Count == 0 ? null : Parts.Find(p => p.Gfx == "hu_harbour_crane" && p.Anim != null);

    /// <summary>
    /// CHarbour.StartWorkAnim / StopWorkAnim (B:5300-5325, SetCraneAnim): while the harbour produces (Action.usl:981 at
    /// the start of every product - the ships) its crane loops "work" (SetAnim(work, 3), unless it runs already); when the
    /// product is done (Action.usl:1020) the loop ends (EndCurrentLoopAnim: the clip runs to its end and stays there).
    /// The harbour's own clip is OnWork's, as for every building.
    /// </summary>
    void CraneWork(bool on)
    {
        if (HarbourCrane is not { Anim: { } ca }) return;
        bool looping = ca.CurName == "work" && ca.Cur != null && (ca.Phase == "s" || (ca.Cur.Loop && ca.Phase != "e"));
        if (on && !looping && ca.Has("work")) ca.Play("work", new AnimOpts { Restart = ca.CurName == "work" });
        else if (!on && looping) ca.StopLoop();
    }

    /// <summary>the BuildDown item at the front of the queue (Aje): the building is being taken down (no fighting, no sailing)</summary>
    public QueueItem? Dismantling => Queue.Count > 0 && Queue[0].BuildDown ? Queue[0] : null;

    /// <summary>
    /// a worker delivered here (FightingObj.AcceptDeliveries / task CAcceptDeliveries): a storehouse with a "deliver"
    /// animation plays it - the lumberjack's cottage, the warehouse, the bazaar ... -, every delivery while it runs adds
    /// a loop (at most 5 more)
    /// </summary>
    public void OnDelivery()
    {
        var a = Anim;
        if (a == null || !Built || !a.Has("deliver")) return;
        var lp = a.LoopOf("deliver");
        double loop = lp != null ? lp.Value.T1 - lp.Value.T0 : a.Duration("deliver");
        if (a.CurName == "deliver" && DeliverT > 0) { if (DeliverN < 5) { DeliverN++; DeliverT += loop; } return; }
        DeliverN = 0;
        DeliverT = (lp != null ? lp.Value.T0 : 0) + loop;
        a.Play("deliver", new AnimOpts { Restart = true });
    }

    /// <summary>advances the animations of the building, its parts and cranes</summary>
    public void UpdateAnim(double dt, double t)
    {
        var a = Anim;
        if (a != null)
        {
            if (DeliverT > 0 && (DeliverT -= dt) <= 0 && a.CurName == "deliver") IdleAnim();
            if (WorkT > 0 && (t - WorkT > 6 || WorkEnd) && (a.CurName == "work" || a.CurName == "work_1"))
            {
                WorkT = -99; WorkEnd = false;
                if (a.Has("work_finished")) a.Play("work_finished", new AnimOpts { Loop = false, OnDone = new AnimDone("idle") });
                else IdleAnim();
            }
        }
        // the harbour's crane: the work over (the product done, or no work for 6 s) ends its loop
        if (WorkT <= 0 || t - WorkT > 6 || WorkEnd) CraneWork(false);
        // (the JS advanced off-screen buildings a few times a second only; the simulation every step)
        a?.Update(dt);
        foreach (var p in Parts) p.Anim?.Update(dt);
        foreach (var c in Cranes)
        {
            if (c.Anim == null) continue;
            bool busy = Builders.Any(w => w.Alive && w.Task.Building == this && w.Task.Phase == "work");
            if (busy) { if (c.Anim.CurName != "build") c.Anim.Play("build"); else if (c.Anim.Cur != null && c.Anim.Cur.Paused) { c.Anim.Cur.Paused = false; LookChanged(); } }
            else if (c.Anim.Cur != null && !c.Anim.Cur.Paused) { c.Anim.Cur.Paused = true; LookChanged(); }
            c.Anim.Update(dt);
        }
    }

    /// <summary>turn the turret towards (x, z); returns the remaining angle. TurretYaw: the turret's heading relative to the building (the JS rotation.z about the link, negated)</summary>
    public double AimTurret(double x, double z, double dt)
    {
        var t = Turret;
        if (t == null) return 0;
        // a turn by a about the link's z turns the turret by -a in the world (the model's root mirrors: game/maps/frame.js)
        double want = SMath.Atan2(-(x - Pos.X), -(z - Pos.Z)) - Rot;
        double diff = want - t.Yaw; diff = SMath.Atan2(SMath.Sin(diff), SMath.Cos(diff));
        t.Yaw += Math.Sign(diff) * Math.Min(Math.Abs(diff), 2.5 * dt);
        return Math.Abs(diff);
    }

    /// <summary>moving harbours: move the model origin by (dx, dz) and turn it to rot; pos (footprint centre) follows</summary>
    public void Slide(double dx, double dz, double rot)
    {
        Origin.X += dx; Origin.Z += dz; Rot = rot;
        double c = SMath.Cos(rot), s = SMath.Sin(rot), lx = FLocal[0], lz = FLocal[1];
        Pos = new V3(Origin.X + lx * c + lz * s, Pos.Y, Origin.Z - lx * s + lz * c);
        Fx = Pos.X; Fz = Pos.Z;
        LookChanged();
    }

    /// <summary>footprint boxes and nav cells for the current position (after sailing)</summary>
    public void Relocate()
    {
        var W = World;
        var F = Footprint.Place(W.Models, Gfx, Origin.X, Origin.Z, Rot, 0, Def!.WallKind == "wall" ? Footprint.WallPfBoxes(W.Models, Gfx, PfMask) : null);
        Boxes = F.Boxes;
        bool was = Blocking;
        if (was) SetBlocking(false);
        W.Reserve(Cells, -1);
        Cells = CellsOf(Boxes, Rot, Def.Gate);
        SolidCells = Def.Gate ? GateSolidCells(Boxes, Rot) : null;
        W.Reserve(Cells, 1);
        if (was) SetBlocking(true);
    }

    /// <summary>wall pieces (the WallMap's tile update): the model parts to show, the pathfinder mask, the arm bits</summary>
    public void SetParts(List<string> parts, int pfMask, int armMask)
    {
        if (Def!.WallKind != "wall") return;
        var k = string.Join(".", parts) + "|" + pfMask;
        if (k == _partsKey) return;
        _partsKey = k;
        WallParts = parts; ArmMask = armMask;
        if (pfMask != PfMask)
        {
            PfMask = pfMask; Relocate();
            // longer arms: units standing in them step out (as when a piece starts to block)
            if (Blocking) World.ClearFootprint(this, new List<Unit>());
        }
        UpdateVisual(true);
    }

    public void SetArms(int mask) { ArmMask = mask; }

    /// <summary>gates: the end pieces (render flags 7 and 11) only on a diagonal gate (Building.usl CGate, client 0x4f93a0)</summary>
    public void SetGateEnds(bool on)
    {
        if (_gateEndsSet && GateEnds == on) return;
        _gateEndsSet = true;
        GateEnds = on;
        UpdateVisual(true);
    }

    public void SpawnAnim() { if (Anim != null && Anim.Has("spawn")) Anim.Play("spawn", new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone("idle") }); }

    /// <summary>a free spot next to the building, towards (tx, tz)</summary>
    public (double X, double Z) ExitPoint(double tx, double tz, NavGrid? nav = null)
    {
        var W = World;
        nav ??= W.Nav;
        double a = SMath.Atan2(tz - Pos.Z, tx - Pos.X);
        for (double r = 2; r < 24; r += 1.5)
        {
            for (int k = 0; k < 16; k++)
            {
                double aa = a + (k % 2 != 0 ? 1 : -1) * Math.Ceiling(k / 2.0) * 0.3;
                double R = Radius + r;
                double x = Pos.X + SMath.Cos(aa) * R, z = Pos.Z + SMath.Sin(aa) * R;
                if (nav.IsFree(x, z) && SurfDist(x, z) > 1) return (x, z);
            }
        }
        if (nav != W.Nav) { int c = nav.NearestFree(nav.Idx(Pos.X, Pos.Z), 40); return nav.Center(c); }
        return (Pos.X + Radius + 2, Pos.Z);
    }

    /// <summary>where produced units appear (link Spwn), falling back to the exit point; ships appear at the harbour's
    /// dock (Do_1, naval.md §1.4) on the water grid</summary>
    public (double X, double Z) SpawnPoint(double tx, double tz, NavGrid? nav = null)
    {
        nav ??= World.Nav;
        foreach (var ln in nav == World.Nav ? new[] { "Spwn" } : new[] { "Do_1", "Spwn", "Ex_1" })
        {
            var p = LinkWorld(ln);
            if (p is V3 q && nav.IsFree(q.X, q.Z)) return (q.X, q.Z);
        }
        return ExitPoint(tx, tz, nav);
    }
}

/// <summary>Footprints of buildings and props from the GSF pathfinder table (manifest "pf").</summary>
public static class Footprint
{
    /// <summary>
    /// Footprint boxes from the GSF pathfinder table (type 1 = box: min corner + size in the model's Z-up space).
    /// Returns boxes as [cx, cz, hx, hz] in the building's local (Y-up) frame: GSF (x, y) -> (-x, -y), the models' root
    /// mirrors x (game/maps/frame.js).
    /// </summary>
    public static double[][] FootBoxes(ModelManifest M, string? gfx)
    {
        var info = M.Get(gfx);
        var pf = info?.Pf;
        var boxes = new List<double[]>();
        if (pf != null) foreach (var r in pf) if (r.Length >= 6 && r[0] == 1 && r[4] > 0.5 && r[5] > 0.5) boxes.Add(new[] { -(r[1] + r[4] / 2), -(r[2] + r[5] / 2), r[4] / 2, r[5] / 2 });
        if (boxes.Count == 0)
        {
            var foot = info?.Foot;
            if (foot != null && foot.Length >= 4) boxes.Add(new[] { foot[0], foot[1], foot[2] * 0.9, foot[3] * 0.9 });
            else if (M.Box(gfx) is var (mn, mx)) boxes.Add(new[] { (mx.X + mn.X) / 2, (mx.Z + mn.Z) / 2, (mx.X - mn.X) / 2 * 0.9, (mx.Z - mn.Z) / 2 * 0.9 });
            else boxes.Add(new[] { 0.0, 0, 0, 0 });       // (an empty three.js box: nothing to stand on)
        }
        return boxes.ToArray();
    }

    /// <summary>
    /// The pathfinder boxes of a wall piece for its PFMask (wallmap.js pfMask: the model's pf boxes whose direction / hub
    /// bit - attr bits 5..15 - is in the mask; GSF space, mirrored like FootBoxes). Wall pieces stand turned by WALL_ROT,
    /// so the model's directions are the engine's.
    /// </summary>
    public static double[][] WallPfBoxes(ModelManifest M, string? gfx, int mask)
    {
        var pf = M.Get(gfx)?.Pf;
        var res = new List<double[]>();
        if (pf != null) foreach (var r in pf)
                if (r.Length >= 8 && r[0] == 1 && r[4] > 0.5 && r[5] > 0.5 && (((uint)(long)r[7]) & 0xffe0 & (uint)mask) != 0) res.Add(new[] { -(r[1] + r[4] / 2), -(r[2] + r[5] / 2), r[4] / 2, r[5] / 2 });
        return res.Count > 0 ? res.ToArray() : Building.WALL_BOXES;
    }

    public sealed class Placed
    {
        public double[][] Boxes = Array.Empty<double[]>();
        public double Fx, Fz, Hx, Hz, Rot;
    }

    /// <summary>world-space footprint of a building model placed at (x, z) with rotation rot</summary>
    public static Placed Place(ModelManifest M, string? gfx, double x, double z, double rot, double margin = 0, double[][]? boxesOverride = null)
    {
        double c = SMath.Cos(rot), s = SMath.Sin(rot);
        var src = boxesOverride ?? FootBoxes(M, gfx);
        var boxes = src.Select(b => new[] { x + b[0] * c + b[1] * s, z - b[0] * s + b[1] * c, b[2] + margin, b[3] + margin }).ToArray();
        double x0 = 1e9, x1 = -1e9, z0 = 1e9, z1 = -1e9;
        foreach (var b in src) { x0 = Math.Min(x0, b[0] - b[2]); x1 = Math.Max(x1, b[0] + b[2]); z0 = Math.Min(z0, b[1] - b[3]); z1 = Math.Max(z1, b[1] + b[3]); }
        double lcx = (x0 + x1) / 2, lcz = (z0 + z1) / 2;
        return new Placed { Boxes = boxes, Fx = x + lcx * c + lcz * s, Fz = z - lcx * s + lcz * c, Hx = (x1 - x0) / 2, Hz = (z1 - z0) / 2, Rot = rot };
    }
}

// ======================================================================= resource nodes (trees, timber, stone, bushes, corpses)
public sealed partial class ResNode : Entity
{
    /// <summary>tree | timber | stone | bush | corpse | farm | fish</summary>
    public string Type;
    /// <summary>wood | stone | food</summary>
    public string Res;
    public double Amount, Max;
    public readonly OrderedSet<Unit> Workers = new();
    public int MaxWorkers;
    /// <summary>in the water (fish)</summary>
    public bool Water;
    /// <summary>the ground cells it blocks (trees, stones, bushes)</summary>
    public List<int>? CellsBlocked;
    public double Rot, Scale = 1;
    /// <summary>models of the stump / falling timber left behind (trees)</summary>
    public string? StumpKind, TimberKind;
    /// <summary>a corpse's decay timer</summary>
    public double RotT;
    /// <summary>other options of the map source (data)</summary>
    public Args Opts = new();

    public ResNode(World world, string type, string res, double x, double z, double amount, ResOpts? opts = null)
        : base(world, "res", null, x, z)
    {
        opts ??= new ResOpts();
        Type = type;
        Res = res;
        Cls = type;
        Amount = amount; Max = amount;
        Radius = opts.Radius ?? 1.5;
        MaxWorkers = opts.MaxWorkers ?? 8;
        Water = opts.Water;
        CellsBlocked = opts.Cells;
        Name = opts.Name ?? type;
        Gfx = opts.Name;
        Rot = opts.Rot; Scale = opts.Scale;
        StumpKind = opts.StumpKind; TimberKind = opts.TimberKind;
        if (opts.Extra != null) Opts = opts.Extra;
    }
}

/// <summary>options of World.AddResource (the JS opts spread onto the node)</summary>
public sealed class ResOpts
{
    public double? Radius;
    public int? MaxWorkers;
    public bool Water;
    public List<int>? Cells;
    public string? Name, StumpKind, TimberKind;
    public double Rot, Scale = 1;
    public Args? Extra;
}

// ======================================================================= projectiles
public sealed partial class Projectile
{
    static readonly Regex ArcRe = new("arrow|spear|stone|wehrspinne|molotov|poison|barrel|spittle", RegexOptions.Compiled);
    public readonly World World;
    public bool FixedAim;
    public Player? OwnerPlayer;
    public V3 From, Pos, Aim, Prev;
    public Entity? Target;
    public Weapon Weapon;
    /// <summary>attacking entity</summary>
    public Entity? Owner;
    /// <summary>flight time, time so far, arc height</summary>
    public double TT, T, Arc;
    /// <summary>the model flown (the weapon's projectile class), null: none</summary>
    public string? Gfx;
    public bool Alive = true;

    public Projectile(World world, V3 from, Entity? target, Weapon weapon, Entity? owner, V3? targetPos = null)
    {
        FixedAim = targetPos != null;
        OwnerPlayer = owner?.Owner;
        World = world;
        From = from;
        Pos = from;
        Target = target;
        Weapon = weapon;
        Owner = owner;
        Aim = targetPos ?? target!.Pos;
        Aim.Y += target != null ? Math.Min(2, (target is Unit tu ? tu.Height : 2) * 0.5) : 0.5;
        double d = V3.Dist(From, Aim);
        TT = Math.Max(0.12, d / Math.Max(10, weapon.BulletSpeed != 0 ? weapon.BulletSpeed : 40));
        T = 0;
        var id = (weapon.Projectile ?? "").ToLowerInvariant();
        Arc = ArcRe.IsMatch(id) ? Math.Min(0.3 * d, 12) : 0;
        Gfx = weapon.Projectile != null && world.Models.Has(weapon.Projectile) ? weapon.Projectile : null;
        Prev = Pos;
    }
}
