// Scenario tests of transport, naval and animals (src/Sim/Systems/Transport.cs, Naval.cs, Animals.cs) and of the moves
// that run over time (automatic moves, entrenching, camouflage, MIRAGE's jetpack flight, explosive rounds, exo suit):
// worlds on a flat map, on a synthetic coast (land x < 0, sea x > 10) and on the staged multiplayer map
// multi_2_jun_001 (skipped when it is not staged), stepped as the game steps them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys3WorldTests
{
    sealed class Env
    {
        public W_ W = null!; public Player P = null!, Q = null!; public Rules R = null!;
        public void Step(double sec) { int n = (int)Math.Round(sec / W_.STEP); for (int i = 0; i < n; i++) W.Step(); }
        public Unit U(string name, Player? p, double x, double z, int? lv = null) => W.SpawnUnit(name, p, x, z, lv, 0) ?? throw new SkipException("no model for " + name);
    }

    static Env Make(TestContext t, string data, string tribe, string qTribe, IWorldMap map, bool upgraded = true)
    {
        var (R, M) = Sys3Data.Load(t, data);
        var e = new Env { R = R, W = new W_(R, M, map, new WorldOptions { Seed = 11 }) };
        e.P = new Player(0, tribe, new PlayerOpts { Rules = R, Team = 0 });
        e.Q = new Player(1, qTribe, new PlayerOpts { Rules = R, Team = 1 });
        e.W.Players.Add(e.P); e.W.Players.Add(e.Q);
        if (upgraded) Sys3Data.Upgrade(e.P, R);
        foreach (var p in e.W.Players) { foreach (var r in new[] { "food", "wood", "stone" }) { p.Caps[r] = 99999; p.Res[r] = 2000; } p.MaxUnits = 200; p.CapsFixed = true; }   // (fixed storage limits: world.setCaps - a building put down does not cut them)
        return e;
    }

    /// <summary>land west of x = 0 (2 m high), a beach down to the sea at x = 10 (4 m deep), water level 0</summary>
    static FlatMap Coast() => new(256) { Water = 0, HeightFn = (x, z) => x < 0 ? 2 : x < 10 ? 2 - x * 0.6 : -4 };

    static double D(Entity a, double x, double z) => Math.Sqrt((a.Pos.X - x) * (a.Pos.X - x) + (a.Pos.Z - z) * (a.Pos.Z - z));

    // ================================================================== transport
    public static void LandTransportBoardAndUnload(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", new FlatMap(192));
        var W = E.W;
        var tri = E.U("hu_triceratops", E.P, -20, 0);
        t.Equal(4.0, W.Capacity(tri), "the triceratops carries 4");
        t.True(W.IsOpenTransport(tri), "an open transport (its riders shoot)");
        var men = Enumerable.Range(0, 5).Select(i => E.U("hu_warrior", E.P, -35 + i * 2, 8)).ToList();
        var beast = E.U("hu_mammoth", E.P, -30, -10);
        t.True(!W.CanBoard(beast, tri), "class 1 transports carry characters only");
        W.Order(men, new Order("board") { Target = tri });
        E.Step(15);
        var pax = tri.Passengers!;
        t.Equal(4, pax.Count, "four boarded");
        t.True(pax.All(u => u.Inside && u.InsideOf == tri && u.Task.Type == "inside"), "inside, with the inside task");
        t.True(!W.UHash.Near(tri.Pos.X, tri.Pos.Z, 10).Any(u => pax.Contains(u)), "passengers are out of the spatial hash");
        var fifth = men.First(u => !u.Inside);
        t.True(!W.CanBoard(fifth, tri) && fifth.Task.Type != "board", "the fifth finds it full");
        t.Equal(4, W.Events.Count(e => e.Type == "boarded"), "boarded events");
        // the passengers ride along
        W.Order(tri, Order.Move(-60, -30));
        E.Step(10);
        t.True(pax.All(u => u.Pos.X == tri.Pos.X && u.Pos.Z == tri.Pos.Z), "the riders move with the transport");
        // an order to a passenger is ignored, but "unload" gets it out
        var riders = pax.ToList();
        W.Order(tri, new Order("unload"));
        t.True(tri.Passengers!.Count == 0 && riders.All(u => !u.Inside && u.InsideOf == null && u.Task.Type == "idle"), "everybody out");
        t.True(riders.All(u => D(u, tri.Pos.X, tri.Pos.Z) < tri.Radius + 3.5 && W.Nav.IsFree(u.Pos.X, u.Pos.Z)), "around the transport on free ground");
        t.True(W.UHash.Near(tri.Pos.X, tri.Pos.Z, 15).Count(u => riders.Contains(u)) == 4, "back in the spatial hash");
    }

    public static void BunkerGarrison(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", new FlatMap(192));
        var W = E.W;
        var b = W.PlaceBuilding("hu_bunker", E.P, 0, 0, 0, true) ?? throw new SkipException("no bunker model");
        t.Equal(4.0, W.Capacity(b), "a bunker holds 4");
        var men = Enumerable.Range(0, 3).Select(i => E.U("hu_warrior", E.P, -20 + i * 2, 15)).ToList();
        var beast = E.U("hu_mammoth", E.P, -20, -15);
        t.True(!W.CanBoard(beast, b), "characters only");
        W.Order(men, new Order("board") { Target = b });
        E.Step(20);
        t.Equal(3, b.Passengers?.Count ?? 0, "garrisoned");
        t.True(W.UnloadOne(b, men[1]) && !men[1].Inside && b.Passengers!.Count == 2, "one out by the passengers' menu");
        W.UnloadAll(b, false);
        t.True(men.All(u => !u.Inside) && b.Passengers!.Count == 0, "all out");
        t.True(men.All(u => W.Nav.IsFree(u.Pos.X, u.Pos.Z)), "outside the walls");
    }

    public static void ShipBoardsAtTheShoreAndUnloads(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", Coast());
        var W = E.W;
        t.True(W.WaterNav != null, "a water grid");
        var ship = E.U("hu_transport_ship", E.P, 50, 0);
        t.True(ship.Naval && W.WaterNav!.IsFree(ship.Pos.X, ship.Pos.Z), "the ship is on the water");
        var men = new[] { E.U("hu_warrior", E.P, -40, 4), E.U("hu_warrior", E.P, -42, -4) };
        var beast = E.U("hu_mammoth", E.P, -45, 12);
        t.True(W.CanBoard(beast, ship), "class 2 transports carry animals too");
        W.Order(men.Append(beast), new Order("board") { Target = ship });
        for (int i = 0; i < 1200 && (ship.Passengers?.Count ?? 0) < 3; i++) W.Step();
        t.Equal(3, ship.Passengers?.Count ?? 0, "all three boarded (the ship came to the shore near them)");
        t.True(ship.Pos.X < 40, $"the idle ship sailed towards the passengers (x {ship.Pos.X:0.0})");
        // far out at sea nobody gets off
        W.Order(ship, Order.Move(100, 70));
        E.Step(30);
        W.UnloadAll(ship, false);
        t.Equal(3, ship.Passengers!.Count, "too far from the shore");
        t.True(W.Events.Any(e => e.Type == "msg" && e.Str("text")!.Contains("shore")), "the player is told");
        // back at the coast: out onto the land
        W.Order(ship, Order.Move(12, 20));
        for (int i = 0; i < 1200 && ship.Path.Count > 0; i++) W.Step();
        W.Order(ship, new Order("unload"));
        t.True(ship.Passengers!.Count == 0, "unloaded at the shore");
        foreach (var u in men.Append(beast)) t.Log($"{u.Name} at ({u.Pos.X:0.0}, {u.Pos.Z:0.0}), ship at ({ship.Pos.X:0.0}, {ship.Pos.Z:0.0})");
        t.True(men.Append(beast).All(u => !u.Inside && !W.IsWater(u.Pos.X, u.Pos.Z) && W.Nav.IsFree(u.Pos.X, u.Pos.Z)), "on dry land");
        // a sinking ship far from the shore takes its passengers with it
        var ship2 = E.U("hu_transport_ship", E.P, 100, -60);
        var sailor = E.U("hu_warrior", E.P, -40, -60);
        W.EnterTransport(sailor, ship2);
        W.UnloadAll(ship2, true);
        t.True(!sailor.Alive && !sailor.Inside, "a passenger far from the shore dies with the ship");
    }

    // ================================================================== naval
    /// <summary>
    /// Player report (MIRAGE mission 3): four ships sent to one place ended up exactly on top of each other, looking like
    /// one. Units on the very same spot (trained in one step at a harbour's spawn point, put there by a script) had no
    /// direction to part in and the separation skipped them for good; ordered alike they then sailed as one. And a group
    /// order whose places lie on the shore moved several of them to the same water cell.
    /// </summary>
    public static void ShipsOnOneSpotPartAndKeepApart(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", Coast());
        var W = E.W;
        t.True(W.WaterNav != null, "a water grid");
        double MinApart(List<Unit> l, out double need)
        {
            double md = 1e9; need = 0;
            for (int i = 0; i < l.Count; i++) for (int j = i + 1; j < l.Count; j++)
            { md = Math.Min(md, D(l[i], l[j].Pos.X, l[j].Pos.Z)); need = Math.Max(need, (l[i].Radius + l[j].Radius) * 0.8); }
            return md;
        }
        // the test data lacks most model files (radius 0.45): the ships get the radius a carrier's hull gives (about 4.5)
        foreach (var radius in new[] { 0.0, 4.5 })
        {
            List<Unit> Fleet() { var l = Enumerable.Range(0, 4).Select(_ => E.U("hu_transport_ship", E.P, 60, 0)).ToList(); if (radius > 0) foreach (var u in l) u.Radius = radius; return l; }
            // 1. on one spot, no order: they part
            var a = Fleet();
            t.True(a.All(u => u.Pos.X == a[0].Pos.X && u.Pos.Z == a[0].Pos.Z), "four ships on one spot");
            E.Step(10);
            t.True(MinApart(a, out var need) >= need, $"r {a[0].Radius}: idle ships on one spot part ({MinApart(a, out _):0.00} m >= {need:0.00})");
            // 2. each sent alone to the same point: they do not arrive as one
            foreach (var u in a) W.Order(u, Order.Move(100, 40));
            E.Step(60);
            t.True(MinApart(a, out need) >= need, $"r {a[0].Radius}: sent to one point, apart ({MinApart(a, out _):0.00} m >= {need:0.00})");
            foreach (var u in a) W.Kill(u, null);
            E.Step(1);
            // 3. a group order to the shore (most places on land): every ship gets its own place
            var b = Fleet();
            E.Step(10);
            var cmd = new PwRemake.Sim.Commands.GroupMoveCommand { Player = E.P.Id, Units = b.Select(u => u.Id).ToArray(), X = 6, Z = -30 };
            t.True(cmd.Apply(W) == null, "group order given");
            var goals = b.Select(u => u.Goal).ToList();
            t.Equal(4, goals.Distinct().Count(), "four different places");
            E.Step(60);
            t.True(MinApart(b, out need) >= need, $"r {b[0].Radius}: group order to the shore, apart ({MinApart(b, out _):0.00} m >= {need:0.00})");
            foreach (var u in b) W.Kill(u, null);
            E.Step(1);
        }
        // land units on one spot part too
        var men = Enumerable.Range(0, 5).Select(_ => E.U("hu_warrior", E.P, -40, 10)).ToList();
        E.Step(5);
        t.True(MinApart(men, out var nm) >= nm, $"warriors on one spot part ({MinApart(men, out _):0.00} m >= {nm:0.00})");
    }

    public static void FishingBoatFishesAndDelivers(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", Coast());
        var W = E.W;
        var harbour = W.PlaceBuilding("hu_harbour", E.P, 8, 30, 0, true) ?? throw new SkipException("no harbour model");
        var shoal = W.AddResource("fish", "food", 50, 70, 120, new ResOpts { Water = true });
        var boat = E.U("hu_fishing_boat", E.P, 40, 30);
        t.True(World_IsFisher(boat) && boat.Naval, "a fishing boat on the water");
        double food0 = E.P.Res["food"];
        t.True(W.StartFishing(boat, shoal), "starts fishing");
        bool sawNet = false;
        for (int i = 0; i < 2400 && E.P.Res["food"] < food0 + 100 - 1e-9; i++) { W.Step(); sawNet |= boat.Task.Phase == "net"; }
        t.True(sawNet, "the net went out");
        t.Near(food0 + 100, E.P.Res["food"], 1e-6, "two loads of 50 delivered");
        // the shoal (120) is fished out on the third load and the boat looks for the next one, finds none and stops
        for (int i = 0; i < 2400 && boat.Task.Type == "fish" && E.P.Res["food"] < food0 + 120 - 1e-9; i++) W.Step();
        t.True(!shoal.Alive || shoal.Amount <= 0.5 || shoal.Amount < 120 - 100, "the shoal is used");
        t.Near(food0 + 120, E.P.Res["food"], 1e-6, "all of its fish delivered");
        // (a used shoal goes with economy.js depleteNode - the hook of sys2's economy port; until then it stays)
        if (!shoal.Alive) t.Equal("idle", boat.Task.Type, "no shoal left: idle");
    }
    static bool World_IsFisher(Unit u) => W_.IsFisher(u);

    public static void MinesLaidAndTriggered(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Ninigi", "Hu", Coast());
        var W = E.W;
        var layer = E.U("ninigi_minelayer", E.P, 40, 0);
        var act = W.Data.Actions(E.P).FirstOrDefault(a => a.Results.Count > 0 && a.Results[0].Obj == "ninigi_mineship_mine");
        if (act == null) t.Skip("no mine action");
        t.Equal("place", W.LayWaterThing(layer, act!, -30, 0, false), "not on land");
        t.Equal(null, W.LayWaterThing(layer, act!, 60, 0, false), "a mine at sea");
        t.Equal("place", W.LayWaterThing(layer, act!, 63, 0, true), "not within 8 m of the next one");
        t.Equal(null, W.LayWaterThing(layer, act!, 60, 30, true), "a second one queued");
        double wood0 = E.P.Res["wood"], stone0 = E.P.Res["stone"];
        for (int i = 0; i < 2400 && W.Units.Count(u => u.Name == "ninigi_mineship_mine") < 2; i++) W.Step();
        var mines = W.Units.Where(u => u.Name == "ninigi_mineship_mine").ToList();
        t.Equal(2, mines.Count, "both mines laid");
        t.True(mines.All(m => m.Stationary && m.Stance == 3 && !m.CountsInPop), "mines stay, never fight, don't count");
        t.True(E.P.Res["wood"] + E.P.Res["stone"] < wood0 + stone0 || act!.Cost.Wood + act.Cost.Stone == 0, "they were paid");
        t.Equal("idle", layer.Task.Type, "done");
        // an enemy ship comes by: the nearer mine goes off after its fuse
        var enemy = E.U("hu_transport_ship", E.Q, 90, 0);
        double hp0 = enemy.Hp;
        W.Order(enemy, Order.Move(45, 0));
        for (int i = 0; i < 1200 && mines[0].Alive; i++) W.Step();
        t.True(!mines[0].Alive && mines[0].Blasted, "the mine blew up");
        t.True(enemy.Hp < hp0 || !enemy.Alive, "the ship is hurt");
        t.True(mines[1].Alive, "the other one waits");
        t.True(W.NavalCanTarget(enemy, mines[1]) && !W.NavalCanTarget(E.U("hu_warrior", E.Q, -20, 0), enemy), "a sword does not reach a ship");
    }

    public static void TorpedoTurtleHuntsShips(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Aje", "Hu", Coast());
        var W = E.W;
        var turtle = E.U("aje_torpedo_turtle", E.P, 40, 0);
        var ship = E.U("hu_transport_ship", E.Q, 90, 20);
        double hp0 = ship.Hp;
        W.Order(turtle, Order.Move(40, 80));
        t.Equal("idle", turtle.Task.Type, "the turtle obeys nothing but Kill");
        for (int i = 0; i < 1200 && turtle.Alive; i++) W.Step();
        t.True(!turtle.Alive && turtle.SilentDeath, "it exploded");
        t.True(ship.Hp < hp0 || !ship.Alive, "at the ship");
        t.True(W.Events.Any(e => e.Type == "camshake"), "with a blast");
        // left alone it dies after 45 s
        var t2 = E.U("aje_torpedo_turtle", E.P, 40, -60);
        E.Step(46);
        t.True(!t2.Alive, "its life is 45 s");
    }

    public static void FloatingHarbourSails(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Aje", "Hu", Coast());
        var W = E.W;
        var h = W.PlaceBuilding("aje_floating_harbour", E.P, 60, -40, 0, true) ?? throw new SkipException("no model");
        t.True(W.IsMovingHarbour(h), "a moving harbour");
        t.Equal("req", W.MoveHarbour(W.PlaceBuilding("hu_warehouse", E.P, -40, -40, 0, true)!, 0, 0), "a warehouse doesn't sail");
        t.Equal(null, W.MoveHarbour(h, 60, 40), "sails");
        t.True(h.Sail != null && !h.Blocking, "under way, not blocking");
        for (int i = 0; i < 2400 && h.Sail != null; i++) W.Step();
        t.True(h.Sail == null && h.Blocking, "stopped, blocking again");
        t.True(D(h, 60, 40) < 8, $"arrived ({h.Pos.X:0.0}, {h.Pos.Z:0.0})");
        // it fishes a shoal within 40 m by itself: 10 food every 5 s
        var shoal = W.AddResource("fish", "food", h.Pos.X + 15, h.Pos.Z, 100, new ResOpts { Water = true });
        double f0 = E.P.Res["food"];
        E.Step(21);
        t.Near(f0 + 40, E.P.Res["food"], 1e-6, "40 food in 21 s");
        t.Near(60, shoal.Amount, 1e-6, "from the shoal");
    }

    // ------------------------------------------------------------------ the staged multiplayer map
    sealed class SourceMap : IWorldMap
    {
        readonly MapSource S;
        public SourceMap(MapSource s) { S = s; Play = new PlayRect(s.Play.X0, s.Play.X1, s.Play.Z0, s.Play.Z1); }
        public double Size => S.Size;
        public PlayRect Play { get; }
        public double Height(double x, double z) => S.Height(x, z);
        public double? Water => S.Water;
        public string Setting => S.Setting;
        public double? StartTime => S.StartTime;
        public double[] WallGrid => S.WallGrid ?? new double[] { 4, 4 };
        public Func<double, double, bool>? Walkable => S.Walkable;
        public double MaxSlope => 32;
        public string Kind => "ula";
    }
    sealed class MapRulesOf : IMapRules
    {
        readonly Rules R;
        public MapRulesOf(Rules r) { R = r; }
        public string? ClassGfx(string name) => R.ClassGfx(name);
        public string? Script(string name) => R.Script(name);
        public double? ResourceValue(string section, string name) { double v = R.ResourceValue(section, name, 0); return v > 0 ? v : null; }
        public bool Exists(string name) => R.Exists(name);
        public string? StatsGfx(string name) => R.Stats(name, 1, null)?.Gfx;
        public string? NestSpecies(string nestClassLower) => R.Extra.Raw["nests"]?[nestClassLower]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? R.Extra.Raw["nests"]![nestClassLower]!.GetValue<string>() : null;
    }

    public static void ShipsOnMulti2Jun001(TestContext t)
    {
        string f = "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer/multi_2_jun_001.ula";
        if (!File.Exists(f)) t.Skip(f + " not staged");
        var (R, M) = Sys3Data.Load(t, RulesTestData.Base(t));
        var S = MapSource.OriginalSource(Ula.ParseUlaFile(f), new MapRulesOf(R), null, n => M.Has(n));
        if (S.Water == null) t.Skip("the map has no water");
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", new SourceMap(S));
        var W = E.W;
        t.True(W.WaterNav != null, "the map's sea");
        foreach (var fs in S.Fish) W.AddResource("fish", "food", fs.X, fs.Z, fs.Amount, new ResOpts { Water = true });
        foreach (var n in S.Nests.Where(n => !n.Swim)) W.AddWildNest(n);
        var shoal = W.Resources.FirstOrDefault(r => r.Water);
        if (shoal == null) t.Skip("no fish on the map");
        var wn = W.WaterNav!;
        var (bx, bz) = wn.Center(wn.NearestFree(wn.Idx(shoal.Pos.X + 30, shoal.Pos.Z), 40));
        var boat = E.U("hu_fishing_boat", E.P, bx, bz);
        t.True(W.StartFishing(boat, shoal), "a boat at the map's fish");
        for (int i = 0; i < 2400 && boat.Carry == null; i++) W.Step();
        t.Equal(50.0, boat.Carry?.Amount ?? 0, "it caught a load");
        // a transport ship sails along the coast and puts a warrior ashore
        var (sx, sz) = wn.Center(wn.NearestFree(wn.Idx(shoal.Pos.X, shoal.Pos.Z + 40), 40));
        var ship = E.U("hu_transport_ship", E.P, sx, sz);
        var shore = W.ShorePoint(ship, 400);
        if (shore == null) t.Skip("no land near the sea");
        var man = E.U("hu_warrior", E.P, shore.Value.X, shore.Value.Z);
        W.Order(man, new Order("board") { Target = ship });
        for (int i = 0; i < 4000 && !man.Inside; i++) W.Step();
        t.True(man.Inside && man.InsideOf == ship, "boarded at the map's shore");
        for (int i = 0; i < 2000 && W.ShorePoint(ship, 30) == null; i++) W.Step();
        W.UnloadAll(ship, false);
        t.True(!man.Inside && W.Nav.IsFree(man.Pos.X, man.Pos.Z), "and ashore again");
        E.Step(30);
        t.True(W.WildNests.Count == 0 || W.Units.Any(u => u.Alive && u.Wild && u.WildNest != null), "the map's nests have animals");
        t.Log($"water {S.Water:0.0} m, {S.Fish.Count} shoals, {S.Nests.Count} nests, {W.Units.Count(u => u.Wild && u.Alive)} wild animals after {W.Time:0} s");
    }

    // ================================================================== animals
    static MapNest Nest(string species, double x, double z, double max = 3, double rate = 5, double amount = -1) =>
        new() { Name = "nest_" + species, Species = species, X = x, Z = z, Max = max, Rate = rate, Amount = amount };

    public static void NestsSpawnAndAnimalsRoam(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", new FlatMap(256));
        var W = E.W;
        var n = W.AddWildNest(Nest("Iguanodon", 20, 20, max: 3, rate: 5));
        var n2 = W.AddWildNest(Nest("Wild_Boar", -40, -40, max: 4, rate: 5, amount: 2));
        t.True(n.Action.Contains(20, 35) && !n.Action.Contains(20, 45) && n.Safe.Contains(20, 28), "default areas: 20 m action, 10 m safe");
        E.Step(20);
        t.Equal(3, n.Members.Count, "three born (one every 5 s up to the maximum)");
        t.Equal(2, n2.Members.Count, "a nest of two animals spawns two");
        t.Equal(0.0, n2.Amount, "and is empty");
        var a = n.Members[0];
        t.True(a.Wild && a.WildNest == n && a.Flock == n.Flock && a.HomeX == 20, "a wild member of the nest and its flock");
        t.True(W.IsBaby(a) && a.Grow < 1 && a.MaxHp < a.Stats!.Hp && a.Babyness > 0.9, "a baby: weaker");
        var start = n.Members.Select(m => m.Pos).ToList();
        E.Step(90);
        t.True(n.Members.Select((m, i) => V3.Dist2D(m.Pos, start[i])).Max() > 2, "they roam");
        t.True(n.Members.All(m => D(m, 20, 20) < 30), "around their nest");
        t.True(n.Members.All(m => m.Task.Type == "roam"), "roaming");
        // a death: the nest's timer restarts and a new one is born
        W.Kill(n.Members[1], null);
        E.Step(7);
        t.Equal(3, n.Members.Count, "replaced");
        // growing up: after the class's growup time the baby is grown
        E.Step(a.Growup);
        t.True(!W.IsBaby(a) && a.Grow == 1 && a.Born == null && a.MaxHp == a.Stats!.Hp, "grown up");
    }

    public static void PeacefulAnimalFleesAndTires(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", new FlatMap(256));
        var W = E.W;
        var ig = E.U("Iguanodon", null, 0, 0);
        var man = E.U("hu_warrior", E.P, 6, 0);
        W.Order(ig, Order.Move(0, 0));            // (wild animals obey nobody)
        E.Step(1);
        W.AnimalHurt(ig, man);
        t.Equal("flee", ig.Task.Type, "a peaceful animal flees");
        t.Equal(man, ig.Task.FleeFrom, "from the attacker");
        double d0 = ig.DistTo(man);
        E.Step(8);
        t.True(ig.DistTo(man) > d0 + 10, $"it ran away ({d0:0.0} -> {ig.DistTo(man):0.0} m)");
        for (int i = 0; i < 1600 && ig.Task.Type == "flee"; i++) W.Step();
        t.True(ig.Task.Type == "roam", "the flight ends");
        t.True(ig.Fatigue > 0 || ig.DistTo(man) > 40, "tired from running, or safe");
        double f = ig.Fatigue;
        E.Step(10);
        t.True(f == 0 || ig.Fatigue < f, "fatigue recovers");
    }

    public static void NeutralAnimalsFightBackTogether(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", new FlatMap(256));
        var W = E.W;
        var boars = new[] { E.U("Wild_Boar", null, 0, 0), E.U("Wild_Boar", null, 5, 2), E.U("Wild_Boar", null, -4, 3) };
        var other = E.U("Iguanodon", null, 3, -5);
        var man = E.U("hu_warrior", E.P, 8, 0);
        W.AnimalHurt(boars[0], man);
        t.True(boars.All(b => b.Task.Type == "attack" && b.Task.Target == man), "the boar fights back and its kind joins");
        t.True(other.Task.Type != "attack", "other species stay out");
        // against a wild animal it can't take with its helpers it flees
        var rex = E.U("Tarbosaurus", null, 40, 40);
        var lone = E.U("Wild_Boar", null, 60, 60);
        W.AnimalHurt(lone, rex);
        t.Equal("flee", lone.Task.Type, "a lone boar flees from a tyrannosaur");
    }

    public static void DeathScaresTheHerd(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", new FlatMap(256));
        var W = E.W;
        var herd = new[] { E.U("Iguanodon", null, 0, 0), E.U("Iguanodon", null, 6, 4) };
        var raptor = E.U("Velociraptor", null, -6, 0);
        E.Step(1);
        W.AddScare(2, 0);
        t.True(W.Scares != null && W.Scares.Count == 1 && W.Scares[0].Targets.Count == 2, "the herd is frightened, the hunter not");
        t.True(herd.All(h => h.Task.Type == "roam" && h.Task.Phase == "walk" && h.Task.Run), "they run");
        t.True(herd.All(h => Math.Sqrt((h.Task.WalkTo!.Value.X - 2) * (h.Task.WalkTo.Value.X - 2) + h.Task.WalkTo.Value.Z * h.Task.WalkTo.Value.Z) > 100), "to twice the scare's radius");
        t.True(raptor.Task.Phase != "walk" || !raptor.Task.Run, "the hunter is not scared");
        E.Step(20);
        t.True(herd.All(h => D(h, 2, 0) > 40), "away from the place");
    }

    public static void HungryAnimalsFeed(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", new FlatMap(256));
        var W = E.W;
        for (int i = 0; i < 12; i++) W.AddResource("tree", "wood", Math.Cos(i) * 20, Math.Sin(i) * 20, 100);
        var herd = Enumerable.Range(0, 4).Select(i => E.U("Iguanodon", null, i * 4, 0)).ToList();
        bool fed = false, ate = false;
        for (int i = 0; i < 12000 && !(fed && ate); i++)
        {
            W.Step();
            foreach (var h in herd) { if (h.Task.Phase == "feed") fed = true; if (h.Task.Phase == "feed" && h.Task.FeedState == "feed") ate = true; }
        }
        t.True(fed && ate, "a hungry herbivore walks to food and eats");
        t.True(herd.Any(h => h.FedT != null), "its meal time is kept");
    }

    // ================================================================== moves over time
    public static void AutomaticMovesFire(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Hu", "Aje", new FlatMap(192));
        var W = E.W;
        var war = E.U("hu_warrior", E.P, 0, 0, 5);
        var foes = new[] { E.U("aje_warrior", E.Q, 0, -3), E.U("aje_warrior", E.Q, 2, -3), E.U("aje_warrior", E.Q, -2, -3) };
        if (!W.Data.Invented(E.P, "quake", "Hu")) t.Skip("quake not researched by the upgrades");
        var hp = foes.Select(f => f.Hp).ToArray();
        t.True(W.AutoMove(war, foes[0]), "an automatic move replaces the hit");
        var used = war.AutoMoves!.List.First(a => war.Cd.Get(a.Id) > W.Time);
        t.True(used.Id == "Quake" || used.Id == "Roar", $"Quake before Roar ({used.Id})");
        t.Near(W.Time + used.Time, war.Cd.Get(used.Id), 1e-9, "its cooldown");
        t.True(!W.AutoMove(war, foes[0]) || war.AutoMoves.List.Count(a => war.Cd.Get(a.Id) > W.Time) == 2, "not again while cooling down");
        E.Step(1.3);
        t.True(foes.Where((f, i) => f.Hp < hp[i] || !f.Alive).Count() >= 2 || war.Cs()!.Dmg == 0, "the bash hit the crowd");
        // the mammoth's stampede: only while chasing an enemy 10 m .. its sight away; ended by its first real attack
        var mam = E.U("hu_mammoth", E.P, 40, 40, 5);
        var prey = E.U("aje_warrior", E.Q, 40, 15);
        if (!W.Data.Invented(E.P, "mammoth_stampede", "Hu")) t.Skip("no stampede research");
        t.True(W.AutoMove(mam, prey, chasing: true), "stampede while chasing");
        t.Equal(30.0, mam.RageT, "30 s of rage");
        t.True(W.Effects.Any(f => f.Kind == "stampede" && f.Host == mam), "the stampede ticks");
        mam.RageHit = true;
        E.Step(2.1);
        t.True(mam.RageT == 0 && !W.Effects.Any(f => f.Kind == "stampede"), "ended by the attack");
    }

    public static void EntrenchAndCamouflage(TestContext t)
    {
        var E = Make(t, RulesTestData.Base(t), "Ninigi", "Hu", new FlatMap(192), upgraded: false);     // (an upgrade hides entrench)
        var W = E.W;
        var sp = E.U("ninigi_spearman", E.P, 0, 0, 5);
        var dig = W.MovesOf(sp).FirstOrDefault(a => a.Id == "entrench");
        if (dig == null || W.MoveCheck(sp, dig) != null) t.Skip("no entrench");
        t.Equal(null, W.UseMove(sp, dig!, null, null), "digs in");
        t.True(sp.Active("trapped") && sp.Task.Type == "entrench", "3 s digging");
        E.Step(3.1);
        t.True(sp.St.Camo.Has("entr"), "hidden");
        W.Order(sp, Order.Move(20, 0));
        t.True(!sp.St.Camo.Has("entr") && sp.Cd.Get("entrench") > W.Time + 29, "an order digs it out, 30 s until the next");
        t.Equal("target", W.UseMove(sp, dig!, null, null), "not again yet");
        // the Aje shaman's camouflage: on, off (then 15 s to wait)
        var E2 = Make(t, RulesTestData.Base(t), "Aje", "Hu", new FlatMap(192));
        var sh = E2.U("aje_shaman", E2.P, 0, 0, 5);
        var cam = E2.W.MovesOf(sh).FirstOrDefault(a => a.Id == "camouflage");
        if (cam == null || E2.W.MoveCheck(sh, cam) != null) t.Skip("no camouflage");
        t.Equal(null, E2.W.UseMove(sh, cam!, null, null), "on");
        t.True(sh.St.Camo.Has("aje") && sh.CamoGfx == "parasaurolophus", "looks like an animal");
        E2.W.Order(sh, Order.Move(10, 0));
        t.True(sh.St.Camo.Has("aje"), "walking keeps it");
        E2.W.Order(sh, new Order("stop"));
        t.True(!sh.St.Camo.Has("aje") && sh.CamoGfx == null, "another order ends it");
        t.Equal("target", E2.W.UseMove(sh, cam!, null, null), "15 s before the next");
        E2.Step(15.1);
        t.Equal(null, E2.W.UseMove(sh, cam!, null, null), "then again");
    }

    public static void MirageJetpackRoundsAndExoSuit(TestContext t)
    {
        var data = RulesTestData.Mirage(t);
        var E = Make(t, data, "Hu", "Aje", new FlatMap(192));
        var W = E.W;
        var jet = E.U("hu_jetpack_warrior", E.P, 0, 0, 5);
        t.True(W.SetFlying(jet, true) && jet.Flying && jet.LocalTT!.Has(MirageMoves.F.jet), "takes off");
        t.True(!W.SetFlying(jet, false), "not while rising");
        E.Step(3.6);
        t.Near(MirageMoves.FLY_HEIGHT, jet.LiftH, 1e-9, "13 m up");
        W.Order(jet, Order.Move(30, 0));
        E.Step(8);
        t.True(jet.Pos.X > 10, "flies");
        t.True(W.SetFlying(jet, false), "lands");
        E.Step(3.1);
        t.True(!jet.Flying && jet.LiftH == 0 && !jet.LocalTT.Has(MirageMoves.F.jet), "on the ground");
        // explosive rounds: a switch that costs the lumberjack 15 hit points a second
        var lj = E.W.SpawnUnit("seas_lumberjack_minigun", E.P, 20, 20, 5);
        if (lj != null)
        {
            double hp0 = lj.Hp;
            t.True(W.ExplosiveRounds(lj, true) && lj.Rounds, "on");
            E.Step(3.05);
            t.True(lj.Hp <= hp0 - 45 + 1e-9, "15 a second");
            t.True(W.ExplosiveRounds(lj, false) && !lj.Rounds && lj.Cd.Get("explosives_rounds") > W.Time + 59, "off: 60 s cooldown");
            t.True(!W.ExplosiveRounds(lj, true), "not before");
        }
        // Babbage's mechanization: 12 s up, the exo suit comes down; destroyed, Babbage climbs out with half his hit points
        var bab = W.SpawnUnit("babbage_s0", E.P, -30, -30, 5);
        if (bab == null) { t.Log("no model for babbage_s0"); return; }
        bab.Cd.Set("Babbage_Minigun_0", W.Time + 50);
        MirageMoves.UPGRADE_HOOKS["special_mechanization"](W, bab, null);
        t.True(bab.Untargetable && bab.Active("trapped"), "up into the sky");
        E.Step(12.1);
        var suit = W.Units.FirstOrDefault(u => u.Alive && u.Name == "special_mobile_suit");
        if (suit == null) { t.True(W.Units.Any(u => u.Alive && u.Name == "babbage_s0"), "without a suit model Babbage comes back"); return; }
        t.True(!bab.Alive && suit.Driver == "babbage_s0" && suit.Cd.Get("Babbage_ExoMinigun_0") > 0, "the suit has landed with his minigun's cooldown");
        W.Kill(suit, null); W.SuitDestroyed(suit);
        E.P.Heroes.Delete("babbage_s0");                // (combat.js kill takes a dead hero off the player's list; the stand-in doesn't)
        E.Step(2.6);
        var back = W.Units.FirstOrDefault(u => u.Alive && u.Name == "babbage_s0");
        t.True(back != null && Math.Abs(back.Hp - back.MaxHp / 2) < 1e-9, "Babbage climbs out with half his hit points");
    }
}
