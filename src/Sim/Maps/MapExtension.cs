// Co-op maps: an original map extended at its edges, base sites made of the map's own land, land bridges to them, and
// resources added (docs/COOP.md "Maps").
//
//   MapExtension.Apply(md, plan) -> a new MapData (md stays as it was: single player never sees any of this)
//
// What the extension does, in the old map's map coordinates (they stay valid: OffX / OffY of the result are the new
// grids' corner, negative for land added to the north / west):
//   base       the old samples are kept (its 32 m frame at an extended edge - the editor's border - is made again); a
//              new sample continues the old edge's ground mirrored (its ridges, coves, shelf) and fades into the sea
//              over 40 .. 200 m (a wandering coast where the old map's land reaches its edge), the sea floor going over
//              into the old map's own open-sea depth
//   stamps     each base site in the new land or in the old map's open sea gets a patch of the old map (StampInfo,
//              MapExtension.Stamp.cs: chosen automatically or given by `stamp`): its heights relative to the water,
//              faded into the sea along a noise-bent, elliptical line (a beach where land fades); not within 28 .. 72 m
//              of the old map's land, not at the map's border; then the build area levelled (most of the relief out)
//   bridges    a land bridge's line is bent into a curve (the spine) and given a body of low land - an isthmus with
//              beaches and dunes whose width wanders and flares at its ends, or a narrow sandy ford -, then the spine
//              graded: the ground where gentle, ramps (cut a third, filled two thirds) where steeper than 0.3, the site's
//              level on its build area; it may cut `cut` m into higher ground (a pass)
//   shelf      the sea floor rises to the new coasts as it does round the old map's islands (its depth by distance
//              from the coast, measured on the old map)
//   materials  the old cells are kept unless their ground changed; a stamp's ground keeps the patch's materials; the
//              rest by what the old map uses its materials for (under water, shore, beach, flat land's three most used,
//              steep ground)
//   walk grid  (the editor's PaFi grid) kept; new cells and changed cells: dry and not steeper than 24 degrees; the old
//              edge's ground going on is walkable only where its mirror image in the old grid is (no new ways round)
//   content    the patches' own trees, stones, bushes, decoration (the setting's rocks, plateaus, shrubs - nothing
//              people built or a mission uses) and forest squares; forest squares and the map's plants in clusters on
//              the land no patch made, on levelled ground and (a little) on the patches
//   resources  CoopResource entries: trees (wood), stones, fruit bushes (food), fish shoals, "kit" (what a base still
//              needs beside what its land has), or objects of a named class - placed as map objects (TREE / STON /
//              FRUI) like the editor's, so MapSource makes them resources as any other
// Everything is seeded (extend.seed) and uses only +, -, *, /, sqrt and floor (sin / cos by series): the same map on every
// machine.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

/// <summary>what MapExtension.Apply did to a map (MapData.Extension)</summary>
public sealed class MapExtensionInfo
{
    public int North, South, East, West, Seed;
    /// <summary>the old map's grid rectangle in map coordinates (x0, y0, x1, y1 of its height samples' extent)</summary>
    public double OldX0, OldY0, OldX1, OldY1;
    public List<CoopSite> Sites = new();
    /// <summary>the land bridges as made: the curved spines (CoopBridge.Land: the width of the land around them)</summary>
    public List<CoopBridge> Bridges = new();
    /// <summary>style "grow": the paths Apply added to join cut-off parts of the grown land (MapExtension.Joins.cs; their
    /// spines are among Bridges)</summary>
    public List<CoopBridge> Joins = new();
    /// <summary>the patches of the old map the base sites' land was made from</summary>
    public List<StampInfo> Stamps = new();
    /// <summary>the objects added (resources), and lines saying what was done</summary>
    public List<MapObject> Added = new();
    public List<string> Log = new();
    /// <summary>is map point (mx, my) on the old map</summary>
    public bool InOld(double mx, double my) => mx >= OldX0 && mx <= OldX1 && my >= OldY0 && my <= OldY1;
}

public static partial class MapExtension
{
    static readonly Logger L = Log.For("Maps.Extension");
    /// <summary>width of the blend from the old edge into the generated land (m)</summary>
    public const double BLEND = 80;
    /// <summary>steepest walkable slope of the new cells (degrees, as tan: 24 degrees)</summary>
    public const double WALK_TAN = 0.445;
    /// <summary>height samples of the old map's border made again at an extended edge: its 32 m frame outside the
    /// playable area (the editor's border: often a step or a flat band, which would lie in the middle of the co-op map)</summary>
    public const int INSET = 16;

    static double S(double a, double b, double x) => Noise.Smoothstep(a, b, x);
    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>the map of a co-op game: `md` extended and given resources by the plan (a new MapData; md unchanged).
    /// Without anything to do it returns md itself.</summary>
    public static MapData Apply(MapData md, CoopMapPlan? plan)
    {
        if (plan == null || (plan.Empty && plan.Sites.Count == 0)) return md;
        var e = plan.Extend ?? new CoopExtend();
        var o = Extend(md, e, plan.Sites);
        // style "grow": a lobe cut off from the land it grows from (a strait between two lobes' beaches, a ridge where two
        // patches meet) gets a graded path to it and the map is made again with it (MapExtension.Joins.cs; at most three
        // rounds - each join can only bring the next part within reach)
        if (e.Grows && o.Walk != null && (e.Grow.Count > 0 || plan.Sites.Count > 0))
        {
            var joins = new List<CoopBridge>(); var jlog = new List<string>();
            for (int round = 0; round < 3; round++)
            {
                var add = Joins(md, o, e, plan.Sites, jlog);
                if (add.Count == 0) break;
                joins.AddRange(add);
                e = e.Merge(new CoopExtend { Bridges = add });
                o = Extend(md, e, plan.Sites);
            }
            o.Extension!.Joins = joins;
            o.Extension.Log.AddRange(jlog);
            foreach (var j in jlog) L.Info($"{md.File}: {j}");
        }
        if (plan.Resources.Count > 0) PlaceResources(o, plan.Resources, e.Seed, plan.Sites);
        return o;
    }

    /// <summary>Apply with the plain parts: the extension (null = none), resources (null = none), the co-op seats'
    /// starts (their base sites; null = none) and the seed is the extension's</summary>
    public static MapData Apply(MapData md, CoopExtend? ext, IEnumerable<CoopResource>? resources = null, IEnumerable<CoopSite>? sites = null) =>
        Apply(md, new CoopMapPlan { Extend = ext, Resources = resources?.ToList() ?? new(), Sites = sites?.ToList() ?? new() });

    // ------------------------------------------------------------------------------------------------ the extension
    static MapData Extend(MapData md, CoopExtend e, List<CoopSite> sites)
    {
        int Round(int v) => v <= 0 ? 0 : (v + 31) / 32 * 32;
        int n = Round(e.North), s = Round(e.South), w = Round(e.West), ea = Round(e.East);
        double water = md.Water, wn = e.Water ?? md.Water;
        int seed = e.Seed;
        var info = new MapExtensionInfo
        {
            North = n, South = s, East = ea, West = w, Seed = seed,
            OldX0 = md.OffX, OldY0 = md.OffY, OldX1 = md.OffX + (md.Hx - 1) * 2, OldY1 = md.OffY + (md.Hy - 1) * 2,
            Sites = sites.ToList(),
        };
        var o = Clone(md);
        o.Extension = info;
        int hx = md.Hx + (w + ea) / 2, hy = md.Hy + (n + s) / 2;
        int ox = md.OffX - w, oy = md.OffY - n;                       // the new grids' corner (map coordinates)
        o.Hx = hx; o.Hy = hy; o.OffX = ox; o.OffY = oy;
        o.W = md.W + w + ea; o.H = md.H + n + s;
        o.Preview = null;                                             // (the stored picture is the old map's)
        int di = w / 2, dj = n / 2;                                   // old sample (i, j) = new (i + di, j + dj)
        var OH = md.Heights;

        // ---- features the land is shaped around: the sites not on the old map's land (in the new land or in its open sea)
        var newSites = sites.Where(q => !q.Enemy && (!info.InOld(q.X, q.Z) || md.HeightAtMap(q.X, q.Z) < water - 0.5)).OrderBy(q => q.Seat).ToList();
        // (the enemy bases of the definition in new land: levelled like a base site, kept clear)
        var enemySites = sites.Where(q => q.Enemy && (!info.InOld(q.X, q.Z) || md.HeightAtMap(q.X, q.Z) < water - 0.5)).OrderBy(q => -q.Seat).ToList();
        var clearSites = newSites.Concat(enemySites).ToList();
        bool grow = e.Grows;
        var lands = e.Land.ToList();
        var bridges = Spines(e.Bridges, seed);
        info.Bridges = bridges;
        double SpineDist(double x, double y)
        {
            double best = 1e9;
            foreach (var b in bridges)
                for (int k = 0; k + 1 < b.Points.Count; k++) best = Math.Min(best, SegDist(x, y, b.Points[k], b.Points[k + 1]) - b.Width / 2);
            return best;
        }

        bool OnOldLand(double x, double y) => info.InOld(x, y) && md.HeightAtMap(x, y) >= water - 0.5;

        // ---- the stamps: each new base site's land is a patch of the old map (MapExtension.Stamp.cs)
        var stamps = new List<StampInfo>();
        var stamped = new HashSet<CoopSite>();
        // ("grow": the base sites and the `grow` lobes are lobes of one landmass, each a patch of the old map)
        var lobes = new List<CoopGrow>();
        int[] lobeStamp = Array.Empty<int>();
        if (grow)
        {
            lobes.AddRange(e.Grow);
            foreach (var site in newSites) lobes.Add(new CoopGrow { X = site.X, Z = site.Z, R = site.LandR, Stamp = site.Stamp, NoStamp = site.NoStamp, Seat = site.Seat });
            lobeStamp = new int[lobes.Count];
            var used = new List<(double X, double Z)>();
            // (the base sites' lobes choose first: they need a flat spot)
            var order = Enumerable.Range(0, lobes.Count).OrderBy(li => lobes[li].Seat > 0 ? 0 : 1).ThenBy(li => li).ToList();
            foreach (int li in order)
            {
                var l = lobes[li];
                var site = l.Seat > 0 ? newSites.First(q => q.Seat == l.Seat) : null;
                var st = GrowStamp(md, l, site?.R ?? 0, used, seed, li);
                lobeStamp[li] = -1;
                if (st == null) { info.Log.Add($"{l}: generated land (no stamp)"); continue; }
                lobeStamp[li] = stamps.Count; stamps.Add(st); used.Add((st.SX, st.SZ));
                info.Log.Add($"{l}: {st}");
            }
            foreach (var site in newSites) stamped.Add(site);
            info.Stamps = stamps;
        }
        else
        {
            var used = new List<(double X, double Z)>();
            int idx = 0;
            foreach (var site in newSites)
            {
                // the direction of its land bridge: the first bridge leaving the site, else towards the old map's middle
                var br = e.Bridges.FirstOrDefault(b => b.Points.Count >= 2 && Sq(b.Points[0].X - site.X) + Sq(b.Points[0].Z - site.Z) < Sq(site.R));
                double dx = br != null ? br.Points[1].X - br.Points[0].X : (info.OldX0 + info.OldX1) / 2 - site.X;
                double dz = br != null ? br.Points[1].Z - br.Points[0].Z : (info.OldY0 + info.OldY1) / 2 - site.Z;
                var st = StampFor(md, site, dx, dz, used, seed, idx++);
                if (st == null) { info.Log.Add($"seat {site.Seat}: generated land (no stamp)"); continue; }
                stamps.Add(st); stamped.Add(site); used.Add((st.SX, st.SZ));
                info.Log.Add(st.ToString());
            }
            info.Stamps = stamps;
        }
        var genSites = newSites.Where(q => !stamped.Contains(q)).ToList();
        double SiteDist(double x, double y)
        {
            double best = 1e9;
            foreach (var q in genSites) best = Math.Min(best, Math.Sqrt((q.X - x) * (q.X - x) + (q.Z - y) * (q.Z - y)) - q.R);
            return best;
        }
        double LandDist(double x, double y)
        {
            double best = 1e9;
            foreach (var q in lands) best = Math.Min(best, Math.Sqrt((q.X - x) * (q.X - x) + (q.Z - y) * (q.Z - y)) - q.R);
            return best;
        }

        // ---- heights: the old samples are kept; a new sample takes the old edge's height and blends into the sea floor
        // ("continue": into the edge's land going on), with the generated land of sites without a stamp and of `land`
        // the old map's outermost rows at an extended edge are often a step (the editor's border): the new land starts
        // INSET samples inside it - those outer rows (outside the playable area) are made again with the new land
        int ei0 = w > 0 ? INSET : 0, ej0 = n > 0 ? INSET : 0, ei1 = md.Hx - 1 - (ea > 0 ? INSET : 0), ej1 = md.Hy - 1 - (s > 0 ? INSET : 0);
        double OldAt(int i, int j) => OH[Math.Clamp(j, ej0, ej1) * md.Hx + Math.Clamp(i, ei0, ei1)];
        // the old edge's height at the edge sample (ci, cj), smoothed over +-16 m along the map's edge
        double EdgeSmooth(int ci, int cj)
        {
            double sum = 0; int c = 0;
            for (int b = -8; b <= 8; b += 2) for (int a = -8; a <= 8; a += 2) { sum += OldAt(ci + a, cj + b); c++; }
            return sum / c;
        }
        // (the old map's open sea floor: the mean of its samples deeper than 6 m)
        double deepSea;
        {
            double sum = 0; int cnt = 0;
            for (int j = 0; j < md.Hy; j += 4) for (int i = 0; i < md.Hx; i += 4) if (OH[j * md.Hx + i] < water - 6) { sum += OH[j * md.Hx + i]; cnt++; }
            deepSea = cnt > 0 ? sum / cnt : wn - 8;
        }
        var H = new float[hx * hy];
        var kept = new bool[hx * hy];
        // (land the base made from the old edge's ground going on: walkable only where its mirror image in the old map
        // is - no new ways round the old map's edge)
        var edgeLand = new bool[hx * hy];
        // (new sample (i, j) lies on the old map's grid: its old height is OH[(j - dj) * md.Hx + i - di])
        bool InOldGrid(int i, int j) => i - di >= 0 && j - dj >= 0 && i - di < md.Hx && j - dj < md.Hy;
        for (int j = 0; j < hy; j++)
            for (int i = 0; i < hx; i++)
            {
                int oi = i - di, oj = j - dj;
                double mx = ox + 2.0 * i, my = oy + 2.0 * j;
                if (oi >= ei0 && oj >= ej0 && oi <= ei1 && oj <= ej1) { H[j * hx + i] = OH[oj * md.Hx + oi]; kept[j * hx + i] = true; continue; }
                int ci = Math.Clamp(oi, ei0, ei1), cj = Math.Clamp(oj, ej0, ej1);
                double cx = md.OffX + 2.0 * ci, cy = md.OffY + 2.0 * cj;
                double d = Math.Sqrt((mx - cx) * (mx - cx) + (my - cy) * (my - cy));
                double e0 = OH[cj * md.Hx + ci], es = EdgeSmooth(ci, cj);
                // the edge's land, looked up at a point shifted along the edge by a noise (wavy coasts, not the
                // edge's columns drawn straight out)
                double jit = (Noise.Fbm(mx * 0.01 + 5.3, my * 0.01 - 2.1, seed + 23, 3) - 0.5) * 2 * Math.Min(60, d * 0.6) / 2;
                int ji = ci, jj = cj;
                if (oi < ei0 || oi > ei1) jj = Math.Clamp(cj + (int)jit, ej0, ej1);
                if (oj < ej0 || oj > ej1) ji = Math.Clamp(ci + (int)jit, ei0, ei1);
                double esj = EdgeSmooth(ji, jj);
                // the old edge's ground goes on mirrored (its ridges, valleys and coves, not a ramp drawn straight out),
                // fading into the sea over 40 .. 200 m - a wandering coast where the old map's land reaches its edge
                int mi = oi < ei0 ? 2 * ei0 - oi : oi > ei1 ? 2 * ei1 - oi : oi, mj = oj < ej0 ? 2 * ej0 - oj : oj > ej1 ? 2 * ej1 - oj : oj;
                double edge = OH[Math.Clamp(mj, 0, md.Hy - 1) * md.Hx + Math.Clamp(mi, 0, md.Hx - 1)];
                double fadeW = 40 + 160 * Math.Clamp((Noise.Fbm(mx * 0.004, my * 0.004, seed + 71, 3) - 0.3) * 2.5, 0, 1);
                double nz = Noise.Fbm(mx * 0.012, my * 0.012, seed + 3, 3) - 0.5;
                // ("continue": the edge's land reaches 60 .. 240 m out, then the sea - unless a site or `land`)
                double land = e.Style == "island" ? 0 : S(water - 1.5, water + 2.5, esj) * (1 - S(60 + 120 * Noise.Fbm(mx * 0.004, my * 0.004, seed + 29, 2), 240, d));
                if (e.Style != "island") land += nz * 0.8 * S(0, 96, d);
                // (and it ends in a coast before the new map's border, not cut off by it)
                {
                    double bd = Math.Min(Math.Min(mx - ox, ox + 2.0 * (hx - 1) - mx), Math.Min(my - oy, oy + 2.0 * (hy - 1) - my));
                    land *= S(24, 110, bd + nz * 40);
                }
                double sd = SiteDist(mx, my), ld = LandDist(mx, my);
                land = Math.Max(land, 1 - S(16, 60, ld + nz * 30));
                double flatK = S(0, 110, ld);
                double hill = Noise.Fbm(mx * 0.007, my * 0.007, seed + 11, 4);
                double hl = wn + 3.2 + hill * hill * 26 * flatK + 1.2 * Noise.Fbm(mx * 0.05, my * 0.05, seed + 5, 2);
                // ("continue": high ground at the edge - mountains, plateaus - goes on and comes down over ~250 m)
                if (e.Style != "island" && esj > hl) hl = Lerp(esj + (hill - 0.5) * 12, hl, Math.Min(1, S(0, 260, d) + (1 - flatK)));
                // (sea floor: next to the old edge, as deep as its water - or 4 .. 9 m where its edge is land -, going over
                // into the old map's open sea floor further out: the water looks the same, nothing drawn straight out)
                double sn = (Noise.Fbm(mx * 0.01, my * 0.01, seed + 7, 3) - 0.5) * 3;
                double hsNear = es < wn - 1 ? es + sn : wn - 3.5 - 5 * Noise.Fbm(mx * 0.01, my * 0.01, seed + 7, 3);
                double hs = Lerp(hsNear, Math.Min(hsNear, deepSea + sn), S(40, 220, d));
                double t = Lerp(hs, hl, S(0.2, 0.8, land));
                H[j * hx + i] = (float)Lerp(edge, t, S(0, e.Style == "island" ? fadeW : BLEND, d + nz * 30));
                edgeLand[j * hx + i] = H[j * hx + i] > wn - 0.4 && sd > 0 && ld > 0;
            }
        // (the ground before anything is put on it: the old map's, the old edge's going on, the sea floor)
        var HB = (float[])H.Clone();
        // ---- the old map's land (at or above the water, and the old edge's land going on): its distance (m), on a 4 m
        // grid over the whole new map - new land keeps away from it (no unplanned land connections; the land bridges are
        // the connections)
        int gw = (hx + 1) / 2, gh = (hy + 1) / 2;
        var LD = new float[gw * gh];
        for (int b = 0; b < gh; b++)
            for (int a = 0; a < gw; a++)
            {
                int k = Math.Min(hy - 1, 2 * b) * hx + Math.Min(hx - 1, 2 * a);
                bool land = (kept[k] || edgeLand[k]) && H[k] >= water - 0.5;
                LD[b * gw + a] = land ? 0 : 1e6f;
            }
        Chamfer(LD, gw, gh);
        double OldLandDist(double x, double y)
        {
            double fx = Math.Clamp((x - ox) / 4.0, 0, gw - 1.001), fy = Math.Clamp((y - oy) / 4.0, 0, gh - 1.001);
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fy); double u = fx - i, v = fy - j;
            return (LD[j * gw + i] * (1 - u) + LD[j * gw + i + 1] * u) * (1 - v) + (LD[(j + 1) * gw + i] * (1 - u) + LD[(j + 1) * gw + i + 1] * u) * v;
        }
        double AtH(double x, double y)
        {
            double fx = Math.Clamp((x - ox) / 2.0, 0, hx - 1.001), fy = Math.Clamp((y - oy) / 2.0, 0, hy - 1.001);
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fy); double u = fx - i, v = fy - j;
            return (H[j * hx + i] * (1 - u) + H[j * hx + i + 1] * u) * (1 - v) + (H[(j + 1) * hx + i] * (1 - u) + H[(j + 1) * hx + i + 1] * u) * v;
        }
        double X1 = ox + 2.0 * (hx - 1), Y1 = oy + 2.0 * (hy - 1);
        double Border(double x, double y) => Math.Min(Math.Min(x - ox, X1 - x), Math.Min(y - oy, Y1 - y));
        // the sample range of a box around (x, y) (radius r)
        (int I0, int I1, int J0, int J1) Box(double x0, double y0, double x1, double y1) =>
            (Math.Max(0, (int)Math.Floor((x0 - ox) / 2)), Math.Min(hx - 1, (int)Math.Ceiling((x1 - ox) / 2)), Math.Max(0, (int)Math.Floor((y0 - oy) / 2)), Math.Min(hy - 1, (int)Math.Ceiling((y1 - oy) / 2)));

        // ---- the stamps' heights: the patch relative to the water, fading into the sea along a noise-bent line (a beach
        // where land fades: the land comes down to the water level, then the sea floor); not near the old map's land
        // (28 .. 72 m: the land bridges connect), not at the new map's border, in the old map only over its sea
        // (each stamp's ellipse: the short axis along its land bridge's direction, 0.72 .. 0.92 of the long one)
        var domK = new sbyte[hx * hy]; Array.Fill(domK, (sbyte)-1);
        var domW = new float[hx * hy];
        // (the grown land's weight per sample: its walk cells are judged as new land, not by the old edge's mirror image)
        var grown = new float[hx * hy];
        // (the join's distance from the old land and the old map's protection, per sample: GrowLand)
        var joinD = new float[hx * hy]; Array.Fill(joinD, 1e6f);
        var freeA = new float[hx * hy]; Array.Fill(freeA, 1f);
        if (grow && lobes.Count > 0) GrowLand(md, H, HB, kept, edgeLand, hx, hy, ox, oy, wn, seed, lobes, lobeStamp, stamps, domK, domW, e.Keep, info, grown, joinD, freeA);
        var eAx = new double[stamps.Count]; var eAz = new double[stamps.Count]; var eRatio = new double[stamps.Count];
        for (int si = 0; si < stamps.Count; si++)
        {
            var st = stamps[si];
            var br = e.Bridges.FirstOrDefault(b => b.Points.Count >= 2 && Sq(b.Points[0].X - st.SiteX) + Sq(b.Points[0].Z - st.SiteZ) < 4);
            double bx = br != null ? br.Points[1].X - br.Points[0].X : 1, bz = br != null ? br.Points[1].Z - br.Points[0].Z : 0, bl = Math.Max(1e-6, Math.Sqrt(bx * bx + bz * bz));
            // (long axis: across the bridge)
            eAx[si] = -bz / bl; eAz[si] = bx / bl;
            eRatio[si] = 0.72 + 0.2 * Noise.Fbm(st.SX * 0.01, st.SZ * 0.01, seed + 73 + si, 2);
        }
        for (int si = 0; si < (grow ? 0 : stamps.Count); si++)
        {
            var st = stamps[si];
            var (i0, i1, j0, j1) = Box(st.SiteX - st.R - 2, st.SiteZ - st.R - 2, st.SiteX + st.R + 2, st.SiteZ + st.R + 2);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    double mx = ox + 2.0 * i, my = oy + 2.0 * j;
                    var (qx, qz) = st.ToSource(mx, my);
                    double d = Math.Sqrt(Sq(qx - st.SX) + Sq(qz - st.SZ));
                    if (d > st.R) continue;
                    // (the fade's line wanders by up to +-45 m - bays and capes, not a circle - and ends inside the patch)
                    double nz = Math.Clamp((Noise.Fbm(mx * 0.006, my * 0.006, seed + 41 + si * 13, 3) - 0.5) * 4, -0.8, 0.8)
                        + (Noise.Fbm(mx * 0.02, my * 0.02, seed + 43 + si * 13, 2) - 0.5) * 0.6;
                    // (and the island is longer one way than the other: an ellipse, its long axis across the land bridge)
                    double ex = mx - st.SiteX, ez = my - st.SiteZ, ea1 = ex * eAx[si] + ez * eAz[si], ea2 = -ex * eAz[si] + ez * eAx[si];
                    double de = Math.Sqrt(ea1 * ea1 + Sq(ea2 / eRatio[si]));
                    double wt = 1 - S(st.R * 0.4, st.R * 0.95, Math.Min(st.R, de) + nz * st.R * 0.4 * (1 - S(st.R * 0.7, st.R, d)));
                    wt *= 1 - S(st.R * 0.9, st.R, d);
                    wt *= S(16, 64, Border(mx, my) + nz * 30);
                    wt *= S(28, 72, OldLandDist(mx, my) + nz * 25);
                    // (only the old map's playable ground: its 32 m frame - the editor's border - is no landscape)
                    double se = Math.Min(Math.Min(qx - md.OffX, md.OffX + (md.Hx - 1) * 2 - qx), Math.Min(qz - md.OffY, md.OffY + (md.Hy - 1) * 2 - qz));
                    wt *= S(36, 80, se + nz * 25);
                    int k = j * hx + i;
                    wt *= 1 - S(water - 1.5, water - 0.3, HB[j * hx + i]);
                    if (wt <= 0.001) continue;
                    if (wt > domW[k]) { domW[k] = (float)wt; domK[k] = (sbyte)si; }
                    double src = md.HeightAtMap(qx, qz) - water + wn, cur = H[k];
                    // (land rises late - a beach where it fades -, the sea floor follows early; continuous at the water line)
                    double g = S(0, 0.45, wt), rel = src - wn;
                    double f = wn + (rel > 0 ? rel * S(0.3, 1, wt) : rel * g) + (cur - wn) * (1 - g);
                    if (f > cur) H[k] = (float)f;
                }
        }

        // (a site without a stamp: an irregular island of the setting's gentle hills, faded as a stamp is)
        foreach (var site in genSites)
        {
            double R = 140;
            var (i0, i1, j0, j1) = Box(site.X - R - 2, site.Z - R - 2, site.X + R + 2, site.Z + R + 2);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    double mx = ox + 2.0 * i, my = oy + 2.0 * j;
                    double d = Math.Sqrt(Sq(mx - site.X) + Sq(my - site.Z));
                    if (d > R) continue;
                    double nz = Math.Clamp((Noise.Fbm(mx * 0.006, my * 0.006, seed + 41 + site.Seat * 29, 3) - 0.5) * 4, -0.8, 0.8)
                        + (Noise.Fbm(mx * 0.02, my * 0.02, seed + 43, 2) - 0.5) * 0.6;
                    double wt = 1 - S(R * 0.4, R * 0.95, d + nz * R * 0.4 * (1 - S(R * 0.7, R, d)));
                    wt *= S(16, 64, Border(mx, my) + nz * 30) * S(28, 72, OldLandDist(mx, my) + nz * 25);
                    wt *= 1 - S(water - 1.5, water - 0.3, HB[j * hx + i]);
                    if (wt <= 0.001) continue;
                    double hill = Noise.Fbm(mx * 0.007, my * 0.007, seed + 11, 4);
                    double src = wn + 3 + hill * hill * 22 * S(site.R * 0.6, site.R + 60, d) + 1.2 * Noise.Fbm(mx * 0.05, my * 0.05, seed + 5, 2);
                    int k = j * hx + i;
                    double cur = H[k], g = S(0, 0.45, wt), rel = src - wn;
                    double f = wn + rel * S(0.3, 1, wt) + (cur - wn) * (1 - g);
                    if (f > cur) H[k] = (float)f;
                }
        }
        // (the heights before the sites are levelled and the bridges graded: the joins' smoothing below looks for what
        // those changed)
        var Hg = (float[])H.Clone();
        // ---- the base sites: ground to build on - most of the patch's relief taken out over the site's radius (a little
        // kept), fading out over 36 m
        // (the distance from the water before the levelling, on a 4 m grid: land next to a coast is not raised to the
        // build area's level - the coast keeps its beach instead of becoming a wall)
        var CD = new float[gw * gh];
        if (clearSites.Count > 0)
        {
            for (int b = 0; b < gh; b++)
                for (int a = 0; a < gw; a++)
                    CD[b * gw + a] = H[Math.Min(hy - 1, 2 * b) * hx + Math.Min(hx - 1, 2 * a)] > wn ? 1e6f : 0;
            Chamfer(CD, gw, gh);
        }
        foreach (var site in clearSites)
        {
            double r = site.R, sum = 0; int cnt = 0;
            var (i0, i1, j0, j1) = Box(site.X - r - 40, site.Z - r - 40, site.X + r + 40, site.Z + r + 40);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    if (Sq(ox + 2.0 * i - site.X) + Sq(oy + 2.0 * j - site.Z) <= r * r) { sum += H[j * hx + i]; cnt++; }
            if (cnt == 0) continue;
            double mean = Math.Max(sum / cnt, wn + 2.5);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int k = j * hx + i;
                    if ((kept[k] || !grow) && HB[k] >= water - 0.5) continue;
                    double mx = ox + 2.0 * i, my = oy + 2.0 * j;
                    double d = Math.Sqrt(Sq(mx - site.X) + Sq(my - site.Z)) + (Noise.Fbm(mx * 0.04, my * 0.04, seed + 45, 2) - 0.5) * 24;
                    // (land is levelled out to r + 36 m; the sea is raised only under the build area itself: no round island;
                    // between them by the ground's height - continuous: no cliff where low land next to the water was
                    // raised and the water beside it not)
                    double coast = CD[Math.Min(gh - 1, (j + 1) / 2) * gw + Math.Min(gw - 1, (i + 1) / 2)];
                    double kLand = (1 - S(r * 0.7, r + 36, d)) * 0.95 * Lerp(S(4, 40, coast), 1, 1 - S(r * 0.9, r + 12, d));
                    double kk = Lerp(1 - S(r * 0.85, r + 8, d), kLand, S(wn + 0.2, wn + 2.2, H[k]));
                    // (grown land: outside the build area itself the join's heights win towards the old land - the levelled
                    // ground comes down to the old coast's own instead of meeting it in a step)
                    if (grow) kk *= Lerp(S(0, 32, joinD[k]), 1, 1 - S(r * 0.9, r + 12, d));
                    if (kk > 0) H[k] = (float)(H[k] + (mean - H[k]) * kk);
                }
        }

        // ---- land bridges: the spine's heights (the ground at its ends, an isthmus' crest between: 1.8 .. 3.4 m above
        // the water - a ford's 0.9 m -, smoothed and not steeper than 0.3), the land around it (an isthmus: a wide, low
        // body with beaches and dunes whose width and edges wander; in the old map only over its water and shore), then
        // the spine graded to its line: cut at most `cut` m into higher ground (a pass through a coast's cliffs)
        var ph = new List<double[]>(); var cum = new List<double[]>();
        for (int bi = 0; bi < bridges.Count; bi++)
        {
            var P = bridges[bi].Points; int np = P.Count;
            var a = new double[np]; var c = new double[np];
            for (int k = 0; k < np; k++)
            {
                if (k > 0) c[k] = c[k - 1] + Math.Sqrt(Sq(P[k].X - P[k - 1].X) + Sq(P[k].Z - P[k - 1].Z));
                double t = AtH(P[k].X, P[k].Z);
                if (k == 0) a[k] = Math.Max(wn + 1, t);
                else if (k >= np - 2) a[k] = Math.Max(Math.Max(wn, water) + 1, AtH(P[np - 2].X, P[np - 2].Z));
                else if (bridges[bi].Path) a[k] = Math.Max(t, wn + 1.2);
                else a[k] = Math.Max(t, wn + (bridges[bi].Ford ? 0.9 : 2.2 + 1.6 * Noise.Fbm(c[k] / 60.0 + bi * 5.1, 0.7, seed + 47, 2)));
            }
            // (the ground itself where it is gentle enough; steps steeper than 0.3 become ramps: a third cut into the
            // higher ground, two thirds filled up below it - the highest ramp under the ground and the lowest over it,
            // mixed)
            // (on a base site's build area the spine keeps the site's level: the ramps begin beyond it)
            var home = newSites.FirstOrDefault(q => Sq(q.X - P[0].X) + Sq(q.Z - P[0].Z) < 1);
            double pin = home != null ? home.R : 0;
            for (int k = 1; k < np && c[k] < pin; k++) a[k] = a[0];
            var cutL = (double[])a.Clone(); var fillL = (double[])a.Clone();
            for (int k = 1; k < np; k++) { if (c[k] < pin) continue; double ds = 0.3 * (c[k] - c[k - 1]); cutL[k] = Math.Min(cutL[k], cutL[k - 1] + ds); fillL[k] = Math.Max(fillL[k], fillL[k - 1] - ds); }
            for (int k = np - 2; k >= 0; k--) { if (c[k] < pin) continue; double ds = 0.3 * (c[k + 1] - c[k]); cutL[k] = Math.Min(cutL[k], cutL[k + 1] + ds); fillL[k] = Math.Max(fillL[k], fillL[k + 1] - ds); }
            for (int k = 0; k < np; k++) a[k] = 0.35 * cutL[k] + 0.65 * fillL[k];
            // (and no step where the ramp leaves the site's level: both ways not steeper than 0.3)
            for (int pass = 0; pass < 2; pass++)
            {
                for (int k = 1; k < np; k++) { if (c[k] < pin) continue; double ds = 0.3 * (c[k] - c[k - 1]); a[k] = Math.Clamp(a[k], a[k - 1] - ds, a[k - 1] + ds); }
                for (int k = np - 2; k >= 0; k--) { if (c[k] < pin) continue; double ds = 0.3 * (c[k + 1] - c[k]); a[k] = Math.Clamp(a[k], a[k + 1] - ds, a[k + 1] + ds); }
            }
            ph.Add(a); cum.Add(c);
        }
        // the nearest point of bridge b's spine: distance from its centre line, position along it, the spine's height
        double Nearest(int b, double x, double y, out double along, out double target)
        {
            var P = bridges[b].Points; double best = 1e9; along = 0; target = 0;
            for (int k = 0; k + 1 < P.Count; k++)
            {
                double dx = P[k + 1].X - P[k].X, dy = P[k + 1].Z - P[k].Z, l2 = dx * dx + dy * dy;
                double t = l2 > 0 ? Math.Clamp(((x - P[k].X) * dx + (y - P[k].Z) * dy) / l2, 0, 1) : 0;
                double d = Math.Sqrt(Sq(P[k].X + t * dx - x) + Sq(P[k].Z + t * dy - y));
                if (d < best) { best = d; along = cum[b][k] + t * (cum[b][k + 1] - cum[b][k]); target = ph[b][k] + (ph[b][k + 1] - ph[b][k]) * t; }
            }
            return best;
        }
        for (int bi = 0; bi < bridges.Count; bi++)
        {
            var br = bridges[bi]; var P = br.Points;
            // (a path through grown land has no body of its own: only its spine is graded)
            if (br.Path) continue;
            double reach = br.Land / 2 * 2.6 + 12;
            var (i0, i1, j0, j1) = Box(P.Min(q => q.X) - reach, P.Min(q => q.Z) - reach, P.Max(q => q.X) + reach, P.Max(q => q.Z) + reach);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    double mx = ox + 2.0 * i, my = oy + 2.0 * j;
                    double d = Nearest(bi, mx, my, out double along, out double crest);
                    if (d > reach) continue;
                    // (its width wanders and flares out where it meets the land at its ends)
                    double Lb = cum[bi][cum[bi].Length - 1], endD = Math.Min(along, Lb - along);
                    double halfW = br.Land / 2 * (0.55 + 0.9 * Noise.Fbm(along / 60.0 + bi * 3.3, 1.9, seed + 53, 2)) * (1 + 0.7 * (1 - S(0, 70, endD)));
                    double en = (Noise.Fbm(mx * 0.035, my * 0.035, seed + 57, 3) - 0.5) * (br.Ford ? 6 : 18);
                    double p = 1 - S(halfW * 0.35, halfW, d + en);
                    if (p <= 0) continue;
                    double dune = br.Ford ? 0 : (Noise.Fbm(mx * 0.03, my * 0.03, seed + 59, 3) - 0.5) * 4 * S(0.4, 1, p);
                    // (the spine's height only along the spine itself - where it climbs to a high landing; the body beside
                    // it comes down at 0.5 to the isthmus' low land: an embankment, not a plateau ending in a wall where
                    // it meets the old shore)
                    double low = br.Ford ? 0.9 : 2.2 + 1.2 * Noise.Fbm(along / 60.0 + bi * 5.1, 0.7, seed + 47, 2);
                    double rel = Math.Max(0.6, Math.Min(crest - wn, Math.Max(low, crest - wn - 0.5 * Math.Max(0, d - br.Width / 2))) + dune);
                    int k = j * hx + i;
                    double cur = H[k];
                    double f = wn + rel * S(0.3, 1, p) + (cur - wn) * (1 - S(0, 0.4, p));
                    f = cur + (f - cur) * (1 - S(water + 0.3, water + 2.0, HB[k]));
                    if (f > cur) H[k] = (float)f;
                }
        }
        if (bridges.Count > 0)
        {
            var H0 = (float[])H.Clone();
            for (int bi = 0; bi < bridges.Count; bi++)
            {
                var P = bridges[bi].Points; double cut = bridges[bi].Cut, half = bridges[bi].Width / 2;
                // (how far beyond the spine's edge the grading may reach: a path's through the grown land's old ground
                // only its strip, an isthmus' and a ford's their land; the walls of a cut and the slopes of a fill come
                // down at 0.6 - steeper only where `cut` needs it to stay within that -, their edges bent by noise:
                // no straight walls along the spine)
                double reach = bridges[bi].Path ? 16 : bridges[bi].Ford ? 18 : 34;
                double wall = Math.Max(0.6, cut / (reach - 2));
                var (i0, i1, j0, j1) = Box(P.Min(q => q.X) - half - reach - 8, P.Min(q => q.Z) - half - reach - 8, P.Max(q => q.X) + half + reach + 8, P.Max(q => q.Z) + half + reach + 8);
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        double mx = ox + 2.0 * i, my = oy + 2.0 * j;
                        double best = Nearest(bi, mx, my, out _, out double target) - half;
                        if (best > reach + 4) continue;
                        // (where two bridges' strips meet, the nearer one grades)
                        bool other = false;
                        // (only where that one grades too: a sample nearer to another bridge but outside its strip is still
                        // this one's - else it was left as it was, a step beside two strips)
                        for (int b2 = 0; b2 < bridges.Count && !other; b2++)
                        {
                            if (b2 == bi) continue;
                            double d2 = Nearest(b2, mx, my, out _, out _) - bridges[b2].Width / 2;
                            if (d2 < best && d2 <= (bridges[b2].Path ? 16 : bridges[b2].Ford ? 18 : 34) + 4) other = true;
                        }
                        if (other) continue;
                        double nz = Noise.Fbm(mx * 0.05, my * 0.05, seed + 31, 2) - 0.5;
                        int k = j * hx + i;
                        double h0 = H0[k], bd = Math.Max(0, best + nz * 8);
                        // (cut: down to the spine's height at most `cut` m, rising at `wall` from its edge; fill: up to it,
                        // falling at `wall` - both ways reaching the ground before within `reach`)
                        double sw = Math.Max(h0 < target ? 0.6 : wall, Math.Abs(h0 - target) / reach);
                        double h = h0 < target ? Math.Max(h0, target - sw * bd) : Math.Max(h0 - cut, Math.Min(h0, target + sw * bd));
                        if (Math.Abs(h - h0) < 1e-4) continue;
                        H[k] = (float)h;
                    }
            }
        }
        // ---- the joins smoothed: where new or changed land meets ground it may not change (the old map's land, the
        // spines) and where levelling and grading met the land around (a build area's rim, a pass' walls, the old edge's
        // remade frame), steps and creases are spread out: around every steep or sharply bent spot of such ground, 16 m
        // of the movable samples are relaxed (12 passes of a 3 x 3 mean, ~6 m wide), the land staying land and the water
        // as it is (no new coast, no new way, no shallower sea lane), never dug below the ground before, the protected old map and the keep zones
        // left as they are
        SmoothJoins(H, HB, Hg, kept, freeA, joinD, hx, hy, ox, oy, water, wn, grow, e.Keep, SpineDist, info);

        // ---- the new land's shelf: the sea floor rises towards its coasts (shallow, light water around it), as wide as the
        // old map's own shelf (how far from its coasts the water stays shallow); in the old map only over its sea
        {
            // (the old map's mean depth by distance from its land, in 20 m rings: the new coasts' sea floor follows it)
            var prof = new double[9];
            {
                var sum = new double[8]; var cnt = new int[8];
                for (int j = 0; j < md.Hy; j += 3)
                    for (int i = 0; i < md.Hx; i += 3)
                    {
                        double h = OH[j * md.Hx + i];
                        if (h >= water - 0.3) continue;
                        int ring = (int)(LD[Math.Min(gh - 1, (j + dj + 1) / 2) * gw + Math.Min(gw - 1, (i + di + 1) / 2)] / 20);
                        if (ring < 8) { sum[ring] += water - h; cnt[ring]++; }
                    }
                prof[0] = 0.8;
                for (int r = 0; r < 8; r++) prof[r + 1] = Math.Max(prof[r], cnt[r] > 0 ? sum[r] / cnt[r] : prof[r]);
                info.Log.Add($"shelf depths {string.Join(" ", prof.Select(v => v.ToString("0.0")))}");
            }
            double Depth(double d)
            {
                // (prof[0] at the coast, prof[r + 1] at the ring's middle: 10, 30, 50 ... m)
                if (d <= 0) return prof[0];
                double f = (d + 10) / 20; int r = (int)Math.Floor(f);
                if (r >= 8) return prof[8] + (d - 150) * 0.25;           // (then down below any sea floor: no edge)
                return r == 0 ? prof[0] + (prof[1] - prof[0]) * d / 10 : prof[r] + (prof[r + 1] - prof[r]) * (f - r);
            }
            var ND = new float[gw * gh];
            for (int b = 0; b < gh; b++)
                for (int a = 0; a < gw; a++)
                {
                    int i = Math.Min(hx - 1, 2 * a), j = Math.Min(hy - 1, 2 * b), k = j * hx + i;
                    bool land = H[k] > wn && Math.Abs(H[k] - HB[k]) > 0.5;
                    ND[b * gw + a] = land ? 0 : 1e6f;
                }
            Chamfer(ND, gw, gh);
            for (int j = 0; j < hy; j++)
                for (int i = 0; i < hx; i++)
                {
                    int k = j * hx + i;
                    if (H[k] >= wn - 0.8) continue;
                    float dd = ND[Math.Min(gh - 1, (j + 1) / 2) * gw + Math.Min(gw - 1, (i + 1) / 2)];
                    if (dd > 240) continue;
                    double mx = ox + 2.0 * i, my = oy + 2.0 * j;
                    // (a keep zone's water stays as deep as it was: a channel, a ship's way)
                    if (e.Keep.Any(z => Sq(mx - z.X) + Sq(my - z.Z) < Sq(z.R + 24))) continue;
                    double shelf = wn - Math.Max(0.8, Depth(dd + (Noise.Fbm(mx * 0.02, my * 0.02, seed + 67, 3) - 0.5) * 50));
                    shelf = H[k] + (shelf - H[k]) * (1 - S(water - 1.5, water - 0.3, HB[k]));
                    if (shelf > H[k]) H[k] = (float)shelf;
                }
        }
        o.Heights = H;
        // the old samples whose ground changed (the edge rows, the land bridges, new land over the old map's sea)
        bool[] changedOld = new bool[md.Hx * md.Hy];
        for (int oj = 0; oj < md.Hy; oj++)
            for (int oi = 0; oi < md.Hx; oi++)
                {
                    double a = H[(oj + dj) * hx + oi + di], b = OH[oj * md.Hx + oi];
                    // (the sea floor raised a little under the water - a shelf - keeps the old ground's material)
                    bool edgeRow = oi < ei0 || oj < ej0 || oi > ei1 || oj > ej1;
                    if (Math.Abs(a - b) > 0.5 || (a >= water - 0.4) != (b >= water - 0.4) || edgeRow && Math.Abs(a - b) > 1e-4) changedOld[oj * md.Hx + oi] = true;
                }

        // a land bridge's way is clear, and nothing of the old map stands where the ground rose or sank: the old map's
        // trees, stones, bushes, plants and forest squares on a spine or on changed ground go
        {
            int before = o.Objects.Count + o.Plants.Count + o.Forest.Blocks.Count;
            bool Moved(double x, double y) => info.InOld(x, y) && Math.Abs(o.HeightAtMap(x, y) - md.HeightAtMap(x, y)) > 0.5;
            o.Objects.RemoveAll(q => q.Owner == null && (q.Type is "TREE" or "STON" or "FRUI" && !q.Cls.Contains("fish", StringComparison.OrdinalIgnoreCase) && SpineDist(q.X, q.Y) < 3
                || q.Type is "TREE" or "STON" or "FRUI" or "VGTN" or "DECO" or "DCCO" && Moved(q.X, q.Y)));
            // (solid pieces - rocks, cliffs - further out too: their footprints reach in)
            o.Plants.RemoveAll(q => SpineDist(q.X, q.Y) < (MapSource.SOLID.IsMatch(q.Name.ToLowerInvariant()) ? 14 : 2) || Moved(q.X, q.Y));
            o.Objects.RemoveAll(q => q.Type is "DCCO" or "DECO" && q.Owner == null && MapSource.SOLID.IsMatch(q.Cls.ToLowerInvariant()) && SpineDist(q.X, q.Y) < 14);
            o.Forest.Blocks.RemoveAll(q => SpineDist(q.X * 32 + 16, q.Y * 32 + 16) < 18 || Moved(q.X * 32 + 4, q.Y * 32 + 4) || Moved(q.X * 32 + 28, q.Y * 32 + 4) || Moved(q.X * 32 + 4, q.Y * 32 + 28) || Moved(q.X * 32 + 28, q.Y * 32 + 28) || Moved(q.X * 32 + 16, q.Y * 32 + 16));
            info.Log.Add($"cleared on the bridges and changed ground {before - o.Objects.Count - o.Plants.Count - o.Forest.Blocks.Count}");
        }
        // what of the old map stays where its ground moved a little (up to 0.5 m: a beach risen under a lobe, a smoothed
        // join, the remade frame) keeps standing on its ground: the map gives every object and plant its height (Z, the
        // editor's; MapSource: MapDecor.Y), so a reed, a rock or a flight of stairs would otherwise sink into risen ground
        // or float over lowered ground (mission 15: the SEAS fortress' stairs 0.2 m into the ground). Copies - the original
        // MapData stays as it is.
        {
            int moved = 0;
            double Delta(double x, double y) => info.InOld(x, y) ? o.HeightAtMap(x, y) - md.HeightAtMap(x, y) : 0;
            // (only the old map's own: a stamp's copies already stand on their new ground)
            var oldObj = new HashSet<MapObject>(md.Objects, ReferenceEqualityComparer.Instance);
            var oldPlant = new HashSet<MapPlant>(md.Plants, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < o.Objects.Count; i++)
            {
                var q = o.Objects[i];
                if (!oldObj.Contains(q)) continue;
                double dh = Delta(q.X, q.Y);
                if (Math.Abs(dh) < 0.01) continue;
                o.Objects[i] = new MapObject { Type = q.Type, Name = q.Name, Cls = q.Cls, X = q.X, Y = q.Y, Z = q.Z + dh, Rot = q.Rot, Q = q.Q, Owner = q.Owner, At = q.At, Level = q.Level, Attr = q.Attr };
                moved++;
            }
            for (int i = 0; i < o.Plants.Count; i++)
            {
                var q = o.Plants[i];
                if (!oldPlant.Contains(q)) continue;
                double dh = Delta(q.X, q.Y);
                if (Math.Abs(dh) < 0.01) continue;
                o.Plants[i] = new MapPlant { Name = q.Name, X = q.X, Y = q.Y, Z = q.Z + dh, Rot = q.Rot, Q = q.Q };
                moved++;
            }
            if (moved > 0) info.Log.Add($"objects and plants of the old map on its moved ground: {moved}, kept on it");
        }
        (int K, double W) Dominant(double x, double y)
        {
            int i = Math.Clamp((int)Math.Floor((x - ox) / 2 + 0.5), 0, hx - 1), j = Math.Clamp((int)Math.Floor((y - oy) / 2 + 0.5), 0, hy - 1);
            return (domK[j * hx + i], domW[j * hx + i]);
        }

        // ---- ground materials: the old cells are kept unless their ground changed; a stamp's ground is the patch's own
        // (where the ground is still the patch's: not a beach the fade made, not a steep material flattened); the rest by
        // what the old map uses its materials for (under water, shore, the flat land's mix, steep ground)
        var (mUnder, mShore, mLand, mLand2, mSteep) = MaterialRoles(md);
        byte mLand3 = ThirdFlat(md, mLand, mLand2), mBeach = Beach(md, mShore);
        // (the ground follows the final slope as on the original maps: a rock material - one the old map keeps for steep
        // ground - only from the slope the old map uses it from (RockSlopes; the patches' rock left on levelled, lowered
        // or smoothed ground becomes the flat land's mix), the steep material where the ground is as steep as the old
        // map's rock typically is; the limits dithered per cell (noise) so the borders are soft, not hard-edged blobs)
        var rockFrom = RockSlopes(md);
        double steepFrom = double.IsInfinity(rockFrom[mSteep]) ? double.PositiveInfinity : Math.Clamp(GroundUse(md)[mSteep].P50, 0.5, 0.8);
        // (the flat land's mix: its three most used materials on flat ground - not a rock one, though a map that paints
        // much of its rock on flat tops counts it among them)
        bool Rock(byte m) => !double.IsInfinity(rockFrom[m & 7]);
        byte flat1 = new[] { mLand, mLand2, mLand3 }.FirstOrDefault(m => !Rock(m), mLand);
        byte F2 = Rock(mLand2) ? flat1 : mLand2, F3 = Rock(mLand3) ? flat1 : mLand3;
        byte FlatMix(double x, double y)
        {
            double f = Noise.Fbm(x * 0.03, y * 0.03, seed + 13, 3);
            return f > 0.6 ? F2 : f < 0.36 && F3 != flat1 ? F3 : flat1;
        }
        int mxn = md.Mx + (w + ea) / 4, myn = md.My + (n + s) / 4, dmi = w / 4, dmj = n / 4;
        var M = new byte[mxn * myn];
        for (int j = 0; j < myn; j++)
            for (int i = 0; i < mxn; i++)
            {
                int oi = i - dmi, oj = j - dmj;
                bool inOld = oi >= 0 && oj >= 0 && oi < md.Mx && oj < md.My && md.Mats.Length > 0;
                // a cell of the old map keeps its material unless its ground changed
                bool changed = false;
                if (inOld)
                {
                    for (int b = 0; b <= 2 && !changed; b++) for (int a = 0; a <= 2 && !changed; a++)
                    {
                        int si = oi * 2 + a, sj = oj * 2 + b;
                        if (si < md.Hx && sj < md.Hy && changedOld[sj * md.Hx + si]) changed = true;
                    }
                    if (!changed) { M[j * mxn + i] = md.Mats[oj * md.Mx + oi]; continue; }
                }
                double mx = ox + 4.0 * i + 2, my = oy + 4.0 * j + 2;
                double h = o.HeightAtMap(mx, my);
                double sl = Math.Max(Math.Abs(o.HeightAtMap(mx + 2, my) - o.HeightAtMap(mx - 2, my)), Math.Abs(o.HeightAtMap(mx, my + 2) - o.HeightAtMap(mx, my - 2))) / 4;
                int k = -1;
                var (sk, sw) = Dominant(mx, my);
                if (sk >= 0 && sw > 0.35 + 0.3 * Noise.Fbm(mx * 0.06, my * 0.06, seed + 61, 2) && md.Mats.Length > 0)
                {
                    var st = stamps[sk];
                    var (qx, qz) = st.ToSource(mx, my);
                    double src = md.HeightAtMap(qx, qz) - water + wn;
                    int m = md.MaterialAtMap(qx, qz);
                    if (m >= 0 && (Math.Abs(h - src) < 1.2 || h > wn + 1.6 && src > wn + 1.6 && !(m == mSteep && sl < 0.4))) k = m;
                }
                if (k < 0)
                {
                    if (h < wn - 0.6) k = mUnder;
                    else if (h < wn + 0.6) k = mShore;
                    else if (h < wn + 1.6 + 1.6 * Noise.Fbm(mx * 0.05, my * 0.05, seed + 63, 2) && sl < 0.5) k = mBeach;
                    else if (sl > 0.75) k = mSteep;
                    else k = FlatMix(mx, my);
                }
                if (!inOld && md.Mats.Length > 0)
                {
                    // near the seam: the old edge cell's material, thinning out over 24 m
                    // (from the old map's playable ground: its frame - the editor's border - often has a material of its own)
                    int fm = INSET / 2;
                    int ci = Math.Clamp(oi, Math.Min(fm, md.Mx - 1), Math.Max(0, md.Mx - 1 - fm)), cj = Math.Clamp(oj, Math.Min(fm, md.My - 1), Math.Max(0, md.My - 1 - fm));
                    double d = Math.Sqrt(Sq(mx - (md.OffX + 4.0 * ci + 2)) + Sq(my - (md.OffY + 4.0 * cj + 2)));
                    if (Noise.Fbm(mx * 0.08, my * 0.08, seed + 17, 2) > S(0, 24, d) + 0.05) k = md.Mats[cj * md.Mx + ci];
                }
                if (h >= wn + 0.6 && md.Mats.Length > 0)
                {
                    // the slope over ~12 m (the cell's own and its neighbours'): rock follows cliffs and slopes, not single cells
                    double ss = 0.5 * sl + 0.125 * (Slope4(o, mx + 4, my) + Slope4(o, mx - 4, my) + Slope4(o, mx, my + 4) + Slope4(o, mx, my - 4));
                    double dz = 0.6 * Noise.Fbm(mx * 0.05, my * 0.05, seed + 71, 2) + 0.4 * Noise.Fbm(mx * 0.45, my * 0.45, seed + 73, 1);
                    if (!double.IsInfinity(rockFrom[k & 7])) { if (ss < rockFrom[k & 7] * (0.75 + 0.5 * dz)) k = FlatMix(mx, my); }
                    else if (ss > steepFrom * (0.8 + 0.4 * dz)) k = mSteep;
                }
                M[j * mxn + i] = (byte)k;
            }
        o.Mx = mxn; o.My = myn; o.Mats = M;

        // ---- the walk grid (PaFi)
        if (md.Walk is WalkGrid wg)
        {
            int ww = wg.W + (w + ea) / 2, wh = wg.H + (n + s) / 2;
            var nw = new WalkGrid { W = ww, H = wh, OffX = wg.OffX - w, OffY = wg.OffY - n, Cells = new byte[ww * wh] };
            for (int j = 0; j < wh; j++)
                for (int i = 0; i < ww; i++)
                {
                    int oi = i - w / 2, oj = j - n / 2;
                    double mx = nw.OffX + 2.0 * i, my = nw.OffY + 2.0 * j;
                    bool inOld = oi >= 0 && oj >= 0 && oi < wg.W && oj < wg.H;
                    if (inOld)
                    {
                        // (the cell's samples: those of the height grid at its corners)
                        int si = (int)Math.Floor((mx - md.OffX) / 2), sj = (int)Math.Floor((my - md.OffY) / 2);
                        bool ch = false;
                        for (int b = 0; b <= 1; b++) for (int a = 0; a <= 1; a++)
                        {
                            int x2 = si + a, y2 = sj + b;
                            if (x2 >= 0 && y2 >= 0 && x2 < md.Hx && y2 < md.Hy && changedOld[y2 * md.Hx + x2]) ch = true;
                        }
                        // (a land bridge's spine, too: the old grid may block flat ground there)
                        if (!ch && SpineDist(mx + 1, my + 1) < 1) ch = true;
                        // (ground a bridge changed: walkable if it was, or if it is now)
                        if (!ch || wg.Cells[oj * wg.W + oi] == 1) { nw.Cells[j * ww + i] = wg.Cells[oj * wg.W + oi]; continue; }
                    }
                    bool ok = WalkCell(o, mx, my, wn);
                    if (ok)
                    {
                        int si = Math.Clamp((int)Math.Floor((mx + 1 - ox) / 2), 0, hx - 1), sj = Math.Clamp((int)Math.Floor((my + 1 - oy) / 2), 0, hy - 1);
                        if (edgeLand[sj * hx + si] && grown[sj * hx + si] < 0.15 && Dominant(mx + 1, my + 1).W < 0.3 && !bridges.Any(b => SegDistAll(b, mx + 1, my + 1) < b.Land / 2 + 8))
                        {
                            // (the mirror image of the edge's ground in the old walk grid)
                            int ri = si - di, rj = sj - dj;
                            int mi = ri < ei0 ? 2 * ei0 - ri : ri > ei1 ? 2 * ei1 - ri : ri, mj = rj < ej0 ? 2 * ej0 - rj : rj > ej1 ? 2 * ej1 - rj : rj;
                            ok = wg.WalkableMap(md.OffX + 2.0 * Math.Clamp(mi, 0, md.Hx - 1) + 1, md.OffY + 2.0 * Math.Clamp(mj, 0, md.Hy - 1) + 1);
                        }
                    }
                    nw.Cells[j * ww + i] = ok ? (byte)1 : (byte)0;
                }
            o.Walk = nw;
        }

        // ---- the stamps' content: the patches' trees, stones, bushes, decoration, plants and forest squares
        if (stamps.Count > 0) CopyStampContent(md, o, stamps, Dominant, OnOldLand, SpineDist, clearSites, info);

        // ---- forest squares and plants on the land no stamp made (isthmuses, generated sites, `land`, "continue")
        var rng = new Mulberry32(seed * 7919 + 17);
        bool KeepClear(double x, double y, double margin)
        {
            foreach (var q in clearSites) if (Math.Sqrt(Sq(q.X - x) + Sq(q.Z - y)) - q.R < margin) return true;
            return SpineDist(x, y) < margin * 0.5;
        }
        // (new ground: the new land, or the old map's sea the extension made land)
        bool InNew(double x, double y) => !info.InOld(x, y) || !OnOldLand(x, y) && Math.Abs(o.HeightAtMap(x, y) - md.HeightAtMap(x, y)) > 0.5;
        bool Generated(double x, double y) => InNew(x, y) && Dominant(x, y).W < 0.3;
        // (plants also where a base site's levelling took the patch's own away)
        bool Levelled(double x, double y) => InNew(x, y) && clearSites.Any(q => Sq(q.X - x) + Sq(q.Z - y) < Sq(q.R + 30));
        double Slope(double x, double y) => Math.Max(Math.Abs(o.HeightAtMap(x + 2, y) - o.HeightAtMap(x - 2, y)), Math.Abs(o.HeightAtMap(x, y + 2) - o.HeightAtMap(x, y - 2))) / 4;
        if (md.Forest.Blocks.Count > 0)
        {
            var src = md.Forest.Blocks.Where(b => b.B.Take(15).Count(v => ((v >> 5) & 3) == 2 && (v & 31) > 0) >= 6).ToList();
            if (src.Count == 0) src = md.Forest.Blocks;
            var have = new HashSet<(int, int)>(o.Forest.Blocks.Select(b => (b.X, b.Y)));
            int added = 0;
            int bx0 = (int)Math.Floor(ox / 32.0), by0 = (int)Math.Floor(oy / 32.0), bx1 = (int)Math.Floor((ox + 2.0 * (hx - 1)) / 32.0), by1 = (int)Math.Floor((oy + 2.0 * (hy - 1)) / 32.0);
            for (int by = by0; by < by1; by++)
                for (int bx = bx0; bx < bx1; bx++)
                {
                    double cx = bx * 32 + 16, cy = by * 32 + 16;
                    double f = Noise.Fbm(cx * 0.006, cy * 0.006, seed + 19, 3);
                    double r = rng.Next();
                    if (have.Contains((bx, by))) continue;
                    // whole squares of new ground only
                    if (!Generated(bx * 32, by * 32) || !Generated(bx * 32 + 32, by * 32) || !Generated(bx * 32, by * 32 + 32) || !Generated(bx * 32 + 32, by * 32 + 32) || !Generated(cx, cy)) continue;
                    if (f < 0.52 || KeepClear(cx, cy, 40)) continue;
                    bool ok = true;
                    for (int b = 0; b <= 32 && ok; b += 16) for (int a = 0; a <= 32 && ok; a += 16)
                        if (o.HeightAtMap(bx * 32 + a, by * 32 + b) < wn + 1.5 || Slope(bx * 32 + a, by * 32 + b) > 0.6) ok = false;
                    if (!ok) continue;
                    var blk = new ForestBlock { X = bx, Y = by, B = (byte[])src[(int)(r * src.Count) % src.Count].B.Clone() };
                    o.Forest.Blocks.Add(blk);
                    added++;
                }
            info.Log.Add($"forest squares {added}");
        }
        else if (!e.Style.Equals("bare", StringComparison.Ordinal))
        {
            // a map without forest squares (caves, some savannas): clumps of the map's (or the setting's) trees as objects
            var cls = TreeClasses(md);
            int added = 0, tries = (int)(NewArea(md, o) / 2500);
            for (int k = 0; k < tries; k++)
            {
                double x = ox + rng.Next() * 2 * (hx - 1), y = oy + rng.Next() * 2 * (hy - 1);
                if (!Generated(x, y) || Noise.Fbm(x * 0.006, y * 0.006, seed + 19, 3) < 0.55 || KeepClear(x, y, 40)) continue;
                for (int c = 0; c < 6; c++)
                {
                    double tx = x + (rng.Next() - 0.5) * 24, ty = y + (rng.Next() - 0.5) * 24;
                    if (!Generated(tx, ty) || o.HeightAtMap(tx, ty) < wn + 1.2 || Slope(tx, ty) > 0.5) continue;
                    AddObject(o, "TREE", cls[(int)(rng.Next() * cls.Count) % cls.Count], tx, ty, rng.Next() * 6.283, null, info);
                    added++;
                }
            }
            info.Log.Add($"forest trees {added}");
        }
        // plants: the old map's own (no solid rocks, plateaus ...; maps that keep their plants as objects: those), in
        // clusters of one kind as on the old map, as dense as on its land, shore plants on the shore
        {
            var names = md.Plants.Where(p => NaturalName(p.Name) && !SolidDeco(p.Name))
                .GroupBy(p => p.Name).Select(g => (Name: g.Key, Count: g.Count(), Shore: g.Average(p => md.HeightAtMap(p.X, p.Y) - water) < 2.0, Obj: false)).ToList();
            if (names.Sum(q => q.Count) < 60)
                names.AddRange(md.Objects.Where(q => q.Type == "VGTN" && q.Owner == null && NaturalName(q.Cls) && !SolidDeco(q.Cls))
                    .GroupBy(q => q.Cls).Select(g => (Name: g.Key, Count: g.Count(), Shore: g.Average(q => md.HeightAtMap(q.X, q.Y) - water) < 2.0, Obj: true)));
            if (names.Count > 0)
            {
                double landOld = 0;
                for (int j = 0; j < md.Hy; j += 4) for (int i = 0; i < md.Hx; i += 4) if (OH[j * md.Hx + i] > water + 0.5) landOld += 64;
                double density = Math.Min(1 / 150.0, names.Sum(q => q.Count) / Math.Max(1, landOld));
                double genArea = 0;
                // (where: the land no stamp made, a base site's levelled ground (twice as dense: no patch plants are
                // left there), and - a little less dense, on top of what the patch brought - a stamp's land)
                double Want(double x, double y) => !InNew(x, y) ? 0 : Levelled(x, y) ? 2 : Generated(x, y) ? 1 : 0.8;
                for (int j = 0; j < hy; j += 4) for (int i = 0; i < hx; i += 4) { double x = ox + 2.0 * i, y = oy + 2.0 * j; if (H[j * hx + i] > wn + 0.3) genArea += 64 * Want(x, y); }
                int want = Math.Min(4000, (int)(genArea * density));
                var shore = names.Where(q => q.Shore).ToList(); var inland = names.Where(q => !q.Shore).ToList();
                if (shore.Count == 0) shore = inland; if (inland.Count == 0) inland = shore;
                // (clusters of 2 .. 7 of one kind, on a jittered 12 m scan of the new ground: as many as the density says)
                int added = 0;
                double perCell = 144.0 * density / 4.5 * 1.6;
                for (double cy = oy + 6; cy < Y1 - 6 && added < want; cy += 12)
                    for (double cx = ox + 6; cx < X1 - 6 && added < want; cx += 12)
                    {
                        double x = cx + (rng.Next() - 0.5) * 12, y = cy + (rng.Next() - 0.5) * 12, roll = rng.Next(), pickR = rng.Next(), cnR = rng.Next();
                        if (roll > perCell * 2) continue;
                        double h = o.HeightAtMap(x, y);
                        if (roll > perCell * Want(x, y) || h < wn + 0.3 || Slope(x, y) > 0.7 || KeepClear(x, y, -16)) continue;
                        var set = h < wn + 2.5 ? shore : inland;
                        int tot = set.Sum(q => q.Count); double r = pickR * tot; var pick = set[0];
                        foreach (var q in set) { if (r < q.Count) { pick = q; break; } r -= q.Count; }
                        int cn = 2 + (int)(cnR * 6);
                        for (int c = 0; c < cn && added < want; c++)
                        {
                            double px = x + (rng.Next() - 0.5) * 14, py = y + (rng.Next() - 0.5) * 14, ph2 = o.HeightAtMap(px, py);
                            double rot = rng.Next() * 6.283;
                            if (Want(px, py) <= 0 || ph2 < wn + 0.3 || Slope(px, py) > 0.7 || SpineDist(px, py) < 1) continue;
                            if (pick.Obj) AddObject(o, "VGTN", pick.Name, px, py, rot, null, null);
                            else o.Plants.Add(new MapPlant { Name = pick.Name, X = px, Y = py, Z = ph2, Rot = rot, Q = QuatZ(rot) });
                            added++;
                        }
                    }
                info.Log.Add($"plants {added} (of {want}: {genArea:0} m² at {density:0.00000} per m², {names.Count} kinds)");
            }
        }
        L.Info($"{md.File}: extended N {n} S {s} W {w} E {ea} m -> {o.Hx * 2} x {o.Hy * 2} m, {bridges.Count} bridges, {newSites.Count} base sites; {string.Join(", ", info.Log)}");
        return o;
    }

    /// <summary>the land bridges as made: each goes on 24 m past its last point (into the land it reaches: over the
    /// shore's rim to walkable ground), and its line is bent into a natural curve (a wandering offset, none at its ends:
    /// the spine). Land = the width of the land around the spine.</summary>
    static List<CoopBridge> Spines(IEnumerable<CoopBridge> list, int seed)
    {
        var res = new List<CoopBridge>();
        int bi = 0;
        foreach (var b in list.Where(b => b.Points.Count >= 2))
        {
            var P = b.Points.ToList();
            {
                var p1 = P[^1]; var p0 = P[^2];
                double dx = p1.X - p0.X, dz = p1.Z - p0.Z, l = Math.Sqrt(dx * dx + dz * dz);
                if (l >= 1) P.Add(new CoopPoint(p1.X + dx / l * 24, p1.Z + dz / l * 24));
            }
            int last = P.Count - 2;                                   // (the landing; the point after it is the 24 m added)
            double L = 0;
            for (int k = 0; k < last; k++) L += Math.Sqrt(Sq(P[k + 1].X - P[k].X) + Sq(P[k + 1].Z - P[k].Z));
            double amp = Math.Min(36, 0.12 * L);
            var pts = new List<CoopPoint>();
            double s0 = 0;
            for (int k = 0; k < last; k++)
            {
                double dx = P[k + 1].X - P[k].X, dz = P[k + 1].Z - P[k].Z, len = Math.Sqrt(dx * dx + dz * dz);
                if (len < 1) continue;
                int m = Math.Max(1, (int)Math.Ceiling(len / 8));
                double nx = -dz / len, nz = dx / len;
                for (int i = 0; i < m; i++)
                {
                    double t = (double)i / m, sAt = s0 + t * len, u = L > 0 ? sAt / L : 0;
                    double env = S(0, 0.22, u) * (1 - S(0.78, 1, u));
                    double off = amp * Math.Clamp((Noise.Fbm(sAt / 140.0 + bi * 7.7, 3.1, seed + 51, 2) - 0.5) * 3.2, -1, 1) * env;
                    pts.Add(new CoopPoint(P[k].X + dx * t + nx * off, P[k].Z + dz * t + nz * off));
                }
                s0 += len;
            }
            pts.Add(P[last]); pts.Add(P[last + 1]);
            res.Add(new CoopBridge { Points = pts, Width = b.Width, Cut = b.Cut, Shape = b.Shape, Land = b.Path ? b.Width : b.Ford ? Math.Max(24, b.Width * 1.6) : Math.Max(56, b.Width * 3.2) });
            bi++;
        }
        return res;
    }

    /// <summary>a distance field (m) on a 4 m grid: 0 cells keep 0, the others get the distance to the nearest 0 cell
    /// (two passes, 3-4 chamfer: plain arithmetic)</summary>
    static void Chamfer(float[] D, int gw, int gh)
    {
        for (int b = 0; b < gh; b++)
            for (int a = 0; a < gw; a++)
            {
                float v = D[b * gw + a];
                if (a > 0) v = Math.Min(v, D[b * gw + a - 1] + 4);
                if (b > 0) v = Math.Min(v, D[(b - 1) * gw + a] + 4);
                if (a > 0 && b > 0) v = Math.Min(v, D[(b - 1) * gw + a - 1] + 5.657f);
                if (a + 1 < gw && b > 0) v = Math.Min(v, D[(b - 1) * gw + a + 1] + 5.657f);
                D[b * gw + a] = v;
            }
        for (int b = gh - 1; b >= 0; b--)
            for (int a = gw - 1; a >= 0; a--)
            {
                float v = D[b * gw + a];
                if (a + 1 < gw) v = Math.Min(v, D[b * gw + a + 1] + 4);
                if (b + 1 < gh) v = Math.Min(v, D[(b + 1) * gw + a] + 4);
                if (a + 1 < gw && b + 1 < gh) v = Math.Min(v, D[(b + 1) * gw + a + 1] + 5.657f);
                if (a > 0 && b + 1 < gh) v = Math.Min(v, D[(b + 1) * gw + a - 1] + 5.657f);
                D[b * gw + a] = v;
            }
    }

    /// <summary>the joins' smoothing (Extend): H is the land as made, HB the ground before (old samples, the old edge going
    /// on), Hg the land before the sites were levelled and the bridges graded</summary>
    static void SmoothJoins(float[] H, float[] HB, float[] Hg, bool[] kept, float[] free, float[] joinD, int hx, int hy, int ox, int oy,
        double water, double wn, bool grow, List<CoopKeep> keep, Func<double, double, double> spineDist, MapExtensionInfo info)
    {
        int n = hx * hy;
        // how far each sample may move: 0 fixed .. 1
        var w = new float[n];
        for (int j = 1; j < hy - 1; j++)
            for (int i = 1; i < hx - 1; i++)
            {
                int k = j * hx + i;
                double mx = ox + 2.0 * i, my = oy + 2.0 * j;
                // (and in style "grow" the old shore below SHORE, as far as the protection lets the growth change it: an
                // old beach next to raised land rises a little instead of staying a step below it)
                bool changed = !kept[k] || Math.Abs(H[k] - HB[k]) > 0.05 || grow && HB[k] < water + SHORE;
                if (!changed) continue;
                double sd = spineDist(mx, my);
                // (a spine's own way stays as graded)
                if (sd < 0.5) continue;
                double f = 1;
                if (kept[k])
                {
                    // (the old map: what the growth may change, as far as it may; its land above the shore - and in style
                    // "island" all its land - only where a bridge cut it)
                    bool high = grow ? HB[k] >= water + SHORE : HB[k] >= water - 0.5;
                    if (high && !(sd < 16 && Math.Abs(H[k] - Hg[k]) > 0.05)) continue;
                    if (!high) f = grow ? free[k] : 0;
                }
                foreach (var z in keep) if (Sq(mx - z.X) + Sq(my - z.Z) < Sq(z.R + 24)) f = 0;
                w[k] = (float)f;
            }
        // the spots to smooth around: dry movable ground that is steep (above 0.5) or sharply bent (0.4) where it joins -
        // levelled or graded ground, within 20 m of the old land the join carried out, the old map's remade frame
        var seed = new bool[n]; int seeds = 0;
        for (int j = 2; j < hy - 2; j++)
            for (int i = 2; i < hx - 2; i++)
            {
                int k = j * hx + i;
                if (w[k] <= 0 || H[k] < wn + 0.3) continue;
                bool join = Math.Abs(H[k] - Hg[k]) > 0.05 || joinD[k] < 20 || !kept[k] && info.InOld(ox + 2.0 * i, oy + 2.0 * j);
                if (!join) continue;
                foreach (int q in new[] { 1, hx })
                {
                    double a = H[k - q], b = H[k], c = H[k + q];
                    if (Math.Max(Math.Abs(c - b), Math.Abs(b - a)) / 2 > 0.5 || Math.Abs(a - 2 * b + c) / 2 > 0.4) { seed[k] = true; seeds++; break; }
                }
            }
        if (seeds == 0) return;
        // (the band: 8 samples - 16 m - around the spots)
        var band = seed;
        for (int it = 0; it < 8; it++)
        {
            var b2 = (bool[])band.Clone();
            for (int j = 1; j < hy - 1; j++)
                for (int i = 1; i < hx - 1; i++)
                {
                    int k = j * hx + i;
                    if (!band[k] && (band[k - 1] || band[k + 1] || band[k - hx] || band[k + hx])) b2[k] = true;
                }
            band = b2;
        }
        var idx = new List<int>();
        for (int k = 0; k < n; k++) if (band[k] && w[k] > 0) idx.Add(k);
        var H0 = (float[])H.Clone();
        for (int pass = 0; pass < 12; pass++)
        {
            var P = (float[])H.Clone();
            foreach (int k in idx)
            {
                double v = (P[k - hx - 1] + P[k - hx] + P[k - hx + 1] + P[k - 1] + P[k] + P[k + 1] + P[k + hx - 1] + P[k + hx] + P[k + hx + 1]) / 9.0;
                double h = P[k] + (v - P[k]) * w[k];
                // (land stays land, water stays water: no new coast and no new way; never dug below the ground before)
                // (the water keeps its depth: a ship's way stays open)
                if (H0[k] >= wn) h = Math.Max(h, wn + Math.Min(0.3, H0[k] - wn));
                else h = H0[k];
                h = Math.Max(h, Math.Min(H0[k], HB[k]));
                H[k] = (float)h;
            }
        }
        info.Log.Add($"joins smoothed: {seeds} steep or bent spots, {idx.Count} samples");
    }

    /// <summary>the old map's beach: its most used material on gentle ground just above the water (0.3 .. 2.5 m)</summary>
    static byte Beach(MapData md, byte shore)
    {
        var c = new int[8];
        for (int j = 0; j < md.My; j++)
            for (int i = 0; i < md.Mx; i++)
            {
                if (md.Mats.Length == 0) break;
                double mx = md.OffX + 4.0 * i + 2, my = md.OffY + 4.0 * j + 2, h = md.HeightAtMap(mx, my);
                if (h < md.Water + 0.3 || h > md.Water + 2.5) continue;
                double sl = Math.Max(Math.Abs(md.HeightAtMap(mx + 2, my) - md.HeightAtMap(mx - 2, my)), Math.Abs(md.HeightAtMap(mx, my + 2) - md.HeightAtMap(mx, my - 2))) / 4;
                if (sl < 0.3) c[md.Mats[j * md.Mx + i] & 7]++;
            }
        int b = 0;
        for (int k = 1; k < 8; k++) if (c[k] > c[b]) b = k;
        return c[b] > 0 ? (byte)b : shore;
    }

    /// <summary>the old map's third most used ground material on flat land (or the most used, if it is rare)</summary>
    static byte ThirdFlat(MapData md, byte land, byte land2)
    {
        var flat = new int[8];
        for (int j = 0; j < md.My; j += 2)
            for (int i = 0; i < md.Mx; i += 2)
            {
                if (md.Mats.Length == 0) break;
                double mx = md.OffX + 4.0 * i + 2, my = md.OffY + 4.0 * j + 2, h = md.HeightAtMap(mx, my);
                double sl = Math.Max(Math.Abs(md.HeightAtMap(mx + 2, my) - md.HeightAtMap(mx - 2, my)), Math.Abs(md.HeightAtMap(mx, my + 2) - md.HeightAtMap(mx, my - 2))) / 4;
                if (h > md.Water + 1.5 && sl < 0.3) flat[md.Mats[j * md.Mx + i] & 7]++;
            }
        int b = -1;
        for (int k = 0; k < 8; k++) if (k != land && k != land2 && (b < 0 || flat[k] > flat[b])) b = k;
        return b >= 0 && flat[b] >= flat[land] / 8 ? (byte)b : land;
    }

    /// <summary>a 2 m walk cell at map (mx, my) .. + 2: dry and not too steep (the new land's rule)</summary>
    static bool WalkCell(MapData o, double mx, double my, double water)
    {
        double a = o.HeightAtMap(mx, my), b = o.HeightAtMap(mx + 2, my), c = o.HeightAtMap(mx + 2, my + 2), d = o.HeightAtMap(mx, my + 2);
        if (Math.Min(Math.Min(a, b), Math.Min(c, d)) < water + 0.1) return false;
        double m = Math.Max(Math.Max(Math.Abs(a - b), Math.Abs(b - c)), Math.Max(Math.Abs(c - d), Math.Abs(d - a))) / 2;
        return Math.Max(m, Math.Max(Math.Abs(a - c), Math.Abs(b - d)) / 2.83) <= WALK_TAN;
    }

    static double Sq(double v) => v * v;
    static double SegDistAll(CoopBridge b, double x, double y)
    {
        double best = 1e9;
        for (int k = 0; k + 1 < b.Points.Count; k++) best = Math.Min(best, SegDist(x, y, b.Points[k], b.Points[k + 1]));
        return best;
    }
    static double SegDist(double x, double y, CoopPoint a, CoopPoint b)
    {
        double dx = b.X - a.X, dy = b.Z - a.Z, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Z) * dy) / l2, 0, 1) : 0;
        double px = a.X + t * dx - x, py = a.Z + t * dy - y;
        return Math.Sqrt(px * px + py * py);
    }
    static double NewArea(MapData md, MapData o) => 4.0 * ((double)o.Hx * o.Hy - (double)md.Hx * md.Hy);

    /// <summary>a quaternion turning by rot about the map's z axis (Rot = -2 atan2(qz, qw)), from a short series - the
    /// plants only need to look turned</summary>
    static double[] QuatZ(double rot)
    {
        double h = -rot / 2;
        // sin / cos by their series on [-pi, pi] (deterministic: no library calls)
        double h2 = h * h;
        double sn = h * (1 - h2 / 6 * (1 - h2 / 20 * (1 - h2 / 42 * (1 - h2 / 72 * (1 - h2 / 110)))));
        double cs = 1 - h2 / 2 * (1 - h2 / 12 * (1 - h2 / 30 * (1 - h2 / 56 * (1 - h2 / 90))));
        return new[] { 0, 0, sn, cs };
    }

    /// <summary>a shallow copy with own lists (objects, plants, forest squares) and grids replaced later</summary>
    internal static MapData Clone(MapData md) => new()
    {
        W = md.W, H = md.H, OffX = md.OffX, OffY = md.OffY, Water = md.Water, Hx = md.Hx, Hy = md.Hy, Heights = md.Heights, Mx = md.Mx, My = md.My, Mats = md.Mats,
        TerrainSetting = md.TerrainSetting, Info = new Dictionary<string, string>(md.Info), File = md.File, Preview = md.Preview,
        Description = md.Description, InfoTree = md.InfoTree, DefaultCamera = md.DefaultCamera, StartTime = md.StartTime, Setting = md.Setting,
        SettingName = md.SettingName, Name = md.Name, MaxPlayers = md.MaxPlayers,
        Objects = md.Objects.ToList(), Plants = md.Plants.ToList(),
        Forest = new ForestMap { W = md.Forest.W, H = md.Forest.H, Blocks = md.Forest.Blocks.ToList() },
        Walk = md.Walk, Regions = md.Regions.ToList(), Extension = md.Extension, Biomes = md.Biomes,
    };

    /// <summary>the old map's ground materials by their use: under water, shore, flat land (most and second most used),
    /// steep ground - counted on the map itself</summary>
    public static (byte Under, byte Shore, byte Land, byte Land2, byte Steep) MaterialRoles(MapData md)
    {
        var under = new int[8]; var shore = new int[8]; var flat = new int[8]; var steep = new int[8];
        for (int j = 0; j < md.My; j++)
            for (int i = 0; i < md.Mx; i++)
            {
                if (md.Mats.Length == 0) break;
                double mx = md.OffX + 4.0 * i + 2, my = md.OffY + 4.0 * j + 2;
                double h = md.HeightAtMap(mx, my);
                double sl = Math.Max(Math.Abs(md.HeightAtMap(mx + 2, my) - md.HeightAtMap(mx - 2, my)), Math.Abs(md.HeightAtMap(mx, my + 2) - md.HeightAtMap(mx, my - 2))) / 4;
                int k = md.Mats[j * md.Mx + i] & 7;
                if (h < md.Water - 0.5) under[k]++;
                else if (h < md.Water + 1.5) shore[k]++;
                else if (sl > 0.7) steep[k]++;
                else if (sl < 0.3) flat[k]++;
            }
        static int Arg(int[] c, int skip = -1) { int b = -1; for (int k = 0; k < 8; k++) if (k != skip && (b < 0 || c[k] > c[b])) b = k; return b; }
        int land = Arg(flat), land2 = Arg(flat, land);
        if (flat[land2] < flat[land] / 10) land2 = land;
        int un = under.Sum() > 0 ? Arg(under) : land, sh = shore.Sum() > 0 ? Arg(shore) : land, st = steep.Sum() > 0 ? Arg(steep) : land;
        return ((byte)un, (byte)sh, (byte)land, (byte)land2, (byte)st);
    }

    /// <summary>the ground's slope at map (mx, my) as the material roles measure it: the larger height difference over
    /// 4 m along x or y, per metre (0.3 gentle, 0.7 a cliff's face)</summary>
    public static double Slope4(MapData md, double mx, double my) =>
        Math.Max(Math.Abs(md.HeightAtMap(mx + 2, my) - md.HeightAtMap(mx - 2, my)), Math.Abs(md.HeightAtMap(mx, my + 2) - md.HeightAtMap(mx, my - 2))) / 4;

    /// <summary>how the old map uses each ground material on its land (above the shore): cells, the slope a fifth of
    /// them lies below (P20), the median slope, the share on steep ground (above 0.5)</summary>
    public static (int Count, double P20, double P50, double Steep)[] GroundUse(MapData md)
    {
        var sl = new List<double>[8];
        for (int k = 0; k < 8; k++) sl[k] = new List<double>();
        for (int j = 0; j < md.My && md.Mats.Length > 0; j++)
            for (int i = 0; i < md.Mx; i++)
            {
                double mx = md.OffX + 4.0 * i + 2, my = md.OffY + 4.0 * j + 2;
                if (md.HeightAtMap(mx, my) < md.Water + 1.5) continue;
                sl[md.Mats[j * md.Mx + i] & 7].Add(Slope4(md, mx, my));
            }
        var r = new (int, double, double, double)[8];
        for (int k = 0; k < 8; k++)
        {
            var l = sl[k]; l.Sort();
            r[k] = l.Count == 0 ? (0, 0, 0, 0) : (l.Count, l[(int)(l.Count * 0.2)], l[l.Count / 2], l.Count(v => v > 0.5) / (double)l.Count);
        }
        return r;
    }

    /// <summary>the old map's rock materials and where they lie: per ground material the slope from which the map uses it
    /// (its P20 on the map's land, at least 0.3) for the materials that lie mostly on steep ground (median slope 0.45 or
    /// more and rarely on flat ground: P20 0.2 or more; at least 30 cells); +infinity for the materials of flat land. New and changed ground gets a rock material
    /// only where it is at least that steep (MapExtension: the ground follows the final slope).</summary>
    public static double[] RockSlopes(MapData md)
    {
        var use = GroundUse(md);
        var r = new double[8];
        for (int k = 0; k < 8; k++)
            r[k] = use[k].Count >= 30 && use[k].P50 >= 0.45 && use[k].P20 >= 0.2 ? Math.Max(0.3, use[k].P20) : double.PositiveInfinity;
        return r;
    }

    // ------------------------------------------------------------------------------------------------ resources
    static string SettingKey(string setting) => setting switch
    {
        "Northland" => "nor", "Savanna" => "sav", "Jungle" => "jun", "Icewaste" => "ice", "Ashvalley" => "ash",
        "Cave1" => "cave1", "Cave2" => "cave2", "Cave3" => "cave3", _ => "nor",
    };
    // the settings' own trees (forest.json): the caves 2 and 3 have the Ash Valley's
    static string TreeSetting(string setting) => setting switch { "Cave2" or "Cave3" => "Ashvalley", "TestSet" => "Northland", _ => setting };

    /// <summary>the tree classes of the map (its placed trees, most used first), else the setting's five</summary>
    public static List<string> TreeClasses(MapData md)
    {
        var l = md.Objects.Where(q => q.Type == "TREE" && q.Cls.Length > 0 && !q.Cls.Contains("dead", StringComparison.OrdinalIgnoreCase))
            .GroupBy(q => q.Cls).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).Take(5).ToList();
        if (l.Count > 0) return l;
        var st = TreeSetting(md.SettingName is { Length: > 0 } sn ? sn : md.Setting);
        return Enumerable.Range(1, 5).Select(k => $"{st}_Tree_0{k}").ToList();
    }
    /// <summary>the stone class of the map (its most used), else Resource_Stone_&lt;setting&gt;</summary>
    public static string StoneClass(MapData md) =>
        md.Objects.Where(q => q.Type == "STON" && q.Cls.Length > 0).GroupBy(q => q.Cls).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault()
        ?? "Resource_Stone_" + Cap(SettingKey(md.SettingName is { Length: > 0 } sn ? sn : md.Setting));
    static string Cap(string k) => char.ToUpperInvariant(k[0]) + k.Substring(1);
    /// <summary>the fruit bush class of the map, else the setting's</summary>
    public static string BushClass(MapData md) =>
        md.Objects.Where(q => q.Type == "FRUI" && q.Cls.Length > 0 && !q.Cls.Contains("fish", StringComparison.OrdinalIgnoreCase)).GroupBy(q => q.Cls).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault()
        ?? TreeSetting(md.SettingName is { Length: > 0 } sn ? sn : md.Setting) + "_Fruit_Bush";
    /// <summary>the fish shoal class of the map, else FishShoal_Tristychius (every setting's)</summary>
    public static string FishClass(MapData md) =>
        md.Objects.Where(q => q.Type == "FRUI" && q.Cls.Contains("fish", StringComparison.OrdinalIgnoreCase)).Select(q => q.Cls).FirstOrDefault() ?? "FishShoal_Tristychius";

    /// <summary>the map object type of a resource class (TREE, STON, FRUI)</summary>
    public static string TypeOfClass(string cls)
    {
        string c = cls.ToLowerInvariant();
        if (c.Contains("fish") || c.Contains("bush") || c.Contains("fruit")) return "FRUI";
        if (c.Contains("stone")) return "STON";
        return "TREE";
    }

    static MapObject AddObject(MapData o, string type, string cls, double x, double y, double rot, double? amount, MapExtensionInfo? info)
    {
        var ob = new MapObject
        {
            Type = type, Cls = cls, Name = $"coop_{cls}_{o.Objects.Count}", X = x, Y = y, Z = o.HeightAtMap(x, y), Rot = rot, Q = QuatZ(rot), Owner = null,
        };
        if (amount is double a) ob.Attr["hitpoints"] = a.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        o.Objects.Add(ob);
        info?.Added.Add(ob);
        return ob;
    }

    /// <summary>puts the resources of a co-op definition on the map (as TREE / STON / FRUI map objects, into md.Objects);
    /// `sites`: the seats' starts - generated groups keep 20 m away from them (room for the main building). Returns
    /// the objects added.</summary>
    public static List<MapObject> PlaceResources(MapData md, IEnumerable<CoopResource> resources, int seed, IReadOnlyList<CoopSite>? sites = null)
    {
        var info = md.Extension ??= new MapExtensionInfo { OldX0 = md.OffX, OldY0 = md.OffY, OldX1 = md.OffX + (md.Hx - 1) * 2, OldY1 = md.OffY + (md.Hy - 1) * 2, Seed = seed };
        int first = info.Added.Count;
        var trees = TreeClasses(md);
        string stone = StoneClass(md), bush = BushClass(md), fish = FishClass(md);
        var placed = new List<(double X, double Y, double R)>();
        foreach (var q in md.Objects)
            if (q.Type is "STON" or "FRUI" or "TREE" or "BLDG" or "DCCO") placed.Add((q.X, q.Y, q.Type == "BLDG" ? 8 : q.Type == "STON" ? 4 : 1.5));
        var bridges = md.Extension?.Bridges ?? new List<CoopBridge>();
        double Slope(double x, double y) => Math.Max(Math.Abs(md.HeightAtMap(x + 2, y) - md.HeightAtMap(x - 2, y)), Math.Abs(md.HeightAtMap(x, y + 2) - md.HeightAtMap(x, y - 2))) / 4;
        bool Dry(double x, double y) => md.HeightAtMap(x, y) > md.Water + 0.8 && Slope(x, y) < 0.45 && (md.Walk == null || md.WalkableMap(x, y) == true);
        bool Wet(double x, double y) => md.HeightAtMap(x, y) < md.Water - 1.5;
        bool Free(double x, double y, double r, bool keepSites)
        {
            foreach (var p in placed) if (Sq(p.X - x) + Sq(p.Y - y) < Sq(p.R + r)) return false;
            if (keepSites && sites != null) foreach (var s in sites) if (Sq(s.X - x) + Sq(s.Z - y) < 22 * 22) return false;
            foreach (var b in bridges) for (int k = 0; k + 1 < b.Points.Count; k++) if (SegDist(x, y, b.Points[k], b.Points[k + 1]) < b.Width / 2 + 3) return false;
            return true;
        }
        int idx = 0;
        void Group(Mulberry32 rng, string type, Func<string> cls, double cx, double cy, double r, int count, double spacing, bool water, double? amount, double r0 = 0)
        {
            int got = 0;
            for (int t = 0; t < count * 60 && got < count; t++)
            {
                // a point in the ring r0 .. r around the centre
                double a = rng.Next() * 2 - 1, b = rng.Next() * 2 - 1;
                if (a * a + b * b > 1) continue;
                double x = cx + a * r, y = cy + b * r;
                if (r0 > 0 && Sq(x - cx) + Sq(y - cy) < r0 * r0) continue;
                if (water ? !Wet(x, y) : !Dry(x, y)) continue;
                if (!Free(x, y, spacing, true)) continue;
                AddObject(md, type, cls(), x, y, rng.Next() * 6.283, amount, info);
                placed.Add((x, y, spacing));
                got++;
            }
            if (got < count) info.Log.Add($"{type} {cls()} at ({cx:0}, {cy:0}): {got} of {count} placed");
        }
        foreach (var res in resources)
        {
            var rng = new Mulberry32(seed * 7919 + 101 * ++idx);
            string RandTree() => trees[(int)(rng.Next() * trees.Count) % trees.Count];
            if (res.Class is { Length: > 0 } cl && res.Kind == null)
            {
                string type = TypeOfClass(cl);
                if (res.R <= 0) { AddObject(md, type, cl, res.X, res.Z, rng.Next() * 6.283, res.Amount, info); placed.Add((res.X, res.Z, 3)); }
                else Group(rng, type, () => cl, res.X, res.Z, res.R, res.Count, type == "STON" ? 7 : 3, cl.Contains("fish", StringComparison.OrdinalIgnoreCase), res.Amount);
                continue;
            }
            switch (res.Kind)
            {
                case "wood": Group(rng, "TREE", RandTree, res.X, res.Z, res.R > 0 ? res.R : 20, res.Count > 1 ? res.Count : 24, 3.2, false, null); break;
                case "stone": Group(rng, "STON", () => stone, res.X, res.Z, res.R > 0 ? res.R : 12, res.Count, 8, false, res.Amount); break;
                case "food": Group(rng, "FRUI", () => bush, res.X, res.Z, res.R > 0 ? res.R : 14, res.Count > 1 ? res.Count : 6, 3.5, false, res.Amount); break;
                case "fish": Group(rng, "FRUI", () => fish, res.X, res.Z, res.R > 0 ? res.R : 30, res.Count, 12, true, res.Amount); break;
                case "kit":
                {
                    // a base's set around (X, Z): the wood patch towards the most land, stones and bushes nearer
                    double best = -1, bx = 1, by = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        double ca = Cos8(k), sa = Cos8(k + 6);
                        int dry = 0;
                        for (double d = 30; d <= 70; d += 8) if (Dry(res.X + ca * d, res.Z + sa * d) && Free(res.X + ca * d, res.Z + sa * d, 4, false)) dry++;
                        double score = dry + (k == 0 ? 0.01 : 0);
                        if (score > best) { best = score; bx = ca; by = sa; }
                    }
                    double rr = res.R > 0 ? res.R : 44;
                    // (only what the land - a stamp's patch - does not have already: its own trees, forest squares,
                    // stones and bushes near the base count)
                    int Near(string type, double r, bool fish = false) => md.Objects.Count(q => q.Type == type && q.Owner == null && q.Cls.Contains("fish", StringComparison.OrdinalIgnoreCase) == fish
                        && Sq(q.X - res.X) + Sq(q.Y - res.Z) < r * r && Dry(q.X, q.Y));
                    int forestTrees = md.Forest.Blocks.Where(b => Sq(b.X * 32 + 16 - res.X) + Sq(b.Y * 32 + 16 - res.Z) < 80 * 80)
                        .Sum(b => b.B.Take(15).Count(v => ((v >> 5) & 3) == 2 && (v & 31) > 0));
                    int haveT = Near("TREE", 95) + forestTrees, haveS = Near("STON", 105), haveB = Near("FRUI", 105);
                    if (haveT < 26) Group(rng, "TREE", RandTree, res.X + bx * rr, res.Z + by * rr, 20, Math.Min(30, 26 - haveT + 4), 3.2, false, null);
                    if (haveS < 2) Group(rng, "STON", () => stone, res.X, res.Z, 46, 2 - haveS, 14, false, res.Amount, 26);
                    if (haveB < 6) Group(rng, "FRUI", () => bush, res.X, res.Z, 42, 6 - haveB, 3.5, false, null, 22);
                    info.Log.Add($"kit at ({res.X:0}, {res.Z:0}): the land has {haveT} trees, {haveS} stones, {haveB} bushes");
                    break;
                }
                default: info.Log.Add($"unknown resource kind '{res.Kind}' ({res})"); break;
            }
        }
        return info.Added.Skip(first).ToList();
    }
    // cos of k * 45 degrees (k mod 8)
    static double Cos8(int k) => (((k % 8) + 8) % 8) switch { 0 => 1, 1 => 0.70710678118654752, 2 => 0, 3 => -0.70710678118654752, 4 => -1, 5 => -0.70710678118654752, 6 => 0, _ => 0.70710678118654752 };
}
