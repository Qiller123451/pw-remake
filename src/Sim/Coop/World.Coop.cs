// The co-op campaign's fields on the world's own types (docs/COOP.md): a player's enemy scaling, the hero rule of the
// seats. Plain fields, set by CoopRuntime; 1 / null in every other game.
using System;

namespace PwRemake.Sim.World;

public sealed partial class Player
{
    /// <summary>co-op scaling: the damage this player's objects deal is multiplied by CoopDamage, the damage they take is
    /// divided by CoopHealth (Combat.Damage; 1 = the original)</summary>
    public double CoopDamage = 1, CoopHealth = 1;
    /// <summary>the population limit before the co-op scaling raised it (null: not raised)</summary>
    public double? CoopPopBase;
}

public sealed partial class World
{
    /// <summary>co-op: may player p not build the unique hero class? (another seat has or trains it; an AI commander
    /// builds no heroes) - Production's "unique" check; null outside co-op</summary>
    public Func<Player, string, bool>? CoopHeroBlocked;
}
