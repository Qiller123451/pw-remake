// MIRAGE's building classes of the Norsemen, the Dustriders and of all tribes that stood as plain buildings in the
// remake: the Norse stone mine (CHuMine), mud pit (CHuMudPit), punching bag (CHuPunchingBag), big horn (CHuBigHorn),
// the Dustriders' Amazon temple (CAjeAmazonTemple), sensor tower (CAjeSensorTower), gallimimus nest (CAjeGalliNest),
// every tribe's zeppelin launch pad (CZeppelinLaunchPad), special box (CSpecialBox), the pile of goods (CGoods) and the
// Halicarnassus gate (CHCGate, a decoration: MapSource). The portal terminus is PortalTerminus.cs, the titans' launch
// pads and the SEAS well MirageBuildings.cs, the pirate boss ship's parts PirateShip.cs.
//
// Reference (MIRAGE, Data/MIRAGE/Scripts/Server): classes/mirage/mirage_buildings.txt (the classes' models; their
// helper objects in mirage_misc.txt of the MIRAGE master); classes/mirage/Mirage.usl CAjeAmazonTemple (8796),
// CAjeGalliNest (8848), CAjeSensorTower (9039), CGoods (9475), CHuBigHorn (9602), CHuMine (9702), CSpecialBox (12800),
// CZeppelinLaunchPad (13212), CHCGate (13896), CAjeAmazonTempleCorpse (14970); classes/buildings/Building.usl
// CHuPunchingBag (5859), CTrap (6553), CHuMudPit (7608); classes/task/GetCorn.usl (the mines: 250-280; SetThing);
// classes/animals/Nest.usl (spawn_max + m_iGrowSize).
//
// * CHuMine (hu_mine, final stage, 1000 food 2000 wood 100 iron, at most 4): a CGrowingField of stone (unlimited stone,
//   2 workers) - the workers hack stone in it with their tribe's pick ("hacking_stone"; GetCorn.usl for hu_mine and
//   seas_mine) and carry it in their tribe's container, not the field's sheaf.
// * CHuMudPit (hu_mud_pit, final stage, 250 wood 100 stone, at most 3): a hidden trap of 12 m that every enemy
//   character, animal or vehicle walking in (not flying ones, not those on a wall or in a transport, not the one
//   attacking it, not wild neutral animals) gets "broken legs" from: for 45 s it moves at its slowest gait (SetBrokenLegs
//   -> UpdateSpeed: maxspeed and defaultspeed 1). Once released (after a second) a unit is caught again only after it
//   left the pit. The pit is not revealed by catching (no OnReveal) and marks itself owner_less_attack_speed.
// * CHuPunchingBag (hu_punching_bag): units walk through it (SetPFBlocking is empty); it is no field although the tree
//   gives it "unlimited stone" (it is a CBuilding). Its build action stays hidden in MIRAGE (-1, final stage +1).
// * CHuBigHorn (hu_big_horn, a map object): its will-o'-the-wisp once finished (AddFX fx_will_o_wisp_sml). Its region
//   of 50 m does nothing: the handlers that set MIRAGE_BIG_HORN on allies are commented out in MIRAGE.
// * CAjeAmazonTemple (aje_amazon_temple, final stage, 1500 wood 1500 stone): a CTemple - it heals and gives mana around
//   itself by its tree data (special_abilities heal / give_mana), shows the temple's healing glow and marks itself
//   owner_healing_temple as every temple; destroyed, its corpse is the temple itself at destruction level 2 for 3 s,
//   then it sinks (CAjeAmazonTempleCorpse: the model has no ruin of its own).
// * CAjeSensorTower (aje_sensor_tower, final stage, 1000 wood 1000 stone 100 iron, at most 1; FOW 135 m): once finished,
//   every 2 s it uncovers every enemy within its sight - camouflaged and disguised units lose their camouflage (Reveal:
//   the ninja's disguise comes back as after an attack), the Dustriders' camouflage ends, hidden traps are found and
//   hidden again 20 s later (TrapFound, StartHiddenTimer(20)). An amazon guard (aje_guard: amazons_aje_velociraptor_
//   handler_s1) stands on top (24.88 m up) and plays attention / look_arround / look_arround_careful / standanim at
//   random, the next one when the last is over.
// * CAjeGalliNest (aje_gallimimus_nest, epoch 4, 250 food 120 wood, at most 8): finished, it keeps a nest
//   (aje_galli_nest_spawn) of gallimimus: one every 10 s up to 3, kept within 70 m; 360 s after it was finished the
//   nest grows by 2 (ChangeSpawnAmount: 5). The animals die with the nest. Its eggs (nest_birds) are the composites
//   table's.
// * CZeppelinLaunchPad (<tribe>_launchpad_zeppelin, built by the trader from epoch 3, 400 food 500 wood 350 stone): it
//   counts in the unit limit from its site on; finished, it becomes the tribe's zeppelin (<tribe>_zeppelin) at the
//   action's level, at its place, without a ruin. Commands are ignored while it is trapped.
// * CSpecialBox (<tribe>_special_box, final stage, 50 food 50 wood 50 stone): finished, a lemur and a sloth (special_box_
//   misc_0 / _1) sit on it, 1.35 m to the left and right, 0.3 m up, with helmet and armour, resting ("rest").
// * CGoods (hu_goods, a map object - the tree has no such object): every 5 s once finished its owner gets 25 wood and
//   25 stone (FOOD_FROM_DEVITO 0). The owner change by the units around it is switched off in MIRAGE ("forget about this
//   for now").
// * CHCGate (aje_hc_gate, a decoration): blocks nothing (SetPlaceBlocker(false), RemovePFBlocker) - MapSource.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>the sensor tower's sweeps and its guard's clips (CAjeSensorTower SENSOR_TIMER, GUARD_TIMER)</summary>
public sealed class SensorState
{
    public double ScanT, GuardT;
}

/// <summary>the gallimimus nest (CAjeGalliNest + its CNest): the animals, the spawn timer, the growth</summary>
public sealed class GalliNestState
{
    public readonly List<Unit> Members = new();
    public double T, GrowT;
    public int Max;
    public bool Grown;
}

public sealed partial class Building
{
    public SensorState? Sensor;
    public GalliNestState? Galli;
    /// <summary>CGoods: seconds to the next delivery (GOODS_TIMER, 5 s)</summary>
    public double GoodsT = -1;
    /// <summary>how long the ruin stands before it sinks (8 s; MIRAGE's Amazon temple: 3 s)</summary>
    public double RuinSinkAt = 8;
}

public sealed partial class World
{
    // the classes of this file and PortalTerminus.cs (Buildings.cs BehaviourOf asks this table after BEHAVIOURS)
    static readonly Dictionary<string, BuildingBehaviour> MIRAGE_CLASSES = new()
    {
        ["CHuMine"] = new FieldB(),
        ["CHuMudPit"] = new TrapB(new TrapDef { Radius = 12, Types = new[] { "CHTR", "ANML", "VHCL" }, Once = false, Reveal = false, Fire = (b, v, W) => W.BreakLegs(v, MUD_PIT_LEGS) }),
        ["CAjeSensorTower"] = new SensorTowerB(),
        ["CAjeGalliNest"] = new GalliNestB(),
        ["CZeppelinLaunchPad"] = new LaunchpadB("{tribe}_zeppelin", 0, false, null),
        ["CSpecialBox"] = new SpecialBoxB(),
        ["CGoods"] = new GoodsB(),
        ["CPortalTerminus"] = new PortalTerminusB(),
    };
    /// <summary>the behaviour of one of this file's classes (tests: a map object the tree lacks - CGoods)</summary>
    public static IBuildingBehaviour? MirageBehaviour(string script) => MIRAGE_CLASSES.TryGetValue(script, out var b) ? b : null;
    /// <summary>classes whose behaviour their subclasses inherit when they have none of their own (the SEAS terminus
    /// CSeasPortalTerminus is a CPortalTerminus)</summary>
    static readonly string[] INHERITED_BEHAVIOURS = { "CPortalTerminus" };

    /// <summary>the behaviour of a class without an entry of its own: an ancestor's (INHERITED_BEHAVIOURS)</summary>
    BuildingBehaviour? InheritedBehaviour(string name)
    {
        foreach (var c in INHERITED_BEHAVIOURS) if (Data.IsA(name, c) && MIRAGE_CLASSES.TryGetValue(c, out var b)) return b;
        return null;
    }

    // ------------------------------------------------------------------------------------------ the mines (GetCorn.usl)
    /// <summary>a mine of MIRAGE: GetCorn.usl hacks stone there (hu_mine, seas_mine)</summary>
    public static bool IsMineField(Building? b) => b != null && (b.Name == "hu_mine" || b.Name == "seas_mine");
    /// <summary>the worker's pick: "&lt;its class's prefix&gt;_pick" (Larry - "arry_s0": hu_pick)</summary>
    static string MinePick(Unit u) => u.Name.Contains("arry_s0") ? "hu_pick" : (u.Name.IndexOf('_') is int i && i > 0 ? u.Name[..i] : TribeOf(u).ToLowerInvariant()) + "_pick";

    // ------------------------------------------------------------------------------------------ the mud pit
    /// <summary>CHuMudPit.OnTrap: SetBrokenLegs(true, 45)</summary>
    public const double MUD_PIT_LEGS = 45;
    /// <summary>FightingObj.SetBrokenLegs: the unit moves at its slowest gait for that long (Unit.Steer)</summary>
    public void BreakLegs(Unit v, double seconds)
    {
        if (!v.Alive) return;
        v.St.T["brokenLegs"] = Time + seconds;
        Emit("brokenLegs", new Args { ["unit"] = v, ["until"] = Time + seconds });
    }

    // ------------------------------------------------------------------------------------------ the sensor tower
    const double SENSOR_INTERVAL = 2, SENSOR_TRAP_HIDE = 20;
    static readonly double[] GUARD_OFFSET = { 0, 0, 24.88 };
    static readonly string[] GUARD_CLIPS = { "attention", "look_arround", "look_arround_careful", "standanim", "standanim" };

    sealed class SensorTowerB : BuildingBehaviour
    {
        // SetReady: the guard, a sweep at once, the guard's first clip, the sweep every 2 s
        public override void Built(Building b, World W)
        {
            b.Sensor = new SensorState();
            b.UpdateTurret();
            W.SensorSweep(b);
            b.Sensor.ScanT = SENSOR_INTERVAL;
            b.Sensor.GuardT = W.GuardAnim(b);
        }
        public override void Update(Building b, World W, double dt)
        {
            var s = b.Sensor;
            if (s == null) return;
            if ((s.ScanT -= dt) <= 0) { s.ScanT += SENSOR_INTERVAL; W.SensorSweep(b); }
            if ((s.GuardT -= dt) <= 0) s.GuardT = W.GuardAnim(b);
        }
        public override IReadOnlyList<ExtraPart>? ExtraParts(Building b, World W) =>
            b.Built ? new[] { new ExtraPart(W.Data.ClassGfx("aje_guard") ?? "amazons_aje_velociraptor_handler_s1", "NOPE", GUARD_OFFSET) } : null;
    }

    /// <summary>GuardAnim: one of the guard's clips at random (Random.GetInt() % 5); the next when it is over</summary>
    double GuardAnim(Building b)
    {
        var g = b.Parts.FirstOrDefault(p => p.OwnState && p.Fourcc == "Char") ?? b.Parts.FirstOrDefault(p => p.OwnState);
        var clip = GUARD_CLIPS[(int)Math.Floor(Rng.Next() * GUARD_CLIPS.Length) % GUARD_CLIPS.Length];
        if (g?.Anim == null || !g.Anim.Has(clip)) return 3;
        g.Anim.Play(clip, AnimOpts.Once());
        double d = g.Anim.Duration(clip);
        return d > 0 ? d : 3;
    }

    /// <summary>RevealTracesAndTraps: the enemies within the tower's sight lose their camouflage; hidden traps are found
    /// and hidden again after 20 s</summary>
    public void SensorSweep(Building b)
    {
        var p = b.Owner;
        if (p == null || !b.Alive || !b.Built) return;
        double R = b.Fow;
        void Test(Entity v)
        {
            if (!v.Alive || v.Owner == null || !p.IsEnemy(v.Owner) || v == b) return;
            if (Hypot(v.Pos.X - b.Pos.X, v.Pos.Z - b.Pos.Z) > R) return;
            if (v is Unit u)
            {
                // Reveal: the layers "disg" and "entr" go (the disguise comes back by itself), the Aje camouflage ends
                if (u.St.Camo.Has("disg")) { u.St.Camo.Delete("disg"); u.DisgT = Time + 10; }
                if (u.St.Camo.Has("entr")) u.St.Camo.Delete("entr");
                if (u.St.Camo.Has("aje")) SetCamoOf(u, "aje", false);
                u.St.RevealT = Math.Max(u.St.RevealT, Time + SENSOR_INTERVAL);
            }
            else if (v is Building t && t.TrapS != null && t.St.Camo.Has("trap"))
            {
                // TrapFound + StartHiddenTimer(20)
                t.St.Camo.Delete("trap");
                t.RehideT = SENSOR_TRAP_HIDE;
                Emit("trapFound", new Args { ["building"] = t, ["player"] = p });
            }
        }
        UHash.Query(b.Pos.X, b.Pos.Z, R + 8, Test);
        BHash.Query(b.Pos.X, b.Pos.Z, R + 20, Test);
    }

    // ------------------------------------------------------------------------------------------ the gallimimus nest
    // (aje_galli_nest_spawn, mirage_buildings.txt: spawn_type aje_gallimimus, spawn_max 3, spawn_rate 10, unlimited,
    // grown; CAjeGalliNest: NEST_RADIUS_1 70 m, NEST_GROW 360 s, NEW_EGGS 2)
    const string GALLI = "aje_gallimimus";
    const double GALLI_RATE = 10, GALLI_RADIUS = 70, GALLI_GROW = 360;
    const int GALLI_MAX = 3, GALLI_EGGS = 2;

    sealed class GalliNestB : BuildingBehaviour
    {
        public override void Built(Building b, World W) { b.Galli = new GalliNestState { T = GALLI_RATE, GrowT = GALLI_GROW, Max = GALLI_MAX }; }
        public override void Update(Building b, World W, double dt)
        {
            var n = b.Galli;
            if (n == null) return;
            n.Members.RemoveAll(u => !u.Alive);
            if (!n.Grown && (n.GrowT -= dt) <= 0) { n.Grown = true; n.Max += GALLI_EGGS; }
            if (n.Members.Count < n.Max && (n.T -= dt) <= 0)
            {
                n.T = GALLI_RATE;
                var (x, z) = b.ExitPoint(b.Pos.X + 5, b.Pos.Z + 5);
                var u = W.SpawnUnit(GALLI, b.Owner, x, z, 1, 0, new UnitOpts { Autonomous = true });
                if (u != null) { u.HomeX = b.Pos.X; u.HomeZ = b.Pos.Z; u.HomeRadius = GALLI_RADIUS; n.Members.Add(u); W.InheritInfection(b, u); }
            }
            foreach (var u in n.Members)
                if (u.Task.Type == "idle" && Hypot(u.Pos.X - b.Pos.X, u.Pos.Z - b.Pos.Z) > GALLI_RADIUS) W.Order(u, new Order("move") { X = b.Pos.X, Z = b.Pos.Z });
        }
        public override void Destroyed(Building b, World W) { if (b.Galli != null) foreach (var u in b.Galli.Members.ToList()) if (u.Alive) W.Kill(u, null); }
    }

    // ------------------------------------------------------------------------------------------ the special box
    static readonly double[] LEMUR_AT = { -1.35, 0, 0.3 }, SLOTH_AT = { 1.35, 0, 0.3 };
    /// <summary>VIS_FLAG_ANML_HELMET / ARMOR shown</summary>
    static readonly (int, bool)[] BOX_ANIMAL_FLAGS = { (7, true), (8, true) };

    sealed class SpecialBoxB : BuildingBehaviour
    {
        public override IReadOnlyList<ExtraPart>? ExtraParts(Building b, World W) => !b.Built ? null : new[]
        {
            new ExtraPart(W.Data.ClassGfx("special_box_misc_0") ?? "lemur", "NOPE", LEMUR_AT, 0, BOX_ANIMAL_FLAGS, "rest"),
            new ExtraPart(W.Data.ClassGfx("special_box_misc_1") ?? "sloth", "NOPE", SLOTH_AT, 0, BOX_ANIMAL_FLAGS, "rest"),
        };
    }

    // ------------------------------------------------------------------------------------------ the pile of goods
    const double GOODS_EVERY = 5, GOODS_WOOD = 25, GOODS_STONE = 25;

    sealed class GoodsB : BuildingBehaviour
    {
        public override void Built(Building b, World W) => b.GoodsT = GOODS_EVERY;
        public override void Update(Building b, World W, double dt)
        {
            if (b.GoodsT < 0 || (b.GoodsT -= dt) > 0) return;
            b.GoodsT += GOODS_EVERY;
            var p = b.Owner;
            if (p == null) return;
            // (FOOD_FROM_DEVITO 0: no food)
            p.Deliver("wood", GOODS_WOOD, W.Time);
            p.Deliver("stone", GOODS_STONE, W.Time);
        }
    }

    // ------------------------------------------------------------------------------------------ the Amazon temple's corpse
    /// <summary>CAjeAmazonTemple.CreateBuildingCorpse: a finished temple drawn with amazon_temple leaves the temple itself
    /// at destruction level 2 for 3 s, then it sinks (CAjeAmazonTempleCorpse); null: not such a temple</summary>
    bool AmazonTempleCorpse(Building e)
    {
        if (!e.Built || !Data.IsA(e.Name, "CAjeAmazonTemple") || !string.Equals(e.Gfx, "amazon_temple", StringComparison.OrdinalIgnoreCase)) return false;
        e.Ruin = e.Gfx;
        e.RuinParts = null;
        e.VisDmg = 2; e.DmgStage = 2;
        e.RuinSinkAt = 3;
        return true;
    }
}
