// A running game: the simulation (src/Sim: a campaign MissionSession or a SkirmishSession - IGameSim), the view of it (game/src/View: the map's
// WorldView, the entities' models, the fog of war), the player's input (game/src/UI/Input), the mission UI
// (game/src/UI/Mission), the HUD (game/src/UI/Hud, it registers itself as IGameHud), the pause menu with the debug
// tools (game/src/UI/Game), and the clock between them.
//
// Load() sets everything up in steps (the LoadingScreen shows them, one per frame); afterwards the session runs the
// simulation in fixed 50 ms steps (real time x game speed, at most 8 per frame), lets the view interpolate between
// the last two steps, drains the world's events once per frame (feedback: GameSession.Feedback.cs) and hands the
// player's commands to the simulation through the command sink (docs/ARCHITECTURE.md "Commands").
// The API the HUD and the menus use: docs/GAME_SESSION.md (GameSession.Current, World, Me, Selection, Commands ...).
//
// Ported from remake/src/main.js (startGame for a campaign, buildWorld's game part, simulate, draw, the frame loop,
// updateFow, G.endMission) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.View;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Sequel;
using PwRemake.Sim.World;
using PwRemake.UI;
using PwRemake.UI.Game;
using PwRemake.UI.Hud;
using PwRemake.UI.Hud.Logic;
using PwRemake.UI.PlayerInput;
using PwRemake.UI.Mission;
using PwRemake.UI.Mission.Logic;
using PwRemake.View.Audio;
using PwRemake.View.Entities;
using PwRemake.View.Models;
using PwRemake.View.Terrain;
using PwRemake.View.World;
using GameWorld = PwRemake.Sim.World.World;
using SimRules = PwRemake.Sim.Rules.Rules;

namespace PwRemake.App;

public partial class GameSession : Node3D
{
    static readonly Logger L = Log.For("Game");
    /// <summary>the running game (null outside a game)</summary>
    public static GameSession? Current { get; private set; }

    public GameStart Start { get; }
    public const double StepSeconds = GameWorld.STEP;
    /// <summary>at most this many simulation steps per frame (a slow frame slows the game instead of spiralling)</summary>
    public const int MaxStepsPerFrame = 8;

    // ---- the simulation
    /// <summary>what is played: a campaign mission or a skirmish (src/Sim/Game/IGameSim)</summary>
    public IGameSim Sim { get; private set; } = null!;
    /// <summary>the campaign mission (null in a skirmish)</summary>
    public MissionSession? Mission { get; private set; }
    /// <summary>the skirmish (null in a campaign mission; GameSession.Skirmish.cs)</summary>
    public SkirmishSession? Skirmish { get; private set; }
    public GameWorld World => Sim.World;
    public Player Me { get; private set; } = null!;
    public SimRules Rules => World.Data;
    public GameConfiguration Config { get; private set; } = null!;
    public AssetSource Assets { get; private set; } = null!;
    public ICommandSink Commands { get; private set; } = null!;

    // ---- the view and the interface
    public ModelLibrary Models { get; private set; } = null!;
    public WorldView View { get; private set; } = null!;
    public WorldRenderer Renderer { get; private set; } = null!;
    public RtsCamera Camera { get; private set; } = null!;
    RtsCameraInput _camInput = null!;
    /// <summary>the human player's fog of war - the world's (World.Fog: the simulation keeps it, docs/SAVES.md)</summary>
    public FogGrid Fog { get; private set; } = null!;
    public EntityViews Entities { get; private set; } = null!;
    public SelectionOverlay Overlay { get; private set; } = null!;
    /// <summary>the attack range circles of the shooting buildings (the ghost's, the own ones', the selected one's)</summary>
    public RangeRings Ranges { get; private set; } = null!;
    public Selection Selection { get; private set; } = null!;
    public GameInput Input { get; private set; } = null!;
    public MissionPresenter Presenter { get; private set; } = null!;
    public MissionPresenterAdapter PresenterLink { get; } = new();
    /// <summary>the cutscenes' camera and actors (the mission UI's ISequenceStage)</summary>
    public SessionSequenceStage Stage { get; private set; } = null!;
    /// <summary>the HUD (game/src/UI/Hud; layer 10 - the mission UI is 20, the pause menu 30)</summary>
    public Hud HudView { get; private set; } = null!;
    /// <summary>the HUD as the input needs it (null: the session's own message line)</summary>
    public IGameHud? Hud { get; set; }
    PauseMenu? _menu;

    // ---- the clock
    double _acc;
    /// <summary>the fraction of a step since the last one (the view's interpolation)</summary>
    public double Alpha { get; private set; }
    /// <summary>the player's pause (Pause key)</summary>
    public bool PausedByPlayer { get; set; }
    /// <summary>the mission UI's pause (cutscenes, the quest log, the end screen)</summary>
    public bool PausedByUi { get; private set; }
    /// <summary>a menu of the game is open (the pause menu, the debug tools, the options)</summary>
    public bool MenuOpen { get; private set; }
    public bool Paused { get => PausedByPlayer || PausedByUi || MenuOpen; set => PausedByPlayer = value; }
    public bool Cinematic => Presenter != null && Presenter.Cinematic;
    public bool Over => Sim != null && Sim.Over;
    public double GameTime => World.Time;
    /// <summary>the alerts (attacks, finished buildings, warp gates): Space jumps to the last one</summary>
    public readonly List<(double X, double Z, double T)> Pings = new();
    int _fogShown = -1;

    // ---- events (docs/GAME_SESSION.md)
    public event Action<Selection>? SelectionChanged;
    /// <summary>once per frame: the world events of the steps of this frame (drained from World.Events)</summary>
    public event Action<IReadOnlyList<WorldEvent>>? FrameEvents;
    /// <summary>a line for the message bar: (text, kind "" | "good" | "bad" | "warn")</summary>
    public event Action<string, string>? MessagePosted;
    /// <summary>a marker for the minimap: (kind "Attack" | "Finished" | "Ping", x, z)</summary>
    public event Action<string, double, double>? MinimapMarker;

    // ---- performance (F9)
    readonly Dictionary<string, double> _prof = new();
    public IReadOnlyDictionary<string, double> Profile => _prof;
    public string PerfLine { get; private set; } = "";
    int _stepsLastFrame, _frameNo, _hitches;
    readonly Dictionary<string, double> _last = new();

    // ---- test options (command line, docs/GAME_SESSION.md)
    readonly Dictionary<string, string> _args;
    Autoplay? _autoplay;

    // --speed x (this run's game speed, not saved) and --max-steps n (steps per frame): test runs on slow machines
    readonly double? _speedArg;
    readonly int _maxSteps = MaxStepsPerFrame;
    public GameSession(GameStart start, SaveGame? resume = null)
    {
        Start = start; Resume = resume; Name = "GameSession"; _args = SafeArgs();
        if (_args.TryGetValue("speed", out var sp) && Json.ParseNum(sp, 0) > 0) _speedArg = Json.ParseNum(sp, 1);
        if (_args.TryGetValue("max-steps", out var ms) && Json.ParseNum(ms, 0) >= 1) _maxSteps = (int)Json.ParseNum(ms, 8);
    }
    public GameSession() : this(new GameStart()) { }

    static Dictionary<string, string> SafeArgs() { try { return GameApp.UserArgs(); } catch (Exception) { return new(); } }

    // ======================================================================================== loading
    // The stages of Load and their share of the loading bar (measured on mission 1: rules and manifest ~2 s, the
    // mission's world ~10 s, the models ~20 s, the terrain ~2 s, the objects < 1 s).
    public const double RulesEnd = 0.05, MissionEnd = 0.33, ModelsStart = 0.35, ModelsEnd = 0.9, TerrainEnd = 0.97;
    /// <summary>ms of loading work per frame before the loading screen gets a frame (keeps the window responsive):
    /// four times what a frame of the loading screen itself takes (drawing, waiting for the screen's refresh), so
    /// the screen costs at most ~20 % of the loading time; within FrameBudgetMin..Max</summary>
    const double FrameBudgetMin = 50, FrameBudgetMax = 250;

    /// <summary>Set-up steps: (progress 0..1, what is being done) - main.js startGame for a campaign mission. Each
    /// MoveNext does at most FrameBudgetMin..Max ms of work on the main thread (the loading screen draws in between);
    /// the rules, the mission and its world (pure simulation, no Godot objects) are made on a worker thread.</summary>
    public IEnumerable<(double, string)> Load()
    {
        var sw = Stopwatch.StartNew();
        Config = GameApp.Config ?? throw new InvalidOperationException("no game configuration");
        Assets = GameApp.Assets ?? new AssetSource(Config);
        L.Info($"loading {Start}");
        // ---- 1. rules, mission, map and world: on a worker thread; its progress through SessionOptions.Progress
        double simK = 0;
        string simWhat = "Reading the rules";
        object gate = new();
        void SimProgress(double k, string what) { lock (gate) { simK = k; simWhat = what; } }
        var dataDir = Config.Data;
        var cfg = Config;
        var start = Start;
        var link = Tap;                           // (the mission UI's adapter behind the tap that records its answers: GameSession.Saves.cs)
        if (Resume != null) SaveReplay.Prepare(Resume, Tap);      // (a saved game: the record answers from the start)
        var flags = Flags;
        var task = System.Threading.Tasks.Task.Run(() =>
        {
            SimProgress(0, "Reading the rules");
            var rules = start.Mode == GameMode.Sequel ? null! : SimRules.Load(dataDir);       // (Containment makes its own: SequelSession.SequelRules)
            SimProgress(0.5, "Reading the model list");
            var manifest = PwRemake.Sim.Content.ModelManifest.Load(dataDir).WithOverrides(cfg);      // (+ the sequel's art from the overrides)
            if (start.Mode == GameMode.Skirmish) return CreateSkirmish(start, cfg, dataDir, rules, manifest, flags, (k, what) => SimProgress(2 + k, what));
            // the remake's own campaigns (Containment, the scenarios: GameSession.Sequel.cs)
            if (start.Mode is GameMode.Sequel or GameMode.Scenario) return CreateSequel(start, cfg, dataDir, rules, manifest, flags, (k, what) => SimProgress(2 + k, what));
            var opts = new SessionOptions
            {
                Rules = rules, Models = manifest, Presenter = link, Flags = flags,
                // Brains: null = the computer player of src/Sim/Ai (Ai.CampaignBrains), as MissionSession defaults
                Progress = (k, what) => SimProgress(2 + k, what),       // (2..3: the mission; 0..1: the rules)
            };
            return (IGameSim)MissionSession.Create(start, cfg, opts);
        });
        string last = "";
        while (!task.IsCompleted)
        {
            double k; string what;
            lock (gate) { k = simK; what = simWhat; }
            if (what != last) { L.Debug($"loading: {what} ({sw.ElapsedMilliseconds} ms)"); last = what; }
            // rules 0..1 -> 0..RulesEnd; the mission (Create's own steps, 2..3) -> RulesEnd..MissionEnd
            double p = k >= 2 ? RulesEnd + (MissionEnd - RulesEnd) * Math.Clamp(k - 2, 0, 1) : RulesEnd * Math.Clamp(k, 0, 1);
            yield return (p, what);
        }
        if (task.IsFaulted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(task.Exception!.GetBaseException()).Throw();
        Sim = task.Result;
        Mission = Sim as MissionSession;
        Skirmish = Sim as SkirmishSession;
        Sequel = Sim as SequelSession;
        foreach (var p in Sim.Problems) L.Warn("game: " + p);
        Me = MyPlayer();                          // (a networked game: this machine's seat - GameSession.Net.cs)
        L.Info($"{Sim.Title} set up in {sw.ElapsedMilliseconds} ms: {World.Units.Count} units, {World.Buildings.Count} buildings, {World.Resources.Count} resources, you are player {Me.Id} ({Me.Tribe})");
        StartRecording();                         // (every step from the first is recorded for saved games: GameSession.Saves.cs)
        // ---- 2. the models (main thread: Godot's glTF import), as many per frame as fit the budget
        yield return (MissionEnd, "Choosing the models");
        Models = new ModelLibrary(Assets) { PartsOnLinks = PartsOnLinksOf(Rules) };
        var want = PreloadList();
        var replay = StartReplay();               // a saved game: its steps run on a worker thread while the models load
        int done = 0;
        var t0 = sw.ElapsedMilliseconds;
        var frame = Stopwatch.StartNew();
        double budget = FrameBudgetMin;
        yield return (ModelsStart, $"Loading models 0 / {want.Count}");
        frame.Restart();
        bool memLog = _args.ContainsKey("perf-mem");
        PwRemake.View.Models.ModelLibrary.MemSteps = memLog;
        foreach (var (name, mo) in want)
        {
            ulong m0 = memLog ? OS.GetStaticMemoryUsage() : 0;
            var mt = Models.Template(name, mo, true);
            if (memLog) L.Info($"MEM {name} {mt?.Fourcc}: +{(OS.GetStaticMemoryUsage() - (double)m0) / 1048576:0.0} MB static, {mt?.Clips?.GetAnimationList().Count ?? 0} clips, {PerfKeys(mt) / 1000:0} k keys, {mt?.LoadMs:0} ms ({mt?.LoadSteps})");
            done++;
            if (frame.Elapsed.TotalMilliseconds >= budget || done == want.Count)
            {
                double work = frame.Elapsed.TotalMilliseconds;
                yield return (ModelsStart + (ModelsEnd - ModelsStart) * done / Math.Max(1, want.Count), $"Loading models {done} / {want.Count}");
                double gap = frame.Elapsed.TotalMilliseconds - work;          // the frame between: drawing, vsync
                budget = Math.Clamp(4 * gap, FrameBudgetMin, FrameBudgetMax);
                frame.Restart();
            }
        }
        L.Info($"{want.Count} models loaded in {sw.ElapsedMilliseconds - t0} ms");
        // (what the models' loading left for the collector - Godot objects whose native memory the managed heap
        // does not see - freed now, before the game)
        GC.Collect(); GC.WaitForPendingFinalizers();
        if (memLog)
        {
            double before = OS.GetStaticMemoryUsage() / 1048576.0;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            L.Info($"MEM after loading: {before:0} MB static, after a full GC {OS.GetStaticMemoryUsage() / 1048576.0:0} MB; textures {Models.Textures.Count} ({Models.Textures.Bytes / 1048576.0:0} MB with mipmaps)");
        }
        if (replay is { } rp) foreach (var p in WaitReplay(rp)) yield return p;
        World.ViewEvents = true;                  // (the view's own copy of the events from now on: World.Events.cs)
        if (Resume != null) World.DrainEvents();  // (not the sounds and messages of the replayed steps)
        // ---- 3. the terrain and the rest of the view, 4. the entities
        yield return (ModelsEnd, $"Building the terrain of {Sim.Map.Name}");
        BuildView();
        yield return (TerrainEnd, "Placing the objects");
        BuildEntities();
        // (what the view and the entities made survives into the game: collected now, under the loading screen - else
        // the game's first collections promoted it, a pause of 90 - 130 ms some 20 s into the Outbreak, --perf-to)
        GC.Collect(); GC.WaitForPendingFinalizers();
        yield return (1.0, "Ready");
        L.Info($"loaded in {sw.ElapsedMilliseconds} ms");
    }

    void BuildView()
    {
        var S = Sim.Map;
        var set = GameApp.Settings;
        var rs = new RenderSettings
        {
            Shadows = set.Shadows switch { "off" => "off", "high" => "high", _ => "medium" },
            Grass = set.Grass is "off" or "dense" ? set.Grass : "on",
            ResScale = Math.Clamp(set.RenderScale, 0.25, 2), FpsCap = set.FpsCap, Antialias = set.Antialias, Particles = set.Particles,
            Weather = set.Weather,
        };
        Renderer = WorldRenderer.Create(rs);
        var W = World;
        var opts = new WorldViewOptions
        {
            Render = rs, Fog = true, Brushes = Sim.Built.Brushes,
            // no grass inside buildings, on blocked cells (main.js GrassLayer density)
            GrassBlocked = (x, z) => !W.Nav.IsFree(x, z) || InBuilding(x, z) || (Sequel != null && SequelMap.OnRoad(Sequel.Roads, x, z)),
        };
        View = WorldView.Load(S, Models, opts, Sim.Built.Map.Hf);
        Biomes = new BiomeAmbience(View, Renderer);          // (a map with biomes: light, fog, weather, ambience - docs/BIOMES.md)
        // the world's ground brushes and plant removal also repaint the view (main.js W.adaptTerrain / clearVegetation)
        var simAdapt = W.AdaptTerrain;
        W.AdaptTerrain = (gfx, x, z, rot) =>
        {
            bool ok = simAdapt?.Invoke(gfx, x, z, rot) ?? false;
            if (ok) View.AdaptTerrain(gfx, x, z, rot);
            return ok;
        };
        var simClear = W.ClearVegetation;
        W.ClearVegetation = (x, z, r) => { simClear?.Invoke(x, z, r); View.ClearVegetation(x, z, r); };
        // the fog of war of the human player: the world keeps it (SimFlags.Game -> World.EnableFog; the simulation asks it
        // as the JS did); a save made without it gets a view-only grid with everything visible
        Fog = W.FogOf(Me) ?? W.Fog ?? new FogGrid(S.Size, 4) { RevealAll = true };      // (a networked game: this machine's player's own)
        // (the clips' sound events come as "animSound" events (Feedback): SimFlags.Game -> World.AnimSounds)
    }

    bool InBuilding(double x, double z)
    {
        bool hit = false;
        World.BHash.Query(x, z, 30, b => { if (!hit && b.Alive && Math.Sqrt((b.Pos.X - x) * (b.Pos.X - x) + (b.Pos.Z - z) * (b.Pos.Z - z)) < b.Radius * 0.95 + 0.5) hit = true; });
        return hit;
    }

    void BuildEntities()
    {
        Selection = new Selection(() => Me);
        Entities = new EntityViews(World, Models, View, () => Me, Fog, Sees) { PrepareInfectedAhead = !_args.ContainsKey("no-warmup"), NoFog = () => Stage?.NoFog == true };
        Entities.Sync(0, 1, null, true);
        BuildEffects();
    }

    // ======================================================================================== in the tree
    public override void _Ready()
    {
        Current = this;
        Log.GameClock = () => World.Time;
        AddChild(Renderer);
        AddChild(View);
        AddChild(Entities);
        var S = Sim.Map;
        Camera = new RtsCamera(Renderer.Camera, (x, z) => View.Ground.At(x, z), S.Size / 2 - 8);
        // the camera starts at the human player's start location, looking along the level's default camera angle
        Camera.X = Sim.StartCamera.X; Camera.Z = Sim.StartCamera.Z; Camera.Yaw = Sim.StartCamera.Yaw;
        if (MyStart() is var (mx, mz)) { Camera.X = mx; Camera.Z = mz; }      // (a networked game: at this machine's player's home)
        if (_args.TryGetValue("cam", out var cs))
        {
            var p = cs.Split(',').Select(s => Json.ParseNum(s.Trim(), double.NaN)).ToArray();
            if (p.Length >= 2 && double.IsFinite(p[0])) { Camera.X = p[0]; Camera.Z = p[1]; }
            if (p.Length >= 3 && double.IsFinite(p[2])) Camera.Yaw = p[2] * Math.PI / 180;
            if (p.Length >= 4 && double.IsFinite(p[3])) Camera.Dist = Camera.TDist = p[3];
        }
        Camera.Update(0.016);
        _camInput = new RtsCameraInput { Cam = Camera, Name = "CameraInput", EdgeScroll = true };
        AddChild(_camInput);
        Overlay = new SelectionOverlay(this);
        AddChild(Overlay);
        Ranges = new RangeRings(this) { ShowAll = GameApp.Settings.ShowRanges };
        AddChild(Ranges);
        Commands = new LocalCommandSink(World);
        NetReady();                               // (a networked game: the host's turns and its command sink - GameSession.Net.cs)
        if (_args.ContainsKey("reveal") && World.Fog is { RevealAll: false }) Submit(new DebugToolCommand { Tool = "fog", On = true });
        Input = new GameInput(this) { Name = "Input" };
        AddChild(Input);
        Selection.Changed += (sel, silent) => OnSelectionChanged(sel, silent);
        // the mission UI (ui/mission.js): presentation calls of the trigger engine, scenes ended back to it
        // (a skirmish: no mission data - the presenter shows only the end screen)
        var ui = new MissionPresenter(MissionUiData.FromJson(Mission?.Lua.World, Mission?.Lua.Mission, null), Assets);
        if (Mission != null) foreach (var (k, v) in Mission.Lua.Script.Texts) ui.State.Data.Texts[k] = v;
        var gameTexts = GameTexts.For(Config);           // the installation's tables behind the mission's (actor names)
        if (gameTexts.Count > 0) ui.State.Data.GameText = k => gameTexts.Has(k) ? gameTexts.Get(k, "") : null;
        Presenter = ui;
        AddChild(ui);
        // (the scenes' ends go back as commands - SceneEndCommand, recorded with the step they come at: GameSession.Saves.cs)
        if (Mission?.Engine != null) ui.Bind(new SessionEngineLink(this, Mission.Engine));
        ui.OnPause = p => PausedByUi = p;
        // the HUD goes while a cutscene shows - the co-op panel too (its own layer: GameSession.Coop.cs)
        ui.OnCinematic = on => { if (Hud != null) Hud.Visible = !on; if (CoopUi != null) CoopUi.Visible = !on; if (on) Input.CancelMouse(); };
        ui.OnEndAction = EndAction;
        ui.ObjectPos = id => Mission?.Campaign.Objects.ById(id) is { Entity: { } e } && e.Alive ? Mission.Campaign.ToMap(e.Pos.X, e.Pos.Z) : null;
        ui.OnSequenceFinished = r => { if (Mission != null && !r.SnapBack && r.Camera is { } c && !c.IsZero) { var (x, z) = Mission.Campaign.ToGame(c.X, c.Y); LookAt(x, z); } };
        ui.EndStats = null;
        Stage = new SessionSequenceStage(this);
        ui.Stage = Stage;
        Entities.Live = e => Stage.Actors.Contains(e);
        Entities.AnimOf = Stage.AnimOf;
        Entities.PropAnimOf = Stage.AnimOf;
        Entities.Hide = Stage.Hides;
        PresenterLink.Attach(this, ui);
        // the HUD (docs/HUD.md): its host, the minimap's map and colours, the message lines and markers of the events
        var set = GameApp.Settings;
        HudView = new Hud(new SessionHudHost(this)) { UiScale = set.UiScale };
        AddChild(HudView);
        HudView.SetMap(MinimapSource.From(S), MinimapModel.LoadTable(Assets.Resolve($"assets/terrain/{S.Setting}/setting.json")));
        var lost = HudView.News.Lost;
        HudView.News.Lost = e => { lost(e); Input.Forget(e); };
        Hud = HudView;
        WarmUp();                                 // (the first placement's costs, paid now - after the HUD's art: GameSession.Models.cs)
        ApplySettings(set);                       // (the Options window's settings: GameSession.Settings.cs)
        if (Resume == null && Sequel == null) ui.ShowTitle();       // (a loaded game: no title card; the remake's campaigns: their briefing)
        ReadySequel();                            // (the remake's own campaigns: GameSession.Sequel.cs)
        ReadyDayNight();                          // (a map's day-night cycle: GameSession.DayNight.cs)
        ReadyCoop();                              // (a co-op campaign game: GameSession.Coop.cs)
        // sound and music: the level's playlist and ambience (main.js after buildWorld)
        try { GameAudio.I.StartGame(S.LevelName, S.Kind == "original" ? S.Atmos : S.Setting.ToLowerInvariant() + ".txt", Me.Tribe); }
        catch (Exception e) { L.Error("sound", e); }
        UpdateFow(true);
        if (Resume != null) AfterLoad();          // camera, selection, groups, the quest log, scenes on show (GameSession.Saves.cs)
        else Message(Skirmish != null ? SkirmishStartLine() : Sim.Title, "good");
        if (_args.ContainsKey("autoplay")) _autoplay = new Autoplay(this);
        if (_args.ContainsKey("shots") || _args.ContainsKey("input-check") || _args.ContainsKey("mark-check") || _args.ContainsKey("menu-at") || _args.ContainsKey("help-at") || _args.ContainsKey("spawn") || _args.ContainsKey("test-placement") || _args.ContainsKey("cine")) _autoplay ??= new Autoplay(this, false);
        _autoplay?.Configure(_args);
        PerfConfigure();
        L.Info($"game started: camera {Camera.X:0}, {Camera.Z:0}; {Models.Loaded.Count()} model templates, {OS.GetStaticMemoryUsage() / 1048576} MB static memory");
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        PwRemake.View.Materials.UnitsBehind.SetSuspended(false);
        Log.GameClock = () => double.NaN;
        _recorder?.Detach();
        // the game is left (lost / won / quit, load, retry, back to the editor, a co-op game closed): its music and
        // ambience end, the main theme plays - MusicMgr.usl turns every game switch off while no game runs (the combat,
        // a trigger's looped track, a cutscene's silence would otherwise go on in the menus). (Children leave the
        // tree first: the mission UI's cutscene skip has run.)
        try { if (GameAudio.Current is { } ga && ga.IsInsideTree()) ga.Menu(); } catch (Exception e) { L.Warn($"menu music: {e.Message}"); }
    }

    // ======================================================================================== the frame
    public override void _Process(double delta)
    {
        if (Sim == null || View == null) return;
        PwRemake.View.Materials.UnitsBehind.SetSuspended(Cinematic);       // (no silhouettes in cutscenes)
        var sw = Stopwatch.StartNew();
        double t = 0;
        long bytes = PerfOn ? GC.GetAllocatedBytesForCurrentThread() : 0;
        void Mark(string what)
        {
            double now = sw.Elapsed.TotalMilliseconds; _prof[what] = (_prof.GetValueOrDefault(what) * 0.9) + (now - t) * 0.1; _last[what] = now - t; t = now;
            if (PerfOn) { long b = GC.GetAllocatedBytesForCurrentThread(); _lastBytes[what] = b - bytes; bytes = b; }
        }
        double dt = Math.Min(_speedArg != null ? 1.0 : 0.1, delta);
        // ---- the simulation (main.js simulate): fixed steps of game time
        double gdt = 0;
        _stepsLastFrame = 0;
        if (Net != null)
        {
            // a networked game: the host's turns, whatever this machine's menus or cutscenes do (GameSession.Net.cs)
            NetFrame(dt);
            gdt = _stepsLastFrame * StepSeconds;
        }
        else if (PerfOn && !Paused) PerfSteps();        // (--perf: a fixed number of steps per frame)
        else if (!Paused)
        {
            double speed = _speedArg ?? Math.Clamp(GameApp.Settings.GameSpeed, 0.25, 4);
            _acc += dt * speed;
            gdt = dt * speed;
            while (_acc >= StepSeconds && _stepsLastFrame < _maxSteps)
            {
                _acc -= StepSeconds;
                Entities.BeforeStep();
                try { Sim.Step(); }
                catch (Exception e) { L.Error($"simulation step {World.StepNo} failed", e); }
                Presenter.Tick(StepSeconds);
                _stepsLastFrame++;
            }
            if (_acc > StepSeconds) _acc = StepSeconds;      // (the rest of a slow frame is dropped)
            gdt = _stepsLastFrame * StepSeconds;
        }
        UpdateFow(false);
        if (Net == null) Alpha = Math.Clamp(_acc / StepSeconds, 0, 1);
        if (Skirmish != null) CheckSkirmishEnd(dt);
        SequelFrame();
        TestEnd();
        Mark("simulation");
        // ---- world events (main.js handleEvents -> ui/feedback.js)
        var evs = World.DrainEvents();
        try { HandleEvents(evs); } catch (Exception e) { L.Error("feedback", e); }
        if (evs.Count > 0) { try { HudView.OnEvents(evs); } catch (Exception e) { ErrOnce("hud-ev", "the HUD's events", e); } FrameEvents?.Invoke(evs); }
        DeliverCommandResults();
        Selection.Prune();
        Mark("events");
        // ---- drawing (main.js DRAW_STEPS)
        Input.Step(dt);
        if (!Cinematic) _camInput.Step(dt);
        if (!Stage.OwnsCamera) Camera.Update(dt);
        View.Fog.Suspended = Stage.NoFog;                // (a cutscene: no fog of war drawn - SessionSequenceStage.NoFog)
        if (View.Clouds != null) { View.Clouds.Suspended = Stage.NoFog; UpdateCloudTint(); }
        Entities.LiveDt = dt;
        Mark("input");
        Entities.Sync(gdt, Alpha, Renderer.Camera, false);
        Mark("entities");
        StepEffects(dt, gdt);
        Mark("effects");
        Biomes?.Update(Camera.Target);
        // the light of the mixed particles (smoke, steam, dust): the original's sun colour of the setting, darkened with
        // the scene's sun (night, dusk, a cutscene's time of day) - WorldRenderer.ParticleLight, docs/EFFECTS.md
        SkyTable();
        View.Particles.MixLight = Renderer.ParticleLight;
        View.Step(dt, Renderer.Camera, Camera.Target, Biomes != null ? Biomes.Weather(WeatherNow()) : WeatherNow());
        PerfProbe.P?.Begin();
        UpdateDayLight();
        Renderer.FollowSun(Camera.Target);
        PerfProbe.P?.Mark("view:light+sun");
        Mark("view");
        PerfProbe.P?.Begin();
        UpdateAudio(dt);
        PerfProbe.P?.Mark("overlay:audio");
        Overlay.Step(dt);
        Ranges.Step();
        PerfProbe.P?.Mark("overlay:overlay");
        Mark("overlay");
        _autoplay?.Step(dt);
        SavesStep(delta);
        double frameMs = sw.Elapsed.TotalMilliseconds;
        _prof["frame"] = (_prof.GetValueOrDefault("frame") * 0.9) + frameMs * 0.1;
        PerfFrame(frameMs);
        // frame spikes (main.js noteHitch): a frame far longer than the usual, with where its time went
        _frameNo++;
        if (frameMs > 250 && (frameMs > _prof["frame"] * 2.5 || _frameNo < 5) && _hitches++ < 40)
            L.Info($"slow frame {_frameNo}: {frameMs:0} ms ({string.Join(", ", _last.Select(kv => $"{kv.Key} {kv.Value:0}"))}; {_stepsLastFrame} steps)");
        PerfLine = $"game {World.Time:0.0} s x{GameApp.Settings.GameSpeed:0.##}{(Paused ? " paused" : "")} · {_stepsLastFrame} steps · " +
                   string.Join(" · ", _prof.Select(kv => $"{kv.Key} {kv.Value:0.0}")) + $" ms · units {World.Units.Count} drawn {Entities.Drawn}";
    }

    /// <summary>the weather drawn: a cutscene's day time item (weather type 0: none, 1 snow, 2 rain - cutscene.js
    /// G.seqWeather), else the world's</summary>
    View.Fx.WeatherState? WeatherNow()
    {
        if (Stage?.Day is { } d) return d.Weather is 1 or 2 ? new View.Fx.WeatherState(d.Weather, d.Intensity, 0, 0) : null;
        return World.WeatherNow() is { } w ? new View.Fx.WeatherState(w.Type, w.Intensity, w.Wind, w.Strength) : null;
    }

    PwRemake.Sim.Content.SkyGradient? _skyTable;
    bool _skyLoaded;
    /// <summary>the setting's sky gradient (assets/terrain/<Setting>/sky.json: the prepare tool's sky.py), loaded once</summary>
    PwRemake.Sim.Content.SkyGradient? SkyTable()
    {
        if (_skyLoaded) return _skyTable;
        _skyLoaded = true;
        var path = Assets.Resolve($"assets/terrain/{Sim.Map.Setting}/sky.json");
        _skyTable = PwRemake.Sim.Content.SkyGradient.Load(path);
        L.Info(_skyTable != null ? $"sky table of {Sim.Map.Setting}: {_skyTable.Source}, {_skyTable.Width} x {_skyTable.Height}{(_skyTable.Varies(PwRemake.Sim.Content.SkyGradient.RowSun) ? "" : " (the same light all day)")}"
                                 : $"no sky table for {Sim.Map.Setting} (assets/terrain/{Sim.Map.Setting}/sky.json: the prepare tool's sky.py) - a cutscene's time of day turns only the sun");
        // its sun colour at noon (row 2) lights the mixed particles (WorldRenderer.SetSunTint: PWClient 0x723520 lights
        // them with the table's sun colour, at least 0x0f0f0f - docs/EFFECTS.md)
        if (_skyTable != null)
        {
            var c = _skyTable.At(PwRemake.Sim.Content.SkyGradient.RowSun, PwRemake.View.World.DayLight.Noon);
            Renderer.SetSunTint(new Color((float)c.R, (float)c.G, (float)c.B));
        }
        return _skyTable;
    }

    double _cloudTod = -1e9;
    /// <summary>the fog of war clouds' colour: the original's sky manager colour (PWClient 0x727e00) - row 8 of the
    /// setting's sky gradient at the time of day (row 0 with fewer rows, 0x607090 without a table); the time: a
    /// cutscene's, the map's cycle, else the map's start time (the original's clock runs from it: GTime)</summary>
    void UpdateCloudTint()
    {
        if (View.Clouds is not { } cl) return;
        double tod = Stage?.Day?.Tod ?? (World.DayNight is { } spec ? spec.At(CycleViewTime ?? World.Time).Clock : Sim.Map.StartTime);
        if (Math.Abs(tod - _cloudTod) < 60) return;
        _cloudTod = tod;
        var t = SkyTable();
        if (t == null) { cl.SetTint(new Color(0x60 / 255f, 0x70 / 255f, 0x90 / 255f)); return; }
        var (r, g, b, a) = t.At(t.Height >= 9 ? 8 : 0, tod);
        cl.SetTint(new Color((float)r, (float)g, (float)b, (float)a));
    }
    /// <summary>
    /// A cutscene's time of day lights the scene (its day time items: SessionSequenceStage.Day): the sun's way and the
    /// original's sky table of the map's setting (assets/terrain/&lt;Setting&gt;/sky.json, the prepare tool's
    /// tools/prepare/sky.py; without it only the sun's direction changes) - PwRemake.View.World.DayLight, docs/GAME_SESSION.md
    /// "Cutscenes". The game's own clock does not change the light (the remake's daylight, as the JS); a mode's own
    /// night (the sequel's) stays under it. Updated when the scene's clock moved a game minute (0.4 s of the scene).
    /// </summary>
    void UpdateDayLight()
    {
        var d = Stage?.Day;
        var cur = Renderer.DayLightNow;
        // a map's own day-night cycle (GameSession.DayNight.cs) when no cutscene sets a time of day
        if (d == null && World.DayNight != null) { CycleLight(); return; }
        if (d != null) _cycDark = -1;                     // (after the scene: the cycle's light again)
        if (d == null) { if (cur != null) { Renderer.SetDayLight(null); View.SetSpriteLight(null); } return; }
        if (cur != null && Math.Abs(cur.Tod - d.Value.Tod) < 60) return;
        var dl = PwRemake.View.World.DayLight.At(d.Value.Tod, SkyTable());
        Renderer.SetDayLight(dl);
        View.SetSpriteLight((dl.SunMul + dl.AmbientMul) / 2);
    }

    /// <summary>night for the ambience: a cutscene's clock (its day time items), else the world's</summary>
    bool IsNight() => Stage?.Day is { } d && World.NightFn == null ? PwRemake.UI.Mission.Logic.SceneDayTime.IsNight(d.Tod) : World.IsNight();

    /// <summary>the biomes around the camera (a map with a biome layer; BiomeAmbience)</summary>
    public BiomeAmbience? Biomes { get; private set; }

    // the ambience and the music (main.js 'audio' step, homes, atmoState)
    void UpdateAudio(double dt)
    {
        var A = GameAudio.Current;
        if (A == null) return;
        var cam = Renderer.Camera.GlobalPosition;
        var tg = Camera.Target;
        double above = cam.Y - View.Ground.At(cam.X, cam.Z);
        var p = above > 20 ? tg : cam;
        var S = Sim.Map;
        int mat = S.SoundMat != null ? S.SoundMat(p.X, p.Z) : 2;
        var home = Music.HomeClass.GetValueOrDefault(Me.Tribe);
        try
        {
            // a map with biomes: the ambience of the biome around the camera, its own material numbers
            if (Biomes is { Active: true } bi) { A.SetAmbience(bi.Ambience(S.Atmos)); mat = bi.SoundMat(mat); }
            A.UpdateWorld(dt, World.Time, new V3(tg.X, tg.Y, tg.Z), Camera.Yaw,
                home == null ? null : () => World.Buildings.Cast<Entity>().Concat(World.Units).Where(e => e.Alive && e.Owner == Me && e.Name == home).Select(e => e.Pos),
                Atmo.State(mat, above > 12, !IsNight()), above);
        }
        catch (Exception e) { ErrOnce("audio", "the sound update failed", e); }
    }

    // ======================================================================================== fog of war (main.js updateFow)
    /// <summary>does the human player see what player p sees? own things; in a campaign also those of players who are
    /// friends in both directions (main.js G.sees; World.FogAllies)</summary>
    public bool Sees(Player? p) => p != null && (p == Me || (World.FogAllies && Me.IsFriend(p) && p.IsFriend(Me)));

    /// <summary>the fog of war on the screen: the world updates it at the end of its steps (World.FogUpdate: every 0.2 s,
    /// after a reveal, a debug tool); the view takes it when it changed (or now: force)</summary>
    public void UpdateFow(bool force)
    {
        if (!force && Fog.Version == _fogShown) return;
        _fogShown = Fog.Version;
        View.SetFog(Fog);
    }

    // ======================================================================================== commands
    readonly Dictionary<Command, Action<string?>> _waiting = new();
    int _resultsSeen;

    /// <summary>submits a command of the human player; the callback gets its result after the step (null = done)</summary>
    public void Submit(Command c, Action<string?>? result = null)
    {
        c.Player = Me.Id;
        // a networked game: the end of a scene is this machine's vote; the host ends it for everybody (docs/MULTIPLAYER.md)
        if (Net != null && c is SceneEndCommand se) { Net.SceneDone(se.Kind, se.Kind == "dialog" ? se.Token : _lockstep?.SequenceToken ?? 0, se.Timeline); return; }
        if (result != null) _waiting[c] = result;
        Commands.Submit(c);
    }

    void DeliverCommandResults()
    {
        var list = World.CommandResults;
        if (_resultsSeen > list.Count) _resultsSeen = 0;
        for (int i = _resultsSeen; i < list.Count; i++)
        {
            var r = list[i];
            if (_waiting.Remove(r.Command, out var cb)) { try { cb(r.Reason); } catch (Exception e) { L.Error("command result", e); } }
        }
        _resultsSeen = list.Count;
    }

    // ======================================================================================== selection, camera
    void OnSelectionChanged(Selection sel, bool silent)
    {
        Input?.SelectionChanged();
        if (!silent && sel.Count > 0) PlaySelected(sel.Items);
        HudView?.SelectionChanged(silent);
        SelectionChanged?.Invoke(sel);
    }

    public void LookAt(double x, double z) { Camera.X = x; Camera.Z = z; }
    /// <summary>main.js G.centerOn</summary>
    public void CenterOn(Entity? e) { if (e != null) LookAt(e.Pos.X, e.Pos.Z); }

    /// <summary>a point of the world on the screen (null: behind the camera)</summary>
    public Vector2? ToScreen(Vector3 p)
    {
        var c = Renderer.Camera;
        if (c.IsPositionBehind(p)) return null;
        return c.UnprojectPosition(p);
    }
    /// <summary>the ground under a screen point (null: none)</summary>
    public Vector3? GroundAt(Vector2 screen) => Input.GroundPoint(screen);

    // ======================================================================================== menus, pause, the end
    public void OpenMenu()
    {
        if (_menu != null || Over && Presenter.MenuOpen) return;
        MenuShot();                       // (the game view under the menu: a save's thumbnail)
        MenuOpen = true;
        Presenter.MenuOpen = true;
        _menu = new PauseMenu(this);
        AddChild(_menu);
    }
    public void CloseMenu()
    {
        _menu?.QueueFree();
        _menu = null;
        MenuOpen = false;
        Presenter.MenuOpen = false;
    }
    public bool MenuShown => _menu != null;
    public PauseMenu? MenuNode => _menu;

    /// <summary>one step while the game stands still (a debug tool from the menu: its command is applied now)</summary>
    public void StepWhilePaused()
    {
        if (Net != null) return;                  // (a networked game steps on the host's turns only)
        Entities.BeforeStep();
        try { Sim.Step(); } catch (Exception e) { L.Error("simulation step failed", e); }
        var evs = World.DrainEvents();
        try { HandleEvents(evs); } catch (Exception e) { L.Error("feedback", e); }
        if (evs.Count > 0) { HudView.OnEvents(evs); FrameEvents?.Invoke(evs); }
        DeliverCommandResults();
        UpdateFow(true);
    }

    /// <summary>main.js G.togglePause (Pause key)</summary>
    public void TogglePause()
    {
        if (Net != null) { Net.Net.RequestPause(!Net.Net.HostPaused); return; }        // (everybody: the host holds the turns)
        PausedByPlayer = !PausedByPlayer; Message(PausedByPlayer ? "Paused" : "Resumed");
    }

    /// <summary>the end screen's buttons and the pause menu: "next" | "retry" | "load" | "menu" | "exit"</summary>
    public void EndAction(string a)
    {
        L.Info($"end of the game: {a}");
        if (a == "stats") { ShowEndStats(); return; }
        if (a == "watch" && Skirmish != null) { Presenter.CloseEnd(); return; }
        if (Net != null || NetSession.Current != null)
        {
            // a networked game: leaving it (Retry and the next mission are the host's new lobby)
            if (a == "exit") { NetSession.Close(); Boot.Instance.RequestQuit(); return; }
            NetSession.Close();
            Boot.Instance.Show(new MultiplayerMenu());
            return;
        }
        if (Skirmish != null && SkirmishEndAction(a)) return;
        if (Mission != null && EditorEndAction(a)) return;                // (the map editor's Test map of a map with triggers)
        if (Sequel != null && SequelEndAction(a)) return;
        switch (a)
        {
            case "next":
                // a user campaign's mission: its next one (CustomPlay); else the next mission of the same group - the
                // original campaign, the Booster Pack, MIRAGE's own missions (MissionGroups.NextInGroup) -, at its end
                // the campaign menu's tab of the group
                if (Start.CustomCampaign != null) { CustomPlay.Continue(Start, _missionWon); break; }
                var next = NextMission();
                if (next == null) Boot.Instance.Show(new CampaignMenu(GroupTab()));
                else StartMission(next.Id, next.Folder);
                break;
            case "retry": StartMission(Start.Mission, Start.MissionFolder); break;
            case "exit": Boot.Instance.RequestQuit(); break;
            case "load": Boot.Instance.Show(new LoadGameScreen()); break;           // (the end screen's Load Game: the load window)
            default: Boot.Instance.Show(new CampaignMenu(Mission != null ? GroupTab() : null)); break;
        }
    }

    void StartMission(int id, string folder)
    {
        var cfg = GameApp.Config!;
        var entry = PwRemake.UI.MissionEntry.Load(cfg.MissionsDir).FirstOrDefault(m => m.Id == id);
        if (entry == null) { L.Error($"mission {id} is not in the campaign"); Boot.Instance.Show(new CampaignMenu()); return; }
        var s = new GameStart
        {
            Configuration = Start.Configuration, Mode = Start.Mode, Mission = id, MissionFolder = folder, Tribe = Start.Tribe,
            Difficulty = Start.Difficulty, Debug = Start.Debug, Seed = Start.Seed,
            Army = id == Start.Mission ? Start.Army : null,                  // (retry: the same army; the next mission: its army screen)
            Gameplay = Start.Gameplay,
            CustomCampaign = id == Start.Mission ? Start.CustomCampaign : null,      // (retry of a user campaign's mission: still the campaign's)
        };
        // your colour (docs/CAMPAIGN.md "Your colour"): retry the same, the next mission the campaign's remembered one if free there
        s.Color = id == Start.Mission ? Start.Color : CampaignColor.For(entry.FolderPath(cfg.MissionsDir), CampaignColor.Key(s));
        if (id == Start.Mission) Boot.Instance.Show(new LoadingScreen(s, entry));
        else CampaignMenu.StartWithArmy(entry, s, () => new CampaignMenu(GroupTab()));
    }

    /// <summary>--skip-cine: cutscenes and dialogues end at once (tests)</summary>
    public bool SkipCutscenes => _args.ContainsKey("skip-cine");

    /// <summary>the trigger engine ended the mission (main.js G.endMission): no combat music behind the end screen,
    /// the end screen after the delay (real time)</summary>
    public void MissionEnded(bool won, string text, double delay)
    {
        L.Info($"mission {(won ? "won" : "lost")}: {text}");
        RememberCredits(won);
        _missionWon = won;
        if (won) CustomPlay.Won(Start);                                    // (a user campaign's mission: the next one opens)
        try { GameAudio.Current?.EndGame(); } catch (Exception) { }
        int t = (int)Math.Floor(World.Time);
        Presenter.EndStats = ($"{t / 60}:{t % 60:00}", Me.Kills, Me.Lost);
        void Show() { if (IsInstanceValid(this)) Presenter.EndMission(won, text); }
        if (delay <= 0) Show();
        else GetTree().CreateTimer(delay, true).Timeout += Show;
    }

    /// <summary>a line on the message bar (main.js G.hud.message)</summary>
    public void Message(string text, string kind = "")
    {
        if (string.IsNullOrEmpty(text)) return;
        L.Debug(() => $"message [{kind}] {text}");
        if (Hud != null) Hud.Message(text, kind); else Overlay?.Message(text, kind);
        MessagePosted?.Invoke(text, kind);
    }

    /// <summary>a marker on the minimap (the HUD's minimap draws it)</summary>
    public void Minimap(string kind, double x, double z) { HudView?.Minimap?.AddMarker(kind, x, z); MinimapMarker?.Invoke(kind, x, z); }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k && k.Keycode == Key.F12)
        {
            // (Boot saves the screenshot)
        }
    }
}
