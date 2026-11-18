// The co-op campaign's commands (docs/COOP.md): plain data like every command (CommandCodec), so they travel in the
// lockstep turns, are recorded in saved games and replays. Player = the human seat that gives it (the host checks that
// a player gives commands for its own player only - the giver is that player).
using System;
using System.Linq;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Commands;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Coop;

static class CoopCmd
{
    public static CoopRuntime? Of(W_ w) => (w.Campaign as Campaign.Campaign)?.Coop;
}

/// <summary>A general command to an AI commander seat (the co-op command panel): Kind = attack | defend | follow |
/// economy | retreat | none | aggression; X / Z the point (HasPoint), Target an entity (attack, defend at it, follow it),
/// Units the units to follow, Value the aggression (0 defensive, 1 normal, 2 aggressive).
/// Seats: one command to several AI commanders at once (the panel's "Both AI commanders"): the seat numbers it goes to,
/// carried out by each in the order given, in the same step; empty = Seat alone. One command, not one per seat: it
/// travels, is saved and replays as one, so the commanders can never get it in different turns (a save of an older
/// version, without the field, reads as empty).</summary>
public sealed class CoopCommand : Command
{
    public int Seat;
    public int[] Seats = Array.Empty<int>();
    public string Kind = "none";
    public bool HasPoint;
    public double X, Z;
    public int Target;
    public int[] Units = Array.Empty<int>();
    public int Value;

    /// <summary>the seat numbers it goes to (Seats, each once, in its order; else Seat)</summary>
    public int[] To() => Seats is { Length: > 0 } ? Seats.Distinct().ToArray() : new[] { Seat };

    public override string? Apply(W_ w)
    {
        var co = CoopCmd.Of(w);
        if (co == null) return "coop";
        var from = co.SeatOfSlot(Player);
        if (from == null || from.Ai) return "seat";               // (only a human seat commands)
        var to = To().Select(co.SeatNo).ToList();
        if (to.Count == 0 || to.Any(s => s?.Commander == null)) return "commander";   // (all or none: every seat an AI commander)
        var t = Target != 0 ? w.ById(Target) : null;
        var us = Units.Select(w.ById).Where(e => e != null && e.Alive).Cast<Entity>().ToList();
        // each carries it out; the first refusal is the answer (the checks are the same for every commander: the target,
        // the units - one refuses, all refuse)
        string? why = null;
        foreach (var s in to) { var r = s!.Commander!.Command(from.Seat, Kind, HasPoint ? new Pt(X, Z) : null, t, us, Value); why ??= r; }
        return why;
    }
}

/// <summary>Units given to another co-op seat (the HUD's "give to"): only the giver's own units, never buildings, heroes
/// only between humans.</summary>
public sealed class CoopGiveCommand : Command
{
    public int[] Units = Array.Empty<int>();
    /// <summary>the seat number (1..3) that gets them</summary>
    public int ToSeat;

    public override string? Apply(W_ w)
    {
        var co = CoopCmd.Of(w);
        if (co == null) return "coop";
        var from = co.SeatOfSlot(Player); var to = co.SeatNo(ToSeat);
        if (from == null || to == null || from.Ai) return "seat";
        var ents = Units.Select(w.ById).Where(e => e != null).Cast<Entity>().ToList();
        var (n, why) = co.Give(from.Player, ents, to.Player);
        if (n > 0) w.Emit("msg", new Args { ["player"] = to.Player, ["text"] = $"{from.Player.Name} gave you {n} unit{(n == 1 ? "" : "s")}", ["kind"] = "good" });
        return why;
    }
}
