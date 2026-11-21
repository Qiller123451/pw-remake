// Tests of the remake's mission "Outbreak" (docs/OUTBREAK.md) and the general features it brought: the day-night cycle
// (World/DayNight.cs: map.lua day_night), infinite resources (map attributes infinite / fallen / scale), the infected
// stage in the simulation, the mission folder (game/missions/outbreak: the generator's map, custom.lua, coop.lua).
//
//   dotnet run --project tests/PwRemake.Tests -- Outbreak
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Coop;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Maps;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class OutbreakTests
{
    static string Folder()
    {
        Environment.SetEnvironmentVariable("PW_MISSIONS", Path.Combine(Repo.Root(), "game", "missions"));
        // (the test binary may live outside the repository: --artifacts-path)
        Environment.SetEnvironmentVariable("PW_LUA_STD", Path.Combine(Repo.Root(), "game", "lua", "std"));
        Environment.SetEnvironmentVariable("PW_COOP", Path.Combine(Repo.Root(), "game", "coop"));
        return Path.Combine(Repo.Root(), "game", "missions", "outbreak");
    }

    internal static MissionSession Session(TestContext t, int difficulty = 1, ulong seed = 7, CoopStart? coop = null, RecordingPresenter? p = null, string cfgId = "base")
    {
        Folder();
        var c = CampaignTests.LoadCfgPublic(cfgId);
        if (c == null) t.Skip($"configuration {cfgId} not found");
        var m = RemakeMissions.ByName("outbreak") ?? throw new Exception("game/missions/outbreak is not found");
        var start = RemakeMissions.Start(m, cfgId, "", difficulty, seed);
        if (coop != null) start.Coop = coop;
        return MissionSession.Create(start, c!.Config, new SessionOptions
        {
            Presenter = p, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data,
        });
    }

    // ------------------------------------------------------------------ the day-night clock
    public static void OutbreakDayNightClock(TestContext t)
    {
        var s = new DayNightSpec { Day = 300, Dusk = 30, Night = 180, Dawn = 30, FirstDay = 420 };
        t.Equal(DayPhase.Day, s.At(0).Phase, "day at the start");
        t.Equal(0, s.At(0).NightNo, "no night yet");
        t.Near(420, s.At(0).ToNight, 1e-9, "the first day is 420 s");
        t.Equal(DayPhase.Day, s.At(419).Phase, "still the first day");
        t.Equal(DayPhase.Dusk, s.At(421).Phase, "dusk after it");
        t.Equal(1, s.At(421).NightNo, "night 1 begins with its dusk");
        t.Equal(DayPhase.Night, s.At(455).Phase, "night");
        t.True(s.At(455).IsNight && s.At(455).Darkness == 1, "dark at night");
        t.Equal(DayPhase.Dawn, s.At(635).Phase, "dawn");
        t.Equal(DayPhase.Day, s.At(665).Phase, "the second day");
        t.Near(300 - (665 - 660), s.At(665).ToNight, 1e-9, "the next night in");
        t.Equal(2, s.At(665 + 300).NightNo, "night 2");
        double prev = 0; bool smooth = true;
        for (double x = 415; x < 460; x += 0.5) { double d = s.At(x).Darkness; if (Math.Abs(d - prev) > 0.06) smooth = false; prev = d; }
        t.True(smooth, "the light changes smoothly through dusk");
        // the clock of the day: 7:00 at the start, 18:00 at dusk, 20:00 at night, 5:00 at dawn
        t.Near(7 * 3600, s.At(0).Clock, 1, "7:00"); t.Near(18 * 3600, s.At(420.0001).Clock, 2, "18:00"); t.Near(5 * 3600, s.At(630.0001).Clock, 2, "5:00");
        // map.lua round trip
        var j = s.ToJson(); var back = DayNightSpec.FromJson(j);
        t.True(back != null && back.SameAs(s), "day_night as JSON and back");
        t.True(DayNightSpec.FromJson(new System.Text.Json.Nodes.JsonObject { ["enabled"] = false }) == null, "enabled = false: none");
    }

    public static void OutbreakDayNightWorld(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no base data");
        var map = new FlatMap(256) { DayNight = new DayNightSpec { Day = 20, Dusk = 4, Night = 10, Dawn = 4, NightSight = 0.5 } };
        var w = new PwRemake.Sim.World.World(c!.Rules, c.Models, map, new WorldOptions { Seed = 3 });
        var phases = new List<string>();
        int ev = 0;
        for (int i = 0; i < (int)(80 / PwRemake.Sim.World.World.STEP); i++)
        {
            w.Step();
            for (; ev < w.Events.Count; ev++) if (w.Events[ev].Type == "daynight") phases.Add(w.Events[ev].Data!["phase"] + "" + w.Events[ev].Data!["night"]);
            if (w.Events.Count < ev) ev = 0;
        }
        t.Equal("day0,dusk1,night1,dawn1,day1,dusk2,night2,dawn2,day2", string.Join(",", phases), "the phases as events");
        t.True(w.IsNight() == w.DayNightNow!.Value.IsNight, "IsNight follows the cycle");
        // sight at night
        var w2 = new PwRemake.Sim.World.World(c.Rules, c.Models, map, new WorldOptions { Seed = 3 });
        for (int i = 0; i < (int)(30 / PwRemake.Sim.World.World.STEP); i++) w2.Step();
        t.Near(0.5, w2.SightScale, 1e-9, "sight at night: night_sight");
    }

    // ------------------------------------------------------------------ infinite resources
    public static void OutbreakInfiniteResources(TestContext t)
    {
        var s = Session(t);
        var W = s.World;
        var inf = W.Resources.Where(n => n.Infinite).ToList();
        t.Equal(6, inf.Count, "three giant trees and three quarries");
        t.Equal(3, inf.Count(n => n.Type == "timber" && n.Fallen), "the giant trees lie felled from the start");
        t.Equal(3, inf.Count(n => n.Type == "stone"), "three quarries");
        // workers on them: an hour of work and the amounts are what they were
        var human = s.Human!;
        var amounts = inf.ToDictionary(n => n, n => n.Amount);
        var workers = W.Units.Where(u => u.Owner == human && u.Alive && u.Name.EndsWith("_worker")).ToList();
        t.True(workers.Count >= 3, "workers");
        var tree = inf.Where(n => n.Res == "wood").OrderBy(n => (n.Pos - workers[0].Pos).Length).First();
        var stone = inf.Where(n => n.Res == "stone").OrderBy(n => (n.Pos - workers[0].Pos).Length).First();
        double wood0 = human.Res["wood"], stone0 = human.Res["stone"];
        for (int i = 0; i < workers.Count; i++) W.Order(workers[i], new Order("gather") { Target = i % 2 == 0 ? tree : stone });
        // (the waves would come at night: the test keeps them away - it is about the resources)
        s.Engine!.Disable(s.Engine.Find("Outbreak: every second")!);
        s.Run(240);
        t.True(human.Res["wood"] > wood0 + 30 && human.Res["stone"] > stone0 + 30, $"wood {wood0} -> {human.Res["wood"]}, stone {stone0} -> {human.Res["stone"]}");
        foreach (var (n, a) in amounts) t.True(n.Alive && n.Amount == a, $"{n.Name} not used up ({n.Amount} of {a})");
    }

    // ------------------------------------------------------------------ the map
    static System.Text.Json.Nodes.JsonNode Layout() => PwRemake.Sim.Maps.LuaData.ParseJson(File.ReadAllText(Path.Combine(Folder(), "layout.lua")), "layout.lua")!;
    const double Cx = 384, Cy = 384;

    public static void OutbreakMapReachable(TestContext t)
    {
        var folder = Folder();
        var md = MapFiles.Read(folder);
        var layout = Layout();
        var camps = layout["camps"]!.AsArray().Select(c => c!).ToList();
        // (1) the world's nav grid (the ridges, the rockslide's rocks): the north and east passes
        // lead out, the south-west one is shut by the rockslide; with the three passes shut the base is closed - the
        // ring of ridges has no other way through (round 5: rock all round, no walls)
        var s = Session(t);
        var W = s.World; var C = s.Campaign; var nav = W.Nav;
        s.Run(2);
        t.True(PassOpen(s, 0) && PassOpen(s, 1), "the north and east passes lead out");
        t.True(!PassOpen(s, 2), "the south-west pass is shut by the rockslide");
        var ins = camps.Select(c => C.ToGame(c["entrance_in"]![0].Num(), c["entrance_in"]![1].Num())).ToList();
        bool Out(bool[] seen) =>
            camps.Any(c => NavAt(s, seen, c["entrance_out"]![0].Num(), c["entrance_out"]![1].Num())) || NavAt(s, seen, 120, 120) || NavAt(s, seen, 650, 650) || NavAt(s, seen, 384, 140) || NavAt(s, seen, 384, 640);
        t.True(!Out(NavFlood(s, ins, 34)), "the three passes shut: the base is closed (no way through the ridges)");
        // (the ring holds by its rock alone: no walls - nor any building of nobody - stand round the base)
        t.True(!W.Buildings.Any(b => b.Alive && (b.Def?.WallKind != null && b.Owner?.Id != 1 || b.Indestructible)), "no walls round the base");
        // (2) every infected building can be reached on foot from the base (the nav grid's ground regions, the rockslide
        // gone)
        var s2 = Session(t);
        W = s2.World; C = s2.Campaign; nav = W.Nav;
        s2.Run(2);
        foreach (var id in RockIds(s2)) s2.Host.Remove(id);
        nav.LabelRegions();
        var (cx, cz) = C.ToGame(Cx, Cy);
        int home = nav.Region![nav.Idx(cx, cz)];
        bool Reach(double x, double z) { int k = nav.Idx(x, z); return k >= 0 && nav.Region[k] == home; }
        int bad = 0;
        var inf = W.Buildings.Where(b => b.Owner?.Id == 1 && b.Alive).ToList();
        foreach (var b in inf)
        {
            bool any = false;
            for (double a = 0; a < 6.3 && !any; a += 0.4) any = Reach(b.Pos.X + Math.Cos(a) * (b.Radius + 4), b.Pos.Z + Math.Sin(a) * (b.Radius + 4));
            if (!any) bad++;
        }
        int planned = layout["villages"]!.AsArray().Sum(v => v!["buildings"]!.AsArray().Count);
        t.Equal(planned, inf.Count, "every infected building of the layout stands");
        t.Equal(0, bad, "every infected building is reachable");
        // the camps are buildable: houses fit round each camp's headquarters (no trees, rocks or slopes in the way), and
        // the camp's giant tree and quarry are within reach of its centre
        var human = s2.Human!;
        foreach (var camp in camps)
        {
            var (gx, gz) = C.ToGame(camp["x"].Num(), camp["y"].Num());
            int ok = 0, tried = 0;
            // (beyond the headquarters, its house, its field and its start army; the camp's tree and quarry take their side)
            for (double a = 0; a < 6.28; a += 0.5) for (double r = 30; r <= 42; r += 12)
            {
                tried++;
                if (W.PlacementOf("hu_stone_cottage", gx + Math.Cos(a) * r, gz + Math.Sin(a) * r, 0, human).Ok) ok++;
            }
            t.True(ok >= tried * 0.55, $"camp {camp["name"]}: {ok} of {tried} house places round the headquarters are free");
            foreach (var key in new[] { "tree", "stone" })
            {
                var (rx, rz) = C.ToGame(camp[key]![0].Num(), camp[key]![1].Num());
                t.True(Reach(rx + 9, rz) || Reach(rx - 9, rz) || Reach(rx, rz + 9) || Reach(rx, rz - 9), $"camp {camp["name"]}: its {key} can be walked to");
            }
        }
        // the base lies at the valley's level (round 4: not a plateau) - the camps within a few metres of the valley
        // beyond the passes
        foreach (var c in camps)
        {
            var (gx, gz) = C.ToGame(c["x"].Num(), c["y"].Num()); var (ox, oz) = C.ToGame(c["entrance_out"]![0].Num(), c["entrance_out"]![1].Num());
            t.True(Math.Abs(W.Height(gx, gz) - W.Height(ox, oz)) < 4, $"camp {c["name"]} at the valley's level ({W.Height(gx, gz):0.0} m, outside its pass {W.Height(ox, oz):0.0} m)");
        }
        // the map is a valley full of nature: forest squares, trees, plants (not a bare disc)
        t.True(md.Forest.Blocks.Count > 150 && md.Plants.Count > 700 && md.Objects.Count(o => o.Type == "TREE") > 900, $"forest blocks {md.Forest.Blocks.Count}, plants {md.Plants.Count}, trees {md.Objects.Count(o => o.Type == "TREE")}");
    }

    // ------------------------------------------------------------------ the indestructible mechanism (round 4's walls)
    // The map no longer uses it (round 5: the walls became rock), other maps may (docs/MAP_FORMAT.md "Indestructible
    // objects"): an object made indestructible takes no hits, is not attacked, cannot be blown up or sold.
    public static void OutbreakIndestructible(TestContext t)
    {
        var s = Session(t);
        var W = s.World;
        s.Run(2);
        var house = W.Buildings.First(b => b.Alive && b.Owner == s.Human && b.Name != "hu_fireplace" && !W.IsFarm(b));
        W.SetIndestructible(house, true);
        t.True(house.Indestructible && house.Untargetable, "indestructible: untargetable too");
        double hp = house.Hp;
        var guard = Infected(s).OrderBy(u => u.Id).First(u => u.DrawScale <= 1);
        W.TakeDmg(guard, house, 50);
        s.Run(1);
        t.True(house.Alive && house.Hp == hp, $"a hit does nothing ({hp} -> {house.Hp})");
        W.Order(guard, PwRemake.Sim.World.Order.Attack(house));
        s.Run(3);
        t.True((guard.Task.Type != "attack" || guard.Task.Target != house) && house.Hp == hp, $"an enemy cannot attack it ({guard.Task.Type}, {house.Hp})");
        new KillCommand { Entities = new[] { house.Id }, Player = s.Human!.Id }.Apply(W);
        W.SelfDestruct(house);
        s.Run(1);
        t.True(house.Alive, "its owner cannot blow it up or sell it");
        ulong h1 = W.StateHash();
        W.SetIndestructible(house, false);
        t.True(W.StateHash() != h1, "part of the world's state (the state hash)");
    }

    // ------------------------------------------------------------------ the passes (round 6): in view, wide, towers beside them
    // Kacper: "make the pillars near the entrances less tall, as they would make the incoming enemies less visible";
    // "make the entrances 25% wider so you can place turrets a bit more easily"
    static readonly Dictionary<string, double> Tops = new()
    {
        ["ice_plateau_01"] = 8.9, ["ice_plateau_02"] = 8.9, ["ice_plateau_03"] = 8.9, ["ice_plateau_addon_01"] = 8.5, ["ice_plateau_addon_02"] = 8.5,
        ["ice_plateau_addon_03"] = 17.3, ["ice_plateau_addon_04"] = 16.0, ["ice_middlerock_01"] = 6.9, ["ice_middlerock_02"] = 7.9, ["ice_middlerock_03"] = 3.6,
        ["ice_snow_middlerock_01"] = 6.9, ["ice_snow_middlerock_02"] = 7.9, ["ice_snow_middlerock_03"] = 3.6, ["ice_smallrock_01"] = 5.2, ["ice_smallrock_02"] = 5.9,
        ["ice_smallrock_03"] = 4.5, ["ice_smallstone_01"] = 1.7, ["ice_smallstone_02"] = 3.9, ["ice_smallstone_03"] = 2.8,
    };

    public static void OutbreakPasses(TestContext t)
    {
        var folder = Folder();
        var md = MapFiles.Read(folder);
        var camps = Layout()["camps"]!.AsArray().Select(c => c!).ToList();
        static double SegDist(double px, double py, double ax, double ay, double bx, double by)
        {
            double vx = bx - ax, vy = by - ay, l2 = vx * vx + vy * vy, u = Math.Clamp(((px - ax) * vx + (py - ay) * vy) / l2, 0, 1);
            double dx = ax + vx * u - px, dy = ay + vy * u - py; return Math.Sqrt(dx * dx + dy * dy);
        }
        // (1) no dressing piece near a pass stands more than 4 m over the ground (a unit's height or so): the incoming
        // enemies stay in view - tall cliff pieces and pillars only further along the ridge
        int near = 0, tall = 0; var bad = new List<string>();
        foreach (var pl in md.Plants)
        {
            if (!Tops.TryGetValue(pl.Name, out var top)) continue;
            bool inZone = camps.Any(c => SegDist(pl.X, pl.Y, c["entrance_in"]![0].Num(), c["entrance_in"]![1].Num(), c["entrance_out"]![0].Num(), c["entrance_out"]![1].Num()) < 30);
            double h = pl.Z + top - md.HeightAtMap(pl.X, pl.Y);
            if (!inZone) { if (h > 6) tall++; continue; }
            near++;
            if (h > 4.2) bad.Add($"{pl.Name} {h:0.0} m at ({pl.X:0}, {pl.Y:0})");
        }
        t.True(near > 20, $"the passes' ridge ends are dressed ({near} rocks within 30 m)");
        t.True(bad.Count == 0, "no piece over 4 m within 30 m of a pass: " + string.Join("; ", bad.Take(8)));
        t.True(tall > 50, $"tall cliff pieces stay along the rest of the ring ({tall})");
        // (2) the passes 25 m wide (the south-west one 20 m): the walk grid open 11 m either side of the line (9 m)
        var wg = md.Walk!;
        for (int k = 0; k < 3; k++)
        {
            var c = camps[k];
            double ax = c["entrance_in"]![0].Num(), ay = c["entrance_in"]![1].Num(), bx = c["entrance_out"]![0].Num(), by = c["entrance_out"]![1].Num();
            double l = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)), tx = (bx - ax) / l, ty = (by - ay) / l;
            double half = k == 2 ? 9 : 11;
            // (across the ring's middle: the point of the line nearest the ring, roughly halfway)
            double mx = ax + tx * l * 0.35, my = ay + ty * l * 0.35;
            bool open = true;
            for (double o = -half; o <= half; o += 1) { int i = (int)((mx - ty * o) / 2), j = (int)((my + tx * o) / 2); if (wg.Cells[j * wg.W + i] == 0) open = false; }
            t.True(open, $"pass {c["name"]} open {half * 2:0} m across");
        }
        // (3) towers of every tribe fit beside each passage just inside the ring, on both sides
        foreach (var (cfg, towers) in new[] { ("base", new[] { "hu_small_tower", "aje_small_tower", "ninigi_small_tower" }), ("mirage", new[] { "seas_turret_tower" }) })
        {
            var s = Session(t, cfgId: cfg);
            s.Run(1);
            var W = s.World; var C = s.Campaign;
            for (int k = 0; k < 3; k++)
            {
                var c = camps[k];
                double ax = c["entrance_in"]![0].Num(), ay = c["entrance_in"]![1].Num(), bx = c["entrance_out"]![0].Num(), by = c["entrance_out"]![1].Num();
                double l = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)), tx = (bx - ax) / l, ty = (by - ay) / l;
                foreach (var cls in towers)
                    foreach (var side in new[] { -1, 1 })
                    {
                        bool ok = false;
                        // (by the passage's edge, a few metres in from the ring's inner foot)
                        for (double o = 9; o <= 17 && !ok; o += 4)
                            for (double along = 0; along <= 8 && !ok; along += 4)
                            {
                                var (gx, gz) = C.ToGame(ax + tx * along - ty * o * side, ay + ty * along + tx * o * side);
                                ok = W.PlacementOf(cls, gx, gz, 0, s.Human).Ok;
                            }
                        t.True(ok, $"{cls} fits {(side < 0 ? "left" : "right")} of pass {c["name"]} inside the ring");
                    }
            }
        }
    }

    // ------------------------------------------------------------------ the layout: StarCraft II's scale
    static readonly string[] NorseBuildings =
    {
        "hu_arena", "hu_bunker", "hu_corn_field", "hu_fireplace", "hu_kennel", "hu_large_tower", "hu_lumberjack_cottage", "hu_machine_maker", "hu_magic_cauldron",
        "hu_small_animal_farm", "hu_small_tower", "hu_stone_cottage", "hu_stone_quarry", "hu_tavern", "hu_temple", "hu_warehouse", "hu_warpgate", "hu_weapons_smith",
    };

    public static void OutbreakLayout(TestContext t)
    {
        var folder = Folder();
        var md = MapFiles.Read(folder);
        var layout = Layout();
        // (768 m: the brief's fallback from 1024 m - the view's memory, docs/OUTBREAK.md)
        t.True(md.W >= 768 && md.H >= 768, $"a 768 m map ({md.W} x {md.H})");
        double pr = layout["base_r"].Num();
        t.True(pr * 2 >= 200 && pr * 2 <= 250, $"the base is 200 - 250 m across ({pr * 2:0})");
        var vs = layout["villages"]!.AsArray().Select(v => v!).ToList();
        int nb = vs.Sum(v => v["buildings"]!.AsArray().Count);
        t.True(vs.Count >= 10 && vs.Count <= 14, $"10 - 14 villages ({vs.Count})");
        t.True(nb >= 70 && nb <= 100, $"70 - 100 infected buildings ({nb})");
        double D(System.Text.Json.Nodes.JsonNode v) => Math.Sqrt((v["x"].Num() - Cx) * (v["x"].Num() - Cx) + (v["y"].Num() - Cy) * (v["y"].Num() - Cy));
        var outposts = vs.Where(v => v["size"].Str() == "outpost").ToList(); var towns = vs.Where(v => v["size"].Str() == "town").ToList();
        t.True(outposts.Count >= 3 && towns.Count >= 3, "outposts and towns");
        t.True(outposts.Max(D) < towns.Min(D), "the outposts near the base, the towns far out");
        t.True(outposts.Average(v => v["buildings"]!.AsArray().Count) < towns.Average(v => v["buildings"]!.AsArray().Count) - 4, "the towns bigger, denser");
        // every placeable Norse building class
        var world = File.ReadAllText(Path.Combine(folder, "world.lua"));
        foreach (var c in NorseBuildings) t.True(world.Contains($"class = \"{c}\""), $"{c} stands in a village");
        // the entrances: north and east open, the south-west one closed by the rockslide
        var camps = layout["camps"]!.AsArray().Select(c => c!).ToList();
        double Ang(System.Text.Json.Nodes.JsonNode c) { double a = Math.Atan2(c["entrance_in"]![1].Num() - Cy, c["entrance_in"]![0].Num() - Cx) * 180 / Math.PI; return a < 0 ? a + 360 : a; }
        t.True(Math.Abs(Ang(camps[0]) - 270) < 15 && (Ang(camps[1]) < 15 || Ang(camps[1]) > 345) && Math.Abs(Ang(camps[2]) - 135) < 15, $"passes north, east, south-west ({Ang(camps[0]):0}, {Ang(camps[1]):0}, {Ang(camps[2]):0})");
        var bar = layout["barrier"]!;
        t.Equal(3, bar["entrance"]!.Int(), "the rockslide closes the south-west pass");
        t.True(bar["rocks"]!.AsArray().Count >= 10, "a rockslide of rocks");
        // a rockslide in the pass, not a ring: its rocks lie in the gap between the ridges, heaped (not round an empty
        // middle)
        var rocks = world.Split("name = \"OB_Rockfall_").Skip(1).Select(x => (X: Num(x, "x = "), Y: Num(x, "y = "))).ToList();
        double bx = bar["x"].Num(), by = bar["y"].Num();
        t.True(rocks.All(r => Math.Sqrt((r.X - bx) * (r.X - bx) + (r.Y - by) * (r.Y - by)) < 20), "the rocks lie in the pass");
        t.True(rocks.Count(r => Math.Sqrt((r.X - bx) * (r.X - bx) + (r.Y - by) * (r.Y - by)) < 6) >= 3, "heaped in the middle too (not a ring)");
        // round 5 (Kacper: "remove the reinforced wall and make it the natural terrain"): no walls in the map's files
        t.True(layout["walls"] == null && !world.Contains("hu_re_enforced_wall") && !File.ReadAllText(Path.Combine(folder, "objects.lua")).Contains("_wall\""), "no walls in world.lua, objects.lua, layout.lua");
        // the ring is rock all round: a straight line out from the centre crosses unwalkable ridge (the walk grid) in
        // every direction but through the three passes - no gap left where the walls stood
        var wg = md.Walk!;
        var gaps = new List<double>();
        for (double a = 0; a < 360; a += 0.5)
        {
            bool free = true;
            for (double r = 60; r <= 190 && free; r += 1)
            {
                int i = (int)((Cx + Math.Cos(a * Math.PI / 180) * r) / 2), j = (int)((Cy + Math.Sin(a * Math.PI / 180) * r) / 2);
                if (wg.Cells[j * wg.W + i] == 0) free = false;
            }
            if (free) gaps.Add(a);
        }
        static double AD(double x, double y) { double d = Math.Abs(x - y) % 360; return d > 180 ? 360 - d : d; }
        t.True(gaps.All(g => new[] { 270.0, 0.0, 135.0 }.Any(e => AD(g, e) < 20)), "the ring is closed but for its passes: open lines at " + string.Join(", ", gaps));
        t.Equal(2, layout["lords"]!.AsArray().Count, "two plague lords' lairs");
    }

    static double Num(string s, string key) { int i = s.IndexOf(key, StringComparison.Ordinal) + key.Length; int j = i; while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.' || s[j] == '-')) j++; return double.Parse(s[i..j], System.Globalization.CultureInfo.InvariantCulture); }

    // ------------------------------------------------------------------ epoch 2 at the start
    public static void OutbreakEpochTwo(TestContext t)
    {
        var s = Session(t);
        s.Run(2);
        var h = s.Human!;
        t.Equal(2, h.Epoch(), "the player starts in epoch 2");
        t.True(h.Units >= 11 && h.Units <= h.MaxUnits, $"a start army of epoch 2 that fits the unit limit ({h.Units}/{h.MaxUnits})");
        StartsWorking(t, s, h, "Hu");
        // the start army's archer stands as an archer (it came as the chariot before Campaign.MountOf's fix)
        var mine = s.World.Units.Where(u => u.Alive && u.Owner == h).Select(u => u.Name).ToList();
        t.Equal(1, mine.Count(n => n == "hu_archer"), "an archer in the start army: " + string.Join(", ", mine));
        t.True(!mine.Any(n => n.Contains("chariot")), "no chariot: " + string.Join(", ", mine));
        t.True(s.World.Buildings.Count(b => b.Owner == h && b.Alive) >= 3, "the headquarters, a house and the field");
        t.True(s.World.Events.All(e => e.Type != "upgrade"), "no \"epoch reached\" message for the start");
    }

    /// <summary>round 4: six workers, two at the seat's own field (pre-built, its tribe's food building), two at wood, two
    /// at stone - at work from the start</summary>
    static void StartsWorking(TestContext t, MissionSession s, Player p, string tribe)
    {
        var W = s.World;
        var workers = W.Units.Where(u => u.Alive && u.Owner == p && u.IsWorker).ToList();
        t.Equal(6, workers.Count, $"{tribe}: six workers");
        var field = W.Buildings.Where(b => b.Alive && b.Owner == p && W.IsFarm(b) && b.Def!.Unlimited.Contains("food")).ToList();
        t.True(field.Count == 1 && field[0].Built, $"{tribe}: its field stands ready ({string.Join(", ", W.Buildings.Where(b => b.Alive && b.Owner == p).Select(b => b.Name))})");
        int On(string res) => workers.Count(u => u.Task.Type == "gather" && u.Task.Res == res);
        t.True(On("food") == 2 && On("wood") == 2 && On("stone") == 2, $"{tribe}: two at food, wood and stone each ({On("food")}, {On("wood")}, {On("stone")}: " + string.Join(", ", workers.Select(u => u.Task.Type + ":" + u.Task.Res)) + ")");
        t.True(workers.Where(u => u.Task.Res == "food").All(u => u.Task.Farm == field[0]), $"{tribe}: the food workers at the field");
    }

    // ------------------------------------------------------------------ a headless run: the first night
    static List<Unit> Infected(MissionSession s) => s.World.Units.Where(u => u.Alive && u.Owner?.Id == 1).ToList();
    static List<int> RockIds(MissionSession s) => Layout()["barrier"]!["rocks"]!.AsArray().Select(r => s.Host.FindObject(r.Str())).Where(i => i >= 0).ToList();
    static string Lua(MissionSession s, string expr) => s.Lua.Lua.RunString("return " + expr, "test")?.CastToString() ?? "";
    /// <summary>the nav grid's cells reached on foot from the base's centre (the ridges, the rockslide's
    /// rocks block), the circles round `shut` (game x, z) closed</summary>
    static bool[] NavFlood(MissionSession s, List<(double X, double Z)> shut, double r)
    {
        var nav = s.World.Nav; int n = nav.N;
        var (cx, cz) = s.Campaign.ToGame(Cx, Cy);
        var seen = new bool[n * n];
        var st = new Stack<int>(); int a = nav.Idx(cx, cz); seen[a] = true; st.Push(a);
        while (st.Count > 0)
        {
            int c = st.Pop(), ci = c % n, cj = c / n;
            foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int i = ci + di, j = cj + dj;
                if (i < 0 || j < 0 || i >= n || j >= n) continue;
                int k = j * n + i;
                if (seen[k] || nav.Block[k] != 0) continue;
                var (x, z) = nav.Center(k);
                if (shut.Any(p => (x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z) < r * r)) continue;
                seen[k] = true; st.Push(k);
            }
        }
        return seen;
    }
    /// <summary>is the map point (x, y) - or a cell within 4 m - among the reached ones</summary>
    static bool NavAt(MissionSession s, bool[] seen, double x, double y)
    {
        var nav = s.World.Nav; var (gx, gz) = s.Campaign.ToGame(x, y);
        for (double dx = -4; dx <= 4; dx += 2) for (double dz = -4; dz <= 4; dz += 2) { int k = nav.Idx(gx + dx, gz + dz); if (k >= 0 && seen[k]) return true; }
        return false;
    }
    /// <summary>can pass `camp` be walked through from the base's centre (the nav grid with its props; the other passes'
    /// inner ends shut - 34 m round them: the 25 m passes of round 6 -, so the way round through them does not count)?</summary>
    static bool PassOpen(MissionSession s, int camp)
    {
        var camps = Layout()["camps"]!.AsArray().Select(c => c!).ToList();
        var shut = camps.Where((c, k) => k != camp).Select(c => s.Campaign.ToGame(c["entrance_in"]![0].Num(), c["entrance_in"]![1].Num())).ToList();
        return NavAt(s, NavFlood(s, shut, 34), camps[camp]["entrance_out"]![0].Num(), camps[camp]["entrance_out"]![1].Num());
    }
    static int Near(MissionSession s, Entity e, double r) => Infected(s).Count(u => (u.Pos - e.Pos).Length < r);

    public static void OutbreakNightWaves(TestContext t)
    {
        var P = new RecordingPresenter();
        var s = Session(t, p: P);
        var W = s.World; var C = s.Campaign;
        t.True(s.Problems.Count == 0, "no load problems: " + string.Join("; ", s.Problems));
        s.Run(5);
        t.True(P.Quests.Any(q => q.Quest == "OB_MQ01" && q.Change == "shown") && P.Quests.Any(q => q.Quest == "OB_SQ02" && q.Change == "shown"), "the objectives are shown");
        t.True(P.Dialogs.Any(d => d.Path.Contains("ob_start")), "the start's dialogue");
        int guards = Infected(s).Count;
        t.True(guards >= 40, $"guards by the villages ({guards})");
        t.True(W.Buildings.Where(b => b.Owner?.Id == 1).All(b => b.Infection >= 2), "the infected buildings are drawn infected");
        t.True(Infected(s).All(u => u.Infection == 3), "the infected units too: " + string.Join(", ", Infected(s).Where(u => u.Infection != 3).Select(u => $"{u.Name} {u.Infection} {u.Id}")));
        t.True(P.InfoBar.Contains("0/2"), "the info bar counts the plague lords: " + P.InfoBar);
        s.Run(60);
        t.True(Infected(s).Count(u => u.IsBurning) == 0 && Infected(s).Count >= guards, $"the guards stand in their buildings' shade: none burns ({Infected(s).Count})");
        t.True(Infected(s).All(u => u.Infection == 3), "the kennels' animals are drawn infected too: " + string.Join(", ", Infected(s).Where(u => u.Infection != 3).Select(u => $"{u.Name} {u.Infection}")));
        // by day nothing comes: after 6 minutes no infected unit is in the base
        var (cx, cz) = C.ToGame(Cx, Cy);
        double Mid(Unit u) => Math.Sqrt((u.Pos.X - cx) * (u.Pos.X - cx) + (u.Pos.Z - cz) * (u.Pos.Z - cz));
        // the weak daytime defence: a building hit by day sends 2 out
        var vb = W.Buildings.Where(b => b.Owner?.Id == 1 && b.Alive).OrderBy(b => (b.Pos.X - cx) * (b.Pos.X - cx) + (b.Pos.Z - cz) * (b.Pos.Z - cz)).ToList();
        int d0 = Near(s, vb[0], 25);
        W.SetHp(vb[0], vb[0].Hp - 40);
        s.Run(3);
        int dayDef = Near(s, vb[0], 25) - d0;
        t.True(dayDef >= 1 && dayDef <= 3, $"by day a building under attack sends a few defenders ({dayDef})");
        s.Run(360 - W.Time);
        t.Equal(0, Infected(s).Count(u => Mid(u) < 125), "by day the infected stay away");
        t.Equal(DayPhase.Day, W.DayNightNow!.Value.Phase, "still the first day at 6:00 (Normal: an 8-minute first day)");
        // dusk at 8:00, the night at 8:30: the first pulse comes out of the villages
        s.Run(480 + 30 + 3 - W.Time);
        t.Equal(DayPhase.Night, W.DayNightNow!.Value.Phase, "night");
        int n0 = Infected(s).Count;
        t.True(n0 > guards, $"the first wave came out ({guards} -> {n0})");
        t.True(P.Dialogs.Any(d => d.Path.Contains("ob_dusk")), "the night's dialogue");
        // the night's counterattack: a building hit at night spawns many, fast
        var nb = vb[1];
        int c0 = Near(s, nb, 25);
        W.SetHp(nb, nb.Hp - 40);
        s.Run(2);
        int night = Near(s, nb, 25) - c0;
        t.True(night >= 5 && night > dayDef + 2, $"at night a building under attack spawns many ({night}, by day {dayDef})");
        // the first night's waves: only the first night's classes
        var allowed = Lua(s, "outbreak.wave_classes(1)").Split(',').ToHashSet();
        var waveCls = Infected(s).Where(u => Mid(u) < 380).Select(u => u.Name).Distinct().ToList();
        t.True(!waveCls.Any(n => n.Contains("killer") || n == "Woolly_Rhino" || n == "Mammoth"), "night 1: no aberrations yet: " + string.Join(",", waveCls));
        int close0 = Infected(s).Count(u => Mid(u) < 170);
        s.Run(100);
        int close = Infected(s).Count(u => Mid(u) < 170);
        t.True(close >= close0 + 3, $"the waves walk to the base ({close} infected within 170 m of the centre)");
        t.True(Infected(s).Count > n0, "more pulses during the night (a steady stream)");
        // dawn: the attackers in the open catch fire (staggered) and burn to death; the guards by their buildings stay
        int near0 = Infected(s).Count(u => Mid(u) < 170);
        s.Run(480 + 30 + 180 + 10 - W.Time);
        t.Equal(DayPhase.Dawn, W.DayNightNow!.Value.Phase, "dawn");
        int burning = Infected(s).Count(u => u.IsBurning);
        t.True(burning > 0, $"at dawn the infected in the open burn ({burning} on fire)");
        t.True(Infected(s).Count(u => Mid(u) < 170) > 0 || near0 == 0, "not all at once: staggered");
        s.Run(480 + 30 + 180 + 30 + 3 - W.Time);
        t.Equal(DayPhase.Day, W.DayNightNow!.Value.Phase, "day");
        // (the kennels' two animals roam within 50 m of their kennel - an outpost's - day and night: not the night's dead)
        t.Equal(0, Infected(s).Count(u => Mid(u) < 170 && u.HomeRadius == 0), $"by day no infected unit is left near the base (they burnt; {near0} were near it at night): " + string.Join(", ", Infected(s).Where(u => Mid(u) < 170 && u.HomeRadius == 0).Select(u => $"{u.Name} {Mid(u):0} m burning {u.IsBurning} {u.BurnUntil:0.0} t {W.Time:0.0} hp {u.Hp}")));
        t.True(Infected(s).Count >= guards / 2, $"the guards by their buildings stay ({Infected(s).Count})");
        t.True(!(s.Over), "the mission goes on");
        t.True(s.Lua.Lua.Errors.Count == 0, "no errors in the mission's Lua: " + string.Join("; ", s.Lua.Lua.Errors));
    }

    // ------------------------------------------------------------------ the escalation: the nights' classes
    public static void OutbreakEscalation(TestContext t)
    {
        var s = Session(t);
        s.Run(2);
        string[] N(int n) => Lua(s, $"outbreak.wave_classes({n})").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var n1 = N(1); var n2 = N(2); var n3 = N(3); var n5 = N(5);
        t.True(n1.Contains("hu_undead_warrior") && !n1.Contains("hu_warrior") && !n1.Any(c => c.Contains("killer")), "night 1: the infected villagers - " + string.Join(",", n1));
        t.True(n2.Contains("hu_warrior") && n2.Contains("hu_spearman") && !n2.Any(c => c.Contains("killer")), "night 2: the soldiers - " + string.Join(",", n2));
        t.True(n3.Contains("hu_undead_killer") && n3.Contains("Woolly_Rhino"), "night 3: the aberrations - " + string.Join(",", n3));
        t.True(n5.Contains("Mammoth") && !n5.Contains("Wild_Boar"), "night 5: the big beasts, no more boars - " + string.Join(",", n5));
        t.True(n1.Contains("Wild_Boar"), "the wild animals spawn (the rules' spelling: Wild_Boar)");
        // the day is shorter on Hard (StarCraft II: 6 minutes on Normal, 3 on Brutal)
        t.Near(360, s.World.DayNight!.Day, 1e-9, "Normal: a 6-minute day");
        var hard = Session(t, difficulty: 2); hard.Run(2);
        t.Near(180, hard.World.DayNight!.Day, 1e-9, "Hard: a 3-minute day");
        t.Near(300, hard.World.DayNight!.FirstDay, 1e-9, "Hard: a 5-minute first day");
    }

    // ------------------------------------------------------------------ the rockfall and the plague lords (Hard: short days)
    public static void OutbreakNights(TestContext t)
    {
        var P = new RecordingPresenter();
        var s = Session(t, difficulty: 2, p: P);
        var W = s.World;
        s.Run(100);
        // by day the lords are out of the world: hidden, not to be hit
        var lords = Lua(s, "table.concat(outbreak.lords(), ',')").Split(',').Select(x => (int)double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        t.Equal(2, lords.Count, "two plague lords");
        Entity Lord(int id) => s.Campaign.Objects.ById(id)!.Entity!;
        t.True(lords.All(id => Lord(id).Parked && Lord(id).Untargetable), "by day the lords hide (not drawn, not hit)");
        t.True(lords.All(id => Lord(id).DrawScale > 1.1 && Lord(id).MaxHp >= 6000), "big and strong");
        t.True(!PassOpen(s, 2), "the rockslide closes the south-west pass");
        t.True(PassOpen(s, 0) && PassOpen(s, 1), "north and east are open");
        // night 1: the lords come out; the rockfall holds
        s.Run(300 + 30 + 20 - W.Time);
        t.Equal(DayPhase.Night, W.DayNightNow!.Value.Phase, "night 1");
        t.True(lords.All(id => !Lord(id).Parked && !Lord(id).Untargetable), "at night the lords come out");
        t.True(P.Dialogs.Any(d => d.Path.Contains("ob_lords")), "the lords' dialogue");
        // round 4: the lords never join the waves - all night each stays in its own town (patrols round its lair, fights
        // what comes near, walks back)
        var lairs = Layout()["lords"]!.AsArray().Select(l => s.Campaign.ToGame(l!["x"].Num(), l["y"].Num())).ToList();
        double far = 0;
        for (double tt = W.Time; tt < 300 + 30 + 175; tt += 5)
        {
            s.Run(5);
            foreach (var id in lords)
            {
                var e = Lord(id);
                double d = lairs.Min(l => Math.Sqrt((e.Pos.X - l.X) * (e.Pos.X - l.X) + (e.Pos.Z - l.Z) * (e.Pos.Z - l.Z)));
                far = Math.Max(far, d);
            }
        }
        t.True(far < 65, $"the lords stay in their towns all night ({far:0} m from the lair at most)");
        var (bcx, bcz) = s.Campaign.ToGame(Cx, Cy);
        t.True(lords.All(id => Math.Sqrt((Lord(id).Pos.X - bcx) * (Lord(id).Pos.X - bcx) + (Lord(id).Pos.Z - bcz) * (Lord(id).Pos.Z - bcz)) > 250), "far from the base");
        t.Equal("false", Lua(s, "tostring(outbreak.breached())"), "night 1: the rockfall holds");
        t.True(!PassOpen(s, 2), "night 1: the south-west pass still closed");
        // the side quest: 0/2 -> 1/2 -> 2/2 and the reward
        var h = s.Human!;
        W.Kill(Lord(lords[0]), null);
        s.Run(2);
        t.Equal("1", s.Engine!.GetVar("ob_lords"), "one lord slain (1/2)");
        t.True(P.InfoBar.Contains("1/2"), "the info bar: " + P.InfoBar);
        double food = h.Res["food"], wood = h.Res["wood"], stone = h.Res["stone"];
        W.Kill(Lord(lords[1]), null);
        s.Run(2);
        t.Equal("2", s.Engine!.GetVar("ob_lords"), "both slain (2/2)");
        t.True(P.Quests.Any(q => q.Quest == "OB_SQ02" && q.Change == "done"), "the side quest is done");
        t.True(h.Res["food"] >= food + 450 && h.Res["wood"] >= wood + 450 && h.Res["stone"] >= stone + 350, $"the reward: food {food} -> {h.Res["food"]}, wood {wood} -> {h.Res["wood"]}, stone {stone} -> {h.Res["stone"]}");
        // night 2: the infected break the rockfall
        double n2 = 300 + 30 + 180 + 30 + 180 + 30;
        s.Run(n2 + 20 - W.Time);
        t.Equal(2, W.DayNightNow!.Value.NightNo, "night 2");
        t.Equal("false", Lua(s, "tostring(outbreak.breached())"), "not yet at its start");
        s.Run(n2 + 50 - W.Time);
        t.Equal("true", Lua(s, "tostring(outbreak.breached())"), "night 2: the rockfall is broken");
        t.True(PassOpen(s, 2), "the south-west pass is open");
        t.True(P.Dialogs.Any(d => d.Path.Contains("ob_breach")) && P.Calls.Any(c => c == "marker ob_breach"), "announced: a line and a ping");
        t.True(s.Lua.Lua.Errors.Count == 0, "no errors in the mission's Lua: " + string.Join("; ", s.Lua.Lua.Errors));
    }

    public static void OutbreakEarlyBreach(TestContext t)
    {
        var s = Session(t, difficulty: 2);
        var W = s.World;
        s.Run(3);
        var inf = W.Buildings.Where(b => b.Owner?.Id == 1 && b.Alive).ToList();
        foreach (var b in inf.Take((int)Math.Ceiling(inf.Count * 0.4))) W.Kill(b, null);
        s.Run(200);
        t.Equal("false", Lua(s, "tostring(outbreak.breached())"), "by day the rockfall holds (the infected break it at night)");
        s.Run(300 + 30 + 3 - W.Time);
        t.Equal(1, W.DayNightNow!.Value.NightNo, "night 1");
        t.Equal("true", Lua(s, "tostring(outbreak.breached())"), "40 % burnt: the rockfall breaks in the first night");
        t.True(PassOpen(s, 2), "the south-west pass is open");
    }

    // ------------------------------------------------------------------ the sunlight (round 4): the infected burn by day
    public static void OutbreakSunlight(TestContext t)
    {
        var s = Session(t);
        var W = s.World; var C = s.Campaign;
        s.Run(5);
        var inf = W.Buildings.Where(b => b.Owner?.Id == 1 && b.Alive).ToList();
        // a guard taken far out into the open: it catches fire within seconds and burns to death in 5 - 15 s
        var g = Infected(s).OrderBy(u => u.Id).First(u => u.DrawScale <= 1);
        var (cx, cz) = C.ToGame(Cx, Cy);
        // (halfway between its village and the base: in the open, away from every infected building)
        double tx = (g.Pos.X + cx) / 2, tz = (g.Pos.Z + cz) / 2;
        t.True(inf.All(b => Math.Sqrt((b.Pos.X - tx) * (b.Pos.X - tx) + (b.Pos.Z - tz) * (b.Pos.Z - tz)) > 40), "the spot is in the open");
        s.Host.Teleport(g.Rec!.Id, C.ToMap(tx, tz).MX, C.ToMap(tx, tz).MY);
        s.Host.OrderStop(g.Rec.Id);
        s.Run(8);
        t.True(g.IsBurning || !g.Alive, "in the open by day it catches fire");
        double hp0 = g.Hp;
        s.Run(18);
        t.True(!g.Alive, "and burns to death");
        // a building's daytime defenders fight, then burn (50 s)
        var b0 = inf.OrderBy(b => (b.Pos.X - cx) * (b.Pos.X - cx) + (b.Pos.Z - cz) * (b.Pos.Z - cz)).First();
        int d0 = Lua(s, "outbreak.count('defender')") is var str && int.TryParse(str, out var dn) ? dn : 0;
        W.SetHp(b0, b0.Hp - 40);
        s.Run(3);
        int d1 = int.Parse(Lua(s, "outbreak.count('defender')"));
        t.True(d1 > d0, $"defenders came out ({d0} -> {d1})");
        s.Run(30);
        t.True(int.Parse(Lua(s, "outbreak.count('defender')")) == d1, "they fight a while");
        s.Run(45);
        t.Equal("0", Lua(s, "outbreak.count('defender')"), "then they burn");
        // the guards in their buildings' shade stay
        t.True(Infected(s).Count(u => u.IsBurning) == 0, "nobody else burns");
        t.True(s.Lua.Lua.Errors.Count == 0, "no errors in the mission's Lua: " + string.Join("; ", s.Lua.Lua.Errors));
    }

    // ------------------------------------------------------------------ MIRAGE: its zombies join; every class the waves name exists or is skipped
    public static void OutbreakMirage(TestContext t)
    {
        var s = Session(t, difficulty: 2, cfgId: "mirage");
        s.Run(300 + 30 + 100 - s.World.Time);
        t.True(s.Lua.Lua.Errors.Count == 0, "no errors in the mission's Lua: " + string.Join("; ", s.Lua.Lua.Errors));
        // every tribe starts in epoch 2 and its start fits its unit limit (MIRAGE: the SEAS too)
        foreach (var tribe in new[] { "Hu", "Aje", "Ninigi", "SEAS" })
        {
            var m = RemakeMissions.ByName("outbreak")!;
            var c = CampaignTests.LoadCfgPublic("mirage")!;
            var st = RemakeMissions.Start(m, "mirage", tribe, 1, 5);
            var ts = MissionSession.Create(st, c.Config, new SessionOptions { Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data });
            ts.Run(2);
            var h = ts.Human!;
            t.True(h.Units <= h.MaxUnits && h.MaxUnits > 0 && h.Units >= 11, $"{tribe}: the start fits the unit limit ({h.Units}/{h.MaxUnits})");
            StartsWorking(t, ts, h, tribe);
            // (the archer in the tribe's class: Tribes.cs's "archer" role)
            var mine = ts.World.Units.Where(u => u.Alive && u.Owner == h).Select(u => u.Name).ToList();
            t.True(mine.Any(n => n is "hu_archer" or "aje_archer" or "ninigi_archer" or "seas_marksman"), $"{tribe}: the start army's archer: " + string.Join(", ", mine));
            t.Equal(2, h.Epoch(), $"{tribe}: epoch 2");
        }
        var names = Infected(s).Select(u => u.Name).Distinct().ToList();
        t.True(names.Any(n => n.StartsWith("hu_zombie_")), "MIRAGE's zombies come: " + string.Join(", ", names));
        t.True(Infected(s).Count > 60, $"the first night on Hard ({Infected(s).Count} infected)");
    }

    // ------------------------------------------------------------------ destroying every infected building wins
    public static void OutbreakWin(TestContext t)
    {
        var P = new RecordingPresenter();
        var s = Session(t, p: P);
        var W = s.World;
        s.Run(3);
        var inf = W.Buildings.Where(b => b.Owner?.Id == 1 && b.Alive).ToList();
        int total = inf.Count;
        // half: the dialogue
        foreach (var b in inf.Take(total / 2 + 1)) W.Kill(b, null);
        s.Run(10);
        t.Equal((total - total / 2 - 1).ToString(), s.Engine!.GetVar("ob_left"), "the buildings left (the info bar's variable)");
        t.True(P.Dialogs.Any(d => d.Path.Contains("ob_half")), "half of them gone: the dialogue");
        t.True(!(s.Over), "not yet");
        foreach (var b in inf.Skip(total / 2 + 1)) W.Kill(b, null);
        s.Run(12);
        t.True(P.Dialogs.Any(d => d.Path.Contains("ob_win")), "the winning words");
        t.True(s.Over && s.Won, "every infected building destroyed: won");
    }

    public static void OutbreakLose(TestContext t)
    {
        var s = Session(t);
        var W = s.World;
        s.Run(3);
        foreach (var b in W.Buildings.Where(b => b.Owner == s.Human && b.Name == "hu_fireplace").ToList()) W.Kill(b, null);
        s.Run(8);
        t.True(s.Over && !s.Won, "the headquarters destroyed: lost");
    }

    // ------------------------------------------------------------------ co-op: three seats
    public static void OutbreakCoopThreeSeats(TestContext t)
    {
        Folder();
        var m = RemakeMissions.ByName("outbreak")!;
        var def = CoopDefinition.Load("base", m.Id);
        t.True(def != null && def.Supports(3), "the mission's co-op definition (coop.lua) with room for three seats");
        var seats = new[]
        {
            new CoopSeat { Seat = 1, Kind = "human", Name = "Host", Tribe = "Hu" },
            new CoopSeat { Seat = 2, Kind = "ai", Name = "AI 2", Tribe = "Aje" },
            new CoopSeat { Seat = 3, Kind = "ai", Name = "AI 3", Tribe = "Ninigi" },
        };
        var coop = CoopPlan.Make(def!, m.Folder, seats, 0);
        var s = Session(t, coop: coop);
        var W = s.World;
        t.True(s.Problems.Count == 0, "no load problems: " + string.Join("; ", s.Problems));
        var players = coop.Seats.Select(x => W.Players.First(p => p.Id == x.Slot)).ToList();
        t.Equal(3, players.Count, "three seats");
        var layout = Layout();
        // every seat's six workers at work from the start (the AI commanders of seats 2 and 3 take theirs over after that)
        s.Run(0.3);
        for (int k = 0; k < 3; k++) StartsWorking(t, s, players[k], $"seat {k + 1} ({players[k].Tribe})");
        s.Run(2);
        for (int k = 0; k < 3; k++)
        {
            var p = players[k];
            var camp = layout["camps"]![k]!;
            var (gx, gz) = s.Campaign.ToGame(camp["x"].Num(), camp["y"].Num());
            var own = W.Units.Cast<Entity>().Concat(W.Buildings).Where(e => e.Alive && e.Owner == p).ToList();
            t.True(own.Count > 0, $"seat {k + 1} has its start");
            double d = own.Min(e => Math.Sqrt((e.Pos.X - gx) * (e.Pos.X - gx) + (e.Pos.Z - gz) * (e.Pos.Z - gz)));
            t.True(d < 30, $"seat {k + 1} starts at camp {camp["name"]} ({d:0} m)");
            t.True(p.Units <= p.MaxUnits && p.MaxUnits > 0, $"seat {k + 1} ({p.Tribe}): the start fits the unit limit ({p.Units}/{p.MaxUnits})");
            t.Equal(2, p.Epoch(), $"seat {k + 1} ({p.Tribe}): epoch 2");
        }
        s.Run(28);
        t.True(!(s.Over), "the co-op game runs");
        // the waves are bigger with three seats (the co-op scaling of the enemy's spawns): the guards already
        int guards = Infected(s).Count;
        var s1 = Session(t);
        s1.Run(2);
        t.True(guards > Infected(s1).Count, $"more infected with three seats ({guards} vs {Infected(s1).Count})");
    }

    // ------------------------------------------------------------------ determinism and the saved game
    public static void OutbreakDeterministic(TestContext t)
    {
        ulong Hash()
        {
            var s = Session(t, seed: 11);
            s.Run(520);
            return s.StateHash();
        }
        t.Equal(Hash(), Hash(), "the same start, the same mission: the same state through the first dusk");
    }

    // ------------------------------------------------------------------ the saved game: the clock, the waves, the infected stages come back
    public static void OutbreakSaveLoad(TestContext t)
    {
        Folder();
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no base data");
        var m = RemakeMissions.ByName("outbreak")!;
        var start = RemakeMissions.Start(m, "base", "", 1, 99);
        (MissionSession S, GameRecorder Rec, PresenterTap Tap) Make(GameStart st, SaveGame? replayOf)
        {
            var tap = new PresenterTap(new RecordingPresenter());
            if (replayOf != null) SaveReplay.Prepare(replayOf, tap);
            var s = MissionSession.Create(st, c!.Config, new SessionOptions { Presenter = tap, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, Flags = SimFlags.Game });
            return (s, new GameRecorder(s.World, s.StateHash, tap), tap);
        }
        var a = Make(start, null);
        // into the first night (the waves out)
        int n = (int)(530 / PwRemake.Sim.World.World.STEP);
        for (int i = 0; i < n; i++) a.S.Step();
        t.Equal(DayPhase.Night, a.S.World.DayNightNow!.Value.Phase, "saved at night");
        var save = a.Rec.Capture(start, SimFlags.Game);
        var file = Path.Combine(Path.GetTempPath(), "outbreak-test.pwsave");
        save.Write(file);
        var loaded = SaveGame.Read(file);
        t.Equal("remake:outbreak", loaded.Start.MissionFolder, "the save names the mission by its token");
        var b = Make(loaded.Start, loaded);
        var rep = new SaveReplay(loaded, b.S.World, b.S.StateHash, b.Tap);
        rep.Run(); rep.End();
        t.True(!rep.Diverged, $"the replay follows the save ({rep.Problem})");
        t.Equal(a.S.StateHash(), b.S.StateHash(), "the loaded world is the saved one");
        t.Equal(a.S.World.DayNightNow, b.S.World.DayNightNow, "the clock: the same phase, night, time");
        t.Equal(Infected(a.S).Count, Infected(b.S).Count, "the same waves");
        t.True(b.S.World.Buildings.Where(x => x.Owner?.Id == 1 && x.Alive).Select(x => x.Infection).SequenceEqual(a.S.World.Buildings.Where(x => x.Owner?.Id == 1 && x.Alive).Select(x => x.Infection)), "the same infected stages");
        for (int i = 0; i < 1200; i++) { a.S.Step(); b.S.Step(); }
        t.Equal(a.S.StateHash(), b.S.StateHash(), "the same world a minute after loading");
    }

    // ------------------------------------------------------------------ the map editor opens it; its settings keep the cycle
    public static void OutbreakOpensInEditor(TestContext t)
    {
        var folder = Folder();
        var doc = PwRemake.Sim.Editor.MapDataDoc.Open(folder);
        t.Equal("Outbreak", doc.Name, "opened in the editor");
        t.True(doc.Mission.HasTriggers, "with its triggers and custom code");
        var pw = doc.ToMap();
        t.True(pw.DayNight != null && pw.DayNight.Day == 360 && pw.DayNight.FirstDay == 480, "the day-night cycle comes along");
        var st = PwRemake.Sim.Editor.MapSettings.Read(pw.ToMapData());
        t.True(st.DayNight != null && st.DayNight.Night == 180, "the map settings show it");
        // the editor's settings write it back; off: map.lua has none
        var md = pw.ToMapData();
        st.DayNight!.Night = 240; st.Write(md);
        t.Equal(240.0, md.DayNight!.Night, "changed in the settings");
        st.DayNight = null; st.Write(md);
        t.True(md.DayNight == null && !System.Text.Encoding.UTF8.GetString(PwMapWriter.Files(PwMap.FromMapData(md))["map.lua"]).Contains("day_night"), "switched off: no day_night in map.lua");
        // the mission's files come back byte for byte from the generator's
        var files = PwMapWriter.Files(PwMapReader.Read(new PwMapReader.Source(folder), false));
        foreach (var f in new[] { "map.lua", "objects.lua", "world.lua", "terrain/height.png", "terrain/walk.png" })
            t.True(files[f].AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(folder, f))), $"{f} read and written again: the same bytes");
    }
}
