// The trigger engine's view of a running mission (Scripting's IMissionHost) implemented on the campaign's world:
// objects are the registry's record ids, positions MAP coordinates, players slots (docs/TRIGGERS.md §4). Every method
// is what the JS conditions and actions did on G.campaign (setup.js), the registry (registry.js), the world
// (game/scripting.js, the systems) and the mission UI / audio / AI brains - the presentation goes to an
// IMissionPresenter (the game's mission UI; NullPresenter headless), the computer players to their ICampaignBrain.
//
// The host also keeps what the JS engine kept beside the triggers but that needs the world: the waypoint patrols
// (engine.js startPatrol / patrolTick: they need the unit's task), the effects of EFCT (engine.js playEffect: data for
// the view), the reveals of SFOW and the running sequence's actors (sequence.js).
//
// Ported from the world side of remake/src/game/campaign/actions.js, conditions.js and engine.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Campaign;

/// <summary>An effect of the particle database that a trigger plays (EFCT): its model plays its clip "anim" at (X, Z)
/// until Until (game time; +Infinity = until the mission ends). The view draws it.</summary>
public sealed class MissionEffect
{
    public string Gfx = "";
    public double X, Z, Until;
}

/// <summary>A unit walking WYPT waypoints (engine.js patrols).</summary>
public sealed class Patrol
{
    public Unit U = null!;
    /// <summary>game (x, z) and map height</summary>
    public List<(double X, double Z, double Mz)> Pts = new();
    public int Mode, I, Dir = 1, Tries;
    public double Speed;
    public bool Straight;
}

public sealed class MissionHost : IMissionHost
{
    static readonly Logger L = Log.For("Campaign.Host");
    static readonly int[] STD_PYRAMID = { 25, 15, 8, 3, 1 };
    readonly Campaign C;
    public IMissionPresenter Presenter;
    GameWorld W => C.World;
    /// <summary>the patrols of WYPT, by unit (insertion order: the order they are checked)</summary>
    public readonly OrderedMap<Unit, Patrol> Patrols = new();
    double _patrolT;
    /// <summary>EFCT effects that play now (the view draws them)</summary>
    public readonly List<MissionEffect> Effects = new();
    readonly List<Reveal> _reveals = new();
    /// <summary>the sequence on show (its actors, its reveal)</summary>
    public SequencePlay? SeqPlay;
    Reveal? _seqReveal;
    readonly List<Reveal> _seqRevealsCoop = new();
    /// <summary>the mission has ended (won, text); set by EndMission</summary>
    public (bool Won, string Text)? Ended;

    public MissionHost(Campaign c, IMissionPresenter? presenter)
    {
        C = c;
        Presenter = presenter ?? new NullPresenter();
        C.Host = this;
        C.QuestionMarkChanged += m => Presenter.QuestionMarkChanged(m);
    }

    // ------------------------------------------------------------------ helpers
    Rec? R(int id) => C.Objects.ById(id) is { Alive: true } r ? r : null;
    Entity? E(int id) => R(id)?.Entity is { Alive: true } e ? e : null;
    Unit? U(int id) => E(id) as Unit;
    (double X, double Z) G(double mx, double my) => C.ToGame(mx, my);
    Player? P(int slot) => C.PlayerAt(slot);
    static int Id(Rec? r) => r?.Id ?? -1;
    // ---- the co-op campaign's seat mapping (docs/COOP.md "Triggers"): "the player" (seat 1's slot) is every seat in
    // conditions, the seat `give_to` in what is given, every seat in what is set for the player
    PwRemake.Sim.Coop.CoopRuntime? Co => C.Coop;
    /// <summary>the slots a player parameter stands for</summary>
    IReadOnlyList<int> Slots(int slot) => Co?.Expand(slot) ?? new[] { slot };
    /// <summary>where something given to `slot` goes</summary>
    int Give(int slot) => Co?.GiveTo(slot) ?? slot;
    public bool OwnerMatches(int actual, int query) => Co?.OwnerMatches(actual, query) ?? actual == query;
    public bool IsHumanSide(int slot) => slot == C.HumanSlot || (Co?.IsSeatSlot(slot) == true && Co.SeatOfSlot(slot)?.Ai == false);
    void StopPatrol(Unit u) => Patrols.Delete(u);

    /// <summary>the host's work of a step before the trigger engine runs (Campaign.Update): patrols, effects, highlights</summary>
    public void Tick(double dt)
    {
        if (Patrols.Count > 0) PatrolTick(dt);
        if (Effects.Count > 0) Effects.RemoveAll(e => e.Until <= W.Time);
        if (W.Highlights.Count > 0) foreach (var (e, until) in W.Highlights.ToList()) if (until <= W.Time || !e.Alive) W.Highlights.Delete(e);
    }

    // ------------------------------------------------------------------ mission
    public int Difficulty => C.Difficulty;
    public int HumanSlot => C.HumanSlot;
    public double Random() => W.Rng.Next();

    // ------------------------------------------------------------------ object queries (registry.js)
    public IReadOnlyList<int> Select(TriggerParams p, string prefix) => C.Objects.Select(p, prefix).Select(r => r.Id).ToList();
    public bool Matches(int obj, TriggerParams p, string prefix) => C.Objects.Matches(R(obj), p, prefix);

    public ObjectInfo? Info(int obj)
    {
        var r = R(obj);
        if (r == null) return null;
        var (gx, gz) = C.Objects.PosOf(r);
        var (mx, my) = C.ToMap(gx, gz);
        var e = r.Entity is { Alive: true } en ? en : null;
        var i = new ObjectInfo
        {
            Id = r.Id, Guid = r.Guid ?? "", Name = r.Name, Class = r.Cls, Type = r.Type, Owner = r.Slot, X = mx, Y = my,
            HasEntity = e != null, Alive = true, IsUnit = e is Unit, IsBuilding = e is Building,
            IsItem = r.Type == "ITEM" && r.Prop is { Alive: true },
        };
        if (e != null)
        {
            i.Z = e.Pos.Y;
            i.Rot = Campaign.Heading(e is Building b0 ? b0.Rot : e.Heading);
            i.Hp = e.Hp; i.MaxHp = e.MaxHp; i.Radius = e.Radius;
            // SGHT: the JS read stats.sight, which its rules never had - every object saw 25 m (the engine's default)
            i.Sight = 0;
            i.Parked = e.Parked;
            i.Produces = Produces(e);
            i.WallLike = e.Def != null && (e.Def.WallKind != null || e.Def.Gate || e.Name.Contains("trap"));
            if (e is Unit u)
            {
                i.Level = u.Level; i.Task = u.Task.Type; i.CountsInPop = u.CountsInPop;
                i.Inside = u.Inside ? Id(C.TransportOf(u)?.Rec) : -1;
                i.Passengers = u.Passengers?.Count ?? 0; i.Capacity = (int)W.Capacity(u);
                i.CarriedItem = u.Item?.Name;
            }
            else if (e is Building b)
            {
                i.Built = b.Built;
                i.GateState = b.Def?.Gate == true ? b.GateState.ToString() : null;
                i.Task = b.Built ? "" : "BuildUpB";
            }
        }
        else if (r.Prop != null) { i.Z = r.Prop.Pos.Y; i.Rot = Campaign.Heading(r.Prop.Rot); i.Parked = !r.Prop.Visible; }
        else { i.Z = r.Data?["z"].Num() ?? 0; i.Rot = r.Data?["rot"].Num() ?? 0; i.Parked = !r.Placed; }
        return i;
    }
    // PLDE: "producer" = a building or unit with a unit action (conditions.js produces)
    bool Produces(Entity e)
    {
        try { return W.Data.ActionsOf(e.RulesOwner(), e.Name, e.Kind, e.Def?.Type ?? e.Cls).Any(a => a.Kind == "Build" && a.Type is "CHTR" or "ANML" or "VHCL" or "SHIP"); }
        catch (Exception) { return e is Building && !(e.Def != null && (e.Def.WallKind != null || e.Def.Gate)); }
    }
    public string? DataAttribute(int obj, string name) => R(obj)?.Attr(name);
    public int FindObject(string guidOrName) => Id(C.Objects.Get(guidOrName));
    public bool IsWorldRegion(string? rgn) => C.Regions.Get(rgn).World;
    public bool IsMissingClass(string cls, int owner)
    {
        if (owner != C.HumanSlot || Registry.ClassList(cls) is not { } l) return false;
        // (a class list: none of them; co-op: missing for every seat - a seat that has the role can make it)
        if (Co != null) return Co.Seats.All(s => Co.SwapOf(s.Slot) is { } sw && l.All(c => sw.Missing(c)));
        return C.Swap != null && l.All(c => C.Swap.Missing(c));
    }
    // CF:2983-3149: finished objects of `class` and `owner` in the region
    public int CountFinished(string cls, int owner, string? rgn)
    {
        var region = C.Regions.Get(rgn); var lc = cls.ToLowerInvariant();
        int n = 0;
        foreach (var r in C.Objects.All)
        {
            if (!r.Alive || r.Entity == null || !OwnerMatches(r.Slot, owner) || !C.Objects.ClassIs(r, lc)) continue;
            if (r.Entity is Building b && !b.Built) continue;         // CurTask != "BuildUpB"
            if (!C.Objects.InRegion(r, region)) continue;
            n++;
        }
        return n;
    }
    public IReadOnlyList<int> PlayerObjects(int slot)
    {
        var ps = Slots(slot).Select(P).Where(p => p != null).ToHashSet();
        if (ps.Count == 0) return Array.Empty<int>();
        var res = new List<int>();
        foreach (var u in W.Units) if (u.Alive && u.Owner != null && ps.Contains(u.Owner) && !u.Parked && u.Rec != null) res.Add(u.Rec.Id);
        foreach (var b in W.Buildings) if (b.Alive && b.Owner != null && ps.Contains(b.Owner) && b.Rec != null) res.Add(b.Rec.Id);
        return res;
    }
    public int CountCharacters(int slot, int level) { var p = P(Give(slot)); return p == null ? 0 : W.Units.Count(u => u.Alive && u.Owner == p && u.Cls == "CHTR" && u.Level == level); }
    // the unit limit of the owner for one more unit of a (1-based) level: CheckUnits(owner, level)
    public bool UnitSlotFree(int slot, int level)
    {
        var p = P(Give(slot));
        if (p == null || level < 1 || level > 5) return true;
        var pyr = p.Pyramid ?? W.Data.Pyramid;
        double max = level - 1 < pyr.Length ? pyr[level - 1] : STD_PYRAMID[level - 1];
        return p.AtLevel[level - 1] < max;
    }

    // ------------------------------------------------------------------ groups
    public int FindGroup(string? guidOrName) => Id(C.Objects.GroupOf(guidOrName)?.Rec);
    public IReadOnlyList<int> GroupMembers(int group) => C.Objects.ById(group)?.GroupObj?.Members.Where(m => m.Alive).Select(m => m.Id).ToList() ?? new List<int>();
    public int GroupOf(int obj) => Id(R(obj)?.Group?.Rec);
    public bool AddToGroup(int group, int obj) => C.Objects.AddToGroup(C.Objects.ById(group)?.GroupObj, R(obj));
    public bool RemoveFromGroup(int obj) => C.Objects.RemoveFromGroup(R(obj));

    // ------------------------------------------------------------------ creating, deleting, changing objects
    public int Spawn(string cls, int owner, double x, double y, SpawnOptions o)
    {
        // co-op: what the mission makes for "the player" goes to its seat (a story hero to the seat it is assigned to); a
        // scaled enemy's units come more often (CoopRuntime.SpawnCount) - not when an object is replaced (REPL)
        int n = 1;
        if (Co != null && !o.Replace) { owner = Co.GiftSlot(cls, owner); n = o.NoScale ? 1 : Co.SpawnCount(owner, cls); }
        var g = o.Group >= 0 ? C.Objects.ById(o.Group)?.GroupObj : null;
        int first = Id(C.Spawn(cls, owner, x, y, o.Level, o.Rot, null, g, o.Exact, true, o.Height));
        for (int i = 1; i < n && first >= 0; i++) C.Spawn(cls, owner, x + 1.5 * i, y + 1.5 * (i % 2), o.Level, o.Rot, null, g, false, true, o.Height);
        return first;
    }

    // a link point of a source object (a building's or a scenery prop's model) in the world, or null
    (double X, double Z)? LinkOf(Rec src, string name)
    {
        if (src.Entity is Building b && b.Alive) return b.LinkWorld(name) is V3 p ? (p.X, p.Z) : null;
        if (src.Entity != null) return null;
        var P = src.Prop;
        if (P == null || W.Models.Link(P.Model, name) is not V3 l) return null;
        double c = SMath.Cos(P.Rot), s = SMath.Sin(P.Rot);
        return (P.Pos.X + l.X * c + l.Z * s, P.Pos.Z - l.X * s + l.Z * c);
    }
    // where a unit comes out of a source without the link Spwn: on the line between the source and `pos`, as close to
    // the source as free ground reaches from `pos`
    (double X, double Z) ExitPoint(Rec src, (double X, double Z)? target)
    {
        var (sx, sz) = C.Objects.PosOf(src);
        var nav = W.Nav;
        if (target == null) return W.FreeSpot(sx, sz);
        double tx = target.Value.X, tz = target.Value.Z;
        if (!nav.IsFree(tx, tz)) (tx, tz) = W.FreeSpot(tx, tz);
        double dx = sx - tx, dz = sz - tz, L = Math.Sqrt(dx * dx + dz * dz);
        var best = (tx, tz);
        for (double d = 1; d <= L; d += 1)
        {
            double x = tx + dx / L * d, z = tz + dz / L * d;
            if (!nav.IsFree(x, z)) break;
            best = (x, z);
        }
        return best;
    }
    /// <summary>SPGR out of a building: one unit leaves it and walks on to the target (CTheLite.usl:84-186: from Spwn it
    /// walks to Ex_1 - else the source's position + its radius along the map's +y -, with the spawn effect, then on to
    /// the rally point; a worker looks for work there). A fighting object (a building, a decorative building, a
    /// vehicle) sends them out; from a scenery prop they stay at Spwn.</summary>
    public int SpawnOutOf(int source, string cls, int level, int owner, int group, MapPos? target)
    {
        if (Co != null)
        {
            owner = Co.GiftSlot(cls, owner);
            int n = Co.SpawnCount(owner, cls), first = SpawnOutOf1(source, cls, level, owner, group, target);
            for (int i = 1; i < n && first >= 0; i++) SpawnOutOf1(source, cls, level, owner, group, target);
            return first;
        }
        return SpawnOutOf1(source, cls, level, owner, group, target);
    }
    int SpawnOutOf1(int source, string cls, int level, int owner, int group, MapPos? target)
    {
        var src = C.Objects.ById(source);
        if (src == null) return -1;
        bool alive = src.Alive;
        (double X, double Z)? tgt = target is MapPos tp ? G(tp.X, tp.Y) : null;
        var cls0 = src.Entity != null ? src.Entity.Name : src.Cls;
        bool fighting = src.Entity is { Alive: true } || (cls0.Length > 0 && W.Data.Exists(C.RuleClass(cls0) ?? cls0) && W.Data.IsA(C.RuleClass(cls0) ?? cls0, "CFightingObj"));
        var sp = alive ? LinkOf(src, "Spwn") : null;
        var (x, z) = sp ?? (alive ? ExitPoint(src, tgt) : tgt ?? C.Objects.PosOf(src));
        var (mx, my) = C.ToMap(x, z);
        var rec = C.Spawn(cls, owner, mx, my, level, 0, null, group >= 0 ? C.Objects.ById(group)?.GroupObj : null, true);
        if (rec?.Entity is not Unit u) return Id(rec);
        if (u.Naval && src.Entity is Building sb && tgt != null) { var (sx, sz) = sb.SpawnPoint(tgt.Value.X, tgt.Value.Z, W.WaterNav); W.Teleport(u, sx, sz); }
        else if (sp != null && !u.Naval) { u.Pos.X = sp.Value.X; u.Pos.Z = sp.Value.Z; u.Pos.Y = W.GroundY(u); W.UHash.Move(u); }   // (exactly there, inside the building)
        if (sp == null || !fighting)
        {
            if (tgt != null && fighting) W.Order(u, new Order("move") { X = tgt.Value.X, Z = tgt.Value.Z, Auto = true });
            return rec.Id;
        }
        var ex = LinkOf(src, "Ex_1");
        if (ex == null)
        {
            var (sx, sz) = C.Objects.PosOf(src); var (smx, smy) = C.ToMap(sx, sz);
            double r = src.Entity?.Radius is double rr && rr != 0 ? rr : 4;
            ex = C.ToGame(smx, smy + r);
        }
        var then = tgt != null ? new Order("move") { X = tgt.Value.X, Z = tgt.Value.Z, Auto = true, Then = u.IsWorker ? new Order("rallyJob") { X = tgt.Value.X, Z = tgt.Value.Z } : null } : null;
        W.Order(u, new Order("move") { X = ex.Value.X, Z = ex.Value.Z, Auto = true, Then = then });
        u.Path = new NavPath(new[] { new Waypoint(ex.Value.X, ex.Value.Z) }); u.Goal = ex;    // (straight out: the pathfinder can't start inside the footprint)
        W.Emit("spawnfx", new Args { ["unit"] = u });
        return rec.Id;
    }
    /// <summary>"&lt;class&gt;§&lt;buildup&gt;": the unit's own tech tree gets that upgrade of its class (AF:4928-4938:
    /// ClearFilters, then /Filters/&lt;tribe&gt;/Upgrades/&lt;class&gt;/&lt;buildup&gt; - the wild boars' rage of missions 4
    /// and 14; the others name their class's default and change nothing)</summary>
    public void ApplyBuildup(int obj, string buildup)
    {
        var u = E(obj);
        if (u?.Owner == null || string.IsNullOrEmpty(buildup)) return;
        var tribe = u.Def?.Tribe is { Length: > 0 } t ? t : u.Owner.Tribe;
        var path = $"{tribe}/Upgrades/{u.Name}/{buildup}";
        if (W.Data.Base.Filter(path) != null) W.LocalFilter(u, path, true);
    }
    public bool Remove(int obj) => C.Remove(R(obj));
    // a normal death (AF:1474-1480); scenery is just removed
    public void Kill(int obj)
    {
        var r = R(obj);
        if (r == null) return;
        var u = E(obj);
        if (u != null) { if (u.Parked) W.SetParked(u, false); if (u is Unit uu) uu.InvulnT = 0; else if (u is Building bb) bb.InvulnT = 0; W.Kill(u, null); }
        else C.Remove(r);
    }
    // OCPY: what the player is given can be selected: mission 7 gives the player Taslow's two unselectable Tesla towers
    // early (Gd03) and makes them selectable only after sequence 2567 (Id03), so they stood there unusable for most of
    // the mission (the user's report). Hit-ability stays as the mission set it. chk_highlight: a ring for that long.
    public void SetOwner(int obj, int slot, double highlightSeconds)
    {
        var rec = R(obj);
        if (rec == null) return;
        // co-op: what is already a seat's stays its seat's when the triggers give it to "the player" again (the
        // tutorial re-occupies the player's workers to mark them; every seat is the player)
        if (Co != null && slot == C.HumanSlot && rec.Entity?.Owner is { } cur && Co.IsSeatSlot(cur.Id)) slot = cur.Id;
        else if (Co != null) slot = Co.GiftSlot(rec.Entity?.Name ?? rec.Cls, slot);
        C.SetOwner(rec, slot);
        var e = rec.Entity;
        bool mine = e != null && e.Alive && e.Owner != null && (e.Owner == C.Human || IsHumanSide(e.Owner.Id));
        if (mine && !rec.Selectable && rec.Visible) C.SetAppearance(rec, (rec.Flags ?? 7) | 4);
        if (highlightSeconds > 0 && mine) W.Highlights.Set(e!, W.Time + highlightSeconds);
    }
    public void SetAppearance(int obj, int flags)
    {
        var r = R(obj);
        // mortal objects stay hitable and selectable while visible (a trigger's flags cannot lock them again: Campaign.IsMortal)
        if (r != null && (flags & 1) != 0 && C.IsMortal(r)) flags |= 6;
        C.SetAppearance(r, flags);
    }
    public void SetHitpoints(int obj, double hp) { var e = E(obj); if (e != null) W.SetHp(e, hp); }
    public void SetLevel(int obj, int level) { var u = U(obj); if (u != null) W.SetLevel(u, level); }

    // ------------------------------------------------------------------ orders (ACDO, WYPT, TRSP)
    public void OrderMove(int obj, MapPos to, double speed, bool attackMove)
    {
        var u = U(obj);
        if (u == null) return;
        if (u.Inside) W.LeaveTransportOf(u);
        StopPatrol(u);
        var (x, z) = G(to.X, to.Y);
        if (attackMove) { W.Order(u, new Order("attackmove") { X = x, Z = z }); return; }
        // (the zeppelin flies to the point's height at the given speed in m/s: misc.usl:137-146; anybody else walks at
        // that gait: Action /Walk /Speed=n, FightingObj HandleWalkCommand - 0 = its own pace, the Lua API's move)
        var o = new Order("move") { X = x, Z = z, Slow = speed < 3, Gait = speed >= 1 ? (int)Math.Round(speed) : 0 };
        if (u.Zeppelin) { o.Alt = to.Z; o.FlySpeed = speed; }
        W.Order(u, o);
    }
    public void OrderAttack(int obj, int target)
    {
        var u = U(obj); var t = E(target);
        if (u == null || t == null) return;
        StopPatrol(u);
        W.Order(u, Order.Attack(t));
    }
    public void OrderStop(int obj) { var u = U(obj); if (u == null) return; StopPatrol(u); W.Order(u, new Order("stop")); }
    public void OrderBuild(int worker, int building) { var u = U(worker); var b = E(building) as Building; if (u != null && b != null) W.Order(u, new Order("build") { Target = b }); }
    public void OrderBoard(int unit, int transporter) { var u = U(unit); var t = E(transporter); if (u == null || t == null) return; StopPatrol(u); W.Order(u, new Order("board") { Target = t }); }
    public bool CanBoard(int unit, int transporter) { var u = U(unit); var t = E(transporter); return u != null && t != null && W.CanBoard(u, t); }
    public void UnloadAll(int transporter) { if (U(transporter) is Unit t && t.Passengers is { Count: > 0 }) W.UnloadAllOf(t, false); }
    public void EnterTransport(int unit, int transporter) { var u = U(unit); var t = U(transporter); if (u != null && t != null) W.EnterTransport(u, t); }
    public void Teleport(int obj, double x, double y) { var r = R(obj); if (r == null) return; if (r.Entity is Unit u) StopPatrol(u); C.Teleport(r, x, y); }
    // break the task and face the point (AF:1198-1206)
    public void FaceTo(int obj, double x, double y)
    {
        var u = U(obj);
        if (u == null) return;
        StopPatrol(u);
        W.Order(u, new Order("stop"));
        var (gx, gz) = G(x, y);
        u.Heading = SMath.Atan2(-(gx - u.Pos.X), -(gz - u.Pos.Z));
        u.LookChanged();
    }
    // "name | loops" (AF:1452-1472): gates of the scenery (open / close), poses
    public bool PlayAnim(int obj, string name, int loops)
    {
        var r = R(obj);
        if (r == null) return false;
        bool ok = C.PlayAnim(r, name, loops);
        if (!ok) r.AnimWanted = name;
        // an opened scenery gate lets units through, a closed one blocks again
        if (r.Prop != null && (name == "open" || name == "close")) r.Prop.SetBlocking(name == "close");
        return ok;
    }
    public double AnimLength(int obj, string name)
    {
        var r = R(obj);
        var A = r?.Entity?.Anim ?? r?.Prop?.Anim;
        return A != null && A.Has(name) ? A.Duration(name) : -1;
    }
    public void SetInvulnerable(int obj, bool on, double seconds)
    {
        var e = E(obj);
        if (e == null) return;
        // objects kept mortal (Campaign.IsMortal: MIRAGE 13's gate tower; co-op `mortal`), by name or alias
        if (on && e.Rec != null && C.IsMortal(e.Rec)) return;
        if (e.Rec != null) e.Rec.MapInvuln = false;
        W.SetInvulnerable(e, on, seconds);
    }
    public void SetDisarmed(int obj, bool on, double seconds) { var e = E(obj); if (e != null) W.SetDisarmed(e, on, seconds); }
    public void SetGate(int obj, int state)
    {
        var r = R(obj);
        if (r == null) return;
        if (r.Entity is Building b && b.Alive && b.Def?.Gate == true) { W.SetGate(b, state); return; }
        if (r.Prop != null) { C.PlayAnim(r, state == 0 ? "open" : "close", 1); r.Prop.SetBlocking(state != 0); }
    }
    public void SetRallyPoint(int obj, double x, double y) { var e = E(obj); if (e != null) e.Rally = G(x, y); }
    // the actor receives the item (AF:1390-1395; FightingObj.AddItem); a carrier of the item drops it first
    public void GiveItem(int unit, int item)
    {
        var u = U(unit); var prop = R(item)?.Prop;
        var it = prop != null ? W.ItemOfProp(prop) : null;
        if (u == null || it == null || u.Item != null) return;
        if (it.Carrier != null) W.DropItem(it.Carrier);
        W.TakeItem(u, it);
    }
    public bool Command(int obj, string command)
    {
        var e = E(obj);
        if (e == null) return false;
        switch (command)
        {
            // BuildDown: an Aje building is taken down (its own BuildDown action, queued as the player's click does)
            case "BuildDown":
            {
                if (e is not Building b) return false;
                var a = W.Data.ActionsOf(b.RulesOwner(), b.Name, b.Kind, b.Def?.Type).FirstOrDefault(x => x.Id == "BuildDown");
                if (a == null) return false;
                W.StartBuildDown(b, a);
                return true;
            }
            // entrench: dig in where it stands (Entrench.usl)
            case "entrench": if (e is Unit u) { W.DigIn(u); return true; } return false;
            // the Scorpion of mission 16 wakes up (Building.usl CScorpionBoss.HandleGamePlayCommand "/JumpOffWall")
            case "JumpOffWall": return e is Building jb && W.ScorpionActivate(jb);
            case "LevelUp": if (e is Unit lu && lu.Level < 5) return W.SetLevel(lu, lu.Level + 1); return false;
        }
        return false;
    }
    // PrepareUnboard "[x y z]": a transporter sails to the point and lets its passengers off there
    public void PrepareUnboard(int obj, MapPos at)
    {
        var u = U(obj);
        if (u == null) return;
        StopPatrol(u);
        var (x, z) = G(at.X, at.Y);
        W.Order(u, Order.Move(x, z));
        W.QueueOrder(u, new Order("unload"));
    }

    // mode 0: once (the end reports WAYR), 1: circle, 2: back and forth. The walk is an attack-move for fighters (a
    // patrol fights what it meets and then goes on: FightingObj m_bAggressionWalk), a plain move for everything else and
    // for a "straight walk" (FightingObj.usl:2414: it only switches the aggression walk off; the path is the same).
    public void StartPatrol(int obj, IReadOnlyList<MapPos> points, int mode, double speed, bool straight)
    {
        var u = U(obj);
        if (u == null || points.Count == 0) return;
        // co-op (`reroute`): an enemy wave's path that ends at a defended point may end at the definition's points instead
        // (mission 13 with three seats: half of the waves go for the second gate) - one decision per wave
        if (Co != null && Co.Def.Reroutes.Count > 0 && u.Owner != null && u.Owner.IsEnemy(Co.Seats[0].Player))
        {
            int key = (int)Math.Round(W.Time * 20) ^ ((int)points[0].X * 7919) ^ ((int)points[0].Y * 104729);
            var rr = Co.Reroute(points.Select(v => (v.X, v.Y)).ToList(), key);
            if (rr != null) points = rr.Select(q => new MapPos(q.X, q.Y, 0)).ToList();
        }
        var p = new Patrol { U = u, Pts = points.Select(v => { var (x, z) = G(v.X, v.Y); return (x, z, v.Z); }).ToList(), Mode = mode, Speed = speed, Straight = straight };
        Patrols.Set(u, p);
        PatrolGo(p);
    }
    void PatrolGo(Patrol p)
    {
        var u = p.U; var (x, z, mz) = p.Pts[p.I];
        bool fight = !p.Straight && !u.CannotFight && u.Stance != 3 && u.Stance != -1 && u.Owner != null;
        if (u.Inside) W.LeaveTransportOf(u);
        if (u.Zeppelin) { W.Order(u, new Order("move") { X = x, Z = z, Auto = true, Alt = mz, FlySpeed = p.Speed }); u.Task.Patrol = p; return; }
        W.Order(u, new Order(fight ? "attackmove" : "move") { X = x, Z = z, Auto = p.Speed < 3, Slow = p.Speed < 3, Gait = p.Speed >= 1 ? (int)Math.Round(p.Speed) : 0 });
        u.Task.Patrol = p;
    }
    void PatrolTick(double dt)
    {
        _patrolT -= dt;
        if (_patrolT > 0) return;
        _patrolT = 0.25;
        foreach (var (u, p) in Patrols.ToList())
        {
            var k = u.Task;
            if (!u.Alive || Patrols.Get(u) != p) { if (Patrols.Get(u) == p) Patrols.Delete(u); continue; }
            if (u.Parked || u.Inside) continue;
            var (x, z, _) = p.Pts[p.I];
            if (k.Type is "move" or "attackmove")
            {
                if (k.Patrol == p) continue;
                if (Math.Abs(k.X - x) < 0.6 && Math.Abs(k.Z - z) < 0.6) { k.Patrol = p; continue; }   // back on the way after a fight
                if (k.Back != null) continue;                                                       // walking back to its post
                Patrols.Delete(u); continue;                                                        // somebody else gave an order
            }
            if (k.Type == "attack") { if (k.User) Patrols.Delete(u); continue; }                 // fighting on the way
            if (k.Type is not ("idle" or "hold" or "roam")) { Patrols.Delete(u); continue; }
            double d = Math.Sqrt((u.Pos.X - x) * (u.Pos.X - x) + (u.Pos.Z - z) * (u.Pos.Z - z));
            if (d > 4 + u.Radius && p.Tries < 3) { p.Tries++; PatrolGo(p); continue; }           // interrupted or blocked: again
            p.Tries = 0;
            // the point is reached (or cannot be): next
            int n = p.Pts.Count;
            if (p.Mode == 0)
            {
                if (p.I >= n - 1) { Patrols.Delete(u); if (u.Rec != null) C.Engine?.OnWaypointReached(u.Rec.Id); continue; }
                p.I++;
            }
            else if (p.Mode == 1) p.I = (p.I + 1) % n;
            else if (n > 1) { if (p.I + p.Dir < 0 || p.I + p.Dir >= n) p.Dir = -p.Dir; p.I += p.Dir; }
            if (n == 1 && p.Mode != 0) continue;
            PatrolGo(p);
        }
    }

    // ------------------------------------------------------------------ players and the world
    public bool PlayerExists(int slot) => P(slot) != null;
    public int? Relation(int a, int b) { var A = P(a); var B = P(b); return A != null && B != null ? A.Relation(B) : null; }
    public void SetRelation(int a, int b, int relation)
    {
        foreach (var sa in Slots(a)) foreach (var sb in Slots(b))
        {
            var A = P(sa); var B = P(sb);
            if (A == null || B == null || A == B || (Co != null && Co.IsSeatSlot(sa) && Co.IsSeatSlot(sb))) continue;     // (the seats stay allies)
            W.SetDiplomacy(A, B, relation, false);
        }
    }
    // CF:1039-1159
    public double? PlayerAttribute(int slot, string name)
    {
        // co-op: the stocks and units of every seat together; the storage limits are the receiving seat's
        if (Co != null && slot == C.HumanSlot && !name.ToLowerInvariant().Contains("max") && !name.ToLowerInvariant().Contains("rescap"))
        {
            double? sum = null;
            foreach (var s in Slots(slot)) { var v = Attr(P(s), name); if (v != null) sum = (sum ?? 0) + v; }
            return sum;
        }
        return Attr(P(Give(slot)), name);
    }
    static double? Attr(Player? p, string name)
    {
        if (p == null) return null;
        return name.ToLowerInvariant() switch
        {
            "food" => Math.Floor(p["food"]), "wood" => Math.Floor(p["wood"]), "stone" => Math.Floor(p["stone"]),
            "iron" or "skulls" => Math.Floor(p["skulls"]),
            "units" => p.Units,
            "max_food" or "rescap_food" => p.Caps["food"], "max_wood" or "rescap_wood" => p.Caps["wood"], "max_stone" or "rescap_stone" => p.Caps["stone"],
            _ => null,
        };
    }
    public void ChangeResource(int slot, string name, string mod, bool cap) { var p = P(Give(slot)); if (p != null) W.SetResource(p, name, mod, cap); }
    // with another tribe chosen the player's filters are the chosen tribe's (tribes.js); nothing of that role: met
    public bool? HasTechFilter(int slot, string filter)
    {
        if (Co != null && slot == C.HumanSlot)
        {
            // co-op: true when any seat's tree has it (each in its own tribe)
            bool? any = null;
            foreach (var s in Slots(slot))
            {
                var q = P(s);
                if (q?.Tt == null) continue;
                var qf = Co.FilterFor(q, filter);
                any = (any ?? false) || qf == null || W.HasFilter(q, qf.TrimStart('/'));
            }
            return any;
        }
        var p = P(slot);
        if (p?.Tt == null) return null;
        var f = C.FilterFor(p, filter);
        return f == null || W.HasFilter(p, f.TrimStart('/'));
    }
    public void SetTechFilter(int slot, string path, bool on)
    {
        foreach (var s in Slots(slot))
        {
            var p = P(s);
            var f = p == null ? null : Co != null ? Co.FilterFor(p, path) : C.FilterFor(p, path);
            if (f != null) W.SetFilter(p!, f, on);
        }
    }
    // a TECH action's filters: every player's tree is rebuilt once at its end (a rebuild per filter took seconds on the
    // MIRAGE tree: mission 7 switches about 60 at the start)
    public void TechBatch(bool begin) { foreach (var p in W.Players) if (p.Tt != null) { if (begin) p.Tt.Suspend(); else p.Tt.Resume(); } }
    // co-op: one action's gifts are dealt round the seats (a hero phase, a no-build mission)
    public void GiftBatch(bool begin) => Co?.GiftBatch(begin);
    // (a debug option's unit limit or pyramid, game/cheats.js, stays)
    public void SetPopulationLimit(int slot, int limit)
    {
        foreach (var s in Slots(slot))
        {
            var p = P(s);
            if (p == null || p.Cheat?.PopMax != null) continue;
            p.PopMax = Math.Max(limit, p.Cheat?.PopFloor ?? 0);
            // a scaled enemy keeps its raised limit (co-op "AI production limits")
            if (p.CoopPopBase != null && Co != null) { p.CoopPopBase = p.PopMax; p.PopMax = Math.Ceiling(p.PopMax * Co.Scaling.Count); }
        }
    }
    public void SetUnitLimits(int slot, int[] perLevel) { foreach (var s in Slots(slot)) { var p = P(s); if (p != null && p.Cheat?.Pyramid == null) p.Pyramid = perLevel.Select(v => (double)v).ToArray(); } }
    public void SetResourceCaps(int slot, string food, string wood, string stone)
    {
        static double? N(string s) => string.IsNullOrWhiteSpace(s) ? null : Json.TryNum(s, out var d) ? d : double.NaN;
        foreach (var s in Slots(slot)) { var p = P(s); if (p != null) W.SetCaps(p, N(food), N(wood), N(stone)); }
    }
    public void SetNeutralToAnimals(int slot, int value) { foreach (var s in Slots(slot)) { var p = P(s); if (p != null) p.AnimalsNeutral = value & 3; } }
    public bool SetRegionEnabled(string rgnGuid, bool on, int subIndex) { var r = C.Regions.Find(rgnGuid); if (r == null) return false; r.SetEnabled(on, subIndex); return true; }
    public bool MoveRegion(string rgnGuid, double x, double y) { var r = C.Regions.Find(rgnGuid); if (r == null) return false; var (gx, gz) = G(x, y); r.MoveTo(gx, gz); return true; }
    public bool EnableDisabledShapes(string rgnGuid)
    {
        var r = C.Regions.Find(rgnGuid);
        if (r == null || r.World || !r.Shapes.Any(s => !s.Enabled)) return false;
        for (int i = 0; i < r.Shapes.Count; i++) if (!r.Shapes[i].Enabled) r.SetEnabled(true, i);
        return true;
    }
    public int RevealArea(int slot, double x, double y, double radius, double seconds, int follow)
    {
        var (gx, gz) = G(x, y);
        int id = -1;
        foreach (var s in Slots(slot))          // (co-op: every seat sees it)
        {
            var p = P(s);
            if (p == null) continue;
            _reveals.Add(W.RevealArea(p, gx, gz, radius, seconds, follow >= 0 ? E(follow) : null));
            if (id < 0) id = _reveals.Count - 1;
        }
        return id;
    }

    // ------------------------------------------------------------------ computer players (ai.md §13.1)
    // (a co-op seat has no mission brain: an AI commander takes the humans' commands, not the mission's)
    ICampaignBrain? Brain(int slot) => Co?.IsSeatSlot(slot) == true ? null : W.Brains.Get(slot);
    public bool AiBehavior(int slot, string behavior, string module) { var b = Brain(slot); if (b == null) return false; b.SetBehaviour(behavior, module); return true; }
    public bool AiAttack(int slot, AiAttackOrder o)
    {
        var b = Brain(slot);
        if (b == null) return false;
        var targets = o.Targets.Select(E).Where(e => e != null).Select(e => e!).ToList();
        if (targets.Count == 0) return true;
        (double, double)? Pos(MapPos? v) => v is MapPos m && !m.IsZero ? G(m.X, m.Y) : null;
        return b.StartAttack(new BrainAttack
        {
            Type = o.Type, Targets = targets, Position = Pos(o.Position), SpawnPosition = Pos(o.SpawnPosition), AttackOnTheWay = o.AttackOnTheWay, Spawn = o.Spawn,
            IgnoreLocations = o.IgnoreLocations, TargetOnly = o.TargetOnly, Ship = o.Ship, ShipLand = o.ShipLand, Behaviour = string.IsNullOrEmpty(o.Behaviour) ? null : o.Behaviour,
        });
    }
    public void AiAutoAttack(int slot, IReadOnlyList<int> targets)
    {
        var list = targets.Select(E).Where(e => e != null).Select(e => e!).ToList();
        if (list.Count > 0) Brain(slot)?.StartAutoAttack(list);
    }
    public void AiDefendArea(int slot, string id, MapPos pos, double radius, int maxUnits) { var (x, z) = G(pos.X, pos.Y); Brain(slot)?.SetDefenceArea(id, x, z, radius, maxUnits); }
    public void AiLockUnits(int slot, IReadOnlyList<int> units, bool locked) => Brain(slot)?.LockUnits(units.Select(U).Where(u => u != null).Select(u => u!).ToList(), locked);
    public void SetAggression(IReadOnlyList<int> units, int state) => W.SetAggro(units.Select(U).Where(u => u != null).Select(u => u!).ToList(), state);
    public void AiRegionMap(int slot, string map, string region, double value, bool add)
    {
        var r = C.Regions.Find(region);
        if (slot < 0) { foreach (var b in W.Brains.Values) b.SetRegionMap(map, r, value, add); }
        else Brain(slot)?.SetRegionMap(map, r, value, add);
    }
    public void AiModuleCommand(int slot, string module, string command) => Brain(slot)?.CallModule(module, command);
    public void AiGameOver() { foreach (var b in W.Brains.Values) b.GameOver(true); }

    // ------------------------------------------------------------------ presentation
    public void QuestChanged(QuestState quest, string change) => Presenter.QuestChanged(quest, change);
    public void SetQuestionMark(string guid, string state, string? tooltip) => C.SetQuestionMark(guid, state, tooltip);
    public void ShowMarker(MapMarker m) => Presenter.ShowMarker(m);
    public void RemoveMarker(string id) => Presenter.RemoveMarker(id);
    public void SetInfoBar(string text) => Presenter.SetInfoBar(text);
    public void PlaySound(string name, MapPos? at)
    {
        V3? pos = null;
        if (at is MapPos m) { var (x, z) = G(m.X, m.Y); pos = new V3(x, W.Height(x, z), z); }
        Presenter.PlaySound(name, pos);
    }
    public void PlayMusic(string file, bool loop) => Presenter.PlayMusic(file, loop);
    public void NextWeather() => W.NextWeatherTrack();
    // an effect of the particle database (EFCT; CActionEffectObj: its model plays `loops` times its length, then goes;
    // loops 0 = until the mission ends)
    public void PlayEffect(string path, double x, double y, int loops)
    {
        var key = (path ?? "").Trim('/').ToLowerInvariant();
        if (W.Data.Extra.Particles?[key] is not JsonArray fx || fx.Count == 0) { C.Warn($"effect {path} is not in the particle database"); return; }
        var (gx, gz) = G(x, y);
        double len = fx.Count > 1 ? fx[1].Num() : 0;
        Effects.Add(new MissionEffect { Gfx = fx[0].Str(), X = gx, Z = gz, Until = loops > 0 ? W.Time + len * loops : double.PositiveInfinity });
    }
    public bool IsBusy => Presenter.IsBusy;

    static JsonNode? FindByFile(JsonObject table, string path)
    {
        if (table[path] is JsonNode d) return d;
        var f = TrigUtil.FilePart(path);
        foreach (var (k, v) in table) if (TrigUtil.FilePart(k) == f) return v;
        return null;
    }
    // engine.js sequenceTick: the reveal of disable_fow, the actors bound now (sequence.js), the UI plays it. A sequence
    // the data does not have, or whose file is not in the installation, ends at once (§6.8)
    public bool PlaySequence(SequenceRequest r)
    {
        var seq = FindByFile(C.Data.Sequences, r.Path);
        var play = new SequencePlay { Request = r, Data = seq, SnapBack = r.SnapBack, Camera = r.Camera is MapPos cam && !cam.IsZero ? G(cam.X, cam.Y) : null };
        SeqPlay = play;
        // disable_fow: the area named by the action is shown for the sequence
        _seqReveal = null;
        if (r.Fow && r.FowPos is MapPos fp && !fp.IsZero && C.Human != null)
        {
            var (x, z) = G(fp.X, fp.Y);
            _seqReveal = W.RevealArea(C.Human, x, z, r.FowRadius > 0 ? r.FowRadius : 30, 0);
            // co-op: the other human seats see it too (their reveals end with the sequence's)
            if (Co != null) foreach (var s in Co.HumanSeats) if (s.Player != C.Human) _seqRevealsCoop.Add(W.RevealArea(s.Player, x, z, r.FowRadius > 0 ? r.FowRadius : 30, 0));
        }
        // its actors: bound now; at the end they stay where the scene leaves them
        if (seq?["play"] is JsonObject pj) try { play.Actors = new SeqActors(C, pj); } catch (Exception e) { L.Error($"sequence {r.Path}: actors", e); }
        if (seq == null || (seq["file"] is JsonValue fv && fv.TryGetValue(out bool fb) && !fb)) return false;
        return Presenter.PlaySequence(play);
    }
    public void SequenceFinished(SequenceRequest r)
    {
        var play = SeqPlay != null && SeqPlay.Request == r ? SeqPlay : new SequencePlay { Request = r, SnapBack = r.SnapBack, Camera = r.Camera is MapPos cam && !cam.IsZero ? G(cam.X, cam.Y) : null };
        SeqPlay = null;
        try { play.Actors?.Finish(r.SnapActors); } catch (Exception e) { L.Error($"sequence {r.Path}: actors", e); }
        if (_seqReveal != null) { W.HideArea(_seqReveal); _seqReveal = null; }
        foreach (var rv in _seqRevealsCoop) W.HideArea(rv);
        _seqRevealsCoop.Clear();
        // camera: back where it was (the UI's part, SnapBack) or to camera_data (Game.usl:1835-1853)
        Presenter.SequenceEnded(play);
    }
    // DGSC: the UI queues the scene; mentor scenes whose file was not exported get an empty scene
    public bool PlayDialog(string path, int token)
    {
        var scene = FindByFile(C.Data.Dialogs, path) ?? new JsonObject
        {
            ["id"] = Regex.Replace(TrigUtil.FilePart(path), @"\.dlg$", ""), ["path"] = path, ["actors"] = new JsonObject(), ["frames"] = new JsonArray(),
            ["mentor"] = Regex.IsMatch(path, @"/mentor/", RegexOptions.IgnoreCase),
        };
        return Presenter.PlayDialog(new DialogPlay { Token = token, Path = path, Scene = scene });
    }
    public void EndMission(bool won, string text)
    {
        if (Ended != null) return;
        Ended = (won, text);
        Presenter.EndMission(won, text, won ? 1.5 : 0);
    }
    public (double X, double Y, double Dist, double Yaw)? Camera => Presenter.Camera;
    public bool IsSelected(int obj) { var e = E(obj); return e != null && Presenter.IsSelected(e); }
    public IReadOnlyDictionary<string, string>? LoadProfile() => Presenter.LoadProfile();
    public void SaveProfile(IReadOnlyDictionary<string, string> values) => Presenter.SaveProfile(values);
}
