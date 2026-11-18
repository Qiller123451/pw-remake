// The original's first-shot delay (Kacper's decision 2026-11-13: on by default, a debug option to change it): a ranged
// character that raises its weapon adds 0.4 s to its first shot - Bela 0.6 s - on the weapon's duration (the time to
// the next shot) and on the projectile's delay (character.usl CCharacter.AttackEnemy m_fWeaponDurationOvertime,
// GetWeaponDuration, GetProjectileDelay; World.FirstShot, Combat.FirstShotUnit). The shots after it, from the raised
// weapon, have none; the weapon lowered (the fight over) and raised again: the delay again. The debug option "First-shot
// delay" (DebugOptions.FirstShot / FirstShotDelay) switches it off or sets one time; it travels with the GameStart.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class FirstShotDelayTests
{
    static (World W, Unit U, Unit E, Player P, Player Q) Arena(TestContext t, string data, string name, double dist, Action<World>? setup = null)
    {
        var (R, M) = Sys1.Load(t, data);
        if (!R.Index.ContainsKey(name)) t.Skip($"no {name}");
        var W = Sys1.NewWorld(R, M, 256, 5);
        setup?.Invoke(W);
        string tribe = R.Index[name].Tribe!;
        if (tribe is not ("Hu" or "Aje" or "Ninigi" or "SEAS")) tribe = "Hu";
        var p = Sys1.AddPlayer(W, R, 0, tribe); var q = Sys1.AddPlayer(W, R, 1, tribe == "Hu" ? "Aje" : "Hu");
        var u = W.SpawnUnit(name, p, 0, 0, 4)!;
        var e = Foe(W, q, dist);
        u.Heading = Math.PI;
        if (u.Anim == null || u.Anim.Clips.Count == 0) t.Skip($"no clips of {u.Gfx} (PW_MODELS)");
        return (W, u, e, p, q);
    }
    static Unit Foe(World W, Player q, double dist)
    {
        var e = W.SpawnUnit(q.Tribe == "Hu" ? "hu_warrior" : "aje_warrior", q, 0, dist, 1)!;
        e.Hp = e.MaxHp = 1e6; W.SetAggro(new[] { e }, -1);
        return e;
    }

    sealed record Shot(double At, double Overtime, double Fired);

    /// <summary>the unit's shots over `seconds`: when each began (LastHitDone), its overtime and when its projectile
    /// left (the pending shot fired)</summary>
    static List<Shot> Shots(World W, Unit u, double seconds)
    {
        var res = new List<(double At, double Ot, double Fired)>();
        double last = u.LastHitDone;
        PendingShot? pend = null; int idx = -1;
        for (int i = 0; i < (int)(seconds / World.STEP); i++)
        {
            W.Step();
            if (pend != null && !ReferenceEquals(u.Pending, pend) && idx >= 0) { res[idx] = res[idx] with { Fired = W.Time }; pend = null; }
            if (u.LastHitDone != last)
            {
                last = u.LastHitDone;
                res.Add((u.LastHitDone, u.ShotOvertime, double.NaN));
                idx = res.Count - 1; pend = u.Pending;
            }
        }
        return res.Select(x => new Shot(x.At, x.Ot, x.Fired)).ToList();
    }

    static string Show(List<Shot> s) => string.Join(", ", s.Take(6).Select(x => $"{x.At:0.00}(+{x.Overtime:0.0}, out {x.Fired - x.At:0.00})"));

    /// <summary>the first shot waits the delay longer than the shots after it (the time to the next shot and the
    /// projectile's start); the ones after it have none - hu_archer, seas_gunner 0.4 s, Bela 0.6 s</summary>
    public static void FirstShotLater(TestContext t)
    {
        foreach (var (name, want) in new[] { ("hu_archer", 0.4), ("seas_gunner", 0.4), ("Bela_s0", 0.6) })
        {
            var (W, u, _, _, _) = Arena(t, RulesTestData.Base(t), name, 14);
            var shots = Shots(W, u, 14);
            t.Log($"{name}: " + Show(shots));
            t.True(shots.Count >= 4, $"{name} shoots ({shots.Count})");
            t.Near(want, shots[0].Overtime, 1e-9, $"{name}: the first shot's extra time");
            t.True(shots.Skip(1).All(s => s.Overtime == 0), $"{name}: no extra time on the shots after it");
            double first = shots[1].At - shots[0].At, next = shots[2].At - shots[1].At;
            t.Near(want, first - next, World.STEP + 1e-9, $"{name}: the second shot comes the delay later ({first:0.00} s after the first, {next:0.00} s between the next)");
            t.Near(next, shots[3].At - shots[2].At, World.STEP + 1e-9, $"{name}: the shots after it keep their pace");
            // the projectile of the first shot leaves the delay later (the clips' shoot delays may differ: compare with the
            // same clip's later shots when the weapon has one clip)
            var w = u.Weapons.Long!;
            if (w.Anims.Select(a => a.ShootDelay).Distinct().Count() <= 1 && w.Projectile != null)
            {
                double out0 = shots[0].Fired - shots[0].At, out1 = shots[1].Fired - shots[1].At;
                t.Near(want, out0 - out1, World.STEP + 1e-9, $"{name}: the first projectile leaves the delay later ({out0:0.00} s / {out1:0.00} s)");
            }
        }
    }

    /// <summary>the weapon lowered (the fight over) and raised again for a new enemy: the delay again</summary>
    public static void AgainAfterLowering(TestContext t)
    {
        var (W, u, e, _, q) = Arena(t, RulesTestData.Base(t), "hu_archer", 14);
        var a = Shots(W, u, 6);
        t.True(a.Count >= 2 && a[0].Overtime == 0.4, "first fight: the first shot delayed");
        W.Kill(e, null);
        Shots(W, u, 5);
        t.True(!u.FightPos || u.Anim!.Phase != "l", "the weapon is down after the fight");
        Foe(W, q, 14);
        var b = Shots(W, u, 6);
        t.Log("second fight: " + Show(b));
        t.True(b.Count >= 2, "it fights the new enemy");
        t.Near(0.4, b[0].Overtime, 1e-9, "the weapon raised again: the delay again");
        t.Equal(0.0, b[1].Overtime, "then none");
    }

    /// <summary>the option: off - no delay; a time of its own for every unit (Bela too); the original's without debug
    /// options; vehicles and melee units never</summary>
    public static void OptionOffAndOverride(TestContext t)
    {
        var (W, u, _, _, _) = Arena(t, RulesTestData.Base(t), "hu_archer", 14, w => w.FirstShot = new FirstShotDelay { On = false });
        var s = Shots(W, u, 10);
        t.True(s.Count >= 3 && s.All(x => x.Overtime == 0), "off: no extra time");
        t.Near(s[1].At - s[0].At, s[2].At - s[1].At, World.STEP + 1e-9, "off: the second shot as the third");

        var (W2, b, _, _, _) = Arena(t, RulesTestData.Base(t), "Bela_s0", 14, w => w.FirstShot = new FirstShotDelay { Seconds = 0.25 });
        var s2 = Shots(W2, b, 10);
        t.Near(0.25, s2[0].Overtime, 1e-9, "override: Bela 0.25 s");
        t.Near(0.25, (s2[1].At - s2[0].At) - (s2[2].At - s2[1].At), World.STEP + 1e-9, "override: the second shot 0.25 s later");

        // the debug options -> the world (Cheats.Apply), for every player
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W3 = Sys1.NewWorld(R, M, 128, 1);
        var me = Sys1.AddPlayer(W3, R, 0, "Hu");
        Cheats.Apply(W3, null, me);
        t.True(W3.FirstShot.On && W3.FirstShot.Seconds == null, "no debug options: the original's");
        t.Equal(0.6, W3.FirstShot.Of("Bela_s0"), "Bela 0.6"); t.Equal(0.4, W3.FirstShot.Of("hu_archer"), "the others 0.4");
        Cheats.Apply(W3, new DebugOptions { On = true, FirstShot = false }.ForGame(), me);
        t.Equal(0.0, W3.FirstShot.Of("Bela_s0"), "debug option off: none");
        Cheats.Apply(W3, new DebugOptions { On = true, FirstShotDelay = 0.2 }.ForGame(), me);
        t.Equal(0.2, W3.FirstShot.Of("Bela_s0"), "debug option 0.2 s");
        Cheats.Apply(W3, new DebugOptions { On = false, FirstShot = false }.ForGame(), me);
        t.True(W3.FirstShot.On, "debug options not used: the original's");
        Cheats.Apply(W3, new DebugOptions { On = true, FirstShotDelay = 99 }.ForGame(), me);
        t.Equal(5.0, W3.FirstShot.Of("hu_archer"), "clamped to 5 s");

        // the classes: the characters of IsAnimLoopUnit, not the vehicles, not the melee units
        Unit Mk(string n) => W3.SpawnUnit(n, me, 0, 0)!;
        foreach (var n in new[] { "hu_archer", "Bela_s0", "seas_flamethrower", "tesla_s0" }) if (R.Index.ContainsKey(n)) t.True(World.FirstShotUnit(Mk(n)), $"{n}: delayed");
        foreach (var n in new[] { "hu_warrior", "seas_lumberjack_minigun", "babbage_mobile_suit", "hu_worker" }) if (R.Index.ContainsKey(n)) t.True(!World.FirstShotUnit(Mk(n)), $"{n}: never");
    }

    /// <summary>the option travels with the game's start (saves, replays, the lockstep: GameStart.Debug); older
    /// settings without it have the original's</summary>
    public static void OptionInGameStart(TestContext t)
    {
        var st = new GameStart { Debug = new DebugOptions { On = true, FirstShot = true, FirstShotDelay = 0.3 }.ForGame() };
        var back = JsonSerializer.Deserialize<GameStart>(JsonSerializer.Serialize(st))!;
        t.Equal(0.3, back.Debug!.FirstShotDelay, "the time in the start");
        t.True(back.Debug.FirstShot, "on in the start");
        var old = JsonSerializer.Deserialize<DebugOptions>("{\"on\":true,\"free\":true}")!;
        t.True(old.FirstShot && old.FirstShotDelay == null, "older settings: the original's delay");
        t.True(new DebugOptions { On = true, FirstShot = false }.Summary().Contains("no first-shot delay"), "the summary says it");
    }
}
