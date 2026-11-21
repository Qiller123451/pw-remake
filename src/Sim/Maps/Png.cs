// A small PNG codec for the map format (docs/MAP_FORMAT.md): the terrain's images (heights 16-bit greyscale, materials,
// walk grid and biome weights 8-bit greyscale) and the minimap (8-bit RGBA). No Godot, no packages: the zlib stream is
// System.IO.Compression's ZLibStream, the CRC our own.
//
//   read   colour types 0 (grey 8 / 16 bit), 2 (RGB 8), 3 (palette 8), 4 (grey + alpha 8), 6 (RGBA 8 / 16); all five
//          filter types; not interlaced (an image editor's "interlaced" PNG is refused with a message). Unknown
//          ancillary chunks are skipped; the CRCs are checked.
//   write  grey 8, grey 16, RGBA 8. Deterministic: the same pixels give the same bytes (the filter of each row is chosen
//          by the minimum sum of absolute differences, a fixed rule; the zlib level is fixed; no time chunk). The zlib
//          stream comes from the runtime's zlib, so another .NET version may compress differently - the pixels stay
//          the same.
//
//   Png.Read(bytes) -> PngImage (W, H, Channels, Bits, Samples: row-major, 16-bit samples as two bytes big endian)
//   img.Grey8() / Grey16() / Rgba8()      the pixels in the shape the reader wants (RGB -> its red channel as grey ...)
//   Png.WriteGrey8 / WriteGrey16 / WriteRgba8 -> bytes
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PwRemake.Sim.Maps;

/// <summary>A decoded PNG: samples row-major, Channels per pixel (1 grey, 2 grey + alpha, 3 RGB, 4 RGBA; a palette
/// image is expanded to RGBA), Bits 8 or 16 (16-bit samples big endian, two bytes each).</summary>
public sealed class PngImage
{
    public int W, H, Channels, Bits;
    public byte[] Samples = Array.Empty<byte>();

    int Bpp => Channels * Bits / 8;

    /// <summary>one 8-bit value per pixel: grey (or the red channel of a colour image; 16-bit: the high byte)</summary>
    public byte[] Grey8()
    {
        var r = new byte[W * H];
        int bpp = Bpp;
        for (int i = 0; i < r.Length; i++) r[i] = Samples[i * bpp];
        return r;
    }

    /// <summary>one 16-bit value per pixel: grey (or the red channel); an 8-bit image's values are scaled (v * 257)</summary>
    public ushort[] Grey16()
    {
        var r = new ushort[W * H];
        int bpp = Bpp;
        for (int i = 0; i < r.Length; i++)
            r[i] = Bits == 16 ? (ushort)((Samples[i * bpp] << 8) | Samples[i * bpp + 1]) : (ushort)(Samples[i * bpp] * 257);
        return r;
    }

    /// <summary>R G B A bytes per pixel (grey -> R = G = B; no alpha -> 255; 16-bit -> the high bytes)</summary>
    public byte[] Rgba8()
    {
        var r = new byte[W * H * 4];
        int bpp = Bpp, step = Bits / 8;
        for (int i = 0; i < W * H; i++)
        {
            int o = i * bpp;
            byte S(int c) => Samples[o + c * step];
            byte cr, cg, cb, ca = 255;
            switch (Channels)
            {
                case 1: cr = cg = cb = S(0); break;
                case 2: cr = cg = cb = S(0); ca = S(1); break;
                case 3: cr = S(0); cg = S(1); cb = S(2); break;
                default: cr = S(0); cg = S(1); cb = S(2); ca = S(3); break;
            }
            r[i * 4] = cr; r[i * 4 + 1] = cg; r[i * 4 + 2] = cb; r[i * 4 + 3] = ca;
        }
        return r;
    }
}

public static class Png
{
    static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    // ------------------------------------------------------------------------------------------- CRC-32 (PNG / zip polynomial)
    static readonly uint[] CrcTable = MakeCrcTable();
    static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
    static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = crc;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
        return c;
    }
    public static uint Crc32(ReadOnlySpan<byte> data) => Crc(0xffffffffu, data) ^ 0xffffffffu;

    // ------------------------------------------------------------------------------------------- reading
    /// <summary>true when the bytes start with the PNG signature</summary>
    public static bool IsPng(ReadOnlySpan<byte> b) => b.Length >= 8 && b[..8].SequenceEqual(Signature);

    /// <summary>only the size (the IHDR chunk) - the map list reads the terrain's size so</summary>
    public static (int W, int H) ReadSize(ReadOnlySpan<byte> b)
    {
        if (!IsPng(b) || b.Length < 24 || Encoding.ASCII.GetString(b.Slice(12, 4)) != "IHDR") throw new InvalidDataException("not a PNG file");
        return ((int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20)));
    }

    public static PngImage Read(byte[] b)
    {
        if (!IsPng(b)) throw new InvalidDataException("not a PNG file");
        int o = 8, w = 0, h = 0, depth = 0, ctype = -1, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        bool end = false;
        while (!end)
        {
            if (o + 12 > b.Length) throw new InvalidDataException("PNG ends before IEND");
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
            if (len < 0 || o + 12L + len > b.Length) throw new InvalidDataException("PNG chunk runs past the end");
            string type = Encoding.ASCII.GetString(b, o + 4, 4);
            var data = b.AsSpan(o + 8, len);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o + 8 + len));
            if (Crc32(b.AsSpan(o + 4, len + 4)) != crc) throw new InvalidDataException($"PNG chunk {type}: bad CRC");
            switch (type)
            {
                case "IHDR":
                    w = (int)BinaryPrimitives.ReadUInt32BigEndian(data); h = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    depth = data[8]; ctype = data[9]; interlace = data[12];
                    if (data[10] != 0 || data[11] != 0) throw new InvalidDataException("PNG: unknown compression or filter method");
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
                case "IEND": end = true; break;
                default:
                    if ((type[0] & 0x20) == 0) throw new InvalidDataException($"PNG: unknown critical chunk {type}");
                    break;                                            // (ancillary: text, gamma, time ... - skipped)
            }
            o += 12 + len;
        }
        if (w <= 0 || h <= 0 || (long)w * h > 64L * 1024 * 1024) throw new InvalidDataException($"PNG: bad size {w} x {h}");
        if (interlace != 0) throw new InvalidDataException("PNG: interlaced images are not supported - save it without interlacing");
        int channels = ctype switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG: colour type {ctype}") };
        bool ok = ctype switch { 0 => depth is 8 or 16, 3 => depth == 8, _ => depth is 8 or 16 };
        if (!ok) throw new InvalidDataException($"PNG: colour type {ctype} with {depth} bits is not supported (8 or 16 bits; palettes 8 bits)");
        int bpp = channels * depth / 8, stride = w * bpp;
        var raw = new byte[(long)h * stride];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var prev = new byte[stride];
            var cur = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                int f = z.ReadByte();
                if (f < 0) throw new InvalidDataException("PNG: image data ends early");
                z.ReadExactly(cur);
                Unfilter(f, cur, prev, bpp);
                Buffer.BlockCopy(cur, 0, raw, y * stride, stride);
                (prev, cur) = (cur, prev);
            }
        }
        if (ctype == 3)
        {
            if (palette == null) throw new InvalidDataException("PNG: palette image without a palette");
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                int k = raw[i];
                rgba[i * 4] = 3 * k + 2 < palette.Length ? palette[3 * k] : (byte)0;
                rgba[i * 4 + 1] = 3 * k + 2 < palette.Length ? palette[3 * k + 1] : (byte)0;
                rgba[i * 4 + 2] = 3 * k + 2 < palette.Length ? palette[3 * k + 2] : (byte)0;
                rgba[i * 4 + 3] = trns != null && k < trns.Length ? trns[k] : (byte)255;
            }
            return new PngImage { W = w, H = h, Channels = 4, Bits = 8, Samples = rgba };
        }
        return new PngImage { W = w, H = h, Channels = channels, Bits = depth, Samples = raw };
    }

    static void Unfilter(int f, byte[] cur, byte[] prev, int bpp)
    {
        int n = cur.Length;
        switch (f)
        {
            case 0: break;
            case 1: for (int i = bpp; i < n; i++) cur[i] += cur[i - bpp]; break;
            case 2: for (int i = 0; i < n; i++) cur[i] += prev[i]; break;
            case 3:
                for (int i = 0; i < n; i++) cur[i] += (byte)(((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1);
                break;
            case 4:
                for (int i = 0; i < n; i++) cur[i] += Paeth(i >= bpp ? cur[i - bpp] : (byte)0, prev[i], i >= bpp ? prev[i - bpp] : (byte)0);
                break;
            default: throw new InvalidDataException($"PNG: unknown filter type {f}");
        }
    }

    static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ------------------------------------------------------------------------------------------- writing
    public static byte[] WriteGrey8(int w, int h, byte[] px) => Write(w, h, 0, 8, 1, px);

    /// <summary>any supported colour type (0 grey, 2 RGB, 3 palette, 4 grey + alpha, 6 RGBA; 8 or 16 bits) with the
    /// samples as stored (16-bit big endian); filter 0..4 forces one filter type on every row (the tests' images),
    /// -1 chooses per row</summary>
    public static byte[] Encode(int w, int h, int ctype, int depth, byte[] samples, int filter = -1, byte[]? palette = null)
    {
        int channels = ctype switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new ArgumentException($"colour type {ctype}") };
        return Write(w, h, ctype, depth, channels * depth / 8, samples, filter, palette);
    }

    public static byte[] WriteGrey16(int w, int h, ushort[] px)
    {
        var s = new byte[px.Length * 2];
        for (int i = 0; i < px.Length; i++) { s[2 * i] = (byte)(px[i] >> 8); s[2 * i + 1] = (byte)px[i]; }
        return Write(w, h, 0, 16, 2, s);
    }

    public static byte[] WriteRgba8(int w, int h, byte[] rgba) => Write(w, h, 6, 8, 4, rgba);

    static byte[] Write(int w, int h, int ctype, int depth, int bpp, byte[] samples, int force = -1, byte[]? palette = null)
    {
        if (w <= 0 || h <= 0 || samples.Length != (long)w * h * bpp) throw new ArgumentException($"PNG: {samples.Length} bytes for {w} x {h} x {bpp}");
        int stride = w * bpp;
        // the filtered rows: each row with the filter of the smallest sum of |signed bytes| (the usual fixed heuristic)
        var filtered = new byte[(long)h * (stride + 1)];
        var cand = new byte[5][];
        for (int k = 0; k < 5; k++) cand[k] = new byte[stride];
        var zero = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            var cur = samples.AsSpan(y * stride, stride);
            var prev = y > 0 ? samples.AsSpan((y - 1) * stride, stride) : zero;
            int best = 0; long bestSum = long.MaxValue;
            for (int f = 0; f < 5; f++)
            {
                if (force >= 0 && f != force) continue;
                var c = cand[f];
                for (int i = 0; i < stride; i++)
                {
                    byte a = i >= bpp ? cur[i - bpp] : (byte)0, b = prev[i], cc = i >= bpp ? prev[i - bpp] : (byte)0;
                    c[i] = f switch
                    {
                        0 => cur[i],
                        1 => (byte)(cur[i] - a),
                        2 => (byte)(cur[i] - b),
                        3 => (byte)(cur[i] - ((a + b) >> 1)),
                        _ => (byte)(cur[i] - Paeth(a, b, cc)),
                    };
                }
                long sum = 0;
                for (int i = 0; i < stride; i++) { int x = (sbyte)c[i]; sum += x < 0 ? -x : x; }
                if (sum < bestSum) { bestSum = sum; best = f; }
            }
            long at = (long)y * (stride + 1);
            filtered[at] = (byte)best;
            Buffer.BlockCopy(cand[best], 0, filtered, (int)at + 1, stride);
        }
        byte[] packed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, true)) z.Write(filtered);
            packed = ms.ToArray();
        }
        using var outp = new MemoryStream();
        outp.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = (byte)depth; ihdr[9] = (byte)ctype;
        Chunk(outp, "IHDR", ihdr);
        if (palette != null) Chunk(outp, "PLTE", palette);
        Chunk(outp, "IDAT", packed);
        Chunk(outp, "IEND", Array.Empty<byte>());
        return outp.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> n = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(n, (uint)data.Length);
        s.Write(n);
        var td = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, 0, 4, td, 0);
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        BinaryPrimitives.WriteUInt32BigEndian(n, Crc32(td));
        s.Write(n);
    }
}
