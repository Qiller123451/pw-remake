// Finding and reading a map of a configuration into a map source (Sim/Maps): the converted map (the remake's map
// format, docs/MAP_FORMAT.md: content/configs/<config>/maps) by name, id or path, the rules (Sim/Rules through
// RulesMapAdapter), the forest layouts (forest.json of the converted data) and the models the converted data has
// (assets/manifest.json). No Godot types.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.View.World;

public static class MapLoader
{
    static readonly Logger L = Log.For("View.World");

    /// <summary>
    /// a map: an existing path (a map folder, a .pwmap - or a .ula, converted in memory), a setup's id
    /// ("Base/Maps/Multiplayer/multi_2_jun_001.ula"), or a name ("single_11", "multi_2_jun_001", "Cpn_single_001/single_11")
    /// searched among the configuration's converted maps (later folders first)
    /// </summary>
    public static string? FindMap(GameConfiguration cfg, string nameOrPath)
    {
        if (PwMapReader.IsMap(nameOrPath) || File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);
        if (MapFiles.Find(cfg, nameOrPath) is { } m) return m;
        string want = MapFiles.FolderOf(nameOrPath);
        var all = MapFiles.All(cfg);
        if (all.Count == 0) L.Warn($"no converted maps in {cfg.MapsDir}: run Prepare");
        // the configuration's later folders first (MIRAGE's single_11 before Base's)
        for (int i = cfg.Folders.Count - 1; i >= 0; i--)
            foreach (var f in all)
            {
                var rel = Path.GetRelativePath(cfg.MapsDir, f).Replace('\\', '/');
                if (!rel.StartsWith(cfg.Folders[i] + "/", StringComparison.OrdinalIgnoreCase)) continue;
                if (("/" + MapFiles.FolderOf(rel)).EndsWith("/" + want, StringComparison.OrdinalIgnoreCase)) return f;
            }
        return all.FirstOrDefault(f => ("/" + MapFiles.FolderOf(Path.GetRelativePath(cfg.MapsDir, f).Replace('\\', '/'))).EndsWith("/" + want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>every converted map of the configuration (for a map list)</summary>
    public static IEnumerable<string> AllMaps(GameConfiguration cfg) => MapFiles.All(cfg);

    /// <summary>
    /// reads the map file into a map source with everything on it (MapSourceConfig null: all objects; a campaign mission
    /// passes its SkipObjects). hasModel: the converted data's models (the asset manifest).
    /// </summary>
    public static MapSource Load(string mapPath, PwRemake.Sim.Rules.Rules rules, string dataDir, Func<string, bool> hasModel, MapSourceConfig? cfg = null)
    {
        var md = MapFiles.Read(mapPath);       // (with its biome layer)
        var forest = ForestData.LoadFile(Path.Combine(dataDir, "forest.json"));
        if (forest == null) L.Warn($"no forest.json in {dataDir}: no forest blocks");
        return MapSource.OriginalSource(md, new RulesMapAdapter(rules), cfg, m => hasModel(m.ToLowerInvariant()), forest);
    }
}
