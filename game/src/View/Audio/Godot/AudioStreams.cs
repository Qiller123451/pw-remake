// The game's sound files as Godot streams, cached: looked up through the configuration's AssetSource ("assets/snd/<p>"
// is the installation's Audio/Sound/<p>, "assets/music/<f>" its Audio/Music/<f>), IMA ADPCM wavs decoded by WavDecoder
// to 16-bit PCM (AudioStreamWav), the other wavs loaded by Godot (AudioStreamWav.LoadFromBuffer), mp3 (music, the
// cutscenes' SeqSounds) as AudioStreamMP3 from their bytes. The JS cached its AudioBuffers the same way (audio.buffer).
using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

/// <summary>A loaded sound: its stream, and a looping copy made when something loops it (an ambience bed).</summary>
public sealed class GodotSoundBuffer : ISoundBuffer
{
    public string File { get; init; } = "";
    public double Duration { get; init; }
    public AudioStream Stream = null!;
    /// <summary>The decoded PCM of an IMA ADPCM file (tests, the self test); null for files Godot loaded.</summary>
    public PcmSound? Pcm;
    AudioStream? _loop;

    public void Dispose() { if (_loop != null && _loop != Stream) _loop.Dispose(); Stream.Dispose(); _loop = null; }

    public AudioStream Looping()
    {
        if (_loop != null) return _loop;
        switch (Stream)
        {
            case AudioStreamWav w:
            {
                var c = (AudioStreamWav)w.Duplicate();
                int bytesPerFrame = (w.Format == AudioStreamWav.FormatEnum.Format8Bits ? 1 : 2) * (w.Stereo ? 2 : 1);
                c.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                c.LoopBegin = 0;
                c.LoopEnd = w.Format is AudioStreamWav.FormatEnum.Format8Bits or AudioStreamWav.FormatEnum.Format16Bits
                    ? w.Data.Length / bytesPerFrame : (int)(w.GetLength() * w.MixRate);
                _loop = c;
                break;
            }
            case AudioStreamMP3 m:
            {
                var c = (AudioStreamMP3)m.Duplicate();
                c.Loop = true;
                _loop = c;
                break;
            }
            default: _loop = Stream; break;
        }
        return _loop;
    }
}

public sealed class AudioStreams
{
    static readonly Logger L = Log.For("View.Audio");
    readonly AssetSource? _assets;
    readonly Dictionary<string, GodotSoundBuffer?> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AudioStreamMP3?> _music = new(StringComparer.OrdinalIgnoreCase);

    public AudioStreams(AssetSource? assets) { _assets = assets; }

    public int Count => _cache.Count;
    public int Decoded { get; private set; }
    public int Failed { get; private set; }

    /// <summary>A file below assets/snd/ (the paths of sounds.json); null when missing or unreadable (logged once).</summary>
    public GodotSoundBuffer? Sound(string file)
    {
        if (_cache.TryGetValue(file, out var b)) return b;
        b = null;
        var path = _assets?.Resolve("assets/snd/" + file);
        if (path == null) L.WarnOnce("snd:" + file, $"sound file missing: {file}");
        else
        {
            try { b = FromBytes(file, File.ReadAllBytes(path)); }
            catch (Exception e) { L.Warn($"cannot read sound {path}", e); }
            if (b == null) L.WarnOnce("snddec:" + file, $"sound file not readable: {path}");
        }
        if (b == null) Failed++; else Decoded++;
        _cache[file] = b;
        return b;
    }

    /// <summary>A sound from its bytes: IMA ADPCM decoded here, MP3 in a wav (0x55) as mp3, other wavs by Godot, mp3.</summary>
    public static GodotSoundBuffer? FromBytes(string file, byte[] bytes)
    {
        if (file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var m = Mp3(bytes);
            return m == null ? null : new GodotSoundBuffer { File = file, Stream = m, Duration = m.GetLength() };
        }
        // (an MPEG layer 3 stream in a wav - format 0x55: some of MIRAGE's sounds, ui_seas_headquarters.wav ...)
        if (WavDecoder.ReadChunks(bytes) is { fmt.Tag: 0x55 } mp)
        {
            var m = Mp3(bytes.AsSpan(mp.dataOffset, mp.dataSize).ToArray());
            return m == null ? null : new GodotSoundBuffer { File = file, Stream = m, Duration = m.GetLength() };
        }
        var pcm = WavDecoder.DecodeImaAdpcm(bytes);
        if (pcm != null)
        {
            var w = new AudioStreamWav
            {
                Format = AudioStreamWav.FormatEnum.Format16Bits, Stereo = pcm.Channels == 2, MixRate = pcm.Rate, Data = pcm.ToBytes(),
            };
            return new GodotSoundBuffer { File = file, Stream = w, Duration = pcm.Duration, Pcm = pcm };
        }
        AudioStreamWav? s = null;
        try { s = AudioStreamWav.LoadFromBuffer(bytes, new Godot.Collections.Dictionary()); } catch (Exception) { s = null; }
        if (s == null)
        {
            // (formats Godot does not take: 8/16-bit PCM through our own reader)
            var p = WavDecoder.DecodePcm(bytes);
            if (p == null) return null;
            s = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, Stereo = p.Channels == 2, MixRate = p.Rate, Data = p.ToBytes() };
        }
        return new GodotSoundBuffer { File = file, Stream = s, Duration = s.GetLength() };
    }

    static AudioStreamMP3? Mp3(byte[] bytes)
    {
        try
        {
            var m = AudioStreamMP3.LoadFromBuffer(bytes);
            return m != null && m.GetLength() > 0 ? m : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Forgets the cached streams (exit, another configuration).</summary>
    public void Clear()
    {
        foreach (var b in _cache.Values) b?.Dispose();
        foreach (var m in _music.Values) m?.Dispose();
        _cache.Clear(); _music.Clear();
    }

    /// <summary>A music file below Audio/ ("music/01_maintheme.mp3": assets/music/... -> Audio/Music/...); null when
    /// missing (the JS element's 'error').</summary>
    public AudioStreamMP3? Music(string file)
    {
        if (_music.TryGetValue(file, out var m)) return m;
        m = null;
        var path = _assets?.Resolve("assets/" + file);
        if (path == null) L.WarnOnce("mus:" + file, $"music file missing: {file}");
        else
        {
            try { m = Mp3(File.ReadAllBytes(path)); } catch (Exception e) { L.Warn($"cannot read music {path}", e); }
            if (m == null) L.WarnOnce("musdec:" + file, $"music file not readable: {path}");
        }
        _music[file] = m;
        return m;
    }
}
