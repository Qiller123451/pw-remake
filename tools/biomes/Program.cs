// tools/biomes: makes and edits biome layers (docs/BIOMES.md "Authoring").
//
//   dotnet run --project tools/biomes -- <command> [options]
//
//   gradient  --map <map> --from <biome> --to <biome> (--split east|west|north|south [--band <m>] | --line x0,y0,x1,y1)
//             [--noise <m>] [--seed <n>] [--cell <m>] --out <x.biomes.json>
//             [--pwmap <x.pwmap.json> --base <path below Data> --name <text> [--description <text>]]
//             a two-biome layer: `from` on one side, `to` on the other, a soft noisy transition between (the map's
//             size from the map); with --pwmap also a remake map that uses it (game/maps: the skirmish list's "Remake")
//   paint     --layer <x.biomes.json> --biome <biome> --at x,y --radius <m> [--strength 0..1] [--soft 0..1]
//             [--noise 0..1] [--seed <n>] [--out <file>]     a soft round blob of a biome (added to the layer if new)
//   info      --layer <x.biomes.json>                        the biomes, the grid, each biome's share, a small picture
//   remap     --from <biome> --to <biome>                    which material of `to` each material of `from` becomes
//   materials --map <map> [--to <biome>]                     how much of the map each ground material covers, its use
//                                                            (MapExtension.MaterialRoles) and what it becomes in `to`
//   biomes    [--write <game/maps/biomes.json>]              the biomes; --write: the original settings' entries made
//                                                            again from BiomeSet.Original, the file's others kept
//
// <map>: a .ula / .pwmap.json path, or a map name found below <install>/Data/*/Maps (--install or PW_INSTALL).
// Coordinates: map coordinates (x east, y south, metres from the map's north-west corner).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PwRemake.Sim.Maps;

namespace PwRemake.Tools.Biomes;

public static class Program
{
    static Dictionary<string, string> _a = new();
    static string Arg(string k, string? def = null) => _a.TryGetValue(k, out var v) ? v : def ?? throw new ArgumentException($"--{k} is needed");
    static double Num(string k, double def) => _a.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;
    static double[] Nums(string k) => Arg(k).Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();

    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("commands: gradient, paint, info, remap, biomes (see tools/biomes/Program.cs)"); return 1; }
        for (int i = 1; i < args.Length; i++)
            if (args[i].StartsWith("--")) { var k = args[i][2..]; _a[k] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "1"; }
        var install = Arg("install", Environment.GetEnvironmentVariable("PW_INSTALL") ?? "");
        MapFiles.UseInstall(install);
        try
        {
            switch (args[0])
            {
                case "gradient": Gradient(install); break;
                case "paint": PaintCmd(); break;
                case "info": Info(BiomeLayer.Read(Arg("layer"))); break;
                case "remap": RemapCmd(); break;
                case "materials": MaterialsCmd(install); break;
                case "biomes": BiomesCmd(); break;
                default: Console.Error.WriteLine($"unknown command {args[0]}"); return 1;
            }
        }
        catch (Exception e) { Console.Error.WriteLine("error: " + e.Message); return 2; }
        return 0;
    }

    static string FindMap(string install, string map)
    {
        if (File.Exists(map)) return map;
        var data = Path.Combine(install, "Data");
        if (Directory.Exists(data))
            foreach (var f in Directory.EnumerateFiles(data, "*.ula", SearchOption.AllDirectories))
                if (Path.GetFileNameWithoutExtension(f).Equals(Path.GetFileNameWithoutExtension(map), StringComparison.OrdinalIgnoreCase)) return f;
        throw new FileNotFoundException($"map {map} (give a path, or --install <ParaWorld>)");
    }

    static void Check(string id) { if (BiomeSet.Default.Get(id) == null) throw new ArgumentException($"no biome {id} ({string.Join(", ", BiomeSet.Default.All.Select(b => b.Id))})"); }

    static void Gradient(string install)
    {
        var mapPath = FindMap(install, Arg("map"));
        var md = MapFiles.Read(mapPath);
        string from = Arg("from"), to = Arg("to");
        Check(from); Check(to);
        var l = BiomeLayer.For(md, new[] { from, to }, Num("cell", 8));
        (double, double) a, b;
        if (_a.ContainsKey("line")) { var p = Nums("line"); a = (p[0], p[1]); b = (p[2], p[3]); }
        else (a, b) = BiomeAuthoring.Split(l, Arg("split", "east"), Num("band", 240));
        BiomeAuthoring.Gradient(l, 0, 1, a, b, Num("noise", 60), Num("seed", 7));
        var outp = Arg("out");
        l.Write(outp);
        Console.WriteLine($"{outp}: {md.Name} ({md.Setting}, {md.Hx * 2} x {md.Hy * 2} m), {from} at {a} -> {to} at {b}");
        Info(l);
        if (_a.TryGetValue("pwmap", out var pw))
        {
            var node = new System.Text.Json.Nodes.JsonObject
            {
                ["format"] = "pw-remake-map", ["version"] = 1, ["base"] = Arg("base"),
                ["name"] = Arg("name"), ["description"] = Arg("description", ""), ["author"] = Arg("author", ""),
                ["biomes"] = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(pw))!, Path.GetFullPath(outp)).Replace('\\', '/'),
            };
            File.WriteAllText(pw, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
            Console.WriteLine($"{pw}: remake map on {Arg("base")}");
        }
    }

    static void PaintCmd()
    {
        var path = Arg("layer");
        var l = BiomeLayer.Read(path);
        var id = Arg("biome");
        Check(id);
        int b = l.AddBiome(id);
        var at = Nums("at");
        var r = BiomeAuthoring.Paint(l, b, at[0], at[1], Num("radius", 100), Num("strength", 1), Num("soft", 0.5), Num("noise", 0.25), Num("seed", 13));
        l.Write(Arg("out", path));
        Console.WriteLine(r == null ? "nothing painted (outside the layer?)" : $"painted {id} around {at[0]}, {at[1]}: {r}");
        Info(l);
    }

    static void Info(BiomeLayer l)
    {
        var share = new double[l.N];
        for (int j = 0; j < l.H; j++) for (int i = 0; i < l.W; i++) { var w = l.CellWeights(i, j); for (int b = 0; b < l.N; b++) share[b] += w[b]; }
        Console.WriteLine($"grid {l.W} x {l.H} cells of {l.Cell} m from ({l.X0}, {l.Y0}); " + string.Join(", ", l.Biomes.Select((b, k) => $"{b} {100 * share[k] / (l.W * l.H):0.#} %")));
        // a picture: one character per block of cells, the strongest biome (0-9), '+' where none has 75 %
        int step = Math.Max(1, Math.Max(l.W, l.H) / 48);
        for (int j = 0; j < l.H; j += step)
        {
            var sb = new System.Text.StringBuilder("  ");
            for (int i = 0; i < l.W; i += step)
            {
                var w = l.CellWeights(i, j);
                int m = Array.IndexOf(w, w.Max());
                sb.Append(w[m] >= 0.75 ? (char)('0' + m % 10) : '+');
            }
            Console.WriteLine(sb);
        }
    }

    static void RemapCmd()
    {
        var s = BiomeSet.Default;
        var a = s.Get(Arg("from")) ?? throw new ArgumentException("no biome " + Arg("from"));
        var b = s.Get(Arg("to")) ?? throw new ArgumentException("no biome " + Arg("to"));
        var r = BiomeSet.Remap(a, b);
        for (int k = 0; k < 8; k++) Console.WriteLine($"{a.Id} {k} ({a.Roles[k]}) -> {b.Id} {r[k]} ({b.Roles[r[k]]})");
    }

    static void MaterialsCmd(string install)
    {
        var md = MapFiles.Read(FindMap(install, Arg("map")));
        var n = new int[8];
        foreach (var m in md.Mats) n[m & 7]++;
        var set = BiomeSet.Default;
        var from = set.Get(md.Setting) ?? throw new ArgumentException("no biome for the setting " + md.Setting);
        var to = _a.TryGetValue("to", out var t) ? set.Get(t) : null;
        var rm = to != null ? BiomeSet.Remap(from, to) : null;
        var roles = PwRemake.Sim.Maps.MapExtension.MaterialRoles(md);
        Console.WriteLine($"{md.Name} ({md.Setting}): under water {roles.Under}, shore {roles.Shore}, land {roles.Land} / {roles.Land2}, steep {roles.Steep}");
        for (int k = 0; k < 8; k++)
            Console.WriteLine($"  {k} {from.Roles[k],-7} {100.0 * n[k] / Math.Max(1, md.Mats.Length),5:0.0} %" + (rm != null ? $"  -> {to!.Id} {rm[k]} ({to.Roles[rm[k]]})" : ""));
    }

    static void BiomesCmd()
    {
        var s = BiomeSet.Default;
        if (_a.TryGetValue("write", out var f))
        {
            var res = BiomeSet.Original();
            foreach (var b in s.All) if (!b.Original) res.Add(b);
            File.WriteAllText(f, res.ToJson() + "\n");
            Console.WriteLine($"{f}: {res.All.Count} biomes");
            return;
        }
        Console.WriteLine($"biomes of {s.Source}:");
        foreach (var b in s.All) Console.WriteLine($"  {b.Id,-12} ground {b.Ground,-10} grass {b.Grass ?? "-",-22} ambience {b.Ambience ?? "-",-10}{(b.Look != null ? " own light" : "")}{(b.Original ? " (original setting)" : "")}");
    }
}
