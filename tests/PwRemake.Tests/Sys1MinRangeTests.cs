// The minimum-range rule of the automatic target choice (FightingObj.usl SortEnemyList FO:4890-4924, docs/SIMULATION.md
// "Melee blows and the titans' turrets"): a unit whose primary weapon has a minimum range and that has no secondary
// short or medium weapon leaves out the enemies inside its minimum range (and those out of its reach) when it picks a
// target by itself - idle, on an attack move, after a kill, when hit; a unit with such a secondary weapon takes them,
// behind the enemies in its primary zone.
// Game data: PW_DATA / PW_DATA_MIRAGE (default /opt/data/base, /opt/data/mirage).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys1MinRangeTests
{
    static IEnumerable<string> Fighters(Rules R) => R.IndexOrder.Where(n =>
    {
        var i = R.Index[n];
        return (i.Type == "CHTR" || i.Type == "ANML" || i.Type == "VHCL") && i.Tribe is "Hu" or "Aje" or "Ninigi" or "SEAS";
    });

    /// <summary>the classes (at level 4) whose primary weapon has a minimum range, with or without a secondary weapon,
    /// that walk on land and can fight</summary>
    static List<(string Name, double Min, bool Secondary)> MinRangeClasses(TestContext t, Rules R, ModelManifest M)
    {
        var res = new List<(string, double, bool)>();
        var W = Sys1.NewWorld(R, M, 256, 3);
        foreach (var name in Fighters(R))
        {
            var p = Sys1.AddPlayer(W, R, res.Count % 8, R.Index[name].Tribe);
            var u = W.SpawnUnit(name, p, 0, 0, 4);
            if (u == null) continue;
            u.Alive = false; W.UHash.Remove(u);
            if (u.Weapons.Long == null || u.Disarmed || u.CannotFight || !u.CanWalk || u.Naval || u.Flying || u.Stationary) continue;
            var cs = u.Cs(u.Weapons.Long);
            if (cs == null || cs.MinRange <= 0 || cs.Dmg <= 0) continue;
            res.Add((name, cs.MinRange, W_.HasSecondary(u)));
        }
        t.Log($"{res.Count} classes with a minimum range: " + string.Join(", ", res.Select(c => $"{c.Item1} ({c.Item2:0}{(c.Item3 ? ", secondary" : "")})")));
        return res;
    }

    public static void MinRangeTargetChoiceBase(TestContext t) => MinRangeTargetChoice(t, RulesTestData.Base(t));
    public static void MinRangeTargetChoiceMirage(TestContext t) => MinRangeTargetChoice(t, RulesTestData.Mirage(t));

    /// <summary>every such class, idle with an enemy at its feet (inside the minimum range) and one in its primary zone:
    /// without a secondary weapon it takes the far one and leaves the near one alone (also alone, and when the near one
    /// hits it); with a secondary weapon it takes the near one when it is alone</summary>
    static void MinRangeTargetChoice(TestContext t, string data)
    {
        var (R, M) = Sys1.Load(t, data);
        var classes = MinRangeClasses(t, R, M);
        t.True(classes.Count > 0, "classes with a minimum range");
        int without = 0, with = 0; var bad = new List<string>();
        foreach (var (name, min, sec) in classes)
        {
            // the near enemy at half the minimum range, the far one in the middle of the primary zone
            (W_ W, Unit u, Unit near, Unit? far) Scene(bool withFar)
            {
                var W = Sys1.NewWorld(R, M, 512, 5);
                var p = Sys1.AddPlayer(W, R, 0, R.Index[name].Tribe); var q = Sys1.AddPlayer(W, R, 1, R.Index[name].Tribe == "Hu" ? "Aje" : "Hu");
                var u = W.SpawnUnit(name, p, 0, 0, 4)!;
                u.Stance = 2;
                string vn = q.Tribe == "Hu" ? "hu_warrior" : "aje_warrior";
                var near = W.SpawnUnit(vn, q, u.Radius + Math.Max(1, min * 0.4), 0, 1)!;
                Unit? far = null;
                if (withFar)
                {
                    var cs = u.Cs(u.Weapons.Long)!;
                    double lo = min + u.Radius + 2, hi = W.AttackRangeOf(u, cs) + u.Radius + 2;
                    far = W.SpawnUnit(vn, q, 0, (lo + hi) / 2 + 0.5, 1)!;
                    far.Hp = far.MaxHp = 1e6;
                }
                foreach (var e in new[] { near, far }) if (e != null) { e.Hp = e.MaxHp = 1e6; W.SetAggro(new[] { e }, -1); }
                return (W, u, near, far);
            }
            if (!sec)
            {
                without++;
                // both: the far one, never the near one
                var (W, u, near, far) = Scene(true);
                t.True(!W.InSortZone(u, near, W.Reach(u, near)) && W.InSortZone(u, far!, W.Reach(u, far!)), $"{name}: the scene (near inside, far in the zone)");
                t.True(W.FindTarget(u, Math.Max(W.AlarmRange(u), 60)) == far, $"{name}: FindTarget picks the far enemy");
                Entity? first = null; bool tookNear = false;
                for (int i = 0; i < 20 * 6; i++)
                {
                    W.Step();
                    if (u.Task.Type == "attack") { first ??= u.Task.Target; if (u.Task.Target == near) tookNear = true; }
                }
                if (first != far) bad.Add($"{name}: picked {first?.Name ?? "nothing"} by itself, not the enemy in its zone");
                if (tookNear) bad.Add($"{name}: turned to the enemy inside its minimum range");
                // the near one alone: no fight, also not when it hits the unit
                (W, u, near, _) = Scene(false);
                t.True(W.FindTarget(u, Math.Max(W.AlarmRange(u), 60)) == null, $"{name}: FindTarget leaves the near enemy alone");
                for (int i = 0; i < 20 * 3; i++) W.Step();
                W.OnHurt(u, near);
                for (int i = 0; i < 20 * 3; i++) W.Step();
                if (u.Task.Type == "attack") bad.Add($"{name}: fights the enemy inside its minimum range ({u.Task.Target?.Name}, user {u.Task.User})");
                // an explicit order is still obeyed (and ends in the minimum range: docs/SIMULATION.md "Minimum range")
            }
            else
            {
                with++;
                // the near one alone: taken
                var (W, u, near, _) = Scene(false);
                t.True(W.FindTarget(u, Math.Max(W.AlarmRange(u), 60)) == near, $"{name} (secondary weapon): FindTarget picks the near enemy");
                bool tookNear = false;
                for (int i = 0; i < 20 * 4; i++) { W.Step(); if (u.Task.Type == "attack" && u.Task.Target == near) tookNear = true; }
                if (!tookNear) bad.Add($"{name} (secondary weapon): did not take the enemy inside its minimum range");
                // both: the one in the primary zone first (SortEnemyList puts the minimum-range targets behind)
                var (W2, u2, near2, far2) = Scene(true);
                t.True(W2.FindTarget(u2, Math.Max(W2.AlarmRange(u2), 60)) == far2, $"{name} (secondary weapon): FindTarget picks the enemy in its primary zone first");
            }
        }
        t.Log($"{without} classes without a secondary weapon, {with} with one");
        foreach (var b in bad.Take(20)) t.Log("  " + b);
        t.True(bad.Count == 0, $"the minimum-range rule ({bad.Count} failed)");
    }

    /// <summary>a unit of class `name` (level 4) at (0, 0) and enemy warriors that never fight (1e6 hit points) at the
    /// given (x, z)</summary>
    static (W_ W, Unit U, List<Unit> Enemies) Plain(Rules R, ModelManifest M, string name, params (double X, double Z)[] at)
    {
        var W = Sys1.NewWorld(R, M, 256, 5);
        string tribe = R.Index[name].Tribe!;
        var p = Sys1.AddPlayer(W, R, 0, tribe); var q = Sys1.AddPlayer(W, R, 1, tribe == "Hu" ? "Aje" : "Hu");
        var u = W.SpawnUnit(name, p, 0, 0, 4)!;
        string vn = q.Tribe == "Hu" ? "hu_warrior" : "aje_warrior";
        var es = at.Select(a => W.SpawnUnit(vn, q, a.X, a.Z, 1)!).ToList();
        foreach (var e in es) { e.Hp = e.MaxHp = 1e6; W.SetAggro(new[] { e }, -1); }
        return (W, u, es);
    }

    /// <summary>the plain case: the first class of the base data with a minimum range and no secondary weapon (the
    /// Norsemen's log cannon), an enemy inside its minimum range and one outside it: it attacks the one outside</summary>
    public static void MinRangeUnitIgnoresCloseEnemy(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var c = MinRangeClasses(t, R, M).FirstOrDefault(x => !x.Secondary);
        t.True(c.Name != null, "a class with a minimum range and no secondary weapon");
        var (W, u, es) = Plain(R, M, c.Name!, (2.5, 0), (0, c.Min + 6));
        var (near, far) = (es[0], es[1]);
        near.Pos = new V3(u.Radius + 2, 0, 0); W.UHash.Move(near);
        t.True(W.FindTarget(u, W.AlarmRange(u)) == far, $"{c.Name}: the enemy outside the minimum range ({W.Reach(u, far):0.0} m, min {c.Min})");
        for (int i = 0; i < 20 * 3; i++) W.Step();
        t.True(u.Task.Type == "attack" && u.Task.Target == far, $"{c.Name}: attacks the enemy outside its minimum range ({u.Task.Type} {u.Task.Target?.Name})");
        t.Log($"{c.Name} (min {c.Min}): near {W.Reach(u, near):0.0} m left alone, far {W.Reach(u, far):0.0} m attacked");
    }

    /// <summary>a unit with a minimum range and a secondary short or medium weapon (MIRAGE: the Ninigi's Kong; the base
    /// game has none) takes an enemy inside its minimum range when it is the only one around</summary>
    public static void MinRangeUnitWithSecondaryTakesCloseEnemy(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Mirage(t));
        var cl = MinRangeClasses(t, R, M);
        var c = cl.FirstOrDefault(x => x.Secondary && !x.Name.Contains("worker"));
        if (c.Name == null) c = cl.FirstOrDefault(x => x.Secondary);
        t.True(c.Name != null, "a class with a minimum range and a secondary weapon");
        var (W, u, es) = Plain(R, M, c.Name!, (0, 0));
        var near = es[0];
        near.Pos = new V3(u.Radius + 2, 0, 0); W.UHash.Move(near);
        t.True(!W.InSortZone(u, near, W.Reach(u, near)), $"{c.Name}: the enemy stands inside the minimum range ({W.Reach(u, near):0.0} m, min {c.Min})");
        for (int i = 0; i < 20 * 3; i++) W.Step();
        t.True(u.Task.Type == "attack" && u.Task.Target == near, $"{c.Name} (secondary weapon): attacks the close enemy ({u.Task.Type} {u.Task.Target?.Name})");
        t.Log($"{c.Name} (min {c.Min}): attacks the enemy {W.Reach(u, near):0.0} m away");
    }
}
