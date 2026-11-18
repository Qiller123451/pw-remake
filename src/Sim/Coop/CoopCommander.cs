// An AI commander (docs/COOP.md "AI commanders"): a co-op seat played by the computer. Its base, economy, army
// production and defence are the remake's computer player (TribeAI, src/Sim/Ai - one brain like a mission's, with no
// mission brain of its own and no hero); on top of it this layer carries out the humans' general commands, StarCraft II
// co-op style:
//
//   attack   a point (or a target object): the army attack-moves there; "reached" when half of it stands within 20 m;
//            then it holds the point
//   defend   a point (radius 25 m): the army stands there, fights what comes within 45 m, walks back
//   follow   a unit or group (escort): the army stays within 12 m of the first living one; all dead -> no command
//   economy  the army stays home and defends; no attacks, nothing joined (the brain builds and gathers)
//   retreat  the army walks (does not fight) back to its base; there the command ends
//   none     defends its base and expands (the brain) and joins the humans' attacks: when 3 or more of the human seats'
//            fighters fight 60 m or more from their bases, its idle army attack-moves to them
//            - in a hero phase (no base yet): the phase's objective instead (the definition's `phase.attack` region:
//            mission 8's barbarian camp) - its fighters attack the enemies in it until the phase ends; without one (or
//            defensive) they escort the humans' heroes (not idle at seat 1's start while the story goes on)
// and the aggression: 0 defensive (no attacks of its own, joins nothing), 1 normal (joins the humans' attacks), 2
// aggressive (also the brain's own attacks).
//
// The commands come as CoopCommand (lockstep, saved with the game); the layer thinks once a second of game time,
// in the step (deterministic: the world's lists in their order).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Coop;

public sealed class CoopCommander
{
    static readonly Logger L = Log.For("Coop.Commander");
    public const double REACHED_RADIUS = 20, DEFEND_RADIUS = 25, DEFEND_CHASE = 45, FOLLOW_RADIUS = 12, JOIN_FAR = 60, THINK = 1.0;
    public static readonly string[] Orders = { "none", "attack", "defend", "follow", "economy", "retreat" };

    readonly CoopRuntime R;
    public readonly CoopSeatRun Seat;
    public readonly TribeAI Brain;
    GameWorld W => R.W;
    Player P => Seat.Player;

    /// <summary>the command it carries out: none | attack | defend | follow | economy | retreat</summary>
    public string Order { get; private set; } = "none";
    /// <summary>attack / defend: the point (game x, z)</summary>
    public Pt? Point { get; private set; }
    /// <summary>attack: the target object (null: a point); follow: the units to escort</summary>
    public Entity? Target { get; private set; }
    public List<Entity> Follow { get; private set; } = new();
    /// <summary>0 defensive, 1 normal, 2 aggressive</summary>
    public int Aggression { get; private set; }
    /// <summary>attack: the army got there</summary>
    public bool Reached { get; private set; }
    /// <summary>none: the humans' attack it joined (where), or null</summary>
    public Pt? Joined { get; private set; }
    /// <summary>who gave the current command (seat number) and when (game time)</summary>
    public int By { get; private set; }
    public double Since { get; private set; }
    double _next;
    readonly OrderedSet<Unit> _locked = new();

    public CoopCommander(CoopRuntime r, CoopSeatRun seat, AiData data)
    {
        R = r; Seat = seat;
        var W = r.W;
        var A = W.Ai;
        if (A == null)
        {
            var c = r.C;
            A = new AiPlayers(W, data) { Human = c.Human?.Id ?? 0 };
            A.MapToGame = m => { var (x, z) = c.ToGame(m.X, m.Y); return new Pt(x, z); };
            A.ResolveRegion = name => c.Regions.Find(name) is { } rg ? new AiRegionFn(rg.Contains) : null;
            A.Attach();
        }
        // a balanced economy (Giraffe) at a fair level (4: no gifts of the harder levels); its own attacks by the aggression
        Brain = new TribeAI(W, seat.Player, data, new TribeAIOptions
        {
            Difficulty = 4, Behaviour = "Giraffe", MapOptions = AiMapOptions.From(r.C.Map["ai_options"]), Multimap = false, LevelName = null, ExtraSquads = false,
        });
        Brain.CoopAlly = true;
        A.Add(Brain);
        SetAggression(seat.Start.Aggression);
    }

    /// <summary>the seat's base was handed out (the hero phase's end, CoopRuntime.HandOver) at (x, z): the brain's start
    /// and home there (its census had taken the hero-phase units' place - seat 1's landing - as its start; the Dustriders'
    /// collector, their home being the start, drove back there and built its base far from the seat's site), no command
    /// carried over from the hero phase, nothing joined</summary>
    public void Rebase(double x, double z)
    {
        Brain.Rebase(new Pt(x, z));
        Release();
        Set("none", 0);
        _next = 0;
        ApplyBrainSettings();
        L.Info($"seat {Seat.Seat}: base handed out at {x:0},{z:0}");
    }

    // ------------------------------------------------------------------ commands
    /// <summary>a command of a human seat (CoopCommand); null = done, else why not</summary>
    public string? Command(int by, string kind, Pt? point, Entity? target, IReadOnlyList<Entity>? units, int value)
    {
        switch (kind)
        {
            case "aggression": SetAggression(value); return null;
            case "attack":
                if (point == null && target is not { Alive: true }) return "target";
                Set("attack", by); Point = point ?? Pt.Of(target!); Target = target is { Alive: true } ? target : null; Reached = false;
                break;
            case "defend":
                if (point == null && target == null) return "target";
                Set("defend", by); Point = point ?? Pt.Of(target!);
                break;
            case "follow":
                var f = (units ?? Array.Empty<Entity>()).Where(e => e.Alive).ToList();
                if (f.Count == 0 && target is { Alive: true }) f.Add(target);
                if (f.Count == 0) return "target";
                Set("follow", by); Follow = f;
                break;
            case "economy": Set("economy", by); Release(); break;
            case "retreat": Set("retreat", by); break;
            case "none": case "clear": Set("none", by); Release(); break;
            default: return "unknown";
        }
        L.Info($"seat {Seat.Seat}: {Order} by seat {by}{(Point != null ? $" at {Point.Value.X:0},{Point.Value.Z:0}" : "")}{(Follow.Count > 0 && Order == "follow" ? $" following {Follow.Count}" : "")}");
        _next = 0;
        ApplyBrainSettings();
        return null;
    }
    void Set(string order, int by) { Order = order; By = by; Since = W.Time; Point = null; Target = null; Follow = new List<Entity>(); Reached = false; Joined = null; }
    void SetAggression(int v) { Aggression = Math.Clamp(v, 0, 2); ApplyBrainSettings(); }
    /// <summary>the brain's own attacks only when aggressive and not on an economy order</summary>
    void ApplyBrainSettings() { Brain.Params.NoFight = !(Aggression >= 2 && Order is "none"); }

    // ------------------------------------------------------------------ the army it commands
    /// <summary>its fighters (not workers, ships, heroes, carts, towers on legs ...), alive and in the world</summary>
    public List<Unit> Army()
    {
        var res = new List<Unit>();
        foreach (var u in W.Units)
            if (u.Alive && u.Owner == P && !u.Parked && !u.Inside && !u.IsWorker && !u.Naval && u.Cls != "SHIP" && !u.CannotFight && !u.Stationary && !u.IsDropoff && u.Def?.Unique != true && !u.Autonomous && !u.Illusion)
                res.Add(u);
        return res;
    }
    void Take(List<Unit> army)
    {
        var fresh = army.Where(u => !_locked.Has(u)).ToList();
        if (fresh.Count == 0) return;
        Brain.LockUnits(fresh, true);           // (the brain's attacks and guards leave them alone)
        foreach (var u in fresh) _locked.Add(u);
    }
    void Release()
    {
        var l = _locked.Where(u => u.Alive).ToList();
        if (l.Count > 0) Brain.LockUnits(l, false);
        _locked.Clear();
    }
    Pt Home() => Brain.Census().Home ?? (R.C.StartLocations[P.Id] is { } sl ? new Pt(sl.X, sl.Z) : Pt.Of(P is var _ && W.Units.FirstOrDefault(u => u.Owner == P) is { } u ? u : (Entity)W.Units[0]));
    static double D(Entity e, Pt p) => p.Dist(e);
    void Go(IEnumerable<Unit> us, Pt to, bool fight)
    {
        var l = us.ToList();
        if (l.Count == 0) return;
        W.Order(l, new Order(fight ? "attackmove" : "move") { X = to.X, Z = to.Z, Auto = true });
    }
    Entity? NearestEnemy(Pt p, double r)
    {
        Entity? best = null; double bd = r;
        foreach (var u in W.Units) if (u.Alive && !u.Parked && u.Owner != null && P.IsEnemy(u.Owner) && !u.Untargetable) { double d = D(u, p); if (d < bd) { bd = d; best = u; } }
        if (best != null) return best;
        foreach (var b in W.Buildings) if (b.Alive && b.Owner != null && P.IsEnemy(b.Owner) && !b.Untargetable) { double d = D(b, p); if (d < bd) { bd = d; best = b; } }
        return best;
    }
    static bool Busy(Unit u) => u.Task.Type is "attack";

    // ------------------------------------------------------------------ thinking (CoopRuntime.Tick, every step)
    public void Tick(double dt)
    {
        if (W.Time < _next) return;
        _next = W.Time + THINK;
        try { Think(); }
        catch (Exception e) { if (Log.FirstTime("coopcmd|" + e.Message)) L.Error($"seat {Seat.Seat}: commander failed", e); }
    }

    void Think()
    {
        Brain.HeroPhase = R.HeroPhase;
        var army = Army();
        switch (Order)
        {
            case "attack":
            {
                Take(army);
                if (Target != null && Target.Alive) Point = Pt.Of(Target);
                else if (Target != null) Target = null;          // (the target is gone: on to where it was)
                var pt = Point!.Value;
                int near = army.Count(u => D(u, pt) <= REACHED_RADIUS);
                if (!Reached && army.Count > 0 && near * 2 >= army.Count) { Reached = true; L.Info($"seat {Seat.Seat}: attack point reached ({near}/{army.Count})"); }
                foreach (var u in army)
                {
                    if (Busy(u)) continue;
                    if (Target != null && D(u, pt) < 40) { if (u.Task.Type != "attack") W.Order(u, World.Order.Attack(Target)); continue; }
                    if (D(u, pt) > REACHED_RADIUS * 0.6 && !(u.Task.Type == "attackmove" && Math.Abs(u.Task.X - pt.X) < 1 && Math.Abs(u.Task.Z - pt.Z) < 1)) Go(new[] { u }, pt, true);
                }
                break;
            }
            case "defend":
            {
                Take(army);
                var pt = Point!.Value;
                var foe = NearestEnemy(pt, DEFEND_CHASE);
                foreach (var u in army)
                {
                    if (foe != null) { if (!Busy(u)) W.Order(u, World.Order.Attack(foe)); continue; }
                    if (D(u, pt) > DEFEND_RADIUS && !(u.Task.Type is "attackmove" or "move" && Math.Abs(u.Task.X - pt.X) < 1 && Math.Abs(u.Task.Z - pt.Z) < 1)) Go(new[] { u }, pt, true);
                }
                break;
            }
            case "follow":
            {
                Follow.RemoveAll(e => !e.Alive);
                var lead = Follow.FirstOrDefault();
                if (lead == null) { Command(By, "none", null, null, null, 0); return; }
                Take(army);
                var lp = Pt.Of(lead);
                foreach (var u in army)
                {
                    if (Busy(u) && D(u, lp) < FOLLOW_RADIUS * 3) continue;
                    if (D(u, lp) > FOLLOW_RADIUS) Go(new[] { u }, lp, Aggression > 0);
                }
                break;
            }
            case "retreat":
            {
                Take(army);
                var home = Home();
                var far = army.Where(u => D(u, home) > 25).ToList();
                if (far.Count == 0) { Command(By, "none", null, null, null, 0); return; }
                foreach (var u in far) if (!(u.Task.Type == "move" && Math.Abs(u.Task.X - home.X) < 1 && Math.Abs(u.Task.Z - home.Z) < 1)) Go(new[] { u }, home, false);
                break;
            }
            case "economy": break;               // (the brain alone: it builds, gathers and defends; NoFight)
            default:
                if (R.HeroPhase) PhaseThink(army);
                else JoinThink(army);
                break;
        }
    }

    /// <summary>the region a hero phase's AI fighters clear (the definition's `phase.attack`), null: none / not found</summary>
    Campaign.Region? PhaseRegion()
    {
        var n = R.Def.Phase.Attack;
        if (n == null) return null;
        var rg = R.C.Regions.Find(n);
        if (rg == null && Log.FirstTime("coopphase|" + n)) L.Warn($"phase.attack: no region '{n}' in the mission");
        return rg;
    }
    /// <summary>no command in a hero phase (no base yet): attack the phase's objective - the nearest enemy object in its
    /// region, else its centre - while it holds any (normal / aggressive); else escort the humans' heroes</summary>
    void PhaseThink(List<Unit> army)
    {
        if (army.Count == 0) return;
        Take(army);
        var rg = Aggression > 0 ? PhaseRegion() : null;
        if (rg != null && rg.Center() is { } c0)
        {
            var mid = new Pt(army.Average(u => u.Pos.X), army.Average(u => u.Pos.Z));
            Entity? foe = null; double bd = double.MaxValue;
            foreach (var e in W.Units.Cast<Entity>().Concat(W.Buildings))
            {
                if (!e.Alive || e.Owner == null || !P.IsEnemy(e.Owner) || e.Untargetable || e.Parked || e.Inside) continue;
                if (!rg.Contains(e.Pos.X, e.Pos.Z)) continue;
                double d = D(e, mid);
                if (d < bd) { bd = d; foe = e; }
            }
            if (foe != null)
            {
                if (Target != foe) { Target = foe; L.Info($"seat {Seat.Seat}: hero phase - attacks {foe.Name} in '{rg.Name}' with {army.Count}"); }
                var fp = Pt.Of(foe);
                foreach (var u in army)
                {
                    if (Busy(u)) continue;
                    if (D(u, fp) < 40) W.Order(u, World.Order.Attack(foe));
                    else if (!(u.Task.Type == "attackmove" && Math.Abs(u.Task.X - fp.X) < 1 && Math.Abs(u.Task.Z - fp.Z) < 1)) Go(new[] { u }, fp, true);
                }
                return;
            }
            Target = null;          // (cleared: the phase ends with the next step - meanwhile they escort)
        }
        // the escort: the first living hero of a human seat (seat 1's first), else a human seat's fighter
        Unit? lead = null;
        foreach (var s in R.HumanSeats)
        {
            lead = W.Units.FirstOrDefault(u => u.Alive && u.Owner == s.Player && !u.Inside && u.Def?.Unique == true)
                ?? W.Units.FirstOrDefault(u => u.Alive && u.Owner == s.Player && !u.Inside && !u.IsWorker && !u.CannotFight);
            if (lead != null) break;
        }
        if (lead == null) return;
        var lp = Pt.Of(lead);
        foreach (var u in army)
        {
            if (Busy(u) && D(u, lp) < FOLLOW_RADIUS * 3) continue;
            if (D(u, lp) > FOLLOW_RADIUS && !(u.Task.Type == "attackmove" && Math.Abs(u.Task.X - lp.X) < 3 && Math.Abs(u.Task.Z - lp.Z) < 3)) Go(new[] { u }, lp, true);
        }
    }

    /// <summary>no command: help an attacked ally, join the humans' attack (normal / aggressive)</summary>
    void JoinThink(List<Unit> army)
    {
        if (Aggression == 0) { if (Joined != null) { Joined = null; Brain.SendHome(army); } return; }
        if (W.Time >= _helpNext) { _helpNext = W.Time + HELP_THINK; HelpAllies(); }
        // the humans' fighters that fight far from their bases
        var pts = new List<Pt>();
        foreach (var s in R.HumanSeats)
        {
            var sl = R.C.StartLocations[s.Slot];
            Pt? baseP = sl != null ? new Pt(sl.X, sl.Z) : null;
            foreach (var u in W.Units)
                if (u.Alive && u.Owner == s.Player && !u.IsWorker && !u.Inside && u.Task.Type is "attack" && (baseP == null || D(u, baseP.Value) >= JOIN_FAR))
                    pts.Add(Pt.Of(u));
        }
        if (pts.Count >= 3)
        {
            var c = new Pt(pts.Average(p => p.X), pts.Average(p => p.Z));
            var free = army.Where(u => !Brain.Claims.Has(u) && !Busy(u)).ToList();
            if (Joined == null || Joined.Value.Dist(c) > 30)
            {
                Joined = c;
                L.Info($"seat {Seat.Seat}: joins the humans' attack at {c.X:0},{c.Z:0} with {free.Count}");
            }
            Go(free.Where(u => D(u, c) > REACHED_RADIUS), c, true);
        }
        else if (Joined != null)
        {
            Joined = null;
            Brain.SendHome(army.Where(u => !Brain.Claims.Has(u)).ToList());
        }
    }

    /// <summary>An ally's base under attack - HELP_ATTACKERS or more enemy fighters within HELP_RADIUS of its main building
    /// (or the centre of its buildings) - gets a support squad from the pool (TribeAI.SupportAlly: the original's answer
    /// to a "help" request, up to 4 / 6 / 8 units; one at a time). The remake's own (2026-11-11): the original's computer
    /// allies helped only when asked in the chat; an AI commander stood by while a human seat's village was raided.</summary>
    void HelpAllies()
    {
        foreach (var s in R.Seats)
        {
            if (s == Seat || s.Player == null || s.Player.Defeated || !W.Buildings.Any(b => b.Alive && b.Owner == s.Player)) continue;
            if (Brain.BaseOf(s.Player) is not { } at) continue;
            int n = 0;
            foreach (var u in W.Units)
                if (u.Alive && !u.Parked && !u.Inside && u.Owner != null && P.IsEnemy(u.Owner) && !u.Wild && !u.IsWorker && !u.Naval && D(u, at) < HELP_RADIUS) n++;
            if (n < HELP_ATTACKERS) continue;
            var why = Brain.SupportAlly(at, HELP_RADIUS + 15);
            if (why == null) { HelpedAt = W.Time; L.Info($"seat {Seat.Seat}: helps seat {s.Seat} ({n} attackers at {at.X:0},{at.Z:0})"); }
            return;
        }
    }
    /// <summary>when it last sent a support squad to an ally (game time; null: never)</summary>
    public double? HelpedAt { get; private set; }
    double _helpNext;
    public const double HELP_RADIUS = 45, HELP_THINK = 5;
    public const int HELP_ATTACKERS = 2;

    public override string ToString() => $"seat {Seat.Seat} {Order}{(Point != null ? $" ({Point.Value.X:0}, {Point.Value.Z:0})" : "")} aggression {Aggression}{(Reached ? " reached" : "")}";
}
