// MIRAGE's pirate boss ship (CPirateShip, ninigi_pirate_boss_ship - the Dragon Clan's titan from its launch pad) and its
// parts: the tail (CPirateShipTail), the sail (CPirateShipSail), the rowing deck (CPirateShipRow) and the cannon deck.
//
// Reference (MIRAGE, Data/MIRAGE/Scripts/Server): classes/mirage/Mirage.usl CPirateShip (7903-8426), CPirateShipBuildUp
// and its classes (8427-8630), CPirateShipCorpse (8631-8795), CPirateShipArrow (13626); mirage_buildings.txt (the parts'
// models: pirate_boss_tail / _sail / _row / _cannons / _cannons_reduced); the tech tree's
// ninigi_pirate_boss_ship_weapon_a (damage 10, hitrange 15, range 85, every 6 s, projectile pirate_ship_arrow).
//
// * BuildParts: the hull (pirate_boss) carries its tail at the link psh1, its sail at psh2, its rowing deck at psh3 and
//   the cannon deck (pirate_boss_cannons) at psh2. The parts are objects of their own in the original (selectable,
//   hitable, but they take no damage and pass every order to the ship); here they are the ship's attached parts
//   (GameData: the composites of every CPirateShip class), so a click on them is a click on the ship.
// * UpdateDestructionFlags: the parts show the ship's damage - above 50 % of its hit points whole, above 25 % at
//   destruction level 1, else 2.
// * AttackEnemy: a volley of 14 pirate_ship_arrow, one from each of the cannon deck's links D_01 .. D_14, aimed at the
//   target's position moved by that link's place along the ship (its y, the deck's length) and a random -5 .. +5 m
//   across it (its x), turned as the ship; each lands where it was aimed and hits what is there with the weapon's area
//   (hitrange 15 m: 10 damage each) - 14 times the weapon's damage a volley (UpdateWeaponAttribs shows the damage x 14).
//   The cannon deck plays "attack_front". (GetProjectileDelay: each arrow a little earlier or later, -0.1 .. +0.09 s.)
// * AttackEnemy, the broadside: SetAttackDirection(Pi/2) - the ship fires over its side. While the target is more than
//   45 degrees off its broadside (GetAngleToR > Pi/4) it turns (RotateActionSpeed(GetAngleToQ, 0.20)) until its side
//   faces the target, and only then fires (PirateBroadside). The side: the cannon deck's links D_01 .. D_14 are all on
//   the model's right (GSF x < 0: GetDirection's DIR_RIGHT; three.js +x), so the ship shows the target its right side.
//   The rate 0.20: RotateActionSpeed's speed, taken as radians per second (a 90 degree turn in 7.9 s; the engine's unit
//   is not documented - the base game's ship turns at 0.07, scripted ones at 100 = at once).
// * SetLevel, the level-up pause: the ship stops what it does (StopEverything, TerminateAction) and obeys no order but
//   Kill for 9 s (TIMER_LEVELING; ReactToGamePlayCommand) while its parts are rebuilt (here the composites follow the
//   level by themselves). Its own automatic fights (AttackSrv, not a command) are not held back, as in MIRAGE.
// * Die -> CreateShipCorpse, CPirateShipCorpse (PirateWreck; drawn by the view's EntityViews.Wrecks): the parts' own
//   wrecks at the parts' places (tail, row, sail at destruction level 2, the cannon deck), the hull's wreck hidden.
//   DieTail: the tail's wreck plays "destroy" at once, the row's 1.33 s and the sail's 2.15 s later; at 4.2 s the cannon
//   deck gives way to its reduced wreck (DeleteCannons); when the tail's clip is 5 s from its end (its length, whole
//   seconds, - 5: 7 s with MIRAGE's 12 s clip) the parts' wrecks go and the hull's wreck (pirate_boss_dest) shows and
//   plays "destroy" (16 s), gone 17 s later (CUniversalCorpse.Init duration 17).
// Not ported (dead code in MIRAGE): the reduced cannon deck after the tail is lost and the parts' own deaths
//   (ReplacePart, CheckDeath, m_bDeadBoat: the parts never die - their Die is commented out -, so m_bDeadBoat, now read
//   back on load by MIRAGE master, is always false and the ship always fires).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public sealed partial class World
{
    /// <summary>the composites' kind of the pirate boss ship's parts (GameData.PirateShipParts)</summary>
    public const string PIRATE_PART = "shippart";
    const int PIRATE_ARROWS = 14;
    const double PIRATE_SPREAD = 5;

    public static bool IsPirateShip(Unit u) => u.Def?.Script == "CPirateShip";

    /// <summary>UpdateDestructionFlags: the parts' destruction level by the ship's hit points</summary>
    public static int PirateDamage(double hp, double max)
    {
        if (hp == 0 || max == 0) return 0;
        double pc = hp / (max / 100);
        return pc > 50 ? 0 : pc > 25 ? 1 : 2;
    }

    /// <summary>CPirateShip.AttackEnemy with a projectile: the 14 arrows of the cannon deck (false: not such a ship)</summary>
    bool PirateVolley(Unit u, Weapon w, WeaponStats cs, Entity tgt)
    {
        if (!IsPirateShip(u) || w.Projectile == null) return false;
        var deck = u.Comp?.Parts.FirstOrDefault(p => p.Spec?.Kind == PIRATE_PART && p.Gfx.StartsWith("pirate_boss_cannons", StringComparison.Ordinal));
        string deckGfx = deck?.Gfx ?? "pirate_boss_cannons";
        var deckAt = Models.Link(u.Gfx, "psh2") ?? V3.Zero;
        bool deckKnown = Models.File(deckGfx) != null;
        if (deck?.Anim != null && deck.Anim.Has("attack_front")) deck.Anim.Play("attack_front", AnimOpts.Once());
        for (int i = 1; i < PIRATE_ARROWS + 1; i++)
        {
            var l = Models.Link(deckGfx, "D_" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
            // (without the model's links: the 14 spread evenly along the deck, 12 m fore and aft; a link the deck lacks -
            // D_10 on pirate_boss_cannons - leaves GetLinkPosObj's vector at zero: mid-ship)
            double along = l is V3 lv ? lv.Z : deckKnown ? 0 : -12 + (i - 1) * 24.0 / (PIRATE_ARROWS - 1);
            double across = Math.Floor(Rng.Next() * 1000) * 0.01 - PIRATE_SPREAD;
            var off = Combat_LinkToWorld(V3.Zero, u.Heading, new V3(across, 0, along));
            var aim = new V3(tgt.Pos.X + off.X, tgt.Pos.Y, tgt.Pos.Z + off.Z);
            aim.Y = Height(aim.X, aim.Z);
            var from = Combat_LinkToWorld(u.Pos, u.Heading, deckAt + (l ?? new V3(0, 3, along)));
            var p = new Projectile(this, from, tgt, w, u, aim);
            p.Snap = new ProjectileSnap { Dmg = cs.Dmg, EndDmg = cs.EndDmg, Splash = cs.Splash, Ap = cs.Ap, Size = w.Size };
            p.Delay = Math.Floor(Rng.Next() * 20) * 0.01;        // (GetProjectileDelay: -0.1 .. +0.09 s about the shot)
            Projectiles.Add(p);
        }
        Emit("shoot", new Args { ["attacker"] = u, ["weapon"] = w, ["pos"] = Combat_LinkToWorld(u.Pos, u.Heading, deckAt) });
        return true;
    }
    static V3 Combat_LinkToWorld(V3 origin, double heading, V3 l) => LinkToWorld(origin, heading, l);

    // ------------------------------------------------------------------ the broadside (CPirateShip.AttackEnemy)
    /// <summary>the rate the ship turns to its broadside (RotateActionSpeed(qRot, 0.20), rad/s)</summary>
    public const double PIRATE_TURN = 0.20;
    /// <summary>the heading that shows the point (x, z) the ship's right side (SetAttackDirection(Pi/2), GetAngleToQ)</summary>
    public static double BroadsideHeading(Unit u, double x, double z) => Geo.Wrap(Geo.HeadingTo(x - u.Pos.X, z - u.Pos.Z) + Math.PI / 2);

    /// <summary>
    /// The pirate ship's aim (Combat's attack loop): the angle between its broadside and the target - more than 45 degrees
    /// (GetAngleToR &gt; Pi/4) and it turns (RotateActionSpeed) until its side faces the target exactly, without firing
    /// meanwhile (the rotate action runs to its end: Pi is returned while turning).
    /// </summary>
    double PirateBroadside(Unit u, Entity tgt, double dt)
    {
        // (a turn left off - the attack ended, another order - is not taken up again: TerminateAction)
        if (u.BroadsideAt < Time - 2 * dt - 1e-9) u.BroadsideTurn = false;
        u.BroadsideAt = Time;
        double diff = Geo.Wrap(BroadsideHeading(u, tgt.Pos.X, tgt.Pos.Z) - u.Heading);
        if (!u.BroadsideTurn && Math.Abs(diff) <= Math.PI / 4) return Math.Abs(diff);
        u.BroadsideTurn = true;
        double step = PIRATE_TURN * dt;
        if (Math.Abs(diff) <= step) { u.Heading = Geo.Wrap(u.Heading + diff); u.BroadsideTurn = false; return 0; }
        u.Heading = Geo.Wrap(u.Heading + Math.Sign(diff) * step);
        return Math.PI;
    }

    // ------------------------------------------------------------------ the level-up pause (CPirateShip.SetLevel)
    /// <summary>TIMER_LEVELING: 9 s without orders after a level-up</summary>
    public const double PIRATE_LEVEL_PAUSE = 9;
    /// <summary>SetLevel: StopEverything, TerminateAction, TIMER_LEVELING (Production.ApplyLevel)</summary>
    void PirateLevelUp(Unit u)
    {
        if (!IsPirateShip(u)) return;
        ReleaseTask(u);
        u.Task = UnitTask.Idle();
        u.Path.Clear();
        u.Vel = V3.Zero;
        u.Pending = null;
        u.BroadsideTurn = false;
        u.St.T["pirateLevel"] = Time + PIRATE_LEVEL_PAUSE;
    }
    /// <summary>ReactToGamePlayCommand while TIMER_LEVELING runs: every order but Kill is ignored (World.Order)</summary>
    public static bool PirateLevelPause(Unit u, Order o) => o.Type != "kill" && u.Active("pirateLevel");

    // ------------------------------------------------------------------ the wrecks (CreateShipCorpse, CPirateShipCorpse)
    /// <summary>CPirateShipCorpse's timers (s after the death): the row's and the sail's clips, the cannon deck's swap</summary>
    public const double PIRATE_ROW_AT = 1.33, PIRATE_SAIL_AT = 2.15, PIRATE_CANNONS_AT = 4.2;
    /// <summary>the hull's corpse lasts this long once it shows (CPirateShip.CreateShipCorpse: Init(.., 17.0, ..))</summary>
    public const double PIRATE_HULL_LIFE = 17;
    /// <summary>the tail wreck's clip "destroy" when the model data is missing: MIRAGE's pirate_boss_tail_dest (12 s)</summary>
    const double PIRATE_TAIL_CLIP = 12;

    string PirateGfx(string cls, string d) => Data.ClassGfx(cls)?.ToLowerInvariant() ?? d;

    /// <summary>
    /// CPirateShip.Die -> CreateShipCorpse: the parts' wrecks where the parts were and the hull's wreck, hidden until the
    /// tail's has nearly sunk (CPirateShipCorpse.DieTail / HandleEvent). False: not a pirate ship (an ordinary wreck).
    /// </summary>
    bool PirateWreck(Unit e)
    {
        if (!IsPirateShip(e)) return false;
        string hull = PirateGfx("ninigi_pirate_ship_dest", "pirate_boss_dest");
        // (the tail's clip length in whole seconds: GetCurrentAnimLength().ToInt(); the timer only when it is > 0)
        var tailGfx = PirateGfx("ninigi_pirate_ship_tail_dest", "pirate_boss_tail_dest");
        double tailLen = Models.File(tailGfx) != null ? Models.Clips(tailGfx).Duration.GetValueOrDefault("destroy") : PIRATE_TAIL_CLIP;
        double main = Math.Max(0, Math.Floor(tailLen) - 5);
        AnimCtl? anim = null;
        if (Models.Has(hull))
        {
            var clips = Models.Clips(hull);
            anim = clips.Count > 0 ? new AnimCtl(clips, null) : null;
            var clip = anim?.Pick("destroy", "dying");
            if (clip != null) anim!.Play(clip, new AnimOpts { Loop = false });
        }
        var w = new Wreck { Gfx = Models.Has(hull) ? hull : null, Anim = anim, From = tailLen > 0 ? main : double.PositiveInfinity, Parts = new() };
        w.Until = w.From + PIRATE_HULL_LIFE;
        WreckPart Part(string cls, string d, string link, int dmg, double from, double until, double play)
        {
            var at = LinkToWorld(e.Pos, e.Heading, Models.Link(e.Gfx, link) ?? V3.Zero);
            return new WreckPart { Gfx = PirateGfx(cls, d), Link = link, Pos = at, Heading = e.Heading, VisDmg = dmg, From = from, Until = until, PlayAt = play };
        }
        // (m_xParts: tail, row, sail - each at destruction level 2 -, then the cannon deck's two wrecks)
        w.Parts.Add(Part("ninigi_pirate_ship_tail_dest", "pirate_boss_tail_dest", "psh1", 2, 0, w.From, 0));
        w.Parts.Add(Part("ninigi_pirate_ship_row_dest", "pirate_boss_row_dest", "psh3", 2, 0, w.From, PIRATE_ROW_AT));
        w.Parts.Add(Part("ninigi_pirate_ship_sail_dest", "pirate_boss_sail_dest", "psh2", 2, 0, w.From, PIRATE_SAIL_AT));
        w.Parts.Add(Part("ninigi_pirate_ship_cannons_dest", "pirate_boss_cannons", "psh2", 0, 0, Math.Min(PIRATE_CANNONS_AT, w.From), double.NaN));
        w.Parts.Add(Part("ninigi_pirate_ship_cannons_reduced_dest", "pirate_boss_cannons_reduced", "psh2", 0, PIRATE_CANNONS_AT, w.From, double.NaN));
        e.Wreck = w;
        e.LookChanged();
        return true;
    }
}

public sealed partial class Unit
{
    /// <summary>the pirate ship turns to its broadside (PirateBroadside); the step it last aimed at</summary>
    public bool BroadsideTurn;
    public double BroadsideAt = double.NegativeInfinity;
}

public sealed partial class Wreck
{
    /// <summary>the wreck shows from this time after the death on (DeadT; the pirate ship's hull: when its tail has
    /// nearly sunk) and is gone at Until (CUniversalCorpse: 10 s for a ship)</summary>
    public double From, Until = 10;
    /// <summary>the wrecks of a ship's parts, each an object of its own (the pirate ship: CPirateShipCorpse); null: none</summary>
    public List<WreckPart>? Parts;
    /// <summary>is the wreck itself shown dt seconds after the death</summary>
    public bool ShownAt(double deadT) => deadT >= From && deadT < Until;
}

/// <summary>A part's wreck (CPirateShipCorpse): its model at its place, shown From .. Until seconds after the death,
/// playing "destroy" from PlayAt on (NaN: no clip - it stands).</summary>
public sealed class WreckPart
{
    public string Gfx = "", Link = "";
    public V3 Pos;
    public double Heading, From, Until, PlayAt = double.NaN;
    public int VisDmg;
    public bool ShownAt(double deadT) => deadT >= From && deadT < Until;
}

