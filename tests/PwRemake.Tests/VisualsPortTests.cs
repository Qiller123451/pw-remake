// The visuals port of 2026-11-18 (agent "visuals"): MIRAGE's pirate boss ship - the broadside turn before it fires, the
// level-up pause, the parts' own wrecks (src/Sim/Systems/PirateShip.cs) - and MIRAGE's custom construction
// (settings/custom_buildups.txt: the building rising out of the ground on paving tiles, the cranes at the table's places,
// the fake corpse - src/Sim/Systems/CustomBuildUps.cs). The sequel's draped searchlight is the view's alone
// (game/src/View/Sequel/SequelFx.cs).
//
//   cd tests/PwRemake.Tests && PW_DATA=/opt/data/base PW_DATA_MIRAGE=/opt/data/mirage dotnet run VisualsPort
using System;
using System.IO;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class VisualsPortTests
{
    static (Rules R, ModelManifest M)? s_data;
    static readonly string[] MODEL_DIRS =
    {
        "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a@MIRAGE/assets/models",
        "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a/assets/models",
    };

    /// <summary>MIRAGE's rules and models (the pirate ship's .glb files staged: pirate_boss, its parts and their wrecks)</summary>
    static (Rules R, ModelManifest M) Data(TestContext t)
    {
        if (s_data != null) return s_data.Value;
        var dir = Sys2.Mirage;
        if (!File.Exists(Path.Combine(dir, "techtree.json"))) t.Skip("MIRAGE data not found");
        var R = Rules.Load(dir);
        var man = Json.ParseFile(Path.Combine(dir, "assets", "manifest.json"))!;
        var M = new ModelManifest(man, null, n => MODEL_DIRS.Select(d => Path.Combine(d, n + ".glb")).FirstOrDefault(File.Exists));
        s_data = (R, M);
        return s_data.Value;
    }

    static (W_ W, Player P, Player E) World(TestContext t, string tribe = "Ninigi", string enemy = "Hu")
    {
        var (R, M) = Data(t);
        var w = new W_(R, M, new FlatMap(400), new WorldOptions { Seed = 5 });
        var p = new Player(1, tribe, new PlayerOpts { Rules = R, Team = 1 });
        var e = new Player(2, enemy, new PlayerOpts { Rules = R, Team = 2 });
        w.Players.Add(p); w.Players.Add(e);
        return (w, p, e);
    }

    static Unit Ship(TestContext t, W_ w, Player p, double heading = 0)
    {
        var u = w.SpawnUnit("ninigi_pirate_boss_ship", p, 0, 0, 5, heading);
        if (u == null) t.Skip("no pirate boss ship");
        return u!;
    }

    static void Run(W_ w, double s, Func<bool>? until = null)
    {
        int n = (int)Math.Round(s / W_.STEP);
        for (int i = 0; i < n; i++) { w.Step(); if (until != null && until()) return; }
    }

    // ------------------------------------------------------------------------------------------ the pirate boss ship
    /// <summary>CPirateShip.AttackEnemy: a target ahead (90 degrees off the broadside) - the ship turns at 0.20 rad/s
    /// until its right side faces it, without firing meanwhile; then the volley</summary>
    public static void PirateBroadside(TestContext t)
    {
        var (w, p, e) = World(t);
        var ship = Ship(t, w, p);
        // (heading 0: the ship looks along -z; the target 60 m ahead)
        var target = w.SpawnUnit("hu_warrior", e, 0, -60)!;
        t.Near(Math.PI / 2, Math.Abs(Geo.Wrap(W_.BroadsideHeading(ship, target.Pos.X, target.Pos.Z) - ship.Heading)), 1e-6, "ahead: a quarter turn off the broadside");
        // (the target's right side: heading +90 degrees shows +x ... the target ahead wants the ship turned so)
        t.Near(Geo.Wrap(Math.PI / 2), W_.BroadsideHeading(ship, 0, -60), 1e-6, "the target on the right: heading + 90 degrees");
        w.Order(ship, new Order("attack") { Target = target });
        int arrows = 0; double firstShot = -1;
        Run(w, 12, () =>
        {
            arrows = w.Projectiles.Count(pr => pr.Owner == ship);
            if (arrows > 0 && firstShot < 0) firstShot = w.Time;
            return arrows > 0;
        });
        t.True(arrows > 0, "it fires in the end");
        double off = Math.Abs(Geo.Wrap(W_.BroadsideHeading(ship, target.Pos.X, target.Pos.Z) - ship.Heading));
        t.True(off < 0.02, $"its broadside faces the target when it fires (off {off:0.000} rad)");
        // a quarter turn at 0.2 rad/s: 7.85 s (the attack loop's own steps on top)
        t.True(firstShot >= Math.PI / 2 / W_.PIRATE_TURN - 0.1, $"not before the turn is over ({firstShot:0.00} s)");
        // a target within 45 degrees of the broadside: no turn, it fires (its first shot after the weapon's 6 s)
        var (w2, p2, e2) = World(t);
        var ship2 = Ship(t, w2, p2);
        var t2 = w2.SpawnUnit("hu_warrior", e2, 60, -20)!;               // (to the right, 18 degrees ahead of the beam)
        double h0 = ship2.Heading;
        w2.Order(ship2, new Order("attack") { Target = t2 });
        Run(w2, 8, () => w2.Projectiles.Any(pr => pr.Owner == ship2));
        t.Log($"(fired at {w2.Time:0.00} s; the turned one at {firstShot:0.00} s)");
        t.True(w2.Projectiles.Any(pr => pr.Owner == ship2), "within 45 degrees: it fires");
        t.Near(h0, ship2.Heading, 1e-9, "without turning");
    }

    /// <summary>CPirateShip.SetLevel: it stops and obeys no order for 9 s (TIMER_LEVELING), then again</summary>
    public static void PirateLevelUpPause(TestContext t)
    {
        var (w, p, e) = World(t);
        var ship = w.SpawnUnit("ninigi_pirate_boss_ship", p, 0, 0, 1);
        if (ship == null) t.Skip("no pirate boss ship");
        if (!w.CanHaveLevel(ship!, 2)) t.Skip("the ship has no level 2");
        var target = w.SpawnUnit("hu_warrior", e, 60, 0)!;
        w.Order(ship!, new Order("attack") { Target = target });
        Run(w, 0.5);
        t.Equal("attack", ship!.Task.Type, "attacking");
        w.ApplyLevel(ship, 2);
        t.Equal("idle", ship.Task.Type, "the level-up stops it (StopEverything)");
        w.Order(ship, new Order("move") { X = 100, Z = 0 });
        t.True(ship.Task.Type != "move", "no orders while it levels up");
        Run(w, W_.PIRATE_LEVEL_PAUSE - 1);
        w.Order(ship, new Order("move") { X = 100, Z = 0 });
        t.True(ship.Task.Type != "move", "still none after 8 s");
        Run(w, 1.1);
        w.Order(ship, new Order("move") { X = 100, Z = 0 });
        t.Equal("move", ship.Task.Type, "after 9 s it obeys again");
        // another unit's level-up holds nothing back
        var warrior = w.SpawnUnit("ninigi_warrior", p, 20, 20, 1)!;
        if (w.CanHaveLevel(warrior, 2))
        {
            w.ApplyLevel(warrior, 2);
            w.Order(warrior, new Order("move") { X = 30, Z = 20 });
            t.Equal("move", warrior.Task.Type, "a warrior obeys at once");
        }
    }

    /// <summary>CPirateShip.Die -> CreateShipCorpse / CPirateShipCorpse: the parts' wrecks at the parts' places, the
    /// tail's clip at once, the row's at 1.33 s, the sail's at 2.15 s, the cannon deck reduced at 4.2 s; the hull's
    /// wreck when the tail's clip is 5 s from its end (12 s - 5), gone 17 s later</summary>
    public static void PirateShipWrecks(TestContext t)
    {
        var (w, p, e) = World(t);
        var ship = Ship(t, w, p, 0.7);
        w.Kill(ship, null);
        var wr = ship.Wreck;
        t.True(wr != null, "a wreck");
        t.Equal("pirate_boss_dest", wr!.Gfx, "the hull's wreck");
        bool tailKnown = w.Models.File("pirate_boss_tail_dest") != null;
        if (tailKnown) t.Near(12, w.Models.Clips("pirate_boss_tail_dest").Duration["destroy"], 1e-6, "the tail wreck's clip (MIRAGE: 12 s)");
        t.Near(7, wr.From, 1e-9, "the hull shows at 12 - 5 s");
        t.Near(7 + W_.PIRATE_HULL_LIFE, wr.Until, 1e-9, "and goes 17 s later");
        var parts = wr.Parts!;
        t.Equal("pirate_boss_tail_dest@psh1,pirate_boss_row_dest@psh3,pirate_boss_sail_dest@psh2,pirate_boss_cannons@psh2,pirate_boss_cannons_reduced@psh2",
            string.Join(",", parts.Select(x => x.Gfx + "@" + x.Link)), "the parts' wrecks (tail, row, sail, cannon deck, its reduced wreck)");
        t.Equal("0,1.33,2.15,NaN,NaN", string.Join(",", parts.Select(x => x.PlayAt.ToString(System.Globalization.CultureInfo.InvariantCulture))), "their clips' starts");
        t.Equal("2,2,2,0,0", string.Join(",", parts.Select(x => x.VisDmg)), "tail, row, sail at destruction level 2");
        if (w.Models.Link("pirate_boss", "psh1") is V3 l1)
        {
            var at = W_.LinkToWorld(ship.Pos, ship.Heading, l1);
            t.Near(at.X, parts[0].Pos.X, 1e-6, "the tail's wreck where the tail was (x)");
            t.Near(at.Z, parts[0].Pos.Z, 1e-6, "(z)");
            t.True(Math.Abs(parts[0].Pos.X - ship.Pos.X) + Math.Abs(parts[0].Pos.Z - ship.Pos.Z) > 5, "aft of the hull's centre");
        }
        bool Shown(int i) => parts[i].ShownAt(ship.DeadT);
        Run(w, 3);
        t.True(!wr.ShownAt(ship.DeadT) && Shown(0) && Shown(1) && Shown(2) && Shown(3) && !Shown(4), "at 3 s: the parts' wrecks, the whole cannon deck");
        Run(w, 2);
        t.True(!Shown(3) && Shown(4), "at 5 s: the reduced cannon deck");
        Run(w, 2.5);
        t.True(wr.ShownAt(ship.DeadT) && !parts.Any(x => x.ShownAt(ship.DeadT)), "at 7.5 s: the hull's wreck alone");
        t.True(!ship.Removed, "still there");
        Run(w, 17);
        t.True(ship.Removed, "gone after 7 + 17 s");
        // an ordinary ship's wreck: shown at once, gone after 10 s (combat.js shipWreck)
        var junk = w.SpawnUnit("ninigi_transport_boat", p, 60, 60);
        if (junk != null)
        {
            w.Kill(junk, null);
            if (junk.Wreck != null)
            {
                t.True(junk.Wreck.Parts == null && junk.Wreck.ShownAt(0) && junk.Wreck.Until == 10, "another ship: its wreck at once, for 10 s");
                Run(w, 10.2);
                t.True(junk.Removed, "gone after 10 s");
            }
        }
    }

    // ------------------------------------------------------------------------------------------ custom construction
    /// <summary>custom_buildups.txt CustomConstruction: paving tiles on the site, the model rising StartHeight m out of
    /// the ground with the construction (whole per cents), gone when finished; HeightOffset; MIRAGE only</summary>
    public static void RisingBuilding(TestContext t)
    {
        var (w, p, _) = World(t, "SEAS");
        var pub = w.PlaceBuilding("seas_pub", p, 0, 0, 0, false);
        if (pub == null) t.Skip("no seas_pub");
        t.True(pub!.Rising, "the pub's site rises (CustomConstruction 1)");
        t.Equal("seas_hq_paving_tiles_02", pub.RisingSiteGfx, "the SEAS' paving tiles");
        t.Near(-9, pub.RiseY, 1e-9, "9 m deep at the start");
        pub.Progress = 0.5; t.Near(-4.5, pub.RiseY, 1e-9, "half way at 50 %");
        pub.Progress = 0.996; t.Near(-0.09, pub.RiseY, 1e-9, "whole per cents (99 %)");
        var ft = CustomBuildUps.TABLE["seas_fortress"];
        t.Near(3.99 - 12, CustomBuildUps.RiseY(ft, 0), 1e-9, "the fortress: HeightOffset 3.99");
        t.Near(3.99, CustomBuildUps.RiseY(ft, 1), 1e-9, "(over its origin at the end)");
        t.Equal("seas_temple_paving_tiles_01", CustomBuildUps.SiteGfx("Hu"), "the other tribes' paving tiles");
        // finished: its own model
        pub.Progress = 1; pub.Built = true;
        t.True(!pub.Rising && pub.RisingSiteGfx == null, "finished: no more rising");
        // not with CustomConstruction 0 (the Norse mine has its own stages) nor in the base game
        var (w2, p2, _) = World(t, "Hu");
        var mine = w2.PlaceBuilding("hu_mine", p2, 0, 0, 0, false);
        if (mine != null) t.True(!mine.Rising && mine.CustomBuild != null, "hu_mine: an entry, CustomConstruction 0");
        var (R, _) = Sys2.Load(t, Sys2.Base);
        t.True(CustomBuildUps.Of(R, "seas_pub") == null && CustomBuildUps.Of(R, "hu_goods") == null, "the base game has none");
    }

    /// <summary>custom_buildups.txt Cranes: cranes at the table's places (metres in the building's frame) besides those
    /// at Cr_1..Cr_4; SEAS always crane_01; gone when finished</summary>
    public static void CustomCranes(TestContext t)
    {
        var (w, p, _) = World(t, "SEAS");
        var pub = w.PlaceBuilding("seas_pub", p, 0, 0, 0, false);
        if (pub == null) t.Skip("no seas_pub");
        var custom = pub!.Cranes.Where(c => c.Link == "NOPE").ToList();
        t.Equal(1, custom.Count, "one crane of the table");
        t.Equal("seas_crane_01", custom[0].Gfx, "the SEAS crane_01");
        t.Equal("[-2.5,5,0]", custom[0].Spec!.Offset!.ToJsonString(), "at the table's place");
        var (w2, p2, _) = World(t, "Hu");
        var pad = w2.PlaceBuilding("hu_launchpad_zeppelin", p2, 0, 0, 0, false);
        if (pad != null)
        {
            var cc = pad.Cranes.Where(c => c.Link == "NOPE").ToList();
            t.Equal(2, cc.Count, "the zeppelin pad: two");
            t.True(cc.All(c => c.Gfx is "hu_crane_01" or "hu_crane_02"), "the Norse cranes");
            pad.Progress = 1; pad.Built = true; pad.UpdateVisual();
            t.Equal(0, pad.Cranes.Count, "gone when finished");
        }
    }

    /// <summary>custom_buildups.txt FakeCorpse (CFakeCorpse): the building's own model at destruction level 2 with a fire
    /// for 3 s, then it sinks - also where a "_dest" model exists; not for classes with their own corpse (towers)</summary>
    public static void FakeCorpse(TestContext t)
    {
        var (w, p, _) = World(t, "SEAS");
        var pub = w.PlaceBuilding("seas_pub", p, 0, 0, 0, true);
        if (pub == null) t.Skip("no seas_pub");
        w.KillBuilding(pub!, null);
        t.Equal(pub!.Gfx, pub.Ruin, "the pub itself is its corpse");
        t.Equal(2, pub.VisDmg, "at destruction level 2");
        t.Near(3, pub.RuinSinkAt, 1e-9, "sinking after 3 s");
        if (w.Models.Has("fire_molotov")) t.True(pub.RuinParts?.Any(r => r.Gfx == "fire_molotov") == true, "with its fire (fire_corpses)");
        Run(w, 2.9);
        t.Near(0, pub.SinkY, 1e-9, "standing for 3 s");
        Run(w, 1.1);
        t.True(pub.SinkY > 0, "then sinking");
        // the sensor tower is a CTower: its own corpse (CTower.CreateBuildingCorpse), no fake one
        var (w2, p2, _) = World(t, "Aje");
        var st = w2.PlaceBuilding("aje_sensor_tower", p2, 0, 0, 0, true);
        if (st != null)
        {
            w2.KillBuilding(st, null);
            t.True(st.Ruin != st.Gfx || st.Ruin == null, "the sensor tower: no fake corpse");
        }
    }
}
