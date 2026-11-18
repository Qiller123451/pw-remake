// MIRAGE's portal terminus (CPortalTerminus: aje_ / hu_ / ninigi_portal_terminus, and the shared base of the SEAS'
// CSeasPortalTerminus, which adds only its part seas_wg_misc_0: MirageSeasBuildings.cs): two termini of one side are tied
// together, and units sent into one come out of the other. Kept in a file of its own, named for the base class.
//
// Reference (MIRAGE, Data/MIRAGE/Scripts/Server): classes/mirage/Mirage.usl CPortalTerminus (9706-10017);
// classes/mirage/Miracle.usl CEnterPortal (the task "EntrPortal", 6362-6545); classes/FightingObj/FightingObj.usl
// EnterPortal (13783-13826), NextJob (8950-8966), SetRallyPoint -> CheckDestination (11450-11493), GetRallyPosition
// (11495-11510: a rally target that is a terminus gives its release point), MountThatUnit (11727);
// classes/character/character.usl NextJob (1767: a worker sent to its own terminus enters it);
// classes/misc/StartLocation.usl InitWarpGate (770-785: portal_terminus_visible when the warp gate is off and the option
// "Portals" is on); Game/controller/GameInputController.usl (987, 1026-1028, 1474-1481: the right click "EnterPortal"
// and the "load" cursor for characters, animals and vehicles - not ships, not buildings).
//
// The rules:
// * a terminus is a rally site (SetRallySite in OnInit: also while a site). Its rally point set on another terminus of
//   its owner or of a mutual ally - finished or not - makes that one its destination (CheckDestination / SetDestination);
//   set on itself, the destination is cleared; set on open ground, the destination stays. A destination of a player that
//   is no longer a mutual ally is dropped when the rally point is set on it again. A destroyed terminus is nobody's
//   destination any more (the destructor's RemovePartner), and its passengers' tasks end.
// * a terminus works (IsFunctionable) when its destination is finished (construct level 4) and of a mutual ally.
// * units (characters, animals, vehicles; not ships, nobody inside a transport) sent into an own terminus - or into a
//   mutual ally's when the diplomacy is locked (DiploLocked) - that works register as its passengers and run there
//   (AdvanceAction 3 m, at their maximum speed); within its radius + 10 m they enter (STATE_ON_THE_WAY; the original also
//   asks for a line of sight - the walk grid has the portal's own cells blocked), else they wait 0.8 s and try again,
//   five times. There the terminus lets them through one per second (RELEASE_FREQUENCY 1 s: the destination is
//   "occupied" for that long - the others wait their turn): the unit stands at the destination's release point, turned
//   as the destination, and walks on to the destination's rally point when it belongs to the destination's owner. A
//   destination that is no longer allied, or a terminus that lost it, ends the task. Sent into a terminus that does not
//   work, they just walk to it.
// * the release point (CalculateReleasePos, when finished; the terminus' own place before): from the model's links Bl_0
//   and Bl_1 - half way from Bl_1 towards Bl_0 (hu_warpgate: 2/5 of the way, and from there 1.5 times as far out from the
//   gate's centre), on the ground. Without the links: in front of the gate.
// * a producer whose rally point is set on a finished terminus of its side sends its new units into it (NextJob ->
//   EnterPortal); their rally position is the terminus' release point (GetCustomRallyPos).
// * build: visibility -1 + age_5 (+1) + the game option "Portals" (portal_terminus_visible, +1: GameplayOptions.Portals -
//   MIRAGE offers it only when the warp gate is off); 800 food, 800 wood, 800 stone, epoch 5, at most 4.
// Not ported: the attribute "CurDest" the client shows (the info window has no line for it).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>a portal terminus' state (CPortalTerminus m_xDestination, m_xPartners, m_xReleases, m_bOccupiedATM,
/// m_vReleasePos1)</summary>
public sealed class PortalState
{
    /// <summary>where its passengers come out (null: none - the terminus does not work)</summary>
    public Building? Dest;
    /// <summary>the termini whose destination this one is</summary>
    public readonly List<Building> Partners = new();
    /// <summary>the units registered to go through (RegisterPassenger)</summary>
    public readonly OrderedSet<Unit> Releases = new();
    /// <summary>a unit came out here: the next one not before this time (TERMINUS_RELEASE, 1 s)</summary>
    public double OccupiedUntil = -1;
    /// <summary>where its arrivals stand (CalculateReleasePos)</summary>
    public V3 ReleasePos;
}

public sealed partial class Building
{
    public PortalState? Portal;
}

public sealed partial class World
{
    /// <summary>CPortalTerminus.RELEASE_FREQUENCY: one unit a second comes out of a destination</summary>
    public const double PORTAL_RELEASE = 1.0;
    /// <summary>CEnterPortal: the entering distance beyond the terminus' collision radius, the tries</summary>
    const double PORTAL_ENTER = 10;
    const int PORTAL_RETRIES = 5;
    static readonly HashSet<string> PORTAL_USERS = new(StringComparer.Ordinal) { "CHTR", "ANML", "VHCL" };

    /// <summary>CPortalTerminus (MirageClasses.cs MIRAGE_CLASSES; the SEAS terminus inherits it)</summary>
    sealed class PortalTerminusB : BuildingBehaviour
    {
        // (the constructor: the release point is the terminus' place until it is finished)
        public override void Init(Building b, World W) { b.Portal = new PortalState { ReleasePos = b.Pos }; }
        public override void Built(Building b, World W) { b.Portal ??= new PortalState(); b.Portal.ReleasePos = W.PortalReleasePos(b); }
        public override void Destroyed(Building b, World W)
        {
            var s = b.Portal;
            if (s == null) return;
            // the destructor: the partners lose their destination (RemovePartner); the passengers' tasks end
            foreach (var p in s.Partners.ToList()) if (p.Portal != null && p.Portal.Dest == b) p.Portal.Dest = null;
            s.Partners.Clear();
            if (s.Dest?.Portal != null) s.Dest.Portal.Partners.Remove(b);
            s.Dest = null;
            foreach (var u in s.Releases.ToList()) if (u.Task.Type == "portal" && u.Task.Target == b) W.PortalQuit(u, b);
            s.Releases.Clear();
        }
    }

    /// <summary>a portal terminus (CPortalTerminus and the classes inheriting from it)</summary>
    public static bool IsPortal(Entity? e) => e is Building b && b.Portal != null;

    /// <summary>IsFunctionable: its destination is finished and of a mutual ally</summary>
    public bool PortalWorks(Building b)
    {
        var d = b.Portal?.Dest;
        return b.Alive && d != null && d.Alive && d.Built && Mutual(d.Owner, b.Owner);
    }

    /// <summary>SetDestination / CheckDestination: the terminus' rally point set on target (another terminus of a mutual
    /// ally: the destination; itself: none; an unfriendly one: dropped if it was the destination)</summary>
    public void PortalSetDestination(Building b, Entity? target)
    {
        var s = b.Portal;
        if (s == null || target == null) return;
        if (target == b) { ClearDestination(b); return; }
        if (target is not Building d || d.Portal == null || !d.Alive) return;
        if (!Mutual(d.Owner, b.Owner)) { if (s.Dest == d) ClearDestination(b); return; }
        if (s.Dest != null && s.Dest != d) s.Dest.Portal?.Partners.Remove(b);
        if (!d.Portal.Partners.Contains(b)) d.Portal.Partners.Add(b);
        s.Dest = d;
    }
    void ClearDestination(Building b)
    {
        var s = b.Portal!;
        s.Dest?.Portal?.Partners.Remove(b);
        s.Dest = null;
    }

    /// <summary>GetCustomRallyPos: where a rally point on the terminus sends the units (its release point)</summary>
    public (double X, double Z) PortalRallyPos(Building b) => b.Portal is { } s ? (s.ReleasePos.X, s.ReleasePos.Z) : (b.Pos.X, b.Pos.Z);

    /// <summary>CalculateReleasePos: from the links Bl_0 and Bl_1 (in front of the gate without them), on the ground</summary>
    public V3 PortalReleasePos(Building b)
    {
        V3 pos = b.Pos;
        if (b.LinkWorld("Bl_0") is V3 t1 && b.LinkWorld("Bl_1") is V3 t2)
        {
            bool hu = string.Equals(b.Gfx, "hu_warpgate", StringComparison.OrdinalIgnoreCase);
            double dx = t1.X - t2.X, dz = t1.Z - t2.Z, dist = Math.Sqrt(dx * dx + dz * dz);
            dist = hu ? dist * 2 / 5 : dist * 0.5;
            double n = Math.Sqrt(dx * dx + dz * dz);
            double t3x = n > 1e-9 ? t2.X + dx / n * dist : t2.X, t3z = n > 1e-9 ? t2.Z + dz / n * dist : t2.Z;
            if (hu)
            {
                // (from the gate's centre 1.5 times as far as the point on the line)
                pos = new V3(pos.X + (t3x - pos.X) * 1.5, 0, pos.Z + (t3z - pos.Z) * 1.5);
            }
            else pos = new V3(t3x, 0, t3z);
        }
        else
        {
            // (no links in the model data: in front of the gate - its heading's direction)
            var (x, z) = b.ExitPoint(b.Pos.X - SMath.Sin(b.Rot) * 20, b.Pos.Z - SMath.Cos(b.Rot) * 20);
            pos = new V3(x, 0, z);
        }
        pos.Y = Height(pos.X, pos.Z);
        return pos;
    }

    /// <summary>may u be sent into the terminus b (EnterPortal: not in a transport, no ship; own, or a mutual ally's
    /// with locked diplomacy; characters, animals, vehicles - GameInputController)</summary>
    public bool CanEnterPortal(Unit u, Entity? t)
    {
        if (t is not Building b || b.Portal == null || !b.Alive || !b.Built || !u.Alive || u.Inside || u.Naval || u.Owner == null) return false;
        if (!PORTAL_USERS.Contains(u.Cls)) return false;
        if (u.Owner != b.Owner && !(Opts.DiplomacyLocked && Mutual(u.Owner, b.Owner))) return false;
        return true;
    }

    /// <summary>the order "portal": into a terminus that works (CEnterPortal: RegisterPassenger), else just a walk to it
    /// (EnterPortal's GoTo)</summary>
    void PortalOrder(Unit u, Entity? t, bool user)
    {
        if (t is not Building b) return;
        if (!CanEnterPortal(u, b) || !PortalWorks(b)) { Order(u, new Order("move") { X = b.Pos.X, Z = b.Pos.Z }); return; }
        b.Portal!.Releases.Add(u);
        u.Task = new UnitTask("portal") { Target = b, User = user, Tries = PORTAL_RETRIES };
    }

    /// <summary>FightingObj.NextJob for a producer's rally target: a finished terminus of a mutual ally - in it goes;
    /// false: not such a terminus</summary>
    public bool PortalNextJob(Unit u, Building pt)
    {
        if (!IsPortal(pt)) return false;
        if (!pt.Alive || !pt.Built || !Mutual(u.Owner, pt.Owner)) return false;
        Order(u, new Order("portal") { Target = pt });
        return true;
    }

    /// <summary>the task "EntrPortal": run there, then wait for the destination to be free and come out there</summary>
    void PortalUpdate(Unit u, double dt)
    {
        var t = u.Task;
        var b = t.Target as Building;
        var s = b?.Portal;
        if (b == null || s == null || !b.Alive || !CanEnterPortal(u, b)) { PortalQuit(u, b); return; }
        if (t.WaitT > 0) { t.WaitT -= dt; return; }
        double cx = b.Pos.X - u.Pos.X, cz = b.Pos.Z - u.Pos.Z;
        bool near = Math.Sqrt(cx * cx + cz * cz) < b.Radius + PORTAL_ENTER;
        if (!near)
        {
            // STATE_GOTO / STATE_ON_THE_WAY: AdvanceAction; not there when the walk ends: wait 0.8 s and again, 5 times
            if (!t.Walking) { var (x, z) = ApproachPoint(u, b); u.SetPath(x, z); t.Walking = true; }
            if (u.Path.Count > 0) { u.MoveAnim(u.Steer(dt, u.RunSpeed > 0 ? u.RunSpeed : u.Speed), true); return; }
            t.Walking = false;
            if (t.Tries-- <= 0) { PortalQuit(u, b); return; }
            t.WaitT = 0.8;
            return;
        }
        u.Path = new NavPath(); u.Vel = V3.Zero; t.Walking = false;
        // STATE_ENTER: CanTeleportIt -> the destination's IsFree
        var d = s.Dest;
        if (!s.Releases.Has(u) || d == null || !d.Alive || d.Portal == null || !Mutual(d.Owner, b.Owner) || !Mutual(u.Owner, b.Owner)) { PortalQuit(u, b); return; }
        if (!d.Built || d.Portal.OccupiedUntil > Time) { t.WaitT = PORTAL_RELEASE; u.Anim?.Play(u.IdleAnim); return; }
        d.Portal.OccupiedUntil = Time + PORTAL_RELEASE;
        s.Releases.Delete(u);
        var at = d.Portal.ReleasePos;
        Teleport(u, at.X, at.Z, d.Rot);
        Emit("portal", new Args { ["unit"] = u, ["from"] = b, ["to"] = d });
        // (Teleport ends the task) the destination's owner's units walk on to its rally point (GetRallyPosSimple)
        if (u.Owner == d.Owner && d.Rally is var (rx, rz)) Order(u, new Order("move") { X = rx, Z = rz });
    }

    /// <summary>the task ends (EndTask: UnregisterPassenger)</summary>
    void PortalQuit(Unit u, Building? b)
    {
        b?.Portal?.Releases.Delete(u);
        u.Task = UnitTask.Idle();
        u.Path = new NavPath(); u.Vel = V3.Zero;
        u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z;
    }
}
