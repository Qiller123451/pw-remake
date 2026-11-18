// Co-op maps: the base sites' land made from patches of the old map itself ("stamps", docs/COOP.md "Maps").
//
// A stamp takes a round patch of the old map - a piece of flat-ish land with its coast - and puts it, turned and maybe
// mirrored, where a co-op seat's base goes: its heights (relative to the water), ground materials, plants, decoration
// (the setting's rocks, plateaus, shrubs: no buildings, units, walls, mission objects) and natural resources (trees,
// stones, fruit bushes, forest squares). The patch's edge fades into the sea along a noise-bent line, so the new land
// has the old map's own look and a natural coast.
//
//   AutoStamp(md, site, dir, used)  the best patch for a site: a flat spot of the site's radius (land, gentle, no
//                                   buildings or start locations near), a walkable way from it to its coast, much water
//                                   on the patch's rim (a coastal piece: little land cut off), varied ground and plants;
//                                   turned so that its coast faces the site's land bridge
//   StampInfo.ToSource / ToTarget   the transform (64 directions: plain arithmetic, the same on every machine)
//   CopyStampContent                the patch's objects, plants and forest squares onto the new land
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PwRemake.Sim.Maps;

/// <summary>a stamp as made: the site it is for, the patch of the old map (centre, radius), its turn (in 64ths of a
/// full turn, clockwise seen from above) and mirroring</summary>
public sealed class StampInfo
{
    public int Seat;
    public double SiteX, SiteZ, SX, SZ, R;
    /// <summary>the turn in steps of 360 / 64 degrees (0 .. 63)</summary>
    public int Rot;
    public bool Mirror, Auto;
    public double Score;
    public double RotDegrees => Rot * 360.0 / MapExtension.DIRS;

    /// <summary>the old map's point a point of the new land is made from</summary>
    public (double X, double Z) ToSource(double x, double z)
    {
        double dx = x - SiteX, dz = z - SiteZ, c = MapExtension.DirC(Rot), s = MapExtension.DirS(Rot);
        double rx = dx * c + dz * s, rz = -dx * s + dz * c;
        if (Mirror) rz = -rz;
        return (SX + rx, SZ + rz);
    }
    /// <summary>where a point of the old map's patch lands</summary>
    public (double X, double Z) ToTarget(double x, double z)
    {
        double dx = x - SX, dz = z - SZ, c = MapExtension.DirC(Rot), s = MapExtension.DirS(Rot);
        if (Mirror) dz = -dz;
        return (SiteX + dx * c - dz * s, SiteZ + dx * s + dz * c);
    }
    /// <summary>a heading (map Rot: about the map's z, from x towards y) of the patch in the new land</summary>
    public double Heading(double rot) => (Mirror ? -rot : rot) + Rot * 2 * Math.PI / MapExtension.DIRS;
    public override string ToString() => $"seat {Seat}: {(Auto ? "auto " : "")}stamp from ({SX:0}, {SZ:0}) r {R:0}, turned {RotDegrees:0.#} deg{(Mirror ? ", mirrored" : "")}";
}

public static partial class MapExtension
{
    /// <summary>the directions stamps are turned by: 64 (5.625 degrees)</summary>
    public const int DIRS = 64;
    static readonly double[] _dc = new double[DIRS], _ds = new double[DIRS];
    static MapExtension()
    {
        for (int k = 0; k < DIRS; k++) { var (s, c) = SinCos(2 * Math.PI * k / DIRS); _dc[k] = c; _ds[k] = s; }
    }
    public static double DirC(int k) => _dc[((k % DIRS) + DIRS) % DIRS];
    public static double DirS(int k) => _ds[((k % DIRS) + DIRS) % DIRS];
    /// <summary>the direction (0 .. 63) nearest to (dx, dz)</summary>
    public static int DirIndex(double dx, double dz)
    {
        int best = 0; double bd = -1e18;
        for (int k = 0; k < DIRS; k++) { double d = dx * _dc[k] + dz * _ds[k]; if (d > bd) { bd = d; best = k; } }
        return best;
    }
    /// <summary>sin and cos by their series (deterministic: no library calls), any angle</summary>
    public static (double S, double C) SinCos(double a)
    {
        double tp = 2 * Math.PI;
        a -= Math.Floor(a / tp + 0.5) * tp;                         // -pi .. pi
        double a2 = a * a;
        double s = a * (1 - a2 / 6 * (1 - a2 / 20 * (1 - a2 / 42 * (1 - a2 / 72 * (1 - a2 / 110 * (1 - a2 / 156 * (1 - a2 / 210 * (1 - a2 / 272))))))));
        double c = 1 - a2 / 2 * (1 - a2 / 12 * (1 - a2 / 30 * (1 - a2 / 56 * (1 - a2 / 90 * (1 - a2 / 132 * (1 - a2 / 182 * (1 - a2 / 240)))))));
        return (s, c);
    }

    // ------------------------------------------------------------------------------------------------ what a stamp copies
    /// <summary>decoration of the setting itself (rocks, plateaus, shrubs, flowers ...): by its name's prefix</summary>
    static readonly Regex NATURAL = new("^(northland|nor|sav|savanna|jungle|jung|ice|icewaste|ash)_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>... but not what people built or what a mission uses</summary>
    static readonly Regex MAN_MADE = new("wall|fence|ruin|bridge|house|hut|temple|pillar|harbour|container|place|statue|monument|tower|camp|poison|wreck|seas|carrier|valhalla|tutorial|fishnet|waterfall|river|sign|flag|grave|altar|totem|gate|footbridge|mammoth|skull|bones|lamp|tent|well|cage|dock|pier|boat|ship|fire",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>a rock, plateau, cliff ... shaped to its ground (MapSource.SOLID; bushes, shrubs, grass and flowers are
    /// plants even where their name says "mountain")</summary>
    static bool SolidDeco(string n) => MapSource.SOLID.IsMatch(n.ToLowerInvariant()) && !PLANT.IsMatch(n);
    static readonly Regex PLANT = new("bush|shrub|grass|flower|fern|reed|weed|underwood|blueberry|juniper", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static bool NaturalName(string n) => NATURAL.IsMatch(n) && !MAN_MADE.IsMatch(n) && !MapSource.NO_PROP.IsMatch(n.ToLowerInvariant());
    /// <summary>a map object a stamp copies: nobody's tree, stone, fruit bush, fish, or the setting's decoration</summary>
    static bool Copyable(MapObject o) => o.Owner == null && (o.Type is "TREE" or "STON" or "FRUI" || o.Type is "DCCO" or "DECO" or "VGTN" && NaturalName(o.Cls));
    /// <summary>an object of the old map whose ground a stamp should not copy (its place is a mission's: buildings, units,
    /// start locations, nests ...); the weight is how much it counts against a patch</summary>
    static double Foreign(MapObject o)
    {
        // (units, nests, quest marks ... stand on natural ground: they do not count; buildings and what people built
        // sit on levelled ground, roads and foundations)
        if (Copyable(o)) return 0;
        if (o.Type is "BLDG" or "SLOC") return 1;
        if (o.Type is "DCCO" or "DECO" or "VGTN" or "WOOD") return 0.5;
        return 0;
    }

    // ------------------------------------------------------------------------------------------------ choosing a patch
    /// <summary>the coast seen from (cx, cz) of the old map: the direction (0 .. 63, every second) in which the water is
    /// nearest, at least r0 out and at most rMax - by a walkable way if there is one (`walkable`); -1 if there is none</summary>
    static int CoastDir(MapData md, double cx, double cz, double r0, double rMax, out double dist, out bool walkable)
    {
        dist = 1e9; int best = -1; walkable = false;
        double water = md.Water, any = 1e9; int anyK = -1;
        for (int k = 0; k < DIRS; k += 2)
        {
            double prev = md.HeightAtMap(cx, cz);
            bool ok = true;
            for (double d = 8; d <= rMax; d += 8)
            {
                double x = cx + _dc[k] * d, z = cz + _ds[k] * d, h = md.HeightAtMap(x, z);
                if (h < water - 0.3)
                {
                    if (d >= r0 && ok && d < dist) { dist = d; best = k; }
                    if (d >= r0 && d < any) { any = d; anyK = k; }
                    break;
                }
                if (Math.Abs(h - prev) > 8 * 0.42) ok = false;      // a cliff: no walkable way down to the water this way
                prev = h;
            }
        }
        walkable = best >= 0;
        if (best < 0) { best = anyK; dist = any; }
        return best;
    }

    /// <summary>a flat-ish spot of radius r at (cx, cz) of the old map: land, gentle, walkable. Returns the height range
    /// (m), or -1 when it is not one</summary>
    static double FlatSpot(MapData md, double cx, double cz, double r, bool loose = false)
    {
        double water = md.Water, lo = 1e9, hi = -1e9;
        int walk = 0, n = 0, steep = 0;
        for (double rr = 0; rr <= r + 0.01; rr += Math.Max(8, r / 6))
        {
            int step = rr < 1 ? DIRS : rr < r * 0.5 ? 8 : 4;
            for (int k = 0; k < DIRS; k += step)
            {
                double x = cx + _dc[k] * rr, z = cz + _ds[k] * rr, h = md.HeightAtMap(x, z);
                if (h < water + 1.5) return -1;
                lo = Math.Min(lo, h); hi = Math.Max(hi, h);
                if (hi - lo > (loose ? 24 : 16)) return -1;
                double sl = Math.Max(Math.Abs(md.HeightAtMap(x + 3, z) - md.HeightAtMap(x - 3, z)), Math.Abs(md.HeightAtMap(x, z + 3) - md.HeightAtMap(x, z - 3))) / 6;
                if (sl > 0.45) steep++;
                n++;
                if (md.Walk == null || md.WalkableMap(x, z) == true) walk++;
            }
        }
        if (walk < n * (loose ? 0.5 : 0.65) || steep > n * (loose ? 0.4 : 0.25)) return -1;
        return hi - lo + 20.0 * steep / n;
    }

    /// <summary>the best patch of the old map for a base site whose land bridge leaves towards (dirX, dirZ); patches
    /// already used (centres) are avoided. null when the map has none (then the site's land is generated)</summary>
    public static StampInfo? AutoStamp(MapData md, CoopSite site, double dirX, double dirZ, IReadOnlyList<(double X, double Z)> used, int seed, int index)
    {
        double R = site.Stamp?.R ?? 160, rb = site.R, water = md.Water;
        // (the patch's land inside the old map's playable ground: its 32 m frame is the editor's border)
        double mg = R * 0.75 + 40;
        double x0 = md.OffX + mg, x1 = md.OffX + (md.Hx - 1) * 2 - mg, z0 = md.OffY + mg, z1 = md.OffY + (md.Hy - 1) * 2 - mg;
        // objects that count against a patch, in 64 m buckets
        var foreign = md.Objects.Select(o => (o.X, o.Y, W: Foreign(o), o.Type)).Where(q => q.W > 0).ToList();
        var natural = md.Objects.Where(o => o.Owner == null && o.Type == "TREE").Select(o => (o.X, o.Y)).ToList();
        var forest = md.Forest.Blocks.Select(b => (X: b.X * 32.0 + 16, Y: b.Y * 32.0 + 16)).ToList();
        StampInfo? best = null; double bestScore = -1e9;
        int kv = DirIndex(dirX, dirZ);
        bool mirror = site.Stamp?.Mirror ?? ((index + seed) & 1) == 1;
        // (a second, looser pass for maps whose gentle land is all built on or hilly: steeper ground, buildings nearer; a
        // third that may use another site's patch again - turned and mirrored differently)
        for (int pass = 0; pass < 3 && best == null; pass++)
        for (double cz = Math.Ceiling(z0 / 16) * 16; cz <= z1; cz += 16)
            for (double cx = Math.Ceiling(x0 / 16) * 16; cx <= x1; cx += 16)
            {
                if (pass < 2 && used.Any(u => Sq(u.X - cx) + Sq(u.Z - cz) < Sq(R * 0.8))) continue;
                double range = FlatSpot(md, cx, cz, rb * 0.75, pass > 0);
                if (range < 0) continue;
                double pen = 0; bool reject = false;
                foreach (var f in foreign)
                {
                    double d2 = Sq(f.X - cx) + Sq(f.Y - cz);
                    if (d2 > R * R) continue;
                    if (f.W >= 1 && d2 < Sq(pass > 0 ? rb * 0.5 : rb + 10) || (f.Type == "SLOC" && d2 < 120 * 120)) { reject = true; break; }
                    pen += f.W;
                }
                if (reject) continue;
                int ku = CoastDir(md, cx, cz, rb * 0.75, R * 0.8, out double dw, out bool walkable);
                if (ku < 0) continue;
                // the rim: how much of it is water (a coastal piece: little land cut off by the fade)
                int wet = 0, mats = 0;
                for (int k = 0; k < DIRS; k++) if (md.HeightAtMap(cx + _dc[k] * R * 0.92, cz + _ds[k] * R * 0.92) < water - 0.5) wet++;
                var mc = new int[8]; int mn = 0;
                for (double dz = -R * 0.7; dz <= R * 0.7; dz += 16)
                    for (double dx = -R * 0.7; dx <= R * 0.7; dx += 16)
                    {
                        if (dx * dx + dz * dz > R * R * 0.49) continue;
                        if (md.HeightAtMap(cx + dx, cz + dz) < water + 0.5) continue;
                        int m = md.MaterialAtMap(cx + dx, cz + dz); if (m >= 0) { mc[m]++; mn++; }
                    }
                for (int m = 0; m < 8; m++) if (mn > 0 && mc[m] >= mn * 0.04) mats++;
                // (the land around the flat spot: an island bigger than the build area, not a lone disc)
                int ringLand = 0;
                for (int k = 0; k < DIRS; k += 2)
                    for (double f = 0.45; f <= 0.76; f += 0.15)
                        if (md.HeightAtMap(cx + _dc[k] * R * f, cz + _ds[k] * R * f) > water + 0.5) ringLand++;
                double landFrac = ringLand / (DIRS / 2 * 3.0);
                if (landFrac < (pass > 0 ? 0.15 : 0.3)) continue;
                int veg = natural.Count(p => Sq(p.X - cx) + Sq(p.Y - cz) < R * R * 0.6) + 8 * forest.Count(p => Sq(p.X - cx) + Sq(p.Y - cz) < R * R * 0.6);
                double score = (walkable ? 0 : -0.5) + Math.Min(0, landFrac - 0.6) * 3 + 1.2 * wet / DIRS + 0.6 * (1 - (dw - rb) / (R * 0.8 - rb)) + 0.6 * (1 - range / 21) + 0.4 * Math.Min(1, veg / 30.0)
                    + 0.4 * Math.Min(1, (mats - 1) / 3.0) - pen / 8;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new StampInfo
                    {
                        Seat = site.Seat, SiteX = site.X, SiteZ = site.Z, SX = cx, SZ = cz, R = R, Mirror = mirror, Auto = true, Score = score,
                        // the patch's coast (direction ku) towards the land bridge (kv)
                        Rot = (((mirror ? kv + ku : kv - ku) % DIRS) + DIRS) % DIRS,
                    };
                }
            }
        return best;
    }

    /// <summary>the stamp of a site: the definition's (`stamp` with a centre: turned by `rot`, or so that its coast faces
    /// the land bridge) or an automatic one; null for `stamp = false` or when the map has no fitting patch</summary>
    static StampInfo? StampFor(MapData md, CoopSite site, double dirX, double dirZ, List<(double X, double Z)> used, int seed, int index)
    {
        if (site.NoStamp) return null;
        var sp = site.Stamp;
        if (sp?.X is double sx && sp.Z is double sz)
        {
            bool mirror = sp.Mirror ?? ((index + seed) & 1) == 1;
            int rot;
            if (sp.Rot is double deg) rot = (((int)Math.Round(deg * DIRS / 360.0) % DIRS) + DIRS) % DIRS;
            else
            {
                int ku = CoastDir(md, sx, sz, site.R * 0.5, sp.R * 0.9, out _, out _);
                int kv = DirIndex(dirX, dirZ);
                rot = ku < 0 ? 0 : (((mirror ? kv + ku : kv - ku) % DIRS) + DIRS) % DIRS;
            }
            return new StampInfo { Seat = site.Seat, SiteX = site.X, SiteZ = site.Z, SX = sx, SZ = sz, R = sp.R, Rot = rot, Mirror = mirror };
        }
        var a = AutoStamp(md, site, dirX, dirZ, used, seed, index);
        if (a != null && sp?.Rot is double d2) a.Rot = (((int)Math.Round(d2 * DIRS / 360.0) % DIRS) + DIRS) % DIRS;
        return a;
    }

    // ------------------------------------------------------------------------------------------------ the patch's content
    static double Slope(MapData o, double x, double y) => Math.Max(Math.Abs(o.HeightAtMap(x + 2, y) - o.HeightAtMap(x - 2, y)), Math.Abs(o.HeightAtMap(x, y + 2) - o.HeightAtMap(x, y - 2))) / 4;

    /// <summary>a quaternion [x, y, z, w] of the map turned by a stamp: mirrored (y -> -y), then turned by the stamp's
    /// angle about the map's z (the engine applies the conjugate: q' = q (x) (0, 0, -sin(a/2), cos(a/2)))</summary>
    static double[] TurnQuat(double[] q, StampInfo st)
    {
        double qx = q.Length > 3 ? q[0] : 0, qy = q.Length > 3 ? q[1] : 0, qz = q.Length > 3 ? q[2] : 0, qw = q.Length > 3 ? q[3] : 1;
        if (st.Mirror) { qx = -qx; qz = -qz; }
        var (s, c) = SinCos(st.Rot * Math.PI / DIRS);
        double bx = 0, by = 0, bz = -s, bw = c;
        return new[]
        {
            qw * bx + qx * bw + qy * bz - qz * by,
            qw * by - qx * bz + qy * bw + qz * bx,
            qw * bz + qx * by - qy * bx + qz * bw,
            qw * bw - qx * bx - qy * by - qz * bz,
        };
    }

    /// <summary>the stamps' objects (natural resources, decoration), plants and forest squares onto the new land `o`:
    /// where the ground is the patch's own (the stamp dominates, the height is unchanged), not on the old map's land,
    /// not on a land bridge's spine and - trees, stones, bushes, rocks - not on a base site's build area</summary>
    static void CopyStampContent(MapData md, MapData o, List<StampInfo> stamps, Func<double, double, (int K, double W)> dominant,
        Func<double, double, bool> oldLand, Func<double, double, double> spineDist, List<CoopSite> sites, MapExtensionInfo info)
    {
        int objs = 0, plants = 0, squares = 0;
        bool BuildArea(double x, double y) => sites.Any(s => Sq(s.X - x) + Sq(s.Z - y) < Sq(s.R * 0.8));
        for (int si = 0; si < stamps.Count; si++)
        {
            var st = stamps[si];
            // the ground there is the patch's: this stamp dominates and the height is as in the old map
            bool Ground(double sx, double sy, double tx, double ty, double minW, double tol)
            {
                var (k, w) = dominant(tx, ty);
                if (k != si || w < minW || oldLand(tx, ty)) return false;
                return Math.Abs(o.HeightAtMap(tx, ty) - md.HeightAtMap(sx, sy)) < tol;
            }
            bool Solid(double sx, double sy, double tx, double ty)
            {
                for (int k = 0; k < DIRS; k += 16)
                {
                    var (ax, ay) = st.ToTarget(sx + _dc[k] * 8, sy + _ds[k] * 8);
                    if (!Ground(sx + _dc[k] * 8, sy + _ds[k] * 8, ax, ay, 0.7, 0.9)) return false;
                }
                return true;
            }
            foreach (var ob in md.Objects)
            {
                if (!Copyable(ob) || Sq(ob.X - st.SX) + Sq(ob.Y - st.SZ) > st.R * st.R) continue;
                var (tx, ty) = st.ToTarget(ob.X, ob.Y);
                // (trees, stones, bushes and plants stand on whatever ground is there - a little changed is all right;
                // rocks and plateaus are shaped to their ground: it must be the patch's own)
                bool resource = ob.Type is "TREE" or "STON" or "FRUI", deco = !resource && ob.Type != "VGTN";
                if (!Ground(ob.X, ob.Y, tx, ty, resource ? 0.6 : 0.6, deco ? 0.7 : 50)) continue;
                if (ob.Type == "VGTN" && (o.HeightAtMap(tx, ty) < o.Water + 0.6 || Slope(o, tx, ty) > 0.8)) continue;
                if (resource && !ob.Cls.Contains("fish", StringComparison.OrdinalIgnoreCase) && (o.HeightAtMap(tx, ty) < o.Water + 0.8 || Slope(o, tx, ty) > 0.7)) continue;
                bool solid = resource || ob.Type == "DCCO" || SolidDeco(ob.Cls);
                bool fish = ob.Cls.Contains("fish", StringComparison.OrdinalIgnoreCase);
                if (!fish && solid && (BuildArea(tx, ty) || spineDist(tx, ty) < 4)) continue;
                if (!fish && spineDist(tx, ty) < 1) continue;
                if (ob.Type is "DCCO" or "DECO" && SolidDeco(ob.Cls) && !Solid(ob.X, ob.Y, tx, ty)) continue;
                double rot = st.Heading(ob.Rot);
                var n = new MapObject
                {
                    Type = ob.Type, Cls = ob.Cls, Name = $"coop_{ob.Cls}_{o.Objects.Count}", X = tx, Y = ty,
                    Z = ob.Z - md.HeightAtMap(ob.X, ob.Y) + o.HeightAtMap(tx, ty), Rot = rot, Q = TurnQuat(ob.Q, st), Owner = null,
                    Attr = new Dictionary<string, string>(ob.Attr),
                };
                o.Objects.Add(n);
                if (ob.Type is "TREE" or "STON" or "FRUI") info.Added.Add(n);
                objs++;
            }
            foreach (var p in md.Plants)
            {
                if (Sq(p.X - st.SX) + Sq(p.Y - st.SZ) > st.R * st.R) continue;
                string nm = p.Name.ToLowerInvariant();
                if (!NaturalName(p.Name)) continue;
                var (tx, ty) = st.ToTarget(p.X, p.Y);
                bool solid = SolidDeco(p.Name);
                if (!Ground(p.X, p.Y, tx, ty, solid ? 0.8 : 0.5, solid ? 0.6 : 50)) continue;
                double th = o.HeightAtMap(tx, ty), sh = md.HeightAtMap(p.X, p.Y);
                // (a plant of the dry land stays on dry land, one of the shore or under water stays there)
                if (!solid && (sh > md.Water + 1 ? th < o.Water + 0.6 || Slope(o, tx, ty) > 0.8 : Math.Abs(th - sh) > 1.2)) continue;
                if (spineDist(tx, ty) < (solid ? 6 : 1)) continue;
                if (solid && (BuildArea(tx, ty) || !Solid(p.X, p.Y, tx, ty))) continue;
                double rot = st.Heading(p.Rot);
                o.Plants.Add(new MapPlant { Name = p.Name, X = tx, Y = ty, Z = p.Z - md.HeightAtMap(p.X, p.Y) + o.HeightAtMap(tx, ty), Rot = rot, Q = TurnQuat(p.Q, st) });
                plants++;
            }
            // forest squares: each 32 m square of the new land whose centre comes from a forest square of the patch
            var src = new Dictionary<(int, int), ForestBlock>();
            foreach (var b in md.Forest.Blocks) src[(b.X, b.Y)] = b;
            if (src.Count > 0)
            {
                var have = new HashSet<(int, int)>(o.Forest.Blocks.Select(b => (b.X, b.Y)));
                int bx0 = (int)Math.Floor((st.SiteX - st.R) / 32), bx1 = (int)Math.Floor((st.SiteX + st.R) / 32), by0 = (int)Math.Floor((st.SiteZ - st.R) / 32), by1 = (int)Math.Floor((st.SiteZ + st.R) / 32);
                for (int by = by0; by <= by1; by++)
                    for (int bx = bx0; bx <= bx1; bx++)
                    {
                        if (have.Contains((bx, by))) continue;
                        double cx = bx * 32 + 16, cy = by * 32 + 16;
                        var (sx, sy) = st.ToSource(cx, cy);
                        if (!src.TryGetValue(((int)Math.Floor(sx / 32), (int)Math.Floor(sy / 32)), out var blk)) continue;
                        bool ok = true;
                        for (int b = 0; b <= 32 && ok; b += 16) for (int a = 0; a <= 32 && ok; a += 16)
                        {
                            double x = bx * 32 + a, y = by * 32 + b;
                            var (qx, qy) = st.ToSource(x, y);
                            if (o.HeightAtMap(x, y) < o.Water + 1.2 || Slope(o, x, y) > 0.8 || !Ground(qx, qy, x, y, 0.55, 5) || BuildArea(x, y) || spineDist(x, y) < 6) ok = false;
                        }
                        if (!ok) continue;
                        o.Forest.Blocks.Add(new ForestBlock { X = bx, Y = by, B = (byte[])blk.B.Clone() });
                        have.Add((bx, by));
                        squares++;
                    }
            }
        }
        info.Log.Add($"from the stamps: {objs} objects, {plants} plants, {squares} forest squares");
    }
}
