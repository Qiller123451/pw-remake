// Tests of the game's sound logic (game/src/View/Audio, linked into this project): the IMA ADPCM decoder against the
// JS decoder (tests/golden/audio_adpcm.json), the event resolution, speech acks, the music manager and the ambience
// against the JS run in node with the same random numbers (tests/golden/audio_logic.json), the feedback rules, the
// animation sound tables on the real data and the original's limits of world sounds (a crowd of idle animals, a fight).
//   PW_DATA   the converted data (assets/sounds.json, assets/manifest.json) - the goldens were made from /opt/data/base
//   PW_SOUND  the installation's Audio/Sound folder with some of the game's wavs (the golden lists the ones it used;
//             default: /mnt/user-data/uploads/Paraworld/Data/Base/Audio/Sound)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.View.Audio;

namespace PwRemake.Tests;

/// <summary>An output that plays nothing and records what would play (the tests' AudioContext).</summary>
public sealed class RecordingAudioOut : IAudioOut
{
    public sealed class Buf : ISoundBuffer { public string File { get; init; } = ""; public double Duration { get; init; } }
    public sealed class Handle : ISoundHandle
    {
        public string File = ""; public AudioChannel Channel; public double Rate, Pan, Offset; public bool Loop, Panner;
        public double StartGain;
        public bool Ended { get; set; }
        public double Gain { get; set; }
        public double RampTo = -1, RampSec; public bool StopAfter;
        public void Stop() => Ended = true;
        public void Ramp(double gain, double sec, bool stopAfter) { RampTo = gain; RampSec = sec; StopAfter = stopAfter; }
    }
    public sealed class Track : IMusicTrack
    {
        public string File { get; init; } = "";
        public bool Loop { get; init; }
        public double Volume { get; set; } = 1;
        public bool Ended { get; set; }
        public bool Failed { get; set; }
        public bool Paused { get; set; } = true;
        public double CurrentTime { get; set; }
        public void Play() => Paused = false;
        public void Pause() => Paused = true;
    }

    public bool Ready { get; set; } = true;
    public double Duration = 2;
    public readonly List<Handle> Starts = new();
    public readonly List<Track> Tracks = new();
    public Func<string, bool> Exists = f => !f.Contains("missing");

    public ISoundBuffer? Load(string file) => Exists(file) ? new Buf { File = file, Duration = Duration } : null;
    public ISoundHandle? Start(ISoundBuffer buf, AudioChannel channel, double gain, double rate, double? pan, double offset, bool loop)
    {
        var h = new Handle { File = buf.File, Channel = channel, StartGain = gain, Gain = gain, Rate = rate, Pan = pan ?? 0, Panner = pan != null, Offset = offset, Loop = loop };
        Starts.Add(h);
        return h;
    }
    public IMusicTrack OpenMusic(string file, bool loop, double startSec)
    {
        // (the JS element: currentTime = min(start, duration - 0.5) when the metadata is there; the fake's duration is 1000)
        var t = new Track { File = file, Loop = loop, CurrentTime = startSec > 0 ? Math.Min(startSec, 999.5) : 0, Failed = !Exists(file) };
        t.Play();
        Tracks.Add(t);
        return t;
    }
    public void ApplyVolumes(AudioVolumes v, bool muted) { }
}

[TestClass]
public static class AudioTests
{
    /// <summary>mulberry32: the random numbers of the goldens (tests/golden/audio_logic.mjs).</summary>
    public static Func<double> Mulberry32(uint seed)
    {
        uint s = seed;
        return () =>
        {
            s += 0x6D2B79F5;
            uint t = (s ^ (s >> 15)) * (1 | s);
            t = (t + (t ^ (t >> 7)) * (61 | t)) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        };
    }

    static string GoldenDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var g = Path.Combine(d.FullName, "tests", "golden");
            if (File.Exists(Path.Combine(g, "audio_adpcm.json"))) return g;
        }
        throw new SkipException("tests/golden not found");
    }

    static string Sha(PcmSound p) => Convert.ToHexString(SHA256.HashData(p.ToBytes())).ToLowerInvariant();

    static void CheckGolden(TestContext t, string what, PcmSound? p, JsonNode? g)
    {
        if (g == null) { t.True(p == null, $"{what}: not IMA ADPCM (the JS says null)"); return; }
        t.True(p != null, $"{what}: decoded");
        t.Equal(g["channels"].Int(), p!.Channels, $"{what} channels");
        t.Equal(g["rate"].Int(), p.Rate, $"{what} rate");
        t.Equal(g["frames"].Int(), p.Frames, $"{what} frames");
        var head = g["head"]!.AsArray();
        for (int i = 0; i < head.Count; i++) t.Equal(head[i].Int(), (int)p.Samples[i], $"{what} sample {i}");
        t.Equal(g["sha256"].Str(), Sha(p), $"{what} all samples (sha256)");
    }

    /// <summary>The IMA ADPCM decoder gives the JS decoder's samples bit for bit: two synthetic files (stereo, mono with a
    /// partial block) and the game's own wavs (where staged).</summary>
    public static void AdpcmMatchesJs(TestContext t)
    {
        var dir = GoldenDir();
        var g = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "audio_adpcm.json")))!;
        foreach (var (f, v) in g["synthetic"]!.AsObject())
            CheckGolden(t, f, WavDecoder.DecodeImaAdpcm(File.ReadAllBytes(Path.Combine(dir, f))), v);
        var snd = Environment.GetEnvironmentVariable("PW_SOUND");
        if (string.IsNullOrEmpty(snd)) snd = "/mnt/user-data/uploads/Paraworld/Data/Base/Audio/Sound";
        int n = 0, pcm = 0;
        foreach (var (f, v) in g["files"]!.AsObject())
        {
            var p = Path.Combine(snd, f);
            if (!File.Exists(p)) continue;
            var bytes = File.ReadAllBytes(p);
            CheckGolden(t, f, WavDecoder.DecodeImaAdpcm(bytes), v);
            if (v == null) { t.True(WavDecoder.DecodePcm(bytes) != null, $"{f}: a PCM wav"); pcm++; }
            n++;
        }
        t.Log($"synthetic 2, game files {n} ({n - pcm} IMA ADPCM, {pcm} PCM) from {snd}");
        if (n == 0) t.Log("(no game wavs found: only the synthetic files were compared)");
    }

    public static void NotAWav(TestContext t)
    {
        t.True(WavDecoder.Decode(new byte[10]) == null, "too short");
        t.True(WavDecoder.Decode(new byte[100]) == null, "no RIFF");
    }

    static SoundDb Db(TestContext t) => SoundDb.Load(Path.Combine(t.Data(), "assets", "sounds.json"));

    public static void SoundDbOnRealData(TestContext t)
    {
        var db = Db(t);
        t.True(db.Events.Count > 500, "events");
        t.True(db.Events["voice_aje_allosaurus_gag"].Seq, "a sequential event");
        t.True(db.Events["ui_click"].Global, "ui_click is global");
        t.Equal(81.0, db.Events["fx_cbigbuildingexplosion"].S[0].MaxHear, "hearing distance");
        t.True(db.Material["Proj_Metal_Small"].ContainsKey("Ground"), "material effects");
        t.True(db.Files.Count > 1000, "animation files");
        t.True(db.Music != null && db.Music.Playlists.ContainsKey("single_05") && db.Music.Tracks.ContainsKey("01_maintheme"), "music tables");
        t.Equal(5000.0, db.Music!.Switches["MainMenu"].FadeInMs ?? 0, "MainMenu fade in");
        t.Equal(3, db.Music.Switches["Hometown"].TracksBy!.Count > 0 ? db.Music.Switches["Hometown"].TracksBy!["Aje"].Count : 0, "Hometown per tribe");
        t.Equal(36, db.Atmo["jungle"].Count, "ambience template states");
        t.Equal("06_atmo/atmo_jungle_plain.wav", db.Atmo["jungle"][18].W[0], "jungle bed (day, low, material 0)");
        t.Log($"{db.Events.Count} events, {db.Files.Count} files, {db.Atmo.Count} ambience templates, {db.Music.Playlists.Count} playlists");
    }

    static double[] Arr(JsonNode? n) => n is JsonArray a ? a.Select(x => x.Num()).ToArray() : Array.Empty<double>();

    /// <summary>The sound logic replays the JS's operations (tests/golden/audio_logic.json) with the same random numbers
    /// and gives the same sounds (file, channel, gain, pan and whether panned, rate, loop, offset), world sound count, speech state, music
    /// switches / tracks / start positions and ambience states.</summary>
    public static void LogicMatchesJs(TestContext t)
    {
        var db = Db(t);
        var g = JsonNode.Parse(File.ReadAllText(Path.Combine(GoldenDir(), "audio_logic.json")))!;
        var ops = g["ops"]!.AsArray();
        var results = g["results"]!.AsArray();
        var o = new RecordingAudioOut();
        // (the browser remake's limits: hearing distance x 1.6, (1 - d/R)^1.5, 25 at a time - the game uses the original's)
        var A = new AudioCore(o) { Rnd = Mulberry32((uint)g["seed"].Int()), Limits = SoundLimits.Js };
        A.SetData(db);
        double atmoT = 0;
        V3? P(JsonNode? n) => n is JsonArray a ? new V3(a[0].Num(), a[1].Num(), a[2].Num()) : null;
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i]!;
            var r = results[i]!;
            int s0 = o.Starts.Count;
            string kind = op["op"].Str(), what = $"op {i} {op.ToJsonString()}";
            switch (kind)
            {
                case "listener": A.Listener = new V3(op["x"].Num(), 0, op["z"].Num()); A.Yaw = op["yaw"].Num(); break;
                case "play": A.Play(op["name"].Str(), P(op["pos"])); break;
                case "playFirst": A.PlayFirst(op["names"]!.AsArray().Select(x => x.Str()), P(op["pos"])); break;
                case "hit": A.Hit(op["w"].Str(), op["t"].Str(), P(op["pos"])); break;
                case "anim":
                {
                    var e = op["ev"]!.AsArray();
                    A.AnimEvent(new AnimSoundEvent(e[0].Num(), e[1].Str(), e[2].Num(), e[3].Num(), e[4].Str()), P(op["pos"]));
                    break;
                }
                case "ack": A.Ack(op["names"]!.AsArray().Select(x => x.Str())); break;
                case "voiceEnd": foreach (var h in o.Starts.Where(h => h.Channel is AudioChannel.Voice or AudioChannel.Unit)) h.Ended = true; break;
                case "playFileOffset": A.PlayFile(op["file"].Str(), 100, null, 0, AudioChannel.Voice, 1, 0, op["off"].Num()); break;
                case "update":
                {
                    var l = Arr(op["l"]);
                    var homes = op["homes"] is JsonArray ha ? ha.Select(x => new V3(x![0].Num(), 0, x[1].Num())).ToList() : null;
                    A.Mus.Update(op["t"].Num(), l.Length == 2 ? new V3(l[0], 0, l[1]) : null, homes != null ? () => homes : null);
                    break;
                }
                case "start": A.Mus.StartGame(op["level"].Str(), op["atmos"].Str(), op["tribe"].Str()); break;
                case "stop": A.Mus.StopGame(); break;
                case "end": if (A.Mus.El is RecordingAudioOut.Track tr) tr.Ended = true; break;
                case "attack": A.Mus.Attack(op["x"].Num(), op["z"].Num(), op["t"].Num()); break;
                case "trigger": A.Mus.Trigger(op["track"].Str(), op["loop"].Bool()); break;
                case "seq": A.Mus.Sequence(op["on"].Bool()); break;
                case "stopCombat": A.Mus.StopCombat(); break;
                case "atmoStart": A.Atmo.Start(op["name"].Str()); break;
                case "tick":
                    for (int k = 0; k < op["n"].Int(); k++) { atmoT += op["dt"].Num(); A.Atmo.Update(op["dt"].Num(), op["state"].Int(), atmoT); }
                    break;
            }
            if (r["playing"] != null)
            {
                var st = A.Mus.State();
                t.Equal(r["playing"].Str(), st.Playing, what + " playing");
                t.Equal(r["track"].Str(), st.Track, what + " track");
                t.Equal(string.Join(",", r["on"]!.AsArray().Select(x => x.Str())), string.Join(",", st.On), what + " switches on");
                t.Equal(r["attacks"].Int(), st.Attacks, what + " attack places");
                t.Equal(r["file"].Str(), A.Mus.Current >= 0 ? A.Mus.Sw[A.Mus.Current].File : "", what + " file");
                var el = r["el"] as JsonArray;
                t.Equal(el == null, A.Mus.El == null, what + " element");
                if (el != null && A.Mus.El != null)
                {
                    t.Equal(el[0].Str(), A.Mus.El.File, what + " element file");
                    t.Equal(el[1].Bool(), A.Mus.El.Loop, what + " element loop");
                    t.Near(el[2].Num(), A.Mus.El.CurrentTime, 1e-6, what + " start position");
                }
                continue;
            }
            if (r["cur"] != null) t.Equal(r["cur"].Int(), A.Atmo.Cur, what + " ambience state");
            var exp = r["starts"]!.AsArray();
            var got = o.Starts.Skip(s0).ToList();
            t.Equal(exp.Count, got.Count, what + " sounds started");
            for (int k = 0; k < Math.Min(exp.Count, got.Count); k++)
            {
                var e = exp[k]!.AsArray();
                var h = got[k];
                t.Equal(e[0].Str(), h.File, what + " file");
                t.Equal(e[1].Str(), h.Channel == AudioChannel.Unit ? "voice" : h.Channel.ToString().ToLowerInvariant(), what + " channel");   // the JS had no unit channel: its unit voices were "voice"
                t.Near(e[2].Num(), h.StartGain, 1e-8, what + " gain");
                t.Near(e[3].Num(), h.Pan, 1e-8, what + " pan");
                t.Near(e[4].Num(), h.Rate, 1e-8, what + " rate");
                t.Equal(e[5].Bool(), h.Loop, what + " loop");
                t.Near(e[6].Num(), h.Offset, 1e-8, what + " offset");
                t.Equal(e[7].Bool(), h.Panner, what + " through a stereo panner");
            }
            if (r["active"] != null)
            {
                t.Equal(r["active"].Int(), A.Active, what + " world sounds playing");
                t.Equal(r["busy"].Bool(), A.VoiceBusy, what + " speech busy");
            }
        }
        t.Log($"{ops.Count} operations, {o.Starts.Count} sounds, {o.Tracks.Count} music tracks opened");
    }

    /// <summary>The fades of the music: the new track rises over the switch's fade in, the old one falls and stops;
    /// the duck turns the music down while somebody speaks.</summary>
    public static void MusicFades(TestContext t)
    {
        var o = new RecordingAudioOut();
        var A = new AudioCore(o) { Rnd = Mulberry32(7) };
        A.Mus.Update(0);
        var menu = (RecordingAudioOut.Track)A.Mus.El!;
        t.Equal("music/01_maintheme.mp3", menu.File, "the main theme (fallback tables)");
        t.Equal(0.0, menu.Volume, "starts silent");
        A.Mus.StepFades(2.5);
        t.Near(0.5, menu.Volume, 1e-9, "half of the 5 s fade in");
        A.Mus.StepFades(3);
        t.Near(1, menu.Volume, 1e-9, "faded in");
        A.Mus.StartGame("", "jungle.txt", "Hu");
        var bg = (RecordingAudioOut.Track)A.Mus.El!;
        t.True(bg != menu, "the background track");
        A.Mus.StepFades(1);
        t.Near(0.8, menu.Volume, 1e-9, "the old one falls with the new one's fade (5 s)");
        t.Near(0.2, bg.Volume, 1e-9, "the new one rises");
        A.Mus.StepFades(5);
        t.True(menu.Paused, "the old one stops");
        A.DuckK = 0.5; A.Mus.ApplyVolume();
        t.Near(0.5, bg.Volume, 1e-9, "ducked");
        A.Muted = true; A.Mus.ApplyVolume();
        t.Equal(0.0, bg.Volume, "muted");
    }

    /// <summary>Kacper: "fight music still plays when I lost the mission and exited it". Leaving a game by any way
    /// (MusicMgr.usl UpdateMusicSwitch while no game runs; GameSession._ExitTree -> GameAudio.Menu -> StopGame): every
    /// game switch off - Combat, a trigger's looped track, a cutscene's silence - and the main theme plays.</summary>
    public static void MusicLeaveGame(TestContext t)
    {
        var o = new RecordingAudioOut();
        var A = new AudioCore(o) { Rnd = Mulberry32(7) };
        void Fight(double at) { A.Mus.Attack(1, 1, at); A.Mus.Attack(50, 1, at); A.Mus.Attack(1, 50, at); A.Mus.Update(at); }
        string Playing() => A.Mus.State().Playing;

        // lost: the end screen (combat off), then back to the menu
        A.Mus.Update(0);
        A.Mus.StartGame("", "jungle.txt", "Hu");
        Fight(20);
        t.Equal("Combat", Playing(), "the fight");
        A.Mus.StopCombat();
        t.Equal("Background", Playing(), "behind the end screen: no combat music");
        A.Mus.StopGame();
        t.Equal("MainMenu", Playing(), "the menu: the main theme");
        t.Equal("music/01_maintheme.mp3", A.Mus.El?.File ?? "", "its track");
        t.Equal(0, A.Mus.State().On.Count(n => n != "MainMenu"), "no game switch left on");
        t.Equal(0, A.Mus.Attacks.Count, "no attacks kept");
        A.Mus.Update(21);
        t.Equal("MainMenu", Playing(), "the menus' frames keep it");

        // quit from the pause menu in the middle of a fight, with a mission's looped fight track (MUSI) and a cutscene
        A.Mus.StartGame("", "jungle.txt", "Hu");
        A.Mus.Trigger("40_combat_heroes", true);
        Fight(30);
        A.Mus.Sequence(true);
        t.Equal("Sequence", Playing(), "a cutscene");
        A.Mus.StopGame();
        t.Equal("MainMenu", Playing(), "quit mid-fight: the main theme");
        t.Equal("music/01_maintheme.mp3", A.Mus.El?.File ?? "", "not the fight's track");

        // the next game (load, retry) starts fresh: no combat from the last one's attacks
        A.Mus.StartGame("", "jungle.txt", "Hu");
        A.Mus.Update(31);
        t.Equal("Background", Playing(), "a new game: its background music");
    }

    /// <summary>The loading screen's journal voice (MusicMgr.usl OnStartJournalSound / OnEndJournalSound): the Loading
    /// switch with silence above the main theme, the main theme again after it; under a running game's switches.</summary>
    public static void MusicJournal(TestContext t)
    {
        var o = new RecordingAudioOut();
        var A = new AudioCore(o) { Rnd = Mulberry32(7) };
        A.Mus.Update(0);
        t.Equal((int)MusicSituation.MainMenu, A.Mus.Current, "the main theme");
        A.Mus.Journal(true);
        t.Equal((int)MusicSituation.Loading, A.Mus.Current, "the journal: the Loading switch plays");
        t.Equal("", A.Mus.S(MusicSituation.Loading).File, "silence");
        A.Mus.Update(1);
        t.Equal((int)MusicSituation.Loading, A.Mus.Current, "stays while the voice plays");
        A.Mus.Journal(false);
        t.Equal((int)MusicSituation.MainMenu, A.Mus.Current, "the main theme again");
        A.Mus.Journal(true);
        A.Mus.StartGame("", "jungle.txt", "Hu");
        t.Equal((int)MusicSituation.Background, A.Mus.Current, "a game's music is above it");
        A.Mus.Journal(false);
        t.Equal((int)MusicSituation.Background, A.Mus.Current, "and stays");
    }

    /// <summary>The ambience: beds fade in and out, the fade times of the JS (1.5 s, 0.2 s when it stops).</summary>
    public static void AtmoBeds(TestContext t)
    {
        var o = new RecordingAudioOut();
        var A = new AudioCore(o) { Rnd = Mulberry32(3) };
        A.SetData(Db(t));
        A.Atmo.Start("jungle.txt");
        A.Atmo.Update(0.1, Atmo.State(0, false, true), 0.1);
        t.Equal(-1, A.Atmo.Cur, "a new material waits FadeDelay");
        for (int i = 0; i < 5; i++) A.Atmo.Update(0.1, Atmo.State(0, false, true), 0.2 + i * 0.1);
        t.Equal(18, A.Atmo.Cur, "state of material 0, low, day");
        var bed = (RecordingAudioOut.Handle)A.Atmo.Beds[0];
        t.True(bed.Loop && bed.Channel == AudioChannel.Atmo, "a looping bed on the ambience bus");
        t.Near(0.35, bed.RampTo, 1e-9, "fading in to the state's volume");
        t.Near(Atmo.FadeTime, bed.RampSec, 1e-9, "over 1.5 s");
        A.Atmo.Update(0.1, Atmo.State(0, false, false), 0.8);
        t.Equal(0, A.Atmo.Cur, "night at once");
        t.True(bed.StopAfter && bed.RampSec == Atmo.FadeTime, "the old bed fades out and stops");
        A.Atmo.Start("No Atmos");
        t.True(A.Atmo.T == null, "No Atmos: silence");
        t.True(((RecordingAudioOut.Handle)o.Starts.Last(h => h.Loop)).StopAfter, "the bed stops with the ambience");
    }

    static SoundUnit U(string key, string name, double level, bool mine = true, bool unit = true) => new(key, name, level, true, unit, mine);

    /// <summary>The feedback rules (UISndMgr): the highest unit answers, the gag after clicking a unit again and again,
    /// orders, the enemy click, death cries and their fallbacks, the attack places of the combat music.</summary>
    public static void FeedbackRules(TestContext t)
    {
        var o = new RecordingAudioOut();
        var A = new AudioCore(o) { Rnd = Mulberry32(5) };
        A.SetData(Db(t));
        var F = new SoundFeedback(A);
        List<RecordingAudioOut.Handle> Since(int n) => o.Starts.Skip(n).ToList();
        void EndVoices() { foreach (var h in o.Starts) if (h.Channel is AudioChannel.Voice or AudioChannel.Unit) h.Ended = true; }

        var sel = new[] { U("a", "aje_warrior", 1), U("b", "aje_allosaurus", 3), U("c", "aje_tent", 5, unit: false) };
        t.Equal("aje_allosaurus", SoundFeedback.VoiceUnit(sel)!.Value.Name, "the highest level unit answers (not a building)");
        int n0 = o.Starts.Count;
        F.Selected(sel);
        var s = Since(n0);
        t.True(s.Any(h => h.Channel == AudioChannel.Ui), "the click (ui_aje_allosaurus_selected)");
        t.True(s.Any(h => h.File.Contains("Aje_Allo_sel") && h.Channel == AudioChannel.Unit), "the allosaurus answers (unit sounds)");
        EndVoices();
        F.Selected(sel); EndVoices(); F.Selected(sel); EndVoices();
        n0 = o.Starts.Count;
        F.Selected(sel);
        t.True(Since(n0).Any(h => h.File.Contains("Aje_Allo_gag")), "the 4th click on the same unit: the gag");
        EndVoices();
        n0 = o.Starts.Count;
        F.Ordered("move", sel);
        s = Since(n0);
        t.True(s.Any(h => h.File == "05_ui/ui_task_go_to.wav") && s.Any(h => h.File.Contains("Aje_Allo_move")), "move order: ui_task_goto + voice");
        n0 = o.Starts.Count;
        F.Ordered("attack", sel);
        t.True(Since(n0).All(h => h.Channel != AudioChannel.Unit), "still speaking: no second ack");
        EndVoices();
        n0 = o.Starts.Count;
        F.Ordered("pickup", sel);
        s = Since(n0);
        t.True(s.Any(h => h.File == "05_ui/ui_pickup_treasure.wav") && s.All(h => h.Channel != AudioChannel.Unit),
            "taking an item (the trade cursor's task_trade): the treasure pick-up sound, no move ack");
        n0 = o.Starts.Count;
        F.Selected(new[] { U("x", "hu_warrior", 1, mine: false) });
        s = Since(n0);
        t.True(s.Count == 1 && s[0].Channel == AudioChannel.Ui, "another's unit: only the click");
        n0 = o.Starts.Count;
        F.Ui("ui_does_not_exist");
        t.True(Since(n0).Single().File == "05_ui/ui_click_success.wav", "a missing ui sound: the click");
        n0 = o.Starts.Count;
        F.Died("aje_allosaurus", "ANML", new V3(1, 0, 1));
        s = Since(n0);
        t.True(s.Count == 1 && s[0].Channel == AudioChannel.Unit && s[0].Panner, "death cry as a positional unit sound");
        // the attack places: hits of 0.15 s -> one place
        F.Attacked(0, 0, 10); F.Attacked(10, 0, 10.05);
        t.True(F.FlushAttacks(10.1) == null, "gathered for 0.15 s");
        var p = F.FlushAttacks(10.2);
        t.True(p != null && p.Value.X == 5 && p.Value.Z == 0, "one attack at their middle");
        t.Equal(1, A.Mus.Attacks.Count, "the music counts it");
    }

    /// <summary>Deaths and attacks far from the camera (an ally's in co-op): a death cry is a world sound with its
    /// hearing distance whoever the owner (Effects.usl CDeathSound: Play3DSound), only the warnings are heard anywhere -
    /// once a fight (Player.usl IWasAttacked), per attacked player; an ally's: the newsticker's ui_message.</summary>
    public static void AllyDeathsAndWarnings(TestContext t)
    {
        var o = new RecordingAudioOut();
        var A = new AudioCore(o) { Rnd = Mulberry32(7) };
        A.SetData(Db(t));
        var F = new SoundFeedback(A);
        t.True(A.Db.Events["voice_aje_allosaurus_die"].Global, "the death cries are global events in the tables");
        A.Listener = new V3(0, 0, 0);
        int n0 = o.Starts.Count;
        F.Died("aje_allosaurus", "ANML", new V3(400, 0, 300));
        t.Equal(0, o.Starts.Count - n0, "a death 500 m from the camera: not heard");
        F.Died("aje_allosaurus", "ANML", new V3(8, 0, 6));
        var s = o.Starts.Skip(n0).ToList();
        t.True(s.Count == 1 && s[0].Channel == AudioChannel.Unit && s[0].Panner, "a death near the camera: a panned unit sound");
        t.True(s[0].StartGain < A.Db.Events["voice_aje_allosaurus_die"].S[0].Vol / 100, "quieter with the distance");

        var w = new AttackWarnings();
        object me = "me", ally = "ally";
        t.True(w.Hit(me, 100), "the first attack: warned");
        t.True(!w.Hit(me, 105), "the fight goes on: no second warning");
        t.True(!w.Hit(me, 115), "attacked every 10 s: still the same fight");
        t.True(w.Hit(ally, 116), "an ally's attack: its own warning");
        t.True(!w.Hit(ally, 120), "the ally's fight goes on");
        t.True(w.Hit(me, 126.5), "11 s without attacks: warned again");

        n0 = o.Starts.Count;
        F.AllyUnderAttack();
        s = o.Starts.Skip(n0).ToList();
        t.True(s.Count == 1 && s[0].File == "05_ui/ui_interjection.wav" && s[0].Channel == AudioChannel.Ui && !s[0].Panner,
            "an ally under attack: the newsticker sound (ui_message), heard anywhere");
        n0 = o.Starts.Count;
        F.UnderAttackWarning();
        t.True(o.Starts.Skip(n0).Single().File == "05_ui/ui_warn_under_attack.wav", "the player's own: ui_warn_under_attack");
    }

    /// <summary>The animation sound table of a model (manifest.models[x].sounds): alternatives by time; a looping clip
    /// that wrapped fires the end and the start.</summary>
    public static void AnimSoundTable(TestContext t)
    {
        var m = Json.ParseFile(Path.Combine(t.Data(), "assets", "manifest.json"))!;
        var tab = AnimSounds.FromManifest(m["models"]?["achelousaurus"]?["sounds"]);
        t.True(tab != null && tab.Clips.ContainsKey("walk_1"), "achelousaurus walk_1");
        var w = tab!.Clips["walk_1"];
        t.Equal(0.96, w[0].T, "first step");
        t.Equal(3, w[0].Alts.Length, "three alternatives");
        var fired = new List<AnimSoundEvent>();
        tab.Fire("Walk_1", 0.5, 1.0, 3, 0, Mulberry32(1), fired.Add);
        t.Equal(1, fired.Count, "one step between 0.5 and 1.0");
        fired.Clear();
        tab.Fire("walk_1", 2.0, 1.0, w[^1].T + 0.1, 0, Mulberry32(1), fired.Add);
        t.Equal(w.Count(x => x.T > 2.0) + w.Count(x => x.T <= 1.0), fired.Count, "wrapped: the end and the start");
        int models = m["models"]!.AsObject().Count(x => AnimSounds.FromManifest(x.Value?["sounds"]) != null);
        t.True(models > 300, $"models with sound tables: {models}");
        var o = new RecordingAudioOut();
        var A = new AudioCore(o);
        A.SetData(Db(t));
        A.AnimEvent(w[0].Alts[0], new V3(0, 0, 0));
        t.True(o.Starts.Count == 1 && o.Starts[0].Rate >= 0.95 && o.Starts[0].Rate <= 1.05, "plays the mapped file at 0.95..1.05 speed");
    }

    // ------------------------------------------------------------------ how many world sounds (SoundLimits)

    /// <summary>A world of sounds in time: a recorder whose sounds end after their length, a clock the test turns.</summary>
    sealed class Timed
    {
        public readonly RecordingAudioOut O = new();
        public readonly AudioCore A;
        public double Now;
        readonly Dictionary<RecordingAudioOut.Handle, double> _t0 = new();
        public Timed(TestContext t, double fileLen, SoundLimits? lim = null)
        {
            O.Duration = fileLen;
            A = new AudioCore(O) { Rnd = Mulberry32(11), Clock = () => Now, Limits = lim ?? SoundLimits.Original };
            A.SetData(Db(t));
        }
        /// <summary>Time runs on: sounds older than their file end.</summary>
        public void Step(double dt)
        {
            Now += dt;
            foreach (var h in O.Starts) { if (!_t0.ContainsKey(h)) _t0[h] = Now - dt; if (!h.Ended && Now - _t0[h] >= O.Duration) h.Ended = true; }
            A.Frame();
        }
    }

    /// <summary>The original's limits (PWClient.exe C3DSoundMgr 0x7a0cb0, CSoundEventMgrClient 0x48fdf0): at most 4
    /// instances of one wav and none within 0.04 s of the last; at most 21 world sounds; heard within the hearing
    /// distance x the camera height's factor; 1/d past the min fade distance; the interface and speech never limited.</summary>
    public static void SoundLimitsOriginal(TestContext t)
    {
        var w = new Timed(t, 2);
        var A = w.A; var o = w.O;
        var ev = new AnimSoundEvent(0, "00_npc/npc_dino_med_rino_idle1.wav", 63, 66, "");
        var here = new V3(0, 0, 0);
        t.True(A.AnimEvent(ev, here) != null, "the first plays");
        t.True(A.AnimEvent(ev, here) == null, "the same wav at the same moment: refused (0.04 s)");
        for (int i = 0; i < 6; i++) { w.Step(0.05); A.AnimEvent(ev, here); }
        t.Equal(4, A.Active, "at most 4 of one wav at a time");
        w.Step(2.0);
        t.Equal(0, A.Active, "they ended");
        t.True(A.AnimEvent(ev, here) != null, "... and it plays again");
        // 30 different wavs at once: 21 slots, the fights' (Combat) take them all; idle chatter at most 8, walking 15
        w.Step(3);
        var files = A.Db.Files.Values.Distinct().Take(40).ToList();
        int n = 0;
        foreach (var f in files.Take(30)) if (A.PlayFile(f, 80, here) != null) n++;
        t.Equal(21, n, "21 world sounds at a time (C3DSoundMgr: more than 20 refuse)");
        t.True(A.Play("ui_click") != null, "the interface is not limited");
        w.Step(3);
        n = 0;
        foreach (var f in files.Take(30)) if (A.PlayFile(f, 80, here, prio: SoundPriority.Ambient) != null) n++;
        t.Equal(8, n, "idle chatter: at most 8 slots");
        n = 0;
        foreach (var f in files.Skip(8).Take(20)) if (A.PlayFile(f, 80, here, prio: SoundPriority.Activity) != null) n++;
        t.Equal(15 - 8, n, "walking and work up to 15 with them");
        n = 0;
        foreach (var f in files.Skip(28)) if (A.PlayFile(f, 80, here) != null) n++;
        t.Equal(21 - 15, n, "the fights still find 6 free slots");
        // hearing distance and loudness
        w.Step(3);
        var hit = files[0];
        t.True(A.PlayFile(hit, 100, new V3(65, 0, 0), 66) != null, "65 m: within 66 m");
        t.Near(6.0 / 65, o.Starts[^1].StartGain, 1e-9, "1/d past the min fade distance (6 m for an event of 66 m)");
        t.True(A.PlayFile(files[1], 100, new V3(67, 0, 0), 66) == null, "67 m: not heard (the JS heard it up to 105 m)");
        t.True(A.PlayFile(files[2], 100, new V3(3, 0, 4), 66, minFade: 5) != null && Math.Abs(o.Starts[^1].StartGain - 1) < 1e-9, "full within the min fade distance");
        t.Near(0.25, SoundLimits.HeightK(5), 1e-9, "a camera on the ground hears a quarter of the distance");
        t.Near(1, SoundLimits.HeightK(52), 1e-9, "the usual camera (70 m away, 52 m up) the whole");
        A.HearK = SoundLimits.HeightK(16.5);     // (the closest the camera comes: 25 m)
        t.True(A.PlayFile(files[3], 100, new V3(40, 0, 0), 66) == null, "zoomed in: 40 m is too far");
        t.Equal(SoundPriority.Ambient, AnimSounds.Priority("standanim", "", "idle"), "an idle unit's stand animation: idle chatter");
        t.Equal(SoundPriority.Ambient, AnimSounds.Priority("feeding", "", "roam"), "a roaming animal feeding");
        t.Equal(SoundPriority.Activity, AnimSounds.Priority("walk_1", "sndgroup_move_dinos_med_herb", "roam"), "... walking");
        t.Equal(SoundPriority.Combat, AnimSounds.Priority("attack_front", "", "attack"), "an attack");
        t.Equal(SoundPriority.Combat, AnimSounds.Priority("res_fight_standanim", "sndgroup_combat", "idle"), "the combat sound group");
        t.Equal(SoundPriority.Activity, AnimSounds.Priority("hammer", "", "build"), "work");
        t.Equal(SoundPriority.Ambient, AnimSounds.Priority("work", "", "building"), "a building's loops");
    }

    /// <summary>A crowd: 100 idle achelousaurus (their stand animation: an idle cry every loop, alternatives) for a
    /// minute, then 30 swordsmen fighting among them. The original's limits keep the idle cries to a few at a time
    /// (the browser remake's played every one it had a slot for); the fight is heard as well as without the crowd.</summary>
    public static void IdleCrowd(TestContext t)
    {
        var m = Json.ParseFile(Path.Combine(t.Data(), "assets", "manifest.json"))!;
        var tab = AnimSounds.FromManifest(m["models"]?["achelousaurus"]?["sounds"])!;
        var idle = tab.Clips["standanim"];
        t.True(idle.Count > 0 && idle[0].Alts.Length >= 2, $"achelousaurus standanim: {idle.Count} events, {idle[0].Alts.Length} alternatives");
        var swords = Db(t).Events["sfx_sword_hit_unit"].S.Select(x => x.File).ToList();
        (double perSec, int maxAt, double hits, double hitsAsked) Run(SoundLimits lim, bool crowd)
        {
            var w = new Timed(t, 1.6, lim);
            var rnd = new Random(5);
            const double loop = 3.0, dt = 1 / 30.0;
            var units = Enumerable.Range(0, crowd ? 100 : 0).Select(i => (pos: new V3(i % 10 * 4 - 18, 0, i / 10 * 4 - 18), ph: rnd.NextDouble() * loop)).ToList();
            int maxAt = 0; double hits = 0, asked = 0; int idleStarts = 0;
            for (double T = 0; T < 90; T += dt)
            {
                foreach (var (pos, ph) in units)
                {
                    double a = (T + ph) % loop, b = (T + dt + ph) % loop;
                    tab.Fire("standanim", a, b, loop, 0, rnd.NextDouble, ev =>
                    {
                        int n0 = w.O.Starts.Count;
                        w.A.AnimEvent(ev, pos, AnimSounds.Priority("standanim", ev.Group, "idle"));
                        if (T >= 10 && T < 70) idleStarts += w.O.Starts.Count - n0;
                    });
                }
                // a fight from 70 s: 30 swordsmen, a hit every 1.2 s each
                if (T >= 70)
                    for (int k = 0; k < 30; k++)
                        if ((T + k * 0.04) % 1.2 < dt)
                        {
                            asked++;
                            if (w.A.PlayFile(swords[rnd.Next(swords.Count)], 68, new V3(k % 6 * 3 - 8, 0, k / 6 * 3 - 8), 66) != null) hits++;
                        }
                w.Step(dt);
                if (T >= 10) maxAt = Math.Max(maxAt, w.A.Active);
            }
            return (idleStarts / 60.0, maxAt, hits, asked);
        }
        var js = Run(SoundLimits.Js, true);
        var org = Run(SoundLimits.Original, true);
        var alone = Run(SoundLimits.Original, false);
        t.Log($"100 idle achelousaurus ({100 * idle.Count / 3.0:0} cries asked a second): the browser remake's limits play {js.perSec:0.0} a second, " +
              $"up to {js.maxAt} sounds at once; the original's {org.perSec:0.0} a second, up to {org.maxAt} at once");
        t.Log($"then 30 fighting: {org.hits} of {org.hitsAsked} hits played among the crowd, {alone.hits} without it (the browser remake's {js.hits})");
        t.True(js.perSec > 2.5 * org.perSec, "the browser remake's limits: the crowd's cries fill every slot (the complaint)");
        t.True(org.perSec <= 8 / 1.6 + 0.2, $"the original's: at most 8 idle cries at a time - {org.perSec:0.0} a second");
        t.True(org.maxAt <= 21, "never more than 21");
        t.True(org.hits >= 0.9 * alone.hits, $"the fight is heard as without the crowd: {org.hits} hits, {alone.hits} without it");
    }
}
