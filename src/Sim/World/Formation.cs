// Group walk formations: where each unit of a group goes when the group is sent somewhere.
//
// Reference: docs/spec/formations.md (from the original scripts). The player draws an arrow with the right mouse
// button (GameInputController.usl: the order goes to the press point, facing the arrow, the spacing grows with its
// length); a plain right-click and the command bar's orders face from the group to the target with the spacing
// 2.7807 (SecondaryInputController.usl). The layout itself is the engine's (not in the scripts); its inputs are the
// units' formation attributes (FightingObj.UpdateFormationAttribs): "ranged weapons are placed in the rear and close
// combat units to the fore" (the game's help), the toughest units in the middle.
//
// Ported from remake/src/game/formation.js (pw-exporter 98c7703). (Array.prototype.sort is stable: the sorts here are
// LINQ OrderBy, which is stable too.)
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public static class Formation
{
    /// <summary>FrmSpc of the command bar's walk and attack orders</summary>
    public const double SPACING = 2.7807;
    static double Clamp(double v, double a, double b) => Math.Max(a, Math.Min(b, v));

    /// <summary>
    /// formation_y: 0 front .. 1 rear by the reach of the unit's weapon (the tech tree's formation_pos overrides it:
    /// workers 1, healers 0.2, jetpack warriors / ice spearmen / Stina 0); formation_x: 0 centre .. 1 side, tougher
    /// (hit points through protection) nearer the centre
    /// </summary>
    public static (double X, double Y) FormationAttribs(World W, Unit u)
    {
        var cs = u.Cs(u.Weapons.Long);
        double y = (cs != null ? W.AttackRangeOf(u, cs) + 1 : 1) / 75 - (cs != null ? cs.Dmg : 0) / 10000;
        double fp = u.Def?.FormationPos ?? -1;
        if (fp >= 0) y = fp;
        double eff = 100 * u.MaxHp / Clamp(100 - (cs != null ? cs.Prot : 0), 1, 100);
        return (1 - Math.Min(eff / 3000, 1), Clamp(y, 0, 1));
    }

    /// <summary>
    /// the places of `units` around (x, z), facing `dir` (radians, atan2(dz, dx) of the direction the group looks to),
    /// `spacing` metres apart: [(unit, x, z), ...]. Rows across the facing, the front row first; in a row the toughest
    /// in the middle, and of two places left and right of it the unit that stands more to that side takes it (so the
    /// paths don't cross).
    /// </summary>
    public static List<(Unit U, double X, double Z)> FormationSlots(World W, IReadOnlyList<Unit> units, double x, double z, double dir, double spacing = SPACING)
    {
        int n = units.Count;
        var res = new List<(Unit, double, double)>();
        if (n == 0) return res;
        double fx = SMath.Cos(dir), fz = SMath.Sin(dir), rx = -fz, rz = fx;
        double sp = Math.Max(spacing, units.Max(u => u.Radius) * 2 + 0.6);
        int cols = (int)Math.Ceiling(Math.Sqrt(n * 1.6));
        var att = new Dictionary<Unit, (double X, double Y)>();
        foreach (var u in units) att[u] = FormationAttribs(W, u);
        var sorted = units.OrderBy(u => att[u].Y).ThenBy(u => att[u].X).ToList();
        var rows = new List<List<Unit>>();
        for (int i = 0; i < n; i += cols) rows.Add(sorted.Skip(i).Take(cols).ToList());
        double Side(Unit u) => (u.Pos.X - x) * rx + (u.Pos.Z - z) * rz;
        var slots = new List<(Unit U, double Lat, double Lon)>();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            double lon = ((rows.Count - 1) / 2.0 - r) * sp;             // the front row ahead of the point
            int m = row.Count;
            var offs = new List<double>();
            for (int c = 0; c < m; c++) offs.Add(c - (m - 1) / 2.0);
            offs = offs.OrderBy(a => Math.Abs(a)).ThenBy(a => a).ToList();
            var byX = row.OrderBy(a => att[a].X).ToList();
            for (int i = 0; i < m;)
            {
                double o = offs[i];
                if (i + 1 < m && Math.Abs(offs[i + 1]) == Math.Abs(o) && o != 0)
                {
                    // a pair of places -o / +o: the unit more to the left takes the left one
                    Unit p = byX[i], q = byX[i + 1];
                    if (Side(p) > Side(q)) (p, q) = (q, p);
                    slots.Add((p, -Math.Abs(o), lon)); slots.Add((q, Math.Abs(o), lon));
                    i += 2;
                }
                else { slots.Add((byX[i], o, lon)); i++; }
            }
        }
        // a place on blocked ground (the shore, a building) moves to the nearest free cell - one no other unit of the
        // group got: several places off the water used to land on the same cell, and those ships on one spot
        var taken = new HashSet<(NavGrid, int)>();
        foreach (var (u, lat, lon) in slots)
        {
            double px = x + rx * lat * sp + fx * lon, pz = z + rz * lat * sp + fz * lon;
            var nv = W.NavFor(u);
            if (!nv.IsFree(px, pz))
            {
                int c = nv.Idx(px, pz), k = nv.NearestFree(c, 6);
                if (taken.Contains((nv, k))) k = FreeUntaken(nv, c, 6, taken, k);
                (px, pz) = nv.Center(k);
            }
            taken.Add((nv, nv.Idx(px, pz)));
            res.Add((u, px, pz));
        }
        return res;
    }

    /// <summary>the nearest free cell around c (rings up to maxR) that is not taken; dflt when there is none</summary>
    static int FreeUntaken(NavGrid nv, int c, int maxR, HashSet<(NavGrid, int)> taken, int dflt)
    {
        int n = nv.N, ci = c % n, cj = c / n;
        for (int r = 1; r <= maxR; r++)
        {
            int best = -1; double bd = 1e9;
            for (int j = cj - r; j <= cj + r; j++)
                for (int i = ci - r; i <= ci + r; i++)
                {
                    if (i < 0 || j < 0 || i >= n || j >= n || Math.Max(Math.Abs(i - ci), Math.Abs(j - cj)) != r) continue;
                    int k = j * n + i;
                    if (nv.Block[k] != 0 || taken.Contains((nv, k))) continue;
                    double d = (double)(i - ci) * (i - ci) + (double)(j - cj) * (j - cj);
                    if (d < bd) { bd = d; best = k; }
                }
            if (best >= 0) return best;
        }
        return dflt;
    }
}
