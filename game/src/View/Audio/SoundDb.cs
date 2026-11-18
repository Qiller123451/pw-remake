// The game's sound tables: assets/sounds.json, built by the toolkit (pipeline/build_sounds.py) from the original's
// Scripts/Server/init/*.txt, Audio/MusicMgr.txt, Audio/MusicTracks.txt and Audio/AtmoTemplates:
//   events:   { name(lowercase): { g: global?, seq: sequential?, s: [[file, volume 0..100, maxHearingDistance(, minFade)], ...] } }
//   material: MaterialEffects.txt  { weaponFx: { targetFx: [sfxEvent, gfx] } }
//   files:    original wav path (lowercase) -> the file below assets/snd/ (the installation's Audio/Sound/<path>)
//   music:    { misc, switches, playlists, tracks } (engine/music.js)
//   atmo:     { template: [36 states: { w: [wav...], p: [[kind, a, b]...], e: [[wav#, a, b, c, d, e, f, g, h]...], v, r }] }
// Animation sound events come from the GSF models (manifest.models[x].sounds: AnimSounds.cs).
// The JSON shapes of engine/audio.js, typed. No Godot types.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

/// <summary>One sound of an event: the file below assets/snd/, its volume (0..100, some above) and the original
/// maximum hearing distance (0: the default 60); MinFade: its min fade distance when the data has it (a 4th value; 0:
/// SoundLimits.DefaultMinFade).</summary>
public readonly record struct SoundEntry(string File, double Vol, double MaxHear, double MinFade = 0);

/// <summary>A sound event (Scripts/Server/init/sounds*.txt): global (2D, the interface) or a world sound, its sounds
/// played in turn (seq) or at random.</summary>
public sealed class SoundEvent
{
    public bool Global;
    public bool Seq;
    public List<SoundEntry> S = new();
}

/// <summary>A state of an ambience template (Audio/AtmoTemplates): the wavs, the playlist, the event playlist, the
/// bed's volume and rate.</summary>
public sealed class AtmoTemplateState
{
    /// <summary>The template's wavs (paths below assets/snd/; "" = none).</summary>
    public List<string> W = new();
    /// <summary>The bed's playlist: [kind ("W"), a, b] - the templates' playlists are empty or "W1".</summary>
    public List<(string Kind, int A, int B)> P = new();
    /// <summary>The event playlist: [wav number (1-based), repeats a..b, delay c..d s, pan e..f (100 = middle), volume g..h].</summary>
    public List<double[]> E = new();
    public double V;
    public double R = 1;
}

/// <summary>A switch of Audio/MusicMgr.txt.</summary>
public sealed class MusicSwitchConf
{
    public double? FadeInMs, FadeOutMs;
    public int AutoselectType;
    public bool RestartOnActivate;
    /// <summary>autoselect_type 1: its tracks (Hometown, tribe_dependent: per tribe; GameEnd: "won"/"lost").</summary>
    public List<string>? Tracks;
    public Dictionary<string, List<string>>? TracksBy;
}

/// <summary>A music track of Audio/MusicTracks.txt: its file (below Audio/) and the start points (ms) of a restart.</summary>
public sealed class MusicTrack
{
    public string File = "";
    public List<double> Start = new();
}

/// <summary>sounds.json "music": MusicMgr.txt + MusicTracks.txt.</summary>
public sealed class MusicData
{
    public Dictionary<string, double> Misc = new();
    public Dictionary<string, MusicSwitchConf> Switches = new();
    /// <summary>Playlist name -> switch name (Opening, Background, Combat ...) -> track ids.</summary>
    public Dictionary<string, Dictionary<string, List<string>>> Playlists = new();
    public Dictionary<string, MusicTrack> Tracks = new();

    public double MiscNum(string k, double def) => Misc.TryGetValue(k, out var v) && v != 0 ? v : def;
}

public sealed class SoundDb
{
    static readonly Logger L = Log.For("View.Audio");

    public Dictionary<string, SoundEvent> Events = new();
    /// <summary>MaterialEffects.txt: weapon fx -> target fx -> [sfx event, gfx].</summary>
    public Dictionary<string, Dictionary<string, string[]>> Material = new();
    /// <summary>Original wav path (lower case) -> the file below assets/snd/.</summary>
    public Dictionary<string, string> Files = new();
    public MusicData? Music;
    public Dictionary<string, List<AtmoTemplateState>> Atmo = new();

    public static SoundDb Load(string path)
    {
        try { return Parse(JsonNode.Parse(File.ReadAllText(path))); }
        catch (Exception e) { L.Warn($"no sounds.json ({path})", e); return new SoundDb(); }
    }

    public static SoundDb Parse(JsonNode? root)
    {
        var db = new SoundDb();
        if (root is not JsonObject o) return db;
        if (o["events"] is JsonObject ev)
            foreach (var (name, n) in ev)
            {
                if (n is not JsonObject e) continue;
                var se = new SoundEvent { Global = e["g"].Bool(), Seq = e["seq"].Bool() };
                if (e["s"] is JsonArray s)
                    foreach (var x in s)
                        if (x is JsonArray a && a.Count > 0) se.S.Add(new SoundEntry(a[0].Str(), a.Count > 1 ? a[1].Num() : 70, a.Count > 2 ? a[2].Num() : 0, a.Count > 3 ? a[3].Num() : 0));
                db.Events[name.ToLowerInvariant()] = se;
            }
        if (o["material"] is JsonObject mat)
            foreach (var (w, n) in mat)
            {
                if (n is not JsonObject row) continue;
                var r = new Dictionary<string, string[]>();
                foreach (var (t, v) in row)
                    if (v is JsonArray a) r[t] = new[] { a.Count > 0 ? a[0].Str() : "", a.Count > 1 ? a[1].Str() : "" };
                db.Material[w] = r;
            }
        if (o["files"] is JsonObject files)
            foreach (var (k, v) in files) db.Files[k.ToLowerInvariant()] = v.Str();
        if (o["music"] is JsonObject mu) db.Music = ParseMusic(mu);
        if (o["atmo"] is JsonObject at)
            foreach (var (k, v) in at)
            {
                if (v is not JsonArray states) continue;
                var list = new List<AtmoTemplateState>();
                foreach (var sn in states)
                {
                    var st = new AtmoTemplateState();
                    if (sn is JsonObject so)
                    {
                        if (so["w"] is JsonArray w) foreach (var x in w) st.W.Add(x.Str());
                        if (so["p"] is JsonArray p)
                            foreach (var x in p)
                                if (x is JsonArray a && a.Count > 0) st.P.Add((a[0].Str(), a.Count > 1 ? a[1].Int() : 0, a.Count > 2 ? a[2].Int() : 0));
                        if (so["e"] is JsonArray e)
                            foreach (var x in e)
                                if (x is JsonArray a)
                                {
                                    var row = new double[9];
                                    for (int i = 0; i < 9 && i < a.Count; i++) row[i] = a[i].Num();
                                    st.E.Add(row);
                                }
                        st.V = so["v"].Num();
                        st.R = so["r"].Num();
                    }
                    list.Add(st);
                }
                db.Atmo[k.ToLowerInvariant()] = list;
            }
        return db;
    }

    static List<string> Strs(JsonNode? n)
    {
        var l = new List<string>();
        if (n is JsonArray a) foreach (var x in a) l.Add(x.Str());
        return l;
    }

    public static MusicData ParseMusic(JsonObject m)
    {
        var d = new MusicData();
        if (m["misc"] is JsonObject misc) foreach (var (k, v) in misc) d.Misc[k] = v.Num();
        if (m["switches"] is JsonObject sw)
            foreach (var (k, v) in sw)
            {
                if (v is not JsonObject c) continue;
                var conf = new MusicSwitchConf
                {
                    FadeInMs = c["fade_in_ms"] != null ? c["fade_in_ms"].Num() : null,
                    FadeOutMs = c["fade_out_ms"] != null ? c["fade_out_ms"].Num() : null,
                    AutoselectType = c["autoselect_type"].Int(),
                    RestartOnActivate = c["restart_on_activate"].Num() != 0,
                };
                if (c["tracks"] is JsonArray) conf.Tracks = Strs(c["tracks"]);
                else if (c["tracks"] is JsonObject by)
                {
                    conf.TracksBy = new Dictionary<string, List<string>>();
                    foreach (var (t, l) in by) conf.TracksBy[t] = Strs(l);
                }
                d.Switches[k] = conf;
            }
        if (m["playlists"] is JsonObject pl)
            foreach (var (k, v) in pl)
            {
                var lists = new Dictionary<string, List<string>>();
                if (v is JsonObject lo) foreach (var (s, l) in lo) lists[s] = Strs(l);
                d.Playlists[k] = lists;
            }
        if (m["tracks"] is JsonObject tr)
            foreach (var (k, v) in tr)
            {
                var t = new MusicTrack { File = v?["file"].Str() ?? "" };
                if (v?["start"] is JsonArray st) foreach (var x in st) t.Start.Add(x.Num());
                d.Tracks[k.ToLowerInvariant()] = t;
            }
        return d;
    }
}
