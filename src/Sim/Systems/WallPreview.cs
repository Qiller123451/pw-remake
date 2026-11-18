// The wall placer's preview, engine-free (docs/spec/walls.md §3.2, PreviewWall 0x4f9f70; ui/input.js wallPreview):
// the ghosts of a planned wall line - every new tile and every own wall piece next to it, each with the parts it will
// show once the line stands (the line is written into the wall map for a moment and taken out again) - and the
// placement grid's dots: the same rule as the placement itself, per dot (towers, gates, traps, ladders go through the
// wall placer, so a dot over an own wall piece is a valid spot for a tower: the footprint check would call it blocked).
// Ported from remake/src/ui/input.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using WMap = PwRemake.Sim.World.WallMap;

namespace PwRemake.Sim.World;

/// <summary>one ghost of the wall preview</summary>
public sealed class WallGhost
{
    public int I, J;
    public double X, Y, Z;
    /// <summary>"ok" / "have" (green), "cost" (yellow: cannot be paid), "bad" (red), "gate" (cyan: a gate would fit)</summary>
    public string State = "ok";
    /// <summary>an existing own piece next to the line: its own class (null = the line's class)</summary>
    public string? Name;
    /// <summary>wall pieces (type WALL): the parts the piece will show (WallMap.PartsOf); null = not a wall piece</summary>
    public List<string>? Parts;
}

public sealed partial class World
{
    /// <summary>The ghosts PreviewWall draws for a line from WallLineOf: the line's tiles (coloured by their state; a tile
    /// where a gate could go later cyan) and the owner's wall pieces next to them, each with the parts it will have when
    /// the line is built. The wall map is unchanged afterwards. Empty for no line.</summary>
    public List<WallGhost> WallPreviewOf(Player p, WallLine? line)
    {
        var res = new List<WallGhost>();
        var WM = Walls;
        if (WM == null || line == null || line.Count == 0) return res;
        var w = line.Wall;
        int me = WM.OwnerId(p);
        bool pieces = w.Type == WMap.WT.WALL;
        var seen = new HashSet<(int, int)>();
        WM.Begin();
        try
        {
            foreach (var t in line) WM.Write(t.I, t.J, me, w.Race, w.Cls, w.Type);
            void Add(int i, int j, string state, bool gateOK, string? name)
            {
                if (!seen.Add((i, j))) return;
                var (x, z) = WM.Centre(i, j);
                res.Add(new WallGhost
                {
                    I = i, J = j, X = x, Z = z, Name = name,
                    State = gateOK && state == "ok" ? "gate" : state,
                    Parts = pieces ? WM.PartsOf(i, j) : null,
                    Y = w.Type == WMap.WT.TRAP ? Height(x, z) : WM.ZOf(i, j),
                });
            }
            // (an own piece the line keeps - "have", of its class or a stronger one - shows as itself; a weaker one the line
            // builds over is a new tile: "ok" / "cost", the line's class)
            foreach (var t in line) Add(t.I, t.J, t.State, t.GateOK, t.State == "have" && WM.At(t.I, t.J)?.Wall is { Alive: true } h && h.Owner == p ? h.Name : null);
            if (pieces)
                foreach (var t in line)
                    for (int d = 0; d < 8; d++)
                    {
                        int ni = t.I + WMap.DX[d], nj = t.J + WMap.DZ[d];
                        if (WM.At(ni, nj)?.Wall is { Alive: true } o && o.Owner == p) Add(ni, nj, "ok", false, o.Name);
                    }
        }
        finally { WM.Restore(); }
        return res;
    }

    /// <summary>The placement grid's spacing and reach (ObjPlaceChecker.EnableGridRendering, ui/input.js): a dot every 2 m
    /// within 24 m of the cursor; the ghost of a building with a grid stands on these dots only.</summary>
    public const double PLACE_STEP = 2, PLACE_GRID_R = 24;

    /// <summary>The one placement rule for a spot, shared by the placement grid's dots, the ghost and the server's
    /// StartConstruction: PlacementOf(...).Ok - the footprint check (CanPlace: the map's border, nav cells blocked by
    /// steep slopes, water, buildings, trees and stones, reserved cells, enemy units, resources) for a plain building,
    /// the wall placer for wall-map objects (walls.md §3.1 / §5: an own wall piece is a valid tile for a tower, a gate
    /// needs the pieces on both sides), for shore buildings the shore rule with the centre on this very spot (CoastalAt;
    /// a placement there stays there, Coastal.cs).</summary>
    public bool PlaceSpotOk(string name, double x, double z, double rot, Player? owner)
    {
        // a shore building: exactly here (PlacementOf snaps a click to the nearest such dot: CoastalSnap)
        if (Data.Def(name, owner) is { Coastal: true }) return CoastalAt(name, x, z, owner).Ok;
        return PlacementOf(name, x, z, rot, owner).Ok;
    }

    /// <summary>The dots of the placement grid around (x0, z0): every PLACE_STEP m within r, each with PlaceSpotOk for the
    /// building's centre on that dot (turned by rot) - exactly what a placement there gets. Wall-map objects are asked
    /// once per 8 m tile (every dot of a tile gives the same answer).</summary>
    public List<(double X, double Z, bool Ok)> PlaceGridOf(string name, double x0, double z0, double rot, Player? owner, double r = PLACE_GRID_R)
    {
        double S = PLACE_STEP, cx = Math.Round(x0 / S) * S, cz = Math.Round(z0 / S) * S;
        var def = Data.Def(name, owner);
        var perTile = def != null && (def.WallKind != null || def.Ladder) && Walls != null ? new Dictionary<(int, int), bool>() : null;
        var res = new List<(double, double, bool)>();
        for (double dz = -r; dz <= r; dz += S)
            for (double dx = -r; dx <= r; dx += S)
            {
                if (dx * dx + dz * dz > r * r) continue;
                double x = cx + dx, z = cz + dz;
                bool ok;
                if (perTile != null)
                {
                    var tile = Walls!.TileOf(x, z);
                    if (!perTile.TryGetValue(tile, out ok)) perTile[tile] = ok = PlaceSpotOk(name, x, z, rot, owner);
                }
                else ok = PlaceSpotOk(name, x, z, rot, owner);
                res.Add((x, z, ok));
            }
        return res;
    }
}
