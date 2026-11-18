// Do the models have the links the classes hang their parts on? gamedata.json "composites" (the scripts' AddObj /
// AddObjCustomized ...: riders, turrets, build-ups, flags, cranes) name a link of the class's model (or of the part it
// hangs on); a link the model lacks was not drawn at all. Every composite of both configurations is checked against
// the converted models (ModelFileInfo: the "link_<name>" nodes); the models a data folder lacks are skipped (the
// container's MIRAGE data has almost none - PW_MODELS_EXTRA=<folder>[;<folder>] adds folders of .glb files, searched
// after the configuration's own).
//
// What the check found (2026-11-02, with the user's converted models of both configurations): MIRAGE's gallimimus
// nest (aje_gallimimus_nest, model ninigi_dilophosaurus_nest) hangs its eggs nest_birds at "Nest", which the model
// does not have - the view now hangs such a part at a static model's origin (ModelInstance.LinkOrOrigin, docs/VIEW.md);
// MIRAGE's level flags at "flag" on models without that link (animals, then with more MIRAGE models staged
// 2026-11-11 also zeppelins, SEAS lumberjacks, mobile suits, hovercraft ... - checked as a class) and two odd MIRAGE
// classes stay undrawn; the
// Norse tower's ballista hangs at "we" of the upgraded tower's model (hu_large_tower_upgrade), which has it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.Tests;

[TestClass]
public static class CompositeLinksTests
{
    /// <summary>class / model / link of the composites whose model lacks the link - known and handled</summary>
    static readonly HashSet<string> Known = new()
    {
        "aje_gallimimus_nest/ninigi_dilophosaurus_nest/Nest",          // drawn at the nest's origin (LinkOrOrigin)
        "hu_large_tower/hu_large_tower/we",                            // the ballista: on hu_large_tower_upgrade's "we"
        // (MIRAGE's level flags at "flag" are known as a class: LevelFlagWithoutLink)
        // MIRAGE classes whose class graphics point at another model (a rider class on the titan, a pilot on the floating harbour)
        "knight_lvl4/titan_triceratops/Ride", "pilot_lvl4/aje_floating_harbour/psh2",
    };

    /// <summary>MIRAGE's level flags (a part "by": "level", the &lt;tribe&gt;_animal_flag_0N models) hang at "flag"; on a model
    /// without that link (animals, SEAS vehicles and walkers, zeppelins, wreckages ...) the flag is not drawn - an
    /// animal's or vehicle's origin is the ground under it, no place for a flag (docs/VIEW.md). Known as a class, not
    /// model by model: every MIRAGE model added to the data showed more of them (2026-11-11: the zeppelins, the SEAS
    /// lumberjacks, mobile suits, hovercraft, carnotaurus and their wreckages). A missing link of any other kind still
    /// fails.</summary>
    static bool LevelFlagWithoutLink(JsonNode part) => part["link"].Str() == "flag" && part["by"].Str() == "level";

    public static void BaseCompositeLinks(TestContext t) => Check(t, t.Data("PW_DATA"), null);
    public static void MirageCompositeLinks(TestContext t) => Check(t, t.Data("PW_DATA_MIRAGE"), Environment.GetEnvironmentVariable("PW_DATA"));

    /// <summary>(class, model, link) of every composite part whose model is there but lacks the link</summary>
    public static List<string> MissingLinks(string data, IEnumerable<string> modelDirs, out int checkedParts, out int noModel)
        => MissingLinks(data, modelDirs, out checkedParts, out noModel, out _);

    /// <summary>... and, apart, those of the level flags (LevelFlagWithoutLink)</summary>
    public static List<string> MissingLinks(string data, IEnumerable<string> modelDirs, out int checkedParts, out int noModel, out List<string> levelFlags)
    {
        var gd = Json.ParseFile(Path.Combine(data, "gamedata.json"))!;
        var gfx = gd["classgfx"] as JsonObject;
        var cache = new Dictionary<string, HashSet<string>?>();
        HashSet<string>? LinksOf(string model)
        {
            if (cache.TryGetValue(model, out var l)) return l;
            foreach (var d in modelDirs)
            {
                var f = Path.Combine(d, model + ".glb");
                if (File.Exists(f)) return cache[model] = ModelFileInfo.ReadGlb(f).Links.Keys.ToHashSet();
            }
            return cache[model] = null;
        }
        var res = new List<string>();
        var flags = new List<string>();
        checkedParts = noModel = 0;
        foreach (var (cls, parts) in gd["composites"] as JsonObject ?? new JsonObject())
        {
            if (parts is not JsonArray arr) continue;
            string host = (gfx?[cls]?.Str() is { Length: > 0 } g ? g : cls).ToLowerInvariant();
            foreach (var p in arr)
            {
                var link = p?["link"].Str() ?? "";
                if (link.Length == 0) continue;
                int pi = p!["pi"].Int(-1);
                string? model = pi < 0 ? host : arr.FirstOrDefault(q => q?["i"].Int(-2) == pi)?["gfx"].Str().ToLowerInvariant();
                if (string.IsNullOrEmpty(model)) continue;
                var links = LinksOf(model);
                if (links == null) { noModel++; continue; }
                checkedParts++;
                if (!links.Contains(link)) (LevelFlagWithoutLink(p!) ? flags : res).Add($"{cls}/{model}/{link}");
            }
        }
        levelFlags = flags.Distinct().ToList();
        return res.Distinct().ToList();
    }

    static void Check(TestContext t, string data, string? baseData)
    {
        var dirs = new List<string> { Path.Combine(data, "assets", "models") };
        if (baseData != null) dirs.Add(Path.Combine(baseData, "assets", "models"));
        foreach (var x in (Environment.GetEnvironmentVariable("PW_MODELS_EXTRA") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) dirs.Add(x);
        var missing = MissingLinks(data, dirs.Where(Directory.Exists), out int n, out int none, out var flags);
        t.Log($"{n} composite parts checked ({none} without their model here); links missing: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");
        if (flags.Count > 0) t.Log($"level flags on models without a \"flag\" link (not drawn, known): {flags.Count}");
        t.True(n > 0, "composites were checked");
        var unknown = missing.Where(m => !Known.Contains(m)).ToList();
        t.True(unknown.Count == 0, "a composite whose model lacks its link (not known yet): " + string.Join(", ", unknown));
    }
}
