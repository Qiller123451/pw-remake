// What a running mission needs of the game's interface: quests, question marks, minimap markers, the info bar,
// sounds and music, cutscenes (sequences), dialogue scenes, the end screen, the camera and the selection (tutorial
// conditions) and the player's profile store. The mission UI of the game (game/src/UI/Mission) implements it; a
// headless run (tests, a dedicated server) uses NullPresenter: sequences and dialogues end at once, as the JS
// remake's engine did without a mission UI.
//
// The JS remake called these on G.mission (ui/mission.js), G.audio, G.rtscam, G.sel and localStorage; the trigger
// engine reaches them through MissionHost (IMissionHost's presentation group). Positions here are GAME coordinates
// (x east, z south, origin = the map's centre; y up) unless the name says Map.
//
// The calls back into the mission (a sequence or dialogue that has ended) go to MissionSession.EndSequence() /
// EndDialog(token).
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Campaign;

/// <summary>A sequence (cutscene) to show: the SQNZ request, its data of world.json (null: the data has none) and its
/// actors bound to world objects (null without a timeline).</summary>
public sealed class SequencePlay
{
    public SequenceRequest Request = new();
    /// <summary>world.json sequences[path]: lines, videos, speech, sounds, duration, play (the timeline: cams, actors, items)</summary>
    public JsonNode? Data;
    public SeqActors? Actors;
    /// <summary>where the camera goes at the end (game coordinates; null = stays / SnapBack)</summary>
    public (double X, double Z)? Camera;
    public bool SnapBack;
}

/// <summary>A dialogue scene to show (DGSC): its token (EndDialog), path and the scene data (world.json dialogs[path], or
/// an empty mentor scene when its file was not exported).</summary>
public sealed class DialogPlay
{
    public int Token;
    public string Path = "";
    public JsonNode Scene = new JsonObject();
}

public interface IMissionPresenter
{
    // ---------------------------------------------------------------- quests, question marks, markers, info bar
    /// <summary>a quest changed: "shown", "done", "failed", "hidden" (news ticker, quest log)</summary>
    void QuestChanged(QuestState quest, string change);
    void QuestionMarkChanged(QuestionMark mark);
    /// <summary>a minimap marker (MPNG; X/Y of the marker are MAP coordinates, Obj its object's record id)</summary>
    void ShowMarker(MapMarker marker);
    void RemoveMarker(string id);
    /// <summary>the resolved info bar line; "" removes it</summary>
    void SetInfoBar(string text);

    // ---------------------------------------------------------------- sound
    /// <summary>a sound event for the human player at a game position (x, height, z) or null (2D)</summary>
    void PlaySound(string name, V3? at);
    /// <summary>MUSI: a music track, once or looped</summary>
    void PlayMusic(string file, bool loop);

    // ---------------------------------------------------------------- sequences, dialogues, the end
    /// <summary>the UI is busy (a sequence waits until it is not: ui/mission.js busy())</summary>
    bool IsBusy { get; }
    /// <summary>shows a sequence; true = it plays and the UI calls MissionSession.EndSequence() when it is over or
    /// skipped; false = it cannot be shown (it ends at once)</summary>
    bool PlaySequence(SequencePlay play);
    /// <summary>the sequence has ended (played, skipped or not shown): the camera goes to play.Camera unless SnapBack</summary>
    void SequenceEnded(SequencePlay play);
    /// <summary>queues a dialogue scene; true = the UI plays it and calls MissionSession.EndDialog(token) at its end;
    /// false = no UI (it ends in the next step)</summary>
    bool PlayDialog(DialogPlay dialog);
    /// <summary>the mission is over: the end screen after `delay` seconds (won: "Next mission"; lost: the reason line)</summary>
    void EndMission(bool won, string text, double delay);

    // ---------------------------------------------------------------- the player's view (tutorial conditions UNTT)
    /// <summary>the camera: map x, map y, distance, yaw; null = unknown</summary>
    (double X, double Y, double Dist, double Yaw)? Camera { get; }
    bool IsSelected(Entity e);
    /// <summary>an object can no longer be selected (OBAP): out of the selection</summary>
    void Deselect(Entity e);

    // ---------------------------------------------------------------- profile (kept between missions)
    /// <summary>the player's profile variables (ProfileVariables.txt names), or null (the defaults)</summary>
    IReadOnlyDictionary<string, string>? LoadProfile();
    void SaveProfile(IReadOnlyDictionary<string, string> values);
}

/// <summary>No interface: a headless mission (tests, servers). Sequences and dialogues end at once; the profile lives in
/// memory; the end is recorded.</summary>
public class NullPresenter : IMissionPresenter
{
    public Dictionary<string, string>? Profile;
    public (bool Won, string Text)? Ended;
    public virtual void QuestChanged(QuestState quest, string change) { }
    public virtual void QuestionMarkChanged(QuestionMark mark) { }
    public virtual void ShowMarker(MapMarker marker) { }
    public virtual void RemoveMarker(string id) { }
    public virtual void SetInfoBar(string text) { }
    public virtual void PlaySound(string name, V3? at) { }
    public virtual void PlayMusic(string file, bool loop) { }
    public virtual bool IsBusy => false;
    public virtual bool PlaySequence(SequencePlay play) => false;
    public virtual void SequenceEnded(SequencePlay play) { }
    public virtual bool PlayDialog(DialogPlay dialog) => false;
    public virtual void EndMission(bool won, string text, double delay) { Ended ??= (won, text); }
    public virtual (double X, double Y, double Dist, double Yaw)? Camera => null;
    public virtual bool IsSelected(Entity e) => false;
    public virtual void Deselect(Entity e) { }
    public virtual IReadOnlyDictionary<string, string>? LoadProfile() => Profile;
    public virtual void SaveProfile(IReadOnlyDictionary<string, string> values) => Profile = new Dictionary<string, string>(values);
}
