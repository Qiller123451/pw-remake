// Tests of the campaign layer (src/Sim/Campaign, docs/CAMPAIGN.md) on the real converted missions: a mission folder +
// the configuration's game data + the original map -> a running world with its trigger engine. Set up, step 5 game
// minutes headless (the systems that are not ported yet are stubs: nothing fights, gathers or builds), check players,
// objects, the start army, groups, regions, triggers firing, no exceptions; tribe swap; determinism.
//
// Data: PW_CONTENT (default /home/claude/testcontent) with configs/base and configs/mirage (mission folders), the
// configuration's data folder (else PW_DATA_BASE / PW_DATA_MIRAGE, default /opt/data/base and /opt/data/mirage) and the
// installation's maps (config.json "install"). Missing data = skipped.
//
//   dotnet run --project tests/PwRemake.Tests -- Campaign            all of them
//   PW_CAMPAIGN_MINUTES=1 ... -- CampaignAll                           shorter runs of every mission
//   PW_CAMPAIGN_MISSIONS=20,41 ... -- CampaignTests.All                  only those missions
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

/// <summary>A mission UI that records what it is asked (tests).</summary>
public sealed class RecordingPresenter : NullPresenter
{
    public readonly List<string> Calls = new();
    public readonly List<(string Quest, string Change)> Quests = new();
    public readonly List<SequencePlay> Sequences = new();
    public readonly List<DialogPlay> Dialogs = new();
    public string InfoBar = "";
    public override void QuestChanged(QuestState quest, string change) { Quests.Add((quest.Name, change)); Calls.Add($"quest {quest.Name} {change}"); }
    public override void QuestionMarkChanged(QuestionMark mark) => Calls.Add($"qm {mark.Name} {mark.State}");
    public override void ShowMarker(MapMarker m) => Calls.Add($"marker {m.Id}");
    public override void RemoveMarker(string id) => Calls.Add($"unmarker {id}");
    public override void SetInfoBar(string text) { InfoBar = text; Calls.Add("infobar " + text); }
    public override void PlaySound(string name, V3? at) => Calls.Add("sound " + name);
    public override void PlayMusic(string file, bool loop) => Calls.Add("music " + file);
    public override bool PlaySequence(SequencePlay play) { Sequences.Add(play); Calls.Add("sequence " + play.Request.Path); return false; }
    public override void SequenceEnded(SequencePlay play) => Calls.Add("sequence end " + play.Request.Path);
    public override bool PlayDialog(DialogPlay d) { Dialogs.Add(d); Calls.Add("dialog " + d.Path); return false; }
    public override void EndMission(bool won, string text, double delay) { base.EndMission(won, text, delay); Calls.Add($"end {won} {text}"); }
}

[TestClass]
public static class CampaignTests
{
    // ------------------------------------------------------------------ shared data (loaded once per configuration)
    public sealed class Cfg
    {
        public GameConfiguration Config = null!;
        public string Data = "";
        public PwRemake.Sim.Rules.Rules Rules = null!;
        public ModelManifest Models = null!;
        public ForestData? Forest;
        public GroundBrushes? Brushes;
        public System.Text.Json.Nodes.JsonNode? Ai;
    }
    static readonly Dictionary<string, Cfg?> _cfgs = new();

    public static Cfg? LoadCfgPublic(string id) => LoadCfg(id);
    static Cfg? LoadCfg(string id)
    {
        if (_cfgs.TryGetValue(id, out var have)) return have;
        var root = Environment.GetEnvironmentVariable("PW_CONTENT") is { Length: > 0 } pc ? pc : "/home/claude/testcontent";
        var cfg = GameConfiguration.Load(Path.Combine(root, "configs", id));
        Cfg? res = null;
        if (cfg != null && Directory.Exists(cfg.MissionsDir))
        {
            var data = Directory.Exists(cfg.Data) && File.Exists(Path.Combine(cfg.Data, "techtree.json")) ? cfg.Data
                : Environment.GetEnvironmentVariable(id == "mirage" ? "PW_DATA_MIRAGE" : "PW_DATA_BASE") ?? (id == "mirage" ? "/opt/data/mirage" : "/opt/data/base");
            if (File.Exists(Path.Combine(data, "techtree.json")))
                res = new Cfg
                {
                    Config = cfg, Data = data, Rules = PwRemake.Sim.Rules.Rules.Load(data), Models = ModelManifest.Load(data),
                    Forest = ForestData.LoadFile(Path.Combine(data, "forest.json")), Brushes = GroundBrushes.LoadFile(Path.Combine(data, "assets", "terrain", "objbrush.json")),
                    Ai = Json.TryParseFile(Path.Combine(data, "ai.json")),
                };
        }
        _cfgs[id] = res;
        return res;
    }

    internal static MissionSession Session(TestContext t, string cfgId, int mission, string tribe = "", int difficulty = 1, ulong seed = 1, IMissionPresenter? presenter = null, DebugOptions? debug = null)
    {
        var c = LoadCfg(cfgId);
        if (c == null) t.Skip($"configuration {cfgId} or its data not found");
        var start = new GameStart { Configuration = cfgId, Mission = mission, Tribe = tribe, Difficulty = difficulty, Seed = seed, Debug = debug };
        if (MissionSession.MissionFolderOf(start, c!.Config) == null) t.Skip($"mission {mission} not in {cfgId}");
        var e = MissionEntry.LoadIndex(c.Config.MissionsDir).First(x => x.Id == mission);
        if (WorldBuilder.FindMap(c.Config, e.Map) == null) t.Skip($"map {e.Map} not staged");
        return MissionSession.Create(start, c.Config, new SessionOptions
        {
            Presenter = presenter, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data,
        });
    }

    static double Minutes => double.TryParse(Environment.GetEnvironmentVariable("PW_CAMPAIGN_MINUTES"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 5;

    // ------------------------------------------------------------------ mission 11, the Arena
    public static void Mission11Setup(TestContext t)
    {
        var P = new RecordingPresenter();
        // the heroes stand still (nobody plays them) and fight back on their own; against the waves of a real fight they
        // would die within two minutes and the mission end ("Xa: GameOver"): the debug option "your defence" keeps them
        // alive (the JS playthrough tests/campaign_play_11.js heals them every second)
        var s = Session(t, "base", 11, presenter: P, debug: new DebugOptions { On = true, Me = new DebugSide { Def = 100 } });
        var C = s.Campaign; var W = s.World;
        // players: 0 human Ninigi, 1-3 the governor's troops, all hostile to the player
        t.Equal(4, W.Players.Count, "players");
        t.Equal("Ninigi", C.Human!.Tribe, "human tribe");
        t.True(!C.Human.Ai && W.Players.Where(p => p != C.Human).All(p => p.Ai), "computer players");
        for (int i = 1; i <= 3; i++) { t.Equal(0, C.Human.Relation(C.Players[i]), $"0 hostile to {i}"); t.Equal(0, C.Players[i]!.Relation(C.Human), $"{i} hostile to 0"); }
        t.Equal(2, C.Players[1]!.Relation(C.Players[2]), "1 friendly to 2");
        // the start army of the point-buy preset: Cole, Stina (her sabre-tooth) and Bela at the start location
        var mine = W.Units.Where(u => u.Owner == C.Human).Select(u => u.Name).OrderBy(x => x).ToList();
        t.Log("start army: " + string.Join(", ", mine));
        t.True(mine.Contains("Cole_s0") && mine.Contains("Bela_s0") && mine.Contains("special_eusmilus"), "Cole, Bela, Stina's sabre-tooth");
        var sl = C.StartLocations[0]!;
        foreach (var u in W.Units.Where(u => u.Owner == C.Human)) t.True(Math.Sqrt((u.Pos.X - sl.X) * (u.Pos.X - sl.X) + (u.Pos.Z - sl.Z) * (u.Pos.Z - sl.Z)) < 15, $"{u.Name} at the start location");
        // -1 resources: the tribe's default stock (a guess of the JS); iron 0
        t.True(C.Human["food"] > 0, "default stock");
        // the tech filter of the level
        t.True(W.HasFilter(C.Human, "/Filters/Ninigi/Upgrades/no_entrench/no_entrench"), "level filter on");
        // groups, regions, registry
        t.Equal(s.Data.Groups.Count, C.Groups.Count, "groups");
        t.Equal(5, C.Regions.List.Count, "regions");
        t.True(C.Regions.Find("Region_7572") != null, "region by name");
        t.Log($"records {C.Objects.All.Count}; counts: units {C.Counts.Units} buildings {C.Counts.Buildings} props {C.Counts.Props} landscape {C.Counts.Landscape} helpers {C.Counts.Helpers} missing {C.Counts.Missing}; warnings: {string.Join(" | ", C.Warnings.Take(8))}");
        t.True(C.Objects.All.Count > 30, "records");
        // every placed record of the data with an object is where the data put it (map coordinates)
        int checkedN = 0;
        foreach (var pl in C.Plans.Where(p => p.Kind == "prop" && p.Rec?.Prop != null))
        {
            var (mx, my) = C.ToMap(pl.Rec!.Prop!.Pos.X, pl.Rec.Prop.Pos.Z);
            t.Near(pl.O["x"].Num(), mx, 1e-6, pl.Rec.Name + " x"); t.Near(pl.O["y"].Num(), my, 1e-6, pl.Rec.Name + " y");
            checkedN++;
        }
        t.True(checkedN > 0, "props placed");
        // the object query (triggers.md §2.1)
        var H = s.Host;
        var p0 = new TriggerParams(new Dictionary<string, string> { ["obj_owner"] = "0", ["obj_type"] = "CHTR|ANML" });
        t.Equal(W.Units.Count(u => u.Owner == C.Human && u.Alive && !u.Parked), H.Select(p0, "").Count, "query owner 0");
        var cole = H.FindObject(C.Objects.All.First(r => r.Cls == "Cole_s0").Name);
        t.True(cole > 0 && H.Info(cole)!.IsUnit && H.Info(cole)!.Owner == 0, "Cole by name");
        // step 5 game minutes
        s.Run(Minutes * 60);
        var E = s.Engine!;
        t.Log($"{E.StatFired} firings, {E.StatActions} actions, {E.Errors.Count} errors, {E.Warnings.Count} warnings; last: {E.DebugPrint(6).Replace("\n", " / ")}");
        foreach (var w in E.Warnings.Take(10)) t.Log("warning: " + w);
        foreach (var (tr, ty, m) in E.Errors.Take(10)) t.Log($"error: {tr} {ty} {m}");
        foreach (var f in E.FiringLog.Take(12)) t.Log($"  {f.Time,6:0.0} {f.Trigger}: {string.Join(' ', f.Actions)}");
        t.Equal(0, E.Errors.Count, "trigger errors");
        // the arena's start: the first sequence (MS_4010), the healing wells' map pings, the first quest, the first wave
        // on the level timer and the following waves every ~60 s (Gf01 .. Gf06)
        foreach (var name in new[] { "Sa: MS_4010", "Ic:HealingWells", "La01: Set Visible", "Gf01: Spawn Pola", "Gf02: Spawn Gallimimus", "Gf03: Spawn Baryonyx" })
            t.True(E.FiringLog.Any(f => f.Trigger == name), name + " fired");
        t.True(P.Quests.Any(q => q.Change == "shown"), "a quest shown");
        t.True(P.Calls.Any(c => c.StartsWith("marker")), "map pings");
        // the waves are the governor's animals (SPGR out of the arena's gates)
        var spawned = W.Units.Where(u => u.Alive && u.Owner != null && u.Owner != C.Human).ToList();
        t.Log($"alive: {string.Join(", ", spawned.GroupBy(u => u.Name).Select(g => g.Key + " x" + g.Count()))}; killed by the heroes: {C.Human.Kills}");
        t.True(spawned.Count + C.Human.Kills >= 10, "waves spawned (alive + killed by the heroes)");
        t.True(P.Sequences.Count > 0, "a sequence was asked for");
    }

    // ------------------------------------------------------------------ missions 1, 2, 8 and every other one
    static void RunMission(TestContext t, string cfg, int id, string tribe = "", double? minutes = null)
    {
        var s = Session(t, cfg, id, tribe);
        var C = s.Campaign; var E = s.Engine!;
        var human = C.Human!;
        t.True(human != null, "human player");
        // what the data gives the player at the start (the tutorial: nothing - its triggers bring Cole in later) is there
        var pd = human.Data!;
        bool given = (pd["start_army"]?.AsArray().Count ?? 0) > 0 || pd["include_buildings"].Bool() || s.Data.Objects.Any(o => o?["owner"] is System.Text.Json.Nodes.JsonValue ov && ov.GetValue<double>() == human.Id && o["type"].Str() is "CHTR" or "ANML" or "VHCL" or "SHIP" or "BLDG");
        if (given) t.True(s.World.Units.Any(u => u.Owner == human) || s.World.Buildings.Any(b => b.Owner == human) || human.FreeMains.Count > 0, "the player has what the data gives");
        s.Run((minutes ?? Minutes) * 60);
        var live = E.Triggers.Where(x => x.Live).ToList();
        t.Log($"{cfg} mission {id}{(tribe.Length > 0 ? " as " + tribe : "")}: {s.World.Players.Count} players, {C.Objects.All.Count} records, units {s.World.Units.Count} buildings {s.World.Buildings.Count}, " +
              $"{E.StatFired} firings, {E.Errors.Count} errors, {E.Warnings.Count} warnings, {live.Count} live; ended {(s.Over ? (s.Won ? "won" : "lost") : "no")}; missing: {C.Counts.Missing}");
        // what the triggers still listening wait for (their condition types): DEAD / DYIN / ISFG / PLDE / OBJP wait on combat,
        // REGN on units walking somewhere, BLDG / TECH / PLYR on construction, production and economy (docs/CAMPAIGN.md)
        t.Log("  waiting on: " + string.Join(" ", live.SelectMany(x => x.Conds.Select(c => c.Type)).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}")));
        foreach (var (tr, ty, m) in E.Errors.Take(5)) t.Log($"  error: {tr} {ty} {m}");
        foreach (var w in E.Warnings.Take(5)) t.Log($"  warning: {w}");
        t.Equal(0, E.Errors.Count, $"{cfg} {id}: trigger errors");
        t.True(E.StatFired > 0, $"{cfg} {id}: triggers fired");
    }
    public static void Mission01(TestContext t) => RunMission(t, "base", 1);
    public static void Mission02(TestContext t) => RunMission(t, "base", 2);
    public static void Mission08(TestContext t) => RunMission(t, "base", 8);

    public static void AllBase(TestContext t) => All(t, "base");
    public static void AllMirage(TestContext t) => All(t, "mirage");
    static void All(TestContext t, string cfg)
    {
        var c = LoadCfg(cfg);
        if (c == null) t.Skip($"configuration {cfg} not found");
        int n = 0;
        var failed = new List<string>();
        // PW_CAMPAIGN_MISSIONS=41,42,43: only those missions
        var only = (Environment.GetEnvironmentVariable("PW_CAMPAIGN_MISSIONS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        foreach (var e in MissionEntry.LoadIndex(c!.Config.MissionsDir))
        {
            if (only.Count > 0 && !only.Contains(e.Id)) continue;
            if (WorldBuilder.FindMap(c.Config, e.Map) == null) { t.Log($"{cfg} {e.Id}: map {e.Map} not staged - skipped"); continue; }
            try { RunMission(t, cfg, e.Id); n++; }
            catch (Exception x) { failed.Add($"{e.Id}: {x.Message}"); t.Log($"FAILED {cfg} {e.Id}: {x}"); }
        }
        t.True(failed.Count == 0, "missions failed: " + string.Join("; ", failed));
        t.True(n > 0, "missions run");
    }

    // ------------------------------------------------------------------ the village guard on open ground
    /// <summary>
    /// Player report (MIRAGE mission 3 as the SEAS, debug options): "units just clump here" - 20-30 Amazons (player 1,
    /// the Turtle main base: its idle army, the village guard) packed against the slaughterhouse at the foot of the
    /// cliff. The guard point, 30 m from the main building (the mobile centre) towards the enemy, lay in the cliff;
    /// every guard walked to the nearest reachable corner of it. Now the guard point is open ground of the village's
    /// own region (TribeAI.OpenGround).
    /// </summary>
    public static void Mission03GuardOnOpenGround(TestContext t)
    {
        var s = Session(t, "mirage", 3, "SEAS", debug: new DebugOptions { On = true, Free = true, Reveal = true, Unlock = true, NoHousing = true, Pyramid = new int?[] { null, null, null, 6, 6 } });
        var W = s.World; var nav = W.Nav;
        var ai = W.Brains.Get(1) as PwRemake.Sim.Ai.TribeAI;
        if (ai == null) t.Skip("no computer player 1");
        s.Run(180);
        var S = ai!.Census(true);
        t.True(S.Home != null, "player 1 has a home");
        var raw = ai.GuardPointRaw(S); var g = ai.GuardPoint(S); var h = S.Home!.Value;
        nav.UpdateClearance();
        int k = nav.Idx(g.X, g.Z);
        int hk = nav.Idx(h.X, h.Z), hr = nav.Region![hk] >= 0 && nav.Block[hk] == 0 ? nav.Region[hk] : nav.Region[nav.NearestFree(hk, 20)];
        t.Log($"home ({h.X:0.0}, {h.Z:0.0}), guard point as the JS ({raw.X:0.0}, {raw.Z:0.0}) block {nav.Block[nav.Idx(raw.X, raw.Z)]}, now ({g.X:0.0}, {g.Z:0.0}) clearance {nav.Clear[k]}");
        t.True(nav.Block[k] == 0 && nav.Clear[k] >= 3, "the guard point is open ground");
        t.Equal(hr, nav.Region[k], "the guard point is in the village's ground region");
        foreach (var b in W.Buildings.Where(b => b.Alive && b.Contains(g.X, g.Z, 6))) t.Log($"  near: {b.Name} at ({b.Pos.X:0.0}, {b.Pos.Z:0.0}) surface {b.SurfDist(g.X, g.Z):0.0} m, blocks {nav.Block[nav.Idx(b.Pos.X, b.Pos.Z)]}");
        t.True(!W.Buildings.Any(b => b.Alive && b.Contains(g.X, g.Z, 2)), "the guard point is not in or at a building");
        // the guards stand around it, not packed against a building
        var guards = S.Pool.Where(u => !u.Inside && Math.Sqrt((u.Pos.X - g.X) * (u.Pos.X - g.X) + (u.Pos.Z - g.Z) * (u.Pos.Z - g.Z)) < 30).ToList();
        t.Log($"{S.Pool.Count} pool units, {guards.Count} at the guard point");
        t.True(guards.Count >= 10, "the village guard stands at the guard point");
        var touching = guards.Where(u => W.Buildings.Any(b => b.Alive && b.Contains(u.Pos.X, u.Pos.Z, u.Radius + 0.5))).ToList();
        t.True(touching.Count <= guards.Count / 5, $"few guards against a building ({touching.Count} of {guards.Count})");
    }

    // ------------------------------------------------------------------ tribe swap
    public static void SwapArenaAsSeas(TestContext t)
    {
        var s = Session(t, "mirage", 11, "SEAS");
        var C = s.Campaign;
        t.True(C.Swap != null, "a swap");
        t.Equal("SEAS", C.Human!.Tribe, "human tribe");
        t.True(C.Human.ForeignActions, "foreign actions");
        // heroes stay heroes; nothing of the Dragon Clan is the player's
        foreach (var u in s.World.Units.Where(u => u.Owner == C.Human)) t.True(!u.Name.StartsWith("ninigi_"), u.Name + " is no Ninigi class");
        // the level's filter of the Ninigi (no_entrench: a tribe-specific upgrade) is dropped for the SEAS
        t.True(!s.World.HasFilter(C.Human, "/Filters/Ninigi/Upgrades/no_entrench/no_entrench"), "Ninigi upgrade filter dropped");
        s.Run(Math.Min(Minutes, 2) * 60);
        t.Equal(0, s.Engine!.Errors.Count, "trigger errors");
        t.True(s.Engine.StatFired > 0, "triggers fired");
    }

    public static void SwapMission01AsDustriders(TestContext t)
    {
        var s = Session(t, "base", 1, "Aje");
        var C = s.Campaign;
        t.Equal("Aje", C.Human!.Tribe, "human tribe");
        var S = C.Swap!;
        // the role table: the Norse worker is the Dustriders' worker, the fireplace their resource collector
        t.Equal("aje_worker", S.Cls("hu_worker"), "worker");
        t.Equal("aje_resource_collector", S.Cls("hu_fireplace"), "main building");
        t.Equal("Cole_s0", S.Cls("Cole_s0"), "heroes stay");
        t.Equal("/Filters/AntiActions/Aje/Build/CHTR/aje_warrior", S.Filter("/Filters/AntiActions/Hu/Build/CHTR/hu_warrior"), "filter translated");
        t.True(S.Filter("/Filters/AntiActions/Ninigi/Build/CHTR/ninigi_warrior") == "/Filters/AntiActions/Ninigi/Build/CHTR/ninigi_warrior", "another tribe's filter as it is");
        // a condition that counts the Norsemen's workers counts the Dustriders' (Registry.ClassIs)
        var own = s.World.Units.Where(u => u.Owner == C.Human && u.Name.StartsWith("hu_")).Select(u => u.Name).ToList();
        t.True(own.Count == 0, "no Norse units of the player: " + string.Join(",", own));
        var w = C.Objects.All.FirstOrDefault(r => r.Slot == 0 && r.Lc == "aje_worker");
        if (w != null) t.True(C.Objects.ClassIs(w, "hu_worker"), "aje_worker stands for hu_worker");
        foreach (var e in C.Human.FreeMains) t.True(e.Parked, "free main waits");
        s.Run(Math.Min(Minutes, 2) * 60);
        t.Equal(0, s.Engine!.Errors.Count, "trigger errors");
        t.True(s.Engine.StatFired > 0, "triggers fired");
    }

    // ------------------------------------------------------------------ determinism
    /// <summary>a mission's computer players are the AI's brains (CampaignBrains): one per ai_ slot, asleep until the
    /// triggers wake them (AIBV), Nightmare gives them the remake's squads, "passive computer" keeps their own attacks
    /// off the human</summary>
    public static void CampaignBrainsAreTheComputerPlayer(TestContext t)
    {
        var s = Session(t, "base", 3);
        var W = s.World;
        var ai = W.Players.Where(p => p != s.Human && p.Data?["control"].Str().StartsWith("ai_") == true).ToList();
        t.True(ai.Count > 0 && W.Ai != null, "computer slots and the world's AiPlayers");
        t.Equal(ai.Count, W.Brains.Count, "one brain per ai_ slot");
        t.True(W.Brains.Values.All(b => b is PwRemake.Sim.Ai.TribeAI tb && tb.Owner == W.Ai), "the brains are TribeAI of World.Ai");
        var before = W.Brains.Values.OfType<PwRemake.Sim.Ai.TribeAI>().Select(b => b.Behaviour).ToList();
        s.Run(30);
        var after = W.Brains.Values.OfType<PwRemake.Sim.Ai.TribeAI>().Select(b => b.Behaviour).ToList();
        t.Log("behaviours: " + string.Join(" ", before) + " -> " + string.Join(" ", after));
        t.True(!before.SequenceEqual(after), "AIBV of the mission's triggers woke a brain");
        t.True(s.Engine!.Warnings.All(w => !w.Contains("no brain")), "no AI action without a brain");
        var nm = Session(t, "base", 3, difficulty: 3);
        t.True(nm.World.Brains.Values.OfType<PwRemake.Sim.Ai.TribeAI>().All(b => b.ExtraSquads), "Nightmare: the remake's squads");
        t.True(s.World.Brains.Values.OfType<PwRemake.Sim.Ai.TribeAI>().All(b => !b.ExtraSquads), "normal: the original's raids");
        var pa = Session(t, "base", 3, debug: new DebugOptions { On = true, Ai = "passive" });
        var b0 = pa.World.Brains.Values.OfType<PwRemake.Sim.Ai.TribeAI>().First();
        t.Equal("passive", pa.World.Cheats?.Ai, "the debug option reaches the world");
        t.True(b0.RequestAttack(b0.Census(true), "pyramid", pa.Human) == null, "passive computer: no attack of its own on the human");
        var none = MissionSession.Create(new GameStart { Configuration = "base", Mission = 3 }, LoadCfg("base")!.Config, new SessionOptions { NoBrains = true, Rules = LoadCfg("base")!.Rules, Models = LoadCfg("base")!.Models, AiData = LoadCfg("base")!.Ai, DataDir = LoadCfg("base")!.Data });
        t.Equal(0, none.World.Brains.Count, "NoBrains: none");
    }

    public static void Determinism(TestContext t)
    {
        var a = Session(t, "base", 11, seed: 7);
        var b = Session(t, "base", 11, seed: 7);
        t.Equal(a.StateHash(), b.StateHash(), "hash after the setup");
        for (int i = 0; i < 4; i++)
        {
            a.Run(30); b.Run(30);
            t.Equal(a.StateHash(), b.StateHash(), $"hash after {(i + 1) * 30} s");
        }
        t.Equal(a.Engine!.FiringLog.Count, b.Engine!.FiringLog.Count, "same firings");
    }

    // ------------------------------------------------------------------ debug options (cheats.js applyCheats)
    public static void DebugOptionsApplied(TestContext t)
    {
        var dbg = new DebugOptions { On = true, Free = true, Reveal = true, Unlock = true, PopMax = 120, Res = new DebugResources { Food = 5000, Skulls = 77 }, Pyramid = new int?[] { 30, null, null, null, 2 } };
        var s = Session(t, "base", 1, debug: dbg);
        var me = s.Campaign.Human!;
        t.True(me.Debug, "free");
        t.True(me.UnlockAll, "no mission bans");
        t.Equal(5000.0, me["food"], "food");
        t.Equal(77.0, me["skulls"], "skulls");
        t.Equal(120.0, me.PopMax, "unit limit");
        t.Equal(30.0, me.Pyramid![0], "pyramid level 1");
        t.Equal(2.0, me.Pyramid[4], "pyramid level 5");
        t.True(s.World.Cheats != null && s.World.Cheats.Reveal, "reveal");
        // the level's bans did not reach the player
        t.True(!s.World.Players.Where(p => p == me).Any(p => p.ScriptFilters.ToList().Any(f => f.StartsWith("script:AntiActions/"))), "no AntiActions on the player");
    }

    // ------------------------------------------------------------------ registry and regions semantics
    /// <summary>
    /// BoosterPack 1 mission 4-2 (single_b42): the rune gates close the side chambers of the ice caves. Their door box is
    /// 1.4 m deep; turned 45 degrees it covered too few 2 m cells (centre test) and left gaps, so every chamber was
    /// reachable with every gate closed. Gates now take every cell their boxes touch (Nav.RectCells cover). Sweep of all
    /// missions (docs/CAMPAIGN.md section 8): missions 1, 4, 16, 41/42 had such leaks.
    /// </summary>
    public static void RuneGatesClosePassages(TestContext t)
    {
        var s = Session(t, "base", 42);
        var W = s.World; var C = s.Campaign; var nav = W.Nav;
        var start = W.Units.First(u => u.Owner == s.Human && u.Name == "livingstone_s0");
        bool Reach(string region)
        {
            var seen = new bool[nav.N * nav.N]; var st = new Stack<int>();
            int k0 = nav.NearestFree(nav.Idx(start.Pos.X, start.Pos.Z)); st.Push(k0); seen[k0] = true;
            while (st.Count > 0)
            {
                int c = st.Pop(), ci = c % nav.N, cj = c / nav.N;
                for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
                {
                    int i = ci + di, j = cj + dj; if (i < 0 || j < 0 || i >= nav.N || j >= nav.N) continue;
                    int k = j * nav.N + i; if (seen[k] || nav.Block[k] != 0 || nav.GateBlocks(k, s.Human)) continue;
                    seen[k] = true; st.Push(k);
                }
            }
            var r = C.Regions.Find(region)!;
            for (int k = 0; k < seen.Length; k++) if (seen[k]) { var (x, z) = nav.Center(k); if (r.Contains(x, z)) return true; }
            return false;
        }
        t.True(Reach("rune01"), "rune01 lies on the open way");
        t.True(!Reach("rune02"), "rune02 lies behind the closed gate01");
        t.True(!Reach("rune07"), "rune07 lies behind the closed gate08");
        W.SetGate((Building)C.Objects.ByName("gate01")!.Entity!, 0);
        t.True(Reach("rune02"), "gate01 open: rune02 reachable");
    }

    /// <summary>A map object is made by its class, not by the type the map stored: MIRAGE moved seas_carrier_fake to the
    /// ships and its maps kept the old type BLDG. Sweep: in both configurations those carriers (MIRAGE 12's fleet carrier,
    /// MIRAGE 13's four SEAS carriers) are the only objects whose stored type (unit or building) is not their class's.</summary>
    public static void MapTypeFollowsClass(TestContext t)
    {
        var FIGHT = new HashSet<string> { "CHTR", "ANML", "VHCL", "SHIP", "BLDG" };
        var odd = new List<string>();
        foreach (var cfgId in new[] { "base", "mirage" })
        {
            var c = LoadCfg(cfgId);
            if (c == null) continue;
            foreach (var dir in Directory.GetDirectories(c.Config.MissionsDir).OrderBy(d => d))
            {
                var wf = Path.Combine(dir, "world.json");
                if (!File.Exists(wf)) continue;
                foreach (var o in Json.ParseFile(wf)!["objects"]!.AsArray())
                {
                    var type = o!["type"]!.GetValue<string>(); var ix = c.Rules.Info(o["class"]!.GetValue<string>());
                    if (ix != null && ix.Type != type && FIGHT.Contains(type) && FIGHT.Contains(ix.Type)) odd.Add($"{cfgId} {Path.GetFileName(dir)} {o["name"]} {type}->{ix.Type}");
                }
            }
        }
        t.Log(string.Join("\n    ", odd));
        t.True(odd.All(x => x.StartsWith("mirage 12_") || x.StartsWith("mirage 13_")) && odd.All(x => x.Contains("seas_carrier_fake")), "only MIRAGE's carriers are stored with another type");
        var s = Session(t, "mirage", 12);
        var rec = s.Campaign.Objects.ByName("seas_carrier_fake_0");
        t.True(rec != null && rec.Entity is PwRemake.Sim.World.Unit && rec.Type == "SHIP", "mission 12's fleet carrier is a ship " + rec);
        t.True(rec!.Entity!.Parked && !rec.Visible, "hidden until Gc00 shows the sequence squad");
        t.True(rec.Entity.MaxHp > 0, "it can be sunk");
    }

    public static void RegistryAndRegions(TestContext t)
    {
        var s = Session(t, "base", 11);
        var C = s.Campaign; var R = C.Objects;
        // a group = its members; a member in at most one group; Drop takes it out of its group
        var g = C.Groups.FirstOrDefault(x => x.Size > 0);
        if (g != null)
        {
            var m = g.List()[0];
            t.True(R.Select(new TriggerParams(new Dictionary<string, string> { ["obj_name"] = g.Name }), "").Contains(m), "group query = members");
            t.True(!R.Matches(m, new TriggerParams(new Dictionary<string, string> { ["obj_name"] = g.Name }), ""), "Matches: a group does not match its members");
        }
        // run-time names <class>_<n>
        var rec = C.Spawn("hu_warrior", 1, 260, 240);
        t.True(rec != null && rec.Entity != null && rec.Name.StartsWith("hu_warrior_"), "spawn + name " + rec?.Name);
        var g2 = C.Groups[0];
        t.True(R.AddToGroup(g2, rec), "add to group");
        t.Equal(g2, rec!.Group, "its group");
        C.Remove(rec);
        t.True(!rec.Alive && rec.Group == null && R.ByName(rec.Name) == null, "removed: out of tables and group");
        // regions: game <-> map, shapes on / off, move
        var r = C.Regions.List[0];
        var c0 = r.Center()!.Value;
        t.True(r.Contains(c0.X, c0.Z), "centre inside");
        r.SetEnabled(false);
        t.True(!r.Contains(c0.X, c0.Z), "disabled");
        r.SetEnabled(true);
        r.MoveTo(c0.X + 10, c0.Z);
        t.Near(c0.X + 10, r.Center()!.Value.X, 1e-9, "moved");
        t.True(C.Regions.Get("UniqueWorldRegion").World && C.Regions.Get("nonexistent").World, "the world region");
        // map coordinates round trip
        var (mx, my) = C.ToMap(12.5, -7.25); var (gx, gz) = C.ToGame(mx, my);
        t.Near(12.5, gx, 1e-12, "x"); t.Near(-7.25, gz, 1e-12, "z");
    }
}
