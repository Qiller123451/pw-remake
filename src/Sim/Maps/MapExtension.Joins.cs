// Co-op maps, style "grow": the grown land really joins (docs/COOP.md "Joins").
//
// The lobes of grown land (`extend.grow`, the seats' base sites, the enemy bases in new land) are meant to make one
// landmass with the old map's land. Their land weights are noise-bent discs whose edges fade into the sea, and where two
// patches of the old map meet their heights are averaged - so two lobes that touch only with their beaches leave a strait
// between them, and two patches of different height leave a ridge too steep to walk. On the walk grid (and so for the
// computer players' "islands", World.LandIsland, which the AI's waves cross by ferry) such a lobe is land of its own:
// base 06's raiders' cove, three lobes south-east of seat 3, was three islands cut off from seat 3's land by a strait
// and two ridges (its waves found no way by land and ended "no transports"). (The same with the 4 m ground before
// main af44116: the walk grid is the extension's own.)
//
// Joins checks, on the walk grid of a made map (4-neighbourhood: World.LandIsland's), every seat's base site and every
// enemy base whose waves walk (CoopSite.Waves): is its part joined to the old map's land (a part with JOIN_OLD_CELLS of
// it)? If not, a search from the joined land that walks the land for free and pays for every cell of water or steep
// ground finds the way that crosses least (through the lobes between: a strait, a lobe, a ridge, a lobe ...), not through
// the old map's land above the shore nor the keep zones, at most JOIN_MAX m of crossing; every crossing gets a graded
// path (CoopBridge shape "path", width 12, cut 8). Apply then makes the map again with those paths (at most three
// rounds). A lobe with nothing on it may stay an island of its own (scenery), so may an enemy base without waves. Plain
// integer and +-*/sqrt arithmetic: deterministic.
using System;
using System.Collections.Generic;
using System.Linq;

namespace PwRemake.Sim.Maps;

public static partial class MapExtension
{
    /// <summary>a part of the walk grid counts as the old map's land with this many cells of it (2 m: 8000 m2) - a
    /// strip of old beach a lobe touches does not</summary>
    const int JOIN_OLD_CELLS = 2000;
    /// <summary>the longest join (m)</summary>
    const double JOIN_MAX = 160;

    /// <summary>the paths that join the cut-off parts of the grown land of `o` (Extend's result of `md`) to the land joined
    /// to the old map's; empty when everything is joined. `log` gets a line per join (and per part it cannot join).</summary>
    internal static List<CoopBridge> Joins(MapData md, MapData o, CoopExtend e, IReadOnlyList<CoopSite> sites, List<string> log)
    {
        var res = new List<CoopBridge>();
        var wg = o.Walk; var info = o.Extension;
        if (wg == null || info == null) return res;
        int W = wg.W, H = wg.H, N = W * H;
        double water = md.Water;
        double CX(int k) => wg.OffX + 2.0 * (k % W) + 1;
        double CY(int k) => wg.OffY + 2.0 * (k / W) + 1;
        // the old map's land above the shore (walkable in its own grid)
        bool OldLand(double x, double y) => info.InOld(x, y) && md.HeightAtMap(x, y) >= water + 0.5;
        // ---- the parts (4-neighbourhood, as World.LandIsland)
        var lab = new int[N]; Array.Fill(lab, -1);
        var size = new List<int>(); var old = new List<int>();
        var st = new Stack<int>();
        for (int k0 = 0; k0 < N; k0++)
        {
            if (lab[k0] >= 0 || wg.Cells[k0] != 1) continue;
            int id = size.Count; size.Add(0); old.Add(0);
            lab[k0] = id; st.Push(k0);
            while (st.Count > 0)
            {
                int k = st.Pop(), i = k % W;
                size[id]++;
                if (OldLand(CX(k), CY(k)) && md.WalkableMap(CX(k), CY(k)) != false) old[id]++;
                if (i > 0 && lab[k - 1] < 0 && wg.Cells[k - 1] == 1) { lab[k - 1] = id; st.Push(k - 1); }
                if (i < W - 1 && lab[k + 1] < 0 && wg.Cells[k + 1] == 1) { lab[k + 1] = id; st.Push(k + 1); }
                if (k >= W && lab[k - W] < 0 && wg.Cells[k - W] == 1) { lab[k - W] = id; st.Push(k - W); }
                if (k + W < N && lab[k + W] < 0 && wg.Cells[k + W] == 1) { lab[k + W] = id; st.Push(k + W); }
            }
        }
        var good = new bool[size.Count];
        for (int c = 0; c < size.Count; c++) good[c] = old[c] >= JOIN_OLD_CELLS;
        if (!good.Any(g => g)) return res;
        int CellAt(double x, double y)
        {
            int i = (int)Math.Floor((x - wg.OffX) / 2), j = (int)Math.Floor((y - wg.OffY) / 2);
            return i < 0 || j < 0 || i >= W || j >= H ? -1 : j * W + i;
        }
        // ---- the anchors: the sites' centres - the seats' base sites and the enemy bases whose waves walk to the seats
        // (CoopSite.Waves; the nearest walkable cell within 24 m). (A lobe with nothing on it may stay an island of its own - its land is scenery; a
        // lobe a site's way leads through is joined on the way.)
        var pending = new List<int>(); var what = new Dictionary<int, string>();
        void Anchor(double x, double y, string name)
        {
            int best = -1; double bd = 1e9;
            for (double dy = -24; dy <= 24; dy += 2)
                for (double dx = -24; dx <= 24; dx += 2)
                {
                    double d = dx * dx + dy * dy;
                    if (d > 576 || d >= bd) continue;
                    int k = CellAt(x + dx, y + dy);
                    if (k >= 0 && lab[k] >= 0) { best = k; bd = d; }
                }
            if (best < 0) return;
            int c = lab[best];
            if (good[c] || pending.Contains(c)) return;
            if (old[c] > 0) { log.Add($"join: {name} is on land with {old[c]} cells of the old map's, not joined to its main land - left as it is"); return; }
            pending.Add(c); what[c] = name;
        }
        foreach (var q in sites.Where(q => q.Ally == null && (!q.Enemy || q.Waves))) Anchor(q.X, q.Z, q.Enemy ? $"enemy site ({q.X:0}, {q.Z:0})" : $"seat {q.Seat}'s site ({q.X:0}, {q.Z:0})");
        if (pending.Count == 0) return res;
        // ---- where a path may go: not through the old map's land above the shore, not into the keep zones
        var barred = new bool[N];
        for (int k = 0; k < N; k++)
        {
            double x = CX(k), y = CY(k);
            if (OldLand(x, y)) { barred[k] = true; continue; }
            foreach (var z in e.Keep) if (Sq(x - z.X) + Sq(y - z.Z) < Sq(z.R + 16)) { barred[k] = true; break; }
        }
        // ---- from the joined land outwards to the nearest cut-off site: a search that walks the land for free and
        // pays for every cell of water or steep ground (4-neighbourhood, as World.LandIsland) - the way that crosses the
        // least, through the lobes between (a strait, a lobe, a ridge, a lobe ...); every crossing gets a path, and the
        // site's part and the parts on the way are joined from then on
        var src = new bool[N];
        for (int k = 0; k < N; k++) if (lab[k] >= 0 && good[lab[k]] && !barred[k]) src[k] = true;
        int maxGap = (int)(JOIN_MAX / 2);
        while (pending.Count > 0)
        {
            var cost = new int[N]; Array.Fill(cost, int.MaxValue);
            var par = new int[N];
            var dq = new LinkedList<int>();
            for (int k = 0; k < N; k++) if (src[k]) { cost[k] = 0; par[k] = -1; dq.AddLast(k); }
            int hit = -1;
            while (dq.Count > 0)
            {
                int k = dq.First!.Value; dq.RemoveFirst();
                if (lab[k] >= 0 && pending.Contains(lab[k])) { hit = k; break; }
                int i = k % W;
                for (int m = 0; m < 4; m++)
                {
                    int k2 = m == 0 ? (i > 0 ? k - 1 : -1) : m == 1 ? (i < W - 1 ? k + 1 : -1) : m == 2 ? k - W : k + W;
                    if (k2 < 0 || k2 >= N || barred[k2]) continue;
                    int c2 = cost[k] + (wg.Cells[k2] == 1 ? 0 : 1);
                    if (c2 >= cost[k2] || c2 > maxGap) continue;
                    cost[k2] = c2; par[k2] = k;
                    if (wg.Cells[k2] == 1) dq.AddFirst(k2); else dq.AddLast(k2);
                }
            }
            if (hit < 0)
            {
                foreach (var c in pending) log.Add($"join: {what[c]} ({size[c]} cells) is cut off and no way crossing less than {JOIN_MAX:0} m joins it");
                break;
            }
            int comp = lab[hit];
            // the way from the site's part (hit) back to the joined land, and its crossings (runs of cells not walkable)
            var trace = new List<int>();
            for (int k = hit; k >= 0; k = par[k]) trace.Add(k);
            var joined = new HashSet<int> { comp };
            for (int t = 1; t < trace.Count; t++)
            {
                if (wg.Cells[trace[t]] == 1) { if (lab[trace[t]] >= 0) joined.Add(lab[trace[t]]); continue; }
                int a = t - 1, b = t;
                while (b + 1 < trace.Count && wg.Cells[trace[b + 1]] != 1) b++;
                int pk = trace[a], qk = b + 1 < trace.Count ? trace[b + 1] : trace[b];
                res.Add(PathAcross(pk, qk, trace.GetRange(a, Math.Min(trace.Count, b + 2) - a)));
                log.Add($"join: {what[comp]} cut off - a path across {2 * (b - a + 1)} m from ({CX(pk):0}, {CY(pk):0}) to ({CX(qk):0}, {CY(qk):0})");
                t = b;
            }
            // the parts on the way are joined now: the search goes on from them too
            foreach (var c in joined) pending.Remove(c);
            for (int k = 0; k < N; k++) if (lab[k] >= 0 && joined.Contains(lab[k]) && !barred[k]) src[k] = true;
        }
        return res;

        // a path across a crossing: from p (land on the site's side) to q (the land beyond); it begins up to 20 m inside
        // p's land (room for its ramp) and Spines carries it 24 m into q's
        CoopBridge PathAcross(int pk, int qk, List<int> cells)
        {
            double px = CX(pk), py = CY(pk), qx = CX(qk), qy = CY(qk);
            double len = Math.Sqrt(Sq(qx - px) + Sq(qy - py));
            double ux = len > 0 ? (px - qx) / len : 1, uy = len > 0 ? (py - qy) / len : 0;
            double sx = px, sy = py;
            for (double s = 2; s <= 20; s += 2)
            {
                int k = CellAt(px + ux * s, py + uy * s);
                if (k < 0 || lab[k] != lab[pk]) break;
                sx = px + ux * s; sy = py + uy * s;
            }
            var pts = new List<CoopPoint> { new(sx, sy) };
            if (Sq(sx - px) + Sq(sy - py) > 4) pts.Add(new CoopPoint(px, py));
            double acc = 0;
            for (int t = 1; t + 1 < cells.Count; t++)
            {
                acc += 2;
                if (acc >= 12 && Math.Sqrt(Sq(CX(cells[t]) - qx) + Sq(CY(cells[t]) - qy)) > 6) { pts.Add(new CoopPoint(CX(cells[t]), CY(cells[t]))); acc = 0; }
            }
            // (and on past q in the crossing's direction, so that Spines carries it on into the land beyond)
            pts.Add(new CoopPoint(qx, qy));
            return new CoopBridge { Points = pts, Width = 12, Cut = 8, Shape = "path" };
        }
    }
}
