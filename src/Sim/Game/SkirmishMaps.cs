// The maps a skirmish can be played on: the multiplayer maps of the installation's Data folders of the configuration
// (Data/<folder>/Maps/**/*.ula - Base/Maps/Multiplayer, the add-ons' and mods' map folders), each with what the map
// list shows: its name, players, game types and type (Root/Base MaxPlayers, GameType, MapType - the toolkit's
// pwexport.ula.mp_header), read from the level info only (Ula.ParseHeader). The campaign's maps (MapType
// singleplayer) are left out, as in the browser remake (ui/skirmish.js fillMaps; they can be opened as a file).
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
public sealed record SkirmishMap(string Path, string FullPath, string Folder, string File, string Name, int Players, List<string> Modes, string Type, string Setting, string Text = "", string Author = "")
{
    /// <summary>the setup's game types the map has (deathmatch, domination, defender)</summary>
    public IEnumerable<string> SetupModes => (Modes.Count > 0 ? Modes : new List<string> { "Deathmatch" }).Select(Skirmish.ModeOfType).Where(m => m != null).Select(m => m!).Distinct();
}

public static class SkirmishMaps
{
    static readonly Logger L = Log.For("Skirmish.Maps");
    static readonly ConcurrentDictionary<string, (long Size, DateTime Time, MapData Header)> s_headers = new();

    /// <summary>every map of the configuration's Data folders (multiplayer ones only unless all), Base first, then by
    /// folder and file</summary>
    public static List<SkirmishMap> List(GameConfiguration cfg, bool all = false)
    {
        s_texts = MapTexts(cfg);
        MapFiles.UseInstall(cfg.Install);
        var res = new List<SkirmishMap>();
        var data = System.IO.Path.Combine(cfg.Install, "Data");
        if (!Directory.Exists(data)) return res;
        foreach (var folder in cfg.Folders)
        {
            var root = Dir(data, folder);
            var maps = root == null ? null : Dir(root, "Maps");
            if (maps == null || !Directory.Exists(maps)) continue;
            foreach (var f in Directory.EnumerateFiles(maps, "*.ula", SearchOption.AllDirectories))
            {
                var e = Entry(data, f);
                if (e == null || (!all && e.Type != "multiplayer")) continue;
                res.RemoveAll(x => string.Equals(x.Path, e.Path, StringComparison.OrdinalIgnoreCase));
                res.Add(e);
            }
        }
        // the remake's own maps (game/maps/*.pwmap.json: an original map of the installation with biomes ...), after the
        // installation's - only those whose base map this installation has
        var remake = MapFiles.Root();
        if (Directory.Exists(remake))
            foreach (var f in Directory.EnumerateFiles(remake, "*" + MapFiles.RemakeExt).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                if (RemakeEntry(f) is { } e && (all || e.Type == "multiplayer")) res.Add(e);
        return res.OrderBy(m => m.Folder.StartsWith("Base", StringComparison.OrdinalIgnoreCase) ? 0 : m.Folder == MapFiles.RemakeFolder ? 2 : 1)
            .ThenBy(m => m.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static Dictionary<string, string> s_texts = new();

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

    static SkirmishMap? Entry(string data, string full)
    {
        var rel = System.IO.Path.GetRelativePath(data, full).Replace('\\', '/');
        var parts = rel.Split('/');
        // "Base / Multiplayer": the pack and the folders below Maps
        var folder = string.Join(" / ", new[] { parts[0] }.Concat(parts.Skip(2).Take(Math.Max(0, parts.Length - 3))));
        try
        {
            var h = Header(full);
            var mp = MapSource.MpSettingsOf(h);
            var type = h.Description.TryGetValue("Root/Base/MapType", out var t) && t.Length > 0 ? t : "multiplayer";
            var (name, text, author) = Describe(h, s_texts);
            return new SkirmishMap(rel, full, folder, parts[^1], name, (int)Math.Max(1, mp.MaxPlayers), mp.GameTypes, type, h.SettingName, text, author);
        }
        catch (Exception ex)
        {
            L.WarnOnce("map:" + full, $"map not readable, left out: {full}: {ex.Message}");
            return null;
        }
    }

    static SkirmishMap? RemakeEntry(string full)
    {
        try
        {
            if (MapFiles.BaseOf(MapFiles.ReadRemake(full)) == null) return null;
            var h = Header(full);
            var mp = MapSource.MpSettingsOf(h);
            var type = h.Description.TryGetValue("Root/Base/MapType", out var t) && t.Length > 0 ? t : "multiplayer";
            var (name, text, author) = Describe(h, s_texts);
            var file = System.IO.Path.GetFileName(full);
            return new SkirmishMap(MapFiles.RemakeFolder + "/" + file, full, MapFiles.RemakeFolder, file, name, (int)Math.Max(1, mp.MaxPlayers), mp.GameTypes, type, h.SettingName, text, author);
        }
        catch (Exception ex)
        {
            L.WarnOnce("map:" + full, $"remake map not readable, left out: {full}: {ex.Message}");
            return null;
        }
    }

    /// <summary>the level info and terrain header of a map file, read once (until the file changes)</summary>
    public static MapData Header(string path)
    {
        var fi = new FileInfo(path);
        if (s_headers.TryGetValue(path, out var hit) && hit.Size == fi.Length && hit.Time == fi.LastWriteTimeUtc) return hit.Header;
        var h = MapFiles.ReadHeader(path);
        s_headers[path] = (fi.Length, fi.LastWriteTimeUtc, h);
        return h;
    }

    /// <summary>the file of a setup's map: a path below the installation's Data folder (found without regard to case),
    /// else the configuration's folders (GameFile), else an absolute path; null: "" (a random map) or not found</summary>
    public static string? Resolve(GameConfiguration cfg, string map)
    {
        if (string.IsNullOrEmpty(map)) return null;
        if (System.IO.Path.IsPathRooted(map)) return File.Exists(map) ? map : null;
        return Campaign.WorldBuilder.FindMap(cfg, map);
    }
}
