// Deterministic elementary functions: a C# port of fdlibm 5.3 (Sun Microsystems, freely distributable - the notice
// below), the library the browser remake's JavaScript engine (V8: src/base/ieee754.cc) also uses for Math.sin, cos,
// tan, asin, acos, atan, atan2, exp, log and pow.
//
// Why: System.Math.Sin & co. call the C runtime of the machine (glibc on Linux, the UCRT on Windows, which picks
// FMA code paths by CPU). Their last bits differ between platforms and CPUs, and a lockstep game (co-op, PvP,
// replays shared between machines: docs/MULTIPLAYER.md) runs apart from one such bit. These functions use only
// +, -, *, / and Math.Sqrt (IEEE 754: exactly rounded everywhere) and bit operations, so they give the same
// result on every machine - and the same as the JS remake's numbers.
//
// The file is compiled into two assemblies: src/Sim (SMath.Sin ... wrap it) and MoonSharp (the Lua math library and
// the ^ operator), as an internal class of each (MoonSharp.csproj links it).
//
// Differences to fdlibm: arguments of sin / cos / tan beyond 2^19 * pi/2 are first reduced by fmod(x, 2 pi) (exact,
// deterministic, less accurate than fdlibm's __kernel_rem_pio2 - nothing in the game turns that far); sinh, cosh and
// tanh are the plain formulas on Exp.
//
// ====================================================
// Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
//
// Developed at SunSoft, a Sun Microsystems, Inc. business.
// Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice
// is preserved.
// ====================================================
using System;

namespace PwRemake.Determinism;

internal static class FdLibm
{
    // ---------------------------------------------------------------- words of a double
    static int HI(double x) => (int)(BitConverter.DoubleToInt64Bits(x) >> 32);
    static uint LO(double x) => (uint)BitConverter.DoubleToInt64Bits(x);
    static double Make(int hi, uint lo) => BitConverter.Int64BitsToDouble(((long)hi << 32) | lo);
    static double WithLo(double x, uint lo) => Make(HI(x), lo);
    static double WithHi(double x, int hi) => Make(hi, LO(x));

    const double one = 1.0, half = 0.5, huge = 1.0e300, tiny = 1.0e-300, zero = 0.0;

    // ================================================================ sin, cos, tan
    static readonly int[] npio2_hw = MakeNpio2();
    static int[] MakeNpio2() { var a = new int[32]; for (int i = 0; i < 32; i++) a[i] = HI((i + 1) * 1.57079632679489661923); return a; }

    const double invpio2 = 6.36619772367581382433e-01,
        pio2_1 = 1.57079632673412561417e+00, pio2_1t = 6.07710050650619224932e-11,
        pio2_2 = 6.07710050630396597660e-11, pio2_2t = 2.02226624879595063154e-21,
        pio2_3 = 2.02226624871116645580e-21, pio2_3t = 8.47842766036889956997e-32;
    const double TwoPi = 6.28318530717958647692;

    /// <summary>__ieee754_rem_pio2: x = n * pi/2 + (y0 + y1), |y0 + y1| &lt;= pi/4</summary>
    static int RemPio2(double x, out double y0, out double y1)
    {
        int hx = HI(x), ix = hx & 0x7fffffff;
        if (ix <= 0x3fe921fb) { y0 = x; y1 = 0; return 0; }
        double z;
        if (ix < 0x4002d97c)
        {
            if (hx > 0)
            {
                z = x - pio2_1;
                if (ix != 0x3ff921fb) { y0 = z - pio2_1t; y1 = (z - y0) - pio2_1t; }
                else { z -= pio2_2; y0 = z - pio2_2t; y1 = (z - y0) - pio2_2t; }
                return 1;
            }
            z = x + pio2_1;
            if (ix != 0x3ff921fb) { y0 = z + pio2_1t; y1 = (z - y0) + pio2_1t; }
            else { z += pio2_2; y0 = z + pio2_2t; y1 = (z - y0) + pio2_2t; }
            return -1;
        }
        if (ix <= 0x413921fb)
        {
            double t = Math.Abs(x);
            int n = (int)(t * invpio2 + half);
            double fn = n;
            double r = t - fn * pio2_1;
            double w = fn * pio2_1t;
            if (n < 32 && ix != npio2_hw[n - 1]) y0 = r - w;
            else
            {
                int j = ix >> 20;
                y0 = r - w;
                int i = j - ((HI(y0) >> 20) & 0x7ff);
                if (i > 16)
                {
                    t = r; w = fn * pio2_2; r = t - w; w = fn * pio2_2t - ((t - r) - w); y0 = r - w;
                    i = j - ((HI(y0) >> 20) & 0x7ff);
                    if (i > 49) { t = r; w = fn * pio2_3; r = t - w; w = fn * pio2_3t - ((t - r) - w); y0 = r - w; }
                }
            }
            y1 = (r - y0) - w;
            if (hx < 0) { y0 = -y0; y1 = -y1; return -n; }
            return n;
        }
        if (ix >= 0x7ff00000) { y0 = y1 = x - x; return 0; }
        // a huge argument: fmod by 2 pi is exact and deterministic (fdlibm: __kernel_rem_pio2)
        return RemPio2(x % TwoPi, out y0, out y1);
    }

    const double S1 = -1.66666666666666324348e-01, S2 = 8.33333333332248946124e-03, S3 = -1.98412698298579493134e-04,
        S4 = 2.75573137070700676789e-06, S5 = -2.50507602534068634195e-08, S6 = 1.58969099521155010221e-10;
    static double KSin(double x, double y, int iy)
    {
        int ix = HI(x) & 0x7fffffff;
        if (ix < 0x3e400000) { if ((int)x == 0) return x; }
        double z = x * x, v = z * x;
        double r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));
        if (iy == 0) return x + v * (S1 + z * r);
        return x - ((z * (half * y - v * r) - y) - v * S1);
    }

    const double C1 = 4.16666666666666019037e-02, C2 = -1.38888888888741095749e-03, C3 = 2.48015872894767294178e-05,
        C4 = -2.75573143513906633035e-07, C5 = 2.08757232129817482790e-09, C6 = -1.13596475577881948265e-11;
    static double KCos(double x, double y)
    {
        int ix = HI(x) & 0x7fffffff;
        if (ix < 0x3e400000) { if ((int)x == 0) return one; }
        double z = x * x;
        double r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));
        if (ix < 0x3FD33333) return one - (0.5 * z - (z * r - x * y));
        double qx = ix > 0x3fe90000 ? 0.28125 : Make(ix - 0x00200000, 0);
        double hz = 0.5 * z - qx, a = one - qx;
        return a - (hz - (z * r - x * y));
    }

    static readonly double[] T =
    {
        3.33333333333334091986e-01, 1.33333333333201242699e-01, 5.39682539762260521377e-02, 2.18694882948595424599e-02,
        8.86323982359930005737e-03, 3.59207910759131235356e-03, 1.45620945432529025516e-03, 5.88041240820264096874e-04,
        2.46463134818469906812e-04, 7.81794442939557092300e-05, 7.14072491382608190305e-05, -1.85586374855275456654e-05,
        2.59073051863633712884e-05,
    };
    const double pio4 = 7.85398163397448278999e-01, pio4lo = 3.06161699786838301793e-17;
    static double KTan(double x, double y, int iy)
    {
        double z, r, v, w, s;
        int hx = HI(x), ix = hx & 0x7fffffff;
        if (ix < 0x3e300000)
        {
            if ((int)x == 0)
            {
                if (((ix | (int)LO(x)) | (iy + 1)) == 0) return one / Math.Abs(x);
                if (iy == 1) return x;
                double a, t;
                z = w = x + y;
                z = WithLo(z, 0);
                v = y - (z - x);
                t = a = -one / w;
                t = WithLo(t, 0);
                s = one + t * z;
                return t + a * (s + t * v);
            }
        }
        if (ix >= 0x3FE59428)
        {
            if (hx < 0) { x = -x; y = -y; }
            z = pio4 - x;
            w = pio4lo - y;
            x = z + w;
            y = 0.0;
        }
        z = x * x;
        w = z * z;
        r = T[1] + w * (T[3] + w * (T[5] + w * (T[7] + w * (T[9] + w * T[11]))));
        v = z * (T[2] + w * (T[4] + w * (T[6] + w * (T[8] + w * (T[10] + w * T[12])))));
        s = z * x;
        r = y + z * (s * (r + v) + y);
        r += T[0] * s;
        w = x + r;
        if (ix >= 0x3FE59428)
        {
            v = iy;
            return (1 - ((hx >> 30) & 2)) * (v - 2.0 * (x - (w * w / (w + v) - r)));
        }
        if (iy == 1) return w;
        {
            double a, t;
            z = w;
            z = WithLo(z, 0);
            v = r - (z - x);
            t = a = -1.0 / w;
            t = WithLo(t, 0);
            s = 1.0 + t * z;
            return t + a * (s + t * v);
        }
    }

    public static double Sin(double x)
    {
        int ix = HI(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb) return KSin(x, 0, 0);
        if (ix >= 0x7ff00000) return x - x;
        int n = RemPio2(x, out var y0, out var y1);
        return (n & 3) switch { 0 => KSin(y0, y1, 1), 1 => KCos(y0, y1), 2 => -KSin(y0, y1, 1), _ => -KCos(y0, y1) };
    }

    public static double Cos(double x)
    {
        int ix = HI(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb) return KCos(x, 0);
        if (ix >= 0x7ff00000) return x - x;
        int n = RemPio2(x, out var y0, out var y1);
        return (n & 3) switch { 0 => KCos(y0, y1), 1 => -KSin(y0, y1, 1), 2 => -KCos(y0, y1), _ => KSin(y0, y1, 1) };
    }

    public static double Tan(double x)
    {
        int ix = HI(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb) return KTan(x, 0, 1);
        if (ix >= 0x7ff00000) return x - x;
        int n = RemPio2(x, out var y0, out var y1);
        return KTan(y0, y1, 1 - ((n & 1) << 1));
    }

    // ================================================================ atan, atan2
    static readonly double[] atanhi = { 4.63647609000806093515e-01, 7.85398163397448278999e-01, 9.82793723247329054082e-01, 1.57079632679489655800e+00 };
    static readonly double[] atanlo = { 2.26987774529616870924e-17, 3.06161699786838301793e-17, 1.39033110312309984516e-17, 6.12323399573676603587e-17 };
    static readonly double[] aT =
    {
        3.33333333333329318027e-01, -1.99999999998764832476e-01, 1.42857142725034663711e-01, -1.11111104054623557880e-01,
        9.09088713343650656196e-02, -7.69187620504482999495e-02, 6.66107313738753120669e-02, -5.83357013379057348645e-02,
        4.97687799461593236017e-02, -3.65315727442169155270e-02, 1.62858201153657823623e-02,
    };

    public static double Atan(double x)
    {
        double w, s1, s2, z;
        int id;
        int hx = HI(x), ix = hx & 0x7fffffff;
        if (ix >= 0x44100000)
        {
            if (ix > 0x7ff00000 || (ix == 0x7ff00000 && LO(x) != 0)) return x + x;
            return hx > 0 ? atanhi[3] + atanlo[3] : -atanhi[3] - atanlo[3];
        }
        if (ix < 0x3fdc0000)
        {
            if (ix < 0x3e200000) { if (huge + x > one) return x; }
            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3ff30000)
            {
                if (ix < 0x3fe60000) { id = 0; x = (2.0 * x - one) / (2.0 + x); }
                else { id = 1; x = (x - one) / (x + one); }
            }
            else
            {
                if (ix < 0x40038000) { id = 2; x = (x - 1.5) / (one + 1.5 * x); }
                else { id = 3; x = -1.0 / x; }
            }
        }
        z = x * x;
        w = z * z;
        s1 = z * (aT[0] + w * (aT[2] + w * (aT[4] + w * (aT[6] + w * (aT[8] + w * aT[10])))));
        s2 = w * (aT[1] + w * (aT[3] + w * (aT[5] + w * (aT[7] + w * aT[9]))));
        if (id < 0) return x - x * (s1 + s2);
        z = atanhi[id] - ((x * (s1 + s2) - atanlo[id]) - x);
        return hx < 0 ? -z : z;
    }

    const double pi_o_4 = 7.8539816339744827900E-01, pi_o_2 = 1.5707963267948965580E+00, pi = 3.1415926535897931160E+00,
        pi_lo = 1.2246467991473531772E-16;

    public static double Atan2(double y, double x)
    {
        double z;
        int hx = HI(x), ix = hx & 0x7fffffff; uint lx = LO(x);
        int hy = HI(y), iy = hy & 0x7fffffff; uint ly = LO(y);
        if ((ix | (int)((lx | (uint)(-(int)lx)) >> 31)) > 0x7ff00000 || (iy | (int)((ly | (uint)(-(int)ly)) >> 31)) > 0x7ff00000) return x + y;
        if (((hx - 0x3ff00000) | (int)lx) == 0) return Atan(y);
        int m = ((hy >> 31) & 1) | ((hx >> 30) & 2);
        if ((iy | (int)ly) == 0)
        {
            switch (m)
            {
                case 0: case 1: return y;
                case 2: return pi + tiny;
                default: return -pi - tiny;
            }
        }
        if ((ix | (int)lx) == 0) return hy < 0 ? -pi_o_2 - tiny : pi_o_2 + tiny;
        if (ix == 0x7ff00000)
        {
            if (iy == 0x7ff00000)
                return m switch { 0 => pi_o_4 + tiny, 1 => -pi_o_4 - tiny, 2 => 3.0 * pi_o_4 + tiny, _ => -3.0 * pi_o_4 - tiny };
            return m switch { 0 => zero, 1 => -zero, 2 => pi + tiny, _ => -pi - tiny };
        }
        if (iy == 0x7ff00000) return hy < 0 ? -pi_o_2 - tiny : pi_o_2 + tiny;
        int k = (iy - ix) >> 20;
        if (k > 60) z = pi_o_2 + 0.5 * pi_lo;
        else if (hx < 0 && k < -60) z = 0.0;
        else z = Atan(Math.Abs(y / x));
        switch (m)
        {
            case 0: return z;
            case 1: return WithHi(z, HI(z) ^ unchecked((int)0x80000000));
            case 2: return pi - (z - pi_lo);
            default: return (z - pi_lo) - pi;
        }
    }

    // ================================================================ asin, acos
    const double pio2_hi = 1.57079632679489655800e+00, pio2_lo = 6.12323399573676603587e-17, pio4_hi = 7.85398163397448278999e-01,
        pS0 = 1.66666666666666657415e-01, pS1 = -3.25565818622400915405e-01, pS2 = 2.01212532134862925881e-01,
        pS3 = -4.00555345006794114027e-02, pS4 = 7.91534994289814532176e-04, pS5 = 3.47933107596021167570e-05,
        qS1 = -2.40339491173441421878e+00, qS2 = 2.02094576023350569471e+00, qS3 = -6.88283971605453293030e-01,
        qS4 = 7.70381505559019352791e-02;

    public static double Asin(double x)
    {
        double t, w, p, q, c, r, s;
        int hx = HI(x), ix = hx & 0x7fffffff;
        if (ix >= 0x3ff00000)
        {
            if (((ix - 0x3ff00000) | (int)LO(x)) == 0) return x * pio2_hi + x * pio2_lo;
            return (x - x) / (x - x);
        }
        if (ix < 0x3fe00000)
        {
            if (ix < 0x3e400000) { if (huge + x > one) return x; }
            t = x * x;
            p = t * (pS0 + t * (pS1 + t * (pS2 + t * (pS3 + t * (pS4 + t * pS5)))));
            q = one + t * (qS1 + t * (qS2 + t * (qS3 + t * qS4)));
            w = p / q;
            return x + x * w;
        }
        w = one - Math.Abs(x);
        t = w * 0.5;
        p = t * (pS0 + t * (pS1 + t * (pS2 + t * (pS3 + t * (pS4 + t * pS5)))));
        q = one + t * (qS1 + t * (qS2 + t * (qS3 + t * qS4)));
        s = Math.Sqrt(t);
        if (ix >= 0x3FEF3333)
        {
            w = p / q;
            t = pio2_hi - (2.0 * (s + s * w) - pio2_lo);
        }
        else
        {
            w = WithLo(s, 0);
            c = (t - w * w) / (s + w);
            r = p / q;
            p = 2.0 * s * r - (pio2_lo - 2.0 * c);
            q = pio4_hi - 2.0 * w;
            t = pio4_hi - (p - q);
        }
        return hx > 0 ? t : -t;
    }

    public static double Acos(double x)
    {
        double z, p, q, r, w, s, c, df;
        int hx = HI(x), ix = hx & 0x7fffffff;
        if (ix >= 0x3ff00000)
        {
            if (((ix - 0x3ff00000) | (int)LO(x)) == 0) return hx > 0 ? 0.0 : pi + 2.0 * pio2_lo;
            return (x - x) / (x - x);
        }
        if (ix < 0x3fe00000)
        {
            if (ix <= 0x3c600000) return pio2_hi + pio2_lo;
            z = x * x;
            p = z * (pS0 + z * (pS1 + z * (pS2 + z * (pS3 + z * (pS4 + z * pS5)))));
            q = one + z * (qS1 + z * (qS2 + z * (qS3 + z * qS4)));
            r = p / q;
            return pio2_hi - (x - (pio2_lo - x * r));
        }
        if (hx < 0)
        {
            z = (one + x) * 0.5;
            p = z * (pS0 + z * (pS1 + z * (pS2 + z * (pS3 + z * (pS4 + z * pS5)))));
            q = one + z * (qS1 + z * (qS2 + z * (qS3 + z * qS4)));
            s = Math.Sqrt(z);
            r = p / q;
            w = r * s - pio2_lo;
            return pi - 2.0 * (s + w);
        }
        z = (one - x) * 0.5;
        s = Math.Sqrt(z);
        df = WithLo(s, 0);
        c = (z - df * df) / (s + df);
        p = z * (pS0 + z * (pS1 + z * (pS2 + z * (pS3 + z * (pS4 + z * pS5)))));
        q = one + z * (qS1 + z * (qS2 + z * (qS3 + z * qS4)));
        r = p / q;
        w = r * s + c;
        return 2.0 * (df + w);
    }

    // ================================================================ exp, log
    const double twom1000 = 9.33263618503218878990e-302, o_threshold = 7.09782712893383973096e+02, u_threshold = -7.45133219101941108420e+02,
        ln2HI0 = 6.93147180369123816490e-01, ln2LO0 = 1.90821492927058770002e-10, invln2 = 1.44269504088896338700e+00,
        P1 = 1.66666666666666019037e-01, P2 = -2.77777777770155933842e-03, P3 = 6.61375632143793436117e-05,
        P4 = -1.65339022054652515390e-06, P5 = 4.13813679705723846039e-08;

    public static double Exp(double x)
    {
        double y, hi = 0, lo = 0, c, t;
        int k = 0;
        int hxi = HI(x);
        int xsb = (hxi >> 31) & 1;
        int hx = hxi & 0x7fffffff;
        if (hx >= 0x40862E42)
        {
            if (hx >= 0x7ff00000)
            {
                if (((hx & 0xfffff) | (int)LO(x)) != 0) return x + x;
                return xsb == 0 ? x : 0.0;
            }
            if (x > o_threshold) return huge * huge;
            if (x < u_threshold) return twom1000 * twom1000;
        }
        if (hx > 0x3fd62e42)
        {
            if (hx < 0x3FF0A2B2)
            {
                if (x == 1.0) return 2.718281828459045;      // (V8's special case: the reduction gets e's last bit wrong)
                hi = x - (xsb == 0 ? ln2HI0 : -ln2HI0); lo = xsb == 0 ? ln2LO0 : -ln2LO0; k = 1 - xsb - xsb;
            }
            else
            {
                k = (int)(invln2 * x + (xsb == 0 ? 0.5 : -0.5));
                t = k;
                hi = x - t * ln2HI0;
                lo = t * ln2LO0;
            }
            x = hi - lo;
        }
        else if (hx < 0x3e300000)
        {
            if (huge + x > one) return one + x;
        }
        else k = 0;
        t = x * x;
        c = x - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
        if (k == 0) return one - ((x * c) / (c - 2.0) - x);
        y = one - ((lo - (x * c) / (2.0 - c)) - hi);
        if (k >= -1021) return WithHi(y, HI(y) + (k << 20));
        return WithHi(y, HI(y) + ((k + 1000) << 20)) * twom1000;
    }

    const double ln2_hi = 6.93147180369123816490e-01, ln2_lo = 1.90821492927058770002e-10, two54 = 1.80143985094819840000e+16,
        Lg1 = 6.666666666666735130e-01, Lg2 = 3.999999999940941908e-01, Lg3 = 2.857142874366239149e-01, Lg4 = 2.222219843214978396e-01,
        Lg5 = 1.818357216161805012e-01, Lg6 = 1.531383769920937332e-01, Lg7 = 1.479819860511658591e-01;

    public static double Log(double x)
    {
        double hfsq, f, s, z, R, w, t1, t2, dk;
        int k, hx, i, j;
        hx = HI(x); uint lx = LO(x);
        k = 0;
        if (hx < 0x00100000)
        {
            if (((hx & 0x7fffffff) | (int)lx) == 0) return double.NegativeInfinity;
            if (hx < 0) return double.NaN;
            k -= 54; x *= two54;
            hx = HI(x);
        }
        if (hx >= 0x7ff00000) return x + x;
        k += (hx >> 20) - 1023;
        hx &= 0x000fffff;
        i = (hx + 0x95f64) & 0x100000;
        x = WithHi(x, hx | (i ^ 0x3ff00000));
        k += i >> 20;
        f = x - 1.0;
        if ((0x000fffff & (2 + hx)) < 3)
        {
            if (f == zero)
            {
                if (k == 0) return zero;
                dk = k; return dk * ln2_hi + dk * ln2_lo;
            }
            R = f * f * (0.5 - 0.33333333333333333 * f);
            if (k == 0) return f - R;
            dk = k; return dk * ln2_hi - ((R - dk * ln2_lo) - f);
        }
        s = f / (2.0 + f);
        dk = k;
        z = s * s;
        i = hx - 0x6147a;
        w = z * z;
        j = 0x6b851 - hx;
        t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
        t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
        i |= j;
        R = t2 + t1;
        if (i > 0)
        {
            hfsq = 0.5 * f * f;
            if (k == 0) return f - (hfsq - s * (hfsq + R));
            return dk * ln2_hi - ((hfsq - (s * (hfsq + R) + dk * ln2_lo)) - f);
        }
        if (k == 0) return f - s * (f - R);
        return dk * ln2_hi - ((s * (f - R) - dk * ln2_lo) - f);
    }

    // ================================================================ pow
    static readonly double[] bp = { 1.0, 1.5 }, dp_h = { 0.0, 5.84962487220764160156e-01 }, dp_l = { 0.0, 1.35003920212974897128e-08 };
    const double two53 = 9007199254740992.0,
        L1 = 5.99999999999994648725e-01, L2 = 4.28571428578550184252e-01, L3 = 3.33333329818377432918e-01,
        L4 = 2.72728123808534006489e-01, L5 = 2.30660745775561754067e-01, L6 = 2.06975017800338417784e-01,
        lg2 = 6.93147180559945286227e-01, lg2_h = 6.93147182464599609375e-01, lg2_l = -1.90465429995776804525e-09,
        ovt = 8.0085662595372944372e-17, cp = 9.61796693925975554329e-01, cp_h = 9.61796700954437255859e-01,
        cp_l = -7.02846165095275826516e-09, ivln2 = 1.44269504088896338700e+00, ivln2_h = 1.44269502162933349609e+00,
        ivln2_l = 1.92596299112661746887e-08;

    public static double Pow(double x, double y)
    {
        double z, ax, z_h, z_l, p_h, p_l, y1, t1, t2, r, s, t, u, v, w;
        int i, j, k, yisint, n;
        int hx = HI(x), hy = HI(y);
        uint lx = LO(x), ly = LO(y);
        int ix = hx & 0x7fffffff, iy = hy & 0x7fffffff;

        if ((iy | (int)ly) == 0) return one;
        if (ix > 0x7ff00000 || (ix == 0x7ff00000 && lx != 0) || iy > 0x7ff00000 || (iy == 0x7ff00000 && ly != 0)) return x + y;

        yisint = 0;
        if (hx < 0)
        {
            if (iy >= 0x43400000) yisint = 2;
            else if (iy >= 0x3ff00000)
            {
                k = (iy >> 20) - 0x3ff;
                if (k > 20)
                {
                    uint jj = ly >> (52 - k);
                    if ((jj << (52 - k)) == ly) yisint = 2 - (int)(jj & 1);
                }
                else if (ly == 0)
                {
                    j = iy >> (20 - k);
                    if ((j << (20 - k)) == iy) yisint = 2 - (j & 1);
                }
            }
        }

        if (ly == 0)
        {
            if (iy == 0x7ff00000)
            {
                if (((ix - 0x3ff00000) | (int)lx) == 0) return y - y;
                if (ix >= 0x3ff00000) return hy >= 0 ? y : zero;
                return hy < 0 ? -y : zero;
            }
            if (iy == 0x3ff00000) return hy < 0 ? one / x : x;
            if (hy == 0x40000000) return x * x;
            if (hy == 0x3fe00000 && hx >= 0) return Math.Sqrt(x);
        }

        ax = Math.Abs(x);
        if (lx == 0)
        {
            if (ix == 0x7ff00000 || ix == 0 || ix == 0x3ff00000)
            {
                z = ax;
                if (hy < 0) z = one / z;
                if (hx < 0)
                {
                    if (((ix - 0x3ff00000) | yisint) == 0) z = (z - z) / (z - z);
                    else if (yisint == 1) z = -z;
                }
                return z;
            }
        }

        n = (hx >> 31) + 1;
        if ((n | yisint) == 0) return (x - x) / (x - x);
        s = one;
        if ((n | (yisint - 1)) == 0) s = -one;

        if (iy > 0x41e00000)
        {
            if (iy > 0x43f00000)
            {
                if (ix <= 0x3fefffff) return hy < 0 ? huge * huge : tiny * tiny;
                if (ix >= 0x3ff00000) return hy > 0 ? huge * huge : tiny * tiny;
            }
            if (ix < 0x3fefffff) return hy < 0 ? s * huge * huge : s * tiny * tiny;
            if (ix > 0x3ff00000) return hy > 0 ? s * huge * huge : s * tiny * tiny;
            t = ax - one;
            w = (t * t) * (0.5 - t * (0.3333333333333333333333 - t * 0.25));
            u = ivln2_h * t;
            v = t * ivln2_l - w * ivln2;
            t1 = u + v;
            t1 = WithLo(t1, 0);
            t2 = v - (t1 - u);
        }
        else
        {
            double ss, s2, s_h, s_l, t_h, t_l;
            n = 0;
            if (ix < 0x00100000) { ax *= two53; n -= 53; ix = HI(ax); }
            n += (ix >> 20) - 0x3ff;
            j = ix & 0x000fffff;
            ix = j | 0x3ff00000;
            if (j <= 0x3988E) k = 0;
            else if (j < 0xBB67A) k = 1;
            else { k = 0; n += 1; ix -= 0x00100000; }
            ax = WithHi(ax, ix);

            u = ax - bp[k];
            v = one / (ax + bp[k]);
            ss = u * v;
            s_h = WithLo(ss, 0);
            t_h = Make(((ix >> 1) | 0x20000000) + 0x00080000 + (k << 18), 0);
            t_l = ax - (t_h - bp[k]);
            s_l = v * ((u - s_h * t_h) - s_h * t_l);
            s2 = ss * ss;
            r = s2 * s2 * (L1 + s2 * (L2 + s2 * (L3 + s2 * (L4 + s2 * (L5 + s2 * L6)))));
            r += s_l * (s_h + ss);
            s2 = s_h * s_h;
            t_h = WithLo(3.0 + s2 + r, 0);
            t_l = r - ((t_h - 3.0) - s2);
            u = s_h * t_h;
            v = s_l * t_h + t_l * ss;
            p_h = WithLo(u + v, 0);
            p_l = v - (p_h - u);
            z_h = cp_h * p_h;
            z_l = cp_l * p_h + p_l * cp + dp_l[k];
            t = n;
            t1 = WithLo(((z_h + z_l) + dp_h[k]) + t, 0);
            t2 = z_l - (((t1 - t) - dp_h[k]) - z_h);
        }

        y1 = WithLo(y, 0);
        p_l = (y - y1) * t1 + y * t2;
        p_h = y1 * t1;
        z = p_l + p_h;
        j = HI(z);
        i = (int)LO(z);
        if (j >= 0x40900000)
        {
            if (((j - 0x40900000) | i) != 0) return s * huge * huge;
            if (p_l + ovt > z - p_h) return s * huge * huge;
        }
        else if ((j & 0x7fffffff) >= 0x4090cc00)
        {
            if ((unchecked((uint)j - 0xc090cc00u) | (uint)i) != 0) return s * tiny * tiny;
            if (p_l <= z - p_h) return s * tiny * tiny;
        }
        i = j & 0x7fffffff;
        k = (i >> 20) - 0x3ff;
        n = 0;
        if (i > 0x3fe00000)
        {
            n = j + (0x00100000 >> (k + 1));
            k = ((n & 0x7fffffff) >> 20) - 0x3ff;
            t = Make(n & ~(0x000fffff >> k), 0);
            n = ((n & 0x000fffff) | 0x00100000) >> (20 - k);
            if (j < 0) n = -n;
            p_h -= t;
        }
        t = WithLo(p_l + p_h, 0);
        u = t * lg2_h;
        v = (p_l - (t - p_h)) * lg2 + t * lg2_l;
        z = u + v;
        w = v - (z - u);
        t = z * z;
        t1 = z - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
        r = (z * t1) / (t1 - 2.0) - (w + z * w);
        z = one - (r - z);
        j = HI(z);
        j += n << 20;
        if ((j >> 20) <= 0) z = Math.ScaleB(z, n);
        else z = WithHi(z, HI(z) + (n << 20));
        return s * z;
    }

    // ================================================================ the rest of the Lua math library
    public static double Sinh(double x) { double e = Exp(x); return (e - 1 / e) * 0.5; }
    public static double Cosh(double x) { double e = Exp(x); return (e + 1 / e) * 0.5; }
    public static double Tanh(double x)
    {
        if (x > 20) return 1; if (x < -20) return -1;
        double e = Exp(2 * x); return (e - 1) / (e + 1);
    }
}
