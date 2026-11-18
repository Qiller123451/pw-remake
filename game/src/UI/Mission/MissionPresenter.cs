// The mission UI of a campaign mission in the game: quest button and quest log, news ticker, dialogue box, info bar
// and timers, the title card, cutscenes (letterbox, subtitles, speech, films) and the end of mission screen - over
// the HUD. One node per mission. The campaign host forwards IMissionHost's presentation calls to it with the same
// names and arguments (one line per call):
//
//   void QuestChanged(QuestState q, string change) => Presenter.QuestChanged(q, change);
//   bool PlayDialog(string path, int token)         => Presenter.PlayDialog(path, token);
//   bool PlaySequence(SequenceRequest r)            => Presenter.PlaySequence(r);
//   bool IsBusy                                     => Presenter.IsBusy;   ... (docs/MISSION_UI.md "Wiring")
//
// and binds the engine once (Bind(engine)): the presenter calls engine.EndDialog(token) / EndSequence() when a scene
// is over, reads the quests and the level timers. The session calls Tick(dt) with the game time of every simulation
// step (dialogue timing); the real-time part runs in _Process. What the HUD, the minimap and the world view read:
// Markers, QuestionMarks, MarkerLit, RingPhase, and the hooks Stage (cutscene camera / actors), ObjectPos, OnPause,
// OnCinematic, OnEndAction.
//
// The logic is MissionUiState / MissionUiBridge / CutsceneTimeline (Logic/, engine-free, tested); this file is the
// Godot side: the views (NewsTickerView, DialogBoxView, TopStackView, CutsceneView, QuestLogWindow, EndScreenWindow),
// the sounds through GameAudio, the keys.
// Ported from remake/src/ui/mission.js + cutscene.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Scripting;
using PwRemake.UI.Mission.Logic;
using PwRemake.View.Audio;

namespace PwRemake.UI.Mission;

public partial class MissionPresenter : CanvasLayer
{
    static readonly Logger L = PwRemake.Sim.Core.Log.For("UI.Mission");

    public readonly MissionUiState State;
    public readonly MissionUiBridge Bridge;
    readonly GodotMissionOutput _out;
    readonly AssetSource? _assets;

    // ---- hooks for the game around it
    /// <summary>The UI pauses / unpauses the game (cutscenes, the quest log): the session's pause.</summary>
    public Action<bool>? OnPause { get => _out.OnPause; set => _out.OnPause = value; }
    /// <summary>A cutscene starts (true) / ends (false): the HUD goes / comes back, input to the world stops.</summary>
    public Action<bool>? OnCinematic;
    /// <summary>A button of the end screen: "next", "retry", "load", "menu".</summary>
    public Action<string>? OnEndAction;
    /// <summary>The sequencer's camera, actors, effects (the world view; null: the scenes play over the frozen view).</summary>
    public ISequenceStage? Stage { get => State.Stage; set => State.Stage = value; }
    /// <summary>Where an object is (map x, y); markers follow it.</summary>
    public Func<int, (double X, double Y)?> ObjectPos { get => State.ObjectPos; set => State.ObjectPos = value; }
    /// <summary>The camera when a sequence is over (the engine's SequenceFinished: r.Camera unless r.SnapBack).</summary>
    public Action<SequenceRequest>? OnSequenceFinished { get => Bridge.OnSequenceFinished; set => Bridge.OnSequenceFinished = value; }
    /// <summary>Tick the dialogue timing with the drawn frames (the test scene; the game calls Tick per step).</summary>
    public bool TickWithFrames;
    /// <summary>The interface scale (the HUD's): 1 at 1600 x 900.</summary>
    public float UiScale = 1;
    /// <summary>Where the objectives panel (info bar, timers) hangs: its top right corner on screen - the HUD sets the
    /// right end of its top bar's last button (Chat), under it. null: the screen's top right.</summary>
    public Vector2? ObjectivesAnchor;
    /// <summary>The objectives panel is folded away (its toggle, Shift+J); kept in the settings.</summary>
    public bool ObjectivesHidden { get; private set; }
    /// <summary>Optional statistics for the end screen (time, enemies killed, units lost).</summary>
    public (string Time, int Kills, int Lost)? EndStats;

    // ---- views
    Control _root = null!;
    NewsTickerView _ticker = null!;
    DialogBoxView _dialog = null!;
    TopStackView _top = null!;
    TitleCardView _title = null!;
    CutsceneView _cine = null!;
    QuestButton _questBtn = null!;
    QuestLogWindow? _log;
    EndScreenWindow? _end;
    bool _cineShown;

    /// <param name="data">the mission's UI data (MissionUiData.Load(folder) / FromJson(LuaMission.World, Mission, texts))</param>
    /// <param name="assets">the configuration's files (HUD art, sounds, films)</param>
    public MissionPresenter(MissionUiData data, AssetSource? assets)
    {
        Name = "MissionUI";
        Layer = 20;
        ProcessMode = ProcessModeEnum.Always;
        _assets = assets;
        _out = new GodotMissionOutput(assets);
        State = new MissionUiState(data, _out);
        Bridge = new MissionUiBridge(State) { OnEndMission = ShowEnd, OnSound = (n, at) => _out.Sound(n), OnMusic = (f, loop) => GameAudio.Current?.Core.Mus.Trigger(f, loop) };
        try { State.MentorTexts = PwRemake.App.GameApp.Settings.MentorTexts; State.Difficulty = PwRemake.App.GameApp.Settings.Difficulty; State.Subtitles = PwRemake.App.GameApp.Settings.Subtitles; State.CinemaBars = PwRemake.App.GameApp.Settings.CutsceneBars; ObjectivesHidden = PwRemake.App.GameApp.Settings.ObjectivesHidden; } catch (Exception) { /* no app (test scene) */ }
    }

    /// <summary>The engine the scenes report back to (EndDialog / EndSequence), whose quests and timers are shown.</summary>
    public void Bind(TriggerEngine engine) => Bridge.Bind(engine);
    public void Bind(IMissionEngineLink engine) => Bridge.Bind(engine);

    // ================================================================ IMissionHost's presentation calls
    public void QuestChanged(QuestState quest, string change) => Bridge.QuestChanged(quest, change);
    public void SetQuestionMark(string guid, string state, string? tooltip) => Bridge.SetQuestionMark(guid, state, tooltip);
    public void ShowMarker(MapMarker m) => Bridge.ShowMarker(m);
    public void RemoveMarker(string id) => Bridge.RemoveMarker(id);
    public void SetInfoBar(string text) => Bridge.SetInfoBar(text);
    public void PlaySound(string name, MapPos? at) => Bridge.PlaySound(name, at);
    public void PlayMusic(string file, bool loop) => Bridge.PlayMusic(file, loop);
    public bool IsBusy => Bridge.IsBusy;
    public bool PlaySequence(SequenceRequest r) => Bridge.PlaySequence(r);
    public void SequenceFinished(SequenceRequest r) => Bridge.SequenceFinished(r);
    public bool PlayDialog(string path, int token) => Bridge.PlayDialog(path, token);
    public void EndMission(bool won, string text) => Bridge.EndMission(won, text);

    // ================================================================ data for the HUD, minimap and world view
    /// <summary>The minimap markers (map coordinates, colour, kind, extended = also a ring on the ground).</summary>
    public IReadOnlyList<MarkerState> Markers => State.Markers;
    /// <summary>Is the marker lit now (ttl / repeats / interval)?</summary>
    public static bool MarkerLit(MarkerState m) => MissionUiState.MarkerOn(m);
    /// <summary>The ring running outwards, 0..1 (minimap: speed 0.85, the world's ring: 0.7).</summary>
    public double RingPhase(MarkerState m, double speed = 0.85) => State.RingPhase(m, speed);
    /// <summary>The question marks by guid (Visible, Model, Glyph, colours, map position, Tooltip).</summary>
    public IReadOnlyDictionary<string, QuestionMarkState> QuestionMarks => State.QuestionMarks;
    /// <summary>A cutscene is on screen (the HUD hides, the world takes no input).</summary>
    public bool Cinematic => State.Cine != null;

    // ================================================================ beyond the contract
    public void ShowTitle() => State.ShowTitle();
    public void OpenLog(string? quest = null) => State.OpenLog(quest);
    public void CloseLog() => State.CloseLog();
    public void ToggleLog() { if (State.LogOpen) State.CloseLog(); else OpenLog(); }
    /// <summary>Folds the objectives panel away or back and remembers it (AppSettings.ObjectivesHidden).</summary>
    public void ToggleObjectives()
    {
        ObjectivesHidden = !ObjectivesHidden;
        try { PwRemake.App.GameApp.Settings.ObjectivesHidden = ObjectivesHidden; PwRemake.App.GameApp.Settings.Save(); } catch (Exception) { /* no app (test scene) */ }
    }
    /// <summary>A line on the ticker (kind "" | new | good | bad | story).</summary>
    public void News(string text, string kind = "", string? icon = null) => State.News_("", text, kind, icon);
    /// <summary>The game time of a simulation step (dialogue timing, markers).</summary>
    public void Tick(double dt) => State.Tick(dt);
    /// <summary>The quest button (the HUD may move it into its top row, left of "Menu").</summary>
    public Control QuestButtonControl => _questBtn;
    /// <summary>Another menu of the game is open: cutscenes wait, the log does not open.</summary>
    public bool MenuOpen { get => State.MenuOpen; set => State.MenuOpen = value; }

    public override void _Ready()
    {
        HudArt.Init(_assets);
        _root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = PwTheme.Theme };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        _top = new TopStackView(State) { OnToggle = ToggleObjectives };
        _ticker = new NewsTickerView(State);
        _dialog = new DialogBoxView(State);
        _questBtn = new QuestButton(State, () => ToggleLog());
        _title = new TitleCardView(State);
        _cine = new CutsceneView(State, _assets);
        foreach (var c in new Control[] { _top, _ticker, _dialog, _questBtn, _title, _cine }) _root.AddChild(c);
        // (the quest button: top right, left of the HUD's 95 px "Menu" button - IngameScreen.usl:309-326)
        _questBtn.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _questBtn.OffsetLeft = -95 - 69 - 2; _questBtn.OffsetRight = -95 - 2; _questBtn.OffsetTop = 2; _questBtn.OffsetBottom = 34;
    }

    public override void _Process(double delta)
    {
        double dt = Math.Min(delta, 1.0);          // (a long hitch does not jump a scene)
        if (TickWithFrames && !_out.Paused) State.Tick(dt);
        State.Update(dt);
        bool cine = State.Cine != null;
        if (cine != _cineShown) { _cineShown = cine; OnCinematic?.Invoke(cine); }
        // the HUD's parts go while a cutscene shows (body.cine .hud)
        _top.Visible = _ticker.Visible = _dialog.Visible = _questBtn.Visible = !cine;
        foreach (var c in new Control[] { _top, _ticker, _dialog, _questBtn }) c.Scale = new Vector2(UiScale, UiScale);
        _top.Anchor = ObjectivesAnchor; _top.Hidden = ObjectivesHidden;
        _top.Sync(); _ticker.Sync(); _dialog.Sync(); _questBtn.Sync(); _title.Sync(); _cine.Sync(dt);
        // the quest log window
        if (State.LogOpen && _log == null)
        {
            _log = new QuestLogWindow(State, () => State.CloseLog());
            _root.AddChild(_log);
        }
        else if (!State.LogOpen && _log != null) { _log.QueueFree(); _log = null; }
        if (_log != null && State.LogDirty) _log.Fill();
    }

    /// <summary>an end screen the game made itself (a skirmish: game/systems/gameover.js decided)</summary>
    public void ShowEndScreen(EndScreenInfo e) { State.End = e; ShowEnd(e); }
    /// <summary>the end screen hidden while the statistics show over the game (false) and back (true)</summary>
    public void SetEndVisible(bool on) { if (_end != null) _end.Visible = on; }
    /// <summary>"Keep playing" (a skirmish): the end screen goes, the game runs on</summary>
    public void CloseEnd()
    {
        _end?.QueueFree();
        _end = null;
        State.End = null;
        _out.Paused = false;
    }

    void ShowEnd(EndScreenInfo e)
    {
        GameAudio.Current?.EndGame();
        if (_end != null) _end.QueueFree();
        _end = new EndScreenWindow(e, EndStats, a => OnEndAction?.Invoke(a));
        _root?.AddChild(_end);
        _out.Paused = true;
    }

    public override void _Input(InputEvent ev)
    {
        if (ev is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (State.Cine != null)
            {
                // during a cutscene the player has no input: Space / Enter = next line, Esc = the end of the scene
                if (MenuOpen || k.Keycode is Key.F9 or Key.F10) return;
                GetViewport()?.SetInputAsHandled();   // first: ending a scene can take the presenter's windows away
                if (k.Keycode == Key.Escape) State.Skip();
                else if (k.Keycode is Key.Space or Key.Enter or Key.KpEnter) State.Next();
                return;
            }
            if (State.LogOpen && k.Keycode == Key.Escape) { GetViewport()?.SetInputAsHandled(); State.CloseLog(); return; }
            // Shift+J: the objectives panel folds away / comes back (the remake's; the original had no key for it)
            if (k.Keycode == Key.J && k.ShiftPressed && !k.CtrlPressed && !k.AltPressed && !k.MetaPressed && _end == null && !State.LogOpen)
            {
                GetViewport()?.SetInputAsHandled();
                ToggleObjectives();
                return;
            }
            // J: the quest log (the original has only the button; L is its weapon smith key)
            if (k.Keycode == Key.J && !k.CtrlPressed && !k.AltPressed && !k.MetaPressed && _end == null)
            {
                GetViewport()?.SetInputAsHandled();
                if (State.LogOpen) State.CloseLog(); else if (!MenuOpen) OpenLog();
            }
        }
    }

    public override void _ExitTree()
    {
        // whatever plays ends with the mission; the music's switches go back
        if (State.Cine != null || State.Scene != null) State.SkipAll();
        _out.Duck(false);
    }
}

/// <summary>The mission UI's sounds, pause and films in the game: GameAudio (Sfx) and the session's pause.</summary>
public sealed class GodotMissionOutput : IMissionUiOutput
{
    readonly AssetSource? _assets;
    public Action<bool>? OnPause;
    bool _paused, _ducked;

    public GodotMissionOutput(AssetSource? assets) { _assets = assets; }

    static AudioCore? A => GameAudio.Current?.Core;

    sealed class Snd : IUiSound
    {
        public ISoundHandle H = null!;
        public double Duration { get; init; }
        public bool Ended => H.Ended;
        public void Stop() => H.Stop();
    }

    public void Sound(string name) => A?.PlayFirst(new[] { name });

    public IUiSound? PlayFile(string file, double vol, bool voice, double offset, bool loop)
    {
        var a = A;
        if (a == null || !a.Ready()) return null;
        var buf = a.Buffer(file);
        if (buf == null) return null;
        var ch = voice ? AudioChannel.Voice : AudioChannel.Sfx;
        var h = loop ? a.Out.Start(buf, ch, vol / 100, 1, null, Math.Max(0, offset), true) : a.PlayFile(file, vol, null, 0, ch, 1, 0, offset);
        return h == null ? null : new Snd { H = h, Duration = buf.Duration };
    }

    public string? EventFile(string name)
    {
        var a = A;
        return a != null && a.Db.Events.TryGetValue(name, out var ev) && ev.S.Count > 0 ? ev.S[0].File : null;
    }

    /// <summary>Sound and music at 50 % while somebody speaks (mission.js duck: the effects bus and the music).</summary>
    public void Duck(bool on)
    {
        var a = A;
        if (a == null || on == _ducked) return;
        _ducked = on;
        a.DuckK = on ? 0.5 : 1;
        a.Mus.ApplyVolume();
        int i = AudioServer.GetBusIndex(AudioBuses.Effects);
        if (i >= 0) AudioServer.SetBusVolumeLinear(i, (float)(a.Vol.Sfx * (on ? 0.5 : 1)));
    }

    public void MusicSequence(bool on) => A?.Mus.Sequence(on);

    public bool Paused
    {
        get => _paused;
        set { if (value == _paused) return; _paused = value; OnPause?.Invoke(value); }
    }

    /// <summary>A film the engine can play: Godot plays Ogg Theora only - assets/video/&lt;name&gt;.ogv. The toolkit
    /// converts the .bik films to WebM (VP9) for the browser, which Godot cannot play.</summary>
    public bool FilmPlayable(string film) => FilmPath(film) != null;
    public string? FilmPath(string film) => _assets?.Resolve("assets/video/" + System.IO.Path.GetFileNameWithoutExtension(film).ToLowerInvariant() + ".ogv");
}
