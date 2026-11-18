// The Dragon Clan's mine belts (goals/AiGoalBuildMineBelts.usl; the positions: the server's CAiMineMgr and its shore
// map, PWServer.exe 0x458de0 / 0x4f0b20 / 0x4f0d50 / 0x4ecce0 - _notes/re_minemgr.md, docs/spec/ai.md §7.4).
//
//   * When: a Ninigi computer player on a multiplayer map with water (map option watermap), from epoch 2
//     (AiModuleDefenseDefault.usl:660-669). The goal thinks every 300 + difficulty ticks for Dodo, Giraffe and
//     Schnecke, else every 50 + difficulty ticks.
//   * A belt: one minelayer (an idle one, else trained at the harbour), 50 wood and 50 stone per mine; the five free
//     positions of the first shore that still has free ones are ordered at once (queued); when the layer is idle
//     again the next belt follows. A position is taken when any mine stands within 6 m of it.
//   * The positions are worked out once (the original: at level load, per start location): the largest body of
//     water (none if it covers less than 9.5 % of the map) eroded by 8 m gives a 20 m lattice; the home island's
//     coast is cut into clusters (joined within 120 m of a cluster's corner, at least 20 m long); a cluster whose
//     land path to the start location is at most 400 m is a shore; its mines are the lattice points nearest to the
//     ring 30-32 m out from it. Shores with the shortest path first.
//   The remake's land and ship grids do not overlap as the original's do (land units wade 0.4 m deep, ships need
//   1.5 m), so the island is the ground no ship can sail that holds the start location, the coast a strip two cells
//   wide of the water along it, and the path a distance over the island (its unwalkable parts count four times).
//
// Ported from remake/src/game/ai/mines.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Ai;

/// <summary>a shore of the home island and its mine positions</summary>
public sealed class MineShore
{
    public double X, Z, Distance;
    public List<Pt> Mines = new();
}

/// <summary>what the shore search saw (tests, debugging)</summary>
public sealed class MineDebug
{
    public double Water;
    public int Lattice, Island, Coast, Reach;
    public List<int[]> Clusters = new();
    public List<double> Len = new();
}

public static class MineShores
{
    const string MINE = "ninigi_mineship_mine";
    const double MIN_WATER = 0.095;
    const int ERODE = 4, STEP = 10, MINBLOB = 10, SPAN = 60, MIN_EXTENT = 10, RING = 15;
    const double MAX_PATH = 400;

    /// <summary>the shores and mine positions of a start location (cells of 2 m; i along x, j along z)</summary>
    public static List<MineShore> Find(W_ W, double sx, double sz, MineDebug? dbg = null)
    {
        var L = W.Nav; int n = L.N, N = n * n; double cs = L.Cell, half = L.Half;
        if (W.WaterLevel == null || W.WaterNav == null) return new List<MineShore>();
        var P = W.Play; double wl = W.WaterLevel.Value;
        var water = new byte[N]; var inPlay = new byte[N];
        for (int k = 0; k < N; k++)
        {
            var (x, z) = L.Center(k);
            if (x < P.X0 + 2 || x > P.X1 - 2 || z < P.Z0 + 2 || z > P.Z1 - 2) continue;
            inPlay[k] = 1;
            if (wl - W.Height(x, z) >= 1.5) water[k] = 1;
        }
        var deepAny = (byte[])water.Clone();
        IEnumerable<int> Nb4(int k) { int i = k % n; foreach (var m in new[] { i > 0 ? k - 1 : -1, i < n - 1 ? k + 1 : -1, k - n, k + n }) if (m >= 0 && m < N) yield return m; }
        // the largest body of water (4-connected)
        var comp = new int[N]; Array.Fill(comp, -1);
        var st = new Stack<int>();
        int best = -1, bestN = 0, id = 0;
        for (int k0 = 0; k0 < N; k0++)
        {
            if (water[k0] == 0 || comp[k0] >= 0) continue;
            int cnt = 0; comp[k0] = id; st.Push(k0);
            while (st.Count > 0)
            {
                int k = st.Pop(); cnt++;
                foreach (var m in Nb4(k)) if (water[m] != 0 && comp[m] < 0) { comp[m] = id; st.Push(m); }
            }
            if (cnt > bestN) { bestN = cnt; best = id; }
            id++;
        }
        if (dbg != null) dbg.Water = (double)bestN / N;
        if ((double)bestN / N < MIN_WATER) return new List<MineShore>();
        for (int k = 0; k < N; k++) if (water[k] != 0 && comp[k] != best) water[k] = 0;
        // the lattice: water at least 8 m from anything else, every 10th cell
        var lattice = new List<(int I, int J)>();
        for (int j = 0; j < n; j += STEP) for (int i = 0; i < n; i += STEP)
        {
            bool ok = water[j * n + i] != 0;
            for (int dj = -ERODE; ok && dj <= ERODE; dj++) for (int di = -ERODE; di <= ERODE; di++)
            {
                if (di * di + dj * dj > ERODE * ERODE) continue;
                int a = i + di, b = j + dj;
                if (a < 0 || b < 0 || a >= n || b >= n) continue;            // (the map's edge does not erode)
                if (water[b * n + a] == 0) { ok = false; break; }
            }
            if (ok) lattice.Add((i, j));
        }
        if (dbg != null) dbg.Lattice = lattice.Count;
        // the home island: the ground (no water a ship could sail) connected to the start location; its distances from
        // the start (8-neighbour Dijkstra, metres; ground a land unit cannot walk - steep shore, shallows - counts four times)
        int s0 = L.Idx(sx, sz);
        if (deepAny[s0] != 0) return new List<MineShore>();
        var ground = new byte[N];
        ground[s0] = 1; st.Push(s0);
        while (st.Count > 0)
        {
            int k = st.Pop();
            foreach (var m in Nb4(k)) if (ground[m] == 0 && deepAny[m] == 0 && inPlay[m] != 0) { ground[m] = 1; st.Push(m); }
        }
        int isl = W.LandIsland(sx, sz);
        bool Walk(int k) => isl >= 0 && W.IslandCell(k) == isl;
        var dist = new double[N]; Array.Fill(dist, double.PositiveInfinity);
        dist[s0] = 0;
        var heap = new List<(double D, int K)> { (0, s0) };
        void Push(double d, int k)
        {
            heap.Add((d, k)); int c = heap.Count - 1;
            while (c > 0) { int p = (c - 1) >> 1; if (heap[p].D <= heap[c].D) break; (heap[p], heap[c]) = (heap[c], heap[p]); c = p; }
        }
        (double D, int K) Pop()
        {
            var top = heap[0]; var last = heap[^1]; heap.RemoveAt(heap.Count - 1);
            if (heap.Count > 0)
            {
                heap[0] = last; int c = 0;
                for (; ; )
                {
                    int l = 2 * c + 1, r = l + 1, m = c;
                    if (l < heap.Count && heap[l].D < heap[m].D) m = l;
                    if (r < heap.Count && heap[r].D < heap[m].D) m = r;
                    if (m == c) break;
                    (heap[m], heap[c]) = (heap[c], heap[m]); c = m;
                }
            }
            return top;
        }
        while (heap.Count > 0)
        {
            var (d, k) = Pop();
            if (d > dist[k] || d > MAX_PATH) continue;
            int i = k % n, j = (k - i) / n;
            for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
            {
                if (di == 0 && dj == 0) continue;
                int a = i + di, b = j + dj;
                if (a < 0 || b < 0 || a >= n || b >= n) continue;
                int m = b * n + a;
                if (ground[m] == 0) continue;
                double nd = d + (di != 0 && dj != 0 ? cs * Math.Sqrt(2) : cs) * (Walk(m) ? 1 : 4);
                if (nd < dist[m]) { dist[m] = nd; Push(nd, m); }
            }
        }
        // the coast: a strip two cells wide of the main water along the island; specks under 10 cells dropped
        var coast = new byte[N];
        for (int k = 0; k < N; k++) if (water[k] != 0 && Nb4(k).Any(m => ground[m] != 0)) coast[k] = 1;
        for (int k = 0; k < N; k++) if (coast[k] == 1) foreach (var m in Nb4(k)) if (water[m] != 0 && coast[m] == 0) coast[m] = 2;
        var seen = new byte[N];
        for (int k0 = 0; k0 < N; k0++)
        {
            if (coast[k0] == 0 || seen[k0] != 0) continue;
            var part = new List<int> { k0 }; seen[k0] = 1;
            for (int q = 0; q < part.Count; q++) foreach (var m in Nb4(part[q])) if (coast[m] != 0 && seen[m] == 0) { seen[m] = 1; part.Add(m); }
            if (part.Count < MINBLOB) foreach (var k in part) coast[k] = 0;
        }
        // clusters: row by row, a cell joins the first cluster whose box corner is within 60 cells
        var clusters = new List<(int[] Box, List<(int I, int J)> Pts)>();
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            if (coast[j * n + i] == 0) continue;
            int ci = clusters.FindIndex(c => Math.Abs(i - c.Box[0]) <= SPAN && Math.Abs(j - c.Box[1]) <= SPAN);
            if (ci < 0) { clusters.Add((new[] { i, j, i, j }, new List<(int, int)>())); ci = clusters.Count - 1; }
            var cl = clusters[ci];
            cl.Pts.Add((i, j));
            cl.Box[0] = Math.Min(cl.Box[0], i); cl.Box[1] = Math.Min(cl.Box[1], j); cl.Box[2] = Math.Max(cl.Box[2], i); cl.Box[3] = Math.Max(cl.Box[3], j);
        }
        if (dbg != null)
        {
            dbg.Island = isl; dbg.Coast = coast.Count(v => v > 0);
            dbg.Clusters = clusters.Select(c => new[] { c.Box[0], c.Box[1], c.Box[2], c.Box[3], c.Pts.Count }).ToList();
            dbg.Reach = dist.Count(v => v < double.PositiveInfinity);
        }
        var shores = new List<MineShore>();
        foreach (var (box, pts) in clusters)
        {
            int minx = box[0], miny = box[1], maxx = box[2], maxy = box[3];
            if (maxx - minx < MIN_EXTENT && maxy - miny < MIN_EXTENT) continue;
            int mx = (minx + maxx) >> 1, my = (miny + maxy) >> 1;
            var rep = pts[0]; int bd = int.MaxValue;
            foreach (var p in pts) { int d = Math.Abs(p.I - mx) + Math.Abs(p.J - my); if (d < bd) { bd = d; rep = p; } }
            // the land path from the shore to the start location
            double len = double.PositiveInfinity;
            for (int dj = -3; dj <= 3; dj++) for (int di = -3; di <= 3; di++)
            {
                int a = rep.I + di, b = rep.J + dj;
                if (a < 0 || b < 0 || a >= n || b >= n) continue;
                double d = dist[b * n + a] + Math.Sqrt(di * di + dj * dj) * cs;
                if (d < len) len = d;
            }
            dbg?.Len.Add(Math.Floor(len + 0.5));
            if (!(len <= MAX_PATH)) continue;
            // the ring 15-16 cells out from the cluster (chamfer distance in a window around it)
            int x0 = Math.Max(0, minx - RING - 3), x1 = Math.Min(n - 1, maxx + RING + 3), y0 = Math.Max(0, miny - RING - 3), y1 = Math.Min(n - 1, maxy + RING + 3);
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            var D = new float[w * h]; Array.Fill(D, 1e9f);
            foreach (var (i, j) in pts) D[(j - y0) * w + (i - x0)] = 0;
            double R2 = Math.Sqrt(2);
            for (int b = 0; b < h; b++) for (int a = 0; a < w; a++)
            {
                int k = b * w + a; double v = D[k];
                if (a > 0) v = Math.Min(v, D[k - 1] + 1.0);
                if (b > 0) { v = Math.Min(v, D[k - w] + 1.0); if (a > 0) v = Math.Min(v, D[k - w - 1] + R2); if (a < w - 1) v = Math.Min(v, D[k - w + 1] + R2); }
                D[k] = (float)v;
            }
            for (int b = h - 1; b >= 0; b--) for (int a = w - 1; a >= 0; a--)
            {
                int k = b * w + a; double v = D[k];
                if (a < w - 1) v = Math.Min(v, D[k + 1] + 1.0);
                if (b < h - 1) { v = Math.Min(v, D[k + w] + 1.0); if (a < w - 1) v = Math.Min(v, D[k + w + 1] + R2); if (a > 0) v = Math.Min(v, D[k + w - 1] + R2); }
                D[k] = (float)v;
            }
            var mines = new List<Pt>(); var key = new HashSet<long>();
            for (int b = 0; b < h; b++) for (int a = 0; a < w; a++)
            {
                double d = D[b * w + a]; int i = a + x0, j = b + y0;
                if (!(d > RING && d <= RING + 1.2) || water[j * n + i] == 0) continue;
                (int I, int J)? lp = null; double ld = 1e6;
                foreach (var q in lattice) { double e = (double)(q.I - i) * (q.I - i) + (double)(q.J - j) * (q.J - j); if (e < ld) { ld = e; lp = q; } }
                if (lp == null || key.Contains(lp.Value.I * 100003L + lp.Value.J)) continue;
                key.Add(lp.Value.I * 100003L + lp.Value.J);
                mines.Add(new Pt(lp.Value.I * cs - half, lp.Value.J * cs - half));
            }
            shores.Add(new MineShore { X = rep.I * cs - half, Z = rep.J * cs - half, Distance = len, Mines = mines });
        }
        return shores;
    }
}

/// <summary>a mine position: its shore, its order there, the shore's path length, taken (a mine stands there)</summary>
public sealed class MinePos
{
    public double X, Z, Dist;
    public int Shore, J;
    public bool Taken;
}

/// <summary>the mine belt goal's state</summary>
public sealed class MineGoal : IClaimer
{
    public string State = "ship";
    public Unit? Layer;
    public int Belts;
}

public sealed partial class TribeAI
{
    const string MINE = "ninigi_mineship_mine", LAYER = "ninigi_minelayer";
    const int IN_A_ROW = 5;
    const double TAKEN = 6;

    List<MinePos>? _minePos;
    public List<MineShore>? MineShoreList;
    public MineDebug? MineDbg;
    public MineGoal? MineGoalState;

    /// <summary>AiModuleDefenseDefault.Think: Ninigi on a multiplayer water map from epoch 2</summary>
    bool MineBeltsOn(Census S) => Multimap && MapOptions.Watermap && W.WaterNav != null && P.Tribe == "Ninigi" && S.Age >= 2 && D.Exists(LAYER);
    double MineWait() => ((Personality is "Dodo" or "Giraffe" or "Schnecke" ? 300 : 50) + Difficulty) * TICK;
    /// <summary>CAiMineMgr.Init: every position of every shore, the shortest land path first</summary>
    public List<MinePos>? MinePositions()
    {
        if (_minePos != null) return _minePos;
        var st = Start ?? S?.Home;
        if (st == null) return null;
        var shores = MineShores.Find(W, st.Value.X, st.Value.Z, MineDbg = new MineDebug());
        var outp = new List<MinePos>();
        for (int si = 0; si < shores.Count; si++) for (int j = 0; j < shores[si].Mines.Count; j++) { var m = shores[si].Mines[j]; outp.Add(new MinePos { X = m.X, Z = m.Z, Shore = si, J = j, Dist = shores[si].Distance }); }
        MineShoreList = shores;
        return _minePos = outp.OrderBy(a => a.Dist).ThenBy(a => a.J).ToList();
    }
    /// <summary>UpdateMinePositions + GetNextMineGroup: up to n free positions of the first shore with free ones</summary>
    List<MinePos> NextMineGroup(int n)
    {
        var list = MinePositions() ?? new List<MinePos>();
        var mines = W.Units.Where(u => u.Alive && u.Name == MINE).ToList();
        foreach (var m in list) m.Taken = mines.Any(u => DistP(u, m.X, m.Z) <= TAKEN);
        int shore = -1;
        var outp = new List<MinePos>();
        foreach (var m in list)
        {
            if (outp.Count >= n) break;
            if (m.Taken) continue;
            if (shore < 0) shore = m.Shore;
            if (m.Shore == shore) outp.Add(m);
        }
        return outp;
    }
    ActionDef? MineAction(Unit u) => Acts(u).FirstOrDefault(a => a.Results.Count > 0 && a.Results[0].Obj == MINE);
    /// <summary>the goal's state machine (start -> ship -> resources -> placing -> next belt)</summary>
    void MineBeltThink(Census S)
    {
        var M = MineGoalState ??= new MineGoal();
        if (M.Layer != null && (!M.Layer.Alive || M.Layer.Owner != P)) { Claims.Delete(M.Layer); M.Layer = null; M.State = "ship"; }
        if (M.State == "ship")
        {
            var u0 = S.Fleet.FirstOrDefault(x => x.Alive && x.Name == LAYER && !Claims.Has(x) && !Locked.Has(x));
            if (u0 != null) { M.Layer = u0; Claims.Set(u0, M); M.State = "belt"; }
            else
            {
                var T = TrainableOf(S, LAYER);
                if (T != null && T.Producer != null && !T.Producer.Queue.Any(q => q.Action == T.Action) && ArmyCanPay(S, T.Action.Cost)) Hands.QueueAction(P, T.Producer, T.Action);
                return;
            }
        }
        var u = M.Layer!;
        if (M.State == "placing")
        {
            if (u.Task.Type == "lay") return;
            M.State = "belt"; M.Belts++;
        }
        if (M.State == "belt")
        {
            var a = MineAction(u);
            if (a == null) return;
            var group = NextMineGroup(IN_A_ROW);
            if (group.Count == 0) return;
            var cost = a.Cost.Clone();
            foreach (var r in Rules.Rules.COSTS) cost[r] = a.Cost[r] * group.Count;
            if (!P.CanAfford(cost)) return;            // (the original's resource allocator waits for it)
            int n = 0;
            foreach (var m in group) if (Hands.LayWaterThing(P, u, a, m.X, m.Z, n > 0) == null) n++;
            if (n > 0) M.State = "placing";
        }
    }
}
