// The audio buses, made in code (no bus layout file): Master and one bus per channel of engine/audio.js, each sending
// to Master. The player's volumes (settings: master, music, effects, speech, ambience) are the buses' volumes - the JS
// had a gain node per channel and one for the master.
//   Master <- Music     (music: Music.cs, the tracks' own volume is their fade)
//          <- Effects   ('sfx': world sounds, animation sounds, hits)
//          <- Speech    ('voice': speech acks, mission speech)
//          <- Ambience  ('atmo': Atmo.cs)
//          <- UI        ('ui': the interface's clicks)
// and, made when first needed, the pan buses of a channel ("Effects pan -0.3" -> Effects): a sound through the JS's
// StereoPannerNode (all world sounds, the ambience's events) plays there - WebAudio's equal-power law for a mono
// input (x = (pan + 1) / 2: left cos(x pi/2), right sin(x pi/2); a centred one 0.707 per side), made of the bus's volume
// and an AudioEffectPanner (which turns a mono input p into left (1 - p), right (1 + p)), in steps of 0.1.
using Godot;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

public static class AudioBuses
{
    static readonly Logger L = Log.For("View.Audio");
    public const string Master = "Master", Music = "Music", Effects = "Effects", Speech = "Speech", Ambience = "Ambience", Ui = "UI", Units = "Units";

    public static string Of(AudioChannel c) => c switch
    {
        AudioChannel.Sfx => Effects, AudioChannel.Unit => Units, AudioChannel.Voice => Speech, AudioChannel.Atmo => Ambience, AudioChannel.Ui => Ui, _ => Music,
    };

    /// <summary>Creates the buses that are missing (idempotent).</summary>
    public static void Ensure()
    {
        foreach (var name in new[] { Music, Effects, Speech, Ambience, Ui, Units })
        {
            if (AudioServer.GetBusIndex(name) >= 0) continue;
            AudioServer.AddBus();
            int i = AudioServer.BusCount - 1;
            AudioServer.SetBusName(i, name);
            AudioServer.SetBusSend(i, Master);
        }
        L.Debug($"audio buses: {AudioServer.BusCount}, driver {AudioServer.GetDriverName()}, mix rate {AudioServer.GetMixRate()}");
    }

    /// <summary>The bus of a sound through a stereo panner (-1 .. 1, in steps of 0.1) on a channel.</summary>
    public static string PanBus(AudioChannel c, double pan)
    {
        int step = (int)System.Math.Round(System.Math.Clamp(pan, -1, 1) * 10);
        string parent = Of(c), name = $"{parent} pan {step / 10.0:+0.0;-0.0;0.0}";
        if (AudioServer.GetBusIndex(name) >= 0) return name;
        Ensure();
        // the gains of WebAudio's StereoPannerNode for a mono input, as a volume and Godot's panner (mono p: 1 - p, 1 + p)
        double x = (step / 10.0 + 1) / 2, l = System.Math.Cos(x * System.Math.PI / 2), r = System.Math.Sin(x * System.Math.PI / 2);
        double vol = (l + r) / 2, gpan = (r - l) / (r + l);
        AudioServer.AddBus();
        int i = AudioServer.BusCount - 1;
        AudioServer.SetBusName(i, name);
        AudioServer.SetBusSend(i, parent);
        AudioServer.SetBusVolumeLinear(i, (float)vol);
        if (step != 0) { using var fx = new AudioEffectPanner { Pan = (float)gpan }; AudioServer.AddBusEffect(i, fx); }
        else AudioServer.SetBusVolumeLinear(i, (float)l);
        L.Debug($"pan bus {name}: volume {vol:0.###}, panner {gpan:0.###}");
        return name;
    }

    public static void Apply(AudioVolumes v, bool muted)
    {
        Ensure();
        Set(Master, muted ? 0 : v.Master);
        Set(Music, v.Music);
        Set(Effects, v.Sfx);
        Set(Speech, v.Voice);
        Set(Ambience, v.Atmo);
        Set(Ui, v.Ui);
    }

    static void Set(string bus, double linear)
    {
        int i = AudioServer.GetBusIndex(bus);
        if (i < 0) return;
        AudioServer.SetBusVolumeLinear(i, (float)System.Math.Max(0, linear));
        AudioServer.SetBusMute(i, linear <= 0);
    }

    public static double Volume(string bus)
    {
        int i = AudioServer.GetBusIndex(bus);
        return i < 0 ? -1 : AudioServer.GetBusVolumeLinear(i);
    }
}
