// Water and naval gameplay (docs/spec/naval.md): fishing boats, harbours (fish delivery, the floating harbour's own
// fishing), water mines, the Aje torpedo turtle, the Ninigi submarine's stealth and the targeting rules of units
// that live in the water. Ships move on world.waterNav (see World.NavFor); harbours snap to the coast
// (construction.js coastalSnap).
//
// Mixed into World. Everything here is a no-op on maps without water.
//
// Ported from remake/src/game/systems/naval.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public abstract partial class Entity
{
    /// <summary>a mine that went off / an entity removed without a death (scripting removeEntity): not again</summary>
    public bool Blasted;
}

public sealed partial class Unit
{
    /// <summary>a water mine's fuse (s left; null: not lit)</summary>
    public double? Fuse;
    /// <summary>the torpedo turtle's life (s left; null: full) and its prey</summary>
    public double? Life;
    public Entity? Prey;
}

public sealed partial class Building
{
    /// <summary>a moving harbour under way: its path and speed (naval.js b.sail)</summary>
    public HarbourSail? Sail;
    /// <summary>the floating harbour's next fishing (s)</summary>
    public double FishT;
}

public sealed class HarbourSail
{
    public NavPath Path = new();
    public double Speed;
}

/// <summary>a mine / water turret to lay after the current one (naval.js t.next items)</summary>
public sealed class LayItem
{
    public ActionDef Action = null!;
    public string Name = "";
    public double X, Z;
}

public sealed partial class UnitTask
{
    /// <summary>fish: the storage was full this long (JS t.fullT); where it delivered last; the berth it sails to</summary>
    public double FishFullT;
    public (double X, double Z)? LastDrop;
    public (double X, double Z) DockAt;
    /// <summary>lay: the class to build (JS t.name), the ones after it (t.next), building time left (t.left), waiting for
    /// resources (t.waitT)</summary>
    public string? LayName;
    public List<LayItem>? LayNext;
    public double LayLeft, LayWaitT;
}

public sealed partial class World
{
    const double FISH_LOAD = 50;            // CFishingBoat inventory (Ship.usl:24-93)
    const double FISH_STOP = 7;             // one load per fishing stop: net out, 7 s, net in (Fishing.usl)
    const double FISH_SEARCH = 400;         // next shoal within 400 m of the last delivery
    const double MINE_SPACING = 8;          // PlaceWaterMine.usl: no mine / water turret within 8 m
    const double MINE_TRIGGER = 10;         // CWaterMine personal region
    const double MINE_FUSE = 0.5;
    static readonly double[] MINE_BLAST = { 15, 1000 };   // radius, flat damage
    const double TT_LIFE = 45, TT_SCAN = 2, TT_RADIUS = 100;       // TORPEDO_TURTLE
    static readonly double[] TT_BLAST = { 15, 250 };

    static readonly Regex FisherRe = new("fishing_boat$", RegexOptions.Compiled);
    static readonly Regex HarbourRe = new("harbour|carrier", RegexOptions.Compiled);
    static readonly Regex MineOrTurretRe = new("mineship_mine|water_turret", RegexOptions.Compiled);

    /// <summary>fishing boats: the only units that gather from fish shoals</summary>
    public static bool IsFisher(Unit? u) => u != null && FisherRe.IsMatch(u.Name);
    static bool IsHarbour(Entity? b) => b is Building && HarbourRe.IsMatch(b.Name) && !b.Name.Contains("rally_point");
    // units that fight only at the waterline (water melee: ram ship, cronosaurus, sea monsters) / below it (torpedo subs)
    static bool WaterMelee(Unit u) => u.Naval && !u.Amphib && u.Weapons.Long != null && u.Weapons.Long.Range < 1;
    static bool TorpedoUnit(Unit u) => u.Name.EndsWith("submarine", StringComparison.Ordinal);

    // ------------------------------------------------------------------ fishing (Fishing.usl, naval.md §4.2)
    /// <summary>a fishing boat goes fishing at a shoal (economy.js startGather of a water node, the computer player)</summary>
    public bool StartFishing(Unit u, ResNode? shoal)
    {
        if (!IsFisher(u) || shoal == null || !shoal.Alive || !shoal.Water) return false;
        ReleaseTask(u);
        shoal.Workers.Add(u);
        u.Task = new UnitTask("fish") { Node = shoal, Phase = "go", User = u.Task.User };
        u.SetPath(shoal.Pos.X, shoal.Pos.Z);
        return true;
    }

    void FishUpdate(Unit u, double dt)
    {
        var t = u.Task;
        double carried = u.Carry != null && u.Carry.Res == "food" ? u.Carry.Amount : 0;
        if (t.Phase == "go")
        {
            if (t.Node == null || !t.Node.Alive) { NextShoal(u, carried); return; }
            double d = Hypot(t.Node.Pos.X - u.Pos.X, t.Node.Pos.Z - u.Pos.Z);
            if (d > 6 + t.Node.Radius)
            {
                if (u.Path.Count == 0) { u.SetPath(t.Node.Pos.X, t.Node.Pos.Z); if (u.Path.Count == 0) { ToIdle(u); return; } }
                u.MoveAnim(u.Steer(dt, u.Speed), false);
                return;
            }
            u.Path.Clear(); u.Vel = V3.Zero;
            // net out: the load is filled at once, the boat waits 7 s
            double want = FISH_LOAD - carried;
            double got = Math.Min(want, t.Node.Amount);
            t.Node.Amount -= got;
            u.Carry = new Carry { Res = "food", Amount = carried + got };
            if (t.Node.Amount <= 0.5) DepleteNode(t.Node);
            t.Phase = "net"; t.Wait = FISH_STOP;
            u.Anim!.Play(u.Anim.Pick("work", "fishing", "standanim") ?? u.IdleAnim);
            return;
        }
        if (t.Phase == "net")
        {
            t.Wait -= dt;
            if (t.Wait > 0) return;
            t.Phase = "deliver"; t.Drop = null;
            return;
        }
        if (t.Phase == "deliver")
        {
            if (t.Drop == null || !t.Drop.Alive)
            {
                t.Drop = NearestFishDelivery(u);
                if (t.Drop == null) { ToIdle(u); return; }
                var (x, z) = DockPoint((Building)t.Drop);
                u.SetPath(x, z); t.DockAt = (x, z);
            }
            // arrived when close to the harbour (its footprint blocks the berth cells on the water grid)
            double sd = ((Building)t.Drop).SurfDist(u.Pos.X, u.Pos.Z);
            if (sd > 8 && !(sd < 20 && u.Path.Count == 0))
            {
                if (u.Path.Count == 0) { u.SetPath(t.DockAt.X, t.DockAt.Z); if (u.Path.Count == 0) { ToIdle(u); return; } }
                u.MoveAnim(u.Steer(dt, u.Speed), false);
                return;
            }
            u.Vel = V3.Zero;
            double left = Gain(u.Owner!, "food", carried);
            if (left >= carried - 1e-6 && carried > 0) { t.FishFullT += dt; return; }   // storage full: retry
            u.Carry = left > 0 ? new Carry { Res = "food", Amount = left } : null;
            t.LastDrop = (u.Pos.X, u.Pos.Z);
            if (u.Carry != null) return;
            if (t.Node != null && t.Node.Alive) { t.Phase = "go"; u.SetPath(t.Node.Pos.X, t.Node.Pos.Z); } else NextShoal(u, 0);
        }
    }

    void NextShoal(Unit u, double carried)
    {
        var t = u.Task;
        if (carried > 0) { t.Phase = "deliver"; t.Drop = null; return; }
        var from = t.LastDrop ?? (u.Pos.X, u.Pos.Z);
        var n = NearestResource(from.X, from.Z, "food", FISH_SEARCH, t.Node, null, water: true);
        if (n != null) { t.Node?.Workers.Delete(u); t.Node = n; n.Workers.Add(u); t.Phase = "go"; u.SetPath(n.Pos.X, n.Pos.Z); } else ToIdle(u);
    }

    Building? NearestFishDelivery(Unit u)
    {
        Building? best = null; double bd = 1e9;
        foreach (var b in Buildings)
        {
            if (!b.Alive || !b.Built || b.Owner != u.Owner || !IsHarbour(b)) continue;
            double d = u.DistTo(b);
            if (d < bd) { bd = d; best = b; }
        }
        return best;
    }

    /// <summary>berth of a harbour on the water grid (link Do_1, naval.md §1.3)</summary>
    public (double X, double Z) DockPoint(Building b)
    {
        var nav = WaterNav ?? Nav;
        foreach (var ln in new[] { "Do_1", "Spwn", "Ex_1" })
        {
            var p = b.LinkWorld(ln);
            if (p != null && nav.IsFree(p.Value.X, p.Value.Z)) return (p.Value.X, p.Value.Z);
        }
        int c = nav.NearestFree(nav.Idx(b.Pos.X, b.Pos.Z), 40);
        return nav.Center(c);
    }

    // ------------------------------------------------------------------ moving harbours (naval.md §1.5)
    // CSwimmingHarbour (aje_floating_harbour) and CSeasCarrier: buildings that swim. Right-click moves them once
    // built (Shift+right-click sets the rally point instead, GIC:273). While sailing they don't block the water grid;
    // where they stop, their footprint is stamped again.
    public bool IsMovingHarbour(Entity? b) => b is Building && (b.Def!.Script == "CSeasCarrier" || b.Def.Script == "CSwimmingHarbour");

    /// <summary>a moving harbour sails to (x, z); null = on its way, else the reason</summary>
    public string? MoveHarbour(Building b, double x, double z)
    {
        if (!IsMovingHarbour(b) || !b.Built || WaterNav == null || b.Dismantling != null) return "req";
        var nav = WaterNav;
        if (!nav.IsFree(x, z)) { var (cx, cz) = nav.Center(nav.NearestFree(nav.Idx(x, z), 30)); x = cx; z = cz; }
        bool was = b.Blocking;
        if (was) b.SetBlocking(false);
        var path = nav.Find(b.Pos.X, b.Pos.Z, x, z, Math.Min(4, b.Radius * 0.5), 40000, b.Owner);
        if (path == null || path.Count == 0) { if (was) b.SetBlocking(true); return "path"; }
        // speed: maxspeed index of the tech tree (2 = 5 m/s as for ships), a bit slower for the big hull
        double ms = b.Stats!.MaxSpeed != 0 ? b.Stats.MaxSpeed : b.Stats.Speed != 0 ? b.Stats.Speed : 2;
        int idx = (int)Math.Max(1, JsRound(ms));
        b.Sail = new HarbourSail { Path = path, Speed = new[] { 0, 1.5, 4, 5.5, 7 }[Math.Min(4, idx)] };
        b.Anim?.Play(b.Anim.Pick("swim_1", "swim_2", "standanim"), new AnimOpts { Fade = 0.4 });
        return null;
    }

    void HarbourSailUpdate(Building b, double dt)
    {
        var s = b.Sail;
        if (s == null) return;
        if (!b.Alive) { b.Sail = null; return; }
        double tx = s.Path[0].X, tz = s.Path[0].Z;
        double dx = tx - b.Pos.X, dz = tz - b.Pos.Z, d = Hypot(dx, dz);
        if (d < (s.Path.Count == 1 ? 1.5 : 6))
        {
            s.Path.RemoveAt(0);
            if (s.Path.Count == 0) { HarbourStop(b); return; }
            tx = s.Path[0].X; tz = s.Path[0].Z; dx = tx - b.Pos.X; dz = tz - b.Pos.Z; d = Hypot(dx, dz);
        }
        // turn towards the next point (slowly - it's a big hull), slow down while turning
        double want = Geo.HeadingTo(dx, dz), diff = Geo.Wrap(want - b.Rot);
        b.Rot = Geo.Wrap(b.Rot + Math.Sign(diff) * Math.Min(Math.Abs(diff), 0.5 * dt));
        double sp = s.Speed * Math.Max(0.15, SMath.Cos(Math.Min(Math.Abs(diff), Math.PI / 2)));
        double step = Math.Min(d, sp * dt);
        b.Slide(-SMath.Sin(b.Rot) * step, -SMath.Cos(b.Rot) * step, b.Rot);
        BHash.Move(b);
    }

    void HarbourStop(Building b)
    {
        b.Sail = null;
        b.Relocate();
        if (!b.Blocking) b.SetBlocking(true);
        b.Anim?.Play(b.Anim.Pick("standanim", "swim_1"), new AnimOpts { Fade = 0.5 });
    }

    // ------------------------------------------------------------------ harbours (naval.md §1.3 / §4.3)
    partial void HarbourUpdate(Building b, double dt)
    {
        if (b.Sail != null) HarbourSailUpdate(b, dt);
        // Aje floating harbour: fishes a shoal within 40 m by itself, 10 food every 5 s straight into the stock
        if (b.Name != "aje_floating_harbour" || !b.Built || b.Owner == null) return;
        b.FishT -= dt;
        if (b.FishT > 0) return;
        b.FishT = 5;
        var n = NearestResource(b.Pos.X, b.Pos.Z, "food", b.Radius + 40, null, null, water: true);
        if (n == null) return;
        double got = Math.Min(10, n.Amount);
        double left = Gain(b.Owner, "food", got);
        n.Amount -= got - left;
        if (n.Amount <= 0.5) DepleteNode(n);
    }

    // ------------------------------------------------------------------ targeting rules of sea units (FO:4792-4842)
    /// <summary>can attacker u fight target e at all, given water and land? (combat.js canTarget asks it)</summary>
    public bool NavalCanTarget(Entity u, Entity e)
    {
        if (WaterLevel == null) return true;
        double wl = WaterLevel.Value;
        double ground = e is Building ? 0 : Height(e.Pos.X, e.Pos.Z);
        var uu = u as Unit; var eu = e as Unit;
        // land melee units can't reach anything in the water
        if ((uu == null || (!uu.Naval && !uu.Amphib)) && eu != null && (eu.Naval || ground < wl - 0.5))
        {
            double r = u.Weapons.Long != null ? u.Weapons.Long.Range : 0;
            if (r < 1) return false;
        }
        if (uu != null && WaterMelee(uu))
        {
            if (e is Building) return IsHarbour(e) || Height(e.Pos.X, e.Pos.Z) <= wl + 1.7;
            return ground <= wl + 1.7;
        }
        if (uu != null && TorpedoUnit(uu))
        {
            if (e is Building) return IsHarbour(e);
            return ground < wl - 0.5 || (eu != null && eu.Naval);
        }
        // wild sea monsters hunt only ships and buildings
        if (uu != null && uu.Wild && uu.Naval && eu != null && !eu.Naval) return false;
        return true;
    }

    // ------------------------------------------------------------------ water mines (CWaterMine, PlaceWaterMine.usl)
    string? PlaceWaterThing(Unit u, string name, double x, double z)
    {
        if (WaterLevel == null || Height(x, z) > WaterLevel.Value) return "place";
        foreach (var o in Units) if (o.Alive && MineOrTurretRe.IsMatch(o.Name) && Hypot(o.Pos.X - x, o.Pos.Z - z) < MINE_SPACING) return "place";
        return null;
    }

    void MineUpdate(Unit m, double dt)
    {
        if (m.Fuse != null) { m.Fuse -= dt; if (m.Fuse <= 0) MineBlast(m); return; }
        m.ScanT -= dt;
        if (m.ScanT > 0) return;
        m.ScanT = 0.25;
        UHash.Query(m.Pos.X, m.Pos.Z, MINE_TRIGGER + 4, e =>
        {
            if (m.Fuse != null || !e.Alive || e == m || e.Cls != "SHIP" || e.Owner == null || m.Owner == null || !m.Owner.IsEnemy(e.Owner)) return;
            if (Hypot(e.Pos.X - m.Pos.X, e.Pos.Z - m.Pos.Z) < MINE_TRIGGER + e.Radius) m.Fuse = MINE_FUSE;
        });
    }

    /// <summary>a mine goes off (also when it is destroyed: combat.js kill)</summary>
    public void MineBlast(Unit m)
    {
        if (m.Blasted) return;
        m.Blasted = true;
        FlatBlast(m, m.Pos, MINE_BLAST[0], MINE_BLAST[1]);
        if (m.Alive) { m.SilentDeath = true; Kill(m, null); }
    }

    // flat area damage (armour applies via takeDirectDmg's protection flag), no friendly fire, passengers are safe
    void FlatBlast(Entity src, V3 pos, double r, double dmg)
    {
        Emit("impact", new Args { ["pos"] = pos, ["splash"] = true, ["big"] = true });
        void Hit(Entity e)
        {
            if (!e.Alive || e == src || e.Inside) return;
            if (e.Owner != null && src.Owner != null && !src.Owner.IsEnemy(e.Owner)) return;
            if (Hypot(e.Pos.X - pos.X, e.Pos.Z - pos.Z) - (e.Radius != 0 ? e.Radius : 1) > r) return;
            TakeDirectDmg(e, dmg, 0, src.Owner, true);
        }
        UHash.Query(pos.X, pos.Z, r + 10, Hit);
        BHash.Query(pos.X, pos.Z, r + 30, Hit);
    }

    // ------------------------------------------------------------------ Aje torpedo turtle (CTorpedoTurtle, naval.md §2.6)
    bool TorpedoTurtleUpdate(Unit u, double dt)
    {
        u.Life = (u.Life ?? TT_LIFE) - dt;
        if (u.Life <= 0) { Kill(u, null); return true; }
        u.ScanT -= dt;
        if (u.ScanT <= 0)
        {
            u.ScanT = TT_SCAN;
            Entity? best = null; double bd = TT_RADIUS;
            void Test(Entity e)
            {
                if (!e.Alive || e.Owner == null || u.Owner == null || !u.Owner.IsEnemy(e.Owner)) return;
                if (!(e.Cls == "SHIP" || (IsHarbour(e) && !e.Name.Contains("carrier")))) return;
                double d = Hypot(e.Pos.X - u.Pos.X, e.Pos.Z - u.Pos.Z);
                if (d < bd) { bd = d; best = e; }
            }
            UHash.Query(u.Pos.X, u.Pos.Z, TT_RADIUS, Test);
            BHash.Query(u.Pos.X, u.Pos.Z, TT_RADIUS + 20, Test);
            if (best != null && best != u.Prey) { u.Prey = best; u.SetPath(best.Pos.X, best.Pos.Z); }
            else if (best == null && u.Path.Count == 0)
            {
                double a = u.Heading + ((Math.Floor(Rng.Next() * 4) - 2) * 0.3);
                u.SetPath(u.Pos.X - SMath.Sin(a) * 25, u.Pos.Z - SMath.Cos(a) * 25);
            }
        }
        var p = u.Prey;
        if (p != null && p.Alive && Hypot(p.Pos.X - u.Pos.X, p.Pos.Z - u.Pos.Z) < (p.Radius != 0 ? p.Radius : 2) + u.Radius + 0.5)
        {
            FlatBlast(u, u.Pos, TT_BLAST[0], TT_BLAST[1]);
            Emit("camshake", new Args { ["cls"] = "CVehicleExplosion", ["pos"] = u.Pos });      // (Ship.usl:1540 Explode: server event 15)
            u.SilentDeath = true; Kill(u, null);
            return true;
        }
        if (p != null && p.Alive && (u.Path.Count == 0 || (u.RepathT -= dt) <= 0)) { u.SetPath(p.Pos.X, p.Pos.Z); u.RepathT = 1; }
        u.MoveAnim(u.Steer(dt, u.Speed), false);
        return true;
    }

    /// <summary>minelayer / corsair: build a water mine / water turret at a point in the water (PlaceWaterMine.usl); null =
    /// done, else the reason. queue: after the ones it has been given already (shift; the computer player's mine belts)</summary>
    public string? LayWaterThing(Unit ship, ActionDef a, double x, double z, bool queue) => LayWaterThing(ship, a, x, z, queue, false);

    string? LayWaterThing(Unit u, ActionDef action, double x, double z, bool queue, bool queued)
    {
        var name = action.Results.Count > 0 ? action.Results[0].Obj : null;
        if (string.IsNullOrEmpty(name) || !u.Alive) return "req";
        var why = Data.Check(u.RulesOwner(), action, u);
        if (why != null) return why == "hidden" || why == "disabled" ? "req" : why;
        if (!queued && !u.Owner!.CanAfford(action.Cost)) return "cost";      // (a queued one pays when it is there)
        var w = PlaceWaterThing(u, name, x, z);
        if (w != null) return w;
        if (queue && u.Task.Type == "lay")
        {
            var t = u.Task;
            if (Hypot(t.X - x, t.Z - z) < MINE_SPACING || t.LayNext!.Any(q => Hypot(q.X - x, q.Z - z) < MINE_SPACING)) return "place";
            t.LayNext!.Add(new LayItem { Action = action, Name = name, X = x, Z = z });
            return null;
        }
        ReleaseTask(u);
        u.Task = new UnitTask("lay") { Action = action, LayName = name, X = x, Z = z, Phase = "go", User = true, LayNext = new List<LayItem>() };
        u.SetPath(x, z);
        return null;
    }

    // the next of the queued ones, or idle
    void LayNext(Unit u)
    {
        var q = u.Task.LayNext ?? new List<LayItem>();
        ToIdle(u);
        while (q.Count > 0)
        {
            var n = q[0]; q.RemoveAt(0);
            if (LayWaterThing(u, n.Action, n.X, n.Z, false, true) != null) continue;
            u.Task.LayNext = q;
            return;
        }
    }

    void LayUpdate(Unit u, double dt)
    {
        var t = u.Task;
        if (t.Phase == "go")
        {
            double d = Hypot(t.X - u.Pos.X, t.Z - u.Pos.Z);
            if (d > u.Radius + 4 && u.Path.Count > 0) { u.MoveAnim(u.Steer(dt, u.RunSpeed), false); return; }
            u.Path.Clear(); u.Vel = V3.Zero;
            // (a path that ended short of the spot: it is not laid out of reach)
            if (d > u.Radius + 10 || PlaceWaterThing(u, t.LayName!, t.X, t.Z) != null) { LayNext(u); return; }
            // not enough wood and stone: it waits there for them a minute, then goes on to the next
            if (!u.Owner!.CanAfford(t.Action!.Cost)) { if ((t.LayWaitT += dt) > 60) LayNext(u); return; }
            u.Owner.Pay(t.Action.Cost);
            t.Phase = "build"; t.LayLeft = Math.Max(0.5, t.Action.Time != 0 ? t.Action.Time : 5);
            return;
        }
        t.LayLeft -= dt;
        if (t.LayLeft > 0) return;
        var m = SpawnUnit(t.LayName!, u.Owner, t.X, t.Z, 1, u.Heading, new UnitOpts { Stationary = true });
        if (m != null) { m.Stance = t.LayName!.Contains("mine") ? 3 : 0; Emit("trained", new Args { ["unit"] = m, ["producer"] = u }); }
        LayNext(u);
    }

    // ------------------------------------------------------------------ per-unit hook (called from World.UnitUpdate)
    /// <summary>the water's part of the unit's step; true when the naval rules handled the unit this tick</summary>
    public bool NavalUnitUpdate(Unit u, double dt)
    {
        if (u.Name == "ninigi_mineship_mine") { MineUpdate(u, dt); return true; }
        if (u.Name == "aje_torpedo_turtle") return TorpedoTurtleUpdate(u, dt);
        // Ninigi muraeno submarine: hidden unless fighting (and for 10 s after the last fight / hit)
        if (u.Name == "ninigi_muraeno_submarine")
        {
            if (u.Task.Type == "attack") { if (u.St.Camo.Has("disg")) u.St.Camo.Delete("disg"); u.DisgT = Time + 10; }
            else if (!u.St.Camo.Has("disg") && Time > u.DisgT) u.St.Camo.Add("disg");
        }
        if (u.Task.Type == "fish") { FishUpdate(u, dt); return true; }
        if (u.Task.Type == "lay") { LayUpdate(u, dt); return true; }
        return false;
    }
}
