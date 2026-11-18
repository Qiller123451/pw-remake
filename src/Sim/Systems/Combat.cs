// Combat: target choice, weapon ranges, the attack loop, the original damage formula, projectiles, splash,
// penetration, poison, knockback, towers and bunkers, death and skulls.
//
// Reference: docs/spec/combat.md (FightingObj.usl TakeDmg / ProvideDmg / Damage, Fight.usl, WeaponMgr.usl, Product.usl).
// All functions are World methods; `u` = attacker, `v` = victim.
//
// Damage in one line (TakeDmg):
//   raw = factor * attackBoni(dmg) * (1 + AttackBonus[victim type]/100)
//   dmg = raw * (1 - clamp(protection% / 100, 0, 0.99))        protection = armour - armour piercing (+ buffs)
//   hit points lost = max(ceil(dmg), 1)
//
// Ported from remake/src/game/systems/combat.js (pw-exporter 98c7703); AttackAllowed from game/scripting.js
// attackAllowed (a stub of the combat port in docs/SIMULATION.md).
//
// C# notes: the JS closures of the animations (onDone) are AnimDone kinds ("busy", "knockGetUp"); the muzzle and
// turret positions come from the model's link positions (the model data has no animated pose: the view draws the
// shot from where the model really holds it - only the flight time depends on it here). gunnerUpdate, which the JS never
// called, runs every step for units with such turrets (UnitUpdate). Melee blows of the attack loop are decided when
// the swing starts and land after its delay (LandBlow; docs/SIMULATION.md "Melee blows").
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>rangeZone's answer: 0 = out of range, 1 = short, 2 = medium, 3 = primary - with the weapon of the zone and
/// the distance to the victim's surface (High: out of reach in height; Los: a wall between; Min: inside the minimum range)</summary>
public struct ZoneHit
{
    public int Zone;
    public Weapon? W;
    public double D;
    public bool High, Los, Min;
}

/// <summary>options of AreaDamage (combat.js areaDamage opts)</summary>
public sealed class AreaOpts
{
    /// <summary>fixed damage (TakeDirectDmg) instead of the attacker's weapon; Ap: its armour piercing</summary>
    public bool Direct;
    public double Ap;
    /// <summary>size class of the blast - smaller characters and animals are thrown (InvokeAreaThrow)</summary>
    public double Size;
    /// <summary>every owner's objects, the attacker's own too (MIRAGE DoAreaDamageForAllAgain: meteor, walking bomb)</summary>
    public bool All;
    public Entity? Skip;
    /// <summary>the player of an attacker that is gone</summary>
    public Player? Owner;
    public Weapon? Weapon;
    public static readonly AreaOpts None = new();
}

/// <summary>A building behaviour (systems/buildings.js) that reacts to hits (TakeDmg: walls drop stones).</summary>
public interface IHitBehaviour { void Hit(Building b, Entity attacker, World w); }
/// <summary>A building behaviour whose weapon is not fired like a tower's (systems/buildings.js noTower).</summary>
public interface INoTowerBehaviour { bool NoTower => true; }

/// <summary>The state of a turret that shoots by itself (combat.js gunnerUpdate: g.wset, g.lvl, g.cool, g.target, g.lookT).</summary>
public sealed class GunnerState
{
    public WeaponSet? WSet;
    public int Lvl;
    public double Cool, LookT;
    public Entity? Target;
}

public sealed partial class World
{
    static readonly Logger LCombat = Log.For("Systems.Combat");

    static double Clamp(double v, double a, double b) => Math.Max(a, Math.Min(b, v));
    // knockback by size-class difference 3..9 (FO:4383): chance and throw distance
    static readonly double[] KNOCK_CHANCE = { 0, 0, 0, 0.02, 0.04, 0.06, 0.08, 0.10, 0.20, 0.30 };
    static readonly double[] KNOCK_DIST = { 0, 0, 0, 1, 1.5, 2, 2.5, 3, 4, 5 };
    static readonly double[] SPLASH_DIST = { 0, 0, 0, 2, 3.5, 4, 5.5, 6, 7, 8 };
    const double POISON_TICK = 3.0;
    // target type rank (SortEnemyList): normal attackers prefer units, siege units prefer towers and buildings
    static bool IsTower(Entity e) => e.Kind == "building" && e.Weapons.Long?.Projectile != null;
    // only wall pieces (CWall) are "walls" here: towers, gates and traps count as normal buildings (FO:752-772, FO:4819)
    static bool IsWall(Entity e) => e.Kind == "building" && e.Def?.WallKind == "wall";
    static int TypeRank(Entity u, Entity e)
    {
        int r = e.Kind == "unit" ? 0 : IsTower(e) ? 1 : IsWall(e) ? 3 : 2;
        if (u.Def == null || u.Def.AttackType != 1) return r;
        return r == 0 ? 3 : r - 1;
    }

    // SNFA (ActionFactory.usl:5520-5592): player.animalsNeutral bit 1 = wild animals do not pick this player's objects
    // (Animal.usl:361), bit 2 = the player's units do not attack wild animals on their own. Whoever was attacked fights back.
    static bool WildTruce(Entity u, Entity e)
    {
        if (u.Owner != null && e.Owner == null) return (u.Owner.AnimalsNeutral & 2) != 0 && !u.Attackers.Has(e);
        if (u.Owner == null && e.Owner != null) return (e.Owner.AnimalsNeutral & 1) != 0 && !u.Attackers.Has(e);
        return false;
    }

    static Weapon? Long(Entity e) => e.Weapons.Long;
    static double LenSq2(V3 v) => v.X * v.X + v.Y * v.Y + v.Z * v.Z;
    /// <summary>the time of an entity's last hit (Unit.LastHit, Building.LastHit)</summary>
    static void SetLastHit(Entity e, double t) { if (e is Unit u) u.LastHit = t; else if (e is Building b) b.LastHit = t; }
    /// <summary>when an entity was last hit (JS e.lastHit); -99: never</summary>
    public double LastHitOf(Entity e) => e is Unit u ? u.LastHit : e is Building b ? b.LastHit : -99;

    // ------------------------------------------------------------------ ranges (FightingObj.IsInCombatRange / GetAttackRange)
    /// <summary>distance from u's centre to v's surface</summary>
    public double Reach(Entity u, Entity v)
    {
        if (v is Building b) return Math.Max(0, b.SurfDist(u.Pos.X, u.Pos.Z));
        return Math.Max(0, Hypot(v.Pos.X - u.Pos.X, v.Pos.Z - u.Pos.Z) - v.Radius);
    }
    /// <summary>attack range of a weapon: range + 2 (characters) / own radius (others); projectile weapons add nothing</summary>
    public double AttackRangeOf(Entity u, WeaponStats? cs)
    {
        if (cs == null) return 0;
        if (u.Active("storm")) return 2;          // in a blinding storm: melee range only (MIRAGE FO:9222)
        bool proj = cs.W.Projectile != null;
        double r = proj ? 0 : u.Cls == "CHTR" ? 2 : u.Radius;
        return cs.Range + r + RangeBonus(u);
    }
    /// <summary>AlarmRange = clamp(AttackRange + 8, 32, FOW)</summary>
    public double AlarmRange(Entity u)
    {
        var cs = u.Cs(Long(u));
        return Clamp(AttackRangeOf(u, cs) + 8, 32, Math.Max(32, u.Fow != 0 ? u.Fow : 32));
    }
    public double Leash(Entity u) { var cs = u.Cs(Long(u)); return 2 * AlarmRange(u) + AttackRangeOf(u, cs); }
    /// <summary>rangeZone(u, x).zone - 0: out of reach</summary>
    public int RangeZone(Unit u, Entity x) => RangeZoneOf(u, x).Zone;

    /// <summary>which weapon zone the victim is in: 0 = out of range, 1 = short, 2 = medium, 3 = primary</summary>
    public ZoneHit RangeZoneOf(Unit u, Entity v)
    {
        var W = u.Weapons;
        var pcs = u.Cs(W.Long);
        if (pcs == null) return new ZoneHit { Zone = 0 };
        double d = Reach(u, v);
        bool proj = pcs.W.Projectile != null; double inner = u.Radius * 0.5;
        double rt = u.Cls == "CHTR" ? 1.5 : u.Radius;
        double secS = W.Short != null ? W.Short.Range + rt + u.Radius + 2 : -1e9;
        double secM = W.Medium != null ? W.Medium.Range + rt + u.Radius + 2 : -1e9;
        double atk = AttackRangeOf(u, pcs);
        if (proj) atk += inner;
        else if (pcs.W.Penetration && pcs.Range > 5) atk += inner;
        else { atk += u.Radius + 2; if (atk > 0 && atk < 4) atk = 4; }
        // the minimum range is measured between the centres (FO:5058-5070): + the fighter's radius, for projectile weapons
        // - the target's inner radius; d is to the target's surface, so its radius comes off too
        double minR = pcs.MinRange > 0 ? Math.Max(0.01, pcs.MinRange + u.Radius - v.Radius - (proj ? v.Radius * 0.5 : 0)) : 0;
        if (minR > 0) secM = minR;
        // the height (FO:5080-5094): |dz| < min(7, the lower one's height) - else only a primary projectile weapon
        // reaches (zone 3; the original's secondary weapons need the height, a shooter right below a wall shoots
        // with its primary); a projectile gains 0.5 m of range per metre it shoots down (FALLOFFCONE_VAL, FO:930)
        bool pp = proj || (pcs.W.Penetration && pcs.Range > 5);
        double dy = u.Pos.Y - v.Pos.Y; Entity low = dy < 0 ? u : v;
        double lowH = low is Building lb ? (lb.Ht != 0 ? lb.Ht : 7) : low is Unit lu ? (lu.Height != 0 ? lu.Height : 2) : 2;
        bool heightOk = Math.Abs(dy) < Math.Min(7, lowH);
        if (pp && dy > 0) atk += 0.5 * dy;
        if (!heightOk) return pp && d <= atk && !(minR > 0 && d <= minR) ? new ZoneHit { Zone = 3, W = W.Long, D = d } : new ZoneHit { Zone = 0, W = W.Long, D = d, High = true };
        ZoneHit z;
        if (d <= secS) z = new ZoneHit { Zone = 1, W = W.Short, D = d };
        else if (d <= secM) z = minR > 0 && W.Medium == null ? new ZoneHit { Zone = 2, Min = true, W = W.Short, D = d } : new ZoneHit { Zone = 2, W = W.Medium ?? W.Short, D = d };
        else if (d <= atk) z = new ZoneHit { Zone = 3, W = W.Long, D = d };
        else return new ZoneHit { Zone = 0, W = W.Long, D = d };
        // the line of sight (CheckLineOfSightFight, FO:5105-5110): not for a primary projectile in zone 3, nor when
        // either side is a building - a wall (or a cliff of the walk grid) between them stops a melee blow
        if (!(z.Zone == 3 && pp) && v is Unit vu && !u.Inside && !vu.Inside && !FightLos(u, vu)) return new ZoneHit { Zone = 0, W = z.W, D = d, Los = true };
        return z;
    }
    /// <summary>is the ground between two units free (the walk grid's blocked cells; the cells at either unit are not counted)</summary>
    public bool FightLos(Unit u, Unit v)
    {
        if (u.OnWall != null || v.OnWall != null || u.Flying || v.Flying || u.Naval || v.Naval) return true;
        var nav = Nav; double x0 = u.Pos.X, z0 = u.Pos.Z, x1 = v.Pos.X, z1 = v.Pos.Z, d = Hypot(x1 - x0, z1 - z0);
        double a = u.Radius + 0.6, b = d - v.Radius - 0.6;
        for (double s = a; s < b; s += nav.Cell * 0.5)
        {
            double f = s / d; int k = nav.Idx(x0 + (x1 - x0) * f, z0 + (z1 - z0) * f);
            if (nav.Block[k] != 0 && !(WetCell != null && WetCell[k] >= nav.Block[k])) return false;
        }
        return true;
    }
    /// <summary>FollowEnemy: stop at this surface distance when chasing</summary>
    public double ApproachDist(Unit u, Weapon? w)
    {
        var cs = u.Cs(w);
        if (cs == null) return 1;
        if (cs.W.Projectile == null) return cs.W.Range < 1 ? u.Radius * 0.6 : cs.Range;
        return Math.Max(1, AttackRangeOf(u, cs) - 2);
    }
    /// <summary>seconds between attacks (Duration, x1.2 in Lovelace's aura); a computer player's units strike a little
    /// faster at difficulty 8 / 9 (FightingObj.GetAICheatModifier)</summary>
    public double AttackDuration(Entity u, WeaponStats? cs)
    {
        double sh = u.St.Aura.Slowhand; if (sh == 0 || double.IsNaN(sh)) sh = 1;
        double wt = u.Owner?.AiMods != null ? u.Owner.AiMods.WeaponTime : 1; if (wt == 0 || double.IsNaN(wt)) wt = 1;
        return (cs != null ? cs.Dur : 2) * sh * wt;
    }

    // ------------------------------------------------------------------ targets
    /// <summary>a primary projectile weapon: shoots up to and down from a wall (FO:5096)</summary>
    public bool ShootsUp(Entity u)
    {
        var cs = Long(u) != null ? u.Cs(Long(u)) : null;
        return cs != null && (cs.W.Projectile != null || (cs.W.Penetration && cs.Range > 5));
    }
    public bool CanTarget(Entity u, Entity? e)
    {
        if (e == null || !e.Alive || e == u || e.Inside) return false;
        // up on a wall / down from one: only shooters, or fighters with a way up (systems/wallwalk.js)
        if (u is Unit uu && e is Unit eu && (eu.OnWall != null) != (uu.OnWall != null) && !ShootsUp(u) && (uu.OnWall != null || WallReach(uu, e) == null)) return false;
        if (e.Kind != "unit" && e.Kind != "building") return false;
        if (!u.IsEnemy(e)) return false;
        if (e.Untargetable || e.Active("peace")) return false;     // (ceasefire: MIRAGE FO:6586)
        if (e is Unit fu && fu.Flying && !ReachesFlyer(u)) return false;
        if (u.Owner != null && HiddenFrom(e, u.Owner)) return false;
        if (WaterLevel != null && !NavalCanTarget(u, e)) return false;   // water / land reach (naval.md §6)
        return true;
    }
    // ------------------------------------------------------------------ SortEnemyList's minimum-range rule (FO:4890-4924)
    // A fighter whose primary weapon has a minimum range (GetRealMinAttackRange() > 0: ExamineEnemies FO:4737 calls
    // SortEnemyList with p_bSortMinAttackRange) splits its sorted enemies by IsInCombatRangeAttackZone with the zones
    //   S = secondary short range + collision radius + 2, M = minimum range + collision radius + 2 (in place of the
    //   secondary medium range), A = attack range + collision radius + 2 (+ the projectile's fall-off cone):
    // the enemies in the primary zone (3) stay in their order; the others - inside the minimum range (1, 2) and out of
    // reach (0) - are left out, or put behind them when the unit has a secondary short or medium weapon. The log cannon,
    // the fire cannon, the Wehrspinne, the rocket boats never pick an enemy at their feet nor one they would have to walk
    // to; MIRAGE's workers and Kong (a minimum range and a short weapon) take the ones in their primary zone first.
    // Measures as RangeZoneOf (d to the victim's surface; the minimum range between the centres, so the victim's radius
    // comes off), without the height and the line of sight (the engine's zone test of the sort has neither).

    /// <summary>does SortEnemyList sort this unit's enemies by its minimum range (its primary weapon has one)</summary>
    public bool SortsByMinRange(Unit u)
    {
        var cs = u.Weapons.Long != null ? u.Cs(u.Weapons.Long) : null;
        return cs != null && cs.MinRange > 0;
    }
    /// <summary>a secondary short or medium weapon: the enemies out of the primary zone are kept, behind the others</summary>
    public static bool HasSecondary(Unit u) => u.Weapons.Short != null || u.Weapons.Medium != null;
    /// <summary>SortEnemyList's zone test: is v (d = distance to its surface) in u's primary zone - beyond the short zone
    /// and the minimum range, within the attack range</summary>
    public bool InSortZone(Unit u, Entity v, double d)
    {
        var pcs = u.Cs(u.Weapons.Long);
        if (pcs == null) return false;
        double rt = u.Cls == "CHTR" ? 1.5 : u.Radius;                       // GetSecondaryRangeS: + radius (characters 1.5)
        double secS = (u.Weapons.Short?.Range ?? 0) + rt + u.Radius + 2;
        double minR = pcs.MinRange + u.Radius + 2 - v.Radius;
        double atk = AttackRangeOf(u, pcs) + u.Radius + 2;
        if (pcs.W.Projectile != null) { double dy = u.Pos.Y - v.Pos.Y; if (dy > 0) atk += 0.5 * dy; }   // FALLOFFCONE_VAL
        return d > secS && d > minR && d <= atk;
    }
    /// <summary>would SortEnemyList keep v at all for u (a unit without a minimum range keeps everyone)</summary>
    public bool SortKeeps(Unit u, Entity v) => !SortsByMinRange(u) || HasSecondary(u) || InSortZone(u, v, Reach(u, v));

    /// <summary>ExamineEnemies / SortEnemyList: best enemy within r of u (units first, then towers, buildings, walls; a unit
    /// with a minimum range: the minimum-range rule above unless opts.AllZones)</summary>
    public Entity? FindTarget(Entity u, double r, FindOpts? opts = null)
    {
        opts ??= FindOpts.None;
        var mu = !opts.AllZones && u is Unit su && SortsByMinRange(su) ? su : null;
        var st = new FindState
        {
            U = u, R = r, Opts = opts, Cur = (u as Unit)?.Task.Target, Leash = opts.Leash ? Leash(u) : 0,
            Mu = mu, KeepOut = mu != null && HasSecondary(mu), Bv = 1e18,
        };
        // (the candidates into lists kept by the world - a local function given to Query allocated its captures and
        // two delegates on every search; a search inside a search gets lists of its own)
        int depth = _ftDepth++;
        try
        {
            if (_ftUnits.Count <= depth) { _ftUnits.Add(new List<Unit>()); _ftBlds.Add(new List<Building>()); }
            var ul = _ftUnits[depth]; ul.Clear();
            UHash.Collect(u.Pos.X, u.Pos.Z, r + 6, ul);
            foreach (var e in ul) TestTarget(ref st, e);
            ul.Clear();
            var best = st.Best;
            if (best == null || best.Kind != "unit" || st.Bv >= FindState.BEHIND || (u.Def != null && u.Def.AttackType == 1))
            {
                var bl = _ftBlds[depth]; bl.Clear();
                BHash.Collect(u.Pos.X, u.Pos.Z, r + 20, bl);
                foreach (var e in bl) TestTarget(ref st, e);
                bl.Clear();
            }
            return st.Best;
        }
        finally { _ftDepth--; }
    }
    int _ftDepth;
    readonly List<List<Unit>> _ftUnits = new();
    readonly List<List<Building>> _ftBlds = new();
    struct FindState
    {
        public const double BEHIND = 1e6;
        public Entity U; public double R; public FindOpts Opts; public Entity? Cur; public double Leash;
        public Unit? Mu; public bool KeepOut; public Entity? Best; public double Bv;
    }
    void TestTarget(ref FindState s, Entity e)
    {
        var u = s.U; var opts = s.Opts; var cur = s.Cur;
        if (!CanTarget(u, e)) return;
        if (opts.Filter != null && !opts.Filter(e)) return;
        // wild animals: only aggressive ones or those that attacked us
        if (e.Owner == null && e.Def != null && e.Def.Aggressive != 1 && !u.Attackers.Has(e) && !opts.Animals && !(opts.Defend && FightsFriendOf(e, u))) return;
        if (WildTruce(u, e)) return;                 // missions: a player the wildlife leaves alone / who leaves it alone (SNFA)
        if (e.IsNest && e != cur) return;           // a nest of a campaign map (campaign/setup.js) is only attacked on an order
        if (IsWall(e) && e != cur && !opts.Walls) return;
        // the pirates' flagship is fought through its parts on deck (FightingObj.usl:4821-4830: the target list takes
        // its sail, tail and bow instead of the hull)
        if (e.Name == "pirate_boss" && e != cur) return;
        if (u is Unit un && un.Unreach != null && un.Unreach.Count > 0 && un.Unreach.Get(e) > Time) return;    // no way to it a moment ago
        // a human player's units don't see through the fog of war (its own: FowOf - every human of a lockstep game)
        if (e.Kind == "unit" && FowOf(u.Owner) is { } fw && !fw.Visible(e.Pos.X, e.Pos.Z)) return;
        double d = Reach(u, e);
        if (d > s.R) return;
        if (s.Leash != 0 && Hypot(e.Pos.X - u.AnchorX, e.Pos.Z - u.AnchorZ) > s.Leash) return;
        double v = TypeRank(u, e) * 10000 + (e == cur ? 0 : 500) + d;
        if (s.Mu != null && !InSortZone(s.Mu, e, d)) { if (!s.KeepOut) return; v += FindState.BEHIND; }
        if (v < s.Bv) { s.Bv = v; s.Best = e; }
    }
    /// <summary>is e (a wild animal) fighting an object of u's owner or of an ally (FindOpts.Defend)</summary>
    static bool FightsFriendOf(Entity e, Entity u) =>
        e is Unit a && a.Task.Type == "attack" && a.Task.Target is { Alive: true, Owner: { } vo } && u.Owner != null && (vo == u.Owner || u.Owner.IsFriend(vo));
    /// <summary>a target the unit found no way to: left alone for 20 s (findTarget)</summary>
    public void Unreachable(Unit u, Entity e) => (u.Unreach ??= new()).Set(e, Time + 20);
    /// <summary>could the unit hit e standing at p (the end of the way it found): its primary weapon's range on the ground</summary>
    public bool ReachFrom(Unit u, Entity e, (double X, double Z)? p)
    {
        var cs = u.Cs(u.Weapons.Long);
        if (cs == null || p == null) return false;
        double r = AttackRangeOf(u, cs) + (cs.W.Projectile != null ? 0.5 * Math.Max(0, u.Pos.Y - e.Pos.Y) : u.Radius + 2) + (e.Radius != 0 ? e.Radius : 1);
        return Hypot(p.Value.X - e.Pos.X, p.Value.Z - e.Pos.Z) <= r;
    }
    /// <summary>kept for older callers</summary>
    public Entity? NearestEnemy(Unit u, double r, Func<Entity, bool>? filter) => FindTarget(u, r, new FindOpts { Filter = filter });
    public bool ValidTarget(Unit u, Entity? t, UnitTask? task)
    {
        if (t == null || !t.Alive || t.Inside) return false;
        // what cannot be hit (SetHitable false: a sleeping creature, the Scorpion's closed panels) ends an automatic fight;
        // a player's fight too when it cannot be hurt either (a mission's prop: MIRAGE mission 2 keeps the poachers' camp
        // invulnerable and unhittable for its ending - Fight.usl ends the task on an unhittable target; combat.js kept
        // the player's units swinging at it for ever)
        if (t.Untargetable && (!(task != null && task.User) || Invulnerable(t, true))) return false;
        if (t.Active("peace") || (t is Unit tu && tu.Flying && !ReachesFlyer(u))) return false;
        if (!u.IsEnemy(t) && !(task != null && task.ForceAttack)) return false;
        if (u.Owner != null && HiddenFrom(t, u.Owner) && !(task != null && task.User)) return false;
        if (t.Kind == "unit" && u.Owner != null && t.Owner != u.Owner && FowOf(u.Owner) is { } fw && !fw.Visible(t.Pos.X, t.Pos.Z) && !(task != null && task.User)) return false;
        return true;
    }
    /// <summary>a unit in the air (MIRAGE's jetpack flight) is out of reach of melee weapons</summary>
    public bool ReachesFlyer(Entity u) => Long(u)?.Projectile != null;

    /// <summary>start fighting (Fight(): automatic fights never replace an order of the player); true = it fights.
    /// user: an order of the player; amove: the walk an attack move goes on with afterwards</summary>
    public bool EngageTarget(Unit u, Entity? target, EngageOpts? opts = null, bool user = false, double[]? amove = null)
    {
        opts ??= EngageOpts.None;
        if (!u.Alive || u.Inside || target == null) return false;
        if (u.Task.User && !user && u.Task.Type != "idle" && u.Task.Type != "hold") return false;
        if (InAct(u)) return false;                                    // a special move plays out first (Moves.cs)
        if ((u.Stance == 3 || u.Stance == -1) && !user) return false;
        if (u.Weapons.Long == null || u.Disarmed) return false;
        if (u.CannotFight && !u.IsWorker && !user) return false;     // transports, hovercraft, carts ... don't pick fights
        // an enemy that hit it or that a friend shouted about goes through ExamineEnemies too (AddEnemy -> SortEnemyList
        // -> OnAttack): one inside the minimum range or out of reach of a weapon with a minimum range and no secondary
        // weapon is left alone
        if (!user && !SortKeeps(u, target)) return false;
        if (u.IsWorker && !user && !opts.Defend && u.Task.Type != "idle") u.PrevTask = u.Task;
        ReleaseTask(u);
        u.Task = new UnitTask("attack") { Target = target, User = user, Auto = !user, Hold = opts.Hold, Amove = amove };
        u.RepathT = 0;
        if (!user) Shout(u, target, false, true);
        return true;
    }
    partial void Engage(Unit u, Entity e, EngageOpts opts) => EngageTarget(u, e, opts);

    /// <summary>SetAggressionState of the campaign action AIAM and of the computer player (FightingObj.usl:1058, 7094, 8696):
    /// 0 stand ground, 1 defensive (follows an enemy inside a 20 m circle around its post, then returns), 2 aggressive,
    /// -1 passive (never fights). Berserkers ignore it and state 3 (never fights on its own: poisoner) is frozen.</summary>
    public void SetAggro(IEnumerable<Unit?> units, int state)
    {
        foreach (var u in units.ToList())
        {
            if (u == null || !u.Alive || u.Stance == 3 || u.Name.Contains("berserker")) continue;
            u.Stance = state;
            u.Guard20 = state == 1;
            if (u.Task.Type == "idle" || u.Task.Type == "hold") { u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z; }
            // passive (-1): it fights nobody - an attack-move it is on goes on as a plain walk, a fight stops
            if (state == -1 && u.Task.Type == "attackmove") Order(new[] { u }, new Order("move") { X = u.Task.X, Z = u.Task.Z, Auto = true });
            else if (state == -1 && u.Task.Type == "attack") Order(new[] { u }, new Order("stop"));
        }
    }
    /// <summary>ShoutForHelp (FO:6641): own fighters within 1.1 x AlarmRange join (7 s throttle, 3 s when hurt)</summary>
    public void Shout(Entity u, Entity? enemy, bool defend, bool forced)
    {
        if (u.Owner == null || enemy == null || !enemy.Alive) return;
        double t = Time;
        if (!forced && t - u.ShoutT < (u.Hp < u.MaxHp ? 3 : 7)) return;
        if (u is Unit su && su.Task.User && su.Task.Type == "attack" && !defend) return;
        u.ShoutT = t;
        double R = u.Kind == "unit" ? 1.1 * AlarmRange(u) : 40;
        UHash.Query(u.Pos.X, u.Pos.Z, R + 4, o =>
        {
            if (o == u || !o.Alive || o.Owner != u.Owner || o.Inside || o.Weapons.Long == null) return;
            if (o.IsWorker || o.Task.User && o.Task.Type != "idle" && o.Task.Type != "hold") return;
            if (o.Stance == 3 || o.Task.Type == "attack" || o.Task.Type == "heal") return;
            if (!defend && o.Stance == 1) return;
            if (Hypot(o.Pos.X - u.Pos.X, o.Pos.Z - u.Pos.Z) > R) return;
            if (o.Stats!.Heal != null && HealScan(o) != null) return;
            Later(0.1, "helpEngage", new Args { ["o"] = o, ["enemy"] = enemy, ["defend"] = defend });
        });
    }

    // ------------------------------------------------------------------ the attack task (Fight.usl)
    // (the searches with a filter in methods of their own: a lambda capturing `u` in AttackUpdate allocated on every call
    // of every fighting unit, also the many that never search)
    Entity? AttackBetter(Unit u, Entity tgt, UnitTask t) => FindTarget(u, Math.Max(AlarmRange(u), Reach(u, tgt)), new FindOpts { Leash = t.Amove == null, Filter = u.Stance == 1 ? e => u.Attackers.Has(e) : null });
    Entity? AttackOther(Unit u) => FindTarget(u, u.Fow, new FindOpts { Filter = e => RangeZoneOf(u, e).Zone == 3 });
    partial void AttackUpdate(Unit u, double dt)
    {
        var t = u.Task;
        var tgt = t.Target;
        if (!ValidTarget(u, tgt, t) || u.Active("peace") || u.Disarmed) { EndAttack(u); return; }   // (ceasefire: InvalidFighter)
        if (u.Active("trapped")) { u.Vel = V3.Zero; return; }
        // a landed flyer takes off first (MIRAGE CFlyerCtrl.TakeOff from the flyers' AttackEnemy; Systems/Flyers.cs)
        if (u.Flyer != null && FlyerAttackGate(u, tgt!, dt)) return;
        // a blow under way: the attacker stands until it lands - it doesn't run after a fleeing victim in the middle of
        // its swing (the original waits even the whole Duration after AttackEnemy: Fight.usl WaitAction)
        if (u.Pending is { Melee: true }) { if (u.Path.Count > 0) u.Path = new NavPath(); u.Vel = V3.Zero; return; }
        // wild animals give up when the target leaves their territory
        if (u.Wild && Hypot(u.Pos.X - u.HomeX, u.Pos.Z - u.HomeZ) > 70) { AnimalWalk(u, u.HomeX, u.HomeZ); return; }
        // an automatic fight looks at its enemies again every 3 s (FightingObj TIMER_AGGRO -> ExamineEnemies, FO:4679-4770)
        // and turns to a better one - units before buildings, the nearer before the farther; one out of reach only after
        // it stayed the better for 2 s. Defensive units (stance 1) only consider those that attacked them.
        if (!t.User && !u.Wild && (t.ExamT = (t.ExamT ?? 3 * (0.9 + 0.2 * Rng.Next())) - dt) <= 0)
        {
            t.ExamT = 3 * (0.9 + 0.2 * Rng.Next());
            var best = AttackBetter(u, tgt!, t);
            if (best != null && best != tgt)
            {
                if (RangeZoneOf(u, best).Zone > 0 || (t.ChangeT != null && Time - t.ChangeT >= 2)) { t.Target = best; t.ChangeT = null; t.Chasing = false; return; }
                t.ChangeT ??= Time;
            }
            else t.ChangeT = null;
        }
        var z = RangeZoneOf(u, tgt!);
        // too close for a weapon with a minimum range and no short weapon: look for another target, else the fight is
        // over - an order of the player too (Fight.usl: "if the follow fails or the target is in the min range, end the
        // task"; combat.js kept the order and the unit walked against its victim for ever: a fire cannon, the
        // Wehrspinne, the log cannon next to an elk that stopped beside it)
        if (z.Min && u.Weapons.Short == null)
        {
            var other = AttackOther(u);
            if (other != null && other != tgt) { t.Target = other; return; }
            EndAttack(u); return;
        }
        // out of range: chase (FollowEnemy). The unit walks up to the weapon's approach distance and then fights as long
        // as the victim stays inside the (larger) combat range - so it doesn't step after every small move of the victim.
        if (z.Zone == 0 || z.Min) { if (!t.Chasing) { t.ChaseT = 0; t.BestD = null; t.NoProg = 0; t.FsDone = false; t.FsPicked = false; } t.Chasing = true; }   // (a new follow: a new first strike)
        double approach = ApproachDist(u, z.W ?? u.Weapons.Long);
        if (t.Chasing)
        {
            t.ChaseT += dt;
            // stop chasing when close enough, when stuck, or when a moving victim is inside the combat range (hit it now)
            bool fleeing = tgt is Unit tv && LenSq2(tv.Vel) > 0.25;
            // no progress inside the attack range: the attacker is pressed against the victim (big bodies can't get as
            // close as the approach distance - units push each other apart), so it fights from here
            if (z.Zone > 0 && t.BestD != null && z.D > t.BestD - 0.05) t.NoProg += dt; else t.NoProg = 0;
            t.BestD = Math.Min(t.BestD ?? double.PositiveInfinity, z.D);
            bool pressed = t.NoProg > 0.4;
            if (z.Zone > 0 && !z.Min && (z.D <= approach + 0.3 || fleeing || pressed || t.ChaseT > 1.5 && LenSq2(u.Vel) < 0.05)) { t.Chasing = false; t.BestD = null; t.NoProg = 0; }
        }
        if (t.Chasing && (u.OnWall != null || (tgt is Unit tw && tw.OnWall != null)))
        {
            // on a wall: along the walkway or down after it (systems/wallwalk.js); after one on a wall: up where the unit
            // may (its own wall, a ladder of its side), a shooter from the ground; a fighter without a way up gives up
            if (u.OnWall != null) { WallChase(u, t, tgt!, dt); return; }
            if (z.High && !ShootsUp(u))
            {
                var r = WallReach(u, tgt!);
                if (r == null) { EndAttack(u); return; }
                var then = new Order("attack") { Target = tgt, Auto = !t.User };
                if (r.Value.Ladder != null) ClimbSiege(u, r.Value.Ladder, t.User); else UseWall(u, r.Value.Wall, t.User);
                u.Task.Then = then;
                return;
            }
        }
        if (t.Chasing)
        {
            // turrets that can't move (water turret ...): an ordered target is kept until it comes into range
            if (u.Stationary) { if (!t.User) { EndAttack(u); return; } u.Vel = V3.Zero; return; }
            if (t.Hold || (u.Stance == 0 && !t.User && t.Amove == null)) { EndAttack(u); return; }
            if (!u.CanWalk) { EndAttack(u); return; }
            if (!t.User && t.Amove == null && !u.Wild)
            {
                double L = Leash(u);
                if (Hypot(tgt!.Pos.X - u.AnchorX, tgt.Pos.Z - u.AnchorZ) > L) { EndAttack(u, true); return; }
            }
            // automatic special moves are also tried while following the enemy (Fight.usl:505): those that are made for it
            // (the stampedes; the others check for an enemy in reach in the original's Check functions)
            t.ChaseMoveT -= dt;
            if (t.ChaseMoveT <= 0) { t.ChaseMoveT = 1; if (AutoMove(u, tgt!, true)) return; }
            // the advance action's first strike of an order of the player (FirstStrike.cs: engine "FrstStrk" -> OnDoFirstStrike)
            if (t.User && !t.FsDone) TryFirstStrike(u, t, tgt!, approach);
            if (FirstStrikeLunge(u, t)) return;    // the first strike's lunge to its strike point (FirstStrike.cs)
            u.RepathT -= dt;
            if (u.RepathT <= 0 || u.Path.Count == 0)
            {
                double d = z.D;
                if (d < 30 && Nav.Los(u.Pos.X, u.Pos.Z, tgt!.Pos.X, tgt.Pos.Z)) { u.Path = new NavPath { new Waypoint(tgt.Pos.X, tgt.Pos.Z) }; u.Goal = (tgt.Pos.X, tgt.Pos.Z); }
                else if (tgt is Building) { var (x, z2) = ApproachPoint(u, tgt!); u.SetPath(x, z2); }
                else u.SetPath(tgt!.Pos.X, tgt.Pos.Z);
                u.RepathT = d < 30 ? 0.3 : 1.2;
                // follow counter: give up on targets we can't reach (Fight.usl:527) - no way at all, or only a way to the foot
                // of the cliff (the island's shore) from where its weapon does not reach the target: then it leaves that
                // one alone for a while, or an attack wave would stand under a unit on a cliff instead of going on
                if (u.Path.Count == 0) { if (++t.Fails > 3) { Unreachable(u, tgt!); EndAttack(u); return; } }
                else if (u.NoWay != null && !ReachFrom(u, tgt!, (u.Path[^1].X, u.Path[^1].Z))) { if (++t.Fails > 2) { Unreachable(u, tgt!); EndAttack(u); return; } }
                else t.Fails = 0;
            }
            double sp = u.Steer(dt, u.RunSpeed, 0.3);
            if (t.FsUntil > Time) return;          // the first strike's clip runs on (it strikes while it runs)
            u.MoveAnim(sp, u.Wild);
            u.BusyAnim = false;
            return;
        }
        // in range
        u.ClearPath();
        u.Vel = V3.Zero;
        var w = z.W ?? u.Weapons.Long!;
        u.UseWeapon(w);
        u.LastTarget = tgt;
        var cs = u.Cs(w);
        bool proj = w.Projectile != null;
        // a unit that sets up to shoot (the log cannon: compose.js DEPLOY) turns to the target, sets up, then only its gun turns
        var dep = u.Comp?.Dep != null ? u.Deploy : null;
        double off;
        if (dep != null && dep.State != "attack")
        {
            if (dep.State == "walk" || dep.State == "up") { if (dep.State == "up" && u.Comp!.HoldMove(Time)) return; if (u.Face(tgt!.Pos.X, tgt.Pos.Z, dt, 7) > 0.3) return; }
            if (!u.Comp!.DeployReady(Time)) return;
        }
        if (dep != null) off = u.Comp!.AimGun(tgt!.Pos.X, tgt.Pos.Z, dt);
        else
        {
            // (MIRAGE's pirate boss ship turns its broadside to the target: PirateShip.cs PirateBroadside)
            off = IsPirateShip(u) ? PirateBroadside(u, tgt!, dt) : u.Face(tgt!.Pos.X, tgt.Pos.Z, dt, 7);
            if (u.Comp?.Top != null) off = u.Comp.Aim(tgt.Pos.X, tgt.Pos.Z, dt);
        }
        // (+ the last shot's extra time: the first shot of a weapon just raised waits longer - FirstShot below)
        double dur = AttackDuration(u, cs) + u.ShotOvertime;
        // (a first strike under way: the next blow after its hit - the first strike does not count as a blow: LastHitDone)
        if (Time - u.LastHitDone > dur && Time >= t.FsHit && off < (proj ? (w.Penetration ? Math.PI / 16 : Math.PI / 4) : Math.PI))
        {
            u.LastHitDone = Time;
            if (u.RageT != 0) u.RageHit = true;                 // a mammoth's stampede ends with its first real attack
            // automatic special moves replace the normal hit (CheckSpecialMoves)
            if (AutoMove(u, tgt!, false)) return;
            // a blow that would kill: a finishing move instead (Fight.usl:426-437, systems/finishing.js)
            if (StartFinishingMove(u, tgt!, w)) return;
            var anims = w.Anims.Count > 0 ? w.Anims : DefaultAnims;
            var plain = anims.Where(a => !(a.Combo != 0 && !double.IsNaN(a.Combo))).ToList();
            int pi = (int)Math.Floor(Rng.Next() * plain.Count);
            var pick = pi < plain.Count ? plain[pi] : anims[0];
            u.FightAnim = pick.Name;          // (GetFightAnim: a victim's beheading asks it - HeadOff)
            var clip = dep != null ? null : u.Anim!.Pick(pick.Name, "attack_front", "attack_1", "res_strike_3", "nat_strike_0");
            // a shot its gun add-on fires (the gun plays its clip) or its rider shoots: an animal carrying them does not
            // strike (Compose.CarrierStrikes) - it stands
            bool gunFires = dep == null && proj && u.Comp?.Gun != null;
            if (clip != null && proj && !Compose.CarrierStrikes(true, Compose.IsAnimal(u.Cls, Models.Get(u.Gfx)?.Arch), gunFires, u.Comp?.Rider?.Attack != null, Data.IsStrikeOf(u.Name, clip)))
            {
                clip = null;
                if (!u.BusyAnim) u.Anim!.Play(u.FightStand() ?? u.IdleAnim);
            }
            if (dep != null) u.Comp!.Fired();                    // (it lies still: the gun recoils)
            if (gunFires) u.Comp!.Fire();
            bool fightPos = clip != null && FightPosition(u, w, clip);
            // the first shot's extra time (CCharacter.AttackEnemy m_fWeaponDurationOvertime): set by every primary shot of
            // the characters that raise their weapon, kept by their other shots (as the original's member)
            if (FirstShotUnit(u) && ReferenceEquals(w, u.Weapons.Long))
            {
                u.ShotOvertime = fightPos && !(u.FightPos && u.Anim!.Phase == "l") ? FirstShot.Of(u.Name) : 0;
                dur = AttackDuration(u, cs) + u.ShotOvertime;
            }
            if (fightPos)
            {
                // its fight position: the start part on the first shot, then the loop part each shot, held between them
                bool intro = !(u.FightPos && u.Anim!.Phase == "l");
                double len = u.Anim!.Duration(clip + "#l") + (intro ? u.Anim.Duration(clip + "#s") : 0);
                u.BusyAnim = true;
                u.Anim.PlaySle(clip, intro, len > dur ? len / dur : 1, new AnimDone("busy"));
                u.FightPos = true;
            }
            else if (clip != null)
            {
                double len = u.Anim!.Duration(clip);
                double ts = len > dur ? len / dur : 1;
                u.BusyAnim = true;
                u.FightPos = false;
                u.Anim.Play(clip, new AnimOpts { Loop = false, Restart = true, Fade = 0.08, Ts = ts, OnDone = new AnimDone("busy") });
            }
            // (a projectile leaves the extra time later: GetProjectileDelay = the clip's shoot delay + the overtime; a direct
            // hit - the flamethrowers' Penetrate - is not delayed by it)
            double delay = proj ? Or(pick.ShootDelay, 0.3) + u.ShotOvertime : Or(pick.Delay, 0.4);
            var shot = new PendingShot { T = Math.Min(delay, dur * 0.9), W = w, Target = tgt };
            // a melee blow is decided now, when the swing starts (FightingObj.AttackEnemy: TakeDmg with the clip's delay,
            // Penetrate, CAreaDamage at the victim's place): it lands after the delay even if the victim ran on - a
            // victim in range when the swing began is hit (combat.js re-checked the range at the hit and a fleeing
            // animal was never hit: docs/SIMULATION.md "Melee blows")
            if (!proj && cs != null)
            {
                shot.Melee = true; shot.At = tgt!.Pos;
                if (w.Penetration) shot.Victims = PenetratedBy(u, tgt, cs, Or(w.PenAngle, 30), true);
                else if (!(cs.Splash > 0)) shot.Victims = new List<Entity> { tgt };
            }
            u.Pending = shot;
            if (!t.User) Shout(u, tgt, false, false);
        }
        else if (!u.BusyAnim && dep == null && !(u.FightPos && u.Anim!.Phase == "l"))
        {
            u.Anim!.Play(u.FightStand() ?? u.IdleAnim);
        }
    }

    /// <summary>
    /// The units that hold a fight position between shots (base CCharacter.IsAnimLoopUnit, character.usl:2529; MIRAGE's
    /// adds seas_worker, seas_sniper, hu_jetpack_warrior, shooter_lvl*; the vehicles with the same m_bTecInFightPos -
    /// Vehicle.usl CLumberjack: seas_lumberjack_minigun, babbage_mobile_suit, MIRAGE's seas_lumberjack -, MIRAGE's
    /// CSpecialMobileSuit and CSeasTerminator): their shooting clip is a start / loop / end clip - the bow comes up, the
    /// shot, the bow goes down -; the first shot of a fight plays its start and loop parts, every further shot the loop
    /// part only (SetSLEAnim(clip, 1, !m_bTecInFightPos, no outro)), and the end part plays when the fight ends
    /// (EndFight: AnimAction(clip, 0, no intro, outro)). They played the whole clip each shot - raised and lowered the
    /// weapon for every arrow or bullet - and stood in the fight stand between shots. The workers of the list (with an
    /// animal's weapon: m_sAnimalWeapon) are left out. The first shot's extra time: <see cref="FirstShot"/>.
    /// </summary>
    static readonly HashSet<string> FIGHT_POS = new(StringComparer.OrdinalIgnoreCase)
    {
        "hu_archer", "hu_marksman", "aje_archer", "aje_rammer", "aje_thrower", "ninigi_archer", "ninigi_marksman", "ninigi_ninja",
        "ninigi_mortar", "seas_marksman", "seas_gunner", "seas_rocketman", "seas_flamethrower", "seas_sniper", "Bela_s0", "tesla_s0",
        "babbage_s0", "lovelace_s0", "schliemann_s0", "hermit_s0", "hu_flamethrower", "aje_flamethrower", "ninigi_flamethrower",
        "hu_jetpack_warrior", "seas_lumberjack_minigun", "babbage_mobile_suit", "seas_lumberjack", "special_mobile_suit",
        "seas_mobile_terminator",
    };
    /// <summary>does this shot put the unit into (or keep it in) its fight position? its primary weapon, a clip with a loop part</summary>
    public static bool FightPosition(Unit u, Weapon w, string clip) =>
        u.Anim != null && (FIGHT_POS.Contains(u.Name) || u.Name.Contains("shooter_lvl", StringComparison.OrdinalIgnoreCase)) &&
        ReferenceEquals(w, u.Weapons.Long) && u.Anim.Has(clip + "#l");
    /// <summary>
    /// The first shot's extra time (base and MIRAGE character.usl CCharacter.AttackEnemy, base lines 2399-2412 / 2471-2474,
    /// GetWeaponDuration / GetProjectileDelay 2584-2590; MIRAGE 3856-3926, 4042-4048): a character of IsAnimLoopUnit that
    /// shoots with its primary weapon and has the shooting clip sets m_fWeaponDurationOvertime - 0.6 s for Bela_s0, 0.4 s
    /// for the others when the weapon is not up yet (!m_bTecInFightPos: the first shot of a fight, after it followed its
    /// target - FollowEnemy calls EndFight - or after the fight ended; the weapon comes up), 0 when it is up. The time is
    /// added to the weapon's duration (Fight.usl waits it before the next shot) and to the projectile's delay (the arrow
    /// leaves that much later). Their other weapons leave the member as it is (the next blow waits it too); vehicles
    /// (CLumberjack, MIRAGE's mobile suits) have none. Here: a shot that takes the fight position from its start part
    /// (Combat.FightPosition, not held: the loop part not playing), Unit.ShotOvertime.
    /// The remake's debug option "First-shot delay" (DebugOptions.FirstShot / FirstShotDelay, docs/DEBUG.md) switches it
    /// off or sets one time for everybody; it travels with the GameStart (saves, replays, the lockstep).
    /// </summary>
    public FirstShotDelay FirstShot = new();
    /// <summary>the characters whose primary shots set the overtime (CCharacter.IsAnimLoopUnit without the workers, whose
    /// slingshot - m_sAnimalWeapon - takes no fight position here; the jetpack warrior only flying)</summary>
    public static bool FirstShotUnit(Unit u) =>
        FIGHT_POS.Contains(u.Name) && !FIRST_SHOT_NOT.Contains(u.Name) && (u.Flying || !u.Name.Equals("hu_jetpack_warrior", StringComparison.OrdinalIgnoreCase))
        || u.Name.Contains("shooter_lvl", StringComparison.OrdinalIgnoreCase);
    /// <summary>the fight-position units that are vehicles (no m_fWeaponDurationOvertime)</summary>
    static readonly HashSet<string> FIRST_SHOT_NOT = new(StringComparer.OrdinalIgnoreCase)
    {
        "seas_lumberjack_minigun", "babbage_mobile_suit", "seas_lumberjack", "special_mobile_suit", "seas_mobile_terminator",
    };
    static readonly List<WeaponAnim> DefaultAnims = new() { new WeaponAnim("attack_front", 0.4, 0.3, 0, null) };
    /// <summary>(x || d) of a JS number</summary>
    static double Or(double x, double d) => x != 0 && !double.IsNaN(x) ? x : d;

    /// <summary>the fight is over: resume work (workers), look for the next enemy, or go back to the post</summary>
    public void EndAttack(Unit u, bool leashed = false)
    {
        var t = u.Task;
        // (a unit in its fight position keeps it while it takes the next target; with nothing to fight its standing clip
        // takes over and the end part of its shooting clip plays first - AnimCtl.Play; CCharacter.EndFight)
        // (a unit set up to shoot stays set up when the fight is over: the original switches the build-up to walk mode only
        // to follow a target out of range and when the fight is broken off - Fight.usl:489-497, Break 620-640 -, not when
        // it ends; it gets up when it has to move: Unit.Move -> Composite.HoldMove. It got up after every fight: the Black
        // Widow uncurled, the log cannon mammoth stood up, between two targets)
        // a worker that killed an animal on an order of the player gathers its carcass (Fight.usl:332-343 "GetFood"; the
        // hunting chain of GetFood.usl orders the next animal the same way)
        var prey = t.Target; var meat = prey is Unit pu && !pu.Alive ? pu.CorpseNode : null;
        if (meat != null && meat.Alive && u.IsWorker && t.User && !u.Inside) { u.PrevTask = null; StartGather(u, meat); return; }
        // a wild animal feeds on the wild animal it killed (Fight.usl "Feed")
        if (u.Wild && prey != null && !prey.Alive && FeedOnKill(u, prey)) return;
        if (u.PrevTask != null && u.PrevTask.Type != null)
        {
            var pt = u.PrevTask; u.PrevTask = null;
            if (pt.Node != null && pt.Node.Alive) { StartGather(u, pt.Node); return; }
            if (pt.Farm != null && pt.Farm.Alive) { StartGather(u, pt.Farm); return; }
            if (pt.Building != null && pt.Building.Alive) { StartBuild(u, pt.Building); return; }
        }
        if (!leashed && (u.Stance == 2 || t.Amove != null || u.Wild))
        {
            var next = FindTarget(u, t.Amove != null ? u.Fow : AlarmRange(u), new FindOpts { Leash = t.Amove == null && !u.Wild });
            if (next != null) { u.Task = new UnitTask("attack") { Target = next, Amove = t.Amove, Auto = true, User = false }; return; }
        }
        if (t.Amove != null) { u.Task = new UnitTask("attackmove") { X = t.Amove[0], Z = t.Amove[1], Then = t.Then, User = true }; u.SetPath(t.Amove[0], t.Amove[1]); return; }
        u.Task = new UnitTask(u.Wild ? "roam" : t.Hold ? "hold" : "idle"); u.Path = new NavPath();
        u.BusyAnim = u.Deploy is { State: not "walk" } && u.Comp?.Dep != null;       // (still set up: no idle clips)
        u.FightStandT = 4;
        // character.usl OnIdleTick after a fight ("victory state"): soon after, when no enemy is left around, one time in
        // four a victory animation (victory_0 .. 2, once or twice) - not workers, not on a transport
        if (!u.Wild && u.Cls == "CHTR" && !u.Name.Contains("worker") && !u.Inside && prey != null && !prey.Alive) u.Task.VictoryAt = Time + Rng.Next() * 0.8;
        // characters after an automatic fight walk back to their post (CheckPatrol)
        // (not on a wall: CheckPatrol returns at once there, FO:2393)
        if (!u.Wild && !t.User && u.OnWall == null && Hypot(u.Pos.X - u.AnchorX, u.Pos.Z - u.AnchorZ) > 3) { u.Task = new UnitTask("move") { X = u.AnchorX, Z = u.AnchorZ, BackToPost = true }; u.SetPath(u.AnchorX, u.AnchorZ); }
    }

    /// <summary>a melee blow of the attack loop lands (its delay is over): its victims chosen at the swing take the damage
    /// wherever they are now (alive and not inside a transport or building); an area blow strikes where the victim
    /// stood (combat.js fire, without its range check: see AttackUpdate)</summary>
    public void LandBlow(Unit u, PendingShot p)
    {
        var w = p.W; var tgt = p.Target;
        if (!u.Alive || w == null) return;
        var cs = u.Cs(w);
        if (cs == null) return;
        if (u.St.Camo.Has("disg")) { u.St.Camo.Delete("disg"); u.DisgT = Time + 10; }
        bool hitTarget = false;
        if (p.Victims == null)
        {
            AreaDamage(u, p.At, cs.Splash, cs.Dmg, cs.EndDmg, new AreaOpts { Size = w.Size });
            hitTarget = tgt != null && tgt.Alive;
        }
        else
            foreach (var v in p.Victims)
            {
                if (!v.Alive || v.Inside) continue;
                TakeDmg(u, v, 1, 0);
                if (v == tgt) hitTarget = true;
            }
        if (!hitTarget) return;
        Emit("hit", new Args { ["attacker"] = u, ["target"] = tgt, ["melee"] = true, ["weapon"] = w });
        if (u.Name == "aje_poisoner") Kill(u, null);     // the poisoner dies with its attack (Fight.usl)
    }

    /// <summary>the delayed part of an attack: melee hit, area hit, cone hit or projectile launch (the attack loop's
    /// melee blows land through LandBlow; this is the shot of the moves - FireShot, FireUnit - and every projectile)</summary>
    partial void Fire(Unit u, Weapon? w, Entity? target)
    {
        var tgt = target;
        if (!u.Alive || tgt == null || !tgt.Alive || w == null) return;
        var cs = u.Cs(w)!;
        // the ninja loses its disguise when it attacks; it comes back 10 s later (FightingObj.usl CAMO_TIMER)
        if (u.St.Camo.Has("disg")) { u.St.Camo.Delete("disg"); u.DisgT = Time + 10; }
        if (w.Projectile == null)
        {
            if (w.Penetration) Penetrate(u, tgt, cs, Or(w.PenAngle, 30));
            else if (cs.Splash > 0) AreaDamage(u, tgt.Pos, cs.Splash, cs.Dmg, cs.EndDmg, new AreaOpts { Size = w.Size });
            else
            {
                if (Reach(u, tgt) > AttackRangeOf(u, cs) + u.Radius + 3.5) return;   // the victim got away during the swing
                TakeDmg(u, tgt, 1, 0);
            }
            Emit("hit", new Args { ["attacker"] = u, ["target"] = tgt, ["melee"] = true, ["weapon"] = w });
            if (u.Name == "aje_poisoner") Kill(u, null);     // the poisoner dies with its attack (Fight.usl)
            return;
        }
        if (PirateVolley(u, w, cs, tgt)) return;           // MIRAGE's pirate boss ship: 14 arrows a shot (PirateShip.cs)
        var from = u.Pos;
        from.Y += Math.Min(u.Height * 0.7, 2.5);
        if (Muzzle(u) is V3 m) from = m;
        Launch(u, from, tgt, w, cs);
    }
    /// <summary>public: a shot of another system (moves) through the unit's attack (combat.js fire)</summary>
    public void FireShot(Unit u, Weapon? w, Entity? tgt) => Fire(u, w, tgt);

    /// <summary>a point of a model's link in the world: the entity's position, the link turned by the heading</summary>
    public static V3 LinkToWorld(V3 origin, double heading, V3 l)
    {
        double c = SMath.Cos(heading), s = SMath.Sin(heading);
        return new V3(origin.X + l.X * c + l.Z * s, origin.Y + l.Y, origin.Z - l.X * s + l.Z * c);
    }

    /// <summary>where a shot leaves the unit: a turret/top part's projectile link (the Black widow's arm tip is an unnamed link at
    /// the end of its bone chain), else the body's own Proj link (a ridden Dilophosaurus spits from its mouth, not from
    /// the rider), else the rider's (a rider holding the gun), else the unit's right hand (characters); null: none
    /// (the links' rest positions: the model data has no animated pose)</summary>
    public V3? Muzzle(Unit u)
    {
        var M = Models; var c = u.Comp;
        V3? PartLink(AttachedPart p, params string[] names)
        {
            foreach (var n in names)
            {
                if (M.Link(p.Gfx, n) is not V3 l) continue;
                var at = (M.Link(u.Gfx, p.Link) ?? V3.Zero) + (p.Spec?.OffsetModel ?? V3.Zero);      // (the part hangs at its link of the unit's model - and its offset there - turned by its yaw)
                return LinkToWorld(u.Pos, u.Heading, at) + LinkToWorld(V3.Zero, u.Heading + p.Yaw, l);
            }
            return null;
        }
        if (c?.Top != null && PartLink(c.Top, "Proj", "unnamed") is V3 a) return a;
        if (M.Link(u.Gfx, "Proj") is V3 own) return LinkToWorld(u.Pos, u.Heading, own);
        if (c?.Rider != null && PartLink(c.Rider, "Proj", "HndR") is V3 r) return r;
        if (M.Link(u.Gfx, "HndR") is V3 h) return LinkToWorld(u.Pos, u.Heading, h);
        return null;
    }

    /// <summary>CArrow.Set/Shoot (Product.usl:618): snapshot of the attacker's values; jitter moves the aim of area shots</summary>
    public Projectile Launch(Entity src, V3 from, Entity tgt, Weapon w, WeaponStats cs, double extraDelay = 0, bool shootEvent = true)
    {
        V3? aim = null;
        if (w.Jitter != 0 && cs.Splash > 0)
        {
            double L = (Math.Floor(Rng.Next() * Math.Max(1, w.Jitter * 20)) / 10) - w.Jitter, th = Rng.Next() * 3.14;
            var a = tgt.Pos; a.X += SMath.Cos(th) * L; a.Z += SMath.Sin(th) * L; aim = a;
        }
        var p = new Projectile(this, from, tgt, w, src, aim);
        p.Snap = new ProjectileSnap { Dmg = cs.Dmg, EndDmg = cs.EndDmg, Splash = cs.Splash, Ap = cs.Ap, Size = w.Size };
        p.Delay = extraDelay;
        Projectiles.Add(p);
        if (shootEvent) Emit("shoot", new Args { ["attacker"] = src, ["weapon"] = w, ["pos"] = from });
        return p;
    }

    partial void ProjectileUpdate(Projectile p, double dt)
    {
        if (p.Delay > 0) { p.Delay -= dt; return; }
        p.T += dt;
        double k = Math.Min(1, p.T / p.TT);
        if (p.Target != null && p.Target.Alive && !p.FixedAim) { p.Aim.X = p.Target.Pos.X; p.Aim.Z = p.Target.Pos.Z; p.Aim.Y = p.Target.Pos.Y + Math.Min(2, (p.Target is Unit tu && tu.Height != 0 ? tu.Height : 2) * 0.5); }
        p.Prev = p.Pos;
        p.Pos = V3.Lerp(p.From, p.Aim, k);
        p.Pos.Y += SMath.Sin(k * Math.PI) * p.Arc;
        if (k < 1) return;
        p.Alive = false;
        var w = p.Weapon; var A = p.Owner;
        // a special arrow (Moves: Bela's, Lovelace's shot) does its own damage on impact
        if (p.Impact != null)
        {
            Delayed.DELAYED[p.Impact](this, p.ImpactArgs ?? new Args());
            Emit("impact", new Args { ["pos"] = p.Aim, ["weapon"] = w, ["splash"] = false, ["target"] = p.Target });
            return;
        }
        var s = p.Snap ?? new ProjectileSnap { Dmg = w.Dmg, EndDmg = w.EndDmg, Splash = w.Splash, Ap = w.Ap };
        // Aje ankylosaurus catapult "dino ammo": the egg hatches a velociraptor that fights for 30 s (CDinoAmmoEgg)
        var hatchOwner = A != null ? A.Owner : p.OwnerPlayer;
        if ((w.Projectile ?? "").ToLowerInvariant() == "aje_ammo_dino" && hatchOwner != null)
        {
            var r = SpawnUnit("aje_velociraptor", hatchOwner, p.Aim.X, p.Aim.Z, 1, 0, new UnitOpts { Autonomous = true });
            if (r != null) { r.HomeX = p.Aim.X; r.HomeZ = p.Aim.Z; r.HomeRadius = 60; Later(30, "hatchlingGone", new Args { ["u"] = r }); }
        }
        // OnDoDmg (Product.usl:535): area weapons hit the (jittered) aim point, others always hit their target
        if (s.Splash > 0)
        {
            if (A != null && A.Alive) AreaDamage(A, p.Aim, s.Splash, A.Cs(w) is { } acs ? acs.Dmg : s.Dmg, s.EndDmg, new AreaOpts { Size = s.Size });
            else AreaDamage(null, p.Aim, s.Splash, s.Dmg, s.EndDmg, new AreaOpts { Owner = p.OwnerPlayer, Size = s.Size });
            Emit("impact", new Args { ["pos"] = p.Aim, ["weapon"] = w, ["splash"] = true });
            return;
        }
        var V = p.Target;
        if (V != null && V.Alive)
        {
            if (A != null && A.Alive) TakeDmg(A, V, 1, 0, w);
            else TakeDirectDmg(V, s.Dmg, s.Ap, p.OwnerPlayer, true);
            Emit("hit", new Args { ["attacker"] = A, ["target"] = V, ["weapon"] = w });
            Emit("impact", new Args { ["pos"] = p.Aim, ["weapon"] = w, ["splash"] = false, ["target"] = V });
            return;
        }
        Emit("impact", new Args { ["pos"] = p.Aim, ["weapon"] = w, ["splash"] = false });
    }

    // ------------------------------------------------------------------ damage (FightingObj.usl)
    /// <summary>TakeDmg: the attacker's weapon hits the victim with `factor` (splash falloff); the hit lands after hitDelay.
    /// ap: armour piercing instead of the weapon's (MIRAGE's triceratops tusk: 100). Returns the damage.</summary>
    public double TakeDmg(Entity A, Entity V, double factor = 1, double hitDelay = 0, Weapon? weapon = null, double? ap = null)
    {
        if (!V.Alive || V.Inside) return 0;
        if (V is Unit vu0 && vu0.St.Camo.Has("aje")) SetCamo(vu0, "aje", false);
        if (A.Cls == "ANML" && V.Active("anmlImmune")) return 0;
        if (V is Building vb0 && vb0.Behaviour is IHitBehaviour hb) hb.Hit(vb0, A, this);          // (walls drop stones)
        var w = weapon ?? A.Weapon ?? A.Weapons.Long;
        if (w == null) return 0;
        var cs = A.Cs(w);
        if (cs == null) return 0;
        if (w.Projectile != null && V.Active("storm")) return 0;     // blinding storm: no projectile damage (MIRAGE FO:5810)
        double pierce = ap ?? cs.Ap;
        double bonus = (w.Bonus.TryGetValue(V.Cls, out var b1) ? b1 : 0) + (w.Bonus.TryGetValue(V.Name, out var b2) ? b2 : 0);
        double raw = factor * AttackBoni(A, cs.Dmg, w) * (1 + (double.IsNaN(bonus) ? 0 : bonus) / 100);
        if (A is Unit au && au.Grow != null) raw *= au.Grow.Value;                                 // a young animal (Animal.GetDmg)
        if (V.Cls == "BLDG" && A.St.Bonus.BLDGDAMAGE > 0)
        {
            double t0 = Math.Truncate(raw); raw *= 1 + A.St.Bonus.BLDGDAMAGE * 0.01; if (Math.Truncate(raw) == t0) raw += 0.5;
        }
        var vcs = V.Kind == "res" ? null : V.Cs();
        double pct;
        if (w.Projectile == null)
        {
            pct = TempDef(V, Math.Max(0, (vcs != null ? vcs.Prot : 0) - pierce)) + DefenceBonusVs(V, A.Cls, A.Name);
        }
        else pct = TempRangedDef(V, Math.Max(0, (vcs != null ? vcs.RProt : 0) - pierce));
        double dmg = raw - raw * Clamp(pct * 0.01, 0, 0.99);
        // AttackFactor / DefenseFactor of computer players (CAiCheatMgr.UpdateFightFactors; docs/spec/ai.md §3)
        if (A.Owner?.AiMods != null) dmg *= Or(A.Owner.AiMods.Attack, 1);
        if (V.Owner?.AiMods != null) dmg *= Or(V.Owner.AiMods.Defense, 1);
        // the co-op campaign's enemy scaling (docs/COOP.md): more damage, more health (as less damage taken)
        if (A.Owner != null && A.Owner.CoopDamage != 1) dmg *= A.Owner.CoopDamage;
        if (V.Owner != null && V.Owner.CoopHealth != 1) dmg /= V.Owner.CoopHealth;
        // the ice spearman freezes its victims for 2.5 s (SetIced)
        if (A.Name == "ninigi_icespearman" && V.Kind == "unit") Trap(V, 2.5, "iced");
        V.DmgCache = dmg;                                                 // (the damage cache a finishing move looks at)
        Later(hitDelay, "hit", new Args { ["dmg"] = dmg, ["A"] = A, ["V"] = V, ["proj"] = w.Projectile != null, ["poison"] = cs.Poison, ["ticks"] = cs.PoisonTicks, ["w"] = w });
        return dmg;
    }
    /// <summary>ProvideDmg (FO:4324): invulnerability, knockback, life drain, poison, then Damage()</summary>
    public void ProvideDmg(double dmg, Entity? A, Entity V, bool isProj, double poison, double ticks, Weapon? w)
    {
        if (!V.Alive) return;
        if (!isProj && A != null && !A.Alive) return;
        if (Invulnerable(V)) return;
        if (Hooks.Damage != null && Hooks.Damage(dmg, A, V)) return;        // (the sequel's tranquilizers and electric weapons: game/sequel/runtime.js)
        V.LastAttacker = A; SetLastHit(V, Time);
        if (A?.Owner != null) V.LastDamager = A.Owner;
        if (A != null) V.Attackers.Add(A);
        // knockback when the attacker is at least 3 size classes bigger (characters and animals only)
        if (A != null && A.Alive && V is Unit vu && (V.Cls == "CHTR" || V.Cls == "ANML") && dmg >= 1)
        {
            double asz = A.Stats?.Size ?? 0, vsz = V.Stats?.Size ?? 0;
            double diff = asz - vsz;
            bool thrown = false;
            if (asz > 0 && vsz > 0 && diff >= 3)
            {
                int i = (int)Math.Min(9, diff);
                if (Rng.Next() < KNOCK_CHANCE[i]) { Knockback(vu, A.Pos, KNOCK_DIST[i] * (1 + Rng.Next() * 0.2), 0.4); thrown = true; }
            }
            // the side the blow came from (FO:4380-4434: not on a wall, not when thrown) - the death clip's choice
            if (!thrown && vu.OnWall == null)
            {
                vu.LastHitDir = HitSide(vu, A.Pos);
                // the hit reaction (FO:4436-4446): a melee blow (!IsProjectile), not in a trap (m_xTrap) - the overlay
                // clip the view plays over the running one; only counted here (no random draw, no state the world reads)
                if (!isProj && vu.LastHitDir != '\0' && !InTrap(vu)) { vu.HitReactN++; vu.HitReactSide = vu.LastHitDir; vu.HitReactT = Time; }
            }
        }
        if (A != null && A.Stats != null && A.Stats.Abilities.TryGetValue("drain_life", out var drain) && A.Alive) A.Hp = Math.Min(A.MaxHp, A.Hp + dmg * Or0(drain.Num("amount", 0)));
        // BLDG_res_back: the killer's player gets the build cost of a destroyed building
        if (V.Hp - Math.Max(Math.Ceiling(dmg), 1) <= 0 && A?.Owner != null && (V.Kind == "building" || V.Name == "aje_resource_collector") && Data.Invented(A.Owner, "BLDG_res_back", A.Owner.Tribe)) GrantBuildCost(V, A.Owner);
        if (poison > 0 && ticks > 0 && V is Unit pv && V.Cls != "VHCL" && V.Cls != "SHIP") ApplyPoison(pv, poison, (int)ticks, A);
        Damage(V, dmg, A);
    }
    static double Or0(double x) => double.IsNaN(x) ? 0 : x;

    /// <summary>Damage(): hit points lost = max(ceil(dmg), 1); ignored while invulnerable after a level up / doping</summary>
    public void Damage(Entity V, double d, Entity? A)
    {
        if (V.ParentGate != null && V.ParentGate.Alive) V = V.ParentGate;      // a gate's wing passes all damage to the gate (B:2266)
        if (!V.Alive) return;
        if (Invulnerable(V, true)) return;
        // debug options (game/cheats.js): the sides' damage and defence factors; a factor of 0 takes nothing
        if (Cheats != null && d > 0) { d = CheatDamage(d, A, V); if (!(d > 0)) return; }
        double loss = Math.Max(Math.Ceiling(d), 1);
        // a blow that would kill: a game mode may keep it alive at 1 hit point (the sequel's tranquilized creatures fall asleep)
        if (V.Hp - loss <= 0 && Hooks.Lethal != null && Hooks.Lethal(V, A)) { V.Hp = 1; SetLastHit(V, Time); return; }
        V.Hp -= loss;
        AfterDamage(V, A);
    }
    /// <summary>TakeDirectDmg / TakeDirectMeleeDmg (FO:4554): fixed damage reduced by (ranged) protection only</summary>
    public void TakeDirectDmg(Entity V, double d, double ap = 0, Player? byPlayer = null, bool ranged = true, Entity? A = null)
    {
        if (!V.Alive || V.Inside || Invulnerable(V)) return;
        if (V.Active("storm")) return;                       // blinding storm (MIRAGE FO:6464)
        var vcs = V.Kind == "res" ? null : V.Cs();
        double p = ranged ? TempRangedDef(V, (vcs != null ? vcs.RProt : 0) - ap) : TempDef(V, (vcs != null ? vcs.Prot : 0) - ap);
        if (byPlayer != null) V.LastDamager = byPlayer;
        if (A != null) V.LastAttacker = A;
        Damage(V, d - d * Clamp(p / 100, 0, 0.99), A);
    }
    /// <summary>legacy name used by effects and moves: fixed damage without protection</summary>
    public void ApplyDamage(Entity V, double dmg, Entity? A) { if (A?.Owner != null) V.LastDamager = A.Owner; Damage(V, dmg, A); }

    void AfterDamage(Entity V, Entity? A)
    {
        SetLastHit(V, Time);
        if (A != null && A.Alive) V.LastAttacker = A;
        if (V.Owner != null) Emit("attacked", new Args { ["entity"] = V, ["by"] = A });
        if (V.Hp <= 0) { Kill(V, A); return; }
        // buildings and ships at 50 % / 25 % hit points: an explosion (UpdateDestructionFlags FO:3047, once per stage)
        if (V.Kind == "building" || V.Cls == "SHIP")
        {
            int st = V.Hp * 4 <= V.MaxHp ? 2 : V.Hp * 2 <= V.MaxHp ? 1 : 0;
            if (st != V.DmgFx) { bool worse = st > V.DmgFx; V.DmgFx = st; if (worse) Emit("damagestage", new Args { ["entity"] = V, ["stage"] = st }); }   // (lower after repairs)
        }
        if (V is Building) { if (A != null && A.Alive && !IsWall(V)) Shout(V, A, true, false); return; }
        var vu = (Unit)V;
        if (vu.St.Camo.Has("entr")) DigOut(vu);
        if (V.Name == "ninigi_ninja") vu.DisgT = Time + 10;
        OnHurt(vu, A);
    }
    public bool Invulnerable(Entity V, bool damageOnly = false)
    {
        double t = Time;
        if (V.St.Active("levelInvul", t) || V.St.Active("doping", t)) return true;
        if (V.St.Active("fountain", t)) return true;                       // drank at a SEAS well (MIRAGE m_bFountainHealed: FountainOfYouth)
        if (!damageOnly && V.St.Active("invul", t)) return true;
        if (V.St.Active("peace", t)) return true;                          // ceasefire (MIRAGE FO:5744)
        if (V is Unit u)
        {
            if (u.Finishing && u.Task.Type == "finish") return true;       // during its finishing move (FinishingMove.usl:204)
            if (u.Zeppelin) return true;                                    // the trader's zeppelin: Damage() is empty (Vehicle.usl:852)
            return u.InvulnT > 0;
        }
        return V is Building b && b.InvulnT > 0;
    }
    /// <summary>retaliation (OnDefend / AddEnemy(defend=true)): fight back unless busy with an order or passive</summary>
    public void OnHurt(Unit u, Entity? att)
    {
        if (att == null || !att.Alive || !u.Alive || u.Inside) return;
        if (u.Wild) { AnimalHurt(u, att); return; }
        if (u.Stance == 3 || u.Stance == -1) return;
        if (u.Weapons.Long == null || u.Cs()!.Dmg <= 0 || u.CannotFight) { Shout(u, att, true, false); return; }
        var t = u.Task.Type;
        if (t == "attack" || t == "heal") return;
        if (u.Task.User && t != "idle" && t != "hold" && !(u.IsWorker && (t == "gather" || t == "build"))) return;
        if (t == "hold" || u.Stance == 0) { if (RangeZoneOf(u, att).Zone == 0) return; }
        if (u.IsWorker && (t == "gather" || t == "build") && (att.Kind != "unit" || u.DistTo(att) > 8)) return;
        EngageTarget(u, att, new EngageOpts { Defend = true, Hold = t == "hold" || u.Stance == 0 });
    }
    /// <summary>area damage (CAreaDamage FO:9431): enemies and neutral units within R; linear falloff from dmg to endDmg.
    /// Returns the number of victims.</summary>
    public int AreaDamage(Entity? A, V3 center, double R, double dmg, double endDmg, AreaOpts? opts = null)
    {
        opts ??= AreaOpts.None;
        if (R <= 0) R = 1;
        var owner = A != null ? A.Owner : opts.Owner;
        var victims = new List<(Entity V, double D)>();
        void Test(Entity V)
        {
            if (!V.Alive || V.Inside || V == A || V == opts.Skip) return;
            if (!opts.All && owner != null && V.Owner != null && !owner.IsEnemy(V.Owner)) return;      // hostile players and ownerless objects only (FO:9443)
            if (!opts.All && owner == null && V.Owner == null) return;
            double d = V is Building b ? Math.Max(0, b.SurfDist(center.X, center.Z)) : Math.Max(0, Hypot(V.Pos.X - center.X, V.Pos.Z - center.Z) - V.Radius);
            if (d >= R) return;
            victims.Add((V, d));
        }
        UHash.Query(center.X, center.Z, R + 20, Test);
        BHash.Query(center.X, center.Z, R + 30, Test);
        double e = dmg != 0 ? endDmg / dmg : 0;
        foreach (var (V, d) in victims)
        {
            double f = (1 - e) * (R - d) / R + e;
            if (A != null && A.Alive && !opts.Direct) TakeDmg(A, V, f, 0, opts.Weapon);
            else TakeDirectDmg(V, dmg * f, opts.Ap, owner, true, A);
            if (opts.Size > 0) ThrowUnit(V, center, opts.Size);
        }
        return victims.Count;
    }
    /// <summary>InvokeAreaThrow (FO:6117): a blast of size class `size` throws smaller characters and animals</summary>
    public void ThrowUnit(Entity V, V3 center, double size)
    {
        if (V is not Unit u || (V.Cls != "CHTR" && V.Cls != "ANML") || u.Flying || !(V.Stats!.Size > 0)) return;
        double diff = size - V.Stats.Size;
        if (diff >= 3) Knockback(u, center, SPLASH_DIST[(int)Math.Min(9, diff)] * (1 + Rng.Next() * 0.3), 0);
    }
    /// <summary>Penetrate (FO:5866): cone weapons (flamethrowers, lances) hit every enemy in the cone</summary>
    public void Penetrate(Unit A, Entity tgt, WeaponStats cs, double angleDeg)
    {
        foreach (var V in PenetratedBy(A, tgt, cs, angleDeg, false)) TakeDmg(A, V, 1, 0);
    }
    /// <summary>GetPenetratedObjs (FO:5900): the enemies in the cone towards tgt - nearer than AttackRange + radius + 2 + their
    /// inner radius, inside half the cone's angle (tgt itself at any angle). withTarget: the attack's own victim is
    /// hit in any case - the attack loop swings only at a victim inside the attack zone, and that zone is measured to its
    /// full radius (rangeZone), the cone to its inner one: a big victim standing at the edge of the zone was swung at
    /// for ever and never hit (the CBR titan, the sumo, the atroxosaurus against an elk)</summary>
    public List<Entity> PenetratedBy(Unit A, Entity tgt, WeaponStats cs, double angleDeg, bool withTarget)
    {
        var list = new List<Entity>();
        double R = AttackRangeOf(A, cs) + A.Radius + 2;
        double dir = SMath.Atan2(tgt.Pos.Z - A.Pos.Z, tgt.Pos.X - A.Pos.X);
        double half = Or(angleDeg, 30) * Math.PI / 360;
        void Test(Entity V)
        {
            if (!V.Alive || V.Inside || list.Contains(V)) return;
            if (withTarget && V == tgt) { list.Add(V); return; }
            if (!A.IsEnemy(V)) return;
            double dx = V.Pos.X - A.Pos.X, dz = V.Pos.Z - A.Pos.Z;
            double d = Hypot(dx, dz) - (V.Kind == "building" ? 0 : V.Radius * 0.5);
            if (d > R + (V.Kind == "building" ? V.Radius : 0)) return;
            if (Math.Abs(Geo.Wrap(SMath.Atan2(dz, dx) - dir)) > half && V != tgt) return;
            list.Add(V);
        }
        UHash.Query(A.Pos.X, A.Pos.Z, R + 6, Test);
        BHash.Query(A.Pos.X, A.Pos.Z, R + 20, Test);
        if (withTarget && !list.Contains(tgt) && tgt.Alive && !tgt.Inside) list.Add(tgt);
        return list;
    }
    /// <summary>poison: two slots, only the stronger one ticks (every 3 s, poison_tick_count times) - FO:223-335</summary>
    public void ApplyPoison(Unit V, double p, int n, Entity? A)
    {
        var st = V.St;
        var cur = st.Poison;
        if (cur == null || cur.Dmg <= p)
        {
            if (cur != null && (st.Poison2 == null || cur.Left >= st.Poison2.Left)) st.Poison2 = cur.Copy();
            st.Poison = new PoisonState { Dmg = p, Left = n, Next = cur != null ? cur.Next : Time + POISON_TICK, By = A };
        }
        else if (st.Poison2 == null || st.Poison2.Left <= n) st.Poison2 = new PoisonState { Dmg = p, Left = n, Next = Time + POISON_TICK, By = A };
    }
    partial void PoisonUpdate(Unit u)
    {
        var V = u;
        var st = V.St; double t = Time;
        if (st.Poison2 != null && st.Poison2.Next <= t) { st.Poison2.Left--; st.Poison2.Next += POISON_TICK; if (st.Poison2.Left <= 0) st.Poison2 = null; }
        var P = st.Poison;
        if (P == null || P.Next > t) return;
        P.Left--; P.Next += POISON_TICK;
        if (P.By?.Owner != null) V.LastDamager = P.By.Owner;
        Damage(V, P.Dmg * (V.St.Active("noHeal", t) ? 2 : 1), null);     // blood infection doubles poison (MIRAGE FO:14174)
        if (P.Left <= 0) { if (st.Poison2 != null) { var q = st.Poison2.Copy(); q.Next = t + POISON_TICK; st.Poison = q; } else st.Poison = null; st.Poison2 = null; }
    }
    /// <summary>a poison tick if one is due (public: tests, the campaign's own steps)</summary>
    public void PoisonTick(Unit u) { if (u.St.Poison != null) PoisonUpdate(u); }
    /// <summary>short stun + push (hit_back / getting_up). Moves call it for anything near (e.g. Mayor's jump); only a
    /// character / animal can be thrown (a building has no knock slide: the JS's V.knock on it did nothing)</summary>
    public void Knockback(Entity e, V3 from, double dist, double delay)
    {
        if (e is not Unit V) return;
        if (!V.Alive || V.Inside || V.Flying || V.OnWall != null || V.WJump != null || V.Climbing) return;     // (on a wall: FO:4383)
        double dx = V.Pos.X - from.X, dz = V.Pos.Z - from.Z, d = Hypot(dx, dz); if (d == 0) d = 1;
        Later(delay, "knock", new Args { ["V"] = V, ["vx"] = dx / d * dist / 0.4, ["vz"] = dz / d * dist / 0.4 });
    }
    /// <summary>SetTrapped(seconds): the unit can't act</summary>
    public void Trap(Entity V, double sec, string why = "trapped")
    {
        V.St.T["trapped"] = Math.Max(V.St.T.TryGetValue("trapped", out var x) ? x : 0, Time + sec);
        V.St.TrapWhy = why;
        if (V is Unit u) { u.Path = new NavPath(); u.Vel = V3.Zero; }
        // a stun ends a special move playing out (SetTrapped: SetTaskImmediate) - a throw only holds it up (the hit
        // reaction is an action of its own: the task goes on after it)
        if (V is Unit cu && why != "knock" && InAct(cu)) BreakAct(cu);
    }
    public void GrantBuildCost(Entity V, Player p)
    {
        var a = Data.Actions(V.Owner).Find(x => x.Kind == "Build" && x.Results.Count > 0 && x.Results[0].Obj == V.Name);
        if (a != null) p.Refund(new Cost { Food = a.Cost.Food, Wood = a.Cost.Wood, Stone = a.Cost.Stone });
    }

    // ------------------------------------------------------------------ towers and bunkers (CTower / CBunker)
    /// <summary>towers the player can aim: built, own, with a ranged weapon (bunkers too - their garrison shoots)</summary>
    public bool CanAimTower(Entity b) => b is Building bb && bb.Built && !b.Disarmed && b.Weapons.Long?.Projectile != null;

    /// <summary>turrets that shoot by themselves (BuildUpBase.usl CBuildUpWeapon with SetAdditionalWeapon: an aggression region
    /// of the weapon's range, AutoAttackEnemy; TransportObj.Fight hands the unit's target to them too): the titan's
    /// ballistas, the Workshop's gunner turrets. The weapon is the best one whose users list the turret's weapon class
    /// at the unit's level (SetWeaponClass / UpdateWeapon), the shot leaves from the turret's Proj link.
    /// Called every step for units with such turrets (UnitUpdate; the JS ported it but never called it, so the CBR titan's
    /// machine guns, the titans' ballistas and gatlings never fired).</summary>
    public void GunnerUpdate(Unit u, double dt)
    {
        var c = u.Comp;
        if (c == null || c.Gunners.Count == 0 || u.Owner == null || u.Disarmed || u.Active("peace")) return;
        var O = u.RulesOwner();
        foreach (var gp in c.Gunners)
        {
            var g = gp.Gun ??= new GunnerState();
            if (g.WSet == null || g.Lvl != u.Level) { g.WSet = Data.WeaponSetOf(gp.WClass!, u.Level, O); g.Lvl = u.Level; }
            var w = g.WSet?.Long;
            if (w?.Projectile == null) continue;
            var cs = Data.WeaponStatsOf(w, u.Name, O)!;
            double R = cs.Range + u.Radius;
            g.Cool -= dt;
            bool Ok(Entity? e) => e != null && e.Alive && CanTarget(u, e) && Reach(u, e) <= R && !(cs.MinRange != 0 && Reach(u, e) < cs.MinRange);
            var own = u.Task.Type == "attack" ? u.Task.Target : null;
            var e = Ok(own) ? own : Ok(g.Target) ? g.Target : null;
            if (e == null)
            {
                g.LookT -= dt;
                if (g.LookT > 0) continue;
                g.LookT = 0.5;
                e = FindTarget(u, R, new FindOpts { Filter = x => cs.MinRange == 0 || Reach(u, x) >= cs.MinRange, AllZones = true, Defend = true });   // (the turret's own range and minimum range, not the carrier's)
            }
            g.Target = e;
            if (e == null) continue;
            if (TurnPartTo(u, gp, e.Pos.X, e.Pos.Z, dt, 2.5) > 0.25 || g.Cool > 0) continue;
            g.Cool = AttackDuration(u, cs);
            gp.ShotT = Time;
            var at = (Models.Link(u.Gfx, gp.Link) ?? V3.Zero) + (gp.Spec?.OffsetModel ?? V3.Zero);
            var from = LinkToWorld(u.Pos, u.Heading, at);
            if ((Models.Link(gp.Gfx, "Proj") ?? Models.Link(gp.Gfx, "unnamed")) is V3 pl) from = from + LinkToWorld(V3.Zero, u.Heading + gp.Yaw, pl);
            if (gp.Anim != null) { var a = gp.Anim.Pick("attack_front", "gun_shoot"); if (a != null) gp.Anim.Play(a, new AnimOpts { Loop = false, Restart = true, Fade = 0 }); }
            Launch(u, from, e, w, cs);
        }
    }
    /// <summary>turn a turret part towards a world point (compose.js turnPart); returns the error left</summary>
    static double TurnPartTo(Unit u, AttachedPart t, double x, double z, double dt, double speed = 3)
    {
        double dx = x - u.Pos.X, dz = z - u.Pos.Z;
        if (dx * dx + dz * dz < 1e-6) return 0;
        double want = Geo.HeadingTo(dx, dz) - u.Heading;
        double diff = want - t.Yaw; diff = SMath.Atan2(SMath.Sin(diff), SMath.Cos(diff));
        double step = Math.Min(Math.Abs(diff), speed * dt);
        t.Yaw += Math.Sign(diff) * step;
        return Math.Abs(diff) - step;
    }
    /// <summary>can player p order an attack on t? Enemies, and wild (ownerless) animals</summary>
    public bool HostileTo(Player? p, Entity? t) => p != null && t != null && t.Alive && t.Kind != "res" && (t.Owner != null ? p.IsEnemy(t.Owner) : t.Kind == "unit");
    /// <summary>aim towers at a target (the player's attack order on towers); returns how many</summary>
    public int AimTowersAt(IEnumerable<Entity> list, Entity target)
    {
        int n = 0;
        foreach (var e in list)
            if (e is Building b && CanAimTower(b) && b.Owner != null && HostileTo(b.Owner, target) && CanTarget(b, target)) { b.ForcedTarget = target; b.TowerTarget = target; n++; }
        return n;
    }
    /// <summary>aim towers at a target (the player's attack order on towers); true = aimed</summary>
    public bool AimTowers(List<Entity> towers, Entity t) => AimTowersAt(towers, t) > 0;

    // ------------------------------------------------------------------ a tower's reach (the range circles: RangeRings)
    /// <summary>how far a shooting building reaches: its weapon's range (with the tribe's upgrades: WeaponStats) + half
    /// its radius, measured from its centre to the target's surface - what TowerUpdate fires at</summary>
    public static double TowerReach(WeaponStats cs, double radius) => cs.Range + radius * 0.5;

    /// <summary>a building's attack range now - (reach, minimum range; 0: none) as TowerUpdate fires (its current
    /// weapon and stats, upgrades included) - or null: it does not shoot (no projectile weapon: walls, gates, farms ...)</summary>
    public (double R, double Min)? TowerRange(Building b)
    {
        var w = b.Weapons.Long;
        if (w?.Projectile == null || b.Cs(w) is not { } cs) return null;
        return (TowerReach(cs, b.Radius), w.MinRange);
    }

    /// <summary>the attack range a building of this class would have, put down now by this player (the placement ghost):
    /// its level-1 weapon with the player's upgrades, its radius from the footprint as the Building constructor makes it
    /// (turned by rot); null: it does not shoot</summary>
    public (double R, double Min)? TowerRangeOf(string name, Player? owner, double rot = 0)
    {
        var w = Data.WeaponSetOf(name, 1, owner).Long;
        if (w?.Projectile == null || Data.WeaponStatsOf(w, name, owner) is not { } cs) return null;
        var def = Data.Def(name, owner);
        var gfx = Data.Stats(name, 1, owner)?.Gfx;
        var F = Footprint.Place(Models, gfx, 0, 0, def?.WallKind == "wall" ? Building.WALL_ROT : rot, 0, def?.WallKind == "wall" ? Building.WALL_BOXES : null);
        double radius = Math.Max(Math.Max(2, F.Hx), Math.Max(2, F.Hz)) * 0.85;     // (Building: Hx, Hz at least 2; Radius)
        return (TowerReach(cs, radius), w.MinRange);
    }

    // (a method of its own: the lambda's captures made TowerUpdate allocate on every call of every building)
    Entity? TowerFind(Building b, double R, Weapon w) => FindTarget(b, R, new FindOpts { Filter = x => w.MinRange == 0 || Reach(b, x) >= w.MinRange, Defend = true });
    partial void TowerUpdate(Building b, double dt)
    {
        var w = b.Weapons.Long;
        if (w?.Projectile == null || b.Owner == null || !b.Built || b.Dismantling != null || b.Disarmed || b.Active("peace") || (b.Behaviour is INoTowerBehaviour nt && nt.NoTower)) return;
        var cs = b.Cs(w)!;
        double R = TowerReach(cs, b.Radius);
        b.Cool -= dt;
        if (b.Cool > 0) return;
        // bunkers only shoot with characters inside (one arrow per character)
        bool bunker = b.Def!.Script == "CBunker";
        int shots = bunker ? PassengersOf(b)?.Count ?? 0 : 1;
        if (shots == 0) { b.Cool = 0.5; return; }
        // a target the player ordered (right-click / Attack): kept while it lives, shot whenever it is in range
        var f = b.ForcedTarget;
        if (f != null && (!f.Alive || !CanTarget(b, f))) b.ForcedTarget = null;
        bool InReach(Entity x) => Reach(b, x) <= R && !(w.MinRange != 0 && Reach(b, x) < w.MinRange);
        var e = b.ForcedTarget != null && InReach(b.ForcedTarget) ? b.ForcedTarget : b.TowerTarget;
        if (e == null || !e.Alive || !CanTarget(b, e) || !InReach(e)) e = TowerFind(b, R, w);
        b.TowerTarget = e;
        if (e == null) { b.Cool = 0.5; return; }
        // the turret turns first (SecRotAction 0.8 s), then fires on the next tick
        if (b.Turret != null && b.AimTurret(e.Pos.X, e.Pos.Z, dt) > 0.25) { b.Cool = 0; return; }
        b.Cool = AttackDuration(b, cs);
        var from = b.Pos; from.Y += b.Ht * 0.8;
        if (b.Turret != null)
        {
            var tp = b.Turret;
            if (b.LinkWorld(tp.Link) is V3 at) from = at + LinkToWorld(V3.Zero, b.Rot, tp.Spec?.OffsetModel ?? V3.Zero) + (Models.Link(tp.Gfx, "Proj") is V3 pl ? LinkToWorld(V3.Zero, b.Rot + tp.Yaw, pl) : V3.Zero);
            if (tp.Anim != null) tp.Anim.Play(tp.Anim.Pick(tp.AttackAnim ?? "attack_front", "attack_front", "gun_shoot") ?? "attack_front", new AnimOpts { Loop = false, Restart = true });
            TowerFired(b);                                  // (MIRAGE's great cannon: its barrel too)
        }
        double sd = w.Anims.Count > 0 ? Or0(w.Anims[0].ShootDelay) : 0;
        // a bunker's volley (CBunker.AttackEnemy, CArrow.SetXtraDelay): every arrow leaves after the projectile delay
        // × (1 + 0.1 i), one after the other
        for (int i = 0; i < shots; i++) Launch(b, from, e, w, cs, bunker ? sd * (1 + 0.1 * i) : 0);
    }

    /// <summary>
    /// V's melee defence bonus (percent) against an attacker of class cls (CHTR, ANML, VHCL, BLDG, SHIP) and name: its
    /// weapon's DefenseBonus × the tech tree's Defence_&lt;cls&gt; tec_rel + tec_abs (FightingObj.usl TakeDmg; the
    /// bony amulet, item_amulett1, adds 10 to Defence_ANML tec_abs). The remake added tec_abs only when the weapon had
    /// a bonus against that class, so the amulet did nothing for most units; the original adds it always. Ranged
    /// attacks get none (the original's TakeDmg; MIRAGE's RangedDef_&lt;cls&gt; modifiers are all 0 in its tree).
    /// The info window's armour tooltip shows these (HudModel.Info).
    /// </summary>
    public double DefenceBonusVs(Entity V, string cls, string? name = null)
    {
        var vm = V.Kind == "unit" ? Data.DefenceVs(V.Name, V.Owner, cls) : (Rel: 1.0, Abs: 0.0);
        var vw = V.Weapon ?? V.Weapons.Long;
        double db = vw != null ? (vw.DefBonus.TryGetValue(cls, out var d1) ? d1 : 0) + (name != null && vw.DefBonus.TryGetValue(name, out var d2) ? d2 : 0) : 0;
        return db * vm.Rel + vm.Abs;
    }

    /// <summary>the skulls player p gets for killing e (Kill: its value - a young animal's less -, the skull protector's
    /// none, Schliemann's × 1.15, then Player.AddScalps' rounding and scalps modifier - the flowerpot of insanity's 1.5)</summary>
    public double ScalpsFor(Entity e, Player p) => ScalpReward(p, KillScalps(e));
    double KillScalps(Entity e)
    {
        double v = ScalpValue(e);
        if (e.St.Aura.SkullProtect) v = 0;
        if (e.St.Aura.Kleemann != 0 && !double.IsNaN(e.St.Aura.Kleemann)) v *= e.St.Aura.Kleemann;
        return v;
    }

    // ------------------------------------------------------------------ death
    /// <summary>death of an entity (killer: who; null = no one): skulls, the item falls, the unit's or building's end</summary>
    public void Kill(Entity e, Entity? att)
    {
        if (e.Name == "ninigi_mineship_mine" && !e.Blasted && e.Alive && e is Unit mine) { MineBlast(mine); return; }   // a destroyed mine goes off (combat.js:783)
        if (!e.Alive) return;
        e.Alive = false;
        e.Hp = 0;
        // skulls for the player who damaged it last (OnKill FO:3950); skull protector aura: none, Kleemann aura: +15 %
        var killer = e.LastDamager ?? att?.Owner;
        if (killer != null && killer != e.Owner && e.Stats != null && e.Stats.Scalps != 0 && !double.IsNaN(e.Stats.Scalps) && (e.Owner != null || e.Kind == "unit"))
        {
            AddScalps(killer, KillScalps(e));                            // (Player.AddScalps: the scalps modifier)
            if (e.Owner != null || e.Cls == "ANML") killer.Kills++;
        }
        if (e is Unit iu && iu.Item != null) DropItem(iu);               // the item falls (FightingObj.Delete)
        EntityFilters(e, false);
        // (the JS's e.onDeath hook: nothing sets it)
        if (e.Cls == "ANML" && e.Kind == "unit") AddScare(e.Pos.X, e.Pos.Z);      // Animal.OnKill
        if (e is Unit u) UnitDied(u, att);
        if (e is Unit su && su.Driver != null && !su.SilentDeath) SuitDestroyed(su);   // (MIRAGE: Babbage's exo suit lets its driver out, combat.js:801)
        if (e is Building b) BuildingDestroyed(b, att);
        Dead.Add(e);
    }

    static readonly string[] NOT_WRECKS = { "CAmphibian", "CMuraenoSubmarine", "CAjeCronosaurus", "CTorpedoTurtle", "CWaterMine" };
    /// <summary>a ship leaves its wreck "&lt;gfx&gt;_dest" playing "destroy" (Ship.usl Die -> CreateShipCorpse)</summary>
    void ShipWreck(Unit e)
    {
        if (PirateWreck(e)) return;                       // (MIRAGE's pirate boss ship: its parts' wrecks, PirateShip.cs)
        if (!Data.IsA(e.Name, "CShip") || NOT_WRECKS.Any(c => Data.IsA(e.Name, c))) return;
        var gfx = e.Stats?.Gfx; var tpl = gfx != null ? (gfx + "_dest").ToLowerInvariant() : null;
        if (tpl == null || !Models.Has(tpl)) return;
        var clips = Models.Clips(tpl);
        var anim = clips.Count > 0 ? new AnimCtl(clips, null) : null;
        var clip = anim?.Pick("destroy", "dying");
        if (clip != null) anim!.Play(clip, new AnimOpts { Loop = false });
        e.Wreck = new Wreck { Gfx = tpl, Anim = anim };
        e.LookChanged();
    }

    /// <summary>GetDirection (FO:3806) of a point seen from the unit: 'f' front, 'b' back, 'l' left, 'r' right, by the
    /// larger of the two components in the unit's frame. The engine's DIR_LEFT is the model's local +X (FO:3812): the
    /// converter mirrors the GSF frame's x (pwexport gsf.py: GSF (x, y, z) -> glTF (-x, z, -y)), so it is the unit's
    /// own left here too (facing -z at heading 0, its right is +x).</summary>
    public static char HitSide(Unit u, V3 from)
    {
        double dx = from.X - u.Pos.X, dz = from.Z - u.Pos.Z;
        if (dx * dx + dz * dz < 1e-8) return '\0';
        // the unit faces (-sin h, -cos h) (Geo.HeadingTo); its right is (-fz, fx)
        double fx = -SMath.Sin(u.Heading), fz = -SMath.Cos(u.Heading);
        double fwd = dx * fx + dz * fz, side = dx * -fz + dz * fx;
        return Math.Abs(side) > Math.Abs(fwd) ? (side > 0 ? 'r' : 'l') : fwd > 0 ? 'f' : 'b';
    }

    /// <summary>held by a trap or a stun (FightingObj m_xTrap valid: SetTrapped by a trap building or for a time, FO:2271-2300);
    /// the remake's throw ("knock": the original's SetHitReactionTimer) and the ice spearman's freeze (SetIced) are not</summary>
    bool InTrap(Unit u) => u.Active("trapped") && u.St.TrapWhy is not ("knock" or "iced");

    /// <summary>the fight clips that can cut a "res" victim's head off (CCharacter.DiePerHeadOff, character.usl:2254-2259;
    /// MIRAGE's character.usl:3609-3614 the same)</summary>
    static readonly HashSet<string> HEAD_OFF_STRIKES = new(StringComparer.Ordinal) { "res_strike_13", "res_strike_15", "res_strike_1", "res_strike_7", "res_strike_12" };

    /// <summary>
    /// CCharacter.DiePerHeadOff (character.usl:2252-2270, called from CreateCorpse 2297): the killer is a character
    /// (cast&lt;CCharacter&gt;(m_xLastEnemy)), the victim no statue, Random % 3 == 0, the killer's fight clip one of
    /// HEAD_OFF_STRIKES and the victim's caste "res": an object "&lt;class&gt;_head" is made at the victim's place and turn
    /// and given to the corpse (SetHead). False when the class has no "_head" class (CreateObj fails: no head, no chop_1).
    /// The draw: the original takes Random.GetInt() here for every character a character kills; the remake takes it from
    /// a stream of its own (HeadOffRoll) so that World.Rng - and with it every other random choice - keeps its order.
    /// </summary>
    bool HeadOff(Unit e, Entity? killer)
    {
        if (killer is not Unit k || k.Cls != "CHTR" || e.Name.Contains("statue", StringComparison.OrdinalIgnoreCase)) return false;
        if (HeadOffRoll(e) % 3 != 0) return false;
        if (k.FightAnim == null || !HEAD_OFF_STRIKES.Contains(k.FightAnim) || e.Def?.Caste != "res") return false;
        var cls = e.Name.ToLowerInvariant() + "_head";
        var gfx = Data.ClassGfx(cls);
        if (gfx == null) return false;
        // (a broken entry of the converted class table - text after the name: miyagi_s0_head - is the class's own name)
        if (gfx.AsSpan().IndexOfAny(" \t\r\n") >= 0) gfx = cls;
        e.Head = gfx;
        e.LookChanged();
        return true;
    }

    /// <summary>the beheading's draw (DiePerHeadOff's Random.GetInt()): a number from the world's seed, the victim and the
    /// step it died in (SplitMix64) - the same in every run and replay of the game, without a draw from World.Rng</summary>
    public ulong HeadOffRoll(Unit e)
    {
        ulong z = Opts.Seed * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)e.Id << 32) ^ (ulong)StepNo;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// The clip a dead character's corpse plays (CCharacter.CreateCorpse, character.usl:2272-2338; MIRAGE 3644-3735):
    /// killed in a hit reaction ("hit_back" of a throw, "hit_front" of a fall: a clip named hit_*) the corpse holds that
    /// clip's last frame at once (SetStillAnim(clip, -1) -> CCharacterCorpse.PlayAnim SetAnim(clip, 0 = AM_Still, -1): a
    /// still frame - the client shows the frame it is sent, PWClient 0x7a97b0 gives mode 0 no time - and -1 the last
    /// one: the clips end lying on the ground, getting_up starts there; docs/SIMULATION.md) - null; killed by its own player (Del: DiePerHarakiri) "harakiri"; on a wall "hit_rotate_s"; else
    /// by the side the last blow came from: the back "dying", a side or no blow "die_simple", the front "hit_fw" (the
    /// characters of all_characters.gsf have it since the toolkit keeps it; a model without it: "dying").
    /// The head cut off (DiePerHeadOff, character.usl:2297-2299): after the Del case, before the side of the blow - the
    /// head object (Unit.Head, see HeadOff) and "chop_1"; on a wall "hit_rotate_s" all the same (2310: the wall's clip
    /// overrides, the head stays). Dead in the snare trap (2300-2306: m_xTrap's GetDieInTrapAnim, only CSnareTrap names
    /// one): "snare_trap_dying", after the head. Animals, vehicles and the rest: "dying" / "die_simple" as before.
    /// </summary>
    string? DeathClip(Unit e, Entity? killer)
    {
        var A = e.Anim!;
        if (e.Cls != "CHTR") return A.Pick("dying", "die_simple", "hit_die_simple") ?? e.IdleAnim;
        if (A.CurName != null && A.CurName.StartsWith("hit_", StringComparison.Ordinal)) return null;
        if (e.SelfKill && A.Pick("harakiri") is { } hk) return hk;
        bool head = !e.SelfKill && HeadOff(e, killer);
        if (e.OnWall != null && A.Pick("hit_rotate_s") is { } hr) return hr;
        if (head && A.Pick("chop_1") is { } chop) return chop;
        if (InTrap(e) && e.St.TrapWhy == "snare" && A.Pick("snare_trap_dying") is { } snare) return snare;
        string? byDir = e.LastHitDir switch { 'b' => A.Pick("dying"), 'f' => A.Pick("hit_fw", "dying"), _ => A.Pick("die_simple") };
        return byDir ?? A.Pick("dying", "die_simple", "hit_die_simple") ?? e.IdleAnim;
    }

    void UnitDied(Unit e, Entity? att)
    {
        ReleaseTask(e);
        SiegeGone(e);              // a docked siege tower frees its wall piece
        UHash.Remove(e);
        if (e.Passengers != null) UnloadAll(e, true);
        if (e.Inside) LeaveTransport(e);
        if (e.Owner != null)
        {
            var p = e.Owner;
            if (e.CountsInPop) { p.Units--; p.AtLevel[e.Level - 1]--; }
            p.Lost++;
            if (e.Def!.Unique) p.Heroes.Delete(e.Name);
            // (the queue's refunds: production.js cancelQueue does the same for a unit's queue entries)
            int n = e.Queue.Count;
            for (int i = 0; i < n && e.Queue.Count > 0; i++) CancelQueue(e, 0);
            e.Queue = new List<QueueItem>();
            RecomputeCaps(p);
        }
        e.Path = new NavPath(); e.Task = new UnitTask("dead");
        e.DeadT = 0;
        // (the victim of a finishing move plays its part of the move: systems/finishing.js)
        var fm = e.FinishMove;
        if (fm?.Anim != null) e.Anim!.Play(fm.Anim, new AnimOpts { Loop = false, Restart = true, Fade = 0.05 });
        else if (DeathClip(e, att ?? e.LastAttacker) is { } dc) e.Anim!.Play(dc, new AnimOpts { Loop = false, Fade = 0.1 });
        else if (e.Anim!.CurName is { } hit) e.Anim.Still(hit, e.Anim.Duration(hit));    // SetStillAnim(clip, -1): its last frame, at once
        e.AttachTool(null);
        e.Comp?.Died();                                   // its riders, catapult, turrets stop (Composite.Died)
        // a ship leaves its wreck (Ship.usl Die -> CreateShipCorpse): the model "<gfx>_dest" at destruction level 2,
        // playing its sink animation "destroy" once, gone after 10 s; the ship itself is out at once. Ships that are
        // animals (turtles, the submarine, the cronosaurus) and mines keep their own model and dying animation.
        if (e.Naval && !e.SilentDeath) ShipWreck(e);
        e.WJump = null; e.Climbing = false; if (e.WallH != null) { e.Pos.Y = e.WallH.Value; e.WallH = null; }
        // owned characters and animals leave a spirit that an Aje shaman can resurrect (not illusions, summons; heroes
        // and the special eusmilus only on multiplayer levels - skirmish -, FightingObj.usl:5566-5571)
        bool hero = Data.IsA(e.Name, "CHero") || e.Name == "special_eusmilus";
        if (e.Owner != null && (e.Cls == "CHTR" || e.Cls == "ANML") && !e.Illusion && !e.Autonomous && !e.SilentDeath && e.Finish == null && (!hero || Opts.Multiplayer))
            Spirits.Add(new Spirit { Name = e.Name, Level = e.Level, Owner = e.Owner, Pos = e.Pos, Alive = true, Until = Time + 60 });
        // animals leave a carcass: wild kills can be eaten for 120 s (Resource.usl CDino_Food); every animal a finishing
        // move kills does (FinishingMove.usl: <class>_food)
        if (e.Cls == "ANML" && (e.Wild || e.Finish != null))
        {
            double food = Data.CorpseFood(e.Name);
            var n = AddResource("corpse", "food", e.Pos.X, e.Pos.Z, food, new ResOpts { Radius = Math.Max(1.5, e.Radius), MaxWorkers = 5, Extra = new Args { ["corpseOf"] = e, ["hunt"] = e.Name } });
            n.RotT = 120;
            e.CorpseNode = n;
        }
        Emit("died", new Args { ["entity"] = e, ["killer"] = att });
    }

    /// <summary>may unit / tower u be ORDERED to attack t? Hostile and ownerless targets: yes. A neutral player's object: yes -
    /// the order makes both players hostile (DeclareWar). Never if either player is a friend of the other
    /// (FightingObj.usl:6594-6603, single player). A skirmish (multiplayer level): anyone may be attacked, except a friend
    /// while the diplomacy is locked (FightingObj.usl:6604-6612). (scripting.js attackAllowed)</summary>
    public bool AttackAllowed(Entity? u, Entity? t)
    {
        if (u == null || t == null || !t.Alive || t == u || t.Kind == "res") return false;
        Player? a = u.Owner, b = t.Owner;
        if (a == null || b == null) return true;
        if (a == b) return false;
        if (Opts.Multiplayer) return !(Opts.DiplomacyLocked && a.IsFriend(b));
        return !(a.IsFriend(b) || b.IsFriend(a));
    }

    // ------------------------------------------------------------------ the delayed parts of combat (World.Later)
    // a unit called for help joins the fight if it is still free
    [Delayed("helpEngage")]
    static void HelpEngage(World W, Args a)
    {
        var o = a.U("o");
        if (o != null && o.Alive && (o.Task.Type == "idle" || o.Task.Type == "hold" || o.Task.Type == "roam")) W.EngageTarget(o, a.E("enemy"), new EngageOpts { Defend = a.Bool("defend"), Hold = o.Stance == 0 });
    }
    // the velociraptor hatched from a dino ammo egg leaves after 30 s
    [Delayed("hatchlingGone")]
    static void HatchlingGone(World W, Args a) { var u = a.U("u"); if (u != null && u.Alive) { u.SilentDeath = true; W.Kill(u, null); } }
    // a hit landing after the attack animation's delay
    [Delayed("hit")]
    static void HitDelayed(World W, Args a)
    {
        var V = a.E("V");
        if (V != null) W.ProvideDmg(a.Num("dmg"), a.E("A"), V, a.Bool("proj"), a.Num("poison"), a.Num("ticks"), a.Get<Weapon>("w"));
    }
    [Delayed("knock")]
    static void KnockDelayed(World W, Args a)
    {
        var V = a.U("V");
        if (V == null || !V.Alive) return;
        V.Knock = new Knock { Vx = a.Num("vx"), Vz = a.Num("vz"), T = 0.4 };
        W.Trap(V, 1.2, "knock");
        var an = V.Anim!.Pick("hit_back");
        if (an != null) { V.BusyAnim = true; V.Anim.Play(an, new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone("knockGetUp") }); }
    }
    // after hit_back: getting up, then free again
    [AnimDone("knockGetUp")]
    static void KnockGetUp(World W, Entity e, Args? a)
    {
        if (e is not Unit V) return;
        var g = V.Anim!.Pick("getting_up");
        if (g != null && V.Alive) V.Anim.Play(g, new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone("busy") }); else V.BusyAnim = false;
    }
}
