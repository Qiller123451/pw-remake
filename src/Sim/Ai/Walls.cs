// The computer player's village wall: a ring of wall segments around the village, a gate in every longer segment,
// a tower at the start of the long ones, archers on the wall near a gate (goals/AiGoalBuildVillageWalls.usl,
// tasks/AiTaskBuildWalls / BuildGate / BuildTowers.usl; docs/spec/ai_group6.md §1).
//
//   * Active from epoch 3 when the map allows walls (AIOptions "walls"), the behaviour closes its start location
//     (close_start_location 1: Giraffe, Schnecke, Turtle) and the level is not "Single 02" (DM:672-685, VW:733).
//   * The class: the best of the tribe's wall list the tech tree lets it build (VW:310-360; the mayor makes the Aje
//     and Ninigi build their walkable walls); none: their prerequisites are researched (UpgradeWalls); after the
//     first wall the next class is prepared (upgrade_walls 1). Standing walls are never replaced.
//   * The ring [?engine: the original's WallPlacer plans it]: the convex hull of the village's buildings (not walls,
//     towers, fields; within 100 m of the start location) 12 m out, on the wall grid; tiles the wall can't stand on
//     leave holes. Its runs are the segments (at most 12 tiles). A segment of 4+ tiles gets its gate in the middle
//     of a straight stretch (built as a wall piece first: the remake's gates replace one); a shorter one keeps a
//     hole of one tile (PutHoleInWall).
//   * One segment at a time, in batches of what the resources pay (< 1 -> 3, at most 10 tiles, TBW:264-294); an
//     enemy within 60 m of its start makes it ask for an attack there instead (UM:615-702). 1-2 workers build; the
//     others of the batch are chained (construction.js autoWork); 60 s without progress: other workers.
//   * Gates: at most 10, none within 24 m of another (TBG:136); 5 failures drop one. SEAS: the gate class
//     "seas_fence_gate" does not exist, so SEAS walls get no gates (VW:568-571, kept).
//   * A finished segment of 24 m or more without a hole: the tribe's small tower on its first tile (at most 2
//     building at a time; 10 failures: no tower there).
//   * Archers on the wall: when the first gate is started, Hu and Aje want 3 archers; once a gate stands they go up
//     the nearest walkable own wall piece within 36 m of it and hold (VW:796-866 - the original centres the search on
//     a position it never sets and never sends them: the intent is done here), at most 10 sendings.
//
// Ported from remake/src/game/ai/walls.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Ai;

/// <summary>a tile of the wall grid in the ring (a reference: a segment's hole is one of its tiles)</summary>
public sealed class WTile
{
    public readonly int I, J;
    public WTile(int i, int j) { I = i; J = j; }
    public override string ToString() => $"[{I}, {J}]";
}

/// <summary>a segment of the village wall</summary>
public sealed class WallSeg
{
    public List<WTile> Tiles = new();
    public WTile? Gate, Hole;
    public List<Building> Pieces = new();
    public bool Done;
    public Building? TowerB, GateObj;
    public int TowerFails, GateFails, Fails;
    public double? Prog;
    public double ProgT;
}

/// <summary>the village wall goal's state</summary>
public sealed class WallState : IClaimer
{
    public List<WallSeg>? Ring;
    public string? Cls;
    public int Gates, Sends;
    public List<Unit> Archers = new();
    public bool WantArchers, Upgraded;
}

public sealed partial class TribeAI
{
    static readonly Dictionary<string, string[]> WALLS = new() { ["Hu"] = new[] { "hu_palisade", "hu_small_wall", "hu_re_enforced_wall" }, ["Aje"] = new[] { "aje_bone_palisade", "aje_clay_wall" }, ["Ninigi"] = new[] { "ninigi_defense_skewer", "ninigi_palisade" }, ["SEAS"] = new[] { "seas_fence" } };
    static readonly Dictionary<string, string> TOWER = new() { ["Hu"] = "hu_small_tower", ["Aje"] = "aje_small_tower", ["Ninigi"] = "ninigi_small_tower", ["SEAS"] = "seas_turret_tower" };
    static readonly Dictionary<string, string> ARCHERS = new() { ["Hu"] = "hu_archer", ["Aje"] = "aje_archer" };
    const int MAX_GATES = 10, BATCH = 10, SEG_MAX = 12;
    const double MARGIN = 12;

    public WallState? Walls;
    string[] WallList() => WALLS.TryGetValue(P.Tribe, out var l) ? l : Array.Empty<string>();

    bool WallsActive(Census S) =>
        MapOptions.Walls && !EconomyOff && S.Age >= 3 && LevelName != "Single 02" && Params.CloseStartLocation != 0 && !double.IsNaN(Params.CloseStartLocation) && !Params.NoWalls && S.Home != null &&
        GameType != "Defender";                                          // no walls in a Defender game (DM:675)
    ActionDef? WallAct(string? cls) { var R0 = cls != null ? Resolve("BLDG/" + cls, "") : null; return R0 != null && R0.Kind == "bldg" && R0.Cls == cls ? R0.Action : null; }
    /// <summary>UpdateBestWallClass: the best class the tech tree lets it build -> class name | null</summary>
    string? BestWallClass(Census S)
    {
        var list = WallList();
        if (S.Units.Any(u => u.Name == "mayor_s0" && u.Level >= 3))       // (VW:320-326)
        {
            string? forced = P.Tribe == "Aje" ? "aje_clay_wall" : P.Tribe == "Ninigi" ? "ninigi_palisade" : null;
            if (forced != null && WallAct(forced) != null) return forced;
        }
        for (int i = list.Length - 1; i >= 0; i--) { var a = WallAct(list[i]); if (a != null && BuildCheck(P, a) == null) return list[i]; }
        return null;
    }
    (double X, double Z) TileCentre(WTile t) => W.Walls!.Centre(t.I, t.J);
    /// <summary>the ring around the village -> segments {tiles, gate | null, hole | null}</summary>
    List<WallSeg> PlanRing(Census S, string cls)
    {
        var WM = W.WallMap; var h = S.Home!.Value; var pts = new List<(double X, double Z)>();
        if (WM == null) return new List<WallSeg>();
        foreach (var b in S.Blds)
        {
            if (!string.IsNullOrEmpty(b.Def!.WallKind) || W.IsFarm(b) || DistP(b, h) > 100) continue;
            double r = Math.Max(b.Radius != 0 ? b.Radius : 4, 4) + MARGIN;
            for (int k = 0; k < 8; k++) pts.Add((b.Pos.X + SMath.Cos(k * Math.PI / 4) * r, b.Pos.Z + SMath.Sin(k * Math.PI / 4) * r));
        }
        if (pts.Count < 3) return new List<WallSeg>();
        // the convex hull (monotone chain)
        pts = pts.OrderBy(a => a.X).ThenBy(a => a.Z).ToList();
        static double Cross((double X, double Z) o, (double X, double Z) a, (double X, double Z) b) => (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);
        var lo = new List<(double X, double Z)>(); var hi = new List<(double X, double Z)>();
        foreach (var q in pts) { while (lo.Count >= 2 && Cross(lo[^2], lo[^1], q) <= 0) lo.RemoveAt(lo.Count - 1); lo.Add(q); }
        for (int k = pts.Count - 1; k >= 0; k--) { var q = pts[k]; while (hi.Count >= 2 && Cross(hi[^2], hi[^1], q) <= 0) hi.RemoveAt(hi.Count - 1); hi.Add(q); }
        var hull = lo.Take(lo.Count - 1).Concat(hi.Take(hi.Count - 1)).ToList();
        // the hull's edges on the wall grid
        var ring = new List<WTile>();
        for (int k = 0; k < hull.Count; k++)
        {
            var a = WM.TileOf(hull[k].X, hull[k].Z); var b = WM.TileOf(hull[(k + 1) % hull.Count].X, hull[(k + 1) % hull.Count].Z);
            foreach (var t in WallMap.Line(a, b)) { var L0 = ring.Count > 0 ? ring[^1] : null; if (L0 == null || L0.I != t.I || L0.J != t.J) ring.Add(new WTile(t.I, t.J)); }
        }
        if (ring.Count > 1 && ring[0].I == ring[^1].I && ring[0].J == ring[^1].J) ring.RemoveAt(ring.Count - 1);
        // the runs of tiles a wall can stand on (an own piece of the class counts), starting after a hole
        var ok = ring.Select(t => { var (x, z) = TileCentre(t); var at = W.PlacementOf(cls, x, z, 0, P); return at.Ok || at.Existing != null; }).ToList();
        int s0 = ok.IndexOf(false);
        var runs = new List<List<WTile>>();
        var cur = new List<WTile>();
        int n = ring.Count;
        for (int k = 0; k < n; k++)
        {
            int idx = s0 < 0 ? k : (s0 + 1 + k) % n;
            if (ok[idx]) { cur.Add(ring[idx]); if (cur.Count >= SEG_MAX) { runs.Add(cur); cur = new List<WTile>(); } }
            else if (cur.Count > 0) { runs.Add(cur); cur = new List<WTile>(); }
        }
        if (cur.Count > 0) runs.Add(cur);
        var segs = runs.Select(tiles =>
        {
            var seg = new WallSeg { Tiles = tiles };
            if (tiles.Count >= 4)
            {
                // the gate: the middle of a straight stretch (gateFit needs a piece on either side along one axis)
                int mid = tiles.Count / 2;
                for (int d = 0; d < tiles.Count - 2 && seg.Gate == null; d++) foreach (var m in new[] { mid + d, mid - d })
                {
                    if (m < 1 || m >= tiles.Count - 1 || seg.Gate != null) continue;
                    WTile a = tiles[m - 1], b = tiles[m], c = tiles[m + 1];
                    if (b.I - a.I == c.I - b.I && b.J - a.J == c.J - b.J) seg.Gate = b;
                }
            }
            return seg;
        }).ToList();
        // gates at least 24 m apart (TBG); a segment without a gate keeps a hole: the walls close every corner, so the
        // ring must not shut the village in
        var kept = new List<(double X, double Z)>();
        foreach (var seg in segs)
        {
            if (seg.Gate != null)
            {
                var (x, z) = TileCentre(seg.Gate);
                if (kept.Any(k => Hyp(k.X - x, k.Z - z) < 24)) { seg.Hole = seg.Gate; seg.Gate = null; } else kept.Add((x, z));
            }
            if (seg.Gate == null && seg.Hole == null && seg.Tiles.Count > 1) seg.Hole = seg.Tiles[seg.Tiles.Count / 2];
            if (seg.Hole != null) seg.Tiles = seg.Tiles.Where(t => t != seg.Hole).ToList();
        }
        return segs;
    }
    /// <summary>a gate that cannot be built: its tile becomes a hole (the own piece there is pulled down)</summary>
    void WallGateGiveUp(WallSeg seg)
    {
        var b = W.Walls?.At(seg.Gate!.I, seg.Gate.J)?.Wall;
        if (b != null && b.Alive && b.Owner == P) Hands.SelfDestruct(P, b);
        seg.Hole = seg.Gate; seg.Tiles = seg.Tiles.Where(x => x != seg.Gate).ToList(); seg.Gate = null;
    }
    void WallsThink(Census S)
    {
        if (!WallsActive(S)) return;
        var p = P;
        var Rw = Walls ??= new WallState();
        var cls = BestWallClass(S);
        if (cls == null) { var a0 = WallAct(WallList().FirstOrDefault()); if (a0 != null) Prerequisites(S, a0, null); return; }
        if (Rw.Ring == null || (Rw.Cls != cls && Rw.Ring.All(s => s.Done))) { Rw.Ring = PlanRing(S, cls); Rw.Cls = cls; }
        // the next class once a wall stands (upgrade_walls 1, VW:764)
        if (Params.UpgradeWalls != 0 && !double.IsNaN(Params.UpgradeWalls) && !Rw.Upgraded && Rw.Ring.Any(s => s.Pieces.Any(b => b.Built)))
        {
            var list = WallList(); int i = Array.IndexOf(list, cls);
            string? next = i + 1 < list.Length ? list[i + 1] : null;
            var a = next != null ? WallAct(next) : null;
            if (a != null && D.Check(p, a) != null) Prerequisites(S, a, null);
            Rw.Upgraded = true;
        }
        var act = WallAct(cls);
        var seg = Rw.Ring.FirstOrDefault(s => !s.Done);
        if (seg != null && act != null) WallSegmentThink(S, Rw, seg, act);
        foreach (var s in Rw.Ring) if (s.Done) { WallGateThink(S, Rw, s, cls); WallTowerThink(S, Rw, s); }
        WallArchersThink(S, Rw);
    }
    Building? PieceAt(WTile t)
    {
        var o = W.Walls?.At(t.I, t.J);
        var b = o?.Wall ?? o?.Gate ?? o?.Tower;
        return b != null && b.Owner == P ? b : null;
    }
    /// <summary>TBW: build the segment in batches; done when every tile has a finished piece</summary>
    void WallSegmentThink(Census S, WallState Rw, WallSeg seg, ActionDef act)
    {
        var p = P;
        seg.Pieces = seg.Tiles.Select(PieceAt).Where(b => b != null).Select(b => b!).ToList();
        var open = seg.Pieces.Where(b => !b.Built).ToList();
        if (seg.Pieces.Count == seg.Tiles.Count && open.Count == 0) { seg.Done = true; return; }
        if (open.Count > 0)
        {
            // builders: 1-2 workers on the nearest unfinished piece; 60 s without progress -> others
            double prog = open.Sum(b => b.Progress) + seg.Pieces.Count;
            if (seg.Prog == null || prog > seg.Prog + 1e-3) { seg.Prog = prog; seg.ProgT = W.Time; }
            var busy = S.Workers.Where(w => w.Task.Type == "build" && w.Task.Building != null && open.Contains(w.Task.Building)).ToList();
            if (busy.Count < (S.Workers.Count >= 8 ? 2 : 1) || W.Time - seg.ProgT > 60)
            {
                if (W.Time - seg.ProgT > 60) seg.ProgT = W.Time;
                var w = PickWorker(S, Pt.Of(open[0]), busy);
                if (w != null) Hands.Order(p, new[] { w }, new Order("build") { Target = open.Aggregate((a, b) => DistE(b, w) < DistE(a, w) ? b : a), Auto = true });
            }
            return;
        }
        // a new batch: the tiles still without a piece
        var todo = seg.Tiles.Where(t => PieceAt(t) == null).ToList();
        var (sx, sz) = TileCentre(todo[0]);
        var foes = WallFoes(sx, sz, 60);
        if (foes.Count > 0)
        {
            // CheckForEnemy: an attack on them instead (siege for mostly buildings, else suicide)
            int blds = foes.Count(e => e is Building);
            RequestAttack(S, blds * 2 > foes.Count ? "siege" : "suicide", foes[0].Owner, foes);
            return;
        }
        var cost = act.Cost;
        var paid = RES.Where(r => cost[r] > 0).Select(r => Math.Floor(p[r] / cost[r])).ToList();
        double n = paid.Count > 0 ? paid.Min() : double.PositiveInfinity;
        if (!double.IsFinite(n)) n = BATCH;
        if (n < 1) n = 3;
        n = Math.Min(BATCH, n);
        int placed = 0;
        foreach (var t in todo.Take((int)n))
        {
            var (x, z) = TileCentre(t);
            var b = Hands.StartConstruction(p, act, x, z, 0, new List<Unit>());
            if (b is string s) { if (s == "cost" || s == PwRemake.Sim.World.World.LIMIT) break; continue; }
            placed++;
        }
        if (placed == 0 && todo.Count > 0)
        {
            seg.Fails++;
            if (seg.Fails > 5) { seg.Tiles = seg.Tiles.Where(t => PieceAt(t) != null).ToList(); seg.Done = seg.Tiles.Count == 0 || seg.Tiles.All(t => PieceAt(t) is { Built: true }); }
        }
        seg.Prog = null;
    }
    /// <summary>enemy units (not workers), buildings, ships, hostile wild animals and nests within r (UM:615-702)</summary>
    List<Entity> WallFoes(double x, double z, double r)
    {
        var outp = new List<Entity>();
        foreach (var u in W.Units) if (u.Alive && !u.Inside && DistP(u, x, z) <= r && ((IsEnemyEntity(u) && !u.IsWorker) || (Multimap && u.Wild && u.Def!.Aggressive == 1))) outp.Add(u);
        foreach (var b in W.Buildings) if (IsEnemyEntity(b) && DistP(b, x, z) <= r) outp.Add(b);
        return outp;
    }
    /// <summary>TBG: the gate of a finished segment</summary>
    void WallGateThink(Census S, WallState Rw, WallSeg seg, string cls)
    {
        var p = P;
        if (seg.Gate == null || seg.GateObj != null || Rw.Gates >= MAX_GATES) return;
        if (seg.GateFails >= 5) { WallGateGiveUp(seg); return; }
        var act = WallAct(cls + "_gate");
        if (act == null || D.Check(p, act) != null) { seg.GateFails = 5; return; }      // (SEAS: no such class)
        if (W.LimitWhy(p, act) != null) return;                                       // (MIRAGE's building limits: later maybe)
        var (x, z) = TileCentre(seg.Gate);
        if (W.Buildings.Any(b => b.Alive && b.Owner == p && b.Def!.WallKind == "gate" && DistP(b, x, z) < 24)) { seg.GateFails = 5; return; }
        if (!CanPay(act.Cost, p.Res)) return;
        var w = PickWorker(S, new Pt(x, z));
        var b = Hands.StartConstruction(p, act, x, z, 0, w != null ? new List<Unit> { w } : new List<Unit>());
        if (b is string s) { if (s != "cost") seg.GateFails++; return; }
        seg.GateObj = (Building)b;
        Rw.Gates++;
        if (!Rw.WantArchers && ARCHERS.TryGetValue(p.Tribe, out var arch)) { Rw.WantArchers = true; AddOnTop(arch, "", count: S.CountOf(arch) + 3); }
    }
    /// <summary>TBT: a tower on the first tile of a long finished segment without a hole</summary>
    void WallTowerThink(Census S, WallState Rw, WallSeg seg)
    {
        var p = P;
        if (seg.TowerB != null || seg.Hole != null || seg.Tiles.Count < 3 || seg.TowerFails >= 10) return;
        if (Rw.Ring!.Count(s => s.TowerB != null && s.TowerB.Alive && !s.TowerB.Built) >= 2) return;
        var act = WallAct(TOWER.TryGetValue(p.Tribe, out var tw) ? tw : null);
        if (act == null || BuildCheck(p, act) != null || !CanPay(act.Cost, p.Res)) return;
        var (x, z) = TileCentre(seg.Tiles[0]);
        var w = PickWorker(S, new Pt(x, z));
        var b = Hands.StartConstruction(p, act, x, z, 0, w != null ? new List<Unit> { w } : new List<Unit>());
        if (b is string) { seg.TowerFails++; return; }
        seg.TowerB = (Building)b;
    }
    /// <summary>the archers on the wall near a gate</summary>
    void WallArchersThink(Census S, WallState Rw)
    {
        var p = P;
        if (!ARCHERS.TryGetValue(p.Tribe, out var cls) || !Rw.WantArchers) return;
        Rw.Archers = Rw.Archers.Where(u => u.Alive && u.Owner == p).ToList();
        foreach (var u in S.Pool) if (Rw.Archers.Count < 3 && u.Name == cls && !Claims.Has(u)) { Rw.Archers.Add(u); Claim(new[] { u }, Rw); }
        var gates = Rw.Ring!.Select(s => s.GateObj).Where(g => g != null && g.Alive && g.Built).Select(g => g!).ToList();
        if (gates.Count == 0 || S.Age < 3 || Rw.Sends >= 10) return;
        var off = Rw.Archers.Where(u => u.OnWall == null && u.Task.Type != "wall" && !u.Inside).ToList();
        if (off.Count == 0) return;
        Building? piece = null; double bd = 36;
        foreach (var g in gates) foreach (var b in W.Buildings)
        {
            if (b.Owner != p || !W.WalkableWall(b)) continue;
            double d = DistE(b, g);
            if (d <= bd) { bd = d; piece = b; }
        }
        if (piece == null) return;
        Hands.Order(p, off, new Order("use") { Target = piece, Auto = true });
        W.SetAggro(off, 0);
        Rw.Sends++;
    }
}
