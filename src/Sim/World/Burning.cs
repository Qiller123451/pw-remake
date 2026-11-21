// A unit set on fire by a mission (the remake's: the "Outbreak" mission's infected burn in the sunlight, docs/OUTBREAK.md
// "Daylight"): it burns for a number of seconds - its hit points run down evenly from what it had to nothing, whatever
// its armour, invulnerability or healing - and dies when the time is up. The view shows the original's fire effect on
// it while it burns (GameFx.Status: the molotov fire, fire_molotov). Lua: api.units.burn(id, seconds).
// Part of the world's state (World.StateHash mixes it in while set): a saved game (a replay) sets it again at the same
// step.
using System;

namespace PwRemake.Sim.World;

public sealed partial class Unit
{
    /// <summary>burning since BurnFrom until BurnUntil (game time; 0 = not burning), from BurnHp hit points</summary>
    public double BurnFrom, BurnUntil, BurnHp;
    public bool IsBurning => BurnUntil > 0 && Alive;
}

public partial class World
{
    /// <summary>sets a unit on fire for `seconds` (at least one step): it burns to death. A unit already burning keeps
    /// the earlier end.</summary>
    public bool Ignite(Unit u, double seconds)
    {
        if (u == null || !u.Alive || u.BurnUntil > 0) return false;
        u.BurnFrom = Time;
        u.BurnUntil = Time + Math.Max(STEP, seconds);
        u.BurnHp = Math.Max(1, u.Hp);
        Emit("burning", new Args { ["entity"] = u, ["until"] = u.BurnUntil });
        return true;
    }

    /// <summary>a step of a burning unit: the hit points fall evenly to 0 at BurnUntil, then it dies</summary>
    void BurnUpdate(Unit u)
    {
        if (Time >= u.BurnUntil - 1e-9) { Kill(u, null); return; }
        double left = (u.BurnUntil - Time) / (u.BurnUntil - u.BurnFrom);
        double hp = Math.Max(1, Math.Floor(u.BurnHp * left));
        if (hp < u.Hp) u.Hp = hp;
    }
}
