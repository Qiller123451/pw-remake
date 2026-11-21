// The generator of the "Outbreak" mission (docs/OUTBREAK.md) - the remake's own single player / co-op mission after
// StarCraft II's "Outbreak": the players' base (about 240 m across) at the level of a snowy northern valley (768 m),
// enclosed by a ring of low rock ridges (small cliffs dressed with Icewaste's cliff pieces and rocks - natural terrain
// all round, round 5: no walls), with three passes - north and east open, the south-west one blocked by a rockslide the
// infected break through later; twelve infected Norse villages around it (small outposts near the base, big towns far
// out); a day-night cycle; each camp by a pass with a giant fallen tree and a stone deposit that are never used up;
// two plague lords' lairs.
//
// It writes the mission folder in the remake's map format (docs/MAP_FORMAT.md, docs/LUA_MAPS.md): map.lua (with the
// mission table and day_night), terrain/*.png, objects.lua (the landscape: trees, stones, rocks, wild animals, start
// locations), regions.lua, minimap.png, world.lua (the infected buildings with GUIDs, the rockslide's rocks, regions,
// quests, dialogues, variables), triggers.lua (the few triggers of the
// mission; the logic is custom.lua's), texts/uk.lua, texts/de.lua and layout.lua (the layout as Lua data for custom.lua
// and the tests: entrances, camps, villages). custom.lua and coop.lua are written by hand and never touched
// (PwMapWriter keeps them).
//
// Reproducible: everything comes from the seed (SimRandom, Noise) - the same seed gives the same bytes. The map opens in
// the map editor like any other (My maps can hold a copy).
//
//   dotnet run --project tools/outbreak -c Release [-- <out folder>] [--seed 1966]
//   (default out: game/missions/outbreak of the repository)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Editor.Triggers;
using PwRemake.Sim.Maps;

namespace PwRemake.Tools.Outbreak;

public static class Program
{
    public static int Main(string[] args)
    {
        string? outDir = null; ulong seed = OutbreakMap.DefaultSeed;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seed" && i + 1 < args.Length) seed = ulong.Parse(args[++i], CultureInfo.InvariantCulture);
            else outDir = args[i];
        }
        outDir ??= Path.Combine(RepoRoot(), "game", "missions", "outbreak");
        var gen = new OutbreakMap(seed);
        var pw = gen.Build();
        PwMapWriter.WriteFolder(pw, outDir);
        Console.WriteLine($"Outbreak written to {outDir}: {pw.Objects.Count} map objects, {gen.Villages.Sum(v => v.Buildings.Count)} infected buildings in {gen.Villages.Count} villages");
        foreach (var w in gen.Notes) Console.WriteLine("  " + w);
        return 0;
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "game", "coop")) && Directory.Exists(Path.Combine(d.FullName, "src", "Sim"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }
}

/// <summary>one infected building of a village</summary>
public sealed class VBuilding { public string Cls = "", Name = ""; public double X, Y, Rot, R; public int Stage = 3; }
public sealed class Village
{
    public int No;
    public string Name = "";
    public double X, Y, R;
    /// <summary>the entrance (0 .. 2) its night waves take</summary>
    public int Entrance;
    public int Stage = 3;
    /// <summary>"outpost", "village" or "town"</summary>
    public string Size = "village";
    /// <summary>the entrance its waves take while the south-west one is still blocked (1-based in layout.lua)</summary>
    public int Alt;
    /// <summary>the height of its levelled ground</summary>
    public double Ground;
    public List<VBuilding> Buildings = new();
}

/// <summary>The map and the mission (map coordinates: metres from the north-west corner, x east, y south).</summary>
public sealed class OutbreakMap
{
    public const ulong DefaultSeed = 1966;
    // (768 m - the brief's fallback from 1024 m: the 1024 m valley's trees and their foliage sprites took the view to
    // 6 GB and more; 768 m keeps the base at StarCraft II's scale - about 240 m across - with room for twelve villages)
    public const int Size = 768;
    public const double Cx = 384, Cy = 384;
    /// <summary>the valley's mean height - the base lies at it (round 4, Kacper: "the base at the same level as the map,
    /// like in StarCraft", not on a plateau)</summary>
    public const double Low = 31;
    /// <summary>the passes' half width: the ridge opens this far either side of the pass's line (it stands again 7 m
    /// further); the south-west pass - the rockslide's - is narrower. Round 6 (Kacper: "make the entrances 25% wider so
    /// you can place turrets a bit more easily"): 25 m and 20 m across (were 20 and 16)</summary>
    public const double PassHalf = 12.5, BarrierHalf = 10;
    /// <summary>the three entrances: map angles (degrees, x east, y south: 90 = south) - north and east open (as
    /// StarCraft II's), the south-west one closed by the rockslide (Barrier)</summary>
    public static readonly double[] EntranceDeg = { 270, 0, 135 };
    public static readonly string[] CampNames = { "A", "B", "C" };
    /// <summary>the entrance the rockslide closes</summary>
    public const int BarrierEntrance = 2;
    public const double CampR = 60;

    public readonly List<Village> Villages = new();
    public readonly List<string> Notes = new();
    readonly SimRandom _rng;
    readonly ulong _seed;
    float[] _h = Array.Empty<float>();
    int _n;

    public OutbreakMap(ulong seed) { _seed = seed; _rng = new SimRandom(seed); }

    static double Rad(double deg) => deg * Math.PI / 180;
    public static (double X, double Y) Dir(int k) => (Math.Cos(Rad(EntranceDeg[k])), Math.Sin(Rad(EntranceDeg[k])));
    public static (double X, double Y) Camp(int k) { var (dx, dy) = Dir(k); return (Cx + dx * CampR, Cy + dy * CampR); }
    /// <summary>the outer end of pass k (in the valley) and its inner end (in the base): the ends of its line</summary>
    public (double X, double Y) EntranceOut(int k) => Passes[k].Outer;
    public (double X, double Y) EntranceIn(int k) => Passes[k].Inner;
    /// <summary>a camp's giant fallen tree and its stone deposit: in the base beside the pass</summary>
    public static (double X, double Y, double Rot) TreeAt(int k) => Beside(k, 84, 30);
    public static (double X, double Y, double Rot) StoneAt(int k) => Beside(k, 82, -30);
    static (double X, double Y, double Rot) Beside(int k, double r, double side)
    {
        var (dx, dy) = Dir(k);
        double px = -dy, py = dx;                      // (perpendicular)
        return (Cx + dx * r + px * side, Cy + dy * r + py * side, Rad(EntranceDeg[k]) + Math.PI / 2);
    }

    // ------------------------------------------------------------------ the ring's outline (organic, not a circle)
    // The base's edge (the inner foot of the ring of ridges), the ridges' width and height go round the centre as sums of
    // a few waves with the seed's phases: bays and spurs; near the three entrances (and so the camps with their tree and
    // quarry) the base keeps room. A little noise on the distance breaks the edge further (rocky, not drawn with a
    // compass).
    double[] _ph = Array.Empty<double>();
    (double A, double W)[] _spurs = Array.Empty<(double, double)>();

    void Shape()
    {
        _ph = Enumerable.Range(0, 12).Select(_ => _rng.Next() * Math.PI * 2).ToArray();
        // two spurs and a bay, away from the entrances
        var free = new List<double>();
        for (double a = 0; a < 360; a += 5) if (Enumerable.Range(0, 3).All(k => AngDist(a, EntranceDeg[k]) > 40)) free.Add(a);
        var sp = new List<(double, double)>();
        while (sp.Count < 3)
        {
            double a = free[_rng.Int(free.Count)];
            if (sp.Any(s => AngDist(s.Item1, a) < 50)) continue;
            sp.Add((a, sp.Count < 2 ? 22 : -15));
        }
        _spurs = sp.ToArray();
    }

    static double Ang(double x, double y) { double a = Math.Atan2(y - Cy, x - Cx) * 180 / Math.PI; return a < 0 ? a + 360 : a; }

    /// <summary>the base's radius (the ring's inner foot) in a direction (degrees)</summary>
    public double RimR(double deg)
    {
        double t = Rad(deg);
        double r = 118 + 11 * Math.Sin(2 * t + _ph[0]) + 8 * Math.Sin(3 * t + _ph[1]) + 5 * Math.Sin(5 * t + _ph[2]) + 2.8 * Math.Sin(9 * t + _ph[3]) + 1.6 * Math.Sin(14 * t + _ph[7]);
        foreach (var (a, w) in _spurs) { double d = AngDist(deg, a); if (d < 20) r += w * (0.5 + 0.5 * Math.Cos(Math.PI * d / 20)); }
        // room at the entrances: the camps, their tree and quarry stand within 30 degrees of them
        double e = Enumerable.Range(0, 3).Min(k => AngDist(deg, EntranceDeg[k]));
        double k2 = e < 30 ? 1 : e < 42 ? 1 - (e - 30) / 12 : 0;
        if (r < 118 && k2 > 0) r += (118 - r) * k2;
        return Math.Max(r, 100);
    }
    /// <summary>the ridge's width (foot to foot) and its height over the ground (4.5 - 11 m; rocky crests rise higher)</summary>
    double RidgeWidth(double deg) => 13 + 7 * (0.5 + 0.5 * Math.Sin(3 * Rad(deg) + _ph[4]));
    double RidgeH(double deg) => 8 + 2.2 * Math.Sin(2 * Rad(deg) + _ph[5]) + 1.3 * Math.Sin(5 * Rad(deg) + _ph[6]);

    // ------------------------------------------------------------------ the passes: gaps in the ring
    public sealed class Pass
    {
        public List<(double X, double Y)> Pts = new();
        public double[] Len = Array.Empty<double>();
        public double Half;
        public (double X, double Y) Inner => Pts[0];
        public (double X, double Y) Outer => Pts[^1];
    }
    public readonly List<Pass> Passes = new();

    static (double X, double Y) Polar(double deg, double r) => (Cx + Math.Cos(Rad(deg)) * r, Cy + Math.Sin(Rad(deg)) * r);

    void MakePasses()
    {
        for (int k = 0; k < 3; k++)
        {
            double a = EntranceDeg[k];
            double bend = (k % 2 == 0 ? 1 : -1) * (5 + _rng.Next() * 4);          // degrees the pass swings aside
            double R0 = RimR(a), W0 = RidgeWidth(a);
            // a quadratic curve: from inside the base out through the ring, bending a little (a pass between rocks, not
            // a cut drawn with a ruler)
            var p0 = Polar(a, R0 - 14);
            var p1 = Polar(a + bend * 0.3, R0 + W0 * 0.5);
            var p2 = Polar(a + bend, R0 + W0 + 30);
            var r = new Pass { Half = k == BarrierEntrance ? BarrierHalf : PassHalf };
            for (int i = 0; i <= 32; i++)
            {
                double t = i / 32.0, u = 1 - t;
                r.Pts.Add((u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X, u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y));
            }
            r.Len = new double[r.Pts.Count];
            for (int i = 1; i < r.Pts.Count; i++) r.Len[i] = r.Len[i - 1] + Dist(r.Pts[i], r.Pts[i - 1]);
            Passes.Add(r);
        }
    }

    /// <summary>how much of the ridge stands at (x, y): 0 in a pass, 1 elsewhere (round 5: no wall stretches any more -
    /// the ring is rock all round but for its three passes)</summary>
    double RidgeOpen(double x, double y, double deg)
    {
        double f = 1;
        foreach (var p in Passes) { double d = Near(p.Pts, p.Len, x, y).D; if (d < p.Half + 7) f = Math.Min(f, Smooth(p.Half, p.Half + 7, d)); }
        return f;
    }

    static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>distance from a point to a polyline, and how far along it (0 .. 1) the nearest point is</summary>
    static (double D, double T) Near(List<(double X, double Y)> pts, double[] len, double x, double y)
    {
        double best = 1e9, bt = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var (ax, ay) = pts[i - 1]; var (bx, by) = pts[i];
            double vx = bx - ax, vy = by - ay, l2 = vx * vx + vy * vy;
            double s = l2 > 0 ? Math.Clamp(((x - ax) * vx + (y - ay) * vy) / l2, 0, 1) : 0;
            double px = ax + vx * s - x, py = ay + vy * s - y, d = Math.Sqrt(px * px + py * py);
            if (d < best) { best = d; bt = (len[i - 1] + (len[i] - len[i - 1]) * s) / len[^1]; }
        }
        return (best, bt);
    }

    // ------------------------------------------------------------------ paths (worn dirt): pass to camp, pass to villages
    public readonly List<(List<(double X, double Y)> Pts, double[] Len, double W)> Paths = new();

    void MakePaths()
    {
        void Add(List<(double X, double Y)> ctrl, double w)
        {
            // a quadratic curve through the control points' middle, with a wobble
            var pts = new List<(double X, double Y)>();
            int n = 40;
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n, u = 1 - t;
                var (a, b, c) = (ctrl[0], ctrl[1], ctrl[2]);
                double x = u * u * a.X + 2 * u * t * b.X + t * t * c.X, y = u * u * a.Y + 2 * u * t * b.Y + t * t * c.Y;
                double wob = Math.Sin(t * Math.PI) * 3 * (Noise.Fbm(x * 0.05, y * 0.05, 31, 2) - 0.5) * 2;
                pts.Add((x + wob, y - wob));
            }
            var len = new double[pts.Count];
            for (int i = 1; i < pts.Count; i++) len[i] = len[i - 1] + Dist(pts[i], pts[i - 1]);
            Paths.Add((pts, len, w));
        }
        for (int k = 0; k < 3; k++)
        {
            var top = Passes[k].Inner; var camp = Camp(k);
            Add(new() { top, ((top.X + camp.X) / 2 + 4, (top.Y + camp.Y) / 2 - 4), camp }, 3.5);
        }
        foreach (var v in Villages)
        {
            var foot = Passes[v.Entrance].Outer;
            double mx = (foot.X + v.X) / 2 + (_rng.Next() - 0.5) * 50, my = (foot.Y + v.Y) / 2 + (_rng.Next() - 0.5) * 50;
            Add(new() { foot, (mx, my), (v.X, v.Y) }, 3);
        }
    }

    double PathDist(double x, double y) => Paths.Count == 0 ? 1e9 : Paths.Min(p => Near(p.Pts, p.Len, x, y).D - p.W);

    // ------------------------------------------------------------------ the lake (frozen pond in the valley)
    public (double X, double Y, double R) Lake;

    // ------------------------------------------------------------------ heights
    double Lowland(double x, double y)
    {
        double s = _seed % 1000;
        // gentle hills, a finer swell, small bumps
        double h = Low + (Noise.Fbm(x * 0.0075, y * 0.0075, s + 1, 4) - 0.5) * 18 + (Noise.Fbm(x * 0.025, y * 0.025, s + 5, 3) - 0.5) * 5
                   + (Noise.Fbm(x * 0.08, y * 0.08, s + 7, 2) - 0.5) * 1.2;
        // the base and the valley round it lie at the valley's mean height (a gentle swell is left: the base is not a
        // table) - the ring of ridges stands on it (Height)
        double dx = x - Cx, dy = y - Cy, r = Math.Sqrt(dx * dx + dy * dy);
        double near = (1 - Smooth(150, 235, r)) * 0.85;
        h = h * (1 - near) + Low * near;
        // the valley's rim: mountains rising towards the map's edge (beyond the playable area)
        double db = Math.Min(Math.Min(x, y), Math.Min(Size - x, Size - y));
        if (db < 80) h += Math.Pow((80 - db) / 80, 1.7) * 46 * (0.6 + 0.8 * Noise.Fbm(x * 0.02, y * 0.02, s + 3, 3));
        return h;
    }

    static double Smooth(double a, double b, double x) { double t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>the ring's ridge over the ground at (x, y): steep sides (small cliffs, above 50 degrees), a rocky crest;
    /// none in the passes</summary>
    double Ridge(double x, double y)
    {
        double dx = x - Cx, dy = y - Cy, r = Math.Sqrt(dx * dx + dy * dy);
        double a = Ang(x, y);
        double R = RimR(a), W = RidgeWidth(a);
        // the foot broken by noise (rocky, irregular) - a few metres in and out
        double re = r + 4 * (Noise.Fbm(x * 0.05, y * 0.05, (_seed % 1000) + 17, 3) - 0.5) * 2;
        double u = (re - R) / W;
        if (u <= 0 || u >= 1) return 0;
        double prof = Smooth(0, 0.3, u) * (1 - Smooth(0.7, 1, u));
        if (prof <= 0) return 0;
        // rocky crests along the ridge (outcrops)
        double crest = Math.Max(0, Noise.Fbm(x * 0.06, y * 0.06, (_seed % 1000) + 23, 2) - 0.45) * 12;
        return prof * (RidgeH(a) + crest) * RidgeOpen(x, y, a);
    }

    double Height(double x, double y)
    {
        double h = Lowland(x, y) + Ridge(x, y);
        // the rockslide's scree: a low mound in the south-west pass (it stays, walkable, when the rocks are gone)
        double bd = Math.Sqrt((x - BarrierAt.X) * (x - BarrierAt.X) + (y - BarrierAt.Y) * (y - BarrierAt.Y));
        if (bd < 18) h += 1.6 * (1 - Smooth(0, 18, bd));
        // the villages stand on levelled ground (their buildings need it), in clearings
        foreach (var v in Villages)
        {
            double vr = Math.Sqrt((x - v.X) * (x - v.X) + (y - v.Y) * (y - v.Y));
            double k2 = 1 - Smooth(v.R, v.R + 16, vr);
            if (k2 > 0) h = h * (1 - k2) + v.Ground * k2;
        }
        // the pond: a shallow basin under the water line
        double lr = Math.Sqrt((x - Lake.X) * (x - Lake.X) + (y - Lake.Y) * (y - Lake.Y));
        if (lr < Lake.R + 14) { double k3 = 1 - Smooth(Lake.R - 6, Lake.R + 14, lr); h = h * (1 - k3) + (WaterLevel - 2.5) * k3; }
        return h;
    }
    public const double WaterLevel = 16;

    // ------------------------------------------------------------------ the villages
    // Twelve infected villages as in StarCraft II's "Outbreak": small outposts near the base, villages further out, big
    // towns at the valley's far end. Every Norse building class that World.PlaceBuilding can put down ready-made is used
    // (Kacper: "also add other buildings", round 3: "use every placeable Norse building class"): the temple, the arena,
    // the fireplace (a town's hearth), the warpgate, the tavern, the warehouse, the smithy, the workshop, the
    // lumberjack, the stonemason, the farm, the fields, the kennel, the cauldron, houses, small and large towers and
    // bunkers. Not used: walls and gates (lines the wall builder draws, not single buildings), the harbour (it needs open
    // water); the big farm, the marketplace and the medium tower are upgrades (no level 1 of their own).
    // (the reach of each building's pathfinder boxes from its centre - measured on the models -, the fields' their look)
    static readonly Dictionary<string, double> Radius = new()
    {
        ["hu_arena"] = 13, ["hu_temple"] = 13, ["hu_fireplace"] = 13.5, ["hu_warpgate"] = 18, ["hu_tavern"] = 10, ["hu_warehouse"] = 13.5,
        ["hu_machine_maker"] = 13, ["hu_weapons_smith"] = 8, ["hu_stone_cottage"] = 6.5, ["hu_lumberjack_cottage"] = 8, ["hu_stone_quarry"] = 7.5,
        ["hu_small_animal_farm"] = 17, ["hu_corn_field"] = 12, ["hu_kennel"] = 6, ["hu_magic_cauldron"] = 6, ["hu_large_tower"] = 6.5,
        ["hu_small_tower"] = 5, ["hu_bunker"] = 8,
    };
    const string Hs = "hu_stone_cottage";
    // (size, angle in degrees, distance from the centre, the stage at the start: 2 = spreading - it grows to 3 -, the
    // buildings: the first stands in the middle)
    static readonly (string Size, double Deg, double R, int Stage, string[] Kinds)[] Sites =
    {
        ("outpost", 315, 210, 2, new[] { "hu_small_tower", Hs, Hs, "hu_kennel" }),
        ("outpost", 60, 212, 3, new[] { "hu_bunker", Hs, "hu_lumberjack_cottage", "hu_corn_field", Hs }),
        ("outpost", 205, 210, 2, new[] { "hu_small_tower", Hs, "hu_stone_quarry", "hu_magic_cauldron" }),
        ("outpost", 100, 214, 3, new[] { "hu_bunker", Hs, "hu_kennel", Hs, "hu_small_tower" }),
        ("village", 250, 280, 3, new[] { "hu_tavern", Hs, Hs, Hs, "hu_small_animal_farm", "hu_lumberjack_cottage", "hu_small_tower", "hu_kennel" }),
        ("village", 20, 280, 2, new[] { "hu_weapons_smith", Hs, Hs, Hs, "hu_corn_field", "hu_corn_field", "hu_large_tower" }),
        ("village", 170, 278, 3, new[] { "hu_warehouse", Hs, Hs, "hu_stone_quarry", "hu_machine_maker", "hu_bunker", "hu_magic_cauldron" }),
        ("village", 80, 278, 3, new[] { "hu_tavern", Hs, Hs, Hs, "hu_lumberjack_cottage", "hu_small_animal_farm", "hu_small_tower", "hu_bunker" }),
        ("town", 45, 336, 3, new[] { "hu_temple", "hu_arena", Hs, Hs, Hs, Hs, "hu_warehouse", "hu_weapons_smith", "hu_large_tower", "hu_large_tower", "hu_kennel", "hu_corn_field" }),
        ("town", 135, 332, 3, new[] { "hu_fireplace", "hu_tavern", Hs, Hs, Hs, "hu_machine_maker", "hu_small_animal_farm", "hu_corn_field", "hu_large_tower", "hu_bunker", "hu_magic_cauldron" }),
        ("town", 225, 336, 3, new[] { "hu_arena", "hu_warpgate", Hs, Hs, Hs, "hu_temple", "hu_lumberjack_cottage", "hu_stone_quarry", "hu_large_tower", "hu_small_tower" }),
        ("town", 315, 332, 3, new[] { "hu_temple", "hu_warehouse", Hs, Hs, Hs, Hs, "hu_tavern", "hu_weapons_smith", "hu_small_animal_farm", "hu_large_tower", "hu_bunker" }),
    };
    /// <summary>the plague lords' lairs (the side quest): the villages (indices into Sites) and what each is</summary>
    public static readonly (int Site, string Name, string Kind)[] Lairs = { (10, "OB_Boss_Lord", "lord"), (8, "OB_Boss_Beast", "beast") };

    void PlaceVillages()
    {
        for (int i = 0; i < Sites.Length; i++)
        {
            var (size, deg, r, stage, kinds) = Sites[i];
            double a = Rad(deg + (_rng.Next() - 0.5) * 6);
            double R = size == "outpost" ? 24 : size == "village" ? 34 : 46;
            var v = new Village { No = i + 1, Name = $"Village_{i + 1}", Size = size, X = Cx + Math.Cos(a) * r, Y = Cy + Math.Sin(a) * r, R = R, Stage = stage };
            // the waves take the pass whose outer end is nearest; while the rockslide stands, the nearest open one
            v.Entrance = Enumerable.Range(0, 3).OrderBy(k => Dist(Passes[k].Outer, (v.X, v.Y))).First();
            v.Alt = v.Entrance != BarrierEntrance ? v.Entrance : Enumerable.Range(0, 3).Where(k => k != BarrierEntrance).OrderBy(k => Dist(Passes[k].Outer, (v.X, v.Y))).First();
            // the main building in the middle, the others round it where they fit (biggest first; a lane of 5 m
            // between them); a village that is too tight grows
            var placed = new List<VBuilding>();
            var order = kinds.Select((k, n) => (k, n)).OrderBy(x => x.n == 0 ? 0 : 1).ThenByDescending(x => Radius[x.k]).ToList();
            for (int tries = 0; ; tries++)
            {
                placed.Clear();
                bool ok = true;
                foreach (var (k, n) in order)
                {
                    double br = Radius[k];
                    double bx = v.X, by = v.Y;
                    if (n != 0)
                    {
                        bool found = false;
                        for (int t = 0; t < 400 && !found; t++)
                        {
                            double ba = _rng.Next() * Math.PI * 2, rr = Radius[kinds[0]] + br + 5 + _rng.Next() * Math.Max(1, v.R - br - Radius[kinds[0]] - 5);
                            bx = v.X + Math.Cos(ba) * rr; by = v.Y + Math.Sin(ba) * rr;
                            found = Math.Sqrt((bx - v.X) * (bx - v.X) + (by - v.Y) * (by - v.Y)) + br <= v.R + 2 && placed.All(o => Math.Sqrt((o.X - bx) * (o.X - bx) + (o.Y - by) * (o.Y - by)) >= o.R + br + 5);
                        }
                        if (!found) { ok = false; break; }
                    }
                    // (turned towards the middle, as before; the main building by the village's side)
                    double rot = n == 0 ? Rad(deg) : Math.Atan2(v.Y - by, v.X - bx);
                    placed.Add(new VBuilding { Cls = k, R = br, Name = $"V{v.No}_{k}_{n + 1}", X = Math.Round(bx, 2), Y = Math.Round(by, 2), Rot = Math.Round(Wrap(rot), 4), Stage = stage });
                }
                if (ok) break;
                v.R += 3;
            }
            v.Buildings.AddRange(placed.OrderBy(b => int.Parse(b.Name.Split('_')[^1], CultureInfo.InvariantCulture)));
            Villages.Add(v);
        }
        // the levelled ground: the lowland's height at the village (computed before the villages flatten anything)
        foreach (var v in Villages) v.Ground = Lowland(v.X, v.Y);
        Notes.Add(string.Join(", ", Villages.Select(v => $"{v.Name} ({v.Size}, r {v.R:0}): {v.Buildings.Count}")));
    }

    // ------------------------------------------------------------------ the rockslide in the south-west pass
    // A rockslide come down from one of the pass's ridge ends (round 4: "a natural rockslide blocking a pass, not a
    // ring"): ice rocks heaped across the gap between the two ridges - the big ones by the side they fell from, smaller
    // ones spread to the other, a tongue of them spilled out of the gap - on a low scree mound (Height). Their pathfinder
    // boxes close the pass until the mission removes them (custom.lua: night 2, or sooner once enough villages burn);
    // the scree and the stones that rolled furthest (plants) stay.
    public readonly List<(string Name, string Model, double X, double Y, double Rot)> Barrier = new();
    public (double X, double Y) BarrierAt;
    /// <summary>the rockslide's frame: along the pass (outwards), across it, the side it came from (+1 / -1)</summary>
    (double Tx, double Ty, double Px, double Py, int Side) _slide;

    void MakeBarrier()
    {
        var rp = Passes[BarrierEntrance];
        double ea = EntranceDeg[BarrierEntrance], want = RimR(ea) + RidgeWidth(ea) * 0.5;
        // where the pass crosses the ridge's middle
        int i = 1; while (i < rp.Pts.Count - 1 && Dist(rp.Pts[i], (Cx, Cy)) < want) i++;
        var (ax, ay) = rp.Pts[i - 1]; var (bx, by) = rp.Pts[i];
        double tl = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)), tx = (bx - ax) / tl, ty = (by - ay) / tl, px = -ty, py = tx;
        double cx = (ax + bx) / 2, cy = (ay + by) / 2;
        BarrierAt = (cx, cy);
        int side = _rng.Next() < 0.5 ? 1 : -1;
        _slide = (tx, ty, px, py, side);
        string[] big = { "ice_snow_middlerock_01", "ice_snow_middlerock_02", "ice_middlerock_01", "ice_middlerock_02" };
        string[] small = { "ice_middlerock_03", "ice_snow_middlerock_03" };
        double span = BarrierHalf + 9;                                     // (round 6: the wider pass - the heap grows with it)
        int n = 0;
        for (double o = -span; o <= span + 1e-9; o += 3.6)
            for (double a = -7.2; a <= 7.2 + 1e-9; a += 3.6)
            {
                double w = (o * side + span) / (2 * span);                 // 0 the far side .. 1 the side it fell from
                double ext = 4.2 + 3.8 * w + (_rng.Next() - 0.5) * 1.6, shift = 1.8 * w;
                if (Math.Abs(a - shift) > ext) continue;
                double along = a + (_rng.Next() - 0.5) * 1.8, across = o + (_rng.Next() - 0.5) * 1.6;
                string model = w > 0.25 || _rng.Next() < 0.6 ? big[_rng.Int(big.Length)] : small[_rng.Int(small.Length)];
                Barrier.Add(($"OB_Rockfall_{++n:00}", model, Math.Round(cx + tx * along + px * across, 2), Math.Round(cy + ty * along + py * across, 2), Math.Round(_rng.Next() * 6.28, 4)));
            }
        Notes.Add($"the base's edge {Enumerable.Range(0, 360).Min(a => RimR(a)):0} - {Enumerable.Range(0, 360).Max(a => RimR(a)):0} m from the centre");
        Notes.Add($"the rockslide: {Barrier.Count} rocks across pass {CampNames[BarrierEntrance]} at ({cx:0}, {cy:0}), fallen from the {(side > 0 ? "right" : "left")}");
    }

    static double AngDist(double a, double b) { double d = Math.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }
    static double Wrap(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

    // ------------------------------------------------------------------ the map
    public PwMap Build()
    {
        Shape();
        MakePasses();
        PlaceVillages();
        MakePaths();
        MakeBarrier();
        // the pond: in the valley between villages, away from the passes' approaches and the paths
        Lake = PickLake();
        var m = new PwMap
        {
            Name = "Outbreak", Title = "Outbreak", Author = "ParaWorld Remake",
            Description = "A plague spreads through the northern villages. Hold the base through the nights and burn every infected building by day.",
            Source = "Remake/missions/outbreak", W = Size, H = Size, Setting = "Icewaste", SettingName = "Icewaste", TerrainSetting = "Icewaste",
            Water = WaterLevel, StartTime = 7 * 3600, DefaultCamera = 0.785, MaxPlayers = 3,
            // (Normal's: a 6-minute day as StarCraft II's Normal, the first one 8 minutes; custom.lua shortens the days on
            // Hard to 3 minutes - Brutal's - and lengthens them on Easy)
            DayNight = new PwRemake.Sim.World.DayNightSpec { Day = 360, Dusk = 30, Night = 180, Dawn = 30, FirstDay = 480, NightSight = 0.65 },
        };
        m.Materials = PwMap.DefaultMaterials("Icewaste");
        m.Info["MapName"] = "Outbreak"; m.Info["Setting"] = "Icewaste"; m.Info["MaxPlayers"] = "3";
        m.Settings = Settings();
        // ---- heights (2 m grid) and the materials (4 m)
        _n = Size / 2;
        m.Hx = m.Hy = _n;
        _h = new float[_n * _n];
        for (int j = 0; j < _n; j++) for (int i = 0; i < _n; i++) _h[j * _n + i] = (float)(Math.Round(Height(i * 2, j * 2) * 128) / 128);
        m.Heights = _h;
        m.Mx = m.My = Size / 4;
        m.Mats = new byte[m.Mx * m.My];
        for (int j = 0; j < m.My; j++) for (int i = 0; i < m.Mx; i++) m.Mats[j * m.Mx + i] = Material(i * 4 + 2, j * 4 + 2);
        // ---- the walk grid: the slope rule (30 degrees), the pond and the ridges (rock all across: their crests are no
        // place to stand) written down, so the ring and its passes are exact
        var wg = new WalkGrid { W = _n, H = _n, Cells = new byte[_n * _n] };
        for (int j = 0; j < _n; j++) for (int i = 0; i < _n; i++)
            wg.Cells[j * _n + i] = (byte)(Slope(i * 2 + 1, j * 2 + 1) < 30 && HAt(i * 2 + 1, j * 2 + 1) > WaterLevel + 0.4 && Ridge(i * 2 + 1, j * 2 + 1) < 1.2 ? 1 : 0);
        m.Walk = wg;
        // ---- forest, objects, plants, regions
        m.Forest = Forest();
        Objects(m);
        Plants(m);
        Regions(m);
        m.Preview = Minimap(m);
        // ---- the mission
        m.Mission = Mission();
        m.World = World(m);
        m.Triggers = Triggers();
        m.Texts["uk"] = Texts(false);
        m.Texts["de"] = Texts(true);
        m.Files["layout.lua"] = Encoding.UTF8.GetBytes(LayoutLua());
        return m;
    }

    (double X, double Y, double R) PickLake()
    {
        // the open spot of the valley (between 200 and 300 m from the centre) furthest from the villages, the passes'
        // outer ends and the paths
        double best = -1; (double X, double Y) at = Polar(150, 250);
        for (double a = 0; a < 360; a += 3)
            for (double r = 200; r <= 300; r += 10)
            {
                var p = Polar(a, r);
                double d = Math.Min(Villages.Min(v => Dist((v.X, v.Y), p) - v.R), Passes.Min(rp => Dist(rp.Outer, p)));
                d = Math.Min(d, PathDist(p.X, p.Y) + 10);
                if (d > best) { best = d; at = p; }
            }
        return (at.X, at.Y, 22);
    }

    double HAt(double x, double y)
    {
        double fx = Math.Clamp(x / 2, 0, _n - 1.001), fy = Math.Clamp(y / 2, 0, _n - 1.001);
        int i = (int)fx, j = (int)fy; double u = fx - i, v = fy - j;
        return (_h[j * _n + i] * (1 - u) + _h[j * _n + i + 1] * u) * (1 - v) + (_h[(j + 1) * _n + i] * (1 - u) + _h[(j + 1) * _n + i + 1] * u) * v;
    }
    double Slope(double x, double y)
    {
        double gx = (HAt(x + 1, y) - HAt(x - 1, y)) / 2, gy = (HAt(x, y + 1) - HAt(x, y - 1)) / 2;
        return Math.Atan(Math.Sqrt(gx * gx + gy * gy)) * 180 / Math.PI;
    }

    // ------------------------------------------------------------------ the ground
    // Icewaste's materials as the original's snowy maps use them (multi_4_ice_601 measured: snow 0 and the smooth snow 6
    // on the gentle ground, snowy grass 2 on the flats, coarse snow 3 on the slopes, rock 4 and icy rock 7 on the
    // cliffs - mean slopes 7, 12, 4, 10, 40, 45 degrees; pebbles 1, gravel 5 for trodden ground): 0 snow, 1 pebbles,
    // 2 snowy grass, 3 coarse snow, 4 rock, 5 gravel / dirt, 6 smooth snow, 7 icy rock
    byte Material(double x, double y)
    {
        double s = Slope(x, y), h = HAt(x, y);
        double n = Noise.Fbm(x * 0.035, y * 0.035, (_seed % 1000) + 21, 3), n2 = Noise.Fbm(x * 0.012, y * 0.012, (_seed % 1000) + 29, 3);
        if (h < WaterLevel + 0.6) return 1;                                    // the pond's bed and shore
        if (s > 36) return h > Low + 7 || n > 0.55 ? (byte)7 : (byte)4;        // the ridges' cliffs: rock, icy high up
        if (s > 26) return n > 0.5 ? (byte)4 : (byte)3;
        // worn paths: dirt with pebbles at the edges
        double pd = PathDist(x, y);
        if (pd < 0) return n > 0.62 ? (byte)1 : (byte)5;
        foreach (var rp in Passes) if (Near(rp.Pts, rp.Len, x, y).D < rp.Half - 2) return n > 0.5 ? (byte)5 : (byte)1;
        // the camps: dark trodden earth round the headquarters, pebbles at the quarry
        for (int k = 0; k < 3; k++)
        {
            var c = Camp(k); double dc = Math.Sqrt((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y)) + (n - 0.5) * 10;
            if (dc < 9) return 5;
            if (dc < 13) return n > 0.55 ? (byte)5 : (byte)2;
            var q = StoneAt(k); if (Math.Sqrt((x - q.X) * (x - q.X) + (y - q.Y) * (y - q.Y)) + (n - 0.5) * 6 < 7) return 1;
        }
        // the villages' clearings: trodden earth round the buildings and along lanes between them, the rest snow trampled
        // in patches - a ragged edge, not a disc
        foreach (var v in Villages)
        {
            double dv = Math.Sqrt((x - v.X) * (x - v.X) + (y - v.Y) * (y - v.Y)) + (n - 0.5) * 26;
            if (dv > v.R + 8) continue;
            double nb = v.Buildings.Min(b => Math.Sqrt((x - b.X) * (x - b.X) + (y - b.Y) * (y - b.Y)) - b.R);
            double nv = Noise.Fbm(x * 0.09, y * 0.09, (_seed % 1000) + 71, 2);
            if (nb < 3 + nv * 3) return nv > 0.62 ? (byte)1 : (byte)5;
            if (dv < v.R - 2) return nv > 0.6 ? (byte)5 : nv > 0.45 ? (byte)3 : nv > 0.32 ? (byte)0 : (byte)1;
            return nv > 0.55 ? (byte)3 : (byte)6;
        }
        // fine-grained mixing (the original's painters dab small patches, not blobs): a high-frequency noise picks
        // coarse snow on rough spots and smooth drifts in hollows; snowy grass shows in small patches on the flats
        double nh = Noise.Fbm(x * 0.07, y * 0.07, (_seed % 1000) + 61, 2);
        if (s > 16) return nh > 0.5 ? (byte)3 : (byte)6;
        if (nh > 0.68) return 3;
        if (n2 > 0.57 && nh > 0.42 && s < 8) return 2;
        if (nh < 0.3) return 6;
        return 0;
    }

    // ------------------------------------------------------------------ the forest (the original's forest blocks)
    // 32 m squares of Icewaste's forest (forest.json: its five pines and firs, and undergrowth of shrubs, junipers and
    // blueberries) where the valley is not a clearing, a path, the pond or the passes' approach; denser in the noise's
    // woods and all round the map's rim (the edge fades into forest and mountains)
    ForestMap Forest()
    {
        var fm = new ForestMap { W = Size / 32, H = Size / 32 };
        int blocks = 0;
        for (int by = 0; by < fm.H; by++)
            for (int bx = 0; bx < fm.W; bx++)
            {
                double x = bx * 32 + 16, y = by * 32 + 16;
                double r = Math.Sqrt((x - Cx) * (x - Cx) + (y - Cy) * (y - Cy));
                double db = Math.Min(Math.Min(x, y), Math.Min(Size - x, Size - y));
                if (r < RimR(Ang(x, y)) + RidgeWidth(Ang(x, y)) + 26) continue;                // (the base, its ridges and their foot)
                if (Villages.Any(v => Math.Sqrt((x - v.X) * (x - v.X) + (y - v.Y) * (y - v.Y)) < v.R + 26)) continue;
                if (PathDist(x, y) < 20) continue;
                if (Passes.Any(rp => Dist(rp.Outer, (x, y)) < 56)) continue;
                if (Math.Sqrt((x - Lake.X) * (x - Lake.X) + (y - Lake.Y) * (y - Lake.Y)) < Lake.R + 26) continue;
                double wood = Noise.Fbm(x * 0.011, y * 0.011, (_seed % 1000) + 41, 3);
                // (the rim's band under the border mountains thinner than before: the trees - their foliage sprites - weigh
                // on the view's memory)
                bool rim = db < 56;
                if (!rim && wood < 0.45) continue;
                double dens = rim ? 0.8 : Math.Clamp(0.45 + (wood - 0.45) * 3, 0.45, 0.9);
                var b = new byte[31];
                for (int i = 0; i < 15; i++) b[i] = _rng.Next() < dens ? (byte)(0x40 | (18 + _rng.Int(12))) : (byte)0;
                for (int i = 15; i < 31; i++) b[i] = _rng.Next() < dens * 0.8 ? (byte)(0x40 | (1 + _rng.Int(30))) : (byte)0;
                fm.Blocks.Add(new ForestBlock { X = bx, Y = by, B = b });
                blocks++;
            }
        Notes.Add($"{blocks} forest blocks");
        return fm;
    }

    // ------------------------------------------------------------------ objects of the landscape (objects.lua)
    static readonly string[] Trees = { "icewaste_tree_01", "icewaste_tree_02", "icewaste_tree_03", "icewaste_tree_04", "icewaste_tree_05" };
    static readonly string[] Leafy = { "icewaste_birch_01", "icewaste_birch_02", "icewaste_birch_03", "icewaste_oak_02", "icewaste_oak_03" };

    int _objNo;
    MapObject Obj(string type, string cls, double x, double y, double rot, int? owner = null, Dictionary<string, string>? attr = null)
    {
        double q = rot / -2;                               // (heading = -2 atan2(qz, qw))
        return new MapObject
        {
            Type = type, Cls = cls, Name = $"{cls}_{++_objNo}", X = Math.Round(x, 3), Y = Math.Round(y, 3), Z = Math.Round(HAt(x, y), 3), Rot = rot,
            Q = new[] { 0, 0, Math.Round(Math.Sin(q), 5), Math.Round(Math.Cos(q), 5) }, Owner = owner, Attr = attr ?? new Dictionary<string, string>(),
        };
    }

    /// <summary>room for a thing: not on the ridges, the passes, the paths, the camps (build space), the villages, the pond</summary>
    bool Free(double x, double y, double clear, bool inBase = false)
    {
        if (x < 40 || y < 40 || x > Size - 40 || y > Size - 40) return false;
        double r = Math.Sqrt((x - Cx) * (x - Cx) + (y - Cy) * (y - Cy)), a = Ang(x, y), R = RimR(a);
        if (inBase ? r > R - 5 : r < R + RidgeWidth(a) + 3) return false;
        if (Slope(x, y) > 24 || HAt(x, y) < WaterLevel + 1) return false;
        foreach (var rp in Passes) if (Near(rp.Pts, rp.Len, x, y).D < rp.Half + clear + 4 || Dist(rp.Outer, (x, y)) < 20 + clear) return false;
        if (PathDist(x, y) < clear + 2) return false;
        for (int k = 0; k < 3; k++)
        {
            var c = Camp(k);
            if ((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y) < 44 * 44) return false;             // (the camps: build space)
            foreach (var p in new[] { TreeAt(k), StoneAt(k) })
                if ((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y) < 16 * 16) return false;
        }
        foreach (var v in Villages) if ((x - v.X) * (x - v.X) + (y - v.Y) * (y - v.Y) < (v.R + clear + 4) * (v.R + clear + 4)) return false;
        if (Math.Sqrt((x - Lake.X) * (x - Lake.X) + (y - Lake.Y) * (y - Lake.Y)) < Lake.R + 4 + clear) return false;
        return true;
    }

    void Objects(PwMap m)
    {
        var O = m.Objects;
        // the start locations: one by each pass (seat 1: A; the co-op seats 2 and 3: B, C)
        for (int k = 0; k < 3; k++)
        {
            var (x, y) = Camp(k);
            var o = Obj("SLOC", "StartLocation", x, y, Rad(EntranceDeg[k]) - Math.PI / 2, k, new Dictionary<string, string> { ["include_building"] = "1" });
            o.Name = $"StartLocation_{k}";
            O.Add(o);
        }
        // each camp: a giant fallen tree with wood that never ends, a stone deposit that never ends (the remake's map
        // attributes infinite / fallen / scale: docs/MAP_FORMAT.md) - Icewaste's own oak and stone
        for (int k = 0; k < 3; k++)
        {
            var t = TreeAt(k);
            var tr = Obj("TREE", "icewaste_oak_01", t.X, t.Y, t.Rot, null, new Dictionary<string, string> { ["infinite"] = "1", ["fallen"] = "1", ["scale"] = "2.2", ["hitpoints"] = "30" });
            tr.Name = $"Camp{CampNames[k]}_GiantTree";
            O.Add(tr);
            var s = StoneAt(k);
            var st = Obj("STON", "resource_stone_ice", s.X, s.Y, s.Rot, null, new Dictionary<string, string> { ["infinite"] = "1", ["scale"] = "1.8", ["hitpoints"] = "2000" });
            st.Name = $"Camp{CampNames[k]}_Quarry";
            O.Add(st);
        }
        // tree groups along the base's edge (not at the camps) and at the forests' fringes in the valley
        int trees = 0;
        for (int g = 0; g < 44; g++)
        {
            double a = _rng.Next() * 360, rr = RimR(a) - 10 - _rng.Next() * 12;
            var (gx, gy) = Polar(a, rr);
            int n = 3 + _rng.Int(4);
            for (int i = 0; i < n; i++)
            {
                double x = gx + (_rng.Next() - 0.5) * 14, y = gy + (_rng.Next() - 0.5) * 14;
                if (!Free(x, y, 3, true)) continue;
                O.Add(Obj("TREE", _rng.Next() < 0.85 ? Trees[_rng.Int(Trees.Length)] : Leafy[_rng.Int(Leafy.Length)], x, y, _rng.Next() * 6.28)); trees++;
            }
        }
        // the valley's woods between the villages: firs on a jittered grid where the woods' noise says so (thick in the
        // middle, thinning at the edges), the clearings, paths and the passes' approaches kept open
        for (double gy = 40; gy < Size - 40; gy += 8)
            for (double gx = 40; gx < Size - 40; gx += 8)
            {
                double x = gx + (_rng.Next() - 0.5) * 6, y = gy + (_rng.Next() - 0.5) * 6;
                double wood = Noise.Fbm(x * 0.011, y * 0.011, (_seed % 1000) + 41, 3);
                if (wood < 0.47 || _rng.Next() > Math.Clamp((wood - 0.47) * 6, 0, 0.8)) continue;
                if (!Free(x, y, 6)) continue;
                if (Villages.Any(v => Math.Sqrt((x - v.X) * (x - v.X) + (y - v.Y) * (y - v.Y)) < v.R + 16)) continue;
                O.Add(Obj("TREE", _rng.Next() < 0.9 ? Trees[_rng.Int(Trees.Length)] : Leafy[_rng.Int(Leafy.Length)], x, y, _rng.Next() * 6.28)); trees++;
            }
        for (int g = 0; g < 60; g++)
        {
            double a = _rng.Next() * 360, rr = 170 + _rng.Next() * 170;
            var (gx, gy) = Polar(a, rr);
            int n = 2 + _rng.Int(5);
            for (int i = 0; i < n; i++)
            {
                double x = gx + (_rng.Next() - 0.5) * 18, y = gy + (_rng.Next() - 0.5) * 18;
                if (!Free(x, y, 5)) continue;
                O.Add(Obj("TREE", _rng.Next() < 0.85 ? Trees[_rng.Int(Trees.Length)] : Leafy[_rng.Int(Leafy.Length)], x, y, _rng.Next() * 6.28)); trees++;
            }
        }
        // stone deposits and berry bushes in the valley (normal ones: they run out)
        int stones = 0, bushes = 0;
        for (int i = 0; i < 120 && stones < 16; i++)
        {
            double a = _rng.Next() * 360, rr = 170 + _rng.Next() * 160;
            var (x, y) = Polar(a, rr);
            if (!Free(x, y, 8) || Slope(x, y) > 18) continue;
            O.Add(Obj("STON", "resource_stone_ice", x, y, _rng.Next() * 6.28, null, new Dictionary<string, string> { ["hitpoints"] = "1500" })); stones++;
        }
        for (int i = 0; i < 160 && bushes < 28; i++)
        {
            double a = _rng.Next() * 360, rr = 160 + _rng.Next() * 170;
            var (x, y) = Polar(a, rr);
            if (!Free(x, y, 4) || Slope(x, y) > 18) continue;
            O.Add(Obj("FRUI", "icewaste_fruit_bush", x, y, _rng.Next() * 6.28, null, new Dictionary<string, string> { ["hitpoints"] = "150" })); bushes++;
        }
        // wild animals to hunt (food) - healthy ones, nobody's (the rules' own spelling: the class names are matched exactly)
        string[] game = { "Megaloceros", "Megaloceros", "Wild_Boar", "Wild_Boar", "Wild_Boar", "Mammoth" };
        int animals = 0;
        for (int i = 0; i < 240 && animals < 26; i++)
        {
            double a = _rng.Next() * 360, rr = 160 + _rng.Next() * 170;
            var (x, y) = Polar(a, rr);
            if (!Free(x, y, 10) || Slope(x, y) > 18) continue;
            O.Add(Obj("ANML", game[animals % game.Length], x, y, _rng.Next() * 6.28)); animals++;
        }
        // the villages' graveyards (the plague's dead): a patch of tombs and graves by each village, on the side away from
        // the base (one patch, not a ring round the village - round 4: the "stone rings")
        string[] grim = { "ice_stonetomb_01", "ice_stonetomb_02", "ice_stonetomb_03", "northland_skeleton", "northland_tombstone_01", "northland_tombstone_02" };
        foreach (var v in Villages)
        {
            double ga = Math.Atan2(v.Y - Cy, v.X - Cx) + (_rng.Next() - 0.5) * 1.6, gr = v.R + 9;
            double gx = v.X + Math.Cos(ga) * gr, gy = v.Y + Math.Sin(ga) * gr;
            int count = v.Size == "town" ? 9 : v.Size == "village" ? 6 : 4;
            for (int i = 0; i < count; i++)
            {
                // (in rows, roughly: a graveyard)
                double row = (i % 3 - 1) * 3.2 + (_rng.Next() - 0.5), col = (i / 3 - 1) * 3.6 + (_rng.Next() - 0.5);
                double ca = ga + Math.PI / 2;
                O.Add(Obj("DCCO", grim[_rng.Int(grim.Length)], gx + Math.Cos(ca) * row + Math.Cos(ga) * col, gy + Math.Sin(ca) * row + Math.Sin(ga) * col, ga + (_rng.Next() - 0.5) * 0.3));
            }
        }
        // the map's rim: ice mountains standing in the border forest
        for (double a = 0; a < 360; a += 360.0 / 40)
        {
            double t = Rad(a);
            // (a point on the square's border band, 10 - 30 m in)
            double ex = Math.Cos(t), ey = Math.Sin(t), k2 = 1 / Math.Max(Math.Abs(ex), Math.Abs(ey));
            double reach = (Size / 2.0 - 12 - _rng.Next() * 16) * k2;
            double x = Cx + ex * reach, y = Cy + ey * reach;
            O.Add(Obj("DCCO", _rng.Next() < 0.5 ? "ice_big_mountain_0" + (1 + _rng.Int(3)) : "ice_middlerock_0" + (1 + _rng.Int(3)), x, y, _rng.Next() * 6.28));
        }
        Notes.Add($"{trees} trees, {stones} stone deposits, {bushes} berry bushes, {animals} wild animals; 3 giant fallen trees and 3 quarries (infinite)");
    }

    // ------------------------------------------------------------------ the passes' zones (round 6): low dressing only
    /// <summary>how far from a pass's line its zone reaches (inside and outside the ring, both sides), and the highest a
    /// dressing piece may stand there over the ground (about a unit's height)</summary>
    public const double PassZone = 35, LowMax = 4;
    /// <summary>the top of each dressing model over its origin (m; measured on the models: the meshes' highest point,
    /// GSF z with their nodes' offsets) - unknown models (plants) count as low</summary>
    public static readonly Dictionary<string, double> Tops = new()
    {
        ["ice_plateau_01"] = 8.9, ["ice_plateau_02"] = 8.9, ["ice_plateau_03"] = 8.9, ["ice_plateau_addon_01"] = 8.5, ["ice_plateau_addon_02"] = 8.5,
        ["ice_plateau_addon_03"] = 17.3, ["ice_plateau_addon_04"] = 16.0, ["ice_middlerock_01"] = 6.9, ["ice_middlerock_02"] = 7.9, ["ice_middlerock_03"] = 3.6,
        ["ice_snow_middlerock_01"] = 6.9, ["ice_snow_middlerock_02"] = 7.9, ["ice_snow_middlerock_03"] = 3.6, ["ice_smallrock_01"] = 5.2, ["ice_smallrock_02"] = 5.9,
        ["ice_smallrock_03"] = 4.5, ["ice_smallstone_01"] = 1.7, ["ice_smallstone_02"] = 3.9, ["ice_smallstone_03"] = 2.8,
    };
    static double ModelTop(string name) => Tops.TryGetValue(name, out var t) ? t : 0;
    // the low pieces: next to the passage (pathfinder boxes of 2.7 / 4.3 m), a little further, further up the ridge's end
    static readonly string[] LowNear = { "ice_smallstone_01", "ice_smallstone_02" };
    static readonly string[] LowMid = { "ice_smallstone_02", "ice_smallstone_03", "ice_smallstone_01" };
    static readonly string[] LowFar = { "ice_snow_middlerock_03", "ice_middlerock_03", "ice_smallrock_03", "ice_smallstone_03", "ice_snow_middlerock_03" };
    bool InPassZone(double x, double y) => Passes.Any(p => Near(p.Pts, p.Len, x, y).D < PassZone);

    // ------------------------------------------------------------------ plants (landscape decoration: plants.lua)
    // the ridges dressed with Icewaste's cliff pieces and boulders, scree at their feet (in drifts, not a line), shrubs,
    // junipers, ferns and blueberries along the base's edge and the forests' fringes, reeds round the pond, the
    // rockslide's scree - Icewaste's own
    void Plants(PwMap m)
    {
        var P = m.Plants;
        void Put0(string name, double x, double y, double z, double rot)
        {
            double q = rot / -2;
            P.Add(new MapPlant { Name = name, X = Math.Round(x, 3), Y = Math.Round(y, 3), Z = Math.Round(z, 3), Rot = rot, Q = new[] { 0, 0, Math.Round(Math.Sin(q), 5), Math.Round(Math.Cos(q), 5) } });
        }
        // round 6 (Kacper: "make the pillars near the entrances less tall, as they would make the incoming enemies less
        // visible"): within PassZone of a pass no piece stands more than LowMax over the ground - a taller one (a cliff
        // piece, a pillar, a big boulder) becomes a low heap of Icewaste's small rocks in its place (a cliff piece three
        // of them along its length), sunk so that its top stays low; next to the passage only the stones with small
        // pathfinder boxes (the passage stays as wide)
        void Put(string name, double x, double y, double z, double rot)
        {
            if (!InPassZone(x, y) || z + ModelTop(name) - HAt(x, y) <= LowMax) { Put0(name, x, y, z, rot); return; }
            int n = name.StartsWith("ice_plateau_0", StringComparison.Ordinal) ? 3 : 1;
            for (int i = 0; i < n; i++)
            {
                double o = (i - (n - 1) / 2.0) * 5.5 + (_rng.Next() - 0.5) * 1.5;
                double px = x + Math.Cos(rot) * o, py = y + Math.Sin(rot) * o;
                double e = Passes.Min(rp => Near(rp.Pts, rp.Len, px, py).D - rp.Half);
                if (e < 1) continue;                                                   // (never in the passage)
                string low = e < 5 ? LowNear[_rng.Int(LowNear.Length)] : e < 9 ? LowMid[_rng.Int(LowMid.Length)] : LowFar[_rng.Int(LowFar.Length)];
                double gz = HAt(px, py);
                Put0(low, px, py, gz - Math.Max(0, ModelTop(low) - (LowMax - 0.3)) - _rng.Next() * 0.4, _rng.Next() * 6.283);
            }
        }
        void Add(string name, double x, double y) => Put(name, x, y, HAt(x, y), _rng.Next() * 6.283);
        string[] rubble = { "ice_smallstone_01", "ice_smallstone_02", "ice_smallstone_03", "ice_smallrock_01", "ice_smallrock_02", "ice_smallrock_03" };
        string[] boulders = { "ice_snow_middlerock_01", "ice_snow_middlerock_02", "ice_snow_middlerock_03", "ice_middlerock_01", "ice_middlerock_02" };
        string[] green = { "icewaste_shrub_01_a", "icewaste_shrub_02_a", "icewaste_shrub_03_a", "icewaste_shrub_01_b", "icewaste_juniper_01", "icewaste_juniper_02", "icewaste_blueberry_01", "icewaste_blueberry_02", "icewaste_fern_01", "icewaste_fern_02", "icewaste_bush_01", "icewaste_bush_02" };
        bool OffPasses(double x, double y, double c) => Passes.All(rp => Near(rp.Pts, rp.Len, x, y).D > rp.Half + c) && PathDist(x, y) > c;
        // the ridges: Icewaste's cliff pieces (ice_plateau_0N, as the original's snowy maps dress their rocks) along both
        // faces, turned along the ring - the inner face towards the base, the outer one to the valley - sunk to the
        // ground's level so that their rock stands as high as the ridge; boulders on the crest here and there
        for (int face = 0; face < 2; face++)
            for (double a = _rng.Next() * 3; a < 360; )
            {
                double R = RimR(a), W = RidgeWidth(a), rr = R + W * (face == 0 ? 0.32 : 0.68);
                var (x, y) = Polar(a + (_rng.Next() - 0.5) * 1.5, rr + (_rng.Next() - 0.5) * 1.5);
                double step = 10.5 / rr * 180 / Math.PI;
                if (RidgeOpen(x, y, a) > 0.6 && OffPasses(x, y, 13))          // (a piece reaches 12 m along: the passes stay clear)
                {
                    double rot = Rad(a) + Math.PI / 2 + (face == 0 ? Math.PI : 0) + (_rng.Next() - 0.5) * 0.5;
                    Put("ice_plateau_0" + (1 + _rng.Int(3)), x, y, Lowland(x, y) - 2.5 - _rng.Next() * 1.5, rot);
                }
                a += step * (0.8 + _rng.Next() * 0.45);
            }
        // the ridges' ends at the passes (round 5: "the low bare grey mound looked unfinished"): where the ridge tapers
        // down to the passage, a heap of rock grown out of the ridge, not a bare mound - since round 6 low ones (Put
        // turns the add-ons' 16 - 17 m pillars into small rocks there: the passes stay in view)
        foreach (var pass in Passes)
        {
            var ends = new List<(double X, double Y)>();
            double ea = Ang(pass.Inner.X, pass.Inner.Y);
            for (double a = ea - 16; a <= ea + 16; a += 0.35)
            {
                var (x, y) = Polar(a, RimR(a) + RidgeWidth(a) * (0.15 + _rng.Next() * 0.7));
                double d = Near(pass.Pts, pass.Len, x, y).D;
                if (d < pass.Half + 1.5 || d > pass.Half + 17 || ends.Any(e => Dist(e, (x, y)) < 3.6)) continue;
                if (pass == Passes[BarrierEntrance] && Dist((x, y), BarrierAt) < 10) continue;      // (the rockslide's own heap)
                string model = d < pass.Half + 10 ? "ice_plateau_addon_0" + (3 + _rng.Int(2)) : _rng.Next() < 0.5 ? "ice_plateau_addon_01" : "ice_plateau_addon_0" + (3 + _rng.Int(2));
                Put(model, x, y, HAt(x, y) - 0.6 - _rng.Next() * 0.8, _rng.Next() * 6.283);
                ends.Add((x, y));
            }
        }
        for (double a = 0; a < 360; a += 3)
        {
            var (x, y) = Polar(a + _rng.Next() * 2, RimR(a) + RidgeWidth(a) * (0.35 + _rng.Next() * 0.3));
            if (RidgeOpen(x, y, a) < 0.8 || !OffPasses(x, y, 10)) continue;
            if (Noise.Fbm(x * 0.06, y * 0.06, (_seed % 1000) + 23, 2) < 0.52 && _rng.Next() > 0.25) continue;
            Add(boulders[_rng.Int(boulders.Length)], x, y);
        }
        // scree at the ridges' feet, inside and out: drifts of rubble where the noise says, single stones between
        for (int i = 0; i < 520; i++)
        {
            double a = _rng.Next() * 360;
            bool inner = _rng.Next() < 0.45;
            double R = RimR(a), W = RidgeWidth(a);
            var (x, y) = Polar(a, inner ? R - 1 - _rng.Next() * 5 : R + W + 1 + _rng.Next() * 7);
            if (!OffPasses(x, y, 7) || RidgeOpen(x, y, a) < 0.7) continue;
            if (Noise.Fbm(x * 0.04, y * 0.04, (_seed % 1000) + 81, 2) < 0.5 && _rng.Next() > 0.15) continue;
            double u = _rng.Next();
            Add(u < 0.05 ? boulders[_rng.Int(boulders.Length)] : u < 0.3 ? rubble[3 + _rng.Int(3)] : rubble[_rng.Int(3)], x, y);
        }
        // greenery along the base's edge and its tree groups
        for (int i = 0; i < 320; i++)
        {
            double a = _rng.Next() * 360;
            var (x, y) = Polar(a, RimR(a) - 4 - _rng.Next() * 18);
            if (!Free(x, y, 0, true)) continue;
            Add(green[_rng.Int(green.Length)], x, y);
        }
        // the valley: shrubs where the woods thin out, and along the paths' sides
        for (int i = 0; i < 1200; i++)
        {
            double x = 40 + _rng.Next() * (Size - 80), y = 40 + _rng.Next() * (Size - 80);
            double wood = Noise.Fbm(x * 0.011, y * 0.011, (_seed % 1000) + 41, 3);
            if (wood < 0.3 && _rng.Next() < 0.7) continue;
            if (!Free(x, y, 0)) continue;
            Add(green[_rng.Int(green.Length)], x, y);
        }
        // the rockslide's scree: stones that rolled furthest, along the pass's side it came from and out of it, and
        // boulders on the ridge's end it broke off (they stay when the rocks are gone; the passage itself stays clear for
        // the waves then - the stones' pathfinder boxes would close it)
        {
            var (tx, ty, px, py, side) = _slide;
            var bp = Passes[BarrierEntrance];
            for (int i = 0; i < 60; i++)
            {
                double along = (_rng.Next() - 0.3) * 24, across = side * (BarrierHalf * 0.5 + _rng.Next() * (BarrierHalf + 8));
                double x = BarrierAt.X + tx * along + px * across, y = BarrierAt.Y + ty * along + py * across;
                if (Near(bp.Pts, bp.Len, x, y).D < BarrierHalf + 2) continue;
                Add(_rng.Next() < 0.35 ? rubble[3 + _rng.Int(3)] : rubble[_rng.Int(3)], x, y);
            }
            for (int i = 0; i < 4; i++)
            {
                double across = side * (BarrierHalf + 8 + _rng.Next() * 6), along = (_rng.Next() - 0.5) * 6;
                Add(boulders[_rng.Int(boulders.Length)], BarrierAt.X + tx * along + px * across, BarrierAt.Y + ty * along + py * across);
            }
        }
        // reeds round the pond
        for (int i = 0; i < 36; i++)
        {
            double a = _rng.Next() * Math.PI * 2, rr = Lake.R + 1 + _rng.Next() * 6;
            double x = Lake.X + Math.Cos(a) * rr, y = Lake.Y + Math.Sin(a) * rr;
            Add(_rng.Next() < 0.3 ? "icewaste_reed_03_large" : _rng.Next() < 0.5 ? "icewaste_reed_01" : "icewaste_reed_02", x, y);
        }
        Notes.Add($"{P.Count} plants");
    }

    // ------------------------------------------------------------------ regions
    static RegionShape Circle(double x, double y, double r) => new() { Oval = true, X = Math.Round(x - r, 3), Y = Math.Round(y - r, 3), W = Math.Round(2 * r, 3), H = Math.Round(2 * r, 3) };
    void Regions(PwMap m)
    {
        m.Regions.Add(new MapRegion { Name = "Base", Shapes = { Circle(Cx, Cy, 116) } });
        for (int k = 0; k < 3; k++)
        {
            var (x, y) = Camp(k);
            m.Regions.Add(new MapRegion { Name = "Camp_" + CampNames[k], Shapes = { Circle(x, y, 32) } });
            var (fx, fy) = EntranceOut(k);
            m.Regions.Add(new MapRegion { Name = "Entrance_" + CampNames[k], Shapes = { Circle(fx, fy, 20) } });
        }
        foreach (var v in Villages) m.Regions.Add(new MapRegion { Name = v.Name, Shapes = { Circle(v.X, v.Y, v.R + 6) } });
        m.Regions.Add(new MapRegion { Name = "Pond", Shapes = { Circle(Lake.X, Lake.Y, Lake.R) } });
        m.Regions.Add(new MapRegion { Name = "Rockfall", Shapes = { Circle(BarrierAt.X, BarrierAt.Y, 18) } });
    }

    // ------------------------------------------------------------------ the minimap
    MapPreview Minimap(PwMap m)
    {
        const int S = 256;
        var px = new byte[S * S * 4];
        (byte, byte, byte)[] col = { (214, 224, 234), (150, 150, 160), (176, 192, 170), (198, 208, 218), (112, 112, 122), (140, 128, 112), (222, 232, 240), (150, 165, 180) };
        for (int j = 0; j < S; j++)
            for (int i = 0; i < S; i++)
            {
                double x = (i + 0.5) * Size / S, y = (j + 0.5) * Size / S;
                var c = col[m.Mats[(int)(y / 4) * m.Mx + (int)(x / 4)] & 7];
                double h = HAt(x, y);
                double shade = 0.7 + 0.3 * Math.Clamp((h - 10) / 50, 0, 1);
                bool water = h < WaterLevel;
                int o = (j * S + i) * 4;
                px[o] = water ? (byte)90 : (byte)(c.Item1 * shade); px[o + 1] = water ? (byte)120 : (byte)(c.Item2 * shade); px[o + 2] = water ? (byte)150 : (byte)(c.Item3 * shade); px[o + 3] = 255;
            }
        return new MapPreview { W = S, H = S, Rgba = px };
    }

    // ------------------------------------------------------------------ settings tree (the map list, the AI options)
    static InfoNode N(string name, string value = "", params InfoNode[] subs) { var n = new InfoNode { Name = name, Value = value }; n.Subs.AddRange(subs); return n; }
    static InfoNode Settings() => N("Root", "",
        N("Base", "", N("LevelName", "Outbreak"), N("Description", "Hold the base through the nights; burn the infected villages by day."), N("Author", "ParaWorld Remake"),
          N("MaxPlayers", "3"), N("MapType", "singleplayer"), N("GameType", "Deathmatch|"), N("StartTime", "7:0"), N("DefaultCamera", "0.785")),
        // light snow all the time (the weather program: Type 1 = snow)
        N("WeatherData", "", N("Name", "Outbreak snow"), N("Loop", "1"), N("Tracks", "",
            N("Track_0", "", N("Duration", "6"), N("Type", "1"), N("Intensity", "0.35"), N("WindDir", "2"), N("WindStrength", "0.3")),
            N("Track_1", "", N("Duration", "4"), N("Type", "1"), N("Intensity", "0.7"), N("WindDir", "3"), N("WindStrength", "0.6")),
            N("Track_2", "", N("Duration", "5"), N("Type", "0"), N("Intensity", "0"), N("WindDir", "0"), N("WindStrength", "0")))));

    // ------------------------------------------------------------------ the mission table (map.lua mission)
    public const int MissionId = 90;
    public const int InfectedSlot = 1;

    JsonObject Mission()
    {
        var players = new JsonArray();
        // (the infected in green - the plague's colour; pink stood out flat on their banners at night)
        for (int id = 0; id < 8; id++)
        {
            bool present = id == 0 || id == InfectedSlot;
            var dip = new JsonArray(Enumerable.Range(0, 8).Select(o => (JsonNode?)JsonValue.Create(o == id ? 2 : present && (o == 0 || o == InfectedSlot) ? 0 : 2)).ToArray());
            var p = new JsonObject
            {
                ["id"] = id, ["type"] = id == 0 ? "human" : "ai", ["control"] = id == 0 ? "human" : "scripted", ["present"] = present,
                ["tribe"] = present ? "Hu" : "", ["tribes"] = new JsonArray(present ? "Hu" : ""), ["name"] = id == 0 ? "Survivors" : id == InfectedSlot ? "The Infected" : $"Player {id + 1}",
                ["name_key"] = id == 0 ? "_OB_PLAYER" : id == InfectedSlot ? "_OB_INFECTED" : "", ["team"] = id == InfectedSlot ? 2 : 1, ["color"] = id == InfectedSlot ? 4 : id,
                ["resources"] = id == 0 ? new JsonObject { ["food"] = 300, ["wood"] = 250, ["stone"] = 150, ["iron"] = 0 } : new JsonObject { ["food"] = 0, ["wood"] = 0, ["stone"] = 0, ["iron"] = 0 },
                ["diplomacy"] = dip, ["tech_filters"] = new JsonArray(), ["include_buildings"] = id == 0,
            };
            if (id == 0)
            {
                var (x, y) = Camp(0);
                p["start_location"] = new JsonObject
                {
                    ["guid"] = EdGuid.Of("StartLocation_0"), ["name"] = "StartLocation_0", ["x"] = Math.Round(x, 3), ["y"] = Math.Round(y, 3), ["z"] = Math.Round(HAt(x, y), 3),
                    ["rot"] = Math.Round(Rad(EntranceDeg[0]) - Math.PI / 2, 4), ["ignore_pointbuy"] = false, ["include_building"] = true, ["is_sequence"] = false, ["seq_filename"] = "",
                };
                var army = new JsonArray();
                // the start in epoch 2 (custom.lua raises every seat to it and puts down a house of the seat's tribe so the
                // army fits the unit limit: the Norsemen's 5 + 8): six workers - custom.lua sends two each to food (the
                // seat's own field it puts down), wood and stone (round 4, Kacper) -, two warriors, two spearmen, an archer
                // (11 - it fits every tribe's limit in epoch 2: OutbreakTests.OutbreakEpochTwo, OutbreakMirage)
                foreach (var c in new[] { "hu_worker", "hu_worker", "hu_worker", "hu_worker", "hu_worker", "hu_worker", "hu_warrior", "hu_warrior", "hu_spearman", "hu_spearman", "hu_archer" })
                    army.Add(new JsonObject { ["class"] = c, ["level"] = 0 });
                p["start_army"] = army;
            }
            players.Add(p);
        }
        return new JsonObject
        {
            ["id"] = MissionId, ["key"] = "outbreak", ["folder"] = "outbreak", ["name"] = "Outbreak", ["title"] = "Outbreak",
            ["description"] = "A plague spreads through the northern villages: the dead rise, the beasts rot alive. Hold the base through the nights - every infected building sends its dead at you after dusk, through the north and east passes in the rocks round it, and soon through the south-west one, where a rockslide still holds them. By day they sleep, and the sun burns those caught outside: burn every infected building to end the outbreak. Two plague lords stalk the far towns at night.",
            ["tribe"] = "Hu", ["point_buy"] = false, ["min_credits"] = 0, ["lang"] = "uk", ["map"] = "Remake/missions/outbreak",
            ["map_info"] = new JsonObject
            {
                ["name"] = "Outbreak", ["title"] = "Outbreak", ["default_camera"] = 0.785, ["black_start"] = false, ["tutorial"] = false, ["setting"] = "Icewaste",
                ["start_camera"] = new JsonObject { ["x"] = Math.Round(Camp(0).X, 1), ["y"] = Math.Round(Camp(0).Y, 1) },
                ["ai_options"] = new JsonObject(),
            },
            ["players"] = players,
        };
    }

    // ------------------------------------------------------------------ world.lua
    static JsonArray Q(double rot) { double q = rot / -2; return new JsonArray(0.0, 0.0, Math.Round(Math.Sin(q), 5), Math.Round(Math.Cos(q), 5)); }

    JsonObject World(PwMap m)
    {
        var objs = new JsonArray();
        var refs = new JsonObject();
        int idx = 0;
        foreach (var v in Villages)
            foreach (var b in v.Buildings)
            {
                var g = EdGuid.Of(b.Name);
                objs.Add(new JsonObject
                {
                    ["index"] = idx++, ["guid"] = g, ["name"] = b.Name, ["class"] = b.Cls, ["type"] = "BLDG", ["owner"] = InfectedSlot,
                    ["x"] = b.X, ["y"] = b.Y, ["z"] = Math.Round(HAt(b.X, b.Y), 3), ["rot"] = b.Rot, ["q"] = Q(b.Rot), ["level"] = 0, ["visible"] = true,
                    ["attr"] = new JsonObject { ["village"] = v.Name, ["stage"] = b.Stage.ToString(CultureInfo.InvariantCulture) },
                });
                refs[g] = new JsonArray("object", b.Name);
            }
        // the rockfall: scenery props the mission addresses (refs), so they stand on their own and can be removed
        foreach (var (name, model, x, y, rot) in Barrier)
        {
            var g = EdGuid.Of(name);
            objs.Add(new JsonObject
            {
                ["index"] = idx++, ["guid"] = g, ["name"] = name, ["class"] = model, ["type"] = "DCCO",
                ["x"] = x, ["y"] = y, ["z"] = Math.Round(HAt(x, y), 3), ["rot"] = rot, ["q"] = Q(rot), ["level"] = 0, ["visible"] = true,
                ["attr"] = new JsonObject { ["barrier"] = "1" },
            });
            refs[g] = new JsonArray("object", name);
        }
        var regions = new JsonArray();
        foreach (var r in m.Regions)
        {
            var g = EdGuid.Of("region:" + r.Name);
            var shapes = new JsonArray(r.Shapes.Select(s => (JsonNode?)new JsonObject
            {
                ["type"] = s.Oval ? "oval" : "rect", ["enabled"] = s.Enabled, ["x"] = s.X, ["y"] = s.Y, ["z"] = Math.Round(HAt(s.X + s.W / 2, s.Y + s.H / 2), 3), ["w"] = s.W, ["h"] = s.H,
            }).ToArray());
            regions.Add(new JsonObject { ["guid"] = g, ["name"] = r.Name, ["color"] = "ffffffff", ["note"] = "", ["shapes"] = shapes });
            refs[g] = new JsonArray("region", r.Name);
        }
        JsonObject Quest(string name, bool main, string head, string desc) => new()
        {
            ["guid"] = EdGuid.Of("quest:" + name), ["name"] = name, ["group"] = "OB_Outbreak", ["main"] = main, ["bonus"] = new JsonObject { ["easy"] = 0, ["medium"] = 0, ["hard"] = 0 },
            ["visible"] = false, ["accomplished"] = false, ["unaccomplishable"] = false, ["headline"] = Tx(head), ["description"] = Tx(desc), ["info"] = "",
            ["group_title"] = Tx("_OB_GROUP"), ["key_headline"] = head, ["key_description"] = desc, ["key_group"] = "_OB_GROUP",
            ["attr"] = new JsonObject { ["name"] = name, ["mainquest"] = main ? "1" : "0", ["group"] = "OB_Outbreak" },
        };
        var quests = new JsonArray(
            Quest("OB_MQ01", true, "_OB_MQ01_Headline", "_OB_MQ01_Description"),
            Quest("OB_MQ02", true, "_OB_MQ02_Headline", "_OB_MQ02_Description"),
            Quest("OB_SQ01", false, "_OB_SQ01_Headline", "_OB_SQ01_Description"),
            Quest("OB_SQ02", false, "_OB_SQ02_Headline", "_OB_SQ02_Description"));
        foreach (var q in quests) refs[q!["guid"]!.GetValue<string>()] = new JsonArray("quest", q["name"]!.GetValue<string>());
        // the barebones dialogue: one voice of the survivors (no audio), the lines in texts/*.lua
        JsonObject Dialog(string id, params string[] frames)
        {
            var fr = new JsonArray();
            for (int i = 0; i < frames.Length; i++)
                fr.Add(new JsonObject { ["name"] = $"Frame_{i}", ["actor"] = "Elder", ["audio"] = "", ["key"] = frames[i], ["text"] = Tx(frames[i]), ["speaker"] = Tx("_OB_ACTOR_Elder") });
            return new JsonObject
            {
                ["path"] = $"outbreak/{id}.dlg", ["id"] = id, ["file"] = false,
                ["actors"] = new JsonObject { ["Elder"] = new JsonObject { ["class"] = "hu_worker", ["owner"] = 0, ["icon"] = "", ["generate_name"] = false, ["name_key"] = "_OB_ACTOR_Elder", ["display"] = Tx("_OB_ACTOR_Elder") } },
                ["frames"] = fr, ["sound_path"] = "", ["mentor"] = false,
            };
        }
        var dialogs = new JsonObject
        {
            ["outbreak/ob_start.dlg"] = Dialog("ob_start", "_OB_DLG_Start_0", "_OB_DLG_Start_1", "_OB_DLG_Start_2"),
            ["outbreak/ob_dusk.dlg"] = Dialog("ob_dusk", "_OB_DLG_Dusk_0", "_OB_DLG_Dusk_1"),
            ["outbreak/ob_dawn.dlg"] = Dialog("ob_dawn", "_OB_DLG_Dawn_0"),
            ["outbreak/ob_half.dlg"] = Dialog("ob_half", "_OB_DLG_Half_0", "_OB_DLG_Half_1"),
            ["outbreak/ob_win.dlg"] = Dialog("ob_win", "_OB_DLG_Win_0"),
            ["outbreak/ob_breach.dlg"] = Dialog("ob_breach", "_OB_DLG_Breach_0", "_OB_DLG_Breach_1"),
            ["outbreak/ob_lords.dlg"] = Dialog("ob_lords", "_OB_DLG_Lords_0", "_OB_DLG_Lords_1"),
            ["outbreak/ob_lord_dead.dlg"] = Dialog("ob_lord_dead", "_OB_DLG_LordDead_0"),
            ["outbreak/ob_lords_done.dlg"] = Dialog("ob_lords_done", "_OB_DLG_LordsDone_0", "_OB_DLG_LordsDone_1"),
        };
        var vars = new JsonObject
        {
            ["ob_left"] = new JsonObject { ["type"] = "int", ["value"] = Villages.Sum(v => v.Buildings.Count).ToString(CultureInfo.InvariantCulture) },
            ["ob_total"] = new JsonObject { ["type"] = "int", ["value"] = Villages.Sum(v => v.Buildings.Count).ToString(CultureInfo.InvariantCulture) },
            ["ob_night"] = new JsonObject { ["type"] = "int", ["value"] = "0" },
            ["ob_lords"] = new JsonObject { ["type"] = "int", ["value"] = "0" },
        };
        return new JsonObject
        {
            ["objects"] = objs, ["groups"] = new JsonArray(), ["question_marks"] = new JsonArray(), ["regions"] = regions, ["quests"] = quests,
            ["variables"] = vars, ["dialogs"] = dialogs, ["sequences"] = new JsonObject(), ["refs"] = refs,
            ["trigger_tree"] = new JsonObject { ["name"] = "Root", ["triggers"] = new JsonArray(), ["folders"] = new JsonArray() },
            ["trigger_defaults"] = TriggerCatalog.DefaultsJson(), ["warnings"] = new JsonArray(),
        };
    }

    // ------------------------------------------------------------------ triggers.lua: the few plain triggers
    static string Triggers() => """
-- The triggers of "Outbreak" (written by tools/outbreak - the generator; the mission's logic is custom.lua's).
-- docs/OUTBREAK.md, docs/LUA_MAPS.md

-- the night falls: the dialogue the first time (custom.lua sends the waves at each dusk)
trigger "Night falls (first)" {
  enabled = true, once = true,
  conditions = { DayPhase{ phase = "dusk", night = "==1" } },
  actions = { Dialog{ scene = "outbreak/ob_dusk.dlg" }, Sound{ soundname = "ui_quest_new", player = -1 } },
}

-- the first dawn: they withdraw
trigger "First dawn" {
  enabled = true, once = true,
  conditions = { DayPhase{ phase = "dawn", night = "==1" } },
  actions = { Dialog{ scene = "outbreak/ob_dawn.dlg" } },
}
""";

    // ------------------------------------------------------------------ texts
    static readonly Dictionary<string, (string En, string De)> Lines = new()
    {
        ["Outbreak"] = ("Outbreak", "Ausbruch"),
        ["_OB_PLAYER"] = ("Survivors", "Überlebende"),
        ["_OB_INFECTED"] = ("The Infected", "Die Infizierten"),
        ["_OB_GROUP"] = ("The Outbreak", "Der Ausbruch"),
        ["_OB_ACTOR_Elder"] = ("Village elder", "Dorfältester"),
        ["_OB_MQ01_Headline"] = ("Burn the infected villages", "Brennt die verseuchten Dörfer nieder"),
        ["_OB_MQ01_Description"] = ("Destroy every infected building. By day the infected sleep in their villages; strike then.", "Zerstört jedes verseuchte Gebäude. Bei Tag schlafen die Infizierten in ihren Dörfern - schlagt dann zu."),
        ["_OB_MQ02_Headline"] = ("Hold the base", "Haltet das Lager"),
        ["_OB_MQ02_Description"] = ("Every night the infected attack through the passes in the rocks round the base - north and east; a rockslide still blocks the south-west one. If every headquarters falls, all is lost.", "Jede Nacht greifen die Infizierten durch die Pässe in den Felsen um das Lager an - im Norden und Osten; ein Felssturz versperrt noch den im Südwesten. Fallen alle Hauptgebäude, ist alles verloren."),
        ["_OB_SQ02_Headline"] = ("Slay the two plague lords", "Erschlagt die zwei Seuchenfürsten"),
        ["_OB_SQ02_Description"] = ("Two plague lords lair in the far towns. By day they hide, out of reach; at night they stalk their towns. Go and kill both: every camp gets 500 food, 500 wood and 400 stone.", "Zwei Seuchenfürsten hausen in den fernen Städten. Bei Tag verbergen sie sich, unerreichbar; nachts streifen sie durch ihre Städte. Zieht aus und tötet beide: jedes Lager erhält 500 Nahrung, 500 Holz und 400 Stein."),
        ["_OB_SQ01_Headline"] = ("Wood and stone without end", "Holz und Stein ohne Ende"),
        ["_OB_SQ01_Description"] = ("Each camp by a pass has a giant fallen tree and a quarry that never run out.", "Jedes Lager an einem Pass hat einen riesigen umgestürzten Baum und einen Steinbruch, die nie erschöpfen."),
        ["_OB_INFO"] = ("Infected buildings left: %1 of %2 - plague lords slain: %3/2", "Verseuchte Gebäude übrig: %1 von %2 - Seuchenfürsten erschlagen: %3/2"),
        ["_OB_DLG_Breach_0"] = ("The rockslide in the south-west pass - they've torn it down!", "Der Felssturz im Südwestpass - sie haben ihn eingerissen!"),
        ["_OB_DLG_Breach_1"] = ("The back way is open. Guard the south-west pass!", "Der Hinterweg ist offen. Bewacht den Südwestpass!"),
        ["_OB_DLG_Lords_0"] = ("Something huge walks the far towns tonight.", "Etwas Riesiges geht heute Nacht durch die fernen Städte."),
        ["_OB_DLG_Lords_1"] = ("The plague lords! By day they vanish - go to their towns and kill them at night.", "Die Seuchenfürsten! Bei Tag verschwinden sie - zieht in ihre Städte und tötet sie bei Nacht."),
        ["_OB_DLG_LordDead_0"] = ("A plague lord is dead!", "Ein Seuchenfürst ist tot!"),
        ["_OB_DLG_LordsDone_0"] = ("Both plague lords are dead.", "Beide Seuchenfürsten sind tot."),
        ["_OB_DLG_LordsDone_1"] = ("The villagers bring what they hid: food, wood and stone for every camp.", "Die Dorfleute bringen, was sie versteckt hatten: Nahrung, Holz und Stein für jedes Lager."),
        ["_OB_DLG_Start_0"] = ("A plague spreads through the northern villages. The dead walk, the beasts rot alive.", "Eine Seuche breitet sich in den Dörfern des Nordens aus. Die Toten gehen um, die Tiere verfaulen bei lebendigem Leib."),
        ["_OB_DLG_Start_1"] = ("By day they hide in their houses. At night they come for the living.", "Bei Tag verkriechen sie sich in ihren Häusern. Nachts holen sie die Lebenden."),
        ["_OB_DLG_Start_2"] = ("Hold the base. Burn every infected building before the plague takes us all.", "Haltet das Lager. Brennt jedes verseuchte Gebäude nieder, bevor die Seuche uns alle holt."),
        ["_OB_DLG_Dusk_0"] = ("Night falls...", "Die Nacht bricht herein ..."),
        ["_OB_DLG_Dusk_1"] = ("They're coming! Guard the passes!", "Sie kommen! Bewacht die Pässe!"),
        ["_OB_DLG_Dawn_0"] = ("Dawn. The dead burn in the light - now we hunt them.", "Morgengrauen. Die Toten verbrennen im Licht - jetzt jagen wir sie."),
        ["_OB_DLG_Half_0"] = ("Half of the infected villages are ashes.", "Die Hälfte der verseuchten Dörfer liegt in Asche."),
        ["_OB_DLG_Half_1"] = ("Their nights grow weaker. Keep burning!", "Ihre Nächte werden schwächer. Brennt weiter!"),
        ["_OB_DLG_Win_0"] = ("The last nest burns. The outbreak is over.", "Das letzte Nest brennt. Der Ausbruch ist vorbei."),
        ["_OB_LOST"] = ("The plague has taken the base.", "Die Seuche hat das Lager überrannt."),
        ["_OB_WIN"] = ("The outbreak is contained.", "Der Ausbruch ist eingedämmt."),
        ["_GAOV_Accomplished"] = ("Mission successful!", "Mission erfolgreich!"),
        ["_GAOV_Failed"] = ("Mission has failed.", "Mission gescheitert."),
    };
    static string Tx(string key) => Lines.TryGetValue(key, out var t) ? t.En : key;
    static JsonObject Texts(bool de)
    {
        var o = new JsonObject();
        foreach (var (k, (en, d)) in Lines.OrderBy(x => x.Key, StringComparer.Ordinal)) o[k] = de ? d : en;
        return o;
    }

    /// <summary>a plague lord's lair: the open spot of its town furthest from the buildings (a titan stands there)</summary>
    static (double X, double Y) LairAt(Village v)
    {
        (double X, double Y) best = (v.X, v.Y); double bd = -1e9;
        for (double r = 0; r <= v.R - 6; r += 2)
            for (double a = 0; a < 360; a += 10)
            {
                double x = v.X + Math.Cos(a * Math.PI / 180) * r, y = v.Y + Math.Sin(a * Math.PI / 180) * r;
                double d = v.Buildings.Min(b => Math.Sqrt((x - b.X) * (x - b.X) + (y - b.Y) * (y - b.Y)) - b.R) - r * 0.05;
                if (d > bd) { bd = d; best = (x, y); }
            }
        return best;
    }

    // ------------------------------------------------------------------ layout.lua (data for custom.lua and the tests)
    static string F(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
    string LayoutLua()
    {
        var sb = new StringBuilder();
        sb.Append("-- The layout of \"Outbreak\" (written by tools/outbreak, the generator - do not edit: run it again). Data only.\n");
        sb.Append("-- Map coordinates (metres from the north-west corner, x east, y south). docs/OUTBREAK.md\n");
        sb.Append("return {\n");
        sb.Append($"  centre = {{ {F(Cx)}, {F(Cy)} }},\n  base_r = {F(Enumerable.Range(0, 72).Average(i => RimR(i * 5)))},\n  infected = {InfectedSlot},\n");
        sb.Append("  camps = {\n");
        for (int k = 0; k < 3; k++)
        {
            var (x, y) = Camp(k); var (fx, fy) = EntranceOut(k); var (tx, ty) = EntranceIn(k); var t = TreeAt(k); var s = StoneAt(k);
            sb.Append($"    {{ name = \"{CampNames[k]}\", x = {F(x)}, y = {F(y)}, entrance_out = {{ {F(fx)}, {F(fy)} }}, entrance_in = {{ {F(tx)}, {F(ty)} }}, tree = {{ {F(t.X)}, {F(t.Y)} }}, stone = {{ {F(s.X)}, {F(s.Y)} }} }},\n");
        }
        sb.Append("  },\n");
        // the rockfall: the entrance it closes (1-based), where, its rocks (world.lua objects)
        sb.Append($"  barrier = {{ entrance = {BarrierEntrance + 1}, x = {F(BarrierAt.X)}, y = {F(BarrierAt.Y)}, rocks = {{ ");
        sb.Append(string.Join(", ", Barrier.Select(b => $"\"{b.Name}\"")));
        sb.Append(" } },\n");
        // the plague lords: their village (1-based)
        sb.Append("  lords = {\n");
        foreach (var (site, name, kind) in Lairs)
        {
            var (lx, ly) = LairAt(Villages[site]);
            sb.Append($"    {{ name = \"{name}\", kind = \"{kind}\", village = {site + 1}, x = {F(lx)}, y = {F(ly)} }},\n");
        }
        sb.Append("  },\n  villages = {\n");
        foreach (var v in Villages)
        {
            sb.Append($"    {{ name = \"{v.Name}\", size = \"{v.Size}\", x = {F(v.X)}, y = {F(v.Y)}, r = {F(v.R)}, entrance = {v.Entrance + 1}, alt = {v.Alt + 1}, stage = {v.Stage}, buildings = {{ ");
            sb.Append(string.Join(", ", v.Buildings.Select(b => $"\"{b.Name}\"")));
            sb.Append(" } },\n");
        }
        sb.Append("  },\n}\n");
        return sb.ToString();
    }
}
