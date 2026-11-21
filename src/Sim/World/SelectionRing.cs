// The selection circle's radius (the view's ring, hover ring, health bar width).
// The original's rule (PWClient.exe, 2026-11-20; docs/VIEW.md "Selection circles"): the circle stands at the middle of
// the model's GSF box, turned with the object, and its size comes from the box's two ground sides a, b:
// r = max(0.5 sqrt(a b), 0.33 a, 0.33 b), 1 when r <= 1, drawn max(1.1 r, 1.8) - ModelManifest.OriginalRing, from
// assets/selboxes.json (tools/prepare/selection_boxes.py). Without that file (data prepared before it) the remake's own
// circle as before: round the model's drawn extent on the ground (ModelManifest.SelReach), never smaller than the old
// body / footprint ring.
using System;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public static class SelectionRing
{
    /// <summary>the largest circle a unit gets (a titan stays below it)</summary>
    public const double MAX = 16;
    /// <summary>a building's circle at most this times its footprint's (a box with hidden upgrade / construction parts)</summary>
    public const double BLDG_MAX = 1.6;

    /// <summary>
    /// radius of an object's selection circle: a unit's at least a bit wider than its body (radius * 1.25 + 0.2), a
    /// building's at least its footprint (radius * 1.05) or a flat plot's whole pick volume (pickR); both round the
    /// model's reach (reach: SelReach, 0 unknown) plus a small margin - a unit's at most MAX, a building's at most
    /// BLDG_MAX times its footprint radius.
    /// </summary>
    public static double Radius(bool building, double radius, double reach, double pickR = 0)
    {
        double body = building ? Math.Max(radius * 1.05, pickR) : radius * 1.25 + 0.2;
        double model = reach > 0 ? Math.Min(reach * 1.05 + 0.15, building ? radius * BLDG_MAX : MAX) : 0;
        return Math.Max(body, model);
    }

    public static double Of(Entity e) => Original(e) is { } o ? o.R : Radius(e is Building, e.Radius, e.SelReach, e is Building b ? b.PickR : 0);

    /// <summary>the original's circle of the object's model (ModelManifest.OriginalRing): radius and the circle's middle
    /// in the world (x, z) relative to the object's place - the box's middle turned as the model is drawn (a building by
    /// its Rot, everything else by its Heading: EntityViews); null without the GSF boxes</summary>
    public static (double R, double Cx, double Cz)? Original(Entity e)
    {
        if (e.World?.Models.OriginalRing(e.Gfx) is not { } o) return null;
        double h = e is Building b ? b.Rot : e.Heading, c = SMath.Cos(h), s = SMath.Sin(h);
        return (o.R, o.X * c + o.Z * s, -o.X * s + o.Z * c);
    }

    /// <summary>a building's footprint at least this many times as long as wide has its circle round its footprint's middle (Shape)</summary>
    public const double LONG = 1.5;

    /// <summary>
    /// the ring: always a circle (Kacper 2026-11-12: "get rid of this elongated selection and make everything a circle" -
    /// the long ring of 2026-11-10, a stadium along walls, gates, harbours and ships, is gone), of radius R round the
    /// object's place moved by (Cx, Cz) (world x, z). Ax, Az stay 0 (kept for the drawing code: a circle drawn out along
    /// (Ax, Az) would be a stadium).
    /// A long building (footprint LONG times as long as wide: walls, gates, harbours, carriers, long halls) gets the circle
    /// round its footprint's middle - a ship's or a hall's place need not be the middle of its footprint (the SEAS
    /// carrier's ring lay beside its hull) - just big enough for the footprint's corners (a building circle's 5 % margin),
    /// not the circle round the model's reach from its place (Of: 30 - 36 m for co-op 13's second gate). A sailing
    /// building (its boxes are those of its last stop) and every other object: Of round its place.
    /// tests SelectionRingTests.LongBuildings lists them.
    /// </summary>
    public static (double R, double Ax, double Az, double Cx, double Cz) Shape(Entity e)
    {
        // the original's circle (the box's middle, turned with the object) wherever the GSF boxes are known
        if (Original(e) is { } o) return (o.R, 0, 0, o.Cx, o.Cz);
        if (e is not Building b || b.Boxes.Length == 0 || b.PickR > 0 || b.Sail != null) return (Of(e), 0, 0, 0, 0);
        double c = SMath.Cos(b.Rot), s = SMath.Sin(b.Rot);
        // the footprint in the building's own frame round its middle (Footprint.Place's turn, inverted)
        double x0 = 1e9, x1 = -1e9, z0 = 1e9, z1 = -1e9;
        foreach (var q in b.Boxes)
        {
            double dx = q[0] - b.Pos.X, dz = q[1] - b.Pos.Z;
            double lx = dx * c - dz * s, lz = dx * s + dz * c;
            x0 = Math.Min(x0, lx - q[2]); x1 = Math.Max(x1, lx + q[2]); z0 = Math.Min(z0, lz - q[3]); z1 = Math.Max(z1, lz + q[3]);
        }
        double hx = (x1 - x0) / 2, hz = (z1 - z0) / 2;
        if (Math.Max(hx, hz) < Math.Max(0.5, LONG * Math.Min(hx, hz))) return (Of(e), 0, 0, 0, 0);
        // the footprint's middle back in the world (local (lx, lz) -> world (lx c + lz s, -lx s + lz c))
        double mx = (x0 + x1) / 2, mz = (z0 + z1) / 2, cx = mx * c + mz * s, cz = -mx * s + mz * c;
        double r = Math.Sqrt(hx * hx + hz * hz) * 1.05 + 0.15;
        return (r, 0, 0, cx, cz);
    }
}
