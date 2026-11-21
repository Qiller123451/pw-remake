// MIRAGE's SEAS building classes that used to stand as plain buildings, towers or producers (mirage_buildings.txt):
// the psi disruptor, the speaker, the Stygimoloch nest, the Tesla machine, the stone mine, the pub, the warp gate, the
// portal terminus' parts, and the turrets of the great cannon, the MG nest, the small and the shore cannon.
//
// Reference (MIRAGE, Data/MIRAGE/Scripts/Server): classes/mirage/mirage_buildings.txt, mirage_misc.txt (the CMirageDeco
// parts); classes/mirage/Mirage.usl CSeasGreatCannon (10508-10702), CSeasMgNest (10704), CSeasMine (10723-10840),
// CSeasPortalTerminus (10842-11094), CSeasPsiDisruptor (11096-11182), CSeasPub (11184-11309), CSeasSmallCannon (11749),
// CSeasShoreCannon (11762-11798), CSeasSpeaker (11800-11866), CSeasStygiNest (12115-12272), CSeasWarpGate (12274-12539),
// CTeslaMachine (12967-12989); classes/FightingObj/FightingObj.usl SetMirageFlag (12030-12180: MIRAGE_PSI_DISRUPT,
// MIRAGE_SPEAKER), AddTemporaryDefenseBoni / AddTemporaryRangedDefenseBoni (10166-10230: + 20 with MIRAGE_SPEAKER),
// SetRallyPoint (11450-11493: CheckTeslaMachine); classes/buildings/Building.usl CTower (2388-2700), CRallyBuilding /
// CTemple / CNPCSeller CheckTeslaMachine and GetSpawnPos (1921-1944, 4983-5006, 5051-5072); classes/task/Action.usl
// (1278-1290: a producer's new unit at its Tesla machine); classes/task/GetCorn.usl (the mines: hacking_stone with the
// pick, the tribe's container); classes/animals/Nest.usl CNest (spawn timer, areas, ChangeSpawnAmount).
//
// * Psi disruptor (seas_disruptor, CSeasPsiDisruptor): "open" held at its first frame while a site, played once when
//   finished. Then enemy characters, animals, vehicles and ships within DISRUPT_RADIUS (40 m) of it get the filter
//   /Filters/SEAS/Upgrades/seas_disruptor/reduce_speed on their own tree (WeaponDuration x 1.5 for every tribe and
//   caste: they attack a third slower), counted per unit as the original's m_aiMirageFlags (two disruptors do not add
//   up), and lose it when they leave or the disruptor goes. Buffs: owner_less_attack_speed on the disruptor (from its
//   site on), slower_fighting_rate on the units.
// * Speaker (seas_speaker, CSeasSpeaker): finished, the owner's own characters, animals and vehicles within
//   SPEAKER_RADIUS (50 m) have + 20 defence and + 20 ranged defence (MIRAGE_SPEAKER; allies only with MIRAGE's game
//   option AuraSharing, default off and not in the remake). Buffs: owner_more_defense, owner_more_armor; more_defense /
//   more_armor on the units.
// * Stygimoloch nest (seas_stygimoloch_nest, CSeasStygiNest): finished, it keeps a hidden nest (seas_stygo_nest_spawn,
//   CNest: spawn_type seas_stygimoloch, spawn_max 2, spawn_rate 10, grown, uninfluenced - mirage_buildings.txt): one
//   grown, autonomous stygimoloch of the owner every 10 s while fewer than the maximum live; NEST_GROW (360 s) after it was
//   finished the maximum rises by NEW_EGGS (2: ChangeSpawnAmount). Its animals keep within NEST_RADIUS_1 (70 m): every 2
//   s one outside walks back to a point inside, unless it fights within NEST_RADIUS_2 (140 m) (TIMER_CHECK_AREA). The
//   building's end kills the nest (CNest.Die), not its animals - they roam on.
// * Tesla machine (seas_tesla_machine, CTeslaMachine: SetRallySite): a rally point set on a finished, own or mutually
//   allied Tesla machine makes a producer (CRallyBuilding: barracks, headquarters, garage ... of every tribe; CTemple;
//   CNPCSeller: the taverns and the SEAS pub; CSeasFortress) put its new units out at the machine - its position + its
//   radius along the world's y axis, turned as the machine (GetSpawnPos) - while the machine stands finished.
// * Mine (seas_mine, CSeasMine: a CGrowingField of stone, 2 workers): the field task as GetCorn.usl does it for
//   "hu_mine" / "seas_mine" (MirageClasses.cs, with the Norse mine): the worker hacks (hacking_stone, its tribe's pick)
//   and carries the stone in its tribe's container (SetThing: "_mine"). Finished, its six parts seas_mine_misc_0..5.
// * Pub (seas_pub, CSeasPub: a CNPCSeller - the tavern of the SEAS, its heroes as the taverns' are bought): finished,
//   its gate and antenna seas_pub_misc_0 / 1.
// * Warp gate (seas_warpgate, CSeasWarpGate: a CWarpGate): the warp gate's countdown and victory (Buildings.cs WarpGateB:
//   it had none, the behaviour was looked up by the exact class); finished, its parts seas_wg_misc_0 / 1; destroyed, the
//   first one plays "destroy" (Die).
// * Portal terminus (seas_portal_terminus, CSeasPortalTerminus): the portal network of CPortalTerminus
//   (PortalTerminus.cs); finished, its part seas_wg_misc_0, which plays "destroy" when it is destroyed.
// * Towers (CTower.SetTurret in SetReady: CSeasMgNest seas_hq_machinegun_nest_top, CSeasSmallCannon
//   seas_hq_defense_turret_top, CSeasShoreCannon seas_carrier_turret 7.05 m below "we"): the turret only once the tower is
//   finished, at construction level 4 (SetConstructLevel(4)); it turns to the target (0.8 s) and plays "attack_front"
//   with every shot (Combat.TowerUpdate). The great cannon (CSeasGreatCannon) has its rotator from 80 % of the
//   construction on (SetProcess) and its barrel seas_hq_big_cannon_cannon on the rotator's "we" once finished; the barrel
//   plays "attack_front" with every shot that did not need to turn first (AttackEnemy: !p_rbRotated), its damage stage
//   follows the cannon's hit points (UpdateDestructionFlags: > 50 % 0, > 25 % 1, else 2 - the parts' VisDmg). Its
//   special shots and self-damage (ThrowBack, INTERNAL_DAMAGE, the announcements) are commented out in MIRAGE: it fires
//   as a tower.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>a personal region of a building (CreatePersonalRegion: the psi disruptor's, the speaker's) and the units in
/// it (m_xRegionObjects)</summary>
public sealed class SeasRegionState
{
    public readonly OrderedSet<Unit> In = new();
    public double T;
}

/// <summary>the hidden nest of a Stygimoloch nest (CSeasStygiNest m_xNest, CNest)</summary>
public sealed class StygiNestState
{
    public readonly List<Unit> Members = new();
    /// <summary>TIMER_SPAWN (null: stopped), NEST_GROW_TIMER (null: done), TIMER_CHECK_AREA</summary>
    public double? SpawnT, GrowT;
    public double CheckT;
    /// <summary>ChangeSpawnAmount(NEW_EGGS): m_iGrowSize</summary>
    public int Grow;
}

public sealed partial class Building
{
    public SeasRegionState? SeasRegion;
    public StygiNestState? Stygi;
    /// <summary>the Tesla machine its rally point is on (CNPCSeller / CRallyBuilding m_xTeslaM)</summary>
    public Building? TeslaM;
}

public sealed partial class Unit
{
    /// <summary>MIRAGE's flag counters (FightingObj m_aiMirageFlags): psi disruptors and speakers it is in</summary>
    public int PsiDisrupt, SpeakerIn;
}

public sealed partial class World
{
    // CSeasPsiDisruptor
    public const double DISRUPT_RADIUS = 40;
    public const string DISRUPT_PATH = "SEAS/Upgrades/seas_disruptor/reduce_speed";
    // CSeasSpeaker
    public const double SPEAKER_RADIUS = 50, SPEAKER_DEFENSE = 20;
    // CSeasStygiNest + its CNest seas_stygo_nest_spawn
    public const double NEST_RADIUS_1 = 70, NEST_RADIUS_2 = 140, NEST_GROW = 360, STYGI_SPAWN_RATE = 10;
    public const int NEW_EGGS = 2, STYGI_SPAWN_MAX = 2;
    public const string STYGI_SPAWN = "seas_stygimoloch";
    /// <summary>the region's check (the engine's region events, here a few times a second)</summary>
    const double SEAS_REGION_TICK = 0.25;

    static bool Mutual(Player? a, Player? b) => a != null && b != null && (a == b || (a.IsFriend(b) && b.IsFriend(a)));

    // ------------------------------------------------------------------------------------------ personal regions
    /// <summary>m_xSink OnObjAdd / OnObjRem: units entering the region get the flag (once per region), units leaving it
    /// or dying lose it</summary>
    void SeasRegionTick(Building b, double r, Func<Unit, bool> accept, Action<Unit, bool> flag)
    {
        var s = b.SeasRegion ??= new SeasRegionState();
        var now = new OrderedSet<Unit>();
        UHash.Query(b.Pos.X, b.Pos.Z, r + 1, u =>
        {
            if (!u.Alive || u.Inside || u.Parked) return;
            double dx = u.Pos.X - b.Pos.X, dz = u.Pos.Z - b.Pos.Z;
            if (dx * dx + dz * dz > r * r || !accept(u)) return;
            now.Add(u);
        });
        foreach (var u in s.In.ToList()) if (!now.Has(u)) { s.In.Delete(u); flag(u, false); }
        foreach (var u in now) if (s.In.Add(u)) flag(u, true);
    }

    /// <summary>the region goes with its building (DeletePersonalRegion): every unit in it loses the flag</summary>
    void SeasRegionClear(Building b, Action<Unit, bool> flag)
    {
        var s = b.SeasRegion;
        if (s == null) return;
        foreach (var u in s.In.ToList()) { s.In.Delete(u); flag(u, false); }
    }

    // ------------------------------------------------------------------------------------------ the psi disruptor
    sealed class PsiDisruptorB : BuildingBehaviour
    {
        // OnInit: SetAnim("open", 0, 0) - shut, held at the clip's first frame while a site
        public override void Init(Building b, World W)
        {
            if (b.Built || b.Anim == null || !b.Anim.Has("open")) return;
            var a = b.Anim.Play("open", new AnimOpts { Loop = false, Restart = true, Fade = 0 });
            if (a != null) { a.Time = 0; a.TimeScale = 0; b.LookChanged(); }
        }
        // SetReady: SetAnim("open", 1); the region (40 m)
        public override void Built(Building b, World W)
        {
            b.Anim?.Play("open", AnimOpts.Once(fade: 0));
            b.SeasRegion = new SeasRegionState();
        }
        public override void Update(Building b, World W, double dt)
        {
            var s = b.SeasRegion;
            if (s == null || (s.T -= dt) > 0) return;
            s.T = SEAS_REGION_TICK;
            W.SeasRegionTick(b, DISRUPT_RADIUS, u => W.Disruptable(b, u), W.PsiFlag);
        }
        public override void Destroyed(Building b, World W) => W.SeasRegionClear(b, W.PsiFlag);
    }

    static readonly HashSet<string> DISRUPT_TYPES = new(StringComparer.Ordinal) { "CHTR", "ANML", "VHCL", "SHIP" };
    /// <summary>CSeasPsiDisruptor.OnObjAdd: an enemy (either way) character, animal, vehicle or ship (wild animals are
    /// enemies)</summary>
    bool Disruptable(Building b, Unit u)
    {
        if (!DISRUPT_TYPES.Contains(u.Cls) || b.Owner == null) return false;
        return u.Owner == null || b.Owner.IsEnemy(u.Owner) || u.Owner.IsEnemy(b.Owner);
    }

    /// <summary>SetMirageFlag(MIRAGE_PSI_DISRUPT): the first disruptor switches the filter on, the last one off</summary>
    void PsiFlag(Unit u, bool on)
    {
        if (on) { if (u.PsiDisrupt++ == 0 && u.Alive && Data.Base.Filter(DISRUPT_PATH) != null) LocalFilter(u, DISRUPT_PATH, true); }
        else if (u.PsiDisrupt > 0 && --u.PsiDisrupt == 0 && u.LocalTT != null && u.LocalTT.Has(DISRUPT_PATH)) LocalFilter(u, DISRUPT_PATH, false);
    }

    // ------------------------------------------------------------------------------------------ the speaker
    sealed class SpeakerB : BuildingBehaviour
    {
        public override void Built(Building b, World W) => b.SeasRegion = new SeasRegionState();
        public override void Update(Building b, World W, double dt)
        {
            var s = b.SeasRegion;
            if (s == null || (s.T -= dt) > 0) return;
            s.T = SEAS_REGION_TICK;
            W.SeasRegionTick(b, SPEAKER_RADIUS, u => SpeakerTypes.Contains(u.Cls) && u.Owner != null && u.Owner == b.Owner, W.SpeakerFlag);
        }
        public override void Destroyed(Building b, World W) => W.SeasRegionClear(b, W.SpeakerFlag);
    }
    static readonly HashSet<string> SpeakerTypes = new(StringComparer.Ordinal) { "CHTR", "ANML", "VHCL" };
    void SpeakerFlag(Unit u, bool on) { if (on) u.SpeakerIn++; else if (u.SpeakerIn > 0) u.SpeakerIn--; }

    /// <summary>AddTemporaryDefenseBoni / AddTemporaryRangedDefenseBoni: + 20 in a speaker's region (Effects.TempDef)</summary>
    public static double SpeakerDef(Entity e) => e is Unit u && u.SpeakerIn > 0 ? SPEAKER_DEFENSE : 0;

    /// <summary>the buffs of these classes (Buffs.BuffsOf): the sources' owner_* (AddRangedBuff in OnInit / SetReady) and
    /// what acts on a unit</summary>
    public void SeasBuffs(Entity e, List<string> res)
    {
        if (e is Building b && b.Owner != null)
        {
            if (b.Def?.Script == "CSeasPsiDisruptor") res.Add("owner_less_attack_speed");
            else if (b.Def?.Script == "CSeasSpeaker" && b.Built) { res.Add("owner_more_defense"); res.Add("owner_more_armor"); }
        }
        if (e is Unit u)
        {
            if (u.PsiDisrupt > 0) res.Add("slower_fighting_rate");
            if (u.SpeakerIn > 0) res.Add("more_armor");
        }
    }

    // ------------------------------------------------------------------------------------------ the Stygimoloch nest
    sealed class StygiNestB : BuildingBehaviour
    {
        // SetReady: the grow timer, the hidden nest (its spawn timer starts: StartTimer)
        public override void Built(Building b, World W)
        {
            b.Stygi = new StygiNestState { SpawnT = STYGI_SPAWN_RATE, GrowT = NEST_GROW, CheckT = 2 };
        }
        public override void Update(Building b, World W, double dt)
        {
            var n = b.Stygi;
            if (n == null) return;
            n.Members.RemoveAll(u => !u.Alive);                         // (DieAnimal: StartTimer)
            if (n.GrowT != null && (n.GrowT -= dt) <= 0) { n.GrowT = null; n.Grow = NEW_EGGS; }   // NEST_GROW_TIMER: ChangeSpawnAmount
            int max = STYGI_SPAWN_MAX + n.Grow;
            if (n.Members.Count >= max) n.SpawnT = null;              // spawn max reached: the timer stops
            else
            {
                n.SpawnT ??= STYGI_SPAWN_RATE;                           // (StartTimer: a new one)
                if ((n.SpawnT -= dt) <= 0) { n.SpawnT = STYGI_SPAWN_RATE; W.StygiSpawn(b, n); }
            }
            if ((n.CheckT -= dt) <= 0) { n.CheckT = 2; W.StygiCheckArea(b, n); }
        }
        // CSeasStygiNest.Die: the nest dies; its animals live on, without a nest
        public override void Destroyed(Building b, World W)
        {
            if (b.Stygi == null) return;
            foreach (var u in b.Stygi.Members) if (u.Alive) u.HomeRadius = 0;
            b.Stygi.Members.Clear();
        }
    }

    /// <summary>CNest.SpawnAnimal: a grown (spawn_grown), autonomous (spawn_uninfluenced) stygimoloch of the owner at a free
    /// place by the nest</summary>
    void StygiSpawn(Building b, StygiNestState n)
    {
        var (x, z) = b.ExitPoint(b.Pos.X + 5, b.Pos.Z + 5);
        var u = SpawnUnit(STYGI_SPAWN, b.Owner, x, z, 1, 0, new UnitOpts { Autonomous = true });
        if (u == null) return;
        InheritInfection(b, u);
        u.HomeX = b.Pos.X; u.HomeZ = b.Pos.Z; u.HomeRadius = NEST_RADIUS_1;
        n.Members.Add(u);
    }

    /// <summary>TIMER_CHECK_AREA: an animal outside the action area (70 m) walks back to a point in it, unless it walks into
    /// it already, flees, or fights within the tolerance area (140 m)</summary>
    void StygiCheckArea(Building b, StygiNestState n)
    {
        foreach (var u in n.Members)
        {
            if (!u.Alive) continue;
            double d = Hypot(u.Pos.X - b.Pos.X, u.Pos.Z - b.Pos.Z);
            if (d <= NEST_RADIUS_1 || u.Task.Type == "flee") continue;
            var t = u.Task;
            if (t.Type == "roam" && t.Phase == "walk" && t.WalkTo is { } to && Hypot(to.X - b.Pos.X, to.Z - b.Pos.Z) <= NEST_RADIUS_1) continue;
            if (t.Type == "attack" && d <= NEST_RADIUS_2) continue;
            double a = Rng.Next() * Math.PI * 2, r = Math.Sqrt(Rng.Next()) * NEST_RADIUS_1;
            AnimalWalk(u, b.Pos.X + SMath.Cos(a) * r, b.Pos.Z + SMath.Sin(a) * r, true);
        }
    }

    // ------------------------------------------------------------------------------------------ the Tesla machine
    sealed class TeslaMachineB : BuildingBehaviour { }

    public static bool IsTeslaMachine(Entity? e) => e is Building b && b.Def?.Script == "CTeslaMachine";
    static readonly string[] TESLA_PRODUCERS = { "CRallyBuilding", "CTemple", "CNPCSeller", "CSeasFortress" };
    /// <summary>the classes whose CheckTeslaMachine / GetSpawnPos do something</summary>
    public bool TeslaProducer(Building b) => TESLA_PRODUCERS.Any(c => Data.IsA(b.Name, c));

    /// <summary>CheckTeslaMachine(rally target), at every new rally point: a finished, own or mutually allied Tesla machine
    /// - else none</summary>
    public void CheckTeslaMachine(Building producer, Entity? target)
    {
        producer.TeslaM = null;
        if (!TeslaProducer(producer) || target is not Building m || !IsTeslaMachine(m) || !m.Alive || !m.Built) return;
        if (!Mutual(m.Owner, producer.Owner)) return;
        producer.TeslaM = m;
    }

    /// <summary>GetSpawnPos: the Tesla machine's position + its radius along the world's y axis (the remake's z), and its
    /// rotation - while it stands finished; null: the producer's own door</summary>
    public (double X, double Z, double Rot)? TeslaSpawn(Building b)
    {
        var m = b.TeslaM;
        if (m == null || !m.Alive || !m.Built) return null;
        double x = m.Origin.X, z = m.Origin.Z + m.Radius;
        var nav = Nav;
        if (!nav.IsFree(x, z)) { int c = nav.NearestFree(nav.Idx(x, z)); if (c >= 0) (x, z) = nav.Center(c); }
        return (x, z, m.Rot);
    }

    // ------------------------------------------------------------------------------------------ the SEAS warp gate
    /// <summary>a warp gate's class: CWarpGate or MIRAGE's CSeasWarpGate (the scripts cast to CWarpGate: the AI's
    /// targets and requests, one gate per player)</summary>
    public static bool IsWarpGateScript(string? script) => script == "CWarpGate" || script == "CSeasWarpGate";

    // ------------------------------------------------------------------------------------------ parts: turrets, decorations
    /// <summary>MIRAGE's decorations (CMirageDeco, linked at "NOPE" with an offset in the building's frame - GSF metres - in
    /// SetReady: BuildAddons); the first of a list with Destroy plays "destroy" when the building dies</summary>
    sealed record Deco(string Gfx, double X, double Y, double Z);
    static readonly Dictionary<string, (Deco[] Parts, bool Destroy)> DECOS = new(StringComparer.Ordinal)
    {
        // CSeasMine.BuildAddons: seas_mine_misc_0..5
        ["seas_mine"] = (new Deco[] { new("seas_hq_house_addon_06", 1.82, 6.54, 0), new("seas_hq_house_addon_11", -1.75, 4.54, 3.01), new("seas_hq_house_addon_24", -4.89, -0.41, -3.07),
            new("seas_hq_house_addon_26", -1.43, 0.39, 6.39), new("seas_hq_house_addon_33", 6.94, -0.02, 0), new("seas_hq_house_addon_39", 1.63, -6.16, 0) }, false),
        // CSeasPub.BuildAddons: the gate, the antenna (seas_pub_misc_0 / 1)
        ["seas_pub"] = (new Deco[] { new("seas_hq_house_addon_07", -1.0, 3.05, -0.3), new("seas_hq_house_addon_14", 5.17, 2.28, 3.34) }, false),
        // CSeasWarpGate.BuildAddons: seas_wg_misc_0 / 1; Die: the first plays "destroy"
        ["seas_warpgate"] = (new Deco[] { new("scorpion_hut_04_dest", 0, -1.47, -5.67), new("seas_hq_babbage_machine", 0.07, 1.88, -0.51) }, true),
        // CSeasPortalTerminus.BuildAddons: seas_wg_misc_0 (the second is commented out); Die: "destroy"
        ["seas_portal_terminus"] = (new Deco[] { new("scorpion_hut_04_dest", 0, -1.47, -5.67) }, true),
    };

    /// <summary>the towers whose turret (CTower.SetTurret, SetConstructLevel(4)) comes in SetReady - the table's "always"
    /// would show it on the site</summary>
    static readonly HashSet<string> READY_TURRETS = new(StringComparer.Ordinal) { "CSeasMgNest", "CSeasSmallCannon", "CSeasShoreCannon", "CSeasGreatCannon" };
    const string GREAT_CANNON_BARREL = "seas_hq_big_cannon_cannon";
    readonly Dictionary<string, List<CompositeSpec>> _seasSpecs = new(StringComparer.Ordinal);

    /// <summary>the parts a building shows (Building.UpdateTurret): the composites table, with the SEAS towers' turrets
    /// only once finished (the great cannon's rotator from 80 % of the construction on) and MIRAGE's decorations</summary>
    public List<CompositeSpec> PartSpecs(Building b, List<CompositeSpec> specs)
    {
        var script = b.Def?.Script;
        bool towers = script != null && READY_TURRETS.Contains(script);
        if (!towers && !DECOS.ContainsKey(b.Name)) return specs;
        if (!_seasSpecs.TryGetValue(b.Name, out var mine))
        {
            mine = new List<CompositeSpec>();
            foreach (var s in specs)
            {
                if (!towers || s.Kind != "turret") { mine.Add(s); continue; }
                var c = CopySpec(s);
                c.Whole = true;
                // CSeasGreatCannon.SetProcess (>= 80 %): the rotator; SetReady: the barrel
                c.Cond = script == "CSeasGreatCannon" && s.Pi < 0 ? "process80" : "ready";
                mine.Add(c);
            }
            if (DECOS.TryGetValue(b.Name, out var d))
                for (int i = 0; i < d.Parts.Length; i++)
                {
                    var p = d.Parts[i];
                    var o = new JsonArray(p.X, p.Y, p.Z);
                    mine.Add(new CompositeSpec
                    {
                        I = 100 + i, Kind = "deco", Gfx = p.Gfx, Link = "NOPE", Pi = -1, Cond = "ready", Whole = true, Offset = o,
                        OffsetModel = new V3(-p.X, p.Z, -p.Y), Anim = d.Destroy && i == 0 ? "destroy" : null,
                    });
                }
            _seasSpecs[b.Name] = mine;
        }
        // (the composites' own conditions, but for the 80 % one; "ready" is checked by Compose.ActiveParts)
        var res = new List<CompositeSpec>(mine.Count);
        foreach (var s in mine)
        {
            if (s.Cond == "process80") { if (b.Built || b.Progress >= 0.8 - 1e-9) res.Add(WholeAlways(s)); continue; }
            res.Add(s);
        }
        return res;
    }

    // (one "always" copy per spec: the parts' key is by index and model, the same copy keeps it the same)
    readonly Dictionary<CompositeSpec, CompositeSpec> _always = new();
    CompositeSpec WholeAlways(CompositeSpec s)
    {
        if (!_always.TryGetValue(s, out var c)) { c = CopySpec(s); c.Cond = "always"; _always[s] = c; }
        return c;
    }

    static CompositeSpec CopySpec(CompositeSpec s) => new()
    {
        I = s.I, Kind = s.Kind, Gfx = s.Gfx, Variants = s.Variants, By = s.By, Link = s.Link, Pi = s.Pi, Anim = s.Anim, AttackAnim = s.AttackAnim,
        Cond = s.Cond, ArgStr = s.ArgStr, ArgList = s.ArgList, Flex = s.Flex, WeaponClass = s.WeaponClass, Offset = s.Offset, OffsetModel = s.OffsetModel, Whole = s.Whole,
    };

    /// <summary>a decoration's part (CMirageDeco): hung at the building's origin with its offset (the link "NOPE" no
    /// model has); null: no such model</summary>
    public static AttachedPart? AttachDeco(World W, Entity host, CompositeSpec s)
    {
        var M = W.Models;
        var g = s.Gfx.ToLowerInvariant();
        if (!M.Has(g)) return null;
        var clips = M.Clips(g);
        return new AttachedPart { Gfx = g, Link = s.Link, Fourcc = M.Fourcc(g), Anim = clips.Count > 0 ? new AnimCtl(clips, host) : null };
    }

    /// <summary>the decoration that plays "destroy" when its building dies (CSeasWarpGate / CSeasPortalTerminus.Die): it
    /// stays as a ruin part (Construction.RuinPartsOf)</summary>
    public AttachedPart? DecoRuin(AttachedPart p)
    {
        if (p.Spec?.Kind != "deco" || p.Spec.Anim != "destroy" || !Models.Clips(p.Gfx).Has("destroy")) return null;
        return new AttachedPart { Spec = p.Spec, Gfx = p.Gfx, Link = p.Link, Fourcc = p.Fourcc, VisLevel = p.VisLevel, VisDmg = p.VisDmg, Night = p.Night, From = p.Gfx };
    }

    /// <summary>a site's construction went on (SetProcess): the class's behaviour (BuildingBehaviour.SiteProgress - the
    /// launch pads' build-ups, MirageBuildings.cs) and CSeasGreatCannon's rotator at 80 %. Called by AddProgress and by
    /// every step of a site (a progress set otherwise: scripts, the debug build)</summary>
    public void SiteProgressed(Building b)
    {
        if (b.Built || !b.Alive) return;
        (b.Behaviour as BuildingBehaviour)?.SiteProgress(b, this);
        if (b.Def?.Script == "CSeasGreatCannon") b.UpdateTurret();
    }

    /// <summary>a tower fired (CTower.AttackEnemy, the shot): the great cannon's barrel plays "attack_front" too</summary>
    public void TowerFired(Building b)
    {
        if (b.Def?.Script != "CSeasGreatCannon") return;
        foreach (var p in b.Parts)
            if (p.Gfx == GREAT_CANNON_BARREL && p.Anim != null && p.Anim.Has("attack_front")) p.Anim.Play("attack_front", AnimOpts.Once());
    }
}
