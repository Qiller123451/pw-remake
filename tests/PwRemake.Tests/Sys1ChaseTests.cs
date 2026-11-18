// Chasing fleeing animals (player report: "the CBR titan was unable to kill the elk, it was just stuttering behind it
// without dealing damage, the attack sound did play"). Every unit class of both configurations that can fight on foot
// is ordered to attack an elk (Megaloceros) that flees from it: every melee blow it starts lands (the blow is decided
// when the swing starts, docs/SIMULATION.md "Melee blows"), and a unit at least as fast as the elk kills it. The titans'
// turrets (the CBR titan's machine guns, the ballistas, the gatlings) shoot at what their carrier fights.
// Game data: PW_DATA / PW_DATA_MIRAGE (default /opt/data/base, /opt/data/mirage); model files: PW_MODELS.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys1ChaseTests
{
    public static void EveryClassHitsAFleeingElkBase(TestContext t) => EveryClassHitsAFleeingElk(t, RulesTestData.Base(t));
    public static void EveryClassHitsAFleeingElkMirage(TestContext t) => EveryClassHitsAFleeingElk(t, RulesTestData.Mirage(t));

    static IEnumerable<string> Fighters(Rules R) => R.IndexOrder.Where(n =>
    {
        var i = R.Index[n];
        return (i.Type == "CHTR" || i.Type == "ANML" || i.Type == "VHCL") && i.Tribe is "Hu" or "Aje" or "Ninigi" or "SEAS";
    });

    /// <summary>the class's unit at level 4 against an elk 4 m in front of it that starts to flee, ordered to attack it, for 60 s</summary>
    static void EveryClassHitsAFleeingElk(TestContext t, string data)
    {
        var (R, M) = Sys1.Load(t, data);
        if (!R.Exists("Megaloceros")) t.Skip("no Megaloceros");
        int n = 0, fast = 0; var bad = new List<string>();
        foreach (var name in Fighters(R))
        {
            var W = Sys1.NewWorld(R, M, 512, 3);
            var p = Sys1.AddPlayer(W, R, 0, R.Index[name].Tribe);
            var u = W.SpawnUnit(name, p, -10, 0, 4);
            if (u == null || u.Weapons.Long == null || u.Disarmed || u.CannotFight || !u.CanWalk || u.Naval || u.Flying) continue;
            var cs = u.Cs(u.Weapons.Long);
            // (not: healers - they go along and heal; a weapon with a minimum range - the elk stands inside it; a weapon
            // that strikes once a minute)
            if (cs == null || cs.Dmg <= 0 || cs.MinRange > 0 || W.AttackDuration(u, cs) > 10 || u.Stats!.Heal != null) continue;
            var e = W.SpawnUnit("Megaloceros", null, -10 + u.Radius + 4, 0)!;
            W.Flee(e, u);
            W.Order(new[] { u }, new Order("attack") { Target = e });
            if (u.Task.Type != "attack") continue;
            n++;
            int swings = 0, landed = 0; double hp = e.Hp;
            for (int s = 0; s < 20 * 60 && e.Alive; s++)
            {
                bool had = u.Pending != null;
                W.Step();
                if (!had && u.Pending is { } ps && ps.Target == e && ps.W?.Projectile == null) swings++;
                if (e.Hp < hp || !e.Alive) { landed++; hp = e.Hp; }
            }
            // every blow started lands (one may still be under way at the end)
            if (landed < swings - 1) bad.Add($"{name}: {swings} blows, {landed} landed");
            if (u.RunSpeed >= e.RunSpeed - 1e-9)
            {
                fast++;
                if (e.Alive) bad.Add($"{name} (runs {u.RunSpeed:0.0}, the elk {e.RunSpeed:0.0}): the elk still lives after 60 s ({e.Hp:0} hp, {swings} blows, {landed} landed)");
            }
        }
        t.Log($"{n} classes, {fast} as fast as the elk");
        foreach (var b in bad.Take(20)) t.Log("  " + b);
        t.True(n > 50, $"classes tried ({n})");
        t.True(bad.Count == 0, $"every class hits the fleeing elk ({bad.Count} failed)");
    }

    /// <summary>a penetration (cone) weapon against a big victim standing at the far edge of its attack zone: the zone is
    /// measured to the victim's full radius, the cone to its inner one - the blow must land all the same (the sumo stood
    /// at 6.1 m from an elk swinging for ever)</summary>
    public static void ConeBlowAtTheZoneEdge(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 5);
        var p = Sys1.AddPlayer(W, R, 0, "Ninigi"); var q = Sys1.AddPlayer(W, R, 1, "Hu");
        var u = W.SpawnUnit("ninigi_sumo", p, 0, 0, 4)!;
        t.True(u.Weapons.Long!.Penetration, "the sumo's staff is a cone weapon");
        var v = W.SpawnUnit("Megaloceros", null, 6, 0)!;
        v.Hp = v.MaxHp = 5000;
        // to the edge of the zone: the farthest place still in zone 3
        double x = 6;
        while (W.RangeZone(u, v) > 0) { x += 0.05; v.Pos = new V3(x, 0, 0); W.UHash.Move(v); }
        x -= 0.1; v.Pos = new V3(x, 0, 0); W.UHash.Move(v);
        t.True(W.RangeZone(u, v) == 3, "the elk is in the attack zone");
        W.Order(new[] { u }, new Order("attack") { Target = v });
        double hp = v.Hp;
        for (int i = 0; i < 20 * 8; i++) { W.Step(); v.Pos = new V3(x, 0, 0); v.Vel = V3.Zero; W.UHash.Move(v); }
        t.True(v.Hp < hp, $"the cone blows land on the victim at the zone's edge ({hp} -> {v.Hp})");
    }

    /// <summary>a melee blow started at a victim in range lands though the victim runs out of range during the swing
    /// (FightingObj AttackEnemy: TakeDmg at the swing; combat.js checked the range again at the hit)</summary>
    public static void BlowLandsOnAVictimThatRanOff(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 5);
        var p = Sys1.AddPlayer(W, R, 0, "Hu");
        var u = W.SpawnUnit("hu_warrior", p, 0, 0, 3)!;
        var v = W.SpawnUnit("Megaloceros", null, u.Radius + 2.5, 0)!;
        v.Hp = v.MaxHp = 5000;
        W.Order(new[] { u }, new Order("attack") { Target = v });
        for (int i = 0; i < 20 && u.Pending == null; i++) W.Step();
        t.True(u.Pending is { } pb && pb.W?.Projectile == null, "a blow is under way");
        double x0 = u.Pos.X, z0 = u.Pos.Z;
        // the victim is carried away (as by its flight) before the blow lands
        v.Pos = new V3(30, 0, 0); W.UHash.Move(v);
        double hp = v.Hp; double moved = 0;
        while (u.Pending != null) { W.Step(); if (u.Pending != null) moved = Math.Max(moved, Math.Abs(u.Pos.X - x0) + Math.Abs(u.Pos.Z - z0)); }
        W.Step();
        t.True(v.Hp < hp, $"the blow landed ({hp} -> {v.Hp})");
        t.True(moved < 0.05, $"the attacker stood still until its blow landed (moved {moved:0.00} m)");
    }

    public static void TitanTurretsShootBase(TestContext t) => TitanTurretsShoot(t, RulesTestData.Base(t));
    public static void TitanTurretsShootMirage(TestContext t) => TitanTurretsShoot(t, RulesTestData.Mirage(t));

    /// <summary>the units with turrets that shoot by themselves (a weapon class on a composite turret: the titans'
    /// ballistas and gatlings, MIRAGE's CBR titan's machine guns) fire them at the enemy their carrier fights</summary>
    static void TitanTurretsShoot(TestContext t, string data)
    {
        var (R, M) = Sys1.Load(t, data);
        int tried = 0;
        foreach (var name in Fighters(R))
        {
            if (!R.Composites(name).Any(s => s.Kind == "turret" && s.WeaponClass != null)) continue;
            var W = Sys1.NewWorld(R, M, 256, 5);
            var p = Sys1.AddPlayer(W, R, 0, R.Index[name].Tribe); var q = Sys1.AddPlayer(W, R, 1, "Hu");
            var u = W.SpawnUnit(name, p, 0, 0, 5);
            if (u == null || u.Comp == null || u.Comp.Gunners.Count == 0) { t.Log($"{name}: no turrets built (model data?)"); continue; }
            tried++;
            var e = W.SpawnUnit("hu_warrior", q, 12, 0, 1)!;
            e.Hp = e.MaxHp = 100000;
            W.SetAggro(new[] { e }, -1);
            W.Order(new[] { u }, new Order("attack") { Target = e });
            var turretWeapons = new HashSet<string>();
            int ev = 0;
            for (int i = 0; i < 20 * 10; i++)
            {
                W.Step();
                foreach (var x in W.EventsSince(ev)) if (x.Type == "shoot" && x.E("attacker") == u && x.Data["weapon"] is Weapon w && w.Id != u.Weapons.Long?.Id) turretWeapons.Add(w.Id);
                ev = W.Events.Count;
            }
            t.Log($"{name}: {u.Comp.Gunners.Count} turrets, their shots: {string.Join(", ", turretWeapons)}");
            t.True(turretWeapons.Count > 0, $"{name}: its turrets shoot");
        }
        if (tried == 0) t.Skip("no unit with turrets has its model data here");
    }
}
