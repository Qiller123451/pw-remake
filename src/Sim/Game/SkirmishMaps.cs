// The maps a skirmish can be played on: the configuration's converted maps (content/configs/<config>/maps: the
// installation's maps of the configuration's Data folders and the remake's own maps, converted by Prepare -
// docs/MAP_FORMAT.md), each with what the map list shows: its name, players, game types and type (Root/Base
// MaxPlayers, GameType, MapType of the level settings - the toolkit's pwexport.ula.mp_header), read from map.lua only
// (MapFiles.ReadHeader). The campaign's maps (MapType singleplayer) are left out, as in the browser remake
// (ui/skirmish.js fillMaps; they can be opened as a file). The user's own maps (MapFiles.UserMaps: the map editor's
// saves and imported .pwmap files) come last, in the group "My maps" (ids "User/<name>"). A map keeps the id of its original
// ("Base/Maps/Multiplayer/multi_2_jun_001.ula"): setups, saved games and lobbies name it so (MapFiles.Find).
//
// Ported from remake/src/ui/mappreview.js listMaps (pw-exporter 98c7703) and the toolkit's maps/index.json
// (toolkit/remake.py maps_index), which the browser remake read from its server.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Game;

/// <summary>a map of the list: Path = below the installation's Data folder ("Base/Maps/Multiplayer/x.ula"); Name, Text: the
/// game's own name and description (mapnames.ltf: "_MN_MP_4_MALICE" -> "Malice")</summary>
public sealed record SkirmishMap(string Path, string FullPath, string Folder, string File, string Name, int Players, List<string> Modes, string Type, string Setting, string Text = "", string Author = "",
    string Requires = "", string? Unfit = null)
{
    /// <summary>the setup's game types the map has (deathmatch, domination, defender)</summary>
    public IEnumerable<string> SetupModes => (Modes.Count > 0 ? Modes : new List<string> { "Deathmatch" }).Select(Skirmish.ModeOfType).Where(m => m != null).Select(m => m!).Distinct();
}

public static class SkirmishMaps
{
    static readonly Logger L = Log.For("Skirmish.Maps");
    static readonly ConcurrentDictionary<string, (long Size, DateTime Time, MapData Header)> s_headers = new();

    /// <summary>every converted map of the configuration (multiplayer ones only unless all), Base first, then the other
    /// folders, the remake's own maps last; by folder and file. A map that needs another configuration (map.lua requires:
    /// a MIRAGE map in the official game - MapFiles.RequirementProblem) is left out, unless unfit (then listed with its
    /// reason: SkirmishMap.Unfit - the editor's Open shows it greyed)</summary>
    public static List<SkirmishMap> List(GameConfiguration cfg, bool all = false, bool unfit = false)
    {
        s_texts = MapTexts(cfg);
        MapFiles.UseInstall(cfg.Install);
        var res = new List<SkirmishMap>();
        foreach (var f in MapFiles.All(cfg).Concat(MapFiles.UserMaps()))
        {
            var e = Entry(cfg, f);
            if (e == null || (!all && e.Type != "multiplayer") || (e.Unfit != null && !unfit)) continue;
            res.RemoveAll(x => string.Equals(x.Path, e.Path, StringComparison.OrdinalIgnoreCase));
            res.Add(e);
        }
        if (res.Count == 0 && !Directory.Exists(cfg.MapsDir)) L.WarnOnce("nomaps:" + cfg.Id, $"no converted maps in {cfg.MapsDir}: run Prepare (it converts the installation's maps)");
        return res.OrderBy(m => m.Folder.StartsWith("Base", StringComparison.OrdinalIgnoreCase) ? 0 : m.Folder == MapFiles.RemakeFolder ? 2 : m.Folder == UserGroup ? 3 : 1)
            .ThenBy(m => m.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static Dictionary<string, string> s_texts = new();
    /// <summary>the map list's group of the user's own maps (MapFiles.UserMaps: the map editor's saves, imported .pwmap)</summary>
    public const string UserGroup = "My maps";

    /// <summary>a map's name, description and author as the map list shows them: the level info's Root/Base/LevelName and
    /// Description, translated where they are text keys of the game's mapnames table</summary>
    public static (string Name, string Text, string Author) Describe(MapData h, IReadOnlyDictionary<string, string> texts)
    {
        string D(string k) => h.Description.TryGetValue("Root/Base/" + k, out var v) ? v : h.Info.TryGetValue(k, out var w) ? w : "";
        string T(string v) => v.Length > 0 && texts.TryGetValue(v, out var t) && t.Length > 0 ? t : v;
        var ln = T(D("LevelName"));
        return (ln.Length > 0 && !ln.StartsWith("_MN_") ? ln : h.Name, T(D("Description")) is var d && !d.StartsWith("_MN_") ? d : "", D("Author"));
    }

    /// <summary>the game's map names and descriptions (Data/locale/&lt;lang&gt;/Texts/mapnames_&lt;lang&gt;.ltf, then the
    /// configuration's folders' Locale/&lt;lang&gt;/Texts/*.ltf: "key";"text" lines, UTF-8)</summary>
    public static Dictionary<string, string> MapTexts(GameConfiguration cfg)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        var lang = string.IsNullOrEmpty(cfg.Language) ? "uk" : cfg.Language;
        var data = System.IO.Path.Combine(cfg.Install, "Data");
        var files = new List<string>();
        var loc = Dir(data, "locale") is { } l ? Dir(l, lang) : null;
        var tx = loc != null ? Dir(loc, "Texts") : null;
        if (tx != null) files.AddRange(Directory.EnumerateFiles(tx, "mapnames*.ltf"));
        foreach (var folder in cfg.Folders)
        {
            var f = Dir(data, folder); var lc = f != null ? Dir(f, "Locale") : null; var lg = lc != null ? Dir(lc, lang) : null; var t2 = lg != null ? Dir(lg, "Texts") : null;
            if (t2 != null) files.AddRange(Directory.EnumerateFiles(t2, "*.ltf").OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
        var line = new System.Text.RegularExpressions.Regex("^\\s*\"([^\"]*)\"\\s*;\\s*\"(.*)\"\\s*$");
        foreach (var f in files)
            try
            {
                foreach (var raw in File.ReadLines(f, System.Text.Encoding.UTF8))
                {
                    var m = line.Match(raw);
                    if (m.Success && m.Groups[1].Value.StartsWith("_MN_", StringComparison.Ordinal) || m.Success && !res.ContainsKey(m.Groups[1].Value) && f.Contains("mapnames", StringComparison.OrdinalIgnoreCase))
                        res[m.Groups[1].Value] = m.Groups[2].Value.Replace("\"\"", "\"");
                }
            }
            catch (Exception ex) { L.WarnOnce("ltf:" + f, $"map names {f}: {ex.Message}"); }
        return res;
    }

    /// <summary>a folder below root by its name, ignoring case (PathUtil.FindCaseInsensitive finds files)</summary>
    static string? Dir(string root, string name)
    {
        try { return Directory.EnumerateDirectories(root).FirstOrDefault(d => string.Equals(System.IO.Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception) { return null; }
    }

    static SkirmishMap? Entry(GameConfiguration cfg, string full)
    {
        try
        {
            var h = Header(full);
            var doc = s_docs.TryGetValue(full, out var dd) ? dd.Doc : null;
            var id = doc != null ? MapFiles.IdOf(cfg, full, doc) : System.IO.Path.GetRelativePath(cfg.MapsDir, full).Replace('\\', '/');
            var parts = id.Split('/');
            // "Base / Multiplayer": the pack and the folders below Maps (the remake's own maps: "Remake")
            bool user = parts[0] == MapFiles.UserFolder && MapFiles.IsUserPath(full);
            var folder = user ? UserGroup : parts[0] == MapFiles.RemakeFolder ? MapFiles.RemakeFolder
                : string.Join(" / ", new[] { parts[0] }.Concat(parts.Skip(2).Take(Math.Max(0, parts.Length - 3))));
            var file = h.File.Length > 0 && parts[0] != MapFiles.RemakeFolder && !user ? h.File : parts[^1];
            var mp = MapSource.MpSettingsOf(h);
            var type = h.Description.TryGetValue("Root/Base/MapType", out var t) && t.Length > 0 ? t : "multiplayer";
            var (name, text, author) = Describe(h, s_texts);
            return new SkirmishMap(id, full, folder, file, name, (int)Math.Max(1, mp.MaxPlayers), mp.GameTypes, type, h.SettingName, text, author,
                h.Requires, MapFiles.RequirementProblem(cfg, h.Requires));
        }
        catch (Exception ex)
        {
            L.WarnOnce("map:" + full, $"map not readable, left out: {full}: {ex.Message}");
            return null;
        }
    }

    static readonly ConcurrentDictionary<string, (DateTime Time, PwMap Doc)> s_docs = new();

    /// <summary>the level info, settings and terrain size of a map, read once (until its map.lua changes)</summary>
    public static MapData Header(string path)
    {
        var key = PwMapReader.IsMap(path) && Directory.Exists(path) ? System.IO.Path.Combine(path, PwMapPaths.Map) : path;
        var fi = new FileInfo(key);
        long size = fi.Exists ? fi.Length : -1;
        var time = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
        if (s_headers.TryGetValue(path, out var hit) && hit.Size == size && hit.Time == time) return hit.Header;
        var doc = MapFiles.ReadDocument(path, header: true);
        var h = doc.ToMapData();
        s_headers[path] = (size, time, h);
        s_docs[path] = (time, doc);
        return h;
    }

    /// <summary>the converted map of a setup's map id (MapFiles.Find: the configuration's maps folder; an absolute path:
    /// itself); with the content hash of a multiplayer setup (SkirmishSetup.MapHash) the copy of that content - the map
    /// received from the host (MapFiles.FindFor); null: "" (a random map) or not found</summary>
    public static string? Resolve(GameConfiguration cfg, string map, string? hash = null) => string.IsNullOrEmpty(map) ? null : MapFiles.FindFor(cfg, map, hash);
}
