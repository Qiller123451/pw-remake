// Commands of the HUD that src/Sim/Commands does not have yet: the JS interface did these directly on the world
// (hud.js: u.stance = n; G.world.unloadAll(bunker); W.unloadOne(t, p)). As commands they reach every machine of a game
// like the others (docs/ARCHITECTURE.md "Commands"). They belong in src/Sim/Commands - kept here until they move.
using System;
using PwRemake.Sim.Commands;
using PwRemake.Sim.World;

namespace PwRemake.UI.Hud.Logic;

/// <summary>The stance of units (the stance cards: FightingObj.m_iAggressionState 0 aggressive, 1 defensive, 2 passive).</summary>
public sealed class SetStanceCommand : Command
{
    public int[] Units = Array.Empty<int>();
    public int Stance;
    public override string? Apply(World w)
    {
        bool any = false;
        foreach (var id in Units)
            if (w.ById(id) is Unit u && u.Alive && u.Owner != null && u.Owner.Id == Player) { u.Stance = Stance; any = true; }
        return any ? null : "none";
    }
}

/// <summary>Passengers out of a transport or a bunker: all of them (Passenger 0: the bunker's dismount button,
/// world.unloadAll(t, false)) or one (the passengers' menu, world.unloadOne).</summary>
public sealed class UnloadCommand : Command
{
    public int Transport;
    public int Passenger;
    public override string? Apply(World w)
    {
        if (w.ById(Transport) is not { Alive: true } t || t.Owner == null || t.Owner.Id != Player) return "none";
        if (Passenger == 0) { w.UnloadAll(t, false); return null; }
        return w.ById(Passenger) is Unit u && w.UnloadOne(t, u) ? null : "none";
    }
}
