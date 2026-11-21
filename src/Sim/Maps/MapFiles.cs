// Where the game finds its maps and how it reads them (docs/MAP_FORMAT.md "Where maps are"). The game reads only the
// remake's map format: a map folder (map.lua, terrain/*.png ...) or a .pwmap zip.
//
//   * a configuration's maps: content/configs/<config>/maps/ - the installation's maps converted by Prepare
//     (tools/mapconvert), at the path of the original below the installation's Data folder without ".ula":
//     Data/Base/Maps/Multiplayer/multi_4_ice_601.ula -> maps/Base/Maps/Multiplayer/multi_4_ice_601/; the remake's own
//     maps (game/maps/<name>.pwmap.json recipes: an original map + name, description, biomes) -> maps/Remake/<name>/;
//     maps made in the editor or downloaded: any folder or .pwmap file below maps/;
//   * a campaign mission's map: the mission folder itself (content/configs/<config>/missions/<folder>/).
//
// A map keeps its old id: setups, saved games, replays and multiplayer lobbies name a map as before
// ("Base/Maps/Multiplayer/multi_2_jun_001.ula", "Remake/frost_to_jungle.pwmap.json"); Find(cfg, id) gives the
// converted map. A .ula (or a .pwmap.json recipe) given directly by its path - a test, a debug command line, a community
// map not converted yet - is converted in memory by PwMapConvert (the one place that reads .ula).
//
//   * the user's own maps (the map editor's saves, imported .pwmap files): UserDir - the game sets it to its user folder's
//     maps/ (Godot's user://maps: %APPDATA%\Godot\app_userdata\ParaWorld Remake\maps on Windows; PW_USER_MAPS
//     overrides it); their ids are "User/<folder or file.pwmap>" (MapFiles.UserFolder), the same on every machine.
//
//   Find(cfg, id)          the converted map of a setup's map id (null: none)
//   All(cfg)               every map of the configuration's maps folder
//   UserMaps()             every map of the user's maps folder
//   ContentHash(path)      a hash of every file of a map (multiplayer: the same map on every machine)
//   Read(path)             MapData of a map (folder, .pwmap; .ula / .pwmap.json converted in memory)
//   ReadHeader(path)       only map.lua, the terrain's size and the minimap (the map list)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

public static class MapFiles
{
    static readonly Logger L = Log.For("Maps.Files");
    /// <summary>a map recipe of the remake (game/maps; converted by Prepare into maps/Remake/)</summary>
    public const string RemakeExt = ".pwmap.json";
    /// <summary>the folder of the remake's own maps (setup ids "Remake/&lt;file&gt;")</summary>
    public const string RemakeFolder = "Remake";
    public const string UlaExt = ".ula";

    /// <summary>the first part of a user map's id ("User/my_map", "User/shared.pwmap")</summary>
    public const string UserFolder = "User";
    static string? s_userDir;
    /// <summary>the user's maps folder (the map editor saves there; null: none - tests, the dedicated server). PW_USER_MAPS
    /// overrides what the game sets.</summary>
    public static string? UserDir
    {
        get => Environment.GetEnvironmentVariable("PW_USER_MAPS") is { Length: > 0 } e ? e : s_userDir;
        set => s_userDir = value;
    }
    /// <summary>is a path inside the user's maps folder</summary>
    public static bool IsUserPath(string path) =>
        UserDir is { } u && Path.GetFullPath(path).StartsWith(Path.GetFullPath(u).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>the installation's Data folder the remake recipes' base maps are found in (UseInstall)</summary>
    public static string? DataDir { get; private set; }
    public static void UseInstall(string? install) { if (!string.IsNullOrEmpty(install)) DataDir = Path.Combine(install, "Data"); }

    /// <summary>game/maps next to the running game (or PW_MAPS): biomes.json and the remake's map recipes</summary>
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
    public static bool IsUla(string path) => path.EndsWith(UlaExt, StringComparison.OrdinalIgnoreCase);

    /// <summary>the folder a map id is converted to, below the maps folder: "Base/Maps/Multiplayer/x.ula" ->
    /// "Base/Maps/Multiplayer/x", "Remake/x.pwmap.json" -> "Remake/x" (a .pwmap file stays itself)</summary>
    public static string FolderOf(string id)
    {
        var p = PathUtil.Norm(id).Trim('/');
        if (IsUla(p)) return p[..^UlaExt.Length];
        if (IsRemake(p)) return p[..^RemakeExt.Length];
        return p;
    }

    /// <summary>a directory or file below root by a relative path, ignoring case; null: none</summary>
    static string? FindCI(string root, string rel)
    {
        var cur = root;
        var parts = PathUtil.Norm(rel).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            var direct = Path.Combine(cur, parts[i]);
            if (Directory.Exists(direct) || (i == parts.Length - 1 && File.Exists(direct))) { cur = direct; continue; }
            if (!Directory.Exists(cur)) return null;
            var hit = Directory.EnumerateFileSystemEntries(cur).FirstOrDefault(e => string.Equals(Path.GetFileName(e), parts[i], StringComparison.OrdinalIgnoreCase));
            if (hit == null) return null;
            cur = hit;
        }
        return cur;
    }

    static string? MapAt(string root, string rel)
    {
        if (!Directory.Exists(root)) return null;
        if (FindCI(root, rel) is { } p && PwMapReader.IsMap(p)) return p;
        if (FindCI(root, rel + PwMapPaths.Ext) is { } z && File.Exists(z)) return z;
        return null;
    }

    /// <summary>the converted map of a setup's map id (see the file header): the configuration's maps folder at the
    /// id's path, else the same path below each of the configuration's folders (the last one that has it); a rooted path:
    /// itself (a map folder, a .pwmap, or a .ula / .pwmap.json read through the converter). null: "" or none.</summary>
    public static string? Find(GameConfiguration cfg, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        UseInstall(cfg.Install);
        if (Path.IsPathRooted(id)) return PwMapReader.IsMap(id) || (File.Exists(id) && (IsUla(id) || IsRemake(id))) ? id : null;
        var rel = FolderOf(id);
        // the user's maps: "User/<name>"
        if (rel.StartsWith(UserFolder + "/", StringComparison.OrdinalIgnoreCase))
            return UserDir is { } ud ? MapAt(ud, rel[(UserFolder.Length + 1)..]) : null;
        if (MapAt(cfg.MapsDir, rel) is { } m) return m;
        var parts = rel.Split('/');
        if (parts.Length > 1)
        {
            var rest = string.Join('/', parts.Skip(1));
            for (int i = cfg.Folders.Count - 1; i >= 0; i--)
                if (MapAt(cfg.MapsDir, cfg.Folders[i] + "/" + rest) is { } f) return f;
        }
        return null;
    }

    /// <summary>the folder below the user's maps that maps received in a multiplayer lobby go to (docs/MULTIPLAYER.md
    /// "Maps": the host sends its .pwmap; saved as &lt;content hash&gt;.pwmap)</summary>
    public const string DownloadedFolder = "downloaded";
    /// <summary>the received maps' folder (null: no user maps folder)</summary>
    public static string? DownloadedDir(string? userDir = null) => (userDir ?? UserDir) is { } u ? Path.Combine(u, DownloadedFolder) : null;
    /// <summary>is a text a content hash (16 lower-case hex digits: ContentHash) - a received map's file name</summary>
    public static bool IsHash(string? h) => h is { Length: 16 } && h.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    /// <summary>the received map of a content hash (My maps/downloaded/&lt;hash&gt;.pwmap), null: none</summary>
    public static string? Downloaded(string? hash, string? userDir = null)
    {
        if (!IsHash(hash) || DownloadedDir(userDir) is not { } d) return null;
        var p = Path.Combine(d, hash + PwMapPaths.Ext);
        return File.Exists(p) ? p : null;
    }

    /// <summary>the map of a setup's id with the content the setup names (a multiplayer game's SkirmishSetup.MapHash):
    /// the map of the id when it is that content, else the received copy of that content (Downloaded), else the map of
    /// the id (null: none). Without a hash: Find.</summary>
    public static string? FindFor(GameConfiguration cfg, string id, string? hash)
    {
        var p = Find(cfg, id);
        if (string.IsNullOrEmpty(hash)) return p;
        try { if (p != null && PwMapReader.IsMap(p) && ContentHash(p) == hash) return p; }
        catch (Exception e) { L.Debug($"{p}: {e.Message}"); }
        return Downloaded(hash) ?? p;
    }

    /// <summary>every map of the configuration's maps folder (folders with map.lua, .pwmap files), in path order</summary>
    public static List<string> All(GameConfiguration cfg)
    {
        var res = new List<string>();
        if (Directory.Exists(cfg.MapsDir)) Walk(cfg.MapsDir, res);
        return res;
    }

    /// <summary>every map of the user's maps folder (folders with map.lua, .pwmap files), in path order</summary>
    public static List<string> UserMaps()
    {
        var res = new List<string>();
        if (UserDir is { } u && Directory.Exists(u)) Walk(u, res);
        return res;
    }

    static void Walk(string dir, List<string> res)
    {
        if (File.Exists(Path.Combine(dir, PwMapPaths.Map))) { res.Add(dir); return; }
        foreach (var f in Directory.EnumerateFiles(dir, "*" + PwMapPaths.Ext).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) res.Add(f);
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            if (!Path.GetFileName(d).StartsWith('.')) Walk(d, res);
    }

    /// <summary>a map's id in setups: a user map's "User/&lt;path below the user's maps folder&gt;"; else its map.lua
    /// source (the original's path), else its path below the maps folder</summary>
    public static string IdOf(GameConfiguration cfg, string path, PwMap header)
    {
        if (IsUserPath(path)) return UserFolder + "/" + Path.GetRelativePath(UserDir!, path).Replace('\\', '/');
        if (header.Source.Length > 0) return header.Source;
        var rel = Path.GetRelativePath(cfg.MapsDir, path).Replace('\\', '/');
        return rel.StartsWith("..", StringComparison.Ordinal) ? path : rel;
    }

    /// <summary>why a map that needs a configuration (map.lua requires: "MIRAGE") is not for this one - null: it is (no
    /// requirement, or the configuration's mod or id is the one required; case does not matter)</summary>
    public static string? RequirementProblem(GameConfiguration cfg, string? requires)
    {
        if (string.IsNullOrEmpty(requires)) return null;
        if (string.Equals(requires, cfg.Mod, StringComparison.OrdinalIgnoreCase) || string.Equals(requires, cfg.Id, StringComparison.OrdinalIgnoreCase)) return null;
        return $"needs {requires} (this is {(cfg.IsMod ? cfg.Mod : "the official game")})";
    }

    /// <summary>a hash of a map's content: every file (its path below the map and its bytes, in path order; temporaries
    /// left out) - a folder and its .pwmap give the same hash. Multiplayer compares it (SkirmishSetup.MapHash); 16 hex
    /// digits (FNV-1a 64, as ContentFingerprint). Cached by the files' sizes and times.</summary>
    public static string ContentHash(string path)
    {
        var src = new PwMapReader.Source(path);
        var files = src.List().Where(r => !r.EndsWith(".tmp", StringComparison.Ordinal)).ToList();
        string stamp;
        if (Directory.Exists(path))
            stamp = string.Join("|", files.Select(r => { var fi = new FileInfo(Path.Combine(path, r)); return $"{r}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}"; }));
        else { var fi = new FileInfo(path); stamp = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}"; }
        lock (s_hashes) if (s_hashes.TryGetValue(path, out var c) && c.Stamp == stamp) return c.Hash;
        ulong h = 14695981039346656037UL;
        void Mix(ReadOnlySpan<byte> b) { foreach (var x in b) { h ^= x; h *= 1099511628211UL; } }
        foreach (var r in files)
        {
            Mix(System.Text.Encoding.UTF8.GetBytes(r + "\n"));
            var b = src.Get(r) ?? Array.Empty<byte>();
            Mix(BitConverter.GetBytes((long)b.Length));
            Mix(b);
        }
        var hash = h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        lock (s_hashes) s_hashes[path] = (stamp, hash);
        return hash;
    }
    static readonly Dictionary<string, (string Stamp, string Hash)> s_hashes = new();

    /// <summary>a map recipe's base map (below the installation's Data folder, else relative to the recipe, else absolute)</summary>
    public static string? FindBase(string baseRel) => DataDir != null ? PathUtil.FindCaseInsensitive(DataDir, baseRel) : null;

    /// <summary>the map document of a path (the remake's format; a .ula or .pwmap.json is converted in memory)</summary>
    public static PwMap ReadDocument(string path, bool header = false, bool mission = true)
    {
        if (IsUla(path) && File.Exists(path))
        {
            L.Info($"{Path.GetFileName(path)}: an original map file - converted in memory (Prepare converts the installation's maps)");
            return PwMapConvert.FromUla(path, DataDir != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(DataDir), StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(DataDir, path).Replace('\\', '/') : null);
        }
        if (IsRemake(path) && File.Exists(path)) return PwMapConvert.FromRemakeRecipe(path, FindBase, RemakeFolder + "/" + Path.GetFileName(path));
        return header ? PwMapReader.ReadHeader(path) : PwMapReader.Read(path, mission);
    }

    /// <summary>the map's MapData (terrain, objects, regions, info; not the mission's data)</summary>
    public static MapData Read(string path) => ReadDocument(path, mission: false).ToMapData();

    /// <summary>the level info, settings and terrain size (SkirmishMaps.Header), with the minimap</summary>
    public static MapData ReadHeader(string path) => ReadDocument(path, header: true).ToMapData();
}
