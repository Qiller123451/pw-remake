// A campaign mission as the game runs it, without the engine: GameStart (which mission, tribe, difficulty, debug
// options, seed) + the game configuration -> the mission folder (LuaMission: mission.json, world.json, triggers.lua,
// custom.lua), the map (.ula of the installation), the world with its landscape, players and objects (Campaign,
// WorldBuilder), the computer players (IBrainFactory), the debug options (Cheats) and the trigger engine (TriggerEngine
// on MissionHost). Step() runs one simulation step (50 ms): the world, the computer players, the campaign and its
// triggers.
//
// The order is main.js startGame for a campaign: loadCampaign -> new Campaign(...).plan -> mapSource (the landscape
// without what the campaign places) -> buildWorld (players, objects, animals, nests, wildlifeReady, applyCheats,
// startWeather, createBrains) -> the trigger engine (it starts with the first step).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Campaign;

/// <summary>What a session is made with besides GameStart and the configuration (all optional).</summary>
public sealed class SessionOptions
{
    /// <summary>the mission UI (null: NullPresenter - sequences and dialogues end at once)</summary>
    public IMissionPresenter? Presenter;
    /// <summary>makes the computer players' brains; null: the computer player of src/Sim/Ai (Ai.CampaignBrains on the
    /// configuration's ai.json)</summary>
    public IBrainFactory? Brains;
    /// <summary>no computer players at all (tests of the campaign layer alone; the AI trigger actions warn "no brain")</summary>
    public bool NoBrains;
    /// <summary>already loaded data of the configuration (tests share them; null: loaded from Config.Data)</summary>
    public Rules.Rules? Rules;
    public ModelManifest? Models;
    public ForestData? Forest;
    public GroundBrushes? Brushes;
    public JsonNode? AiData;
    /// <summary>the Lua standard library folder (null: game/lua/std found from the program's folder)</summary>
    public string? LuaStd;
    /// <summary>only the world, no trigger engine (the JS ?notriggers)</summary>
    public bool NoTriggers;
    /// <summary>another data folder than the configuration's (tests)</summary>
    public string? DataDir;
    /// <summary>told how far Create is (0..1 of the set-up, what it does now) - the loading screen's bar. Create may
    /// run on a worker thread (GameSession.Load): the callback is called on that thread.</summary>
    public Action<double, string>? Progress;
    /// <summary>how the simulation runs besides the start (the game: SimFlags.Game - the world's own fog of war, events
    /// trimmed per step; a saved game keeps them and its replay uses the same: docs/SAVES.md); null: none (tests)</summary>
    public SimFlags? Flags;
}

public sealed class MissionSession : IGameSim
{
    // ---- IGameSim (GameSession runs a mission or a skirmish through it)
    MapSource IGameSim.Map => Map;
    BuiltWorld IGameSim.Built => Built;
    (double X, double Z, double Yaw) IGameSim.StartCamera => StartCamera;
    IReadOnlyList<string> IGameSim.Problems => Problems;
    public string Title => string.IsNullOrEmpty(Data.Entry.Title) ? Map.Name : Data.Entry.Title;

    static readonly Logger L = Log.For("Campaign.Session");
    public readonly GameStart Start;
    public readonly GameConfiguration Config;
    public MissionData Data = null!;
    public LuaMission Lua = null!;
    public Campaign Campaign = null!;
    public MissionHost Host = null!;
    public TriggerEngine? Engine;
    public MapSource Map = null!;
    public BuiltWorld Built = null!;
    public GameWorld World => Built.World;
    /// <summary>the human player</summary>
    public Player? Human => Campaign.Human;
    /// <summary>where the camera starts: the human's start location (or its first object), looking along the level's
    /// default camera angle (game x, z; yaw = π/2 - default_camera: StartLocation.usl:352-361, a guess of the angle's zero)</summary>
    public (double X, double Z, double Yaw) StartCamera;
    /// <summary>load problems that do not stop the mission (Lua errors, a missing map is fatal)</summary>
    public readonly List<string> Problems = new();

    MissionSession(GameStart start, GameConfiguration cfg) { Start = start; Config = cfg; }

    /// <summary>the mission folder of a GameStart: MissionFolder below the configuration's missions/, else the folder of the
    /// mission id in index.json</summary>
    public static string? MissionFolderOf(GameStart start, GameConfiguration cfg)
    {
        // the remake's own missions (game/missions: RemakeMissions) by their token "remake:<name>"
        if (RemakeMissions.IsToken(start.MissionFolder)) return RemakeMissions.FolderOf(start.MissionFolder);
        if (!string.IsNullOrEmpty(start.MissionFolder))
        {
            var f = Path.IsPathRooted(start.MissionFolder) ? start.MissionFolder : Path.Combine(cfg.MissionsDir, start.MissionFolder);
            if (Directory.Exists(f)) return f;
        }
        var e = MissionEntry.LoadIndex(cfg.MissionsDir).FirstOrDefault(x => x.Id == start.Mission);
        return e != null ? Path.Combine(cfg.MissionsDir, e.Folder) : null;
    }

    /// <summary>sets the mission up; throws for what makes it impossible (no mission folder, no map, no game data)</summary>
    public static MissionSession Create(GameStart start, GameConfiguration cfg, SessionOptions? o = null)
    {
        o ??= new SessionOptions();
        var s = new MissionSession(start, cfg);
        var dataDir = o.DataDir ?? cfg.Data;
        void Step(double k, string what) { try { o.Progress?.Invoke(k, what); } catch (Exception) { } }
        Step(0, "Reading the rules");
        var rules = o.Rules ?? Rules.Rules.Load(dataDir);
        var models = o.Models ?? ModelManifest.Load(dataDir);
        var forest = o.Forest ?? ForestData.LoadFile(Path.Combine(dataDir, "forest.json"));
        var brushes = o.Brushes ?? GroundBrushes.LoadFile(Path.Combine(dataDir, "assets", "terrain", "objbrush.json"));
        var aiData = o.AiData ?? Json.TryParseFile(Path.Combine(dataDir, "ai.json"));
        // ---- the mission folder (loadCampaign)
        Step(0.02, "Reading the mission");
        var folder = MissionFolderOf(start, cfg) ?? throw new InvalidOperationException($"There is no campaign mission {start.Mission} ({start.MissionFolder}) in {cfg.MissionsDir}.");
        s.Lua = LuaMission.Load(folder, o.LuaStd, cfg.Language);
        if (!s.Lua.Ok) s.Problems.Add("the mission's Lua files have errors (see the log)");
        s.Data = MissionData.FromLua(s.Lua, MissionEntry.LoadIndex(cfg.MissionsDir));
        L.Info($"mission {s.Data.Id} {s.Data.Entry.Title}: {s.Data.Triggers.Count} triggers, {s.Data.Objects.Count} objects ({start})");
        Step(0.08, "Planning the mission");
        // ---- the campaign's plan, the map source without what the campaign places itself
        var ccfg = new CampaignConfig { Difficulty = start.Difficulty, Tribe = start.Tribe, Cheats = start.Debug?.ForGame(), AiData = aiData, Army = start.Army, Humans = start.Seats != null ? start.SeatPlayers() : null, Gameplay = start.Gameplay, Color = start.Color };
        // a co-op campaign game (mode 2, docs/COOP.md): the seats are players of their own, no ally is "taken over"
        if (start.Coop != null)
        {
            ccfg.Coop = new Coop.CoopRuntime(start.Coop);
            ccfg.Humans = null;
            if (start.Coop.SeatOf(1) is { Tribe.Length: > 0 } s1) ccfg.Tribe = s1.Tribe;
            // (seat 1's colour: its human's choice; an AI commander on seat 1 keeps the mission's)
            ccfg.Color = start.Coop.SeatOf(1) is { Ai: false } h1 ? h1.Color : null;
        }
        var C = s.Campaign = new Campaign(s.Data, ccfg).Plan(rules, models);
        // (the mission folder is the mission's map: its terrain, objects and regions - docs/MAP_FORMAT.md)
        Step(0.18, "Reading the map");
        s.Map = WorldBuilder.MapSourceOf(folder, rules, models, C.Skip, forest);
        // co-op: the map work's part of the definition (extension, resources) - src/Sim/Coop/CoopMapHook.cs
        Coop.CoopMapContext? mapCtx = null;
        if (C.Coop != null)
        {
            mapCtx = new Coop.CoopMapContext { Definition = C.Coop.Def, Map = s.Map, Rules = rules, Models = models, Config = cfg, Campaign = C };
            foreach (var seat in C.Coop.Seats)
            {
                mapCtx.SeatsInUse.Add(seat.Seat);
                if (seat.Def is { } sd) { mapCtx.SeatParts.Add((seat.Seat, sd.Extend, sd.MapResources)); if (sd.X is double sx && sd.Z is double sz) mapCtx.Starts.Add((seat.Seat, sx, sz)); }
            }
            Coop.CoopMapHook.Before(mapCtx);
            foreach (var pr in mapCtx.Problems) s.Problems.Add(pr);
        }
        // ---- the world (buildWorld): landscape, players and objects, animals and nests
        var wopts = new WorldOptions { Seed = start.Seed, Warpgate = false, Multiplayer = false };   // campaign missions have no built-in victory, no warp gate rule (GameOverMgr.usl:95-103)
        s.Host = new MissionHost(C, o.Presenter);
        Step(0.4, "Building the world");
        s.Built = WorldBuilder.Build(s.Map, rules, models, wopts, C, brushes);
        // the debug options chosen before the start (cheats.js applyCheats), then the computer players (createBrains)
        Step(0.95, "Waking the computer players");
        Cheats.Apply(s.World, ccfg.Cheats, C.Human);
        var aiTables = o.NoBrains && C.Coop == null ? null : AiDataOf(aiData, cfg);
        WorldBuilder.CreateBrains(C, o.NoBrains ? null : o.Brains ?? new Ai.CampaignBrains(aiTables!));
        // co-op: the AI commanders (a computer player each, driven by the humans' commands), the hero split, the scaling
        if (C.Coop != null)
        {
            C.Coop.CreateCommanders(aiTables!);
            C.Coop.AfterBuild(C);
            mapCtx!.World = s.World;
            Coop.CoopMapHook.After(mapCtx);
            foreach (var pr in mapCtx.Problems) if (!s.Problems.Contains(pr)) s.Problems.Add(pr);
        }
        // ---- the camera at the human player's start location
        var sl = C.Human != null ? C.StartLocations[C.Human.Id] : null;
        Entity? own = sl == null && C.Human != null ? (Entity?)s.World.Buildings.FirstOrDefault(b => b.Owner == C.Human && b.Alive) ?? s.World.Units.FirstOrDefault(u => u.Owner == C.Human && u.Alive) : null;
        s.StartCamera = (sl?.X ?? own?.Pos.X ?? 0, sl?.Z ?? own?.Pos.Z ?? 0, Math.PI / 2 - C.Map["default_camera"].Num(0.785));
        // (a start camera the map editor set: map_info.start_camera { x, y } - docs/MAP_EDITOR.md "Cameras")
        if (C.Map["start_camera"] is System.Text.Json.Nodes.JsonObject scam) { var (scx, scz) = C.ToGame(scam["x"].Num(), scam["y"].Num()); s.StartCamera = (scx, scz, s.StartCamera.Yaw); }
        // ---- the trigger engine: starts with the first step
        if (!o.NoTriggers)
        {
            var E = s.Engine = new TriggerEngine(s.Host, s.Lua.Script, s.Lua.Lua);
            C.Engine = E;
            s.Lua.Bind(E);
        }
        // a co-op game: who commands which player, every human its own fog (docs/MULTIPLAYER.md)
        Seats.Apply(s.World, start.Seats);
        o.Flags?.Apply(s.World, C.Human, start.Seats != null ? Seats.Players(s.World, start.Seats) : null);
        foreach (var w in C.Warnings) L.Debug("setup: " + w);
        Step(1, "Mission ready");
        return s;
    }

    /// <summary>the computer players' tables: ai.json, and the gifts of the installation's control script where ai.json
    /// has none (MIRAGE: AiData.ReadControlScript)</summary>
    public static Ai.AiData AiDataOf(JsonNode? aiJson, GameConfiguration? cfg)
    {
        var ad = new Ai.AiData(aiJson);
        try
        {
            var f = cfg?.GameFile("Scripts/Ai/Modules/AiModuleControlDefault.usl");
            if (f != null && ad.ReadControlScript(File.ReadAllText(f))) L.Info("the computer players' gifts from the control script (ai.json has none)");
        }
        catch (Exception e) { L.Warn("the AI control script: " + e.Message); }
        return ad;
    }

    /// <summary>one simulation step (World.STEP = 50 ms): commands, the world, the computer players, the campaign and its
    /// triggers. Nothing happens once the mission is over.</summary>
    public void Step() => World.Step();
    /// <summary>steps for `seconds` of game time (tests, a headless run)</summary>
    public void Run(double seconds)
    {
        int n = (int)Math.Round(seconds / GameWorld.STEP);
        for (int i = 0; i < n; i++) World.Step();
    }
    /// <summary>the mission UI: the sequence on show is over or was skipped</summary>
    public void EndSequence() => Engine?.EndSequence();
    /// <summary>the mission UI: the dialogue scene of this token is over</summary>
    public void EndDialog(int token) => Engine?.EndDialog(token);
    /// <summary>the mission has ended (won / lost: Host.Ended)</summary>
    public bool Over => Host.Ended != null;
    public bool Won => Host.Ended?.Won == true;
    /// <summary>the next mission of the campaign (null after the last)</summary>
    public MissionEntry? Next => Campaign.Next();
    /// <summary>the world's state hash plus the campaign's (records, variables, trigger firings): the same start and the
    /// same commands give the same value (docs/ARCHITECTURE.md "Determinism")</summary>
    /// <summary>the world's dump (World.StateDump) and the campaign's: objects, variables, the trigger firings</summary>
    public void StateDump(TextWriter o)
    {
        World.StateDump(o);
        foreach (var r in Campaign.Objects.All) o.WriteLine($"record {r.Id} alive {r.Alive} slot {r.Slot}");
        if (Engine == null) return;
        o.WriteLine($"triggers fired {Engine.StatFired} actions {Engine.StatActions} running sequence {Engine.SeqRunning?.Path} dialogs {string.Join(",", Engine.DialogsRunning.Select(d => d.Token))}");
        foreach (var (k, v) in Engine.Variables) o.WriteLine($"var {k} = {v}");
        int i = 0;
        foreach (var f in Engine.FiringLog) o.WriteLine($"fired {i++} {f.Trigger}");
    }

    public ulong StateHash()
    {
        ulong h = World.StateHash();
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        void MixS(string s) { foreach (var ch in s) Mix(ch); Mix(0xff); }
        foreach (var r in Campaign.Objects.All) { Mix((ulong)r.Id); Mix(r.Alive ? 1UL : 0UL); Mix((ulong)(r.Slot + 2)); }
        if (Engine != null)
        {
            Mix((ulong)Engine.StatFired); Mix((ulong)Engine.StatActions);
            foreach (var (k, v) in Engine.Variables) { MixS(k); MixS(v); }
            foreach (var f in Engine.FiringLog) MixS(f.Trigger);
        }
        return h;
    }
}
