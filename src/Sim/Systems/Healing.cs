// Healing: healer units (druid, monk, SEAS medic), healing buildings (temples), the hermit's ranged heal.
//
// Reference: HealUnits.usl, Building.usl DoBuildingFunction, FightingObj.usl:3744 (docs/spec/moves.md 4).
//   heal per second = amount * Healing(rel) + Healing(abs) + target max hp * mod / 100
// There is no passive regeneration in ParaWorld.
// A unit with blood infection (MIRAGE contamination: St.T["noHeal"]) can't be healed (FO:11553 HealMe etc.).
//
// Ported from remake/src/game/systems/healing.js (pw-exporter 98c7703).
using System;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public sealed partial class World
{
    const double SUPPORT_NEAR = 12, SUPPORT_FAR = 18;      // m: where a hand-to-hand healer told to attack waits (SupportUpdate)

    public double HealPerSecond(Entity src, double amount, double mod, Entity target)
    {
        var s = src.Stats;
        double rel = s != null ? s.HealModRel : 1, abs = s != null ? s.HealModAbs : 0;
        return amount * rel + abs + target.MaxHp * Or0(mod) * 0.01;
    }
    public double HealPerSecond(Entity src, HealAbility h, Entity target) => HealPerSecond(src, h.Amount, h.Mod, target);

    /// <summary>own and allied characters, animals, vehicles within 1.5 x the healing radius; lowest hit point ratio first</summary>
    public Entity? HealScan(Unit u)
    {
        var h = u.Stats!.Heal;
        if (h == null || u.Owner == null) return null;
        Unit? best = null; double bh = 1;
        UHash.Query(u.Pos.X, u.Pos.Z, h.Radius * 1.5 + 6, o =>
        {
            if (o == u || !o.Alive || o.Inside || o.Owner == null || !u.Owner.IsFriend(o.Owner) || o.Hp >= o.MaxHp || o.Active("noHeal")) return;
            if (u.DistTo(o) > h.Radius * 1.5) return;
            if (o.Stats!.Heal != null && o.Task.Type == "heal") return;     // don't heal busy healers
            double r = o.Hp / o.MaxHp;
            if (r < bh) { bh = r; best = o; }
        });
        return best;
    }

    partial void HealUpdate(Unit u, double dt)
    {
        var h = u.Stats!.Heal; var t = u.Task;
        var tgt = t.Target;
        if (h == null) { u.Task = UnitTask.Idle(); return; }
        if (tgt == null || !tgt.Alive || tgt.Hp >= tgt.MaxHp || tgt.Active("noHeal"))
        {
            tgt = t.Target = HealScan(u);
            if (tgt == null) { StopHealing(u); u.Task = t.Back != null && (t.Back.Target == null || t.Back.Target.Alive) ? t.Back : new UnitTask(t.Hold ? "hold" : "idle"); return; }
        }
        double d = u.DistTo(tgt) - tgt.Radius;
        if (d > h.Radius)
        {
            if (t.Hold || (!t.User && d > h.Radius * 1.5)) { t.Target = null; return; }
            StopHealing(u);
            u.RepathT -= dt;
            if (u.RepathT <= 0 || u.Path.Count == 0) { u.SetPath(tgt.Pos.X, tgt.Pos.Z); u.RepathT = 0.8; }
            double sp = u.Steer(dt, Or(u.RunSpeed, u.Speed), 0.5);
            u.MoveAnim(sp, true);
            return;
        }
        u.ClearPath(); u.Vel = V3.Zero;
        u.Face(tgt.Pos.X, tgt.Pos.Z, dt);
        if (!u.Healing)
        {
            u.Healing = true;
            u.SetVisFlag(16, true);          // VIS_FLAG_CHTR_ACTIVATED: the medic's glowing kit
            var a = u.Anim!.Pick("heal_0");
            if (a != null) u.Anim.Play(a, new AnimOpts { Fade = 0.2 });
        }
        tgt.Hp = Math.Min(tgt.MaxHp, tgt.Hp + HealPerSecond(u, h, tgt) * dt);
        if (tgt is Unit tu) tu.HealedT = Time;
        t.FxT -= dt;
        if (t.FxT <= 0) { t.FxT = 0.5; Emit("healfx", new Args { ["from"] = u, ["to"] = tgt }); }
    }

    /// <summary>a healer that fights hand to hand (the SEAS medic, the druid) told to attack: it goes with the attack but stays
    /// back (SUPPORT_NEAR .. SUPPORT_FAR m from the target) and heals the friends who fight there; then back to its post
    /// behind the target. The user: "they shouldn't try to go into melee range but stay at a distance and heal".</summary>
    public bool MeleeHealer(Unit u) { var w = u.Weapons.Long; return u.Stats!.Heal != null && u.Owner != null && w?.Projectile == null; }

    partial void SupportUpdate(Unit u, double dt)
    {
        var t = u.Task; var e = t.Target;
        if (e == null || !e.Alive || e.Parked || (e.Owner != null && u.Owner != null && !u.Owner.IsEnemy(e.Owner) && !t.Force)) { StopHealing(u); u.Task = UnitTask.Idle(); return; }
        u.ScanT -= dt;
        if (u.ScanT <= 0)
        {
            u.ScanT = 0.4;
            var o = HealScan(u);
            if (o != null) { u.Task = new UnitTask("heal") { Target = o, User = false, Back = t }; u.RepathT = 0; return; }
        }
        var h = u.Stats!.Heal!; double far = Math.Min(SUPPORT_FAR, h.Radius * 0.6), d = u.DistTo(e) - e.Radius;
        if (d > far)
        {
            u.RepathT -= dt;
            if (u.RepathT <= 0 || u.Path.Count == 0)
            {
                // a point on the way, SUPPORT_NEAR m short of the target
                double dx = u.Pos.X - e.Pos.X, dz = u.Pos.Z - e.Pos.Z, l = Hypot(dx, dz); if (l == 0) l = 1;
                double k = (SUPPORT_NEAR + e.Radius) / l;
                u.SetPath(e.Pos.X + dx * k, e.Pos.Z + dz * k); u.RepathT = 0.8;
            }
            double sp = u.Steer(dt, t.User ? Or(u.RunSpeed, u.Speed) : u.Speed, 0.8);
            u.MoveAnim(sp, t.User);
            return;
        }
        u.ClearPath(); u.Vel = V3.Zero;
        u.Face(e.Pos.X, e.Pos.Z, dt);
        u.IdleTick(dt);
    }

    partial void StopHealing(Unit u)
    {
        if (!u.Healing) return;
        u.Healing = false;
        u.SetVisFlag(16, false);
        // HealUnits.usl:358-364: heal_0 is SetSLEAnim(heal_0, 9999, intro, outro); the healing over, AnimActionStopLoop
        // plays its end part
        if (u.Anim?.CurName == "heal_0") u.Anim.StopLoop();
    }

    /// <summary>temples and harbours heal friends in their radius; the hermit heals around himself (ranged_heal)</summary>
    void BuildingHeal(double dt)
    {
        void Heal(Entity src, double radius, double amount, double mod, Func<Unit, bool>? filter)
        {
            UHash.Query(src.Pos.X, src.Pos.Z, radius + 6, o =>
            {
                if (!o.Alive || o.Inside || o.Owner == null || !src.Owner!.IsFriend(o.Owner) || o == src || o.Hp >= o.MaxHp || o.Active("noHeal")) return;
                if (filter != null && !filter(o)) return;
                if (Hypot(o.Pos.X - src.Pos.X, o.Pos.Z - src.Pos.Z) > radius + o.Radius) return;
                o.Hp = Math.Min(o.MaxHp, o.Hp + HealPerSecond(src, amount, mod, o) * dt);
                o.HealedT = Time;
                if (Rng.Next() < 0.2) Emit("healfx", new Args { ["from"] = src, ["to"] = o });       // (the glow lasts 2.5 s: feedback.js healGlow)
            });
        }
        for (int i = 0; i < Buildings.Count; i++)
        {
            var b = Buildings[i];
            if (!b.Alive || !b.Built || b.Owner == null || b.Stats!.Heal == null || !(b.Stats.Heal.Radius != 0 && !double.IsNaN(b.Stats.Heal.Radius))) continue;
            bool ships = b.Def!.Script == "CHarbour" || b.Def.Script == "CSwimmingHarbour";
            var h = b.Stats.Heal;
            Heal(b, h.Radius, h.Amount, h.Mod, o => ships ? o.Cls == "SHIP" : o.Cls != "SHIP");
        }
        for (int i = 0; i < Units.Count; i++)
        {
            var u = Units[i];
            if (!u.Alive || u.Owner == null || u.Stats == null || !u.Stats.Abilities.TryGetValue("ranged_heal", out var rh)) continue;
            double r = rh.Num("radius", 0);
            if (r > 0) Heal(u, r, rh.Num("amount", 0), rh.Num("mod", 0), null);
        }
    }
}
