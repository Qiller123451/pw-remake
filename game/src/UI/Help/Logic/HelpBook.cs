// The in-game help book (the original's HelpMenu.usl, F1) without Godot types (compiled into the tests too): the
// help of the game's own texts - Texts/Help/index.txt and its *.seml files, converted by the toolkit to help.json
// (remake/pipeline/build_data.py help_book):
//   { index: [{key, text, file, subs}], docs: {file: [{t: 'chapter'|'section', key, title} |
//                                                    {t: 'item', key, title, img, medium, long}]} }
// texts in the markup of pwexport.texts._rich: '{ref:<file#key>|<label>}', '{img:<spec>}', newlines.
//
// What the original does and this follows:
//   * the index (BuildIndex / BuildIndexRec / BuildIndexRecFile): every topic of index.txt, under it the topic tree of
//     its file (chapters, sections, help items - "-nodisplay" items are not in help.json), then its sub-topics; a
//     topic without a file links to the next entry that has one (FixNavigationLinksOfUnlinkedEntries);
//   * SelectHelpTopic(name): the entry whose name (a topic key or a help item key, lower case) is the name;
//   * the history (Navigate / NavigateBack / NavigateForward): Navigate adds a page unless it is the current one and
//     clears the forward list; Back is possible with two pages or more;
//   * Home: H_Intro.seml;
//   * F1 (StartHelpNav): the help item of the hovered command button, of the building being placed (its tech-tree
//     description), of the first selected object (GetObjHelpitemKey: its description, else its class), of the hovered
//     object - the first that the index has; else Home.
// The browser remake's ui/helpbook.js showed the index topics only and looked an item up with a '_' put in front
// (the command bar's keys cb_* / mm_* have none); here the index is the original's and keys match with or without it.
//
// Ported from remake/src/ui/helpbook.js (pw-exporter 98c7703) and Game/UI/HelpMenu.usl.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;

namespace PwRemake.UI.Help;

/// <summary>a part of a help file: a chapter or section heading, or a help item</summary>
public sealed class HelpPart
{
    /// <summary>"chapter" | "section" | "item"</summary>
    public string Kind = "item";
    /// <summary>lower case, as spelled ("_aje_tent", "cb_walk")</summary>
    public string Key = "";
    public string Title = "";
    /// <summary>the picture at the item's right ("bunit:aje_tent", "bres:2;layer+stance_0"), "" none</summary>
    public string Img = "";
    public string Medium = "", Long = "";
}

/// <summary>an entry of the index tree</summary>
public sealed class HelpTopic
{
    /// <summary>the name SelectHelpTopic compares with (a topic key or a help item key, lower case)</summary>
    public string Name = "";
    public string Text = "";
    /// <summary>"file" or "file#key" (lower case); "" none</summary>
    public string Ref = "";
    public int Depth;
    /// <summary>"topic" (index.txt) | "chapter" | "section" | "item"</summary>
    public string Kind = "topic";
    public HelpTopic? Parent;
    public readonly List<HelpTopic> Subs = new();
}

/// <summary>a piece of a help text: plain text, a link or a picture</summary>
public readonly record struct HelpRun(string Kind, string Text, string Ref = "")
{
    public static HelpRun Plain(string t) => new("text", t);
    public static HelpRun Link(string r, string label) => new("link", label, r);
    public static HelpRun Image(string spec) => new("img", "", spec);
}

/// <summary>what a picture spec of the help shows</summary>
public readonly record struct HelpPicture(string Kind, string Name, int? Level = null)
{
    /// <summary>bunit:&lt;class&gt; - the class's card; bres:&lt;level&gt;;layer+&lt;sprite&gt; - an interface sprite of
    /// the atlas; pics/&lt;x&gt;.tga - a mouse pointer (Name: cur/&lt;x&gt;.png below assets/ui/)</summary>
    public static HelpPicture? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var m = Regex.Match(spec, @"^bunit:(\S+)");
        if (m.Success) return new HelpPicture("card", m.Groups[1].Value);
        m = Regex.Match(spec, @"^bres:(\d+);layer\+(\S+)");
        if (m.Success) return new HelpPicture("sprite", m.Groups[2].Value, int.Parse(m.Groups[1].Value));
        m = Regex.Match(spec, @"^pics/(\w+)\.tga$", RegexOptions.IgnoreCase);
        if (m.Success) return new HelpPicture("cursor", "cur/" + m.Groups[1].Value.ToLowerInvariant() + ".png");
        return null;
    }
}

public sealed class HelpBook
{
    public const string HomeFile = "h_intro.seml";

    public readonly List<HelpTopic> Roots = new();
    /// <summary>every index entry in the original's order (m_axIndexItemData)</summary>
    public readonly List<HelpTopic> All = new();
    public readonly Dictionary<string, List<HelpPart>> Docs = new(StringComparer.OrdinalIgnoreCase);

    public bool Empty => Docs.Count == 0;

    public static HelpBook Load(JsonNode? help)
    {
        var b = new HelpBook();
        if (help?["docs"] is JsonObject docs)
            foreach (var (file, items) in docs)
            {
                var list = new List<HelpPart>();
                if (items is JsonArray a)
                    foreach (var it in a)
                        if (it is JsonObject o)
                            list.Add(new HelpPart
                            {
                                Kind = o["t"].Str() ?? "item", Key = (o["key"].Str() ?? "").ToLowerInvariant(), Title = (o["title"].Str() ?? "").Trim(),
                                Img = o["img"].Str() ?? "", Medium = o["medium"].Str() ?? "", Long = o["long"].Str() ?? "",
                            });
                b.Docs[file.ToLowerInvariant()] = list;
            }
        if (help?["index"] is JsonArray idx) b.BuildIndex(idx, null, 0);
        // FixNavigationLinksOfUnlinkedEntries: an entry without a file opens the next one that has one
        for (int i = 0; i < b.All.Count; i++)
            if (b.All[i].Ref == "")
                for (int j = i; j < b.All.Count; j++)
                    if (b.All[j].Ref != "") { b.All[i].Ref = b.All[j].Ref; break; }
        return b;
    }

    void BuildIndex(JsonArray list, HelpTopic? parent, int depth)
    {
        foreach (var n in list)
        {
            if (n is not JsonObject o) continue;
            var file = (o["file"].Str() ?? "").ToLowerInvariant();
            var t = new HelpTopic
            {
                Name = (o["key"].Str() ?? "").ToLowerInvariant(), Text = o["text"].Str() ?? "", Ref = file, Depth = depth, Parent = parent,
            };
            Add(t, parent);
            // the file's topic tree: chapters, their sections, the items under the last heading
            if (file != "" && Docs.TryGetValue(file, out var parts))
            {
                HelpTopic? chapter = null, section = null;
                foreach (var p in parts)
                {
                    var under = p.Kind switch { "chapter" => t, "section" => chapter ?? t, _ => section ?? chapter ?? t };
                    var e = new HelpTopic
                    {
                        Name = p.Key, Text = p.Title != "" ? p.Title : p.Key, Ref = p.Key != "" ? $"{file}#{p.Key}" : file,
                        Depth = under.Depth + 1, Kind = p.Kind, Parent = under,
                    };
                    Add(e, under);
                    if (p.Kind == "chapter") { chapter = e; section = null; }
                    else if (p.Kind == "section") section = e;
                }
            }
            if (o["subs"] is JsonArray subs) BuildIndex(subs, t, depth + 1);
        }
    }

    void Add(HelpTopic t, HelpTopic? parent)
    {
        All.Add(t);
        if (parent == null) Roots.Add(t); else parent.Subs.Add(t);
    }

    /// <summary>Home (NavigateHome: H_Intro.seml; else the first topic)</summary>
    public string Home => Docs.ContainsKey(HomeFile) ? HomeFile : All.FirstOrDefault(x => x.Ref != "")?.Ref ?? "";

    /// <summary>SelectHelpTopic: the page of an index entry named so (a topic or help item key, any case, with or
    /// without the leading '_'); a heroes' or upgrades' numbered key ("_ninigi_fireplace_1", "lovelace_s0") falls back
    /// to its stem (the original redirects such "-nodisplay" items to the main one). Null: none.</summary>
    public string? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().ToLowerInvariant();
        foreach (var cand in Candidates(n))
        {
            var t = All.FirstOrDefault(x => x.Name == cand && x.Ref != "");
            if (t != null) return t.Ref;
        }
        return null;
    }

    static IEnumerable<string> Candidates(string n)
    {
        var bare = n.TrimStart('_');
        yield return n;
        yield return bare;
        yield return "_" + bare;
        var stem = Regex.Replace(bare, @"(_s\d|_\d)$", "");
        if (stem != bare) { yield return "_" + stem; yield return stem; }
    }

    /// <summary>the index entry of a page (TreeSelect), null when the page has none</summary>
    public HelpTopic? TopicOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var r = reference.ToLowerInvariant();
        return All.FirstOrDefault(x => x.Ref == r) ?? All.FirstOrDefault(x => x.Ref == r.Split('#')[0]);
    }

    /// <summary>a page: (file, key) of "file#key"</summary>
    public static (string File, string Key) Split(string? reference)
    {
        var r = (reference ?? "").ToLowerInvariant();
        int i = r.IndexOf('#');
        return i < 0 ? (r, "") : (r[..i], r[(i + 1)..]);
    }

    public IReadOnlyList<HelpPart> Doc(string file) => Docs.TryGetValue(file, out var d) ? d : Array.Empty<HelpPart>();

    /// <summary>the first of the candidates (StartHelpNav's order) that the index has; else Home</summary>
    public string StartPage(IEnumerable<string?> candidates)
    {
        foreach (var c in candidates) { var r = Find(c); if (r != null) return r; }
        return Home;
    }

    static readonly Regex Markup = new(@"\{ref:([^|}]*)\|([^}]*)\}|\{img:([^}]*)\}");

    /// <summary>a help text as runs: text, links ("file#key", label), pictures</summary>
    public static List<HelpRun> Parse(string? text)
    {
        var res = new List<HelpRun>();
        text ??= "";
        int i = 0;
        foreach (Match m in Markup.Matches(text))
        {
            if (m.Index > i) res.Add(HelpRun.Plain(text[i..m.Index]));
            res.Add(m.Groups[3].Success ? HelpRun.Image(m.Groups[3].Value) : HelpRun.Link(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value));
            i = m.Index + m.Length;
        }
        if (i < text.Length) res.Add(HelpRun.Plain(text[i..]));
        return res;
    }
}

/// <summary>the pages visited (HelpMenu m_asHistory / m_asHistoryRev): the last of History is the current page</summary>
public sealed class HelpHistory
{
    public readonly List<string> History = new();
    public readonly List<string> Forward = new();

    public string? Current => History.Count > 0 ? History[^1] : null;
    public bool CanBack => History.Count > 1;
    public bool CanForward => Forward.Count > 0;

    /// <summary>Navigate: false when it is the page shown</summary>
    public bool Go(string page)
    {
        page = page.ToLowerInvariant();
        if (Current == page) return false;
        History.Add(page);
        Forward.Clear();
        return true;
    }

    public bool Back()
    {
        if (!CanBack) return false;
        Forward.Add(History[^1]);
        History.RemoveAt(History.Count - 1);
        return true;
    }

    public bool Fwd()
    {
        if (!CanForward) return false;
        History.Add(Forward[^1]);
        Forward.RemoveAt(Forward.Count - 1);
        return true;
    }

    public void Clear() { History.Clear(); Forward.Clear(); }
}
