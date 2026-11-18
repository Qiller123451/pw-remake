// The mission UI on a real converted mission (mission 1 by default) over a plain background: the title card, quests
// on the ticker and in the log, the info bar and a timer, dialogue scenes with their speech (ds_1020, ds_1030, the
// mentor's L01B01), a cutscene with its timeline (sc_1040: bars, fades, subtitles, speech, sounds), a film that
// cannot be played (ms_1120) and the end screen - with checks and screenshots.
//
// Run (rendering on llvmpipe in the container):
//   xvfb-run -a -s "-screen 0 1600x900x24" godot --path game res://src/UI/Mission/Tests/MissionUiTest.tscn -- --shots=<dir>
//     [--mission=<folder of a converted mission>] [--data=<converted data>] [--install=<ParaWorld installation>]
// Exit code 0 = all checks passed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;
using PwRemake.UI.Mission.Logic;
using PwRemake.View.Audio;

namespace PwRemake.UI.Mission.Tests;

public partial class MissionUiTest : Control
{
    static readonly Logger L = Log.For("UI.Mission.Test");
    int _fails, _checks;
    string _shots = "";
    MissionPresenter _p = null!;
    TestEngine _eng = null!;

    /// <summary>The engine's side as the test drives it: the mission's quests and level timers; the UI's EndDialog /
    /// EndSequence calls are counted.</summary>
    sealed class TestEngine : IMissionEngineLink
    {
        public readonly List<QuestState> QuestList;
        public readonly Dictionary<string, LevelTimer> TimerMap = new();
        public readonly List<int> DialogsEnded = new();
        public int SequencesEnded;
        public TestEngine(List<QuestState> q) { QuestList = q; }
        public void EndSequence() => SequencesEnded++;
        public void EndDialog(int token) => DialogsEnded.Add(token);
        public IEnumerable<QuestState> Quests => QuestList;
        public IEnumerable<LevelTimer> Timers => TimerMap.Values;
        public double Boni => 0;
    }

    void Check(bool ok, string what)
    {
        _checks++;
        if (ok) L.Info("ok    " + what);
        else { _fails++; L.Error("FAIL  " + what); }
    }

    public override async void _Ready()
    {
        if (Log.Sink<ConsoleLogSink>() == null) Log.AddSink(new ConsoleLogSink());
        Log.DefaultLevel = LogLevel.Info;
        var args = GameApp.UserArgs();
        _shots = args.GetValueOrDefault("shots", "");
        var mission = args.GetValueOrDefault("mission", "/home/claude/testcontent/configs/base/missions/01_single_01");
        var cfg = new GameConfiguration
        {
            Id = "missionui-test", Title = "mission UI test", Folders = new() { "Base" },
            Install = args.GetValueOrDefault("install", "/mnt/user-data/uploads/Paraworld"),
            Data = args.GetValueOrDefault("data", "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a"),
        };
        var assets = new AssetSource(cfg);
        PwTheme.Init(assets);
        SetAnchorsPreset(LayoutPreset.FullRect);
        try
        {
            HudArt.Init(assets);
            Background();
            var audio = new GameAudio(assets) { FollowApp = false };
            AddChild(audio);
            // the mission as the game loads it: the Lua mission (quests with the engine's flags) and the UI's data
            var lm = LuaMission.Load(mission);
            var data = MissionUiData.FromJson(lm.World, lm.Mission, null);
            foreach (var (k, v) in lm.Script.Texts) data.Texts[k] = v;
            _eng = new TestEngine(lm.Script.Quests);
            _p = new MissionPresenter(data, assets) { TickWithFrames = true };
            _p.State.MentorTexts = true;
            _p.Bind(_eng);
            AddChild(_p);
            await Run(data);
        }
        catch (Exception ex) { _fails++; L.Error("EXCEPTION", ex); }
        L.Info($"mission UI test: {_checks - _fails}/{_checks} passed");
        GetTree().Quit(_fails == 0 ? 0 : 1);
    }

    // a stand-in for the game: the mission's loading picture, darkened, with the HUD's menu button
    void Background()
    {
        var bg = PwTheme.Tex("decoration__loadbg_single01.jpg") ?? PwTheme.Tex("decoration__loadbg_static.jpg");
        if (bg != null)
        {
            var r = new TextureRect { Texture = bg, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, Modulate = new Color(0.62f, 0.62f, 0.6f) };
            r.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(r);
        }
        else AddChild(new ColorRect { Color = new Color(0.25f, 0.3f, 0.22f), AnchorRight = 1, AnchorBottom = 1 });
        var menu = HudArt.Tex("hud_menu_menubutton.png");
        if (menu != null)
        {
            var m = new TextureRect { Texture = menu };
            m.SetAnchorsPreset(LayoutPreset.TopRight);
            m.OffsetLeft = -95; m.OffsetRight = 0; m.OffsetTop = 2; m.OffsetBottom = 34;
            m.AddChild(new Label { Text = "Menu", AnchorRight = 1, OffsetLeft = 12, OffsetRight = -4, OffsetBottom = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, LabelSettings = HudArt.Text(12, new Color(0xf3 / 255f, 0xdc / 255f, 0xa6 / 255f), true) });
            AddChild(m);
        }
    }

    // (real time, polled per frame: on llvmpipe a frame can take longer than a SceneTreeTimer's step)
    async Task Wait(double s)
    {
        var t0 = Time.GetTicksMsec();
        do await Frames(1); while (Time.GetTicksMsec() - t0 < s * 1000);
    }
    async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    async Task Until(Func<bool> cond, double timeout)
    {
        var t0 = Time.GetTicksMsec();
        while (!cond() && Time.GetTicksMsec() - t0 < timeout * 1000) await Frames(1);
    }

    async Task Shot(string name)
    {
        if (_shots.Length == 0) return;
        await Frames(2);
        System.IO.Directory.CreateDirectory(_shots);
        var path = System.IO.Path.Combine(_shots, name + ".png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        L.Info($"shot {path}");
    }

    async Task Run(MissionUiData data)
    {
        var S = _p.State;
        var Q = _eng.QuestList;
        Check(Q.Count == data.QuestOrder.Count && Q.Count > 0, $"quests of the mission ({Q.Count})");
        Check(UiAtlas.Count > 500, $"atlas ({UiAtlas.Count} entries)");

        // the title card, the first quests, info bar and a timer
        _p.ShowTitle();
        await Wait(1.2);
        await Shot("01_title");
        Show(Q[0]); Show(Q[1]);
        _p.SetInfoBar(data.Text("_L01_InfoBar_Wood", "Stina's spearmen: 2 / 5"));
        _eng.TimerMap["t1"] = new LevelTimer { Id = "t1", Left = 95, Total = 95, Show = true, Label = "The pack arrives" };
        Check(S.News.Count == 2 && S.Unread, "two quests on the ticker, the button flashes");

        // dialogue scenes with speech
        _p.PlayDialog("Cpn_single_001/single_01/ds_1020.dlg", 1);
        _p.PlayDialog("Cpn_single_001/single_01/ds_1030.dlg", 2);
        await Until(() => S.Scene != null, 2);
        Check(S.Scene?.Scene.Id == "ds_1020" && S.Scene.Name == "Cole:" && S.Scene.Portrait == "cole", "ds_1020: Cole speaks with his portrait");
        Check(S.Scene?.Speech != null || GameAudio.Current?.Core.Ready() == false, $"speech plays ({S.Scene?.Speech?.Duration:0.00} s)");
        var w0 = Time.GetTicksMsec();
        await Wait(0.8);
        L.Info($"waited {(Time.GetTicksMsec() - w0) / 1000.0:0.00} s, {Engine.GetFramesPerSecond():0} fps");
        Check(S.TitleT < 0.5, $"the title card fades for the first line ({S.TitleT:0.00})");
        await Shot("02_dialogue");
        await Until(() => S.Scene?.I == 1, 8);
        Check(S.Scene?.Name == "Warrior:" && S.Scene.Portrait == "hu_warrior", "frame 1: the warrior");
        await Wait(0.5);
        await Shot("03_dialogue_warrior");
        await Until(() => _eng.DialogsEnded.Contains(1), 8);
        Check(_eng.DialogsEnded.SequenceEqual(new[] { 1 }), "EndDialog(1) after ds_1020");
        // the quest log over the next scene (the game pauses: the scene waits)
        Q[1].Accomplished = true; _p.QuestChanged(Q[1], "done");
        var side = Q.FirstOrDefault(q => !q.Main);
        if (side != null) { side.Visible = true; _p.QuestChanged(side, "shown"); }
        await Wait(0.6);
        await Shot("04_hud");
        _p.OpenLog();
        await Frames(3);
        Check(S.LogOpen && GetTree().Root.FindChild("QuestLog", true, false) != null, "the quest log is open");
        await Shot("05_questlog");
        _p.CloseLog();
        await Until(() => _eng.DialogsEnded.Contains(2), 40);
        Check(_eng.DialogsEnded.SequenceEqual(new[] { 1, 2 }), "EndDialog(2) after ds_1030");
        // the mentor: with the option off it is not shown but ends
        S.MentorTexts = false;
        _p.PlayDialog("Cpn_single_001/single_01/mentor/ds_L01B01.dlg", 3);
        await Until(() => _eng.DialogsEnded.Contains(3), 4);
        Check(_eng.DialogsEnded.Contains(3), "a hidden mentor scene ends");
        S.MentorTexts = true;
        _p.PlayDialog("Cpn_single_001/single_01/mentor/ds_L01B01.dlg", 4);
        await Until(() => S.Scene != null, 2);
        await Wait(0.6);
        await Shot("06_mentor");
        S.CloseDialog();
        Check(_eng.DialogsEnded.Contains(4), "the × closes the scene");

        // a cutscene with its timeline
        var paused = new List<bool>();
        _p.OnPause = paused.Add;
        var r = new SequenceRequest { Path = "Cpn_single_001/single_01/sc_1040.seq", Trigger = "test" };
        Check(_p.PlaySequence(r) && _p.IsBusy, "sc_1040 queued, busy");
        await Until(() => S.Cine != null, 2);
        Check(S.Cine?.Kind == CineKind.Timeline && paused.LastOrDefault(), "a timeline scene, the game paused");
        await Until(() => S.Cine?.Text.StartsWith("Stina?") == true, 12);
        Check(S.Cine?.Speaker == "Cole:", "Cole's subtitle");
        await Wait(0.3);
        await Shot("07_cutscene_cole");
        await Until(() => S.Cine?.Text.StartsWith("Not really") == true, 6);
        await Wait(1.0);
        await Shot("08_cutscene_stina");
        // Esc
        var esc = new InputEventKey { Keycode = Key.Escape, Pressed = true };
        Input.ParseInputEvent(esc);
        await Frames(3);
        Check(S.Cine == null && _eng.SequencesEnded == 1 && !_p.IsBusy && paused.LastOrDefault() == false, "Esc: EndSequence(), unpaused");
        // a film that cannot be played here
        _p.PlaySequence(new SequenceRequest { Path = "Cpn_single_001/single_01/ms_1120.seq" });
        await Until(() => S.Cine != null, 2);
        Check(S.Cine?.Kind == CineKind.FilmCard, "ms_1120: the film's card (WebM cannot play in Godot)");
        await Wait(1.2);
        await Shot("09_film_card");
        await Until(() => S.Cine == null, 6);
        Check(_eng.SequencesEnded == 2, "the film sequence ended");

        // markers and question marks (data for the minimap / world)
        _p.ShowMarker(new MapMarker { Id = "m", Kind = "SPMainQuest", X = 300, Y = 400, Ttl = 0 });
        var qm = data.QuestionMarks.FirstOrDefault();
        if (qm != null) _p.SetQuestionMark(qm.Guid, "QM_STATE_YELLOW", "?");
        Check(_p.Markers.Count == 1 && MissionPresenter.MarkerLit(_p.Markers[0]) && (qm == null || _p.QuestionMarks[qm.Guid].Visible), "marker and question mark data");

        // the end screen
        Q[0].Accomplished = true;
        _p.EndMission(true, "");
        await Frames(3);
        Check(S.End != null && S.End.Won && GetTree().Root.FindChild("EndScreen", true, false) != null, "the end screen");
        await Shot("10_end_victory");
    }

    public override void _Process(double delta)
    {
        if (_p == null || _eng == null || _p.State.Cine != null || _p.State.LogOpen || _p.State.End != null) return;
        foreach (var t in _eng.TimerMap.Values) if (!t.Paused) t.Left = Math.Max(0, t.Left - delta);
    }

    void Show(QuestState q) { q.Visible = true; _p.QuestChanged(q, "shown"); }
}
