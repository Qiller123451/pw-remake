// MIRAGE's building classes of the Norsemen, the Dustriders and of every tribe that stood as plain buildings, the
// titans' launch pads' build-up decorations, the pirate boss ship's parts, the option FlyingEnabled and the SEAS ruins
// that stay (src/Sim/Systems/MirageClasses.cs, PortalTerminus.cs, PirateShip.cs, MirageBuildings.cs; Construction.cs
// PermanentRuin; Rules/GameplayOptions.cs). Kacper: "please port the rest of these, we do not want to lose any features
// from the original".
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Game;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class MirageClassesTests
{
    static void Rich(Player p) { p.CapsFixed = true; foreach (var r in Rules.RES) { p.Caps[r] = 20000; p[r] = 10000; } p["skulls"] = 2000; }

    static (W_ W, Player P, Player E) World(TestContext t, string tribe = "Hu", string enemy = "Aje", GameplayOptions? o = null)
    {
        var (w, p, e) = Sys2.World(t, Sys2.Mirage, tribe, enemy);
        if (o != null) { p.Gameplay = o; p.Tt = w.Data.PlayerTree(tribe, o); }
        return (w, p, e);
    }
    static Building Place(TestContext t, W_ w, string name, Player? p, double x, double z, bool built = true)
    {
        var b = w.PlaceBuilding(name, p, x, z, 0, built);
        if (b == null) t.Skip("no model for " + name);
        return b!;
    }
    static ActionDef? Act(W_ w, Player p, string path) => w.Data.Actions(p).FirstOrDefault(a => a.Path == path);
    static void FinalStage(W_ w, Player p)
    {
        var bas = new Dictionary<string, string> { ["SEAS"] = "seas_headquarters", ["Hu"] = "hu_fireplace", ["Aje"] = "aje_resource_collector", ["Ninigi"] = "ninigi_fireplace" }[p.Tribe];
        w.RaiseEpoch(p, 5);
        w.CompleteUpgrade(null, Act(w, p, $"/Actions/{p.Tribe}/Upgrades/{bas}/final_stage")!, p);
    }

    // ------------------------------------------------------------------------------------------ C: FlyingEnabled
    /// <summary>MIRAGE's default (FlyingEnabled off): xenage/flying_disabled for every tribe - the flyers' build actions
    /// hidden and disabled, the jetpack move too, ai_cap 0, the zeppelins no flying units; with the option, as before; the
    /// base game has no such filter</summary>
    public static void FlyingDisabledByDefault(TestContext t)
    {
        var flyers = new[]
        {
            ("Hu", "/Actions/Hu/Build/ANML/hu_pteranodon", "/Objects/Hu/ANML/hu_pteranodon/ai_cap"),
            ("Aje", "/Actions/Aje/Build/ANML/aje_pteranodon", "/Objects/Aje/ANML/aje_pteranodon/ai_cap"),
            ("Ninigi", "/Actions/Ninigi/Build/VHCL/ninigi_cargolifter", "/Objects/Ninigi/VHCL/ninigi_cargolifter/ai_cap"),
            ("Ninigi", "/Actions/Ninigi/Build/VHCL/ninigi_dirigible", "/Objects/Ninigi/VHCL/ninigi_dirigible/ai_cap"),
            ("SEAS", "/Actions/SEAS/Build/VHCL/seas_helicopter_flying", null),
        };
        t.True(!GameplayOptions.Default.FlyingEnabled, "MIRAGE's default: no flying units (SetDefaultStuff FlyingEnabled false)");
        foreach (var (tribe, path, cap) in flyers)
        {
            var (w, p, _) = World(t, tribe);
            t.Equal(1, p.Tt!.EnabledCount($"{tribe}/Upgrades/xenage/flying_disabled"), $"{tribe}: flying_disabled on by default");
            var a = Act(w, p, path);
            t.True(a != null && !a.Visible && a.Disabled, $"{tribe}: {path} hidden and disabled");
            if (cap != null) t.Equal(0.0, w.Data.Tree(p).Num(cap, -1), $"{tribe}: {cap} 0");
            t.Equal("false", w.Data.Tree(p).Get($"/Objects/{tribe}/VHCL/{tribe.ToLowerInvariant()}_zeppelin/flying_unit", "?"), $"{tribe}: the zeppelin is no flying unit");
            var (w2, p2, _) = World(t, tribe, o: new GameplayOptions { FlyingEnabled = true });
            t.Equal(0, p2.Tt!.EnabledCount($"{tribe}/Upgrades/xenage/flying_disabled"), $"{tribe}: no filter with the option");
            var a2 = Act(w2, p2, path);
            t.True(a2 != null && !a2.Disabled, $"{tribe}: {path} enabled with the option");
        }
        // the units after the flyers move up in their menus (menupos -1 / -2): none of them drops out of its menu
        foreach (var tribe in new[] { "Hu", "Aje", "Ninigi", "SEAS" })
        {
            var (w, p, _) = World(t, tribe);
            var (w2, p2, _) = World(t, tribe, o: new GameplayOptions { FlyingEnabled = true });
            var on = w2.Data.Actions(p2).Where(a => a.Locs.Any(l => !l.Hidden)).Select(a => a.Path).ToHashSet();
            var lost = w.Data.Actions(p).Where(a => on.Contains(a.Path) && a.Locs.All(l => l.Hidden) && !a.Disabled).Select(a => a.Path).ToList();
            t.Equal("", string.Join(", ", lost), $"{tribe}: no unit pushed out of its menu");
            t.True(!w.Data.Actions(p).Any(a => a.Id == "ninigi_ninigi_helicopter"), $"{tribe}: no action of the filter's misspelt path");
        }
        {
            var (w, p, _) = World(t, "Hu");
            var jet = Act(w, p, "/Actions/Hu/Moves/CHTR/jetfly_on");
            t.True(jet == null || (!jet.Visible && jet.Disabled), "Hu: the jetpack move hidden and disabled");
        }
        var (wb, pb, _) = Sys2.World(t, Sys2.Base, "Hu");
        t.Equal(0, pb.Tt!.OwnEnabled.Count, "base game: no option filters");
        // the skirmish setup carries it (JSON "flyingEnabled") into every player's tree
        var s = Skirmish.DefaultSetup("Hu", "Aje");
        t.True(!s.FlyingEnabled, "the skirmish setup's default: off");
        s.FlyingEnabled = true; s.Portals = true;
        var back = SkirmishSetup.FromJson(s.ToJson());
        t.True(back!.FlyingEnabled && back.Portals, "the setup keeps the options (JSON)");
        var go = System.Text.Json.JsonSerializer.Deserialize<GameplayOptions>("{\"epochSix\":true}")!;
        t.True(!go.FlyingEnabled && !go.Portals && go.IsDefault, "an options object without the key: MIRAGE's default");
    }

    // ------------------------------------------------------------------------------------------ D: permanent ruins
    /// <summary>CBuildingCorpse.ShouldStay: the SEAS headquarters' bunker, big cannon (and its rotator), defense turret,
    /// machine-gun nest and the city gates' ruins stay for good; MIRAGE's own classes with those models (IsException)
    /// sink; the shore cannon's stays; a ruin blocks nothing</summary>
    public static void PermanentRuins(TestContext t)
    {
        t.True(W_.PermanentRuin("seas_hq_bunker_dest", "seas_hq_bunker"), "bunker");
        t.True(W_.PermanentRuin("seas_hq_big_cannon_rotator_dest", "seas_hq_big_cannon"), "big cannon rotator");
        t.True(W_.PermanentRuin("PT_Citywall_Gate_dest", "pt_citywall_gate"), "city gate");
        t.True(!W_.PermanentRuin("seas_hq_machinegun_nest_top_dest", "seas_hq_machinegun_nest"), "the nest's top sinks");
        t.True(!W_.PermanentRuin("seas_hq_bunker_dest", "seas_fortress"), "MIRAGE's fortress sinks (IsException)");
        t.True(!W_.PermanentRuin("seas_hq_defense_turret_dest", "seas_small_cannon"), "MIRAGE's small cannon sinks");
        t.True(W_.PermanentRuin("seas_hq_defense_turret_dest", "seas_shore_cannon"), "MIRAGE's shore cannon's ruin stays");
        t.True(!W_.PermanentRuin("hu_fireplace_dest", "hu_fireplace"), "others sink");
        var (w, p, _) = Sys2.World(t, Sys2.Base, "SEAS");
        var bunker = Place(t, w, "seas_hq_bunker", p, 0, 0);
        var other = Place(t, w, "seas_hq_machinegun_nest", p, 60, 0);
        var cells = bunker.Cells.ToList();
        t.True(cells.Any(k => w.Nav.Block[k] != 0), "the bunker blocks its cells");
        w.KillBuilding(bunker, null); w.KillBuilding(other, null);
        t.True(bunker.Ruin == "seas_hq_bunker_dest" && bunker.RuinStays, "the bunker's ruin stays");
        t.True(other.RuinStays, "the nest's ruin stays");
        Sys2.Run(w, 60);
        t.True(!bunker.Removed && bunker.SinkY == 0 && w.Buildings.Contains(bunker), "still there after a minute, not sunk");
        t.True(cells.All(k => w.Nav.Block[k] == 0), "the ruin blocks nothing");
        t.True(other.RuinParts == null || other.RuinParts.All(rp => !rp.Gfx.Contains("_top_")), "the nest's top ruin is gone, its base stays");
        // a MIRAGE class with the same model sinks as every ruin
        var (wm, pm, _) = Sys2.World(t, Sys2.Mirage, "SEAS");
        var fort = Place(t, wm, "seas_fortress", pm, 0, 0);
        wm.KillBuilding(fort, null);
        t.True(fort.Ruin != null && !fort.RuinStays, "seas_fortress: its ruin sinks");
        Sys2.Run(wm, 21);
        t.True(fort.Removed, "gone after 20 s");
    }

    /// <summary>saved and loaded: a save is the start and the commands - the bunker destroyed by its owner's command stays
    /// a ruin in the replayed world too, the same state hash</summary>
    public static void PermanentRuinSavedAndLoaded(TestContext t)
    {
        (W_ W, Building B) Make()
        {
            var (w, p, _) = Sys2.World(t, Sys2.Base, "SEAS");
            var b = Place(t, w, "seas_hq_bunker", p, 0, 0);
            return (w, b);
        }
        var (wa, ba) = Make();
        var rec = new GameRecorder(wa, wa.StateHash, null);
        wa.Enqueue(new KillCommand { Player = ba.Owner!.Id, Entities = new[] { ba.Id } });
        Sys2.Run(wa, 40);
        var save = rec.Capture(new GameStart { Configuration = "base", Mode = GameMode.Skirmish, Seed = 7 }, SimFlags.Game);
        t.True(ba.RuinStays && !ba.Removed, "the bunker's ruin stands");
        var (wb, bb) = Make();
        var rep = new SaveReplay(save, wb, wb.StateHash, null);
        rep.Run(); rep.End();
        t.True(!rep.Diverged, $"the replay follows the save ({rep.Problem})");
        t.True(!bb.Alive && bb.RuinStays && !bb.Removed && bb.Ruin == "seas_hq_bunker_dest", "the loaded game has the ruin");
        t.Equal(wa.StateHash(), wb.StateHash(), "the same world");
    }

    // ------------------------------------------------------------------------------------------ A: the classes
    /// <summary>CHuMine: a field of stone - the worker hacks stone with its pick ("hacking_stone", GetCorn.usl), carries
    /// it in its pannier, not as a sheaf</summary>
    public static void HuMineHacksStone(TestContext t)
    {
        var (w, p, _) = World(t, "Hu");
        FinalStage(w, p);
        var a = Act(w, p, "/Actions/Hu/Build/BLDG/hu_mine");
        t.True(a != null && w.Data.Check(p, a) == null, "hu_mine buildable after the final stage");
        t.Equal(2000.0, a!.Cost.Wood, "2000 wood");
        var mine = Place(t, w, "hu_mine", p, 0, 0);
        t.True(w.IsFarm(mine) && W_.IsMineField(mine), "a field and a mine");
        t.Equal("stone", mine.Def!.Unlimited[0], "it gives stone");
        var u = w.SpawnUnit("hu_worker", p, 15, 0)!;
        w.Order(u, new Order("gather") { Target = mine });
        bool hacked = false, pick = false;
        for (int i = 0; i < 1200 && !(u.Carry != null && u.Task.Phase != "work"); i++)
        {
            w.Step();
            if (u.Task.Type == "gather" && u.Task.Phase == "work")
            {
                if (u.Anim?.CurName?.StartsWith("hacking", StringComparison.Ordinal) == true) hacked = true;
                if (u.ToolGfx == "hu_pick") pick = true;
            }
        }
        t.True(pick, "the worker holds its pick");
        t.True(hacked || u.Anim?.Has("hacking_stone") != true, "it hacks stone (hacking_stone)");
        t.True(u.Carry != null && u.Carry.Res == "stone", "it carries stone");
        t.True(u.Carry!.Look == null || u.Carry.Look.Length < 2 || u.Carry.Look[1] != "hu_corn", "in its pannier, not a sheaf");
    }

    /// <summary>CHuMudPit: an enemy walking into its 12 m gets broken legs for 45 s (its slowest gait); the pit stays
    /// hidden; own units, flying units are not caught</summary>
    public static void MudPitBreaksLegs(TestContext t)
    {
        var (w, p, e) = World(t, "Hu", "Aje");
        var pit = Place(t, w, "hu_mud_pit", p, 0, 0);
        t.True(pit.St.Camo.Has("trap"), "hidden");
        t.True(Buffs.BuffsOf(w, pit).Contains("owner_less_attack_speed"), "owner_less_attack_speed (AddRangedBuff)");
        var v = w.SpawnUnit("aje_worker", e, 40, 0)!;
        var own = w.SpawnUnit("hu_worker", p, -40, 2)!;
        double fast = v.RunSpeed;
        w.Order(v, new Order("move") { X = -40, Z = 0 });
        w.Order(own, new Order("move") { X = 40, Z = 2 });
        double t0 = -1, maxAfter = 0;
        for (int i = 0; i < 1500; i++)
        {
            w.Step();
            if (t0 < 0 && v.Active("brokenLegs")) t0 = w.Time;
            if (t0 > 0 && w.Time - t0 > 0.5 && v.Path.Count > 0) maxAfter = Math.Max(maxAfter, Math.Sqrt(v.Vel.X * v.Vel.X + v.Vel.Z * v.Vel.Z));
        }
        t.True(t0 > 0, "caught walking through");
        t.True(maxAfter <= v.GaitSpeed(1) + 1e-6 && v.GaitSpeed(1) < fast, $"at its slowest gait ({maxAfter:0.00} <= {v.GaitSpeed(1):0.00}, ran {fast:0.00})");
        t.Near(t0 + W_.MUD_PIT_LEGS, v.St.T["brokenLegs"], 0.05, "for 45 s");
        t.True(!own.Active("brokenLegs"), "not the owner's units");
        t.True(pit.St.Camo.Has("trap"), "still hidden (no OnReveal)");
    }

    /// <summary>CHuPunchingBag: walked through (SetPFBlocking empty), no field though the tree says "unlimited stone";
    /// its build action stays hidden in MIRAGE</summary>
    public static void PunchingBagWalkedThrough(TestContext t)
    {
        var (w, p, _) = World(t, "Hu");
        var bag = Place(t, w, "hu_punching_bag", p, 0, 0);
        t.True(bag.Cells.All(k => w.Nav.Block[k] == 0), "it blocks nothing");
        t.True(!w.IsFarm(bag), "no field");
        FinalStage(w, p);
        var a = Act(w, p, "/Actions/Hu/Build/BLDG/hu_punching_bag");
        t.True(a == null || !a.Visible, "hidden as in MIRAGE (-1, final stage +1)");
    }

    /// <summary>CHuBigHorn and CAjeAmazonTemple: the ready effects and buffs by class (the temple is a CTemple: heal glow,
    /// owner_healing_temple, it heals); the temple's corpse is the temple at destruction level 2, sinking after 3 s</summary>
    public static void BigHornAndAmazonTemple(TestContext t)
    {
        var (w, p, _) = World(t, "Aje");
        t.Equal("fx_will_o_wisp_sml", Building.ReadyFxOf(w.Data, "hu_big_horn"), "the big horn's will-o'-the-wisp");
        t.Equal("fx_heal_area", Building.ReadyFxOf(w.Data, "aje_amazon_temple"), "the temple's heal glow (a CTemple)");
        FinalStage(w, p);
        var a = Act(w, p, "/Actions/Aje/Build/BLDG/aje_amazon_temple");
        t.True(a != null && w.Data.Check(p, a) == null, "buildable after the final stage");
        var tp = Place(t, w, "aje_amazon_temple", p, 0, 0);
        t.True(Buffs.BuffsOf(w, tp).Contains("owner_healing_temple"), "owner_healing_temple");
        t.True(tp.Stats!.Heal != null, "it heals (special_abilities heal)");
        var u = w.SpawnUnit("aje_worker", p, 12, 0)!;
        u.Hp = u.MaxHp / 2;
        Sys2.Run(w, 3);
        t.True(u.Hp > u.MaxHp / 2, "a hurt unit beside it heals");
        w.KillBuilding(tp, null);
        t.Equal("amazon_temple", tp.Ruin, "its corpse is the temple itself");
        t.Equal(2, tp.VisDmg, "at destruction level 2");
        Sys2.Run(w, 2.9);
        t.Equal(0.0, tp.SinkY, "stands 3 s");
        Sys2.Run(w, 1);
        t.True(tp.SinkY > 0, "then sinks");
        Sys2.Run(w, 12);
        t.True(tp.Removed, "and is gone");
    }

    /// <summary>CAjeSensorTower: every 2 s the enemies in its sight lose their camouflage, hidden traps are found and
    /// hidden again after 20 s; its guard stands on top</summary>
    public static void SensorTowerReveals(TestContext t)
    {
        var (w, p, e) = World(t, "Aje", "Ninigi");
        FinalStage(w, p);
        var a = Act(w, p, "/Actions/Aje/Build/BLDG/aje_sensor_tower");
        t.True(a != null && w.Data.Check(p, a) == null, "buildable after the final stage");
        var trapName = w.Data.Index.Keys.FirstOrDefault(n => n.StartsWith("ninigi_") && w.Data.Script(n) == "CPitfall");
        var trap = trapName != null ? w.PlaceBuilding(trapName, e, -40, 20) : null;
        var ninja = w.SpawnUnit("ninigi_ninja", e, -50, 0)!;
        ninja.St.Camo.Add("disg");
        var far = w.SpawnUnit("ninigi_ninja", e, 110, 0)!;          // (the test map: -128 .. 128)
        far.St.Camo.Add("disg");
        t.True(trap == null || trap.St.Camo.Has("trap"), "the trap hidden");
        var tower = Place(t, w, "aje_sensor_tower", p, -100, 0, built: false);
        t.Equal(135.0, tower.Fow, "its sight: 135 m");
        w.FinishBuilding(tower);
        t.True(!ninja.St.Camo.Has("disg"), "the ninja's disguise is gone (in sight)");
        t.True(far.St.Camo.Has("disg"), "not out of sight");
        if (trap != null)
        {
            t.True(!trap.St.Camo.Has("trap"), "the trap is found");
            // (the tower keeps finding it every 2 s while it stands: hidden again 20 s after the tower is gone)
            w.KillBuilding(tower, null);
            Sys2.Run(w, 20.5);
            t.True(trap.St.Camo.Has("trap"), "hidden again after 20 s");
        }
        t.True(tower.Parts.Any(pp => pp.OwnState && pp.Gfx.Contains("velociraptor_handler")) || !w.Models.Has("amazons_aje_velociraptor_handler_s1"), "the guard on top");
    }

    /// <summary>CAjeGalliNest: gallimimus one every 10 s up to 3, within 70 m; 360 s after it was finished up to 5; they
    /// die with it</summary>
    public static void GallimimusNest(TestContext t)
    {
        var (w, p, _) = World(t, "Aje");
        if (!w.Data.Exists("aje_gallimimus")) t.Skip("no aje_gallimimus");
        var n = Place(t, w, "aje_gallimimus_nest", p, 0, 0);
        Sys2.Run(w, 9.9);
        t.Equal(0, n.Galli!.Members.Count, "none before 10 s");
        Sys2.Run(w, 30);
        t.Equal(3, n.Galli.Members.Count, "3 after 30 s");
        t.True(n.Galli.Members.All(u => u.Name == "aje_gallimimus" && u.Owner == p && u.HomeRadius == 70), "the owner's gallimimus, kept within 70 m");
        Sys2.Run(w, 340);
        t.True(n.Galli.Grown, "grown after 360 s");
        Sys2.Run(w, 25);
        t.Equal(5, n.Galli.Members.Count, "5 then");
        var members = n.Galli.Members.ToList();
        w.KillBuilding(n, null);
        t.True(members.All(u => !u.Alive), "they die with the nest");
    }

    /// <summary>CPortalTerminus: a terminus' rally point on another of its side makes that its destination; a unit sent in
    /// comes out at the destination's release point, one a second; a destroyed destination is nobody's; one that does
    /// not work lets the units just walk there; buildable only with the option Portals</summary>
    public static void PortalTerminusTeleports(TestContext t)
    {
        var (w, p, e) = World(t, "Aje", "Hu");
        w.RaiseEpoch(p, 5);
        var act = Act(w, p, "/Actions/Aje/Build/BLDG/aje_portal_terminus");
        t.Equal("hidden", w.Data.Check(p, act!), "hidden without the option Portals (-1 + age_5)");
        var (w2, p2, _) = World(t, "Aje", o: new GameplayOptions { Portals = true });
        w2.RaiseEpoch(p2, 5);
        var act2 = Act(w2, p2, "/Actions/Aje/Build/BLDG/aje_portal_terminus")!;
        p2.Debug = true;
        t.Equal(null, w2.Data.Check(p2, act2), "buildable with Portals at epoch 5");
        t.Equal(800.0, act2.Cost.Food, "800 food");
        var a = Place(t, w, "aje_portal_terminus", p, 0, 0);
        var b = Place(t, w, "aje_portal_terminus", p, 150, 0);
        t.True(W_.IsPortal(a) && W_.IsPortal(b), "termini");
        var u1 = w.SpawnUnit("aje_worker", p, -20, 0)!;
        var u2 = w.SpawnUnit("aje_worker", p, -20, 5)!;
        w.Order(u1, new Order("portal") { Target = a });
        t.True(u1.Task.Type != "portal", "a terminus without a destination: the unit only walks there");
        new SetRallyCommand { Player = p.Id, Producers = new[] { a.Id }, Target = b.Id, X = b.Pos.X, Z = b.Pos.Z }.Apply(w);
        t.True(a.Portal!.Dest == b && b.Portal!.Partners.Contains(a), "b is a's destination");
        t.True(w.PortalWorks(a), "a works");
        var rel = b.Portal!.ReleasePos;
        w.Order(u1, new Order("portal") { Target = a });
        w.Order(u2, new Order("portal") { Target = a });
        double t1 = -1, t2 = -1;
        for (int i = 0; i < 2000 && (t1 < 0 || t2 < 0); i++)
        {
            w.Step();
            if (t1 < 0 && Math.Abs(u1.Pos.X - rel.X) < 4 && Math.Abs(u1.Pos.Z - rel.Z) < 4) t1 = w.Time;
            if (t2 < 0 && Math.Abs(u2.Pos.X - rel.X) < 4 && Math.Abs(u2.Pos.Z - rel.Z) < 4) t2 = w.Time;
        }
        t.True(t1 > 0 && t2 > 0, "both came out at b");
        t.True(Math.Abs(t2 - t1) >= W_.PORTAL_RELEASE - 1e-6, $"one a second ({Math.Abs(t2 - t1):0.00} s apart)");
        // an enemy may not use it
        var foe = w.SpawnUnit("hu_worker", e, -20, -5)!;
        t.True(!w.CanEnterPortal(foe, a), "not an enemy");
        // a producer's rally point on a terminus: its rally position is the release point
        // the destination destroyed: a no longer works
        w.KillBuilding(b, null);
        t.True(a.Portal.Dest == null && !w.PortalWorks(a), "a destroyed destination is nobody's");
        // its rally point set on itself clears it
        var c = Place(t, w, "aje_portal_terminus", p, -100, 60);
        new SetRallyCommand { Player = p.Id, Producers = new[] { a.Id }, Target = c.Id }.Apply(w);
        t.True(a.Portal.Dest == c, "c now");
        new SetRallyCommand { Player = p.Id, Producers = new[] { a.Id }, Target = a.Id }.Apply(w);
        t.True(a.Portal.Dest == null, "set on itself: none");
    }

    /// <summary>CZeppelinLaunchPad: counts in the unit limit from the site on; finished it becomes the tribe's zeppelin at
    /// the action's level, without a ruin</summary>
    public static void ZeppelinLaunchPad(TestContext t)
    {
        foreach (var tribe in new[] { "Hu", "SEAS" })
        {
            var (w, p, _) = World(t, tribe);
            var pad = Place(t, w, tribe.ToLowerInvariant() + "_launchpad_zeppelin", p, 0, 0, built: false);
            t.True(pad.CountsInPop, $"{tribe}: the site counts in the unit limit");
            int units = p.Units;
            w.FinishBuilding(pad);
            Sys2.Run(w, 0.2);
            var z = w.Units.FirstOrDefault(u => u.Alive && u.Name == tribe.ToLowerInvariant() + "_zeppelin");
            t.True(z != null && !pad.Alive && pad.Ruin == null, $"{tribe}: the zeppelin took its place");
            t.Equal(1, z!.Level, $"{tribe}: at the action's level 1");
            t.Equal(units, p.Units, $"{tribe}: the zeppelin takes the pad's card");
        }
    }

    /// <summary>CSpecialBox: a lemur and a sloth on it once finished (special_box_misc_0 / _1), 1.35 m left and right,
    /// 0.3 m up, helmet and armour shown, resting</summary>
    public static void SpecialBoxAnimals(TestContext t)
    {
        var (w, p, _) = World(t, "SEAS");
        var box = Place(t, w, "seas_special_box", p, 0, 0, built: false);
        t.True(!box.Parts.Any(pp => pp.OwnState), "nothing on the site");
        w.FinishBuilding(box);
        var deco = box.Parts.Where(pp => pp.OwnState).ToList();
        if (!w.Models.Has("lemur")) t.Skip("no lemur model");
        t.Equal(2, deco.Count, "two animals");
        t.True(deco[0].Gfx == "lemur" && deco[1].Gfx == "sloth", "a lemur and a sloth");
        t.Near(-1.35, deco[0].Spec!.Offset![0]!.GetValue<double>(), 1e-9, "the lemur 1.35 m left");
        t.True(deco.All(d => d.VisFlags != null && d.VisFlags.Contains((7, true)) && d.VisFlags.Contains((8, true))), "helmet and armour");
    }

    /// <summary>CGoods: every 5 s its owner gets 25 wood and 25 stone (no food)</summary>
    public static void GoodsDeliver(TestContext t)
    {
        var (w, p, _) = World(t, "Hu");
        t.Equal("CGoods", w.Data.Script("hu_goods"), "hu_goods is a CGoods (no tree object: a map object)");
        Rich(p);
        var b = Place(t, w, "hu_fireplace", p, 0, 0, built: false);
        b.Behaviour = W_.MirageBehaviour("CGoods");
        w.FinishBuilding(b);
        double wood = p["wood"], stone = p["stone"], food = p["food"];
        Sys2.Run(w, 4.9);
        t.Equal(wood, p["wood"], "nothing before 5 s");
        Sys2.Run(w, 5.2);
        t.Equal(wood + 50, p["wood"], "25 wood every 5 s");
        t.Equal(stone + 50, p["stone"], "25 stone");
        t.Equal(food, p["food"], "no food");
    }

    /// <summary>CHCGate: a decoration that blocks nothing</summary>
    public static void HcGateBlocksNothing(TestContext t)
    {
        t.True(!PwRemake.Sim.Maps.MapSource.DecorBlocks("DCCO", "hc_gate", "CHCGate"), "the Halicarnassus gate never blocks");
        t.True(PwRemake.Sim.Maps.MapSource.DecorBlocks("DCCO", "hc_gate", "CDecoObj"), "another collision decoration does");
        t.True(PwRemake.Sim.Maps.MapSource.DecorBlocks("DECO", "rock_01", null), "a rock does");
    }

    /// <summary>CPirateShip: tail, sail, rowing deck and cannon deck on psh1..3; their destruction level follows the
    /// ship's hit points (>50 % 0, >25 % 1, else 2); a shot is a volley of 14 arrows spread along and across the target</summary>
    public static void PirateShipParts(TestContext t)
    {
        var (w, p, e) = World(t, "Ninigi", "Hu");
        var specs = w.Data.Composites("ninigi_pirate_boss_ship").Where(s => s.Kind == W_.PIRATE_PART).ToList();
        t.Equal(4, specs.Count, "four parts");
        t.Equal("psh1:pirate_boss_tail,psh2:pirate_boss_sail,psh3:pirate_boss_row,psh2:pirate_boss_cannons", string.Join(",", specs.Select(s => s.Link + ":" + s.Gfx)), "on the hull's links");
        t.Equal(0, W_.PirateDamage(4400, 4400), "whole"); t.Equal(1, W_.PirateDamage(2000, 4400), "level 1"); t.Equal(2, W_.PirateDamage(1000, 4400), "level 2");
        var ship = w.SpawnUnit("ninigi_pirate_boss_ship", p, 0, 0, 5);
        if (ship == null) t.Skip("no pirate boss ship");
        var parts = ship!.Comp!.Parts.Where(pp => pp.Spec!.Kind == W_.PIRATE_PART).ToList();
        if (parts.Count == 0) t.Log("    (the parts need the hull's links in the model data - not in this container)");
        else
        {
            t.Equal(4, parts.Count, "the ship carries them");
            ship.Hp = ship.MaxHp * 0.3;
            Sys2.Run(w, 0.1);
            t.True(parts.All(pp => pp.OwnState && pp.VisDmg == 1), "at 30 %: destruction level 1");
        }
        var w0 = ship.Weapons.Long;
        t.True(w0?.Projectile != null, "its weapon shoots arrows");
        var target = w.SpawnUnit("hu_warrior", e, 60, 0)!;
        int before = w.Projectiles.Count;
        w.FireShot(ship, w0, target);
        t.Equal(before + 14, w.Projectiles.Count, "14 arrows a shot");
        var aims = w.Projectiles.Skip(before).Select(pr => pr.Aim).ToList();
        t.True(aims.All(a => Math.Abs(a.X - target.Pos.X) <= 13 && Math.Abs(a.Z - target.Pos.Z) <= 13), "around the target");
        t.True(aims.Select(a => Math.Round(a.X, 2) + "," + Math.Round(a.Z, 2)).Distinct().Count() > 7, "spread out");
    }

    // ------------------------------------------------------------------------------------------ B: the launch pads
    /// <summary>the colossus' pad: the colossus stands on the site without arms, head and weapon; the arms at 51 %, the
    /// head at 76 %; the pad counts in the unit limit; the colossus takes its card</summary>
    public static void ColossusPadBuildUp(TestContext t)
    {
        var (w, p, _) = World(t, "Hu");
        Rich(p); FinalStage(w, p);
        var pad = Place(t, w, "hu_launchpad_colossi", p, 0, 0, built: false);
        t.True(pad.CountsInPop, "the site counts in the unit limit");
        bool On(int bit) => pad.Parts.First(pp => pp.OwnState).VisFlags!.First(f => f.Bit == bit).On;
        void At(double pr) { pad.Progress = pr; w.Step(); }
        At(0.1);
        t.True(pad.Parts.Count(pp => pp.OwnState) == 1 || !w.Models.Has("hx_berserk_statue_04"), "the colossus deco on the site");
        if (!w.Models.Has("hx_berserk_statue_04")) t.Skip("no colossus model");
        t.True(!On(5) && !On(6) && !On(7), "no head, arms, weapon");
        At(0.51);
        t.True(On(6) && !On(5), "arms at 51 %");
        At(0.76);
        t.True(On(5) && On(6) && !On(7), "head at 76 %, never the weapon");
        int units = p.Units;
        w.FinishBuilding(pad);
        Sys2.Run(w, 0.2);
        t.True(w.Units.Any(u => u.Alive && u.Name == "hu_colossi") && !pad.Alive, "the colossus replaced its pad");
        t.Equal(units, p.Units, "the colossus takes the pad's card");
    }

    /// <summary>the pirate pad: the ship's wreck on the site, at 40 % the ship's parts at destruction level 2, 60 % 1,
    /// 80 % 0; the ship then where the sail stood</summary>
    public static void PiratePadBuildUp(TestContext t)
    {
        var (w, p, _) = World(t, "Ninigi");
        Rich(p); FinalStage(w, p);
        var pad = Place(t, w, "ninigi_launchpad_pirate_boss", p, 0, 0, built: false);
        if (!w.Models.Has("pirate_boss_dest")) t.Skip("no pirate models");
        t.True(pad.CountsInPop, "the site counts in the unit limit");
        List<AttachedPart> Decos() => pad.Parts.Where(pp => pp.OwnState).ToList();
        void At(double pr) { pad.Progress = pr; w.Step(); }
        At(0.2);
        t.Equal("pirate_boss_dest", string.Join(",", Decos().Select(d => d.Gfx)), "the wreck");
        At(0.4);
        t.Equal("pirate_boss_tail,pirate_boss_sail,pirate_boss_row,pirate_boss_cannons", string.Join(",", Decos().Select(d => d.Gfx)), "the parts at 40 %");
        t.True(Decos().All(d => d.VisDmg == 2), "at destruction level 2");
        t.Near(-11.36, Decos()[0].Spec!.Offset![1]!.GetValue<double>(), 1e-9, "the tail 11.36 m aft");
        At(0.6); t.True(Decos().All(d => d.VisDmg == 1), "level 1 at 60 %");
        At(0.8); t.True(Decos().All(d => d.VisDmg == 0), "whole at 80 %");
        w.FinishBuilding(pad);
        Sys2.Run(w, 0.2);
        t.True(w.Units.Any(u => u.Alive && u.Name == "ninigi_pirate_boss_ship") && !pad.Alive, "the ship replaced its pad");
    }
}
