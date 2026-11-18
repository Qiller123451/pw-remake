// The sound output in Godot: plays what AudioCore / Music / Atmo decide (the JS did it with WebAudio nodes and <audio>
// elements).
//   * every sound: an AudioStreamPlayer at the gain the logic computed - for a world sound the JS's (1 - d / R)^1.5 of
//     the original hearing distance x hearScale, once at its start as in the JS
//   * a sound without pan (2D): on its channel's bus
//   * a panned sound (world sounds: the JS StereoPannerNode from the camera's yaw; ambience events): on a pan bus of its
//     channel (AudioBuses.PanBus: WebAudio's equal-power law for the pan, in steps of 0.1). Not AudioStreamPlayer3D:
//     Godot's 3D panning law and distance models are not the JS's, and its listener needs a Camera3D in the viewport
//     (an AudioListener3D alone stays silent - checked headless)
//   * ramps (the ambience beds' fades): per frame, linear as WebAudio's linearRampToValueAtTime
//   * music: an AudioStreamPlayer per track on the Music bus (AudioStreamMP3, looped by the switch)
// Players are pooled; finished ones go back to the pool.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

public sealed partial class GodotAudioOut : Node, IAudioOut
{
    static readonly Logger L = Log.For("View.Audio");

    public AudioStreams Streams { get; private set; }
    readonly List<SoundHandle> _playing = new();
    readonly Stack<AudioStreamPlayer> _pool2 = new();
    readonly List<MusicTrack> _tracks = new();
    MusicTrack? _lastTrack;
    /// <summary>Sounds started since the start (the self test, the debug overlay).</summary>
    public int Started { get; private set; }
    public int Playing => _playing.Count;

    public GodotAudioOut(AssetSource? assets)
    {
        Name = "AudioOut";
        Streams = new AudioStreams(assets);
        ProcessMode = ProcessModeEnum.Always;     // (sound goes on while the game is paused: menus, dialogues)
    }

    // (Godot needs a parameterless constructor for nodes it makes itself; this one is only made in code)
    public GodotAudioOut() : this(null) { }

    public void SetAssets(AssetSource? assets) { Streams = new AudioStreams(assets); }

    public override void _Ready()
    {
        AudioBuses.Ensure();
    }

    bool IAudioOut.Ready => IsInsideTree();

    public ISoundBuffer? Load(string file) => Streams.Sound(file);

    public ISoundHandle? Start(ISoundBuffer buf, AudioChannel channel, double gain, double rate, double? pan, double offset, bool loop)
    {
        if (buf is not GodotSoundBuffer b || !IsInsideTree()) return null;
        var p = _pool2.Count > 0 ? _pool2.Pop() : NewPlayer();
        p.Stream = loop ? b.Looping() : b.Stream;
        p.Bus = pan is double pn ? AudioBuses.PanBus(channel, pn) : AudioBuses.Of(channel);
        p.PitchScale = (float)rate;
        var h = new SoundHandle(p, gain) { Loop = loop };
        p.Play((float)offset);
        _playing.Add(h);
        Started++;
        return h;
    }

    AudioStreamPlayer NewPlayer()
    {
        var p = new AudioStreamPlayer { Name = "Snd" };
        AddChild(p);
        return p;
    }

    public IMusicTrack OpenMusic(string file, bool loop, double startSec)
    {
        var stream = Streams.Music(file);
        var p = new AudioStreamPlayer { Name = "Music", Bus = AudioBuses.Music };
        AddChild(p);
        var t = new MusicTrack(file, loop, p, stream);
        if (stream != null)
        {
            // (a looped copy per track: the cached stream stays unlooped)
            var s = loop ? (AudioStreamMP3)stream.Duplicate() : stream;
            s.Loop = loop;
            p.Stream = s;
            if (s != stream) t.Own = s;
            t.Start = Math.Max(0, Math.Min(startSec, stream.GetLength() - 0.5));
        }
        t.Volume = 0;
        t.Play();
        // the older tracks that stand paused are done (Music only ever resumes the newest)
        for (int i = _tracks.Count - 1; i >= 0; i--)
            if (_tracks[i].Paused) { _tracks[i].Free(); _tracks.RemoveAt(i); }
        _tracks.Add(t);
        _lastTrack = t;
        L.Debug(() => $"music track {file}{(stream == null ? " (missing)" : $", {stream.GetLength():0} s from {t.Start:0.#} s")}{(loop ? ", looped" : "")}");
        return t;
    }

    public void ApplyVolumes(AudioVolumes v, bool muted) => AudioBuses.Apply(v, muted);

    public override void _Process(double delta)
    {
        for (int i = _playing.Count - 1; i >= 0; i--)
        {
            var h = _playing[i];
            h.Step(delta);
            if (h.Ended) { _playing.RemoveAt(i); Recycle(h); }
        }
    }

    void Recycle(SoundHandle h)
    {
        if (h.P != null && IsInstanceValid(h.P)) { h.P.Stop(); h.P.Stream = null; _pool2.Push(h.P); }
        h.P = null;
    }

    /// <summary>Stops every sound and track (exit, a new configuration).</summary>
    public void StopAll()
    {
        foreach (var h in _playing) { h.Stop(); Recycle(h); }
        _playing.Clear();
        foreach (var t in _tracks) t.Free();
        _tracks.Clear();
    }

    // ---------------------------------------------------------------- a playing sound
    public sealed class SoundHandle : ISoundHandle
    {
        internal AudioStreamPlayer? P;
        bool _stopped;
        double _gain, _rampFrom, _rampTo, _rampT, _rampSec;
        bool _ramping, _stopAfter;
        internal bool Loop;

        internal SoundHandle(AudioStreamPlayer p, double gain)
        {
            P = p;
            SetGain(gain);
        }

        public double Gain => _gain;
        public bool Ended => _stopped || P == null || !IsInstanceValid(P) || !P.Playing;

        void SetGain(double g)
        {
            _gain = g;
            if (P != null && IsInstanceValid(P)) P.VolumeDb = g <= 1e-5 ? -100f : (float)(20 * Math.Log10(g));
        }

        public void Stop()
        {
            _stopped = true;
            if (P != null && IsInstanceValid(P)) P.Stop();
        }

        public void Ramp(double gain, double sec, bool stopAfter)
        {
            _rampFrom = _gain; _rampTo = gain; _rampT = 0; _rampSec = Math.Max(1e-3, sec); _ramping = true; _stopAfter = stopAfter;
        }

        internal void Step(double dt)
        {
            if (!_ramping || _stopped) return;
            _rampT += dt;
            double k = Math.Min(1, _rampT / _rampSec);
            SetGain(_rampFrom + (_rampTo - _rampFrom) * k);
            if (k < 1) return;
            _ramping = false;
            if (_stopAfter) Stop();
        }
    }

    // ---------------------------------------------------------------- a music track (the JS <audio> element)
    public sealed class MusicTrack : IMusicTrack
    {
        AudioStreamPlayer? _p;
        readonly AudioStream? _stream;
        double _vol = 1;
        bool _ended, _paused = true, _started;
        internal double Start;
        internal AudioStream? Own;          // the track's own looped copy, freed with it

        internal MusicTrack(string file, bool loop, AudioStreamPlayer p, AudioStream? stream)
        {
            File = file; Loop = loop; _p = p; _stream = stream;
            p.Finished += () => { if (!Loop) _ended = true; };
        }

        public string File { get; }
        public bool Loop { get; }
        public bool Failed => _stream == null;
        public bool Ended => _ended;
        public bool Paused => _paused;
        public double CurrentTime => _p != null && IsInstanceValid(_p) && _p.Playing ? _p.GetPlaybackPosition() : Start;
        public double Volume
        {
            get => _vol;
            set
            {
                _vol = Math.Clamp(value, 0, 1);
                if (_p != null && IsInstanceValid(_p)) _p.VolumeDb = _vol <= 1e-5 ? -100f : (float)(20 * Math.Log10(_vol));
            }
        }

        public void Play()
        {
            if (_p == null || !IsInstanceValid(_p) || _stream == null) return;
            _paused = false;
            if (_started && _p.StreamPaused) { _p.StreamPaused = false; return; }
            _started = true;
            _p.Play((float)Start);
        }

        public void Pause()
        {
            _paused = true;
            if (_p == null || !IsInstanceValid(_p)) return;
            if (_p.Playing) Start = _p.GetPlaybackPosition();
            _p.StreamPaused = true;
        }

        internal void Free()
        {
            if (_p != null && IsInstanceValid(_p)) { _p.Stop(); _p.Stream = null; _p.QueueFree(); }
            _p = null;
            Own?.Dispose(); Own = null;
            _paused = true;
        }
    }
}
