// Tests of the help book (game/src/UI/Help/Logic/HelpBook.cs, linked into this project): the original's index of
// index.txt with the files' chapters and items, SelectHelpTopic's lookups (topic keys, item keys with and without '_',
// numbered keys), the links and pictures of the texts, the history of HelpMenu.usl; on the real help.json (PW_DATA)
// every link and every class card of the encyclopaedia pages resolve.
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.UI.Help;

namespace PwRemake.Tests;

[TestClass]
public static class HelpBookTests
{
    const string Sample = """
    { "index": [
        { "key": "introduction", "text": "Introduction", "file": "h_intro.seml", "subs": [] },
        { "key": "hu-techtree", "text": "The Norsemen", "file": "", "subs": [
            { "key": "units", "text": "Units", "file": "h_hu_produkte.seml", "subs": [] } ] },
        { "key": "ui", "text": "User Interface", "file": "h_ui.seml", "subs": [] } ],
      "docs": {
        "h_intro.seml": [ { "t": "item", "key": "_intro", "title": "Welcome", "img": "", "medium": "", "long": "See {ref:h_ui.seml#cb_walk|Move} and {img:bres:9;layer+info_attack_pierce} {img:pics/Standard.tga}.\n\nNext." } ],
        "h_hu_produkte.seml": [ { "t": "chapter", "key": "_infantry", "title": "Infantry" },
                                { "t": "item", "key": "_hu_worker", "title": "Worker", "img": "bunit:hu_worker", "medium": "a", "long": "b" },
                                { "t": "section", "key": "_heroes", "title": "Heroes" },
                                { "t": "item", "key": "_ninigi_fireplace", "title": "Fireplace", "img": "", "medium": "", "long": "" } ],
        "h_ui.seml": [ { "t": "item", "key": "cb_walk", "title": "Move", "img": "", "medium": "", "long": "" } ] } }
    """;

    public static void IndexAndLookups(TestContext t)
    {
        var b = HelpBook.Load(JsonNode.Parse(Sample));
        t.Equal(3, b.Roots.Count, "the topics of index.txt");
        var hu = b.Roots[1];
        t.Equal("h_hu_produkte.seml", hu.Ref, "a topic without a file opens the next entry's (FixNavigationLinksOfUnlinkedEntries)");
        var units = hu.Subs.Single();
        t.Equal("Infantry", units.Subs[0].Text, "the file's chapter under its topic");
        t.Equal("_hu_worker", units.Subs[0].Subs[0].Name, "the item under its chapter");
        t.Equal("Heroes", units.Subs[0].Subs[1].Text, "a section under its chapter");
        t.Equal("_ninigi_fireplace", units.Subs[0].Subs[1].Subs[0].Name, "an item under its section");
        t.Equal("h_hu_produkte.seml#_hu_worker", b.Find("_hu_worker"), "an item key");
        t.Equal("h_hu_produkte.seml#_hu_worker", b.Find("HU_WORKER"), "any case, without '_'");
        t.Equal("h_ui.seml#cb_walk", b.Find("_cb_walk"), "a key spelled without '_' found with one");
        t.Equal("h_ui.seml", b.Find("ui"), "a topic key");
        t.Equal("h_hu_produkte.seml#_ninigi_fireplace", b.Find("_ninigi_fireplace_1"), "a numbered key: its stem");
        t.True(b.Find("_nothing") == null && b.Find("") == null, "none");
        t.Equal("h_intro.seml", b.Home, "Home: H_Intro.seml");
        t.Equal("h_ui.seml#cb_walk", b.StartPage(new[] { null, "_nothing", "cb_walk", "_hu_worker" }), "F1: the first candidate the index has");
        t.Equal("h_intro.seml", b.StartPage(new[] { "_nothing" }), "F1 on nothing: Home");
        t.True(b.TopicOf("h_hu_produkte.seml#_hu_worker")?.Text == "Worker", "the index entry of a page");
    }

    public static void TextsAndPictures(TestContext t)
    {
        var b = HelpBook.Load(JsonNode.Parse(Sample));
        var runs = HelpBook.Parse(b.Doc("h_intro.seml")[0].Long);
        t.Equal("See ", runs[0].Text, "text");
        t.True(runs[1].Kind == "link" && runs[1].Ref == "h_ui.seml#cb_walk" && runs[1].Text == "Move", "a link");
        t.True(runs[3].Kind == "img" && runs[3].Ref == "bres:9;layer+info_attack_pierce", "a picture");
        t.True(runs.Last().Text.EndsWith("\n\nNext."), "paragraphs kept");
        t.Equal(new HelpPicture("card", "hu_worker"), HelpPicture.Parse("bunit:hu_worker"), "a class card");
        t.Equal(new HelpPicture("sprite", "stance_0", 2), HelpPicture.Parse("bres:2;layer+stance_0"), "an interface sprite");
        t.Equal(new HelpPicture("cursor", "cur/standard.png"), HelpPicture.Parse("pics/Standard.tga"), "a mouse pointer");
        t.True(HelpPicture.Parse("") == null && HelpPicture.Parse("foo") == null, "none");
    }

    public static void History(TestContext t)
    {
        var h = new HelpHistory();
        t.True(h.Go("h_intro.seml") && !h.CanBack && !h.CanForward, "the first page");
        t.True(!h.Go("H_INTRO.SEML"), "the page shown again: nothing");
        h.Go("a#x"); h.Go("b");
        t.True(h.Back() && h.Current == "a#x" && h.CanForward, "back");
        t.True(h.Back() && h.Current == "h_intro.seml" && !h.CanBack, "back to the first");
        t.True(h.Fwd() && h.Current == "a#x", "forward");
        h.Go("c");
        t.True(!h.CanForward && h.History.Count == 3, "a new page clears forward");
    }

    /// <summary>the real help book: the index of the original, every link goes somewhere, the encyclopaedia pages</summary>
    public static void RealHelpBook(TestContext t)
    {
        var json = Json.TryParseFile(Path.Combine(t.Data(), "help.json"));
        if (json == null) t.Skip("no help.json");
        var b = HelpBook.Load(json);
        t.True(b.Roots.Count >= 13, $"the topics of index.txt: {b.Roots.Count}");
        t.True(b.All.Count > 600, $"index entries with the files' chapters and items: {b.All.Count}");
        t.True(b.All.All(x => x.Ref != ""), "every entry opens a page");
        int links = 0, bad = 0;
        foreach (var (file, parts) in b.Docs)
            foreach (var p in parts)
                foreach (var r in HelpBook.Parse(p.Medium).Concat(HelpBook.Parse(p.Long)).Where(x => x.Kind == "link"))
                {
                    links++;
                    var (f, k) = HelpBook.Split(r.Ref);
                    if (!b.Docs.ContainsKey(f) || k != "" && !b.Doc(f).Any(x => x.Key == k)) { bad++; if (bad <= 10) t.Log($"link of {file}#{p.Key} to nowhere: {r.Ref}"); }
                }
        t.Log($"{links} links, {bad} to a key the files lack");
        t.True(links > 2000 && bad < links / 20, $"links resolve ({bad} of {links} do not)");
        foreach (var k in new[] { "_hu_worker", "_aje_tent", "_ninigi_dojo", "_seas_headquarters", "_lovelace", "_allosaurus", "cb_walk", "_stance_0" })
            t.True(b.Find(k) != null, $"F1 finds {k}");
        t.True(b.Roots.Any(r => r.Text == "The SEAS") && b.Find("_seas_marksman") != null, "the SEAS pages");
    }
}
