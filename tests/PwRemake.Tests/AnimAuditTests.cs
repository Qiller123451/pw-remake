// The animation audit (2026-11-13, docs/VIEW.md "Animation audit"): how the original drives a unit's start / loop / end
// clips outside the fights (Harvest.usl, Mine.usl, GetCorn.usl, BuildUp.usl, IdleAnims.txt, CCharacter.CreateCorpse) and
// how the remake plays them. AnimAction(clip, loops, intro, outro) is the start part once, the loop part `loops` times,
// the end part once; SetSLEAnim(clip, 9999, ..) loops until AnimActionStopLoop plays the end part.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class AnimAuditTests
{
    // every start of a clip part (AnimCtl.Serial): "clip#phase" - a restart of the same part shows up twice
    static List<string> Starts(W_ w, Unit u, double seconds, Func<bool>? stop = null)
    {
        var seen = new List<string>();
        int serial = u.Anim!.Serial;
        for (int i = 0; i < (int)(seconds / W_.STEP); i++)
        {
            w.Step();
            if (u.Anim!.Serial != serial) { serial = u.Anim.Serial; seen.Add($"{u.Anim.CurName}#{u.Anim.Phase}"); }
            if (stop?.Invoke() == true) break;
        }
        return seen;
    }

    /// <summary>AnimCtl.PlayLoops without game data: start part once, the loop part counted, the end part, onDone; its
    /// length; StopLoop ends an endless loop with the end part; the next Play starts afresh after an end part ran out.</summary>
    public static void PlayLoopsCountsTheLoopPart(TestContext t)
    {
        var cs = new ClipSet();
        cs.Add("work", 1.8); cs.Add("work#s", 0.4); cs.Add("work#l", 1.0); cs.Add("work#e", 0.4); cs.Loops["work"] = (0.4, 1.4);
        cs.Add("stand", 2.0); cs.Add("plain", 0.5);
        var A = new AnimCtl(cs, null);
        double len = A.PlayLoops("work", 3);
        t.Near(3.8, len, 1e-9, "start + 3 loops + end");
        var phases = new List<string>(); int serial = A.Serial; double tt = 0;
        while (tt < 5) { A.Update(0.05); tt += 0.05; if (A.Serial != serial) { serial = A.Serial; phases.Add(A.Phase); } if (A.Done && phases.Count > 0 && phases[^1] == "e") break; }
        t.Equal("l,e", string.Join(",", phases), "the loop part follows the start part, the end part the third loop (no restarts)");
        t.Near(3.8, tt, 0.06, "it ran out after its length");
        t.True(A.Done, "done: held on the last frame");
        A.Play("stand");
        t.Equal("stand", A.CurName, "the next clip starts afresh after the end part ran out");
        // endless (9999) until StopLoop
        A.PlayLoops("work", 9999);
        for (int i = 0; i < 200; i++) A.Update(0.05);
        t.Equal("l", A.Phase, "9999: still looping after 10 s");
        A.StopLoop();
        t.Equal("e", A.Phase, "StopLoop: the end part");
        // a clip without loop marks: loops x the whole clip
        t.Near(1.5, A.PlayLoops("plain", 3), 1e-9, "no loop marks: 3 x the clip");
        // Still: a frame held
        A.Still("work", 0.8);
        t.True(A.Cur!.Paused && Math.Abs(A.Cur.Time - 0.8) < 1e-9, "Still: the frame held");
    }

    /// <summary>A Hu worker chops a tree (chop_tree: start part once, the loop part, the end part when the tree falls),
    /// then hacks the log (hacking_dirt: start part, 5 loops, end part - not the whole clip each loop) and only then
    /// picks it up; walks with shoulder_walk_* carrying wood, carry_walk_* carrying stone, walk_* empty.</summary>
    public static void GatherIntroLoopOutroAndCarryWalks(TestContext t)
    {
        foreach (var res in new[] { "wood", "stone" })
        {
            var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
            w.PlaceBuilding(w.Data.Extra.Start["Hu"].Base, p, 0, 0);
            var u = w.SpawnUnit("hu_worker", p, 8, 8)!;
            if (u.Anim == null || !u.Anim.Has("hacking_dirt#l")) t.Skip("no clips of " + u.Gfx);
            ResNode n = res == "wood"
                ? w.AddResource("tree", "wood", 26, 6, 200, new ResOpts { Radius = 1.5, MaxWorkers = 4, Cells = w.Nav.MarkCircle(26, 6, 1.6) })
                : w.AddResource("stone", "stone", 26, 6, 2000, new ResOpts { Radius = 2.5, MaxWorkers = 8, Cells = w.Nav.MarkCircle(26, 6, 2) });
            w.Order(u, new Order("gather") { Target = n });
            var walks = new HashSet<string>();
            var seen = new List<string>(); int serial = u.Anim.Serial;
            for (int i = 0; i < (int)(60 / W_.STEP); i++)
            {
                w.Step();
                if (u.Anim.Serial == serial) continue;
                serial = u.Anim.Serial;
                var k = $"{u.Anim.CurName}#{u.Anim.Phase}";
                seen.Add(k);
                if (u.Anim.CurName!.Contains("walk")) walks.Add((u.Carry != null ? u.Carry.Res : "empty") + ":" + u.Anim.CurName!.Split('#')[0]);
                if (w.Events.Any(e => e.Type == "delivered")) break;
            }
            string work = res == "wood" ? "hacking_dirt" : "hacking_stone";
            t.Log($"{res}: {string.Join(" ", seen)}");
            t.Log($"{res} walks: {string.Join(" ", walks)}");
            if (res == "wood")
            {
                int c0 = seen.IndexOf("chop_tree#s"), ce = seen.IndexOf("chop_tree#e");
                t.True(c0 >= 0 && ce > c0, "chopping: its start part, then its end part when the tree falls");
                t.Equal(1, seen.Count(x => x == "chop_tree#s"), "the chopping's start part once (not each blow)");
                t.Equal(1, seen.Count(x => x == "chop_tree#l"), "its loop part loops (not restarted each blow)");
                t.True(seen.IndexOf(work + "#s") > ce, "hacking starts after the chopping's end part");
            }
            int s0 = seen.IndexOf(work + "#s"), e0 = seen.IndexOf(work + "#e");
            t.True(s0 >= 0 && e0 > s0, $"{work}: start part, then end part");
            t.Equal(1, seen.Count(x => x == work + "#s"), $"{work}: the start part once a load");
            t.Equal(1, seen.Count(x => x == work + "#l"), $"{work}: the loop part runs on (counted), not restarted");
            var pu = res == "wood" ? "shoulder_pick_up" : "pick_up";
            t.True(seen.IndexOf(pu + "#") > e0, $"{pu} after the end part of {work}");
            t.True(walks.Contains("empty:walk_2") || walks.Any(x => x.StartsWith("empty:walk_")), "walking empty: walk_*");
            string carryWalk = res == "wood" ? "shoulder_walk_" : "carry_walk_";
            t.True(walks.Any(x => x.StartsWith(res + ":" + carryWalk)), $"carrying {res}: {carryWalk}*");
            t.True(!walks.Any(x => x.StartsWith(res + ":walk_")), $"carrying {res}: never the empty walk");
        }
    }

    /// <summary>An idle Hu worker plays its IdleAnims.txt set: every variant its start part once, its loop part counted,
    /// its end part before the next variant (not cut by the next one); after hammering, the hammer's end part first.</summary>
    public static void IdleVariantsAndEndOfWork(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        var u = w.SpawnUnit("hu_worker", p, 8, 8)!;
        if (u.Anim == null || !u.Anim.Has("idle_2#l")) t.Skip("no clips of " + u.Gfx);
        var set = w.Data.IdleSet("hu_worker");
        t.True(set != null && set.Count > 1, "hu_worker has an idle set");
        var seen = Starts(w, u, 90);
        t.Log(string.Join(" ", seen.Take(40)));
        var variants = seen.Select(s => s.Split('#')[0]).Distinct().ToList();
        t.True(variants.Count >= 3, "several idle variants in 90 s: " + string.Join(",", variants));
        t.True(variants.All(v => set!.Any(e => e.Anim.Equals(v, StringComparison.OrdinalIgnoreCase)) || v == u.IdleAnim), "only clips of the set");
        // a variant with loop marks: s, l, e in that order before the next variant starts
        for (int i = 0; i < seen.Count; i++)
        {
            var (clip, ph) = (seen[i].Split('#')[0], seen[i].Split('#')[1]);
            if (ph != "s" || !u.Anim.Has(clip + "#e")) continue;
            int j = i + 1;
            while (j < seen.Count && seen[j] == clip + "#l") j++;
            if (j >= seen.Count) break;
            t.Equal(clip + "#e", seen[j], $"{clip}: its end part before the next one (#{i})");
        }
        // hammering ends with the hammer's end part, then the idle clips
        var b = w.PlaceBuilding("hu_warehouse", p, 30, 0, 0, false);
        if (b == null) return;
        w.Order(u, new Order("build") { Target = b });
        var seen2 = Starts(w, u, 200, () => b.Built && u.Task.Type == "idle" && u.Anim.CurName != "hammer");
        seen2.AddRange(Starts(w, u, 3));
        t.Log(string.Join(" ", seen2));
        int h = seen2.LastIndexOf("hammer#l");
        t.True(h >= 0 && h + 1 < seen2.Count && seen2[h + 1] == "hammer#e", "the hammer's end part when the house is done");
    }

    /// <summary>The death clip of a character (CCharacter.CreateCorpse): a blow from behind "dying", from the side
    /// "die_simple", from the front "hit_fw" (data converted before the toolkit kept it: "dying"), the player's Kill
    /// (Del: DiePerHarakiri) "harakiri".</summary>
    public static void DeathClipBySide(TestContext t)
    {
        var (w, p, e) = Sys2.World(t, Sys2.Base, "Hu");
        Unit Victim(double x)
        {
            var v = w.SpawnUnit("hu_warrior", p, x, 0)!;
            if (v.Anim == null || !v.Anim.Has("die_simple")) t.Skip("no clips of " + v.Gfx);
            v.Heading = 0;            // facing -z
            return v;
        }
        string DeathBy(double ax, double az)
        {
            var v = Victim(0);
            var a = w.SpawnUnit("aje_warrior", e, ax, az)!;
            v.Hp = 1;
            w.ProvideDmg(5, a, v, false, 0, 0, null);
            t.True(!v.Alive, "killed");
            var c = v.Anim!.CurName!;
            a.Alive = false; w.Kill(a, null);
            return c;
        }
        t.Equal("dying", DeathBy(0, 3), "a blow from behind (+z): dying");
        t.Equal("die_simple", DeathBy(3, 0), "from the side: die_simple");
        bool fw = Victim(-10).Anim!.Has("hit_fw");
        if (!fw) t.Log("the data has no hit_fw (converted before the toolkit kept it): the front plays dying");
        t.Equal(fw ? "hit_fw" : "dying", DeathBy(0, -3), "from the front (-z): hit_fw");
        var s = Victim(10);
        new PwRemake.Sim.Commands.KillCommand { Player = p.Id, Entities = new[] { s.Id } }.Apply(w);
        t.Equal("harakiri", s.Anim!.CurName, "Del: harakiri");
    }

    /// <summary>
    /// The snare trap (Building.usl CSnareTrap, B:4945-5103; character.usl:2300-2306): a character caught plays
    /// all_snar_trap_catching once, then all_snar_trap_hanging looping; dead in the trap snare_trap_dying; let go (the trap
    /// destroyed) all_snar_trap_end once, then it goes on.
    /// </summary>
    public static void SnareTrapVictimClips(TestContext t)
    {
        var (w, p, e) = Sys2.World(t, Sys2.Base, "Hu");
        var probe = w.SpawnUnit("hu_warrior", p, -40, 0)!;
        if (probe.Anim == null || !probe.Anim.Has("die_simple")) t.Skip("no clips of " + probe.Gfx);
        if (!probe.Anim.Has("all_snar_trap_catching")) t.Skip("the data has no snare clips (converted before the toolkit kept them; re-run Prepare)");
        Unit Caught(double x)
        {
            var trap = w.PlaceBuilding("ninigi_snare_trap", e, x, 0);
            t.True(trap != null && trap.Built, "a snare trap at " + x);
            var v = w.SpawnUnit("hu_warrior", p, x + 0.5, 0)!;
            var seen = Starts(w, v, 3, () => v.Anim!.CurName == "all_snar_trap_catching");
            t.Equal("all_snar_trap_catching", v.Anim!.CurName, "caught: all_snar_trap_catching (" + string.Join(" ", seen) + ")");
            t.True(v.Active("trapped") && v.St.TrapWhy == "snare", "held by the snare");
            return v;
        }
        var a = Caught(0);
        var b = w.Buildings.First(x => x.Alive && x.Held == a);
        double len = a.Anim!.Duration("all_snar_trap_catching") + (b.Anim?.Duration("catching") ?? 0) + 0.5;
        Starts(w, a, len, () => a.Anim!.CurName == "all_snar_trap_hanging");
        t.Equal("all_snar_trap_hanging", a.Anim!.CurName, "then hanging");
        a.Hp = 0.5;
        Starts(w, a, 1, () => !a.Alive);
        t.True(!a.Alive, "dead in the trap (3 damage a second)");
        t.Equal("snare_trap_dying", a.Anim!.CurName, "dead in the trap: snare_trap_dying");

        var c = Caught(20);
        var tc = w.Buildings.First(x => x.Alive && x.Held == c);
        w.Kill(tc, null);
        t.Equal("all_snar_trap_end", c.Anim!.CurName, "let go: all_snar_trap_end");
        t.True(c.BusyAnim, "busy while it gets up");
        Starts(w, c, c.Anim.Duration("all_snar_trap_end") + 0.5, () => !c.BusyAnim);
        t.True(!c.BusyAnim && c.WeaponsVisible, "free again, its weapons shown");
    }

    /// <summary>The Hu harbour's crane (CHarbour.SetReady / StartWorkAnim / StopWorkAnim, B:5180-5190, 5300-5325):
    /// hu_harbour_crane at Cr_3 of a finished Hu harbour, looping "work" while the harbour produces, its loop ended
    /// when the work is over.</summary>
    public static void HarbourCraneWorks(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        if (!w.Models.Has("hu_harbour") || w.Models.Clips("hu_harbour_crane").Count == 0) t.Skip("no model file of hu_harbour_crane");
        var h = w.PlaceBuilding("hu_harbour", p, 0, 0)!;
        t.True(h.Built, "a finished harbour");
        h.UpdateTurret();
        var crane = h.Parts.FirstOrDefault(x => x.Gfx == "hu_harbour_crane");
        t.True(crane != null && crane.Link == "Cr_3" && crane.Anim != null, "the crane at Cr_3");
        t.True(crane!.Anim!.CurName != "work", "idle: the crane does not work");
        h.OnWork(w.Time);
        t.Equal("work", crane.Anim.CurName, "producing: work");
        for (int i = 0; i < (int)(2 / W_.STEP); i++) { h.OnWork(w.Time); w.Step(); }
        t.True(crane.Anim.CurName == "work" && crane.Anim.Phase != "e", "still working while it produces (" + crane.Anim + ")");
        h.WorkEnd = true;
        w.Step();
        t.True(crane.Anim.Phase == "e" || crane.Anim.Cur == null || !crane.Anim.Cur.Loop, "the work over: the loop ends (" + crane.Anim + ")");
        var site = w.PlaceBuilding("hu_harbour", p, 60, 0, 0, false)!;
        site.UpdateTurret();
        t.True(site.Parts.All(x => x.Gfx != "hu_harbour_crane"), "no crane on a harbour under construction");
    }

    /// <summary>A field (GetCorn.usl): the grow clip held on the frame of its grow step (not played through), the worker
    /// works in chunks of sowing / scything each with its start and end part.</summary>
    public static void FieldGrowStepAndChunks(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        w.PlaceBuilding(w.Data.Extra.Start["Hu"].Base, p, 0, 0);
        var f = w.PlaceBuilding("hu_corn_field", p, 30, 0);
        if (f == null || f.Anim == null || !f.Anim.Has("grow")) t.Skip("no hu_corn_field clips");
        var u = w.SpawnUnit("hu_worker", p, 10, 5)!;
        if (u.Anim == null || !u.Anim.Has("sowing#l")) t.Skip("no clips of " + u.Gfx);
        w.Order(u, new Order("gather") { Target = f });
        var seen = Starts(w, u, 40);
        t.Log(string.Join(" ", seen));
        t.True(f.GrowStep > 0, "grow steps: " + f.GrowStep);
        t.True(f.Anim!.CurName == "grow" && f.Anim.Cur!.Paused, "the grow clip held");
        t.Near((f.GrowStep - 1) / 25.0, f.Anim.Cur.Time, 1e-9, "on the frame of the step before the last chunk (SetAnim(grow, 0, step))");
        int chunks = seen.Count(x => x == "sowing#s");
        t.True(chunks >= 2 && chunks == seen.Count(x => x == "sowing#e") || chunks - 1 == seen.Count(x => x == "sowing#e"), $"chunks of sowing, each with its start and end part ({chunks})");
    }
}
