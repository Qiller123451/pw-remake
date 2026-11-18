// Scenario tests of construction (src/Sim/Systems/Construction.cs, ported from systems/construction.js): a building
// site paid and placed, built up by workers to completion at the JS rate (duration x the slowest worker's time factor x
// (0.1 + 0.9 x 0.8^(N-1))), the placement checks, the next job of the builders, repairs (50 % of the cost per full
// repair), self destruction and the Aje BuildDown, the debug options (free and instant, speed), the ground levelling
// event.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys2ConstructionTests
{
    static void Rich(Player p) { p.CapsFixed = true; foreach (var r in Rules.RES) { p.Caps[r] = 10000; p[r] = 5000; } }

    /// <summary>the first building action of a tribe that is allowed at the start and not a wall-map object</summary>
    static ActionDef FirstBuilding(W_ w, Player p, params string[] prefer)
    {
        foreach (var id in prefer) { var a = Sys2.Action(w, p, id, "Build"); if (a != null && w.Data.Check(p, a) == null) return a; }
        return w.Data.Actions(p).First(a => a.Cat == "Build/BLDG" && w.Data.Check(p, a) == null && w.Data.Def(a.Results[0].Obj, p)?.WallKind == null && !w.Data.Def(a.Results[0].Obj, p)!.Coastal);
    }

    public static void BuildToCompletionEveryTribe(TestContext t)
    {
        foreach (var tribe in new[] { "Hu", "Aje", "Ninigi", "SEAS" })
        {
            foreach (int N in new[] { 1, 3 })
            {
                var (w, p, _) = Sys2.World(t, Sys2.Base, tribe);
                Rich(p);
                var workers = Enumerable.Range(0, N).Select(i => w.SpawnUnit(w.Data.Extra.Start[tribe].Units[0], p, -12, -6 + 4 * i)!).ToList();
                var a = FirstBuilding(w, p, "hu_lumberjack_cottage", "aje_bazaar", "ninigi_lumbermill", "seas_small_tent");
                var before = p.Res.ToDictionary(kv => kv.Key, kv => kv.Value);
                var r = w.StartConstruction(p, a, 10, 0, 0, workers);
                t.True(r is Building, $"{tribe} {a.Id}: placed ({r})");
                var b = (Building)r;
                foreach (var res in Rules.RES) t.Equal(before[res] - a.Cost[res], p[res], $"{tribe} {a.Id}: {res} paid up front");
                t.True(!b.Built && b.Progress == 0 && b.Hp == 1, "a site");
                t.True(workers.All(u => u.Task.Type == "build" && u.Task.Building == b), "the workers go to build it");
                t.Equal(1, w.Events.Count(e => e.Type == "placed"), "placed event");
                // time: duration x the slowest worker's tf x (0.1 + 0.9 x 0.8^(N-1)); the walk there comes first
                double tf = workers.Min(u => u.Stats!.Tf > 0 ? u.Stats.Tf : 2.0), D = a.Time > 0 ? a.Time : 10;
                double total = D * tf * (0.1 + 0.9 * Math.Pow(0.8, N - 1));
                double start = -1, done = -1;
                for (int i = 0; i < (int)((total + 60) / W_.STEP) && !b.Built; i++)
                {
                    w.Step();
                    if (start < 0 && workers.All(u => u.Task.Phase == "work")) start = w.Time;
                    if (b.Progress >= 0.25) t.True(b.Blocking || b.Def!.Gate, "blocks from construction level 1");
                }
                done = w.Time;
                t.True(b.Built, $"{tribe} {a.Id} ({N} workers): finished");
                t.True(start > 0 && done - start <= total + W_.STEP * 2 && done - start >= total - 3, $"{tribe} {a.Id} ({N} workers): built in {done - start:0.00} s of work, the formula says {total:0.00} s (the first ones start a little earlier)");
                t.True(b.Hp >= b.MaxHp * 0.999, "full hit points");
                t.Equal(4, b.VisLevel, "finished look");
                t.Equal(1, w.Events.Count(e => e.Type == "built" && e.B("building") == b), "built event");
                t.True(p.Tt!.Has($"{b.Def!.Tribe}/BuildObjects/{b.Name}"), "its BuildObjects filter");
                t.True(workers.All(u => u.Task.Type != "build" || u.Task.Building != b), "the builders are free again");
                t.Log($"{tribe} {a.Id}: {N} workers, {done - start:0.00} s (formula {total:0.00} s)");
            }
        }
    }

    public static void PlacementChecks(TestContext t)
    {
        var (w, p, e) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p);
        var a = FirstBuilding(w, p, "hu_lumberjack_cottage");
        var name = a.Results[0].Obj;
        t.True(w.CanPlace(name, 0, 0, 0, p), "free ground");
        t.True(!w.CanPlace(name, 126, 0, 0, p), "not at the map's edge");
        var b = w.PlaceBuilding(name, p, 0, 0)!;
        t.True(!w.CanPlace(name, 2, 1, 0, p), "not on another building");
        w.AddResource("stone", "stone", 40, 0, 1000, new ResOpts { Radius = 3 });
        t.True(!w.CanPlace(name, 40, 0, 0, p), "not on a resource");
        w.SpawnUnit("aje_warrior", e, -40, 0);
        t.True(!w.CanPlace(name, -40, 0, 0, p), "not on an enemy unit");
        t.Equal("place", w.StartConstruction(p, a, 1, 1, 0, new List<Unit>()), "the place check");
        foreach (var r in Rules.RES) p[r] = 0;
        t.Equal("cost", w.StartConstruction(p, a, 40, 40, 0, new List<Unit>()), "the price");
        var top = w.Data.Actions(p).FirstOrDefault(x => x.Cat == "Build/BLDG" && w.Data.Check(p, x) == "req");
        if (top != null) t.Equal("req", w.StartConstruction(p, top, 40, 40, 0, new List<Unit>()), "the requirements");
    }

    public static void DebugFreeAndInstantAndSpeed(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        foreach (var r in Rules.RES) p[r] = 0;
        p.Debug = true;
        var a = FirstBuilding(w, p, "hu_lumberjack_cottage");
        var b = w.StartConstruction(p, a, 0, 0, 0, new List<Unit>()) as Building;
        t.True(b != null && b.Built, "debug mode: free and finished at once");
        t.Equal(0.0, p["wood"], "nothing paid");
        // the debug option "speed": construction x speed
        var (w2, p2, _) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p2); p2.Cheat = new PlayerCheat { Speed = 4 };
        var u = w2.SpawnUnit("hu_worker", p2, -10, 0)!;
        var b2 = (Building)w2.StartConstruction(p2, a, 10, 0, 0, new List<Unit> { u });
        double start = -1;
        while (!b2.Built && w2.Time < 200) { w2.Step(); if (start < 0 && u.Task.Phase == "work") start = w2.Time; }
        double tf = u.Stats!.Tf > 0 ? u.Stats.Tf : 2;
        t.Near(a.Time * tf / 4, w2.Time - start, 0.11, "four times as fast");
    }

    public static void BuildersNextJob(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p);
        w.PlaceBuilding("hu_fireplace", p, -30, -30);
        var tree = w.AddResource("tree", "wood", 30, 20, 5000, new ResOpts { Radius = 1.5, MaxWorkers = 4 });
        var u = w.SpawnUnit("hu_worker", p, -10, 0)!;
        var a = Sys2.Action(w, p, "hu_lumberjack_cottage", "Build");
        if (a == null || w.Data.Check(p, a) != null) t.Skip("no lumberjack cottage at the start");
        var b = (Building)w.StartConstruction(p, a!, 10, 0, 0, new List<Unit> { u });
        while (!b.Built && w.Time < 200) w.Step();
        w.Step();
        t.True(b.Built, "built");
        t.True(u.Task.Type == "gather" && u.Task.Res == "wood", "the lumberjack cottage's builder goes for wood (64 m): " + u.Task);
        // a second site within 50 m: builders of a finished building build it
        var b3 = (Building)w.StartConstruction(p, a!, 10, 30, 0, new List<Unit>());
        var u2 = w.SpawnUnit("hu_worker", p, 0, 20)!;
        w.AutoWork(u2, b);
        t.True(u2.Task.Type == "gather" || (u2.Task.Type == "build" && u2.Task.Building == b3), "next job " + u2.Task);
        // a new worker at a rally point next to a site builds it (rallyJob)
        var u3 = w.SpawnUnit("hu_worker", p, 10, 22)!;
        w.Order(u3, new Order("rallyJob") { X = 10, Z = 25 });
        t.True(u3.Task.Type == "build" && u3.Task.Building == b3, "rally job: build the nearest site " + u3.Task);
        t.True(w.NextJob(u3, tree) && u3.Task.Type == "gather", "next job at a tree: gather");
    }

    public static void Repair(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p);
        var a = FirstBuilding(w, p, "hu_lumberjack_cottage");
        var b = w.PlaceBuilding(a.Results[0].Obj, p, 10, 0)!;
        b.Hp = b.MaxHp / 2;
        var u = w.SpawnUnit("hu_worker", p, -6, 0)!;
        var before = p.Res.ToDictionary(kv => kv.Key, kv => kv.Value);
        w.Order(u, new Order("repair") { Target = b });
        t.True(u.Task.Type == "build" && u.Task.Repair, "repairing");
        double hp0 = -1, t0 = 0;
        while (b.Hp < b.MaxHp && w.Time < 200) { w.Step(); if (hp0 < 0 && u.Task.Phase == "work") { hp0 = b.Hp; t0 = w.Time; } }
        t.Near(b.MaxHp, b.Hp, 1e-6, "repaired");
        w.Step();
        double rate = (b.MaxHp - hp0) / (w.Time - t0);
        t.True(Math.Abs(rate - 20) < 2 || w.Time - t0 < 2, $"20 hp/s at level 1 ({rate:0.0})");
        // half of the hit points: 25 % of the build cost (50 % for a full repair), whole units
        foreach (var r in Rules.RES) if (a.Cost[r] > 0) t.True(Math.Abs(before[r] - p[r] - 0.25 * a.Cost[r]) <= 1, $"{r}: {before[r] - p[r]} of {a.Cost[r]} paid");
        t.Equal("idle", u.Task.Type, "done");
    }

    public static void SelfDestructAndRuins(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p);
        var a = FirstBuilding(w, p, "hu_lumberjack_cottage");
        var b = w.PlaceBuilding(a.Results[0].Obj, p, 10, 0)!;
        var cells = b.Cells.ToList();
        t.True(cells.Any(k => w.Nav.Block[k] != 0), "blocks");
        w.SelfDestruct(b);
        if (b.HashKey != PwRemake.Sim.World.SpatialHash<Building>.NoKey) w.BuildingDestroyed(b, null);   // (combat's Kill is a stand-in)
        t.True(!b.Alive, "destroyed");
        t.True(cells.All(k => w.Nav.Block[k] == 0), "its cells free");
        t.Equal(w.RuinOf(b), b.Ruin, "the ruin model (if the game has one)");
        t.True(w.Events.Any(e => e.Type == "destroyed" && e.E("entity") == b), "destroyed event");
        // an unfinished site goes silently
        var s = (Building)w.StartConstruction(p, a, -20, 0, 0, new List<Unit>());
        w.SelfDestruct(s);
        if (s.HashKey != PwRemake.Sim.World.SpatialHash<Building>.NoKey) w.BuildingDestroyed(s, null);
        t.True(s.SilentDeath && s.Removed && s.Ruin == null, "a site: no ruin");
    }

    public static void AjeBuildDown(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Aje");
        p.CapsFixed = true; foreach (var r in Rules.RES) { p.Caps[r] = 10000; p[r] = 1000; }
        var a = FirstBuilding(w, p, "aje_bazaar");
        var b = w.PlaceBuilding(a.Results[0].Obj, p, 10, 0)!;
        b.Action = a;
        var bd = w.Data.ActionsOf(b.RulesOwner(), b.Name, "building", b.Def!.Type).FirstOrDefault(x => x.Id == "BuildDown") ?? new ActionDef { Id = "BuildDown", Kind = "Moves" };
        var before = p.Res.ToDictionary(kv => kv.Key, kv => kv.Value);
        t.Equal(null, w.StartBuildDown(b, bd), "BuildDown queued");
        t.True(b.Queue.Count == 1 && b.Queue[0].BuildDown, "a queue item");
        t.True(b.LocalTT != null && b.LocalTT.Has($"Aje/Upgrades/{b.Name}/BuildDown"), "its BuildDown filter");
        double half = 0.5 * (w.BuildActionOf(b)?.Time ?? 20);
        Sys2.Run(w, half + 0.5);
        t.True(!b.Alive && b.SilentDeath, $"taken down after {half} s");
        foreach (var r in Rules.RES) t.Equal(before[r] + a.Cost[r], p[r], $"{r}: the whole cost back");
        // cancelled: the filter goes again
        var b2 = w.PlaceBuilding(a.Results[0].Obj, p, -20, 0)!;
        w.StartBuildDown(b2, bd);
        w.CancelQueue(b2, 0);
        t.True(b2.Queue.Count == 0 && !b2.LocalTT!.Has($"Aje/Upgrades/{b2.Name}/BuildDown"), "cancelled");
    }

    public static void GroundLevellingEvent(TestContext t)
    {
        var bp = "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a/assets/terrain/objbrush.json";
        if (!System.IO.File.Exists(bp)) t.Skip("no objbrush.json");
        var hf = new PwRemake.Sim.Maps.HeightField(256, 2, (x, z) => 0.1 * x);
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu", height: hf.At);
        Rich(p);
        w.UseGround(hf, PwRemake.Sim.Maps.GroundBrushes.LoadFile(bp));
        var a = FirstBuilding(w, p, "hu_lumberjack_cottage");
        double h0 = hf.At(10, 0), e0 = hf.At(4, 0) - hf.At(16, 0);
        var b = w.StartConstruction(p, a, 10, 0, 0.5, new List<Unit>());
        t.True(b is Building, "placed");
        var ev = w.Events.FirstOrDefault(e => e.Type == "terrain");
        t.True(ev != null, "the event the view redraws the ground on");
        t.Equal(w.Data.Stats(a.Results[0].Obj, 1, p)!.Gfx, ev!.Str("gfx"), "with the building's brush");
        t.True(ev["rect"] is PwRemake.Sim.Maps.SampleRect, "the height samples changed");
        t.True(Math.Abs(hf.At(4, 0) - hf.At(16, 0)) < Math.Abs(e0) * 0.5, $"the ground under it levelled ({e0:0.00} m -> {hf.At(4, 0) - hf.At(16, 0):0.00} m)");
        t.Equal(1, w.GroundLog.Count, "the levelling logged (saved games)");
    }

    /// <summary>The placement grid's dots (World.PlaceGridOf, what the input draws) are the placement rule itself: on a
    /// steep ramp next to flat ground, every dot is green exactly where StartConstruction puts the building down there
    /// and red exactly where it refuses ("place"); there are both.</summary>
    public static void PlaceGridIsThePlacementRule(TestContext t)
    {
        // flat west of x = 10, a ramp of 1:1.5 east of it, a plateau from x = 40
        Func<double, double, double> hill = (x, z) => x < 10 ? 0 : x < 40 ? (x - 10) / 1.5 : 20;
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu", height: hill);
        Rich(p);
        var a = FirstBuilding(w, p, "hu_lumberjack_cottage");
        var name = a.Results[0].Obj;
        var dots = w.PlaceGridOf(name, 10, 0, 0, p);
        t.True(dots.Count > 400, $"a grid of dots ({dots.Count})");
        int green = dots.Count(d => d.Ok), red = dots.Count - green;
        t.True(green > 20 && red > 20, $"green ({green}) and red ({red}) dots");
        foreach (var (x, z, ok) in dots) t.Equal(w.PlacementOf(name, x, z, 0, p).Ok, ok, $"dot {x},{z}");
        // a sample of dots placed for real, each in a fresh world: green ones stand, red ones are refused
        var sample = dots.Where(d => d.Ok).Take(3).Concat(dots.Where(d => !d.Ok && d.X > 10 && d.X < 40).Take(3)).ToList();
        foreach (var (x, z, ok) in sample)
        {
            var (w2, p2, _) = Sys2.World(t, Sys2.Base, "Hu", height: hill);
            Rich(p2);
            var r = w2.StartConstruction(p2, FirstBuilding(w2, p2, "hu_lumberjack_cottage"), x, z, 0, new List<Unit>());
            t.True(ok ? r is Building : r as string == "place", $"dot {x},{z} {(ok ? "green" : "red")}: {r}");
        }
    }
}
