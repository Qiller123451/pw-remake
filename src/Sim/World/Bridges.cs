// Bridges of the original maps (docs/SIMULATION.md "Bridges", docs/MAPS.md "Bridges").
//
// What the original does: every bridge is a CDecoColObj (MiscObj.usl), and that class only hands its model's pathfinder
// boxes to the pathfinder (CSrvWrap.GetPathfinder().AddPFBlocker) - there is no walk surface on a bridge, the only
// objects with a walkable top are the walls (the wall map's "/Walkable" config). A bridge is crossed on the ground: the
// level designers raised a strip of terrain to the deck's height under it and put river pieces in and out on both sides
// (mission 9's jungle_river_bridge_in / _out: a 23 m strip between river pieces at 21 m; BoosterPack 1's
// nor_river_bridge_in / _out, mission 7's sav_bridge_*), and the map's own walk grid (PaFi) lets units walk there. Where
// the ground under a deck is low (a ravine, a gorge) the walk grid blocks it, and the bridge is scenery - mission 13's
// hcl13_bridge_01, mission 5's hc_bridge_*, the jungle bridge of missions 9 and 10 (60 m up, both ends on blocked cells).
//
// What the remake adds: a bridge without pathfinder boxes blocks nothing in the original, so walkable ground that lies
// just below its deck - between 0.5 and 4 m under the bridge's stored height, inside the model's drawn box - was walked
// under the deck or through its towers, out of sight (player report, MIRAGE 13: the allied Dustriders coming back from the
// SEAS outpost walked the strip of ground between the two city bridges along its edge, under the first bridge's end).
// Those cells are blocked (BridgeShade) - unless that cuts a way the walk grid has: then a smaller part of the box, or
// nothing. Ground far below a deck (more than 4 m: an arch units walk under in plain sight, Ice_Bridge_01) and ground at or
// above it (the raised strips under the river bridges) stay as they are.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public static class Bridges
{
    static readonly Regex NAME = new("bridge", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    /// <summary>a bridge model (by name: every bridge class of the game is a CDecoColObj named so)</summary>
    public static bool IsBridge(string? model) => model != null && NAME.IsMatch(model);

    /// <summary>ground this far below the deck (m) is "under" it: a unit there is hidden by the deck or stands in it</summary>
    public const double UNDER_MIN = 0.5, UNDER_MAX = 4;
    /// <summary>the box widened by this much (m) on every side: a unit's body reaches out from its centre</summary>
    public const double MARGIN = 0.5;
    /// <summary>the parts of the box tried in turn when blocking all of it would cut a way</summary>
    static readonly double[] SCALES = { 1.0, 0.75, 0.5 };
    /// <summary>the cells around the shade looked at for cut ways (cells)</summary>
    const int WINDOW = 24;
    /// <summary>the widths checked: any unit (1 cell) and the big ones (3 cells of clearance: radius up to about 6 m)</summary>
    static readonly int[] NEEDS = { 1, 3 };

    /// <summary>
    /// the free cells under a bridge's deck that units should not walk (see the file's head), or null: the model's drawn
    /// box (ModelManifest.Box, the glb frame: x / z on the ground, as the pathfinder boxes after Prop.PfCells' turn) at
    /// (x, z) turned by rot, deckY the bridge's stored height. Deterministic: the manifest's box, the heights and the grid.
    /// </summary>
    public static List<int>? Shade(NavGrid nav, ModelManifest M, Func<double, double, double> height, string model, double x, double z, double rot, double deckY, double scale = 1)
    {
        if (M.Box(model) is not var (mn, mx)) return null;
        double c = SMath.Cos(rot), s = SMath.Sin(rot);
        double lx = (mn.X + mx.X) / 2 * scale, lz = (mn.Z + mx.Z) / 2 * scale;
        double cx = x + lx * c + lz * s, cz = z - lx * s + lz * c;
        double hx = (mx.X - mn.X) / 2 * scale, hz = (mx.Z - mn.Z) / 2 * scale;
        if (hx <= 0 || hz <= 0) return null;
        foreach (var f in SCALES)
        {
            var cells = new List<int>();
            foreach (var k in nav.RectCells(cx, cz, hx * f + MARGIN, hz * f + MARGIN, rot))
            {
                if (nav.Block[k] != 0) continue;
                var (px, pz) = nav.Center(k);
                double under = deckY - height(px, pz);
                if (under > UNDER_MIN && under < UNDER_MAX) cells.Add(k);
            }
            if (cells.Count == 0) return null;
            if (KeepsWays(nav, cells)) return cells;
        }
        return null;
    }

    /// <summary>
    /// would blocking these cells keep every way of the grid around them? For each width of NEEDS: the free ground of a
    /// window round the cells, cut into its connected parts (4 neighbours: the way finder's diagonal steps need both
    /// sides free) before and after - no part may fall apart. Looked at within the window only, so a way that went round
    /// outside it counts as cut: the check errs on the side of blocking less.
    /// </summary>
    static bool KeepsWays(NavGrid nav, List<int> cells)
    {
        int n = N(nav);
        int i0 = n, j0 = n, i1 = -1, j1 = -1;
        foreach (var k in cells) { int i = k % n, j = k / n; i0 = Math.Min(i0, i); i1 = Math.Max(i1, i); j0 = Math.Min(j0, j); j1 = Math.Max(j1, j); }
        int maxNeed = 0; foreach (var q in NEEDS) maxNeed = Math.Max(maxNeed, q);
        // the window, and around it the cells its clearance depends on
        int wi0 = Math.Max(0, i0 - WINDOW), wj0 = Math.Max(0, j0 - WINDOW), wi1 = Math.Min(n - 1, i1 + WINDOW), wj1 = Math.Min(n - 1, j1 + WINDOW);
        int oi0 = Math.Max(0, wi0 - maxNeed), oj0 = Math.Max(0, wj0 - maxNeed), oi1 = Math.Min(n - 1, wi1 + maxNeed), oj1 = Math.Min(n - 1, wj1 + maxNeed);
        int ow = oi1 - oi0 + 1, oh = oj1 - oj0 + 1;
        var before = new bool[ow * oh];
        for (int j = oj0; j <= oj1; j++) for (int i = oi0; i <= oi1; i++) before[(j - oj0) * ow + i - oi0] = nav.Block[j * n + i] != 0;
        var after = (bool[])before.Clone();
        foreach (var k in cells) after[(k / n - oj0) * ow + k % n - oi0] = true;
        foreach (var need in NEEDS)
        {
            var lb = Label(Fits(before, ow, oh, need), ow, oh, wi0 - oi0, wj0 - oj0, wi1 - oi0, wj1 - oj0);
            var fa = Fits(after, ow, oh, need);
            var la = Label(fa, ow, oh, wi0 - oi0, wj0 - oj0, wi1 - oi0, wj1 - oj0);
            // each part before: the parts after of its cells that still fit must be one
            var seen = new Dictionary<int, int>();
            for (int k = 0; k < lb.Length; k++)
            {
                if (lb[k] < 0 || la[k] < 0) continue;
                if (seen.TryGetValue(lb[k], out var was)) { if (was != la[k]) return false; }
                else seen[lb[k]] = la[k];
            }
        }
        return true;
    }
    static int N(NavGrid nav) => nav.N;

    /// <summary>cells of the window grid with `need` cells of clearance: no blocked cell within need - 1 (chessboard)</summary>
    static bool[] Fits(bool[] blocked, int w, int h, int need)
    {
        var fit = new bool[w * h];
        int r = need - 1;
        for (int j = 0; j < h; j++) for (int i = 0; i < w; i++)
        {
            bool ok = true;
            for (int dj = -r; dj <= r && ok; dj++) for (int di = -r; di <= r; di++)
            {
                int a = i + di, b = j + dj;
                if (a < 0 || b < 0 || a >= w || b >= h) continue;           // (beyond what was copied: as it is, free)
                if (blocked[b * w + a]) { ok = false; break; }
            }
            fit[j * w + i] = ok;
        }
        return fit;
    }

    /// <summary>connected parts (4 neighbours) of the fitting cells inside [i0, i1] x [j0, j1]; -1 elsewhere</summary>
    static int[] Label(bool[] fit, int w, int h, int i0, int j0, int i1, int j1)
    {
        var lab = new int[w * h];
        Array.Fill(lab, -1);
        var st = new Stack<int>();
        int id = 0;
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++)
        {
            int k0 = j * w + i;
            if (!fit[k0] || lab[k0] >= 0) continue;
            lab[k0] = id; st.Push(k0);
            while (st.Count > 0)
            {
                int k = st.Pop(), ci = k % w, cj = k / w;
                if (ci > i0 && fit[k - 1] && lab[k - 1] < 0) { lab[k - 1] = id; st.Push(k - 1); }
                if (ci < i1 && fit[k + 1] && lab[k + 1] < 0) { lab[k + 1] = id; st.Push(k + 1); }
                if (cj > j0 && fit[k - w] && lab[k - w] < 0) { lab[k - w] = id; st.Push(k - w); }
                if (cj < j1 && fit[k + w] && lab[k + w] < 0) { lab[k + w] = id; st.Push(k + w); }
            }
            id++;
        }
        return lab;
    }
}
