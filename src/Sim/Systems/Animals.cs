// Wild animals as the original's scripts run them (docs/spec/units.md section 7): nests and their areas, flocks,
// babies growing up, the idle loop (walk targets, sleep, hunger and feeding), hunters looking for prey, the reaction
// to an attack (fight, call for help, flee), the scare of a death, the threat when an enemy comes near.
// Also the autonomous animals of kennels and hatchlings (they roam and hunt around their home).
//
// Reference: Animal.usl (CAnimal), Nest.usl (CNest), Flock.usl, ScareMap.usl, task/Feed.usl, task/Sleep.usl,
// task/Fight.usl (CFlee, CAlarmed).
//   aggressive -1: peaceful, always flees       0: neutral, fights back and alerts its kind       1: hunter
//
// Times are game seconds (the original's virtual times: 144 virtual seconds a second, World.DayTime).
//   unit fields: WildNest (its nest), Flock, Born / Growup (a nest's young: born at, grows up in growup s; Grow =
//   the share of its strength 0.2 .. 1), Fatigue (fleeing), FedT (last meal), Food (cache) / FoodT, NextWalk,
//   AgilityT, AlarmT
//
// Ported from remake/src/game/systems/animals.js (pw-exporter 98c7703).
// C# notes: the model's size while it grows (growupScale: the "growup" pose) is the view's - it reads Unit.Babyness.
// The threat clip's onDone closure is the AnimDone kind "animalThreat".
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.World;

// ---------------------------------------------------------------- areas (the nests' regions: Nest.usl)
/// <summary>shapes in game coordinates; an oval is the ellipse inscribed in its box</summary>
public sealed class Area
{
    public readonly List<AreaBox> Shapes;
    public Area(IEnumerable<AreaBox> shapes) { Shapes = shapes.Where(s => s.X1 > s.X0 && s.Z1 > s.Z0).ToList(); }
    public bool Empty => Shapes.Count == 0;
    public bool Contains(double x, double z)
    {
        foreach (var s in Shapes)
        {
            if (x < s.X0 || x > s.X1 || z < s.Z0 || z > s.Z1) continue;
            if (!s.Oval) return true;
            double a = (s.X1 - s.X0) / 2, c = (s.Z1 - s.Z0) / 2;
            double u = (x - s.X0 - a) / a, v = (z - s.Z0 - c) / c;
            if (u * u + v * v <= 1) return true;
        }
        return false;
    }
    /// <summary>CRegion.GetRandomPointInside: a sub-shape (by size), a point in it</summary>
    public (double X, double Z)? RandomPoint(Func<double> rand)
    {
        if (Empty) return null;
        double tot = 0;
        foreach (var t in Shapes) tot += (t.X1 - t.X0) * (t.Z1 - t.Z0);
        double r = rand() * tot; var s = Shapes[0];
        foreach (var t in Shapes) { r -= (t.X1 - t.X0) * (t.Z1 - t.Z0); if (r <= 0) { s = t; break; } }
        for (int i = 0; i < 20; i++)
        {
            double x = s.X0 + rand() * (s.X1 - s.X0), z = s.Z0 + rand() * (s.Z1 - s.Z0);
            if (!s.Oval || Contains(x, z)) return (x, z);
        }
        return ((s.X0 + s.X1) / 2, (s.Z0 + s.Z1) / 2);
    }
    public double Radius() { double r = 0; foreach (var s in Shapes) r = Math.Max(r, Math.Max((s.X1 - s.X0) / 2, (s.Z1 - s.Z0) / 2)); return r; }
    public static Area Circle(double x, double z, double r) => new(new[] { new AreaBox { Oval = true, X0 = x - r, Z0 = z - r, X1 = x + r, Z1 = z + r } });
}

/// <summary>a flock (Flock.usl): its members, centre and size</summary>
public sealed class Flock
{
    public readonly OrderedSet<Unit> Members = new();
    public (double X, double Z) Pos;
    public double Size;
}

/// <summary>a nest of an original map that respawns wild animals (Nest.usl; animals.js addWildNest)</summary>
public sealed partial class WildNest
{
    public string Name = "", Species = "";
    public double X, Z, Max, Rate, Amount;
    public bool Swim, Grown;
    /// <summary>the hotspots' day times [sub area, from hour, to hour]</summary>
    public List<double[]> Table = new();
    public List<Unit> Members = new();
    /// <summary>the spawn timer; the members alive</summary>
    public double T;
    public int Alive;
    public (double X, double Z) LastRandom;
    public Area Action = null!, Safe = null!;
    public List<Area> Hotspots = new();
    public double Agility, Density;
    public (double X, double Z) FlockPos;
    public double FlockT, CheckT;
    public Flock? Flock;
}

/// <summary>a death's scare (ScareMap.usl)</summary>
public sealed class Scare
{
    public double X, Z, R, Str, T;
    public OrderedSet<Unit> Targets = new();
}

public sealed partial class Unit
{
    public WildNest? WildNest;
    public Flock? Flock;
    /// <summary>a nest's young: born at (game time; null: grown) and the class's growup duration</summary>
    public double? Born;
    public double Growup;
    /// <summary>1 - age / growup: the view blends the model's "growup" pose by it</summary>
    public double Babyness;
    public double GrowT;
    public double? AgilityT;
    public (double X, double Z)? NextWalk;
    /// <summary>what it eats around it (trees / carcasses) and when that was looked up</summary>
    public List<ResNode>? Food;
    public double FoodT;
    public double? FedT;
    /// <summary>a sea animal that can't go on land</summary>
    public bool SwimOnly;
}

public sealed partial class UnitTask
{
    /// <summary>roam: the idle tick (JS t.tick; null: not started), the walk's goal and gait (t.to, t.run)</summary>
    public double? Tick;
    public (double X, double Z)? WalkTo;
    public bool Run;
    /// <summary>roam / feed: the food, where to stand, goto | feed (t.state), bouts and loops left, time to the next loop</summary>
    public ResNode? Food;
    public (double X, double Z) At;
    public string? FeedState;
    public int Bouts;          // (Loops: Economy.cs)
    public double LoopT;
    public bool AfterFight;
    /// <summary>roam / threat: the clip started</summary>
    public bool Started;
    /// <summary>flee: from whom (t.from), legs run, where the last one began (t.last), slower after waking (t.slow)</summary>
    public Entity? FleeFrom;
    public int Legs;
    public (double X, double Z)? Last;
    public double FleeSlow;
}

public sealed partial class World
{
    /// <summary>wild animals alive at once on a map (performance guard; big community maps have 60+ nests)</summary>
    public const int WILD_CAP = 260;
    const double IDLE_TICK = 0.6;            // OnIdleTick: every 6th idle tick decides what to do
    const double SCARE_R = 60, SCARE_STR = 360, SCARE_DECAY = 0.1;     // Animal.OnKill: AddScareSource(pos, 60, 360); ScareMap: 0.1 a second
    const double FLEE_WALK = 15, FLEE_DIST = 40, FLEE_LEGS = 8;         // CFlee

    /// <summary>the scares of deaths (ScareMap)</summary>
    public List<Scare>? Scares;
    double _nestT;

    double Growup(string name) { var d = Data.Def(name, null); return d != null ? d.Growup : 0; }
    static bool IsWildish(Unit u) => u.Owner == null || u.Autonomous;
    double R01() => Rng.Next();

    // ---------------------------------------------------------------- nests (Nest.usl, docs/spec/units.md 7.4)
    /// <summary>a nest of the map (members: animals already there, pre-spawned by advance_time: {u, born})</summary>
    public WildNest AddWildNest(MapNest n, IEnumerable<(Unit U, double? Born)>? members = null)
    {
        var def = Data.Def(n.Species, null);
        var nest = new WildNest
        {
            Name = n.Name, Species = n.Species, X = n.X, Z = n.Z, Max = n.Max, Rate = n.Rate, Amount = n.Amount, Swim = n.Swim, Grown = n.Grown,
            Table = n.Table, T = 0, Alive = 0, LastRandom = (n.X, n.Z),
            Action = n.ActionArea != null && n.ActionArea.Count > 0 ? new Area(n.ActionArea) : Area.Circle(n.X, n.Z, 20),
            Safe = n.SafeArea != null && n.SafeArea.Count > 0 ? new Area(n.SafeArea) : Area.Circle(n.X, n.Z, 10),
            Hotspots = n.HotspotArea.Select(s => new Area(new[] { s })).ToList(),
            Agility = def != null && def.Agility > 0 ? def.Agility : 30, Density = def?.Density ?? 0,
            FlockPos = (n.X, n.Z), FlockT = 0, CheckT = 0, Flock = null,
        };
        // MIRAGE 2.6.8 CSwimmingNest.OnInit: aggressive water animals without a limit - three broods in all, the animals
        // born before the start among them (MirageMaster268.cs SwimmingNestEggs)
        if (n.Swim && n.Amount < 0 && SwimmingNestEggs(Data, n.Species, n.Max, -1) is var eggs and >= 0) nest.Amount = Math.Max(0, eggs - n.Start.Count);
        if (members != null) foreach (var (u, born) in members) if (u != null) JoinNest(nest, u, born);
        // CNest.SpawnAnimal "Flocking": nests whose safe area holds another nest share that one's flock
        foreach (var o in WildNests) if (o.Flock != null && nest.Safe.Contains(o.X, o.Z)) { nest.Flock = o.Flock; break; }
        nest.Flock ??= new Flock { Pos = (n.X, n.Z), Size = 0 };
        foreach (var u in nest.Members) FlockAdd(nest.Flock, u);
        nest.Alive = nest.Members.Count;
        WildNests.Add(nest);
        UpdateFlockPos(nest);
        return nest;
    }

    public void JoinNest(WildNest nest, Unit u, double? born)
    {
        u.WildNest = nest;
        u.HomeX = nest.X; u.HomeZ = nest.Z;
        nest.Members.Add(u);
        if (born != null) Birth(u, born.Value);
    }

    /// <summary>Nest.SpawnAnimal: one animal at the nest; a baby unless the nest spawns them grown (born 2 days ago)</summary>
    public Unit? SpawnFromNest(WildNest nest)
    {
        var u = SpawnUnit(nest.Species, null, nest.X, nest.Z, null, R01() * 6.28, nest.Swim ? new UnitOpts { Swim = true } : new UnitOpts());
        if (u == null) return null;
        JoinNest(nest, u, nest.Grown ? Time - 2 * 600 : Time);
        if (nest.Flock != null) FlockAdd(nest.Flock, u);
        if (nest.Amount > 0) nest.Amount--;
        return u;
    }

    partial void WildNestsUpdate(double dt)
    {
        _nestT -= dt;
        if (_nestT > 0) return;
        double step = 1 - _nestT; _nestT = 1;                  // once a second
        int wild = 0; foreach (var u in Units) if (u.Alive && u.Wild) wild++;
        foreach (var n in WildNests)
        {
            int before = n.Members.Count;
            n.Members = n.Members.Where(u => u.Alive && u.WildNest == n).ToList();
            if (n.Members.Count < before) n.T = 0;                   // a member died: the spawn timer starts again
            n.Alive = n.Members.Count;
            if (n.Alive < n.Max && n.Amount != 0 && wild < WILD_CAP)
            {
                n.T += step;
                if (n.T >= n.Rate) { n.T = 0; if (SpawnFromNest(n) != null) n.Alive++; }
            }
            // TIMER_UPDATEPOS: the flock's centre moves every `agility` s; TIMER_CHECK_AREA: every 2 s, members outside the
            // action area walk back
            if ((n.FlockT -= step) <= 0) { n.FlockT = n.Agility; UpdateFlockPos(n); }
            if ((n.CheckT -= step) <= 0) { n.CheckT = 2; CheckNestArea(n); }
        }
        // the flocks' centres (Flock.Update: every second)
        var seen = new HashSet<Flock>();
        foreach (var n in WildNests) if (n.Flock != null && seen.Add(n.Flock)) UpdateFlock(n.Flock);
    }

    public void FlockAdd(Flock f, Unit u) { u.Flock = f; f.Members.Add(u); FlockSize(f); }
    // Flock.UpdateSize: the sum of the members' sizes; UpdatePos: their centre
    static void FlockSize(Flock f) { double s = 0; foreach (var u in f.Members) if (u.Alive) s += 2 * Math.Sqrt(2) * u.Radius; f.Size = s; }
    void UpdateFlock(Flock f)
    {
        double x = 0, z = 0; int n = 0;
        foreach (var u in f.Members.ToList()) { if (!u.Alive) { f.Members.Delete(u); continue; } x += u.Pos.X; z += u.Pos.Z; n++; }
        if (n > 0) f.Pos = (x / n, z / n);
        FlockSize(f);
    }

    /// <summary>Nest.GetRandomPoint: babies anywhere in the safe area; else a point of the action area (not too near the last
    /// one), moved along its direction from the flock centre (up to the nest's flock size) - or, ignoring the flock,
    /// just the point</summary>
    public (double X, double Z)? NestPoint(WildNest n, bool baby, bool ignoreFlock = false)
    {
        if (baby) return n.Safe.RandomPoint(R01);
        (double X, double Z)? p = null;
        double v = Math.Min(10, n.Action.Radius() / 4);
        for (int i = 0; i < 100; i++)
        {
            var q = n.Action.RandomPoint(R01);
            if (q == null) break;
            p = q;
            if (Math.Abs(q.Value.X - n.LastRandom.X) + Math.Abs(q.Value.X - n.LastRandom.Z) > v) break;     // (sic: the x twice)
        }
        if (p == null) return null;
        n.LastRandom = p.Value;
        if (ignoreFlock) return p;
        double size = 2 * Math.Max(n.Density, 5) * Math.Sqrt(n.Members.Count / Math.PI);
        double dx = p.Value.X - n.FlockPos.X, dz = p.Value.Z - n.FlockPos.Z;
        double len = Hypot(dx, dz);
        if (len == 0) return p;
        dx /= len; dz /= len;
        if (len > size) len = R01() * size;
        return (p.Value.X + dx * len, p.Value.Z + dz * len);
    }

    /// <summary>Nest.AcceptablePos</summary>
    bool NestAccepts(WildNest n, double x, double z, bool baby, bool ignoreFlock = false)
    {
        if (baby) return n.Safe.Contains(x, z);
        if (!n.Action.Contains(x, z)) return false;
        if (ignoreFlock || n.Flock == null) return true;
        return Hypot(x - n.FlockPos.X, z - n.FlockPos.Z) <= n.Flock.Size;
    }

    /// <summary>Nest.UpdateFlockPos: a hotspot of the time of day (60 %), else by day anywhere in the action area, at night in
    /// the safe area; then every member gets a next walk target</summary>
    public void UpdateFlockPos(WildNest n)
    {
        (double X, double Z)? p = null;
        if (n.Hotspots.Count > 0 && R01() < 0.6)
        {
            double h = Math.Floor(DayTime() / 3600) % 24;
            var e = n.Table.FirstOrDefault(r => r[1] > r[2] ? h >= r[1] || h < r[2] : h >= r[1] && h < r[2]);
            if (e != null && e[0] >= 0 && e[0] < n.Hotspots.Count && e[0] == Math.Floor(e[0])) p = n.Hotspots[(int)e[0]].RandomPoint(R01);
        }
        p ??= n.Members.Count == 0 || !SleepTime(n.Members[0]) ? n.Action.RandomPoint(R01) : n.Safe.RandomPoint(R01);
        n.FlockPos = p ?? (n.X, n.Z);
        foreach (var u in n.Members) if (u.Alive) u.NextWalk = NestPoint(n, IsBaby(u));
    }

    void CheckNestArea(WildNest n)
    {
        foreach (var u in n.Members)
        {
            if (!u.Alive || n.Action.Contains(u.Pos.X, u.Pos.Z) || u.Task.Type == "flee") continue;
            var t = u.Task;
            if (t.Type == "roam" && t.Phase == "walk" && t.WalkTo is { } to && (n.Action.Contains(to.X, to.Z) || n.Safe.Contains(to.X, to.Z))) continue;
            if (t.Type != "roam") continue;                         // (a fight leaves when its target goes: combat.js)
            var p = NestPoint(n, IsBaby(u));
            if (p != null) AnimalWalk(u, p.Value.X, p.Value.Z);
        }
    }

    // ---------------------------------------------------------------- growing up (Animal.Birth, CalculateHitpointsFromGrowUpSize)
    // a nest's young: born at `born`; until it has grown up (the class's growup_duration) its hit points, damage and
    // skulls are a share of the grown animal's (strength = age / growup, at least 20 % of the hit points and damage) and
    // its model is smaller (the client blends the model's "growup" pose by 1 - age / growup)
    public void Birth(Unit u, double born)
    {
        double g = Growup(u.Name);
        if (!(g > 0)) return;
        u.Born = born; u.Growup = g;
        GrowthUpdate(u);
    }
    public bool IsBaby(Unit u) => u.Born != null && Time - u.Born.Value < u.Growup;
    public void GrowthUpdate(Unit u)
    {
        if (u.Born == null) return;
        double age = Time - u.Born.Value, k = age / u.Growup;
        double grow = k >= 1 ? 1 : Math.Max(0.2, k);
        if (grow != u.Grow)
        {
            double ratio = u.MaxHp > 0 ? u.Hp / u.MaxHp : 1;
            u.Grow = grow;
            u.MaxHp = u.Stats!.Hp * grow;
            u.Hp = Math.Max(1, Math.Min(u.MaxHp, ratio * u.MaxHp));
        }
        u.Babyness = Math.Max(0, 1 - k);
        if (k >= 1) { u.Born = null; u.Babyness = 0; }
        u.LookChanged();
    }
    /// <summary>its skulls (CalculateScalpsFromGrowUpSize: by the strength without the 20 % floor, at least 1)</summary>
    public double ScalpValue(Entity e)
    {
        double v = e.Stats?.Scalps ?? 0;
        if (e is not Unit u || u.Born == null || !(u.Growup > 0)) return v;
        double k = (Time - u.Born.Value) / u.Growup;
        return k >= 1 ? v : Math.Max(Math.Truncate(v * k + 0.5), 1);
    }

    // ---------------------------------------------------------------- the idle loop (Animal.OnIdleTick)
    bool SleepTime(Unit u) => IsNight() != (u.Def != null && u.Def.NightActive);
    bool Hungry(Unit u) => Time - (u.FedT ?? -1e9) >= (u.Def?.Repletion ?? 0);

    partial void RoamUpdate(Unit u, double dt)
    {
        var t = u.Task;
        if (u.Tracker) { TrackerUpdate(u, dt); return; }
        if (u.Born != null && (u.GrowT -= dt) <= 0) { u.GrowT = Math.Max(1, u.Growup / 10); GrowthUpdate(u); }
        if (PreCheckEnemies(u, dt)) return;
        // the agility timer (an animal without a nest): every `agility` s the next walk target
        if (u.WildNest == null && (u.AgilityT = (u.AgilityT ?? R01() * 5) - dt) <= 0)
        {
            u.AgilityT = u.Def != null && u.Def.Agility > 0 ? u.Def.Agility : 15;
            CalcNextWalk(u);
        }
        switch (t.Phase)
        {
            case "walk": WalkUpdate(u, dt); return;
            case "sleep": SleepUpdate(u, dt); return;
            case "feed": FeedUpdate(u, dt); return;
            case "threat": ThreatUpdate(u, dt); return;
            case "wait":
                u.Vel = V3.Zero;
                WildIdle(u);         // its standing clip, or an idle animation of IdleAnims.txt / the T-rex's (WildIdle.cs)
                if ((t.Wait -= dt) > 0) return;
                t.Phase = "idle"; t.Tick = 0;
                return;
        }
        // idle: what to do next
        u.Vel = V3.Zero;
        WildIdle(u);
        if ((t.Tick = (t.Tick ?? R01() * IDLE_TICK) - dt) > 0) return;
        t.Tick = IDLE_TICK;
        if (StartNextWalk(u)) return;
        // in its sleeping time (the night, by day for a night-active one) four of ten idle ticks put it to sleep
        double r = R01();
        if (SleepTime(u) && r > 0.6 && u.Anim!.Has("rest")) { StartSleep(u); return; }
        if (Hungry(u) && r > 0.8 && FindFood(u)) return;
        t.Phase = "wait"; t.Wait = 5 + R01() * 5;
    }

    /// <summary>CalcNextWalkTarget: a point of its nest (by the flock), else one up to 30 m away (5 m for a baby) where it may be</summary>
    void CalcNextWalk(Unit u)
    {
        bool baby = IsBaby(u); var n = u.WildNest;
        for (int i = 0; i < 30; i++)
        {
            var p = n != null ? NestPoint(n, baby) : null;
            if (p == null) { double d = baby ? 5 : 30; double px = u.Pos.X + (R01() * 2 - 1) * d; p = (px, u.Pos.Z + (R01() * 2 - 1) * d); }
            if (PositionOk(u, p.Value.X, p.Value.Z)) { u.NextWalk = p; return; }
        }
    }

    bool PositionOk(Unit u, double x, double z)
    {
        if (u.WildNest != null) return NestAccepts(u.WildNest, x, z, IsBaby(u));
        if (u.Flock != null) return Hypot(x - u.Flock.Pos.X, z - u.Flock.Pos.Z) <= 2 * u.Flock.Size;
        if (u.HomeRadius != 0) return Hypot(x - u.HomeX, z - u.HomeZ) <= u.HomeRadius;      // (kennels, hatchlings)
        return true;
    }

    /// <summary>StartNewWalkTarget: to the next walk target - away from a scare if there is one (then the nest's limits do not count)</summary>
    bool StartNextWalk(Unit u)
    {
        if (u.NextWalk == null) return false;
        var (x, z) = u.NextWalk.Value;
        u.NextWalk = null;
        var un = Unscare(u, x, z, 1);
        if (un != null) (x, z) = un.Value;
        AnimalWalk(u, x, z);
        return true;
    }

    public void AnimalWalk(Unit u, double x, double z, bool run = false)
    {
        var nv = NavFor(u);
        if (!nv.IsFree(x, z)) { int c = nv.NearestFree(nv.Idx(x, z)); if (c >= 0) (x, z) = nv.Center(c); }
        u.Task = new UnitTask("roam") { Phase = "walk", WalkTo = (x, z), Run = run };
        u.SetPath(x, z);
    }

    void WalkUpdate(Unit u, double dt)
    {
        var t = u.Task;
        double sp = u.Steer(dt, t.Run ? u.RunSpeed : u.Speed);
        u.MoveAnim(sp, t.Run);
        if (u.Path.Count == 0) { t.Phase = "idle"; t.Tick = 0; }
    }

    void StartSleep(Unit u)
    {
        var t = u.Task; var n = u.WildNest;
        t.Phase = "sleep";
        t.Loops = 20 + (int)Math.Floor(R01() * 40);
        t.Wait = 0;
        if (n != null)
        {
            var p = NestPoint(n, IsBaby(u));
            if (p != null && NavFor(u).IsFree(p.Value.X, p.Value.Z)) u.SetPath(p.Value.X, p.Value.Z);
        }
    }

    void SleepUpdate(Unit u, double dt)
    {
        var t = u.Task;
        if (u.Path.Count > 0) { u.MoveAnim(u.Steer(dt, u.Speed), false); return; }
        u.Vel = V3.Zero;
        // Sleep.usl:72 AnimAction("rest", 20 + Random % 40, intro, outro): lies down, its loop part that many times, gets
        // up (the whole clip was counted as a loop)
        if (t.Wait == 0) { double d = u.BusyAnim ? 0 : u.Anim!.PlayLoops("rest", t.Loops); t.Wait = d > 0 ? d : t.Loops * 2; }
        t.Wait -= dt;
        if (t.Wait <= 0) { t.Phase = "idle"; t.Tick = 0; }
    }

    // ---------------------------------------------------------------- food (Animal.FindFood / UpdateFoodCache, task/Feed.usl)
    bool Vegetarian(Unit u) => !(u.Def != null && u.Def.Aggressive == 1);
    // what it eats around it: trees (and plants) for a vegetarian, carcasses for a hunter; in its nest's action area (a
    // baby: the safe area) or within 50 m. Every 60 s (hunters 30 s).
    void UpdateFoodCache(Unit u)
    {
        bool veg = Vegetarian(u), baby = IsBaby(u); var n = u.WildNest;
        var area = n != null ? (baby ? n.Safe : n.Action) : null;
        double R = area != null ? Math.Max(20, area.Radius()) + 5 : 50, cx = area != null ? n!.X : u.Pos.X, cz = area != null ? n!.Z : u.Pos.Z;
        var res = new List<ResNode>();
        RHash.Query(cx, cz, R, r =>
        {
            if (!r.Alive) return;
            if (veg ? r.Type != "tree" : r.Type != "corpse") return;
            if (area != null ? !area.Contains(r.Pos.X, r.Pos.Z) : Hypot(r.Pos.X - u.Pos.X, r.Pos.Z - u.Pos.Z) > 50) return;
            res.Add(r);
        });
        u.Food = res; u.FoodT = Time;
    }

    bool FindFood(Unit u)
    {
        if (u.Food == null || Time - u.FoodT >= (Vegetarian(u) ? 60 : 30)) UpdateFoodCache(u);
        ResNode? food = null;
        for (int i = 0; i < 5 && u.Food!.Count > 0; i++)
        {
            int k = (int)Math.Floor(R01() * u.Food.Count); var f = u.Food[k];
            if (!f.Alive) { u.Food.RemoveAt(k); continue; }
            if (PositionOk(u, f.Pos.X, f.Pos.Z) && !ScaredAt(u, f.Pos.X, f.Pos.Z)) { food = f; break; }
        }
        if (food != null) { Feed(u, food); return true; }
        if (Vegetarian(u)) { Feed(u, null); return true; }     // grazing where it stands
        return false;
    }

    // CFeed: walk to the food (a tree: 2 m short of it); too far from it then: wait 5 s; else 2 .. 6 bouts of 3 .. 10
    // "feeding" loops, each taking the animal's damage off a carcass and healing half of it
    void Feed(Unit u, ResNode? food, bool afterFight = false)
    {
        double x = u.Pos.X, z = u.Pos.Z;
        if (food != null)
        {
            x = food.Pos.X; z = food.Pos.Z;
            if (food.Type == "tree") { double d = Hypot(u.Pos.X - x, u.Pos.Z - z); if (d == 0) d = 1; x += (u.Pos.X - x) / d * 2; z += (u.Pos.Z - z) / d * 2; }
        }
        u.Task = new UnitTask("roam") { Phase = "feed", Food = food, At = (x, z), FeedState = "goto" };
        u.FedT = Time;
        if (Hypot(x - u.Pos.X, z - u.Pos.Z) > 1) u.SetPath(x, z); else u.Path.Clear();
        if (afterFight) u.Task.AfterFight = true;
    }

    void FeedUpdate(Unit u, double dt)
    {
        var t = u.Task;
        if (t.Food != null && !t.Food.Alive) { t.Phase = "idle"; t.Tick = 0; return; }
        if (t.FeedState == "goto")
        {
            if (u.Path.Count > 0) { u.MoveAnim(u.Steer(dt, u.Speed), false); return; }
            u.Vel = V3.Zero;
            if (Hypot(t.At.X - u.Pos.X, t.At.Z - u.Pos.Z) > 5) { t.Phase = "wait"; t.Wait = 5; return; }
            if (t.Food != null) u.Face(t.Food.Pos.X, t.Food.Pos.Z, 1, 100);
            t.FeedState = "feed"; t.Bouts = 2 + (int)Math.Floor(R01() * 5); t.Loops = 3 + (int)Math.Floor(R01() * 8); t.LoopT = 0; t.LoopsDone = 0;
            return;
        }
        u.Vel = V3.Zero;
        if (t.Loops <= 0)
        {
            if (t.Bouts <= 0) { t.Phase = "idle"; t.Tick = 0; return; }
            t.Bouts--; t.Loops = 3 + (int)Math.Floor(R01() * 8); t.LoopT = 0.5; t.LoopsDone = 0;
            if (!u.BusyAnim) u.Anim!.Play(u.IdleAnim);
            return;
        }
        if ((t.LoopT -= dt) > 0) return;
        // Feed.usl:148 AnimAction("feeding", 1, first loop of the bout, last loop of the bout): a bout is the clip's start
        // part, its loop part once a loop, its end part (it was restarted whole for every loop)
        bool first = t.LoopsDone == 0, last = t.Loops == 1;
        double len = u.Anim!.PlayLoops("feeding", 1, first, last);
        if (len <= 0) len = 1.5;
        t.LoopT = len; t.Loops--; t.LoopsDone++;
        // the food loses what the animal takes (a carcass: Food.Mine), the animal heals half of it
        double got = AnimalDmg(u);
        if (t.Food != null && t.Food.Type == "corpse")
        {
            got = Math.Min(got, t.Food.Amount);
            t.Food.Amount -= got; t.Food.RotT = 120;
            if (t.Food.Amount <= 0.5) DepleteNode(t.Food);
        }
        SetHp(u, Math.Min(u.MaxHp, u.Hp + got / 2));
    }

    /// <summary>the damage of its weapon (Animal.GetDmg: by its strength)</summary>
    double AnimalDmg(Unit u) { var cs = u.Weapons.Long != null ? u.Cs(u.Weapons.Long) : null; return (cs != null ? cs.Dmg : 0) * (u.Grow ?? 1); }

    /// <summary>after it killed a wild animal a wild animal feeds on the carcass (Fight.usl "Feed", Animal.HandleEvent:
    /// within 20 m) - combat.js asks it</summary>
    public bool FeedOnKill(Unit u, Entity? prey)
    {
        if (!u.Alive || u.Owner != null || prey == null || prey.Owner != null) return false;
        var c = (prey as Unit)?.CorpseNode;
        if (c == null || !c.Alive || Hypot(c.Pos.X - u.Pos.X, c.Pos.Z - u.Pos.Z) > 20) return false;
        Feed(u, c, afterFight: true);
        return true;
    }

    // ---------------------------------------------------------------- the scare of a death (ScareMap.usl)
    /// <summary>any animal that dies frightens the wild animals that are not hunters within 60 m: they run away (to twice the
    /// radius) and avoid the place while its strength (360, losing 0.1 a second) lasts (combat.js kill calls it)</summary>
    public void AddScare(double x, double z)
    {
        var targets = new OrderedSet<Unit>();
        UHash.Query(x, z, SCARE_R, o =>
        {
            if (o.Alive && o.Owner == null && o.Cls == "ANML" && !(o.Def != null && o.Def.Aggressive == 1) && Hypot(o.Pos.X - x, o.Pos.Z - z) <= SCARE_R) targets.Add(o);
        });
        if (targets.Count == 0) return;
        var s = new Scare { X = x, Z = z, R = SCARE_R, Str = SCARE_STR, T = Time, Targets = targets };
        (Scares ??= new List<Scare>()).Add(s);
        foreach (var o in targets) GetScared(o);
    }

    Scare? ScareOf(Unit u, double x, double z)
    {
        Scare? best = null; double bs = 0;
        if (Scares == null) return null;
        Scares = Scares.Where(s => s.Str - SCARE_DECAY * (Time - s.T) > 0).ToList();
        foreach (var s in Scares)
        {
            if (!s.Targets.Has(u)) continue;
            double d2 = (x - s.X) * (x - s.X) + (z - s.Z) * (z - s.Z);
            if (d2 > s.R * s.R) continue;
            double str = (s.Str - SCARE_DECAY * (Time - s.T)) * (1 - d2 / (s.R * s.R));
            if (str > bs) { bs = str; best = s; }
        }
        return best;
    }
    bool ScaredAt(Unit u, double x, double z) => !(u.Def != null && u.Def.Aggressive == 1) && ScareOf(u, x, z) != null;

    /// <summary>UnscarePos: a scared point moves out of the scare (with a nest: to a point of the nest outside it)</summary>
    (double X, double Z)? Unscare(Unit u, double x, double z, double factor)
    {
        if (u.Def != null && u.Def.Aggressive == 1) return null;
        var s = ScareOf(u, x, z);
        if (s == null) return null;
        double r = s.R * factor, d = Hypot(x - s.X, z - s.Z); if (d == 0) d = 1;
        var p = (s.X + (x - s.X) / d * r, s.Z + (z - s.Z) / d * r);
        var n = u.WildNest;
        if (n == null || NestAccepts(n, p.Item1, p.Item2, IsBaby(u), true)) return p;
        for (int i = 0; i < 30; i++) { var q = NestPoint(n, IsBaby(u), true); if (q != null && ScareOf(u, q.Value.X, q.Value.Z) == null) return q; }
        return p;
    }

    void GetScared(Unit u)
    {
        if (u.Task.Type != "roam") return;
        var p = Unscare(u, u.Pos.X, u.Pos.Z, 2) ?? (u.Pos.X, u.Pos.Z);
        AnimalWalk(u, p.X, p.Z, run: true);
    }

    // ---------------------------------------------------------------- hunters (Animal.PreCheckForEnemies, FillEnemyList)
    // A wild hunter (aggressive 1, grown up) looks around: the players' units and buildings - and other wild animals
    // while it knows no carcass - within its alarm range + 50 m (not of its own class or flock, not Darwin's protected,
    // with a nest only those inside the nest's action area). It attacks the nearest within max(attack range, 30 m); one
    // only within its alarm range alarms it (a threat). A wild prey only when its kind can take it: needed helpers =
    // floor(time to kill it / time to be killed), not more than 2 x maxhelpers, and twice that many of its kind near it.
    // Not while it feeds.
    bool PreCheckEnemies(Unit u, double dt)
    {
        if ((u.ScanT = (u.ScanT != 0 ? u.ScanT : R01() * 2) - dt) > 0) return false;
        u.ScanT = 1.5 + R01();
        var t = u.Task;
        if (t.Phase == "feed" || t.Phase == "sleep") return false;
        bool hunter = IsWildish(u) && u.Owner == null ? (u.Def != null && u.Def.Aggressive == 1) : true;     // (kennel animals hunt)
        if (!hunter || IsBaby(u)) return false;
        var n = u.WildNest;
        if (u.Food == null || Time - u.FoodT >= 30) UpdateFoodCache(u);
        bool noFood = u.Food!.Count == 0;
        int avail = HelpersNear(u);
        bool Ok(Entity e)
        {
            if (e.Kind != "unit" && e.Kind != "building") return false;
            if (e.Name == u.Name && e.Owner == u.Owner) return false;
            if (u.Flock != null && e is Unit eu && u.Flock.Members.Has(eu)) return false;
            if (Data.Kind(e.Name) == "darwin_s0" || e.St.Aura.NoAnimalAggro) return false;
            if (n != null && !n.Action.Contains(e.Pos.X, e.Pos.Z)) return false;
            if (n != null && e.St.Camo.Count > 0) return false;
            if (e.Owner == null)
            {
                if (!noFood || e.Cls != "ANML") return false;
                int need = HelperCount(u, e); double max = u.Def?.MaxHelpers ?? 0;
                if (2 * max < need || avail < 2 * need) return false;
            }
            if (u.HomeRadius != 0 && Hypot(e.Pos.X - u.HomeX, e.Pos.Z - u.HomeZ) > u.HomeRadius) return false;
            return true;
        }
        double direct = Math.Max(AttackRangeOf(u, u.Cs(u.Weapons.Long)), 30);
        var e = FindTarget(u, direct, new FindOpts { Animals = true, Filter = Ok });
        if (e != null) { u.Task = new UnitTask("attack") { Target = e }; u.RepathT = 0; return true; }
        var a = FindTarget(u, AlarmRange(u), new FindOpts { Animals = true, Filter = Ok, AllZones = true });
        if (a != null) Alarm(u, a);
        return false;
    }

    /// <summary>Animal.GetHelperCount: floor(own time to kill the enemy / the enemy's time to kill it) (the scripts multiply
    /// damage by the weapon duration)</summary>
    int HelperCount(Unit u, Entity e)
    {
        var ecs = e.Weapons.Long != null ? e.Cs(e.Weapons.Long) : null;
        double fd = ecs != null ? ecs.Dmg * ((e as Unit)?.Grow ?? 1) : 0;
        if (fd == 0) return 0;
        double foreign = Math.Max(u.Hp, 1) / (fd * Math.Max(ecs!.Dur, 0.001));
        double od = AnimalDmg(u);
        if (od == 0) return 1000;
        var ocs = u.Cs(u.Weapons.Long)!;
        if (e.Hp == 0) return 0;
        double own = e.Hp / (od * Math.Max(ocs.Dur, 0.001));
        return (int)Math.Floor(own / foreign);
    }

    // its kind within its sight (2 x the largest of alarm range, attack range, FOW)
    double SightRange(Unit u) => 2 * Math.Max(Math.Max(AlarmRange(u), AttackRangeOf(u, u.Cs(u.Weapons.Long))), u.Fow);
    int HelpersNear(Unit u, Entity? call = null)
    {
        double R = SightRange(u);
        int n = 0;
        UHash.Query(u.Pos.X, u.Pos.Z, R, o =>
        {
            if (o == u || !o.Alive || o.Name != u.Name || Hypot(o.Pos.X - u.Pos.X, o.Pos.Z - u.Pos.Z) > R) return;
            n++;
            if (call != null && o.Task.Type != "attack" && o.Task.Type != "flee") { o.Task = new UnitTask("attack") { Target = call }; o.RepathT = 0; }
        });
        return n;
    }

    // SetAlarmed / CAlarmed: an enemy near but not in reach - the animal turns to it and threatens ("menace", not in
    // the water) - again at most every 10 s
    void Alarm(Unit u, Entity e)
    {
        if (Time < u.AlarmT) return;
        u.AlarmT = Time + 10;
        var t = u.Task;
        if (t.Type != "roam" || (t.Phase != null && t.Phase != "idle" && t.Phase != "wait" && t.Phase != "walk")) return;
        if (!u.Anim!.Has("menace") || u.Swimming) return;
        u.Task = new UnitTask("roam") { Phase = "threat", Target = e, T = 0 };
        u.Path.Clear();
    }

    void ThreatUpdate(Unit u, double dt)
    {
        var t = u.Task;
        u.Vel = V3.Zero;
        if (!t.Started)
        {
            t.Started = true;
            if (t.Target != null && t.Target.Alive) u.Face(t.Target.Pos.X, t.Target.Pos.Z, 1, 100);
            u.BusyAnim = true;
            u.Anim!.Play("menace", new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone("animalThreat", new Args { ["t"] = t }) });
            double d = u.Anim.Duration("menace");
            t.Until = Time + (d != 0 ? d : 2) + 0.2;
        }
        if (Time > t.Until) { u.BusyAnim = false; t.Phase = "idle"; t.Tick = 0; }
    }

    [AnimDone("animalThreat")]
    static void ThreatDone(World W, Entity e, Args? a)
    {
        var u = (Unit)e; var t = a?.Get<UnitTask>("t");
        u.BusyAnim = false;
        if (t != null && u.Task == t) { t.Phase = "idle"; t.Tick = 0; }
    }

    // ---------------------------------------------------------------- attacked (Animal.OnDefend, Nest.AnimalAttacked)
    /// <summary>A peaceful animal flees. A neutral or hunting one fights and calls its kind within sight - unless the enemy is a
    /// wild animal it cannot take with the help it has (hunters: half the threshold), the enemy stands outside its
    /// nest's action area, or it is a water melee animal and the enemy on land: then it flees. A baby attacked: every
    /// grown member of its nest that sees it attacks. (combat.js onHurt calls it for wild animals)</summary>
    public void AnimalHurt(Unit u, Entity att)
    {
        var n = u.WildNest;
        if (n != null && IsBaby(u)) foreach (var o in n.Members)
        {
            if (o == u || !o.Alive || IsBaby(o) || o.Task.Type == "attack") continue;
            if (Hypot(o.Pos.X - u.Pos.X, o.Pos.Z - u.Pos.Z) <= (o.Fow != 0 ? o.Fow : 32)) { o.Task = new UnitTask("attack") { Target = att }; o.RepathT = 0; }
        }
        if (u.Owner != null) return;
        if (u.Task.Type == "attack") return;
        double agg = u.Def?.Aggressive ?? 0;
        bool flee = agg < 0;
        if (!flee)
        {
            double max = u.Def?.MaxHelpers ?? 0; int need = HelperCount(u, att); bool world = att.Owner == null;
            int k = agg == 1 ? 2 : 1;
            if (world && max * k < need) flee = true;
            else if (world && HelpersNear(u) < need * k) flee = true;
            else HelpersNear(u, att);
            if (!flee && n != null && !n.Action.Contains(att.Pos.X, att.Pos.Z)) flee = true;
            if (!flee && u.SwimOnly && AttackRangeOf(u, u.Cs(u.Weapons.Long)) < 1 && !(att is Unit au && au.Naval) && Height(att.Pos.X, att.Pos.Z) > (WaterLevel ?? -1e9) - 0.5) flee = true;
        }
        if (flee) { Flee(u, att); return; }
        u.Task = new UnitTask("attack") { Target = att }; u.RepathT = 0;
    }

    // CFlee: up to 8 legs of 15 m away from the enemy (or to points of its nest); over when the enemy can't reach it
    // and is more than 40 m away. Fatigue grows with the way run and is lost again at 0.02 a second; tired out it stops.
    // Hurt it runs slower (below 90 / 80 / 60 % of its hit points one, two, three gaits less), below 60 % hobbling.
    public void Flee(Unit u, Entity from)
    {
        if (u.Task.Type == "flee") return;
        if (u.Fatigue >= 1) { u.Task = new UnitTask("roam") { Phase = "wait", Wait = 1 }; return; }
        u.Task = new UnitTask("flee") { FleeFrom = from, Legs = 0, Last = null, FleeSlow = u.Task.Phase == "sleep" ? 1 : 0 };
    }

    partial void FleeUpdate(Unit u, double dt)
    {
        var t = u.Task; var e = t.FleeFrom;
        if (e == null || !e.Alive) { u.Task = new UnitTask("roam"); return; }
        if (u.Path.Count > 0)
        {
            double k = u.Hp / u.MaxHp; int gaitDown = k < 0.6 ? 3 : k < 0.8 ? 2 : k < 0.9 ? 1 : 0;
            double ms = u.Stats!.MaxSpeed != 0 ? u.Stats.MaxSpeed : u.Stats.Speed != 0 ? u.Stats.Speed : 2;
            int g = (int)Math.Max(1, ms - gaitDown);
            double sp = u.GaitSpeed(g);
            double s = u.Steer(dt, sp * (1 - 0.5 * t.FleeSlow));
            string hump = "hump_" + Math.Min(3, g);
            if (k < 0.6 && u.Anim!.Has(hump)) u.Anim.Play(hump, new AnimOpts { Ts = 1, Cut = true }); else u.MoveAnim(s, true);
            return;
        }
        // a leg is over
        double d = Hypot(u.Pos.X - e.Pos.X, u.Pos.Z - e.Pos.Z);
        if (t.Legs > FLEE_LEGS || (d > FLEE_DIST && Reach(e, u) > AttackRangeOfAny(e, e.Cs(e.Weapons.Long)))) { u.Task = new UnitTask("roam"); return; }
        double walked = -1;
        if (t.Legs > 0) walked = Hypot(u.Pos.X - t.Last!.Value.X, u.Pos.Z - t.Last.Value.Z);
        t.Last = (u.Pos.X, u.Pos.Z);
        if (t.Legs > 0 && walked > 0)
        {
            double factor = 2 / Math.Max(u.Stats!.MaxSpeed != 0 ? u.Stats.MaxSpeed : 1, 1);
            u.Fatigue = Math.Min(1, u.Fatigue + walked / (factor * (FLEE_WALK + FLEE_DIST)));
            if (u.Fatigue >= 1) { u.Task = new UnitTask("roam"); return; }
        }
        var p = u.WildNest != null ? NestPoint(u.WildNest, IsBaby(u)) : null;
        if (p == null)
        {
            if (t.Legs > 0 && walked < 0.1) { u.Task = new UnitTask("roam") { Phase = "wait", Wait = 1 }; return; }
            double a = SMath.Atan2(u.Pos.Z - e.Pos.Z, u.Pos.X - e.Pos.X);
            if (t.Legs > 0 && walked < FLEE_WALK * 0.5) a += (R01() - 0.5) * Math.PI;
            double j = FLEE_WALK / 4;
            double px = u.Pos.X + SMath.Cos(a) * FLEE_WALK + (R01() * 2 - 1) * j;
            p = (px, u.Pos.Z + SMath.Sin(a) * FLEE_WALK + (R01() * 2 - 1) * j);
        }
        t.FleeSlow = Math.Max(0, t.FleeSlow - 0.25);
        t.Legs++;
        u.SetPath(p.Value.X, p.Value.Z);
        if (u.Path.Count == 0) u.Task = new UnitTask("roam") { Phase = "wait", Wait = 1 };
    }

    /// <summary>combat.js attackRangeOf for any attacker (a tower too): the units' is combat's</summary>
    double AttackRangeOfAny(Entity e, Rules.WeaponStats? cs)
    {
        if (e is Unit u) return AttackRangeOf(u, cs);
        if (cs == null) return 0;
        if (e.St.Active("storm", Time)) return 2;
        return cs.Range + (cs.W.Projectile != null ? 0 : e.Radius) + RangeBonus(e);
    }

    // the fatigue recovers 0.02 a second (TIMER_FATIGUE); called for the animals that have some
    partial void FatigueUpdate(Unit u, double dt) { if (u.Fatigue > 0 && u.Task.Type != "flee") u.Fatigue = Math.Max(0, u.Fatigue - 0.02 * dt); }

    // ---------------------------------------------------------------- the tracker dino (CTrackerDino)
    // CheckForNearbyEnemies every 2 s (50 m), otherwise AutoScout - random points ahead of its heading
    void TrackerUpdate(Unit u, double dt)
    {
        var t = u.Task;
        u.ScanT -= dt;
        if (u.ScanT <= 0)
        {
            u.ScanT = 2;
            var e = FindTarget(u, 50, new FindOpts { Animals = true, Filter = x => x.Kind != "res" });
            if (e != null) { u.Task = new UnitTask("attack") { Target = e }; u.RepathT = 0; return; }
        }
        if (u.Path.Count == 0 || t.Phase != "walk")
        {
            for (int i = 0; i < 8; i++)
            {
                double a = u.Heading + (R01() - 0.5) * (i < 4 ? 1.2 : 6.28), r = 25 + R01() * 20;
                double x = u.Pos.X - SMath.Sin(a) * r, z = u.Pos.Z - SMath.Cos(a) * r;
                if (Math.Abs(x) < Nav.Half - 4 && Math.Abs(z) < Nav.Half - 4 && NavFor(u).IsFree(x, z)) { u.SetPath(x, z); t.Phase = "walk"; break; }
            }
            if (u.Path.Count == 0) { u.Vel = V3.Zero; u.Anim!.Play(u.IdleAnim); return; }
        }
        double sp = u.Steer(dt, u.Speed);
        u.MoveAnim(sp, false);
        if (u.Path.Count == 0) t.Phase = "wait";
    }
}
