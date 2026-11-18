// Shore buildings (docs/spec/naval.md §1.2: coastal=1 - hu_harbour, ninigi_harbour, aje_floating_harbour,
// seas_carrier, the rally-point helpers): the engine's ObjPlaceChecker.CheckGetCoastal(cls, clickPos) -> (pos, rot)
// [?engine], as the spec's remake rule has it: the building stands ON the shore line - its landward half on ground, its
// seaward half in water - turned by the shore itself, its sea side (a harbour's dock Do_1, a moving harbour's bow)
// perpendicular to the coast, out to the open water. The normal place check is skipped (only this one counts).
//   ShoreNormal: the direction from the land to the water around a point (the water samples on a ring minus the
//                land ones); null where the point is not on a shore
//   CoastalAt:   whether the building may stand with its centre exactly here, and its rotation (what the placement
//                grid's dots show)
//   CoastalSnap: the nearest such spot to a click on the 2 m grid of the dots (the server's snap)
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public sealed partial class World
{
    /// <summary>how far around a point the shore is looked at (m) and the share of the footprint on either side</summary>
    const double SHORE_RING = 10, SHORE_SIDE = 0.75, SHORE_MIN = 0.2, DOCK_DEPTH = 1.5;
    const int SHORE_DIRS = 32;

    /// <summary>The direction from the land to the open water at (x, z), a unit vector: the sum of the ring's water
    /// directions minus its land directions (water = deeper than the land units' 0.4 m). Null when the ring is all land,
    /// all water or no clear side (a narrow spit, a pond smaller than the ring).</summary>
    public (double X, double Z)? ShoreNormal(double x, double z, double ring = SHORE_RING)
    {
        if (WaterLevel == null) return null;
        double sx = 0, sz = 0; int wet = 0;
        for (int k = 0; k < SHORE_DIRS; k++)
        {
            double a = k * 2 * Math.PI / SHORE_DIRS, dx = SMath.Cos(a), dz = SMath.Sin(a);
            bool w = IsWater(x + dx * ring, z + dz * ring, 0.4);
            if (w) wet++;
            sx += w ? dx : -dx; sz += w ? dz : -dz;
        }
        if (wet == 0 || wet == SHORE_DIRS) return null;
        double l = Math.Sqrt(sx * sx + sz * sz) / SHORE_DIRS;
        if (l < 0.25) return null;
        double n = Math.Sqrt(sx * sx + sz * sz);
        return (sx / n, sz / n);
    }

    /// <summary>the side of a shore building that faces the water, in its own frame: a moving harbour's bow (it sails
    /// forward along (-sin rot, -cos rot), Naval.cs), else the dock link Do_1 (where ships berth) from the footprint
    /// centre</summary>
    (double X, double Z) SeaSide(string name, Player? owner)
    {
        var def = Data.Def(name, owner);
        if (def != null && (def.Script == "CSeasCarrier" || def.Script == "CSwimmingHarbour")) return (0, -1);
        var (dx, dz) = DockOffset(name, owner);
        var f = FootprintOf(name, 0, 0, 0, false, owner);
        double lx = dx - f.Fx, lz = dz - f.Fz;
        return Math.Abs(lx) + Math.Abs(lz) < 0.5 ? (0, 1) : (lx, lz);
    }

    /// <summary>the rotation that turns the building's sea side along the shore normal n (rotation.y adds to the heading:
    /// a local vector of heading h points at h + rot)</summary>
    public double CoastalRot(string name, Player? owner, double nx, double nz)
    {
        var (lx, lz) = SeaSide(name, owner);
        return Geo.Wrap(Geo.HeadingTo(nx, nz) - Geo.HeadingTo(lx, lz));
    }

    /// <summary>Whether a shore building may stand with its centre on (x, z), and its rotation there (turned by the shore
    /// even when it may not, for the ghost): on a shore (ShoreNormal), the footprint inside the map, every cell of it
    /// free apart from the water (no buildings, trees, cliffs, reserved cells; no enemy units, no land resources), at
    /// least SHORE_SIDE of its landward cells on land and of its seaward cells in water, at least SHORE_MIN of all
    /// cells on either, and the sea side's end (the dock, the bow) at least 1.5 m deep (ships berth there; a moving
    /// harbour floats off).</summary>
    public (bool Ok, double Rot) CoastalAt(string name, double x, double z, Player? owner)
    {
        if (WaterNav == null || WetCell == null) return (false, 0);
        var n = ShoreNormal(x, z);
        if (n == null) return (false, 0);
        var (nx, nz) = n.Value;
        double rot = CoastalRot(name, owner, nx, nz);
        var f = FootprintOf(name, x, z, rot, false, owner);
        var P = Play;
        if (f.Fx - f.Hx < P.X0 + 4 || f.Fx + f.Hx > P.X1 - 4 || f.Fz - f.Hz < P.Z0 + 4 || f.Fz + f.Hz > P.Z1 - 4) return (false, rot);
        int land = 0, landDry = 0, sea = 0, seaWet = 0, wet = 0;
        foreach (var k in f.Cells)
        {
            // the shore is not walkable either (steep cells at the waterline are fine)
            if (Reserved[k] != 0 || Nav.Block[k] - WetCell[k] - (SteepCell != null ? SteepCell[k] : 0) > 0) return (false, rot);
            wet += WetCell[k];
            var (cx, cz) = Nav.Center(k);
            double s = (cx - f.Fx) * nx + (cz - f.Fz) * nz;
            if (s < -1) { land++; if (WetCell[k] == 0) landDry++; }
            else if (s > 1) { sea++; seaWet += WetCell[k]; }
        }
        int cells = Math.Max(1, f.Cells.Count);
        if (land == 0 || sea == 0 || landDry < SHORE_SIDE * land || seaWet < SHORE_SIDE * sea) return (false, rot);
        if (wet < SHORE_MIN * cells || cells - wet < SHORE_MIN * cells) return (false, rot);
        // the sea side's end: the dock link, or the footprint's edge along the normal
        double reach = Math.Max(f.Hx, f.Hz);
        if (!IsWater(f.Fx + nx * reach, f.Fz + nz * reach, DOCK_DEPTH)) return (false, rot);
        bool blocked = false;
        UHash.Query(f.Fx, f.Fz, f.Radius + 8, u => { if (u.Alive && u.Owner != owner && InBoxes(f.Boxes, rot, u.Pos.X, u.Pos.Z, u.Radius - 2.2)) blocked = true; });
        RHash.Query(f.Fx, f.Fz, f.Radius + 8, r => { if (r.Alive && !r.Water && InBoxes(f.Boxes, rot, r.Pos.X, r.Pos.Z, r.Radius - 2.2)) blocked = true; });
        return (!blocked, rot);
    }

    /// <summary>Coastal snap (ServerApp.usl:1143 CheckGetCoastal [?engine], naval.md §1.2): the nearest spot to the click,
    /// within 24 m, on the 2 m grid of the placement dots, where CoastalAt says yes - so the spot a click snaps to is
    /// always a green dot, and a click on a green dot stays there.</summary>
    public (double X, double Z, double Rot)? CoastalSnap(string name, double x, double z, Player? owner)
    {
        double S = PLACE_STEP, R = PLACE_GRID_R;
        double cx = Math.Round(x / S) * S, cz = Math.Round(z / S) * S;
        var spots = new List<(double D, double X, double Z)>();
        for (double dz = -R; dz <= R; dz += S)
            for (double dx = -R; dx <= R; dx += S)
            {
                double px = cx + dx, pz = cz + dz, d = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (d <= R * R) spots.Add((d, px, pz));
            }
        spots.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
        foreach (var (_, px, pz) in spots)
        {
            var (ok, rot) = CoastalAt(name, px, pz, owner);
            if (ok) return (px, pz, rot);
        }
        return null;
    }
}
