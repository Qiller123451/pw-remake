// Walkability grid + A* with string pulling. Cells hold a block counter (trees, stones, buildings).
// A clearance field (distance to the nearest blocked cell, in cells) lets big units (dinosaurs, vehicles)
// path only through gaps they fit through; it is rebuilt lazily whenever the grid changed.
//
// Ported from remake/src/game/nav.js (pw-exporter 98c7703). The arrays keep the JS types (Uint8Array -> byte[]:
// counters wrap like the JS ones, Float32Array -> float[]: costs rounded to single precision as in the JS), so the
// searches open the same cells in the same order.
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>A point of a path: [x, z] or [x, z, 1] (Exact: reached exactly, steer - Unit.SetPath's lead-in).</summary>
public struct Waypoint
{
    public double X, Z;
    public bool Exact;
    public Waypoint(double x, double z, bool exact = false) { X = x; Z = z; Exact = exact; }
    public override readonly string ToString() => $"({X:0.#}, {Z:0.#}{(Exact ? ", 1" : "")})";
}

/// <summary>A path found by NavGrid.Find; Gate: the enemy gate a breach search goes through (out.gate).</summary>
public sealed class NavPath : List<Waypoint>
{
    public Building? Gate;
    public NavPath() { }
    public NavPath(IEnumerable<Waypoint> pts) : base(pts) { }
}

public sealed class NavGrid
{
    const int COARSE = 4;              // fine cells per side of a coarse block (coarseRoute)
    const int COARSE_MIN = 60;         // goals at least this many cells away (120 m) take the coarse route
    const int LEG = 10;                // blocks per leg (about 80 m)
    const int LEG_ITER = 6000;         // the fine search's limit per leg

    public readonly double Size, Cell, Half;
    public readonly int N;
    public readonly byte[] Block;
    readonly float[] _g, _f;
    readonly int[] _from;
    readonly uint[] _stamp, _closed;
    readonly int[] _heap;
    uint _cur = 1;
    public int Version;
    /// <summary>clearance in cells (0 = blocked), at most CLEAR_CAP: it is only ever compared with a unit's need (a few
    /// cells), so the farther cells need not be told apart - and a change of Block changes it only within CLEAR_CAP
    /// cells (UpdateClearance does those alone)</summary>
    public readonly byte[] Clear;
    public const int CLEAR_CAP = 32;
    int _clearVersion = -1;
    // (incremental clearance: the boxes of cells changed since Clear was right, while every change was noted - else
    // the whole grid again; the box of the change being made)
    int _clearKnown = int.MinValue;
    readonly List<(int I0, int J0, int I1, int J1)> _clearRects = new();
    int _bi0 = int.MaxValue, _bj0 = int.MaxValue, _bi1 = -1, _bj1 = -1;
    byte[]? _clearTmp;
    /// <summary>(tests) how many times the clearance was worked out for the whole grid</summary>
    public int ClearWholeBuilds;
    /// <summary>gate cells (never in Block): passable for everybody when open, for the owner's team in AUTO mode, never when closed</summary>
    public Dictionary<int, Building> GateAt = new();
    /// <summary>the player a path is searched for (set by Find)</summary>
    public Player? Who;
    /// <summary>the last Find reached its goal</summary>
    public bool Found;
    /// <summary>connected ground regions (LabelRegions), null before</summary>
    public int[]? Region;
    public int Regions;
    // the coarse graph (BuildCoarse)
    int[]? _comp;
    double[]? _cRep;
    List<int>[]? _cAdj;
    int _cN, _cGateN, _coarseVersion = -1;
    // (incremental: the nodes of each block, each node's block, ids given back, the blocks changed since - BuildCoarse)
    List<int>?[]? _blkNodes;
    int[]? _nodeBlk;
    readonly List<int> _freeIds = new(), _dirtyList = new();
    bool[]? _dirtyBlk;
    /// <summary>the Version up to which every change of Block went through Mark / MarkCircle (its blocks noted in
    /// _dirtyList); another Version++ (a grid copied whole, the terrain) makes the next BuildCoarse a whole one</summary>
    int _knownVersion = int.MinValue;
    readonly Stack<int> _cStack = new();
    Dictionary<int, List<int>>? _cGates;
    float[]? _cg, _cf;
    int[]? _cfrom, _cheap;
    uint[]? _cstamp, _cclosed;
    uint _ccur = 1;
    uint[]? _gmask;
    bool _lastCoarse;                // (--perf: the last search went the coarse way)
    uint _gcur;
    // (World.AmphibNav: the key and time of its last rebuild)
    internal long AmpKey = -1;
    internal double AmpT = -99;

    public NavGrid(double size, double cell = 2)
    {
        Size = size; Cell = cell; Half = size / 2;
        N = (int)Math.Round(size / cell, MidpointRounding.AwayFromZero);
        int NN = N * N;
        Block = new byte[NN];
        _g = new float[NN]; _f = new float[NN];
        _from = new int[NN]; _stamp = new uint[NN]; _closed = new uint[NN];
        _heap = new int[NN * 4];
        int n = N;
        for (int i = 0; i < n; i++) { Block[i]++; Block[(n - 1) * n + i]++; Block[i * n]++; Block[i * n + n - 1]++; }
        Clear = new byte[NN];
    }

    public bool GateBlocks(int k, Player? who)
    {
        // (a gate site too, from its placement on: MIRAGE's mirage_gate_blocking - CGate.OnInit's SetPFBlocking, which the
        // original had commented out, so an unfinished gate was a hole for every player)
        if (!GateAt.TryGetValue(k, out var g) || !g.Alive) return false;
        if (g.GateState == 0 || g.BrokenT > 0) return false;                         // open
        // auto: the owner and its friends pass, enemies are blocked (buildings.md 2.2); a gate of nobody has no enemies -
        // everybody passes (mission 8's -01_gate_01 into Valhalla: it was walked through only by the gaps the 45-degree
        // gate cells had, Building.CellsOf cover)
        if (g.GateState == 2) return g.Owner != null && !(who != null && g.Owner.IsFriend(who));
        return true;                                                                  // closed
    }

    /// <summary>two-pass chamfer distance transform (8-neighbourhood: the chessboard distance to the nearest blocked
    /// cell, exact, capped at CLEAR_CAP); after changes made through Mark / MarkCircle only the cells near them</summary>
    public void UpdateClearance()
    {
        if (_clearVersion == Version) return;
        long p0 = StepProfile.Current != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        bool part = _clearKnown == Version && _clearVersion != -1;
        if (part) foreach (var (i0, j0, i1, j1) in _clearRects) ClearBox(i0, j0, i1, j1);
        else { ClearWholeBuilds++; UpdateClearance0(); }
        _clearRects.Clear();
        _clearVersion = Version; _clearKnown = Version;
        if (StepProfile.Current != null) StepProfile.Current.Add(part ? "nav:clearance part" : "nav:clearance", System.Diagnostics.Stopwatch.GetTimestamp() - p0);
    }
    /// <summary>the clearance of the cells within CLEAR_CAP of the box [i0, i1] x [j0, j1]: the transform over the box
    /// widened by twice that (the blocked cells that decide them all lie in it, and so do the chessboard ways to them)</summary>
    void ClearBox(int i0, int j0, int i1, int j1)
    {
        int n = N, C = CLEAR_CAP;
        int wi0 = Math.Max(0, i0 - 2 * C), wj0 = Math.Max(0, j0 - 2 * C), wi1 = Math.Min(n - 1, i1 + 2 * C), wj1 = Math.Min(n - 1, j1 + 2 * C);
        int w = wi1 - wi0 + 1, h = wj1 - wj0 + 1;
        if (_clearTmp == null || _clearTmp.Length < w * h) _clearTmp = new byte[Math.Max(w * h, 4096)];
        var t = _clearTmp; var b = Block;
        for (int j = 0; j < h; j++) for (int i = 0; i < w; i++) t[j * w + i] = b[(j + wj0) * n + i + wi0] != 0 ? (byte)0 : (byte)C;
        Chamfer(t, w, h);
        int ci0 = Math.Max(0, i0 - C), cj0 = Math.Max(0, j0 - C), ci1 = Math.Min(n - 1, i1 + C), cj1 = Math.Min(n - 1, j1 + C);
        for (int j = cj0; j <= cj1; j++) Array.Copy(t, (j - wj0) * w + ci0 - wi0, Clear, j * n + ci0, ci1 - ci0 + 1);
    }
    void UpdateClearance0()
    {
        int n = N; var c = Clear; var b = Block;
        for (int k = 0; k < n * n; k++) c[k] = b[k] != 0 ? (byte)0 : (byte)CLEAR_CAP;
        Chamfer(c, n, n);
    }
    static void Chamfer(byte[] c, int n, int m)
    {
        // (n columns, m rows)
        for (int j = 0; j < m; j++) for (int i = 0; i < n; i++)
        {
            int k = j * n + i; if (c[k] == 0) continue;
            int v = c[k];
            if (i > 0) v = Math.Min(v, c[k - 1] + 1);
            if (j > 0) { v = Math.Min(v, c[k - n] + 1); if (i > 0) v = Math.Min(v, c[k - n - 1] + 1); if (i < n - 1) v = Math.Min(v, c[k - n + 1] + 1); }
            c[k] = (byte)v;
        }
        for (int j = m - 1; j >= 0; j--) for (int i = n - 1; i >= 0; i--)
        {
            int k = j * n + i; if (c[k] == 0) continue;
            int v = c[k];
            if (i < n - 1) v = Math.Min(v, c[k + 1] + 1);
            if (j < m - 1) { v = Math.Min(v, c[k + n] + 1); if (i < n - 1) v = Math.Min(v, c[k + n + 1] + 1); if (i > 0) v = Math.Min(v, c[k + n - 1] + 1); }
            c[k] = (byte)v;
        }
    }

    /// <summary>
    /// regions of the ground that are connected (8-neighbourhood, diagonals like Find), labelled once the terrain is
    /// marked (mountains, water, map border) and before anything is built: a goal in another region is unreachable
    /// whatever stands there, so Find() heads for the nearest cell of the start's region instead of searching in vain
    /// </summary>
    public void LabelRegions()
    {
        int n = N; var B = Block;
        var R = Region = new int[n * n];
        Array.Fill(R, -1);
        var st = new int[n * n];
        int label = 0;
        for (int k0 = 0; k0 < n * n; k0++)
        {
            if (B[k0] != 0 || R[k0] >= 0) continue;
            int top = 0; st[top++] = k0; R[k0] = label;
            while (top > 0)
            {
                int c = st[--top], ci = c % n, cj = (c - ci) / n;
                for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int i = ci + di, j = cj + dj;
                    if (i < 0 || j < 0 || i >= n || j >= n) continue;
                    int k = j * n + i;
                    if (B[k] != 0 || R[k] >= 0 || (di != 0 && dj != 0 && (B[cj * n + i] != 0 || B[j * n + ci] != 0))) continue;
                    R[k] = label; st[top++] = k;
                }
            }
            label++;
        }
        Regions = label;
    }

    /// <summary>nearest free cell of region reg around cell c (ring search), or -1</summary>
    public int NearestInRegion(int c, int reg, int maxR = 80)
    {
        int n = N, ci = c % n, cj = c / n; var R = Region!;
        for (int r = 0; r < maxR; r++)
        {
            int best = -1; double bd = 1e9;
            for (int j = cj - r; j <= cj + r; j++)
            {
                if (j < 0 || j >= n) continue;
                int step = j == cj - r || j == cj + r ? 1 : 2 * r;
                for (int i = ci - r; i <= ci + r; i += step != 0 ? step : 1)
                {
                    if (i < 0 || i >= n) continue;
                    int k = j * n + i;
                    if (R[k] == reg && Block[k] == 0) { double d = (double)(i - ci) * (i - ci) + (double)(j - cj) * (j - cj); if (d < bd) { bd = d; best = k; } }
                }
            }
            if (best >= 0) return best;
        }
        return -1;
    }

    /// <summary>cells a unit of radius r needs between its centre and the nearest obstacle</summary>
    public int Need(double r) => Math.Max(1, (int)Math.Ceiling(r / Cell - 0.25));
    public bool Fits(int k, int need) => Clear[k] >= need;

    public int Idx(double x, double z)
    {
        int i = (int)Math.Min(N - 1, Math.Max(0, Math.Floor((x + Half) / Cell)));
        int j = (int)Math.Min(N - 1, Math.Max(0, Math.Floor((z + Half) / Cell)));
        return j * N + i;
    }

    public (double X, double Z) Center(int c) => (((c % N) + 0.5) * Cell - Half, (Math.Floor((double)c / N) + 0.5) * Cell - Half);

    /// <summary>circle footprint (d: +1 block, -1 unblock, 0 only the cells)</summary>
    public List<int> MarkCircle(double x, double z, double r, int d = 1)
    {
        var cells = new List<int>();
        int i0 = (int)Math.Max(0, Math.Floor((x - r + Half) / Cell)), i1 = (int)Math.Min(N - 1, Math.Floor((x + r + Half) / Cell));
        int j0 = (int)Math.Max(0, Math.Floor((z - r + Half) / Cell)), j1 = (int)Math.Min(N - 1, Math.Floor((z + r + Half) / Cell));
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++)
        {
            double cx = (i + 0.5) * Cell - Half, cz = (j + 0.5) * Cell - Half;
            if ((cx - x) * (cx - x) + (cz - z) * (cz - z) < r * r) { int k = j * N + i; Block[k] = (byte)Math.Max(0, Block[k] + d); cells.Add(k); if (d != 0) Dirty(k); }
        }
        Bump();
        return cells;
    }

    /// <summary>oriented rectangle footprint (half extents hx, hz, rotation rot): the cells whose centre lies in it; cover:
    /// every cell the rectangle touches (separating axes of the cell square and the rectangle) - a gate's thin door box
    /// turned 45 degrees (BoosterPack 1 mission 4-2's rune gates: 1.4 m deep) left gaps between the cell centres it
    /// covered, and units walked through the closed gate</summary>
    public List<int> RectCells(double x, double z, double hx, double hz, double rot, bool cover = false)
    {
        var cells = new List<int>();
        double c = SMath.Cos(rot), s = SMath.Sin(rot), ac = Math.Abs(c), az = Math.Abs(s), h = Cell / 2;
        double R = Math.Sqrt(hx * hx + hz * hz) + (cover ? Cell : 0);
        // cover: the cell square's half extent along the rectangle's axes, and the rectangle's along the grid's
        double ex = cover ? h * (ac + az) : 0, gx = cover ? hx * ac + hz * az + h : 1e9, gz = cover ? hx * az + hz * ac + h : 1e9;
        int i0 = (int)Math.Max(0, Math.Floor((x - R + Half) / Cell)), i1 = (int)Math.Min(N - 1, Math.Floor((x + R + Half) / Cell));
        int j0 = (int)Math.Max(0, Math.Floor((z - R + Half) / Cell)), j1 = (int)Math.Min(N - 1, Math.Floor((z + R + Half) / Cell));
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++)
        {
            double dx = (i + 0.5) * Cell - Half - x, dz = (j + 0.5) * Cell - Half - z;
            double lx = dx * c - dz * s, lz = dx * s + dz * c;
            if (Math.Abs(lx) <= hx + ex && Math.Abs(lz) <= hz + ex && Math.Abs(dx) <= gx && Math.Abs(dz) <= gz) cells.Add(j * N + i);
        }
        return cells;
    }

    /// <summary>the cells of RectCells (not cover) all free here and in reserved (when given) - without the list: the
    /// computer players' building placement tries up to 150 spots a think (World.CanPlace)</summary>
    public bool RectFree(double x, double z, double hx, double hz, double rot, byte[]? reserved)
    {
        double c = SMath.Cos(rot), s = SMath.Sin(rot);
        double R = Math.Sqrt(hx * hx + hz * hz);
        int i0 = (int)Math.Max(0, Math.Floor((x - R + Half) / Cell)), i1 = (int)Math.Min(N - 1, Math.Floor((x + R + Half) / Cell));
        int j0 = (int)Math.Max(0, Math.Floor((z - R + Half) / Cell)), j1 = (int)Math.Min(N - 1, Math.Floor((z + R + Half) / Cell));
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++)
        {
            double dx = (i + 0.5) * Cell - Half - x, dz = (j + 0.5) * Cell - Half - z;
            double lx = dx * c - dz * s, lz = dx * s + dz * c;
            if (Math.Abs(lx) <= hx && Math.Abs(lz) <= hz)
            {
                int k = j * N + i;
                if (Block[k] != 0 || (reserved != null && reserved[k] != 0)) return false;
            }
        }
        return true;
    }

    public void Mark(IEnumerable<int> cells, int d) { foreach (var k in cells) { Block[k] = (byte)Math.Max(0, Block[k] + d); Dirty(k); } Bump(); }
    /// <summary>the gates changed (GateAt, a gate's state): Block did not - the coarse graph keeps its nodes</summary>
    public void GatesChanged() => Bump();
    // (the coarse graph's block of cell k changed)
    void Dirty(int k)
    {
        int ki = k % N, kj = k / N;
        if (ki < _bi0) _bi0 = ki; if (ki > _bi1) _bi1 = ki; if (kj < _bj0) _bj0 = kj; if (kj > _bj1) _bj1 = kj;
        var db = _dirtyBlk;
        if (db == null || _knownVersion != Version) return;          // (a whole rebuild is due anyway)
        int n = N, cn = (n + COARSE - 1) / COARSE, bk = (k / n) / COARSE * cn + (k % n) / COARSE;
        if (!db[bk]) { db[bk] = true; _dirtyList.Add(bk); }
    }
    // Version++, still known when it was before
    void Bump()
    {
        bool known = _knownVersion == Version, cknown = _clearKnown == Version;
        if (cknown && _bi1 >= 0)
        {
            if (_clearRects.Count < 64) _clearRects.Add((_bi0, _bj0, _bi1, _bj1));
            else { cknown = false; _clearRects.Clear(); _clearKnown = int.MinValue; }      // (many: the whole grid next time)
        }
        _bi0 = _bj0 = int.MaxValue; _bi1 = _bj1 = -1;
        Version++;
        if (known) _knownVersion = Version;
        if (cknown) _clearKnown = Version;
    }
    public bool Free(IEnumerable<int> cells) { foreach (var k in cells) if (Block[k] != 0) return false; return true; }

    public bool IsFree(double x, double z)
    {
        if (Math.Abs(x) >= Half - Cell || Math.Abs(z) >= Half - Cell) return false;
        return Block[Idx(x, z)] == 0;
    }

    public int NearestFree(int c, int maxR = 40)
    {
        if (Block[c] == 0) return c;
        int n = N, ci = c % n, cj = c / n;
        for (int r = 1; r < maxR; r++)
        {
            int best = -1; double bd = 1e9;
            for (int i = ci - r; i <= ci + r; i++) for (int j = cj - r; j <= cj + r; j++)
            {
                if (i < 0 || j < 0 || i >= n || j >= n) continue;
                if (Math.Max(Math.Abs(i - ci), Math.Abs(j - cj)) != r) continue;
                int k = j * n + i;
                if (Block[k] == 0) { double d = (double)(i - ci) * (i - ci) + (double)(j - cj) * (j - cj); if (d < bd) { bd = d; best = k; } }
            }
            if (best >= 0) return best;
        }
        return c;
    }

    public bool Los(double x0, double z0, double x1, double z1, int need = 1)
    {
        double d = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        int steps = (int)Math.Ceiling(d / (Cell * 0.5));
        if (need > 1) UpdateClearance();
        for (int s = 1; s < steps; s++)
        {
            double t = (double)s / steps;
            int k = Idx(x0 + (x1 - x0) * t, z0 + (z1 - z0) * t);
            if (need > 1 ? Clear[k] < need : Block[k] != 0) return false;
            if (GateAt.Count > 0 && GateBlocks(k, Who)) return false;
        }
        return true;
    }

    /// <summary>nearest cell with enough clearance</summary>
    public int NearestFit(int c, int need, int maxR = 30)
    {
        if (need <= 1) return NearestFree(c, maxR);
        UpdateClearance();
        if (Clear[c] >= need) return c;
        int n = N, ci = c % n, cj = c / n;
        for (int r = 1; r < maxR; r++)
        {
            int best = -1; double bd = 1e9;
            for (int i = ci - r; i <= ci + r; i++) for (int j = cj - r; j <= cj + r; j++)
            {
                if (i < 0 || j < 0 || i >= n || j >= n || Math.Max(Math.Abs(i - ci), Math.Abs(j - cj)) != r) continue;
                int k = j * n + i;
                if (Clear[k] >= need) { double d = (double)(i - ci) * (i - ci) + (double)(j - cj) * (j - cj); if (d < bd) { bd = d; best = k; } }
            }
            if (best >= 0) return best;
        }
        return NearestFree(c, maxR);
    }

    // ---- far goals: a coarse route, then short fine searches along it
    // One plain A* over the whole grid of a campaign map (736 x 736 cells) to a goal several hundred metres away opens
    // up to its limit of 30,000 cells and often ends short of the goal. Instead the grid is cut into blocks of
    // COARSE x COARSE cells; the free cells of a block that hang together form a node, nodes whose cells touch across a
    // block border are linked (so the nodes connect exactly as the cells do). A route is found over the nodes and the
    // way searched in legs of about 80 m between cells of the route's nodes. A leg that does not get through (a closed
    // gate) gives up the coarse way: the plain search runs as before. The nodes are rebuilt when the grid changed.
    void BuildCoarse()
    {
        var P = StepProfile.Current;
        if (P == null) { BuildCoarse0(); return; }
        long p0 = System.Diagnostics.Stopwatch.GetTimestamp(), b0 = GC.GetAllocatedBytesForCurrentThread();
        BuildCoarse0();
        P.Add("nav:coarse", System.Diagnostics.Stopwatch.GetTimestamp() - p0, GC.GetAllocatedBytesForCurrentThread() - b0);
    }
    // The graph is kept up to date by blocks: a change of Block through Mark / MarkCircle notes its blocks (Dirty), and
    // the next far search refloods only those blocks and relinks the nodes around them (BuildCoarseDirty) - a whole
    // rebuild of a 16-player map took 10 - 50 ms after every building placed or destroyed (docs/DEBUG.md
    // "Performance"). The result is the same graph as a whole rebuild (the same nodes, centres and links in the same
    // order; only the numbers of the nodes differ, and they decide nothing): NavCoarseIncremental tests it.
    void BuildCoarse0()
    {
        int n = N, cn = (n + COARSE - 1) / COARSE;
        bool whole = _comp == null || _comp.Length != n * n || _knownVersion != Version || _dirtyBlk == null;
        if (whole) BuildCoarseWhole();
        else if (_dirtyList.Count > 0) BuildCoarseDirty();
        foreach (var bk in _dirtyList) _dirtyBlk![bk] = false;
        _dirtyList.Clear();
        // the gates' cells (free in Block, shut or open by the moment and the player): their nodes are tested per search
        var comp = _comp!;
        var gates = new Dictionary<int, List<int>>();
        foreach (var k in SortedGateKeys()) { int c = comp[k]; if (c >= 0) { if (!gates.TryGetValue(c, out var l)) gates[c] = l = new List<int>(); l.Add(k); } }
        _cGates = gates; _cGateN = GateAt.Count;
        int id = _cN;
        if (_cg == null || _cg.Length < id)
        {
            int M = Math.Max(id, 16);
            _cg = new float[M]; _cf = new float[M]; _cfrom = new int[M];
            _cstamp = new uint[M]; _cclosed = new uint[M]; _cheap = new int[M * 8]; _ccur = 1;
        }
        _coarseVersion = Version; _knownVersion = Version;
    }

    /// <summary>(tests) how many times the coarse graph was built whole</summary>
    public int CoarseWholeBuilds;
    void BuildCoarseWhole()
    {
        CoarseWholeBuilds++;
        int n = N, cn = (n + COARSE - 1) / COARSE, NN = n * n;
        if (_comp == null || _comp.Length != NN) _comp = new int[NN];
        Array.Fill(_comp, -1);
        if (_blkNodes == null || _blkNodes.Length != cn * cn) { _blkNodes = new List<int>?[cn * cn]; _dirtyBlk = new bool[cn * cn]; }
        foreach (var l in _blkNodes) l?.Clear();
        _freeIds.Clear();
        _cN = 0;
        for (int cj = 0; cj < cn; cj++) for (int ci = 0; ci < cn; ci++) FloodBlock(cj * cn + ci);
        var adj = _cAdj!;
        for (int i = 0; i < _cN; i++) adj[i].Clear();
        // links between nodes whose cells touch (right, down, and both diagonals below)
        LinkScan(0, 0, n, n, -1, 0, 0, 0, 0);
    }

    /// <summary>the free cells of block bk that hang together, one node each (diagonals as in Find), numbered from the
    /// block's old nodes, then the ids given back, then new ones</summary>
    void FloodBlock(int bk)
    {
        int F = COARSE, n = N, cn = (n + COARSE - 1) / COARSE, ci = bk % cn, cj = bk / cn; var b = Block; var comp = _comp!;
        int i0 = ci * F, j0 = cj * F, i1 = Math.Min(n, i0 + F), j1 = Math.Min(n, j0 + F);
        var old = _blkNodes![bk];
        int reuse = 0, oldN = old?.Count ?? 0;
        var stack = _cStack;
        List<int>? mine = null;
        // (the old numbers of this block first, in order; kept apart until the block is done)
        Span<int> olds = oldN <= 64 ? stackalloc int[oldN] : new int[oldN];
        for (int q = 0; q < oldN; q++) olds[q] = old![q];
        old?.Clear();
        for (int j = j0; j < j1; j++) for (int i = i0; i < i1; i++)
        {
            int k0 = j * n + i;
            if (b[k0] != 0 || comp[k0] >= 0) continue;
            int id = reuse < oldN ? olds[reuse++] : _freeIds.Count > 0 ? PopFree() : _cN++;
            EnsureNode(id);
            comp[k0] = id; stack.Clear(); stack.Push(k0);
            double sx = 0, sz = 0; int cnt = 0;
            while (stack.Count > 0)
            {
                int c = stack.Pop(), x = c % n, z = (c - x) / n;
                sx += x; sz += z; cnt++;
                for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int x2 = x + di, z2 = z + dj;
                    if (x2 < i0 || x2 >= i1 || z2 < j0 || z2 >= j1) continue;
                    int k = z2 * n + x2;
                    if (b[k] != 0 || comp[k] >= 0 || (di != 0 && dj != 0 && (b[z * n + x2] != 0 || b[z2 * n + x] != 0))) continue;
                    comp[k] = id; stack.Push(k);
                }
            }
            var rep = _cRep!;
            rep[id * 3] = k0; rep[id * 3 + 1] = sx / cnt; rep[id * 3 + 2] = sz / cnt;     // a cell of the node, and its centre (for distances)
            _nodeBlk![id] = bk;
            (mine ??= _blkNodes[bk] ??= new List<int>()).Add(id);
        }
        for (int q = reuse; q < oldN; q++) { _freeIds.Add(olds[q]); _cAdj![olds[q]].Clear(); }
    }
    int PopFree() { int id = _freeIds[^1]; _freeIds.RemoveAt(_freeIds.Count - 1); return id; }
    void EnsureNode(int id)
    {
        int cap = _nodeBlk?.Length ?? 0;
        if (id < cap) return;
        int m = Math.Max(1024, Math.Max(id + 1, cap * 2));
        Array.Resize(ref _nodeBlk, m);
        Array.Resize(ref _cRep, m * 3);
        Array.Resize(ref _cAdj, m);
        for (int q = cap; q < m; q++) _cAdj![q] = new List<int>();
    }

    /// <summary>
    /// the links of the cells in [x0, x1) x [z0, z1) in the order of a whole scan (rows, then columns): with win &lt; 0
    /// every node's; else only those of the nodes in the blocks [bi0, bi1] x [bj0, bj1] (their lists emptied before:
    /// the window is those blocks and a cell around, where all their links are found)
    /// </summary>
    void LinkScan(int x0, int z0, int x1, int z1, int win, int bi0, int bj0, int bi1, int bj1)
    {
        int n = N, cn = (n + COARSE - 1) / COARSE; var comp = _comp!; var b = Block; var adj = _cAdj!; var nb = _nodeBlk!;
        bool In(int a) { if (win < 0) return true; int bk = nb[a], bi = bk % cn, bj = bk / cn; return bi >= bi0 && bi <= bi1 && bj >= bj0 && bj <= bj1; }
        void Link(int a, int c)
        {
            if (a == c) return;
            if (In(a) && !adj[a].Contains(c)) adj[a].Add(c);
            if (In(c) && !adj[c].Contains(a)) adj[c].Add(a);
        }
        for (int z = z0; z < z1; z++) for (int x = x0; x < x1; x++)
        {
            int k = z * n + x, a = comp[k];
            if (a < 0) continue;
            if (x + 1 < n && comp[k + 1] >= 0 && comp[k + 1] != a) Link(a, comp[k + 1]);
            if (z + 1 < n)
            {
                if (comp[k + n] >= 0 && comp[k + n] != a) Link(a, comp[k + n]);
                if (x + 1 < n && comp[k + n + 1] >= 0 && b[k + 1] == 0 && b[k + n] == 0 && comp[k + n + 1] != a) Link(a, comp[k + n + 1]);
                if (x > 0 && comp[k + n - 1] >= 0 && b[k - 1] == 0 && b[k + n] == 0 && comp[k + n - 1] != a) Link(a, comp[k + n - 1]);
            }
        }
    }

    /// <summary>the changed blocks flooded anew, then the links of the nodes in the 3 x 3 blocks around each</summary>
    void BuildCoarseDirty()
    {
        int F = COARSE, n = N, cn = (n + COARSE - 1) / COARSE; var comp = _comp!;
        foreach (var bk in _dirtyList)
        {
            int ci = bk % cn, cj = bk / cn, i0 = ci * F, j0 = cj * F, i1 = Math.Min(n, i0 + F), j1 = Math.Min(n, j0 + F);
            for (int j = j0; j < j1; j++) for (int i = i0; i < i1; i++) comp[j * n + i] = -1;
        }
        foreach (var bk in _dirtyList) FloodBlock(bk);
        foreach (var bk in _dirtyList)
        {
            int ci = bk % cn, cj = bk / cn;
            int bi0 = Math.Max(0, ci - 1), bj0 = Math.Max(0, cj - 1), bi1 = Math.Min(cn - 1, ci + 1), bj1 = Math.Min(cn - 1, cj + 1);
            for (int bj = bj0; bj <= bj1; bj++) for (int bi = bi0; bi <= bi1; bi++)
                if (_blkNodes![bj * cn + bi] is { } l) foreach (var id in l) _cAdj![id].Clear();
            LinkScan(Math.Max(0, bi0 * F - 1), Math.Max(0, bj0 * F - 1), Math.Min(n, (bi1 + 1) * F + 1), Math.Min(n, (bj1 + 1) * F + 1), 1, bi0, bj0, bi1, bj1);
        }
    }

    /// <summary>(tests) the coarse graph as text, the nodes numbered as a whole rebuild numbers them: equal for a graph
    /// kept up to date by blocks and one built whole from the same grid</summary>
    public string CoarseSignature()
    {
        if (_coarseVersion != Version || _comp == null || _cGateN != GateAt.Count) BuildCoarse();
        int cn = (N + COARSE - 1) / COARSE;
        var canon = new Dictionary<int, int>();
        for (int bk = 0; bk < cn * cn; bk++) if (_blkNodes![bk] is { } l) foreach (var id in l) canon[id] = canon.Count;
        var sb = new System.Text.StringBuilder();
        for (int bk = 0; bk < cn * cn; bk++) if (_blkNodes![bk] is { } l) foreach (var id in l)
        {
            sb.Append(canon[id]).Append(' ').Append(_cRep![id * 3]).Append(' ').Append(_cRep[id * 3 + 1].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
              .Append(_cRep[id * 3 + 2].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':');
            foreach (var c in _cAdj![id]) sb.Append(' ').Append(canon[c]);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // (GateAt is a Dictionary: its keys in insertion order are not guaranteed - the JS Map's were; sorted they are
    // the same on every machine)
    List<int> SortedGateKeys() { var l = new List<int>(GateAt.Keys); l.Sort(); return l; }

    readonly record struct Leg(bool Found, List<int> Cells);

    /// <summary>
    /// { cells, found }: the fine cells from s towards t along a coarse route - found false: t is out of reach, the
    /// cells end at the nearest reachable point - or null (the plain search instead)
    /// </summary>
    Leg? CoarseRoute(int s, int t, Func<int, int, int, Leg> astar)
    {
        int n = N;
        int si = s % n, sj = s / n, ti = t % n, tj = t / n;
        if (Math.Max(Math.Abs(ti - si), Math.Abs(tj - sj)) < COARSE_MIN) return null;
        if (_coarseVersion != Version || _comp == null || _cGateN != GateAt.Count) BuildCoarse();
        var comp = _comp!; int cs = comp[s], ct = comp[t];
        if (cs < 0 || ct < 0) return null;
        var rep = _cRep!; var adj = _cAdj!; var g = _cg!; var f = _cf!; var from = _cfrom!; var heap = _cheap!; var closed = _cclosed!;
        double tx = rep[ct * 3 + 1], tz = rep[ct * 3 + 2];
        double H(int c) { double ax = rep[c * 3 + 1] - tx, az = rep[c * 3 + 2] - tz; return Math.Sqrt(ax * ax + az * az); }
        uint stamp = ++_ccur;
        int hn = 0;
        void Push(int c)
        {
            int i = hn++; heap[i] = c;
            while (i > 0) { int p = (i - 1) >> 1; if (f[heap[p]] <= f[heap[i]]) break; (heap[p], heap[i]) = (heap[i], heap[p]); i = p; }
        }
        int Pop()
        {
            int top = heap[0]; heap[0] = heap[--hn]; int i = 0;
            for (; ; )
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < hn && f[heap[l]] < f[heap[m]]) m = l;
                if (r < hn && f[heap[r]] < f[heap[m]]) m = r;
                if (m == i) break;
                (heap[m], heap[i]) = (heap[i], heap[m]); i = m;
            }
            return top;
        }
        _cstamp![cs] = stamp; g[cs] = 0; f[cs] = (float)H(cs); from[cs] = -1; Push(cs);
        bool found = false, gated = false; int best = cs; double bestH = 1e9;
        var gates = _cGates!; var who = Who;
        while (hn > 0)
        {
            int c = Pop();
            if (closed[c] == stamp) continue;
            closed[c] = stamp;
            if (c == ct) { found = true; break; }
            double hc = (double)f[c] - g[c];
            if (hc < bestH) { bestH = hc; best = c; }
            double cx = rep[c * 3 + 1], cz = rep[c * 3 + 2];
            foreach (var k in adj[c])
            {
                if (closed[k] == stamp) continue;
                // a node with a gate that is shut for this player (a node split by a gate is taken as shut as a whole)
                if (gates.Count > 0 && gates.TryGetValue(k, out var gl) && k != ct && gl.Exists(q => GateBlocks(q, who))) { gated = true; continue; }
                double ax = rep[k * 3 + 1] - cx, az = rep[k * 3 + 2] - cz, ng = g[c] + Math.Sqrt(ax * ax + az * az);
                if (_cstamp[k] != stamp || ng < g[k]) { _cstamp[k] = stamp; g[k] = (float)ng; f[k] = (float)(ng + H(k) * 1.02); from[k] = c; Push(k); }
                if (hn >= heap.Length - 8) break;
            }
        }
        // the goal cannot be reached (the nodes connect as the cells do): the way leads to the reachable node nearest to
        // it (not when a gate was in the way: a node only partly a gate may still let one pass beside it - the plain
        // search decides then)
        int end = found ? ct : best;
        if (!found && (end == cs || gated)) return null;
        var route = new List<int>();
        for (int c = end; c != -1; c = from[c]) route.Add(c);
        route.Reverse();
        // the legs: to a cell of every LEG-th node of the route, then to t
        var res = new List<int>();
        int cur = s;
        for (int r = LEG; r < route.Count - LEG / 2.0; r += LEG)
        {
            int w = (int)rep[route[r] * 3];
            if (w == cur) continue;
            var leg = astar(cur, w, LEG_ITER);
            if (!leg.Found) return null;
            res.AddRange(leg.Cells);
            cur = w;
        }
        int goal = found ? t : (int)rep[end * 3];
        if (goal != cur)
        {
            var last = astar(cur, goal, LEG_ITER * 2);
            if (!last.Found) return null;
            res.AddRange(last.Cells);
        }
        return new Leg(found, res);
    }

    /// <summary>
    /// path from (sx,sz) to (tx,tz) for a unit of the given radius, or null.
    /// breach: closed gates of the enemy count as passable at a high cost - an attacker whose way they block attacks
    /// the first of them (World.UnitUpdate); the result then has .Gate. Found: the goal was reached.
    /// </summary>
    public NavPath? Find(double sx, double sz, double tx, double tz, double radius = 0.7, int maxIter = 30000, Player? who = null, bool breach = false)
    {
        if (StepProfile.Current == null) return Find0(sx, sz, tx, tz, radius, maxIter, who, breach);
        long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastCoarse = false;
        var r = Find0(sx, sz, tx, tz, radius, maxIter, who, breach);
        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - p0;
        StepProfile.Current.Add("nav:find", dt);
        StepProfile.Current.Find(dt, N, sx, sz, tx, tz, Need(radius), breach, Found, _lastCoarse);
        return r;
    }
    NavPath? Find0(double sx, double sz, double tx, double tz, double radius, int maxIter, Player? who, bool breach)
    {
        int n = N;
        int need = Need(radius);
        if (need > 1) UpdateClearance();
        Who = who;
        Found = false;
        bool gates = GateAt.Count > 0;
        // the gate cells marked for this search: a cell that is no gate is not looked up in GateAt (a lookup per
        // neighbour of every cell opened was most of a long search's time on a map with walls and gates)
        uint gst = 0;
        if (gates)
        {
            if (_gmask == null || _gmask.Length != n * n) _gmask = new uint[n * n];
            gst = ++_gcur;
            foreach (var gk0 in GateAt.Keys) _gmask[gk0] = gst;
        }
        var gm = _gmask;
        bool IsGate(int k) => gm![k] == gst;
        bool Ok0(int k) => need > 1 ? Clear[k] >= need : Block[k] == 0;
        // only an enemy's gate is broken; a closed gate of one's own side or of a neutral stays shut
        bool EnemyGate(int k) => IsGate(k) && GateAt.TryGetValue(k, out var gg) && GateBlocks(k, who) && who != null && gg.Owner != null && who.IsEnemy(gg.Owner);
        bool Ok(int k) => !gates ? Ok0(k) : breach ? Ok0(k) && (!IsGate(k) || !GateBlocks(k, who) || EnemyGate(k)) : Ok0(k) && (!IsGate(k) || !GateBlocks(k, who));
        double Toll(int k) => gates && breach && EnemyGate(k) ? 25 : 0;
        int sIdx = Idx(sx, sz);
        int s = Ok(sIdx) ? sIdx : NearestFit(sIdx, need, 8);
        int tIdx = Idx(tx, tz);
        // a goal the start's ground region does not reach (across mountains or water): the nearest point that it does
        var R = Region;
        if (R != null && R[s] >= 0)
        {
            int tr = R[Block[tIdx] != 0 ? NearestFree(tIdx, 6) : tIdx];
            if (tr != R[s]) { int k = NearestInRegion(tIdx, R[s]); if (k >= 0) { tIdx = k; (tx, tz) = Center(k); } }
        }
        bool tb = !Ok(tIdx);
        int t = tb ? NearestFit(tIdx, need) : tIdx;
        if (!tb && Ok(sIdx) && Los(sx, sz, tx, tz, need)) { Found = true; return new NavPath { new Waypoint(tx, tz) }; }
        if (s == t) { Found = true; var (cx0, cz0) = Center(t); return new NavPath { tb ? new Waypoint(cx0, cz0) : new Waypoint(tx, tz) }; }
        // the fine search (A*) from cell s0 to t0: the cells after s0 up to t0 - or, when maxIter runs out, up to the cell
        // nearest t0 that it reached (found false)
        Leg Astar(int s0, int t0, int iterMax)
        {
            uint stamp = ++_cur;
            var g = _g; var f = _f; var from = _from; var heap = _heap; var closed = _closed;
            int tcx = t0 % n, tcz = t0 / n;
            double H(int c) { int dx = Math.Abs(c % n - tcx), dz = Math.Abs(c / n - tcz); return (dx + dz) + (Math.Sqrt(2) - 2) * Math.Min(dx, dz); }
            int hn = 0;
            void Push(int c)
            {
                int i = hn++; heap[i] = c;
                while (i > 0) { int p = (i - 1) >> 1; if (f[heap[p]] <= f[heap[i]]) break; (heap[p], heap[i]) = (heap[i], heap[p]); i = p; }
            }
            int Pop()
            {
                int top = heap[0]; heap[0] = heap[--hn]; int i = 0;
                for (; ; )
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < hn && f[heap[l]] < f[heap[m]]) m = l;
                    if (r < hn && f[heap[r]] < f[heap[m]]) m = r;
                    if (m == i) break;
                    (heap[m], heap[i]) = (heap[i], heap[m]); i = m;
                }
                return top;
            }
            _stamp[s0] = stamp; g[s0] = 0; f[s0] = (float)H(s0); from[s0] = -1; Push(s0);
            bool found = false; int it = 0, best = s0; double bestH = 1e9;
            const double W_H = 1.02;                         // (a heuristic a touch above the distance: fewer cells opened)
            while (hn > 0 && it++ < iterMax)
            {
                int c = Pop();
                if (closed[c] == stamp) continue;
                closed[c] = stamp;
                if (c == t0) { found = true; break; }
                double hc = (double)f[c] - g[c];
                if (hc < bestH) { bestH = hc; best = c; }
                int ci = c % n, cj = (c - ci) / n;
                for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                {
                    if (di == 0 && dj == 0) continue;
                    int i = ci + di, j = cj + dj;
                    if (i < 0 || j < 0 || i >= n || j >= n) continue;
                    int k = j * n + i;
                    if (!Ok(k) || closed[k] == stamp) continue;
                    if (di != 0 && dj != 0 && (!Ok(cj * n + i) || !Ok(j * n + ci))) continue;
                    double ng = g[c] + (di != 0 && dj != 0 ? 1.41421 : 1) + Toll(k);
                    if (_stamp[k] != stamp || ng < g[k]) { _stamp[k] = stamp; g[k] = (float)ng; f[k] = (float)(ng + H(k) * W_H); from[k] = c; Push(k); }
                }
            }
            int end = found ? t0 : best;
            var cells = new List<int>();
            for (int c = end; c != -1 && c != s0; c = from[c]) cells.Add(c);
            cells.Reverse();
            return new Leg(found, cells);
        }
        // far goals: a coarse route first, then short fine searches along it (CoarseRoute); null = search the plain way
        var far = need == 1 && !breach ? CoarseRoute(s, t, Astar) : null;
        _lastCoarse = far != null;
        List<int> cellsR; bool foundR;
        if (far is Leg fr) { cellsR = fr.Cells; foundR = fr.Found; } else { var r = Astar(s, t, maxIter); cellsR = r.Cells; foundR = r.Found; }
        Found = foundR;
        int gk = -1;
        if (breach) foreach (var c in cellsR) if (EnemyGate(c)) { gk = c; break; }
        var pts = new List<(double X, double Z)>(cellsR.Count);
        foreach (var c in cellsR) pts.Add(Center(c));
        if (foundR && !tb && pts.Count > 0) pts[^1] = (tx, tz);
        var res = new NavPath();
        double ax2 = sx, az2 = sz; int ii = 0;
        while (ii < pts.Count)
        {
            int j = Math.Min(pts.Count - 1, ii + 40);
            while (j > ii && !Los(ax2, az2, pts[j].X, pts[j].Z, need)) j--;
            res.Add(new Waypoint(pts[j].X, pts[j].Z));
            (ax2, az2) = pts[j];
            ii = j + 1;
        }
        if (gk >= 0 && res.Count > 0) res.Gate = GateAt[gk];
        return res.Count > 0 ? res : null;
    }
}

/// <summary>What a spatial hash holds: a position and its bucket key.</summary>
public interface IHashed
{
    double HX { get; }
    double HZ { get; }
    int HashKey { get; set; }
}

/// <summary>Spatial hash for entity queries (buckets in insertion order, like the JS arrays).</summary>
public sealed class SpatialHash<T> where T : class, IHashed
{
    public const int NoKey = int.MinValue;
    readonly double _cell;
    readonly Dictionary<int, List<T>> _map = new();

    public SpatialHash(double cell = 12) { _cell = cell; }

    public int Key(double x, double z) => ((int)Math.Floor(x / _cell) + 4096 << 13) | ((int)Math.Floor(z / _cell) + 4096);

    public void Insert(T e)
    {
        int k = Key(e.HX, e.HZ); e.HashKey = k;
        if (!_map.TryGetValue(k, out var l)) _map[k] = l = new List<T>();
        l.Add(e);
    }

    public void Remove(T e)
    {
        if (_map.TryGetValue(e.HashKey, out var l)) { int i = l.IndexOf(e); if (i >= 0) { l[i] = l[^1]; l.RemoveAt(l.Count - 1); } }
        e.HashKey = NoKey;
    }

    public void Move(T e) { int k = Key(e.HX, e.HZ); if (k != e.HashKey) { Remove(e); Insert(e); } }

    public void Query(double x, double z, double r, Action<T> fn)
    {
        double c = _cell;
        int i0 = (int)Math.Floor((x - r) / c), i1 = (int)Math.Floor((x + r) / c), j0 = (int)Math.Floor((z - r) / c), j1 = (int)Math.Floor((z + r) / c);
        for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++)
            if (_map.TryGetValue(((i + 4096) << 13) | (j + 4096), out var l))
                for (int q = 0; q < l.Count; q++) fn(l[q]);
    }

    /// <summary>Query into a list the caller keeps (no allocation per query; the same order as Query)</summary>
    public void Collect(double x, double z, double r, List<T> into)
    {
        double c = _cell;
        int i0 = (int)Math.Floor((x - r) / c), i1 = (int)Math.Floor((x + r) / c), j0 = (int)Math.Floor((z - r) / c), j1 = (int)Math.Floor((z + r) / c);
        for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++)
            if (_map.TryGetValue(((i + 4096) << 13) | (j + 4096), out var l))
                into.AddRange(l);
    }

    /// <summary>Query as a list (a copy: safe to change the hash while going through it)</summary>
    public List<T> Near(double x, double z, double r)
    {
        var res = new List<T>();
        Query(x, z, r, res.Add);
        return res;
    }
}
