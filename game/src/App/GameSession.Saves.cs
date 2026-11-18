// Saved games in the running game (docs/SAVES.md): recording from the first step, saving (pause menu, quick save F5 /
// Ctrl+S, autosave every N minutes), loading (the loading screen replays the save on a worker thread while the models
// load), and what the player's view gets back after loading - camera, selection, control groups and camera views, the
// quest log's read marks and the minimap markers, the info bar, the scene that was on show, a MUSI track.
//
// The browser remake's main.js saveGame / loadSaved / quickSave / quickLoad / autosave (IngameScreen.usl
// UpdateAutosaveTimer) and the restore part of game/save.js (camera, selection, MissionUI read / unread / bar /
// markers, the engine's resumeShows), pw-exporter 98c7703. The save itself is not the JS's snapshot: SaveGame.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using PwRemake.UI;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.App;

public partial class GameSession
{
    static readonly Logger LS = Log.For("Game.Saves");
    /// <summary>the saved game this session continues (null: a new game)</summary>
    public SaveGame? Resume { get; }
    PresenterTap? _tap;
    /// <summary>the mission UI's adapter behind the tap that records its answers to the mission (PresenterTap)</summary>
    public PresenterTap Tap => _tap ??= MakeTap();         // (a start with seats: LockstepPresenter between - GameSession.Net.cs)
    /// <summary>how the simulation runs: the game's flags, or a loaded save's</summary>
    public SimFlags Flags => Resume?.Flags ?? SimFlags.Game;
    GameRecorder? _recorder;
    SaveReplay? _replay;
    double _autosaveT;
    bool _autosaveDue;
    byte[]? _menuShot;
    double _saveAt = -1;

    public const string QuickFile = "quicksave" + SaveGame.Extension, AutoFile = "autosave" + SaveGame.Extension;

    // ======================================================================================== recording, replaying
    void StartRecording()
    {
        _recorder = new GameRecorder(World, Sim.StateHash, Tap);
        if (_args.TryGetValue("save-at", out var sa) && Json.TryNum(sa, out var t)) _saveAt = t;
    }

    /// <summary>a saved game: its steps on a worker thread (the models load meanwhile on the main thread)</summary>
    (SaveReplay Rep, Task Task)? StartReplay()
    {
        if (Resume == null) return null;
        var rep = _replay = new SaveReplay(Resume, World, Sim.StateHash, Tap);
        rep.Begin();
        LS.Info($"restoring \"{Resume.Header.Name}\": {Resume.Steps} steps ({SaveHeader.Clock(Resume.Header.GameTime)}), saved by {Resume.Header.Build}");
        return (rep, Task.Run(() => { while (rep.StepOnce()) { } }));
    }

    IEnumerable<(double, string)> WaitReplay((SaveReplay Rep, Task Task) r)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string total = SaveHeader.Clock(Resume!.Header.GameTime);
        while (!r.Task.IsCompleted)
            yield return (ModelsEnd + (TerrainEnd - ModelsEnd) * 0.5 * r.Rep.Progress, $"Restoring the saved game {SaveHeader.Clock(World.Time)} / {total}");
        if (r.Task.IsFaulted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(r.Task.Exception!.GetBaseException()).Throw();
        r.Rep.End();
        LS.Info($"replay: {World.StepNo} steps in {sw.ElapsedMilliseconds} ms after the models ({r.Rep.Verified} hashes equal{(r.Rep.Diverged ? "; " + r.Rep.Problem : "")}); state hash {Sim.StateHash():x16}");
    }

    // ======================================================================================== after loading
    /// <summary>the player's view as it was saved; the scenes on show start again (engine.js resumeShows)</summary>
    void AfterLoad()
    {
        var v = Resume!.View;
        try
        {
            if (v?["camera"] is JsonArray c && c.Count >= 4) { Camera.X = c[0].Num(); Camera.Z = c[1].Num(); Camera.Yaw = c[2].Num(); Camera.Dist = Camera.TDist = c[3].Num(); Camera.Update(0.016); }
            if (v?["selection"] is JsonArray sel) Selection.Replace(sel.Select(x => World.ById(x.Int())).Where(e => e is { Alive: true }).Cast<Entity>().ToList(), true);
            Input.RestoreState(v?["input"]);
            Presenter.State.RestoreView(v?["mission"]);
            if (v?["pausedByPlayer"].Bool() == true) PausedByPlayer = true;
        }
        catch (Exception e) { LS.Error("the saved view could not be restored", e); }
        var E = Mission?.Engine;
        if (E != null)
        {
            Presenter.SetInfoBar(E.InfoBarShown);
            // the scenes that were on show begin again (the JS: resumeShows); the UI ends them by command as always
            if (E.SeqRunning != null)
            {
                var play = Mission!.Host.SeqPlay ?? new SequencePlay { Request = E.SeqRunning };
                if (!PresenterLink.PlaySequence(play)) Submit(new SceneEndCommand { Kind = "sequence" });
            }
            foreach (var (token, path) in E.DialogsRunning.ToList())
                if (!PresenterLink.PlayDialog(new PwRemake.Sim.Campaign.DialogPlay { Token = token, Path = path })) Submit(new SceneEndCommand { Kind = "dialog", Token = token });
        }
        if (Tap.ReplayedMusic is var (file, loop)) PresenterLink.PlayMusic(file, loop);
        var rep = _replay;
        if (rep != null && rep.Diverged)
            Message($"This game was saved by another version of the remake: it could be restored up to {SaveHeader.Clock(World.Time)} of {SaveHeader.Clock(Resume.Header.GameTime)} (see the log).", "bad");
        else Message($"Game loaded: {Resume.Header.Name}", "good");
        L.Info($"LOADED {Resume.Header.Name}: step {World.StepNo}, game time {World.Time:0.0} s, state hash {Sim.StateHash():x16}{(rep?.Diverged == true ? ", diverged: " + rep.Problem : "")}");
    }

    // ======================================================================================== saving
    /// <summary>why the game cannot be saved now (null: it can)</summary>
    public string? CannotSave()
    {
        if (_recorder == null) return "The game is not recorded.";
        if (Net != null && NetLink is { Hosting: false }) return "In a network game only the host saves.";
        if (Over) return Skirmish != null ? "The game is over." : "The mission is over.";
        if (Cinematic) return "Not during a cutscene.";
        if (_replay is { Done: false }) return "The game is still being restored.";
        if (_args.ContainsKey("test-placement")) return "--test-placement changes the world directly: such a run cannot be replayed.";
        return null;
    }

    /// <summary>a short name for a save of this game: the mission's title and the game time</summary>
    public string SaveTitle() => $"{Sim.Title} {SaveHeader.Clock(World.Time)}";

    /// <summary>the player's view for a save (docs/SAVES.md)</summary>
    JsonObject SaveView() => new()
    {
        ["camera"] = new JsonArray(Camera.X, Camera.Z, Camera.Yaw, Camera.TDist),
        ["selection"] = new JsonArray(Selection.Items.Where(e => e.Alive).Select(e => (JsonNode)e.Id).ToArray()),
        ["input"] = Input.SaveState(),
        ["mission"] = Presenter.State.SaveView(),
        ["pausedByPlayer"] = PausedByPlayer,
    };

    /// <summary>saves the game now: kind "manual" (into its own file, or `file`), "quick" or "auto" (their fixed files);
    /// returns the file. Throws when it cannot (CannotSave, a disk error).</summary>
    public string SaveGameNow(string name, string kind = "manual", string? file = null)
    {
        var why = CannotSave();
        if (why != null) throw new InvalidOperationException(why);
        // (a skirmish: its setup also as the save's "setup" - GameStart.Skirmish carries it too)
        var s = _recorder!.Capture(Start, Flags, Start.Skirmish != null ? System.Text.Json.JsonSerializer.SerializeToNode(Start.Skirmish) : null, SaveView());
        s.Header.Name = string.IsNullOrWhiteSpace(name) ? SaveTitle() : name.Trim();
        s.Header.Kind = kind;
        s.Header.Build = GameApp.Version;
        s.Header.Title = Sim.Title;
        s.Header.ConfigurationTitle = Config.Title;
        s.Thumbnail = MenuShown && _menuShot != null ? _menuShot : Thumbnail();
        file ??= Path.Combine(AppPaths.SavesDir, kind == "quick" ? QuickFile : kind == "auto" ? AutoFile : SaveGame.FileName(s.Header.Name));
        s.Write(file);
        L.Info($"SAVED {file}: step {World.StepNo}, game time {World.Time:0.0} s, state hash {Sim.StateHash():x16}, {s.Commands.Count} commands, {s.Answers.Count} answers");
        return file;
    }

    /// <summary>a small picture of the game view (JPEG, 256 px wide), or null</summary>
    byte[]? Thumbnail()
    {
        try
        {
            var img = GetViewport().GetTexture().GetImage();
            if (img == null || img.IsEmpty()) return null;
            int w = 256, h = Math.Max(1, (int)Math.Round(256.0 * img.GetHeight() / Math.Max(1, img.GetWidth())));
            img.Resize(w, h, Image.Interpolation.Bilinear);
            return img.SaveJpgToBuffer(0.8f);
        }
        catch (Exception e) { LS.Warn($"no thumbnail: {e.Message}"); return null; }
    }

    /// <summary>the pause menu opens: the picture of the game under it (a save from the menu takes this one)</summary>
    void MenuShot() => _menuShot = Thumbnail();

    /// <summary>F5 / Ctrl+S (input.js quickSave)</summary>
    public void QuickSave()
    {
        try { SaveGameNow("Quick save", "quick"); Message("Quick saved", "good"); }
        catch (Exception e) { Message("Not saved: " + e.Message, "bad"); if (e is not InvalidOperationException) LS.Error("quick save", e); }
    }

    /// <summary>F8 (input.js quickLoad)</summary>
    public void QuickLoad()
    {
        if (Net != null) { Message("Not in a network game.", "bad"); return; }
        var f = Path.Combine(AppPaths.SavesDir, QuickFile);
        if (!File.Exists(f)) { Message("There is no quick save yet", "bad"); return; }
        LoadGame(f, m => Message(m, "bad"));
    }

    /// <summary>once per frame: the autosave (IngameScreen.usl UpdateAutosaveTimer: every N minutes of real time; not
    /// while the game is paused, a menu or the quest log is open or a scene shows - then at the next moment it can) and
    /// --save-at</summary>
    void SavesStep(double realDt)
    {
        if (_saveAt >= 0 && World.Time >= _saveAt && CannotSave() == null)
        {
            _saveAt = -1;
            try { SaveGameNow("Test save", "manual", _args.TryGetValue("save-to", out var to) && to.Length > 0 ? to : null); }
            catch (Exception e) { LS.Error("--save-at", e); }
        }
        int every = GameApp.Settings.Autosave ? GameApp.Settings.AutosaveMinutes : 0;
        if (every <= 0 || Over) { _autosaveT = 0; _autosaveDue = false; return; }
        _autosaveT += realDt;
        if (_autosaveT >= Math.Clamp(every, 1, 120) * 60) { _autosaveT = 0; _autosaveDue = true; }
        if (!_autosaveDue || Paused || MenuOpen || Presenter.IsBusy || Presenter.State.Scene != null || CannotSave() != null) return;
        _autosaveDue = false;
        try { SaveGameNow("Autosave", "auto"); Message(Rules.Ui("_NT_SavingGame", "Saving game ..."), "good"); }
        catch (Exception e) { LS.Error("autosave", e); }
    }

    // ======================================================================================== loading
    /// <summary>loads a saved game: its configuration, the loading screen with the save's start and its replay. Problems
    /// (another format version, a missing mission) go to `say` (null: the log) and nothing changes.</summary>
    public static bool LoadGame(string file, Action<string>? say = null)
    {
        void Say(string m) { LS.Warn($"{file}: {m}"); say?.Invoke(m); }
        SaveGame s;
        try { s = SaveGame.Read(file); }
        catch (SaveFormatException e) { Say(e.Message); return false; }
        catch (Exception e) { Say("The saved game cannot be read: " + e.Message); return false; }
        if (s.Start.Mode is not (GameMode.Campaign or GameMode.Skirmish or GameMode.Sequel or GameMode.Scenario)) { Say($"Saved games of a {s.Start.Mode} cannot be loaded yet."); return false; }
        var cfg = GameApp.Configurations.FirstOrDefault(c => c.Id == s.Start.Configuration);
        if (cfg == null || cfg.Problems().Count > 0) { Say($"This game was saved with the configuration \"{s.Header.ConfigurationTitle}\" ({s.Start.Configuration}), which is not available here."); return false; }
        if (GameApp.Config != cfg) { GameApp.UseConfiguration(cfg); GameApp.Settings.Save(); }
        if (s.Start.Mode == GameMode.Skirmish)
        {
            // a skirmish: its setup (GameStart.Skirmish, else the save's "setup"), rebuilt and replayed (docs/SKIRMISH.md)
            s.Start.Skirmish = SkirmishSession.SetupOf(s.Start, s.Setup);
            if (s.Start.Skirmish == null) { Say("This skirmish was saved without its setup."); return false; }
            if (s.Start.Skirmish.Map != "" && SkirmishMaps.Resolve(cfg, s.Start.Skirmish.Map) == null) { Say($"The map of this saved game is not in the installation: {s.Start.Skirmish.Map}"); return false; }
            LS.Info($"loading {file}: {s.Header.Title}, {SaveHeader.Clock(s.Header.GameTime)}");
            Boot.Instance.Show(LoadingScreen.ForSkirmish(s.Start, s));
            return true;
        }
        if (s.Start.Mode is GameMode.Sequel or GameMode.Scenario)
        {
            // a mission of the remake's own campaigns (docs/SEQUEL.md): made again from its folder and replayed
            var se = PwRemake.Sim.Sequel.SequelCatalog.Find(PwRemake.Sim.Sequel.SequelSession.KindOf(s.Start)!, s.Start.Mission);
            if (se == null) { Say($"The mission {s.Start.Mission} of this saved game is not in the game ({s.Start.Mode})."); return false; }
            LS.Info($"loading {file}: {s.Header.Title}, {SaveHeader.Clock(s.Header.GameTime)}");
            Boot.Instance.Show(LoadingScreen.ForSequel(s.Start, se, s));
            return true;
        }
        var entry = PwRemake.UI.MissionEntry.Load(cfg.MissionsDir).FirstOrDefault(m => m.Id == s.Start.Mission && (s.Start.MissionFolder.Length == 0 || m.Folder == s.Start.MissionFolder))
                    ?? PwRemake.UI.MissionEntry.Load(cfg.MissionsDir).FirstOrDefault(m => m.Id == s.Start.Mission);
        if (entry == null) { Say($"The mission {s.Start.Mission} of this saved game is not in {cfg.Title}."); return false; }
        LS.Info($"loading {file}: {s.Header.Title}, {SaveHeader.Clock(s.Header.GameTime)}");
        Boot.Instance.Show(new LoadingScreen(s.Start, entry, s));
        return true;
    }
}

/// <summary>The mission UI's link to the trigger engine in the game: the scenes' ends as commands (SceneEndCommand), so
/// they reach the triggers at a step and are recorded with it (docs/SAVES.md); the quests and timers it reads from the
/// engine.</summary>
public sealed class SessionEngineLink : IMissionEngineLink
{
    readonly GameSession S;
    readonly TriggerEngine E;
    public SessionEngineLink(GameSession s, TriggerEngine e) { S = s; E = e; }
    public void EndSequence() => S.Submit(new SceneEndCommand { Kind = "sequence", Timeline = S.Stage.TimelineRequest != null && S.Stage.TimelineRequest == E.SeqRunning });
    public void EndDialog(int token) => S.Submit(new SceneEndCommand { Kind = "dialog", Token = token });
    public IEnumerable<QuestState> Quests => E.Quests;
    public IEnumerable<LevelTimer> Timers => E.Timers.Values;
    public double Boni => E.Boni;
}
