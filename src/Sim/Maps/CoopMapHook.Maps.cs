// The map work's side of the co-op map hook (src/Sim/Coop/CoopMapHook.cs, docs/COOP.md "Maps"): before the world is
// built, the mission's map is extended and given resources by the co-op definition (MapExtension.Apply, for the seats
// in use only - an empty seat 3 leaves out its own land and resources), the session's map source is made again from
// the extended map, and the co-op seats' starts - written in the original map's coordinates, as the mission's own
// positions are - are turned into the game coordinates the core places them at.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Coop;

public static partial class CoopMapHook
{
    static readonly Logger L = Log.For("Coop.Maps");

    static partial void BeforeWorld(CoopMapContext ctx)
    {
        var S = ctx.Map;
        var plan = CoopMapPlan.FromJson(ctx.Definition.Raw, ctx.SeatsInUse);
        if (S.Md is { } md && S.Rebuild != null && !plan.Empty)
        {
            var ext = MapExtension.Apply(md, plan);
            if (!ReferenceEquals(ext, md))
            {
                S.CopyFrom(S.Rebuild(ext));
                foreach (var line in ext.Extension?.Log ?? new()) L.Info($"mission {ctx.Campaign.Id}: {line}");
            }
        }
        // `map_objects`: attributes of the mission's own objects changed in the co-op game (mission 1: the beach nest
        // sends its two Dilophosaurs together) - on a copy (single player's map data stays as it is)
        var mos = ctx.Definition.MapObjects.Where(m => m.Seats <= ctx.SeatsInUse.Count).ToList();
        if (mos.Count > 0 && S.Md is { } md2 && S.Rebuild != null)
        {
            var cp = MapExtension.Clone(md2);
            int n = 0;
            for (int i = 0; i < cp.Objects.Count; i++)
            {
                var o = cp.Objects[i];
                var m = mos.FirstOrDefault(q => q.Name == o.Name);
                if (m == null) continue;
                var no = new MapObject { Type = o.Type, Name = o.Name, Cls = o.Cls, X = o.X, Y = o.Y, Z = o.Z, Rot = o.Rot, Q = o.Q, Owner = o.Owner, At = o.At, Level = o.Level, Attr = new Dictionary<string, string>(o.Attr) };
                foreach (var (k, v) in m.Attr) no.Attr[k] = v;
                cp.Objects[i] = no; n++;
            }
            if (n > 0) S.CopyFrom(S.Rebuild(cp));
            foreach (var m in mos) if (!cp.Objects.Any(o => o.Name == m.Name)) ctx.Problems.Add($"co-op map_objects: no object {m.Name}");
        }
        PaveGates(ctx);
        // the seats' starts: map coordinates of the original map -> game coordinates of the (extended) map
        foreach (var (seat, sd) in ctx.Definition.Seats)
        {
            if (ctx.Definition.Raw["seats"]?[seat.ToString()]?["start"] is not System.Text.Json.Nodes.JsonObject st) continue;
            var (x, z) = S.ToGame(st["x"].Num(), st["z"].Num());
            sd.X = x; sd.Z = z;
        }
    }

    /// <summary>half extents (m) of the area round a gate whose pavement is copied: along the wall, through the gate</summary>
    const double PAVE_ALONG = 28, PAVE_THROUGH = 32;
    /// <summary>the way through a gate (PaveGates): half its width, where it starts (the gate's own depth) and how far
    /// it may go to reach walkable ground (m)</summary>
    const double PASS_HALF = 3.5, PASS_FROM = 4, PASS_MAX = 36;

    /// <summary>
    /// `gates`: a gate put in place of wall pieces (CoopRuntime.Gates) gets the pavement of the mission's own gate of
    /// that class. The original paints it into the ground (mission 13's hcl13_gate_3: ground material 5, the paving stones,
    /// a road through the gate and a square on the city side - no road object, nothing in the gate's model): the ground
    /// material of the gate's opening is the paving; each 4 m cell round the new gate (PAVE_ALONG x PAVE_THROUGH) that
    /// lies, in the gate's own frame, on the paving round the mission's gate is painted with it. On a copy of the map
    /// (single player's stays as it is), where the new gate will stand (CoopRuntime.GatePlace, as Gates places it).
    /// </summary>
    static void PaveGates(CoopMapContext ctx)
    {
        var S = ctx.Map;
        if (S.Md is not { } md || S.Rebuild == null || md.Mats.Length == 0) return;
        byte[]? mats = null, walk = null;
        foreach (var g in ctx.Definition.Gates.Where(g => g.Seats <= ctx.SeatsInUse.Count))
        {
            var cls = ctx.Campaign.RuleClass(g.Class) ?? g.Class;
            var gfx = ctx.Rules.Stats(cls, 1, null)?.Gfx;
            // a map object as the world places it: origin (game), turn, footprint
            (double X, double Z, double Rot, PwRemake.Sim.World.Footprint.Placed F) Placed(MapObject o, string? ogfx)
            {
                var (x, z) = S.ToGame(o.X, o.Y);
                double rot = Frame.MapHeading(o.Rot);
                return (x, z, rot, PwRemake.Sim.World.Footprint.Place(ctx.Models, ogfx, x, z, rot));
            }
            var pieces = md.Objects.Where(o => g.Replaces.Contains(o.Name))
                .Select(o => Placed(o, ctx.Rules.Stats(ctx.Campaign.RuleClass(o.Cls) ?? o.Cls, 1, null)?.Gfx)).Select(p => (p.F.Boxes, p.Rot)).ToList();
            var own = md.Objects.FirstOrDefault(o => o.Type == "BLDG" && (o.Cls == g.Class || o.Cls == cls) && !g.Replaces.Contains(o.Name));
            if (pieces.Count == 0 || own == null || CoopRuntime.GatePlace(ctx.Models, gfx, pieces) is not { } at) continue;
            var og = Placed(own, gfx);
            double oc = SMath.Cos(og.Rot), os = SMath.Sin(og.Rot);
            var nf = PwRemake.Sim.World.Footprint.Place(ctx.Models, gfx, at.X, at.Z, at.Rot);
            double nc = SMath.Cos(at.Rot), ns = SMath.Sin(at.Rot);
            // the ground under the mission's gate at (bx, bz) of its frame (x along the wall, z through the opening)
            int OwnAt(double bx, double bz)
            {
                var (mx, my) = S.ToMap(og.F.Fx + bx * oc + bz * os, og.F.Fz - bx * os + bz * oc);
                return md.MaterialAtMap(mx, my);
            }
            // the paving: the most frequent material in the opening
            var count = new int[8];
            for (double bx = -6; bx <= 6; bx += 2)
                for (double bz = -16; bz <= 16; bz += 2) if (OwnAt(bx, bz) is int m and >= 0) count[m]++;
            int pave = Array.IndexOf(count, count.Max());
            if (count[pave] == 0) continue;
            mats ??= (byte[])md.Mats.Clone();
            var (cx, cy) = S.ToMap(nf.Fx, nf.Fz);
            double R = Math.Max(PAVE_ALONG, PAVE_THROUGH);
            int i0 = Math.Max(0, (int)Math.Floor((cx - R - md.OffX) / 4)), i1 = Math.Min(md.Mx - 1, (int)Math.Floor((cx + R - md.OffX) / 4));
            int j0 = Math.Max(0, (int)Math.Floor((cy - R - md.OffY) / 4)), j1 = Math.Min(md.My - 1, (int)Math.Floor((cy + R - md.OffY) / 4));
            int n = 0;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var (x, z) = S.ToGame(md.OffX + 4 * i + 2, md.OffY + 4 * j + 2);
                    double dx = x - nf.Fx, dz = z - nf.Fz;
                    double bx = dx * nc - dz * ns, bz = dx * ns + dz * nc;     // (Footprint.Place's turn, inverted)
                    if (bx * bx / (PAVE_ALONG * PAVE_ALONG) + bz * bz / (PAVE_THROUGH * PAVE_THROUGH) > 1 || OwnAt(bx, bz) != pave) continue;
                    int k = j * md.Mx + i;
                    if ((mats[k] & 7) != pave) { mats[k] = (byte)((mats[k] & ~7) | pave); n++; }
                }
            L.Info($"mission {ctx.Campaign.Id}: {g.Class} in place of {string.Join(", ", g.Replaces)} paved like {own.Name} (material {pave}, {n} cells)");
            // the way through it: the map's land grid (PaFi) leaves the ground on both sides of a wall unwalkable where
            // houses stand behind it (13: 13 m outside, 31 m inside the city to the street's sidewalk) - a lane of
            // PASS_HALF either side of the gate's middle line, from the gate to the first walkable ground on each side (at
            // most PASS_MAX), not under houses and other tall scenery; none if a side has no walkable ground that near
            // (only a gate class: base's hcl13_gate is a plain building, shut like the mission's own - a lane would lead
            // to its thin door, which the pathfinder's cells do not close everywhere)
            if (md.Walk is { } wg && ctx.Rules.Def(cls, null)?.Gate == true)
            {
                bool Walk(byte[] c, double mx, double my) { double i = Math.Floor((mx - wg.OffX) / 2), j = Math.Floor((my - wg.OffY) / 2); return i >= 0 && j >= 0 && i < wg.W && j < wg.H && c[(int)j * wg.W + (int)i] == 1; }
                double? Reach(int sgn)
                {
                    for (double d = PASS_FROM; d <= PASS_MAX; d += 1)
                    {
                        double px = cx + ns * d * sgn, py = cy + nc * d * sgn;
                        if (Walk(wg.Cells, px, py) && Walk(wg.Cells, px + ns * sgn * 2, py + nc * sgn * 2)) return d;
                    }
                    return null;
                }
                // the houses and other tall scenery near it (footprints of models at least 1.5 m high and 3 m across):
                // the lane goes round them, never through
                var tall = new List<double[]>();
                foreach (var dc in S.Decor)
                {
                    if (Math.Abs(dc.X - nf.Fx) > 60 || Math.Abs(dc.Z - nf.Fz) > 60 || ctx.Models.Box(dc.Model) is not var (bmn, bmx) || bmx.Y - bmn.Y < 1.5) continue;
                    foreach (var q in PwRemake.Sim.World.Footprint.Place(ctx.Models, dc.Model, dc.X, dc.Z, dc.Rot).Boxes)
                        if (Math.Max(q[2], q[3]) * dc.Scale >= 1.5) tall.Add(new[] { q[0], q[1], q[2] * dc.Scale + 0.5, q[3] * dc.Scale + 0.5, dc.Rot });
                }
                bool UnderTall(double x, double z) => tall.Any(q =>
                {
                    double c = SMath.Cos(q[4]), sn = SMath.Sin(q[4]), dx = x - q[0], dz = z - q[1];
                    return Math.Abs(dx * c - dz * sn) <= q[2] && Math.Abs(dx * sn + dz * c) <= q[3];
                });
                if (Reach(1) is double d1 && Reach(-1) is double d0)
                {
                    walk ??= (byte[])wg.Cells.Clone();
                    int m = 0;
                    for (double d = -d0 - 1; d <= d1 + 1; d += 0.5)
                        for (double a = -PASS_HALF; a <= PASS_HALF; a += 0.5)
                        {
                            double px = cx + ns * d + nc * a, py = cy + nc * d - ns * a;
                            int i = (int)Math.Floor((px - wg.OffX) / 2), j = (int)Math.Floor((py - wg.OffY) / 2);
                            if (i < 0 || j < 0 || i >= wg.W || j >= wg.H || walk[j * wg.W + i] == 1) continue;
                            var (lx, lz) = S.ToGame(wg.OffX + 2 * i + 1, wg.OffY + 2 * j + 1);
                            if (UnderTall(lx, lz)) continue;
                            walk[j * wg.W + i] = 1; m++;
                        }
                    L.Info($"mission {ctx.Campaign.Id}: a way through {g.Class}: {d0:0} m / {d1:0} m to the walkable ground, {m} land grid cells opened");
                }
                else { ctx.Problems.Add($"co-op gate {g.Class}: no walkable ground within {PASS_MAX} m of both its sides"); L.Warn($"mission {ctx.Campaign.Id}: no way through {g.Class}: walkable ground at {Reach(-1)?.ToString() ?? "-"} / {Reach(1)?.ToString() ?? "-"} m"); }
            }
        }
        if (mats == null) return;
        var cp = MapExtension.Clone(md);
        cp.Mats = mats;
        if (walk != null) cp.Walk = new WalkGrid { W = md.Walk!.W, H = md.Walk.H, OffX = md.Walk.OffX, OffY = md.Walk.OffY, Cells = walk };
        S.CopyFrom(S.Rebuild(cp));
    }
}
