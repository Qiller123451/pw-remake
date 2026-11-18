// Units that set up or take a fight position to shoot (user report 2026-11-12: "When the Black Widow is in the shooting
// mode and is 'curled up', it should continue to be curled up until it has to move. Currently it uncurls after every
// attack.").
//
// The original (Vehicle.usl CWehrspinne, Fight.usl): in range the build-up switches to attack mode - the body plays
// attack_front's start part (curls up) -, each shot keeps its loop part (SetAttackAnim: SetSLEAnim(attack_front, 0)),
// and only to follow a target out of range or when the fight is broken off it switches to walk mode - the end part
// (uncurls). The ranged characters of CCharacter.IsAnimLoopUnit (and CLumberjack's minigun) do the same with their
// shooting clip: the start part on the first shot, the loop part on every shot, the end part when the fight ends.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class FightPositionTests
{
    static (World W, Unit U, Unit E) Arena(TestContext t, string data, string name, double dist)
    {
        var (R, M) = Sys1.Load(t, data);
        if (!R.Index.ContainsKey(name)) t.Skip($"no {name}");
        var W = Sys1.NewWorld(R, M, 256, 5);
        string tribe = R.Index[name].Tribe!;
        var p = Sys1.AddPlayer(W, R, 0, tribe); var q = Sys1.AddPlayer(W, R, 1, tribe == "Hu" ? "Aje" : "Hu");
        var u = W.SpawnUnit(name, p, 0, 0, 4)!;
        var e = W.SpawnUnit(q.Tribe == "Hu" ? "hu_warrior" : "aje_warrior", q, 0, dist, 1)!;
        e.Hp = e.MaxHp = 1e6; W.SetAggro(new[] { e }, -1);
        u.Heading = Math.PI;      // (it turns first)
        if (u.Anim == null || u.Anim.Clips.Count == 0) t.Skip($"no clips of {u.Gfx} (PW_MODELS)");
        return (W, u, e);
    }

    // the body's clip parts over the steps: (name, phase) each time they change
    static List<string> Track(World W, Unit u, double seconds, Func<bool>? stop = null)
    {
        var seen = new List<string>();
        for (int i = 0; i < (int)(seconds / World.STEP); i++)
        {
            W.Step();
            var k = $"{u.Anim!.CurName}#{u.Anim.Phase}";
            if (seen.Count == 0 || seen[^1] != k) seen.Add(k);
            if (stop?.Invoke() == true) break;
        }
        return seen;
    }

    /// <summary>The Black Widow curls up once, stays curled through its shots and after the fight, and uncurls to move;
    /// the log cannon mammoth lies down the same way ("rest": it lay down, rested and stood up again while set up); MIRAGE's
    /// machine-gun walker stands up ("standup").</summary>
    public static void SetUpUnitsStaySetUp(TestContext t)
    {
        SetsUpOnce(t, RulesTestData.Base(t), "seas_wehrspinne", "attack_front");
        SetsUpOnce(t, RulesTestData.Base(t), "hu_mammoth_log_cannon", "rest");
        SetsUpOnce(t, RulesTestData.Mirage(t), "seas_mgwalker", "standup");
    }

    static void SetsUpOnce(TestContext t, string data, string name, string clip)
    {
        World W; Unit u, e;
        try { (W, u, e) = Arena(t, data, name, 40); } catch (SkipException x) { t.Log($"{name}: {x.Message}"); return; }
        if (!u.Anim!.Has(clip + "#l")) { t.Log($"{name}: {clip} has no loop part in this data"); return; }
        t.True(u.Anim.Has(clip + "#s") && u.Anim.Has(clip + "#e"), $"{name}: {clip} is a start / loop / end clip");
        int shots = 0; double last = u.LastHitDone;
        var seen = new List<string>();
        for (int i = 0; i < (int)(20 / World.STEP); i++)
        {
            W.Step();
            if (u.LastHitDone != last) { shots++; last = u.LastHitDone; }
            var k = $"{u.Anim.CurName}#{u.Anim.Phase}";
            if (seen.Count == 0 || seen[^1] != k) seen.Add(k);
        }
        t.Log($"{name} fight: " + string.Join(" -> ", seen) + $" ({shots} shots)");
        t.True(shots >= 3, $"{name} shoots ({shots})");
        t.Equal("attack", u.Deploy?.State, $"{name} set up");
        t.Equal(1, seen.Count(s => s == clip + "#s"), $"{name} sets up once");
        t.True(!seen.Any(s => s.StartsWith(clip + "#e")) && !seen.Contains(clip + "#"), $"{name} never gets up (nor plays the whole clip) between shots");
        // the fight ends: it stays set up
        W.Kill(e, null);
        var after = Track(W, u, 6);
        t.Log($"{name} after the fight: " + string.Join(" -> ", after));
        t.Equal("attack", u.Deploy?.State, $"{name} still set up after the fight");
        t.True(!after.Any(s => s.StartsWith(clip + "#e")), $"{name} does not get up without having to move");
        // a move: it gets up, then walks
        W.Order(u, new Order("move") { X = 30, Z = -30 });
        var move = Track(W, u, 8);
        t.Log($"{name} ordered to move: " + string.Join(" -> ", move));
        int ie = move.FindIndex(s => s.StartsWith(clip + "#e"));
        t.True(ie >= 0, $"{name} gets up to move");
        t.True(move.Skip(ie + 1).Any(s => s.StartsWith("walk")), $"{name} then walks");
        t.True(Math.Abs(u.Pos.X) > 1 || Math.Abs(u.Pos.Z) > 1, $"{name} moves");
    }

    /// <summary>An archer raises the bow once, shoots from its fight position (the loop part) and lowers it when the
    /// fight is over.</summary>
    public static void ArcherHoldsFightPosition(TestContext t)
    {
        foreach (var name in new[] { "hu_archer", "seas_gunner" })
        {
            var (W, u, e) = Arena(t, RulesTestData.Base(t), name, 14);
            var w = u.Weapons.Long!;
            var clip = w.Anims.Select(a => a.Name).FirstOrDefault(c => u.Anim!.Has(c + "#l"));
            if (clip == null) { t.Log($"{name}: no shooting clip with a loop part"); continue; }
            int shots = 0; double last = u.LastHitDone;
            var seen = new List<string>();
            for (int i = 0; i < (int)(15 / World.STEP); i++)
            {
                W.Step();
                if (u.LastHitDone != last) { shots++; last = u.LastHitDone; }
                var k = $"{u.Anim!.CurName}#{u.Anim.Phase}";
                if (seen.Count == 0 || seen[^1] != k) seen.Add(k);
            }
            t.Log($"{name} fight: " + string.Join(" -> ", seen.Take(12)) + $" ... ({shots} shots)");
            t.True(shots >= 3, $"{name} shoots ({shots})");
            t.True(World.FightPosition(u, w, u.Anim!.CurName!), $"{name}: {u.Anim.CurName} is a fight position");
            t.Equal(1, seen.Count(s => s.EndsWith("#s")), $"{name}: the weapon comes up once");
            t.True(!seen.Any(s => s.EndsWith("#e")), $"{name}: it is not lowered between shots");
            t.True(!seen.SkipWhile(s => !s.EndsWith("#s")).Any(s => s.Contains("fight_standanim") || s.Contains("fightpos_standanim")), $"{name}: no fight stand between shots");
            W.Kill(e, null);
            var after = Track(W, u, 4);
            t.Log($"{name} after the fight: " + string.Join(" -> ", after));
            t.True(after.Any(s => s.EndsWith("#e")), $"{name}: the weapon goes down when the fight is over");
        }
    }
}
