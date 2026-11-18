// The engine's gait changes: walkanimconfig.txt (Data/Base/Scripts/Server/init/walkanimconfig.txt) and the GSF walk
// sets' transition slots.
//
// A walk set (GSF table of contents; pwexport/blender archive.py WALKSET_SLOTS) has the gaits walk_1 .. walk_4 (slots
// 0-3), their end clips, and the transition clips accel_1_2, accel_2_3, accel_3_4 (slots 19-21: T12, T23, T34) and
// brake_4_3, brake_3_2, brake_2_1 (slots 22-24: T43, T32, T21). Slot 28 names the set's table in walkanimconfig.txt:
// Animals1 .. Animals4 for the characters and animals - always "Animals<number of gaits>" (all_animals.gsf,
// all_characters.gsf, all_vehicles.gsf: 1797 walk sets checked; the exceptions: the brachiosaurus' "rage" set, Animals1
// with two gaits, and the flying trader's Ships4) -, Ships2 .. Ships4, Hu_SteamTank, Walk1Only (no transitions).
//
// A table: row = the gait asked for (0 = standing), column = the gait the unit is in; the entry is the clip the engine
// plays next - 'Id' the idle, 'S<n>' / 'L<n>' / 'E<n>' gait n's start / loop / end, 'T<a><b>' the transition from gait a
// to gait b, a digit 'n' "go for gait n first" (Animals3 from gait 1 to gait 3: '2' -> T12, then from gait 2: T23,
// then L3). After a transition the unit is in gait b and the table is asked again.
//
// The converted data keeps the walk sets' gait clips (manifest "walk") but not their transition slots; the transition
// clips themselves are in the models (KEEP_ANIMS keeps the walk sets' clips). Their names follow one rule that gives the
// slots' clips of 1728 of the 1797 walk sets' transition slots (the rest name a gait clip as their "transition" - no
// transition - or a clip the model does not have): accel_<a>_<b> / brake_<a>_<b> with the gait clip's "_new" (the
// newer SEAS / hero walks "defn"), the shoulder and belly walks with their prefix (shoulder_accel_1_2,
// belly_brake_2_1), the carry walk without (carry_walk_n -> accel_1_2), swim_<a>to<b> for the swimming gaits.
//
// Only the clip changes: the speed of the unit is the simulation's as before (docs/SIMULATION.md "Animation audit").
using System;
using System.Collections.Generic;

namespace PwRemake.Sim.World;

public static class WalkGaits
{
    // walkanimconfig.txt Animals1 .. Animals4 (rows: gait asked for 0..4; columns: gait the unit is in 0..4)
    static readonly string[][][] ANIMALS =
    {
        new[]   // Animals1
        {
            new[] { "Id", "E1", "E1", "E1", "E1" },
            new[] { "S1", "L1", "L1", "L1", "L1" },
            new[] { "1", "1", "1", "1", "1" },
            new[] { "1", "1", "1", "1", "1" },
            new[] { "1", "1", "1", "1", "1" },
        },
        new[]   // Animals2
        {
            new[] { "Id", "E1", "E2", "E2", "E2" },
            new[] { "S1", "L1", "T21", "L1", "L1" },
            new[] { "S2", "T12", "L2", "L2", "L2" },
            new[] { "2", "2", "2", "2", "2" },
            new[] { "2", "2", "2", "2", "2" },
        },
        new[]   // Animals3
        {
            new[] { "Id", "E1", "E2", "E3", "E3" },
            new[] { "S1", "L1", "T21", "T21", "T21" },
            new[] { "S2", "T12", "L2", "T32", "T32" },
            new[] { "S3", "2", "T23", "L3", "L3" },
            new[] { "3", "2", "3", "3", "L3" },
        },
        new[]   // Animals4
        {
            new[] { "Id", "E1", "E2", "E3", "E4" },
            new[] { "S1", "L1", "T21", "T21", "T21" },
            new[] { "S2", "T12", "L2", "T32", "T32" },
            new[] { "S3", "2", "T23", "L3", "T43" },
            new[] { "S4", "2", "3", "T34", "L4" },
        },
    };

    /// <summary>the transitions (from gait, to gait) the table plays going from gait `from` to gait `to` with a walk set
    /// of `gaits` gaits (Animals&lt;gaits&gt;), in order; empty when it switches directly</summary>
    public static List<(int A, int B)> Chain(int gaits, int from, int to)
    {
        var res = new List<(int, int)>(3);
        if (gaits < 1 || gaits > 4 || from < 1 || from > 4 || to < 1 || to > 4) return res;
        var tab = ANIMALS[gaits - 1];
        int cur = from;
        for (int guard = 0; guard < 8; guard++)
        {
            string e = tab[to][cur];
            for (int r = 0; r < 4 && e.Length == 1 && char.IsDigit(e[0]); r++) e = tab[e[0] - '0'][cur];   // "gait n first"
            if (e.Length != 3 || e[0] != 'T') break;                                                       // L / S / E: there
            int a = e[1] - '0', b = e[2] - '0';
            res.Add((a, b));
            if (b == cur) break;
            cur = b;
        }
        return res;
    }

    /// <summary>a gait clip of a walk set: the part before the gait number ("walk_", "shoulder_walk_", "hump_" ...), the
    /// gait 1..4 and what follows ("" or "_new"); gait 0 when the clip is none</summary>
    public static (string Prefix, int Gait, string Suffix) Parse(string? clip)
    {
        if (string.IsNullOrEmpty(clip)) return ("", 0, "");
        foreach (var fam in FAMILIES)
        {
            int i = clip.LastIndexOf(fam, StringComparison.Ordinal);
            if (i < 0 || i + fam.Length >= clip.Length) continue;
            char d = clip[i + fam.Length];
            if (d < '1' || d > '4') continue;
            string suf = clip[(i + fam.Length + 1)..];
            if (suf != "" && suf != "_new") continue;
            string pre = clip[..(i + fam.Length)];
            // the families' own clips only (walk_2, carry_walk_2, shoulder_walk_2, belly_walk_2, hump_2, swim_2, rage_2)
            if (fam != "walk_" && i != 0) continue;
            return (pre, d - '0', suf);
        }
        return ("", 0, "");
    }
    static readonly string[] FAMILIES = { "walk_", "hump_", "swim_", "rage_" };

    /// <summary>the transition clip from gait a to gait b of the walk set of a gait clip with this prefix and suffix
    /// (the naming rule above)</summary>
    public static string ClipName(string prefix, string suffix, int a, int b)
    {
        if (prefix == "swim_") return $"swim_{a}to{b}";
        string keep = prefix.StartsWith("shoulder_", StringComparison.Ordinal) ? "shoulder_" : prefix.StartsWith("belly_", StringComparison.Ordinal) ? "belly_" : "";
        return $"{keep}{(b > a ? "accel_" : "brake_")}{a}_{b}{suffix}";
    }

    /// <summary>
    /// The transition clips the engine plays when a unit in the gait clip `from` is asked for the gait clip `to` (both
    /// of one walk set), in order; null when there are none (another walk set, not gaits, the same gait, a model without
    /// the clips: the gait switches directly, as before). The table: Animals&lt;the number of the set's gaits the model
    /// has&gt;.
    /// </summary>
    public static string[]? Transitions(AnimCtl anim, string? from, string? to)
    {
        if (from == null || to == null || from == to) return null;
        var (p0, g0, s0) = Parse(from);
        var (p1, g1, s1) = Parse(to);
        if (g0 == 0 || g1 == 0 || g0 == g1 || p0 != p1 || s0 != s1) return null;
        int gaits = 0;
        for (int n = 1; n <= 4; n++) if (anim.Has(p1 + n + s1)) gaits = n; else break;
        if (gaits < Math.Max(g0, g1)) return null;
        var chain = Chain(gaits, g0, g1);
        if (chain.Count == 0) return null;
        var res = new List<string>(chain.Count);
        foreach (var (a, b) in chain)
        {
            var n = ClipName(p1, s1, a, b);
            // a slot naming a gait clip (the brachiosaurus' walk_2 as its "accel"): no transition
            if (anim.Has(n) && Parse(n).Gait == 0) res.Add(n);
        }
        return res.Count > 0 ? res.ToArray() : null;
    }
}
