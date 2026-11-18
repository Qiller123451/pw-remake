// Whole-game soak runs (docs/TESTING.md "Soak"): every campaign mission of a configuration headless with MissionSession
// (its triggers, the computer players), for N game minutes, the human player either passive or a small scripted bot
// that sends its starting army to attack-move towards the nearest enemy; collects every exception, every error log line
// (and the distinct warnings), NaN positions, the triggers fired, the quests, whether the mission ended, the step time.
// AI-vs-AI skirmishes of every tribe pair on a multiplayer map (SkirmishSession): a timeline per side.
//
// They take long and are skipped unless PW_SOAK is set:
//   PW_SOAK=1 PW_SOAK_CFG=base PW_SOAK_MODE=passive PW_SOAK_MINUTES=15 dotnet run --project tests/PwRemake.Tests -- SoakMissions
//   PW_SOAK_MISSIONS=7,13 ... (only these)        PW_SOAK_OUT=<file> (the table; default: the console)
//   PW_SOAK=1 PW_SOAK_MINUTES=20 ... -- SoakSkirmish     (PW_SOAK_MAP: the .ula, default multi_2_jun_001)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

/// <summary>collects warnings and errors of the log (distinct messages, with counts)</summary>
public sealed class CollectSink : ILogSink
{
    public string Name => "collect";
    public readonly Dictionary<string, int> Errors = new(), Warnings = new();
    public void Write(in LogEntry e)
    {
        if (e.Level < LogLevel.Warn) return;
        var key = $"[{e.Category}] {e.Message}" + (e.Exception != null ? " | " + e.Exception.GetType().Name + ": " + e.Exception.Message + " @ " + (e.Exception.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() : "");
        var d = e.Level >= LogLevel.Error ? Errors : Warnings;
        d[key] = d.TryGetValue(key, out var n) ? n + 1 : 1;
    }
    public void Flush() { }
    public void Dispose() { }
    public void Clear() { Errors.Clear(); Warnings.Clear(); }
}

[TestClass]
public static class SoakTests
{
    static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
    static double Minutes => double.Parse(Env("PW_SOAK_MINUTES", "15"), CultureInfo.InvariantCulture);
    static void Out(TestContext t, string line)
    {
        t.Log(line);
        var f = Environment.GetEnvironmentVariable("PW_SOAK_OUT");
        if (!string.IsNullOrEmpty(f)) File.AppendAllText(f, line + "\n");
    }

    /// <summary>NaN / infinite positions of living entities (the first few)</summary>
    public static List<string> BadPositions(World W)
    {
        var bad = new List<string>();
        foreach (var u in W.Units) if (u.Alive && !(double.IsFinite(u.Pos.X) && double.IsFinite(u.Pos.Y) && double.IsFinite(u.Pos.Z) && double.IsFinite(u.Hp))) bad.Add($"{u.Name}#{u.Id} {u.Pos} hp {u.Hp} task {u.Task.Type}");
        foreach (var b in W.Buildings) if (b.Alive && !(double.IsFinite(b.Pos.X) && double.IsFinite(b.Pos.Z) && double.IsFinite(b.Hp))) bad.Add($"{b.Name}#{b.Id} {b.Pos} hp {b.Hp}");
        foreach (var p in W.Players) foreach (var r in new[] { "food", "wood", "stone" }) if (!double.IsFinite(p.Res[r])) bad.Add($"player {p.Id} {r} {p.Res[r]}");
        return bad;
    }

    /// <summary>the soak bot: the human's starting army (no workers) attack-moves to the nearest enemy every 10 s when idle</summary>
    public sealed class Bot
    {
        readonly World W; readonly Player Me; readonly HashSet<Unit> Army;
        double _next;
        public int Orders;
        public Bot(World w, Player me) { W = w; Me = me; Army = w.Units.Where(u => u.Alive && u.Owner == me && !u.IsWorker && u.Cls != "SHIP").ToHashSet(); }
        public void Tick()
        {
            if (W.Time < _next) return;
            _next = W.Time + 10;
            var idle = Army.Where(u => u.Alive && u.Owner == Me && !u.Inside && !u.Parked && (u.Task.Type == "idle" || u.Task.Type == "hold")).ToList();
            if (idle.Count == 0) return;
            double cx = idle.Average(u => u.Pos.X), cz = idle.Average(u => u.Pos.Z);
            Entity? best = null; double bd = double.PositiveInfinity;
            foreach (var e in W.Units.Cast<Entity>().Concat(W.Buildings))
            {
                if (!e.Alive || e.Inside || e.Parked || e.Owner == null || !Me.IsEnemy(e.Owner)) continue;
                double d = Math.Sqrt((e.Pos.X - cx) * (e.Pos.X - cx) + (e.Pos.Z - cz) * (e.Pos.Z - cz));
                if (d < bd) { bd = d; best = e; }
            }
            if (best == null) return;
            W.Order(idle, new Order("attackmove") { X = best.Pos.X, Z = best.Pos.Z });
            Orders++;
        }
    }

    public sealed class MissionResult
    {
        public string Cfg = "", Mode = "", Title = ""; public int Id;
        public string? CreateError, StepError;
        public double GameTime, MsAvg, MsMax, Setup;
        public int Triggers, Fired, Firings, Actions, EngineErrors, EngineWarnings, NaN, Units0, Units1, Human0, Human1, Quests, QuestsDone, BotOrders;
        public string Ended = "-";
        public List<string> Details = new();
        public string Brains = "";
    }

    public static MissionResult RunMission(TestContext t, string cfgId, int id, string mode, double minutes, CollectSink sink, ulong seed = 1)
    {
        var R = new MissionResult { Cfg = cfgId, Id = id, Mode = mode };
        var c = CampaignTests.LoadCfgPublic(cfgId);
        if (c == null) { R.CreateError = "no configuration"; return R; }
        var P = new RecordingPresenter();
        MissionSession s;
        var sw = Stopwatch.StartNew();
        sink.Clear();
        try
        {
            var start = new GameStart { Configuration = cfgId, Mission = id, Difficulty = 1, Seed = seed };
            s = MissionSession.Create(start, c.Config, new SessionOptions { Presenter = P, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data });
        }
        catch (Exception e) { R.CreateError = e.GetType().Name + ": " + e.Message + " @ " + e.StackTrace?.Split('\n').FirstOrDefault()?.Trim(); return R; }
        R.Setup = sw.Elapsed.TotalSeconds;
        R.Title = s.Data.Entry.Title;
        var W = s.World; var me = s.Human;
        R.Units0 = W.Units.Count(u => u.Alive); R.Human0 = me == null ? 0 : W.Units.Count(u => u.Alive && u.Owner == me);
        var bot = mode == "bot" && me != null ? new Bot(W, me) : null;
        int n = (int)Math.Round(minutes * 60 / World.STEP);
        double total = 0, max = 0; int steps = 0;
        var nanSeen = new HashSet<string>();
        for (int i = 0; i < n && !s.Over; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            try { s.Step(); }
            catch (Exception e) { R.StepError = $"{W.Time:0.0}s {e.GetType().Name}: {e.Message} @ {string.Join(" < ", (e.StackTrace ?? "").Split('\n').Take(4).Select(x => x.Trim()))}"; break; }
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            total += ms; steps++; if (ms > max) max = ms;
            // the UI's part: sequences and dialogue scenes end at once (NullPresenter returns false: they end themselves)
            bot?.Tick();
            if (i % 100 == 0)
                foreach (var b in BadPositions(W)) if (nanSeen.Add(b)) { R.NaN++; if (R.NaN <= 3) R.Details.Add($"NaN at {W.Time:0}s: {b}"); }
        }
        R.GameTime = W.Time; R.MsAvg = steps > 0 ? total / steps : 0; R.MsMax = max;
        R.Units1 = W.Units.Count(u => u.Alive); R.Human1 = me == null ? 0 : W.Units.Count(u => u.Alive && u.Owner == me);
        R.BotOrders = bot?.Orders ?? 0;
        var E = s.Engine;
        if (E != null)
        {
            R.Triggers = E.Triggers.Count; R.Fired = E.Triggers.Count(x => x.Fired > 0); R.Firings = E.StatFired; R.Actions = E.StatActions;
            R.EngineErrors = E.Errors.Count; R.EngineWarnings = E.Warnings.Count;
            foreach (var (tr, ty, m) in E.Errors.Take(5)) R.Details.Add($"trigger error: {tr} {ty} {m}");
            foreach (var w in E.Warnings.Distinct().Take(5)) R.Details.Add($"trigger warning: {w}");
            foreach (var u in E.UnknownConditions.Concat(E.UnknownActions)) R.Details.Add("unknown type: " + u);
        }
        R.Quests = P.Quests.Where(q => q.Change == "shown").Select(q => q.Quest).Distinct().Count();
        R.QuestsDone = P.Quests.Where(q => q.Change == "accomplished").Select(q => q.Quest).Distinct().Count();
        if (s.Host.Ended is { } en) R.Ended = (en.Won ? "won" : "lost") + "@" + W.Time.ToString("0", CultureInfo.InvariantCulture) + (en.Text is { Length: > 0 } rs ? " (" + rs + ")" : "");
        R.Brains = string.Join(" ", W.Brains.Values.OfType<TribeAI>().Select(b => $"{b.P.Id}:{b.Behaviour}/{b.Launched.Count}"));
        foreach (var (k, v) in sink.Errors.Take(8)) R.Details.Add($"log error x{v}: {k}");
        foreach (var (k, v) in sink.Warnings.Where(w => !w.Key.Contains("no model")).Take(6)) R.Details.Add($"log warning x{v}: {k}");
        if (R.StepError != null) R.Details.Insert(0, "EXCEPTION " + R.StepError);
        return R;
    }

    public static void SoakMissions(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_SOAK") == null) t.Skip("set PW_SOAK=1 (long)");
        var sink = new CollectSink(); Log.AddSink(sink);
        try
        {
            var cfgs = Env("PW_SOAK_CFG", "base,mirage").Split(',');
            var modes = Env("PW_SOAK_MODE", "passive,bot").Split(',');
            var only = Env("PW_SOAK_MISSIONS", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
            int bad = 0;
            foreach (var cfgId in cfgs)
            {
                var c = CampaignTests.LoadCfgPublic(cfgId);
                if (c == null) { Out(t, $"{cfgId}: no configuration"); continue; }
                foreach (var e in MissionEntry.LoadIndex(c.Config.MissionsDir))
                {
                    if (only.Count > 0 && !only.Contains(e.Id)) continue;
                    foreach (var mode in modes)
                    {
                        var r = RunMission(t, cfgId, e.Id, mode, Minutes, sink);
                        string line = r.CreateError != null ? $"| {cfgId} | {e.Id} | {mode} | SETUP FAILED: {r.CreateError} |"
                            : $"| {cfgId} | {e.Id} {r.Title} | {mode} | {r.GameTime / 60:0.0} min | {r.Fired}/{r.Triggers} fired, {r.Firings} firings, {r.Actions} actions | quests {r.Quests} shown {r.QuestsDone} done | {r.Ended} | " +
                              $"err {r.EngineErrors} trig / {sink.Errors.Values.Sum()} log, warn {r.EngineWarnings} | NaN {r.NaN} | units {r.Units0}->{r.Units1} (human {r.Human0}->{r.Human1}) | step {r.MsAvg:0.00} ms avg {r.MsMax:0} max, setup {r.Setup:0.0} s | brains {r.Brains} |";
                        Out(t, line);
                        foreach (var d in r.Details) Out(t, "    " + d);
                        if (r.CreateError != null || r.StepError != null || r.NaN > 0 || r.EngineErrors > 0 || sink.Errors.Count > 0) bad++;
                    }
                }
            }
            t.True(bad == 0, $"every mission ran clean ({bad} with problems)");
        }
        finally { Log.RemoveSink("collect"); }
    }
    // ------------------------------------------------------------------ AI against AI (tests/ai_match.js)
    sealed class Side
    {
        public TribeAI B = null!; public Player P = null!;
        public List<double> Epochs = new(); public int Peak, WPeak, IdleSamples, Samples; public (int W, int A)? At10;
        public double Ms, MaxMs; public HashSet<string> Builds = new();
    }
    static string Mmss(double t) => $"{(int)(t / 60)}:{(int)(t % 60):00}";

    public static List<string> RunMatch(TestContext t, string cfgId, string tribeA, string tribeB, double minutes, string behA = "Giraffe", string behB = "Giraffe", double level = 4, ulong seed = 1)
    {
        var c = CampaignTests.LoadCfgPublic(cfgId) ?? throw new SkipException("no configuration " + cfgId);
        var ula = Env("PW_SOAK_MAP", "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer/multi_2_jun_001.ula");
        if (!File.Exists(ula)) throw new SkipException("map not staged: " + ula);
        var setup = new SkirmishSetup
        {
            Seed = seed, Points = -1, ChooseHQ = true,
            Slots = { new SkirmishSlot { Tribe = tribeA, Type = "ai", Level = level, Behaviour = behA, Team = 1, Hq = 0 }, new SkirmishSlot { Tribe = tribeB, Type = "ai", Level = level, Behaviour = behB, Team = 2, Hq = 1 } },
        };
        var cs = c.Config.GameFile("Scripts/Ai/Modules/AiModuleControlDefault.usl");          // (MIRAGE's gifts, as the game reads them)
        var ss = SkirmishSession.Create(setup, ula, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, cs != null ? File.ReadAllText(cs) : null);
        var W = ss.World;
        var sides = ss.Ai.Brains.Values.Select(b => new Side { B = b, P = b.P }).ToList();
        var outp = new List<string>();
        string? err = null;
        var sw = Stopwatch.StartNew();
        double stepMax = 0, stepTotal = 0; int steps = 0;
        try
        {
            for (double tt = 0; tt < minutes * 60; tt += 5)
            {
                for (int i = 0; i < 100; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    W.Step();
                    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds; stepTotal += ms; steps++; if (ms > stepMax) stepMax = ms;
                }
                foreach (var sd in sides)
                {
                    var S = sd.B.Census(true);
                    int ep = sd.P.Epoch();
                    while (sd.Epochs.Count < ep - 1) sd.Epochs.Add(W.Time);
                    sd.Peak = Math.Max(sd.Peak, S.Fighters.Count); sd.WPeak = Math.Max(sd.WPeak, S.Workers.Count);
                    foreach (var b in S.Blds) sd.Builds.Add(b.Name);
                    sd.Samples++;
                    if (S.Idle.Count > 3 && new[] { "food", "wood", "stone" }.Any(r => sd.P.Res[r] < sd.P.Caps[r] - 20)) sd.IdleSamples++;
                    if (sd.At10 == null && W.Time >= 600) sd.At10 = (S.Workers.Count, S.Fighters.Count);
                }
                if (sides.Any(sd => sd.P.Defeated)) break;
                var bad = BadPositions(W);
                if (bad.Count > 0) { outp.Add($"NaN at {W.Time:0}: {string.Join("; ", bad.Take(3))}"); break; }
            }
        }
        catch (Exception e) { err = e.ToString().Split('\n').Take(4).Aggregate((a, b) => a + " | " + b.Trim()); }
        double end = W.Time;
        foreach (var sd in sides)
        {
            var b = sd.B; var p = sd.P; var L = b.Launched;
            var plan = L.Where(l => !l.Scripted && !l.Hunt && l.Go != null).OrderBy(l => l.Go).ToList();
            var first = plan.FirstOrDefault();
            var S = b.Census(true);
            outp.Add($"{p.Tribe} {b.Behaviour} d{b.Difficulty}: epochs {(sd.Epochs.Count > 0 ? string.Join(" ", sd.Epochs.Select((x, i) => $"{i + 2}@{Mmss(x)}")) : "none")} | at 10 min workers/army {(sd.At10 is { } a ? $"{a.W}/{a.A}" : "-")} | now {S.Workers.Count}/{S.Fighters.Count} peak army {sd.Peak} workers {sd.WPeak}" +
                $" | attacks {plan.Count}{(first != null ? $" (first {Mmss(first.Go!.Value)} {first.Type} x{first.N})" : "")} | kills/lost {p.Kills}/{p.Lost} | {(p.Defeated ? "DEFEATED " + Mmss(end) : "alive")} | res {string.Join("/", new[] { "food", "wood", "stone", "skulls" }.Select(r => Math.Round(p.Res[r])))}" +
                $" | units {S.Units.Count} buildings {S.Blds.Count} | idle samples {sd.IdleSamples}/{sd.Samples}");
            outp.Add($"   attacks: {string.Join(", ", L.Where(l => !l.Hunt).OrderBy(l => l.Go ?? l.T).Select(l => l.Go == null ? $"{Mmss(l.T)} {l.Type} (squad forming)" : $"{Mmss(l.Go.Value)} {l.Type}x{l.N}{(l.Result != null ? ">" + l.Result : "")}"))}");
            outp.Add($"   buildings: {string.Join(" ", sd.Builds)}");
        }
        outp.Add($"{(err != null ? "FAIL " + err : "ok")}: {Mmss(end)} game time in {sw.Elapsed.TotalSeconds:0} s, step {stepTotal / Math.Max(1, steps):0.00} ms avg {stepMax:0} max, {W.Units.Count(u => u.Alive)} units, hash {W.StateHash():x}");
        return outp;
    }

    public static void SoakSkirmish(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_SOAK") == null) t.Skip("set PW_SOAK=1 (long)");
        var sink = new CollectSink(); Log.AddSink(sink);
        try
        {
            foreach (var cfgId in Env("PW_SOAK_CFG", "base,mirage").Split(','))
            {
                var tribes = cfgId == "mirage" ? new[] { "Hu", "Aje", "Ninigi", "SEAS" } : new[] { "Hu", "Aje", "Ninigi" };
                var pairs = Env("PW_SOAK_PAIRS", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('-')).ToList();
                if (pairs.Count == 0) for (int i = 0; i < tribes.Length; i++) for (int j = i + 1; j < tribes.Length; j++) pairs.Add(new[] { tribes[i], tribes[j] });
                var beh = Env("PW_SOAK_BEHAVIOURS", "Giraffe,Giraffe").Split(',');
                foreach (var pr in pairs)
                {
                    sink.Clear();
                    Out(t, $"### {cfgId}: {pr[0]} {beh[0]} vs {pr[1]} {beh[1]}, level {Env("PW_SOAK_LEVEL", "4")}");
                    foreach (var l in RunMatch(t, cfgId, pr[0], pr[1], double.Parse(Env("PW_SOAK_MINUTES", "20"), CultureInfo.InvariantCulture), beh[0], beh[1], double.Parse(Env("PW_SOAK_LEVEL", "4"), CultureInfo.InvariantCulture))) Out(t, l);
                    foreach (var (k, v) in sink.Errors.Take(8)) Out(t, $"    log error x{v}: {k}");
                    foreach (var (k, v) in sink.Warnings.Where(w => !w.Key.Contains("no model")).Take(6)) Out(t, $"    log warning x{v}: {k}");
                }
            }
        }
        finally { Log.RemoveSink("collect"); }
    }
    // ------------------------------------------------------------------ the step's time on the big missions
    /// <summary>PW_SOAK=1 [PW_SOAK_CFG=base] [PW_SOAK_MISSIONS=7,13,15] [PW_SOAK_MINUTES=5] [PW_SOAK_MODE=bot] -- SoakPerf</summary>
    public static void SoakPerf(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_SOAK") == null) t.Skip("set PW_SOAK=1 (long)");
        foreach (var cfgId in Env("PW_SOAK_CFG", "base").Split(','))
        {
            var c = CampaignTests.LoadCfgPublic(cfgId);
            if (c == null) continue;
            foreach (var id in Env("PW_SOAK_MISSIONS", "7,13,15").Split(',').Select(int.Parse))
            {
                var P = new RecordingPresenter();
                // (PW_SOAK_GOD=1: the human's units take a hundredth of the damage - the debug option - so that a mission
                // whose heroes would fall in the first minute runs the whole time)
                var dbg = Env("PW_SOAK_GOD", "0") == "1" ? new DebugOptions { On = true, Me = new DebugSide { Def = 100 } } : null;
                var s = MissionSession.Create(new GameStart { Configuration = cfgId, Mission = id, Difficulty = 1, Seed = 1, Debug = dbg }, c.Config,
                    new SessionOptions { Presenter = P, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data });
                var W = s.World;
                var bot = Env("PW_SOAK_MODE", "bot") == "bot" && s.Human != null ? new Bot(W, s.Human) : null;
                // warm-up (the first steps: path caches, JIT), then measured
                int warm = int.Parse(Env("PW_SOAK_WARMUP", "200"));
                for (int i = 0; i < warm; i++) { s.Step(); bot?.Tick(); }
                if (Env("PW_SOAK_PROFILE", "1") == "1") W.Profile = new StepProfile();
                long alloc0 = GC.GetTotalAllocatedBytes(); var pause0 = GC.GetTotalPauseDuration(); int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                int n = (int)Math.Round(double.Parse(Env("PW_SOAK_MINUTES", "5"), CultureInfo.InvariantCulture) * 60 / World.STEP);
                var times = new List<double>(n);
                for (int i = 0; i < n && !s.Over; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    s.Step();
                    times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    bot?.Tick();
                }
                times.Sort();
                double Pct(double q) => times.Count == 0 ? 0 : times[Math.Min(times.Count - 1, (int)(q * times.Count))];
                Out(t, $"### {cfgId} mission {id} {s.Data.Entry.Title}: {W.Units.Count(u => u.Alive)} units, {W.Buildings.Count(b => b.Alive)} buildings, {W.Resources.Count} resources, {W.Brains.Count} brains; " +
                    $"step median {Pct(0.5):0.00} ms, p95 {Pct(0.95):0.00}, p99 {Pct(0.99):0.00}, max {Pct(1):0.0} ms over {times.Count} steps ({W.Time / 60:0.0} game min)");
                Out(t, $"    GC: {(GC.GetTotalAllocatedBytes() - alloc0) / 1048576.0 / Math.Max(1, times.Count) * 1000:0.0} MB allocated per 1000 steps, collections gen0 {GC.CollectionCount(0) - g0} gen1 {GC.CollectionCount(1) - g1} gen2 {GC.CollectionCount(2) - g2}, pauses {(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:0} ms in all");
                if (W.Profile != null) foreach (var line in W.Profile.Report(25).Split('\n')) if (line.Length > 0) Out(t, "    " + line);
            }
        }
    }
    // ------------------------------------------------------------------ determinism with everything on
    /// <summary>PW_SOAK=1 [PW_SOAK_MINUTES=10] -- SoakDeterminism: a mission with its triggers, its computer players and the
    /// soak bot, and an AI-vs-AI skirmish, each run twice with the same seed: the same state hash (sampled every minute)</summary>
    public static void SoakDeterminism(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_SOAK") == null) t.Skip("set PW_SOAK=1 (long)");
        double minutes = double.Parse(Env("PW_SOAK_MINUTES", "10"), CultureInfo.InvariantCulture);
        foreach (var cfgId in Env("PW_SOAK_CFG", "base,mirage").Split(','))
        {
            var c = CampaignTests.LoadCfgPublic(cfgId);
            if (c == null) continue;
            int mission = int.Parse(Env("PW_SOAK_MISSIONS", "3").Split(',')[0]);
            List<ulong> RunM()
            {
                var s = MissionSession.Create(new GameStart { Configuration = cfgId, Mission = mission, Difficulty = 2, Seed = 77 }, c.Config,
                    new SessionOptions { Presenter = new RecordingPresenter(), Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data });
                var bot = s.Human != null ? new Bot(s.World, s.Human) : null;
                var hs = new List<ulong>();
                int n = (int)Math.Round(minutes * 60 / World.STEP);
                for (int i = 0; i < n && !s.Over; i++) { s.Step(); bot?.Tick(); if (i % 1200 == 1199) hs.Add(s.StateHash()); }
                hs.Add(s.StateHash());
                return hs;
            }
            var a = RunM(); var b = RunM();
            Out(t, $"{cfgId} mission {mission}: {string.Join(" ", a.Select(h => h.ToString("x")))}");
            t.True(a.SequenceEqual(b), $"{cfgId} mission {mission}: the same hashes twice (first difference at minute {a.Zip(b).TakeWhile(x => x.First == x.Second).Count() + 1})");
            var tribes = cfgId == "mirage" ? ("SEAS", "Ninigi") : ("Hu", "Aje");
            var m1 = RunMatch(t, cfgId, tribes.Item1, tribes.Item2, minutes, "Dodo", "Giraffe", 5, 99);
            var m2 = RunMatch(t, cfgId, tribes.Item1, tribes.Item2, minutes, "Dodo", "Giraffe", 5, 99);
            string H(List<string> l) => l.Last().Split("hash ").Last();
            Out(t, $"{cfgId} skirmish {tribes.Item1}-{tribes.Item2}: {H(m1)} / {H(m2)}");
            t.Equal(H(m1), H(m2), $"{cfgId} skirmish: the same hash twice");
        }
    }
}
