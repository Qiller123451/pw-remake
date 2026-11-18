// Original ParaWorld sound events and music.
//
// Data: assets/sounds.json (SoundDb.cs; built by tools/build_sounds.py from Scripts/Server/init/*.txt)
//   events:   { name(lowercase): { g: global?, seq: sequential?, s: [[file, volume 0..100, maxHearingDistance(, minFade)], ...] } }
//   material: MaterialEffects.txt  { weaponFx: { targetFx: [sfxEvent, gfx] } }
//   files:    original wav path (lowercase) -> the file below assets/snd/ (AssetSource delivers the game's own
//             Audio/Sound/<path> there; most are IMA ADPCM wavs - WavDecoder.cs)
// Animation sound events come from the GSF models (manifest.models[x].sounds: { anim: [[time, wav, vol, maxhear, group,
// minFade, pitch]] }, AnimSounds.cs); their sound groups feed the crowd sounds (CrowdSounds.cs, Audio/soundeffects.txt).
//
// Channels: 'sfx' (world sounds, panned from the camera's target), 'ui', 'voice' (one speech ack at a time), 'atmo'
// (Atmo.cs), 'music' (streamed mp3: Music.cs, the original's MusicMgr, as Mus).
//
// World sounds follow the original client's rules (SoundLimits; docs/AUDIO.md "How many sounds"): heard within the
// event's hearing distance x the camera height's factor (CSoundEventMgrClient, PWClient 0x48fdf0), quieter as 1/d past
// the min fade distance (Miles' 3D roll-off), at most 21 at a time and at most 4 of one wav, never two of one wav within
// 0.04 s (C3DSoundMgr, 0x7a0cb0). The remake adds slots by priority: idle chatter and walking / work sounds cannot take
// the last slots, which are kept for the fights. SoundLimits.Js is the browser remake's (the golden tests).
//
// The class is AudioCore (not Audio: that is the namespace). It decides what plays, how loud and where; an IAudioOut
// plays it (GodotAudioOut.cs in the game, a recorder in the tests). GameAudio.cs is the node that owns both.
// Ported from remake/src/engine/audio.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

/// <summary>An animation sound event of a model (manifest.models[x].sounds[clip][i]): [time, wav, vol, maxhear, group,
/// minFade, pitch]; MinFade: the GSF's min fade distance when the data has it (0: SoundLimits.DefaultMinFade); Pitch: the
/// GSF's speed of the sound (the client plays the wav at its rate x this, C3DSound 0x7a2f70; 0: not in the data - the
/// browser remake's 0.95 .. 1.05 at random).</summary>
public readonly record struct AnimSoundEvent(double Time, string Wav, double Vol, double MaxHear, string Group, double MinFade = 0, double Pitch = 0);

/// <summary>What a world sound is to the slots (SoundLimits): idle chatter gives way to walking and work, they to the
/// fights. The original had one priority for all of them (0; its crowd sounds 1).</summary>
public enum SoundPriority { Ambient, Activity, Combat }

/// <summary>
/// How many world sounds play, how far they are heard and how loud (docs/AUDIO.md "How many sounds"). Original: the
/// client's rules (PWClient.exe). Js: the browser remake's (audio.js), for the golden tests.
/// </summary>
public sealed class SoundLimits
{
    /// <summary>x the event's maximum hearing distance (the JS: 1.6, "for a closer camera").</summary>
    public double HearScale = 1;
    /// <summary>The camera height's factor (AudioCore.HearK) shortens the hearing and min fade distances (the JS: no).</summary>
    public bool CameraHeight = true;
    /// <summary>Gain vol x minFade / d beyond the min fade distance (Miles' 3D roll-off; the original set the min and
    /// max fade distances of every sample: AIL_set_sample_3D_distances) - else the JS's vol x (1 - d / R)^1.5.</summary>
    public bool MilesFalloff = true;
    /// <summary>World sounds playing at once: C3DSoundMgr refuses a sound while more than 20 play (0x7a0cb0).</summary>
    public int MaxPlaying = 21;
    /// <summary>Instances of one wav playing at once (0x7a0cb0: "count &lt; 4").</summary>
    public int MaxSameFile = 4;
    /// <summary>Seconds after the newest instance of a wav started before the next may start (0x7a0cb0: 0.04).</summary>
    public double SameFileGap = 0.04;
    /// <summary>Remake: idle sounds (SoundPriority.Ambient) start only while fewer than this many world sounds play.</summary>
    public int AmbientSlots = 8;
    /// <summary>Remake: walking and work sounds (Activity) start only while fewer than this many play - the rest of the
    /// MaxPlaying slots are the fights'.</summary>
    public int ActivitySlots = 15;

    public static SoundLimits Original => new();
    /// <summary>engine/audio.js: hearing distance x 1.6, (1 - d/R)^1.5, at most 25 at a time (`active > 24` refuses),
    /// no other limit.</summary>
    public static SoundLimits Js => new()
    {
        HearScale = 1.6, CameraHeight = false, MilesFalloff = false, MaxPlaying = 25, MaxSameFile = int.MaxValue, SameFileGap = 0,
        AmbientSlots = int.MaxValue, ActivitySlots = int.MaxValue,
    };

    /// <summary>The slots a sound of this priority may take.</summary>
    public int Slots(SoundPriority p) => p switch
    {
        SoundPriority.Ambient => Math.Min(MaxPlaying, AmbientSlots),
        SoundPriority.Activity => Math.Min(MaxPlaying, ActivitySlots),
        _ => MaxPlaying,
    };

    /// <summary>The min fade distance when the data has none: the original's tables - animation sounds (the GSF) 5 m
    /// (most of them); sound events 6 m up to a hearing distance of 66 m, 10 m beyond (explosions, big projectiles),
    /// and the events heard everywhere (maxhear 150: building ready, item taken) full up to 2 m before their end.</summary>
    public static double DefaultMinFade(double maxHear, bool anim) =>
        anim ? 5 : maxHear >= 140 ? maxHear - 2 : maxHear > 66 ? 10 : 6;

    /// <summary>The camera height's factor on the hearing and min fade distances (PWClient 0x48fdf0): 0.25 + (height
    /// above the ground - 10) / 25, within 0.25 .. 1 - close to the ground only what is near is heard.</summary>
    public static double HeightK(double cameraAboveGround) => 0.25 + Math.Clamp((cameraAboveGround - 10) / 25, 0, 0.75);
}

/// <summary>What happened to the world sounds asked for (the counts behind the log's "world sounds" line).</summary>
public sealed class WorldSoundStats
{
    public int Played, Far, Quiet, Cap, SameFile;
    public int Asked => Played + Far + Quiet + Cap + SameFile;
    public void Clear() { Played = Far = Quiet = Cap = SameFile = 0; }
    public override string ToString() => $"{Played} of {Asked} played; refused: {Far} too far, {Quiet} too quiet, {Cap} no free slot, {SameFile} same wav";
}

public sealed class AudioCore
{
    static readonly Logger L = Log.For("View.Audio");

    public SoundDb Db = new();
    public readonly IAudioOut Out;
    readonly Dictionary<string, int> _seqIdx = new();
    /// <summary>Where the sounds are heard from: the camera's target on the ground (main.js 'audio' step).</summary>
    public V3 Listener;
    /// <summary>The camera's yaw (the stereo pan of world sounds).</summary>
    public double Yaw;
    /// <summary>How many world sounds, how far, how loud (SoundLimits.Original; the golden tests: Js).</summary>
    public SoundLimits Limits = SoundLimits.Original;
    /// <summary>The camera height's factor on the hearing distances (SoundLimits.HeightK; GameAudio.UpdateWorld).</summary>
    public double HearK = 1;
    /// <summary>The world sounds since the last log line (every 10 s at Debug).</summary>
    public readonly WorldSoundStats Stats = new();
    /// <summary>Seconds (real time: the original timed its sounds by the clock, not by the game). Tests set their own.</summary>
    public Func<double> Clock = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public readonly AudioVolumes Vol = new();
    public bool Muted;
    ISoundHandle? _voice;            // current speech ack
    bool _voiceBusy;
    /// <summary>A playing world sound: its file (the per-wav limit), priority, start (Clock).</summary>
    sealed record WorldSound(ISoundHandle H, string File, SoundPriority P, double T);
    readonly List<WorldSound> _active = new();     // playing world sounds (limited)
    double _statsT = double.NaN;
    /// <summary>Music turned down while somebody speaks (ui/mission.js duck: 0.5).</summary>
    public double DuckK = 1;
    public readonly Music Mus;
    /// <summary>The level's ambience (Atmo.cs).</summary>
    public readonly Atmo Atmo;
    /// <summary>The crowd sounds (CrowdSounds.cs: soundeffects.txt "Additionals"; the table: GameAudio.UseAssets).</summary>
    public readonly CrowdSounds Crowd = new();
    /// <summary>The random numbers of the presentation (variants, pitch, playlists): Math.random in the JS. Not the
    /// simulation's - sounds never change the game. Tests set a seeded one.</summary>
    public Func<double> Rnd = Random.Shared.NextDouble;

    public AudioCore(IAudioOut output)
    {
        Out = output;
        Mus = new Music(this);
        Atmo = new Atmo(this);
        Crowd.Rnd = () => Rnd();
        Crowd.Play = PlayCrowd;
    }

    /// <summary>The tables (sounds.json); the music manager takes its part.</summary>
    public void SetData(SoundDb db)
    {
        Db = db;
        Mus.SetData(db.Music);
        L.Info($"sounds: {db.Events.Count} events, {db.Files.Count} animation files, {db.Material.Count} weapon materials, " +
               $"{db.Atmo.Count} ambience templates, music {(db.Music != null ? db.Music.Playlists.Count + " playlists" : "fallback")}");
    }

    /// <summary>Can sounds be heard now: the output runs and the sound is not muted.</summary>
    public bool Ready() => Out.Ready && !Muted;

    public void ApplyVolumes()
    {
        Out.ApplyVolumes(Vol, Muted);
        Mus.ApplyVolume();
    }

    /// <summary>The decoded file (cached by the output); null when missing.</summary>
    public ISoundBuffer? Buffer(string file) => string.IsNullOrEmpty(file) ? null : Out.Load(file);

    public bool Has(string? name) => !string.IsNullOrEmpty(name) && Db.Events.ContainsKey(name.ToLowerInvariant());

    /// <summary>Number of world sounds playing.</summary>
    public int Active
    {
        get { _active.RemoveAll(w => w.H.Ended); return _active.Count; }
    }

    /// <summary>Number of world sounds of a priority playing.</summary>
    public int ActiveOf(SoundPriority p) { _active.RemoveAll(w => w.H.Ended); return _active.Count(w => w.P == p); }

    /// <summary>Why a world sound may not start now (null: it may): no free slot for its priority, or its wav plays
    /// MaxSameFile times already / started less than SameFileGap ago (C3DSoundMgr 0x7a0cb0).</summary>
    string? Refused(string file, SoundPriority prio)
    {
        var Lm = Limits;
        if (Active >= Lm.Slots(prio)) { Stats.Cap++; return "cap"; }
        if (Lm.MaxSameFile == int.MaxValue && Lm.SameFileGap <= 0) return null;
        int same = 0; double last = double.NegativeInfinity;
        foreach (var w in _active) if (w.File == file) { same++; last = Math.Max(last, w.T); }
        if (same >= Lm.MaxSameFile || (same > 0 && Clock() - last < Lm.SameFileGap)) { Stats.SameFile++; return "same wav"; }
        return null;
    }

    /// <summary>Plays a sound file (below assets/snd/); pos = world position (null for 2D), maxHear = original hearing
    /// distance; pan (-1 .. 1) for a 2D one; offset: seconds into the file (speech that starts late). A world sound
    /// takes a slot of its priority (SoundLimits); minFade: its min fade distance (0: SoundLimits.DefaultMinFade).</summary>
    public ISoundHandle? PlayFile(string? file, double vol = 70, V3? pos = null, double maxHear = 0, AudioChannel channel = AudioChannel.Sfx,
        double rate = 1, double pan = 0, double offset = 0, SoundPriority prio = SoundPriority.Combat, double minFade = 0, bool anim = false)
    {
        if (!Out.Ready || Muted || string.IsNullOrEmpty(file)) return null;
        double gain = vol / 100;
        if (pos is V3 p)
        {
            var Lm = Limits;
            double d = Math.Sqrt((p.X - Listener.X) * (p.X - Listener.X) + (p.Z - Listener.Z) * (p.Z - Listener.Z));
            double mh = maxHear != 0 ? maxHear : 60, k = Lm.CameraHeight ? HearK : 1;
            double R = mh * Lm.HearScale * k;
            if (d > R) { Stats.Far++; return null; }
            if (Lm.MilesFalloff)
            {
                double mf = (minFade > 0 ? minFade : SoundLimits.DefaultMinFade(mh, anim)) * k;
                if (d > mf) gain *= mf / d;
            }
            else gain *= Math.Pow(1 - d / R, 1.5);
            if (gain < 0.01) { Stats.Quiet++; return null; }
            if (Refused(file, prio) != null) return null;
        }
        var buf = Buffer(file);
        if (buf == null) return null;
        // (the JS counted the sound before this check; a late start past the end plays nothing and counts nothing)
        if (offset > 0 && offset >= buf.Duration) return null;
        // (the JS: world sounds always through a StereoPannerNode, 2D ones only with a pan)
        double? pn = null;
        if (pos is V3 q)
        {
            double dx = q.X - Listener.X, dz = q.Z - Listener.Z;
            double a = Math.Atan2(dx, -dz) - Yaw;
            pn = Math.Max(-0.8, Math.Min(0.8, Math.Sin(a) * Math.Min(1, Math.Sqrt(dx * dx + dz * dz) / 30)));
        }
        else if (pan != 0) pn = Math.Max(-1, Math.Min(1, pan));
        var h = Out.Start(buf, channel, gain, rate, pn, Math.Max(0, offset), false);
        if (h != null && pos != null) { _active.Add(new WorldSound(h, file, prio, Clock())); Stats.Played++; }
        if (h != null) L.Trace(() => $"play {file} {channel} gain {gain:0.###} pan {pn?.ToString("0.##") ?? "-"}{(pos is V3 w ? $" at {w.X:0.#},{w.Z:0.#}" : "")}");
        return h;
    }

    /// <summary>Plays an event by name (random or sequential choice among its sounds). Global events are 2D (ui, or
    /// voice for voice_*), the others world sounds at pos. world: a world sound at pos even when the event is global
    /// (the client's Play3DSound of a global event: the death cries, Effects.usl CDeathSound).</summary>
    public ISoundHandle? Play(string? name, V3? pos = null, AudioChannel? channel = null, SoundPriority prio = SoundPriority.Combat, bool world = false)
    {
        if (!Out.Ready || string.IsNullOrEmpty(name)) return null;
        if (!Db.Events.TryGetValue(name.ToLowerInvariant(), out var ev) || ev.S.Count == 0) return null;
        int i;
        if (ev.Seq) { i = (_seqIdx.TryGetValue(name, out var k) ? k : 0) % ev.S.Count; _seqIdx[name] = i + 1; }
        else i = (int)Math.Floor(Rnd() * ev.S.Count);
        var s = ev.S[i];
        return PlayFile(s.File, s.Vol, ev.Global && !(world && pos != null) ? null : pos, s.MaxHear,
            channel ?? (name.StartsWith("voice_", StringComparison.Ordinal) ? AudioChannel.Unit : ev.Global ? AudioChannel.Ui : AudioChannel.Sfx),
            prio: prio, minFade: s.MinFade);
    }

    /// <summary>The first existing event of a list (the UI sound manager's fallback chain).</summary>
    public ISoundHandle? PlayFirst(IEnumerable<string> names, V3? pos = null, AudioChannel? channel = null)
    {
        foreach (var n in names) if (Has(n)) return Play(n, pos, channel);
        return null;
    }

    /// <summary>Speech acknowledgements: one at a time - while one speaks, the next is not said.</summary>
    public void Ack(IEnumerable<string> names)
    {
        string? n = null;
        foreach (var x in names) if (Has(x)) { n = x; break; }
        if (n == null) return;
        PollVoice();
        if (_voice != null && _voiceBusy) return;
        _voiceBusy = true;
        var src = Play(n, null, AudioChannel.Unit);
        if (src == null) { _voiceBusy = false; return; }
        _voice = src;
    }

    /// <summary>A speech ack is being said.</summary>
    public bool VoiceBusy { get { PollVoice(); return _voiceBusy; } }

    void PollVoice()
    {
        if (_voice != null && _voice.Ended) { _voiceBusy = false; _voice = null; }
    }

    /// <summary>A weapon hit: MaterialEffects[weapon fx][target fx] -> sfx event.</summary>
    public ISoundHandle? Hit(string? weaponFx, string? targetFx, V3? pos)
    {
        if (!Db.Material.TryGetValue(weaponFx ?? "", out var row)) return null;
        if (!row.TryGetValue(string.IsNullOrEmpty(targetFx) ? "Unit" : targetFx, out var e)) row.TryGetValue("Unit", out e);
        if (e != null && !string.IsNullOrEmpty(e[0])) return Play(e[0], pos, AudioChannel.Sfx);
        return null;
    }

    /// <summary>An animation sound event of a model (AnimSounds.Fire picks one of the alternatives); prio: what the
    /// entity is doing (an idle unit's chatter: Ambient - GameSession.Feedback).
    /// The speed: the GSF's pitch of the sound when the data has it (the client: the wav's rate x pitch, no random -
    /// C3DSound 0x7a2f70 via CSoundEventMgrClient 0x48fdf0), else 0.95 .. 1.05 at random (the browser remake). A sound
    /// started with a sound group is a trigger of the crowd sounds (CrowdSounds; client 0x4900e9).</summary>
    public ISoundHandle? AnimEvent(AnimSoundEvent ev, V3? pos, SoundPriority prio = SoundPriority.Activity, AudioChannel channel = AudioChannel.Sfx)
    {
        if (!Db.Files.TryGetValue((ev.Wav ?? "").ToLowerInvariant(), out var f) || string.IsNullOrEmpty(f)) return null;
        double rate = ev.Pitch > 0 ? ev.Pitch : 0.95 + Rnd() * 0.1;
        var h = PlayFile(f, ev.Vol, pos, ev.MaxHear != 0 ? ev.MaxHear : 50, channel, rate,
            prio: prio, minFade: ev.MinFade, anim: true);
        if (h != null && pos is V3 p && !string.IsNullOrEmpty(ev.Group)) Crowd.Trigger(ev.Group, p, Clock());
        return h;
    }

    /// <summary>A crowd sound (CrowdSounds.Play): the entry's wav (below assets/snd/, through the files table when it is
    /// there) at the crowd's middle with its volume, min fade and hearing distance - a world sound under the same limits
    /// (the client's priority 1, C3DSoundMgr 0x7a0cb0); the fights' din takes the fights' slots, the marching the
    /// walking's. An entry without a hearing distance is heard nowhere (0x48fdf0: beyond 0 m); the client passes no
    /// pitch for them (0): the wav's own speed.</summary>
    ISoundHandle? PlayCrowd(CrowdGroup g, CrowdAdd a, V3 pos)
    {
        if (a.MaxHear <= 0) return null;
        var file = Db.Files.TryGetValue(a.Wav.ToLowerInvariant(), out var f) && !string.IsNullOrEmpty(f) ? f : a.Wav;
        bool fight = g.Name.Contains("combat", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("fight", StringComparison.OrdinalIgnoreCase);
        var h = PlayFile(file, a.Volume, pos, a.MaxHear, AudioChannel.Sfx, 1, prio: fight ? SoundPriority.Combat : SoundPriority.Activity,
            minFade: a.MinFade);
        L.Debug(() => $"crowd sound {g.Name}: {a.Wav} at {pos.X:0.#},{pos.Z:0.#}{(h == null ? " (refused)" : "")}");
        return h;
    }

    /// <summary>Once a frame: the speech ack's end, the finished world sounds; every 10 s the counts of the world
    /// sounds (Debug: "world sounds in 10 s: ...").</summary>
    public void Frame()
    {
        PollVoice();
        _active.RemoveAll(w => w.H.Ended);
        double now = Clock();
        if (Out.Ready && !Muted) Crowd.Update(now);
        if (double.IsNaN(_statsT)) _statsT = now;
        if (now - _statsT >= 10)
        {
            if (Stats.Asked > 0) L.Debug($"world sounds in {now - _statsT:0} s: {Stats}; playing {_active.Count}");
            Stats.Clear();
            _statsT = now;
        }
    }

    /// <summary>Stop everything for good (exit): the music, the ambience, the sounds.</summary>
    public void Shutdown()
    {
        Muted = true;
        Mus.Shutdown();
        Atmo.Stop();
        Crowd.Clear();
        foreach (var w in _active) w.H.Stop();
        _active.Clear();
        _voice?.Stop();
        _voice = null; _voiceBusy = false;
    }
}
