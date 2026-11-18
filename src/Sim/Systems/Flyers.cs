// MIRAGE's flying units: the pteranodons of the Norsemen and the Dustriders, the Dragon Clan's cargolifter and dirigible,
// the four tribes' zeppelins (only with the game option "FlyingEnabled"), the SEAS helicopter in its flying mode, and the
// wild Pteranodon and Archaeopteryx. Until now the remake had them as walking units (only the jetpack warrior flew).
//
// Ported from the MIRAGE flying units design v4 (delivered to the MIRAGE modder, tested in parts by Zidell; not in MIRAGE
// master a349cb41): mirage_flying_units_v4 GameFiles/.../Scripts/Server/classes/mirage/Mirage.usl class CFlyerCtrl (and
// its callers CAjePteranodon / CHuPteranodon, CNinigiCargoLifter / CNinigiDirigible, CZeppelin; Vehicle.usl
// CSeasHelicopter; Animal.usl CFlyingAnimal), _TechTree.ttree Moves/<TYPE>/fly_land, fly_takeoff (Rules.RemakeFlyAbilities).
//
// What a flyer does (CFlyerCtrl's header):
// * A move is the engine's flight (CActFlight FM_Flight): 15 m above the ground or the water, following the terrain, in
//   a straight line - no way finder, no unit collision (Unit.Flying). From the ground it climbs over 90 % of the route.
// * A move order first gives the flyer its own target at least one unit size away from the targets of the other flyers'
//   running orders and from the flyers standing there (FindFreeSpot); flyers ordered together to one point keep their
//   places (moved to the point, squeezed to 30 m). In a fight the new target stays as far from the enemy as the order's.
// * v4: a new move to within 4 m of the running flight's order point keeps that flight (no restart of the flight - the
//   chase in a fight gives one on every update).
// * An idle flyer in the air moves away from the other flyers once per order (1.5 s after it became idle) and stays in
//   the air: no idle landing (v3).
// * The abilities "Land" (fly_land, a target point: a free, passable land spot one unit size from the others; from more
//   than 24 m it flies at 15 m to 12 m before the spot first; from less than 4 m it takes a short 4 m approach) and
//   "Take off" (fly_takeoff, on the spot). A landed flyer stays on the ground until an order: a move order takes it up by
//   itself (the climb of FM_Flight), an attack takes off first (TakeOff, then the fight).
// * v4: the pteranodons (aje_pteranodon, hu_pteranodon) switch to their ground model "<gfx>_boden" when they have landed
//   (the "land" clip, then the ground model with "standanim"; it walks there with the ground model's walk_1, by the way
//   finder) and back to the flying model with "take_off" - a move order walks, only take-off and attacks fly.
// * The pteranodons attack in dives (FM_DiveCombat): here the height near the target goes down to the dive height (3 m
//   hand-to-hand, 8 m with a projectile, 12 m at buildings, 14.5 m at flyers) and the blows are the normal fight's.
// * Wild flyers (CFlyingAnimal) have no abilities and never land; the Archaeopteryx comes down to its enemy to fight.
//
// The remake's own choices (the engine's numbers are not known): the height changes at most 6 m/s (CLIMB_RATE); the
// flight's speed is the unit's walking speed as before. The registry of CFlyerCtrl (its arrays) is the FlyerState of each
// unit (Unit.Flyer). A saved game is a replay (docs/SAVES.md): all of it comes back by itself.
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>a flying unit's state (MIRAGE Mirage.usl CFlyerCtrl: one entry of its arrays, plus the engine's flight)</summary>
public sealed class FlyerState
{
    /// <summary>"ptera" (aje_pteranodon / hu_pteranodon: ground model), "vehicle" (cargolifter, dirigible), "zeppelin",
    /// "heli" (the SEAS helicopter's flying mode), "wild" (CFlyingAnimal: Pteranodon, Archaeopteryx)</summary>
    public string Kind = "";
    /// <summary>m_aiKind: 0, 1 KIND_ORDER, 2 KIND_SETTLE, 3 KIND_DIVE</summary>
    public int Order;
    public double ClickX, ClickZ, DestX, DestZ, AnchorX, AnchorZ;
    public double OrderTime = -1000, IdleSince = -1;
    public bool IdleDone, LandPending, Landing, LandAnim, TakeoffReady, TakeoffPending;
    public double LandX, LandZ, TowardX, TowardZ;
    /// <summary>v4: on the ground with the ground model "&lt;gfx&gt;_boden" (pteranodons): walks, doesn't fly</summary>
    public bool Ground;
    /// <summary>the land / take_off clip plays until</summary>
    public double AnimUntil;
    /// <summary>the engine's flight (CActFlight): 0 none, 1 FM_Flight, 2 FM_Land; the height and the route's length at its
    /// start, its point</summary>
    public int Mode;
    public double H0, D0, LegX, LegZ;
    /// <summary>taking off for a fight: the fight waits until it is up</summary>
    public bool Climb;
    /// <summary>the height is still changing (part of the flight action)</summary>
    public bool VMoving;
    public bool WasIdle;
    /// <summary>the way of the running flight (another way - the chase's straight line set by the fight - is a new flight
    /// from where it is)</summary>
    public NavPath? PathRef;
    public Entity? DiveEnemy;
}

public sealed partial class Unit
{
    /// <summary>MIRAGE's flying units (World.Flyers); null for every other unit (the jetpack warrior flies with Flying only)</summary>
    public FlyerState? Flyer;

    /// <summary>the model to show for the tech tree's gfx: the ground model of a landed pteranodon (v4: SetGFX
    /// "&lt;gfx&gt;_boden") when the data has it, else the gfx</summary>
    public string? LookGfx(string? gfx) =>
        Flyer is { Ground: true } && gfx != null && World.Models.Has(gfx + "_boden") ? (gfx + "_boden").ToLowerInvariant() : gfx;
}

public sealed partial class World
{
    public const double FLIGHT_HEIGHT = 15;        // CFlyerCtrl.FLIGHT_HEIGHT: FM_Flight's height, fixed in PWServer.exe
    const double FLY_UNIT_RADIUS_MIN = 3;          // UNIT_RADIUS_MIN: smallest radius used for spacing
    const double FLY_UNIT_GAP = 1;                 // UNIT_GAP: free space between two flyers
    const double FLY_GROUP_WINDOW = 1;             // GROUP_WINDOW: orders to one point within this time form one group
    const double FLY_GROUP_CLICK_DIST = 1;         // GROUP_CLICK_DIST
    const double FLY_FORMATION_RADIUS = 30;        // FORMATION_RADIUS
    const double FLY_ORDER_KEEP = 30;              // ORDER_KEEP: an order target keeps others away this long
    const double FLY_IDLE_DELAY = 1.5;             // IDLE_LAND_DELAY: seconds of idle before a flyer moves apart
    public const double FLY_LAND_APPROACH = 12;    // LAND_APPROACH
    public const double FLY_REISSUE_MIN = 4;       // REISSUE_MIN (v4)
    public const double FLY_LANDED_HEIGHT = 1;     // LANDED_HEIGHT
    public const double FLY_APPROACH_MIN = 4;      // APPROACH_MIN: the horizontal leg of a landing or a take-off on the spot
    const double DIVE_HEIGHT_MELEE = 3, DIVE_HEIGHT_RANGED = 8, DIVE_HEIGHT_BUILDING = 12, DIVE_HEIGHT_AIR = 14.5;
    /// <summary>the remake's: metres per second a flyer's height changes at most</summary>
    public const double FLY_CLIMB_RATE = 6;
    const int FK_ORDER = 1, FK_SETTLE = 2, FK_DIVE = 3;
    const int FM_FLIGHT = 1, FM_LAND = 2;

    // ------------------------------------------------------------------ which units fly
    /// <summary>the kind of flyer a unit is now (null: none) - by its script class; the zeppelins and the helicopter by the
    /// tech tree's flying_unit (CZeppelin m_bFlying = the option FlyingEnabled, whose filter xenage/flying_disabled sets
    /// flying_unit false; CSeasHelicopter m_bApache = the flying mode's filter seas_helicopter_flying)</summary>
    public string? FlyerKind(Unit u)
    {
        switch (Data.Script(u.Name))
        {
            case "CAjePteranodon": case "CHuPteranodon": return "ptera";
            case "CNinigiCargoLifter": case "CNinigiDirigible": return "vehicle";
            case "CFlyingAnimal": return "wild";
            case "CZeppelin": return FlyingUnitFlag(u) ? "zeppelin" : null;
            case "CSeasHelicopter": return FlyingUnitFlag(u) ? "heli" : null;
            default: return null;
        }
    }
    bool FlyingUnitFlag(Unit u) => Data.ObjectNode(u.Name, u.RulesOwner())?["flying_unit"]?.Scalar == "true";

    /// <summary>after the unit read its rules (Unit.ReadRules: spawn, level, tree change): becomes a flyer or stops being
    /// one (the helicopter's modes; CSeasHelicopter.HandleAction -> ChangeHeight: down it lands where it is)</summary>
    public void FlyerRules(Unit u)
    {
        var kind = FlyerKind(u);
        if (kind != null)
        {
            if (u.Flyer == null) { u.Flyer = new FlyerState { Kind = kind }; u.Flying = true; }
            return;
        }
        if (u.Flyer == null) return;
        u.Flyer = null;
        u.Flying = false;
        if (u.LiftH > 0) u.Lift = new Lift { From = u.LiftH, To = 0, T = 0, TT = Math.Max(0.5, u.LiftH / FLY_CLIMB_RATE) };
        var nav = NavFor(u);
        if (!nav.IsFree(u.Pos.X, u.Pos.Z)) { int c = nav.NearestFree(nav.Idx(u.Pos.X, u.Pos.Z), 8); if (c >= 0) (u.Pos.X, u.Pos.Z) = nav.Center(c); }
        u.Path.Clear();
    }

    /// <summary>is the flyer on the ground (CFlyerCtrl.IsLanded: less than 1 m above the ground or the water)</summary>
    public bool IsLanded(Unit u) => u.Flyer is { } f && (f.Ground || u.LiftH < FLY_LANDED_HEIGHT);

    /// <summary>the unit's model was built (Unit.BuildModel): a flyer's stand and walk clips - a pteranodon on the ground:
    /// "standanim", walk_1 at its clip's speed (the ground model's walk set def); in the air: "idle" (CAjePteranodon /
    /// CFlyingAnimal SetIdleAnim)</summary>
    public void FlyerLook(Unit u)
    {
        var f = u.Flyer; var A = u.Anim;
        if (f == null || A == null) return;
        if (f.Ground)
        {
            u.IdleAnim = A.Pick("standanim") ?? u.IdleAnim;
            u.WalkAnim = u.RunAnim = A.Pick("walk_1") ?? u.WalkAnim;
            double s = u.Gaits.TryGetValue("walk_1", out var g) && g > 0 ? g : 1.5;
            u.Speed = u.RunSpeed = u.AnimSpeed = u.RunAnimSpeed = s;
            return;
        }
        // (in the air; FlyerIdleClip picks the standing clip while it stands on the ground)
        bool ptera = f.Kind == "ptera" || (f.Kind == "wild" && u.Name.Equals("pteranodon", StringComparison.OrdinalIgnoreCase));
        var c = ptera ? A.Pick("idle", "fly") : f.Kind == "wild" ? A.Pick("fly") : null;
        if (c != null) u.IdleAnim = c;
    }

    /// <summary>the clip of an idle flyer (null: the unit's own idle clips): pteranodons in the air "idle", standing (landed
    /// or the ground model) "standanim"; the Archaeopteryx "fly" (CFlyingAnimal m_sFlyIdleAnim)</summary>
    public string? FlyerIdleClip(Unit u)
    {
        var f = u.Flyer; var A = u.Anim;
        if (f == null || A == null) return null;
        bool ptera = f.Kind == "ptera" || (f.Kind == "wild" && u.Name.Equals("pteranodon", StringComparison.OrdinalIgnoreCase));
        if (ptera) return f.Ground || IsLanded(u) ? A.Pick("standanim", "idle") : A.Pick("idle", "fly");
        if (f.Kind == "wild") return A.Pick("fly");
        return null;
    }

    // ------------------------------------------------------------------ every step
    /// <summary>a flyer's step (UnitUpdate, before its task): the height of the running flight, the end of the land /
    /// take_off clip, and what an idle flyer does (CFlyerCtrl.OnIdleEnter / OnIdleTick)</summary>
    void FlyerUpdate(Unit u, double dt)
    {
        var f = u.Flyer!;
        // a way set without FlyTo (the chase's straight line near the target): a flight from where it is
        if (!f.Ground && u.Path.Count > 0 && u.Path != f.PathRef) { f.PathRef = u.Path; var w = u.Path[^1]; f.LegX = w.X; f.LegZ = w.Z; StartLeg(u, f, FM_FLIGHT); }
        double want = FlyerWantHeight(u, f);
        double dh = want - u.LiftH, step = FLY_CLIMB_RATE * dt;
        u.LiftH = Math.Max(0, u.LiftH + Math.Max(-step, Math.Min(step, dh)));
        f.VMoving = Math.Abs(want - u.LiftH) > 0.05;
        if (u.Path.Count == 0 && !f.VMoving && f.Mode == FM_LAND && u.LiftH < 0.05) f.Mode = 0;
        // the land / take_off clip is over
        if (f.AnimUntil > 0 && Time >= f.AnimUntil) { f.AnimUntil = 0; u.BusyAnim = false; }
        bool idle = u.Task.Type == "idle" || u.Task.Type == "hold";
        if (!idle) { f.WasIdle = false; return; }
        if (!f.WasIdle) { f.WasIdle = true; FlyerIdleEnter(u, f); }
        FlyerIdleTick(u, f);
    }

    /// <summary>the height the flight wants now (the engine's CActFlight: FM_Flight climbs to 15 m over 90 % of the
    /// route, FM_Land comes down over the route; a dive goes down near the target)</summary>
    double FlyerWantHeight(Unit u, FlyerState f)
    {
        if (f.Ground) return 0;
        // (taking off for a fight: up to 15 m first, then the dive)
        double cruise = f.Climb ? FLIGHT_HEIGHT : DiveHeight(u) ?? FLIGHT_HEIGHT;
        if (f.Mode == FM_LAND)
        {
            if (u.Path.Count == 0) return 0;
            double d = Hypot(f.LegX - u.Pos.X, f.LegZ - u.Pos.Z);
            return f.H0 * Math.Min(1, d / Math.Max(0.01, f.D0));
        }
        if (f.Mode == FM_FLIGHT)
        {
            if (u.Path.Count == 0) return cruise;
            double d = Hypot(f.LegX - u.Pos.X, f.LegZ - u.Pos.Z);
            double frac = Math.Max(0, Math.Min(1, (1 - d / Math.Max(0.01, f.D0)) / 0.9));
            // (already higher: straight to the new height, no dip)
            return f.H0 >= cruise ? cruise : f.H0 + (cruise - f.H0) * frac;
        }
        return u.LiftH;
    }

    /// <summary>a pteranodon's dive (StartDive: DIVE_HEIGHT_*; the wild Archaeopteryx comes down to its enemy's radius
    /// - CFlyingAnimal.GetFlyingHeight, m_fAttackHeight 0): the height near the target it attacks, null when it isn't</summary>
    double? DiveHeight(Unit u)
    {
        var f = u.Flyer!;
        if (u.Task.Type != "attack" || u.Task.Target is not { Alive: true } e) return null;
        bool arch = f.Kind == "wild" && u.Name.Equals("archaeopteryx", StringComparison.OrdinalIgnoreCase);
        bool ptera = f.Kind == "ptera" || (f.Kind == "wild" && !arch);
        if (!ptera && !arch) return null;
        double h;
        if (arch) h = e.Radius;
        else if (e is Unit eu && eu.Flying && !(eu.Flyer != null && IsLanded(eu))) h = DIVE_HEIGHT_AIR;
        else if (e is Building) h = DIVE_HEIGHT_BUILDING;
        else if (u.Weapons.Long?.Projectile != null) h = DIVE_HEIGHT_RANGED;
        else h = DIVE_HEIGHT_MELEE;
        // down near the target: within its reach the dive's height, from 15 m farther out the cruise
        double d = e is Building b ? b.SurfDist(u.Pos.X, u.Pos.Z) : Hypot(e.Pos.X - u.Pos.X, e.Pos.Z - u.Pos.Z) - e.Radius;
        double reach = u.Radius + 2 + (u.Weapons.Long != null ? (u.Cs(u.Weapons.Long)?.Range ?? 0) : 0);
        double k = Math.Max(0, Math.Min(1, (d - reach) / 15));
        return h + (FLIGHT_HEIGHT - h) * k;
    }

    /// <summary>OnIdleEnter: after its own separation / landing / take-off move a flyer stays where it is (v4: no new
    /// check, no turning on the spot); only a real order allows the next separation move</summary>
    void FlyerIdleEnter(Unit u, FlyerState f)
    {
        f.IdleSince = Time;
        f.IdleDone = false;
        f.DiveEnemy = null;
        if (f.Order == FK_DIVE) f.Order = 0;
        if (f.Order == FK_SETTLE) f.IdleDone = true;
    }

    /// <summary>the flight, the land / take_off clip: still busy (CFightingObj.HasAction)</summary>
    bool FlyerBusy(Unit u, FlyerState f) => u.Path.Count > 0 || f.VMoving || Time < f.AnimUntil;

    /// <summary>OnIdleTick: the landing's second leg, the land clip and the ground model, the take-off after its clip, and
    /// 1.5 s after an order the move apart (once)</summary>
    void FlyerIdleTick(Unit u, FlyerState f)
    {
        if (!u.Alive || u.Inside || FlyerBusy(u, f)) return;
        if (f.LandPending)
        {
            // v3: the land ability flew its first leg, now land
            f.LandPending = false;
            f.IdleDone = true;
            StartFlight(u, f, f.LandX, f.LandZ, FM_LAND, true);
            SetFlyOrder(f, FK_SETTLE, f.LandX, f.LandZ, f.LandX, f.LandZ, u.Pos.X, u.Pos.Z);
            f.Landing = true;
            u.AnchorX = f.LandX; u.AnchorZ = f.LandZ;
            return;
        }
        if (f.Landing)
        {
            // v4: the landing flight ended: "land" clip, then the ground model
            f.Landing = false;
            f.IdleDone = true;
            if (!IsLanded(u)) return;
            if (u.Anim!.Has("land")) { PlayFlyClip(u, f, "land"); f.LandAnim = true; return; }
            EnterGround(u);
            return;
        }
        if (f.LandAnim) { f.LandAnim = false; EnterGround(u); return; }   // v4: the "land" clip ended
        if (f.TakeoffPending)
        {
            // v4: the "take_off" clip ended (the take-off ability): now fly up
            f.TakeoffPending = false;
            TakeOff(u, f.TowardX, f.TowardZ);
            return;
        }
        if (f.IdleDone) return;
        if (IsLanded(u)) { f.IdleDone = true; return; }        // v3: landed by the ability: stays on the ground
        if (f.IdleSince < 0 || Time < f.IdleSince) { f.IdleSince = Time; return; }
        if (Time - f.IdleSince < FLY_IDLE_DELAY) return;
        f.IdleDone = true;
        Settle(u, f);
    }

    void PlayFlyClip(Unit u, FlyerState f, string clip)
    {
        double len = Moves.Play(u, clip);
        f.AnimUntil = len > 0 ? Time + len : 0;
        if (len <= 0) u.BusyAnim = false;
    }

    // ------------------------------------------------------------------ moves
    /// <summary>the flight to a point (the flyer's WalkAction -> FlyTo; Unit.SetPath). Own target apart from the other
    /// flyers; v4: a new move to within 4 m of the running flight's order point keeps the flight</summary>
    public bool FlyTo(Unit u, double x, double z)
    {
        var f = u.Flyer!;
        var (cx, cz) = InPlay(x, z);
        if (u.Path.Count > 0 && f.Order == FK_ORDER && f.Mode == FM_FLIGHT && Hypot(cx - f.ClickX, cz - f.ClickZ) < FLY_REISSUE_MIN) return true;
        f.LandPending = false; f.Landing = false; f.LandAnim = false; f.TakeoffPending = false;
        // a pteranodon's attack flies at its target itself (StartDive: no own target, FM_DiveCombat)
        if (u.Task.Type == "attack" && u.Task.Target is { Alive: true } dt && (f.Kind == "ptera" || f.Kind == "wild"))
        {
            StartFlight(u, f, cx, cz, FM_FLIGHT, false);
            SetFlyOrder(f, FK_DIVE, cx, cz, cx, cz, u.Pos.X, u.Pos.Z);
            f.DiveEnemy = dt;
            f.IdleSince = -1; f.IdleDone = false;
            return true;
        }
        // a trading zeppelin flies straight to the market (CZeppelin.WalkAction: FlyTo(.., GetCurTaskName() != "Trade"))
        if (u.Task.Type == "trade")
        {
            StartFlight(u, f, cx, cz, FM_FLIGHT, false);
            SetFlyOrder(f, FK_ORDER, cx, cz, cx, cz, u.Pos.X, u.Pos.Z);
            f.IdleSince = -1; f.IdleDone = false; f.DiveEnemy = null;
            return true;
        }
        double ax = u.Pos.X, az = u.Pos.Z;
        // approach in a fight: stay within the same distance to the enemy, else the unit is out of range
        (double X, double Z, double R)? limit = null;
        if (u.Task.Type == "attack" && u.Task.Target is { Alive: true } en) limit = (en.Pos.X, en.Pos.Z, Hypot(cx - en.Pos.X, cz - en.Pos.Z) + 0.5);
        // flyers of the same owner ordered to the same point within a second: one group (the first one's anchor)
        foreach (var o in Units)
        {
            var of = o.Flyer;
            if (o == u || of == null || !o.Alive || of.Order != FK_ORDER || Math.Abs(Time - of.OrderTime) > FLY_GROUP_WINDOW) continue;
            if (Hypot(of.ClickX - cx, of.ClickZ - cz) > FLY_GROUP_CLICK_DIST || o.Owner != u.Owner) continue;
            ax = of.AnchorX; az = of.AnchorZ;
            break;
        }
        double ox = u.Pos.X - ax, oz = u.Pos.Z - az, len = Hypot(ox, oz);
        if (len > FLY_FORMATION_RADIUS) { ox *= FLY_FORMATION_RADIUS / len; oz *= FLY_FORMATION_RADIUS / len; }
        var (dx, dz) = limit != null ? FindFreeSpot(u, cx, cz, false, limit) ?? (cx, cz) : FindFreeSpot(u, cx + ox, cz + oz, false, null) ?? (cx, cz);
        StartFlight(u, f, dx, dz, FM_FLIGHT, false);
        SetFlyOrder(f, FK_ORDER, cx, cz, dx, dz, ax, az);
        f.IdleSince = -1; f.IdleDone = false; f.DiveEnemy = null;
        return true;
    }

    /// <summary>a straight flight to (x, z) (FlightAction with FM_Flight / FM_Land). task: give an idle flyer a walk task
    /// for it (the flights the flyer makes on its own: settling, landing, taking off)</summary>
    void StartFlight(Unit u, FlyerState f, double x, double z, int mode, bool task)
    {
        u.Path = new NavPath { new Waypoint(x, z) };
        u.Goal = (x, z); u.Breach = null; u.NoWay = null; u.StuckT = 0; u.StuckFrom = u.Pos;
        f.LegX = x; f.LegZ = z; f.PathRef = u.Path;
        StartLeg(u, f, mode);
        if (task && (u.Task.Type == "idle" || u.Task.Type == "hold")) u.Task = new UnitTask("move") { X = x, Z = z, User = false };
    }
    void StartLeg(Unit u, FlyerState f, int mode)
    {
        f.Mode = mode; f.H0 = u.LiftH; f.D0 = Math.Max(0.01, Hypot(f.LegX - u.Pos.X, f.LegZ - u.Pos.Z));
    }

    /// <summary>SetOrder: the running order's kind, point, own target and anchor, now; v4: a new flight ends a landing /
    /// take-off in progress (the caller sets it again)</summary>
    void SetFlyOrder(FlyerState f, int kind, double cx, double cz, double dx, double dz, double ax, double az)
    {
        f.Order = kind; f.ClickX = cx; f.ClickZ = cz; f.DestX = dx; f.DestZ = dz; f.AnchorX = ax; f.AnchorZ = az;
        f.OrderTime = Time;
        f.Landing = false; f.LandAnim = false; f.TakeoffPending = false;
    }

    /// <summary>Settle: an idle flyer in the air moves away from the others (IDLE_LANDING 0: it doesn't land)</summary>
    bool Settle(Unit u, FlyerState f)
    {
        var s = FindFreeSpot(u, u.Pos.X, u.Pos.Z, false, null);
        if (s == null) return false;
        var (sx, sz) = s.Value;
        if (Hypot(sx - u.Pos.X, sz - u.Pos.Z) <= 0.5) return false;
        StartFlight(u, f, sx, sz, FM_FLIGHT, true);
        SetFlyOrder(f, FK_SETTLE, sx, sz, sx, sz, u.Pos.X, u.Pos.Z);
        u.AnchorX = sx; u.AnchorZ = sz;
        return true;
    }

    /// <summary>TakeOff: a landed flyer climbs (the take-off ability; a fight takes off first). v4: back to the flying
    /// model, the "take_off" clip first - the next TakeOff flies (4 m toward the point, climbing to 15 m)</summary>
    public bool TakeOff(Unit u, double tx, double tz)
    {
        var f = u.Flyer;
        if (f == null || !IsLanded(u)) return false;
        LeaveGround(u);
        if (!f.TakeoffReady && u.Anim!.Has("take_off"))
        {
            u.Path.Clear(); u.Vel = V3.Zero;
            PlayFlyClip(u, f, "take_off");
            f.Landing = false; f.LandAnim = false;
            f.TakeoffReady = true; f.TakeoffPending = true;
            f.TowardX = tx; f.TowardZ = tz;
            return true;
        }
        f.TakeoffReady = false;
        double dx = tx - u.Pos.X, dz = tz - u.Pos.Z, d = Hypot(dx, dz);
        if (d < 0.1) { double a = SpreadAngle(u, 0); dx = SMath.Cos(a); dz = SMath.Sin(a); d = 1; }
        var (x, z) = InPlay(u.Pos.X + dx / d * FLY_APPROACH_MIN, u.Pos.Z + dz / d * FLY_APPROACH_MIN);
        StartFlight(u, f, x, z, FM_FLIGHT, true);
        SetFlyOrder(f, FK_SETTLE, x, z, x, z, u.Pos.X, u.Pos.Z);
        f.LandPending = false; f.IdleSince = -1; f.IdleDone = false;
        f.Climb = true;
        return true;
    }

    /// <summary>"Land" (fly_land): at (or next to) the point, on a free passable land spot one unit size from the other
    /// units. Farther than 24 m: at 15 m to 12 m before the spot first, then down (OnIdleTick); nearer than 4 m: a short
    /// 4 m approach instead of a drop. A pteranodon already on the ground walks there.</summary>
    public bool LandAt(Unit u, double x, double z)
    {
        var f = u.Flyer;
        if (f == null || !u.Alive || u.Inside) return false;
        var spot = FindFreeSpot(u, x, z, true, null);
        if (spot == null) return false;
        var (sx, sz) = spot.Value;
        FlyerStop(u);
        if (f.Ground)
        {
            u.Task = new UnitTask("move") { X = sx, Z = sz, User = true };
            u.SetPath(sx, sz);
            return true;
        }
        double vx = sx - u.Pos.X, vz = sz - u.Pos.Z, dist = Hypot(vx, vz);
        f.DiveEnemy = null; f.IdleSince = -1; f.IdleDone = false;
        bool landed = IsLanded(u);
        if (dist > FLY_LAND_APPROACH * 2 && !landed)
        {
            var (px, pz) = InPlay(sx - vx / dist * FLY_LAND_APPROACH, sz - vz / dist * FLY_LAND_APPROACH);
            StartFlight(u, f, px, pz, FM_FLIGHT, true);
            u.Task.User = true;
            SetFlyOrder(f, FK_SETTLE, sx, sz, sx, sz, u.Pos.X, u.Pos.Z);
            f.LandPending = true; f.LandX = sx; f.LandZ = sz;
            u.AnchorX = sx; u.AnchorZ = sz;
            return true;
        }
        if (dist < 0.5 && landed) return true;
        if (dist < FLY_APPROACH_MIN && !landed)
        {
            // FM_Land spreads the descent over the route: a short approach instead of a vertical drop
            double ax, az;
            if (dist < 0.1) { double a = SpreadAngle(u, 0); ax = SMath.Cos(a); az = SMath.Sin(a); }
            else { ax = vx / dist; az = vz / dist; }
            if (FindFreeSpot(u, u.Pos.X + ax * FLY_APPROACH_MIN, u.Pos.Z + az * FLY_APPROACH_MIN, true, null) is { } alt) (sx, sz) = alt;
        }
        f.LandPending = false;
        StartFlight(u, f, sx, sz, FM_LAND, true);
        u.Task.User = true;
        SetFlyOrder(f, FK_SETTLE, sx, sz, sx, sz, u.Pos.X, u.Pos.Z);
        f.Landing = true;
        u.AnchorX = sx; u.AnchorZ = sz;
        return true;
    }

    /// <summary>"Take off" (fly_takeoff): a landed flyer rises on the spot (its tasks stopped first)</summary>
    public bool TakeOffAbility(Unit u)
    {
        if (u.Flyer == null || !u.Alive || u.Inside || !IsLanded(u)) return true;
        FlyerStop(u);
        TakeOff(u, u.Pos.X, u.Pos.Z);
        return true;
    }

    /// <summary>StopEverything: the tasks and the running flight end</summary>
    void FlyerStop(Unit u)
    {
        ToIdle(u);
        u.Path.Clear(); u.Vel = V3.Zero;
        var f = u.Flyer!;
        f.WasIdle = true;          // (no idle entry of its own: the ability sets what comes next)
        f.Climb = false;
    }

    /// <summary>a fight's turn for a flyer (FightingObj.AttackEnemy of the flyers: a landed flyer takes off first, the
    /// fight task attacks after it; CFlyerCtrl.TakeOff / StartDive): true while it is taking off</summary>
    bool FlyerAttackGate(Unit u, Entity tgt, double dt)
    {
        var f = u.Flyer!;
        if (Time < f.AnimUntil) { u.Vel = V3.Zero; return true; }               // the take_off clip
        if (f.Climb)
        {
            if (u.Path.Count > 0) { double sp = u.Steer(dt, u.RunSpeed, 0.5); u.MoveAnim(sp, true); return true; }
            if (u.LiftH < FLIGHT_HEIGHT - 0.5) { u.Vel = V3.Zero; return true; }
            f.Climb = false;
        }
        if (IsLanded(u) && TakeOff(u, tgt.Pos.X, tgt.Pos.Z)) return true;
        return false;
    }

    // ------------------------------------------------------------------ the ground model (v4, pteranodons)
    /// <summary>EnterGround: the ground model "&lt;gfx&gt;_boden" (walks, doesn't fly), "standanim"</summary>
    void EnterGround(Unit u)
    {
        var f = u.Flyer!;
        if (f.Kind != "ptera" || f.Ground) return;
        f.Ground = true; f.Mode = 0;
        u.Flying = false;
        u.LiftH = 0;
        var nav = NavFor(u);
        if (!nav.IsFree(u.Pos.X, u.Pos.Z)) { int c = nav.NearestFree(nav.Idx(u.Pos.X, u.Pos.Z), 8); if (c >= 0) (u.Pos.X, u.Pos.Z) = nav.Center(c); }
        u.BusyAnim = false;
        u.BuildModel();
    }

    /// <summary>LeaveGround: back to the flying model</summary>
    void LeaveGround(Unit u)
    {
        var f = u.Flyer!;
        if (!f.Ground) return;
        f.Ground = false;
        u.Flying = true;
        u.BuildModel();
    }

    // ------------------------------------------------------------------ free spots
    readonly List<(double X, double Z, double R)> _flyObs = new();

    /// <summary>GetUnitRadius: the unit's radius, at least 3 m (the old collision regions were 3.5 m)</summary>
    static double FlyRadius(Unit u) => Math.Max(u.Radius, FLY_UNIT_RADIUS_MIN);

    static double SpreadAngle(Unit u, int k) => ((Math.Abs(u.Id) * 37 + k * 101) % 628) / 100.0;

    (double X, double Z) InPlay(double x, double z) => (Math.Max(Play.X0 + 1, Math.Min(Play.X1 - 1, x)), Math.Max(Play.Z0 + 1, Math.Min(Play.Z1 - 1, z)));

    /// <summary>CollectObstacles: targets of the running orders of the other flyers, and the flyers where they are (a flyer
    /// on its way counts by its target); ground: also the units on the ground (for a landing spot)</summary>
    void CollectFlyObstacles(Unit u, double x, double z, double range, bool ground)
    {
        _flyObs.Clear();
        foreach (var o in Units)
        {
            var of = o.Flyer;
            if (o == u || of == null || of.Order == FK_DIVE || !o.Alive || o.Inside) continue;
            if (Time < of.OrderTime || Time - of.OrderTime > FLY_ORDER_KEEP) continue;
            if (Hypot(of.DestX - x, of.DestZ - z) > range) continue;
            _flyObs.Add((of.DestX, of.DestZ, FlyRadius(o)));
        }
        UHash.Query(x, z, range, o =>
        {
            if (o == u || !o.Alive || o.Inside || o.Parked || (o.Cls != "ANML" && o.Cls != "VHCL" && o.Cls != "CHTR")) return;
            if (Hypot(o.Pos.X - x, o.Pos.Z - z) > range) return;
            var of = o.Flyer;
            bool flyer = of != null || o.Flying;
            if (!flyer && !ground) return;
            if (of != null && of.Order != FK_DIVE && o.Path.Count > 0 && Time >= of.OrderTime && Time - of.OrderTime <= FLY_ORDER_KEEP) return;
            _flyObs.Add((o.Pos.X, o.Pos.Z, FlyRadius(o)));
        });
    }

    int FindFlyConflict(double x, double z, double r)
    {
        int best = -1; double bd = 0;
        for (int i = 0; i < _flyObs.Count; i++)
        {
            var (ox, oz, or) = _flyObs[i];
            double d = Hypot(ox - x, oz - z);
            if (d < r + or + FLY_UNIT_GAP && (best < 0 || d < bd)) { best = i; bd = d; }
        }
        return best;
    }

    bool FlySpotOk(double x, double z, double r, bool ground, (double X, double Z, double R)? limit)
    {
        if (FindFlyConflict(x, z, r) >= 0) return false;
        if (limit is { } l && Hypot(x - l.X, z - l.Z) > l.R) return false;
        if (ground)
        {
            if (WaterLevel != null && Height(x, z) < WaterLevel.Value) return false;   // (IsWater: under the sea level)
            if (!Nav.IsFree(x, z)) return false;
        }
        return true;
    }

    /// <summary>FindFreeSpot: a spot near the wanted point at least one unit size from the obstacles; ground: a free land
    /// spot to land on (null: none). Pushes away from the nearest obstacle up to 8 times, then rings around the point.</summary>
    public (double X, double Z)? FindFreeSpot(Unit u, double wx, double wz, bool ground, (double X, double Z, double R)? limit)
    {
        double r = FlyRadius(u), step = r * 2 + FLY_UNIT_GAP;
        CollectFlyObstacles(u, wx, wz, step * 7 + 20, ground);
        var (sx, sz) = InPlay(wx, wz);
        for (int k = 0; k < 8; k++)
        {
            int hit = FindFlyConflict(sx, sz, r);
            if (hit < 0) break;
            var (ox, oz, or) = _flyObs[hit];
            double ax = sx - ox, az = sz - oz, al = Hypot(ax, az);
            if (al < 0.1) { double a = SpreadAngle(u, k); ax = SMath.Cos(a); az = SMath.Sin(a); al = 1; }
            double need = r + or + FLY_UNIT_GAP + 0.1;
            (sx, sz) = InPlay(ox + ax / al * need, oz + az / al * need);
        }
        if (FlySpotOk(sx, sz, r, ground, limit)) return (sx, sz);
        // crowded, or no land there: rings around the wanted point
        for (int ring = 1; ring <= 6; ring++)
        {
            double dist = step * ring; int n = 6 * ring;
            for (int i = 0; i < n; i++)
            {
                double a = Math.PI * 2 * i / n + SpreadAngle(u, 0);
                var (cx, cz) = InPlay(wx + SMath.Cos(a) * dist, wz + SMath.Sin(a) * dist);
                if (FlySpotOk(cx, cz, r, ground, limit)) return (cx, cz);
            }
        }
        if (ground) return null;
        return limit != null ? InPlay(wx, wz) : (sx, sz);
    }
}

/// <summary>the flyers' abilities (MIRAGE v3: tech tree Moves/VHCL|ANML/fly_land, fly_takeoff - CFlyerCtrl.HandleAbility)</summary>
public static class Flyers
{
    public static bool IsAbility(string id) => id == "fly_land" || id == "fly_takeoff";

    internal static void Register(Dictionary<string, MoveDef> moves)
    {
        // "Land": the point from the secondary controller (VEC3); acts at once (a queued order is not supported)
        moves["fly_land"] = new MoveDef { Id = "fly_land", Target = "ground", Range = 1e9, Run = (W, u, t, p, s) =>
            p is { } q ? W.LandAt(u, q.X, q.Z) : t != null && W.LandAt(u, t.Pos.X, t.Pos.Z) };
        // "Take off": on the spot
        moves["fly_takeoff"] = new MoveDef { Id = "fly_takeoff", Self = true, Run = (W, u, t, p, s) => W.TakeOffAbility(u) };
    }
}
