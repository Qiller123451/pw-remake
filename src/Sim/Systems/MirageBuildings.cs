// MIRAGE buildings the browser remake never had: the SEAS healing well (CSeasWell, seas_outpouring) and the titans'
// launch pads (CSeasScorpionLaunchpad seas_final_boss, CHuAvatarLaunchpad hu_launchpad_colossi,
// CNinigiPirateBossLaunchpad ninigi_launchpad_pirate_boss), which turn into their titan when finished.
//
// Reference (MIRAGE, Data/MIRAGE/Scripts/Server): classes/mirage/mirage_buildings.txt; classes/mirage/Mirage.usl
// CSeasWell (12508-12575), CSeasScorpionLaunchpad (11513-11700), CHuAvatarLaunchpad (4418-4580),
// CNinigiPirateBossLaunchpad (11283-11512); classes/mirage/Miracle.usl CSeasWellHeal (1887-2010, the task "SWHeal");
// classes/FightingObj/FightingObj.usl SeasWellHeal (12632), the energy ("mana") of a building: SetManaOnInit /
// SetMana / UpdateEnergy / StartReplenishTimer / Replenish / IncreaseEnergy (12431-13270), HealMe / FullHeal (11548-
// 11590), FountainOfYouth (13342), m_bFountainHealed in Damage / PoisonDamage / ChainDamage / WoundDamage; the client's
// GameInputController.usl (MIRAGE master: the "healing" cursor and the "/SeasWellHeal" command on seas_outpouring).
//
// The SEAS well (tech tree /Objects/SEAS/BLDG/seas_outpouring: starting_energy 1000, max_energy 2000, replenish_step 2,
// replenish_time 1, mana_user 1; built by the SEAS worker from epoch 4 for 250 wood and 100 stone, at most 8):
// * it holds energy, starting_energy from the moment its site is placed (SetManaOnInit); once finished it refills
//   replenish_step every replenish_time seconds up to max_energy (2 a second: 1000 -> 2000 in 500 s).
// * own and allied characters, animals and vehicles (not ships, not flying units, not blood-infected ones) are sent to
//   it with a right click (task "fountain" on the building). Within the unit's radius + 7 m of its centre the well gives
//   it what it lacks - FullHeal when the energy covers it, else all the energy that is left (HealMe, with the tribe's
//   Healing_Bonus) - and that much energy is gone.
// * "fountain of youth": a unit that drank takes no damage for 0.1 s per hit point healed (300 healed: 30 s) and is
//   cured of poison (Detoxification).
// * nothing happens to units that only stand near it; a site (not finished) heals nobody (workers right-clicking it build).
// * the model's "fill_anim" plays when somebody drinks (InvokeGenericSCEvent 23).
//
// The launch pads: a worker builds the pad (the titan's model as a site); finished, it can no longer be selected or hit,
// and is replaced by the titan - a unit at the pad's level (the build action's result level: 5), at its place and
// rotation; the pad goes without a ruin. The Scorpion's pad waits 1 s, plays "stand_up" once and then turns into
// seas_scorpion; the colossus comes at once; the pirate boss ship at the pad's link "Bl_1" (on the water).
// The pads count in the unit limit from the moment the site is placed (DoesCountInUnitLimit: the card in the pyramid
// that the titan takes over - "clientpyramid_replacecard"); they cannot be set on fire (IgniteBuilding is empty) and
// leave no ruin.
// The build-up decorations (CHuAvatarLaunchpad / CNinigiPirateBossLaunchpad OnInit and SetProcess; MIRAGE's
// mirage_misc.txt gives their models):
// * the Norsemen's colossus pad: hu_colossi_launchpad_misc_0 (hx_berserk_statue_04, the colossus itself) stands on
//   the site from its start, its head, arms (VIS_FLAG_CHTR_BODY) and weapon (VIS_FLAG_CHTR_LEGS) hidden; at 51 % the
//   arms appear, at 76 % the head; the weapon never (it comes with the colossus).
// * the Dragon Clan's pirate pad: ninigi_pirate_ship_deco_0 (pirate_boss_dest, the wreck of a ship) at the pad's
//   link Bl_1; at 40 % it gives way to the ship's parts - deco_1 the tail (pirate_boss_tail, 11.36 m aft), deco_2 the
//   sail (pirate_boss_sail), deco_3 the rowing deck (pirate_boss_row, 12.6 m ahead), deco_4 the cannons
//   (pirate_boss_cannons) - at destruction level 2, at 60 % level 1, at 80 % level 0 (whole); the boss ship appears
//   where the sail stood. The parts' places are the boss ship's links psh1 / psh2 / psh3 (sl_pirate_outpost.gsf:
//   -11.36 / 0 / 12.6 m); the pad's Bl_1 is its origin (0, 0, 0.02).
// The decorations go with the site (Die) and when the titan comes (SetReady).
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>the energy of a SEAS healing well (CSeasWell: FightingObj's m_fEnergyState / m_fMaxEnergy ...)</summary>
public sealed class SeasWellState
{
    public double Energy, Max, Step, Every;
    /// <summary>seconds to the next refill (REPLENISH_TIMER); null: not running</summary>
    public double? T;
}

/// <summary>a titan's launch pad turning into its titan</summary>
public sealed class LaunchState
{
    public double T;
    public bool StandingUp;
}

/// <summary>MIRAGE's pads whose site counts in the unit limit (DoesCountInUnitLimit: the titans' launch pads, the
/// zeppelin launch pad)</summary>
public static class MiragePads
{
    public static readonly HashSet<string> COUNT_AS_UNIT = new(StringComparer.Ordinal)
    {
        "CSeasScorpionLaunchpad", "CHuAvatarLaunchpad", "CNinigiPirateBossLaunchpad", "CZeppelinLaunchPad",
    };
}

public sealed partial class Building
{
    public SeasWellState? SeasWell;
    public LaunchState? Launch;
    /// <summary>a launch pad's build-up decoration stage (-1: none yet)</summary>
    public int PadStage = -1;
}

public sealed partial class World
{
    /// <summary>CSeasWellHeal: arrived within the unit's radius + 7 m of the well's centre</summary>
    const double SEAS_WELL_REACH = 7;
    static readonly System.Text.RegularExpressions.Regex SeasWellUsers = new("^(CHTR|ANML|VHCL)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ------------------------------------------------------------------------------------------ the SEAS healing well
    sealed class SeasWellB : BuildingBehaviour
    {
        // SetManaOnInit: the energy at the start is starting_energy (8 when the class has none)
        public override void Init(Building b, World W)
        {
            b.SeasWell = new SeasWellState { Energy = W.ObjValue(b, "starting_energy", 8) };
            W.SeasWellRules(b);
        }
        // SetReady -> UpdateEnergy: the refill starts unless it is full
        public override void Built(Building b, World W) { W.SeasWellRules(b); W.SeasWellRefill(b); }
        public override void Update(Building b, World W, double dt)
        {
            var s = b.SeasWell;
            if (s?.T == null) return;
            if ((s.T -= dt) > 0) return;
            // REPLENISH_TIMER -> Replenish(amount) -> IncreaseEnergy (capped at the maximum) -> UpdateEnergy (again)
            W.SeasWellRules(b);
            if (s.Energy < s.Max) s.Energy = Math.Min(s.Max, s.Energy + s.Step);
            s.T = null;
            W.SeasWellRefill(b);
        }
    }

    /// <summary>a value of the building's class in its owner's tree (/Objects/&lt;tribe&gt;/&lt;type&gt;/&lt;class&gt;/key)</summary>
    double ObjValue(Entity e, string key, double d)
    {
        var path = e.Def?.Path;
        if (string.IsNullOrEmpty(path)) return d;
        var v = Data.Tree(e.RulesOwner()).Num(path + "/" + key, double.NaN);
        return double.IsNaN(v) ? d : v;
    }

    /// <summary>SetMana (OnTechTreeChange): maximum, refill step and time from the tree (upgrades may change them)</summary>
    void SeasWellRules(Building b)
    {
        var s = b.SeasWell!;
        s.Max = ObjValue(b, "max_energy", 0);
        s.Step = ObjValue(b, "replenish_step", 0);
        s.Every = ObjValue(b, "replenish_time", 20);
    }

    /// <summary>UpdateEnergy / StartReplenishTimer: a finished well that is not full refills (CSeasWell: only when ready)</summary>
    void SeasWellRefill(Building b)
    {
        var s = b.SeasWell!;
        if (!b.Built || s.Max <= 0 || s.Energy == s.Max) { s.T = null; return; }
        s.T ??= Math.Max(STEP, s.Every);
    }

    /// <summary>a finished SEAS healing well (CSeasWell)</summary>
    public static bool IsSeasWell(Entity? e) => e is Building b && b.SeasWell != null;

    /// <summary>may u be sent to the SEAS well b? (GameInputController.usl: own or mutually allied, finished; characters,
    /// animals and vehicles; FightingObj.SeasWellHeal: not a flying unit, not one with blood infection)</summary>
    bool CanUseSeasWell(Unit u, Building b)
    {
        if (b.SeasWell == null || !b.Alive || !b.Built || u.Owner == null || b.Owner == null) return false;
        if (u.Owner != b.Owner && !(u.Owner.IsFriend(b.Owner) && b.Owner.IsFriend(u.Owner))) return false;
        if (!SeasWellUsers.IsMatch(u.Cls) || u.Flying || u.Inside) return false;
        return !u.St.Active("noHeal", Time);
    }

    // task "fountain" on a SEAS well (CSeasWellHeal): run to it (AdvanceAction at the unit's maximum speed), within its
    // radius + 7 m of the well's centre: ObjArrived. Five tries (SetMaxCounter(5)).
    void SeasWellUpdate(Unit u, Building b, double dt)
    {
        var t = u.Task;
        if (!CanUseSeasWell(u, b)) { FountainDone(u); return; }
        double dx = b.Pos.X - u.Pos.X, dz = b.Pos.Z - u.Pos.Z;
        // (also within the AdvanceAction's 3 m of its surface: the walk grid's 2 m cells may end a walk a little
        // further out than the engine's, and the well's 9.2 m footprint puts anything next to it within reach anyway)
        if (Math.Sqrt(dx * dx + dz * dz) <= u.Radius + SEAS_WELL_REACH || b.SurfDist(u.Pos.X, u.Pos.Z) <= 3 + u.Radius)
        {
            u.Path = new NavPath(); u.Vel = V3.Zero;
            SeasWellArrived(b, u);
            u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z;
            FountainDone(u);
            return;
        }
        if (!t.Walking)
        {
            if (t.Tries > 4) { FountainDone(u); return; }
            t.Tries++;
            var (x, z) = ApproachPoint(u, b);
            u.SetPath(x, z);
            t.Walking = true;
        }
        if (u.Path.Count > 0) { u.MoveAnim(u.Steer(dt, u.RunSpeed > 0 ? u.RunSpeed : u.Speed), true); return; }
        t.Walking = false;                                  // the walk ended short of it: again (STATE_CHECK -> WALKTO)
    }

    /// <summary>CSeasWell.ObjArrived: own and allied units get what they lack, or what is left of the energy; they take no
    /// damage for a tenth of a second per hit point and lose their poison (FountainOfYouth)</summary>
    public void SeasWellArrived(Building b, Unit u)
    {
        var s = b.SeasWell;
        if (s == null || !b.Alive || !b.Built || !u.Alive || u.Owner == null || b.Owner == null) return;
        if (u.Owner != b.Owner && !u.Owner.IsFriend(b.Owner)) return;
        double hp = u.Hp, max = u.MaxHp;
        if (hp == max) return;
        double diff = s.Energy - (max - hp);
        if (diff >= 0)
        {
            SetHp(u, max);                                  // FullHeal
            FountainOfYouth(u, max - hp);
        }
        else
        {
            // HealMe: the tribe's healing bonus (MiscValues Healing_Bonus_rel / _abs: 1 and 0 in MIRAGE)
            double amount = s.Energy * Data.MiscValue(u.RulesOwner(), "Healing_Bonus_rel", 1) + Data.MiscValue(u.RulesOwner(), "Healing_Bonus_abs", 0);
            HealMe(u, amount);
            FountainOfYouth(u, s.Energy);
            diff = 0;
        }
        s.Energy = diff;
        SeasWellRefill(b);
        if (b.Anim != null && b.Anim.Has("fill_anim")) b.Anim.Play("fill_anim", AnimOpts.Once(new AnimDone("idle")));    // (event 23)
        Emit("drink", new Args { ["unit"] = u, ["well"] = b });
    }

    /// <summary>FightingObj.FountainOfYouth(time): no damage for time x 0.1 s (m_bFountainHealed), and Detoxification</summary>
    void FountainOfYouth(Unit u, double t)
    {
        u.St.T["fountain"] = Time + t * 0.1;
        u.St.Poison = null; u.St.Poison2 = null;
    }

    // ------------------------------------------------------------------------------------------ the titans' launch pads
    sealed class LaunchpadB : BuildingBehaviour
    {
        readonly string _unit; readonly double _wait; readonly bool _standUp; readonly string? _link; readonly string? _deco;
        /// <summary>deco: "colossus" | "pirate" | null - the build-up decorations of the site</summary>
        public LaunchpadB(string unit, double wait, bool standUp, string? link, string? deco = null) { _unit = unit; _wait = wait; _standUp = standUp; _link = link; _deco = deco; }
        public override bool NoTower => true;           // IsAbleToFight: false
        // SetProcess(percent): the decorations' stage (World.SiteProgressed)
        public override void SiteProgress(Building b, World W)
        {
            if (_deco == null) return;
            int st = PadStage(_deco, b.Progress);
            if (st == b.PadStage) return;
            b.PadStage = st;
            b.UpdateTurret();
        }
        public override IReadOnlyList<ExtraPart>? ExtraParts(Building b, World W) => b.Built || _deco == null ? null : W.PadDecos(_deco, PadStage(_deco, b.Progress));
        // SetReady: no longer selectable or hitable; the titan follows (the next step at the earliest)
        public override void Built(Building b, World W)
        {
            b.Untargetable = true; b.Unselectable = true;
            b.Launch = new LaunchState { T = _wait };
        }
        public override void Update(Building b, World W, double dt)
        {
            var s = b.Launch;
            if (s == null || (s.T -= dt) > 0) return;
            // CSeasScorpionLaunchpad: SCLAUNCHPAD_STANDUP (1 s) plays "stand_up" once, SCLAUNCHPAD_REPLACE after its length
            if (_standUp && !s.StandingUp && b.Anim != null && b.Anim.Has("stand_up"))
            {
                s.StandingUp = true;
                b.Anim.Play("stand_up", AnimOpts.Once());
                s.T = b.Anim.Duration("stand_up");
                return;
            }
            b.Launch = null;
            W.LaunchTitan(b, _unit, _link);
        }
    }

    /// <summary>the stage of a pad's build-up decoration at a construction progress (0..1; SetProcess gets whole percents)</summary>
    public static int PadStage(string deco, double progress)
    {
        int pc = (int)Math.Floor(progress * 100 + 1e-9);
        return deco == "colossus" ? (pc >= 76 ? 2 : pc >= 51 ? 1 : 0)
            : pc >= 80 ? 4 : pc >= 60 ? 3 : pc >= 40 ? 2 : 0;
    }

    /// <summary>VIS_FLAG_CHTR_HEAD / BODY (arms) / LEGS (weapon) of the colossus standing on its pad</summary>
    const int VIS_HEAD = 5, VIS_BODY = 6, VIS_LEGS = 7;

    /// <summary>the decorations of a pad's site at a stage (the models of mirage_misc.txt's classes)</summary>
    public IReadOnlyList<ExtraPart> PadDecos(string deco, int stage)
    {
        string G(string cls, string d) => Data.ClassGfx(cls) ?? d;
        if (deco == "colossus")
            return new[] { new ExtraPart(G("hu_colossi_launchpad_misc_0", "hx_berserk_statue_04"), "NONE", null, 0,
                new[] { (VIS_HEAD, stage >= 2), (VIS_BODY, stage >= 1), (VIS_LEGS, false) }) };
        if (stage < 2) return new[] { new ExtraPart(G("ninigi_pirate_ship_deco_0", "pirate_boss_dest"), "Bl_1") };
        int dmg = stage == 2 ? 2 : stage == 3 ? 1 : 0;
        return new[]
        {
            new ExtraPart(G("ninigi_pirate_ship_deco_1", "pirate_boss_tail"), "Bl_1", new[] { 0, -11.36, 0.01 }, dmg),
            new ExtraPart(G("ninigi_pirate_ship_deco_2", "pirate_boss_sail"), "Bl_1", new[] { 0.0, 0, 0 }, dmg),
            new ExtraPart(G("ninigi_pirate_ship_deco_3", "pirate_boss_row"), "Bl_1", new[] { 0, 12.6, 0 }, dmg),
            new ExtraPart(G("ninigi_pirate_ship_deco_4", "pirate_boss_cannons"), "Bl_1", new[] { 0.0, 0, 0 }, dmg),
        };
    }

    /// <summary>the pad goes (no ruin, no loss) and its titan stands there at the pad's level (SetLevelClean(GetLevel()):
    /// the build action's result level)</summary>
    public Unit? LaunchTitan(Building b, string unit, string? link = null)
    {
        if (!b.Alive) return null;
        var at = (link != null ? b.LinkWorld(link) : null) ?? b.Pos;
        var owner = b.Owner;
        unit = unit.Replace("{tribe}", (owner?.Tribe ?? b.Def?.Tribe ?? "").ToLowerInvariant());      // (CZeppelinLaunchPad: sTribe+"_zeppelin")
        var act = b.Action ?? BuildActionOf(b);
        int lv = Math.Max(act != null ? Data.StartLevel(act) : 1, Data.MinLevel(unit, owner));
        double rot = b.Rot;
        RemoveEntity(b);
        var u = SpawnUnit(unit, owner, at.X, at.Z, lv, rot);
        InheritInfection(b, u);
        if (u != null) Emit("launched", new Args { ["building"] = b, ["unit"] = u });
        return u;
    }
}
