// The ranges the range markers of a selected object show (MIRAGE Scripts/Game/mgr/RangeMarkerMgr.usl CRMObject
// SetRangeMarker; the view draws them: game/src/View/Entities/RangeMarkers.cs). The original reads attributes the
// server sets on every fighting object:
//   attack   "range_marker"        FightingObj.usl SetAttackRange -> GetAttackRangeForMarker (MIRAGE 9244): the attack
//                                  range + 2 (characters) / the radius (others) / 0 (projectile weapons) + the
//                                  temporary range bonus; 0 when it cannot fight            -> World.AttackRangeOf
//                                  (a shooting building: the reach its TowerUpdate fires at, World.TowerRange)
//   alarm    "alarm_range_marker"  SetAlarmRange (9377): clamp(attack range + 8, 32, FOW); 0 when it cannot fight
//                                                                                          -> World.AlarmRange
//   healing  "HealingMarker"       the ability heal's radius (FightingObj 10501, Hero.usl, Ship.usl)
//   aura     "RangeMarker"         "Personal/<radius>;" of its personal region (an aura: CreatePersonalRegion -
//                                  Mirage.usl RangedEffect, the heroes, drums, pennant ...) -> the auras of Effects.cs
//   fog      "FowRange"            SetFOWRange (9288): the sight radius with its bonus (1 when blinded) -> Entity.Fow
// Only for the object types the original marks: CHTR, ANML, VHCL, SHIP, BLDG.
using System;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>The five marker types of CRangeMarkerMgr, in its order.</summary>
public enum RangeMarkerType { Attack, Alarm, Fow, Healing, Aura }

/// <summary>The ranges of one object's markers (0 = no such marker).</summary>
public readonly record struct MarkerRanges(double Attack, double Alarm, double Fow, double Healing, double Aura)
{
    public double this[RangeMarkerType t] => t switch
    {
        RangeMarkerType.Attack => Attack, RangeMarkerType.Alarm => Alarm, RangeMarkerType.Fow => Fow,
        RangeMarkerType.Healing => Healing, _ => Aura,
    };
}

public sealed partial class World
{
    /// <summary>the object types CRMObject.SetRangeMarker draws for</summary>
    public static bool HasRangeMarkers(Entity e) => e.Cls is "CHTR" or "ANML" or "VHCL" or "SHIP" or "BLDG";

    /// <summary>the ranges of the markers of a selected object (RangeMarkerMgr.usl SetRangeMarker)</summary>
    public MarkerRanges MarkerRangesOf(Entity e)
    {
        if (!e.Alive || !HasRangeMarkers(e)) return default;
        if (e is Building b0 && !b0.Built) return default;
        // IsAbleToFight: a weapon, not disarmed (the transports, carts ... that never pick a fight have one too)
        bool fights = e.Weapons.Long != null && !e.Disarmed && !(e is Unit cu && cu.CannotFight && !cu.IsWorker);
        double atk = 0, alarm = 0;
        if (fights)
        {
            if (e is Building b && TowerRange(b) is var (r, _)) atk = r;
            else atk = AttackRangeOf(e, e.Cs(e.Weapons.Long));
            alarm = AlarmRange(e);
        }
        double fow = e.Fow * SightScale;
        double heal = e.Stats?.Heal is { } h && h.Radius > 0 ? h.Radius : 0;
        return new MarkerRanges(atk > 0 ? atk : 0, alarm > 0 ? alarm : 0, fow > 0 ? fow : 0, heal, AuraRadius(e));
    }

    /// <summary>the radius of the object's aura now (0: none): a hero's RangeEffect (Hero.usl; radius 20 when its
    /// ability has none) or a row of the aura table (Effects.cs AURAS) that is on</summary>
    public double AuraRadius(Entity e)
    {
        if (e.Owner == null) return 0;
        if (e is Unit && e.Stats != null && e.Stats.Abilities.TryGetValue("RangeEffect", out var ab) && ab.On("enabled"))
        {
            double r = ab.Num("radius", 0);
            return r == 0 || double.IsNaN(r) ? 20 : r;
        }
        var k = Data.Kind(e.Name);
        if (!AURAS.TryGetValue(k, out var A) && !AURAS.TryGetValue(k.ToLowerInvariant(), out A)) return 0;
        if (!A.When(e, this)) return 0;
        double ra = A.Radius(e);
        return ra > 0 ? ra : 0;
    }
}
