// Transports and garrisons: the Hu bunker, closed transports (steam tank, hovercraft, siege tower, turtle) and
// open ones where passengers ride on the animal and keep shooting (triceratops, saltasaurus, rhino, chariot ...).
//
// Reference: docs/spec/units.md section 4 (TransportObj.usl, BoardTransport.usl, CBunker in Building.usl).
//   transport class 1 (land) carries characters only; class 2 (ships, hovercraft, turtle) also animals and vehicles.
//   Passengers can't be hit. When a land transport dies they get out; on a sinking ship they die.
//
// Ported from remake/src/game/systems/transport.js (pw-exporter 98c7703).
// C# notes: the JS's u.inside is the transport; here Entity.Inside (bool, the core's) and Unit.InsideOf (the
// transport). Passengers of a building (the bunker) are Building.Passengers. The hooks LeaveTransport(u) and
// UnloadAll(u, dying) of world.js are private to World; the public methods are the overloads with the exit point /
// any entity.
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public sealed partial class Unit
{
    /// <summary>the transport (unit or bunker) the unit is in (JS u.inside); null outside</summary>
    public Entity? InsideOf;
}

public sealed partial class Building
{
    /// <summary>the garrison of a bunker (transport.js t.passengers)</summary>
    public List<Unit>? Passengers;
}

public sealed partial class UnitTask
{
    /// <summary>board: boarding a ship - time to the next look for the shore, the shore point (JS tk.shipT, tk.shore)</summary>
    public double ShipT;
    public (double X, double Z)? Shore;
    /// <summary>board: it has walked to the transport (JS u.task.walked)</summary>
    public bool Walked;
}

public sealed partial class World
{
    static readonly HashSet<string> OPEN = new(StringComparer.Ordinal) { "hu_chariot", "hu_rhino_transporter", "hu_triceratops", "aje_triceratops_archer", "aje_brachiosaurus",
        "aje_stegosaurus", "ninigi_saltasaurus_archer", "seas_triceratops_transporter" };

    /// <summary>the passengers of a transport or bunker (null: it has had none)</summary>
    public List<Unit>? PassengersOf(Entity t) => t is Unit u ? u.Passengers : t is Building b ? b.Passengers : null;
    void SetPassengers(Entity t, List<Unit> l) { if (t is Unit u) u.Passengers = l; else if (t is Building b) b.Passengers = l; }

    public double Capacity(Entity t)
    {
        if (t is Building) return t.Def!.Script == "CBunker" ? 4 : 0;
        double c = t.Stats?.MaxPassengers ?? 0;
        return c != 0 ? c : t.Def?.MaxPassengers ?? 0;
    }
    public bool IsOpenTransport(Entity t) => OPEN.Contains(t.Name);

    /// <summary>may u board t (replaces the core's stub)</summary>
    public bool CanBoard(Unit u, Entity? t)
    {
        if (t == null || !u.Alive || !t.Alive || u == t || u.Owner != t.Owner || u.Inside || (u.Passengers != null && u.Passengers.Count > 0)) return false;
        if (u.Flying || t.St.Active("cursed", Time)) return false;   // (MIRAGE: jetpack flight; the monk's curse, TransportObj.usl:1313)
        if (IsStatue(u)) return false;   // (the original's BoardTransport / EnterBunker: no statue boards)
        double cap = Capacity(t);
        if (cap == 0 || (PassengersOf(t)?.Count ?? 0) >= cap) return false;
        if (t is Building b) return b.Built && u.Cls == "CHTR";
        int cls = t.Def!.TransportClass != 0 ? t.Def.TransportClass : 1;
        if (cls == 1) return u.Cls == "CHTR";
        return u.Cls != "SHIP" && !(u.Def!.TransportClass >= 2);
    }

    /// <summary>nearest walkable land point within r of a ship (GetShipBoardingPos [?engine], naval.md §2.5), or null</summary>
    public (double X, double Z)? ShorePoint(Entity t, double r = 30, V3? near = null)
    {
        var nav = Nav; var from = near ?? t.Pos;
        int c = nav.NearestFree(nav.Idx(from.X, from.Z), (int)Math.Ceiling((r + 20) / nav.Cell));
        if (nav.Block[c] != 0) return null;
        var (x, z) = nav.Center(c);
        return Hypot(x - t.Pos.X, z - t.Pos.Z) <= r + t.Radius ? (x, z) : null;
    }

    // boarding a ship: the passenger walks to the shore next to the ship, the ship comes to the shore near the passenger
    void BoardShipUpdate(Unit u, Unit t, double dt)
    {
        var tk = u.Task;
        tk.ShipT -= dt;
        if (tk.ShipT <= 0)
        {
            tk.ShipT = 1.5;
            // bring an idle ship to the waterline closest to the passenger
            if ((t.Task.Type == "idle" || t.Task.Type == "hold") && Hypot(t.Pos.X - u.Pos.X, t.Pos.Z - u.Pos.Z) > 25 && WaterNav != null)
            {
                var wn = WaterNav; int c = wn.NearestFree(wn.Idx(u.Pos.X, u.Pos.Z), 30);
                if (wn.Block[c] == 0) { var (x, z) = wn.Center(c); Order(t, new Order("move") { X = x, Z = z, Auto = true }); }
            }
            tk.Shore = ShorePoint(t, 30, u.Pos) ?? ShorePoint(t, 30);
            if (tk.Shore is { } sh) u.SetPath(sh.X, sh.Z);
        }
        double dShip = Hypot(t.Pos.X - u.Pos.X, t.Pos.Z - u.Pos.Z) - t.Radius;
        bool atShore = tk.Shore is { } s && Hypot(s.X - u.Pos.X, s.Z - u.Pos.Z) < 3.5;
        if (dShip < 30 && (atShore || dShip < 3)) { EnterTransport(u, t); return; }
        if (u.Path.Count > 0) u.MoveAnim(u.Steer(dt, u.RunSpeed, 0.5), false); else { u.Vel = V3.Zero; u.Anim!.Play(u.StandAnim()); }
    }

    partial void BoardUpdate(Unit u, double dt)
    {
        var t = u.Task.Target;
        if (t == null || !CanBoard(u, t)) { u.Task = UnitTask.Idle(); return; }
        if (t is Unit tu && tu.Naval) { BoardShipUpdate(u, tu, dt); return; }
        double d = t is Building b ? b.SurfDist(u.Pos.X, u.Pos.Z) : u.DistTo(t) - t.Radius;
        // an amphibious transport (the SEAS hovercraft, the Aje's turtle) waiting in the water or on another island: nobody
        // on foot reaches it there - an idle one comes ashore to the passenger, as a ship comes to the waterline
        // (BoardShipUpdate)
        if (t is Unit au && au.Amphib && d > 6)
        {
            u.Task.ShipT -= dt;
            if (u.Task.ShipT <= 0)
            {
                u.Task.ShipT = 1.5;
                if ((au.Task.Type == "idle" || au.Task.Type == "hold") && (IsWater(au.Pos.X, au.Pos.Z) || LandIsland(au.Pos.X, au.Pos.Z) != LandIsland(u.Pos.X, u.Pos.Z))) Order(au, new Order("move") { X = u.Pos.X, Z = u.Pos.Z, Auto = true });
            }
        }
        // close enough: 2.5 m from the wall, or standing at the approach point (which can be a little farther)
        if (d > 2.5 && !(d < 4 && u.Path.Count == 0 && u.Task.Walked))
        {
            u.Task.Walked = true;
            u.RepathT -= dt;
            if (u.RepathT <= 0 || u.Path.Count == 0) { var (x, z) = t is Building ? ApproachPoint(u, t) : (t.Pos.X, t.Pos.Z); u.SetPath(x, z); u.RepathT = 1; }
            double sp = u.Steer(dt, u.RunSpeed, 0.5); u.MoveAnim(sp, false);
            return;
        }
        EnterTransport(u, t);
    }

    /// <summary>u gets into t (also the computer player's and the missions' direct boarding)</summary>
    public void EnterTransport(Unit u, Entity t)
    {
        ReleaseTask(u);
        DropStates(u);
        var pax = PassengersOf(t);
        if (pax == null) { pax = new List<Unit>(); SetPassengers(t, pax); }
        pax.Add(u);
        u.Inside = true; u.InsideOf = t;
        u.Task = new UnitTask("inside");
        u.Path.Clear(); u.Vel = V3.Zero;
        UHash.Remove(u);
        u.LookChanged();                                          // (hidden: the view draws no unit inside)
        if (HasItemCarried(u)) RideCup(u, true);                  // (the ride cup: the transport's hit points, systems/items.js)
        Emit("boarded", new Args { ["unit"] = u, ["transport"] = t });
    }

    partial void LeaveTransport(Unit u) => LeaveTransport(u, null, null);
    /// <summary>u gets out of its transport at (x, z) (null: next to it)</summary>
    public void LeaveTransport(Unit u, double? x0, double? z0)
    {
        var t = u.InsideOf;
        if (t == null) return;
        var pax = PassengersOf(t);
        if (pax != null) SetPassengers(t, pax.FindAll(p => p != u));
        if (HasItemCarried(u)) RideCup(u, false);
        u.Inside = false; u.InsideOf = null;
        double x, z;
        if (x0 == null || z0 == null)
        {
            if (t is Building b) (x, z) = b.ExitPoint(t.Pos.X + 5, t.Pos.Z + 5);
            else { x = t.Pos.X + (Rng.Next() - 0.5) * 6; z = t.Pos.Z + (Rng.Next() - 0.5) * 6; }
        }
        else { x = x0.Value; z = z0.Value; }
        if (!Nav.IsFree(x, z)) { int c = Nav.NearestFree(Nav.Idx(x, z), 8); (x, z) = Nav.Center(c); }
        u.Pos = new V3(x, Height(x, z), z);
        if (u.Alive) { UHash.Insert(u); u.Task = UnitTask.Idle(); u.AnchorX = x; u.AnchorZ = z; u.LookChanged(); }
    }

    partial void UnloadAll(Unit u, bool here) => UnloadAll((Entity)u, here);
    /// <summary>everybody out (/DismountAll); on death of a ship the passengers die with it</summary>
    public void UnloadAll(Entity t, bool dying)
    {
        var list = new List<Unit>(PassengersOf(t) ?? new List<Unit>());
        bool naval = t is Unit tu && tu.Naval;
        // ships unload onto the shore within 30 m; on a sinking ship whoever can't reach the shore dies (Ship.usl:248)
        var shore = naval ? ShorePoint(t, 30) : null;
        if (naval && shore == null && !dying) { Emit("msg", new Args { ["player"] = t.Owner, ["text"] = "Too far from the shore to unload" }); return; }
        // a siege tower docked at a wall: out on the wall's far side (CLeaveSiegeTower, systems/siege.js)
        var far = t is Unit dt && dt.Docked != null ? SiegeExitPoint(dt) : null;
        for (int i = 0; i < list.Count; i++)
        {
            var u = list[i];
            if (naval && shore == null) { LeaveTransport(u, null, null); u.SilentDeath = true; Kill(u, null); continue; }
            // a flying zeppelin: its passengers die with it (MIRAGE CZeppelin.Die: m_bFlying -> AllGotDrowned)
            if (dying && t is Unit zt && zt.Flyer is { Kind: "zeppelin" }) { LeaveTransport(u, null, null); u.SilentDeath = true; Kill(u, null); continue; }
            double a = (double)i / Math.Max(1, list.Count) * Math.PI * 2;
            if (far is { } f) { LeaveTransport(u, f.X + SMath.Cos(a) * 1.5, f.Z + SMath.Sin(a) * 1.5); continue; }
            double r = naval ? 1.5 + (i % 3) * 1.2 : (t.Radius != 0 ? t.Radius : 2) + 2;
            var (cx, cz) = shore ?? (t.Pos.X, t.Pos.Z);
            LeaveTransport(u, cx + SMath.Cos(a) * r, cz + SMath.Sin(a) * r);
        }
        SetPassengers(t, new List<Unit>());
    }

    /// <summary>one passenger out (the passengers' menu: CommandBar.usl SLOT flyout, "/Unboard|&lt;slot&gt;|&lt;guid&gt;"); ships
    /// only at the shore, like UnloadAll</summary>
    public bool UnloadOne(Entity t, Unit u)
    {
        var pax = PassengersOf(t);
        if (pax == null || !pax.Contains(u)) return false;
        if (t is Building) { LeaveTransport(u, null, null); return true; }
        var far = t is Unit dt && dt.Docked != null ? SiegeExitPoint(dt) : null;
        if (far is { } f) { LeaveTransport(u, f.X, f.Z); return true; }
        bool naval = t is Unit tu && tu.Naval;
        var shore = naval ? ShorePoint(t, 30) : null;
        if (naval && shore == null) { Emit("msg", new Args { ["player"] = t.Owner, ["text"] = "Too far from the shore to unload" }); return false; }
        var (cx, cz) = shore ?? (t.Pos.X, t.Pos.Z);
        double a = Rng.Next() * Math.PI * 2, r = naval ? 1.5 : (t.Radius != 0 ? t.Radius : 2) + 2;
        LeaveTransport(u, cx + SMath.Cos(a) * r, cz + SMath.Sin(a) * r);
        return true;
    }

    // passengers of open transports shoot with their long-range weapon from the animal's back
    partial void PassengerUpdate(Unit u, double dt)
    {
        var t = u.InsideOf;
        if (t == null || !t.Alive) return;
        u.Pos = t.Pos;
        if (!IsOpenTransport(t) || u.Weapons.Long == null || u.Weapons.Long.Projectile == null) return;
        var cs = u.Cs(u.Weapons.Long);
        double R = AttackRangeOf(u, cs) + t.Radius;
        var e = u.Task.Target;
        if (e == null || !CanTarget(u, e) || Reach(t, e) > R)
        {
            var tt = t as Unit;
            e = tt != null && tt.Task.Type == "attack" && tt.Task.Target != null && CanTarget(u, tt.Task.Target) && Reach(t, tt.Task.Target) <= R ? tt.Task.Target
                : tt != null ? FindTarget(tt, R, FindOpts.Around) : null;   // (for the passenger's bow, not the carrier's weapon)
            u.Task.Target = e;
        }
        if (e == null || cs == null || Time - u.LastHitDone <= AttackDuration(u, cs)) return;      // (no long weapon: nothing to launch)
        u.LastHitDone = Time;
        var from = t.Pos; from.Y += (t is Unit h && h.Height != 0 ? h.Height : 3) * 0.9;
        Launch(u, from, e, u.Weapons.Long, cs);
    }

    // u.item: systems/items.js
    bool HasItemCarried(Unit u) => u.Item != null;
}
