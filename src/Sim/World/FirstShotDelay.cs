// The original's first-shot delay (World.FirstShot, Combat.cs FirstShotUnit / AttackUpdate; docs/SIMULATION.md "Setting
// up and fight positions"): a ranged character whose weapon is not up yet adds 0.4 s (Bela 0.6 s) to its first shot -
// to the weapon's duration and to the projectile's delay (character.usl CCharacter.AttackEnemy, m_fWeaponDurationOvertime).
// The debug option "First-shot delay" (DebugOptions.FirstShot / FirstShotDelay, docs/DEBUG.md) switches it off or sets
// one time for every unit; Cheats.Apply puts it here at the start of a game.
using System;

namespace PwRemake.Sim.World;

public sealed class FirstShotDelay
{
    /// <summary>the original's times (character.usl: Bela_s0 0.6, every other IsAnimLoopUnit 0.4)</summary>
    public const double ORIGINAL = 0.4, ORIGINAL_BELA = 0.6;

    /// <summary>off: no extra time on any shot</summary>
    public bool On = true;
    /// <summary>one time for every unit (seconds); null = the original's per class</summary>
    public double? Seconds;

    /// <summary>the extra time of a first shot of class cls</summary>
    public double Of(string cls) => !On ? 0 : Seconds ?? (cls.Equals("Bela_s0", StringComparison.OrdinalIgnoreCase) ? ORIGINAL_BELA : ORIGINAL);
}
