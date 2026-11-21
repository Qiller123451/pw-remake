// Goals units cannot reach, and mission 9's Dragon Clan village (docs/SIMULATION.md "Goals out of reach",
// docs/CAMPAIGN.md mission 9).
//
//   Mission9VillageReachesTheStart   the village is reachable: a way from the village to the heroes' start (the player's
//                                    base) and back for every unit size. The islands of the altars and of the airplane
//                                    are not, by land: the map's own walk grid (PaFi) parts them by water and by a ridge
//                                    whose slopes are 38 degrees and more - the original's archipelago (transport boats;
//                                    the Barbarians land by ship), no bridge joins them.
//   FarGoalEndsAtNearestPoint        a goal on another island more than 160 m off: from anywhere on the village's island
//                                    the way ends at the one cell of that island nearest the goal (exactly: the nearest
//                                    by straight-line distance over all its cells), "not found" as before.
//   UnitsStopAtTheEdge               small and big units sent there walk to that point and stop (they wandered: the
//                                    failed search ended elsewhere from each place, and a big unit's ran out after
//                                    30,000 cells); also mission 6's army sent from its island to the pirates' prison.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class UnreachableGoalTests
{
    // mission 9 (map metres): the heroes' start, the village's fireplace, the launch ramp (north island), the west altar
    static readonly (double X, double Y) START = (702, 1128), VILLAGE = (580, 795), PAD = (748, 314), ALTAR_W = (177, 818);

    static int Cell(MissionSession s, (double X, double Y) m) { var (x, z) = s.Campaign.ToGame(m.X, m.Y); var nav = s.World.Nav; int k = nav.Idx(x, z); return nav.Block[k] != 0 ? nav.NearestFree(k, 10) : k; }

    public static void Mission9VillageReachesTheStart(TestContext t)
    {
        var s = CampaignTests.Session(t, "base", 9, "Hu");
        var C = s.Campaign; var nav = s.World.Nav;
        var (sx, sz) = C.ToGame(START.X, START.Y); var (vx, vz) = C.ToGame(VILLAGE.X, VILLAGE.Y);
        foreach (var r in new[] { 0.7, 1.2, 1.8, 2.5, 3.5 })
        {
            var p = nav.Find(vx, vz, sx, sz, r, 30000);
            t.True(nav.Found && p != null && p.Count > 0 && Math.Abs(p[^1].X - sx) < 3 && Math.Abs(p[^1].Z - sz) < 3, $"radius {r}: a way from the village to the start");
            p = nav.Find(sx, sz, vx, vz, r, 30000);
            t.True(nav.Found && p != null && p.Count > 0 && Math.Abs(p[^1].X - vx) < 3 && Math.Abs(p[^1].Z - vz) < 3, $"radius {r}: a way from the start to the village");
        }
        var R = nav.Region!;
        int rv = R[Cell(s, VILLAGE)];
        t.Equal(rv, R[Cell(s, START)], "the village and the start: one ground region");
        t.True(R[Cell(s, PAD)] != rv && R[Cell(s, ALTAR_W)] != rv && R[Cell(s, PAD)] != R[Cell(s, ALTAR_W)], "the launch ramp's island and the west altar's island: other regions (by boat)");
    }

    public static void FarGoalEndsAtNearestPoint(TestContext t)
    {
        var s = CampaignTests.Session(t, "base", 9, "Hu");
        var C = s.Campaign; var nav = s.World.Nav; var R = nav.Region!;
        int n = nav.N, reg = R[Cell(s, VILLAGE)];
        foreach (var goal in new[] { PAD, ALTAR_W })
        {
            var (gx, gz) = C.ToGame(goal.X, goal.Y);
            int g = nav.Idx(gx, gz), gi = g % n, gj = g / n;
            // the nearest free cell of the village's region, by brute force
            int best = -1; double bd = double.MaxValue;
            for (int k = 0; k < n * n; k++)
                if (R[k] == reg && nav.Block[k] == 0) { double d = (double)(k % n - gi) * (k % n - gi) + (double)(k / n - gj) * (k / n - gj); if (d < bd) { bd = d; best = k; } }
            t.True(Math.Sqrt(bd) * nav.Cell > 160, $"{goal}: more than 160 m from the village's island ({Math.Sqrt(bd) * nav.Cell:0} m)");
            var (bx, bz) = nav.Center(best);
            foreach (var from in new[] { (634.0, 722.0), (695.0, 749.0), (614.0, 758.0), (454.0, 826.0), VILLAGE, START })
            {
                var (x, z) = C.ToGame(from.Item1, from.Item2);
                var p = nav.Find(x, z, gx, gz, 0.7, 30000);
                t.True(!nav.Found && p != null && p.Count > 0 && Math.Abs(p[^1].X - bx) < 0.01 && Math.Abs(p[^1].Z - bz) < 0.01,
                    $"{goal} from {from}: not found, the way ends at the nearest point {C.ToMap(bx, bz)} (ends at {(p != null && p.Count > 0 ? C.ToMap(p[^1].X, p[^1].Z) : default)})");
            }
        }
    }

    /// <summary>n units (of class cls, or the player's own) put at from and sent to goal for 4 minutes: where they stand
    /// after 1 minute is where they stand at the end, near the nearest point of their region</summary>
    static void Stop(TestContext t, string cfg, int mission, (double X, double Y) from, (double X, double Y) goal, int count, string? cls)
    {
        var s = CampaignTests.Session(t, cfg, mission, "Hu");
        var C = s.Campaign; var W = s.World; var nav = W.Nav;
        var (fx, fz) = C.ToGame(from.X, from.Y);
        if (cls != null) for (int q = 0; q < count; q++) W.SpawnUnit(cls, s.Human, fx + q * 6, fz, null, 0);
        var us = W.Units.Where(u => u.Alive && u.Owner == s.Human && !u.Naval && u.Speed > 0 && (cls == null || u.Name == cls)).Take(count).ToList();
        int i = 0;
        foreach (var u in us) { C.Teleport(Registry.Get(u), from.X + (i % 3) * 4, from.Y + (i / 3) * 4); i++; }
        var (gx, gz) = C.ToGame(goal.X, goal.Y);
        W.Order(us, new Order("move") { X = gx, Z = gz });
        var edge = nav.Center(nav.NearestInRegion(nav.Idx(gx, gz), nav.Region![nav.Idx(us[0].Pos.X, us[0].Pos.Z)], nav.N));
        s.Run(90);
        var at = us.ToDictionary(u => u, u => (u.Pos.X, u.Pos.Z));
        double walked = 0;
        for (int k = 0; k < 150; k++)
        {
            s.Run(1);
            foreach (var u in us) { walked += Math.Sqrt((u.Pos.X - at[u].X) * (u.Pos.X - at[u].X) + (u.Pos.Z - at[u].Z) * (u.Pos.Z - at[u].Z)); at[u] = (u.Pos.X, u.Pos.Z); }
        }
        double far = us.Max(u => Math.Sqrt((u.Pos.X - edge.X) * (u.Pos.X - edge.X) + (u.Pos.Z - edge.Z) * (u.Pos.Z - edge.Z)));
        string where = string.Join(" ", us.Select(u => { var m = C.ToMap(u.Pos.X, u.Pos.Z); return $"{m.MX:0},{m.MY:0}"; }));
        t.True(walked < 2 * us.Count, $"{cfg} {mission} {cls ?? "own units"} x{us.Count} {from} -> {goal}: they stand (walked {walked:0.#} m in the last 150 s; at {where})");
        t.True(far < 12 + 2 * us.Count, $"{cfg} {mission}: at the nearest point {C.ToMap(edge.X, edge.Z)} (the farthest {far:0.#} m off)");
    }

    public static void UnitsStopAtTheEdge(TestContext t)
    {
        Stop(t, "base", 9, VILLAGE, PAD, 6, null);
        Stop(t, "base", 9, VILLAGE, PAD, 3, "hu_triceratops");
        Stop(t, "base", 9, VILLAGE, ALTAR_W, 3, "hu_triceratops");
        // mission 6: the player's island to the pirates' prison across the sea
        Stop(t, "base", 6, (1236, 467), (330, 553), 6, null);
    }
}
