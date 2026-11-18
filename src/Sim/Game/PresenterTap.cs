// What the mission asks of the player's interface, recorded - and answered again from the record when a saved game is
// replayed (docs/SAVES.md).
//
// A few questions of the trigger engine are answered by the interface, not by the world: is the mission UI busy (a
// cutscene waits until it is not), does it play this cutscene / dialogue scene (or does it end at once), where is the
// camera and is this object selected (the tutorial's UNTT conditions), what does the player's profile hold (Tutorial_
// Started: mission 1's short intro). The answers decide what the triggers do next, so a replay must get the same ones
// at the same moments: the tap sits between the mission (MissionHost) and the real presenter, writes down every answer
// that differs from the one before (kind, the how-many-th question of that kind, the step) and, while a saved game is
// replayed, gives the recorded answers instead of asking - and keeps the interface quiet (no sounds, no news, no
// camera moves while the game is fast-forwarded on the loading screen). A question asked at another step than in the
// record, or more often, means the replay went another way than the saved game (Mismatch).
//
// Not a JS module: the JS saved the state of its mission UI and trigger engine (game/save.js), not their inputs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Game;

/// <summary>One recorded answer: the kind of question, which one of that kind (0, 1, ...), the step it was asked in,
/// the answer (JSON: a bool, [x, y, dist, yaw] / null, a profile object / null).</summary>
public sealed record TapAnswer(string Kind, long N, long Step, JsonNode? Value);

public sealed class PresenterTap : IMissionPresenter
{
    static readonly Logger L = Log.For("Game.Saves");
    /// <summary>the real presenter (the game's mission UI adapter; NullPresenter headless)</summary>
    public IMissionPresenter Inner;
    /// <summary>the world whose StepNo marks the answers (set once the world exists; 0 before)</summary>
    public World.World? World;
    /// <summary>every answer that differed from the one before (the record of this game, replayed answers included)</summary>
    public readonly List<TapAnswer> Record = new();
    /// <summary>the last MUSI music (file, loop) asked for while replaying (the game plays it once it runs)</summary>
    public (string File, bool Loop)? ReplayedMusic;
    /// <summary>a question that went another way than in the saved game (null: none yet)</summary>
    public string? Mismatch;
    /// <summary>the inner presenter answers the same on every run (LockstepPresenter: a lockstep game and its saves): a
    /// replay asks it too instead of the record (a dedicated server's replay has no record); the record is still compared</summary>
    public bool DeterministicInner;

    readonly Dictionary<string, long> _count = new();
    readonly Dictionary<string, JsonNode?> _last = new();
    Dictionary<string, List<TapAnswer>>? _replay;
    readonly Dictionary<string, int> _cursor = new();

    public PresenterTap(IMissionPresenter? inner = null) { Inner = inner ?? new NullPresenter(); }

    /// <summary>replaying: the questions are answered from this record, the interface stays quiet</summary>
    public bool Replaying => _replay != null;

    public void StartReplay(IEnumerable<TapAnswer> record)
    {
        _replay = new();
        foreach (var a in record) { if (!_replay.TryGetValue(a.Kind, out var l)) _replay[a.Kind] = l = new(); l.Add(a); }
        _cursor.Clear();
    }
    /// <summary>the replay is over (done or diverged): from now on the interface answers</summary>
    public void EndReplay() { _replay = null; }

    long Step => World?.StepNo ?? 0;

    static bool Same(JsonNode? a, JsonNode? b) => a == null ? b == null : b != null && a.ToJsonString() == b.ToJsonString();

    /// <summary>the answer to the n-th question of a kind: live from ask(), or from the record when replaying</summary>
    JsonNode? Answer(string kind, Func<JsonNode?> ask)
    {
        long n = _count.TryGetValue(kind, out var c) ? c : 0;
        _count[kind] = n + 1;
        JsonNode? v;
        if (_replay != null)
        {
            v = DeterministicInner ? ask() : (_last.TryGetValue(kind, out var lv) ? lv : Default(kind))?.DeepClone();
            if (_replay.TryGetValue(kind, out var list))
            {
                int i = _cursor.TryGetValue(kind, out var ci) ? ci : 0;
                while (i < list.Count && list[i].N <= n)
                {
                    var a = list[i];
                    if (a.N == n && a.Step != Step && Mismatch == null) Mismatch = $"the interface was asked \"{kind}\" #{n} at step {Step}, in the saved game at step {a.Step}";
                    if (!DeterministicInner) v = a.Value?.DeepClone();
                    i++;
                }
                _cursor[kind] = i;
            }
        }
        else v = ask();
        if (!Same(_last.TryGetValue(kind, out var prev) ? prev : Default(kind), v))
            Record.Add(new TapAnswer(kind, n, Step, v?.DeepClone()));
        _last[kind] = v?.DeepClone();
        return v;
    }
    static JsonNode? Default(string kind) => kind is "busy" or "seq" or "dlg" or "sel" ? JsonValue.Create(false) : null;
    static bool B(JsonNode? v) => v is JsonValue jv && jv.TryGetValue(out bool b) && b;

    // ---------------------------------------------------------------- the questions
    public bool IsBusy => B(Answer("busy", () => JsonValue.Create(Inner.IsBusy)));
    public bool PlaySequence(SequencePlay play) => B(Answer("seq", () => JsonValue.Create(Inner.PlaySequence(play))));
    public bool PlayDialog(DialogPlay dialog) => B(Answer("dlg", () => JsonValue.Create(Inner.PlayDialog(dialog))));
    public (double X, double Y, double Dist, double Yaw)? Camera
    {
        get
        {
            var v = Answer("cam", () => Inner.Camera is var (x, y, d, w) ? new JsonArray(x, y, d, w) : null);
            if (v is not JsonArray a || a.Count < 4) return null;
            return (a[0].Num(), a[1].Num(), a[2].Num(), a[3].Num());
        }
    }
    public bool IsSelected(Entity e) => B(Answer("sel", () => JsonValue.Create(Inner.IsSelected(e))));
    public IReadOnlyDictionary<string, string>? LoadProfile()
    {
        var v = Answer("prof", () =>
        {
            var p = Inner.LoadProfile();
            if (p == null) return null;
            var o = new JsonObject();
            foreach (var (k, x) in p.OrderBy(kv => kv.Key, StringComparer.Ordinal)) o[k] = x;
            return o;
        });
        if (v is not JsonObject obj) return null;
        return obj.ToDictionary(kv => kv.Key, kv => kv.Value.Str());
    }

    // ---------------------------------------------------------------- what the mission shows (quiet while replaying)
    public void QuestChanged(QuestState quest, string change) { if (!Replaying) Inner.QuestChanged(quest, change); }
    public void QuestionMarkChanged(QuestionMark mark) => Inner.QuestionMarkChanged(mark);          // (the game keeps them until its UI exists)
    public void ShowMarker(MapMarker marker) { if (!Replaying) Inner.ShowMarker(marker); }
    public void RemoveMarker(string id) { if (!Replaying) Inner.RemoveMarker(id); }
    public void SetInfoBar(string text) { if (!Replaying) Inner.SetInfoBar(text); }
    public void PlaySound(string name, V3? at) { if (!Replaying) Inner.PlaySound(name, at); }
    public void PlayMusic(string file, bool loop) { if (Replaying) ReplayedMusic = (file, loop); else Inner.PlayMusic(file, loop); }
    public void SequenceEnded(SequencePlay play) { if (!Replaying) Inner.SequenceEnded(play); }
    public void EndMission(bool won, string text, double delay) => Inner.EndMission(won, text, delay);
    public void Deselect(Entity e) { if (!Replaying) Inner.Deselect(e); }
    public void SaveProfile(IReadOnlyDictionary<string, string> values) { if (!Replaying) Inner.SaveProfile(values); }

    // ---------------------------------------------------------------- the record as JSON
    public static JsonArray ToJson(IEnumerable<TapAnswer> record)
    {
        var a = new JsonArray();
        foreach (var x in record) a.Add(new JsonArray(x.Kind, x.N, x.Step, x.Value?.DeepClone()));
        return a;
    }
    public static List<TapAnswer> FromJson(JsonNode? n)
    {
        var res = new List<TapAnswer>();
        if (n is not JsonArray a) return res;
        foreach (var e in a)
            if (e is JsonArray x && x.Count >= 4) res.Add(new TapAnswer(x[0].Str(), (long)x[1].Num(), (long)x[2].Num(), x[3]?.DeepClone()));
        return res;
    }
}
