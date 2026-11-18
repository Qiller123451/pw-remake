// Numbers of the simulation. The simulation is engine-free (no Godot types) and deterministic: the same commands on
// the same data give the same game on every machine, which co-op and multiplayer (lockstep) and replays need
// (docs/ARCHITECTURE.md "Determinism"). Rules:
//   * doubles everywhere (the JS remake computed in doubles; porting 1:1 keeps its numbers),
//   * randomness only from the world's SimRandom (never System.Random, never Math.random-like globals),
//   * no iteration over hash sets / dictionaries where the order changes the result (use lists or sorted keys),
//   * no wall clock, no threads inside a simulation step.
using System;
using System.Runtime.CompilerServices;

namespace PwRemake.Sim.Core;

/// <summary>A point / direction of the world (metres). X east, Y up, Z south - as the JS remake and three.js.</summary>
public struct V3 : IEquatable<V3>
{
    public double X, Y, Z;
    public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static readonly V3 Zero = new(0, 0, 0);

    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static V3 operator /(V3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);

    public readonly double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    /// <summary>Length on the ground plane (x, z) - most rules measure so.</summary>
    public readonly double Length2D => Math.Sqrt(X * X + Z * Z);
    public readonly V3 Flat => new(X, 0, Z);
    public readonly V3 Normalized { get { double l = Length; return l > 1e-12 ? this / l : Zero; } }

    public static double Dist(V3 a, V3 b) => (a - b).Length;
    public static double Dist2D(V3 a, V3 b) { double dx = a.X - b.X, dz = a.Z - b.Z; return Math.Sqrt(dx * dx + dz * dz); }
    public static double DistSq2D(V3 a, V3 b) { double dx = a.X - b.X, dz = a.Z - b.Z; return dx * dx + dz * dz; }
    public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static V3 Lerp(V3 a, V3 b, double t) => a + (b - a) * t;

    public readonly bool Equals(V3 o) => X == o.X && Y == o.Y && Z == o.Z;
    public override readonly bool Equals(object? o) => o is V3 v && Equals(v);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(V3 a, V3 b) => a.Equals(b);
    public static bool operator !=(V3 a, V3 b) => !a.Equals(b);
    public override readonly string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";
}

/// <summary>The process's culture for the simulation: numbers in strings (Lua's tostring and concatenation, keys, logs,
/// parsing of data) must read the same on a Polish and an English Windows, or two machines of a lockstep game run
/// apart. World's static constructor calls Use(); the game, the tests and the server call it at their start.</summary>
public static class SimCulture
{
    public static void Use()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = c;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = c;
        System.Globalization.CultureInfo.CurrentCulture = c;
        System.Globalization.CultureInfo.CurrentUICulture = c;
    }
}

public static class SMath
{
    public const double PI = Math.PI;
    public const double TAU = Math.PI * 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    /// <summary>Angle a - b wrapped to (-PI, PI].</summary>
    public static double AngleDiff(double a, double b)
    {
        double d = (a - b) % TAU;
        if (d > PI) d -= TAU; else if (d <= -PI) d += TAU;
        return d;
    }
    /// <summary>Heading of the ground direction (dx, dz) as the JS remake: atan2(dx, dz).</summary>
    public static double Heading(double dx, double dz) => Atan2(dx, dz);

    // ---- the elementary functions of the simulation: the same bits on every machine (FdLibm.cs: a port of fdlibm, as
    // V8 uses it - System.Math's call the C runtime, whose last bits differ by platform and CPU; docs/MULTIPLAYER.md
    // "Determinism"). src/Sim calls these, never Math.Sin / Cos / Tan / Atan / Atan2 / Asin / Acos / Exp / Log / Pow.
    // (Math.Sqrt, Abs, Floor, Round, Min, Max are exact everywhere and stay.)
    public static double Sin(double x) => Determinism.FdLibm.Sin(x);
    public static double Cos(double x) => Determinism.FdLibm.Cos(x);
    public static double Tan(double x) => Determinism.FdLibm.Tan(x);
    public static double Atan(double x) => Determinism.FdLibm.Atan(x);
    public static double Atan2(double y, double x) => Determinism.FdLibm.Atan2(y, x);
    public static double Asin(double x) => Determinism.FdLibm.Asin(x);
    public static double Acos(double x) => Determinism.FdLibm.Acos(x);
    public static double Exp(double x) => Determinism.FdLibm.Exp(x);
    public static double Log(double x) => Determinism.FdLibm.Log(x);
    public static double Pow(double x, double y) => Determinism.FdLibm.Pow(x, y);
}

/// <summary>
/// The simulation's random numbers (xoshiro256**): seeded per game, saved with it, identical on every machine.
/// Replaces the JS remake's Math.random(): `rng.Next()` is a double in [0, 1).
/// </summary>
public sealed class SimRandom
{
    ulong _s0, _s1, _s2, _s3;

    public SimRandom(ulong seed) { Seed(seed); }

    public void Seed(ulong seed)
    {
        // splitmix64 to spread the seed
        ulong z = seed;
        _s0 = Mix(ref z); _s1 = Mix(ref z); _s2 = Mix(ref z); _s3 = Mix(ref z);
        static ulong Mix(ref ulong x)
        {
            ulong r = x += 0x9E3779B97F4A7C15UL;
            r = (r ^ (r >> 30)) * 0xBF58476D1CE4E5B9UL;
            r = (r ^ (r >> 27)) * 0x94D049BB133111EBUL;
            return r ^ (r >> 31);
        }
    }

    public ulong NextU64()
    {
        ulong result = RotL(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
        _s2 ^= t; _s3 = RotL(_s3, 45);
        return result;
        static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));
    }

    /// <summary>[0, 1)</summary>
    public double Next() => (NextU64() >> 11) * (1.0 / (1UL << 53));
    /// <summary>[lo, hi)</summary>
    public double Range(double lo, double hi) => lo + (hi - lo) * Next();
    /// <summary>integer in [0, n)</summary>
    public int Int(int n) => n <= 0 ? 0 : (int)(Next() * n);
    /// <summary>integer in [lo, hi] (both included)</summary>
    public int IntRange(int lo, int hi) => lo + Int(hi - lo + 1);
    public bool Chance(double p) => Next() < p;
    public T Pick<T>(System.Collections.Generic.IReadOnlyList<T> list) => list[Int(list.Count)];

    public ulong[] State => new[] { _s0, _s1, _s2, _s3 };
    public void SetState(ulong[] s) { _s0 = s[0]; _s1 = s[1]; _s2 = s[2]; _s3 = s[3]; }
}
