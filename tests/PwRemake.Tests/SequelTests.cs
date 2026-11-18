// Tests of the remake's own campaigns (src/Sim/Sequel, game/campaigns; docs/SEQUEL.md): the sequel's classes in the
// rules, the maps made from the recipes, the power grid, the runtime's rifts / captures / civilians, and every mission
// of Containment and the scenarios set up and played a few minutes headless without errors in its Lua.
// Data: PW_CONTENT (default /home/claude/testcontent) configs/mirage (Containment) and configs/base (scenarios).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Sequel;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class SequelTests
{
    static GameConfiguration Config(TestContext t, string id)
    {
        var c = CampaignTests.LoadCfgPublic(id);
        if (c == null) t.Skip($"no test configuration {id}");
        return c!.Config;
    }
    static string DataOf(string id) => CampaignTests.LoadCfgPublic(id)!.Data;

    public static SequelSession Session(TestContext t, string kind, int id, int difficulty = 1, string tribe = "", string? cfgId = null, DebugOptions? debug = null, SimFlags? flags = null)
    {
        cfgId ??= kind == "sequel" ? "mirage" : "base";
        var cfg = Config(t, cfgId);
        var c = CampaignTests.LoadCfgPublic(cfgId)!;
        var start = new GameStart { Configuration = cfgId, Mode = kind == "sequel" ? GameMode.Sequel : GameMode.Scenario, Mission = id, Tribe = tribe, Difficulty = difficulty, Seed = 7, Debug = debug };
        return SequelSession.Create(start, cfg, kind == "sequel" ? null : c.Rules, c.Models, flags, dataDir: c.Data);
    }

    // ------------------------------------------------------------------ the catalog and the rules
    public static void CatalogListsTheMissions(TestContext t)
    {
        var seq = SequelCatalog.List("sequel");
        t.Equal(16, seq.Count, "Containment missions");
        t.Equal(5, SequelCatalog.List("scenario").Count, "scenarios");
        t.True(seq.All(e => File.Exists(Path.Combine(e.Folder, "mission.lua"))), "every mission has its mission.lua");
        t.True(SequelCatalog.List("scenario").All(e => File.Exists(Path.Combine(e.Folder, "mission.lua"))), "every scenario has its mission.lua");
        t.True(SequelCatalog.LockedBefore(1).Contains("sq_tranq"), "the tranquilizer comes later than mission 1");
        t.True(!SequelCatalog.LockedBefore(3).Contains("sq_tranq"), "mission 3 brings it in");
        var d = SequelCatalog.Definition(SequelCatalog.Find("scenario", 1)!, "Aje", 1);
        t.True(d["start"] != null && d["variants"] == null, "a scenario's definition for its tribe");
    }

    public static void ClassesAreWrittenIntoTheRules(TestContext t)
    {
        Config(t, "mirage");
        var R = SequelSession.SequelRules(DataOf("mirage"), 3, false);
        foreach (var c in new[] { "sq_trooper", "sq_tranq", "sq_capture_truck", "sq_stabilizer", "sq_rift", "sq_halloran", "sq_civilian_1", "sq_power_pole" })
            t.True(R.Exists(c), $"class {c}");
        t.Equal("seas_gunner", R.Kind("sq_trooper"), "the copy keeps the original's scripted rules (sameAs)");
        t.True(R.Stats("sq_jeep", 1, null)?.Gfx == "sq_jeep", "the jeep's own model");
        var me = new Player(0, "SEAS", new PlayerOpts { Rules = R });
        var build = R.Actions(me).Where(a => a.Kind == "Build").Select(a => a.Results.Count > 0 ? a.Results[0].Obj : "").ToHashSet();
        t.True(build.Contains("sq_tranq"), "mission 3 builds tranquilizer marksmen");
        t.True(!build.Contains("sq_tesla_coil"), "the Tesla coil is locked before its mission");
        var stab = R.Actions(me).FirstOrDefault(a => a.Kind == "Build" && a.Results.Count > 0 && a.Results[0].Obj == "sq_stabilizer");
        t.True(stab != null && stab.Cost.Stone == 200, "the stabilizer costs 200 stone");
        var shock = R.Actions(me).FirstOrDefault(a => a.Kind == "Build" && a.Results.Count > 0 && a.Results[0].Obj == "sq_shock_trooper");
        t.True(shock != null && shock.Cost.Power == 40, "the shock trooper costs power");
    }

    // ------------------------------------------------------------------ maps
    public static void MapsAreTheSameEveryTime(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("mirage");
        if (c == null) t.Skip("no mirage data");
        foreach (var e in SequelCatalog.List("sequel").Take(3))
        {
            var def = SequelCatalog.Definition(e, "SEAS", 1);
            var a = SequelMap.Source(SequelRuntime.MapRecipe(def), null, c!.Models.Names.ToList(), out var ra);
            var b = SequelMap.Source(SequelRuntime.MapRecipe(def), null, c.Models.Names.ToList(), out _);
            t.Equal(a.Decor.Count, b.Decor.Count, $"{e.Title}: decor");
            t.Equal(a.Trees.Count, b.Trees.Count, $"{e.Title}: trees");
            t.Near(a.Height(13, 17), b.Height(13, 17), 1e-12, $"{e.Title}: height");
            t.True(a.Decor.Count > 20 && a.Trees.Count > 10 && ra.Count > 0, $"{e.Title}: {a.Decor.Count} decor, {a.Trees.Count} trees, {ra.Count} roads");
            t.Log($"{e.Title}: {a.Decor.Count} decor, {a.Trees.Count} trees, {a.Stones.Count} stones, {a.Bushes.Count} bushes, {ra.Count} roads");
        }
    }

    // ------------------------------------------------------------------ missions
    static void Play(TestContext t, SequelSession s, double seconds)
    {
        var W = s.World;
        W.Enqueue(new SequelCommand { Player = s.Human!.Id, What = "begin" });
        s.Run(seconds);
        t.True(s.Lua.Errors.Count == 0, $"{s.Title}: Lua errors: {string.Join(" | ", s.Lua.Errors.Take(3))}");
    }

    public static void Mission01SetsUpAndRuns(TestContext t)
    {
        var s = Session(t, "sequel", 1);
        var R = s.Runtime;
        t.True(R.Objectives.Any(o => o.Id == "evac"), "the evacuation objective");
        t.True(s.World.Units.Count(u => u.Owner == R.PlayerOf("civilians")) >= 18, "townsfolk");
        t.True(s.World.Units.Any(u => u.Name == "sq_halloran"), "the colonel");
        t.True(s.World.Buildings.Any(b => b.Name == "seas_headquarters" && b.Owner == R.Me), "the headquarters");
        Play(t, s, 160);
        t.True(R.Rifts.Count == 1, "the rift opened after 70 s");
        t.True(s.World.Units.Any(u => u.Name == "sq_taslov"), "Taslov came at 150 s");
        t.True(R.Lines.Count >= 7, $"the radio: {R.Lines.Count} lines");
        t.Log($"after 160 s: {R.Lines.Count} radio lines, {s.World.Units.Count} units, cp {R.Cp}");
    }

    /// <summary>every mission of a campaign set up and played for PW_SEQUEL_SECONDS (default 200) game seconds with nobody at
    /// the controls: no errors in its Lua, the objectives there</summary>
    static void AllMissions(TestContext t, string kind)
    {
        double secs = double.TryParse(Environment.GetEnvironmentVariable("PW_SEQUEL_SECONDS"), out var v) ? v : 200;
        var only = Environment.GetEnvironmentVariable("PW_SEQUEL_ONLY");
        foreach (var e in SequelCatalog.List(kind))
        {
            if (!string.IsNullOrEmpty(only) && !only.Split(',').Contains(e.Id.ToString())) continue;
            var tribes = kind == "scenario" ? SequelCatalog.SCENARIO_TRIBES.Take(e.Id % 2 == 0 ? 4 : 2) : new[] { "SEAS" };
            foreach (var tribe in tribes)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var s = Session(t, kind, e.Id, 1, tribe, kind == "scenario" && tribe == "SEAS" ? "mirage" : null);
                t.True(s.Lua.Errors.Count == 0, $"{e.Title}: Lua errors at the start: {string.Join(" | ", s.Lua.Errors.Take(3))}");
                t.True(s.Runtime.Objectives.Count > 0, $"{e.Title}: objectives");
                Play(t, s, secs);
                var R = s.Runtime;
                t.Log($"{kind} {e.Id} {e.Title} ({tribe}): {secs} s in {sw.ElapsedMilliseconds} ms; units {s.World.Units.Count(u => u.Alive)}, rifts {R.Rifts.Count}, lines {R.Lines.Count}, objectives {string.Join(",", R.Objectives.Select(o => o.Id + (o.Done ? "+" : o.Failed ? "x" : "")))}" + (R.Ended is { } en ? $", ended {(en.Won ? "won" : "lost")}: {en.Text}" : ""));
            }
        }
    }
    public static void ContainmentMissionsRun(TestContext t) => AllMissions(t, "sequel");
    public static void ScenariosRun(TestContext t) => AllMissions(t, "scenario");

    /// <summary>a mission of Containment saved and loaded (docs/SAVES.md: the start and the inputs, replayed): the
    /// briefing's begin and the command powers are commands, the Lua state comes back by the replay - the same world</summary>
    public static void SaveLoadContinueSequel(TestContext t)
    {
        var start = new GameStart { Configuration = "mirage", Mode = GameMode.Sequel, Mission = 2, Difficulty = 1, Seed = 31 };
        SequelSession Make(GameStart st, SimFlags flags) => Session(t, "sequel", st.Mission, st.Difficulty, "", null, null, flags);
        var a = Make(start, SimFlags.Game);
        var rec = new GameRecorder(a.World, a.StateHash, null);
        int me = a.Human!.Id;
        a.World.Enqueue(new SequelCommand { Player = me, What = "begin" });
        a.Run(170);                                 // (two command points by then: one every 75 ticks of 0.5 s - 82.5 s, as the JS counts)
        t.Log($"cp {a.Runtime.Cp} earned {a.Runtime.CpEarned} at {a.Runtime.Time:0.0}");
        a.World.Enqueue(new SequelCommand { Player = me, What = "power", Id = "supply" });
        a.Run(40);
        t.True(a.Runtime.PowerReady.ContainsKey("supply"), $"a power was used (cp {a.Runtime.Cp}, time {a.Runtime.Time}, started {a.Runtime.Started}, results {string.Join(",", a.World.CommandResults.Select(r => r.Reason ?? "ok"))})");
        var save = rec.Capture(start, SimFlags.Game, null);
        var file = Path.Combine(Path.GetTempPath(), "sequel_test.pwsave");
        save.Write(file);
        var hashA = a.StateHash();
        var loaded = SaveGame.Read(file);
        var b = Make(loaded.Start, loaded.Flags);
        var rep = new SaveReplay(loaded, b.World, b.StateHash, null);
        rep.Run(); rep.End();
        t.True(!rep.Diverged, $"the replay follows the save ({rep.Problem}; {rep.Verified} hashes equal)");
        t.Equal(hashA, b.StateHash(), "the loaded mission is the saved one (state hash)");
        t.Equal(a.Runtime.Lines.Count, b.Runtime.Lines.Count, "the same radio lines");
        for (int i = 0; i < 600; i++) { a.Step(); b.Step(); }
        t.Equal(a.StateHash(), b.StateHash(), "the same world after playing on");
    }
}
