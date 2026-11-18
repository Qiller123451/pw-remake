// Siege objects at a wall (task/DockWall.usl; docs/spec/walls_walk.md §4, §5): the Hu ladder (hu_ladder, CLadder),
// the Ninigi siege tower (ninigi_siegetower, CSiegeTower) and the siege brachiosaurus (aje_brachiosaurus with the
// build-up aje_brachiosaurus_siege). The Base tech tree hides all three (visibility -1); mods such as MIRAGE offer
// them.
//
//   * One siege object per wall piece (GetWallSiege, DW:385-391): World.Sieges (i, j) -> the ladder or the docked
//     vehicle (an unfinished ladder holds its piece too).
//   * Ladder (CBuildLadder, DW:328-455): a Hu worker puts it against the piece under the cursor, on the side nearest
//     the worker, facing the wall, without the place check; it is an ordinary building (20 wood, 1000 hp, no
//     timer). Finished, it is an entrance of the wall (AddAdditionalEntrance, DW:49-57): its owner's and allies'
//     characters climb it (right click: "ClimbLadder", GIC:852) - from its foot (link Wo_1) up to its top (Wo_2) at
//     3.65 m/s, then onto the walkway (walkable walls) or over the wall: a jump to 4 m past the wall's centre on the
//     far side (CClimbLadder DW:458-631; the climb animations as named, not the original's swapped ones).
//     Characters on the walkway leave by it too (systems/wallwalk.js wallExit).
//   * Siege tower (CDockWall, DW:79-326): an attack order on a wall piece (or "Dock" on an own one) docks it -
//     to a free side of the piece, its back to the wall, "opengate_moveup" (low walls: "opengate"), docked after
//     2 s. Its passengers' "Dismount" puts them on the far side of the wall (CLeaveSiegeTower, DW:1011-1107). (The
//     climb over a docked tower, CClimbSiegeTower DW:846-1009, is started by the engine's path finder for ground
//     paths across the wall [?engine]: not done.) A new order undocks it first ("closegate", 2 s).
//   * Siege brachiosaurus: docks the same way facing the wall ("siege_down" of its siege build-up); it is an
//     entrance like the ladder, from the build-up's psh1 to psh2 (CClimbBrachio, DW:633-844; "climb_up_brachio").
//
// Ported from remake/src/game/systems/siege.js (pw-exporter 98c7703). The links of the brachiosaurus' build-up are read
// from the model data (its link on the animal + its own link, turned by the animal's heading).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using WMap = PwRemake.Sim.World.WallMap;

namespace PwRemake.Sim.World;

/// <summary>a docked siege vehicle: the wall piece and its tile (u.docked)</summary>
public sealed class DockInfo { public Building Wall = null!; public int I, J; }

/// <summary>a climb's straight move from one point to another (siege.js climbUpdate t.mv)</summary>
public sealed class ClimbMove { public double FromX, FromZ, ToX, ToZ, Y0, Y1, T, TT; }

public sealed partial class UnitTask
{
    /// <summary>climb: the ladder / docked vehicle climbed, the move under way</summary>
    public Entity? Siege;
    public ClimbMove? Mv;
}

public sealed partial class Building
{
    /// <summary>a ladder: the wall tile it stands against</summary>
    public (int I, int J)? DockTile;
}

public sealed partial class World
{
    const double DOCK_T = 2;          // docking / undocking (DW:247-259, 280-291)
    const double CLIMB = 3.65;        // up the ladder / tower (DW:571-583)
    const double TRIP = 5;            // the steps to the foot and from the top (DW:554-562)
    const double FAR = 4;             // the far side: 4 m past the wall's centre (DW:614)
    const double LADDER_OUT = 5.3;    // the ladder's foot from the piece's centre: its top (4.27 m out, 7.61 m up) on the walkway

    /// <summary>one siege object per wall piece: tile -> the ladder or the docked vehicle</summary>
    public readonly Dictionary<(int I, int J), Entity> Sieges = new();

    public static bool IsLadder(Entity? b) => b is Building bb && bb.Def != null && bb.Def.Ladder;
    public static bool IsSiegeTower(Entity? u) => u is Unit uu && uu.Def != null && (uu.Def.Script == "CSiegeTower" || uu.Name == "ninigi_siegetower");
    static AttachedPart? SiegePart(Unit u) => u.Comp?.Parts.FirstOrDefault(p => p.Spec?.Gfx == "aje_brachiosaurus_siege");
    /// <summary>the siege brachiosaurus</summary>
    public bool IsSiegeBrachio(Entity? t) => t is Unit u && u.Name == "aje_brachiosaurus" && SiegePart(u) != null;
    /// <summary>can u dock to a wall (siege tower, brachiosaurus)</summary>
    public bool CanDock(Unit u) => IsSiegeTower(u) || IsSiegeBrachio(u);
    public Entity? SiegeAt(int i, int j) => Sieges.TryGetValue((i, j), out var s) ? s : null;
    void SetSiege((int I, int J) tile, Entity? s) { if (s != null) Sieges[tile] = s; else Sieges.Remove(tile); }
    /// <summary>the wall tile a siege object holds (a ladder's, a docked / docking vehicle's)</summary>
    public static (int I, int J)? DockTileOf(Entity e) => e is Building b ? b.DockTile : e is Unit u && u.DockTile is ValueTuple<int, int> t ? t : null;
    static void SetDockTile(Entity e, (int I, int J)? t) { if (e is Building b) b.DockTile = t; else if (e is Unit u) u.DockTile = t; }

    /// <summary>the entrances of the walkway at piece (i, j): a finished ladder, a docked brachiosaurus</summary>
    public List<Entity> LaddersAt(int i, int j)
    {
        var s = SiegeAt(i, j);
        if (s == null || !s.Alive) return new List<Entity>();
        if (IsLadder(s) && ((Building)s).Built) return new List<Entity> { s };
        if (IsSiegeBrachio(s) && ((Unit)s).Docked != null) return new List<Entity> { s };
        return new List<Entity>();
    }
    /// <summary>may u climb siege object s (its owner's and allies' characters; a ready ladder, a docked brachiosaurus -
    /// the siege tower is boarded: its passengers get out beyond the wall)</summary>
    public bool MayUseSiege(Unit u, Entity? s)
    {
        if (!CanClimb(u) || s == null || !s.Alive || DockTileOf(s) == null || u.Owner == null || s.Owner == null) return false;
        if (s.Owner != u.Owner && !u.Owner.IsFriend(s.Owner)) return false;
        return IsLadder(s) ? ((Building)s).Built : IsSiegeBrachio(s) && ((Unit)s).Docked != null;
    }

    sealed class SiegeEntryInfo { public V3 Foot, Top; public (int I, int J) Tile; public double Cx, Cz, Nx, Nz; public bool Over; }

    /// <summary>a link of an attached part in the world (the part's link on the unit plus its own link, turned by the
    /// unit's heading)</summary>
    V3? PartLinkWorld(Unit u, AttachedPart p, string link)
    {
        var own = Models.Link(p.Gfx, link);
        if (own == null) return null;
        var at = Models.Link(u.Gfx, p.Link) ?? V3.Zero;
        double lx = at.X + own.Value.X, ly = at.Y + own.Value.Y, lz = at.Z + own.Value.Z, c = SMath.Cos(u.Heading), s = SMath.Sin(u.Heading);
        return new V3(u.Pos.X + lx * c + lz * s, u.Pos.Y + ly, u.Pos.Z - lx * s + lz * c);
    }

    // where a siege object is climbed: foot and top (world), the piece, and whether the climb ends on the walkway or
    // goes over the wall (a wall without a walkway, the siege tower)
    SiegeEntryInfo SiegeEntry(Entity s)
    {
        var (i, j) = DockTileOf(s)!.Value; var (cx, cz) = Walls!.Centre(i, j);
        V3 foot, top;
        if (IsLadder(s))
        {
            var b = (Building)s;
            foot = b.LinkWorld("Wo_1") ?? s.Pos;
            top = b.LinkWorld("Wo_2") ?? new V3(cx, s.Pos.Y + WALL_TOP, cz);
        }
        else if (IsSiegeTower(s))
        {
            foot = s.Pos;
            top = s.Pos; top.Y += WALL_TOP;                     // (DW:914: the entry + 7.8 m)
        }
        else
        {
            var u = (Unit)s; var p = SiegePart(u);
            foot = (p != null ? PartLinkWorld(u, p, "psh1") : null) ?? s.Pos;
            top = (p != null ? PartLinkWorld(u, p, "psh2") : null) ?? new V3(cx, s.Pos.Y + WALL_TOP, cz);
            foot.Y = Height(foot.X, foot.Z);
        }
        double nx = cx - foot.X, nz = cz - foot.Z, L = Math.Sqrt(nx * nx + nz * nz);
        if (L == 0) L = 1;
        return new SiegeEntryInfo { Foot = foot, Top = top, Tile = (i, j), Cx = cx, Cz = cz, Nx = nx / L, Nz = nz / L, Over = IsSiegeTower(s) || Walkway(i, j) == null };
    }

    // ------------------------------------------------------------------ ladders
    /// <summary>the ladder model's top link (Wo_2) at rotation 0 relative to its origin</summary>
    (double X, double Z) LadderTop(string name, Player? owner)
    {
        var st = Data.Stats(name, 1, owner);
        if (st == null || !Models.Has(st.Gfx)) return (0, LADDER_OUT - 1);
        var v = Models.Link(st.Gfx, "Wo_2") ?? new V3(0, 7.6, 4.3);
        return (v.X, v.Z);
    }

    /// <summary>placement of a ladder for a click at (x, z) by workers standing at `from`: the side of the wall piece
    /// there that is nearest the workers, turned to the wall; one siege object per piece</summary>
    public Placement LadderPlacement(string name, double x, double z, Player? owner, (double X, double Z)? from)
    {
        var WM = Walls;
        if (WM == null) return new Placement { X = x, Z = z, Rot = 0, Ok = false };
        var (i, j) = WM.TileOf(x, z); var (cx, cz) = WM.Centre(i, j);
        var t = WM.At(i, j);
        if (t == null || t.Wall == null || !t.Wall.Alive || t.Gate != null || t.Tower != null || SiegeAt(i, j) != null) return new Placement { X = cx, Z = cz, Rot = 0, Ok = false };
        var f = from ?? (x, z); int m = WM.Mask(i, j);
        (double X, double Z)? best = null; double bd = double.PositiveInfinity;
        for (int d = 0; d < 8; d++)
        {
            if (((m >> d) & 1) != 0) continue;
            int di = WMap.DIRS[d][0], dj = WMap.DIRS[d][1]; double L = Math.Sqrt(di * di + dj * dj), px = cx + di / L * LADDER_OUT, pz = cz + dj / L * LADDER_OUT;
            if (!Nav.IsFree(px, pz)) continue;
            double dd = Math.Sqrt((px - f.X) * (px - f.X) + (pz - f.Z) * (pz - f.Z));
            if (dd < bd) { bd = dd; best = (px, pz); }
        }
        if (best == null) return new Placement { X = cx, Z = cz, Rot = 0, Ok = false };
        // turn it so that its top points at the wall: rotation.y maps the angle atan2(x, z) of a vector by + rot
        var (tx, tz) = LadderTop(name, owner);
        double rot = SMath.Atan2(cx - best.Value.X, cz - best.Value.Z) - SMath.Atan2(tx, tz);
        var P = Play;
        bool ok = best.Value.X > P.X0 + 6 && best.Value.X < P.X1 - 6 && best.Value.Z > P.Z0 + 6 && best.Value.Z < P.Z1 - 6;
        return new Placement { X = best.Value.X, Z = best.Value.Z, Rot = rot, Ok = ok, Dock = (i, j) };
    }

    /// <summary>a ladder was placed: it holds its piece and doesn't block the way to its foot</summary>
    public void LadderPlaced(Building b, (int I, int J) tile)
    {
        b.DockTile = tile;
        SetSiege(tile, b);
        b.SetBlocking(false);
    }

    // a docked siege vehicle leaves its wall (DropStates, a unit's death)
    partial void SiegeGone(Unit u) => SiegeGoneEntity(u);

    /// <summary>an entity is gone: a ladder frees its piece; a wall piece's ladder or docked vehicle loses its hold</summary>
    public void SiegeGoneEntity(Entity e)
    {
        if (DockTileOf(e) is var (di, dj) && SiegeAt(di, dj) == e) SetSiege((di, dj), null);
        if (e is Building b && b.Tile != null && b.Def != null && b.Def.WallKind == "wall")
        {
            var tile = b.Tile.Value;
            var s = SiegeAt(tile.I, tile.J);
            if (s != null)
            {
                SetSiege(tile, null);
                SetDockTile(s, null);
                if (s is Unit su && (su.Docked != null || su.Task.Type == "dock")) UndockNow(su, null);      // (DW:168-172, AN:1887)
            }
        }
    }

    // ------------------------------------------------------------------ climbing (CClimbLadder, CClimbBrachio)
    partial void ClimbSiege(Unit u, Entity? target, bool user) => ClimbSiegeOrder(u, target, user);

    public bool ClimbSiegeOrder(Unit u, Entity? s, bool user = true)
    {
        if (!MayUseSiege(u, s)) return false;
        u.Task = new UnitTask("climb") { Siege = s, Phase = "go", User = user };
        return true;
    }
    /// <summary>down a ladder / brachiosaurus from the walkway, then the order</summary>
    void ClimbDown(Unit u, Entity s, Order? then)
    {
        u.Task = new UnitTask("climb") { Siege = s, Phase = "down", User = then != null && !then.Auto, Then = then };
        u.WPath = new List<Waypoint>();
    }

    partial void ClimbUpdate(Unit u, double dt)
    {
        var t = u.Task; var s = t.Siege;
        if (s == null || !s.Alive || DockTileOf(s) == null || (!IsLadder(s) && ((Unit)s).Docked == null))
        {
            // the ladder is gone under it: down to the ground where it is
            if (u.Climbing) WallFallNow(u, true);
            else u.Task = UnitTask.Idle();
            return;
        }
        var E = SiegeEntry(s);
        bool Move(double tx, double tz, double y1, double speed, string? anim)
        {
            if (t.Mv == null)
            {
                double y0 = u.WallH ?? u.Pos.Y, dx = tx - u.Pos.X, dz = tz - u.Pos.Z, dy = y1 - y0, d = Math.Sqrt(dx * dx + dz * dz + dy * dy);
                t.Mv = new ClimbMove { FromX = u.Pos.X, FromZ = u.Pos.Z, ToX = tx, ToZ = tz, Y0 = y0, Y1 = y1, T = 0, TT = Math.Max(0.2, d / speed) };
                if (anim != null && u.Anim!.Has(anim)) { u.BusyAnim = true; double ad = u.Anim.Duration(anim); u.Anim.Play(anim, new AnimOpts { Restart = true, Ts = Math.Max(0.3, (ad != 0 ? ad : 1) / t.Mv.TT * (anim == "trip" ? 0.25 : 1)) }); }
            }
            var m = t.Mv;
            m.T += dt;
            double f = Math.Min(1, m.T / m.TT);
            u.Pos.X = m.FromX + (m.ToX - m.FromX) * f; u.Pos.Z = m.FromZ + (m.ToZ - m.FromZ) * f;
            u.WallH = m.Y0 + (m.Y1 - m.Y0) * f;
            u.Vel = V3.Zero;
            if (f < 1) return false;
            t.Mv = null;
            return true;
        }
        switch (t.Phase)
        {
            case "go":
            {
                double d = Math.Sqrt((E.Foot.X - u.Pos.X) * (E.Foot.X - u.Pos.X) + (E.Foot.Z - u.Pos.Z) * (E.Foot.Z - u.Pos.Z)), reach = IsLadder(s) ? 1.2 : s.Radius + 1.5;
                if (d > reach)
                {
                    if (!t.Walking || (u.Path.Count == 0 && ++t.Tries < 4)) { u.SetPath(E.Foot.X, E.Foot.Z); t.Walking = true; }
                    if (u.Path.Count == 0 && d > reach + 2) { u.Task = UnitTask.Idle(); return; }
                    u.MoveAnim(u.Steer(dt, t.User ? u.RunSpeed : u.Speed, 0.4), t.User);
                    return;
                }
                u.Path = new NavPath(); u.Climbing = true; u.WallH = u.Pos.Y;
                u.Heading = SMath.Atan2(-E.Nx, -E.Nz);
                t.Phase = "foot";
                goto case "foot";
            }
            case "foot":
                if (!Move(E.Foot.X, E.Foot.Z, E.Foot.Y, TRIP, "trip")) return;
                t.Phase = "up";
                goto case "up";
            case "up":
            {
                var anim = IsLadder(s) ? "hu_high_ladder_climb_up" : IsSiegeTower(s) ? "climb_up_ladder" : "climb_up_brachio";
                if (!Move(E.Top.X, E.Top.Z, E.Top.Y, CLIMB, anim)) return;
                t.Phase = E.Over ? "over" : "top";
                return;
            }
            case "top":
            {
                // the steps onto the walkway (DW:595 trip_end)
                var (i, j) = E.Tile;
                if (!Move(E.Cx, E.Cz, WallTop(i, j), TRIP, "trip")) return;
                u.Climbing = false; u.BusyAnim = false;
                SetWallTile(u, (i, j)); u.WallH = null;
                SetOnWall(u, true);
                var then = t.Then;
                u.Task = UnitTask.Idle();
                if (then != null) Order(u, then);
                return;
            }
            case "over":
            {
                // over the wall: a jump to 4 m past its centre on the far side (DW:588-593, "climb_up_ladder", speed 20)
                double gx = E.Cx + E.Nx * FAR, gz = E.Cz + E.Nz * FAR;
                int k = Nav.NearestFree(Nav.Idx(gx, gz), 6); var (fx, fz) = Nav.Center(k);
                u.Climbing = false; u.BusyAnim = false; u.WallH = null;
                SetWallTile(u, null);
                u.Pos.Y = E.Top.Y;
                u.WJump = new WallJump { FromX = u.Pos.X, FromZ = u.Pos.Z, ToX = fx, ToZ = fz, Y0 = E.Top.Y, Y1 = Height(fx, fz), T = 0, TT = 1.2, Onto = null, Then = t.Then };
                u.WallH = E.Top.Y;
                if (u.Anim!.Has("climb_up_ladder")) { u.BusyAnim = true; u.Anim.Play("climb_up_ladder", new AnimOpts { Loop = false, Restart = true }); }
                return;
            }
            // from the walkway: to the top of the ladder, down to its foot
            case "down":
            {
                if (!u.Climbing) { u.Climbing = true; u.WallH = u.Pos.Y; SetOnWall(u, false); SetWallTile(u, null); }
                if (!Move(E.Top.X, E.Top.Z, E.Top.Y, TRIP, "trip")) return;
                t.Phase = "down2";
                return;
            }
            case "down2":
            {
                var anim = IsLadder(s) ? "climb_down_ladder" : "climb_down_brachio";
                if (!Move(E.Foot.X, E.Foot.Z, Height(E.Foot.X, E.Foot.Z), CLIMB, anim)) return;
                u.Climbing = false; u.BusyAnim = false; u.WallH = null;
                // a step away from the foot, so the next one can come down
                int k = Nav.NearestFree(Nav.Idx(E.Foot.X - E.Nx * 1.5, E.Foot.Z - E.Nz * 1.5), 6); var (fx, fz) = Nav.Center(k);
                if (Math.Sqrt((fx - u.Pos.X) * (fx - u.Pos.X) + (fz - u.Pos.Z) * (fz - u.Pos.Z)) < 4) { u.Pos.X = fx; u.Pos.Z = fz; }
                u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z;
                var then = t.Then;
                u.Task = UnitTask.Idle();
                if (then != null) Order(u, then);
                return;
            }
        }
    }

    // ------------------------------------------------------------------ docking (CDockWall)
    partial void DockOrder(Unit u, Entity? target, bool user) => DockTo(u, target, user);

    public bool DockTo(Unit u, Entity? wall, bool user = true)
    {
        if (!CanDock(u) || wall is not Building w || !w.Alive || w.Tile == null || w.Def!.WallKind != "wall") return false;
        u.Task = new UnitTask("dock") { Wall = w, Phase = "find", Tries = 0, User = user };
        return true;
    }

    partial void DockUpdate(Unit u, double dt)
    {
        var t = u.Task; var w = t.Wall;
        if (t.Phase == "undock")
        {
            u.Vel = V3.Zero;
            if ((t.T -= dt) > 0) return;
            u.BusyAnim = false;
            var then = t.Then;
            u.Task = UnitTask.Idle();
            if (then != null) Order(u, then);
            return;
        }
        if (w == null || !w.Alive || w.Tile == null) { if (u.Docked != null) UndockNow(u, null); else u.Task = UnitTask.Idle(); return; }
        var (i, j) = w.Tile.Value; var (cx, cz) = Walls!.Centre(i, j);
        bool brachio = IsSiegeBrachio(u);
        switch (t.Phase)
        {
            case "find":
            {
                var other = SiegeAt(i, j);
                if ((other != null && other != u) || ++t.Tries > 3) { u.Task = UnitTask.Idle(); return; }
                // GetFreeDockPos: a free side of the piece nearest the vehicle (a brachiosaurus 2 m further back, DW:183)
                int m = Walls.Mask(i, j); double o = 1.6 + u.Radius + 0.5 + (brachio ? 2 : 0) + ((w.PfMask & 0x4000) != 0 ? 1.6 : 0);   // (an end piece: its big hub box, wallmap.js pfMask)
                (double X, double Z)? best = null; double bd = double.PositiveInfinity;
                for (int d = 0; d < 8; d++)
                {
                    if (((m >> d) & 1) != 0) continue;
                    int di = WMap.DIRS[d][0], dj = WMap.DIRS[d][1]; double L = Math.Sqrt(di * di + dj * dj), px = cx + di / L * o, pz = cz + dj / L * o;
                    if (!Nav.IsFree(px, pz)) continue;
                    double dd = Math.Sqrt((px - u.Pos.X) * (px - u.Pos.X) + (pz - u.Pos.Z) * (pz - u.Pos.Z));
                    if (dd < bd) { bd = dd; best = (px, pz); }
                }
                if (best == null) { u.Task = UnitTask.Idle(); return; }
                t.Dest = best;
                u.SetPath(best.Value.X, best.Value.Z);
                t.Phase = "go";
                return;
            }
            case "go":
            {
                if (u.Path.Count > 0) { u.MoveAnim(u.Steer(dt, u.Speed, 0.6), false); return; }
                var (tx, tz) = t.Dest!.Value;
                if (Math.Sqrt((tx - u.Pos.X) * (tx - u.Pos.X) + (tz - u.Pos.Z) * (tz - u.Pos.Z)) > 2) { t.Phase = "find"; return; }     // (DW:203-207)
                t.Phase = "turn";
                return;
            }
            case "turn":
            {
                // the siege tower turns its back to the wall, the brachiosaurus faces it (DW:208-219)
                double fx = brachio ? cx : 2 * u.Pos.X - cx, fz = brachio ? cz : 2 * u.Pos.Z - cz;
                u.Vel = V3.Zero;
                if (u.Face(fx, fz, dt, 2) > 0.05) { u.MoveAnim(0, false); return; }
                if (SiegeAt(i, j) != null && SiegeAt(i, j) != u) { u.Task = UnitTask.Idle(); return; }
                SetSiege((i, j), u);
                u.DockTile = (i, j);
                t.T = DOCK_T;
                t.Phase = "docking";
                u.BusyAnim = true;
                if (brachio)
                {
                    var p = SiegePart(u);
                    p?.Anim?.Play("siege_down", new AnimOpts { Loop = false, Restart = true });
                    if (u.Anim!.Has("siege")) u.Anim.Play("siege", new AnimOpts { Loop = false, Restart = true });
                }
                else
                {
                    bool low = (w.Name.Contains("palisade") || w.Name.Contains("skewer")) && w.Name != "ninigi_palisade";
                    var a = low ? "opengate" : "opengate_moveup";
                    if (u.Anim!.Has(a)) u.Anim.Play(a, new AnimOpts { Loop = false, Restart = true });
                }
                u.LookChanged();
                return;
            }
            case "docking":
                u.Vel = V3.Zero;
                if ((t.T -= dt) > 0) return;
                u.Docked = new DockInfo { Wall = w, I = i, J = j };
                t.Phase = "docked";
                return;
            case "docked": u.Vel = V3.Zero; return;
        }
    }

    // a docked (or docking) vehicle gets a new order: it undocks first (2 s), then the order (DW:300-316)
    partial void Undock(Unit u, Order o) => UndockNow(u, o);

    public void UndockNow(Unit u, Order? then)
    {
        if (DockTileOf(u) is var (di, dj) && SiegeAt(di, dj) == u) SetSiege((di, dj), null);
        u.DockTile = null; u.Docked = null;
        u.Task = new UnitTask("dock") { Phase = "undock", T = DOCK_T, Then = then };
        u.BusyAnim = true;
        if (IsSiegeBrachio(u)) SiegePart(u)?.Anim?.Play("siege_up", new AnimOpts { Loop = false, Restart = true });
        else if (u.Anim!.Has("closegate")) u.Anim.Play("closegate", new AnimOpts { Loop = false, Restart = true });
        u.LookChanged();
    }

    /// <summary>CLeaveSiegeTower: a passenger of a docked tower gets out on the far side of the wall (DW:1090-1100);
    /// null: not docked</summary>
    public (double X, double Z)? SiegeExitPoint(Unit t)
    {
        if (t.Docked == null || DockTileOf(t) is not var (i, j)) return null;
        var (cx, cz) = Walls!.Centre(i, j);
        double nx = cx - t.Pos.X, nz = cz - t.Pos.Z, L = Math.Sqrt(nx * nx + nz * nz), r = 1.6 + FAR;
        if (L == 0) L = 1;
        int k = Nav.NearestFree(Nav.Idx(cx + nx / L * r, cz + nz / L * r), 6);
        return Nav.Center(k);
    }
}
