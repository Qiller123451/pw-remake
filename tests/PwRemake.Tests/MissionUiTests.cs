// Tests of the mission UI's logic (game/src/UI/Mission/Logic, linked into this project): the timing helpers against
// the JS (values of remake/src/ui/mission.js run in node), the real converted missions' data (dialogues, sequences,
// quests), the dialogue queue with speech lengths, mentor scenes, cutscenes (timeline of sc_1040, line scenes, films
// that cannot be played), quests in the log and on the ticker, markers, timers, the end screen, and the bridge with a
// real TriggerEngine (EndDialog / EndSequence come back with the right tokens).
//   PW_CONTENT  a content folder with configs/base/missions (default: /home/claude/testcontent)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Scripting;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.Tests;

/// <summary>A mission UI output that plays nothing and records (sound lengths from a table).</summary>
public sealed class RecordingUiOutput : IMissionUiOutput
{
    public sealed class Snd : IUiSound
    {
        public string File = ""; public bool Voice, Loop; public double Offset, Vol, Start;
        public RecordingUiOutput Out = null!;
        public double Duration { get; init; }
        bool stopped;
        /// <summary>Stopped, or (AutoEnd) played to its end on the output's clock.</summary>
        public bool Ended { get => stopped || (Out.AutoEnd && !Loop && Out.Now >= Start + Duration - Offset - 1e-9); set => stopped = value; }
        public void Stop() => stopped = true;
    }
    public readonly List<string> Sounds = new();
    public readonly List<Snd> Files = new();
    public readonly List<bool> Ducks = new(), MusicSeq = new();
    public Func<string, double> Length = f => f.Contains("missing") ? -1 : 2.0;
    public Func<string, bool> Films = _ => false;
    public bool Paused { get; set; }
    /// <summary>The clock of the sounds (Run advances it); AutoEnd: sounds end after their length.</summary>
    public double Now;
    public bool AutoEnd = true;
    public void Sound(string name) => Sounds.Add(name);
    public IUiSound? PlayFile(string file, double vol, bool voice, double offset, bool loop)
    {
        double d = Length(file);
        if (d < 0) return null;
        var s = new Snd { File = file, Voice = voice, Loop = loop, Offset = offset, Vol = vol, Duration = d, Start = Now, Out = this };
        Files.Add(s);
        return s;
    }
    public string? EventFile(string name) => null;
    public void Duck(bool on) => Ducks.Add(on);
    public void MusicSequence(bool on) => MusicSeq.Add(on);
    public bool FilmPlayable(string film) => Films(film);
    /// <summary>Ends the speech that plays (the sound ran out).</summary>
    public void EndAll() { foreach (var s in Files) s.Ended = true; }
}

[TestClass]
public static class MissionUiTests
{
    static string MissionDir(TestContext t, string folder)
    {
        var root = Environment.GetEnvironmentVariable("PW_CONTENT");
        if (string.IsNullOrEmpty(root)) root = "/home/claude/testcontent";
        var d = Path.Combine(root, "configs", "base", "missions", folder);
        if (!Directory.Exists(d)) t.Skip($"no mission folder {d}");
        return d;
    }

    static (MissionUiState S, RecordingUiOutput O, MissionScript Script) Mission(TestContext t, string folder = "01_single_01")
    {
        var dir = MissionDir(t, folder);
        var data = MissionUiData.Load(dir);
        var o = new RecordingUiOutput();
        var s = new MissionUiState(data, o);
        // the engine's quests as the engine reads them (TriggerData.ReadCommon)
        var lm = LuaMission.Load(dir, Repo.Std);
        s.Quests = () => lm.Script.Quests;
        return (s, o, lm.Script);
    }

    /// <summary>every speaker of every mission's dialogues and cutscenes gets a name, not a code name: the original's
    /// actor names (dialogscenes_uk.ltf, playernames_uk.ltf) behind the export's speakers ("_seq_single_16_stina" -> Stina,
    /// "babbit" -> Babbit, "nordvolk" -> Norsemen); without the installation's tables the words of the key</summary>
    public static void SpeakerNames(TestContext t)
    {
        var install = Environment.GetEnvironmentVariable("PW_INSTALL") ?? "/mnt/user-data/uploads/Paraworld";
        var tx = PwRemake.Sim.Content.GameTexts.Read(install, new[] { "Base", "BoosterPack1", "MIRAGE" }, "uk");
        int n = 0;
        foreach (var cfg in new[] { "base", "mirage" })
        {
            var c = CampaignTests.LoadCfgPublic(cfg);
            if (c == null) continue;
            var seen = new HashSet<string>();
            foreach (var dir in System.IO.Directory.GetDirectories(c.Config.MissionsDir).OrderBy(x => x))
            {
                var data = MissionUiData.Load(dir);
                if (tx.Count > 0) data.GameText = k => tx.Has(k) ? tx.Get(k, "") : null;
                var s = new MissionUiState(data, new RecordingUiOutput());
                var sps = data.Sequences.Values.SelectMany(q => q.Lines.Select(l => l.Speaker)).Concat(data.Dialogs.Values.SelectMany(d => d.Frames.Select(f => f.Speaker)));
                foreach (var sp in sps.Where(x => !string.IsNullOrEmpty(x)).Distinct())
                {
                    var name = s.SpeakerName(sp);
                    if (seen.Add(sp)) t.Log($"{sp} -> {name}");
                    t.True(name.Length == 0 || (!name.Contains('_') && !name.StartsWith("Seq", StringComparison.OrdinalIgnoreCase)), $"{System.IO.Path.GetFileName(dir)}: {sp} has a name ({name})");
                    n++;
                }
            }
        }
        if (n == 0) t.Skip("no prepared missions");
        t.Equal("Stina", new MissionUiState(new MissionUiData(), new RecordingUiOutput()).SpeakerName("_seq_single_16_stina"), "a cutscene key's speaker without the tables");
        t.Equal("Barry", new MissionUiState(new MissionUiData(), new RecordingUiOutput()).SpeakerName("trader_barry"), "an alias");
    }

    static void Run(MissionUiState s, double seconds, double dt = 0.05, bool game = true)
    {
        var o = s.Out as RecordingUiOutput;
        for (double x = 0; x < seconds - 1e-9; x += dt) { if (o != null) o.Now += dt; if (game) s.Tick(dt); s.Update(dt); }
    }

    // ---- the helpers against the JS (node: vowelGroups, frameLength, clock of remake/src/ui/mission.js)
    public static void TimingHelpersAsJs(TestContext t)
    {
        string[] texts = { "", "banana", "Fight now, talk later. Please!", "What the?! What the hell kinda animal is that?", "Stina? You okay?", "aeiouAEIOU Ééé",
            "You've washed up on the shore of an uncharted island. Explore it!" };
        int[] vowels = { 0, 1, 6, 10, 6, 10, 21 };
        double[] frame = { 1.5, 1.5, 2.55, 3.59, 1.64, 2.5, 5.25 }, frameSnd = { 2.2, 2.2, 2.2, 2.5, 2.2, 2.5, 5.25 };
        for (int i = 0; i < texts.Length; i++)
        {
            t.Equal(vowels[i], MissionUiState.VowelGroups(texts[i]), $"vowel groups of '{texts[i]}'");
            t.Near(frame[i], MissionUiState.FrameLength(texts[i]), 1e-9, $"frame length of '{texts[i]}' without sound");
            t.Near(frameSnd[i], MissionUiState.FrameLength(texts[i], 2.2), 1e-9, $"frame length of '{texts[i]}' with a 2.2 s sound");
        }
        double[] secs = { 0, 0.2, 9.5, 59.01, 61, 3599, 3600, 3725.4, -3 };
        string[] clocks = { "00:00", "00:01", "00:10", "01:00", "01:01", "59:59", "1:00:00", "1:02:06", "00:00" };
        for (int i = 0; i < secs.Length; i++) t.Equal(clocks[i], MissionUiState.Clock(secs[i]), $"clock {secs[i]}");
    }

    // ---- the data of real missions
    public static void DataOfRealMissions(TestContext t)
    {
        var d = MissionUiData.Load(MissionDir(t, "01_single_01"));
        t.Equal(1, d.Id, "mission id");
        t.Equal("Mission 1: Stranded", d.Title, "title");
        t.Equal(59, d.Dialogs.Count, "dialogues");
        t.Equal(12, d.Sequences.Count, "sequences");
        t.Equal(16, d.QuestOrder.Count, "quests");
        t.Equal(14, d.QuestionMarks.Count, "question marks");
        var ds = d.Dialog("Cpn_single_001/single_01/ds_1020.dlg")!;
        t.True(ds != null && ds.Frames.Count == 2 && ds.Frames[1].Speaker == "Warrior" && ds.ActorOf(ds.Frames[0])!.Icon == "cole", "ds_1020: frames, speakers, actor icons");
        t.True(d.Dialog("Cpn_single_001\\SINGLE_01\\ds_1020.dlg") == ds && d.Dialog("other/ds_1020.dlg") == ds, "a scene by a path in another case / by its file name");
        var sq = d.Sequence("Cpn_single_001/single_01/sc_1040.seq")!;
        t.True(sq.Lines.Count == 4 && sq.Play != null && sq.Play.End > 21 && sq.Play.Actors.Count > 0, "sc_1040: lines and timeline");
        t.True(d.Sequence("Cpn_single_001/single_01/ms_1120.seq")!.Videos.SequenceEqual(new[] { "hs_1120.bik" }), "ms_1120 is a film");
        t.True(d.QuestOrder[0].Description.StartsWith("Cole, Stina, and B"), "quest description");
        // every mission of the official campaign reads
        var root = Path.GetDirectoryName(MissionDir(t, "01_single_01"))!;
        int n = 0, frames = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var m = MissionUiData.Load(dir);
            n++; frames += m.Dialogs.Values.Sum(x => x.Frames.Count);
        }
        t.True(n >= 20 && frames > 500, $"all missions read ({n} missions, {frames} dialogue frames)");
    }

    public static void PortraitsFromTheAtlas(TestContext t)
    {
        var atlas = "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a/assets/ui/atlas.json";
        if (!File.Exists(atlas)) t.Skip("no atlas.json staged");
        UiAtlas.Load(atlas);
        t.True(UiAtlas.Count > 500, "atlas entries");
        t.Equal("Cole_s0", UiAtlas.IconFor("Cole_s0"), "class Cole_s0: its own card");
        t.Equal("cole", UiAtlas.IconFor("cole_s7"), "class cole_s7 -> card cole");
        t.Equal("hu_warrior", UiAtlas.IconFor("hu_warrior"), "hu_warrior");
        t.Equal(new AtlasEntry("hud_cards_cards_heroes_lvl5.png", 0, 61, 64, 61), UiAtlas.Entry("stina", 4)!.Value, "stina level 4");
        t.Equal(new AtlasEntry("hud_newstickericons.png", 112, 0, 16, 16), UiAtlas.Entry("nticon_quest_new_normal")!.Value, "ticker icon");
        t.True(UiAtlas.IconFor("nobody_at_all") == null, "unknown: no portrait");
    }

    // ---- dialogue scenes
    public static void DialogueQueueAndTiming(TestContext t)
    {
        var (s, o, _) = Mission(t);
        o.Length = f => f.EndsWith("1020_Cole_01.mp3") ? 3.0 : f.EndsWith("1020_Norsemen_01.mp3") ? 0.5 : 2.0;
        int ends1 = 0, ends2 = 0;
        s.PlayDialog(s.Data.Dialog("ds_1020.dlg")!, () => ends1++);
        s.PlayDialog(s.Data.Dialog("ds_1023b.dlg")!, () => ends2++);
        t.True(s.Scene == null, "nothing starts inside PlayDialog");
        s.Tick(0.05);
        t.True(s.Scene != null && s.Scene.I == 0 && s.Scene.Name == "Cole:" && s.Scene.Portrait == "cole" || UiAtlas.Count == 0, "frame 0: Cole with his portrait");
        t.Equal("../SeqSounds/Level_1/1020_Cole_01.mp3", o.Files[0].File, "speech file by the dialogsounds rule");
        t.True(o.Files[0].Voice, "speech on the voice channel");
        t.Near(3.0, s.Scene!.Len, 1e-9, "frame 0 lasts its sound (3 s > 0.25 x 10 vowel groups)");
        t.True(o.Ducks.SequenceEqual(new[] { true }) && o.Sounds.Contains("ui_message"), "music ducked, ui_message");
        Run(s, 2.9);
        t.Equal(0, s.Scene!.I, "still frame 0 at 2.95 s");
        Run(s, 0.1);
        t.Equal(1, s.Scene!.I, "frame 1 after 3 s");
        // frame 1: 0.5 s sound, 'Fight now, talk later. Please!' = 6 vowel groups = 1.5 s
        t.Near(1.5, s.Scene.Len, 1e-9, "frame 1: at least 0.25 s per vowel group");
        t.True(o.Files[0].Ended, "the speech of frame 0 was stopped");
        Run(s, 1.6);
        t.Equal(1, ends1, "scene 1 ended once");
        t.True(s.Scene != null && s.Scene.Scene.Id == "ds_1023b", "the next scene starts: " + string.Join(" | ", s.LogLines.Select(l => string.Join(",", l))));
        t.True(!o.Ducks.Contains(false), "no unduck between queued scenes");
        // the close button
        s.CloseDialog();
        t.Equal(1, ends2, "closed: scene 2 ended");
        t.True(o.Ducks.Last() == false, "music back");
        Run(s, 1);
        t.True(ends1 == 1 && ends2 == 1 && s.Scene == null, "each onEnd exactly once");
        // a speech that does not end holds the frame (DS:372-375), at most 20 s more
        o.Length = _ => 1.0;
        o.AutoEnd = false;
        var sc = s.Data.Dialog("ds_1023b.dlg")!;
        int e3 = 0;
        s.PlayDialog(sc, () => e3++);
        s.Tick(0.05);
        var snd = o.Files.Last();
        Run(s, 4.0);
        t.Equal(0, e3, "speech still playing after its length and the vowel rule: the frame waits");
        snd.Ended = true;
        Run(s, 0.1);
        t.Equal(1, e3, "speech over: the scene ends");
    }

    public static void DialogueWithoutSpeechAndUnknown(TestContext t)
    {
        var (s, o, _) = Mission(t);
        o.Length = _ => -1;                 // no sound files
        int ends = 0;
        s.PlayDialog(s.Data.Dialog("ds_1020.dlg")!, () => ends++);
        s.Tick(0.05);
        t.Near(MissionUiState.FrameLength("What the?! What the hell kinda animal is that?"), s.Scene!.Len, 1e-9, "no speech: the text estimate");
        Run(s, 3.59 + 2.55 + 0.2);
        t.Equal(1, ends, "the scene ends after both estimates");
        s.PlayDialog(DialogScene.Unknown("x/ds_nothing.dlg"), () => ends++);
        s.Tick(0.05);
        t.Equal(2, ends, "an unknown scene ends on the next tick");
    }

    public static void MentorScenes(TestContext t)
    {
        var (s, o, _) = Mission(t);
        var mentor = s.Data.Dialog("Cpn_single_001/single_01/mentor/ds_L01B01.dlg")!;
        t.True(mentor.Mentor, "ds_L01B01 is a mentor scene");
        t.Equal("../SeqSounds/Mentor/L01B01.mp3".ToLowerInvariant(), s.FrameSound(mentor, mentor.Frames[0])!.ToLowerInvariant(), "mentor speech folder");
        s.MentorTexts = false;
        int ends = 0;
        s.PlayDialog(mentor, () => ends++);
        s.Tick(0.05);
        t.True(s.Scene!.Hidden && o.Files.Count == 0 && !o.Sounds.Contains("ui_message"), "mentor texts off: not shown, no speech");
        Run(s, 0.5 * mentor.Frames.Count + 0.1);
        t.Equal(1, ends, "the hidden scene still ends (0.5 s per frame)");
        s.MentorTexts = true;
        s.PlayDialog(mentor, () => ends++);
        s.Tick(0.05);
        t.True(!s.Scene!.Hidden && o.Files.Count == 1, "mentor texts on: shown with speech");
        s.SkipAll();
        t.Equal(2, ends, "skipAll ends it");
    }

    /// <summary>
    /// Dialogue scenes and cutscenes do not overlap (player report, mission 1 in co-op: "the mentor guy was talking while
    /// the cutscene was playing"): the original hides the dialogue scenes while a sequence plays and lets them go on
    /// after it (MusicMgr.usl OnSequenceStart "stop Dialogs" / OnSequenceEnd "continue Dialogs"). A scene speaking when a
    /// cutscene starts falls silent and says its line again after it; a scene asked for during the cutscene (a mentor
    /// hint, a trigger's DGSC) waits for its end - also when the game's tick goes on under the cutscene (a networked
    /// game); its end (the DSEN of the triggers) comes after the cutscene's.
    /// </summary>
    public static void DialoguesWaitForCutscenes(TestContext t)
    {
        foreach (bool ticking in new[] { false, true })
        {
            var (s, o, _) = Mission(t);
            o.Length = f => f.Contains("Mentor") ? 4.0 : f.Contains("SeqSounds/Level_1/1040") || f.Contains("music") ? 1.0 : 3.0;
            var order = new List<string>();
            var mentor = s.Data.Dialog("Cpn_single_001/single_01/mentor/ds_L01B01.dlg")!;
            s.PlayDialog(mentor, () => order.Add("mentor"));
            s.Tick(0.05);
            var speech = o.Files.Last();
            t.True(s.Scene != null && speech.File.Contains("Mentor") && !speech.Ended, $"{ticking}: the mentor speaks");
            // the cutscene comes while the mentor speaks
            s.PlaySequence(s.Data.Sequence("sc_1040.seq")!, new SequenceOptions(), () => order.Add("sc_1040"));
            s.Update(0.016);
            t.True(s.Cine != null, $"{ticking}: the cutscene starts (it does not wait for the dialogue)");
            t.True(speech.Ended, $"{ticking}: the mentor's speech stops when the cutscene starts");
            // a scene a trigger starts during the cutscene, and another mentor hint
            s.PlayDialog(s.Data.Dialog("ds_1020.dlg")!, () => order.Add("ds_1020"));
            int Voices() => o.Files.Count(f => f.Voice && !f.File.Contains("1040"));
            int files = Voices(); bool quiet = true;
            for (int i = 0; i < 25 * 20 && s.Cine != null; i++)
            {
                o.Now += 0.05; if (ticking) s.Tick(0.05); s.Update(0.05);
                if (s.Cine != null && Voices() != files) quiet = false;          // (the step the cutscene ends in: the mentor again)
            }
            t.True(s.Cine == null && order.SequenceEqual(new[] { "sc_1040" }), $"{ticking}: the cutscene ended, no dialogue ended under it: {string.Join(",", order)}");
            t.True(quiet, $"{ticking}: no dialogue speech started under the cutscene");
            // after it: the mentor says his line again, then the scene asked for during the cutscene
            Run(s, 0.05);
            t.True(s.Scene?.Scene == mentor && s.Scene.I == 0 && o.Files.Last().File.Contains("Mentor") && !o.Files.Last().Ended, $"{ticking}: the mentor's frame again, with its speech");
            Run(s, 60);
            t.True(order.SequenceEqual(new[] { "sc_1040", "mentor", "ds_1020" }), $"{ticking}: the dialogues end after the cutscene, in their order: {string.Join(",", order)}");
            t.True(s.LogLines.Any(l => l.Contains("dialogHeld")) && s.LogLines.Any(l => l.Contains("dialogResumed")), $"{ticking}: held and resumed (log)");
        }
    }

    // ---- cutscenes
    public static void TimelineCutsceneSc1040(TestContext t)
    {
        var (s, o, _) = Mission(t);
        o.Length = f => f.Contains("SeqSounds") ? 1.0 : 3.0;
        int ends = 0;
        var seq = s.Data.Sequence("sc_1040.seq")!;
        s.PlaySequence(seq, new SequenceOptions(), () => ends++);
        t.True(s.Busy && s.Cine == null, "queued, busy");
        s.Update(0.016);
        t.True(s.Cine != null && s.Cine.Kind == CineKind.Timeline && o.Paused, "a timeline scene; the game is paused");
        t.True(o.MusicSeq.SequenceEqual(new[] { true }), "music: Sequence switch on");
        // fade in over 2.04 s
        t.True(s.Cine!.Fade > 0.9, "black at the start (fade in)");
        Run(s, 1.0, game: false);
        t.Near(1 - 1.016 / 2.04, s.Cine.Fade, 0.02, "half faded in at 1 s");
        // sounds of the timeline: stonesfall at 2.4 s
        Run(s, 1.5, game: false);
        t.True(o.Files.Any(f => f.File == "99_sequences/seq_1040_stonesfall.wav" && !f.Voice), "timeline sound (effects channel)");
        // the cut item 6.12 + 1.72: the scene jumps to 7.84; Cole's line at 7.8
        Run(s, 3.7, game: false);
        var player = s.Cine.Player!;
        t.True(player.T > 7.84 && player.T < 8.0, $"skip item jumped the clock ({player.T:0.00})");
        t.True(s.Cine.Text == "Stina? You okay?" && s.Cine.Speaker == "Cole:", $"Cole's subtitle ('{s.Cine.Speaker} {s.Cine.Text}')");
        t.True(o.Files.Any(f => f.File == "../SeqSounds/Level_1/1040/1040_Cole_01.mp3" && f.Voice), "Cole's speech");
        Run(s, 2.3, game: false);
        t.True(s.Cine.Text.StartsWith("Not really.") && s.Cine.Speaker == "Stina:", "Stina's subtitle at 10 s (line breaks joined)");
        t.True(o.Files.Any(f => f.File == "../SeqSounds/music/NS_1040.mp3" && !f.Voice), "the scene's music item (effects channel, late start)");
        Run(s, 12, game: false);
        t.Equal(1, ends, "over at its end (21.2 s)");
        t.True(!s.Busy && s.Cine == null && !o.Paused, "not busy, pause restored");
        t.True(o.Files.Where(f => !f.Loop).All(f => f.Ended) && o.Files.All(f => f.Ended), "every sound stopped");
        s.Update(0.016);
        t.True(o.MusicSeq.SequenceEqual(new[] { true, false }), "music: Sequence switch off");
    }

    public static void LineCutsceneAndSkip(TestContext t)
    {
        var (s, o, _) = Mission(t);
        o.Length = _ => -1;
        // sc_1070: lines, no timeline
        var seq = s.Data.Sequence("sc_1070.seq")!;
        t.True(seq.Play == null && seq.Lines.Count == 2, "sc_1070: 2 lines, no timeline");
        int ends = 0;
        s.PlaySequence(seq, null, () => ends++);
        s.Update(0.016);
        t.True(s.Cine!.Kind == CineKind.Lines && s.Cine.I == 0, "a line scene");
        var l0 = s.Cine.Text;
        t.Near(MissionUiState.LineLength(l0), s.Cine.Len, 1e-9, "reading time");
        s.Next();
        t.Equal(1, s.Cine.I, "Space: next line");
        Run(s, MissionUiState.LineLength(s.Cine.Text) + 0.1, game: false);
        t.Equal(1, ends, "over after the last line");
        // Esc
        s.PlaySequence(s.Data.Sequence("sc_1050.seq"), null, () => ends++);
        s.Update(0.016);
        s.Skip();
        t.True(ends == 2 && s.Cine == null, "Esc ends the scene");
        // unknown and empty sequences end at once (on the next update, not inside PlaySequence)
        s.PlaySequence(null, null, () => ends++);
        t.Equal(2, ends, "not inside PlaySequence");
        s.Update(0.016);
        t.Equal(3, ends, "an unknown sequence ends on the next update");
        // the quest log blocks a cutscene; it starts when the log closes
        s.OpenLog();
        s.PlaySequence(seq, null, () => ends++);
        s.Update(0.016);
        t.True(s.Cine == null && s.Busy, "waits under the open quest log");
        s.CloseLog();
        s.Update(0.016);
        t.True(s.Cine != null, "starts after the log closed");
        t.True(!s.OpenLog(), "no quest log during a cutscene");
    }

    public static void FilmsThatCannotPlay(TestContext t)
    {
        var (s, o, _) = Mission(t);
        int ends = 0;
        s.PlaySequence(s.Data.Sequence("ms_1120.seq"), null, () => ends++);
        s.Update(0.016);
        t.True(s.Cine!.Kind == CineKind.FilmCard && s.Cine.MissingFilm == "hs_1120.bik", "no playable film, no lines: the card");
        t.True(s.LogLines.Any(l => l[0] == "filmMissing"), "logged filmMissing");
        Run(s, MissionUiState.FilmCardTime + 0.1, game: false);
        t.Equal(1, ends, "the card ends after its time");
        // a playable film: the films one after the other, the view reports their ends
        o.Films = _ => true;
        s.PlaySequence(s.Data.Sequence("ms_1000_1010.seq"), null, () => ends++);
        s.Update(0.016);
        t.True(s.Cine!.Kind == CineKind.Film && s.Cine.Films.Count == 3 && s.Cine.FilmIndex == 0, "three films");
        s.FilmEnded(); s.FilmEnded();
        t.Equal(2, s.Cine!.FilmIndex, "third film");
        s.FilmEnded();
        t.True(ends == 2 && s.Cine == null, "over after the last film");
    }

    // ---- quests, ticker, log, end screen
    public static void QuestsTickerLogAndEnd(TestContext t)
    {
        var (s, o, script) = Mission(t);
        var q = script.Quests;
        q[0].Visible = true;                         // the engine sets the flags before the call
        s.QuestChanged(q[0], "shown");
        t.True(s.News.Count == 1 && s.News[0].Title == "New quest" && s.News[0].Text == "Heroes never die" && s.News[0].Kind == "new" && s.News[0].Icon == "nticon_quest_new_normal", "ticker line of a new quest");
        t.True(o.Sounds.Contains("ui_quest_new") && s.Unread, "sound, button flashes");
        q[1].Visible = true; q[1].Accomplished = true;
        s.QuestChanged(q[1], "done");
        t.True(s.News[1].Kind == "good" && o.Sounds.Contains("ui_quest_accomplished"), "accomplished");
        var side = q.First(x => !x.Main);
        side.Visible = true; side.Unaccomplishable = true;
        s.QuestChanged(side, "failed");
        s.QuestChanged(q[2], "hidden");
        t.Equal(3, s.News.Count, "hidden: no ticker line");
        var groups = s.QuestGroups();
        t.True(groups[0].Title == "Heroes" && groups.Last().Key == "SubQuestGroup" && groups.Last().Title == "Subquests", "groups: main quest groups by title, side quests last");
        t.True(groups.Sum(g => g.Max) == q[0].Bonus[1] + q[1].Bonus[1] + side.Bonus[1] && groups.Sum(g => g.Got) == q[1].Bonus[1], "points got / possible (medium)");
        t.True(s.OpenLog() && o.Paused && !s.Unread, "the log opens, pauses, stops the flashing");
        t.True(s.Fresh.Count == 3 && s.SelectedQuest() == q[0], "everything is new; the first open quest is selected");
        t.True(s.QuestDescription(q[0]).Contains("must survive"), "its description");
        s.CloseLog();
        t.True(!o.Paused, "closed: the game runs again");
        s.OpenLog();
        t.True(s.Fresh.Count == 0, "seen quests are not new any more");
        s.CloseLog();
        // a click on a ticker line opens the log at its quest
        s.ClickNews(s.News[0]);
        t.True(s.LogOpen && s.LogSel == q[0].Guid && s.News.Count == 2, "click: log at the quest, line gone");
        s.CloseLog();
        // the ticker lines go after 12 s
        Run(s, 12.1, game: false);
        t.Equal(0, s.News.Count, "ticker lines gone after 12 s");
        // the end screen
        var e = s.EndMission(false, "A hero is dead!");
        t.True(!e.Won && e.Result == "Mission has failed." && e.Title == "Mission 1: Stranded" && e.Text == "A hero is dead!", "defeat: result line, title, reason");
        t.True(e.Quests.Count == 3 && e.Earned == q[1].Bonus[1] && e.PointsLine == $"Bonus points: {e.Earned} of {e.Max}", "quests and bonus points");
        t.True(!s.OpenLog(), "no log after the end");
    }

    public static void MarkersQuestionMarksTimersInfoBar(TestContext t)
    {
        var (s, _, _) = Mission(t);
        s.ObjectPos = id => id == 7 ? (100.0, 200.0) : null;
        s.Marker(new MapMarker { Id = "m1", Kind = "SPMainQuest", X = 10, Y = 20, Ttl = 2, Repeats = 1, Interval = 1 });
        s.Marker(new MapMarker { Id = "m2", Kind = "FixedColor", Color = 0x123456, Obj = 7 });
        t.True(s.Markers[0].Color == 0xffc864 && s.Markers[1].Color == 0x123456, "colours by kind (MM:238-262), FixedColor its own");
        t.True(s.Markers[1].X == 100 && s.Markers[1].Y == 200, "a marker on an object is where the object is");
        Run(s, 2.5);
        t.True(!MissionUiState.MarkerOn(s.Markers[0]), "dark between the repeats");
        Run(s, 1.0);
        t.True(MissionUiState.MarkerOn(s.Markers[0]), "lit again");
        Run(s, 2.0);
        t.True(s.Markers.Count == 1 && s.Markers[0].Id == "m2", "gone after ttl + repeats x (ttl + interval)");
        s.RemoveMarker("m2");
        t.Equal(0, s.Markers.Count, "removed");
        var qm = s.Data.QuestionMarks[1];
        t.True(!s.QuestionMarks[qm.Guid].Visible, "question marks start invisible");
        s.SetQuestionMark(qm.Guid, "QM_STATE_YELLOW", "Talk to the spearman");
        var m = s.QuestionMarks[qm.Guid];
        t.True(m.Visible && m.Glyph == "?" && m.Model == "questionmark_yellow" && m.Tooltip == "Talk to the spearman" && m.X == qm.X, "yellow question mark at its place with its tooltip");
        s.SetQuestionMark(qm.Name, "EC_STATE_YELLOW", null);
        t.True(m.Glyph == "!" && m.Tooltip == "Talk to the spearman", "by name; exclamation mark; tooltip kept");
        s.SetQuestionMark(qm.Guid, "STATE_INVISIBLE", null);
        t.True(!m.Visible, "invisible again");
        var timers = new List<LevelTimer> { new() { Id = "a", Left = 75.2, Show = true, Label = "Reinforcements" }, new() { Id = "b", Left = 5, Show = true, Paused = true }, new() { Id = "c", Left = 9, Show = false } };
        s.Timers = () => timers;
        s.Update(0.016);
        t.True(s.TimerViews.Count == 2 && s.TimerViews[0].Clock == "01:16" && s.TimerViews[0].Label == "Reinforcements" && !s.TimerViews[0].Low, "a shown timer");
        t.True(s.TimerViews[1].Low && s.TimerViews[1].Paused, "last 10 s: red; paused: dimmed");
        s.InfoBar("Wood: 3 / 10");
        t.Equal("Wood: 3 / 10", s.Bar, "info bar");
        s.InfoBar("");
        t.Equal("", s.Bar, "info bar off");
    }

    // ---- the bridge with a real engine
    public static void BridgeWithTriggerEngine(TestContext t)
    {
        var dir = MissionDir(t, "01_single_01");
        var lm = LuaMission.Load(dir, Repo.Std);
        var host = new FakeMissionHost { HoldDialogs = true, HoldSequences = true };
        var eng = new TriggerEngine(host, lm.Script);
        var o = new RecordingUiOutput { Length = _ => 0.5 };
        var ui = new MissionUiBridge(new MissionUiState(MissionUiData.FromJson(lm.World, lm.Mission, null), o));
        foreach (var (k, v) in lm.Script.Texts) ui.State.Data.Texts[k] = v;
        ui.Bind(eng);
        // the engine asks the host; the host forwards to the bridge (what the campaign host will do)
        eng.PlayDialog("Cpn_single_001/single_01/ds_1020.dlg", null);
        eng.PlayDialog("Cpn_single_001/single_01/ds_1023b.dlg", null);
        foreach (var (path, token) in host.Dialogs) t.True(ui.PlayDialog(path, token), "the UI plays the scene");
        t.Equal(2, eng.DialogsRunning.Count(), "two scenes running in the engine");
        Run(ui.State, 10);
        t.True(eng.DialogsEnded.Contains("ds_1020.dlg") && eng.DialogsEnded.Contains("ds_1023b.dlg") && !eng.DialogsRunning.Any(), "EndDialog(token) for both: " + string.Join(",", eng.DialogsEnded) + " / " + string.Join(" | ", ui.State.LogLines.Select(l => string.Join(",", l))));
        // a sequence: the engine starts it in its update when the host is not busy
        t.True(!ui.PlaySequence(new SequenceRequest { Path = "Cpn_single_001/single_01/sc_1070.seq" }), "a sequence without a file (sc_1070): the engine ends it at once");
        var r = new SequenceRequest { Path = "Cpn_single_001/single_01/sc_1050.seq", Trigger = "test" };
        t.True(ui.PlaySequence(r) && ui.IsBusy, "queued and busy");
        ui.State.Update(0.016);
        ui.State.Skip();
        t.True(!ui.IsBusy, "skipped: not busy");
        // quests of the engine
        var q = eng.Quests.First();
        eng.SetQuest(q, 0);
        ui.QuestChanged(q, "shown");
        t.True(ui.State.QuestGroups().Count == 1 && ui.State.News.Count == 1, "the engine's quest in the log and on the ticker");
        ui.SetInfoBar("x");
        t.Equal("x", ui.State.Bar, "info bar");
    }
}
