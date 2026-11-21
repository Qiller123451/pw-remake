// A particle emitter of a model, as the converter wrote it (pwexport/gsf.py parse_particle: GSF chunk type 2, glTF
// node extras {kind: 'particles', attr, emitter: {src, scale, max, spread, tiles: [{tex, tiles_w_log2, ...}],
// params: {size, life_time, flags, ...}, anims}}). Read once per template (ModelLibrary) and shared by every
// instance; Particles runs it (particle source 3, docs/spec/particles.md of the browser remake).
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.View.Fx;

public sealed class EmitterDef
{
    /// <summary>the emitter's vector ("scale" in the file: m_vVel): x the start disk's radius, z the speed along its axis</summary>
    public float ScaleX, ScaleY, ScaleZ;
    public int Max = 1;
    public float Spread;
    /// <summary>the sprite sheet (path relative to assets/models/, "../tex/gore.png"), its grid, tiles, pivot, blending</summary>
    public string Tex = "";
    public int TilesWLog2, TilesHLog2, TileStart, TileLast;
    public int MidX = 127, MidY = 127;
    /// <summary>drawn added to the screen, the texture's alpha unused (fire, sparks, rays); false: mixed by the texture's
    /// alpha (smoke, steam, dust). The original's rule, from the sheet's GSF material: ResolveBlend</summary>
    public bool Additive;
    /// <summary>the GSF material flags (attr1) the blending was taken from (ResolveBlend; 0: not known)</summary>
    public uint GsfFlags;
    /// <summary>clips that make the emitter spray (a finishing move's blood, an effect's "anim"), lower case; null = always</summary>
    public HashSet<string>? Anims;
    // params
    public float Size = 1, LifeTime, Rotate, Gravitation, SizeVar, Drag, FadeIn, FadeOut, TileAnimSpeed = 1, LifeTimeVar,
        SizeIncrease, EmitTimer, SpeedVar, EmitTimerVar, EmitRotInc, SrcRotSpeed, CenterSuck, SuckVel = 1, RotationVar,
        EmitPerMeter, SizeIncExp;
    public float ColorR = 255, ColorG = 255, ColorB = 255, Alpha = 255;
    public int ColorVarR, ColorVarG, ColorVarB;
    public uint Flags;

    /// <summary>tiles in the sheet's animation (tile_last + 1) and the grid's width</summary>
    public int Count => TileLast + 1;
    public int GridW => 1 << TilesWLog2;
    public int GridH => 1 << TilesHLog2;

    /// <summary>parses the emitter object of the node extras (null: no texture - nothing to draw)</summary>
    public static EmitterDef? FromJson(JsonObject? e)
    {
        if (e == null) return null;
        var t = (e["tiles"] as JsonArray)?.Count > 0 ? e["tiles"]![0] as JsonObject : null;
        if (t == null || t["tex"].Str().Length == 0) return null;
        var d = new EmitterDef();
        if (e["scale"] is JsonArray sc && sc.Count >= 3) { d.ScaleX = (float)sc[0].Num(); d.ScaleY = (float)sc[1].Num(); d.ScaleZ = (float)sc[2].Num(); }
        d.Max = Math.Max(1, (int)e["max"].Num(1));
        d.Spread = (float)e["spread"].Num();
        d.Tex = t["tex"].Str();
        d.TilesWLog2 = (int)t["tiles_w_log2"].Num(); d.TilesHLog2 = (int)t["tiles_h_log2"].Num();
        d.TileStart = (int)t["tile_start"].Num(); d.TileLast = (int)t["tile_last"].Num();
        d.MidX = (int)t["mid_x"].Num(127); d.MidY = (int)t["mid_y"].Num(127);
        d.Additive = t["additive"].Bool();
        if (e["anims"] is JsonArray an && an.Count > 0)
        {
            d.Anims = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in an) d.Anims.Add(a.Str().ToLowerInvariant());
        }
        var P = e["params"] as JsonObject ?? new JsonObject();
        float F(string k, double def = 0) => (float)P[k].Num(def);
        d.Size = F("size", 1); d.LifeTime = F("life_time"); d.Rotate = F("rotate"); d.Gravitation = F("gravitation");
        d.SizeVar = F("size_var"); d.Drag = F("drag"); d.FadeIn = F("fade_in"); d.FadeOut = F("fade_out");
        d.TileAnimSpeed = F("tile_anim_speed", 1); d.LifeTimeVar = F("life_time_var"); d.SizeIncrease = F("size_increase");
        d.EmitTimer = F("emit_timer"); d.SpeedVar = F("speed_var"); d.EmitTimerVar = F("emit_timer_var");
        d.EmitRotInc = F("emit_rot_inc"); d.SrcRotSpeed = F("src_rot_speed"); d.CenterSuck = F("center_suck");
        d.SuckVel = F("suck_vel", 1); d.RotationVar = F("rotation_var"); d.EmitPerMeter = F("emit_per_meter");
        d.SizeIncExp = F("size_inc_exp");
        d.ColorR = F("color_r", 255); d.ColorG = F("color_g", 255); d.ColorB = F("color_b", 255); d.Alpha = F("alpha", 255);
        d.ColorVarR = (int)F("color_var_r"); d.ColorVarG = (int)F("color_var_g"); d.ColorVarB = (int)F("color_var_b");
        d.Flags = (uint)(long)P["flags"].Num();
        if (d.Size == 0) d.Size = 1;           // (the JS: P.size || 1)
        return d;
    }

    // ------------------------------------------------------------------------------------------------ blending

    /// <summary>
    /// The original's blending of a particle sheet by its GSF material's flags (attr1). PWClient.exe 0x6c767f (the
    /// particle material made of a GSF material): a material with alpha - alpha test 0x1 or alpha blend 0x2 - becomes
    /// "&lt;texture&gt;_pm" (premultiplied: the renderer 0x704d40 multiplies the colour by the alpha and mixes it,
    /// ONE / INVSRCALPHA - the texture's alpha shapes the sprite); one without becomes "&lt;texture&gt;_add" (handle |
    /// 0x80000000: the renderer's additive path 0x705329, alpha 0 - the sheet is added, its alpha unused). The flag
    /// 0x4 ("additive / self illuminated" of the meshes) is not looked at for particles: the SEAS buildings' steam
    /// (particle_01.tga, flags 0x106) is a mixed sprite in the original.
    /// </summary>
    public static bool OriginalAdditive(uint attr1) => (attr1 & 0x3) == 0;

    /// <summary>how the blending of an emitter was found (ResolveBlend)</summary>
    public enum Blend { Kept, Material, Changed, Ambiguous }

    /// <summary>
    /// Sets Additive by the original's rule (OriginalAdditive) from the model's materials: (image URI, GSF attr1) of
    /// each material of the .glb. The converter (pwexport gsf.py) writes the tile's "additive" from the material flag
    /// 0x4 and drops the material's index, so the emitter's material is the one with the emitter's sheet and that
    /// bit as written (in the export every emitter has exactly one such material, or several that agree). Without one
    /// (no materials, an older export without gsf_flags) the converter's flag is kept; materials that disagree keep it
    /// too (Ambiguous).
    /// </summary>
    public Blend ResolveBlend(IReadOnlyList<(string? Uri, uint Flags)> materials)
    {
        uint found = 0;
        bool any = false, add = false;
        foreach (var (uri, f) in materials)
        {
            if (f == 0 || uri == null || !string.Equals(uri, Tex, StringComparison.OrdinalIgnoreCase)) continue;
            if (((f & 0x4) != 0) != Additive) continue;
            bool a = OriginalAdditive(f);
            if (any && a != add) return Blend.Ambiguous;
            any = true; add = a; found = f;
        }
        if (!any) return Blend.Kept;
        GsfFlags = found;
        if (add == Additive) return Blend.Material;
        Additive = add;
        return Blend.Changed;
    }

    /// <summary>(image URI of the base colour texture, GSF attr1 of the extras "gsf_flags") of each material of a glTF
    /// JSON (the root object: materials, textures, images)</summary>
    public static List<(string? Uri, uint Flags)> MaterialsOf(JsonObject gltf)
    {
        var r = new List<(string?, uint)>();
        if (gltf["materials"] is not JsonArray mats) return r;
        var texs = gltf["textures"] as JsonArray; var imgs = gltf["images"] as JsonArray;
        foreach (var m in mats)
        {
            string? uri = null;
            if (m?["pbrMetallicRoughness"]?["baseColorTexture"]?["index"] is JsonNode ix && texs?[ix.Int()]?["source"] is JsonNode src)
            {
                uri = imgs?[src.Int()]?["uri"].Str();
                if (string.IsNullOrEmpty(uri)) uri = null;
            }
            string fl = m?["extras"]?["gsf_flags"].Str() ?? "";
            uint v = 0;
            if (fl.Length > 0) uint.TryParse(fl.Split('/')[0], System.Globalization.NumberStyles.HexNumber, null, out v);
            r.Add((uri, v));
        }
        return r;
    }
}
