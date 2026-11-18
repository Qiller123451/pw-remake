// Two units whose ways run head-on through each other (player report, MIRAGE 13 co-op: two SEAS mechs of a computer
// player "kept going straight at each other forever"). Separation pushed them apart only along the line between them,
// the way finder knows no units: both walked into each other for good. Head-on swaps of units of several sizes, in the
// open and in a passage two of them fit through side by side, for a human's and a computer player's units, must get
// through in a few seconds.
using System;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class MoveDeadlockTests
{
    /// <summary>a and b ordered to each other's places (by their owners); the seconds until both are there (or -1)</summary>
    static double Swap(W_ W, Unit a, Unit b, double limit)
    {
        var (ax, az, bx, bz) = (a.Pos.X, a.Pos.Z, b.Pos.X, b.Pos.Z);
        W.Enqueue(new OrderCommand { Player = a.Owner!.Id, Units = new[] { a.Id }, Type = "move", X = bx, Z = bz });
        W.Enqueue(new OrderCommand { Player = b.Owner!.Id, Units = new[] { b.Id }, Type = "move", X = ax, Z = az });
        int n = (int)(limit / W_.STEP);
        for (int i = 0; i < n; i++)
        {
            W.Step();
            bool da = Math.Abs(a.Pos.X - bx) + Math.Abs(a.Pos.Z - bz) < 2 + a.Radius, db = Math.Abs(b.Pos.X - ax) + Math.Abs(b.Pos.Z - az) < 2 + b.Radius;
            if (da && db) return (i + 1) * W_.STEP;
        }
        return -1;
    }

    // (configuration, tribe, unit[, "ship": on water or "", the radius: the test data lack most models - 0.45 for those])
    static readonly string[][] Kinds =
    {
        new[] { "mirage", "SEAS", "seas_mobile_suit" }, new[] { "mirage", "SEAS", "seas_mgwalker" }, new[] { "mirage", "SEAS", "seas_mechanical_walker" },
        new[] { "base", "Hu", "hu_warrior" }, new[] { "base", "Hu", "hu_mammoth" }, new[] { "base", "Aje", "aje_brachiosaurus", "", "3.4" },
        new[] { "base", "Hu", "hu_dragon_boat", "ship" }, new[] { "base", "Hu", "hu_transport_ship", "ship", "3.2" },
    };

    enum Ground { Open, Passage, Corner }

    /// <summary>two units of kind k at (-20, 0) and (20, 0) facing each other (alone: only the first); Passage: blocked
    /// ground on both sides, wide enough for the two side by side; Corner: a block on one side reaching just short of the
    /// line between them; enemy: the second of another player of another team</summary>
    static (W_ W, Unit a, Unit? b, double width) Setup(TestContext t, string[] k, bool ai, Ground g, bool alone, bool enemy = false)
    {
        var (R, M) = Sys1.Load(t, k[0] == "mirage" ? t.Data("PW_DATA_MIRAGE") : t.Data());
        var W = new W_(R, M, k.Length > 3 && k[3] == "ship" ? new FlatMap(256) { Water = 0, HeightFn = (x, z) => -8 } : new FlatMap(256), new WorldOptions { Seed = 1 });
        var p = new Player(0, k[1], new PlayerOpts { Rules = R, Ai = ai, Team = 0 }); W.Players.Add(p);
        var q = p;
        if (enemy) { q = new Player(1, k[1], new PlayerOpts { Rules = R, Ai = ai, Team = 1 }); W.Players.Add(q); }
        var a = W.SpawnUnit(k[2], p, -20, 0) ?? throw new Exception(k[2] + ": not in the rules");
        double? r = k.Length > 4 ? double.Parse(k[4], System.Globalization.CultureInfo.InvariantCulture) : null;
        if (r != null) a.Radius = r.Value;
        var nav = W.NavFor(a);
        double halfW = Math.Max(2.5, 2.2 * a.Radius);
        if (g != Ground.Open)
        {
            var cells = nav.RectCells(0, halfW + 4, 12, 4, 0, true);
            if (g == Ground.Passage) cells.AddRange(nav.RectCells(0, -halfW - 4, 12, 4, 0, true));
            else cells = nav.RectCells(0, a.Radius + 0.5 + 6, 3, 6, 0, true);
            nav.Mark(cells, 1); nav.UpdateClearance();
            t.True(!nav.IsFree(0, (g == Ground.Passage ? halfW : a.Radius + 0.5) + 1), "the blocked ground is there");
        }
        var b = alone ? null : W.SpawnUnit(k[2], q, 20, 0)!;
        if (b != null && r != null) b.Radius = r.Value;
        a.Heading = Geo.HeadingTo(1, 0); if (b != null) b.Heading = Geo.HeadingTo(-1, 0);
        return (W, a, b, 2 * halfW);
    }

    public static void HeadOnSwapGetsThrough(TestContext t)
    {
        var bad = new System.Collections.Generic.List<string>();
        foreach (var k in Kinds.Where(k => Environment.GetEnvironmentVariable("PW_KIND") is not { } f || k[2] == f))
            foreach (var g in new[] { Ground.Open, Ground.Passage, Ground.Corner })
            {
                // the time one of them takes alone
                var (W0, a0, _, _) = Setup(t, k, false, g, true);
                W0.Enqueue(new OrderCommand { Player = a0.Owner!.Id, Units = new[] { a0.Id }, Type = "move", X = 20, Z = 0 });
                double free = 0; for (int i = 0; i < 2400 && free == 0; i++) { W0.Step(); if (a0.Task.Type == "idle" && i > 2) free = (i + 1) * W_.STEP; }
                foreach (var (ai, enemy) in new[] { (false, false), (true, false), (false, true) })
                {
                    var (W, a, b, width) = Setup(t, k, ai, g, false, enemy);
                    double s = Swap(W, a, b!, free + 40);
                    string what = $"{k[2],-22} r={a.Radius:0.00} {(ai ? "ai   " : enemy ? "enemy" : "human")} {(g == Ground.Passage ? $"passage {width:0.#} m" : g.ToString().ToLowerInvariant())}: alone {free:0.0} s, swap {(s < 0 ? "STUCK" : $"{s:0.0} s")}";
                    t.Log(what + $"  a {a.Pos} b {b!.Pos}");
                    if (!(s > 0 && s < free + 8)) bad.Add(what);
                }
            }
        t.True(bad.Count == 0, "every swap through within 8 s of the time alone: " + string.Join("; ", bad));
    }
    /// <summary>a group sent to one point from all sides (an attack wave's waypoint: base mission 13's third outpost team,
    /// eight SEAS units sent by a trigger's attack-move to 1121,785) comes to rest there: each one met those across the
    /// point "head-on", turned off, and they circled it for 45 minutes - the waypoint chain stalled and "Hold the Fort"
    /// was never accomplished. Units going to the same point are not turned off from (Unit.Avoid).</summary>
    public static void GroupToOnePointComesToRest(TestContext t)
    {
        var bad = new System.Collections.Generic.List<string>();
        foreach (var (cfg, tribe, kinds) in new[] { ("base", "Hu", new[] { "hu_warrior", "hu_warrior", "hu_mammoth", "hu_warrior", "hu_archer", "hu_warrior", "hu_mammoth", "hu_warrior" }),
                                                    ("mirage", "SEAS", new[] { "seas_mobile_suit", "seas_mobile_suit", "seas_wehrspinne", "seas_triceratops", "seas_triceratops", "seas_warrior", "seas_flamethrower", "seas_flamethrower" }) })
            foreach (var type in new[] { "move", "attackmove" }) foreach (var ring in new[] { 20.0, 6.0 })
            {
                var (R, M) = Sys1.Load(t, cfg == "mirage" ? t.Data("PW_DATA_MIRAGE") : t.Data());
                var W = new W_(R, M, new FlatMap(256), new WorldOptions { Seed = 1 });
                var p = new Player(0, tribe, new PlayerOpts { Rules = R, Ai = true, Team = 0 }); W.Players.Add(p);
                var us = new System.Collections.Generic.List<Unit>();
                for (int i = 0; i < kinds.Length; i++)
                {
                    double a = i * 2 * Math.PI / kinds.Length;
                    var u = W.SpawnUnit(kinds[i], p, ring * Math.Cos(a), ring * Math.Sin(a)) ?? throw new Exception(kinds[i] + ": not in the rules");
                    us.Add(u);
                }
                foreach (var u in us) W.Enqueue(new OrderCommand { Player = p.Id, Units = new[] { u.Id }, Type = type, X = 0, Z = 0 });
                for (int i = 0; i < (int)(40 / W_.STEP); i++) W.Step();
                var moving = us.Where(u => u.Path.Count > 0 || Math.Sqrt(u.Vel.X * u.Vel.X + u.Vel.Z * u.Vel.Z) > 0.2).ToList();
                double far = us.Max(u => Math.Sqrt(u.Pos.X * u.Pos.X + u.Pos.Z * u.Pos.Z));
                string what = $"{cfg} {type} from {ring} m: {moving.Count} of {us.Count} still moving after 40 s, farthest {far:0.#} m";
                t.Log(what);
                if (moving.Count > 0 || far > 12) bad.Add(what);
            }
        t.True(bad.Count == 0, "every group at rest round its point: " + string.Join("; ", bad));
    }
}
