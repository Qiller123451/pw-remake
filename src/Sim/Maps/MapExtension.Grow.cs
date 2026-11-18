// Co-op maps, style "grow" (the default, docs/COOP.md "Maps"): the mission's landmass itself gets bigger. Each co-op
// seat's base site and each lobe of `extend.grow` is a round piece of land made of a patch of the old map (a stamp,
// MapExtension.Stamp.cs: its heights, ground, plants, rocks, trees), and the lobes melt into one landmass with the old
// map's land:
//
//   lobes       each lobe's land weight is a noise-bent disc; where several overlap their heights (relative to the
//               water) are averaged by weight, so neighbouring patches blend instead of meeting in a step; inside a lobe
//               the patch's own coves and lakes are filled to low land (a lobe is land, its outer edge is the coast)
//   the join    near the old map's land the new heights go over into the old land's own (its coast's heights carried
//               outwards over ~110 m): the old shallow sea there becomes land, the old beaches under the lobes rise a
//               little and become inland meadow, a cliff coast goes on as high ground that comes down gently
//   the coast   the lobes' union fades into the sea floor along its noise-bent edge (a beach where land fades); the
//               shelf, ground materials, walk grid and plants follow as for any new land (MapExtension.cs)
//   protected   the old map stays exactly as it is within 40 m of every building, wall, gate, ruin or other placed piece
//               of the mission and within 20 m of its units, start locations and nests (not the natural trees, stones
//               and plants), and in the definition's `keep` zones (no new land in them either); its land above the
//               shore (2.5 m over the water) never changes
//
// What the old map's gameplay needs - no way round a gate, a wall or a scripted choke - is the definition's choice of
// where the lobes touch the old coast; CoopMapsTests checks it (old ground seat 1 reaches only as in single player).
// Deterministic as the rest of the extension: seeded, plain arithmetic.
using System;
using System.Collections.Generic;
using System.Linq;

namespace PwRemake.Sim.Maps;

public static partial class MapExtension
{
    /// <summary>distance (m) around a mission object of the old map within which the extension changes nothing</summary>
    public const double PROTECT = 40;
    /// <summary>... around a unit, a start location or a nest (they stand on natural ground)</summary>
    public const double PROTECT_UNIT = 20;
    /// <summary>how far (m) the old land's heights are carried out into the new land at the join</summary>
    public const double JOIN = 110;
    /// <summary>old ground at or above water + SHORE (m) is never changed by the growth</summary>
    public const double SHORE = 2.5;

    /// <summary>how far (m) around an object of the old map the growth changes nothing: PROTECT for what stands on built
    /// ground (buildings, walls, gates, ruins and other placed pieces), PROTECT_UNIT for units, start locations and
    /// nests, 0 for the natural trees, stones, bushes and the setting's own decoration (its rocks, craters, steam,
    /// flowers: landscape, not a mission's)</summary>
    public static double ProtectRadius(MapObject o)
    {
        if (Copyable(o) || o.Type is "TREE" or "STON" or "FRUI" || o.Owner == null && o.Type is "DCCO" or "DECO" or "VGTN" && NATURAL.IsMatch(o.Cls)) return 0;
        return o.Type is "BLDG" or "DCCO" or "DECO" or "WALL" ? PROTECT : PROTECT_UNIT;
    }

    /// <summary>the patch of the old map a lobe of grown land is made of: the given one, or the best automatic one - land
    /// all round (a piece of the island's inside, not its coast), gentle, with varied ground and plants, no buildings or
    /// start locations; a base site's lobe also needs a flat spot of the site's size at its centre. Turned by a seeded
    /// direction. null: generated hills.</summary>
    static StampInfo? GrowStamp(MapData md, CoopGrow g, double flatR, List<(double X, double Z)> used, int seed, int index)
    {
        if (g.NoStamp) return null;
        var sp = g.Stamp;
        double R = g.R;
        bool mirror = sp?.Mirror ?? ((index + seed) & 1) == 1;
        int rot = sp?.Rot is double deg ? (((int)Math.Round(deg * DIRS / 360.0) % DIRS) + DIRS) % DIRS
            : (int)Math.Floor(Noise.Fbm(index * 3.7 + 0.5, seed * 0.31 + 1.3, seed + 97, 2) * DIRS * 4) % DIRS;
        if (sp?.X is double sx && sp.Z is double sz)
            return new StampInfo { Seat = g.Seat, SiteX = g.X, SiteZ = g.Z, SX = sx, SZ = sz, R = R, Rot = rot, Mirror = mirror };
        double water = md.Water;
        double mg = Math.Min(R * 0.6, 200) + 32;
        double x0 = md.OffX + mg, x1 = md.OffX + (md.Hx - 1) * 2 - mg, z0 = md.OffY + mg, z1 = md.OffY + (md.Hy - 1) * 2 - mg;
        var foreign = md.Objects.Where(o => Foreign(o) > 0).Select(o => (o.X, o.Y, W: Foreign(o), o.Type)).ToList();
        var natural = md.Objects.Where(o => o.Owner == null && o.Type == "TREE").Select(o => (o.X, o.Y)).ToList();
        var forest = md.Forest.Blocks.Select(b => (X: b.X * 32.0 + 16, Y: b.Y * 32.0 + 16)).ToList();
        StampInfo? best = null; double bestScore = -1e9;
        // (the patch: what lies within 0.75 R of its centre counts - the lobe's outer quarter fades into the sea)
        double rs = R * 0.75;
        for (int pass = 0; pass < 2 && best == null; pass++)
            for (double cz = Math.Ceiling(z0 / 24) * 24; cz <= z1; cz += 24)
                for (double cx = Math.Ceiling(x0 / 24) * 24; cx <= x1; cx += 24)
                {
                    if (pass == 0 && used.Any(u => Sq(u.X - cx) + Sq(u.Z - cz) < Sq(R * 0.7))) continue;
                    double range = 0;
                    if (flatR > 0) { range = FlatSpot(md, cx, cz, flatR * 0.75, pass > 0); if (range < 0) continue; }
                    else if (md.HeightAtMap(cx, cz) < water + 1.5) continue;
                    double pen = 0; bool reject = false;
                    foreach (var f in foreign)
                    {
                        double d2 = Sq(f.X - cx) + Sq(f.Y - cz);
                        if (d2 > rs * rs) continue;
                        if (f.W >= 1 && d2 < Sq(flatR > 0 ? flatR + 10 : 30) || (f.Type == "SLOC" && d2 < Sq(flatR > 0 ? 120 : 60))) { reject = true; break; }
                        pen += f.W;
                    }
                    if (reject) continue;
                    // land, slope, materials over the patch
                    int land = 0, steep = 0, n = 0;
                    var mc = new int[8]; int mn = 0;
                    for (double dz = -rs; dz <= rs; dz += 16)
                        for (double dx = -rs; dx <= rs; dx += 16)
                        {
                            if (dx * dx + dz * dz > rs * rs) continue;
                            double x = cx + dx, z = cz + dz, h = md.HeightAtMap(x, z);
                            n++;
                            if (h < water + 0.5) continue;
                            land++;
                            double sl = Math.Max(Math.Abs(md.HeightAtMap(x + 3, z) - md.HeightAtMap(x - 3, z)), Math.Abs(md.HeightAtMap(x, z + 3) - md.HeightAtMap(x, z - 3))) / 6;
                            if (sl > 0.5) steep++;
                            int m = md.MaterialAtMap(x, z); if (m >= 0) { mc[m]++; mn++; }
                        }
                    if (n == 0) continue;
                    double landFrac = (double)land / n, steepFrac = land > 0 ? (double)steep / land : 1;
                    if (landFrac < (pass > 0 ? 0.4 : 0.6) || steepFrac > (pass > 0 ? 0.5 : 0.35)) continue;
                    int mats = 0; for (int m = 0; m < 8; m++) if (mn > 0 && mc[m] >= mn * 0.04) mats++;
                    int veg = natural.Count(p => Sq(p.X - cx) + Sq(p.Y - cz) < rs * rs) + 8 * forest.Count(p => Sq(p.X - cx) + Sq(p.Y - cz) < rs * rs);
                    double score = 1.5 * landFrac - 1.2 * steepFrac + 0.5 * Math.Min(1, veg / 40.0) + 0.4 * Math.Min(1, (mats - 1) / 3.0) - pen / 10 - (flatR > 0 ? range / 30 : 0)
                        // (a little noise: neighbouring lobes do not all take the same best piece)
                        + 0.15 * Noise.Fbm(cx * 0.013 + index, cz * 0.013, seed + 101, 2);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new StampInfo { Seat = g.Seat, SiteX = g.X, SiteZ = g.Z, SX = cx, SZ = cz, R = R, Mirror = mirror, Auto = true, Score = score, Rot = rot };
                    }
                }
        return best;
    }

    /// <summary>the grown land's heights: the lobes (their stamps or generated hills) melted into one landmass with the
    /// old map's land. Writes H (only upwards: land is added, nothing dug), and the dominant stamp per sample (domK /
    /// domW: the ground materials and the content of the patches follow it); joinD: per sample the distance (m) from the
    /// old land whose heights the join carries out (0 on it, 1e6 beyond JOIN); freeOut: per old sample how far the growth
    /// may change it (0 protected .. 1; the joins' smoothing in MapExtension.cs uses both).</summary>
    static void GrowLand(MapData md, float[] H, float[] HB, bool[] kept, bool[] edgeLand, int hx, int hy, int ox, int oy, double wn, int seed,
        List<CoopGrow> lobes, int[] lobeStamp, List<StampInfo> stamps, sbyte[] domK, float[] domW, List<CoopKeep> keep, MapExtensionInfo info, float[] grown,
        float[] joinD, float[] freeOut)
    {
        double water = md.Water;
        int gw = (hx + 1) / 2, gh = (hy + 1) / 2;
        // ---- the old land's heights carried outwards (relative to the water, on a 4 m grid): the join's target near the
        // old coast. Old land (and the old edge's land going on) keeps its own; each ring of sea cells around it takes the
        // mean of its neighbours that have one, ring by ring, out to JOIN m.
        var C = new float[gw * gh]; var has = new bool[gw * gh];
        for (int b = 0; b < gh; b++)
            for (int a = 0; a < gw; a++)
            {
                int k = Math.Min(hy - 1, 2 * b) * hx + Math.Min(hx - 1, 2 * a);
                if ((kept[k] || edgeLand[k]) && HB[k] >= water - 0.3) { C[b * gw + a] = (float)Math.Clamp(HB[k] - water, 0.4, 40); has[b * gw + a] = true; }
            }
        var dist = new float[gw * gh];
        for (int k = 0; k < dist.Length; k++) dist[k] = has[k] ? 0 : 1e6f;
        int rings = (int)(JOIN / 4);
        for (int it = 1; it <= rings; it++)
        {
            var add = new List<(int K, float V)>();
            for (int b = 0; b < gh; b++)
                for (int a = 0; a < gw; a++)
                {
                    int k = b * gw + a;
                    if (has[k]) continue;
                    double sum = 0; int cnt = 0;
                    for (int db = -1; db <= 1; db++) for (int da = -1; da <= 1; da++)
                    {
                        int aa = a + da, bb = b + db;
                        if (aa < 0 || bb < 0 || aa >= gw || bb >= gh) continue;
                        int q = bb * gw + aa;
                        if (has[q] && dist[q] < it) { sum += C[q]; cnt++; }
                    }
                    if (cnt > 0) add.Add((k, (float)(sum / cnt)));
                }
            foreach (var (k, v) in add) { C[k] = v; has[k] = true; dist[k] = it; }
        }
        // (bilinear between the 4 m cells that have a value: no steps)
        double Cont(double x, double y, out double d)
        {
            double fx = Math.Clamp((x - ox) / 4, 0, gw - 1.001), fy = Math.Clamp((y - oy) / 4, 0, gh - 1.001);
            int a = (int)Math.Floor(fx), b = (int)Math.Floor(fy); double u = fx - a, v = fy - b;
            double sw = 0, sv = 0, sd = 0;
            void Add(int aa, int bb, double w) { int k = bb * gw + aa; if (!has[k] || w <= 0) return; sw += w; sv += w * C[k]; sd += w * dist[k] * 4; }
            Add(a, b, (1 - u) * (1 - v)); Add(a + 1, b, u * (1 - v)); Add(a, b + 1, (1 - u) * v); Add(a + 1, b + 1, u * v);
            if (sw < 0.5) { d = 1e6; return 0; }
            d = sd / sw;
            return sv / sw;
        }
        // ---- the protected old map: 40 m around the mission's objects (not the natural ones a stamp copies), the `keep`
        // zones; a factor 0 (protected) .. 1 (free) over the next 30 m
        // (what stands on levelled or built ground - buildings, walls, gates, ruins and other placed pieces - keeps 40 m;
        // units, start locations and nests stand on natural ground and keep PROTECT_UNIT: the shore in front of a
        // start may become land, the start itself and its ground stay)
        var PD = new float[gw * gh];
        for (int k = 0; k < PD.Length; k++) PD[k] = 1e6f;
        foreach (var o in md.Objects)
        {
            double pr = ProtectRadius(o);
            if (pr <= 0) continue;
            int a = (int)Math.Floor((o.X - ox) / 4 + 0.5), b = (int)Math.Floor((o.Y - oy) / 4 + 0.5);
            if (a >= 0 && b >= 0 && a < gw && b < gh) PD[b * gw + a] = Math.Min(PD[b * gw + a], (float)(PROTECT - pr));
        }
        Chamfer(PD, gw, gh);
        foreach (var z in keep)
        {
            var (i0, i1, j0, j1) = ((int)Math.Floor((z.X - z.R - 40 - ox) / 4), (int)Math.Ceiling((z.X + z.R + 40 - ox) / 4), (int)Math.Floor((z.Z - z.R - 40 - oy) / 4), (int)Math.Ceiling((z.Z + z.R + 40 - oy) / 4));
            for (int b = Math.Max(0, j0); b <= Math.Min(gh - 1, j1); b++)
                for (int a = Math.Max(0, i0); a <= Math.Min(gw - 1, i1); a++)
                {
                    double d = Math.Sqrt(Sq(ox + 4.0 * a - z.X) + Sq(oy + 4.0 * b - z.Z)) - z.R + PROTECT;
                    if (d < PD[b * gw + a]) PD[b * gw + a] = (float)Math.Max(0, d);
                }
        }
        double Free(double x, double y)
        {
            double fx = Math.Clamp((x - ox) / 4, 0, gw - 1.001), fy = Math.Clamp((y - oy) / 4, 0, gh - 1.001);
            int a = (int)Math.Floor(fx), b = (int)Math.Floor(fy); double u = fx - a, v = fy - b;
            double pd = (PD[b * gw + a] * (1 - u) + PD[b * gw + a + 1] * u) * (1 - v) + (PD[(b + 1) * gw + a] * (1 - u) + PD[(b + 1) * gw + a + 1] * u) * v;
            return S(PROTECT, PROTECT + 30, pd);
        }
        for (int j = 0; j < hy; j++)
            for (int i = 0; i < hx; i++)
            {
                int k = j * hx + i;
                double mx = ox + 2.0 * i, my = oy + 2.0 * j;
                if (kept[k]) freeOut[k] = (float)Free(mx, my);
                Cont(mx, my, out double cd);
                joinD[k] = (float)cd;
            }
        // ---- the lobes
        int nl = lobes.Count;
        var nzSeed = new int[nl];
        for (int li = 0; li < nl; li++) nzSeed[li] = seed + 211 + li * 37;
        double x0 = lobes.Min(l => l.X - l.R) - 4, x1 = lobes.Max(l => l.X + l.R) + 4, y0 = lobes.Min(l => l.Z - l.R) - 4, y1 = lobes.Max(l => l.Z + l.R) + 4;
        int I0 = Math.Max(0, (int)Math.Floor((x0 - ox) / 2)), I1 = Math.Min(hx - 1, (int)Math.Ceiling((x1 - ox) / 2));
        int J0 = Math.Max(0, (int)Math.Floor((y0 - oy) / 2)), J1 = Math.Min(hy - 1, (int)Math.Ceiling((y1 - oy) / 2));
        double X1 = ox + 2.0 * (hx - 1), Y1 = oy + 2.0 * (hy - 1);
        var wt = new double[nl];
        int raised = 0, shore = 0;
        for (int j = J0; j <= J1; j++)
            for (int i = I0; i <= I1; i++)
            {
                int k = j * hx + i;
                // (the old map's land above the shore never changes)
                if (kept[k] && HB[k] >= water + SHORE) continue;
                double mx = ox + 2.0 * i, my = oy + 2.0 * j;
                double sumW = 0, prod = 1;
                for (int li = 0; li < nl; li++)
                {
                    var l = lobes[li];
                    double d = Math.Sqrt(Sq(mx - l.X) + Sq(my - l.Z));
                    if (d > l.R) { wt[li] = 0; continue; }
                    // (a noise-bent edge: bays and capes, not a circle)
                    double nz = Math.Clamp((Noise.Fbm(mx * 0.006, my * 0.006, nzSeed[li], 3) - 0.5) * 4, -0.8, 0.8) + (Noise.Fbm(mx * 0.02, my * 0.02, nzSeed[li] + 2, 2) - 0.5) * 0.6;
                    double w = (1 - S(l.R * 0.45, l.R * 0.98, d + nz * l.R * 0.32)) * (1 - S(l.R * 0.92, l.R, d));
                    wt[li] = w; sumW += w; prod *= 1 - w;
                }
                if (sumW <= 1e-4) continue;
                double U = 1 - prod;
                // (a `keep` zone: no new land in it either - an islet stays an islet, a channel stays open)
                foreach (var z in keep)
                {
                    double dz = Math.Sqrt(Sq(mx - z.X) + Sq(my - z.Z)) - z.R;
                    if (dz < 40) U *= S(0, 40, dz);
                }
                // (the map's border: the land fades before it)
                double border = Math.Min(Math.Min(mx - ox, X1 - mx), Math.Min(my - oy, Y1 - my));
                double bn = (Noise.Fbm(mx * 0.01, my * 0.01, seed + 223, 2) - 0.5) * 40;
                U *= S(24, 80, border + bn);
                if (U <= 1e-4) continue;
                grown[k] = (float)U;
                // the lobes' heights (relative to the water), by weight
                double rel = 0, bestW = 0; int bestS = -1;
                for (int li = 0; li < nl; li++)
                {
                    if (wt[li] <= 0) continue;
                    var l = lobes[li];
                    double r;
                    int si = lobeStamp[li];
                    if (si >= 0)
                    {
                        var st = stamps[si];
                        var (qx, qz) = st.ToSource(mx, my);
                        r = md.HeightAtMap(qx, qz) - water;
                        // (the old map's 32 m frame - the editor's border - is no landscape: low land instead)
                        double se = Math.Min(Math.Min(qx - md.OffX, md.OffX + (md.Hx - 1) * 2 - qx), Math.Min(qz - md.OffY, md.OffY + (md.Hy - 1) * 2 - qz));
                        r = Lerp(2.0, r, S(36, 72, se));
                        if (wt[li] > bestW) { bestW = wt[li]; bestS = si; }
                    }
                    else
                    {
                        double hill = Noise.Fbm(mx * 0.007, my * 0.007, seed + 11 + li, 4);
                        r = 3.2 + hill * hill * 24 + 1.2 * Noise.Fbm(mx * 0.05, my * 0.05, seed + 5, 2);
                    }
                    // (inside a lobe the patch's own coves and lakes are low land: the lobe's edge is the coast)
                    // (meadow height: 2.5 .. 5.5 m over the water - beach only where the land meets the sea)
                    double floor = 2.5 + 3.0 * Noise.Fbm(mx * 0.02, my * 0.02, seed + 227, 2);
                    if (r < floor) r = Lerp(r, floor, S(0.25, 0.7, wt[li]));
                    rel += wt[li] * r;
                }
                rel /= sumW;
                // (high ground lower than in the patch: the grown land is hills and meadows to build on, its rocky tops
                // come down to walkable slopes)
                if (rel > 6) rel = 6 + (rel - 6) * 0.5;
                // the join: near the old land its own heights, carried out (no step at the old coast)
                double cv = Cont(mx, my, out double cd);
                if (cd < JOIN)
                {
                    double jn = (Noise.Fbm(mx * 0.03, my * 0.03, seed + 229, 2) - 0.5) * 30;
                    rel = Lerp(Math.Max(cv, 0.8), rel, S(0, JOIN - 10, cd + jn));
                }
                double cur = H[k];
                // land rises late (a beach where it fades), the sea floor follows early: continuous at the water line
                double g = S(0, 0.45, U);
                double f = wn + (rel > 0 ? rel * S(0.3, 1, U) : rel * g) + (cur - wn) * (1 - g);
                double free = kept[k] ? Free(mx, my) : 1;
                if (free <= 0) continue;
                if (kept[k] && HB[k] >= water - 0.3)
                {
                    // an old beach under the lobes: it rises a little and goes over into the new land's meadow
                    double target = Math.Max(cur, wn + Math.Max(rel, cur - wn));
                    double nh = Lerp(cur, target, S(0.55, 0.9, U) * free * 0.85);
                    if (nh > cur + 1e-4) { H[k] = (float)nh; shore++; }
                }
                else
                {
                    f = Lerp(cur, f, free);
                    if (f > cur) { H[k] = (float)f; raised++; }
                }
                if (bestS >= 0)
                {
                    double dw = bestW * U;
                    if (dw > domW[k]) { domW[k] = (float)dw; domK[k] = (sbyte)bestS; }
                }
            }
        // ---- the grown land smoothed (two 3 x 3 passes over the samples it raised): the patches' rock and the blend of
        // several patches leave a rough surface - the land of the original maps is rolling ground
        var changed = new bool[hx * hy];
        for (int j = J0; j <= J1; j++) for (int i = I0; i <= I1; i++) { int k = j * hx + i; if (grown[k] > 0 && H[k] > HB[k] + 1e-3 && !(kept[k] && HB[k] >= water - 0.3)) changed[k] = true; }
        for (int pass = 0; pass < 2; pass++)
        {
            var H2 = (float[])H.Clone();
            for (int j = Math.Max(1, J0); j <= Math.Min(hy - 2, J1); j++)
                for (int i = Math.Max(1, I0); i <= Math.Min(hx - 2, I1); i++)
                {
                    int k = j * hx + i;
                    if (!changed[k]) continue;
                    double sum = 0;
                    for (int b = -1; b <= 1; b++) for (int a = -1; a <= 1; a++) sum += H2[k + b * hx + a];
                    double v = sum / 9;
                    // (never below the ground before: the old map's sea floor and the old edge stay under it)
                    H[k] = (float)Math.Max(HB[k], H2[k] + (v - H2[k]) * Math.Min(1, grown[k] * 2));
                }
        }
        info.Log.Add($"grown: {nl} lobes, {raised} samples raised, {shore} old shore samples blended");
    }
}
