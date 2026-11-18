// The scripted campaign playthroughs (Playthrough/PlayNN.cs, ports of remake/tests/campaign_play_NN.js): each mission
// played to its end with MissionSession (triggers, computer players) by the CampaignBot, one tribe and configuration
// per run. docs/TESTING.md "Playthroughs" says how to run them; docs/PLAYTHROUGHS.md is the table of the last results
// (built from Playthrough/results/*.json).
//
// Opt-in (they take minutes each), run from the repository's root:
//   PW_PLAY=1 dotnet run --project tests/PwRemake.Tests -- Playthroughs
//   PW_PLAY_CFG=base,mirage   PW_PLAY_MISSIONS=0,3,11,41 (index.json ids: 41-43 BoosterPack 1, 20-23 MIRAGE's SEAS missions)
//   PW_PLAY_TRIBES=Hu,SEAS (default: the configuration's playable tribes)
//   PW_PLAY_DEBUG=<debug options JSON, as settings.json "debug"> (replays a game with the menu's debug options)
//   PW_PLAY_OUT=<folder> (results and logs; default tests/PwRemake.Tests/Playthrough/results)
//   PW_PLAY=1 ... -- PlaythroughTable      (only rebuilds docs/PLAYTHROUGHS.md from the results)
// SmokeMission03 (mission 3 as the Norsemen, base) runs in the normal suite.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;

namespace PwRemake.Tests.Playthrough;

/// <summary>What one run gave (a results JSON).</summary>
public sealed class PlayResult
{
    public string Cfg { get; set; } = "";
    public int Mission { get; set; }
    public string Tribe { get; set; } = "";
    public string Title { get; set; } = "";
    public int Steps { get; set; }
    public int Passed { get; set; }
    public string? FailedStep { get; set; }
    public bool Over { get; set; }
    public bool Won { get; set; }
    public bool Finished { get; set; }
    public double GameMinutes { get; set; }
    public double WallSeconds { get; set; }
    public int Teleports { get; set; }
    public int CheatKills { get; set; }
    public int TopUps { get; set; }
    public int TriggerErrors { get; set; }
    public int Fails { get; set; }
    public string? Exception { get; set; }
    public string Date { get; set; } = "";
}

[TestClass]
public static class PlaythroughTests
{
    static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;

    /// <summary>the repository's root (the folder with docs/ and tests/ above the test binary), else the working folder</summary>
    public static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "docs")) && Directory.Exists(Path.Combine(d.FullName, "tests", "PwRemake.Tests"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }
    static string OutDir => Env("PW_PLAY_OUT", Path.Combine(RepoRoot(), "tests", "PwRemake.Tests", "Playthrough", "results"));

    /// <summary>the playthrough of each mission id (index.json "id"): 0-16 the campaign, 41-43 BoosterPack 1's missions
    /// (both configurations), 20-23 MIRAGE's SEAS missions (20-22 the SEAS versions of 41-43, 23 "Seeking Ada")</summary>
    public static readonly Dictionary<int, Func<MissionSession, PlayPresenter, CampaignBot>> Plays = new()
    {
        [0] = (s, p) => new Play00(s, p), [1] = (s, p) => new Play01(s, p), [2] = (s, p) => new Play02(s, p), [3] = (s, p) => new Play03(s, p),
        [4] = (s, p) => new Play04(s, p), [5] = (s, p) => new Play05(s, p), [6] = (s, p) => new Play06(s, p), [7] = (s, p) => new Play07(s, p),
        [8] = (s, p) => new Play08(s, p), [9] = (s, p) => new Play09(s, p), [10] = (s, p) => new Play10(s, p), [11] = (s, p) => new Play11(s, p),
        [12] = (s, p) => new Play12(s, p), [13] = (s, p) => new Play13(s, p), [14] = (s, p) => new Play14(s, p), [15] = (s, p) => new Play15(s, p),
        [16] = (s, p) => new Play16(s, p),
        [41] = (s, p) => new PlayB41(s, p), [20] = (s, p) => new PlayB41(s, p),
        [42] = (s, p) => new PlayB42(s, p), [21] = (s, p) => new PlayB42(s, p),
        [43] = (s, p) => new PlayB43(s, p), [22] = (s, p) => new PlayB43(s, p),
        [23] = (s, p) => new PlaySeas02(s, p),
    };
    /// <summary>the missions in the menu's order (Campaigns.txt: the BoosterPack's after mission 4), the SEAS missions last</summary>
    public static readonly int[] Order = { 0, 1, 2, 3, 4, 41, 42, 43, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 21, 22, 23 };

    /// <summary>one playthrough: the mission of the configuration with the tribe; the report (log) and the result</summary>
    public static (PlayResult R, string Log) Run(TestContext t, string cfgId, int mission, string tribe, Action<string>? progress = null)
    {
        var c = CampaignTests.LoadCfgPublic(cfgId);
        if (c == null) t.Skip($"configuration {cfgId} or its data not found");
        var start = new GameStart { Configuration = cfgId, Mission = mission, Tribe = tribe, Difficulty = 1, Seed = 1 };
        // PW_PLAY_DEBUG='{"on":true,"free":true,...}': the debug options of the menu (settings.json "debug"), to replay a player's game
        if (Environment.GetEnvironmentVariable("PW_PLAY_DEBUG") is { Length: > 0 } dj)
            start.Debug = System.Text.Json.JsonSerializer.Deserialize<DebugOptions>(dj)?.ForGame();
        if (MissionSession.MissionFolderOf(start, c!.Config) == null) t.Skip($"mission {mission} not in {cfgId}");
        var e = MissionEntry.LoadIndex(c.Config.MissionsDir).First(x => x.Id == mission);
        if (WorldBuilder.FindMap(c.Config, e.Map) == null) t.Skip($"map {e.Map} not staged");
        // a Dustrider mission with an army budget played as the Dustriders starts with the army screen's default army, as the
        // campaign menu starts it (CampaignMenu.StartWithArmy; no bonus points): the level's start army the JS took lacks
        // their resource collector - a card bought with the budget (mission 6 as the Dustriders had no base). (Not for the
        // other tribes: mission 11's default army for the Norsemen lacks the three heroes - see docs/CAMPAIGN.md)
        if (e.PointBuy && start.Army == null && e.Tribe == "Aje" && (string.IsNullOrEmpty(tribe) || tribe == "Aje"))
            start.Army = CampaignArmy.Of(Json.TryParseFile(Path.Combine(c.Config.MissionsDir, e.Folder, "mission.json")), c.Rules, tribe, 0)?.Defaults();
        var sw = Stopwatch.StartNew();
        var P = new PlayPresenter();
        var s = MissionSession.Create(start, c.Config, new SessionOptions { Presenter = P, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data });
        var bot = Plays[mission](s, P);
        bot.Progress = progress;
        string log;
        try { log = bot.Play(); }
        catch (Exception ex) { log = "FAIL exception outside the steps: " + ex; bot.Exception = ex.GetType().Name + ": " + ex.Message; }
        var E = s.Engine!;
        var R = new PlayResult
        {
            Cfg = cfgId, Mission = mission, Tribe = s.Human?.Tribe ?? tribe, Title = s.Data.Entry.Title,
            Steps = bot.StepCount, Passed = bot.reached.Count(x => x), FailedStep = bot.FailedStep, Over = s.Over, Won = s.Won,
            GameMinutes = Math.Round(s.World.Time / 60, 1), WallSeconds = Math.Round(sw.Elapsed.TotalSeconds),
            Teleports = bot.Teleports, CheatKills = bot.Kills, TopUps = bot.TopUps, TriggerErrors = E.Errors.Count,
            Fails = log.Split('\n').Count(l => l.StartsWith("FAIL")), Exception = bot.Exception,
            Date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        // played to its end: the mission was won (a step whose own check failed on the way - a quest the mission shows again
        // in the same step, a level the data gives otherwise - is in Fails and the table)
        R.Finished = R.Over && R.Won && R.Exception == null;
        return (R, log);
    }

    static string Key(string cfg, int m, string tribe) => $"{cfg}_{m:00}_{tribe}";

    public static void Playthroughs(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_PLAY") == null) t.Skip("set PW_PLAY=1 (long)");
        var cfgs = Env("PW_PLAY_CFG", "base,mirage").Split(',', StringSplitOptions.RemoveEmptyEntries);
        // PW_PLAY_MISSIONS: mission ids as in index.json (41-43 BoosterPack 1, 20-23 MIRAGE's SEAS missions; "B41" = 41);
        // default all (a mission a configuration does not have is skipped: 20-23 are MIRAGE's)
        var missions = Env("PW_PLAY_MISSIONS", string.Join(",", Order)).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x.Trim().TrimStart('B', 'b'))).ToList();
        var tribesEnv = Env("PW_PLAY_TRIBES", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        var dir = OutDir; Directory.CreateDirectory(Path.Combine(dir, "logs"));
        int bad = 0, runs = 0;
        foreach (var cfgId in cfgs)
        {
            var c = CampaignTests.LoadCfgPublic(cfgId);
            if (c == null) { t.Log($"{cfgId}: no configuration"); continue; }
            var tribes = tribesEnv.Count > 0 ? tribesEnv.Where(x => c.Config.PlayableTribes.Contains(x)).ToList() : c.Config.PlayableTribes;
            foreach (var m in missions)
                foreach (var tribe in tribes)
                {
                    if (!Plays.ContainsKey(m)) continue;
                    var mc = CampaignTests.LoadCfgPublic(cfgId)!;
                    if (!MissionEntry.LoadIndex(mc.Config.MissionsDir).Any(x => x.Id == m)) continue;     // (the base game has no SEAS missions)
                    var key = Key(cfgId, m, tribe);
                    Console.WriteLine($"==== {key}");
                    var (R, log) = Run(t, cfgId, m, tribe, line => Console.WriteLine($"   [{key}] {line}"));
                    runs++;
                    if (!R.Finished) bad++;
                    Console.WriteLine(log);
                    Console.WriteLine($"==== {key}: {(R.Finished ? "FINISHED" : "NOT FINISHED")} steps {R.Passed}/{R.Steps}, {R.GameMinutes} game min, {R.WallSeconds} s, teleports {R.Teleports}, cheat kills {R.CheatKills}, top-ups {R.TopUps}, over {R.Over} won {R.Won}");
                    File.WriteAllText(Path.Combine(dir, "logs", key + ".txt"), log + "\n");
                    File.WriteAllText(Path.Combine(dir, key + ".json"), JsonSerializer.Serialize(R, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n");
                }
        }
        WriteTable(dir);
        t.True(bad == 0, $"every playthrough finished ({runs - bad} of {runs})");
    }

    public static void PlaythroughTable(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_PLAY") == null) t.Skip("set PW_PLAY=1");
        WriteTable(OutDir);
    }

    /// <summary>docs/PLAYTHROUGHS.md from the results: a row per mission, a column per configuration and tribe</summary>
    public static void WriteTable(string dir)
    {
        var all = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(f => JsonSerializer.Deserialize<PlayResult>(File.ReadAllText(f), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!).ToList() : new List<PlayResult>();
        foreach (var r in all) r.Finished = r.Over && r.Won && r.Exception == null;     // (as Run: results written before that rule)
        var cols = new[] { ("base", "Hu"), ("base", "Aje"), ("base", "Ninigi"), ("mirage", "Hu"), ("mirage", "Aje"), ("mirage", "Ninigi"), ("mirage", "SEAS") };
        var sb = new StringBuilder();
        sb.AppendLine("# Campaign playthroughs");
        sb.AppendLine();
        sb.AppendLine("The scripted playthroughs (`tests/PwRemake.Tests/Playthrough`, ports of the browser remake's");
        sb.AppendLine("`tests/campaign_play_NN.js`; BoosterPack 1's 41-43 and MIRAGE's SEAS missions 20-23 written from their triggers) play");
        sb.AppendLine("every mission to its end with each tribe in both configurations (docs/TESTING.md \"Playthroughs\"). Built from");
        sb.AppendLine("`tests/PwRemake.Tests/Playthrough/results/*.json` by the runner; do not edit by hand. Rows in the menu's order (index.json ids).");
        sb.AppendLine();
        sb.AppendLine("A cell: **won** = the mission was played to its end and won (game minutes; cheats: teleports / cheat kills / top-ups;");
        sb.AppendLine("a step whose own check failed on the way is named), else the step it stopped at (steps passed / all).");
        sb.AppendLine();
        sb.AppendLine("| mission | " + string.Join(" | ", cols.Select(c => c.Item1 + " " + c.Item2)) + " |");
        sb.AppendLine("|---|" + string.Concat(cols.Select(_ => "---|")));
        foreach (var m in Order)
        {
            var row = all.Where(r => r.Mission == m).ToList();
            if (row.Count == 0 && m > 16) continue;
            var title = row.FirstOrDefault()?.Title ?? "";
            sb.Append($"| {m} {title} |");
            foreach (var (cfg, tribe) in cols)
            {
                var r = row.FirstOrDefault(x => x.Cfg == cfg && x.Tribe == tribe);
                sb.Append(r == null ? " - |" : r.Finished ? $" **won** {N(r.GameMinutes)} min ({r.Teleports}/{r.CheatKills}/{r.TopUps}){(r.Fails > 0 ? $"; {r.Fails} check(s) failed: {Short(r.FailedStep ?? "")}" : "")} |"
                    : $" {r.Passed}/{r.Steps}: {Short(r.FailedStep ?? r.Exception ?? (r.Over ? (r.Won ? "won, a check failed" : "lost") : "?"))} |");
            }
            sb.AppendLine();
        }
        var dates = all.Select(r => r.Date).Distinct().OrderBy(x => x).ToList();
        sb.AppendLine();
        sb.AppendLine($"Runs: {all.Count}, finished {all.Count(r => r.Finished)}; dates {string.Join(", ", dates)}. Difficulty 1 (normal), seed 1.");
        // (results kept elsewhere - PW_PLAY_OUT, a replay - get their table there, not over the repository's)
        var target = Environment.GetEnvironmentVariable("PW_PLAY_OUT") is { Length: > 0 } ? Path.Combine(dir, "PLAYTHROUGHS.md") : Path.Combine(RepoRoot(), "docs", "PLAYTHROUGHS.md");
        File.WriteAllText(target, sb.ToString());
    }
    static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    static string Short(string s) { s = s.Replace("|", "/"); return s.Length > 70 ? s[..67] + "..." : s; }

    /// <summary>a quick playthrough in the normal suite: mission 3 (Amazon Island) as the Norsemen</summary>
    public static void SmokeMission03(TestContext t)
    {
        var (R, log) = Run(t, "base", 3, "Hu");
        if (!R.Finished) t.Log(log);
        t.True(R.Finished, $"mission 3 played to the end ({R.Passed}/{R.Steps} steps, over {R.Over}, won {R.Won})");
        t.Log($"mission 3 as Hu: {R.GameMinutes} game minutes, {R.WallSeconds} s, teleports {R.Teleports}, cheat kills {R.CheatKills}");
    }
}
