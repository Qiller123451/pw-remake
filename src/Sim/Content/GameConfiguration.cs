// A game configuration = the rules, assets and missions one game is played with: the official game (Base +
// BoosterPack 1: Norsemen, Dustriders, Dragon Clan) or a mod such as MIRAGE (Base + BoosterPack 3 + MIRAGE, with the
// SEAS playable). The title screen asks which one to play; everything after that reads through the chosen one.
//
// A configuration is a folder content/configs/<id>/ with config.json (docs/CONTENT.md):
//   {
//     "id": "mirage", "title": "MIRAGE", "mod": "MIRAGE",
//     "folders": ["Base", "BoosterPack3", "MIRAGE"],          // the game's Data folders, later ones win
//     "playableTribes": ["Hu", "Aje", "Ninigi", "SEAS"],
//     "data": "E:/Paraworld/export/remake/375f3d3a@MIRAGE",  // rules + converted assets (built by the toolkit today)
//     "install": "E:/Paraworld",                             // the original game (sounds, music, maps)
//     (data and install may be relative to the configuration's folder)
//     "language": "uk"
//   }
// and missions/ (the campaign as Lua mission folders, docs/LUA_MAPS.md), plus optional overrides/ (files that replace
// files of "data" by the same relative path - how own assets come in later, see AssetSource).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Content;

public sealed class GameConfiguration
{
    static readonly Logger L = Log.For("Content");

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    /// <summary>"" for the official game, else the mod's name (MIRAGE).</summary>
    [JsonPropertyName("mod")] public string Mod { get; set; } = "";
    /// <summary>Data folders of the installation in load order; a later folder's file replaces an earlier one's.</summary>
    [JsonPropertyName("folders")] public List<string> Folders { get; set; } = new() { "Base", "BoosterPack1" };
    [JsonPropertyName("playableTribes")] public List<string> PlayableTribes { get; set; } = new() { "Hu", "Aje", "Ninigi" };
    /// <summary>Folder with gamedata.json, techtree.json, ai.json, forest.json, help.json and assets/ (manifest.json ...).</summary>
    [JsonPropertyName("data")] public string Data { get; set; } = "";
    /// <summary>The ParaWorld installation (the folder that contains Data).</summary>
    [JsonPropertyName("install")] public string Install { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "uk";
    /// <summary>When and from what the prepare tool made this configuration (shown in the menu, logged).</summary>
    [JsonPropertyName("prepared")] public string Prepared { get; set; } = "";

    /// <summary>The folder of this configuration (set when loaded).</summary>
    [JsonIgnore] public string Dir { get; private set; } = "";
    [JsonIgnore] public string MissionsDir => Path.Combine(Dir, "missions");
    /// <summary>the configuration's maps, converted by Prepare (docs/MAP_FORMAT.md "Where maps are")</summary>
    [JsonIgnore] public string MapsDir => Path.Combine(Dir, "maps");
    [JsonIgnore] public string OverridesDir => Path.Combine(Dir, "overrides");
    [JsonIgnore] public bool IsMod => !string.IsNullOrEmpty(Mod);

    public static GameConfiguration? Load(string dir)
    {
        var path = Path.Combine(dir, "config.json");
        var c = Json.Load<GameConfiguration>(path);
        if (c == null) return null;
        c.Dir = dir;
        // "data" and "install" may be relative to the configuration's folder: a package made by tools/package keeps the
        // converted data beside the game ("../../../data/<id>")
        c.Data = Resolve(dir, c.Data);
        c.Install = Resolve(dir, c.Install);
        if (string.IsNullOrEmpty(c.Id)) c.Id = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(c.Title)) c.Title = c.IsMod ? c.Mod : "ParaWorld";
        return c;
    }

    static string Resolve(string dir, string p) =>
        string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(dir, p));

    /// <summary>What is wrong with the configuration (empty = ready to play).</summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        if (!Directory.Exists(Data)) p.Add($"game data folder not found: {Data}");
        else
        {
            foreach (var f in new[] { "gamedata.json", "techtree.json", "assets/manifest.json" })
                if (!File.Exists(Path.Combine(Data, f))) p.Add($"missing {f} in {Data}");
        }
        if (string.IsNullOrWhiteSpace(Install)) p.Add("the ParaWorld folder is not set (a package: run \"Set ParaWorld folder.bat\")");
        else if (!Directory.Exists(Path.Combine(Install, "Data"))) p.Add($"ParaWorld installation not found: {Install}");
        if (!Directory.Exists(MissionsDir)) p.Add($"no missions folder: {MissionsDir} (run the prepare tool)");
        // the map format (docs/MAP_FORMAT.md): content prepared before it has missions of the old layout and no maps
        else if (Directory.EnumerateDirectories(MissionsDir).Any(d => File.Exists(Path.Combine(d, "mission.json")) && !File.Exists(Path.Combine(d, "map.lua"))))
            p.Add("the missions are in the old layout: run the prepare tool again (it converts the maps to the remake's map format)");
        if (!Directory.Exists(MapsDir)) p.Add($"no maps folder: {MapsDir} (run the prepare tool again: it converts the installation's maps)");
        return p;
    }

    /// <summary>A file of the installation by its path below Data/&lt;folder&gt;/ - the last folder that has it wins.</summary>
    public string? GameFile(string rel)
    {
        for (int i = Folders.Count - 1; i >= 0; i--)
        {
            var p = PathUtil.FindCaseInsensitive(Path.Combine(Install, "Data", Folders[i]), rel);
            if (p != null) return p;
        }
        return null;
    }

    public override string ToString() => $"{Id} ({Title}; folders {string.Join("+", Folders)}; data {Data})";
}

/// <summary>Finds the configurations below content/configs.</summary>
public static class Configurations
{
    static readonly Logger L = Log.For("Content");

    public static List<GameConfiguration> Discover(string contentRoot)
    {
        var res = new List<GameConfiguration>();
        var dir = Path.Combine(contentRoot, "configs");
        if (!Directory.Exists(dir)) { L.Warn($"no configurations in {dir}"); return res; }
        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var c = GameConfiguration.Load(d);
            if (c == null) { L.Warn($"{d}: no readable config.json"); continue; }
            res.Add(c);
            L.Info($"configuration {c}");
        }
        // the official game first, then mods by name
        return res.OrderBy(c => c.IsMod ? 1 : 0).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public static class PathUtil
{
    /// <summary>The path below root whose parts match rel ignoring case (the game's files are named inconsistently).</summary>
    public static string? FindCaseInsensitive(string root, string rel)
    {
        var direct = Path.Combine(root, rel);
        if (File.Exists(direct)) return direct;
        if (!Directory.Exists(root)) return null;
        string cur = root;
        // ("Audio/Sound/../SeqSounds/x.mp3": the mission speech is addressed relative to Audio/Sound, as the original's
        // dialogsounds.txt does - '..' steps back before the parts are matched)
        var parts = new List<string>();
        foreach (var p in rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p != ".") parts.Add(p);
        for (int i = 0; i < parts.Count; i++)
        {
            bool last = i == parts.Count - 1;
            string? next = null;
            try
            {
                foreach (var e in last ? Directory.EnumerateFileSystemEntries(cur) : Directory.EnumerateDirectories(cur))
                    if (string.Equals(Path.GetFileName(e), parts[i], StringComparison.OrdinalIgnoreCase)) { next = e; break; }
            }
            catch { return null; }
            if (next == null) return null;
            cur = next;
        }
        return File.Exists(cur) ? cur : null;
    }

    public static string Norm(string p) => p.Replace('\\', '/');
}
