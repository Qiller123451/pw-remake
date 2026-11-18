// Finding and reading an original map of a configuration into a map source (Sim/Maps): the .ula file by name or path
// (the installation's Data/<folder>/Maps/**, later folders first), the rules (Sim/Rules through RulesMapAdapter), the
// forest layouts (forest.json of the converted data) and the models the converted data has (assets/manifest.json).
// No Godot types.
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
    /// the .ula file of a map: an existing path, or a name ("single_11", "multi_2_jun_001", "Cpn_single_001/single_11")
    /// searched below Data/&lt;folder&gt;/Maps of the installation (the configuration's folders, last first)
    /// </summary>
    public static string? FindMap(GameConfiguration cfg, string nameOrPath)
    {
        MapFiles.UseInstall(cfg.Install);
        if (File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);
        if (MapFiles.FindRemake(nameOrPath) is { } rm) return rm;                         // Remake/<file>.pwmap.json
        if (nameOrPath.EndsWith(MapFiles.RemakeExt, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(MapFiles.Root(), nameOrPath)))
            return Path.Combine(MapFiles.Root(), nameOrPath);
        string want = nameOrPath.Replace('\\', '/');
        if (!want.EndsWith(".ula", StringComparison.OrdinalIgnoreCase)) want += ".ula";
        var folders = cfg.Folders.Count > 0 ? cfg.Folders : new List<string> { "Base" };
        for (int i = folders.Count - 1; i >= 0; i--)
        {
            var dir = Path.Combine(cfg.Install, "Data", folders[i], "Maps");
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.ula", SearchOption.AllDirectories))
                if (f.Replace('\\', '/').EndsWith("/" + want, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return null;
    }

    /// <summary>every .ula map of the installation (for a map list)</summary>
    public static IEnumerable<string> AllMaps(GameConfiguration cfg)
    {
        foreach (var folder in cfg.Folders)
        {
            var dir = Path.Combine(cfg.Install, "Data", folder, "Maps");
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.ula", SearchOption.AllDirectories).OrderBy(f => f)) yield return f;
        }
    }

    /// <summary>
    /// reads the map file into a map source with everything on it (MapSourceConfig null: all objects; a campaign mission
    /// passes its SkipObjects). hasModel: the converted data's models (the asset manifest).
    /// </summary>
    public static MapSource Load(string ulaPath, PwRemake.Sim.Rules.Rules rules, string dataDir, Func<string, bool> hasModel, MapSourceConfig? cfg = null)
    {
        var md = MapFiles.Read(ulaPath);       // (the remake's biome layer; a remake map .pwmap.json)
        var forest = ForestData.LoadFile(Path.Combine(dataDir, "forest.json"));
        if (forest == null) L.Warn($"no forest.json in {dataDir}: no forest blocks");
        return MapSource.OriginalSource(md, new RulesMapAdapter(rules), cfg, m => hasModel(m.ToLowerInvariant()), forest);
    }
}
