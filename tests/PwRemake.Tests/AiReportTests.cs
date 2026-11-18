// The AI report (docs/AI.md "Measuring the computer player", docs/TESTING.md): long headless games with a probe on every
// computer player that prints a timeline per minute and a stall report - for finding what makes a computer player
// stop growing, and as a regression check after changes to src/Sim/Ai. Skipped unless PW_AIREPORT is set:
//
//   PW_AIREPORT=1 dotnet run -- AiReport.Skirmish     AI against AI on a multiplayer map (default multi_2_jun_001)
//       PW_AIREPORT_CFG=base,mirage  PW_AIREPORT_PAIRS=Hu-Aje,Ninigi-SEAS (default: every tribe against the next)
//       PW_AIREPORT_MINUTES=20  PW_AIREPORT_LEVEL=4  PW_AIREPORT_BEHAVIOUR=Giraffe  PW_AIREPORT_SEED=1
//   PW_AIREPORT=1 dotnet run -- AiReport.Coop         campaign missions in co-op: seat 1 a passive human, seats 2-3
//       AI commanders (PW_AIREPORT_TRIBES=Aje,Ninigi); the hero phase is ended after 30 s; the mission's own
//       computer players are probed too (PW_AIREPORT_MISSIONS=1,4,8,9,13)
//   PW_AIREPORT_TIMELINE=0: only the summary and the stalls;  PW_AIREPORT_OUT=<file>: also appended there
//
// The probe only reads the world (it never takes the brain's census), so a probed game plays as an unprobed one.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Coop;
using PwRemake.Sim.Game;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

/// <summary>watches one computer player: a sample every 5 s, a line per minute, stalls</summary>
public sealed class AiProbe
{
    public readonly TribeAI B; public readonly Player P; public readonly string Label;
    readonly World W;
    public readonly List<string> Lines = new(), Stalls = new();
    public readonly List<double> Epochs = new();
    public (int W, int A, int Lv)? At10, At20;
    public int StallCount, IdleMinutes, StuckMinutes, NoGrowthMinutes, Trained, Finished, PeakArmy;
    public double DefeatedAt = -1;
    readonly Dictionary<int, (double X, double Z)> _last = new();
    readonly Dictionary<int, double> _siteSeen = new();
    readonly HashSet<int> _siteReported = new(), _seenUnits = new(), _builtSeen = new();
    int _samples, _idleS, _stuckS, _minTrained, _minFinished, _lastEpoch = 1, _noGrowStreak, _atk0;
    double _gain0;
    bool _first = true;
    readonly Dictionary<string, int> _jobsSum = new();

    public AiProbe(World w, TribeAI b, string label) { W = w; B = b; P = b.P; Label = label; }

    static double D(double ax, double az, double bx, double bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
    static bool Working(Unit u) => u.Task.Type switch
    {
        "gather" => u.Task.Phase is "work" or "chop" or "pickup" or "putdown",
        "build" or "repair" => u.Task.Building != null && u.Task.Building.Alive && u.EdgeDist(u.Task.Building) < 4,
        _ => false,
    };
    public static string Job(Unit u) => u.Task.Type switch
    {
        "idle" => "idle",
        "gather" => u.Task.Res ?? "gather",
        "build" or "repair" => "build",
        "attack" or "attackmove" => "fight",
        _ => "other:" + u.Task.Type,
    };

    /// <summary>every 5 s of game time</summary>
    public void Sample()
    {
        if (P.Defeated) { if (DefeatedAt < 0) DefeatedAt = W.Time; return; }
        if (B.Paused && B.Behaviour == "Mikrobe") return;           // (asleep: a campaign player before its AIBV)
        _samples++;
        var workers = new List<Unit>(); int army = 0, lv = 0;
        foreach (var u in W.Units)
        {
            if (!u.Alive || u.Owner != P || u.Parked || u.Autonomous || u.Illusion) continue;
            if (_seenUnits.Add(u.Id) && !_first) { Trained++; _minTrained++; }
            if (u.Naval || u.Cls == "SHIP") continue;
            if (u.IsWorker) { if (!u.Inside) workers.Add(u); continue; }
            if (u.CannotFight || u.Stationary || u.IsDropoff || u.Def?.Unique == true) continue;
            army++; lv += u.Level;
        }
        foreach (var b in W.Buildings)
        {
            if (!b.Alive || b.Owner != P) continue;
            if (!b.Built) { if (!_siteSeen.ContainsKey(b.Id)) _siteSeen[b.Id] = W.Time; continue; }
            if (_builtSeen.Add(b.Id) && !_first) { Finished++; _minFinished++; }
        }
        _first = false;
        PeakArmy = Math.Max(PeakArmy, army);
        int ep = P.Epoch();
        while (Epochs.Count < ep - 1) Epochs.Add(W.Time);
        // idle and stuck workers (stuck: has a task but stood within 0.5 m for 5 s without working at it)
        int idle = 0, stuck = 0;
        foreach (var u in workers)
        {
            var j = Job(u);
            _jobsSum[j] = _jobsSum.GetValueOrDefault(j) + 1;
            if (j == "idle") idle++;
            else if (_last.TryGetValue(u.Id, out var lp) && D(lp.X, lp.Z, u.Pos.X, u.Pos.Z) < 0.5 && !Working(u)) stuck++;
            _last[u.Id] = (u.Pos.X, u.Pos.Z);
        }
        bool capped = new[] { "food", "wood", "stone" }.All(r => P[r] >= P.Caps[r] - 20);
        if (idle >= 2 && !capped) _idleS++;
        if (stuck >= 3) _stuckS++;
        // old construction sites
        foreach (var (id, t0) in _siteSeen)
        {
            if (_siteReported.Contains(id) || W.Time - t0 < 180) continue;
            var b = W.Buildings.FirstOrDefault(x => x.Id == id);
            if (b == null || !b.Alive || b.Built) continue;
            _siteReported.Add(id);
            Stall($"site {b.Name}#{id} unfinished for {W.Time - t0:0} s (progress {b.Progress:0.00}, builders {b.Builders.Count(x => x.Alive)}) at {b.Pos.X:0},{b.Pos.Z:0}");
        }
        if (W.Time >= 599 && At10 == null) At10 = (workers.Count, army, lv);
        if (W.Time >= 1199 && At20 == null) At20 = (workers.Count, army, lv);
        // a minute
        if (_samples % 12 != 0) return;
        double gain = P.Gained.Values.Sum();
        double income = gain - _gain0; _gain0 = gain;
        int atk = B.Launched.Count(l => !l.Hunt);
        var jobs = string.Join(" ", _jobsSum.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value / 12.0:0.#}"));
        _jobsSum.Clear();
        var reqs = string.Join(", ", B.Extra.Concat(B.Requests).Select(e => e.ToString()));
        double room = P.MaxUnits - P.Units - P.QueuedUnits;
        Lines.Add($"{Mmss(W.Time)} E{ep} res {P["food"]:0}/{P["wood"]:0}/{P["stone"]:0}/{P["skulls"]:0} cap {P.Caps["food"]:0} +{income:0}/min pop {P.Units}/{P.MaxUnits:0} | W{workers.Count} [{jobs}] stuck {stuck} | +{_minTrained} units +{_minFinished} blds | army {army} lv {lv} | atk {atk}{(atk > _atk0 ? $" (+{atk - _atk0})" : "")} | rsv {string.Join("/", new[] { "food", "wood", "stone", "skulls" }.Select(r => B.Reserve.GetValueOrDefault(r).ToString("0")))} | req {reqs}");
        if (_idleS >= 10) { IdleMinutes++; Stall($"idle workers: {idle} idle ({_idleS}/12 samples), res {P["food"]:0}/{P["wood"]:0}/{P["stone"]:0}"); }
        if (_stuckS >= 10)
        {
            StuckMinutes++;
            var st = workers.Where(u => _last.TryGetValue(u.Id, out var lp) && !Working(u) && u.Task.Type != "idle").Take(4).Select(u => $"{u.Name}#{u.Id}@{u.Pos.X:0},{u.Pos.Z:0} {u.Task.Type}/{u.Task.Phase}");
            Stall($"stuck workers: {stuck} ({_stuckS}/12 samples): {string.Join(" ", st)}");
        }
        bool grew = _minTrained > 0 || _minFinished > 0 || ep > _lastEpoch;
        double banked = P["food"] + P["wood"] + P["stone"];
        if (!grew && banked >= 300) { _noGrowStreak++; NoGrowthMinutes++; } else _noGrowStreak = 0;
        if (_noGrowStreak == 3) Stall($"no growth for 3 minutes with {banked:0} banked, pop {P.Units}/{P.MaxUnits:0}, requests: {reqs}; reserve {string.Join("/", B.Reserve.Values.Select(v => v.ToString("0")))}");
        _lastEpoch = ep; _minTrained = 0; _minFinished = 0; _idleS = 0; _stuckS = 0; _atk0 = atk;
    }
    void Stall(string s) { StallCount++; Stalls.Add($"{Mmss(W.Time)} {s}"); }
    public static string Mmss(double t) => $"{(int)(t / 60)}:{(int)(t % 60):00}";

    public string Summary()
    {
        string E(int i) => Epochs.Count > i ? Mmss(Epochs[i]) : "-";
        int plan = B.Launched.Count(l => !l.Hunt && !l.Scripted && l.Go != null);
        return $"| {Label} | {E(0)} | {E(1)} | {E(2)} | {E(3)} | {(At10 is { } a ? $"{a.W}/{a.A}" : "-")} | {(At20 is { } b ? $"{b.W}/{b.A}" : "-")} | {PeakArmy} | {plan} | {StallCount} (idle {IdleMinutes}, stuck {StuckMinutes}, no growth {NoGrowthMinutes}) | {B.PlaceFails} | {B.Launched.Count(l => l.Type == "coop")} | {P.Kills}/{P.Lost} | {(DefeatedAt >= 0 ? "defeated " + Mmss(DefeatedAt) : "alive")} |";
    }
    public const string Header = "| side | E2 | E3 | E4 | E5 | workers/army 10 min | 20 min | peak army | attacks | stalls | places not found | support squads | kills/lost | end |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|";
}

[TestClass]
public static class AiReport
{
    static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
    static void Out(TestContext t, string line)
    {
        Console.WriteLine(line);
        var f = Environment.GetEnvironmentVariable("PW_AIREPORT_OUT");
        if (!string.IsNullOrEmpty(f)) File.AppendAllText(f, line + "\n");
    }
    static void Print(TestContext t, string title, List<AiProbe> probes, string end)
    {
        Out(t, "### " + title + " - " + end);
        bool tl = Env("PW_AIREPORT_TIMELINE", "1") != "0";
        foreach (var p in probes)
        {
            Out(t, $"#### {p.Label}");
            if (tl) foreach (var l in p.Lines) Out(t, "    " + l);
            foreach (var s in p.Stalls) Out(t, "    STALL " + s);
        }
        Out(t, AiProbe.Header);
        foreach (var p in probes) Out(t, p.Summary());
    }

    /// <summary>AI against AI; -> the probes (the result: who was defeated)</summary>
    public static List<AiProbe> Skirmish(TestContext t, string cfgId, string a, string b, double minutes, string beh = "Giraffe", double level = 4, ulong seed = 1)
    {
        var c = CampaignTests.LoadCfgPublic(cfgId) ?? throw new SkipException("no configuration " + cfgId);
        var ula = Env("PW_AIREPORT_MAP", "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer/multi_2_jun_001.ula");
        if (!File.Exists(ula)) throw new SkipException("map not staged: " + ula);
        var setup = new SkirmishSetup
        {
            Seed = seed, Points = -1, ChooseHQ = true,
            Slots = { new SkirmishSlot { Tribe = a, Type = "ai", Level = level, Behaviour = beh, Team = 1, Hq = 0 }, new SkirmishSlot { Tribe = b, Type = "ai", Level = level, Behaviour = beh, Team = 2, Hq = 1 } },
        };
        var cs = c.Config.GameFile("Scripts/Ai/Modules/AiModuleControlDefault.usl");
        var ss = SkirmishSession.Create(setup, ula, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, cs != null ? File.ReadAllText(cs) : null);
        var W = ss.World;
        var probes = ss.Ai.Brains.Values.Select(br => new AiProbe(W, br, $"{cfgId} {br.P.Tribe} (vs {(br.P.Tribe == a ? b : a)}) p{br.P.Id}")).ToList();
        var sw = Stopwatch.StartNew();
        for (double tt = 0; tt < minutes * 60; tt += 5)
        {
            for (int i = 0; i < 100; i++) W.Step();
            foreach (var p in probes) p.Sample();
            if (probes.Any(p => p.P.Defeated)) { foreach (var p in probes) p.Sample(); break; }
        }
        Print(t, $"{cfgId} skirmish {a} vs {b} ({beh}, level {level}, seed {seed})", probes, $"{AiProbe.Mmss(W.Time)} in {sw.Elapsed.TotalSeconds:0} s, hash {W.StateHash():x}");
        return probes;
    }

    public static void Skirmish(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_AIREPORT") == null) t.Skip("set PW_AIREPORT=1 (long)");
        double minutes = double.Parse(Env("PW_AIREPORT_MINUTES", "20"), CultureInfo.InvariantCulture);
        double level = double.Parse(Env("PW_AIREPORT_LEVEL", "4"), CultureInfo.InvariantCulture);
        ulong seed = ulong.Parse(Env("PW_AIREPORT_SEED", "1"));
        var all = new List<AiProbe>();
        foreach (var cfg in Env("PW_AIREPORT_CFG", "base,mirage").Split(','))
        {
            var tribes = cfg == "mirage" ? new[] { "Hu", "Aje", "Ninigi", "SEAS" } : new[] { "Hu", "Aje", "Ninigi" };
            var pairs = Env("PW_AIREPORT_PAIRS", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('-')).ToList();
            if (pairs.Count == 0) for (int i = 0; i < tribes.Length; i++) pairs.Add(new[] { tribes[i], tribes[(i + 1) % tribes.Length] });
            foreach (var pr in pairs) all.AddRange(Skirmish(t, cfg, pr[0], pr[1], minutes, Env("PW_AIREPORT_BEHAVIOUR", "Giraffe"), level, seed));
        }
        Out(t, "### all skirmish sides"); Out(t, AiProbe.Header);
        foreach (var p in all) Out(t, p.Summary());
    }

    /// <summary>a campaign mission in co-op: seat 1 passive, seats 2-3 AI commanders; every computer player probed</summary>
    public static List<AiProbe> CoopMission(TestContext t, string cfg, int mission, string t2, string t3, double minutes, ulong seed = 1)
    {
        var start = CoopTests.CoopStart(t, cfg, mission, new[] { ("human", ""), ("ai", t2), ("ai", t3) }, seed: seed);
        var s = CoopTests.Session(t, start);
        var W = s.World; var R = s.Campaign.Coop!;
        var probes = new List<AiProbe>();
        void Watch()
        {
            foreach (var br in (W.Ai?.Brains.Values ?? Enumerable.Empty<TribeAI>()).Concat(R.Seats.Where(x => x.Ai && x.Commander != null).Select(x => x.Commander!.Brain)).Distinct())
                if (!probes.Any(p => p.B == br) && !(br.Paused && br.Behaviour == "Mikrobe"))
                {
                    var seat = R.Seats.FirstOrDefault(x => x.Ai && x.Commander?.Brain == br);
                    probes.Add(new AiProbe(W, br, seat != null ? $"{cfg} m{mission} seat {seat.Seat} {br.P.Tribe} (AI ally)" : $"{cfg} m{mission} enemy p{br.P.Id} {br.P.Tribe} {br.Behaviour}"));
                }
        }
        var sw = Stopwatch.StartNew();
        for (double tt = 0; tt < minutes * 60 && !s.Over; tt += 5)
        {
            s.Run(5);
            if (R.HeroPhase && W.Time >= 30) R.HandOver();
            Watch();
            foreach (var p in probes) p.Sample();
        }
        var end = $"{AiProbe.Mmss(W.Time)} in {sw.Elapsed.TotalSeconds:0} s{(s.Over ? (s.Won ? " WON" : " LOST") : "")}, commanders: {string.Join("; ", R.Seats.Where(x => x.Ai).Select(x => x.Commander!.ToString()))}";
        Print(t, $"{cfg} mission {mission} co-op [human, ai {t2}, ai {t3}]", probes, end);
        return probes;
    }

    public static void Coop(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_AIREPORT") == null) t.Skip("set PW_AIREPORT=1 (long)");
        double minutes = double.Parse(Env("PW_AIREPORT_MINUTES", "15"), CultureInfo.InvariantCulture);
        var tr = Env("PW_AIREPORT_TRIBES", "Aje,Ninigi").Split(',');
        var all = new List<AiProbe>();
        foreach (var cfg in Env("PW_AIREPORT_CFG", "base,mirage").Split(','))
            foreach (var m in Env("PW_AIREPORT_MISSIONS", "1,4,8,9,13").Split(',').Select(int.Parse))
            {
                if (CoopDefinition.Load(cfg, m) == null) { Out(t, $"{cfg} {m}: no co-op definition"); continue; }
                all.AddRange(CoopMission(t, cfg, m, tr[0], tr.Length > 1 ? tr[1] : tr[0], minutes));
            }
        Out(t, "### all co-op computer players"); Out(t, AiProbe.Header);
        foreach (var p in all) Out(t, p.Summary());
    }
}
