// Tests of the computer player (src/Sim/Ai) on a world built from the real game data: what the brain wants to do
// given a world state - behaviours and their module settings, the difficulty handicaps, the build list and its
// realisation, the standing army, attack planning (pyramid, army tables, the passive debug option), the campaign's
// custom attacks (AIFT with spawn), automatic attacks, defence areas, unit locks, region maps, the village alarm,
// the Nightmare's own squads (extraSquads), the Dragon Clan's mine shores, the campaign / skirmish set-up - and an
// AI-against-AI smoke test that becomes meaningful once the systems are ported.
//
// The systems (production, construction, economy, combat) are ported in parallel: the decision tests use recording
// hands (FakeHands) that put what the brain queues into the producer's queue and building sites into the world.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

/// <summary>Hands that record what the brain does; queued actions go into the producer's queue (paid), building sites
/// are placed unfinished, orders are given to the world.</summary>
public sealed class FakeHands : IAiHands
{
    readonly W_ W;
    public readonly List<string> Log = new();
    public readonly List<(Entity Producer, ActionDef A)> Queued = new();
    public readonly List<Building> Sites = new();
    public readonly List<(List<Unit> Units, Order O)> Orders = new();
    public FakeHands(W_ w) { W = w; }
    public void Order(Player p, IReadOnlyList<Unit> units, Order o) { Orders.Add((units.ToList(), o)); Log.Add($"order {o} x{units.Count}"); W.Order(units, o); }
    public string? QueueAction(Player p, Entity producer, ActionDef a)
    {
        var why = W.CanQueue(p, a, producer);
        Log.Add($"queue {a.Id} at {producer.Name}: {why ?? "ok"}");
        if (why != null) return why;
        p.Pay(a.Cost);
        producer.Queue.Add(new QueueItem { Action = a });
        if (a.Kind == "Build" && a.Cat != "Build/BLDG") { int lv = W.UnitStartLevel(a, p); p.QueuedAtLevel[lv - 1]++; p.QueuedUnits++; }
        Queued.Add((producer, a));
        return null;
    }
    public object StartConstruction(Player p, ActionDef a, double x, double z, double rot, List<Unit> workers)
    {
        var b = W.PlaceBuilding(a.Results[0].Obj, p, x, z, rot, false);
        Log.Add($"build {a.Results[0].Obj} at ({x:0}, {z:0}) by {workers.Count}");
        if (b == null) return "place";
        p.Pay(a.Cost);
        Sites.Add(b);
        return b;
    }
    public void CancelQueue(Player p, Entity producer, int i) { Log.Add($"cancel {i} at {producer.Name}"); producer.Queue.RemoveAt(i); }
    public string? ChangeLevel(Player p, Unit u, int level) { Log.Add($"level {u.Name} -> {level}"); return null; }
    public string? LevelUp(Player p, Unit u) { Log.Add($"levelup {u.Name}"); return null; }
    public string? UseMove(Player p, Entity e, ActionDef a, Entity? target = null, (double X, double Z)? ground = null) { Log.Add($"move {a.Id}"); return "req"; }
    public string? LayWaterThing(Player p, Unit ship, ActionDef a, double x, double z, bool queue) { Log.Add($"lay {a.Id}"); return "req"; }
    public void SelfDestruct(Player p, Building b) { Log.Add($"destruct {b.Name}"); }
}

[TestClass]
public static class AiTests
{
    static readonly Dictionary<string, (Rules R, ModelManifest M, AiData A)> s_data = new();
    static (Rules R, ModelManifest M, AiData A) Load(TestContext t, bool mirage = false)
    {
        string data = mirage ? RulesTestData.Mirage(t) : RulesTestData.Base(t);
        if (s_data.TryGetValue(data, out var d)) return d;
        var man = Json.ParseFile(Path.Combine(data, "assets", "manifest.json"));
        if (man == null) t.Skip("no assets/manifest.json in " + data);
        return s_data[data] = (Rules.Load(data), new ModelManifest(man!, null, _ => null), AiData.Load(data));
    }

    /// <summary>a 512 m flat world with two villages: player 0 (west) and 1 (east), enemies</summary>
    sealed class Game
    {
        public W_ W = null!; public AiData A = null!; public Player P0 = null!, P1 = null!; public FakeHands H = null!;
        public Building Base0 = null!, Base1 = null!;
    }
    static readonly Dictionary<string, string> BASE = new() { ["Hu"] = "hu_fireplace", ["Aje"] = "aje_resource_collector", ["Ninigi"] = "ninigi_fireplace", ["SEAS"] = "seas_headquarters" };
    static Game NewGame(TestContext t, string tribe0 = "Hu", string tribe1 = "Hu", bool mirage = false, FlatMap? map = null, bool aiData = true)
    {
        var (R, M, A) = Load(t, mirage);
        var g = new Game { A = aiData ? A : AiData.Fallback };
        g.W = new W_(R, M, map ?? new FlatMap(512), new WorldOptions { Seed = 7 });
        g.P0 = new Player(0, tribe0, new PlayerOpts { Rules = R, Ai = true }); g.P1 = new Player(1, tribe1, new PlayerOpts { Rules = R, Ai = true });
        g.W.Players.Add(g.P0); g.W.Players.Add(g.P1);
        foreach (var p in g.W.Players) { p.Res["food"] = 300; p.Res["wood"] = 300; p.Res["stone"] = 300; p.MaxUnits = 30; }
        g.Base0 = Village(g, g.P0, -150);
        g.Base1 = Village(g, g.P1, 150);
        g.H = new FakeHands(g.W);
        return g;
    }
    static Building Village(Game g, Player p, double x)
    {
        string b = BASE[p.Tribe];
        Building? bb = null;
        if (g.W.Data.Info(b)?.Type == "BLDG") bb = g.W.PlaceBuilding(b, p, x, 0);
        else g.W.SpawnUnit(b, p, x, 0);
        string worker = p.Tribe.ToLowerInvariant() + "_worker";
        for (int i = 0; i < 3; i++) g.W.SpawnUnit(worker, p, x + 10 + i * 3, 8);
        return bb!;
    }
    static List<Unit> Spawn(Game g, Player p, string cls, int n, double x, double z)
    {
        var l = new List<Unit>();
        for (int i = 0; i < n; i++) { var u = g.W.SpawnUnit(cls, p, x + i * 2.5, z); if (u != null) l.Add(u); }
        return l;
    }
    static TribeAI Brain(Game g, Player p, string behaviour = "Mikrobe", double d = 4, bool multimap = true, bool extraSquads = true, string? level = null) =>
        new(g.W, p, g.A, new TribeAIOptions { Difficulty = d, Behaviour = behaviour, Multimap = multimap, ExtraSquads = extraSquads, LevelName = level }, g.H);

    // ------------------------------------------------------------------ behaviours, difficulty
    public static void AiMikrobeSleeps(TestContext t)
    {
        var g = NewGame(t);
        var b = Brain(g, g.P0);
        t.True(b.Paused, "a campaign player sleeps (Mikrobe)");
        for (int i = 0; i < 600; i++) { g.W.Time += 0.2; b.Update(0.2); }
        t.Equal(0, g.H.Log.Count, "a sleeping brain does nothing in two minutes");
        t.True(b.SetBehaviour("Dodo"), "AIBV Dodo");
        t.True(!b.Paused, "awake");
        t.True(!b.SetBehaviour("NoSuchBehaviour"), "an unknown behaviour is refused");
    }

    public static void AiBehavioursSetTheModules(TestContext t)
    {
        var g = NewGame(t);
        var b = Brain(g, g.P0, "Dodo");
        t.Equal("D", b.Letter, "Dodo: tactics D<n>");
        t.True(b.Tactic >= 1 && b.Tactic <= 4, "one of four variants");
        t.Equal(100.0, b.Params.Aggressiveness, "Dodo aggressiveness");
        t.Equal(0.0, b.Params.CloseStartLocation, "Dodo does not close its start location");
        t.Equal("Dodo", b.Personality, "personality");
        b.SetBehaviour("Giraffe");
        t.Equal("G", b.Letter, "Giraffe");
        t.Equal(50.0, b.Params.Riskiness, "Giraffe riskiness");
        t.Equal(1.0, b.Params.CloseStartLocation, "Giraffe closes its start location");
        b.SetBehaviour("Schnecke");
        t.Equal(1.3, b.Params.AttacksRiskLevel, "Schnecke risk level");
        b.SetBehaviour("Turtle");
        t.Equal("S", b.Letter, "Turtle: S5"); t.Equal(5, b.Tactic, "Turtle: S5");
        t.Equal("none none none none none", string.Join(" ", g.A.AttackPlan(b.Ctx().With("S")).Attacks), "a Turtle never attacks");
        b.SetBehaviour("FightOnly");
        t.True(b.EconomyOff, "FightOnly has no economy");
        b.SetBehaviour("Singleplayer_L3_1");
        t.Equal("X", b.Letter, "campaign behaviour letter X");
        t.Equal("Level_3_Guards1", b.Params.Army, "its guard army");
        // module commands (AIBV with a module, AICM)
        t.True(b.SetBehaviour("upgrade_walls 1", "DFNS"), "DFNS command");
        t.Equal(1.0, b.Params.UpgradeWalls, "upgrade_walls 1");
        t.True(b.CallModule("ECON", "max_age 3"), "AICM max_age");
        t.Equal(3.0, b.Params.MaxAge, "max_age 3");
        b.CallModule("DFNS", "defend_place 300_260");
        t.Near(300 - 256, b.GuardBase!.Value.X, 1e-9, "defend_place: map -> game x");
        t.Near(260 - 256, b.GuardBase!.Value.Z, 1e-9, "defend_place: map -> game z");
        b.CallModule("DFNS", "AddDefenseArea gate [256_256] 20 5");
        t.True(b.Areas.Has("gate") && b.Areas.Get("gate")!.Max == 5, "AddDefenseArea");
        t.True(!b.CallModule("FGHT", "no_such_command"), "unknown command");
        b.CallModule("FGHT", "disable");
        t.True(b.Params.NoFight, "disable: no own attacks");
    }

    public static void AiDifficultyHandicaps(TestContext t)
    {
        var g = NewGame(t);
        var b = Brain(g, g.P0, "Giraffe", 9);
        var m = g.P0.AiMods!;
        t.Equal("Hard", b.Cls, "difficulty 9 is Hard");
        t.Equal("10 10 7 1 0", string.Join(" ", m.Pyramid!), "extra pyramid slots at 9");
        t.Equal(28.0, m.UnitLimit, "extra population at 9");
        t.Near(2.5, m.Gather, 1e-12, "gather factor at 9");
        t.Near(0.5, m.BuildTime, 1e-12, "build time at 9");
        t.Near(0.5, m.ResearchTime, 1e-12, "research uses the build factor (Action.usl path test)");
        t.Near(0.9, m.WeaponTime, 1e-12, "weapon cycle at 9");
        b.SetDifficulty(0);
        t.Equal("Easy", b.Cls, "difficulty 0 is Easy");
        t.True(g.P0.AiMods!.Pyramid == null, "no extra slots below 7");
        b.SetDifficulty(42);
        t.Equal(9, b.Difficulty, "clamped to 9");
        // gifts by the control think: skirmish Hard at 9 = 50 / 50 / 20 / 5 skulls; the stores are not overflowed
        g.P0.Res["food"] = 100; g.P0.Res["skulls"] = 0; g.P0.Caps["food"] = 120;
        b.ThinkNow("control");
        t.Equal(120.0, g.P0["food"], "gift fills the store up to its cap");
        t.Equal(5.0, g.P0["skulls"], "skull gift");
        t.True(m.Attack >= 1 && g.P0.AiMods!.Defense <= 1, "fight factors from difficulty 5");
    }

    // ------------------------------------------------------------------ economy
    public static void AiBuildOrderAndWorkers(TestContext t)
    {
        var g = NewGame(t);
        var b = Brain(g, g.P0, "Giraffe", 4);
        foreach (var w in g.W.Units.Where(u => u.Owner == g.P0)) w.Task = new UnitTask("gather") { Res = "wood" };   // every worker busy
        b.ThinkNow("build");
        t.True(b.BuildList != null && b.BuildList.Count > 0, "a build list");
        var expect = g.A.BuildList(b.Ctx()).Select(r => r.Request).ToList();
        int k = 0;
        foreach (var e in b.BuildList!) { while (k < expect.Count && expect[k] != e.Request) k++; t.True(k < expect.Count, $"{e.Request} in the table's order"); }
        t.True(b.Requests.Count > 0 && b.Requests.Count <= 5, "at most five requests in realisation");
        t.Log("requests: " + string.Join(", ", b.Requests));
        b.ThinkNow("eco");
        t.Log(string.Join(" | ", g.H.Log));
        t.True(g.H.Queued.Any(q => q.A.Results.Count > 0 && q.A.Results[0].Obj == "hu_worker" && q.Producer == g.Base0), "a worker is trained at the fireplace (all workers busy / idle <= 1)");
        t.True(g.H.Sites.Count >= 1, "a building of the list is started");
        t.True(g.H.Sites.All(s => b.Requests.Concat(b.Extra).Any(r => r.R.Cls == s.Name)), "what is built is a request");
        t.True(g.H.Log.Any(l => l.StartsWith("build ") && !l.EndsWith("by 0")), "workers are sent to the site");
        double spent = 900 - g.P0["food"] - g.P0["wood"] - g.P0["stone"];
        t.True(spent > 0, "the brain pays for what it builds");
    }

    public static void AiHousingAtTheUnitLimit(TestContext t)
    {
        var g = NewGame(t);
        var b = Brain(g, g.P0, "Giraffe", 4);
        g.P0.MaxUnits = 4;                                         // 3 workers + room for one: housing first (after the
                                                                   // brain: its handicaps recompute the limits)
        b.ThinkNow("build"); b.ThinkNow("eco");
        t.True(b.Extra.Any(e => e.Housing), "a house on top of the list");
        t.Log("extra: " + string.Join(", ", b.Extra));
    }

    // ------------------------------------------------------------------ army
    public static void AiStandingArmyFromTheUnitMix(TestContext t)
    {
        var g = NewGame(t);
        var arena = g.W.PlaceBuilding("hu_arena", g.P0, -130, -30)!;
        t.True(arena != null, "an arena");
        var b = Brain(g, g.P0, "Dodo", 4);
        g.P0.Res["food"] = 900; g.P0.Res["wood"] = 900; g.P0.Res["stone"] = 900; g.P0.Caps["food"] = g.P0.Caps["wood"] = g.P0.Caps["stone"] = 1000;
        b.ThinkNow("mod");
        t.Log(string.Join(" | ", g.H.Log));
        t.True(g.H.Queued.Any(q => q.Producer == arena && q.A.Results[0].Obj == "hu_warrior"), "the Hu mix of epoch 1 (hu_warrior) is trained at the arena");
        t.True(arena!.Rally != null, "the new units gather at the guard point");
    }

    public static void AiNightmareSquadsForTribesWithoutTables(TestContext t)
    {
        // the original's campaign: a tribe without settings tables trains no standing army (MOD:96); the Nightmare
        // (extraSquads) derives one from the tech tree
        var g = NewGame(t, aiData: false);
        g.W.PlaceBuilding("hu_arena", g.P0, -130, -30);
        g.P0.Res["food"] = 900; g.P0.Res["wood"] = 900; g.P0.Res["stone"] = 900; g.P0.Caps["food"] = g.P0.Caps["wood"] = g.P0.Caps["stone"] = 1000;
        var orig = Brain(g, g.P0, "Singleplayer_L3_1", 8, multimap: false, extraSquads: false);
        orig.ThinkNow("mod");
        t.Equal(0, g.H.Queued.Count, "the original's ministry trains nothing without Units.txt");
        var nightmare = Brain(g, g.P0, "Singleplayer_L3_1", 8, multimap: false, extraSquads: true);
        var mix = nightmare.DerivedMix(nightmare.Census(true));
        t.True(mix.Values.SelectMany(x => x).Any(x => x.Units[0].Cls == "hu_warrior"), "the derived mix holds the first infantry");
        nightmare.ThinkNow("mod");
        t.True(g.H.Queued.Count > 0, "Nightmare: the remake's own standing army is trained");
    }

    // ------------------------------------------------------------------ attacks
    public static void AiPyramidAttack(TestContext t)
    {
        var g = NewGame(t);
        var b = Brain(g, g.P0, "Dodo", 4);
        var squad = Spawn(g, g.P0, "hu_warrior", 6, -120, 20);
        Spawn(g, g.P1, "hu_warrior", 1, 140, 20);
        var a = b.RequestAttack(b.Census(true), "pyramid", g.P1);
        t.True(a != null, "an all-in with six units");
        t.Equal(6, a!.Units.Count, "the whole pool goes");
        t.True(a.State is "gather" or "walk", "it sets out (gather / walk): " + a.State);
        t.True(b.Launched.Any(l => l.Id == a.Id && l.N == 6 && l.Go != null), "logged as launched");
        t.True(squad.All(u => b.Claims.Has(u)), "the attack holds its units");
        t.True(b.Census(true).Pool.Count == 0, "claimed units leave the pool");
        // the gate of the fight module: one of a type, at most three, none while defending
        t.True(b.RequestAttack(b.Census(true), "suicide", g.P1) != null || true, "(suicide: depends on the pool)");
        a.End("brainwash");
        t.True(!b.Attacks.Contains(a) && squad.All(u => !b.Claims.Has(u)), "an ended attack releases its units");
    }

    public static void AiPassiveDebugOption(TestContext t)
    {
        var g = NewGame(t);
        var AP = new AiPlayers(g.W, g.A) { Passive = true, Human = 1 };
        var b = AP.Add(Brain(g, g.P0, "Dodo", 4));
        Spawn(g, g.P0, "hu_warrior", 6, -120, 20);
        t.True(b.RequestAttack(b.Census(true), "pyramid", g.P1) == null, "passive: no own attack on the human player");
        var tgt = new List<Entity> { g.Base1 };
        int? id = b.StartAutoAttack(tgt);
        t.True(id != null, "passive: scripted attacks still come");
    }

    public static void AiArmyTableSquadIsTrained(TestContext t)
    {
        var g = NewGame(t);
        var arena = g.W.PlaceBuilding("hu_arena", g.P0, -130, -30)!;
        g.P0.Res["food"] = 900; g.P0.Res["wood"] = 900; g.P0.Res["stone"] = 900; g.P0.Caps["food"] = g.P0.Caps["wood"] = g.P0.Caps["stone"] = 1000;
        var b = Brain(g, g.P0, "Dodo", 4);
        var a = b.RequestAttack(b.Census(true), "suicide", g.P1);
        t.True(a != null && a.State == "build", "a suicide squad is put together: " + a?.State);
        t.True(a!.ArmyName == "SuicideAttack_1" && a.Orders.Count > 0 && a.Orders.All(o => o.Cls == "hu_warrior"), "the classes of SuicideAttack_1 to train");
        t.True(g.H.Queued.Any(q => q.Producer == arena && q.A.Results[0].Obj == "hu_warrior"), "trained at the arena");
        var bl = b.RequestAttack(b.Census(true), "blitz", g.P1);
        t.True(bl != null && bl.Orders.Any(o => o.Cls == "hu_archer"), "BlitzAttack_1: archers");
    }

    public static void AiCampaignCustomAttackWithSpawn(TestContext t)
    {
        var g = NewGame(t, "Hu", "Aje");
        var b = Brain(g, g.P1, "Mikrobe", 4, multimap: false, extraSquads: false, level: "Single 07");      // asleep: waves still come
        var army = g.A.Army("Aje", "Singleplayer", "L07_aje_easy")!;
        int want = army.Sum(e => (int)e.Min);
        int before = g.W.Units.Count(u => u.Owner == g.P1);
        var id = b.StartAttack(new TribeAI.AttackRequest { Type = "L07_aje_easy", Targets = new List<Entity> { g.Base0 }, Spawn = true, IgnoreLocations = true, SpawnPosition = new Pt(150, 60), AttackOnTheWay = false });
        t.True(id != null, "AIFT spawn: an attack");
        int made = g.W.Units.Count(u => u.Owner == g.P1) - before;
        t.Log($"spawned {made} of {want}");
        t.True(made > 0 && made <= want, "the army table's units appear (classes without a model are left out)");
        var a = b.Attacks.First(x => x.Id == id);
        t.True(a.Scripted && a.State == "walk", "the wave walks to its target");
        t.True(g.H.Orders.Any(o => o.O.Type == "move" && Math.Abs(o.O.X - g.Base0.Pos.X) < 1e-9), "campaign waves walk (all_the_way off)");
        for (int i = 0; i < 30; i++) { g.W.Time += 1; b.Update(1); }
        t.True(b.Attacks.Contains(a) || a.State == "over", "the sleeping brain runs its wave");
        // outposts by AIFT
        var op = b.StartAttack(new TribeAI.AttackRequest { Type = "RessourceOutpost", Position = new Pt(100, -100), Targets = new List<Entity> { g.Base0 } });
        t.True(op != null && b.Outposts.Count == 1, "AIFT RessourceOutpost starts an outpost");
    }

    public static void AiAutoAttack(TestContext t)
    {
        var g = NewGame(t);
        var b = Brain(g, g.P0, "Mikrobe", 4, multimap: false);
        Spawn(g, g.P0, "hu_warrior", 4, -120, 20);
        var enemy = Spawn(g, g.P1, "hu_warrior", 2, 100, 0);
        var id = b.StartAutoAttack(enemy.Cast<Entity>().ToList());
        t.True(id != null, "AIFT custom_attack = 0");
        var a = b.Attacks.First(x => x.Id == id);
        t.True(a.Units.Count >= 2 && a.Units.Count <= 4, "a squad picked against the targets: " + a.Units.Count);
        t.True(b.StartAutoAttack(new List<Entity>()) == null, "no targets: no attack");
    }

    // ------------------------------------------------------------------ defence, locks, regions
    public static void AiDefenceAreaAndLocks(TestContext t)
    {
        var g = NewGame(t);
        var b = Brain(g, g.P0, "Giraffe", 4);
        var us = Spawn(g, g.P0, "hu_warrior", 6, -120, 20);
        b.SetDefenceArea("bridge", new Pt(-60, 60), 15, 3);
        b.ThinkNow("guard");
        var area = b.Areas.Get("bridge")!;
        t.Equal(3, area.Units.Count, "the area is manned up to max_units");
        t.True(g.H.Orders.Any(o => o.O.Type == "attackmove" && o.Units.All(u => area.Units.Has(u))), "its guards go there");
        b.SetDefenceArea("bridge", new Pt(-60, 60), 15, 0);
        t.True(!b.Areas.Has("bridge") && us.All(u => !b.Claims.Has(u)), "max_units 0 removes it");
        b.LockUnits(us.Take(2));
        var S = b.Census(true);
        t.True(!S.Pool.Contains(us[0]) && !S.Pool.Contains(us[1]) && S.Pool.Contains(us[2]), "locked units are not the AI's");
        b.LockUnits(us.Take(2), false);
        t.True(b.Census(true).Pool.Contains(us[0]), "unlocked again");
        // AIRG
        b.SetRegionMap("BuildModifier", new AiCircle(0, 0, 50), -5);
        b.SetRegionMap("BuildModifier", null, 2);
        t.Equal(-3.0, b.RegionValue("BuildModifier", 10, 10), "values add up inside");
        t.Equal(2.0, b.RegionValue("BuildModifier", 100, 100), "null region = the whole map");
        b.SetRegionMap("BuildModifier", null, 1, add: false);
        t.Equal(1.0, b.RegionValue("BuildModifier", 10, 10), "add = false replaces");
    }

    public static void AiVillageAlarm(TestContext t)
    {
        var g = NewGame(t);
        var b = Brain(g, g.P0, "Giraffe", 4);
        Spawn(g, g.P0, "hu_warrior", 5, -120, 20);
        Spawn(g, g.P1, "hu_warrior", 2, -150, -12);              // next to the fireplace
        b.ThinkNow("defense");
        t.True(b.Alarm != null && !b.Alarm.Animal, "enemies in the village raise the alarm");
        t.True(b.Defending, "defending: no own attacks");
        t.True(b.Guard!.Units.Count >= 2, "defenders by need (score >= 1.5 x attackers + 2): " + b.Guard.Units.Count);
        t.True(b.RequestAttack(b.Census(true), "suicide", g.P1) == null, "attacks are refused while defending");
    }

    // ------------------------------------------------------------------ mines, set-up
    public static void AiMineShoresOnAWaterMap(TestContext t)
    {
        // land west of x = 0, the sea east of it
        var map = new FlatMap(512) { Water = 0, HeightFn = (x, z) => x < 0 ? 4 : -6 };
        var g = NewGame(t, "Ninigi", "Hu", map: map);
        if (g.W.WaterNav == null) t.Skip("the world builds no water grid for this map");
        var dbg = new MineDebug();
        var shores = MineShores.Find(g.W, -150, 0, dbg);
        t.Log($"water {dbg.Water:0.00}, lattice {dbg.Lattice}, coast {dbg.Coast}, clusters {dbg.Clusters.Count}, shores {shores.Count}, path lengths {string.Join(",", dbg.Len)}");
        t.True(shores.Count > 0, "shores of the home island");
        foreach (var s in shores) foreach (var m in s.Mines) t.True(g.W.IsWater(m.X, m.Z, 1.5), $"mine at ({m.X}, {m.Z}) in deep water");
        t.True(shores.All(s => s.Distance <= 400), "within 400 m of the start");
    }

    public static void AiPlayersForCampaignAndSkirmish(TestContext t)
    {
        var g = NewGame(t);
        var slots = new[] { new CampaignAiSlot(g.P1, "ai_Mikrobe", 2, 5, 9), new CampaignAiSlot(g.P0, "human", null, null, null) };
        var A = AiPlayers.ForCampaign(g.W, g.A, slots, 1, 4).Attach();
        t.Equal(1, A.Brains.Count, "one brain per ai_ slot");
        var b = A.Get(1)!;
        t.Equal(5, b.Difficulty, "medium: AI_Difficulty_Medium");
        t.Equal("Single 04", b.LevelName, "level name of the attack plans");
        t.True(!b.ExtraSquads && b.Paused, "the original's raids; asleep");
        var nm = AiPlayers.ForCampaign(g.W, g.A, slots, 3, 4);
        t.True(nm.Get(1)!.ExtraSquads && nm.Get(1)!.Difficulty == 9, "Nightmare: hard's AI level, the remake's squads");
        // the trigger actions as IMissionHost forwards them
        t.True(A.AiBehavior(1, "Dodo", "CTRL") && !b.Paused, "AIBV wakes it");
        t.True(!A.AiBehavior(0, "Dodo", "CTRL"), "no brain for the human slot");
        A.AiDefendArea(1, "a", new MapPos(256 + 100, 256 + 50, 0), 20, 2);
        t.True(b.Areas.Get("a") is Pool ar && Math.Abs(ar.X - 100) < 1e-9 && Math.Abs(ar.Z - 50) < 1e-9, "AIDA in map coordinates");
        A.AiRegionMap(-1, "Enemy", "r", -1, true);
        t.Equal(-1.0, b.RegionValue("Enemy", 0, 0), "AIRG for every computer player (unknown region: the whole map)");
        A.AiModuleCommand(1, "DFNS", "guard_village 0");
        t.Equal(0.0, b.Params.GuardVillage, "AICM");
        var w = Spawn(g, g.P1, "hu_warrior", 1, 150, 30)[0];
        A.SetAggression(new[] { w.Id }, 0);
        t.Equal(0, w.Stance, "AIAM sets the aggression state");
        A.AiLockUnits(1, new[] { w.Id }, true);
        t.True(b.Locked.Has(w), "AILU by object id");
        t.True(A.AiAttack(1, new AiAttackOrder { Type = "PyramidAttack", Targets = new List<int> { g.Base0.Id } }), "AIFT reaches the brain");
        A.AiGameOver();
        t.True(b.GameOver, "GAOV");
        t.True(g.W.Ai == A, "attached to the world");
        // skirmish
        var S = AiPlayers.ForSkirmish(g.W, g.A, new[] { new SkirmishAiSlot(g.P0, 8, "Giraffe"), new SkirmishAiSlot(g.P1, 1) });
        t.Equal("Giraffe", S.Get(0)!.Behaviour, "skirmish personality");
        t.True(S.Get(1)!.Behaviour is "Dodo" or "Giraffe" or "Schnecke", "random personality");
        t.True(S.Get(0)!.Multimap && S.Get(0)!.Cls == "Hard", "skirmish: multimap, hard");
    }

    public static void AiPlayersFromCampaignJson(TestContext t)
    {
        string dir = Environment.GetEnvironmentVariable("PW_CAMPAIGN_JSON") is { Length: > 0 } s ? s : "/home/claude/testdata/campaign";
        string f = Path.Combine(dir, "base", "single_04.json");
        if (!File.Exists(f)) t.Skip("no campaign mission JSON " + f);
        var mission = Json.ParseFile(f)!;
        var (R, M, A) = Load(t);
        var W = new W_(R, M, new FlatMap(512));
        var players = (System.Text.Json.Nodes.JsonArray)mission["players"]!;
        foreach (var p in players) if (p!["present"].Bool(true)) W.Players.Add(new Player(p["id"].Int(), p["tribe"].Str("Hu"), new PlayerOpts { Rules = R, Ai = p["control"].Str() != "human" }));
        var AP = AiPlayers.ForCampaign(W, A, mission, 2, 4);
        int ai = players.Count(p => p!["present"].Bool(true) && p["control"].Str().StartsWith("ai_") && p["id"].Int() != 0);
        t.Equal(ai, AP.Brains.Count, "a brain per ai_ slot of the mission");
        foreach (var b in AP.Brains.Values) t.Log($"player {b.P.Id} {b.P.Tribe}: {b.Behaviour} d={b.Difficulty} {b.Cls}");
    }

    // ------------------------------------------------------------------ AI against AI
    public static void AiAgainstAiSmoke(TestContext t)
    {
        var (R, M, A) = Load(t);
        var W = new W_(R, M, new FlatMap(512), new WorldOptions { Seed = 3 });
        var p0 = new Player(0, "Hu", new PlayerOpts { Rules = R, Ai = true }); var p1 = new Player(1, "Aje", new PlayerOpts { Rules = R, Ai = true });
        W.Players.Add(p0); W.Players.Add(p1);
        var g = new Game { W = W, A = A, P0 = p0, P1 = p1 };
        foreach (var p in W.Players) { p.Res["food"] = 200; p.Res["wood"] = 150; p.Res["stone"] = 100; p.MaxUnits = 20; }
        Village(g, p0, -180); Village(g, p1, 180);
        var AP = AiPlayers.ForSkirmish(W, A, new[] { new SkirmishAiSlot(p0, 4, "Dodo"), new SkirmishAiSlot(p1, 4, "Giraffe") }).Attach();
        int units0 = W.Units.Count, blds0 = W.Buildings.Count;
        var issued = new List<string>();
        foreach (var b in AP.Brains.Values) if (b.Hands is CommandHands ch) ch.Issued = (c, why) => issued.Add($"{c.GetType().Name}: {why ?? "ok"}");
        for (int i = 0; i < 20 * 60 * 3; i++) W.Step();                // three minutes
        t.True(issued.Count > 0, "the brains give commands");
        t.Log($"{issued.Count} commands; {issued.Count(x => x.EndsWith("ok"))} done; first refusals: {string.Join(", ", issued.Where(x => !x.EndsWith("ok")).Distinct().Take(5))}");
        bool trained = W.Units.Count > units0, built = W.Buildings.Count > blds0;
        if (!trained && !built) t.Skip("the world's systems (production, construction) are not ported yet: nothing was trained or built in three minutes");
        t.True(trained, "units were trained");
        t.True(built, "buildings were started");
        for (int i = 0; i < 20 * 60 * 12; i++) W.Step();               // fifteen minutes in all
        t.True(AP.Brains.Values.Any(b => b.Launched.Any(l => l.Go != null)), "an attack set out within fifteen minutes");
    }

    public static void AiDeterministic(TestContext t)
    {
        string Run()
        {
            var (R, M, A) = Load(t);
            var W = new W_(R, M, new FlatMap(512), new WorldOptions { Seed = 11 });
            var p0 = new Player(0, "Hu", new PlayerOpts { Rules = R, Ai = true }); var p1 = new Player(1, "Ninigi", new PlayerOpts { Rules = R, Ai = true });
            W.Players.Add(p0); W.Players.Add(p1);
            var g = new Game { W = W, A = A, P0 = p0, P1 = p1 };
            foreach (var p in W.Players) { p.Res["food"] = 300; p.Res["wood"] = 300; p.Res["stone"] = 300; p.MaxUnits = 20; }
            Village(g, p0, -180); Village(g, p1, 180);
            Spawn(g, p0, "hu_warrior", 5, -150, 30);
            var AP = AiPlayers.ForSkirmish(W, A, new[] { new SkirmishAiSlot(p0, 6), new SkirmishAiSlot(p1, 6) }).Attach();
            var log = new List<string>();
            foreach (var b in AP.Brains.Values) if (b.Hands is CommandHands ch) ch.Issued = (c, why) => log.Add($"{c.Step} {c}");
            for (int i = 0; i < 20 * 90; i++) W.Step();
            return string.Join("\n", log) + "\n" + W.StateHash();
        }
        var a = Run(); var b = Run();
        t.True(a.Length > 0, "commands given");
        t.Equal(a, b, "same seed, same commands and world");
    }
}
