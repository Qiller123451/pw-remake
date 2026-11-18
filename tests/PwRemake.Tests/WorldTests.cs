// Tests of the world core (src/Sim/World, src/Sim/Commands, src/Sim/Content/ModelManifest): path finding on a flat
// map with obstacles, units of every tribe spawned from the rules and the world stepped for a minute, determinism
// (same seed + same commands = same state hash), the animation controller's timing, the model data.
//
// Game data: PW_DATA / PW_DATA_MIRAGE (default /opt/data/base, /opt/data/mirage). Model files (.glb) of the toolkit's
// export for the model data tests: PW_MODELS (default the staged export of the user's PC); without them those tests
// are skipped and the world runs on the manifest alone (no clips, default sizes).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class WorldTests
{
    static readonly Dictionary<string, (Rules R, ModelManifest M)> s_data = new();

    static string ModelsDir() => Environment.GetEnvironmentVariable("PW_MODELS") is { Length: > 0 } s ? s : "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a/assets/models";

    /// <summary>rules and manifest of a data folder (the models of PW_MODELS when they are there)</summary>
    static (Rules R, ModelManifest M) Load(string data)
    {
        if (s_data.TryGetValue(data, out var d)) return d;
        var R = Rules.Load(data);
        var man = Json.ParseFile(Path.Combine(data, "assets", "manifest.json"));
        if (man == null) throw new Exception("no manifest in " + data);
        string md = ModelsDir();
        var M = new ModelManifest(man, null, n => { var p = Path.Combine(md, n + ".glb"); return File.Exists(p) ? p : null; });
        return s_data[data] = (R, M);
    }

    static string Manifested(TestContext t, string data)
    {
        if (!File.Exists(Path.Combine(data, "assets", "manifest.json"))) t.Skip($"no assets/manifest.json in {data}");
        return data;
    }

    // ------------------------------------------------------------------ navigation
    public static void NavFindsWayAroundObstacles(TestContext t)
    {
        var nav = new NavGrid(200, 2);
        // a wall from z = -60 to z = 60 at x = 0 with a gap at z = 40..46
        for (double z = -60; z <= 60; z += 1) if (z < 40 || z > 46) nav.MarkCircle(0, z, 1.5);
        nav.LabelRegions();
        var p = nav.Find(-30, 0, 30, 0, 0.7);
        t.True(p != null && nav.Found, "a way through the gap");
        t.Near(30, p![^1].X, 1e-9, "ends at the goal (x)");
        t.Near(0, p[^1].Z, 1e-9, "ends at the goal (z)");
        double px = -30, pz = 0;
        foreach (var w in p) { t.True(nav.Los(px, pz, w.X, w.Z), $"leg to {w} free"); px = w.X; pz = w.Z; }
        t.True(p.Any(w => w.Z > 30), "goes round through the gap");
        // a big unit (radius 3) does not fit through a 6 m gap minus clearance: it finds no full way or a way of its own
        var big = nav.Find(-30, 0, 30, 0, 3);
        t.True(big == null || !nav.Found || big.All(w => nav.Clear[nav.Idx(w.X, w.Z)] >= nav.Need(3)), "a big unit's way keeps its clearance");
        // a closed box: the goal inside cannot be reached; the way ends at the nearest reachable cell (regions)
        var nav2 = new NavGrid(200, 2);
        for (double a = 0; a < Math.PI * 2; a += 0.02) nav2.MarkCircle(50 + Math.Cos(a) * 12, 50 + Math.Sin(a) * 12, 1.2);
        nav2.LabelRegions();
        var q = nav2.Find(-50, -50, 50, 50, 0.7);
        t.True(q != null, "a way towards an enclosed goal");
        double d = Math.Sqrt((q![^1].X - 50) * (q[^1].X - 50) + (q[^1].Z - 50) * (q[^1].Z - 50));
        t.True(d > 10 && d < 18, $"ends at the enclosure's edge (distance {d:0.0})");
    }

    /// <summary>the coarse graph kept up to date by blocks (Mark / MarkCircle note their blocks) is the graph a whole
    /// rebuild makes of the same grid - nodes, centres, links in the same order -, and the far ways are the same</summary>
    public static void NavCoarseIncremental(TestContext t)
    {
        var rng = new Random(5);
        var inc = new NavGrid(600, 2);
        for (double x = -300; x <= 300; x += 1) if (x < 180 || x > 190) inc.MarkCircle(x, 0, 1.5);
        inc.LabelRegions();
        inc.CoarseSignature(); inc.UpdateClearance();            // (built whole once)
        var marks = new List<(List<int> Cells, int D)>();
        for (int round = 0; round < 60; round++)
        {
            // buildings placed and removed, round and square ones, some on the block borders, some side by side
            if (marks.Count > 0 && rng.NextDouble() < 0.35) { int i = rng.Next(marks.Count); inc.Mark(marks[i].Cells, -1); marks.RemoveAt(i); }
            else if (rng.NextDouble() < 0.5) marks.Add((inc.MarkCircle(rng.Next(-280, 280), rng.Next(-280, 280), 1 + rng.NextDouble() * 9), 1));
            else { var c = inc.RectCells(rng.Next(-280, 280), rng.Next(-280, 280), 1 + rng.Next(8), 1 + rng.Next(8), rng.NextDouble() * 3); inc.Mark(c, 1); marks.Add((c, 1)); }
            if (round % 3 != 0) continue;                       // (several changes between two searches too)
            var whole = new NavGrid(600, 2);
            Array.Copy(inc.Block, whole.Block, inc.Block.Length);
            whole.Version = 1;                                   // (an unknown change: built whole)
            bool same = inc.CoarseSignature() == whole.CoarseSignature();
            t.True(same, $"round {round}: the graph kept by blocks is the whole one");
            if (!same) return;
            // the clearance worked out near the changes only is the whole grid's
            inc.UpdateClearance(); whole.UpdateClearance();
            t.True(inc.Clear.AsSpan().SequenceEqual(whole.Clear), $"round {round}: the clearance near the changes is the whole one");
            double sx = rng.Next(-280, 280), sz = rng.Next(-280, -50), tx = rng.Next(-280, 280), tz = rng.Next(50, 280);
            var a = inc.Find(sx, sz, tx, tz, 0.7); var b = whole.Find(sx, sz, tx, tz, 0.7);
            t.True((a == null) == (b == null) && (a == null || a.Count == b!.Count && a.Zip(b).All(p => p.First.X == p.Second.X && p.First.Z == p.Second.Z)), $"round {round}: the same way");
        }
        t.Equal(1, inc.CoarseWholeBuilds, "built whole once, then by blocks");
        t.Equal(1, inc.ClearWholeBuilds, "the clearance worked out whole once, then near the changes");
    }

    public static void NavCoarseRouteForFarGoals(TestContext t)
    {
        // 600 m map, a long wall with one gap far away: the goal 400 m off goes by the coarse route + legs
        var nav = new NavGrid(600, 2);
        for (double x = -300; x <= 300; x += 1) if (x < 180 || x > 190) nav.MarkCircle(x, 0, 1.5);
        nav.LabelRegions();
        var p = nav.Find(-200, -150, -200, 150, 0.7);
        t.True(p != null && nav.Found, "a far way found");
        t.True(p!.Any(w => w.X > 170), "through the far gap");
        double px = -200, pz = -150;
        foreach (var w in p!) { t.True(nav.Los(px, pz, w.X, w.Z), "each leg free"); px = w.X; pz = w.Z; }
    }

    public static void SpatialHashQueries(TestContext t)
    {
        var map = new FlatMap(200);
        var (R, M) = Load(Manifested(t, RulesTestData.Base(t)));
        var W = new W_(R, M, map);
        var p = new Player(0, "Hu", new PlayerOpts { Rules = R });
        W.Players.Add(p);
        var a = W.SpawnUnit("hu_warrior", p, 10, 10)!;
        var b = W.SpawnUnit("hu_warrior", p, 40, 10)!;
        t.Equal(1, W.UHash.Near(10, 10, 5).Count(u => u == a || u == b), "only the near one");
        t.True(W.UHash.Near(25, 10, 20).Contains(a) && W.UHash.Near(25, 10, 20).Contains(b), "both within 20 m");
        t.Equal(a, W.ById(a.Id), "by id");
    }

    // ------------------------------------------------------------------ spawning and stepping
    static readonly string[] TRIBES = { "Hu", "Aje", "Ninigi", "SEAS" };

    static W_ ArmyWorld(Rules R, ModelManifest M, ulong seed, out List<Unit> spawned)
    {
        var map = new FlatMap(512);
        map.Blocked.Add((-20, -100, -10, 100));      // a ridge in the middle with ways round it
        var W = new W_(R, M, map, new WorldOptions { Seed = seed });
        spawned = new List<Unit>();
        int pid = 0;
        foreach (var tribe in TRIBES)
        {
            if (R.Base.Start["Objects"]?[tribe] == null) continue;
            var p = new Player(pid, tribe, new PlayerOpts { Rules = R, Team = pid });
            W.Players.Add(p);
            double bx = -150 + pid * 100, bz = 120;
            var st = R.StartOf(tribe);
            if (st != null) W.PlaceBuilding(st.Base, p, bx, bz, 0, true);
            int k = 0;
            foreach (var name in R.IndexOrder)
            {
                var ix = R.Info(name)!;
                if (ix.Tribe != tribe || ix.Type == "BLDG" || ix.Type == "NEST") continue;
                double x = bx - 40 + (k % 10) * 6, z = bz - 60 - (k / 10) * 6;
                var u = W.SpawnUnit(name, p, x, z);
                if (u != null) spawned.Add(u);
                k++;
            }
            pid++;
        }
        // some wild animals of the world
        int w = 0;
        foreach (var name in R.IndexOrder)
        {
            var ix = R.Info(name)!;
            if (ix.Tribe != "World" || ix.Type != "ANML" || w >= 12) continue;
            var u = W.SpawnUnit(name, null, 150 + (w % 4) * 8, -150 + (w / 4) * 8);
            if (u != null) { spawned.Add(u); w++; }
        }
        return W;
    }

    static void RunArmy(TestContext t, string data)
    {
        var (R, M) = Load(Manifested(t, data));
        var W = ArmyWorld(R, M, 7, out var spawned);
        t.True(spawned.Count > 50, $"units of every tribe spawned ({spawned.Count})");
        foreach (var tribe in TRIBES) if (W.Players.Any(p => p.Tribe == tribe)) t.True(spawned.Any(u => u.Owner?.Tribe == tribe), $"{tribe} units spawned");
        t.Log($"{data}: {spawned.Count} units, {W.Buildings.Count} buildings, {W.Players.Count} players");
        // orders: every player's units walk to the other side (round the ridge), in formation
        var sink = new LocalCommandSink(W);
        foreach (var p in W.Players)
        {
            var mine = W.Units.Where(u => u.Owner == p).Select(u => u.Id).ToArray();
            sink.Submit(new GroupMoveCommand { Player = p.Id, Units = mine, X = 60 + p.Id * 20, Z = -100, Type = p.Id % 2 == 0 ? "move" : "attackmove" });
        }
        int events = 0;
        for (int i = 0; i < 1200; i++)
        {
            W.Step();
            events += W.DrainEvents().Count;
        }
        t.Near(60, W.Time, 1e-6, "60 s of game time");
        foreach (var u in W.Units) t.True(double.IsFinite(u.Pos.X) && double.IsFinite(u.Pos.Z) && double.IsFinite(u.Pos.Y), $"{u} has a position");
        var moved = W.Units.Count(u => u.Owner != null && u.Pos.Z < 0);
        t.Log($"after 60 s: {moved} of {W.Units.Count(u => u.Owner != null)} owned units across, {events} events, results {string.Join(",", W.CommandResults.Select(r => r.Reason ?? "ok"))}");
        t.True(moved > W.Units.Count(u => u.Owner != null) / 3, "the armies walked round the ridge");
    }

    public static void SpawnEveryTribeAndStepBase(TestContext t) => RunArmy(t, RulesTestData.Base(t));
    public static void SpawnEveryTribeAndStepMirage(TestContext t) => RunArmy(t, RulesTestData.Mirage(t));

    // ------------------------------------------------------------------ determinism
    public static void DeterministicWithSameSeedAndCommands(TestContext t)
    {
        var (R, M) = Load(Manifested(t, RulesTestData.Base(t)));
        ulong Run(ulong seed)
        {
            var W = ArmyWorld(R, M, seed, out _);
            var sink = new LocalCommandSink(W);
            for (int i = 0; i < 400; i++)
            {
                if (i % 100 == 0)
                    foreach (var p in W.Players)
                    {
                        var mine = W.Units.Where(u => u.Owner == p).Select(u => u.Id).ToArray();
                        sink.Submit(new GroupMoveCommand { Player = p.Id, Units = mine, X = (i / 100 % 2 == 0 ? 80 : -120) + p.Id * 10, Z = (i / 100 % 2 == 0 ? -90 : 90) });
                    }
                W.Step();
                W.DrainEvents();
            }
            return W.StateHash();
        }
        ulong a = Run(42), b = Run(42), c = Run(43);
        t.Log($"hashes {a:x16} {b:x16} {c:x16}");
        t.Equal(a, b, "same seed and commands: same state");
        t.True(a != c, "another seed: another game (the idle animations and scans draw random numbers)");
    }

    // ------------------------------------------------------------------ commands
    public static void CommandsObeyOnlyTheirPlayer(TestContext t)
    {
        var (R, M) = Load(Manifested(t, RulesTestData.Base(t)));
        var W = new W_(R, M, new FlatMap(256));
        var p0 = new Player(0, "Hu", new PlayerOpts { Rules = R }); var p1 = new Player(1, "Aje", new PlayerOpts { Rules = R });
        W.Players.Add(p0); W.Players.Add(p1);
        var u = W.SpawnUnit("hu_warrior", p0, 0, 0)!;
        W.Enqueue(new OrderCommand { Player = 1, Units = new[] { u.Id }, Type = "move", X = 50, Z = 0 });
        W.Step();
        t.Equal("idle", u.Task.Type, "another player's command does nothing");
        W.Enqueue(new OrderCommand { Player = 0, Units = new[] { u.Id }, Type = "move", X = 50, Z = 0 });
        t.Equal("idle", u.Task.Type, "a command waits for the next step");
        W.Step();
        t.Equal("move", u.Task.Type, "applied at the start of the step");
        for (int i = 0; i < 400 && u.Task.Type != "idle"; i++) W.Step();
        t.True(Math.Abs(u.Pos.X - 50) < 1.5 && Math.Abs(u.Pos.Z) < 1.5, $"arrived ({u.Pos})");
        // queued orders and patrol
        W.Enqueue(new OrderCommand { Player = 0, Units = new[] { u.Id }, Type = "move", X = 50, Z = 40 });
        W.Enqueue(new OrderCommand { Player = 0, Units = new[] { u.Id }, Type = "move", X = 0, Z = 40, Queue = true });
        W.Step();
        t.True(u.Task.Then != null && u.Task.Then.X == 0 && u.Task.Then.Z == 40, "the second walk is queued");
        for (int i = 0; i < 800 && !(u.Task.Type == "idle" && u.Task.Then == null); i++) W.Step();
        t.True(Math.Abs(u.Pos.X) < 1.5 && Math.Abs(u.Pos.Z - 40) < 1.5, $"walked both legs ({u.Pos})");
    }

    // ------------------------------------------------------------------ animation timing
    public static void AnimationPlaysOnceAndLoops(TestContext t)
    {
        var (R, M) = Load(Manifested(t, RulesTestData.Base(t)));
        var W = new W_(R, M, new FlatMap(128));
        var p = new Player(0, "Hu", new PlayerOpts { Rules = R });
        W.Players.Add(p);
        var u = W.SpawnUnit("hu_warrior", p, 0, 0)!;
        // a clip set of our own: a loop clip with start / loop / end parts, and a clip played once
        var cs = new ClipSet();
        cs.Add("walk_1", 2); cs.Add("walk_1#s", 0.5); cs.Add("walk_1#l", 1.0); cs.Add("walk_1#e", 0.5);
        cs.Add("hit", 0.8); cs.Add("standanim", 3);
        var A = new AnimCtl(cs, u);
        u.Anim = A;
        A.Play("walk_1");
        t.Equal("walk_1#s", A.Cur!.Clip, "a loop clip starts with its start part");
        for (int i = 0; i < 12; i++) A.Update(0.05);
        t.Equal("walk_1#l", A.Cur!.Clip, "then its loop part");
        for (int i = 0; i < 40; i++) A.Update(0.05);
        t.Equal("walk_1#l", A.Cur!.Clip, "the loop part repeats");
        A.Play("standanim");
        t.Equal("walk_1#e", A.Cur!.Clip, "leaving it for another looping clip: the end part first");
        for (int i = 0; i < 12; i++) A.Update(0.05);
        t.Equal("standanim", A.CurName, "then the clip asked for");
        u.BusyAnim = true;
        A.Play("hit", AnimOpts.Once(new AnimDone("busy")));
        double tt = 0;
        while (u.BusyAnim && tt < 5) { A.Update(0.05); tt += 0.05; }
        t.Near(0.8, tt, 0.051, "onDone at the end of a clip played once");
        t.True(A.Cur!.Paused && A.Cur.Time == 0.8, "it stays on its last frame");
        int s0 = A.Serial;
        A.Play("standanim");
        t.True(A.Serial > s0, "the view sees the new start (Serial)");
    }

    // ------------------------------------------------------------------ model data
    public static void ModelFileInfoFromGlb(TestContext t)
    {
        string md = ModelsDir();
        if (!File.Exists(Path.Combine(md, "hu_fireplace.glb")) || !File.Exists(Path.Combine(md, "hu_worker_s1.glb"))) t.Skip("model files not staged (PW_MODELS)");
        var fp = ModelFileInfo.ReadGlb(Path.Combine(md, "hu_fireplace.glb"));
        t.Equal("Bldg", fp.Fourcc, "a building");
        t.True(fp.Links.ContainsKey("Cr_1") && fp.Links.Keys.Count(k => k.StartsWith("Bl_")) >= 2, $"crane and builder links ({string.Join(",", fp.Links.Keys.Take(12))})");
        t.True(fp.BoxMin != null && fp.BoxMax!.Value.Y - fp.BoxMin!.Value.Y > 3, "a box of a few metres");
        var wk = ModelFileInfo.ReadGlb(Path.Combine(md, "hu_worker_s1.glb"));
        t.Equal("Char", wk.Fourcc, "a character");
        t.True(wk.Clips.Count > 100 && wk.Clips.Values.All(d => d >= 0), $"its clips ({wk.Clips.Count})");
        t.True(wk.Links.ContainsKey("HndR"), "the right hand link");
        double h = wk.BoxMax!.Value.Y - wk.BoxMin!.Value.Y;
        t.True(h > 1.2 && h < 3, $"a man's height ({h:0.00})");
        // through the manifest: clips with the loop parts, a unit's size, a building's links in the world
        var (R, M) = Load(Manifested(t, RulesTestData.Base(t)));
        var clips = M.Clips("hu_worker_s1");
        t.True(clips.Has("walk_1"), "walk_1 clip");
        var (r, ht) = M.UnitSize("hu_worker_s1");
        t.True(r >= 0.45 && r < 1.5 && ht > 1.2, $"unit size {r:0.00} / {ht:0.00}");
        var W = new W_(R, M, new FlatMap(256));
        var p = new Player(0, "Hu", new PlayerOpts { Rules = R }); W.Players.Add(p);
        var b = W.PlaceBuilding("hu_fireplace", p, 10, 20, 0.5, false)!;
        t.True(b.Slots.Count >= 2, $"builder slots from the links ({b.Slots.Count})");
        t.True(b.Cranes.Count > 0 || !M.Has("hu_crane_01"), "cranes on a building site");
        foreach (var s in b.Slots) t.True(Math.Abs(s.X - 10) < 25 && Math.Abs(s.Z - 20) < 25, "slots near the building");
        var wkr = W.SpawnUnit("hu_worker", p, -20, 0)!;
        t.True(wkr.Anim!.CurName != null, $"the worker plays its idle clip ({wkr.Anim})");
        t.True(wkr.Speed > 0 && wkr.WalkAnim != null, $"walk speed {wkr.Speed} and clip {wkr.WalkAnim}");
    }

    // ------------------------------------------------------------------ players
    public static void PlayerResourcesAndDiplomacy(TestContext t)
    {
        var p = new Player(0, "Hu");
        var q = new Player(1, "Aje");
        t.Equal(0, p.Relation(q), "skirmish: other teams are enemies");
        p.SetRelation(q, 2);
        t.True(p.IsFriend(q) && q.IsEnemy(p), "one direction per pair");
        p.Res["wood"] = 290;
        t.Equal(10.5, p.Deliver("wood", 20.5, 1), "what does not fit comes back");
        t.Equal(300.0, p.Res["wood"], "up to the cap");
        p.Res["stone"] = 10;
        p.Deliver("stone", 2.5, 2);
        t.Equal(13.0, p.Res["stone"], "rounded to whole units (2.5 -> 13 total)");
        t.Equal("housing", p.SlotFree(1, new double[] { 25, 15, 8, 3, 1 }), "no housing");
        p.MaxUnits = 10;
        t.Equal<string?>(null, p.SlotFree(1, new double[] { 25, 15, 8, 3, 1 }), "a free slot");
    }
}
