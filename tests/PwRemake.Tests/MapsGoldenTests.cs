// The map port against the JS remake: tests/golden/maps_dump.mjs ran the JS map code (maps/ula.js, source.js,
// forest.js, mapgen.js, ground.js) on the real map files and wrote what it got; these tests read the same files with
// the C# port (src/Sim/Maps) and compare. The rules' and the manifest's answers are replayed from the golden (what
// the JS asked and got), so the maps are tested without the Rules port. Maps whose .ula is not on this machine are
// skipped (the originals are staged from the user's installation, not part of the repository).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Tests;

/// <summary>IMapRules + manifest from a golden's record of the JS rules' answers. A question the JS did not ask is an
/// error: the port must ask the same.</summary>
sealed class RecordedRules : IMapRules
{
    readonly JsonObject _log;
    public RecordedRules(JsonObject log) { _log = log; }
    JsonNode? Get(string table, string key)
    {
        var t = (JsonObject)_log[table]!;
        if (!t.ContainsKey(key)) throw new Exception($"the port asked {table}({key}), the JS did not");
        return t[key];
    }
    static string? S(JsonNode? n) => n == null ? null : n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : null;
    public string? ClassGfx(string name) => S(Get("classGfx", name));
    public string? Script(string name) => S(Get("script", name));
    public double? ResourceValue(string section, string name) { var v = Get("resourceValue", section + "|" + name); return v == null ? null : v.GetValue<double>(); }
    public bool Exists(string name) => Get("exists", name)!.GetValue<bool>();
    public string? StatsGfx(string name) => S(Get("statsGfx", name));
    public string? NestSpecies(string nestClassLower) => S(Get("nests", nestClassLower));
    public bool HasModel(string m) => Get("models", m)!.GetValue<bool>();
}

/// <summary>comparison helpers: numbers with a relative tolerance (JS and .NET trigonometry may differ in the last
/// bit), FNV-1a hashes as the dump script</summary>
sealed class Cmp
{
    readonly TestContext _t;
    readonly string _map;
    public Cmp(TestContext t, string map) { _t = t; _map = map; }
    public void Num(JsonNode? e, double a, string what, double tol = 1e-9)
    {
        if (e == null) { _t.True(double.IsNaN(a), $"{_map}: {what}: expected NaN/null, got {a}"); return; }
        double v = e.GetValue<double>();
        _t.Near(v, a, tol * Math.Max(1, Math.Abs(v)), $"{_map}: {what}");
    }
    public void Num(JsonNode? e, double? a, string what, double tol = 1e-9)
    {
        if (e == null) { _t.True(a == null || double.IsNaN(a.Value), $"{_map}: {what}: expected null, got {a}"); return; }
        _t.True(a != null, $"{_map}: {what}: expected {e}, got null");
        Num(e, a!.Value, what, tol);
    }
    public void Str(JsonNode? e, string? a, string what) => _t.Equal(e == null ? null : e.GetValue<string>(), a, $"{_map}: {what}");
    public void Bool(JsonNode? e, bool a, string what) => _t.Equal(e!.GetValue<bool>(), a, $"{_map}: {what}");
    public void Int(JsonNode? e, long a, string what) => _t.Equal((long)e!.GetValue<double>(), a, $"{_map}: {what}");
    public void Int(int e, long a, string what) => _t.Equal((long)e, a, $"{_map}: {what}");
    public void Count(JsonNode? e, int a, string what) => _t.Equal(((JsonArray)e!).Count, a, $"{_map}: {what} (count)");
    public void Strs(JsonNode? e, IEnumerable<string> a, string what) => _t.Equal(string.Join("|", ((JsonArray)e!).Select(x => x!.GetValue<string>())), string.Join("|", a), $"{_map}: {what}");
    public void Dict(JsonNode? e, IReadOnlyDictionary<string, string> a, string what)
    {
        var o = (JsonObject)e!;
        _t.Equal(o.Count, a.Count, $"{_map}: {what} (count)");
        foreach (var (k, v) in o) _t.Equal(v!.GetValue<string>(), a.TryGetValue(k, out var av) ? av : "<missing>", $"{_map}: {what}[{k}]");
    }

    public static uint Fnv(ReadOnlySpan<byte> b) { uint h = 0x811c9dc5; foreach (byte x in b) { h ^= x; h = unchecked(h * 0x01000193); } return h; }
    public static uint FnvF32(float[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return Fnv(b); }
    public static uint FnvStr(string s) { uint h = 0x811c9dc5; foreach (char c in s) { h ^= c; h = unchecked(h * 0x01000193); } return h; }
    public static uint AttrHash(Dictionary<string, string> a) => FnvStr(string.Join("\n", a.Select(kv => kv.Key + "=" + kv.Value)));
}

[TestClass]
public static class MapsGoldenTests
{
    static string GoldenDir(TestContext t)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var g = Path.Combine(d.FullName, "tests", "golden");
            if (Directory.Exists(Path.Combine(g, "maps"))) return g;
        }
        t.Skip("tests/golden/maps not found");
        return "";
    }

    static JsonNode ReadGolden(string path)
    {
        using var f = File.OpenRead(path);
        using Stream s = path.EndsWith(".gz") ? new GZipStream(f, CompressionMode.Decompress) : f;
        return JsonNode.Parse(s)!;
    }

    /// <summary>every staged map against the JS: the reader (Ula), forest blocks, the map source</summary>
    public static void OriginalMapsMatchJs(TestContext t)
    {
        var files = Directory.GetFiles(Path.Combine(GoldenDir(t), "maps"), "*.json.gz").OrderBy(f => f, StringComparer.Ordinal).ToList();
        int done = 0;
        var forests = new Dictionary<string, ForestData?>();
        foreach (var gf in files)
        {
            var G = ReadGolden(gf);
            string file = G["file"]!.GetValue<string>();
            if (!File.Exists(file)) { t.Log($"skip {Path.GetFileName(gf)}: {file} not staged"); continue; }
            string fj = G["forestJson"]!.GetValue<string>();
            if (!forests.TryGetValue(fj, out var forest)) forests[fj] = forest = File.Exists(fj) ? ForestData.LoadFile(fj) : null;
            if (forest == null) { t.Log($"skip {Path.GetFileName(gf)}: {fj} missing"); continue; }
            string id = Path.GetFileName(gf).Replace(".json.gz", "");
            var c = new Cmp(t, id);
            var md = Ula.ParseUlaFile(file);
            CompareMapData(c, G["md"]!, md, forest);
            var rules = new RecordedRules((JsonObject)G["rules"]!);
            var S = MapSource.OriginalSource(md, rules, null, rules.HasModel, forest);
            var skipped = MapSource.OriginalSource(md, rules, new MapSourceConfig { SkipObjects = new HashSet<string>() }, rules.HasModel, forest);
            CompareSource(c, G["source"]!, S, skipped);
            done++;
        }
        if (done == 0) t.Skip("no staged map files");
        t.Log($"{done} maps compared");
    }

    static void CompareMapData(Cmp c, JsonNode e, MapData md, ForestData forest)
    {
        c.Str(e["name"], md.Name, "name"); c.Str(e["file"], md.File, "file");
        c.Int(e["w"], md.W, "w"); c.Int(e["h"], md.H, "h"); c.Int(e["hx"], md.Hx, "hx"); c.Int(e["hy"], md.Hy, "hy"); c.Int(e["mx"], md.Mx, "mx"); c.Int(e["my"], md.My, "my");
        c.Num(e["water"], md.Water, "water", 0);
        c.Str(e["setting"], md.Setting, "setting"); c.Str(e["settingName"], md.SettingName, "settingName");
        c.Num(e["defaultCamera"], md.DefaultCamera, "defaultCamera", 0); c.Num(e["startTime"], md.StartTime, "startTime", 0); c.Num(e["maxPlayers"], md.MaxPlayers, "maxPlayers", 0);
        c.Dict(e["info"], md.Info, "info");
        c.Dict(e["description"], md.Description, "description");
        if (e["preview"] is JsonNode pv) { c.Int(pv["w"], md.Preview!.W, "preview w"); c.Int(pv["hash"], Cmp.Fnv(md.Preview.Rgba), "preview pixels"); }
        else c.Str(null, md.Preview == null ? null : "preview", "preview");
        c.Int(e["heights"], Cmp.FnvF32(md.Heights), "heights");
        c.Int(e["mats"], Cmp.Fnv(md.Mats), "materials");
        if (e["walk"] is JsonNode wk)
        {
            c.Int(wk["w"], md.Walk!.W, "walk w"); c.Int(wk["h"], md.Walk.H, "walk h"); c.Int(wk["hash"], Cmp.Fnv(md.Walk.Cells), "walk cells");
            c.Int(wk["ones"], md.Walk.Cells.Sum(x => (long)x), "walkable cells");
        }
        else c.Str(null, md.Walk == null ? null : "walk", "walk grid");
        var objs = (JsonArray)e["objects"]!;
        c.Count(objs, md.Objects.Count, "objects");
        for (int i = 0; i < objs.Count; i++)
        {
            var a = (JsonArray)objs[i]!;
            var o = md.Objects[i];
            string w = $"object {i} {o.Name}";
            c.Str(a[0], o.Type, w + " type"); c.Str(a[1], o.Name, w + " name"); c.Str(a[2], o.Cls, w + " cls");
            c.Num(a[3], o.X, w + " x", 0); c.Num(a[4], o.Y, w + " y", 0); c.Num(a[5], o.Z, w + " z", 0); c.Num(a[6], o.Rot, w + " rot", 1e-12);
            c.Num(a[7], o.Owner, w + " owner", 0); c.Int(a[8], o.At, w + " at");
            c.Int(a[9], o.Attr.Count, w + " attributes"); c.Int(a[10], Cmp.AttrHash(o.Attr), w + " attribute values");
        }
        var pl = e["plants"]!;
        c.Int(pl["n"], md.Plants.Count, "plants");
        c.Int(pl["names"], Cmp.FnvStr(string.Join("|", md.Plants.Select(p => p.Name))), "plant names");
        c.Num(pl["x"], md.Plants.Sum(p => p.X), "plants x sum"); c.Num(pl["y"], md.Plants.Sum(p => p.Y), "plants y sum");
        c.Num(pl["z"], md.Plants.Sum(p => p.Z), "plants z sum"); c.Num(pl["rot"], md.Plants.Sum(p => p.Rot), "plants rot sum");
        var pf = (JsonArray)pl["first"]!;
        for (int i = 0; i < pf.Count; i++)
        {
            var a = (JsonArray)pf[i]!; var p = md.Plants[i];
            c.Str(a[0], p.Name, $"plant {i}"); c.Num(a[1], p.X, $"plant {i} x", 0); c.Num(a[2], p.Y, $"plant {i} y", 0); c.Num(a[4], p.Rot, $"plant {i} rot", 1e-12);
            for (int k = 0; k < 4; k++) c.Num(a[5 + k], p.Q[k], $"plant {i} q{k}", 0);
        }
        var rg = (JsonArray)e["regions"]!;
        c.Count(rg, md.Regions.Count, "regions");
        for (int i = 0; i < rg.Count; i++)
        {
            var a = (JsonArray)rg[i]!; var r = md.Regions[i];
            c.Str(a[0], r.Name, $"region {i}");
            var sh = (JsonArray)a[1]!;
            c.Count(sh, r.Shapes.Count, $"region {r.Name} shapes");
            for (int k = 0; k < sh.Count; k++)
            {
                var s = (JsonArray)sh[k]!; var q = r.Shapes[k];
                c.Int(s[0], q.Oval ? 1 : 0, $"region {r.Name} shape {k} oval"); c.Int(s[1], q.Enabled ? 1 : 0, $"region {r.Name} shape {k} enabled");
                c.Num(s[2], q.X, "shape x", 0); c.Num(s[3], q.Y, "shape y", 0); c.Num(s[4], q.W, "shape w", 0); c.Num(s[5], q.H, "shape h", 0);
            }
        }
        var fo = e["forest"]!;
        c.Int(fo["w"], md.Forest.W, "forest w"); c.Int(fo["n"], md.Forest.Blocks.Count, "forest blocks");
        c.Int(fo["hash"], Cmp.Fnv(md.Forest.Blocks.SelectMany(b => new[] { (byte)(b.X & 255), (byte)(b.Y & 255) }.Concat(b.B)).ToArray()), "forest block bytes");
        double ox = md.Hx, oy = md.Hy;                     // (W2 / 2 = Hx)
        var fi = forest.ForestItems(md, (x, y) => md.HeightAtMap(x - ox + ox, y - oy + oy));
        c.Int(fo["trees"], fi.Trees.Count, "forest trees"); c.Int(fo["deco"], fi.Deco.Count, "forest undergrowth");
        c.Num(fo["tx"], fi.Trees.Sum(q => q.X), "forest trees x"); c.Num(fo["ty"], fi.Trees.Sum(q => q.Y), "forest trees y");
        c.Num(fo["trot"], fi.Trees.Sum(q => q.Rot), "forest trees rot"); c.Num(fo["tkind"], fi.Trees.Sum(q => q.Kind * 31 + q.Amount), "forest tree kinds");
        c.Num(fo["dx"], fi.Deco.Sum(q => q.X), "forest deco x"); c.Num(fo["dkind"], fi.Deco.Sum(q => q.Kind), "forest deco kinds");
        var ft = (JsonArray)fo["first"]!;
        for (int i = 0; i < ft.Count; i++)
        {
            var a = (JsonArray)ft[i]!; var q = fi.Trees[i];
            c.Num(a[0], q.X, "forest tree x", 0); c.Num(a[1], q.Y, "forest tree y", 0); c.Num(a[2], q.Rot, "forest tree rot", 0); c.Int(a[3], q.Kind, "forest tree kind"); c.Int(a[4], q.Amount, "forest tree amount");
        }
        var qs = (JsonArray)e["quats"]!;
        for (int i = 0; i < qs.Count; i++) { var mq = Frame.MapQuat(md.Objects[i].Q); for (int k = 0; k < 4; k++) c.Num(qs[i]![k], mq[k], $"mapQuat {i}", 1e-12); }
    }

    static void Boxes(Cmp c, JsonNode? e, List<AreaBox>? a, string what)
    {
        if (e == null) { c.Str(null, a == null ? null : "areas", what); return; }
        var l = (JsonArray)e;
        c.Count(l, a!.Count, what);
        for (int i = 0; i < l.Count; i++)
        {
            c.Bool(l[i]!["oval"], a[i].Oval, what + " oval");
            c.Num(l[i]!["x0"], a[i].X0, what + " x0"); c.Num(l[i]!["z0"], a[i].Z0, what + " z0"); c.Num(l[i]!["x1"], a[i].X1, what + " x1"); c.Num(l[i]!["z1"], a[i].Z1, what + " z1");
        }
    }

    static void CompareSource(Cmp c, JsonNode e, MapSource S, MapSource skipped)
    {
        c.Str(e["name"], S.Name, "source name"); c.Str(e["setting"], S.Setting, "source setting");
        c.Num(e["size"], S.Size, "size", 0);
        // (the remake samples the ground every 2 m on every map, the original's grid - the JS took 4 m above 1400 m:
        // the JS's height field below is made with the JS's cell)
        c.Num(JsonValue.Create(2.0), S.MeshCell, "meshCell (the remake: 2 m on every map)", 0);
        var pl = e["play"]!;
        c.Num(pl["x0"], S.Play.X0, "play x0", 0); c.Num(pl["x1"], S.Play.X1, "play x1", 0); c.Num(pl["z0"], S.Play.Z0, "play z0", 0); c.Num(pl["z1"], S.Play.Z1, "play z1", 0);
        c.Num(e["water"], S.Water, "water", 0);
        c.Num(e["origin"]![0], S.Origin![0], "origin x", 0); c.Num(e["origin"]![1], S.Origin[1], "origin y", 0);
        c.Num(e["wallGrid"]![0], S.WallGrid![0], "wallGrid x", 0); c.Num(e["wallGrid"]![1], S.WallGrid[1], "wallGrid z", 0);
        c.Strs(e["textures"], S.Textures, "textures");
        c.Num(e["defaultCamera"], S.DefaultCamera, "defaultCamera", 0); c.Num(e["startTime"], S.StartTime, "startTime", 0);
        c.Str(e["levelName"], S.LevelName, "levelName"); c.Str(e["atmos"], S.Atmos, "atmos");
        if (e["aiOptions"] is JsonObject ai) { c.Int(ai.Count, S.AiOptions!.Count, "aiOptions"); foreach (var (k, v) in ai) c.Bool(v, S.AiOptions[k], "aiOption " + k); }
        else c.Str(null, S.AiOptions == null ? null : "aiOptions", "aiOptions");
        if (e["weather"] is JsonObject we)
        {
            c.Str(we["name"], S.Weather!.Name, "weather name"); c.Bool(we["loop"], S.Weather.Loop, "weather loop");
            var tr = (JsonArray)we["tracks"]!;
            c.Count(tr, S.Weather.Tracks.Count, "weather tracks");
            for (int i = 0; i < tr.Count; i++)
            {
                var a = S.Weather.Tracks[i];
                c.Num(tr[i]!["dur"], a.Dur, "track dur"); c.Num(tr[i]!["type"], a.Type, "track type"); c.Num(tr[i]!["intensity"], a.Intensity, "track intensity");
                c.Num(tr[i]!["wind"], a.Wind, "track wind"); c.Num(tr[i]!["strength"], a.Strength, "track strength");
            }
        }
        else c.Str(null, S.Weather == null ? null : "weather", "weather");
        c.Strs(e["itemPool"]!["pool"], S.ItemPool.Pool, "item pool"); c.Num(e["itemPool"]!["max"], S.ItemPool.Max, "item max");
        var mp = e["mp"]!;
        c.Num(mp["maxPlayers"], S.Mp.MaxPlayers, "mp maxPlayers"); c.Strs(mp["gameTypes"], S.Mp.GameTypes, "mp gameTypes");
        c.Bool(mp["lastManStanding"], S.Mp.LastManStanding, "mp lms"); c.Bool(mp["multiplayer"], S.Mp.Multiplayer, "mp multiplayer"); c.Num(mp["defenderTimer"], S.Mp.DefenderTimer, "mp defenderTimer");
        var slots = (JsonObject)mp["slots"]!;
        c.Int(slots.Count, S.Mp.Slots.Count, "mp slots");
        foreach (var (k, v) in slots)
        {
            var sl = S.Mp.Slots[k];
            if (v!["tribes"] is JsonArray tb) c.Strs(tb, sl.Tribes!, $"slot {k} tribes"); else c.Str(null, sl.Tribes == null ? null : "tribes", $"slot {k} tribes");
            c.Num(v["team"], sl.Team, $"slot {k} team"); c.Num(v["popMax"], sl.PopMax, $"slot {k} popMax");
            var res = (JsonObject)v["res"]!;
            c.Int(res.Count, sl.Res.Count, $"slot {k} res");
            foreach (var (rk, rv) in res) c.Num(rv, sl.Res[rk], $"slot {k} res {rk}");
        }
        void Pairs(JsonNode? j, List<double[]> l, string what) { var a = (JsonArray)j!; c.Count(a, l.Count, what); for (int i = 0; i < a.Count; i++) { c.Num(a[i]![0], l[i][0], what); c.Num(a[i]![1], l[i][1], what); } }
        Pairs(e["starts"], S.Starts, "starts"); Pairs(e["bases"], S.Bases, "bases");
        var ss = (JsonArray)e["startSlots"]!;
        c.Count(ss, S.StartSlots.Count, "startSlots");
        for (int i = 0; i < ss.Count; i++) c.Num(ss[i], S.StartSlots[i], "start slot");
        c.Strs(e["models"], S.Models, "models");

        var tr2 = e["trees"]!;
        c.Int(tr2["n"], S.Trees.Count, "trees");
        c.Num(tr2["x"], S.Trees.Sum(q => q.X), "trees x", 1e-7); c.Num(tr2["z"], S.Trees.Sum(q => q.Z), "trees z", 1e-7); c.Num(tr2["rot"], S.Trees.Sum(q => q.Rot), "trees rot", 1e-7);
        c.Num(tr2["wood"], S.Trees.Sum(q => q.Wood), "trees wood", 0);
        c.Int(tr2["inside"], S.Trees.Count(q => q.Inside), "trees inside"); c.Int(tr2["big"], S.Trees.Count(q => q.Big), "trees big"); c.Int(tr2["forest"], S.Trees.Count(q => q.Forest), "forest trees");
        c.Int(tr2["names"], Cmp.FnvStr(string.Join("|", S.Trees.Select(q => q.Model + "/" + (q.Stump ?? "null") + "/" + (q.Timber ?? "null")))), "tree models");
        var tf = (JsonArray)tr2["first"]!;
        for (int i = 0; i < tf.Count; i++)
        {
            var a = (JsonArray)tf[i]!; var q = S.Trees[i];
            c.Str(a[0], q.Model, "tree model"); c.Num(a[1], q.X, "tree x"); c.Num(a[2], q.Z, "tree z"); c.Num(a[3], q.Rot, "tree rot"); c.Num(a[4], q.Wood, "tree wood");
            c.Bool(a[5], q.Inside, "tree inside"); c.Str(a[6], q.Stump, "tree stump"); c.Str(a[7], q.Timber, "tree timber"); c.Bool(a[8], q.Big, "tree big");
        }
        var de = e["decor"]!;
        c.Int(de["n"], S.Decor.Count, "decor");
        c.Num(de["x"], S.Decor.Sum(q => q.X), "decor x", 1e-7); c.Num(de["z"], S.Decor.Sum(q => q.Z), "decor z", 1e-7); c.Num(de["rot"], S.Decor.Sum(q => q.Rot), "decor rot", 1e-7);
        c.Num(de["y"], S.Decor.Sum(q => q.Y ?? 0), "decor y", 1e-7);
        c.Int(de["block"], S.Decor.Count(q => q.Block), "decor blocking"); c.Int(de["forest"], S.Decor.Count(q => q.Forest), "forest decor"); c.Int(de["q"], S.Decor.Count(q => q.Q != null), "decor with q");
        c.Int(de["names"], Cmp.FnvStr(string.Join("|", S.Decor.Select(q => q.Model))), "decor models");
        var df = (JsonArray)de["first"]!;
        for (int i = 0; i < df.Count; i++)
        {
            var a = (JsonArray)df[i]!; var q = S.Decor[i];
            c.Str(a[0], q.Model, "decor model"); c.Num(a[1], q.X, "decor x"); c.Num(a[2], q.Z, "decor z"); c.Num(a[3], q.Rot, "decor rot"); c.Bool(a[4], q.Block, "decor block"); c.Num(a[5], q.Y, "decor y");
        }
        void Res(JsonNode? j, List<MapResource> l, string what, bool rot)
        {
            var a = (JsonArray)j!; c.Count(a, l.Count, what);
            for (int i = 0; i < a.Count; i++)
            {
                var r = (JsonArray)a[i]!; int k = 0;
                c.Str(r[k++], l[i].Model, what + " model"); c.Num(r[k++], l[i].X, what + " x"); c.Num(r[k++], l[i].Z, what + " z");
                if (rot) c.Num(r[k++], l[i].Rot, what + " rot");
                c.Num(r[k], l[i].Amount, what + " amount");
            }
        }
        Res(e["stones"], S.Stones, "stones", true); Res(e["bushes"], S.Bushes, "bushes", true); Res(e["fish"], S.Fish, "fish", false);
        var an = (JsonArray)e["animals"]!;
        c.Count(an, S.Animals.Count, "animals");
        for (int i = 0; i < an.Count; i++) { c.Str(an[i]![0], S.Animals[i].Species, "animal"); c.Num(an[i]![1], S.Animals[i].X, "animal x"); c.Num(an[i]![2], S.Animals[i].Z, "animal z"); }
        var ne = (JsonArray)e["nests"]!;
        c.Count(ne, S.Nests.Count, "nests");
        for (int i = 0; i < ne.Count; i++)
        {
            var j = ne[i]!; var n = S.Nests[i]; string w = "nest " + n.Name;
            c.Str(j["name"], n.Name, w); c.Str(j["species"], n.Species, w + " species"); c.Num(j["x"], n.X, w + " x"); c.Num(j["z"], n.Z, w + " z");
            c.Num(j["max"], n.Max, w + " max"); c.Num(j["rate"], n.Rate, w + " rate"); c.Num(j["amount"], n.Amount, w + " amount");
            c.Bool(j["swim"], n.Swim, w + " swim"); c.Bool(j["grown"], n.Grown, w + " grown");
            Boxes(c, j["action"], n.ActionArea, w + " action area"); Boxes(c, j["safe"], n.SafeArea, w + " safe area"); Boxes(c, j["hotspot"], n.HotspotArea, w + " hotspot area");
            var tb = (JsonArray)j["table"]!;
            c.Count(tb, n.Table.Count, w + " timetable");
            for (int k = 0; k < tb.Count; k++) { var row = (JsonArray)tb[k]!; c.Count(row, n.Table[k].Length, w + " timetable row"); for (int m = 0; m < row.Count; m++) c.Num(row[m], n.Table[k][m], w + " timetable"); }
            var st = (JsonArray)j["start"]!;
            c.Count(st, n.Start.Count, w + " start");
            for (int k = 0; k < st.Count; k++) c.Num(st[k], n.Start[k].Born, w + " born");
        }
        var wl = (JsonArray)e["wells"]!;
        c.Count(wl, S.Wells.Count, "wells");
        for (int i = 0; i < wl.Count; i++) { c.Str(wl[i]![0], S.Wells[i].Model, "well"); c.Str(wl[i]![1], S.Wells[i].Cls, "well cls"); c.Num(wl[i]![2], S.Wells[i].X, "well x"); c.Num(wl[i]![4], S.Wells[i].Y, "well y"); c.Num(wl[i]![5], S.Wells[i].Rot, "well rot"); }
        var lk = (JsonArray)e["lakes"]!;
        c.Count(lk, S.Lakes.Count, "lakes");
        for (int i = 0; i < lk.Count; i++)
        {
            var j = lk[i]!; var l = S.Lakes[i];
            c.Str(j["name"], l.Name, "lake"); c.Str(j["region"], l.Region, "lake region"); c.Bool(j["heal"], l.Heal, "lake heal"); c.Num(j["amount"], l.Amount, "lake amount"); c.Str(j["types"], l.Types, "lake types");
        }
        var it = (JsonArray)e["items"]!;
        c.Count(it, S.Items.Count, "items");
        for (int i = 0; i < it.Count; i++) { c.Str(it[i]!["cls"], S.Items[i].Cls, "item"); c.Num(it[i]!["x"], S.Items[i].X, "item x"); c.Num(it[i]!["rot"], S.Items[i].Rot, "item rot"); }
        var isp = (JsonArray)e["itemSpawns"]!;
        c.Count(isp, S.ItemSpawns.Count, "item spawns");
        for (int i = 0; i < isp.Count; i++)
        {
            var j = isp[i]!; var s = S.ItemSpawns[i];
            c.Num(j["x"], s.X, "spawn x"); c.Strs(j["items"], s.Items, "spawn items"); c.Bool(j["always"], s.Always, "spawn always"); c.Bool(j["random"], s.Random, "spawn random");
        }
        var fl = (JsonArray)e["flags"]!;
        c.Count(fl, S.Flags.Count, "flags");
        for (int i = 0; i < fl.Count; i++) { c.Str(fl[i]!["name"], S.Flags[i].Name, "flag"); c.Str(fl[i]!["model"], S.Flags[i].Model, "flag model"); c.Num(fl[i]!["x"], S.Flags[i].X, "flag x"); }
        var ow = (JsonArray)e["owned"]!;
        c.Count(ow, S.Owned.Count, "owned");
        for (int i = 0; i < ow.Count; i++)
        {
            var a = (JsonArray)ow[i]!; var o = S.Owned[i];
            c.Str(a[0], o.Type, "owned type"); c.Str(a[1], o.Cls, "owned cls"); c.Num(a[2], o.X, "owned x"); c.Num(a[4], o.Rot, "owned rot"); c.Num(a[5], o.Owner, "owned owner"); c.Num(a[6], o.Level, "owned level");
        }
        var sk = e["skipped"]!;
        c.Int(sk["owned"], skipped.Owned.Count, "campaign: owned"); c.Int(sk["decor"], skipped.Decor.Count, "campaign: decor"); c.Int(sk["trees"], skipped.Trees.Count, "campaign: trees");

        // the world's height field and the per-point queries (at the JS's cell)
        var hf = new HeightField(S.Size, e["meshCell"]!.GetValue<double>(), S.Height);
        c.Int(e["hf"]!["n"], hf.N, "height field n"); c.Int(e["hf"]!["hash"], Cmp.FnvF32(hf.H), "height field samples");
        var slope = MapSource.SlopeWalkable(hf.At);
        foreach (var sp in (JsonArray)e["samples"]!)
        {
            var a = (JsonArray)sp!;
            double x = a[0]!.GetValue<double>(), z = a[1]!.GetValue<double>();
            string w = $"at ({x:0.##}, {z:0.##})";
            c.Num(a[2], S.Height(x, z), "height " + w, 1e-12);
            c.Num(a[3], hf.At(x, z), "height field " + w, 1e-12);
            if (a[4] == null) c.Str(null, S.Walkable == null ? null : "walkable", "walkable " + w);
            else c.Int(a[4], S.Walkable!(x, z) ? 1 : 0, "walkable " + w);
            c.Int(a[5], slope(x, z) ? 1 : 0, "slope walkable " + w);
            c.Int(a[6], S.SoundMat!(x, z), "sound material " + w);
            c.Num(a[7], hf.NormalY(x, z), "normalY " + w, 1e-12);
        }
        var sp0 = (JsonArray)e["samples"]!;
        var splat = S.Splat(hf);
        var spl = (JsonArray)e["splat"]!;
        for (int i = 0; i < spl.Count; i++)
        {
            double x = sp0[i]![0]!.GetValue<double>(), z = sp0[i]![1]!.GetValue<double>();
            var wts = splat(x, z);
            var ew = (JsonArray)spl[i]!;
            c.Count(ew, wts.Length, "splat");
            for (int k = 0; k < ew.Count; k++) c.Num(ew[k], wts[k], $"splat {k} at ({x:0.##}, {z:0.##})", 1e-9);
        }
    }

    /// <summary>the random jungle map against the JS (same seed, same map)</summary>
    public static void GeneratedMapMatchesJs(TestContext t)
    {
        var path = Path.Combine(GoldenDir(t), "maps", "generated.json");
        var all = (JsonArray)ReadGolden(path);
        foreach (var e in all)
        {
            int players = e!["players"]!.GetValue<int>();
            double seed = e["seed"]!.GetValue<double>();
            var c = new Cmp(t, $"generated {players}p seed {seed}");
            var S = MapSource.GeneratedSource(new MapSourceConfig { Players = players, Seed = seed });
            c.Num(e["size"], S.Size, "size", 0);
            c.Num(e["play"]!["x0"], S.Play.X0, "play", 0);
            c.Count(e["starts"], S.Starts.Count, "starts");
            c.Strs(e["models"], S.Models, "models");
            var hf = new HeightField(S.Size, S.MeshCell, S.Height);
            c.Int(e["hf"]!["n"], hf.N, "hf n");
            var tr = e["trees"]!;
            c.Int(tr["n"], S.Trees.Count, "trees");
            c.Num(tr["x"], S.Trees.Sum(q => q.X), "trees x", 1e-7); c.Num(tr["z"], S.Trees.Sum(q => q.Z), "trees z", 1e-7); c.Num(tr["scale"], S.Trees.Sum(q => q.Scale), "trees scale", 1e-7);
            c.Int(tr["names"], Cmp.FnvStr(string.Join("|", S.Trees.Select(q => q.Model))), "tree models");
            var tf = (JsonArray)tr["first"]!;
            for (int i = 0; i < tf.Count; i++)
            {
                var a = (JsonArray)tf[i]!; var q = S.Trees[i];
                c.Str(a[0], q.Model, "tree"); c.Num(a[1], q.X, "tree x"); c.Num(a[2], q.Z, "tree z"); c.Num(a[3], q.Rot, "tree rot"); c.Num(a[4], q.Scale, "tree scale");
                c.Num(a[5], q.Wood, "tree wood"); c.Bool(a[6], q.Inside, "tree inside"); c.Bool(a[7], q.Big, "tree big");
            }
            var de = e["decor"]!;
            c.Int(de["n"], S.Decor.Count, "decor"); c.Num(de["x"], S.Decor.Sum(q => q.X), "decor x", 1e-7);
            c.Int(de["names"], Cmp.FnvStr(string.Join("|", S.Decor.Select(q => q.Model))), "decor models"); c.Int(de["block"], S.Decor.Count(q => q.Block), "decor block");
            void Things(JsonNode? j, List<MapResource> l, string what)
            {
                var a = (JsonArray)j!; c.Count(a, l.Count, what);
                for (int i = 0; i < a.Count; i++) { c.Num(a[i]![0], l[i].X, what + " x"); c.Num(a[i]![1], l[i].Z, what + " z"); c.Num(a[i]![2], l[i].Rot, what + " rot"); }
            }
            Things(e["stones"], S.Stones, "stones"); Things(e["bushes"], S.Bushes, "bushes");
            var an = (JsonArray)e["animals"]!;
            c.Count(an, S.Animals.Count, "animals");
            for (int i = 0; i < an.Count; i++)
            {
                var a = (JsonArray)an[i]!; var m = S.Animals[i];
                c.Str(a[0], m.Species, "animal"); c.Num(a[1], m.X, "animal x"); c.Num(a[2], m.Z, "animal z"); c.Num(a[3], m.Home[0], "animal home x");
            }
            foreach (var sp in (JsonArray)e["samples"]!)
            {
                var a = (JsonArray)sp!;
                double x = a[0]!.GetValue<double>(), z = a[1]!.GetValue<double>();
                c.Num(a[2], S.Height(x, z), $"height at ({x:0.##}, {z:0.##})", 1e-9);
                c.Num(a[3], hf.At(x, z), $"height field at ({x:0.##}, {z:0.##})", 1e-6);
            }
            var splat = S.Splat(hf);
            var spl = (JsonArray)e["splat"]!;
            var pts = (JsonArray)e["samples"]!;
            for (int i = 0; i < spl.Count; i++)
            {
                double x = pts[i]![0]!.GetValue<double>(), z = pts[i]![1]!.GetValue<double>();
                var w = splat(x, z);
                for (int k = 0; k < 4; k++) c.Num(spl[i]![k], w[k], $"splat {k} at ({x:0.##}, {z:0.##})", 1e-6);
            }
            // the float32 samples can differ in the last bit where JS and .NET sin/cos differ: compare them as numbers
            // (the hash is exact on the original maps, whose heights are bilinear only)
            if (Cmp.FnvF32(hf.H) != e["hf"]!["hash"]!.GetValue<uint>()) t.Log($"generated {players}p: height field hash differs (last-bit trigonometry); samples agree");
        }
    }

    /// <summary>ground brushes (ground.js) against the JS: levelling and painted cells</summary>
    public static void GroundBrushesMatchJs(TestContext t)
    {
        var e = ReadGolden(Path.Combine(GoldenDir(t), "maps", "ground.json"));
        string file = e["file"]!.GetValue<string>();
        if (!File.Exists(file)) t.Skip(file + " not staged");
        var brushes = GroundBrushes.LoadFile(file);
        var c = new Cmp(t, "ground");
        c.Num(e["mpp"], brushes.Mpp, "metres per pixel", 0);
        static double fn(double x, double z) => 20 + 6 * Math.Sin(x * 0.05) * Math.Cos(z * 0.07) + x * 0.05;
        foreach (var cs in (JsonArray)e["cases"]!)
        {
            string name = cs!["name"]!.GetValue<string>();
            var hf = new HeightField(256, 2, fn);
            var b = brushes.Get(name)!;
            double x = cs["x"]!.GetValue<double>(), z = cs["z"]!.GetValue<double>(), heading = cs["heading"]!.GetValue<double>();
            c.Int(cs["w"], b.W, name + " w");
            var r = Ground.LevelGround(hf, b, brushes.Mpp, x, z, heading);
            if (cs["rect"] is JsonObject rc) { c.Int(rc["i0"], r!.Value.I0, name + " i0"); c.Int(rc["j1"], r.Value.J1, name + " j1"); }
            else c.Str(null, r == null ? null : "rect", name + " rect");
            c.Num(cs["at"], hf.At(x, z), name + " levelled height", 1e-6);
            var cells = Ground.BrushCells(b, brushes.Mpp, x, z, heading, GroundBrushes.MaterialCellOf(256));
            var ec = (JsonArray)cs["cells"]!;
            c.Count(ec, cells.Count, name + " cells");
            for (int i = 0; i < ec.Count; i++) { c.Int(ec[i]![0], cells[i].I, name + " cell i"); c.Int(ec[i]![1], cells[i].J, name + " cell j"); c.Int(ec[i]![2], cells[i].Material, name + " cell material"); }
            if (Cmp.FnvF32(hf.H) != cs["hash"]!.GetValue<uint>()) t.Log($"{name}: levelled samples hash differs (the test terrain uses sin/cos)");
        }
    }
}
