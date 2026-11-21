// Scenario tests of stronger walls (docs/SIMULATION.md "Wall upgrades"): a stronger wall line drawn over an own weaker
// one builds over its pieces (WallMap.Weaker: the weaker piece gives way, the tile costs as a new one; equal or stronger
// pieces stay "have"; a gate keeps its wings, a tower stays); the per-piece upgrades - the Dustriders' clay wall
// (MIRAGE), the remake's SEAS fence -> SEAS wall and SEAS gate -> SEAS wall gate (Rules.RemakeWallUpgrades) - and the
// Norsemen's player-wide small / reinforced wall: the piece becomes the stronger class (World.WallClassChanged: model,
// hit points with their ratio, the wall map's class), several selected pieces each take it (QueueActionCommand.Also).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class Sys2WallUpgradeTests
{
    static void Rich(Player p) { p.CapsFixed = true; foreach (var r in Rules.RES) { p.Caps[r] = 20000; p[r] = 10000; } }
    /// <summary>MIRAGE: the chief at level 4 (Mayor_s4, the stronger walls shown)</summary>
    static void Level4(Player p) { p.Tt!.Enable("Special/Upgrades/mayor_s0/Lvl4_tt"); }

    static ActionDef Build(TestContext t, W_ w, Player p, string id)
    {
        var a = w.Data.Actions(p).FirstOrDefault(x => x.Kind == "Build" && x.Id == id);
        if (a == null) t.Skip("no action " + id);
        t.Equal(null, w.Data.Check(p, a!), id + " allowed");
        return a!;
    }
    /// <summary>a wall line placed and finished: the new pieces</summary>
    static List<Building> Line(W_ w, Player p, ActionDef a, double x0, double z0, double x1, double z1, out WallLine line)
    {
        line = w.WallLineOf(p, a, x0, z0, x1, z1)!;
        var before = w.Buildings.ToHashSet();
        w.PlaceWall(p, a, line.Select(x => (x.I, x.J, x.X, x.Z, x.State == "have")).ToList(), new List<Unit>(), false);
        var made = w.Buildings.Where(b => b.Alive && !before.Contains(b)).ToList();
        foreach (var b in made) if (!b.Built) w.FinishBuilding(b);
        return made;
    }
    static Building? WallAt(W_ w, double x, double z) { var (i, j) = w.Walls!.TileOf(x, z); return w.Walls.At(i, j)?.Wall; }
    static double Res(Player p, string r) => p[r];

    /// <summary>a SEAS wall line over a SEAS fence line: the fence pieces are built over (charged per tile, "ok" in the
    /// preview), the gate in the fence keeps its wings (the new pieces), a tower stays; a fence or wall line over the
    /// wall keeps every piece ("have")</summary>
    public static void OverbuildWeakerPieces(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Mirage, "SEAS");
        Rich(p); Level4(p);
        var fence = Build(t, w, p, "seas_fence");
        var wall = Build(t, w, p, "seas_wall");
        var WM = w.Walls!;
        // 10 fence pieces from x -36 to 36 (tiles 8 m), a gate on the 6th, a tower on the 2nd
        var fences = Line(w, p, fence, -36, -20, 36, -20, out _);
        t.Equal(10, fences.Count, "ten fence pieces");
        var gateA = Build(t, w, p, "seas_gate");
        var gate = w.StartConstruction(p, gateA, 4, -20, 0, new List<Unit>()) as Building;
        t.True(gate != null, "a gate in the fence");
        w.FinishBuilding(gate!);
        t.Equal("seas_gate", gate!.Name, "a fence gate");
        var towerA = w.Data.Actions(p).FirstOrDefault(x => x.Kind == "Build" && x.Id == "seas_turret_tower");
        Building? tower = null;
        if (towerA != null && w.Data.Check(p, towerA) == null) { tower = w.StartConstruction(p, towerA, -28, -20, 0, new List<Unit>()) as Building; if (tower != null) w.FinishBuilding(tower); }
        // the wall line from the west end to the gate's west wing: every tile a weaker piece -> "ok", costs as new
        var line = w.WallLineOf(p, wall, -36, -20, -4, -20)!;
        t.Equal(5, line.Count, "five tiles to the gate's wing");
        t.True(line.All(x => x.State == "ok"), "the fence tiles are built over: " + string.Join(",", line.Select(x => x.State)));
        var ghosts = w.WallPreviewOf(p, line);
        t.True(ghosts.Where(g => line.Any(x => x.I == g.I && x.J == g.J)).All(g => (g.State == "ok" || g.State == "gate") && g.Name == null), "the preview: new tiles of the wall");
        var wing = WallAt(w, -4, -20)!;
        t.True(wing.ParentGate == gate, "the fence piece is the gate's wing");
        var before = Rules.RES.ToDictionary(r => r, r => Res(p, r));
        var made = Line(w, p, wall, -36, -20, -4, -20, out _);
        t.Equal(5, made.Count, "five wall pieces");
        foreach (var r in Rules.RES) t.Equal(before[r] - 5 * wall.Cost[r], Res(p, r), $"{r}: five tiles paid");
        t.True(made.All(b => b.Name == "seas_wall"), "SEAS wall pieces");
        t.Equal(5, fences.Count(b => !b.Alive && b.SilentDeath && b.Origin.X < 0), "the five fence pieces gave way (no ruin)");
        t.True(fences.Where(b => b.Origin.X > 8).All(b => b.Alive), "the fence beyond the gate stands");
        t.True(made.All(b => WM.ClassOfObj(WM.At(b.Tile!.Value.I, b.Tile.Value.J)!.Wall) == 3), "the wall map: class 3 on the tiles");
        var newWing = WallAt(w, -4, -20)!;
        t.True(gate.Alive && newWing.ParentGate == gate && gate.Wings!.Contains(newWing) && gate.Wings.All(x => x.Alive), "the gate keeps its wings (the new piece one)");
        t.Equal(2, gate.Wings!.Count, "two wings");
        t.True(tower != null, "a tower on the second piece");
        if (tower != null) t.True(tower.Alive && WM.HasOwn(tower.Tile!.Value.I, tower.Tile.Value.J, WM.OwnerId(p), 3, WallMap.WT.TOWER), "the tower on the replaced piece stays");
        // the pieces connect across the classes: the new end piece and the old fence beyond the gate keep their arms
        t.True(newWing.WallParts != null && newWing.WallParts.Count > 1, "the new wing has its arms");
        // equal or stronger: a fence line and a wall line over the wall keep every piece
        var again = w.WallLineOf(p, fence, -36, -20, -4, -20)!;
        t.True(again.All(x => x.State == "have"), "a fence line over the wall: have " + string.Join(",", again.Select(x => x.State)));
        t.True(w.WallPreviewOf(p, again).Where(g => again.Any(x => x.I == g.I && x.J == g.J)).All(g => g.Name == "seas_wall"), "the preview shows the stronger pieces as themselves");
        var same = w.WallLineOf(p, wall, -36, -20, -4, -20)!;
        t.True(same.All(x => x.State == "have"), "a wall line over the wall: have");
        before = Rules.RES.ToDictionary(r => r, r => Res(p, r));
        int n = w.PlaceWall(p, fence, again.Select(x => (x.I, x.J, x.X, x.Z, x.State == "have")).ToList(), new List<Unit>(), false);
        t.Equal(0, n, "nothing placed");
        t.True(made.All(b => b.Alive), "the wall pieces stay");
        foreach (var r in Rules.RES) t.Equal(before[r], Res(p, r), $"{r}: nothing paid");
        // the single-tile placement (the ghost, the AI's StartConstruction): a fence tile beyond the gate is a valid spot
        var pl = w.PlacementOf("seas_wall", 20, -20, 0, p);
        t.True(pl.Ok && pl.Replaces != null && pl.Replaces.Name == "seas_fence", "PlacementOf: a fence tile takes a wall piece");
        var pl2 = w.PlacementOf("seas_fence", -20, -20, 0, p);
        t.True(!pl2.Ok && pl2.Existing != null, "PlacementOf: a fence on a wall tile is 'have'");
        // another tribe: a Dragon Clan palisade (MIRAGE class 5, stored as 3 in the tile word) over defence skewers
        var (w2, n2, _) = Sys2.World(t, Sys2.Mirage, "Ninigi");
        Rich(n2); Level4(n2);
        var skewers = Line(w2, n2, Build(t, w2, n2, "ninigi_defense_skewer"), -20, 20, 20, 20, out _);
        var nl = w2.WallLineOf(n2, Build(t, w2, n2, "ninigi_palisade"), -20, 20, 20, 20)!;
        t.True(skewers.Count == 6 && nl.All(x => x.State == "ok"), "Dragon Clan: the skewers are built over");
    }

    /// <summary>the SEAS fence -> SEAS wall upgrade (the remake's own, MIRAGE only): offered at a fence piece once the
    /// SEAS wall can be built, priced as the wall less the fence, applied to every selected fence piece, each becoming a
    /// SEAS wall with its hit-point ratio; the SEAS gate -> SEAS wall gate the same</summary>
    public static void SeasFenceUpgrade(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Mirage, "SEAS");
        Rich(p);
        var fence = Build(t, w, p, "seas_fence");
        var pieces = Line(w, p, fence, -20, -20, 20, -20, out _);
        t.Equal(6, pieces.Count, "six fence pieces");
        static ActionDef? Up(W_ w, Building b, string id) => w.Data.ActionsOf(b.RulesOwner(), b.Name, "building", b.Def!.Type).FirstOrDefault(a => a.Kind == "Upgrades" && a.Id == id);
        var up = Up(w, pieces[0], "seas_wall");
        t.True(up != null, "a fence piece offers the SEAS wall upgrade");
        t.True(up!.Results.All(r => r.Cls == "local"), "an upgrade of the piece itself");
        t.Equal("hidden", w.Data.Check(p, up, pieces[0]), "hidden before the chief's level 4 (as the SEAS wall's build)");
        Level4(p);
        up = Up(w, pieces[0], "seas_wall")!;
        t.Equal(null, w.Data.Check(pieces[0].RulesOwner(), up, pieces[0]), "offered at level 4");
        var wb = w.Data.Actions(p).First(a => a.Kind == "Build" && a.Id == "seas_wall");
        foreach (var r in Rules.RES) t.Equal(Math.Max(0, wb.Cost[r] - fence.Cost[r]), up.Cost[r], $"{r}: the wall less the fence");
        t.Equal(Math.Max(1, wb.Time - fence.Time), up.Time, "duration: the wall's less the fence's");
        t.True(up.Locs.Any(l => l.Icon == "seas_wall" && l.Desc == "_seas_wall"), "seas_wall's icon and help text");
        t.True(up.Req.SequenceEqual(wb.Req), "the wall's inventions required");
        // two of them damaged to half, three selected
        pieces[0].Hp = pieces[0].MaxHp / 2;
        double before = p["wood"], beforeS = p["stone"];
        var cmd = new QueueActionCommand { Player = p.Id, Producer = pieces[0].Id, Action = up.Path, Also = new[] { pieces[1].Id, pieces[2].Id } };
        t.Equal(null, cmd.Apply(w), "queued");
        t.True(pieces.Take(3).All(b => b.Queue.Count == 1), "each selected piece takes the upgrade");
        t.True(pieces.Skip(3).All(b => b.Queue.Count == 0), "the others not");
        t.Equal(before - 3 * up.Cost.Wood, p["wood"], "each pays its own price (wood)");
        t.Equal(beforeS - 3 * up.Cost.Stone, p["stone"], "(stone)");
        Sys2.Run(w, up.Time + 1);
        var WM = w.Walls!;
        var wallHp = w.Data.Stats("seas_wall", 1, p)!.Hp;
        foreach (var b in pieces.Take(3))
        {
            t.Equal("seas_wall", b.Name, "the piece is a SEAS wall");
            t.Equal(w.Data.Stats("seas_wall", 1, p)!.Gfx, b.Gfx, "with its model");
            t.Equal(3, WM.ClassOfObj(WM.At(b.Tile!.Value.I, b.Tile.Value.J)!.Wall), "the wall map's class 3");
            t.True(b.Action != null && b.Action.Results[0].Obj == "seas_wall", "repairs as a SEAS wall");
            t.Equal(wallHp, b.MaxHp, "the wall's hit points");
        }
        t.True(Math.Abs(pieces[0].Hp - wallHp / 2) < 1, $"the damaged one keeps its ratio ({pieces[0].Hp} / {wallHp})");
        t.True(pieces.Skip(3).All(b => b.Name == "seas_fence"), "the others stay fences");
        t.True(Up(w, pieces[0], "seas_wall") == null, "a SEAS wall offers no SEAS wall upgrade");
        // the overbuild rule sees it: a wall line over the upgraded pieces keeps them
        var line = w.WallLineOf(p, wb, -20, -20, 20, -20)!;
        t.Equal(3, line.Count(x => x.State == "have"), "the three upgraded pieces are 'have' for a wall line");
        // the gate: a SEAS gate between the fence pieces becomes a SEAS wall gate
        var ga = Build(t, w, p, "seas_gate");
        var gate = w.StartConstruction(p, ga, 12, -20, 0, new List<Unit>()) as Building;
        t.True(gate != null, "a SEAS gate");
        w.FinishBuilding(gate!);
        t.Equal("seas_gate", gate!.Name, "between fences: a fence gate");
        var gu = Up(w, gate, "seas_wall_gate");
        t.True(gu != null && w.Data.Check(gate.RulesOwner(), gu, gate) == null, "the gate offers the wall gate upgrade");
        t.Equal(null, w.QueueAction(gate, gu!), "queued at the gate");
        Sys2.Run(w, gu!.Time + 1);
        t.Equal("seas_wall_gate", gate.Name, "a SEAS wall gate");
        t.Equal(w.Data.Stats("seas_wall_gate", 1, p)!.Hp, gate.MaxHp, "its hit points");
        t.True(gate.Cells.Count > 0 && gate.Cells.All(k => w.Nav.GateAt.TryGetValue(k, out var g) && g == gate), "its cells ask the gate");
        t.Equal(2, gate.Wings!.Count(x => x.Alive), "its wings");
        // the base game has no SEAS wall: no upgrade
        var (wb2, pb, _) = Sys2.World(t, Sys2.Base, "SEAS");
        t.True(!wb2.Data.Actions(pb).Any(a => a.Kind == "Upgrades" && a.Id == "seas_wall"), "no SEAS wall upgrade without MIRAGE");
    }

    /// <summary>the original's wall upgrades still work and now change the class: the Dustriders' clay wall (MIRAGE, per
    /// piece, several selected) and the Norsemen's small / reinforced wall (player-wide: every palisade and gate)</summary>
    public static void AjeAndHuWallUpgrades(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Mirage, "Aje", "Hu");
        Rich(p); Level4(p);
        var pal = Build(t, w, p, "aje_bone_palisade");
        var pieces = Line(w, p, pal, -20, -20, 4, -20, out _);
        t.Equal(4, pieces.Count, "four bone palisade pieces");
        var up = w.Data.ActionsOf(pieces[0].RulesOwner(), pieces[0].Name, "building", "BLDG").FirstOrDefault(a => a.Kind == "Upgrades" && a.Id == "clay_wall");
        t.True(up != null && w.Data.Check(pieces[0].RulesOwner(), up, pieces[0]) == null, "the clay wall upgrade is offered");
        pieces[1].Hp = pieces[1].MaxHp * 0.25;
        double wood = p["wood"];
        var cmd = new QueueActionCommand { Player = p.Id, Producer = pieces[0].Id, Action = up!.Path, Also = new[] { pieces[1].Id } };
        t.Equal(null, cmd.Apply(w), "queued");
        t.Equal(wood - 2 * up.Cost.Wood, p["wood"], "two paid");
        Sys2.Run(w, up.Time + 1);
        var WM = w.Walls!;
        var clayHp = w.Data.Stats("aje_clay_wall", 1, p)!.Hp;
        foreach (var b in pieces.Take(2))
        {
            t.Equal("aje_clay_wall", b.Name, "a clay wall");
            t.Equal("aje_clay_wall", b.Gfx, "its model");
            t.Equal(clayHp, b.MaxHp, "its hit points");
            t.Equal(WM.ClassOf("aje_clay_wall")![1], WM.ClassOfObj(WM.At(b.Tile!.Value.I, b.Tile.Value.J)!.Wall), "the clay wall's class");
        }
        t.True(Math.Abs(pieces[1].Hp - clayHp * 0.25) < 1, "the ratio kept");
        t.True(pieces.Skip(2).All(b => b.Name == "aje_bone_palisade"), "the others unchanged");
        // the Norsemen (base game): researched once, every palisade and palisade gate becomes a small wall
        var (w2, h, _) = Sys2.World(t, Sys2.Base, "Hu");
        Rich(h); h.Debug = true;
        var hp = w2.Data.Actions(h).First(a => a.Kind == "Build" && a.Id == "hu_palisade");
        var hpieces = Line(w2, h, hp, -36, -20, 36, -20, out _);
        var hg = w2.Data.Actions(h).First(a => a.Kind == "Build" && a.Id == "hu_palisade_gate");
        var gate = (Building)w2.StartConstruction(h, hg, 4, -20, 0, new List<Unit>());
        hpieces = hpieces.Where(b => b.Alive).ToList();
        foreach (var (id, cls, gcls, k) in new[] { ("hu_small_wall", "hu_small_wall", "hu_small_wall_gate", 2), ("hu_re_enforced_wall", "hu_re_enforced_wall", "hu_re_enforced_wall_gate", 3) })
        {
            var a = w2.Data.Actions(h).First(x => x.Kind == "Upgrades" && x.Id == id);
            w2.CompleteUpgrade(hpieces[0], a);
            Sys2.Run(w2, 1);
            t.True(hpieces.All(b => b.Name == cls), $"{id}: every piece a {cls} ({string.Join(",", hpieces.Select(b => b.Name).Distinct())})");
            t.Equal(w2.Data.Stats(cls, 1, h)!.Hp, hpieces[0].MaxHp, $"{id}: its hit points");
            t.True(hpieces.All(b => WM2(w2).ClassOfObj(b) == k), $"{id}: class {k}");
            t.Equal(gcls, gate.Name, $"{id}: the gate a {gcls}");
            t.True(gate.Wings!.All(x => x.Alive && x.ParentGate == gate), $"{id}: the gate keeps its wings");
        }
    }
    static WallMap WM2(W_ w) => w.Walls!;
}
