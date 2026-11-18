// A mission of the remake's own campaigns as the game runs it, without the engine (docs/SEQUEL.md): GameStart (Mode
// Sequel: a Containment mission; Mode Scenario: a scenario with the tribe played) + the game configuration -> the
// rules (Containment: with the sequel's classes written into the tech tree, those of later missions locked), the map
// made from the mission's recipe (SequelMap), the world with its players and start (SequelRuntime.Build), the
// mission's script (SequelLua), the computer players, the debug options. Step() runs one simulation step (50 ms).
//
// Ported from remake/src/main.js (startGame for cfg.sequel / cfg.scenario, createBrains for G.sequel) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Sequel;

public sealed class SequelSession : IGameSim
{
    static readonly Logger L = Log.For("Sequel.Session");
    public readonly GameStart Start;
    public readonly SequelEntry Entry;
    public SequelRuntime Runtime = null!;
    public SequelLua Lua = null!;
    public MapSource Map { get; private set; } = null!;
    public List<MapRoad> Roads = new();
    public BuiltWorld Built { get; private set; } = null!;
    public GameWorld World => Built.World;
    public Player? Human { get; private set; }
    public (double X, double Z, double Yaw) StartCamera { get; private set; }
    public string Title => Entry.Title;
    public readonly List<string> Problems = new();
    IReadOnlyList<string> IGameSim.Problems => Problems;
    public bool Over => Runtime.Ended != null;
    public bool Won => Runtime.Ended?.Won == true;
    public string Kind => Entry.Kind;

    SequelSession(GameStart s, SequelEntry e) { Start = s; Entry = e; }

    /// <summary>the kind of a start: "sequel" | "scenario" | null (not one of these campaigns)</summary>
    public static string? KindOf(GameStart s) => s.Mode == GameMode.Sequel ? "sequel" : s.Mode == GameMode.Scenario ? "scenario" : null;

    /// <summary>the rules of a Containment mission: the configuration's tech tree and game data with the sequel's classes
    /// (those of later missions without build actions, unless the debug option "no mission bans")</summary>
    public static Rules.Rules SequelRules(string dataDir, int missionId, bool unlockAll, SequelClasses? classes = null)
    {
        var tt = (JsonObject)Json.ParseFile(Path.Combine(dataDir, "techtree.json"))!;
        var gd = (JsonObject)(Json.ParseFile(Path.Combine(dataDir, "gamedata.json")) ?? new JsonObject());
        (classes ?? SequelCatalog.Classes()).Apply(tt, gd, unlockAll ? new HashSet<string>() : SequelCatalog.LockedBefore(missionId));
        return new Rules.Rules(TtNode.FromJson(tt), new GameData(gd));
    }

    /// <summary>sets the mission up; throws for what makes it impossible (no such mission, no game data)</summary>
    /// <param name="rules">already made rules (tests; a scenario may use the configuration's own); null: made here</param>
    public static SequelSession Create(GameStart start, GameConfiguration cfg, Rules.Rules? rules = null, ModelManifest? models = null, SimFlags? flags = null,
        Action<double, string>? progress = null, bool noBrains = false, string? dataDir = null)
    {
        void Step(double k, string what) { try { progress?.Invoke(k, what); } catch (Exception) { } }
        var kind = KindOf(start) ?? throw new InvalidOperationException($"not a mission of the remake's campaigns: {start.Mode}");
        var entry = SequelCatalog.Find(kind, start.Mission) ?? throw new InvalidOperationException($"There is no {(kind == "scenario" ? "scenario" : "Containment mission")} {start.Mission} in {SequelCatalog.Dir(kind)}.");
        var s = new SequelSession(start, entry);
        dataDir ??= cfg.Data;
        string tribe = kind == "scenario" ? (SequelCatalog.SCENARIO_TRIBES.Contains(start.Tribe) ? start.Tribe : SequelCatalog.SCENARIO_TRIBES[0]) : "SEAS";
        var def = SequelCatalog.Definition(entry, tribe, start.Difficulty);
        Step(0, "Reading the rules");
        var classes = SequelCatalog.Classes();
        var cheats = start.Debug?.ForGame();
        if (kind == "sequel" && !string.Equals(cfg.Mod, SequelCatalog.SEQUEL_MOD, StringComparison.OrdinalIgnoreCase))
            s.Problems.Add($"Containment is played with the {SequelCatalog.SEQUEL_MOD} configuration (this is {cfg.Title})");
        rules = kind == "sequel" ? SequelRules(dataDir, entry.Id, cheats?.Unlock == true, classes) : rules ?? Rules.Rules.Load(dataDir);
        models ??= ModelManifest.Load(dataDir);
        var brushes = GroundBrushes.LoadFile(Path.Combine(dataDir, "assets", "terrain", "objbrush.json"));
        var aiData = Json.TryParseFile(Path.Combine(dataDir, "ai.json"));
        Step(0.3, "Making the map");
        s.Map = SequelMap.Source(SequelRuntime.MapRecipe(def), new RulesMapAdapter(rules), models.Names.ToList(), out s.Roads);
        Step(0.5, "Building the world");
        SequelRuntime? R = null;
        var wopts = new WorldOptions { Seed = start.Seed, Warpgate = false, Multiplayer = false };
        s.Built = WorldBuilder.Build(s.Map, rules, models, wopts, null, brushes, W =>
        {
            R = new SequelRuntime(W, classes, kind, entry.Id, Math.Clamp(start.Difficulty, 0, 2), tribe, def);
            R.TakePlantSpots(s.Map);
            if (kind == "sequel") R.Locked = cheats?.Unlock == true ? new HashSet<string>() : SequelCatalog.LockedBefore(entry.Id);
            s.Human = R.Build(rules);
        });
        var W = s.World;
        var rt = s.Runtime = R!;
        // the sequel's own rules in the world's step (World.Campaign) and its hooks
        W.Campaign = rt;
        W.Hooks.Damage = rt.DamageHook;
        W.Hooks.Lethal = rt.LethalHook;
        W.Hooks.Power = rt.Grid.Credit;
        W.NightFn = () => rt.NightOn;
        W.ExtraViewers = rt.Viewers;
        Step(0.85, "Starting the mission");
        s.Lua = SequelLua.Load(entry.Folder, rt);
        if (s.Lua.Errors.Count > 0) s.Problems.Add("the mission's Lua file has errors (see the log)");
        rt.RunSetup();
        // the debug options chosen before the start, then the computer players
        Cheats.Apply(W, cheats, s.Human);
        if (!noBrains) s.CreateBrains(aiData, cfg);
        flags?.Apply(W, s.Human);
        if (rt.NightOn) W.SightScale = 0.6;
        var (cx, cz) = rt.Camera();
        s.StartCamera = (cx, cz, 0);
        L.Info($"{kind} {entry.Id} {entry.Title} ({tribe}, difficulty {start.Difficulty}): {W.Units.Count} units, {W.Buildings.Count} buildings; players {string.Join(", ", rt.Roles.Select(kv => $"{kv.Key}={kv.Value.Id} {kv.Value.Tribe}"))}");
        Step(1, "Ready");
        return s;
    }

    /// <summary>runtime.js brains(): the computer players' brains (Crane, the Dustriders, a scenario's tribes) - asleep
    /// (Mikrobe) until the mission wakes them, unless the definition gives a behaviour; ai false: only the script moves it</summary>
    void CreateBrains(JsonNode? aiJson, GameConfiguration cfg)
    {
        var rt = Runtime; var W = World;
        AiData? data = null;
        AiPlayers? A = null;
        foreach (var r in SequelRuntime.EXTRA_ROLES)
        {
            var p = rt.PlayerOf(r);
            var o = rt.Def[r];
            if (p == null || o == null || (o is JsonObject oo && oo["ai"] is JsonValue av && av.GetValueKind() == System.Text.Json.JsonValueKind.False)) continue;
            data ??= MissionSession.AiDataOf(aiJson, cfg);
            if (A == null)
            {
                A = new AiPlayers(W, data) { Human = Human?.Id ?? 0 };
                A.Resolve = id => W.ById(id);
                A.Attach();
            }
            int diff = o["difficulty"] is JsonValue dv ? (int)dv.Num() : new[] { 2, 5, 8 }[rt.Difficulty];
            var b = new TribeAI(W, p, data, new TribeAIOptions { Difficulty = diff, Behaviour = o["behaviour"] is JsonValue bv ? bv.Str() : "Mikrobe", MapOptions = AiMapOptions.From(null), Multimap = false }, null);
            A.Add(b);
            rt.Brains[r] = b;
            W.Brains.Set(p.Id, b);
        }
    }

    public void Step() => World.Step();
    public void Run(double seconds) { int n = (int)Math.Round(seconds / GameWorld.STEP); for (int i = 0; i < n; i++) World.Step(); }
    /// <summary>the world's state hash plus the mission's</summary>
    public ulong StateHash()
    {
        ulong h = World.StateHash();
        h ^= Runtime.StateHash(); h *= 1099511628211UL;
        return h;
    }
}
