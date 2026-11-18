// Map files with the remake's additions (docs/BIOMES.md "Files"):
//   * an original map: <name>.ula, and next to it (optional) <name>.biomes.json - its biome layer (BiomeLayer)
//   * a remake map: <name>.pwmap.json in game/maps (the skirmish list shows them under "Remake"): an original map it is
//     made from plus what the remake adds -
//       { "format": "pw-remake-map", "version": 1, "base": "Base/Maps/Multiplayer/multi_4_ice_601.ula",
//         "name": "...", "description": "...", "author": "...", "biomes": "<file next to it>" | { a biome layer } }
//     "base" is below the installation's Data folder (or a path relative to the remake map, or absolute). The remake
//     ships no original data: the base map comes from the player's installation.
// Read(path) gives the MapData of either (the layer in MapData.Biomes); ReadHeader the level info only.
using System;
using System.IO;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

public static class MapFiles
{
    static readonly Logger L = Log.For("Maps.Files");
    public const string RemakeExt = ".pwmap.json";
    /// <summary>the skirmish list's folder of the remake maps (setup paths "Remake/&lt;file&gt;")</summary>
    public const string RemakeFolder = "Remake";

    /// <summary>the installation's Data folder the remake maps' bases are found in (the configuration in use: UseInstall)</summary>
    public static string? DataDir { get; private set; }
    public static void UseInstall(string? install) { if (!string.IsNullOrEmpty(install)) DataDir = Path.Combine(install, "Data"); }

    /// <summary>game/maps next to the running game (or PW_MAPS): biomes.json and the remake maps</summary>
    public static string Root()
    {
        var env = Environment.GetEnvironmentVariable("PW_MAPS");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "game", "maps");
            if (Directory.Exists(c)) return c;
            c = Path.Combine(d.FullName, "maps");
            if (File.Exists(Path.Combine(c, "biomes.json"))) return c;
        }
        return Path.Combine("game", "maps");
    }

    public static bool IsRemake(string path) => path.EndsWith(RemakeExt, StringComparison.OrdinalIgnoreCase);

    /// <summary>a setup's "Remake/&lt;file&gt;" -> the file in game/maps (null: not such a path or no file)</summary>
    public static string? FindRemake(string map)
    {
        var p = PathUtil.Norm(map);
        if (!p.StartsWith(RemakeFolder + "/", StringComparison.OrdinalIgnoreCase)) return null;
        var f = Path.Combine(Root(), p[(RemakeFolder.Length + 1)..]);
        return File.Exists(f) ? f : null;
    }

    /// <summary>a remake map's own fields</summary>
    public sealed record RemakeMap(string Path, string Base, string? Name, string? Description, string? Author, JsonNode? Biomes);

    public static RemakeMap ReadRemake(string path)
    {
        var n = JsonNode.Parse(File.ReadAllText(path)) ?? throw new FormatException("empty");
        return new RemakeMap(path, n["base"]?.GetValue<string>() ?? throw new FormatException("a remake map needs \"base\""),
            n["name"]?.GetValue<string>(), n["description"]?.GetValue<string>(), n["author"]?.GetValue<string>(), n["biomes"]);
    }

    /// <summary>the base map's file: below the installation's Data folder, else relative to the remake map, else absolute</summary>
    public static string? BaseOf(RemakeMap m)
    {
        if (DataDir != null && PathUtil.FindCaseInsensitive(DataDir, m.Base) is { } p) return p;
        var rel = Path.Combine(Path.GetDirectoryName(m.Path) ?? "", m.Base);
        if (File.Exists(rel)) return rel;
        return Path.IsPathRooted(m.Base) && File.Exists(m.Base) ? m.Base : null;
    }

    /// <summary>the map with the remake's additions (a .ula and its .biomes.json, or a .pwmap.json)</summary>
    public static MapData Read(string path) => Load(path, header: false);

    /// <summary>the level info and terrain header (SkirmishMaps.Header), with a remake map's name and description</summary>
    public static MapData ReadHeader(string path) => Load(path, header: true);

    static MapData Load(string path, bool header)
    {
        if (!IsRemake(path))
        {
            var md = header ? Ula.ParseHeader(File.ReadAllBytes(path), Path.GetFileName(path)) : Ula.ParseUlaFile(path);
            if (!header)
            {
                var side = BiomeLayer.SidecarOf(path);
                if (File.Exists(side))
                    try { md.Biomes = BiomeLayer.Read(side); L.Info($"{Path.GetFileName(path)}: biome layer {string.Join(", ", md.Biomes.Biomes)}"); }
                    catch (Exception e) { L.Warn($"{side}: {e.Message} - the map without biomes"); }
            }
            return md;
        }
        var rm = ReadRemake(path);
        var bp = BaseOf(rm) ?? throw new FileNotFoundException($"{Path.GetFileName(path)}: the base map {rm.Base} is not in the installation");
        var m = header ? Ula.ParseHeader(File.ReadAllBytes(bp), Path.GetFileName(bp)) : Ula.ParseUlaFile(bp);
        // (a copy: the header cache shares the base's dictionary)
        var desc = new System.Collections.Generic.Dictionary<string, string>(m.Description);
        if (rm.Name != null) { desc["Root/Base/LevelName"] = rm.Name; m.Name = rm.Name; }
        if (rm.Description != null) desc["Root/Base/Description"] = rm.Description;
        if (rm.Author != null) desc["Root/Base/Author"] = rm.Author;
        m.Description = desc;
        if (!header && rm.Biomes != null)
        {
            try
            {
                m.Biomes = rm.Biomes is JsonValue v && v.TryGetValue<string>(out var f)
                    ? BiomeLayer.Read(Path.Combine(Path.GetDirectoryName(path) ?? "", f))
                    : BiomeLayer.Parse(rm.Biomes);
            }
            catch (Exception e) { L.Warn($"{path}: biomes: {e.Message} - the map without biomes"); }
        }
        return m;
    }
}
