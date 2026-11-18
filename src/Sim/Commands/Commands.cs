// The concrete commands: what the interface of the JS remake did directly (main.js G.*, ui/input.js), as data.
// Only the player's own entities obey (a command for another player's units does nothing).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Commands;

static class Cmd
{
    public static Player? PlayerOf(W_ w, int id) => w.Players.FirstOrDefault(p => p.Id == id);
    /// <summary>the alive entities of the ids that belong to the player</summary>
    public static List<T> Own<T>(W_ w, int player, IEnumerable<int> ids) where T : Entity
    {
        var res = new List<T>();
        foreach (var id in ids) if (w.ById(id) is T e && e.Alive && e.Owner != null && e.Owner.Id == player) res.Add(e);
        return res;
    }
    /// <summary>an action of the player's tree by its path (/Actions/&lt;tribe&gt;/&lt;kind&gt;/&lt;cat&gt;/&lt;id&gt;), or one an entity offers</summary>
    public static ActionDef? Action(W_ w, Player p, string path, Entity? at = null)
    {
        if (at != null)
        {
            var o = at.RulesOwner();
            var a = w.Data.ActionsOf(o, at.Name, at.Kind, at.Def?.Type ?? at.Cls).FirstOrDefault(x => x.Path == path);
            if (a != null) return a;
        }
        return w.Data.Actions(p).FirstOrDefault(x => x.Path == path);
    }
}

/// <summary>An order to units (G.order / World.Order): move, attackmove, attack, stop, hold, gather, build, repair,
/// deliver, heal, board, dock, fountain, pickup, use, climb, unload, trade, patrol. Queue (Shift): after the walks the
/// unit has (World.QueueOrder).</summary>
public sealed class OrderCommand : Command
{
    public int[] Units = Array.Empty<int>();
    public string Type = "move";
    public double X, Z;
    public int Target;
    public bool Queue, Force, Slow;
    public List<(double X, double Z)>? Pts;
    public int I;

    public override string? Apply(W_ w)
    {
        var units = Cmd.Own<Unit>(w, Player, Units);
        if (units.Count == 0) return "none";
        var t = Target != 0 ? w.ById(Target) : null;
        if (Target != 0 && (t == null || !t.Alive) && Type != "move") return "target";
        foreach (var u in units)
        {
            var o = new Order(Type) { X = X, Z = Z, Target = t, Force = Force, Slow = Slow, Pts = Pts, I = I };
            if (Queue && w.QueueOrder(u, o)) continue;
            w.Order(u, o);
        }
        return null;
    }
}

/// <summary>
/// A group order to a point in formation (ui/input.js moveGroup, game/formation.js): Dir / Spacing from the arrow the
/// player drew, else facing from the group to the point; Queue (Shift): after the orders it has; Patrol (Ctrl): to and
/// fro from where each unit is. The units of one walk share a group id (the first to reach a waypoint takes the
/// others on: World.PullGroup).
/// </summary>
public sealed class GroupMoveCommand : Command
{
    public int[] Units = Array.Empty<int>();
    /// <summary>move | attackmove</summary>
    public string Type = "move";
    public double X, Z;
    public double? Dir, Spacing;
    public bool Queue, Patrol;

    public override string? Apply(W_ w)
    {
        var units = Cmd.Own<Unit>(w, Player, Units).Where(u => !u.Inside).ToList();
        int n = units.Count;
        if (n == 0) return "none";
        List<(Unit U, double X, double Z)> slots;
        if (n == 1) slots = new() { (units[0], X, Z) };
        else
        {
            // facing from where the group will be: its last queued waypoint (Shift) or where it stands
            double cx = 0, cz = 0;
            foreach (var u in units) { var (qx, qz) = Queue ? LastWaypoint(u) : (u.Pos.X, u.Pos.Z); cx += qx; cz += qz; }
            cx /= n; cz /= n;
            slots = Formation.FormationSlots(w, units, X, Z, Dir ?? SMath.Atan2(Z - cz, X - cx), Spacing ?? Formation.SPACING);
        }
        // one walk of the group: the first one to reach a waypoint takes the others on to the next (World.PullGroup)
        int wp = n > 1 ? w.NextWalkGroup() : 0;
        foreach (var (u, px, pz) in slots)
        {
            var o = Patrol ? new Order("patrol") { Pts = new() { (u.Pos.X, u.Pos.Z), (px, pz) }, I = 1 } : new Order(Type) { X = px, Z = pz, Wp = wp };
            if (Queue && w.QueueOrder(u, o)) continue;
            w.Order(u, o);
        }
        return null;
    }

    /// <summary>where a unit's walks end (ui/input.js lastWaypoint)</summary>
    public static (double X, double Z) LastWaypoint(Unit u)
    {
        var t = u.Task;
        if (t.Type != "move" && t.Type != "attackmove" && !(t.Type == "attack" && t.Amove != null)) return (u.Pos.X, u.Pos.Z);
        if (t.Then == null || t.Then.Type == "patrol") return t.Amove != null ? (t.Amove[0], t.Amove[1]) : (t.X, t.Z);
        var o = t.Then;
        while (o.Then != null && o.Then.Type != "patrol") o = o.Then;
        return (o.X, o.Z);
    }
}

/// <summary>A producer's build / upgrade action (G.useAction for producers: queued Count times, Shift = 5). Also: the
/// rest of the selection - an upgrade of the object itself (a local result: the Dustriders' clay wall, the SEAS wall, the
/// farm modes) goes to every selected object of the producer's class that offers it, each paying its own price, as the
/// original sends an action to every selected object; anything else only to the producer.</summary>
public sealed class QueueActionCommand : Command
{
    public int Producer;
    public string Action = "";
    public int Count = 1;
    public int[]? Also;

    public override string? Apply(W_ w)
    {
        var p = Cmd.Own<Entity>(w, Player, new[] { Producer }).FirstOrDefault();
        if (p == null) return "none";
        var a = Cmd.Action(w, p.Owner!, Action, p);
        if (a == null) return "req";
        var first = Queue(w, p, a);
        if (Also == null || Also.Length == 0 || a.Kind != "Upgrades" || !a.Results.Any(r => r.Cls == "local")) return first;
        bool any = first == null;
        foreach (var e in Cmd.Own<Entity>(w, Player, Also))
        {
            if (e == p || e.Name != p.Name) continue;
            var ea = Cmd.Action(w, e.Owner!, Action, e);
            if (ea != null && Queue(w, e, ea) == null) any = true;
        }
        return any ? null : first;
    }

    string? Queue(W_ w, Entity e, ActionDef a)
    {
        for (int i = 0; i < Count; i++)
        {
            var why = w.QueueAction(e, a);
            if (why != null) return i == 0 ? why : null;
        }
        return null;
    }
}

/// <summary>Cancels the last queue entry of an action at a producer (G.unqueue).</summary>
public sealed class UnqueueCommand : Command
{
    public int Producer;
    public string ActionId = "";

    public override string? Apply(W_ w)
    {
        var e = Cmd.Own<Entity>(w, Player, new[] { Producer }).FirstOrDefault();
        if (e == null) return "none";
        for (int i = e.Queue.Count - 1; i >= 0; i--) if (e.Queue[i].Action.Id == ActionId) { w.CancelQueue(e, i); return null; }
        return "none";
    }
}

/// <summary>A building site (G.placeBuilding): the workers go to build it.</summary>
public sealed class PlaceBuildingCommand : Command
{
    public string Action = "";
    public double X, Z, Rot;
    public int[] Workers = Array.Empty<int>();

    public override string? Apply(W_ w)
    {
        var p = Cmd.PlayerOf(w, Player);
        if (p == null) return "none";
        var a = Cmd.Action(w, p, Action);
        if (a == null) return "req";
        var r = w.StartConstruction(p, a, X, Z, Rot, Cmd.Own<Unit>(w, Player, Workers));
        return r as string;
    }
}

/// <summary>A wall line (G.placeWall: tiles from the wall placer; Queued: after the piece the workers build now).</summary>
public sealed class PlaceWallCommand : Command
{
    public string Action = "";
    public List<(int I, int J, double X, double Z, bool Have)> Tiles = new();
    public int[] Workers = Array.Empty<int>();
    public bool Queued;

    public override string? Apply(W_ w)
    {
        var p = Cmd.PlayerOf(w, Player);
        if (p == null) return "none";
        var a = Cmd.Action(w, p, Action);
        if (a == null) return "req";
        return w.PlaceWall(p, a, Tiles, Cmd.Own<Unit>(w, Player, Workers), Queued) > 0 ? null : "place";
    }
}

/// <summary>Moves a unit to another level of the army pyramid (G.changeLevel).</summary>
public sealed class ChangeLevelCommand : Command
{
    public int Unit;
    public int Level;
    public override string? Apply(W_ w) { var u = Cmd.Own<Unit>(w, Player, new[] { Unit }).FirstOrDefault(); return u == null ? "none" : w.ChangeLevel(u, Level); }
}

/// <summary>Levels a unit up with skulls (G.levelUp, Ctrl+U).</summary>
public sealed class LevelUpCommand : Command
{
    public int Unit;
    public override string? Apply(W_ w) { var u = Cmd.Own<Unit>(w, Player, new[] { Unit }).FirstOrDefault(); return u == null ? "none" : w.LevelUp(u); }
}

/// <summary>A special move or building command of the casters (G.useAction for moves; input.js target mode): Target an
/// entity, or a ground point (HasGround); one caster uses it unless the move is a "self" move.</summary>
public sealed class UseMoveCommand : Command
{
    public int[] Casters = Array.Empty<int>();
    public string ActionId = "";
    public int Target;
    public bool HasGround;
    public double X, Z;
    /// <summary>every caster uses it (MOVES[id].self), else the first that can</summary>
    public bool All;

    public override string? Apply(W_ w)
    {
        var t = Target != 0 ? w.ById(Target) : null;
        string? res = "req";
        foreach (var c in Cmd.Own<Entity>(w, Player, Casters))
        {
            var a = w.MovesOf(c).FirstOrDefault(x => x.Id == ActionId);
            if (a == null) continue;
            var why = w.UseMove(c, a, t != null && t.Kind != "res" ? t : null, HasGround ? (X, Z) : null);
            if (why == null) { res = null; if (!All) break; }
            else if (res != null) res = why;
        }
        return res;
    }
}

/// <summary>Del: kills the first selected (Kill), Shift+Del all of them (KillAll); buildings blow up (G.killSelected).</summary>
public sealed class KillCommand : Command
{
    public int[] Entities = Array.Empty<int>();
    public override string? Apply(W_ w)
    {
        foreach (var e in Cmd.Own<Entity>(w, Player, Entities))
        {
            if (e is Building b) w.SelfDestruct(b);
            else if (e is Unit ku) { ku.SelfKill = true; w.Kill(e, null); }      // DiePerHarakiri: "harakiri" (character.usl:2294)
        }
        return null;
    }
}

/// <summary>A rally point for producers (input.js: a point, or a resource the new workers go to).</summary>
public sealed class SetRallyCommand : Command
{
    public int[] Producers = Array.Empty<int>();
    public double X, Z;
    public int Target;
    public override string? Apply(W_ w)
    {
        var t = Target != 0 ? w.ById(Target) : null;
        foreach (var e in Cmd.Own<Entity>(w, Player, Producers))
        {
            // MIRAGE (FightingObj.SetRallyPoint: CheckTeslaMachine, CheckDestination; GetRallyPosition): a portal terminus as
            // the target - a terminus takes it as its destination (set on itself: none, and an ordinary rally point); the
            // units of another producer go into it, their rally point is its release point (PortalTerminus.cs); a Tesla
            // machine - the producer's units come out there (MirageSeasBuildings.cs)
            if (e is Building pb) { w.CheckTeslaMachine(pb, t); if (W_.IsPortal(pb)) w.PortalSetDestination(pb, t); }
            if (W_.IsPortal(t) && t != e)
            {
                e.Rally = w.PortalRallyPos((Building)t!);
                e.RallyTarget = t;
                continue;
            }
            e.Rally = t is ResNode ? (t.Pos.X, t.Pos.Z) : (X, Z);
            e.RallyTarget = t is ResNode ? t : null;
        }
        return null;
    }
}

/// <summary>Towers of the selection aim at a target (input.js attack on towers).</summary>
public sealed class AimTowersCommand : Command
{
    public int[] Towers = Array.Empty<int>();
    public int Target;
    public override string? Apply(W_ w)
    {
        var t = w.ById(Target);
        if (t == null || !t.Alive) return "target";
        return w.AimTowers(Cmd.Own<Entity>(w, Player, Towers), t) ? null : "none";
    }
}

/// <summary>Moving harbours sail to a point (input.js right-click with a SEAS carrier / Aje floating harbour selected).</summary>
public sealed class MoveHarbourCommand : Command
{
    public int[] Harbours = Array.Empty<int>();
    public double X, Z;
    public override string? Apply(W_ w)
    {
        string? res = null;
        foreach (var b in Cmd.Own<Building>(w, Player, Harbours)) res = w.MoveHarbour(b, X, Z) ?? res;
        return res;
    }
}

/// <summary>A minelayer / corsair lays a mine / water turret in deep water (input.js lay mode).</summary>
public sealed class LayWaterThingCommand : Command
{
    public int Ship;
    public string Action = "";
    public double X, Z;
    public bool Queue;
    public override string? Apply(W_ w)
    {
        var s = Cmd.Own<Unit>(w, Player, new[] { Ship }).FirstOrDefault();
        if (s == null) return "none";
        var a = Cmd.Action(w, s.Owner!, Action, s);
        return a == null ? "req" : w.LayWaterThing(s, a, X, Z, Queue);
    }
}

/// <summary>Workers carrying a load deliver it to a chosen storehouse (input.js right-click on an own drop-off).</summary>
public sealed class DeliverCommand : Command
{
    public int[] Workers = Array.Empty<int>();
    public int Target;
    public override string? Apply(W_ w)
    {
        var t = w.ById(Target);
        if (t == null || !t.Alive) return "target";
        foreach (var u in Cmd.Own<Unit>(w, Player, Workers))
        {
            if (u.Carry == null) continue;
            w.ReleaseTask(u);
            u.Task = new UnitTask("gather") { Res = u.Carry.Res, Phase = "deliver", Drop = t, Node = null };
            var (x, z) = w.ApproachPoint(u, t);
            u.SetPath(x, z);
        }
        return null;
    }
}

/// <summary>Diplomacy (the diplomacy window): the player's relation towards another (0 hostile, 1 neutral, 2 friendly).</summary>
public sealed class DiplomacyCommand : Command
{
    public int Other;
    public int Relation;
    public override string? Apply(W_ w)
    {
        if (w.Opts.DiplomacyLocked) return "locked";
        var a = Cmd.PlayerOf(w, Player); var b = Cmd.PlayerOf(w, Other);
        if (a == null || b == null || a == b) return "none";
        w.SetDiplomacy(a, b, Relation, false);
        return null;
    }
}

/// <summary>Resources given to another player (the diplomacy window's tribute).</summary>
public sealed class TributeCommand : Command
{
    public int To;
    public string Res = "food";
    public double Amount;
    public override string? Apply(W_ w)
    {
        var a = Cmd.PlayerOf(w, Player); var b = Cmd.PlayerOf(w, To);
        if (a == null || b == null) return "none";
        w.Tribute(a, b, Res, Amount);
        return null;
    }
}
