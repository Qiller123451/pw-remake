// Writes a map document (PwMap) as a map folder or a .pwmap zip (docs/MAP_FORMAT.md). Deterministic: the same map gives
// the same bytes (fixed key order, numbers as the shortest exact text, PNGs by Png's fixed rules, zip entries sorted
// with a fixed time stamp).
//
//   PwMapWriter.Files(map)                 relative path -> bytes of every file of the map
//   PwMapWriter.WriteFolder(map, dir)      writes them (each file through a temporary file), removes the format's
//                                          files the map no longer has (and, removeOldLayout, the old mission
//                                          layout's mission.json / world.json / texts/*.json); custom.lua is never removed
//   PwMapWriter.WriteZip(map, path)        the same files in one zip
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace PwRemake.Sim.Maps;

public static class PwMapWriter
{
    static readonly UTF8Encoding Utf8 = new(false);
    /// <summary>the zip entries' time stamp (a fixed one: the same map, the same zip)</summary>
    static readonly DateTimeOffset ZipTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static string F(double v) => LuaData.FloatText(v);
    static string N(double v) => LuaData.NumText(v);

    public static SortedDictionary<string, byte[]> Files(PwMap m)
    {
        var f = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        void Text(string path, string s) => f[path] = Utf8.GetBytes(s);
        // ---- terrain
        var (hmin, hstep, hv) = HeightValues(m);
        f[PwMapPaths.Height] = Png.WriteGrey16(m.Hx, m.Hy, hv);
        f[PwMapPaths.Materials] = Png.WriteGrey8(m.Mx, m.My, m.Mats.Length == m.Mx * m.My ? m.Mats : new byte[m.Mx * m.My]);
        if (m.Walk is { } wg) f[PwMapPaths.Walk] = Png.WriteGrey8(wg.W, wg.H, WalkValues(wg));
        if (m.Biomes is { } bl)
        {
            int nb = bl.Biomes.Count;
            for (int b = 0; b < nb; b++)
            {
                var px = new byte[bl.W * bl.H];
                for (int k = 0; k < px.Length; k++) px[k] = bl.Weights[k * nb + b];
                f[PwMapPaths.Biome(bl.Biomes[b])] = Png.WriteGrey8(bl.W, bl.H, px);
            }
        }
        if (m.Forest.Blocks.Count > 0 || m.Forest.W > 0) Text(PwMapPaths.Forest, ForestLua(m.Forest));
        Text(PwMapPaths.Plants, PlantsLua(m.Plants));
        Text(PwMapPaths.Objects, ObjectsLua(m.Objects));
        Text(PwMapPaths.Regions, RegionsLua(m.Regions));
        if (m.Preview is { W: > 0, H: > 0 } p && p.Rgba.Length == p.W * p.H * 4) f[PwMapPaths.Minimap] = Png.WriteRgba8(p.W, p.H, p.Rgba);
        // ---- the mission
        if (m.World != null) Text(PwMapPaths.World, LuaData.Write(m.World, "The mission's world (docs/MAP_FORMAT.md, docs/LUA_MAPS.md): objects, groups, regions, quests, question marks,\nvariables, dialogues, sequences. Data only: no functions."));
        foreach (var (lang, tx) in m.Texts) Text(PwMapPaths.Texts(lang), LuaData.Write(tx, $"The map's texts in language \"{lang}\": key = text. Data only."));
        if (m.Triggers != null) Text(PwMapPaths.Triggers, m.Triggers);
        if (m.Custom != null) Text(PwMapPaths.Custom, m.Custom);
        foreach (var (path, bytes) in m.Files) if (!f.ContainsKey(path)) f[path] = bytes;
        // ---- map.lua last (it names the heights' range the PNG was written with)
        Text(PwMapPaths.Map, MapLua(m, hmin, hstep));
        return f;
    }

    /// <summary>walk.png's values: 255 walkable, 0 blocked; the map editor's painted cells 192 (painted walkable) and 64
    /// (painted blocked) - "128 and up = walkable" reads them right without knowing the paint</summary>
    public static byte[] WalkValues(WalkGrid wg)
    {
        var v = new byte[wg.Cells.Length];
        for (int k = 0; k < v.Length; k++)
        {
            byte p = wg.Paint != null && k < wg.Paint.Length ? wg.Paint[k] : (byte)0;
            v[k] = p == 1 ? (byte)192 : p == 2 ? (byte)64 : wg.Cells[k] == 1 ? (byte)255 : (byte)0;
        }
        return v;
    }

    /// <summary>the 16-bit values of the heights: with the map's HeightMin / HeightStep when every height is exactly a
    /// value of that range (every converted map: the original's 1/128 m), else a range made to fit (the nearest value)</summary>
    public static (double Min, double Step, ushort[] Values) HeightValues(PwMap m)
    {
        var hs = m.Heights;
        var v = new ushort[hs.Length];
        double min = m.HeightMin, step = m.HeightStep > 0 ? m.HeightStep : PwMap.DefaultHeightStep;
        bool exact = true;
        for (int i = 0; i < hs.Length && exact; i++)
        {
            double k = Math.Round((hs[i] - min) / step);
            if (k < 0 || k > 65535 || (float)(min + k * step) != hs[i]) exact = false;
            else v[i] = (ushort)k;
        }
        if (exact) return (min, step, v);
        double lo = hs.Length > 0 ? hs.Min() : 0, hi = hs.Length > 0 ? hs.Max() : 0;
        step = PwMap.DefaultHeightStep;
        min = Math.Floor(lo / step) * step;
        if ((hi - min) / step > 65535) step = (hi - min) / 65535;
        for (int i = 0; i < hs.Length; i++) v[i] = (ushort)Math.Clamp(Math.Round((hs[i] - min) / step), 0, 65535);
        return (min, step, v);
    }

    // ------------------------------------------------------------------------------------------- map.lua
    static JsonArray Tree(InfoNode n)
    {
        var a = new JsonArray { n.Name, n.Value };
        if (n.Subs.Count > 0) a.Add(new JsonArray(n.Subs.Select(s => (JsonNode)Tree(s)).ToArray()));
        return a;
    }

    static string MapLua(PwMap m, double hmin, double hstep)
    {
        var sb = new StringBuilder();
        sb.Append("-- A map of the ParaWorld Remake (docs/MAP_FORMAT.md). Data only: no functions.\n");
        sb.Append("return {\n");
        void Line(string key, string value) => sb.Append("  ").Append(key).Append(" = ").Append(value).Append(",\n");
        Line("format", LuaData.Str(PwMap.FormatName));
        Line("version", PwMap.FormatVersion.ToString(CultureInfo.InvariantCulture));
        Line("name", LuaData.Str(m.Name));
        if (m.Title != null) Line("title", LuaData.Str(m.Title));
        if (m.Description != null) Line("description", LuaData.Str(m.Description));
        if (m.Author != null) Line("author", LuaData.Str(m.Author));
        if (m.Source.Length > 0) Line("source", LuaData.Str(m.Source));
        if (m.Requires.Length > 0) Line("requires", LuaData.Str(m.Requires));
        if (m.File.Length > 0) Line("file", LuaData.Str(m.File));
        Line("size", $"{{ {m.W}, {m.H} }}");
        Line("setting", LuaData.Str(m.Setting));
        if (m.SettingName != m.Setting) Line("setting_name", LuaData.Str(m.SettingName));
        if (m.TerrainSetting != m.Setting) Line("terrain_setting", LuaData.Str(m.TerrainSetting));
        Line("water", N(m.Water));
        Line("start_time", LuaData.Str(DayTimeText(m.StartTime)));
        Line("camera", N(m.DefaultCamera));
        if (m.DayNight is { } dn)
            Line("day_night", $"{{ day = {N(dn.Day)}, dusk = {N(dn.Dusk)}, night = {N(dn.Night)}, dawn = {N(dn.Dawn)}, start = {N(dn.Start)}, night_sight = {N(dn.NightSight)}{(dn.FirstDay > 0 ? $", first_day = {N(dn.FirstDay)}" : "")} }}");
        Line("max_players", N(m.MaxPlayers));
        // terrain: the heights' range, the materials' textures, the biome layer's frame
        sb.Append("  terrain = {\n");
        sb.Append("    height_min = ").Append(N(hmin)).Append(", height_step = ").Append(N(hstep)).Append(",\n");
        sb.Append("    materials = { ").Append(string.Join(", ", m.Materials.Select(LuaData.Str))).Append(" },\n");
        if (m.Biomes is { } bl)
        {
            sb.Append("    biomes = {\n");
            sb.Append("      names = { ").Append(string.Join(", ", bl.Biomes.Select(LuaData.Str))).Append(" },\n");
            sb.Append("      cell = ").Append(N(bl.Cell)).Append(", x0 = ").Append(N(bl.X0)).Append(", y0 = ").Append(N(bl.Y0)).Append(",\n");
            if (bl.Remap.Count > 0)
            {
                sb.Append("      remap = {\n");
                foreach (var (k, r) in bl.Remap.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.Append("        ").Append(LuaData.Key(k)).Append(" = { ").Append(string.Join(", ", r)).Append(" },\n");
                sb.Append("      },\n");
            }
            sb.Append("    },\n");
        }
        sb.Append("  },\n");
        // the original's level info and settings tree
        var info = new JsonObject();
        foreach (var (k, v) in m.Info) info[k] = v;
        sb.Append("  info = ");
        LuaData.Value(sb, info, 1, "info");
        sb.Append(",\n");
        if (m.Settings != null)
        {
            sb.Append("  settings = ");
            LuaData.Value(sb, Tree(m.Settings), 1, "settings");
            sb.Append(",\n");
        }
        else if (m.FlatSettings != null)
        {
            var fl = new JsonObject();
            foreach (var (k, v) in m.FlatSettings) fl[k] = v;
            sb.Append("  settings_flat = ");
            LuaData.Value(sb, fl, 1, "settings_flat");
            sb.Append(",\n");
        }
        if (m.Mission != null)
        {
            sb.Append("  mission = ");
            LuaData.Value(sb, m.Mission, 1, "mission");
            sb.Append(",\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>seconds of the day as "h:mm" (Ula.DayTime reads it back to the same number)</summary>
    public static string DayTimeText(double s)
    {
        double h = Math.Floor(s / 3600), min = (s - h * 3600) / 60;
        string ms = min == Math.Floor(min) && min < 10 && min >= 0 ? "0" + N(min) : N(min);
        var t = N(h) + ":" + ms;
        return Ula.DayTime(t) == s ? t : N(h) + ":" + N(min);
    }

    // ------------------------------------------------------------------------------------------- the Lua files
    static string ObjectsLua(List<MapObject> objs)
    {
        var sb = new StringBuilder();
        sb.Append("-- The map's placed objects (docs/MAP_FORMAT.md \"objects.lua\"): type, name (the class with a number), position\n");
        sb.Append("-- (map metres: x east, y south, z up), q = the rotation quaternion { x, y, z, w }, owner (the start slot; none:\n");
        sb.Append("-- nobody's), attr = the object's attributes. Data only.\n");
        sb.Append("return {\n");
        foreach (var o in objs)
        {
            sb.Append("  { type = ").Append(LuaData.Str(o.Type)).Append(", name = ").Append(LuaData.Str(o.Name));
            // (a name that is not the class with a number - the map editor's names for the triggers - keeps its class)
            if (System.Text.RegularExpressions.Regex.Replace(o.Name, @"_[0-9]+\z", "") != o.Cls) sb.Append(", class = ").Append(LuaData.Str(o.Cls));
            sb.Append(", x = ").Append(F(o.X)).Append(", y = ").Append(F(o.Y)).Append(", z = ").Append(F(o.Z));
            sb.Append(", q = { ").Append(F(o.Q[0])).Append(", ").Append(F(o.Q[1])).Append(", ").Append(F(o.Q[2])).Append(", ").Append(F(o.Q[3])).Append(" }");
            if (o.Owner is int ow) sb.Append(", owner = ").Append(ow);
            if (o.Level != 0) sb.Append(", level = ").Append(o.Level);
            if (o.Attr.Count > 0)
            {
                sb.Append(", attr = { ");
                bool first = true;
                foreach (var (k, v) in o.Attr) { if (!first) sb.Append(", "); first = false; sb.Append(LuaData.Key(k)).Append(" = ").Append(LuaData.Str(v)); }
                sb.Append(" }");
            }
            sb.Append(" },\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    static bool PosZero(double d) => BitConverter.DoubleToInt64Bits(d) == 0;

    static string PlantsLua(List<MapPlant> plants)
    {
        var names = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in plants) if (!index.ContainsKey(p.Name)) { index[p.Name] = names.Count + 1; names.Add(p.Name); }
        var sb = new StringBuilder();
        sb.Append("-- The map's plants: landscape decoration instances (docs/MAP_FORMAT.md \"terrain/plants.lua\"). names = the models;\n");
        sb.Append("-- each plant { name number, x, y, z, qz, qw } (map metres; the rotation quaternion's z and w), or with qx, qy\n");
        sb.Append("-- after them for a tilted one. Data only.\n");
        sb.Append("return {\n  names = {\n");
        foreach (var n in names) sb.Append("    ").Append(LuaData.Str(n)).Append(",\n");
        sb.Append("  },\n  plants = {\n");
        foreach (var p in plants)
        {
            sb.Append("    { ").Append(index[p.Name]).Append(", ").Append(F(p.X)).Append(", ").Append(F(p.Y)).Append(", ").Append(F(p.Z));
            sb.Append(", ").Append(F(p.Q[2])).Append(", ").Append(F(p.Q[3]));
            if (!PosZero(p.Q[0]) || !PosZero(p.Q[1])) sb.Append(", ").Append(F(p.Q[0])).Append(", ").Append(F(p.Q[1]));
            sb.Append(" },\n");
        }
        sb.Append("  },\n}\n");
        return sb.ToString();
    }

    static string ForestLua(ForestMap fm)
    {
        var sb = new StringBuilder();
        sb.Append("-- The map's forest blocks (docs/MAP_FORMAT.md \"terrain/forest.lua\"): 32 m squares w x h; each block { x, y, items }\n");
        sb.Append("-- with 31 item bytes (15 trees, 16 undergrowth plants: bits 5-6 the state, 2 = standing; bits 0-4 the amount).\n");
        sb.Append("-- Where the items stand: the game's forest layouts (forest.json). Data only.\n");
        sb.Append("return {\n  w = ").Append(fm.W).Append(", h = ").Append(fm.H).Append(",\n  blocks = {\n");
        foreach (var b in fm.Blocks) sb.Append("    { ").Append(b.X).Append(", ").Append(b.Y).Append(", { ").Append(string.Join(", ", b.B)).Append(" } },\n");
        sb.Append("  },\n}\n");
        return sb.ToString();
    }

    static string RegionsLua(List<MapRegion> regions)
    {
        var sb = new StringBuilder();
        sb.Append("-- The map's regions (docs/MAP_FORMAT.md \"regions.lua\"): a name and shapes { \"rect\" | \"oval\", x, y, w, h } (the box\n");
        sb.Append("-- x .. x + w, y .. y + h in map metres; an oval is the ellipse in it; a 6th value false: switched off). Data only.\n");
        sb.Append("return {\n");
        foreach (var r in regions)
        {
            sb.Append("  { name = ").Append(LuaData.Str(r.Name)).Append(", shapes = {");
            for (int i = 0; i < r.Shapes.Count; i++)
            {
                var s = r.Shapes[i];
                sb.Append(i == 0 ? " " : ", ").Append("{ ").Append(s.Oval ? "\"oval\"" : "\"rect\"").Append(", ").Append(F(s.X)).Append(", ").Append(F(s.Y)).Append(", ").Append(F(s.W)).Append(", ").Append(F(s.H));
                if (!s.Enabled) sb.Append(", false");
                sb.Append(" }");
            }
            sb.Append(r.Shapes.Count > 0 ? " } },\n" : "} },\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------- folder and zip
    /// <summary>writes the map folder (see the file header); returns the files written</summary>
    public static SortedDictionary<string, byte[]> WriteFolder(PwMap m, string dir, bool removeOldLayout = false)
    {
        var files = Files(m);
        Directory.CreateDirectory(dir);
        foreach (var (rel, bytes) in files)
        {
            var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (System.IO.File.Exists(path) && SameBytes(path, bytes)) continue;
            var tmp = path + ".tmp";
            System.IO.File.WriteAllBytes(tmp, bytes);
            System.IO.File.Move(tmp, path, true);
        }
        // the format's files the map has no more
        void Drop(string rel) { if (!files.ContainsKey(rel)) { var p = Path.Combine(dir, rel); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } }
        foreach (var rel in new[] { PwMapPaths.Walk, PwMapPaths.Forest, PwMapPaths.Minimap, PwMapPaths.World, PwMapPaths.Triggers }) Drop(rel);
        foreach (var sub in new[] { "terrain/biomes", "texts" })
        {
            var d = Path.Combine(dir, sub);
            if (!Directory.Exists(d)) continue;
            foreach (var p in Directory.GetFiles(d))
            {
                var rel = sub + "/" + Path.GetFileName(p);
                bool ours = sub == "texts" ? p.EndsWith(".lua", StringComparison.Ordinal) : p.EndsWith(".png", StringComparison.Ordinal);
                if (ours && !files.ContainsKey(rel)) System.IO.File.Delete(p);
                else if (removeOldLayout && sub == "texts" && p.EndsWith(".json", StringComparison.Ordinal)) System.IO.File.Delete(p);
            }
        }
        if (removeOldLayout)
            foreach (var old in new[] { "mission.json", "world.json" }) { var p = Path.Combine(dir, old); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); }
        return files;
    }

    static bool SameBytes(string path, byte[] b)
    {
        var fi = new FileInfo(path);
        return fi.Length == b.Length && System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(b);
    }

    /// <summary>the map as one .pwmap zip (the folder's files, sorted, with a fixed time stamp)</summary>
    public static void WriteZip(PwMap m, string path)
    {
        var files = Files(m);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = System.IO.File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (rel, bytes) in files)
            {
                // (PNGs are compressed already)
                var e = zip.CreateEntry(rel, rel.EndsWith(".png", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.SmallestSize);
                e.LastWriteTime = ZipTime;
                using var s = e.Open();
                s.Write(bytes);
            }
        }
        System.IO.File.Move(tmp, path, true);
    }
}

/// <summary>The files of a map folder (docs/MAP_FORMAT.md).</summary>
public static class PwMapPaths
{
    public const string Map = "map.lua", Height = "terrain/height.png", Materials = "terrain/materials.png", Walk = "terrain/walk.png",
        Forest = "terrain/forest.lua", Plants = "terrain/plants.lua", Objects = "objects.lua", Regions = "regions.lua",
        Minimap = "minimap.png", World = "world.lua", Triggers = "triggers.lua", Custom = "custom.lua";
    public const string Ext = ".pwmap";
    public static string Biome(string name) => $"terrain/biomes/{name}.png";
    public static string Texts(string lang) => $"texts/{lang}.lua";
}
