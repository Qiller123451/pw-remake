// What the loading screen of a mission shows, without Godot types (compiled into the tests too):
//
//   * the picture: the original's loading art of the mission (Data/<folder>/UI/menue/decoration/loadbg_singleNN.jpg,
//     converted by the toolkit's step "menu" to assets/ui/menu/menue__decoration__loadbg_singleNN.jpg). Looked up
//     by the map's file name (single_03.ula -> loadbg_single03; the add-ons' single_b41.ula -> loadbg_single_b41 /
//     loadbg_singleb41 ...), then the mission number, then the multiplayer picture of the map's setting
//     (loadbg_mp_jungle ...), then the game's static loading picture (loadbg_static) - the first that exists.
//   * the title and the description of the mission (mission.json); an add-on's missing string table gives the game's
//     placeholder "!boosterpack1.ltf load failed!", which is never shown (the map's name instead, no description).
//   * a hint: one of the original help book's "Hints" chapter (help.json, h_tipps.seml - the tips and tricks of the
//     game's own help), its first paragraph, the help links ({ref:file#key|text}) as their plain text.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PwRemake.UI.Loading;

public sealed record LoadingHint(string Title, string Text);

public static class LoadingInfo
{
    /// <summary>where the toolkit puts the menu art (remake/pipeline/build_menu.py)</summary>
    public const string MenuArt = "assets/ui/menu/menue__decoration__";

    static readonly Regex Placeholder = new(@"^\s*!\S+\.ltf load failed!\s*$", RegexOptions.IgnoreCase);
    static readonly Regex Ref = new(@"\{ref:[^|}]*\|([^}]*)\}");
    static readonly Regex AnyTag = new(@"\{[a-z]+:[^}]*\}");

    /// <summary>the relative paths (AssetSource) of the pictures to try, best first</summary>
    public static List<string> PictureCandidates(int missionId, string? map, string? setting)
    {
        var names = new List<string>();
        void Add(string n) { if (!names.Contains(n)) names.Add(n); }
        // the map's file name: Base/Maps/Cpn_single_001/single_03.ula -> single_03
        var stem = Path.GetFileNameWithoutExtension((map ?? "").Replace('\\', '/')).ToLowerInvariant();
        var m = Regex.Match(stem, @"^single_?(.+)$");
        if (m.Success)
        {
            var rest = m.Groups[1].Value;                              // 03, b41, b41_seas, seas_02
            var parts = rest.Split('_');
            for (int n = parts.Length; n >= 1; n--)                    // b41_seas, then b41
            {
                var p = string.Join("_", parts.Take(n));
                Add($"loadbg_single{p}");
                Add($"loadbg_single_{p}");
            }
        }
        else if (stem != "") Add($"loadbg_{stem}");
        if (missionId >= 0) Add($"loadbg_single{missionId:00}");
        var set = (setting ?? "").Trim().ToLowerInvariant();
        if (set != "" && !set.StartsWith("cave") && set != "testset") Add($"loadbg_mp_{set}");
        Add("loadbg_static");
        Add("loadbg_mp_jungle");
        var res = new List<string>();
        foreach (var n in names) { res.Add(MenuArt + n + ".jpg"); res.Add(MenuArt + n + ".png"); }
        return res;
    }

    /// <summary>a picture named by the original's tables ('menue/decoration/loadbg_single01.jpg', LoadingScreens.txt)
    /// as the converted menu art (.jpg, then .png); none for an empty name</summary>
    public static List<string> ArtOf(string? image)
    {
        var res = new List<string>();
        var n = Path.GetFileNameWithoutExtension((image ?? "").Replace('\\', '/').Trim());
        if (n == "") return res;
        res.Add(MenuArt + n + ".jpg");
        res.Add(MenuArt + n + ".png");
        return res;
    }

    /// <summary>a text of the mission as it may be shown: "" for an add-on's "!x.ltf load failed!" placeholder</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || Placeholder.IsMatch(text)) return "";
        return text.Trim();
    }

    /// <summary>the title to show: the mission's title, else its name, else "Mission n"</summary>
    public static string Title(string? title, string? name, int id)
    {
        var t = Clean(title);
        if (t == "") t = Clean(name);
        return t != "" ? t : $"Mission {id}";
    }

    /// <summary>the hints of the original help book (help.json docs["h_tipps.seml"], without its introduction)</summary>
    public static List<LoadingHint> Hints(JsonNode? help)
    {
        var res = new List<LoadingHint>();
        if (help?["docs"]?["h_tipps.seml"] is not JsonArray items) return res;
        foreach (var it in items)
        {
            if (it is not JsonObject o || (string?)o["t"] != "item") continue;
            var key = (string?)o["key"] ?? "";
            if (key == "_tipps_general") continue;                   // "The Tips and Tricks section is ..." (not a hint)
            var text = HintText((string?)o["long"] ?? (string?)o["medium"] ?? "");
            if (text == "") continue;
            res.Add(new LoadingHint(((string?)o["title"] ?? "").Trim(), text));
        }
        return res;
    }

    /// <summary>a help text as one short paragraph: the links as plain text; whole sentences from its start (across
    /// its paragraphs: some begin with a single short sentence) while they fit in maxChars, at least the first one</summary>
    public static string HintText(string text, int maxChars = 330)
    {
        text = Ref.Replace(text ?? "", "$1");
        text = AnyTag.Replace(text, "");
        var sentences = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            var p = Regex.Replace(para, @"\s+", " ").Replace(" .", ".").Replace(" ,", ",").Trim();
            if (p == "") continue;
            // (a list or heading line without an end mark gets none: it is not run into the next sentence)
            if (!Regex.IsMatch(p, @"[.!?:]$")) { if (sentences.Count > 0) break; p += "."; }
            sentences.AddRange(Regex.Split(p, @"(?<=[.!?])\s+").Where(x => x != ""));
        }
        var sb = new System.Text.StringBuilder();
        foreach (var s in sentences)
        {
            if (sb.Length > 0 && sb.Length + 1 + s.Length > maxChars) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }
        var res = sb.ToString();
        // a first sentence longer than maxChars: cut at a word
        if (res.Length > maxChars) res = res[..res.LastIndexOf(' ', maxChars - 2)].TrimEnd(',', ';', ' ') + "…";
        return res;
    }

    /// <summary>one of the hints, chosen by seed (null: none)</summary>
    public static LoadingHint? Pick(IReadOnlyList<LoadingHint> hints, int seed)
    {
        if (hints.Count == 0) return null;
        return hints[(int)((uint)seed % (uint)hints.Count)];
    }
}
