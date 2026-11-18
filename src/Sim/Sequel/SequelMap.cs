// The maps of the remake's own campaigns (docs/SEQUEL.md "Maps"): a MapSource made from a recipe - terrain shapes,
// ground textures of one of ParaWorld's settings, highways and streets, 1960s towns, farms and camps (the sequel's
// art), the setting's trees and rocks, resources for the player's base. The scenarios use the same recipes with the
// game's own models.
//
// Recipe (mission.json "map"; game coordinates: x east, z south, metres; the playable square is size x size around 0):
//   seed, setting ('Savanna' | 'Northland' | 'Icewaste' | 'Jungle' | 'Ashvalley'), size, base (m), water (level or null)
//   noise:    [[scale, amplitude], ...]               rolling ground
//   features: [{type: 'hill' | 'mesa' | 'basin' | 'flat' | 'ridge' | 'river' | 'strip', ...}]
//   ground:   [textureIndex, ...] of the setting's scape_0..7: [base, patches, rock, dirt (roads, towns), sand / shore, ...]
//   roads:    [{path: [[x, z], ...], width, kind: 'highway' | 'street' | 'dirt' | 'city' | 'pave', poles}]
//   places:   [{kind: 'town' | 'strip' | 'farm' | 'camp' | 'industry' | 'launch' | 'dam' | 'model' | 'block' | 'quay' |
//             'avenue' | 'lamps', x, z, rot, ...}]
//   forests:  [{x, z, r, n}]     rocks: [{x, z, r, n}]    resources: [{x, z, wood, stone, food, dx}]
//   herds:    [{species, x, z, n}]     shrubs: n
//   clear:    [[x, z, r], ...]     where nothing grows or stands (rifts, the mission's buildings, the player's base)
//
// The random numbers are the recipe's seed (mulberry32), so a map is the same as in the browser remake.
// Ported from remake/src/game/sequel/maps.js sequelSource, onRoad, junctions (pw-exporter 98c7703); the road meshes
// (roadMeshes) are the view's: game/src/View/Sequel/SequelRoads.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Sequel;

/// <summary>a road of a sequel map (the view draws it: SequelRoads; no grass on it)</summary>
public sealed class MapRoad
{
    public List<double[]> Path = new();
    public double Width = 8;
    public string Kind = "highway";
    public bool Poles = true;
}

public static class SequelMap
{
    sealed class Setting { public Regex Trees = null!, Shrubs = null!, Rocks = null!; public string Stone = "", Bush = ""; }
    static readonly Dictionary<string, Setting> SETTINGS = new()
    {
        ["Savanna"] = new() { Trees = new(@"^savanna_(tree_0\d|umbrella_acacia_0\d|baobab_0\d|koekerboom_01)$"), Shrubs = new(@"^savanna_(shrub_0\d|groundbush_0\d|agave_0\d|grass_0\d)$|^sav_(smallrocks_0\d|flowers_0\d)$"),
            Rocks = new(@"^sav_(smallrocks_0\d|rock_sand_0\d|rock_terrace_stone_0\d)$"), Stone = "resource_stone_sav", Bush = "savanna_fruit_bush" },
        ["Northland"] = new() { Trees = new(@"^northland_(tree_0\d|birch_0\d|larch_0\d|oak_0\d|maple_0\d|poplar_0\d|beech_01)$"), Shrubs = new(@"^northland_(shrub_0\d|fern_0\d|juniper_0\d|flowers_0\d|blueberry_0\d)$"),
            Rocks = new(@"^northland_(stone_0\d|boulders_01|small_rock_0\d)$"), Stone = "resource_stone_nor", Bush = "northland_fruit_bush" },
        ["Icewaste"] = new() { Trees = new(@"^icewaste_(tree_0\d|birch_0\d|oak_0\d)$"), Shrubs = new(@"^icewaste_(bush_0\d|shrub_0\d_a|fern_0\d|juniper_0\d)$"),
            Rocks = new(@"^ice_(smallrock_0\d|middlerock_0\d|snow_middlerock_0\d)$"), Stone = "resource_stone_ice", Bush = "icewaste_fruit_bush" },
        ["Ashvalley"] = new() { Trees = new(@"^ash_(dragontree_0\d|joshua_tree_0\d)$"), Shrubs = new(@"^ash_(shrub_0\d|underwood_0\d|ashgrass_0\d)$"),
            Rocks = new(@"^ash_(stone_0\d|smallstones_0\d|basalt0[1-4])$"), Stone = "resource_stone_ash", Bush = "ashvalley_fruit_bush" },
        ["Jungle"] = new() { Trees = new(@"^jungle_(tree_0\d|tree_med_0\d)$"), Shrubs = new(@"^jungle_(bush_0\d|underwood_0\d|flowers_0\d)$"),
            Rocks = new(@"^jungle_smallrock_0\d$|^jung_middle_rock_0\d$"), Stone = "resource_stone_jun", Bush = "jungle_fruit_bush" },
    };
    static readonly string[] HOUSES = { "white", "mint", "pink", "sky", "butter", "sage", "cream" };
    static readonly string[] CARS = { "cherry", "aqua", "butter", "navy", "sage", "pink" };
    static readonly string[] CAR_KINDS = { "sedan", "wagon", "pickup" };
    static readonly Dictionary<double, string[]> ROWS = new()
    {
        [16.8] = new[] { "sq_row14_1", "sq_row14_2", "sq_row14_3", "sq_row14_4" },
        [25.2] = new[] { "sq_row21_1", "sq_row21_2", "sq_row21_3", "sq_row21_4", "sq_row21_5", "sq_row21_6" },
        [33.6] = new[] { "sq_row28_1", "sq_row28_2", "sq_row28_3", "sq_row28_4" },
    };
    static readonly double[] ROW_W = { 16.8, 25.2, 33.6 };
    const double CORNER_W = 16.8, FRONT = 8.4;
    static readonly string[] CORNERS = { "sq_corner_dome", "sq_corner_turret", "sq_corner_curve", "sq_corner_chamfer" };

    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
    static double SS(double a, double b, double x) => Noise.Smoothstep(a, b, x);
    /// <summary>distance from (x, z) to a polyline</summary>
    public static double PolyDist(List<double[]> path, double x, double z)
    {
        double best = 1e9;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            double ax = path[i][0], az = path[i][1], bx = path[i + 1][0], bz = path[i + 1][1];
            double dx = bx - ax, dz = bz - az, L2 = dx * dx + dz * dz; if (L2 == 0) L2 = 1;
            double t = Math.Max(0, Math.Min(1, ((x - ax) * dx + (z - az) * dz) / L2));
            double d = Hypot(x - ax - t * dx, z - az - t * dz);
            if (d < best) best = d;
        }
        return best;
    }
    /// <summary>points every `step` metres along a polyline: [x, z, heading of the direction]</summary>
    public static List<double[]> Along(List<double[]> path, double step, double offset = 0)
    {
        var o = new List<double[]>();
        double carry = offset;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            double ax = path[i][0], az = path[i][1], bx = path[i + 1][0], bz = path[i + 1][1];
            double L = Hypot(bx - ax, bz - az);
            double s = carry;
            for (; s < L; s += step) o.Add(new[] { ax + (bx - ax) * s / L, az + (bz - az) * s / L, Math.Atan2(bx - ax, bz - az) });
            carry = s - L;
        }
        return o;
    }
    static double Bell(double d, double r) => d >= r ? 0 : 0.5 + 0.5 * Math.Cos(Math.PI * d / r);
    /// <summary>heading that turns a model's front (-z) towards (dx, dz)</summary>
    static double Face(double dx, double dz) => Math.Atan2(-dx, -dz);

    static List<double[]> Path(JsonNode? n)
    {
        var l = new List<double[]>();
        if (n is JsonArray a) foreach (var p in a) if (p is JsonArray q && q.Count >= 2) l.Add(new[] { q[0].Num(), q[1].Num() });
        return l;
    }
    static double N(JsonNode? o, string k, double def) => o?[k] is JsonValue v ? v.Num(def) : def;
    static double? NOpt(JsonNode? o, string k) => o?[k] is JsonValue v ? v.Num() : null;
    static string S(JsonNode? o, string k, string def = "") => o?[k] is JsonValue v ? v.Str(def) : def;

    sealed class Place
    {
        public string Kind = "";
        public double? X, Z;
        public double Rot, R;
        public bool HasR;
        public JsonObject J = null!;
    }

    /// <summary>is (x, z) on a road of the map (no grass there)</summary>
    public static bool OnRoad(IReadOnlyList<MapRoad>? roads, double x, double z)
    {
        if (roads == null) return false;
        foreach (var r in roads) if (PolyDist(r.Path, x, z) < r.Width / 2 + 0.5) return true;
        return false;
    }

    /// <summary>maps.js sequelSource: R the mission's map recipe (plus clear and name), rules the class data (the wood of
    /// the trees), models the asset manifest's model names (which exist; their order is the manifest's)</summary>
    public static MapSource Source(JsonObject R, IMapRules? rules, IReadOnlyCollection<string> modelNames, out List<MapRoad> roadsOut)
    {
        double seed = N(R, "seed", 7); if (seed == 0) seed = 7;
        var rnd = new Mulberry32(seed);
        double size = N(R, "size", 480), half = size / 2, world = size + 160;
        var setting = S(R, "setting", "Savanna");
        var St = SETTINGS.TryGetValue(setting, out var st0) ? st0 : SETTINGS["Savanna"];
        double? water = R["water"] is JsonValue wv ? wv.Num() : null;
        var roads = new List<MapRoad>();
        if (R["roads"] is JsonArray ra)
            foreach (var r in ra)
                roads.Add(new MapRoad { Path = Path(r?["path"]), Width = N(r, "width", 8), Kind = S(r, "kind", "highway"), Poles = r?["poles"] is not JsonValue pv || pv.Bool(true) });
        var places = new List<Place>();
        if (R["places"] is JsonArray pa)
            foreach (var p in pa)
            {
                if (p is not JsonObject po) continue;
                var pl = new Place { Kind = S(po, "kind"), X = NOpt(po, "x"), Z = NOpt(po, "z"), Rot = N(po, "rot", 0), J = po, HasR = po["r"] != null, R = N(po, "r", 0) };
                if (pl.Kind == "block")
                {
                    double x0 = N(po, "x0", 0), x1 = N(po, "x1", 0), z0 = N(po, "z0", 0), z1 = N(po, "z1", 0);
                    pl.X = (x0 + x1) / 2; pl.Z = (z0 + z1) / 2; pl.R = Math.Max(x1 - x0, z1 - z0) / 2; pl.HasR = true;
                }
                places.Add(pl);
            }
        var noise = new List<(double S, double A)>();
        if (R["noise"] is JsonArray na) foreach (var n in na) if (n is JsonArray q && q.Count >= 2) noise.Add((q[0].Num(), q[1].Num()));
        var features = R["features"] as JsonArray ?? new JsonArray();
        var feats = features.OfType<JsonObject>().Select(f => (f, path: f["path"] != null ? Path(f["path"]) : null)).ToList();
        double baseH = N(R, "base", 5); if (baseH == 0 && R["base"] == null) baseH = 5;
        var clear = new List<double[]>();
        if (R["clear"] is JsonArray ca) foreach (var c in ca) if (c is JsonArray q && q.Count >= 3) clear.Add(new[] { q[0].Num(), q[1].Num(), q[2].Num() });

        // ---------------------------------------------------------------- shape
        double Coarse(double x, double z)
        {
            double h = R["base"] is JsonValue bv0 && bv0.Num() != 0 ? bv0.Num() : 5;
            foreach (var (s, a) in noise) if (s < 0.01) h += (Noise.Fbm(x * s, z * s, seed + s * 1000, 4) - 0.5) * 2 * a;
            foreach (var (f, path) in feats)
            {
                double d = path != null ? PolyDist(path, x, z) : Hypot(x - N(f, "x", 0), z - N(f, "z", 0));
                switch (S(f, "type"))
                {
                    case "hill": h += N(f, "h", 0) * Bell(d, N(f, "r", 1)); break;
                    case "basin": h -= N(f, "depth", 0) * Bell(d, N(f, "r", 1)); break;
                    case "mesa":
                        {
                            double rr = N(f, "r", 1) * (1 + 0.12 * (Noise.Fbm(x * 0.03, z * 0.03, 91, 3) - 0.5));
                            double edge = f["edge"] is JsonValue ev && ev.Num() != 0 ? ev.Num() : 12;
                            h += N(f, "h", 0) * SS(rr + edge, rr, d); break;
                        }
                    case "ridge": h += N(f, "h", 0) * Bell(d, N(f, "w", 1)); break;
                    case "river":
                        {
                            double depth = f["depth"] is JsonValue dv && dv.Num() != 0 ? dv.Num() : 3, bank = f["bank"] is JsonValue bk && bk.Num() != 0 ? bk.Num() : 14;
                            double w = N(f, "w", 0);
                            h = h + (((water ?? 0) - depth) - h) * SS(w / 2 + bank, w / 2, d); break;
                        }
                    case "flat":
                        {
                            double edge = f["edge"] is JsonValue ev && ev.Num() != 0 ? ev.Num() : 20;
                            double fh = f["h"] is JsonValue hv ? hv.Num() : h;
                            h = h + (fh - h) * SS(N(f, "r", 0) + edge, N(f, "r", 0), d); break;
                        }
                    case "strip":
                        {
                            double edge = f["edge"] is JsonValue ev && ev.Num() != 0 ? ev.Num() : 1;
                            h = h + (N(f, "h", 0) - h) * SS(N(f, "w", 0) / 2 + edge, N(f, "w", 0) / 2, d); break;
                        }
                }
            }
            return h;
        }
        double RoadNear(double x, double z) { double k = 0; foreach (var r in roads) { double d = PolyDist(r.Path, x, z); k = Math.Max(k, SS(r.Width / 2 + 10, r.Width / 2 + 1, d)); } return k; }
        double PlaceNear(double x, double z)
        {
            double k = 0;
            foreach (var p in places)
            {
                if (p.X == null) continue;
                double pr = p.HasR && p.R != 0 ? p.R : 60;
                double d = Hypot(x - p.X.Value, z - p.Z!.Value);
                k = Math.Max(k, SS(pr + 20, pr, d));
            }
            return k;
        }
        double Fine(double x, double z) { double h = 0; foreach (var (s, a) in noise) if (s >= 0.01) h += (Noise.Fbm(x * s, z * s, seed + s * 1000, 3) - 0.5) * 2 * a; return h; }
        double Height(double x, double z)
        {
            double h = Coarse(x, z) + Fine(x, z) * (1 - Math.Max(RoadNear(x, z), PlaceNear(x, z)));
            double e = Math.Max(Math.Abs(x), Math.Abs(z));
            h += SS(half - 6, world / 2, e) * 26;            // the world's rim
            return h;
        }
        // ---------------------------------------------------------------- ground
        var G = R["ground"] is JsonArray ga ? ga.Select(x => x.Int()).ToList() : new List<int> { 0, 1, 4, 3, 6 };
        var textures = G.Select(i => $"{setting}/scape_{i}.jpg").ToList();
        int NT = textures.Count > 4 ? 8 : 4;
        // the free slots: the setting's gravel, earth and grass that buildings put down paint (World.AdaptTerrain)
        foreach (var k in new[] { 5, 3, 2 }) if (textures.Count < NT && !G.Contains(k)) textures.Add($"{setting}/scape_{k}.jpg");
        while (textures.Count < NT) textures.Add(textures[0]);
        Func<double, double, double[]> Splat(HeightField? hf) => (x, z) =>
        {
            var w = new double[NT];
            w[0] = 1;
            void Mix(int k, double f) { if (f <= 0 || k >= NT) return; for (int i = 0; i < NT; i++) w[i] *= 1 - f; w[k] += f; }
            Mix(1, SS(0.45, 0.62, Noise.Fbm(x * 0.012, z * 0.012, 33, 4)));                                   // patches
            if (G.Count > 5) Mix(5, SS(0.55, 0.7, Noise.Fbm(x * 0.02 + 9, z * 0.02, 57, 3)));
            if (water != null) Mix(4, SS(water.Value + 2.5, water.Value + 0.6, hf != null ? hf.At(x, z) : Height(x, z)));      // shore
            Mix(3, Math.Max(RoadNear(x, z) * 0.9, PlaceNear(x, z) * 0.55));                                         // roads, towns
            if (hf != null) Mix(2, SS(0.86, 0.72, hf.NormalY(x, z)));                                               // slopes
            return w;
        };
        // ---------------------------------------------------------------- objects
        var models = new ModelSet();
        var have = new HashSet<string>(modelNames, StringComparer.Ordinal);
        List<string> ListOf(Regex re) => modelNames.Where(k => re.IsMatch(k)).ToList();
        bool Has(string? m) => m != null && have.Contains(m);
        var treeModels = ListOf(St.Trees); var shrubModels = ListOf(St.Shrubs); var rockModels = ListOf(St.Rocks);
        var decor = new List<MapDecor>(); var trees = new List<MapTree>(); var stones = new List<MapResource>(); var bushes = new List<MapResource>(); var animals = new List<MapAnimal>();
        var taken = new List<double[]>();
        bool Free(double x, double z, double r)
        {
            if (!(Math.Abs(x) < half - 8 && Math.Abs(z) < half - 8)) return false;
            foreach (var t in taken) if (!(Hypot(x - t[0], z - t[1]) > r + t[2])) return false;
            foreach (var c in clear) if (!(Hypot(x - c[0], z - c[1]) > r + c[2])) return false;
            foreach (var rd in roads) if (!(PolyDist(rd.Path, x, z) > rd.Width / 2 + r + 1)) return false;
            return true;
        }
        bool Put(string model, double x, double z, double rot, double r = 3, bool force = false, bool block = true, double? y = null, double scale = 1)
        {
            if (!force && !Free(x, z, r)) return false;
            if (!have.Contains(model)) return false;
            models.Use(model);
            decor.Add(new MapDecor { Model = model, X = x, Z = z, Rot = rot, Scale = scale == 0 ? 1 : scale, Block = block, Y = y });
            taken.Add(new[] { x, z, r });
            return true;
        }
        T Pick<T>(IReadOnlyList<T> a) => a[(int)Math.Floor(rnd.Next() * a.Count)];
        bool OnStreet(double x, double z, double m = 0.6) { foreach (var rd in roads) if (rd.Kind != "pave" && PolyDist(rd.Path, x, z) < rd.Width / 2 + m) return true; return false; }
        (double, double) Local(Place p, double lx, double lz) { double c = Math.Cos(p.Rot), s = Math.Sin(p.Rot); return (p.X!.Value + lx * c + lz * s, p.Z!.Value - lx * s + lz * c); }
        double[] LocalA(Place p, double lx, double lz) { var (x, z) = Local(p, lx, lz); return new[] { x, z }; }
        // streets of the places first (they shape the ground and keep the lots free)
        foreach (var p in places)
        {
            if (p.Kind != "town") continue;
            int n = (int)N(p.J, "blocks", 2); if (p.J["blocks"] == null) n = 2;
            double L = (p.J["len"] is JsonValue lv ? lv.Num() : 140) / 2;
            for (int i = -n; i <= n; i++) roads.Add(new MapRoad { Path = new() { LocalA(p, -L, i * 46), LocalA(p, L, i * 46) }, Width = 7, Kind = "street" });
            roads.Add(new MapRoad { Path = new() { LocalA(p, 0, -n * 46 - 10), LocalA(p, 0, n * 46 + 10) }, Width = 7, Kind = "street" });
        }
        foreach (var p in places)
        {
            double rot = p.Rot;
            bool Lot(double lx, double lz, string model, double r, double fdx, double fdz, bool block = true, bool force = false)
            {
                var (x, z) = Local(p, lx, lz);
                double c = Math.Cos(rot), s = Math.Sin(rot);
                return Put(model, x, z, Face(fdx * c + fdz * s, -fdx * s + fdz * c), r, force, block);
            }
            string Car() { var k = Pick(CAR_KINDS); return $"sq_car_{k}_{Pick(CARS)}"; }
            switch (p.Kind)
            {
                case "town":
                    {
                        int n = p.J["blocks"] == null ? 2 : (int)N(p.J, "blocks", 2);
                        double L = (p.J["len"] is JsonValue lv ? lv.Num() : 140) / 2;
                        double empty = p.J["empty"] is JsonValue ev && ev.Num() != 0 ? ev.Num() : 0.15;
                        for (int i = -n; i <= n; i++)
                        {
                            foreach (var side in new[] { -1, 1 })
                            {
                                for (double lx = -L + 16; lx < L - 10; lx += 25)      // (the houses are built 1.3x life size)
                                {
                                    if (Math.Abs(lx) < 12) continue;
                                    if (rnd.Next() < empty) continue;
                                    var w = Pick(HOUSES); bool two = rnd.Next() < 0.35;
                                    Lot(lx, i * 46 + side * 17, $"sq_house_{(two ? "two" : "ranch")}_{w}", 9.5, 0, -side);
                                    if (rnd.Next() < 0.6) Lot(lx + 9, i * 46 + side * 8, Car(), 2.6, 0, -side, block: false);
                                    if (rnd.Next() < 0.5) Lot(lx - 5, i * 46 + side * 5.5, "sq_mailbox", 0.5, 0, -side, block: false);
                                }
                                for (double lx = -L + 6; lx < L; lx += 30) Lot(lx, i * 46 + side * 5, "sq_telephone_pole", 0.6, 0, 1);
                            }
                        }
                        if (!(p.J["landmarks"] is JsonValue lm && !lm.Bool(true)))
                        {
                            Lot(-L - 18, 0, "sq_water_tower", 6, 1, 0);
                            Lot(10, -n * 46 - 26, "sq_diner", 9, 0, 1);
                            Lot(-24, -n * 46 - 26, "sq_gas_station", 13, 0, 1);
                        }
                        break;
                    }
                case "strip":
                    {
                        Lot(-30, 18, "sq_gas_station", 13, 0, -1); Lot(0, 16, "sq_diner", 9, 0, -1); Lot(26, 18, "sq_billboard", 4, 0, -1, block: false);
                        int cars = p.J["cars"] is JsonValue cv && cv.Num() != 0 ? (int)cv.Num() : 5;
                        for (int k = 0; k < cars; k++) { var c = Car(); Lot(-12 + k * 6, 8 + (k % 2) * 3, c, 2.4, rnd.Next() - 0.5, -1, block: false); }
                        break;
                    }
                case "farm":
                    Lot(0, 0, "sq_barn", 11, 0, -1); Lot(16, 6, "sq_silo", 4, 0, -1); Lot(-18, 4, "sq_house_two_" + Pick(HOUSES), 8, 0, -1);
                    Lot(-10, -14, $"sq_car_pickup_{Pick(CARS)}", 2.6, 1, 0, block: false);
                    for (int k = -4; k <= 4; k++) Lot(k * 4.2, -24, "sq_picket_fence", 0.4, 0, -1, block: false, force: true);
                    break;
                case "camp":
                    {
                        int tents = p.J["tents"] is JsonValue tv && tv.Num() != 0 ? (int)tv.Num() : 4;
                        for (int k = 0; k < tents; k++) Lot(-20 + k * 9, 20, "sq_tent", 4, 0, -1);
                        Lot(22, 0, "sq_quonset", 13, 1, 0); Lot(-26, -10, "sq_watchtower", 4, 0, -1);
                        Lot(0, -22, "sq_checkpoint", 3, 0, -1); for (int k = 0; k < 3; k++) Lot(-8 + k * 4, -16, "sq_sandbags", 3, 0, -1, block: true);
                        Lot(8, 10, "sq_army_truck", 5, 1, 0, block: false); Lot(14, 22, "sq_drums", 1.5, 0, 1); Lot(-8, 4, "sq_crates", 2, 0, 1);
                        break;
                    }
                case "industry":
                    Lot(0, 0, "sq_power_station", 20, 0, -1);
                    for (int k = -2; k <= 2; k++) Lot(k * 14, 40, "sq_pylon", 4, 0, 1);
                    break;
                case "launch":
                    if (!(p.J["pad"] is JsonValue pv && !pv.Bool(true))) Lot(0, 0, "sq_rocket", 14, 0, 1);      // (a mission that places the launch pad itself: pad false)
                    Lot(-40, 10, "sq_quonset", 13, 1, 0); Lot(30, -20, "sq_radar", 4, 0, -1);
                    break;
                case "dam":
                    for (int k = -2; k <= 2; k++) Lot(k * 40, 0, "sq_dam", 22, 0, 1, force: true);
                    break;
                case "model":
                    Put(S(p.J, "model"), p.X ?? 0, p.Z ?? 0, rot, p.J["r"] is JsonValue rv ? rv.Num() : 5, true, !(p.J["block"] is JsonValue bv && !bv.Bool(true)), NOpt(p.J, "y"), N(p.J, "scale", 1));
                    break;
                case "block": LondonBlock(p.J, (m, x, z, r) => Put(m, x, z, r, 0, true), Pick); break;
                case "quay":
                    {
                        var path = Path(p.J["path"]);
                        double step = p.J["step"] is JsonValue sv && sv.Num() != 0 ? sv.Num() : 15.6, off = N(p.J, "off", 0);
                        var gaps = p.J["gaps"] is JsonArray gp ? gp.Select(x => x.Num()).ToList() : new List<double>();
                        var steps = p.J["steps"] is JsonArray sp ? sp.Select(x => x.Num()).ToList() : new List<double>();
                        foreach (var q in Along(path, step, step / 2))
                        {
                            double x = q[0], z = q[1], a = q[2];
                            double nx = Math.Cos(a), nz = -Math.Sin(a), s = Math.Sign(off);
                            double qx = x + nx * off, qz = z + nz * off;
                            if (gaps.Any(gx => Math.Abs(gx - qx) < 12)) continue;           // (a bridge's end)
                            bool stairs = steps.Any(sx => Math.Abs(sx - qx) < 8);
                            Put(stairs ? "sq_quay_steps" : "sq_quay", qx, qz, Face(-nx * s, -nz * s), 0, true, false, NOpt(p.J, "y"));
                        }
                        break;
                    }
                case "lamps":
                    {
                        var path = Path(p.J["path"]);
                        double step = p.J["step"] is JsonValue sv && sv.Num() != 0 ? sv.Num() : 24, start = p.J["start"] is JsonValue s0 && s0.Num() != 0 ? s0.Num() : 6, off = N(p.J, "off", 0);
                        foreach (var q in Along(path, step, start))
                        {
                            double lx = q[0] + Math.Cos(q[2]) * off, lz = q[1] - Math.Sin(q[2]) * off;
                            if (OnStreet(lx, lz)) continue;                   // (where a side street meets the one it lines)
                            Put(S(p.J, "model", "sq_lamp_london"), lx, lz, q[2], 0.4, true, false);
                        }
                        break;
                    }
            }
        }
        // poles along highways
        foreach (var r in roads.ToList())
            if (r.Kind == "highway" && r.Poles)
                foreach (var q in Along(r.Path, 36, 10)) Put("sq_telephone_pole", q[0] + Math.Cos(q[2]) * (r.Width / 2 + 5), q[1] - Math.Sin(q[2]) * (r.Width / 2 + 5), q[2], 0.6);
        // forests (choppable trees), rocks and shrubs
        bool Tree(double x, double z)
        {
            if (treeModels.Count == 0 || !Free(x, z, 2)) return false;
            var model = Pick(treeModels);
            models.Use(model);
            double wood = rules?.ResourceValue("WOOD", model + "_timber") ?? 300;
            string? stump = Has(model + "_stump") ? model + "_stump" : null, timber = Has(model + "_timber") ? model + "_timber" : null;
            models.Use(stump); models.Use(timber);
            double trot = rnd.Next() * 6.28, tscale = 0.8 + rnd.Next() * 0.5;
            trees.Add(new MapTree { Model = model, X = x, Z = z, Rot = trot, Scale = tscale, Wood = wood, Inside = true, Stump = stump, Timber = timber, Big = !Regex.IsMatch(model, "_med_|bamboo|dead") });
            taken.Add(new[] { x, z, 2.0 });
            return true;
        }
        if (R["forests"] is JsonArray fa)
            foreach (var f in fa)
            {
                double fn = N(f, "n", 0), fr = N(f, "r", 0), fx = N(f, "x", 0), fz = N(f, "z", 0);
                for (int k = 0, made = 0; k < fn * 4 && made < fn; k++)
                {
                    double a = rnd.Next() * 6.283, d = Math.Sqrt(rnd.Next()) * fr;
                    if (Tree(fx + Math.Cos(a) * d, fz + Math.Sin(a) * d)) made++;
                }
            }
        foreach (var p in places)
        {
            if (p.Kind != "avenue") continue;
            double step = p.J["step"] is JsonValue sv && sv.Num() != 0 ? sv.Num() : 14, start = p.J["start"] is JsonValue s0 && s0.Num() != 0 ? s0.Num() : 7, off = N(p.J, "off", 0);
            foreach (var q in Along(Path(p.J["path"]), step, start)) Tree(q[0] + Math.Cos(q[2]) * off, q[1] - Math.Sin(q[2]) * off);
        }
        if (R["rocks"] is JsonArray rka)
            foreach (var f in rka)
            {
                double fn = N(f, "n", 0), fr = N(f, "r", 0), fx = N(f, "x", 0), fz = N(f, "z", 0);
                for (int k = 0; k < fn; k++)
                {
                    double a = rnd.Next() * 6.283, d = Math.Sqrt(rnd.Next()) * fr;
                    if (rockModels.Count > 0) { var m = Pick(rockModels); double rr = rnd.Next() * 6.28, sc = 0.8 + rnd.Next() * 0.8; Put(m, fx + Math.Cos(a) * d, fz + Math.Sin(a) * d, rr, 3, scale: sc); }
                }
            }
        int shrubs = R["shrubs"] is JsonValue shv ? (int)shv.Num() : 260;
        for (int k = 0; k < shrubs; k++)
        {
            double x = (rnd.Next() * 2 - 1) * (half - 10), z = (rnd.Next() * 2 - 1) * (half - 10);
            if (shrubModels.Count > 0 && (water == null || Coarse(x, z) > water + 0.5)) { var m = Pick(shrubModels); double rr = rnd.Next() * 6.28; Put(m, x, z, rr, 1.2, block: false); }
        }
        // a base's resources: every deposit and bush is placed (the ring widens while the base, the roads and the trees
        // leave no room), so a mission never starts without stone or food at hand
        void Ring(double rx, double rz, int n, double sz, double d0, double d1, double dx, Action<double, double> add)
        {
            for (int k = 0, made = 0; k < n * 40 && made < n; k++)
            {
                double a = rnd.Next() * 6.283, d = d0 + rnd.Next() * (d1 + k * 0.5), x = rx + 40 * dx + Math.Cos(a) * d, z = rz + Math.Sin(a) * d;
                if (Free(x, z, sz)) { add(x, z); taken.Add(new[] { x, z, sz }); made++; }
            }
        }
        if (R["resources"] is JsonArray rsa)
            foreach (var r in rsa)
            {
                double rx = N(r, "x", 0), rz = N(r, "z", 0);
                Ring(rx, rz, (int)N(r, "stone", 0), 4, 6, 14, 0, (x, z) => { stones.Add(new MapResource { Model = St.Stone, X = x, Z = z, Rot = rnd.Next() * 6.28, Amount = 2000 }); models.Use(St.Stone); });
                Ring(rx, rz, (int)N(r, "food", 0), 2, 6, 16, N(r, "dx", 0), (x, z) => { bushes.Add(new MapResource { Model = St.Bush, X = x, Z = z, Rot = rnd.Next() * 6.28, Amount = 150 }); models.Use(St.Bush); });
                // the trees too: all of them
                int wood = (int)N(r, "wood", 0);
                for (int k = 0, made = 0; k < wood * 40 && made < wood; k++) { double a = rnd.Next() * 6.283, d = 18 + rnd.Next() * (26 + k * 0.4); if (Tree(rx + Math.Cos(a) * d, rz + Math.Sin(a) * d)) made++; }
            }
        if (R["herds"] is JsonArray ha)
            foreach (var h in ha)
                for (int k = 0; k < N(h, "n", 0); k++)
                {
                    double hx = N(h, "x", 0), hz = N(h, "z", 0);
                    double ax = hx + (rnd.Next() - 0.5) * 20, az = hz + (rnd.Next() - 0.5) * 20;
                    animals.Add(new MapAnimal { Species = S(h, "species"), X = ax, Z = az, Home = new[] { hx, hz } });
                }
        foreach (var a in animals) models.Use(a.Species.ToLowerInvariant());
        foreach (var m in new[] { "product_wood_jun", "product_stone_jun", "jungle_tree_01_stump", "jungle_tree_01_timber", "jungle_tree_02_timber" }) models.Use(m);
        var starts = new List<double[]>();
        if (R["starts"] is JsonArray sa) foreach (var s in sa) if (s is JsonArray q) starts.Add(new[] { q[0].Num(), q[1].Num() });
        if (starts.Count == 0) starts.Add(new double[] { 0, 0 });
        roadsOut = roads;
        return new MapSource
        {
            Kind = "sequel", Name = S(R, "name", "Sequel"), Setting = setting, Size = world, MeshCell = 2,
            Play = new PlayArea { X0 = -half, X1 = half, Z0 = -half, Z1 = half }, Water = water,
            Height = Height, Textures = textures, Scales = textures.Select(_ => new[] { 1 / 22.0, 1 / 22.0 }).ToList(), Splat = Splat,
            Trees = trees, Decor = decor, Stones = stones, Bushes = bushes, Animals = animals,
            Starts = starts, Bases = starts.Select(s => (double[])s.Clone()).ToList(), Models = models, Skirt = true, LevelName = "",
        };
    }

    // a London block: rows of houses round the edges named by `sides` ('n' the edge at z0, 's' at z1, 'w' at x0, 'e' at
    // x1), their fronts to the street, and a corner building where two of them meet (maps.js londonBlock)
    static void LondonBlock(JsonObject p, Action<string, double, double, double> put, Func<IReadOnlyList<string>, string> pick)
    {
        string sides = p["sides"] is JsonValue sv ? sv.Str() : "nesw";
        double x0 = N(p, "x0", 0), x1 = N(p, "x1", 0), z0 = N(p, "z0", 0), z1 = N(p, "z1", 0);
        var E = new Dictionary<char, double[][]>
        {
            ['n'] = new[] { new[] { x0, z0 }, new[] { x1, z0 }, new double[] { 0, -1 } }, ['s'] = new[] { new[] { x1, z1 }, new[] { x0, z1 }, new double[] { 0, 1 } },
            ['w'] = new[] { new[] { x0, z1 }, new[] { x0, z0 }, new double[] { -1, 0 } }, ['e'] = new[] { new[] { x1, z0 }, new[] { x1, z1 }, new double[] { 1, 0 } },
        };
        const string order = "nesw";
        bool Corner(char a, char b) => sides.Contains(a) && sides.Contains(b);
        string? last = null;
        string Other(IReadOnlyList<string> list) { var l = list.Where(n => n != last).ToList(); return last = pick(l.Count > 0 ? l : list); }
        // a pick over row widths (the same random stream as the models' picks)
        double PickW(List<double> ws) => ws[(int)Math.Floor(PickIndex(pick, ws.Count))];
        for (int i = 0; i < 4; i++)
        {
            char k = order[i], nk = order[(i + 1) % 4];
            if (!sides.Contains(k)) continue;
            var e = E[k];
            double ax = e[0][0], az = e[0][1], bx = e[1][0], bz = e[1][1], nx = e[2][0], nz = e[2][1];
            double L = Hypot(bx - ax, bz - az), dx = (bx - ax) / L, dz = (bz - az) / L;
            bool c0 = Corner(order[(i + 3) % 4], k), c1 = Corner(k, nk);
            double s0 = c0 ? CORNER_W : 0, s1 = c1 ? CORNER_W : 0;
            var (ws, left) = FillRows(L - s0 - s1, PickW);
            double t = s0 + left / 2;
            foreach (var w in ws)
            {
                put(Other(ROWS[w]), ax + dx * (t + w / 2) - nx * FRONT, az + dz * (t + w / 2) - nz * FRONT, Face(nx, nz));
                t += w;
            }
            if (c1)
            {
                var m = E[nk][2];
                double cx = bx - nx * CORNER_W / 2 - m[0] * CORNER_W / 2, cz = bz - nz * CORNER_W / 2 - m[1] * CORNER_W / 2;
                put(Other(CORNERS), cx, cz, -Math.PI / 4 - Math.Atan2(nz + m[1], nx + m[0]));
            }
        }
    }

    // pick(list) of the JS takes rnd() * list.length: the index it would take from a list of n, from a pick over strings
    static double PickIndex(Func<IReadOnlyList<string>, string> pick, int n)
    {
        var idx = Enumerable.Range(0, n).Select(i => i.ToString()).ToList();
        return int.Parse(pick(idx));
    }

    static (List<double> Ws, double Left) FillRows(double len, Func<List<double>, double> pick)
    {
        var best = new List<double>(); double left = len;
        for (int t = 0; t < 16; t++)
        {
            var ws = new List<double>(); double rest = len;
            for (; ; )
            {
                var fit = ROW_W.Where(w => w <= rest + 1e-6).ToList();
                if (fit.Count == 0) break;
                double w = pick(fit);
                ws.Add(w); rest -= w;
            }
            if (rest < left - 1e-6) { best = ws; left = rest; }
        }
        return (best, left);
    }
}
