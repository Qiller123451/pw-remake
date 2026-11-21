// An indestructible object (the remake's): a building or unit a map puts down that nothing can damage, target, sell or
// blow up - the "Outbreak" mission's old Norse walls round the base, owned by nobody (docs/OUTBREAK.md). The map marks
// it with the object attribute `indestructible = "1"` (world.lua; docs/MAP_FORMAT.md "Indestructible objects"); a
// building of nobody with it stands as a building (Campaign.Plan), otherwise only gates of nobody do. A mission's own
// script can still remove it (api.objects.remove) or kill it (api.units.kill: World.Kill is not stopped).
//   * Combat.Invulnerable: no damage;  Untargetable: not attacked, not chosen as a target;
//   * World.SelfDestruct (the owner's Del / the AI's demolition): refused.
// Part of the world's state (World.StateHash mixes it in while set).
namespace PwRemake.Sim.World;

public abstract partial class Entity
{
    /// <summary>nothing damages, targets, sells or blows it up (Indestructible.cs)</summary>
    public bool Indestructible;
}

public partial class World
{
    /// <summary>makes an object indestructible (or not): invulnerable and untargetable for good</summary>
    public void SetIndestructible(Entity e, bool on)
    {
        if (e == null) return;
        e.Indestructible = on;
        e.Untargetable = on;
        SetInvulnerable(e, on);
    }
}
