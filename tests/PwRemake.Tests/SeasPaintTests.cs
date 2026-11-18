// Tests of the SEAS parts that keep their colours (game/src/View/Materials/SeasKeepParts.cs, linked into this project):
// the Black Widow's legs and the exo flamethrower's gun are found by their mesh nodes in the converted models, nothing
// else is (Kacper 2026-11-12, mission 13: "no longer paint its legs in team colours", "do not paint the flamethrower
// for the exo robot"). Needs PW_DATA (and PW_DATA_MIRAGE for MIRAGE's own models).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.View.Materials;

namespace PwRemake.Tests;

[TestClass]
public static class SeasPaintTests
{
    static JsonNode Gltf(string f)
    {
        var b = File.ReadAllBytes(f);
        return JsonNode.Parse(new ReadOnlySpan<byte>(b, 20, BitConverter.ToInt32(b, 12)))!;
    }

    static List<string> Meshes(JsonNode j) =>
        j["nodes"]!.AsArray().Where(n => n?["mesh"] != null).Select(n => n!["name"]!.GetValue<string>()).ToList();

    public static void SeasKeepPartsFound(TestContext t)
    {
        var models = Path.Combine(t.Data(), "assets", "models");
        var widow = Path.Combine(models, "seas_wehrspinne.glb");
        var exo = Path.Combine(models, "seas_mobile_suit_flamethrower.glb");
        if (!File.Exists(widow) || !File.Exists(exo)) t.Skip("no SEAS models");

        // the Black Widow: every mesh but the body (mesh3) - the legs
        var w = Meshes(Gltf(widow));
        var kept = w.Where(n => SeasKeepParts.Keeps("seas_wehrspinne", n)).ToList();
        t.True(!kept.Any(n => n.StartsWith("seas_wehrspinne_mesh3_")), "the widow's body keeps its paint");
        t.Equal(w.Count - 1, kept.Count, "the widow's legs (all meshes but the body) keep their colours");
        // (the turret on it and the wreck are other models: their paint stays)
        t.True(!SeasKeepParts.Keeps("seas_wehrspinne_top", "seas_wehrspinne_mesh7_lod3_f0"), "the turret is another model");

        // the exo flamethrower: only the gun in its right hand
        var e = Meshes(Gltf(exo));
        var ek = e.Where(n => SeasKeepParts.Keeps("seas_mobile_suit_flamethrower", n)).ToList();
        t.Equal(1, ek.Count, "one part of the exo flamethrower keeps its colours");
        t.True(ek[0].StartsWith("seas_mobile_suit_flamethrower_mesh28_"), "the flamethrower (mesh28)");
        // ... the part the flamethrower suit has and the plain suit has not (its right hand has fingers there)
        var plain = Path.Combine(models, "seas_mobile_suit.glb");
        if (File.Exists(plain)) t.True(!Meshes(Gltf(plain)).Any(n => SeasKeepParts.Keeps("seas_mobile_suit", n)), "the plain exo keeps all its paint");

        // no other model of the data has a part that keeps its colours
        int others = 0;
        foreach (var v in new[] { "PW_DATA", "PW_DATA_MIRAGE" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } d && Directory.Exists(Path.Combine(d, "assets", "models")))
                foreach (var f in Directory.EnumerateFiles(Path.Combine(d, "assets", "models"), "*.glb"))
                {
                    var m = Path.GetFileNameWithoutExtension(f);
                    if (m is "seas_wehrspinne" or "seas_mobile_suit_flamethrower") continue;
                    if (!m.StartsWith("seas_") && !m.StartsWith("babbag")) continue;
                    others += Meshes(Gltf(f)).Count(n => SeasKeepParts.Keeps(m, n));
                }
        t.Equal(0, others, "no other SEAS model loses paint");
        t.Log($"widow: {kept.Count} of {w.Count} meshes kept; exo flamethrower: {ek[0]}");
    }
}
