// MIRAGE's flying units (src/Sim/Systems/Flyers.cs; the MIRAGE flying units design v4: Mirage.usl CFlyerCtrl and its
// callers, _TechTree.ttree fly_land / fly_takeoff - docs/SIMULATION.md "Flying units"): a move flies straight at 15 m
// over what blocks the ground, landing and taking off (the abilities), the pteranodon's ground model and its walk, a fight
// taking a landed flyer up, a new order within 4 m keeping the flight, the option FlyingEnabled (off: no flyers built, the
// zeppelins don't fly), a saved game with a landed pteranodon.
//
//   cd tests/PwRemake.Tests && PW_DATA=/opt/data/base PW_DATA_MIRAGE=/opt/data/mirage dotnet run FlyingUnits
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class FlyingUnitsTests
{
    static (Rules R, ModelManifest M)? s_data;
    static readonly string[] MODEL_DIRS =
    {
        "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a@MIRAGE/assets/models",
        "/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a/assets/models",
    };

    /// <summary>MIRAGE's rules and models (the .glb files staged for the clips: pteranodon, pteranodon_boden, pteranodod,
    /// the cargolifter, the dirigible, the archaeopteryx)</summary>
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

    /// <summary>a flat 256 m world; the player (tribe) with FlyingEnabled (or not), an enemy; a wall of blocked ground
    /// across x = 20 .. 24 (z -40 .. 40) that walkers must go round</summary>
    static (W_ W, Player P, Player E) World(TestContext t, string tribe, bool flying = true, string enemy = "Aje")
    {
        var (R, M) = Data(t);
        var map = new FlatMap(256);
        map.Blocked.Add((20, -40, 24, 40));
        var w = new W_(R, M, map, new WorldOptions { Seed = 11 });
        var o = new GameplayOptions { FlyingEnabled = flying };
        var p = new Player(1, tribe, new PlayerOpts { Rules = R, Team = 1, Gameplay = o });
        var e = new Player(2, enemy, new PlayerOpts { Rules = R, Team = 2, Gameplay = o });
        w.Players.Add(p); w.Players.Add(e);
        return (w, p, e);
    }

    static Unit Spawn(TestContext t, W_ w, string name, Player? p, double x, double z)
    {
        var u = w.SpawnUnit(name, p, x, z);
        if (u == null) t.Skip("no model for " + name);
        return u!;
    }

    static void Run(W_ w, double s, Func<bool>? until = null)
    {
        int n = (int)Math.Round(s / W_.STEP);
        for (int i = 0; i < n; i++) { w.Step(); if (until != null && until()) return; }
    }

    static double Dist(Unit u, double x, double z) => Math.Sqrt((u.Pos.X - x) * (u.Pos.X - x) + (u.Pos.Z - z) * (u.Pos.Z - z));

    static string? Use(W_ w, Unit u, string id, (double X, double Z)? at = null)
    {
        var a = w.MovesOf(u).FirstOrDefault(x => x.Id == id);
        if (a == null) return "none";
        return w.UseMove(u, a, null, at);
    }

    /// <summary>a move order: straight to the point at 15 m over the blocked ground (no way finder), the height climbing
    /// over the route; the flyers of MIRAGE's classes fly, the others don't</summary>
    public static void FliesToPointAt15m(TestContext t)
    {
        var (w, p, _) = World(t, "Ninigi");
        var c = Spawn(t, w, "ninigi_cargolifter", p, 0, 0);
        t.True(c.Flyer != null && c.Flying, "the cargolifter is a flyer");
        t.Near(0, c.LiftH, 0.01, "a new flyer stands on the ground");
        var walker = Spawn(t, w, "ninigi_rider_a", p, 0, 10);
        t.True(walker.Flyer == null, "a rider is no flyer");
        w.Order(c, new Order("move") { X = 50, Z = 0 });
        t.Equal(1, c.Path.Count, "one straight leg (no way round the blocked strip)");
        double maxY = 0; bool climbing = true; double last = 0;
        Run(w, 25, () =>
        {
            maxY = Math.Max(maxY, c.LiftH);
            if (c.Pos.X < 40 && c.LiftH + 1e-9 < last) climbing = false;
            last = c.LiftH;
            return c.Task.Type == "idle" && !c.Flyer!.VMoving;
        });
        t.Near(50, c.Pos.X, 1.0, "arrived at x");
        t.Near(0, c.Pos.Z, 1.0, "arrived at z (the straight line through the blocked strip)");
        t.Near(W_.FLIGHT_HEIGHT, c.LiftH, 0.1, "15 m above the ground");
        t.Near(W_.FLIGHT_HEIGHT, c.Pos.Y, 0.2, "drawn at its height (Pos.Y)");
        t.True(climbing, "the height only climbs on the way");
        // stays in the air when idle (no idle landing)
        Run(w, 5);
        t.Near(W_.FLIGHT_HEIGHT, c.LiftH, 0.1, "idle: still in the air");
    }

    /// <summary>over water the flight is 15 m above the water (CFlyerCtrl.GetFloor: the ground or the sea level); no landing
    /// spot in the water</summary>
    public static void FliesOverWater(TestContext t)
    {
        var (R, M) = Data(t);
        var map = new FlatMap(256) { Water = 3, HeightFn = (x, z) => x > 20 ? -6 : 4 };
        var w = new W_(R, M, map, new WorldOptions { Seed = 5 });
        var p = new Player(1, "Ninigi", new PlayerOpts { Rules = R, Team = 1, Gameplay = new GameplayOptions { FlyingEnabled = true } });
        w.Players.Add(p);
        var c = Spawn(t, w, "ninigi_cargolifter", p, 0, 0);
        w.Order(c, new Order("move") { X = 60, Z = 0 });
        Run(w, 30, () => c.Task.Type == "idle" && !c.Flyer!.VMoving);
        t.Near(3 + W_.FLIGHT_HEIGHT, c.Pos.Y, 0.2, "15 m above the water");
        t.True(w.FindFreeSpot(c, 60, 0, true, null) is not { } s || s.X <= 20.5, "a landing spot only on land");
    }

    /// <summary>"Land" from far away (two legs: to 12 m before the spot at 15 m, then down), then "Take off" (rises on the
    /// spot, 4 m to the side) - the abilities offered to a flyer only</summary>
    public static void LandAndTakeOff(TestContext t)
    {
        var (w, p, _) = World(t, "Ninigi");
        var c = Spawn(t, w, "ninigi_dirigible", p, -60, 0);
        var ids = w.MovesOf(c).Select(a => a.Id).ToList();
        t.True(ids.Contains("fly_land") && ids.Contains("fly_takeoff"), $"the dirigible offers Land and Take off ({string.Join(",", ids)})");
        var la = w.MovesOf(c).First(a => a.Id == "fly_land");
        t.Equal("jetfly_off", la.Loc?.Icon, "Land's icon");
        t.Equal("jetfly_on", w.MovesOf(c).First(a => a.Id == "fly_takeoff").Loc?.Icon, "Take off's icon");
        w.Order(c, new Order("move") { X = -60, Z = 30 });
        Run(w, 30, () => c.Task.Type == "idle" && !c.Flyer!.VMoving);
        t.Near(W_.FLIGHT_HEIGHT, c.LiftH, 0.1, "up");
        t.Equal(null, Use(w, c, "fly_land", (0, 30)), "Land at a point 60 m away");
        w.Step();
        t.True(c.Flyer!.LandPending, "first leg: to 12 m before the spot");
        t.Near(-12, c.Goal!.Value.X, 0.5, "the first leg's point");
        Run(w, 60, () => w.IsLanded(c) && c.Task.Type == "idle" && !c.Flyer!.VMoving && !c.Flyer.Landing && !c.Flyer.LandPending);
        t.True(w.IsLanded(c), "landed");
        t.Near(0, c.LiftH, 0.05, "on the ground");
        t.True(Dist(c, 0, 30) < 1, $"at the spot ({c.Pos.X:0.0},{c.Pos.Z:0.0})");
        Run(w, 5);
        t.True(w.IsLanded(c), "stays on the ground when idle");
        // take off: on the spot, up to 15 m
        t.Equal(null, Use(w, c, "fly_takeoff"), "Take off");
        Run(w, 20, () => c.LiftH > W_.FLIGHT_HEIGHT - 0.05 && c.Task.Type == "idle");
        t.Near(W_.FLIGHT_HEIGHT, c.LiftH, 0.1, "back at 15 m");
        t.True(Dist(c, 0, 30) < 5, "rose on the spot (a 4 m leg)");
        // land near by: a short approach of 4 m (not a vertical drop)
        double x0 = c.Pos.X, z0 = c.Pos.Z;
        t.Equal(null, Use(w, c, "fly_land", (x0 + 1, z0)), "Land next to it");
        w.Step();
        t.True(Math.Abs(Dist(c, c.Goal!.Value.X, c.Goal.Value.Z) - World_.APPROACH) < 1.5, "a short approach leg of about 4 m");
        Run(w, 30, () => w.IsLanded(c) && c.Task.Type == "idle" && !c.Flyer!.VMoving && !c.Flyer.Landing);
        t.True(w.IsLanded(c), "landed again");
        // a move order takes it up by itself
        w.Order(c, new Order("move") { X = 40, Z = 30 });
        Run(w, 4);
        t.True(c.LiftH > 1, "a move order climbs");
    }
    static class World_ { public const double APPROACH = W_.FLY_APPROACH_MIN; }

    /// <summary>v4: a pteranodon lands ("land", then its ground model "pteranodon_boden" with "standanim"), walks with the
    /// ground model's walk_1 by the way finder (round the blocked strip, no take-off), and takes off with "take_off" back to
    /// the flying model</summary>
    public static void PteranodonWalksAfterLanding(TestContext t)
    {
        var (w, p, _) = World(t, "Aje", enemy: "Hu");
        var u = Spawn(t, w, "aje_pteranodon", p, 0, 0);
        t.Equal("ptera", u.Flyer!.Kind, "a pteranodon");
        t.True(!u.Gfx!.Contains("boden"), $"the flying model ({u.Gfx})");
        w.Order(u, new Order("move") { X = 0, Z = 40 });
        Run(w, 25, () => u.Task.Type == "idle" && !u.Flyer!.VMoving);
        Run(w, 3);
        t.Near(W_.FLIGHT_HEIGHT, u.LiftH, 0.1, "up");
        t.Equal("idle", u.Anim!.CurName, "hovering: its \"idle\" clip");
        t.Equal(null, Use(w, u, "fly_land", (0, 46)), "Land");
        bool sawLand = false;
        Run(w, 30, () => { if (u.Anim!.CurName == "land") sawLand = true; return u.Flyer!.Ground; });
        t.True(sawLand || !u.Anim!.Has("land"), "the \"land\" clip played");
        t.True(u.Flyer!.Ground && !u.Flying, "on the ground: ground mode, no flyer in the air");
        t.Equal("pteranodon_boden", u.Gfx?.ToLowerInvariant(), "the ground model");
        t.Near(0, u.LiftH, 0.01, "on the ground");
        Run(w, 1);
        t.Equal("standanim", u.Anim!.CurName, "stands with \"standanim\"");
        // a move across the blocked strip: walks round it (way finder), never flies
        double x0 = u.Pos.X;
        w.Order(u, new Order("move") { X = 40, Z = 0 });
        t.True(u.Path.Count > 1, "a way round the blocked strip");
        bool walkClip = false, lifted = false, inside = false;
        Run(w, 60, () =>
        {
            if (u.Anim!.CurName == "walk_1") walkClip = true;
            if (u.LiftH > 0.01) lifted = true;
            if (u.Pos.X > 20.2 && u.Pos.X < 23.8 && Math.Abs(u.Pos.Z) < 39.8) inside = true;
            return u.Task.Type == "idle";
        });
        t.True(walkClip, "walks with walk_1");
        t.True(!lifted && u.Flyer.Ground, "stays on the ground");
        t.True(!inside, "never through the blocked strip");
        t.True(Dist(u, 40, 0) < 2.5, $"walked there ({u.Pos.X:0.0},{u.Pos.Z:0.0} from x {x0:0.0})");
        // take off: "take_off", the flying model, up to 15 m
        t.Equal(null, Use(w, u, "fly_takeoff"), "Take off");
        t.True(!u.Flyer.Ground && !u.Gfx!.Contains("boden"), "back to the flying model");
        t.Equal("take_off", u.Anim!.CurName, "the take_off clip");
        Run(w, 20, () => u.LiftH > W_.FLIGHT_HEIGHT - 0.05 && u.Task.Type == "idle");
        t.Near(W_.FLIGHT_HEIGHT, u.LiftH, 0.1, "flying again");
    }

    /// <summary>the Norse pteranodon's ground model (pteranodod_boden) is not in the remake's asset export: it lands in
    /// ground mode with its flying model (which has standanim and walk_1 too)</summary>
    public static void NorsePteranodonGroundModel(TestContext t)
    {
        var (w, p, _) = World(t, "Hu");
        var u = Spawn(t, w, "hu_pteranodon", p, 0, 0);
        bool has = w.Models.Has("pteranodod_boden");
        t.Log($"pteranodod_boden in the manifest: {has}");
        w.Order(u, new Order("move") { X = -30, Z = 0 });
        Run(w, 20, () => u.Task.Type == "idle" && !u.Flyer!.VMoving);
        t.Equal(null, Use(w, u, "fly_land", (-30, 5)), "Land");
        Run(w, 30, () => u.Flyer!.Ground);
        t.True(u.Flyer!.Ground, "ground mode");
        t.Equal(has ? "pteranodod_boden" : "pteranodod", u.Gfx?.ToLowerInvariant(), "the model");
        Run(w, 1);
        t.Equal("standanim", u.Anim!.CurName, "standanim");
    }

    /// <summary>an attack takes a landed pteranodon up first ("take_off", then the climb), then it dives at the target
    /// and hurts it; the ground units' melee weapons can't reach it in the air</summary>
    public static void AttackTakesOff(TestContext t)
    {
        var (w, p, e) = World(t, "Aje", enemy: "Hu");
        var u = Spawn(t, w, "aje_pteranodon", p, -40, 0);
        w.Order(u, new Order("move") { X = -40, Z = 10 });
        Run(w, 20, () => u.Task.Type == "idle" && !u.Flyer!.VMoving);
        Use(w, u, "fly_land", (-40, 14));
        Run(w, 30, () => u.Flyer!.Ground);
        t.True(u.Flyer!.Ground, "landed (ground mode)");
        var v = Spawn(t, w, "hu_warrior", e, -40, 50);
        v.Stance = 3;                                           // (it doesn't come at the pteranodon)
        double hp0 = v.Hp;
        w.Order(u, new Order("attack") { Target = v });
        w.Step();
        t.True(!u.Flyer.Ground, "the flying model again");
        t.Equal("take_off", u.Anim!.CurName, "the take_off clip first");
        double maxH = 0, minH = 99;
        Run(w, 40, () =>
        {
            maxH = Math.Max(maxH, u.LiftH);
            if (maxH > W_.FLIGHT_HEIGHT - 1) minH = Math.Min(minH, u.LiftH);
            return v.Hp < hp0 - 1;
        });
        Run(w, 3, () => { minH = Math.Min(minH, u.LiftH); return false; });
        t.True(maxH > W_.FLIGHT_HEIGHT - 1, $"climbed to 15 m first ({maxH:0.0})");
        t.True(v.Hp < hp0, $"the target was hurt ({v.Hp:0}/{hp0:0})");
        double dive = u.Weapons.Long?.Projectile != null ? 8 : 3;
        t.True(minH < dive + 2, $"dived at it ({minH:0.0} m; the dive's height {dive} m - {(u.Weapons.Long?.Projectile != null ? "a projectile" : "hand to hand")})");
        // a hand-to-hand fighter can't hit it in the air (Combat.ReachesFlyer: a projectile weapon)
        t.True(!w.CanTarget(v, u) || w.ReachesFlyer(v), "melee can't target a flyer");
    }

    /// <summary>v4: a new move to within 4 m of the running flight's order point keeps the flight (same way, same target,
    /// no restart); farther: a new flight</summary>
    public static void ReissueWithin4mKeepsFlight(TestContext t)
    {
        var (w, p, _) = World(t, "Ninigi");
        var c = Spawn(t, w, "ninigi_cargolifter", p, -80, 0);
        w.Order(c, new Order("move") { X = 60, Z = 0 });
        Run(w, 2);
        var path = c.Path; var goal = c.Goal; double h0 = c.Flyer!.H0, t0 = c.Flyer.OrderTime;
        w.Order(c, new Order("move") { X = 62.5, Z = 1 });
        t.True(ReferenceEquals(path, c.Path), "the same flight (way kept)");
        t.Equal(goal, c.Goal, "the same target");
        t.Equal(t0, c.Flyer.OrderTime, "the order not restarted");
        t.Near(h0, c.Flyer.H0, 1e-9, "the climb not restarted");
        w.Order(c, new Order("move") { X = 66, Z = 0 });
        t.True(!ReferenceEquals(path, c.Path), "6 m away: a new flight");
        t.Near(66, c.Goal!.Value.X, 0.01, "to the new point");
    }

    /// <summary>a group ordered to one point: each flyer its own target, a unit size apart; idle they move apart once and
    /// then stay (no turning on the spot, no landing)</summary>
    public static void GroupSpreadsOnce(TestContext t)
    {
        var (w, p, _) = World(t, "Ninigi");
        var cs = Enumerable.Range(0, 4).Select(i => Spawn(t, w, "ninigi_cargolifter", p, -50 + i * 2, 0)).ToList();
        foreach (var c in cs) w.Order(c, new Order("move") { X = 30, Z = 30 });
        var goals = cs.Select(c => c.Goal!.Value).ToList();
        for (int i = 0; i < goals.Count; i++)
            for (int j = i + 1; j < goals.Count; j++)
                t.True(Math.Sqrt(Math.Pow(goals[i].X - goals[j].X, 2) + Math.Pow(goals[i].Z - goals[j].Z, 2)) >= 6.9, $"targets {i} and {j} a unit size apart");
        Run(w, 40, () => cs.All(c => c.Task.Type == "idle" && !c.Flyer!.VMoving));
        Run(w, 3);
        var at = cs.Select(c => (c.Pos.X, c.Pos.Z, c.Heading)).ToList();
        Run(w, 10);
        for (int i = 0; i < cs.Count; i++)
        {
            t.True(Math.Abs(cs[i].Pos.X - at[i].X) < 0.01 && Math.Abs(cs[i].Pos.Z - at[i].Z) < 0.01, $"flyer {i} stays where it is");
            t.Near(at[i].Heading, cs[i].Heading, 1e-6, $"flyer {i} doesn't turn");
            t.Near(W_.FLIGHT_HEIGHT, cs[i].LiftH, 0.1, $"flyer {i} stays in the air");
        }
    }

    /// <summary>the option FlyingEnabled off (MIRAGE's default): the flyers' build actions can't be used (the computer
    /// players build none), the zeppelins are no flyers (no Land / Take off); on: they can, the zeppelins fly</summary>
    public static void OptionOffNoFlyers(TestContext t)
    {
        var builds = new Dictionary<string, string[]>
        {
            ["Hu"] = new[] { "hu_pteranodon" }, ["Aje"] = new[] { "aje_pteranodon" },
            ["Ninigi"] = new[] { "ninigi_cargolifter", "ninigi_dirigible" }, ["SEAS"] = new[] { "seas_helicopter_flying" },
        };
        foreach (var (tribe, ids) in builds)
            foreach (bool on in new[] { false, true })
            {
                var (w, p, _) = World(t, tribe, on);
                p.Debug = true;                        // (the buildings and epochs they need are not asked for)
                foreach (var id in ids)
                {
                    var a = w.Data.Actions(p).FirstOrDefault(x => x.Id == id && x.Kind == "Build");
                    t.True(a != null, $"{tribe}: build action {id}");
                    // (the computer players' production asks Check too: an action disabled by the filter is never queued)
                    var why = w.Data.Check(p, a!);
                    if (on) t.True(!a!.Disabled && why != "disabled", $"{tribe} flying on: {id} not disabled ({why})");
                    else t.True(a!.Disabled && why != null, $"{tribe} flying off: {id} disabled ({why})");
                }
                var z = Spawn(t, w, tribe.ToLowerInvariant() + "_zeppelin", p, 0, 0);
                t.Equal(on, z.Flyer != null, $"{tribe} zeppelin flies with the option {(on ? "on" : "off")}");
                t.Equal(on, w.MovesOf(z).Any(x => x.Id == "fly_land"), $"{tribe} zeppelin: Land offered {(on ? "with" : "without")} the option");
            }
        // the SEAS helicopter: a flyer only in its flying mode (the flying build's local filter)
        {
            var (w, p, _) = World(t, "SEAS", true);
            var h = Spawn(t, w, "seas_helicopter", p, 0, 0);
            t.True(h.Flyer == null, "the helicopter in its assault mode doesn't fly");
            w.LocalFilter(h, "SEAS/Upgrades/seas_helicopter/seas_helicopter_flying", true);
            h.RefreshRules();
            t.True(h.Flyer != null && h.Flyer.Kind == "heli", "in its flying mode it flies");
            w.LocalFilter(h, "SEAS/Upgrades/seas_helicopter/seas_helicopter_flying", false);
            h.RefreshRules();
            t.True(h.Flyer == null && !h.Flying, "back in its assault mode: on the ground");
        }
        // wild flyers fly without the option and have no abilities
        {
            var (w, _, _) = World(t, "Hu", false);
            var a = Spawn(t, w, "Archaeopteryx", null, 0, 0);
            t.True(a.Flyer is { Kind: "wild" }, "the wild Archaeopteryx flies");
            t.True(!w.MovesOf(a).Any(x => Flyers.IsAbility(x.Id)), "no abilities");
        }
    }

    /// <summary>a game with a landed pteranodon on its ground model, saved and loaded (the replay: docs/SAVES.md): the same
    /// world, still landed, and it walks on after loading as in the running game</summary>
    public static void SaveLoadLanded(TestContext t)
    {
        GameRecorder? rec = null;
        (W_ W, Unit U, Player P) Make()
        {
            var (w, p, _) = World(t, "Aje", enemy: "Hu");
            var u = Spawn(t, w, "aje_pteranodon", p, 0, 0);
            return (w, u, p);
        }
        var a = Make();
        rec = new GameRecorder(a.W, a.W.StateHash, null);
        a.W.Enqueue(new OrderCommand { Player = a.P.Id, Units = new[] { a.U.Id }, Type = "move", X = 10, Z = 30, Step = a.W.StepNo + 1 });
        Run(a.W, 20);
        a.W.Enqueue(new UseMoveCommand { Player = a.P.Id, Casters = new[] { a.U.Id }, ActionId = "fly_land", HasGround = true, X = 12, Z = 36, Step = a.W.StepNo + 1 });
        Run(a.W, 25);
        t.True(a.U.Flyer!.Ground, "landed on its ground model before saving");
        var save = rec.Capture(new GameStart { Configuration = "mirage", Seed = 11 }, SimFlags.Game);
        var file = Path.Combine(Path.GetTempPath(), "pw_fly_test.pwsave");
        save.Write(file);
        var loaded = SaveGame.Read(file);
        var b = Make();
        var rep = new SaveReplay(loaded, b.W, b.W.StateHash, null);
        rep.Run(); rep.End();
        t.True(!rep.Diverged, $"the replay follows the save ({rep.Problem})");
        t.Equal(a.W.StateHash(), b.W.StateHash(), "the loaded world is the saved one");
        t.True(b.U.Flyer!.Ground && !b.U.Flying && b.U.Gfx == a.U.Gfx, "still landed on its ground model");
        foreach (var x in new[] { a, b }) x.W.Enqueue(new OrderCommand { Player = x.P.Id, Units = new[] { x.U.Id }, Type = "move", X = -20, Z = 36, Step = x.W.StepNo + 1 });
        for (int i = 0; i < 400; i++) { a.W.Step(); b.W.Step(); }
        t.Equal(a.W.StateHash(), b.W.StateHash(), "the same world after walking on");
        t.True(b.U.Flyer.Ground && b.U.LiftH == 0 && Dist(b.U, -20, 36) < 2.5, "it walked on the ground after loading");
    }
}
