// Tests of the menus' and cutscenes' logic ported with port/menus: the lip sync (an .lsd of the installation, the
// phoneme table, the jaw's weights), the cutscenes' day time items, the game's own interface texts (GameTexts: the
// installation's string tables, the options window's captions).
//   PW_CONTENT  a content folder with configs/base (its installation: the .lsd files of Level_1/1040, main_uk.ltf);
//               default /home/claude/testcontent
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.Tests;

[TestClass]
public static class MenusCutsceneTests
{
    static GameConfiguration Base(TestContext t)
    {
        var root = Environment.GetEnvironmentVariable("PW_CONTENT");
        if (string.IsNullOrEmpty(root)) root = "/home/claude/testcontent";
        var c = GameConfiguration.Load(Path.Combine(root, "configs", "base"));
        if (c == null || !Directory.Exists(c.Install)) t.Skip("no base configuration with an installation (PW_CONTENT)");
        return c!;
    }

    public static void LipSyncPhonemeTable(TestContext t)
    {
        // Setup.ini LipSyncSetup: all 40 phoneme ids, each once; frame 1 = IY, IH, h, y
        t.Equal(40, LipSync.PhonemeFrame.Count, "ids");
        t.True(Enumerable.Range(0, 40).All(LipSync.PhonemeFrame.ContainsKey), "ids 0..39");
        t.True(LipSync.PhonemeFrame.Where(kv => kv.Value == 1).Select(kv => kv.Key).OrderBy(x => x).SequenceEqual(new[] { 1, 2, 17, 39 }), "frame 1: IY IH y h");
        // the frame 1 pose of the shipped faceanim: 4.8 degrees
        var f = LipSync.Frame1;
        t.Near(4.8, 2 * Math.Acos(f[3] / Math.Sqrt(f.Sum(x => x * x))) * 180 / Math.PI, 0.05, "frame 1 turn");
        t.True(LipSync.UsableTurn(new double[] { 0, 0, 0, 1 }, f), "frame 1 usable");
        // the converted models' faceanim keys (zeros, a huge w): left out
        t.True(!LipSync.UsableTurn(new double[] { 0, 0, 0, 0 }, new double[] { 0, 0.04, 0, -5e31 }), "broken keys");
        t.True(!LipSync.UsableTurn(new double[] { 0, 0, 0, 1 }, new double[] { 0, 0, 0.7071, 0.7071 }), "a turn no jaw makes (90 degrees)");
    }

    public static void LipSyncLsd(TestContext t)
    {
        var c = Base(t);
        var f = c.GameFile("Audio/SeqSounds/Level_1/1040/1040_Cole_01.lsd");
        if (f == null) t.Skip("1040_Cole_01.lsd is not in the installation");
        var recs = LipSync.ReadLsd(File.ReadAllBytes(f!));
        t.True(recs != null, "read");
        t.Equal(35, recs!.Count, "records");
        t.Near(4 / 25.0, recs[4].T, 1e-9, "frame 4 at 0.16 s (25 fps)");
        t.Near(0.866, recs[4].Weights.First(w => w.Id == 1).W, 0.001, "IY at frame 4");
        t.True(LipSync.ReadLsd(new byte[] { 1, 2, 3 }) == null, "not gzip");
        // the jaw's layers: only the phonemes on frame 1 (IY 1 and y 17 in this line), at the item's time
        var layers = LipSync.Layers(recs, 10);
        t.Equal(2, layers.Count, "layers IY, y");
        t.Near(10 + 2 / 25.0, layers[0][0].T, 1e-9, "first IY key at the item's start + 2 frames");
        t.Near(0.866, LipSync.Weight(layers, 10 + 4 / 25.0), 0.001, "IY's peak (Sti-)");
        t.Near(0.452, LipSync.Weight(layers, 10 + 23 / 25.0), 0.001, "y (you)");
        t.Near(0.0, LipSync.Weight(layers, 10 + 15 / 25.0), 1e-9, "between: 0");
        t.Near(0.207, LipSync.Weight(layers, 30), 0.001, "the last key held (LIP_SYNC.md: the jaw stays lifted ~1 degree)");
        t.Near(0.0, LipSync.Weight(layers, 5), 1e-9, "before: the first key (0)");
        // a clip of one frame shows nothing (frame 1 does not exist)
        t.Equal(0, LipSync.Layers(recs, 0, frames: 1).Count, "1-frame clip");
    }

    public static void SceneDayTimeItems(TestContext t)
    {
        var items = new[]
        {
            TimelineItem.FromJson(JsonNode.Parse("{\"k\":\"daytime\",\"t\":0,\"len\":1,\"tod\":46800,\"weather\":0,\"intensity\":25}")),
            TimelineItem.FromJson(JsonNode.Parse("{\"k\":\"bars\",\"t\":0,\"len\":0,\"on\":true}")),
            TimelineItem.FromJson(JsonNode.Parse("{\"k\":\"daytime\",\"t\":10,\"len\":2,\"tod\":82800,\"weather\":2,\"intensity\":50}")),
        };
        t.True(SceneDayTime.At(items.Skip(1).Take(1), 5) == null, "no day time item: the world's");
        var d = SceneDayTime.At(items, 0.5)!.Value;
        t.Near(46800, d.Tod, 1e-9, "held for its length");
        t.Equal(0, d.Weather, "weather type 0: none");
        t.Near(0.25, d.Intensity, 1e-9, "intensity / 100");
        t.Near(46800 + 2 * 144, SceneDayTime.At(items, 3)!.Value.Tod, 1e-6, "then running on (144 game s per s)");
        t.True(!SceneDayTime.IsNight(SceneDayTime.At(items, 3)!.Value.Tod), "13:00 is day");
        var n = SceneDayTime.At(items, 14)!.Value;
        t.Near((82800 + 2 * 144) % 86400, n.Tod, 1e-6, "the later item");
        t.Equal(2, n.Weather, "rain");
        t.True(SceneDayTime.IsNight(n.Tod), "23:00 is night");
        // the shipped missions' day time items have a time of day and weather type 0
        var root = Environment.GetEnvironmentVariable("PW_CONTENT");
        if (string.IsNullOrEmpty(root)) root = "/home/claude/testcontent";
        var wj = PwRemake.Sim.Maps.PwMapReader.WorldOf(Path.Combine(root, "configs", "base", "missions", "01_single_01"));
        if (wj == null) return;
        var seq = wj["sequences"]!["Cpn_single_001/single_01/sc_1050.seq"]!;
        var play = SequenceTimeline.FromJson(seq["play"])!;
        var day = SceneDayTime.At(play.Items, play.End);
        t.True(day != null && day.Value.Weather == 0, "sc_1050: a day time item, no weather");
    }

    /// <summary>the original's day-time light (SkyGradient: the sky table's sampling and the sun's way, PWClient.exe
    /// CSkyMgr) - a small table written here (the game's come from the installation: tools/prepare/sky.py)</summary>
    public static void SkyTableAndSunPath(TestContext t)
    {
        var g = SkyGradient.FromJson(JsonNode.Parse("{\"source\":\"test\",\"rows\":[[\"000000ff\",\"ffffff80\",\"000000ff\"],[\"10203040\",\"10203040\",\"10203040\"]]}"))!;
        t.Equal(3, g.Width, "width"); t.Equal(2, g.Height, "height");
        t.Near(0, g.At(0, 0).R, 1e-9, "midnight: the first column");
        t.Near(1, g.At(0, 43200).G, 1e-9, "noon: the middle column of three");
        t.Near(0.5, g.At(0, 21600).B, 1e-9, "6:00: halfway between the first and the middle column");
        t.Near((255 + 128) / 2.0 / 255, g.At(0, 21600).A, 1e-9, "the alpha too");
        t.Near(g.At(0, 43200).R, g.At(2, 43200).R, 1e-9, "a missing row: row 0 (the client's getters)");
        t.True(g.Varies(0) && !g.Varies(1), "a row that changes over the day / one that does not");
        t.True(SkyGradient.FromJson(JsonNode.Parse("{\"rows\":[[\"000000ff\"],[\"00\"]]}")) == null, "a damaged table is refused");
        var (az, el) = SkyGradient.SunAngles(43200);
        t.Near(0.6, el, 1e-9, "noon: elevation 0.6 rad");
        t.Near(0.2, SkyGradient.SunAngles(0).Elevation, 1e-9, "midnight: 0.2 rad - the light stays above the horizon");
        t.Near(Math.PI / 2, az % (2 * Math.PI), 1e-9, "noon: azimuth pi/2 (2 pi f + 3 pi / 2)");
        var dir = SkyGradient.SunDirection(43200);
        t.True(Math.Abs(dir.X) < 1e-9 && dir.Z > 0.8 && Math.Abs(dir.Y - Math.Sin(0.6)) < 1e-9, "noon: the sun over the map's +y (game +z)");
    }

    public static void GameTextsOfTheInstallation(TestContext t)
    {
        // the .ltf format: "key";"text", "" a quote, \n a line break, the header line left out
        var dir = Path.Combine(Path.GetTempPath(), "pwtexts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "x.ltf");
            File.WriteAllText(f, "\uFEFF\"Suit Localization File\";\"1\"\r\n\"_A\";\"Master Volume:\"\r\n\"_B\";\"say \"\"hi\"\"\\nthere\"\r\n\"_A\";\"second\"\r\n", new System.Text.UTF8Encoding(true));
            var g = new GameTexts();
            g.ReadLtf(f);
            t.Equal(2, g.Count, "keys");
            t.Equal("Master Volume", g.Label("_A", "?"), "label without the colon; the first text wins");
            t.Equal("say \"hi\"\nthere", g.Get("_B", "?"), "quotes and line breaks");
            t.Equal("fallback", g.Get("_missing", "fallback"), "fallback");
        }
        finally { Directory.Delete(dir, true); }
        // the installation's: the options window's captions
        var c = Base(t);
        var tx = GameTexts.For(c);
        if (tx.Count == 0) t.Skip("no string tables in the installation");
        t.Equal("Master Volume", tx.Label("_UI_OptionsM_Label_MasterVolume", "?"), "_UI_OptionsM_Label_MasterVolume");
        t.Equal("Options", tx.Get("_UI_OptionsM_WindowTitle", "?"), "window title");
        t.Equal("Show Subtitles", tx.Get("_UI_OptionsM_CheckBox_ShowSubtitles", "?"), "subtitles");
        foreach (var k in new[] { "Graphics", "Advanced", "Sound", "Gameplay", "Controls", "Back", "Mute", "Reset" })
            t.True(tx.Has("_UI_OptionsM_Button_" + k), "_UI_OptionsM_Button_" + k);
        t.True(ReferenceEquals(tx, GameTexts.For(c)), "read once per installation");
    }
}
