// An in-engine cutscene played as the original's sequencer does (docs/SEQUENCE_FORMAT.md §9 of the toolkit; the data:
// pwexport/campaign.py seq_play) - the part of it the mission UI owns:
//
//   * The game stands still (the server skips the world update while a sequence runs); the timeline runs on real time.
//   * Speech: each lip-sync ("say") item plays its .mp3 (Audio/SeqSounds) and shows its subtitle for its length.
//   * Sounds of the timeline, fades to and from black, the wide-screen bars and the time skips of cut items.
//   * Camera, actors (walks, animations, lip sync), particle effects, camera shakes and day time items are the
//     world view's: they go to the ISequenceStage hook with the scene's time (not ported yet - the camera stays where
//     it is and the actors where the world has them).
//
// No Godot types: the tests link this file.
// Ported from remake/src/ui/cutscene.js (pw-exporter 98c7703) without its three.js parts (camera tracks, actors, lip
// sync, effects, day time): those come with the world view.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PwRemake.UI.Mission.Logic;

/// <summary>What a cutscene asks of the world view: the camera, the actors, effects, day time. All optional; a
/// presenter without a world (the test scene) has none and the scenes play over what is on screen.</summary>
public interface ISequenceStage
{
    /// <summary>A cutscene starts: the HUD goes, the fog is refreshed (G.updateFow), the camera of a line scene eases
    /// to opts.Camera in CamEase seconds.</summary>
    void Begin(CinePlay c);
    /// <summary>A line scene's frame (real seconds): the camera ease.</summary>
    void Update(CinePlay c, double dt);
    /// <summary>A timeline scene at its time t (after the skips): camera cuts and tracks (play.Raw cuts / cams), camera
    /// shakes, actors (play.Actors' walk / path / anim / act / attr items, lip sync of the say items), day time.</summary>
    void Timeline(CinePlay c, SequenceTimeline play, double t, double dt);
    /// <summary>A particle effect item of the timeline (path, map position, length).</summary>
    void Effect(TimelineItem fx);
    /// <summary>The cutscene is over: the camera back where it was (opts.SnapBack) or left at opts.Camera, the actors
    /// drawn for the scene removed, the HUD back.</summary>
    void End(CinePlay c);
}

public sealed class CutsceneTimeline
{
    readonly MissionUiState M;
    readonly CinePlay C;
    readonly SequenceTimeline P;
    public double T;
    public bool Done;
    readonly HashSet<TimelineItem> started = new();
    readonly HashSet<TimelineItem> spoken = new();
    readonly List<(IUiSound Snd, double Until)> sounds = new();
    string lineKey = "\0";

    public CutsceneTimeline(MissionUiState m, CinePlay c, SequenceTimeline play)
    {
        M = m; C = c; P = play;
        T = play.Start;
    }

    /// <summary>Items begun / sounds playing (tests).</summary>
    public int Started => started.Count;
    public int Sounds => sounds.Count(s => !s.Snd.Ended);

    public void Start()
    {
        C.Bars = true;          // (the JS showed the bars with the scene; a 'bars' item switches them)
        Apply(0);
    }

    /// <summary>One frame: dt real seconds.</summary>
    public void Update(double dt)
    {
        if (Done) return;
        double t0 = T;
        T += dt;
        // a cut item skips its time range
        foreach (var it in P.Items)
            if (it.K == "skip" && it.Len > 0 && T >= it.T && t0 < it.T + it.Len && T < it.T + it.Len) T = it.T + it.Len;
        Apply(dt);
        if (T >= P.End) Done = true;
    }

    void Apply(double dt)
    {
        double t = T;
        // global items
        double fadeA = 0;
        bool? bars = null;
        foreach (var it in P.Items)
        {
            if (it.T > t) continue;
            if (it.K == "fade")
            {
                double f = it.Len > 0 ? Math.Min(1, (t - it.T) / it.Len) : 1;
                fadeA = it.Dir == "in" ? 1 - f : f;
            }
            else if (it.K == "bars") bars = it.On;
            else if (!started.Contains(it))
            {
                started.Add(it);
                if (it.K == "sound" && it.T + Math.Max(0.2, it.Len) > t)
                {
                    var file = Regex.Replace(it.Path, @"^/?(data/base/audio/)?sound/", "", RegexOptions.IgnoreCase).ToLowerInvariant();
                    var s = M.Out.PlayFile(file, it.Vol, false, 0, it.Loop);
                    if (s != null) sounds.Add((s, it.T + it.Len));
                }
                else if (it.K == "fx") M.Stage?.Effect(it);
            }
        }
        C.Fade = Math.Max(0, Math.Min(1, fadeA));
        if (bars != null) C.Bars = bars.Value;
        for (int i = 0; i < sounds.Count; i++)
            if (sounds[i].Until <= t && !sounds[i].Snd.Ended) sounds[i].Snd.Stop();
        M.Stage?.Timeline(C, P, t, dt);
        // speech
        TimelineItem? line = null;
        foreach (var items in P.Actors)
            foreach (var it in items)
            {
                if (it.K != "say" || it.T > t) continue;
                started.Add(it);
                if (it.Mp3.Length > 0 && !spoken.Contains(it) && t < it.T + it.Len - 0.25)
                {
                    spoken.Add(it);
                    Say(it, t - it.T);
                }
                if (!it.Music && t < it.T + it.Len + 0.3) line = it;
            }
        Subtitle(line);
    }

    // off: seconds the scene is past the item's start
    void Say(TimelineItem it, double off)
    {
        var s = M.Out.PlayFile("../SeqSounds/" + it.Mp3, 100, !it.Music, off > 0.15 ? off : 0, false);
        if (s != null) sounds.Add((s, 1e9));
    }

    void Subtitle(TimelineItem? it)
    {
        var l = it == null ? null : (C.Seq?.Lines ?? new List<SequenceLine>()).FirstOrDefault(x => x.Key.Length > 0 && it.Key.Length > 0 && string.Equals(x.Key, it.Key, StringComparison.OrdinalIgnoreCase));
        var key = l?.Key ?? "";
        if (key == lineKey) return;
        lineKey = key;
        bool sub = M.Subtitles;
        C.Text = l != null && sub ? Regex.Replace(l.Text, @"\s*\n\s*", " ") : "";
        C.Speaker = l != null && sub && l.Speaker.Length > 0 ? M.SpeakerName(l.Speaker) + M.Separator : "";
        C.Portrait = "";
        if (l != null) M.LogLines.Add(new[] { "line", C.Seq?.Id ?? "", (C.Seq?.Lines.IndexOf(l) ?? -1).ToString() });
    }

    public void Stop()
    {
        Done = true;
        foreach (var s in sounds) { try { s.Snd.Stop(); } catch (Exception) { /* over */ } }
        sounds.Clear();
        C.Fade = 0;
    }
}
