// Scenario tests of walls (src/Sim/Systems/WallMap.cs, Construction.cs PlaceWall, Buildings.cs gates, WallWalk.cs,
// Siege.cs): a wall line placed as the server does and built by workers piece after piece (the queued tiles), the
// pieces' parts and pathfinder boxes, a gate replacing a piece (its wings, its cells for the pathfinder, AUTO opening
// for the owner's units), a tower on a piece, a destroyed piece, characters walking on a walkable wall and coming down,
// a ladder against a wall (MIRAGE); a destroyed gate's gap taking a new gate - one-tile gates of every tribe, the
// campaign maps' two-tile gates (WallMap.GapAt; docs/SIMULATION.md "Walls and gates").
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys2WallTests
{
    static void Rich(Player p) { p.CapsFixed = true; foreach (var r in Rules.RES) { p.Caps[r] = 20000; p[r] = 10000; } }

    /// <summary>an action the player may use now - in debug mode if its buildings are missing</summary>
    static ActionDef Allowed(TestContext t, W_ w, Player p, string id)
    {
        var a = Sys2.Action(w, p, id, "Build");
        if (a == null || w.Data.Check(p, a) == "hidden") { w.RaiseEpoch(p, 5); a = Sys2.Action(w, p, id, "Build"); }
        if (a == null) t.Skip("no action " + id);
        if (w.Data.Check(p, a!) != null) p.Debug = true;
        t.Equal(null, w.Data.Check(p, a!), id + " allowed");
        return a!;
    }

    /// <summary>a finished wall line put down as a map does (CalcWall's tiles, PlaceBuilding)</summary>
    static int PlaceLine(W_ w, Player owner, string name, double x0, double z0, double x1, double z1)
    {
        var c = w.Walls!.CalcWall(owner, name, x0, z0, x1, z1);
        if (c == null) return 0;
        foreach (var x in c.Tiles) { var (cx, cz) = w.Walls.Centre(x.I, x.J); w.PlaceBuilding(name, owner, cx, cz); }
        return c.Tiles.Count;
    }

    static List<Building> Pieces(W_ w, string name) => w.Buildings.Where(b => b.Alive && b.Name == name).ToList();

    public static void WallLineBuiltByWorkers(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p);
        var a = Allowed(t, w, p, "hu_palisade");
        bool debug = p.Debug; p.Debug = false;
        var line = w.WallLineOf(p, a, -36, -20, 36, -20);
        t.True(line != null && line.Count == 10, $"a line of 10 tiles ({line?.Count})");
        t.True(line!.All(x => x.State == "ok" || debug), "all payable");
        var workers = new List<Unit> { w.SpawnUnit("hu_worker", p, -40, -10)!, w.SpawnUnit("hu_worker", p, -40, -14)! };
        p.Debug = debug;
        var before = p.Res.ToDictionary(kv => kv.Key, kv => kv.Value);
        int n = w.PlaceWall(p, a, line.Select(x => (x.I, x.J, x.X, x.Z, x.State == "have")).ToList(), workers, false);
        t.Equal(10, n, "ten pieces placed");
        if (!p.Debug) foreach (var r in Rules.RES) t.Equal(before[r] - 10 * a.Cost[r], p[r], $"{r}: ten pieces paid");
        var pieces = Pieces(w, "hu_palisade");
        t.Equal(10, pieces.Count, "ten sites");
        if (!p.Debug)
        {
            t.True(workers.All(u => u.Task.Type == "build" && u.BuildQueue != null && u.BuildQueue.Count == 9), "every builder gets the first and the rest queued");
            Sys2.Run(w, 400);
        }
        t.True(pieces.All(b => b.Built), $"all built ({pieces.Count(b => b.Built)})");
        // the pieces: the ends lone, the middle straight (x1 hub, arms east and west)
        var mid = pieces.OrderBy(b => b.Pos.X).ElementAt(5);
        t.True(mid.WallParts != null && mid.WallParts[0] == "x1", "a straight piece: " + string.Join(".", mid.WallParts ?? new()));
        t.True((mid.ArmMask & 1) != 0 && (mid.ArmMask & 16) != 0, "arms east and west " + mid.ArmMask);
        t.True(mid.Cells.Any(k => w.Nav.Block[k] != 0), "it blocks the pathfinder");
        // a wall across the way: a unit walks round it
        var u2 = w.SpawnUnit("hu_warrior", p, 0, -40)!;
        w.Order(u2, Order.Move(0, 0));
        t.True(u2.Path.Count > 0 && Math.Abs(u2.Path.Max(q => Math.Abs(q.X))) > 30, "the way goes round the wall");
    }

    public static void GateTowerAndRemoval(TestContext t)
    {
        var (w, p, e) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p); p.Debug = true;
        var wall = Allowed(t, w, p, "hu_palisade");
        var line = w.WallLineOf(p, wall, -36, -20, 36, -20)!;
        w.PlaceWall(p, wall, line.Select(x => (x.I, x.J, x.X, x.Z, false)).ToList(), new List<Unit>(), false);
        var WM = w.Walls!;
        var (gi, gj) = WM.TileOf(4, -20);
        var old = WM.At(gi, gj)!.Wall!;
        // the gate: on the line's tile, it takes the piece's place, its neighbours become its wings
        var ga = Allowed(t, w, p, "hu_palisade_gate");
        var pl = w.PlacementOf(ga.Results[0].Obj, 4, -20, 0, p);
        t.True(pl.Ok && pl.Fit != null && pl.Fit.Wall == old, "a gate fits on the line, replacing the piece");
        var gate = w.StartConstruction(p, ga, 4, -20, 0, new List<Unit>()) as Building;
        t.True(gate != null && gate.Built, "the gate stands");
        t.True(!old.Alive && old.SilentDeath, "the piece it replaced is gone (no ruin)");
        t.Equal(2, gate!.Wings!.Count, "two wings");
        t.True(gate.Wings.All(x => x.ParentGate == gate), "the wings know their gate");
        t.True(gate.Cells.All(k => w.Nav.GateAt.TryGetValue(k, out var g) && g == gate), "its cells ask the gate (nav.GateBlocks)");
        t.Equal(W_.GATE.AUTO, gate.GateState, "AUTO");
        // AUTO: opens for the owner's unit near it, never for the enemy's
        var friend = w.SpawnUnit("hu_warrior", p, gate.Pos.X, gate.Pos.Z - 3)!;
        Sys2.Run(w, 0.2);
        t.True(gate.GateOpen, "opens for its owner's unit");
        friend.Pos.X = 60; friend.Pos.Z = 60;
        Sys2.Run(w, 9);
        t.True(!gate.GateOpen, "closes 8 s after the last one");
        var foe = w.SpawnUnit("aje_warrior", e, gate.Pos.X, gate.Pos.Z - 3)!;
        Sys2.Run(w, 0.5);
        t.True(!gate.GateOpen, "not for the enemy");
        t.True(w.Nav.GateBlocks(gate.Cells[0], e) && !w.Nav.GateBlocks(gate.Cells[0], p), "the pathfinder: shut for the enemy");
        w.SetGate(gate, W_.GATE.CLOSED);
        t.True(w.Nav.GateBlocks(gate.Cells[0], p), "CLOSED: shut for everybody");
        // a tower on a piece of the line: the piece stays
        var ta = Allowed(t, w, p, "hu_small_tower");
        var under = WM.At(WM.TileOf(-28, -20).I, WM.TileOf(-28, -20).J)!.Wall!;
        var tower = w.StartConstruction(p, ta, -28, -20, 0, new List<Unit>()) as Building;
        t.True(tower != null && under.Alive, "a tower on a piece; the piece stays");
        t.True(WM.HasOwn(under.Tile!.Value.I, under.Tile.Value.J, WM.OwnerId(p), 0, WallMap.WT.TOWER), "a tower in the grid");
        // a piece destroyed: its neighbours lose their arm towards it
        var victim = WM.At(WM.TileOf(-12, -20).I, WM.TileOf(-12, -20).J)!.Wall!;
        var (vi, vj) = victim.Tile!.Value;
        var left = WM.At(vi - 1, vj)!.Wall!;
        t.True((left.ArmMask & 1) != 0, "the left neighbour has its arm east");
        w.KillBuilding(victim, null);
        t.True(WM.At(vi, vj) == null || WM.At(vi, vj)!.Wall == null, "off the grid");
        t.True((left.ArmMask & 1) == 0, "the neighbour's arm is gone");
        t.True(victim.Ruin == null, "no ruin for a wall piece");
    }

    /// <summary>The wall placer's preview (World.WallPreviewOf, PreviewWall): every tile of the line with the parts it will
    /// show (a ghost without parts showed nothing), the own pieces next to it with their future arms, the wall map
    /// untouched afterwards; and the placement grid's dots for wall-map objects (World.PlaceDotOk): an own wall piece
    /// is a valid spot for a tower and a gate, though its footprint is not free.</summary>
    public static void WallPreviewAndDots(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p); p.Debug = true;
        var a = Allowed(t, w, p, "hu_palisade");
        var WM = w.Walls!;
        var line = w.WallLineOf(p, a, -36, -20, 36, -20);
        t.True(line != null && line.Count == 10, "a line of 10 tiles");
        var ghosts = w.WallPreviewOf(p, line);
        t.Equal(10, ghosts.Count, "a ghost per tile (no own pieces next to it yet)");
        t.True(ghosts.All(g => g.Parts != null && g.Parts.Count > 0), "every ghost has its parts");
        t.True(ghosts.All(g => g.State == "ok" || g.State == "gate"), "all green / cyan");
        t.True(line!.All(x => WM.Word(x.I, x.J) == WM.Word(1000, 1000)), "the wall map is untouched");
        // the ends differ from the middle (an end piece has one arm, a middle piece two)
        t.True(string.Join(",", ghosts[0].Parts!) != string.Join(",", ghosts[4].Parts!), "end and middle pieces differ");
        t.Equal(0, w.WallPreviewOf(p, null).Count, "no line: nothing");
        // the line placed (finished): a new line from its east end shows that piece too, with its arm towards the new tile
        w.PlaceWall(p, a, line!.Select(x => (x.I, x.J, x.X, x.Z, false)).ToList(), new List<Unit>(), false);
        var last = line[^1];
        var end = WM.At(last.I, last.J)!.Wall!;
        var line2 = w.WallLineOf(p, a, last.X + 8, last.Z, last.X + 24, last.Z);
        t.True(line2 != null && line2.Count >= 2, "a second line next to the first");
        var g2 = w.WallPreviewOf(p, line2);
        var nb = g2.FirstOrDefault(g => g.I == last.I && g.J == last.J);
        t.True(nb != null && nb.Name == "hu_palisade", "the own piece next to the line is shown");
        t.True(nb!.Parts != null && string.Join(",", nb.Parts) != string.Join(",", end.WallParts ?? new List<string>()), "with the arm it will get");
        t.Equal(string.Join(",", WM.PartsOf(last.I, last.J)), string.Join(",", end.WallParts ?? new List<string>()), "and the map is unchanged");
        // the dots: a tower on an own piece is fine (the footprint check says no), a gate on the line too, not out in the open
        var ta = Allowed(t, w, p, "hu_small_tower");
        var (mx, mz) = WM.Centre(line[3].I, line[3].J);
        t.True(w.PlaceSpotOk(ta.Results[0].Obj, mx + 1, mz - 1, 0, p), "a tower dot over an own wall piece is green");
        t.True(!w.CanPlace(ta.Results[0].Obj, mx, mz, 0, p), "(the footprint check alone calls it blocked)");
        var ga = Allowed(t, w, p, "hu_palisade_gate");
        t.True(w.PlaceSpotOk(ga.Results[0].Obj, mx, mz, 0, p), "a gate dot on the line is green");
        t.True(!w.PlaceSpotOk(ga.Results[0].Obj, mx, mz + 40, 0, p), "a gate dot in the open is red");
    }

    /// <summary>Every tribe's wall-mounted buildings in both configurations (base, MIRAGE): a line of the tribe's first
    /// wall, then each tower / gate of the tribe - the placement grid's dots over the line's middle piece are green
    /// (World.PlaceSpotOk: the wall placer, not the footprint check, which the wall blocks), and the placement there is
    /// the same answer.</summary>
    public static void EveryTribesWallMountedBuildings(TestContext t)
    {
        int n = 0;
        foreach (var (data, tribes) in new[] { (Sys2.Base, new[] { "Hu", "Aje", "Ninigi" }), (Sys2.Mirage, new[] { "Hu", "Aje", "Ninigi", "SEAS" }) })
        {
            if (!System.IO.Directory.Exists(data)) continue;
            foreach (var tribe in tribes)
            {
                var (w, p, _) = Sys2.World(t, data, tribe, tribe == "Aje" ? "Hu" : "Aje");
                Rich(p); p.Debug = true;
                w.RaiseEpoch(p, 5);
                var acts = w.Data.Actions(p).Where(a => a.Kind == "Build" && a.Results.Count > 0).ToList();
                var wall = acts.FirstOrDefault(a => w.Data.Def(a.Results[0].Obj, p)?.WallKind == "wall" && w.Models.Has(w.Data.Stats(a.Results[0].Obj, 1, p)?.Gfx));
                if (wall == null) { t.Log($"{data} {tribe}: no wall with model data"); continue; }
                var line = w.WallLineOf(p, wall, -36, -20, 36, -20);
                t.True(line != null && line.Count == 10, $"{tribe}: a line of {wall.Results[0].Obj}");
                w.PlaceWall(p, wall, line!.Select(x => (x.I, x.J, x.X, x.Z, false)).ToList(), new List<Unit>(), false);
                var WM = w.Walls!;
                var (mx, mz) = WM.Centre(line[4].I, line[4].J);
                foreach (var a in acts)
                {
                    var name = a.Results[0].Obj;
                    var def = w.Data.Def(name, p);
                    if (def?.WallKind is not ("tower" or "gate") || !w.Models.Has(w.Data.Stats(name, 1, p)?.Gfx)) continue;
                    // (the tribe's own; mission classes such as hcl13_gate - no gate script - are no player buildings)
                    if (!name.StartsWith(tribe.ToLowerInvariant() + "_")) continue;
                    var pl = w.PlacementOf(name, mx, mz, 0, p);
                    var dots = w.PlaceGridOf(name, mx, mz, 0, p, 8);
                    var onPiece = dots.Where(d => WM.TileOf(d.X, d.Z) == (line[4].I, line[4].J)).ToList();
                    t.True(onPiece.Count > 0 && onPiece.All(d => d.Ok == pl.Ok), $"{tribe} {name}: the dots over the piece say what the placement says ({pl.Ok})");
                    t.True(pl.Ok, $"{tribe} {name} ({def.WallKind}): may stand on the own wall piece");
                    t.Log($"{(data == Sys2.Base ? "base" : "mirage")} {tribe} {name} on {wall.Results[0].Obj}: ok");
                    n++;
                }
            }
        }
        if (n == 0) t.Skip("no wall-mounted buildings with model data");
        t.Log($"{n} wall-mounted buildings checked");
    }

    public static void WallWalk(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p); p.Debug = true;
        t.True(PlaceLine(w, p, "hu_small_wall", -36, 20, 36, 20) >= 8, "a line of hu_small_wall");
        var pieces = Pieces(w, "hu_small_wall").OrderBy(b => b.Pos.X).ToList();
        t.True(pieces.Count >= 8 && pieces.All(b => w.WalkableWall(b)), "a walkable wall");
        var u = w.SpawnUnit("hu_warrior", p, -20, 30)!;
        w.Order(u, new Order("use") { Target = pieces[2] });
        t.Equal("wall", u.Task.Type, "sent up");
        Sys2.Run(w, 15);
        t.True(u.OnWall != null && u.WallTile != null, "on the walkway");
        t.Near(w.WallTop(u.WallTile!.Value.I, u.WallTile.Value.J), u.Pos.Y, 0.5, "at the walkway's height");
        t.True(u.WallDef == w.Data.MiscValue(p, "Defence_On_Wall", 0), "the wall's protection " + u.WallDef);
        // along the walkway to another piece
        w.Order(u, new Order("use") { Target = pieces[^2] });
        Sys2.Run(w, 20);
        t.True(u.WallTile == pieces[^2].Tile, "walked along to the other piece");
        // an order to the ground: down first
        w.Order(u, Order.Move(20, 40));
        Sys2.Run(w, 20);
        t.True(u.OnWall == null && u.WallTile == null && u.WallDef == 0, "down again");
        t.True(Math.Sqrt((u.Pos.X - 20) * (u.Pos.X - 20) + (u.Pos.Z - 40) * (u.Pos.Z - 40)) < 2, "and at the goal");
        // the piece under a unit destroyed: it falls
        w.Order(u, new Order("use") { Target = pieces[4] });
        Sys2.Run(w, 20);
        t.True(u.WallTile != null, "up again");
        var under = w.Walls!.At(u.WallTile!.Value.I, u.WallTile.Value.J)!.Wall!;
        w.KillBuilding(under, null);
        Sys2.Run(w, 2);
        t.True(u.OnWall == null && u.WallTile == null && u.WJump == null, "fallen down");
        t.True(Math.Abs(u.Pos.Y - w.Height(u.Pos.X, u.Pos.Z)) < 0.01, "on the ground");
    }

    public static void LadderMirage(TestContext t)
    {
        var (w, p, e) = Sys2.World(t, Sys2.Mirage, "Hu");
        Rich(p); p.Debug = true;
        var lad = w.Data.Index.Keys.FirstOrDefault(n => w.Data.Def(n, p) is { Ladder: true });
        if (lad == null) t.Skip("no ladder class");
        // the enemy's wall
        e.Debug = true; foreach (var r in Rules.RES) e[r] = 10000;
        int nl = PlaceLine(w, e, "aje_clay_wall", -20, 0, 20, 0);
        var WM = w.Walls!; var (li, lj) = WM.TileOf(4, 0);
        t.True(nl > 0 && WM.At(li, lj)?.Wall != null, $"the enemy's wall ({nl} tiles)");
        var pl = w.PlacementOf(lad!, 4, 0, 0, p, (4, -20));
        t.True(pl.Ok && pl.Dock != null, $"{lad}: against the wall ({pl.X:0.0}, {pl.Z:0.0})");
        t.True(pl.Z < 0, "on the workers' side");
        // (the ladder's action needs MIRAGE's inventions: put it down as construction.js does with the placement)
        var b = w.PlaceBuilding(lad!, p, pl.X, pl.Z, pl.Rot, true);
        t.True(b != null, "the ladder placed");
        w.LadderPlaced(b!, pl.Dock!.Value);
        t.True(b!.DockTile == pl.Dock && w.SiegeAt(pl.Dock!.Value.I, pl.Dock.Value.J) == b, "it holds its piece");
        t.True(w.LaddersAt(pl.Dock.Value.I, pl.Dock.Value.J).Contains(b!), "an entrance of the wall");
        var u = w.SpawnUnit("hu_warrior", p, 4, -15)!;
        t.True(w.ClimbSiegeOrder(u, b), "climbs it");
        Sys2.Run(w, 15);
        t.True(u.Pos.Z > 0 || u.WallTile != null, $"over the wall or on it ({u.Pos.X:0.0}, {u.Pos.Z:0.0})");
    }

    // ------------------------------------------------------------------------------------------ rebuilding a gate
    /// <summary>the gate action of the player's own tribe (the class of its first wall)</summary>
    static ActionDef? GateAction(W_ w, Player p) => w.Data.Actions(p).FirstOrDefault(a => a.Kind == "Build" && a.Results.Count > 0
        && w.Data.Def(a.Results[0].Obj, p)?.WallKind == "gate" && a.Results[0].Obj.StartsWith(p.Tribe.ToLowerInvariant() + "_") && w.Models.Has(w.Data.Stats(a.Results[0].Obj, 1, p)?.Gfx));

    /// <summary>an enemy unit's way from one side of the line to the other: through (x, z) or round the line's end</summary>
    static bool PathCrosses(W_ w, Player e, double x, double z, double reach)
    {
        var u = w.SpawnUnit(e.Tribe == "Aje" ? "aje_warrior" : "hu_warrior", e, x, z - 16)!;
        w.Order(u, Order.Move(x, z + 16));
        bool through = u.Path.Count > 0 && u.Path.Max(q => Math.Abs(q.X - x)) < reach;
        w.RemoveEntity(u);
        return through;
    }

    /// <summary>A one-tile gate (NewGates, walls_engine.md §5.1) destroyed - straight and diagonal, every tribe of both
    /// configurations: its tile is a gap with the wings on both sides, a gate of the tribe goes back in (snapped to the
    /// tile, turned along the line, the wings linked again) and closes the line for the enemy; the gate elsewhere keeps
    /// the old rule (not in the open, not at a line's end).</summary>
    public static void GateRebuiltInGap(TestContext t)
    {
        int n = 0;
        foreach (var (data, tribes) in new[] { (Sys2.Base, new[] { "Hu", "Aje", "Ninigi" }), (Sys2.Mirage, new[] { "Hu", "Aje", "Ninigi", "SEAS" }) })
        {
            if (!System.IO.Directory.Exists(data)) continue;
            foreach (var tribe in tribes)
                foreach (var diag in new[] { false, true })
                {
                    var (w, p, e) = Sys2.World(t, data, tribe, tribe == "Aje" ? "Hu" : "Aje");
                    Rich(p); p.Debug = true; w.RaiseEpoch(p, 5);
                    var wall = w.Data.Actions(p).FirstOrDefault(a => a.Kind == "Build" && a.Results.Count > 0 && w.Data.Def(a.Results[0].Obj, p)?.WallKind == "wall" && w.Models.Has(w.Data.Stats(a.Results[0].Obj, 1, p)?.Gfx));
                    var ga = GateAction(w, p);
                    if (wall == null || ga == null) { t.Log($"{data} {tribe}: no wall / gate with model data"); continue; }
                    string what = $"{(data == Sys2.Base ? "base" : "mirage")} {tribe} {ga.Results[0].Obj} {(diag ? "diagonal" : "straight")}";
                    var line = diag ? w.WallLineOf(p, wall, -36, -36, 36, 36)! : w.WallLineOf(p, wall, -36, -20, 36, -20)!;
                    w.PlaceWall(p, wall, line.Select(x => (x.I, x.J, x.X, x.Z, false)).ToList(), new List<Unit>(), false);
                    var WM = w.Walls!;
                    var mid = line[line.Count / 2];
                    var gate = w.StartConstruction(p, ga, mid.X, mid.Z, 0, new List<Unit>()) as Building;
                    t.True(gate != null && gate.Built && gate.Wings!.Count == 2, $"{what}: the gate stands with its wings");
                    var wings = gate!.Wings!.ToList();
                    int d = gate.GateD!.Value;
                    // the wing passes its damage to the gate (B:2266)
                    double gh = gate.Hp, wh = wings[0].Hp;
                    w.Damage(wings[0], 100, null);
                    t.True(wings[0].Hp == wh && gate.Hp < gh, $"{what}: a hit on a wing goes to the gate");
                    w.Damage(gate, 1e7, null);
                    Sys2.Run(w, 1);
                    t.True(!gate.Alive && wings.All(x => x.Alive && x.ParentGate == null), $"{what}: the gate destroyed, the wings stand, released");
                    t.True(WM.At(mid.I, mid.J) == null || WM.At(mid.I, mid.J)!.Gate == null, $"{what}: the gap");
                    t.True(PathCrosses(w, e, mid.X, mid.Z, 20) || diag, $"{what}: the enemy walks through the gap");
                    // a new gate: the cursor anywhere on the gap tile -> the tile centre, along the line
                    var pl = w.PlacementOf(ga.Results[0].Obj, mid.X + 2.5, mid.Z - 1.5, 1.0, p);
                    t.True(pl.Ok && pl.X == mid.X && pl.Z == mid.Z && pl.Fit != null && pl.Fit.D % 4 == d % 4, $"{what}: a gate fits into the gap, snapped ({pl.X}, {pl.Z}, d {pl.Fit?.D})");
                    t.True(w.PlaceSpotOk(ga.Results[0].Obj, mid.X, mid.Z, 0, p), $"{what}: the dots over the gap are green");
                    var g2 = w.StartConstruction(p, ga, mid.X + 2.5, mid.Z - 1.5, 1.0, new List<Unit>()) as Building;
                    t.True(g2 != null && g2.Built && g2.Tile == (mid.I, mid.J), $"{what}: the new gate in the gap");
                    t.True(g2!.Wings!.Count == 2 && g2.Wings.All(x => wings.Contains(x) && x.ParentGate == g2), $"{what}: the same wall pieces are its wings again");
                    t.True(g2.Cells.Count > 0 && g2.Cells.All(k => w.Nav.GateAt.TryGetValue(k, out var gg) && gg == g2), $"{what}: its cells ask the gate");
                    w.SetGate(g2, W_.GATE.CLOSED);
                    t.True(g2.Cells.All(k => w.Nav.GateBlocks(k, e) && w.Nav.GateBlocks(k, p)), $"{what}: closed - shut for everybody");
                    if (!diag) t.True(!PathCrosses(w, e, mid.X, mid.Z, 20), $"{what}: the line is closed again (the enemy goes round)");
                    w.SetGate(g2, W_.GATE.AUTO);
                    t.True(!w.Nav.GateBlocks(g2.Cells[0], p) && w.Nav.GateBlocks(g2.Cells[0], e), $"{what}: AUTO - open for the owner only");
                    // the old rule elsewhere: not in the open, not at the end of a line, not on the wing next to the gate
                    t.True(!w.PlacementOf(ga.Results[0].Obj, mid.X, mid.Z + 48, 0, p).Ok, $"{what}: no gate in the open");
                    t.True(!w.PlacementOf(ga.Results[0].Obj, line[0].X, line[0].Z, 0, p).Ok, $"{what}: no gate at the line's end");
                    t.True(!w.PlacementOf(ga.Results[0].Obj, wings[0].Pos.X, wings[0].Pos.Z, 0, p).Ok, $"{what}: no gate on the wing (a gate next to it)");
                    n++;
                }
        }
        if (n == 0) t.Skip("no walls with model data");
    }

    /// <summary>A two-tile gate of a campaign map (the old gate mode: centred on the tile edge, the wall ends 12 m from
    /// its centre - walls.md §5.3) destroyed: the two-tile gap it leaves takes a new gate of the tribe exactly where the
    /// old one stood (WallMap.GapAt; the one-tile rule alone - wall pieces right next to the gate's tile - never fits
    /// it); the gate closes the line again. A two-tile gap that never had a gate keeps the original rule (no gate), a wall
    /// piece put into the old gate's gap makes it an ordinary line (a gate on the other tile, by the one-tile rule).</summary>
    public static void OldMapGateRebuilt(TestContext t)
    {
        var (w, p, e) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(p); p.Debug = true;
        var wall = Allowed(t, w, p, "hu_palisade");
        var ga = Allowed(t, w, p, "hu_palisade_gate");
        var WM = w.Walls!;
        // two lines with a two-tile gap each: tiles x 4 and 12 (gate at x 8, z -20) and x 4, 12 at z 60 (no gate)
        foreach (var z in new[] { -20.0, 60.0 })
        {
            var line = w.WallLineOf(p, wall, -36, z, 36, z)!;
            w.PlaceWall(p, wall, line.Where(x => x.X != 4 && x.X != 12).Select(x => (x.I, x.J, x.X, x.Z, false)).ToList(), new List<Unit>(), false);
        }
        var map = w.PlaceBuilding("hu_palisade_gate", p, 8, -20, 0)!;           // as a map puts it down
        t.True(map.Tile2 != null && map.GateD % 4 == 3, $"an old two-tile gate ({map.Tile} {map.Tile2}, d {map.GateD})");
        var (ai, aj) = map.Tile!.Value; var (bi, bj) = map.Tile2!.Value;
        t.True(!w.PlacementOf("hu_palisade_gate", 4, 60, 0, p).Ok && !w.PlacementOf("hu_palisade_gate", 12, 60, 0, p).Ok, "a two-tile gap without a gate: no gate (NewGates)");
        w.Damage(map, 1e7, null);
        Sys2.Run(w, 1);
        t.True(!map.Alive && (WM.Word(ai, aj) & 0x8d80) == 0 && (WM.Word(bi, bj) & 0x8d80) == 0, "the gate's tiles are empty");
        t.True(WM.GapAt(ai, aj) != null && WM.GapAt(bi, bj) != null, "the gap is remembered on both tiles");
        t.True(PathCrosses(w, e, 8, -20, 20), "the enemy walks through the gap");
        foreach (var (cx, cz) in new[] { (4.0, -20.0), (12.0, -20.0), (9.5, -18.0) })
        {
            var pl = w.PlacementOf("hu_palisade_gate", cx, cz, 0.7, p);
            t.True(pl.Ok && Math.Abs(pl.X - 8) < 1e-9 && Math.Abs(pl.Z + 20) < 1e-9 && Math.Abs(pl.Rot - map.Rot) < 1e-9, $"cursor ({cx}, {cz}): the gate goes where the map's gate stood ({pl.Ok} {pl.X}, {pl.Z})");
        }
        var g = w.StartConstruction(p, ga, 12, -20, 0.7, new List<Unit>()) as Building;
        t.True(g != null && g.Built && g.Tile == (ai, aj) && g.Tile2 == (bi, bj), "the new gate takes both tiles");
        t.True(WM.GapAt(ai, aj) == null, "the gap is closed");
        var endL = WM.At(ai - 1, aj)!.Wall!; var endR = WM.At(bi + 1, bj)!.Wall!;
        t.True((endL.ArmMask & 1) != 0 && (endR.ArmMask & 16) != 0, $"the wall ends have their arms into the gate ({endL.ArmMask}, {endR.ArmMask})");
        w.SetGate(g!, W_.GATE.CLOSED);
        t.True(!PathCrosses(w, e, 8, -20, 20), "closed: the line holds again");
        // destroyed again: the gap again; a wall piece put into it: an ordinary line, the one-tile rule for the other tile
        w.Damage(g!, 1e7, null);
        Sys2.Run(w, 1);
        t.True(WM.GapAt(ai, aj) != null, "destroyed again: the gap again");
        var piece = w.PlacementOf("hu_palisade", 4, -20, 0, p);
        t.True(piece.Ok, "a wall piece fits into the gap");
        w.PlaceWall(p, wall, new List<(int, int, double, double, bool)> { (ai, aj, 4, -20, false) }, new List<Unit>(), false);
        t.True(WM.GapAt(bi, bj) == null, "with a piece in it the gap is no gate gap any more");
        var pl2 = w.PlacementOf("hu_palisade_gate", 12, -20, 0, p);
        t.True(pl2.Ok && pl2.X == 12 && pl2.Z == -20 && pl2.Fit!.Wings.Count == 2, "the one-tile gate on the other tile, between two pieces");
    }

    /// <summary>The campaign maps' own gates destroyed and rebuilt by the player: mission 13 (Ninigi, the player's
    /// skewer gates at the city wall) and mission 1 as SEAS on MIRAGE (the village's gates given to the player become
    /// SEAS gates - the case of the bug report). Every own two-tile gate of the player: destroyed, a gate of the
    /// player's tribe placed into its gap where it stood.</summary>
    public static void CampaignGatesRebuilt(TestContext t)
    {
        int n = 0;
        foreach (var (cfg, mission, tribe) in new[] { ("base", 13, ""), ("mirage", 1, "SEAS"), ("mirage", 13, "") })
        {
            if (CampaignTests.LoadCfgPublic(cfg) == null) { t.Log($"{cfg}: no data"); continue; }
            var s = CampaignTests.Session(t, cfg, mission, tribe);
            var W = s.World; var C = s.Campaign; var me = C.Human!;
            var WM = W.Walls!;
            // the village's gates and walls given to the player (mission 1: the base is handed over later)
            var gates0 = W.Buildings.Where(b => b.Alive && b.Def!.WallKind == "gate" && b.Tile2 != null && System.Text.RegularExpressions.Regex.IsMatch(b.Def.Script ?? "", "^CGate|Gate$")).ToList();
            if (!gates0.Any(b => b.Owner == me))
            {
                var give = gates0.Where(b => b.Owner != null && !b.Owner.Ai).Take(2).ToList();
                if (give.Count == 0) give = gates0.Take(2).ToList();
                var near = W.Buildings.Where(b => b.Alive && b.Def!.WallKind is "wall" or "gate" && give.Any(g => Math.Abs(g.Pos.X - b.Pos.X) < 40 && Math.Abs(g.Pos.Z - b.Pos.Z) < 40)).ToList();
                s.Host.GiftBatch(true);
                foreach (var b in near) if (b.Rec is { Alive: true } r) s.Host.SetOwner(r.Id, 0, 0);
                s.Host.GiftBatch(false);
            }
            var mine = W.Buildings.Where(b => b.Alive && b.Owner == me && b.Def!.WallKind == "gate" && b.Tile2 != null).ToList();
            t.True(mine.Count > 0, $"{cfg} {mission}: the player owns old map gates ({mine.Count}: {string.Join(", ", mine.Select(b => b.Name).Distinct())})");
            me.Debug = true; Rich(me);
            var ga = GateAction(W, me);
            if (ga == null) { t.Log($"{cfg} {mission}: no gate action for {me.Tribe}"); continue; }
            foreach (var old in mine)
            {
                var (ai, aj) = old.Tile!.Value; var (t1, t2) = (old.Tile, old.Tile2); var (ox, oz, orot) = (old.Origin.X, old.Origin.Z, old.Rot);
                W.Damage(old, 1e8, null);
                if (old.Alive) { t.Log($"{cfg} {mission}: {old.Name} at ({ox:0}, {oz:0}) survives (invulnerable): skipped"); continue; }
                var (cx, cz) = WM.Centre(ai, aj);
                var pl = W.PlacementOf(ga.Results[0].Obj, cx, cz, 0, me);
                t.True(pl.Ok && Math.Abs(pl.X - ox) < 1e-6 && Math.Abs(pl.Z - oz) < 1e-6 && Math.Abs(pl.Rot - orot) < 1e-6, $"{cfg} {mission}: {old.Name} at ({ox:0}, {oz:0}) - a {ga.Results[0].Obj} goes back into its gap");
                var g = W.StartConstruction(me, ga, cx, cz, 0, new List<Unit>()) as Building;
                t.True(g != null && g.Tile == t1 && g.Tile2 == t2, $"{cfg} {mission}: rebuilt as {g?.Name}");
                t.Log($"{cfg} {mission} ({me.Tribe}): {old.Name} at ({ox:0}, {oz:0}) d {old.GateD} -> {g?.Name}");
                n++;
            }
        }
        if (n == 0) t.Skip("no campaign data");
    }
}
