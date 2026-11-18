// Helpers of the map readers: a bounds-checked little-endian view of a byte range (the JS readers' DataView over a
// Uint8Array subarray), the text decoding of the JS readers (TextDecoder('latin1'), which is Windows-1252 by the web's
// encoding standard), and the JS conversion of a string to a number (`+s`), which the map readers use on the level
// info's values.
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PwRemake.Sim.Maps;

/// <summary>
/// A view of bytes [Off, Off + Length) of an array, as the JS `b.subarray(...)` with a DataView over it: reads outside
/// the view throw (DataView's RangeError), <see cref="At"/> gives -1 outside (the JS `b[i]` = undefined).
/// </summary>
public readonly struct ByteView
{
    public readonly byte[] A;
    public readonly int Off;
    public readonly int Length;

    public ByteView(byte[] a) : this(a, 0, a.Length) { }
    public ByteView(byte[] a, int off, int length) { A = a; Off = off; Length = length; }

    public static readonly ByteView Empty = new(Array.Empty<byte>());
    public bool IsEmpty => Length == 0;

    /// <summary>the byte at i, or -1 outside the view (the JS undefined: compares unequal to every number)</summary>
    public int At(int i) => i >= 0 && i < Length ? A[Off + i] : -1;
    public byte this[int i] { get { Check(i, 1); return A[Off + i]; } }

    void Check(int o, int n) { if (o < 0 || n < 0 || o > Length - n) throw new IndexOutOfRangeException($"offset {o} + {n} outside {Length} bytes"); }
    public uint U32(int o) { Check(o, 4); return BinaryPrimitives.ReadUInt32LittleEndian(A.AsSpan(Off + o, 4)); }
    public ushort U16(int o) { Check(o, 2); return BinaryPrimitives.ReadUInt16LittleEndian(A.AsSpan(Off + o, 2)); }
    /// <summary>f32 widened to double (as the JS getFloat32)</summary>
    public double F32(int o) { Check(o, 4); return BinaryPrimitives.ReadSingleLittleEndian(A.AsSpan(Off + o, 4)); }

    /// <summary>subarray(begin, end) with the JS clamping (a range past the end is cut, never an error)</summary>
    public ByteView Sub(long begin, long end)
    {
        long b = Math.Clamp(begin, 0, Length), e = Math.Clamp(end, 0, Length);
        if (e < b) e = b;
        return new ByteView(A, Off + (int)b, (int)(e - b));
    }
    public ReadOnlySpan<byte> Span => A.AsSpan(Off, Length);
    public int IndexOf(byte v) => Span.IndexOf(v);
    public byte[] ToArray() => Span.ToArray();
}

/// <summary>TextDecoder('latin1') of the JS readers = Windows-1252 (WHATWG encoding standard).</summary>
public static class Latin1
{
    // 0x80 .. 0x9F; the five bytes Windows-1252 leaves undefined map to the same code point (WHATWG)
    static readonly char[] High =
    {
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    };

    public static string Decode(ByteView b)
    {
        var s = b.Span;
        var sb = new StringBuilder(s.Length);
        foreach (byte c in s) sb.Append(c >= 0x80 && c < 0xA0 ? High[c - 0x80] : (char)c);
        return sb.ToString();
    }
    public static string Decode(byte[] b) => Decode(new ByteView(b));
}

/// <summary>JavaScript number conversions the map code relies on.</summary>
public static class JsNum
{
    /// <summary>The JS `+s` (ToNumber of a string): surrounding white space ignored, "" = 0, 0x / 0o / 0b prefixes,
    /// "Infinity"; anything else that is not a whole decimal literal is NaN. null (undefined) = NaN.</summary>
    public static double ToNumber(string? s)
    {
        if (s == null) return double.NaN;
        string t = s.Trim(' ', '\t', '\n', '\v', '\f', '\r', ' ', '﻿', (char)0x2028, (char)0x2029);
        if (t.Length == 0) return 0;
        if (t.Length > 2 && t[0] == '0' && (t[1] | 0x20) is 'x' or 'o' or 'b')
        {
            int radix = (t[1] | 0x20) == 'x' ? 16 : (t[1] | 0x20) == 'o' ? 8 : 2;
            double v = 0;
            for (int i = 2; i < t.Length; i++)
            {
                int d = HexDigit(t[i]);
                if (d < 0 || d >= radix) return double.NaN;
                v = v * radix + d;
            }
            return v;
        }
        if (t is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (t == "-Infinity") return double.NegativeInfinity;
        // a decimal literal: [+-] digits [. digits] [e [+-] digits], at least one digit in the mantissa
        int k = 0;
        if (t[k] == '+' || t[k] == '-') k++;
        int digits = 0;
        while (k < t.Length && char.IsAsciiDigit(t[k])) { k++; digits++; }
        if (k < t.Length && t[k] == '.') { k++; while (k < t.Length && char.IsAsciiDigit(t[k])) { k++; digits++; } }
        if (digits == 0) return double.NaN;
        if (k < t.Length && (t[k] | 0x20) == 'e')
        {
            k++;
            if (k < t.Length && (t[k] == '+' || t[k] == '-')) k++;
            int ed = 0;
            while (k < t.Length && char.IsAsciiDigit(t[k])) { k++; ed++; }
            if (ed == 0) return double.NaN;
        }
        if (k != t.Length) return double.NaN;
        return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    static int HexDigit(char c) => c >= '0' && c <= '9' ? c - '0' : (c | 0x20) >= 'a' && (c | 0x20) <= 'f' ? (c | 0x20) - 'a' + 10 : -1;

    /// <summary>the JS truthiness of a number: not 0 and not NaN</summary>
    public static bool Truthy(double v) => v != 0 && !double.IsNaN(v);
    /// <summary>`+s || def`</summary>
    public static double Or(string? s, double def) { double v = ToNumber(s); return Truthy(v) ? v : def; }
}
