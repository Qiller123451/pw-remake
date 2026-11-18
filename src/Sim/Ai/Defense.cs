// Defense module of the computer player: danger detection (CAiGoalDefendOutpost), the village guard
// (CAiGoalGuardVillage), defend mode for the workers (CAiGoalDefendMode), towers (CAiGoalBuildTowers) and the
// defence areas of campaign triggers (AIDA). docs/spec/ai.md §7. TribeAI methods; S = the census.
//
// Differences to the original, on purpose: the original sends its whole pool at any enemy near its buildings; here
// as many defenders go as the threat needs (level score >= 1.5 x attackers + 2), so a scout does not empty the
// village. Workers hide only when an attacker comes close to them (22 m), not all at once.
//
// Ported from remake/src/game/ai/defense.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Ai;

/// <summary>the units of the base guard or of a defence area (AIDA): claimed units leave it by Drop()</summary>
public sealed class Pool : IClaimer
{
    public readonly OrderedSet<Unit> Units = new();
    public string Id = "";
    public double X, Z, R;
    public int Max;
    /// <summary>where the guard was sent last</summary>
    public Pt? At;
    public void Drop(Unit u) => Units.Delete(u);
}

/// <summary>the village is alarmed: by whom (enemies, or hostile animals only)</summary>
public sealed class AiAlarm
{
    public List<Unit> Units = new();
    public bool Animal;
}

public sealed partial class TribeAI
{
    /// <summary>an enemy this close to one of the village's buildings is a danger</summary>
    const double NEAR = 28;
    /// <summary>workers take cover from attackers this close</summary>
    const double HIDE = 22;
    const double SAFE = 36;
    /// <summary>the Aje's main building, a unit</summary>
    const string COLLECTOR = "aje_resource_collector";

    // defence state
    public AiAlarm? Alarm;
    /// <summary>attacks are refused while enemies are in the village (FM:211)</summary>
    public bool Defending;
    /// <summary>the workers' defend mode: no new workers</summary>
    public bool DefendMode;
    public Pool? Guard;
    /// <summary>a campaign's guard place (defend_place)</summary>
    public Pt? GuardBase;
    Pt? _guardAt;
    int _guardN;
    OrderedSet<Unit>? _guardSpots;
    /// <summary>workers that took cover; workers that fight (the militia)</summary>
    public OrderedSet<Unit>? Hidden, MilitiaSet;
    /// <summary>who shoots at the village from beyond the alarm distance: unit -> when</summary>
    OrderedMap<Unit, double>? _hitters;
    (Unit U, double T)? _animalHit;
    double _hitT = -99;
    Pt? _hitAt;
    /// <summary>the absolute number (World.EventBase + index) of the next world event to read</summary>
    long _evAbs;
    WorldEvent? _evLast;

    /// <summary>what happened to this player since the last look (world events: deaths, hits)</summary>
    void ReadEvents()
    {
        var ev = W.Events; var p = P;
        if (ev.Count == 0) { _evAbs = W.EventBase; _evLast = null; return; }
        long rel = _evAbs - W.EventBase;
        int _evI = rel < 0 || rel > ev.Count ? 0 : (int)rel;
        if (_evI > 0 && ev[_evI - 1] != _evLast) _evI = 0;
        for (int i = _evI; i < ev.Count; i++)
        {
            var e = ev[i];
            if (e.Type == "attacked")
            {
                var v = e.E("entity"); var by = e.E("by");
                if (v == null || v.Owner != p || by == null || !by.Alive) continue;
                if (by.Owner != null)
                {
                    if (p.IsEnemy(by.Owner))
                    {
                        LastAttacker = by.Owner; _hitT = W.Time; _hitAt = Pt.Of(v);
                        // who shoots at the village from beyond the alarm distance (a mortar, a catapult) is a threat too
                        // (the Aje's main building is a unit: the resource collector)
                        if (by is Unit bu && (v is Building || (v is Unit vu && (vu.IsWorker || vu.Name == COLLECTOR)))) (_hitters ??= new OrderedMap<Unit, double>()).Set(bu, W.Time);
                    }
                }
                else if (by is Unit au) _animalHit = (au, W.Time);
            }
            else if (e.Type == "died")
            {
                var v = e.E("entity"); var k = e.E("killer");
                // a worker killed by a wild animal or an enemy: a pest patrol there (DA:689; the original asks for it when every
                // worker of an economy task is gone, TargetRealization:206-227 - the remake's workers have no shared tasks)
                if (v is Unit vu && v.Owner == p && vu.IsWorker && k != null && (k.Owner == null || p.IsEnemy(k.Owner))) RequestPest(Pt.Of(v));
                if (v is Unit vs && v.Owner == p && (vs.Naval || vs.Amphib)) FerryLost(vs);          // (a crossing's transport - the hovercraft and the turtle are amphibious: Naval.cs)
            }
        }
        _evAbs = W.EventBase + ev.Count; _evLast = ev[^1];
    }
    bool NearOwnBuilding(double x, double z, double r)
    {
        bool hit = false;
        W.BHash.Query(x, z, r + 24, b => { if (!hit && b.Alive && b.Owner == P && string.IsNullOrEmpty(b.Def!.WallKind) && b.SurfDist(x, z) < r) hit = true; });
        return hit;
    }
    /// <summary>the village is also where its workers are: an enemy fighter next to a worker inside the base range (the
    /// farthest building + 30 m, GV:734-770) is in the village</summary>
    bool NearOwnWorker(Census S, Unit u)
    {
        if (u.IsWorker || S.Home == null) return false;
        var h = S.Home.Value;
        if (S.Range == null) { double r = 60; foreach (var b in S.Blds) r = Math.Max(r, DistP(b, h)); S.Range = r + 30; }
        if (DistP(u, h) > S.Range) return false;
        bool hit = false;
        W.UHash.Query(u.Pos.X, u.Pos.Z, 18, w => { if (!hit && w.Alive && w.Owner == P && w.IsWorker && DistE(w, u) < 18) hit = true; });
        return hit;
    }
    /// <summary>where the idle army stands: the place a campaign script named, else between the village and the enemy -
    /// moved to open ground the village can walk to (OpenGround)</summary>
    public Pt GuardPoint(Census S) => OpenGround(GuardPointRaw(S), S.Home!.Value);
    /// <summary>the guard point as the JS has it: the script's place, else 30 m from the main building towards the enemy
    /// (wherever that is: a cliff, a building, the sea)</summary>
    public Pt GuardPointRaw(Census S)
    {
        if (GuardBase != null) return GuardBase.Value;
        var h = S.Home!.Value; var e = EnemyHint(S);
        double a = SMath.Atan2(e.Z - h.Z, e.X - h.X);
        return new Pt(h.X + SMath.Cos(a) * 30, h.Z + SMath.Sin(a) * 30);
    }

    /// <summary>open ground clearance (in nav cells, 2 m) the guard point wants around it: room for a squad</summary>
    const int GUARD_CLEAR = 4;
    readonly Dictionary<(int Cell, int Home), (int Version, Pt At)> _openAt = new();
    /// <summary>
    /// The nearest place to p with open ground around it (GUARD_CLEAR cells to the nearest obstacle, less if there is
    /// no such place within 50 m) in the ground region of home (that of the nearest free cell when home is blocked),
    /// i.e. a place the village's units can walk to; p itself when there is none. Without
    /// this the point 30 m from the main building could lie in a cliff, a building or a lake (MIRAGE mission 3: the
    /// Amazons' guard point is in the cliff behind their slaughterhouse) and the whole idle army packed into the
    /// nearest reachable corner, a knot of 20-30 units against the building (the remake's own rule: the original's
    /// guard position is a building, a defence point or the start location, GV:399-428, whose surroundings the
    /// engine's group move spreads). Cached per cell and nav version.
    /// </summary>
    public Pt OpenGround(Pt p, Pt home)
    {
        var nav = W.Nav;
        int c = nav.Idx(p.X, p.Z), hc = nav.Idx(home.X, home.Z);
        if (_openAt.TryGetValue((c, hc), out var o) && o.Version == nav.Version) return o.At;
        if (_openAt.Count > 64) _openAt.Clear();
        Pt at = p;
        int reg = -1;
        if (nav.Region != null)
        {
            // (home on a building or on ground without a region: the region of the nearest free labelled cell)
            reg = nav.Block[hc] == 0 ? nav.Region[hc] : -1;
            if (reg < 0) reg = NearestRegion(nav, hc, 30);
            if (reg < 0) { _openAt[(c, hc)] = (nav.Version, p); return p; }     // (no ground around home: as it was)
        }
        nav.UpdateClearance();
        int n = nav.N, ci = c % n, cj = c / n;
        // (also off the buildings that do not block the way - the Aje slaughterhouse, fields, traps: guards would stand
        // in them)
        bool Ok(int k, int need)
        {
            if (nav.Clear[k] < need || (reg >= 0 && nav.Region![k] != reg)) return false;
            var (x, z) = nav.Center(k);
            return !InBuilding(x, z, need * nav.Cell * 0.5);
        }
        for (int need = GUARD_CLEAR; need >= 1; need--)
        {
            int best = -1; double bd = 1e18;
            for (int r = 0; r <= 25 && best < 0; r++)
                for (int j = cj - r; j <= cj + r; j++)
                {
                    if (j < 0 || j >= n) continue;
                    int step = j == cj - r || j == cj + r ? 1 : Math.Max(1, 2 * r);
                    for (int i = ci - r; i <= ci + r; i += step)
                    {
                        if (i < 0 || i >= n) continue;
                        int k = j * n + i;
                        if (!Ok(k, need)) continue;
                        double d = (double)(i - ci) * (i - ci) + (double)(j - cj) * (j - cj);
                        if (d < bd) { bd = d; best = k; }
                    }
                }
            if (best >= 0)
            {
                if (best != c) { var (x, z) = nav.Center(best); at = new Pt(x, z); }
                break;
            }
        }
        _openAt[(c, hc)] = (nav.Version, at);
        return at;
    }

    /// <summary>the ground region of a point (of the nearest free cell within maxR cells when it stands on a blocked one),
    /// -1: unknown (no regions, or no free cell near)</summary>
    public int GroundRegion(double x, double z, int maxR = 30)
    {
        var nav = W.Nav;
        if (nav.Region == null) return -1;
        int c = nav.Idx(x, z);
        int r = nav.Block[c] == 0 ? nav.Region[c] : -1;
        return r >= 0 ? r : NearestRegion(nav, c, maxR);
    }
    /// <summary>the ground region of the nearest free cell around c that has one (rings up to maxR), or -1</summary>
    static int NearestRegion(NavGrid nav, int c, int maxR)
    {
        int n = nav.N, ci = c % n, cj = c / n; var R = nav.Region!;
        for (int r = 0; r <= maxR; r++)
        {
            int best = -1; double bd = 1e18;
            for (int j = cj - r; j <= cj + r; j++)
            {
                if (j < 0 || j >= n) continue;
                int step = j == cj - r || j == cj + r ? 1 : Math.Max(1, 2 * r);
                for (int i = ci - r; i <= ci + r; i += step)
                {
                    if (i < 0 || i >= n) continue;
                    int k = j * n + i;
                    if (nav.Block[k] != 0 || R[k] < 0) continue;
                    double d = (double)(i - ci) * (i - ci) + (double)(j - cj) * (j - cj);
                    if (d < bd) { bd = d; best = k; }
                }
            }
            if (best >= 0) return R[best];
        }
        return -1;
    }

    /// <summary>a building (any, also one that is walked over) within m of its surface</summary>
    bool InBuilding(double x, double z, double m)
    {
        bool hit = false;
        W.BHash.Query(x, z, m + 24, b => { if (!hit && b.Alive && b.SurfDist(x, z) < m) hit = true; });
        return hit;
    }

    // ------------------------------------------------------------------ danger and defenders (DefendOutpost, GV:365-594)
    void DefenseThink(Census S)
    {
        var p = P;
        if (Params.NoDefence) { Alarm = null; Defending = false; return; }
        var guard = Guard ??= new Pool();
        var threats = new List<Unit>();
        var animals = new List<Unit>();
        // the Aje's main building is a unit (the resource collector, moving or set down): the village is around it too
        var cols = S.Units.Where(c => c.Alive && !c.Inside && c.Name == COLLECTOR).ToList();
        foreach (var u in W.Units)
        {
            if (!u.Alive || u.Inside || u.Owner == p) continue;
            if (u.Owner != null) { if (!p.IsEnemy(u.Owner) || u.Naval || W.HiddenFrom(u, p)) continue; }
            else if (!(_animalHit is { } ah && ah.U == u && W.Time - ah.T < 12)) continue;
            if (!NearOwnBuilding(u.Pos.X, u.Pos.Z, NEAR) && !cols.Any(c => DistE(c, u) < NEAR + c.Radius) && !(u.Owner != null && (NearOwnWorker(S, u) || (_hitters != null && W.Time - (_hitters.TryGetValue(u, out var ht) ? ht : -99) < 15)))) continue;
            (u.Owner != null ? threats : animals).Add(u);
        }
        // hostile animals: fewer than seven fighters leave them alone (GV:530); never more than a handful go
        if (threats.Count > 0 || S.Fighters.Count < 7) { if (threats.Count == 0) animals = new List<Unit>(); }
        var all = threats.Count > 0 ? threats : animals;
        Alarm = all.Count > 0 ? new AiAlarm { Units = all, Animal = threats.Count == 0 } : null;
        // attacks are refused while enemies are in the village (FM:211); the workers' defend mode
        Defending = threats.Any(u => !u.IsWorker);
        DefendMode = Defending && S.Home != null && threats.Any(u => DistP(u, S.Home.Value) < 45);
        ProtectWorkers(S, all);
        foreach (var u in guard.Units.ToList()) if (!u.Alive || Locked.Has(u)) { guard.Units.Delete(u); Claims.Delete(u); }
        if (_hitters != null && _hitters.Count > 40) foreach (var (u, t) in _hitters) if (!u.Alive || W.Time - t > 15) _hitters.Delete(u);
        if (all.Count == 0)
        {
            if (guard.Units.Count > 0) { Release(guard.Units.ToList()); guard.Units.Clear(); }
            if (MilitiaSet != null && MilitiaSet.Count > 0) Militia(S, new List<Unit>(), 0, 0);
            return;
        }
        double cx = 0, cz = 0, need = 2;
        foreach (var u in all) { cx += u.Pos.X; cz += u.Pos.Z; if (!u.IsWorker) need += u.Level * 1.5; else need += 0.5; }
        cx /= all.Count; cz /= all.Count;
        if (Alarm!.Animal) need = Math.Min(need, 5);
        double have = Score(guard.Units);
        if (have < need)
        {
            var cand = S.Pool.Where(u => !Claims.Has(u)).ToList();
            // a squad that is still being put together in the village defends it too (its attack then fails or waits), and
            // so does a hunt (food, skulls) - it went on chasing a Corythosaurus while enemies beat the main building down
            if (!Alarm.Animal) foreach (var a in Attacks) if (!a.Scripted && (a.Hunt || a.State == "build" || a.State == "gather")) foreach (var u in a.Units) if (u.Alive && !u.Inside) cand.Add(u);
            cand = cand.OrderBy(u => DistP(u, cx, cz)).ToList();
            var add = new List<Unit>();
            foreach (var u in cand) { if (have >= need || (Alarm.Animal && guard.Units.Count + add.Count >= 2)) break; add.Add(u); have += u.Level + (u.Def!.Unique ? 2 : 0); }
            foreach (var u in add) { if (Claims.TryGetValue(u, out var c) && c is IClaimer cl) cl.Drop(u); guard.Units.Add(u); }
            Claim(add, guard);
        }
        Militia(S, threats, have, need);
        if (!Alarm.Animal) CryForHelp(S, threats.Where(u => !u.IsWorker).ToList(), guard.Units.Count);      // (GV:696-716, Chat.cs)
        bool moved = guard.At == null || Hyp(guard.At.Value.X - cx, guard.At.Value.Z - cz) > 12;
        if (moved) guard.At = new Pt(cx, cz);
        foreach (var u in guard.Units)
        {
            var t = u.Task.Type;
            if (t == "attack" && u.Task.Target != null && u.Task.Target.Alive) continue;
            if (t == "idle" || t == "hold" || moved || t == "move")
            {
                if (Alarm.Animal) Hands.Order(p, new[] { u }, Order.Attack(all[0]));
                else Hands.Order(p, new[] { u }, new Order("attackmove") { X = cx, Z = cz, Auto = true });
            }
        }
    }
    // A village without fighters (or with fewer than a third of what it would need) is defended by its workers: the
    // ones near an attacker gang up on it. (CheckForWorkerSupport, GV:1142-1191 - in the scripts, but never called.)
    void Militia(Census S, List<Unit> threats, double have, double need)
    {
        var m = MilitiaSet ??= new OrderedSet<Unit>();
        var foes = threats.Where(u => !u.IsWorker && u.Cls != "VHCL").ToList();
        bool on = foes.Count > 0 && have < need / 3 && S.Workers.Count >= 2 * foes.Count + 2;
        foreach (var w in m.ToList())
        {
            if (w.Alive && on && w.Task.Type == "attack" && w.Task.Target != null && w.Task.Target.Alive) continue;
            m.Delete(w);
            if (w.Alive && !on && w.Task.Type == "attack") { W.ReleaseTask(w); w.Task = UnitTask.Idle(); w.Path.Clear(); }
        }
        if (!on) return;
        foreach (var f in foes)
        {
            int n = 0;
            foreach (var w in m) if (w.Task.Target == f) n++;
            int want = Math.Min(6, 2 + f.Level * 2);
            if (n >= want) continue;
            var near = S.Workers.Where(w => !m.Has(w) && !w.Inside && w.Task.Type != "build" && DistE(w, f) < 45).OrderBy(w => DistE(w, f)).Take(want - n).ToList();
            foreach (var w in near) { m.Add(w); Hidden?.Delete(w); }
            if (near.Count > 0) Hands.Order(P, near, Order.Attack(f));
        }
    }
    /// <summary>workers near an attacker take cover: Hu in bunkers (four each), Ninigi dig in, the others run to the main
    /// building; they come back when the attacker is gone (DMo:118-291)</summary>
    void ProtectWorkers(Census S, List<Unit> threats)
    {
        var hid = Hidden ??= new OrderedSet<Unit>();
        var fighters = threats.Where(u => !u.IsWorker).ToList();
        foreach (var w in hid.ToList())
        {
            if (!w.Alive) { hid.Delete(w); continue; }
            if (fighters.Any(u => DistE(u, w) < SAFE)) continue;
            if (W.Time - w.AiHidT < 6) continue;
            hid.Delete(w);
            // out of the bunker, out of the ground (any order digs it out), or the walk to cover ends
            if (w.Inside) Hands.Order(P, new[] { w }, new Order("unload"));
            else if (w.St.Camo.Has("entr")) Hands.Order(P, new[] { w }, new Order("stop"));
            else if (w.Task.Type == "entrench" || w.Task.Type == "move") { W.ReleaseTask(w); w.Task = UnitTask.Idle(); w.Path.Clear(); }
        }
        if (fighters.Count == 0) return;
        string tribe = P.Tribe;
        var bunkers = tribe == "Hu" ? S.Blds.Where(b => b.Built && b.Def!.Script == "CBunker").ToList() : new List<Building>();
        foreach (var w in S.Workers)
        {
            if (hid.Has(w) || w.Inside || Locked.Has(w) || (MilitiaSet != null && MilitiaSet.Has(w))) continue;
            var foe = fighters.FirstOrDefault(u => DistE(u, w) < HIDE);
            if (foe == null) continue;
            w.AiHidT = W.Time;
            var bunker = bunkers.Where(b => (W.PassengersOf(b)?.Count ?? 0) < 4 && DistE(b, w) < 100).OrderBy(b => b.DistTo(w)).FirstOrDefault();
            if (bunker != null && W.CanBoard(w, bunker)) { Hands.Order(P, new[] { w }, new Order("board") { Target = bunker, Auto = true }); hid.Add(w); continue; }
            if (tribe == "Ninigi")
            {
                var a = W.MovesOf(w).FirstOrDefault(x => x.Id == "entrench");
                if (a != null && W.MoveCheck(w, a) == null && Hands.UseMove(P, w, a) == null) { hid.Add(w); continue; }
            }
            // away from the attacker, towards the main building
            if (S.Home != null && DistP(w, S.Home.Value) > 12 && w.Task.Type != "attack")
            {
                var h = S.Home.Value; double ox = (R.Next() - 0.5) * 10, oz = (R.Next() - 0.5) * 10;
                Hands.Order(P, new[] { w }, new Order("move") { X = h.X + ox, Z = h.Z + oz, Auto = true }); hid.Add(w);
            }
        }
    }

    // ------------------------------------------------------------------ the village guard, defence areas, towers
    void GuardThink(Census S)
    {
        _guardN++;
        if (Areas.Count > 0) AreasThink(S);
        GuardItem(S);
        if (!EconomyOff && _guardN % 10 == 3) TowerThink(S);
        if (Params.GuardVillage == 0 || double.IsNaN(Params.GuardVillage) || Params.NoDefence) return;
        // the idle army patrols to a building that was just hit, else it stands at the guard point (GV:399-428)
        Pt? at = null;
        if (_hitAt != null && W.Time - _hitT < 15 && Alarm == null) at = _hitAt;
        var g = at ?? CrystalGuard() ?? GuardPoint(S);             // (a Defender game's crystal, GameTypes.cs)
        bool changed = _guardAt == null || D2(_guardAt.Value, g) > 8;
        _guardAt = g;
        const double RG = 30;                                      // SetGuardRange(30)
        // the guards that got their own spot at this guard point. The others - trained units that walked to the
        // producers' rally point (the guard point itself), squads sent home to it - stood on that one point in a knot:
        // they get a spot too, once.
        var spotted = _guardSpots ??= new OrderedSet<Unit>();
        if (changed || spotted.Count > 300) spotted.Clear();
        foreach (var u in S.Pool)
        {
            if (Claims.Has(u)) continue;
            var t = u.Task.Type;
            if (t == "attack" || t == "special" || t == "heal") continue;
            bool near = DistP(u, g) <= RG;
            if (near && (t != "idle" || spotted.Has(u))) continue;
            if (near || t == "idle" || t == "hold" || (changed && (t == "move" || t == "attackmove") && u.Task.Then == null))
            {
                spotted.Add(u);
                double ox = (R.Next() - 0.5) * 16, oz = (R.Next() - 0.5) * 16;
                // (a spot off the open ground - in a cliff, a building, also one walked over - ended the walk at the nearest
                // corner, or inside the building: another spot, at last the point itself)
                for (int tries = 0; tries < 4 && (!W.Nav.IsFree(g.X + ox, g.Z + oz) || InBuilding(g.X + ox, g.Z + oz, 1)); tries++)
                    if (tries == 3) { ox = 0; oz = 0; } else { ox = (R.Next() - 0.5) * 16; oz = (R.Next() - 0.5) * 16; }
                Hands.Order(P, new[] { u }, new Order("attackmove") { X = g.X + ox, Z = g.Z + oz, Auto = true });
            }
        }
    }
    /// <summary>GuardVillage.CheckForItem (GV:1107-1138, every think, skirmish maps): the first guard with empty hands picks
    /// up a free item lying within 60 m of it</summary>
    void GuardItem(Census S)
    {
        if (!Multimap) return;
        var items = W.Items.ToList();
        if (items.Count == 0) return;
        var u = S.Pool.FirstOrDefault(x => !Claims.Has(x) && x.Item == null && W.CanCarry(x) && !x.Inside);
        if (u == null || u.Task.Type == "pickup") return;
        var it = items.FirstOrDefault(x => W.ItemLying(x) && DistE(x, u) <= 60);
        if (it != null) Hands.Order(P, new[] { u }, new Order("pickup") { Target = it, Auto = true });
    }
    /// <summary>AIDA {id, position, radius, max_units}: a guard of up to maxUnits pool units in a disc; maxUnits &lt;= 0
    /// removes it</summary>
    public void SetDefenceArea(string id, Pt? pos, double radius, double maxUnits)
    {
        var old = Areas.Get(id);
        if (!(maxUnits > 0) || pos == null)
        {
            if (old != null) { Release(old.Units.ToList()); Areas.Delete(id); }
            return;
        }
        var a = old ?? new Pool { Id = id };
        a.X = pos.Value.X; a.Z = pos.Value.Z; a.R = Math.Max(5, double.IsFinite(radius) && radius != 0 ? radius : 20); a.Max = (int)Math.Floor(maxUnits + 0.5);
        Areas.Set(id, a);
        Next["guard"] = Math.Min(N("guard"), W.Time + 0.2);
    }
    void AreasThink(Census S)
    {
        foreach (var a in Areas.Values.ToList())
        {
            foreach (var u in a.Units.ToList()) if (!u.Alive || Locked.Has(u) || Claims.Get(u) != a) a.Units.Delete(u);
            // four more units per think until the area is manned (AddDefensePool(.., 4, ..))
            if (a.Units.Count < a.Max)
            {
                var cand = S.Pool.Where(u => !Claims.Has(u)).OrderBy(x => DistP(x, a.X, a.Z)).Take(Math.Min(4, a.Max - a.Units.Count)).ToList();
                foreach (var u in cand) a.Units.Add(u);
                Claim(cand, a);
            }
            Unit? foe = null; double fd = 1e9; Pt? spot = null;
            W.UHash.Query(a.X, a.Z, a.R + 10, e => { if (!IsEnemyEntity(e) || e.Inside || W.HiddenFrom(e, P)) return; double d = DistP(e, a.X, a.Z); if (d < a.R + e.Radius && d < fd) { fd = d; foe = e; } });
            foreach (var u in a.Units)
            {
                var t = u.Task.Type;
                if (t == "attack" && u.Task.Target != null && u.Task.Target.Alive) { if (DistP(u, a.X, a.Z) > a.R + 35) Hands.Order(P, new[] { u }, new Order("move") { X = a.X, Z = a.Z, Auto = true }); continue; }
                if (foe != null) { if (t == "idle" || t == "hold") Hands.Order(P, new[] { u }, new Order("attackmove") { X = foe.Pos.X, Z = foe.Pos.Z, Auto = true }); continue; }
                // (the area's open ground - of the ground the centre is on, which may be another island than the
                // village's: a centre in a cliff or a building gathered its guards in the nearest corner, and sent them
                // there again on every think as they never got near the centre)
                var c = spot ??= OpenGround(new Pt(a.X, a.Z), new Pt(a.X, a.Z));
                double d = DistP(u, c.X, c.Z);
                if ((t == "idle" || t == "hold") && d > Math.Max(6, a.R * 0.6))
                {
                    double ang = R.Next() * 6.283, r = R.Next() * Math.Min(a.R * 0.5, 12);
                    double tx = c.X + SMath.Cos(ang) * r, tz = c.Z + SMath.Sin(ang) * r;
                    if (!W.Nav.IsFree(tx, tz) || InBuilding(tx, tz, 1)) { tx = c.X; tz = c.Z; }
                    Hands.Order(P, new[] { u }, new Order("attackmove") { X = tx, Z = tz, Auto = true });
                }
            }
        }
    }
    /// <summary>the best tower the tribe can build now</summary>
    ActionDef? BestTower(Census S)
    {
        var p = P;
        ActionDef? best = null; double bc = -1;
        foreach (var a in D.Actions(p))
        {
            if (a.Cat != "Build/BLDG" || a.Results.Count == 0 || BuildCheck(p, a) != null) continue;      // (a tower at its MIRAGE limit: the next best)
            string cls = a.Results[0].Obj; var def = D.Def(cls, p);
            if (def == null || def.WallKind != "tower") continue;
            var ws = D.WeaponSetOf(cls, 1, p);
            if (ws.Long?.Projectile == null) continue;
            double c = a.Cost.Food + a.Cost.Wood + a.Cost.Stone;
            if (c > bc) { bc = c; best = a; }
        }
        return best;
    }
    /// <summary>towers where buildings are not covered by one (DM:752-823), and at a new stone site (CR:373); never next to
    /// enemies. At most 1 + epoch of them (the original has no such limit: its planner's priorities do the limiting)</summary>
    void TowerThink(Census S)
    {
        var p = P;
        if (Params.UpgradeTowers == 0 || double.IsNaN(Params.UpgradeTowers) || Params.NoTowers || Alarm != null) return;
        if (GameType == "Defender" && !IsDefender) return;           // the attackers build no towers (DM:148)
        if (Extra.Any(e => e.Tower) || S.Blds.Any(b => !b.Built && b.Def!.WallKind == "tower")) return;
        var a = BestTower(S);
        if (a == null) return;
        var towers = S.Blds.Where(b => b.Def!.WallKind == "tower" && b.Weapons.Long != null).ToList();
        if (towers.Count >= 1 + S.Age + (Letter == "S" ? 1 : 0)) return;
        string cls = a.Results[0].Obj;
        var ws = D.WeaponSetOf(cls, 1, p); var cs = ws.Long != null ? D.WeaponStatsOf(ws.Long, cls, p) : null;
        double reach = (cs != null ? cs.Range : 24) * 1.2;
        Pt? spot = TowerAt != null && !DangerAt(TowerAt.Value.X, TowerAt.Value.Z) ? TowerAt : null;
        if (spot == null)
        {
            double bv = 0;
            foreach (var b in S.Blds)
            {
                if (!b.Built || !string.IsNullOrEmpty(b.Def!.WallKind) || towers.Any(t => t.DistTo(b) < reach)) continue;
                if (Regions.Count > 0 && RegionValue("DefensiveCoverage", b.Pos.X, b.Pos.Z) < 0) continue;
                double v = (b == S.Base ? 3 : 2) + R.Next() * 0.5;          // (every building has a queue: 2)
                if (v > bv && !DangerAt(b.Pos.X, b.Pos.Z)) { bv = v; spot = Pt.Of(b); }
            }
        }
        if (spot == null) return;
        TowerAt = spot;
        if (!Extra.Any(e => e.R.Cls == cls) && AddOnTop("BLDG/" + cls, "", unique: false)) Extra[^1].Tower = true;
    }
}
