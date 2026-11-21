// Part visibility from the GSF chunk attributes (the engine's "render invisibility mask").
//
// Raw attribute bits of a mesh chunk (verified against the original models, see claude/gsf_format_notes.md):
//   0-4   LOD mask (bit 0 = full detail)            9-13  epoch variants I..V (buildings)
//   5-8   buildings: build-up flags - 5 rises while built, 6 sinks after its level, 7-8 the group (BuildUp)
//   18    selection / pick volume                   19    shadow model
//   17    day-only parts (buildings, ships)         20    night-only lights (the glows and fireflies of lamps)
//   21-24 construction levels 0..3 (ConstructLevel = progress/25)
//   25    finished building                         26    intact   27 damage stage 1   28 damage stage 2
// Animals / vehicles use script flags instead (FightingObj.usl VIS_FLAG_ANML_*):
//   5 party colour, 6 saddle, 7 helmet, 8 armour, 9 standard, 10 armour saddle, 11 misc
//   20..27 wounds (arm l/r, leg l/r, belly l/r, head, tail) shown as hit points drop
//
// Ported from remake/src/engine/parts.js (pw-exporter 98c7703). The functions that walk a model (applyMask,
// applyState, applyNight) work on the part list of a ModelInstance (ModelInstance.ApplyMask / ApplyState); the bit
// logic is here, free of Godot types, so it can be tested on its own.
using System;
using System.Collections.Generic;

namespace PwRemake.View.Parts;

/// <summary>A part's time of day: shown only by night, only by day, or always.</summary>
public enum TimeOfDay : byte { Always = 0, Day = 1, Night = 2 }

public static class PartBits
{
    public const int DAY = 17, PICK = 18, SHADOW = 19, NIGHT = 20, CL0 = 21, FIN = 25, OK = 26, D1 = 27, D2 = 28, AGE1 = 9;

    // animal / vehicle script flags (FightingObj.usl VIS_FLAG_ANML_*)
    public const int PARTYCOL = 5, SADDLE = 6, HELMET = 7, ARMOR = 8, STANDARTE = 9, ARMORSADDLE = 10, MISC = 11;
    /// <summary>VIS_FLAG_CHTR_ACTIVATED: the medic's glowing kit (healing.js setVisFlag(16, ...)).</summary>
    public const int CHTR_ACTIVATED = 16;

    // Animal.usl UpdateHitpoints: wound flag, shown at or below this hit point ratio
    static readonly (int bit, double at)[] WOUNDS = { (20, 0.8), (21, 0.7), (22, 0.6), (23, 0.5), (24, 0.4), (25, 0.3), (27, 0.2) };
    const uint WOUND_ALL = 0x0ff00000;
    const uint GEAR_ALL = 0x7e0;          // bits 5..10 (MISC 11 visible by default)
    /// <summary>"No static signature": visible in every building state.</summary>
    public const int SigAll = 0x1fff;

    // the kinds of model whose bit 20 means "lit at night" (the exporter's parts.py NIGHT)
    static readonly HashSet<string> NIGHT_KINDS = new() { "Bldg", "Deko", "Fiel", "Misc", "Ship" };

    /// <summary>
    /// A part's time of day. Buildings and ships switch them (Building.usl / Ship.usl:
    /// SetRndInvMaskSingleFlagInv(VIS_FLAG_BLDG_NIGHTMODE = 20, night), (VIS_FLAG_BLDG_INIGHTMODE = 17, !night));
    /// everything else keeps the default: the night lights off.
    /// </summary>
    public static TimeOfDay TodOf(uint a, string fourcc)
    {
        if (NIGHT_KINDS.Contains(fourcc) && ((a >> NIGHT) & 1) != 0) return TimeOfDay.Night;
        if ((fourcc == "Bldg" || fourcc == "Ship") && ((a >> DAY) & 1) != 0) return TimeOfDay.Day;
        return TimeOfDay.Always;
    }

    /// <summary>
    /// A gate's end pieces: render flags 7 and 11 (Building.usl CGate SetRndInvMaskSingleFlagInv, docs walls_engine.md
    /// §5.4) - script flags, not LOD or epoch bits. Bit 11 lies in the epoch range (9-13): read as an epoch bit it made
    /// the -x end piece an "epoch III only" part, so outside epoch III one side of a gate never reached its wing.
    /// </summary>
    public const uint GATE_ENDS = 0x880;

    static readonly System.Text.RegularExpressions.Regex WallPartName =
        new(@"^(?:particles_)?(x0[357]?|x1[46]?|x2[57]?|x36|x47|[0-7][a-h])[A-P](?:\d*)$");

    /// <summary>
    /// The wall component ("3b", "x05") of a wall archive's part: its "part" extra ("3bD") or, for a particle emitter
    /// (GSF chunk type 2, the variants G - J of a component: the smoke and fire of the damage stages), its node name
    /// "particles_3bG" (the exporter names emitters by their GSF name and writes no "part" extra for them; Godot's import
    /// appends a number to a duplicate name: "particles_4dI2"). null: not a wall part. The engine switches on every
    /// variant A..P of the named components, emitters included (PWClient 0x4f9100, walls_engine.md §3.4).
    /// </summary>
    public static string? WallComp(string name)
    {
        var m = WallPartName.Match(name);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>the static signature of a part whose model uses some attribute bits as script flags (a gate: GATE_ENDS):
    /// those bits are left out</summary>
    public static int StaticSig(uint a, uint scriptFlags) => StaticSig(a & ~scriptFlags);

    /// <summary>progress/condition/epoch signature of a static part: visible iff all three masks contain the current state</summary>
    public static int StaticSig(uint a)
    {
        int prog = (int)(((a >> 21) & 0xf) | (((a >> 25) & 1) << 4));
        int cond = (int)((a >> 26) & 7);
        int age = (int)((a >> 9) & 0x1f);
        if (prog == 0) prog = 0x1f;
        if (cond == 0) cond = 7;
        if (age == 0) age = 0x1f;
        return prog | (cond << 5) | (age << 8);
    }

    /// <summary>level 0..4 (4 = finished), dmg 0..2, age (epoch) 1..5</summary>
    public static bool SigVisible(int sig, int level, int dmg, int age) =>
        ((sig >> level) & 1) != 0 && ((sig >> (5 + dmg)) & 1) != 0 && ((sig >> (7 + age)) & 1) != 0;

    /// <summary>
    /// Meshes that are never drawn as geometry: "pick", "shadow", "lod" or null.
    /// additive: the chunk's material adds light (GSF material flag 0x4). On decorations the pick bit then marks the
    /// light shapes of an effect, which are what is shown (fx_heal_area, fx_holy_forest, ash_lava_glow_01).
    /// </summary>
    public static string? HelperPart(uint a, string fourcc, bool additive = false)
    {
        if (((a >> PICK) & 1) != 0 && fourcc != "Wall" && !(additive && fourcc == "Deko")) return "pick";
        // (a wall piece's construction parts - the states A, B, C of every arm and hub: levels 0-1, 1-3, 3 - carry the
        // pick and shadow bits too and are drawn: without them a wall under construction showed nothing until finished)
        if (fourcc != "Anim" && fourcc != "Char" && fourcc != "Wall" && ((a >> SHADOW) & 1) != 0) return "shadow";
        // (a wall archive's shadow hull: the shadow bit without the pick bit - the holy city gates' hcl13_gate / hc_gate
        // mesh 18, a pyramid with the placeholder texture test/gates.tga drawn as a brown slab in the gate, sc_4070;
        // every wall piece's construction part carries both)
        if (fourcc == "Wall" && ((a >> SHADOW) & 1) != 0 && ((a >> PICK) & 1) == 0) return "shadow";
        if ((a & 1) == 0) return "lod";
        return null;
    }

    /// <summary>
    /// A building's mesh without any construction state bit (21-25): never drawn. Every building chunk that shows in a
    /// state names its construction levels (the designers set all five, 0x3e00000, for "always"); the chunks without
    /// any are leftovers of the modelling - copies of a state's chunk, often moved a little: the Norse lumberjack
    /// cottage's ("_f0" meshes 79, 81, 84, 101, 103, 104, 120, 122, 126 and mesh 70: the roof's ridge beam and boards
    /// floating over its foundation, copies of its ornaments - the report "the cottage shows duplicate parts"), the
    /// large tower's second ring of dragon heads, the weapon smith's banners and chimney cap, the temple's, harbour's and
    /// tavern's moved copies, the tesla tower cannon's damage parts, the SEAS carrier's own turret. Drawn with "no bits = every state" they doubled those parts in every state. The check over every
    /// building of both configurations: docs/VIEW.md "Leftover chunks", ModelViewTest / tests ViewPartsTests.
    /// (Billboards and emitters keep the old rule: the ruins' dust emitters of the *_dem_fx models have no bits.)
    /// </summary>
    public static bool Unstaged(uint a, string fourcc) => fourcc == "Bldg" && ((a >> CL0) & 0x1f) == 0;

    public static bool IsDynamicKind(string fourcc) => fourcc == "Anim" || fourcc == "Vehi" || fourcc == "Char";

    /// <summary>Hide mask for animals/vehicles: gear flags as set by the scripts, wounds by the hit point ratio.</summary>
    public static uint AnimalMask(in AnimalLook o)
    {
        uint hide = GEAR_ALL | WOUND_ALL | (1u << PICK) | (1u << SHADOW);
        void Show(int b) { hide &= ~(1u << b); }
        if (o.Owned) { Show(SADDLE); Show(PARTYCOL); }
        if (o.Armor) Show(ARMOR);
        if (o.Gear != null) foreach (var b in o.Gear) Show(b);    // helmet, armour, armoured saddle, standard (compose.js GEAR)
        if (o.NoSaddle) hide |= 1u << SADDLE;
        if (o.Misc == false) hide |= 1u << MISC;
        double hr = o.Hp ?? 1;
        foreach (var (b, t) in WOUNDS) if (hr <= t) Show(b);
        return hide;
    }

    public static int WoundStage(double hr) { int n = 0; foreach (var (_, t) in WOUNDS) if (hr <= t) n++; return n; }

    /// <summary>
    /// Is a dynamic part (animal / vehicle / character mesh) shown under a hide mask? Animals/vehicles/characters
    /// have a 5-bit LOD mask (bits 0-4); the script flags start at bit 5 (party colour, saddle ...). An earlier version
    /// ignored bits 0-8 here, which also ignored the party colour / saddle / helmet / armour flags - wild animals then
    /// wore their harness.
    /// </summary>
    public static bool MaskVisible(uint attr, uint hide) => (attr & hide & ~0x1fu) == 0;
}

/// <summary>
/// How a building's parts come while it is built - the engine's, not a script's (PWClient 0x46e9b0, 0x7aa060, 0x7a9d30,
/// 0x46fe90; Building.usl / BuildUpBuilding.usl only set the construction level, progress / 25, and CurProcess):
///   - The parts of the next level that its current level lacks rise out of the ground while the level is built - when
///     they carry the model kind's "appear" flag. Each group of them (the "group" bits) rises as one: lowered by the
///     highest top of the group (its chunks' boxes, GSF z) times 1 - f, f = CurProcess / 100 * 4 - level (0..1), not
///     drawn at f = 0.
///   - The parts of the last level that the current one lacks sink into the ground in the half second after the level
///     changed - when they carry the "disappear" flag (scaffolding): lowered by their group's highest top times the
///     time / 0.5. The others go at once.
///   - Parts of neither level are not drawn; a part of the next level without the "appear" flag waits for its level.
/// The flags, by the model's kind: Bldg appear 0x20, disappear 0x40, group 0x180 (bits 5-8 - beside the LOD bits 0-4);
/// Wall 0x40000 / 0x80000 / 0x60000000 (bits 18, 19: every wall piece's construction parts carry both); Fiel 0x200 /
/// 0x400 / 0x1800. The engine does this for buildings and turrets (object class BLDG, TRRT) under construction and in
/// the half second after they are finished. ModelInstance.BuildUp.cs draws it.
/// </summary>
public static class BuildUp
{
    public readonly record struct Masks(uint Group, uint Appear, uint Disappear);

    /// <summary>the flags of a model kind; null: its parts do not move (only buildings, walls and fields)</summary>
    public static Masks? Of(string fourcc) => fourcc switch
    {
        "Bldg" => new Masks(0x180, 0x20, 0x40),
        "Wall" => new Masks(0x60000000, 0x40000, 0x80000),
        "Fiel" => new Masks(0x1800, 0x200, 0x400),
        _ => null,
    };

    /// <summary>the bits that keep parts apart when a model's static parts are merged</summary>
    public static uint Keep(string fourcc) => Of(fourcc) is { } m ? m.Group | m.Appear | m.Disappear : 0;

    /// <summary>how long the parts of the last level take to sink (s; PWClient 0x46ff21: (now - change) / 0.5)</summary>
    public const double SINK_TIME = 0.5;

    /// <summary>f: how far the current level is built, 0..1 (the engine reads CurProcess, a whole percent)</summary>
    public static double StageFraction(double progress, int level) => Math.Clamp(Math.Round(progress * 100) / 100 * 4 - level, 0, 1);

    /// <summary>what a transition from level a to level b does with a part: 0 nothing, 1 rises, -1 sinks, 2 not drawn
    /// (PWClient 0x7aa060: in a and in b nothing; only in b: rises with the appear flag; only in a: sinks with the
    /// disappear flag, else not drawn when hideRest; in neither: not drawn)</summary>
    public static int Transition(bool inA, bool inB, bool appear, bool disappear, bool hideRest)
    {
        if (inA && inB) return 0;
        if (inB) return appear ? 1 : 0;
        if (inA) return disappear ? -1 : hideRest ? 2 : 0;
        return 2;
    }
}

/// <summary>What an animal or vehicle wears (parts.js animalMask opts).</summary>
public struct AnimalLook
{
    /// <summary>Owned by a player (not wild): saddle and party colour shown.</summary>
    public bool Owned;
    public bool Armor;
    /// <summary>Extra flags to show (PartBits.HELMET, ARMOR, ARMORSADDLE, STANDARTE ...).</summary>
    public int[]? Gear;
    public bool NoSaddle;
    /// <summary>null = default (misc shown), false = hidden.</summary>
    public bool? Misc;
    /// <summary>Hit point ratio 0..1 (null = 1): wounds show as it drops.</summary>
    public double? Hp;
}
