// The corpses a unit's script makes itself (src/Sim/Systems/ScriptCorpses.cs; docs/SIMULATION.md "Attack clips of
// vehicles and big units" / "Script corpses"): MIRAGE's Scorpion (CScorpionCorpse), the siege tower
// (CSiegeTowerCorpse), the zeppelins (CZeppelinCorpse), the Dragon Clan's cargolifter and dirigible
// (CCargolifterCorpse) - their model, clip, fire, fall, sinking and the time they go.
using System;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class ScriptCorpsesTests
{
    const double GROUND = 20;

    static Unit Kill(TestContext t, W_ w, Player p, string cls, double lift = 0)
    {
        var u = w.SpawnUnit(cls, p, 0, 0, 1) ?? throw new Exception("no " + cls);
        if (lift > 0) u.LiftH = lift;
        w.Step();
        w.Kill(u, null);
        t.True(!u.Alive, cls + " dead");
        return u;
    }

    static void RunUntilRemoved(W_ w, Unit u, double max)
    {
        for (int i = 0; i < (int)(max / W_.STEP) && !u.Removed; i++) w.Step();
    }

    /// <summary>MIRAGE's Scorpion leaves scorpion_dest playing "destroy" (not the pirate ship's wreck of its carrier
    /// model), held at frame 249 from 10 s, sinking from 14 s at 1 m/s to height 0; gone when it got there</summary>
    public static void ScorpionLeavesItsWreck(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Mirage, "SEAS", "Hu", (x, z) => GROUND);
        var u = Kill(t, w, p, "seas_scorpion");
        var wr = u.Wreck;
        t.True(wr != null, "a wreck");
        t.Equal("scorpion_dest", wr!.Gfx, "the corpse's model (seas_scorpion_corpse: scorpion + _dest), not pirate_boss_dest");
        t.True(wr.Moves && wr.FallTo == null, "it sinks, no fall");
        t.Near(GROUND, wr.Y0, 1e-6, "where it died");
        t.Near(14 + GROUND, wr.Until, 1e-6, "TIMER_DELETE 14 s, then 20 m at 1 m/s");
        t.Near(GROUND, wr.YAt(13.9), 1e-9, "still standing at 13.9 s");
        t.Near(GROUND - 5, wr.YAt(19), 1e-9, "5 m sunk 5 s later");
        if (wr.Anim != null)
        {
            t.Equal("destroy", wr.Anim.CurName, "plays destroy");
            Sys2.Run(w, 10.2);
            t.True(wr.Anim.Cur!.Paused && Math.Abs(wr.Anim.Cur.Time - Math.Min(249 / 25.0, wr.Anim.Duration("destroy"))) < 1e-6, "held at frame 249 from 10 s (AM_Still)");
        }
        else t.Log("(no model file of scorpion_dest here: its clip not checked)");
        RunUntilRemoved(w, u, 60);
        t.True(u.Removed && u.DeadT >= 14 + GROUND - 0.1 && u.DeadT <= 14 + GROUND + 0.2, $"gone when sunk ({u.DeadT:0.0} s)");
    }

    /// <summary>the siege tower: its own model, burning, sinks from 1 s at 5 m/s (base and MIRAGE)</summary>
    public static void SiegeTowerSinksBurning(TestContext t)
    {
        foreach (var data in new[] { Sys2.Base, Sys2.Mirage })
        {
            var (w, p, _) = Sys2.World(t, data, "Ninigi", "Hu", (x, z) => GROUND);
            var u = Kill(t, w, p, "ninigi_siegetower");
            var wr = u.Wreck!;
            t.True(wr != null && wr.Gfx == "ninigi_siegetower", "siegetower_corpse: the tower's model");
            t.Equal("fire_molotov", wr.Fire, "fire_corpses burns on it");
            t.True(wr.Anim == null, "no clip");
            t.Near(1 + GROUND / 5, wr.Until, 1e-6, "1 s, then 20 m at 5 m/s");
        }
    }

    /// <summary>a zeppelin killed in the air falls to the ground - 12.26 m at 15.5 m/s, waits 5 s and sinks to 0 at
    /// 1 m/s; the cargolifter to the ground, the dirigible 3 m above it, at 15 m/s</summary>
    public static void AirshipsFallAndSink(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Mirage, "SEAS", "Hu", (x, z) => GROUND);
        var z = Kill(t, w, p, "seas_zeppelin", 15);
        var zw = z.Wreck!;
        t.True(zw != null && zw.Gfx == "flyingtrader" && zw.Fire == "fire_molotov" && zw.FireLink == "Dri2", "zeppelin_corpse: flyingtrader, burning at Dri2");
        t.True(zw.Y0 > GROUND + 10, $"it starts in the air ({zw.Y0:0.0})");
        t.Near(GROUND - 12.26, zw.FallTo!.Value, 1e-9, "falls to the ground - 12.26 m");
        double fall = (zw.Y0 - (GROUND - 12.26)) / 15.5;
        t.Near(zw.Y0 - 15.5, zw.YAt(1.0001), 1e-6, "15.5 m in the first second");
        t.Near(GROUND - 12.26, zw.YAt(fall + 4), 1e-6, "lies there for 5 s");
        t.Near(0.0001 + fall + 5 + (GROUND - 12.26), zw.Until, 1e-6, "then sinks to 0 at 1 m/s");
        foreach (var (cls, fix) in new[] { ("ninigi_cargolifter", 0.0), ("ninigi_dirigible", 3.0) })
        {
            var (w2, p2, _) = Sys2.World(t, Sys2.Mirage, "Ninigi", "Hu", (x, zz) => GROUND);
            var c = Kill(t, w2, p2, cls, 10);
            var cw = c.Wreck!;
            t.True(cw != null && cw.Gfx == cls && cw.FireLink == "Dri1", cls + ": its model, burning at Dri1");
            t.Near(GROUND + fix, cw.FallTo!.Value, 1e-9, cls + ": falls to the ground + " + fix);
            t.Near(Math.Max(cw.Y0 - 15, cw.FallTo.Value), cw.YAt(1.0001), 1e-3, cls + ": at 15 m/s");
        }
        // a unit without such a class keeps its usual death
        var (w3, p3, _) = Sys2.World(t, Sys2.Mirage, "SEAS", "Hu");
        var m = Kill(t, w3, p3, "seas_mobile_suit");
        t.True(m.Wreck == null || !m.Wreck.Moves, "a mobile suit: no script corpse");
    }

    /// <summary>the hold of a script corpse's clip without game data: "destroy" runs, from HoldFrom on it is held at
    /// HoldAt (the Scorpion's frame 249 at 10 s)</summary>
    public static void CorpseClipHeldFromItsTime(TestContext t)
    {
        var cs = new ClipSet();
        cs.Add("destroy", 12);
        var a = new AnimCtl(cs, null);
        a.Play("destroy", new AnimOpts { Loop = false });
        var wr = new Wreck { Anim = a, HoldFrom = 10, HoldAt = 249 / 25.0 };
        double tt = 0;
        while (tt < 9.9) { a.Update(0.05); tt += 0.05; W_.CorpseHold(wr, tt); }
        t.True(!a.Cur!.Paused && a.Cur.Time > 9.8, "running before 10 s");
        for (int i = 0; i < 40; i++) { a.Update(0.05); tt += 0.05; W_.CorpseHold(wr, tt); }
        t.True(a.Cur!.Paused && Math.Abs(a.Cur.Time - 9.96) < 1e-9, $"held at frame 249 ({a.Cur.Time:0.00})");
    }
}
