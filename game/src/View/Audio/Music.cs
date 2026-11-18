// The original's music manager (Scripts/Game/mgr/MusicMgr.usl) on the data it reads (sounds.json "music", built by
// pipeline/build_sounds.py from Audio/MusicMgr.txt and Audio/MusicTracks.txt):
//   switches   MusicMgr.txt: one per situation, the highest one that is on plays -
//              MainMenu < Loading < Background < BackgroundLeveled < Hometown < Region < Trigger < Opening < Combat
//              < Sequence < GameEnd; each with its fade in / out, how it picks a track (autoselect_type 1: its own
//              music_track_id_N, 2: the won / lost node, 3: the level's playlist) and whether it starts a track anew
//              (restart_on_activate: at a random start_ms_N of the track) or goes on where it stopped
//   playlists  MusicTracks.txt: per level (LevelName lower case, blanks -> '_': "single_05", "tutorial"), else per
//              setting (material_atmos: "savanna_txt"), else "default": Opening / Background / Combat tracks
//   tracks     id -> file below Data/Base/Audio (music/<id>.mp3)
// When the switches turn on (MusicMgr.usl Tick, UpdateMusicSwitch):
//   MainMenu   always (the intro is over); the rest only while a game runs
//   Background from the start of the game; Opening too when the playlist has one (played once)
//   Combat     the player's objects were attacked at 3 places within 10 s (the minimap's attack events,
//              FeedbackEventMgr.usl: one per 0.15 s of hits); off when there was none for 10 s
//   Hometown   the listener within 75 m of one of the player's home objects for 5 s - the query takes the class names
//              of MusicMgr.usl: "fireplace" and "seas_headquaters" match no object of the game, so only the Aje's
//              resource collector brings the tribe's theme, as in the original
//   Trigger / BackgroundLeveled  the trigger action MUSI (CActionMusic: once / looped)
//   Region, Sequence, GameEnd    never started by the game's scripts (MusicMgr.OnGameEnd has no caller): after the
//              end of a game its music goes on behind the end screen
// Tracks loop except Opening and Trigger, which turn their switch off when they end.
//
// Godot: the tracks are AudioStreamMP3 on the Music bus (GodotAudioOut.cs); the bus has the music and master volumes,
// a track's own volume is its fade x the duck while somebody speaks (JS: el.volume = music * master * duck * fade).
// Ported from remake/src/engine/music.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

/// <summary>The situations of MusicMgr.txt, lowest first (the highest one that is on plays).</summary>
public enum MusicSituation { MainMenu, Loading, Background, BackgroundLeveled, Hometown, Region, Trigger, Opening, Combat, Sequence, GameEnd }

/// <summary>A switch: on or off, its track, the file, where it stopped, the element that plays it.</summary>
public sealed class MusicSwitch
{
    public string Name = "";
    public bool On;
    public string Track = "";
    public string File = "";
    public double Pos;
    public IMusicTrack? El;
}

/// <summary>Music.State(): tests and the debug overlay.</summary>
public readonly record struct MusicStateInfo(string Playing, string Track, string[] On, int Attacks);

public sealed class Music
{
    static readonly Logger L = Log.For("View.Audio.Music");

    static readonly string[] Order = Enum.GetNames<MusicSituation>();
    static readonly HashSet<string> Once = new() { "Trigger", "Opening" };
    static readonly string[] GameSw = Order.Skip(2).ToArray();

    /// <summary>The player's home objects for the Hometown switch (MusicMgr.usl CheckHomeTownSwitch: an object query by class name).</summary>
    public static readonly Dictionary<string, string> HomeClass = new()
    {
        ["SEAS"] = "seas_headquaters", ["Aje"] = "aje_resource_collector", ["Hu"] = "fireplace", ["Ninigi"] = "fireplace",
    };

    // the original's tables, for game data prepared before they were exported
    static MusicData Fallback()
    {
        var d = new MusicData();
        d.Misc["hometown_switch_delay_in_seconds"] = 5; d.Misc["hometown_range_in_meters"] = 75;
        d.Misc["combat_delay_in_seconds"] = 10; d.Misc["combat_attack_trigger"] = 3;
        d.Switches["MainMenu"] = new MusicSwitchConf { FadeInMs = 5000, AutoselectType = 1, RestartOnActivate = true, Tracks = new() { "01_maintheme" } };
        d.Switches["Background"] = new MusicSwitchConf { FadeInMs = 5000, AutoselectType = 3, RestartOnActivate = true };
        d.Switches["Combat"] = new MusicSwitchConf { FadeInMs = 2000, FadeOutMs = 10000, AutoselectType = 3 };
        d.Playlists["default"] = new()
        {
            ["Background"] = new() { "11_plain_jungle_1", "12_plain_jungle_2", "05_plain_northland_2" },
            ["Combat"] = new() { "35_combat_hu_1", "40_combat_heroes", "41_combat_hu_2", "42_combat_aje_2", "47_var_combat_hu_2" },
        };
        return d;
    }

    /// <summary>The playlist name of a level: its LevelName, else its setting's atmosphere file, else "default".</summary>
    public static string PlaylistName(IReadOnlyDictionary<string, Dictionary<string, List<string>>> lists, string? levelName, string? atmos)
    {
        static string Key(string? s, bool dot)
        {
            var k = (s ?? "").ToLowerInvariant().Replace(' ', '_');
            return dot ? k.Replace('.', '_') : k;
        }
        string a = Key(levelName, false), b = Key(atmos, true);
        return a != "" && lists.ContainsKey(a) ? a : b != "" && lists.ContainsKey(b) ? b : "default";
    }

    readonly AudioCore A;
    public MusicData Data = Fallback();
    IMusicTrack? _el;                    // the playing track
    /// <summary>Index of the switch that plays (-1: none yet).</summary>
    public int Current = -1;
    public readonly MusicSwitch[] Sw;
    public bool Running;
    /// <summary>The last attack places [x, z, t].</summary>
    public List<(double X, double Z, double T)> Attacks = new();
    (bool Inside, double T, double Next) _home = (false, -1e9, 0);
    Dictionary<string, List<string>>? _level;
    public string? Tribe;
    readonly List<Fade> _fades = new();

    sealed class Fade { public IMusicTrack? El, Old; public double Elapsed, T, V0; }

    public Music(AudioCore audio)
    {
        A = audio;
        Sw = Order.Select(n => new MusicSwitch { Name = n }).ToArray();
        Sw[0].On = true;
    }

    public void SetData(MusicData? m) { if (m != null && m.Switches.Count > 0) Data = m; }
    double Misc(string k, double def) => Data.MiscNum(k, def);
    public MusicSwitch S(string name) => Sw[Array.IndexOf(Order, name)];
    public MusicSwitch S(MusicSituation s) => Sw[(int)s];
    MusicSwitchConf Conf(string name) => Data.Switches.TryGetValue(name, out var c) ? c : new MusicSwitchConf();
    /// <summary>The level's playlist (tests).</summary>
    public Dictionary<string, List<string>>? Level => _level;

    // ---------------------------------------------------------------- what the game tells it
    /// <summary>A game starts: its playlist (MusicMgr.usl:701-738). levelName: the map's LevelName; atmos: its
    /// material_atmos ("jungle.txt"); tribe: the player's.</summary>
    public void StartGame(string? levelName, string? atmos, string? tribe)
    {
        foreach (var n in GameSw) S(n).On = false;
        S("Opening").Track = "";
        Running = true; Attacks = new(); _home = (false, -1e9, 0);
        var lists = Data.Playlists;
        var name = PlaylistName(lists, levelName, atmos);
        _level = lists.TryGetValue(name, out var l) ? l : new();
        Tribe = tribe;
        L.Info($"game music: playlist {name} (level '{levelName}', atmosphere '{atmos}'), tribe {tribe}");
        if (_level.ContainsKey("Opening")) S("Opening").On = true;
        S("Background").On = true;
        Update(0);
    }

    /// <summary>Back in the menus: the main theme.</summary>
    public void StopGame()
    {
        Running = false;
        foreach (var n in GameSw) S(n).On = false;
        Update(0);
    }

    /// <summary>An attack on one of the player's objects (the minimap's attack event; MusicMgr.usl OnUnderAttack).</summary>
    public void Attack(double x, double z, double time)
    {
        int n = (int)Misc("combat_attack_trigger", 3);
        int same = Attacks.FindIndex(a => a.X == x && a.Z == z);
        if (same >= 0) { Attacks[same] = (x, z, time); return; }
        if (Attacks.Count >= n)
        {
            int k = 0;
            for (int i = 1; i < Attacks.Count; i++) if (time - Attacks[i].T < time - Attacks[k].T) k = i;
            Attacks[k] = (x, z, time);
        }
        else Attacks.Add((x, z, time));
    }

    /// <summary>The fights are over (a game or mission ended): no combat music behind the end screen - its attack
    /// times stand still with the game, so the Combat switch would stay on (main.js stopCombatMusic).</summary>
    public void StopCombat()
    {
        Attacks.Clear();
        S("Combat").On = false;
        Refresh();
    }

    /// <summary>A cutscene starts / ends (MusicMgr.usl OnSequenceStart / OnSequenceEnd): the Sequence switch - above
    /// Combat - is on for its length with "-silence-" (the remake's cutscenes play their own music items).</summary>
    public void Sequence(bool on)
    {
        var s = S("Sequence");
        if (on) { s.On = true; SetTrack(s, "-silence-"); } else { s.On = false; s.Track = ""; s.File = ""; }
        if (Current == Array.IndexOf(Sw, s) && on) Current = -1;
        Refresh();
    }

    /// <summary>A journal entry is read aloud on the loading screen (MusicMgr.usl OnStartJournalSound /
    /// OnEndJournalSound): the Loading switch - the original's JOURNAL_MUSIC, above MainMenu - is on with "-silence-"
    /// while it plays, so the main theme pauses for the voice and comes back after it.</summary>
    public void Journal(bool on)
    {
        var s = S("Loading");
        if (on) { s.On = true; SetTrack(s, "-silence-"); } else { s.On = false; s.Track = ""; s.File = ""; }
        if (Current == Array.IndexOf(Sw, s) && on) Current = -1;
        Refresh();
    }

    /// <summary>The trigger action MUSI: a track id or a file below Audio/ ('music/x.mp3'); looped = BackgroundLeveled.</summary>
    public void Trigger(string track, bool loop)
    {
        var s = S(loop ? "BackgroundLeveled" : "Trigger");
        s.On = true;
        SetTrack(s, track);
        if (Current == Array.IndexOf(Sw, s)) Current = -1;     // play the new track at once
    }

    /// <summary>Turns a situation on or off directly (debug, own scripts); the next Update plays the highest one.</summary>
    public void SetSituation(MusicSituation sit, bool on)
    {
        S(sit).On = on;
        if (sit == MusicSituation.Sequence) { Sequence(on); return; }
        Refresh();
    }

    // ---------------------------------------------------------------- every frame
    /// <summary>time: game seconds; listener (x, z); homes(): positions of the player's home objects (asked every second).</summary>
    public void Update(double time, V3? listener = null, Func<IEnumerable<V3>>? homes = null)
    {
        if (Running)
        {
            Combat(time);
            if (listener is V3 li && homes != null) Hometown(time, li, homes);
            // Opening and Trigger end with their track (or at once when it is silence or its file is missing)
            foreach (var n in Once)
            {
                var s = S(n);
                if (s.On && Current == Array.IndexOf(Sw, s) && (s.El == null || s.El.Ended || s.El.Failed)) s.On = false;
            }
        }
        int k = Highest();
        if (k < 0) return;
        if (k != Current) Activate(k);
        else if (Current >= 0 && _el != null) Sw[Current].Pos = _el.CurrentTime;
    }

    int Highest()
    {
        for (int i = Sw.Length - 1; i >= 0; i--) if (Sw[i].On) return i;
        return -1;
    }

    /// <summary>Switch to the highest switch that is on, without the timed checks.</summary>
    public void Refresh()
    {
        int k = Highest();
        if (k >= 0 && k != Current) Activate(k);
    }

    void Combat(double time)
    {
        double delay = Misc("combat_delay_in_seconds", 10);
        int n = (int)Misc("combat_attack_trigger", 3);
        Attacks = Attacks.Where(a => time - a.T <= delay).ToList();
        var c = S("Combat");
        c.On = c.On ? Attacks.Count > 0 : Attacks.Count >= n;
    }

    void Hometown(double time, V3 li, Func<IEnumerable<V3>> homes)
    {
        double r = Misc("hometown_range_in_meters", 75);
        if (time >= _home.Next)
        {
            _home.Next = time + 1;
            bool inside = homes().Any(p => (p.X - li.X) * (p.X - li.X) + (p.Z - li.Z) * (p.Z - li.Z) < r * r);
            if (inside != _home.Inside) { _home.Inside = inside; _home.T = time; }
        }
        var s = S("Hometown");
        if (s.On != _home.Inside && time - _home.T > Misc("hometown_switch_delay_in_seconds", 5)) s.On = _home.Inside;
    }

    // ---------------------------------------------------------------- switching
    // the track a switch plays (SMusicSwitch.OnActivate / SetMusicTrack)
    void Pick(MusicSwitch s)
    {
        var c = Conf(s.Name);
        int auto = c.AutoselectType;
        if (auto == 0) return;
        List<string>? list;
        if (auto == 3) list = _level != null && _level.TryGetValue(s.Name, out var l) ? l : null;
        else list = c.Tracks ?? (c.TracksBy != null && Tribe != null && c.TracksBy.TryGetValue(Tribe, out var t) ? t : null);   // Hometown: per tribe
        if (auto == 2) list = null;                                 // GameEnd: never started
        if (list == null || list.Count == 0) { SetTrack(s, "-silence-"); return; }
        SetTrack(s, list[(int)Math.Floor(A.Rnd() * list.Count)]);
    }

    void SetTrack(MusicSwitch s, string? id)
    {
        id = (id ?? "").ToLowerInvariant();
        if (id != s.Track) s.Pos = 0;
        s.Track = id;
        if (id == "-silence-" || id == "") { s.File = ""; s.Pos = 0; return; }
        if (id.EndsWith(".mp3", StringComparison.Ordinal)) { s.File = id; return; }
        Data.Tracks.TryGetValue(id, out var t);
        s.File = t != null && t.File != "" ? t.File : $"music/{id}.mp3";
        if (Conf(s.Name).RestartOnActivate)
            s.Pos = t != null && t.Start.Count > 0 ? t.Start[(int)Math.Floor(A.Rnd() * t.Start.Count)] / 1000 : 0;
    }

    void Activate(int k)
    {
        int prev = Current;
        var s = Sw[k];
        double fOut = prev >= 0 ? Conf(Sw[prev].Name).FadeOutMs ?? 0 : 0;
        double fIn = Conf(s.Name).FadeInMs ?? 5000;
        double fade = fOut == 0 && fIn != 0 ? fIn : fOut != 0 && fIn == 0 ? fOut : k > prev ? fIn : fOut;
        Pick(s);
        Current = k;
        L.Debug(() => $"music: {s.Name} '{s.Track}' ({(s.File == "" ? "silence" : s.File)}{(s.Pos > 0 ? $" from {s.Pos:0.#} s" : "")}), fade {fade} ms");
        Play(s, fade);
    }

    void Play(MusicSwitch s, double fadeMs)
    {
        var old = _el;
        if (old != null && s.File != "" && old.File == s.File && old.Loop == !Once.Contains(s.Name)) { s.El = old; return; }
        IMusicTrack? el = null;
        if (s.File != "")
        {
            el = A.Out.OpenMusic(s.File, !Once.Contains(s.Name), s.Pos);
            el.Volume = 0;     // (a missing file: the output warns; the switch goes on silent)
        }
        s.El = el;
        _el = el;
        var f = new Fade { El = el, Old = old, T = Math.Max(1, fadeMs) / 1000, V0 = old?.Volume ?? 0 };
        _fades.Add(f);
        Step(f);
    }

    /// <summary>Volume of the playing track: the fades run on top (the bus has the music and master volumes).</summary>
    public double Volume() => A.Muted ? 0 : A.DuckK;

    /// <summary>Real seconds passed: the fades (the JS ran them on requestAnimationFrame).</summary>
    public void StepFades(double dt)
    {
        for (int i = 0; i < _fades.Count; i++) { _fades[i].Elapsed += dt; if (Step(_fades[i])) _fades.RemoveAt(i--); }
    }

    bool Step(Fade f)
    {
        double k = Math.Min(1, f.Elapsed / f.T);
        if (f.El != null && _el == f.El) f.El.Volume = Volume() * k;
        if (f.Old != null && f.Old != _el) f.Old.Volume = Math.Max(0, f.V0 * (1 - k));
        if (k < 1) return false;
        if (f.Old != null && f.Old != _el) f.Old.Pause();
        return true;
    }

    /// <summary>Start what could not start before (the output was not ready).</summary>
    public void Resume() { if (_el != null && _el.Paused && !_el.Ended) _el.Play(); }
    public void ApplyVolume() { if (_el != null) _el.Volume = Volume(); }
    public void Shutdown()
    {
        try { _el?.Pause(); } catch (Exception) { /* ignore */ }
        foreach (var f in _fades) { try { f.Old?.Pause(); } catch (Exception) { /* ignore */ } }
        _fades.Clear();
        _el = null;
    }
    /// <summary>The playing track (tests, debug).</summary>
    public IMusicTrack? El => _el;
    /// <summary>Tests / the debug overlay.</summary>
    public MusicStateInfo State() => new(Current >= 0 ? Sw[Current].Name : "", Current >= 0 ? Sw[Current].Track : "",
        Sw.Where(s => s.On).Select(s => s.Name).ToArray(), Attacks.Count);
}
