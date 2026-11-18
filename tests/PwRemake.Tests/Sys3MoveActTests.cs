// Special moves play out (src/Sim/Systems/Moves.cs "Playing out", docs/SIMULATION.md "Special moves play out"): the
// user's report "heroes abilities fire in an instant when they should play out their animation". For each kind of
// timing of the original's tasks - the effect when the clip is over (Cole's shotgun: Shotgun.usl "reaction"), a
// projectile leaving at its start timer (Bela's arrow: CSniperArrow 2.0 s), the effect as the clip starts with the hero
// held to its end (the mayor's jump: MayorSpecialMove.usl), an effect at a time mark of the clip (the sacrifice: 0.9 s),
// an automatic move inside a fight (the archer's multishot: back to the fight afterwards) - the effect does not land
// before its moment, lands at it, the hero stays in the act until the clip ends (orders wait: SetUnStoppable), and a stun
// or death before the moment loses the effect.
using System;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys3MoveActTests
{
    const double EPS = 1e-6;

    static Sys3Arena Arena(TestContext t, string data, string tribe, string name)
    {
        var (R, M) = Sys3Data.Load(t, data);
        Sys3Arena A;
        try { A = Sys3Arena.Make(R, M, tribe, name, null, true); } catch (SkipException e) { t.Skip(e.Message); throw; }
        // nobody fights on its own: what changes is the move's doing
        foreach (var e in A.W.Units) if (e.Owner != null) e.Stance = 3;
        return A;
    }

    static ActionDef Move(Sys3Arena A, string id)
    {
        var a = A.W.MovesOf(A.Caster).FirstOrDefault(x => x.Id == id);
        if (a == null) throw new SkipException($"{A.Caster.Name} has no {id}");
        return a;
    }

    /// <summary>uses the move and steps until it runs (its act begins) -> the act</summary>
    static MoveAct Begin(TestContext t, Sys3Arena A, string id, Entity? tg, (double, double)? pos = null)
    {
        var u = A.Caster;
        var why = A.W.UseMove(u, Move(A, id), tg, pos);
        t.True(why == null, $"{id} used ({why})");
        for (int i = 0; i < 400 && u.Task.Type == "special" && u.Task.Act == null; i++) A.W.Step();
        t.True(W_.InAct(u), $"{id}: the hero is in the move's act ({u.Task})");
        return u.Task.Act!;
    }

    static void StepTo(Sys3Arena A, double time) { while (A.W.Time + EPS < time) A.W.Step(); }

    // ---------------------------------------------------------------- the effect when the clip is over (Cole's shotgun)
    public static void ShotgunAtClipEnd(TestContext t)
    {
        var A = Arena(t, Sys2.Base, "Hu", "Cole_s0");
        var u = A.Caster; var e = A.EnemyChar!; var e2 = A.EnemyChar2!;
        double hp = e.Hp, hp2 = e2.Hp;
        var act = Begin(t, A, "Shotgun", e);
        double len = u.Anim!.Duration("shotgun");
        t.True(len > 2, $"Cole has the clip shotgun ({len} s)");
        t.Near(act.Start + len, act.End, EPS, "the act lasts the clip");
        t.Equal("shotgun", u.Anim.CurName, "the clip plays");
        t.Near(act.Start + 60, u.Cd.Get("Shotgun"), EPS, "the cooldown starts as he fires");
        bool gun = u.ToolShown && u.ToolGfx == "cole_shotgun" && !u.WeaponsVisible;
        t.True(gun || !A.W.Models.Has("cole_shotgun"), "the shotgun in his hand, his weapons put away (HideWeapons, SetLinkGFX HndR)");
        StepTo(A, act.End - 0.1);
        t.True(e.Hp == hp && e2.Hp == hp2, "no damage before the clip is over");
        t.True(W_.InAct(u), "Cole still in the act");
        t.Equal("shotgun", u.Anim.CurName, "still the clip shotgun");
        StepTo(A, act.End + W_.STEP);
        t.True(e.Hp < hp, "the target is hit when the clip is over");
        t.True(e2.Hp < hp2, "the bystander too");
        t.True(!W_.InAct(u), $"Cole free after the clip ({u.Task})");
        t.True(!u.ToolShown && u.WeaponsVisible, "his weapons back (ShowWeapons)");
    }

    // ---------------------------------------------------------------- an order waits for the end of the act
    public static void OrderWaitsForTheAct(TestContext t)
    {
        var A = Arena(t, Sys2.Base, "Hu", "Cole_s0");
        var u = A.Caster; var e = A.EnemyChar!;
        double hp = e.Hp;
        var act = Begin(t, A, "Shotgun", e);
        A.W.Step();
        var at = u.Pos;
        A.W.Order(u, Order.Move(40, 40));
        t.True(W_.InAct(u), "the order does not break the act (SetUnStoppable)");
        t.True(u.Task.Next != null, "the order is kept for afterwards");
        // a fight it is drawn into does not break it either
        A.W.EngageTarget(u, A.EnemyChar2, null);
        t.True(W_.InAct(u), "a fight does not break the act");
        t.True(A.W.UseMove(u, Move(A, "Shotgun"), e, null) != null && W_.InAct(u), "no other move meanwhile");
        StepTo(A, act.End - 0.1);
        t.True(V3.Dist2D(at, u.Pos) < 0.01, "Cole stands while the clip runs");
        StepTo(A, act.End + W_.STEP);
        t.True(e.Hp < hp, "the shot still lands");
        t.Equal("move", u.Task.Type, "then the order");
        A.W.Step(); A.W.Step();
        t.True(V3.Dist2D(at, u.Pos) > 0.05, "and he walks");
    }

    // ---------------------------------------------------------------- a stun or death before the moment loses the effect
    public static void StunBreaksTheAct(TestContext t)
    {
        var A = Arena(t, Sys2.Base, "Hu", "Cole_s0");
        var u = A.Caster; var e = A.EnemyChar!;
        double hp = e.Hp;
        var act = Begin(t, A, "Shotgun", e);
        StepTo(A, act.Start + 1.0);
        A.W.Trap(u, 2, "roar");
        t.True(!W_.InAct(u), "the stun ends the act (SetTrapped: SetTaskImmediate)");
        StepTo(A, act.End + 3);
        t.Equal(hp, e.Hp, "the shot never lands");
        t.True(u.Cd.Get("Shotgun") > A.W.Time, "the cooldown stays spent");
    }

    public static void ThrowOnlyHoldsTheActUp(TestContext t)
    {
        var A = Arena(t, Sys2.Base, "Hu", "Cole_s0");
        var u = A.Caster; var e = A.EnemyChar!;
        double hp = e.Hp;
        var act = Begin(t, A, "Shotgun", e);
        StepTo(A, act.Start + 1.0);
        A.W.Trap(u, 2.5, "knock");          // a throw (the hit reaction is an action of its own: the task goes on after it)
        t.True(W_.InAct(u), "a throw does not end the act");
        StepTo(A, act.End + 0.2);
        t.Equal(hp, e.Hp, "nothing while he is down");
        StepTo(A, act.Start + 3.5 + W_.STEP);
        t.True(e.Hp < hp, "the shot lands when he is up again");
    }

    public static void DeathBreaksTheAct(TestContext t)
    {
        var A = Arena(t, Sys2.Base, "Hu", "Cole_s0");
        var u = A.Caster; var e = A.EnemyChar!;
        double hp = e.Hp;
        var act = Begin(t, A, "Shotgun", e);
        StepTo(A, act.Start + 1.0);
        A.W.Kill(u, null);
        StepTo(A, act.End + 3);
        t.Equal(hp, e.Hp, "a dead hero's shot never lands");
    }

    // ---------------------------------------------------------------- a projectile leaving at its start timer (Bela)
    public static void SnipershotLeavesAtItsTime(TestContext t)
    {
        var A = Arena(t, Sys2.Base, "Hu", "Bela_s0");
        var u = A.Caster; var e = A.EnemyChar!;
        double hp = e.Hp;
        var act = Begin(t, A, "Snipershot", e);
        double len = u.Anim!.Duration("throwdownshot");
        t.True(len > 2, $"Bela has the clip throwdownshot ({len} s)");
        int shots = A.W.Projectiles.Count;
        StepTo(A, act.Start + 2.0 - 0.1);
        t.Equal(shots, A.W.Projectiles.Count, "no arrow before 2 s (CSniperArrow's start timer)");
        StepTo(A, act.Start + 2.0 + W_.STEP);
        t.True(A.W.Projectiles.Count > shots, "the arrow leaves at 2 s");
        t.Equal(hp, e.Hp, "and is in the air");
        t.True(W_.InAct(u), "Bela still in the clip");
        StepTo(A, act.Start + 3.0);
        t.True(e.Hp < hp, "the arrow hits");
        StepTo(A, act.End + W_.STEP);
        t.True(!W_.InAct(u), "Bela free after the clip");
    }

    // ---------------------------------------------------------------- the effect as the clip starts, held to its end
    public static void MayorJumpHoldsToTheEnd(TestContext t)
    {
        var A = Arena(t, Sys2.Base, "Hu", "mayor_s0");
        var u = A.Caster; var e = A.EnemyChar!;
        e.Pos = new V3(0, 0, -3);
        double hp = e.Hp;
        var why = A.W.UseMove(u, Move(A, "Mayor_Specialmove_0"), null, null);
        t.True(why == null, $"the jump ({why})");
        t.True(e.Hp < hp, "the damage as the clip starts (MayorSpecialMove.usl calc_dmg)");
        t.True(W_.InAct(u), "the mayor is held in the clip");
        double end = u.Task.Act!.End, len = u.Anim!.Duration("res_sm_jump");
        t.True(len > 1, $"the clip res_sm_jump ({len} s)");
        StepTo(A, end - 0.1);
        t.True(W_.InAct(u), "still in it");
        StepTo(A, end + W_.STEP);
        t.True(!W_.InAct(u), "free when it ends");
    }

    // ---------------------------------------------------------------- an effect at a time mark (the sacrifice: 0.9 s)
    public static void SacrificeAtItsMark(TestContext t) => Sacrifice(t, false);
    public static void SacrificeStunnedBefore(TestContext t) => Sacrifice(t, true);
    static void Sacrifice(TestContext t, bool stun)
    {
        var A = Arena(t, Sys2.Base, "Hu", "schliemann_s0");
        var u = A.Caster; var o = A.OwnChar!;
        var act = Begin(t, A, "schliemann_special_move_1", o);
        t.True(act.End - act.Start >= 0.9 - EPS, "the act lasts at least to the strike");
        StepTo(A, act.Start + 0.85);
        t.True(o.Alive, "the unit lives before the strike");
        if (stun) A.W.Trap(u, 2, "roar");
        StepTo(A, act.Start + 0.9 + W_.STEP);
        t.Equal(!stun, !o.Alive, stun ? "a stun before the strike saves it" : "the strike at 0.9 s");
    }

    // ---------------------------------------------------------------- an automatic move inside a fight (multishot)
    public static void AutoMoveBackToTheFight(TestContext t)
    {
        var A = Arena(t, Sys2.Base, "Ninigi", "ninigi_archer");
        var u = A.Caster; var e = A.EnemyChar!;
        u.Stance = 2;
        e.Pos = new V3(0, 0, -15);
        A.W.Order(u, new Order("attack") { Target = e });
        MoveAct? act = null;
        for (int i = 0; i < 400 && act == null; i++) { A.W.Step(); if (W_.InAct(u)) act = u.Task.Act; }
        t.True(act != null, "an automatic move (burst arrow / multishot) begins in the fight");
        t.Equal("attack", u.Task.Prev?.Type, "it broke off the fight");
        t.True(act!.Clip != null && act.Clip.StartsWith("tec_sm_"), $"its clip plays ({act.Clip})");
        StepTo(A, act.End - 0.1);
        t.True(W_.InAct(u), "the archer stays in it");
        StepTo(A, act.End + W_.STEP);
        t.Equal("attack", u.Task.Type, "back to the fight (ReturnToFight)");
        t.True(u.Task.Target == e, "on the same enemy");
    }

    // ---------------------------------------------------------------- the same in a recorded game: a deterministic world
    public static void ActsAreDeterministic(TestContext t)
    {
        string Run()
        {
            var A = Arena(t, Sys2.Base, "Hu", "Cole_s0");
            Begin(t, A, "Shotgun", A.EnemyChar);
            StepTo(A, A.W.Time + 1.3);
            A.W.Order(A.Caster, Order.Move(30, 10));
            StepTo(A, A.W.Time + 5);
            return string.Join(";", A.W.Units.Select(x => $"{x.Id}:{x.Hp:R}:{x.Pos.X:R}:{x.Pos.Z:R}:{x.Task.Type}"));
        }
        t.Equal(Run(), Run(), "the same world twice");
    }
}
