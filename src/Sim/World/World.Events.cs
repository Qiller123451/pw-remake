// Events of the world: what the player should hear or see and what the campaign's triggers wait for
// (world.js emit: `world.emit('built', { building })` -> W.Emit("built", new Args { ["building"] = b })).
//
// The events of every step collect in World.Events; the view and the interface drain them once per frame
// (main.js handleEvents -> ui/feedback.js: sounds, messages, the minimap, the grass under new buildings), the
// campaign reads the events of the step it runs in (EventsSince). Every event also passes the game's statistics
// (StatsOn, game/stats.js).
//
// Readers inside the simulation (the campaign, the computer players' defence) keep an ABSOLUTE position
// (EventBase + index): when the view drained the list at the end of a drawn frame, what they had not read yet was
// lost - after one step or after eight, as the frames fell, so the same game went differently on another machine and
// in a replay (docs/SAVES.md). The game therefore turns on ViewEvents (the view gets its own copy of every event:
// DrainEvents empties only that copy) and TrimEvents (the world itself drops, at the start of each step, the events
// older than the step before: every reader has seen them by then). Without both (tests, headless runs) DrainEvents
// empties World.Events as before. Types and their fields (Args keys) as in the JS: built {building}, died {entity, killer},
// hit {attacker, victim}, trained {unit, producer}, msg {player, text, key}, ground {x, z, r}, levelup {unit},
// victory {player}, owner {entity}, removed {entity}, animSound {entity, alts, pos} ...
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public sealed class WorldEvent
{
    public readonly string Type;
    public readonly Args Data;
    /// <summary>the step and game time it happened at</summary>
    public long Step;
    public double Time;

    public WorldEvent(string type, Args? data = null) { Type = type; Data = data ?? new Args(); }

    public object? this[string key] => Data[key];
    public Entity? E(string key) => Data.E(key);
    public Unit? U(string key) => Data.U(key);
    public Building? B(string key) => Data.B(key);
    public Player? P(string key) => Data.P(key);
    public string? Str(string key) => Data.Str(key);
    public double Num(string key, double d = 0) => Data.Num(key, d);
    public override string ToString() => $"{Type} {{{Data}}}";
}

public sealed partial class World
{
    static readonly Logger LE = Log.For("World.Events");
    /// <summary>the events not drained yet (in the order they happened)</summary>
    public readonly List<WorldEvent> Events = new();
    /// <summary>how many events have left the front of Events so far: Events[i] is event number EventBase + i (the
    /// readers of the simulation keep such absolute numbers)</summary>
    public long EventBase;
    /// <summary>events kept at most when nobody drains them (a headless run): the oldest go</summary>
    public int EventLimit = 200000;
    /// <summary>the view gets its own copy of every event (DrainEvents takes that; turned on, it starts with the events
    /// not drained yet); World.Events is then the simulation's alone (the game: docs/SAVES.md)</summary>
    public bool ViewEvents
    {
        get => _outbox != null;
        set { if (value && _outbox == null) _outbox = new List<WorldEvent>(Events); else if (!value) _outbox = null; }
    }
    List<WorldEvent>? _outbox;
    /// <summary>at the start of each step the events older than the step before leave Events (every reader of the
    /// simulation has seen them): the list stays short without a drain, at the same moments on every machine</summary>
    public bool TrimEvents;

    public WorldEvent Emit(string type, Args? data = null) => Emit(new WorldEvent(type, data));

    public WorldEvent Emit(WorldEvent e)
    {
        e.Step = StepNo; e.Time = Time;
        if (Events.Count >= EventLimit) { LE.WarnOnce("limit", $"more than {EventLimit} events not drained: the oldest are dropped"); int n = Events.Count / 2; Events.RemoveRange(0, n); EventBase += n; }
        Events.Add(e);
        if (_outbox != null) { if (_outbox.Count >= EventLimit) _outbox.RemoveRange(0, _outbox.Count / 2); _outbox.Add(e); }
        StatsOn(e);
        return e;
    }

    /// <summary>the events since index i of Events (the campaign keeps its read position)</summary>
    public IEnumerable<WorldEvent> EventsSince(int i) { for (int k = Math.Max(0, i); k < Events.Count; k++) yield return Events[k]; }

    /// <summary>the index in Events of absolute event number n (0 when it has left the list: what is left is read)</summary>
    public int EventIndex(long n) { long i = n - EventBase; return i < 0 ? 0 : i > Events.Count ? Events.Count : (int)i; }

    /// <summary>takes all events (the view / interface, once per frame): with ViewEvents the view's copy, else
    /// World.Events itself</summary>
    public List<WorldEvent> DrainEvents()
    {
        if (_outbox != null)
        {
            var o = new List<WorldEvent>(_outbox);
            _outbox.Clear();
            return o;
        }
        var l = new List<WorldEvent>(Events);
        EventBase += Events.Count;
        Events.Clear();
        EventsDrained?.Invoke();
        return l;
    }

    /// <summary>TrimEvents: the start of a step drops the events of the steps before the last one</summary>
    void TrimOldEvents()
    {
        int n = 0;
        while (n < Events.Count && Events[n].Step < StepNo - 1) n++;
        if (n == 0) return;
        Events.RemoveRange(0, n);
        EventBase += n;
    }

    /// <summary>the event list was emptied (the campaign resets its read position: G.campaign.evAt = 0)</summary>
    public event Action? EventsDrained;
}
