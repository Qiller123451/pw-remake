// The merged outline of the range markers of one type (no Godot types: tests/PwRemake.Tests links this file).
//
// MIRAGE's range markers (CRangeMarkerMgr) draw every ring on its own; the merge patch of the PWClient.exe
// (/home/claude/mirage_ref/rangemarkers_README.md "Overlapping markers", patch_rangemarker_merge.py) leaves out every
// segment of a ring whose middle point lies inside another ring of the same type (the same two colours), with a
// margin of 0.05 m: what is left is the outer outline of all rings of one type; a ring wholly inside a bigger one
// disappears; rings that only touch and identical rings (two units on one spot) stay. The engine cuts a ring into
// 64 + 1.5 x range segments. Here the same segments are kept, with the patch's limit of 32 neighbours per ring lifted
// (every overlapping ring counts), and without its per-frame cost: the outline is worked out only when a ring moved
// or changed (RangeMarkers.cs), from the arcs the rings cover (Build's remarks).
using System;
using System.Collections.Generic;

namespace PwRemake.View.Entities;

/// <summary>A ring of a range marker on the ground: centre and radius (m).</summary>
public readonly record struct MarkerCircle(double X, double Z, double R);

/// <summary>A drawn piece of a ring: segments First .. First + Count - 1 of the ring's N segments (segment k runs from
/// angle k·2π/N to (k+1)·2π/N).</summary>
public readonly record struct MarkerArc(int Circle, int First, int Count, int N);

public static class RangeOutline
{
    /// <summary>the patch's margin: a segment is hidden only when its middle is this much inside another ring</summary>
    public const double MARGIN = 0.05;

    /// <summary>the engine's segment count of a ring (64 + 1.5 x range; at most 1024)</summary>
    public static int Segments(double r) => Math.Clamp((int)(64 + 1.5 * r), 64, 1024);

    /// <summary>the arcs of the merged outline of these rings (all of one type), appended to `arcs` (cleared first).
    /// `scratch` is reused between calls (no allocation once it has grown).</summary>
    /// <remarks>The patch tests every segment's middle against the kept rings; here the same answer comes from the
    /// angles: the middle of a segment of ring i lies inside ring j (radius less the margin) exactly when its angle is
    /// within the arc of i that j covers - the centre's direction ± acos((d² + ri² - rj²) / 2·d·ri). The covered arcs of
    /// a ring are joined (sorted by their start) and the segments outside them kept: O(n² + segments) for n rings.</remarks>
    public static void Build(IReadOnlyList<MarkerCircle> rings, List<MarkerArc> arcs, List<(double A0, double A1)>? scratch = null)
    {
        arcs.Clear();
        scratch ??= new List<(double, double)>();
        int n = rings.Count;
        for (int i = 0; i < n; i++)
        {
            var c = rings[i];
            if (!(c.R > 0)) continue;
            scratch.Clear();
            bool hidden = false;
            for (int j = 0; j < n && !hidden; j++)
            {
                if (j == i) continue;
                var o = rings[j];
                double rj = o.R - MARGIN;
                if (!(rj > 0)) continue;
                double dx = o.X - c.X, dz = o.Z - c.Z, d = Math.Sqrt(dx * dx + dz * dz);
                if (d >= c.R + rj) continue;                 // apart, touching
                if (d + c.R < rj) { hidden = true; break; }  // wholly inside o
                if (d + rj <= c.R) continue;                 // o inside this ring (or the same ring): covers none of it
                double ca = (d * d + c.R * c.R - rj * rj) / (2 * d * c.R);
                double al = Math.Acos(Math.Clamp(ca, -1, 1)), th = Math.Atan2(dz, dx);
                double a0 = th - al, a1 = th + al;
                a0 -= Math.Floor(a0 / Math.Tau) * Math.Tau; a1 = a0 + 2 * al;
                if (a1 > Math.Tau) { scratch.Add((a0, Math.Tau)); scratch.Add((0, a1 - Math.Tau)); }
                else scratch.Add((a0, a1));
            }
            if (hidden) continue;
            int N = Segments(c.R);
            if (scratch.Count == 0) { arcs.Add(new MarkerArc(i, 0, N, N)); continue; }
            scratch.Sort(ByStart);
            double step = Math.Tau / N;
            // walk the segments and the joined covered arcs together: segment k is kept when its middle is in no arc
            int iv = 0, runStart = -1, firstCount = -1;
            double coverEnd = double.NegativeInfinity;
            for (int k = 0; k < N; k++)
            {
                double m = (k + 0.5) * step;
                while (iv < scratch.Count && scratch[iv].A0 < m) { coverEnd = Math.Max(coverEnd, scratch[iv].A1); iv++; }
                bool inside = m < coverEnd;            // (open arcs: a middle exactly on an end is outside, as the distance test)
                if (!inside) { if (runStart < 0) runStart = k; }
                else if (runStart >= 0)
                {
                    if (runStart == 0) firstCount = k; else arcs.Add(new MarkerArc(i, runStart, k - runStart, N));
                    runStart = -1;
                }
            }
            if (runStart >= 0)
            {
                if (runStart == 0) arcs.Add(new MarkerArc(i, 0, N, N));                                          // nothing covered
                else arcs.Add(new MarkerArc(i, runStart, N - runStart + Math.Max(0, firstCount), N));             // (wraps round: joins the run at 0)
            }
            else if (firstCount > 0) arcs.Add(new MarkerArc(i, 0, firstCount, N));
        }
    }

    static readonly Comparison<(double A0, double A1)> ByStart = (x, y) => x.A0.CompareTo(y.A0);

    /// <summary>the total number of segments drawn (tests, the performance probe)</summary>
    public static int Drawn(List<MarkerArc> arcs) { int s = 0; foreach (var a in arcs) s += a.Count; return s; }
}
