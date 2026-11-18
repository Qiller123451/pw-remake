// Scenario tests of the economy (src/Sim/Systems/Economy.cs, ported from systems/economy.js): a worker of every tribe
// gathers wood, stone and food and delivers it at its tribe's storehouse - the load is the worker's carrying capacity,
// the work of one load AnimAction(work clip, loops of beginWork, intro, outro): its start part, the loops x its loop part,
// its end part (the JS: loops x the whole clip; docs/SIMULATION.md "Animation audit"); a full storehouse stops the
// delivery; the computer player's and the debug options' gather factors; a field; the market; trade carts.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys2EconomyTests
{
    static ResNode Tree(W_ w, double x, double z, double wood = 200) =>
        w.AddResource("tree", "wood", x, z, wood, new ResOpts { Radius = 1.5, MaxWorkers = 4, Cells = w.Nav.MarkCircle(x, z, 1.6) });
    static ResNode Stone(W_ w, double x, double z, double amount = 2000) =>
        w.AddResource("stone", "stone", x, z, amount, new ResOpts { Radius = 2.5, MaxWorkers = 8, Cells = w.Nav.MarkCircle(x, z, 2) });
    static ResNode Bush(W_ w, double x, double z, double amount = 150) =>
        w.AddResource("bush", "food", x, z, amount, new ResOpts { Radius = 1.5, MaxWorkers = 4, Cells = w.Nav.MarkCircle(x, z, 1.2) });

    /// <summary>the tribe's start base (a building, or the Aje resource collector) at the origin</summary>
    static Entity Base(W_ w, Player p)
    {
        var name = w.Data.Extra.Start[p.Tribe].Base;
        if (w.Data.Info(name)?.Type == "BLDG") return w.PlaceBuilding(name, p, 0, 0)!;
        return w.SpawnUnit(name, p, 0, 0)!;
    }

    static List<WorldEvent> Ev(W_ w, string type) => w.Events.Where(e => e.Type == type).ToList();

    public static void GatherDeliverEveryTribeBase(TestContext t) => GatherDeliver(t, Sys2.Base);
    public static void GatherDeliverEveryTribeMirage(TestContext t) => GatherDeliver(t, Sys2.Mirage);

    static void GatherDeliver(TestContext t, string data)
    {
        foreach (var tribe in new[] { "Hu", "Aje", "Ninigi", "SEAS" })
        {
            foreach (var res in new[] { "wood", "stone", "food" })
            {
                var (w, p, _) = Sys2.World(t, data, tribe);
                var home = Base(w, p);
                var worker = w.SpawnUnit(w.Data.Extra.Start[tribe].Units[0], p, 8, 8);
                t.True(worker != null, $"{tribe}: a worker");
                var u = worker!;
                ResNode n = res == "wood" ? Tree(w, 26, 6) : res == "stone" ? Stone(w, 26, 6) : Bush(w, 26, 6);
                foreach (var r in Rules.RES) p[r] = 0;
                w.Order(u, new Order("gather") { Target = n });
                t.Equal("gather", u.Task.Type, $"{tribe} {res}: gathering");
                double cap = w.CarryCap(u, res);
                Sys2.Run(w, 150);
                var del = Ev(w, "delivered").Where(e => e.U("unit") == u).ToList();
                t.True(del.Count >= 2, $"{tribe} {res}: at least two loads delivered in 150 s ({del.Count})");
                // every load is the worker's capacity (stone and food in whole units: CPlayer.AddResource rounds)
                t.Near(del.Count * cap, p[res], 1e-6, $"{tribe} {res}: {del.Count} loads of {cap}");
                // the time of a load: AnimAction(work clip, loops, intro, outro) - the start part, the loops of beginWork x the
                // loop part, the end part (Harvest.usl:846, Mine.usl:300, GetFood.usl:343) -, the waits, the walks
                var A = u.Anim!;
                string? clip = res == "wood" ? A.Pick("hacking_dirt", "hacking", "chop_tree") : res == "stone" ? A.Pick("hacking_stone", "hacking") : A.Pick("harvesting_bush", "potter");
                double tf = u.Stats!.Tf > 0 ? u.Stats.Tf : 2.0, k = res == "food" ? 3 : 5;
                int loops = Math.Max(1, (int)Math.Truncate(k * (res == "wood" ? tf : tf * W_.NODE_TF)));
                double work = clip != null ? A.SleLength(clip, loops) : loops * 1.2, loopT = A.LoopLength(clip);
                var pu = A.Pick(res == "wood" ? "shoulder_pick_up" : "pick_up", "pick_up");
                double waitUp = pu != null ? Math.Min(1.6, A.Duration(pu)) : 0; if (res != "wood") waitUp = Math.Min(waitUp, 0.6);
                var pd = A.Pick(res == "wood" ? "shoulder_put_down" : "put_down", "belly_put_down", "put_down");
                double waitDown = pd != null ? Math.Min(1.2, A.Duration(pd)) : 0; if (res != "wood") waitDown = Math.Min(waitDown, 0.6);
                double period = del[^1].Time - del[^2].Time;
                double walk = period - work - waitUp - waitDown;
                double dist = Math.Max(0, home is Building hb ? hb.SurfDist(n.Pos.X, n.Pos.Z) : home.DistTo(n) - home.Radius) - n.Radius;
                t.Log($"{tribe} {res}: {del.Count} loads of {cap}, a load every {period:0.00} s ({work:0.00} s work: {loops} x {loopT:0.00} s loop part + start / end, {waitUp:0.0}+{waitDown:0.0} s waits, {walk:0.0} s walking {dist:0} m)");
                t.True(walk > 0 && walk < 2 * dist / Math.Min(u.Speed, 1.5) + 4, $"{tribe} {res}: the walks ({walk:0.0} s for twice {dist:0} m at {u.Speed:0.0} m/s)");
                t.True(Math.Abs(period - (work + waitUp + waitDown + walk)) < 1e-9, "consistent");
                if (res == "wood") t.Equal("timber", n.Type, $"{tribe}: the tree was felled");
            }
        }
    }

    public static void StorageFullAndCaps(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        var home = (Building)Base(w, p);
        var u = w.SpawnUnit("hu_worker", p, 8, 8)!;
        var n = Stone(w, 22, 4);
        // the caps: at least 300 per resource, the buildings' limits on top
        t.True(p.Caps["stone"] >= 300, "a storage cap");
        p["stone"] = p.Caps["stone"] - 5;
        w.Order(u, new Order("gather") { Target = n });
        Sys2.Run(w, 120);
        t.Equal(p.Caps["stone"], p["stone"], "filled up to the cap");
        t.True(Ev(w, "storagefull").Count >= 1, "the storage-full message");
        t.Equal("deliver", u.Task.Phase, "the worker waits at the storehouse with its load");
        t.True(u.Carry != null && u.Carry.Amount > 0, "and keeps what did not fit");
        // losing the storehouse cuts the stock down to the new limit
        p.Caps["stone"] = 1000; p["stone"] = 900;
        w.KillBuilding(home, null);
        t.Equal(300.0, p.Caps["stone"], "the cap back to 300");
        t.Equal(300.0, p["stone"], "the stock cut to it");
    }

    public static void GatherFactors(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        foreach (var r in Rules.RES) p[r] = 0;
        // a computer player's gather factor (CAiPlayer.AddResource) and the debug option multiply each load
        p.AiMods = new AiMods { Gather = 1.5 };
        t.Equal(0.0, w.Gain(p, "wood", 20), "all of it stored");
        t.Equal(30.0, p["wood"], "x 1.5");
        p.AiMods = null; p.Cheat = new PlayerCheat { Gather = 2 };
        t.Equal(0.0, w.Gain(p, "food", 20), "all of it stored");
        t.Equal(40.0, p["food"], "x 2 (debug option)");
        t.Equal(40.0, p.Gained["food"], "statistics");
        // what does not fit comes back divided by the factor (a remainder below one unit is dropped)
        p.Caps["stone"] = 50; p["stone"] = 40;
        t.Equal(5.0, w.Gain(p, "stone", 10), "(20 - 10) / 2 back");
    }

    public static void TreesFallAndDeplete(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        Base(w, p);
        var tr = Tree(w, 20, 0, 30);
        int blocked = tr.CellsBlocked!.Count(k => w.Nav.Block[k] != 0);
        t.True(blocked > 0, "the tree blocks its cells");
        var u = w.SpawnUnit("hu_worker", p, 8, 0)!;
        var other = Tree(w, 30, 10, 200);
        w.Order(u, new Order("gather") { Target = tr });
        Sys2.Run(w, 30);
        t.Equal("timber", tr.Type, "felled");
        t.Equal(1, Ev(w, "treefall").Count, "treefall event");
        t.True(tr.FallClip != null && tr.FallClip.StartsWith("chop_down_0"), "a fall clip " + tr.FallClip);
        t.Equal(2.2, tr.Radius, "a log's radius");
        Sys2.Run(w, 90);
        t.True(!tr.Alive, "the log is used up (30 wood)");
        t.True(Ev(w, "timbergone").Count == 1, "timbergone");
        t.True(u.Task.Type == "gather" && u.Task.Node == other, "the worker goes on to the next tree (64 m)");
    }

    public static void FieldAndSlaughterhouse(TestContext t)
    {
        foreach (var data in new[] { Sys2.Base, Sys2.Mirage })
        {
            var (w, p, _) = Sys2.World(t, data, "Hu");
            var home = Base(w, p);
            var fieldName = w.Data.Index.Keys.FirstOrDefault(n => w.Data.Info(n)!.Tribe == "Hu" && w.Data.Info(n)!.Type == "BLDG" && w.Data.Def(n, p) is { } d && d.Unlimited.Contains("food") && (d.Script == "CGrowingField" || d.Script == "CCornfield"));
            if (fieldName == null) { t.Log("no Hu field in " + data); continue; }
            var f = w.PlaceBuilding(fieldName, p, 30, 0)!;
            var u1 = w.SpawnUnit("hu_worker", p, 10, 5)!;
            var u2 = w.SpawnUnit("hu_worker", p, 10, -5)!;
            var u3 = w.SpawnUnit("hu_worker", p, 10, 0)!;
            p["food"] = 0;
            w.Order(new[] { u1, u2, u3 }, new Order("gather") { Target = f });
            t.Equal(w.FarmSlots(f), f.Workers.Count, $"{fieldName}: its slots taken ({w.FarmSlots(f)})");
            t.True(u3.Task.Type != "gather", "the third worker finds no place (no other field)");
            Sys2.Run(w, 120);
            t.True(p["food"] > 0, $"{fieldName}: food from the field ({p["food"]})");
            t.Log($"{fieldName} ({f.Def!.Script}): food {p["food"]}, grow step {f.GrowStep}, workers {u1.Task} {u2.Task}");
            t.True(f.GrowStep > 0 || p["food"] >= 100, "the field grows " + f.GrowStep);
            t.Equal(0.0, p["food"] % w.CarryCap(u1, "food"), "whole loads");
        }
    }

    public static void BuyResources(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        p["skulls"] = 150; p["wood"] = 0; p.Caps["wood"] = 300;
        t.Equal(null, w.BuyResource(p, "wood"), "bought");
        t.Equal(67.0, p["wood"], "100 skulls buy 67 wood (Math.round(100 / 1.5))");
        t.Equal(50.0, p["skulls"], "100 skulls paid");
        p["stone"] = 290; p.Caps["stone"] = 300;
        t.Equal(null, w.BuyResource(p, "stone"), "bought");
        t.Equal(295.0, p["stone"], "limited by free storage: 10 skulls -> 5 stone");
        p["skulls"] = 0;
        t.Equal("skulls", w.BuyResource(p, "food"), "no skulls");
    }

    public static void TradeCart(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu", size: 400);
        var friend = new Player(3, "Hu", new PlayerOpts { Rules = w.Data, Team = 1 });
        w.Players.Add(friend);
        var mk = w.Data.Index.Keys.FirstOrDefault(n => w.Data.Info(n)!.Tribe == "Hu" && (w.Data.Script(n) ?? "").Contains("CMarketplace"))
            ?? w.Data.Index.Keys.FirstOrDefault(n => w.Data.Info(n)!.Tribe == "Hu" && (w.Data.Script(n) ?? "").Contains("CWarehouse"));
        var cart = w.Data.Index.Keys.FirstOrDefault(n => w.Data.Info(n)!.Tribe == "Hu" && n.EndsWith("_cart"));
        if (mk == null || cart == null) t.Skip("no Hu market / cart");
        var home = w.PlaceBuilding(mk!, p, -60, 0)!;
        var far = w.PlaceBuilding(mk!, friend, 60, 0)!;
        var u = w.SpawnUnit(cart!, p, -45, 0);
        if (u == null) t.Skip("no cart model");
        foreach (var r in Rules.RES) friend[r] = 0;
        t.True(w.TryStartTrade(u!, far), "trade to the friend's market");
        Sys2.Run(w, 120);
        var tr = Ev(w, "traded");
        t.True(tr.Count >= 1, $"{tr.Count} trips");
        double D = Math.Sqrt((far.Pos.X - home.Pos.X) * (far.Pos.X - home.Pos.X) + (far.Pos.Z - home.Pos.Z) * (far.Pos.Z - home.Pos.Z));
        double win = Math.Max(1, 5 * Math.Pow(D / 50, 1.3219));
        t.Near(win, tr[0].Num("amount"), 1e-9, "win = max(1, 5 (D / 50)^1.32)");
        t.True(tr[0].B("building") == far, "paid at the market reached");
    }
}
