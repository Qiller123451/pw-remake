// Scenario tests of production (src/Sim/Systems/Production.cs, ported from systems/production.js): every tribe trains
// a worker at its base (paid up front, queued in the pyramid, out after the production time at the spawn point, to the
// rally point), the reasons a queue refuses (cost, housing, the pyramid's row, full), cancelling refunds, player-wide
// and local upgrades, the epochs (researched, and the debug options' start / highest epoch), the army pyramid's level
// changes for skulls.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys2ProductionTests
{
    static readonly string[] TRIBES = { "Hu", "Aje", "Ninigi", "SEAS" };

    static Entity Base(W_ w, Player p)
    {
        var name = w.Data.Extra.Start[p.Tribe].Base;
        if (w.Data.Info(name)?.Type == "BLDG") return w.PlaceBuilding(name, p, 0, 0)!;
        return w.SpawnUnit(name, p, 0, 0)!;
    }
    static void Rich(Player p) { p.CapsFixed = true; foreach (var r in Rules.RES) { p.Caps[r] = 10000; p[r] = 5000; } p["skulls"] = 2000; }
    static List<ActionDef> ActionsOf(W_ w, Entity e) => w.Data.ActionsOf(e.RulesOwner(), e.Name, e.Kind, e.Def?.Type ?? e.Cls);

    public static void TrainWorkerEveryTribeBase(TestContext t) => TrainWorker(t, Sys2.Base);
    public static void TrainWorkerEveryTribeMirage(TestContext t) => TrainWorker(t, Sys2.Mirage);

    static void TrainWorker(TestContext t, string data)
    {
        foreach (var tribe in TRIBES)
        {
            var (w, p, _) = Sys2.World(t, data, tribe);
            Rich(p);
            var home = Base(w, p);
            // housing: the base's own limit (or none) - give the player room
            p.PopMax = 52; w.RecomputeCapsOf(p);
            if (p.MaxUnits < 5) { var house = w.Data.Actions(p).FirstOrDefault(a => a.Cat == "Build/BLDG" && w.Data.Stats(a.Results[0].Obj, 1, p)?.Limits.MaxUnits > 0); if (house != null) w.PlaceBuilding(house.Results[0].Obj, p, 40, 40); }
            t.True(p.MaxUnits >= 5, $"{tribe}: a unit limit from the buildings ({p.MaxUnits})");
            var worker = w.Data.Extra.Start[tribe].Units[0];
            var a = ActionsOf(w, home).FirstOrDefault(x => x.Kind == "Build" && x.Results.Count > 0 && x.Results[0].Obj == worker);
            if (a == null) { t.Log($"{tribe}: {home.Name} does not train {worker}"); continue; }
            home.Rally = (30, 0);
            var before = p.Res.ToDictionary(kv => kv.Key, kv => kv.Value);
            int units0 = p.Units;
            t.Equal(null, w.QueueAction(home, a), $"{tribe}: {worker} queued at {home.Name}");
            t.Equal(null, w.QueueAction(home, a), "a second one");
            foreach (var r in Rules.COSTS) t.Equal(before[r] - 2 * a.Cost[r], p[r], $"{tribe}: {r} paid");
            t.Equal(2, p.QueuedUnits, "queued in the pyramid");
            int lv = w.UnitStartLevel(a, p);
            t.Equal(2, p.QueuedAtLevel[lv - 1], $"on level {lv}");
            double T = w.ProductionTime(a, p);
            var trained = new List<double>(); string? firstTask = null;
            for (int i = 0; i < (int)((2 * T + 5) / W_.STEP); i++) { int n = w.Events.Count; w.Step(); foreach (var e in w.EventsSince(n)) if (e.Type == "trained") { trained.Add(w.Time); var tu = e.U("unit")!; firstTask ??= $"{tu.Task.Type}/{tu.Task.Then?.Type}"; } }
            t.Equal(2, trained.Count, $"{tribe}: two trained");
            t.Near(T, trained[0], W_.STEP + 1e-9, $"{tribe}: the first after {T} s");
            t.Near(2 * T, trained[1], 2 * W_.STEP + 1e-9, "the second after it");
            t.Equal(units0 + 2, p.Units, "in the population");
            t.Equal(0, p.QueuedUnits, "out of the queue");
            t.Equal("move/rallyJob", firstTask, $"{tribe}: to the rally point, then a job there");
            // cancelled: everything back
            var mid = p.Res.ToDictionary(kv => kv.Key, kv => kv.Value);
            w.QueueAction(home, a);
            w.CancelQueue(home, 0);
            foreach (var r in Rules.RES) t.Equal(mid[r], p[r], $"{tribe}: {r} refunded");
            t.Equal(0, p.QueuedUnits, "and out of the pyramid");
        }
    }

    public static void QueueReasons(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p);
        var home = Base(w, p);
        var a = ActionsOf(w, home).First(x => x.Kind == "Build" && x.Results[0].Obj == "hu_worker");
        // housing: the unit limit reached
        p.PopMax = 1; w.RecomputeCapsOf(p);
        w.SpawnUnit("hu_worker", p, 20, 0);
        t.Equal("housing", w.QueueAction(home, a), "no room");
        // the pyramid's row full
        p.PopMax = 52; p.Cheat = new PlayerCheat { NoHousing = true }; w.RecomputeCapsOf(p);
        t.Equal(52.0, p.MaxUnits, "the debug option: no housing needed");
        p.Pyramid = new double[] { 1, 15, 8, 3, 1 };
        t.Equal("level", w.QueueAction(home, a), "the row is full");
        p.Pyramid = null;
        // cost
        var res = p.Res.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var r in Rules.RES) p[r] = 0;
        if (a.Cost.Food + a.Cost.Wood + a.Cost.Stone > 0) t.Equal("cost", w.QueueAction(home, a), "too expensive");
        foreach (var (k, v) in res) p[k] = v;
        // six at most
        for (int i = 0; i < 6; i++) t.Equal(null, w.QueueAction(home, a), $"queued #{i + 1}");
        t.Equal("full", w.QueueAction(home, a), "a queue of six");
        // a site produces nothing
        var site = (Building)w.StartConstruction(p, w.Data.Actions(p).First(x => x.Cat == "Build/BLDG" && x.Results[0].Obj == "hu_fireplace" || x.Results[0].Obj == home.Name), 60, 60, 0, new List<Unit>()) is Building sb ? sb : null!;
        if (site != null) t.Equal("req", w.QueueAction(site, a), "not before it is built");
    }

    public static void UpgradesAndEpochsEveryTribe(TestContext t)
    {
        foreach (var data in new[] { Sys2.Base, Sys2.Mirage })
            foreach (var tribe in TRIBES)
            {
                var (w, p, _) = Sys2.World(t, data, tribe);
                Rich(p);
                var home = Base(w, p);
                t.Equal(1, p.Epoch(), $"{tribe}: epoch 1");
                // epoch 2 researched where the tribe offers it (debug mode: the buildings it needs are not asked for)
                var age2 = w.Data.Actions(p).FirstOrDefault(x => x.Kind == "Upgrades" && x.Id == "age_2");
                t.True(age2 != null, $"{tribe}: age_2");
                var at = w.Buildings.Cast<Entity>().Concat(w.Units).FirstOrDefault(e => e.Owner == p && ActionsOf(w, e).Any(x => x.Id == "age_2")) ?? home;
                var why = w.QueueAction(at, age2!);
                if (why == "req") { p.Debug = true; why = w.QueueAction(at, age2!); p.Debug = false; }
                t.Equal(null, why, $"{tribe}: age_2 queued at {at.Name}");
                p.Debug = true; t.Equal("done", w.CanQueue(p, age2!, at), "not twice"); p.Debug = false;
                double T = at.Queue.Count > 0 ? at.Queue[0].Total : 0;
                Sys2.Run(w, T + 0.2);
                t.Equal(2, p.Epoch(), $"{tribe}: epoch 2 after {T} s");
                t.True(p.Techs.Has("age_2"), "in the techs");
                t.True(w.Events.Any(e => e.Type == "upgrade" && e["action"] == age2), "upgrade event");
                // the debug options: start epoch (raiseEpoch), capped by the highest epoch
                w.RaiseEpoch(p, 5, 4);
                t.Equal(4, p.Epoch(), $"{tribe}: raised to epoch 4 (highest 4)");
                p.MaxEpoch = 4;
                var age5 = w.Data.Actions(p).FirstOrDefault(x => x.Kind == "Upgrades" && x.Id == "age_5");
                if (age5 != null) t.Equal("hidden", w.Data.Check(p, age5), "epoch 5 does not exist with the highest epoch 4");
                p.MaxEpoch = 0;
                w.RaiseEpoch(p, 5);
                t.Equal(5, p.Epoch(), $"{tribe}: epoch 5");
                if (home is Building hb) { hb.UpdateVisual(); t.Equal(5, hb.VisAge, "the base looks its epoch"); }
            }
    }

    public static void LocalUpgrade(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Aje");
        Rich(p); p.Debug = true;
        // an action with a "local" result: only the building that researched it changes (the Aje farm modes ...)
        var acts = w.Data.Actions(p).Where(a => a.Kind == "Upgrades" && a.Results.Any(r => r.Cls == "local") && a.Locs.Count > 0).ToList();
        if (acts.Count == 0) t.Skip("no local upgrade");
        var a = acts[0];
        var where = a.Locs[0].At;
        if (w.Data.Info(where)?.Type != "BLDG") t.Skip("local upgrade not at a building: " + where);
        var b = w.PlaceBuilding(where, p, 0, 0)!;
        var b2 = w.PlaceBuilding(where, p, 40, 0)!;
        w.CompleteUpgrade(b, a);
        var path = a.Results.First(r => r.Cls == "local").Path!.Replace("/Filters/", "").TrimStart('/');
        if (path.StartsWith("Filters/")) path = path.Substring(8);
        bool on = a.Results.First(r => r.Cls == "local").Type != "deactivate";
        t.True(b.LocalTT != null && b.LocalTT.Has(path) == on, $"{a.Id}: {path} on the building");
        t.True(b2.LocalTT == null, "not on the other one");
        t.True(!p.Tt!.Has(path) || !on, "not in the player's tree");
        t.True(!p.Techs.Has(a.Id), "no player-wide tech");
    }

    public static void ArmyPyramid(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        var u = w.SpawnUnit("hu_warrior", p, 0, 0, 1)!;
        t.Equal(1, p.AtLevel[0], "on level 1");
        p["skulls"] = 30;
        t.Equal(null, w.LevelUp(u), "level up for 25 skulls");
        t.Equal(2, u.Level, "level 2");
        t.Equal(5.0, p["skulls"], "25 paid");
        t.True(p.AtLevel[0] == 0 && p.AtLevel[1] == 1, "moved in the pyramid");
        t.True(w.Events.Any(e => e.Type == "levelup" && e.U("unit") == u), "levelup event");
        t.Equal("skulls", w.LevelUp(u), "50 needed for level 3");
        t.Equal("down", w.ChangeLevel(u, 1), "never down");
        t.Equal("same", w.ChangeLevel(u, 2), "same");
        p["skulls"] = 1000;
        t.Equal(null, w.ChangeLevel(u, 4), "two levels at once");
        t.Equal(1000.0 - 50 - 100, p["skulls"], "for the skulls of every level on the way");
        t.Equal(u.MaxHp, u.Hp, "a level up heals fully");
        // the row of level 5 is full
        p.Pyramid = new double[] { 25, 15, 8, 3, 0 };
        t.Equal("level", w.ChangeLevel(u, 5), "no free slot on level 5");
        // a computer player's extra slots (difficulty 7-9)
        p.AiMods = new AiMods { Pyramid = new[] { 0, 0, 0, 0, 2 } };
        t.Equal(2.0, w.PyramidFor(p)[4], "+2 slots");
        t.Equal(null, w.ChangeLevel(u, 5), "now it fits");
        t.Equal(1, p.AtLevel[4], "level 5");
        // debug mode: free level-ups
        var v = w.SpawnUnit("hu_warrior", p, 5, 0, 1)!;
        p.Debug = true; p["skulls"] = 0;
        t.Equal(null, w.ChangeLevel(v, 3), "free in debug mode");
    }
}
