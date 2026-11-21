// The co-op hero phase and the fairness of the base places (docs/COOP.md "Hero phase", "Fair bases"), headless:
//
//   CoopHeroPhase01      mission 1 with 3 humans, 2 humans and 2 humans + an AI commander (both configurations): seats 2 / 3
//                        start with their Norse warrior only (no base, no stock), the beach nest's two Dilophosaurs come
//                        together, no enemy addition and no extra garrison yet; the story's gifts are dealt (Béla's 3 archers:
//                        one each), workers go to seat 1 and end the phase: the bases, the enemy additions and the garrisons
//                        come then
//   CoopHeroPhase08      mission 8 (user, 2026-11-11), 3 seats (2 AI commanders) and 2 seats: seat 1 starts with its base,
//                        seats 2 / 3 with a share of the start spearmen only (Kleemann and the heroes not to the AI); the
//                        AI commanders' fighters attack the barbarian camp (region rescamp); when it is cleared (D02) the
//                        bases appear at the definition's sites, the enemy additions come, the mission goes on. And mission
//                        1's hero phase: an AI commander's fighters escort the humans' hero (not idle at the landing)
//   CoopGiftRules        the dealing rules on mission 9's village (a group of 15 fighters, workers and buildings in one
//                        action) and in a no-build mission (mission 5: single helpers to the seat with the fewest units)
//   CoopSecondGate13     mission 13 with 3 seats: the second gate in place of wall pieces 79 / 74 / 85, X01 watches it (the
//                        mission is lost when it falls), the defence split; with 2 seats: no second gate
//   CoopFairness         every base mission, 2 and 3 seats: no seat's base nearer to an enemy building than seat 1's base
//                        is in single player (D1), no enemy addition nearer than D1 to seat 1, every seat's base within
//                        NearShare of the map's width of seat 1's (exceptions named in NearExceptions)
//   CoopPlay01           (PW_PLAY=1) the mission 1 playthrough bot in a 3-seat co-op game: the hero phase as the story goes,
//                        the gifts dealt as designed, the bases handed out when Béla frees the workers, the mission won
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Coop;
using PwRemake.Sim.Game;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class CoopPhaseTests
{
    static readonly string[] Cfgs = { "base", "mirage" };
    static bool HasCfg(string cfg) => CampaignTests.LoadCfgPublic(cfg) != null;
    static List<Unit> UnitsOf(World W, Player p) => W.Units.Where(u => u.Alive && u.Owner == p).ToList();

    /// <summary>gives the objects to "the player" as one OCPY action does (the host deals them in a co-op game)</summary>
    static void Occupy(MissionSession s, IEnumerable<Entity> ents)
    {
        s.Host.GiftBatch(true);
        foreach (var e in ents.ToList()) if (e.Rec is { Alive: true } r) s.Host.SetOwner(r.Id, 0, 0);
        s.Host.GiftBatch(false);
    }

    // ======================================================================================== mission 1
    public static void CoopHeroPhase01(TestContext t)
    {
        foreach (var cfg in Cfgs)
        {
            if (!HasCfg(cfg)) { t.Log($"{cfg}: no data, skipped"); continue; }
            // single player: one Dilophosaurus at the beach at first (the nest's spawn_max 1)
            var c = CampaignTests.LoadCfgPublic(cfg)!;
            var sp = MissionSession.Create(new GameStart { Configuration = cfg, Mission = 1, Seed = 1, Difficulty = 1 }, c.Config, new SessionOptions { Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data });
            int dilo1 = sp.World.Units.Count(u => u.Alive && u.Name == "Dilophosaurus");
            foreach (var seats in new[] { new[] { ("human", ""), ("human", ""), ("human", "") }, new[] { ("human", ""), ("human", "") }, new[] { ("human", ""), ("human", ""), ("ai", "") }, new[] { ("human", ""), ("human", "Aje"), ("human", "Ninigi") } })
            {
                string w = $"{cfg} 01 [{string.Join(",", seats.Select(x => x.Item1 + (x.Item2.Length > 0 ? ":" + x.Item2 : "")))}]";
                var s = CoopTests.Session(t, CoopTests.CoopStart(t, cfg, 1, seats));
                var C = s.Campaign; var W = s.World; var R = C.Coop!;
                t.True(R.HasHeroPhase && R.HeroPhase, $"{w}: a hero phase (seat 1 starts with Cole only)");
                t.Equal(dilo1 + 1, W.Units.Count(u => u.Alive && u.Name == "Dilophosaurus"), $"{w}: the beach nest's two Dilophosaurs together ({dilo1} in single player)");
                foreach (var seat in R.Seats.Where(x => x.Seat > 1))
                {
                    var us = UnitsOf(W, seat.Player);
                    t.Equal(1, us.Count, $"{w}: seat {seat.Seat} starts with one unit ({string.Join(" ", us.Select(u => u.Name))})");
                    t.True(us.All(u => !u.IsWorker && u.Name.EndsWith("_warrior")), $"{w}: seat {seat.Seat}'s unit is its tribe's warrior");
                    t.True(!W.Buildings.Any(b => b.Alive && b.Owner == seat.Player), $"{w}: seat {seat.Seat} has no building in the hero phase");
                    t.True(seat.Player.Res.Values.All(v => v == 0), $"{w}: seat {seat.Seat} has no stock in the hero phase");
                }
                t.Equal(0, R.GarrisonExtra, $"{w}: no extra garrison units in the hero phase");
                t.True(R.Enemies.List.All(e => e.Placed.Count == 0), $"{w}: the enemy additions wait for the bases");
                t.True(!R.EnemyScaling, $"{w}: the enemies' numbers are not scaled in the hero phase");
                t.True(W.Players.All(p => p.CoopDamage == 1 && p.CoopHealth == 1), $"{w}: no enemy health or damage scaling in the hero phase");
                // Béla's archers (the prison's, player 3): one action, one each (2 seats: 2 + 1, the fewest first)
                var archers = W.Units.Where(u => u.Alive && u.Name == "hu_archer" && u.Owner?.Id == 3 && C.Regions.Find("prison_archer")!.Contains(u.Pos.X, u.Pos.Z)).ToList();
                t.Equal(3, archers.Count, $"{w}: the prison's three archers");
                var before = R.Seats.ToDictionary(x => x.Seat, x => UnitsOf(W, x.Player).Count);
                Occupy(s, archers);
                var got = R.Seats.ToDictionary(x => x.Seat, x => UnitsOf(W, x.Player).Count - before[x.Seat]);
                t.Equal(3, got.Values.Sum(), $"{w}: the archers are split, not copied ({string.Join(" ", got)})");
                if (R.Seats.Count == 3) t.True(got.Values.All(v => v == 1), $"{w}: one archer to each seat ({string.Join(" ", got)})");
                else t.True(got.Values.All(v => v >= 1), $"{w}: each seat gets an archer ({string.Join(" ", got)})");
                // the workers: seat 1's, and the bases are handed out
                var workers = W.Units.Where(u => u.Alive && u.IsWorker && u.Owner?.Id == 3 && C.Regions.Find("prison_worker")!.Contains(u.Pos.X, u.Pos.Z)).ToList();
                t.True(workers.Count > 0, $"{w}: the prison's workers");
                Occupy(s, workers);
                t.True(workers.All(u => u.Owner == R.Seats[0].Player), $"{w}: workers go to seat 1 (its base)");
                s.Step();
                t.True(!R.HeroPhase && R.HandedOverAt != null, $"{w}: the hero phase ends when seat 1 gets workers");
                foreach (var seat in R.Seats.Where(x => x.Seat > 1))
                {
                    // (the Dustriders' main is a vehicle, their resource collector)
                    Entity? mb = W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == seat.Player);
                    mb ??= W.Units.FirstOrDefault(u => u.Alive && u.Owner == seat.Player && u.Name == "aje_resource_collector");
                    t.True(mb != null, $"{w}: seat {seat.Seat} has its main building after the hand-over");
                    var sd = seat.Def!;
                    t.True(mb != null && Math.Abs(mb.Pos.X - sd.X!.Value) < 30 && Math.Abs(mb.Pos.Z - sd.Z!.Value) < 30, $"{w}: seat {seat.Seat}'s base at its start ({sd.X:0}, {sd.Z:0})");
                    t.True(UnitsOf(W, seat.Player).Count(u => u.IsWorker) >= 4, $"{w}: seat {seat.Seat}'s workers");
                    t.True(seat.Player.Res.Values.Any(v => v > 0), $"{w}: seat {seat.Seat}'s stock");
                }
                // (mission 1's barbarians are neutral until the story turns them: their garrisons are not scaled - as before)
                t.True(R.EnemyScaling, $"{w}: the enemies' numbers scaled from the hand-over");
                t.True(W.Players.Where(p => R.IsScaled(p.Id)).All(p => p.CoopHealth == R.Scaling.Health && p.CoopDamage == R.Scaling.Damage), $"{w}: the scaled enemies' health and damage from the hand-over");
                t.True(R.Enemies.List.All(e => e.Placed.Count > 0), $"{w}: the enemy additions placed at the hand-over");
            }
        }
    }

    /// <summary>the hero phase's end with AI commanders (user report 2026-11-05, mission 1: "dust riders spawned at the
    /// bottom of the map instead of their actual spawn point" - the brain kept the hero-phase start, seat 1's landing,
    /// and the Dustriders' collector, their home, drove back there): every hero-phase mission (0, 1, 7, 9), every tribe
    /// (base and MIRAGE with the SEAS), the AI commanders' bases at the definition's sites, their brains' start there, and
    /// two minutes on the main building, the buildings and the workers still there, no command, nothing thrown away</summary>
    public static void CoopHandOverAiBases(TestContext t)
    {
        var runs = new List<(string Cfg, int Mission, string T2, string T3)>();
        var only = Environment.GetEnvironmentVariable("PW_COOP_HANDOVER_MISSION");
        foreach (var m in new[] { 0, 1, 7, 9 }.Where(m => only == null || only == m.ToString()))
        {
            runs.Add(("base", m, "Aje", "Ninigi"));
            runs.Add(("base", m, "Hu", "Aje"));
            runs.Add(("mirage", m, "SEAS", "Aje"));
        }
        double minutes = double.TryParse(Environment.GetEnvironmentVariable("PW_COOP_HANDOVER_S"), out var mm) ? mm : 120;
        foreach (var (cfg, mission, t2, t3) in runs)
        {
            if (!HasCfg(cfg)) { t.Log($"{cfg}: no data, skipped"); continue; }
            if (CoopDefinition.Load(cfg, mission) == null) { t.Log($"{cfg} {mission:00}: no definition"); continue; }
            string w = $"{cfg} {mission:00} [Hu, ai {t2}, ai {t3}]";
            var s = CoopTests.Session(t, CoopTests.CoopStart(t, cfg, mission, new[] { ("human", ""), ("ai", t2), ("ai", t3) }));
            var W = s.World; var R = s.Campaign.Coop!;
            if (!R.HeroPhase) { t.True(false, $"{w}: a hero phase"); continue; }
            // the hero phase: the AI commanders' brains see their hero-phase units (at seat 1's start) first - their census
            // takes that place as the start (in the game the hero phase lasts minutes)
            s.Run(30);
            foreach (var a in R.Seats.Where(x => x.Ai))
            {
                int n = UnitsOf(W, a.Player).Count;
                if (n > 0) t.True(a.Commander!.Brain.Start != null, $"{w}: seat {a.Seat}: the brain saw its {n} hero-phase units");
                else t.Log($"{w}: seat {a.Seat} has no hero-phase units");
            }
            // (mission 9: the village joins - the hand-over - when the Barbarians attacking it are beaten, quest "Rescue
            // Mission"; their attack groups, player 4's, stand around the seats' sites until then)
            if (mission == 9) foreach (var u in W.Units.Where(u => u.Alive && u.Owner?.Id == 4).ToList()) W.Kill(u, null);
            // (where the brains stood in the hero phase: the start they kept before the fix)
            var heroAt = R.Seats.Where(x => x.Ai).ToDictionary(x => x.Seat, x => (X: s.Campaign.StartLocations[x.Slot]!.X, Z: s.Campaign.StartLocations[x.Slot]!.Z));
            R.HandOver();
            s.Step();
            var ais = R.Seats.Where(x => x.Ai).ToList();
            var site = ais.ToDictionary(x => x.Seat, x => (X: x.Def!.X!.Value, Z: x.Def!.Z!.Value));
            static double D(Entity e, (double X, double Z) p) => Math.Sqrt((e.Pos.X - p.X) * (e.Pos.X - p.X) + (e.Pos.Z - p.Z) * (e.Pos.Z - p.Z));
            Entity? Main(CoopSeatRun a) { var mb = PwRemake.Sim.Campaign.Campaign.MAIN_BUILDING[a.Tribe]; return (Entity?)W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == a.Player && b.Name == mb) ?? W.Units.FirstOrDefault(u => u.Alive && u.Owner == a.Player && u.Name == mb); }
            string Killer(Unit u) => W.Events.LastOrDefault(ev => ev.Type == "died" && ev.E("entity") == u)?.E("killer") is { } k ? $"{k.Name}/{k.Owner?.Id} at {k.Pos.X:0},{k.Pos.Z:0}" : "?";
            var workers0 = new Dictionary<int, int>();
            var wlist = new Dictionary<int, List<Unit>>();
            var fighters0 = new Dictionary<int, List<Unit>>();
            foreach (var a in ais)
            {
                var p = site[a.Seat];
                var mb = Main(a);
                t.True(mb != null && D(mb, p) < 30, $"{w}: seat {a.Seat} ({a.Tribe}): its main building at the site {p.X:0},{p.Z:0} ({mb?.Name} at {mb?.Pos.X:0},{mb?.Pos.Z:0})");
                var ws = UnitsOf(W, a.Player).Where(u => u.IsWorker).ToList();
                workers0[a.Seat] = ws.Count; wlist[a.Seat] = ws;
                fighters0[a.Seat] = UnitsOf(W, a.Player).Where(u => !u.IsWorker && !u.CannotFight && u.Name != PwRemake.Sim.Campaign.Campaign.MAIN_BUILDING[a.Tribe]).ToList();
                t.True(ws.Count >= 3 && ws.All(u => D(u, p) < 30), $"{w}: seat {a.Seat}: its workers at the site ({string.Join(" ", ws.Select(u => $"{u.Pos.X:0},{u.Pos.Z:0}"))})");
                var st = a.Commander!.Brain.Start;
                t.True(st == null || Math.Abs(st.Value.X - p.X) < 30 && Math.Abs(st.Value.Z - p.Z) < 30, $"{w}: seat {a.Seat}: the brain's start is at the site {p.X:0},{p.Z:0} (or not known yet: {st})");
                var sl = s.Campaign.StartLocations[a.Slot];
                t.True(sl != null && Math.Abs(sl.X - p.X) < 1 && Math.Abs(sl.Z - p.Z) < 1, $"{w}: seat {a.Seat}: its start location is the site");
            }
            s.Run(minutes);
            foreach (var a in ais)
            {
                var p = site[a.Seat];
                var mb = Main(a);
                var mbDied = W.Events.LastOrDefault(ev => ev.Type == "died" && ev.E("entity") is { } de && de.Owner == a.Player && de.Name == PwRemake.Sim.Campaign.Campaign.MAIN_BUILDING[a.Tribe]);
                // (a village overwhelmed is no failure of this test: the main building may fall when every fighter it had at
                // the hand-over fell first, at home - MIRAGE 9's Aje seat, 3 fighters and 2 workers against 4 Barbarian
                // warriors: whether they then go for the collector or a building site is chance)
                double? DiedAt(Unit u) => W.Events.LastOrDefault(ev => ev.Type == "died" && ev.E("entity") == u)?.Time;
                bool overwhelmed = mb == null && mbDied != null && fighters0[a.Seat].All(u => !u.Alive && DiedAt(u) is { } dt && dt <= mbDied.Time && D(u, p) < 60);
                if (overwhelmed) { t.Log($"{w}: seat {a.Seat} ({a.Tribe}): its {fighters0[a.Seat].Count} fighters fell defending, then its main building ({mbDied!.Time:0} s)"); }
                t.True(overwhelmed || (mb != null && D(mb, p) < 40), $"{w}: seat {a.Seat} ({a.Tribe}): after {minutes:0} s its main building is still at the site {p.X:0},{p.Z:0} ({mb?.Pos.X:0},{mb?.Pos.Z:0}, task {(mb as Unit)?.Task.Type}; died: {(mbDied == null ? "no" : $"{mbDied.Time:0} s at {mbDied.E("entity")!.Pos.X:0},{mbDied.E("entity")!.Pos.Z:0} by {mbDied.E("killer")?.Name}/{mbDied.E("killer")?.Owner?.Id}")}; owns {string.Join(" ", UnitsOf(W, a.Player).Select(u => u.Name).Distinct())} | {string.Join(" ", W.Buildings.Where(x => x.Alive && x.Owner == a.Player).Select(x => x.Name).Distinct())})");
                // (a harbour stands at the nearest shore - mission 9's island seats build theirs ~150 m away -, bunkers and
                // towers on the way to the enemy; nothing nearer the hero phase's place than the site)
                var far = W.Buildings.Where(b => b.Alive && b.Owner == a.Player && (D(b, p) > (b.Name.Contains("harbour") ? 250 : b.Name.Contains("tower") || b.Name.Contains("bunker") ? 200 : 150) || (D(b, p) > 60 && D(b, heroAt[a.Seat]) < D(b, p)))).ToList();
                t.True(far.Count == 0, $"{w}: seat {a.Seat}: it builds at its base ({string.Join(" ", far.Select(b => $"{b.Name}@{b.Pos.X:0},{b.Pos.Z:0} {D(b, p):0} m from the site {p.X:0},{p.Z:0}, {D(b, heroAt[a.Seat]):0} m from the hero place"))})");
                var ws = UnitsOf(W, a.Player).Where(u => u.IsWorker).ToList();
                // (a wild animal at the base may take one - mission 9's Allosaurus -; a worker sent away dies far from it)
                var lost = wlist[a.Seat].Where(u => !u.Alive && D(u, p) > 160).ToList();
                t.True(lost.Count == 0, $"{w}: seat {a.Seat}: no worker lost away from the base ({string.Join(" ", lost.Select(u => $"{u.Name}@{u.Pos.X:0},{u.Pos.Z:0} d {D(u, p):0}; killed by {Killer(u)}"))})");
                // nothing of it killed by an enemy player away from its base (the army is not sent to the enemy)
                var thrown = W.Events.Where(ev => ev.Type == "died" && ev.Time > R.HandedOverAt && ev.E("entity") is Unit du && du.Owner == a.Player && ev.E("killer")?.Owner is { } ko && a.Player.IsEnemy(ko) && D(du, p) > 150)
                    .Select(ev => ev.U("entity")!).ToList();
                t.True(thrown.Count == 0, $"{w}: seat {a.Seat}: nothing killed by an enemy away from its base ({string.Join(" ", thrown.Select(u => $"{u.Name}@{u.Pos.X:0},{u.Pos.Z:0} by {Killer(u)}"))})");
                // (fishing boats fish at sea; workers build the towers and harbours above, up to 150 m out)
                var wfar = ws.Where(u => !u.Naval && u.Cls != "SHIP" && (D(u, p) > 160 || (D(u, p) > 60 && D(u, heroAt[a.Seat]) < D(u, p)))).ToList();
                t.True(wfar.Count == 0, $"{w}: seat {a.Seat}: its workers work at its base ({string.Join(" ", wfar.Select(u => $"{u.Pos.X:0},{u.Pos.Z:0}:{u.Task}"))}; its buildings over 90 m: {string.Join(" ", W.Buildings.Where(b => b.Alive && b.Owner == a.Player && D(b, p) > 90).Select(b => $"{b.Name}@{b.Pos.X:0},{b.Pos.Z:0} built {b.Built}"))})");
                t.True(a.Commander!.Order == "none", $"{w}: seat {a.Seat}: no command ({a.Commander})");
                var st2 = a.Commander.Brain.Start;
                t.True(st2 != null && Math.Abs(st2.Value.X - p.X) < 30 && Math.Abs(st2.Value.Z - p.Z) < 30, $"{w}: seat {a.Seat}: the brain's start is at the site {p.X:0},{p.Z:0} ({st2})");
            }
        }
    }

    /// <summary>exploration (PW_COOP_HANDOVER_LOG=1): mission 1 with two AI commanders, hand-over, then where their things
    /// are and what they do for 3 minutes</summary>
    public static void CoopHandOverExplore(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_COOP_HANDOVER_LOG") == null) t.Skip("PW_COOP_HANDOVER_LOG");
        int mission = int.TryParse(Environment.GetEnvironmentVariable("PW_COOP_HANDOVER_LOG"), out var mi) ? mi : 1;
        var s = CoopTests.Session(t, CoopTests.CoopStart(t, "base", mission, new[] { ("human", "Hu"), ("ai", "Aje"), ("ai", "Ninigi") }));
        var C = s.Campaign; var W = s.World; var R = C.Coop!;
        foreach (var p in W.Players)
        {
            var us = W.Units.Where(u => u.Alive && u.Owner == p).ToList();
            t.Log($"player {p.Id} {p.Tribe} {p.Name} hostile to seat 2: {R.Seats[1].Player.IsEnemy(p)}; {us.Count} units around {(us.Count > 0 ? $"{us.Average(u => u.Pos.X):0},{us.Average(u => u.Pos.Z):0}" : "-")}; {W.Buildings.Count(b => b.Alive && b.Owner == p)} buildings; brain {W.Brains.Has(p.Id)}");
        }
        if (mission != 1)
        {
            s.Run(5); R.HandOver(); s.Step();
            foreach (var seat in R.Seats.Where(x => x.Seat > 1)) t.Log($"seat {seat.Seat} site {seat.Def!.X:0},{seat.Def.Z:0}");
            for (int k = 0; k < 12; k++)
            {
                s.Run(10);
                foreach (var seat in R.Seats.Where(x => x.Seat > 1))
                {
                    var sd = seat.Def!;
                    var foes = W.Units.Where(u => u.Alive && u.Owner != null && seat.Player.IsEnemy(u.Owner) && Math.Sqrt((u.Pos.X - sd.X!.Value) * (u.Pos.X - sd.X.Value) + (u.Pos.Z - sd.Z!.Value) * (u.Pos.Z - sd.Z.Value)) < 80).ToList();
                    t.Log($"t {W.Time:0} seat {seat.Seat}: foes near: {string.Join(" ", foes.Select(u => $"{u.Name}/{u.Owner!.Id}:{u.Task.Type}"))}");
                }
            }
            return;
        }
        s.Run(20);
        foreach (var seat in R.Seats.Where(x => x.Seat > 1)) t.Log($"hero phase: seat {seat.Seat} brain start {seat.Commander!.Brain.Start} units {string.Join(" ", UnitsOf(W, seat.Player).Select(u => $"{u.Name}@{u.Pos.X:0},{u.Pos.Z:0}"))}");
        var archers = W.Units.Where(u => u.Alive && u.Name == "hu_archer" && u.Owner?.Id == 3 && C.Regions.Find("prison_archer")!.Contains(u.Pos.X, u.Pos.Z)).ToList();
        Occupy(s, archers);
        var workers = W.Units.Where(u => u.Alive && u.IsWorker && u.Owner?.Id == 3 && C.Regions.Find("prison_worker")!.Contains(u.Pos.X, u.Pos.Z)).ToList();
        Occupy(s, workers);
        s.Step();
        t.Log($"handed over at {R.HandedOverAt}; seat 1 start {C.StartLocations[R.Seats[0].Slot]?.X:0},{C.StartLocations[R.Seats[0].Slot]?.Z:0}");
        foreach (var e in W.Buildings.Where(b => b.Alive && b.Owner?.Id == 2).Take(3)) t.Log($"  barbarian building {e.Name} at {e.Pos.X:0},{e.Pos.Z:0}");
        for (int k = 0; k <= 18; k++)
        {
            foreach (var seat in R.Seats.Where(x => x.Seat > 1))
            {
                var sd = seat.Def!;
                var b = seat.Commander!.Brain;
                t.Log($"t {W.Time:0} seat {seat.Seat} {seat.Tribe} def {sd.X:0},{sd.Z:0} brain start {b.Start} home {b.LastCensus?.Home} cmd {seat.Commander} blds {W.Buildings.Count(x => x.Alive && x.Owner == seat.Player)}");
                foreach (var u in UnitsOf(W, seat.Player)) t.Log($"     {u.Name} @{u.Pos.X:0},{u.Pos.Z:0} d {Math.Sqrt((u.Pos.X - sd.X!.Value) * (u.Pos.X - sd.X.Value) + (u.Pos.Z - sd.Z!.Value) * (u.Pos.Z - sd.Z.Value)):0} task {u.Task.Type} {u.Task.X:0},{u.Task.Z:0}");
            }
            s.Run(10);
        }
    }

    // ======================================================================================== mission 8
    public static void CoopHeroPhase08(TestContext t)
    {
        static double D(Entity e, double x, double z) => Math.Sqrt((e.Pos.X - x) * (e.Pos.X - x) + (e.Pos.Z - z) * (e.Pos.Z - z));
        foreach (var (cfg, seats) in new[] { ("base", new[] { ("human", ""), ("ai", ""), ("ai", "") }), ("base", new[] { ("human", ""), ("human", "") }), ("mirage", new[] { ("human", ""), ("ai", "SEAS"), ("ai", "Hu") }) })
        {
            if (!HasCfg(cfg)) { t.Log($"{cfg}: no data, skipped"); continue; }
            string w = $"{cfg} 08 [{string.Join(",", seats.Select(x => x.Item1 + (x.Item2.Length > 0 ? ":" + x.Item2 : "")))}]";
            var s = CoopTests.Session(t, CoopTests.CoopStart(t, cfg, 8, seats));
            var C = s.Campaign; var W = s.World; var R = C.Coop!;
            var camp = C.Regions.Find("rescamp")!;
            var (cx, cz) = camp.Center()!.Value;
            var p1 = C.Players[1]!;
            List<Entity> Camp() => W.Units.Cast<Entity>().Concat(W.Buildings).Where(e => e.Alive && e.Owner == p1 && camp.Contains(e.Pos.X, e.Pos.Z)).ToList();
            t.True(R.HasHeroPhase && R.HeroPhase, $"{w}: a hero phase until the camp is cleared");
            t.True(W.Units.Any(u => u.Alive && u.Owner == R.Seats[0].Player && u.IsWorker), $"{w}: seat 1 has its base from the start (the Dustriders' collector, workers)");
            t.True(Camp().Count > 0, $"{w}: the barbarian camp ({Camp().Count} objects)");
            foreach (var seat in R.Seats.Where(x => x.Seat > 1))
            {
                var us = UnitsOf(W, seat.Player);
                t.True(us.Count >= 1 && us.All(u => !u.IsWorker), $"{w}: seat {seat.Seat} starts with fighters only ({string.Join(" ", us.Select(u => u.Name))})");
                t.True(!W.Buildings.Any(b => b.Alive && b.Owner == seat.Player), $"{w}: seat {seat.Seat} has no building in the hero phase");
                t.True(seat.Player.Res.Values.All(v => v == 0), $"{w}: seat {seat.Seat} has no stock in the hero phase");
                if (seat.Ai) t.True(us.All(u => u.Def?.Unique != true), $"{w}: no hero to AI seat {seat.Seat}");
            }
            t.True(W.Units.Any(u => u.Alive && u.Name == "schliemann_s0" && u.Owner == R.Seats[0].Player), $"{w}: Kleemann stays seat 1's");
            int spear = R.Seats.Sum(x => UnitsOf(W, x.Player).Count(u => u.Name.EndsWith("_spearman")));
            t.Equal(4, spear, $"{w}: the four start spearmen, dealt (not copied): {string.Join(" ", R.Seats.Select(x => $"{x.Seat}:{UnitsOf(W, x.Player).Count(u => u.Name.EndsWith("_spearman"))}"))}");
            t.True(!R.EnemyScaling && R.Enemies.List.All(e => e.Placed.Count == 0), $"{w}: no extra enemies in the hero phase");
            var ais = R.Seats.Where(x => x.Ai).ToList();
            var d0 = ais.ToDictionary(a => a.Seat, a => a.Commander!.Army().Average(u => D(u, cx, cz)));
            s.Run(60);
            foreach (var a in ais)
            {
                var army = a.Commander!.Army();
                double d1 = army.Count > 0 ? army.Min(u => D(u, cx, cz)) : -1;
                bool fights = army.Any(u => u.Task.Type == "attack" || camp.Contains(u.Pos.X, u.Pos.Z));
                t.True(army.Count == 0 || fights, $"{w}: seat {a.Seat}'s fighters attack the camp (from {d0[a.Seat]:0} m to {d1:0} m; tasks {string.Join(" ", army.Select(u => u.Task.Type))}; target {a.Commander.Target?.Name})");
            }
            if (R.HeroPhase) t.True(ais.All(a => a.Commander!.Order == "none"), $"{w}: no command - the phase's objective is the AI's default");
            // the camp cleared (the rest of it, as the players would): D02 ends the phase
            // (the humans' fighters go there - D01, the camp seen, activates D02)
            var hum = R.HumanSeats.SelectMany(x => UnitsOf(W, x.Player)).Where(u => !u.IsWorker && u.Name != "schliemann_s0").ToList();
            W.Order(hum, new Order("attackmove") { X = cx, Z = cz });
            for (int i = 0; i < 150 && R.HeroPhase; i++)
            {
                if (C.Engine!.Find("D01#ds_2599") is { Fired: > 0 }) foreach (var e in Camp()) W.Kill(e, null);
                s.Run(1);
            }
            t.True(!R.HeroPhase && R.HandedOverAt != null, $"{w}: the camp cleared ends the hero phase (D02 fired: {C.Engine!.Find("D02#ds_2592")?.Fired})");
            foreach (var seat in R.Seats.Where(x => x.Seat > 1))
            {
                Entity? mb = W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == seat.Player);
                mb ??= W.Units.FirstOrDefault(u => u.Alive && u.Owner == seat.Player && u.Name == "aje_resource_collector");
                var sd = seat.Def!;
                t.True(mb != null && D(mb, sd.X!.Value, sd.Z!.Value) < 30, $"{w}: seat {seat.Seat}'s main building at its site ({sd.X:0}, {sd.Z:0}): {mb?.Name} at {mb?.Pos.X:0},{mb?.Pos.Z:0}");
                t.True(UnitsOf(W, seat.Player).Count(u => u.IsWorker) >= 3, $"{w}: seat {seat.Seat}'s workers");
                t.True(seat.Player.Res.Values.Any(v => v > 0), $"{w}: seat {seat.Seat}'s stock");
                if (seat.Ai) t.True(seat.Commander!.Order == "none" && seat.Commander.Brain.Start is { } st && Math.Abs(st.X - sd.X!.Value) < 30 && Math.Abs(st.Z - sd.Z!.Value) < 30, $"{w}: seat {seat.Seat}'s commander rebased at its site ({seat.Commander})");
            }
            t.True(R.EnemyScaling && R.Enemies.List.Where(e => e.Def.Seats <= R.Seats.Count).All(e => e.Placed.Count > 0), $"{w}: the enemy additions and the count scaling from the hand-over");
            s.Run(30);
            t.True(!s.Over, $"{w}: the mission goes on");
        }
        // mission 1: an AI commander's fighters escort the humans' hero in the hero phase
        {
            const string cfg = "base";
            var s = CoopTests.Session(t, CoopTests.CoopStart(t, cfg, 1, new[] { ("human", ""), ("ai", ""), ("ai", "") }));
            var W = s.World; var R = s.Campaign.Coop!;
            var cole = W.Units.First(u => u.Alive && u.Owner == R.Seats[0].Player && u.Def?.Unique == true);
            double sx = cole.Pos.X, sz = cole.Pos.Z;
            s.Run(3);
            W.Order(new List<Unit> { cole }, new Order("move") { X = cole.Pos.X + 30, Z = cole.Pos.Z + 30 });
            s.Run(30);
            var army = R.Seats.Where(x => x.Ai).SelectMany(a => a.Commander!.Army()).ToList();
            t.True(R.HeroPhase && army.Count >= 2 && army.All(u => D(u, cole.Pos.X, cole.Pos.Z) < 3 * CoopCommander.FOLLOW_RADIUS), $"01: the AI commanders' fighters follow {cole.Name} ({string.Join(" ", army.Select(u => $"{D(u, cole.Pos.X, cole.Pos.Z):0} m"))}; Cole moved {D(cole, sx, sz):0} m)");
        }
    }

    // ======================================================================================== the dealing rules
    public static void CoopGiftRules(TestContext t)
    {
        var cfg = "base";
        // mission 9: the village joins in one action - 15 fighters dealt 5 / 5 / 5, workers and buildings to seat 1
        {
            var s = CoopTests.Session(t, CoopTests.CoopStart(t, cfg, 9, new[] { ("human", ""), ("human", ""), ("ai", "") }));
            var C = s.Campaign; var W = s.World; var R = C.Coop!;
            t.True(R.HeroPhase, "09: a hero phase (the heroes' path)");
            var p5 = C.Players[5]!;
            var all = W.Units.Cast<Entity>().Concat(W.Buildings).Where(e => e.Alive && e.Owner == p5).ToList();
            var fighters = all.OfType<Unit>().Count(u => !u.IsWorker && !R.IsBaseItem(u.Name));
            var before = R.Seats.ToDictionary(x => x.Seat, x => R.UnitsOf(x));
            Occupy(s, all);
            var got = R.Seats.ToDictionary(x => x.Seat, x => R.UnitsOf(x) - before[x.Seat]);
            t.Equal(fighters, got.Values.Sum(), $"09: the village's {fighters} fighters dealt ({string.Join(" ", got)})");
            t.True(got.Values.Max() - got.Values.Min() <= 1, $"09: an even split ({string.Join(" ", got)})");
            t.True(W.Buildings.Where(b => b.Alive && all.Contains(b)).All(b => b.Owner == R.Seats[0].Player), "09: the village's buildings are seat 1's");
            t.True(W.Units.Where(u => u.Alive && u.IsWorker && all.Contains(u)).All(u => u.Owner == R.Seats[0].Player), "09: the village's workers are seat 1's");
            s.Step();
            t.True(!R.HeroPhase, "09: the village's workers end the hero phase");
        }
        // mission 5 (no-build): single gifts to the seat with the fewest units; no enemy count scaling ever
        {
            var s = CoopTests.Session(t, CoopTests.CoopStart(t, cfg, 5, new[] { ("human", ""), ("human", ""), ("human", "") }));
            var C = s.Campaign; var W = s.World; var R = C.Coop!;
            t.True(!R.HasHeroPhase && R.Dealing && !R.EnemyScaling, "05: a no-build mission deals its gifts and does not scale the enemies' numbers");
            t.Equal(0, R.GarrisonExtra, "05: no extra garrison units");
            // no scaling at all in a no-build mission (Kacper 2026-11-20): the enemies keep their health and damage too
            t.True(C.World.Players.All(p => p.CoopDamage == 1 && p.CoopHealth == 1 && p.CoopPopBase == null), "05: no enemy health, damage or population scaling");
            t.True(C.World.Players.All(p => C.World.Brains.Get(p.Id) is not PwRemake.Sim.Ai.TribeAI ta || ta.WaveScale == 1), "05: no bigger waves");
            var counts = R.Seats.ToDictionary(x => x.Seat, x => R.UnitsOf(x));
            int fewest = R.Seats.OrderBy(x => counts[x.Seat]).ThenBy(x => x.Seat == 1 ? 9 : x.Seat).First().Seat;
            var sl = C.StartLocations[C.HumanSlot]!;
            var (mx, my) = C.ToMap(sl.X, sl.Z);
            int id = s.Host.Spawn("aje_velociraptor_handler", 0, mx, my, new PwRemake.Sim.Scripting.SpawnOptions { Level = 2 });
            var e = C.Objects.ById(id)?.Entity;
            t.True(e?.Owner == R.SeatNo(fewest)!.Player, $"05: the helper goes to seat {fewest}, the one with the fewest units (got {e?.Owner?.Id})");
        }
    }

    static double Sq(double v) => v * v;

    // ======================================================================================== mission 13
    /// <summary>MIRAGE 13 played as SEAS with Dragon Clan allies: the placed defences dealt to the allies (seat 1's,
    /// swapped to SEAS) are the allies' own tribe's again (Kacper 2026-11-11: "my yellow ally starts as Dragon Clan yet
    /// their walls / turrets are SEAS ones")</summary>
    public static void CoopSplitKeepsSeatTribe(TestContext t)
    {
        if (!HasCfg("mirage")) t.Skip("no mirage");
        var s = CoopTests.Session(t, CoopTests.CoopStart(t, "mirage", 13, new[] { ("human", "SEAS"), ("ai", "Ninigi"), ("ai", "Ninigi") }));
        var W = s.World; var R = s.Campaign.Coop!;
        foreach (var seat in R.Seats.Where(x => x.Seat > 1))
        {
            var foreign = W.Buildings.Concat<Entity>(W.Units).Where(e => e.Alive && e.Owner == seat.Player && e.Name.StartsWith("seas_", StringComparison.OrdinalIgnoreCase)).Select(e => e.Name).ToList();
            t.True(foreign.Count == 0, $"seat {seat.Seat} (Ninigi) owns no SEAS objects (got {string.Join(", ", foreign.Distinct())})");
            var own = W.Buildings.Where(b => b.Alive && b.Owner == seat.Player).Select(b => b.Name).Distinct().ToList();
            t.Log($"seat {seat.Seat}: {string.Join(", ", own)}");
            t.True(own.Any(n => n.StartsWith("ninigi_", StringComparison.OrdinalIgnoreCase) && n.Contains("tower")), $"seat {seat.Seat} owns Dragon Clan towers ({string.Join(", ", own)})");
        }
    }

    /// <summary>co-op with your own start army (Kacper: "in coop mode I still should be able to pick my starting units"):
    /// the army bought on the army screen (GameStart.Army, CoopLobby -> CampaignMenu.StartWithArmy) stands at seat 1's
    /// start in a co-op game as in single player; the AI commanders keep their seats' armies</summary>
    public static void CoopBoughtArmy(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no base");
        var mj = PwRemake.Sim.Maps.PwMapReader.MissionOf(System.IO.Path.Combine(c!.Config.MissionsDir, "02_single_02"));
        var offer = PwRemake.Sim.Campaign.CampaignArmy.Of(mj, c.Rules, "", 1000 + (mj!["min_credits"]?.GetValue<double>() ?? 0))!;
        var army = offer.Defaults();
        var PB = new PointBuy(c.Rules, null);
        t.True(PB.Buy(army, "hu_warrior", 1, offer.Total, "Hu", offer.Blocked) != null, "a warrior bought on the army screen");
        var start = CoopTests.CoopStart(t, "base", 2, new[] { ("human", "Hu"), ("ai", "Aje") });
        start.Army = army;
        var s = CoopTests.Session(t, start);
        var R = s.Campaign.Coop!;
        var me = R.Seats.First(x => x.Seat == 1).Player!;
        int warriors = s.World.Units.Count(u => u.Alive && u.Owner == me && u.Name == "hu_warrior");
        t.True(warriors >= army.Cards.Values.Count(x => x == "hu_warrior"), $"seat 1: the bought warriors ({warriors})");
        var ai = R.Seats.First(x => x.Seat == 2).Player!;
        t.True(s.World.Units.Any(u => u.Alive && u.Owner == ai), "seat 2 (AI) still has its army");
    }

    /// <summary>MIRAGE 13's tower before the gate (ninigi_small_tower_121) can be clicked, targeted and destroyed from the
    /// start in single player too, with the mission's own tribe and swapped (Kacper, three times; Campaign.IsMortal) - the
    /// map clears its hitable / selectable bits and the init trigger makes it invulnerable</summary>
    public static void MortalTower13(TestContext t)
    {
        var c1 = CampaignTests.LoadCfgPublic("mirage");
        if (c1 == null) t.Skip("no mirage");
        foreach (var tribe in new[] { "", "SEAS", "Hu" })
        {
            var sp = MissionSession.Create(new GameStart { Configuration = "mirage", Mission = 13, Tribe = tribe, Seed = 1, Difficulty = 1 }, c1!.Config,
                new SessionOptions { Rules = c1.Rules, Models = c1.Models, Forest = c1.Forest, Brushes = c1.Brushes, AiData = c1.Ai, DataDir = c1.Data, NoBrains = true });
            sp.Run(5);
            var tw = sp.Campaign.Objects.Get("ninigi_small_tower_121")?.Entity as Building;
            string w = $"single 13 ({(tribe == "" ? "own tribe" : tribe)})";
            t.True(tw != null && tw.Alive, $"{w}: the gate tower stands ({tw?.Name})");
            t.True(tw != null && tw.InvulnT == 0, $"{w}: the gate tower can be destroyed (InvulnT {tw?.InvulnT})");
            t.True(tw != null && !tw.Unselectable && !tw.Untargetable, $"{w}: the gate tower can be clicked and targeted (unselectable {tw?.Unselectable}, untargetable {tw?.Untargetable})");
            if (tw != null) { double hp = tw.Hp; sp.World.Damage(tw, 50, null); t.True(tw.Hp < hp, $"{w}: the gate tower takes damage ({hp} -> {tw.Hp})"); }
        }
        // co-op played as the SEAS
        var cs = CoopTests.Session(t, CoopTests.CoopStart(t, "mirage", 13, new[] { ("human", "SEAS"), ("ai", "Ninigi") }));
        cs.Run(5);
        var ct = cs.Campaign.Objects.Get("ninigi_small_tower_121")?.Entity as Building;
        t.True(ct != null && ct.Alive && ct.InvulnT == 0 && !ct.Unselectable && !ct.Untargetable,
            $"co-op 13 as SEAS: the gate tower ({ct?.Name}) can be clicked, targeted and destroyed (InvulnT {ct?.InvulnT}, unselectable {ct?.Unselectable}, untargetable {ct?.Untargetable})");
    }

    public static void CoopSecondGate13(TestContext t)
    {
        foreach (var cfg in Cfgs)
        {
            if (!HasCfg(cfg)) continue;
            foreach (int seats in new[] { 2, 3 })
            {
                var s = CoopTests.Session(t, CoopTests.CoopStart(t, cfg, 13, new[] { ("human", ""), ("human", ""), ("human", "") }.Take(seats).ToArray()));
                var C = s.Campaign; var W = s.World; var R = C.Coop!;
                string w = $"{cfg} 13 ({seats} seats)";
                var gates = W.Buildings.Where(b => b.Alive && b.Name == "hcl13_gate").ToList();
                t.Equal(seats == 3 ? 2 : 1, gates.Count, $"{w}: city gates");
                foreach (var n in new[] { "hcl13_city_wall_79", "hcl13_city_wall_74", "hcl13_city_wall_85" })
                {
                    var rec = C.Objects.Get(n);
                    if (seats == 3) t.True(rec?.Entity?.Name == "hcl13_gate", $"{w}: {n} now stands for the second gate (X01 watches it)");
                    else t.True(rec?.Entity?.Name == "hcl13_city_wall", $"{w}: {n} is a wall piece");
                }
                // the defence split: seats 2 / 3 own placed towers, seat 1 keeps its base
                foreach (var seat in R.Seats.Where(x => x.Seat > 1))
                {
                    int towers = W.Buildings.Count(b => b.Alive && b.Owner == seat.Player && b.Name == "ninigi_small_tower");
                    t.True(towers >= 2, $"{w}: seat {seat.Seat} owns {towers} of the placed towers");
                    t.True(UnitsOf(W, seat.Player).Count(u => !u.IsWorker && u.Def?.Unique != true) >= 3, $"{w}: seat {seat.Seat} got a share of the start fighters");
                }
                // MIRAGE: the gate tower the original keeps invulnerable until the main attack is over can be destroyed
                // in co-op (`mortal`); its start trigger (Id02) has run after a few steps
                if (cfg == "mirage")
                {
                    s.Run(2);
                    var tw = C.Objects.Get("ninigi_small_tower_121")?.Entity as Building;
                    t.True(tw != null && tw.InvulnT == 0, $"{w}: the gate tower ninigi_small_tower_121 can be destroyed (InvulnT {tw?.InvulnT})");
                    t.True(tw != null && !tw.Unselectable && !tw.Untargetable, $"{w}: the gate tower can be clicked and targeted (unselectable {tw?.Unselectable}, untargetable {tw?.Untargetable})");
                }
                int all = W.Buildings.Count(b => b.Alive && b.Name == "ninigi_small_tower" && R.IsSeatSlot(b.Owner?.Id ?? -1));
                var c1 = CampaignTests.LoadCfgPublic(cfg)!;
                var sp = MissionSession.Create(new GameStart { Configuration = cfg, Mission = 13, Seed = 1, Difficulty = 1 }, c1.Config, new SessionOptions { Rules = c1.Rules, Models = c1.Models, Forest = c1.Forest, Brushes = c1.Brushes, AiData = c1.Ai, DataDir = c1.Data, NoBrains = true, NoTriggers = true });
                int single = sp.World.Buildings.Count(b => b.Alive && b.Name == "ninigi_small_tower" && b.Owner == sp.Human);
                t.Equal(single, all, $"{w}: the placed towers ({single}), split (not copied)");
                if (seats == 3)
                {
                    var gb = (Building)C.Objects.Get("hcl13_city_wall_74")!.Entity!;
                    // in the wall line: along the line from wall 77's end to wall 81's (the pieces left on both sides),
                    // its ends in them (no gap), turned as the mission's own gate to its walls (CoopRuntime.GatePlace)
                    var w77 = (Building)C.Objects.Get("hcl13_city_wall_77")!.Entity!; var w81 = (Building)C.Objects.Get("hcl13_city_wall_81")!.Entity!;
                    (double X, double Z) End(Building b, double sign) => (b.Pos.X + sign * b.Hx * Math.Cos(b.Rot), b.Pos.Z - sign * b.Hx * Math.Sin(b.Rot));
                    (double X, double Z) A = new[] { End(w77, 1), End(w77, -1) }.MinBy(p => Sq(p.X - gb.Pos.X) + Sq(p.Z - gb.Pos.Z));
                    (double X, double Z) B = new[] { End(w81, 1), End(w81, -1) }.MinBy(p => Sq(p.X - gb.Pos.X) + Sq(p.Z - gb.Pos.Z));
                    double lx = B.X - A.X, lz = B.Z - A.Z, ll = Math.Sqrt(lx * lx + lz * lz);
                    double gx = Math.Cos(gb.Rot), gz = -Math.Sin(gb.Rot);                // the gate's long side (local x)
                    t.True(Math.Abs(gx * lz - gz * lx) / ll < 0.01, $"{w}: the second gate along the wall line (sin {Math.Abs(gx * lz - gz * lx) / ll:0.000})");
                    t.True(Math.Abs((gb.Pos.X - A.X) * lz - (gb.Pos.Z - A.Z) * lx) / ll < 0.3, $"{w}: the second gate on the wall line");
                    t.True(gb.Hx * 2 >= ll, $"{w}: the second gate ({gb.Hx * 2:0.0} m) fills the gap between the walls ({ll:0.0} m)");
                    var own = W.Buildings.First(b => b.Alive && b.Name == "hcl13_gate" && b != gb);
                    t.True(Math.Abs(Math.IEEERemainder(gb.Rot - w81.Rot - (own.Rot - ((Building)C.Objects.Get("hcl13_city_wall_80")!.Entity!).Rot), 2 * Math.PI)) < 0.2, $"{w}: turned to its walls as the mission's gate");
                    // the pavement of the mission's gate (ground material 5) through it
                    double tx = Math.Sin(gb.Rot), tz = Math.Cos(gb.Rot);                 // through the opening (local z)
                    foreach (double d in new[] { -12.0, 0, 12 })
                    {
                        var (mx, my) = C.ToMap(gb.Pos.X + tx * d, gb.Pos.Z + tz * d);
                        t.Equal(5, s.Map.Md!.MaterialAtMap(mx, my), $"{w}: paving {d} m through the second gate");
                    }
                    // a way through it for its owner's side (MIRAGE: the gate opens for them; base: hcl13_gate is no
                    // gate class - shut like the mission's own), none for the attackers; nearby (40 m) from 12 m outside
                    // to 12 m inside
                    var nav = W.Nav; var foe = W.Players.First(p => p.IsEnemy(gb.Owner!));
                    bool Free(int k, Player p) => nav.Block[k] == 0 && !nav.GateBlocks(k, p);
                    bool Through(Player p)
                    {
                        int n = nav.N, k0 = nav.Idx(gb.Pos.X + tx * 14, gb.Pos.Z + tz * 14);
                        var seen = new HashSet<int> { k0 }; var st = new Stack<int>(); st.Push(k0);
                        while (st.Count > 0)
                        {
                            int c = st.Pop(), ci = c % n, cj = c / n;
                            for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
                            {
                                int k = (cj + dj) * n + ci + di;
                                // (no corner cutting, as the pathfinder)
                                if ((di == 0 && dj == 0) || seen.Contains(k) || !Free(k, p) || (di != 0 && dj != 0 && (!Free(cj * n + ci + di, p) || !Free((cj + dj) * n + ci, p)))) continue;
                                double qx = (ci + di + 0.5) * nav.Cell - nav.Half - gb.Pos.X, qz = (cj + dj + 0.5) * nav.Cell - nav.Half - gb.Pos.Z;
                                if (qx * qx + qz * qz > 40 * 40) continue;
                                if (qx * tx + qz * tz < -12) return true;                      // inside
                                seen.Add(k); st.Push(k);
                            }
                        }
                        return false;
                    }
                    t.True(Free(nav.Idx(gb.Pos.X + tx * 14, gb.Pos.Z + tz * 14), foe), $"{w}: the attackers reach the second gate");
                    t.True(Through(gb.Owner!) == (cfg == "mirage"), $"{w}: the owner's units walk through the second gate: {Through(gb.Owner!)}");
                    t.True(!Through(foe), $"{w}: the second gate and the walls next to it stop the attackers");
                    // no gap where it meets the walls: across the joins nobody walks (4 m either side of each end)
                    foreach (var (ex, ez) in new[] { A, B })
                        for (double a = -4; a <= 4; a += 1)
                        {
                            double px = ex + lx / ll * a, pz = ez + lz / ll * a;
                            bool shut = false;
                            for (double d = -6; d <= 6; d += 0.5) if (!Free(nav.Idx(px + tx * d, pz + tz * d), foe)) { shut = true; break; }
                            t.True(shut, $"{w}: the wall line closed at ({px:0.0}, {pz:0.0}) next to the second gate");
                        }
                    // the second gate falls: the mission is lost (X01, the game's own text)
                    var g2 = C.Objects.Get("hcl13_city_wall_74")!.Entity!;
                    for (int i = 0; i < 40; i++) s.Step();
                    W.Kill(g2, null);
                    for (int i = 0; i < 600 && !s.Over; i++) s.Step();
                    t.True(!g2.Alive, $"{w}: the second gate is down");
                    t.True(s.Over && !s.Won, $"{w}: the second gate destroyed - the mission is lost");
                }
            }
        }
    }

    // ======================================================================================== fairness
    /// <summary>a seat's base within this share of the map's width of seat 1's base</summary>
    public const double NearShare = 0.4;
    /// <summary>missions whose Fair targets are not enemy bases (13: the gates the seats defend)</summary>
    public static readonly HashSet<int> TargetsNotEnemies = new() { 13 };
    /// <summary>named exceptions to NearShare (mission, seat) - docs/COOP.md "Fair bases"</summary>
    public static readonly Dictionary<(int Mission, int Seat), string> NearExceptions = new()
    {
        [(10, 3)] = "islands kept: the cliff ring leaves no sea for an island nearer than the west edge's",
    };
    /// <summary>per mission: seat 1's base (map coordinates) and what the seats walk to - the enemy bases / the
    /// objective (x, z, r); naval: the sea counts like a walk</summary>
    public static readonly Dictionary<int, ((double X, double Z) Seat1, (double X, double Z, double R)[] Targets, bool Naval)> Fair = new()
    {
        [0] = ((711, 472), new[] { (450.0, 535.0, 40.0) }, false),
        [1] = ((568, 395), new[] { (753.0, 440.0, 45.0), (863, 373, 55) }, false),
        [2] = ((474, 1011), new[] { (689.0, 298.0, 50.0) }, false),
        [3] = ((466, 247), new[] { (788.0, 697.0, 55.0) }, false),
        [4] = ((662, 929), new[] { (764.0, 629.0, 55.0), (548, 336, 50) }, false),
        [6] = ((1236, 467), new[] { (1362.0, 726.0, 50.0), (838, 565, 45), (808, 362, 45), (492, 373, 45), (355, 598, 45) }, true),
        [7] = ((563, 919), new[] { (532.0, 375.0, 50.0), (748, 756, 50), (962, 748, 45) }, false),
        [8] = ((254, 1181), new[] { (526.0, 804.0, 50.0), (336, 535, 50) }, false),
        [9] = ((553, 835), new[] { (247.0, 854.0, 50.0), (288, 442, 50), (628, 577, 50) }, true),
        [10] = ((507, 927), new[] { (307.0, 668.0, 45.0), (774, 842, 45), (506, 285, 50) }, false),
        [12] = ((677, 275), new[] { (575.0, 800.0, 80.0), (1021, 262, 45) }, true),
        [13] = ((750, 410), new[] { (821.0, 330.0, 22.0), (729, 312, 22) }, false),
        [14] = ((745, 988), new[] { (642.0, 489.0, 40.0), (1049, 489, 40), (833, 372, 35), (315, 327, 35), (390, 443, 40) }, false),
        [15] = ((555, 1025), new[] { (524.0, 420.0, 40.0), (725, 419, 40), (193, 735, 40), (1085, 766, 35), (613, 769, 30) }, true),
    };

    /// <summary>the walk (metres) from every cell to the nearest target: Dijkstra on the nav grid (gates open; naval: the
    /// ships' water too)</summary>
    static double[] Walk(MissionSession s, (double X, double Z, double R)[] targets, bool naval)
    {
        var W = s.World; var nav = W.Nav; int n = nav.N; double cell = nav.Cell;
        var wn = naval ? W.WaterNav : null;
        bool Free(int k)
        {
            if (nav.Block[k] == 0 || nav.GateAt.ContainsKey(k)) return true;
            // (naval: the ships' water, and the shore next to it - a transport lands on any coast)
            if (wn != null)
            {
                var (x, z) = nav.Center(k);
                for (int dz = -6; dz <= 6; dz += 6) for (int dx = -6; dx <= 6; dx += 6) if (wn.Block[wn.Idx(x + dx, z + dz)] == 0) return true;
            }
            return false;
        }
        var dist = new double[n * n];
        Array.Fill(dist, double.PositiveInfinity);
        var pq = new PriorityQueue<int, double>();
        foreach (var (tx, tz, tr) in targets)
        {
            var (gx, gz) = s.Campaign.ToGame(tx, tz);
            int r = (int)Math.Ceiling((tr + 10) / cell), ci = nav.Idx(gx, gz);
            for (int dj = -r; dj <= r; dj++)
                for (int di = -r; di <= r; di++)
                {
                    int i = ci % n + di, j = ci / n + dj;
                    if (i < 0 || j < 0 || i >= n || j >= n || (di * di + dj * dj) * cell * cell > (tr + 10) * (tr + 10)) continue;
                    int k = j * n + i;
                    if (!Free(k) || dist[k] == 0) continue;
                    dist[k] = 0; pq.Enqueue(k, 0);
                }
        }
        while (pq.TryDequeue(out int c, out double d))
        {
            if (d > dist[c]) continue;
            int ci = c % n, cj = c / n;
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int i = ci + di, j = cj + dj;
                    if (i < 0 || j < 0 || i >= n || j >= n) continue;
                    int k = j * n + i;
                    if (!Free(k)) continue;
                    if (di != 0 && dj != 0 && (!Free(cj * n + i) || !Free(j * n + ci))) continue;
                    double nd = d + cell * (di != 0 && dj != 0 ? 1.41421356 : 1);
                    if (nd < dist[k]) { dist[k] = nd; pq.Enqueue(k, nd); }
                }
        }
        return dist;
    }
    static double At(MissionSession s, double[] dist, double gx, double gz, double r = 40)
    {
        var nav = s.World.Nav; double best = double.PositiveInfinity;
        for (double dz = -r; dz <= r; dz += nav.Cell)
            for (double dx = -r; dx <= r; dx += nav.Cell)
            {
                if (dx * dx + dz * dz > r * r) continue;
                int k = nav.Idx(gx + dx, gz + dz);
                if (k >= 0 && k < dist.Length && dist[k] + Math.Sqrt(dx * dx + dz * dz) < best) best = dist[k] + Math.Sqrt(dx * dx + dz * dz);
            }
        return best;
    }

    /// <summary>the seats' bases are fair and near seat 1's: straight-line distances on the co-op map (after the start or
    /// the hero phase's hand-over). D1 = seat 1's base to the nearest of the mission's own enemy buildings (hostile at
    /// the start) and the enemy bases of Fair (the bases the story turns hostile later; not mission 13's gates). Every
    /// other seat's base is at least D1 from every enemy building (the definition's additions included) and those bases,
    /// no addition stands nearer than D1 to seat 1's base, and every seat's base lies within NearShare of the map's width
    /// (single player's) of seat 1's</summary>
    public static void CoopFairness(TestContext t)
    {
        var problems = new List<string>();
        foreach (var cfg in Cfgs)
        {
            if (!HasCfg(cfg)) continue;
            foreach (var (id, f) in Fair.OrderBy(x => x.Key))
            {
                if (Environment.GetEnvironmentVariable("PW_COOP_MISSIONS") is { Length: > 0 } only && !only.Split(',').Contains(id.ToString())) continue;
                foreach (int seats in new[] { 2, 3 })
                {
                    var start = CoopTests.CoopStart(t, cfg, id, new[] { ("human", ""), ("human", ""), ("ai", "") }.Take(seats).ToArray());
                    var c = CampaignTests.LoadCfgPublic(cfg)!;
                    var s = MissionSession.Create(start, c.Config, new SessionOptions { Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = true, NoTriggers = true });
                    var C = s.Campaign; var R = C.Coop!; var W = s.World; var hu = C.Human!;
                    if (R.HeroPhase) R.HandOver();
                    var added = new HashSet<Entity>(R.Enemies.List.SelectMany(e => e.Placed));
                    var hostile = W.Buildings.Where(b => b.Alive && b.Owner != null && R.SeatOf(b.Owner) == null && hu.IsEnemy(b.Owner)).ToList();
                    var own = hostile.Where(b => !added.Contains(b)).Select(b => (b.Pos.X, b.Pos.Z)).ToList();
                    if (!TargetsNotEnemies.Contains(id)) own.AddRange(f.Targets.Select(q => C.ToGame(q.X, q.Z)));
                    var adds = W.Buildings.Where(b => b.Alive && added.Contains(b)).Select(b => (b.Pos.X, b.Pos.Z)).ToList();
                    static double D((double X, double Z) a, (double X, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
                    static double Near(List<(double X, double Z)> l, (double X, double Z) p) => l.Count == 0 ? double.PositiveInfinity : l.Min(q => D(q, p));
                    var g1 = C.ToGame(f.Seat1.X, f.Seat1.Z);
                    double d1 = Near(own, g1), a1 = Near(adds, g1);
                    var ext = s.Map.Md?.Extension;
                    double mapW = ext != null ? ext.OldX1 - ext.OldX0 : s.Map.Play.X1 - s.Map.Play.X0 + 64, near = NearShare * mapW;
                    string w = $"{cfg} {id:00} ({seats} seats)";
                    var line = new List<string> { $"seat 1: enemy {d1:0} m, additions {a1:0} m" };
                    if (a1 < d1) problems.Add($"{w}: an enemy addition {a1:0} m from seat 1's base (D1 {d1:0} m)");
                    foreach (var seat in R.Seats.Where(x => x.Seat > 1))
                    {
                        var sd = seat.Def!;
                        (double X, double Z) p = (sd.X!.Value, sd.Z!.Value);
                        double e = Math.Min(Near(own, p), Near(adds, p)), d = D(p, g1);
                        line.Add($"seat {seat.Seat}: enemy {e:0} m, {d:0} m from seat 1");
                        if (e < d1) problems.Add($"{w}: seat {seat.Seat} {e:0} m from an enemy building (D1 {d1:0} m)");
                        if (d > near && !NearExceptions.ContainsKey((id, seat.Seat))) problems.Add($"{w}: seat {seat.Seat} {d:0} m from seat 1's base (at most {near:0} m)");
                    }
                    t.Log($"{w}: {string.Join(", ", line)}");
                }
            }
        }
        foreach (var p in problems) t.Log(p);
        t.Equal(0, problems.Count, "unfair base places (listed above)");
    }

    // ======================================================================================== the playthrough in co-op
    public static void CoopPlay01(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_PLAY") != "1") t.Skip("PW_PLAY=1 runs it (a whole mission)");
        var cfg = Environment.GetEnvironmentVariable("PW_COOP_PLAY_CFG") ?? "base";
        var start = CoopTests.CoopStart(t, cfg, 1, new[] { ("human", ""), ("human", ""), ("human", "") });
        var c = CampaignTests.LoadCfgPublic(cfg)!;
        var P = new Playthrough.PlayPresenter();
        var s = MissionSession.Create(start, c.Config, new SessionOptions { Presenter = P, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data });
        var R = s.Campaign.Coop!;
        var bot = new Playthrough.Play01(s, P);
        string log = bot.Play();
        t.Log(log);
        foreach (var g in R.GiftLog) t.Log($"  {g.T:0} s: {g.Cls} -> seat {g.Seat} (units before: {g.Units})");
        t.True(R.HandedOverAt != null, "the bases were handed out (Béla freed the workers)");
        // the groups of the story, dealt: the village's warriors, the spearmen, Béla's archers - every seat one or more
        foreach (var cls in new[] { "hu_warrior", "hu_spearman", "hu_archer" })
        {
            var g = R.GiftLog.Where(x => x.Cls == cls && (R.HandedOverAt == null || x.T <= R.HandedOverAt)).GroupBy(x => x.Seat).ToDictionary(x => x.Key, x => x.Count());
            t.True(R.Seats.All(x => g.GetValueOrDefault(x.Seat) >= 1), $"{cls}: every seat got one ({string.Join(" ", g.OrderBy(x => x.Key))})");
        }
        foreach (var seat in R.Seats.Where(x => x.Seat > 1))
            t.True(s.World.Buildings.Any(b => b.Owner == seat.Player), $"seat {seat.Seat} built its base");
        t.True(s.Over && s.Won, "the mission is won in co-op");
    }
}
