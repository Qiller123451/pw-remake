// Lip sync and the day time of an in-engine cutscene - the parts without the engine (the stage applies them:
// App/SessionSequenceStage.cs).
//
// Lip sync (toolkit docs/original/LIP_SYNC.md; Data/Base/Sequences/Setup.ini LipSyncSetup, PWClient 0x7aadb0 /
// 0x6e6090): the .lsd next to a speech file holds the weights of the phonemes it uses (gzip: f32 fps, u32 n,
// n x {u32 frame, u32 k, k x {u32 phoneme, f32 weight}}); each phoneme is a layer turning the character's jaw towards
// a frame of its two-frame "faceanim" clip. Setup.ini's phoneme -> frame table (ids: PWClient 0x7acc70); a layer whose
// frame the clip does not have is not drawn - with the shipped clips only frame 1 (IY, IH, h, y) shows: the jaw turns
// by up to 4.8 degrees x (the sum of their weights), as in the original (almost invisible).
//
// Day time items (ItemDayTime 0x50a8d0 / 0x50a930): from its start the clock shows the item's time of day, held for its
// length and running on from there (144 game seconds per second); weather type 0 switches rain and snow off, 1 snow,
// 2 rain at intensity / 100 (every shipped item has type 0). The JS put the scene's clock on top of the world's
// (World.dayStart) and took it off at the end; here the world is not touched (docs/SAVES.md): the stage hands the scene's
// time of day and weather to the session, which uses them instead of the world's while the scene plays (the ambience's
// day / night, the weather drawn).
//
// No Godot types: the tests link this file.
// Ported from remake/src/ui/cutscene.js (readLsd, PHONEME_FRAME, lip, jaw, dayItems) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using PwRemake.Sim.Core;

namespace PwRemake.UI.Mission.Logic;

public static class LipSync
{
    /// <summary>Setup.ini LipSyncSetup: phoneme id -> frame of the faceanim clip (the 40 ids, each once).</summary>
    public static readonly IReadOnlyDictionary<int, int> PhonemeFrame = new Dictionary<int, int>
    {
        [0] = 0, [1] = 1, [2] = 1, [39] = 1, [17] = 1, [3] = 2, [4] = 2, [10] = 2, [11] = 2, [5] = 3, [8] = 3, [9] = 3, [7] = 4, [6] = 4,
        [13] = 5, [12] = 6, [14] = 6, [38] = 6, [18] = 7, [20] = 8, [26] = 9, [29] = 9, [34] = 10, [35] = 10, [16] = 11, [19] = 11,
        [21] = 11, [22] = 11, [28] = 11, [31] = 11, [15] = 11, [23] = 12, [32] = 12, [33] = 12, [36] = 12, [37] = 12, [24] = 12,
        [25] = 13, [30] = 13, [27] = 13,
    };

    /// <summary>The frame 1 pose of every shipped faceanim (LIP_SYNC.md §2, read from all_characters.gsf: one 2-frame
    /// chunk shared by all 230 character models): q(x, y, z, w) = 4.8 degrees about the jaw's local z. Frame 0 is the
    /// identity. Used when a converted model's faceanim keys are not usable (the toolkit writes this chunk type's
    /// quaternions as zeros).</summary>
    public static readonly double[] Frame1 = { -0.0017, -0.0007, 0.0419, 0.9991 };

    /// <summary>The jaw bone (depth-first bone 18, guid 0x8924f02c) as the toolkit names it.</summary>
    public const string JawBone = "bone_8924f02c";

    /// <summary>One record of an .lsd: its time (seconds from the speech's start) and the weights of its phonemes.</summary>
    public readonly record struct Record(double T, (int Id, double W)[] Weights);

    /// <summary>Reads an .lsd (gzip); null when it is not one.</summary>
    public static List<Record>? ReadLsd(byte[] gz)
    {
        byte[] b;
        try
        {
            using var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
            using var m = new MemoryStream();
            z.CopyTo(m);
            b = m.ToArray();
        }
        catch (InvalidDataException) { return null; }
        if (b.Length < 8) return null;
        float fps = BitConverter.ToSingle(b, 0);
        uint n = BitConverter.ToUInt32(b, 4);
        var res = new List<Record>();
        int o = 8;
        for (uint i = 0; i < n && o + 8 <= b.Length; i++)
        {
            uint frame = BitConverter.ToUInt32(b, o), k = BitConverter.ToUInt32(b, o + 4);
            o += 8;
            var ws = new List<(int, double)>();
            for (uint j = 0; j < k && o + 8 <= b.Length; j++, o += 8) ws.Add(((int)BitConverter.ToUInt32(b, o), BitConverter.ToSingle(b, o + 4)));
            res.Add(new Record(frame / (fps > 0 ? fps : 25.0), ws.ToArray()));
        }
        return res;
    }

    /// <summary>The weight keys (scene time, weight) of the phonemes that turn the jaw: those on frame 1 (frame 0 is the
    /// rest pose; frames the clip lacks - frames &gt;= `frames` - are dropped, as the client does). start: the speech
    /// item's time on the timeline.</summary>
    public static List<List<(double T, double W)>> Layers(IEnumerable<Record> recs, double start, int frames = 2)
    {
        var layers = new Dictionary<int, List<(double, double)>>();
        var order = new List<int>();
        foreach (var r in recs)
            foreach (var (id, w) in r.Weights)
            {
                int f = PhonemeFrame.TryGetValue(id, out var x) ? x : 0;
                if (f != 1 || f >= frames) continue;
                if (!layers.TryGetValue(id, out var l)) { layers[id] = l = new(); order.Add(id); }
                l.Add((start + r.T, w));
            }
        var res = new List<List<(double, double)>>();
        foreach (var id in order) res.Add(layers[id]);
        return res;
    }

    /// <summary>The jaw's weight at scene time t: each layer's keys linear between, the ends held; layers at or below
    /// 0.01 are not drawn (0x6e6090).</summary>
    public static double Weight(List<List<(double T, double W)>> layers, double t)
    {
        double w = 0;
        foreach (var K in layers)
        {
            if (K.Count == 0) continue;
            double v = K[^1].W;
            if (t <= K[0].T) v = K[0].W;
            else
                for (int i = 0; i < K.Count - 1; i++)
                    if (t < K[i + 1].T) { v = K[i].W + (K[i + 1].W - K[i].W) * ((t - K[i].T) / Math.Max(1e-6, K[i + 1].T - K[i].T)); break; }
            if (v > 0.01) w += v;
        }
        return w;
    }

    /// <summary>Is this a usable frame 1 of a faceanim (4 finite numbers of a unit quaternion, a turn a jaw makes:
    /// at most 0.6 rad from the rest pose q0)? The JS left a broken clip out; here Frame1 takes its place.</summary>
    public static bool UsableTurn(double[] q0, double[] q1)
    {
        if (q0.Length < 4 || q1.Length < 4) return false;
        double n0 = 0, n1 = 0, d = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!double.IsFinite(q0[i]) || !double.IsFinite(q1[i])) return false;
            n0 += q0[i] * q0[i]; n1 += q1[i] * q1[i]; d += q0[i] * q1[i];
        }
        if (Math.Abs(n0 - 1) > 0.02 || Math.Abs(n1 - 1) > 0.02) return false;
        double turn = 2 * Math.Acos(Math.Min(1, Math.Abs(d) / Math.Sqrt(n0 * n1)));
        return double.IsFinite(turn) && turn <= 0.6;
    }
}

/// <summary>The time of day and the weather a cutscene's day time items set (cutscene.js dayItems).</summary>
public static class SceneDayTime
{
    /// <summary>game seconds of the clock per real second (the world's VIRTUAL_FACTOR)</summary>
    public const double Factor = 144;

    /// <summary>The latest day time item begun at scene time t: (time of day in seconds 0..86400, weather type,
    /// intensity 0..1); null when none has begun (the world's clock and weather stay).</summary>
    public static (double Tod, int Weather, double Intensity)? At(IEnumerable<TimelineItem> items, double t)
    {
        TimelineItem? it = null;
        foreach (var x in items)
            if (x.K == "daytime" && x.T <= t && x.Raw?["tod"] != null && (it == null || x.T >= it.T)) it = x;
        if (it == null) return null;
        double tod = it.Raw!["tod"].Num() + Math.Max(0, t - (it.T + it.Len)) * Factor;
        tod = ((tod % 86400) + 86400) % 86400;
        return (tod, (int)it.Raw["weather"].Num(), it.Raw["intensity"].Num() / 100);
    }

    /// <summary>The world's night test (CTimeMgr.IsVirtualDay) on a time of day.</summary>
    public static bool IsNight(double tod) { double f = tod / 86400; return f < 0.25 || f > 0.75; }
}
