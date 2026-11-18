// Walking on walls: characters on the walkway of the walkable walls (wallclassconfig "Walkable": hu_small_wall,
// hu_re_enforced_wall, aje_clay_wall, ninigi_palisade), getting up and down, fighting from there, falling when the
// piece under them is destroyed - and the siege objects that dock to a wall (systems/siege.js: ladders, the siege
// tower).
//
// Reference: docs/spec/walls_walk.md (character.usl, FightingObj.usl, task/misc.usl, task/DockWall.usl,
// GameInputController.usl; PWServer.exe CActWallWalk: a unit gets on and off a wall with a jump, anim "jump_wall",
// speed 20).
//
//   * The walkway: the built walkable wall pieces of the wall grid (game/wallmap.js), connected as their arms are
//     (towers and gates are not part of it). The top is WALL_TOP above the ground at the tile centre.
//   * Only characters go up ("SetCanUseSiege", CH:640). A right click on an own walkable wall ("Use", GIC:841) sends
//     them up: they walk to a free side of the piece and jump on it; on the walkway they walk along the pieces.
//     Any order to the ground takes them along the walkway to the piece nearest the goal and down with a jump.
//     Enemy walls are entered only through a docked ladder (systems/siege.js).
//   * On the wall: +Defence_On_Wall ranged protection (tech tree MiscValues, 50; FO:7959), no knockback (FO:4383),
//     no walk back to the post (FO:2393); melee fighters can't reach across the wall's height (the height rule of
//     combat.js rangeZone), shooters can.
//   * A destroyed piece drops the characters on it: their order ends, they fall (hit_front, getting_up) and take
//     (height x 5) damage, reduced by melee protection (CH:2165-2174).
//
// Unit state: u.WallTile = the tile it is on while on the walkway (u.OnWall: the piece there); u.WPath = the tile
// centres it walks to; u.WJump = {from, to, y0, y1, t, T, then} while jumping up or down.
//
// Ported from remake/src/game/systems/wallwalk.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using WMap = PwRemake.Sim.World.WallMap;

namespace PwRemake.Sim.World;

public sealed partial class WallJump
{
    public double FromX, FromZ, ToX, ToZ, Y0, Y1, T, TT;
    /// <summary>the walkway tile it lands on (null: the ground)</summary>
    public (int I, int J)? Onto;
}

/// <summary>the way down from a walkway (wallwalk.js wallExit): the tiles to walk, the ground point, or a ladder</summary>
public sealed class WallExit
{
    public List<(int I, int J)> Route = new();
    public (double X, double Z) Ground;
    public Entity? Ladder;
    public (int I, int J) Tile;
}

public sealed partial class UnitTask
{
    /// <summary>wall: the piece to go up on, the ground point beside it, where the order leads, the way down</summary>
    public Building? Wall;
    public (double X, double Z)? Side, Dest;
    public WallExit? Exit;
}

public sealed partial class Unit
{
    /// <summary>the walkway tile the unit is on (with OnWall = the piece there)</summary>
    public (int I, int J)? WallTile;
}

public sealed partial class World
{
    /// <summary>the walkway above the ground at the tile centre (the models: 7.6 .. 7.9 m)</summary>
    public const double WALL_TOP = 7.8;
    const double FALL_DMG = 5;                   // per metre of the fall (CH:2170)
    const double SIDE = 4.5;                     // a ground point beside a piece: 4 m from its centre (DW:614) and a little more
    static readonly HashSet<string> STAY = new() { "idle", "hold", "wall", "attack", "finish", "special", "cast", "heal", "entrench", "climb" };
    HashSet<string>? _walkable;

    HashSet<string> WalkableNames() => _walkable ??= new HashSet<string>(Data.Extra.WalkableWalls.Select(s => s.ToLowerInvariant()));

    /// <summary>a wall piece units walk on: a finished piece of a walkable class</summary>
    public bool WalkableWall(Building? b) =>
        b != null && b.Alive && b.Built && b.Def != null && b.Def.WallKind == "wall" && (WalkableNames().Contains(b.Name.ToLowerInvariant()) || WalkableNames().Contains((b.Stats?.Gfx ?? "").ToLowerInvariant()));
    /// <summary>may u climb up: onto a walkway, a ladder, a docked siege brachiosaurus (not a statue: IsStatue -
    /// MIRAGE's CCharacter.CannotClimbSiege; climbing down stays allowed)</summary>
    public static bool CanClimb(Unit? u) => u != null && u.Alive && u.Cls == "CHTR" && !u.Inside && !u.Flying && !u.Naval && !u.Parked && !IsStatue(u);
    /// <summary>
    /// The Norsemen's statues: hu_berserk_statue_01..04 (MIRAGE's colossi), hu_avatar, hu_stone_statue. The original's
    /// BoardTransport and EnterBunker refuse them, MIRAGE's mirage_statues_no_boarding climbing up siege objects too
    /// (CCharacter.CannotClimbSiege): no boarding, no bunker, no climbing up (CanBoard, CanClimb).
    /// </summary>
    public static bool IsStatue(Entity? e) => e != null && (e.Name.StartsWith("hu_berserk_statue_", StringComparison.Ordinal)
        || e.Name is "hu_avatar" or "hu_stone_statue");
    /// <summary>the walkway piece of tile (i, j) or null</summary>
    public Building? Walkway(int i, int j)
    {
        var t = Walls?.At(i, j);
        return t != null && t.Wall != null && t.Gate == null && t.Tower == null && WalkableWall(t.Wall) ? t.Wall : null;
    }
    /// <summary>the walkway tiles next to (i, j): along the piece's arms</summary>
    public List<(int I, int J)> WalkwayNext(int i, int j)
    {
        var o = new List<(int, int)>(); int m = Walls!.Mask(i, j);
        for (int d = 0; d < 8; d++) if (((m >> d) & 1) != 0) { int di = WMap.DIRS[d][0], dj = WMap.DIRS[d][1]; if (Walkway(i + di, j + dj) != null) o.Add((i + di, j + dj)); }
        return o;
    }
    public double WallTop(int i, int j) { var (x, z) = Walls!.Centre(i, j); return Height(x, z) + WALL_TOP; }

    /// <summary>puts a unit on the walkway tile (i, j) (u.onWall = {i, j}); null: off the wall</summary>
    void SetWallTile(Unit u, (int I, int J)? t)
    {
        u.WallTile = t;
        u.OnWall = t is var (i, j) ? (Entity?)Walkway(i, j) ?? Walls?.At(i, j)?.Wall : null;
    }

    /// <summary>the height of the walkway under a unit on it: between the tops of the two tile centres it walks between</summary>
    public double WallY(Unit u)
    {
        var WM = Walls;
        if (WM == null || u.WallTile == null) return Height(u.Pos.X, u.Pos.Z);
        var here = WM.TileOf(u.Pos.X, u.Pos.Z);
        var (i, j) = Walkway(here.I, here.J) != null ? here : u.WallTile.Value;
        var (cx, cz) = WM.Centre(i, j); double ox = u.Pos.X - cx, oz = u.Pos.Z - cz, o = Math.Sqrt(ox * ox + oz * oz);
        double y0 = WallTop(i, j);
        if (o < 0.3) return y0;
        int d = (((int)Math.Floor(SMath.Atan2(-oz, ox) / (Math.PI / 4) + 0.5) % 8) + 8) % 8; int di = WMap.DIRS[d][0], dj = WMap.DIRS[d][1];
        if (Walkway(i + di, j + dj) == null) return y0;
        double L = Math.Sqrt(di * di + dj * dj) * 8;
        return y0 + (WallTop(i + di, j + dj) - y0) * Math.Min(1, o / L);
    }

    /// <summary>the walkway tiles from a to b (breadth first; null: not connected)</summary>
    public List<(int I, int J)>? WallRoute((int I, int J) a, (int I, int J) b)
    {
        if (a == b) return new List<(int, int)> { a };
        var prev = new Dictionary<(int, int), (int, int)?> { [a] = null };
        var q = new Queue<(int, int)>(); q.Enqueue(a);
        while (q.Count > 0)
        {
            var t = q.Dequeue();
            foreach (var n in WalkwayNext(t.Item1, t.Item2))
            {
                if (prev.ContainsKey(n)) continue;
                prev[n] = t;
                if (n == b)
                {
                    var o = new List<(int, int)> { n };
                    for ((int, int)? p = t; p != null; p = prev[p.Value]) o.Insert(0, p.Value);
                    return o;
                }
                q.Enqueue(n);
            }
        }
        return null;
    }
    /// <summary>all walkway tiles connected to tile a</summary>
    public List<(int I, int J)> WalkwayOf((int I, int J) a)
    {
        var seen = new OrderedSet<(int, int)> { a }; var q = new Queue<(int, int)>(); q.Enqueue(a);
        while (q.Count > 0) { var t = q.Dequeue(); foreach (var n in WalkwayNext(t.Item1, t.Item2)) if (seen.Add(n)) q.Enqueue(n); }
        return seen.ToList();
    }
    /// <summary>the free ground points beside a piece (its sides without an arm), nearest to (x, z) first</summary>
    public List<(double X, double Z)> WallSides(int i, int j, double x, double z)
    {
        var (cx, cz) = Walls!.Centre(i, j); int m = Walls.Mask(i, j); var o = new List<(double X, double Z)>();
        for (int d = 0; d < 8; d++)
        {
            if (((m >> d) & 1) != 0) continue;
            int di = WMap.DIRS[d][0], dj = WMap.DIRS[d][1]; double L = Math.Sqrt(di * di + dj * dj), px = cx + di / L * SIDE, pz = cz + dj / L * SIDE;
            if (Nav.IsFree(px, pz)) o.Add((px, pz));
        }
        return o.OrderBy(p => Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z))).ToList();
    }
    /// <summary>may u get onto the walkway of piece w on its own (no ladder): own walls only (GIC:826-836 "Use")</summary>
    public bool MayUseWall(Unit u, Entity? w) => CanClimb(u) && w is Building b && WalkableWall(b) && b.Owner == u.Owner;

    // ------------------------------------------------------------------ orders
    /// <summary>World.Order for a unit on the walkway: true = handled here</summary>
    public bool WallOrder(Unit u, Order o)
    {
        var here = u.WallTile ?? (0, 0);
        switch (o.Type)
        {
            case "stop": case "hold":
                u.Task = new UnitTask(o.Type); u.WPath = new List<Waypoint>(); u.Path = new NavPath(); u.Vel = V3.Zero;
                return true;
            case "use":
            {
                var w = o.Target as Building;
                if (!WalkableWall(w) || w!.Tile == null) return true;
                var route = WallRoute(here, w.Tile.Value);
                if (route != null) { u.Task = new UnitTask("wall") { Phase = "walk", User = !o.Auto }; u.WPath = route.Select(t => C(t)).ToList(); return true; }
                u.Task = new UnitTask("wall") { Phase = "leave", Dest = (w.Pos.X, w.Pos.Z), Then = o, User = !o.Auto };
                return true;
            }
            case "climb":
            {
                // down the ladder of a piece of this walkway
                var s = o.Target;
                var dt = s != null ? DockTileOf(s) : null;
                var route = s != null && dt != null && MayUseSiege(u, s) ? WallRoute(here, dt.Value) : null;
                if (route == null) return true;
                u.Task = new UnitTask("wall") { Phase = "leave", Exit = new WallExit { Ladder = s, Tile = dt!.Value, Route = route }, Dest = (s!.Pos.X, s.Pos.Z), User = !o.Auto };
                u.WPath = route.Select(t => C(t)).ToList();
                return true;
            }
            case "attack": return false;           // fights from the wall (attackUpdate: wallChase)
            case "unload": case "kill": case "rallyJob": return false;
        }
        // everything else happens on the ground: down first, then the order
        u.Task = new UnitTask("wall") { Phase = "leave", Dest = OrderDest(u, o), Then = o, User = !o.Auto };
        u.WPath = new List<Waypoint>();
        return true;
    }
    Waypoint C((int I, int J) t) { var (x, z) = Walls!.Centre(t.I, t.J); return new Waypoint(x, z); }

    static (double X, double Z) OrderDest(Unit u, Order o)
    {
        if (o.Type is "move" or "attackmove" or "rallyJob") return (o.X, o.Z);     // (orders with a point: o.x)
        if (o.Pts != null) return o.Pts[o.I % o.Pts.Count];
        var t = o.Target;
        return t != null ? (t.Pos.X, t.Pos.Z) : (u.Pos.X, u.Pos.Z);
    }

    // "Use" on an own walkable wall from the ground: up there
    partial void UseWall(Unit u, Entity? target, bool user) => UseWallOrder(u, target, user);

    public bool UseWallOrder(Unit u, Entity? w, bool user = true)
    {
        if (!MayUseWall(u, w)) return false;
        u.Task = new UnitTask("wall") { Phase = "up", Wall = (Building)w!, User = user };
        u.WPath = new List<Waypoint>();
        return true;
    }

    // ------------------------------------------------------------------ the task 'wall'
    // phases: 'up' (walk to a side of the piece, jump), 'walk' (along the walkway), 'leave' (along the walkway to the
    // piece nearest the goal, jump down, then the order)
    partial void WallUpdate(Unit u, double dt)
    {
        var t = u.Task;
        if (u.WJump != null) return;
        if (t.Phase == "up")
        {
            var w = t.Wall;
            if (!MayUseWall(u, w) || w!.Tile == null) { ToIdle(u); return; }
            var (ti, tj) = w.Tile.Value;
            if (t.Side == null)
            {
                var sides = WallSides(ti, tj, u.Pos.X, u.Pos.Z);
                if (sides.Count == 0) { ToIdle(u); return; }
                t.Side = sides[0];
                u.SetPath(t.Side.Value.X, t.Side.Value.Z);
            }
            double d = Math.Sqrt((t.Side.Value.X - u.Pos.X) * (t.Side.Value.X - u.Pos.X) + (t.Side.Value.Z - u.Pos.Z) * (t.Side.Value.Z - u.Pos.Z));
            if (d > 1.2 && u.Path.Count > 0) { u.MoveAnim(u.Steer(dt, t.User ? u.RunSpeed : u.Speed, 0.5), t.User); return; }
            if (d > 3) { ToIdle(u); return; }        // no way there
            var (cx, cz) = Walls!.Centre(ti, tj);
            WallJumpTo(u, cx, cz, true, (ti, tj));
            t.Phase = "walk";
            return;
        }
        if (u.WallTile == null) { ToIdle(u); return; }
        if (t.Phase == "walk")
        {
            if (u.WPath.Count == 0)
            {
                var then = t.Then;
                ToIdle(u); u.Vel = V3.Zero;
                if (!u.BusyAnim) u.Anim!.Play(u.StandAnim());
                if (then != null) Order(u, then);
                return;
            }
            u.MoveAnim(WallSteer(u, dt, t.User ? u.RunSpeed : u.Speed), t.User);
            return;
        }
        if (t.Phase == "leave")
        {
            if (t.Exit == null)
            {
                t.Exit = WallExitOf(u, t.Dest ?? (u.Pos.X, u.Pos.Z));
                if (t.Exit == null) { ToIdle(u); return; }       // no way down (an enemy's wall without a ladder)
                u.WPath = t.Exit.Route.Select(x => C(x)).ToList();
            }
            if (u.WPath.Count > 0) { u.MoveAnim(WallSteer(u, dt, t.User ? u.RunSpeed : u.Speed), t.User); return; }
            var e = t.Exit; var then = t.Then ?? (t.Resume != null ? TaskOrder(t.Resume, t.Dest) : null);
            if (e.Ladder != null) { ClimbDown(u, e.Ladder, then); return; }
            WallJumpTo(u, e.Ground.X, e.Ground.Z, false, null, then);
        }
    }

    /// <summary>the order that starts a task again (a ground task begun on the wall, resumed after the way down)</summary>
    static Order? TaskOrder(UnitTask t, (double X, double Z)? dest)
    {
        bool auto = !t.User;
        switch (t.Type)
        {
            case "move": case "attackmove": return new Order(t.Type) { X = t.X, Z = t.Z, Then = t.Then, Auto = auto };
            case "gather": return t.Node != null ? new Order("gather") { Target = t.Node, Auto = auto } : new Order("deliver") { Target = t.Drop, Auto = auto };
            case "build": return t.Building != null ? new Order(t.Building.Built ? "repair" : "build") { Target = t.Building, Auto = auto } : null;
            case "board": case "fountain": case "pickup": case "trade": return t.Target != null ? new Order(t.Type) { Target = t.Target, Auto = auto } : null;
        }
        return dest is var (dx, dz) ? new Order("move") { X = dx, Z = dz, Auto = auto } : null;
    }

    /// <summary>the way down to the ground near dest: along the walkway to a piece with a free side (own walls) or a
    /// ladder of the unit's side, the one that leaves the shortest walk -> null: none</summary>
    WallExit? WallExitOf(Unit u, (double X, double Z) dest)
    {
        var here = u.WallTile!.Value;
        var tiles = WalkwayOf(here);
        WallExit? best = null; double bd = double.PositiveInfinity;
        foreach (var (i, j) in tiles)
        {
            var w = Walkway(i, j);
            var (cx, cz) = Walls!.Centre(i, j);
            var cand = new List<WallExit>();
            if (w != null && w.Owner == u.Owner) { var ss = WallSides(i, j, dest.X, dest.Z); if (ss.Count > 0) cand.Add(new WallExit { Ground = ss[0] }); }
            foreach (var l in LaddersAt(i, j)) if (MayUseSiege(u, l)) cand.Add(new WallExit { Ladder = l, Ground = (l.Pos.X, l.Pos.Z) });
            foreach (var c in cand)
            {
                double walk = Math.Sqrt((cx - u.Pos.X) * (cx - u.Pos.X) + (cz - u.Pos.Z) * (cz - u.Pos.Z)) * 0.5 + Math.Sqrt((c.Ground.X - dest.X) * (c.Ground.X - dest.X) + (c.Ground.Z - dest.Z) * (c.Ground.Z - dest.Z));
                if (walk < bd) { bd = walk; c.Tile = (i, j); best = c; }
            }
        }
        if (best == null) return null;
        best.Route = WallRoute(here, best.Tile) ?? new List<(int, int)> { best.Tile };
        return best;
    }

    /// <summary>straight to the next tile centre of u.WPath (no path finding on the walkway)</summary>
    double WallSteer(Unit u, double dt, double speed)
    {
        while (true)
        {
            var (tx, tz) = (u.WPath[0].X, u.WPath[0].Z);
            double dx = tx - u.Pos.X, dz = tz - u.Pos.Z, d = Math.Sqrt(dx * dx + dz * dz);
            if (d < 0.3)
            {
                u.WPath.RemoveAt(0);
                var (i, j) = Walls!.TileOf(tx, tz);
                if (Walkway(i, j) != null) SetWallTile(u, (i, j));
                if (u.WPath.Count == 0) { u.Vel = V3.Zero; return 0; }
                continue;
            }
            u.Face(tx, tz, dt, 9);
            double sp = Math.Min(speed, d / dt);
            u.Vel = new V3(dx / d * sp, 0, dz / d * sp);
            u.Pos.X += u.Vel.X * dt; u.Pos.Z += u.Vel.Z * dt;
            var (ni, nj) = Walls!.TileOf(u.Pos.X, u.Pos.Z);
            if (u.WallTile != (ni, nj) && Walkway(ni, nj) != null) SetWallTile(u, (ni, nj));
            return sp;
        }
    }

    /// <summary>a jump on or off a wall (CActWallWalk: "jump_wall"; down "jump_of_wall"); onto = the walkway tile at the
    /// end, null on the ground; then = the order after landing</summary>
    public void WallJumpTo(Unit u, double x, double z, bool up, (int I, int J)? onto, Order? then = null)
    {
        var A = u.Anim!;
        string anim = up ? "jump_wall" : A.Has("jump_of_wall") ? "jump_of_wall" : "jump_wall";
        double dur = A.Has(anim) ? A.Duration(anim) : 0;
        double T = Math.Max(0.5, dur != 0 ? dur : 1.2);
        double y0 = u.Pos.Y, y1 = up ? WallTop(onto!.Value.I, onto.Value.J) : Height(x, z);
        u.Path = new NavPath(); u.WPath = new List<Waypoint>(); u.Vel = V3.Zero;
        u.Face(x, z, 1, 100);
        u.WJump = new WallJump { FromX = u.Pos.X, FromZ = u.Pos.Z, ToX = x, ToZ = z, Y0 = y0, Y1 = y1, T = 0, TT = T, Onto = onto, Then = then };
        if (A.Has(anim)) { u.BusyAnim = true; A.Play(anim, new AnimOpts { Loop = false, Restart = true, Fade = 0.1 }); }
        if (up) SetWallTile(u, onto);                 // (its height comes from the jump until it lands)
    }

    /// <summary>the jump in flight; called from the unit's step before the task</summary>
    void WallJumpUpdate(Unit u, double dt)
    {
        var j = u.WJump!;
        j.T += dt;
        double f = Math.Min(1, j.T / j.TT);
        u.Pos.X = j.FromX + (j.ToX - j.FromX) * f;
        u.Pos.Z = j.FromZ + (j.ToZ - j.FromZ) * f;
        u.WallH = j.Y0 + (j.Y1 - j.Y0) * f + SMath.Sin(f * Math.PI) * 1.2;
        if (f < 1) return;
        u.WJump = null; u.WallH = null; u.BusyAnim = false;
        if (j.Onto != null) { SetWallTile(u, j.Onto); SetOnWall(u, true); }
        else { SetOnWall(u, false); SetWallTile(u, null); }
        u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z;
        if (j.Onto == null) ToIdle(u);
        if (j.Then != null) { if (j.Onto != null) ToIdle(u); Order(u, j.Then); }
    }

    // SetOnWall (FO:1979): the ranged protection of the wall (Defence_On_Wall of the player's tribe)
    // (off the wall: its tile goes too - DropStates clears OnWall only)
    partial void SetOnWall(Unit u, bool on) { u.WallDef = on ? Data.MiscValue(u.RulesOwner(), "Defence_On_Wall", 0) : 0; if (!on) u.WallTile = null; }

    // ------------------------------------------------------------------ unitUpdate hooks
    /// <summary>before the task: a jump in flight, or a ground task of a unit on the walkway (it goes down first).
    /// true = done for this step</summary>
    public bool WallPreUpdate(Unit u, double dt)
    {
        if (u.WJump != null) { WallJumpUpdate(u, dt); return true; }
        if (u.WallTile == null) return false;
        var (wi, wj) = u.WallTile.Value;
        if (Walkway(wi, wj) == null) { WallFall(u, false); return false; }
        var t = u.Task;
        if (STAY.Contains(t.Type)) return false;
        // a ground task started on the wall (a resumed job, a script): down first, then the task again
        (double, double) dest = t.Type is "move" or "attackmove" ? (t.X, t.Z) : t.Target != null ? (t.Target.Pos.X, t.Target.Pos.Z) : t.Node != null ? (t.Node.Pos.X, t.Node.Pos.Z) : t.Building != null ? (t.Building.Pos.X, t.Building.Pos.Z) : (u.Pos.X, u.Pos.Z);
        u.Task = new UnitTask("wall") { Phase = "leave", Dest = dest, Resume = t, User = t.User };
        return false;
    }

    /// <summary>the attack of a unit on the walkway with its target out of reach: along the walkway to a target on it,
    /// down after it on an order of the player, else the fight ends (no chasing from a wall: CheckPatrol, FO:2393)</summary>
    public void WallChase(Unit u, UnitTask t, Entity tgt, double dt)
    {
        if (tgt is Unit tu && tu.WallTile != null && u.WallTile != null)
        {
            var route = WallRoute(u.WallTile.Value, tu.WallTile.Value);
            if (route != null)
            {
                if (route.Count > 1 || Math.Sqrt((tgt.Pos.X - u.Pos.X) * (tgt.Pos.X - u.Pos.X) + (tgt.Pos.Z - u.Pos.Z) * (tgt.Pos.Z - u.Pos.Z)) > 1.5)
                {
                    u.WPath = route.Skip(route.Count > 1 ? 1 : 0).Select(x => C(x)).ToList();
                    if (route.Count == 1) u.WPath = new List<Waypoint> { new(tgt.Pos.X, tgt.Pos.Z) };
                    u.MoveAnim(WallSteer(u, dt, u.RunSpeed), true);
                    return;
                }
            }
        }
        if (t.User && !t.Hold) { u.Task = new UnitTask("wall") { Phase = "leave", Dest = (tgt.Pos.X, tgt.Pos.Z), Then = new Order("attack") { Target = tgt }, User = true }; return; }
        EndAttack(u);
    }

    /// <summary>a ground unit after a target on the walkway: melee fighters go up where they may (their own wall, a ladder
    /// of their side) - else the target is out of their reach. -> the wall or the ladder, null: none</summary>
    public (Building? Wall, Entity? Ladder)? WallReach(Unit u, Entity tgt)
    {
        if (tgt is not Unit tu || tu.WallTile == null || u.WallTile != null || !CanClimb(u)) return null;
        var tiles = WalkwayOf(tu.WallTile.Value);
        (Building?, Entity?)? best = null; double bd = double.PositiveInfinity;
        foreach (var (i, j) in tiles)
        {
            var w = Walkway(i, j);
            var (cx, cz) = Walls!.Centre(i, j); double d = Math.Sqrt((cx - u.Pos.X) * (cx - u.Pos.X) + (cz - u.Pos.Z) * (cz - u.Pos.Z));
            if (w != null && w.Owner == u.Owner && d < bd) { bd = d; best = (w, null); }
            foreach (var l in LaddersAt(i, j)) if (MayUseSiege(u, l) && d < bd) { bd = d; best = (null, l); }
        }
        return best;
    }

    // ------------------------------------------------------------------ falling
    // the piece under them is gone (WCT_Destroyed, CH:2165-2174): the order ends, they fall and take (height x 5)
    // melee damage; hurt = false when the piece only stopped being walkway (replaced by a gate or a tower)
    partial void WallFall(Unit u, bool hurt) => WallFallNow(u, hurt);

    public void WallFallNow(Unit u, bool hurt = true)
    {
        if (u.WallTile == null && u.OnWall == null && !u.Climbing) return;
        double h = u.Pos.Y - Height(u.Pos.X, u.Pos.Z);
        SetWallTile(u, null); u.WPath = new List<Waypoint>(); u.WallDef = 0; u.Climbing = false; u.WallH = null;
        ReleaseTask(u);
        ToIdle(u); u.Path = new NavPath();
        int k = Nav.NearestFree(Nav.Idx(u.Pos.X, u.Pos.Z), 6); var (fx, fz) = Nav.Center(k);
        u.WJump = new WallJump { FromX = u.Pos.X, FromZ = u.Pos.Z, ToX = fx, ToZ = fz, Y0 = u.Pos.Y, Y1 = Height(fx, fz), T = 0, TT = 0.6, Onto = null, Then = null };
        if (u.Anim!.Has("hit_front"))
        {
            u.BusyAnim = true;
            u.Anim.Play("hit_front", new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone("wallGetUp") });
        }
        if (hurt && h > 0.5) TakeDirectDmg(u, h * FALL_DMG, 0, null, false);
    }
    // after the fall: getting up, then free again
    [AnimDone("wallGetUp")]
    static void WallGetUp(World W, Entity e, Args? a)
    {
        if (e is not Unit u) return;
        if (u.Alive && u.Anim!.Has("getting_up")) u.Anim.Play("getting_up", new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone("busy") });
        else u.BusyAnim = false;
    }

    /// <summary>a wall piece is destroyed (or replaced): the characters on its tile fall</summary>
    public void WallPieceGone(Building b)
    {
        if (b.Tile == null) return;
        var tile = b.Tile.Value;
        foreach (var u in Units.ToList()) if (u.Alive && u.WallTile == tile && u.WJump == null) WallFallNow(u, !b.SilentDeath);
    }
}
