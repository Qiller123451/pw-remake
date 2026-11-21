// Tests of the loading screen's choices (game/src/UI/Loading/Logic/LoadingInfo.cs, linked into this project): the
// picture candidates of the original's loading art per map, the add-ons' text placeholders, the hints of the help
// book; and every mission of every configuration of the test content (base and MIRAGE, with the BoosterPack / SEAS
// missions) gets a title that is no placeholder and a picture that exists in its data. The journal entries
// (Logic/Journal.cs): the PropDB tables, level -> entry, which kind of loading reads it aloud and waits for the player,
// and every original campaign mission's entry in the test installation (texts, voice files).
//   PW_DATA     a converted data folder (help.json)
//   PW_CONTENT  a content folder with configs/*/ (default: /home/claude/testcontent)
using System;
using System.IO;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.UI.Loading;

namespace PwRemake.Tests;

[TestClass]
public static class LoadingScreenTests
{
    const string Art = LoadingInfo.MenuArt;

    public static void PictureCandidates(TestContext t)
    {
        var c = LoadingInfo.PictureCandidates(3, "Base/Maps/Cpn_single_001/single_03.ula", "Savanna");
        t.Equal(Art + "loadbg_single03.jpg", c[0], "a campaign map: its own picture first");
        t.True(c.IndexOf(Art + "loadbg_mp_savanna.jpg") > c.IndexOf(Art + "loadbg_single03.jpg"), "then the setting's");
        t.True(c.IndexOf(Art + "loadbg_static.jpg") > c.IndexOf(Art + "loadbg_mp_savanna.jpg"), "then the static picture");
        var b = LoadingInfo.PictureCandidates(20, "BoosterPack1/Maps/Cpn_single_001/single_b41_seas.ula", "Northland");
        t.True(b.IndexOf(Art + "loadbg_singleb41_seas.jpg") < b.IndexOf(Art + "loadbg_single_b41.jpg"), "an add-on map: the full name before its base map");
        t.True(b.Contains(Art + "loadbg_single20.jpg"), "the mission number");
        var cave = LoadingInfo.PictureCandidates(5, "single_05.ula", "Cave1");
        t.True(!cave.Any(x => x.Contains("mp_cave")), "no multiplayer picture of a cave");
        t.Equal(Art + "loadbg_single05.jpg", LoadingInfo.PictureCandidates(5, @"Base\Maps\Cpn_single_001\single_05.ula", null)[0], "backslashes");
        t.True(LoadingInfo.PictureCandidates(-1, null, null).Contains(Art + "loadbg_static.jpg"), "nothing known: the static picture");
        t.Equal(Art + "loadbg_seas2.jpg", LoadingInfo.ArtOf("menue/decoration/loadbg_seas2.jpg")[0], "LoadingScreens.txt's picture as the converted art");
        t.True(LoadingInfo.ArtOf("").Count == 0, "no picture named");
    }

    public static void Texts(TestContext t)
    {
        t.Equal("", LoadingInfo.Clean("!boosterpack1.ltf load failed!"), "the add-on placeholder");
        t.Equal("", LoadingInfo.Clean(" !cep.ltf load failed! "), "MIRAGE's placeholder");
        t.Equal("Mission 3: Amazon Island", LoadingInfo.Clean(" Mission 3: Amazon Island "), "a title");
        t.Equal("Single B41", LoadingInfo.Title("!boosterpack1.ltf load failed!", "Single B41", 41), "the map's name instead");
        t.Equal("Mission 7", LoadingInfo.Title("", "", 7), "nothing at all");
        t.Equal("Walls require stone. Second paragraph.", LoadingInfo.HintText("Walls require {ref:h_world.seml#_stone|stone}.\nSecond paragraph."), "links as text, paragraphs joined");
        t.Equal("Walls require stone.", LoadingInfo.HintText("Walls require stone.\nSecond paragraph.", 25), "only what fits");
        t.Equal("Animals.", LoadingInfo.HintText("Animals.\n• a list line\nMore."), "a list line ends it");
        var one = LoadingInfo.HintText(string.Join(" ", Enumerable.Repeat("word", 100)) + ".", 60);
        t.True(one.Length <= 60 && one.EndsWith("…"), $"one long sentence cut at a word: {one}");
        var longText = string.Join(" ", Enumerable.Repeat("This is a sentence of the hint.", 30));
        var h = LoadingInfo.HintText(longText, 100);
        t.True(h.Length <= 100 && h.EndsWith("."), $"whole sentences within the limit: {h}");
    }

    public static void HintsOfTheHelpBook(TestContext t)
    {
        var help = Json.TryParseFile(Path.Combine(t.Data(), "help.json"));
        if (help == null) t.Skip("no help.json");
        var hints = LoadingInfo.Hints(help);
        t.True(hints.Count >= 5, $"hints: {hints.Count}");
        foreach (var h in hints)
        {
            t.True(h.Title != "" && h.Text.Length > 20, $"hint {h.Title}");
            t.True(!h.Text.Contains('{') && !h.Text.Contains('\n'), $"plain one paragraph: {h.Title}");
            t.True(h.Text.Length <= 330, $"short: {h.Title} ({h.Text.Length})");
        }
        t.True(!hints.Any(x => x.Text.StartsWith("The Tips and Tricks section")), "not the chapter's introduction");
        t.True(LoadingInfo.Pick(hints, 12345) != null && LoadingInfo.Pick(hints, -7) != null, "pick");
        t.True(LoadingInfo.Pick(Array.Empty<LoadingHint>(), 1) == null, "none");
        foreach (var h in hints) t.Log($"{h.Title}: {h.Text}");
    }

    const string Screens = @"Root {
	Levels {
		tutorial {
			image = 'menue/decoration/loadbg_single00.jpg'
			title = ''
			text = ''
			audio = ''
		}
		single_01 {
			image = 'menue/decoration/loadbg_single01.jpg'
			title = '_ds_JOURNAL_1070_title'
			text = '_ds_JOURNAL_1070_text'
			audio = 'ds_1070_JOURNAL_ENTRY_(COLE)_01'
		}
		single_12 {
			title = '_ds_JOURNAL_4020_title'
			text = '_ds_JOURNAL_4020_text'
			audio = 'ds_4020_JOURNAL_(ENTRY_BELA)_01'
		}
		single_b41_seas {
			title = '_db_JOURNAL_b41_title'
			text = '_db_JOURNAL_b41_text'
			audio = ''
		}
	}
	Settings {
		Jungle {
			image = 'menue/decoration/loadbg_mp_jungle.jpg'
		}
	}
}";

    /// <summary>LoadingScreens.txt read; a mission's level by its key or map; the plan per kind of loading.</summary>
    public static void JournalMapping(TestContext t)
    {
        var levels = Journal.Levels(PropNode.Parse(Screens));
        t.Equal(4, levels.Count, "levels (not the settings)");
        t.Equal("ds_1070_JOURNAL_ENTRY_(COLE)_01", levels["single_01"].Audio, "the sound event, parentheses kept");
        t.Equal("_ds_JOURNAL_1070_text", Journal.Find(levels, Journal.LevelNames("single_01", "Base/Maps/Cpn_single_001/single_01.ula"))!.TextKey, "by the key");
        t.Equal("single_01", Journal.Find(levels, Journal.LevelNames("", @"Base\Maps\Cpn_single_001\Single_01.ula"))!.Level, "by the map's name");
        t.Equal("single_b41_seas", Journal.Find(levels, Journal.LevelNames("Single B41 SEAS", null))!.Level, "LevelName: lower case, blanks -> _");
        t.True(Journal.Find(levels, Journal.LevelNames("single_17", "single_17.ula")) == null, "a level without a line");

        var tx = new System.Collections.Generic.Dictionary<string, string>
        {
            ["_ds_JOURNAL_1070_title"] = "JOURNAL ENTRY (COLE)",
            ["_ds_JOURNAL_1070_text"] = @"If this is a training camp ... \{br}Maybe I'm not.\{br}\{br}Viking park, yeah right!\{br}",
            ["_db_JOURNAL_b41_title"] = "DIARY (LEIGHTON)",
            ["_db_JOURNAL_b41_text"] = "!boosterpack1.ltf load failed!",
            ["_LoadScreen_LoadingGame"] = "Loading Game...",
        };
        string Tr(string k, string f) => tx.TryGetValue(k, out var v) ? v : f;
        var e = levels["single_01"];
        var camp = Journal.Plan(e, LoadKind.Campaign, Tr)!;
        t.Equal("JOURNAL ENTRY (COLE)", camp.Caption, "caption: the title");
        t.Equal("If this is a training camp ...\nMaybe I'm not.\n\nViking park, yeah right!", camp.Text, "\\{br} as line breaks, none at the end");
        t.Equal("ds_1070_JOURNAL_ENTRY_(COLE)_01", camp.Voice, "a new campaign mission: read aloud");
        t.True(Journal.WaitForStart(LoadKind.Campaign), "a new campaign mission waits for the player");
        var save = Journal.Plan(e, LoadKind.SavedGame, Tr)!;
        t.Equal(camp.Text, save.Text, "a saved game: the text");
        t.True(save.Voice == null && !Journal.WaitForStart(LoadKind.SavedGame), "a saved game: no voice, no ready button (the original)");
        var net = Journal.Plan(e, LoadKind.Network, Tr)!;
        t.True(net.Text != "" && net.Voice == null && !Journal.WaitForStart(LoadKind.Network), "co-op: the text; no voice, no waiting");
        t.True(Journal.Plan(e, LoadKind.Other, Tr) == null && !Journal.WaitForStart(LoadKind.Other), "skirmish / own campaigns: no journal");
        t.True(Journal.Plan(levels["tutorial"], LoadKind.Campaign, Tr) == null, "the tutorial: an empty entry, no window (the hints stay)");
        t.True(Journal.Plan(null, LoadKind.Campaign, Tr) == null, "no entry");
        t.True(Journal.Plan(levels["single_b41_seas"], LoadKind.Campaign, Tr) == null, "a placeholder text: no window");
        var noTitle = Journal.Plan(levels["single_12"] with { TitleKey = "" , TextKey = "_ds_JOURNAL_1070_text" }, LoadKind.Campaign, Tr)!;
        t.Equal("Loading Game...", noTitle.Caption, "no title: _LoadScreen_LoadingGame");
        t.Equal("A\n\nB", Journal.Text(@"\{cogiw_1}A\{p}B\{cp}"), "\\{p} a paragraph, other tags dropped");
    }

    /// <summary>The voice's file: the dialogsounds.txt event, else the table's rule for a journal entry.</summary>
    public static void JournalVoiceFile(TestContext t)
    {
        var ds = PropNode.Parse(@"Root {
	ds_4020_JOURNAL_(ENTRY_BELA)_01 {
		global = '1'
		sound_1 {
			wav = '../SeqSounds/Journal_Entry/4020_JOURNAL_(ENTRY_BELA)_01.mp3'
			volume = '100'
		}
	}
}");
        t.Equal("../SeqSounds/Journal_Entry/4020_JOURNAL_(ENTRY_BELA)_01.mp3", Journal.SoundFile(ds, "ds_4020_JOURNAL_(ENTRY_BELA)_01"), "the table's wav");
        t.Equal("../SeqSounds/Journal_Entry/1070_JOURNAL_ENTRY_(COLE)_01.mp3", Journal.SoundFile(ds, "ds_1070_JOURNAL_ENTRY_(COLE)_01"), "not in the table: the rule");
        t.Equal("../SeqSounds/Journal_Entry/1070_JOURNAL_ENTRY_(COLE)_01.mp3", Journal.SoundFile(null, "ds_1070_JOURNAL_ENTRY_(COLE)_01"), "no table: the rule");
        t.True(Journal.SoundFile(ds, "cep_loading_01") == null, "MIRAGE's cep_loading_01: no sound");
        t.True(Journal.SoundFile(ds, "") == null, "none");
    }

    /// <summary>The installation of the test content: every original campaign mission (1 - 16) has its entry with title,
    /// text and a voice event whose file the table names; the add-on missions have texts without voice.</summary>
    public static void JournalOfEveryMission(TestContext t)
    {
        var root = Environment.GetEnvironmentVariable("PW_CONTENT");
        if (string.IsNullOrEmpty(root)) root = "/home/claude/testcontent";
        if (!Directory.Exists(Path.Combine(root, "configs"))) t.Skip("no test content");
        int n = 0;
        foreach (var cfg in Directory.GetDirectories(Path.Combine(root, "configs")).OrderBy(x => x).Select(GameConfiguration.Load).Where(c => c != null).Select(c => c!))
        {
            if (!Directory.Exists(cfg.MissionsDir) || cfg.GameFile(Journal.ScreensFile) == null) { t.Log($"{cfg.Id}: no LoadingScreens.txt in {cfg.Install}"); continue; }
            var texts = GameTexts.For(cfg);
            foreach (var m in PwRemake.Sim.Campaign.MissionEntry.LoadIndex(cfg.MissionsDir))
            {
                var e = Journal.ForMission(cfg, m.Key, m.Map);
                var show = Journal.Plan(e, LoadKind.Campaign, texts.Get);
                if (m.Id >= 1 && m.Id <= 16)
                {
                    t.True(e != null && e.Level == $"single_{m.Id:00}", $"{cfg.Id} {m.Id}: its line");
                    t.True(show != null && show.Caption.Contains("JOURNAL") && show.Text.Length > 200 && !show.Text.Contains("\\{"), $"{cfg.Id} {m.Id}: title and text");
                    t.True(show!.Voice != null && show.Voice.StartsWith("ds_"), $"{cfg.Id} {m.Id}: voice event");
                    var tablePath = cfg.GameFile(Journal.SoundsFile);
                    var wav = Journal.SoundFile(tablePath == null ? null : Journal.ReadProps(tablePath), show.Voice!);
                    t.True(wav != null && wav.StartsWith("../SeqSounds/Journal_Entry/"), $"{cfg.Id} {m.Id}: voice file {wav}");
                    n++;
                }
                if (m.Id == 0) t.True(show == null, $"{cfg.Id}: the tutorial has none");
                if (m.Key.StartsWith("single_b4")) t.True(show == null || (show.Voice == null && show.Caption != ""), $"{cfg.Id} {m.Key}: an add-on entry has no voice");
                var voicePath = show?.Voice == null ? null : Journal.VoicePath(cfg, show.Voice);
                t.Log($"{cfg.Id} {m.Id,2} {m.Key,-18} {(show == null ? "(no entry: hints)" : $"{show.Caption,-24} {show.Text.Length,4} chars  voice {show.Voice ?? "-"} {(show.Voice == null ? "" : voicePath == null ? "(file not in this installation)" : "-> " + voicePath)}")}");
            }
        }
        if (n == 0) t.Skip("the test installation has no LoadingScreens.txt");
    }

    /// <summary>Every mission of every configuration: a real title, and the picture the screen will show.</summary>
    public static void EveryMission(TestContext t)
    {
        var root = Environment.GetEnvironmentVariable("PW_CONTENT");
        if (string.IsNullOrEmpty(root)) root = "/home/claude/testcontent";
        if (!Directory.Exists(Path.Combine(root, "configs"))) t.Skip("no test content");
        int n = 0;
        var all = Directory.GetDirectories(Path.Combine(root, "configs")).OrderBy(x => x).Select(GameConfiguration.Load).Where(c => c != null).Select(c => c!).ToList();
        foreach (var cfg in all)
        {
            if (!Directory.Exists(cfg.MissionsDir)) continue;
            // as the screen looks: this configuration's data first, then the others' (LoadingScreen.Sources)
            var sources = all.OrderBy(c => c == cfg ? 0 : 1).Select(c => new AssetSource(c)).ToList();
            string? Resolve(string rel) => sources.Select(s => s.Resolve(rel)).FirstOrDefault(p => p != null);
            bool hasArt = Resolve(Art + "loadbg_static.jpg") != null;
            var missions = PwRemake.Sim.Campaign.MissionEntry.LoadIndex(cfg.MissionsDir);
            foreach (var m in missions)
            {
                var mj = PwRemake.Sim.Maps.PwMapReader.MissionOf(Path.Combine(cfg.MissionsDir, m.Folder));
                var title = LoadingInfo.Title(m.Title, m.Name, m.Id);
                t.True(!title.Contains("load failed"), $"{cfg.Id} {m.Id}: title {title}");
                var desc = LoadingInfo.Clean(m.Description);
                t.True(!desc.Contains("load failed"), $"{cfg.Id} {m.Id}: description");
                var pic = LoadingInfo.PictureCandidates(m.Id, m.Map, mj?["map_info"]?["setting"].Str()).FirstOrDefault(x => Resolve(x) != null);
                if (hasArt) t.True(pic != null, $"{cfg.Id} {m.Id}: a picture");
                t.Log($"{cfg.Id} {m.Id,2} {title,-40} {(desc == "" ? "(no description)" : "")} {(pic == null ? "(no picture: the data has no menu art)" : Path.GetFileName(pic))}");
                n++;
            }
        }
        t.True(n > 0, "missions");
    }
}
