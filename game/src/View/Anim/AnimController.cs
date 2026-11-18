// Small animation controller: named clips, cross-fading, fallbacks.
//
// Start / loop / end ("S/L/E"): a clip with loop marks comes with its parts as clips "<name>#s", "#l", "#e"
// (ClipTools.SplitLoops; ModelTemplate.Loops = {name: [t0, t1]}). Played looping, such a clip runs its start once and
// then repeats the loop part; when another looping clip takes over (walk -> stand), the end part plays first. Played
// once (loop: false) it runs as a whole. CurName stays the name that was asked for; Phase is 's', 'l', 'e' or ''.
//
// Godot: one AnimationPlayer per model instance, driven by Update(dt) (manual process mode: the game decides the time -
// game speed, pause), playing the clips of the template's shared AnimationLibrary. Godot keeps "loop" on the clip, not
// on the playback, so a clip played the other way than it is stored gets a twin "<name>@loop" / "<name>@once" in the
// library, made once per template on first use.
//
// Hit reactions (HitReaction / OverlayRestore / OverlayApply): the engine's overlay clip of a melee hit
// (EID_HitReactionOVLAnim_F/B/L/R, FightingObj.usl:4436-4446) played over the running clip without stopping it - the
// bone rotations of the reaction clip relative to its first frame, added to the pose the player made, faded in and out.
//
// Ported from remake/src/game/anim.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;
using PwRemake.Sim.Core;

namespace PwRemake.View.Anim;

public sealed class PlayOptions
{
    public bool Loop = true;
    /// <summary>time scale (negative: backwards)</summary>
    public double Ts = 1;
    /// <summary>cross-fade time; null = 0.2 s</summary>
    public double? Fade;
    public bool Restart;
    /// <summary>no end part of the clip before (a unit moving off)</summary>
    public bool Cut;
    public Action? OnDone;
    /// <summary>start at this time of the clip (seconds; null = 0, or the end when Ts &lt; 0)</summary>
    public double? StartAt;
    public static readonly PlayOptions Looping = new();
}

/// <summary>An animation sound / effect event of the GSF sound table: [time, wav, volume, maxHear, group].</summary>
public readonly record struct AnimEvent(double Time, string Wav, double Volume, double MaxHear, string Group);

public sealed class AnimController
{
    public AnimationPlayer Player { get; }
    readonly AnimationLibrary _lib;                            // the template's clips (shared)
    readonly AnimationLibrary _own = new();                    // the clips this instance played (cheap to attach)
    readonly Dictionary<string, string> _byName;               // lower-case name -> library name (the template's)
    readonly Dictionary<string, (double t0, double t1)> _loops; // loop marks by lower-case name (the template's)
    public string? CurName { get; private set; }               // the clip asked for (lower case) - by Play only
    /// <summary>the clip the player runs (lower case, without its part "#s" / "#l" / "#e"), however it was started: Play
    /// or Start - the game's units follow the simulation's clip through Start (EntityView.SyncAnim), which leaves
    /// CurName alone. What the particles ask (a weapon's fire while its wielder attacks, Particles.Firing).</summary>
    public string? Running { get; private set; }
    public string Phase { get; private set; } = "";
    string? _cur;                                              // the library clip playing (a part, a loop twin ...)
    double _ts = 1;
    (string name, PlayOptions opts)? _next;
    Action? _onceCb;
    string? _finished;
    Dictionary<string, List<(double t, AnimEvent[] alts)>>? _events;
    Action<AnimEvent>? _onEvent;
    static readonly Random s_rnd = new();
    readonly Node _root;

    /// <summary>lib: the template's clips; names: lower-case name -> clip; loops: loop marks by lower-case name.</summary>
    public AnimController(Node modelRoot, AnimationLibrary lib, Dictionary<string, string> names, Dictionary<string, (double t0, double t1)> loops)
    {
        _lib = lib; _byName = names; _loops = loops; _root = modelRoot;
        Player = new AnimationPlayer { Name = "AnimationPlayer", CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual };
        // (the player gets a library of its own that fills with the clips it plays: attaching the template's library
        // of a character - ~470 clips with the start / loop / end parts - cost ~2 ms per unit)
        Player.AddAnimationLibrary("", _own);
        modelRoot.AddChild(Player);
        Player.AnimationFinished += n => _finished = n;
    }

    public IEnumerable<string> Names => _byName.Keys;
    public bool Has(string? name) => !string.IsNullOrEmpty(name) && _byName.ContainsKey(name.ToLowerInvariant());
    /// <summary>The first of the names the model has (lower case), or null.</summary>
    public string? Pick(params string?[] names) { foreach (var n in names) if (Has(n)) return n!.ToLowerInvariant(); return null; }
    public double Duration(string? name) => name != null && _byName.TryGetValue(name.ToLowerInvariant(), out var s) ? _lib.GetAnimation(s).Length : 0;
    public bool IsPlaying => Player.IsPlaying();
    /// <summary>the unit's stream weapon fires (Unit.Spraying, set by the view each frame): its weapon emitters stay on
    /// between the attack clips (Particles.Firing)</summary>
    public bool HoldFire;
    /// <summary>Time in the clip part playing.</summary>
    public double Time => AnimPos.Position(Player);
    public double TimeScale => _ts;

    // the library clip for a name, looping or not (a twin with the other loop mode is made once)
    string? Clip(string lower, bool loop)
    {
        if (!_byName.TryGetValue(lower, out var s)) return null;
        var a = _lib.GetAnimation(s);
        bool isLoop = a.LoopMode != Animation.LoopModeEnum.None;
        if (isLoop != loop)
        {
            var twin = s + (loop ? "@loop" : "@once");
            if (!_lib.HasAnimation(twin))
            {
                var d = (Animation)a.Duplicate();
                d.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
                _lib.AddAnimation(twin, d);
            }
            s = twin;
        }
        if (!_own.HasAnimation(s)) _own.AddAnimation(s, _lib.GetAnimation(s));
        return s;
    }

    /// <summary>start one clip (a whole clip or a part)</summary>
    public bool Start(string n, bool loop, double ts, double fade, string phase)
    {
        var clip = Clip(n.ToLowerInvariant(), loop);
        if (clip == null) return false;
        bool same = Player.AssignedAnimation == clip;
        bool hadCur = _cur != null;
        Player.Play(clip, same || !hadCur ? 0 : fade, 1, false);
        if (same) Player.Seek(0, true);
        Player.SpeedScale = (float)ts;
        if (ts < 0) Player.Seek(AnimPos.Length(Player), true);
        _cur = clip; Phase = phase;
        var r = n.ToLowerInvariant(); int h = r.IndexOf('#');
        Running = h >= 0 ? r[..h] : r;
        _finished = null;
        return true;
    }

    /// <summary>play a clip (anim.js play): looping by default; a start-loop-end clip runs its parts</summary>
    public bool Play(string? name, PlayOptions? opts = null)
    {
        if (string.IsNullOrEmpty(name)) return false;
        opts ??= PlayOptions.Looping;
        var n = name.ToLowerInvariant();
        if (!_byName.ContainsKey(n)) return false;
        bool loop = opts.Loop;
        double ts = opts.Ts;
        bool parts = loop && _byName.ContainsKey(n + "#l");
        if (CurName == n && !opts.Restart)
        {
            _ts = ts;
            // its end part was running (the unit stopped and goes on again): back into the loop
            if (Phase == "e") { _next = null; return Start(n + "#l", true, ts, 0.15, "l"); }
            if (_cur != null) Player.SpeedScale = (float)ts;
            return true;
        }
        bool plain = loop && !opts.Restart && opts.OnDone == null && !opts.Cut;      // cut: start at once (a unit moving off)
        // an end part is running: the clip asked for follows it
        if (Phase == "e" && plain) { _next = (name, opts); return true; }
        // leaving a start / loop / end clip for another looping clip: its end part first
        if (plain && (Phase == "l" || Phase == "s") && CurName != null && _byName.ContainsKey(CurName + "#e"))
        {
            _next = (name, opts);
            return Start(CurName + "#e", false, _ts, 0.12, "e");
        }
        _next = null;
        _ts = ts;
        CurName = n;
        _onceCb = opts.OnDone;
        double fade = opts.Fade ?? 0.2;
        bool ok = parts
            ? (_byName.ContainsKey(n + "#s") ? Start(n + "#s", false, ts, fade, "s") : Start(n + "#l", true, ts, fade, "l"))
            : Start(n, loop, ts, fade, "");
        if (ok && opts.StartAt is double at && Phase == "") Player.Seek(Math.Clamp(at, 0, AnimPos.Length(Player)), true);
        return ok;
    }

    /// <summary>A clip held at its first or last frame (compose.js hold).</summary>
    public void Hold(string clip, bool atEnd)
    {
        if (!Play(clip, new PlayOptions { Loop = false, Restart = true, Fade = 0 })) return;
        Player.Seek(atEnd ? AnimPos.Length(Player) : 0, true);
        if (!atEnd) Player.Pause();
    }

    /// <summary>The current clip held at time t (a cutscene's frame range: SeqActors.FrameTime).</summary>
    public void Pose(double t)
    {
        if (_cur == null || !Player.HasAnimation(Player.AssignedAnimation)) return;
        Player.Seek(Math.Clamp(t, 0, AnimPos.Length(Player)), true);
        Player.Pause();
    }

    /// <summary>Go on after Hold (a paused clip runs again).</summary>
    public void Resume() { if (_cur != null && !Player.IsPlaying()) Player.Play(_cur); }

    public void SetTimeScale(double ts) { _ts = ts; if (_cur != null) Player.SpeedScale = (float)ts; }

    /// <summary>
    /// Animation sound events: { clipName: [[time, wav, volume, maxHear, group], ...] } (GSF sound table).
    /// Entries sharing a time are alternatives - one of them is played.
    /// </summary>
    public void SetEvents(JsonObject? map, Action<AnimEvent> cb)
    {
        _events = new();
        if (map != null)
            foreach (var (k, v) in map)
            {
                if (v is not JsonArray arr) continue;
                var byT = new SortedDictionary<double, List<AnimEvent>>();
                foreach (var e in arr)
                {
                    if (e is not JsonArray ea || ea.Count < 2) continue;
                    var ev = new AnimEvent(ea[0].Num(), ea[1].Str(), ea.Count > 2 ? ea[2].Num() : 0, ea.Count > 3 ? ea[3].Num() : 0, ea.Count > 4 ? ea[4].Str() : "");
                    if (!byT.TryGetValue(ev.Time, out var l)) byT[ev.Time] = l = new();
                    l.Add(ev);
                }
                var list = new List<(double, AnimEvent[])>();
                foreach (var (t, l) in byT) list.Add((t, l.ToArray()));
                _events[k.ToLowerInvariant()] = list;
            }
        _onEvent = cb;
    }

    public void Update(double dt)
    {
        string? cur = _cur;
        double t0 = cur != null ? Time : 0;
        Player.Advance(dt);
        if (_finished != null && _finished == _cur)
        {
            _finished = null;
            if (Phase == "s") { Start(CurName + "#l", true, _ts, 0.12, "l"); return; }
            if (Phase == "e" && _next != null) { var nx = _next.Value; _next = null; Phase = ""; CurName = null; Play(nx.name, nx.opts); return; }
            if (_onceCb != null) { var cb = _onceCb; _onceCb = null; cb(); }
        }
        if (_onEvent == null || cur == null || cur != _cur || _events == null || !Player.IsPlaying() || CurName == null) return;
        if (!_events.TryGetValue(CurName, out var list)) return;
        double t1 = Time, dur = AnimPos.Length(Player);
        // a part of a start / loop / end clip: its events are timed on the whole clip
        double off = 0;
        if (Phase != "" && _loops.TryGetValue(CurName, out var lp)) off = Phase == "l" ? lp.t0 : Phase == "e" ? lp.t1 : 0;
        void Fire(double lo, double hi)
        {
            foreach (var (t, alts) in list) if (t - off > lo && t - off <= hi) _onEvent!(alts[s_rnd.Next(alts.Length)]);
        }
        if (t1 >= t0) Fire(t0, t1);
        else { Fire(t0, dur + 1e-3); Fire(-1, t1); }     // wrapped around (looping clip)
    }

    public void Stop() { Player.Stop(); _cur = null; CurName = null; Running = null; Phase = ""; _next = null; _ov = null; }

    // ------------------------------------------------------------------------------------------- hit reactions
    // The original: the server invokes EID_HitReactionOVLAnim_<side> (FO:4436-4446); the client's default handling
    // plays an overlay animation (PWClient.exe settings Client/HitReaction/Weight, Blend, AddObjSpace, UseB,
    // FramesBlendIn, FramesBlendOut) from the walk set's slots hit_reaction_fa / la / ra / ba (and _fb .. _bb for
    // "UseB"). 84 character models have those clips (in the shipped all_characters.gsf only the test model cole_test
    // fills the slots); the toolkit keeps them in the converted data since 2026-11-13 (before: only "hit_reaction",
    // 0.28 s, 55 models): the side's clip "hit_reaction_<f|l|r|b>a" where the model has it, else "hit_reaction".
    // How: after the player posed the skeleton, each bone the clip turns gets the clip's turn relative to its first
    // frame (so the running clip's pose stays the base), weighted in over FADE and out over the last FADE seconds.
    // Before the player poses the next frame the bones go back to the base pose (OverlayRestore): a held pose that the
    // player does not pose again is not turned twice. No allocation per frame: the tracks of a clip are resolved once
    // per model template (s_tracks), the base rotations kept in an array of the instance made on its first reaction.

    sealed class OvTracks { public int[] Track = null!; public int[] Bone = null!; public Quaternion[] Inv0 = null!; }
    static readonly Dictionary<(AnimationLibrary, string), OvTracks?> s_tracks = new();
    static readonly string[] SIDE_CLIPS = { "hit_reaction_fa", "hit_reaction_ba", "hit_reaction_la", "hit_reaction_ra" };
    const double FADE = 0.08;          // (2 frames: the engine's FramesBlendIn / Out - their values are not known)
    Animation? _ov;
    OvTracks? _ovT;
    double _ovTime, _ovLen;
    Skeleton3D? _skel;
    Quaternion[]? _base;
    bool _based;

    /// <summary>is a hit reaction playing</summary>
    public bool Overlaying => _ov != null;
    /// <summary>the clip of the hit reaction playing (lower case) or null</summary>
    public string? OverlayClip { get; private set; }

    /// <summary>start the hit reaction of a blow from side 'f', 'b', 'l', 'r' (Unit.HitReactSide); false when the model
    /// has no clip for it. A reaction running starts again.</summary>
    public bool HitReaction(char side)
    {
        int i = side switch { 'f' => 0, 'b' => 1, 'l' => 2, 'r' => 3, _ => -1 };
        string? name = i >= 0 && _byName.ContainsKey(SIDE_CLIPS[i]) ? SIDE_CLIPS[i] : _byName.ContainsKey("hit_reaction") ? "hit_reaction" : null;
        if (name == null || !_byName.TryGetValue(name, out var libName)) return false;
        var key = (_lib, libName);
        if (!s_tracks.TryGetValue(key, out var tr)) s_tracks[key] = tr = Resolve(_lib.GetAnimation(libName));
        if (tr == null) return false;
        _skel ??= FindSkeleton();
        if (_skel == null) return false;
        if (_ov != null && _based) OverlayRestore();
        _ov = _lib.GetAnimation(libName); _ovT = tr; _ovTime = 0; _ovLen = _ov.Length; _based = false;
        OverlayClip = name;
        if (_base == null || _base.Length < tr.Track.Length) _base = new Quaternion[Math.Max(tr.Track.Length, 64)];
        return true;
    }

    Skeleton3D? FindSkeleton() => _root.GetNodeOrNull<Skeleton3D>("Model/Skeleton3D") ?? _root.FindChild("Skeleton3D", true, false) as Skeleton3D;

    // the rotation tracks of the clip on bones of the skeleton (their path "Model/Skeleton3D:bone"), with the inverse
    // of each one's first frame
    OvTracks? Resolve(Animation a)
    {
        var sk = _skel ?? FindSkeleton();
        if (sk == null) return null;
        var tr = new List<int>(); var bo = new List<int>(); var inv = new List<Quaternion>();
        for (int i = 0; i < a.GetTrackCount(); i++)
        {
            if (a.TrackGetType(i) != Animation.TrackType.Rotation3D || a.TrackGetKeyCount(i) == 0) continue;
            var path = a.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            int b = sk.FindBone(path.GetSubName(0));
            if (b < 0) continue;
            tr.Add(i); bo.Add(b); inv.Add(a.RotationTrackInterpolate(i, 0).Normalized().Inverse());
        }
        return tr.Count == 0 ? null : new OvTracks { Track = tr.ToArray(), Bone = bo.ToArray(), Inv0 = inv.ToArray() };
    }

    /// <summary>before the player poses the frame: the bones the reaction turned go back to the pose the player made</summary>
    public void OverlayRestore()
    {
        if (_ov == null || !_based || _skel == null || _ovT == null) return;
        var b = _ovT.Bone;
        for (int k = b.Length - 1; k >= 0; k--) _skel.SetBonePoseRotation(b[k], _base![k]);     // (backwards: a bone twice gets its first base)
        _based = false;
    }

    /// <summary>after the player posed the frame: the reaction's turn on top, advanced by dt (game time; 0: paused - the
    /// same turn again)</summary>
    public void OverlayApply(double dt)
    {
        if (_ov == null || _skel == null || _ovT == null) return;
        _ovTime += dt;
        if (_ovTime >= _ovLen) { _ov = null; OverlayClip = null; return; }
        double w = Math.Min(1, Math.Min(_ovTime / FADE, (_ovLen - _ovTime) / FADE));
        if (w < 0) w = 0;
        var t = _ovT.Track; var b = _ovT.Bone; var inv = _ovT.Inv0;
        for (int k = 0; k < t.Length; k++)
        {
            var cur = _skel.GetBonePoseRotation(b[k]);
            _base![k] = cur;
            var d = inv[k] * _ov.RotationTrackInterpolate(t[k], _ovTime).Normalized();
            if (w < 1) d = Quaternion.Identity.Slerp(d, (float)w);
            _skel.SetBonePoseRotation(b[k], (cur * d).Normalized());
        }
        _based = true;
    }
}
