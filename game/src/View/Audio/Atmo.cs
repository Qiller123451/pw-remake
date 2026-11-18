// The ambience of a level - the client's CAtmoSoundMgr (PWClient.exe 0x432d80 .. 0x436e9d), driven by the level's
// atmosphere template (Root/Base/material_atmos "jungle.txt" ...; "No Atmos": silence) from Audio/AtmoTemplates
// (pipeline/build_sounds.py atmo -> sounds.json "atmo").
//
// A template has 36 states: the ground material of the 32 m square in the middle of the view (the most frequent of
// its 4 m ground materials; a square of forest counts as forest: index 8), whether the camera is more than 12 m
// above the ground ("HeightBreakPoint") and day or night. The state's bed (wav1, or the wav its playlist names) loops
// at the state's volume; when the state changes the new bed fades in and the old one out (after FadeDelay 0.5 s for
// another material, at once for height or daytime). Its event playlist plays one entry at a time: the entry's wav,
// a .. b times, every c .. d s, at volume e .. f (0: silent - most entries are pauses) and pan g .. h - 100 %; then a
// random other entry.
// The ambience has its own volume (SOUND_VOLTRACK_ATMO: Options, "Ambience") - the Ambience bus.
//
// The game computes the state (main.js atmoState: the sound material of the map under the view - game/maps/source.js
// soundMaterial, owned by the maps - the camera's height above the ground, the world's daytime) and calls
// Update(dt, Atmo.State(mat, high, day), time) once a frame.
// Ported from remake/src/engine/atmo.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

public sealed class Atmo
{
    static readonly Logger L = Log.For("View.Audio.Atmo");
    public const double FadeDelay = 0.5, FadeTime = 1.5;

    readonly AudioCore A;
    /// <summary>The template's 36 states (null: silence).</summary>
    public List<AtmoTemplateState>? T;
    /// <summary>The state playing (-1: none).</summary>
    public int Cur = -1;
    int _want = -1;
    double _wantT;
    readonly List<ISoundHandle> _beds = new();
    // the event playlist's place
    sealed class EvPlace { public int I; public int Left = -1; public double Next = -1; }
    EvPlace? _ev;

    public Atmo(AudioCore audio) { A = audio; }

    double Rnd(double a, double b) => a + A.Rnd() * (b - a);

    /// <summary>name = the level's material_atmos ("jungle.txt"; "No Atmos" or unknown: silence).</summary>
    public void Start(string? name)
    {
        Stop();
        Load(name);
    }

    /// <summary>The template of the ambience key now ("jungle"), null before Start.</summary>
    public string? Key { get; private set; }

    /// <summary>Another template while the game runs (a map's biomes: the camera moved into another one): the beds
    /// playing fade out over FadeTime while the new template's state fades in. The same template: nothing.</summary>
    public void Switch(string? name)
    {
        if (KeyOf(name) == Key) return;
        foreach (var b in _beds) FadeOut(b, FadeTime);
        _beds.Clear(); Cur = -1; _want = -1; _ev = null;
        Load(name);
    }

    static string KeyOf(string? name)
    {
        var key = (name ?? "").ToLowerInvariant();
        if (key.EndsWith(".txt") || key.EndsWith("_txt")) key = key.Substring(0, key.Length - 4);
        return key;
    }

    void Load(string? name)
    {
        Key = KeyOf(name);
        T = A.Db.Atmo.TryGetValue(Key, out var t) ? t : null;
        L.Info($"ambience '{name}': {(T != null ? T.Count + " states" : "silence")}");
    }

    public void Stop()
    {
        foreach (var b in _beds) FadeOut(b, 0.2);
        _beds.Clear(); Cur = -1; _want = -1; _ev = null;
    }

    /// <summary>The state for the ground material (0 .. 8), the camera more than 12 m above the ground, and the daytime.</summary>
    public static int State(int mat, bool high, bool day) => mat + 9 * ((high ? 1 : 0) + 2 * (day ? 1 : 0));

    public void Update(double dt, int state, double time)
    {
        if (T == null || !A.Out.Ready || A.Muted) return;
        if (state != _want)
        {
            // another material waits FadeDelay, the height or daytime of the same material switches at once
            bool sameMat = Cur >= 0 && state % 9 == Cur % 9;
            _want = state; _wantT = sameMat ? 0 : FadeDelay;
        }
        if (_want != Cur && (_wantT -= dt) <= 0) SwitchTo(_want);
        Events(time);
    }

    void SwitchTo(int i)
    {
        var S = T != null && i >= 0 && i < T.Count ? T[i] : null;
        foreach (var b in _beds) FadeOut(b, FadeTime);
        _beds.Clear();
        Cur = i; _ev = null;
        if (S == null) return;
        // the bed: wav1, or the wav of the playlist's W entry (the templates' playlists are '' or "W1")
        var w = S.P.FirstOrDefault(e => e.Kind == "W");
        int wi = Math.Max(0, Math.Min(2, (w.Kind == "W" ? w.A : 1) - 1));
        var file = wi < S.W.Count ? S.W[wi] : "";
        L.Debug(() => $"ambience state {i} (material {i % 9}, {(i / 9 % 2 == 1 ? "high" : "low")}, {(i / 18 == 1 ? "day" : "night")}): bed '{file}'");
        if (!string.IsNullOrEmpty(file)) Bed(file, S.V / 100, S.R != 0 ? S.R : 1);
    }

    void Bed(string file, double vol, double rate)
    {
        var buf = A.Buffer(file);
        if (buf == null || !A.Out.Ready) return;
        var src = A.Out.Start(buf, AudioChannel.Atmo, 0.0001, rate, null, 0, true);
        if (src == null) return;
        src.Ramp(vol, FadeTime, false);
        _beds.Add(src);
    }

    static void FadeOut(ISoundHandle b, double sec)
    {
        try { b.Ramp(0.0001, sec, true); } catch (Exception) { /* already stopped */ }
    }

    /// <summary>The beds playing (tests).</summary>
    public IReadOnlyList<ISoundHandle> Beds => _beds;

    // the event playlist of the state (0x4332a0): the entry's repeats, its delays, then a random entry
    void Events(double time)
    {
        var S = Cur >= 0 && T != null && Cur < T.Count ? T[Cur] : null;
        if (S == null || S.E.Count == 0) return;
        var ev = _ev ??= new EvPlace();
        var e = S.E[ev.I];
        if (ev.Next < 0) ev.Next = time + Rnd(e[3], e[4]);
        if (ev.Left < 0) ev.Left = (int)Math.Round(Rnd(e[1], e[2]), MidpointRounding.AwayFromZero);
        if (time < ev.Next) return;
        double vol = Rnd(e[7], e[8]), pan = (Rnd(e[5], e[6]) - 100) / 100;
        ev.Next = time + Rnd(e[3], e[4]);
        int wi = (int)e[0] - 1;
        var file = wi >= 0 && wi < S.W.Count ? S.W[wi] : "";
        if (!string.IsNullOrEmpty(file) && vol > 0) A.PlayFile(file, vol, null, 0, AudioChannel.Atmo, 1, pan);
        if (--ev.Left <= 0) { ev.I = (int)Math.Floor(A.Rnd() * S.E.Count); ev.Left = -1; ev.Next = -1; }
    }
}
