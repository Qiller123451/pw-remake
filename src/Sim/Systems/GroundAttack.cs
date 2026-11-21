// Attack the ground: an area weapon (a catapult, the Diplodocus' tail, the fire cannons ...) fires at a spot instead of
// at an object, again and again until the next order.
//
// The original (SecondaryInputController.usl HandleMsg, "Attack" command): a click of the Attack button's mode on open
// ground sends every selected object whose "hitrange" (splash radius) is above 0 a Fight command with no target and the
// pick point, "Scape"; the others walk there, each to the point shifted by up to sqrt(n) metres. While the mode is
// armed with an area weapon in the selection a range marker of the selection's largest splash radius follows the mouse
// (AddRangeMarker(m_fAreaRange)), and the cursor reads attack_area_weapon(_invalid inside the minimum range)(_walk out
// of reach) (GetCursor). The fighter walks into range of the spot (not into its minimum range), turns to it and fires
// at the cadence of its normal attacks; the shot lands on the spot (jittered as every area shot: Launch).
using System;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public partial class World
{
    /// <summary>can the unit fire at the ground: its primary weapon does area damage (hitrange &gt; 0) and is no cone
    /// (the flamethrowers' penetration)</summary>
    public bool CanAttackGround(Unit u)
    {
        var w = u.Weapons.Long;
        return w != null && !w.Penetration && u.Cs(w) is { } cs && cs.Splash > 0 && cs.Dmg > 0 && !u.Disarmed;
    }

    /// <summary>a spot's reach for an attack on the ground: the farthest and the nearest distance (centre to the spot)
    /// it fires from (as RangeZoneOf for a target without a body)</summary>
    public (double Max, double Min) GroundReach(Unit u)
    {
        var w = u.Weapons.Long; var cs = u.Cs(w);
        if (w == null || cs == null) return (0, 0);
        bool proj = w.Projectile != null;
        double atk = AttackRangeOf(u, cs);
        if (proj) atk += u.Radius * 0.5;
        else { atk += u.Radius + 2; if (atk < 4) atk = 4; }
        double min = cs.MinRange > 0 ? cs.MinRange + u.Radius : 0;
        return (atk, min);
    }

    void GroundAttackUpdate(Unit u, double dt)
    {
        var t = u.Task;
        if (!CanAttackGround(u) || u.Active("peace")) { EndAttack(u); return; }
        if (u.Active("trapped")) { u.Vel = V3.Zero; return; }
        var w = u.Weapons.Long!; var cs = u.Cs(w)!;
        bool proj = w.Projectile != null;
        double d = Hypot(t.X - u.Pos.X, t.Z - u.Pos.Z);
        var (max, min) = GroundReach(u);
        bool far = d > max, near = min > 0 && d <= min;
        if (far || near)
        {
            // a turret that can't move waits for the next order
            if (u.Stationary || !u.CanWalk) { u.Vel = V3.Zero; EndAttack(u); return; }
            u.RepathT -= dt;
            if (u.RepathT <= 0 || u.Path.Count == 0)
            {
                // to a place in reach on the line from the spot to the unit (away from it when too near)
                double want = far ? Math.Max(min + 1, max - 2) : Math.Min(max - 0.5, min + 2);
                double ux = u.Pos.X - t.X, uz = u.Pos.Z - t.Z;
                if (d < 0.01) { ux = SMath.Sin(u.Heading); uz = SMath.Cos(u.Heading); d = 1; }
                u.SetPath(t.X + ux / d * want, t.Z + uz / d * want);
                u.RepathT = 1.2;
                if (u.Path.Count == 0) { if (++t.Fails > 3) { EndAttack(u); return; } }
                else t.Fails = 0;
            }
            double sp = u.Steer(dt, u.RunSpeed, 0.3);
            u.MoveAnim(sp, u.Wild);
            u.BusyAnim = false;
            return;
        }
        // in reach: stand, turn to the spot, fire at the weapon's cadence
        u.ClearPath();
        u.Vel = V3.Zero;
        u.UseWeapon(w);
        var dep = u.Comp?.Dep != null ? u.Deploy : null;
        double off;
        if (dep != null && dep.State != "attack")
        {
            if (dep.State == "walk" || dep.State == "up") { if (dep.State == "up" && u.Comp!.HoldMove(Time)) return; if (u.Face(t.X, t.Z, dt, 7) > 0.3) return; }
            if (!u.Comp!.DeployReady(Time)) return;
        }
        if (dep != null) off = u.Comp!.AimGun(t.X, t.Z, dt);
        else
        {
            off = u.Face(t.X, t.Z, dt, 7);
            if (u.Comp?.Top != null) off = u.Comp.Aim(t.X, t.Z, dt);
        }
        double dur = AttackDuration(u, cs) + u.ShotOvertime;
        if (u.Pending != null || Time - u.LastHitDone <= dur || off >= (proj ? Math.PI / 4 : Math.PI)) return;
        u.LastHitDone = Time;
        var anims = w.Anims.Count > 0 ? w.Anims : DefaultAnims;
        var plain = anims.Where(a => !(a.Combo != 0 && !double.IsNaN(a.Combo))).ToList();
        int pi = (int)Math.Floor(Rng.Next() * plain.Count);
        var pick = pi < plain.Count ? plain[pi] : anims[0];
        u.FightAnim = pick.Name;
        var clip = dep != null ? null : u.Anim!.Pick(pick.Name, "attack_front", "attack_1", "res_strike_3", "nat_strike_0");
        bool gunFires = dep == null && proj && u.Comp?.Gun != null;
        if (clip != null && proj && !Compose.CarrierStrikes(true, Compose.IsAnimal(u.Cls, Models.Get(u.Gfx)?.Arch), gunFires, u.Comp?.Rider?.Attack != null, Data.IsStrikeOf(u.Name, clip)))
        {
            clip = null;
            if (!u.BusyAnim) u.Anim!.Play(u.FightStand() ?? u.IdleAnim);
        }
        if (dep != null) u.Comp!.Fired();
        if (gunFires) u.Comp!.Fire();
        bool fightPos = clip != null && FightPosition(u, w, clip);
        if (FirstShotUnit(u))
        {
            u.ShotOvertime = fightPos && !(u.FightPos && u.Anim!.Phase == "l") ? FirstShot.Of(u.Name) : 0;
            dur = AttackDuration(u, cs) + u.ShotOvertime;
        }
        if (fightPos)
        {
            bool intro = !(u.FightPos && u.Anim!.Phase == "l");
            double len = u.Anim!.Duration(clip + "#l") + (intro ? u.Anim.Duration(clip + "#s") : 0);
            u.BusyAnim = true;
            u.Anim.PlaySle(clip!, intro, len > dur ? len / dur : 1, new AnimDone("busy"));
            u.FightPos = true;
        }
        else if (clip != null)
        {
            double len = u.Anim!.Duration(clip);
            u.BusyAnim = true;
            u.FightPos = false;
            u.Anim.Play(clip, new AnimOpts { Loop = false, Restart = true, Fade = 0.08, Ts = len > dur ? len / dur : 1, OnDone = new AnimDone("busy") });
        }
        double delay = proj ? Or(pick.ShootDelay, 0.3) + u.ShotOvertime : Or(pick.Delay, 0.4);
        u.Pending = new PendingShot { T = Math.Min(delay, dur * 0.9), W = w, Ground = true, At = new V3(t.X, Height(t.X, t.Z), t.Z) };
    }

    /// <summary>the shot of an attack on the ground (the delayed part, as Fire): a projectile to the spot, or the blow's
    /// area damage there</summary>
    public void FireAt(Unit u, Weapon? w, V3 at)
    {
        if (!u.Alive || w == null || u.Cs(w) is not { } cs) return;
        if (u.St.Camo.Has("disg")) { u.St.Camo.Delete("disg"); u.DisgT = Time + 10; }
        if (w.Projectile == null)
        {
            AreaDamage(u, at, cs.Splash, cs.Dmg, cs.EndDmg, new AreaOpts { Size = w.Size });
            Emit("impact", new Args { ["pos"] = at, ["weapon"] = w, ["splash"] = true });
            return;
        }
        var from = u.Pos;
        from.Y += Math.Min(u.Height * 0.7, 2.5);
        if (Muzzle(u) is V3 m) from = m;
        var aim = at;
        if (w.Jitter != 0)
        {
            double L = (Math.Floor(Rng.Next() * Math.Max(1, w.Jitter * 20)) / 10) - w.Jitter, th = Rng.Next() * 3.14;
            aim.X += SMath.Cos(th) * L; aim.Z += SMath.Sin(th) * L; aim.Y = Height(aim.X, aim.Z);
        }
        var p = new Projectile(this, from, null, w, u, aim);
        p.Snap = new ProjectileSnap { Dmg = cs.Dmg, EndDmg = cs.EndDmg, Splash = cs.Splash, Ap = cs.Ap, Size = w.Size };
        Projectiles.Add(p);
        Emit("shoot", new Args { ["attacker"] = u, ["weapon"] = w, ["pos"] = from });
    }
}
