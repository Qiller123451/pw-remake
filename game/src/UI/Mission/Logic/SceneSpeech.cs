// Who speaks a cutscene's lines, and whose mouth moves (player report, mission 13's opening sc_4070: "I noticed Bela
// moving his mouth when Cole was speaking (Cole was moving his mouth too)").
//
// The mouth of the original's characters is the jaw bone (toolkit docs/original/LIP_SYNC.md): the lip sync adds at most
// 4.8 degrees per unit of weight on top of what the body clip does with it - almost nothing. What shows is the body
// clip: the talk gestures the scenes give their actors (talking_*, seq_talk_*, all_talk_*) open and close the jaw by
// 6-15 degrees (bela_s0 talking_0 8.2, talking_3 9.1, seq_talk_05 15.1; idle_1..6 hold it shut). The scenes run these
// gestures longer than the actor's own line: in sc_4070 Bela's talking_0 (18.04-30.28 s) runs 3.4 s into Cole's first
// line (26.88 s), his talking_a (34.88-38.96 s) through Cole's second (33.4-37.24 s); Cole's seq_talk_stand_001_anim
// (28.72-34.24 s) through Bela's second (30.96-33.32 s). The original plays them so (the same items, the lip layers on
// top: LIP_SYNC.md §6) - 274 such stretches of more than half a second in the base campaign's 16 missions and tutorial.
// The remake goes beyond it here: a talk gesture moves the jaw only while its actor speaks (Mute: the jaw goes to its
// rest pose - the closed mouth of idle_1 - over Ramp seconds when the line ends and comes back when it begins).
//
// Which actor speaks a line: the actor whose item it is, unless the line is a character's whom that actor is not. The
// scenes put some lines on another actor (the item's speech file and subtitle key name the speaker: 4070_Bela_01): mission
// 1's sc_1050 has Stina's lines on Cole's actor while Stina rides in the scene (NS1050_stina_eusmilus) - her line goes to
// her; mission 6's sc_8011 has Stina's and Bela's lines on Cole (neither in the scene: an off-screen voice - nobody's
// mouth), mission 15's sc_7038 Stina's on Cole, mission 16's sc_7052 Babbit's on Stina, sc_7030_* Cole's and Stina's on
// SEAS gunners. A line of a character nobody in the scene is, on another named character or a sound effect
// (sfx_* / seq_*), moves no mouth; a line of an unnamed speaker (a lieutenant, a temple guard, the Norsemen) stays on its
// actor. The speakers' names are the speech files' (Cole, Stina, Bela, Kleemann ...); their classes: Speakers.
//
// No Godot types: the tests link this file.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;

namespace PwRemake.UI.Mission.Logic;

public static class SceneSpeech
{
    /// <summary>s: a talk gesture's jaw goes still (to the rest pose) or comes back over this time at a line's end / start</summary>
    public const double Ramp = 0.15;

    /// <summary>a speaker's name in the speech files -> the word of the class its character is drawn with (the heroes and
    /// the named characters of the base campaign; a name not here is its own class word: cole, stina, bela)</summary>
    public static readonly IReadOnlyDictionary<string, string> Speakers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cole"] = "cole", ["stina"] = "stina", ["bela"] = "bela",
        ["kleemann"] = "schliemann", ["taslov"] = "tesla", ["ada"] = "lovelace", ["leighton"] = "livingstone",
        ["warden"] = "darwin", ["babbit"] = "babbage", ["druid"] = "hermit", ["tarna"] = "queen", ["amazonen_koenigin"] = "queen",
        ["trader_barry"] = "trader", ["haendler_larry"] = "trader",
    };
    static readonly HashSet<string> CharacterWords = new(Speakers.Values, StringComparer.Ordinal);

    /// <summary>One line of a scene: the actor (index into play.actors) whose item it is and the one who speaks it (-1:
    /// nobody in the scene), its time and length, the item.</summary>
    public sealed record Line(int Holder, int Speaker, double T, double Len, JsonNode Item, string Name);

    static readonly Regex FileName = new(@"^(?:\d+[a-z]?_|single_\d+_)?(.*?)(?:_\d+[a-z]?)?$", RegexOptions.CultureInvariant);

    /// <summary>the speaker's name of a speech item (from its speech file: Level_13/4070/4070_Bela_01.mp3 -> "bela";
    /// add_ lines are the same speaker's); "" for none</summary>
    public static string SpeakerName(JsonNode item)
    {
        var f = item["mp3"].Str();
        if (f.Length == 0) f = item["lsd"].Str();
        f = f.Replace('\\', '/');
        f = f[(f.LastIndexOf('/') + 1)..];
        int dot = f.LastIndexOf('.');
        if (dot > 0) f = f[..dot];
        var n = FileName.Match(f.ToLowerInvariant()).Groups[1].Value;
        if (n.StartsWith("add_")) n = n[4..];
        return n;
    }

    // the words of a class name (ns1050_stina_eusmilus: ns1050, stina, eusmilus)
    static string[] Words(string cls) => cls.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
    /// <summary>the named character an actor's class is (its word of Speakers' values), else null</summary>
    public static string? CharacterOf(string cls) => Words(cls).FirstOrDefault(CharacterWords.Contains);

    /// <summary>The scene's spoken lines (say items that are no music) with their speakers (above).</summary>
    public static List<Line> Lines(JsonArray? actors)
    {
        var res = new List<Line>();
        if (actors == null) return res;
        var chars = actors.Select(a => CharacterOf(a?["cls"].Str() ?? "")).ToList();
        for (int i = 0; i < actors.Count; i++)
        {
            if (actors[i]?["items"] is not JsonArray items) continue;
            foreach (var it in items)
            {
                if (it == null || it["k"].Str() != "say" || it["music"].Bool() == true) continue;
                var name = SpeakerName(it);
                res.Add(new Line(i, SpeakerOf(i, name, chars), it["t"].Num(), it["len"].Num(), it, name));
            }
        }
        return res;
    }

    static int SpeakerOf(int holder, string name, List<string?> chars)
    {
        if (name.Length == 0 || name.StartsWith("sfx") || name.StartsWith("seq")) return -1;       // (a sound effect)
        if (!Speakers.TryGetValue(name, out var who)) return holder;                                // an unnamed speaker: its actor
        if (chars[holder] == who) return holder;
        int other = chars.IndexOf(who);
        return other >= 0 ? other : -1;          // that character elsewhere in the scene, else an off-screen voice
    }

    /// <summary>how much actor `actor` speaks at scene time t: 1 within one of its lines, 0 outside, ramped over Ramp
    /// seconds at the ends</summary>
    public static double Speaking(IReadOnlyList<Line> lines, int actor, double t)
    {
        double w = 0;
        foreach (var l in lines)
        {
            if (l.Speaker != actor) continue;
            double a = l.T, b = l.T + l.Len;
            double v = t < a - Ramp || t > b + Ramp ? 0 : t < a ? 1 - (a - t) / Ramp : t > b ? 1 - (t - b) / Ramp : 1;
            if (v > w) w = v;
        }
        return w;
    }

    /// <summary>a talk gesture: a clip that moves the jaw to speak (talking_*, seq_talk_*, all_talk_*)</summary>
    public static bool IsTalkClip(string? clip) => clip != null && clip.Contains("talk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How far a character's jaw is held at its rest pose (closed) at scene time t (0: as its body clip has it, 1: still):
    /// while the scene's animation item of the moment is a talk gesture (clip: that item's clip, null for none) and the
    /// actor does not speak.
    /// </summary>
    public static double Mute(IReadOnlyList<Line> lines, int actor, string? clip, double t) =>
        IsTalkClip(clip) ? 1 - Speaking(lines, actor, t) : 0;
}
