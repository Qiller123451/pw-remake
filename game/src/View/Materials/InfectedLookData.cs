// The "infected" look's numbers (docs/INFECTED_LOOK.md): what each infection stage shows, which look a model wears
// (building, vehicle, animal, character, undead, item: KindOf), how each Norse building family wears it (BuildingTune),
// which emitters turn green, and where the torn wounds of the wild animals (the table, FromSkin) and of the Norse
// characters (per skeleton: CharacterWounds) are. No Godot types (the tests compile this file: tests/PwRemake.Tests/InfectedLookTests.cs);
// InfectedLook (materials) and ModelInstance.SetInfected (effects) read it.
//
// Kacper 2026-11-19, for a mission in the spirit of StarCraft 2's "Outbreak": "give a new texture/effects for infected
// buildings/units ... some green/decay effect like in Warcraft 3 ... For animals make them look like having parts of
// their body torn off, seeing the bones/blood, different colour than usual." And (23:45): "also add other buildings
// units too" - every Norse building and unit of both configurations and every wild animal. View only: nothing here touches the
// simulation, the stage is a drawing option of a model (ModelInstance.SetInfected).
//
// The colours follow the original's own undead: the Norse undead warrior / killer (spec_hu_undead.png,
// spec_hu_undead_killer.png - grey-olive skin, moss-green on grey stone, green-black shadows) and Schliemann's zombie
// (schliemann_zombie.png - sickly yellow-green skin); InfectedLook keeps them as sRGB constants.
using System;
using System.Collections.Generic;
using System.Linq;

namespace PwRemake.View.Materials;

/// <summary>What is left inside a torn wound (the surface is cut away elsewhere).</summary>
public enum WoundKind
{
    /// <summary>rib bands across the body's length (a flank)</summary>
    Ribs,
    /// <summary>a spine along the body's length with its vertebrae (the back, a tail)</summary>
    Spine,
    /// <summary>one bone along the vertical (a leg, a neck seen from the side)</summary>
    Bone,
    /// <summary>no bone: torn flesh round a hole</summary>
    Flesh,
}

/// <summary>
/// A torn wound: an ellipsoid in the model's own frame (the GSF frame of the converted glTF, metres: x the side - +x
/// the model's left as drawn, y forward - the head, z up - 0 the ground under the feet), in the bind pose, so it moves
/// with the animation (the shader reads each vertex's bind position). From stage `Stage` on.
/// </summary>
public readonly record struct Wound(double X, double Y, double Z, double Rx, double Ry, double Rz, WoundKind Kind, int Stage);

/// <summary>The eyes of an animal (bind pose, model frame as Wound): two spheres at (Cx +- Dx, Y, Z), radius R - the
/// head's middle Cx (a bind pose need not hold the head straight: the allosaurus' looks 0.6 m to its left). Measured
/// on side views of the heads (InfectedLookTest --only=eyes) and the skin's width there.</summary>
public readonly record struct EyePair(double Cx, double Dx, double Y, double Z, double R);

/// <summary>An animal's wound table entry.</summary>
public sealed record AnimalWounds(string Model, Wound[] Wounds, EyePair? Eyes);

/// <summary>What one infection stage shows (InfectedLookData.Stage).</summary>
public readonly record struct InfectionStage
{
    public int Stage { get; init; }
    /// <summary>0..1: how far the surfaces are rotten (desaturated, darkened to the undead's grey-olive)</summary>
    public double Rot { get; init; }
    /// <summary>m: how high the slime and moss creep up a building from the ground (at most SLIME_MAX_SHARE of its height)</summary>
    public double SlimeHeight { get; init; }
    /// <summary>0..1: moss on the upward faces (roofs, ledges) above the slime</summary>
    public double Moss { get; init; }
    /// <summary>0..1: the glowing green veins / cracks (emissive, pulsing)</summary>
    public double Veins { get; init; }
    /// <summary>fires, lights and glows of the model turned green (Particles, the glow materials)</summary>
    public bool GreenFire { get; init; }
    /// <summary>the original's poison clouds round a building's foot (fx_poison_big; fx_poison on an animal)</summary>
    public int Mist { get; init; }
    /// <summary>emitters of the original's meat flies (hu_lumberjack_cottage's "Fleischfliegen") about the model</summary>
    public int Flies { get; init; }
    /// <summary>the blight on the ground round a building: its radius as a share of the building's radius (0 = none)</summary>
    public double Blight { get; init; }
    /// <summary>0..1: an animal's skin turned pale grey-green</summary>
    public double Skin { get; init; }
    /// <summary>0..1: dark bruised patches on an animal</summary>
    public double Bruises { get; init; }
    /// <summary>green glowing eyes (animals with eyes in the table)</summary>
    public bool Eyes { get; init; }
}

public static class InfectedLookData
{
    /// <summary>the stages: 0 healthy (the normal look), 1 freshly infected, 2 spreading, 3 fully overrun</summary>
    public const int MAX_STAGE = 3;
    /// <summary>wounds per model the shader evaluates at most (uniform arrays)</summary>
    public const int MAX_WOUNDS = 6;
    /// <summary>the slime never climbs higher than this share of a building's height</summary>
    public const double SLIME_MAX_SHARE = 0.45;

    static readonly InfectionStage[] STAGES =
    {
        new() { Stage = 0 },
        // freshly infected: a little rot, a band of slime at the foot, the first veins, a cloud and some flies
        new() { Stage = 1, Rot = 0.35, SlimeHeight = 1.2, Moss = 0.15, Veins = 0.35, GreenFire = false, Mist = 1, Flies = 1, Blight = 0.9,
                Skin = 0.45, Bruises = 0.35, Eyes = false },
        // spreading: rotten wood and stone, slime to the windows, glowing veins, the fires turned green
        new() { Stage = 2, Rot = 0.65, SlimeHeight = 3.0, Moss = 0.45, Veins = 0.7, GreenFire = true, Mist = 3, Flies = 2, Blight = 1.2,
                Skin = 0.8, Bruises = 0.7, Eyes = true },
        // fully overrun: almost black-green, slime half way up, bright veins, a ring of poison, the whole blight
        new() { Stage = 3, Rot = 0.9, SlimeHeight = 6.0, Moss = 0.8, Veins = 1.0, GreenFire = true, Mist = 6, Flies = 3, Blight = 1.5,
                Skin = 1.0, Bruises = 1.0, Eyes = true },
    };

    /// <summary>the look of a stage (clamped to 0..MAX_STAGE)</summary>
    public static InfectionStage Stage(int stage) => STAGES[Math.Clamp(stage, 0, MAX_STAGE)];

    /// <summary>how high the slime climbs on a building of this height at a stage (m)</summary>
    public static double SlimeOn(double buildingHeight, int stage) =>
        Math.Min(Stage(stage).SlimeHeight, Math.Max(0, buildingHeight) * SLIME_MAX_SHARE);

    // ------------------------------------------------------------------------------------------- the wound table
    //
    // Per animal model, in its bind pose (the converted glTF's frame; the slices of the skin meshes along y gave the
    // body: e.g. the triceratops' trunk y -4 .. 4.7, back at z 6.2, flanks at x +-2). Placed where the RTS camera sees
    // them - the upper flanks and the back, both sides (an animal turns) - and large enough to read at game zoom (the
    // small ones relatively larger: a raptor is 3 m long, 50 pixels at the default zoom); the
    // first of each list shows from stage 1, the others from 2 or 3. Checked in the test scene (InfectedLookTest
    // --only=close --markers draws the ellipsoids' centres).
    static readonly AnimalWounds[] TABLE =
    {
        new("velociraptor", new Wound[]
        {
            new(-0.30,  0.15,  1.45, 0.38, 0.70, 0.48, WoundKind.Ribs, 1),  // left flank
            new( 0.28,  0.40,  1.40, 0.36, 0.62, 0.46, WoundKind.Ribs, 2),  // right flank
            new( 0.00, -1.10,  1.80, 0.30, 0.75, 0.30, WoundKind.Spine, 2), // the tail's root, from above
            new( 0.12,  1.55,  2.15, 0.26, 0.36, 0.32, WoundKind.Flesh, 3), // the neck
            new(-0.22, -0.15,  1.05, 0.24, 0.34, 0.36, WoundKind.Bone, 3),  // the left thigh
        }, new EyePair(0.11, 0.12, 1.94, 2.32, 0.10)),
        new("smilodon", new Wound[]
        {
            new(-0.78,  0.75,  2.35, 0.50, 1.05, 0.76, WoundKind.Ribs, 1),  // left flank
            new( 0.00, -0.40,  3.05, 0.52, 0.98, 0.40, WoundKind.Spine, 2), // the back
            new( 0.78,  1.70,  2.40, 0.46, 0.70, 0.68, WoundKind.Ribs, 2),  // right shoulder / flank
            new( 0.40,  2.95,  2.85, 0.42, 0.46, 0.46, WoundKind.Flesh, 3), // the neck
            new( 0.55, -1.20,  1.40, 0.36, 0.44, 0.58, WoundKind.Bone, 3),  // the right hind leg
        }, new EyePair(0.11, 0.33, 3.57, 3.25, 0.13)),
        new("eusmilus", new Wound[]
        {
            new(-0.50,  0.10,  2.15, 0.42, 0.90, 0.62, WoundKind.Ribs, 1),  // left flank
            new( 0.00, -0.90,  2.75, 0.46, 0.85, 0.36, WoundKind.Spine, 2), // the back
            new( 0.50,  0.60,  2.10, 0.42, 0.65, 0.62, WoundKind.Ribs, 2),  // right flank
            new( 0.35,  1.80,  2.50, 0.36, 0.42, 0.46, WoundKind.Flesh, 3), // the neck
            new( 0.35, -1.50,  1.00, 0.30, 0.34, 0.50, WoundKind.Bone, 3),  // the right hind leg
        }, new EyePair(0.09, 0.30, 2.95, 2.75, 0.11)),
        new("triceratops", new Wound[]
        {
            new(-1.85,  0.20,  4.10, 0.84, 1.92, 1.44, WoundKind.Ribs, 1),  // left flank
            new( 0.00, -2.40,  5.50, 1.02, 1.56, 0.66, WoundKind.Spine, 2), // the back over the hips
            new( 1.75, -0.90,  3.80, 0.78, 1.56, 1.32, WoundKind.Ribs, 2),  // right flank
            new(-1.20,  3.40,  6.10, 0.72, 0.84, 0.96, WoundKind.Flesh, 3), // the neck under the frill
            new( 1.30, -3.20,  1.90, 0.66, 0.72, 0.96, WoundKind.Bone, 3),  // the right hind leg
        }, new EyePair(-0.40, 0.62, 5.90, 5.80, 0.28)),
        new("allosaurus", new Wound[]
        {
            new(-1.25,  0.60,  3.70, 0.62, 1.25, 1.00, WoundKind.Ribs, 1),  // left flank
            new( 1.30, -0.10,  3.70, 0.62, 1.12, 0.94, WoundKind.Ribs, 2),  // right flank
            new( 0.35, -3.80,  4.30, 0.56, 1.00, 0.50, WoundKind.Spine, 2), // the tail
            new( 0.70,  2.55,  4.25, 0.45, 0.60, 0.48, WoundKind.Flesh, 3), // the neck
            new(-0.95, -0.40,  1.60, 0.50, 0.56, 0.75, WoundKind.Bone, 3),  // the left thigh
        }, new EyePair(0.60, 0.42, 4.20, 4.81, 0.20)),
        new("mammoth", new Wound[]
        {
            new(-1.45,  0.80,  4.20, 0.66, 1.44, 1.20, WoundKind.Ribs, 1),  // left flank
            new( 0.00, -1.60,  5.45, 0.90, 1.08, 0.54, WoundKind.Spine, 2), // the back
            new( 1.55, -0.70,  4.00, 0.66, 1.26, 1.08, WoundKind.Ribs, 2),  // right flank
            new( 0.95,  2.90,  4.90, 0.54, 0.66, 0.72, WoundKind.Flesh, 3), // behind the head
            new( 1.10,  2.40,  1.40, 0.54, 0.60, 0.90, WoundKind.Bone, 3),  // the right front leg
        }, new EyePair(-0.05, 0.66, 4.07, 4.95, 0.20)),
        new("wild_boar", new Wound[]
        {
            new(-0.95, -0.30,  2.20, 0.50, 0.90, 0.75, WoundKind.Ribs, 1),  // left flank
            new( 0.00,  0.20,  3.55, 0.60, 0.90, 0.42, WoundKind.Spine, 2), // the hump
            new( 1.05, -1.10,  2.00, 0.50, 0.85, 0.70, WoundKind.Ribs, 2),  // right flank
            new( 0.80,  1.40,  2.60, 0.45, 0.50, 0.55, WoundKind.Flesh, 3), // the neck
            new(-0.60, -2.00,  0.90, 0.36, 0.40, 0.55, WoundKind.Bone, 3),  // the left hind leg
        }, new EyePair(-0.30, 0.42, 2.05, 2.55, 0.13)),
        new("megaloceros", new Wound[]
        {
            new(-0.55,  0.20,  2.30, 0.36, 0.80, 0.55, WoundKind.Ribs, 1),  // left flank
            new( 0.55, -0.50,  2.30, 0.36, 0.75, 0.55, WoundKind.Ribs, 2),  // right flank
            new( 0.00, -0.60,  3.00, 0.45, 0.80, 0.30, WoundKind.Spine, 2), // the back
            new( 0.25,  2.00,  3.30, 0.30, 0.40, 0.45, WoundKind.Flesh, 3), // the neck
            new(-0.30, -1.30,  1.00, 0.25, 0.30, 0.45, WoundKind.Bone, 3),  // the left hind leg
        }, new EyePair(-0.02, 0.18, 2.60, 3.85, 0.09)),
        new("dilophosaurus", new Wound[]
        {
            new(-0.45,  0.60,  2.95, 0.42, 0.90, 0.60, WoundKind.Ribs, 1),  // left flank
            new( 0.50,  0.90,  2.90, 0.42, 0.80, 0.60, WoundKind.Ribs, 2),  // right flank
            new( 0.00, -1.20,  3.45, 0.36, 0.90, 0.32, WoundKind.Spine, 2), // the tail's root
            new( 0.00, -3.00,  3.05, 0.30, 0.70, 0.30, WoundKind.Flesh, 3), // the tail
            new(-0.35,  0.00,  2.00, 0.30, 0.40, 0.50, WoundKind.Bone, 3),  // the left thigh
        }, null),
        new("stegosaurus", new Wound[]
        {
            new(-1.70,  0.00,  3.90, 0.90, 1.90, 1.30, WoundKind.Ribs, 1),  // left flank
            new( 1.70, -1.20,  3.90, 0.90, 1.80, 1.30, WoundKind.Ribs, 2),  // right flank
            new( 0.00, -5.00,  6.20, 0.60, 1.60, 0.60, WoundKind.Spine, 2), // the tail's root
            new( 0.80,  3.80,  3.60, 0.60, 0.70, 0.80, WoundKind.Flesh, 3), // the neck
            new( 1.20, -1.80,  1.40, 0.60, 0.70, 1.00, WoundKind.Bone, 3),  // the right hind leg
        }, new EyePair(0.56, 0.27, 6.00, 3.15, 0.14)),
        new("ankylosaurus", new Wound[]
        {
            new(-1.30,  0.00,  2.00, 0.60, 1.20, 0.70, WoundKind.Ribs, 1),  // left flank
            new( 1.30, -1.00,  2.00, 0.60, 1.10, 0.70, WoundKind.Ribs, 2),  // right flank
            new( 0.00, -0.80,  3.20, 0.80, 1.20, 0.45, WoundKind.Spine, 2), // the armoured back
            new( 0.00, -4.00,  2.40, 0.35, 0.90, 0.35, WoundKind.Flesh, 3), // the tail
            new( 0.90,  1.00,  0.60, 0.40, 0.45, 0.55, WoundKind.Bone, 3),  // the right front leg
        }, new EyePair(-0.24, 0.54, 2.90, 2.20, 0.13)),
        new("brachiosaurus", new Wound[]
        {
            new(-2.20,  0.00,  5.50, 1.00, 2.00, 1.60, WoundKind.Ribs, 1),  // left flank
            new( 2.20, -1.00,  5.40, 1.00, 1.90, 1.50, WoundKind.Ribs, 2),  // right flank
            new( 0.00, -1.00,  7.70, 1.20, 2.00, 0.80, WoundKind.Spine, 2), // the back
            new( 0.40,  5.80,  11.00, 0.70, 0.80, 1.20, WoundKind.Flesh, 3), // the neck
            new( 1.50,  1.60,  2.00, 0.70, 0.80, 1.20, WoundKind.Bone, 3),  // the right front leg
        }, new EyePair(-2.48, 0.50, 7.50, 18.10, 0.18)),
        new("trex", new Wound[]
        {
            new(-2.00,  1.00,  6.80, 1.00, 2.00, 1.50, WoundKind.Ribs, 1),  // left flank
            new( 2.00,  0.00,  6.80, 1.00, 1.90, 1.50, WoundKind.Ribs, 2),  // right flank
            new( 0.29, -3.61,  8.30, 0.90, 2.00, 0.80, WoundKind.Spine, 2), // the tail's root
            new( 0.68,  4.63,  8.25, 0.80, 1.00, 1.00, WoundKind.Flesh, 3), // the neck
            new(-1.50, -0.50,  4.00, 0.80, 1.00, 1.30, WoundKind.Bone, 3),  // the left thigh
        }, new EyePair(-0.50, 0.90, 8.60, 9.70, 0.30)),
        new("pteranodon", new Wound[]
        {
            new(-3.50, -1.00,  0.35, 1.10, 0.90, 0.60, WoundKind.Flesh, 1), // the left wing torn
            new( 0.00, -1.80,  0.20, 0.45, 0.90, 0.55, WoundKind.Spine, 2), // the back
            new( 4.50, -2.00,  0.40, 1.20, 1.00, 0.60, WoundKind.Flesh, 2), // the right wing torn
            new(-6.17, -4.14,  0.22, 0.80, 0.90, 0.60, WoundKind.Flesh, 3), // the left wing tip
            new( 0.00,  0.80,  0.10, 0.30, 0.50, 0.40, WoundKind.Flesh, 3), // the neck
        }, null),
        new("woolly_rhino", new Wound[]
        {
            new(-1.45,  0.20,  2.60, 0.60, 1.10, 0.90, WoundKind.Ribs, 1),  // left flank
            new( 1.45, -1.00,  2.60, 0.60, 1.00, 0.90, WoundKind.Ribs, 2),  // right flank
            new( 0.00, -0.60,  4.40, 0.80, 1.10, 0.45, WoundKind.Spine, 2), // the back
            new( 1.00,  2.00,  3.20, 0.50, 0.60, 0.70, WoundKind.Flesh, 3), // the neck
            new(-0.90, -2.50,  1.00, 0.45, 0.50, 0.60, WoundKind.Bone, 3),  // the left hind leg
        }, new EyePair(0.01, 0.58, 3.00, 2.85, 0.14)),
        new("kentrosaurus", new Wound[]
        {
            new(-1.25,  0.20,  3.30, 0.65, 1.40, 1.00, WoundKind.Ribs, 1),  // left flank
            new( 1.25, -0.80,  3.30, 0.65, 1.30, 1.00, WoundKind.Ribs, 2),  // right flank
            new( 0.00, -3.50,  4.60, 0.50, 1.20, 0.50, WoundKind.Spine, 2), // the tail's root
            new( 0.60,  3.30,  3.20, 0.50, 0.60, 0.60, WoundKind.Flesh, 3), // the neck
            new( 0.72, -0.84,  0.88, 0.45, 0.55, 0.70, WoundKind.Bone, 3),  // the right hind leg
        }, new EyePair(0.14, 0.20, 5.40, 2.75, 0.12)),
        new("titan_triceratops", new Wound[]
        {
            new(-3.60,  0.00,  6.50, 1.60, 3.00, 2.20, WoundKind.Ribs, 1),  // left flank
            new( 3.60, -2.00,  6.30, 1.60, 2.80, 2.20, WoundKind.Ribs, 2),  // right flank
            new( 0.00, -3.50,  9.30, 1.80, 2.80, 1.10, WoundKind.Spine, 2), // the back
            new(-2.40,  7.00,  7.40, 1.20, 1.40, 1.60, WoundKind.Flesh, 3), // the neck under the frill
            new( 1.69, -4.37,  2.32, 1.10, 1.20, 1.80, WoundKind.Bone, 3),  // the right hind leg
        }, new EyePair(-0.62, 0.75, 10.30, 7.60, 0.30)),
        new("carcharodontosaurus", new Wound[]
        {
            new(-0.65,  0.60,  3.40, 0.50, 1.00, 0.75, WoundKind.Ribs, 1),  // left flank
            new( 0.85,  0.60,  3.40, 0.50, 0.90, 0.75, WoundKind.Ribs, 2),  // right flank
            new( 0.00, -2.00,  4.40, 0.45, 1.10, 0.40, WoundKind.Spine, 2), // the tail's root
            new( 0.60,  2.40,  4.30, 0.45, 0.55, 0.55, WoundKind.Flesh, 3), // the neck
            new(-0.45, -0.10,  2.00, 0.40, 0.50, 0.65, WoundKind.Bone, 3),  // the left thigh
        }, new EyePair(0.47, 0.35, 3.90, 4.45, 0.14)),
    };

    // models drawn from another's table: the same mesh under another name (MIRAGE's pteranodon mount)
    static readonly Dictionary<string, string> ALIAS = new(StringComparer.OrdinalIgnoreCase) { ["pteranodod"] = "pteranodon" };

    static readonly Dictionary<string, AnimalWounds> s_byModel = Build();
    static Dictionary<string, AnimalWounds> Build()
    {
        var d = new Dictionary<string, AnimalWounds>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in TABLE) d[a.Model] = a;
        foreach (var (k, v) in ALIAS) d[k] = d[v];
        return d;
    }

    /// <summary>the models with a wound table of their own</summary>
    public static IReadOnlyList<AnimalWounds> Table => TABLE;

    /// <summary>an animal model's table entry (null: none - FromSkin / Generic give wounds from its skin / bounds)</summary>
    public static AnimalWounds? Animal(string? model) => model != null && s_byModel.TryGetValue(model, out var a) ? a : null;

    /// <summary>the smallest radius a wound of the tables has (m): smaller ones do not read at the game's camera
    /// (70 m: a raptor is 50 pixels long - a 0.24 m wound some 8 pixels)</summary>
    public const double MIN_WOUND_RADIUS = 0.24;

    /// <summary>the wounds of a model shown at a stage, in the table's order (at most MAX_WOUNDS)</summary>
    public static Wound[] WoundsAt(AnimalWounds a, int stage)
    {
        var l = new List<Wound>();
        foreach (var w in a.Wounds) if (w.Stage <= stage && l.Count < MAX_WOUNDS) l.Add(w);
        return l.ToArray();
    }

    /// <summary>
    /// Wounds for an animal without a table entry (MIRAGE's and the other animals), from its bind-pose box in the
    /// model frame: a rib wound on each upper flank, the back, a leg - the same layout as the table, scaled to the
    /// body (the trunk taken as the middle 60 % of the length). Deterministic: the box alone decides.
    /// </summary>
    public static AnimalWounds Generic(string model, double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
    {
        double w = Math.Max(0.2, maxX - minX), L = Math.Max(0.4, maxY - minY), H = Math.Max(0.4, maxZ - minZ);
        double cx = (minX + maxX) / 2, cy = minY + L * 0.5, z0 = minZ;
        double rx = w * 0.18, ry = L * 0.12, rz = H * 0.16;
        return new AnimalWounds(model, new Wound[]
        {
            new(cx - w * 0.42, cy + L * 0.04, z0 + H * 0.62, rx, ry, rz, WoundKind.Ribs, 1),
            new(cx, cy - L * 0.12, z0 + H * 0.86, rx * 1.3, ry * 0.9, rz * 0.55, WoundKind.Spine, 2),
            new(cx + w * 0.42, cy - L * 0.06, z0 + H * 0.60, rx, ry * 0.9, rz, WoundKind.Ribs, 2),
            new(cx + w * 0.30, cy - L * 0.18, z0 + H * 0.25, rx * 0.8, ry * 0.5, rz * 0.9, WoundKind.Bone, 3),
        }, null);
    }

    /// <summary>
    /// Wounds of an animal without a table entry from its skin (the bind-pose vertices of its body meshes, model frame):
    /// the trunk is the run of slices along the length with the largest cross-section; a rib wound on each upper flank,
    /// the spine over the hips, a bone wound on a hind leg (the leg's vertices below the trunk) - each moved onto the
    /// skin when it misses it, the radii at least MIN_WOUND_RADIUS. No eyes (a head is not found reliably). Deterministic:
    /// the points alone decide. Few points: Generic from their box.
    /// </summary>
    public static AnimalWounds FromSkin(string model, IReadOnlyList<(double X, double Y, double Z)> pts)
    {
        if (pts.Count < 24)
        {
            if (pts.Count == 0) return Generic(model, -0.5, 0.5, -1, 1, 0, 1.5);
            return Generic(model, pts.Min(p => p.X), pts.Max(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.Y), pts.Min(p => p.Z), pts.Max(p => p.Z));
        }
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y), minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);
        double L = Math.Max(0.4, maxY - minY), H = Math.Max(0.2, maxZ - minZ);
        const int N = 24;
        var sl = new Slice?[N];
        for (int i = 0; i < N; i++)
        {
            double a = minY + L * i / N, b = minY + L * (i + 1) / N;
            var q = pts.Where(p => p.Y >= a && (p.Y < b || i == N - 1) && p.Z > minZ + 0.3 * H).ToList();
            if (q.Count < 4) continue;
            double top = Quantile(q.Select(p => p.Z), 0.95), bot = Quantile(q.Select(p => p.Z), 0.05);
            double lo = Quantile(q.Select(p => p.X), 0.05), hi = Quantile(q.Select(p => p.X), 0.95);
            sl[i] = new Slice((a + b) / 2, top, bot, (lo + hi) / 2, Math.Max(0.05, (hi - lo) / 2));
        }
        int m = -1;
        for (int i = 0; i < N; i++) if (sl[i] is { } s && (m < 0 || s.Area > sl[m]!.Value.Area)) m = i;
        if (m < 0) return Generic(model, pts.Min(p => p.X), pts.Max(p => p.X), minY, maxY, minZ, maxZ);
        int i0 = m, i1 = m;
        double thr = sl[m]!.Value.Area * 0.5;
        while (i0 > 0 && sl[i0 - 1] is { } s0 && s0.Area >= thr) i0--;
        while (i1 < N - 1 && sl[i1 + 1] is { } s1 && s1.Area >= thr) i1++;
        double y0 = minY + L * i0 / N, y1 = minY + L * (i1 + 1) / N, tl = Math.Max(y1 - y0, L * 0.2), ty = (y0 + y1) / 2;
        Slice At(double y)
        {
            int k = Math.Clamp((int)((y - minY) / L * N), 0, N - 1);
            for (int d = 0; d < N; d++)
            {
                if (k - d >= 0 && sl[k - d] is { } a) return a;
                if (k + d < N && sl[k + d] is { } b) return b;
            }
            return sl[m]!.Value;
        }
        var w = new List<Wound>();
        var sA = At(ty + 0.1 * tl);
        w.Add(new(sA.Cx - sA.Hw * 0.8, ty + 0.1 * tl, sA.Bot + 0.55 * (sA.Top - sA.Bot), sA.Hw * 0.55, tl * 0.28, (sA.Top - sA.Bot) * 0.3, WoundKind.Ribs, 1));
        var sB = At(ty - 0.15 * tl);
        w.Add(new(sB.Cx, ty - 0.15 * tl, sB.Top - 0.1 * (sB.Top - sB.Bot), sB.Hw * 0.6, tl * 0.26, (sB.Top - sB.Bot) * 0.2, WoundKind.Spine, 2));
        var sC = At(ty - 0.1 * tl);
        w.Add(new(sC.Cx + sC.Hw * 0.8, ty - 0.1 * tl, sC.Bot + 0.55 * (sC.Top - sC.Bot), sC.Hw * 0.55, tl * 0.26, (sC.Top - sC.Bot) * 0.3, WoundKind.Ribs, 2));
        // a hind leg: the vertices under the trunk's rear half, on the right side
        double belly = sB.Bot;
        var leg = pts.Where(p => p.Y >= y0 && p.Y <= ty && p.X > sB.Cx && p.Z > minZ + 0.15 * (belly - minZ) && p.Z < minZ + 0.8 * (belly - minZ)).ToList();
        if (leg.Count >= 4)
        {
            double lx = leg.Average(p => p.X), ly = leg.Average(p => p.Y), lz = leg.Average(p => p.Z);
            double sx = Math.Sqrt(leg.Average(p => (p.X - lx) * (p.X - lx))), sy = Math.Sqrt(leg.Average(p => (p.Y - ly) * (p.Y - ly)));
            w.Add(new(lx, ly, lz, sx * 1.4, sy * 1.4, Math.Max(0.1, belly - minZ) * 0.3, WoundKind.Bone, 3));
        }
        else w.Add(new(sA.Cx + sA.Hw * 0.5, y1 - 0.05 * tl, sA.Top - 0.3 * (sA.Top - sA.Bot), sA.Hw * 0.45, tl * 0.18, (sA.Top - sA.Bot) * 0.25, WoundKind.Flesh, 3));
        var res = new Wound[w.Count];
        for (int i = 0; i < w.Count; i++) res[i] = OntoSkin(Readable(w[i]), pts);
        return new AnimalWounds(model, res, null);
    }

    readonly record struct Slice(double Y, double Top, double Bot, double Cx, double Hw) { public double Area => (Top - Bot) * Hw; }

    static double Quantile(IEnumerable<double> v, double q)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a[Math.Clamp((int)Math.Round(q * (a.Length - 1)), 0, a.Length - 1)];
    }

    /// <summary>a wound with its radii at least MIN_WOUND_RADIUS (it reads at the game's camera)</summary>
    public static Wound Readable(Wound w) => w with
    {
        Rx = Math.Max(w.Rx, MIN_WOUND_RADIUS), Ry = Math.Max(w.Ry, MIN_WOUND_RADIUS), Rz = Math.Max(w.Rz, MIN_WOUND_RADIUS),
    };

    /// <summary>the normalised distance of a point from a wound's centre (1: on its ellipsoid)</summary>
    public static double Dist(in Wound w, double x, double y, double z)
    {
        double a = (x - w.X) / w.Rx, b = (y - w.Y) / w.Ry, c = (z - w.Z) / w.Rz;
        return Math.Sqrt(a * a + b * b + c * c);
    }

    /// <summary>a wound that holds no point of the skin moved towards the nearest one (it then lies at 0.45 of its
    /// radius from the centre): the wound cuts the surface</summary>
    public static Wound OntoSkin(Wound w, IReadOnlyList<(double X, double Y, double Z)> pts)
    {
        if (pts.Count == 0) return w;
        int best = 0, inside = 0;
        double bd = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            double d = Dist(w, pts[i].X, pts[i].Y, pts[i].Z);
            if (d < 1) inside++;
            if (d < bd) { bd = d; best = i; }
        }
        if (inside >= 3 || bd <= 0) return w;
        var p = pts[best];
        double k = 0.45 / bd;
        return w with { X = p.X + (w.X - p.X) * k, Y = p.Y + (w.Y - p.Y) * k, Z = p.Z + (w.Z - p.Z) * k };
    }

    // ------------------------------------------------------------------------------------------- characters

    // The Norse characters (workers, warriors, archers, spearmen, riders, the heroes Bela and Stina, the undead - and
    // the SEAS flamethrower the Norse train in the tech tree) share one skeleton of 47 bones (the converted glTF's joint
    // names are hashes of the original's). Their bind poses differ by the unit's size (level 5 is 25 % larger than
    // level 1) and build, so the wounds are placed per skeleton - at a bone (or between two), with an offset in the
    // model frame - and scaled by the neck's height (REF_NECK_Z: the level-1 worker). The bind pose stands facing +y,
    // left leg forward, the left arm (+x) swung back.
    /// <summary>a bone of the Norse skeleton (the joint name in the converted glTF)</summary>
    public static class Bones
    {
        public const string Pelvis = "bone_784df207";
        public const string ThighL = "bone_568552a4", KneeL = "bone_ef59d82b";     // +x
        public const string ThighR = "bone_8b722150", KneeR = "bone_32aeabdf";     // -x
        public const string Spine = "bone_f9c938e3", Chest = "bone_e5a68e8c", Neck = "bone_2aadcb79";
        public const string Head = "bone_b3a49ac3", HeadTop = "bone_05069e98";
        public const string ShoulderL = "bone_413f2520", ElbowL = "bone_765f52ce";  // +x
        public const string ShoulderR = "bone_9cc856d4", ElbowR = "bone_aba8213a";  // -x
    }

    /// <summary>the level-1 worker's neck height (bind pose, m): the scale 1 of the body tables</summary>
    public const double REF_NECK_Z = 3.03;

    /// <summary>a character's wound: at Bone (or half way to Bone2), offset (Dx, Dy, Dz) and radii in the scale of a
    /// level-1 worker (REF_NECK_Z), from stage Stage on</summary>
    public readonly record struct BodyWound(string Bone, string? Bone2, double Dx, double Dy, double Dz, double Rx, double Ry, double Rz, WoundKind Kind, int Stage);

    /// <summary>a body type: its wounds and its eyes (Dx either side of the head bone, Dy forward, Dz up, radius R)</summary>
    public sealed record CharacterBody(string Id, BodyWound[] Wounds, double EyeDx, double EyeDy, double EyeDz, double EyeR);

    // torn clothing with the wound under it: the left of the chest, the back, the right upper arm, the left thigh, the neck
    // (seen from the RTS camera: the shoulders, the back and the flanks are what shows)
    static readonly CharacterBody MALE = new("norse_male", new BodyWound[]
    {
        new(Bones.Chest, null, 0.30, 0.02, 0.22, 0.26, 0.30, 0.32, WoundKind.Ribs, 1),          // the left of the chest
        new(Bones.Chest, null, 0.00, -0.28, 0.28, 0.32, 0.24, 0.34, WoundKind.Spine, 2),        // the back
        new(Bones.ShoulderR, Bones.ElbowR, -0.04, 0.00, 0.00, 0.20, 0.20, 0.26, WoundKind.Flesh, 2), // the right upper arm
        new(Bones.ThighL, Bones.KneeL, 0.04, 0.03, 0.00, 0.24, 0.26, 0.32, WoundKind.Bone, 3),   // the left thigh
        new(Bones.Neck, null, -0.12, 0.00, -0.08, 0.20, 0.20, 0.18, WoundKind.Flesh, 3),         // the right of the neck
    }, 0.09, 0.24, 0.37, 0.045);
    // the women (Stina, the spear- and pikewomen, the scout's rider): the same places, a narrower chest
    static readonly CharacterBody FEMALE = new("norse_female", MALE.Wounds.Select(w => w with
    {
        Dx = w.Dx * 0.9, Dy = w.Dy * 0.9, Rx = w.Rx * 0.92, Ry = w.Ry * 0.92,
    }).ToArray(), 0.085, 0.22, 0.36, 0.042);

    /// <summary>the body types</summary>
    public static IReadOnlyList<CharacterBody> Bodies => new[] { MALE, FEMALE };

    // the women: the heroine Stina, the Norse spear- and pikewomen, the rider of the scout (hu_rider_a) - Bela is a man
    static readonly string[] WOMEN = { "stina_", "hu_spearman_", "hu_pikeman_", "hu_rider_a" };

    /// <summary>a character model's body type (the Norse women: the female one)</summary>
    public static CharacterBody BodyOf(string model)
    {
        string m = model.ToLowerInvariant();
        return WOMEN.Any(w => m.StartsWith(w)) ? FEMALE : MALE;
    }

    /// <summary>
    /// The wounds and eyes of a character from its skeleton's bind pose (joint: a bone's bind position in the model
    /// frame, null when the model lacks it). null: not the Norse skeleton (GenericCharacter then).
    /// </summary>
    public static AnimalWounds? CharacterWounds(string model, Func<string, (double X, double Y, double Z)?> joint)
    {
        var body = BodyOf(model);
        if (joint(Bones.Neck) is not { } neck || joint(Bones.Head) is not { } head || neck.Z <= 0) return null;
        double s = neck.Z / REF_NECK_Z;
        var w = new List<Wound>();
        foreach (var b in body.Wounds)
        {
            if (joint(b.Bone) is not { } p) return null;
            if (b.Bone2 != null)
            {
                if (joint(b.Bone2) is not { } q) return null;
                p = ((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2);
            }
            w.Add(new(p.X + b.Dx * s, p.Y + b.Dy * s, p.Z + b.Dz * s, b.Rx * s, b.Ry * s, b.Rz * s, b.Kind, b.Stage));
        }
        return new AnimalWounds(model, w.ToArray(), new EyePair(head.X, body.EyeDx * s, head.Y + body.EyeDy * s, head.Z + body.EyeDz * s, body.EyeR * s));
    }

    /// <summary>every wound of a table moved onto the skin where it misses it (OntoSkin): a character's armour, cloak or
    /// bare neck differ from model to model on the same skeleton</summary>
    public static AnimalWounds OntoSkin(AnimalWounds a, IReadOnlyList<(double X, double Y, double Z)> pts) =>
        pts.Count == 0 ? a : a with { Wounds = a.Wounds.Select(w => OntoSkin(w, pts)).ToArray() };

    /// <summary>a character of another skeleton: wounds from its box (chest, back, a thigh), no eyes</summary>
    public static AnimalWounds GenericCharacter(string model, double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
    {
        double w = Math.Max(0.2, maxX - minX), d = Math.Max(0.2, maxY - minY), H = Math.Max(0.5, maxZ - minZ);
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        return new AnimalWounds(model, new Wound[]
        {
            new(cx + w * 0.2, cy, minZ + H * 0.66, w * 0.17, d * 0.2, H * 0.085, WoundKind.Ribs, 1),
            new(cx, cy - d * 0.2, minZ + H * 0.7, w * 0.2, d * 0.15, H * 0.09, WoundKind.Spine, 2),
            new(cx + w * 0.15, cy, minZ + H * 0.4, w * 0.15, d * 0.17, H * 0.085, WoundKind.Bone, 3),
        }, null);
    }

    // ------------------------------------------------------------------------------------------- which look

    /// <summary>
    /// The look of a model: what kind it is drawn as. fourcc: the model's (GSF) type; animated: it has a skeleton or
    /// clips (a Misc model that moves: MIRAGE's zeppelin flyingtrader).
    /// </summary>
    public static LookKind KindOf(string model, string fourcc, bool animated)
    {
        string m = model.ToLowerInvariant();
        switch (fourcc)
        {
            case "Anim": return LookKind.Animal;
            case "Char":
                if (IsUndead(m)) return LookKind.Undead;
                // the berserk statues, MIRAGE's golem: stone that walks - the building look on the moving model
                if (m.Contains("statue") || m == "golem") return LookKind.Vehicle;
                return LookKind.Character;
            case "Vehi": case "Ship": return LookKind.Vehicle;
            case "Misc": case "Deko": return animated ? LookKind.Vehicle : LookKind.Building;
            default: return LookKind.Building;
        }
    }

    /// <summary>the undead models (they already look undead: only the glow, mist and flies - no second rot)</summary>
    public static bool IsUndead(string model)
    {
        string m = model.ToLowerInvariant();
        return m.Contains("undead") || m.Contains("zombi") || m.StartsWith("hx_archer");
    }

    /// <summary>the look of an attached part (child) of a model drawn as `parent`: a rider of an animal is a character,
    /// a saddle build-up or turret on an animal the moving building look, a character's weapon or tool an item; null:
    /// the part does not follow (a character or animal on a building)</summary>
    public static LookKind? ChildKind(LookKind parent, LookKind child) => parent switch
    {
        LookKind.Building => child is LookKind.Character or LookKind.Undead or LookKind.Animal ? null : LookKind.Building,
        LookKind.Character or LookKind.Undead => child is LookKind.Character or LookKind.Undead or LookKind.Animal ? child : LookKind.Item,
        LookKind.Item => LookKind.Item,
        _ => child is LookKind.Character or LookKind.Undead or LookKind.Animal ? child : LookKind.Vehicle,
    };

    /// <summary>half size (m) of a building that gets all the stage's clouds (the headquarters: 9); BIG_MIST_SIZE: from
    /// this half size on the clouds are fx_poison_big</summary>
    public const double BIG_BUILDING = 9, BIG_MIST_SIZE = 7;

    /// <summary>
    /// The infection's own effects of a model at a stage: poison clouds (Mist, big: fx_poison_big), fly swarms, the blight
    /// on the ground. Buildings: the stage's (InfectionStage) scaled by the building's tune and its size (halfSize: the
    /// larger half width of its footprint, m - a cottage fewer and smaller clouds than the headquarters); animals: one cloud, the
    /// stage's flies; vehicles: a cloud from stage 2, flies; characters: flies from stage 2, a small cloud at 3; the
    /// undead: a small cloud from stage 2 (their eyes glow from stage 1), flies at 3; items nothing.
    /// </summary>
    public static (int Mist, bool Big, int Flies, bool Blight) Effects(LookKind kind, int stage, BuildingTune? tune = null, double halfSize = BIG_BUILDING)
    {
        stage = Math.Clamp(stage, 0, MAX_STAGE);
        if (stage == 0) return (0, false, 0, false);
        var st = Stage(stage);
        var t = tune ?? BuildingTune.Default;
        // (a smaller building fewer clouds, and the small ones: six big clouds hid a cottage at the game's camera)
        double k = Math.Clamp((halfSize - 3) / (BIG_BUILDING - 3), 0.3, 1);
        return kind switch
        {
            LookKind.Building => ((int)Math.Round(st.Mist * t.Mist * k), stage >= 2 && t.BigMist && halfSize >= BIG_MIST_SIZE,
                (int)Math.Round(st.Flies * t.Flies), st.Blight > 0 && t.Blight > 0),
            LookKind.Animal => (1, false, st.Flies, false),
            LookKind.Vehicle => (stage >= 2 ? stage - 1 : 0, false, stage >= 3 ? 2 : 1, false),
            LookKind.Character => (stage >= 3 ? 1 : 0, false, stage >= 2 ? 1 : 0, false),
            LookKind.Undead => (stage >= 2 ? 1 : 0, false, stage >= 3 ? 1 : 0, false),
            _ => (0, false, 0, false),
        };
    }

    // ------------------------------------------------------------------------------------------- buildings

    /// <summary>
    /// How a building family wears the look where the generic numbers misread (BuildingTunes): Slime x the stage's slime
    /// height, SlimeShare the cap (share of the height), Veins x the vein strength, VeinScale x the veins' density,
    /// Blight x the blight's radius, BlightMin the least half width of the blight (m: a wall's), Mist / Flies x the
    /// counts, BigMist fx_poison_big from stage 2 (else fx_poison), Keep: emitters (node name parts) that keep their
    /// colours besides the natural ones (EmitterTurnsGreen).
    /// </summary>
    public sealed record BuildingTune(string Family)
    {
        public double Slime { get; init; } = 1;
        public double SlimeShare { get; init; } = SLIME_MAX_SHARE;
        public double Veins { get; init; } = 1;
        public double VeinScale { get; init; } = 1;
        public double Blight { get; init; } = 1;
        public double BlightMin { get; init; } = 3;
        public double Mist { get; init; } = 1;
        public double Flies { get; init; } = 1;
        public bool BigMist { get; init; } = true;
        public string[] Keep { get; init; } = Array.Empty<string>();
        public static readonly BuildingTune Default = new("default");
    }

    // per family (the model name without _dest / _upgrade ...): looked at on the game's camera (InfectedLookTest
    // --only=norse_buildings) - the generic look misread on walls (slime over the whole wall, a round blight 20 m
    // wide round a 2 m palisade), on towers (slime to the platform, the blight lost under them), on the flat field
    // (slime over the crop - kept low), on the small markers (rally points: no clouds), on the harbour (its water
    // splashes stay water)
    static readonly BuildingTune[] TUNES =
    {
        new("hu_fireplace"),
        new("hu_arena") { Blight = 0.9 },
        new("hu_temple") { Slime = 0.8 },
        // walls and gates: low slime, few small clouds; a wall post's box holds all its arms (every direction it may
        // join), so its blight is the half of a building's - the posts' blights join along the wall; a gate's is long
        new("hu_palisade") { Slime = 0.45, SlimeShare = 0.35, VeinScale = 1.6, Blight = 0.55, BlightMin = 2.6, Mist = 0.34, Flies = 0.34, BigMist = false },
        new("hu_small_wall") { Slime = 0.45, SlimeShare = 0.35, VeinScale = 1.6, Blight = 0.55, BlightMin = 2.6, Mist = 0.34, Flies = 0.34, BigMist = false },
        new("hu_re_enforced_wall") { Slime = 0.5, SlimeShare = 0.35, VeinScale = 1.4, Blight = 0.55, BlightMin = 3.0, Mist = 0.34, Flies = 0.34, BigMist = false },
        new("hu_ladder") { Slime = 0.4, SlimeShare = 0.3, Mist = 0.34, Flies = 0.34, BigMist = false },
        // towers: tall and narrow - the slime a third up, a blight wider than the foot
        new("hu_small_tower") { SlimeShare = 0.3, VeinScale = 1.3, Blight = 1.8, Mist = 0.5 },
        new("hu_large_tower") { SlimeShare = 0.3, VeinScale = 1.2, Blight = 1.5, Mist = 0.67 },
        new("hu_bunker") { SlimeShare = 0.35, Blight = 1.3, Mist = 0.67 },
        // the field: flat - rotting crop, a little slime, the blight round it
        new("hu_corn_field") { Slime = 0.25, SlimeShare = 0.5, Veins = 0.8, Blight = 0.85, Mist = 0.5, BigMist = false },
        new("hu_small_animal_farm") { Slime = 0.6 },
        new("hu_lumberjack_cottage") { Slime = 0.8 },
        new("hu_stone_quarry") { Slime = 0.8 },
        new("hu_stone_cottage") { Slime = 0.7 },
        new("hu_warehouse"),
        new("hu_tavern"),
        new("hu_kennel") { Slime = 0.8 },
        new("hu_weapons_smith") { Slime = 0.8 },
        new("hu_machine_maker"),
        new("hu_magic_cauldron") { Slime = 0.7 },
        new("hu_warpgate") { Slime = 0.6, VeinScale = 1.2 },
        // the harbour stands in the water: its splashes stay water
        new("hu_harbour") { Slime = 0.6, Blight = 0.8, Keep = new[] { "wasser", "water", "welle", "wave", "gischt", "splash" } },
        // the rally point flags: a mark on the ground, no clouds
        new("hu_rally_point") { Slime = 0.3, SlimeShare = 0.3, Blight = 0.6, BlightMin = 1.5, Mist = 0, Flies = 0, BigMist = false },
    };

    static readonly string[] SUFFIXES = { "_upgrade_balista_dest", "_upgrade_balista", "_upgrade_dest", "_upgrade", "_anvil_dest", "_anvil", "_crane", "_gate_dest", "_gate", "_harbour", "_dest" };

    /// <summary>a building model's tune: its family's (the name without _dest, _upgrade, _gate ...), else Default</summary>
    public static BuildingTune TuneFor(string? model)
    {
        if (string.IsNullOrEmpty(model)) return BuildingTune.Default;
        string m = model.ToLowerInvariant();
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (var t in TUNES) if (t.Family == m) return t;
            string? cut = null;
            foreach (var s in SUFFIXES) if (m.EndsWith(s)) { cut = m.Substring(0, m.Length - s.Length); break; }
            if (cut == null) break;
            m = cut;
        }
        return BuildingTune.Default;
    }

    /// <summary>the tunes of the families (tests, docs)</summary>
    public static IReadOnlyList<BuildingTune> Tunes => TUNES;

    /// <summary>how high the slime climbs on a building of this height at a stage with its tune (m)</summary>
    public static double SlimeOn(double buildingHeight, int stage, BuildingTune tune) =>
        Math.Min(Stage(stage).SlimeHeight * tune.Slime, Math.Max(0, buildingHeight) * tune.SlimeShare);

    /// <summary>
    /// The blight's half widths (m) under a building of this footprint (half sizes, m) at a stage: the stage's share
    /// of each, at least the tune's BlightMin (a wall's thin side), at most 2.5 x the other (no 30 m strip along a
    /// wall piece). (0, 0): none.
    /// </summary>
    public static (double Rx, double Rz) BlightRadii(double halfX, double halfZ, int stage, BuildingTune tune)
    {
        double k = Stage(stage).Blight * tune.Blight;
        if (k <= 0) return (0, 0);
        double rx = Math.Max(tune.BlightMin, halfX * k), rz = Math.Max(tune.BlightMin, halfZ * k);
        return (Math.Min(rx, rz * 2.5), Math.Min(rz, rx * 2.5));
    }

    // the parts of an emitter's node name that mark fire, smoke and light (German and English, as the models name them)
    static readonly string[] GREEN = { "feuer", "fire", "flame", "flamm", "rauch", "smoke", "qualm", "glow", "glut", "light", "ligt", "licht", "aura", "blast", "comet", "streak", "spark", "funk", "fackel", "torch", "kerze", "buff" };
    // ... and the natural ones (insects, dust, stones, splinters, fluff) - they keep their colours (as does any emitter
    // named neither way)
    static readonly string[] KEEP = { "fliegen", "flies", "fly", "dust", "staub", "stone", "debris", "splid", "splitter", "flusen", "impact", "puff", "poof", "ring", "shockwave", "leaf", "blatt", "snow", "schnee" };

    /// <summary>
    /// Whether an emitter of an infected model turns green (from stage 2): fires, smoke, glows and lights do; insects,
    /// dust, flying stones, splinters and the tune's Keep (water) keep their colours; an emitter named neither way keeps
    /// them too. node: the emitter node's name ("particles_DEST_02_feuer_Gross_01").
    /// </summary>
    public static bool EmitterTurnsGreen(string node, BuildingTune? tune = null)
    {
        string n = node.ToLowerInvariant();
        if (tune != null) foreach (var k in tune.Keep) if (n.Contains(k)) return false;
        // (fire and smoke first: the chimney's "schornstein_qualm" and the "stonecottage_smoke" are smoke, not stones)
        foreach (var g in GREEN) if (n.Contains(g)) return true;
        foreach (var k in KEEP) if (n.Contains(k)) return false;
        return false;
    }

    // ------------------------------------------------------------------------------------------- effect placement

    /// <summary>
    /// Where a building's poison clouds stand: `count` points on a ring of `radius` about its origin (x, z in the
    /// model's frame as placed), the first at angle `phase`, evenly apart, every other one a little further out.
    /// </summary>
    public static (double X, double Z)[] MistRing(int count, double radius, double phase = 0.6) => MistRing(count, radius, radius, phase);

    /// <summary>MistRing on an ellipse of half widths rx (x) and rz (z): along a wall</summary>
    public static (double X, double Z)[] MistRing(int count, double rx, double rz, double phase)
    {
        var r = new (double, double)[Math.Max(0, count)];
        for (int i = 0; i < r.Length; i++)
        {
            double a = phase + i * 2 * Math.PI / r.Length, k = i % 2 == 0 ? 1.0 : 1.18;
            r[i] = (Math.Cos(a) * rx * k, Math.Sin(a) * rz * k);
        }
        return r;
    }
}

/// <summary>How a model wears the infected look (InfectedLookData.KindOf).</summary>
public enum LookKind
{
    /// <summary>static: rot, slime from the ground, moss, veins, green fires, clouds, flies, the blight</summary>
    Building,
    /// <summary>a vehicle, ship, walking statue, an animal's build-up: the building look (rotting wood, slime from the
    /// keel) that moves with the model - no blight</summary>
    Vehicle,
    /// <summary>pale skin, bruises, torn wounds with bones and blood, green eyes</summary>
    Animal,
    /// <summary>pale grey-green skin, rotting clothes, bruises, torn cloth over wounds, green eyes</summary>
    Character,
    /// <summary>the undead (hu_undead_*, the zombies): only the glowing eyes, mist and flies</summary>
    Undead,
    /// <summary>a character's weapon, tool or load: rot and veins only</summary>
    Item,
}
