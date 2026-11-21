// Enemy scaling of a co-op campaign game (docs/COOP.md "Scaling"): the enemies get more units and more strength per
// extra seat (an AI commander counts like a human), times the host's co-op difficulty.
//
//   factor(kind) = 1 + per_seat(kind) × (seats - 1) × difficulty
//   per_seat:   the definition's `scaling` (count / health / damage), else the defaults below
//   difficulty: Normal 1.0, Hard 1.5, Brutal 2.0
//
// Example: 3 seats, Hard, the defaults: units ×(1 + 0.5 × 2 × 1.5) = ×2.5, health ×1.6, damage ×1.3.
//
// Which players: every player hostile to seat 1 at the start of the mission, and every player that becomes hostile later
// (re-checked when the diplomacy changes), except the definition's `exclude` and the seats. Wild animals (no player) are
// left as they are. When: only while the seats can build - a no-build mission and a hero phase scale nothing (no
// extra units, health or damage: CoopRuntime.EnemyScaling). Everything is a pure function of GameStart (no randomness): fractions of units carry over from one
// spawn to the next per player (Carry), so 3 spawns of 1 at ×1.5 make 1, 2, 1 extra.
using System;
using System.Collections.Generic;

namespace PwRemake.Sim.Coop;

public sealed class CoopScaling
{
    /// <summary>more units per extra seat (+50 %)</summary>
    public const double DefaultCount = 0.5;
    /// <summary>more health per extra seat (+20 %)</summary>
    public const double DefaultHealth = 0.2;
    /// <summary>more damage per extra seat (+10 %)</summary>
    public const double DefaultDamage = 0.1;
    public static readonly string[] DifficultyNames = { "Normal", "Hard", "Brutal" };
    /// <summary>the co-op difficulty's multiplier of the per-seat values</summary>
    public static double DifficultyFactor(int d) => d switch { <= 0 => 1.0, 1 => 1.5, _ => 2.0 };

    public readonly double Count, Health, Damage;
    public readonly CoopScalingDef Def;
    public readonly int Seats, Difficulty;
    readonly Dictionary<int, double> _carry = new();

    public CoopScaling(CoopScalingDef def, int seats, int difficulty)
    {
        Def = def; Seats = seats; Difficulty = difficulty;
        double k = Math.Max(0, seats - 1) * DifficultyFactor(difficulty);
        Count = 1 + def.Count * k;
        Health = 1 + def.Health * k;
        Damage = 1 + def.Damage * k;
    }

    /// <summary>how many units to make for n the mission asks for, for player `slot` (fractions carry over)</summary>
    public int Units(int slot, int n)
    {
        if (n <= 0 || Count <= 1) return n;
        double want = n * Count + _carry.GetValueOrDefault(slot);
        int k = (int)Math.Floor(want + 1e-9);
        _carry[slot] = want - k;
        return Math.Max(n, k);
    }

    /// <summary>the scaled number without the carry (tables of waves: the same factor every time)</summary>
    public int Fixed(int n) => n <= 0 ? n : Math.Max(n, (int)Math.Round(n * Count, MidpointRounding.AwayFromZero));

    public override string ToString() => $"units ×{Count:0.##}, health ×{Health:0.##}, damage ×{Damage:0.##} ({Seats} seats, {DifficultyNames[Math.Clamp(Difficulty, 0, 2)]})";
}
