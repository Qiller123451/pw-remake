// The interface's answers to a mission in a lockstep game (co-op, docs/MULTIPLAYER.md "Interface answers"): the same on
// every machine at the same step, whatever its screen does.
//
// Single player, a few questions of the trigger engine are answered by the player's interface (PresenterTap): is the
// mission UI busy, does it play this cutscene / dialogue scene, where is the camera, is this selected, the profile. In a
// lockstep game the answers must not depend on the machine:
//   busy      no (the engine itself waits while a cutscene runs; the single player UI's "busy" is the same moment)
//   seq / dlg yes - every machine shows the scene; it ends when the players have seen or skipped it: each machine's
//             UI votes (SceneStarted tells the network which scene began; a machine whose UI cannot show it votes at
//             once) and the host issues the SceneEndCommand by the game's scene rule
//   cam / sel the host's seat's, sent as UiStateCommand (Campaign.SharedCamera / SharedSelection)
//   profile   the host's profile values of the game's start (GameStart.Profile)
// Everything the mission shows (quests, markers, sounds, music, the end) goes on to the machine's own interface.
// A saved lockstep game answers the same when it is loaded (the session uses this presenter for a start with seats).
using System;
using System.Collections.Generic;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Game;

public sealed class LockstepPresenter : IMissionPresenter
{
    static readonly Logger L = Log.For("Net.Scenes");
    /// <summary>this machine's interface (the game's mission UI adapter; NullPresenter headless)</summary>
    public IMissionPresenter Inner;
    public readonly GameStart Start;
    /// <summary>the mission (set once it exists: the shared camera / selection)</summary>
    public Campaign.Campaign? C;
    int _sequences;
    /// <summary>a scene began at this step: (kind "sequence" | "dialog", token - sequences are counted from 1, dialogues
    /// carry the engine's token -, shown by this machine's interface). The network layer tells the host (a cutscene holds
    /// the turns) and votes at once for a scene the interface did not show.</summary>
    public event Action<string, int, bool>? SceneStarted;
    /// <summary>the token of the last cutscene begun (the interface's end of it is a vote for this one)</summary>
    public int SequenceToken => _sequences;
    /// <summary>true while the interface must stay quiet (a saved game replayed on the loading screen: PresenterTap.Replaying)
    /// - the scenes are not handed to the interface then</summary>
    public Func<bool>? Quiet;

    public LockstepPresenter(GameStart start, IMissionPresenter? inner = null) { Start = start; Inner = inner ?? new NullPresenter(); }

    public bool IsBusy => false;
    public bool PlaySequence(SequencePlay play)
    {
        int token = ++_sequences;
        bool shown = false;
        try { shown = Quiet?.Invoke() != true && Inner.PlaySequence(play); } catch (Exception e) { L.Error("a cutscene could not be shown", e); }
        SceneStarted?.Invoke("sequence", token, shown);
        return true;
    }
    public bool PlayDialog(DialogPlay dialog)
    {
        bool shown = false;
        try { shown = Quiet?.Invoke() != true && Inner.PlayDialog(dialog); } catch (Exception e) { L.Error("a dialogue could not be shown", e); }
        SceneStarted?.Invoke("dialog", dialog.Token, shown);
        return true;
    }
    public (double X, double Y, double Dist, double Yaw)? Camera => C?.SharedCamera;
    public bool IsSelected(Entity e) => C != null && C.SharedSelection.Contains(e.Id);
    public IReadOnlyDictionary<string, string>? LoadProfile() => Start.Profile;

    public void QuestChanged(QuestState quest, string change) => Inner.QuestChanged(quest, change);
    public void QuestionMarkChanged(QuestionMark mark) => Inner.QuestionMarkChanged(mark);
    public void ShowMarker(MapMarker marker) => Inner.ShowMarker(marker);
    public void RemoveMarker(string id) => Inner.RemoveMarker(id);
    public void SetInfoBar(string text) => Inner.SetInfoBar(text);
    public void PlaySound(string name, V3? at) => Inner.PlaySound(name, at);
    public void PlayMusic(string file, bool loop) => Inner.PlayMusic(file, loop);
    public void SequenceEnded(SequencePlay play) => Inner.SequenceEnded(play);
    public void EndMission(bool won, string text, double delay) => Inner.EndMission(won, text, delay);
    public void Deselect(Entity e) => Inner.Deselect(e);
    public void SaveProfile(IReadOnlyDictionary<string, string> values) => Inner.SaveProfile(values);
}
