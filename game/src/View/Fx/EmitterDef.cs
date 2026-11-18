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
    public bool Additive;
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
}
