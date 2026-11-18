// Shore buildings (src/Sim/Systems/Coastal.cs, docs/spec/naval.md §1.2): every tribe's harbour - hu_harbour,
// ninigi_harbour, aje_floating_harbour, seas_carrier (MIRAGE) - stands on the shore line, half on land and half in the
// water, turned with its sea side (the dock, a moving harbour's bow) straight out to the open water; the placement
// grid's dots are that rule per spot, and a click snaps to the nearest green dot.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys2CoastalTests
{
    /// <summary>a straight coast along a line at angle `a` (the water on the side of the normal (cos a, sin a)): land 2 m
    /// high, a 10 m beach down to the sea 4 m deep; water level 0</summary>
    static FlatMap Coast(double a) => new(256)
    {
        Water = 0,
        HeightFn = (x, z) => { double s = x * Math.Cos(a) + z * Math.Sin(a); return s < 0 ? 2 : s < 10 ? 2 - s * 0.6 : -4; },
    };

    static (W_ W, Player P) World(TestContext t, string data, string tribe, double a)
    {
        var (R, M) = Sys2.Load(t, data);
        var w = new W_(R, M, Coast(a), new WorldOptions { Seed = 7 });
        var p = new Player(1, tribe, new PlayerOpts { Rules = R, Team = 1 });
        w.Players.Add(p);
        p.CapsFixed = true; foreach (var r in PwRemake.Sim.Rules.Rules.RES) { p.Caps[r] = 10000; p[r] = 5000; }
        return (w, p);
    }

    static readonly (string Data, string Tribe, string Name)[] HARBOURS =
    {
        ("base", "Hu", "hu_harbour"), ("base", "Ninigi", "ninigi_harbour"), ("base", "Aje", "aje_floating_harbour"), ("mirage", "SEAS", "seas_carrier"),
    };

    public static void ShoreNormalAndTurn(TestContext t)
    {
        foreach (var a in new[] { 0.0, Math.PI / 2, 2.3, -0.7 })
        {
            var (w, _) = World(t, Sys2.Base, "Hu", a);
            double nx = Math.Cos(a), nz = Math.Sin(a);
            var n = w.ShoreNormal(5 * nx, 5 * nz);
            t.True(n != null && n.Value.X * nx + n.Value.Z * nz > 0.97, $"angle {a:0.00}: the normal points out to the water ({n})");
            t.True(w.ShoreNormal(-40 * nx, -40 * nz) == null, "inland: no shore");
            t.True(w.ShoreNormal(60 * nx, 60 * nz) == null, "out at sea: no shore");
            // a heading turned by CoastalRot: a moving harbour's bow (-sin r, -cos r) is the normal
            double r = Geo.Wrap(Geo.HeadingTo(nx, nz) - Geo.HeadingTo(0, -1));
            t.Near(nx, -Math.Sin(r), 1e-9, "bow x"); t.Near(nz, -Math.Cos(r), 1e-9, "bow z");
        }
    }

    public static void EveryHarbourOnTheShoreFacingTheSea(TestContext t)
    {
        int n = 0;
        foreach (var (data, tribe, name) in HARBOURS)
        {
            string dir = data == "base" ? Sys2.Base : Sys2.Mirage;
            if (!System.IO.Directory.Exists(dir)) continue;
            foreach (var a in new[] { 0.0, 2.3 })
            {
                var (w, p) = World(t, dir, tribe, a);
                if (w.Data.Def(name, p) is not { Coastal: true }) { t.Log($"{name}: not in {data}"); continue; }
                if (!w.Models.Has(w.Data.Stats(name, 1, p)?.Gfx)) { t.Log($"{name}: no model data"); continue; }
                double nx = Math.Cos(a), nz = Math.Sin(a);
                // a click inland and a click far out: both snap onto the shore line
                foreach (var (cx, cz) in new[] { (-12 * nx, -12 * nz), (20 * nx + 6 * nz, 20 * nz - 6 * nx) })
                {
                    var pl = w.PlacementOf(name, cx, cz, 0, p);
                    t.True(pl.Ok, $"{name} at {a:0.00}: snapped onto the shore from {cx:0},{cz:0}");
                    if (!pl.Ok) continue;
                    double s = pl.X * nx + pl.Z * nz;
                    t.True(s > -12 && s < 22, $"{name}: on the shore line (s = {s:0.0})");
                    // half land, half water: the footprint's cells on either side
                    var f = w.FootprintOf(name, pl.X, pl.Z, pl.Rot, false, p);
                    int wet = f.Cells.Count(k => w.WetCell![k] != 0);
                    t.True(wet >= 0.2 * f.Cells.Count && wet <= 0.8 * f.Cells.Count, $"{name}: part land, part water ({wet}/{f.Cells.Count} wet)");
                    // turned to the sea: the sea side (dock / bow) along the normal
                    var (ok, rot) = w.CoastalAt(name, pl.X, pl.Z, p);
                    t.True(ok && Math.Abs(Geo.Wrap(rot - pl.Rot)) < 1e-9, $"{name}: the snapped spot is a green dot with that rotation");
                    t.Near(Geo.Wrap(w.CoastalRot(name, p, nx, nz)), Geo.Wrap(pl.Rot), 0.2, $"{name}: turned perpendicular to the coast");
                    // the dots: green exactly where CoastalAt says so, and a placement on one stays there
                    var dots = w.PlaceGridOf(name, pl.X, pl.Z, 0, p, 8);
                    t.True(dots.Any(d => d.Ok) && dots.Any(d => !d.Ok), $"{name}: green and red dots on the shore");
                    foreach (var (dx, dz, dok) in dots)
                    {
                        t.Equal(w.CoastalAt(name, dx, dz, p).Ok, dok, $"{name}: dot {dx},{dz}");
                        if (dok) { var at = w.PlacementOf(name, dx, dz, 0, p); t.True(at.Ok && at.X == dx && at.Z == dz, $"{name}: a click on a green dot stays there"); }
                    }
                    // and the building goes up there, turned so
                    var (w2, p2) = World(t, dir, tribe, a);
                    var act = Sys2.Action(w2, p2, name, "Build");
                    if (act != null && w2.Data.Check(p2, act) is null or "hidden")
                    {
                        if (w2.Data.Check(p2, act) != null) p2.Debug = true;
                        var b = w2.StartConstruction(p2, act, pl.X, pl.Z, 0, new List<Unit>()) as Building;
                        t.True(b != null && Math.Abs(Geo.Wrap(b.Rot - pl.Rot)) < 1e-6, $"{name}: built there, turned to the sea ({b?.Rot:0.00} / {pl.Rot:0.00})");
                    }
                    n++;
                }
                // not inland, not in open water
                t.True(!w.CoastalAt(name, -40 * nx, -40 * nz, p).Ok && !w.CoastalAt(name, 70 * nx, 70 * nz, p).Ok, $"{name}: neither inland nor out at sea");
            }
        }
        if (n == 0) t.Skip("no harbour model data");
    }
}
