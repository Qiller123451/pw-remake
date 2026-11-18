// Bridges (src/Sim/World/Bridges.cs, docs/SIMULATION.md "Bridges"): the ground just under a bridge's deck is not walked,
// bridges block their own pathfinder boxes, and MIRAGE 13's allies coming back from the SEAS outpost keep to walkable
// ground (player report: "ally units ended under the bridge after the SEAS camp was cleared and they were coming back").
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class BridgeTests
{
    /// <summary>the box given to mission 13's city bridge where the test data has none: 36 m along x, 10 m wide, 12 m high
    /// (the deck spans the ravine east - west: the ground under its ends, 21 - 22 m, rises to the deck's 24 m there)</summary>
    static readonly double[] BRIDGE_BOX = { -18, 0, -5, 18, 12, 5 };

    static ModelManifest ManifestWithBridgeBox(string data, double[] box)
    {
        var man = Json.ParseFile(Path.Combine(data, "assets", "manifest.json"))!;
        var mi = Json.TryParseFile(Path.Combine(data, "assets", "modelinfo.json")) as JsonObject ?? new JsonObject();
        if (mi["models"] is not JsonObject models) mi["models"] = models = new JsonObject();
        models["hcl13_bridge_01"] = new JsonObject { ["box"] = new JsonArray(box.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()) };
        string md = Path.Combine(data, "assets", "models");
        return new ModelManifest(man, mi, n => { var p = Path.Combine(md, n + ".glb"); return File.Exists(p) ? p : null; });
    }

    /// <summary>a ravine running north - south (x -15 .. 15, ground 17 m, off the walk grid) with banks at 22 m, a strip of
    /// ground at 23 m across it (z neckZ0 .. neckZ1) and the city bridge over it at z 0, deck 24 m</summary>
    static (W_ W, Player P) Ravine(TestContext t, double neckZ0, double neckZ1, double[] box)
    {
        string data = RulesTestData.Base(t);
        var R = Rules.Load(data);
        var M = ManifestWithBridgeBox(data, box);
        if (!M.Has("hcl13_bridge_01")) t.Skip("no hcl13_bridge_01 in the manifest");
        var map = new FlatMap(256)
        {
            HeightFn = (x, z) => Math.Abs(x) < 15 ? (z >= neckZ0 && z <= neckZ1 ? 23 : 17) : 22,
        };
        map.Blocked.Add((-15, -200, 15, neckZ0 - 0.01));
        map.Blocked.Add((-15, neckZ1 + 0.01, 15, 200));
        var W = new W_(R, M, map, new WorldOptions { Seed = 5 });
        var P = new Player(0, "Hu", new PlayerOpts { Rules = R });
        W.Players.Add(P);
        W.AddProp(new PropSpec { Model = "hcl13_bridge_01", X = 0, Z = 0, Y = 24, Rot = 0, Block = true });
        return (W, P);
    }

    /// <summary>is (x, z) under the bridge's box (turned 0, at the origin) on ground between 0.5 and 4 m below its deck?</summary>
    static bool UnderDeck(W_ W, double[] box, double x, double z)
    {
        double under = 24 - W.Height(x, z);
        return x >= box[0] && x <= box[3] && z >= box[2] && z <= box[5] && under > Bridges.UNDER_MIN && under < Bridges.UNDER_MAX;
    }

    static List<Unit> Walk(W_ W, Player P, double fx, double fz, double tx, double tz, int n, out int under, out int blocked, double seconds = 60)
    {
        var us = new List<Unit>();
        for (int i = 0; i < n; i++) us.Add((Unit)W.Spawn(i % 3 == 2 ? "hu_triceratops" : "hu_warrior", P, fx + (i % 3) * 1.5, fz + (i / 3) * 1.5)!);
        foreach (var u in us) W.Order(u, new Order("attackmove") { X = tx, Z = tz });
        under = 0; blocked = 0;
        for (int s = 0; s < seconds * 20; s++)
        {
            W.Step();
            foreach (var u in us)
            {
                if (!u.Alive) continue;
                if (UnderDeck(W, BRIDGE_BOX, u.Pos.X, u.Pos.Z)) under++;
                if (W.Nav.Block[W.Nav.Idx(u.Pos.X, u.Pos.Z)] != 0) blocked++;
            }
        }
        return us;
    }

    /// <summary>the strip across the ravine reaches under the bridge's south edge: those cells are blocked, the rest of
    /// the strip stays open, and units crossing from the bank beside the bridge go round under no deck</summary>
    public static void ShadeKeepsUnitsFromUnderTheDeck(TestContext t)
    {
        var (W, P) = Ravine(t, 3, 17, BRIDGE_BOX);
        var nav = W.Nav;
        var prop = W.Props.Single();
        t.True(prop.Cells.Count > 0 && prop.Blocking, $"the bridge (no pathfinder boxes) blocks the ground just under its deck ({prop.Cells.Count} cells)");
        foreach (var k in prop.Cells)
        {
            var (x, z) = nav.Center(k);
            double below = 24 - W.Height(x, z);
            if (!(below > Bridges.UNDER_MIN && below < Bridges.UNDER_MAX)) { t.True(false, $"cell ({x}, {z}) is {below:0.##} m under the deck"); break; }
        }
        t.True(nav.IsFree(0, 4.0) == false, "the strip under the bridge's south edge is blocked");
        t.True(nav.IsFree(0, 11), "the middle of the strip is free");
        t.True(nav.IsFree(-17, 20) && nav.IsFree(17, 20), "the banks away from the bridge are free");
        var us = Walk(W, P, -40, 0, 40, 0, 9, out int under, out int blocked);
        t.Equal(0, under, "unit-steps under the deck");
        t.Equal(0, blocked, "unit-steps on blocked cells");
        int there = us.Count(u => u.Alive && Math.Abs(u.Pos.X - 40) < 12 && Math.Abs(u.Pos.Z) < 12);
        t.True(there == us.Count, $"all crossed by the strip ({there} of {us.Count})");
    }

    /// <summary>a bridge whose box covers the whole strip: blocking all of it would cut the only way across - a smaller part
    /// of the box or nothing is blocked, and units still cross (for every width the way finder knows: 1 and 3 cells)</summary>
    public static void ShadeNeverCutsAWay(TestContext t)
    {
        var wide = new double[] { -18, 0, -16, 18, 12, 16 };
        var (W, P) = Ravine(t, -6, 6, wide);
        var nav = W.Nav;
        nav.LabelRegions();
        int west = nav.Region![nav.Idx(-40, 0)], east = nav.Region[nav.Idx(40, 0)];
        t.True(west >= 0 && west == east, "the strip still joins the two banks");
        // a big unit (clearance 3) too
        nav.UpdateClearance();
        t.True(nav.Find(-40, 0, 40, 0, 5.5) != null && nav.Found, "a big unit (radius 5.5) still finds the way across");
        var us = Walk(W, P, -40, 0, 40, 0, 6, out _, out int blocked, 80);
        t.Equal(0, blocked, "unit-steps on blocked cells");
        int there = us.Count(u => u.Alive && Math.Abs(u.Pos.X - 40) < 12 && Math.Abs(u.Pos.Z) < 12);
        t.True(there == us.Count, $"all crossed ({there} of {us.Count})");
    }

    /// <summary>ground far below a deck (an arch walked under in plain sight, more than 4 m) and ground at or above it (the
    /// raised strips under the river bridges) stay walkable</summary>
    public static void ShadeOnlyJustUnderTheDeck(TestContext t)
    {
        string data = RulesTestData.Base(t);
        var R = Rules.Load(data);
        var M = ManifestWithBridgeBox(data, BRIDGE_BOX);
        if (!M.Has("hcl13_bridge_01")) t.Skip("no hcl13_bridge_01 in the manifest");
        foreach (var (ground, blocks) in new[] { (17.0, false), (20.5, true), (23.0, true), (23.6, false), (25.0, false) })
        {
            var map = new FlatMap(256) { HeightFn = (x, z) => ground };
            map.Blocked.Add((100, 100, 120, 120));                 // (a walk grid: an original map)
            var W = new W_(R, M, map, new WorldOptions { Seed = 5 });
            W.AddProp(new PropSpec { Model = "hcl13_bridge_01", X = 0, Z = 0, Y = 24, Rot = 0, Block = true });
            t.Equal(blocks, !W.Nav.IsFree(0, 0), $"ground {ground} m under a deck at 24 m: blocked");
        }
        // the remake's own maps (no walk grid): bridges block nothing (no circle either)
        var flat = new W_(R, M, new FlatMap(256) { HeightFn = (x, z) => 23 }, new WorldOptions { Seed = 5 });
        flat.AddProp(new PropSpec { Model = "hcl13_bridge_01", X = 0, Z = 0, Y = 24, Rot = 0, Block = true });
        t.True(flat.Nav.IsFree(0, 0), "no walk grid: the bridge blocks nothing");
    }

    /// <summary>bridges a mission places itself block their pathfinder boxes as the map's own bridges do (CDecoColObj
    /// AddPFBlocker): the pirate town's footbridges of mission 6, the river bridges of mission 7 (Campaign.NO_BLOCK no
    /// longer names bridges)</summary>
    public static void CampaignBridgesBlockTheirBoxes(TestContext t)
    {
        foreach (var (mission, model) in new[] { (6, "pt_footbridge_straight_16m"), (7, "sav_bridge_calm_in") })
        {
            var s = CampaignTests.Session(t, "base", mission);
            var props = s.World.Props.Where(p => p.Alive && p.Model == model).ToList();
            t.True(props.Count > 0, $"mission {mission}: {model} placed ({props.Count})");
            t.True(props.All(p => p.Cells.Count > 0 && p.Blocking), $"mission {mission}: every {model} blocks its pathfinder boxes ({props.Count(p => p.Blocking)} of {props.Count})");
        }
    }

    /// <summary>
    /// MIRAGE / base mission 13: the Dustriders' team_citydefender, the SEAS outpost 1 cleared, walks back as the mission's
    /// CBL_Ga05 sends it (Aggressive Walk to 820.2, 350.1) - past the two city bridges over the strip of ground between
    /// them. No unit on a blocked cell (the ravine, the rocks), none just under a deck, all back. Run with the test data
    /// (the city bridge has no drawn box there: no shade) and with a box given to it.
    /// </summary>
    public static void Mission13AlliesComeBack(TestContext t)
    {
        foreach (var cfg in new[] { "mirage", "base" })
        foreach (var withBox in new[] { false, true })
        {
            var c = CampaignTests.LoadCfgPublic(cfg);
            if (c == null) { t.Skip($"no {cfg}"); return; }
            var models = withBox ? ManifestWithBridgeBox(c.Data, BRIDGE_BOX) : c.Models;
            var start = new GameStart { Configuration = cfg, Mission = 13, Seed = 1, Difficulty = 1 };
            if (MissionSession.MissionFolderOf(start, c.Config) == null) { t.Skip($"mission 13 not in {cfg}"); return; }
            var s = MissionSession.Create(start, c.Config, new SessionOptions { Rules = c.Rules, Models = models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoBrains = true });
            s.Run(2);
            var W = s.World; var C = s.Campaign; var nav = W.Nav;
            string w = $"{cfg} 13{(withBox ? " (bridge box)" : "")}";
            var bridges = W.Props.Where(p => p.Model == "hcl13_bridge_01").ToList();
            var decks = s.Map.Decor.Where(d => d.Model == "hcl13_bridge_01").ToList();
            t.Equal(2, bridges.Count + decks.Count, $"{w}: the two city bridges");
            // just under a deck: inside its box (turned as the bridge) on ground 0.5 .. 4 m below it
            bool Under(double x, double z)
            {
                if (!withBox) return false;
                foreach (var d in decks)
                {
                    double c = SMath.Cos(d.Rot), sn = SMath.Sin(d.Rot), dx = x - d.X, dz = z - d.Z;
                    double lx = dx * c - dz * sn, lz = dx * sn + dz * c, below = d.Y!.Value - W.Height(x, z);
                    if (lx >= BRIDGE_BOX[0] && lx <= BRIDGE_BOX[3] && lz >= BRIDGE_BOX[2] && lz <= BRIDGE_BOX[5] && below > Bridges.UNDER_MIN && below < Bridges.UNDER_MAX) return true;
                }
                return false;
            }
            foreach (var e in W.EntitiesOf(C.Players[2]!)) W.Kill(e, null);
            var team = W.Units.Where(u => u.Alive && !u.Parked && u.Owner == C.Players[6]).ToList();
            t.True(team.Count >= 12, $"{w}: the Dustriders' team ({team.Count})");
            var (cx, cz) = C.ToGame(548.5, 474.7);                   // CBL_Ga03: where the team went
            for (int i = 0; i < team.Count; i++) W.Teleport(team[i], cx + (i % 6) * 2.5 - 6, cz + (i / 6) * 2.5 - 6);
            s.Run(1);
            var (tx, tz) = C.ToGame(820.2, 350.1);                   // CBL_Ga05: back
            foreach (var u in team) W.Order(u, new Order("attackmove") { X = tx, Z = tz });
            int blocked = 0, under = 0; string first = "";
            for (int step = 0; step < 150 * 10; step++)
            {
                s.Run(0.1);
                foreach (var u in team)
                {
                    if (!u.Alive || u.Inside) continue;
                    int k = nav.Idx(u.Pos.X, u.Pos.Z);
                    if (nav.Block[k] != 0) { blocked++; if (first.Length == 0) first = $"{u.Name} at ({u.Pos.X:0.#}, {u.Pos.Z:0.#})"; }
                    if (Under(u.Pos.X, u.Pos.Z)) under++;
                }
            }
            t.Equal(0, blocked, $"{w}: unit-steps on blocked cells{(first.Length > 0 ? " - first " + first : "")}");
            t.Equal(0, under, $"{w}: unit-steps just under a bridge deck");
            int back = team.Count(u => u.Alive && Math.Sqrt((u.Pos.X - tx) * (u.Pos.X - tx) + (u.Pos.Z - tz) * (u.Pos.Z - tz)) < 40);
            t.True(back >= team.Count(u => u.Alive) * 0.9, $"{w}: back at the post ({back} of {team.Count(u => u.Alive)})");
        }
    }
}
