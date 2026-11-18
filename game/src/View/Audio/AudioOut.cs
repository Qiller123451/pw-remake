// What the sound logic (Audio, Music, Atmo - ports of engine/audio.js, music.js, atmo.js) needs from the machine that
// makes the sound: the browser's AudioContext and <audio> elements in the JS, Godot's AudioServer here
// (GodotAudioOut.cs), a recorder in the tests. The logic decides what plays, how loud, where; the output only plays.
// No Godot types: the tests link this file.
namespace PwRemake.View.Audio;

/// <summary>The channels of engine/audio.js; each is a bus (AudioBuses.cs).</summary>
/// <summary>Unit: what units say and do (acknowledgements, death cries, their animations' sounds) - its own volume
/// setting "Unit Sounds" (Kacper); Sfx: the other world sounds (hits, buildings, effects).</summary>
public enum AudioChannel { Sfx, Ui, Voice, Atmo, Music, Unit }

/// <summary>The volumes 0..1 (engine/audio.js vol). The settings set all but Ui (the JS kept 0.8).</summary>
public sealed class AudioVolumes
{
    public double Master = 0.8, Sfx = 0.9, Ui = 0.8, Voice = 1, Music = 0.45, Atmo = 0.8, Unit = 0.8;
    public double Of(AudioChannel c) => c switch
    {
        AudioChannel.Sfx => Sfx, AudioChannel.Unit => Unit, AudioChannel.Ui => Ui, AudioChannel.Voice => Voice, AudioChannel.Atmo => Atmo, _ => Music,
    };
}

/// <summary>A decoded sound file (the JS AudioBuffer).</summary>
public interface ISoundBuffer
{
    string File { get; }
    double Duration { get; }
}

/// <summary>A playing sound (the JS AudioBufferSourceNode + its gain).</summary>
public interface ISoundHandle
{
    /// <summary>It has finished (or was stopped).</summary>
    bool Ended { get; }
    double Gain { get; }
    void Stop();
    /// <summary>A linear ramp of the gain to `gain` over `sec` seconds (WebAudio linearRampToValueAtTime); then stops
    /// the sound when stopAfter.</summary>
    void Ramp(double gain, double sec, bool stopAfter);
}

/// <summary>A streamed music track (the JS &lt;audio&gt; element).</summary>
public interface IMusicTrack
{
    string File { get; }
    bool Loop { get; }
    /// <summary>The element's own volume 0..1 (the fades; the bus has the music and master volumes).</summary>
    double Volume { get; set; }
    bool Ended { get; }
    /// <summary>The file is missing or cannot be decoded (the JS 'error' event).</summary>
    bool Failed { get; }
    bool Paused { get; }
    double CurrentTime { get; }
    void Play();
    void Pause();
}

public interface IAudioOut
{
    /// <summary>Sound can play (the JS: an AudioContext exists and runs).</summary>
    bool Ready { get; }
    /// <summary>The decoded file below assets/snd/ (cached), or null when it is missing or unreadable.</summary>
    ISoundBuffer? Load(string file);
    /// <summary>Plays a decoded sound on a channel: gain (linear), playback rate, stereo pan -1..1 through an equal-power
    /// panner (WebAudio's StereoPannerNode: a centred one is 0.707 per side) or null for none (2D: full on both sides),
    /// offset seconds into the file, looped or not.</summary>
    ISoundHandle? Start(ISoundBuffer buf, AudioChannel channel, double gain, double rate, double? pan, double offset, bool loop);
    /// <summary>A music track (file below Audio/: "music/01_maintheme.mp3"), started at `startSec`, volume 0.</summary>
    IMusicTrack OpenMusic(string file, bool loop, double startSec);
    /// <summary>The volumes of the buses (master, channels) and whether everything is muted.</summary>
    void ApplyVolumes(AudioVolumes v, bool muted);
}
