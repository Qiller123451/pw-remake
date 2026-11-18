// The co-op campaign mode (docs/COOP.md), headless: definitions, seats in the world, the trigger seat mapping, hero
// split, enemy scaling, transfers, AI commanders, Shared and Co-op over the network, saves.
//
//   dotnet run --project tests/PwRemake.Tests -- Coop
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Coop;
using PwRemake.Sim.Game;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class CoopTests
{
    // ======================================================================================== the rig
    /// <summary>a co-op campaign start: the mission's definition from game/coop, seats as (kind, tribe), the humans on
    /// their seats' slots (GameStart.Seats); null definition = skip</summary>
    internal static GameStart CoopStart(TestContext t, string cfgId, int mission, (string Kind, string Tribe)[] seats, int coopDifficulty = 0, Dictionary<string, int>? heroes = null, ulong seed = 1)
    {
        var c = CampaignTests.LoadCfgPublic(cfgId);
        if (c == null) t.Skip($"configuration {cfgId} not found");
        var def = CoopDefinition.Load(cfgId, mission);
        if (def == null) t.Skip($"no co-op definition of mission {mission}");
        var start = new GameStart { Configuration = cfgId, Mission = mission, Difficulty = 1, Seed = seed, Tribe = seats[0].Tribe };
        var folder = MissionSession.MissionFolderOf(start, c!.Config);
        if (folder == null) t.Skip($"mission {mission} not in {cfgId}");
        int i = 0;
        var cs = seats.Select(x => new CoopSeat { Seat = ++i, Kind = x.Kind, Tribe = x.Tribe, Name = (x.Kind == "ai" ? "AI " : "Human ") + i }).ToList();
        start.Coop = CoopPlan.Make(def!, folder!, cs, coopDifficulty, heroes);
        start.Seats = start.Coop.Seats.Where(x => !x.Ai).Select((x, k) => new Seat { Name = x.Name, Player = x.Slot, Peer = k }).ToList();
        return start;
    }
    internal static MissionSession Session(TestContext t, GameStart start, IMissionPresenter? presenter = null)
    {
        var c = CampaignTests.LoadCfgPublic(start.Configuration)!;
        return MissionSession.Create(start, c.Config, new SessionOptions
        {
            Presenter = presenter ?? (start.Seats != null ? new LockstepPresenter(start) : null), Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data,
        });
    }
    static PwRemake.Sim.Scripting.TriggerParams Q(params (string K, string V)[] kv) => new(kv.Select(x => new KeyValuePair<string, string>(x.K, x.V)));

    // ======================================================================================== definitions
    public static void CoopDefinitionSchema(TestContext t)
    {
        var d = CoopDefinition.FromLua(@"return { version = 1, nobuild = true, heroes = { 'Cole_s0', 'Bela_s0' },
            extend = { south = 192, seed = 7 }, resources = { { class = 'stone_big', x = 1, z = 2 } },
            seats = { [2] = { start = { x = 10, z = -20, dir = 1.5 }, army = { 'hu_worker', 'aje_worker' }, extend = { east = 64 }, resources = { { kind = 'wood', x = 3, z = 4 } } },
                      [3] = { ally = 3, start_resources = { food = 500 } } },
            allies = { usable = { 3 } }, scaling = { count = 0.6, health = 0.25, damage = 0.15, exclude = { 7 }, waves = false },
            triggers = { give_to = 2 }, notes = 'n' }");
        t.True(d.NoBuild, "nobuild");
        t.Equal(2, d.Heroes.Count, "heroes");
        t.Equal(192.0, d.Extend!["south"]!.GetValue<double>(), "extend kept as data");
        t.Equal(1, d.Resources!.Count, "resources kept as data");
        t.Equal(10.0, d.Seats[2].X!.Value, "seat 2 x");
        t.Equal(1.5, d.Seats[2].Dir, "seat 2 dir");
        t.Equal("list", d.Seats[2].Army, "army list");
        t.Equal(2, d.Seats[2].ArmyList!.Count, "army classes");
        t.Equal(64.0, d.Seats[2].Extend!["east"]!.GetValue<double>(), "per-seat extend");
        t.Equal(1, d.Seats[2].MapResources!.Count, "per-seat resources");
        t.Equal(3, d.Seats[3].Ally!.Value, "ally seat");
        t.Equal(500.0, d.Seats[3].Resources!["food"], "start resources");
        t.Equal(0.6, d.Scaling.Count, "count");
        t.True(!d.Scaling.Waves && d.Scaling.Spawns, "waves off, spawns on");
        t.Equal(7, d.Scaling.Exclude[0], "exclude");
        t.Equal(2, d.GiveTo, "give_to");
        t.Equal(3, d.MaxSeats, "max seats");
        // the JSON round trip (GameStart carries it)
        var again = CoopDefinition.FromJson(d.Raw);
        t.Equal(d.Seats[2].X, again.Seats[2].X, "round trip");
        // the files of the test missions
        foreach (var m in new[] { 2, 3, 11 }) t.True(CoopDefinition.Load("base", m) is { } dm && dm.Supports(3), $"game/coop definition of mission {m}");
        t.True(!CoopDefinition.Exists("base", 23), "mission 23 (MIRAGE's only) has none in the base game (greyed out for mode 2)");
        // scaling factors
        var sc = new CoopScaling(new CoopScalingDef(), 3, 1);
        t.Near(1 + CoopScaling.DefaultCount * 2 * 1.5, sc.Count, 1e-9, "units ×2.5 at 3 seats, Hard");
        t.Near(1 + CoopScaling.DefaultHealth * 2 * 1.5, sc.Health, 1e-9, "health");
        t.Near(1 + CoopScaling.DefaultDamage * 2 * 1.5, sc.Damage, 1e-9, "damage");
        var s2 = new CoopScaling(new CoopScalingDef(), 2, 0);
        t.Equal(1.5, s2.Count, "2 seats Normal");
        t.Equal("1,2,1,2", string.Join(",", Enumerable.Range(0, 4).Select(_ => s2.Units(1, 1))), "fractions carry over");
        t.Equal(1.0, new CoopScaling(new CoopScalingDef(), 1, 2).Count, "one seat: nothing");
    }

    public static void CoopHeroSplit(TestContext t)
    {
        var def = CoopDefinition.FromLua("return { heroes = { 'Cole_s0', 'Bela_s0', 'Stina_s0' }, seats = { [2] = { start = { x = 0, z = 0 } }, [3] = { start = { x = 0, z = 0 } } } }");
        CoopSeat S(int n, string k) => new() { Seat = n, Kind = k };
        var h2 = CoopPlan.DefaultHeroes(def, new[] { S(1, "human"), S(2, "human") });
        t.Equal("1,2,1", string.Join(",", def.Heroes.Select(h => h2[h])), "2 humans, 3 heroes: seat 1 gets two");
        var h3 = CoopPlan.DefaultHeroes(def, new[] { S(1, "human"), S(2, "human"), S(3, "human") });
        t.Equal("1,2,3", string.Join(",", def.Heroes.Select(h => h3[h])), "3 humans: one each");
        var ha = CoopPlan.FixHeroes(def, new[] { S(1, "human"), S(2, "ai"), S(3, "human") }, new Dictionary<string, int> { ["Cole_s0"] = 2, ["Bela_s0"] = 3 });
        t.Equal("1,3,1", string.Join(",", def.Heroes.Select(h => ha[h])), "no hero on an AI seat; the host's choice kept, the rest round the humans");
    }

    // ======================================================================================== seats in the world
    public static void CoopThreeSeatsFreeSpots(TestContext t)
    {
        var start = CoopStart(t, "base", 3, new[] { ("human", "Hu"), ("human", "Aje"), ("ai", "Ninigi") });
        var s = Session(t, start);
        var W = s.World; var co = s.Campaign.Coop!;
        t.Equal(3, co.Seats.Count, "seats");
        t.Equal(0, co.Seats[0].Slot, "seat 1 = the mission's human player");
        var p2 = co.Seats[1].Player; var p3 = co.Seats[2].Player;
        t.True(co.Seats[1].New && co.Seats[2].New && p2.Id >= 5 && p3.Id >= 5 && p2.Id != p3.Id, $"new slots {p2.Id} {p3.Id}");
        t.Equal("Aje", p2.Tribe, "seat 2 faction"); t.Equal("Ninigi", p3.Tribe, "seat 3 faction");
        t.True(!p2.Ai && p3.Ai && p3.ControlledBy == ControlledBy.Computer, "human / AI commander");
        foreach (var a in co.Seats) foreach (var b in co.Seats) if (a != b) t.Equal(2, a.Player.Relation(b.Player), $"seat {a.Seat} -> {b.Seat} allied");
        var enemy = W.Players.First(p => p.Id == 1);
        t.True(enemy.IsEnemy(p2) && p2.IsEnemy(enemy), "seat 2 hostile to the enemy both ways");
        t.Equal(1, W.Buildings.Count(b => b.Owner == p3 && b.Name == "ninigi_fireplace"), "seat 3 main building");
        t.True(W.Units.Any(u => u.Owner == p2 && u.Name == "aje_resource_collector"), "seat 2 (Dustriders): resource collector");
        t.True(W.Units.Count(u => u.Owner == p2 && u.IsWorker) >= 3, "seat 2 workers (the mission's start army in its tribe)");
        t.True(!W.Units.Any(u => u.Owner == p3 && u.Def!.Unique), "no hero for the AI commander");
        t.Equal(150.0, p2["wood"], "seat 2 resources = the mission's");
        // heroes: 2 humans, 3 heroes - Cole and Stina seat 1, Béla seat 2
        t.True(W.Units.Any(u => u.Name == "Bela_s0" && u.Owner == p2), "Béla is seat 2's");
        t.True(W.Units.Any(u => u.Name == "Cole_s0" && u.Owner == co.Seats[0].Player), "Cole is seat 1's");
        t.True(co.Seats[2].Commander != null, "AI commander");
        // the scaling (3 seats, Normal)
        t.True(co.Scaled.Has(1) && !co.Scaled.Has(3), "the enemies are scaled, the map's ally is not");
        t.Near(co.Scaling.Health, enemy.CoopHealth, 1e-9, "enemy health factor");
        t.True(co.GarrisonExtra > 0, $"garrison units added: {co.GarrisonExtra}");
        // the world runs
        s.Run(30);
        t.True(!s.Over, "the mission runs");
        t.Equal(0, s.Engine!.Errors.Count, "no trigger errors: " + string.Join("; ", s.Engine.Errors.Take(3)));
    }

    /// <summary>a start with a definition of the test's own (Lua)</summary>
    internal static GameStart CoopStartWith(TestContext t, string cfgId, int mission, string defLua, (string Kind, string Tribe)[] seats, int coopDifficulty = 0)
    {
        var c = CampaignTests.LoadCfgPublic(cfgId);
        if (c == null) t.Skip($"configuration {cfgId} not found");
        var start = new GameStart { Configuration = cfgId, Mission = mission, Difficulty = 1, Seed = 1, Tribe = seats[0].Tribe };
        var folder = MissionSession.MissionFolderOf(start, c!.Config);
        if (folder == null) t.Skip($"mission {mission} not in {cfgId}");
        int i = 0;
        start.Coop = CoopPlan.Make(CoopDefinition.FromLua(defLua), folder!, seats.Select(x => new CoopSeat { Seat = ++i, Kind = x.Kind, Tribe = x.Tribe, Name = "S" + i }), coopDifficulty);
        start.Seats = start.Coop.Seats.Where(x => !x.Ai).Select((x, k) => new Seat { Name = x.Name, Player = x.Slot, Peer = k }).ToList();
        return start;
    }

    /// <summary>a definition with an ally seat on mission 2 (the core's first test definition; the mission's real one,
    /// game/coop/common/02.lua, uses no ally - the holy mammoths are no tribe and the triggers hand objects to and from
    /// player 3). Coordinates are the original map's (docs/COOP.md "Maps").</summary>
    internal const string AllyDef02 = @"return { version = 1, heroes = { 'Cole_s0', 'Bela_s0', 'Stina_s0' },
        seats = { [2] = { ally = 3, army = 'default' }, [3] = { start = { x = 788, z = 616, dir = 0 }, army = 'default' } },
        allies = { usable = { 3 } }, scaling = { count = 0.5, health = 0.2, damage = 0.1 }, triggers = { give_to = 1 },
        notes = 'Test definition: seat 2 takes the ally 3 (its start location), seat 3 starts beside it.' }";

    /// <summary>mission 2: seat 2 takes the allied player 3 (its start location; an AI commander there: no mission brain);
    /// two seats only: nothing of seat 3 is placed</summary>
    public static void CoopAllySeatTwoSeats(TestContext t)
    {
        foreach (var kind in new[] { "ai", "human" })
        {
            var start = CoopStartWith(t, "base", 2, AllyDef02, new[] { ("human", "Hu"), (kind, "Aje") });
            var s = Session(t, start);
            var W = s.World; var co = s.Campaign.Coop!;
            var p3 = W.Players.First(p => p.Id == 3);
            t.Equal(2, co.Seats.Count, "two seats");
            t.Equal(3, co.Seats[1].Slot, "seat 2 = the ally's slot");
            t.True(!co.Seats[1].New && co.Seats[1].Player == p3, "the ally's player");
            t.Equal("Aje", p3.Tribe, "the seat's faction");
            t.Equal(kind == "ai", p3.Ai, "AI commander / human");
            t.True(W.Brains.Get(3) == null, "no mission brain on the seat");
            t.True(W.Units.Any(u => u.Owner == p3 && u.Name == "aje_resource_collector"), "the ally (owning nothing) got a base at its start location");
            t.Equal(5, W.Players.Count, "no player for the empty seat 3");
            t.Near(1 + CoopScaling.DefaultCount, co.Scaling.Count, 1e-9, "scaling counts the seats in use");
            // 2 seats, 3 heroes: seat 1 two of them when seat 2 is a human, all three when it is an AI commander
            int mine = W.Units.Count(u => u.Owner?.Id == 0 && u.Def!.Unique), theirs = W.Units.Count(u => u.Owner == p3 && u.Def!.Unique);
            t.Equal(kind == "ai" ? 3 : 2, mine, "heroes of seat 1");
            t.Equal(kind == "ai" ? 0 : 1, theirs, "heroes of seat 2");
            s.Run(20);
            t.Equal(0, s.Engine!.Errors.Count, "no trigger errors");
            t.Log($"mission 2, ally seat ({kind}): {W.Units.Count(u => u.Owner == p3)} units, {co.GarrisonExtra} garrison extras, scaling {co.Scaling}");
        }
        // an ally an empty seat would have taken stays the map's computer ally (with its brain)
        var def = "return { heroes = { 'Cole_s0' }, seats = { [2] = { start = { x = 788, z = 616 } }, [3] = { ally = 3 } }, allies = { usable = { 3 } } }";
        var s2 = Session(t, CoopStartWith(t, "base", 2, def, new[] { ("human", "Hu"), ("human", "Hu") }));
        var a3 = s2.World.Players.First(p => p.Id == 3);
        t.True(a3.Ai && s2.World.Brains.Get(3) != null && s2.Campaign.Coop!.SeatOfSlot(3) == null, "player 3: the map's ally with its brain");
    }

    /// <summary>mission 11 (no-build): the heroes divided, no bases</summary>
    public static void CoopNoBuildHeroes(TestContext t)
    {
        var s = Session(t, CoopStart(t, "base", 11, new[] { ("human", "Ninigi"), ("human", "Hu"), ("human", "Aje") }));
        var W = s.World; var co = s.Campaign.Coop!;
        foreach (var seat in co.Seats)
        {
            t.Equal(1, W.Units.Count(u => u.Owner == seat.Player && u.Def!.Unique), $"seat {seat.Seat}: one hero");
            t.Equal(0, W.Buildings.Count(b => b.Owner == seat.Player), $"seat {seat.Seat}: no base");
        }
        t.True(W.Units.Any(u => u.Name == "special_eusmilus" && u.Owner == co.Seats[2].Player), "Stina (her sabre-tooth) is seat 3's");
        // two humans + an AI commander: the AI gets none, one human two
        var s2 = Session(t, CoopStart(t, "base", 11, new[] { ("human", "Ninigi"), ("ai", "Hu"), ("human", "Aje") }));
        var c2 = s2.Campaign.Coop!;
        t.Equal(0, s2.World.Units.Count(u => u.Owner == c2.Seats[1].Player && u.Def!.Unique), "no hero for the AI commander");
        t.Equal(2, s2.World.Units.Count(u => u.Owner == c2.Seats[0].Player && u.Def!.Unique), "seat 1: two");
        t.Equal(1, s2.World.Units.Count(u => u.Owner == c2.Seats[2].Player && u.Def!.Unique), "seat 3: one");
        // the hero's death loses as in the original (mission 11: Trigger_GameOver, a hero dead)
        var bela = s2.World.Units.First(u => u.Name == "Bela_s0");
        s2.World.SetHp(bela, 0); s2.World.Kill(bela, null);
        s2.Run(8);
        t.True(s2.Over && !s2.Won, "a hero of another seat dead: the mission is lost for everybody");
    }

    /// <summary>the trigger engine's "the player" on the seats (docs/COOP.md "Triggers")</summary>
    public static void CoopTriggerMapping(TestContext t)
    {
        var s = Session(t, CoopStart(t, "base", 3, new[] { ("human", "Hu"), ("human", "Aje"), ("ai", "Ninigi") }));
        var W = s.World; var co = s.Campaign.Coop!; var H = s.Host;
        var p1 = co.Seats[0].Player; var p2 = co.Seats[1].Player; var p3 = co.Seats[2].Player;
        // conditions count every seat: units of the player (REGN / DEAD query), by the mission's class (swapped per seat)
        int w1 = W.Units.Count(u => u.Owner == p1 && u.IsWorker), w2 = W.Units.Count(u => u.Owner == p2 && u.IsWorker), w3 = W.Units.Count(u => u.Owner == p3 && u.IsWorker);
        var workers = H.Select(Q(("obj_owner", "0"), ("obj_class", "hu_worker"), ("obj_type", "CHTR")), "");
        t.Equal(w1 + w2 + w3, workers.Count, $"'hu_worker of player 0' finds every seat's workers ({w1}+{w2}+{w3})");
        var seat3Unit = W.Units.First(u => u.Owner == p3).Rec!.Id;
        t.True(H.Matches(seat3Unit, Q(("obj_owner", "0")), ""), "a seat 3 unit matches 'of player 0' (ISFG, UNTT)");
        t.True(H.OwnerMatches(p2.Id, 0) && !H.OwnerMatches(0, p2.Id), "the player = every seat; a seat's own id = only itself");
        t.True(H.CountFinished("hu_fireplace", 0, null) >= 1, "BLDG: seat 3's fireplace counts for the player");
        double wood = p1["wood"] + p2["wood"] + p3["wood"];
        t.Equal(Math.Floor(wood), H.PlayerAttribute(0, "wood")!.Value, "PLYR: the seats' stocks together");
        t.Equal((double)(p1.Units + p2.Units + p3.Units), H.PlayerAttribute(0, "units")!.Value, "PLYR units: every seat");
        t.True(H.PlayerObjects(0).Count == W.Units.Count(u => co.IsSeatSlot(u.Owner?.Id ?? -1) && !u.Parked && u.Rec != null) + W.Buildings.Count(b => co.IsSeatSlot(b.Owner?.Id ?? -1) && b.Rec != null), "PLDE: the player's objects are every seat's");
        // actions that give go to give_to (seat 1)
        double w0 = p1["wood"], w2b = p2["wood"];
        H.ChangeResource(0, "wood", "+100", false);
        t.Equal(w0 + 100, p1["wood"], "RSRC +100 to seat 1"); t.Equal(w2b, p2["wood"], "not to seat 2");
        int id = H.Spawn("hu_warrior", 0, 300, 300, new PwRemake.Sim.Scripting.SpawnOptions());
        t.Equal(p1.Id, H.Info(id)!.Owner, "a spawn for the player is seat 1's");
        // actions that set something for the player: every seat
        H.SetRelation(4, 0, 0); H.SetRelation(0, 4, 0);
        var p4 = W.Players.First(p => p.Id == 4);
        foreach (var seat in co.Seats) t.True(seat.Player.IsEnemy(p4) && p4.IsEnemy(seat.Player), $"DIPL with the player: seat {seat.Seat} too");
        t.Equal(2, p1.Relation(p2), "the seats stay allies");
        H.SetPopulationLimit(0, 77);
        t.True(co.Seats.All(x => x.Player.PopMax == 77), "POPL: every seat");
        int rv = H.RevealArea(0, 600, 600, 20, 0, -1);
        t.True(rv >= 0, "SFOW for the player: revealed");
        // the enemies' spawns come more often (scaled)
        int before = W.Units.Count(u => u.Owner?.Id == 1);
        H.Spawn("aje_warrior", 1, 700, 700, new PwRemake.Sim.Scripting.SpawnOptions());
        int made = W.Units.Count(u => u.Owner?.Id == 1) - before;
        t.True(made >= 2, $"a scaled enemy's spawn makes {made} (×{co.Scaling.Count})");
        H.Spawn("aje_warrior", 1, 700, 720, new PwRemake.Sim.Scripting.SpawnOptions { Replace = true });
        t.Equal(made + 1, W.Units.Count(u => u.Owner?.Id == 1) - before, "REPL: one");
    }

    /// <summary>units handed between seats (CoopGiveCommand): heroes only between humans, no buildings</summary>
    public static void CoopTransfers(TestContext t)
    {
        var s = Session(t, CoopStart(t, "base", 3, new[] { ("human", "Hu"), ("human", "Aje"), ("ai", "Ninigi") }));
        var W = s.World; var co = s.Campaign.Coop!;
        var p1 = co.Seats[0].Player; var p2 = co.Seats[1].Player; var p3 = co.Seats[2].Player;
        var worker = W.Units.First(u => u.Owner == p1 && u.IsWorker);
        var cole = W.Units.First(u => u.Owner == p1 && u.Name == "Cole_s0");
        var house = W.Buildings.First(b => b.Owner == p3);
        W.Enqueue(new CoopGiveCommand { Player = p1.Id, Units = new[] { worker.Id }, ToSeat = 3 });
        W.Enqueue(new CoopGiveCommand { Player = p1.Id, Units = new[] { cole.Id }, ToSeat = 3 });
        W.Enqueue(new CoopGiveCommand { Player = p3.Id, Units = new[] { house.Id }, ToSeat = 1 });
        s.Run(0.2);
        t.Equal(p3, worker.Owner, "a worker to the AI commander");
        t.Equal(p1, cole.Owner, "a hero never to an AI commander");
        t.Equal(p3, house.Owner, "an AI seat gives nothing / buildings stay");
        W.Enqueue(new CoopGiveCommand { Player = p1.Id, Units = new[] { cole.Id }, ToSeat = 2 });
        s.Run(0.2);
        t.Equal(p2, cole.Owner, "a hero between humans");
        t.True(s.Host.Select(Q(("obj_name", "NA"), ("obj_owner", "0"), ("obj_class", "Cole_s0")), "").Count == 1, "the triggers still find Cole as the player's");
        // resources: the tribute between seats
        double f = p2["food"];
        W.Enqueue(new PwRemake.Sim.Commands.TributeCommand { Player = p1.Id, To = p2.Id, Res = "food", Amount = 50 });
        s.Run(0.2);
        t.Equal(f + 50, p2["food"], "tribute to a co-op seat");
    }

    /// <summary>the AI commander's commands: attack a point (reached), defend a point (holds), follow a unit</summary>
    public static void CoopCommanderCommands(TestContext t)
    {
        var s = Session(t, CoopStart(t, "base", 3, new[] { ("human", "Hu"), ("human", "Aje"), ("ai", "Ninigi") }));
        var W = s.World; var co = s.Campaign.Coop!;
        var p1 = co.Seats[0].Player; var ai = co.Seats[2]; var cmd = ai.Commander!;
        var sl = s.Campaign.StartLocations[ai.Slot]!;
        for (int i = 0; i < 5; i++) { var (x, z) = W.FreeSpot(sl.X + 8 + i * 2, sl.Z + 8); W.Spawn("ninigi_warrior", ai.Player, x, z); }
        t.True(cmd.Army().Count >= 5, "its army");
        // attack a point 50 m away
        var (ax, az) = W.FreeSpot(sl.X + 45, sl.Z + 20);
        W.Enqueue(new CoopCommand { Player = p1.Id, Seat = 3, Kind = "attack", HasPoint = true, X = ax, Z = az });
        for (int i = 0; i < 90 * 20 && !cmd.Reached; i++) s.Step();
        t.True(cmd.Reached, $"attack point reached ({cmd}) at {W.Time:0} s");
        // defend a point; an enemy comes: they fight it, then stand there again
        // (south of the seat: mission 3's Amazon war camp, three seats, stands north of seat 3)
        var (dx, dz) = W.FreeSpot(sl.X + 15, sl.Z + 25);
        W.Enqueue(new CoopCommand { Player = p1.Id, Seat = 3, Kind = "defend", HasPoint = true, X = dx, Z = dz });
        s.Run(40);
        var army = cmd.Army();
        t.True(army.Count(u => Math.Sqrt((u.Pos.X - dx) * (u.Pos.X - dx) + (u.Pos.Z - dz) * (u.Pos.Z - dz)) <= CoopCommander.DEFEND_RADIUS + 5) * 2 >= army.Count, "defend: the army stands at the point");
        // (on free ground next to the point: the seats' places changed with the 2026-11-03 redesign)
        var (ex, ez) = W.FreeSpot(dx + 20, dz + 15);
        var foe = W.Spawn("aje_warrior", W.Players.First(p => p.Id == 1), ex, ez) as Unit;
        double hp0 = foe!.Hp;
        bool fought = false;
        for (int i = 0; i < 10 * 20 && !fought; i++) { s.Step(); fought = army.Any(u => u.Task.Type == "attack" && u.Task.Target == foe) || !foe.Alive || foe.Hp < hp0; }
        t.True(fought, $"defend: the enemy is fought (foe at {ex:0},{ez:0}, point {dx:0},{dz:0}; army tasks: {string.Join(" ", army.Select(u => u.Task.Type))})");
        for (int i = 0; i < 60 * 20 && foe.Alive; i++) s.Step();
        t.True(!foe!.Alive, $"defend holds: the enemy is dead (hp {foe.Hp:0} of {hp0:0} at {foe.Pos.X:0},{foe.Pos.Z:0}; army tasks: {string.Join(" ", army.Select(u => u.Alive ? u.Task.Type : "dead"))})");
        // follow a human's unit
        // (a unit of seat 1 near the AI seat: on mission 3's co-op map seat 3 starts on new land 700 m from seat 1)
        var (lx, lz) = W.FreeSpot(sl.X + 25, sl.Z + 10);
        var lead = (Unit)W.Spawn("hu_worker", p1, lx, lz)!;
        W.Enqueue(new CoopCommand { Player = p1.Id, Seat = 3, Kind = "follow", Units = new[] { lead.Id } });
        var (fx, fz) = W.FreeSpot(lead.Pos.X + 40, lead.Pos.Z + 30);
        W.Order(lead, Order.Move(fx, fz));
        s.Run(45);
        army = cmd.Army();
        int near = army.Count(u => Math.Sqrt((u.Pos.X - lead.Pos.X) * (u.Pos.X - lead.Pos.X) + (u.Pos.Z - lead.Pos.Z) * (u.Pos.Z - lead.Pos.Z)) <= CoopCommander.FOLLOW_RADIUS * 2);
        t.True(near * 2 >= army.Count, $"follow: {near} of {army.Count} beside the unit (lead {(lead.Alive ? "alive" : "dead")} at {lead.Pos.X:0},{lead.Pos.Z:0}; army at {string.Join(" ", army.Select(u => $"{u.Pos.X:0},{u.Pos.Z:0}:{u.Task.Type}"))})");
        // aggression and the refusals
        W.Enqueue(new CoopCommand { Player = p1.Id, Seat = 3, Kind = "aggression", Value = 0 });
        W.Enqueue(new CoopCommand { Player = ai.Slot, Seat = 3, Kind = "retreat" });
        s.Run(0.2);
        t.Equal(0, cmd.Aggression, "aggression set");
        t.Equal("follow", cmd.Order, "an AI seat gives no commands");
        W.Enqueue(new CoopCommand { Player = p1.Id, Seat = 3, Kind = "retreat" });
        s.Run(60);
        t.Equal("none", cmd.Order, "retreat: home, then no command");
    }

    /// <summary>"Both AI commanders" (the panel): one CoopCommand with Seats goes to both AI commanders in the same step -
    /// both attack the point and get there, both defend, the aggression of both; refusals: a human seat in the set, an AI
    /// seat as the giver (nothing applied)</summary>
    public static void CoopCommanderBoth(TestContext t)
    {
        var s = Session(t, CoopStart(t, "base", 3, new[] { ("human", "Hu"), ("ai", "Aje"), ("ai", "Ninigi") }));
        var W = s.World; var co = s.Campaign.Coop!;
        var p1 = co.Seats[0].Player; var a2 = co.Seats[1]; var a3 = co.Seats[2];
        var c2 = a2.Commander!; var c3 = a3.Commander!;
        var sl2 = s.Campaign.StartLocations[a2.Slot]!; var sl3 = s.Campaign.StartLocations[a3.Slot]!;
        // (the seats start ~800 m apart on mission 3's co-op map: both armies stand by seat 3's base, so both can get there)
        for (int i = 0; i < 5; i++)
        {
            var (x, z) = W.FreeSpot(sl3.X + 8 + i * 2, sl3.Z + 8); W.Spawn("aje_warrior", a2.Player, x, z);
            (x, z) = W.FreeSpot(sl3.X + 8 + i * 2, sl3.Z + 12); W.Spawn("ninigi_warrior", a3.Player, x, z);
        }
        t.True(c2.Army().Count >= 5 && c3.Army().Count >= 5, $"their armies ({c2.Army().Count}, {c3.Army().Count})");
        static double D(Unit u, double x, double z) => Math.Sqrt((u.Pos.X - x) * (u.Pos.X - x) + (u.Pos.Z - z) * (u.Pos.Z - z));
        // attack a point: one command, both commanders, the same step
        var (ax, az) = W.FreeSpot(sl3.X + 45, sl3.Z + 20);
        var both = new CoopCommand { Player = p1.Id, Seat = 2, Seats = new[] { 2, 3 }, Kind = "attack", HasPoint = true, X = ax, Z = az };
        W.Enqueue(both);
        s.Run(0.2);
        t.Equal(null, W.CommandResults.Last(x => x.Command == both).Reason, "accepted");
        t.True(c2.Order == "attack" && c3.Order == "attack", $"both attack ({c2}; {c3})");
        t.True(c2.Since == c3.Since && c2.By == 1 && c3.By == 1, "in the same step, by seat 1");
        t.True(c2.Point!.Value.Dist(new Pt(ax, az)) < 0.01 && c3.Point!.Value.Dist(new Pt(ax, az)) < 0.01, "the same point");
        for (int i = 0; i < 90 * 20 && !(c2.Reached && c3.Reached); i++) s.Step();
        t.True(c2.Reached && c3.Reached, $"both got there ({c2}; {c3}) at {W.Time:0} s");
        t.True(c2.Army().Count(u => D(u, ax, az) <= CoopCommander.REACHED_RADIUS) * 2 >= c2.Army().Count && c3.Army().Count(u => D(u, ax, az) <= CoopCommander.REACHED_RADIUS) * 2 >= c3.Army().Count, "both armies at the point");
        // defend a point
        var (dx, dz) = W.FreeSpot(sl3.X + 15, sl3.Z + 25);
        W.Enqueue(new CoopCommand { Player = p1.Id, Seat = 2, Seats = new[] { 2, 3 }, Kind = "defend", HasPoint = true, X = dx, Z = dz });
        s.Run(40);
        t.True(c2.Order == "defend" && c3.Order == "defend", "both defend");
        foreach (var c in new[] { c2, c3 })
        {
            var army = c.Army();
            t.True(army.Count(u => D(u, dx, dz) <= CoopCommander.DEFEND_RADIUS + 5) * 2 >= army.Count, $"seat {c.Seat.Seat}: the army stands at the defended point");
        }
        // the aggression of both
        W.Enqueue(new CoopCommand { Player = p1.Id, Seat = 2, Seats = new[] { 2, 3 }, Kind = "aggression", Value = 2 });
        s.Run(0.2);
        t.True(c2.Aggression == 2 && c3.Aggression == 2, "both aggressive");
        // refusals: a human seat among them (nothing applied, not even to the AI commanders), an AI seat as the giver
        var bad = new CoopCommand { Player = p1.Id, Seat = 1, Seats = new[] { 1, 2, 3 }, Kind = "retreat" };
        var byAi = new CoopCommand { Player = a2.Slot, Seat = 2, Seats = new[] { 2, 3 }, Kind = "retreat" };
        W.Enqueue(bad); W.Enqueue(byAi);
        s.Run(0.2);
        t.Equal("commander", W.CommandResults.Last(x => x.Command == bad).Reason, "a human seat in the set: refused");
        t.Equal("seat", W.CommandResults.Last(x => x.Command == byAi).Reason, "an AI seat gives no commands");
        t.True(c2.Order == "defend" && c3.Order == "defend", "nothing applied");
        // one of them alone still works next to it
        W.Enqueue(new CoopCommand { Player = p1.Id, Seat = 3, Kind = "economy" });
        s.Run(0.2);
        t.True(c2.Order == "defend" && c3.Order == "economy", "a command to one leaves the other alone");
        // no command for both
        W.Enqueue(new CoopCommand { Player = p1.Id, Seat = 2, Seats = new[] { 2, 3 }, Kind = "clear" });
        s.Run(0.2);
        t.True(c2.Order == "none" && c3.Order == "none", "both cleared");
    }

    /// <summary>a saved co-op game loads to the same world (the commands of the commander and the transfers in it)</summary>
    public static void CoopSaveLoad(TestContext t)
    {
        var start = CoopStart(t, "base", 3, new[] { ("human", "Hu"), ("human", "Aje"), ("ai", "Ninigi") }, coopDifficulty: 1, seed: 77);
        var c = CampaignTests.LoadCfgPublic("base")!;
        (MissionSession S, PresenterTap Tap, GameRecorder Rec) Make(GameStart st, SaveGame? of)
        {
            var tap = new PresenterTap(new LockstepPresenter(st)) { DeterministicInner = true };
            if (of != null) SaveReplay.Prepare(of, tap);
            var s = MissionSession.Create(st, c.Config, new SessionOptions { Presenter = tap, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, Flags = SimFlags.Game });
            return (s, tap, new GameRecorder(s.World, s.StateHash, tap));
        }
        var a = Make(start, null);
        var W = a.S.World; var co = a.S.Campaign.Coop!;
        a.S.Run(5);
        W.Enqueue(new CoopCommand { Player = 0, Seat = 3, Kind = "economy" });
        W.Enqueue(new CoopGiveCommand { Player = 0, Units = new[] { W.Units.First(u => u.Owner?.Id == 0 && u.IsWorker).Id }, ToSeat = 2 });
        a.S.Run(25);
        var save = a.Rec.Capture(start, SimFlags.Game);
        var file = Path.Combine(Path.GetTempPath(), $"pw_coop_{Guid.NewGuid():N}.pwsave");
        save.Write(file);
        var loaded = SaveGame.Read(file);
        t.True(loaded.Start.Coop != null && loaded.Start.Coop.Seats.Count == 3 && loaded.Start.Coop.Definition != null, "the save carries the co-op start and the definition");
        var b = Make(loaded.Start, loaded);
        var rep = new SaveReplay(loaded, b.S.World, b.S.StateHash, b.Tap);
        rep.Run(); rep.End();
        t.True(!rep.Diverged, $"the replay follows the save ({rep.Problem})");
        t.Equal(a.S.StateHash(), b.S.StateHash(), "the loaded world is the saved one");
        t.Equal("economy", b.S.Campaign.Coop!.Seats[2].Commander!.Order, "the commander's command came back");
        for (int i = 0; i < 400; i++) { a.S.Step(); b.S.Step(); }
        t.Equal(a.S.StateHash(), b.S.StateHash(), "the same world 20 s after loading");
        File.Delete(file);
    }

    /// <summary>a "Both AI commanders" command in a saved game and its replay: saved as one command with its seats, loads to
    /// the same world with both commanders on it, the same world on; the same game twice gives the same hash; the JSON of
    /// the command (network, saves) keeps the seats, an older command without them reads as one seat</summary>
    public static void CoopBothSaveReplay(TestContext t)
    {
        var start = CoopStart(t, "base", 3, new[] { ("human", "Hu"), ("ai", "Aje"), ("ai", "Ninigi") }, coopDifficulty: 1, seed: 31);
        var c = CampaignTests.LoadCfgPublic("base")!;
        (MissionSession S, PresenterTap Tap, GameRecorder Rec) Make(GameStart st, SaveGame? of)
        {
            var tap = new PresenterTap(new LockstepPresenter(st)) { DeterministicInner = true };
            if (of != null) SaveReplay.Prepare(of, tap);
            var s = MissionSession.Create(st, c.Config, new SessionOptions { Presenter = tap, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, Flags = SimFlags.Game });
            return (s, tap, new GameRecorder(s.World, s.StateHash, tap));
        }
        (MissionSession S, PresenterTap Tap, GameRecorder Rec) Play()
        {
            var a = Make(start, null);
            var W = a.S.World; var co = a.S.Campaign.Coop!;
            var sl = a.S.Campaign.StartLocations[co.Seats[2].Slot]!;
            a.S.Run(5);
            W.Enqueue(new CoopCommand { Player = 0, Seat = 2, Seats = new[] { 2, 3 }, Kind = "defend", HasPoint = true, X = sl.X + 20, Z = sl.Z });
            a.S.Run(10);
            W.Enqueue(new CoopCommand { Player = 0, Seat = 2, Seats = new[] { 2, 3 }, Kind = "aggression", Value = 0 });
            a.S.Run(15);
            return a;
        }
        var a = Play();
        var cs = a.S.Campaign.Coop!.Seats;
        t.True(cs[1].Commander!.Order == "defend" && cs[2].Commander!.Order == "defend" && cs[1].Commander!.Aggression == 0 && cs[2].Commander!.Aggression == 0, "both carried it out");
        t.Equal(a.S.StateHash(), Play().S.StateHash(), "the same game twice: the same world");
        var save = a.Rec.Capture(start, SimFlags.Game);
        var file = Path.Combine(Path.GetTempPath(), $"pw_coopboth_{Guid.NewGuid():N}.pwsave");
        save.Write(file);
        var loaded = SaveGame.Read(file);
        var saved = loaded.Commands.Select(PwRemake.Sim.Commands.CommandCodec.Decode).OfType<CoopCommand>().ToList();
        t.Equal(2, saved.Count, "two commands saved (one per press, not one per seat)");
        t.True(saved.All(x => x.Seats.SequenceEqual(new[] { 2, 3 })), "with both seats");
        var b = Make(loaded.Start, loaded);
        var rep = new SaveReplay(loaded, b.S.World, b.S.StateHash, b.Tap);
        rep.Run(); rep.End();
        t.True(!rep.Diverged, $"the replay follows the save ({rep.Problem})");
        t.Equal(a.S.StateHash(), b.S.StateHash(), "the loaded world is the saved one");
        var bs = b.S.Campaign.Coop!.Seats;
        t.True(bs[1].Commander!.Order == "defend" && bs[2].Commander!.Order == "defend", "both commanders' command came back");
        for (int i = 0; i < 400; i++) { a.S.Step(); b.S.Step(); }
        t.Equal(a.S.StateHash(), b.S.StateHash(), "the same world 20 s after loading");
        File.Delete(file);
        // the JSON (the network sends the same): the seats travel; a command without them (an older save) is one seat's
        var j = PwRemake.Sim.Commands.CommandCodec.Encode(new CoopCommand { Player = 0, Seat = 2, Seats = new[] { 2, 3 }, Kind = "retreat" });
        var back = (CoopCommand)PwRemake.Sim.Commands.CommandCodec.Decode(System.Text.Json.Nodes.JsonNode.Parse(j.ToJsonString())!);
        t.True(back.Seats.SequenceEqual(new[] { 2, 3 }) && back.To().SequenceEqual(new[] { 2, 3 }), "the seats in the JSON");
        var old = (CoopCommand)PwRemake.Sim.Commands.CommandCodec.Decode(System.Text.Json.Nodes.JsonNode.Parse("{\"t\":\"CoopCommand\",\"c\":{\"Player\":0,\"Seat\":3,\"Kind\":\"economy\"}}")!);
        t.True(old.Seats.Length == 0 && old.To().SequenceEqual(new[] { 3 }), "an older command: its one seat");
    }

    /// <summary>the same co-op start twice: the same game (scaling, commanders, garrisons)</summary>
    public static void CoopDeterminism(TestContext t)
    {
        ulong Run()
        {
            var s = Session(t, CoopStart(t, "base", 2, new[] { ("human", "Hu"), ("ai", "Ninigi"), ("human", "Aje") }, coopDifficulty: 2, seed: 9));
            s.Run(60);
            return s.StateHash();
        }
        t.Equal(Run(), Run(), "same hash");
    }

    public static void CoopExplore(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_COOP_EXPLORE") == null) t.Skip("PW_COOP_EXPLORE");
        foreach (var m in new[] { 2, 3, 11 })
        {
            var s = CampaignTests.Session(t, "base", m);
            var W = s.World; var h = s.Human!;
            var sl = s.Campaign.StartLocations[h.Id];
            t.Log($"mission {m}: map {W.Map.Size} human {h.Id} {h.Tribe} start {sl?.X:0},{sl?.Z:0}");
            foreach (var u in W.Units.Where(u => u.Owner == h)) t.Log($"  unit {u.Name} unique {u.Def?.Unique} at {u.Pos.X:0},{u.Pos.Z:0}");
            foreach (var p in W.Players) { var l = s.Campaign.StartLocations[p.Id]; t.Log($"  player {p.Id} {p.Tribe} rel0 {h.Relation(p)} start {l?.X:0},{l?.Z:0} units {W.Units.Count(u => u.Owner == p)} blds {W.Buildings.Count(b => b.Owner == p)}"); }
            if (sl == null) continue;
            for (int r = 60; r <= 180; r += 60)
                for (int a = 0; a < 8; a++)
                {
                    double x = sl.X + Math.Cos(a * Math.PI / 4) * r, z = sl.Z + Math.Sin(a * Math.PI / 4) * r;
                    bool free = W.Nav.IsFree(x, z) && W.PlacementOf("hu_fireplace", x, z, 0, h).Ok;
                    if (free) t.Log($"  free {x:0},{z:0} (r {r}, dir {a})");
                }
        }
    }
}
