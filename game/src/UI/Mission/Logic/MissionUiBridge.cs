// The presentation half of IMissionHost, on the mission UI: the campaign host forwards these calls (same names and
// arguments as IMissionHost) and the bridge turns them into MissionUiState's contract - looks up the scene / sequence
// of a path in the mission's data and calls the engine back (TriggerEngine.EndDialog(token), EndSequence()) when a
// scene is over. MissionPresenter (the Godot node) owns one; the tests use it with a real TriggerEngine.
// The JS did the lookup in the engine (engine.js playSequence / playDialog); here the engine only knows paths.
// No Godot types: the tests link this file.
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;

namespace PwRemake.UI.Mission.Logic;

/// <summary>What the bridge calls on the engine (TriggerEngine implements it through EngineLink; tests may fake it).</summary>
public interface IMissionEngineLink
{
    void EndSequence();
    void EndDialog(int token);
    IEnumerable<QuestState> Quests { get; }
    IEnumerable<LevelTimer> Timers { get; }
    /// <summary>The bonus points the mission has given (TriggerEngine.Boni).</summary>
    double Boni { get; }
}

/// <summary>A TriggerEngine as the bridge's link.</summary>
public sealed class EngineLink : IMissionEngineLink
{
    readonly TriggerEngine E;
    public EngineLink(TriggerEngine e) { E = e; }
    public void EndSequence() => E.EndSequence();
    public void EndDialog(int token) => E.EndDialog(token);
    public IEnumerable<QuestState> Quests => E.Quests;
    public IEnumerable<LevelTimer> Timers => E.Timers.Values;
    public double Boni => E.Boni;
}

public sealed class MissionUiBridge
{
    static readonly Logger L = Log.For("UI.Mission");
    public readonly MissionUiState State;
    public IMissionEngineLink? Engine { get; private set; }
    /// <summary>The engine's request of the sequence on screen (SequenceFinished gets it back from the engine).</summary>
    public SequenceRequest? Running;
    /// <summary>The camera hook of SequenceFinished: the camera goes to r.Camera unless r.SnapBack (the world's).</summary>
    public Action<SequenceRequest>? OnSequenceFinished;
    /// <summary>The end screen was asked for (the presenter shows it).</summary>
    public Action<EndScreenInfo>? OnEndMission;
    /// <summary>A sound event for the player (PSND: PlaySound) and a music track (MUSI: PlayMusic) - the game's audio.</summary>
    public Action<string, MapPos?>? OnSound;
    public Action<string, bool>? OnMusic;

    public MissionUiBridge(MissionUiState state) { State = state; }

    /// <summary>The engine the scenes report their end to; the UI reads its quests and timers.</summary>
    public void Bind(IMissionEngineLink engine)
    {
        Engine = engine;
        State.Quests = () => engine.Quests;
        State.Timers = () => engine.Timers;
    }
    public void Bind(TriggerEngine engine) => Bind(new EngineLink(engine));

    // ---------------------------------------------------------------- IMissionHost's presentation calls
    public void QuestChanged(QuestState quest, string change) => State.QuestChanged(quest, change);
    public void SetQuestionMark(string guid, string state, string? tooltip) => State.SetQuestionMark(guid, state, tooltip);
    public void ShowMarker(MapMarker m) => State.Marker(m);
    public void RemoveMarker(string id) => State.RemoveMarker(id);
    public void SetInfoBar(string text) => State.InfoBar(text);
    public void PlaySound(string name, MapPos? at) => OnSound?.Invoke(name, at);
    public void PlayMusic(string file, bool loop) => OnMusic?.Invoke(file, loop);
    public bool IsBusy => State.Busy;

    /// <summary>Queues the sequence; true: the UI plays it and calls EndSequence() at its end (also a sequence the
    /// data does not know: it ends on the next tick - no ending inside the engine's own action).</summary>
    public bool PlaySequence(SequenceRequest r)
    {
        var seq = State.Data.Sequence(r.Path);
        if (seq == null) L.WarnOnce("seq:" + r.Path, $"sequence {r.Path} is not in the mission's data");
        if (seq is { File: false }) return false;       // engine.js: a sequence without a file ends at once (§6.8)
        Running = r;
        var opts = new SequenceOptions { Camera = r.Camera is { } c && (c.X != 0 || c.Y != 0) ? c : null, SnapBack = r.SnapBack, Request = r };
        State.PlaySequence(seq, opts, () => { if (Running == r) Running = null; Engine?.EndSequence(); });
        return true;
    }
    public void SequenceFinished(SequenceRequest r) => OnSequenceFinished?.Invoke(r);

    /// <summary>Queues the scene; true: the UI plays it and calls EndDialog(token) at its end (an unknown scene: on the
    /// next tick).</summary>
    public bool PlayDialog(string path, int token)
    {
        var scene = State.Data.Dialog(path);
        if (scene == null) L.WarnOnce("dlg:" + path, $"dialogue {path} is not in the mission's data");
        State.PlayDialog(scene ?? DialogScene.Unknown(path), () => Engine?.EndDialog(token));
        return true;
    }

    public void EndMission(bool won, string text)
    {
        // (engine.js win / endNow pass no points: the end screen sums the accomplished quests)
        var e = State.EndMission(won, text);
        OnEndMission?.Invoke(e);
    }
}
