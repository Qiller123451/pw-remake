// Reads a map of the remake's format (docs/MAP_FORMAT.md) - a map folder (map.lua, terrain/*.png ...) or the same
// files in a .pwmap zip - into the map document (PwMap). The Lua files are data (LuaData: checked, then evaluated by
// MoonSharp in an empty environment); the PNGs go through Png.
//
//   PwMapReader.IsMap(path)        a folder with map.lua, or a .pwmap file
//   PwMapReader.Read(path)         everything
//   PwMapReader.ReadHeader(path)   map.lua, the terrain's size and the minimap only (the map list)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

public static class PwMapReader
{
    static readonly Logger L = Log.For("Maps.PwMap");

    /// <summary>the paths of a mission's data (mission and world) whose empty table is an object, not a list
    /// (LuaData.ParseJson; * = any key or index) - the maps of the toolkit's mission export</summary>
    public static readonly string[] MissionObjectPaths =
    {
        "map_info", "map_info.*", "players.*.resources", "players.*.ai_difficulty", "players.*.point_buy", "players.*.point_buy.preset",
        "players.*.point_buy.tribes", "players.*.point_buy.tribes.*", "players.*.allowed_units", "players.*.allowed_units.infantry",
        "players.*.allowed_units.cavalry", "players.*.heroes", "players.*.heroes.*", "counts",
    };
    public static readonly string[] WorldObjectPaths =
    {
        "region_tree", "variables", "dialogs", "dialogs.*", "dialogs.*.actors", "dialogs.*.actors.*", "sequences", "sequences.*",
        "sequences.*.play", "sequences.*.play.cams", "refs", "trigger_tree", "trigger_defaults", "trigger_defaults.conditions",
        "trigger_defaults.actions", "trigger_defaults.conditions.*", "trigger_defaults.actions.*", "objects.*.attr", "quests.*.attr",
    };

    // ------------------------------------------------------------------------------------------- the files of a map
    /// <summary>a map's files, from a folder or a zip</summary>
    public sealed class Source
    {
        public readonly string Path, Name;
        readonly Dictionary<string, byte[]>? _zip;
        public Source(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
            if (System.IO.File.Exists(path))
            {
                _zip = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                using var za = ZipFile.OpenRead(path);
                foreach (var e in za.Entries)
                {
                    if (e.FullName.EndsWith('/')) continue;
                    var rel = e.FullName.Replace('\\', '/');
                    if (rel.Contains("..")) throw new InvalidDataException($"{Name}: a file outside the map: {rel}");
                    using var s = e.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    _zip[rel] = ms.ToArray();
                }
                // a zip of the folder itself ("mymap/map.lua"): strip the one top folder
                if (!_zip.ContainsKey(PwMapPaths.Map))
                {
                    var top = _zip.Keys.FirstOrDefault(k => k.EndsWith("/" + PwMapPaths.Map, StringComparison.Ordinal) && k.Count(c => c == '/') == 1);
                    if (top != null)
                    {
                        var pre = top[..(top.IndexOf('/') + 1)];
                        _zip = _zip.Where(kv => kv.Key.StartsWith(pre, StringComparison.Ordinal)).ToDictionary(kv => kv.Key[pre.Length..], kv => kv.Value, StringComparer.Ordinal);
                    }
                }
            }
            else if (!Directory.Exists(path)) throw new FileNotFoundException($"no map at {path}");
        }
        public byte[]? Get(string rel)
        {
            if (_zip != null) return _zip.TryGetValue(rel, out var b) ? b : null;
            var p = System.IO.Path.Combine(Path, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return System.IO.File.Exists(p) ? System.IO.File.ReadAllBytes(p) : null;
        }
        public string? Text(string rel) => Get(rel) is { } b ? new UTF8Encoding(false).GetString(b).TrimStart('﻿') : null;
        /// <summary>every file (relative paths with /)</summary>
        public IEnumerable<string> List()
        {
            if (_zip != null) return _zip.Keys.OrderBy(k => k, StringComparer.Ordinal);
            return Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(Path, f).Replace('\\', '/')).OrderBy(k => k, StringComparer.Ordinal);
        }
    }

    public static bool IsMap(string path) =>
        Directory.Exists(path) ? System.IO.File.Exists(System.IO.Path.Combine(path, PwMapPaths.Map))
        : System.IO.File.Exists(path) && path.EndsWith(PwMapPaths.Ext, StringComparison.OrdinalIgnoreCase);

    /// <summary>the whole map; mission false: without the mission's world, texts, triggers and other files (the
    /// simulation's map source needs only the map)</summary>
    public static PwMap Read(string path, bool mission = true) => Read(new Source(path), header: false, withMission: mission);
    public static PwMap ReadHeader(string path) => Read(new Source(path), header: true);

    // ------------------------------------------------------------------------------------------- a mission's data alone
    /// <summary>map.lua's mission table of a mission folder (mission.json of the old layout), or null</summary>
    public static System.Text.Json.Nodes.JsonNode? MissionOf(string folder)
    {
        var f = System.IO.Path.Combine(folder, PwMapPaths.Map);
        if (!System.IO.File.Exists(f)) return null;
        var root = LuaData.ReadJsonFile(f, System.IO.Path.GetFileName(folder.TrimEnd('/', '\\')) + "/" + PwMapPaths.Map, MissionObjectPaths.Select(p => "mission." + p).Append("mission").ToArray());
        return root?["mission"];
    }

    /// <summary>world.lua of a mission folder (world.json of the old layout), or null</summary>
    public static System.Text.Json.Nodes.JsonNode? WorldOf(string folder)
    {
        var f = System.IO.Path.Combine(folder, PwMapPaths.World);
        return System.IO.File.Exists(f) ? LuaData.ReadJsonFile(f, System.IO.Path.GetFileName(folder.TrimEnd('/', '\\')) + "/" + PwMapPaths.World, WorldObjectPaths) : null;
    }

    /// <summary>the texts of a map folder in a language (texts/&lt;lang&gt;.lua), else English (uk), else the first
    /// language there is; empty when it has none</summary>
    public static Dictionary<string, string> TextsOf(string folder, string lang)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        var td = System.IO.Path.Combine(folder, "texts");
        if (!Directory.Exists(td)) return res;
        var f = new[] { lang, "uk" }.Select(l => System.IO.Path.Combine(td, l + ".lua")).FirstOrDefault(System.IO.File.Exists)
                ?? Directory.GetFiles(td, "*.lua").OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
        if (f != null && LuaData.ReadJsonFile(f, "texts/" + System.IO.Path.GetFileName(f)) is System.Text.Json.Nodes.JsonObject tx)
            foreach (var (k, v) in tx) res[k] = v.Str();
        return res;
    }

    /// <summary>a mission folder of the old layout (mission.json, no map.lua): made before the map format - Prepare
    /// must convert it</summary>
    public static bool IsOldMissionFolder(string folder) =>
        !System.IO.File.Exists(System.IO.Path.Combine(folder, PwMapPaths.Map)) && System.IO.File.Exists(System.IO.Path.Combine(folder, "mission.json"));

    // ------------------------------------------------------------------------------------------- reading
    static JsonNode? Lua(Source s, string rel, IReadOnlyCollection<string>? objectPaths = null)
    {
        var t = s.Text(rel);
        return t == null ? null : LuaData.ParseJson(t, s.Name + "/" + rel, objectPaths);
    }

    static double D(JsonNode? n, double def) => n is JsonValue ? n.Num(def) : def;
    static float F(JsonNode? n) => (float)n.Num(0);
    static string? S(JsonNode? n) => n is JsonValue v ? v.Str() : null;

    static InfoNode TreeOf(JsonNode n, string where)
    {
        if (n is not JsonArray a || a.Count < 2) throw new InvalidDataException($"{where}: a settings node is {{ name, value [, {{ children }}] }}");
        var node = new InfoNode { Name = a[0].Str(), Value = a[1].Str() };
        if (a.Count > 2 && a[2] is JsonArray subs) foreach (var c in subs) node.Subs.Add(TreeOf(c!, where));
        return node;
    }

    static readonly Regex NumberSuffix = new(@"_[0-9]+\z", RegexOptions.CultureInvariant);

    public static PwMap Read(Source s, bool header, bool withMission = true)
    {
        var mapText = s.Text(PwMapPaths.Map) ?? throw new FileNotFoundException($"{s.Path}: no {PwMapPaths.Map} - not a map of the remake's format");
        var root = LuaData.ParseJson(mapText, s.Name + "/" + PwMapPaths.Map, MissionObjectPaths.Select(p => "mission." + p).Concat(new[] { "info", "settings_flat", "terrain", "terrain.biomes", "terrain.biomes.remap", "mission" }).ToArray()) as JsonObject
                   ?? throw new InvalidDataException($"{s.Name}/map.lua: not a table");
        if (root["format"].Str() != PwMap.FormatName) throw new InvalidDataException($"{s.Name}/map.lua: format is \"{root["format"].Str()}\", not \"{PwMap.FormatName}\"");
        if (root["version"].Int(1) > PwMap.FormatVersion) throw new InvalidDataException($"{s.Name}/map.lua: version {root["version"].Int()} - made by a newer remake (this one reads up to {PwMap.FormatVersion})");
        var m = new PwMap();
        // ---- map.lua
        if (root["info"] is JsonObject info) foreach (var (k, v) in info) m.Info[k] = v.Str();
        if (root["settings"] is JsonNode st) m.Settings = TreeOf(st, s.Name + "/map.lua settings");
        else if (root["settings_flat"] is JsonObject fl) { m.FlatSettings = new(); foreach (var (k, v) in fl) m.FlatSettings[k] = v.Str(); }
        var desc = m.DescriptionFlat();
        string? Info(string k) => m.Info.TryGetValue(k, out var v) ? v : null;
        m.Source = S(root["source"]) ?? "";
        m.File = S(root["file"]) ?? "";
        m.Requires = S(root["requires"]) ?? "";
        m.Title = S(root["title"]); m.Description = S(root["description"]); m.Author = S(root["author"]);
        if (root["size"] is JsonArray size && size.Count == 2) { m.W = size[0].Int(); m.H = size[1].Int(); }
        m.TerrainSetting = S(root["terrain_setting"]) ?? S(root["setting"]) ?? "Jungle";
        // (what map.lua leaves out comes from the level info and settings, as the .ula reader took it)
        string? set = Info("Setting");
        m.Setting = S(root["setting"]) ?? (set != null && Ula.KNOWN_SETTINGS.TryGetValue(set, out var ks) ? ks : Ula.KNOWN_SETTINGS.TryGetValue(m.TerrainSetting, out var kt) ? kt : "Jungle");
        m.SettingName = S(root["setting_name"]) ?? m.Setting;
        m.Water = D(root["water"], 16);
        m.StartTime = root["start_time"] is JsonValue stv ? Ula.DayTime(stv.Str()) : Ula.DayTime(desc.TryGetValue("StartTime", out var dst) ? dst : null);
        m.DayNight = World.DayNightSpec.FromJson(root["day_night"]);
        string? cam = desc.TryGetValue("DefaultCamera", out var cv) ? cv : null;
        m.DefaultCamera = root["camera"] is JsonValue ? root["camera"].Num() : cam != null && cam != "" && double.IsFinite(JsNum.ToNumber(cam)) ? JsNum.ToNumber(cam) : 0.785;
        m.MaxPlayers = root["max_players"] is JsonValue ? root["max_players"].Num() : JsNum.Or(Info("MaxPlayers"), JsNum.Or(Info("StartLocations"), 2));
        string? ln = Info("LevelName"), mn = Info("MapName");
        m.Name = S(root["name"]) ?? (!string.IsNullOrEmpty(ln) ? ln : !string.IsNullOrEmpty(mn) ? mn : Path.GetFileNameWithoutExtension(m.File.Length > 0 ? m.File : s.Name));
        var terr = root["terrain"] as JsonObject ?? new JsonObject();
        m.HeightMin = D(terr["height_min"], 0);
        m.HeightStep = D(terr["height_step"], PwMap.DefaultHeightStep);
        m.Materials = terr["materials"] is JsonArray mats && mats.Count > 0 ? mats.Select(x => x.Str()).ToList() : PwMap.DefaultMaterials(m.Setting);
        if (root["mission"] is JsonObject mis) m.Mission = mis;
        // ---- the terrain's size; the minimap
        var hb = s.Get(PwMapPaths.Height) ?? throw new FileNotFoundException($"{s.Name}: no {PwMapPaths.Height}");
        if (s.Get(PwMapPaths.Minimap) is { } mm)
            try { var img = Png.Read(mm); m.Preview = new MapPreview { W = img.W, H = img.H, Rgba = img.Rgba8() }; }
            catch (Exception e) { L.Warn($"{s.Name}/{PwMapPaths.Minimap}: {e.Message}"); }
        if (header)
        {
            (m.Hx, m.Hy) = Png.ReadSize(hb);
            return m;
        }
        // ---- terrain
        var h = Png.Read(hb);
        m.Hx = h.W; m.Hy = h.H;
        var hv = h.Grey16();
        m.Heights = new float[hv.Length];
        for (int i = 0; i < hv.Length; i++) m.Heights[i] = (float)(m.HeightMin + hv[i] * m.HeightStep);
        if (s.Get(PwMapPaths.Materials) is { } mb) { var mi = Png.Read(mb); m.Mx = mi.W; m.My = mi.H; m.Mats = mi.Grey8(); }
        else { m.Mx = m.Hx / 2; m.My = m.Hy / 2; m.Mats = new byte[m.Mx * m.My]; }
        if (s.Get(PwMapPaths.Walk) is { } wb)
        {
            var wi = Png.Read(wb);
            var wv = wi.Grey8();
            m.Walk = new WalkGrid { W = wi.W, H = wi.H, Cells = wv.Select(v => v >= 128 ? (byte)1 : (byte)0).ToArray() };
            // (the map editor's painted cells: 192 walkable, 64 blocked)
            if (wv.Any(v => v is 64 or 192)) m.Walk.Paint = wv.Select(v => v == 192 ? (byte)1 : v == 64 ? (byte)2 : (byte)0).ToArray();
        }
        if (terr["biomes"] is JsonObject bo && bo["names"] is JsonArray bn && bn.Count > 0)
        {
            var layer = new BiomeLayer { Biomes = bn.Select(x => x.Str()).ToList(), Cell = D(bo["cell"], 8), X0 = D(bo["x0"], 0), Y0 = D(bo["y0"], 0) };
            int nb = layer.Biomes.Count;
            for (int b = 0; b < nb; b++)
            {
                var pb = s.Get(PwMapPaths.Biome(layer.Biomes[b])) ?? throw new FileNotFoundException($"{s.Name}: no {PwMapPaths.Biome(layer.Biomes[b])}");
                var bi = Png.Read(pb);
                if (b == 0) { layer.W = bi.W; layer.H = bi.H; layer.Weights = new byte[bi.W * bi.H * nb]; }
                else if (bi.W != layer.W || bi.H != layer.H) throw new InvalidDataException($"{s.Name}: the biome pictures differ in size");
                var px = bi.Grey8();
                for (int k = 0; k < px.Length; k++) layer.Weights[k * nb + b] = px[k];
            }
            if (bo["remap"] is JsonObject rm) foreach (var (k, v) in rm) if (v is JsonArray ra) layer.Remap[k] = ra.Select(x => x.Int()).ToArray();
            m.Biomes = layer;
        }
        if (Lua(s, PwMapPaths.Forest) is JsonObject fo)
        {
            m.Forest = new ForestMap { W = fo["w"].Int(), H = fo["h"].Int() };
            foreach (var b in fo["blocks"] as JsonArray ?? new JsonArray())
                if (b is JsonArray ba && ba.Count >= 3 && ba[2] is JsonArray items)
                {
                    var bytes = new byte[Forest.ITEMS];
                    for (int i = 0; i < Math.Min(Forest.ITEMS, items.Count); i++) bytes[i] = (byte)items[i].Int();
                    m.Forest.Blocks.Add(new ForestBlock { X = ba[0].Int(), Y = ba[1].Int(), B = bytes });
                }
        }
        if (Lua(s, PwMapPaths.Plants) is JsonObject po)
        {
            var names = (po["names"] as JsonArray ?? new JsonArray()).Select(x => x.Str()).ToList();
            foreach (var p in po["plants"] as JsonArray ?? new JsonArray())
            {
                if (p is not JsonArray pa || pa.Count < 6) continue;
                int k = pa[0].Int();
                double qz = F(pa[4]), qw = F(pa[5]), qx = pa.Count >= 8 ? F(pa[6]) : 0, qy = pa.Count >= 8 ? F(pa[7]) : 0;
                m.Plants.Add(new MapPlant
                {
                    Name = k >= 1 && k <= names.Count ? names[k - 1] : "", X = F(pa[1]), Y = F(pa[2]), Z = F(pa[3]),
                    Rot = -2 * SMath.Atan2(qz, qw), Q = new[] { qx, qy, qz, qw },
                });
            }
        }
        if (Lua(s, PwMapPaths.Objects) is JsonArray oa)
            for (int i = 0; i < oa.Count; i++)
            {
                if (oa[i] is not JsonObject o) continue;
                var q = o["q"] as JsonArray;
                double[] qq = q != null && q.Count == 4 ? new double[] { F(q[0]), F(q[1]), F(q[2]), F(q[3]) } : new double[] { 0, 0, 0, 1 };
                var name = o["name"].Str();
                var mo = new MapObject
                {
                    Type = o["type"].Str(), Name = name, Cls = S(o["class"]) ?? NumberSuffix.Replace(name, "", 1), X = F(o["x"]), Y = F(o["y"]), Z = F(o["z"]),
                    Q = qq, Rot = -2 * SMath.Atan2(qq[2], qq[3]), Owner = o["owner"] is JsonValue ov ? ov.Int() : null, At = i, Level = o["level"].Int(0),
                };
                if (o["attr"] is JsonObject at) foreach (var (k, v) in at) mo.Attr[k] = v.Str();
                m.Objects.Add(mo);
            }
        if (Lua(s, PwMapPaths.Regions) is JsonArray ra2)
            foreach (var r in ra2)
            {
                if (r is not JsonObject ro) continue;
                var reg = new MapRegion { Name = ro["name"].Str() };
                foreach (var sh in ro["shapes"] as JsonArray ?? new JsonArray())
                    if (sh is JsonArray sa && sa.Count >= 5)
                        reg.Shapes.Add(new RegionShape { Oval = sa[0].Str() == "oval", X = F(sa[1]), Y = F(sa[2]), W = F(sa[3]), H = F(sa[4]), Enabled = sa.Count < 6 || sa[5].Bool(true) });
                m.Regions.Add(reg);
            }
        // ---- the mission
        if (!withMission) return m;
        m.World = Lua(s, PwMapPaths.World, WorldObjectPaths);
        m.Triggers = s.Text(PwMapPaths.Triggers);
        m.Custom = s.Text(PwMapPaths.Custom);
        foreach (var rel in s.List())
        {
            if (rel.StartsWith("texts/", StringComparison.Ordinal) && rel.EndsWith(".lua", StringComparison.Ordinal) && rel.Count(c => c == '/') == 1)
            {
                if (Lua(s, rel) is JsonObject tx) m.Texts[rel[6..^4]] = tx;
                else m.Texts[rel[6..^4]] = new JsonObject();
                continue;
            }
            if (Known(rel)) continue;
            m.Files[rel] = s.Get(rel)!;
        }
        L.Debug($"{s.Name}: {m.Name}, {m.Hx * 2}x{m.Hy * 2} m, {m.Setting}, {m.Objects.Count} objects, {m.Plants.Count} plants, {m.Forest.Blocks.Count} forest blocks, {m.Regions.Count} regions{(m.IsMission ? ", a mission" : "")}");
        return m;
    }

    /// <summary>a file the format itself reads (not kept among PwMap.Files); also the old mission layout's and temporaries</summary>
    static bool Known(string rel) =>
        rel is PwMapPaths.Map or PwMapPaths.Height or PwMapPaths.Materials or PwMapPaths.Walk or PwMapPaths.Forest or PwMapPaths.Plants
            or PwMapPaths.Objects or PwMapPaths.Regions or PwMapPaths.Minimap or PwMapPaths.World or PwMapPaths.Triggers or PwMapPaths.Custom
            or "mission.json" or "world.json"
        || (rel.StartsWith("terrain/biomes/", StringComparison.Ordinal) && rel.EndsWith(".png", StringComparison.Ordinal))
        || (rel.StartsWith("texts/", StringComparison.Ordinal) && rel.EndsWith(".json", StringComparison.Ordinal))
        || rel.EndsWith(".tmp", StringComparison.Ordinal);
}
