// Small animation controller: named clips, cross-fading, fallbacks.
//
// Start / loop / end ("S/L/E"): a clip with loop marks comes with its parts as clips "<name>#s", "#l", "#e"
// (ModelManifest.Clips: engine/assets.js splitLoops; Loops = {name: [t0, t1]}). Played looping, such a clip runs its
// start once and then repeats the loop part; when another looping clip takes over (walk -> stand), the end part plays
// first. Played once (Loop = false) it runs as a whole. CurName stays the name that was asked for; Phase is "s", "l",
// "e", "t" or "".
//
// Gait changes (AnimOpts.Via, WalkGaits): a walk clip asked for while another gait of the same walk set runs plays the
// engine's transition clips first (accel_1_2 ..., Phase "t", each once; CurName is already the new gait's), then the
// new gait's loop part.
//
// Ported from remake/src/game/anim.js (pw-exporter 98c7703): the timing part. The JS drove a three.js AnimationMixer;
// here the controller keeps the playing action as data (clip, time, time scale, loop, paused) and advances it like
// the mixer does (AnimationAction._updateTime: a clip played once stops at its end - clamped or disabled - and the
// mixer's "finished" event runs; a looping clip wraps). The simulation reads it (clip lengths, the end of a clip
// played once, which clip runs); the view poses the model from the same data (Cur.Clip, Cur.Time, Cur.TimeScale,
// Fade when Serial changed) and cross-fades as three.js did. Sound events of the clips (the GSF sound table) are
// fired as World events "animSound" when World.AnimSounds is on (the view plays them).
//
// Differences to the JS: animations advance every step for every entity (the JS advanced off-screen units only a
// few times a second - the simulation must not depend on the camera); the onDone closure is an AnimDone kind
// (Delayed.ANIM_DONE) so the state stays data; the choice between alternative sound events is the view's.
using System;
using System.Collections.Generic;
using PwRemake.Sim.Content;

namespace PwRemake.Sim.World;

/// <summary>Options of AnimCtl.Play: loop, ts (time scale), fade, restart, onDone, cut (no end part of the clip before).</summary>
public sealed class AnimOpts
{
    public bool Loop = true;
    public double Ts = 1;
    public double? Fade;
    public bool Restart;
    public AnimDone? OnDone;
    public bool Cut;
    /// <summary>transition clips (a gait change: WalkGaits.Transitions) played once each, in order, before the clip's
    /// loop part (its start part is left out); null = none</summary>
    public string[]? Via;
    public static readonly AnimOpts Default = new();
    public static AnimOpts Once(AnimDone? onDone = null, bool restart = true, double? fade = null, double ts = 1) => new() { Loop = false, Restart = restart, OnDone = onDone, Fade = fade, Ts = ts };
}

/// <summary>A running clip (a three.js AnimationAction): what the view poses the model with.</summary>
public sealed class AnimAction
{
    /// <summary>the clip (lower case; a part is "&lt;name&gt;#s" / "#l" / "#e")</summary>
    public readonly string Clip;
    public readonly double Duration;
    public double Time;
    public double TimeScale = 1;
    public bool Loop;
    /// <summary>a clip played once stays on its last frame (clampWhenFinished)</summary>
    public bool Clamp;
    public bool Paused;
    public bool Enabled = true;
    public AnimAction(string clip, double duration) { Clip = clip; Duration = duration; }
    public bool IsRunning => Enabled && !Paused && TimeScale != 0;
    public override string ToString() => $"{Clip} {Time:0.00}/{Duration:0.00} x{TimeScale:0.##}{(Loop ? " loop" : "")}{(Paused ? " paused" : "")}";
}

public sealed class AnimCtl
{
    public readonly ClipSet Clips;
    /// <summary>the entity whose model it animates (onDone handlers and sound events go to it)</summary>
    public readonly Entity? Host;
    readonly Dictionary<string, AnimAction> _actions = new(StringComparer.Ordinal);
    public AnimAction? Cur;
    public string? CurName;
    public string Phase = "";
    public AnimDone? OnceCb;
    (string Name, AnimOpts Opts)? _next;
    /// <summary>PlaySle: the loop part runs once and holds (a fight position), not looping</summary>
    bool _holdLoop;
    /// <summary>PlayLoops: the repetitions of the looping part (the loop part, or a whole clip without one) still to run;
    /// 0 = for ever (until StopLoop or another clip); _outro: its end part follows them</summary>
    int _count;
    bool _outro;
    /// <summary>an end part (StopLoop, PlayLoops) or PlayLoops' last loop has run out: the next Play starts afresh</summary>
    bool _ended;
    /// <summary>a gait change: the transition clips (Phase "t") and the one running</summary>
    string[]? _via;
    int _viaI;
    public double Ts = 1;
    /// <summary>the fade the current action started with (seconds; the view's cross-fade)</summary>
    public double Fade;
    /// <summary>counts every start of an action: the view restarts / cross-fades when it changes</summary>
    public int Serial;
    /// <summary>the model's sound events: clip (lower case) -> [(time, alternatives)] sorted by time</summary>
    List<(double T, List<AnimSoundEvent> Alts)>? _evList;
    Dictionary<string, List<(double T, List<AnimSoundEvent> Alts)>>? _events;

    public AnimCtl(ClipSet clips, Entity? host)
    {
        Clips = clips;
        Host = host;
    }

    public bool Has(string? name) => !string.IsNullOrEmpty(name) && Clips.Has(name.ToLowerInvariant());
    /// <summary>the first of the names the model has (lower case), else null</summary>
    public string? Pick(params string?[] names) { foreach (var n in names) if (Has(n)) return n!.ToLowerInvariant(); return null; }
    public double Duration(string? name) => !string.IsNullOrEmpty(name) && Clips.Duration.TryGetValue(name.ToLowerInvariant(), out var d) ? d : 0;
    /// <summary>loop marks of a clip (lower case) or null</summary>
    public (double T0, double T1)? LoopOf(string name) => Clips.Loops.TryGetValue(name, out var l) ? l : null;

    AnimAction? Action(string name)
    {
        var n = name.ToLowerInvariant();
        if (_actions.TryGetValue(n, out var a)) return a;
        if (!Clips.Duration.TryGetValue(n, out var dur)) return null;
        a = new AnimAction(n, dur);
        _actions[n] = a;
        return a;
    }

    /// <summary>start one action (a whole clip or a part)</summary>
    public AnimAction? Start(string n, bool loop, double ts, double fade, string phase)
    {
        var a = Action(n);
        if (a == null) return null;
        a.Time = 0; a.Paused = false; a.Enabled = true;       // reset()
        a.TimeScale = ts;
        a.Loop = loop;
        a.Clamp = !loop;
        var prev = Cur;
        Cur = a; Phase = phase;
        Fade = prev != null && prev != a && fade > 0 ? fade : 0;     // (crossFadeFrom the previous action)
        Serial++;
        Host?.LookChanged();
        return a;
    }

    /// <summary>play a clip; opts: loop, ts (time scale), fade, restart, onDone, cut (no end part of the clip before)</summary>
    public AnimAction? Play(string? name, AnimOpts? opts = null)
    {
        opts ??= AnimOpts.Default;
        if (string.IsNullOrEmpty(name)) return null;
        var n = name.ToLowerInvariant();
        if (!Clips.Has(n)) return null;
        if (_ended) { _ended = false; CurName = null; Phase = ""; _next = null; }
        bool loop = opts.Loop;
        double ts = opts.Ts;
        if (CurName == n && !opts.Restart)
        {
            Ts = ts;
            // its end part was running (the unit stopped and goes on again): back into the loop
            if (Phase == "e") { _next = null; return Start(n + "#l", true, ts, 0.15, "l"); }
            if (Cur != null) Cur.TimeScale = ts;
            return Cur;
        }
        bool plain = loop && !opts.Restart && opts.OnDone == null && !opts.Cut;      // cut: start at once (a unit moving off)
        // an end part is running: the clip asked for follows it
        if (Phase == "e" && plain) { _next = (name, opts); return Cur; }
        // leaving a start / loop / end clip for another looping clip: its end part first
        if (plain && (Phase == "l" || Phase == "s" || Phase == "t") && CurName != null && Clips.Has(CurName + "#e"))
        {
            _next = (name, opts); _via = null;
            return Start(CurName + "#e", false, Ts != 0 ? Ts : 1, 0.12, "e");
        }
        // (asked only here: the clip already playing - most calls, every step of every standing unit - returned above
        // without building the name)
        bool parts = loop && Clips.Has(n + "#l");
        _next = null; _holdLoop = false; _count = 0; _via = null;
        Ts = ts;
        CurName = n;
        OnceCb = opts.OnDone;
        double fade = opts.Fade ?? 0.2;
        // a gait change: its transition clips first, then the loop part (WalkGaits)
        if (loop && opts.Via is { Length: > 0 } via && Action(via[0]) != null)
        {
            _via = via; _viaI = 0;
            return Start(via[0], false, ts, Math.Min(fade, 0.12), "t");
        }
        if (parts) return Clips.Has(n + "#s") ? Start(n + "#s", false, ts, fade, "s") : Start(n + "#l", true, ts, fade, "l");
        return Start(n, loop, ts, fade, "");
    }

    /// <summary>
    /// A start / loop / end clip into its fight position (CCharacter / CLumberjack AttackEnemy: SetSLEAnim(clip, 1, intro,
    /// no outro)): its start part when intro (the first shot: the bow comes up), then its loop part once, held at its end
    /// (the next shot plays the loop part again, without the start); onDone at the loop part's end. The end part (the bow
    /// goes down: EndFight's AnimAction(clip, 0, no intro, outro)) plays when a looping clip takes over (Play). null: the
    /// clip has no loop part.
    /// </summary>
    public AnimAction? PlaySle(string name, bool intro, double ts, AnimDone? onDone, double fade = 0.08)
    {
        var n = name.ToLowerInvariant();
        if (!Clips.Has(n + "#l")) return null;
        _next = null; _holdLoop = true; _count = 0; _ended = false; _via = null;
        Ts = ts; CurName = n; OnceCb = onDone;
        return intro && Clips.Has(n + "#s") ? Start(n + "#s", false, ts, fade, "s") : Start(n + "#l", false, ts, fade, "l");
    }

    /// <summary>
    /// The original's AnimAction(name, loops, intro, outro) / SetSLEAnim(name, loops, intro, outro) (the engine's
    /// start / loop / end clips): the start part once (intro), the loop part `loops` times, the end part once (outro),
    /// then onDone; the model stays on the last frame. A clip without loop marks runs whole `loops` times. loops &lt;= 0
    /// counts as 1 (Harvest.usl F2I can give 0); 9999 (SetSLEAnim(.., 9999, ..): chopping, hammering, healing) loops
    /// until StopLoop (AnimActionStopLoop) ends it with its end part. Returns the length in seconds (SleLength / ts),
    /// 0 when the model has no such clip. Not a "plain" Play: a running end part of another clip is cut (callers that
    /// should wait for it check Phase == "e" first).
    /// </summary>
    public double PlayLoops(string? name, int loops, bool intro = true, bool outro = true, double ts = 1, AnimDone? onDone = null, double fade = 0.2)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        var n = name.ToLowerInvariant();
        if (!Clips.Has(n)) return 0;
        loops = Math.Max(1, loops);
        _next = null; _holdLoop = false; _via = null;
        _count = loops >= 9999 ? 0 : loops; _outro = outro; _ended = false;
        Ts = ts; CurName = n; OnceCb = onDone;
        if (!Clips.Has(n + "#l")) Start(n, true, ts, fade, "");
        else if (intro && Clips.Has(n + "#s")) Start(n + "#s", false, ts, fade, "s");
        else Start(n + "#l", true, ts, fade, "l");
        return SleLength(n, loops, intro, outro) / (ts > 0 ? ts : 1);
    }

    /// <summary>the length of PlayLoops(name, loops, intro, outro) at time scale 1: start part + loops x loop part + end
    /// part (a clip without loop marks: loops x the clip)</summary>
    public double SleLength(string? name, int loops, bool intro = true, bool outro = true)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        var n = name.ToLowerInvariant();
        loops = Math.Max(1, loops);
        if (!Clips.Has(n + "#l")) return loops * Duration(n);
        return (intro ? Duration(n + "#s") : 0) + loops * Duration(n + "#l") + (outro ? Duration(n + "#e") : 0);
    }

    /// <summary>the loop part of a start / loop / end clip (one loop; the whole clip without loop marks)</summary>
    public double LoopLength(string? name) => string.IsNullOrEmpty(name) ? 0 : Has(name + "#l") ? Duration(name + "#l") : Duration(name);

    /// <summary>
    /// AnimActionStopLoop / EndCurrentLoopAnim: a looping start / loop / end clip goes on with its end part (once, then
    /// onDone); a looping clip without one runs to its end and stays there. Anything else is left as it is.
    /// </summary>
    public void StopLoop()
    {
        var a = Cur;
        if (a == null || CurName == null || _holdLoop) return;
        _count = 0;
        if ((Phase == "s" || Phase == "l" || Phase == "t") && Clips.Has(CurName + "#e")) { _next = null; _via = null; Start(CurName + "#e", false, Ts != 0 ? Ts : 1, 0.12, "e"); return; }
        if (a.Loop) { a.Loop = false; a.Clamp = true; }
        if (Phase == "s") _count = -1;          // (no end part: the loop part is not started after the start part)
    }

    /// <summary>a still frame of a clip (the original's SetAnim(name, 0, frame); frame / 25 = t seconds): the fields' grow
    /// step, the gates' last frame</summary>
    public AnimAction? Still(string name, double t)
    {
        var n = name.ToLowerInvariant();
        var a = Cur != null && Cur.Clip == n && CurName == n ? Cur : null;
        if (a == null)
        {
            _next = null; _holdLoop = false; _count = 0; _ended = false; _via = null; CurName = n; OnceCb = null;
            a = Start(n, false, 1, 0, "");
            if (a == null) return null;
        }
        double tt = Math.Clamp(t, 0, a.Duration);
        if (a.Time != tt || !a.Paused) { a.Time = tt; a.Paused = true; Host?.LookChanged(); }
        return a;
    }

    /// <summary>the clip asked for has run out (a clip played once at its end, an end part done, PlayLoops' last loop
    /// without an end part); false while it runs or loops</summary>
    public bool Done => Cur == null || !Cur.Enabled || (Cur.Paused && !Cur.Loop);

    // the mixer's "finished" event of the current action
    void Finished(AnimAction a)
    {
        if (a != Cur) return;
        if (Phase == "t" && _via != null && CurName != null)
        {
            // the next transition clip, else the gait's loop part (or the whole clip looping, without loop marks)
            if (++_viaI < _via.Length && Action(_via[_viaI]) != null) { Start(_via[_viaI], false, Ts, 0.08, "t"); return; }
            _via = null;
            if (Clips.Has(CurName + "#l")) Start(CurName + "#l", true, Ts, 0.1, "l"); else Start(CurName, true, Ts, 0.1, "");
            return;
        }
        if (Phase == "s" && _count >= 0) { Start(CurName + "#l", !_holdLoop, Ts, 0.12, "l"); return; }
        if (Phase == "s") { _count = 0; _ended = true; }
        if (Phase == "e" && _next is var (nn, no)) { _next = null; Phase = ""; CurName = null; Play(nn, no); return; }
        if (Phase == "e") _ended = true;
        if (OnceCb != null)
        {
            var cb = OnceCb; OnceCb = null;
            if (Host != null) Host.World.RunAnimDone(Host, cb);
        }
    }

    /// <summary>animation sound events: { clip: [[time, wav, volume, maxHear, group], ...] } (GSF sound table).
    /// Entries sharing a time are alternatives - one of them is played.</summary>
    public void SetEvents(Dictionary<string, List<AnimSoundEvent>>? map)
    {
        _events = null;
        if (map == null || map.Count == 0) return;
        _events = new Dictionary<string, List<(double, List<AnimSoundEvent>)>>(StringComparer.Ordinal);
        foreach (var (k, list) in map)
        {
            var byT = new List<(double T, List<AnimSoundEvent> Alts)>();
            foreach (var e in list)
            {
                int i = byT.FindIndex(x => x.T == e.T);
                if (i < 0) byT.Add((e.T, new List<AnimSoundEvent> { e })); else byT[i].Alts.Add(e);
            }
            byT.Sort((a, b) => a.T.CompareTo(b.T));
            _events[k.ToLowerInvariant()] = byT;
        }
    }

    /// <summary>advance by dt seconds (mixer.update)</summary>
    public void Update(double dt)
    {
        var a = Cur;
        double t0 = a?.Time ?? 0;
        if (a != null) Advance(a, dt);
        if (_events == null || a == null || !a.IsRunning || a != Cur || CurName == null || Host == null || !Host.World.AnimSounds) return;
        // (a transition clip of a gait change: its own events - the GSF has footsteps on accel_2_3 ...)
        if (!_events.TryGetValue(Phase == "t" ? a.Clip : CurName, out _evList)) return;
        double t1 = a.Time, dur = a.Duration;
        // a part of a start / loop / end clip: its events are timed on the whole clip
        var lp = Phase != "" && Phase != "t" ? LoopOf(CurName) : null;
        double off = lp == null ? 0 : Phase == "l" ? lp.Value.T0 : Phase == "e" ? lp.Value.T1 : 0;
        if (t1 >= t0) Fire(t0, t1, off);
        else { Fire(t0, dur + 1e-3, off); Fire(-1, t1, off); }     // wrapped around (looping clip)
    }

    void Fire(double lo, double hi, double off)
    {
        foreach (var (t, alts) in _evList!) if (t - off > lo && t - off <= hi) Host!.World.EmitAnimSound(Host, alts, CurName);
    }

    // AnimationAction._updateTime
    void Advance(AnimAction a, double dt)
    {
        if (!a.Enabled || a.Paused) return;
        double delta = dt * a.TimeScale;
        if (delta == 0) return;
        double dur = a.Duration, time = a.Time + delta;
        if (!a.Loop)
        {
            if (time >= dur) time = dur;
            else if (time < 0) time = 0;
            else { a.Time = time; return; }
            if (a.Clamp) a.Paused = true; else a.Enabled = false;
            a.Time = time;
            Finished(a);
            return;
        }
        if (dur <= 0) { a.Time = 0; return; }
        if (time >= dur && _count > 0 && a == Cur && (Phase == "l" || Phase == ""))
        {
            // PlayLoops: the counted repetitions of the loop part
            int wraps = (int)Math.Floor(time / dur);
            if (wraps >= _count)
            {
                double over = time - _count * dur;
                _count = 0;
                if (Phase == "l" && _outro && CurName != null && Clips.Has(CurName + "#e"))
                {
                    var e = Start(CurName + "#e", false, a.TimeScale, 0.12, "e");
                    if (e != null && over > 0) Advance(e, over / (a.TimeScale != 0 ? a.TimeScale : 1));
                    return;
                }
                a.Loop = false; a.Clamp = true; a.Paused = true; a.Time = dur;
                _ended = true;
                Finished(a);
                return;
            }
            _count -= wraps;
        }
        if (time >= dur || time < 0) time -= dur * Math.Floor(time / dur);
        a.Time = time;
    }

    /// <summary>stop everything (the JS dispose: mixer.stopAllAction)</summary>
    public void Stop()
    {
        Cur = null; CurName = null; Phase = ""; OnceCb = null; _next = null; _count = 0; _ended = false; _via = null;
        Serial++;
        Host?.LookChanged();
    }

    public override string ToString() => Cur == null ? "(none)" : $"{CurName} [{Phase}] {Cur}";
}
