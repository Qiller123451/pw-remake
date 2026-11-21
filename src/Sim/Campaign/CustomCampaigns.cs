// Custom campaigns (docs/CAMPAIGN.md "Custom campaigns", docs/MENUS.md "Campaign menu"): the campaign menu's second tab
// lists every mission that is not the original campaign, grouped as campaigns. This file is the menu's data, without
// Godot types (tests: tests/PwRemake.Tests/CustomCampaignTests.cs):
//
//   MissionGroups   the configuration's missions (missions/index.json) split into the original campaign (the tutorial
//                   and single_01 .. single_16: Campaigns.txt's main line), the Booster Pack (single_bNN: BoosterPack 1's
//                   missions 4-1 .. 4-3), the mod's own missions (MIRAGE: the SEAS versions single_bNN_seas and
//                   single_seas_02) and any other folder's. A mission whose map comes from a data folder the
//                   configuration does not have is left out: the toolkit's campaign export fills gaps of the mission
//                   list from every folder of the installation (pwexport/campaign.py campaign_maps: "folders outside the
//                   configuration only fill gaps"), so the official game's index can list MIRAGE's SEAS missions
//                   (map "MIRAGE/Maps/...") - they need MIRAGE's SEAS and are not the official game's.
//   MyMapInfo       a map of My maps as the list shows it (map.lua only: name, texts, requires, the mission table) - and
//                   whether it is an edited copy of a campaign mission (the editor's Save of a mission writes a copy into
//                   My maps that keeps the mission table - key, title, the original's .ula -, MapDataDoc.ToMap).
//   UserCampaign    a campaign of the user's: a folder <campaigns>/<name>/campaign.lua (data only, read with LuaData like
//                   map.lua): name, description, author, missions = { the maps of My maps by name, or the
//                   configuration's missions by key }, played in order, each opened when the one before was won. Shared
//                   as one .pwcampaign file (a zip: campaign.lua and maps/<name>.pwmap of every map of My maps it plays;
//                   the configuration's missions are every installation's and are not packed). An imported campaign
//                   keeps its maps in its own folder (maps/), found before My maps.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Campaign;

public enum MissionGroupKind { Original, BoosterPack, Mod, Other }

/// <summary>a group of the configuration's missions (one campaign of the menu)</summary>
public sealed class MissionGroup
{
    public MissionGroupKind Kind;
    /// <summary>"Booster Pack", "MIRAGE", the folder's name</summary>
    public string Title = "";
    public List<MissionEntry> Missions = new();
}

public static class MissionGroups
{
    static readonly Regex OriginalKey = new(@"^(tutorial|single_\d\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex BoosterKey = new(@"^single_b\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public const string BoosterTitle = "Booster Pack";

    /// <summary>the data folder a mission's map comes from ("Base", "BoosterPack1", "MIRAGE"; index.json `map`
    /// "MIRAGE/Maps/...", else `map_source` "maps/MIRAGE/..."); "" when the index does not say</summary>
    public static string SourceFolder(MissionEntry e)
    {
        var m = e.Map.Replace('\\', '/').Trim('/');
        if (m.Contains('/')) return m[..m.IndexOf('/')];
        var s = e.MapSource.Replace('\\', '/').Trim('/');
        if (s.StartsWith("maps/", StringComparison.OrdinalIgnoreCase)) s = s[5..];
        return s.Contains('/') ? s[..s.IndexOf('/')] : "";
    }

    /// <summary>is the mission the configuration's (its map from one of the configuration's data folders, or unknown)</summary>
    public static bool Belongs(GameConfiguration cfg, MissionEntry e)
    {
        var f = SourceFolder(e);
        return f == "" || cfg.Folders.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
    }

    public static MissionGroupKind KindOf(GameConfiguration cfg, MissionEntry e)
    {
        if (OriginalKey.IsMatch(e.Key)) return MissionGroupKind.Original;
        if (BoosterKey.IsMatch(e.Key)) return MissionGroupKind.BoosterPack;
        var f = SourceFolder(e);
        if (cfg.IsMod && string.Equals(f, cfg.Mod, StringComparison.OrdinalIgnoreCase)) return MissionGroupKind.Mod;
        return MissionGroupKind.Other;
    }

    /// <summary>the original campaign (tab "Campaign"): the tutorial and missions 1 - 16, in the index's order</summary>
    public static List<MissionEntry> Original(GameConfiguration cfg, IEnumerable<MissionEntry> index, Func<int, bool>? hidden = null) =>
        index.Where(e => Belongs(cfg, e) && KindOf(cfg, e) == MissionGroupKind.Original && !(hidden?.Invoke(e.Id) ?? false)).ToList();

    /// <summary>every other mission of the configuration as groups (tab "Custom campaigns"): the Booster Pack, the mod's
    /// own missions, then one group per other data folder; empty groups left out; the index's order inside a group</summary>
    public static List<MissionGroup> Custom(GameConfiguration cfg, IEnumerable<MissionEntry> index, Func<int, bool>? hidden = null)
    {
        var booster = new MissionGroup { Kind = MissionGroupKind.BoosterPack, Title = BoosterTitle };
        var mod = new MissionGroup { Kind = MissionGroupKind.Mod, Title = cfg.IsMod ? cfg.Mod : "" };
        var other = new List<MissionGroup>();
        foreach (var e in index)
        {
            if (!Belongs(cfg, e) || (hidden?.Invoke(e.Id) ?? false)) continue;
            switch (KindOf(cfg, e))
            {
                case MissionGroupKind.Original: break;
                case MissionGroupKind.BoosterPack: booster.Missions.Add(e); break;
                case MissionGroupKind.Mod: mod.Missions.Add(e); break;
                default:
                    var f = SourceFolder(e);
                    var title = f == "" ? "Other missions" : f;
                    var g = other.FirstOrDefault(x => x.Title == title);
                    if (g == null) other.Add(g = new MissionGroup { Kind = MissionGroupKind.Other, Title = title });
                    g.Missions.Add(e);
                    break;
            }
        }
        return new[] { booster, mod }.Concat(other).Where(g => g.Missions.Count > 0).ToList();
    }

    /// <summary>the end screen's Continue: the next mission of the same group (the original campaign: 4 -> 5, 16 -> the
    /// end; the Booster Pack: 4-1 -> 4-2 -> 4-3 -> the end), null at the group's end</summary>
    public static MissionEntry? NextInGroup(GameConfiguration cfg, IReadOnlyList<MissionEntry> index, int id, Func<int, bool>? hidden = null)
    {
        var e = index.FirstOrDefault(x => x.Id == id);
        if (e == null) return null;
        var kind = KindOf(cfg, e);
        var group = kind == MissionGroupKind.Original ? Original(cfg, index, hidden)
            : Custom(cfg, index, hidden).FirstOrDefault(g => g.Missions.Any(m => m.Id == id))?.Missions ?? new List<MissionEntry>();
        int i = group.FindIndex(m => m.Id == id);
        return i >= 0 && i + 1 < group.Count ? group[i + 1] : null;
    }
}

/// <summary>a map of My maps as the custom campaigns' list shows it (map.lua only)</summary>
public sealed class MyMapInfo
{
    public string Path = "";
    /// <summary>its name below My maps ("Island", "Island.pwmap", "sub/Island") - what campaign.lua names it by</summary>
    public string Ref = "";
    public string Name = "", Description = "", Author = "", Requires = "";
    /// <summary>the map's mission table (map.lua `mission`): key, title, description, tribe of a campaign mission</summary>
    public string MissionKey = "", MissionTitle = "", MissionDescription = "", MissionTribe = "";
    /// <summary>an edited copy of a campaign mission (its mission table names the original's .ula map)</summary>
    public bool Edited;
    /// <summary>the original mission of an edited copy, when the configuration has it</summary>
    public MissionEntry? Original;

    /// <summary>"Mission 5: The Holy City (edited)" for an edited copy (the original's title), else the map's name</summary>
    public string Title => Edited ? $"{(Original?.Title is { Length: > 0 } t ? t.Trim() : MissionTitle.Length > 0 ? MissionTitle : Name)} (edited)" : Name;

    static readonly string[] EmptyObjects = PwMapReader.MissionObjectPaths.Select(p => "mission." + p)
        .Concat(new[] { "info", "settings_flat", "terrain", "terrain.biomes", "terrain.biomes.remap", "mission" }).ToArray();

    /// <summary>reads map.lua of a map (a folder or .pwmap); index: the configuration's missions (for an edited copy's original)</summary>
    public static MyMapInfo Read(string path, IReadOnlyList<MissionEntry>? index = null, string? userDir = null)
    {
        var src = new PwMapReader.Source(path);
        var text = src.Text(PwMapPaths.Map) ?? throw new FileNotFoundException($"{path}: no {PwMapPaths.Map}");
        var root = LuaData.ParseJson(text, src.Name + "/" + PwMapPaths.Map, EmptyObjects) as JsonObject ?? throw new InvalidDataException($"{src.Name}/map.lua: not a table");
        var dir = userDir ?? MapFiles.UserDir;
        var info = new MyMapInfo
        {
            Path = path, Ref = RefOf(path, dir),
            Name = root["title"].Str(root["name"].Str(System.IO.Path.GetFileNameWithoutExtension(src.Name))),
            Description = root["description"].Str(), Author = root["author"].Str(), Requires = root["requires"].Str(),
        };
        if (root["mission"] is JsonObject m)
        {
            info.MissionKey = m["key"].Str(); info.MissionTitle = m["title"].Str(); info.MissionDescription = m["description"].Str(); info.MissionTribe = m["tribe"].Str();
            // the converter's mission table names the original map (.ula); Test map's and a new map's have none
            info.Edited = info.MissionKey.Length > 0 && m["map"].Str().EndsWith(MapFiles.UlaExt, StringComparison.OrdinalIgnoreCase);
            if (info.Edited) info.Original = index?.FirstOrDefault(e => string.Equals(e.Key, info.MissionKey, StringComparison.OrdinalIgnoreCase));
        }
        return info;
    }

    /// <summary>a map's name below My maps (its path relative to it, with /), else its file name</summary>
    public static string RefOf(string path, string? userDir)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (userDir != null)
        {
            var u = System.IO.Path.GetFullPath(userDir).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (full.StartsWith(u, StringComparison.OrdinalIgnoreCase)) return full[u.Length..].Replace('\\', '/');
        }
        return System.IO.Path.GetFileName(full);
    }
}

/// <summary>where a mission of a user campaign is</summary>
public enum CampaignRefKind { Missing, Mission, MyMap, Bundled }

public sealed record CampaignMissionRef(string Ref, CampaignRefKind Kind, MissionEntry? Mission = null, string? MapPath = null);

/// <summary>a user campaign (a folder with campaign.lua)</summary>
public sealed class UserCampaign
{
    static readonly Logger L = Log.For("Campaign.Custom");
    public const string FileName = "campaign.lua", Ext = ".pwcampaign", FormatName = "pw-campaign", MapsFolder = "maps";
    public const int FormatVersion = 1;

    static string? s_dir;
    /// <summary>the user's campaigns folder (the game: user://campaigns; PW_USER_CAMPAIGNS overrides it; null: none)</summary>
    public static string? Dir
    {
        get => Environment.GetEnvironmentVariable("PW_USER_CAMPAIGNS") is { Length: > 0 } e ? e : s_dir;
        set => s_dir = value;
    }

    /// <summary>the campaign's folder (its id: the folder's name)</summary>
    public string Folder = "";
    public string Id => System.IO.Path.GetFileName(Folder.TrimEnd('/', '\\'));
    public string Name = "", Description = "", Author = "";
    /// <summary>the missions in playing order: map names of My maps (or of the campaign's own maps/) or keys of the configuration's missions</summary>
    public List<string> Missions = new();

    // ------------------------------------------------------------------------------------------- reading and writing
    /// <summary>every campaign of the folder (folders with campaign.lua, by name); a damaged one is left out (logged)</summary>
    public static List<UserCampaign> List(string? dir = null)
    {
        dir ??= Dir;
        var res = new List<UserCampaign>();
        if (dir == null || !Directory.Exists(dir)) return res;
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(System.IO.Path.Combine(d, FileName))) continue;
            try { res.Add(Read(d)); }
            catch (Exception e) { L.Warn($"campaign {d}: {e.Message}"); }
        }
        return res;
    }

    public static UserCampaign Read(string folder)
    {
        var c = Parse(File.ReadAllText(System.IO.Path.Combine(folder, FileName), Encoding.UTF8), System.IO.Path.GetFileName(folder.TrimEnd('/', '\\')) + "/" + FileName);
        c.Folder = folder;
        return c;
    }

    /// <summary>campaign.lua's text (LuaData: data only) as a campaign (no folder)</summary>
    public static UserCampaign Parse(string lua, string chunk)
    {
        var root = LuaData.ParseJson(lua, chunk) as JsonObject ?? throw new LuaDataException($"{chunk}: not a table");
        if (root["format"] is JsonNode f && f.Str() != FormatName) throw new LuaDataException($"{chunk}: format is \"{f.Str()}\", not \"{FormatName}\"");
        if (root["version"].Int(1) > FormatVersion) throw new LuaDataException($"{chunk}: version {root["version"].Int()} - made by a newer remake");
        var c = new UserCampaign { Name = root["name"].Str(), Description = root["description"].Str(), Author = root["author"].Str() };
        if (root["missions"] is JsonArray ms) c.Missions = ms.Select(x => x.Str()).Where(x => x.Length > 0).ToList();
        else if (root["missions"] != null) throw new LuaDataException($"{chunk}: missions is not a list of names");
        return c;
    }

    public string ToLua()
    {
        var o = new JsonObject
        {
            ["format"] = FormatName, ["version"] = FormatVersion, ["name"] = Name, ["description"] = Description, ["author"] = Author,
            ["missions"] = new JsonArray(Missions.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
        };
        return LuaData.Write(o, "A campaign of the ParaWorld Remake (docs/CAMPAIGN.md \"Custom campaigns\"). Data only: no functions.\n" +
                                "missions: in playing order - maps of My maps by name, or the configuration's missions by key (single_05 ...).");
    }

    /// <summary>writes campaign.lua (a new campaign: a free folder in dir named after it); the folder</summary>
    public string Save(string? dir = null)
    {
        if (Problems(false).FirstOrDefault() is { } p) throw new InvalidOperationException(p);
        if (Folder == "")
        {
            dir ??= Dir ?? throw new InvalidOperationException("no campaigns folder");
            Folder = FreeFolder(dir, Name);
        }
        Directory.CreateDirectory(Folder);
        var tmp = System.IO.Path.Combine(Folder, FileName + ".tmp");
        File.WriteAllText(tmp, ToLua(), new UTF8Encoding(false));
        File.Move(tmp, System.IO.Path.Combine(Folder, FileName), true);
        L.Info($"campaign \"{Name}\" saved: {Folder} ({Missions.Count} missions)");
        return Folder;
    }

    public void Delete()
    {
        if (Folder != "" && Directory.Exists(Folder)) Directory.Delete(Folder, true);
        L.Info($"campaign \"{Name}\" deleted: {Folder}");
    }

    /// <summary>a folder name of a campaign's name (letters, digits, space, - _ . kept)</summary>
    public static string SafeName(string name)
    {
        var s = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_').ToArray()).Trim('.', ' ');
        return s.Length == 0 ? "campaign" : s.Length > 60 ? s[..60] : s;
    }

    /// <summary>a folder in dir not taken yet: "name", "name (2)" ...</summary>
    public static string FreeFolder(string dir, string name)
    {
        var safe = SafeName(name);
        var f = System.IO.Path.Combine(dir, safe);
        for (int k = 2; Directory.Exists(f) || File.Exists(f); k++) f = System.IO.Path.Combine(dir, $"{safe} ({k})");
        return f;
    }

    // ------------------------------------------------------------------------------------------- the missions
    /// <summary>where a mission name is: the campaign's own maps/ (an imported campaign's), My maps, the configuration's
    /// missions (by key)</summary>
    public CampaignMissionRef Resolve(string r, IReadOnlyList<MissionEntry> index, string? userDir = null)
    {
        if (Folder != "" && MapIn(System.IO.Path.Combine(Folder, MapsFolder), r) is { } b) return new(r, CampaignRefKind.Bundled, MapPath: b);
        if ((userDir ?? MapFiles.UserDir) is { } u && MapIn(u, r) is { } p) return new(r, CampaignRefKind.MyMap, MapPath: p);
        if (index.FirstOrDefault(e => string.Equals(e.Key, r, StringComparison.OrdinalIgnoreCase)) is { } m) return new(r, CampaignRefKind.Mission, m);
        return new(r, CampaignRefKind.Missing);
    }

    /// <summary>a map named r in a folder: the folder r (with map.lua) or the file r / r.pwmap; never outside the folder</summary>
    static string? MapIn(string dir, string r)
    {
        if (r.Length == 0 || r.Contains("..") || System.IO.Path.IsPathRooted(r) || !Directory.Exists(dir)) return null;
        var p = System.IO.Path.Combine(dir, r.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (Directory.Exists(p) && File.Exists(System.IO.Path.Combine(p, PwMapPaths.Map))) return p;
        if (File.Exists(p) && p.EndsWith(PwMapPaths.Ext, StringComparison.OrdinalIgnoreCase)) return p;
        if (File.Exists(p + PwMapPaths.Ext)) return p + PwMapPaths.Ext;
        return null;
    }

    /// <summary>what is wrong with the campaign: no name, no missions; with the configuration: a mission that is not
    /// there, a map that needs another configuration (map.lua requires)</summary>
    public List<string> Problems(bool missions = true, GameConfiguration? cfg = null, IReadOnlyList<MissionEntry>? index = null, string? userDir = null)
    {
        var res = new List<string>();
        if (Name.Trim().Length == 0) res.Add("The campaign has no name.");
        if (Missions.Count == 0) res.Add("The campaign has no missions.");
        if (!missions || index == null) return res;
        for (int i = 0; i < Missions.Count; i++)
        {
            var r = Resolve(Missions[i], index, userDir);
            if (r.Kind == CampaignRefKind.Missing) res.Add($"Mission {i + 1}: \"{Missions[i]}\" is neither a map of My maps nor a mission of this configuration.");
            else if (cfg != null && r.MapPath != null)
                try { if (MapFiles.RequirementProblem(cfg, MyMapInfo.Read(r.MapPath, index, userDir).Requires) is { } q) res.Add($"Mission {i + 1}: \"{Missions[i]}\" {q}."); }
                catch (Exception e) { res.Add($"Mission {i + 1}: \"{Missions[i]}\" cannot be read: {e.Message}"); }
        }
        return res;
    }

    // ------------------------------------------------------------------------------------------- progress
    /// <summary>is mission i (0-based) open when `won` missions were won in order (the first is always open)</summary>
    public static bool IsOpen(int i, int won) => i >= 0 && i <= won;
    /// <summary>the number won after mission i was won (never less than before)</summary>
    public static int AfterWin(int i, int won) => Math.Max(won, i + 1);
    /// <summary>the mission to play next (the first one not won yet; the last when all were won)</summary>
    public int Current(int won) => Missions.Count == 0 ? -1 : Math.Clamp(won, 0, Missions.Count - 1);

    // ------------------------------------------------------------------------------------------- sharing
    /// <summary>the campaign as one .pwcampaign file: campaign.lua and maps/&lt;name&gt;.pwmap of every map it plays (of its
    /// own maps/ or My maps); the configuration's missions are not packed. The packed maps' names.</summary>
    public List<string> Export(string file, IReadOnlyList<MissionEntry> index, string? userDir = null)
    {
        var packed = new List<string>();
        var tmp = file + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Put(zip, FileName, Encoding.UTF8.GetBytes(ToLua()));
            foreach (var r in Missions.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var res = Resolve(r, index, userDir);
                if (res.MapPath == null) continue;
                var name = r.EndsWith(PwMapPaths.Ext, StringComparison.OrdinalIgnoreCase) ? r : r + PwMapPaths.Ext;
                Put(zip, MapsFolder + "/" + name, PwmapBytes(res.MapPath));
                packed.Add(r);
            }
        }
        File.Move(tmp, file, true);
        L.Info($"campaign \"{Name}\" exported to {file} ({packed.Count} maps)");
        return packed;
    }

    static void Put(ZipArchive zip, string name, byte[] data)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        e.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var s = e.Open();
        s.Write(data);
    }

    /// <summary>a map as .pwmap bytes (a .pwmap file as it is; a folder zipped: its files, temporaries left out)</summary>
    public static byte[] PwmapBytes(string mapPath)
    {
        if (File.Exists(mapPath)) return File.ReadAllBytes(mapPath);
        var src = new PwMapReader.Source(mapPath);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var rel in src.List().Where(r => !r.EndsWith(".tmp", StringComparison.Ordinal)))
                Put(zip, rel, src.Get(rel) ?? Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>largest file of a .pwcampaign that is unpacked</summary>
    public const long MaxEntry = 256L << 20;

    /// <summary>a .pwcampaign unpacked into a new folder of dir (a free name after the campaign's name): campaign.lua
    /// (checked: data only) and its maps (each read once - a damaged map refuses the file); other files of the zip are
    /// left out. The campaign.</summary>
    public static UserCampaign Import(string file, string? dir = null)
    {
        dir ??= Dir ?? throw new InvalidOperationException("no campaigns folder");
        using var za = ZipFile.OpenRead(file);
        var main = za.Entries.FirstOrDefault(e => e.FullName == FileName) ?? throw new InvalidDataException($"{System.IO.Path.GetFileName(file)}: no {FileName} - not a campaign");
        var c = Parse(Encoding.UTF8.GetString(Read(main)).TrimStart('﻿'), System.IO.Path.GetFileName(file) + "/" + FileName);
        if (c.Name.Trim().Length == 0) c.Name = System.IO.Path.GetFileNameWithoutExtension(file);
        var maps = new List<(string Rel, byte[] Data)>();
        foreach (var e in za.Entries)
        {
            var rel = e.FullName.Replace('\\', '/');
            if (!rel.StartsWith(MapsFolder + "/", StringComparison.Ordinal) || !rel.EndsWith(PwMapPaths.Ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Contains("..") || rel.Contains(':') || rel.Split('/').Any(p => p.Length == 0)) throw new InvalidDataException($"{System.IO.Path.GetFileName(file)}: a file outside the campaign: {rel}");
            maps.Add((rel, Read(e)));
        }
        Directory.CreateDirectory(dir);
        var folder = FreeFolder(dir, c.Name);
        var tmp = folder + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        try
        {
            Directory.CreateDirectory(tmp);
            foreach (var (rel, data) in maps)
            {
                var p = System.IO.Path.Combine(tmp, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, data);
                PwMapReader.ReadHeader(p);                       // (a damaged map: the import is refused)
            }
            c.Folder = tmp;
            c.Save();
            Directory.Move(tmp, folder);
            c.Folder = folder;
        }
        catch
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            throw;
        }
        L.Info($"campaign \"{c.Name}\" imported from {file}: {folder} ({maps.Count} maps)");
        return c;
    }

    static byte[] Read(ZipArchiveEntry e)
    {
        if (e.Length > MaxEntry) throw new InvalidDataException($"{e.FullName}: too large ({e.Length} bytes)");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
