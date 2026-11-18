// Saved games (docs/SAVES.md): a save is the game's start plus its inputs (commands, the interface's answers) and
// verification hashes; loading replays it. The test that matters: play N steps, save, load (replay into a fresh
// session), then step the running game and the loaded one M more steps with the same inputs - the state hashes must be
// equal all the way. Plus: the command codec, the file (header, version refusal), the interface tap, and a replay
// that goes another way than its save (a tampered hash) stopping cleanly.
//
//   dotnet run --project tests/PwRemake.Tests -- Saves           (PW_SAVES_MINUTES=3: the minutes played before saving)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class SavesTests
{
    static double Minutes => double.TryParse(Environment.GetEnvironmentVariable("PW_SAVES_MINUTES"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 3;
    static string Tmp(string name) { var d = Path.Combine(Path.GetTempPath(), "pw_saves_test"); Directory.CreateDirectory(d); return Path.Combine(d, name); }

    /// <summary>A mission UI as the game's behaves towards the mission, scripted: it plays every cutscene and dialogue
    /// scene and ends it some steps later through the command (SceneEndCommand, as the game's UI does), is busy while a
    /// cutscene waits, moves its camera and selects now and then (the tutorial's UNTT conditions) and has a profile.
    /// Everything it answers is a function of the step and of the scenes it was given.</summary>
    sealed class ScriptedUi : NullPresenter
    {
        public World? W;
        readonly List<(SequencePlay P, long At)> _seqs = new();
        readonly List<(int Token, long At)> _dlgs = new();
        public int Asked;
        public bool Idle => _seqs.Count == 0 && _dlgs.Count == 0;
        public ScriptedUi() { Profile = new Dictionary<string, string> { ["Tutorial_Started"] = "1", ["Games_Started"] = "3" }; }
        long Now => W?.StepNo ?? 0;
        public override bool IsBusy { get { Asked++; return _seqs.Count > 0; } }
        public override bool PlaySequence(SequencePlay play) { Asked++; _seqs.Add((play, Now)); return true; }
        public override bool PlayDialog(DialogPlay d) { Asked++; _dlgs.Add((d.Token, Now)); return true; }
        public override (double X, double Y, double Dist, double Yaw)? Camera { get { Asked++; long k = Now / 150; return (200 + k * 3, 300 - k * 2, 40 + k % 3, 0.785 + (k % 4) * 0.1); } }
        public override bool IsSelected(Entity e) { Asked++; return e.Id % 3 == 0 && Now / 100 % 2 == 1; }
        /// <summary>after each step (the game's MissionPresenter.Tick): scenes whose time is up end by command</summary>
        public void Pump(World w)
        {
            if (_seqs.Count > 0 && w.StepNo - _seqs[0].At >= 60)
            {
                w.Enqueue(new SceneEndCommand { Player = 0, Kind = "sequence", Timeline = _seqs[0].P.Actors != null });
                _seqs.RemoveAt(0);
            }
            for (int i = _dlgs.Count - 1; i >= 0; i--)
                if (w.StepNo - _dlgs[i].At >= 40) { w.Enqueue(new SceneEndCommand { Player = 0, Kind = "dialog", Token = _dlgs[i].Token }); _dlgs.RemoveAt(i); }
        }
    }

    /// <summary>the player's orders, as commands, decided from the world alone (the same in both games): every 10 s the
    /// idle army attack-moves to the nearest enemy, every 30 s a group walk, once a debug tool</summary>
    static void Drive(World W, Player me)
    {
        long n = W.StepNo;
        if (n % 200 == 0)
        {
            var idle = W.Units.Where(u => u.Alive && u.Owner == me && !u.Inside && !u.Parked && !u.IsWorker && (u.Task.Type == "idle" || u.Task.Type == "hold")).ToList();
            if (idle.Count > 0)
            {
                double cx = idle.Average(u => u.Pos.X), cz = idle.Average(u => u.Pos.Z);
                var best = W.Units.Cast<Entity>().Concat(W.Buildings).Where(e => e.Alive && !e.Parked && e.Owner != null && me.IsEnemy(e.Owner))
                    .OrderBy(e => (e.Pos.X - cx) * (e.Pos.X - cx) + (e.Pos.Z - cz) * (e.Pos.Z - cz)).ThenBy(e => e.Id).FirstOrDefault();
                if (best != null) W.Enqueue(new OrderCommand { Player = me.Id, Units = idle.Select(u => u.Id).ToArray(), Type = "attackmove", X = best.Pos.X, Z = best.Pos.Z });
            }
        }
        if (n % 600 == 300)
        {
            var all = W.Units.Where(u => u.Alive && u.Owner == me && !u.Inside && !u.Parked).Take(6).ToList();
            if (all.Count > 1) W.Enqueue(new GroupMoveCommand { Player = me.Id, Units = all.Select(u => u.Id).ToArray(), X = all[0].Pos.X + 12, Z = all[0].Pos.Z - 8 });
        }
        if (n == 450) W.Enqueue(new DebugToolCommand { Player = me.Id, Tool = "res" });
    }

    sealed class Run
    {
        public MissionSession S = null!;
        public ScriptedUi Ui = null!;
        public PresenterTap Tap = null!;
        public GameRecorder Rec = null!;
        public void Step() { S.Step(); Ui.Pump(S.World); if (S.Human != null) Drive(S.World, S.Human); }
    }

    static Run Make(TestContext t, string cfgId, GameStart start, SimFlags flags, SaveGame? replayOf = null, Dictionary<string, string>? profile = null)
    {
        var c = CampaignTests.LoadCfgPublic(cfgId);
        if (c == null) t.Skip($"configuration {cfgId} or its data not found");
        if (MissionSession.MissionFolderOf(start, c!.Config) == null) t.Skip($"mission {start.Mission} not in {cfgId}");
        var e = MissionEntry.LoadIndex(c.Config.MissionsDir).First(x => x.Id == start.Mission);
        if (WorldBuilder.FindMap(c.Config, e.Map) == null) t.Skip($"map {e.Map} not staged");
        var ui = new ScriptedUi();
        if (profile != null) ui.Profile = profile;
        var tap = new PresenterTap(ui);
        if (replayOf != null) SaveReplay.Prepare(replayOf, tap);
        var s = MissionSession.Create(start, c.Config, new SessionOptions
        {
            Presenter = tap, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, Flags = flags,
        });
        ui.W = s.World;
        var r = new Run { S = s, Ui = ui, Tap = tap };
        r.Rec = new GameRecorder(s.World, s.StateHash, tap);
        return r;
    }

    /// <summary>play, save, load, play on: the same world all the way</summary>
    static void SaveLoadContinue(TestContext t, string cfgId, int mission, string tribe = "", int difficulty = 1)
    {
        var start = new GameStart { Configuration = cfgId, Mission = mission, Tribe = tribe, Difficulty = difficulty, Seed = 4242 };
        var a = Make(t, cfgId, start, SimFlags.Game);
        int n = (int)(Minutes * 60 / World.STEP);
        for (int i = 0; i < n && !a.S.Over; i++) a.Step();
        // (saved when no scene is on show: the game's own UI would start a running scene again after loading - the
        // inputs after the load are the player's, here the same in both games only if both UIs are in the same state)
        for (int i = 0; i < 4000 && !a.S.Over && !(a.Ui.Idle && a.S.Engine?.SeqRunning == null && !(a.S.Engine?.DialogsRunning.Any() ?? false)); i++) a.Step();
        t.True(!a.S.Over, $"{cfgId} {mission}: the mission still runs at {a.S.World.Time:0} s");
        var save = a.Rec.Capture(start, SimFlags.Game, view: new JsonObject { ["camera"] = new JsonArray(1.5, 2.5) });
        save.Header.Name = "test"; save.Header.Title = a.S.Data.Entry.Title; save.Thumbnail = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };
        var file = Tmp($"{cfgId}_{mission}.pwsave");
        save.Write(file);
        var hashA = a.S.StateHash();
        t.Log($"{cfgId} mission {mission}{(tribe != "" ? " as " + tribe : "")}: saved at {SaveHeader.Clock(a.S.World.Time)} (step {a.S.World.StepNo}): {save.Commands.Count} commands, {save.Answers.Count} answers, {save.Hashes.Count} hashes, {new FileInfo(file).Length / 1024.0:0.0} KB; UI asked {a.Ui.Asked} times");
        t.True(save.Commands.Count > 0, "commands recorded");
        // ---- load: a fresh session of the save's start, replayed
        var loaded = SaveGame.Read(file);
        t.Equal(save.Steps, loaded.Steps, "steps read back");
        t.Equal(save.Commands.Count, loaded.Commands.Count, "commands read back");
        t.Equal("test", loaded.Header.Name, "header read back");
        t.True(loaded.Thumbnail != null && loaded.Thumbnail.Length == 4, "thumbnail read back");
        t.Equal(1.5, loaded.View?["camera"]?[0].Num() ?? 0, "view read back");
        // (the loaded game's UI has another profile today: the mission must see the saved game's)
        var b = Make(t, cfgId, loaded.Start, loaded.Flags, loaded, new Dictionary<string, string> { ["Tutorial_Started"] = "0", ["Games_Started"] = "9" });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rep = new SaveReplay(loaded, b.S.World, b.S.StateHash, b.Tap);
        rep.Run();
        rep.End();
        t.Log($"    replayed {loaded.Steps} steps in {sw.ElapsedMilliseconds} ms ({loaded.Steps * World.STEP / Math.Max(0.001, sw.Elapsed.TotalSeconds):0}x real time), {rep.Verified} hashes equal; the loaded game's UI asked {b.Ui.Asked} times");
        t.True(!rep.Diverged, $"the replay follows the save ({rep.Problem})");
        t.Equal(0, b.Ui.Asked, "the interface is not asked while replaying (the record answers)");
        t.Equal(a.S.World.StepNo, b.S.World.StepNo, "same step");
        t.Equal(hashA, b.S.StateHash(), "the loaded world is the saved one (state hash)");
        t.Equal(a.S.World.PendingCommands, b.S.World.PendingCommands, "the commands waiting for the next step too");
        t.True(a.S.Engine == null || a.S.Engine.FiringLog.Count == b.S.Engine!.FiringLog.Count, "same trigger firings");
        // ---- both go on with the same inputs
        int m = (int)(1.5 * 60 / World.STEP);
        for (int i = 0; i < m; i++)
        {
            a.Step(); b.Step();
            if (i % 300 == 299) t.Equal(a.S.StateHash(), b.S.StateHash(), $"same world {i + 1} steps after loading");
        }
        t.Equal(a.S.StateHash(), b.S.StateHash(), $"same world {m} steps after loading");
        // ---- the loaded game's own save holds the whole history: it replays too
        var save2 = b.Rec.Capture(loaded.Start, loaded.Flags);
        t.Equal(a.Rec.Commands.Count, b.Rec.Commands.Count, "the loaded game recorded the same commands");
        t.Equal(a.Tap.Record.Count, b.Tap.Record.Count, "the loaded game recorded the same answers");
        var c = Make(t, cfgId, save2.Start, save2.Flags, save2);
        var rep2 = new SaveReplay(save2, c.S.World, c.S.StateHash, c.Tap);
        rep2.Run(); rep2.End();
        t.True(!rep2.Diverged, $"a save of the loaded game replays ({rep2.Problem})");
        t.Equal(b.S.StateHash(), c.S.StateHash(), "a save of the loaded game gives the loaded game");
    }

    /// <summary>a skirmish (docs/SKIRMISH.md): play with an army budget and debug options, save (the setup goes into the
    /// save), load (the skirmish rebuilt from the save's setup, replayed), play on - the same hashes all the way</summary>
    public static void SaveLoadContinueSkirmish(TestContext t)
    {
        const string MAP = "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer/multi_2_jun_001.ula";
        var c = CampaignTests.LoadCfgPublic("mirage");
        if (c == null) t.Skip("no configuration mirage");
        if (!File.Exists(MAP)) t.Skip("map not staged");
        var setup = Skirmish.DefaultSetup("SEAS", "Aje");
        setup.Map = "Base/Maps/Multiplayer/multi_2_jun_001.ula"; setup.Points = 1000; setup.Seed = 77;
        setup.Slots[1].Personality = "Offensive"; setup.Slots[1].Level = 6;
        var start = new GameStart { Configuration = "mirage", Mode = GameMode.Skirmish, Skirmish = setup, Seed = setup.Seed, Debug = new DebugOptions { On = true, Gather = 2 } };
        var cs = c!.Config.GameFile("Scripts/Ai/Modules/AiModuleControlDefault.usl");
        string? script = cs != null ? File.ReadAllText(cs) : null;
        (SkirmishSession S, GameRecorder Rec) Make(GameStart st, SimFlags flags)
        {
            var su = st.Skirmish!.Clone(); su.Debug ??= st.Debug;                // (as GameSession.CreateSkirmish)
            var s = SkirmishSession.Create(su, MAP, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, script, flags);
            return (s, new GameRecorder(s.World, s.StateHash, null));
        }
        void Step(SkirmishSession s) { s.Step(); if (s.Human != null) Drive(s.World, s.Human); }
        var a = Make(start, SimFlags.Game);
        int n = (int)(Minutes * 60 / World.STEP);
        for (int i = 0; i < n && !a.S.Over; i++) Step(a.S);
        var save = a.Rec.Capture(start, SimFlags.Game, System.Text.Json.JsonSerializer.SerializeToNode(start.Skirmish));
        save.Header.Name = "skirmish test";
        var file = Tmp("skirmish.pwsave");
        save.Write(file);
        var hashA = a.S.StateHash();
        t.Log($"skirmish saved at {SaveHeader.Clock(a.S.World.Time)}: {save.Commands.Count} commands, {save.Hashes.Count} hashes, {new FileInfo(file).Length / 1024.0:0.0} KB");
        t.True(save.Commands.Count > 0, "commands recorded");
        var loaded = SaveGame.Read(file);
        t.Equal("Skirmish", loaded.Header.Mode, "the header says skirmish");
        t.True(loaded.Setup != null, "the save carries the setup");
        // the load rebuilds the skirmish from the save's setup (as GameSession.LoadGame: GameStart.Skirmish, else "setup")
        var st2 = loaded.Start; var fromSetup = SkirmishSession.SetupOf(new GameStart(), loaded.Setup);
        t.Equal(start.Skirmish!.ToJson(), fromSetup!.ToJson(), "the setup read back from the save's \"setup\"");
        t.Equal(start.Skirmish!.ToJson(), st2.Skirmish!.ToJson(), "the setup read back from the save's start");
        var b = Make(st2, loaded.Flags);
        var rep = new SaveReplay(loaded, b.S.World, b.S.StateHash, null);
        rep.Run(); rep.End();
        t.True(!rep.Diverged, $"the replay follows the save ({rep.Problem}; {rep.Verified} hashes equal)");
        t.Equal(a.S.World.StepNo, b.S.World.StepNo, "same step");
        t.Equal(hashA, b.S.StateHash(), "the loaded skirmish is the saved one (state hash)");
        int m = (int)(1.5 * 60 / World.STEP);
        for (int i = 0; i < m; i++)
        {
            Step(a.S); Step(b.S);
            if (i % 300 == 299) t.Equal(a.S.StateHash(), b.S.StateHash(), $"same world {i + 1} steps after loading");
        }
        t.Equal(a.S.StateHash(), b.S.StateHash(), "same world after playing on");
    }

    /// <summary>a save of a big late game for the frame measurements (docs/DEBUG.md "Performance"): PW_PERF_SAVE=&lt;file&gt;
    /// [PW_PERF_CFG=base] [PW_PERF_MISSION=13] [PW_PERF_SECONDS=860] [PW_PERF_DIFFICULTY=2] -- PerfSave; the player's
    /// units attack-move as in the other saves (Drive)</summary>
    public static void PerfSave(TestContext t)
    {
        var file = Environment.GetEnvironmentVariable("PW_PERF_SAVE");
        if (string.IsNullOrEmpty(file)) t.Skip("set PW_PERF_SAVE=<file>");
        string E(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
        string cfgId = E("PW_PERF_CFG", "base");
        var start = new GameStart { Configuration = cfgId, Mission = int.Parse(E("PW_PERF_MISSION", "13")), Difficulty = int.Parse(E("PW_PERF_DIFFICULTY", "2")), Seed = 4242 };
        var a = Make(t, cfgId, start, SimFlags.Game);
        int n = (int)(double.Parse(E("PW_PERF_SECONDS", "860"), System.Globalization.CultureInfo.InvariantCulture) / World.STEP);
        for (int i = 0; i < n && !a.S.Over; i++)
        {
            a.Step();
            if (i % 2000 == 0) t.Log($"{a.S.World.Time:0} s: {a.S.World.Units.Count(u => u.Alive)} units, {a.S.World.Buildings.Count(b => b.Alive)} buildings");
        }
        t.True(!a.S.Over, $"the mission still runs at {a.S.World.Time:0} s");
        var save = a.Rec.Capture(start, SimFlags.Game, view: new JsonObject { ["camera"] = new JsonArray(1.5, 2.5) });
        save.Header.Name = "perf"; save.Header.Title = a.S.Data.Entry.Title; save.Thumbnail = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };
        save.Write(file!);
        t.Log($"saved {file} at {a.S.World.Time:0} s (step {a.S.World.StepNo}): {a.S.World.Units.Count(u => u.Alive)} units, {a.S.World.Buildings.Count(b => b.Alive)} buildings");
    }

    /// <summary>the same with a skirmish of six (you and five computer players in three teams, PW_PERF_LEVEL default
    /// 8) on multi_6_sav_601: PW_PERF_SAVE=&lt;file&gt; [PW_PERF_SECONDS=860] -- PerfSaveSkirmish; PW_PERF_PLAYERS=16: you and
    /// 15 computer players (each its own team) on the huge random jungle</summary>
    public static void PerfSaveSkirmish(TestContext t)
    {
        var file = Environment.GetEnvironmentVariable("PW_PERF_SAVE");
        if (string.IsNullOrEmpty(file)) t.Skip("set PW_PERF_SAVE=<file>");
        string E(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
        const string MAP = "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer/multi_6_sav_601.ula";
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no configuration base");
        if (!File.Exists(MAP)) t.Skip("map not staged");
        var setup = Skirmish.DefaultSetup("Hu", "Aje");
        setup.Map = "Base/Maps/Multiplayer/multi_6_sav_601.ula"; setup.Seed = 77;
        string[] tribes = { "Hu", "Aje", "Ninigi", "Hu", "Aje", "Ninigi" };
        // PW_PERF_PLAYERS=16: you and 15 computer players, each its own team, on the huge random jungle (16 players)
        int np = int.Parse(E("PW_PERF_PLAYERS", "6"));
        bool big = np > 6;
        if (big) { setup.Map = ""; setup.Teams = np; }
        for (int i = 0; i < np; i++)
        {
            var sl = setup.Slots[i];
            sl.Type = i == 0 ? "human" : "ai"; sl.Tribe = tribes[i % 6]; sl.Team = big ? i + 1 : i / 2 + 1; sl.Level = int.Parse(E("PW_PERF_LEVEL", "8")); sl.Hq = i;
            sl.Color = Skirmish.FreeColor(setup.Slots, sl);
        }
        var start = new GameStart { Configuration = "base", Mode = GameMode.Skirmish, Skirmish = setup, Seed = setup.Seed };
        var cs = c!.Config.GameFile("Scripts/Ai/Modules/AiModuleControlDefault.usl");
        string? script = cs != null ? File.ReadAllText(cs) : null;
        var su = setup.Clone();
        var S = SkirmishSession.Create(su, big ? null : MAP, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, script, SimFlags.Game);
        var rec = new GameRecorder(S.World, S.StateHash, null);
        var swAll = System.Diagnostics.Stopwatch.StartNew(); long last = 0;
        int n = (int)(double.Parse(E("PW_PERF_SECONDS", "860"), System.Globalization.CultureInfo.InvariantCulture) / World.STEP);
        for (int i = 0; i < n && (big || !S.Over); i++)          // (16 players: on even when you are beaten)
        {
            S.Step(); if (S.Human != null) Drive(S.World, S.Human);
            if (i % 2000 == 0) { t.Log($"{S.World.Time:0} s: {S.World.Units.Count(u => u.Alive)} units, {S.World.Buildings.Count(b => b.Alive)} buildings, {(swAll.ElapsedMilliseconds - last) / 2000.0:0.00} ms/step, heap {GC.GetTotalMemory(false) / 1048576} MB"); last = swAll.ElapsedMilliseconds; }
        }
        var save = rec.Capture(start, SimFlags.Game, System.Text.Json.JsonSerializer.SerializeToNode(start.Skirmish));
        save.Header.Name = "perf skirmish";
        save.Write(file!);
        t.Log($"saved {file} at {S.World.Time:0} s (over: {S.Over}): {S.World.Units.Count(u => u.Alive)} units, {S.World.Buildings.Count(b => b.Alive)} buildings");
    }

    /// <summary>the simulation of a skirmish save (PerfSaveSkirmish) measured: replayed, then PW_PERF_STEPS (600) steps with
    /// the step profile; the state hash after them (the same before and after a change that must not change the game):
    /// PW_PERF_SAVE=&lt;file&gt; -- PerfReplaySkirmish</summary>
    public static void PerfReplaySkirmish(TestContext t)
    {
        var file = Environment.GetEnvironmentVariable("PW_PERF_SAVE");
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) t.Skip("set PW_PERF_SAVE=<file>");
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no configuration base");
        var loaded = SaveGame.Read(file!);
        var su = loaded.Start.Skirmish!.Clone(); su.Debug ??= loaded.Start.Debug;
        var cs = c!.Config.GameFile("Scripts/Ai/Modules/AiModuleControlDefault.usl");
        var S = SkirmishSession.Create(su, su.Map == "" ? null : "/mnt/user-data/uploads/Paraworld/Data/" + su.Map, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, cs != null ? File.ReadAllText(cs) : null, loaded.Flags);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rep = new SaveReplay(loaded, S.World, S.StateHash, null);
        rep.Run(); rep.End();
        t.True(!rep.Diverged, $"the replay follows the save ({rep.Problem})");
        t.Log($"replayed {S.World.StepNo} steps in {sw.ElapsedMilliseconds} ms; state hash {S.StateHash():x}");
        S.World.Profile = new StepProfile();
        int n = int.Parse(Environment.GetEnvironmentVariable("PW_PERF_STEPS") ?? "600");
        // PW_PERF_ALLOC=1: what is allocated, by type (AllocSampler)
        using var alloc = Environment.GetEnvironmentVariable("PW_PERF_ALLOC") == "1" ? new AllocSampler() : null;
        var pause0 = GC.GetTotalPauseDuration(); int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long a0 = GC.GetTotalAllocatedBytes(true);
        if (alloc != null) alloc.On = true;
        sw.Restart();
        for (int i = 0; i < n; i++) S.Step();          // (on after the end too: a 16-player game goes on without you)
        if (alloc != null) alloc.On = false;
        t.Log($"{n} steps in {sw.ElapsedMilliseconds} ms; {S.World.Units.Count(u => u.Alive)} units, {S.World.Buildings.Count(b => b.Alive)} buildings; state hash {S.StateHash():x}");
        t.Log($"GC: {GC.CollectionCount(0) - g0} / {GC.CollectionCount(1) - g1} / {GC.CollectionCount(2) - g2} collections (gen 0/1/2), pauses {(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:0} ms, allocated {(GC.GetTotalAllocatedBytes(true) - a0) / 1024.0 / n:0.0} KB per step, heap {GC.GetTotalMemory(false) / 1048576} MB");
        var gi = GC.GetGCMemoryInfo(GCKind.Any);
        t.Log($"last collection: gen {gi.Generation}, pause {gi.PauseDurations[0].TotalMilliseconds:0.0} ms, promoted {gi.PromotedBytes / 1048576.0:0.0} MB, heap {gi.HeapSizeBytes / 1048576} MB, fragmented {gi.FragmentedBytes / 1048576} MB, gen0 budget ~{(gi.GenerationInfo[0].SizeBeforeBytes) / 1048576} MB");
        t.Log(S.World.Profile.Report(int.Parse(Environment.GetEnvironmentVariable("PW_PERF_TOP") ?? "40")));
        if (alloc != null) t.Log(alloc.Report());
    }

    public static void SaveLoadContinueBaseTutorial(TestContext t) => SaveLoadContinue(t, "base", 0);
    public static void SaveLoadContinueBaseMission1(TestContext t) => SaveLoadContinue(t, "base", 1, difficulty: 2);
    public static void SaveLoadContinueMirageMission3AsSeas(TestContext t) => SaveLoadContinue(t, "mirage", 3, "SEAS");

    /// <summary>a replay that goes another way than its save (here: a tampered hash) stops there, cleanly</summary>
    public static void DivergenceStopsTheReplay(TestContext t)
    {
        var start = new GameStart { Configuration = "base", Mission = 1, Seed = 9 };
        var a = Make(t, "base", start, SimFlags.Game);
        for (int i = 0; i < 2 * GameRecorder.HashEvery + 50; i++) a.Step();
        var save = a.Rec.Capture(start, SimFlags.Game);
        t.True(save.Hashes.Count >= 2, "two hashes");
        save.Hashes[1] = (save.Hashes[1].Step, save.Hashes[1].Hash ^ 1);
        var b = Make(t, "base", save.Start, save.Flags, save);
        var rep = new SaveReplay(save, b.S.World, b.S.StateHash, b.Tap);
        rep.Run(); rep.End();
        t.True(rep.Diverged, "the difference is found");
        t.Equal(save.Hashes[1].Step, rep.DivergedAt, "at the hash that differs");
        t.Equal(1, rep.Verified, "the hash before was equal");
        t.Equal(0, b.S.World.PendingCommands, "the rest of the save's commands is dropped");
        t.True(!b.Tap.Replaying, "the interface answers again");
        b.Step();          // (the game goes on)
    }

    // ------------------------------------------------------------------ the file
    public static void FileHeaderAndVersion(TestContext t)
    {
        var s = new SaveGame
        {
            Start = new GameStart { Configuration = "mirage", Mission = 7, Tribe = "SEAS", Difficulty = 3, Seed = 5, Debug = new DebugOptions { On = true, Free = true } },
            Flags = SimFlags.Game, Steps = 1234,
            Header = { Name = "Before the temple", Title = "Mission 7", Mission = 7, Tribe = "SEAS", GameTime = 61.7, Kind = "quick" },
        };
        s.Commands.Add(CommandCodec.Encode(new OrderCommand { Player = 0, Step = 17, Units = new[] { 4, 5 }, Type = "attack", Target = 9 }));
        s.Hashes.Add((600, 0xfedcba9876543210UL));
        s.Answers.Add(new TapAnswer("cam", 3, 40, new JsonArray(1.0, 2.0, 3.0, 4.0)));
        var f = Tmp("header.pwsave");
        s.Write(f);
        var (h, thumb) = SaveGame.ReadHeader(f);
        t.Equal("Before the temple", h.Name, "name");
        t.Equal("quick", h.Kind, "kind");
        t.Equal(1234L, h.Steps, "steps in the header");
        t.Equal(1, h.Commands, "commands in the header");
        t.Equal(SaveGame.FormatVersion, h.Version, "version");
        t.True(thumb == null, "no thumbnail");
        var r = SaveGame.Read(f);
        t.Equal("SEAS", r.Start.Tribe, "start: tribe");
        t.Equal(3, r.Start.Difficulty, "start: difficulty");
        t.True(r.Start.Debug != null && r.Start.Debug.Free, "start: debug options");
        t.Equal(0xfedcba9876543210UL, r.Hashes[0].Hash, "hash");
        t.Equal(4.0, r.Answers[0].Value![3].Num(), "answer");
        t.True(r.Flags.Fog && r.Flags.TrimEvents, "flags");
        t.Equal(17L, CommandCodec.Decode(r.Commands[0]).Step, "command step");
        // another format version: refused with a message, never half loaded
        var g = Tmp("old.pwsave");
        using (var fs = File.Create(g))
        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("header.json");
            using var w = new StreamWriter(e.Open());
            w.Write("{\"format\": \"pwremake-save\", \"version\": " + (SaveGame.FormatVersion + 1) + ", \"name\": \"future\"}");
        }
        bool refused = false;
        try { SaveGame.Read(g); } catch (SaveFormatException ex) { refused = ex.Message.Contains("newer"); t.Log("    refused: " + ex.Message); }
        t.True(refused, "a save of another format version is refused");
        var junk = Tmp("junk.pwsave");
        File.WriteAllText(junk, "not a zip");
        refused = false;
        try { SaveGame.ReadHeader(junk); } catch (SaveFormatException) { refused = true; }
        t.True(refused, "a file that is no save is refused");
        var list = SaveGame.List(Path.GetDirectoryName(f)!);
        t.True(list.Any(x => x.File == f && x.Header != null) && list.Any(x => x.File == junk && x.Problem != null), "the list shows saves and problems");
        t.Equal("Mission 3_ the camp.pwsave", SaveGame.FileName("Mission 3: the camp"), "file name");
    }

    // ------------------------------------------------------------------ Continue Campaign (the title screen)
    public static void NewestCampaignSave(TestContext t)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pwsaves_continue_" + Environment.ProcessId);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        t.True(SaveGame.NewestCampaign(dir) == null, "no saves: nothing to continue");
        void Save(string file, string mode, string date)
        {
            using var fs = File.Create(Path.Combine(dir, file));
            using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
            using var w = new StreamWriter(zip.CreateEntry("header.json").Open());
            w.Write($"{{\"format\": \"{SaveGame.FormatName}\", \"version\": {SaveGame.FormatVersion}, \"name\": \"{file}\", \"mode\": \"{mode}\", \"date\": \"{date}\"}}");
        }
        Save("old mission.pwsave", "Campaign", "2026-10-01T10:00:00");
        Save("containment.pwsave", "Sequel", "2026-10-03T10:00:00");
        Save("skirmish.pwsave", "Skirmish", "2026-10-05T10:00:00");
        File.WriteAllText(Path.Combine(dir, "broken.pwsave"), "not a zip");
        var n = SaveGame.NewestCampaign(dir);
        t.True(n != null && n.Value.Header.Name == "containment.pwsave", "the newest single player save, not the newer skirmish");
        Save("autosave.pwsave", "Campaign", "2026-10-06T10:00:00");
        t.Equal("autosave.pwsave", SaveGame.NewestCampaign(dir)!.Value.Header.Name, "an autosave counts (the original: any save file)");
        Directory.Delete(dir, true);
    }

    // ------------------------------------------------------------------ commands as JSON
    public static void CommandCodecRoundTrip(TestContext t)
    {
        var all = new Command[]
        {
            new OrderCommand { Units = new[] { 1, 2 }, Type = "patrol", X = 1.25, Z = -3.5, Target = 7, Queue = true, Force = true, Slow = true, Pts = new() { (1.0, 2.0), (3.5, double.NaN) }, I = 1 },
            new GroupMoveCommand { Units = new[] { 3 }, Type = "attackmove", X = 0.1, Z = 0.2, Dir = 1.5707963267948966, Spacing = null, Queue = true, Patrol = false },
            new QueueActionCommand { Producer = 5, Action = "/Actions/Hu/Build/CHTR/hu_warrior", Count = 5 },
            new UnqueueCommand { Producer = 5, ActionId = "hu_warrior" },
            new PlaceBuildingCommand { Action = "/Actions/Hu/Build/BLDG/hu_farm", X = 10, Z = 20, Rot = 0.5, Workers = new[] { 8, 9 } },
            new PlaceWallCommand { Action = "a", Tiles = new() { (1, 2, 3.0, 4.0, true), (5, 6, 7.0, 8.0, false) }, Workers = new[] { 1 }, Queued = true },
            new ChangeLevelCommand { Unit = 3, Level = 2 }, new LevelUpCommand { Unit = 3 },
            new UseMoveCommand { Casters = new[] { 1 }, ActionId = "m", Target = 2, HasGround = true, X = 1, Z = 2, All = true },
            new KillCommand { Entities = new[] { 4 } }, new SetRallyCommand { Producers = new[] { 1 }, X = 3, Z = 4, Target = 0 },
            new AimTowersCommand { Towers = new[] { 1 }, Target = 2 }, new MoveHarbourCommand { Harbours = new[] { 1 }, X = 5, Z = 6 },
            new LayWaterThingCommand { Ship = 1, Action = "mine", X = 1, Z = 2, Queue = true }, new DeliverCommand { Workers = new[] { 1 }, Target = 2 },
            new DiplomacyCommand { Other = 2, Relation = 1 }, new TributeCommand { To = 2, Res = "wood", Amount = 100 },
            new SceneEndCommand { Kind = "dialog", Token = 4, Timeline = true },
            new PwRemake.Sim.Coop.CoopCommand { Seat = 2, Seats = new[] { 2, 3 }, Kind = "attack", HasPoint = true, X = 1.5, Z = -2, Target = 5, Units = new[] { 1 }, Value = 1 },
            new DebugToolCommand { Tool = "spawn", Class = "hu_warrior", Side = "foe", Level = 3, X = 1, Z = 2, Entities = new[] { 1 }, Text = "q", On = true, Said = "not saved" },
        };
        int i = 0;
        foreach (var c in all)
        {
            c.Player = i % 3; c.Step = 100 + i++;
            var j = CommandCodec.Encode(c).ToJsonString();
            var d = CommandCodec.Decode(JsonNode.Parse(j)!);
            t.Equal(c.GetType(), d.GetType(), $"{c.GetType().Name}: class");
            t.Equal(j, CommandCodec.Encode(d).ToJsonString(), $"{c.GetType().Name}: the same JSON again");
            t.Equal(c.Step, d.Step, $"{c.GetType().Name}: step");
        }
        t.True(!CommandCodec.Encode(all[^1]).ToJsonString().Contains("not saved"), "a [JsonIgnore] field is not written");
        bool threw = false;
        try { CommandCodec.Decode(JsonNode.Parse("{\"t\": \"NoSuchCommand\", \"c\": {}}")!); } catch (FormatException) { threw = true; }
        t.True(threw, "an unknown command class is an error");
    }

    // ------------------------------------------------------------------ the interface's answers
    public static void TapRecordsAndReplays(TestContext t)
    {
        var ui = new ScriptedUi();
        var tap = new PresenterTap(ui);
        var answers = new List<string>();
        // a fake world clock: the tap marks answers with World.StepNo - here without a world (step 0)
        for (int k = 0; k < 5; k++) answers.Add($"{tap.IsBusy} {tap.Camera} {tap.LoadProfile()?["Games_Started"]}");
        t.True(tap.Record.Count >= 2, $"answers that differ from the default are recorded ({tap.Record.Count})");
        var tap2 = new PresenterTap(new NullPresenter());
        tap2.StartReplay(tap.Record);
        var again = new List<string>();
        for (int k = 0; k < 5; k++) again.Add($"{tap2.IsBusy} {tap2.Camera} {tap2.LoadProfile()?["Games_Started"]}");
        t.Equal(string.Join("|", answers), string.Join("|", again), "the replay answers as the record");
        t.Equal(tap.Record.Count, tap2.Record.Count, "the replay records the same answers again");
        t.True(tap2.Mismatch == null, "no mismatch");
        tap2.EndReplay();
        t.True(tap2.Camera == null, "after the replay the interface (here none) answers");
    }
}
