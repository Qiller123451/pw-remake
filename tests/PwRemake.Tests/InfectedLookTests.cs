// Tests of the infected look's numbers (game/src/View/Materials/InfectedLookData.cs, linked into this project;
// docs/INFECTED_LOOK.md): the stages grow from healthy to overrun, the wound table is well formed, its wounds and eyes
// sit on the animals' skins (the converted models' bind pose), every wild animal of both configurations gets a wound
// table on its skin (the hand-tuned one or FromSkin), every Norse unit a look (characters: wounds per skeleton on the
// skin, eyes on the head; the undead, statues, vehicles, mounts) and every Norse building a tune (slime, blight,
// clouds, green emitters), the poison clouds' ring. The look itself: game/src/View/Tests/InfectedLookTest.tscn.
//
// Models: PW_MODELS (one folder or several separated by ':'), else the converted data staged in the container
// (Sys1.ModelsDir), then PW_DATA's assets/models. A model missing there is skipped (and counted in the log).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.View.Materials;

namespace PwRemake.Tests;

[TestClass]
public static class InfectedLookTests
{
    public static void StagesGrow(TestContext t)
    {
        var s0 = InfectedLookData.Stage(0);
        t.True(s0.Rot == 0 && s0.SlimeHeight == 0 && s0.Veins == 0 && s0.Mist == 0 && s0.Flies == 0 && s0.Blight == 0 && s0.Skin == 0 && !s0.GreenFire && !s0.Eyes,
            "stage 0 is the normal look");
        for (int i = 1; i <= InfectedLookData.MAX_STAGE; i++)
        {
            InfectionStage a = InfectedLookData.Stage(i - 1), b = InfectedLookData.Stage(i);
            t.Equal(i, b.Stage, $"stage {i} numbered");
            t.True(b.Rot > a.Rot && b.SlimeHeight > a.SlimeHeight && b.Veins > a.Veins && b.Skin > a.Skin && b.Bruises > a.Bruises,
                $"stage {i}: rot, slime, veins, skin and bruises grow");
            t.True(b.Mist > a.Mist && b.Flies >= a.Flies && b.Blight > a.Blight && b.Moss > a.Moss, $"stage {i}: more clouds, flies, blight, moss");
            t.True(b.Rot <= 1 && b.Veins <= 1 && b.Skin <= 1 && b.Bruises <= 1 && b.Moss <= 1, $"stage {i}: shares within 0..1");
        }
        t.True(!InfectedLookData.Stage(1).GreenFire && InfectedLookData.Stage(2).GreenFire && InfectedLookData.Stage(3).GreenFire, "the fires turn green from stage 2");
        t.True(!InfectedLookData.Stage(1).Eyes && InfectedLookData.Stage(2).Eyes, "green eyes from stage 2");
        t.Equal(3, InfectedLookData.Stage(99).Stage, "a stage above the last: the last");
        t.Equal(0, InfectedLookData.Stage(-2).Stage, "a stage below 0: healthy");
        // the slime: the stage's height, at most 45 % of a low building
        t.Near(6.0, InfectedLookData.SlimeOn(23, 3), 1e-9, "a tall building: the stage's slime height");
        t.Near(4 * InfectedLookData.SLIME_MAX_SHARE, InfectedLookData.SlimeOn(4, 3), 1e-9, "a 4 m building: slime to 45 % of it");
        t.Near(0, InfectedLookData.SlimeOn(10, 0), 1e-9, "healthy: no slime");
    }

    public static void WoundTable(TestContext t)
    {
        var names = InfectedLookData.Table.Select(a => a.Model).ToList();
        // the hand-tuned ones: the original five and the common animals Kacper named (wolf / deer: megaloceros; sabre-tooth:
        // smilodon, eusmilus; the raptors; dilophosaurus, stegosaurus, ankylosaurus, brachiosaurus, T-rex, pteranodon) and
        // the Norse mounts
        foreach (var n in new[] { "velociraptor", "smilodon", "triceratops", "allosaurus", "mammoth", "wild_boar", "megaloceros", "eusmilus",
                     "dilophosaurus", "stegosaurus", "ankylosaurus", "brachiosaurus", "trex", "pteranodon", "woolly_rhino", "kentrosaurus",
                     "titan_triceratops", "carcharodontosaurus" })
            t.True(names.Contains(n), $"{n} has wounds of its own");
        t.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "one entry per model");
        t.True(InfectedLookData.Animal("pteranodod") == InfectedLookData.Animal("pteranodon"), "MIRAGE's pteranodon mount (pteranodod) wears the pteranodon's wounds");
        foreach (var a in InfectedLookData.Table) CheckTable(t, a, true);
        t.True(InfectedLookData.Animal("TRICERATOPS") != null && InfectedLookData.Animal("seas_triceratops") == null && InfectedLookData.Animal(null) == null,
            "looked up by model name (case-insensitive), the SEAS triceratops is another model");
    }

    // a wound table's form: 3 .. MAX_WOUNDS wounds, the first from stage 1, in order, both sides, readable radii, more at
    // each stage, all when overrun
    static void CheckTable(TestContext t, AnimalWounds a, bool readable)
    {
        t.True(a.Wounds.Length >= 3 && a.Wounds.Length <= InfectedLookData.MAX_WOUNDS, $"{a.Model}: 3 .. {InfectedLookData.MAX_WOUNDS} wounds");
        t.Equal(1, a.Wounds[0].Stage, $"{a.Model}: the first wound from stage 1");
        for (int i = 1; i < a.Wounds.Length; i++) t.True(a.Wounds[i].Stage >= a.Wounds[i - 1].Stage, $"{a.Model}: wounds in the order they appear");
        t.True(a.Wounds.All(w => w.Stage >= 1 && w.Stage <= InfectedLookData.MAX_STAGE && w.Rx > 0 && w.Ry > 0 && w.Rz > 0), $"{a.Model}: stages 1 .. 3, radii > 0");
        t.True(a.Wounds.Any(w => w.X < -0.05) && a.Wounds.Any(w => w.X > 0.05), $"{a.Model}: wounds on both sides");
        if (readable)
            t.True(a.Wounds.All(w => Math.Min(w.Rx, Math.Min(w.Ry, w.Rz)) >= InfectedLookData.MIN_WOUND_RADIUS - 1e-9),
                $"{a.Model}: every wound at least {InfectedLookData.MIN_WOUND_RADIUS} m (it reads at the game's camera)");
        if (a.Eyes is { } e) t.True(e.Dx > 0 && e.R > 0, $"{a.Model}: eyes");
        t.Equal(0, InfectedLookData.WoundsAt(a, 0).Length, $"{a.Model}: healthy, no wound");
        int prev = 0;
        for (int s = 1; s <= InfectedLookData.MAX_STAGE; s++)
        {
            var w = InfectedLookData.WoundsAt(a, s);
            t.True(w.Length > prev || (s == 1 && w.Length == 1), $"{a.Model}: more wounds at stage {s} ({w.Length})");
            prev = w.Length;
        }
        t.Equal(a.Wounds.Length, InfectedLookData.WoundsAt(a, 3).Length, $"{a.Model}: every wound when overrun");
    }

    // ------------------------------------------------------------------------------------------- the converted models

    static IEnumerable<string> ModelDirs(TestContext t)
    {
        if (Environment.GetEnvironmentVariable("PW_MODELS") is { Length: > 0 } env)
            foreach (var d in env.Split(':', StringSplitOptions.RemoveEmptyEntries)) yield return d;
        yield return Sys1.ModelsDir();
        foreach (var v in new[] { "PW_DATA", "PW_DATA_MIRAGE" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } d) yield return Path.Combine(d, "assets", "models");
    }

    static string? ModelFile(TestContext t, string model)
    {
        foreach (var d in ModelDirs(t))
        {
            var f = Path.Combine(d, model + ".glb");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>a converted model's bind pose: the body meshes' triangles (attribute 0x1f bits: full detail, no gear) as
    /// sample points (corners, middles, edge midpoints), the joints' bind positions (the skins' inverse bind matrices),
    /// its fourcc, the emitter node names; GSF frame</summary>
    sealed record Glb(string Fourcc, List<(double X, double Y, double Z)> Skin, Dictionary<string, (double X, double Y, double Z)> Joints, List<string> Emitters, bool Skinned)
    {
        public (double X, double Y, double Z) Min => (Skin.Min(p => p.X), Skin.Min(p => p.Y), Skin.Min(p => p.Z));
        public (double X, double Y, double Z) Max => (Skin.Max(p => p.X), Skin.Max(p => p.Y), Skin.Max(p => p.Z));
    }

    static readonly Dictionary<string, Glb?> s_glb = new();

    static Glb? Read(TestContext t, string model)
    {
        if (s_glb.TryGetValue(model, out var g)) return g;
        var file = ModelFile(t, model);
        if (file == null) return s_glb[model] = null;
        var b = File.ReadAllBytes(file);
        int len = BitConverter.ToInt32(b, 12);
        var j = JsonNode.Parse(new ReadOnlySpan<byte>(b, 20, len))!;
        int bin = 20 + len + 8;
        float[] Acc(int i, int comps)
        {
            var acc = j["accessors"]![i]!;
            var view = j["bufferViews"]![acc["bufferView"]!.GetValue<int>()]!;
            int off = bin + (view["byteOffset"]?.GetValue<int>() ?? 0) + (acc["byteOffset"]?.GetValue<int>() ?? 0);
            int count = acc["count"]!.GetValue<int>(), stride = view["byteStride"]?.GetValue<int>() ?? comps * 4;
            var r = new float[count * comps];
            for (int k = 0; k < count; k++) for (int c = 0; c < comps; c++) r[k * comps + c] = BitConverter.ToSingle(b, off + k * stride + c * 4);
            return r;
        }
        int[] Idx(int i)
        {
            var acc = j["accessors"]![i]!;
            var view = j["bufferViews"]![acc["bufferView"]!.GetValue<int>()]!;
            int off = bin + (view["byteOffset"]?.GetValue<int>() ?? 0) + (acc["byteOffset"]?.GetValue<int>() ?? 0);
            int count = acc["count"]!.GetValue<int>(), ct = acc["componentType"]!.GetValue<int>();
            var r = new int[count];
            for (int k = 0; k < count; k++) r[k] = ct == 5125 ? (int)BitConverter.ToUInt32(b, off + k * 4) : ct == 5123 ? BitConverter.ToUInt16(b, off + k * 2) : b[off + k];
            return r;
        }
        var nodes = j["nodes"]!.AsArray();
        var root = nodes[j["scenes"]![0]!["nodes"]![0]!.GetValue<int>()]!;
        string fourcc = root["extras"]?["fourcc"]?.GetValue<string>() ?? "";
        var skin = new List<(double, double, double)>();
        var emitters = new List<string>();
        foreach (var n in nodes)
        {
            string name = n?["name"]?.GetValue<string>() ?? "";
            if (name.StartsWith("particles", StringComparison.OrdinalIgnoreCase)) emitters.Add(name);
            if (n?["mesh"] == null) continue;
            uint attr = (uint)(n["extras"]?["attr"]?.GetValue<long>() ?? 0);
            if ((attr & 0xffffffe0) != 0) continue;
            foreach (var p in j["meshes"]![n["mesh"]!.GetValue<int>()]!["primitives"]!.AsArray())
            {
                var v = Acc(p!["attributes"]!["POSITION"]!.GetValue<int>(), 3);
                int[] ix = p["indices"] != null ? Idx(p["indices"]!.GetValue<int>()) : Enumerable.Range(0, v.Length / 3).ToArray();
                (double, double, double) V(int k) => (v[k * 3], v[k * 3 + 1], v[k * 3 + 2]);
                for (int k = 0; k + 2 < ix.Length; k += 3)
                {
                    var (ax, ay, az) = V(ix[k]); var (bx, by, bz) = V(ix[k + 1]); var (cx, cy, cz) = V(ix[k + 2]);
                    skin.Add((ax, ay, az));
                    skin.Add(((ax + bx + cx) / 3, (ay + by + cy) / 3, (az + bz + cz) / 3));
                    skin.Add(((ax + bx) / 2, (ay + by) / 2, (az + bz) / 2));
                    skin.Add(((bx + cx) / 2, (by + cy) / 2, (bz + cz) / 2));
                    skin.Add(((ax + cx) / 2, (ay + cy) / 2, (az + cz) / 2));
                }
            }
        }
        var joints = new Dictionary<string, (double, double, double)>();
        bool skinned = false;
        if (j["skins"] is JsonArray skins)
            foreach (var s in skins)
            {
                skinned = true;
                if (s?["inverseBindMatrices"] == null) continue;
                var ibm = Acc(s["inverseBindMatrices"]!.GetValue<int>(), 16);
                var js = s["joints"]!.AsArray();
                for (int k = 0; k < js.Count; k++)
                {
                    string name = nodes[js[k]!.GetValue<int>()]?["name"]?.GetValue<string>() ?? "";
                    if (joints.ContainsKey(name)) continue;
                    // (column-major; the joint's bind position: the inverse of the inverse bind matrix's translation)
                    var m = new double[16];
                    for (int q = 0; q < 16; q++) m[q] = ibm[k * 16 + q];
                    joints[name] = BindOrigin(m);
                }
            }
        return s_glb[model] = new Glb(fourcc, skin, joints, emitters, skinned);
    }

    // the origin of the inverse of an affine matrix (column-major 4x4): -R^-1 t
    static (double, double, double) BindOrigin(double[] m)
    {
        double a = m[0], b = m[4], c = m[8], d = m[1], e = m[5], f = m[9], g = m[2], h = m[6], i = m[10];
        double tx = m[12], ty = m[13], tz = m[14];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        double[] inv = { (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
                         (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
                         (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det };
        return (-(inv[0] * tx + inv[1] * ty + inv[2] * tz), -(inv[3] * tx + inv[4] * ty + inv[5] * tz), -(inv[6] * tx + inv[7] * ty + inv[8] * tz));
    }

    // a wound table on its model's skin: every wound's centre inside the skin's box, skin inside every wound (a hole to
    // cut or a patch to paint), all together under maxShare of the skin; the eyes on the head's surface
    static void OnSkin(TestContext t, AnimalWounds a, Glb g, double maxShare, List<string> bad)
    {
        var (lo, hi) = (g.Min, g.Max);
        foreach (var w in a.Wounds)
        {
            bool inBox = w.X >= lo.X - 0.05 && w.X <= hi.X + 0.05 && w.Y >= lo.Y - 0.05 && w.Y <= hi.Y + 0.05 && w.Z >= lo.Z - 0.05 && w.Z <= hi.Z + 0.05;
            int inside = g.Skin.Count(p => InfectedLookData.Dist(w, p.X, p.Y, p.Z) < 1);
            if (!inBox || inside < 3) bad.Add($"{a.Model}: the {w.Kind} wound at ({w.X:0.00}, {w.Y:0.00}, {w.Z:0.00}) {(inBox ? "" : "outside the model ")}({inside} skin points in it)");
        }
        double share = g.Skin.Count(p => a.Wounds.Any(w => InfectedLookData.Dist(w, p.X, p.Y, p.Z) < 1)) / (double)g.Skin.Count;
        if (share >= maxShare) bad.Add($"{a.Model}: the wounds take {share:P0} of the skin");
        if (a.Eyes is { } e)
            foreach (var x in new[] { e.Cx + e.Dx, e.Cx - e.Dx })
            {
                double near = g.Skin.Min(p => Math.Sqrt(Math.Pow(p.X - x, 2) + Math.Pow(p.Y - e.Y, 2) + Math.Pow(p.Z - e.Z, 2)));
                if (near > e.R * 2.5) bad.Add($"{a.Model}: an eye at x {x:0.00} off the head (nearest skin {near:0.00} m, radius {e.R:0.00})");
            }
    }

    public static void WoundsOnTheSkin(TestContext t)
    {
        int found = 0;
        var bad = new List<string>();
        foreach (var a in InfectedLookData.Table)
        {
            if (Read(t, a.Model) is not { } g) continue;
            found++;
            t.True(g.Skin.Count > 100, $"{a.Model}: its skin read ({g.Skin.Count} points)");
            OnSkin(t, a, g, 0.4, bad);
        }
        if (found == 0) t.Skip("no animal models in the data");
        t.Log($"{found} of {InfectedLookData.Table.Count} table animals checked on their skins");
        t.True(bad.Count == 0, "the table's wounds on the skins:\n      " + string.Join("\n      ", bad));
    }

    public static void GenericWounds(TestContext t)
    {
        var g = InfectedLookData.Generic("ice_dunkleosteus", -1, 1, -4, 4, 0, 3);
        var h = InfectedLookData.Generic("ice_dunkleosteus", -1, 1, -4, 4, 0, 3);
        t.True(g.Wounds.SequenceEqual(h.Wounds), "deterministic: the box alone decides");
        t.True(g.Wounds.All(w => w.X >= -1 && w.X <= 1 && w.Y >= -4 && w.Y <= 4 && w.Z > 0 && w.Z <= 3), "within the box");
        t.True(g.Wounds[0].Stage == 1 && g.Wounds.Any(w => w.X < 0) && g.Wounds.Any(w => w.X > 0), "the first from stage 1, both flanks");
        t.True(g.Eyes == null, "no eyes known");
        var tiny = InfectedLookData.Generic("x", 0, 0, 0, 0, 0, 0);
        t.True(tiny.Wounds.All(w => w.Rx > 0 && w.Ry > 0 && w.Rz > 0), "a degenerate box: radii still > 0");
        // from the skin: a box of points (a crude body on four legs) - wounds on it, deterministic, readable
        var pts = new List<(double, double, double)>();
        for (double y = -3; y <= 3; y += 0.25)
            for (double a = 0; a < Math.PI * 2; a += 0.3)
            {
                pts.Add((Math.Cos(a) * 1.0, y, 2.5 + Math.Sin(a) * 0.9));
                if (Math.Abs(Math.Abs(y) - 2) < 0.3) for (double z = 0; z < 1.6; z += 0.2) pts.Add((Math.Cos(a) * 0.25 + (a < Math.PI ? 0.6 : -0.6), y, z));
            }
        var s = InfectedLookData.FromSkin("blob", pts);
        t.True(s.Wounds.SequenceEqual(InfectedLookData.FromSkin("blob", pts).Wounds), "FromSkin: deterministic");
        CheckTable(t, s, true);
        t.True(s.Wounds.All(w => pts.Count(p => InfectedLookData.Dist(w, p.Item1, p.Item2, p.Item3) < 1) >= 3), "FromSkin: every wound on the skin");
        t.True(InfectedLookData.FromSkin("none", new List<(double, double, double)>()).Wounds.Length >= 3, "FromSkin without points: the box generic");
    }

    // ------------------------------------------------------------------------------------------- the data: both configurations

    static IEnumerable<(string cfg, JsonNode tt, JsonNode classgfx)> Configs(TestContext t)
    {
        foreach (var (v, cfg) in new[] { ("PW_DATA", "base"), ("PW_DATA_MIRAGE", "mirage") })
        {
            var d = Environment.GetEnvironmentVariable(v);
            if (string.IsNullOrEmpty(d) || !File.Exists(Path.Combine(d, "techtree.json"))) continue;
            var tt = JsonNode.Parse(File.ReadAllText(Path.Combine(d, "techtree.json")))!["StartTT"]!["Objects"]!;
            var cg = JsonNode.Parse(File.ReadAllText(Path.Combine(d, "gamedata.json")))!["classgfx"]!;
            yield return (cfg, tt, cg);
        }
    }

    static string? Gfx(JsonNode cg, string cls) => (cg[cls] ?? cg[cls.ToLowerInvariant()])?.GetValue<string>();

    static IEnumerable<string> Classes(JsonNode tt, string tribe, string type) =>
        tt[tribe]?[type] is JsonObject o ? o.Select(kv => kv.Key) : Enumerable.Empty<string>();

    public static void EveryWildAnimal(TestContext t)
    {
        var cfgs = Configs(t).ToList();
        if (cfgs.Count == 0) t.Skip("PW_DATA / PW_DATA_MIRAGE not set");
        var models = new SortedSet<string>();
        foreach (var (_, tt, cg) in cfgs)
        {
            foreach (var c in Classes(tt, "World", "ANML")) if (Gfx(cg, c) is { } m) models.Add(m);
            foreach (var c in Classes(tt, "Hu", "ANML")) if (Gfx(cg, c) is { } m) models.Add(m);      // the Norse mounts
        }
        int table = 0, skin = 0;
        var missing = new List<string>();
        var bad = new List<string>();
        foreach (var m in models)
        {
            var g = Read(t, m);
            if (g == null) { missing.Add(m); continue; }
            // (MIRAGE's people of Heiligenstadt live in the World's animals: characters)
            var kind = InfectedLookData.KindOf(m, g.Fourcc, g.Skinned);
            if (kind != LookKind.Animal) { t.True(kind is LookKind.Character or LookKind.Vehicle or LookKind.Undead, $"{m} ({g.Fourcc}): drawn as {kind}"); continue; }
            var a = InfectedLookData.Animal(m);
            if (a != null) table++;
            else { a = InfectedLookData.FromSkin(m, g.Skin); skin++; }
            CheckTable(t, a, true);
            OnSkin(t, a, g, 0.45, bad);
        }
        t.Log($"{models.Count} wild animals and mounts: {table} from the table, {skin} from their skins, {missing.Count} not in the data ({string.Join(", ", missing)})");
        t.True(table + skin > 0, "animals checked");
        t.True(bad.Count == 0, "the animals' wounds on the skins:\n      " + string.Join("\n      ", bad));
    }

    public static void EveryNorseUnit(TestContext t)
    {
        var cfgs = Configs(t).ToList();
        if (cfgs.Count == 0) t.Skip("PW_DATA / PW_DATA_MIRAGE not set");
        var models = new SortedSet<string>();
        foreach (var (_, tt, cg) in cfgs)
        {
            foreach (var type in new[] { "CHTR", "VHCL", "SHIP" })
                foreach (var c in Classes(tt, "Hu", type))
                    if (Gfx(cg, c) is { } m)
                    {
                        models.Add(m);
                        // the unit's other levels (hu_warrior_s1 .. s5)
                        if (System.Text.RegularExpressions.Regex.Match(m, @"^(.*)_s\d$") is { Success: true } lv)
                            for (int i = 1; i <= 5; i++) models.Add($"{lv.Groups[1].Value}_s{i}");
                    }
            foreach (var c in new[] { "Bela_s0", "Stina_s0" }) if (Gfx(cg, c) is { } m) models.Add(m);
        }
        var missing = new List<string>();
        var bad = new List<string>();
        var kinds = new Dictionary<LookKind, int>();
        int skeleton = 0;
        foreach (var m in models)
        {
            var g = Read(t, m);
            if (g == null) { missing.Add(m); continue; }
            var kind = InfectedLookData.KindOf(m, g.Fourcc, g.Skinned);
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            switch (kind)
            {
                case LookKind.Character or LookKind.Undead:
                {
                    var a = InfectedLookData.CharacterWounds(m, b => g.Joints.TryGetValue(b, out var p) ? p : null);
                    if (a == null) { bad.Add($"{m}: not the Norse skeleton"); continue; }
                    // (as InfectedLook.WoundsOf: onto this model's body)
                    a = InfectedLookData.OntoSkin(a, g.Skin);
                    skeleton++;
                    CheckTable(t, a, false);
                    t.True(a.Eyes != null, $"{m}: eyes");
                    OnSkin(t, a, g, 0.45, bad);
                    // the eyes in the head's upper half, in front of its middle
                    var head = g.Joints[InfectedLookData.Bones.Head];
                    var e = a.Eyes!.Value;
                    t.True(e.Z > head.Z && e.Y > head.Y, $"{m}: the eyes in front of the head, above the neck");
                    if (kind == LookKind.Undead) t.True(InfectedLookData.IsUndead(m), $"{m}: undead");
                    break;
                }
                case LookKind.Vehicle or LookKind.Building:
                    t.True(g.Fourcc is "Vehi" or "Ship" or "Misc" or "Char" or "Deko" or "Bldg", $"{m} ({g.Fourcc}): the building look");
                    break;
                case LookKind.Animal:
                {
                    // (Stina on her eusmilus: one animal model)
                    var a = InfectedLookData.Animal(m) ?? InfectedLookData.FromSkin(m, g.Skin);
                    CheckTable(t, a, true);
                    OnSkin(t, a, g, 0.45, bad);
                    break;
                }
                default:
                    bad.Add($"{m} ({g.Fourcc}): drawn as {kind}");
                    break;
            }
        }
        // the look of each kind, the rules of the attached parts
        t.True(InfectedLookData.KindOf("hu_undead_warrior", "Char", true) == LookKind.Undead && InfectedLookData.KindOf("hu_berserk_statue_01", "Char", true) == LookKind.Vehicle
            && InfectedLookData.KindOf("hu_steam_boat", "Ship", true) == LookKind.Vehicle && InfectedLookData.KindOf("flyingtrader", "Misc", true) == LookKind.Vehicle
            && InfectedLookData.KindOf("hu_axe_a", "Misc", false) == LookKind.Building && InfectedLookData.KindOf("megaloceros", "Anim", true) == LookKind.Animal,
            "the kinds: undead, statues, ships, MIRAGE's zeppelin, a lone axe, a mount");
        t.True(InfectedLookData.ChildKind(LookKind.Animal, LookKind.Character) == LookKind.Character && InfectedLookData.ChildKind(LookKind.Animal, LookKind.Building) == LookKind.Vehicle
            && InfectedLookData.ChildKind(LookKind.Character, LookKind.Building) == LookKind.Item && InfectedLookData.ChildKind(LookKind.Building, LookKind.Character) == null
            && InfectedLookData.ChildKind(LookKind.Building, LookKind.Building) == LookKind.Building, "the attached parts: a rider, a build-up, a weapon, a man on a tower, a crane");
        t.True(InfectedLookData.BodyOf("stina_s0").Id == "norse_female" && InfectedLookData.BodyOf("hu_rider_a").Id == "norse_female" && InfectedLookData.BodyOf("bela_s0").Id == "norse_male"
            && InfectedLookData.BodyOf("hu_spearman_s3").Id == "norse_female" && InfectedLookData.BodyOf("hu_warrior_s1").Id == "norse_male",
            "the body types: Stina, the spearwomen and the scout's rider are women, Bela and the warriors men");
        var (m0, _, f0, b0) = InfectedLookData.Effects(LookKind.Undead, 1);
        var (m3, _, f3, b3) = InfectedLookData.Effects(LookKind.Character, 3);
        t.True(m0 == 0 && f0 == 0 && !b0 && m3 == 1 && f3 == 1 && !b3 && InfectedLookData.Effects(LookKind.Item, 3) == (0, false, 0, false)
            && !InfectedLookData.Effects(LookKind.Vehicle, 3).Blight, "the units' effects: no blight; flies and a small cloud on the characters at stage 3; nothing on items");
        t.Log($"{models.Count} Norse unit models: {string.Join(", ", kinds.Select(k => $"{k.Value} {k.Key}"))}, {skeleton} on the Norse skeleton, {missing.Count} not in the data ({string.Join(", ", missing)})");
        t.True(bad.Count == 0, "the Norse units:\n      " + string.Join("\n      ", bad));
    }

    public static void EveryNorseBuilding(TestContext t)
    {
        var cfgs = Configs(t).ToList();
        if (cfgs.Count == 0) t.Skip("PW_DATA / PW_DATA_MIRAGE not set");
        var models = new SortedSet<string>();
        foreach (var (_, tt, cg) in cfgs)
            foreach (var c in Classes(tt, "Hu", "BLDG"))
                if (Gfx(cg, c) is { } m)
                {
                    models.Add(m);
                    foreach (var suf in new[] { "_dest", "_upgrade", "_upgrade_dest", "_upgrade_balista", "_upgrade_balista_dest", "_crane", "_anvil", "_anvil_dest" })
                        if (ModelFile(t, m + suf) != null) models.Add(m + suf);
                }
        var missing = new List<string>();
        int tuned = 0, emitters = 0, green = 0;
        foreach (var m in models)
        {
            var tune = InfectedLookData.TuneFor(m);
            if (tune != InfectedLookData.BuildingTune.Default) tuned++;
            t.True(tune.Slime > 0 && tune.SlimeShare > 0 && tune.SlimeShare <= 0.6 && tune.Blight > 0 && tune.Mist >= 0 && tune.VeinScale > 0, $"{m}: a valid tune ({tune.Family})");
            for (int s = 1; s <= InfectedLookData.MAX_STAGE; s++)
            {
                t.True(InfectedLookData.SlimeOn(10, s, tune) <= 10 * tune.SlimeShare + 1e-9, $"{m}: the slime under its share at stage {s}");
                var (rx, rz) = InfectedLookData.BlightRadii(6, 1, s, tune);
                t.True(rx >= tune.BlightMin && rz >= tune.BlightMin && rx <= rz * 2.5 + 1e-9, $"{m}: the blight at stage {s} ({rx:0.0} x {rz:0.0} m)");
                var (mist, _, flies, blight) = InfectedLookData.Effects(LookKind.Building, s, tune);
                t.True(mist >= 0 && flies >= 0 && blight, $"{m}: its effects at stage {s}");
            }
            var g = Read(t, m);
            if (g == null) { missing.Add(m); continue; }
            t.True(InfectedLookData.KindOf(m, g.Fourcc, g.Skinned) is LookKind.Building or LookKind.Vehicle, $"{m} ({g.Fourcc}): the building look");
            foreach (var e in g.Emitters)
            {
                emitters++;
                bool gr = InfectedLookData.EmitterTurnsGreen(e, tune);
                if (gr) green++;
                string n = e.ToLowerInvariant();
                if (n.Contains("feuer") || n.Contains("fire") || n.Contains("rauch") || n.Contains("smoke")) t.True(gr, $"{m}: its fire / smoke {e} turns green");
                if (n.Contains("fliegen") || n.Contains("nightflies") || n.Contains("fx_dust")) t.True(!gr, $"{m}: its {e} keeps its colours");
            }
        }
        // the families the generic look misread
        t.True(InfectedLookData.TuneFor("hu_palisade_gate_dest").Family == "hu_palisade" && InfectedLookData.TuneFor("hu_large_tower_upgrade_balista_dest").Family == "hu_large_tower"
            && InfectedLookData.TuneFor("hu_rally_point_harbour").Family == "hu_rally_point" && InfectedLookData.TuneFor("hu_weapons_smith_anvil").Family == "hu_weapons_smith"
            && InfectedLookData.TuneFor("aje_fireplace") == InfectedLookData.BuildingTune.Default, "a ruin, upgrade, gate, part: its family's tune");
        var hq = InfectedLookData.Effects(LookKind.Building, 3, InfectedLookData.TuneFor("hu_fireplace"), 9);
        var cottage = InfectedLookData.Effects(LookKind.Building, 3, InfectedLookData.TuneFor("hu_stone_cottage"), 5);
        t.True(hq.Mist == 6 && hq.Big && cottage.Mist < hq.Mist && !cottage.Big, $"a cottage fewer and smaller clouds than the headquarters ({cottage.Mist} / {hq.Mist})");
        t.True(InfectedLookData.SlimeOn(10, 3, InfectedLookData.TuneFor("hu_palisade")) < InfectedLookData.SlimeOn(10, 3) && InfectedLookData.Effects(LookKind.Building, 3, InfectedLookData.TuneFor("hu_rally_point")).Mist == 0,
            "a wall's slime lower than a house's, no clouds at a rally point");
        t.True(!InfectedLookData.EmitterTurnsGreen("particles_wasser_01", InfectedLookData.TuneFor("hu_harbour")) && InfectedLookData.EmitterTurnsGreen("particles_hu_waffenmacher_schornstein_qualm")
            && InfectedLookData.EmitterTurnsGreen("particles_hu_stonecottage_smoke") && !InfectedLookData.EmitterTurnsGreen("particles_aje_Schlachthaus_lecker_Fleischfliegen_01"),
            "the harbour's water stays water, the chimney smoke turns green, the meat flies stay flies");
        t.Log($"{models.Count} Norse building models: {tuned} with a family tune, {emitters} emitters ({green} turn green), {missing.Count} not in the data ({string.Join(", ", missing)})");
    }

    public static void MistRing(TestContext t)
    {
        t.Equal(0, InfectedLookData.MistRing(0, 5).Length, "no clouds");
        var r = InfectedLookData.MistRing(6, 10);
        t.Equal(6, r.Length, "six clouds");
        t.True(r.All(p => { double d = Math.Sqrt(p.X * p.X + p.Z * p.Z); return d >= 10 - 1e-9 && d <= 11.8 + 1e-9; }), "on the ring (every other one 18 % further out)");
        t.True(r.Select(p => Math.Round(Math.Atan2(p.Z, p.X), 3)).Distinct().Count() == 6, "at different angles");
        t.True(r.SequenceEqual(InfectedLookData.MistRing(6, 10)), "deterministic");
        var e = InfectedLookData.MistRing(4, 8, 2, 0);
        t.True(e.All(p => Math.Abs(p.X) <= 8 * 1.18 + 1e-9 && Math.Abs(p.Z) <= 2 * 1.18 + 1e-9) && e.Max(p => Math.Abs(p.X)) > 7, "an ellipse along a wall");
    }
}
