// The one place that converts the original's maps to the remake's format (docs/MAP_FORMAT.md "Converting"):
//
//   PwMapConvert.FromUla(ulaPath, source)      an original map (.ula, read by Ula.cs) + its biome layer beside it
//                                              (<name>.biomes.json) -> PwMap
//   PwMapConvert.FromRemakeRecipe(path, find)  a map recipe of the old remake layout (game/maps/<name>.pwmap.json: an
//                                              original map + name, description, author, biomes) -> PwMap
//   PwMapConvert.AddMission(map, folder)       a mission folder of the old layout (mission.json, world.json,
//                                              texts/<lang>.json, triggers.lua, custom.lua - tools/prepare/
//                                              campaign_to_lua.py) -> the map's mission, world, texts, triggers; the
//                                              JSON is checked to come back the same from its Lua (CheckSameAsLua)
//
// The game reads only the converted maps; the .ula reader is called from here only. Version: when the conversion
// changes what it writes, Version goes up and Prepare converts every map again (tools/mapconvert).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

public static class PwMapConvert
{
    static readonly Logger L = Log.For("Maps.Convert");
    /// <summary>the converter's version (Prepare converts again when it changes)</summary>
    public const int Version = 2;          // 2: JSON numbers written as the reader writes them ("16.0" -> 16: LuaData.Scalar)

    /// <summary>an original map file; source = its id in setups ("Base/Maps/Multiplayer/x.ula"; default: the file name)</summary>
    public static PwMap FromUla(string ulaPath, string? source = null)
    {
        var md = Ula.ParseUlaFile(ulaPath);
        var side = BiomeLayer.SidecarOf(ulaPath);
        if (File.Exists(side))
            try { md.Biomes = BiomeLayer.Read(side); }
            catch (Exception e) { L.Warn($"{side}: {e.Message} - the map without biomes"); }
        var m = PwMap.FromMapData(md);
        m.Source = source ?? Path.GetFileName(ulaPath);
        return m;
    }

    /// <summary>a map recipe of the old remake layout (.pwmap.json); findBase: its "base" (below the installation's Data
    /// folder ...) -> the .ula, or null</summary>
    public static PwMap FromRemakeRecipe(string path, Func<string, string?> findBase, string? source = null)
    {
        var n = JsonNode.Parse(File.ReadAllText(path)) ?? throw new FormatException($"{path}: empty");
        var bas = n["base"]?.GetValue<string>() ?? throw new FormatException($"{path}: a remake map needs \"base\"");
        var rel = Path.Combine(Path.GetDirectoryName(path) ?? "", bas);
        var bp = findBase(bas) ?? (File.Exists(rel) ? rel : Path.IsPathRooted(bas) && File.Exists(bas) ? bas : null)
                 ?? throw new FileNotFoundException($"{Path.GetFileName(path)}: the base map {bas} is not in the installation");
        var m = PwMap.FromMapData(Ula.ParseUlaFile(bp));
        m.Source = source ?? Path.GetFileName(path);
        if (n["name"]?.GetValue<string>() is { } nm) { m.Name = nm; m.Title = nm; }
        if (n["description"]?.GetValue<string>() is { } d) m.Description = d;
        if (n["author"]?.GetValue<string>() is { } a) m.Author = a;
        if (n["biomes"] is JsonNode b)
            m.Biomes = b is JsonValue v && v.TryGetValue<string>(out var f) ? BiomeLayer.Read(Path.Combine(Path.GetDirectoryName(path) ?? "", f)) : BiomeLayer.Parse(b);
        return m;
    }

    /// <summary>a mission folder of the old layout into the map: mission.json -> Mission, world.json -> World,
    /// texts/&lt;lang&gt;.json -> Texts, triggers.lua, custom.lua and other Lua files as they are</summary>
    public static void AddMission(PwMap m, string folder)
    {
        string name = Path.GetFileName(folder.TrimEnd('/', '\\'));
        m.Mission = Json.TryParseFile(Path.Combine(folder, "mission.json")) as JsonObject ?? throw new FileNotFoundException($"{folder}: no mission.json");
        m.World = Json.TryParseFile(Path.Combine(folder, "world.json")) ?? new JsonObject();
        CheckSameAsLua(m.Mission, name + "/mission.json", PwMapReader.MissionObjectPaths);
        CheckSameAsLua(m.World, name + "/world.json", PwMapReader.WorldObjectPaths);
        var td = Path.Combine(folder, "texts");
        if (Directory.Exists(td))
            foreach (var f in Directory.GetFiles(td, "*.json").OrderBy(x => x, StringComparer.Ordinal))
                if (Json.TryParseFile(f) is JsonObject tx) m.Texts[Path.GetFileNameWithoutExtension(f)] = tx;
        var tr = Path.Combine(folder, "triggers.lua");
        if (File.Exists(tr)) m.Triggers = File.ReadAllText(tr, Encoding.UTF8);
        var cu = Path.Combine(folder, "custom.lua");
        if (File.Exists(cu)) m.Custom = File.ReadAllText(cu, Encoding.UTF8);
        foreach (var f in Directory.GetFiles(folder, "*.lua"))
        {
            var fn = Path.GetFileName(f);
            if (fn is "triggers.lua" or "custom.lua" or "map.lua" or "world.lua" or "objects.lua" or "regions.lua") continue;
            m.Files[fn] = File.ReadAllBytes(f);                   // (a module custom.lua requires)
        }
    }

    /// <summary>the JSON comes back the same from its Lua (nulls are left out in Lua: dropped on both sides); throws
    /// naming the first difference - a new map-valued field of the export needs its path in the reader's list</summary>
    public static void CheckSameAsLua(JsonNode json, string what, IReadOnlyCollection<string> objectPaths)
    {
        var back = LuaData.ParseJson(LuaData.Write(json), what, objectPaths);
        var diff = FirstDifference(json, back, "");
        if (diff != null) throw new InvalidDataException($"{what}: the Lua form differs from the JSON at {diff}");
    }

    /// <summary>the first path where two JSON trees differ (numbers by value, nulls = absent), or null</summary>
    public static string? FirstDifference(JsonNode? a, JsonNode? b, string path)
    {
        if (a == null || b == null) return a == null && b == null ? null : path + " (null)";
        switch (a)
        {
            case JsonObject oa:
            {
                if (b is not JsonObject ob) return path + " (object / " + b.GetValueKind() + ")";
                var ka = oa.Where(x => x.Value != null).Select(x => x.Key).ToList();
                var kb = ob.Where(x => x.Value != null).Select(x => x.Key).ToList();
                if (!ka.SequenceEqual(kb)) return path + " (keys)";
                foreach (var k in ka) if (FirstDifference(oa[k], ob[k], path + "." + k) is { } d) return d;
                return null;
            }
            case JsonArray aa:
            {
                if (b is not JsonArray ab) return path + " (array / " + b.GetValueKind() + ")";
                if (aa.Count != ab.Count) return path + " (length)";
                for (int i = 0; i < aa.Count; i++) if (FirstDifference(aa[i], ab[i], path + "[" + i + "]") is { } d) return d;
                return null;
            }
            default:
            {
                var va = (JsonValue)a;
                if (b is not JsonValue vb) return path;
                var ka = va.GetValueKind(); var kb = vb.GetValueKind();
                if (ka == JsonValueKind.Number && kb == JsonValueKind.Number)
                {
                    double da = va.GetValue<double>(), db = vb.GetValue<double>();
                    return BitConverter.DoubleToInt64Bits(da) == BitConverter.DoubleToInt64Bits(db) ? null : path + $" ({da} / {db})";
                }
                return ka == kb && va.ToJsonString() == vb.ToJsonString() ? null : path;
            }
        }
    }
}
