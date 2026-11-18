// SEAS paint: whole parts (mesh nodes) of a model that keep their own colours - neither the white key nor a marking
// box paints them (ModelInstance.ApplyTints skips SeasPainted on them: PartRef.SeasKeep, set by ModelLibrary).
//
// Why parts and not Sel.Keep boxes (SeasMarkings): a box keeps texels, and the SEAS robots and vehicles reuse their
// texels all over the model. The exo flamethrower's gun (mesh28) shares 45 % of the shoulder plates' texels (the round
// grey disc) and parts of the thighs; the Black Widow's legs share a sixth of the body's seas_products texels (and the
// turret seas_wehrspinne_top uses the same sheets): a box over the gun or the legs unpainted the shoulders and the body
// too. A part is exact. (The lumberjack's and the Babbage suits' minigun boxes are on texels only the gun uses.)
//
// No Godot types: linked into the tests (tests/PwRemake.Tests: SeasPaintTests).
using System;
using System.Text.RegularExpressions;

namespace PwRemake.View.Materials;

public static class SeasKeepParts
{
    /// <summary>A model (its exact name) and the mesh nodes that keep their colours (a pattern of the glTF node name).</summary>
    public readonly record struct Part(string Model, Regex Node, string Note);

    public static readonly Part[] All =
    {
        // Kacper 2026-11-12 (mission 13): "no longer paint its legs in team colours" - every mesh of the Black Widow
        // but its body (mesh3): the upper legs (mesh23, 25, 37, 38), knees (75, 76, 89, 90), knee pistons (39 - 42),
        // feet (7, 8, 9, 22) and claws (43 - 47, 60, 73, 74, 91 - 94). MIRAGE's MG walker and the wreckages use the
        // same model: the same legs.
        new("seas_wehrspinne", new Regex(@"^seas_wehrspinne_mesh(?!3_)\d+_"), "Black Widow: the legs"),
        // Kacper 2026-11-12: "do not paint the flamethrower for the exo robot" - the gun in its right hand (mesh28;
        // MIRAGE's exo flamethrower, its wreckage seas_flamer_wreckage; the base game's class draws the plain suit)
        new("seas_mobile_suit_flamethrower", new Regex(@"^seas_mobile_suit_flamethrower_mesh28_"), "Exo flamethrower: the flamethrower"),
    };

    /// <summary>Whether the mesh node <paramref name="node"/> of <paramref name="model"/> keeps its colours.</summary>
    public static bool Keeps(string? model, string? node)
    {
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(node)) return false;
        foreach (var p in All)
            if (string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase) && p.Node.IsMatch(node)) return true;
        return false;
    }
}
