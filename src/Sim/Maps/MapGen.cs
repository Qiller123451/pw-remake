// Jungle skirmish map: SEAS in the south-west, Dustriders in the north-east, wild dinosaurs in between. The remake's
// own random map (MapSource.GeneratedSource) - no original map file behind it.
// (The JS keeps MAP as a module object that setPlayers changes; here it is a MapGen instance.)
// Ported from remake/src/game/mapgen.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

public sealed class GenTree { public string Model = ""; public double X, Z, Rot, Scale, Wood; public bool Inside; }
public sealed class GenThing { public double X, Z, Rot; }
public sealed class GenDecor { public string Model = ""; public double X, Z, Rot, Scale; public bool Block; }
public sealed class GenAnimal { public string Species = ""; public double X, Z; public double[] Home = new double[2]; }

public sealed class GenResult
{
    public List<GenTree> Trees = new();
    public List<GenDecor> Decor = new();
    public List<GenThing> Stones = new(), Bushes = new();
    public List<GenAnimal> Animals = new();
}

public sealed class MapGen
{
    /// <summary>playable square</summary>
    public double Size = 480;
    /// <summary>rendered terrain (border forest beyond the playable area)</summary>
    public double World = 640;
    public List<double[]> Bases = new(BASES2);

    // the bases for a number of players: two in opposite corners, up to four in the four corners (the first two stay
    // where they are, so a two-player map is the same as before). The remake's bigger jungles (not in the JS): 5 - 8
    // players on a large square (800 m: the bases on a 3 x 3 grid without its centre, 260 m apart), 9 - 16 on a huge
    // one (1120 m: a 4 x 4 grid, 280 m apart) - the corners first, so a few players still sit far apart.
    static readonly double[][] BASES2 = { new double[] { -150, 150 }, new double[] { 150, -150 } };
    static readonly double[][] BASES4 = { BASES2[0], BASES2[1], new double[] { 150, 150 }, new double[] { -150, -150 } };
    static readonly double[][] BASES8 = Grid(new[] { -260.0, 0, 260 }, new[] { (0, 2), (2, 0), (2, 2), (0, 0), (1, 2), (1, 0), (0, 1), (2, 1) });
    static readonly double[][] BASES16 = Grid(new[] { -420.0, -140, 140, 420 }, new[]
    {
        (0, 3), (3, 0), (3, 3), (0, 0), (1, 3), (2, 0), (3, 2), (0, 1), (2, 3), (1, 0), (3, 1), (0, 2), (1, 2), (2, 1), (2, 2), (1, 1),
    });
    static double[][] Grid(double[] at, (int I, int J)[] order) => Array.ConvertAll(order, o => new[] { at[o.I], -at[o.J] });
    /// <summary>the most bases a random jungle has (the huge one)</summary>
    public const int MAX_PLAYERS = 16;
    /// <summary>the most players of each size: small (the original random jungle), large, huge</summary>
    public static readonly int[] SIZE_PLAYERS = { 4, 8, 16 };
    /// <summary>the playable square of each size (m)</summary>
    public static readonly double[] SIZES = { 480, 800, 1120 };
    /// <summary>the size of the random jungle for n players (0 small, 1 large, 2 huge)</summary>
    public static int SizeFor(int n) => n <= 4 ? 0 : n <= 8 ? 1 : 2;
    /// <summary>playable area of this map relative to the small one (how many more trees, stones, herds)</summary>
    double AreaK => (Size + 80) * (Size + 80) / (560.0 * 560.0);

    public void SetPlayers(int n)
    {
        int k = SizeFor(n);
        Size = SIZES[k]; World = Size + 160;
        Bases = k == 0 ? (n > 2 ? new List<double[]>(BASES4[..Math.Min(4, n)]) : new List<double[]>(BASES2))
              : new List<double[]>((k == 1 ? BASES8 : BASES16)[..Math.Min(k == 1 ? 8 : 16, n)]);
    }

    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
    static double Smoothstep(double a, double b, double x) => Noise.Smoothstep(a, b, x);

    public double HeightFn(double x, double z)
    {
        double h = 3.2 * SMath.Sin(x * 0.013 + 0.7) * SMath.Cos(z * 0.017 - 0.4) + 1.6 * SMath.Sin(x * 0.037 + z * 0.029 + 2.1);
        h += (Noise.Fbm(x * 0.011, z * 0.011, 7, 4) - 0.5) * 12;
        foreach (var b in Bases)
        {
            double d = Hypot(x - b[0], z - b[1]);
            double k = Smoothstep(40, 80, d);
            h = 1.5 + (h - 1.5) * k;
        }
        double e = Math.Max(Math.Abs(x), Math.Abs(z));
        h += Smoothstep(Size / 2 - 10, World / 2, e) * 22;
        return h;
    }

    public double ForestDensity(double x, double z)
    {
        double d = Noise.Fbm(x * 0.009 + 3.1, z * 0.009 - 1.7, 3, 4);
        d = (d - 0.38) * 2.1;
        foreach (var b in Bases) d -= (1 - Smoothstep(45, 85, Hypot(x - b[0], z - b[1]))) * 1.6;
        // a lightly wooded corridor between the bases
        double[] b0 = Bases[0], b1 = Bases[1];
        double t = ((x - b0[0]) * (b1[0] - b0[0]) + (z - b0[1]) * (b1[1] - b0[1])) /
                   (SMath.Pow(b1[0] - b0[0], 2) + SMath.Pow(b1[1] - b0[1], 2));
        double px = b0[0] + t * (b1[0] - b0[0]), pz = b0[1] + t * (b1[1] - b0[1]);
        double wig = SMath.Sin(t * 9) * 30;
        d -= (1 - Smoothstep(10, 26, Hypot(x - px - wig * 0.7, z - pz - wig * 0.7))) * 0.8;
        double e = Math.Max(Math.Abs(x), Math.Abs(z));
        d += Smoothstep(Size / 2 - 24, Size / 2 + 4, e) * 1.4;
        return d;
    }

    /// <summary>ground material weights [green grass, yellow grass, dirt, rock]</summary>
    public Func<double, double, double[]> SplatFn(HeightField hf) => (x, z) =>
    {
        double n = Noise.Fbm(x * 0.02, z * 0.02, 11, 3), n2 = Noise.Fbm(x * 0.05 + 7, z * 0.05, 5, 2);
        double green = 0.6 + (n - 0.5) * 1.6, yellow = 0.4 - (n - 0.5) * 1.6, dirt = 0, rock;
        foreach (var b in Bases) dirt += (1 - Smoothstep(18, 42, Hypot(x - b[0], z - b[1]) + (n2 - 0.5) * 20)) * 1.4;
        double fd = ForestDensity(x, z);
        dirt += Smoothstep(0.55, 1.1, fd) * 0.35 * n2;
        const double e = 1.5;
        double sl = Hypot(hf.At(x + e, z) - hf.At(x - e, z), hf.At(x, z + e) - hf.At(x, z - e)) / (2 * e);
        rock = Smoothstep(0.35, 0.7, sl) * 2;
        green = Math.Max(0, green); yellow = Math.Max(0, yellow);
        return new[] { green, yellow, Math.Max(0, dirt), rock };
    };

    bool AnyBaseWithin(double x, double z, double r) { foreach (var b in Bases) if (Hypot(x - b[0], z - b[1]) < r) return true; return false; }

    /// <summary>Place trees, decor, stones, bushes and animals. Returns placement lists.</summary>
    public GenResult Generate(double seed = 1234)
    {
        var rng = new Mulberry32(seed);
        Func<double> rnd = rng.Next;
        var R = new GenResult();
        var occ = new Dictionary<double, List<double[]>>();
        double Key(double x, double z) => (Math.Floor(x / 6) + 1000) * 4000 + Math.Floor(z / 6) + 1000;
        bool Near(double x, double z, double d)
        {
            double cx = Math.Floor(x / 6), cz = Math.Floor(z / 6), k = Math.Ceiling(d / 6);
            for (double i = -k; i <= k; i++) for (double j = -k; j <= k; j++)
                if (occ.TryGetValue((cx + i + 1000) * 4000 + cz + j + 1000, out var l))
                    foreach (var p in l) if (Hypot(p[0] - x, p[1] - z) < d) return true;
            return false;
        }
        void Mark(double x, double z)
        {
            double k = Key(x, z);
            if (!occ.TryGetValue(k, out var l)) occ[k] = l = new List<double[]>();
            l.Add(new[] { x, z });
        }
        double H = Size / 2, WH = World / 2 - 30;
        // keep base areas clear
        foreach (var b in Bases) for (int a = 0; a < 30; a++) Mark(b[0] + SMath.Cos(a) * 10, b[1] + SMath.Sin(a) * 10);
        string[] bigTrees = { "jungle_tree_01", "jungle_tree_02", "jungle_tree_03", "jungle_tree_04", "jungle_tree_05" };
        string[] medTrees = { "jungle_tree_med_01", "jungle_tree_med_02", "jungle_tree_med_03", "jungle_tree_med_04", "jungle_tree_med_05", "jungle_tree_med_06" };
        int area = (int)Math.Round(AreaK * 1000);          // (1000 on the small map: the JS's counts)
        for (int i = 0; i < 60 * area; i++)
        {
            double x = (rnd() * 2 - 1) * (H + 40), z = (rnd() * 2 - 1) * (H + 40);
            double d = ForestDensity(x, z);
            double p = Smoothstep(0.35, 0.75, d);
            if (rnd() > p * 0.28) continue;
            double minD = 6.5 - 1.5 * Smoothstep(0.7, 1.2, d);
            if (Near(x, z, minD)) continue;
            Mark(x, z);
            bool big = rnd() < 0.62;
            string model = big ? bigTrees[(int)Math.Floor(rnd() * bigTrees.Length)] : medTrees[(int)Math.Floor(rnd() * medTrees.Length)];
            double rot = rnd() * 6.283, scale = 0.85 + rnd() * 0.3;
            R.Trees.Add(new GenTree { Model = model, X = x, Z = z, Rot = rot, Scale = scale, Wood = big ? 300 : 150, Inside = Math.Max(Math.Abs(x), Math.Abs(z)) < H - 4 });
        }
        // stones: 3 per base + neutral ones
        bool StoneAt(double x, double z) { if (!Near(x, z, 10)) { Mark(x, z); R.Stones.Add(new GenThing { X = x, Z = z, Rot = rnd() * 6.28 }); return true; } return false; }
        foreach (var b in Bases)
        {
            int n = 0;
            for (int k = 0; k < 200 && n < 3; k++) { double a = rnd() * 6.28, r = 38 + rnd() * 14; if (StoneAt(b[0] + SMath.Cos(a) * r, b[1] + SMath.Sin(a) * r)) n++; }
        }
        for (int k = 0, n = 0; k < 400 * area / 1000 && n < 8 * area / 1000; k++)
        {
            double x = (rnd() * 2 - 1) * (H - 40), z = (rnd() * 2 - 1) * (H - 40);
            if (AnyBaseWithin(x, z, 90)) continue;
            if (StoneAt(x, z)) n++;
        }
        // fruit bushes: groups near bases and in meadows
        void BushGroup(double cx, double cz, int n)
        {
            for (int k = 0, m = 0; k < 60 && m < n; k++)
            {
                double x = cx + (rnd() - 0.5) * 16, z = cz + (rnd() - 0.5) * 16;
                if (Near(x, z, 3.5)) continue;
                Mark(x, z); R.Bushes.Add(new GenThing { X = x, Z = z, Rot = rnd() * 6.28 }); m++;
            }
        }
        foreach (var b in Bases)
            for (int g = 0; g < 2; g++) { double a = rnd() * 6.28, r = 30 + rnd() * 12; BushGroup(b[0] + SMath.Cos(a) * r, b[1] + SMath.Sin(a) * r, 5); }
        for (int g = 0; g < 7 * area / 1000; g++)
        {
            double x = (rnd() * 2 - 1) * (H - 50), z = (rnd() * 2 - 1) * (H - 50);
            if (AnyBaseWithin(x, z, 80) || ForestDensity(x, z) > 0.4) continue;
            BushGroup(x, z, 4);
        }
        // decor: undergrowth along forest edges, flowers and rocks in meadows
        string[] edge = { "jungle_bush_01", "jungle_bush_02", "jungle_bush_03", "jungle_bush_04" };
        string[] meadow = { "jungle_flowers_01", "jungle_flowers_02", "jungle_reed_01" };
        string[] rocks = { "jungle_smallrock_01", "jungle_smallrock_03", "jungle_smallrock_05", "jung_middle_rock_01" };
        GenDecor Deco(string[] list, double x, double z, double s0, double s1, bool block)
        {
            string model = list[(int)Math.Floor(rnd() * list.Length)];
            double rot = rnd() * 6.28, scale = s0 + rnd() * s1;
            return new GenDecor { Model = model, X = x, Z = z, Rot = rot, Scale = scale, Block = block };
        }
        for (int i = 0; i < 9 * area; i++)
        {
            double x = (rnd() * 2 - 1) * WH, z = (rnd() * 2 - 1) * WH;
            double d = ForestDensity(x, z);
            if (AnyBaseWithin(x, z, 34)) continue;
            double pe = Smoothstep(0.1, 0.45, d) * (1 - Smoothstep(0.9, 1.3, d));
            double r = rnd();
            if (r < pe * 0.09) { if (!Near(x, z, 3)) R.Decor.Add(Deco(edge, x, z, 0.6, 0.35, false)); }
            else if (r < pe * 0.09 + (1 - Smoothstep(0, 0.3, d)) * 0.06) R.Decor.Add(Deco(meadow, x, z, 0.8, 0.5, false));
            else if (r > 0.9985 && !Near(x, z, 6)) { Mark(x, z); R.Decor.Add(Deco(rocks, x, z, 0.7, 0.5, true)); }
        }
        // wild animals: [species, count range, spawn rule]
        (string sp, int lo, int hi, int groups)[] herds =
        {
            ("Corythosaurus", 3, 5, 6), ("Tsintaosaurus", 3, 4, 4), ("Parasaurolophus", 2, 4, 4), ("Psittacosaurus", 3, 5, 3),
            ("Triceratops", 1, 2, 3), ("Stegosaurus", 1, 2, 2), ("Ankylosaurus", 1, 2, 2),
            ("Velociraptor", 3, 4, 3), ("Deinonychus", 2, 3, 2), ("Dilophosaurus", 2, 3, 3), ("Allosaurus", 1, 1, 2), ("Spinosaurus", 1, 1, 1),
        };
        var dangerous = new HashSet<string> { "Velociraptor", "Deinonychus", "Dilophosaurus", "Allosaurus", "Spinosaurus" };
        foreach (var (sp, lo, hi, groups) in herds)
        {
            bool danger = dangerous.Contains(sp);
            for (int g = 0; g < groups * area / 1000; g++)
            {
                double x = 0, z = 0;
                bool ok = false;
                for (int k = 0; k < 200; k++)
                {
                    x = (rnd() * 2 - 1) * (H - 30); z = (rnd() * 2 - 1) * (H - 30);
                    double bd = double.PositiveInfinity;
                    foreach (var b in Bases) bd = Math.Min(bd, Hypot(x - b[0], z - b[1]));
                    if (bd < (danger ? 150 : 110)) continue;          // herds roam ~30 m around home: keep them clear of the bases
                    if (ForestDensity(x, z) > 0.35) continue;
                    ok = true; break;
                }
                if (!ok) continue;
                int n = lo + (int)Math.Floor(rnd() * (hi - lo + 1));
                for (int i = 0; i < n; i++)
                {
                    double ax = x + (rnd() - 0.5) * 12, az = z + (rnd() - 0.5) * 12;
                    R.Animals.Add(new GenAnimal { Species = sp, X = ax, Z = az, Home = new[] { x, z } });
                }
            }
        }
        return R;
    }
}
