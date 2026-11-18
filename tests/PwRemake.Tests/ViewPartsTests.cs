// Tests of the part bits (game/src/View/Parts/Parts.cs, linked into this project): the static signature of a gate's
// end pieces. A gate's render flags 7 and 11 (attr bits 0x80 / 0x800) select its end pieces; bit 11 lies in the epoch
// range, so read as an epoch bit the -x end piece showed only in epoch III and one side of the gate stood apart from
// its wing (the attributes are hu_palisade_gate's construction-state end pieces, mesh73 and mesh76).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.World;
using PwRemake.View.Parts;

namespace PwRemake.Tests;

[TestClass]
public static class ViewPartsTests
{
    public static void GateEndPiecesInEveryEpoch(TestContext t)
    {
        const uint east = 0x5c0009f, west = 0x5c0081f;          // the +x end (flag 7) and the -x end (flag 11)
        for (int age = 1; age <= 5; age++)
        {
            t.True(PartBits.SigVisible(PartBits.StaticSig(east, PartBits.GATE_ENDS), 2, 0, age), $"+x end piece, epoch {age}");
            t.True(PartBits.SigVisible(PartBits.StaticSig(west, PartBits.GATE_ENDS), 2, 0, age), $"-x end piece, epoch {age}");
        }
        t.Equal(PartBits.StaticSig(east, PartBits.GATE_ENDS), PartBits.StaticSig(west, PartBits.GATE_ENDS), "both ends: the same state");
        // (read without the gate's flags, bit 11 is an epoch bit: the old behaviour, epoch III only)
        t.True(!PartBits.SigVisible(PartBits.StaticSig(west), 2, 0, 1) && PartBits.SigVisible(PartBits.StaticSig(west), 2, 0, 3), "bit 11 as an epoch bit");
        // other models keep their epoch variants
        const uint epoch3 = 0x600081f;
        t.True(!PartBits.SigVisible(PartBits.StaticSig(epoch3, 0), 4, 0, 1) && PartBits.SigVisible(PartBits.StaticSig(epoch3, 0), 4, 0, 3), "an epoch III part elsewhere");
    }

    /// <summary>
    /// A wall archive's emitters (the smoke G / H and fire I / J of every component's damage stages) belong to their
    /// component: a piece sprays only those of its own parts (BuildName). Before, every piece sprayed all of the
    /// archive's emitters - 98 smoke emitters on a half destroyed MIRAGE SEAS wall piece (mission 13, Kacper 2026-11-12).
    /// </summary>
    public static void WallEmittersByComponent(TestContext t)
    {
        t.Equal("3b", PartBits.WallComp("particles_3bG"), "an arm's emitter");
        t.Equal("x05", PartBits.WallComp("particles_x05H"), "a bend hub's emitter");
        t.Equal("4d", PartBits.WallComp("particles_4dI2"), "a duplicate name (Godot appends a number)");
        t.Equal("x1", PartBits.WallComp("x1D"), "a mesh's part extra");
        t.True(PartBits.WallComp("particles_dest01_rauch_schwarz") == null && PartBits.WallComp("particles_feuer") == null, "other emitters");
        var models = new List<string>();
        foreach (var v in new[] { "PW_DATA", "PW_DATA_MIRAGE" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } d)
                foreach (var m in new[] { "hs_re_enforced_wall", "seas_fence", "hu_palisade", "hu_re_enforced_wall", "hu_small_wall", "aje_clay_wall" })
                    if (Path.Combine(d, "assets", "models", m + ".glb") is var f && File.Exists(f) && !models.Any(x => Path.GetFileName(x) == m + ".glb")) models.Add(f);
        if (models.Count == 0) t.Skip("no wall models");
        // a straight piece (neighbours east and west, level), a corner and a lone post
        var pieces = new[] { new[] { -10, -10, -10, 0, -10, -10, -10, 0 }, new[] { -10, 0, -10, 0, -10, -10, -10, -10 }, new[] { -10, -10, -10, -10, -10, -10, -10, -10 } };
        foreach (var f in models)
        {
            var b = File.ReadAllBytes(f);
            var j = JsonNode.Parse(new ReadOnlySpan<byte>(b, 20, BitConverter.ToInt32(b, 12)))!;
            var em = j["nodes"]!.AsArray().Where(n => n?["extras"]?["kind"]?.GetValue<string>() == "particles")
                .Select(n => (name: n!["name"]!.GetValue<string>(), attr: n["extras"]!["attr"]!.GetValue<uint>())).ToList();
            string m = Path.GetFileNameWithoutExtension(f);
            // (an emitter of no component the engine names is never shown: hu_palisade's "x35H")
            var odd = em.Where(e => PartBits.WallComp(e.name) == null).Select(e => e.name).ToList();
            t.True(odd.Count <= 1, $"{m}: every emitter but a stray one has its component ({string.Join(", ", odd)})");
            if (odd.Count > 0) t.Log($"{m}: emitters of no component (never shown): {string.Join(", ", odd)}");
            em = em.Where(e => PartBits.WallComp(e.name) != null).ToList();
            foreach (var rel in pieces)
            {
                var comps = WallMap.BuildName(rel).ToHashSet();
                int smoke = em.Count(e => comps.Contains(PartBits.WallComp(e.name)!) && PartBits.SigVisible(PartBits.StaticSig(e.attr), 4, 1, 1));
                int fire = em.Count(e => comps.Contains(PartBits.WallComp(e.name)!) && PartBits.SigVisible(PartBits.StaticSig(e.attr), 4, 2, 1));
                int all = em.Count(e => PartBits.SigVisible(PartBits.StaticSig(e.attr), 4, 1, 1));
                t.True(smoke <= 16 && fire <= 16, $"{m} [{string.Join(".", comps)}]: {smoke} emitters at damage 1, {fire} at damage 2 (all the archive's: {all})");
                t.Log($"{m} {string.Join(".", comps)}: damage 1 {smoke} emitters, damage 2 {fire} (before: {all} at damage 1)");
            }
        }
    }
}
