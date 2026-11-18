// Scenario tests of the special moves (src/Sim/Systems/Moves.cs, MovesMirage.cs): every move that every unit class of
// every tribe (heroes included) offers at its highest level, with all of the tribe's upgrades, is used in an arena
// (Sys3Arena) on the target its kind needs - it must start, run, set its cooldown to the tech tree's duration (taken
// when it ran: at once, after the walk into range, or at the end of a cast) and have its effect. Then the automatic
// moves (Quake / Roar / Stampede), entrenching, camouflage and a set of MIRAGE moves step by step.
//
// The damage, knockback, trap, gate and filter calls go through the stand-ins of World.Stubs.cs (sys3 block) until
// combat & co. are ported, so the checks look at what changes (hit points lost, states set), not at exact numbers.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys3MovesTests
{
    public static void EveryMoveBase(TestContext t) => EveryMove(t, RulesTestData.Base(t));
    public static void EveryMoveMirage(TestContext t) => EveryMove(t, RulesTestData.Mirage(t));

    /// <summary>moves that cannot succeed in the arena (yet): their condition needs combat's range zones (stand-in: out of
    /// reach), a passenger, a rider, a flight to land from, a target with something to steal</summary>
    static readonly HashSet<string> MAY_FAIL = new() { "AlloScrunch", "triceratops_tusk", "stimpack_rex", "jetfly_off", "MountThatUnit" };

    sealed class Snap
    {
        public Dictionary<Entity, double> Hp = new();
        public int Units;
        public double QFood, PFood;
        public V3 CasterPos;
    }

    static double Hp(Snap s, Entity? e) => e != null && s.Hp.TryGetValue(e, out var v) ? v : double.NaN;
    static bool Hurt(Snap s, Entity? e) => e != null && (!e.Alive || e.Hp < Hp(s, e));
    static bool Set(Entity? e, string k) => e != null && e.St.T.ContainsKey(k);

    /// <summary>the effect of a move in the arena: null = seen, else what is missing (looked at 16 s after it ran)</summary>
    static readonly Dictionary<string, Func<Sys3Arena, Snap, string?>> EFFECT = new()
    {
        ["Shotgun"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "the target is not hurt",
        ["Snipershot"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "the target is not hurt",
        ["Ada_DeathShoot"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "the target is not hurt",
        ["schliemann_deathshoot"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "the target is not hurt",
        ["Tesla_DstrVhcl_0"] = (A, s) => A.EnemyVehicle == null || !A.EnemyVehicle.Alive ? null : "the vehicle lives",
        ["Druid_HealAnml_0"] = (A, s) => A.OwnAnimal == null || A.OwnAnimal.Hp == A.OwnAnimal.MaxHp ? null : "the own animal is not healed",
        ["Babbage_Minigun_0"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "nobody in the cone hurt",
        ["Babbage_ExoMinigun_0"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "nobody in the cone hurt",
        ["Mayor_Specialmove_0"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "the enemy within 6 m is not hurt",
        ["warden_spec"] = (A, s) => A.OwnChar == null || Set(A.OwnChar, "anmlImmune") ? null : "no animal immunity",
        ["livingstone_special_move_1"] = (A, s) => Set(A.Caster, "vanish") && A.Caster.St.Camo.Has("hero") == (A.W.Time < A.Caster.St.T["vanish"]) ? null : "no vanish (or it did not end in time)",
        ["schliemann_special_move_1"] = (A, s) => A.OwnChar != null && !A.OwnChar.Alive && A.Caster.LocalTT != null ? null : "the own unit is not sacrificed",
        ["schliemann_sacrifice"] = (A, s) => A.OwnChar != null && !A.OwnChar.Alive && Set(A.Caster, "f:" + MirageMoves.F.sacrifice) ? null : "the own unit is not sacrificed",
        ["Hypnosis"] = (A, s) => A.WildAnimal?.St.TrapWhy == "hypnosis" ? null : "the animal is not hypnotised",
        ["MickDundee"] = (A, s) => A.WildAnimal?.St.TrapWhy == "hypnosis" ? null : "the animal is not hypnotised",
        ["Resurrect"] = (A, s) => !A.OwnSpirit!.Alive && A.W.Units.Count(u => u.Alive && u.Owner == A.P && u.Name == A.OwnSpirit.Name) == 2 ? null : "the spirit did not come back",
        ["reanimation"] = (A, s) => !A.EnemySpirit!.Alive && A.W.Units.Any(u => u.Alive && u.Owner == A.P && u.Name == A.EnemySpirit.Name) ? null : "the enemy's spirit did not come back as hers",
        ["call_twins"] = (A, s) => A.W.Units.Count(u => u.Alive && (u.Name == "Barry_s0" || u.Name == "Harry_s0")) == A.W.Units.Count(u => u.Name == "Barry_s0" || u.Name == "Harry_s0") ? null : "twins",
        ["insects"] = (A, s) => A.Q.Res["food"] < s.QFood ? null : "the building's owner lost no food",
        ["termites"] = (A, s) => Hurt(s, A.EnemyBuilding) ? null : "the building is not hurt",
        ["illusion"] = (A, s) => A.W.Units.Count(u => u.Illusion && u.Name == A.Caster.Name) == 3 || A.W.Units.Count(u => u.Illusion) == 0 && !A.W.Models.Has(A.Caster.Gfx) ? null : "no illusions",
        ["quicksand"] = (A, s) => A.EnemyChar?.St.TrapWhy == "quicksand" ? null : "the enemy in the sand is not held",
        ["tornado"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "the tornado hurt nobody",
        ["reveal"] = (A, s) => A.EnemyChar!.St.RevealT > 0 ? null : "the enemy is not revealed",
        ["oracle"] = (A, s) => A.EnemyChar!.St.RevealT > 0 && A.W.Events.Any(e => e.Type == "revealarea") ? null : "the area is not revealed",
        ["fireworks"] = (A, s) => A.EnemyChar!.St.RevealT > 0 && A.W.Events.Count(e => e.Type == "glow") == 6 ? null : "no fireworks",
        ["Mammoth_Trumpet"] = (A, s) => A.W.Units.Any(v => v.Owner == A.Q && Set(v, "trumpet")) ? null : "no trumpet on the enemy",   // (after the clip: a titan's own turrets may have shot the first enemy by then)
        ["mammoth_trumpet"] = (A, s) => A.W.Units.Any(v => v.Owner == A.Q && Set(v, "trumpet")) ? null : "no trumpet on the enemy",   // (after the clip: a titan's own turrets may have shot the first enemy by then)
        ["titan_paw"] = (A, s) => A.W.Units.Any(v => v.Owner == A.Q && Set(v, "paw")) ? null : "no pawing on the enemy",   // (after the clip: a titan's own turrets may have shot the first enemy by then)
        ["trex_roar"] = (A, s) => A.EnemyChar!.St.TrapWhy == "roar" ? null : "the enemy is not held by the roar",
        ["BrachioStomp"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "the stomp hurt nobody",
        ["enchain"] = (A, s) => A.EnemyChar!.St.TrapWhy == "enchain" ? null : "the enemy is not chained",
        ["doping"] = (A, s) => Set(A.Caster, "doping") ? null : "no doping",
        ["entrench"] = (A, s) => A.Caster.St.Camo.Has("entr") && A.Caster.Task.Type == "entrench" ? null : "not dug in",
        ["lockpicking"] = (A, s) => A.EnemyGate == null || A.EnemyGate.GateState == Moves.GATE_OPEN ? null : "the gate is not open",
        ["camouflage"] = (A, s) => A.Caster.St.Camo.Has("aje") && A.Caster.CamoGfx != null ? null : "not camouflaged",
        ["defensive_mode_on"] = (A, s) => A.Caster.Defensive && A.Caster.LocalTT!.Has("Hu/Upgrades/hu_warrior/hu_defensive_mode") ? null : "not defensive",
        ["defensive_mode_off"] = (A, s) => !A.Caster.Defensive ? null : "still defensive",
        ["jetpack"] = (A, s) => V3.Dist2D(s.CasterPos, A.Caster.Pos) > 5 ? null : "no jump",
        ["jetleap"] = (A, s) => V3.Dist2D(s.CasterPos, A.Caster.Pos) > 5 ? null : "no jump",
        ["Babbage_ExoJumpjet_0"] = (A, s) => V3.Dist2D(s.CasterPos, A.Caster.Pos) > 5 ? null : "no jump",
        ["seas_jumpjet"] = (A, s) => V3.Dist2D(s.CasterPos, A.Caster.Pos) > 5 ? null : "no jump",
        ["jetfly_on"] = (A, s) => A.Caster.Flying && A.Caster.LiftH > 12.9 ? null : "not flying",
        ["explosives_rounds"] = (A, s) => A.Caster.Rounds && (A.Caster.Hp < Hp(s, A.Caster) || !A.Caster.Alive) ? null : "no explosive rounds",
        ["stimpack"] = (A, s) => A.Caster.Cls != "CHTR" || Set(A.Caster, "stim") ? null : "no stimpack",
        ["frightening"] = (A, s) => Set(A.EnemyChar, "f:" + MirageMoves.F.fright) ? null : "the enemy is not frightened",
        ["song_of_valor"] = (A, s) => A.OwnChar == null || Set(A.OwnChar, "f:" + MirageMoves.F.valor) ? null : "no valor",
        ["ceasefire"] = (A, s) => Set(A.OwnChar, "peace") ? null : "no peace",
        ["cyborg_roar"] = (A, s) => Set(A.Caster, "cyborg") ? null : "no roar",
        ["contamination"] = (A, s) => Set(A.EnemyChar, "noHeal") ? null : "no infection",
        ["deafening"] = (A, s) => Set(A.EnemyChar, "deaf") ? null : "not deafened",
        ["parasite"] = (A, s) => A.W.Watchers.Count == 1 && A.W.Watchers[0].Host == A.EnemyChar ? null : "no parasite",
        ["meteor"] = (A, s) => Hurt(s, A.EnemyChar) && A.W.Events.Any(e => e.Type == "blast") ? null : "no meteor",
        ["blinding_storm"] = (A, s) => Set(A.EnemyChar, "storm") ? null : "no storm",
        ["suicide"] = (A, s) => A.OwnChar == null || !A.OwnChar.Alive && A.W.Events.Any(e => e.Type == "blast") ? null : "no walking bomb",
        ["cyborg_tnt"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "the dynamite hurt nobody",
        ["triceratops_shake_off"] = (A, s) => Hurt(s, A.EnemyChar) ? null : "the shake hurt nobody",
    };

    sealed class Outcome { public int Used, Effects; public List<string> Fails = new(); public HashSet<string> Ids = new(); }

    static void EveryMove(TestContext t, string data)
    {
        var (R, M) = Sys3Data.Load(t, data);
        var o = new Outcome();
        int heroes = 0;
        foreach (var tribe in Sys3Data.TRIBES)
        {
            if (R.Base.Start["Objects"]?[tribe] == null) continue;
            // the moves usable with a fresh tree and those usable with all of the tribe's upgrades (some upgrades hide a move)
            var probes = new[] { false, true }.Select(up => { var p = new Player(9, tribe, new PlayerOpts { Rules = R }); if (up) Sys3Data.Upgrade(p, R); return (up, p); }).ToList();
            var probeW = new W_(R, M, new FlatMap(64));
            foreach (var name in R.IndexOrder)
            {
                var ix = R.Info(name)!;
                if (ix.Type == "BLDG" || (ix.Tribe != tribe && ix.Tribe != "Special")) continue;
                int lv = Enumerable.Range(1, 5).Reverse().FirstOrDefault(l => R.HasLevel(name, l));
                if (lv == 0) lv = 1;
                var done = new HashSet<string>();
                bool any = false;
                foreach (var (up, probe) in probes.AsEnumerable().Reverse())
                {
                    var pu = new Unit(probeW, name, probe, 0, 0, lv);
                    foreach (var a in probeW.MovesOf(pu).Where(a => Moves.MOVES.ContainsKey(a.Id) && probeW.MoveCheck(pu, a) == null))
                        if (done.Add(a.Id)) { RunOne(t, R, M, tribe, name, lv, a.Id, o, up); any = true; }
                }
                if (any && ix.Tribe == "Special") heroes++;
            }
        }
        foreach (var f in o.Fails.Take(40)) t.Log(f);
        t.Log($"{data}: {o.Used} moves used ({o.Ids.Count} different), {heroes} heroes, {o.Effects} effects seen, {o.Fails.Count} failures");
        t.True(o.Fails.Count == 0, $"every move works ({o.Fails.Count} failures, first: {o.Fails.FirstOrDefault()})");
        t.True(o.Ids.Count >= 30 && heroes >= 40, "a broad sample");
    }

    static void RunOne(TestContext t, Rules R, PwRemake.Sim.Content.ModelManifest M, string tribe, string name, int lv, string id, Outcome o, bool upgraded)
    {
        Sys3Arena A;
        try { A = Sys3Arena.Make(R, M, tribe, name, lv, upgraded); } catch (SkipException) { return; }
        var W = A.W; var u = A.Caster;
        A.P.MaxUnits = 100;
        // the arena's units are dummies: nobody starts a fight of its own (stance 3), so what changes is the move's doing
        // (with combat in the world a hurt caster would fight back - combat.js onHurt - and break its dig-in or its cast)
        foreach (var e in W.Units) if (e.Owner != null) e.Stance = 3;
        var a = W.MovesOf(u).First(x => x.Id == id);
        var Mv = Moves.MOVES[id];
        u.LastTarget = A.EnemyChar;
        var s = new Snap { Units = W.Units.Count, QFood = A.Q.Res["food"], PFood = A.P.Res["food"], CasterPos = u.Pos };
        foreach (var e in W.Units) s.Hp[e] = e.Hp;
        foreach (var e in W.Buildings) s.Hp[e] = e.Hp;
        var (tg, pos) = A.TargetFor(Mv);
        string where = $"{tribe} {name} L{lv} {id}{(upgraded ? "" : " (fresh tree)")}";
        if (Mv.Target == "own" && tg == null || Mv.Target == "vehicle" && tg == null || Mv.Target == "gate" && tg == null || Mv.Target == "animal" && tg == null) return;
        double t0 = W.Time;
        string? why;
        try { why = W.UseMove(u, a, tg, pos); }
        catch (Exception e) { o.Fails.Add($"{where}: {e.GetType().Name} {e.Message}"); return; }
        o.Used++; o.Ids.Add(id);
        double ranAt = t0;
        try
        {
            if (why == null && !Mv.Self)
            {
                if (u.Task.Type != "special") { o.Fails.Add($"{where}: no special task ({u.Task})"); return; }
                // walking into range, then the move runs (its cooldown starts) and plays out (its act) - or a cast
                for (int i = 0; i < 800 && (u.Task.Type == "special" && u.Task.Act == null || u.Task.Type == "cast"); i++) W.Step();
                ranAt = W.Time;
                if (u.Task.Type == "special" && u.Task.Act != null) ranAt = u.Task.Act.Start;
                for (int i = 0; i < 800 && (u.Task.Type == "special" || u.Task.Type == "cast"); i++) W.Step();
                if (u.Task.Type == "special" || u.Task.Type == "cast") { o.Fails.Add($"{where}: still {u.Task} after 40 s"); return; }
            }
            if (why != null)
            {
                if (!(MAY_FAIL.Contains(id) || (id == "stimpack" && u.Cls != "CHTR"))) o.Fails.Add($"{where}: refused ({why})");
                return;
            }
            double cd = u.Cd.Get(id);
            bool castMove = id is "reanimation" or "meteor" or "ceasefire";
            if (!Mv.NoCooldown || castMove)
            {
                if (cd == 0 && MAY_FAIL.Contains(id)) return;
                if (Math.Abs(cd - (ranAt + a.Time)) > 1e-9) { o.Fails.Add($"{where}: cooldown {cd - ranAt:0.###} s after it ran, the tech tree says {a.Time}"); return; }
            }
            A.Step(16);
            if (EFFECT.TryGetValue(id, out var fx))
            {
                var bad = fx(A, s);
                if (bad != null) o.Fails.Add($"{where}: {bad}"); else o.Effects++;
            }
        }
        catch (Exception e) { o.Fails.Add($"{where}: {e.GetType().Name} {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()}"); }
    }
}
