// Noise helpers of the terrain: the random jungle map (mapgen.js) and the irregular ground-material borders of the
// original maps (maps/source.js splat) use them. Integer arithmetic as the JS (Math.imul = wrapping 32-bit multiply).
// Ported from remake/src/engine/terrain.js "noise helpers" (pw-exporter 98c7703).
using System;

namespace PwRemake.Sim.Maps;

public static class Noise
{
    /// <summary>JS ToInt32 of a whole number (Math.floor results): modulo 2^32.</summary>
    static uint U32(double v) => unchecked((uint)(long)v);

    static double Hash2(double ix, double iz, double seed)
    {
        uint h = unchecked(U32(ix) * 374761393u + U32(iz) * 668265263u + U32(seed) * 982451653u);
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return (h ^ (h >> 16)) / 4294967296.0;
    }

    static double VNoise(double x, double z, double seed)
    {
        double ix = Math.Floor(x), iz = Math.Floor(z), fx = x - ix, fz = z - iz;
        double sx = fx * fx * (3 - 2 * fx), sz = fz * fz * (3 - 2 * fz);
        double a = Hash2(ix, iz, seed), b = Hash2(ix + 1, iz, seed), c = Hash2(ix, iz + 1, seed), d = Hash2(ix + 1, iz + 1, seed);
        return a + (b - a) * sx + (c - a) * sz + (a - b - c + d) * sx * sz;
    }

    /// <summary>fractal value noise in [0, 1]</summary>
    public static double Fbm(double x, double z, double seed, int oct = 4)
    {
        double s = 0, amp = 0.5, f = 1, n = 0;
        for (int i = 0; i < oct; i++) { s += amp * VNoise(x * f, z * f, seed + i * 17); n += amp; amp *= 0.5; f *= 2.03; }
        return s / n;
    }

    public static double Smoothstep(double a, double b, double x)
    {
        double t = Math.Min(1, Math.Max(0, (x - a) / (b - a)));
        return t * t * (3 - 2 * t);
    }
}

/// <summary>mulberry32: the seeded generator of the random jungle map (mapgen.js generate). Not the world's
/// SimRandom - the map must come out the same as in the JS remake for the same seed.</summary>
public sealed class Mulberry32
{
    uint _a;
    public Mulberry32(double seed) { _a = unchecked((uint)(long)seed); }
    public double Next()
    {
        unchecked
        {
            _a += 0x6D2B79F5u;
            uint t = (_a ^ (_a >> 15)) * (1u | _a);
            t = (t + (t ^ (t >> 7)) * (61u | t)) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }
}
