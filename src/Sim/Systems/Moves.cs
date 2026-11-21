// Special moves (tech tree "Moves" actions): unit abilities such as Kick, Tornado, Shotgun, and the commands of
// buildings (open/close gates, buy resources, dismantle, self-destruct, burn).
//
// Reference: docs/spec/moves.md. A move's tech tree "duration" is its cooldown (not a cast time). Moves are offered
// where the action's "locations" list the unit class; they appear once the enabling upgrade made them visible.
//
// Adding a move: add an entry to MOVES keyed by the action id.
//   Auto(W, u, enemy)      -> bool   automatic move: checked on every attack opportunity (replaces that hit)
//   Self: true                       instant move without a target
//   Target: "unit" | "enemy" | "own" | "building" | "ground" | "animal" | "vehicle" | "gate" | "spirit"
//   Range: metres                    the unit walks this close to the target first
//   Run(W, u, target, pos, spirit) -> bool   the effect; returning false means "not done" (no cooldown)
//   NoCooldown: true                 (original quirks: stampede, lockpicking)
//
// Playing out (docs/SIMULATION.md "Special moves play out"): a move with an animation begins an act in its Run -
// Act(u, clips) plays the original's clip, After(u, sec, kind, args) / AfterClip(u, kind, args) put its effects at the
// original's moment of it (a WaitAction / time mark of the task, the end of its AnimAction) - and the caller
// (UseMoveAt / SpecialUpdate / AutoMove: BeginAct) keeps the unit in that act until the clip ends: unstoppable (a new
// order waits for the end), broken by a stun (the effects still to come are lost), back to its fight afterwards
// (ReturnToFight). An effect the original applies when the task starts its clip is applied in Run at once.
//
// Ported from remake/src/game/systems/moves.js (pw-exporter 98c7703).
// C# notes: the JS's play(u, name, cb) callbacks are AnimDone kinds ("busy"); the targets of moves on spirits (which
// are not entities) are passed as their own argument; the JS world method reveal() is RevealAround here (World has a
// class Reveal); digOut's answer is DoDigOut (the hook DigOut has none). The effects the JS drew itself (W.fx: dust,
// fireworks' glows) are events for the view ("dust", "glow").
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public delegate bool MoveRun(World W, Unit u, Entity? t, V3? pos, Spirit? s);
public delegate bool MoveAuto(World W, Unit u, Entity? e);

/// <summary>An entry of MOVES (see the file's header).</summary>
public sealed class MoveDef
{
    public string Id = "";
    public MoveAuto? Auto;
    public bool Self, NoCooldown, Chase, AnySpirit;
    public string? Target;
    public double Range;
    public Func<World, Unit, double>? RangeOf;
    public MoveRun Run = (W, u, t, p, s) => false;
    public override string ToString() => Id;
}

public static class Moves
{
    static double Clamp(double v, double a, double b) => Math.Max(a, Math.Min(b, v));
    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    /// <summary>enemies of u within R of c (units; buildings too with buildings; only these classes with types)</summary>
    public static List<Entity> EnemiesNear(World W, Entity u, V3 c, double R, bool buildings = false, string[]? types = null)
    {
        var res = new List<Entity>();
        void Test(Entity v)
        {
            if (!v.Alive || v.Inside || !u.IsEnemy(v)) return;
            if (types != null && Array.IndexOf(types, v.Cls) < 0) return;
            double d = v is Building b ? b.SurfDist(c.X, c.Z) : Hypot(v.Pos.X - c.X, v.Pos.Z - c.Z) - v.Radius;
            if (d <= R) res.Add(v);
        }
        W.UHash.Query(c.X, c.Z, R + 8, Test);
        if (buildings) W.BHash.Query(c.X, c.Z, R + 20, Test);
        return res;
    }

    /// <summary>plays the first of the clips the unit has, once (busy until it ends) -> its length (0: none). Not a move's
    /// act (jumps, digging in, getting up): the unit is not held in it.</summary>
    public static double Play(Unit u, params string[] names)
    {
        var a = u.Anim!.Pick(names);
        if (a == null) return 0;
        u.BusyAnim = true;
        u.Anim.Play(a, new AnimOpts { Loop = false, Restart = true, Fade = 0.1, OnDone = new AnimDone("busy") });
        return u.Anim.Duration(a);
    }

    /// <summary>a move's act (the original task's AnimAction): plays the first of the clips the unit has, `loops` times, and
    /// begins the act the caller holds the unit in until the clip ends (see the file's header). A model without any of
    /// the clips: the act lasts until its last effect (At).</summary>
    public static MoveAct Act(Unit u, params string[] names) => ActLoops(u, 1, names);
    public static MoveAct ActLoops(Unit u, int loops, params string[] names)
    {
        var W = u.World;
        var a = u.Anim!.Pick(names);
        double d = a != null ? u.Anim.Duration(a) * loops : 0;
        if (a != null)
        {
            u.BusyAnim = true;
            u.Anim.Play(a, new AnimOpts { Loop = loops > 1, Restart = true, Fade = 0.1, OnDone = loops > 1 ? null : new AnimDone("busy") });
        }
        var act = u.NewAct ??= new MoveAct { Start = W.Time, End = W.Time };
        act.Clip = a;
        act.End = Math.Max(act.End, W.Time + d);
        return act;
    }

    /// <summary>a model in the unit's right hand while the act lasts, its weapons hidden (HideWeapons + SetLinkGFX "HndR":
    /// Cole's shotgun, Babbage's minigun)</summary>
    public static void Hold(Unit u, string gfx)
    {
        var act = u.NewAct ??= new MoveAct { Start = u.World.Time, End = u.World.Time };
        u.AttachTool(gfx);
        act.Tool = u.ToolShown;
    }

    /// <summary>an effect of the act `sec` seconds after its start: DELAYED[kind](world, args) then, unless a stun broke
    /// the act before (0: at once)</summary>
    public static void After(Unit u, double sec, string kind, Args args)
    {
        var W = u.World;
        if (!Delayed.DELAYED.TryGetValue(kind, out var fn)) throw new ArgumentException("unknown delayed call " + kind);
        if (sec <= 0) { fn(W, args); return; }
        var act = u.NewAct ??= new MoveAct { Start = W.Time, End = W.Time };
        act.Hits.Add(new ActHit { T = act.Start + sec, Kind = kind, A = args });
        act.End = Math.Max(act.End, act.Start + sec);
    }

    /// <summary>an effect when the act's clip has ended (the original: the task's next state after its AnimAction);
    /// `min`: the time it takes at least (a model without the clip)</summary>
    public static void AfterClip(Unit u, string kind, Args args, double min = 0)
    {
        var act = u.NewAct;
        double len = act != null ? act.End - act.Start : 0;
        After(u, Math.Max(len, min), kind, args);
    }

    /// <summary>CharacterBash / ShakeOff: the clip, after `delay` (WaitAction) characters around knocked back and flat area
    /// damage (the unit's own weapon damage) (dmg: a fixed damage instead of the unit's weapon damage - MIRAGE's ShakeOff
    /// calls pass one); the unit stays in it until the clip ends (Break() = false)</summary>
    public static void Bash(World W, Unit u, string[] anim, double R, double delay, double spread = 0.1, double[]? force = null, double? dmg = null, double? endDmg = null)
    {
        force ??= new[] { 1, 3.9 };
        endDmg ??= dmg;
        Act(u, anim);
        var a = new Args { ["u"] = u, ["R"] = R, ["spread"] = spread, ["f0"] = force[0], ["f1"] = force[1] };
        if (dmg != null) a["dmg"] = dmg.Value;
        if (endDmg != null) a["endDmg"] = endDmg.Value;
        After(u, delay, "bash", a);
    }
    public static void Bash(World W, Unit u, string anim, double R, double delay, double spread = 0.1, double[]? force = null, double? dmg = null, double? endDmg = null)
        => Bash(W, u, new[] { anim }, R, delay, spread, force, dmg, endDmg);

    public static bool Crowd(World W, Unit u, double R, int n) => EnemiesNear(W, u, u.Pos, R).Count(v => v.Kind == "unit") >= n;
    public static bool InFront(Unit u, Entity e, double ang = 0.39)
    {
        double a = SMath.Atan2(-(e.Pos.X - u.Pos.X), -(e.Pos.Z - u.Pos.Z)) - u.Heading;
        return Math.Abs(SMath.Atan2(SMath.Sin(a), SMath.Cos(a))) <= ang;
    }
    public static double Pct(Entity v, double p, double lo, double hi) => Clamp(v.Hp * p, lo, hi);

    public static readonly Dictionary<string, MoveDef> MOVES = new(StringComparer.Ordinal);
    /// <summary>order in which automatic moves are tried (character.usl:200, Animal.usl:64)</summary>
    public static readonly List<string> AUTO_ORDER = new() { "Matrix", "Quake", "Roar", "Twister", "Kick", "burst_arrow", "multishot", "Mammoth_Stampede", "StegoBash" };
    /// <summary>automatic moves have no button (tech tree visibility 0); the server checks the invention of the same name instead</summary>
    public static readonly Dictionary<string, string> AUTO_INVENT = new(StringComparer.Ordinal)
    {
        ["Kick"] = "kick", ["Roar"] = "roar", ["Quake"] = "quake", ["Twister"] = "twister", ["Matrix"] = "matrix", ["burst_arrow"] = "burst_arrow",
        ["multishot"] = "multishot", ["Mammoth_Stampede"] = "mammoth_stampede", ["StegoBash"] = "stegosaurus_caudal_bash",
    };

    static void Add(MoveDef m) => MOVES[m.Id] = m;
    static Entity? LastFoe(Unit u) => u.Task.Target ?? u.LastTarget;
    static V3 At(Entity? t, V3? pos) => t != null ? t.Pos : pos!.Value;

    static readonly string[] INSECT_TARGETS = { "hu_corn_field", "hu_lumberjack_cottage", "hu_fireplace", "hu_warehouse", "aje_bazaar", "aje_slaughterhouse", "aje_resource_collector", "ninigi_fireplace", "ninigi_hunting_lodge", "ninigi_paddy" };

    static Moves()
    {
        // ---------------------------------------------------------------- Hu
        Add(new MoveDef { Id = "Kick", Auto = (W, u, e) => e is Unit && InFront(u, e) && W.RangeZone(u, e) > 0,
            Run = (W, u, e, p, s) => { Act(u, "res_sm_kick"); W.TakeDmg(u, e!, 2.0, 0.4); if (e!.Cls == "CHTR") W.Knockback(e, u.Pos, 3, 0.7); return true; } });   // ResKick.usl: the blow as the clip starts
        Add(new MoveDef { Id = "Roar", Auto = (W, u, e) => Crowd(W, u, u.Radius + 4, 3), Run = (W, u, t, p, s) => { Bash(W, u, "res_sm_jump", u.Radius + 4, 1.2); return true; } });
        Add(new MoveDef { Id = "Quake", Auto = (W, u, e) => Crowd(W, u, u.Radius + 5, 3), Run = (W, u, t, p, s) => { Bash(W, u, "res_sm_jump", u.Radius + 5, 1.2); return true; } });
        Add(new MoveDef { Id = "defensive_mode_on", Self = true, NoCooldown = true, Run = (W, u, t, p, s) => { W.LocalFilter(u, "Hu/Upgrades/hu_warrior/hu_defensive_mode", true); u.Defensive = true; return true; } });
        Add(new MoveDef { Id = "defensive_mode_off", Self = true, NoCooldown = true, Run = (W, u, t, p, s) => { W.LocalFilter(u, "Hu/Upgrades/hu_warrior/hu_defensive_mode", false); u.Defensive = false; return true; } });
        Add(new MoveDef { Id = "jetpack", Target = "ground", Range = 100, Run = (W, u, t, p, s) => W.Jump(u, p!.Value, 100) });
        Add(new MoveDef { Id = "reveal", Self = true, Run = (W, u, t, p, s) => { Act(u, "res_guarding").Breakable = true; W.RevealAround(u.Owner!, u.Pos, 30, 5); return true; } });   // FindTracesAndTraps: a plain AnimAction (an order ends it)
        Add(new MoveDef { Id = "oracle", Target = "ground", Range = 1e9, Run = (W, u, t, p, s) => { var q = At(t, p); W.RevealAround(u.Owner!, q, 20, 10, true); Act(u, "res_guarding", "heal_0").Breakable = true; return true; } });   // FindTracesAndTraps as reveal
        Add(new MoveDef { Id = "insects", Target = "building", Range = 20, Run = (W, u, b, p, s) =>
        {
            if (b == null || Array.IndexOf(INSECT_TARGETS, b.Name) < 0 || b.Owner == null) return false;
            Act(u, "nat_throw");          // Insects.usl: the swarm leaves as the clip starts
            // every second the building's owner loses 20 food, for 180 s
            W.PeriodicEffect(b, 180, 1, "insects");
            return true;
        } });
        Add(new MoveDef { Id = "illusion", Self = true, Run = (W, u, t, p, s) =>
        {
            Act(u, "heal_0").Breakable = true;   // CreateIllusions: a plain AnimAction, the illusions as it starts
            for (int i = 0; i < 3; i++)
            {
                double a = W.Rng.Next() * 3.13;
                var c = W.SpawnUnit(u.Name, u.Owner, u.Pos.X + SMath.Cos(a) * 4, u.Pos.Z + SMath.Sin(a) * 4, u.Level, u.Heading, new UnitOpts { Illusion = true });
                if (c != null) { W.InheritInfection(u, c); W.Later(60, "expire", new Args { ["u"] = c }); }
            }
            return true;
        } });
        Add(new MoveDef { Id = "Mammoth_Stampede", NoCooldown = true, Chase = true, Auto = (W, u, e) => u.RageT == 0 && e != null && W.Reach(u, e) >= 10 && W.Reach(u, e) <= u.Fow,
            Run = (W, u, t, p, s) =>
            {
                u.RageT = 30;
                W.PeriodicEffect(u, 30, 2, "stampede");
                return true;
            } });
        Add(new MoveDef { Id = "Mammoth_Trumpet", Self = true, Run = (W, u, t, p, s) => { Act(u, "trumpet"); AfterClip(u, "trumpet", new Args { ["u"] = u }); return true; } });   // CMammothTrumpet: after the clip
        Add(new MoveDef { Id = "titan_paw", Self = true, Run = (W, u, t, p, s) => { Act(u, "pawing"); AfterClip(u, "paw", new Args { ["u"] = u }); return true; } });   // CPaw: after the clip
        Add(new MoveDef { Id = "titan_shake_off", Self = true, Run = (W, u, t, p, s) => { Bash(W, u, "titan_rage", u.Radius + 5, 0.1, 1.8, new[] { 3, 6.9 }); return true; } });
        Add(new MoveDef { Id = "rhino_shake_off", Self = true, Run = (W, u, t, p, s) => { Bash(W, u, "sm_shake_off", u.Radius + 5, 0.4, 2.0, new[] { 3, 6.9 }); return true; } });
        // ---------------------------------------------------------------- Aje
        // Resurrect (Resurrect.usl): walk to a spirit, pray, and the unit comes back at its level if the pyramid has room
        Add(new MoveDef { Id = "Resurrect", Target = "spirit", Range = 20, Run = (W, u, t, p, s) =>
        {
            if (s == null || !s.Alive || s.Owner != u.Owner || s.Busy) return false;
            if (u.Owner!.SlotFree(s.Level, W.PyramidFor(u.Owner)) != null) return false;
            s.Busy = true;
            u.Owner.QueuedAtLevel[s.Level - 1]++; u.Owner.QueuedUnits++;
            u.BusyAnim = true; u.Anim!.Play(u.Anim.Pick("praying_wall", "heal_0") ?? u.IdleAnim);
            // pray 6.5 s (praying_wall), then 1.5 s wait, then the unit returns
            W.Trap(u, 8, "resurrect");
            W.Later(8, "resurrect", new Args { ["u"] = u, ["s"] = s });
            return true;
        } });
        Add(new MoveDef { Id = "Twister", Auto = (W, u, e) => Crowd(W, u, u.Radius + 4, 3), Run = (W, u, t, p, s) => { Bash(W, u, "nat_sm_twister", u.Radius + 4, 0.6, 0.2); return true; } });
        Add(new MoveDef { Id = "Matrix", Auto = (W, u, e) => Crowd(W, u, u.Radius + 6, 3), Run = (W, u, t, p, s) => { Bash(W, u, "nat_sm_matrix", u.Radius + 6, 1.0, 0.6); return true; } });
        Add(new MoveDef { Id = "termites", Target = "building", Range = 20, Run = (W, u, b, p, s) =>
        {
            if (b == null || b.Owner == null || b.Owner == u.Owner) return false;
            Act(u, "termites");           // Termites.usl: the termites leave as the clip starts
            W.PeriodicEffect(b, 120, 1, "termites", new Args { ["by"] = u.Owner });
            return true;
        } });
        Add(new MoveDef { Id = "camouflage", Self = true, NoCooldown = true, Run = (W, u, t, p, s) =>
        {
            if (u.St.Camo.Has("aje")) { W.SetCamoOf(u, "aje", false); return true; }
            if (u.Cd.Get("camouflage") > W.Time) return false;
            W.SetCamoOf(u, "aje", true);
            return true;
        } });
        Add(new MoveDef { Id = "quicksand", Target = "ground", Range = 30, Run = (W, u, t, p, s) => { Act(u, "heal_0"); W.SpawnQuicksand(u.Owner!, At(t, p)); return true; } });   // QuicksandTask.usl: the trap, then the clip
        Add(new MoveDef { Id = "tornado", Target = "ground", Range = 30, Run = (W, u, t, p, s) =>
        {
            var q = At(t, p);
            Act(u, "tornado");            // Tornado.usl: the tornado, then the clip
            var victims = EnemiesNear(W, u, q, 10, buildings: true);
            foreach (var v in victims) if (v.Kind == "unit") W.Trap(v, 11, "tornado");
            W.PeriodicEffect(u, 11, 1, "tornado", new Args { ["victims"] = victims, ["p"] = q, ["n"] = 0.0 }, true);
            return true;
        } });
        Add(new MoveDef { Id = "AlloScrunch", Self = true, Run = (W, u, t, p, s) =>
        {
            var e = LastFoe(u);
            if (e == null || !e.Alive || !InFront(u, e) || W.RangeZone(u, e) == 0) return false;
            Act(u, "sm_scrunch"); W.TakeDmg(u, e, 2.0, 0.4); if (e.Cls == "CHTR") W.Knockback(e, u.Pos, 3, 0.7); return true;   // CAlloScrunch: the bite as the clip starts
        } });
        Add(new MoveDef { Id = "BrachioStomp", Self = true, Run = (W, u, t, p, s) =>
        {
            Act(u, "stomp_harvest");      // CBrachioHarvest: WaitAction(1.8), then the trees and the damage
            After(u, 1.8, "stomp", new Args { ["u"] = u });
            return true;
        } });
        Add(new MoveDef { Id = "StegoBash", Auto = (W, u, e) => Crowd(W, u, u.Radius + 5, 4), Run = (W, u, t, p, s) => { Bash(W, u, "sm_attack_back", u.Radius + 5, 1.4, 0.1, new[] { 3, 6.9 }); return true; } });
        Add(new MoveDef { Id = "trex_scrunch", Self = true, Run = (W, u, t, p, s) => { Bash(W, u, "trex_fm_2", u.Radius + 5, 2.0, 0.2, new[] { 3, 6.9 }); return true; } });
        Add(new MoveDef { Id = "trex_roar", Self = true, Run = (W, u, t, p, s) => { Act(u, "menace"); After(u, 0.8, "roar", new Args { ["u"] = u }); return true; } });   // CStunningRoar("menace", 7, 0.8)
        // ---------------------------------------------------------------- Ninigi
        Add(new MoveDef { Id = "entrench", Self = true, NoCooldown = true, Run = (W, u, t, p, s) => u.St.Camo.Has("entr") ? W.DoDigOut(u) : W.DigIn(u) });
        Add(new MoveDef { Id = "lockpicking", Target = "gate", Range = 4, NoCooldown = true, Run = (W, u, t, p, s) =>
        {
            if (t is not Building g || g.Def?.Gate != true || g.GateState == GATE_OPEN || g.Owner == null || !u.Owner!.IsEnemy(g.Owner)) return false;
            W.SetCamoOf(u, "disg", false);
            u.BusyAnim = true; u.Anim!.Play(u.Anim.Pick("potter") ?? u.IdleAnim);
            W.Trap(u, 15, "lockpicking");
            W.Later(15, "lockpick", new Args { ["u"] = u, ["g"] = g });
            return true;
        } });
        Add(new MoveDef { Id = "fireworks", Self = true, Run = (W, u, t, p, s) => { Act(u, "potter_ground").Breakable = true; W.RevealAround(u.Owner!, u.Pos, 100, 10, true); for (int i = 0; i < 6; i++) W.Later(i * 0.4, "firework", new Args { ["u"] = u, ["i"] = (double)i }); return true; } });
        Add(new MoveDef { Id = "burst_arrow", Auto = (W, u, e) => e != null && EnemiesNear(W, u, e.Pos, e.Radius + 5).Count >= 3,
            Run = (W, u, e, p, s) => { Act(u, "tec_sm_burst_arrow"); W.Later(0.4, "burstArrow", new Args { ["u"] = u, ["e"] = e, ["w"] = u.Weapons.Long }); return true; } });
        Add(new MoveDef { Id = "multishot", Auto = (W, u, e) => e != null, Run = (W, u, e, p, s) => { Act(u, "tec_sm_multishot"); W.Later(0.4, "multishot", new Args { ["u"] = u, ["e"] = e }); return true; } });
        Add(new MoveDef { Id = "doping", Self = true, Run = (W, u, t, p, s) => { u.St.T["doping"] = W.Time + 7; return true; } });
        Add(new MoveDef { Id = "barrage", Self = true, Run = (W, u, t, p, s) => { Bash(W, u, "idle_1", u.Radius + 25, 1.0, 2.0); return true; } });
        Add(new MoveDef { Id = "enchain", Target = "enemy", Range = 30, Run = (W, u, v, p, s) => { if (v is not Unit) return false; W.Trap(v, 20, "enchain"); return true; } });
        Add(new MoveDef { Id = "lacerate", Self = true, Run = (W, u, t, p, s) =>
        {
            var e = LastFoe(u);
            if (e == null || !e.Alive) return false;
            ActLoops(u, 2, "harvest");    // VHCLSpec.usl: "harvest" twice, WaitAction(0.6), then the cut
            After(u, 0.6, "lacerate", new Args { ["u"] = u, ["e"] = e });
            return true;
        } });
        // ---------------------------------------------------------------- heroes (level 3+)
        // Cole's shotgun (Shotgun.usl CShotgun): the bystanders within 8 m of the target are taken when he fires; the
        // characters smaller than him are thrown 1.95 s into the clip (the target 9 m, the others 6..9 m), and when the
        // clip "shotgun" is over (the task's "reaction" state) the target and the bystanders take their damage and the
        // target turns on him
        Add(new MoveDef { Id = "Shotgun", Target = "enemy", Range = 15, Run = (W, u, t, p, s) =>
        {
            var c = At(t, p);
            Act(u, "shotgun"); Hold(u, "cole_shotgun");
            var near = EnemiesNear(W, u, c, 8, buildings: true).Where(v => v != t).ToList();
            double size = u.Stats?.Size ?? 0;
            if (t != null && t.Cls == "CHTR" && size > (t.Stats?.Size ?? 0)) W.Knockback(t, u.Pos, 9, 1.95);
            foreach (var v in near)
                if (v.Cls == "CHTR" && size > (v.Stats?.Size ?? 0)) W.Knockback(v, u.Pos, 6 + Math.Floor(W.Rng.Next() * 4), 1.95 + Math.Floor(W.Rng.Next() * 15) / 100);
            AfterClip(u, "shotgunHit", new Args { ["u"] = u, ["t"] = t, ["near"] = near }, 2.8);
            return true;
        } });
        // Bela (Throwdownshot.usl): the clip "throwdownshot" and her arrow (CSniperArrow) - it leaves 2 s into the clip
        Add(new MoveDef { Id = "Snipershot", Target = "enemy", Range = 50, Run = (W, u, t, p, s) => { Act(u, "throwdownshot"); W.SpecialShot(u, t!, 2.0, "snipershot"); return true; } });
        // Tesla (DestroyVHCL.usl): the clip "potter", then the vehicle is wrecked
        Add(new MoveDef { Id = "Tesla_DstrVhcl_0", Target = "vehicle", Range = 2, Run = (W, u, t, p, s) => { if (t == null || t.Cls != "VHCL") return false; Act(u, "potter"); AfterClip(u, "destroyVhcl", new Args { ["u"] = u, ["t"] = t }, 2.0); return true; } });
        // the hermit (HealANML.usl): "heal_0" five times, then every own animal within 40 m is healed fully
        Add(new MoveDef { Id = "Druid_HealAnml_0", Self = true, Run = (W, u, t, p, s) => { ActLoops(u, 5, "heal_0"); AfterClip(u, "healAnml", new Args { ["u"] = u }, 12); return true; } });
        // Babbage (BabbageMinigun.usl): 8 bursts into the cone while "babbage_minigun" runs (the first at once, every 0.5 s)
        Add(new MoveDef { Id = "Babbage_Minigun_0", Target = "enemy", Range = 30, Run = (W, u, t, p, s) =>
        {
            var c = At(t, p);
            ActLoops(u, 2, "babbage_minigun"); Hold(u, "babbage_minigun");
            for (int i = 0; i < 8; i++) After(u, i * 0.5, "minigun", new Args { ["u"] = u, ["c"] = c });
            u.NewAct!.End = Math.Max(u.NewAct.End, u.NewAct.Start + 4);
            return true;
        } });
        Add(new MoveDef { Id = "Mayor_Specialmove_0", Self = true, Run = (W, u, t, p, s) =>
        {
            Act(u, "res_sm_jump");        // MayorSpecialMove.usl: the damage, then the clip (the unit in it to its end)
            foreach (var v in EnemiesNear(W, u, u.Pos, 6))
            {
                W.TakeDirectDmg(v, Pct(v, 0.25, 300, 2000), 0, u.Owner, false, u);
                if (v.Cls == "CHTR") { double dist = 1 + W.Rng.Next() * 3.9; W.Knockback(v, u.Pos, dist, 0.1 + W.Rng.Next() * 1.5); }
            }
            return true;
        } });
        Add(new MoveDef { Id = "warden_spec", Self = true, Run = (W, u, t, p, s) => { Act(u, "warden_spec"); W.UHash.Query(u.Pos.X, u.Pos.Z, 26, o => { if (o.Alive && o.Owner != null && u.Owner!.IsFriend(o.Owner) && u.DistTo(o) <= 20) o.St.T["anmlImmune"] = W.Time + 7; }); return true; } });
        // Lovelace / Schliemann (Shotgun.usl CAdaDeathShot): the clip "lovelace_musket" and the shot (CAdaArrow) - it
        // leaves 1.72 s into the clip
        Add(new MoveDef { Id = "Ada_DeathShoot", Target = "enemy", Range = 40, Run = (W, u, t, p, s) => { Act(u, "lovelace_musket"); W.SpecialShot(u, t!, 1.72, "deathShot"); return true; } });
        Add(new MoveDef { Id = "schliemann_deathshoot", Target = "enemy", Range = 40, Run = (W, u, t, p, s) => MOVES["Ada_DeathShoot"].Run(W, u, t, p, s) });
        Add(new MoveDef { Id = "schliemann_special_move_1", Target = "own", Range = 15, Run = (W, u, t, p, s) =>
        {
            if (t == u || t == null || t.Owner != u.Owner || t.Kind != "unit") return false;
            Act(u, "aje_velo_strike_0");  // CSacrifice: the strike lands 0.9 s into the clip (KILL_ANIMDELAY)
            After(u, 0.9, "sacrifice", new Args { ["u"] = u, ["t"] = t });
            return true;
        } });
        Add(new MoveDef { Id = "livingstone_special_move_1", Self = true, Run = (W, u, t, p, s) =>
        {
            double T = W.Data.Extra.HeroConsts.TryGetValue("LIVINGSTONE_VANISH_TIME", out var v) && v != 0 ? v : 10;
            W.SetCamoOf(u, "hero", true); u.St.T["vanish"] = W.Time + T; W.Later(T, "unvanish", new Args { ["u"] = u }); return true;
        } });
        Add(new MoveDef { Id = "Hypnosis", Target = "animal", Range = 10, Run = (W, u, t, p, s) => { if (t == null || t.Cls != "ANML") return false; Act(u, "sm_01"); After(u, 2.6, "hypnosis", new Args { ["t"] = t }); u.NewAct!.End = Math.Max(u.NewAct.End, u.NewAct.Start + 3.4); return true; } });   // MickDundeeMove.usl: 2.6 s, then the animal sleeps; the task ends at 3.4 s

        MirageMoves.Register();
    }

    /// <summary>buildings.js GATE</summary>
    public const int GATE_OPEN = 0, GATE_CLOSED = 1, GATE_AUTO = 2;

    /// <summary>makes sure the tables are filled (the static constructor ran)</summary>
    public static void Init() { }
}

// ------------------------------------------------------------------------------------------------ world mixin
public sealed partial class World
{
    static readonly Logger LM = Log.For("Systems.Moves");

    /// <summary>moves a unit or building offers (visible ones only)</summary>
    public List<ActionDef> MovesOf(Entity e)
    {
        if (e.Owner == null) return new List<ActionDef>();
        // (MIRAGE's flyers' "Land" / "Take off": only a unit that flies now - not a zeppelin without the option
        // FlyingEnabled, a helicopter in its assault mode or a wild flyer; Systems/Flyers.cs)
        return Data.ActionsOf(e.RulesOwner(), e.Name, e.Kind, e.Def?.Type ?? e.Cls)
            .Where(a => a.Kind == "Moves" && (!Flyers.IsAbility(a.Id) || (e is Unit fu && fu.Flyer is { Kind: not "wild" }))).ToList();
    }

    /// <summary>why a move can't be used now (null = ok)</summary>
    public string? MoveCheck(Entity e, ActionDef a)
    {
        if (e is Unit u && u.Illusion) return "illusion";
        var why = Data.Check(e.RulesOwner(), a, e);
        if (why != null) return why;
        if (Moves.MOVES.TryGetValue(a.Id, out var M) && !M.NoCooldown && e.Cd.Get(a.Id) > Time) return "cooldown";
        return null;
    }

    /// <summary>a move's cooldown (its tech tree duration) for this unit</summary>
    public double MoveTime(Entity e, string id) { var a = MovesOf(e).FirstOrDefault(x => x.Id == id); return a != null ? a.Time : 0; }
    public double CooldownLeft(Entity e, ActionDef a) => Math.Max(0, e.Cd.Get(a.Id) - Time);

    /// <summary>use a move: instant ones happen now, targeted ones start a task that walks into range first; null = done,
    /// else the reason ("target": choose a target first, "cooldown", "req" ...)</summary>
    public string? UseMove(Entity e, ActionDef a, Entity? target, (double X, double Z)? ground)
    {
        V3? pos = ground is { } g ? new V3(g.X, Height(g.X, g.Z), g.Z) : null;
        return UseMoveAt(e, a, target, pos);
    }

    /// <summary>UseMove with the point as a vector (the JS's pos)</summary>
    public string? UseMoveAt(Entity e, ActionDef a, Entity? target, V3? pos)
    {
        if (a.Kind != "Moves") return "req";
        if (GenericMove(e, a, target, pos, out var generic)) return generic;
        if (!Moves.MOVES.TryGetValue(a.Id, out var M)) return "req";
        // (the moves of MOVES are units' moves)
        if (e is not Unit u) { LM.WarnOnce("bmove|" + a.Id, $"move {a.Id} of a building"); return "req"; }
        var why = MoveCheck(u, a);
        if (why != null) return why;
        // a move playing out cannot be broken by another one (SetUnStoppable)
        if (InAct(u) && !u.Task.Act!.Breakable) return "busy";
        if (M.Self)
        {
            var prev = u.Task;
            u.NewAct = null;
            bool ok = M.Run(this, u, null, null, null);
            if (!ok) { u.NewAct = null; return "target"; }
            if (!M.NoCooldown) u.Cd.Set(a.Id, Time + a.Time);
            if (a.Id != "camouflage" && u.St.Camo.Has("aje")) SetCamoOf(u, "aje", false);
            // a move with a clip replaces what the unit did (the original's AddTask) and plays out
            if (u.NewAct != null) { if (u.Task == prev) ReleaseTask(u); BeginAct(u, prev.Act != null ? prev.Prev : prev, M, a, true); }
            return null;
        }
        Spirit? spirit = null;
        if (M.Target == "spirit")
        {
            // (Tarna's reanimation takes any player's spirit: GetBestSpiritToResurrect(pos, 60) - the nearest one)
            spirit = pos == null ? null : M.AnySpirit ? NearestSpirit(null, pos.Value.X, pos.Value.Z, 60) : NearestSpirit(u.Owner, pos.Value.X, pos.Value.Z);
            if (spirit == null) return "target";
            target = null;
        }
        if (target == null && spirit == null && pos == null) return "target";
        if (target != null && M.Target == "ground") pos = target.Pos;
        ReleaseTask(u);
        u.Task = new UnitTask("special") { Move = M, Action = a, Target = target, Spirit = spirit, MovePos = pos, User = true, Prev = InAct(u) ? u.Task.Prev : u.Task };
        u.Path.Clear();
        return null;
    }

    partial void SpecialUpdate(Unit u, double dt)
    {
        var t = u.Task;
        if (t.Act != null) { ActUpdate(u, t); return; }
        var M = t.Move!;
        Entity? tgt = t.Target; var sp = t.Spirit;
        if ((tgt != null && !tgt.Alive) || (sp != null && !sp.Alive)) { u.Task = UnitTask.Idle(); return; }
        V3 p = tgt != null ? tgt.Pos : sp != null ? sp.Pos : t.MovePos!.Value;
        double d = tgt != null ? Reach(u, tgt) : Hypot(p.X - u.Pos.X, p.Z - u.Pos.Z);
        double range = M.RangeOf != null ? M.RangeOf(this, u) : M.Range;
        bool hasTgt = tgt != null || sp != null;
        if ((d > range + (hasTgt ? 0 : 0.5) && M.Target != "ground") || (M.Target == "ground" && d > range))
        {
            if (u.Path.Count == 0 || (t.RepathT -= dt) <= 0) { u.SetPath(p.X, p.Z); t.RepathT = 1; if (u.Path.Count == 0) { u.Task = UnitTask.Idle(); return; } }
            double s = u.Steer(dt, u.RunSpeed, 0.5);
            u.MoveAnim(s, true);
            return;
        }
        u.Path.Clear(); u.Vel = V3.Zero;
        if (u.Face(p.X, p.Z, dt, 8) > 0.3 && M.Target != "ground") return;
        u.NewAct = null;
        bool ok = M.Run(this, u, tgt, t.MovePos, sp);
        if (ok && !M.NoCooldown) u.Cd.Set(t.Action!.Id, Time + t.Action.Time);
        if (u.St.Camo.Has("aje")) SetCamoOf(u, "aje", false);
        if (u.Task != t) { u.NewAct = null; return; }       // (a move may start a task of its own: World.Cast)
        // the move plays out (its clip, its effects at their time), else the unit is free again
        if (!ok || !BeginAct(u, t.Prev, M, t.Action, t.User)) { u.NewAct = null; u.Task = UnitTask.Idle(); }
    }

    // ------------------------------------------------------------------ a move playing out (MoveAct)
    /// <summary>is the unit in a move's act (its clip running, its effects to come)</summary>
    public static bool InAct(Unit u) => u.Task.Type == "special" && u.Task.Act != null;

    /// <summary>the act the move's Run began (u.NewAct) becomes the unit's task: it stands in the clip, its effects land at
    /// their time, then back to `prev` (ReturnToFight). false: the move began none.</summary>
    bool BeginAct(Unit u, UnitTask? prev, MoveDef? M, ActionDef? a, bool user)
    {
        var act = u.NewAct; u.NewAct = null;
        if (act == null) return false;
        if (act.Hits.Count > 1) act.Hits = act.Hits.OrderBy(h => h.T).ToList();      // (stable: same times keep their order)
        u.Task = new UnitTask("special") { Phase = "act", Act = act, Move = M, Action = a, Prev = prev, User = user };
        u.Path.Clear(); u.Vel = V3.Zero;
        return true;
    }

    // the unit stands in the clip; the effects that are due land; at the clip's end it goes back to its fight
    void ActUpdate(Unit u, UnitTask t)
    {
        var act = t.Act!;
        u.Vel = V3.Zero;
        if (u.Path.Count > 0) u.Path.Clear();
        while (act.Next < act.Hits.Count && act.Hits[act.Next].T <= Time + 1e-9)
        {
            var h = act.Hits[act.Next++];
            Delayed.DELAYED[h.Kind](this, h.A);
            if (u.Task != t || !u.Alive) return;
        }
        if (act.Next < act.Hits.Count || Time + 1e-9 < act.End) return;
        EndAct(u, t);
    }

    /// <summary>the act is over: the order given meanwhile, else the fight it broke off (ReturnToFight), else idle</summary>
    void EndAct(Unit u, UnitTask t)
    {
        u.BusyAnim = false;
        if (t.Act!.Tool) u.AttachTool(null);                 // (ShowWeapons)
        u.Task = UnitTask.Idle();
        if (t.Next != null) { Order(u, t.Next); return; }
        var p = t.Prev;
        if (p != null && p.Type == "attack" && p.Act == null && ValidTarget(u, p.Target, p)) { u.Task = p; u.RepathT = 0; return; }
        if (u.Anim != null && u.IdleAnim != null) u.Anim.Play(u.StandAnim());
    }

    /// <summary>a stun (SetTrapped: SetTaskImmediate) breaks the act: the effects still to come are lost; an order given
    /// meanwhile still follows</summary>
    public void BreakAct(Unit u)
    {
        if (!InAct(u)) return;
        var t = u.Task;
        u.BusyAnim = false;
        if (t.Act!.Tool) u.AttachTool(null);
        u.Task = UnitTask.Idle();
        if (t.Next != null) Order(u, t.Next);
    }

    /// <summary>a special arrow (CSniperArrow, CAdaArrow): it leaves `delay` s after the shooter began its clip (CArrow.Set's
    /// start timer, not broken with the shooter's task) and flies like the shooter's shots; on impact DELAYED[hit]
    /// with {u, t} instead of the weapon's damage</summary>
    public void SpecialShot(Unit u, Entity t, double delay, string hit) => Later(delay, "specialShot", new Args { ["u"] = u, ["t"] = t, ["hit"] = hit });

    /// <summary>CheckSpecialMoves: the first automatic move whose condition holds replaces this hit
    /// (chasing: the unit is following its enemy - only moves marked Chase then)</summary>
    public bool AutoMove(Unit u, Entity? enemy, bool chasing = false)
    {
        if (u.Owner == null || u.Illusion || u.Inside) return false;
        int ver = u.RulesOwner()?.Tt?.Version ?? 0;
        var avail = u.AutoMoves;
        if (avail == null || avail.V != ver)
        {
            var list = MovesOf(u).Where(a => Moves.MOVES.TryGetValue(a.Id, out var m) && m.Auto != null).ToList();
            u.AutoMoves = avail = new AutoMovesList { V = ver, List = Moves.AUTO_ORDER.Select(id => list.FirstOrDefault(a => a.Id == id)).Where(a => a != null).ToList()! };
        }
        foreach (var a in avail.List)
        {
            Moves.AUTO_INVENT.TryGetValue(a.Id, out var inv);
            if (inv != null && !Data.Invented(u.RulesOwner(), inv, a.Tribe == "Special" ? u.Owner.Tribe : a.Tribe, u)) continue;
            if (inv == null && MoveCheck(u, a) != null) continue;
            if (u.Cd.Get(a.Id) > Time) continue;
            var M = Moves.MOVES[a.Id];
            if (chasing && !M.Chase) continue;
            if (!M.Auto!(this, u, enemy)) continue;
            u.NewAct = null;
            if (!M.Run(this, u, enemy, null, null)) { u.NewAct = null; continue; }
            if (!M.NoCooldown) u.Cd.Set(a.Id, Time + a.Time);
            // the move plays out, then the fight goes on (ReturnToFight)
            BeginAct(u, u.Task, M, a, u.Task.User);
            return true;
        }
        return false;
    }

    /// <summary>commands that aren't special moves: gates, markets, dismantling, self-destruction, fire (true: it is one;
    /// why: null = done, else the reason)</summary>
    bool GenericMove(Entity e, ActionDef a, Entity? target, V3? pos, out string? why)
    {
        why = null;
        var b = e as Building;
        switch (a.Id)
        {
            case "Open": if (b != null && b.Def!.Gate) SetGate(b, Moves.GATE_OPEN); else why = "req"; return true;
            case "Close": if (b != null && b.Def!.Gate) { if (b.BrokenT > 0) why = "busy"; else SetGate(b, Moves.GATE_CLOSED); } else why = "req"; return true;
            case "Auto": if (b != null && b.Def!.Gate) SetGate(b, Moves.GATE_AUTO); else why = "req"; return true;
            case "buy_food": case "buy_wood": case "buy_stone":
                why = Data.Check(e.RulesOwner(), a, e) ?? BuyResource(e.Owner!, a.Id.Substring(4));
                return true;
            case "BuildDown": why = b != null ? StartBuildDown(b, a) : "req"; return true;
            case "Kill": if (b != null) { SelfDestruct(b); return true; } Kill(e, null); return true;
            case "Burn": why = b != null ? (BurnResin(b) ? null : "cooldown") : "req"; return true;
            case "Attack": case "Walk": case "Stop": case "AggressiveTarget": case "AggroState_0": case "AggroState_1": case "AggroState_2":
            case "Formation_1": case "Formation_2": case "Formation_3": return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ helpers used by moves
    /// <summary>reveal camouflaged enemies and traps around a point (reveal / oracle / fireworks; JS: reveal)</summary>
    public void RevealAround(Player p, V3 c, double R, double dur, bool fow = false)
    {
        void Test(Entity v)
        {
            if (!v.Alive || v.Owner == null || p.IsFriend(v.Owner)) return;
            if (Hypot(v.Pos.X - c.X, v.Pos.Z - c.Z) > R + v.Radius) return;
            v.St.RevealT = Time + dur;
            if (v.St.Camo.Has("trap")) v.St.Camo.Delete("trap");
            if (v.St.Camo.Has("entr") || v.St.Camo.Has("disg")) v.St.Camo.Delete("disg");
        }
        UHash.Query(c.X, c.Z, R + 8, Test);
        BHash.Query(c.X, c.Z, R + 20, Test);
        if (fow) Emit("revealarea", new Args { ["player"] = p, ["x"] = c.X, ["z"] = c.Z, ["r"] = R, ["until"] = Time + dur });
    }

    /// <summary>a camouflage layer on / off (the hook of world.js and combat.js)</summary>
    partial void SetCamo(Unit u, string layer, bool on) => SetCamoOf(u, layer, on);
    /// <summary>a camouflage layer on / off (moves.js setCamo; public for the other classes)</summary>
    public void SetCamoOf(Unit u, string layer, bool on)
    {
        if (on) u.St.Camo.Add(layer); else u.St.Camo.Delete(layer);
        if (layer == "aje")
        {
            u.SetCamoLook(on);
            if (!on) u.Cd.Set("camouflage", Time + 15);    // the cooldown starts when the camouflage ends
        }
        if (on) foreach (var o in Units) if (o.Alive && o.Task.Type == "attack" && o.Task.Target == u && !o.Task.User) o.Task = UnitTask.Idle();
    }

    /// <summary>Entrench.usl: dig in (3 s), hidden and frozen; digs out on a new command or when hit (3 s), then 30 s cooldown</summary>
    public bool DigIn(Unit u)
    {
        if (u.Cd.Get("entrench") > Time || u.St.Camo.Has("entr")) return false;
        ReleaseTask(u); u.Task = new UnitTask("entrench"); u.Path.Clear();
        Moves.Play(u, "digandhide");
        Trap(u, 3, "dig");
        Later(3, "dugIn", new Args { ["u"] = u });
        return true;
    }
    partial void DigOut(Unit u) => DoDigOut(u);
    /// <summary>digOut (the JS's answer: false = it was not dug in)</summary>
    public bool DoDigOut(Unit u)
    {
        if (!u.St.Camo.Has("entr")) return false;
        SetCamoOf(u, "entr", false);
        u.Cd.Set("entrench", Time + 30);
        Moves.Play(u, "hideandstand");
        Trap(u, 3, "dig");
        if (u.Task.Type == "entrench") u.Task = UnitTask.Idle();
        return true;
    }

    /// <summary>jetpack jump to a point up to `max` metres away</summary>
    public bool Jump(Unit u, V3 pos, double max)
    {
        double tx = pos.X, tz = pos.Z;
        double d = Hypot(tx - u.Pos.X, tz - u.Pos.Z);
        if (d > max) { tx = u.Pos.X + (tx - u.Pos.X) * max / d; tz = u.Pos.Z + (tz - u.Pos.Z) * max / d; }
        if (!Nav.IsFree(tx, tz)) { int c = Nav.NearestFree(Nav.Idx(tx, tz), 6); (tx, tz) = Nav.Center(c); }
        Moves.Play(u, "sm_jump_01", "jump");
        u.JumpFx = new JumpFx { From = u.Pos, To = new V3(tx, Height(tx, tz), tz), T = 0, TT = Math.Max(0.8, Math.Min(2, Hypot(tx - u.Pos.X, tz - u.Pos.Z) / 40)) };
        Trap(u, u.JumpFx.TT, "jump");
        return true;
    }

    /// <summary>(CBrachioHarvest, ANMLSpec.usl:480-545: every tree falls away from the brachio)</summary>
    public void FellTreesAround(V3 c, double R)
    {
        RHash.Query(c.X, c.Z, R + 4, n =>
        {
            if (!n.Alive || n.Type != "tree" || Hypot(n.Pos.X - c.X, n.Pos.Z - c.Z) > R) return;
            double a = SMath.Atan2(n.Pos.Z - c.Z, n.Pos.X - c.X);
            FellTree(n, (((int)JsRound(a / (Math.PI / 4)) % 8) + 8) % 8);
        });
    }
    static double JsRound(double x) => Math.Floor(x + 0.5);

    /// <summary>Aje quicksand: 30 s, radius 8, holds every enemy unit that walks in for 5 s (the effect's state is its
    /// host: owner, pos, held)</summary>
    public void SpawnQuicksand(Player owner, V3 p)
    {
        Emit("dust", new Args { ["pos"] = p, ["r"] = 8.0, ["kind"] = "quicksand", ["life"] = 30.0 });   // (the view: the trap model Aje_Quicksand_Trap)
        PeriodicEffect(null, 30, 0.25, "quicksand", new Args { ["owner"] = owner, ["pos"] = p, ["held"] = new OrderedMap<Unit, double>() }, true);
    }

    public bool ActiveState(Entity e, string k) => e.St.Active(k, Time);

    /// <summary>combat.js engage, for the moves (the hook is private to World)</summary>
    internal void EngageUnit(Unit u, Entity e, EngageOpts opts) => Engage(u, e, opts);
    internal void FireUnit(Unit u, Weapon? w, Entity? t) => Fire(u, w, t);
}

// the delayed parts of the moves (World.Later)
static class MovesDelayed
{
    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    [Delayed("bash")]
    static void Bash(World W, Args a)
    {
        var u = a.U("u")!;
        if (!u.Alive) return;
        double R = a.Num("R"), spread = a.Num("spread"), f0 = a.Num("f0"), f1 = a.Num("f1");
        foreach (var v in Moves.EnemiesNear(W, u, u.Pos, R, types: new[] { "CHTR" })) { double dist = f0 + W.Rng.Next() * f1; W.Knockback(v, u.Pos, dist, W.Rng.Next() * spread); }
        double d = a.Has("dmg") ? a.Num("dmg") : (u.Cs() is { } cs ? cs.Dmg : 0);
        W.AreaDamage(u, u.Pos, R, d, a.Has("endDmg") ? a.Num("endDmg") : d, new AreaOpts { Direct = true });
    }

    [Delayed("resurrect")]
    static void Resurrect(World W, Args a)
    {
        var u = a.U("u")!; var s = a.Get<Spirit>("s")!;
        u.BusyAnim = false;
        u.Owner!.QueuedAtLevel[s.Level - 1]--; u.Owner.QueuedUnits--;
        s.Alive = false;
        if (!u.Alive) return;
        var r = W.SpawnUnit(s.Name, s.Owner, s.Pos.X, s.Pos.Z, s.Level);
        if (r != null) W.Emit("trained", new Args { ["unit"] = r, ["producer"] = u });
    }

    [Delayed("stomp")]
    static void Stomp(World W, Args a)
    {
        var u = a.U("u")!;
        if (!u.Alive) return;
        W.FellTreesAround(u.Pos, 15);
        // the original's damage grows with distance: 0 at the centre, 100 at 20 m (kept as in the game)
        foreach (var v in Moves.EnemiesNear(W, u, u.Pos, 20))
        {
            double d = Hypot(v.Pos.X - u.Pos.X, v.Pos.Z - u.Pos.Z);
            W.TakeDirectDmg(v, 100 * d / 20, 0, u.Owner, false);
            if ((v.Stats?.Size ?? 0) <= 7) W.Knockback(v, u.Pos, 3, 0);
        }
    }

    [Delayed("roar")]
    static void Roar(World W, Args a) { var u = a.U("u")!; foreach (var v in Moves.EnemiesNear(W, u, u.Pos, u.Radius + 10)) W.Trap(v, 7, "roar"); }

    [Delayed("lockpick")]
    static void Lockpick(World W, Args a) { var u = a.U("u")!; var g = a.B("g")!; u.BusyAnim = false; if (u.Alive && g.Alive) W.OpenViolently(g); }

    // (the JS drew a glow 20 .. 40 m above the unit; the view shows the game's model ninigi_fireworks at the first one)
    [Delayed("firework")]
    static void Firework(World W, Args a)
    {
        var u = a.U("u")!; int i = a.Int("i");
        W.Emit("glow", new Args { ["kind"] = "firework", ["unit"] = u, ["i"] = (double)i, ["pos"] = u.Pos, ["size"] = 6.0, ["size1"] = 16.0, ["life"] = 1.2, ["color"] = new[] { 0xff5030, 0x40ff60, 0x4080ff }[i % 3] });
    }

    [Delayed("burstArrow")]
    static void BurstArrow(World W, Args a)
    {
        var u = a.U("u")!; var e = a.E("e")!; var w = a.Get<Weapon>("w");
        if (!u.Alive || !e.Alive || w == null) return;
        var cs = u.Cs(w)!;
        W.AreaDamage(u, e.Pos, 5 + e.Radius, cs.Dmg, cs.EndDmg, new AreaOpts { Size = w.Size });    // CBurstArrow: CAreaDamage(fighter) - its damage down to its enddamage at the edge
        W.Emit("impact", new Args { ["pos"] = e.Pos, ["weapon"] = w, ["splash"] = true });
    }

    [Delayed("multishot")]
    static void Multishot(World W, Args a) { var u = a.U("u")!; var e = a.E("e")!; if (u.Alive && e.Alive) { W.Penetrate(u, e, u.Cs()!, 10); W.FireUnit(u, u.Weapons.Long, e); } }

    [Delayed("lacerate")]
    static void Lacerate(World W, Args a)
    {
        var u = a.U("u")!; var e = a.E("e")!;
        if (!u.Alive) return;
        double dir = SMath.Atan2(e.Pos.Z - u.Pos.Z, e.Pos.X - u.Pos.X);
        foreach (var v in Moves.EnemiesNear(W, u, u.Pos, u.Radius + 4, buildings: true))
        {
            double an = SMath.Atan2(v.Pos.Z - u.Pos.Z, v.Pos.X - u.Pos.X) - dir;
            if (Math.Abs(SMath.Atan2(SMath.Sin(an), SMath.Cos(an))) <= Math.PI / 3) W.TakeDirectDmg(v, 200, 0, u.Owner, false);
        }
    }

    // Cole's shotgun when its clip is over: the target and the bystanders taken when he fired, then the target turns on him
    [Delayed("shotgunHit")]
    static void ShotgunHit(World W, Args a)
    {
        var u = a.U("u")!; var t = a.E("t");
        if (t != null && t.Alive) W.TakeDirectDmg(t, Moves.Pct(t, t.Kind == "building" ? 0.1 : 0.5, 200, 2000), 0, u.Owner, true, u);
        foreach (var v in a.Get<List<Entity>>("near")!)
            if (v.Alive && !v.Inside) W.TakeDirectDmg(v, Moves.Pct(v, v.Kind == "building" ? 0.1 : 0.5, 200, 2000), 0, u.Owner, true, u);
        if (t is Unit tu && tu.Alive && u.Alive) W.EngageUnit(tu, u, new EngageOpts { Defend = true });
    }

    // Tesla's wrench when "potter" is over: the vehicle is wrecked
    [Delayed("destroyVhcl")]
    static void DestroyVhcl(World W, Args a) { var u = a.U("u")!; var t = a.E("t"); if (t != null && t.Alive) W.TakeDirectDmg(t, 999999.9, 0, u.Owner, false, u); }

    // the hermit after "heal_0" x5: every own animal within 40 m healed fully
    [Delayed("healAnml")]
    static void HealAnml(World W, Args a)
    {
        var u = a.U("u")!;
        W.UHash.Query(u.Pos.X, u.Pos.Z, 46, o => { if (o.Alive && o.Owner == u.Owner && o.Cls == "ANML" && u.DistTo(o) <= 40) o.Hp = o.MaxHp; });
    }

    // the mammoth's trumpet / the titan triceratops' pawing after their clip: enemies within 30 m deal x0.8 for 15 s
    [Delayed("trumpet")]
    static void Trumpet(World W, Args a) { var u = a.U("u")!; foreach (var v in Moves.EnemiesNear(W, u, u.Pos, 30)) v.St.T["trumpet"] = W.Time + 15; }
    [Delayed("paw")]
    static void Paw(World W, Args a) { var u = a.U("u")!; foreach (var v in Moves.EnemiesNear(W, u, u.Pos, 30)) v.St.T["paw"] = W.Time + 15; }

    // a burst of Babbage's minigun (8 while "babbage_minigun" runs): 30° cone up to 35 m, each hit 30 % of the victim's
    // hit points (150..2000) / 8
    [Delayed("minigun")]
    static void Minigun(World W, Args a)
    {
        var u = a.U("u")!; var c = a.V("c");
        if (!u.Alive) return;
        double dir = SMath.Atan2(c.Z - u.Pos.Z, c.X - u.Pos.X);
        foreach (var v in Moves.EnemiesNear(W, u, u.Pos, 35, buildings: true))
        {
            double an = SMath.Atan2(v.Pos.Z - u.Pos.Z, v.Pos.X - u.Pos.X) - dir;
            if (Math.Abs(SMath.Atan2(SMath.Sin(an), SMath.Cos(an))) <= Math.PI / 12) W.TakeDirectDmg(v, Moves.Pct(v, 0.3, 150, 2000) * 0.125, 0, u.Owner, true, u);
        }
    }

    // a special arrow leaves (CArrow START_TIMER -> Shoot: ValidateObjects) and flies like the shooter's shots
    [Delayed("specialShot")]
    static void SpecialShotGo(World W, Args a)
    {
        var u = a.U("u")!; var t = a.E("t"); var hit = a.Str("hit")!;
        if (!u.Alive || t == null || !t.Alive) return;
        var w = u.Weapons.Long;
        var cs = w != null ? u.Cs(w) : null;
        if (w == null || cs == null || w.Projectile == null) { Delayed.DELAYED[hit](W, a); return; }
        var from = u.Pos; from.Y += Math.Min(u.Height * 0.7, 2.5);
        if (W.Muzzle(u) is V3 m) from = m;
        var p = W.Launch(u, from, t, w, cs, 0, false);
        p.Impact = hit; p.ImpactArgs = a;
    }

    [Delayed("snipershot")]
    static void Snipershot(World W, Args a) { var u = a.U("u")!; var t = a.E("t"); if (t != null && t.Alive) W.TakeDirectDmg(t, Moves.Pct(t, 0.5, 500, 2000), 0, u.Owner, true, u); }

    [Delayed("deathShot")]
    static void DeathShot(World W, Args a) { var u = a.U("u")!; var t = a.E("t"); if (t != null && t.Alive) W.TakeDirectDmg(t, Moves.Pct(t, 0.9, 1500, 5500), u.Cs() is { } cs ? cs.Ap : 0, u.Owner, true, u); }

    [Delayed("sacrifice")]
    static void Sacrifice(World W, Args a)
    {
        var u = a.U("u")!; var t = a.E("t")!;
        if (!u.Alive || !t.Alive) return;
        u.Hp = Math.Min(u.MaxHp, u.Hp + t.Hp);
        W.Kill(t, null);
        W.LocalFilter(u, "Special/Upgrades/schliemann_s0/Sacrifice_Bonus", true);
        W.Later(10, "localFilterOff", new Args { ["u"] = u, ["path"] = "Special/Upgrades/schliemann_s0/Sacrifice_Bonus" });
    }

    [Delayed("localFilterOff")]
    static void LocalFilterOff(World W, Args a) => W.LocalFilter(a.E("u")!, a.Str("path")!, false);

    [Delayed("unvanish")]
    static void Unvanish(World W, Args a) => W.SetCamoOf(a.U("u")!, "hero", false);

    [Delayed("hypnosis")]
    static void Hypnosis(World W, Args a)
    {
        var t = a.U("t");
        if (t == null || !t.Alive) return;
        W.Trap(t, 20, "hypnosis");
        var r = t.Anim!.Pick("rest"); if (r != null) t.Anim.Play(r);
    }

    [Delayed("dugIn")]
    static void DugIn(World W, Args a) { var u = a.U("u")!; if (u.Alive && u.Task.Type == "entrench") W.SetCamoOf(u, "entr", true); }

    // ------------------------------------------------------------------ the repeating effects of the moves (World.PeriodicEffect)
    // every second the building's owner loses 20 food, for 180 s
    [Periodic("insects")]
    static bool Insects(World W, PeriodicEffect f)
    {
        var b = f.Host!;
        if (b.Owner == null) return false;
        b.Owner.Res["food"] = Math.Max(0, b.Owner.Res["food"] - 20);
        return b.Alive && b.Owner.Res["food"] > 0;
    }

    // Animal.usl RageTo (every 2 s for 30 s): area damage 7 m around the mammoth, and the characters within 7 m thrown
    // 10 m to the side they are on (SetHitReactionTimer: unless something else already throws them); the stampede ends
    // with the mammoth's first real attack (AttackEnemy -> EndRageTo)
    [Periodic("stampede")]
    static bool Stampede(World W, PeriodicEffect f)
    {
        var u = (Unit)f.Host!;
        if (!u.Alive || u.RageHit) { u.RageHit = false; return false; }
        double d = u.Cs() is { } cs ? cs.Dmg : 0;
        W.AreaDamage(u, u.Pos, 7, d, 10, new AreaOpts { Direct = true, Size = u.Stats!.Size });
        double hx = -SMath.Sin(u.Heading), hz = -SMath.Cos(u.Heading);
        foreach (var e in Moves.EnemiesNear(W, u, u.Pos, 7))
        {
            if (e is not Unit v || v.Cls != "CHTR" || v.Knock != null || !v.Alive || v.Inside) continue;
            double side = Math.Sign(hx * (v.Pos.Z - u.Pos.Z) - hz * (v.Pos.X - u.Pos.X)); if (side == 0) side = 1;
            W.Later(0, "knock", new Args { ["V"] = v, ["vx"] = -hz * side * 10 / 0.4, ["vz"] = hx * side * 10 / 0.4 });
        }
        return true;
    }
    [PeriodicEnd("stampede")]
    static void StampedeEnd(World W, PeriodicEffect f) { ((Unit)f.Host!).RageT = 0; }

    [Periodic("termites")]
    static bool Termites(World W, PeriodicEffect f) { var b = f.Host!; if (!b.Alive) return false; W.TakeDirectDmg(b, 25, 0, f.S.P("by"), false); return true; }

    [Periodic("tornado")]
    static bool Tornado(World W, PeriodicEffect f)
    {
        var u = f.Host!; var s = f.S;
        foreach (var v in s.Get<List<Entity>>("victims")!) if (v.Alive) W.TakeDirectDmg(v, v.Kind == "unit" ? Math.Max(30, Math.Min(200, v.Hp * 0.05)) : Math.Max(30, Math.Min(200, v.Hp * 0.025)), 0, u.Owner, false);
        W.Emit("dust", new Args { ["pos"] = s.V("p"), ["r"] = 6.0, ["kind"] = "tornado" });   // (the view: the model Aje_Tornado while it lasts)
        double n = s.Num("n") + 1; s["n"] = n;
        return n < 11;
    }

    // Aje quicksand: holds every enemy unit that walks in for 5 s (the effect's state: {owner, pos, held: unit -> time})
    [Periodic("quicksand")]
    static bool Quicksand(World W, PeriodicEffect f)
    {
        var q = f.S; var p = q.V("pos"); var owner = q.P("owner")!; var held = q.Get<OrderedMap<Unit, double>>("held")!;
        W.UHash.Query(p.X, p.Z, 12, v =>
        {
            if (!v.Alive || v.Inside || v.Owner == null || !owner.IsEnemy(v.Owner) || held.Has(v)) return;
            if (Hypot(v.Pos.X - p.X, v.Pos.Z - p.Z) > 8) return;
            held.Set(v, W.Time); W.Trap(v, 5, "quicksand");
        });
        return true;
    }
}
