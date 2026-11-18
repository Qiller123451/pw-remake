// Everything a player does, as data (docs/ARCHITECTURE.md "Commands"): the seam for co-op, multiplayer and replays.
// The interface never calls the world's methods for an order directly; it submits a command to the session's
// ICommandSink. The world applies the commands due at the start of a step (World.Step), in the order they were
// submitted. Commands refer to entities by id (Entity.Id), so they can be sent over a network or written to a replay.
//
// The orders of the JS remake's interface (main.js G.order, G.useAction, G.placeBuilding, G.placeWall, G.changeLevel,
// G.levelUp, G.killSelected, ui/input.js moveGroup, rally points, tower aims, harbour moves) are the concrete
// commands in Commands.cs.
using System;
using System.Collections.Generic;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Commands;

/// <summary>A player's action, applied by World at the start of step Step.</summary>
public abstract class Command
{
    /// <summary>the player who gives it (Player.Id)</summary>
    public int Player;
    /// <summary>the step it applies at (0: the next step)</summary>
    public long Step;
    /// <summary>order of submission within a step (set by the world)</summary>
    public long Seq;

    /// <summary>carries the command out; returns null, or why it could not be done (a reason code for the interface)</summary>
    public abstract string? Apply(World.World w);

    public override string ToString() => $"{GetType().Name} p{Player} @{Step}";
}

/// <summary>Where the interface sends commands.</summary>
public interface ICommandSink
{
    void Submit(Command c);
}

/// <summary>Single player: the command is applied at the next step of the local world.</summary>
public sealed class LocalCommandSink : ICommandSink
{
    readonly World.World _w;
    public LocalCommandSink(World.World w) { _w = w; }
    public void Submit(Command c) => _w.Enqueue(c);
}

/// <summary>What a command did (World.CommandResults, read by the interface for its messages).</summary>
public readonly record struct CommandResult(Command Command, string? Reason);
