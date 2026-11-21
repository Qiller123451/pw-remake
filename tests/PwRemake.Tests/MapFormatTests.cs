// The remake's map format (docs/MAP_FORMAT.md): the PNG codec, the Lua data files, the map document's reader and
// writer, the converter - every original map staged on this machine converted, written, read back gives the same map
// (MapData) and the same map source (MapSource) as the .ula; the missions' data the same as the old JSON; a mission and
// a skirmish on converted maps play the same; every game mode starts on them.
//
// Data: the staged originals (/mnt/user-data/uploads/Paraworld/Data/*/Maps/**.ula), the rules (/opt/data/base, mirage),
// PW_CONTENT (converted content: configs/<id>/maps and missions in the map format; default /home/claude/testcontent)
// and PW_CONTENT_OLD (mission folders of the old layout - mission.json, world.json -, the converter's input; default
// /home/claude/testcontent, converted in place: the JSON files stay beside the map format's).
//   dotnet run --project tests/PwRemake.Tests -- MapFormat
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class MapFormatTests
{
    const string Originals = "/mnt/user-data/uploads/Paraworld/Data";

    static string Tmp(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "pw-mapformat-tests", name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    static string OldContentRoot() => Environment.GetEnvironmentVariable("PW_CONTENT_OLD") is { Length: > 0 } pc ? pc : "/home/claude/testcontent";

    // ================================================================================================ PNG
    public static void PngRoundTrips(TestContext t)
    {
        var rng = new SimRandom(7);
        foreach (var (w, h) in new[] { (1, 1), (7, 3), (64, 33), (200, 200) })
        {
            var g8 = new byte[w * h]; for (int i = 0; i < g8.Length; i++) g8[i] = (byte)(i % 3 == 0 ? rng.Next() * 256 : (i / w) * 3);
            var g16 = new ushort[w * h]; for (int i = 0; i < g16.Length; i++) g16[i] = (ushort)(i % 5 == 0 ? rng.Next() * 65536 : 1000 + (i % w) * 17);
            var rgba = new byte[w * h * 4]; for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(rng.Next() * 256);
            var a = Png.WriteGrey8(w, h, g8); var b = Png.WriteGrey16(w, h, g16); var c = Png.WriteRgba8(w, h, rgba);
            t.True(Png.Read(a).Grey8().SequenceEqual(g8), $"grey 8 {w}x{h}");
            t.True(Png.Read(b).Grey16().SequenceEqual(g16), $"grey 16 {w}x{h}");
            t.True(Png.Read(c).Rgba8().SequenceEqual(rgba), $"RGBA {w}x{h}");
            t.Equal((w, h), Png.ReadSize(b), "size from the header");
            // deterministic: the same pixels, the same bytes
            t.True(Png.WriteGrey16(w, h, g16).SequenceEqual(b) && Png.WriteRgba8(w, h, rgba).SequenceEqual(c), $"same bytes {w}x{h}");
        }
        // a broken file is refused with a message
        var ok = Png.WriteGrey8(4, 4, new byte[16]);
        var bad = (byte[])ok.Clone(); bad[^20] ^= 0xff;
        try { Png.Read(bad); t.True(false, "a damaged PNG is refused"); } catch (InvalidDataException e) { t.Log("damaged: " + e.Message); }
    }

    public static void PngReadsEveryFilterAndColourType(TestContext t)
    {
        int w = 23, h = 11;
        var rng = new SimRandom(3);
        byte[] Rand(int n) { var r = new byte[n]; for (int i = 0; i < n; i++) r[i] = (byte)(rng.Next() * 256); return r; }
        foreach (var (ct, depth, ch) in new[] { (0, 8, 1), (0, 16, 1), (2, 8, 3), (4, 8, 2), (6, 8, 4), (6, 16, 4) })
            for (int f = 0; f <= 4; f++)
            {
                var s = Rand(w * h * ch * depth / 8);
                var img = Png.Read(Png.Encode(w, h, ct, depth, s, f));
                t.True(img.Samples.SequenceEqual(s) && img.Channels == ch && img.Bits == depth, $"colour type {ct} {depth} bit, filter {f}");
            }
        // a palette image: expanded to RGBA
        var pal = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        var idx = new byte[w * h]; for (int i = 0; i < idx.Length; i++) idx[i] = (byte)(i % 3);
        var p = Png.Read(Png.Encode(w, h, 3, 8, idx, 4, pal)).Rgba8();
        t.True(p[0] == 255 && p[1] == 0 && p[4] == 0 && p[5] == 255 && p[10] == 255 && p[11] == 255, "palette -> RGBA");
        // an RGB picture saved by an image editor: its red channel is the grey
        var rgb = new byte[w * h * 3]; for (int i = 0; i < w * h; i++) rgb[i * 3] = (byte)i;
        t.True(Png.Read(Png.Encode(w, h, 2, 8, rgb)).Grey8().SequenceEqual(Enumerable.Range(0, w * h).Select(i => (byte)i)), "RGB -> grey");
    }

    // ================================================================================================ Lua data
    public static void LuaDataIsOnlyData(TestContext t)
    {
        var good = "-- a comment\nreturn { a = 1, [\"b c\"] = -2.5e3, list = { \"x\", 'y', [[long\nstring]], true, false }, nested = { { 1, 2 }, {} }; }";
        var j = LuaData.ParseJson(good, "good.lua")!;
        t.Equal(-2500.0, j["b c"].Num(), "keys in brackets, negative numbers");
        t.Equal("long\nstring", j["list"]![2].Str(), "long strings");
        t.True(j["nested"]![1] is JsonArray, "an empty table is a list by default");
        t.True(LuaData.ParseJson("return { m = {} }", "x.lua", new[] { "m" })!["m"] is JsonObject, "an empty table at a map path is an object");
        string[] bad =
        {
            "return { f = function() return 1 end }", "return { x = os.time() }", "return { x = y }", "while true do end return {}",
            "return { x = 1 + 2 }", "local a = 1 return { a }", "return { x = (1) }", "return { x = #\"abc\" }", "return { x = \"a\" .. \"b\" }",
            "return { 1, x = 2 }", "return { [1] = 1, [3] = 3 }", "x = 1", "return {", "return { x = 1 } return {}", "return { [{}] = 1 }",
            "return { goto = 1 }", "return setmetatable({}, {})", "return { x = -y }", "return { x = 1e999 }",
        };
        foreach (var b in bad)
        {
            try { LuaData.ParseJson(b, "bad.lua"); t.True(false, $"refused: {b}"); }
            catch (LuaDataException e) { t.True(e.Message.StartsWith("bad.lua"), $"the message names the file: {e.Message}"); }
        }
        // line numbers in the token check's messages
        try { LuaData.Check("return {\n  a = 1,\n  b = print(2),\n}", "m.lua"); t.True(false, "a call refused"); }
        catch (LuaDataException e) { t.True(e.Message.StartsWith("m.lua:3:"), "line of the error: " + e.Message); }
        // strings with every escape the writer makes, numbers that must stay exact
        var o = new JsonObject { ["s"] = "q\"b\\s\n\t\u0001é€", ["n"] = 0.1 + 0.2, ["neg0"] = JsonNode.Parse("-0.0"), ["big"] = 1e300, ["int"] = 12345678901234 };
        var back = LuaData.ParseJson(LuaData.Write(o), "w.lua")!;
        t.Equal(o["s"].Str(), back["s"].Str(), "string escapes");
        t.True(back["n"]!.GetValue<double>() == 0.1 + 0.2 && back["big"]!.GetValue<double>() == 1e300 && back["int"]!.GetValue<long>() == 12345678901234, "numbers exact");
        t.True(double.IsNegative(back["neg0"]!.GetValue<double>()), "-0 stays negative");
        // float32 text: the shortest that reads back to the same float
        var r = new SimRandom(5);
        for (int i = 0; i < 20000; i++)
        {
            float f = (float)((r.Next() - 0.5) * Math.Pow(10, (int)(r.Next() * 8) - 3));
            t.True((float)double.Parse(LuaData.FloatText(f), System.Globalization.CultureInfo.InvariantCulture) == f, $"float text {f:R}");
        }
    }

    // ================================================================================================ maps
    /// <summary>a reflection dump of an object's data (fields, properties, lists, dictionaries; doubles bit for bit,
    /// big arrays hashed; delegates and the MapData behind a source left out) - two dumps are equal when the data is</summary>
    static string Dump(object? v, params string[] skip)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void D(object? x, int depth)
        {
            if (x == null) { sb.Append("null"); return; }
            switch (x)
            {
                case string s: sb.Append('"').Append(s).Append('"'); return;
                case double d: sb.Append(BitConverter.DoubleToInt64Bits(d).ToString("x")); return;
                case float f: sb.Append(BitConverter.SingleToInt32Bits(f).ToString("x")); return;
                case bool or int or long or byte or uint or short or ulong or char: sb.Append(Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)); return;
                case Enum e: sb.Append(e); return;
                case Delegate: sb.Append("fn"); return;
                case MapData when depth > 0: sb.Append("md"); return;
                case byte[] ba: sb.Append("bytes:").Append(ba.Length).Append(':').Append(Cmp.Fnv(ba).ToString("x")); return;
                case float[] fa: sb.Append("floats:").Append(fa.Length).Append(':').Append(Cmp.FnvF32(fa).ToString("x")); return;
            }
            if (depth > 12 || !seen.Add(x)) { sb.Append("..."); return; }
            if (x is IDictionary dict)
            {
                sb.Append('{');
                foreach (DictionaryEntry e in dict) { D(e.Key, depth + 1); sb.Append(':'); D(e.Value, depth + 1); sb.Append(','); }
                sb.Append('}');
                return;
            }
            if (x is IEnumerable en)
            {
                sb.Append('[');
                foreach (var e in en) { D(e, depth + 1); sb.Append(','); }
                sb.Append(']');
                return;
            }
            var ty = x.GetType();
            if (ty.IsValueType && ty.IsGenericType && ty.Name.StartsWith("ValueTuple", StringComparison.Ordinal) || ty.IsValueType)
            {
                foreach (var fi in ty.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal)) { sb.Append(fi.Name).Append('='); D(fi.GetValue(x), depth + 1); sb.Append(';'); }
                foreach (var pi in ty.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal)) { sb.Append(pi.Name).Append('='); D(pi.GetValue(x), depth + 1); sb.Append(';'); }
                return;
            }
            sb.Append(ty.Name).Append('(');
            foreach (var fi in ty.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (skip.Contains(fi.Name)) continue;
                sb.Append(fi.Name).Append('='); D(fi.GetValue(x), depth + 1); sb.Append(';');
            }
            sb.Append(')');
        }
        D(v, 0);
        return sb.ToString();
    }

    static string? FirstDiff(string a, string b)
    {
        if (a == b) return null;
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int s = Math.Max(0, i - 160);
        return $"at {i}: ...{a.Substring(s, Math.Min(260, a.Length - s))}\n   vs ...{b.Substring(s, Math.Min(260, b.Length - s))}";
    }

    /// <summary>what the source answers at points of a grid (the functions the dump leaves out)</summary>
    static string Samples(MapSource S)
    {
        var sb = new StringBuilder();
        var hf = new HeightField(S.Size, S.MeshCell, S.Height);
        var splat = S.Splat?.Invoke(hf);
        for (int j = 0; j <= 30; j++)
            for (int i = 0; i <= 30; i++)
            {
                double x = -S.Size / 2 + (i + 0.37) * S.Size / 31, z = -S.Size / 2 + (j + 0.61) * S.Size / 31;
                sb.Append(BitConverter.DoubleToInt64Bits(S.Height(x, z)).ToString("x")).Append(',');
                sb.Append(S.Walkable?.Invoke(x, z)).Append(',').Append(S.SoundMat?.Invoke(x, z)).Append(',');
                sb.Append(BitConverter.DoubleToInt64Bits(hf.At(x, z)).ToString("x")).Append(',');
                if (splat != null) foreach (var w in splat(x, z)) sb.Append(BitConverter.DoubleToInt64Bits(w).ToString("x")).Append(' ');
                sb.Append('|');
            }
        return sb.ToString();
    }

    static readonly Dictionary<string, (Sim.Rules.Rules R, ModelManifest M, ForestData? F)?> s_data = new();
    static (Sim.Rules.Rules R, ModelManifest M, ForestData? F)? DataFor(string relPath)
    {
        var id = relPath.StartsWith("MIRAGE", StringComparison.OrdinalIgnoreCase) || relPath.StartsWith("BoosterPack3", StringComparison.OrdinalIgnoreCase) ? "mirage" : "base";
        if (s_data.TryGetValue(id, out var have)) return have;
        var dir = Environment.GetEnvironmentVariable(id == "mirage" ? "PW_DATA_MIRAGE" : "PW_DATA") ?? "/opt/data/" + id;
        (Sim.Rules.Rules, ModelManifest, ForestData?)? r = File.Exists(Path.Combine(dir, "techtree.json"))
            ? (Sim.Rules.Rules.Load(dir), ModelManifest.Load(dir), ForestData.LoadFile(Path.Combine(dir, "forest.json"))) : null;
        s_data[id] = r;
        return r;
    }

    /// <summary>the same map data and the same map source (with the real rules, models and forest layouts; with and
    /// without the objects a campaign places itself)</summary>
    static void SameMap(TestContext t, string what, MapData a, MapData b, string rel)
    {
        var d = FirstDiff(Dump(a, "At"), Dump(b, "At"));
        t.True(d == null, $"{what}: the same MapData {d}");
        if (DataFor(rel) is not { } data) return;
        var rules = new RulesMapAdapter(data.R);
        foreach (var cfg in new[] { null, new MapSourceConfig { SkipObjects = new HashSet<string>() } })
        {
            var sa = MapSource.OriginalSource(a, rules, cfg, n => data.M.Has(n), data.F);
            var sb = MapSource.OriginalSource(b, rules, cfg, n => data.M.Has(n), data.F);
            var ds = FirstDiff(Dump(sa) + Samples(sa), Dump(sb) + Samples(sb));
            t.True(ds == null, $"{what}: the same MapSource{(cfg != null ? " (campaign)" : "")} {ds}");
        }
    }

    /// <summary>every staged original: .ula -> PwMap -> map folder -> read back = the .ula's MapData and MapSource</summary>
    public static void ConvertedMapsSameAsUla(TestContext t)
    {
        if (!Directory.Exists(Originals)) t.Skip("no staged maps");
        var files = Directory.EnumerateFiles(Originals, "*.ula", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (Environment.GetEnvironmentVariable("PW_MAPFORMAT_MAPS") is { Length: > 0 } only) files = files.Where(f => only.Split(',').Any(o => f.Contains(o))).ToList();
        var dir = Tmp("maps");
        long biggestRead = 0; string biggest = "";
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(Originals, f).Replace('\\', '/');
            var ula = Ula.ParseUlaFile(f);
            var doc = PwMapConvert.FromUla(f, rel);
            var outDir = Path.Combine(dir, MapFiles.FolderOf(rel));
            PwMapWriter.WriteFolder(doc, outDir);
            var sw = Stopwatch.StartNew();
            var back = PwMapReader.Read(outDir);
            long ms = sw.ElapsedMilliseconds;
            if (ms > biggestRead) { biggestRead = ms; biggest = rel; }
            t.Equal(rel, back.Source, "the source id");
            SameMap(t, rel, ula, back.ToMapData(), rel);
            // the header alone: the map list's fields
            var hd = MapFiles.ReadHeader(outDir);
            t.True(hd.Name == ula.Name && hd.Setting == ula.Setting && hd.Hx == ula.Hx && hd.Hy == ula.Hy && hd.MaxPlayers == ula.MaxPlayers && hd.SettingName == ula.SettingName
                   && hd.Description.SequenceEqual(ula.Description) && hd.Info.SequenceEqual(ula.Info) && (hd.Preview?.Rgba.SequenceEqual(ula.Preview!.Rgba) ?? ula.Preview == null), $"{rel}: header");
            // heights: exact (the original's own 1/128 m step)
            t.True(back.HeightMin == 0 && back.HeightStep == 1 / 128.0, $"{rel}: heights on the original's step");
        }
        t.Log($"{files.Count} maps converted and compared; slowest read {biggestRead} ms ({biggest})");
    }

    /// <summary>the remake's own map (a recipe: an original + name, description, biomes) converted the same, with its
    /// biome layer through the biome pictures</summary>
    public static void RemakeRecipeConverted(TestContext t)
    {
        var recipe = Path.Combine(MapFiles.Root(), "frost_to_jungle.pwmap.json");
        if (!File.Exists(recipe) || !Directory.Exists(Originals)) t.Skip("no recipe or originals");
        MapFiles.UseInstall(Path.GetDirectoryName(Originals));
        var doc = PwMapConvert.FromRemakeRecipe(recipe, MapFiles.FindBase, "Remake/frost_to_jungle.pwmap.json");
        t.True(doc.Biomes != null && doc.Biomes.Biomes.Count >= 2, "the biome layer");
        var dir = Path.Combine(Tmp("recipe"), "frost_to_jungle");
        PwMapWriter.WriteFolder(doc, dir);
        t.True(File.Exists(Path.Combine(dir, "terrain", "biomes", doc.Biomes!.Biomes[0] + ".png")), "a picture per biome");
        var back = PwMapReader.Read(dir);
        t.True(back.Biomes!.Weights.SequenceEqual(doc.Biomes.Weights) && back.Biomes.Biomes.SequenceEqual(doc.Biomes.Biomes) && back.Biomes.W == doc.Biomes.W
               && back.Biomes.Cell == doc.Biomes.Cell && back.Biomes.Remap.Count == doc.Biomes.Remap.Count, "the same biome layer");
        SameMap(t, "frost_to_jungle", doc.ToMapData(), back.ToMapData(), "Base/x");
        var h = MapFiles.ReadHeader(dir);
        t.Equal("Frost and Jungle", SkirmishMaps.Describe(h, new Dictionary<string, string>()).Name, "the recipe's name in the map list");
    }

    /// <summary>a .pwmap zip holds the folder's files; the writer is deterministic; an edited height outside the
    /// original's step is fitted into a range of its own</summary>
    public static void ZipDeterminismAndHeights(TestContext t)
    {
        var f = Path.Combine(Originals, "Base/Maps/Multiplayer/multi_2_jun_001.ula");
        if (!File.Exists(f)) t.Skip("map not staged");
        var doc = PwMapConvert.FromUla(f, "Base/Maps/Multiplayer/multi_2_jun_001.ula");
        var dir = Tmp("zip");
        var a = PwMapWriter.Files(doc); var b = PwMapWriter.Files(doc);
        t.True(a.Keys.SequenceEqual(b.Keys) && a.All(kv => kv.Value.SequenceEqual(b[kv.Key])), "the same map, the same bytes");
        var zip = Path.Combine(dir, "m.pwmap");
        PwMapWriter.WriteZip(doc, zip);
        var z1 = File.ReadAllBytes(zip);
        PwMapWriter.WriteZip(doc, zip);
        t.True(File.ReadAllBytes(zip).SequenceEqual(z1), "the same zip");
        t.True(PwMapReader.IsMap(zip), "a .pwmap is a map");
        SameMap(t, "zip", doc.ToMapData(), MapFiles.Read(zip), "Base/x");
        // a zip of the folder itself (one top folder) reads as well
        var folder = Path.Combine(dir, "mymap");
        PwMapWriter.WriteFolder(doc, folder);
        var z2 = Path.Combine(dir, "folder.pwmap");
        System.IO.Compression.ZipFile.CreateFromDirectory(folder, z2, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: true);
        t.Equal(doc.Objects.Count, PwMapReader.Read(z2).Objects.Count, "a zipped folder");
        // heights the editor made: not on the 1/128 m step -> a range fitted to them, within half a step
        var edited = PwMapReader.Read(folder);
        var rng = new SimRandom(9);
        for (int i = 0; i < edited.Heights.Length; i += 7) edited.Heights[i] = (float)(edited.Heights[i] + rng.Next() * 0.3 - 0.1);
        edited.Heights[5] = -20.123f; edited.Heights[6] = 600.5f;
        var e2 = Path.Combine(dir, "edited");
        PwMapWriter.WriteFolder(edited, e2);
        var eb = PwMapReader.Read(e2);
        double maxErr = 0;
        for (int i = 0; i < eb.Heights.Length; i++) maxErr = Math.Max(maxErr, Math.Abs(eb.Heights[i] - edited.Heights[i]));
        t.True(maxErr <= eb.HeightStep / 2 + 1e-4, $"edited heights within half a step ({maxErr:0.0000} m, step {eb.HeightStep:0.0000} m, from {eb.HeightMin})");
        // a deep copy writes the same files and shares nothing
        var copy = edited.Clone();
        t.True(PwMapWriter.Files(copy).All(kv => PwMapWriter.Files(edited)[kv.Key].SequenceEqual(kv.Value)), "a clone writes the same map");
        copy.Heights[0] += 5; copy.Objects[0].Attr["x"] = "y"; copy.Forest.Blocks.Clear();
        t.True(edited.Heights[0] != copy.Heights[0] && !edited.Objects[0].Attr.ContainsKey("x") && edited.Forest.Blocks.Count > 0, "a clone shares nothing");
        // the format's files the map no longer has go away when it is written again
        doc.Preview = null;
        PwMapWriter.WriteFolder(doc, folder);
        t.True(!File.Exists(Path.Combine(folder, PwMapPaths.Minimap)), "a removed minimap");
    }

    // ================================================================================================ missions
    static List<string> OldMissionFolders(string cfg)
    {
        var dir = Path.Combine(OldContentRoot(), "configs", cfg, "missions");
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "mission.json"))).OrderBy(x => x, StringComparer.Ordinal).ToList() : new();
    }

    /// <summary>every mission folder of the old layout: converted, its mission, world, texts and triggers read back by the
    /// game's reader are the old JSON (nulls aside) and the old Lua</summary>
    public static void MissionsConvertedSameData(TestContext t)
    {
        int n = 0;
        var dir = Tmp("missions");
        foreach (var cfg in new[] { "base", "mirage" })
            foreach (var old in OldMissionFolders(cfg))
            {
                var name = Path.GetFileName(old);
                var doc = new PwMap { Hx = 16, Hy = 16, Heights = new float[256], Mx = 8, My = 8, Mats = new byte[64], Materials = PwMap.DefaultMaterials("Jungle") };
                PwMapConvert.AddMission(doc, old);
                var to = Path.Combine(dir, cfg, name);
                PwMapWriter.WriteFolder(doc, to);
                var lm = LuaMission.Load(to);
                t.True(lm.Ok, $"{cfg}/{name}: the Lua loads");
                t.Equal(null, PwMapConvert.FirstDifference(Json.ParseFile(Path.Combine(old, "mission.json")), lm.Mission, ""), $"{cfg}/{name}: mission");
                t.Equal(null, PwMapConvert.FirstDifference(Json.ParseFile(Path.Combine(old, "world.json")), lm.World, ""), $"{cfg}/{name}: world");
                var texts = Json.ParseFile(Path.Combine(old, "texts", "uk.json")) as JsonObject;
                if (texts != null) t.True(texts.All(kv => lm.Script.Texts.TryGetValue(kv.Key, out var v) && v == kv.Value.Str()) && lm.Script.Texts.Count == texts.Count, $"{cfg}/{name}: texts");
                t.Equal(File.ReadAllText(Path.Combine(old, "triggers.lua")), File.ReadAllText(Path.Combine(to, "triggers.lua")), $"{cfg}/{name}: triggers.lua");
                t.True(lm.Script.Triggers.Count > 0, $"{cfg}/{name}: triggers");
                n++;
            }
        if (n == 0) t.Skip("no mission folders of the old layout (PW_CONTENT_OLD)");
        t.Log($"{n} missions");
        // the old layout (no map.lua) is refused with a message naming Prepare
        var o = OldMissionFolders("base").FirstOrDefault();
        if (o != null)
        {
            var only = Path.Combine(dir, "old", Path.GetFileName(o));
            Directory.CreateDirectory(only);
            File.Copy(Path.Combine(o, "mission.json"), Path.Combine(only, "mission.json"));
            try { LuaMission.Load(only); t.True(false, "an old mission folder is refused"); }
            catch (InvalidOperationException e) { t.True(e.Message.Contains("Prepare"), e.Message); }
        }
    }

    /// <summary>a configuration with converted maps and missions (PW_CONTENT, else made here from PW_CONTENT_OLD)</summary>
    static CampaignTests.Cfg? Converted(TestContext t, string id)
    {
        var c = CampaignTests.LoadCfgPublic(id);
        if (c == null) t.Skip($"no configuration {id} (PW_CONTENT)");
        if (!Directory.Exists(c!.Config.MapsDir) || MissionEntry.LoadIndex(c.Config.MissionsDir).Select(e => Path.Combine(c.Config.MissionsDir, e.Folder)).Any(PwMapReader.IsOldMissionFolder))
            t.Skip($"PW_CONTENT ({c.Config.Dir}) is not converted: run tools/mapconvert prepare on it (docs/MAP_FORMAT.md)");
        return c;
    }

    /// <summary>mission 11 converted plays as before: the arena's first triggers fire, no trigger errors; two runs are
    /// the same</summary>
    public static void ConvertedMissionPlays(TestContext t)
    {
        Converted(t, "base");
        ulong Run(out MissionSession s)
        {
            s = CampaignTests.Session(t, "base", 11, debug: new DebugOptions { On = true, Me = new DebugSide { Def = 100 } });
            s.Run(90);
            return s.World.StateHash();
        }
        var h1 = Run(out var s1);
        var E = s1.Engine!;
        t.Equal(0, E.Errors.Count, "trigger errors");
        foreach (var name in new[] { "Sa: MS_4010", "Ic:HealingWells", "La01: Set Visible", "Gf01: Spawn Pola" })
            t.True(E.FiringLog.Any(f => f.Trigger == name), name + " fired");
        t.Equal(h1, Run(out _), "the same game twice");
        t.Log($"{E.StatFired} firings, hash {h1:x}");
    }

    // ================================================================================================ the game modes
    /// <summary>a skirmish on the converted map plays exactly as on the .ula (the same state after two minutes), and as
    /// on the .pwmap zip</summary>
    public static void SkirmishSameOnConvertedMap(TestContext t)
    {
        const string ID = "Base/Maps/Multiplayer/multi_2_jun_001.ula";
        var ula = Path.Combine(Originals, ID);
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null || !File.Exists(ula)) t.Skip("no configuration or map");
        var dir = Tmp("skirmish");
        var doc = PwMapConvert.FromUla(ula, ID);
        var folder = Path.Combine(dir, "multi_2_jun_001");
        PwMapWriter.WriteFolder(doc, folder);
        var zip = Path.Combine(dir, "multi_2_jun_001.pwmap");
        PwMapWriter.WriteZip(doc, zip);
        ulong Play(string path)
        {
            var setup = Skirmish.DefaultSetup("Hu", "Aje");
            setup.Map = ID; setup.Points = 1000; setup.Seed = 5;
            var s = SkirmishSession.Create(setup, path, c!.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
            for (int i = 0; i < 120 / World.STEP; i++) s.Step();
            return s.StateHash();
        }
        var a = Play(ula); var b = Play(folder); var z = Play(zip);
        t.Equal(a, b, "the converted folder");
        t.Equal(a, z, "the .pwmap");
    }

    /// <summary>on the converted content every mode starts: the skirmish list and a skirmish by its id, a campaign
    /// mission, a co-op mission (its map extended), a multiplayer lobby's map</summary>
    public static void GameModesStart(TestContext t)
    {
        var c = Converted(t, "base")!;
        var list = SkirmishMaps.List(c.Config);
        t.True(list.Count > 0, $"skirmish maps: {list.Count}");
        t.True(list.All(m => m.Type == "multiplayer") && list.Any(m => m.Path == "Base/Maps/Multiplayer/multi_2_jun_001.ula"), "the multiplayer maps by their old ids");
        t.True(list.Any(m => m.Folder == MapFiles.RemakeFolder), "the remake's own map");
        foreach (var m in list) t.True(SkirmishMaps.Resolve(c.Config, m.Path) == m.FullPath, $"{m.Path} resolves to its folder");
        t.True(SkirmishMaps.List(c.Config, all: true).Any(m => m.Type == "singleplayer"), "the campaign's maps with all");
        // a skirmish by the setup's id (GameSession: Resolve -> SkirmishSession)
        var setup = Skirmish.DefaultSetup("Ninigi", "Hu");
        setup.Map = "Base/Maps/Multiplayer/multi_4_ice_601.ula"; setup.Seed = 3;
        var path = SkirmishMaps.Resolve(c.Config, setup.Map);
        t.True(path != null && PwMapReader.IsMap(path), "resolved to a converted map");
        var sk = SkirmishSession.Create(setup, path, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
        for (int i = 0; i < 40; i++) sk.Step();
        t.True(sk.World.Buildings.Count > 0 && sk.World.Units.Count > 0, "the skirmish runs");
        // a remake map by its id
        var rm = list.First(m => m.Folder == MapFiles.RemakeFolder);
        var rs = SkirmishSession.Create(new SkirmishSetup { Map = rm.Path, Slots = Skirmish.DefaultSetup("Hu", "Aje").Slots, Seed = 1 }, SkirmishMaps.Resolve(c.Config, rm.Path), c.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
        t.True(rs.Map.BiomeLayer != null, "the remake map with its biomes");
        // a campaign mission from its folder
        var ms = CampaignTests.Session(t, "base", 3);
        ms.Run(10);
        t.True(ms.World.Units.Count > 0 && ms.Engine!.StatFired > 0, "mission 3 runs");
        // co-op: the mission's map extended for three seats
        var start = CoopTests.CoopStart(t, "base", 3, new[] { ("human", "Hu"), ("human", "Aje"), ("ai", "Ninigi") });
        var cs = CoopTests.Session(t, start);
        cs.Run(5);
        t.True(cs.Campaign.Coop!.Seats.Count == 3 && cs.World.Buildings.Any(b => b.Owner == cs.Campaign.Coop.Seats[2].Player), "the co-op mission runs");
        t.True(cs.Map.Md?.Extension != null || cs.Map.Size >= ms.Map.Size, "the co-op map");
    }

    /// <summary>map ids: the old ids find the converted folders (MapFiles.Find)</summary>
    public static void MapIds(TestContext t)
    {
        t.Equal("Base/Maps/Multiplayer/x", MapFiles.FolderOf("Base/Maps/Multiplayer/x.ula"), "a .ula id");
        t.Equal("Base/Maps/Multiplayer/x", MapFiles.FolderOf(@"Base\Maps\Multiplayer\x.ULA"), "backslashes, case");
        t.Equal("Remake/frost", MapFiles.FolderOf("Remake/frost.pwmap.json"), "a recipe id");
        t.Equal("Custom/m.pwmap", MapFiles.FolderOf("Custom/m.pwmap"), "a .pwmap stays");
        var root = Tmp("ids");
        var cdir = Path.Combine(root, "configs", "x");
        Directory.CreateDirectory(cdir);
        File.WriteAllText(Path.Combine(cdir, "config.json"), "{\"id\":\"x\",\"folders\":[\"Base\",\"MIRAGE\"],\"install\":\"/nowhere\",\"data\":\"/nowhere\"}");
        var cfg = GameConfiguration.Load(cdir)!;
        var doc = new PwMap { Name = "m", Hx = 16, Hy = 16, Heights = new float[256], Mx = 8, My = 8, Mats = new byte[64], Materials = PwMap.DefaultMaterials("Jungle") };
        foreach (var rel in new[] { "Base/Maps/Multiplayer/a", "MIRAGE/Maps/Cpn_single_001/single_11", "Base/Maps/Cpn_single_001/single_11" })
            PwMapWriter.WriteFolder(doc, Path.Combine(cfg.MapsDir, rel));
        PwMapWriter.WriteZip(doc, Path.Combine(cfg.MapsDir, "Custom", "z.pwmap"));
        t.Equal(Path.Combine(cfg.MapsDir, "Base/Maps/Multiplayer/a"), MapFiles.Find(cfg, "base/maps/multiplayer/A.ula"), "case does not matter");
        t.Equal(Path.Combine(cfg.MapsDir, "Base/Maps/Cpn_single_001/single_11"), MapFiles.Find(cfg, "Base/Maps/Cpn_single_001/single_11.ula"), "the folder named first");
        t.Equal(Path.Combine(cfg.MapsDir, "MIRAGE/Maps/Cpn_single_001/single_11"), MapFiles.Find(cfg, "BoosterPack3/Maps/Cpn_single_001/single_11.ula"), "else the last configuration folder that has it");
        t.Equal(Path.Combine(cfg.MapsDir, "Custom", "z.pwmap"), MapFiles.Find(cfg, "Custom/z.pwmap"), "a .pwmap by its path");
        t.Equal(null, MapFiles.Find(cfg, "Base/Maps/none.ula"), "none");
        t.Equal(4, MapFiles.All(cfg).Count, "every map");
    }
}
