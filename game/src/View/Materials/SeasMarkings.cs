// SEAS player-colour markings: the parts of the SEAS textures (seas_*) painted in the owner's colour besides their
// white / light grey parts (ModelMaterials.SeasPainted). The SEAS models never had player-colour areas and many of
// them have little white (the carrier, the turret tower, the robots and vehicles barely changed), so these boxes
// mark natural livery on the textures' UV layout: hull stripes, roof sheets, emblems, hazard stripes, flag cloth,
// armour plates, uniform cloth. Kacper 2026-11-11: "for units that didn't have a clear spot you need to paint your
// own", about 30 % of a model, "more natural markings" - not the red paint wholesale.
//
// A box is (texture, u0, v0, u1, v1) in UV (0..1, v down as in the texture file), Sel.All paints everything in it,
// Sel.Red only its red / orange paint (an emblem or a stripe on a dark panel), Sel.Gold its brass, Sel.Keep keeps the
// box unpainted (also by the white key). Mask() rasterizes a texture's boxes
// at load (MASK_SIZE px, soft edges) into the shader's paint_mask; the shader paints max(white key, mask): the
// marked parts take the owner's colour keeping the texture's shading, dirt and scratches (docs/VIEW.md "SEAS paint").
// A texture is shared by many models: a box paints every model that uses those texels (the comments name the main
// ones). Tool to review: --only=seas of LightingTest (all SEAS models, original colours above).
using System;
using System.Collections.Generic;
using Godot;

namespace PwRemake.View.Materials;

public static class SeasMarkings
{
    /// <summary>All: everything in the box; Red: its red / orange paint; Gold: its brass (yellow-orange metal: an
    /// animal's armour baked into its skin texture); Keep: nothing in the box is painted, not even by the white key
    /// (a part that must keep its colours - the lumberjack's minigun).</summary>
    public enum Sel { All, Red, Gold, Keep }
    /// <summary>Which models a box paints: all, only buildings (GSF kinds Bldg, Wall, Deko), only the others (units,
    /// vehicles, ships) - for a texture both use.</summary>
    public enum Scope { Any, Buildings, Others }

    /// <summary>A box; Model: only on that model (name prefix), null: every model using the texture.</summary>
    public readonly record struct Mark(string Texture, float U0, float V0, float U1, float V1, Sel Sel, string Note, Scope Scope = Scope.Any, string? Model = null);

    // ---- the tents: three proposals each (Kacper 2026-11-11 "for tents could you do some nicer pattern? Give me 3
    // propositions"); PW_SEAS_TENTS="<small><big>" picks them (e.g. "BA"; default "DD"). The small tent's net is 3 x 3
    // tiles of one texture square (u 0.33-0.49 left to right in every tile, v shifted per row); the big tent's canvas
    // runs u 0.72 (eaves) .. 0.99 (ridge) across, v along each of its four panels (0.5 .. 0.97).
    // D (Kacper 2026-11-12, the default): "just an outline where I marked" - the net's outer border only (its tiles
    // share one texture square, so a UV box would paint the inner seams too: a mesh-space outline, NetOutline); "a red
    // line where the walls met the roof" - the big tent's eaves all round (u 0.72 along the sides, v 0.50 at the ends).
    public static readonly string[] SmallTentNames = { "wide band with thin edge lines", "camouflage patches", "lattice", "outline of the net" };
    public static readonly string[] BigTentNames = { "ridge-line stripe and end panels", "eave trim and centre emblem patch", "checker band and end trim", "eave line all round" };
    static readonly Mark[][] SmallTent =
    {
        new Mark[]
        {
            new("seas_tent", 0.400f, 0.400f, 0.420f, 0.800f, Sel.All, "small tent A: wide band with thin edge lines"),
            new("seas_tent", 0.330f, 0.400f, 0.336f, 0.800f, Sel.All, "small tent A: edge line"),
            new("seas_tent", 0.484f, 0.400f, 0.492f, 0.800f, Sel.All, "small tent A: edge line"),
        },
        new Mark[]
        {
            new("seas_tent", 0.368f, 0.598f, 0.442f, 0.672f, Sel.All, "small tent B: camouflage patches"),
        },
        new Mark[]
        {
            new("seas_tent", 0.330f, 0.400f, 0.343f, 0.800f, Sel.All, "small tent C: lattice"),
            new("seas_tent", 0.478f, 0.400f, 0.492f, 0.800f, Sel.All, "small tent C: lattice"),
            new("seas_tent", 0.320f, 0.622f, 0.495f, 0.640f, Sel.All, "small tent C: lattice"),
        },
        System.Array.Empty<Mark>(),   // D: NetOutline
    };
    static readonly Mark[][] BigTent =
    {
        new Mark[]
        {
            new("seas_tent", 0.925f, 0.480f, 1.000f, 1.000f, Sel.All, "big tent A: ridge-line stripe"),
            new("seas_tent", 0.650f, 0.480f, 1.000f, 0.570f, Sel.All, "big tent A: end panels"),
        },
        new Mark[]
        {
            new("seas_tent", 0.700f, 0.480f, 0.770f, 1.000f, Sel.All, "big tent B: eave trim"),
            new("seas_tent", 0.860f, 0.820f, 1.000f, 0.940f, Sel.All, "big tent B: centre emblem patch"),
        },
        new Mark[]
        {
            new("seas_tent", 0.700f, 0.780f, 0.770f, 0.870f, Sel.All, "big tent C: checker band"),
            new("seas_tent", 0.840f, 0.780f, 0.910f, 0.870f, Sel.All, "big tent C: checker band"),
            new("seas_tent", 0.980f, 0.780f, 1.000f, 0.870f, Sel.All, "big tent C: checker band"),
            new("seas_tent", 0.770f, 0.870f, 0.840f, 0.960f, Sel.All, "big tent C: checker band"),
            new("seas_tent", 0.910f, 0.870f, 0.980f, 0.960f, Sel.All, "big tent C: checker band"),
            new("seas_tent", 0.650f, 0.480f, 1.000f, 0.530f, Sel.All, "big tent C: end trim"),
        },
        new Mark[]
        {
            new("seas_tent", 0.712f, 0.480f, 0.735f, 1.000f, Sel.All, "big tent D: eave line along the sides"),
            new("seas_tent", 0.712f, 0.480f, 1.000f, 0.522f, Sel.All, "big tent D: eave line at the ends"),
        },
    };

    /// <summary>
    /// A painted outline in the mesh's own frame: where a texel of Texture inside the UV box (U0..V1) lies within Width
    /// of the closed polygon Points (mesh x, y: the GSF frame, z up; at most 12 corners) - for a border that the UVs
    /// cannot tell from inner seams.
    /// </summary>
    public readonly record struct Outline(string Model, string Texture, float U0, float V0, float U1, float V1, Vector2[] Points, float Width, string Note);

    /// <summary>small tent D: the net's outer border - its rim vertices (the corners at 5.56, the middle of each side
    /// pulled in to 5.02 in mesh38's frame), in the frame the model's static meshes are merged into (ModelLibrary
    /// MergeStatic: below the model's root turn, with each mesh node's own offset - mesh38's x -0.319, y -0.077; the
    /// first version left that offset out: the band was thick on the left, missing on the right).</summary>
    static readonly Outline NetOutline = new("seas_small_tent", "seas_tent", 0.31f, 0.48f, 0.50f, 0.80f, new Vector2[]
    {
        new(-5.879f, -5.637f), new(-1.989f, -5.097f), new(1.351f, -5.097f), new(5.241f, -5.637f), new(4.701f, -1.757f), new(4.701f, 1.593f),
        new(5.241f, 5.483f), new(1.351f, 4.943f), new(-1.989f, 4.943f), new(-5.879f, 5.483f), new(-5.339f, 1.593f), new(-5.339f, -1.757f),
    }, 0.30f, "small tent D: outline of the net");

    /// <summary>The outline painted on a model's texture (the chosen tent proposals), or null.</summary>
    public static Outline? OutlineFor(string? model, string? textureFile)
    {
        if (TentChoice.Small != 3 || model == null || !model.StartsWith(NetOutline.Model, StringComparison.OrdinalIgnoreCase)) return null;
        return string.Equals(System.IO.Path.GetFileNameWithoutExtension(textureFile ?? ""), NetOutline.Texture, StringComparison.OrdinalIgnoreCase) ? NetOutline : null;
    }

    /// <summary>The chosen tent proposals (PW_SEAS_TENTS, "AA" .. "DD": small, big; default "DD").</summary>
    public static (int Small, int Big) TentChoice
    {
        get
        {
            var e = (System.Environment.GetEnvironmentVariable("PW_SEAS_TENTS") ?? "DD").ToUpperInvariant().PadRight(2, 'D');
            return (Math.Clamp(e[0] - 'A', 0, 3), Math.Clamp(e[1] - 'A', 0, 3));
        }
    }

    static Mark[]? s_all;
    /// <summary>Marks and the chosen tent proposals.</summary>
    static Mark[] All
    {
        get
        {
            if (s_all != null) return s_all;
            var (sm, bg) = TentChoice;
            var l = new List<Mark>(Marks);
            foreach (var m in SmallTent[sm]) l.Add(m with { Model = "seas_small_tent" });
            foreach (var m in BigTent[bg]) l.Add(m with { Model = "seas_big_tent" });
            return s_all = l.ToArray();
        }
    }

    /// <summary>The model kinds painted as buildings (Scope.Buildings): GSF fourcc Bldg, Wall, Deko.</summary>
    public static bool IsBuilding(string? fourcc) => fourcc is "Bldg" or "Wall" or "Deko";

    // ---- the markings (keep each texture's boxes together; numbers are UV, 3 decimals are plenty)
    public static readonly Mark[] Marks =
    {
        // seas_building_a: headquarters, barracks, steelwork, greenhouse, garage, laboratory, gate, turret, turret tower,
        // healspring, crane, the carrier's and the walls' metal
        // Kacper 2026-11-11 on the buildings: "the garage is nice but others have too much color change ... closer to
        // 10-20% and mostly stripe based, not whole roofs": no roof sheet and no armour-plate band on buildings
        // (Scope.Others: the vehicles using this texture - helicopter, defence spider - keep them), narrower stripes.
        new("seas_building_a", 0.565f, 0.000f, 1.000f, 0.045f, Sel.All, "riveted beam along roof edges and tops", Scope.Others),
        new("seas_building_a", 0.565f, 0.008f, 1.000f, 0.036f, Sel.All, "riveted beam along roof edges and tops (a stripe)", Scope.Buildings),
        new("seas_building_a", 0.790f, 0.100f, 1.000f, 0.300f, Sel.All, "red-brown roof sheet", Scope.Others),
        new("seas_building_a", 0.375f, 0.000f, 0.440f, 0.190f, Sel.Red, "hazard stripes"),
        new("seas_building_a", 0.420f, 0.330f, 0.760f, 0.470f, Sel.Red, "SEAS emblems (triangle, round seal)"),
        new("seas_building_a", 0.000f, 0.480f, 0.290f, 0.780f, Sel.Red, "red window frames"),
        new("seas_building_a", 0.140f, 0.500f, 0.260f, 0.545f, Sel.All, "band across the hatched armour plate", Scope.Others),
        new("seas_building_a", 0.110f, 0.755f, 1.000f, 0.785f, Sel.All, "riveted rail along the bottom of the walls"),
        new("seas_building_a", 0.670f, 0.480f, 0.715f, 0.775f, Sel.All, "upright stripe on the riveted pillar panel (gate, laboratory)"),
        new("seas_building_a", 0.640f, 0.805f, 1.000f, 0.838f, Sel.All, "coping band on top of the brick bases"),
        new("seas_building_a", 0.120f, 0.805f, 0.630f, 0.860f, Sel.All, "stripe along the girder band", Scope.Others),
        new("seas_building_a", 0.120f, 0.818f, 0.630f, 0.848f, Sel.All, "stripe along the girder band (crane arms, lab, greenhouse)", Scope.Buildings),

        // seas_carrier: the carrier and its turret
        new("seas_carrier", 0.000f, 0.920f, 1.000f, 1.000f, Sel.All, "hull waterline band"),
        new("seas_carrier", 0.000f, 0.625f, 0.470f, 0.715f, Sel.All, "superstructure band"),
        new("seas_carrier", 0.200f, 0.260f, 0.370f, 0.435f, Sel.Red, "turret: copper outline of the curved armour plate"),
        new("seas_carrier", 0.424f, 0.075f, 0.561f, 0.097f, Sel.All, "turret: band along the top of the gun housing"),
        new("seas_carrier", 0.420f, 0.420f, 0.650f, 0.500f, Sel.Red, "copper pipe"),

        // seas_products: submarine, hovercraft, helicopter, defence spider, headquarters' and barracks' parts, flags
        new("seas_products", 0.505f, 0.330f, 0.635f, 0.470f, Sel.All, "pennant cloth"),
        new("seas_products", 0.170f, 0.100f, 0.990f, 0.170f, Sel.All, "submarine hull band"),
        new("seas_products", 0.580f, 0.680f, 1.000f, 1.000f, Sel.Red, "hovercraft trim lines"),
        new("seas_products", 0.000f, 0.220f, 0.110f, 0.470f, Sel.Red, "defence spider: frame of the gun housing"),

        // seas_robots: mechanical walker, mobile suits, lumberjacks (selected armour plates, not all the orange paint;
        // Scope.Others: not on the buildings using this texture - the small tent's posts, the greenhouse)
        new("seas_robots", 0.320f, 0.120f, 0.610f, 0.300f, Sel.Red, "walker: back armour", Scope.Others),
        new("seas_robots", 0.800f, 0.050f, 1.000f, 0.140f, Sel.Red, "walker: arm plate", Scope.Others),
        // (not the lumberjack's head dome - u 0.62-0.80, v 0.89-1 - nor its leg plates - u 0.44-0.53 v 0.48-0.64,
        // u 0.38-0.52 v 0.95-1, u 0.65-0.80 v 0.74-0.89: Kacper 2026-11-11 "for lumberjacks don't color their head and
        // legs"; the walker and mobile suits share those plate texels and lost them too)
        new("seas_robots", 0.370f, 0.740f, 0.615f, 0.948f, Sel.Red, "lumberjack: hull and back plates", Scope.Others),
        new("seas_robots", 0.200f, 0.380f, 0.335f, 0.600f, Sel.Red, "mobile suit: arm plates", Scope.Others),
        new("seas_robots", 0.000f, 0.690f, 0.170f, 0.980f, Sel.Red, "mobile suit: leg armour", Scope.Others),

        // ash_triceratops_a: the SEAS triceratops (transporter, MG, resource collector) - its harness and armour are
        // baked into the animal's texture and it has no party-colour (attr bit 5) parts: the brass armour plates, not
        // the skin, horns or face (Kacper 2026-11-11 "triceratops doesn't seem to have changed its color")
        new("ash_triceratops_a", 0.000f, 0.000f, 1.000f, 0.800f, Sel.Gold, "triceratops: brass armour plates and trims", Scope.Any, "seas_"),
        new("ash_triceratops_a", 0.100f, 0.800f, 1.000f, 1.000f, Sel.Gold, "triceratops: brass plates (not the leg armour, u 0-0.1)", Scope.Any, "seas_"),

        // seas_weapons: the lumberjack's minigun keeps its colours (Kacper 2026-11-11 "for lumberjack do not color the
        // minigun"; only on that model - the saber and flamer share some of these texels)
        new("seas_weapons", 0.615f, 0.240f, 0.812f, 0.712f, Sel.Keep, "lumberjack minigun: barrels and housing", Scope.Any, "seas_lumberjack_minigun"),
        new("seas_weapons", 0.565f, 0.715f, 0.630f, 0.800f, Sel.Keep, "lumberjack minigun: grip", Scope.Any, "seas_lumberjack_minigun"),
        new("seas_weapons", 0.000f, 0.390f, 0.072f, 0.480f, Sel.Keep, "lumberjack minigun: drum", Scope.Any, "seas_lumberjack_minigun"),
        new("seas_weapons", 0.000f, 0.872f, 0.040f, 0.962f, Sel.Keep, "lumberjack minigun: ammo belt", Scope.Any, "seas_lumberjack_minigun"),
        new("seas_weapons", 0.270f, 0.610f, 0.295f, 0.728f, Sel.Keep, "lumberjack minigun: rod", Scope.Any, "seas_lumberjack_minigun"),
        new("seas_weapons", 0.164f, 0.025f, 0.185f, 0.120f, Sel.Keep, "lumberjack minigun: sight", Scope.Any, "seas_lumberjack_minigun"),

        // ---- MIRAGE's SEAS (Kacper 2026-11-11 "could you add coloring to the custom seas mirage units as well?"); the
        // textures of other tribes only on the named model
        // seas_macroclemyss: the cannon turtle's own skin and shell (an animal: its SEAS parts are the gun housing and
        // rider on it) - nothing painted, not even the white key
        new("seas_macroclemyss", 0.000f, 0.000f, 1.000f, 1.000f, Sel.Keep, "cannon turtle: the animal keeps its colours"),
        // scorpion (the SEAS boss / MIRAGE's scorpion vehicle): its red armour plates
        new("ash_seasboss_a", 0.000f, 0.000f, 1.000f, 1.000f, Sel.Red, "scorpion: red armour plates", Scope.Any, "scorpion"),
        new("ash_seasboss_b", 0.000f, 0.000f, 1.000f, 1.000f, Sel.Red, "scorpion: red armour plates", Scope.Any, "scorpion"),
        new("ash_seasboss_c", 0.000f, 0.000f, 1.000f, 1.000f, Sel.Red, "scorpion, disruptor: red trims", Scope.Any, "scorpion"),
        // disruptor (scorpion_controller): the red ring of the SEAS dial
        new("ash_seasboss_d", 0.000f, 0.000f, 0.760f, 0.420f, Sel.Red, "disruptor: the red ring of the dial", Scope.Any, "scorpion_controller"),
        // tesla machine (seq_gate_machine_extended): hazard stripes and the red parts
        new("sav_taslov_gatemachine_ingame", 0.615f, 0.060f, 0.665f, 0.440f, Sel.All, "tesla machine: hazard stripes", Scope.Any, "seq_gate_machine_extended"),
        new("sav_taslov_gatemachine_ingame", 0.000f, 0.530f, 0.450f, 0.585f, Sel.Red, "tesla machine: the copper beam", Scope.Any, "seq_gate_machine_extended"),
        new("sav_taslov_gatemachine_ingame", 0.370f, 0.000f, 0.450f, 0.500f, Sel.Red, "tesla machine: the copper column", Scope.Any, "seq_gate_machine_extended"),
        // warp gate / portal terminus (hs_warpgate): the red lines of the SEAS dimension panel
        new("all_dimension_seass", 0.690f, 0.080f, 1.000f, 0.420f, Sel.Red, "warp gate: red lines of the dimension panel", Scope.Any, "hs_warpgate"),
        // speaker: its red bar and hazard stripes (ash_specloc_i is also the bunker's: only the speaker)
        new("ash_specloc_i", 0.000f, 0.000f, 0.120f, 0.550f, Sel.Red, "speaker: red bar", Scope.Any, "seas_hq_speaker"),
        new("ash_specloc_i", 0.500f, 0.100f, 0.800f, 0.160f, Sel.All, "speaker: hazard stripes", Scope.Any, "seas_hq_speaker"),
        // special box (plane_parachute_box_ingame): the red panels of the parachute
        new("nor_specloc_h", 0.500f, 0.700f, 0.950f, 0.850f, Sel.Red, "special box: a band of the red parachute panels", Scope.Any, "plane_parachute_box_ingame"),

        // seas_robots: the Babbage suits' minigun keeps its colours (Kacper 2026-11-12 "do not color minigun on babbage
        // suit"): babbage_mobile_suit (Babbage, special suit, wreckage) and MIRAGE's babbagx_mobile_suit (mobile
        // terminator) - only they carry this gun (meshes 72, 73); the other robots share these texels and keep theirs
        new("seas_robots", 0.169f, 0.941f, 0.255f, 0.996f, Sel.Keep, "Babbage suit minigun: barrels", Scope.Any, "babbag"),
        new("seas_robots", 0.271f, 0.600f, 0.294f, 0.780f, Sel.Keep, "Babbage suit minigun: barrel rods", Scope.Any, "babbag"),
        new("seas_robots", 0.278f, 0.286f, 0.337f, 0.380f, Sel.Keep, "Babbage suit minigun: housing", Scope.Any, "babbag"),
        new("seas_robots", 0.718f, 0.353f, 0.769f, 0.416f, Sel.Keep, "Babbage suit minigun: housing", Scope.Any, "babbag"),
        new("seas_robots", 0.478f, 0.008f, 0.753f, 0.039f, Sel.Keep, "Babbage suit minigun: ammo belt", Scope.Any, "babbag"),
        new("seas_robots", 0.435f, 0.282f, 0.569f, 0.416f, Sel.Keep, "Babbage suit minigun: drum", Scope.Any, "babbag"),
        new("seas_robots", 0.216f, 0.372f, 0.282f, 0.424f, Sel.Keep, "Babbage suit minigun: housing", Scope.Any, "babbag"),
        new("seas_robots", 0.004f, 0.674f, 0.067f, 0.816f, Sel.Keep, "Babbage suit minigun: grip", Scope.Any, "babbag"),
        new("seas_robots", 0.051f, 0.514f, 0.153f, 0.549f, Sel.Keep, "Babbage suit minigun: mount", Scope.Any, "babbag"),
        new("seas_robots", 0.565f, 0.745f, 0.647f, 0.902f, Sel.Keep, "Babbage suit minigun: mount", Scope.Any, "babbag"),
        new("seas_robots", 0.608f, 0.537f, 0.655f, 0.569f, Sel.Keep, "Babbage suit minigun: mount", Scope.Any, "babbag"),
        new("seas_robots", 0.533f, 0.502f, 0.675f, 0.600f, Sel.Keep, "Babbage suit minigun: mount", Scope.Any, "babbag"),
        new("seas_robots", 0.718f, 0.565f, 0.784f, 0.628f, Sel.Keep, "Babbage suit minigun: mount", Scope.Any, "babbag"),
        new("seas_robots", 0.537f, 0.463f, 0.635f, 0.506f, Sel.Keep, "Babbage suit minigun: mount", Scope.Any, "babbag"),
        new("seas_robots", 0.000f, 0.325f, 0.079f, 0.424f, Sel.Keep, "Babbage suit minigun: mount", Scope.Any, "babbag"),

        // seas_misc_b: the scaffolds of the turret tower, the headquarters, barracks, steelwork
        new("seas_misc_b", 0.000f, 0.000f, 0.505f, 0.036f, Sel.All, "a stripe along the scaffold rails"),

        // ash_specloc_d (an Ashland texture the SEAS defences use; markings only, no white key - SeasMarkings.Textures)
        new("ash_specloc_d", 0.420f, 0.106f, 0.558f, 0.130f, Sel.All, "hq bunker: hatch X (the hazard edge the hatch fan maps along)"),
        new("ash_specloc_d", 0.536f, 0.106f, 0.558f, 0.250f, Sel.All, "hq bunker: hatch X (second hazard edge)"),
        new("ash_specloc_d", 0.040f, 0.020f, 0.052f, 0.380f, Sel.All, "defence turret top: red trim line round the rim and cockpit (also on the MG nest top and the big cannon rotator)"),
        new("ash_specloc_d", 0.352f, 0.836f, 0.432f, 0.930f, Sel.All, "defence turret top: red bulb"),


        // soldiers' uniforms
        new("seas_gunner", 0.420f, 0.565f, 0.700f, 1.000f, Sel.Red, "gunner: red trousers"),
        new("seas_rocket_trooper", 0.560f, 0.050f, 0.920f, 0.530f, Sel.All, "rocket trooper: jacket"),
        new("seas_flamethrower", 0.285f, 0.635f, 0.540f, 0.895f, Sel.All, "flamethrower: padded vest"),
        new("seas_worker", 0.460f, 0.040f, 0.670f, 0.310f, Sel.Red, "warrior: red strap"),
        new("seas_rider", 0.000f, 0.270f, 0.170f, 0.550f, Sel.All, "rider: jacket"),
        new("seas_rider", 0.400f, 0.300f, 0.620f, 0.520f, Sel.All, "rider: vest"),
    };

    /// <summary>
    /// Whether a texture file (e.g. "ash_specloc_d.png") has markings for a model: besides the seas_* textures, a texture
    /// of another tribe is painted where marked, without the white key - on seas_* models, or on the model a box names
    /// (MIRAGE's SEAS use models of other tribes: scorpion, hs_warpgate ...).
    /// </summary>
    public static bool HasMarks(string? file, string? model)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(file ?? ""), mo = model ?? "";
        foreach (var m in All)
            if (string.Equals(m.Texture, name, StringComparison.OrdinalIgnoreCase)
                && mo.StartsWith(m.Model ?? "seas_", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The mask's size (px, square; the shader reads it bilinear).</summary>
    public const int MASK_SIZE = 256;
    /// <summary>The soft edge of a box (mask px).</summary>
    const float FEATHER = 1.5f;

    static readonly Dictionary<(string, bool, string), ImageTexture?> s_masks = new();

    /// <summary>
    /// The marking mask of a SEAS texture for a building or another model (red channel: 0 untouched .. 1 painted; green:
    /// 1 = kept, Sel.Keep), null when it has no markings.
    /// Built once per texture: each box's coverage (soft edge) times its selection (Sel.Red: the texel's redness, read
    /// from a half-size copy so whole stripes count, not specks).
    /// </summary>
    public static ImageTexture? Mask(Texture2D? albedo, bool building, string? model = null)
    {
        if (albedo == null) return null;
        string name = System.IO.Path.GetFileNameWithoutExtension(albedo.ResourceName ?? ""); // (ModelMaterials: the file name)
        var marks = new List<Mark>();
        string only = "";   // the model-specific boxes that apply (part of the cache key; most textures have none)
        foreach (var m in All)
            if (string.Equals(m.Texture, name, StringComparison.OrdinalIgnoreCase)
                && (m.Scope == Scope.Any || (m.Scope == Scope.Buildings) == building)
                && (m.Model == null || (model ?? "").StartsWith(m.Model, StringComparison.OrdinalIgnoreCase)))
            { marks.Add(m); if (m.Model != null) only += m.Model + ";"; }
        if (s_masks.TryGetValue((name, building, only), out var t)) return t;
        t = null;
        if (marks.Count > 0)
        {
            const int N = MASK_SIZE;
            Image? sel = null;
            try
            {
                var img = albedo.GetImage();
                if (img != null && !img.IsEmpty())
                {
                    sel = (Image)img.Duplicate();
                    if (sel.IsCompressed()) sel.Decompress();
                    sel.Convert(Image.Format.Rgba8);
                    sel.Resize(N / 2, N / 2, Image.Interpolation.Bilinear);
                    sel.Resize(N, N, Image.Interpolation.Bilinear);
                }
            }
            catch (Exception) { sel = null; /* not readable: Sel.Red boxes paint nothing */ }
            var data = new byte[N * N * 2];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f, v = 0, keep = 0;
                    foreach (var m in marks)
                    {
                        float d = Math.Min(Math.Min(px - m.U0 * N, m.U1 * N - px), Math.Min(py - m.V0 * N, m.V1 * N - py));
                        float cov = Math.Clamp(d / FEATHER + 0.5f, 0, 1);
                        if (m.Sel == Sel.Keep) { keep = Math.Max(keep, cov); continue; }
                        if (cov <= v) continue;
                        if (m.Sel == Sel.Red) cov *= sel == null ? 0 : Redness(sel.GetPixel(x, y));
                        if (m.Sel == Sel.Gold) cov *= sel == null ? 0 : Brass(sel.GetPixel(x, y));
                        v = Math.Max(v, cov);
                    }
                    data[(y * N + x) * 2] = (byte)Math.Round(v * 255);
                    data[(y * N + x) * 2 + 1] = (byte)Math.Round(keep * 255);
                }
            t = ImageTexture.CreateFromImage(Image.CreateFromData(N, N, false, Image.Format.Rg8, data));
        }
        s_masks[(name, building, only)] = t;
        return t;
    }

    /// <summary>Sel.Gold: how much a texel is brass (hue about 25-55 degrees, saturated, not dark; skin, flesh and horn
    /// are less saturated or redder).</summary>
    static float Brass(Color c)
    {
        c.ToHsv(out float h, out float s, out float v);
        float hd = h * 360f;
        return Smooth(18f, 28f, hd) * (1 - Smooth(55f, 65f, hd)) * Smooth(0.42f, 0.55f, s) * Smooth(0.28f, 0.38f, v);
    }

    /// <summary>Sel.Red: how much a texel is red / orange paint (0 grey, black, brown-grey .. 1 clearly red).</summary>
    static float Redness(Color c)
    {
        float mx = Math.Max(c.R, Math.Max(c.G, c.B)), mn = Math.Min(c.R, Math.Min(c.G, c.B));
        float s = (mx - mn) / (mx + 1e-6f), red = (c.R - Math.Max(c.G, c.B)) / (mx + 1e-6f);
        return Smooth(0.18f, 0.35f, red) * Smooth(0.25f, 0.4f, s) * Smooth(0.08f, 0.16f, mx);
    }

    static float Smooth(float e0, float e1, float x) { float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }
}
