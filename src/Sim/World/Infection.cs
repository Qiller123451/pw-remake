// The infected stage of a building or a unit (the remake's: the "Outbreak" mission, docs/OUTBREAK.md): 0 healthy, 1
// freshly infected, 2 spreading, 3 fully overrun. The simulation only keeps the number - a mission sets it
// (Lua api.objects.infect) and reads it back; the view draws it (ModelInstance.SetInfected, docs/INFECTED_LOOK.md).
// Part of the world's state like any other field: a saved game (a replay) sets it again at the same step.
using System;

namespace PwRemake.Sim.World;

public abstract partial class Entity
{
    /// <summary>the infected stage drawn (0 .. 3); the view follows it (EntityViews)</summary>
    public int Infection;
}

public partial class World
{
    /// <summary>sets an object's infected stage (0 .. 3); emits "infected" when it changed</summary>
    public bool SetInfection(Entity e, int stage)
    {
        stage = Math.Clamp(stage, 0, 3);
        if (e == null || !e.Alive || e.Infection == stage) return false;
        e.Infection = stage;
        Emit("infected", new Args { ["entity"] = e, ["stage"] = stage });
        return true;
    }

    /// <summary>
    /// A unit born of an infected object takes its stage: what an infected building trains or breeds (the kennel's
    /// sabre-tooths, a nest's young, a launch pad's titan), an infected unit's copies (illusions, the twins) and what it
    /// turns into (Babbage's suit and back), a hatchling of an infected catapult's egg. Kacper 2026-11-20 (Outbreak):
    /// "Kennel Eusmilus do not have skin" - the infected kennel's guards came out healthy (the mission infects only the
    /// units it spawns itself).
    /// </summary>
    public void InheritInfection(Entity? from, Entity? born)
    {
        if (from == null || born == null || from.Infection <= 0) return;
        SetInfection(born, from.Infection);
    }
}
