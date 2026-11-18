// The campaign's IMissionPresenter (src/Sim/Campaign: what a running mission needs of the interface) on the game's
// mission UI (game/src/UI/Mission: MissionPresenter, whose calls follow IMissionHost's presentation group) and the
// game session (camera, selection, sound, the profile). The two interfaces differ in their arguments: the campaign
// gives game coordinates and resolved data (SequencePlay, DialogPlay, V3), the mission UI takes the engine's
// requests and map positions - this class translates.
//
// The adapter exists before the session's nodes (MissionSession.Create needs a presenter while it builds the world):
// what arrives before the UI is attached (question marks set up by the mission's objects) is kept and replayed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using PwRemake.UI.Mission;
using PwRemake.View.Audio;

namespace PwRemake.App;

public sealed class MissionPresenterAdapter : IMissionPresenter
{
    static readonly Logger L = Log.For("UI.Mission");
    GameSession? _s;
    MissionPresenter? _ui;
    readonly List<QuestionMark> _early = new();

    public void Attach(GameSession s, MissionPresenter ui)
    {
        _s = s; _ui = ui;
        foreach (var m in _early) QuestionMarkChanged(m);
        _early.Clear();
    }

    Campaign? C => _s?.Mission?.Campaign;

    // ---------------------------------------------------------------- quests, question marks, markers, info bar
    public void QuestChanged(QuestState quest, string change) => _ui?.QuestChanged(quest, change);
    public void QuestionMarkChanged(QuestionMark m)
    {
        if (_ui == null || C == null) { _early.Add(m); return; }
        var (mx, my) = C.ToMap(m.X, m.Z);
        _ui.SetQuestionMark(m.Guid, m.State, string.IsNullOrEmpty(m.Tooltip) ? null : m.Tooltip);
        // (the mission UI keeps the mark's place in map coordinates: the world view draws it there)
        if (_ui.QuestionMarks.TryGetValue(m.Guid, out var q)) { q.X = mx; q.Y = my; q.Z = _s!.World.Height(m.X, m.Z); }
    }
    public void ShowMarker(MapMarker marker) => _ui?.ShowMarker(marker);
    public void RemoveMarker(string id) => _ui?.RemoveMarker(id);
    public void SetInfoBar(string text) => _ui?.SetInfoBar(text);

    // ---------------------------------------------------------------- sound
    /// <summary>a sound event for the player, at a game position or 2D</summary>
    public void PlaySound(string name, V3? at)
    {
        try { GameAudio.Current?.Core.Play(name, at); }
        catch (Exception e) { L.WarnOnce("snd:" + name, $"sound {name}: {e.Message}"); }
    }
    public void PlayMusic(string file, bool loop) => _ui?.PlayMusic(file, loop);

    // ---------------------------------------------------------------- sequences, dialogues, the end
    public bool IsBusy => _ui?.IsBusy ?? false;
    public bool PlaySequence(SequencePlay play)
    {
        if (_ui == null) return false;
        if (_s != null && _s.SkipCutscenes) return false;
        _s?.Input.CancelMouse();
        return _ui.PlaySequence(play.Request);
    }
    public void SequenceEnded(SequencePlay play)
    {
        _ui?.SequenceFinished(play.Request);
        // the camera: back where it was (SnapBack) or to the sequence's camera point (Game.usl:1835-1853)
        if (_s != null && !play.SnapBack && play.Camera is var (x, z)) _s.LookAt(x, z);
    }
    public bool PlayDialog(DialogPlay d)
    {
        if (_ui == null) return false;
        if (_s != null && _s.SkipCutscenes) return false;
        return _ui.PlayDialog(d.Path, d.Token);
    }
    public void EndMission(bool won, string text, double delay)
    {
        if (_s == null || _ui == null) return;
        _s.MissionEnded(won, text, delay);
    }

    // ---------------------------------------------------------------- the player's view
    public (double X, double Y, double Dist, double Yaw)? Camera
    {
        get
        {
            if (_s?.Camera == null || C == null) return null;
            var (mx, my) = C.ToMap(_s.Camera.X, _s.Camera.Z);
            return (mx, my, _s.Camera.Dist, _s.Camera.Yaw);
        }
    }
    public bool IsSelected(Entity e) => _s?.Selection?.Contains(e) ?? false;
    public void Deselect(Entity e) => _s?.Selection?.Remove(e);

    // ---------------------------------------------------------------- profile (kept between missions: the user folder)
    static string ProfileFile => Path.Combine(AppPaths.UserDir, "profile.json");
    public IReadOnlyDictionary<string, string>? LoadProfile()
    {
        try { return File.Exists(ProfileFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ProfileFile)) : null; }
        catch (Exception e) { L.Warn($"profile: {e.Message}"); return null; }
    }
    public void SaveProfile(IReadOnlyDictionary<string, string> values)
    {
        try { File.WriteAllText(ProfileFile, JsonSerializer.Serialize(values.ToDictionary(kv => kv.Key, kv => kv.Value))); }
        catch (Exception e) { L.Warn($"profile: {e.Message}"); }
    }
}
