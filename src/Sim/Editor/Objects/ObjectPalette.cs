// The map editor's object palette (docs/MAP_EDITOR.md "Objects", plan phase 3): every class a map can hold, from the
// configuration's own data - so a mod's classes (MIRAGE's) and the classes a configuration adds come with it:
//   * the tech tree's classes (StartTT Objects/<tribe>/<type>: Rules.Index): the tribes' buildings, units, vehicles,
//     ships and animals, the wild animals (tribe World), the nests;
//   * the scenery classes of the class files (gamedata "script": the class each object class derives from) that the
//     map format knows: trees (CTree), stones (CStone), fruit bushes (CFruit_Food), fish shoals (CFishShoal), healing
//     wells (CFountain), decoration (CDecoObj, CDecoColObj, CDecoColAnimObj, CDecoBuilding, CHC_Wall, CVegetation),
//     decoration characters (CDecoChar), effects (CFX), items (CItem), item spawns, domination flags, start locations.
// Each entry says how the map stores it: the object's type tag (the original's Objs chunk: BLDG, CHTR, VHCL, SHIP, ANML,
// NEST, TREE, STON, FRUI, FNTN, DCCO, DECO, VGTN, CFXE, ITEM, ITSP, DOFL, SLOC - MapSource turns them into the world's
// buildings, units, resources and scenery exactly as an original map's). Lakes of healing / damage and question marks
// need regions (phase 5). No Godot types.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using SimRules = PwRemake.Sim.Rules.Rules;

namespace PwRemake.Sim.Editor;

/// <summary>a placeable class of the palette</summary>
public sealed class PaletteEntry
{
    /// <summary>the class as the map stores it (the tech tree's spelling; scenery: the class file's, lower case)</summary>
    public string Cls = "";
    /// <summary>the map's object type tag (BLDG, CHTR, TREE, DCCO ...)</summary>
    public string Type = "";
    /// <summary>ObjectPalette.CATEGORIES</summary>
    public string Category = "";
    /// <summary>within the category: the tribe (Hu, Aje, Ninigi, SEAS, Special, World), or the kind of scenery</summary>
    public string Group = "";
    /// <summary>the name shown (the game's text of the class, else the class made readable)</summary>
    public string Name = "";
    /// <summary>the model of its first level (null: the data has none - placed, shown as a marker)</summary>
    public string? Model;
    /// <summary>a player's by default (the tribes' classes); else nobody's</summary>
    public bool Owned;
    /// <summary>a class the MIRAGE mod adds (not in the base game)</summary>
    public bool Mirage;
    /// <summary>the levels the class has (1-based; 0 = none: scenery)</summary>
    public int MinLevel, MaxLevel;
    /// <summary>a resource with an amount (stones, fruit bushes, fish: the attribute "hitpoints" as the original) and its default</summary>
    public double Amount;
    /// <summary>has hit points (the properties panel's hit points %)</summary>
    public bool Living;
    public override string ToString() => $"{Category}/{Group}/{Cls} ({Type})";
}

public static class ObjectPalette
{
    static readonly Logger L = Log.For("Editor");

    /// <summary>the palette's categories, in order ("MIRAGE": the mod's classes, from every category)</summary>
    public static readonly string[] CATEGORIES = { "Buildings", "Units", "Tribe animals", "Wild animals", "Nests", "Resources", "Healing wells", "Decoration", "Items", "Markers" };
    public const string MIRAGE = "MIRAGE", ALL = "All";

    // scenery: the class file's class -> (type tag, category, group)
    static readonly Dictionary<string, (string Type, string Cat, string Group)> SCENERY = new(StringComparer.Ordinal)
    {
        ["CTree"] = ("TREE", "Resources", "Trees"),
        ["CStone"] = ("STON", "Resources", "Stones"),
        ["CFruit_Food"] = ("FRUI", "Resources", "Fruit bushes"),
        ["CFishShoal"] = ("FRUI", "Resources", "Fish"),
        ["CFountain"] = ("FNTN", "Healing wells", "Wells"),
        ["CDecoObj"] = ("DECO", "Decoration", ""),
        ["CDecoColObj"] = ("DCCO", "Decoration", ""),
        ["CDecoColAnimObj"] = ("DCCO", "Decoration", ""),
        ["CDecoBuilding"] = ("DCCO", "Decoration", ""),
        ["CHC_Wall"] = ("DCCO", "Decoration", ""),
        ["CVegetation"] = ("VGTN", "Decoration", "Plants"),
        ["CDecoChar"] = ("CHTR", "Decoration", "Characters"),
        ["CFX"] = ("CFXE", "Decoration", "Effects"),
        ["CItem"] = ("ITEM", "Items", "Items"),
        ["CItemSpawn"] = ("ITSP", "Items", "Item spawns"),
        ["CDominationFlag"] = ("DOFL", "Markers", "Domination flags"),
        ["CStartLocation"] = ("SLOC", "Markers", "Start locations"),
    };

    // the kinds of decoration, by name (in this order)
    static readonly (string Group, Regex Re)[] DECO_GROUPS =
    {
        ("Plants", new Regex("bush|plant|fern|flower|grass|reed|mushroom|palm|tree|bamboo|underwood|vegetation|leaf|leaves|moss|cactus|liana|root|shrub|ivy|wildrice|seaweed|coral|algae", RegexOptions.CultureInvariant)),
        ("Water", new Regex("river|waterfall|lake|water|bridge|shore|beach|swamp|lava", RegexOptions.CultureInvariant)),
        ("Rocks and cliffs", new Regex("rock|cliff|plateau|mountain|stone|basalt|berg|crater|boulder|canyon|hill|cave|ice_|glacier|volcano|geyser", RegexOptions.CultureInvariant)),
        ("Ruins and buildings", new Regex("ruin|temple|wall|pillar|statue|house|hut|tent|tower|gate|monument|arch|colloseum|city|fence|grave|tomb|camp|ship|wreck|skull|bone|totem|altar|well|pyramid|obelisk|throne|door|stair|column|building|valhalla|hc_|pt_|dh_", RegexOptions.CultureInvariant)),
    };

    /// <summary>the species a nest class spawns, most frequent first: what the original's maps set as spawn_type
    /// (27 maps of Base, BoosterPack and MIRAGE); the first the rules know is the default of a placed nest</summary>
    public static readonly Dictionary<string, string[]> NEST_SPECIES = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nest_big_carnivores"] = new[] { "Baryonyx", "Allosaurus", "Spinosaurus", "Carcharodontosaurus", "Carnotaurus", "Atroxosaurus", "Tarbosaurus", "Gigantosaurus", "Ceratosaurus" },
        ["nest_hadrosaurs"] = new[] { "Iguanodon", "Parasaurolophus", "Maiasauria", "Corythosaurus" },
        ["nest_small_carnivores"] = new[] { "Velociraptor", "Dilophosaurus", "Gallimimus", "Deinonychus", "Oviraptor", "Psittacosaurus" },
        ["nest_saurpods"] = new[] { "Saltasaurus", "Diplodocus", "Brachiosaurus", "Apatosaurus" },
        ["nest_mammal_cave"] = new[] { "Megaloceros", "Woolly_Rhino", "Mammoth", "Smilodon", "Eusmilus", "Wild_Boar" },
        ["nest_armoured_dinosaur"] = new[] { "Polakanthus", "Ankylosaurus", "Panoplosaurus", "Triceratops", "Styracosaurus", "Stygimoloch", "Stegosaurus", "Kentrosaurus", "Pentaceratops", "Achelousaurus" },
        ["nest_ice_mammal_cave"] = new[] { "Smilodon", "Mammoth", "Megaloceros", "Eusmilus", "Wild_Boar", "Woolly_Rhino" },
        ["nest_swimmer"] = new[] { "Mosasaurus", "Kronosaurus", "Muraenosaurus", "Liopleurodon", "Macrolemys" },
        ["nest_hc_cititzen"] = new[] { "hc_citizen_01_animal" },
        ["hu_kennel_nest"] = new[] { "hu_kennel_smilodon" },
    };

    /// <summary>the species a placed nest of that class spawns by default (null: the rules know none of the list)</summary>
    public static string? DefaultNestSpecies(SimRules R, string nestCls)
    {
        if (NEST_SPECIES.TryGetValue(nestCls, out var l)) foreach (var s in l) if (R.Exists(s)) return s;
        return null;
    }

    /// <summary>the wild animals a nest can spawn (the World tribe's animals)</summary>
    public static List<string> WildSpecies(SimRules R) =>
        R.IndexOrder.Where(n => R.Index[n].Tribe == "World" && R.Index[n].Type == "ANML").ToList();

    /// <summary>the palette of a configuration. hasModel: the asset manifest has a model of that name (scenery without one
    /// is left out); baseClasses: the base game's class names (lower case; null: no MIRAGE marks)</summary>
    public static List<PaletteEntry> Build(SimRules R, Func<string, bool> hasModel, ISet<string>? baseClasses = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outp = new List<PaletteEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Mirage(string c) => baseClasses != null && !baseClasses.Contains(c.ToLowerInvariant());
        // the tech tree's classes
        foreach (var name in R.IndexOrder)
        {
            var ix = R.Index[name];
            string cat = ix.Type switch
            {
                "BLDG" => "Buildings", "NEST" => "Nests",
                "ANML" => ix.Tribe == "World" ? "Wild animals" : "Tribe animals",
                _ => "Units",
            };
            int min = 0, max = 0;
            string? gfx = null;
            for (int l = 1; l <= 5; l++)
            {
                var s = R.Stats(name, l, null);
                if (s == null || !s.Exists) continue;
                if (min == 0) min = l;
                max = l;
                gfx ??= s.Gfx;
            }
            if (min == 0) { min = max = 1; }
            gfx = (gfx ?? R.ClassGfx(name))?.ToLowerInvariant();
            seen.Add(name);
            outp.Add(new PaletteEntry
            {
                Cls = name, Type = ix.Type, Category = cat, Group = ix.Tribe, Name = Display(R, name), Model = gfx != null && hasModel(gfx) ? gfx : null,
                Owned = ix.Tribe != "World", Mirage = Mirage(name), MinLevel = ix.Type == "NEST" ? 0 : min, MaxLevel = ix.Type == "NEST" ? 0 : max,
                Living = ix.Type != "NEST",
            });
        }
        // the scenery of the class files
        foreach (var (cls, script) in R.Scripts.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!SCENERY.TryGetValue(script, out var sc) || seen.Contains(cls)) continue;
            string model = (R.ClassGfx(cls) ?? cls).ToLowerInvariant();
            bool marker = sc.Type is "SLOC" or "ITSP" or "DOFL" or "ITEM";
            if (!hasModel(model) && !marker) continue;
            string group = sc.Group.Length > 0 ? sc.Group : DecoGroup(cls);
            double amount = script switch { "CStone" => 2000, "CFruit_Food" => 150, "CFishShoal" => 1000, _ => 0 };
            outp.Add(new PaletteEntry
            {
                Cls = cls, Type = sc.Type, Category = sc.Cat, Group = group, Name = SimRules.Prettify(cls), Model = hasModel(model) ? model : null,
                Mirage = Mirage(cls), Amount = amount, Living = sc.Type == "FNTN",
            });
            seen.Add(cls);
        }
        L.Info($"object palette: {outp.Count} classes ({string.Join(", ", CATEGORIES.Select(c => $"{c} {outp.Count(e => e.Category == c)}"))}; MIRAGE {outp.Count(e => e.Mirage)}) - {sw.ElapsedMilliseconds} ms");
        return outp;
    }

    static string DecoGroup(string cls)
    {
        foreach (var (g, re) in DECO_GROUPS) if (re.IsMatch(cls)) return g;
        return "Other";
    }

    static string Display(SimRules R, string name)
    {
        try { var t = R.Text(name); return t.Name.Length > 0 ? t.Name : SimRules.Prettify(name); }
        catch (Exception) { return SimRules.Prettify(name); }
    }

    /// <summary>the entries of a category ("All", "MIRAGE" or one of CATEGORIES) whose name, class or group has every word
    /// of the query (any case), sorted by group and name</summary>
    public static List<PaletteEntry> Filter(IEnumerable<PaletteEntry> all, string category, string? query)
    {
        var words = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return all.Where(e => category == ALL || (category == MIRAGE ? e.Mirage : e.Category == category))
            .Where(e => words.All(w => e.Name.ToLowerInvariant().Contains(w) || e.Cls.ToLowerInvariant().Contains(w) || e.Group.ToLowerInvariant().Contains(w)))
            .OrderBy(e => Array.IndexOf(CATEGORIES, e.Category)).ThenBy(e => e.Group, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>the class names of a data folder (lower case): the tech tree's objects and the class files' classes - the
    /// base game's, to mark the classes a mod adds</summary>
    public static HashSet<string> ClassNamesOf(string dataDir)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (Json.TryParseFile(Path.Combine(dataDir, "techtree.json")) is JsonObject tt && tt["StartTT"]?["Objects"] is JsonObject objs)
                foreach (var (_, tribe) in objs)
                    if (tribe is JsonObject to)
                        foreach (var (_, types) in to)
                            if (types is JsonObject ty) foreach (var (n, _) in ty) set.Add(n.ToLowerInvariant());
            if (Json.TryParseFile(Path.Combine(dataDir, "gamedata.json")) is JsonObject gd && gd["script"] is JsonObject sc)
                foreach (var (n, _) in sc) set.Add(n.ToLowerInvariant());
        }
        catch (Exception e) { L.Warn($"class names of {dataDir}: {e.Message}"); }
        return set;
    }
}
