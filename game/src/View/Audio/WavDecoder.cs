// IMA ADPCM (wav format 0x11) -> PCM16. Most of the game's sounds (Data/*/Audio/Sound/**.wav) are IMA ADPCM, which
// neither browsers nor Godot's AudioStreamWav read in this layout (Godot's own IMA format is not the block layout of
// a Microsoft/IMA wav); the remake decodes them itself. The other wavs (PCM 0x1) load directly.
//
// Output: the samples as 16-bit integers, channels interleaved. The JS wrote pred / 32768 into float channels; the
// integer here is that same pred (bit-exact: tests/golden/audio_adpcm.json, made by the JS in node).
// Ported from remake/src/engine/audio.js decodeWav (pw-exporter 98c7703).
// No Godot types: the tests link this file (tests/PwRemake.Tests).
using System;
using System.Buffers.Binary;

namespace PwRemake.View.Audio;

/// <summary>Decoded PCM16: channels interleaved, Frames samples per channel.</summary>
public sealed class PcmSound
{
    public int Channels;
    public int Rate;
    public int Frames;
    /// <summary>Frames * Channels samples, interleaved (L R L R ...).</summary>
    public short[] Samples = Array.Empty<short>();
    public double Duration => Rate > 0 ? (double)Frames / Rate : 0;

    /// <summary>The samples as little-endian bytes (AudioStreamWav.Data, 16 bit).</summary>
    public byte[] ToBytes()
    {
        var b = new byte[Samples.Length * 2];
        for (int i = 0; i < Samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), Samples[i]);
        return b;
    }
}

/// <summary>The fmt chunk of a RIFF/WAVE file.</summary>
public readonly record struct WavFormat(int Tag, int Channels, int Rate, int BlockAlign, int Bits);

public static class WavDecoder
{
    // ---------------------------------------------------------------- IMA ADPCM (wav format 0x11)
    static readonly int[] ImaStep = { 7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
        107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 };
    static readonly int[] ImaIndex = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

    /// <summary>The chunks of a RIFF/WAVE: its format and where its data is (null when it is no wav).</summary>
    public static (WavFormat fmt, int dataOffset, int dataSize)? ReadChunks(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 44 || BinaryPrimitives.ReadUInt32BigEndian(buf) != 0x52494646 ||
            BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(8)) != 0x57415645) return null;
        long o = 12;
        WavFormat? fmt = null;
        int dataO = -1, dataN = 0;
        while (o + 8 <= buf.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice((int)o));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice((int)o + 4));
            if (id == 0x666d7420 && o + 24 <= buf.Length)
            {
                var f = buf.Slice((int)o + 8);
                fmt = new WavFormat(BinaryPrimitives.ReadUInt16LittleEndian(f), BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(2)),
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(4)), BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(12)),
                    BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(14)));
            }
            else if (id == 0x64617461) { dataO = (int)o + 8; dataN = (int)Math.Min(size, buf.Length - o - 8); }
            o += 8 + (long)size + (size & 1);
        }
        if (fmt == null || dataO < 0) return null;
        return (fmt.Value, dataO, dataN);
    }

    /// <summary>A RIFF/WAVE with IMA ADPCM data -> PCM16 (null for other formats: those load directly).</summary>
    public static PcmSound? DecodeImaAdpcm(ReadOnlySpan<byte> buf)
    {
        var chunks = ReadChunks(buf);
        if (chunks == null) return null;
        var (fmt, dataO, dataN) = chunks.Value;
        if (fmt.Tag != 0x11 || fmt.Bits != 4 || fmt.Channels < 1 || fmt.BlockAlign <= 4 * fmt.Channels) return null;
        int ch = fmt.Channels, align = fmt.BlockAlign, perBlock = (align - 4 * ch) * 2 / ch + 1;
        int blocks = dataN / align;
        int n = blocks * perBlock;
        var res = new PcmSound { Channels = ch, Rate = fmt.Rate, Frames = Math.Max(1, n), Samples = new short[Math.Max(1, n) * ch] };
        var outS = res.Samples;
        Span<int> pred = stackalloc int[ch], idx = stackalloc int[ch];
        for (int k = 0; k < blocks; k++)
        {
            int bas = dataO + k * align;
            // the block header per channel: the first sample, the step index
            for (int c = 0; c < ch; c++)
            {
                pred[c] = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(bas + 4 * c));
                idx[c] = Math.Min(88, (int)buf[bas + 4 * c + 2]);
                outS[(k * perBlock) * ch + c] = (short)pred[c];
            }
            // then groups of 4 bytes (8 samples, low nibble first) per channel, channels interleaved
            int p = bas + 4 * ch, s = 1;
            while (s < perBlock)
            {
                for (int c = 0; c < ch; c++)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        int by = buf[p++];
                        for (int h = 0; h < 2; h++)
                        {
                            int nib = h != 0 ? by >> 4 : by & 15;
                            int step = ImaStep[idx[c]];
                            int diff = ((2 * (nib & 7) + 1) * step) >> 3;
                            pred[c] = Math.Max(-32768, Math.Min(32767, pred[c] + ((nib & 8) != 0 ? -diff : diff)));
                            idx[c] = Math.Max(0, Math.Min(88, idx[c] + ImaIndex[nib]));
                            int si = s + i * 2 + h;
                            if (si < perBlock) outS[(k * perBlock + si) * ch + c] = (short)pred[c];
                        }
                    }
                }
                s += 8;
            }
        }
        return res;
    }

    /// <summary>A plain PCM wav (format 1, 8 or 16 bit) -> PCM16; null for other formats. (Godot loads these itself;
    /// this is for tests and for a backend without a wav loader.)</summary>
    public static PcmSound? DecodePcm(ReadOnlySpan<byte> buf)
    {
        var chunks = ReadChunks(buf);
        if (chunks == null) return null;
        var (fmt, dataO, dataN) = chunks.Value;
        if (fmt.Tag != 1 || (fmt.Bits != 16 && fmt.Bits != 8) || fmt.Channels < 1) return null;
        int bps = fmt.Bits / 8, frames = dataN / (bps * fmt.Channels);
        var res = new PcmSound { Channels = fmt.Channels, Rate = fmt.Rate, Frames = frames, Samples = new short[frames * fmt.Channels] };
        for (int i = 0; i < res.Samples.Length; i++)
            res.Samples[i] = bps == 2 ? BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(dataO + i * 2)) : (short)((buf[dataO + i] - 128) << 8);
        return res;
    }

    /// <summary>Either decoder: IMA ADPCM or PCM; null for anything else.</summary>
    public static PcmSound? Decode(ReadOnlySpan<byte> buf) => DecodeImaAdpcm(buf) ?? DecodePcm(buf);
}
