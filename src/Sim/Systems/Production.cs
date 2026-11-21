// Production: training units, researching upgrades (player-wide and "local" ones on a single building),
// hiring heroes, and the army pyramid (levels bought with skulls).
//
// Reference: docs/spec/buildings.md 1.9 & 5, docs/spec/units.md 2-3 (Action.usl, RequirementsMgr.usl, NewPyramid.usl).
//
// An upgrade's results name filters of the tech tree:
//   class "player" -> the filter changes the player's tree (all buildings/units of that player)
//   class "local"  -> the filter changes only the building that researched it (its own LocalTree)
//   type  "activate" / "deactivate" switches the filter on or off (e.g. the three Aje farm modes exclude each other)
//
// Ported from remake/src/game/systems/production.js (pw-exporter 98c7703); RaiseEpoch from game/cheats.js raiseEpoch
// (the debug options' start epoch).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public sealed partial class World
{
    static readonly Logger LogProd = Log.For("Systems.Production");
    const int MAX_QUEUE = 6;
    /// <summary>s until the carrier's front hull is open (seas_carrier work_finished)</summary>
    const double HULL_OPEN = 2.4;
    static readonly Regex FiltersPrefix = new(@"^/?Filters/", RegexOptions.Compiled);
    static readonly Regex ActionsPrefix = new(@"^/Actions/", RegexOptions.Compiled);

    /// <summary>why an action can't be queued right now (null = ok): req | level | done | limit | cost | housing | unique</summary>
    public string? CanQueue(Player p, ActionDef action, Entity? producer)
    {
        var owner = producer != null ? producer.RulesOwner() : p;
        var why0 = Data.Check(owner, action, producer);
        if (why0 != null) return why0 == "hidden" || why0 == "disabled" ? "req" : why0;
        if (producer is Building pb && !pb.Built) return "req";
        if (action.Kind == "Upgrades")
        {
            bool local = action.Results.Any(r => r.Cls == "local");
            if (local ? producer!.Queue.Any(q => q.Action.Id == action.Id) : (p.Techs.Has(action.Id) || AnyQueued(p, action.Id))) return "done";
        }
        // MIRAGE's building limits: an upgrade with check = 1 at its product's limit (CAction.Init: CanAddBuilding), and a
        // worker's Build/BLDG card at the building's (the command bar greys it: CheckBuildConditions) - BuildingLimits.cs
        if (LimitWhy(p, action) is { } lim) return lim;
        if (!p.CanAfford(action.Cost)) return "cost";
        if (action.Kind == "Build" && action.Cat != "Build/BLDG")
        {
            var name = action.Results[0].Obj;
            var def = Data.Def(name, p);
            if (def != null && def.Unique && (p.Heroes.Has(name) || AnyQueuedObj(p, name) || (CoopHeroBlocked?.Invoke(p, name) ?? false))) return "unique";
            // while one hero is being hired the others can't be (<hero>_RemoveMe filters)
            if (def != null && def.Unique && producer!.Queue.Any(q => q.Action.Results.Count > 0 && Data.Def(q.Action.Results[0].Obj, p) is { Unique: true })) return "unique";
            int lv = UnitStartLevel(action, p);
            var why = p.SlotFree(lv, PyramidFor(p));
            if (why != null) return why;
        }
        return null;
    }

    public int UnitStartLevel(ActionDef action, Player? p) => Math.Max(Data.StartLevel(action), Data.MinLevel(action.Results[0].Obj, p));

    /// <summary>queue a build / upgrade action at a producer; null = queued, else the reason</summary>
    public string? QueueAction(Entity producer, ActionDef action)
    {
        var p = producer.Owner!;
        if (producer.Active("deaf")) return "deaf";     // deafened (MIRAGE: no commands)
        // instant actions (Moves on buildings, e.g. buy_food / Open / Kill) are handled by the moves system
        if (action.Kind == "Moves") return UseMove(producer, action, null, null);
        var why = CanQueue(p, action, producer);
        if (why != null) return why;
        if (producer.Queue.Count >= MAX_QUEUE) return "full";
        p.Pay(action.Cost);
        var item = new QueueItem { Action = action, T = 0, Total = ProductionTime(action, p), Level = 0 };
        if (action.Kind == "Build")
        {
            item.Level = UnitStartLevel(action, p);
            p.QueuedAtLevel[item.Level - 1]++; p.QueuedUnits++;
        }
        producer.Queue.Add(item);
        // (main.js prewarm: the view compiles the new unit's shaders on "queued")
        Emit("queued", new Args { ["producer"] = producer, ["action"] = action });
        return null;
    }

    /// <summary>duration of production / research; debug mode: instant. A computer player's actions are faster from
    /// difficulty 5 on, except the first epoch upgrade (Action.usl:346-382, "AIHelp"; player.aiMods). The tribe's
    /// modifier /Modifications/&lt;tribe&gt;/Build/&lt;type&gt;/duration (or /&lt;kind&gt;/duration) shortens it
    /// (FightingObj.GetTechTreeActionModifier: Ada's chief bonus, ...)</summary>
    public double ProductionTime(ActionDef action, Player? p)
    {
        if (p != null && p.Debug) return 0.05;
        var m = p?.AiMods;
        double k = m == null || action.Id == "age_2" ? 1 : action.Kind == "Upgrades" ? Or1(m.ResearchTime) : Or1(m.BuildTime);
        double tm = p?.Tt != null ? p.Tt.Num($"/Modifications/{p.Tribe}/{action.Kind}{(action.Kind == "Build" ? "/" + action.Type : "")}/duration", 1) : 1;
        double sp = p?.Cheat != null && p.Cheat.Speed > 0 ? p.Cheat.Speed : 1;      // (debug option: game/cheats.js)
        return Math.Max(0.5 / sp, (action.Time != 0 ? action.Time : 1) * k * tm / sp);
    }
    static double Or1(double v) => v != 0 && !double.IsNaN(v) ? v : 1;

    /// <summary>pyramid slots per level: the game's, plus the extra slots of a computer player at difficulty 7-9
    /// (RequirementsMgr.usl:213-241)</summary>
    public double[] PyramidFor(Player? p)
    {
        var m = p?.AiMods;
        var bas = p?.Pyramid ?? Data.Pyramid;        // a mission's own limits (campaign unit_limits / BLSL)
        if (m?.Pyramid == null) return bas;
        return bas.Select((v, i) => v + (i < m.Pyramid.Length ? m.Pyramid[i] : 0)).ToArray();
    }

    /// <summary>cancel the queue entry i of a producer (refund)</summary>
    public void CancelQueue(Entity producer, int i)
    {
        if (i < 0 || i >= producer.Queue.Count) return;
        var item = producer.Queue[i];
        producer.Queue.RemoveAt(i);
        if (item.BuildDown) { BuildDownFilter((Building)producer, false); return; }
        var p = producer.Owner!;
        p.Refund(item.Action.Cost);
        if (item.Level != 0) { p.QueuedAtLevel[item.Level - 1]--; p.QueuedUnits--; }
    }

    public bool AnyQueued(Player p, string id)
    {
        foreach (var e in Buildings.Cast<Entity>().Concat(Units)) if (e.Alive && e.Owner == p && e.Queue.Any(q => q.Action.Id == id)) return true;
        return false;
    }
    public bool AnyQueuedObj(Player p, string obj)
    {
        foreach (var e in Buildings.Cast<Entity>().Concat(Units)) if (e.Alive && e.Owner == p && e.Queue.Any(q => q.Action.Results.Count > 0 && q.Action.Results[0].Obj == obj)) return true;
        return false;
    }

    /// <summary>a product is done (Action.usl:962 OnWorkFinished): with nothing more in the queue the work loop ends at once
    /// (work_finished, else the idle clip) - not 6 s later (the WORK_ANIM_TIMEOUT of OnWork, which production does not
    /// use: StartWorkAnim / OnWorkFinished)</summary>
    static void WorkDone(Building? b, Entity e)
    {
        if (b != null && e.Queue.Count == 0 && b.WorkT > 0) b.WorkEnd = true;
    }

    partial void ProductionUpdate(Entity e, double dt)
    {
        if (e.Queue.Count == 0 || (e is Building eb && !eb.Built)) return;
        var item = e.Queue[0];
        var b = e as Building;
        if (item.BuildDown)
        {
            item.T = Math.Min(item.Total, item.T + dt);
            b!.OnWork(Time);
            if (item.T >= item.Total) BuildDownDone(b, item);
            return;
        }
        // a unit can't come out while the population is full; it waits at 100 %
        item.T = Math.Min(item.Total, item.T + dt);
        b?.OnWork(Time);
        if (item.T < item.Total) return;
        // buildings that open up to let the unit out (SEAS carrier: work_finished = the front hull opens, fully open
        // after HULL_OPEN s; Building.OnWorkFinished). The unit appears once the hull is open.
        if (b != null && item.Action.Kind == "Build" && b.Anim != null && b.Anim.Has("work_finished") && b.Anim.Pick("work", "work_1") == null)
        {
            if (item.HullT == null)
            {
                item.HullT = Time + HULL_OPEN;
                b.Anim.Play("work_finished", new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone("hullOpen") });
            }
            if (Time < item.HullT) return;
        }
        var p = e.Owner!;
        var a = item.Action;
        if (a.Kind == "Upgrades")
        {
            e.Queue.RemoveAt(0);
            WorkDone(b, e);
            CompleteUpgrade(e, a);
            return;
        }
        e.Queue.RemoveAt(0);
        WorkDone(b, e);
        p.QueuedAtLevel[item.Level - 1]--; p.QueuedUnits--;
        var name = a.Results[0].Obj;
        var tgt = e.Rally ?? (e.Pos.X + 10, e.Pos.Z + 10);
        // ships and amphibians leave on their own grid (harbour dock)
        var info = Data.Info(name);
        bool amph = Unit.AMPHIBIOUS.IsMatch(name);
        var nv = NavFor(info?.Type == "SHIP" && !amph, amph);
        // the carrier: inside the hull at Spwn, out through the open front (Ex_1)
        V3? h0 = null, h1 = null;
        bool hull = b != null && item.HullT != null;
        if (hull) { h0 = b!.LinkWorld("Spwn"); h1 = b.LinkWorld("Ex_1"); }
        var (x, z) = hull && h0 != null ? (h0.Value.X, h0.Value.Z) : b != null ? b.SpawnPoint(tgt.X, tgt.Z, nv) : (e.Pos.X + (e.Radius + 2), e.Pos.Z + (e.Radius + 2));
        double heading = Geo.HeadingTo(tgt.X - x, tgt.Z - z);
        // MIRAGE: a rally point on a Tesla machine - the unit comes out there (Action.usl GetSpawnPos, MirageSeasBuildings.cs)
        if (b != null && !hull && !(info?.Type == "SHIP" && !amph) && TeslaSpawn(b) is var (tx, tz, tr)) { x = tx; z = tz; heading = tr; }
        var u = SpawnUnit(name, p, x, z, item.Level, heading);
        if (u == null) return;
        InheritInfection(e, u);                   // (an infected building trains infected units: Infection.cs)
        b?.SpawnAnim();
        Emit("trained", new Args { ["unit"] = u, ["producer"] = e });
        // a trade cart's home is the market it came out of (TransportObj.usl: the CTheLite event sets m_xHome); it
        // trades when it is sent to another market (the Dock order)
        if (b != null && IsTradeBuilding(b)) u.TradeHome = b;
        if (hull && h1 != null)
        {
            var then = e.Rally is var (rx, rz) ? new Order("move") { X = rx, Z = rz } : null;
            Order(u, new Order("move") { X = h1.Value.X, Z = h1.Value.Z, Then = then });
            // straight out through the open bow (the pathfinder can't start deep inside the hull's own footprint)
            u.Path = new NavPath { new Waypoint(h1.Value.X, h1.Value.Z) }; u.Goal = (h1.Value.X, h1.Value.Z);
            return;
        }
        // the rally point (CTheLite): a worker sent to a resource or a building does what it means there (NextJob); one
        // sent to open ground looks for work around the point when it arrives (rallyJob)
        if (e.Rally is var (ex, ez))
        {
            var rt = e.RallyTarget;
            // a portal terminus (FightingObj.NextJob: finished and of a mutual ally -> EnterPortal; PortalTerminus.cs)
            if (rt is Building pt && PortalNextJob(u, pt)) return;
            if (rt != null && u.IsWorker && NextJob(u, rt)) return;
            Order(u, new Order("move") { X = ex, Z = ez, Then = u.IsWorker ? new Order("rallyJob") { X = ex, Z = ez } : null });
        }
        else if (b != null)
        {
            var (qx, qz) = b.ExitPoint(tgt.X, tgt.Z, nv);
            Order(u, new Order("move") { X = qx, Z = qz });
        }
    }

    // the carrier's hull is open (production.js: the work_finished onDone): a sailing carrier swims on, else idle
    [AnimDone("hullOpen")]
    static void HullOpen(World W, Entity e, Args? a)
    {
        if (e is not Building b || b.Anim == null) return;
        if (b.Sail != null) b.Anim.Play(b.Anim.Pick("swim_1", "standanim")); else b.IdleAnim();
    }

    /// <summary>an upgrade finished: switch its filters (player-wide or on this object only). e: the object that
    /// researched it (null: the player itself - the debug options' start epoch)</summary>
    public void CompleteUpgrade(Entity? e, ActionDef a, Player? owner = null)
    {
        var p = e?.Owner ?? owner!;
        bool local = false;
        // (the player's tree is rebuilt once after all of the upgrade's filters, not once per filter: the same tree - an
        // epoch switches dozens, each rebuild of the MIRAGE tree costs tens of milliseconds)
        p.Tt?.Suspend();
        try
        {
            foreach (var r in a.Results)
            {
                var path = FiltersPrefix.Replace(r.Path ?? "", "", 1);
                if (path == "") continue;
                if (r.Cls == "local") { local = true; if (e != null) LocalFilter(e, path, r.Type != "deactivate"); }
                else if (r.Type == "deactivate") p.Tt!.Disable(path);
                else p.Tt!.Enable(path);
            }
        }
        finally { p.Tt?.Resume(); }
        if (a.Results.Count == 0) p.Tt!.Enable(ActionsPrefix.Replace(a.Path, "", 1));
        if (!local) p.Techs.Add(a.Id);
        if (e != null && MirageMoves.UPGRADE_HOOKS.TryGetValue(a.Id, out var hook)) hook(this, e, a);     // (moves_mirage.js: what upgrades do besides filters)
        Emit("upgrade", new Args { ["player"] = p, ["action"] = a, ["local"] = local, ["entity"] = e });
    }

    /// <summary>enable / disable a filter on one object only (its own tree follows the owner's tree)</summary>
    public void LocalFilter(Entity e, string path, bool on)
    {
        if (e.LocalTT == null)
        {
            if (!on || e.Owner?.Tt == null) return;
            e.LocalTT = new LocalTree(e.Owner.Tt);
        }
        if (on) { if (!e.LocalTT.Has(path)) e.LocalTT.Enable(path); } else e.LocalTT.Disable(path);
        if (e is Unit u) u.RefreshRules(); else if (e is Building b) b.RefreshRules();
        RecomputeCapsOf(e.Owner);
    }

    /// <summary>"resultactions" of a build action: an upgrade applied right after construction (aje_medium_farm etc.)</summary>
    public void ApplyResultAction(Entity e, string path)
    {
        var a = Data.Actions(e.Owner).FirstOrDefault(x => x.Path == path);
        if (a != null) CompleteUpgrade(e, a);
    }

    // ------------------------------------------------------------------ army pyramid (NewPyramid.usl, FightingObj.SetLevel)
    /// <summary>level a unit up with skulls (Ctrl+U); null = done, else the reason</summary>
    public string? LevelUp(Unit u) => ChangeLevel(u, u.Level + 1);
    /// <summary>skull cost from level a to level b: sum of the per-level costs 25 / 50 / 100 / 300</summary>
    public double SkullCost(int a, int b) { double c = 0; for (int l = a + 1; l <= b; l++) c += l - 2 >= 0 && l - 2 < Data.LevelupSkulls.Length ? Data.LevelupSkulls[l - 2] : 0; return c; }
    public bool CanHaveLevel(Unit u, int nl) { var st = Data.Stats(u.Name, nl, u.Owner); return st != null && st.Exists && !u.Illusion; }

    /// <summary>move a unit to a higher pyramid row (drag &amp; drop onto a free slot), for the skulls of every level on
    /// the way. As the original's pyramid (NewPyramid.usl SwapSlotsAndCards): a card never goes to a lower row - a unit
    /// keeps the level it reached - and cards on different rows do not swap; the target row needs a free slot.
    /// null = done, else "same" | "down" | "max" | "level" | "skulls"</summary>
    public string? ChangeLevel(Unit u, int nl)
    {
        var p = u.Owner;
        if (p == null || !u.Alive || nl == u.Level) return "same";
        if (nl < u.Level) return "down";
        if (nl > 5) return "max";
        if (!CanHaveLevel(u, nl)) return "max";
        if (p.AtLevel[nl - 1] + p.QueuedAtLevel[nl - 1] >= PyramidFor(p)[nl - 1]) return "level";
        double cost = p.Debug ? 0 : SkullCost(u.Level, nl);          // (debug mode: free level-ups)
        if (p["skulls"] < cost) return "skulls";
        p["skulls"] -= cost;
        ApplyLevel(u, nl);
        return null;
    }

    static readonly HashSet<string> KeepTasks = new() { "attack", "move", "gather", "build", "heal" };

    public void ApplyLevel(Unit u, int nl)
    {
        var p = u.Owner!; int ol = u.Level;
        p.AtLevel[ol - 1]--; p.AtLevel[nl - 1]++;
        var task = u.Task;
        LevelFilters(u, ol, false);
        u.ApplyLevel(nl);
        LevelFilters(u, nl, true);
        u.Task = KeepTasks.Contains(task.Type) ? task : UnitTask.Idle();
        PirateLevelUp(u);                                  // (MIRAGE's pirate boss ship: 9 s without orders, PirateShip.cs)
        if (nl > ol)
        {
            // full heal; the level-up animation and invulnerable while it plays (2.5 s for characters, 1.5 s otherwise) -
            // only with a "level_up" animation, not in a transport or trapped (DoLevelUpAnim: character.usl:948, FO:3532)
            if (!u.Inside && !u.Active("trapped") && u.Anim!.Has("level_up"))
            {
                u.St.T["levelInvul"] = Time + (u.Cls == "CHTR" ? 2.5 : 1.5);
                u.BusyAnim = true;
                u.Anim.Play("level_up", new AnimOpts { Loop = false, OnDone = new AnimDone("busy") });
            }
            Emit("levelup", new Args { ["unit"] = u });
        }
    }

    // ------------------------------------------------------------------ the debug options (game/cheats.js)
    /// <summary>give player p the epoch upgrades up to `epoch` (capped by `max`), as if researched at its headquarters
    /// (cheats.js raiseEpoch)</summary>
    public void RaiseEpoch(Player p, int epoch, int max = 0)
    {
        int top = max != 0 ? Math.Min(epoch, max) : epoch;
        for (int k = p.Epoch() + 1; k <= top; k++)
        {
            var id = "age_" + k;
            var a = Data.Actions(p).FirstOrDefault(x => x.Kind == "Upgrades" && x.Id == id);
            if (a == null) break;
            // the building that offers it (headquarters, fireplace, ...), else any of the player's objects
            Entity? at = Buildings.FirstOrDefault(b => b.Alive && b.Owner == p && Data.ActionsOf(b.RulesOwner(), b.Name, "building", b.Def?.Type ?? b.Cls).Any(x => x.Id == id))
                ?? (Entity?)Buildings.FirstOrDefault(b => b.Alive && b.Owner == p) ?? Units.FirstOrDefault(u => u.Alive && u.Owner == p);
            CompleteUpgrade(at, a, p);
        }
    }
}
