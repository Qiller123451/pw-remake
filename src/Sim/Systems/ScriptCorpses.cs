// The corpses that a unit's script makes itself (MIRAGE master Mirage.usl, Vehicle.usl; the base installation's
// script folder lacks TransportObj/ and Vehicle.usl, the classes are MIRAGE's copies of the base ones where they
// exist): a CUniversalCorpse object of its own class, with its own model, clip and way of going - drawn by the view as
// the unit's Wreck (EntityViews.Wrecks), like the ships' and vehicles' "<gfx>_dest" wrecks (Combat.cs ShipWreck,
// VehicleWreck). Every one waits its TIMER_DELETE time, then MoveAction's it somewhere at a speed (m/s) and is deleted
// when it arrives (OnActionEnd); the falling ones first come down to the ground, wait 5 s (TIMERID_SINK) and then sink.
//   CSeasScorpion.CreateCorpse -> seas_scorpion_corpse (CScorpionCorpse, Mirage.usl 15904): model scorpion_dest
//     ("scorpion" + "_dest") playing "destroy" (PlayAnim: AnimAction), after 10 s held at frame 249
//     (TIMERID_STILLSTAND: SetAnim("destroy", 0, 249) - AM_Still), at 14 s it sinks to height 0 at 1 m/s.
//     (With MIRAGE's unit resources a stone corpse instead - the remake has no unit resources.)
//   CSiegeTower.CreateCorpse (Vehicle.usl 1700) -> siegetower_corpse (CSiegeTowerCorpse): the tower's own model
//     ninigi_siegetower without a clip, burning (AddFX "fire_corpses" = fire_molotov at its origin), after 1 s it sinks
//     to height 0 at 5 m/s.
//   CZeppelin.Delete (Mirage.usl 6970) -> zeppelin_corpse (CZeppelinCorpse): model flyingtrader, burning at "Dri2"; it
//     falls to the ground's height - 12.26 m at 15.5 m/s, 5 s, then sinks to 0 at 1 m/s.
//   CNinigiCargoLifter / CNinigiDirigible.CreateCorpse -> cargolifter_corpse / dirigible_corpse (CCargolifterCorpse):
//     its model, burning at "Dri1"; it falls to the ground (or the sea level, the higher) + 0 / + 3 m at 15 m/s, 5 s,
//     then sinks to 0 at 1 m/s.
// Before: the Scorpion left the pirate ship's wreck (VehicleWreck took its carrier model pirate_boss + "_dest"), the
// siege tower played its own dying, and a dead flying zeppelin, cargolifter or dirigible stayed in the air where it
// died until its corpse sank after 12 s.
// Not ported: the pteranodons' CPteranodonCorpse (Delete, besides CAnimal's own corpse), the SEAS carrier's sinking
// (CCarrierSink: a building, BuildingCorpse or sink at random), the rebuild protocol's wreckage (CWreckageStill), the
// magma's lava (CCustomCorpse), the Crash RPG's pilots (CPilotCorpse).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public sealed partial class Wreck
{
    /// <summary>a script corpse that moves (falls, sinks): the height it starts at, the time after the death it starts
    /// moving (TIMER_DELETE), where it falls to (null: it sinks at once) and how fast, the wait before it sinks
    /// (TIMERID_SINK) and the sinking's speed down to height SinkTo</summary>
    public bool Moves;
    public double Y0, MoveAt = double.PositiveInfinity, FallSpeed, SinkWait = 5, SinkSpeed = 1, SinkTo;
    public double? FallTo;
    /// <summary>the clip held at this time of it from HoldFrom on (SetAnim(clip, 0, frame): AM_Still)</summary>
    public double HoldFrom = double.PositiveInfinity, HoldAt;
    /// <summary>a fire burning on it (AddFX "fire_corpses") at this link of its model ("": its origin)</summary>
    public string? Fire, FireLink;

    /// <summary>its height deadT seconds after the death</summary>
    public double YAt(double deadT)
    {
        if (!Moves || deadT < MoveAt) return Y0;
        double t = deadT - MoveAt, y = Y0;
        if (FallTo is double ft)
        {
            double tf = Math.Abs(ft - Y0) / Math.Max(0.01, FallSpeed);
            if (t < tf) return Y0 + Math.Sign(ft - Y0) * FallSpeed * t;
            y = ft; t -= tf;
            if (t < SinkWait) return y;
            t -= SinkWait;
        }
        return y <= SinkTo ? y : Math.Max(SinkTo, y - SinkSpeed * t);
    }

    /// <summary>the time after the death it arrives at SinkTo (OnActionEnd: Delete)</summary>
    public double EndAt()
    {
        double t = MoveAt, y = Y0;
        if (FallTo is double ft) { t += Math.Abs(ft - Y0) / Math.Max(0.01, FallSpeed) + SinkWait; y = ft; }
        return t + Math.Max(0, y - SinkTo) / Math.Max(0.01, SinkSpeed);
    }
}

public sealed partial class World
{
    /// <summary>how long a sinking corpse stays at most after it began to move (a corpse on a mountain sinks for
    /// minutes, long out of sight)</summary>
    const double CORPSE_SINK_MAX = 60;

    /// <summary>the corpse a unit's script makes itself (ScriptCorpses.cs); false: none, the unit's usual death</summary>
    public bool ScriptCorpse(Unit e)
    {
        Wreck? w = null;
        double ground = Height(e.Pos.X, e.Pos.Z);
        if (Data.IsA(e.Name, "CSeasScorpion"))
        {
            // CScorpionCorpse.Init("scorpion", name, 14, 10): super.Init("scorpion_dest", name, 14)
            w = Corpse(e, Data.ClassGfx("seas_scorpion_corpse") ?? "scorpion_dest", "destroy");
            w.MoveAt = 14; w.HoldFrom = 10; w.HoldAt = 249 / 25.0;
        }
        else if (Data.IsA(e.Name, "CSiegeTower"))
        {
            w = Corpse(e, Data.ClassGfx("siegetower_corpse") ?? e.Gfx, null);
            w.MoveAt = 1; w.SinkSpeed = 5; w.Fire = "fire_molotov"; w.FireLink = "";
        }
        else if (Data.IsA(e.Name, "CZeppelin"))
        {
            w = Corpse(e, Data.ClassGfx("zeppelin_corpse") ?? e.Gfx, null);
            w.MoveAt = 0.0001; w.FallTo = ground - 12.26; w.FallSpeed = 15.5; w.Fire = "fire_molotov"; w.FireLink = "Dri2";
        }
        else if (Data.IsA(e.Name, "CNinigiCargoLifter") || Data.IsA(e.Name, "CNinigiDirigible"))
        {
            bool dirigible = Data.IsA(e.Name, "CNinigiDirigible");
            w = Corpse(e, Data.ClassGfx(dirigible ? "dirigible_corpse" : "cargolifter_corpse") ?? e.Gfx, null);
            w.MoveAt = 0.0001; w.FallTo = Math.Max(ground, WaterLevel ?? double.NegativeInfinity) + (dirigible ? 3.0 : 0.0);
            w.FallSpeed = 15; w.Fire = "fire_molotov"; w.FireLink = "Dri1";
        }
        if (w == null) return false;
        w.Moves = true; w.Y0 = e.Pos.Y;
        w.Until = Math.Min(w.EndAt(), (w.MoveAt == double.PositiveInfinity ? 0 : w.MoveAt) + CORPSE_SINK_MAX);
        e.Wreck = w;
        e.LookChanged();
        return true;
    }

    Wreck Corpse(Unit e, string? gfx, string? clip)
    {
        var tpl = (gfx ?? e.Gfx ?? "").ToLowerInvariant();
        AnimCtl? anim = null;
        if (clip != null && Models.Has(tpl))
        {
            var clips = Models.Clips(tpl);
            if (clips.Count > 0 && (anim = new AnimCtl(clips, null)).Has(clip)) anim.Play(clip, new AnimOpts { Loop = false });
            else anim = null;
        }
        return new Wreck { Gfx = tpl, Anim = anim };
    }

    /// <summary>a dead unit's script corpse: its clip held from HoldFrom on (DeadUnitUpdate)</summary>
    public static void CorpseHold(Wreck w, double deadT)
    {
        if (deadT < w.HoldFrom || w.Anim?.CurName is not string c) return;
        if (w.Anim.Cur is { Paused: true } a && Math.Abs(a.Time - w.HoldAt) < 1e-9) return;
        w.Anim.Still(c, Math.Min(w.HoldAt, w.Anim.Duration(c)));
    }
}
