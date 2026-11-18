// A headless run of the game's sound (no window, the dummy audio driver: nothing is heard, everything else runs):
// loads the tables and files of a configuration made in code, decodes every wav it finds, plays a sequence of
// interface sounds, speech, world sounds, hits, animation sounds, the music's switches (menu, game, combat, trigger,
// cutscene) and the ambience's states, and checks what it can see: buses, streams, players, switches, beds.
//   godot --headless --path game res://src/View/Audio/Godot/AudioSelfTest.tscn -- [--install <ParaWorld folder>]
//         [--data <converted data folder>] [--seconds 12]
// Exit code 0 when every check passed (the log says which failed).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

public partial class AudioSelfTest : Node
{
    static readonly Logger L = Log.For("View.Audio.SelfTest");
    GameAudio _ga = null!;
    double _t, _seconds = 12;
    int _step, _fails, _checks;
    readonly List<Action> _steps = new();
    readonly List<double> _at = new();
    ISoundHandle? _click;

    void Check(bool ok, string what)
    {
        _checks++;
        if (ok) L.Info("ok    " + what);
        else { _fails++; L.Error("FAIL  " + what); }
    }

    public override void _Ready()
    {
        if (Log.Sink<ConsoleLogSink>() == null) Log.AddSink(new ConsoleLogSink());
        Log.DefaultLevel = LogLevel.Info;
        Log.SetLevel("View.Audio.Music", LogLevel.Debug);
        Log.SetLevel("View.Audio.Atmo", LogLevel.Debug);
        var args = GameApp.UserArgs();
        var cfg = new GameConfiguration
        {
            Id = "audiotest", Title = "audio self test", Folders = new() { "Base" },
            Install = args.TryGetValue("install", out var i) ? i : "/mnt/user-data/uploads/Paraworld",
            Data = args.TryGetValue("data", out var d) ? d : "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a",
        };
        if (args.TryGetValue("seconds", out var s)) _seconds = Json.ParseNum(s, 12);
        var assets = new AssetSource(cfg);
        L.Info($"configuration: install {cfg.Install}, data {cfg.Data}");
        _ga = new GameAudio(assets) { FollowApp = false };
        AddChild(_ga);
        var A = _ga.Core;
        A.Rnd = new Random(1).NextDouble;     // (the same run every time)

        At(0.05, () =>
        {
            foreach (var b in new[] { AudioBuses.Music, AudioBuses.Effects, AudioBuses.Speech, AudioBuses.Ambience, AudioBuses.Ui })
                Check(AudioServer.GetBusIndex(b) > 0, $"bus {b} (volume {AudioBuses.Volume(b):0.##})");
            Check(A.Db.Events.Count > 500 && A.Db.Atmo.Count > 0 && A.Db.Music != null, $"sounds.json: {A.Db.Events.Count} events");
            // every wav of the installation's Audio/Sound that is there: a stream
            var root = cfg.GameFile("Audio/Sound/05_ui/ui_click_success.wav");
            var sndDir = root != null ? Path.GetDirectoryName(Path.GetDirectoryName(root))! : "";
            int n = 0, adpcm = 0, bad = 0;
            if (Directory.Exists(sndDir))
                foreach (var f in Directory.EnumerateFiles(sndDir, "*.wav", SearchOption.AllDirectories).OrderBy(x => x))
                {
                    var rel = Path.GetRelativePath(sndDir, f).Replace('\\', '/');
                    var b = _ga.Out.Streams.Sound(rel);
                    n++;
                    if (b == null || b.Duration <= 0) { bad++; L.Error($"no stream for {rel}"); continue; }
                    if (b.Pcm != null)
                    {
                        adpcm++;
                        var w = (AudioStreamWav)b.Stream;
                        if (w.Data.Length != b.Pcm.Samples.Length * 2 || Math.Abs(w.GetLength() - b.Pcm.Duration) > 1e-3) { bad++; L.Error($"stream of {rel} does not match its PCM"); }
                    }
                    L.Debug(() => $"{rel}: {b.Duration:0.###} s{(b.Pcm != null ? $", IMA ADPCM {b.Pcm.Channels} ch {b.Pcm.Rate} Hz" : ", PCM (Godot)")}");
                }
            Check(n > 0 && bad == 0, $"streams of {n} wavs ({adpcm} IMA ADPCM decoded, {n - adpcm} PCM by Godot), {bad} bad");
            var mus = _ga.Out.Streams.Music("music/01_maintheme.mp3");
            Check(mus != null && mus.GetLength() > 60, $"mp3 stream 01_maintheme: {mus?.GetLength():0.#} s");
            Check(_ga.Out.Streams.Sound("05_ui/no_such_file.wav") == null, "a missing file: null (warned once)");
        });
        At(0.1, () =>
        {
            _ga.Menu();
            Check(A.Mus.State().Playing == "MainMenu" && A.Mus.El != null && !A.Mus.El.Failed, $"menu music: {A.Mus.State().Track}");
            _click = Sfx.PlayEvent("ui_click");
            Check(_click != null, "ui_click plays");
            Sfx.Ui("ui_does_not_exist");
            Sfx.Feedback.Selected(new[] { new SoundUnit("u1", "aje_allosaurus", 3, true, true, true) });
            Check(A.VoiceBusy, "the allosaurus answers (speech busy)");
            Sfx.Voice("aje_allosaurus", "task_goto");
        });
        At(0.5, () =>
        {
            Check(_click != null && _click.Ended, "ui_click (0.1 s) has ended and its player went back to the pool");
            Check(A.Mus.El!.Volume > 0.02 && A.Mus.El.Volume < 0.2, $"the main theme fades in (5 s): volume {A.Mus.El.Volume:0.###} after 0.4 s");
            Check(A.Mus.El.CurrentTime > 0.1, $"the main theme plays: {A.Mus.El.CurrentTime:0.##} s");
            _ga.StartGame("", "jungle.txt", "Aje");
            Check(A.Mus.State().Playing == "Background", $"game music: {A.Mus.State().Playing} '{A.Mus.State().Track}' (failed: {A.Mus.El?.Failed})");
        });
        At(1.0, () =>
        {
            int s0 = _ga.Out.Started;
            var near = new V3(105, 0, 98);
            // (an event whose variants are all staged: the variant is random)
            Check(Sfx.PlayEvent("sfx_proj_metal_small_hit_unit", near) != null, "a hit near the camera's target");
            Check(Sfx.PlayEvent("fx_cmedexplo01", new V3(500, 0, 500)) == null, "... and one too far away: not heard");
            Sfx.Hit("Proj_Metal_Small", "Unit", near);
            Sfx.Hit("Proj_Metal_Small", "Ground", new V3(90, 0, 110));
            Sfx.Anim(new AnimSoundEvent(0.96, "04_step/step_huge_dino_carn1.wav", 48, 66, ""), new V3(100, 0, 104));
            Sfx.Feedback.Placed("Aje");
            Sfx.Feedback.Upgrade(true);
            Check(_ga.Out.Started - s0 >= 5, $"world sounds started: {_ga.Out.Started - s0}, playing {_ga.Out.Playing}");
            for (int k = 0; k < 40; k++) Sfx.PlayEvent("sfx_proj_metal_small_hit_unit", new V3(100 + k * 0.1, 0, 100));
            Check(A.Active <= A.Limits.MaxPlaying, $"the cap of world sounds: {A.Active} playing");
            Check(A.Active <= A.Limits.MaxSameFile + 5, $"... and of one event's wavs: {A.Active} playing");
            // three attack places: combat music
            Sfx.Feedback.Attacked(10, 10, _t); Sfx.Feedback.FlushAttacks(_t + 0.2);
            Sfx.Feedback.Attacked(40, 10, _t + 0.3); Sfx.Feedback.FlushAttacks(_t + 0.5);
            Sfx.Feedback.Attacked(80, 10, _t + 0.6); Sfx.Feedback.FlushAttacks(_t + 0.8);
        });
        At(1.5, () => Check(A.Mus.State().Playing == "Combat", $"combat music after attacks at 3 places: '{A.Mus.State().Track}' {(A.Mus.El?.Failed == true ? "(file not staged)" : "")}"));
        At(2.0, () =>
        {
            Check(A.Atmo.Cur == Atmo.State(0, false, true), $"ambience state {A.Atmo.Cur} (jungle, material 0, low, day)");
            Check(A.Atmo.Beds.Count == 1 && !A.Atmo.Beds[0].Ended, $"the bed loops, gain {A.Atmo.Beds.FirstOrDefault()?.Gain:0.###}");
            A.Mus.Sequence(true);
        });
        At(2.5, () =>
        {
            Check(A.Mus.State().Playing == "Sequence" && A.Mus.El == null, "a cutscene: silence");
            A.Mus.Sequence(false);
            // (Combat is above Trigger: the fights end first)
            A.Mus.StopCombat();
            A.Mus.Trigger("01_maintheme", false);
        });
        At(3.0, () => Check(A.Mus.State().Playing == "Trigger", $"the trigger action's track: {A.Mus.State().Track}"));
        At(_seconds - 1.0, () =>
        {
            Check(A.Atmo.Beds.Count == 1 && Math.Abs(A.Atmo.Beds[0].Gain - A.Atmo.T![A.Atmo.Cur].V / 100) < 1e-6, $"the bed has faded in: {A.Atmo.Beds[0].Gain:0.###}");
            _ga.EndGame();
            _ga.Menu();
            Check(A.Mus.State().Playing == "MainMenu", "back in the menu: the main theme");
            Check(!A.VoiceBusy, "the speech has ended");
            Check(A.Atmo.T == null && A.Atmo.Beds.Count == 0, "no ambience in the menu");
        });
        // the pan of a 2D sound (an ambience event; world sounds the same way): the bus's left and right peaks
        ISoundHandle? panned = null;
        double PeakL() => AudioServer.GetBusPeakVolumeLeftDb(AudioServer.GetBusIndex(AudioBuses.Ambience), 0);
        double PeakR() => AudioServer.GetBusPeakVolumeRightDb(AudioServer.GetBusIndex(AudioBuses.Ambience), 0);
        At(_seconds - 0.45, () => panned = A.PlayFile("06_atmo/atmo_jungle_wind.wav", 100, null, 0, AudioChannel.Atmo, 1, -0.9));
        At(_seconds - 0.3, () =>
        {
            // WebAudio at pan -0.9: left cos(0.05 pi/2), right sin(0.05 pi/2) = 22.1 dB apart
            double want = 20 * Math.Log10(Math.Cos(0.05 * Math.PI / 2) / Math.Sin(0.05 * Math.PI / 2));
            Check(Math.Abs(PeakL() - PeakR() - want) < 0.5, $"panned left: peaks L {PeakL():0.#} dB, R {PeakR():0.#} dB ({want:0.#} dB apart in the JS)");
            panned?.Stop();
            panned = A.PlayFile("06_atmo/atmo_jungle_wind.wav", 100, null, 0, AudioChannel.Atmo, 1, 0.9);
        });
        At(_seconds - 0.12, () =>
        {
            double want = 20 * Math.Log10(Math.Cos(0.05 * Math.PI / 2) / Math.Sin(0.05 * Math.PI / 2));
            Check(Math.Abs(PeakR() - PeakL() - want) < 0.5, $"panned right: peaks L {PeakL():0.#} dB, R {PeakR():0.#} dB");
            panned?.Stop();
        });
        At(_seconds, () =>
        {
            L.Info($"sounds started {_ga.Out.Started}, playing {_ga.Out.Playing}, streams {_ga.Out.Streams.Count} ({_ga.Out.Streams.Failed} missing), " +
                   $"warnings {Log.Warnings}, errors {Log.Errors}");
            L.Info(_fails == 0 ? $"audio self test: all {_checks} checks passed" : $"audio self test: {_fails} of {_checks} checks FAILED");
            _ga.Shutdown();
        });
        // (a frame later: the freed players are gone)
        At(_seconds + 0.1, () => { Log.Flush(); GetTree().Quit(_fails == 0 ? 0 : 1); });
    }

    void At(double t, Action a) { _at.Add(t); _steps.Add(a); }

    public override void _Process(double delta)
    {
        _t += delta;
        // the game's frame: the listener at (100, 100) looking north, the ambience of jungle material 0, low, day
        _ga.UpdateWorld(delta, _t, new V3(100, 0, 100), 0, () => Array.Empty<V3>(), Atmo.State(0, false, true));
        while (_step < _steps.Count && _t >= _at[_step])
        {
            try { _steps[_step](); }
            catch (Exception e) { _fails++; L.Error($"step {_step} threw", e); }
            _step++;
        }
    }
}
