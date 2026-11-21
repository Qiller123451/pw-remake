// The particle emitters' sheets (docs/EFFECTS.md "Blending: the original's material rule"): every emitter of every
// model of the data (PW_DATA, PW_DATA_MIRAGE) has its sheet, gets the original's blending from its GSF material
// (EmitterDef.ResolveBlend: alpha bits 0x3 of attr1, PWClient.exe 0x6c767f - not the converter's bit 0x4), and its
// cells drawn that way have no visible square edge: a mixed sprite's cell is transparent at its border, an additive
// one's dark there. Until 2026-11-21 the SEAS buildings' steam (particle_01.tga cells 16-21, GSF flags 0x106: grey
// on its border, the puff only in the alpha) was drawn additive with the alpha unused - bright squares over the
// chimneys (Kacper: "smoke is wrong, it's just a bunch of squares"; MIRAGE, Icewaste, Outbreak).
//
// The test lists every emitter whose sheet is missing, whose materials disagree, or whose cells show their square,
// with the model, emitter, sheet, cell and blending; it fails when there is any.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.View.Fx;
using PwRemake.View.Models;

namespace PwRemake.Tests;

[TestClass]
public static class EmitterSheetsTests
{
    /// <summary>the border of a cell counts as a visible edge above this (mean alpha of a mixed sheet, mean brightest
    /// channel of an additive one, 0 .. 1)</summary>
    const double EDGE = 0.25;

    /// <summary>the original's rule and its match to the emitter's material (no data needed)</summary>
    public static void BlendRule(TestContext t)
    {
        t.True(EmitterDef.OriginalAdditive(0x104), "0x104 (gore.tga: no alpha bit) is additive");
        t.True(EmitterDef.OriginalAdditive(0x1104), "0x1104 (player colour, no alpha bit) is additive");
        t.True(!EmitterDef.OriginalAdditive(0x106), "0x106 (alpha blend + 0x4) is mixed: the SEAS steam");
        t.True(!EmitterDef.OriginalAdditive(0x105), "0x105 (alpha test + 0x4) is mixed: the lamps' night flies");
        t.True(!EmitterDef.OriginalAdditive(0x102), "0x102 is mixed");
        t.True(EmitterDef.OriginalAdditive(0x100), "0x100 (no alpha bit, no 0x4) is additive");
        var mats = new List<(string?, uint)> { ("../tex/particle_01.png", 0x102), ("../tex/particle_01.png", 0x106), ("../tex/gore.png", 0x104), (null, 0) };
        var steam = new EmitterDef { Tex = "../tex/particle_01.png", Additive = true };
        t.Equal(EmitterDef.Blend.Changed, steam.ResolveBlend(mats), "the steam's material 0x106 found");
        t.True(!steam.Additive && steam.GsfFlags == 0x106, "the steam is mixed");
        var smoke = new EmitterDef { Tex = "../tex/particle_01.png", Additive = false };
        t.Equal(EmitterDef.Blend.Material, smoke.ResolveBlend(mats), "the smoke's material 0x102");
        var fire = new EmitterDef { Tex = "../tex/gore.png", Additive = true };
        t.Equal(EmitterDef.Blend.Material, fire.ResolveBlend(mats), "the fire's material 0x104");
        t.True(fire.Additive, "the fire stays additive");
        var none = new EmitterDef { Tex = "../tex/rays.png", Additive = true };
        t.Equal(EmitterDef.Blend.Kept, none.ResolveBlend(mats), "no material of the sheet: the converter's flag kept");
        var two = new EmitterDef { Tex = "../tex/particle_01.png", Additive = true };
        t.Equal(EmitterDef.Blend.Ambiguous, two.ResolveBlend(new List<(string?, uint)> { ("../tex/particle_01.png", 0x104), ("../tex/particle_01.png", 0x106) }),
            "materials that disagree");
    }

    /// <summary>every emitter of the data: sheet found, blending from its material, no square edges</summary>
    public static void EmitterSheets(TestContext t)
    {
        var cfgs = new List<(string Name, string Dir)>();
        foreach (var (v, n) in new[] { ("PW_DATA", "base"), ("PW_DATA_MIRAGE", "mirage") })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } d && Directory.Exists(Path.Combine(d, "assets", "models"))) cfgs.Add((n, d));
        if (cfgs.Count == 0) t.Skip("PW_DATA / PW_DATA_MIRAGE not set");
        // (a configuration's sheets, then the base data's: the MIRAGE data here may hold only what it changes)
        var texDirs = cfgs.Select(c => Path.Combine(c.Dir, "assets", "tex")).ToList();
        var sheets = new Dictionary<string, Png?>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var changed = new List<string>();
        int emitters = 0, models = 0, dots = 0;
        var blends = new Dictionary<string, int>();
        foreach (var (cfg, dir) in cfgs)
            foreach (var f in Directory.GetFiles(Path.Combine(dir, "assets", "models"), "*.glb").OrderBy(x => x, StringComparer.Ordinal))
            {
                var g = GlbJson.Read(f);
                List<(string?, uint)>? mats = null;
                bool any = false;
                string model = cfg + ":" + Path.GetFileNameWithoutExtension(f);
                foreach (var n in g.Nodes)
                {
                    if (n?["extras"] is not JsonObject x || x["kind"]?.GetValue<string>() != "particles") continue;
                    var e = EmitterDef.FromJson(x["emitter"] as JsonObject);
                    if (e == null) continue;
                    any = true; emitters++;
                    string name = model + "/" + n["name"]?.GetValue<string>();
                    bool was = e.Additive;
                    var b = e.ResolveBlend(mats ??= EmitterDef.MaterialsOf(g.Root));
                    string kind = (e.Additive ? "add" : "mix") + $" 0x{e.GsfFlags:x}";
                    blends[kind] = blends.GetValueOrDefault(kind) + 1;
                    if (b == EmitterDef.Blend.Ambiguous) { problems.Add($"{name}: the materials of {e.Tex} disagree on the blending"); continue; }
                    if (b == EmitterDef.Blend.Changed) changed.Add($"{name} ({Path.GetFileName(e.Tex)} 0x{e.GsfFlags:x}: {(was ? "add" : "mix")} -> {(e.Additive ? "add" : "mix")})");
                    string file = Path.GetFileName(e.Tex.Replace('\\', '/'));
                    if (!sheets.TryGetValue(file, out var png))
                    {
                        var p = texDirs.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists);
                        sheets[file] = png = p == null ? null : Png.Read(p);
                    }
                    if (png == null) { problems.Add($"{name}: sheet {e.Tex} missing (assets/tex/{file})"); continue; }
                    // (a sprite under 10 cm high, never growing: a dot - hu_temple's truncated night flies, size 0.1)
                    if (e.Size < 0.4f && e.SizeIncrease <= 0) { dots++; continue; }
                    int gw = e.GridW, gh = e.GridH, cw = png.W / gw, ch = png.H / gh;
                    for (int c = e.TileStart; c < e.TileStart + e.Count && c < gw * gh; c++)
                    {
                        double edge = png.Border((c % gw) * cw, (c / gw) * ch, cw, ch, e.Additive);
                        if (edge > EDGE)
                        {
                            problems.Add($"{name}: {file} cell {c} drawn {(e.Additive ? "additive" : "mixed")} (GSF 0x{e.GsfFlags:x}) shows its square: " +
                                $"border {(e.Additive ? "brightness" : "alpha")} {edge:0.00}");
                            break;
                        }
                    }
                }
                if (any) models++;
            }
        t.Log($"{emitters} emitters of {models} models, {sheets.Count} sheets ({sheets.Count(s => s.Value == null)} missing), {dots} dots not looked at");
        t.Log("blendings (by the GSF material): " + string.Join(", ", blends.OrderByDescending(k => k.Value).Select(k => $"{k.Key}: {k.Value}")));
        t.Log($"{changed.Count} emitters drawn other than the converter's flag says:");
        foreach (var s in changed) t.Log("  " + s);
        foreach (var s in problems) t.Log("PROBLEM " + s);
        if (cfgs.Any(c => c.Name == "base") && File.Exists(Path.Combine(cfgs.First(c => c.Name == "base").Dir, "assets", "models", "seas_greenhouse.glb")))
            t.True(changed.Any(s => s.StartsWith("base:seas_greenhouse/particles_steam01 ", StringComparison.Ordinal) && s.EndsWith("add -> mix)", StringComparison.Ordinal)),
                "seas_greenhouse's steam is drawn mixed (GSF 0x106)");
        t.Equal(0, problems.Count, "emitters with a missing sheet, disagreeing materials or square sprites");
    }

    /// <summary>a PNG of 8 bits per channel (grey, grey + alpha, RGB, RGBA), not interlaced: the converted sheets</summary>
    sealed class Png
    {
        public int W, H;
        byte[] _rgba = Array.Empty<byte>();

        public static Png Read(string path)
        {
            var b = File.ReadAllBytes(path);
            int pos = 8, w = 0, h = 0, ct = 0;
            var idat = new MemoryStream();
            while (pos + 8 <= b.Length)
            {
                int len = (b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
                string type = System.Text.Encoding.ASCII.GetString(b, pos + 4, 4);
                int d = pos + 8;
                if (type == "IHDR")
                {
                    w = (b[d] << 24) | (b[d + 1] << 16) | (b[d + 2] << 8) | b[d + 3];
                    h = (b[d + 4] << 24) | (b[d + 5] << 16) | (b[d + 6] << 8) | b[d + 7];
                    if (b[d + 8] != 8 || b[d + 12] != 0) throw new InvalidDataException($"{path}: only 8 bits, not interlaced");
                    ct = b[d + 9];
                }
                else if (type == "IDAT") idat.Write(b, d, len);
                else if (type == "IEND") break;
                pos = d + len + 4;
            }
            int ch = ct switch { 0 => 1, 4 => 2, 2 => 3, 6 => 4, _ => throw new InvalidDataException($"{path}: colour type {ct}") };
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            var raw = new MemoryStream(); z.CopyTo(raw);
            var r = raw.ToArray();
            int stride = w * ch;
            var px = new byte[stride * h];
            var prev = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                int ft = r[y * (stride + 1)], o = y * (stride + 1) + 1;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= ch ? px[y * stride + i - ch] : 0, up = prev[i], c = i >= ch ? prev[i - ch] : 0, v = r[o + i];
                    v += ft switch
                    {
                        1 => a, 2 => up, 3 => (a + up) / 2,
                        4 => Paeth(a, up, c),
                        _ => 0,
                    };
                    px[y * stride + i] = (byte)v;
                }
                Array.Copy(px, y * stride, prev, 0, stride);
            }
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                int s = i * ch;
                byte R = px[s], G = ch >= 3 ? px[s + 1] : R, B = ch >= 3 ? px[s + 2] : R, A = ch == 4 ? px[s + 3] : ch == 2 ? px[s + 1] : (byte)255;
                rgba[i * 4] = R; rgba[i * 4 + 1] = G; rgba[i * 4 + 2] = B; rgba[i * 4 + 3] = A;
            }
            return new Png { W = w, H = h, _rgba = rgba };
        }

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        /// <summary>the mean over the border pixels of a cell: its alpha (mixed) or its brightest channel (additive), 0 .. 1</summary>
        public double Border(int x0, int y0, int w, int h, bool additive)
        {
            double sum = 0; int n = 0;
            void At(int x, int y)
            {
                int i = ((y0 + y) * W + x0 + x) * 4;
                sum += additive ? Math.Max(_rgba[i], Math.Max(_rgba[i + 1], _rgba[i + 2])) / 255.0 : _rgba[i + 3] / 255.0;
                n++;
            }
            for (int x = 0; x < w; x++) { At(x, 0); At(x, h - 1); }
            for (int y = 1; y < h - 1; y++) { At(0, y); At(w - 1, y); }
            return n > 0 ? sum / n : 0;
        }
    }
}
