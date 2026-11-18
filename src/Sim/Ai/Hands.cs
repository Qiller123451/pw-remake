// The computer player's hands: everything a player can do, as the world's commands (docs/ARCHITECTURE.md "Commands").
// The brain decides inside the AI phase of a step and needs the answer at once (the JS called the world's methods and
// looked at the result: a queued item, a building site), so the default hands apply each command immediately - the
// same code path as the interface's commands, the same rules (own entities only, costs, checks). Every command is
// also handed to `Issued` (a host that runs the computer players for others sends them on; a replay records them).
//
// What only the original's engine did for the computer player (resource gifts, spawned waves, the free epoch,
// boarding a spawned wave into its ships) the brain does on the world directly, as the JS did (Brain.cs and the modules).
//
// Not a JS module: the seam between the port of game/ai/*.js and src/Sim/Commands.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Ai;

/// <summary>What the brain does as a player (the JS: W.order, W.queueAction, W.startConstruction, W.cancelQueue,
/// W.changeLevel, W.levelUp, W.useMove, W.layWaterThing, W.selfDestruct).</summary>
public interface IAiHands
{
    void Order(Player p, IReadOnlyList<Unit> units, Order o);
    /// <summary>null = queued, else the reason</summary>
    string? QueueAction(Player p, Entity producer, ActionDef a);
    /// <summary>the new building site, or the reason it could not be placed ("cost", "place", "req" ...)</summary>
    object StartConstruction(Player p, ActionDef a, double x, double z, double rot, List<Unit> workers);
    void CancelQueue(Player p, Entity producer, int i);
    string? ChangeLevel(Player p, Unit u, int level);
    string? LevelUp(Player p, Unit u);
    string? UseMove(Player p, Entity e, ActionDef a, Entity? target = null, (double X, double Z)? ground = null);
    string? LayWaterThing(Player p, Unit ship, ActionDef a, double x, double z, bool queue);
    void SelfDestruct(Player p, Building b);
    /// <summary>MIRAGE: a heal walk the brain's other orders cannot override ("Action_AILock" of AiGoalUseFountains:
    /// AiOrderCommand.HealLock; Fountains.cs). Hands without the lock just give the order.</summary>
    void HealOrder(Player p, Unit u, Order o) => Order(p, new[] { u }, o);
}

/// <summary>The default hands: each action is a command applied now (and reported to Issued).</summary>
public sealed class CommandHands : IAiHands
{
    readonly W_ W;
    /// <summary>every command the computer players gave, with its result (null: none listens)</summary>
    public Action<Command, string?>? Issued;
    public CommandHands(W_ w) { W = w; }

    string? Exec(Command c, Player p)
    {
        c.Player = p.Id; c.Step = W.StepNo;
        string? why;
        try { why = c.Apply(W); }
        catch (Exception e) { Core.Log.For("Ai").Error($"command {c} failed", e); why = "error"; }
        Issued?.Invoke(c, why);
        return why;
    }

    public void Order(Player p, IReadOnlyList<Unit> units, Order o)
    {
        if (units.Count == 0) return;
        Exec(AiOrderCommand.Of(units, o), p);
    }
    public void HealOrder(Player p, Unit u, Order o)
    {
        var c = AiOrderCommand.Of(new[] { u }, o);
        c.HealLock = true;
        Exec(c, p);
    }
    public string? QueueAction(Player p, Entity producer, ActionDef a) => Exec(new QueueActionCommand { Producer = producer.Id, Action = a.Path, Count = 1 }, p);
    public object StartConstruction(Player p, ActionDef a, double x, double z, double rot, List<Unit> workers)
    {
        int n = W.Buildings.Count;
        var why = Exec(new PlaceBuildingCommand { Action = a.Path, X = x, Z = z, Rot = rot, Workers = workers.Select(u => u.Id).ToArray() }, p);
        if (why != null) return why;
        // the site is the building the command added
        for (int i = W.Buildings.Count - 1; i >= n; i--) if (W.Buildings[i].Owner == p) return W.Buildings[i];
        return "place";
    }
    public void CancelQueue(Player p, Entity producer, int i) => Exec(new AiCancelQueueCommand { Producer = producer.Id, Index = i }, p);
    public string? ChangeLevel(Player p, Unit u, int level) => Exec(new ChangeLevelCommand { Unit = u.Id, Level = level }, p);
    public string? LevelUp(Player p, Unit u) => Exec(new LevelUpCommand { Unit = u.Id }, p);
    // (a unit on its locked heal walk takes no special move of the brain but these: FightingObj.HandleGamePlayCommand)
    static readonly HashSet<string> HEAL_LOCK_MOVES = new(StringComparer.Ordinal) { "Kill", "LevelUp", "Dismount", "LeaveTransport" };
    public string? UseMove(Player p, Entity e, ActionDef a, Entity? target = null, (double X, double Z)? ground = null) =>
        e is Unit hu && W.AiHealLocked(hu) && !HEAL_LOCK_MOVES.Contains(a.Id) ? "locked" :
        Exec(new UseMoveCommand { Casters = new[] { e.Id }, ActionId = a.Id, Target = target?.Id ?? 0, HasGround = ground != null, X = ground?.X ?? 0, Z = ground?.Z ?? 0 }, p);
    public string? LayWaterThing(Player p, Unit ship, ActionDef a, double x, double z, bool queue) =>
        Exec(new LayWaterThingCommand { Ship = ship.Id, Action = a.Path, X = x, Z = z, Queue = queue }, p);
    public void SelfDestruct(Player p, Building b) => Exec(new KillCommand { Entities = new[] { b.Id } }, p);
}

/// <summary>An order of the computer player (W.order with auto: true, then: the next order): OrderCommand with the
/// fields the AI uses. Entities by id.</summary>
public sealed class AiOrderCommand : Command
{
    public int[] Units = Array.Empty<int>();
    public string Type = "move";
    public double X, Z;
    public int Target;
    public bool Auto, Force;
    /// <summary>the next order on arrival (units not used)</summary>
    public AiOrderCommand? Then;
    /// <summary>MIRAGE: the heal walk of AiGoalUseFountains ("Action_AILock"): the units that start it are held to it -
    /// the brain's other orders do not reach them until it is over (Unit.AiHealLock, World.AiHealLocked)</summary>
    public bool HealLock;

    public static AiOrderCommand Of(IEnumerable<Unit> units, Order o) => new()
    {
        Units = units.Select(u => u.Id).ToArray(), Type = o.Type, X = o.X, Z = o.Z, Target = o.Target?.Id ?? 0, Auto = o.Auto, Force = o.Force,
        Then = o.Then != null ? Of(Array.Empty<Unit>(), o.Then) : null,
    };
    Order ToOrder(W_ w) => new(Type) { X = X, Z = Z, Target = Target != 0 ? w.ById(Target) : null, Auto = Auto, Force = Force, Then = Then?.ToOrder(w) };

    public override string? Apply(W_ w)
    {
        var units = new List<Unit>();
        foreach (var id in Units) if (w.ById(id) is Unit u && u.Alive && u.Owner != null && u.Owner.Id == Player) units.Add(u);
        if (units.Count == 0) return "none";
        // MIRAGE: a unit on the heal walk its computer player locked it to takes no other order of it - but kill and
        // getting out (FightingObj.usl HandleGamePlayCommand: Kill, LevelUp, Dismount, LeaveTransport and queued "Q_"
        // orders pass; the walk back is queued behind the walk itself here: UnitTask.Then)
        if (!HealLock && Type != "kill" && Type != "unload")
        {
            units.RemoveAll(w.AiHealLocked);
            if (units.Count == 0) return "locked";
        }
        var o = ToOrder(w);
        if (Target != 0 && (o.Target == null || !o.Target.Alive) && Type != "move") return "target";
        w.Order(units, o);
        if (HealLock) foreach (var u in units) if (u.Task.Type == "fountain") u.AiHealLock = true;
        return null;
    }
}

/// <summary>Cancels entry i of a producer's queue (W.cancelQueue: the refund).</summary>
public sealed class AiCancelQueueCommand : Command
{
    public int Producer, Index;
    public override string? Apply(W_ w)
    {
        if (w.ById(Producer) is not { Alive: true } e || e.Owner == null || e.Owner.Id != Player) return "none";
        if (Index < 0 || Index >= e.Queue.Count) return "none";
        w.CancelQueue(e, Index);
        return null;
    }
}

/// <summary>A region of an influence map (AIRG): the campaign's regions, or a disc / rectangle.</summary>
public interface IAiRegion { bool Contains(double x, double z); }
public sealed record AiCircle(double X, double Z, double R) : IAiRegion { public bool Contains(double x, double z) => Math.Sqrt((x - X) * (x - X) + (z - Z) * (z - Z)) <= R; }
public sealed record AiRect(double X0, double Z0, double X1, double Z1) : IAiRegion { public bool Contains(double x, double z) => x >= X0 && x <= X1 && z >= Z0 && z <= Z1; }
public sealed class AiRegionFn : IAiRegion { readonly Func<double, double, bool> _f; public AiRegionFn(Func<double, double, bool> f) { _f = f; } public bool Contains(double x, double z) => _f(x, z); }
