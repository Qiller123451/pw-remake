// The campaign's question / exclamation marks' tooltips (game/src/UI/Input/GameInput.Marks.cs): a mark's text is the one
// the trigger's QMRK action gave it (questiontooltip, a key of the mission's string tables, translated - the original's
// "QuestionMarkToolTip" attribute, IngameScreen.usl OnTick). PW_CONTENT: a content folder with configs/<cfg>/missions
// (default /home/claude/testcontent).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Scripting;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.Tests;

[TestClass]
public static class QuestionMarkTipTests
{
    static string Root()
    {
        var root = Environment.GetEnvironmentVariable("PW_CONTENT");
        return string.IsNullOrEmpty(root) ? "/home/claude/testcontent" : root;
    }

    /// <summary>mission 3 (MIRAGE): questionmark_10 shown yellow with "_TTQ_G_01" has the mission's text; hidden: no tip</summary>
    public static void MarkTextOfMission3(TestContext t)
    {
        var dir = Path.Combine(Root(), "configs", "mirage", "missions", "03_single_03");
        if (!Directory.Exists(dir)) t.Skip($"no mission folder {dir}");
        var script = LuaMission.Load(dir, Repo.Std).Script;
        var s = new MissionUiState(MissionUiData.Load(dir), new RecordingUiOutput());
        // the trigger's action as the engine runs it (Actions.QMRK: E.Text(questiontooltip))
        var act = script.Triggers.SelectMany(x => x.Actions).First(a => a.Code == "QMRK" && a.P.GetValueOrDefault("questiontooltip") == "_TTQ_G_01");
        string guid = act.P["questionmark"];
        string text = script.Texts.TryGetValue("_TTQ_G_01", out var tx) ? tx : "_TTQ_G_01";
        s.SetQuestionMark(guid, "QM_STATE_YELLOW", text);
        var q = s.QuestionMarks[guid];
        t.Equal("Quest: Move a unit close to this position!", q.Tip()?.Text, "hover text");
        t.True(q.Tip()?.Title == null, "hover: no title");
        t.Equal("Quest", q.Tip("Quest")?.Title, "picked: titled");
        s.SetQuestionMark(guid, "STATE_INVISIBLE", null);
        t.True(q.Tip() == null, "a hidden mark has no tooltip");
        // a mark whose key is not in the tables (the engine passes the key on) shows nothing
        s.SetQuestionMark(guid, "EC_STATE_YELLOW", "_TTQ_nothing");
        t.True(q.Tip() == null, "an untranslated key: no tooltip");
    }

    /// <summary>every QMRK action of every MIRAGE mission names a text the mission's string tables have - but one: the
    /// original's _TTQ_LB42_21 (single_b42) is in none of its tables (the mark shows no tooltip). (The test content's Base
    /// folder carries the SEAS missions without their MIRAGE texts: not checked.)</summary>
    public static void EveryMarkKeyHasText(TestContext t)
    {
        int keys = 0; var missing = new System.Collections.Generic.List<string>();
        foreach (var cfg in new[] { "mirage" })
        {
            var md = Path.Combine(Root(), "configs", cfg, "missions");
            if (!Directory.Exists(md)) continue;
            foreach (var dir in Directory.GetDirectories(md).OrderBy(x => x))
            {
                if (!File.Exists(Path.Combine(dir, "triggers.lua"))) continue;
                var script = LuaMission.Load(dir, Repo.Std).Script;
                foreach (var a in script.Triggers.SelectMany(x => x.Actions).Where(a => a.Code == "QMRK"))
                {
                    var k = a.P.GetValueOrDefault("questiontooltip");
                    if (string.IsNullOrEmpty(k) || a.P.GetValueOrDefault("questionstate") == "STATE_INVISIBLE") continue;
                    keys++;
                    if (!script.Texts.ContainsKey(k) && k != "_TTQ_LB42_21") missing.Add($"{cfg}/{Path.GetFileName(dir)}: {k}");
                }
            }
        }
        if (keys == 0) t.Skip("no missions with question marks");
        t.Log($"{keys} shown marks' keys, {missing.Count} without a text{(missing.Count > 0 ? ": " + string.Join(", ", missing.Distinct().Take(10)) : "")}");
        t.True(missing.Count == 0, "every shown mark's key has a text");
    }
}
