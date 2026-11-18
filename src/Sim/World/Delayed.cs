// Delayed calls and repeating effects of the simulation are data - a kind and its arguments - so that a saved game
// carries them (game/save.js). The systems register their kinds here:
//   DELAYED[kind](world, args)                        World.Later(sec, kind, args)
//   PERIODIC[kind] = {Tick(world, f) -> false: stop,  World.PeriodicEffect(host, dur, every, kind, s, now);
//                     End(world, f)}                   f = {Host, End, Every, Next, Kind, S}
//   JOBS[kind](engine, args, trigger)                 the campaign's TriggerEngine.after(sec, kind, args, trigger)
//   ANIM_DONE[kind](world, entity, args)              what happens when a clip played once ends (AnimCtl onDone)
//
// Ported from remake/src/game/delayed.js (pw-exporter 98c7703).
//
// C# notes: the JS systems fill the tables with Object.assign(DELAYED, {...}). Here a handler is a static method with
// an attribute, found once in the assembly:
//   [Delayed("hit")]        static void Hit(World W, Args a) { ... }
//   [Periodic("insects")]   static bool Insects(World W, PeriodicEffect f) { ... }        (false: stop)
//   [PeriodicEnd("insects")] static void InsectsEnd(World W, PeriodicEffect f) { ... }    (optional)
//   [AnimDone("busy")]      static void Busy(World W, Entity e, Args? a) { ... }
//   [Job("spawnWave")]      static void SpawnWave(object engine, Args a, object? trigger) { ... }
// The onDone of AnimCtl.Play (the JS passes a closure) is an AnimDone kind with its arguments for the same reason:
// the animation state is part of the world state.
using System;
using System.Collections.Generic;
using System.Reflection;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public sealed class DelayedAttribute : Attribute { public readonly string Kind; public DelayedAttribute(string kind) { Kind = kind; } }
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public sealed class PeriodicAttribute : Attribute { public readonly string Kind; public PeriodicAttribute(string kind) { Kind = kind; } }
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public sealed class PeriodicEndAttribute : Attribute { public readonly string Kind; public PeriodicEndAttribute(string kind) { Kind = kind; } }
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public sealed class AnimDoneAttribute : Attribute { public readonly string Kind; public AnimDoneAttribute(string kind) { Kind = kind; } }
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public sealed class JobAttribute : Attribute { public readonly string Kind; public JobAttribute(string kind) { Kind = kind; } }

/// <summary>A delayed call: at game time T run DELAYED[Kind](world, Args).</summary>
public sealed class Timer
{
    public double T;
    public string Kind = "";
    public Args Args = new();
    public override string ToString() => $"{T:0.00} {Kind}";
}

/// <summary>A repeating effect (World.PeriodicEffect): Tick every Every s from Next until End.</summary>
public sealed class PeriodicEffect
{
    public Entity? Host;
    public double End, Every, Next;
    public string Kind = "";
    /// <summary>its state (s)</summary>
    public Args S = new();
}

/// <summary>What happens when a clip played once ends: an ANIM_DONE kind and its arguments.</summary>
public sealed record AnimDone(string Kind, Args? Args = null);

public static class Delayed
{
    static readonly Logger L = Log.For("World.Delayed");

    public static readonly Dictionary<string, Action<World, Args>> DELAYED = new(StringComparer.Ordinal);
    public static readonly Dictionary<string, Func<World, PeriodicEffect, bool>> PERIODIC = new(StringComparer.Ordinal);
    public static readonly Dictionary<string, Action<World, PeriodicEffect>> PERIODIC_END = new(StringComparer.Ordinal);
    public static readonly Dictionary<string, Action<World, Entity, Args?>> ANIM_DONE = new(StringComparer.Ordinal);
    public static readonly Dictionary<string, Action<object, Args, object?>> JOBS = new(StringComparer.Ordinal);

    static bool s_scanned;
    static readonly object s_lock = new();

    /// <summary>finds the handlers of the assemblies loaded (once; the simulation's own assembly always)</summary>
    public static void EnsureRegistered()
    {
        if (s_scanned) return;
        lock (s_lock)
        {
            if (s_scanned) return;
            Scan(typeof(Delayed).Assembly);
            s_scanned = true;
        }
    }

    /// <summary>registers the handlers of another assembly (a mod, the game project)</summary>
    public static void Scan(Assembly asm)
    {
        Type[] types;
        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null)!; }
        foreach (var t in types)
            foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                foreach (var a in m.GetCustomAttributes<DelayedAttribute>()) Add(DELAYED, a.Kind, m.CreateDelegate<Action<World, Args>>(), m);
                foreach (var a in m.GetCustomAttributes<PeriodicAttribute>()) Add(PERIODIC, a.Kind, m.CreateDelegate<Func<World, PeriodicEffect, bool>>(), m);
                foreach (var a in m.GetCustomAttributes<PeriodicEndAttribute>()) Add(PERIODIC_END, a.Kind, m.CreateDelegate<Action<World, PeriodicEffect>>(), m);
                foreach (var a in m.GetCustomAttributes<AnimDoneAttribute>()) Add(ANIM_DONE, a.Kind, m.CreateDelegate<Action<World, Entity, Args?>>(), m);
                foreach (var a in m.GetCustomAttributes<JobAttribute>()) Add(JOBS, a.Kind, m.CreateDelegate<Action<object, Args, object?>>(), m);
            }
    }

    static void Add<T>(Dictionary<string, T> table, string kind, T fn, MethodInfo m)
    {
        if (table.ContainsKey(kind)) L.Warn($"{kind}: registered twice ({m.DeclaringType?.Name}.{m.Name})");
        table[kind] = fn;
    }

    // ------------------------------------------------------------------ the kinds of the world core
    // the end of a threat or a victory animation (World.AlarmUnit / VictoryAnim; JS: systems/combat.js DELAYED.animDone)
    [Delayed("animDone")]
    static void AnimDoneDelayed(World W, Args a)
    {
        var u = a.U("u");
        if (u?.Anim != null && u.Alive && u.Anim.CurName == a.Str("name")) { u.BusyAnim = false; u.Anim.Play(u.IdleAnim); }
    }

    // illusions, tracker dinos (JS: systems/moves.js DELAYED.expire)
    [Delayed("expire")]
    static void Expire(World W, Args a) { var u = a.U("u"); if (u != null && u.Alive) W.Kill(u, null); }

    // an action clip played once ended: the unit is free again (the JS closures `() => { u.busyAnim = false; }`)
    [AnimDone("busy")]
    static void Busy(World W, Entity e, Args? a) { if (e is Unit u) u.BusyAnim = false; }

    // a building's clip played once ended: back to its idle clip (Building.idleAnim; work_finished, spawn)
    [AnimDone("idle")]
    static void Idle(World W, Entity e, Args? a) { if (e is Building b) b.IdleAnim(); }
}
