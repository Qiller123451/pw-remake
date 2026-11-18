// Construction: placing buildings, building them up, repairing, dismantling (Aje "BuildDown"), self-destruction,
// wall lines, and what happens when a building is destroyed.
//
// Reference: docs/spec/buildings.md sections 1.2-1.8 (BuildUp.usl, BuildUpBuilding.usl, Repair.usl,
// BuildDownBuilding.usl, Building.usl).
//
//   build time  = duration * slowest worker's time factor * (0.1 + 0.9 * 0.8^(N-1))      N = workers
//   repair      = (20 + 5 * level) hp/s per worker; a full repair costs 50 % of the build cost
//   BuildDown   = half the build time, then a 100 % refund of the build cost (capped by storage)
//
// Ported from remake/src/game/systems/construction.js (pw-exporter 98c7703); PlaceWall from main.js G.placeWall, the
// ground levelling under a new building from main.js W.adaptTerrain (UseGround: the levelling through Sim/Maps Ground,
// the event "terrain" tells the view what to redraw). The ruin is a model name (Building.Ruin) the view draws.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using WMap = PwRemake.Sim.World.WallMap;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public sealed partial class UnitTask
{
    /// <summary>build: a repair (Repair.usl), the builder link it walks to (Bl_n)</summary>
    public bool Repair;
    public (string Name, double X, double Z)? BSlot;
}

public sealed partial class Building
{
    /// <summary>the action that put it up and what was paid for it (repairs cost half of it)</summary>
    public ActionDef? Action;
    public Cost? Paid;
    /// <summary>repair costs not yet paid (fractions of a resource)</summary>
    public double[]? RepairDebt;
    /// <summary>a gate's wings (the wall pieces at both ends); the gate a wall piece is the wing of is Entity.ParentGate
    /// (Combat.Entities.cs: combat forwards a wing's damage through it - a second field here hid that one)</summary>
    public List<Building>? Wings;
}

public sealed partial class QueueItem
{
    /// <summary>seconds done / in all; the pyramid level of the unit trained (0: an upgrade); the cost given back</summary>
    public double T, Total;
    public int Level;
    public Cost? Cost;
    /// <summary>the carrier's hull: open at this time (production.js HULL_OPEN)</summary>
    public double? HullT;
}

/// <summary>where a building would really be placed for a click (construction.js placement)</summary>
public sealed class Placement
{
    public double X, Z, Rot;
    public bool Ok;
    public string? Why;
    /// <summary>a gate: its axis, rotation, wings, the wall piece it replaces, its class</summary>
    public GateFit? Fit;
    /// <summary>a gate of the stronger class of its wall: that class's gate</summary>
    public string? GateName;
    /// <summary>a tower / wall / trap tile that has one already: that object</summary>
    public Building? Existing;
    /// <summary>a wall piece on a tile with an own weaker piece (WallMap.Weaker): that piece, which gives way to the new
    /// site (the remake's overbuilding)</summary>
    public Building? Replaces;
    /// <summary>a ladder: the wall tile it docks to</summary>
    public (int I, int J)? Dock;
}

public sealed class GateFit
{
    public int D;
    public double Rot;
    public List<Building> Wings = new();
    public Building? Wall;
    public int Cls;
}

/// <summary>a tile of a wall line as the wall placer shows it (construction.js wallLine)</summary>
public sealed class WallLineEntry
{
    public int I, J;
    public double X, Z, Rot;
    public bool GateOK;
    /// <summary>"have" (an own piece is there: no cost), "ok", "cost" (cannot be paid), "bad" (requirements)</summary>
    public string State = "ok";
}

public sealed class WallLine : List<WallLineEntry>
{
    public WallCalc Wall = null!;
}

public sealed partial class World
{
    static readonly Logger LogCon = Log.For("Systems.Construction");
    /// <summary>wall segments sit on an 8 m grid (engine WallMap; see spec 2.1)</summary>
    public const double WALL_STEP = 8;
    static readonly Regex GateScript = new("^CGate|Gate$", RegexOptions.Compiled);
    static readonly Regex QuarryRe = new("^(hu|ninigi)_stone_quarry$", RegexOptions.Compiled);
    static readonly Regex MirageStoreRe = new("^(aje_bazaar|hu_marketplace|hu_warehouse|ninigi_warehouse|seas_small_tent|seas_steelwork|aje_resource_collector)$", RegexOptions.Compiled);

    /// <summary>the ground levellings done ([gfx, x, z, rot]: saved games redo them)</summary>
    public readonly List<(string Gfx, double X, double Z, double Rot)> GroundLog = new();

    /// <summary>footprint of a building placed at (x, z): nav cells of its pathfinder boxes (plus a margin) and bounds</summary>
    public sealed class FootprintInfo
    {
        public double Fx, Fz, Hx, Hz, Radius;
        public double[][] Boxes = Array.Empty<double[]>();
        public List<int> Cells = new();
    }

    public FootprintInfo FootprintOf(string name, double x, double z, double rot = 0, bool margin = true, Player? owner = null)
    {
        var st = Data.Stats(name, 1, owner);
        var def = Data.Def(name, owner);
        var F = Footprint.Place(Models, st?.Gfx, x, z, rot, margin ? 2.2 : 0, def != null && def.WallKind == "wall" ? Building.WALL_BOXES : null);
        var cells = new OrderedSet<int>();
        foreach (var b in F.Boxes) foreach (var k in Nav.RectCells(b[0], b[1], b[2], b[3], rot)) cells.Add(k);
        return new FootprintInfo { Fx = F.Fx, Fz = F.Fz, Hx = F.Hx, Hz = F.Hz, Boxes = F.Boxes, Cells = cells.ToList(), Radius = Math.Max(F.Hx, F.Hz) * 0.85 };
    }

    public static bool InBoxes(double[][] boxes, double rot, double x, double z, double m = 0)
    {
        double c = SMath.Cos(rot), s = SMath.Sin(rot);
        foreach (var b in boxes)
        {
            double dx = x - b[0], dz = z - b[1];
            if (Math.Abs(dx * c - dz * s) < b[2] + m && Math.Abs(dx * s + dz * c) < b[3] + m) return true;
        }
        return false;
    }

    /// <summary>placement check (engine place check): inside the map, free cells, no enemy units or resources in the way</summary>
    public bool CanPlace(string name, double x, double z, double rot = 0, Player? owner = null)
    {
        var def = Data.Def(name, owner);
        // (a building whose model the assets lack - game data newer than the export - can't be placed; the computer
        // player asked for one and stopped with an error)
        if (!Models.Has(Data.Stats(name, 1, owner)?.Gfx)) return false;
        if (def != null && def.Coastal) return CoastalAt(name, x, z, owner).Ok;      // harbours: on the shore, turned by it (Coastal.cs)
        bool wallish = def != null && def.WallKind != null;
        // (FootprintOf without its list of cells: each box's cells looked at in place, the first taken one ends it)
        var f = Footprint.Place(Models, Data.Stats(name, 1, owner)?.Gfx, x, z, rot, !wallish ? 2.2 : 0, def != null && def.WallKind == "wall" ? Building.WALL_BOXES : null);
        var P = Play;
        if (f.Fx - f.Hx < P.X0 + 6 || f.Fx + f.Hx > P.X1 - 6 || f.Fz - f.Hz < P.Z0 + 6 || f.Fz + f.Hz > P.Z1 - 6) return false;
        foreach (var b in f.Boxes) if (!Nav.RectFree(b[0], b[1], b[2], b[3], rot, Reserved)) return false;
        double fr = Math.Max(f.Hx, f.Hz) * 0.85;
        bool blocked = false;
        UHash.Query(f.Fx, f.Fz, fr + 8, u => { if (u.Alive && u.Owner != owner && InBoxes(f.Boxes, rot, u.Pos.X, u.Pos.Z, u.Radius - 2.2)) blocked = true; });
        if (blocked) return false;
        RHash.Query(f.Fx, f.Fz, fr + 8, n => { if (n.Alive && InBoxes(f.Boxes, rot, n.Pos.X, n.Pos.Z, n.Radius - 2.2)) blocked = true; });
        return !blocked;
    }

    /// <summary>Where a building would really be placed for a click at (x, z): coastal buildings snap to the shore, wall
    /// map objects to their tile, everything else stays where it is. (from: where the workers stand - a ladder goes to
    /// the side of the wall nearest them, systems/siege.js)</summary>
    public Placement PlacementOf(string name, double x, double z, double rot = 0, Player? owner = null, (double X, double Z)? from = null)
    {
        var def = Data.Def(name, owner);
        if (def != null && def.Ladder) return LadderPlacement(name, x, z, owner, from);
        var kind = def?.WallKind;
        if (kind == "gate" && !GateScript.IsMatch(def!.Script ?? "")) return new Placement { X = x, Z = z, Rot = rot, Ok = false };
        var WM = Walls;
        if (kind == "gate" && WM != null)
        {
            // CalcWall for a gate (wallmap.js calcGate, 0x7e26c0): the cursor's tile, the first axis d = 0..7 that fits;
            // the gate takes the stronger wall class of its tiles (gateName: the class's gate)
            var (i, j) = WM.TileOf(x, z);
            var (cx, cz) = WM.Centre(i, j);
            var w = WM.CalcWall(owner, name, x, z, x, z);
            if (w == null) return new Placement { X = cx, Z = cz, Rot = 0, Ok = false, Why = "gate" };
            // the gap a destroyed two-tile map gate left: the new gate stands where that one stood, across both tiles
            // (no wings: the map's own gates have none either, Building.usl CGate.OnInit bNewGate)
            if (w.Gap is { } g)
                return new Placement { X = g.X, Z = g.Z, Rot = g.Rot, Ok = true, Fit = new GateFit { D = g.D, Rot = g.Rot, Cls = w.Cls }, GateName = WallClassName(w.Race, w.Cls, 1) ?? name };
            var t = WM.At(i, j);
            var fit = new GateFit { D = w.D, Rot = WMap.GateRot(w.D), Wings = WM.GateWings(i, j, w.D), Wall = t?.Wall != null && t.Wall.Alive ? t.Wall : null, Cls = w.Cls };
            return new Placement { X = cx, Z = cz, Rot = fit.Rot, Ok = true, Fit = fit, GateName = WallClassName(w.Race, w.Cls, 1) ?? name };
        }
        if ((kind == "wall" || kind == "tower" || kind == "trap") && WM != null)
        {
            // the wall placer: one tile (CalcWall with start = end); a tower may stand on an own wall piece, which stays
            var (i, j) = WM.TileOf(x, z);
            var (cx, cz) = WM.Centre(i, j);
            var w = WM.CalcWall(owner, name, x, z, x, z);
            var t = w != null ? w.Tiles[^1] : null;
            // a stronger wall on an own weaker piece: built over it (CalcWall: not "have")
            var weak = w != null && t != null && !t.Existing && kind == "wall" ? WM.Weaker(i, j, WM.OwnerId(owner), w.Race, w.Cls) : null;
            return new Placement { X = cx, Z = cz, Rot = 0, Ok = w != null && !(t != null && t.Existing), Existing = t != null && t.Existing ? WM.At(i, j)?[kind] : null, Replaces = weak };
        }
        if (def != null && def.Coastal)
        {
            var s = CoastalSnap(name, x, z, owner);
            return s != null ? new Placement { X = s.Value.X, Z = s.Value.Z, Rot = s.Value.Rot, Ok = true } : new Placement { X = x, Z = z, Rot = ShoreNormal(x, z) is var (nx, nz) ? CoastalRot(name, owner, nx, nz) : rot, Ok = false };
        }
        return new Placement { X = x, Z = z, Rot = rot, Ok = CanPlace(name, x, z, rot, owner) };
    }

    /// <summary>dock side of a coastal building model at rotation 0: link Do_1 (ships berth there), else Ex_1 / RE_1 / Spwn</summary>
    public (double X, double Z) DockOffset(string name, Player? owner)
    {
        var st = Data.Stats(name, 1, owner);
        if (st == null || !Models.Has(st.Gfx)) return (0, 8);
        foreach (var n in new[] { "Do_1", "Ex_1", "RE_1", "Spwn" }) if (Models.Link(st.Gfx, n) is V3 v) return (v.X, v.Z);
        return (0, 8);
    }

    /// <summary>move units out of a building footprint</summary>
    public void ClearFootprint(Building b, List<Unit> except)
    {
        foreach (var u in UHash.Near(b.Pos.X, b.Pos.Z, b.Radius + 10))
        {
            if (!u.Alive || except.Contains(u) || u.Inside) continue;
            if (b.SurfDist(u.Pos.X, u.Pos.Z) < u.Radius + 0.3)
            {
                var (ex, ez) = b.ExitPoint(u.Pos.X, u.Pos.Z);
                if (u.Owner == b.Owner && (u.Task.Type == "idle" || u.Task.Type == "hold")) Order(u, new Order("move") { X = ex, Z = ez });
                else { u.Pos.X = ex; u.Pos.Z = ez; if (u.Path.Count > 0 && u.Goal != null) u.SetPath(u.Goal.Value.X, u.Goal.Value.Z); }
            }
        }
    }

    /// <summary>CheckConditionsAndPay + PlaceObj: pay the whole cost up front, lay the foundation, send the workers.
    /// -> the Building, or a reason ("req", "limit", "cost", "place", "unique", "level" ...)</summary>
    public object StartConstruction(Player p, ActionDef action, double x, double z, double rot, List<Unit> workers)
    {
        var why = Data.Check(p, action);
        if (why != null) return why == "hidden" || why == "disabled" ? "req" : why;
        // MIRAGE's building limits (ServerApp.usl PlaceObj: CBLDGMgr.CanAddBuilding of the placement class) - BuildingLimits.cs
        if (LimitWhy(p, action) is { } lim) return lim;
        if (!p.CanAfford(action.Cost)) return "cost";
        var name = action.Results[0].Obj;
        (double X, double Z)? from = workers.Count > 0 ? (workers.Sum(w => w.Pos.X) / workers.Count, workers.Sum(w => w.Pos.Z) / workers.Count) : null;
        var at = PlacementOf(name, x, z, rot, p, from);
        if (!at.Ok) return "place";
        x = at.X; z = at.Z; rot = at.Rot;
        // one warp gate per player
        if (IsWarpGateScript(Data.Script(name)) && Buildings.Any(b => b.Alive && b.Owner == p && IsWarpGateScript(b.Def!.Script))) return "unique";
        // a gate of the stronger class of its wall (CalcWall's class rule): that class's gate and price
        var act = action;
        if (at.GateName != null && at.GateName != name)
        {
            var a2 = Data.Actions(p).FirstOrDefault(q => q.Kind == "Build" && q.Results.Count > 0 && q.Results[0].Obj == at.GateName);
            if (a2 != null) { act = a2; if (LimitWhy(p, a2) is { } lim2) return lim2; if (!p.CanAfford(a2.Cost)) return "cost"; }
        }
        p.Pay(act.Cost);
        // a gate replaces the wall piece of its tile (the server's tile update deletes it, no refund; its neighbours
        // become the wings); a tower stands on an own piece, which stays
        if (at.Fit?.Wall != null) RemoveWallPiece(at.Fit.Wall);
        // a stronger wall piece over an own weaker one (the remake's overbuilding): the weaker piece gives way the same
        // way, no refund; the gate it was a wing of takes the new piece as its wing
        var wingOf = at.Replaces?.ParentGate;
        if (at.Replaces != null) RemoveWallPiece(at.Replaces);
        // AdaptTerrain: the ground levels to the building and takes its brush's materials (every building but walls,
        // gates, traps, coastal buildings and a tower on a wall piece - BuildingGround.Changes; game/ground.js; the
        // original's bWall: no AdaptTerrain, SA:1327)
        var def = Data.Def(name, p);
        if (AdaptTerrain != null && BuildingGround.Changes(def, onWall: at.Fit != null, dock: at.Dock != null))
        {
            var st = Data.Stats(name, 1, p);
            if (st?.Gfx != null) AdaptTerrain(st.Gfx, x, z, rot);
        }
        if (act != action) action = act;
        var b = PlaceBuilding(action.Results[0].Obj, p, x, z, rot, false);
        if (b == null) { p.Refund(act.Cost); return "place"; }      // (no model: the JS stopped with an error)
        if (at.Fit != null) b.GateD = at.Fit.D;
        // the plants, trees and stumps within the building's radius go (ServerApp.usl PlaceObj "Vegetation")
        ClearVegetation?.Invoke(x, z, b.Hx != 0 ? Math.Sqrt(b.Hx * b.Hx + b.Hz * b.Hz) : b.Radius);      // (GetRadius: the bounding circle)
        b.Action = action;
        b.Paid = action.Cost.Clone();
        if (at.Fit != null) { b.Wings = at.Fit.Wings.Where(w => w.Alive).ToList(); foreach (var w in b.Wings) w.ParentGate = b; }
        if (wingOf != null && wingOf.Alive && wingOf.Wings != null)
        {
            wingOf.Wings = wingOf.Wings.Where(w => w.Alive).ToList();
            wingOf.Wings.Add(b); b.ParentGate = wingOf;
        }
        if (at.Dock != null) LadderPlaced(b, at.Dock.Value);                // a ladder holds its wall piece (systems/siege.js)
        ClearFootprint(b, workers);
        // debug mode: the building stands finished at once
        if (p.Debug) { b.Progress = 1; b.Hp = b.MaxHp; FinishBuilding(b, new List<Unit>()); }
        else if (workers.Count > 0) Order(workers, new Order("build") { Target = b });
        Emit("placed", new Args { ["building"] = b });
        return b;
    }

    /// <summary>A wall line from (x0, z0) to (x1, z1) after the legs already held (Shift corners): the engine's CalcWall
    /// (wallmap.js calcWall: a shortest path over placeable tiles, all or nothing). -> null when there is no line, else
    /// the tiles: state "have" (an own piece is there already: no cost), "ok" or "cost" (the k-th entry of the list is
    /// green while k &lt; what the player can pay for, PreviewWall's numA - the existing tiles count too, as in the
    /// original), the line's race / class / type in .Wall</summary>
    public WallLine? WallLineOf(Player p, ActionDef action, double x0, double z0, double x1, double z1, List<WallLineEntry>? held = null)
    {
        var WM = Walls;
        if (WM == null) return null;
        var name = action.Results[0].Obj;
        var w = WM.CalcWall(p, name, x0, z0, x1, z1, (held ?? new List<WallLineEntry>()).Select(t => new WallLineTile { I = t.I, J = t.J, Existing = t.State == "have", GateOK = t.GateOK }).ToList());
        if (w == null) return null;
        double afford = 1e9;
        if (!p.Debug)
        {
            afford = 1000;
            foreach (var r in new[] { "food", "wood", "stone" }) if (action.Cost[r] > 0) afford = Math.Min(afford, Math.Floor(p[r] / action.Cost[r]));
        }
        bool req = Data.Check(p, action) == null;
        // MIRAGE's building limits: the new pieces past the limit are not placed (PlaceWall stops at the first one
        // StartConstruction refuses) - shown as the unpaid ones
        int room = LimitRoom(p, action), fresh = 0;
        var o = new WallLine { Wall = w };
        for (int k = 0; k < w.Tiles.Count; k++)
        {
            var t = w.Tiles[k];
            var (x, z) = WM.Centre(t.I, t.J);
            bool over = !t.Existing && fresh++ >= room;
            o.Add(new WallLineEntry { I = t.I, J = t.J, X = x, Z = z, Rot = 0, GateOK = t.GateOK, State = t.Existing ? "have" : !req ? "bad" : k + 1 <= afford && !over ? "ok" : "cost" });
        }
        return o;
    }

    /// <summary>the class name for (race, class, type) of the wall class table (0x7df350)</summary>
    public string? WallClassName(int race, int cls, int type)
    {
        var T = Walls?.Classes;
        if (T == null) return null;
        foreach (var (n, c) in T) if (c[0] == race && c[1] == cls && c[2] == type) return n;
        return null;
    }

    /// <summary>a wall piece disappears without a ruin and without a refund (a gate or tower takes its tile)</summary>
    public void RemoveWallPiece(Building? w)
    {
        if (w == null || !w.Alive) return;
        w.SilentDeath = true;
        KillBuilding(w, null);
    }

    /// <summary>WallClassChanged (CWall / CGate.UpdateGfx -> 0x48f3b0, walls_engine.md §9): a filter gave a wall piece or
    /// gate the model of another wall class of its race and type (the Norsemen's small / reinforced wall, the Dustriders'
    /// clay wall, the remake's SEAS wall) - the object becomes that class: its name, values and hit points (the ratio
    /// kept), its build action (repairs, BuildDown), its BuildObjects entry, its model, and the wall map's class of its
    /// tiles (the gate rule and the overbuilding see it). Its tile, owner, wings and gate state stay.</summary>
    public void WallClassChanged(Building b, string cls)
    {
        if (!b.Alive || b.Name == cls || !Data.Exists(cls)) return;
        var O = b.RulesOwner();
        if (b.Built) EntityFilters(b, false);
        double ratio = b.MaxHp > 0 ? b.Hp / b.MaxHp : 1;
        b.Name = cls;
        b.Def = Data.Def(cls, O)!;
        b.Stats = Data.Stats(cls, 1, O)!;
        b.MaxHp = b.Stats!.Hp;
        b.Hp = Math.Max(1, Math.Min(b.MaxHp, ratio * b.MaxHp));
        b.Weapons = Data.WeaponSetOf(cls, 1, O);
        b.Weapon = b.Weapons.Long;
        b.Fow = b.Stats.Fow != 0 ? b.Stats.Fow : 40;
        b.Action = BuildActionOf(b);
        b.Paid = b.Action?.Cost.Clone();
        if (b.Built) EntityFilters(b, true);
        Walls?.ClassChanged(b);
        if (b.Stats.Gfx != null && b.Stats.Gfx != b.Gfx)
        {
            bool gate = b.Def!.Gate;
            if (gate) ClearGateCells(b);
            b.SwapModel(b.Stats.Gfx);
            if (gate) { b.Relocate(); SetGateCells(b); SetGateAnim(b, b.GateOpen); }      // (a site too: mirage_gate_blocking)
        }
        RecomputeCapsOf(b.Owner);
    }

    /// <summary>a wall line (tiles from WallLineOf) as the server places it (main.js G.placeWall, PlaceWall 0x490920):
    /// every tile is checked first, in order, with the earlier ones written (one failing tile: nothing is placed); then
    /// the new tiles in list order, existing ones skipped, until one cannot be paid. Every builder gets the first tile
    /// (queued with Ctrl) and every later tile queued (Q_Build). -> the number of pieces placed</summary>
    public int PlaceWall(Player p, ActionDef a, List<(int I, int J, double X, double Z, bool Have)> tiles, List<Unit> workers, bool queued)
    {
        var WM = Walls;
        var c = WM?.ClassOf(a.Results.Count > 0 ? a.Results[0].Obj : null);
        if (WM == null || tiles == null || tiles.Count == 0 || c == null) return 0;
        int race = c[0], cls = c[1], type = c[2];
        int me = WM.OwnerId(p);
        WM.Begin();
        WM.ScanObstacles(tiles.Min(t => t.I) - 3, tiles.Min(t => t.J) - 3, tiles.Max(t => t.I) + 3, tiles.Max(t => t.J) + 3);
        bool bad = false;
        foreach (var t in tiles)
        {
            if (t.Have) { if (!WM.HasOwn(t.I, t.J, me, race, type == 3 ? 3 : 0)) { bad = true; break; } continue; }
            if (!WM.Placeable(t.I, t.J, me, race, cls, type)) { bad = true; break; }
            WM.Write(t.I, t.J, me, race, cls, type);
        }
        WM.Restore();
        if (bad) { LogCon.Debug("wall line: cannot be placed here"); return 0; }
        int n = 0; string? why = null;
        var placed = new List<Building>();
        foreach (var t in tiles)
        {
            if (t.Have) continue;
            var b = StartConstruction(p, a, t.X, t.Z, 0, new List<Unit>());
            if (b is string s) { why = s; break; }
            placed.Add((Building)b);
            n++;
        }
        if (placed.Count > 0 && workers.Count > 0)
        {
            var ws = workers.Where(u => u.Alive).ToList();
            // queued: after the piece the worker is building now (a worker doing anything else starts at once)
            if (queued) foreach (var u in ws)
                {
                    var q = new List<Building>(u.BuildQueue ?? new List<Building>()); q.AddRange(placed);
                    if (u.Task.Type == "build") u.BuildQueue = q;
                    else { Order(u, new Order("build") { Target = q[0] }); u.BuildQueue = q.Skip(1).ToList(); }
                }
            else { Order(ws, new Order("build") { Target = placed[0] }); foreach (var u in ws) u.BuildQueue = placed.Skip(1).ToList(); }
        }
        if (n == 0 && why != null) LogCon.Debug("wall line: " + why);
        return n;
    }

    // BuildUp.usl: the worker occupies the nearest free builder link (Bl_n), walks there and hammers
    partial void StartBuild(Unit u, Entity? target) => StartBuildAt(u, target as Building);

    public void StartBuildAt(Unit u, Building? b)
    {
        if (!u.CanBuild || b == null || !b.Alive || b.Owner != u.Owner) return;
        if (b.Built) { if (b.Hp < b.MaxHp) StartRepairAt(u, b); return; }
        u.Task = new UnitTask("build") { Building = b, Phase = "go", BSlot = b.OccupySlot(u), User = u.Task.User };
        b.Builders.Add(u);
        var (x, z) = u.Task.BSlot is { } sl ? (sl.X, sl.Z) : ApproachPoint(u, b);
        u.SetPath(x, z);
    }

    /// <summary>a reachable point next to a building (or any entity), towards the unit</summary>
    public (double X, double Z) ApproachPoint(Unit u, Entity b)
    {
        double a = SMath.Atan2(u.Pos.Z - b.Pos.Z, u.Pos.X - b.Pos.X);
        var bb = b as Building;
        for (double r0 = 0.8; r0 < 6; r0 += 1.5)
        {
            for (int k = 0; k < 24; k++)
            {
                double aa = a + (k % 2 != 0 ? 1 : -1) * Math.Ceiling(k / 2.0) * 0.27;
                for (double r = 0; r < b.Radius * 2 + 10; r += 1)
                {
                    double x = b.Pos.X + SMath.Cos(aa) * r, z = b.Pos.Z + SMath.Sin(aa) * r;
                    double sd = bb != null ? bb.SurfDist(x, z) : Math.Sqrt((x - b.Pos.X) * (x - b.Pos.X) + (z - b.Pos.Z) * (z - b.Pos.Z)) - b.Radius;
                    if (sd >= r0 + u.Radius) { if (Nav.IsFree(x, z)) return (x, z); break; }
                }
            }
        }
        return (b.Pos.X, b.Pos.Z);
    }

    partial void BuildUpdate(Unit u, double dt)
    {
        var t = u.Task; var b = t.Building;
        if (t.Repair) { RepairUpdate(u, dt); return; }
        if (b == null || !b.Alive || b.Built) { var done = b; ReleaseTask(u); u.Task = UnitTask.Idle(); u.BusyAnim = false; u.Anim!.StopLoop(); if (done != null && done.Alive && done.Built) AutoWork(u, done); return; }
        if (t.Phase == "go")
        {
            bool arrived = t.BSlot is { } sl ? Math.Sqrt((u.Pos.X - sl.X) * (u.Pos.X - sl.X) + (u.Pos.Z - sl.Z) * (u.Pos.Z - sl.Z)) < 1.2 : u.EdgeDist(b) < 1.8;
            if (arrived || u.Path.Count == 0) { t.Phase = "work"; u.Path = new NavPath(); }
            else { double sp = u.Steer(dt, u.Speed, 0.5); u.MoveAnim(sp, false); return; }
        }
        u.Vel = V3.Zero;
        u.Face(b.Pos.X, b.Pos.Z, dt);
        WorkAnim(u, b);
    }

    static string HammerOf(Unit u) => $"{(u.Owner != null ? u.Owner.Tribe : u.Def!.Tribe).ToLowerInvariant()}_hammer";

    /// <summary>
    /// The builder's clip (BuildUp.usl:184-188, Repair.usl:241-242): "hammer" with the tribe's hammer in the right hand,
    /// a trap (CTrap: the pitfall, the snare, the minefield, the quicksand, the poison dung, the resin fields) "disembowel"
    /// with empty hands. SetSLEAnim(clip, 9999, intro, outro): its start part once, its loop part while the work goes on;
    /// the end part when the work is done (AnimActionStopLoop: BuildUpdate / RepairUpdate, or the idle unit's IdleTick).
    /// </summary>
    void WorkAnim(Unit u, Building b)
    {
        bool trap = Data.IsA(b.Name, "CTrap");
        var clip = trap ? u.Anim!.Pick("disembowel", "hammer", "hacking") : u.Anim!.Pick("hammer", "hacking");
        u.AttachTool(clip == "disembowel" ? null : HammerOf(u));
        u.Anim.Play(clip);
    }

    // the building's side of construction (CBuildUpBuilding): progress, hit points, construction levels, finish
    partial void ConstructionUpdate(Building b, double dt)
    {
        var active = b.Builders.ToList().Where(w => w.Alive && w.Task.Type == "build" && w.Task.Building == b && w.Task.Phase == "work" && !w.Task.Repair).ToList();
        if (active.Count == 0) return;
        int N = active.Count;
        var act = b.Action ?? BuildActionOf(b);
        double D = act != null && act.Time > 0 ? act.Time : 10;
        double tf = active.Min(w => SelfTF(w));
        double S = 0.1 + 0.9 * SMath.Pow(0.8, N - 1);
        double rate = 1;
        // Tesla (level 2+) within 20 m doubles the speed, Babbage at level 5 anywhere doubles it again
        // (copies of them too: Rules.Kind)
        foreach (var h in Units)
        {
            if (!h.Alive || h.Owner != b.Owner) continue;
            var k = Data.Kind(h.Name);
            if (k == "tesla_s0" && h.Level >= 2 && b.SurfDist(h.Pos.X, h.Pos.Z) < 20) rate *= 2;
            if (k == "babbage_s0" && h.Level >= 5) rate *= 2;
        }
        if (b.Owner?.Cheat != null && b.Owner.Cheat.Speed > 0) rate *= b.Owner.Cheat.Speed;      // (debug option: game/cheats.js)
        double total = D * tf * S;          // (the computer player's time handicap is Action.usl's: production and research only)
        AddProgress(b, dt * rate / total, active);
    }

    /// <summary>CBuildUpBuilding.AddProgress: k of the whole (0..1), the hit points along with it</summary>
    public void AddProgress(Building b, double k, List<Unit>? active = null)
    {
        active ??= new List<Unit>();
        if (b.Built) return;
        b.Progress = Math.Min(1, b.Progress + k);
        b.Hp = Math.Max(1, Math.Min(b.MaxHp, b.Hp + b.MaxHp * k));
        // construction level 1 reached: the building starts blocking the pathfinder
        // (a gate: only the solid parts beside its door, Building.SolidCells - the door is a gate cell from the placement on,
        // World.SetGateCells; a gate built on the map marked them, one put up by workers never did)
        if (b.Progress >= 0.25 && !b.Blocking) { b.SetBlocking(true); if (!b.Def!.Gate) ClearFootprint(b, active); }
        b.UpdateVisual();
        SiteProgressed(b);                                  // (SetProcess: MIRAGE's great cannon's rotator, the pads' build-ups)
        if (b.Progress >= 1) FinishBuilding(b, active);
    }

    public void FinishBuilding(Building b, List<Unit>? workers = null)
    {
        workers ??= new List<Unit>();
        b.Built = true; b.Hp = Math.Max(b.Hp, b.MaxHp * 0.999);
        if (b.Tile != null) Walls?.SetReady(b);              // (WallMap.SetReady: the built bits)
        if (!b.Blocking) { b.SetBlocking(true); if (!b.Def!.Gate) ClearFootprint(b, new List<Unit>()); }
        b.UpdateVisual();
        EntityFilters(b, true);
        // resultactions of the build action (e.g. aje_medium_farm = small farm + the "medium farm" mode)
        var act = b.Action ?? BuildActionOf(b);
        if (act != null) foreach (var r in act.Results) foreach (var ra in r.ResultActions) ApplyResultAction(b, ra);
        RecomputeCapsOf(b.Owner);
        b.Behaviour?.Built(b, this);   // (a gate's passable cells, a warp gate's timer ...)
        Emit("built", new Args { ["building"] = b });
        foreach (var w in workers) { ReleaseTask(w); w.Task = UnitTask.Idle(); w.BusyAnim = false; w.Anim!.StopLoop(); AutoWork(w, b); }
    }

    /// <summary>the action that puts the building up; a building of another tribe the player was given (mission 9's
    /// launch ramp, played with another tribe) is put up as its own tribe does it</summary>
    public ActionDef? BuildActionOf(Building b)
    {
        static ActionDef? Find(List<ActionDef> list, string name) => Rules.Rules.OwnActionOf(list, name, a => a.Cat == "Build/BLDG");
        var ix = Data.Info(b.Name);
        return Find(Data.Actions(b.Owner), b.Name) ?? (b.Owner != null && ix != null && !string.IsNullOrEmpty(ix.Tribe) && ix.Tribe != b.Owner.Tribe ? Find(Data.ForeignActions(b.Owner, ix.Tribe), b.Name) : null);
    }

    /// <summary>after finishing (CCharacter.NextJob, character.usl:1234-1280): the next wall piece (BuildUp.usl:413-440);
    /// farm the new field or slaughterhouse; quarries send their builders for stone (100 m), lumberjack cottages and
    /// lumber mills for wood, the hunting lodge for food (100 m); MIRAGE's storehouses for the nearest resource they
    /// take; any other building is repaired if damaged, else the builders look for an unfinished building within 50 m</summary>
    public void AutoWork(Unit w, Building b)
    {
        // the queued tiles of a wall line first (Q_Build, in list order: PlaceWall 0x490920)
        while (w.BuildQueue != null && w.BuildQueue.Count > 0)
        {
            var x = w.BuildQueue[0]; w.BuildQueue.RemoveAt(0);
            if (x != null && x.Alive && !x.Built) { StartBuildAt(w, x); return; }
        }
        w.BuildQueue = null;
        if (b.Def!.WallKind == "wall" || b.Def.WallKind == "gate")
        {
            Building? next = null; double nd = 50;
            foreach (var x in Buildings)
            {
                if (!x.Alive || x.Built || x.Owner != w.Owner || x == b || (x.Def!.WallKind != "wall" && x.Def.WallKind != "gate")) continue;
                double d = Math.Sqrt((x.Pos.X - w.Pos.X) * (x.Pos.X - w.Pos.X) + (x.Pos.Z - w.Pos.Z) * (x.Pos.Z - w.Pos.Z));
                if (d < nd) { nd = d; next = x; }
            }
            if (next != null) { StartBuildAt(w, next); return; }
        }
        bool own = b.Owner == w.Owner; var k = Data.Kind(b.Name); bool mirage = Data.Extra.Mod == "MIRAGE";
        bool Near(string res, double r) { var n = NearestResource(w.Pos.X, w.Pos.Z, res, r); if (n != null) StartGatherAt(w, n); return n != null; }
        if (IsFarm(b)) { if (own) StartGatherAt(w, b); return; }
        if (QuarryRe.IsMatch(k)) { Near("stone", 100); return; }
        if (k == "hu_lumberjack_cottage" || k == "ninigi_lumbermill") { Near("wood", 64); return; }
        if (k == "ninigi_hunting_lodge") { Near("food", 100); return; }
        if (mirage && b.IsDropoff && MirageStoreRe.IsMatch(k))
        {
            foreach (var r in new[] { "wood", "stone", "food" }) if (b.Def.Delivery.Contains(r) && Near(r, 40)) return;
            return;
        }
        if (b.Hp < b.MaxHp * 0.998) { StartRepairAt(w, b); return; }      // (finishBuilding leaves at least 99.9 %)
        if (b.Def.WallKind == "wall") return;
        var nx = Buildings.FirstOrDefault(x => x.Alive && !x.Built && x.Owner == w.Owner && x != b && x.DistTo(b) < 50 && (x.Def!.WallKind != null || x.Builders.Count == 0));
        if (nx != null) StartBuildAt(w, nx);
    }

    /// <summary>the work a target means for a worker (CCharacter.NextJob): an unfinished own or friendly building - build
    /// it; a finished one - what its builders do (AutoWork); a resource - gather it. false: nothing to do</summary>
    public bool NextJob(Unit w, Entity? t)
    {
        if (t == null || !t.Alive || !w.IsWorker) return false;
        if (t is ResNode) { StartGatherAt(w, t); return true; }
        if (t is not Building b || b.Owner == null || !w.Owner!.IsFriend(b.Owner)) return false;
        if (!b.Built) StartBuildAt(w, b); else AutoWork(w, b);
        return true;
    }

    // a new worker that reached a rally point on open ground (CTheLite "exit"): a tree within 5 m, stone within 3 m,
    // fruit or a carcass within 10 m; else a building within 10 m - an unfinished one: the nearest unfinished building
    // to build, a finished one: the nearest damaged one to repair (SearchBuildingToBuildUp / SearchBuildingToRepair)
    partial void RallyJob(Unit u, double x, double z)
    {
        var w = u;
        if (!w.IsWorker) return;
        foreach (var (res, r) in new[] { ("wood", 5.0), ("stone", 3.0), ("food", 10.0) })
        {
            var n = NearestResource(x, z, res, r);
            if (n != null) { StartGatherAt(w, n); return; }
        }
        Building? b = null; double bd = 10;
        BHash.Query(x, z, 10 + 40, e => { if (!e.Alive) return; double d = e.SurfDist(x, z); if (d < bd) { bd = d; b = e; } });
        if (b == null) return;
        bool Own(Building e) => e.Alive && e.Owner != null && w.Owner!.IsFriend(e.Owner);
        Building? Pick(Func<Building, bool> ok) => Buildings.Where(e => Own(e) && ok(e) && e.DistTo(w) < 50).OrderBy(e => e.DistTo(w)).FirstOrDefault();
        var t = !b.Built ? Pick(e => !e.Built) : Pick(e => e.Built && e.Hp < e.MaxHp * 0.998);
        if (t != null && !t.Built) StartBuildAt(w, t); else if (t != null) StartRepairAt(w, t);
    }

    // ------------------------------------------------------------------ repair (Repair.usl)
    partial void StartRepair(Unit u, Entity? target) => StartRepairAt(u, target as Building);

    public void StartRepairAt(Unit u, Building? b)
    {
        if (!u.CanBuild || b == null || !b.Alive || !b.Built || b.Hp >= b.MaxHp || b.Owner != u.Owner) return;
        u.Task = new UnitTask("build") { Repair = true, Building = b, Phase = "go", BSlot = b.OccupySlot(u), User = u.Task.User };
        b.Builders.Add(u);
        var (x, z) = u.Task.BSlot is { } sl ? (sl.X, sl.Z) : ApproachPoint(u, b);
        u.SetPath(x, z);
    }

    void RepairUpdate(Unit u, double dt)
    {
        var t = u.Task; var b = t.Building;
        if (b == null || !b.Alive || b.Hp >= b.MaxHp)
        {
            ReleaseTask(u); u.Task = UnitTask.Idle(); u.BusyAnim = false; u.Anim!.StopLoop();
            // continue with the next damaged building (walls: next damaged segment) within 50 m
            var next = b != null ? Buildings.FirstOrDefault(x => x.Alive && x.Built && x.Owner == u.Owner && x.Hp < x.MaxHp && x.DistTo(b) < 50) : null;
            if (next != null) StartRepairAt(u, next);
            return;
        }
        if (t.Phase == "go")
        {
            bool arrived = t.BSlot is { } sl ? Math.Sqrt((u.Pos.X - sl.X) * (u.Pos.X - sl.X) + (u.Pos.Z - sl.Z) * (u.Pos.Z - sl.Z)) < 1.2 : u.EdgeDist(b) < 1.8;
            if (arrived || u.Path.Count == 0) { t.Phase = "work"; u.Path = new NavPath(); }
            else { double sp = u.Steer(dt, u.Speed, 0.5); u.MoveAnim(sp, false); return; }
        }
        u.Vel = V3.Zero;
        u.Face(b.Pos.X, b.Pos.Z, dt);
        WorkAnim(u, b);
        // cost per hit point: 50 % of the build cost spread over the building's hit points; stops when a resource runs out
        double hp = Math.Min(b.MaxHp - b.Hp, (20 + 5 * (u.Level - 1)) * dt);
        var cost = b.Paid ?? b.Action?.Cost ?? BuildActionOf(b)?.Cost ?? new Cost();
        var p = u.Owner!;
        b.RepairDebt ??= new double[3];
        // the toolkit artefact: repairs cost its player nothing (Repair.usl, character.usl ms_iRepairNoCost)
        var R = Rules.Rules.RES;
        if (RepairNoCost != p) for (int i = 0; i < R.Length; i++)
            {
                var r = R[i];
                if (cost[r] == 0) continue;
                if (p[r] <= 0) { ReleaseTask(u); u.Task = UnitTask.Idle(); Emit("msg", new Args { ["player"] = p, ["text"] = "Not enough resources to repair" }); return; }
                b.RepairDebt[i] += 0.5 * cost[r] * hp / b.MaxHp;
                double whole = Math.Floor(b.RepairDebt[i]);
                if (whole > 0) { p[r] -= whole; b.RepairDebt[i] -= whole; }
            }
        b.Hp += hp;
    }

    // ------------------------------------------------------------------ BuildDown (Aje) and self destruction
    /// <summary>BuildDown (CBuildDownBuilding) is an item of the building's queue: it waits behind what is queued before
    /// it, shows in the queue with its progress (CurProcess), and a click on it there cancels it (RemoveAction). From the
    /// moment it is queued the filter /Filters/Aje/Upgrades/&lt;class&gt;/BuildDown takes away the building's other
    /// actions. a: the BuildDown move action. null = queued, else "busy" | "full"</summary>
    public string? StartBuildDown(Building b, ActionDef a)
    {
        if (!b.Alive || !b.Built) return "busy";
        if (b.Queue.Any(q => q.BuildDown)) return null;                // Building.BuildDown: once
        if (b.Queue.Count >= 6) return "full";
        var act = BuildActionOf(b);
        b.Queue.Add(new QueueItem { Action = a, BuildDown = true, T = 0, Total = 0.5 * (act != null ? act.Time : 20), Cost = act?.Cost, Level = 0 });
        BuildDownFilter(b, true);
        return null;
    }
    void BuildDownFilter(Building b, bool on) => LocalFilter(b, $"{b.Def!.Tribe}/Upgrades/{b.Name}/BuildDown", on);

    /// <summary>the BuildDown item at the front of the queue: GrantResources (the full build cost back, capped by
    /// storage), then the building vanishes without ruins</summary>
    void BuildDownDone(Building b, QueueItem item)
    {
        if (b.Owner != null && item.Cost != null) b.Owner.Refund(new Cost { Food = item.Cost.Food, Wood = item.Cost.Wood, Stone = item.Cost.Stone });
        BuildDownFilter(b, false);
        b.Queue.Remove(item);
        b.SilentDeath = true;
        KillBuilding(b, null);
    }

    /// <summary>"Kill" action on own buildings (Building.usl Action "/Kill" -> DiePerHarakiri = OnKill(); Die()): no
    /// refund, an unfinished site's cost is lost too</summary>
    public void SelfDestruct(Building b)
    {
        if (!b.Alive) return;
        b.SilentDeath = !b.Built;
        KillBuilding(b, null);
    }

    // ------------------------------------------------------------------ destruction (Die / Delete / BuildingCorpse)
    /// <summary>a building died (combat.js kill calls it): its cells, its queue and builders, the pyramid, the wall grid,
    /// its ruin "&lt;gfx&gt;_dest" for 8 s - or nothing at all (a silent death)</summary>
    public void BuildingDestroyed(Building e, Entity? att)
    {
        BHash.Remove(e);
        (e.Behaviour as BuildingBehaviour)?.Destroyed(e, this);      // (systems/buildings.js: gates free their cells, a Scorpion panel)
        Emit("ground", new Args { ["x"] = e.Pos.X, ["z"] = e.Pos.Z, ["r"] = e.Radius + 2 });
        e.SetBlocking(false);
        Reserve(e.Cells, -1);
        e.RemoveCranes();
        foreach (var w in e.Builders.ToList()) if (w.Task.Building == e) { ReleaseTask(w); w.Task = UnitTask.Idle(); }
        var p = e.Owner;
        if (p != null) foreach (var q in e.Queue) { if (!q.BuildDown) p.Refund(q.Action.Cost); if (q.Level != 0) { p.QueuedAtLevel[q.Level - 1]--; p.QueuedUnits--; } }
        if (p != null && e.CountsInPop) { p.Units--; p.AtLevel[e.Level - 1]--; }
        e.Queue = new List<QueueItem>();
        foreach (var w in e.Workers.ToList()) if (w.Task.Farm == e) w.Task = UnitTask.Idle();
        if (e.Passengers != null) UnloadAll(e, false);
        RecomputeCapsOf(p);
        e.DeadT = 0;
        // Ninigi buildings with the "Explode" invention blow up: 500 -> 100 damage in 20 m (FO:5578)
        if (e.LocalInventsSet != null && e.LocalInventsSet.Has("Explode")) AreaDamage(null, e.Pos, 20, 500, 100, new AreaOpts { Owner = p, Direct = true, Size = 7.0 });
        // finished buildings (not walls) leave their ruin model "<gfx>_dest" for 8 s, and their turrets theirs
        bool ruins = e.Built && !e.SilentDeath && e.Def!.WallKind != "wall";
        // (MIRAGE's fake corpses first - CBuilding.CreateBuildingCorpse: the building itself, CustomBuildUps.cs)
        if (!(ruins && FakeCorpse(e)))
        {
            e.Ruin = ruins ? RuinOf(e) : null;
            e.RuinParts = ruins ? RuinPartsOf(e) : null;
            if (ruins && e.Ruin == null) AmazonTempleCorpse(e);                // (MIRAGE: the temple itself, MirageClasses.cs)
        }
        e.RuinStays = e.Ruin != null && PermanentRuin(e.Ruin, e.Name);
        // the wall grid: the characters on a piece fall, its siege object loses its hold, neighbours lose their arm
        // towards it; a gate frees its wings (systems/wallwalk.js, siege.js)
        if (e.Tile != null && e.Def!.WallKind == "wall") WallPieceGone(e);
        SiegeGoneEntity(e);
        if (e.Tile != null) Walls?.Remove(e);
        if (e.Wings != null) { foreach (var w in e.Wings) if (w.ParentGate == e) w.ParentGate = null; e.Wings = null; }
        if (e.SilentDeath) e.Removed = true;
        e.LookChanged();
        Emit("destroyed", new Args { ["entity"] = e, ["killer"] = att, ["silent"] = e.SilentDeath });
    }

    /// <summary>combat.js kill for a building: Kill, which calls BuildingDestroyed (the systems' own kills go through
    /// here so that the building is taken off the map and the wall grid also while combat's Kill is a stand-in that
    /// only marks it dead)</summary>
    public void KillBuilding(Building b, Entity? att)
    {
        Kill(b, att);
        if (!b.Alive && b.HashKey != SpatialHash<Building>.NoKey) BuildingDestroyed(b, att);
    }

    /// <summary>
    /// The ruins that stay on the map for good (Building.usl CBuildingCorpse.ShouldStay, base game and MIRAGE): the corpse
    /// of these models never sinks (its delete timer only sets the state to 0 - "HACK"). They are the SEAS headquarters'
    /// buildings of the campaign maps - the bunker (seas_hq_bunker), the big cannon and its rotator, the defense turret,
    /// the machine-gun nest - and the city gates (PT_Citywall_Gate, hcl13_gate). MIRAGE excepts its own classes that use
    /// those models (IsException by the corpse's source class: seas_fortress, seas_mg_nest, seas_small_cannon,
    /// seas_great_cannon and its rotator): their ruins sink as every other. MIRAGE's shore cannon (seas_hq_defense_turret)
    /// is not excepted: its ruin stays. A ruin is a dead building: it blocks nothing (its cells were freed at its
    /// destruction), cannot be selected, hit or built on top of differently than before - only drawn.
    /// </summary>
    public static bool PermanentRuin(string ruinGfx, string? sourceClass)
    {
        if (!STAY_RUINS.Contains(ruinGfx)) return false;
        return sourceClass == null || !STAY_EXCEPTIONS.Contains(sourceClass);
    }
    static readonly HashSet<string> STAY_RUINS = new(StringComparer.OrdinalIgnoreCase)
    {
        "hcl13_gate", "PT_Citywall_Gate_dest", "seas_hq_bunker_dest", "seas_hq_big_cannon_dest", "seas_hq_big_cannon_rotator_dest",
        "seas_hq_defense_turret_dest", "seas_hq_machinegun_nest_dest",
    };
    static readonly HashSet<string> STAY_EXCEPTIONS = new(StringComparer.OrdinalIgnoreCase)
    {
        "seas_fortress", "seas_mg_nest", "seas_small_cannon", "seas_great_cannon", "seas_great_cannon_rotator",
    };

    /// <summary>a permanent ruin keeps only the ruins of its parts that stay too (the big cannon's rotator); the others
    /// (the machine-gun nest's top, the defense turret's top) are gone once their own corpse would have sunk</summary>
    public void SettleRuinParts(Building b)
    {
        b.RuinPartsSettled = true;
        var keep = b.RuinParts!.Where(p => PermanentRuin(p.Gfx, b.Name) && (p.Parent == null || PermanentRuin(p.Parent.Gfx, b.Name))).ToList();
        if (keep.Count == b.RuinParts!.Count) return;
        b.RuinParts = keep.Count > 0 ? keep : null;
        b.LookChanged();
    }

    /// <summary>ruins of another name than "&lt;gfx&gt;_dest": MIRAGE's Ninigi large tower has no ruin of its own and
    /// leaves the smoke tower's (MIRAGE Building.usl CBuildingCorpse.Init / CTower.CorpseAvailable)</summary>
    static readonly Dictionary<string, string> RUIN_GFX = new(StringComparer.OrdinalIgnoreCase) { ["ninigi_large_tower"] = "ninigi_smoke_tower_dest" };

    /// <summary>the ruin model of a finished building (its gfx + "_dest", or RUIN_GFX), null: none</summary>
    public string? RuinOf(Building b)
    {
        var gfx = (b.Stats?.Gfx ?? "").ToLowerInvariant();
        var n = gfx + "_dest";
        if (!Models.Has(n) && RUIN_GFX.TryGetValue(gfx, out var alt)) n = alt;
        return Models.Has(n) ? n : null;
    }

    /// <summary>
    /// The ruins of a destroyed building's attached parts (null: none). The original makes a corpse of the tower's
    /// turret apart from the building's own (Building.usl CTower.CreateBuildingCorpse: BuildingCorpse.Init(turret gfx)
    /// -> "&lt;turret&gt;_dest", turned as the turret; CSeasBigCannon: the rotator; CRocketRamp: the top and the bird), and
    /// the corpse plays "destroy" (CBuildingCorpse.PlayAnim). For the SEAS machine-gun nest, the small cannon (defense
    /// turret) and the big cannon the whole collapse is in the turret's ruin - "seas_hq_machinegun_nest_dest" has no
    /// clip, "seas_hq_machinegun_nest_top_dest" has "destroy" -, so with the live turret kept on the ruin they showed no
    /// destruction. Every part with a "&lt;gfx&gt;_dest" model gets one (same link, parent and turn); parts without one go
    /// with the building (the original deletes its grouped children: the big cannon's barrel, cranes, the harbour's
    /// turtles). The ruin parts stand where the live parts hung - MIRAGE's fix for its towers (CTower.CreateBuildingCorpse
    /// at the turret's position: the base game put seas_turret_dest 10 m down at the tower's foot).
    /// </summary>
    public List<AttachedPart>? RuinPartsOf(Building b)
    {
        List<AttachedPart>? res = null;
        Dictionary<AttachedPart, AttachedPart>? made = null;
        foreach (var p in b.Parts)
        {
            if (string.IsNullOrEmpty(p.Gfx)) continue;
            // MIRAGE's decorations: the one that plays "destroy" stays (CSeasWarpGate / CSeasPortalTerminus.Die), the others go
            if (p.Spec?.Kind == "deco") { if (DecoRuin(p) is { } dr) (res ??= new()).Add(dr); continue; }
            var n = (p.Gfx + "_dest").ToLowerInvariant();
            if (!Models.Has(n)) continue;
            AttachedPart? parent = null;
            if (p.Parent != null && (made == null || !made.TryGetValue(p.Parent, out parent))) continue;   // (its parent left no ruin)
            var r = new AttachedPart
            {
                Spec = p.Spec, Gfx = n, Link = p.Link, Parent = parent, Fourcc = Models.Fourcc(n), Yaw = p.Yaw,
                VisLevel = p.VisLevel, VisDmg = p.VisDmg, Night = p.Night, From = p.Gfx,
            };
            (res ??= new()).Add(r);
            (made ??= new())[p] = r;
        }
        return res;
    }

    // ------------------------------------------------------------------ the ground under new buildings (main.js W.adaptTerrain)
    /// <summary>
    /// The map setup's ground levelling: AdaptTerrain levels the height field under a new building with its ground brush
    /// (Sim/Maps Ground.LevelGround) and paints the brush's materials into the 4 m material cells (Ground.BrushCells).
    /// The world emits "terrain" {gfx, x, z, rot, rect (the height samples changed, SampleRect or null), cells
    /// (List&lt;BrushCell&gt;), r} - the view redraws the terrain mesh and splat there - and "ground" {x, z, r} (the grass
    /// follows the new ground). hf must be the height field the map's Height reads.
    /// </summary>
    public void UseGround(HeightField hf, GroundBrushes brushes)
    {
        var cellOf = GroundBrushes.MaterialCellOf(hf.Size);
        BuildingGround.Use(brushes, Data, Models);
        AdaptTerrain = (gfx, x, z, rot) =>
        {
            var b = brushes.Get(gfx);
            if (b == null) return false;
            var rect = Ground.LevelGround(hf, b, brushes.Mpp, x, z, rot);
            var cells = Ground.BrushCells(b, brushes.Mpp, x, z, rot, cellOf);
            double R = Math.Sqrt((double)b.W * b.W + (double)b.H * b.H) * brushes.Mpp / 2 + 8;
            GroundLog.Add((gfx, x, z, rot));
            Emit("terrain", new Args { ["gfx"] = gfx, ["x"] = x, ["z"] = z, ["rot"] = rot, ["rect"] = rect, ["cells"] = cells, ["r"] = R });
            Emit("ground", new Args { ["x"] = x, ["z"] = z, ["r"] = R });   // the grass follows the new ground
            return true;
        };
    }
}
