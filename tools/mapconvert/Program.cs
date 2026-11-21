// tools/mapconvert: converts the original maps and the campaign's mission folders to the remake's map format
// (docs/MAP_FORMAT.md "Converting"). The conversion itself is src/Sim/Maps/PwMapConvert.cs (the one place); this is its
// command line.
//
//   dotnet run --project tools/mapconvert -c Release -- <command> [options]
//
//   prepare   what Prepare runs, per configuration (incremental: a map is converted again only when its source files or
//             the converter's version changed - stamps in <content>/cache/maps/<config>.json; --force: all):
//               --config <content/configs/<id>>   the configuration (config.json: install, folders); or instead
//               --install <ParaWorld folder> --folders Base,BoosterPack1 --out <content/configs/<id>>
//               [--missions-from <dir>]  mission folders of the old layout (tools/prepare/campaign_to_lua.py's output:
//                                        index.json + <NN>_<key>/mission.json, world.json, texts/, triggers.lua) ->
//                                        <out>/missions/<NN>_<key>/ in the map format (custom.lua there is kept; the old
//                                        layout's JSON files there are removed); the same folder as <out>/missions:
//                                        converted in place, the JSON files kept beside (--clean removes them)
//               [--recipes <dir>]        the remake's map recipes (*.pwmap.json; default game/maps)
//               [--no-maps]              only the missions
//             every <install>/Data/<folder>/Maps/**/*.ula of the folders -> <out>/maps/<folder>/Maps/.../<name>/,
//             every recipe whose base map the installation has -> <out>/maps/Remake/<name>/
//   ula       <map.ula> <out folder | out.pwmap> [--id <Base/Maps/...ula>]     one original map
//   mission   <old mission folder> <map.ula> <out folder | out.pwmap>         one mission folder of the old layout
//   zip       <map folder> <out.pwmap>                                         a map folder as one file
//   unzip     <map.pwmap> <out folder>
//   check     <map folder | .pwmap> ...                                        reads each map, prints what it holds
//
// Exit code 0: all converted; 2: some failed (each failure printed); 1: bad arguments.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Tools.MapConvert;

public static class Program
{
    static readonly Dictionary<string, string> A = new();
    static readonly List<string> Pos = new();
    static string Arg(string k, string? def = null) => A.TryGetValue(k, out var v) ? v : def ?? throw new ArgumentException($"--{k} is needed");

    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("commands: prepare, ula, mission, zip, unzip, check (see tools/mapconvert/Program.cs)"); return 1; }
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--")) { var k = args[i][2..]; A[k] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "1"; }
            else Pos.Add(args[i]);
        }
        Log.DefaultLevel = LogLevel.Warn;
        try
        {
            switch (args[0])
            {
                case "prepare": return Prepare();
                case "ula":
                {
                    Need(2);
                    var m = PwMapConvert.FromUla(Pos[0], A.TryGetValue("id", out var id) ? id : null);
                    Save(m, Pos[1]);
                    Console.WriteLine($"{Pos[0]} -> {Pos[1]}: {Summary(m)}");
                    return 0;
                }
                case "mission":
                {
                    Need(3);
                    var mj = Json.TryParseFile(Path.Combine(Pos[0], "mission.json"));
                    var m = PwMapConvert.FromUla(Pos[1], mj?["map"].Str() is { Length: > 0 } src ? src : null);
                    PwMapConvert.AddMission(m, Pos[0]);
                    Save(m, Pos[2], A.ContainsKey("clean"));
                    Console.WriteLine($"{Pos[0]} + {Pos[1]} -> {Pos[2]}: {Summary(m)}");
                    return 0;
                }
                case "zip": Need(2); PwMapWriter.WriteZip(PwMapReader.Read(Pos[0]), Pos[1]); return 0;
                case "unzip": Need(2); PwMapWriter.WriteFolder(PwMapReader.Read(Pos[0]), Pos[1]); return 0;
                case "check":
                {
                    int bad = 0;
                    foreach (var p in Pos)
                        try { var sw = Stopwatch.StartNew(); var m = PwMapReader.Read(p); Console.WriteLine($"{p}: {Summary(m)} ({sw.ElapsedMilliseconds} ms)"); }
                        catch (Exception e) { Console.WriteLine($"{p}: ERROR {e.Message}"); bad++; }
                    return bad > 0 ? 2 : 0;
                }
                default: Console.Error.WriteLine($"unknown command {args[0]}"); return 1;
            }
        }
        catch (ArgumentException e) { Console.Error.WriteLine("error: " + e.Message); return 1; }
        catch (Exception e) { Console.Error.WriteLine("error: " + e.Message); return 2; }
    }

    static void Need(int n) { if (Pos.Count < n) throw new ArgumentException($"{n} paths are needed"); }

    static void Save(PwMap m, string outPath, bool clean = false)
    {
        if (outPath.EndsWith(PwMapPaths.Ext, StringComparison.OrdinalIgnoreCase)) PwMapWriter.WriteZip(m, outPath);
        else PwMapWriter.WriteFolder(m, outPath, clean);
    }

    static string Summary(PwMap m) =>
        $"{m.Name}, {m.Hx * 2} x {m.Hy * 2} m, {m.Setting}, {m.Objects.Count} objects, {m.Plants.Count} plants, {m.Forest.Blocks.Count} forest blocks, " +
        $"{m.Regions.Count} regions{(m.Walk != null ? ", walk grid" : "")}{(m.Biomes != null ? ", biomes " + string.Join("+", m.Biomes.Biomes) : "")}" +
        (m.IsMission ? $", mission {m.Mission!["id"].Str()} ({m.Texts.Count} languages, {(m.Triggers?.Length ?? 0) / 1024} KB triggers)" : "");

    // ------------------------------------------------------------------------------------------- prepare
    sealed class Stamps
    {
        readonly string _path;
        readonly JsonObject _j;
        public Stamps(string path) { _path = path; _j = Json.TryParseFile(path) as JsonObject ?? new JsonObject(); }
        public bool Same(string target, string key) => _j[target]?.GetValue<string>() == key;
        public void Set(string target, string key) => _j[target] = key;
        public void Save() { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, _j.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
    }

    /// <summary>the stamp of a conversion: the converter's version and a hash of every source file's bytes</summary>
    static string Key(IEnumerable<string> files)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.UTF8.GetBytes($"pwmapconvert {PwMapConvert.Version}\n"));
        foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
        {
            h.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(f) + "\n"));
            if (File.Exists(f)) h.AppendData(File.ReadAllBytes(f));
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    static int Prepare()
    {
        string outDir, install; List<string> folders;
        if (A.TryGetValue("config", out var cdir))
        {
            var cfg = GameConfiguration.Load(cdir) ?? throw new ArgumentException($"no configuration in {cdir}");
            outDir = cdir; install = cfg.Install; folders = cfg.Folders;
        }
        else { outDir = Arg("out"); install = Arg("install"); folders = Arg("folders").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(); }
        bool force = A.ContainsKey("force");
        var data = Path.Combine(install, "Data");
        if (!Directory.Exists(data)) throw new ArgumentException($"no Data folder in {install}");
        MapFiles.UseInstall(install);
        var id = Path.GetFileName(Path.GetFullPath(outDir).TrimEnd('/', '\\'));
        var content = Path.GetFullPath(Path.Combine(outDir, "..", ".."));
        var stamps = new Stamps(Path.Combine(content, "cache", "maps", id + ".json"));
        int done = 0, skipped = 0;
        var failed = new List<string>();
        var sw = Stopwatch.StartNew();

        void One(string what, string target, IEnumerable<string> sources, Func<PwMap> make, bool removeOld = false)
        {
            var key = Key(sources);
            var rel = Path.GetRelativePath(outDir, target).Replace('\\', '/');
            if (!force && stamps.Same(rel, key) && File.Exists(Path.Combine(target, PwMapPaths.Map))) { skipped++; return; }
            try
            {
                var m = make();
                PwMapWriter.WriteFolder(m, target, removeOld);
                stamps.Set(rel, key);
                done++;
                Console.WriteLine($"  {what} -> {rel}: {Summary(m)}");
            }
            catch (Exception e) { failed.Add($"{what}: {e.Message}"); Console.WriteLine($"  {what}: FAILED {e.Message}"); }
        }

        // ---- the installation's maps of the configuration's folders
        if (!A.ContainsKey("no-maps"))
        {
            foreach (var folder in folders)
            {
                var root = Directory.EnumerateDirectories(data).FirstOrDefault(d => string.Equals(Path.GetFileName(d), folder, StringComparison.OrdinalIgnoreCase));
                var maps = root == null ? null : Directory.EnumerateDirectories(root).FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Maps", StringComparison.OrdinalIgnoreCase));
                if (maps == null) continue;
                foreach (var f in Directory.EnumerateFiles(maps, "*.ula", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(data, f).Replace('\\', '/');
                    var side = BiomeLayer.SidecarOf(f);
                    One(rel, Path.Combine(outDir, "maps", MapFiles.FolderOf(rel)), new[] { f, side }, () => PwMapConvert.FromUla(f, rel));
                }
            }
            // ---- the remake's own maps (recipes: an original map + name, description, biomes)
            var recipes = A.TryGetValue("recipes", out var rd) ? rd : MapFiles.Root();
            if (Directory.Exists(recipes))
                foreach (var f in Directory.EnumerateFiles(recipes, "*" + MapFiles.RemakeExt).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(f);
                    string? bas;
                    try { bas = JsonNode.Parse(File.ReadAllText(f))?["base"]?.GetValue<string>(); }
                    catch (Exception e) { failed.Add($"{name}: {e.Message}"); continue; }
                    var basePath = bas == null ? null : MapFiles.FindBase(bas);
                    if (basePath == null) { Console.WriteLine($"  {name}: its base map {bas} is not in this installation - left out"); continue; }
                    var srcs = new List<string> { f, basePath };
                    if (JsonNode.Parse(File.ReadAllText(f))?["biomes"] is JsonValue bv && bv.TryGetValue<string>(out var bf)) srcs.Add(Path.Combine(recipes, bf));
                    One("Remake/" + name, Path.Combine(outDir, "maps", MapFiles.RemakeFolder, name[..^MapFiles.RemakeExt.Length]), srcs,
                        () => PwMapConvert.FromRemakeRecipe(f, MapFiles.FindBase, MapFiles.RemakeFolder + "/" + name));
                }
        }
        // ---- the campaign's missions
        if (A.TryGetValue("missions-from", out var from))
        {
            var missions = Path.Combine(outDir, "missions");
            bool inPlace = Path.GetFullPath(from).TrimEnd('/', '\\') == Path.GetFullPath(missions).TrimEnd('/', '\\');
            Directory.CreateDirectory(missions);
            foreach (var mdir in Directory.GetDirectories(from).OrderBy(x => x, StringComparer.Ordinal))
            {
                var mjf = Path.Combine(mdir, "mission.json");
                if (!File.Exists(mjf)) continue;
                var mj = Json.TryParseFile(mjf);
                var map = mj?["map"].Str() ?? "";
                var ula = FindUla(data, folders, map);
                var name = Path.GetFileName(mdir);
                if (ula == null) { failed.Add($"mission {name}: its map {map} is not in the installation"); Console.WriteLine($"  mission {name}: FAILED its map {map} is not in the installation"); continue; }
                var srcs = new List<string> { ula, BiomeLayer.SidecarOf(ula), mjf, Path.Combine(mdir, "world.json"), Path.Combine(mdir, "triggers.lua") };
                if (Directory.Exists(Path.Combine(mdir, "texts"))) srcs.AddRange(Directory.GetFiles(Path.Combine(mdir, "texts"), "*.json"));
                var target = Path.Combine(missions, name);
                // (custom.lua of the target is the player's: never overwritten - the source's only for a new folder)
                One("mission " + name, target, srcs, () =>
                {
                    var m = PwMapConvert.FromUla(ula, map);
                    PwMapConvert.AddMission(m, mdir);
                    if (!inPlace && File.Exists(Path.Combine(target, PwMapPaths.Custom))) m.Custom = null;
                    return m;
                }, removeOld: !inPlace || A.ContainsKey("clean"));
            }
            var idx = Path.Combine(from, "index.json");
            if (File.Exists(idx) && !inPlace) File.Copy(idx, Path.Combine(missions, "index.json"), true);
        }
        stamps.Save();
        Console.WriteLine($"maps: {done} converted, {skipped} unchanged, {failed.Count} failed ({sw.Elapsed.TotalSeconds:0.0} s)");
        foreach (var f in failed) Console.WriteLine("  failed: " + f);
        return failed.Count > 0 ? 2 : 0;
    }

    /// <summary>a mission's .ula: its path below the installation's Data folder, else the same path below each of the
    /// configuration's folders (the last one that has it) - the old WorldBuilder.FindMap</summary>
    static string? FindUla(string data, List<string> folders, string map)
    {
        if (string.IsNullOrEmpty(map)) return null;
        if (PathUtil.FindCaseInsensitive(data, map) is { } p) return p;
        var rest = System.Text.RegularExpressions.Regex.Replace(PathUtil.Norm(map), "^[^/]+/", "");
        for (int i = folders.Count - 1; i >= 0; i--)
            if (PathUtil.FindCaseInsensitive(Path.Combine(data, folders[i]), rest) is { } q) return q;
        return null;
    }
}
