// The animation audit's last three rows (docs/SIMULATION.md "Animation audit (2026-11-13)"): the head cut off
// (CCharacter.DiePerHeadOff), the hit reactions (FightingObj ProvideDmg's EID_HitReactionOVLAnim_*) and the gait
// transitions (walkanimconfig.txt).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class AnimHeadHitGaitTests
{
    // a victim facing -z, a killer of the enemy at (ax, az) whose last swing was `strike`; steps the world until the
    // beheading's draw is (or is not) a hit for the victim, then kills it with one blow
    static Unit Kill(TestContext t, W_ w, Player p, Player e, string victim, string strike, bool roll, double x)
    {
        Unit? v = null;
        for (int i = 0; i < 40; i++)
        {
            v = w.SpawnUnit(victim, p, x, 0)!;
            v.Heading = 0;
            if ((w.HeadOffRoll(v) % 3 == 0) == roll) break;
            v.Alive = false; w.Kill(v, null); v = null;
            w.Step();
        }
        if (v == null) { t.True(false, "no step with the draw wanted"); throw new InvalidOperationException(); }
        var a = w.SpawnUnit("aje_warrior", e, x + 3, 0)!;
        a.FightAnim = strike;
        v.Hp = 1;
        w.ProvideDmg(5, a, v, false, 0, 0, null);
        t.True(!v.Alive, victim + " killed");
        a.Alive = false; w.Kill(a, null);
        return v;
    }

    /// <summary>
    /// DiePerHeadOff (character.usl:2252-2270, 2297-2299): a "res" victim killed after one of the res strikes 13 / 15 / 1
    /// / 7 / 12 with the draw % 3 == 0 gets the head object "&lt;class&gt;_head" and plays chop_1; a "nat" victim, another
    /// strike or another draw: no head, the clip of the side (die_simple from the right). The draw is not World.Rng's.
    /// </summary>
    public static void BeheadingByStrikeCasteAndDraw(TestContext t)
    {
        var (w, p, e) = Sys2.World(t, Sys2.Base, "Hu");
        var probe = w.SpawnUnit("hu_worker", p, -20, 0)!;
        if (probe.Anim == null || !probe.Anim.Has("chop_1")) t.Skip("no clips of " + probe.Gfx);
        t.Equal("res", probe.Def!.Caste, "a worker's caste");
        t.Equal("hu_worker_head", w.Data.ClassGfx("hu_worker_head"), "the head class's model");

        var rng0 = string.Join(",", w.Rng.State);
        ulong r0 = w.HeadOffRoll(probe);
        t.Equal(r0, w.HeadOffRoll(probe), "the draw is the same when asked again");
        t.Equal(rng0, string.Join(",", w.Rng.State), "World.Rng untouched by the beheading's draw");
        var hit = Kill(t, w, p, e, "hu_worker", "res_strike_13", true, 0);
        t.Equal("hu_worker_head", hit.Head, "res victim, res_strike_13, draw 0: the head");
        t.Equal("chop_1", hit.Anim!.CurName, "beheaded: chop_1");

        var other = Kill(t, w, p, e, "hu_worker", "res_strike_3", true, 10);
        t.Equal(null, other.Head, "another strike: no head");
        t.Equal("die_simple", other.Anim!.CurName, "another strike: the clip of the side");

        var noDraw = Kill(t, w, p, e, "hu_worker", "res_strike_1", false, 20);
        t.Equal(null, noDraw.Head, "the draw not 0: no head");

        // a "nat" character (the Hu spearman) with chop_1 on its model
        var nat = w.SpawnUnit("hu_spearman", p, -30, 0)!;
        t.Equal("nat", nat.Def!.Caste, "the spearman's caste");
        var sp = Kill(t, w, p, e, "hu_spearman", "res_strike_13", true, 30);
        t.Equal(null, sp.Head, "nat victim: no head");
        t.True(sp.Anim!.CurName != "chop_1", "nat victim: no chop_1");

        // a killer that is no character (an animal) never beheads
        var v2 = w.SpawnUnit("hu_worker", p, 40, 0)!;
        for (int i = 0; i < 40 && w.HeadOffRoll(v2) % 3 != 0; i++) w.Step();
        var boar = w.Data.IndexOrder.FirstOrDefault(n => w.Data.Info(n)?.Type == "ANML" && w.Models.Has(w.Data.ClassGfx(n) ?? n));
        if (boar != null)
        {
            var an = w.SpawnUnit(boar, e, 43, 0)!;
            an.FightAnim = "res_strike_13";
            v2.Hp = 1; w.ProvideDmg(5, an, v2, false, 0, 0, null);
            t.Equal(null, v2.Head, "killed by an animal: no head");
        }
    }

    /// <summary>
    /// The hit reaction (FO:4423-4446): a melee blow on a character counts a reaction with the side the blow came from
    /// (f / b / l / r - DIR_LEFT the unit's left), a projectile none, a vehicle none, a stunned (trapped) unit none.
    /// </summary>
    public static void HitReactionBySide(TestContext t)
    {
        var (w, p, e) = Sys2.World(t, Sys2.Base, "Hu");
        char Blow(double ax, double az, bool proj = false, string victim = "hu_warrior", Action<Unit>? prep = null)
        {
            var v = w.SpawnUnit(victim, p, 0, 0)!;
            v.Heading = 0;          // facing -z: its right is +x
            prep?.Invoke(v);
            var a = w.SpawnUnit("aje_warrior", e, ax, az)!;
            int n = v.HitReactN;
            w.ProvideDmg(3, a, v, proj, 0, 0, null);
            char side = v.HitReactN == n + 1 ? v.HitReactSide : v.HitReactN == n ? '-' : '?';
            a.Alive = false; w.Kill(a, null); v.Alive = false; w.Kill(v, null);
            return side;
        }
        t.Equal('f', Blow(0, -3), "from the front (-z)");
        t.Equal('b', Blow(0, 3), "from behind (+z)");
        t.Equal('r', Blow(3, 0), "from its right (+x)");
        t.Equal('l', Blow(-3, 0), "from its left (-x)");
        t.Equal('-', Blow(0, -3, proj: true), "a projectile: no reaction");
        t.Equal('-', Blow(0, -3, prep: v => w.Trap(v, 5, "roar")), "stunned (SetTrapped): no reaction");
        var veh = w.Data.IndexOrder.FirstOrDefault(n => w.Data.Info(n) is { Type: "VHCL", Tribe: "Hu" });
        if (veh != null) t.Equal('-', Blow(0, -3, victim: veh), "a vehicle (" + veh + "): no reaction");
        else t.True(false, "no Hu vehicle in the data");
    }

    /// <summary>The data the two need: every head class of the class files has its model (the toolkit converts the 163
    /// "&lt;class&gt;_head" meshes of all_characters.gsf, static: no clips), and the characters with the side hit reactions
    /// keep them (hit_reaction_fa / la / ra / ba, the view's AnimController.HitReaction picks them by side).</summary>
    public static void HeadModelsAndSideClipsInData(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        var probe = w.SpawnUnit("hu_warrior", p, 0, 0)!;
        if (probe.Anim == null || !probe.Anim.Has("hit_reaction")) t.Skip("no clips of " + probe.Gfx);
        if (!w.Models.Has("hu_worker_head")) t.Skip("the data has no head models (converted before the toolkit exported them; re-run Prepare)");
        // (the class files' "<class>_head" entries; a broken entry - text after the name: miyagi_s0_head - is the class's
        // own name, as Combat.HeadOff takes it; brachiosaurus_head is no beheading head; aje_rammer_head is an empty model
        // in all_characters.gsf - no chunks, the converter makes no file: the original shows no head there either)
        var heads = w.Data.Extra.ClassGfx.Keys.Where(n => n.EndsWith("_head", StringComparison.Ordinal) && n is not ("brachiosaurus_head" or "aje_rammer_head")).OrderBy(n => n, StringComparer.Ordinal).ToList();
        string Gfx(string n) { var g = w.Data.ClassGfx(n) ?? n; return g.AsSpan().IndexOfAny(" \t\r\n") >= 0 ? n : g; }
        var missing = heads.Where(n => !w.Models.Has(Gfx(n))).ToList();
        t.True(heads.Count > 150 && missing.Count == 0, $"{heads.Count} head classes, without a model: {string.Join(", ", missing)}");
        t.Equal(0, w.Models.Clips("hu_worker_head").Count, "a head is a static mesh");
        foreach (var s in new[] { "fa", "la", "ra", "ba" })
            t.True(probe.Anim.Has("hit_reaction_" + s), $"{probe.Gfx}: hit_reaction_{s}");
    }

    /// <summary>walkanimconfig.txt Animals2 .. 4: the transitions from gait to gait (the digits: a gait first)</summary>
    public static void GaitTableChains(TestContext t)
    {
        static string C(int g, int a, int b) => string.Join(" ", WalkGaits.Chain(g, a, b).Select(x => $"T{x.A}{x.B}"));
        t.Equal("T12", C(3, 1, 2), "Animals3 1 -> 2");
        t.Equal("T12 T23", C(3, 1, 3), "Animals3 1 -> 3: gait 2 first");
        t.Equal("T21", C(3, 3, 1), "Animals3 3 -> 1: brake_2_1 at once");
        t.Equal("T32", C(3, 3, 2), "Animals3 3 -> 2");
        t.Equal("T12 T23 T34", C(4, 1, 4), "Animals4 1 -> 4");
        t.Equal("T32", C(4, 4, 2), "Animals4 4 -> 2: brake_3_2");
        t.Equal("T21", C(2, 2, 1), "Animals2 2 -> 1");
        t.Equal("", C(1, 1, 1), "Animals1: none");
        t.Equal("accel_1_2_new", WalkGaits.ClipName("walk_", "_new", 1, 2), "defn walk set");
        t.Equal("shoulder_brake_3_2", WalkGaits.ClipName("shoulder_walk_", "", 3, 2), "shoulder walk set");
        t.Equal("accel_2_3", WalkGaits.ClipName("carry_walk_", "", 2, 3), "carry walk set: the plain clips");
        t.Equal(0, WalkGaits.Parse("walk_3_end_s").Gait, "an end clip is no gait");
        t.Equal(0, WalkGaits.Parse("all_walk_3_loop").Gait, "nor a rider's loop");
    }

    /// <summary>
    /// A worker speeding up from gait 1 to gait 2 plays accel_1_2 once, then walk_2's loop part (not its start part);
    /// 1 -> 3 plays accel_1_2 and accel_2_3; slowing down 3 -> 2 brake_3_2. The unit's speed is the simulation's as
    /// before (only the clip changes); stopping during a transition plays the new gait's end part.
    /// </summary>
    public static void GaitTransitionsPlayed(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        var u = w.SpawnUnit("hu_worker", p, 0, 0)!;
        var A = u.Anim!;
        if (!A.Has("accel_1_2") || !A.Has("walk_2#l")) t.Skip("no clips of " + u.Gfx);
        var seen = new List<string>();
        int serial = A.Serial;
        void Run(double s, int gait)
        {
            for (int i = 0; i < (int)(s / W_.STEP); i++)
            {
                u.MoveAnim(u.GaitSpeed(gait), false, gait);
                A.Update(W_.STEP);
                if (A.Serial != serial) { serial = A.Serial; seen.Add($"{A.Cur!.Clip}:{A.Phase}"); }
            }
        }
        Run(2, 1);
        t.True(A.CurName == "walk_1" && A.Phase == "l", "walking in gait 1: " + A);
        seen.Clear();
        Run(A.Duration("accel_1_2") + 1, 2);
        t.Equal("accel_1_2:t walk_2#l:l", string.Join(" ", seen), "gait 1 -> 2: accel_1_2, then walk_2's loop part");
        seen.Clear();
        Run(A.Duration("brake_2_1") + 1, 1);
        t.Equal("brake_2_1:t walk_1#l:l", string.Join(" ", seen), "gait 2 -> 1: brake_2_1");
        seen.Clear();
        Run(A.Duration("accel_1_2") + A.Duration("accel_2_3") + 1, 3);
        t.Equal("accel_1_2:t accel_2_3:t walk_3#l:l", string.Join(" ", seen), "gait 1 -> 3: through gait 2");
        seen.Clear();
        Run(0.1, 2);
        t.Equal("brake_3_2:t", string.Join(" ", seen), "gait 3 -> 2: brake_3_2");
        t.Equal("walk_2", A.CurName, "the clip asked for is the new gait's already");
        seen.Clear();
        u.MoveAnim(0, false);
        A.Update(W_.STEP);
        t.True(A.Phase == "e" && A.Cur!.Clip == "walk_2#e", "stopping during the transition: the new gait's end part, " + A);

        // a speed the walk set's table goes through a gait for (4 gaits: the megaloceros)
        var deer = w.SpawnUnit("Megaloceros", null, 20, 0);
        if (deer?.Anim is not { } D || !D.Has("accel_3_4")) t.True(false, "the megaloceros with accel_3_4");
        else
        {
            D.Play("walk_1", new AnimOpts { Cut = true });
            for (int i = 0; i < 100; i++) D.Update(W_.STEP);
            var via = WalkGaits.Transitions(D, "walk_1", "walk_4");
            t.Equal("accel_1_2 accel_2_3 accel_3_4", string.Join(" ", via ?? Array.Empty<string>()), "megaloceros 1 -> 4");
        }
        // another walk set (carrying): no transition between the sets
        t.Equal(null, WalkGaits.Transitions(A, "walk_1", "carry_walk_2"), "walk -> carry walk: none");
    }
}
